using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Views;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop;

public partial class MainWindow : Window
{
    private bool _isNavigating;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            source?.AddHook(WndProc);
        };

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
                NavigationService.Instance.ClearPage("Audio");
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
            else if (path.Contains("/Audio/"))
            {
                NavigationService.Instance.ClearPage("Audio");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (current == "Audio" && NavigationService.Instance.CurrentPage is Views.AudioPage ap)
                        ap.RefreshContent();
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
        WindowState = WindowState.Minimized;
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

    // ===== AI 网站书签 =====
    private void AiBookmarkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AiBookmarkPopup.IsOpen) { AiBookmarkPopup.IsOpen = false; return; }
        var config = FileService.LoadConfig(App.WorkRoot);
        BookmarkList.ItemsSource = config.AiBookmarks;
        NewBookmarkName.Text = "";
        NewBookmarkUrl.Text = "";
        NamePlaceholder.Visibility = Visibility.Visible;
        UrlPlaceholder.Visibility = Visibility.Visible;
        AiBookmarkPopup.IsOpen = true;
    }

    private void BookmarkItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is AiBookmark bm && !string.IsNullOrWhiteSpace(bm.Url))
        {
            AiBookmarkPopup.IsOpen = false;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(bm.Url) { UseShellExecute = true });
            }
            catch { /* 浏览器打开失败静默 */ }
        }
    }

    private void DeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is AiBookmark bm)
        {
            var config = FileService.LoadConfig(App.WorkRoot);
            // 用 Name + Url 匹配而非引用比较，因为每次 LoadConfig 创建新 List
            var match = config.AiBookmarks.FirstOrDefault(x => x.Name == bm.Name && x.Url == bm.Url);
            if (match != null)
            {
                config.AiBookmarks.Remove(match);
                FileService.SaveConfig(App.WorkRoot, config);
                BookmarkList.ItemsSource = null;
                BookmarkList.ItemsSource = config.AiBookmarks;
            }
        }
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        var name = NewBookmarkName.Text.Trim();
        var url = NewBookmarkUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) return;
        // 自动补全 https://
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        var config = FileService.LoadConfig(App.WorkRoot);
        config.AiBookmarks.Add(new AiBookmark { Name = name, Url = url });
        FileService.SaveConfig(App.WorkRoot, config);
        BookmarkList.ItemsSource = null;
        BookmarkList.ItemsSource = config.AiBookmarks;
        NewBookmarkName.Text = "";
        NewBookmarkUrl.Text = "";
        NamePlaceholder.Visibility = Visibility.Visible;
        UrlPlaceholder.Visibility = Visibility.Visible;
    }

    private void NewBookmarkName_TextChanged(object sender, TextChangedEventArgs e) =>
        NamePlaceholder.Visibility = string.IsNullOrEmpty(NewBookmarkName.Text)
            ? Visibility.Visible : Visibility.Collapsed;

    private void NewBookmarkUrl_TextChanged(object sender, TextChangedEventArgs e) =>
        UrlPlaceholder.Visibility = string.IsNullOrEmpty(NewBookmarkUrl.Text)
            ? Visibility.Visible : Visibility.Collapsed;

    // ===== 书签拖拽排序 =====
    private AiBookmark? _dragItem;
    private int _dragIndex = -1;
    private Point _dragStart;
    private bool _isDragging;
    private Border? _sourceBorder;
    private Border? _targetBorder;
    private AppConfig? _dragConfig;

    private void ItemBorder_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border)
            border.MouseLeftButtonDown += ItemBorder_MouseLeftButtonDown;
    }

    private void ItemBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.DataContext is not AiBookmark item) return;

        var idx = FindBookmarkIndex(item.Name, item.Url);
        if (idx < 0) return;

        _dragItem = item;
        _dragIndex = idx;
        _dragStart = e.GetPosition(BookmarkList);
        _isDragging = false;
        _sourceBorder = null;
        _targetBorder = null;
        _dragConfig = null;
    }

    private int FindBookmarkIndex(string name, string url)
    {
        _dragConfig ??= FileService.LoadConfig(App.WorkRoot);
        return _dragConfig.AiBookmarks.FindIndex(x => x.Name == name && x.Url == url);
    }

    private void BookmarkList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CleanupDrag(); return; }

        var pos = e.GetPosition(BookmarkList);
        if (!_isDragging && Math.Abs(pos.X - _dragStart.X) + Math.Abs(pos.Y - _dragStart.Y) < 5)
            return;

        // 首次进入拖拽态：缓存配置 + 源项缩小淡出 + 光标切换
        if (!_isDragging)
        {
            _isDragging = true;
            _dragConfig = FileService.LoadConfig(App.WorkRoot);
            Mouse.OverrideCursor = Cursors.ScrollAll;
            ApplySourceDim();
        }

        var targetIdx = HitTestBookmarkIndex(pos);
        if (targetIdx >= 0 && targetIdx != _dragIndex)
        {
            // 实时重排：移动源项到目标位
            RealTimeReorder(targetIdx);
        }
    }

    private void RealTimeReorder(int newIndex)
    {
        if (_dragConfig == null) return;
        var list = _dragConfig.AiBookmarks;
        if (_dragIndex >= list.Count || newIndex >= list.Count) return;

        // 移动
        var item = list[_dragIndex];
        list.RemoveAt(_dragIndex);
        int insertAt = newIndex > _dragIndex ? newIndex - 1 : newIndex;
        list.Insert(insertAt, item);

        // 刷新列表视图 → 所有容器重建
        BookmarkList.ItemsSource = null;
        BookmarkList.ItemsSource = list;

        // 更新拖拽项新位置
        _dragIndex = insertAt;
        _sourceBorder = null;
        _targetBorder = null;

        // 布局完成后重新应用源项缩放样式
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _sourceBorder = GetBorderAt(_dragIndex);
            if (_sourceBorder != null)
            {
                _sourceBorder.RenderTransform = new ScaleTransform(0.95, 0.95);
                _sourceBorder.RenderTransformOrigin = new Point(0.5, 0.5);
                _sourceBorder.Opacity = 0.35;
                _sourceBorder.Background = (Brush)FindResource("HoverBrush");
            }
        }), DispatcherPriority.Loaded);
    }

    private void ApplySourceDim()
    {
        _sourceBorder = GetBorderAt(_dragIndex);
        if (_sourceBorder != null)
        {
            _sourceBorder.RenderTransform = new ScaleTransform(0.95, 0.95);
            _sourceBorder.RenderTransformOrigin = new Point(0.5, 0.5);
            _sourceBorder.Opacity = 0.35;
            _sourceBorder.Background = (Brush)FindResource("HoverBrush");
        }
    }

    private void BookmarkList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging && _dragConfig != null)
        {
            // 列表已实时重排完毕，直接落盘
            FileService.SaveConfig(App.WorkRoot, _dragConfig);
            BookmarkList.ItemsSource = null;
            BookmarkList.ItemsSource = _dragConfig.AiBookmarks;
        }
        CleanupDrag();
    }

    private void BookmarkList_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isDragging) { ResetBorderStyle(_targetBorder); _targetBorder = null; }
    }

    private void CleanupDrag()
    {
        Mouse.OverrideCursor = null;
        if (_sourceBorder != null)
        {
            _sourceBorder.RenderTransform = Transform.Identity;
            _sourceBorder.Opacity = 1;
            _sourceBorder.Background = Brushes.Transparent;
            _sourceBorder = null;
        }
        ResetBorderStyle(_targetBorder);
        _targetBorder = null;
        _dragItem = null;
        _dragIndex = -1;
        _isDragging = false;
        _dragConfig = null;
    }

    private void ResetBorderStyle(Border? border)
    {
        if (border != null)
        {
            border.BorderBrush = Brushes.Transparent;
            border.Background = Brushes.Transparent;
        }
    }

    private int HitTestBookmarkIndex(Point pos)
    {
        var hitResult = VisualTreeHelper.HitTest(BookmarkList, pos);
        var targetBorder = FindParent<Border>(hitResult?.VisualHit);
        if (targetBorder?.DataContext is not AiBookmark target) return -1;
        return FindBookmarkIndex(target.Name, target.Url);
    }

    private Border? GetBorderAt(int index)
    {
        if (index < 0) return null;
        var container = BookmarkList.ItemContainerGenerator.ContainerFromIndex(index);
        return FindParent<Border>(container);
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        // 切换主题前，先保存当前页面内容（防止 FlowDocument 在资源切换时损坏）
        if (NavigationService.Instance.CurrentPage is Views.ScriptPage sp)
            sp.ForceSave();

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

    /// <summary>
    /// 修复 AllowsTransparency=True 时任务栏点击最小化/还原失效问题。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_MINIMIZE  = 0xF020;
        const int SC_RESTORE   = 0xF120;

        if (msg == WM_SYSCOMMAND)
        {
            int cmd = wParam.ToInt32() & 0xFFF0;
            if (cmd == SC_MINIMIZE)
            {
                WindowState = WindowState.Minimized;
                handled = true;
            }
            else if (cmd == SC_RESTORE)
            {
                WindowState = WindowState.Normal;
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        NavigationService.Instance.PageChanged -= OnPageChanged;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        var current = child;
        while (current is not null and not T)
            current = VisualTreeHelper.GetParent(current);
        return current as T;
    }
}
