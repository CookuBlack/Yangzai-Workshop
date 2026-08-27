using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YangzaiWorkshop.Services;

/// <summary>跨页面复用工具方法</summary>
public static class ViewHelpers
{
    // ===== AI 生成尺寸 =====

    /// <summary>图片可选比例（宽:高）</summary>
    public static readonly string[] ImageRatios = { "1:1", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9" };

    /// <summary>图片可选像素档位（短边像素，1K=1024）</summary>
    public static readonly string[] ImageLevels = { "0.5K", "1K", "1.5K", "2K", "3K", "4K" };

    /// <summary>视频可选分辨率档位（agnes-video-2.5 非 Flash：720P/960P/2K）</summary>
    public static readonly string[] VideoLevels = { "720P", "960P", "2K" };

    /// <summary>视频可选比例（agnes-video 2.5 支持 21:9 / 16:9 / 4:3 / 1:1 / 3:4 / 9:16）</summary>
    public static readonly string[] VideoRatios = { "16:9", "9:16", "1:1", "4:3", "3:4", "21:9" };

    /// <summary>是否为 Flash 视频模型（Flash 固定 size=720P，且不支持参考视频）</summary>
    public static bool IsFlashVideoModel(string? model) =>
        model?.Contains("flash", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>按视频模型返回可用分辨率档位：Flash 仅 720P，非 Flash 支持 720P/960P/2K</summary>
    public static string[] VideoLevelsForModel(string? model) =>
        IsFlashVideoModel(model) ? new[] { "720P" } : VideoLevels;

    /// <summary>
    /// 视频时长上限（秒）：agnes-video 2.5 / 2.5-flash 的 seconds 支持字符串 "4"–"12"。
    /// </summary>
    public static int CalcVideoMaxSeconds(string level, string ratio) => 12;

    private static (double w, double h) GetRatio(string ratio) => ratio switch
    {
        "3:4" => (3, 4), "4:3" => (4, 3), "4:5" => (4, 5), "5:4" => (5, 4),
        "9:16" => (9, 16), "16:9" => (16, 9), "21:9" => (21, 9),
        _ => (1, 1)
    };

    private static int GetPixelLevel(string level) => level switch
    {
        "0.5K" => 512, "1.5K" => 1536, "2K" => 2048, "3K" => 3072, "4K" => 4096,
        _ => 1024
    };

    /// <summary>根据比例 + 像素档位计算图片实际尺寸（短边=档位像素，长边取 8 的倍数），返回 "宽x高"</summary>
    public static string CalcImageSize(string ratio, string level)
    {
        var (rw, rh) = GetRatio(ratio);
        int shortSide = GetPixelLevel(level);
        int w, h;
        if (rw >= rh)
        {
            h = shortSide;
            w = (int)Math.Round(shortSide * rw / rh / 8.0) * 8;
        }
        else
        {
            w = shortSide;
            h = (int)Math.Round(shortSide * rh / rw / 8.0) * 8;
        }
        return $"{w}x{h}";
    }

    /// <summary>
    /// 参考模式下按 agnes-video 2.5 文档补齐 <Picture N>/<Video N> 提示词引用
    /// imageCount/videoCount 为参考图/参考视频张数（仅当用户未自行书写时才自动追加，避免覆盖已有引用）。
    /// </summary>
    public static string BuildVideoPrompt(string prompt, int imageCount, int videoCount)
    {
        var p = prompt.Trim();
        var suffix = new List<string>();
        if (imageCount > 0 && !p.Contains("<Picture", StringComparison.OrdinalIgnoreCase))
        {
            var tags = Enumerable.Range(1, imageCount).Select(i => $"<Picture {i}>").ToList();
            suffix.Add(imageCount == 1
                ? "以 <Picture 1> 为参考，保持主体外观与风格一致"
                : $"以 {string.Join("、", tags)} 为参考，保持各主体外观与风格一致");
        }
        if (videoCount > 0 && !p.Contains("<Video", StringComparison.OrdinalIgnoreCase))
        {
            var tags = Enumerable.Range(1, videoCount).Select(i => $"<Video {i}>").ToList();
            suffix.Add(videoCount == 1
                ? "参考 <Video 1> 的动作与镜头节奏，保持时序连贯"
                : $"参考 {string.Join("、", tags)} 的动作与镜头节奏，保持时序连贯");
        }
        if (suffix.Count > 0)
            p = (p.Length > 0 ? p + "，" : "") + string.Join("，", suffix);
        return p;
    }

    /// <summary>本地视频文件转 base64 data URL（供视频参考 videos[].url 使用）</summary>
    public static string? VideoToBase64DataUrl(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var mime = ext switch
            {
                ".mov" => "video/quicktime",
                ".mkv" => "video/x-matroska",
                ".avi" => "video/x-msvideo",
                ".webm" => "video/webm",
                ".wmv" => "video/x-ms-wmv",
                _ => "video/mp4"
            };
            var bytes = File.ReadAllBytes(filePath);
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch { return null; }
    }

    /// <summary>
    /// 收集当前项目中的全部图片资产（章节图片 + 人物素材图片 + 小说封面 + 角色头像）。
    /// </summary>
    public static List<string> CollectProjectImagePaths(string workRoot, string novelId, string mediaFolder)
    {
        var list = new List<string>();
        AddDirImages(list, Path.Combine(FileService.ImageRoot(workRoot), "小说", mediaFolder));
        AddDirImages(list, Path.Combine(FileService.ImageRoot(workRoot), "人物素材", mediaFolder));
        AddFile(list, FileService.NovelCoverFile(workRoot, novelId));
        // 角色头像（Characters\{novelId}\{charId}\avatar.png）
        try
        {
            var charsBase = Path.Combine(FileService.CharactersPath(workRoot), novelId);
            if (Directory.Exists(charsBase))
                foreach (var dir in Directory.GetDirectories(charsBase))
                    AddFile(list, Path.Combine(dir, "avatar.png"));
        }
        catch { }
        return list;
    }

    private static void AddDirImages(List<string> list, string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif")
                    list.Add(f);
            }
        }
        catch { }
    }

