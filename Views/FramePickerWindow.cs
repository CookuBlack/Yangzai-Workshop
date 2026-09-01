using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

/// <summary>
/// 视频精准选帧窗口：大画幅播放视频、拖动进度条或微调时间（逐帧/0.5s/1s/精确输入）精准定位；
/// 暂停时显示当前画面帧（不再黑屏）；底部提供「邻近帧胶片条」方便对比相近帧差异；
/// 可直接拖入新视频更换源。点击「取此帧」确认并返回该帧的 base64 与时间点，用于「首尾帧」生成。
/// </summary>
public sealed class FramePickerWindow : Window
{
    private readonly string _confirmLabel;
    private readonly IReadOnlyList<string> _videoAssets;

    /// <summary>确认选择的帧（Data URI Base64）；未确认返回 null。</summary>
    public string? FrameDataUrl { get; private set; }
    /// <summary>确认选择的帧所在时间点（秒）。</summary>
    public double FrameSeconds { get; private set; }

    private static readonly Brush _accent = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2));
    private static readonly Brush _normalBorder = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x46));

    // 播放与状态
    private MediaElement _media = null!;
    private Slider _slider = null!;
    private TextBlock _curLabel = null!;
    private TextBlock _durLabel = null!;
    private TextBlock _statusLabel = null!;
    private TextBlock _overlayHint = null!;
    private TextBlock _stripLabel = null!;
    private Image _previewImage = null!;
    private Image _frameOverlay = null!;
    private Button _ppBtn = null!;
    private ComboBox _fpsBox = null!;
    private TextBox _timeBox = null!;
    private DispatcherTimer _timer = null!;
    private ViewHelpers.VideoFrameExtractor? _fx;

    // 邻近帧胶片条
    private const int StripCount = 5;
    private readonly StripThumb[] _strip = new StripThumb[StripCount];

    private string _videoPath;
    private double _duration = 0;
    private double _fps = 30;
    private double _currentSeconds = 0;
    private string? _currentFrameDataUrl;
    private bool _isPlaying;
    private bool _isAtEnd;
    private bool _sliderDragging;
    private bool _wasPlayingBeforeDrag;
    private int _extractSeq;   // 当前帧提取序号（丢弃过期结果）
    private int _stripSeq;     // 胶片条提取序号（丢弃过期结果）

    public FramePickerWindow(string videoPath, string confirmLabel, IReadOnlyList<string>? videoAssets = null)
    {
        _videoPath = videoPath;
        _confirmLabel = confirmLabel;
        _videoAssets = videoAssets ?? Array.Empty<string>();

        Title = $"视频选帧 - {Path.GetFileName(videoPath)}";
        Width = 1180; Height = 820;
        MinWidth = 860; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Background = Br("WindowBackgroundBrush", Color.FromRgb(0x1E, 0x1E, 0x28));
        AllowDrop = true;

        Content = BuildUi();

        // 播放进度定时刷新
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) =>
        {
            if (_sliderDragging || !_media.NaturalDuration.HasTimeSpan) return;
            _slider.Value = _media.Position.TotalSeconds;
            _curLabel.Text = FormatPrecise(_media.Position);
        };
        _media.MediaEnded += (_, _) =>
        {
            _isAtEnd = true;
            Pause();
            _slider.Value = _slider.Maximum;
            _curLabel.Text = _durLabel.Text;
        };

        // 拖入新视频直接更换源
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                        && (e.Data.GetData(DataFormats.FileDrop) as string[])?.Any(ViewHelpers.IsVideoFile) == true
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += async (_, e) =>
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var video = files?.FirstOrDefault(ViewHelpers.IsVideoFile);
            if (video == null) return;
            await LoadVideoAsync(video);
            NotifyStatus("✓ 已更换视频：" + Path.GetFileName(video));
        };

        PreviewKeyDown += OnWindowKey;
        Closed += (_, _) => { _timer.Stop(); try { _media.Close(); } catch { } _fx?.Dispose(); _fx = null; };
        Loaded += async (_, _) => { await LoadVideoAsync(_videoPath); };
    }

    // ===== UI 构建 =====

    private FrameworkElement BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 标题
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 1 视频区
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 2 播放进度条
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 3 精准微调
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 4 帧预览 + 胶片条 + 确认
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 5 底部快捷键提示

        // ---- 0 标题 ----
        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(16, 10, 16, 10),
            BorderBrush = _normalBorder,
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 1 从资产库
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 3 打开视频
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 5 关闭
        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = $"🎬 精准选帧 · {_confirmLabel}",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Br("TextPrimaryBrush", Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 640
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(_videoPath),
            FontSize = 11, Foreground = Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB)),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 640
        });
        titleGrid.Children.Add(titleStack);

        var assetBtn = new Button
        {
            Content = "📁 资产库", FontSize = 11, Height = 26, Padding = new Thickness(10, 2, 10, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
            ToolTip = "从软件中已有的视频资产里选择要选帧的视频（也可直接把视频文件拖入窗口）",
            IsEnabled = _videoAssets.Count > 0
        };
        assetBtn.Click += async (_, _) =>
        {
            if (_videoAssets.Count == 0) { NotifyStatus("⚠ 当前项目没有可用的视频资产"); return; }
            var picker = new AssetPickerWindow(_videoAssets, "选择视频资产") { Owner = this };
            if (picker.ShowDialog() != true) return;
            var path = picker.SelectedPath;
            if (string.IsNullOrEmpty(path)) return;
            await LoadVideoAsync(path);
            NotifyStatus("✓ 已从资产库选择视频：" + Path.GetFileName(path));
        };
        Grid.SetColumn(assetBtn, 1);
        titleGrid.Children.Add(assetBtn);

        var openBtn = new Button
        {
            Content = "📂 打开视频", FontSize = 11, Height = 26, Padding = new Thickness(10, 2, 10, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
            ToolTip = "更换要选帧的视频（也可直接把视频文件拖入窗口）"
        };
        openBtn.Click += async (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv",
                Title = "打开要选帧的视频"
            };
            if (dlg.ShowDialog(this) != true) return;
            await LoadVideoAsync(dlg.FileName);
        };
        Grid.SetColumn(openBtn, 3);
        titleGrid.Children.Add(openBtn);

        var closeBtn = new Button
        {
            Content = "✕ 关闭 (Esc)", FontSize = 11, Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, Foreground = Br("TextPrimaryBrush", Colors.White),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        closeBtn.Click += (_, _) => DialogResult = false;
        Grid.SetColumn(closeBtn, 5);
        titleGrid.Children.Add(closeBtn);

        titleBar.Child = titleGrid;
        root.Children.Add(titleBar);

        // ---- 1 视频区（大画幅）----
        var videoBorder = new Border { Background = Brushes.Black, ClipToBounds = true };
        _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Stop,
            Stretch = Stretch.Uniform,
            ScrubbingEnabled = true,
            Volume = 1
        };
        // 帧覆盖层：暂停/未播放时立即显示当前提取帧（解决 MediaElement 未就绪时的黑屏等待）
        _frameOverlay = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _overlayHint = new TextBlock
        {
            Text = "正在加载视频…",
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00)),
            Padding = new Thickness(14, 7, 14, 7)
        };
        var dropHint = new TextBlock
        {
            Text = "🖱 可拖入视频文件直接更换",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 10, 8)
        };
        var overlay = new Grid();
        overlay.Children.Add(_media);
        overlay.Children.Add(_frameOverlay);
        overlay.Children.Add(_overlayHint);
        overlay.Children.Add(dropHint);
        videoBorder.Child = overlay;
        Grid.SetRow(videoBorder, 1);
        root.Children.Add(videoBorder);

        // ---- 2 播放进度条 ----
        var transportBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(16, 8, 16, 8),
            BorderBrush = _normalBorder,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        var tGrid = new Grid();
        tGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 0 播放
        tGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        tGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 2 当前时间
        tGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        tGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 4 进度条
        tGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        tGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 6 总时长

        _ppBtn = new Button
        {
            Content = "\uE768", FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18, Width = 34, Height = 34, Padding = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Br("TextPrimaryBrush", Colors.White),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            ToolTip = "播放 / 暂停（空格）"
        };
        _ppBtn.Click += (_, _) => TogglePlay();
        tGrid.Children.Add(_ppBtn);

        _curLabel = new TextBlock
        {
            Text = "00:00.000", FontSize = 13, FontFamily = new FontFamily("Consolas"),
            Foreground = Br("TextPrimaryBrush", Colors.White),
            VerticalAlignment = VerticalAlignment.Center, MinWidth = 74
        };
        Grid.SetColumn(_curLabel, 2);
        tGrid.Children.Add(_curLabel);

        _slider = new Slider
        {
            Minimum = 0, Maximum = 1, Value = 0,
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true
        };
        Grid.SetColumn(_slider, 4);
        tGrid.Children.Add(_slider);

        _durLabel = new TextBlock
        {
            Text = "00:00.000", FontSize = 13, FontFamily = new FontFamily("Consolas"),
            Foreground = Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB)),
            VerticalAlignment = VerticalAlignment.Center, MinWidth = 74
        };
        Grid.SetColumn(_durLabel, 6);
        tGrid.Children.Add(_durLabel);

        _slider.PreviewMouseLeftButtonDown += (_, _) =>
        {
            _sliderDragging = true;
            _wasPlayingBeforeDrag = _isPlaying;
            if (_isPlaying) Pause();
        };
        _slider.PreviewMouseLeftButtonUp += async (_, _) =>
        {
            _sliderDragging = false;
            await SeekAsync(_slider.Value);
            if (_wasPlayingBeforeDrag) Play();
        };
        _slider.ValueChanged += (_, _) =>
        {
            if (_sliderDragging) _curLabel.Text = FormatPrecise(TimeSpan.FromSeconds(_slider.Value));
        };

        transportBar.Child = tGrid;
        Grid.SetRow(transportBar, 2);
        root.Children.Add(transportBar);

        // ---- 3 精准微调 ----
        var fineBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(16, 8, 16, 8),
            BorderBrush = _normalBorder,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        var fineStack = new StackPanel { Orientation = Orientation.Horizontal };
        fineStack.Children.Add(MicroBtn("⏮", "后退 1 秒", () => Step(-1.0)));
        fineStack.Children.Add(MicroBtn("◀", "后退 0.5 秒", () => Step(-0.5)));
        fineStack.Children.Add(MicroBtn("◀◀", "后退 1 帧", () => Step(-1.0 / _fps)));
        fineStack.Children.Add(MicroBtn("▶▶", "前进 1 帧", () => Step(1.0 / _fps)));
        fineStack.Children.Add(MicroBtn("▶", "前进 0.5 秒", () => Step(0.5)));
        fineStack.Children.Add(MicroBtn("⏭", "前进 1 秒", () => Step(1.0)));

        fineStack.Children.Add(new TextBlock
        {
            Text = "  帧率", FontSize = 11,
            Foreground = Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0)
        });
        _fpsBox = new ComboBox
        {
            Width = 58, Height = 26, FontSize = 12,
            ItemsSource = new[] { "24", "25", "30", "60" },
            SelectedItem = "30",
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "视频帧率（逐帧微调与邻近帧按此计算），不确定时保持 30 即可",
            Style = (Style)Application.Current.FindResource("ModernComboBoxStyle"),
            Background = Br("WindowBackgroundBrush", Color.FromRgb(0x24, 0x24, 0x2E)),
            BorderBrush = _normalBorder,
            Foreground = Br("TextPrimaryBrush", Colors.White)
        };
        _fpsBox.SelectionChanged += async (_, _) =>
        {
            if (double.TryParse(_fpsBox.SelectedItem?.ToString(), out var f) && f > 0)
            {
                _fps = f;
                await RefreshStripAsync(_currentSeconds);
            }
        };
        fineStack.Children.Add(_fpsBox);

        fineStack.Children.Add(new TextBlock
        {
            Text = "  时间", FontSize = 11,
            Foreground = Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0)
        });
        _timeBox = new TextBox
        {
            Width = 96, Height = 26, FontSize = 12,
            Text = "00:00.000", TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Consolas"),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "输入 秒数 或 分:秒.毫秒（如 12.5 / 0:12.340），回车跳转",
            Background = Br("WindowBackgroundBrush", Color.FromRgb(0x24, 0x24, 0x2E)),
            BorderBrush = _normalBorder,
            BorderThickness = new Thickness(1),
            Foreground = Br("TextPrimaryBrush", Colors.White)
        };
        _timeBox.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await JumpToInputAsync();
        };
        fineStack.Children.Add(_timeBox);
        var jumpBtn = new Button
        {
            Content = "跳转", FontSize = 11, Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(4, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle")
        };
        jumpBtn.Click += async (_, _) => await JumpToInputAsync();
        fineStack.Children.Add(jumpBtn);

        fineBar.Child = fineStack;
        Grid.SetRow(fineBar, 3);
        root.Children.Add(fineBar);

        // ---- 4 帧预览 + 邻近帧胶片条 + 确认 ----
        var bottomBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(16, 10, 16, 10),
            BorderBrush = _normalBorder,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        var bottomGrid = new Grid();
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });          // 0 将提交帧预览
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2 胶片条+确认

        // 将提交的画面帧预览
        var previewBorder = new Border
        {
            Width = 272, Height = 153,
            Background = Brushes.Black,
            BorderBrush = _normalBorder,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            ClipToBounds = true
        };
        var previewGrid = new Grid();
        _previewImage = new Image { Stretch = Stretch.Uniform };
        previewGrid.Children.Add(_previewImage);
        var previewCap = new TextBlock
        {
            Text = "将提交的画面帧",
            FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 4),
            Background = new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0x00, 0x00)),
            Padding = new Thickness(8, 2, 8, 2)
        };
        previewGrid.Children.Add(previewCap);
        previewBorder.Child = previewGrid;
        bottomGrid.Children.Add(previewBorder);

        // 邻近帧胶片条 + 状态 + 确认
        var rightStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _stripLabel = new TextBlock
        {
            FontSize = 10.5,
            Foreground = Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB)),
            Margin = new Thickness(0, 0, 0, 4)
        };
        rightStack.Children.Add(_stripLabel);

        var stripHost = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        for (int i = 0; i < StripCount; i++)
        {
            var idx = i;
            var thumb = new StripThumb();
            var b = new Border
            {
                Width = 148, Height = 84, Margin = new Thickness(0, 0, 6, 0),
                Background = Brushes.Black,
                BorderBrush = i == 2 ? _accent : _normalBorder,
                BorderThickness = new Thickness(i == 2 ? 2.5 : 1),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand, ClipToBounds = true
            };
            thumb.Image = new Image { Stretch = Stretch.Uniform };
            thumb.Time = new TextBlock
            {
                FontSize = 9, Foreground = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 4, 2),
                Background = new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0x00, 0x00)),
                Padding = new Thickness(4, 0, 4, 0)
            };
            var g = new Grid();
            g.Children.Add(thumb.Image);
            g.Children.Add(thumb.Time);
            b.Child = g;
            b.MouseLeftButtonUp += (_, _) => _ = SeekAsync(_strip[idx].Seconds);
            thumb.Border = b;
            _strip[i] = thumb;
            stripHost.Children.Add(b);
        }
        var stripScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = stripHost };
        rightStack.Children.Add(stripScroll);

        var statusRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        _statusLabel = new TextBlock
        {
            FontSize = 11, Foreground = Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusRow.Children.Add(_statusLabel);
        var saveBtn = new Button
        {
            Content = "💾 保存该帧", FontSize = 12, Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
            ToolTip = "把当前预览的画面帧保存为图片到本地"
        };
        saveBtn.Click += (_, _) => SaveCurrentFrame();
        DockPanel.SetDock(saveBtn, Dock.Right);
        statusRow.Children.Add(saveBtn);
        var confirmBtn = new Button
        {
            Content = $"📌 {_confirmLabel}",
            FontSize = 14, Padding = new Thickness(24, 9, 24, 9),
            Style = (Style)Application.Current.FindResource("PrimaryButtonStyle"),
            ToolTip = "把当前预览的画面帧作为首尾帧提交"
        };
        confirmBtn.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(_currentFrameDataUrl))
            {
                NotifyStatus("⚠ 尚未取到画面帧，请先定位到有效时间点");
                return;
            }
            FrameDataUrl = _currentFrameDataUrl;
            FrameSeconds = _currentSeconds;
            DialogResult = true;
        };
        DockPanel.SetDock(confirmBtn, Dock.Right);
        statusRow.Children.Add(confirmBtn);
        rightStack.Children.Add(statusRow);

        Grid.SetColumn(rightStack, 2);
        bottomGrid.Children.Add(rightStack);
        bottomBar.Child = bottomGrid;
        Grid.SetRow(bottomBar, 4);
        root.Children.Add(bottomBar);

        // ---- 5 快捷键提示 ----
        var hintBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(16, 6, 16, 6),
            BorderBrush = _normalBorder,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        hintBar.Child = new TextBlock
        {
            Text = "空格 = 播放/暂停     ← / → = 逐帧     ↑ / ↓ = ±0.5 秒     Esc = 关闭",
            FontSize = 11, Foreground = Br("TextSecondaryBrush", Color.FromRgb(0x99, 0x99, 0xAA)),
            TextAlignment = TextAlignment.Center
        };
        Grid.SetRow(hintBar, 5);
        root.Children.Add(hintBar);

        return root;
    }

    private Button MicroBtn(string text, string tip, Action act)
    {
        var b = new Button
        {
            Content = text, FontSize = 12, Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand,
            ToolTip = tip,
            Background = Br("CardBackgroundBrush", Color.FromRgb(0x2B, 0x2B, 0x36)),
            Foreground = Br("TextPrimaryBrush", Colors.White),
            BorderBrush = _normalBorder,
            BorderThickness = new Thickness(1)
        };
        b.Click += (_, _) => act();
        return b;
    }

    // ===== 播放控制 =====

    private void Play()
    {
        if (_isAtEnd)
        {
            _media.Position = TimeSpan.Zero;
            _isAtEnd = false;
        }
        _frameOverlay.Visibility = Visibility.Collapsed;   // 播放时由 MediaElement 显示画面
        _media.Play();
        _isPlaying = true;
        _ppBtn.Content = "\uE769";
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void Pause()
    {
        try { _media.Pause(); } catch { }
        _isPlaying = false;
        _ppBtn.Content = "\uE768";
        _timer.Stop();
        // 暂停时切回帧覆盖层（已有画面帧则立即显示，不黑屏）
        if (_frameOverlay.Source != null) _frameOverlay.Visibility = Visibility.Visible;
    }

    private void TogglePlay()
    {
        if (_isPlaying) Pause();
        else Play();
    }

    /// <summary>暂停状态下强制渲染指定时间点的画面帧（避免黑屏，WPF 需短暂播放才能渲染定位帧）。</summary>
    private void ForceRenderFrame(TimeSpan pos)
    {
        try { _media.Position = pos; } catch { }
        if (_isPlaying || _duration <= 0) return;
        try { _media.Play(); } catch { }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (_isPlaying) return;
            try { _media.Pause(); } catch { }
        }));
    }

    // ===== 视频加载 =====

    private async Task LoadVideoAsync(string path)
    {
        _overlayHint.Visibility = Visibility.Visible;
        _overlayHint.Text = "正在加载视频…";
        try
        {
            _videoPath = path;
            Title = $"视频选帧 - {Path.GetFileName(path)}";
            _media.Source = new Uri(path);

            // 复用单个播放器：一次性读取时长，避免额外等待解码器初始化
            _fx?.Dispose();
            _fx = await Task.Run(() => ViewHelpers.VideoFrameExtractor.Open(path));
            if (_fx?.DurationSeconds is not { } d || d <= 0) { NotifyStatus("⚠ 无法读取视频时长"); return; }
            _duration = d;
            _slider.Maximum = d;
            _durLabel.Text = FormatPrecise(TimeSpan.FromSeconds(d));

            // 后台等待 MediaElement 就绪；画面帧直接用共享提取器快速取到，不再等待黑屏
            _ = WaitMediaAndShowAsync();

            NotifyStatus("已加载：拖动进度条或微调时间精准选帧");
            await ExtractAtAsync(0);
            await RefreshStripAsync(0);
        }
        catch (Exception ex)
        {
            _overlayHint.Text = "⚠ 视频加载失败";
            NotifyStatus($"⚠ {ex.Message}");
        }
    }

    /// <summary>UI 线程等待 MediaElement 就绪后隐藏加载提示并渲染首帧（不阻塞首帧提取）。</summary>
    private async Task WaitMediaAndShowAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(80);
            if (_media.NaturalDuration.HasTimeSpan) break;
        }
        _overlayHint.Visibility = Visibility.Collapsed;
        if (!_isPlaying) ForceRenderFrame(TimeSpan.Zero);
    }

    // ===== 定位与取帧 =====

    private async Task SeekAsync(double seconds)
    {
        var clamped = ClampSeconds(seconds);
        _currentSeconds = clamped;
        try { _media.Position = TimeSpan.FromSeconds(clamped); } catch { }
        _slider.Value = clamped;
        _curLabel.Text = FormatPrecise(TimeSpan.FromSeconds(clamped));
        _timeBox.Text = FormatPrecise(TimeSpan.FromSeconds(clamped));
        if (!_isPlaying) ForceRenderFrame(TimeSpan.FromSeconds(clamped));
        await ExtractAtAsync(clamped);
        await RefreshStripAsync(clamped);
    }

    private async void Step(double delta)
    {
        if (_duration <= 0) return;
        await SeekAsync(_currentSeconds + delta);
    }

    private async Task JumpToInputAsync()
    {
        if (_duration <= 0) return;
        var t = ParseTime(_timeBox.Text.Trim());
        if (t < 0) { NotifyStatus("⚠ 时间格式无效，请输入如 12.5 或 0:12.340"); return; }
        await SeekAsync(t);
    }

    private double ClampSeconds(double s) => Math.Clamp(s, 0, Math.Max(0, _duration - 0.03));

    /// <summary>后台提取指定时间点的帧，更新「将提交帧」预览（丢弃过期结果）。</summary>
    private async Task ExtractAtAsync(double seconds)
    {
        if (_duration <= 0 || _fx == null) return;
        var seq = ++_extractSeq;
        NotifyStatus("⏳ 正在提取画面帧…");
        var data = await Task.Run(() => _fx!.ExtractFrameToBase64(seconds));
        if (seq != _extractSeq) return;
        if (string.IsNullOrEmpty(data)) { NotifyStatus("⚠ 该时间点未能取到画面帧，请尝试其他位置"); return; }
        _currentFrameDataUrl = data;
        _currentSeconds = seconds;
        var img = ViewHelpers.DecodeBase64Image(data);
        if (img != null)
        {
            _previewImage.Source = img;
            // 未播放时同步刷新大画幅帧覆盖层，即时看到当前帧画面
            if (!_isPlaying)
            {
                _frameOverlay.Source = img;
                _frameOverlay.Visibility = Visibility.Visible;
            }
        }
        NotifyStatus($"✓ 已取得画面帧：{FormatPrecise(TimeSpan.FromSeconds(seconds))}（点击「{_confirmLabel}」确认提交）");
    }

    /// <summary>用共享播放器并行提取当前位置前后各 2 帧，刷新「邻近帧胶片条」。</summary>
    private async Task RefreshStripAsync(double center)
    {
        if (_duration <= 0 || _fx == null) return;
        var seq = ++_stripSeq;
        var frameDur = 1.0 / Math.Max(1, _fps);
        var times = new double[StripCount];
        for (int i = 0; i < StripCount; i++)
            times[i] = ClampSeconds(center + (i - 2) * frameDur);

        // 共享播放器内部加锁串行提取：打开一次后每帧仅需 160ms，比每次新建播放器快数倍
        var results = await Task.Run(() =>
        {
            var arr = new string?[StripCount];
            for (int i = 0; i < StripCount; i++)
                arr[i] = _fx!.ExtractFrameToBase64(times[i], maxEdge: 300);
            return arr;
        });
        if (seq != _stripSeq) return;

        for (int i = 0; i < StripCount; i++)
        {
            var s = _strip[i];
            var data = results[i];
            s.Seconds = times[i];
            s.Time.Text = FormatPrecise(TimeSpan.FromSeconds(times[i]));
            s.Image.Source = string.IsNullOrEmpty(data) ? null : ViewHelpers.DecodeBase64Image(data);
            bool isCenter = i == 2;
            s.Border.BorderBrush = isCenter ? _accent : _normalBorder;
            s.Border.BorderThickness = new Thickness(isCenter ? 2.5 : 1);
        }
        _stripLabel.Text = $"邻近帧（每格 {(int)Math.Round(frameDur * 1000)}ms，帧率 {_fps:0}）· 点击某格即可切换";
    }

    // ===== 键盘 =====

    private async void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; return; }
        if (_timeBox.IsKeyboardFocusWithin && e.Key != Key.Space) return;
        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true;
                TogglePlay();
                break;
            case Key.Left:
                e.Handled = true;
                await SeekAsync(_currentSeconds - 1.0 / _fps);
                break;
            case Key.Right:
                e.Handled = true;
                await SeekAsync(_currentSeconds + 1.0 / _fps);
                break;
            case Key.Up:
                e.Handled = true;
                await SeekAsync(_currentSeconds + 0.5);
                break;
            case Key.Down:
                e.Handled = true;
                await SeekAsync(_currentSeconds - 0.5);
                break;
        }
    }

    // ===== 工具 =====

    // ===== 保存当前帧 =====

    /// <summary>把当前取到的画面帧保存为 PNG/JPEG 图片到本地。</summary>
    private void SaveCurrentFrame()
    {
        if (string.IsNullOrEmpty(_currentFrameDataUrl))
        {
            NotifyStatus("⚠ 尚未取到画面帧，请先定位到有效时间点");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
            Title = "保存画面帧",
            FileName = $"{Path.GetFileNameWithoutExtension(_videoPath)}_帧_{FormatTimeFile(_currentSeconds)}.png"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var bmp = ViewHelpers.DecodeBase64Image(_currentFrameDataUrl);
            if (bmp == null) { NotifyStatus("⚠ 无法解码画面帧"); return; }
            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            BitmapEncoder encoder = ext == ".jpg" || ext == ".jpeg"
                ? new JpegBitmapEncoder { QualityLevel = 95 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = new FileStream(dlg.FileName, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
            NotifyStatus($"✓ 已保存画面帧到：{Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            NotifyStatus($"⚠ 保存失败：{ex.Message}");
        }
    }

    private void NotifyStatus(string msg) => _statusLabel.Text = msg;

    private static Brush Br(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is Brush b ? b : new SolidColorBrush(fallback);

    private static string FormatPrecise(TimeSpan t)
        => $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";

    private static string FormatTimeFile(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalMinutes:D2}_{t.Seconds:D2}_{t.Milliseconds:D3}";
    }

    private static double ParseTime(string s)
    {
        if (double.TryParse(s, out var total)) return total;
        var parts = s.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var m) && double.TryParse(parts[1], out var sec))
            return m * 60 + sec;
        if (parts.Length == 3 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var mn) && double.TryParse(parts[2], out var sc))
            return h * 3600 + mn * 60 + sc;
        return -1;
    }

    private sealed class StripThumb
    {
        public Border Border = null!;
        public Image Image = null!;
        public TextBlock Time = null!;
        public double Seconds;
    }
}
