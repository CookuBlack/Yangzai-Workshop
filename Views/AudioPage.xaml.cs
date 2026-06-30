using System.IO;
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

public partial class AudioPage : UserControl
{
    private List<NovelInfo> _novels = new();
    private NovelInfo? _currentNovel;
    private List<Chapter> _chapters = new();
    private Chapter? _currentChapter;
    private bool _isGlobalAudio;

    public AudioPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void RefreshContent()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var curNovelId = _currentNovel?.Id;
            var curChapterIndex = _currentChapter != null
                ? _chapters.IndexOf(_currentChapter) : -1;
            var wasGlobal = _isGlobalAudio;
            RefreshNovels();
            if (curNovelId != null)
            {
                var novel = _novels.FirstOrDefault(n => n.Id == curNovelId);
                if (novel != null)
                {
                    SelectNovel(novel);
                    if (wasGlobal)
                        SelectGlobalAudio();
                    else if (curChapterIndex >= 0 && curChapterIndex < _chapters.Count)
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

    // ===== 小说 =====
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
                    using var ms = new MemoryStream(data);
                    bmp.StreamSource = ms;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 140; bmp.EndInit();
                    coverBorder.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                }
                catch { coverBorder.Child = CoverFb(novel.Name); }
            }
            else coverBorder.Child = CoverFb(novel.Name);
            stack.Children.Add(coverBorder);

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

