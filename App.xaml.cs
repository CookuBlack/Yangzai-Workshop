using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using YangzaiWorkshop.Services;
using YangzaiWorkshop.Views;

namespace YangzaiWorkshop;

public partial class App : Application
{
    public static string WorkRoot { get; private set; } = string.Empty;
    public static string AvatarDir => FileService.AssetsAvatarPath;

    private const string GitHubRepo = "CookuBlack/Yangzai-Workshop";
    private const string CurrentVersion = "4.5.0";
    public static string AppVersion => CurrentVersion;

    /// <summary>版本信息 JSON 地址（GitHub Raw 优先确保实时性，CDN 作为加速备用）</summary>
    private static string[] GetVersionInfoUrls()
    {
        var t = DateTime.UtcNow.Ticks;
        // GitHub Raw 直连：版本文件推送后几分钟内生效，确保更新检测实时性
        var raw = $"https://raw.githubusercontent.com/{GitHubRepo}/main/version.json?t={t}";
        // github.com 域名下的 raw 路径：国内网络有时比 raw.githubusercontent 更可达
        var githubRaw = $"https://github.com/{GitHubRepo}/raw/main/version.json?t={t}";
        // jsDelivr CDN：国内加速，但有较长边缘缓存（默认约 12h），需先 purge 才能拿到最新
        var cdn = $"https://cdn.jsdelivr.net/gh/{GitHubRepo}@main/version.json?t={t}";
        return new[] { raw, githubRaw, cdn };
    }

