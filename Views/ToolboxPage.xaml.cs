using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

public partial class ToolboxPage : UserControl
{
    public ToolboxPage()
    {
        InitializeComponent();
    }

    private void TrashCard_Click(object sender, MouseButtonEventArgs e) => OpenTrashWindow();

    private Window? _trashWindow;

    private void OpenTrashWindow()
    {
        _trashWindow = new Window
        {
            Title = "回收站",
            Width = 520, Height = 500,
            MinWidth = 360, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = true,
            Owner = Application.Current.MainWindow,
            Background = (Brush)Application.Current.FindResource("WindowBackgroundBrush")
        };

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 标题
        var title = new TextBlock
        {
            Text = "回收站", FontSize = 18, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(title);

        // 操作栏
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(toolbar, 1);
        var countText = new TextBlock
        {
            Text = "已删除文件（30天自动清理）", FontSize = 12,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        toolbar.Children.Add(countText);
        var emptyBtn = new Button
        {
            Content = "清空回收站", FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(12, 0, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("DangerBrush"),
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle")
        };
        toolbar.Children.Add(emptyBtn);
        root.Children.Add(toolbar);

        // 列表
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        var trashList = new ItemsControl();
        scroll.Content = trashList;
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        // 绑定清空按钮事件
        emptyBtn.Click += (_, _) =>
        {
            if (MessageDialog.Confirm("确认", "确定清空回收站？此操作不可撤销。"))
            { FileService.EmptyTrash(App.WorkRoot); LoadTrashList(trashList, countText); }
        };

        _trashWindow.Content = root;
        _trashWindow.Closed += (_, _) =>
        {
            _trashWindow = null;
            // 修复 AllowsTransparency 主窗口关闭子窗口时被最小化的问题
            var mw = Application.Current.MainWindow;
            if (mw != null)
            {
                if (mw.WindowState == WindowState.Minimized)
                    mw.WindowState = WindowState.Normal;
                mw.Activate();
            }
        }; 
        LoadTrashList(trashList, countText);
        _trashWindow.Show();
    }

    private void LoadTrashList(ItemsControl list, TextBlock countText)
    {
        list.Items.Clear();
        var items = FileService.GetTrashItems(App.WorkRoot);
        countText.Text = items.Count == 0
            ? "已删除文件（30天自动清理）"
            : $"已删除 {items.Count} 个文件（30天自动清理）";

        foreach (var item in items)
        {
            var border = new Border
            {
                Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(4),
                Background = (Brush)Application.Current.FindResource("CardBackgroundBrush")
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text = item.FileName,
                FontSize = 11, FontFamily = new FontFamily("Microsoft YaHei UI"),
                Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = Cursors.Hand,
                Tag = item.FilePath
            };
            nameBlock.MouseLeftButtonDown += (_, _) => ViewTrashItem(item, _trashWindow!);
            Grid.SetColumn(nameBlock, 0);
            grid.Children.Add(nameBlock);

            var restoreBtn = new Button
            {
                Content = "还原", FontSize = 10, Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(6, 0, 4, 0),
                Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
                Foreground = (Brush)Application.Current.FindResource("SuccessBrush"), Tag = item
            };
            restoreBtn.Click += (_, _) =>
            {
                FileService.RestoreTrashItem(item.Id);
                LoadTrashList(list, countText);
            };
            Grid.SetColumn(restoreBtn, 1);
            grid.Children.Add(restoreBtn);

            var delBtn = new Button
            {
                Content = "X", FontSize = 10, Width = 20, Height = 20,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = (Brush)Application.Current.FindResource("DangerBrush"),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Tag = item
            };
            delBtn.Click += (_, _) =>
            {
                try { Directory.Delete(Path.Combine(FileService.TrashPath(App.WorkRoot), item.Id), true); }
                catch { }
                LoadTrashList(list, countText);
            };
            delBtn.MouseEnter += (s, _) => ((Button)s).Background = (Brush)Application.Current.FindResource("HoverBrush");
            delBtn.MouseLeave += (s, _) => ((Button)s).Background = Brushes.Transparent;
            Grid.SetColumn(delBtn, 2);
            grid.Children.Add(delBtn);

            border.Child = grid;
            list.Items.Add(border);
        }
    }

    private void ViewTrashItem(TrashItem item, Window ownerWindow)
    {
        var ext = Path.GetExtension(item.FileName).ToLower();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".webp")
        {
            try
            {
                var win = new Window
                {
                    Title = item.FileName,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = ownerWindow,
                    Background = Brushes.Black,
                    SizeToContent = SizeToContent.WidthAndHeight
                };
                var data = File.ReadAllBytes(item.FilePath);
                var bmp = new BitmapImage(); bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(data);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 800;
                bmp.EndInit();
                var img = new Image { Source = bmp, Stretch = Stretch.Uniform, MaxWidth = 800, MaxHeight = 600 };
                win.Content = img;
                win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
                img.MouseLeftButtonDown += (_, _) => { win.Close(); };
                win.Loaded += (_, _) => win.Activate();
                win.ShowDialog();
            }
            catch { }
        }
        else if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv")
        {
            try
            {
                var win = new Window
                {
                    Title = item.FileName,
                    Width = 700, Height = 450,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = ownerWindow,
                    Background = Brushes.Black,
                    ResizeMode = ResizeMode.CanResizeWithGrip
                };
                var me = new MediaElement
                {
                    Source = new Uri(item.FilePath, UriKind.Absolute),
                    LoadedBehavior = MediaState.Play,
                    Stretch = Stretch.Uniform,
                    Volume = 1
                };
                me.MediaEnded += (_, _) => { me.Position = TimeSpan.Zero; me.Pause(); };
                me.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount == 2) return;
                    me.Position = TimeSpan.Zero;
                };
                win.Content = me;
                win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
                win.ShowDialog();
            }
            catch { }
        }
        else
        {
            try { Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true }); }
            catch { }
        }
    }

    // ===== 备忘录 =====
    private void MemoCard_Click(object sender, MouseButtonEventArgs e) => OpenMemoWindow();

    internal static void OpenMemoWindow()
    {
        var memos = FileService.LoadMemos(App.WorkRoot);
        Memo? selectedMemo = memos.FirstOrDefault();

        var win = new Window
        {
            Title = "备忘录",
            Width = 720, Height = 520,
            MinWidth = 560, MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            Background = (Brush)Application.Current.FindResource("WindowBackgroundBrush")
        };

        var root = new Grid { Margin = new Thickness(16) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ==== 左侧列表 ====
        var leftPanel = new Grid();
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var leftHeader = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        leftHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        leftHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerText = new TextBlock
        {
            Text = $"全部备忘录 ({memos.Count})", FontSize = 13, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(headerText, 0);
        leftHeader.Children.Add(headerText);

        var addBtn = new Button
        {
            Content = "+ 新建", FontSize = 11, Padding = new Thickness(8, 3, 8, 3),
            Style = (Style)Application.Current.FindResource("PrimaryButtonStyle")
        };
        Grid.SetColumn(addBtn, 1);
        leftHeader.Children.Add(addBtn);
        leftPanel.Children.Add(leftHeader);

        var memoList = new ItemsControl();
        var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = memoList };
        Grid.SetRow(listScroll, 1);
        leftPanel.Children.Add(listScroll);
        Grid.SetColumn(leftPanel, 0);
        root.Children.Add(leftPanel);

        // 分隔线
        var separator = new Border
        {
            Background = (Brush)Application.Current.FindResource("BorderBrush"),
            Width = 1, Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetColumn(separator, 1);
        root.Children.Add(separator);

        // ==== 右侧编辑区 ====
        var rightPanel = new Grid { Margin = new Thickness(12, 0, 0, 0) };
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBox = new TextBox
        {
            FontSize = 18, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush"),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 6, 4, 6),
            Margin = new Thickness(0, 0, 0, 8),
            Text = "选择或新建备忘录"
        };
        Grid.SetRow(titleBox, 0);
        rightPanel.Children.Add(titleBox);

        var contentBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush"),
            Background = (Brush)Application.Current.FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10)
        };
        Grid.SetRow(contentBox, 1);
        rightPanel.Children.Add(contentBox);

        // 底部信息栏
        var bottomBar = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dateText = new TextBlock
        {
            FontSize = 11, FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 320
        };
        Grid.SetColumn(dateText, 0);
        bottomBar.Children.Add(dateText);

        var delBtn = new Button
        {
            Content = "删除", FontSize = 11, Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("SecondaryButtonStyle"),
            Foreground = (Brush)Application.Current.FindResource("DangerBrush"),
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(delBtn, 1);
        bottomBar.Children.Add(delBtn);

        // 提示文字放在父容器底部
        var saveHint = new TextBlock
        {
            Text = "Ctrl+S 即刻保存 | 失焦自动保存", FontSize = 11,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0)
        };
        Grid.SetRow(saveHint, 3);

        Grid.SetRow(bottomBar, 2);
        rightPanel.Children.Add(bottomBar);
        rightPanel.Children.Add(saveHint);
        Grid.SetColumn(rightPanel, 2);
        root.Children.Add(rightPanel);

        // 右侧空状态
        var emptyHint = new TextBlock
        {
            Text = "← 点击「+ 新建」创建第一条备忘录",
            FontSize = 13, FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(emptyHint, 2);
        root.Children.Add(emptyHint);

        // 自动保存定时器
        System.Windows.Threading.DispatcherTimer? autoSaveTimer = null;
        bool rightPanelVisible = false;

        // ==== 刷新左侧列表 ====
        void RefreshList()
        {
            memos = FileService.LoadMemos(App.WorkRoot);
            headerText.Text = $"全部备忘录 ({memos.Count})";
            memoList.Items.Clear();

            if (memos.Count == 0)
            {
                selectedMemo = null;
                rightPanel.Visibility = Visibility.Collapsed;
                emptyHint.Visibility = Visibility.Visible;
                return;
            }

            rightPanel.Visibility = Visibility.Visible;
            emptyHint.Visibility = Visibility.Collapsed;

            foreach (var m in memos)
            {
                var itemBorder = new Border
                {
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(10, 8, 10, 8),
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(0, 1, 0, 1),
                    Tag = m
                };
                itemBorder.MouseLeftButtonDown += (_, _) =>
                {
                    // 先保存当前备忘录，再切换
                    autoSaveTimer?.Stop();
                    if (selectedMemo != null)
                    {
                        SaveCurrentMemo(selectedMemo, titleBox.Text, contentBox.Text);
                        UpdateCardDisplay(selectedMemo);
                    }
                    SelectMemo(m);
                    // 不立即重启定时器，等用户真正编辑新备忘录时再启动
                };
                itemBorder.MouseEnter += (_, _) =>
                {
                    if (selectedMemo?.Id != m.Id)
                        itemBorder.Background = (Brush)Application.Current.FindResource("HoverBrush");
                };
                itemBorder.MouseLeave += (_, _) =>
                {
                    if (selectedMemo?.Id != m.Id)
                        itemBorder.Background = Brushes.Transparent;
                };

                var textStack = new StackPanel();
                textStack.Children.Add(new TextBlock
                {
                    Text = m.Title.Length > 0 ? m.Title : "无标题",
                    FontSize = 12, FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    Foreground = (Brush)Application.Current.FindResource("TextPrimaryBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                textStack.Children.Add(new TextBlock
                {
                    Text = m.UpdatedAt.ToString("MM-dd HH:mm"),
                    FontSize = 10, FontFamily = new FontFamily("Microsoft YaHei UI"),
                    Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                itemBorder.Child = textStack;
                memoList.Items.Add(itemBorder);
            }

            // 选中项高亮
            void SelectMemo(Memo m)
            {
                // 从磁盘重新加载最新数据，避免读到过期的内存对象
                var latest = FileService.LoadMemos(App.WorkRoot)
                    .FirstOrDefault(x => x.Id == m.Id);
                if (latest != null) m = latest;

                selectedMemo = m;
                foreach (var child in memoList.Items)
                {
                    if (child is Border b && b.Tag is Memo tm)
                    {
                        b.Background = tm.Id == m.Id
                            ? (Brush)Application.Current.FindResource("PrimaryBrush") : Brushes.Transparent;
                        if (b.Child is StackPanel sp && sp.Children[0] is TextBlock tb)
                            tb.Foreground = tm.Id == m.Id
                                ? Brushes.White
                                : (Brush)Application.Current.FindResource("TextPrimaryBrush");
                        if (b.Child is StackPanel sp2 && sp2.Children[1] is TextBlock tb2)
                            tb2.Foreground = tm.Id == m.Id
                                ? new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
                                : (Brush)Application.Current.FindResource("TextSecondaryBrush");
                    }
                }
                // 更新右侧（使用最新数据）
                titleBox.Text = m.Title;
                contentBox.Text = m.Content;
                dateText.Text = $"创建于 {m.CreatedAt:yyyy-MM-dd HH:mm}  更新于 {m.UpdatedAt:yyyy-MM-dd HH:mm}";
                delBtn.Visibility = Visibility.Visible;
                if (!rightPanelVisible)
                {
                    rightPanel.Visibility = Visibility.Visible;
                    emptyHint.Visibility = Visibility.Collapsed;
                    rightPanelVisible = true;
                }
            }

            // 默认选中第一个
            if (selectedMemo != null && memos.Any(x => x.Id == selectedMemo.Id))
                SelectMemo(selectedMemo);
            else if (memos.Count > 0)
                SelectMemo(memos[0]);
        }

        void SaveCurrentMemo(Memo m, string title, string content)
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content)) return;
            m.Title = title?.Trim() ?? "";
            m.Content = content ?? "";
            FileService.SaveMemo(App.WorkRoot, m);
        }

        // 刷新左侧卡片列表中的标题和时间
        void UpdateCardDisplay(Memo m)
        {
            foreach (var child in memoList.Items)
            {
                if (child is Border b && b.Tag is Memo tm && tm.Id == m.Id
                    && b.Child is StackPanel sp)
                {
                    if (sp.Children.Count >= 2)
                    {
                        if (sp.Children[0] is TextBlock tbTitle)
                            tbTitle.Text = m.Title.Length > 0 ? m.Title : "无标题";
                        if (sp.Children[1] is TextBlock tbDate)
                            tbDate.Text = m.UpdatedAt.ToString("MM-dd HH:mm");
                    }
                    // 更新卡片 Tag 为最新对象
                    b.Tag = m;
                    break;
                }
            }
        }

        // 新增按钮
        addBtn.Click += (_, _) =>
        {
            if (autoSaveTimer != null && selectedMemo != null)
            {
                autoSaveTimer.Stop();
                SaveCurrentMemo(selectedMemo, titleBox.Text, contentBox.Text);
                autoSaveTimer.Start();
            }
            var newMemo = new Memo { Title = "新建备忘录", Content = "" };
            FileService.SaveMemo(App.WorkRoot, newMemo);
            selectedMemo = newMemo;
            RefreshList();
            titleBox.Focus();
            titleBox.SelectAll();
        };

        // 标题/内容改变时自动保存（2秒延迟）
        autoSaveTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        autoSaveTimer.Tick += (_, _) =>
        {
            autoSaveTimer.Stop();
            if (selectedMemo != null)
            {
                SaveCurrentMemo(selectedMemo, titleBox.Text, contentBox.Text);
                UpdateCardDisplay(selectedMemo);
                dateText.Text = $"创建于 {selectedMemo.CreatedAt:yyyy-MM-dd HH:mm}  更新于 {selectedMemo.UpdatedAt:yyyy-MM-dd HH:mm}";
            }
        };

        void StartAutoSave()
        {
            autoSaveTimer?.Stop();
            autoSaveTimer?.Start();
        }

        titleBox.TextChanged += (_, _) => StartAutoSave();
        contentBox.TextChanged += (_, _) => StartAutoSave();

        // Ctrl+S 即刻保存
        win.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                autoSaveTimer?.Stop();
                if (selectedMemo != null)
                {
                    SaveCurrentMemo(selectedMemo, titleBox.Text, contentBox.Text);
                    UpdateCardDisplay(selectedMemo);
                    var prev = saveHint.Text;
                    saveHint.Text = "✓ 已保存";
                    saveHint.Foreground = (Brush)Application.Current.FindResource("SuccessBrush");
                    var restoreTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromSeconds(1.5) };
                    restoreTimer.Tick += (_, _) =>
                    {
                        restoreTimer.Stop();
                        saveHint.Text = prev;
                        saveHint.Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush");
                    };
                    restoreTimer.Start();
                }
                autoSaveTimer?.Start();
            }
        };

        // 删除按钮
        delBtn.Click += (_, _) =>
        {
            if (selectedMemo == null) return;
            if (!MessageDialog.Confirm("确认删除", $"确定删除备忘录「{selectedMemo.Title}」？此操作不可撤销。")) return;
            autoSaveTimer?.Stop();
            FileService.DeleteMemo(App.WorkRoot, selectedMemo.Id);
            selectedMemo = null;
            RefreshList();
        };

        // 关闭时保存
        win.Closing += (_, _) =>
        {
            autoSaveTimer?.Stop();
            if (selectedMemo != null)
                SaveCurrentMemo(selectedMemo, titleBox.Text, contentBox.Text);
        };

        win.Content = root;
        RefreshList();
        win.Show();
    }

    private void NovelCard_Click(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://www.isyd.net/") { UseShellExecute = true }); }
        catch { }
    }

    private void OpenRootDir_Click(object sender, MouseButtonEventArgs e)
    {
        var path = App.WorkRoot;
        FileService.EnsureDirectory(path);
        try { Process.Start("explorer.exe", path); } catch { }
    }

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border card) card.Opacity = 0.85;
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border card) card.Opacity = 1.0;
    }
}
