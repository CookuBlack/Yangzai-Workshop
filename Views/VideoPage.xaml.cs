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

        // 关键：不设置 Owner！WPF 关闭 owned 子窗口时会激活/最小化 AllowsTransparency 主窗口。
        // 去掉 Owner 彻底切断 owned 关系，用 Topmost + 手动居中保持使用体验。
        var win = new Window
        {
            Title = "AI 生成视频",
            Width = 800, Height = 540,
            MinWidth = 700, MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Topmost = true,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.CanResize,
            Background = (Brush)FindResource("WindowBackgroundBrush")
        };
        ViewHelpers.CenterWindowOnOwner(win, Window.GetWindow(this));

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 标题区域（含优化按钮）
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
        grid.Children.Add(headerGrid);

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

        // 底部区域：分为上下两行
        var footerStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 8, 0, 0) };

        // 第1行：参考图（图生视频，可选）+ 状态
        var topRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

        var refPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        string? refImageData = null;
        var refBtn = new Button
        {
            Content = "🖼️ 参考图（可选）", FontSize = 11, Padding = new Thickness(10, 4, 10, 4),
            Style = (Style)FindResource("SecondaryButtonStyle"),
            ToolTip = "选择一张参考图片，作为生成视频的画面参考（图生视频）"
        };
        refPanel.Children.Add(refBtn);
        var assetRefBtn = new Button
        {
            Content = "📁 项目资产", FontSize = 11, Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButtonStyle"),
            ToolTip = "从当前项目的图片资产中选择参考图（章节图片 / 人物素材 / 封面 / 头像）"
        };
        refPanel.Children.Add(assetRefBtn);
        var refThumb = new Image
        {
            Width = 36, Height = 36, Stretch = Stretch.UniformToFill,
            Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed,
            SnapsToDevicePixels = true
        };
        var refClip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 36, 36), 4, 4);
        refThumb.Clip = refClip;
        refPanel.Children.Add(refThumb);
        var clearRefBtn = new Button
        {
            Content = "✕", FontSize = 11, Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed,
            Style = (Style)FindResource("SecondaryButtonStyle"),
            ToolTip = "清除参考图"
        };
        refPanel.Children.Add(clearRefBtn);

        // 应用参考图（文件路径 → base64 + 缩略图）
        void ApplyRefImage(string path)
        {
            var data = ViewHelpers.ImageToBase64DataUrl(path);
            if (data == null) { Toast("⚠ 参考图读取失败"); return; }
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 72;
                bmp.EndInit();
                refThumb.Source = bmp;
                refThumb.Visibility = Visibility.Visible;
                clearRefBtn.Visibility = Visibility.Visible;
                refImageData = data;
                Toast("✓ 已添加参考图");
            }
            catch { Toast("⚠ 参考图加载失败"); }
        }

        // 从本地文件选择参考图
        refBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif",
                Title = "选择参考图片"
            };
            // 显式绑定 owner 为 AI 小窗口，避免对话框关闭后激活主窗口触发其误最小化
            if (dlg.ShowDialog(win) != true) return;
            ApplyRefImage(dlg.FileName);
        };

        // 从项目资产中选择参考图（owner 传 AI 小窗口，避免模态选择器关闭时激活主窗口触发其误最小化）
        assetRefBtn.Click += (_, _) =>
        {
            try
            {
                if (_currentNovel == null) { Toast("⚠ 请先选择小说"); return; }
                var path = ViewHelpers.PickProjectImage(
                    win, "选择项目图片作为参考图",
                    App.WorkRoot, _currentNovel.Id, _currentNovel.MediaFolder);
                if (path == null) return;
                ApplyRefImage(path);
            }
            catch (Exception ex)
            {
                Toast($"⚠ 无法打开项目资产：{ex.Message}");
            }
        };

        clearRefBtn.Click += (_, _) =>
        {
            refImageData = null;
            refThumb.Source = null;
            refThumb.Visibility = Visibility.Collapsed;
            clearRefBtn.Visibility = Visibility.Collapsed;
        };
        DockPanel.SetDock(refPanel, Dock.Left);
        topRow.Children.Add(refPanel);

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

        // 参数卡片：分辨率档位 + 横竖屏 + 时长滑动条 + FPS
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

        // 分辨率档位（480P / 720P / 1080P / 2K）
        paramRow.Children.Add(new TextBlock
        {
            Text = "画质", FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });
        var levelBox = new ComboBox
        {
            Width = 74, Height = 26, FontSize = 12,
            ItemsSource = ViewHelpers.VideoLevels,
            SelectedItem = "720P",
            ToolTip = "分辨率档位：480P / 720P / 1080P（2K 超出接口支持范围，将按 1080P 输出）",
            Style = (Style)Application.Current.FindResource("ModernComboBoxStyle"),
            Background = (Brush)FindResource("WindowBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Padding = new Thickness(6, 0, 6, 0)
        };
        paramRow.Children.Add(levelBox);

        // 比例（16:9 / 9:16 / 1:1 / 4:3 / 3:4）
        paramRow.Children.Add(new TextBlock
        {
            Text = "比例", FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0)
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
        paramRow.Children.Add(ratioBox);

        // 时长滑动条（上限随 比例+分辨率 联动：1:1 可达 18s，1080P 横竖屏仅 5s 等）
        paramRow.Children.Add(new TextBlock
        {
            Text = "时长", FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0)
        });
        var secSlider = new Slider { Minimum = 1, Maximum = 18, Value = 5, Width = 90, VerticalAlignment = VerticalAlignment.Center };
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
            Margin = new Thickness(4, 0, 0, 0)
        };
        paramRow.Children.Add(secBox);
        paramRow.Children.Add(new TextBlock
        {
            Text = "秒", FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        });

        // 当前允许的最大秒数（随 比例+分辨率 联动）
        int maxSec = 18;
        bool syncingSec = false;

        // 比例/分辨率变化时，更新时长滑块上限并钳制当前值
        void UpdateMaxSeconds()
        {
            var lv = levelBox.SelectedItem?.ToString() ?? "720P";
            var rt = ratioBox.SelectedItem?.ToString() ?? "16:9";
            maxSec = ViewHelpers.CalcVideoMaxSeconds(lv, rt);
            secSlider.Maximum = maxSec;
            if (secSlider.Value > maxSec) secSlider.Value = maxSec;
            if (secBox.IsKeyboardFocusWithin == false && int.TryParse(secBox.Text.Trim(), out var cur) && cur > maxSec)
            {
                syncingSec = true;
                secBox.Text = maxSec.ToString();
                syncingSec = false;
            }
            secSlider.ToolTip = $"时长上限：{maxSec} 秒（1:1 比例可达 18s，非 1:1 受分辨率限制）";
        }
        levelBox.SelectionChanged += (_, _) => UpdateMaxSeconds();
        ratioBox.SelectionChanged += (_, _) => UpdateMaxSeconds();

        // 秒数滑动条与输入框双向同步（输入超出 1~maxSec 自动钳制）
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
            if (!double.TryParse(secBox.Text.Trim(), out var v) || v < 1) return;
            syncingSec = true;
            secSlider.Value = Math.Clamp(v, 1, maxSec);
            syncingSec = false;
        };
        secBox.LostFocus += (_, _) =>
        {
            if (!double.TryParse(secBox.Text.Trim(), out var v)) { secBox.Text = "5"; return; }
            secBox.Text = ((int)Math.Clamp(Math.Round(v), 1, maxSec)).ToString();
        };

        // FPS（帧数 = 秒数 × FPS 自动计算）
        paramRow.Children.Add(new TextBlock
        {
            Text = "FPS", FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0)
        });
        var fpsBox = new TextBox
        {
            Width = 34, Height = 24, FontSize = 12, Text = "24",
            TextAlignment = TextAlignment.Center,
            Background = (Brush)FindResource("WindowBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 0, 2, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        paramRow.Children.Add(fpsBox);

        paramCard.Child = paramRow;
        rightPanel.Children.Add(paramCard);

        var queueBtn = new Button
        {
            Content = "📋 查看队列",
            FontSize = 12, Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)FindResource("SecondaryButtonStyle")
        };
        queueBtn.Click += (_, _) => OpenQueueWindow();
        rightPanel.Children.Add(queueBtn);

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
                // 是否有参考图：无=文生视频（仅依据文本优化），有=图生视频（依据文本 + 图像内容优化）
                bool hasRef = !string.IsNullOrWhiteSpace(refImageData);

                var sys = "你是一位专业的 AI 视频生成提示词优化师。"
                    + (hasRef
                        ? "用户提供了参考图，请仔细观察参考图的画面内容（主体、动作、场景、构图、色调与光影），"
                          + "并结合用户文本，扩展为一段详细、专业的视频生成提示词，使生成的视频画面与参考图风格统一、运动自然。"
                          + "要求：1. 准确提炼参考图中的主体特征、构图与色调并融入提示词 2. 若文本与参考图冲突，以文本意图为主、参考图风格为辅 "
                          + "3. 添加镜头描述（如特写、全景、跟踪镜头） 4. 描述光影和色彩氛围 "
                          + "5. 丰富动作和场景细节 6. 使用流畅的英文或中英混合（英文术语更准确）"
                          + "7. 保持原意的同时让画面更具电影质感 8. 只输出优化后的提示词，不要任何解释。"
                        : "请根据用户提供的简短提示词，扩展为一段详细、专业的视频生成提示词。"
                          + "要求：1. 添加镜头描述（如特写、全景、跟踪镜头） 2. 描述光影和色彩氛围 "
                          + "3. 丰富动作和场景细节 4. 使用流畅的英文或中英混合（英文术语更准确）"
                          + "5. 保持原意的同时让画面更具电影质感 6. 只输出优化后的提示词，不要任何解释。");

                // 有参考图时，将参考图作为视觉输入一起交给模型；否则仅用文本
                var result = hasRef
                    ? await ApiService.ChatWithImagesAsync(
                        config.ApiEndpoint, config.ApiKey, config.ApiModel,
                        sys, $"请结合参考图优化以下视频生成提示词：\n{rawPrompt}",
                        new[] { refImageData! })
                    : await ApiService.ChatAsync(
                        config.ApiEndpoint, config.ApiKey, config.ApiModel,
                        sys, $"请优化以下视频生成提示词：\n{rawPrompt}");

                if (!string.IsNullOrWhiteSpace(result))
                {
                    promptBox.Text = result.Trim();
                    Toast(hasRef ? "✓ 提示词已结合参考图优化" : "✓ 提示词已优化");
                }
            }
            catch (ApiException ex)
            {
                Toast($"⚠ {ex.Message}");
            }
            catch (Exception ex)
            {
                Toast($"⚠ 优化失败：{ex.Message}");
            }
            finally
            {
                optimizeBtn.IsEnabled = true;
                optimizeBtn.Content = "✨ 优化提示词";
            }
        };

        win.Content = grid;

        // 生成按钮：创建任务并入队后立即关闭窗口，生成交给后台队列串行执行
        genBtn.Click += (_, _) =>
        {
            var prompt = promptBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            { Toast("⚠ 请输入提示词"); return; }

            var level = levelBox.SelectedItem?.ToString() ?? "720P";
            var ratio = ratioBox.SelectedItem?.ToString() ?? "16:9";
            var (w, h) = ViewHelpers.CalcVideoSize(level, ratio);
            var seconds = (int)Math.Clamp(Math.Round(secSlider.Value), 1, ViewHelpers.CalcVideoMaxSeconds(level, ratio));
            var fps = int.TryParse(fpsBox.Text.Trim(), out var r) ? Math.Clamp(r, 8, 60) : 24;
            var frames = seconds * fps;
            // 2K 超出接口支持范围，实际按 1080P 输出，队列详情如实标注
            var displayLevel = level == "2K" ? "2K(1080P)" : level;

            // 快照当前小说/章节，防止用户切换后任务保存到错误目录
            var novel = _currentNovel;
            var chapter = _currentChapter;
            var task = new AiTask
            {
                Type = AiTaskType.Video,
                Prompt = prompt,
                Detail = $"{displayLevel}·{ratio}·{seconds}s·{fps}fps",
                ApiEndpoint = config.ApiEndpoint,
                ApiKey = config.ApiKey,
                Model = config.VideoModel,
                TargetDir = FileService.ChapterVideosPath(App.WorkRoot, novel.MediaFolder, chapter.FolderName),
                FileNameBase = $"AI_{DateTime.Now:yyyyMMdd_HHmmss}",
                VideoWidth = w, VideoHeight = h, VideoFrames = frames, VideoFps = fps,
                VideoSeconds = seconds,
                ReferenceImageData = refImageData,
                NovelName = novel.Name,
                ScopeName = $"第{chapter.Index}章 {chapter.Title}"
            };
            AiTaskManager.Enqueue(task);
            Toast("✓ 已加入 AI 任务队列");
            try { win.Close(); } catch { }
        };

        // 注册到浮动窗口管理器：最小化时自动隐藏，可通过快捷键恢复
        FloatingWindowManager.Instance.Register(win);

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
