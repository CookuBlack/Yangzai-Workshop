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
    private const string CurrentVersion = "2.2.1";
    public static string AppVersion => CurrentVersion;

    /// <summary>版本信息 JSON 地址（GitHub 直连优先，CDN 备用）</summary>
    private static readonly string[] VersionInfoUrls = new[]
    {
        "https://raw.githubusercontent.com/CookuBlack/Yangzai-Workshop/main/version.json",
        "https://cdn.jsdelivr.net/gh/CookuBlack/Yangzai-Workshop@main/version.json",
    };

    // 缓存：避免频繁启动时耗尽 API 速率
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private static string CacheFile =>
        Path.Combine(FileService.ConfigPath(WorkRoot), ".update_cache");

    /// <summary>最近一次更新检查失败的详细错误信息</summary>
    public static string LastUpdateError => _lastUpdateError;
    private static string _lastUpdateError = "";

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
        _backupTimer.Tick += (_, _) =>
        {
            try
            {
                var cfg = FileService.LoadConfig(WorkRoot);
                if (!cfg.AutoBackup) return;
                var elapsed = DateTime.Now - _lastBackupTime;
                if (elapsed.TotalHours >= cfg.BackupIntervalHours - 0.1)
                    DoAutoBackup();
            }
            catch { }
        };
        _backupTimer.Start();
        _lastBackupTime = DateTime.Now;
    }

    private static void DoAutoBackup()
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
                File.Delete(files[i]);
        }
        catch { }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        WorkRoot = FileService.DefaultWorkPath;
        FileService.InitializeWorkData(WorkRoot, CurrentVersion);
        FileService.EnsureDirectory(FileService.AssetsAvatarPath);
        ThemeService.InitTheme(WorkRoot);

        // 清理上次更新残留的安装包
        CleanupUpdateFiles();

        // 启动自动备份定时器
        if (!System.ComponentModel.DesignerProperties.GetIsInDesignMode(new Window()))
            RestartBackupTimer();

        try { await CheckForUpdateAsync(); }
        catch { /* 更新检测静默失败，不影响主流程 */ }
    }

    private static void CleanupUpdateFiles()
    {
        try
        {
            var dir = FileService.AppBasePath;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "YangzaiWorkshop_Update_*.msi"))
            {
                try { File.Delete(f); } catch { }
            }
            var bat = Path.Combine(dir, "_update_cleanup.bat");
            try { if (File.Exists(bat)) File.Delete(bat); } catch { }
        }
        catch { }
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

    private static async Task<UpdateCheckResult> CheckForUpdateCoreAsync(bool forceCheck)
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

        // ---- 第一步：多源获取版本信息 ----
        var errors = new System.Collections.Generic.List<string>();
        foreach (var url in VersionInfoUrls)
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

                tag = root.TryGetProperty("latest", out var vp)
                    ? vp.GetString()?.TrimStart('v', 'V') ?? "" : "";
                htmlUrl = root.TryGetProperty("release_url", out var hp)
                    ? hp.GetString() ?? "" : "";
                msiUrl = root.TryGetProperty("msi", out var mp)
                    ? mp.GetString() : null;

                if (!string.IsNullOrEmpty(tag))
                    break;
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
        var tempFile = Path.Combine(FileService.AppBasePath,
            $"YangzaiWorkshop_Update_{newTag}.msi");

        var progressWindow = new UpdateProgressWindow("正在下载更新",
            $"Yangzai Workshop v{newTag} 下载中...");

        try
        {
            TryDeleteFile(tempFile);

            // 显示进度窗口
            progressWindow.Show();

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("YangzaiWorkshop");

            using var response = await client.GetAsync(downloadUrl,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fs = File.Create(tempFile);

            var buffer = new byte[8192];
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
                    // 无法获取总大小时，简单闪烁
                    var mb = totalRead / (1024.0 * 1024.0);
                    progressWindow.Report(0, $"已下载 {mb:F1} MB...");
                }
            }

            await fs.FlushAsync();

            progressWindow.Report(100, "下载完成，正在安装...");
            progressWindow.Close();

            // 先确保 UI 关闭，再创建安装脚本
            await Current.Dispatcher.InvokeAsync(() => Current.Shutdown());

            // 等待应用完全退出
            await Task.Delay(2000);

            // 创建安装脚本：等旧进程退出 → 安装新 MSI → 启动新版本 → 清理
            var cleanupBat = Path.Combine(FileService.AppBasePath, "_update_cleanup.bat");
            var exePath = Path.Combine(FileService.AppBasePath, "YangzaiWorkshop.exe");
            File.WriteAllText(cleanupBat,
                "@echo off\r\n" +
                "echo Installing Yangzai Workshop...\r\n" +
                $"msiexec /i \"{tempFile}\" INSTALL_FOLDER=\"{FileService.AppBasePath}\" /qb!- /norestart\r\n" +
                "echo Starting Yangzai Workshop...\r\n" +
                $"start \"\" \"{exePath}\"\r\n" +
                $"del /f /q \"{tempFile}\" 2>nul\r\n" +
                $"del /f /q \"%~f0\" 2>nul\r\n",
                System.Text.Encoding.GetEncoding(936)); // GBK 编码兼容中文路径

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{cleanupBat}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            });
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempFile);
            progressWindow.Close();
            await Current.Dispatcher.InvokeAsync(() =>
            {
                MessageDialog.Show("更新失败", $"下载或安装失败：\n{ex.Message}");
            });
        }
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
        catch { }
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
        catch { }
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
