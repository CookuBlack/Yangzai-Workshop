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

    public VideoPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
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

            // 封面
            var coverBorder = new Border
            {
                Width = 70, Height = 95,
                CornerRadius = new CornerRadius(4),
                Background = ParseColor(novel.CoverColor),
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
                catch { coverBorder.Child = CoverFb(novel.Name); }
            }
            else coverBorder.Child = CoverFb(novel.Name);
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
        VideoGrid.Children.Clear();
        VideoGrid.ColumnDefinitions.Clear();
        VideoGrid.RowDefinitions.Clear();

        if (_currentNovel == null || _currentChapter == null) return;

        var path = FileService.ChapterVideosPath(
            App.WorkRoot, _currentNovel.MediaFolder, _currentChapter.FolderName);
        var videos = FileService.GetFiles(path, ".mp4", ".mkv", ".avi", ".mov", ".wmv");

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
    }

    private Border CreateVideoCard(string videoPath)
    {
        string vp = videoPath;
        string name = Path.GetFileName(vp);

        var card = new Border
        {
            Style = (Style)FindResource("CardStyle"),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(4), Padding = new Thickness(6),
            Cursor = Cursors.Hand, Tag = vp, ClipToBounds = true,
            MaxWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        card.Loaded += (s, e) => ViewHelpers.ApplyRoundedClip(card);
        card.SizeChanged += (s, e) => ViewHelpers.ApplyRoundedClip(card);

        var stack = new StackPanel();

        // 缩略图区（胶卷边框 + 视频首帧）
        var thumbArea = new Grid();
        var outerFrame = new Border
        {
            Height = 120, Background = Brushes.Black,
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 0, 0)
        };

        // 胶卷边条效果（上下黑边 + 齿孔点缀）
        var filmGrid = new Grid();
        filmGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        filmGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        filmGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });

        // 上胶卷边
        var topFilm = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        // 胶卷齿孔（小方块）
        for (int k = 0; k < 12; k++)
            ((StackPanel)topFilm.Child).Children.Add(new Border
            {
                Width = 4, Height = 4, Margin = new Thickness(6, 1, 6, 1),
                Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44))
            });
        filmGrid.Children.Add(topFilm);

        // 视频首帧缩略图（先占位，异步提取）
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };
        Grid.SetRow(thumbImage, 1);
        // 占位：视频图标
        var placeGrid = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)) };
        var placeIcon = new TextBlock
        {
            Text = "\uE714", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 40, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        placeGrid.Children.Add(placeIcon);
        filmGrid.Children.Add(placeGrid);
        // 异步提取真实缩略图
        BeginExtractThumbnail(vp, thumbImage, placeGrid);

        // 下胶卷边
        var botFilm = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        Grid.SetRow(botFilm, 2);
        for (int k = 0; k < 12; k++)
            ((StackPanel)botFilm.Child).Children.Add(new Border
            {
                Width = 4, Height = 4, Margin = new Thickness(6, 1, 6, 1),
                Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44))
            });
        filmGrid.Children.Add(botFilm);

        outerFrame.Child = filmGrid;
        thumbArea.Children.Add(outerFrame);

        // 播放按钮覆盖层
        var playOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)),
            CornerRadius = new CornerRadius(4, 4, 0, 0), Opacity = 0
        };
        playOverlay.Child = new TextBlock
        {
            Text = "\uE768", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 32, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        thumbArea.Children.Add(playOverlay);
        stack.Children.Add(thumbArea);

        // 文件名
        stack.Children.Add(new TextBlock
        {
            Text = name, FontSize = 10,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 6)
        });

        // 悬停操作栏
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0
        };
        toolbar.Children.Add(VideoBtn("复制", () => CopyVideo(vp)));
        toolbar.Children.Add(VideoBtn("改名", () => RenameVideo(vp)));
        toolbar.Children.Add(VideoBtn("删除", () => DeleteVideo(vp)));
        stack.Children.Add(toolbar);

        card.Child = stack;

        // 选中遮罩
        var selOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x4A, 0x90, 0xE2)),
            BorderBrush = (Brush)FindResource("PrimaryBrush"),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(4),
            Visibility = _selectedFiles.Contains(vp) ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "\uE73E", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 28, Foreground = (Brush)FindResource("PrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        thumbArea.Children.Add(selOverlay);

        card.MouseEnter += (_, _) => { playOverlay.Opacity = 0.7; toolbar.Opacity = 1; };
        card.MouseLeave += (_, _) => { playOverlay.Opacity = 0; toolbar.Opacity = 0; };
        card.MouseLeftButtonDown += (_, _) =>
        {
            if (_multiSelectMode)
                ToggleVideoSelection(vp, selOverlay);
            else
                PlayVideoInline(vp);
        };

        return card;
    }

    private Button VideoBtn(string text, Action act)
    {
        var b = new Button
        {
            Content = text, FontSize = 10, Padding = new Thickness(5, 2, 5, 2),
            Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x33, 0x33, 0x33)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0)
        };
        b.Click += (_, _) => act();
        b.MouseEnter += (s, _) => ((Button)s).Background =
            new SolidColorBrush(Color.FromArgb(0xF0, 0x55, 0x55, 0x55));
        b.MouseLeave += (s, _) => ((Button)s).Background =
            new SolidColorBrush(Color.FromArgb(0xD0, 0x33, 0x33, 0x33));
        return b;
    }

    /// <summary>
    /// 后台提取视频首帧缩略图（MediaPlayer + 阻塞渲染）
    /// </summary>
    private async void BeginExtractThumbnail(string path, Image target, Grid placeholder)
    {
        try
        {
            var bmp = await Task.Run(() => ExtractThumbnailCore(path, 320, 180));
            if (bmp != null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    target.Source = bmp;
                    if (placeholder.Parent is Panel p)
                    {
                        p.Children.Remove(placeholder);
                        if (!p.Children.Contains(target))
                            p.Children.Add(target);
                    }
                });
            }
        }
        catch { }
    }

    private static BitmapSource? ExtractThumbnailCore(string path, int w, int h)
    {
        // 尝试多个时间点：10% -> 1% -> 50% -> 30%
        double[] seekRatios = { 0.10, 0.01, 0.50, 0.30 };
        foreach (var ratio in seekRatios)
        {
            var result = TryExtractAtPosition(path, w, h, ratio);
            if (result != null) return result;
        }
        return null;
    }

    private static BitmapSource? TryExtractAtPosition(string path, int w, int h, double ratio)
    {
        System.Windows.Media.MediaPlayer? player = null;
        try
        {
            player = new System.Windows.Media.MediaPlayer
            {
                ScrubbingEnabled = true,
                Volume = 0
            };
            player.Open(new Uri(path));

            // 轮询等待解码器初始化（最多 5 秒）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!player.NaturalDuration.HasTimeSpan && sw.ElapsedMilliseconds < 5000)
                System.Threading.Thread.Sleep(100);

            if (!player.NaturalDuration.HasTimeSpan) return null;

            var dur = player.NaturalDuration.TimeSpan;
            if (dur.TotalSeconds < 0.1) return null;

            // 跳到指定比例位置
            var pos = TimeSpan.FromSeconds(dur.TotalSeconds * ratio);
            player.Position = pos;
            player.Pause();

            // 等待解码器渲染（增加时间）
            System.Threading.Thread.Sleep(600);

            // 检查是否成功定位
            if (Math.Abs((player.Position - pos).TotalSeconds) > 1.0)
                return null;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawVideo(player, new Rect(0, 0, w, h));

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            // 检查是否获取到有效帧（简单像素检测）
            if (rtb.Width > 1 && rtb.Height > 1 && !IsFrameAllBlack(rtb))
            {
                rtb.Freeze();
                return rtb;
            }
            return null;
        }
        catch { return null; }
        finally { player?.Close(); }
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
        try { FileService.DeleteFile(path); RefreshVideoGrid(); Toast("✓ 已删除"); }
        catch { Toast("✗ 删除失败"); }
    }

    /// <summary>
    /// 在内嵌 MediaElement 中播放视频
    /// </summary>
    private void PlayVideoInline(string path)
    {
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

        // 双击全屏（使用 WorkArea 避免覆盖任务栏）
        me.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                win.WindowStyle = WindowStyle.None;
                var wa = SystemParameters.WorkArea;
                win.Left = wa.Left; win.Top = wa.Top;
                win.Width = wa.Width; win.Height = wa.Height;
                win.ResizeMode = ResizeMode.NoResize;
            }
            else
                ppBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
        if (_currentNovel == null || _currentChapter == null) return;

        var config = FileService.LoadConfig(App.WorkRoot);
        if (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(config.ApiEndpoint))
        {
            Toast("⚠ 请先在「设置→AI 模型配置」中填入 API 地址和密钥");
            return;
        }

        var win = new Window
        {
            Title = "AI 生成视频",
            Width = 660, Height = 460,
            MinWidth = 520, MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.CanResize,
            Background = (Brush)FindResource("WindowBackgroundBrush")
        };

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 标题区域
        var headerStack = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        headerStack.Children.Add(new TextBlock
        {
            Text = $"AI 生成视频 · 第{_currentChapter.Index}章 {_currentChapter.Title}",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 2)
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = "描述主体、动作、场景、镜头运动、光照和视觉风格",
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush")
        });
        Grid.SetRow(headerStack, 0);
        grid.Children.Add(headerStack);

        // 提示词
        var promptBox = new TextBox
        {
            Text = "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13, FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10)
        };
        Grid.SetRow(promptBox, 2);
        grid.Children.Add(promptBox);

        // 参数变量（提前声明以便预设按钮引用）
        TextBox WidthBox = null!, HeightBox = null!, FramesBox = null!, FpsBox = null!;

        // 底部区域：分为上下两行
        var footerStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 8, 0, 0) };

        // 第1行：预设按钮 + 状态
        var topRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

        // 左侧预设按钮
        var presetPanel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, w, h, nf, fr) in new (string, int, int, int, int)[]
        {
            ("5s横屏", 1152, 768, 121, 24),
            ("3s横屏", 1152, 768, 81, 24),
            ("5s竖屏", 768, 1152, 121, 24),
            ("3s竖屏", 768, 1152, 81, 24),
            ("10s横屏", 1152, 768, 241, 24)
        })
        {
            var pb = new Button
            {
                Content = label, FontSize = 11, Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindResource("SecondaryButtonStyle"),
                Tag = (w, h, nf, fr)
            };
            pb.Click += (_, _) =>
            {
                var t = ((int, int, int, int))pb.Tag;
                WidthBox.Text = t.Item1.ToString();
                HeightBox.Text = t.Item2.ToString();
                FramesBox.Text = t.Item3.ToString();
                FpsBox.Text = t.Item4.ToString();
            };
            presetPanel.Children.Add(pb);
        }
        DockPanel.SetDock(presetPanel, Dock.Left);
        topRow.Children.Add(presetPanel);

        // 右侧状态文字
        var statusLabel = new TextBlock
        {
            Text = "", FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        topRow.Children.Add(statusLabel);
        footerStack.Children.Add(topRow);

        // 第2行：参数卡片 + 生成按钮（右对齐）
        var bottomRow = new DockPanel();

        // 右侧参数 + 按钮容器
        var rightPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        // 参数卡片
        var paramCard = new Border
        {
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 10, 0)
        };
        var paramRow = new StackPanel { Orientation = Orientation.Horizontal };
        var paramDefs = new[]
        {
            ("W", "1152", new Action<TextBox>(b => WidthBox = b)),
            ("H", "768", new Action<TextBox>(b => HeightBox = b)),
            ("帧数", "121", new Action<TextBox>(b => FramesBox = b)),
            ("FPS", "24", new Action<TextBox>(b => FpsBox = b))
        };
        foreach (var (label, def, boxRef) in paramDefs)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0) };
            sp.Children.Add(new TextBlock
            {
                Text = label, FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
            var box = new TextBox
            {
                Width = 44, FontSize = 12, Text = def, TextAlignment = TextAlignment.Center,
                Background = (Brush)FindResource("WindowBackgroundBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                BorderThickness = new Thickness(1),
                Height = 24, Padding = new Thickness(4, 0, 4, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            boxRef(box);
            sp.Children.Add(box);
            paramRow.Children.Add(sp);
        }
        paramCard.Child = paramRow;
        rightPanel.Children.Add(paramCard);

        var genBtn = new Button
        {
            Content = "🎬 生成视频",
            FontSize = 13, Padding = new Thickness(20, 6, 20, 6),
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        rightPanel.Children.Add(genBtn);
        DockPanel.SetDock(rightPanel, Dock.Right);
        bottomRow.Children.Add(rightPanel);

        footerStack.Children.Add(bottomRow);

        Grid.SetRow(footerStack, 4);
        grid.Children.Add(footerStack);

        win.Content = grid;

        var winCts = new CancellationTokenSource();
        win.Closed += (_, _) => { try { winCts.Cancel(); } catch { } };

        genBtn.Click += async (_, _) =>
        {
            var prompt = promptBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            { Toast("⚠ 请输入提示词"); return; }

            genBtn.IsEnabled = false;
            genBtn.Content = "⏳ 创建任务...";
            statusLabel.Text = "";
            var cts = CancellationTokenSource.CreateLinkedTokenSource(winCts.Token);

            try
            {
                int w = int.TryParse(WidthBox.Text, out var x) ? x : 1152;
                int h = int.TryParse(HeightBox.Text, out var y) ? y : 768;
                int nf = int.TryParse(FramesBox.Text, out var f) ? f : 121;
                int fr = int.TryParse(FpsBox.Text, out var r) ? r : 24;

                var videoId = await ApiService.CreateVideoTaskAsync(
                    config.ApiEndpoint, config.ApiKey, config.VideoModel, prompt, w, h, nf, fr, cts.Token);

                genBtn.Content = "⏳ 生成中...";

                var progress = new Progress<string>(msg =>
                {
                    try { statusLabel.Text = msg; } catch { }
                });

                var videoUrl = await ApiService.PollVideoResultAsync(
                    config.ApiEndpoint, config.ApiKey, videoId, progress, cts.Token);

                statusLabel.Text = "下载中...";
                genBtn.Content = "⏳ 下载中...";
                var videoBytes = await ApiService.DownloadVideoAsync(videoUrl, cts.Token);

                var targetDir = FileService.ChapterVideosPath(
                    App.WorkRoot, _currentNovel.MediaFolder, _currentChapter.FolderName);
                FileService.EnsureDirectory(targetDir);
                var fileName = $"AI_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
                var filePath = Path.Combine(targetDir, fileName);
                await File.WriteAllBytesAsync(filePath, videoBytes, cts.Token);

                RefreshVideoGrid();
                Toast("✓ 视频已生成并保存");
                try { win.Close(); } catch { }
            }
            catch (OperationCanceledException)
            {
                statusLabel.Text = "";
                Toast("⚠ 已取消");
            }
            catch (ApiException ex)
            {
                statusLabel.Text = "";
                Toast($"⚠ {ex.Message}");
            }
            catch (Exception ex)
            {
                statusLabel.Text = "";
                Toast($"⚠ {ex.Message}");
            }
            finally
            {
                try { cts.Dispose(); } catch { }
                genBtn.IsEnabled = true;
                genBtn.Content = "🎬 生成视频";
            }
        };

        win.ShowDialog();
        try { winCts.Dispose(); } catch { }
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
