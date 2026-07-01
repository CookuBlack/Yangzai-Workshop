using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using YangzaiWorkshop;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

public partial class SettingsPage : UserControl
{
    private AppConfig _config = null!;
    private bool _isLoading;

    // ====== 自定义主题：单一背景色调色板 ======
    private static readonly string[] PaletteColors =
    {
        // 灰白系
        "#FFFFFF","#F8F8F8","#F0F0F0","#EDEDED","#E8E8E8","#E0E0E0","#D6D6D6",
        "#CCCCCC","#BDBDBD","#AAAAAA","#999999","#777777","#555555","#333333","#222222",
        // 暖色系
        "#FFF8E1","#FFECB3","#FFE0B2","#FFCC80","#FFB74D","#FFA726","#FF9800","#F57C00",
        "#FBE9E7","#FFCCBC","#FFAB91","#FF8A65","#FF7043","#FF5722","#E64A19",
        "#FCE4EC","#F8BBD0","#F48FB1","#EC407A","#E91E63","#C2185B",
        "#FFF3E0","#FFCCBC","#FFAB91","#FF7043","#FF5722","#BF360C",
        // 冷色系
        "#E3F2FD","#BBDEFB","#90CAF9","#64B5F6","#42A5F5","#2196F3","#1976D2","#0D47A1",
        "#E8EAF6","#C5CAE9","#9FA8DA","#7986CB","#5C6BC0","#3F51B5","#303F9F",
        "#E0F7FA","#B2EBF2","#80DEEA","#4DD0E1","#00BCD4","#0097A7","#006064",
        "#E8F5E9","#C8E6C9","#A5D6A7","#81C784","#66BB6A","#4CAF50","#388E3C","#2E7D32",
        "#F1F8E9","#DCEDC8","#C5E1A5","#AED581","#9CCC65","#8BC34A","#689F38",
        "#E0E0E0","#B0BEC5","#90A4AE","#78909C","#607D8B","#455A64","#263238",
        // 特色
        "#F5F0E6","#EFE9DC","#E8D5B7","#D4C5A9","#C8B896",
        "#E6EEF5","#D8E4F0","#C8D8E8","#B0C8E0","#A0B8D8",
        "#EDE7F6","#D1C4E9","#B39DDB","#9575CD","#7E57C2",
        "#FCE4EC","#F8BBD0","#F48FB1","#F06292","#EC407A",
    };

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private bool _loaded;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        _isLoading = true;
        _config = FileService.LoadConfig(App.WorkRoot);

        // 生成调色板
        GenerateColorPalette();

        RefreshSettings();

