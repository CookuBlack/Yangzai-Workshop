using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

public partial class SettingsPage : UserControl
{
    private AppConfig _config = null!;
    private bool _isLoading;

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
        RefreshSettings();

        if (_config.FollowSystemTheme)
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // 加载关于图标
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
        ThemeManualPanel.Visibility = _config.FollowSystemTheme
            ? Visibility.Collapsed : Visibility.Visible;

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

        // GitHub Token（显示星号掩码）
        if (!string.IsNullOrEmpty(_config.GitHubToken))
        {
            GitHubTokenBox.Password = _config.GitHubToken;
        }

        // 版本信息（统一从 App.AppVersion 读取）
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
    private void LightRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.Theme = "Light";
        ThemeService.ApplyTheme("Light", App.WorkRoot);
        SaveConfig();
    }

    private void DarkRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.Theme = "Dark";
        ThemeService.ApplyTheme("Dark", App.WorkRoot);
        SaveConfig();
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
        ThemeService.ApplyTheme(_config.Theme, App.WorkRoot);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            Dispatcher.Invoke(ApplySystemTheme);
        }
    }

    private void ApplySystemTheme()
    {
        var isLight = ThemeService.IsSystemLightTheme();
        var theme = isLight ? "Light" : "Dark";
        if (ThemeService.CurrentTheme != theme)
        {
            ThemeService.ApplyTheme(theme, App.WorkRoot);
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
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow == null) return;
            // 查找 ScriptPage 中的文本框并应用字体大小
            var scriptPage = FindVisualChild<ScriptPage>(mainWindow);
            if (scriptPage != null)
                scriptPage.ApplyFontSize(fontSize);
        }
        catch { /* 非关键路径 */ }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private void AutoPlayCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.AutoPlayBanner = AutoPlayCheck.IsChecked == true;
        SaveConfig();
    }

    private void IntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.BannerIntervalSeconds = (int)e.NewValue;
        IntervalLabel.Text = $"{_config.BannerIntervalSeconds}秒";
        SaveConfig();
    }

    // ==================== 自动更新 ====================
    private void GitHubTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded) return;
        _config.GitHubToken = GitHubTokenBox.Password;
        SaveConfig();
    }

    private void GitHubTokenHelp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                "https://github.com/settings/tokens/new?description=YangzaiWorkshop&scopes=repo")
            { UseShellExecute = true });
        }
        catch { }
    }

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
                    UpdateStatusLabel.Text = "✗ 网络不可用，请检查网络连接";
                    break;
                case App.UpdateCheckResult.RateLimited:
                    UpdateStatusLabel.Foreground = (Brush)FindResource("WarningBrush");
                    UpdateStatusLabel.Text = "⚠ GitHub API 速率限制，请配置 Token 或稍后重试";
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
            UpdateStatusLabel.Text = "✗ 检查失败，请稍后重试";
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
