using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using YangzaiWorkshop.Models;
using Microsoft.Win32;

namespace YangzaiWorkshop.Services;

public static class ThemeService
{
    public static string CurrentTheme { get; private set; } = "Light";
    public static event Action? ThemeChanged;

    /// <summary>主题循环顺序：亮色 → 暗色 → 天空之蓝 → 自定义</summary>
    public static readonly string[] ThemeCycle = { "Light", "Dark", "Glass", "Custom" };

    private const string ThemeBasePath = "Resources/Themes/";

    public static void ApplyTheme(string themeName, string workRoot)
    {
        var mainWindow = Application.Current.MainWindow;

        if (themeName == "Custom")
        {
            // 先生成主题资源，再设置背景图（避免主题变更导致的重渲染刷掉图片）
            ApplyCustomTheme(workRoot);
        }
        else
        {
            SwapThemeDictionaries(themeName);
            if (mainWindow != null && themeName != "Glass")
            {
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0.7, TimeSpan.FromSeconds(0.12));
                fadeOut.Completed += (s, e) =>
                {
                    var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0.7, 1, TimeSpan.FromSeconds(0.12));
                    mainWindow.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                };
                mainWindow.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
        }

        CurrentTheme = themeName;

        if (mainWindow != null && themeName != "Custom")
        {
            var method = mainWindow.GetType().GetMethod("UpdateAcrylicEffect");
            method?.Invoke(mainWindow, new object[] { themeName });
        }

        FileService.SaveAppSetting(workRoot, "Theme", themeName);
        ThemeChanged?.Invoke();

