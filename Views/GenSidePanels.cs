using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Threading.Tasks;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

/// <summary>
/// 生成窗口左侧边栏：可导入文本、编辑文本、把选中文字加入提示词、导出文本，
/// 并提供「默认提示词」选择与管理（默认提示词按 Image/Video 分类持久化到配置）。
/// </summary>
public sealed class PromptPanel
{
    private static readonly Brush _primaryBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2));

    public FrameworkElement Root { get; }
    public TextBox TextBox { get; }

    /// <summary>把文本追加到主提示词输入框（由宿主窗口设置）</summary>
    public Action<string>? AppendToPrompt { get; set; }

    /// <param name="owner">宿主窗口（用于默认提示词管理窗口的 Owner）</param>
    /// <param name="promptKey">默认提示词分类键："Image" 或 "Video"</param>
    public PromptPanel(Window owner, string promptKey)
    {
        var textBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 11.5,
            Foreground = Brush("TextPrimaryBrush"),
            Background = Brush("CardBackgroundBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            MinHeight = 120,
            ToolTip = "可导入/编辑文本，选中文字后点击「选区加入」把该段文字加入提示词"
        };
        TextBox = textBox;

        var importBtn = SmallBtn("📄 导入");
        var exportBtn = SmallBtn("💾 导出");
        var useSelBtn = SmallBtn("✂️ 选区加入");
        var presetBtn = SmallBtn("⭐ 默认");

        // 四个按钮用 2×2 等宽网格铺满左侧栏宽度，整齐美观
        var btnGrid = new System.Windows.Controls.Primitives.UniformGrid
        {
            Rows = 2, Columns = 2, Margin = new Thickness(0, 0, 0, 6)
        };
        foreach (var b in new[] { importBtn, exportBtn, useSelBtn, presetBtn })
        {
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Margin = new Thickness(0, 0, 4, 4);
            btnGrid.Children.Add(b);
        }

        // 文本框占满剩余高度（自带垂直滚动），避免左侧栏下方留白
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 按钮区
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });                    // 1 间距
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 2 文本编辑区
        Grid.SetRow(btnGrid, 0);
        Grid.SetRow(textBox, 2);
        body.Children.Add(btnGrid);
        body.Children.Add(textBox);

        // 导入文本文件到文本框
        importBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "文本文件|*.txt;*.md|所有文件|*.*",
                Title = "导入提示词文本"
            };
            if (dlg.ShowDialog(owner) != true) return;
            try
            {
                var content = File.ReadAllText(dlg.FileName);
                textBox.Text = content;
                MainWindow.Notify($"✓ 已导入 {Path.GetFileName(dlg.FileName)}（{content.Length} 字符）");
            }
            catch (Exception ex)
            {
                MainWindow.Notify($"⚠ 导入失败：{ex.Message}", success: false);
            }
        };

        // 导出文本框内容
        exportBtn.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(textBox.Text))
            {
                MainWindow.Notify("⚠ 文本框内容为空，无可导出", success: false);
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "文本文件|*.txt",
                Title = "导出提示词文本",
                FileName = $"prompt_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
            };
            if (dlg.ShowDialog(owner) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, textBox.Text);
                MainWindow.Notify("✓ 已导出文本");
            }
            catch (Exception ex)
            {
                MainWindow.Notify($"⚠ 导出失败：{ex.Message}", success: false);
            }
        };

        // 把选中文字（无选区则整文）加入主提示词
        useSelBtn.Click += (_, _) =>
        {
            var sel = textBox.SelectedText;
            if (string.IsNullOrWhiteSpace(sel))
            {
                if (string.IsNullOrWhiteSpace(textBox.Text)) { MainWindow.Notify("⚠ 请先导入或输入文本", success: false); return; }
                sel = textBox.Text.Trim();
            }
            sel = sel.Trim();
            AppendToPrompt?.Invoke(sel);
            MainWindow.Notify("✓ 已加入提示词");
        };

        // 默认提示词：打开管理窗口（勾选式，勾选后生成时自动追加到提示词末尾）
        presetBtn.Click += (_, _) =>
        {
            try
            {
                DefaultPromptWindow.Show(owner, promptKey,
                    promptKey == "Video" ? "视频默认提示词" : "图片默认提示词");
            }
            catch (Exception ex)
            {
                MainWindow.Notify($"⚠ 默认提示词操作失败：{ex.Message}", success: false);
            }
        };

        var titleText = new TextBlock
        {
            Text = "📋 提示词素材", FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var tip = new TextBlock
        {
            Text = "选区加入", FontSize = 10,
            Foreground = Brush("TextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(tip, 1);
        titleRow.Children.Add(titleText);
        titleRow.Children.Add(tip);

        var root = new Border
        {
            MinWidth = 215,
            Background = Brush("SidebarBackgroundBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 10, 10, 10),
            Child = new DockPanel { Children = { titleRow, body } }
        };
        // DockPanel：标题放顶部
        DockPanel.SetDock(titleRow, Dock.Top);
        Root = root;
    }

    /// <summary>把折叠/展开按钮放进面板标题栏右侧（代替浮在面板上方的独立按钮）。</summary>
    public void SetCollapseToggle(Button toggle)
    {
        if (Root is not Border border || border.Child is not DockPanel dock) return;
        var titleRow = dock.Children.OfType<Grid>().FirstOrDefault();
        if (titleRow == null) return;
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toggle.HorizontalAlignment = HorizontalAlignment.Center;
        toggle.VerticalAlignment = VerticalAlignment.Center;
        toggle.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(toggle, titleRow.ColumnDefinitions.Count - 1);
        titleRow.Children.Add(toggle);
    }

    private static Button SmallBtn(string text) => new()
    {
        Content = text, FontSize = 11, Padding = new Thickness(8, 4, 8, 4),
        Margin = new Thickness(0, 0, 6, 0),
        Style = Style("SecondaryButtonStyle")
    };

    private static Brush Brush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    private static Style? Style(string key) =>
        Application.Current.TryFindResource(key) as Style;
}

/// <summary>
/// 生成窗口右侧边栏：展示项目图片资产缩略图，点击切换选中并按点击顺序编号（参考图顺序）。
/// 达到 MaxCount 上限后拒绝继续添加并提示。
/// </summary>
public sealed class AssetPanel
{
    private static readonly Brush _normalBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly Brush _selectedBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2));

    private const int BatchSize = 10;
    private const int MaxCards = 300;

    private readonly Dictionary<string, Border> _cards = new();
    private readonly Dictionary<string, TextBlock> _orderBadges = new();
    private readonly List<string> _selectedOrder = new();
    private readonly List<string> _allPaths = new();
    private readonly WrapPanel _grid = new();
    private readonly TextBlock _countText;
    private readonly TextBlock _hintText;
    private readonly string _title;
    private int _loadIndex;
    private int _pendingDecodes;

    public FrameworkElement Root { get; }
    /// <summary>参考图数量上限（图片 6，视频 Flash 5）</summary>
    public int MaxCount { get; set; }
    /// <summary>选择顺序变化时触发（宿主据此重建参考图列表）</summary>
    public Action? SelectionChanged { get; set; }
    /// <summary>按点击顺序返回已选图片路径</summary>
    public IReadOnlyList<string> SelectedOrder => _selectedOrder;

    public AssetPanel(string title, IReadOnlyList<string> imagePaths, int maxCount = 6)
    {
        _title = title;
        MaxCount = maxCount;
        AddPaths(imagePaths);

        _countText = new TextBlock
        {
            FontSize = 10.5, Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _hintText = new TextBlock
        {
            FontSize = 10.5, Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap
        };

        var refreshBtn = SmallBtn("🔄");
        refreshBtn.ToolTip = "重新扫描项目资产目录";
        refreshBtn.Click += (_, _) => MainWindow.Notify("资产列表已是最新");

        var titleText = new TextBlock
        {
            Text = $"🖼️ {_title}", FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center
        };
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(refreshBtn, 1);
        titleRow.Children.Add(titleText);
        titleRow.Children.Add(refreshBtn);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _grid,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var body = new DockPanel();
        DockPanel.SetDock(titleRow, Dock.Top);
        DockPanel.SetDock(_countText, Dock.Bottom);
        DockPanel.SetDock(_hintText, Dock.Bottom);
        body.Children.Add(titleRow);
        body.Children.Add(_countText);
        body.Children.Add(_hintText);
        body.Children.Add(scroll);   // 最后一个子元素填充剩余空间，避免右侧栏下方留白

        Root = new Border
        {
            MinWidth = 280,
            Background = Brush("SidebarBackgroundBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 10, 10, 10),
            Child = body
        };
        UpdateCountText();
    }

    /// <summary>把折叠/展开按钮放进面板标题栏右侧（代替浮在面板上方的独立按钮）。</summary>
    public void SetCollapseToggle(Button toggle)
    {
        if (Root is not Border border || border.Child is not DockPanel body) return;
        var titleRow = body.Children.OfType<Grid>().FirstOrDefault();
        if (titleRow == null) return;
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toggle.HorizontalAlignment = HorizontalAlignment.Center;
        toggle.VerticalAlignment = VerticalAlignment.Center;
        toggle.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(toggle, titleRow.ColumnDefinitions.Count - 1);
        titleRow.Children.Add(toggle);
    }

    /// <summary>加入资产路径（去重），并开始分批加载缩略图</summary>
    public void AddPaths(IReadOnlyList<string> imagePaths)
    {
        var seen = new HashSet<string>(_allPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var p in imagePaths)
        {
            if (File.Exists(p) && seen.Add(p)) _allPaths.Add(p);
        }
        _loadIndex = 0;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(LoadNextBatch), DispatcherPriority.Background);
    }

    private void LoadNextBatch()
    {
        int scheduled = 0;
        for (int i = 0; i < BatchSize && _loadIndex < _allPaths.Count && _cards.Count < MaxCards; i++, _loadIndex++)
        {
            var path = _allPaths[_loadIndex];
            if (!File.Exists(path)) continue;
            scheduled++;
            _pendingDecodes++;
            var captured = path;
            // 图片解码放到后台线程，避免大量缩略图解码阻塞 UI（首次打开窗口卡顿的根因）
            Task.Run(() => DecodeThumb(captured))
                .ContinueWith(t =>
                {
                    _pendingDecodes--;
                    try
                    {
                        var bmp = t.Result;
                        if (bmp != null && !_cards.ContainsKey(captured))
                        {
                            var card = BuildCard(captured, bmp);
                            _grid.Children.Add(card);
                            _cards[captured] = card;
                            UpdateHint();
                        }
                    }
                    catch { /* 跳过无法解码的图片 */ }
                    if (_pendingDecodes == 0) ScheduleNextBatchOrFinish();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        if (scheduled == 0) ScheduleNextBatchOrFinish();
    }

    private void ScheduleNextBatchOrFinish()
    {
        if (_loadIndex < _allPaths.Count && _cards.Count < MaxCards)
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(LoadNextBatch), DispatcherPriority.Background);
        else
            UpdateHint();
    }

    /// <summary>后台线程解码缩略图（冻结后跨线程安全）</summary>
    private static BitmapImage? DecodeThumb(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 200;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private void UpdateHint()
    {
        _hintText.Text = _cards.Count == 0
            ? "当前项目没有可用的图片资产，请先通过 AI 生图或拖入图片生成素材。"
            : $"共 {_cards.Count} 张，点击按顺序编号（上限 {MaxCount} 张）";
    }

    private Border BuildCard(string path, BitmapImage bmp)
    {
        var img = new Image
        {
            Source = bmp, Stretch = Stretch.Uniform,
            Margin = new Thickness(4, 4, 4, 2)
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var name = new TextBlock
        {
            Text = Path.GetFileName(path),
            FontSize = 9.5,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush("TextSecondaryBrush"),
            Margin = new Thickness(4, 0, 4, 4),
            ToolTip = path
        };
        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(img, 0);
        Grid.SetRow(name, 1);
        inner.Children.Add(img);
        inner.Children.Add(name);

        var orderBadge = new TextBlock
        {
            FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 4, 0),
            Background = _selectedBrush,
            Padding = new Thickness(4, 0, 4, 0),
            Visibility = Visibility.Collapsed
        };
        inner.Children.Add(orderBadge);

        var card = new Border
        {
            Width = 120, Height = 96,
            Margin = new Thickness(0, 0, 6, 6),
            Background = Brush("CardBackgroundBrush"),
            BorderBrush = _normalBrush,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            Tag = path,
            Child = inner,
            // 右键菜单：查看图片 / 在文件夹中显示
            ContextMenu = ViewHelpers.BuildAssetContextMenu(path, () => ViewHelpers.ShowImageViewer(path))
        };
        card.MouseLeftButtonUp += (_, _) => ToggleSelect(path);
        // 鼠标悬停显示原图放大预览（复用参考图缩略图的悬停预览浮层）
        ViewHelpers.AttachLargePreview(card, path, null);
        _orderBadges[path] = orderBadge;
        return card;
    }

    /// <summary>点击切换选中并按点击顺序编号</summary>
    private void ToggleSelect(string path)
    {
        if (_selectedOrder.Contains(path))
        {
            _selectedOrder.Remove(path);
        }
        else
        {
            if (_selectedOrder.Count >= MaxCount)
            {
                MainWindow.Notify($"⚠ 参考图最多 {MaxCount} 张，请先移除部分已选图片", success: false);
                return;
            }
            _selectedOrder.Add(path);
        }
        UpdateOrderBadges();
        UpdateCountText();
        SelectionChanged?.Invoke();
    }

    /// <summary>外部（历史回填/拖入）添加已选图片，不触发重复编号</summary>
    public void SelectImported(string path)
    {
        if (_selectedOrder.Contains(path) || !File.Exists(path)) return;
        if (_selectedOrder.Count >= MaxCount) return;
        _selectedOrder.Add(path);
        UpdateOrderBadges();
        UpdateCountText();
        SelectionChanged?.Invoke();
    }

    /// <summary>按指定顺序设置已选（历史回填），并高亮对应卡片</summary>
    public void SetSelection(IEnumerable<string> paths)
    {
        _selectedOrder.Clear();
        foreach (var p in paths)
        {
            if (_selectedOrder.Count >= MaxCount) break;
            if (!string.IsNullOrEmpty(p) && !_selectedOrder.Contains(p) && _allPaths.Contains(p))
                _selectedOrder.Add(p);
        }
        UpdateOrderBadges();
        UpdateCountText();
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        if (_selectedOrder.Count == 0) return;
        _selectedOrder.Clear();
        UpdateOrderBadges();
        UpdateCountText();
        SelectionChanged?.Invoke();
    }

    /// <summary>从已选中移除某张图片（保留其余顺序与编号），用于参考图 ✕ 删除时保持右侧资产栏同步。
    /// 不触发 SelectionChanged：参考图缩略图与 refImages 已由删除方直接移除，避免重建清空本地手动添加的参考图。</summary>
    public void RemoveSelected(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (_selectedOrder.Remove(path))
        {
            UpdateOrderBadges();
            UpdateCountText();
        }
    }

    private void UpdateOrderBadges()
    {
        foreach (var kv in _cards)
        {
            var inSel = _selectedOrder.Contains(kv.Key);
            kv.Value.BorderBrush = inSel ? _selectedBrush : _normalBrush;
            kv.Value.BorderThickness = new Thickness(inSel ? 2.5 : 1.5);
        }
        foreach (var kv in _orderBadges)
        {
            var idx = _selectedOrder.IndexOf(kv.Key);
            if (idx >= 0)
            {
                kv.Value.Text = (idx + 1).ToString();
                kv.Value.Visibility = Visibility.Visible;
            }
            else kv.Value.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateCountText()
    {
        if (_selectedOrder.Count == 0)
        {
            _countText.Text = "未选择资产";
            _countText.ToolTip = null;
        }
        else
        {
            var names = string.Join(" → ", _selectedOrder.Select(Path.GetFileName));
            _countText.Text = $"已选 {_selectedOrder.Count}/{MaxCount}：{names}";
            _countText.ToolTip = $"参考图顺序：\n{string.Join("\n", _selectedOrder.Select((p, i) => $"{i + 1}. {Path.GetFileName(p)}"))}";
        }
    }

    private static Button SmallBtn(string text) => new()
    {
        Content = text, FontSize = 10.5, Padding = new Thickness(6, 3, 6, 3),
        Margin = new Thickness(0, 0, 4, 0),
        Style = Style("SecondaryButtonStyle")
    };

    private static Brush Brush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    private static Style? Style(string key) =>
        Application.Current.TryFindResource(key) as Style;
}

/// <summary>
/// 素材区（参考图 / 参考视频 / 参考音频）统一版式：
/// 顶部一行 = 左侧标题 + 数量角标，右侧操作按钮；
/// 支持两种布局：
///  - 普通模式：标题/按钮一行，其下为缩略图/标签流（ContentPanel），最底部为提示文字；
///  - 内联模式（contentInline=true）：标题 + 按钮 + 缩略图全部挤在同一「扁」行内，缩略图直接排在按钮右侧，
///    仅占地占位少，适合为提示词输入框省出纵向空间（视频生成为主）。
/// 底部提示文字 <see cref="HintText"/> 两种模式都保留。
/// </summary>
public sealed class MaterialStrip
{
    private static readonly Brush _badgeBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2));

    private readonly TextBlock _countBadge;
    private readonly TextBlock _hintText;
    private readonly WrapPanel _buttonsWrap;

    /// <summary>缩略图 / 标签流（参考图缩略图、参考视频名称、参考音频 chip 放入此面板）。</summary>
    public WrapPanel ContentPanel { get; }

    /// <summary>底部提示文字块（随内容变化由宿主更新）。</summary>
    public TextBlock HintText => _hintText;

    /// <summary>整个素材区根元素。</summary>
    public FrameworkElement Root { get; }

    /// <param name="title">素材区标题，如「参考图」。</param>
    /// <param name="defaultHint">默认提示文字。</param>
    /// <param name="icon">标题前的图标（emoji）。</param>
    public MaterialStrip(string title, string defaultHint, string? icon = "📷")
        : this(title, defaultHint, icon, contentInline: false) { }

    /// <param name="contentInline">true=缩略图就地排在按钮右侧（扁行，纵向省空间）；false=缩略图在按钮下一行。</param>
    public MaterialStrip(string title, string defaultHint, string? icon, bool contentInline)
    {
        _countBadge = new TextBlock
        {
            FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            Background = _badgeBrush, Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center, Visibility = Visibility.Collapsed
        };
        var titleText = new TextBlock
        {
            Text = $"{icon} {title}", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center
        };
        var titleWrap = new StackPanel { Orientation = Orientation.Horizontal };
        titleWrap.Children.Add(titleText);
        titleWrap.Children.Add(_countBadge);

        _buttonsWrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };

        // 头部：标题 | 按钮（| 内联内容，仅 contentInline 时存在）
        var header = new Grid();
        ContentPanel = new WrapPanel { VerticalAlignment = contentInline ? VerticalAlignment.Center : VerticalAlignment.Top };
        if (contentInline)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(titleWrap, 0);
            Grid.SetColumn(_buttonsWrap, 1);
            Grid.SetColumn(ContentPanel, 2);
        }
        else
        {
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(titleWrap, 0);
            Grid.SetColumn(_buttonsWrap, 1);
        }
        header.Children.Add(titleWrap);
        header.Children.Add(_buttonsWrap);
        if (contentInline) header.Children.Add(ContentPanel);

        _hintText = new TextBlock
        {
            Text = defaultHint, FontSize = 10.5, Foreground = Brush("TextTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap
        };

        var root = new Grid();
        if (contentInline)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 0 头部（含内联内容）
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3) });  // 1 间距
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 2 提示
            Grid.SetRow(header, 0);
            Grid.SetRow(_hintText, 2);
            root.Children.Add(header);
            root.Children.Add(_hintText);
        }
        else
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 0 标题行
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });  // 1 间距
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 2 内容流
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });  // 3 间距
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 4 提示
            Grid.SetRow(header, 0);
            Grid.SetRow(ContentPanel, 2);
            Grid.SetRow(_hintText, 4);
            root.Children.Add(header);
            root.Children.Add(ContentPanel);
            root.Children.Add(_hintText);
        }
        Root = root;
    }

    /// <summary>在标题行右侧追加一个操作按钮（等高标准）。</summary>
    public Button AddButton(string label, string tooltip, string? style = null)
    {
        var b = new Button
        {
            Content = label, FontSize = 11, Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Style = Style(style ?? "SecondaryButtonStyle"), ToolTip = tooltip
        };
        _buttonsWrap.Children.Add(b);
        return b;
    }

    /// <summary>更新标题行右侧的数量角标（0 或以下时隐藏）。</summary>
    public void SetCount(int count)
    {
        _countBadge.Text = count > 0 ? count.ToString() : "";
        _countBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush Brush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    private static Style? Style(string key) =>
        Application.Current.TryFindResource(key) as Style;
}

