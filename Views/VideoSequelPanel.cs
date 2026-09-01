using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

/// <summary>
/// AI 视频生成窗口的「视频续集」面板：
/// 上传上一段视频自动提取其尾帧，或手动精准选帧，作为续集生成的参考图片。
/// 帧就绪后通过 FrameReady 事件交给宿主，宿主将其加入参考图列表并显示缩略图。
/// </summary>
public sealed class VideoSequelPanel
{
    private readonly Window _owner;
    private readonly IReadOnlyList<string> _videoAssets;

    private readonly Image _thumb = null!;
    private readonly Border _thumbHost = null!;
    private readonly TextBlock _placeholder = null!;
    private readonly TextBlock _info = null!;
    private readonly Button _clearBtn = null!;
    private readonly Button _saveBtn = null!;

    private string? _frameDataUrl;
    private string? _sourcePath;
    private string _label = "";

    public FrameworkElement Root { get; }

    /// <summary>已就绪的尾帧 base64 Data URL（未设置为 null）</summary>
    public string? FrameDataUrl => _frameDataUrl;
    /// <summary>尾帧提及名称（用于提示词 @）</summary>
    public string Label => _label;

    /// <summary>尾帧就绪时触发（dataUrl, label, sourcePath）</summary>
    public event Action<string, string, string>? FrameReady;
    /// <summary>尾帧被清除时触发</summary>
    public event Action? FrameCleared;
    /// <summary>提示气泡（由宿主窗口接入其 Toast）</summary>
    public Action<string>? Notify { get; set; }

    public VideoSequelPanel(Window owner, IReadOnlyList<string>? videoAssets = null, bool startCollapsed = false)
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
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 2 内容区
        var gapRow = rootGrid.RowDefinitions[1];

        // ---- 标题行 ----
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = "🎬 视频续集（尾帧参考）",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Br("TextPrimaryBrush", Colors.White),
            VerticalAlignment = VerticalAlignment.Center
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "  上传上一段视频自动取尾帧作参考，或手动选帧，让续集画面无缝衔接",
            FontSize = 10.5,
            Foreground = Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        header.Children.Add(titleStack);

        var toggleBtn = ViewHelpers.BuildAccentToggleButton("展开 / 收起面板");
        toggleBtn.Content = "▾";
        Grid.SetColumn(toggleBtn, 1);
        header.Children.Add(toggleBtn);
        rootGrid.Children.Add(header);

        // ---- 内容区 ----
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 0 缩略图
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2 信息+按钮

