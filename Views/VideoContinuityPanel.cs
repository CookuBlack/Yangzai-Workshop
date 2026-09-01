using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

/// <summary>
/// AI 视频生成窗口的「视频连贯（首尾帧）」面板：
/// 提供两个帧位——首帧（A 段视频尾帧）与尾帧（B 段视频首帧）。
/// 支持「上传视频自动提取」与「手动选帧」两种方式，预览帧图并回传 base64 供生成提交。
/// </summary>
public sealed class VideoContinuityPanel
{
    private readonly Window _owner;
    private readonly IReadOnlyList<string> _videoAssets;
    private readonly FrameSlot _first = new();
    private readonly FrameSlot _last = new();
    private readonly Button _toggleBtn = null!;
    private readonly Grid _slotsHost = null!;
    private readonly TextBlock _badge = null!;

    /// <summary>首帧 base64 Data URL（未设置为 null）</summary>
    public string? FirstFrameDataUrl => _first.DataUrl;
    /// <summary>尾帧 base64 Data URL（未设置为 null）</summary>
    public string? LastFrameDataUrl => _last.DataUrl;
    /// <summary>是否已设置任意一帧</summary>
    public bool HasAnyFrame => _first.DataUrl != null || _last.DataUrl != null;

    /// <summary>面板内容变化（选帧/清除）时触发</summary>
    public event Action? Changed;
    /// <summary>提示气泡（由宿主窗口接入其 Toast）</summary>
    public Action<string>? Notify { get; set; }

    public FrameworkElement Root { get; }

    public VideoContinuityPanel(Window owner) : this(owner, Array.Empty<string>(), false) { }

    /// <param name="videoAssets">软件中已有的视频资产路径列表（用于「从资产库选择」），可为空。</param>
    /// <param name="startCollapsed">true=面板默认收起（初始只显示标题行，更扁，为提示词省出空间）。</param>
    public VideoContinuityPanel(Window owner, IReadOnlyList<string> videoAssets, bool startCollapsed = false)
    {
        _owner = owner;
        _videoAssets = videoAssets ?? Array.Empty<string>();

        var card = new Border
        {
            Background = Br("CardBackgroundBrush", Color.FromRgb(0x24, 0x24, 0x2E)),
            BorderBrush = Br("BorderBrush", Color.FromRgb(0x3A, 0x3A, 0x46)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 10),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 标题
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });                    // 1 间距
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 2 帧位区
        var gapRow = rootGrid.RowDefinitions[1];

        // ---- 标题行 ----
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = "🔗 视频连贯（首尾帧）",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Br("TextPrimaryBrush", Colors.White),
            VerticalAlignment = VerticalAlignment.Center
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "  上传两段视频，自动取 A 的尾帧作首帧、B 的首帧作尾帧，或手动精准选帧，让两段视频无缝衔接",
            FontSize = 10.5,
            Foreground = Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        header.Children.Add(titleStack);