        // 背景图在 ThemeChanged 后同步设置
        if (mainWindow != null)
        {
            ApplyCustomBackground(mainWindow, themeName, workRoot);
        }
    }

    /// <summary>应用自定义主题：HSL 色相空间自动生成和谐色板</summary>
    private static void ApplyCustomTheme(string workRoot)
    {
        var config = FileService.LoadConfig(workRoot);
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var oldThemes = dictionaries
            .Where(d => d.Source != null && d.Source.OriginalString.Contains("Theme"))
            .ToList();
        foreach (var old in oldThemes)
            dictionaries.Remove(old);

        Color bg;
        try { bg = (Color)ColorConverter.ConvertFromString(config.CustomBgColor); }
        catch { bg = Color.FromRgb(0xED, 0xED, 0xED); }

        // 图片/颜色完全独立 — 图片模式下根据前景偏好切换底色
        bool hasBgImage = !string.IsNullOrEmpty(config.CustomBgImagePath)
            && File.Exists(config.CustomBgImagePath);
        if (hasBgImage)
        {
            bg = config.ImageForeground == "Dark"
                ? Color.FromRgb(0x2D, 0x2D, 0x2D)   // 暗色前景
                : Color.FromRgb(0xF5, 0xF0, 0xE6);  // 奶白色前景
        }

        var (h, s, l) = RgbToHsl(bg);
        double lum = l;
        bool isLight = lum > 0.55;
        var dict = new ResourceDictionary();

        double sidebarL = isLight ? Math.Max(0.02, lum - 0.04) : Math.Min(0.98, lum + 0.06);
        double cardL    = isLight ? Math.Min(0.99, lum + 0.03) : Math.Max(0.01, lum - 0.04);
        double borderL  = isLight ? Math.Max(0, lum - 0.10) : Math.Min(1, lum + 0.12);
        double hoverL   = isLight ? Math.Max(0, lum - 0.06) : Math.Min(1, lum + 0.10);
        // 文字色用对比度算法
        var textPrimary   = BestTextColor(bg);
        var textSecondary = BestTextSecondary(bg, textPrimary);
        var textTertiary  = BestTextTertiary(bg, textPrimary);

        // 按钮色：
        //   纯色模式 → 从背景 HSL 派生
        //   图片模式 → 提取图片主色调，基于主色调派生
        Color primary, primaryLight;
        if (hasBgImage)
        {
            var domColor = ExtractDominantColor(config.CustomBgImagePath);
            var (dh, ds, dl) = RgbToHsl(domColor);
            bool domIsLight = dl > 0.55;
            double pSat = Math.Min(1, ds * 1.2 + 0.15);
            double pL   = domIsLight ? Math.Max(0.18, dl - 0.20) : Math.Min(0.82, dl + 0.20);
            double plL  = domIsLight ? Math.Min(1, pL + 0.10) : Math.Max(0, pL - 0.10);
            primary      = HslToRgb(dh, pSat, pL);
            primaryLight = HslToRgb(dh, pSat, plL);

            // 注意：文字色始终基于 UI 实际背景色(bg)计算，不随图片变化
        }
        else
        {
            double btnSat    = Math.Min(1, s * 1.3 + 0.15);
            double btnL      = isLight ? Math.Max(0.15, lum - 0.22) : Math.Min(0.85, lum + 0.25);
            double btnHoverL = isLight ? Math.Min(1, btnL + 0.08) : Math.Max(0, btnL - 0.08);
            primary      = HslToRgb(h, btnSat, btnL);
            primaryLight = HslToRgb(h, btnSat, btnHoverL);
        }

        // 背景色：
        //   纯色模式 → 完全不透明
        //   图片模式 → 半透明使底层图片可见，但卡片保持不透明保证可读
        byte bgAlpha = hasBgImage ? (byte)160 : (byte)255; // ~63% opacity
        Color win  = Color.FromArgb(bgAlpha, bg.R, bg.G, bg.B);
        Color side = HslToRgb(h, s, sidebarL);
        side = Color.FromArgb(bgAlpha, side.R, side.G, side.B);
        Color card = HslToRgb(h, s, cardL); // 卡片保持不透明，保证内容可读

        AddC(dict, "WindowBackgroundColor",  win);
        AddC(dict, "SidebarBackgroundColor", side);
        AddC(dict, "CardBackgroundColor",    card);
        AddC(dict, "TextPrimaryColor",       textPrimary);
        AddC(dict, "TextSecondaryColor",     textSecondary);
        AddC(dict, "TextTertiaryColor",      textTertiary);
        AddC(dict, "PrimaryColor",           primary);
        AddC(dict, "PrimaryLightColor",      primaryLight);
        AddC(dict, "BorderColor",            HslToRgb(h, s * 0.6, borderL));
        AddC(dict, "DangerColor",            Color.FromRgb(0xD3, 0x2F, 0x2F));
        AddC(dict, "AccentColor",            Color.FromRgb(0xFF, 0x70, 0x43));
        AddC(dict, "SuccessColor",           Color.FromRgb(0x38, 0x8E, 0x3C));
        AddC(dict, "HoverColor",             HslToRgb(h, s, hoverL));
        AddC(dict, "ShadowColor",            Color.FromArgb(0x30, (byte)(bg.R / 5), (byte)(bg.G / 5), (byte)(bg.B / 5)));

        string[] bn = { "WindowBackground","SidebarBackground","CardBackground",
            "TextPrimary","TextSecondary","TextTertiary","Primary","PrimaryLight",
            "Border","Danger","Accent","Success","Hover","Shadow" };
        foreach (var n in bn)
            dict[$"{n}Brush"] = new SolidColorBrush((Color)dict[$"{n}Color"]);

        var pc = (Color)dict["PrimaryColor"];
        dict["PrimaryLowBrush"] = new SolidColorBrush(Color.FromArgb(0x14, pc.R, pc.G, pc.B));

        dictionaries.Add(dict);
    }

    private static void AddC(ResourceDictionary dict, string key, Color c) => dict[key] = c;

    /// <summary>递归遍历逻辑树（含不可见元素），按 x:Name 查找目标元素</summary>
    private static T? FindElement<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent is T element && element.Name == name)
            return element;
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is DependencyObject depChild)
            {
                var found = FindElement<T>(depChild, name);
                if (found != null) return found;
            }
        }
        return null;
    }

    // ── HSL ↔ RGB ──
    private static (double h, double s, double l) RgbToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;
        if (Math.Abs(max - min) < 0.001) return (0, 0, l);
        double d = max - min;
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = 0;
        if (Math.Abs(max - r) < 0.001)
            h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
        else if (Math.Abs(max - g) < 0.001)
            h = ((b - r) / d + 2) / 6;
        else
            h = ((r - g) / d + 4) / 6;
        return (h, s, l);
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        double Hue2Rgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        if (s < 0.001) return Color.FromRgb((byte)(l*255), (byte)(l*255), (byte)(l*255));
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        byte r = (byte)Math.Clamp(Hue2Rgb(p, q, h + 1.0/3) * 255, 0, 255);
        byte g = (byte)Math.Clamp(Hue2Rgb(p, q, h) * 255, 0, 255);
        byte b = (byte)Math.Clamp(Hue2Rgb(p, q, h - 1.0/3) * 255, 0, 255);
        return Color.FromRgb(r, g, b);
    }

    // ── 图片主色调提取（简化箱体聚类） ──
    /// <summary>从图片文件提取主色调（采样 + 箱体聚合）</summary>
    internal static Color ExtractDominantColor(string imagePath)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // 缩小采样以提高性能
            bmp.DecodePixelWidth = Math.Min(bmp.PixelWidth, 100);
            bmp.DecodePixelHeight = Math.Min(bmp.PixelHeight, 100);
            bmp.EndInit();
            bmp.Freeze();

            if (bmp.PixelWidth == 0 || bmp.PixelHeight == 0)
                return Color.FromRgb(0x6D, 0x72, 0x78);

            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            int stride = w * 4;
            var pixels = new byte[stride * h];
            bmp.CopyPixels(pixels, stride, 0);

            // 箱体：将颜色空间分为 8×8×8 = 512 个箱子
            var boxes = new int[8, 8, 8];
            var boxSum = new long[8, 8, 3]; // R,G,B 总和

            int step = Math.Max(1, (w * h) / 2000); // 最多采 ~2000 像素
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x += step)
                {
                    int idx = y * stride + x * 4;
                    byte r = pixels[idx + 2];
                    byte g = pixels[idx + 1];
                    byte b = pixels[idx];

                    // 跳过接近透明或过亮的像素
                    if (pixels[idx + 3] < 128) continue;

                    int ri = r / 32; // 0..7
                    int gi = g / 32;
                    int bi = b / 32;

                    boxes[ri, gi, bi]++;
                    boxSum[ri, gi, 0] += r;
                    boxSum[ri, gi, 1] += g;
                    boxSum[ri, gi, 2] += b;
                }
            }

            // 找最大箱子
            int maxCount = 0;
            int bestRi = 0, bestGi = 0, bestBi = 0;
            for (int ri = 0; ri < 8; ri++)
                for (int gi = 0; gi < 8; gi++)
                    for (int bi = 0; bi < 8; bi++)
                        if (boxes[ri, gi, bi] > maxCount)
                        {
                            maxCount = boxes[ri, gi, bi];
                            bestRi = ri; bestGi = gi; bestBi = bi;
                        }

            if (maxCount == 0)
                return Color.FromRgb(0x6D, 0x72, 0x78);

            // 返回该箱子的平均色
            int c = boxes[bestRi, bestGi, bestBi];
            return Color.FromRgb(
                (byte)(boxSum[bestRi, bestGi, 0] / c),
                (byte)(boxSum[bestRi, bestGi, 1] / c),
                (byte)(boxSum[bestRi, bestGi, 2] / c));
        }
        catch
        {
            return Color.FromRgb(0x6D, 0x72, 0x78); // 默认中性灰
        }
    }

    // ── WCAG 对比度文字色 ──
    private static readonly Color DarkText  = Color.FromRgb(0x19, 0x1B, 0x1E);
    private static readonly Color LightText = Color.FromRgb(0xF0, 0xF2, 0xF5);
    private static readonly Color MidGray   = Color.FromRgb(0x8A, 0x8E, 0x93);

    /// <summary>返回与背景对比度最高的文字色</summary>
    private static Color BestTextColor(Color bg)
    {
        double cDark  = ContrastRatio(DarkText,  bg);
        double cLight = ContrastRatio(LightText, bg);
        return cDark >= cLight ? DarkText : LightText;
    }

    /// <summary>次要文字：透明化主色 + 确保可读</summary>
    private static Color BestTextSecondary(Color bg, Color primary)
    {
        // 与背景保持 ≥3:1 对比度，透明度折中
        bool useDark = primary.R < 128;
        int step = useDark ? 40 : -40;
        for (int i = 0; i < 6; i++)
        {
            var c = Color.FromRgb(
                (byte)Math.Clamp(primary.R + step * i, 0, 255),
                (byte)Math.Clamp(primary.G + step * i, 0, 255),
                (byte)Math.Clamp(primary.B + step * i, 0, 255));
            if (ContrastRatio(c, bg) >= 3.0) return c;
        }
        return useDark ? Color.FromRgb(0x60, 0x63, 0x68) : Color.FromRgb(0xA0, 0xA4, 0xAA);
    }

    private static Color BestTextTertiary(Color bg, Color primary)
    {
        bool useDark = primary.R < 128;
        int step = useDark ? 25 : -25;
        for (int i = 0; i < 6; i++)
        {
            var c = Color.FromRgb(
                (byte)Math.Clamp(primary.R + step * i, 0, 255),
                (byte)Math.Clamp(primary.G + step * i, 0, 255),
                (byte)Math.Clamp(primary.B + step * i, 0, 255));
            if (ContrastRatio(c, bg) >= 2.0) return c;
        }
        return MidGray;
    }

    /// <summary>WCAG 2.1 对比度 (L1≥L2)</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        double L1 = Math.Max(la, lb);
        double L2 = Math.Min(la, lb);
        return (L1 + 0.05) / (L2 + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        double Linearize(byte ch)
        {
            double v = ch / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linearize(c.R) + 0.7152 * Linearize(c.G) + 0.0722 * Linearize(c.B);
    }

    /// <summary>应用/清除自定义背景图片（透明+模糊）</summary>
    internal static void ApplyCustomBackground(Window mainWindow, string themeName, string workRoot,
        bool reloadImage = true)
    {
        var bgGrid = FindElement<Grid>(mainWindow, "BgImageGrid");
        var bgImage = FindElement<System.Windows.Controls.Image>(mainWindow, "BgImage");
        if (bgGrid == null || bgImage == null) return;

        var config = FileService.LoadConfig(workRoot);

        if (themeName == "Custom" && !string.IsNullOrEmpty(config.CustomBgImagePath)
            && File.Exists(config.CustomBgImagePath))
        {
            if (reloadImage)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(config.CustomBgImagePath, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                bgImage.Source = bmp;
            }

            bgImage.Opacity = Math.Clamp(config.CustomBgOpacity, 0.05, 1);
            // 每次创建新 Effect 实例确保 WPF 重新渲染
            bgImage.Effect = config.CustomBgBlur > 0
                ? new BlurEffect { Radius = Math.Clamp(config.CustomBgBlur, 0, 50), RenderingBias = RenderingBias.Quality }
                : null;

            bgGrid.Visibility = Visibility.Visible;
        }
        else
        {
            bgGrid.Visibility = Visibility.Collapsed;
            bgImage.Source = null;
            bgImage.Effect = null;
        }
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

        // Custom 主题需要动态生成 ResourceDictionary
        if (themeToApply == "Custom")
            ApplyCustomTheme(workRoot);
        else
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