        if (_config.FollowSystemTheme)
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        LoadAboutIcon();
        _isLoading = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void LoadAboutIcon()
    {
        try
        {
            var iconPath = FileService.DefaultAvatarFile;
            if (File.Exists(iconPath))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(iconPath);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                AboutIcon.Source = bmp;
            }
        }
        catch { /* 图标加载失败静默忽略 */ }
    }

    private void RefreshSettings()
    {
        // 主题
        FollowSystemCheck.IsChecked = _config.FollowSystemTheme;
        LightRadio.IsChecked = _config.Theme == "Light";
        DarkRadio.IsChecked = _config.Theme == "Dark";
        GlassRadio.IsChecked = _config.Theme == "Glass";
        CustomRadio.IsChecked = _config.Theme == "Custom";
        CustomThemePanel.Visibility = _config.Theme == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        ThemeManualPanel.Visibility = _config.FollowSystemTheme
            ? Visibility.Collapsed : Visibility.Visible;

        // 自定义主题
        _selectedColor = _config.CustomBgColor;
        HighlightSelectedSwatch();
        ImgFgLightRadio.IsChecked = _config.ImageForeground == "Light";
        ImgFgDarkRadio.IsChecked = _config.ImageForeground == "Dark";
        BgOpacitySlider.Value = _config.CustomBgOpacity;
        BgOpacityLabel.Text = $"{_config.CustomBgOpacity * 100:F0}%";
        BgBlurSlider.Value = _config.CustomBgBlur;
        BgBlurLabel.Text = $"{_config.CustomBgBlur:F0}px";
        RefreshBgPreview();

        // 工作目录（显示相对路径）
        WorkPathText.Text = GetRelativePath(App.WorkRoot);
        WorkPathText.ToolTip = App.WorkRoot;

        // 通用设置
        AutoSaveCheck.IsChecked = _config.AutoSaveScript;
        FontSizeSlider.Value = _config.FontSize;
        FontSizeLabel.Text = $"{_config.FontSize}px";
        AutoPlayCheck.IsChecked = _config.AutoPlayBanner;
        IntervalSlider.Value = _config.BannerIntervalSeconds;
        IntervalLabel.Text = $"{_config.BannerIntervalSeconds}秒";
        AutoBackupCheck.IsChecked = _config.AutoBackup;
        BackupIntervalSlider.Value = _config.BackupIntervalHours;
        BackupIntervalLabel.Text = FormatBackupInterval(_config.BackupIntervalHours);
        ApiEndpointBox.Text = _config.ApiEndpoint;
        ApiKeyBox.Password = _config.ApiKey;
        ApiModelBox.Text = _config.ApiModel;
        ImageModelBox.Text = _config.ImageModel;
        VideoModelBox.Text = _config.VideoModel;

        // 音乐播放器
        MusicAutoPlayCheck.IsChecked = _config.MusicAutoPlay;
        RefreshSettingsMusicList();

        // 版本信息
        VersionLabel.Text = $"v{App.AppVersion}";
        UpdateDateLabel.Text = _config.LastUpdateDate;
        VersionSubText.Text = $"版本 v{App.AppVersion} · 更新于 {_config.LastUpdateDate}";
        GitHubLink.Text = "GitHub: https://github.com/CookuBlack/Yangzai-Workshop";
    }

    // ===== 滚轮 =====
    private void SettingsScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3);
        e.Handled = true;
    }

    // ==================== 主题 ====================

    /// <summary>切换主题前先保存当前页内容，防止 RichTextBox 文档在资源切换时损坏</summary>
    private static void SaveAndApplyTheme(string theme)
    {
        if (NavigationService.Instance.CurrentPage is ScriptPage sp)
            sp.ForceSave();
        ThemeService.ApplyTheme(theme, App.WorkRoot);
    }

    private void LightRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.Theme = "Light";
        SaveAndApplyTheme("Light");
        SaveConfig();
        CustomThemePanel.Visibility = Visibility.Collapsed;
    }

    private void DarkRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.Theme = "Dark";
        SaveAndApplyTheme("Dark");
        SaveConfig();
        CustomThemePanel.Visibility = Visibility.Collapsed;
    }

    private void GlassRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.Theme = "Glass";
        SaveAndApplyTheme("Glass");
        SaveConfig();
        CustomThemePanel.Visibility = Visibility.Collapsed;
    }

    private void CustomRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.Theme = "Custom";
        SaveConfig();
        CustomThemePanel.Visibility = Visibility.Visible;
        // 立即应用自定义主题
        SaveAndApplyTheme("Custom");
    }

    // ==================== 自定义主题：单一背景色或背景图 ====================

    private string _selectedColor = "#EDEDED";

    /// <summary>在 ColorPalettePanel 中生成色块网格</summary>
    private void GenerateColorPalette()
    {
        ColorPalettePanel.Children.Clear();
        foreach (var hex in PaletteColors)
        {
            var swatch = new Border
            {
                Width = 22, Height = 22,
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                ToolTip = hex,
                Background = HexToBrush(hex),
                Margin = new Thickness(3),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(hex == _selectedColor ? 2 : 1)
            };
            var capturedHex = hex;
            swatch.MouseLeftButtonDown += (_, _) =>
            {
                _selectedColor = capturedHex;
                _config.CustomBgColor = capturedHex;
                _config.CustomBgImagePath = string.Empty;
                SaveConfig();
                RefreshBgPreview();
                HighlightSelectedSwatch();
                LivePreviewTheme();
            };

            ColorPalettePanel.Children.Add(swatch);
        }
        HighlightSelectedSwatch();
    }

    /// <summary>高亮当前选中的色块</summary>
    private void HighlightSelectedSwatch()
    {
        foreach (var child in ColorPalettePanel.Children)
        {
            if (child is Border b && b.Background is SolidColorBrush scb)
            {
                // Color.ToString() 返回 #AARRGGBB，取后6位
                var hexStr = scb.Color.ToString();
                bool match = hexStr.Length >= 7
                    && string.Equals(hexStr[^6..], _selectedColor.TrimStart('#'),
                        StringComparison.OrdinalIgnoreCase);
                b.BorderThickness = new Thickness(match ? 2 : 1);
            }
        }
    }

    // ====== 背景图片 ======

    private void SelectBgImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp",
            Title = "选择背景图片"
        };
        var window = Window.GetWindow(this);
        if (dlg.ShowDialog(window) == true)
        {
            _config.CustomBgImagePath = dlg.FileName;
            SaveConfig();
            RefreshBgPreview();

            // 重建主题
            LivePreviewTheme();

            // 提示用户重启生效
            if (MessageDialog.Confirm("提示",
                "背景图片已保存。\n更换背景图需要重启应用才能完全生效，是否立即重启？"))
            {
                Process.Start(Environment.ProcessPath!);
                Application.Current.Shutdown();
            }
        }
    }

    private void ClearBgImage_Click(object sender, RoutedEventArgs e)
    {
        _config.CustomBgImagePath = string.Empty;
        SaveConfig();
        RefreshBgPreview();
        // 清除图片后需重建主题（切换回纯色 alpha=255 模式）
        LivePreviewTheme();
    }

    private void RefreshBgPreview()
    {
        if (!string.IsNullOrEmpty(_config.CustomBgImagePath) && File.Exists(_config.CustomBgImagePath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(_config.CustomBgImagePath);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 72;
                bmp.EndInit();
                bmp.Freeze();
                BgPreviewImage.Source = bmp;
                BgImagePathText.Text = Path.GetFileName(_config.CustomBgImagePath);
                return;
            }
            catch { }
        }
        BgPreviewImage.Source = null;
        BgImagePathText.Text = "未选择图片";
    }

    private void BgOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.CustomBgOpacity = Math.Round(e.NewValue, 2);
        BgOpacityLabel.Text = $"{_config.CustomBgOpacity * 100:F0}%";
    }

    private void BgBlur_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.CustomBgBlur = Math.Round(e.NewValue, 0);
        BgBlurLabel.Text = $"{_config.CustomBgBlur:F0}px";
    }

    private void ImgForeground_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.ImageForeground = ImgFgLightRadio.IsChecked == true ? "Light" : "Dark";
        SaveConfig();
        // 立即预览切换
        LivePreviewTheme();
    }

    /// <summary>实时预览：立即刷新窗口外观（ApplyTheme 内部已调用 ApplyCustomBackground）</summary>
    private static void LivePreviewTheme()
    {
        ThemeService.ApplyTheme("Custom", App.WorkRoot);
    }

    // ====== 应用背景效果并重启 ======

    private void ApplyBgEffect_Click(object sender, RoutedEventArgs e)
    {
        SaveConfig();
        if (MessageDialog.Confirm("应用背景效果",
            "透明度和模糊度已保存。\n效果需要重启应用才能完全生效。\n\n是否立即重启？"))
        {
            Process.Start(Environment.ProcessPath!);
            Application.Current.Shutdown();
        }
    }

    // ====== 重置 ======

    private void ResetCustomTheme_Click(object sender, RoutedEventArgs e)
    {
        _selectedColor = "#EDEDED";
        _config.CustomBgColor = "#EDEDED";
        _config.CustomBgImagePath = string.Empty;
        _config.CustomBgOpacity = 0.35;
        _config.CustomBgBlur = 15;
        _config.ImageForeground = "Light";
        BgOpacitySlider.Value = 0.35;
        BgOpacityLabel.Text = "35%";
        BgBlurSlider.Value = 15;
        BgBlurLabel.Text = "15px";
        ImgFgLightRadio.IsChecked = true;
        ImgFgDarkRadio.IsChecked = false;
        HighlightSelectedSwatch();
        RefreshBgPreview();
        SaveConfig();
        if (ThemeService.CurrentTheme == "Custom")
            LivePreviewTheme();
    }

    // ====== 辅助 ======

    private static SolidColorBrush HexToBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(Colors.Gray); }
    }

    private void FollowSystemCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.FollowSystemTheme = true;
        SaveConfig();
        ThemeManualPanel.Visibility = Visibility.Collapsed;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ApplySystemTheme();
    }

    private void FollowSystemCheck_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.FollowSystemTheme = false;
        SaveConfig();
        ThemeManualPanel.Visibility = Visibility.Visible;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        // 恢复之前手动选择的主题
        SaveAndApplyTheme(_config.Theme);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            Dispatcher.BeginInvoke(ApplySystemTheme);
        }
    }

    private void ApplySystemTheme()
    {
        var isLight = ThemeService.IsSystemLightTheme();
        var theme = isLight ? "Light" : "Dark";
        if (ThemeService.CurrentTheme != theme)
        {
            SaveAndApplyTheme(theme);
            _config.Theme = theme;
            SaveConfig();
        }
        _isLoading = true;
        LightRadio.IsChecked = isLight;
        DarkRadio.IsChecked = !isLight;
        _isLoading = false;
    }

    // ==================== 工作目录 ====================
    private void OpenWorkDir_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", App.WorkRoot); }
        catch { }
    }

    private static string GetRelativePath(string fullPath)
    {
        var baseDir = FileService.AppBasePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (fullPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
        {
            var relative = fullPath.Substring(baseDir.Length);
            return ".\\" + relative;
        }
        return fullPath;
    }

    // ==================== 通用设置 ====================
    private void ResetGeneralSettings_Click(object sender, RoutedEventArgs e)
    {
        // 自动保存
        _config.AutoSaveScript = true;
        AutoSaveCheck.IsChecked = true;

        // 字体大小
        const int defaultFontSize = 14;
        _config.FontSize = defaultFontSize;
        FontSizeSlider.Value = defaultFontSize;
        FontSizeLabel.Text = $"{defaultFontSize}px";

        // 轮播自动播放
        _config.AutoPlayBanner = true;
        AutoPlayCheck.IsChecked = true;

        // 轮播间隔
        const int defaultInterval = 5;
        _config.BannerIntervalSeconds = defaultInterval;
        IntervalSlider.Value = defaultInterval;
        IntervalLabel.Text = $"{defaultInterval}秒";

        // 自动备份
        _config.AutoBackup = false;
        AutoBackupCheck.IsChecked = false;
        _config.BackupIntervalHours = 24;
        BackupIntervalSlider.Value = 24;
        BackupIntervalLabel.Text = FormatBackupInterval(24);

        SaveConfig();
        ApplyFontSizeToEditor(defaultFontSize);
        App.RestartBackupTimer();
    }

    private void AutoSaveCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.AutoSaveScript = AutoSaveCheck.IsChecked == true;
        SaveConfig();
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.FontSize = (int)e.NewValue;
        FontSizeLabel.Text = $"{_config.FontSize}px";
        SaveConfig();

        // 同步应用到 ScriptPage 编辑器
        ApplyFontSizeToEditor(_config.FontSize);
    }

    private static void ApplyFontSizeToEditor(int fontSize)
    {
        try
        {
            // 直接通过 NavigationService 缓存获取 ScriptPage，无需遍历可视化树
            var scriptPage = NavigationService.Instance.GetPage<ScriptPage>("Script");
            scriptPage?.ApplyFontSize(fontSize);
        }
        catch { /* 非关键路径 */ }
    }

    private void AutoPlayCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.AutoPlayBanner = AutoPlayCheck.IsChecked == true;
        SaveConfig();
    }

    private void AutoBackupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.AutoBackup = AutoBackupCheck.IsChecked == true;
        SaveConfig();
        App.RestartBackupTimer();
    }

    private void BackupIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.BackupIntervalHours = (int)e.NewValue;
        BackupIntervalLabel.Text = FormatBackupInterval(_config.BackupIntervalHours);
        SaveConfig();
        App.RestartBackupTimer();
    }

    private static string FormatBackupInterval(int hours)
    {
        return hours switch
        {
            <= 1 => "1小时",
            < 24 => $"{hours}小时",
            24 => "24小时（1天）",
            _ => $"{hours}小时（{hours / 24}天{hours % 24}小时）"
        };
    }

    // ===== 音乐播放器 =====

    private void RefreshSettingsMusicList()
    {
        var svc = MusicPlayerService.Instance;
        var items = svc.Playlist.Select(f => new { Path = f, Name = Path.GetFileName(f) }).ToList();
        SettingsMusicList.ItemsSource = items;
        SettingsMusicCount.Text = items.Count > 0 ? $"共 {items.Count} 首曲目" : "暂无音乐文件";
    }

    private void MusicAutoPlayCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.MusicAutoPlay = MusicAutoPlayCheck.IsChecked == true;
        SaveConfig();
    }

    private void SettingsMusicAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "音频文件|*.mp3;*.wav;*.ogg;*.m4a;*.flac;*.aac;*.wma",
            Multiselect = true,
            Title = "添加音乐文件"
        };
        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
        {
            MusicPlayerService.Instance.AddFiles(dlg.FileNames, FileService.MusicPath(App.WorkRoot));
            RefreshSettingsMusicList();
        }
    }

    private void SettingsOpenMusicDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = FileService.MusicPath(App.WorkRoot);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private void SettingsMusicDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext == null) return;
        var path = (string)btn.DataContext.GetType().GetProperty("Path")!.GetValue(btn.DataContext)!;
        MusicPlayerService.Instance.DeleteFile(path);
        RefreshSettingsMusicList();
    }

    private void IntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.BannerIntervalSeconds = (int)e.NewValue;
        IntervalLabel.Text = $"{_config.BannerIntervalSeconds}秒";
        SaveConfig();
    }

    // ==================== AI 模型配置 ====================
    private void ApiEndpointBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.ApiEndpoint = ApiEndpointBox.Text.Trim();
        SaveConfig();
    }
    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.ApiKey = ApiKeyBox.Password;
        SaveConfig();
    }
    private void ApiModelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.ApiModel = ApiModelBox.Text.Trim();
        SaveConfig();
    }

    /// <summary>ComboBox 鼠标滚轮切换选中项</summary>
    private void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ComboBox cb && cb.Items.Count > 0)
        {
            int delta = e.Delta > 0 ? -1 : 1;
            int newIdx = cb.SelectedIndex + delta;
            if (newIdx >= 0 && newIdx < cb.Items.Count)
                cb.SelectedIndex = newIdx;
            e.Handled = true;
        }
    }

    private void ApiModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        if (ApiModelBox.SelectedItem is string model)
        {
            _config.ApiModel = model;
            SaveConfig();
        }
    }

    private void ImageModelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.ImageModel = ImageModelBox.Text.Trim();
        SaveConfig();
    }

    private void VideoModelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.VideoModel = VideoModelBox.Text.Trim();
        SaveConfig();
    }

    private void EditSkill_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window
        {
            Title = "编辑 AI Skill",
            Width = 620, Height = 520,
            MinWidth = 500, MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.CanResize,
            Background = (Brush)FindResource("WindowBackgroundBrush")
        };

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 两个编辑框放入同一行，通过 Visibility 切换
        var scriptBox = new TextBox
        {
            Text = _config.ScriptSkill,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10)
        };
        Grid.SetRow(scriptBox, 2);
        grid.Children.Add(scriptBox);

        var promptBox = new TextBox
        {
            Text = _config.PromptSkill,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(promptBox, 2);
        grid.Children.Add(promptBox);

        // 标签页切换按钮
        var scriptBtn = CreateTabBtn("生成剧本 Skill", true);
        var promptBtn = CreateTabBtn("生成提示词 Skill", false);
        scriptBtn.Click += (_, _) => { SwitchTab(scriptBox, promptBox, scriptBtn, promptBtn); };
        promptBtn.Click += (_, _) => { SwitchTab(promptBox, scriptBox, promptBtn, scriptBtn); };
        var tabBar = new StackPanel { Orientation = Orientation.Horizontal };
        tabBar.Children.Add(scriptBtn);
        tabBar.Children.Add(promptBtn);
        Grid.SetRow(tabBar, 0);
        grid.Children.Add(tabBar);

        // 底部按钮
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var resetBtn = new Button
        {
            Content = "🔄 恢复默认",
            FontSize = 13,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)FindResource("SecondaryButtonStyle")
        };
        var saveBtn = new Button
        {
            Content = "💾 保存",
            FontSize = 13,
            Padding = new Thickness(20, 6, 20, 6),
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        resetBtn.Click += (_, _) =>
        {
            var def = new AppConfig();
            scriptBox.Text = def.ScriptSkill;
            promptBox.Text = def.PromptSkill;
        };
        saveBtn.Click += (_, _) =>
        {
            _config.ScriptSkill = scriptBox.Text;
            _config.PromptSkill = promptBox.Text;
            SaveConfig();
            win.Close();
        };
        footer.Children.Add(resetBtn);
        footer.Children.Add(saveBtn);
        Grid.SetRow(footer, 4);
        grid.Children.Add(footer);

        win.Content = grid;
        win.ShowDialog();
    }

    private Button CreateTabBtn(string text, bool active)
    {
        var btn = new Button
        {
            Content = text,
            FontSize = 14,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            MinWidth = 150,
            Padding = new Thickness(20, 7, 20, 7),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = active
                ? (Brush)FindResource("PrimaryBrush")
                : (Brush)FindResource("CardBackgroundBrush"),
            Foreground = active
                ? Brushes.White
                : (Brush)FindResource("TextPrimaryBrush"),
            BorderBrush = active
                ? (Brush)FindResource("PrimaryBrush")
                : (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 1, 0)
        };
        // 不使用样式，手动设置模板避免样式覆盖我们的属性
        btn.Style = null;
        btn.OverridesDefaultStyle = true;
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Border";
        border.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderBrushProperty,
            new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderThicknessProperty,
            new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6, 6, 0, 0));
        var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        contentPresenter.SetValue(ContentPresenter.MarginProperty, new Thickness(0));
        contentPresenter.SetBinding(ContentPresenter.ContentProperty,
            new System.Windows.Data.Binding("Content") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var sp = new FrameworkElementFactory(typeof(StackPanel));
        sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        sp.AppendChild(border);
        border.AppendChild(contentPresenter);
        template.VisualTree = sp;

        // 鼠标悬停效果（非激活状态）
        if (!active)
        {
            var trigger = new System.Windows.Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true
            };
            trigger.Setters.Add(new Setter(Button.BackgroundProperty,
                (Brush)FindResource("HoverBrush")));
            template.Triggers.Add(trigger);
        }

        btn.Template = template;
        return btn;
    }

    private void SwitchTab(TextBox show, TextBox hide, Button activeBtn, Button inactiveBtn)
    {
        show.Visibility = Visibility.Visible;
        hide.Visibility = Visibility.Collapsed;

        // 激活按钮样式
        activeBtn.Background = (Brush)FindResource("PrimaryBrush");
        activeBtn.Foreground = Brushes.White;
        activeBtn.BorderBrush = (Brush)FindResource("PrimaryBrush");
        activeBtn.FontWeight = FontWeights.SemiBold;

        // 非激活按钮样式
        inactiveBtn.Background = (Brush)FindResource("CardBackgroundBrush");
        inactiveBtn.Foreground = (Brush)FindResource("TextPrimaryBrush");
        inactiveBtn.BorderBrush = (Brush)FindResource("BorderBrush");
        inactiveBtn.FontWeight = FontWeights.Normal;
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = ApiEndpointBox.Text.Trim();
        var apiKey = ApiKeyBox.Password.Trim();
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey))
        {
            MessageDialog.Show("提示", "请先填写 API 地址和密钥");
            return;
        }

        // 获取模型按钮改为加载状态
        var btn = (Button)sender;
        btn.IsEnabled = false;
        btn.Content = "⏳ 获取中...";
        ApiModelBox.IsEnabled = false;
        ImageModelBox.IsEnabled = false;
        VideoModelBox.IsEnabled = false;

        try
        {
            var models = await ApiService.FetchModelsAsync(endpoint, apiKey);
            // 填充三个模型下拉框
            ApiModelBox.ItemsSource = models;
            ImageModelBox.ItemsSource = models;
            VideoModelBox.ItemsSource = models;

            if (!string.IsNullOrEmpty(_config.ApiModel))
                ApiModelBox.SelectedItem = _config.ApiModel;
            if (!string.IsNullOrEmpty(_config.ImageModel))
                ImageModelBox.SelectedItem = _config.ImageModel;
            if (!string.IsNullOrEmpty(_config.VideoModel))
                VideoModelBox.SelectedItem = _config.VideoModel;

            MessageDialog.Show("成功", $"获取到 {models.Count} 个模型");
        }
        catch (ApiException ex)
        {
            MessageDialog.Show("获取失败", ex.Message);
        }
        catch (Exception ex)
        {
            MessageDialog.Show("获取失败", $"网络错误：{ex.Message}");
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content = "⬇ 获取模型列表";
            ApiModelBox.IsEnabled = true;
            ImageModelBox.IsEnabled = true;
            VideoModelBox.IsEnabled = true;
        }
    }

    // ==================== 自动更新 ====================
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateStatusLabel.Visibility = Visibility.Visible;
        UpdateStatusLabel.Foreground = (Brush)FindResource("TextSecondaryBrush");
        UpdateStatusLabel.Text = "正在检查更新...";

        try
        {
            var result = await App.CheckForUpdateAsync(forceCheck: true);
            switch (result)
            {
                case App.UpdateCheckResult.NoUpdate:
                    UpdateStatusLabel.Foreground = (Brush)FindResource("SuccessBrush");
                    UpdateStatusLabel.Text = $"✓ 已是最新版本 (v{App.AppVersion})";
                    break;
                case App.UpdateCheckResult.NetworkError:
                    UpdateStatusLabel.Foreground = (Brush)FindResource("DangerBrush");
                    UpdateStatusLabel.Text = string.IsNullOrEmpty(App.LastUpdateError)
                        ? "✗ 网络不可用，请检查网络连接"
                        : $"✗ {App.LastUpdateError}";
                    break;
                case App.UpdateCheckResult.RateLimited:
                    UpdateStatusLabel.Foreground = (Brush)FindResource("WarningBrush");
                    UpdateStatusLabel.Text = string.IsNullOrEmpty(App.LastUpdateError)
                        ? "⚠ GitHub API 请求频繁，请稍后重试"
                        : $"⚠ {App.LastUpdateError}";
                    break;
                case App.UpdateCheckResult.HasUpdateNoMsi:
                case App.UpdateCheckResult.HasUpdate:
                    // 弹窗已经在 CheckForUpdateAsync 中处理了
                    UpdateStatusLabel.Foreground = (Brush)FindResource("SuccessBrush");
                    UpdateStatusLabel.Text = "✓ 操作完成";
                    break;
            }
        }
        catch
        {
            UpdateStatusLabel.Foreground = (Brush)FindResource("DangerBrush");
            UpdateStatusLabel.Text = string.IsNullOrEmpty(App.LastUpdateError)
                ? "✗ 检查失败，请稍后重试"
                : $"✗ {App.LastUpdateError}";
        }

        CheckUpdateBtn.IsEnabled = true;

        // 3 秒后自动隐藏状态
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { timer.Stop(); UpdateStatusLabel.Visibility = Visibility.Collapsed; };
        timer.Start();
    }

    // ==================== 备份与恢复 ====================
    private void BackupData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "ZIP文件|*.zip",
            FileName = $"YangzaiWorkshop_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
            Title = "选择备份保存位置"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                FileService.BackupData(App.WorkRoot, dlg.FileName);
                var size = new FileInfo(dlg.FileName).Length;
                MessageDialog.Show("备份完成",
                    $"备份成功！\n\n保存位置：{dlg.FileName}\n文件大小：{FormatFileSize(size)}");
            }
            catch (Exception ex)
            {
                MessageDialog.Show("错误", $"备份失败：{ex.Message}");
            }
        }
    }

    private void RestoreData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "ZIP备份文件|*.zip",
            Title = "选择要恢复的备份文件"
        };

        if (dlg.ShowDialog() != true) return;

        if (!IsValidBackup(dlg.FileName))
        {
            MessageDialog.Show("无效备份", "所选文件不是有效的 Yangzai Workshop 备份文件。");
            return;
        }

        if (!MessageDialog.Confirm("确认恢复数据", "恢复数据将覆盖当前所有数据！\n\n确定要继续吗？")) return;

        try
        {
            var safetyBackup = Path.Combine(
                Path.GetDirectoryName(App.WorkRoot)!,
                $"SafetyBackup_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            FileService.BackupData(App.WorkRoot, safetyBackup);

            FileService.RestoreData(App.WorkRoot, dlg.FileName);
            FileService.InitializeWorkData(App.WorkRoot);
            _config = FileService.LoadConfig(App.WorkRoot);
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            // 清除所有页面缓存，确保恢复后各页面重新加载最新数据
            NavigationService.Instance.ClearCache();
            RefreshSettings();
            ThemeService.InitTheme(App.WorkRoot);
            if (_config.FollowSystemTheme)
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            MessageDialog.Show("恢复完成", "数据恢复成功！\n\n请重新浏览各页面以加载恢复的数据。");
        }
        catch (Exception ex)
        {
            MessageDialog.Show("恢复失败", $"恢复失败：{ex.Message}\n\n已自动备份恢复前的数据。");
        }
    }

    private static bool IsValidBackup(string zipPath)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            return archive.Entries.Any(e =>
                e.FullName.EndsWith("Config/appsettings.json", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    // ==================== 关于 ====================
    private void GitHubLink_Click(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/CookuBlack/Yangzai-Workshop") { UseShellExecute = true }); }
        catch { }
    }

    // ==================== 工具方法 ====================
    private void SaveConfig()
    {
        FileService.SaveConfig(App.WorkRoot, _config);
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
