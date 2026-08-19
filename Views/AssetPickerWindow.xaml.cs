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
    private string? _selected;
    private static readonly Brush _normalBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly Brush _selectedBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2));

    /// <summary>单批加载的缩略图数量</summary>
    private const int BatchSize = 12;
    /// <summary>最多展示的图片数量（防止超大项目卡顿）</summary>
    private const int MaxCards = 300;

    private readonly List<string> _allPaths = new();
    private int _loadIndex;

    /// <param name="imagePaths">可选的项目内图片文件完整路径集合</param>
    /// <param name="title">窗口标题</param>
    public AssetPickerWindow(IReadOnlyList<string> imagePaths, string title = "选择项目图片")
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;

        // 去重（大小写不敏感）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in imagePaths)
        {
            var key = p.Replace('\\', '/').ToLowerInvariant();
            if (seen.Add(key)) _allPaths.Add(p);
        }

        HintText.Text = _allPaths.Count == 0
            ? "当前项目没有可用的图片资产，请先通过 AI 生图或添加图片生成素材。"
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
            SelectCard(path);
            if (e.ClickCount >= 2) ConfirmAndClose();
        };
        return card;
    }

    private void SelectCard(string path)
    {
        _selected = path;
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
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => ConfirmAndClose();

    private void ConfirmAndClose()
    {
        DialogResult = _selected != null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>返回选中的图片完整路径；未选择返回 null</summary>
    public string? SelectedPath => DialogResult == true ? _selected : null;
}
