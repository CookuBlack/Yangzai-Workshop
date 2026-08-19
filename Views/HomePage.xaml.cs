using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

public partial class HomePage : UserControl
{
    private readonly List<string> _bannerVideos = new();
    private int _currentBannerIndex = 0;
    private DispatcherTimer? _autoPlayTimer;
    /// <summary>图片轮播定时器（图片没有 MediaEnded，需定时自动切换，与视频行为一致）</summary>
    private DispatcherTimer? _imageTimer;

    /// <summary>轮播支持的图片扩展名（区分视频与图片的显示方式）</summary>
    private static readonly string[] _imageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    public HomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private bool _loaded;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 轮播视频每次回页必须重载（MediaElement 离树后会 Stop）
        LoadBanners();
        RestartAutoPlay();

        if (_loaded) return;
        _loaded = true;
        LoadNotice();
        LoadNovelCount();
    }

    private void RestartAutoPlay()
    {
        _autoPlayTimer?.Stop();
        var config = FileService.LoadConfig(App.WorkRoot);
        if (config.AutoPlayBanner && _bannerVideos.Count > 1)
        {
            _autoPlayTimer = new DispatcherTimer
            { Interval = TimeSpan.FromSeconds(config.BannerIntervalSeconds) };
            _autoPlayTimer.Tick += (s, a) => NextBanner();
            _autoPlayTimer.Start();
        }
    }

    /// <summary>强制 Banner 视频圆角裁切（RectangleGeometry 对 MediaElement 必用）</summary>
    private void BannerGrid_Loaded(object sender, RoutedEventArgs e) => ViewHelpers.ApplyRoundedClip(BannerGrid, 12);
    private void BannerGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ViewHelpers.ApplyRoundedClip(BannerGrid, 12);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _autoPlayTimer?.Stop();
        _imageTimer?.Stop();
        StopAllVideos();
    }

    // ===== 视频轮播（双 MediaElement 交叉淡入淡出） =====
    private bool _useVideo1 = true;
    private RoutedEventHandler? _pendingMediaOpened;
    private bool _isTransitioning;

    private void LoadBanners()
    {
        _bannerVideos.Clear();
        StopAllVideos();

        var carouselPath = FileService.CarouselPath;
        var files = FileService.GetFiles(carouselPath, ".mp4", ".wmv", ".avi", ".png", ".jpg", ".jpeg", ".bmp", ".gif");

        if (files.Count == 0)
        {
            BannerVideo.Visibility = Visibility.Collapsed;
            BannerVideo2.Visibility = Visibility.Collapsed;
            BannerImage.Visibility = Visibility.Collapsed;
            BannerImage2.Visibility = Visibility.Collapsed;
            BannerPlaceholder.Visibility = Visibility.Visible;
            PrevButton.Visibility = Visibility.Collapsed;
            NextButton.Visibility = Visibility.Collapsed;
            return;
        }

        BannerPlaceholder.Visibility = Visibility.Collapsed;
        BannerVideo.Visibility = Visibility.Visible;
        BannerVideo2.Visibility = Visibility.Visible;
        BannerImage.Visibility = Visibility.Visible;
        BannerImage2.Visibility = Visibility.Visible;
        _bannerVideos.AddRange(files);

        PrevButton.Visibility = _bannerVideos.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _bannerVideos.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        ShowBanner(0);
    }

    /// <summary>按文件扩展名判断是否为图片</summary>
    private static bool IsImageFile(string path)
        => _imageExts.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>停止旧动画并重置显隐，保证切换时状态干净</summary>
    private void ResetSlotAnimations(MediaElement me, Image img)
    {
        me.BeginAnimation(UIElement.OpacityProperty, null);
        img.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private void ShowBanner(int index)
    {
        if (_bannerVideos.Count == 0) return;
        _currentBannerIndex = (index + _bannerVideos.Count) % _bannerVideos.Count;
        var mediaPath = _bannerVideos[_currentBannerIndex];
        var isImage = IsImageFile(mediaPath);

        // 选当前不用的槽位（视频 + 图片配对，共用同一轮播位置）
        var currentMe = _useVideo1 ? BannerVideo : BannerVideo2;
        var nextMe = _useVideo1 ? BannerVideo2 : BannerVideo;
        var currentImg = _useVideo1 ? BannerImage : BannerImage2;
        var nextImg = _useVideo1 ? BannerImage2 : BannerImage;
        _useVideo1 = !_useVideo1;

        // 清除上一个 MediaOpened 防止多次累积触发
        if (_pendingMediaOpened != null)
        {
            BannerVideo.MediaOpened -= _pendingMediaOpened;
            BannerVideo2.MediaOpened -= _pendingMediaOpened;
            _pendingMediaOpened = null;
        }
        nextMe.MediaEnded -= OnVideoEnded;

        // 清理目标槽位的残留动画与内容
        ResetSlotAnimations(nextMe, nextImg);
        nextMe.Stop();
        nextMe.Source = null;
        nextImg.Source = null;
        nextMe.Opacity = 0;
        nextImg.Opacity = 0;

        // 有媒体时总是隐藏占位符（避免上一次失败残留遮挡轮播）
        BannerPlaceholder.Visibility = Visibility.Collapsed;

        if (isImage)
        {
            // ===== 图片轮播：同步解码（限制尺寸）后直接交叉淡入淡出 =====
            _isTransitioning = true;
            _imageTimer?.Stop();
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(mediaPath);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 1920;
                bmp.EndInit();
                bmp.Freeze();
                nextImg.Source = bmp;
                CrossFade(nextImg, currentMe, currentImg);

                // 图片显示固定时长后自动切换（复用轮播间隔配置）
                var config = FileService.LoadConfig(App.WorkRoot);
                var seconds = Math.Max(3, config.BannerIntervalSeconds);
                _imageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                _imageTimer.Tick += (_, _) =>
                {
                    _imageTimer.Stop();
                    NextBanner();
                };
                _imageTimer.Start();
            }
            catch
            {
                // 图片加载失败：隐藏图片槽位，展示占位，并解除切换锁定
                nextImg.Source = null;
                _isTransitioning = false;
                BannerPlaceholder.Visibility = Visibility.Visible;
            }
        }
        else
        {
            // ===== 视频轮播：先挂事件再加载，避免 MediaOpened 丢失 =====
            _imageTimer?.Stop();
            _pendingMediaOpened = (s, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isTransitioning) return;
                    _isTransitioning = true;
                    CrossFade(nextMe, currentMe, currentImg);
                });
            };
            nextMe.MediaOpened += _pendingMediaOpened;
            nextMe.MediaEnded += OnVideoEnded;

            nextMe.Source = new Uri(mediaPath);
            nextMe.Position = TimeSpan.Zero;
            nextMe.Play();
        }

        UpdateDots();
    }

    /// <summary>
    /// 交叉淡入淡出：同时淡出旧的视频与图片（避免残留 Source 导致某一路径被跳过），
    /// 再淡入新元素（MediaElement 或 Image）。
    /// </summary>
    private void CrossFade(FrameworkElement newEl, MediaElement oldMe, Image oldImg)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        // 淡出旧视频
        if (oldMe.Source != null)
        {
            var fadeOut = new DoubleAnimation(oldMe.Opacity, 0, TimeSpan.FromSeconds(0.5))
            {
                EasingFunction = ease
            };
            fadeOut.Completed += (_, _) =>
            {
                oldMe.Stop();
                oldMe.Source = null;
            };
            oldMe.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        // 淡出旧图片（与视频互不干扰，都检查）
        if (oldImg.Source != null)
        {
            var fadeOut = new DoubleAnimation(oldImg.Opacity, 0, TimeSpan.FromSeconds(0.5))
            {
                EasingFunction = ease
            };
            fadeOut.Completed += (_, _) => oldImg.Source = null;
            oldImg.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        // 淡入新元素
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.5))
        {
            EasingFunction = ease
        };
        fadeIn.Completed += (_, _) => _isTransitioning = false;
        newEl.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    private void StopAllVideos()
    {
        _imageTimer?.Stop();
        BannerVideo.MediaEnded -= OnVideoEnded;
        BannerVideo2.MediaEnded -= OnVideoEnded;
        BannerVideo.Stop();
        BannerVideo2.Stop();
        BannerVideo.Source = null;
        BannerVideo2.Source = null;
        BannerImage.Source = null;
        BannerImage2.Source = null;
        BannerVideo.BeginAnimation(UIElement.OpacityProperty, null);
        BannerVideo2.BeginAnimation(UIElement.OpacityProperty, null);
        BannerImage.BeginAnimation(UIElement.OpacityProperty, null);
        BannerImage2.BeginAnimation(UIElement.OpacityProperty, null);
        BannerVideo.Opacity = 1;
        BannerVideo2.Opacity = 0;
        BannerImage.Opacity = 0;
        BannerImage2.Opacity = 0;
        _useVideo1 = true;
        _isTransitioning = false;
        if (_pendingMediaOpened != null)
        {
            BannerVideo.MediaOpened -= _pendingMediaOpened;
            BannerVideo2.MediaOpened -= _pendingMediaOpened;
            _pendingMediaOpened = null;
        }
    }

    private void OnVideoEnded(object? sender, RoutedEventArgs e)
    {
        if (_isTransitioning) return;
        Dispatcher.Invoke(() => NextBanner());
    }

    private void UpdateDots()
    {
        DotsPanel.Children.Clear();
        for (int i = 0; i < _bannerVideos.Count; i++)
        {
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(3, 0, 3, 0),
                Fill = i == _currentBannerIndex
                    ? new SolidColorBrush((Color)FindResource("PrimaryColor"))
                    : new SolidColorBrush((Color)FindResource("BorderColor"))
            };
            DotsPanel.Children.Add(dot);
        }
    }

    private void NextBanner()
    {
        if (_isTransitioning) return;
        _autoPlayTimer?.Stop();
        ShowBanner(_currentBannerIndex + 1);
        _autoPlayTimer?.Start();
    }

    private void PrevBanner_Click(object sender, RoutedEventArgs e)
    {
        _autoPlayTimer?.Stop();
        ShowBanner(_currentBannerIndex - 1);
        _autoPlayTimer?.Start();
    }

    private void NextBanner_Click(object sender, RoutedEventArgs e) => NextBanner();

    // ===== 右键菜单：管理轮播视频 =====
    private void Banner_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        var addItem = new MenuItem { Header = "添加轮播视频/图片" };
        addItem.Click += (s, a) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "媒体文件|*.mp4;*.wmv;*.avi;*.png;*.jpg;*.jpeg",
                Multiselect = true
            };
            if (dlg.ShowDialog() == true)
            {
                var destPath = FileService.CarouselPath;
                FileService.EnsureDirectory(destPath);
                foreach (var file in dlg.FileNames)
                    FileService.CopyFile(file, destPath);
                LoadBanners();
            }
        };
        menu.Items.Add(addItem);

        if (_bannerVideos.Count > 0)
        {
            var delItem = new MenuItem { Header = "删除当前" };
            delItem.Click += (s, a) =>
            {
                FileService.DeleteFile(_bannerVideos[_currentBannerIndex]);
                LoadBanners();
            };
            menu.Items.Add(delItem);
        }

        menu.IsOpen = true;
    }

    // ===== 公告 =====
    private void LoadNotice()
    {
        var notice = FileService.ReadText(FileService.NoticeFile(App.WorkRoot));
        NoticeText.Text = string.IsNullOrEmpty(notice) ? "暂无公告" : notice;
    }

    private void NoticeScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 阻止公告区域的滚轮事件冒泡到外层 ScrollViewer
        var scroller = (ScrollViewer)sender;
        scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta / 3);
        e.Handled = true;
    }

    private void LoadNovelCount()
    {
        var novels = FileService.LoadAllNovels(App.WorkRoot);
        NovelCountText.Text = $"已导入 {novels.Count} 本小说";
    }

    // ===== 目录打开 =====
    private void OpenDir(string path)
    {
        FileService.EnsureDirectory(path);
        try { System.Diagnostics.Process.Start("explorer.exe", path); }
        catch { }
    }

    // ===== 卡片悬停/按下效果 =====
    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            // 整个卡片变暗：设置 Border.Background 为半透明黑色叠加
            border.Tag = border.Background; // 保存原始背景
            var darkBrush = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            border.Background = darkBrush;
        }
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
        {
            // 恢复原始背景（CardStyle 会自动恢复）
            border.ClearValue(Border.BackgroundProperty);
        }
    }

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            if (border.RenderTransform is ScaleTransform st)
            {
                st.ScaleX = 0.96;
                st.ScaleY = 0.96;
            }
        }
    }

    private void Card_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            if (border.RenderTransform is ScaleTransform st)
            {
                st.ScaleX = 1;
                st.ScaleY = 1;
            }
        }

        // 触发对应目录的点击
        if (sender == RootDirCard) OpenDir(App.WorkRoot);
        else if (sender == ImageDirCard) OpenImageDir();
        else if (sender == VideoDirCard) OpenVideoDir();
        else if (sender == AudioDirCard) OpenAudioDir();
    }

    private void OpenImageDir()
    {
        OpenDir(FileService.ImageRoot(App.WorkRoot));
    }

    private void OpenVideoDir()
    {
        OpenDir(FileService.VideoRoot(App.WorkRoot));
    }

    private void OpenAudioDir()
    {
        OpenDir(FileService.AudioRoot(App.WorkRoot));
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        NavigationService.Instance.NavigateTo("Script");
    }

    private void GitHubLink_Click(object sender, MouseButtonEventArgs e)
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
}