        var thumbHost = new Border
        {
            Width = 148, Height = 82,
            Background = Brushes.Black,
            BorderBrush = Br("BorderBrush", Color.FromRgb(0x3A, 0x3A, 0x46)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            ClipToBounds = true
        };
        var thumbGrid = new Grid();
        _placeholder = new TextBlock
        {
            Text = "未取尾帧", FontSize = 11,
            Foreground = Br("TextSecondaryBrush", Color.FromRgb(0x88, 0x88, 0x99)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        thumbGrid.Children.Add(_placeholder);
        _thumb = new Image { Stretch = Stretch.Uniform };
        thumbGrid.Children.Add(_thumb);
        thumbHost.Child = thumbGrid;
        content.Children.Add(thumbHost);
        _thumbHost = thumbHost;

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var roleRow = new StackPanel { Orientation = Orientation.Horizontal };
        roleRow.Children.Add(new TextBlock
        {
            Text = "尾帧", FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            Foreground = Br("PrimaryBrush", Color.FromRgb(0x4A, 0x90, 0xE2)),
            VerticalAlignment = VerticalAlignment.Center
        });
        roleRow.Children.Add(new TextBlock
        {
            Text = " · 上一段视频结尾的画面，将作为续集生成的参考图", FontSize = 10.5,
            Foreground = Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        infoStack.Children.Add(roleRow);
        _info = new TextBlock
        {
            Text = "未选择视频", FontSize = 10.5,
            Foreground = Br("TextSecondaryBrush", Color.FromRgb(0xAA, 0xAA, 0xBB)),
            Margin = new Thickness(0, 2, 0, 4),
            TextWrapping = TextWrapping.Wrap, MaxHeight = 30
        };
        infoStack.Children.Add(_info);

        var btnRow = new WrapPanel();
        var autoBtn = new Button
        {
            Content = "📤 上传视频 · 自动取尾帧", FontSize = 10.5, Height = 26, Padding = new Thickness(10, 2, 10, 2),
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
            ToolTip = "选择上一段视频，软件自动提取其尾帧作为续集参考图"
        };
        btnRow.Children.Add(autoBtn);
        var assetBtn = new Button
        {
            Content = "📁 资产库", FontSize = 10.5, Height = 26, Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(4, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
            ToolTip = "从软件中已有的视频资产里选择（也可直接把视频文件拖入本卡片）",
            IsEnabled = _videoAssets.Count > 0
        };
        btnRow.Children.Add(assetBtn);
        var manualBtn = new Button
        {
            Content = "🎞 手动选帧", FontSize = 10.5, Height = 26, Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(4, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
            ToolTip = "打开视频播放器，精准选择某一时间点的画面帧作为尾帧"
        };
        btnRow.Children.Add(manualBtn);
        _clearBtn = new Button
        {
            Content = "清除该帧", FontSize = 10.5, Height = 26, Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x2A, 0x4A, 0x90, 0xE2)),
            Foreground = Br("TextPrimaryBrush", Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x4A, 0x90, 0xE2)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "清除尾帧参考",
            Visibility = Visibility.Collapsed
        };
        btnRow.Children.Add(_clearBtn);
        _saveBtn = new Button
        {
            Content = "💾 保存该帧", FontSize = 10.5, Height = 26, Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(4, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
            ToolTip = "把当前尾帧保存为图片到本地",
            Visibility = Visibility.Collapsed
        };
        btnRow.Children.Add(_saveBtn);
        infoStack.Children.Add(btnRow);

        Grid.SetColumn(infoStack, 2);
        content.Children.Add(infoStack);

        autoBtn.Click += async (_, _) => await AutoExtractAsync();
        assetBtn.Click += async (_, _) => await PickFromAssetsAsync();
        manualBtn.Click += async (_, _) => await ManualPickAsync();
        _clearBtn.Click += (_, _) => ClearFrame();
        _saveBtn.Click += (_, _) => SaveFrame();
        toggleBtn.Click += (_, _) =>
        {
            bool expanded = content.Visibility == Visibility.Visible;
            content.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            toggleBtn.Content = expanded ? "▸" : "▾";
            // 收起时更扁：隐藏间距、缩小内边距，仅存标题行，为提示词区省出空间
            gapRow.Height = expanded ? new GridLength(0) : new GridLength(8);
            card.Padding = expanded ? new Thickness(12, 6, 12, 4) : new Thickness(12, 8, 12, 10);
        };

        // 拖入视频文件直接自动取尾帧
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
            await AutoExtractAsync(video);
            Notify?.Invoke("✓ 已通过拖入视频取尾帧");
        };

        Grid.SetRow(content, 2);
        rootGrid.Children.Add(content);
        card.Child = rootGrid;
        Root = card;

        // 默认收起：仅显示标题行（更扁），需要时点 ▸ 展开
        if (startCollapsed)
        {
            content.Visibility = Visibility.Collapsed;
            toggleBtn.Content = "▸";
            gapRow.Height = new GridLength(0);
            card.Padding = new Thickness(12, 6, 12, 4);
        }
    }

    // ===== 取帧逻辑 =====

    private async Task AutoExtractAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv",
            Title = "选择上一段视频（自动提取尾帧作续集参考）"
        };
        if (dlg.ShowDialog(_owner) != true) return;
        await AutoExtractAsync(dlg.FileName);
    }

    private async Task PickFromAssetsAsync()
    {
        if (_videoAssets.Count == 0)
        {
            Notify?.Invoke("⚠ 当前项目没有可用的视频资产，可点击「上传视频」选择本地视频");
            return;
        }
        var picker = new AssetPickerWindow(_videoAssets, "选择视频资产（用于取尾帧）") { Owner = _owner };
        if (picker.ShowDialog() != true) return;
        var path = picker.SelectedPath;
        if (string.IsNullOrEmpty(path)) return;
        await AutoExtractAsync(path);
    }

    private async Task AutoExtractAsync(string path)
    {
        try
        {
            var dur = await Task.Run(() => ViewHelpers.GetVideoDurationSeconds(path));
            if (dur is not { } d || d <= 0) { Notify?.Invoke("⚠ 无法读取视频时长"); return; }
            var sec = Math.Max(0, d - 0.1);
            var data = await Task.Run(() => ViewHelpers.ExtractVideoFrameToBase64(path, sec));
            if (string.IsNullOrEmpty(data))
            {
                Notify?.Invoke("⚠ 帧提取失败，请尝试「手动选帧」");
                return;
            }
            SetFrame(path, sec, data);
            Notify?.Invoke("✓ 已自动提取视频尾帧作为续集参考图");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"⚠ 提取失败：{ex.Message}");
        }
    }

    private Task ManualPickAsync()
    {
        // 复用已选视频（自动取帧/资产库/上次手动选帧后），避免每次手动选帧都重新要求上传
        var path = _sourcePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv",
                Title = "选择视频（手动选尾帧）"
            };
            if (dlg.ShowDialog(_owner) != true) return Task.CompletedTask;
            path = dlg.FileName;
        }