/// <summary>
/// 默认提示词管理窗口：列表展示、新增、编辑、删除，每条带勾选框。
/// 勾选的条目会在每次生成时自动追加到提示词末尾；未勾选的仅保存不使用。
/// 数据按 Image/Video 分类持久化在 AppConfig.DefaultImagePrompts / DefaultVideoPrompts。
/// </summary>
public sealed class DefaultPromptWindow : Window
{
    private readonly ListBox _list;
    private readonly string _key;
    private readonly List<DefaultPromptItem> _items;

    private DefaultPromptWindow(string key, string title)
    {
        _key = key;
        Title = title;
        Width = 500; Height = 440;
        MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = Brush("WindowBackgroundBrush");

        _items = GetList(key);
        _list = new ListBox
        {
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 10),
            Background = Brush("CardBackgroundBrush"),
            Foreground = Brush("TextPrimaryBrush"),
            Padding = new Thickness(6)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        RebuildList();

        var addBtn = Btn("➕ 新增", "SecondaryButtonStyle");
        var editBtn = Btn("✏️ 编辑", "SecondaryButtonStyle");
        var delBtn = Btn("🗑 删除", "SecondaryButtonStyle");
        var doneBtn = Btn("✅ 完成", "PrimaryButtonStyle");

        addBtn.Click += (_, _) =>
        {
            var dlg = new PromptEditDialog("新增默认提示词") { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ResultText)) return;
            _items.Add(new DefaultPromptItem { Text = dlg.ResultText!.Trim() });
            SaveList();
            RebuildList();
        };
        editBtn.Click += (_, _) =>
        {
            if (_list.SelectedItem is not ListBoxItem lbi || lbi.Tag is not DefaultPromptItem item)
            { MainWindow.Notify("请先选择一条提示词", success: false); return; }
            var dlg = new PromptEditDialog("编辑默认提示词", item.Text) { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ResultText)) return;
            item.Text = dlg.ResultText!.Trim();
            SaveList();
            RebuildList();
        };
        delBtn.Click += (_, _) =>
        {
            if (_list.SelectedItem is not ListBoxItem lbi || lbi.Tag is not DefaultPromptItem item)
            { MainWindow.Notify("请先选择一条提示词", success: false); return; }
            _items.Remove(item);
            SaveList();
            RebuildList();
        };
        doneBtn.Click += (_, _) => DialogResult = true;

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btnRow.Children.Add(addBtn);
        btnRow.Children.Add(editBtn);
        btnRow.Children.Add(delBtn);
        btnRow.Children.Add(doneBtn);

        var hint = new TextBlock
        {
            Text = "勾选（✓）的条目会在每次生成时自动追加到提示词末尾；未勾选的仅保存不使用。新增 / 编辑 / 删除即时保存。",
            FontSize = 10.5, Foreground = Brush("TextTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };

        // Grid 三行布局：顶部提示 / 中部列表（占满可滚动）/ 底部按钮（固定高度），
        // 长提示词只会让列表滚动，不会把按钮挤掉
        var body = new Grid { Margin = new Thickness(16) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 提示
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 1 列表
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 2 按钮
        Grid.SetRow(hint, 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(btnRow, 2);
        body.Children.Add(hint);
        body.Children.Add(_list);
        body.Children.Add(btnRow);
        Content = body;
    }

    /// <summary>打开默认提示词管理窗口（勾选式，修改即时保存）。</summary>
    public static void Show(Window? owner, string key, string title)
    {
        var win = new DefaultPromptWindow(key, title) { Owner = owner };
        win.ShowDialog();
    }

    /// <summary>
    /// 重建列表项：每条展示「编号 + 勾选框 + 提示词文本（自动换行）」。
    /// 勾选框勾选后条目在生成时自动追加；未勾选的用弱化颜色显示，方便区分。
    /// </summary>
    private void RebuildList()
    {
        _list.Items.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var num = new TextBlock
            {
                Text = $"{i + 1}",
                FontSize = 10.5,
                Foreground = Brush("TextTertiaryBrush"),
                Width = 20, TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 6, 0)
            };
            var chk = new CheckBox
            {
                IsChecked = item.Enabled,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 8, 0),
                ToolTip = "勾选后：每次生成时自动追加到提示词末尾"
            };
            var text = new TextBlock
            {
                Text = item.Text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = item.Enabled ? Brush("TextPrimaryBrush") : Brush("TextSecondaryBrush"),
                Margin = new Thickness(0, 1, 0, 1)
            };
            var captured = item;
            chk.Checked += (_, _) =>
            {
                captured.Enabled = true;
                text.Foreground = Brush("TextPrimaryBrush");
                SaveList();
            };
            chk.Unchecked += (_, _) =>
            {
                captured.Enabled = false;
                text.Foreground = Brush("TextSecondaryBrush");
                SaveList();
            };

            var row = new DockPanel { Margin = new Thickness(4, 5, 4, 5) };
            DockPanel.SetDock(num, Dock.Left);
            DockPanel.SetDock(chk, Dock.Left);
            row.Children.Add(num);
            row.Children.Add(chk);
            row.Children.Add(text);

            var lbi = new ListBoxItem
            {
                Content = row,
                Tag = item,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0)
            };
            _list.Items.Add(lbi);
        }
    }

    private static List<DefaultPromptItem> GetList(string key)
    {
        var config = FileService.LoadConfig(App.WorkRoot);
        return key == "Video" ? config.DefaultVideoPrompts : config.DefaultImagePrompts;
    }

    private void SaveList()
    {
        var config = FileService.LoadConfig(App.WorkRoot);
        if (_key == "Video") config.DefaultVideoPrompts = new List<DefaultPromptItem>(_items);
        else config.DefaultImagePrompts = new List<DefaultPromptItem>(_items);
        FileService.SaveConfig(App.WorkRoot, config);
    }

    private static Button Btn(string text, string styleKey) => new()
    {
        Content = text, FontSize = 10, Padding = new Thickness(8, 3, 8, 3),
        Height = 24, MinWidth = 52, Margin = new Thickness(0, 0, 6, 0),
        Style = GetStyle(styleKey)
    };

    private static Brush Brush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    private static Style? GetStyle(string key) =>
        Application.Current.TryFindResource(key) as Style;
}

