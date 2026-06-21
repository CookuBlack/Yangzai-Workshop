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
    private bool _initialized;
    private DateTime _selectedDate = DateTime.Today;

    public StatsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        _platformData["抖音"] = new List<DailyStats>();
        _platformData["快手"] = new List<DailyStats>();
        _platformData["Bilibili"] = new List<DailyStats>();
        LoadData();
        SwitchPlatform("抖音");
        DateBtnText.Text = _selectedDate.ToString("yyyy/MM/dd");
    }

    private int _calYear, _calMonth;

    private void DatePopup_Opened(object? sender, EventArgs e)
    {
        _calYear = _selectedDate.Year;
        _calMonth = _selectedDate.Month;
        RefreshCalendar();
    }

    private void RefreshCalendar()
    {
        CalMonthLabel.Text = $"{_calYear}年{_calMonth}月";
        CalDayGrid.Children.Clear();
        CalDayGrid.ColumnDefinitions.Clear();
        CalDayGrid.RowDefinitions.Clear();

        for (int c = 0; c < 7; c++)
            CalDayGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < 6; r++)
            CalDayGrid.RowDefinitions.Add(new RowDefinition());

        var firstDay = new DateTime(_calYear, _calMonth, 1);
        int startDow = (int)firstDay.DayOfWeek; // 0=Sun
        var today = DateTime.Today;

        for (int day = 1; day <= DateTime.DaysInMonth(_calYear, _calMonth); day++)
        {
            var date = new DateTime(_calYear, _calMonth, day);
            int pos = startDow + day - 1;
            int r = pos / 7, c = pos % 7;

            bool isSelected = date == _selectedDate.Date;
            bool isToday = date == today;

            var btn = new Button
            {
                Content = day.ToString(),
                Tag = date,
                FontSize = 12,
                FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei"),
                Background = isSelected
                    ? (Brush)FindResource("PrimaryBrush")
                    : Brushes.Transparent,
                Foreground = isSelected
                    ? Brushes.White
                    : isToday
                        ? (Brush)FindResource("AccentBrush")
                        : (Brush)FindResource("TextPrimaryBrush"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2),
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(1)
            };

            btn.Click += DayBtn_Click;
            Grid.SetRow(btn, r);
            Grid.SetColumn(btn, c);
            CalDayGrid.Children.Add(btn);
        }
    }

    private void DayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DateTime dt)
        {
            _selectedDate = dt;
            DateBtnText.Text = _selectedDate.ToString("yyyy/MM/dd");
            DatePopup.IsOpen = false;
            DateBtn.IsChecked = false;
        }
    }

    private void CalPrevMonth_Click(object sender, RoutedEventArgs e)
    {
        if (_calMonth == 1) { _calMonth = 12; _calYear--; }
        else _calMonth--;
        RefreshCalendar();
    }

    private void CalNextMonth_Click(object sender, RoutedEventArgs e)
    {
        if (_calMonth == 12) { _calMonth = 1; _calYear++; }
        else _calMonth++;
        RefreshCalendar();
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
        DouyinBtn.Background = platform == "抖音" ? new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#D0D0D0")) : Brushes.Transparent;
        DouyinBtn.Foreground = platform == "抖音" ? new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#1A1A1A")) : (Brush)FindResource("TextPrimaryBrush");
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
        DateTime[] dates = sorted.Select(d => d.Date).ToArray();
        double[] plays = sorted.Select(d => (double)d.Plays).ToArray();
        double[] likes = sorted.Select(d => (double)d.Likes).ToArray();
        double[] comments = sorted.Select(d => (double)d.Comments).ToArray();

        QuickPlot(PlayChart, dates, plays, WpfColors.DodgerBlue);
        QuickPlot(LikeChart, dates, likes, WpfColors.Orange);
        QuickPlot(CommentChart, dates, comments, WpfColors.MediumSeaGreen);
    }

    private void QuickPlot(WpfPlot chart, DateTime[] dates, double[] y, WpfColor lineColor)
    {
        var plt = chart.Plot;
        plt.Clear();
        double[] xs = dates.Select(d => d.ToOADate()).ToArray();
        var line = plt.Add.ScatterLine(xs, y);
        line.Color = new ScottPlot.Color(lineColor.R, lineColor.G, lineColor.B);
        line.MarkerSize = 4;
        line.LineWidth = 2;
        plt.Axes.DateTimeTicksBottom();
        plt.Axes.AutoScale();
        chart.Refresh();
    }

    private void ClearChart(WpfPlot chart)
    {
        chart.Plot.Clear();
        chart.Refresh();
    }

    private void RefreshDataList()
    {
        DataList.Items.Clear();
        var data = _platformData[_currentPlatform];
        var sorted = data.OrderBy(d => d.Date).ToList();
        int idx = 0;
        foreach (var d in sorted)
        {
            DataList.Items.Add(CreateDataListItem(d, idx++));
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
            Text = $"{d.Date:yyyy-MM-dd}  |  {FormatNum(d.Plays)}  {FormatNum(d.Likes)}  {FormatNum(d.Comments)}",
            FontSize = 10, FontFamily = new FontFamily("Microsoft YaHei"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
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
        if (sender is Border b) { _dragSource = b; _dragStart = e.GetPosition(null); _isDragging = false; }
    }

    private void DataItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSource == null || _isDragging) return;
        if (Math.Abs((e.GetPosition(null) - _dragStart).Y) > 6)
        {
            _isDragging = true; _dragSource.Opacity = 0.5;
            Mouse.Capture(_dragSource, CaptureMode.Element);
        }
    }

    private void DataItem_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging || _dragSource == null) { _dragSource = null; return; }
        _dragSource.Opacity = 1; Mouse.Capture(null);

        var pos = e.GetPosition(DataList);
        var items = DataList.Items.OfType<FrameworkElement>().ToList();
        int targetIdx = -1; double bestDist = double.MaxValue;
        for (int i = 0; i < items.Count; i++)
        {
            var itemPos = items[i].TransformToAncestor(DataList).Transform(new Point(0, 0));
            double dist = Math.Abs(pos.Y - (itemPos.Y + items[i].ActualHeight / 2));
            if (dist < bestDist) { bestDist = dist; targetIdx = i; }
        }

        if (targetIdx >= 0 && _dragSource.Tag is DailyStats src)
        {
            var list = _platformData[_currentPlatform];
            int srcIdx = list.IndexOf(src);
            if (srcIdx >= 0 && targetIdx != srcIdx)
            {
                list.RemoveAt(srcIdx);
                int insertIdx = Math.Clamp(targetIdx > srcIdx ? targetIdx - 1 : targetIdx, 0, list.Count);
                list.Insert(insertIdx, src);
                SaveData(); RefreshAll();
            }
        }
        _dragSource = null; _isDragging = false;
    }

    private static string FormatNum(long n)
    {
        if (n >= 10000) return $"{n / 10000.0:F1}万";
        if (n >= 1000) return $"{n / 1000.0:F1}k";
        return n.ToString();
    }

    private void AddData_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDate == default) { MessageDialog.Show("提示", "请选择日期！"); return; }
        if (_selectedDate.Date > DateTime.Today) { MessageDialog.Show("提示", "日期不能是未来日期！"); return; }

        if (!long.TryParse(PlaysInput.Text.Trim(), out var plays) || plays < 0)
        { MessageDialog.Show("提示", "播放量必须是非负整数！"); return; }
        if (!long.TryParse(LikesInput.Text.Trim(), out var likes) || likes < 0)
        { MessageDialog.Show("提示", "点赞量必须是非负整数！"); return; }
        if (!long.TryParse(CommentsInput.Text.Trim(), out var comments) || comments < 0)
        { MessageDialog.Show("提示", "评论量必须是非负整数！"); return; }

        var list = _platformData[_currentPlatform];
        var existing = list.FirstOrDefault(d => d.Date.Date == _selectedDate.Date);
        if (existing != null) { existing.Plays = plays; existing.Likes = likes; existing.Comments = comments; }
        else list.Add(new DailyStats { Date = _selectedDate, Plays = plays, Likes = likes, Comments = comments });

        SaveData(); RefreshAll();
        _selectedDate = DateTime.Today; DateBtnText.Text = _selectedDate.ToString("yyyy/MM/dd");
        PlaysInput.Text = LikesInput.Text = CommentsInput.Text = "";
    }

    private void TogglePanel_Click(object sender, RoutedEventArgs e)
    {
        if (_panelExpanded) { RightPanel.Visibility = Visibility.Collapsed; RightPanelCol.Width = GridLength.Auto; }
        else { RightPanel.Visibility = Visibility.Visible; RightPanelCol.Width = new GridLength(240); }
        _panelExpanded = !_panelExpanded;
    }

    private void DouyinBtn_Click(object sender, RoutedEventArgs e) => SwitchPlatform("抖音");
    private void KuaishouBtn_Click(object sender, RoutedEventArgs e) => SwitchPlatform("快手");
    private void BilibiliBtn_Click(object sender, RoutedEventArgs e) => SwitchPlatform("Bilibili");
}
