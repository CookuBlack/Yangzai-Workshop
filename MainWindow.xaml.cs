using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, Button> _navButtons = new();
    private bool _isTransitioning;

    public MainWindow()
    {
        InitializeComponent();

        _navButtons["Profile"] = NavProfile;
        _navButtons["Home"] = NavHome;
        _navButtons["Script"] = NavScript;
        _navButtons["Character"] = NavCharacter;
        _navButtons["Video"] = NavVideo;
        _navButtons["Stats"] = NavStats;
        _navButtons["Toolbox"] = NavToolbox;
        _navButtons["Settings"] = NavSettings;

        NavigationService.Instance.PageChanged += OnPageChanged;

        NavigateToPage("Home");
        UpdateThemeIcon();
        UpdateStatusBar();
        LoadAvatars();
    }

    private void LoadAvatars()
    {
        // 顶部标题栏 Logo 使用 Yangzai.ico
        var logoPath = Path.Combine(FileService.AppBasePath, "Assets", "Icon", "Yangzai.ico");
        var logoImg = FileService.LoadImage(logoPath, decodeWidth: 40);
        if (logoImg != null) LogoBrush.ImageSource = logoImg;
        LoadNavAvatar();
    }

    public void LoadNavAvatar()
    {
        var custom = FileService.CustomAvatarFile;
        var path = File.Exists(custom) ? custom : FileService.DefaultMiniAvatarFile;
        var img = FileService.LoadImage(path, decodeWidth: 40);
        if (img == null) return;

        // 模板内的 ImageBrush 通过 FindName 获取
        NavProfile.ApplyTemplate();
        var brush = NavProfile.Template.FindName("NavAvatarBrush", NavProfile) as ImageBrush;
        if (brush != null) brush.ImageSource = img;
    }

    private void UpdateStatusBar()
    {
        var config = FileService.LoadConfig(App.WorkRoot);
        VersionText.Text = $"v{config.Version}";
        UpdateDateText.Text = config.LastUpdateDate;
        GitHubStarsText.Text = config.GitHubStars.ToString();
    }

    // ===== 页面导航（丝滑淡入淡出） =====
    private async void NavigateToPage(string pageName)
    {
        if (_isTransitioning) return;
        _isTransitioning = true;

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        // 淡出当前页面
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.15)) { EasingFunction = ease };
        ContentArea.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        await Task.Delay(TimeSpan.FromSeconds(0.15));

        // 切换页面
        NavigationService.Instance.NavigateTo(pageName);
        ContentArea.Content = NavigationService.Instance.CurrentPage;

        // 淡入新页面
        ContentArea.Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2)) { EasingFunction = ease };
        fadeIn.BeginTime = TimeSpan.FromSeconds(0.05);
        ContentArea.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        UpdateNavHighlight(pageName);
        _isTransitioning = false;
    }

    private async void OnPageChanged()
    {
        if (_isTransitioning) return;
        _isTransitioning = true;

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.15)) { EasingFunction = ease };
        ContentArea.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        await Task.Delay(TimeSpan.FromSeconds(0.15));

        ContentArea.Content = NavigationService.Instance.CurrentPage;
        ContentArea.Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2)) { EasingFunction = ease };
        fadeIn.BeginTime = TimeSpan.FromSeconds(0.05);
        ContentArea.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        UpdateNavHighlight(NavigationService.Instance.CurrentPageName);
        _isTransitioning = false;
    }

    private void UpdateNavHighlight(string activePage)
    {
        foreach (var kvp in _navButtons)
        {
            var isActive = kvp.Key == activePage;
            if (kvp.Key == "Profile")
            {
                NavProfile.BorderBrush = isActive
                    ? (Brush)FindResource("PrimaryBrush") : Brushes.Transparent;
                NavProfile.BorderThickness = isActive
                    ? new Thickness(2) : new Thickness(0);
            }
            else if (kvp.Value.Style == FindResource("SideNavButtonStyle"))
            {
                kvp.Value.Background = isActive
                    ? (Brush)FindResource("PrimaryBrush") : Brushes.Transparent;
                kvp.Value.Foreground = isActive
                    ? Brushes.White : (Brush)FindResource("TextSecondaryBrush");
            }
        }
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            var pageName = btn.Name switch
            {
                "NavProfile" => "Profile",
                "NavHome" => "Home",
                "NavScript" => "Script",
                "NavCharacter" => "Character",
                "NavVideo" => "Video",
                "NavStats" => "Stats",
                "NavToolbox" => "Toolbox",
                "NavSettings" => "Settings",
                _ => "Home"
            };
            NavigateToPage(pageName);
        }
    }

    // ===== 窗口控制 =====
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        ToggleMaximize();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        { WindowState = WindowState.Normal; MaxIcon.Text = "\uE922"; }
        else
        { WindowState = WindowState.Maximized; MaxIcon.Text = "\uE923"; }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var newTheme = ThemeService.CurrentTheme == "Light" ? "Dark" : "Light";
        ThemeService.ApplyTheme(newTheme, App.WorkRoot);
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon() =>
        ThemeIcon.Text = ThemeService.CurrentTheme == "Light" ? "\uE706" : "\uE708";

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        AnimateStateChange();
    }

    /// <summary>
    /// 最大化/还原时平滑渐变圆角，圆角 12↔0 线性动画约 250ms，
    /// 配合 Windows 原生窗口动效，视觉上自然丝滑
    /// </summary>
    private async void AnimateStateChange()
    {
        bool toMaximized = WindowState == WindowState.Maximized;
        MaxIcon.Text = toMaximized ? "\uE923" : "\uE922";

        double fromRadius = toMaximized ? 12 : 0;
        double toRadius = toMaximized ? 0 : 12;

        const int frames = 25;
        const int interval = 10; // 25 × 10 = 250ms

        for (int i = 1; i <= frames; i++)
        {
            // Cubic ease-out: 开头快、收尾慢，过渡自然
            double t = (double)i / frames;
            double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
            double r = fromRadius + (toRadius - fromRadius) * eased;
            MainBorder.CornerRadius = new CornerRadius(r);
            await Task.Delay(interval);
        }

        // 确保最终状态精确到位
        MainBorder.CornerRadius = new CornerRadius(toRadius);
        MainBorder.Effect = toMaximized
            ? null
            : new DropShadowEffect { ShadowDepth = 0, BlurRadius = 12, Opacity = 0.3 };
    }

    private void GitHubLink_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/CookuBlack/Yangzai-Workshop",
                UseShellExecute = true
            });
        }
        catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        NavigationService.Instance.PageChanged -= OnPageChanged;
    }
}
