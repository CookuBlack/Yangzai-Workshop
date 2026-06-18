using System.Windows;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace YangzaiWorkshop.Services;

public static class ThemeService
{
    public static string CurrentTheme { get; private set; } = "Light";

    private const string ThemeBasePath = "Resources/Themes/";

    public static void ApplyTheme(string themeName, string workRoot)
    {
        var mainWindow = Application.Current.MainWindow;
        if (mainWindow != null)
        {
            var fadeOut = new DoubleAnimation(1, 0.7, TimeSpan.FromSeconds(0.12));
            fadeOut.Completed += (s, e) =>
            {
                SwapThemeDictionaries(themeName);
                var fadeIn = new DoubleAnimation(0.7, 1, TimeSpan.FromSeconds(0.12));
                mainWindow.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            };
            mainWindow.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
        else
        {
            SwapThemeDictionaries(themeName);
        }

        CurrentTheme = themeName;
        FileService.SaveAppSetting(workRoot, "Theme", themeName);
    }

    private static void SwapThemeDictionaries(string themeName)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var oldThemes = dictionaries
            .Where(d => d.Source != null && d.Source.OriginalString.Contains("Theme"))
            .ToList();
        foreach (var old in oldThemes)
            dictionaries.Remove(old);

        var newDict = new ResourceDictionary
        {
            Source = new Uri($"{ThemeBasePath}{themeName}Theme.xaml", UriKind.Relative)
        };
        dictionaries.Add(newDict);
    }

    /// <summary>初始化主题：优先跟随系统，其次使用配置文件中的设置</summary>
    public static void InitTheme(string workRoot)
    {
        var config = FileService.LoadConfig(workRoot);

        string themeToApply;
        if (config.FollowSystemTheme)
        {
            themeToApply = IsSystemLightTheme() ? "Light" : "Dark";
        }
        else
        {
            themeToApply = config.Theme;
        }

        SwapThemeDictionaries(themeToApply);
        CurrentTheme = themeToApply;
    }

    /// <summary>读取 Windows 注册表判断当前系统是否为亮色主题</summary>
    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int intVal)
                return intVal != 0;
        }
        catch { }
        return true; // 默认亮色
    }
}
