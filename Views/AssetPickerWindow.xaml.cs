using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

/// <summary>
/// 项目图片资产选择器：展示一组本地图片（缩略图 + 文件名），
/// 点击选中（高亮）、双击或“使用此图”确认，返回选中的完整路径。
/// 窗口立即显示，缩略图按批次异步加载，避免大量图片阻塞 UI。
/// </summary>
public partial class AssetPickerWindow : Window
{
    private readonly Dictionary<string, Border> _cards = new();
    private readonly Dictionary<string, TextBlock> _orderBadges = new();
    private readonly List<string> _selectedOrder = new();
    private static readonly Brush _normalBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly Brush _selectedBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2));

    /// <summary>单批加载的缩略图数量</summary>
    private const int BatchSize = 12;
    /// <summary>最多展示的图片数量（防止超大项目卡顿）</summary>
    private const int MaxCards = 300;

    private readonly List<string> _allPaths = new();
    private int _loadIndex;

    /// <summary>是否为多选模式（多选时按点击顺序排序，返回 OrderedPaths）</summary>
    private readonly bool _multiSelect;

    /// <param name="imagePaths">可选的项目内图片文件完整路径集合</param>
    /// <param name="title">窗口标题</param>
    /// <param name="multiSelect">是否支持多选并按点击顺序排序（false=单选，兼容旧调用）</param>
    public AssetPickerWindow(IReadOnlyList<string> imagePaths, string title = "选择项目图片", bool multiSelect = false)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        _multiSelect = multiSelect;
        if (multiSelect)
        {
            HintText.Text = imagePaths.Count == 0
                ? "当前项目没有可用的图片资产，请先通过 AI 生图或添加图片生成素材。"
                : $"共 {imagePaths.Count} 张图片，正在加载…";
            ConfirmButton.Content = "确定使用选中";
        }

        // 去重（大小写不敏感）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in imagePaths)
        {
            var key = p.Replace('\\', '/').ToLowerInvariant();
            if (seen.Add(key)) _allPaths.Add(p);
        }

        HintText.Text = _allPaths.Count == 0
            ? "当前项目没有可用的图片资产，请先通过 AI 生图或添加图片生成素材。"
            : multiSelect
                ? $"共 {_allPaths.Count} 张图片，正在加载…（点击多次选择，按点击顺序排序）"
                : $"共 {_allPaths.Count} 张图片，正在加载…";

        Loaded += (_, _) => BeginLoadThumbs();
    }

    /// <summary>窗口显示后开始分批异步加载缩略图，UI 保持响应</summary>
    private void BeginLoadThumbs()
    {
        _loadIndex = 0;
        Dispatcher.BeginInvoke(new Action(LoadNextBatch), DispatcherPriority.Background);
    }

    private void LoadNextBatch()
    {
        for (int i = 0; i < BatchSize && _loadIndex < _allPaths.Count && _cards.Count < MaxCards; i++, _loadIndex++)
        {
            var path = _allPaths[_loadIndex];
            if (!File.Exists(path)) continue;
            try
            {
                var card = CreateCard(path);
                AssetPanel.Children.Add(card);
                _cards[path] = card;
            }
            catch { /* 跳过无法解码的图片 */ }
        }

        // 继续下一批（后台优先级，不阻塞界面）；超限或加载完则更新提示
        if (_loadIndex < _allPaths.Count && _cards.Count < MaxCards)
        {
            Dispatcher.BeginInvoke(new Action(LoadNextBatch), DispatcherPriority.Background);
        }
        else
        {
            HintText.Text = _cards.Count == 0
                ? "当前项目没有可用的图片资产，请先通过 AI 生图或添加图片生成素材。"
                : _allPaths.Count > MaxCards
                    ? $"共 {_allPaths.Count} 张图片，已展示前 {MaxCards} 张（加载完成）。"
                    : $"共 {_cards.Count} 张图片：点击选中，双击或点击“使用此图”确认。";
        }
    }

    /// <summary>创建一张可点击的图片卡片（Border + 缩略图 + 文件名）</summary>
    private Border CreateCard(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 220;
        bmp.EndInit();
        bmp.Freeze();

        var img = new Image
        {
            Source = bmp, Stretch = Stretch.Uniform,
            Margin = new Thickness(6, 6, 6, 4)
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var name = new TextBlock
        {
            Text = System.IO.Path.GetFileName(path),
            FontSize = 10.5,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(6, 0, 6, 6),
            ToolTip = path
        };
        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(img, 0);
        Grid.SetRow(name, 1);
        inner.Children.Add(img);
        inner.Children.Add(name);

        // 多选时右上角序号徽标
        var orderBadge = new TextBlock
        {
            FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2)),
            Padding = new Thickness(5, 1, 5, 1),
            Visibility = Visibility.Collapsed
        };
        inner.Children.Add(orderBadge);

        var card = new Border
        {
            Width = 130, Height = 150,
            Margin = new Thickness(0, 0, 10, 10),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = _normalBrush,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            Tag = path,
            Child = inner
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            ToggleSelect(path);
            if (!_multiSelect && e.ClickCount >= 2) ConfirmAndClose();
        };
        _orderBadges[path] = orderBadge;
        return card;
    }

    /// <summary>切换卡片选中状态；多选时维护点击顺序，单选时互斥</summary>
    private void ToggleSelect(string path)
    {
        if (!_multiSelect)
        {
            SelectSingle(path);
            return;
        }

        // 已选中 → 取消并移除其序号
        if (_selectedOrder.Contains(path))
        {
            var idx = _selectedOrder.IndexOf(path);
            _selectedOrder.RemoveAt(idx);
            UpdateOrderBadges();
            UpdateCountText();
            return;
        }

        _selectedOrder.Add(path);
        UpdateOrderBadges();
        UpdateCountText();
    }

    /// <summary>单选语义：唯一选中</summary>
    private void SelectSingle(string path)
    {
        _selectedOrder.Clear();
        _selectedOrder.Add(path);
        SelectedText.Text = $"已选：{System.IO.Path.GetFileName(path)}";
        foreach (var kv in _cards)
        {
            kv.Value.BorderBrush = _normalBrush;
            kv.Value.BorderThickness = new Thickness(1.5);
        }
        if (_cards.TryGetValue(path, out var sel))
        {
            sel.BorderBrush = _selectedBrush;
            sel.BorderThickness = new Thickness(2.5);
        }
        if (_orderBadges.TryGetValue(path, out var b)) b.Visibility = Visibility.Visible;
    }

    /// <summary>刷新所有卡片高亮与序号徽标（多选）</summary>
    private void UpdateOrderBadges()
    {
        foreach (var kv in _cards)
        {
            var inSel = _selectedOrder.Contains(kv.Key);
            kv.Value.BorderBrush = inSel ? _selectedBrush : _normalBrush;
            kv.Value.BorderThickness = new Thickness(inSel ? 2.5 : 1.5);
        }
        for (int i = 0; i < _selectedOrder.Count; i++)
        {
            if (_orderBadges.TryGetValue(_selectedOrder[i], out var b))
            {
                b.Text = (i + 1).ToString();
                b.Visibility = Visibility.Visible;
            }
        }
        foreach (var kv in _orderBadges)
        {
            if (!_selectedOrder.Contains(kv.Key)) kv.Value.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateCountText()
    {
        if (_selectedOrder.Count == 0)
        {
            SelectedText.Text = "未选择";
            return;
        }
        SelectedText.Text = $"已选 {_selectedOrder.Count} 张（按选择顺序排列，序号见卡片右上角）";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => ConfirmAndClose();

    private void ConfirmAndClose()
    {
        DialogResult = _selectedOrder.Count > 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>单选时返回选中图片路径；未选择返回 null</summary>
    public string? SelectedPath => DialogResult == true ? (_selectedOrder.Count > 0 ? _selectedOrder[0] : null) : null;

    /// <summary>多选时返回按点击顺序排列的图片路径列表</summary>
    public IReadOnlyList<string> OrderedPaths => DialogResult == true ? _selectedOrder : (IReadOnlyList<string>)new List<string>();
}
