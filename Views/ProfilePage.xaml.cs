using System;
using System.Collections.Generic;
using System.IO;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

public partial class ProfilePage : UserControl
{
    private AppConfig _config = null!;
    private bool _isEditing = false;
    private List<NovelInfo> _novels = new();

    public ProfilePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private bool _loaded;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _config = FileService.LoadConfig(App.WorkRoot);
        RefreshProfile();
        RefreshAchievements();
    }

    private void RefreshProfile()
    {
        UserNameDisplay.Text = _config.UserName;
        SignatureDisplay.Text = _config.UserSignature;
        UserNameEdit.Text = _config.UserName;
        SignatureEdit.Text = _config.UserSignature;

        var path = FileService.GetEffectiveAvatarPath();
        if (File.Exists(path))
        {
            try
            {
                var data = File.ReadAllBytes(path);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(data);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                AvatarBrush.ImageSource = null;
                AvatarBrush.ImageSource = bmp;
                AvatarPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch { /* 加载失败静默忽略 */ }
        }
        else
        {
            AvatarBrush.ImageSource = null;
            AvatarPlaceholder.Visibility = Visibility.Visible;
        }

        SetEditMode(false);
    }

    private void SetEditMode(bool editing)
    {
        _isEditing = editing;
        ViewPanel.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        EditPanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        var icon = (TextBlock)EditButton.Content;
        icon.Text = editing ? "✓" : "✎";
        icon.FontSize = editing ? 18 : 16;
    }

    private void ProfileScroller_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isEditing)
        {
            _config.UserName = UserNameEdit.Text;
            _config.UserSignature = SignatureEdit.Text;
            FileService.SaveConfig(App.WorkRoot, _config);
            UserNameDisplay.Text = _config.UserName;
            SignatureDisplay.Text = _config.UserSignature;
        }
        SetEditMode(!_isEditing);
    }

    private void Avatar_Click(object sender, MouseButtonEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp",
            Title = "选择头像图片"
        };
        if (dlg.ShowDialog() != true) return;

        var cw = new CropWindow(dlg.FileName) { Owner = Window.GetWindow(this) };
        cw.Cropped += img =>
        {
            var avatarDir = FileService.AssetsAvatarPath;
            FileService.EnsureDirectory(avatarDir);
            var targetPath = FileService.CustomAvatarFile;
            if (File.Exists(targetPath)) FileService.DeleteFile(targetPath);

            // 保存头像并立即刷新到磁盘
            using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                enc.Save(fs);
                fs.Flush(true);
            }

            var miniDir = FileService.AssetsAvatarMiniPath;
            FileService.EnsureDirectory(miniDir);
            var miniFile = Path.Combine(miniDir, "profile.png");
            if (File.Exists(miniFile)) FileService.DeleteFile(miniFile);
            File.Copy(targetPath, miniFile, overwrite: true);

            // 更新大头像
            var frozen = img.Clone();
            frozen.Freeze();
            AvatarBrush.ImageSource = null;
            AvatarBrush.ImageSource = frozen;
            AvatarPlaceholder.Visibility = Visibility.Collapsed;

            // 更新导航栏小头像（文件已写入完毕）
            if (Window.GetWindow(this) is MainWindow mw) mw.LoadNavAvatar();
        };
        cw.Show();
    }

    // ===== 个人成就（独立存储，与剧本章节小说隔离） =====
    private void RefreshAchievements()
    {
        _novels = FileService.LoadProfileWorks(App.WorkRoot);
        AchievementPanel.Children.Clear();

        if (_novels.Count == 0)
        {
            AchievementPanel.Children.Add(new TextBlock
            {
                Text = "暂无作品，点击「+ 新建作品」添加",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                FontSize = 13, Margin = new Thickness(12)
            });
            return;
        }
        foreach (var novel in _novels)
            AchievementPanel.Children.Add(CreateAchievementCard(novel));
    }

    private Border CreateAchievementCard(NovelInfo novel)
    {
        var border = new Border
        {
            Style = (Style)FindResource("ListItemCardStyle"),
            Margin = new Thickness(0, 3, 0, 3)
        };

        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左侧：封面+信息
        var stack = new StackPanel { Orientation = Orientation.Horizontal };

        var coverBorder = new Border
        {
            Width = 55, Height = 75,
            CornerRadius = new CornerRadius(5),
            Background = ParseColor(novel.CoverColor),
            Margin = new Thickness(0, 0, 12, 0)
        };
        if (novel.HasCoverImage)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(FileService.NovelCoverFile(App.WorkRoot, novel.Id));
                bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
                coverBorder.Child = new Border
                {
                    CornerRadius = new CornerRadius(5), ClipToBounds = true,
                    Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill }
                };
            }
            catch { coverBorder.Child = CoverPlaceholder(novel.Name); }
        }
        else coverBorder.Child = CoverPlaceholder(novel.Name);
        stack.Children.Add(coverBorder);

        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = novel.Name, FontSize = 14, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Microsoft YaHei"),
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        });
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(novel.Description) ? "暂无简介" : novel.Description,
            FontSize = 11, FontFamily = new FontFamily("Microsoft YaHei"),
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 3, 0, 6),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // 数据行 — WrapPanel 右对齐，收益红色
        var statsWrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        AddStatItem(statsWrap, "评论", novel.TotalComments);
        AddStatItem(statsWrap, "收藏", novel.TotalFavorites);
        AddStatItem(statsWrap, "播放", novel.TotalPlays);
        AddStatItem(statsWrap, "点赞", novel.TotalLikes);
        AddStatItem(statsWrap, "转发", novel.TotalForwards);
        // 收益红色
        statsWrap.Children.Add(new TextBlock
        {
            Text = $"¥{novel.TotalIncome:N0}",
            FontSize = 11, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Microsoft YaHei"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });
        info.Children.Add(statsWrap);
        stack.Children.Add(info);
        Grid.SetColumn(stack, 0);
        mainGrid.Children.Add(stack);

        // 编辑按钮
        var editBtn = new Button
        {
            Content = "编辑", FontSize = 10, FontFamily = new FontFamily("Microsoft YaHei"),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(8, 0, 4, 0),
            Style = (Style)FindResource("SecondaryButtonStyle"),
            VerticalAlignment = VerticalAlignment.Top
        };
        editBtn.Click += (_, _) => EditNovelStats(novel);
        Grid.SetColumn(editBtn, 1);
        mainGrid.Children.Add(editBtn);

        // 删除按钮
        var delBtn = new Button
        {
            Content = "X", FontSize = 10, Width = 22, Height = 22,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("DangerBrush"),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top
        };
        delBtn.Click += (_, _) =>
        {
            if (!MessageDialog.Confirm("确认删除", $"确定删除「{novel.Name}」？\n此操作不可恢复。")) return;
            _novels.Remove(novel);
            FileService.SaveProfileWorks(App.WorkRoot, _novels);
            RefreshAchievements();
        };
        delBtn.MouseEnter += (s, _) => ((Button)s).Background = (Brush)FindResource("HoverBrush");
        delBtn.MouseLeave += (s, _) => ((Button)s).Background = Brushes.Transparent;
        Grid.SetColumn(delBtn, 2);
        mainGrid.Children.Add(delBtn);

        border.Child = mainGrid;
        return border;
    }

    private void AddStatItem(Panel parent, string label, long value)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 2) };
        sp.Children.Add(new TextBlock
        {
            Text = $"{label} ", FontSize = 10, FontFamily = new FontFamily("Microsoft YaHei"),
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        sp.Children.Add(new TextBlock
        {
            Text = FormatNum(value), FontSize = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Microsoft YaHei"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        parent.Children.Add(sp);
    }

    private static string FormatNum(long n)
    {
        if (n >= 10000) return $"{n / 10000.0:F1}万";
        if (n >= 1000) return $"{n / 1000.0:F1}k";
        return n.ToString();
    }

    private TextBlock CoverPlaceholder(string name) => new()
    {
        Text = name.Length > 0 ? name[..Math.Min(2, name.Length)] : "书",
        Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.Bold,
        FontFamily = new FontFamily("Microsoft YaHei"),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    // ===== 编辑弹窗（跟随主题） =====
    private void EditNovelStats(NovelInfo novel)
    {
        var dialog = new Window
        {
            Title = $"编辑 - {novel.Name}",
            Width = 420, MinHeight = 400, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(this),
            Background = (Brush)FindResource("WindowBackgroundBrush")
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = novel.Name, FontSize = 18, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Microsoft YaHei"), Margin = new Thickness(0, 0, 0, 12),
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        });

        var descBox = new TextBox
        {
            Text = novel.Description,
            Margin = new Thickness(0, 0, 0, 8), MinHeight = 60,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("ModernTextBoxStyle"), FontSize = 12
        };
        panel.Children.Add(descBox);

        AddField(panel, "总评论:", novel.TotalComments.ToString(), v => novel.TotalComments = long.Parse(v));
        AddField(panel, "总收藏:", novel.TotalFavorites.ToString(), v => novel.TotalFavorites = long.Parse(v));
        AddField(panel, "总播放:", novel.TotalPlays.ToString(), v => novel.TotalPlays = long.Parse(v));
        AddField(panel, "总转发:", novel.TotalForwards.ToString(), v => novel.TotalForwards = long.Parse(v));
        AddField(panel, "总点赞:", novel.TotalLikes.ToString(), v => novel.TotalLikes = long.Parse(v));
        AddField(panel, "总收益(元):", novel.TotalIncome.ToString(), v => novel.TotalIncome = decimal.Parse(v));
        AddField(panel, "制作时长(h):", novel.TotalProductionHours.ToString(), v => novel.TotalProductionHours = double.Parse(v));

        var saveBtn = new Button
        {
            Content = "保存", Style = (Style)FindResource("PrimaryButtonStyle"),
            Margin = new Thickness(0, 16, 0, 0)
        };
        saveBtn.Click += (_, _) =>
        {
            novel.Description = descBox.Text;
            FileService.SaveProfileWorks(App.WorkRoot, _novels);
            RefreshAchievements();
            dialog.Close();
        };
        panel.Children.Add(saveBtn);

        dialog.Content = panel;
        dialog.Show();
    }

    private void AddField(StackPanel panel, string label, string value, Action<string> setter)
    {
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12, FontFamily = new FontFamily("Microsoft YaHei"),
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 4, 0, 2)
        });
        var box = new TextBox
        {
            Text = value, Style = (Style)FindResource("ModernTextBoxStyle"), FontSize = 12
        };
        box.LostFocus += (_, _) =>
        {
            try { setter(box.Text); } catch { }
        };
        panel.Children.Add(box);
    }

    private void AddNovel_Click(object sender, RoutedEventArgs e)
    {
        var novel = new NovelInfo
        {
            Id = Guid.NewGuid().ToString(),
            Name = "新作品",
            Description = "点击编辑作品信息",
            CoverColor = "#4A90E2"
        };
        _novels.Add(novel);
        FileService.SaveProfileWorks(App.WorkRoot, _novels);
        RefreshAchievements();
        EditNovelStats(novel);
    }

    private static SolidColorBrush ParseColor(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(Colors.SteelBlue); }
    }
}