    private static void AddFile(List<string> list, string file)
    {
        try { if (File.Exists(file)) list.Add(file); } catch { }
    }

    /// <summary>
    /// 弹出项目图片选择器窗口，从当前项目已有图片资产中选择一张，返回其完整路径（未选择返回 null）。
    /// 注意：owner 必须传 AI 小窗口（而非主窗口）——模态选择器关闭时会激活 owner，
    /// 若 owner 是 AllowsTransparency 主窗口会触发其误最小化，传小窗口则只激活小窗口。
    /// </summary>
    public static string? PickProjectImage(
        System.Windows.Window owner, string title,
        string workRoot, string novelId, string mediaFolder)
    {
        var paths = CollectProjectImagePaths(workRoot, novelId, mediaFolder);
        var picker = new YangzaiWorkshop.Views.AssetPickerWindow(paths, title) { Owner = owner };
        if (picker.ShowDialog() == true) return picker.SelectedPath;
        return null;
    }

    /// <summary>
    /// 多选项目图片并按点击顺序返回（顺序即参考图顺序）。取消返回 null。
    /// 用于参考图（图像/视频）支持一次选择多张素材。
    /// </summary>
    public static IReadOnlyList<string> PickProjectImages(
        System.Windows.Window owner, string title,
        string workRoot, string novelId, string mediaFolder)
    {
        var paths = CollectProjectImagePaths(workRoot, novelId, mediaFolder);
        var picker = new YangzaiWorkshop.Views.AssetPickerWindow(paths, title, multiSelect: true) { Owner = owner };
        if (picker.ShowDialog() != true) return Array.Empty<string>();
        return picker.OrderedPaths;
    }