/// <summary>
/// 多行提示词编辑对话框：用于默认提示词的新增 / 编辑。
/// 支持换行、自动换行与滚动，Ctrl+Enter 确认、Esc 取消，避免单行输入框看不到长内容的问题。
/// </summary>
public sealed class PromptEditDialog : Window
{
    public string? ResultText { get; private set; }

    public PromptEditDialog(string title, string initialText = "")
    {
        Title = title;
        Width = 560; Height = 380;
        MinWidth = 420; MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        Background = Brush("WindowBackgroundBrush");

        var textBox = new TextBox
        {
            Text = initialText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontSize = 13,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = Brush("TextPrimaryBrush"),
            Background = Brush("CardBackgroundBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            VerticalContentAlignment = VerticalAlignment.Top
        };

        var okBtn = new Button
        {
            Content = "✅ 确定", FontSize = 11, Padding = new Thickness(14, 3, 14, 3),
            Height = 26, MinWidth = 72, Margin = new Thickness(8, 0, 0, 0),
            Style = GetStyle("PrimaryButtonStyle")
        };
        var cancelBtn = new Button
        {
            Content = "取消", FontSize = 11, Padding = new Thickness(12, 3, 12, 3),
            Height = 26, MinWidth = 60,
            Style = GetStyle("SecondaryButtonStyle")
        };
        okBtn.Click += (_, _) => { ResultText = textBox.Text; DialogResult = true; };
        cancelBtn.Click += (_, _) => DialogResult = false;
        // Enter 换行，Ctrl+Enter 确认，Esc 取消
        textBox.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        };

        var titleText = new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.Bold,
            Foreground = Brush("TextPrimaryBrush")
        };
        var hint = new TextBlock
        {
            Text = "提示：Enter 换行，Ctrl+Enter 确认，Esc 取消。",
            FontSize = 10.5,
            Foreground = Brush("TextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(hint, Dock.Left);
        DockPanel.SetDock(btnRow, Dock.Right);
        footer.Children.Add(hint);
        footer.Children.Add(btnRow);

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 标题
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });                    // 1 间距
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 2 编辑区
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 3 底部
        Grid.SetRow(titleText, 0);
        Grid.SetRow(textBox, 2);
        Grid.SetRow(footer, 3);
        grid.Children.Add(titleText);
        grid.Children.Add(textBox);
        grid.Children.Add(footer);
        Content = grid;

