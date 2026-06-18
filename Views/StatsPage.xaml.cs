using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YangzaiWorkshop.Models;
using ScottPlot;
using ScottPlot.WPF;
using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;

namespace YangzaiWorkshop.Views;

public partial class StatsPage : UserControl
{
    private readonly Dictionary<string, List<DailyStats>> _platformData = new();
    private string _currentPlatform = "抖音";
    private string _dataFilePath => Path.Combine(App.WorkRoot, "platform_stats.json");

    private bool _panelExpanded = true;

    public StatsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _platformData["抖音"] = new List<DailyStats>();
        _platformData["快手"] = new List<DailyStats>();
        _platformData["Bilibili"] = new List<DailyStats>();
        LoadData();
        SwitchPlatform("抖音");
    }

    private void LoadData()
    {
        try
        {
            if (File.Exists(_dataFilePath))
            {
                var json = File.ReadAllText(_dataFilePath);
                var saved = JsonSerializer.Deserialize<Dictionary<string, List<DailyStats>>>(json);
                if (saved != null)
                    foreach (var kv in saved)
                        if (_platformData.ContainsKey(kv.Key))
                            _platformData[kv.Key] = kv.Value.OrderBy(d => d.Date).ToList();
            }
        }
        catch { }
    }

    private void SaveData()
    {
        try
        {
            var json = JsonSerializer.Serialize(_platformData, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_dataFilePath, json);
        }
        catch { }
    }

    private void SwitchPlatform(string platform)
    {
        _currentPlatform = platform;
        DouyinBtn.Background = platform == "抖音" ? (Brush)FindResource("PrimaryBrush") : Brushes.Transparent;
        DouyinBtn.Foreground = platform == "抖音" ? Brushes.White : (Brush)FindResource("TextPrimaryBrush");
        KuaishouBtn.Background = platform == "快手" ? new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#FF6A00")) : Brushes.Transparent;
        KuaishouBtn.Foreground = platform == "快手" ? Brushes.White : (Brush)FindResource("TextPrimaryBrush");
        BilibiliBtn.Background = platform == "Bilibili" ? new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#FB7299")) : Brushes.Transparent;
        BilibiliBtn.Foreground = platform == "Bilibili" ? Brushes.White : (Brush)FindResource("TextPrimaryBrush");
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshCharts();
        RefreshDataList();
    }

    private void RefreshCharts()
    {
        var data = _platformData.GetValueOrDefault(_currentPlatform, new List<DailyStats>());
        if (data.Count == 0)
        {
            ClearChart(PlayChart); ClearChart(LikeChart); ClearChart(CommentChart);
            return;
        }
        var sorted = data.OrderBy(d => d.Date).ToList();
        // 用 DateTime 轴显示完整日期
        DateTime[] dates = sorted.Select(d => d.Date).ToArray();

        PlotChart(PlayChart, dates, sorted.Select(d => (double)d.Plays).ToArray(), WpfColors.DodgerBlue);
        PlotChart(LikeChart, dates, sorted.Select(d => (double)d.Likes).ToArray(), WpfColors.Orange);
        PlotChart(CommentChart, dates, sorted.Select(d => (double)d.Comments).ToArray(), WpfColors.MediumSeaGreen);
    }

    private void PlotChart(WpfPlot chart, DateTime[] dates, double[] y, WpfColor lineColor)
    {
        chart.Plot.Clear();
        double[] xs = dates.Select(d => d.ToOADate()).ToArray();
        var line = chart.Plot.Add.ScatterLine(xs, y);
        line.Color = new ScottPlot.Color(lineColor.R, lineColor.G, lineColor.B);
        line.MarkerSize = 4;
        line.LineWidth = 2;

        // 美化：网格线
        chart.Plot.Grid.MajorLineColor = ScottPlot.Colors.Gray.WithAlpha(0.2);
        chart.Plot.Grid.MajorLineWidth = 0.5f;
        chart.Plot.Axes.DateTimeTicksBottom();
        chart.Plot.Axes.AutoScale();
        chart.Refresh();
    }

    private void ClearChart(WpfPlot chart)
    {
        chart.Plot.Clear();
        chart.Plot.Grid.MajorLineColor = ScottPlot.Colors.Gray.WithAlpha(0.2);
        chart.Plot.Grid.MajorLineWidth = 0.5f;
        chart.Plot.Axes.AutoScale();
        chart.Refresh();
    }

    // ===== 数据列表（支持拖拽排序） =====
    private void RefreshDataList()
    {
        DataList.Items.Clear();
        var data = _platformData[_currentPlatform];
        var sorted = data.OrderBy(d => d.Date).ToList();
        int idx = 0;
        foreach (var d in sorted)
        {
            var item = CreateDataListItem(d, idx++);
            DataList.Items.Add(item);
        }
    }

    private FrameworkElement CreateDataListItem(DailyStats d, int index)
    {
        var item = new Border
        {
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(6, 4, 6, 4),
            CornerRadius = new CornerRadius(4),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            Cursor = Cursors.Hand
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new TextBlock
        {
            Text = $"{d.Date:yyyy-MM-dd}  |  ▸{FormatNum(d.Plays)}  ♥{FormatNum(d.Likes)}  ✎{FormatNum(d.Comments)}",
            FontSize = 10, FontFamily = new FontFamily("Microsoft YaHei"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Tag = index
        };
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var delBtn = new Button
        {
            Content = "X", FontSize = 9, Width = 20, Height = 20,
            Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("DangerBrush"),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            Tag = d
        };
        delBtn.Click += (_, _) =>
        {
            _platformData[_currentPlatform].Remove(d);
            SaveData(); RefreshAll();
        };
        delBtn.MouseEnter += (s, _) => ((Button)s).Background = (Brush)FindResource("HoverBrush");
        delBtn.MouseLeave += (s, _) => ((Button)s).Background = Brushes.Transparent;
        Grid.SetColumn(delBtn, 1);
        grid.Children.Add(delBtn);

        item.Child = grid;

        // 拖拽排序
        item.PreviewMouseLeftButtonDown += DataItem_MouseDown;
        item.PreviewMouseMove += DataItem_MouseMove;
        item.PreviewMouseLeftButtonUp += DataItem_MouseUp;
        item.Tag = d;

        return item;
    }

    private Border? _dragSource;
    private Point _dragStart;
    private bool _isDragging;

    private void DataItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b)
        {
            _dragSource = b;
            _dragStart = e.GetPosition(null);
            _isDragging = false;
        }
    }

    private void DataItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSource == null || _isDragging) return;
        var diff = e.GetPosition(null) - _dragStart;
        if (Math.Abs(diff.Y) > 6)
        {
            _isDragging = true;
            _dragSource.Opacity = 0.5;
            Mouse.Capture(_dragSource, CaptureMode.Element);
        }
    }

    private void DataItem_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging || _dragSource == null) { _dragSource = null; return; }
        _dragSource.Opacity = 1;
        Mouse.Capture(null);

        // 找到释放位置
        var pos = e.GetPosition(DataList);
        var items = DataList.Items.OfType<FrameworkElement>().ToList();
        int targetIdx = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < items.Count; i++)
        {
            var itemPos = items[i].TransformToAncestor(DataList).Transform(new Point(0, 0));
            double midY = itemPos.Y + items[i].ActualHeight / 2;
            double dist = Math.Abs(pos.Y - midY);
            if (dist < bestDist) { bestDist = dist; targetIdx = i; }
        }

        if (targetIdx >= 0 && _dragSource.Tag is DailyStats src && src != null)
        {
            var list = _platformData[_currentPlatform];
            int srcIdx = list.IndexOf(src);
            if (srcIdx >= 0 && targetIdx != srcIdx)
            {
                list.RemoveAt(srcIdx);
                // 调整索引
                int insertIdx = targetIdx > srcIdx ? targetIdx - 1 : targetIdx;
                insertIdx = Math.Clamp(insertIdx, 0, list.Count);
                list.Insert(insertIdx, src);
                SaveData(); RefreshAll();
            }
        }
        _dragSource = null;
        _isDragging = false;
    }

    private static string FormatNum(long n)
    {
        if (n >= 10000) return $"{n / 10000.0:F1}万";
        if (n >= 1000) return $"{n / 1000.0:F1}k";
        return n.ToString();
    }

    // ===== 添加数据 =====
    private void AddData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dateStr = DateInput.Text.Trim();
            DateTime date = string.IsNullOrEmpty(dateStr) ? DateTime.Today : DateTime.Parse(dateStr);
            if (!long.TryParse(PlaysInput.Text.Trim(), out var plays)) plays = 0;
            if (!long.TryParse(LikesInput.Text.Trim(), out var likes)) likes = 0;
            if (!long.TryParse(CommentsInput.Text.Trim(), out var comments)) comments = 0;

            var list = _platformData[_currentPlatform];
            var existing = list.FirstOrDefault(d => d.Date.Date == date.Date);
            if (existing != null) { existing.Plays = plays; existing.Likes = likes; existing.Comments = comments; }
            else list.Add(new DailyStats { Date = date, Plays = plays, Likes = likes, Comments = comments });

            SaveData(); RefreshAll();
            DateInput.Text = ""; PlaysInput.Text = ""; LikesInput.Text = ""; CommentsInput.Text = "";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"数据格式错误：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ===== 面板收起/展开 =====
    private void TogglePanel_Click(object sender, RoutedEventArgs e)
    {
        if (_panelExpanded)
        {
            RightPanel.Visibility = Visibility.Collapsed;
            RightPanelCol.Width = GridLength.Auto;
        }
        else
        {
            RightPanel.Visibility = Visibility.Visible;
            RightPanelCol.Width = new GridLength(240, GridUnitType.Pixel);
        }
        _panelExpanded = !_panelExpanded;
    }

    // ===== 平台切换 =====
    private void DouyinBtn_Click(object sender, RoutedEventArgs e) => SwitchPlatform("抖音");
    private void KuaishouBtn_Click(object sender, RoutedEventArgs e) => SwitchPlatform("快手");
    private void BilibiliBtn_Click(object sender, RoutedEventArgs e) => SwitchPlatform("Bilibili");
}
