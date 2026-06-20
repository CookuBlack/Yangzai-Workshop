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
    private const string CurrentVersion = "2.0.0";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        WorkRoot = FileService.DefaultWorkPath;
        FileService.InitializeWorkData(WorkRoot);
        FileService.EnsureDirectory(FileService.AssetsAvatarPath);
        ThemeService.InitTheme(WorkRoot);

        try { await CheckForUpdateAsync(); }
        catch { }
    }

    private static string UpdateSkipFile =>
        Path.Combine(FileService.ConfigPath(WorkRoot), ".update_skip");

    private static async Task CheckForUpdateAsync()
    {
        string? msiUrl = null;
        string tag = "";

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("YangzaiWorkshop");
            client.Timeout = TimeSpan.FromSeconds(15);

            var json = await client.GetStringAsync(
                $"https://api.github.com/repos/{GitHubRepo}/releases/latest");

            using var doc = JsonDocument.Parse(json);
            tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "";

            if (CompareVersions(tag, CurrentVersion) <= 0) return;
            if (ShouldSkipReminder(tag)) return;

            var assets = doc.RootElement.GetProperty("assets");
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                {
                    msiUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }
        catch { return; }

        if (msiUrl == null) return;
        var downloadUrl = msiUrl;
        var newTag = tag;

        bool? shouldUpdate = null;
        await Current.Dispatcher.InvokeAsync(() =>
        {
            shouldUpdate = MessageDialog.Confirm("发现新版本",
                $"Yangzai Workshop v{newTag} 已发布，是否立即下载并安装？\n\n选择「取消」将在 7 天内不再提醒此版本。");
        });

        if (shouldUpdate != true) { SaveSkipReminder(newTag); return; }

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
}
