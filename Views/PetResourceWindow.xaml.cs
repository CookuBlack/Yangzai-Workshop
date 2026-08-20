using System;
using System.Collections.Generic;
using System.Diagnostics;
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

public partial class PetResourceWindow : Window
{
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
    private static readonly string[] VideoExts = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm" };
    private static readonly string[] TextExts = { ".txt", ".md", ".json", ".csv" };

    private readonly List<PetResourceItem> _all = new();
    private readonly Dictionary<string, BitmapImage> _imageCache = new();
    private bool _closing;

    public PetResourceWindow()
    {
        InitializeComponent();
        FilterBox.ItemsSource = new[] { "全部", "图片", "视频", "文本" };
        FilterBox.SelectedIndex = 0;
        Loaded += (_, _) => RefreshList();
        SizeChanged += (_, _) => { if (IsLoaded && !_closing) ApplyFilter(); };
        Closed += (_, _) => _closing = true;
    }

    private static string ResourceDir => FileService.PetResourcesPath(App.WorkRoot);

    private void RefreshList()
    {
        _all.Clear();
        _imageCache.Clear();
        if (Directory.Exists(ResourceDir))
        {
            foreach (var f in Directory.GetFiles(ResourceDir))
            {
                _all.Add(PetResourceItem.From(f));
            }
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var kind = FilterBox.SelectedItem?.ToString() ?? "全部";
        IEnumerable<PetResourceItem> items = _all.OrderByDescending(i => i.ModifiedTicks);
        if (kind != "全部")
            items = items.Where(i => i.Kind == kind);

        var list = items.ToList();
        CountText.Text = $"共 {list.Count} 项 · 目录：{ResourceDir}";
        RenderGrid(list);
    }

    // ===== 瀑布流 / 卡片渲染 =====

    private void RenderGrid(List<PetResourceItem> items)
    {
        ResourceGrid.Children.Clear();
        ResourceGrid.RowDefinitions.Clear();
        ResourceGrid.ColumnDefinitions.Clear();

        if (items.Count == 0)
        {
            ResourceGrid.RowDefinitions.Add(new RowDefinition());
            ResourceGrid.ColumnDefinitions.Add(new ColumnDefinition());
            ResourceGrid.Children.Add(new TextBlock
            {
                Text = "暂无资源\n宠物通过 AI 功能生成的内容会保存到这里",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                TextAlignment = TextAlignment.Center,
                LineHeight = 26
            });
            return;
        }

        // 计算列数（自适应窗口宽度），并据此算出每张卡片的实际内容宽度
        double availW = Math.Max(200, ActualWidth - 28);
        int cols = Math.Max(1, (int)Math.Floor(availW / 220.0));
        while (cols > 1 && (availW / cols) < 190) cols--; // 卡片过窄时减少列数，避免挤压

        for (int i = 0; i < cols; i++)
            ResourceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ResourceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var columnPanels = new StackPanel[cols];
        for (int c = 0; c < cols; c++)
        {
            columnPanels[c] = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(columnPanels[c], c);
            ResourceGrid.Children.Add(columnPanels[c]);
        }

        var columnHeights = new double[cols];
        // 卡片内容宽度 = 列宽 - 卡片左右 margin(8) - 边框(2)
        double cardW = Math.Max(120, availW / cols - 12);

        foreach (var item in items)
        {
            int targetCol = 0;
            for (int c = 1; c < cols; c++)
                if (columnHeights[c] < columnHeights[targetCol]) targetCol = c;

            Border card;
            double itemHeight;
            switch (item.Kind)
            {
                case "图片": card = BuildImageCard(item, cardW, out itemHeight); break;
                case "视频": card = BuildVideoCard(item, cardW, out itemHeight); break;
                default: card = BuildTextCard(item, cardW, out itemHeight); break;
            }

            columnHeights[targetCol] += itemHeight;
            columnPanels[targetCol].Children.Add(card);
        }
    }

    /// <summary>图片卡片：完整展示图片（瀑布流，按原始宽高比）</summary>
    private Border BuildImageCard(PetResourceItem item, double cardW, out double height)
    {
        double ratio = GetAspectRatio(item.FilePath); // 宽/高
        double h = ratio > 0 ? cardW / ratio : cardW * 0.75;
        height = h + 30; // 图片区 + 文件名字段

        var card = new Border
        {
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Tag = item.FilePath,
            Cursor = Cursors.Hand
        };

        var stack = new StackPanel();

        // 图片区：横向拉伸填满卡片内容宽度（不再固定 220，保证悬停按钮相对卡片居中）
        var imgArea = new Grid { Height = h, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        var img = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = item.FilePath
        };
        imgArea.Children.Add(img);

        // 悬停工具栏（底部居中显示在图片上）
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = 0
        };
        toolbar.Children.Add(CreateIconBtn("\uE8A7", "打开", () => PreviewFile(item.FilePath, item.Kind)));
        toolbar.Children.Add(CreateIconBtn("\uE838", "定位", () => LocateFile(item.FilePath)));
        toolbar.Children.Add(CreateIconBtn("\uE74D", "删除", () => DeleteFile(item.FilePath)));
        imgArea.Children.Add(toolbar);

        card.MouseEnter += (_, _) => toolbar.Opacity = 1;
        card.MouseLeave += (_, _) => toolbar.Opacity = 0;
        // 点击卡片主体才预览；点击悬停工具栏按钮不触发（避免重复打开）
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Button) return;
            PreviewFile(item.FilePath, item.Kind);
        };

        stack.Children.Add(imgArea);

        // 文件名
        stack.Children.Add(new TextBlock
        {
            Text = item.FileName,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 6, 8, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = Math.Max(80, cardW - 16)
        });

        card.Child = stack;

        // 后台异步加载缩略图
        LoadImageAsync(item.FilePath, 360, img);
        return card;
    }

    /// <summary>视频卡片：深色底 + 播放图标</summary>
    private Border BuildVideoCard(PetResourceItem item, double cardW, out double height)
    {
        height = 150;
        var card = new Border
        {
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Tag = item.FilePath,
            Cursor = Cursors.Hand
        };

        var stack = new StackPanel();

        // 视频占位区域：横向拉伸填满卡片内容宽度（确保悬停按钮相对卡片居中）
        var mediaArea = new Grid
        {
            Height = 124,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))
        };
        mediaArea.Children.Add(new TextBlock
        {
            Text = "🎬",
            FontSize = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        mediaArea.Children.Add(new TextBlock
        {
            Text = "▶ 播放",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 8),
            Background = new SolidColorBrush(Color.FromArgb(0xAA, 0x00, 0x00, 0x00)),
            Padding = new Thickness(8, 3, 8, 3)
        });

        // 悬停工具栏
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0
        };
        toolbar.Children.Add(CreateIconBtn("\uE768", "播放", () => PreviewFile(item.FilePath, item.Kind)));
        toolbar.Children.Add(CreateIconBtn("\uE838", "定位", () => LocateFile(item.FilePath)));
        toolbar.Children.Add(CreateIconBtn("\uE74D", "删除", () => DeleteFile(item.FilePath)));
        mediaArea.Children.Add(toolbar);

        card.MouseEnter += (_, _) => toolbar.Opacity = 1;
        card.MouseLeave += (_, _) => toolbar.Opacity = 0;
        // 点击卡片主体才预览；点击悬停工具栏按钮不触发（避免重复打开）
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Button) return;
            PreviewFile(item.FilePath, item.Kind);
        };

        stack.Children.Add(mediaArea);
        stack.Children.Add(new TextBlock
        {
            Text = item.FileName,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 6, 8, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = Math.Max(80, cardW - 16)
        });
        card.Child = stack;
        return card;
    }

    /// <summary>文本/其他卡片：文件图标 + 名称</summary>
    private Border BuildTextCard(PetResourceItem item, double cardW, out double height)
    {
        height = 74;
        var card = new Border
        {
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Tag = item.FilePath,
            Cursor = Cursors.Hand
        };

        var stack = new StackPanel { Margin = new Thickness(10, 12, 10, 12) };
        stack.Children.Add(new TextBlock
        {
            Text = item.Icon,
            FontSize = 24,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = item.FileName,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = Math.Max(80, cardW - 20)
        });
        card.Child = stack;
        card.MouseLeftButtonDown += (_, _) =>
        {
            // 文本文件用应用内预览（置顶，避免被置顶宠物小窗口遮挡）；其他类型用系统默认程序打开
            if (item.Kind == "文本") PreviewFile(item.FilePath, item.Kind);
            else OpenFile(item.FilePath);
        };
        return card;
    }

    /// <summary>生成小图标按钮（Segoe MDL2 图标 + 圆角悬停）</summary>
    private Button CreateIconBtn(string glyph, string tip, Action action)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = Brushes.White
            },
            Width = 28, Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x30, 0x30, 0x30)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = tip
        };
        btn.Click += (_, _) => action();
        return btn;
    }

    // ===== 工具 =====

    private static double GetAspectRatio(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count > 0)
            {
                int w = decoder.Frames[0].PixelWidth;
                int h = decoder.Frames[0].PixelHeight;
                if (w > 0 && h > 0) return (double)w / h;
            }
        }
        catch { }
        return 0;
    }

    private void LoadImageAsync(string path, int decodeWidth, Image img)
    {
        if (_imageCache.TryGetValue(path, out var cached))
        {
            img.Source = cached;
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var data = File.ReadAllBytes(path);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                using var ms = new MemoryStream(data);
                bmp.StreamSource = ms;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = decodeWidth;
                bmp.EndInit();
                bmp.Freeze();

                Dispatcher.BeginInvoke(() =>
                {
                    if (_closing) return;
                    _imageCache[path] = bmp;
                    img.Source = bmp;
                });
            }
            catch { }
        });
    }

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileService.EnsureDirectory(ResourceDir);
            Process.Start(new ProcessStartInfo(ResourceDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainWindow.Notify($"⚠ 无法打开目录：{ex.Message}", success: false);
        }
    }

    private static void LocateFile(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { MainWindow.Notify($"⚠ 无法定位文件：{ex.Message}", success: false); }
    }

    private void DeleteFile(string path)
    {
        try
        {
            FileService.DeleteFile(path);
            MainWindow.Notify($"已删除：{Path.GetFileName(path)}");
            RefreshList();
        }
        catch (Exception ex)
        {
            MainWindow.Notify($"⚠ 删除失败：{ex.Message}", success: false);
        }
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainWindow.Notify($"⚠ 无法打开资源：{ex.Message}", success: false);
        }
    }

    /// <summary>在应用内预览图片或视频</summary>
    private void PreviewFile(string path, string kind)
    {
        try
        {
            Window? win = null;
            MediaElement? me = null;

            if (kind == "图片")
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                win = new Window
                {
                    Title = Path.GetFileName(path),
                    Width = Math.Min(900, Math.Max(320, bmp.PixelWidth + 40)),
                    Height = Math.Min(700, Math.Max(240, bmp.PixelHeight + 60)),
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ShowInTaskbar = false,
                    Topmost = true,
                    Background = Brushes.Black,
                    ResizeMode = ResizeMode.CanResizeWithGrip,
                    MinWidth = 320,
                    MinHeight = 240,
                    Content = new Image
                    {
                        Source = bmp,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(4)
                    }
                };
            }
            else if (kind == "视频")
            {
                me = new MediaElement
                {
                    Source = new Uri(path, UriKind.Absolute),
                    LoadedBehavior = MediaState.Play,
                    UnloadedBehavior = MediaState.Stop,
                    Stretch = Stretch.Uniform,
                    Volume = 1,
                    Margin = new Thickness(4)
                };
                me.MediaEnded += (_, _) => { me.Position = TimeSpan.Zero; me.Play(); };

                win = new Window
                {
                    Title = Path.GetFileName(path),
                    Width = 760,
                    Height = 500,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ShowInTaskbar = false,
                    Topmost = true,
                    Background = Brushes.Black,
                    ResizeMode = ResizeMode.CanResizeWithGrip,
                    MinWidth = 320,
                    MinHeight = 240,
                    Content = me
                };
            }
            else if (kind == "文本")
            {
                // 文本：应用内只读预览（置顶，避免被置顶宠物小窗口遮挡）
                string text;
                try { text = File.ReadAllText(path); }
                catch (Exception ex)
                {
                    MainWindow.Notify($"⚠ 无法读取文本：{ex.Message}", success: false);
                    return;
                }

                var box = new TextBox
                {
                    Text = text,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    FontFamily = new FontFamily("Consolas, Microsoft YaHei UI, Segoe UI"),
                    FontSize = 13,
                    Margin = new Thickness(4),
                    Background = Brushes.White,
                    Foreground = Brushes.Black,
                    BorderThickness = new Thickness(0)
                };

                win = new Window
                {
                    Title = Path.GetFileName(path),
                    Width = 720,
                    Height = 520,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ShowInTaskbar = false,
                    Topmost = true,
                    ResizeMode = ResizeMode.CanResizeWithGrip,
                    MinWidth = 360,
                    MinHeight = 260,
                    Content = box
                };
            }
            else
            {
                // 其他类型：交给系统默认程序打开
                OpenFile(path);
                return;
            }

            // 关闭时释放 MediaElement，避免媒体句柄占用导致界面卡死
            win.Closed += (_, _) =>
            {
                if (me != null)
                {
                    me.Close();
                    me.Source = null;
                }
            };

            win.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) win.Close();
            };

            // 延迟到鼠标点击手势结束后再显示：
            // 避免在 MouseDown 中直接 Show() 导致焦点回跳、预览窗口被资源窗口遮挡
            Dispatcher.BeginInvoke(() =>
            {
                if (win == null) return;
                win.Show();
                win.Activate();
            });
        }
        catch (Exception ex)
        {
            MainWindow.Notify($"⚠ 无法预览：{ex.Message}", success: false);
        }
    }

    /// <summary>宠物资源列表项（绑定用）</summary>
    public class PetResourceItem
    {
        public string FilePath { get; init; } = "";
        public string FileName => Path.GetFileName(FilePath);
        public string Kind { get; init; } = "其他";
        public string Icon { get; init; } = "📄";
        public string SizeText { get; init; } = "";
        public string ModifiedText { get; init; } = "";
        public long ModifiedTicks { get; init; }

        public static PetResourceItem From(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            string kind, icon;
            if (ImageExts.Contains(ext)) { kind = "图片"; icon = "🖼️"; }
            else if (VideoExts.Contains(ext)) { kind = "视频"; icon = "🎬"; }
            else if (TextExts.Contains(ext)) { kind = "文本"; icon = "📝"; }
            else { kind = "其他"; icon = "📄"; }

            FileInfo fi = new(path);
            return new PetResourceItem
            {
                FilePath = path,
                Kind = kind,
                Icon = icon,
                SizeText = FormatSize(fi.Length),
                ModifiedText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                ModifiedTicks = fi.LastWriteTime.Ticks
            };
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
            return $"{bytes / 1024.0 / 1024.0:0.#} MB";
        }
    }
}