    // ===== 章节 =====
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
            SelectChapter(_chapters[0]);
        else
            SelectGlobalAudio();
    }

    private void GlobalAudioBtn_Click(object sender, RoutedEventArgs e) => SelectGlobalAudio();

    private void SelectGlobalAudio()
    {
        _isGlobalAudio = true;
        _currentChapter = null;
        GlobalAudioBtn.Style = (Style)FindResource("PrimaryButtonStyle");
        HighlightTabButton(null); // 清除所有章节按钮高亮
        RefreshAudioGrid();
    }

    private void SelectChapter(Chapter chapter)
    {
        _isGlobalAudio = false;
        _currentChapter = chapter;
        GlobalAudioBtn.Style = (Style)FindResource("SecondaryButtonStyle");
        HighlightTabButton(chapter);
        RefreshAudioGrid();
        var tab = ChapterTabsPanel.Children.OfType<Button>()
            .FirstOrDefault(b => b.Tag is Chapter cc && cc == chapter);
        tab?.BringIntoView();
    }

    private void HighlightTabButton(object? tag)
    {
        foreach (Button btn in ChapterTabsPanel.Children.OfType<Button>())
        {
            bool sel = tag != null && btn.Tag != null && btn.Tag.Equals(tag);
            btn.Background = sel ? (Brush)FindResource("PrimaryBrush") : Brushes.Transparent;
            btn.Foreground = sel ? Brushes.White : (Brush)FindResource("TextPrimaryBrush");
        }
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
            bool sel = ch == _currentChapter && !_isGlobalAudio;
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

    // ===== 音频卡片网格 =====
    private void RefreshAudioGrid()
    {
        AudioGrid.Children.Clear();

        if (_currentNovel == null)
        {
            AudioGrid.Children.Add(PlaceholderText("请先选择小说"));
            return;
        }

        string path;
        if (_isGlobalAudio)
        {
            path = FileService.NovelGlobalAudioPath(App.WorkRoot, _currentNovel.MediaFolder);
        }
        else
        {
            if (_currentChapter == null)
            {
                AudioGrid.Children.Add(PlaceholderText("暂无章节\n请先选择有章节的小说"));
                return;
            }
            path = FileService.ChapterAudiosPath(
                App.WorkRoot, _currentNovel.MediaFolder, _currentChapter.FolderName);
        }

        FileService.EnsureDirectory(path);
        var audios = FileService.GetFiles(path, ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".wma", ".aac");

        if (audios.Count == 0)
        {
            AudioGrid.Children.Add(PlaceholderText("暂无音频素材\n拖拽音频文件或点击下方按钮导入"));
            return;
        }

        foreach (var audioFile in audios)
            AudioGrid.Children.Add(CreateAudioCard(audioFile));
    }

    private static TextBlock PlaceholderText(string msg) => new()
    {
        Text = msg, FontSize = 13, TextAlignment = TextAlignment.Center,
        Foreground = Application.Current.TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray,
        Margin = new Thickness(20, 30, 20, 30), HorizontalAlignment = HorizontalAlignment.Center
    };

    private Border CreateAudioCard(string audioPath)
    {
        string name = Path.GetFileName(audioPath);
        string ext = Path.GetExtension(audioPath).ToUpper().TrimStart('.');
        string sizeStr = FormatFileSize(new FileInfo(audioPath).Length);

        var card = new Border
        {
            Height = 56,
            Style = (Style)FindResource("ListItemCardStyle"),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand, Tag = audioPath,
            Background = (Brush)FindResource("CardBackgroundBrush")
        };

        var grid = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 播放按钮（使用 Path 三角形确保视觉居中）
        var playIcon = new System.Windows.Shapes.Path
        {
            Fill = Brushes.White,
            Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M 0,0 L 10,5 L 0,10 Z"),
            Width = 16, Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var playBtn = new Button
        {
            Width = 36, Height = 36, Padding = new Thickness(0),
            Background = (Brush)FindResource("PrimaryBrush"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, ToolTip = "播放",
            Content = playIcon
        };
        playBtn.Click += (_, _) => PlayAudio(audioPath);
        grid.Children.Add(playBtn);

        // 文件名 + 信息
        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(infoStack, 2);
        infoStack.Children.Add(new TextBlock
        {
            Text = name, FontSize = 13, FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        infoStack.Children.Add(new TextBlock
        {
            Text = $"{ext} · {sizeStr}", FontSize = 10,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 2, 0, 0)
        });
        grid.Children.Add(infoStack);

        // 操作按钮
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(AudioActionBtn("复制", "复制", () => CopyAudio(audioPath)));
        actions.Children.Add(AudioActionBtn("改名", "改名", () => RenameAudio(audioPath)));
        actions.Children.Add(AudioActionBtn("删除", "删除", () => DeleteAudio(audioPath)));
        Grid.SetColumn(actions, 4);
        grid.Children.Add(actions);

        card.Child = grid;
        card.MouseLeftButtonDown += (s, _) => PlayAudio(audioPath);

        return card;
    }

    private Button AudioActionBtn(string label, string tooltip, Action act)
    {
        var b = new Button
        {
            Content = label, FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 11, Width = 40, Height = 26, Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            BorderThickness = new Thickness(0),
            ToolTip = tooltip
        };
        b.Click += (_, _) => act();
        return b;
    }

    private void CopyAudio(string path)
    {
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new string[] { path });
            Clipboard.SetDataObject(data);
            Toast("✓ 已复制");
        }
        catch { Toast("✗ 复制失败"); }
    }

    private void RenameAudio(string path)
    {
        var dlg = new InputDialog("重命名音频", "名称（不含扩展名）：",
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
            RefreshAudioGrid();
        };
        dlg.Show();
    }

    private void DeleteAudio(string path)
    {
        try { FileService.DeleteFile(path); RefreshAudioGrid(); Toast("✓ 已删除"); }
        catch { Toast("✗ 删除失败"); }
    }

    private void PlayAudio(string path)
    {
        var name = Path.GetFileName(path);
        var win = new Window
        {
            Title = $"▶ {name}",
            Width = 520, Height = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            Background = (Brush)FindResource("WindowBackgroundBrush"),
            ResizeMode = ResizeMode.CanResizeWithGrip,
            MinWidth = 380, MinHeight = 180
        };

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 文件名
        root.Children.Add(new TextBlock
        {
            Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // 进度条
        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Value = 0,
            IsMoveToPointEnabled = true
        };
        Grid.SetRow(slider, 2);
        root.Children.Add(slider);

        // 控制栏（卡片容器，居中按钮组）
        var controlBar = new Border
        {
            Background = (Brush)FindResource("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(controlBar, 4);
        root.Children.Add(controlBar);

        var controlStack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
        controlBar.Child = controlStack;

        // 按钮行
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var rwBtn = MakeBtn("⏪", "后退10秒", false);
        var ppBtn = MakeBtn("▶", "播放/暂停", true);
        var ffBtn = MakeBtn("⏩", "前进10秒", false);
        btnPanel.Children.Add(rwBtn);
        btnPanel.Children.Add(ppBtn);
        btnPanel.Children.Add(ffBtn);
        controlStack.Children.Add(btnPanel);

        // 时间标签
        var timeLabel = new TextBlock
        {
            Text = "00:00 / 00:00", FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };
        controlStack.Children.Add(timeLabel);

        // 底部提示
        var tip = new TextBlock
        {
            Text = "空格键 播放/暂停  ·  Esc 关闭",
            FontSize = 11, Foreground = (Brush)FindResource("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(tip, 5);
        root.Children.Add(tip);

        // 音频播放器（1x1 隐藏，确保在视觉树中）
        var me = new MediaElement
        {
            Source = new Uri(path),
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Stop,
            Volume = 1, Width = 1, Height = 1,
            Visibility = Visibility.Hidden
        };
        Grid.SetRow(me, 5);
        root.Children.Add(me);

        win.Content = root;

        bool isPlaying = false, sliderDragging = false;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        me.MediaOpened += (_, _) =>
        {
            if (me.NaturalDuration.HasTimeSpan)
            {
                slider.Maximum = me.NaturalDuration.TimeSpan.TotalSeconds;
                timeLabel.Text = $"00:00 / {FormatTime(me.NaturalDuration.TimeSpan)}";
            }
            me.Play(); ppBtn.Content = "⏸"; isPlaying = true;
            if (!timer.IsEnabled) timer.Start();
        };
        me.MediaEnded += (_, _) =>
        {
            isPlaying = false; ppBtn.Content = "▶"; me.Stop(); me.Position = TimeSpan.Zero;
            slider.Value = 0; timer.Stop();
            timeLabel.Text = me.NaturalDuration.HasTimeSpan
                ? $"00:00 / {FormatTime(me.NaturalDuration.TimeSpan)}" : "00:00 / 00:00";
        };
        timer.Tick += (_, _) =>
        {
            if (!sliderDragging && isPlaying && me.NaturalDuration.HasTimeSpan)
            { slider.Value = me.Position.TotalSeconds; timeLabel.Text = $"{FormatTime(me.Position)} / {FormatTime(me.NaturalDuration.TimeSpan)}"; }
        };
        ppBtn.Click += (_, _) =>
        {
            if (isPlaying) { me.Pause(); ppBtn.Content = "▶"; } else { me.Play(); ppBtn.Content = "⏸"; if (!timer.IsEnabled) timer.Start(); }
            isPlaying = !isPlaying;
        };
        rwBtn.Click += (_, _) => { var p = me.Position - TimeSpan.FromSeconds(10); me.Position = p > TimeSpan.Zero ? p : TimeSpan.Zero; };
        ffBtn.Click += (_, _) =>
        {
            if (me.NaturalDuration.HasTimeSpan)
            { var p = me.Position + TimeSpan.FromSeconds(10); me.Position = p < me.NaturalDuration.TimeSpan ? p : me.NaturalDuration.TimeSpan; }
        };
        slider.PreviewMouseDown += (_, _) => sliderDragging = true;
        slider.PreviewMouseUp += (_, _) => { me.Position = TimeSpan.FromSeconds(slider.Value); sliderDragging = false; };
        slider.ValueChanged += (s, _) =>
        {
            if (sliderDragging && me.NaturalDuration.HasTimeSpan)
                timeLabel.Text = $"{FormatTime(TimeSpan.FromSeconds(slider.Value))} / {FormatTime(me.NaturalDuration.TimeSpan)}";
        };
        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) win.Close();
            if (e.Key == Key.Space) { ppBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
        };
        win.Closed += (_, _) => { timer.Stop(); me.Stop(); me.Close(); };
        win.Show();
    }

    private Button MakeBtn(string text, string tip, bool primary) => new()
    {
        Content = new TextBlock
        {
            Text = text,
            FontSize = primary ? 18 : 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = primary ? new Thickness(2, 0, 0, 0) : new Thickness(0)
        },
        Width = primary ? 50 : 40, Height = primary ? 50 : 40,
        Padding = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = primary ? (Brush)FindResource("PrimaryBrush") : Brushes.Transparent,
        Foreground = primary ? Brushes.White : (Brush)FindResource("TextPrimaryBrush"),
        BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        ToolTip = tip,
        Margin = primary ? new Thickness(8, 0, 8, 0) : new Thickness(0)
    };

    private static string FormatTime(TimeSpan t)
    {
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    // ===== 导入 + 拖拽 =====
    private void ImportAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null) return;
        if (!_isGlobalAudio && _currentChapter == null) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "音频文件|*.mp3;*.wav;*.flac;*.m4a;*.ogg;*.wma;*.aac",
            Multiselect = true, Title = "选择要导入的音频文件"
        };
        if (dlg.ShowDialog() == true)
        {
            var targetDir = _isGlobalAudio
                ? FileService.NovelGlobalAudioPath(App.WorkRoot, _currentNovel.MediaFolder)
                : FileService.ChapterAudiosPath(App.WorkRoot, _currentNovel.MediaFolder, _currentChapter!.FolderName);
            foreach (var file in dlg.FileNames)
                FileService.CopyFile(file, targetDir);
            RefreshAudioGrid();
            Toast("✓ 已导入");
        }
    }

    private void AudioGrid_Drop(object sender, DragEventArgs e)
    {
        if (_currentNovel == null) return;
        if (!_isGlobalAudio && _currentChapter == null) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var targetDir = _isGlobalAudio
                ? FileService.NovelGlobalAudioPath(App.WorkRoot, _currentNovel.MediaFolder)
                : FileService.ChapterAudiosPath(App.WorkRoot, _currentNovel.MediaFolder, _currentChapter!.FolderName);
            var audioExts = new HashSet<string> { ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".wma", ".aac" };
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLower();
                if (audioExts.Contains(ext))
                    FileService.CopyFile(file, targetDir);
            }
            RefreshAudioGrid();
        }
    }

    private void AudioGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void AudioScroller_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
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
        catch { }
    }
}