        Loaded += (_, _) => { textBox.Focus(); textBox.SelectAll(); };
    }

    private static Brush Brush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    private static Style? GetStyle(string key) =>
        Application.Current.TryFindResource(key) as Style;
}

/// <summary>
/// 生成窗口三栏布局辅助：左栏（提示词素材）| 中央 | 右栏（项目资产）。
/// 左右栏宽度可通过 GridSplitter 拖拽调整，关闭窗口时把宽度持久化到配置，下次打开恢复；
/// 左右栏还支持一键折叠/展开（折叠后栏宽收敛为窄条，再次展开恢复折叠前宽度，即“宽度记忆”）。
/// </summary>
public static class GenPanelLayout
{
    public const double LeftMin = 215;
    public const double RightMin = 280;
    public const double CenterMin = 360;
    /// <summary>折叠后的窄条宽度</summary>
    public const double CollapsedStrip = 26;

    /// <summary>单个侧边栏的折叠状态与记忆宽度</summary>
    private sealed class SideState
    {
        public bool Collapsed;
        public GridLength ExpandedWidth = new(1, GridUnitType.Star);
        public double ExpandedPx;
        public double MinWidth;
    }

    public static Grid CreateThreeColumn(Window win,
        PromptPanel? left, FrameworkElement center, AssetPanel? right)
    {
        var config = FileService.LoadConfig(App.WorkRoot);
        // 三列都用 Star 权重（按保存的实际宽度比例），随窗口伸缩，永不溢出
        var grid = new Grid();
        var leftCol = new ColumnDefinition { Width = new GridLength(config.GenLeftPanelWidth, GridUnitType.Star), MinWidth = LeftMin };   // 0 左栏
        var centerCol = new ColumnDefinition { Width = new GridLength(config.GenCenterPanelWidth, GridUnitType.Star), MinWidth = CenterMin }; // 2 中央
        var rightCol = new ColumnDefinition { Width = new GridLength(config.GenRightPanelWidth, GridUnitType.Star), MinWidth = RightMin }; // 4 右栏
        grid.ColumnDefinitions.Add(leftCol);                                                                                               // 0 左栏
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                                                      // 1 分割线
        grid.ColumnDefinitions.Add(centerCol);                                                                                             // 2 中央
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                                                      // 3 分割线
        grid.ColumnDefinitions.Add(rightCol);                                                                                              // 4 右栏

        // 折叠/展开按钮放在面板标题栏内部；折叠时面板隐藏，由窄条上的同步按钮恢复
        var leftState = new SideState { MinWidth = LeftMin };
        var rightState = new SideState { MinWidth = RightMin };

        var leftToggle = MakeToggle();
        var leftStrip = MakeStrip(isLeft: true, out var leftStripBtn);
        if (left != null)
        {
            left.SetCollapseToggle(leftToggle);
            WireCollapse(leftToggle, leftStripBtn, leftCol, leftState, left.Root, leftStrip, isLeft: true);
        }

        var rightToggle = MakeToggle();
        var rightStrip = MakeStrip(isLeft: false, out var rightStripBtn);
        if (right != null)
        {
            right.SetCollapseToggle(rightToggle);
            WireCollapse(rightToggle, rightStripBtn, rightCol, rightState, right.Root, rightStrip, isLeft: false);
        }

        var leftDock = new Grid();
        if (left != null)
        {
            Grid.SetColumn(leftStrip, 0);
            Grid.SetRowSpan(leftStrip, 2);
            leftDock.Children.Add(left.Root);
            leftDock.Children.Add(leftStrip);
        }
        var rightDock = new Grid();
        if (right != null)
        {
            Grid.SetColumn(rightStrip, 0);
            Grid.SetRowSpan(rightStrip, 2);
            rightDock.Children.Add(right.Root);
            rightDock.Children.Add(rightStrip);
        }

        Grid.SetColumn(leftDock, 0);
        Grid.SetColumn(center, 2);
        Grid.SetColumn(rightDock, 4);
        grid.Children.Add(leftDock);
        grid.Children.Add(center);
        grid.Children.Add(rightDock);

        var s1 = MakeSplitter();
        Grid.SetColumn(s1, 1);
        var s2 = MakeSplitter();
        Grid.SetColumn(s2, 3);
        grid.Children.Add(s1);
        grid.Children.Add(s2);

        // 关闭窗口时保存三栏当前宽度（折叠时保存折叠前的展开宽度，保证“宽度记忆”）
        win.Closed += (_, _) =>
        {
            try
            {
                var cfg = FileService.LoadConfig(App.WorkRoot);
                cfg.GenLeftPanelWidth = Math.Max(LeftMin, leftState.Collapsed ? leftState.ExpandedPx : leftCol.ActualWidth);
                cfg.GenCenterPanelWidth = Math.Max(CenterMin, centerCol.ActualWidth);
                cfg.GenRightPanelWidth = Math.Max(RightMin, rightState.Collapsed ? rightState.ExpandedPx : rightCol.ActualWidth);
                FileService.SaveConfig(App.WorkRoot, cfg);
            }
            catch { /* 保存失败不影响使用 */ }
        };
        return grid;
    }

