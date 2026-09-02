using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

public partial class VideoPage : UserControl
{
    private List<NovelInfo> _novels = new();
    private NovelInfo? _currentNovel;
    private List<Chapter> _chapters = new();
    private Chapter? _currentChapter;
    private bool _multiSelectMode;
    private readonly HashSet<string> _selectedFiles = new();

    // ===== 视频缩略图性能：受限并行（默认 4 路）worker 池 + 热播放器复用 + 内存缓存（按文件修改时间失效）=====
    private static readonly int _thumbConcurrency = 4;
    private static readonly SemaphoreSlim _thumbWorkerSlots = new(_thumbConcurrency, _thumbConcurrency);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Stamp, BitmapSource Bmp, TimeSpan Dur)> _thumbCache
        = new(StringComparer.OrdinalIgnoreCase);
    // 待提取队列（每页实例，刷新时清空重建；由 worker 池并发消费）
    private readonly System.Collections.Concurrent.ConcurrentQueue<ThumbJob> _thumbQueue = new();

    /// <summary>一次缩略图提取任务（持有目标控件引用与文件修改时间戳）</summary>
    private sealed class ThumbJob
    {
        public string Path = "";
        public long Stamp;
        public Image Img = null!;
        public Grid Placeholder = null!;
        public Border? Badge;
    }

    // ===== 悬停自动播放预览：全页共享单个 MediaElement，避免大量播放器实例卡顿 =====
    private MediaElement? _hoverPreview;
    private readonly DispatcherTimer _hoverTimer;
    private Grid? _previewThumbArea;
    private string _previewPath = "";

    // ===== 排序状态：0=名称 1=最后修改时间 2=创建时间 3=文件大小，false=升序 true=降序 =====
    private int _videoSortKey;
    private bool _videoSortDescending;

    public VideoPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        VideoSortBox.Items.Add("按名称");
        VideoSortBox.Items.Add("按修改时间");
        VideoSortBox.Items.Add("按创建时间");
        VideoSortBox.Items.Add("按文件大小");
        VideoSortBox.SelectedIndex = 0;
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            ShowHoverPreview();
        };
    }

    /// <summary>外部触发刷新：重新加载小说列表（含封面），保持当前选中状态</summary>
    public void RefreshContent()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var curNovelId = _currentNovel?.Id;
            var curChapterIndex = _currentChapter != null
                ? _chapters.IndexOf(_currentChapter) : -1;
            RefreshNovels();
            if (curNovelId != null)
            {
                var novel = _novels.FirstOrDefault(n => n.Id == curNovelId);
                if (novel != null)
                {
                    SelectNovel(novel);
                    if (curChapterIndex >= 0 && curChapterIndex < _chapters.Count)
                        SelectChapter(_chapters[curChapterIndex]);
                }
            }
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private bool _loaded;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        RefreshNovels();
    }

    // ===== 小说选择（封面横条）=====
    private void RefreshNovels()
    {
        _novels = FileService.LoadAllNovels(App.WorkRoot);
        NovelCardPanel.Children.Clear();
        foreach (var novel in _novels)
        {
            var card = new Border
            {
                Background = (Brush)FindResource("CardBackgroundBrush"),
                CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand,
                Tag = novel, Width = 120,
                Margin = new Thickness(3), Padding = new Thickness(6, 6, 6, 6),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("BorderBrush")
            };
            var stack = new StackPanel();

            // 封面（有封面图时不设置背景，保留 PNG 透明区域，避免透出默认蓝色）
            var coverBorder = new Border
            {
                Width = 70, Height = 95,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 0, 6)
            };
            var coverPath = FileService.NovelCoverFile(App.WorkRoot, novel.Id);
            if (File.Exists(coverPath))
            {
                try
                {
                    var data = File.ReadAllBytes(coverPath);
                    var bmp = new BitmapImage(); bmp.BeginInit();
                    using var msVid = new MemoryStream(data);
                    bmp.StreamSource = msVid;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 140; bmp.EndInit();
                    coverBorder.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                }
                catch
                {
                    coverBorder.Background = ParseColor(novel.CoverColor);
                    coverBorder.Child = CoverFb(novel.Name);
                }
            }
            else
            {
                coverBorder.Background = ParseColor(novel.CoverColor);
                coverBorder.Child = CoverFb(novel.Name);
            }
            stack.Children.Add(coverBorder);

            // 书名
            stack.Children.Add(new TextBlock
            {
                Text = novel.Name, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 110
            });
            card.Child = stack;
            card.MouseLeftButtonDown += (s, _) => SelectNovel(novel);
            card.MouseEnter += (s, _) =>
            { if (s is Border b && b.Tag is NovelInfo ni && ni != _currentNovel) b.Opacity = 0.85; };
            card.MouseLeave += (s, _) =>
            { if (s is Border b && b.Tag is NovelInfo ni && ni != _currentNovel) b.Opacity = 0.65; };
            NovelCardPanel.Children.Add(card);
        }
        if (_novels.Count > 0 && _currentNovel == null) SelectNovel(_novels[0]);
    }

    private static TextBlock CoverFb(string n) => new()
    {
        Text = n.Length > 0 ? n[..System.Math.Min(2, n.Length)] : "书",
        Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.Bold,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    private static SolidColorBrush ParseColor(string h)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)); }
        catch { return new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2)); }
    }

    private void SelectNovel(NovelInfo novel)
    {
        _currentNovel = novel;
        _currentChapter = null;
        foreach (Border c in NovelCardPanel.Children)
        {
            if (c.Tag is NovelInfo ni)
            {
                bool a = ni.Id == novel.Id;
                c.Opacity = a ? 1.0 : 0.65;
                c.BorderThickness = a ? new Thickness(1.5) : new Thickness(1);
                c.BorderBrush = a ? (Brush)FindResource("PrimaryBrush") : (Brush)FindResource("BorderBrush");
            }
        }
        LoadChapterTabs();
    }

    private void NovelScroller_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // ===== 章节导航 =====
    private void LoadChapterTabs()
    {
        if (_currentNovel == null) return;
        _chapters = FileService.LoadChapters(App.WorkRoot, _currentNovel.Id);
        var displayOrder = _chapters
            .OrderBy(c => c.IsCompleted ? 1 : 0)
            .ThenBy(c => c.Index)
            .ToList();

        ChapterTabsPanel.Children.Clear();
        foreach (var ch in displayOrder)
        {
            var btn = new Button
            {
                Content = ch.DisplayName, Tag = ch,
                FontSize = 12, Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(2, 0, 2, 0),
                Style = (Style)FindResource("SecondaryButtonStyle")
            };
            btn.Click += (s, e) => SelectChapter(ch);
            ChapterTabsPanel.Children.Add(btn);
        }

        if (_chapters.Count > 0)
        {
            SelectChapter(_chapters[0]);
        }
        else
        {
            _currentChapter = null;
            VideoGrid.Children.Clear();
            VideoGrid.RowDefinitions.Clear();
            VideoGrid.ColumnDefinitions.Clear();
            VideoGrid.RowDefinitions.Add(new RowDefinition());
            VideoGrid.ColumnDefinitions.Add(new ColumnDefinition());
            VideoGrid.Children.Add(new TextBlock
            {
                Text = "暂无章节\n请先选择有章节的小说",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13, TextAlignment = TextAlignment.Center
            });
        }
    }

    private void SelectChapter(Chapter chapter)
    {
        _currentChapter = chapter;
        foreach (Button btn in ChapterTabsPanel.Children.OfType<Button>())
        {
            if (btn.Tag is Chapter ch)
            {
                btn.Background = ch == chapter
                    ? (Brush)FindResource("PrimaryBrush")
                    : Brushes.Transparent;
                btn.Foreground = ch == chapter ? Brushes.White
                    : (Brush)FindResource("TextPrimaryBrush");
            }
        }
        RefreshVideoGrid();
        // 滚动 Tab 条到可见
        var tab = ChapterTabsPanel.Children.OfType<Button>()
            .FirstOrDefault(b => b.Tag is Chapter cc && cc == chapter);
        tab?.BringIntoView();
    }

    private void ChapterTabsScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void ChapterExpandBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ChapterPopup.IsOpen) { ChapterPopup.IsOpen = false; return; }

        ChapterPopupList.Children.Clear();
        var displayOrder = _chapters
            .OrderBy(c => c.IsCompleted ? 1 : 0)
            .ThenBy(c => c.Index).ToList();

        double leftEdge = ChapterExpandBtn.TranslatePoint(new Point(0, 0), this).X;
        double rightEdge = ActualWidth - 32;
        double popupWidth = Math.Max(400, rightEdge - leftEdge + 40);
        ChapterPopupBorder.MaxWidth = popupWidth;
        ChapterPopupBorder.MinWidth = Math.Min(400, popupWidth);

        foreach (var ch in displayOrder)
        {
            bool sel = ch == _currentChapter;
            var btn = new Button
            {
                Content = ch.DisplayName, Tag = ch,
                Style = (Style)FindResource("SecondaryButtonStyle"),
                Margin = new Thickness(2), FontSize = 11,
                Padding = new Thickness(8, 5, 8, 5),
                Background = sel ? (Brush)FindResource("PrimaryBrush") : null,
                Foreground = sel ? Brushes.White : (Brush)FindResource("TextPrimaryBrush")
            };
            btn.Click += (s, _) =>
            {
                ChapterPopup.IsOpen = false;
                SelectChapter(ch);
            };
            ChapterPopupList.Children.Add(btn);
        }
        ChapterPopup.IsOpen = true;
    }

    // ===== 视频网格 =====
    private void RefreshVideoGrid()
    {
        // 页面未加载时直接返回：避免构造函数内 SelectedIndex 同步触发 SelectionChanged
        // 走到这里时 _hoverTimer 尚未初始化（导致 NullReference、界面空白）
        if (!_loaded) return;

        StopHoverPreview();
        VideoGrid.Children.Clear();
        VideoGrid.ColumnDefinitions.Clear();
        VideoGrid.RowDefinitions.Clear();
        // 丢弃上一轮尚未消费的缩略图任务，避免旧任务的控件引用占用 worker 槽位
        _thumbQueue.Clear();

        if (_currentNovel == null || _currentChapter == null) return;

        var path = FileService.ChapterVideosPath(
            App.WorkRoot, _currentNovel.MediaFolder, _currentChapter.FolderName);
        var videos = SortFiles(
            FileService.GetFiles(path, ".mp4", ".mkv", ".avi", ".mov", ".wmv"),
            _videoSortKey, _videoSortDescending).ToList();

        if (videos.Count == 0)
        {
            VideoGrid.RowDefinitions.Add(new RowDefinition());
            VideoGrid.ColumnDefinitions.Add(new ColumnDefinition());
            VideoGrid.Children.Add(new TextBlock
            {
                Text = "暂无视频素材\n拖拽视频文件或点击下方按钮导入",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13, TextAlignment = TextAlignment.Center
            });
            return;
        }

        int cols = 4;
        for (int i = 0; i < cols; i++)
            VideoGrid.ColumnDefinitions.Add(new ColumnDefinition());

        for (int i = 0; i < videos.Count; i++)
        {
            VideoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = CreateVideoCard(videos[i]);
            Grid.SetRow(card, i / cols);
            Grid.SetColumn(card, i % cols);
            VideoGrid.Children.Add(card);
        }

        // 全部卡片建好后启动受限并行 worker 池，消费队列中的缩略图任务
        EnsureThumbnailWorkersRunning();
    }

    /// <summary>按排序键排序文件列表：0=名称 1=最后修改时间 2=创建时间 3=文件大小。</summary>
    private static IEnumerable<string> SortFiles(IEnumerable<string> files, int key, bool descending)
    {
        var list = files.Select(f => new FileInfo(f)).ToList();
        IEnumerable<FileInfo> sorted = key switch
        {
            1 => list.OrderBy(fi => fi.LastWriteTime),
            2 => list.OrderBy(fi => fi.CreationTime),
            3 => list.OrderBy(fi => fi.Length),
            _ => list.OrderBy(fi => fi.Name, StringComparer.Ordinal)
        };
        if (descending) sorted = sorted.Reverse();
        return sorted.Select(fi => fi.FullName);
    }

    private void VideoSortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideoSortBox.SelectedIndex < 0) return;
        _videoSortKey = VideoSortBox.SelectedIndex;
        RefreshVideoGrid();
    }

    private void VideoSortDir_Click(object sender, RoutedEventArgs e)
    {
        _videoSortDescending = !_videoSortDescending;
        VideoSortDirBtn.Content = _videoSortDescending ? "↓ 降序" : "↑ 升序";
        RefreshVideoGrid();
    }

    private Border CreateVideoCard(string videoPath)
    {
        string vp = videoPath;
        string name = Path.GetFileName(vp);

        // B站风格圆角卡片：圆角封面 + 时长角标 + 居中播放按钮 + 底部信息
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(6),
            Cursor = Cursors.Hand, Tag = vp, ClipToBounds = true,
            MaxWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1)
        };

        var stack = new StackPanel();

        // ===== 封面区（圆角缩略图） =====
        var cover = new Border
        {
            Height = 130, Background = Brushes.Black,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Margin = new Thickness(6, 6, 6, 0)
        };
        cover.Loaded += (s, e) => ViewHelpers.ApplyRoundedClip(cover, 8);
        cover.SizeChanged += (s, e) => ViewHelpers.ApplyRoundedClip(cover, 8);
        var coverArea = new Grid();
        cover.Child = coverArea;

        // 占位：视频图标（缩略图异步提取后替换）
        var placeGrid = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)) };
        placeGrid.Children.Add(new TextBlock
        {
            Text = "\uE714", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 34, Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        coverArea.Children.Add(placeGrid);

        // 视频首帧缩略图
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };

        // 时长角标（右下角，半透明黑底圆角，B站风格）
        var durText = new TextBlock { Foreground = Brushes.White, FontSize = 10 };
        var durBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 6),
            Visibility = Visibility.Collapsed,
            Child = durText
        };
        coverArea.Children.Add(durBadge);

        // 异步提取真实缩略图
        BeginExtractThumbnail(vp, thumbImage, placeGrid, durBadge);

        // 居中播放按钮（悬停淡入，圆形半透明）
        var playBtn = new Border
        {
            Width = 46, Height = 46,
            CornerRadius = new CornerRadius(23),
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00)),
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        playBtn.Child = new TextBlock
        {
            Text = "\uE768", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 22, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
        Panel.SetZIndex(playBtn, 10);
        coverArea.Children.Add(playBtn);

        stack.Children.Add(cover);

        // 文件名（底部信息）
        stack.Children.Add(new TextBlock
        {
            Text = name, FontSize = 11,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 5, 8, 0),
            ToolTip = name
        });

        card.Child = stack;

        // 选中遮罩（覆盖封面）
        var selOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x50, 0x4A, 0x90, 0xE2)),
            BorderBrush = (Brush)FindResource("PrimaryBrush"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Visibility = _selectedFiles.Contains(vp) ? Visibility.Visible : Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "\uE73E", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 24, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Panel.SetZIndex(selOverlay, 20);
        coverArea.Children.Add(selOverlay);

        card.MouseEnter += (_, _) =>
        {
            playBtn.Opacity = 1;
            StartHoverPreview(coverArea, vp);
        };
        card.MouseLeave += (_, _) =>
        {
            playBtn.Opacity = 0;
            StopHoverPreview();
        };
        // 右键菜单：在文件夹中显示 / 播放 / 复制 / 改名 / 删除
        var menu = new ContextMenu();
        foreach (var (header, act) in new (string, Action)[]
        {
            ("📂 在文件夹中显示", () => ViewHelpers.OpenInExplorer(vp)),
            ("▶ 播放", () => PlayVideoInline(vp)),
            ("📋 复制", () => CopyVideo(vp)),
            ("✏️ 改名", () => RenameVideo(vp)),
            ("🗑 删除", () => DeleteVideo(vp))
        })
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        card.ContextMenu = menu;
        // 按住左键拖出复制（可拖到资源管理器/桌面复制该视频文件）；
        // onClick 在「未拖动的普通点击」松开时触发，避免按下即打开播放器挡住拖拽
        ViewHelpers.AttachDragCopy(card, vp, () =>
        {
            if (_multiSelectMode)
                ToggleVideoSelection(vp, selOverlay);
            else
                PlayVideoInline(vp);
        });

        return card;
    }

    /// <summary>
    /// 将缩略图提取任务加入队列。已命中的缓存立即在线程上回填，无需解码。
    /// 未命中项由 worker 池并发处理（每个 worker 复用一个"热"播放器，共享解码管线）。
    /// </summary>
    private void BeginExtractThumbnail(string path, Image target, Grid placeholder, Border? durBadge = null)
    {
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; }
        catch { stamp = 0; }

        // 1) 内存缓存命中：直接回填，跳过解码与磁盘读取
        if (_thumbCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
        {
            var bmp = cached.Bmp; var dur = cached.Dur;
            Dispatcher.BeginInvoke(() =>
            {
                ApplyThumb(target, placeholder, bmp);
                SetDurationBadge(durBadge, dur);
            });
            return;
        }

        // 2) 持久化封面命中：仅快速解码 JPEG，即秒开（无需解码视频）
        if (TryLoadPersistedCover(path, stamp, out var pbmp, out var pdur) && pbmp != null)
        {
            _thumbCache[path] = (stamp, pbmp, pdur); // 提升到内存缓存，避免重复读盘
            var bmp = pbmp; var dur = pdur;
            Dispatcher.BeginInvoke(() =>
            {
                ApplyThumb(target, placeholder, bmp);
                SetDurationBadge(durBadge, dur);
            });
            return;
        }

        // 3) 均未命中：入队由 worker 池解码，解码后回填并落盘持久化封面
        _thumbQueue.Enqueue(new ThumbJob
        {
            Path = path, Stamp = stamp,
            Img = target, Placeholder = placeholder, Badge = durBadge
        });
    }

    /// <summary>启动受限并行的 worker 池，消费队列中所有待提取任务。已在运行则不再重复启动。</summary>
    private void EnsureThumbnailWorkersRunning()
    {
        if (_thumbQueue.IsEmpty) return;
        // 非阻塞尝试获取并行槽位：空闲则开 worker，槽位满则停止新增（既有 worker 会继续消费共享队列）。
        while (!_thumbQueue.IsEmpty && _thumbWorkerSlots.Wait(0))
            Task.Run(ThumbnailWorkerLoop);
    }

    /// <summary>
    /// 单个 worker：持有一个"热"MediaPlayer 串行处理它领取到的多个视频，共享解码管线。
    /// 相比串行 1 路，N 路并行让首屏缩略图同时解码填充，明显加速首次加载。
    /// </summary>
    private void ThumbnailWorkerLoop()
    {
        System.Windows.Media.MediaPlayer? player = null;
        try
        {
            player = new System.Windows.Media.MediaPlayer { ScrubbingEnabled = true, Volume = 0 };
            while (_thumbQueue.TryDequeue(out var job))
            {
                if (job.Img.Dispatcher == null) continue;
                var res = ExtractThumbnailCore(player, job.Path, 320, 180);
                if (res.Bmp == null) continue;
                // 落盘持久化封面：下次进入该章节直接秒开，无需再次解码视频
                SavePersistedCover(job.Path, res.Bmp, res.Dur);
                // 缓存有上限：超限时丢弃较早的条目，控制内存占用
                if (_thumbCache.Count >= 150)
                    _thumbCache.Clear();
                _thumbCache[job.Path] = (job.Stamp, res.Bmp, res.Dur);
                var bmp = res.Bmp; var dur = res.Dur;
                job.Img.Dispatcher.BeginInvoke(() =>
                {
                    ApplyThumb(job.Img, job.Placeholder, bmp);
                    SetDurationBadge(job.Badge, dur);
                });
            }
        }
        catch { }
        finally
        {
            player?.Close();
            _thumbWorkerSlots.Release();
        }
    }

    // ===== 持久化封面：首帧落盘 JPEG + 时长 sidecar，首次加载无需解码视频，秒开 =====

    /// <summary>持久化封面目录（与视频同级 .video_thumbs 隐藏缓存文件夹，避免污染素材目录）</summary>
    private static string GetCoverDir(string videoPath)
        => Path.Combine(Path.GetDirectoryName(videoPath) ?? "", ".video_thumbs");
    private static string GetCoverFile(string videoPath)
        => Path.Combine(GetCoverDir(videoPath), Path.GetFileNameWithoutExtension(videoPath) + ".jpg");
    private static string GetCoverMetaFile(string videoPath)
        => Path.Combine(GetCoverDir(videoPath), Path.GetFileNameWithoutExtension(videoPath) + ".txt");

    /// <summary>
    /// 尝试读取已持久化的首帧封面。仅当封面文件存在且时间戳不早于视频源文件时才有效；
    /// 可保证与源视频当前内容一致（导入后封面即生成，之后一直命中，无需解码视频）。
    /// </summary>
    private static bool TryLoadPersistedCover(string videoPath, long videoStamp,
        out BitmapSource? bmp, out TimeSpan dur)
    {
        bmp = null; dur = TimeSpan.Zero;
        var coverFile = GetCoverFile(videoPath);
        var metaFile = GetCoverMetaFile(videoPath);
        try
        {
            if (!File.Exists(coverFile) || !File.Exists(metaFile)) return false;
            // 封面早于源视频：源视频被修改过，需重新生成
            if (File.GetLastWriteTimeUtc(coverFile).Ticks < videoStamp) return false;

            var bi = new BitmapImage();
            bi.BeginInit();
            using (var ms = new MemoryStream(File.ReadAllBytes(coverFile)))
            {
                bi.StreamSource = ms;
                bi.DecodePixelWidth = 320;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
            }
            bi.Freeze();
            bmp = bi;
            if (double.TryParse(File.ReadAllText(metaFile).Trim(),
                    System.Globalization.CultureInfo.InvariantCulture, out var secs) && secs > 0)
                dur = TimeSpan.FromSeconds(secs);
            return true;
        }
        catch { return false; }
    }

    /// <summary>把首帧封面 JPEG 及时长 sidecar 落盘，供下次快速加载（后台线程调用，线程安全）。</summary>
    private static void SavePersistedCover(string videoPath, BitmapSource bmp, TimeSpan dur)
    {
        try
        {
            var dir = GetCoverDir(videoPath);
            Directory.CreateDirectory(dir);

            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(GetCoverFile(videoPath));
            encoder.Save(fs);
            // 设置封面文件时间为当前时间，确保比源视频新
            try { File.SetLastWriteTimeUtc(GetCoverFile(videoPath), DateTime.UtcNow); } catch { }
            File.WriteAllText(GetCoverMetaFile(videoPath),
                dur.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { }
    }

    private static void SetDurationBadge(Border? durBadge, TimeSpan dur)
    {
        if (durBadge == null) return;
        if (dur <= TimeSpan.Zero || dur.TotalSeconds < 0.5)
        {
            durBadge.Visibility = Visibility.Collapsed;
            return;
        }
        if (durBadge.Child is TextBlock tb)
            tb.Text = dur.TotalHours >= 1
                ? $"{(int)dur.TotalHours:00}:{dur.Minutes:00}:{dur.Seconds:00}"
                : $"{(int)dur.TotalMinutes:00}:{dur.Seconds:00}";
        durBadge.Visibility = Visibility.Visible;
    }

    private static void ApplyThumb(Image target, Grid placeholder, BitmapSource bmp)
    {
        target.Source = bmp;
        if (placeholder.Parent is Panel p)
        {
            p.Children.Remove(placeholder);
            if (!p.Children.Contains(target))
                p.Children.Add(target);
        }
    }

    /// <summary>
    /// 后台提取视频首帧缩略图并返回时长。
    /// 性能优化：热播放器复用 + 受限并行 worker 池 + 内存缓存，多路同时解码加速首次加载。
    /// </summary>
    private static (BitmapSource? Bmp, TimeSpan Dur) ExtractThumbnailCore(
        System.Windows.Media.MediaPlayer player, string path, int w, int h)
    {
        try
        {
            player.Open(new Uri(path));

            // 轮询等待解码器初始化（最多 5 秒）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!player.NaturalDuration.HasTimeSpan && sw.ElapsedMilliseconds < 5000)
                System.Threading.Thread.Sleep(50);

            if (!player.NaturalDuration.HasTimeSpan) return (null, TimeSpan.Zero);

            var dur = player.NaturalDuration.TimeSpan;
            if (dur.TotalSeconds < 0.1) return (null, dur);

            // 按时间比采样多个点位，提高命中非黑帧的概率：
            // 先试广覆盖的 8 个点，若仍全黑再在 1%~95% 匀步长扫描，确保尽量拿到一帧有效封面
            double[] seekRatios = { 0.10, 0.25, 0.50, 0.75, 0.90, 0.05, 0.01, 0.02 };
            int wait = 180; // 首次定位等待稍长，后续复用热播放器大幅缩短
            foreach (var ratio in seekRatios)
            {
                if (TrySeekFrame(player, ratio, dur, w, h, wait, out var hit)) return (hit, dur);
                wait = 90;
            }
            // 兜底：从 3% 到 95% 匀步长扫描，直到找到非黑帧
            for (double p = 0.03; p <= 0.95; p += 0.06)
            {
                if (TrySeekFrame(player, p, dur, w, h, 90, out var hit)) return (hit, dur);
            }
            return (null, dur);
        }
        catch { return (null, TimeSpan.Zero); }
        // 不在此 Close：播放器由 worker 复用并统一在循环结束后关闭，保持解码管线热复用
    }

    /// <summary>跳到指定时间比并尝试渲染一帧；成功则返回冻结的位图。</summary>
    private static bool TrySeekFrame(System.Windows.Media.MediaPlayer player, double ratio,
        TimeSpan dur, int w, int h, int wait, out BitmapSource? bmp)
    {
        bmp = null;
        try
        {
            var pos = TimeSpan.FromSeconds(Math.Clamp(
                dur.TotalSeconds * ratio, 0, Math.Max(0, dur.TotalSeconds - 0.03)));
            player.Position = pos;
            player.Pause();

            System.Threading.Thread.Sleep(wait);

            // 检查是否成功定位
            if (Math.Abs((player.Position - pos).TotalSeconds) > 1.0) return false;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawVideo(player, new Rect(0, 0, w, h));

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            if (rtb.Width > 1 && rtb.Height > 1 && !IsFrameAllBlack(rtb))
            {
                rtb.Freeze();
                bmp = rtb;
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static bool IsFrameAllBlack(RenderTargetBitmap bmp)
    {
        try
        {
            // 采样中心点像素判断是否为纯黑
            var stride = bmp.PixelWidth * 4;
            var pixels = new byte[stride * bmp.PixelHeight];
            bmp.CopyPixels(pixels, stride, 0);
            
            int centerIdx = (bmp.PixelHeight / 2) * stride + (bmp.PixelWidth / 2) * 4;
            byte b = pixels[centerIdx];
            byte g = pixels[centerIdx + 1];
            byte r = pixels[centerIdx + 2];
            
            // 如果中心像素很暗（接近黑色），可能是黑帧
            return r < 15 && g < 15 && b < 15;
        }
        catch { return false; }
    }

    // ===== 悬停自动播放预览 =====

    private void StartHoverPreview(Grid thumbArea, string path)
    {
        if (!ViewHelpers.IsVideoFile(path)) return;
        _previewThumbArea = thumbArea;
        _previewPath = path;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void StopHoverPreview()
    {
        if (_hoverTimer != null) _hoverTimer.Stop();
        _previewPath = "";
        _previewThumbArea = null;
        // 保留共享播放器实例并暂停（不 Close），再次悬停同一视频时无需重新解码，更丝滑
        if (_hoverPreview != null)
        {
            if (_hoverPreview.Parent is Panel p2)
                p2.Children.Remove(_hoverPreview);
            try { _hoverPreview.Pause(); } catch { }
        }
    }

    /// <summary>
    /// 彻底释放悬停预览共享播放器的文件句柄（设为 null 并 Close）。
    /// 用于转内嵌播放前调用：避免同一视频被页面共享播放器与内嵌播放器同时打开导致黑屏。
    /// </summary>
    private void ReleaseHoverPreview()
    {
        StopHoverPreview();
        if (_hoverPreview == null) return;
        try { _hoverPreview.Source = null; } catch { }
        try { _hoverPreview.Close(); } catch { }
        // 关闭后该播放器需重新设置 Source 才能再播，直接丢弃，下次新建
        _hoverPreview = null;
    }

    private void ShowHoverPreview()
    {
        if (_previewThumbArea == null || string.IsNullOrEmpty(_previewPath)) return;
        // 移除旧预览（若还在树中）
        if (_hoverPreview != null && _hoverPreview.Parent is Panel old)
            old.Children.Remove(_hoverPreview);

        if (_hoverPreview == null)
        {
            _hoverPreview = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Stop,
                Stretch = Stretch.UniformToFill,
                Volume = 0,
                IsMuted = true
            };
            // 循环播放
            _hoverPreview.MediaEnded += (_, _) =>
            {
                try { _hoverPreview.Position = TimeSpan.Zero; _hoverPreview.Play(); } catch { }
            };
        }

        try
        {
            // 同一视频再次悬停时不重复设置 Source，避免重新解码
            if (_hoverPreview.Source == null ||
                !string.Equals(_hoverPreview.Source.OriginalString, _previewPath, StringComparison.OrdinalIgnoreCase))
                _hoverPreview.Source = new Uri(_previewPath);
            Grid.SetRow(_hoverPreview, 0);
            Grid.SetColumn(_hoverPreview, 0);
            Panel.SetZIndex(_hoverPreview, 30);
            _previewThumbArea.Children.Add(_hoverPreview);
            _hoverPreview.Position = TimeSpan.Zero;
            _hoverPreview.Play();
        }
        catch { }
    }

    // ===== 多选模式 =====
    private void ToggleVideoMultiSelect_Click(object sender, RoutedEventArgs e)
    {
        _multiSelectMode = !_multiSelectMode;
        _selectedFiles.Clear();
        VideoMultiSelectBtn.Content = _multiSelectMode
            ? "☑ 退出多选" : "☐ 多选";
        VideoCopySelectedBtn.Visibility = _multiSelectMode ? Visibility.Visible : Visibility.Collapsed;
        RefreshVideoGrid();
    }

    private void ToggleVideoSelection(string filePath, Border overlay)
    {
        if (_selectedFiles.Contains(filePath))
        {
            _selectedFiles.Remove(filePath);
            overlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            _selectedFiles.Add(filePath);
            overlay.Visibility = Visibility.Visible;
        }
    }

    private void CopyVideoSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFiles.Count == 0) return;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, _selectedFiles.ToArray());
            Clipboard.SetDataObject(data);
            Toast($"✓ 已复制 {_selectedFiles.Count} 个文件");
        }
        catch { Toast("✗ 复制失败"); }
    }

    private void CopyVideo(string path)
    {
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new string[] { path });
            Clipboard.SetDataObject(data);
            Toast("✓ 视频文件已复制，可在文件管理器中粘贴");
        }
        catch
        {
            Clipboard.SetText(path);
            Toast("✓ 视频路径已复制到剪贴板");
        }
    }

    private void RenameVideo(string path)
    {
        var dlg = new InputDialog("重命名视频", "名称（不含扩展名）：",
            Path.GetFileNameWithoutExtension(path)) { Owner = Window.GetWindow(this) };
        dlg.Confirmed += name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var dir = Path.GetDirectoryName(path)!;
            var ext = Path.GetExtension(path);
            var np = Path.Combine(dir, name.Trim() + ext);
            if (!string.Equals(path, np, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(np)) FileService.DeleteFile(np);
                    File.Move(path, np);
                    Toast("✓ 已改名");
                }
                        catch { Toast("✗ 改名失败"); }
            }
            RefreshVideoGrid();
        };
        dlg.Show();
    }

    private void DeleteVideo(string path)
    {
        if (!MessageDialog.Confirm("删除视频",
            $"确定要删除视频「{Path.GetFileName(path)}」吗？\n此操作不可恢复。"))
            return;
        try { FileService.DeleteFile(path); RefreshVideoGrid(); Toast("✓ 已删除"); }
        catch { Toast("✗ 删除失败"); }
    }

    /// <summary>
    /// 在内嵌 MediaElement 中播放视频
    /// </summary>
    private void PlayVideoInline(string path)
    {
        // 先彻底释放悬停自动预览的文件句柄，避免同一视频文件被页面共享播放器与内嵌播放器同时打开（易导致后打开者黑屏）
        ReleaseHoverPreview();

        var name = Path.GetFileName(path);
        var win = new Window
        {
            Title = $"播放 - {name}",
            Width = 900, Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            Background = Brushes.Black,
            ResizeMode = ResizeMode.CanResizeWithGrip
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 视频播放器
        var me = new System.Windows.Controls.MediaElement
        {
            Source = new Uri(path),
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Stop,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Volume = 1,
            ScrubbingEnabled = true
        };
        rootGrid.Children.Add(me);

        // 控制栏面板
        var controlBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x11, 0x11, 0x11)),
            Padding = new Thickness(12, 8, 12, 8)
        };
        Grid.SetRow(controlBar, 1);

        var controlGrid = new Grid();
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 播放/暂停
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 当前时间
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 进度条
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        controlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 总时长

        // 播放/暂停按钮
        var ppBtn = new Button
        {
            Content = "\uE768", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18, Width = 36, Height = 36, Padding = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Brushes.White,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        controlGrid.Children.Add(ppBtn);

        // 当前时间
        var curLabel = new TextBlock
        {
            Text = "00:00", FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(curLabel, 2);
        controlGrid.Children.Add(curLabel);

        // 进度条
        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Value = 0,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true
        };
        Grid.SetColumn(slider, 4);
        controlGrid.Children.Add(slider);

        // 总时长
        var durLabel = new TextBlock
        {
            Text = "00:00", FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(durLabel, 6);
        controlGrid.Children.Add(durLabel);

        controlBar.Child = controlGrid;
        rootGrid.Children.Add(controlBar);

        // 底部关闭栏
        var closeBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x80, 0x11, 0x11, 0x11)),
            Padding = new Thickness(0, 2, 12, 4),
            Height = 8
        };
        Grid.SetRow(closeBar, 2);
        rootGrid.Children.Add(closeBar);

        win.Content = rootGrid;

        bool isPlaying = false;
        bool isAtEnd = false;
        bool started = false;
        bool wasPlayingBeforeDrag = true;
        bool sliderDragging = false;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        // 播放/暂停
        ppBtn.Click += (_, _) =>
        {
            if (isAtEnd)
            {
                me.Position = TimeSpan.Zero;
                isAtEnd = false;
            }
            if (isPlaying) { me.Pause(); ppBtn.Content = "\uE768"; }
            else { me.Play(); ppBtn.Content = "\uE769"; if (!timer.IsEnabled) timer.Start(); }
            isPlaying = !isPlaying;
        };

        // 视频打开后自动播放
        me.MediaOpened += (_, _) =>
        {
            started = true;
            Dispatcher.BeginInvoke(() =>
            {
                if (me.NaturalDuration.HasTimeSpan)
                {
                    slider.Maximum = me.NaturalDuration.TimeSpan.TotalSeconds;
                    durLabel.Text = FormatTime(me.NaturalDuration.TimeSpan);
                }
                me.Play();
                ppBtn.Content = "\uE769";
                isPlaying = true;
                if (!timer.IsEnabled) timer.Start();
            });
        };

        // 兜底：窗口加载后如果还没播放则自动播放
        win.Loaded += (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (started) return;
                if (me.NaturalDuration.HasTimeSpan)
                {
                    slider.Maximum = me.NaturalDuration.TimeSpan.TotalSeconds;
                    durLabel.Text = FormatTime(me.NaturalDuration.TimeSpan);
                }
                me.Play();
                ppBtn.Content = "\uE769";
                isPlaying = true;
                if (!timer.IsEnabled) timer.Start();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        };

        // 播放完毕：停在最后一帧
        me.MediaEnded += (_, _) =>
        {
            isAtEnd = true;
            isPlaying = false;
            ppBtn.Content = "\uE768";
            me.Pause();
            timer.Stop();
            slider.Value = slider.Maximum;
            curLabel.Text = durLabel.Text;
        };

        // 定时更新进度（拖动时不更新滑块值，防止冲突）
        timer.Tick += (_, _) =>
        {
            if (!sliderDragging && me.NaturalDuration.HasTimeSpan)
            {
                slider.Value = me.Position.TotalSeconds;
                curLabel.Text = FormatTime(me.Position);
            }
        };

        // 进度条：拖拽时暂停 + 只更新标签，松手时 seek 并恢复
        slider.PreviewMouseDown += (_, _) =>
        {
            sliderDragging = true;
            wasPlayingBeforeDrag = isPlaying;
            if (isPlaying) { me.Pause(); isPlaying = false; ppBtn.Content = "\uE768"; }
        };
        slider.PreviewMouseUp += (_, _) =>
        {
            me.Position = TimeSpan.FromSeconds(slider.Value);
            sliderDragging = false;
            if (wasPlayingBeforeDrag) { me.Play(); isPlaying = true; ppBtn.Content = "\uE769"; }
        };
        slider.ValueChanged += (s, _) =>
        {
            if (sliderDragging)
                curLabel.Text = FormatTime(TimeSpan.FromSeconds(slider.Value));
        };

        // 双击全屏 / 再次双击退出全屏（使用 WorkArea 避免覆盖任务栏）。
        // 用延迟的单击定时器区分「单击暂停/播放」与「双击全屏」，避免触发双击时被单击逻辑抢先暂停。
        bool isFullScreen = false;
        double fsLeft = 0, fsTop = 0, fsWidth = 0, fsHeight = 0;
        var singleClickTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
            Tag = "scrub"
        };
        singleClickTimer.Tick += (_, _) =>
        {
            singleClickTimer.Stop();
            ppBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        me.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                singleClickTimer.Stop(); // 取消本次双击产生的第一次「单击」暂停

                // 切换全屏会改变窗口样式，可能导致 MediaElement 重载而从头播放。
                // 先记录当前位置与播放状态，切换后再恢复，保证双击放大/缩小不从开头重播、也不被暂停。
                var prePos = me.Position;
                bool prePlaying = isPlaying;

                if (!isFullScreen)
                {
                    fsLeft = win.Left; fsTop = win.Top;
                    fsWidth = win.Width; fsHeight = win.Height;
                    win.WindowStyle = WindowStyle.None;
                    var wa = SystemParameters.WorkArea;
                    win.Left = wa.Left; win.Top = wa.Top;
                    win.Width = wa.Width; win.Height = wa.Height;
                    win.ResizeMode = ResizeMode.NoResize;
                    isFullScreen = true;
                }
                else
                {
                    win.WindowStyle = WindowStyle.SingleBorderWindow;
                    win.ResizeMode = ResizeMode.CanResizeWithGrip;
                    win.Left = fsLeft; win.Top = fsTop;
                    win.Width = fsWidth; win.Height = fsHeight;
                    isFullScreen = false;
                }

                // 等窗口布局重算完成后，恢复播放位置；若之前在播放则继续播
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (prePos < me.NaturalDuration) me.Position = prePos;
                    if (prePlaying)
                    {
                        me.Play();
                        ppBtn.Content = "\uE769";
                        isPlaying = true;
                        if (!timer.IsEnabled) timer.Start();
                    }
                    else if (isAtEnd) return;
                    slider.Value = me.Position.TotalSeconds;
                    curLabel.Text = FormatTime(me.Position);
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
            else
            {
                // 单击：延迟执行，若 250ms 内收到第二次点击则取消，避免双击切换全屏时被暂停
                singleClickTimer.Stop();
                singleClickTimer.Start();
            }
        };

        // 空格键暂停/播放
        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) win.Close();
            if (e.Key == Key.Space) { ppBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        };

        // 关闭时清理
        win.Closed += (_, _) => { timer.Stop(); me.Stop(); me.Close(); };

        win.Show();
    }

    private static string FormatTime(TimeSpan t)
    {
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    private void VideoScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    // ===== 导入 + 拖拽 =====
    private void ImportVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null || _currentChapter == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.wmv",
            Multiselect = true, Title = "选择要导入的视频文件"
        };
        if (dlg.ShowDialog() == true)
        {
            var targetDir = FileService.ChapterVideosPath(
                App.WorkRoot, _currentNovel.MediaFolder, _currentChapter.FolderName);
            foreach (var file in dlg.FileNames)
                FileService.CopyFile(file, targetDir);
            RefreshVideoGrid();
            Toast("✓ 已导入");
        }
    }

    private void AiGenerateVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null || _currentChapter == null)
        {
            Toast(_currentNovel == null
                ? "⚠ 请先导入或创建小说，才能生成视频"
                : "⚠ 请先在左侧选择一部小说并进入章节，才能生成视频");
            return;
        }

        var config = FileService.LoadConfig(App.WorkRoot);
        if (!config.VideoApi.IsConfigured)
        {
            Toast("⚠ 请先在「AI 接口配置」窗口中配置视频接口地址和密钥");
            return;
        }
        // Flash 模型固定 720P 且不支持参考视频；非 Flash（agnes-video-2.5）支持 720P/960P/2K 与参考视频
        var isFlashModel = ViewHelpers.IsFlashVideoModel(config.VideoApi.ModelId);

        // 用 Win32 层归属（SetWin32Owner）代替 WPF Owner：子窗口可被鼠标选中、可被 Alt+Tab 切换，
        // 并在任务栏与主窗口共用同一图标；同时规避 WPF 关闭 owned 窗口误激活/最小化 AllowsTransparency 主窗口的问题。
        var win = new Window
        {
            Title = "AI 生成视频",
            Width = 1010, Height = 620,
            MinWidth = 980, MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = true,
            ResizeMode = ResizeMode.CanResize,
            Background = (Brush)FindResource("WindowBackgroundBrush")
        };
        ViewHelpers.SetWin32Owner(win, Window.GetWindow(this));
        ViewHelpers.CenterWindowOnOwner(win, Window.GetWindow(this));

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 标题
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });                    // 1 间距
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 2 主体（左栏|中央|右栏）
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });                    // 3 间距
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 4 底部
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                  // 0 左栏
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 1 中央
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                  // 2 右栏

        // 标题区域（含优化按钮）
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = $"AI 生成视频 · 第{_currentChapter.Index}章 {_currentChapter.Title}",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 2)
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "描述主体、动作、场景、镜头运动、光照和视觉风格",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush")
        });
        headerGrid.Children.Add(titleStack);

        // 优化提示词按钮（位于标题行右侧）
        var optimizeBtn = new Button
        {
            Content = "✨ 优化提示词",
            FontSize = 12, Padding = new Thickness(14, 5, 14, 5),
            Style = (Style)FindResource("PrimaryButtonStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "AI 将您的简短提示词丰富为高质量视频生成提示词"
        };
        Grid.SetColumn(optimizeBtn, 1);
        headerGrid.Children.Add(optimizeBtn);

        Grid.SetRow(headerGrid, 0);
        Grid.SetColumnSpan(headerGrid, 3);
        grid.Children.Add(headerGrid);

        // 提示词（支持 @ 提及参考图 + 图像名称自动匹配 + 内置水印）
        var promptBox = new PromptMentionBox
        {
            Watermark = "在此输入提示词…"
        };

        // 「提示词设置」按钮（优化提示词右侧，内含中英文切换 + 实时自动匹配）
        var settingsBtn = ViewHelpers.BuildGenSettingsButton(promptBox, msg => Toast(msg));
        Grid.SetColumn(settingsBtn, 2);
        headerGrid.Children.Add(settingsBtn);

        // 底部区域：分为上下两行
        var footerStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 8, 0, 0) };

        // 第1行：参考图（图生视频，可选）+ 状态（两行网格，按钮/缩略图自动换行）
        // 第1行：参考图 + 参考视频（统一素材区，纵向堆叠）
        var materialStack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var refImages = new List<string>();
        var refPaths = new List<string>();  // 与 refImages 对应的源文件路径（用于历史回填）

        // ===== 参考素材（图片/视频/音频）统一素材区，按上传类型自动分类并限制数量 =====
        const int MaxRefImages = 5, MaxRefVideo = 1, MaxRefAudio = 3;

        string? refVideoData = null;   // 参考视频 base64（DataUrl）
        string? refVideoPath = null;   // 参考视频源文件路径（用于历史回填）
        string? sequelLabel = null;        // 视频续集尾帧当前 label
        string? sequelFrameData = null;    // 尾帧 base64（供重建时保留尾帧参考图）
        string? sequelFrameSource = null;  // 尾帧源视频路径
        List<string> audioFiles = new();      // 参考音频源文件路径
        List<string> audioDataUrls = new();   // 参考音频 base64（DataUrl）
        Border? videoChip = null;             // 参考视频 chip（名称 + ✕）
        List<Border> audioChips = new();      // 参考音频 chips（仅用于重排 <Audio N> 编号）

        // 参考素材区：非内联模式，标题+角标在左、操作按钮右对齐；内容区图片/视频/音频各占一行
        var materialStrip = new MaterialStrip("参考素材",
            "支持图片/视频/音频，按上传类型自动分类；图片 ≤5 张、视频 ≤1 段、音频 ≤3 段",
            "🧩");
        var addMaterialBtn = materialStrip.AddButton("添加素材",
            "选择图片/视频/音频文件，按类型自动分类并限制数量（图片≤5 / 视频≤1 / 音频≤3）");
        var clearAllBtn = materialStrip.AddButton("✕ 全部清除", "清除所有参考素材（参考图/参考视频/参考音频）");
        clearAllBtn.Visibility = Visibility.Collapsed;

        // 内容区：图片 / 视频 / 音频 各自独立一行，行内自动换行，避免混排溢出
        var contentStack = new StackPanel { Orientation = Orientation.Vertical };
        var imageWrap = new WrapPanel();                                        // 图片行
        var videoRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };    // 视频行
        var audioWrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };   // 音频行
        contentStack.Children.Add(imageWrap);
        contentStack.Children.Add(videoRow);
        contentStack.Children.Add(audioWrap);
        materialStrip.ContentPanel.Children.Add(contentStack);

        // ===== 右侧边栏：项目资产（点击按顺序编号，作为参考图顺序） =====
        var assetPaths = _currentNovel != null
            ? ViewHelpers.CollectProjectImagePaths(App.WorkRoot, _currentNovel.Id, _currentNovel.MediaFolder)
            : new List<string>();
        var assetPanel = new AssetPanel("项目资产", assetPaths, maxCount: MaxRefImages);
        // 参考图缩略图被 ✕ 删除时：同步移除右侧栏对应选中项（保留其余顺序与编号），并刷新 @ 提及候选
        void RemoveSelectedRef(string p)
        {
            assetPanel.RemoveSelected(p);
            UpdateMergedState();
        }

        // 应用参考图（缩略图流，支持多张；最多 MaxRefImages 张）
        void ApplyRefImage(string path)
        {
            ViewHelpers.AddReferenceThumb(imageWrap, path, refImages, UpdateMergedState, maxCount: MaxRefImages, refPaths: refPaths, onRefRemoved: RemoveSelectedRef);
            UpdateMergedState();
        }

        // 构建「文件名 + ✕」样式的 chip（参考视频 / 参考音频共用）
        Border MakeFileChip(string label, string tooltip, Action onRemove)
        {
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x20, 0x4A, 0x90, 0xE2)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x4A, 0x90, 0xE2)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 1, 6, 1),
                ToolTip = tooltip
            };
            var lay = new StackPanel { Orientation = Orientation.Horizontal };
            lay.Children.Add(new TextBlock
            {
                Text = label, FontSize = 9.5, Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center, MaxWidth = 170,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var removeBtn = new Button
            {
                Content = "✕", FontSize = 9, Padding = new Thickness(2, 0, 2, 0), Margin = new Thickness(4, 0, 0, 0),
                Background = Brushes.Transparent, Foreground = Brushes.White,
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "移除该项"
            };
            removeBtn.Click += (_, _) => onRemove();
            lay.Children.Add(removeBtn);
            chip.Child = lay;
            return chip;
        }

        // 参考视频：替换式添加 chip（仅 1 段；agnes-video-2.5-flash 不支持）
        void AddVideoRef(string path)
        {
            if (isFlashModel)
            {
                Toast("⚠ agnes-video-2.5-flash 不支持参考视频，请改用 agnes-video-2.5");
                return;
            }
            var data = ViewHelpers.VideoToBase64DataUrl(path);
            if (data == null) { Toast("⚠ 参考视频读取失败"); return; }
            if (videoChip != null) videoRow.Children.Remove(videoChip);
            refVideoData = data;
            refVideoPath = path;
            videoChip = MakeFileChip($"<Video 1> {Path.GetFileName(path)}", path, RemoveVideoRef);
            videoRow.Children.Add(videoChip);
            Toast("✓ 已添加参考视频");
            UpdateMergedState();
        }
        void RemoveVideoRef()
        {
            if (videoChip != null) videoRow.Children.Remove(videoChip);
            videoChip = null;
            refVideoData = null;
            refVideoPath = null;
            UpdateMergedState();
        }

        // 参考音频：追加 chip（自动编号 <Audio N>，最多 MaxRefAudio 段）
        void AddAudioRef(string path)
        {
            if (audioDataUrls.Count >= MaxRefAudio) { Toast($"⚠ 参考音频最多 {MaxRefAudio} 段，已忽略多余文件"); return; }
            if (audioFiles.Contains(path)) { Toast("⚠ 该音频已在列表中"); return; }
            var data = ViewHelpers.AudioToBase64DataUrl(path);
            if (string.IsNullOrEmpty(data)) { Toast($"⚠ 无法读取音频：{Path.GetFileName(path)}"); return; }
            audioFiles.Add(path);
            audioDataUrls.Add(data);
            Border chip = null!;   // 先声明，供移除回调捕获使用
            chip = MakeFileChip($"<Audio {audioDataUrls.Count}> {Path.GetFileName(path)}", path, () =>
            {
                var i = audioChips.IndexOf(chip);
                if (i < 0) return;
                audioWrap.Children.Remove(chip);
                audioChips.RemoveAt(i);
                audioFiles.RemoveAt(i);
                audioDataUrls.RemoveAt(i);
                RenumberAudioChips();
            });
            audioChips.Add(chip);
            audioWrap.Children.Add(chip);
        }
        void RenumberAudioChips()
        {
            for (int i = 0; i < audioChips.Count; i++)
                if (audioChips[i].Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock t)
                    t.Text = $"<Audio {i + 1}> {Path.GetFileName(audioFiles[i])}";
            UpdateMergedState();
        }

        // 依据扩展名自动归类上传：图片→参考图、视频→参考视频、音频→参考音频，并自动限制数量
        void AddMaterialFiles(string[] files)
        {
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif") ApplyRefImage(file);
                else if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm") AddVideoRef(file);
                else if (ext is ".mp3" or ".wav" or ".m4a" or ".aac" or ".flac" or ".ogg" or ".wma") AddAudioRef(file);
                else Toast($"⚠ 不支持的类型：{Path.GetFileName(file)}");
            }
            UpdateMergedState();
        }

        // 统一刷新：角标 = 总数量，提示文字与 @ 提及候选随参考图路径更新
        void UpdateMergedState()
        {
            var imgs = refImages.Count;
            var hasVideo = !string.IsNullOrWhiteSpace(refVideoData);
            var auds = audioDataUrls.Count;
            var total = imgs + (hasVideo ? 1 : 0) + auds;
            clearAllBtn.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
            materialStrip.SetCount(total);
            var parts = new List<string>();
            if (imgs > 0) parts.Add($"图片 {imgs}/{MaxRefImages}");
            if (hasVideo) parts.Add($"视频 1/{MaxRefVideo}");
            if (auds > 0) parts.Add($"音频 {auds}/{MaxRefAudio}");
            materialStrip.HintText.Text = parts.Count == 0
                ? "支持图片/视频/音频，按上传类型自动分类；图片 ≤5 张、视频 ≤1 段、音频 ≤3 段"
                : $"已选：{string.Join("、", parts)}；提示词用 @图片名 / <Video 1> / <Audio N> 指代";
            // 参考图（路径）变化时同步 @ 提及候选与图像名称自动匹配
            promptBox.SetRefImages(refPaths);
        }

        // 「添加素材」：支持多选，按扩展名自动归类
        addMaterialBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "素材文件（图片/视频/音频）|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.wma",
                Title = "选择参考素材（图片/视频/音频，可多选）", Multiselect = true
            };
            if (dlg.ShowDialog(win) != true) return;
            AddMaterialFiles(dlg.FileNames);
        };

        // 全部清除：同时清空参考素材与右侧栏资产选择
        void ClearAllMaterials()
        {
            assetPanel.ClearSelection();
            refImages.Clear(); refPaths.Clear();
            audioFiles.Clear(); audioDataUrls.Clear(); audioChips.Clear();
            refVideoData = null; refVideoPath = null; videoChip = null;
            imageWrap.Children.Clear();
            videoRow.Children.Clear();
            audioWrap.Children.Clear();
            sequelLabel = null; sequelFrameData = null; sequelFrameSource = null;
            UpdateMergedState();
        }
        clearAllBtn.Click += (_, _) => ClearAllMaterials();

        // 仅清空参考图缩略图（保留参考视频/参考音频 chip）
        void ClearReferenceImages()
        {
            refImages.Clear(); refPaths.Clear();
            for (int i = imageWrap.Children.Count - 1; i >= 0; i--)
                if (imageWrap.Children[i] is Border b && b.Tag is string s && s == "refthumb") imageWrap.Children.RemoveAt(i);
        }

        // 右侧栏选择顺序 → 重建参考图列表（含手动添加的本地参考图共存于 refImages）
        void RebuildRefsFromAssets()
        {
            // 保留「视频续集尾帧」参考图，避免重建时被清掉（重建会按右侧资产顺序重排普通图）
            string? saveData = sequelFrameData, saveSrc = sequelFrameSource, saveLabel = sequelLabel;
            ClearReferenceImages();
            // 尾帧先占一个槽位，其余留给新增资产，保证尾帧不因数量上限被挤掉
            if (saveLabel != null && saveData != null)
                ViewHelpers.AddReferenceFrame(imageWrap, saveData, saveLabel, saveSrc ?? "", refImages, refPaths, UpdateMergedState, maxCount: MaxRefImages);
            ViewHelpers.AddReferenceThumbsAsync(imageWrap, assetPanel.SelectedOrder, refImages,
                UpdateMergedState, maxCount: MaxRefImages, refPaths: refPaths, onRefRemoved: RemoveSelectedRef);
            UpdateMergedState();
        }
        assetPanel.SelectionChanged = RebuildRefsFromAssets;

        materialStack.Children.Add(materialStrip.Root);

        // ===== 左侧边栏：文本导入/编辑/选区加入/导出 + 默认提示词 =====
        void AppendPromptText(string t) => promptBox.AppendText(t);
        var promptPanel = new PromptPanel(win, "Video")
        {
            AppendToPrompt = AppendPromptText
        };

        // ===== 中央：提示词输入 + 参考素材区 =====
        var center = new Grid();
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 0 提示词
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });                  // 1 间距
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // 2 参考素材
        var promptHost = promptBox;
        Grid.SetRow(promptHost, 0);
        center.Children.Add(promptHost);
        Grid.SetRow(materialStack, 2);
        center.Children.Add(materialStack);

        // 第2行：参数卡片（占满行宽）+ 操作按钮（右对齐）
        var bottomRow = new Grid();
        bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 0 参数卡片
        bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // 1 按钮区

        // 右侧按钮容器（查看队列 / 历史 / 生成）
        var rightPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 参数卡片：分辨率档位 + 横竖屏 + 时长滑动条（滑块自动拉伸占满行内剩余宽度）
        var paramCard = new Border
        {
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var paramRow = new Grid();
        paramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 0 画质组
        paramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });                   // 1 间距
        paramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 2 比例组
        paramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });                   // 3 间距
        paramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 4 “时长”标签
        paramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 5 时长滑块（拉伸）
        paramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 6 秒数框
        paramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 7 “秒”标签

        // 分辨率档位（480P / 720P / 1080P / 2K）
        var levelGroup = new StackPanel { Orientation = Orientation.Horizontal };
        levelGroup.Children.Add(new TextBlock
        {
            Text = "画质", FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });
        var levelBox = new ComboBox
        {
            Width = 74, Height = 26, FontSize = 12,
            ItemsSource = ViewHelpers.VideoLevelsForModel(config.VideoApi.ModelId),
            SelectedItem = "720P",
            ToolTip = isFlashModel
                ? "agnes-video-2.5-flash 固定输出 720P"
                : "分辨率档位：720P / 960P / 2K（agnes-video-2.5）",
            Style = (Style)Application.Current.FindResource("ModernComboBoxStyle"),
            Background = (Brush)FindResource("WindowBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Padding = new Thickness(6, 0, 6, 0)
        };
        levelGroup.Children.Add(levelBox);
        Grid.SetColumn(levelGroup, 0);
        paramRow.Children.Add(levelGroup);

        // 比例（16:9 / 9:16 / 1:1 / 4:3 / 3:4）
        var ratioGroup = new StackPanel { Orientation = Orientation.Horizontal };
        ratioGroup.Children.Add(new TextBlock
        {
            Text = "比例", FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });
        var ratioBox = new ComboBox
        {
            Width = 72, Height = 26, FontSize = 12,
            ItemsSource = ViewHelpers.VideoRatios,
            SelectedItem = "16:9",
            Style = (Style)Application.Current.FindResource("ModernComboBoxStyle"),
            Background = (Brush)FindResource("WindowBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Padding = new Thickness(6, 0, 6, 0)
        };
        ratioGroup.Children.Add(ratioBox);
        Grid.SetColumn(ratioGroup, 2);
        paramRow.Children.Add(ratioGroup);

        // 时长滑动条（agnes-video 2.5 系列 seconds 支持 4–12，滑块拉伸占满剩余宽度）
        var durLabel = new TextBlock
        {
            Text = "时长", FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(durLabel, 4);
        paramRow.Children.Add(durLabel);
        var secSlider = new Slider
        {
            Minimum = 4, Maximum = 12, Value = 5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumn(secSlider, 5);
        paramRow.Children.Add(secSlider);
        var secBox = new TextBox
        {
            Width = 34, Height = 24, FontSize = 12, Text = "5",
            TextAlignment = TextAlignment.Center,
            Background = (Brush)FindResource("WindowBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 0, 2, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(secBox, 6);
        paramRow.Children.Add(secBox);
        var secLabel = new TextBlock
        {
            Text = "秒", FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
        Grid.SetColumn(secLabel, 7);
        paramRow.Children.Add(secLabel);

        // 当前允许的最大秒数（agnes-video 2.5 系列固定 12）
        int maxSec = 12;
        bool syncingSec = false;

        // 比例/分辨率变化时，更新时长滑块上限并钳制当前值
        void UpdateMaxSeconds()
        {
            maxSec = ViewHelpers.CalcVideoMaxSeconds(
                levelBox.SelectedItem?.ToString() ?? "720P",
                ratioBox.SelectedItem?.ToString() ?? "16:9");
            secSlider.Maximum = maxSec;
            if (secSlider.Value > maxSec) secSlider.Value = maxSec;
            if (secBox.IsKeyboardFocusWithin == false && int.TryParse(secBox.Text.Trim(), out var cur) && cur > maxSec)
            {
                syncingSec = true;
                secBox.Text = maxSec.ToString();
                syncingSec = false;
            }
            secSlider.ToolTip = $"时长范围：4–12 秒（agnes-video 2.5 系列）";
        }
        levelBox.SelectionChanged += (_, _) => UpdateMaxSeconds();
        ratioBox.SelectionChanged += (_, _) => UpdateMaxSeconds();

        // 秒数滑动条与输入框双向同步（输入超出 4~maxSec 自动钳制）
        secSlider.ValueChanged += (_, _) =>
        {
            if (syncingSec) return;
            syncingSec = true;
            secBox.Text = ((int)Math.Round(secSlider.Value)).ToString();
            syncingSec = false;
        };
        secBox.TextChanged += (_, _) =>
        {
            if (syncingSec) return;
            if (!double.TryParse(secBox.Text.Trim(), out var v) || v < 4) return;
            syncingSec = true;
            secSlider.Value = Math.Clamp(v, 4, maxSec);
            syncingSec = false;
        };
        secBox.LostFocus += (_, _) =>
        {
            if (!double.TryParse(secBox.Text.Trim(), out var v)) { secBox.Text = "5"; return; }
            secBox.Text = ((int)Math.Clamp(Math.Round(v), 4, maxSec)).ToString();
        };

        paramCard.Child = paramRow;

        var queueBtn = new Button
        {
            Content = "📋 查看队列",
            FontSize = 12, Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("SecondaryButtonStyle")
        };
        ViewHelpers.AttachQueueBadge(queueBtn);
        queueBtn.Click += (_, _) => OpenQueueWindow();
        rightPanel.Children.Add(queueBtn);

        // 历史记录：点击一条即回填提示词/档位/比例/时长/参考图/参考视频
        var historyBtn = new Button
        {
            Content = "🕘 历史", FontSize = 12, Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("SecondaryButtonStyle"),
            ToolTip = "查看历史记录，点击一条自动回填提示词、参数与参考素材"
        };
        historyBtn.Click += (_, _) =>
        {
            try
            {
                var history = AiGenHistory.Load(App.WorkRoot)
                    .Where(h => h.Type == AiGenType.Video).ToList();
                var picker = new AiGenHistoryWindow(history) { Owner = win };
                if (picker.ShowDialog() != true) return;
                var e = picker.SelectedEntry;
                if (e == null) return;
                promptBox.Text = e.Prompt;
                if (e.Level is { Length: > 0 })
                {
                    var lv = levelBox.Items.OfType<string>().FirstOrDefault(x => x == e.Level);
                    if (lv != null) levelBox.SelectedItem = lv;
                }
                if (e.Ratio is { Length: > 0 })
                {
                    var rt = ratioBox.Items.OfType<string>().FirstOrDefault(x => x == e.Ratio);
                    if (rt != null) ratioBox.SelectedItem = rt;
                }
                if (e.Seconds > 0) secSlider.Value = Math.Clamp(e.Seconds, 4, ViewHelpers.CalcVideoMaxSeconds(
                    levelBox.SelectedItem?.ToString() ?? "720P", ratioBox.SelectedItem?.ToString() ?? "16:9"));

                // 回填参考图：先同步右侧栏选择顺序（存在的资产），再补充非资产路径
                assetPanel.SetSelection(e.RefImagePaths);
                ClearReferenceImages();
                var ordered = assetPanel.SelectedOrder.ToList();
                foreach (var p in e.RefImagePaths)
                    if (!ordered.Contains(p) && System.IO.File.Exists(p)) ordered.Add(p);
                ViewHelpers.AddReferenceThumbsAsync(imageWrap, ordered, refImages,
                    UpdateMergedState, maxCount: MaxRefImages, refPaths: refPaths, onRefRemoved: RemoveSelectedRef);
                // 回填参考视频
                if (!string.IsNullOrWhiteSpace(e.RefVideoPath) && System.IO.File.Exists(e.RefVideoPath) && !isFlashModel)
                    AddVideoRef(e.RefVideoPath);
                UpdateMergedState();
                Toast("✓ 已从历史回填");
            }
            catch (Exception ex) { Toast($"⚠ 历史回填失败：{ex.Message}"); }
        };
        rightPanel.Children.Add(historyBtn);

        var genBtn = new Button
        {
            Content = "🎬 生成视频",
            FontSize = 13, Padding = new Thickness(20, 6, 20, 6),
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        rightPanel.Children.Add(genBtn);
        Grid.SetColumn(paramCard, 0);
        bottomRow.Children.Add(paramCard);
        Grid.SetColumn(rightPanel, 1);
        bottomRow.Children.Add(rightPanel);

        // ===== 视频连贯（首尾帧）面板：上传两段视频自动取 A 尾帧/B 首帧，或手动精准选帧 =====
        var videoAssets = _currentNovel != null
            ? ViewHelpers.CollectProjectVideoPaths(App.WorkRoot, _currentNovel.Id)
            : new List<string>();
        var continuity = new VideoContinuityPanel(win, videoAssets, startCollapsed: true) { Notify = Toast };
        footerStack.Children.Add(continuity.Root);

        // ===== 视频续集（尾帧参考）面板：上传上一段视频自动提取尾帧作参考图，或手动选帧 =====
        var sequel = new VideoSequelPanel(win, videoAssets, startCollapsed: true) { Notify = Toast };
        sequel.FrameReady += (data, label, sourcePath) =>
        {
            sequelLabel = label;
            sequelFrameData = data;
            sequelFrameSource = sourcePath;
            // 帧数据加入参考图列表，同时让 @ 提及与名称自动匹配识别（伪路径 frame://label|源路径）
            ViewHelpers.AddReferenceFrame(imageWrap, data, label, sourcePath, refImages, refPaths, UpdateMergedState, maxCount: MaxRefImages);
        };
        sequel.FrameCleared += () =>
        {
            if (sequelLabel != null)
            {
                ViewHelpers.RemoveReferenceFrame(imageWrap, refImages, refPaths, sequelLabel, UpdateMergedState);
                sequelLabel = null;
                sequelFrameData = null;
                sequelFrameSource = null;
            }
        };
        footerStack.Children.Add(sequel.Root);
        footerStack.Children.Add(bottomRow);
        Grid.SetRow(footerStack, 4);
        Grid.SetColumnSpan(footerStack, 3);
        grid.Children.Add(footerStack);

        // 三栏布局（左右栏可拖拽调整宽度并持久化）：左栏（提示词素材） | 中央（提示词+参考图） | 右栏（项目资产）
        var bodyRow = GenPanelLayout.CreateThreeColumn(win, promptPanel, center, assetPanel);
        Grid.SetRow(bodyRow, 2);
        Grid.SetColumnSpan(bodyRow, 3);
        grid.Children.Add(bodyRow);

        // 优化提示词按钮事件
        optimizeBtn.Click += async (_, _) =>
        {
            var rawPrompt = promptBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(rawPrompt))
            {
                Toast("⚠ 请先输入提示词再优化");
                return;
            }

            optimizeBtn.IsEnabled = false;
            optimizeBtn.Content = "⏳ 优化中...";

            try
            {
                // 是否有参考图：无=文生视频（仅依据文本优化），有=图生视频/多图参考（依据文本 + 图像内容优化）
                bool hasRef = refImages.Count > 0;

                // 使用用户自定义的优化 Skill（设置 → AI 生成 Skill 中编辑）
                var optPrompt = ViewHelpers.ResolveRefMentions(rawPrompt, refImages, "picture");   // @名 → <Picture N>
                var (sys, userMsg) = ViewHelpers.BuildOptimizePrompt(
                    config.VideoOptimizeSkill, optPrompt, hasRef, refImages.Count, subject: "视频",
                    language: FileService.LoadConfig(App.WorkRoot).OptimizePromptLanguage, markerStyle: "picture");

                // 有参考图时，将参考图作为视觉输入一起交给模型；否则仅用文本
                var optCfg = config.TextApi;
                PromptMentionBox.Dbg($"OPTV start hasRef={hasRef} txt={{url={(optCfg?.BaseUrl is null ? "NULL" : "set")},key={(string.IsNullOrEmpty(optCfg?.ApiKey) ? "∅" : "set")},model={(optCfg?.ModelId ?? "NULL")}}}");
                var result = hasRef
                    ? await ApiService.ChatWithImagesAsync(
                        config.TextApi.BaseUrl, config.TextApi.ApiKey, config.TextApi.ModelId, sys, userMsg, refImages)
                    : await ApiService.ChatAsync(
                        config.TextApi.BaseUrl, config.TextApi.ApiKey, config.TextApi.ModelId, sys, userMsg);
                PromptMentionBox.Dbg($"OPTV result-len={(result?.Length ?? -1)} head={(result?.Substring(0, Math.Min(60, result.Length)) ?? "NULL")}");

                if (!string.IsNullOrWhiteSpace(result))
                {
                    promptBox.Text = result.Trim();
                    Toast(hasRef ? "✓ 提示词已结合参考图优化" : "✓ 提示词已优化");
                }
            }
            catch (ApiException ex)
            {
                Toast($"⚠ {ex.Message}");
                PromptMentionBox.Dbg($"OPTV ApiException:{ex.Message}");
            }
            catch (Exception ex)
            {
                Toast($"⚠ 优化失败：{ex.Message}");
                PromptMentionBox.Dbg($"OPTV Exception:{ex.GetType().Name}:{ex.Message}");
            }
            finally
            {
                optimizeBtn.IsEnabled = true;
                optimizeBtn.Content = "✨ 优化提示词";
            }
        };

        win.Content = grid;

        // 拖拽媒体到窗口 → 图片自动归入资产目录并作参考图，视频/音频按扩展名自动归类为参考视频/参考音频
        ViewHelpers.EnableMediaDrop(grid,
            assetImportDir: FileService.ChapterImagesPath(App.WorkRoot, _currentNovel!.MediaFolder, _currentChapter!.FolderName),
            onImageImported: path =>
            {
                ApplyRefImage(path);
                assetPanel.SelectImported(path);
                Toast("✓ 已加入参考图并归入项目资产");
            },
            onVideoImported: path => AddVideoRef(path),
            onAudioImported: path => AddAudioRef(path),
            onInvalid: () => Toast("⚠ 请拖入支持的图片/视频/音频文件"));

        // 生成按钮：创建任务并入队，窗口保持打开，生成交给后台队列串行执行
        genBtn.Click += (_, _) =>
        {
            var prompt = ViewHelpers.ResolveRefMentions(
                ViewHelpers.AppendEnabledDefaultPrompts(promptBox.Text.Trim(), "Video"), refImages, "picture");
            if (string.IsNullOrWhiteSpace(prompt))
            { Toast("⚠ 请输入提示词"); return; }

            var level = levelBox.SelectedItem?.ToString() ?? "720P";
            var ratio = ratioBox.SelectedItem?.ToString() ?? "16:9";
            var seconds = (int)Math.Clamp(Math.Round(secSlider.Value), 4, ViewHelpers.CalcVideoMaxSeconds(level, ratio));
            var hasImageRef = refImages.Count > 0;
            var hasVideoRef = !string.IsNullOrWhiteSpace(refVideoData);
            // 视频连贯（首尾帧）：两个帧都必须设置才能以 keyframe 模式提交
            var hasKeyframes = continuity.HasAnyFrame;
            if (hasKeyframes && (continuity.FirstFrameDataUrl == null || continuity.LastFrameDataUrl == null))
            { Toast("⚠ 视频连贯需同时设置首帧和尾帧，请补全后再生成"); return; }
            // 参考模式按文档补齐 <Picture N>/<Video N>/<Audio N> 提示词引用（首尾帧模式不追加参考标签）
            var hasAudioRef = audioDataUrls.Count > 0;
            var finalPrompt = hasKeyframes
                ? prompt
                : ViewHelpers.BuildVideoPrompt(prompt, refImages.Count, hasVideoRef ? 1 : 0, audioDataUrls.Count);

            var detail = $"{level}·{ratio}·{seconds}s";
            if (hasImageRef) detail += $"·参考图{refImages.Count}";
            if (hasVideoRef) detail += "·参考视频";
            if (hasAudioRef) detail += $"·音频{audioDataUrls.Count}";
            if (hasKeyframes) detail += "·首尾帧";

            // 快照当前小说/章节，防止用户切换后任务保存到错误目录
            var novel = _currentNovel;
            var chapter = _currentChapter;
            var task = new AiTask
            {
                Type = AiTaskType.Video,
                Prompt = finalPrompt,
                Detail = detail,
                ApiEndpoint = config.VideoApi.BaseUrl,
                ApiKey = config.VideoApi.ApiKey,
                Model = config.VideoApi.ModelId,
                ApiProvider = config.VideoApi.Provider,
                TargetDir = FileService.ChapterVideosPath(App.WorkRoot, novel.MediaFolder, chapter.FolderName),
                FileNameBase = $"AI_{DateTime.Now:yyyyMMdd_HHmmss_fff}",
                VideoSize = level, VideoRatio = ratio, VideoSeconds = seconds,
                ReferenceImages = hasImageRef ? new List<string>(refImages) : null,
                ReferenceVideos = hasVideoRef ? new List<string> { refVideoData! } : null,
                ReferenceAudios = (!hasKeyframes && audioDataUrls.Count > 0) ? new List<string>(audioDataUrls) : null,
                FirstFrame = continuity.FirstFrameDataUrl,
                LastFrame = continuity.LastFrameDataUrl,
                NovelName = novel.Name,
                ScopeName = $"第{chapter.Index}章 {chapter.Title}"
            };
            AiTaskManager.Enqueue(task);
            AiGenHistory.Add(App.WorkRoot, new AiGenHistoryEntry
            {
                Type = AiGenType.Video,
                Prompt = prompt,
                Level = level,
                Ratio = ratio,
                Seconds = seconds,
                RefImagePaths = new List<string>(refPaths),
                RefVideoPath = refVideoPath ?? "",
                EngineBadge = config.VideoApi.ModelId
            });
            Toast(hasKeyframes
                ? "✓ 已加入 AI 任务队列（首尾帧连贯模式），窗口保持打开"
                : "✓ 已加入 AI 任务队列，窗口保持打开");
        };

        // 异步窗口：非模态显示且无 Owner，用户可关闭窗口/离开页面做其他事，生成在后台队列中继续
        win.Show();
    }

    /// <summary>打开 AI 任务队列窗口（Topmost 置顶显示，不设 Owner 避免遮挡与主窗口最小化问题）</summary>
    private void OpenQueueWindow()
    {
        try
        {
            var qw = new AiTaskQueueWindow();
            qw.Show();
        }
        catch (Exception ex)
        {
            Toast($"⚠ 无法打开队列：{ex.Message}");
        }
    }

    /// <summary>AI 任务完成后，若目标目录是当前章节的视频目录则实时刷新素材列表</summary>
    public void TryRefreshAfterAiTask(AiTask task)
    {
        if (_currentNovel == null || _currentChapter == null) return;
        var target = FileService.ChapterVideosPath(App.WorkRoot, _currentNovel.MediaFolder, _currentChapter.FolderName);
        if (string.Equals(target.TrimEnd('\\', '/'), task.TargetDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            RefreshVideoGrid();
    }

    private void VideoGrid_Drop(object sender, DragEventArgs e)
    {
        if (_currentNovel == null || _currentChapter == null) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var targetDir = FileService.ChapterVideosPath(
                App.WorkRoot, _currentNovel.MediaFolder, _currentChapter.FolderName);
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLower();
                if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv")
                    FileService.CopyFile(file, targetDir);
            }
            RefreshVideoGrid();
        }
    }

    private void VideoGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // ===== Toast =====
    private async void Toast(string msg)
    {
        if (ToastText == null || ToastBorder == null) return;
        ToastText.Text = msg;
        ToastBorder.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2)));
        try
        {
            await Task.Delay(1500);
            ToastBorder.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.35)));
        }
        catch { /* 页面卸载时忽略 */ }
    }
}