        var picker = new FramePickerWindow(path, "取此帧作为续集尾帧", _videoAssets) { Owner = _owner };
        if (picker.ShowDialog() != true) return Task.CompletedTask;
        if (string.IsNullOrEmpty(picker.FrameDataUrl)) return Task.CompletedTask;
        SetFrame(path, picker.FrameSeconds, picker.FrameDataUrl);
        Notify?.Invoke("✓ 已手动选取续集尾帧");
        return Task.CompletedTask;
    }

    private void SetFrame(string path, double seconds, string data)
    {
        var label = "尾帧·" + Path.GetFileNameWithoutExtension(path);

        // 若已有尾帧，先通知宿主移除旧的，避免重复
        if (_frameDataUrl != null) FrameCleared?.Invoke();

        _frameDataUrl = data;
        _sourcePath = path;
        _label = label;

        var img = ViewHelpers.DecodeBase64Image(data);
        if (img != null)
        {
            _thumb.Source = img;
            _placeholder.Visibility = Visibility.Collapsed;
        }
        AttachHoverPreview(img);
        _info.Text = $"来自 {Path.GetFileName(path)}\n时间 {FormatTime(seconds)} · 提示词中用 @{label} 引用";
        _clearBtn.Visibility = Visibility.Visible;
        _saveBtn.Visibility = Visibility.Visible;
        FrameReady?.Invoke(data, label, path);
    }

    private void ClearFrame()
    {
        _frameDataUrl = null;
        _sourcePath = null;
        _thumb.Source = null;
        _thumbHost.ToolTip = null;
        _placeholder.Visibility = Visibility.Visible;
        _info.Text = "未选择视频";
        _clearBtn.Visibility = Visibility.Collapsed;
        _saveBtn.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrEmpty(_label)) FrameCleared?.Invoke();
        _label = "";
    }

    /// <summary>把当前尾帧保存为 PNG/JPEG 图片到本地。</summary>
    private void SaveFrame()
    {
        if (string.IsNullOrEmpty(_frameDataUrl))
        {
            Notify?.Invoke("⚠ 尚未取到尾帧，请先上传视频或手动选帧");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
            Title = "保存尾帧图片",
            FileName = $"{Path.GetFileNameWithoutExtension(_sourcePath ?? "尾帧")}_尾帧.png"
        };
        if (dlg.ShowDialog(_owner) != true) return;
        try
        {
            var bmp = ViewHelpers.DecodeBase64Image(_frameDataUrl);
            if (bmp == null) { Notify?.Invoke("⚠ 无法解码尾帧"); return; }
            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            BitmapEncoder encoder = ext == ".jpg" || ext == ".jpeg"
                ? new JpegBitmapEncoder { QualityLevel = 95 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = new FileStream(dlg.FileName, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
            Notify?.Invoke($"✓ 已保存尾帧到：{Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"⚠ 保存失败：{ex.Message}");
        }
    }

    // ===== 工具 =====

    /// <summary>为帧缩略图挂上“大图预览”悬停浮层（显示原帧全尺寸预览）。</summary>
    private void AttachHoverPreview(ImageSource? source)
    {
        _thumbHost.ToolTip = null;
        if (source == null) return;
        var pop = new Image
        {
            Source = source,
            Stretch = Stretch.Uniform,
            MaxWidth = 460, MaxHeight = 320
        };
        _thumbHost.ToolTip = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x20)),
            BorderBrush = Br("BorderBrush", Color.FromRgb(0x3A, 0x3A, 0x46)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Child = pop
        };
        System.Windows.Controls.ToolTipService.SetShowDuration(_thumbHost, 20000);
    }

    private static Brush Br(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is Brush b ? b : new SolidColorBrush(fallback);

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
    }
}
