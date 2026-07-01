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
using System.Windows.Media.Imaging;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Views;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop;

public partial class MainWindow : Window
{
    private bool _isNavigating;
    private string? _pendingNavigation;
    private bool _musicUpdating;
    private AppConfig? _configCache;
    private readonly DispatcherTimer _volumeSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string _lastTrackName = "";
    private double _lastPosition = -1;
    private bool _lastIsPlaying;
    private string _lastPlayMode = "";
    private bool _lastIsMuted;
    private double _lastVolume = -1;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            source?.AddHook(WndProc);

            // Custom 主题启动时应用背景图（窗口句柄就绪后才能设置）
            if (ThemeService.CurrentTheme == "Custom")
                ThemeService.ApplyCustomBackground(this, "Custom", App.WorkRoot);
        };

        NavigationService.Instance.PageChanged += OnPageChanged;

        // 回收站还原文件后，清除相关页面缓存强制下次访问重新加载
        FileService.FileRestored += restoredPath =>
        {
            var path = restoredPath.Replace('\\', '/');
            var current = NavigationService.Instance.CurrentPageName;

            if (path.Contains("/Novels/"))
            {
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
            _configCache = null; // 主题变化时刷新配置缓存
            var current = NavigationService.Instance.CurrentPageName;
            NavigationService.Instance.ClearCache();
            NavigationService.Instance.NavigateTo(current);
            ContentArea.Content = NavigationService.Instance.CurrentPage;
            ContentArea.Opacity = 1;
            SelectNavItem(current);
            UpdateStatusBar();
            LoadNavAvatar();
            UpdateThemeIcon();
        };

        // ===== 初始化（Config 缓存复用，避免反复 LoadConfig） =====
        _configCache = FileService.LoadConfig(App.WorkRoot);
        MusicPlayerService.Instance.Initialize(MusicMedia);
        MusicPlayerService.Instance.LoadSettings(_configCache);
        MusicPlayerService.Instance.LoadPlaylist(FileService.MusicPath(App.WorkRoot));
        MusicPlayerService.Instance.StateChanged += UpdateMusicUI;

        // 音量保存去抖动：拖拽停止 600ms 后才写入磁盘
        _volumeSaveTimer.Tick += (_, _) =>
        {
            _volumeSaveTimer.Stop();
            _configCache ??= FileService.LoadConfig(App.WorkRoot);
            _configCache.MusicVolume = MusicPlayerService.Instance.Volume;
            FileService.SaveConfig(App.WorkRoot, _configCache);
        };

        _ = NavigateToPage("Home");
        UpdateThemeIcon();
        UpdateStatusBar();
        LoadAvatars();
        StyleSliderThumbs();

        if (_configCache.MusicAutoPlay && MusicPlayerService.Instance.Playlist.Count > 0)
            MusicPlayerService.Instance.Play(0);
    }

    /// <summary>代码设置圆形 Thumb，避免 XAML 模板冲突</summary>
    private void StyleSliderThumbs()
    {
        // 进度条：10px 圆点
        MusicProgressSlider.ApplyTemplate();
        var pt = MusicProgressSlider.Template.FindName("PART_Track", MusicProgressSlider)
            as System.Windows.Controls.Primitives.Track;
        if (pt?.Thumb != null)
        {
            pt.Thumb.Style = null;
            pt.Thumb.Template = CreateCircleThumbTemplate(10);
        }

        // 音量条：8px 圆点
        MusicVolumeSlider.ApplyTemplate();
        var vt = MusicVolumeSlider.Template.FindName("PART_Track", MusicVolumeSlider)
            as System.Windows.Controls.Primitives.Track;
        if (vt?.Thumb != null)
        {
            vt.Thumb.Style = null;
            vt.Thumb.Template = CreateCircleThumbTemplate(8);
        }
    }

    private static ControlTemplate CreateCircleThumbTemplate(double d)
    {
        var t = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb));
        var b = new FrameworkElementFactory(typeof(Border));
        b.SetValue(Border.WidthProperty, d);
        b.SetValue(Border.HeightProperty, d);
        b.SetValue(Border.CornerRadiusProperty, new CornerRadius(d / 2));
        b.SetValue(Border.BackgroundProperty, Application.Current.Resources["PrimaryBrush"]);
        b.SetValue(Border.SnapsToDevicePixelsProperty, true);
        t.VisualTree = b;
        return t;
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
        _configCache ??= FileService.LoadConfig(App.WorkRoot);
        VersionText.Text = $"v{App.AppVersion}";
        UpdateDateText.Text = _configCache.LastUpdateDate;
    }

    // ===== 页面导航（带淡入淡出过渡动画） =====
    private async Task NavigateToPage(string pageName)
    {
        // 正在导航中 → 记录最新请求，等当前完成后处理
        if (_isNavigating)
        {
            if (NavigationService.Instance.CurrentPageName != pageName)
                _pendingNavigation = pageName;
            return;
        }
        if (ContentArea.Content != null
            && NavigationService.Instance.CurrentPageName == pageName)
            return;

        _isNavigating = true;
        _pendingNavigation = null;

        bool animate = ContentArea.Content != null;

        if (animate)
        {
            await AnimateOpacityTo(ContentArea, 0, 150);
        }

        // 再次检查：等待淡出期间可能被追加了新导航请求
        if (_pendingNavigation != null)
        {
            pageName = _pendingNavigation;
            _pendingNavigation = null;
        }

        // 切换内容
        NavigationService.Instance.NavigateTo(pageName);
        ContentArea.Content = NavigationService.Instance.CurrentPage;
        SelectNavItem(pageName);

        if (animate)
        {
            await AnimateOpacityTo(ContentArea, 1, 150);
        }
        else
        {
            ContentArea.Opacity = 1;
        }

        _isNavigating = false;

        // 处理在导航期间积压的最新请求
        if (_pendingNavigation != null)
        {
            var next = _pendingNavigation;
            _pendingNavigation = null;
            _ = NavigateToPage(next);
        }
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
        _configCache = FileService.LoadConfig(App.WorkRoot);
        BookmarkList.ItemsSource = _configCache.AiBookmarks;
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
            _configCache ??= FileService.LoadConfig(App.WorkRoot);
            var match = _configCache.AiBookmarks.FirstOrDefault(x => x.Name == bm.Name && x.Url == bm.Url);
            if (match != null)
            {
                _configCache.AiBookmarks.Remove(match);
                FileService.SaveConfig(App.WorkRoot, _configCache);
                BookmarkList.ItemsSource = null;
                BookmarkList.ItemsSource = _configCache.AiBookmarks;
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

        _configCache ??= FileService.LoadConfig(App.WorkRoot);
        _configCache.AiBookmarks.Add(new AiBookmark { Name = name, Url = url });
        FileService.SaveConfig(App.WorkRoot, _configCache);
        BookmarkList.ItemsSource = null;
        BookmarkList.ItemsSource = _configCache.AiBookmarks;
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
        _dragConfig ??= _configCache ?? FileService.LoadConfig(App.WorkRoot);
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
            _dragConfig = _configCache ?? FileService.LoadConfig(App.WorkRoot);
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

        var item = list[_dragIndex];
        list.RemoveAt(_dragIndex);
        int insertAt = newIndex > _dragIndex ? newIndex - 1 : newIndex;
        list.Insert(insertAt, item);

        // 直接重设（跳过 null 清除，减少一次布局计算）
        BookmarkList.ItemsSource = list;

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
        if (NavigationService.Instance.CurrentPage is Views.ScriptPage sp)
            sp.ForceSave();

        // 四向循环：Light → Dark → 天空之蓝 → Custom → Light
        var themes = ThemeService.ThemeCycle;
        var idx = Array.IndexOf(themes, ThemeService.CurrentTheme);
        var newTheme = themes[(idx + 1) % themes.Length];
        ThemeService.ApplyTheme(newTheme, App.WorkRoot);
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon() =>
        ThemeIcon.Text = ThemeService.CurrentTheme switch
        {
            "Custom" => "\uE771",  // 调色板图标
            "Glass" => "\uE7C6",   // 亚克力图标
            "Light" => "\uE706",  // 太阳
            _ => "\uE708"         // 月亮
        };

    /// <summary>天空之蓝主题无需 DWM 特效，仅确保投影正常</summary>
    public void UpdateAcrylicEffect(string themeName)
    {
        // 天空之蓝主题用纯白背景，不需要 DWM 亚克力效果
        // 保留此方法供 ThemeService 反射调用（不做额外操作）
    }

    /// <summary>滑块实时调整背景图透明度和模糊度（无需重建主题，图片缺失则自动加载）</summary>
    public void UpdateBgImageLive(double opacity, double blur)
    {
        // 确保图片已加载
        if (BgImage.Source == null)
        {
            try
            {
                _configCache ??= FileService.LoadConfig(App.WorkRoot);
                if (string.IsNullOrEmpty(_configCache.CustomBgImagePath) || !File.Exists(_configCache.CustomBgImagePath))
                    return;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(_configCache.CustomBgImagePath, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 1920; // 限制解码尺寸提升性能
                bmp.EndInit();
                bmp.Freeze();
                BgImage.Source = bmp;
            }
            catch { return; }
        }
        BgImageGrid.Visibility = Visibility.Visible;
        BgImage.Opacity = Math.Clamp(opacity, 0.05, 1);
        BgImage.Effect = blur > 0
            ? new BlurEffect { Radius = Math.Clamp(blur, 0, 50), RenderingBias = RenderingBias.Performance }
            : null;
    }

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

    // ===== 音乐播放器控件事件 =====

    private bool _isSliderDragging;

    private void UpdateMusicUI()
    {
        _musicUpdating = true;
        try
        {
            var svc = MusicPlayerService.Instance;
            if (!svc.IsActive) return;

            MusicBar.Visibility = Visibility.Visible;

            // ===== 只在值变化时更新 Text，减少 Layout 重排 =====
            var trackName = svc.CurrentTrackName ?? "";
            if (trackName != _lastTrackName)
            {
                MusicTrackName.Text = trackName;
                _lastTrackName = trackName;
            }

            double dur = svc.Duration;
            if (!_isSliderDragging && dur > 0)
            {
                MusicProgressSlider.Maximum = dur;
                MusicProgressSlider.Value = svc.Position;
                MusicTimeText.Text = $"{FormatMusicTime(svc.Position)} / {FormatMusicTime(dur)}";
                _lastPosition = svc.Position;
            }
            else if (dur <= 0)
            {
                MusicTimeText.Text = "--:-- / --:--";
            }

            bool isPlaying = svc.IsPlaying;
            if (isPlaying != _lastIsPlaying)
            {
                MusicPlayIcon.Text = isPlaying ? "\uE769" : "\uE768";
                _lastIsPlaying = isPlaying;
            }

            var playMode = svc.PlayMode;
            if (playMode != _lastPlayMode)
            {
                MusicModeIcon.Text = playMode switch
                {
                    "Shuffle" => "\uE8B1",
                    "RepeatOne" => "\uE8ED",
                    _ => "\uE8EE"
                };
                MusicModeBtn.ToolTip = playMode switch
                {
                    "Shuffle" => "随机播放",
                    "RepeatOne" => "单曲循环",
                    _ => "列表循环"
                };
                _lastPlayMode = playMode;
            }

            bool isMuted = svc.IsMuted;
            if (isMuted != _lastIsMuted)
            {
                MusicMuteIcon.Text = isMuted ? "\uE74F" : "\uE767";
                _lastIsMuted = isMuted;
            }

            double volume = svc.Volume;
            if (Math.Abs(volume - _lastVolume) > 0.01)
            {
                MusicVolumeSlider.Value = volume;
                _lastVolume = volume;
            }
        }
        finally { _musicUpdating = false; }
    }

    private static string FormatMusicTime(double seconds)
    {
        int totalSeconds = (int)Math.Floor(seconds);
        int m = totalSeconds / 60;
        int s = totalSeconds % 60;
        return $"{m}:{s:D2}";
    }

    private void MusicPlay_Click(object sender, RoutedEventArgs e)
        => MusicPlayerService.Instance.TogglePlayPause();

    private void MusicStop_Click(object sender, RoutedEventArgs e)
    {
        MusicPlayerService.Instance.Stop();
        MusicBar.Visibility = Visibility.Collapsed;
    }

    private void MusicNext_Click(object sender, RoutedEventArgs e)
        => MusicPlayerService.Instance.Next();

    private void MusicMode_Click(object sender, RoutedEventArgs e)
    {
        MusicPlayerService.Instance.TogglePlayMode();
        _configCache ??= FileService.LoadConfig(App.WorkRoot);
        _configCache.MusicPlayMode = MusicPlayerService.Instance.PlayMode;
        FileService.SaveConfig(App.WorkRoot, _configCache);
    }

    private void MusicMute_Click(object sender, RoutedEventArgs e)
        => MusicPlayerService.Instance.ToggleMute();

    private void MusicVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_musicUpdating) return;
        MusicPlayerService.Instance.SetVolume(e.NewValue);
        // 去抖动：拖拽停止 600ms 后一次性写入磁盘
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    // ===== 顺滑进度条拖拽 =====
    private void MusicProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isSliderDragging = true;
    }

    private void MusicProgressSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSliderDragging)
        {
            var svc = MusicPlayerService.Instance;
            if (svc.Duration > 0)
                svc.Position = MusicProgressSlider.Value;
        }
        _isSliderDragging = false;
    }

    private void MusicProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isSliderDragging) return;
        // 拖拽过程中实时更新时间显示
        var dur = MusicProgressSlider.Maximum;
        if (dur > 0)
            MusicTimeText.Text = $"{FormatMusicTime(e.NewValue)} / {FormatMusicTime(dur)}";
    }

    private void MusicListBtn_Click(object sender, RoutedEventArgs e)
    {
        if (MusicListPopup.IsOpen) { MusicListPopup.IsOpen = false; return; }
        RefreshMusicPlaylistUI();
        MusicListPopup.IsOpen = true;
    }

    private void RefreshMusicPlaylistUI()
    {
        int i = 0;
        var items = MusicPlayerService.Instance.Playlist
            .Select(f => new MusicEntry { Index = i++, Path = f, Name = Path.GetFileName(f) })
            .ToList();
        MusicPlaylistItems.ItemsSource = items;
    }

    private void MusicItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { DataContext: MusicEntry entry })
        {
            MusicPlayerService.Instance.Play(entry.Index);
            MusicListPopup.IsOpen = false;
        }
    }

    private void MusicDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MusicEntry entry })
        {
            MusicPlayerService.Instance.DeleteFile(entry.Path);
            RefreshMusicPlaylistUI();
            UpdateMusicUI();
        }
    }

    private void MusicAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "音频文件|*.mp3;*.wav;*.ogg;*.m4a;*.flac;*.aac;*.wma",
            Multiselect = true,
            Title = "添加音乐文件"
        };
        if (dlg.ShowDialog() == true)
        {
            MusicPlayerService.Instance.AddFiles(dlg.FileNames, FileService.MusicPath(App.WorkRoot));
            RefreshMusicPlaylistUI();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        NavigationService.Instance.PageChanged -= OnPageChanged;
        // 保存音乐设置
        var config = FileService.LoadConfig(App.WorkRoot);
        MusicPlayerService.Instance.SaveSettings(config);
        FileService.SaveConfig(App.WorkRoot, config);
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        var current = child;
        while (current is not null and not T)
            current = VisualTreeHelper.GetParent(current);
        return current as T;
    }
}

/// <summary>音乐列表条目（避免反射）</summary>
internal sealed class MusicEntry
{
    public int Index { get; init; }
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
}