        _badge = new TextBlock
        {
            FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
            Foreground = Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB))
        };
        Grid.SetColumn(_badge, 1);
        header.Children.Add(_badge);

        _toggleBtn = ViewHelpers.BuildAccentToggleButton("展开 / 收起面板");
        _toggleBtn.Content = "▾";
        Grid.SetColumn(_toggleBtn, 2);
        header.Children.Add(_toggleBtn);

        rootGrid.Children.Add(header);

        // ---- 帧位区（首帧 / 尾帧两列） ----
        _slotsHost = new Grid();
        _slotsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _slotsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        _slotsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _first.Role = "首帧";
        var firstCard = BuildSlotCard(_first, "上一段视频（A）的尾帧",
            "📤 上传 A 段视频 · 自动取尾帧", "选择第一段视频，软件自动提取其尾帧作为首帧");
        _slotsHost.Children.Add(firstCard);

        _last.Role = "尾帧";
        var lastCard = BuildSlotCard(_last, "下一段视频（B）的首帧",
            "📤 上传 B 段视频 · 自动取首帧", "选择第二段视频，软件自动提取其首帧作为尾帧");
        Grid.SetColumn(lastCard, 2);
        _slotsHost.Children.Add(lastCard);

        // 首帧：上传 A → 取尾帧；尾帧：上传 B → 取首帧
        _first.AutoBtn.Click += async (_, _) => await AutoExtractAsync(_first, tail: true);
        _last.AutoBtn.Click += async (_, _) => await AutoExtractAsync(_last, tail: false);
        _first.AssetBtn.Click += async (_, _) => await PickFromAssetsAsync(_first, tail: true);
        _last.AssetBtn.Click += async (_, _) => await PickFromAssetsAsync(_last, tail: false);
        _first.ManualBtn.Click += async (_, _) => await ManualPickAsync(_first, "取此帧作为首帧");
        _last.ManualBtn.Click += async (_, _) => await ManualPickAsync(_last, "取此帧作为尾帧");
        _first.ClearBtn.Click += (_, _) => ClearSlot(_first);
        _last.ClearBtn.Click += (_, _) => ClearSlot(_last);

        _toggleBtn.Click += (_, _) =>
        {
            bool expanded = _slotsHost.Visibility == Visibility.Visible;
            _slotsHost.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            _toggleBtn.Content = expanded ? "▸" : "▾";
            // 收起时更扁：隐藏间距、缩小内边距，仅存标题行，为提示词区省出空间
            gapRow.Height = expanded ? new GridLength(0) : new GridLength(8);
            card.Padding = expanded ? new Thickness(12, 6, 12, 4) : new Thickness(12, 8, 12, 10);
        };

        Grid.SetRow(_slotsHost, 2);
        rootGrid.Children.Add(_slotsHost);

        card.Child = rootGrid;
        Root = card;

        // 默认收起：仅显示标题行（更扁），需要时点 ▸ 展开
        if (startCollapsed && _slotsHost != null)
        {
            _slotsHost.Visibility = Visibility.Collapsed;
            _toggleBtn.Content = "▸";
            gapRow.Height = new GridLength(0);
            card.Padding = new Thickness(12, 6, 12, 4);
        }

        UpdateBadge();
    }

    // ===== 帧位卡片 =====

    private Border BuildSlotCard(
        FrameSlot slot, string roleDesc, string autoLabel, string autoTip)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
            BorderBrush = Br("BorderBrush", Color.FromRgb(0x3A, 0x3A, 0x46)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 0 缩略图
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2 信息+按钮

        // 缩略图
        var thumbHost = new Border
        {
            Width = 148, Height = 82,
            Background = Brushes.Black,
            BorderBrush = Br("BorderBrush", Color.FromRgb(0x3A, 0x3A, 0x46)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            ClipToBounds = true
        };
        var thumbGrid = new Grid();
        slot.Placeholder = new TextBlock
        {
            Text = "未选帧", FontSize = 11,
            Foreground = Br("TextSecondaryBrush", Color.FromRgb(0x88, 0x88, 0x99)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        thumbGrid.Children.Add(slot.Placeholder);
        slot.Thumb = new Image { Stretch = Stretch.Uniform };
        thumbGrid.Children.Add(slot.Thumb);
        thumbHost.Child = thumbGrid;
        grid.Children.Add(thumbHost);

        // 信息 + 按钮
        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var roleRow = new StackPanel { Orientation = Orientation.Horizontal };
        roleRow.Children.Add(new TextBlock
        {
            Text = slot.Role, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            Foreground = Br("PrimaryBrush", Color.FromRgb(0x4A, 0x90, 0xE2)),
            VerticalAlignment = VerticalAlignment.Center
        });
        roleRow.Children.Add(new TextBlock
        {
            Text = $" · {roleDesc}", FontSize = 10.5,
            Foreground = Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        infoStack.Children.Add(roleRow);

        slot.Info = new TextBlock
        {
            Text = "未选择视频", FontSize = 10.5,
            Foreground = Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB)),
            Margin = new Thickness(0, 2, 0, 4),
            TextWrapping = TextWrapping.Wrap, MaxHeight = 30
        };
        infoStack.Children.Add(slot.Info);

        var btnRow = new WrapPanel();
        slot.AutoBtn = new Button
        {
            Content = autoLabel, FontSize = 10.5, Height = 26, Padding = new Thickness(10, 2, 10, 2),
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
            ToolTip = autoTip
        };
        btnRow.Children.Add(slot.AutoBtn);
        slot.AssetBtn = new Button
        {
            Content = "📁 资产库", FontSize = 10.5, Height = 26, Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(4, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
            ToolTip = "从软件中已有的视频资产里选择（也可直接把视频文件拖入本卡片）",
            IsEnabled = _videoAssets.Count > 0
        };
        btnRow.Children.Add(slot.AssetBtn);
        slot.ManualBtn = new Button
        {
            Content = "🎞 手动选帧", FontSize = 10.5, Height = 26, Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(4, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
            ToolTip = "打开视频播放器，精准选择某一时间点的画面帧"
        };
        btnRow.Children.Add(slot.ManualBtn);
        slot.ClearBtn = new Button
        {
            Content = "清除该帧", FontSize = 10.5, Height = 26, Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x2A, 0x4A, 0x90, 0xE2)),
            Foreground = Br("TextPrimaryBrush", Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x4A, 0x90, 0xE2)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "清除该帧",
            Visibility = Visibility.Collapsed
        };
        btnRow.Children.Add(slot.ClearBtn);
        slot.SaveBtn = new Button
        {
            Content = "💾 保存该帧", FontSize = 10.5, Height = 26, Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(4, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
            ToolTip = "把当前帧保存为图片到本地",
            Visibility = Visibility.Collapsed
        };
        btnRow.Children.Add(slot.SaveBtn);
        slot.SaveBtn.Click += (_, _) => SaveFrame(slot);
        infoStack.Children.Add(btnRow);

        Grid.SetColumn(infoStack, 2);
        grid.Children.Add(infoStack);
        card.Child = grid;

        slot.ThumbHost = thumbHost;

        // 拖入视频文件直接自动取帧（首帧拖入取尾帧、尾帧拖入取首帧，由宿主绑定）
        card.AllowDrop = true;
        card.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                        && (e.Data.GetData(DataFormats.FileDrop) as string[])?.Any(ViewHelpers.IsVideoFile) == true
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        card.Drop += async (_, e) =>
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var video = files?.FirstOrDefault(ViewHelpers.IsVideoFile);
            if (video == null) return;
            await AutoExtractAsync(slot, video, tail: slot.Role == "首帧");
            Notify?.Invoke("✓ 已通过拖入视频取帧");
        };

        return card;
    }

    // ===== 取帧逻辑 =====

    private async Task AutoExtractAsync(FrameSlot slot, bool tail)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv",
            Title = tail ? "选择 A 段视频（自动提取尾帧作首帧）" : "选择 B 段视频（自动提取首帧作尾帧）"
        };
        if (dlg.ShowDialog(_owner) != true) return;
        await AutoExtractAsync(slot, dlg.FileName, tail);
    }

    /// <summary>从软件已有的视频资产里选择一段视频，自动取首/尾帧。</summary>
    private async Task PickFromAssetsAsync(FrameSlot slot, bool tail)
    {
        if (_videoAssets.Count == 0)
        {
            Notify?.Invoke("⚠ 当前项目没有可用的视频资产，可点击「上传」选择本地视频");
            return;
        }
        var picker = new AssetPickerWindow(_videoAssets, $"选择视频资产（用于{slot.Role}）") { Owner = _owner };
        if (picker.ShowDialog() != true) return;
        var path = picker.SelectedPath;
        if (string.IsNullOrEmpty(path)) return;
        await AutoExtractAsync(slot, path, tail);
    }

    /// <summary>对指定视频自动提取首/尾帧，并填入对应帧位。</summary>
    private async Task AutoExtractAsync(FrameSlot slot, string path, bool tail)
    {
        slot.AutoBtn.IsEnabled = false;
        var oldContent = slot.AutoBtn.Content;
        slot.AutoBtn.Content = "⏳ 提取中…";
        try
        {
            var dur = await Task.Run(() => ViewHelpers.GetVideoDurationSeconds(path));
            if (dur is not { } d || d <= 0) { Notify?.Invoke("⚠ 无法读取视频时长"); return; }
            var sec = tail ? Math.Max(0, d - 0.1) : 0.0;
            var data = await Task.Run(() => ViewHelpers.ExtractVideoFrameToBase64(path, sec));
            if (string.IsNullOrEmpty(data))
            {
                Notify?.Invoke("⚠ 帧提取失败，请尝试「手动选帧」");
                return;
            }
            SetSlot(slot, path, sec, data);
            Notify?.Invoke(tail
                ? "✓ 已自动提取视频尾帧作为首帧"
                : "✓ 已自动提取视频首帧作为尾帧");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"⚠ 提取失败：{ex.Message}");
        }
        finally
        {
            slot.AutoBtn.IsEnabled = true;
            slot.AutoBtn.Content = oldContent;
        }
    }

    private Task ManualPickAsync(FrameSlot slot, string confirmLabel)
    {
        var path = slot.VideoPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv",
                Title = $"选择视频（用于{slot.Role}）"
            };
            if (dlg.ShowDialog(_owner) != true) return Task.CompletedTask;
            path = dlg.FileName;
        }

        var picker = new FramePickerWindow(path, confirmLabel, _videoAssets) { Owner = _owner };
        if (picker.ShowDialog() != true) return Task.CompletedTask;
        if (string.IsNullOrEmpty(picker.FrameDataUrl)) return Task.CompletedTask;
        SetSlot(slot, path, picker.FrameSeconds, picker.FrameDataUrl);
        Notify?.Invoke($"✓ 已手动选取{slot.Role}");
        return Task.CompletedTask;
    }

    private void SetSlot(FrameSlot slot, string path, double seconds, string data)
    {
        slot.VideoPath = path;
        slot.TimeSeconds = seconds;
        slot.DataUrl = data;

        var img = ViewHelpers.DecodeBase64Image(data);
        if (img != null)
        {
            slot.Thumb.Source = img;
            slot.Placeholder.Visibility = Visibility.Collapsed;
        }
        AttachHoverPreview(slot);
        slot.Info.Text = $"来自 {Path.GetFileName(path)}\n时间 {FormatTime(seconds)}";
        slot.ClearBtn.Visibility = Visibility.Visible;
        slot.SaveBtn.Visibility = Visibility.Visible;
        UpdateBadge();
        Changed?.Invoke();
    }

    private void ClearSlot(FrameSlot slot)
    {
        slot.VideoPath = null;
        slot.TimeSeconds = 0;
        slot.DataUrl = null;
        slot.Thumb.Source = null;
        slot.ThumbHost.ToolTip = null;
        slot.Placeholder.Visibility = Visibility.Visible;
        slot.Info.Text = "未选择视频";
        slot.ClearBtn.Visibility = Visibility.Collapsed;
        slot.SaveBtn.Visibility = Visibility.Collapsed;
        UpdateBadge();
        Changed?.Invoke();
    }

    private void UpdateBadge()
    {
        bool f = _first.DataUrl != null, l = _last.DataUrl != null;
        _badge.Text = f && l
            ? "✓ 首尾帧已就绪"
            : (f || l ? "· 请同时设置首尾帧" : "· 未设置");
        _badge.Foreground = f && l
            ? Br("PrimaryBrush", Color.FromRgb(0x4A, 0x90, 0xE2))
            : Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB));
    }

    // ===== 工具 =====

    /// <summary>为帧缩略图挂上“大图预览”悬停浮层（显示原帧全尺寸预览）。</summary>
    private static void AttachHoverPreview(FrameSlot slot)
    {
        slot.ThumbHost.ToolTip = null;
        if (slot.Thumb.Source == null) return;
        var pop = new Image
        {
            Source = slot.Thumb.Source,
            Stretch = Stretch.Uniform,
            MaxWidth = 460, MaxHeight = 320
        };
        slot.ThumbHost.ToolTip = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x20)),
            BorderBrush = Br("BorderBrush", Color.FromRgb(0x3A, 0x3A, 0x46)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Child = pop
        };
        System.Windows.Controls.ToolTipService.SetShowDuration(slot.ThumbHost, 20000);
    }

    /// <summary>把当前帧保存为 PNG/JPEG 图片到本地。</summary>
    private void SaveFrame(FrameSlot slot)
    {
        if (string.IsNullOrEmpty(slot.DataUrl))
        {
            Notify?.Invoke($"⚠ 尚未设置{slot.Role}，请先上传视频或手动选帧");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
            Title = $"保存{slot.Role}图片",
            FileName = $"{Path.GetFileNameWithoutExtension(slot.VideoPath ?? slot.Role)}_{slot.Role}.png"
        };
        if (dlg.ShowDialog(_owner) != true) return;
        try
        {
            var bmp = ViewHelpers.DecodeBase64Image(slot.DataUrl);
            if (bmp == null) { Notify?.Invoke($"⚠ 无法解码{slot.Role}"); return; }
            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            BitmapEncoder encoder = ext == ".jpg" || ext == ".jpeg"
                ? new JpegBitmapEncoder { QualityLevel = 95 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = new FileStream(dlg.FileName, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
            Notify?.Invoke($"✓ 已保存{slot.Role}到：{Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"⚠ 保存失败：{ex.Message}");
        }
    }

    private sealed class FrameSlot
    {
        public string Role = "";
        public string? VideoPath;
        public double TimeSeconds;
        public string? DataUrl;
        public Image Thumb = null!;
        public Border ThumbHost = null!;
        public TextBlock Placeholder = null!;
        public TextBlock Info = null!;
        public Button AutoBtn = null!;
        public Button AssetBtn = null!;
        public Button ManualBtn = null!;
        public Button ClearBtn = null!;
        public Button SaveBtn = null!;
    }

    private static Brush Br(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is Brush b ? b : new SolidColorBrush(fallback);

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
    }
}
