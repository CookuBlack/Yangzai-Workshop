using System.IO;
using System.Windows;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop;

public partial class App : Application
{
    /// <summary>当前工作根目录（相对 exe 的 WorkData 目录）</summary>
    public static string WorkRoot { get; private set; } = string.Empty;

    /// <summary>Assets/Avatar 目录路径</summary>
    public static string AvatarDir => FileService.AssetsAvatarPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 初始化工作目录：相对于应用程序目录的 WorkData 文件夹
        WorkRoot = FileService.DefaultWorkPath;
        FileService.InitializeWorkData(WorkRoot);

        // 确保 Assets/Avatar 目录存在
        FileService.EnsureDirectory(FileService.AssetsAvatarPath);

        // 加载主题配置
        ThemeService.InitTheme(WorkRoot);
    }
}