    /// <summary>是否为受支持的图片文件扩展名</summary>
    private static bool IsSupportedImageFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";
    }

    /// <summary>
    /// 复制图片文件到目标目录（重名自动追加序号），返回导入后的完整路径；
    /// 目标目录或复制失败时返回 null。用于把拖入的图片归入项目资产以便下次复用。
    /// </summary>
    public static string? ImportImageIntoAsset(string srcFile, string destDir)
    {
        try
        {
            if (!File.Exists(srcFile) || !IsSupportedImageFile(srcFile)) return null;
            Directory.CreateDirectory(destDir);
            var fileName = Path.GetFileName(srcFile);
            var dest = Path.Combine(destDir, fileName);
            int i = 1;
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            while (File.Exists(dest))
                dest = Path.Combine(destDir, $"{baseName}_{i++}{ext}");
            File.Copy(srcFile, dest, overwrite: false);
            return dest;
        }
        catch { return null; }
    }

    /// <summary>
    /// 为宿主元素（生成窗口根网格/窗口）启用图片拖放。
    /// 拖入的图片先复制到 assetImportDir（归入项目资产，供下次「项目资产」选择），
    /// 再回调 onImported(导入后路径)。非图片文件回调 onInvalid（可空）。
    /// </summary>
    public static void EnableImageDrop(
        System.Windows.FrameworkElement host, string assetImportDir,
        Action<string> onImported, Action? onInvalid = null)
    {
        host.AllowDrop = true;
        host.PreviewDragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        host.PreviewDrop += (_, e) =>
        {
            e.Handled = true;
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;
            foreach (var f in files)
            {
                if (!IsSupportedImageFile(f)) { onInvalid?.Invoke(); continue; }
                var imported = ImportImageIntoAsset(f, assetImportDir);
                if (imported != null) onImported(imported);
                else { onInvalid?.Invoke(); }
            }
        };
    }

    /// <summary>
    /// 添加参考图缩略图到 WrapPanel。每个缩略图为带 ✕ 的 44px 圆角图块，
    /// 点击 ✕ 从 refImages 移除并删除自身。返回移除回调供批量清除使用。
    /// </summary>
    public static void AddReferenceThumb(
        System.Windows.Controls.WrapPanel panel, string filePath,
        System.Collections.Generic.List<string> refImages,
        Action onChanged, int maxCount = 6,
        System.Collections.Generic.List<string>? refPaths = null)
    {
        if (refImages.Count >= maxCount) return;
        var data = ImageToBase64DataUrl(filePath);
        if (data == null) return;

        refImages.Add(data);
        refPaths?.Add(filePath);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(filePath);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 88;
        bmp.EndInit();

        var img = new Image
        {
            Source = bmp, Stretch = Stretch.UniformToFill,
            Width = 44, Height = 44, Margin = new Thickness(0, 0, 6, 0),
            SnapsToDevicePixels = true
        };
        var clip = new RectangleGeometry(new Rect(0, 0, 44, 44), 4, 4);
        img.Clip = clip;

        var delBtn = new Button
        {
            Content = "✕", FontSize = 9, Width = 16, Height = 16,
            Padding = new Thickness(0), Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Style = Application.Current.FindResource("SecondaryButtonStyle") as Style,
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };

        var border = new Border
        {
            Width = 44, Height = 44, Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(4),
            Tag = "refthumb",
            ClipToBounds = true
        };
        border.Child = new Grid
        {
            Children = { img, delBtn }
        };
        panel.Children.Add(border);

        delBtn.Click += (_, _) =>
        {
            var idx = refImages.FindIndex(x => x == data);
            if (idx >= 0) refImages.RemoveAt(idx);
            if (refPaths != null && idx >= 0 && idx < refPaths.Count) refPaths.RemoveAt(idx);
            panel.Children.Remove(border);
            onChanged?.Invoke();
        };
    }

    /// <summary>更新参考图提示文字与清除按钮显隐</summary>
    public static void UpdateReferenceHint(
        System.Collections.Generic.IReadOnlyCollection<string> refImages,
        System.Windows.Controls.TextBlock hintText,
        System.Windows.Controls.Button clearBtn)
    {
        hintText.Text = refImages.Count switch
        {
            0 => "可添加 1 张（图生图）或多张（多图编辑）参考图",
            1 => "图生图模式：AI 将基于参考图进行编辑",
            _ => $"多图编辑模式：已选 {refImages.Count} 张参考图，请在提示词中说明组合方式"
        };
        clearBtn.Visibility = refImages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>本地图片文件转 base64 data URL（供图生视频参考图使用）</summary>
    public static string? ImageToBase64DataUrl(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/png"
            };
            var bytes = File.ReadAllBytes(filePath);
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch { return null; }
    }

    /// <summary>对 UIElement 应用圆角矩形裁切</summary>
    public static void ApplyRoundedClip(UIElement el, double radius = 6)
    {
        double w = (el is FrameworkElement fe) ? fe.ActualWidth : el.RenderSize.Width;
        double h = (el is FrameworkElement fe2) ? fe2.ActualHeight : el.RenderSize.Height;
        if (w > 0 && h > 0)
            el.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
    }

    /// <summary>安全解析色值字符串为 SolidColorBrush</summary>
    public static SolidColorBrush ParseColor(string hex, string fallback = "#4A90E2")
    {
        // 入参空保护：ColorConverter.ConvertFromString(null) 会抛 ArgumentNullException
        if (string.IsNullOrWhiteSpace(hex)) hex = fallback;
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)); }
                catch { return new SolidColorBrush(Colors.Gray); } }
    }

    /// <summary>数值友好格式化（万/k）</summary>
    public static string FormatNumber(long n)
    {
        if (n >= 10000) return $"{n / 10000.0:F1}万";
        if (n >= 1000) return $"{n / 1000.0:F1}k";
        return n.ToString();
    }

    /// <summary>封面占位文字</summary>
    public static TextBlock CoverPlaceholder(string name, int maxChars = 2, double fontSize = 22)
    {
        var clipped = name.Length > 0 ? name[..Math.Min(maxChars, name.Length)] : "?";
        return new TextBlock
        {
            Text = clipped,
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>将窗口居中显示在目标窗口之上（目标为 null 时居中于屏幕）</summary>
    public static void CenterWindowOnOwner(Window win, Window? owner)
    {
        if (owner == null)
        {
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }
        // owner 尚未定位时等其加载完成后居中
        if (owner.WindowState == WindowState.Minimized || owner.Left <= 0 && owner.Top <= 0)
        {
            owner.SourceInitialized += (_, _) => DoCenter();
            return;
        }
        DoCenter();

        void DoCenter()
        {
            win.Left = owner.Left + (owner.ActualWidth - win.Width) / 2;
            win.Top = owner.Top + (owner.ActualHeight - win.Height) / 2;
        }
    }

    /// <summary>
    /// 保护 owned 子窗口（AI 生成窗口 / 项目资产选择器等）关闭时不把主窗口联动最小化。
    /// 采用两层防护：
    /// 1）子窗口打开期间调用 MainWindow.EnterChildWindow()，让主窗口 WndProc 屏蔽误发的
    ///    SC_MINIMIZE（根治：WPF 关闭 owned 窗口会误发该命令，被 WndProc 直接最小化）；
    /// 2）关闭后延迟 2.5s 解除屏蔽，并保留定时器兜底恢复（双保险）。
    /// owner 为 null 时不做任何事。
    /// </summary>
    public static void RestoreOwnerAfterClose(Window child, Window? owner)
    {
        if (owner == null) return;
        MainWindow.EnterChildWindow();

        child.Closed += (_, _) =>
        {
            // 延迟解除 SC_MINIMIZE 屏蔽，等 WPF 可能的异步最小化请求发完
            var leaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            leaveTimer.Tick += (_, _) =>
            {
                leaveTimer.Stop();
                MainWindow.LeaveChildWindow();
            };
            leaveTimer.Start();

            // 兜底：若仍被最小化，持续恢复 owner
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            var deadline = DateTime.Now.AddSeconds(2.5);
            timer.Tick += (_, _) =>
            {
                if (DateTime.Now > deadline)
                {
                    timer.Stop();
                    return;
                }
                if (owner.WindowState == WindowState.Minimized)
                {
                    owner.WindowState = WindowState.Normal;
                    owner.Activate();
                }
            };
            timer.Start();
        };
    }

    /// <summary>图片查看器（支持滚轮缩放 + 左键拖动平移 + 圆角裁切）</summary>
    public static void ShowImageViewer(string path, Window? owner = null)
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            var win = new Window
            {
                Title = System.IO.Path.GetFileName(path),
                Width = Math.Min(wa.Width * 0.85, 1400),
                Height = Math.Min(wa.Height * 0.85, 900),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                Background = Brushes.Black,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();

            var img = new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(12) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var zoom = new ScaleTransform(1, 1);
            var pan = new TranslateTransform(0, 0);
            var group = new TransformGroup();
            group.Children.Add(zoom);
            group.Children.Add(pan);
            img.RenderTransform = group;
            img.RenderTransformOrigin = new Point(0.5, 0.5);

            var outer = new Border();
            outer.SizeChanged += (s, _) =>
            {
                var b = (Border)s;
                if (b.ActualWidth > 0 && b.ActualHeight > 0)
                    b.Clip = new RectangleGeometry(new Rect(0, 0, b.ActualWidth, b.ActualHeight), 12, 12);
            };
            outer.Child = img;
            win.Content = outer;

            outer.MouseWheel += (_, e) =>
            {
                double f = e.Delta > 0 ? 1.15 : 1 / 1.15;
                double ns = Math.Max(0.2, Math.Min(8, zoom.ScaleX * f));
                zoom.ScaleX = ns; zoom.ScaleY = ns;
            };

            Point? panStart = null;
            double stx = 0, sty = 0;
            img.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 1)
                { panStart = e.GetPosition(outer); stx = pan.X; sty = pan.Y; img.CaptureMouse(); }
                else win.Close();
            };
            img.MouseMove += (_, e) =>
            {
                if (panStart.HasValue && e.LeftButton == MouseButtonState.Pressed)
                {
                    var cur = e.GetPosition(outer);
                    pan.X = stx + (cur.X - panStart.Value.X);
                    pan.Y = sty + (cur.Y - panStart.Value.Y);
                }
            };
            img.MouseLeftButtonUp += (_, _) => { panStart = null; img.ReleaseMouseCapture(); };
            win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
            win.Show();
        }
        catch { }
    }
}
