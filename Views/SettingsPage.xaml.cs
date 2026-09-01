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
    private bool _syncingInput;

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
        BgOpacityInput.Text = ((int)Math.Round(_config.CustomBgOpacity * 100)).ToString();
        BgBlurSlider.Value = _config.CustomBgBlur;
        BgBlurInput.Text = _config.CustomBgBlur.ToString("F0");
        RefreshBgPreview();

        // 工作目录（显示相对路径）
        WorkPathText.Text = GetRelativePath(App.WorkRoot);
        WorkPathText.ToolTip = App.WorkRoot;

        // 通用设置
        AutoSaveCheck.IsChecked = _config.AutoSaveScript;
        FontSizeSlider.Value = _config.FontSize;
        FontSizeInput.Text = _config.FontSize.ToString();
        AutoPlayCheck.IsChecked = _config.AutoPlayBanner;
        IntervalSlider.Value = _config.BannerIntervalSeconds;
        IntervalInput.Text = _config.BannerIntervalSeconds.ToString("F0");
        AutoBackupCheck.IsChecked = _config.AutoBackup;
        BackupIntervalSlider.Value = _config.BackupIntervalHours;
        BackupIntervalInput.Text = _config.BackupIntervalHours.ToString("F0");
        HistoryCountSlider.Value = _config.TextHistoryMaxCount;
        HistoryCountInput.Text = _config.TextHistoryMaxCount.ToString("F0");

        // AI 接口配置概览（完整配置在独立的「AI 接口配置」窗口管理）
        UpdateAiConfigSummary();

        // 音乐播放器
        MusicAutoPlayCheck.IsChecked = _config.MusicAutoPlay;
        RefreshSettingsMusicList();

        // 版本信息
        VersionLabel.Text = $"v{App.AppVersion}";
        UpdateDateLabel.Text = _config.LastUpdateDate;
        VersionSubText.Text = $"版本 v{App.AppVersion} · 更新于 {_config.LastUpdateDate}";
        VersionNumText.Text = App.AppVersion;
        UpdateVersionSubText.Text = $"上次更新：{_config.LastUpdateDate}";
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
        if (!BgOpacityInput.IsKeyboardFocusWithin)
            BgOpacityInput.Text = ((int)Math.Round(_config.CustomBgOpacity * 100)).ToString();
    }

    private void BgBlur_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.CustomBgBlur = Math.Round(e.NewValue, 0);
        if (!BgBlurInput.IsKeyboardFocusWithin)
            BgBlurInput.Text = _config.CustomBgBlur.ToString("F0");
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
        BgOpacityInput.Text = "35";
        BgBlurSlider.Value = 15;
        BgBlurInput.Text = "15";
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
        FontSizeInput.Text = defaultFontSize.ToString();

        // 轮播自动播放
        _config.AutoPlayBanner = true;
        AutoPlayCheck.IsChecked = true;

        // 轮播间隔
        const int defaultInterval = 5;
        _config.BannerIntervalSeconds = defaultInterval;
        IntervalSlider.Value = defaultInterval;
        IntervalInput.Text = defaultInterval.ToString();

        // 自动备份
        _config.AutoBackup = false;
        AutoBackupCheck.IsChecked = false;
        _config.BackupIntervalHours = 24;
        BackupIntervalSlider.Value = 24;
        BackupIntervalInput.Text = "24";

        // 文本历史上限（恢复默认 50）
        _config.TextHistoryMaxCount = TextHistoryService.DefaultMaxHistory;
        HistoryCountSlider.Value = TextHistoryService.DefaultMaxHistory;
        HistoryCountInput.Text = TextHistoryService.DefaultMaxHistory.ToString();
        TextHistoryService.Instance.MaxHistory = TextHistoryService.DefaultMaxHistory;

        // ComfyUI 本地生图
        _config.ComfyUiEndpoint = "http://127.0.0.1:8188";
        _config.ComfyUiWorkflowFile = "";

        // 默认生图引擎恢复为云端 API
        _config.DefaultImageProvider = "Api";

        // 刷新 AI 接口配置概览
        UpdateAiConfigSummary();

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
        if (!FontSizeInput.IsKeyboardFocusWithin)
            FontSizeInput.Text = _config.FontSize.ToString();
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
        if (!BackupIntervalInput.IsKeyboardFocusWithin)
            BackupIntervalInput.Text = _config.BackupIntervalHours.ToString("F0");
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
        // 反射取值时增加空保护：避免 DataContext 为匿名类型时 GetValue 返回 null 导致崩溃
        var prop = btn.DataContext.GetType().GetProperty("Path");
        if (prop == null) return;
        var value = prop.GetValue(btn.DataContext);
        if (value is not string path || string.IsNullOrEmpty(path)) return;
        MusicPlayerService.Instance.DeleteFile(path);
        RefreshSettingsMusicList();
    }

    private void IntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.BannerIntervalSeconds = (int)e.NewValue;
        if (!IntervalInput.IsKeyboardFocusWithin)
            IntervalInput.Text = _config.BannerIntervalSeconds.ToString("F0");
        SaveConfig();
    }

    private void HistoryCountSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.TextHistoryMaxCount = (int)e.NewValue;
        if (!HistoryCountInput.IsKeyboardFocusWithin)
            HistoryCountInput.Text = _config.TextHistoryMaxCount.ToString("F0");
        SaveConfig();
        // 实时生效到历史服务
        TextHistoryService.Instance.MaxHistory = _config.TextHistoryMaxCount;
    }

    // ===== 滑块数值输入与步进微调 =====

    private static double Clamp(double v, double min, double max)
        => Math.Max(min, Math.Min(max, v));

    /// <summary>“+ / −”按钮微调：根据 Tag 判断目标滑块与方向，超出阈值自动钳制到边界</summary>
    private void StepBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        var parts = tag.Split(',');
        if (parts.Length != 2) return;
        var delta = parts[1] == "+" ? 1 : -1;
        switch (parts[0])
        {
            case "opacity":
                BgOpacitySlider.Value = Clamp(BgOpacitySlider.Value + delta * 0.05, 0.05, 1.0);
                break;
            case "blur":
                BgBlurSlider.Value = Clamp(BgBlurSlider.Value + delta, 0, 50);
                break;
            case "fontsize":
                FontSizeSlider.Value = Clamp(FontSizeSlider.Value + delta, 10, 24);
                break;
            case "interval":
                IntervalSlider.Value = Clamp(IntervalSlider.Value + delta, 2, 15);
                break;
            case "backup":
                BackupIntervalSlider.Value = Clamp(BackupIntervalSlider.Value + delta, 1, 72);
                break;
            case "historycount":
                HistoryCountSlider.Value = Clamp(HistoryCountSlider.Value + delta, 10, 200);
                break;
        }
    }

    /// <summary>数值输入框：实时解析输入并钳制到阈值范围后同步滑块</summary>
    private void NumericInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || !IsLoaded || _syncingInput) return;
        if (sender is not TextBox tb) return;
        if (!double.TryParse(tb.Text.Trim(), out var val)) return;
        switch (tb.Name)
        {
            case "BgOpacityInput":
                BgOpacitySlider.Value = Clamp(val / 100.0, 0.05, 1.0);
                break;
            case "BgBlurInput":
                BgBlurSlider.Value = Clamp(val, 0, 50);
                break;
            case "FontSizeInput":
                FontSizeSlider.Value = Clamp(val, 10, 24);
                break;
            case "IntervalInput":
                IntervalSlider.Value = Clamp(val, 2, 15);
                break;
            case "BackupIntervalInput":
                BackupIntervalSlider.Value = Clamp(val, 1, 72);
                break;
            case "HistoryCountInput":
                HistoryCountSlider.Value = Clamp(val, 10, 200);
                break;
        }
    }

    /// <summary>数值输入框失焦：把输入框统一为钳制后的有效值，并同步滑块</summary>
    private void NumericInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        string expected;
        switch (tb.Name)
        {
            case "BgOpacityInput":
                expected = ((int)Math.Round(Clamp(BgOpacitySlider.Value, 0.05, 1.0) * 100)).ToString();
                break;
            case "BgBlurInput":
                expected = Clamp(BgBlurSlider.Value, 0, 50).ToString("F0");
                break;
            case "FontSizeInput":
                expected = Clamp(FontSizeSlider.Value, 10, 24).ToString("F0");
                break;
            case "IntervalInput":
                expected = Clamp(IntervalSlider.Value, 2, 15).ToString("F0");
                break;
            case "BackupIntervalInput":
                expected = Clamp(BackupIntervalSlider.Value, 1, 72).ToString("F0");
                break;
            case "HistoryCountInput":
                expected = Clamp(HistoryCountSlider.Value, 10, 200).ToString("F0");
                break;
            default: return;
        }
        if (tb.Text.Trim() != expected)
        {
            _syncingInput = true;
            tb.Text = expected;
            _syncingInput = false;
        }
    }

    // ==================== AI 接口配置（独立窗口管理） ====================

    /// <summary>打开独立的「AI 接口配置」窗口（文本/图片/视频接口与 ComfyUI 集中管理）</summary>
    private void OpenAiConfig_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var win = new AiApiConfigWindow
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (win.ShowDialog() == true)
        {
            // 保存后重新加载配置并刷新概览
            _config = FileService.LoadConfig(App.WorkRoot);
            UpdateAiConfigSummary();
        }
    }

    /// <summary>刷新「AI 接口配置」卡片中的当前配置概览</summary>
    private void UpdateAiConfigSummary()
    {
        if (AiConfigSummaryText == null) return;

        var cfg = _config;
        var lines = new List<string>
        {
            $"💬 文本：{AiModelCatalog.ProviderName(cfg.TextApi.Provider)} · {ModelOrDefault(cfg.TextApi.ModelId)}",
            $"🖼️ 图片：{AiModelCatalog.ProviderName(cfg.ImageApi.Provider)} · {ModelOrDefault(cfg.ImageApi.ModelId)}",
            $"🎬 视频：{AiModelCatalog.ProviderName(cfg.VideoApi.Provider)} · {ModelOrDefault(cfg.VideoApi.ModelId)}",
            cfg.DefaultImageProvider == "ComfyUI"
                ? $"🖥️ 默认生图引擎：本地 ComfyUI（{cfg.ComfyUiEndpoint}）"
                : "🖥️ 默认生图引擎：云端 API"
        };
        AiConfigSummaryText.Text = string.Join("\n", lines);

        static string ModelOrDefault(string model) =>
            string.IsNullOrWhiteSpace(model) ? "未设置" : model;
    }

    private void EditSkill_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window
        {
            Title = "编辑 AI Skill",
            Width = 680, Height = 580,
            MinWidth = 540, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.CanResize,
            Background = (Brush)FindResource("WindowBackgroundBrush")
        };

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 顶部 Tab
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 1 优化子 Tab（默认隐藏）
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 2 编辑区
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 3 提示
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });                    // 4 间距
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 5 底部按钮

        // 四个编辑框：生成剧本 / 生成提示词 / 图片优化 / 视频优化
        var scriptBox = MakeSkillBox(_config.ScriptSkill);
        var promptBox = MakeSkillBox(_config.PromptSkill);
        var imgOptBox = MakeSkillBox(_config.ImageOptimizeSkill);
        var vidOptBox = MakeSkillBox(_config.VideoOptimizeSkill);
        foreach (var box in new[] { scriptBox, promptBox, imgOptBox, vidOptBox })
        {
            Grid.SetRow(box, 2);
            grid.Children.Add(box);
        }
        promptBox.Visibility = Visibility.Collapsed;
        imgOptBox.Visibility = Visibility.Collapsed;
        vidOptBox.Visibility = Visibility.Collapsed;

        // 顶部 Tab：生成剧本 / 生成提示词 / 优化提示词
        var scriptBtn = CreateTabBtn("生成剧本 Skill", true);
        var promptBtn = CreateTabBtn("生成提示词 Skill", false);
        var optBtn = CreateTabBtn("优化提示词 Skill", false);
        var topBar = new StackPanel { Orientation = Orientation.Horizontal };
        topBar.Children.Add(scriptBtn);
        topBar.Children.Add(promptBtn);
        topBar.Children.Add(optBtn);
        Grid.SetRow(topBar, 0);
        grid.Children.Add(topBar);

        // 优化提示词的子 Tab：图片 / 视频
        var imgTab = CreateTabBtn("图片优化", true);
        var vidTab = CreateTabBtn("视频优化", false);
        var subBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        subBar.Children.Add(imgTab);
        subBar.Children.Add(vidTab);
        Grid.SetRow(subBar, 1);
        grid.Children.Add(subBar);

        // 占位符说明提示
        var tipText = new TextBlock
        {
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextTertiaryBrush"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        Grid.SetRow(tipText, 3);
        grid.Children.Add(tipText);

        // 统一切换逻辑
        void Activate(TextBox show, string tip)
        {
            scriptBox.Visibility = show == scriptBox ? Visibility.Visible : Visibility.Collapsed;
            promptBox.Visibility = show == promptBox ? Visibility.Visible : Visibility.Collapsed;
            imgOptBox.Visibility = show == imgOptBox ? Visibility.Visible : Visibility.Collapsed;
            vidOptBox.Visibility = show == vidOptBox ? Visibility.Visible : Visibility.Collapsed;
            subBar.Visibility = (show == imgOptBox || show == vidOptBox) ? Visibility.Visible : Visibility.Collapsed;
            SetTab(scriptBtn, show == scriptBox);
            SetTab(promptBtn, show == promptBox);
            SetTab(optBtn, show == imgOptBox || show == vidOptBox);
            SetTab(imgTab, show == imgOptBox);
            SetTab(vidTab, show == vidOptBox);
            tipText.Text = tip;
        }

        scriptBtn.Click += (_, _) => Activate(scriptBox, "生成剧本时使用的 System Prompt 指令。");
        promptBtn.Click += (_, _) => Activate(promptBox, "生成提示词时使用的 System Prompt 指令。");
        optBtn.Click += (_, _) => Activate(imgOptBox,
            "优化提示词时使用的 System Prompt 指令。可用占位符：{hasRef} 参考图情况、{refCount} 参考图数量、{roleName} 角色名、{personality} 角色性格、{prompt} 原提示词。");
        imgTab.Click += (_, _) => Activate(imgOptBox, "优化图片生成提示词时使用的指令。占位符同上。");
        vidTab.Click += (_, _) => Activate(vidOptBox, "优化视频生成提示词时使用的指令。占位符同上。");

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
            imgOptBox.Text = def.ImageOptimizeSkill;
            vidOptBox.Text = def.VideoOptimizeSkill;
        };
        saveBtn.Click += (_, _) =>
        {
            _config.ScriptSkill = scriptBox.Text;
            _config.PromptSkill = promptBox.Text;
            _config.ImageOptimizeSkill = imgOptBox.Text;
            _config.VideoOptimizeSkill = vidOptBox.Text;
            SaveConfig();
            win.Close();
        };
        footer.Children.Add(resetBtn);
        footer.Children.Add(saveBtn);
        Grid.SetRow(footer, 5);
        grid.Children.Add(footer);

        win.Content = grid;
        win.ShowDialog();
    }

    /// <summary>创建 Skill 编辑用的多行文本框</summary>
    private TextBox MakeSkillBox(string text)
    {
        return new TextBox
        {
            Text = text,
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
    }

    /// <summary>设置页签的激活/非激活样式</summary>
    private void SetTab(Button btn, bool active)
    {
        if (active)
        {
            btn.Background = (Brush)FindResource("PrimaryBrush");
            btn.Foreground = Brushes.White;
            btn.BorderBrush = (Brush)FindResource("PrimaryBrush");
            btn.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            btn.Background = (Brush)FindResource("CardBackgroundBrush");
            btn.Foreground = (Brush)FindResource("TextPrimaryBrush");
            btn.BorderBrush = (Brush)FindResource("BorderBrush");
            btn.FontWeight = FontWeights.Normal;
        }
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

    // ==================== 自动更新 ====================
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        SetUpdateStatus("正在检查更新，请稍候...", "TextSecondaryBrush", showSpinner: true);

        try
        {
            var result = await App.CheckForUpdateAsync(forceCheck: true);
            switch (result)
            {
                case App.UpdateCheckResult.NoUpdate:
                    SetUpdateStatus($"✓ 已是最新版本 v{App.AppVersion}", "SuccessBrush");
                    break;
                case App.UpdateCheckResult.NetworkError:
                    SetUpdateStatus(string.IsNullOrEmpty(App.LastUpdateError)
                        ? "✗ 网络不可用，请检查网络连接"
                        : $"✗ {App.LastUpdateError}", "DangerBrush");
                    break;
                case App.UpdateCheckResult.RateLimited:
                    SetUpdateStatus(string.IsNullOrEmpty(App.LastUpdateError)
                        ? "⚠ GitHub API 请求频繁，请稍后重试"
                        : $"⚠ {App.LastUpdateError}", "WarningBrush");
                    break;
                case App.UpdateCheckResult.HasUpdateNoMsi:
                case App.UpdateCheckResult.HasUpdate:
                    SetUpdateStatus("✓ 已开始下载更新", "SuccessBrush");
                    break;
            }
        }
        catch
        {
            SetUpdateStatus(string.IsNullOrEmpty(App.LastUpdateError)
                ? "✗ 检查失败，请稍后重试"
                : $"✗ {App.LastUpdateError}", "DangerBrush");
        }

        CheckUpdateBtn.IsEnabled = true;
        // 更新检查时间
        UpdateVersionSubText.Text = $"上次检查：{DateTime.Now:yyyy-MM-dd HH:mm}";
    }

    /// <summary>设置更新状态栏的显示样式和文字</summary>
    private void SetUpdateStatus(string text, string brushKey, bool showSpinner = false)
    {
        UpdateStatusBar.Visibility = Visibility.Visible;
        UpdateStatusBar.Background = (Brush)FindResource("HoverBrush");
        UpdateStatusLabel.Foreground = (Brush)FindResource(brushKey);
        UpdateStatusLabel.Text = text;
        UpdateSpinner.Visibility = showSpinner ? Visibility.Visible : Visibility.Collapsed;
        // 旋转动画
        if (showSpinner)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 360,
                new Duration(TimeSpan.FromSeconds(1)))
            { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
            SpinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, anim);
        }
        else
        {
            SpinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        }
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