    /// <summary>创建折叠/展开小按钮（放进面板标题栏内）。</summary>
    private static Button MakeToggle() => new()
    {
        Width = 22, Height = 22, Padding = new Thickness(0),
        FontSize = 9, Style = Application.Current.TryFindResource("SecondaryButtonStyle") as Style,
        Cursor = Cursors.Hand
    };

    /// <summary>创建折叠后的窄条（26px 宽，内含同步按钮，点击恢复展开）。</summary>
    private static Border MakeStrip(bool isLeft, out Button stripBtn)
    {
        stripBtn = new Button
        {
            Width = 22, Height = 22, Padding = new Thickness(0),
            FontSize = 9, Style = Application.Current.TryFindResource("SecondaryButtonStyle") as Style,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0)
        };
        return new Border
        {
            Width = CollapsedStrip,
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = (Brush)(Application.Current.TryFindResource("BorderBrush") ?? Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Visibility = Visibility.Collapsed,
            Child = stripBtn
        };
    }

    /// <summary>绑定折叠/展开：切换栏宽并记忆展开宽度（标题栏按钮与窄条按钮同步）。</summary>
    private static void WireCollapse(Button headerToggle, Button stripBtn, ColumnDefinition col,
        SideState state, FrameworkElement panel, Border strip, bool isLeft)
    {
        string closeGlyph = isLeft ? "◀" : "▶";   // 展开时：左栏向左收，右栏向右收
        string openGlyph = isLeft ? "▶" : "◀";    // 折叠时：点击恢复展开
        headerToggle.Content = closeGlyph;
        stripBtn.Content = openGlyph;
        headerToggle.ToolTip = isLeft ? "折叠 / 展开左侧提示词素材栏" : "折叠 / 展开右侧项目资产栏";
        stripBtn.ToolTip = isLeft ? "展开左侧提示词素材栏" : "展开右侧项目资产栏";

        void Toggle()
        {
            if (!state.Collapsed)
            {
                state.ExpandedWidth = col.Width;
                state.ExpandedPx = Math.Max(1, col.ActualWidth);
                col.MinWidth = 0;
                col.Width = new GridLength(CollapsedStrip);
                panel.Visibility = Visibility.Collapsed;
                strip.Visibility = Visibility.Visible;
                headerToggle.Content = openGlyph;
                stripBtn.Content = closeGlyph;
                state.Collapsed = true;
            }
            else
            {
                col.MinWidth = state.MinWidth;
                col.Width = state.ExpandedWidth;
                panel.Visibility = Visibility.Visible;
                strip.Visibility = Visibility.Collapsed;
                headerToggle.Content = closeGlyph;
                stripBtn.Content = openGlyph;
                state.Collapsed = false;
            }
        }
        headerToggle.Click += (_, _) => Toggle();
        stripBtn.Click += (_, _) => Toggle();
    }

    private static GridSplitter MakeSplitter() => new()
    {
        Width = 6,
        Margin = new Thickness(3, 0, 3, 0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Stretch,
        ResizeDirection = GridResizeDirection.Columns,
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        ShowsPreview = false,
        Background = new SolidColorBrush(Color.FromArgb(0x18, 0x8A, 0x8A, 0x8A)),
        ToolTip = "拖拽调整栏宽"
    };
}
