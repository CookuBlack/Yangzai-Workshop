using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

public partial class HomePage : UserControl
{
    private readonly List<string> _bannerVideos = new();
    private int _currentBannerIndex = 0;
    private DispatcherTimer? _autoPlayTimer;

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
        StopAllVideos();
    }

    // ===== 视频轮播（双 MediaElement 交叉淡入淡出） =====
    private bool _useVideo1 = true;

    private void LoadBanners()
    {
        _bannerVideos.Clear();
        StopAllVideos();

        var carouselPath = FileService.CarouselPath;
        var videos = FileService.GetFiles(carouselPath, ".mp4", ".wmv", ".avi");

        if (videos.Count == 0)
        {
            BannerVideo.Visibility = Visibility.Collapsed;
            BannerVideo2.Visibility = Visibility.Collapsed;
            BannerPlaceholder.Visibility = Visibility.Visible;
            PrevButton.Visibility = Visibility.Collapsed;
            NextButton.Visibility = Visibility.Collapsed;
            return;
        }

        BannerPlaceholder.Visibility = Visibility.Collapsed;
        BannerVideo.Visibility = Visibility.Visible;
        BannerVideo2.Visibility = Visibility.Visible;
        _bannerVideos.AddRange(videos);

        PrevButton.Visibility = _bannerVideos.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _bannerVideos.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        ShowBanner(0);
    }

    private void ShowBanner(int index)
    {
        if (_bannerVideos.Count == 0) return;
        _currentBannerIndex = (index + _bannerVideos.Count) % _bannerVideos.Count;
        var videoPath = _bannerVideos[_currentBannerIndex];

        // 选当前不用的 MediaElement 来加载
        var currentMe = _useVideo1 ? BannerVideo : BannerVideo2;
        var nextMe = _useVideo1 ? BannerVideo2 : BannerVideo;
        _useVideo1 = !_useVideo1;

        // 在新元素上准备视频
        nextMe.MediaEnded -= OnVideoEnded;
        nextMe.Source = new Uri(videoPath);
        nextMe.Position = TimeSpan.Zero;
        nextMe.Opacity = 0;
        nextMe.Play();

        // 视频打开后触发交叉淡入淡出（带缓动过渡）
        nextMe.MediaOpened += (s, e) =>
        {
            Dispatcher.Invoke(() =>
            {
                // 淡出旧视频（带缓动）
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.5))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                fadeOut.Completed += (_, _) =>
                {
                    currentMe.Stop();
                    currentMe.Source = null;
                };
                currentMe.BeginAnimation(UIElement.OpacityProperty, fadeOut);

                // 淡入新视频（带缓动）
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.5))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                nextMe.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            });
        };

        // 视频播放完毕事件
        nextMe.MediaEnded += OnVideoEnded;

        UpdateDots();
    }

    private void StopAllVideos()
    {
        BannerVideo.MediaEnded -= OnVideoEnded;
        BannerVideo2.MediaEnded -= OnVideoEnded;
        BannerVideo.Stop();
        BannerVideo2.Stop();
        BannerVideo.Source = null;
        BannerVideo2.Source = null;
        BannerVideo.Opacity = 1;
        BannerVideo2.Opacity = 0;
        _useVideo1 = true;
    }

    private void OnVideoEnded(object? sender, RoutedEventArgs e)
    {
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
