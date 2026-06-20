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
    private const string CurrentVersion = "2.1.0";
    public static string AppVersion => CurrentVersion;

    // 缓存：避免频繁启动时耗尽 API 速率
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private static string CacheFile =>
        Path.Combine(FileService.ConfigPath(WorkRoot), ".update_cache");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        WorkRoot = FileService.DefaultWorkPath;
        FileService.InitializeWorkData(WorkRoot);
        FileService.EnsureDirectory(FileService.AssetsAvatarPath);
        ThemeService.InitTheme(WorkRoot);

        try { await CheckForUpdateAsync(); }
        catch { /* 更新检测静默失败，不影响主流程 */ }
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

        // ---- 第一步：请求 GitHub API ----
        try
        {
            var config = FileService.LoadConfig(WorkRoot);
            var githubToken = config?.GitHubToken?.Trim() ?? string.Empty;

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("YangzaiWorkshop");
            if (!string.IsNullOrEmpty(githubToken))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", githubToken);
            }
            client.Timeout = TimeSpan.FromSeconds(15);

            using var response = await client.GetAsync(
                $"https://api.github.com/repos/{GitHubRepo}/releases?per_page=1");

            // 速率限制 → 静默跳过，缓存避免持续重试
            if ((int)response.StatusCode == 403)
            {
                var hitLimit = response.Headers.TryGetValues("X-RateLimit-Remaining", out var vals)
                    && int.TryParse(vals.FirstOrDefault(), out var rem) && rem == 0;
                if (hitLimit)
                {
                    SaveCache(UpdateCheckResult.RateLimited, "");
                    return UpdateCheckResult.RateLimited;
                }
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return UpdateCheckResult.NoUpdate;

            var latest = root[0];
            tag = latest.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "";
            htmlUrl = latest.GetProperty("html_url").GetString() ?? "";

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

            // 查找 MSI 资源
            var assets = latest.GetProperty("assets");
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                {
                    msiUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            // API 调用成功，保存缓存
            var result = msiUrl != null ? UpdateCheckResult.HasUpdate : UpdateCheckResult.HasUpdateNoMsi;
            SaveCache(result, tag);
        }
        catch
        {
            // API 失败时不缓存（下次启动重试）
            return UpdateCheckResult.NetworkError;
        }

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
        var tempFile = Path.Combine(Path.GetTempPath(),
            $"YangzaiWorkshop_{newTag}.msi");
        try
        {
            TryDeleteFile(tempFile);

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("YangzaiWorkshop");
            using var stream = await client.GetStreamAsync(downloadUrl);
            using var fs = File.Create(tempFile);
            await stream.CopyToAsync(fs);
            await fs.FlushAsync();

            await Current.Dispatcher.InvokeAsync(() =>
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = $"/i \"{tempFile}\" /qn /norestart",
                    UseShellExecute = true,
                    Verb = "runas"
                });
                Current.Shutdown();
            });
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempFile);
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