    /// <summary>清空 jsDelivr 对 version.json 的边缘缓存，确保国内 CDN 读取到最新版本</summary>
    private static async Task PurgeJsDelivrCacheAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var resp = await client.GetAsync(
                $"https://purge.jsdelivr.net/gh/{GitHubRepo}@main/version.json");
        }
        catch { }
    }

    // 缓存：避免频繁启动时耗尽 API 速率
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private static string CacheFile =>
        Path.Combine(FileService.ConfigPath(WorkRoot), ".update_cache");

    /// <summary>最近一次更新检查失败的详细错误信息</summary>
    public static string LastUpdateError => _lastUpdateError;
    private static string _lastUpdateError = "";
    private static string? _msiMirrorUrl = null;

    private static System.Windows.Threading.DispatcherTimer? _backupTimer;
    private static DateTime _lastBackupTime;

    /// <summary>重启自动备份定时器（设置变更时调用）</summary>
    public static void RestartBackupTimer()
    {
        _backupTimer?.Stop();
        _backupTimer = null;

        var config = FileService.LoadConfig(WorkRoot);
        if (!config.AutoBackup) return;

        _backupTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(10)
        };
        _backupTimer.Tick += async (_, _) =>
        {
            try
            {
                var cfg = FileService.LoadConfig(WorkRoot);
                if (!cfg.AutoBackup) return;
                var elapsed = DateTime.Now - _lastBackupTime;
                if (elapsed.TotalHours >= cfg.BackupIntervalHours - 0.1)
                    // 自动备份同样全量压缩，放到后台线程执行，避免阻塞 UI 线程
                    await Task.Run(DoAutoBackup);
            }
            catch (Exception ex) { Debug.WriteLine($"[备份定时器] {ex.Message}"); }
        };
        _backupTimer.Start();
        _lastBackupTime = DateTime.Now;
    }

    private static void DoAutoBackup()
    {
        try
        {
            var backupsDir = Path.Combine(WorkRoot, "Backups");
            Directory.CreateDirectory(backupsDir);
            var fileName = $"AutoBackup_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            var zipPath = Path.Combine(backupsDir, fileName);
            FileService.BackupData(WorkRoot, zipPath);
            _lastBackupTime = DateTime.Now;

            // 清理旧备份：保留最近 10 个
            try
            {
                var files = Directory.GetFiles(backupsDir, "AutoBackup_*.zip")
                    .OrderByDescending(f => f).ToArray();
                for (int i = 10; i < files.Length; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[自动备份失败] {ex.Message}");
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局未处理异常捕获（防止 async void 等导致进程静默崩溃）
        DispatcherUnhandledException += (_, args) =>
        {
            Debug.WriteLine($"[UI异常] {args.Exception}");
            try { File.AppendAllText(Path.Combine(WorkRoot, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} UI: {args.Exception}\n"); } catch { }
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Debug.WriteLine($"[后台任务异常] {args.Exception}");
            try { File.AppendAllText(Path.Combine(WorkRoot, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} BgTask: {args.Exception}\n"); } catch { }
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Debug.WriteLine($"[进程异常] {ex}");
            try { File.AppendAllText(Path.Combine(WorkRoot, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Fatal: {ex}\n"); } catch { }
        };

        WorkRoot = FileService.DefaultWorkPath;
        FileService.InitializeWorkData(WorkRoot, CurrentVersion);
        FileService.EnsureDirectory(FileService.AssetsAvatarPath);
        ThemeService.InitTheme(WorkRoot);

        // 初始化桌面宠物桥接（把宠物的音乐/AI/队列/资源回调节点接到主程序）
        PetService.Initialize();

        // 「打开软件时自动打开宠物」：用户在宠物设置中开启后，主程序启动时自动显示宠物
        try
        {
            if (DesktopPet.PetSettings.Load().AutoOpenPet)
                PetService.ShowPet();
        }
        catch (Exception ex) { Debug.WriteLine($"[自动打开宠物] {ex.Message}"); }

        // 清理上次更新残留的安装包
        CleanupUpdateFiles();

        // 启动自动备份定时器
        if (!System.ComponentModel.DesignerProperties.GetIsInDesignMode(new Window()))
            RestartBackupTimer();

        // 启动后延迟执行数据快照备份（不阻塞启动，防止 WorkData 整体损坏/误删时无恢复点）
        try
        {
            var snapshotTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(8)
            };
            snapshotTimer.Tick += async (_, _) =>
            {
                snapshotTimer.Stop();
                // 快照是全量压缩 WorkData，耗时且占 CPU，放到后台线程执行，避免阻塞 UI 线程造成卡死
                await Task.Run(CreateStartupSnapshot);
            };
            snapshotTimer.Start();
        }
        catch (Exception ex) { Debug.WriteLine($"[启动快照定时器] {ex.Message}"); }

        // 启动时静默检查（不弹窗，缓存结果即可）
        try { await CheckForUpdateCoreAsync(false, silent: true); }
        catch (Exception ex) { Debug.WriteLine($"[更新检查] {ex.Message}"); }
    }

    /// <summary>
    /// 启动快照备份：每次启动后自动将 WorkData 打包到独立安全目录
    /// （%LOCALAPPDATA%\YangzaiWorkshop\StartupBackups，与 WorkData 分开存放），
    /// 即使工作目录整体损坏或误删，也能恢复到最近一次启动时的数据。
    /// 保留最近 5 份快照。
    /// </summary>
    private static void CreateStartupSnapshot()
    {
        try
        {
            if (!Directory.Exists(WorkRoot)) return;
            var snapshotRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YangzaiWorkshop", "StartupBackups");
            Directory.CreateDirectory(snapshotRoot);
            var zipPath = Path.Combine(snapshotRoot, $"Snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            FileService.BackupData(WorkRoot, zipPath);

            // 清理旧快照：保留最近 5 份
            try
            {
                var files = Directory.GetFiles(snapshotRoot, "Snapshot_*.zip")
                    .OrderByDescending(f => f).ToArray();
                for (int i = 5; i < files.Length; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[启动快照备份失败] {ex.Message}");
            try
            {
                File.AppendAllText(Path.Combine(WorkRoot, "error.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 启动快照备份失败: {ex.Message}\n");
            }
            catch { }
        }
    }

    private static void CleanupUpdateFiles()
    {
        foreach (var dir in new[] { FileService.AppBasePath, Path.GetTempPath() })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "YangzaiWorkshop_Update_*.msi"))
                { try { File.Delete(f); } catch { } }
                var bat = Path.Combine(dir, "_update_cleanup.bat");
                try { if (File.Exists(bat)) File.Delete(bat); } catch { }
            }
            catch { }
        }
    }

    private static string UpdateSkipFile =>
        Path.Combine(FileService.ConfigPath(WorkRoot), ".update_skip");

    // ==================== 检查更新 ====================

    /// <summary>检查更新结果状态</summary>
    public enum UpdateCheckResult
    {
        NoUpdate,
        NetworkError,
        RateLimited,
        HasUpdateNoMsi,
        HasUpdate
    }

    /// <summary>
    /// 检查 GitHub 最新 Release
    /// </summary>
    /// <param name="forceCheck">强制检查：忽略 7 天跳过提醒，不保存新的跳过记录</param>
    /// <returns>检查结果状态</returns>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(bool forceCheck = false)
    {
        try
        {
            return await CheckForUpdateCoreAsync(forceCheck);
        }
        catch (Exception ex)
        {
            _lastUpdateError = $"检查异常：{ex.Message}";
            SaveCache(UpdateCheckResult.NetworkError, "");
            return UpdateCheckResult.NetworkError;
        }
    }

    private static async Task<UpdateCheckResult> CheckForUpdateCoreAsync(bool forceCheck, bool silent = false)
    {
            // 缓存：1 小时内复用「无更新/限速」结果，减少 API 调用
            // 有更新时不缓存，确保每次都弹窗提醒
            if (!forceCheck && TryLoadCache(out var cachedResult)
                && cachedResult != UpdateCheckResult.HasUpdate
                && cachedResult != UpdateCheckResult.HasUpdateNoMsi)
            {
                return cachedResult;
            }

        string? msiUrl = null;
        string tag = "";
        string htmlUrl = "";
        string? mirrorUrl = null;

        // ---- 第一步：多源获取版本信息 ----
        // 先清 jsDelivr 缓存，避免 CDN 返回过期 version.json
        await PurgeJsDelivrCacheAsync();

        var errors = new System.Collections.Generic.List<string>();
        foreach (var url in GetVersionInfoUrls())
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("YangzaiWorkshop");
                client.Timeout = TimeSpan.FromSeconds(10);

                using var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var cand = root.TryGetProperty("latest", out var vp)
                    ? vp.GetString()?.TrimStart('v', 'V') ?? "" : "";
                if (string.IsNullOrEmpty(cand)) continue;

                var candHtml = root.TryGetProperty("release_url", out var hp)
                    ? hp.GetString() ?? "" : "";
                var rawMsi = root.TryGetProperty("msi", out var mp)
                    ? mp.GetString() : null;
                var candMirror = root.TryGetProperty("msi_mirror", out var mrp)
                    ? mrp.GetString() : null;

                // 多源取最高版本：避免某个源（如 jsDelivr 缓存过期）返回旧版本，盖过已发布的新版本。
                // 只有候选版本比当前记录更新时才覆盖，否则忽略该源。
                if (tag.Length == 0 || CompareVersions(cand, tag) > 0)
                {
                    tag = cand;
                    htmlUrl = candHtml;
                    // MSI 地址：若未指定则自动拼接 GitHub Release 下载链接
                    msiUrl = !string.IsNullOrEmpty(rawMsi)
                        ? rawMsi
                        : $"https://github.com/{GitHubRepo}/releases/download/v{tag}/YangzaiWorkshop-windows-x64-v{tag}.msi";
                    mirrorUrl = !string.IsNullOrEmpty(candMirror) ? candMirror : null;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{new Uri(url).Host}: {ex.Message}");
            }
        }

        if (string.IsNullOrEmpty(tag))
        {
            _lastUpdateError = errors.Count > 0
                ? $"版本获取失败：{string.Join(" | ", errors)}"
                : "版本信息文件不存在（请将 version.json 推送至 GitHub）";
            return UpdateCheckResult.NetworkError;
        }
        _msiMirrorUrl = mirrorUrl;

        // 版本比较：不大于当前版本 → 无更新
        if (CompareVersions(tag, CurrentVersion) <= 0)
        {
            SaveCache(UpdateCheckResult.NoUpdate, tag);
            return UpdateCheckResult.NoUpdate;
        }

        // 非强制检查时，检查 7 天跳过提醒
        if (!forceCheck && ShouldSkipReminder(tag))
        {
            SaveCache(UpdateCheckResult.NoUpdate, tag);
            return UpdateCheckResult.NoUpdate;
        }

        // 保存缓存
        var result = msiUrl != null ? UpdateCheckResult.HasUpdate : UpdateCheckResult.HasUpdateNoMsi;
        SaveCache(result, tag);

        // ---- 第二步：处理结果 ----
        // 静默模式下仅缓存结果，不弹出对话框
        if (silent)
            return result;

        if (msiUrl == null)
        {
            // 无 MSI 文件 → 引导去 GitHub
            if (!string.IsNullOrEmpty(htmlUrl))
            {
                var openBrowser = await Current.Dispatcher.InvokeAsync(() =>
                    MessageDialog.Confirm("发现新版本",
                        $"Yangzai Workshop v{tag} 已发布！\n\n未找到 MSI 安装包，是否前往 GitHub 下载？"));

                if (openBrowser)
                {
                    try { Process.Start(new ProcessStartInfo(htmlUrl) { UseShellExecute = true }); }
                    catch { }
                }
                if (!forceCheck) SaveSkipReminder(tag);
            }
            return UpdateCheckResult.HasUpdateNoMsi;
        }

        // 有 MSI → 询问是否下载安装（勾选框控制 7 天跳过）
        bool skipRemind = false;
        var shouldUpdate = await Current.Dispatcher.InvokeAsync(() =>
            MessageDialog.ConfirmWithCheck("发现新版本",
                $"Yangzai Workshop v{tag} 已发布，是否立即下载并安装？",
                "7 天内不再提醒此版本",
                out skipRemind));

        if (!shouldUpdate)
        {
            if (!forceCheck && skipRemind) SaveSkipReminder(tag);
            return UpdateCheckResult.HasUpdate;
        }

        // 执行下载安装
        await DownloadAndInstallAsync(msiUrl, tag);
        return UpdateCheckResult.HasUpdate;
    }

    // ==================== 下载安装 ====================

    private static async Task DownloadAndInstallAsync(string downloadUrl, string newTag)
    {
        // MSI 必须放在 AppBasePath 之外！因为 MajorUpgrade 会先卸载旧版 → 清空安装目录 → MSI 被删 → 安装失败
        var tempFile = Path.Combine(Path.GetTempPath(),
            $"YangzaiWorkshop_Update_{newTag}.msi");

        // 多源下载：镜像优先 → GitHub 直连兜底
        var urls = new List<string>();
        if (!string.IsNullOrEmpty(_msiMirrorUrl)) urls.Add(_msiMirrorUrl);
        urls.Add(downloadUrl);

        Exception? lastEx = null;
        foreach (var url in urls)
        {
            var isMirror = url != downloadUrl;
            try
            {
                var source = isMirror ? "镜像源" : "GitHub";
                var progressWindow = new UpdateProgressWindow("正在下载更新",
                    $"Yangzai Workshop v{newTag} 下载中... ({source})");
                TryDeleteFile(tempFile);
                progressWindow.Show();

                using var handler = new HttpClientHandler();
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("YangzaiWorkshop");

                using var response = await client.GetAsync(url,
                    HttpCompletionOption.ResponseHeadersRead);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    progressWindow.Close();
                    if (!isMirror)
                    {
                        var openBrowser = await Current.Dispatcher.InvokeAsync(() =>
                            MessageDialog.Confirm("MSI 未上传",
                                $"v{newTag} 的安装包尚未上传。\n\n是否前往 GitHub Releases 手动下载？"));
                        if (openBrowser)
                        {
                            var releaseUrl = $"https://github.com/{GitHubRepo}/releases/tag/v{newTag}";
                            try { Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true }); } catch { }
                        }
                    }
                    lastEx = new HttpRequestException($"{source} 返回 404");
                    continue;
                }
                response.EnsureSuccessStatusCode();

                await DownloadWithProgress(response, tempFile, progressWindow);

                progressWindow.Report(100, "下载完成，正在安装...");
                progressWindow.Close();
                break; // 下载成功，退出循环
            }
            catch (Exception ex)
            {
                lastEx = ex;
                // 镜像失败则尝试下一个源
                continue;
            }
        }

        if (lastEx != null && !File.Exists(tempFile))
        {
            throw lastEx;
        }

        await InstallUpdate(tempFile, newTag);
    }

    /// <summary>大缓冲区流式下载 + 进度回调</summary>
    private static async Task DownloadWithProgress(
        HttpResponseMessage response, string tempFile, UpdateProgressWindow progressWindow)
    {
        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        using var stream = await response.Content.ReadAsStreamAsync();
        using var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 65536, useAsync: true);

        var buffer = new byte[65536];
        long totalRead = 0;
        int bytesRead;
        int lastPercent = -1;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fs.WriteAsync(buffer, 0, bytesRead);
            totalRead += bytesRead;

            if (totalBytes > 0)
            {
                var percent = (int)(totalRead * 100 / totalBytes);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    var mb = totalRead / (1024.0 * 1024.0);
                    var totalMb = totalBytes / (1024.0 * 1024.0);
                    progressWindow.Report(percent,
                        $"已下载 {mb:F1} MB / {totalMb:F1} MB");
                }
            }
            else
            {
                var mb = totalRead / (1024.0 * 1024.0);
                progressWindow.Report(0, $"已下载 {mb:F1} MB...");
            }
        }

        await fs.FlushAsync();
    }

    /// <summary>执行安装：启动 MSI → 关闭应用（让安装程序接管）</summary>
    private static async Task InstallUpdate(string tempFile, string newTag)
    {
        // 方案：先启动 msiexec 安装（Windows 会弹出 UAC），再关闭当前应用
        // 这样安装进程独立于应用进程，不会随应用退出而中断

        // 清理脚本、MSI 与安装日志都放系统 Temp（不在 AppBasePath），
        // 避免 MajorUpgrade 卸载旧版时把安装包/脚本一并删掉
        var cleanupBat = Path.Combine(Path.GetTempPath(), "_update_cleanup.bat");
        var installLog = Path.Combine(Path.GetTempPath(), $"YangzaiWorkshop_Update_{newTag}.log");
        var installPath = FileService.AppBasePath.TrimEnd('\\', '/');
        var exePath = Path.Combine(FileService.AppBasePath, "YangzaiWorkshop.exe");
        var currentPid = Environment.ProcessId;

        // 关键修复点：
        // 1) 目录属性名必须是 INSTALLFOLDER（与 Product.wxs 的 Directory Id 一致）。
        //    之前误写成 INSTALL_FOLDER，安装路径传不进去 → 装到默认位置，
        //    而安装后启动的仍是旧位置 exe，表现为“更新后还是老版本”。
        // 2) msiexec 结束后需额外轮询等待其真正退出：UAC 提权下非提权的
        //    msiexec 包装进程会立即返回，若不等待，可能在新版尚未写盘时
        //    就启动旧 exe。等待时按命令行过滤只匹配本次更新的 msiexec，
        //    避免与其它软件的安装进程混淆导致误等或卡住。
        // 3) 追加 /l*v 安装日志，安装失败时可通过日志快速定位原因。
        File.WriteAllText(cleanupBat,
            "@echo off\r\n" +
            "echo Waiting for old process to exit...\r\n" +
            $":waitloop\r\n" +
            $"tasklist /fi \"PID eq {currentPid}\" 2>nul | find \"{currentPid}\" >nul\r\n" +
            "if not errorlevel 1 (\r\n" +
            "    timeout /t 1 /nobreak >nul\r\n" +
            "    goto waitloop\r\n" +
            ")\r\n" +
            $"echo Installing Yangzai Workshop v{newTag} to {installPath}...\r\n" +
            $"msiexec /i \"{tempFile}\" INSTALLFOLDER=\"{installPath}\" /qb!- /norestart /l*v \"{installLog}\"\r\n" +
            "if errorlevel 1 goto :fail\r\n" +
            "echo Waiting for installer to finish...\r\n" +
            ":waitmsi\r\n" +
            "timeout /t 1 /nobreak >nul\r\n" +
            "powershell -NoProfile -Command \"if (Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'msiexec.exe' -and $_.CommandLine -like '*YangzaiWorkshop_Update_*' }) { exit 0 } else { exit 1 }\" >nul 2>nul\r\n" +
            "if not errorlevel 1 goto waitmsi\r\n" +
            "echo Verifying installation...\r\n" +
            $"if not exist \"{exePath}\" goto :fail\r\n" +
            "echo Starting Yangzai Workshop...\r\n" +
            $"start \"\" \"{exePath}\"\r\n" +
            ":cleanup\r\n" +
            $"del /f /q \"{tempFile}\" 2>nul\r\n" +
            $"del /f /q \"{installLog}\" 2>nul\r\n" +
            $"del /f /q \"%~f0\" 2>nul\r\n" +
            "exit /b 0\r\n" +
            ":fail\r\n" +
            $"echo Installation failed! Log: {installLog}\r\n" +
            "echo The log file is kept for troubleshooting.\r\n" +
            "pause\r\n" +
            "goto :cleanup\r\n",
            GetGbkEncoding());

        // 以独立窗口启动批处理（不受父进程退出影响）
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c start \"\" /min cmd.exe /c \"{cleanupBat}\"",
            UseShellExecute = true,
            CreateNoWindow = true
        });

        // 等待批处理进程启动
        await Task.Delay(500);

        // 关闭当前应用
        await Current.Dispatcher.InvokeAsync(() => Current.Shutdown());
    }

    // ==================== 缓存 ====================

    private static bool TryLoadCache(out UpdateCheckResult result)
    {
        result = UpdateCheckResult.NoUpdate;
        try
        {
            if (!File.Exists(CacheFile)) return false;
            var data = JsonSerializer.Deserialize<UpdateCacheData>(
                File.ReadAllText(CacheFile));
            if (data == null) return false;
            if (DateTime.Now - data.CachedAt > CacheDuration) return false;

            result = data.Result;
            return true;
        }
        catch { return false; }
    }

    /// <summary>获取 GBK 编码（.NET 8 需先注册 CodePages 提供程序，幂等安全）</summary>
    private static System.Text.Encoding GetGbkEncoding()
    {
        try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
        catch (InvalidOperationException) { /* 已注册，忽略 */ }
        return System.Text.Encoding.GetEncoding(936);
    }

    private static void SaveCache(UpdateCheckResult result, string tag)
    {
        try
        {
            FileService.EnsureDirectory(FileService.ConfigPath(WorkRoot));
            var data = new UpdateCacheData
            {
                Result = result,
                LatestTag = tag,
                CachedAt = DateTime.Now
            };
            File.WriteAllText(CacheFile,
                JsonSerializer.Serialize(data));
        }
        catch (Exception ex) { Debug.WriteLine($"[缓存保存失败] {ex.Message}"); }
    }

    // ==================== 跳过提醒 ====================

    private static bool ShouldSkipReminder(string tag)
    {
        try
        {
            if (!File.Exists(UpdateSkipFile)) return false;
            var data = JsonSerializer.Deserialize<UpdateSkipData>(
                File.ReadAllText(UpdateSkipFile));
            if (data == null) return false;
            if (data.SkipTag != tag) return false;
            return (DateTime.Now - data.LastReminded).TotalDays < 7;
        }
        catch { return false; }
    }

    private static void SaveSkipReminder(string tag)
    {
        try
        {
            FileService.EnsureDirectory(FileService.ConfigPath(WorkRoot));
            var data = new UpdateSkipData
            {
                SkipTag = tag,
                CurrentVersion = CurrentVersion,
                LastReminded = DateTime.Now
            };
            File.WriteAllText(UpdateSkipFile,
                JsonSerializer.Serialize(data));
        }
        catch (Exception ex) { Debug.WriteLine($"[跳过提醒保存失败] {ex.Message}"); }
    }

    // ==================== 工具方法 ====================

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int na = i < pa.Length && int.TryParse(pa[i], out var va) ? va : 0;
            int nb = i < pb.Length && int.TryParse(pb[i], out var vb) ? vb : 0;
            if (na != nb) return na.CompareTo(nb);
        }
        return 0;
    }

    private class UpdateSkipData
    {
        public string SkipTag { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public DateTime LastReminded { get; set; }
    }

    private class UpdateCacheData
    {
        public UpdateCheckResult Result { get; set; }
        public string LatestTag { get; set; } = "";
        public DateTime CachedAt { get; set; }
    }
}
