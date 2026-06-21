using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using YangzaiWorkshop.Views;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop;

public partial class MainWindow : Window
{
    private bool _isNavigating;

    public MainWindow()
    {
        InitializeComponent();

        NavigationService.Instance.PageChanged += OnPageChanged;

        // 回收站还原文件后，清除相关页面缓存强制下次访问重新加载
        FileService.FileRestored += restoredPath =>
        {
            var path = restoredPath.Replace('\\', '/');
            var current = NavigationService.Instance.CurrentPageName;

            if (path.Contains("/Novels/"))
            {
                // 还原的是整本小说
                NavigationService.Instance.ClearPage("Script");
                NavigationService.Instance.ClearPage("Character");
                NavigationService.Instance.ClearPage("Video");
            }
            else if (path.Contains("/Image/"))
            {
                if (path.Contains("/Image/人物素材/"))
                {
                    NavigationService.Instance.ClearPage("Character");
                }
                else
                {
                    NavigationService.Instance.ClearPage("Script");
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (current == "Script" && NavigationService.Instance.CurrentPage is Views.ScriptPage sp)
                            sp.RefreshContent();
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            else if (path.Contains("/Video/"))
            {
                NavigationService.Instance.ClearPage("Video");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (current == "Video" && NavigationService.Instance.CurrentPage is Views.VideoPage vp)
                        vp.RefreshContent();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        };

        ThemeService.ThemeChanged += () =>
        {
            var current = NavigationService.Instance.CurrentPageName;
            NavigationService.Instance.ClearCache();
            NavigationService.Instance.NavigateTo(current);
            ContentArea.Content = NavigationService.Instance.CurrentPage;
            ContentArea.Opacity = 1;
            SelectNavItem(current);
            UpdateStatusBar();
            LoadNavAvatar();
        };

        _ = NavigateToPage("Home");
        UpdateThemeIcon();
        UpdateStatusBar();
        LoadAvatars();
    }

    private void LoadAvatars()
    {
        var logoPath = Path.Combine(FileService.AppBasePath, "Assets", "Icon", "Yangzai.ico");
        var logoImg = FileService.LoadImage(logoPath, decodeWidth: 40);
        if (logoImg != null) LogoBrush.ImageSource = logoImg;
        LoadNavAvatar();
    }

    public void LoadNavAvatar()
    {
        var custom = FileService.CustomAvatarFile;
        var path = File.Exists(custom) ? custom : FileService.DefaultMiniAvatarFile;
        if (!File.Exists(path)) return;
        try
        {
            var data = File.ReadAllBytes(path);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            using var msAvatar = new MemoryStream(data);
            bmp.StreamSource = msAvatar;
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 48;
            bmp.EndInit();
            bmp.Freeze();

            // 在 BottomNavList 中找到 Profile 项，更新其头像
            foreach (ListBoxItem item in BottomNavList.Items)
            {
                if (item.Tag is string tag && tag == "Profile"
                    && item.Content is StackPanel sp && sp.Children.Count > 0
                    && sp.Children[0] is System.Windows.Shapes.Ellipse ellipse)
                {
                    ellipse.Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                    break;
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[头像加载] {ex.Message}"); }
    }

    private void UpdateStatusBar()
    {
        var config = FileService.LoadConfig(App.WorkRoot);
        VersionText.Text = $"v{App.AppVersion}";
        UpdateDateText.Text = config.LastUpdateDate;
    }

    // ===== 页面导航（带淡入淡出过渡动画） =====
    private async Task NavigateToPage(string pageName)
    {
        if (_isNavigating) return;
        if (ContentArea.Content != null
            && NavigationService.Instance.CurrentPageName == pageName)
            return;

        _isNavigating = true;

        bool animate = ContentArea.Content != null;

        if (animate)
        {
            // 淡出当前页面
            await AnimateOpacityTo(ContentArea, 0, 150);
        }

        // 切换内容
        NavigationService.Instance.NavigateTo(pageName);
        ContentArea.Content = NavigationService.Instance.CurrentPage;
        SelectNavItem(pageName);

        if (animate)
        {
            // 淡入新页面
            await AnimateOpacityTo(ContentArea, 1, 150);
        }
        else
        {
            ContentArea.Opacity = 1;
        }

        _isNavigating = false;
    }

    /// <summary>淡入淡出核心方法</summary>
    private static async Task AnimateOpacityTo(UIElement target, double to, double durationMs)
    {
        var tcs = new TaskCompletionSource<bool>();
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) => tcs.TrySetResult(true);
        target.BeginAnimation(UIElement.OpacityProperty, anim);
        await tcs.Task;
    }

    private void OnPageChanged()
    {
        ContentArea.Content = NavigationService.Instance.CurrentPage;
        ContentArea.Opacity = 1;
        SelectNavItem(NavigationService.Instance.CurrentPageName);
    }

    private void SelectNavItem(string pageName)
    {
        // 主导航列表
        foreach (ListBoxItem item in NavList.Items)
        {
            if (item.Tag is string tag && tag == pageName)
            {
                NavList.SelectedItem = item;
                BottomNavList.SelectedItem = null;
                return;
            }
        }
        // 底部导航（Profile / Settings）
        NavList.SelectedItem = null;
        foreach (ListBoxItem item in BottomNavList.Items)
        {
            if (item.Tag is string tag && tag == pageName)
            {
                BottomNavList.SelectedItem = item;
                return;
            }
        }
        BottomNavList.SelectedItem = null;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is ListBoxItem item && item.Tag is string tag)
            _ = NavigateToPage(tag);
    }

    private void BottomNavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BottomNavList.SelectedItem is ListBoxItem item && item.Tag is string tag)
            _ = NavigateToPage(tag);
    }

    // ===== 窗口控制 =====
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        _userMinimizing = true;
        WindowState = WindowState.Minimized;
        Dispatcher.BeginInvoke(() => _userMinimizing = false, DispatcherPriority.Background);
    }

    private bool _isMaximized;
    private double _normalLeft, _normalTop, _normalWidth, _normalHeight;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        ToggleMaximize();

    private void ToggleMaximize()
    {
        if (_isMaximized)
        {
            _isMaximized = false;
            MaxIcon.Text = "\uE922";
            MainBorder.CornerRadius = new CornerRadius(12);
            MainBorder.Effect = new DropShadowEffect
            {
                ShadowDepth = 3, BlurRadius = 25, Opacity = 0.35,
                Direction = 315,
                Color = (Color)ColorConverter.ConvertFromString("#35000000")
            };
            Left = _normalLeft;
            Top = _normalTop;
            Width = _normalWidth;
            Height = _normalHeight;
        }
        else
        {
            _normalLeft = Left;
            _normalTop = Top;
            _normalWidth = Width;
            _normalHeight = Height;
            _isMaximized = true;
            MaxIcon.Text = "\uE923";
            MainBorder.CornerRadius = new CornerRadius(0);
            MainBorder.Effect = null;

            var workArea = SystemParameters.WorkArea;
            Left = workArea.Left;
            Top = workArea.Top;
            Width = workArea.Width;
            Height = workArea.Height;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Environment.Exit(0);
    }

    private void MemoButton_Click(object sender, RoutedEventArgs e)
    {
        ToolboxPage.OpenMemoWindow(this);
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var newTheme = ThemeService.CurrentTheme == "Light" ? "Dark" : "Light";
        ThemeService.ApplyTheme(newTheme, App.WorkRoot);
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon() =>
        ThemeIcon.Text = ThemeService.CurrentTheme == "Light" ? "\uE706" : "\uE708";

    private void GitHubLink_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/CookuBlack/Yangzai-Workshop",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Debug.WriteLine($"[头像加载] {ex.Message}"); }
    }

    private bool _userMinimizing;
    private bool _suppressStateChange;

    /// <summary>
    /// 防护：拦截 AllowsTransparency bug 导致的非预期最小化。
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && !_userMinimizing && !_suppressStateChange)
        {
            _suppressStateChange = true;
            WindowState = WindowState.Normal;
            Activate();
            Dispatcher.BeginInvoke(() => _suppressStateChange = false, DispatcherPriority.Loaded);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        NavigationService.Instance.PageChanged -= OnPageChanged;
    }
}
