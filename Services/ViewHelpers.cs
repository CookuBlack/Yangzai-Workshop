using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YangzaiWorkshop.Services;

/// <summary>跨页面复用工具方法</summary>
public static class ViewHelpers
{
    /// <summary>
    /// 参考图 base64 缓存：按「文件路径 + 最后修改时间」判断是否复用，
    /// 避免每次点击资产重建参考图时重复读取并压缩大图（选中图片卡顿的根因之一）。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Stamp, string Data)> RefImageCache = new();

    // ===== AI 生成尺寸 =====

    /// <summary>
    /// 图片可选比例（宽:高），对应 agnes-image-2.1-flash 官方支持的 ratio 列表：
    /// 1:1 / 3:4 / 4:3 / 16:9 / 9:16 / 2:3 / 3:2 / 21:9。
    /// </summary>
    public static readonly string[] ImageRatios = { "1:1", "3:4", "4:3", "16:9", "9:16", "2:3", "3:2", "21:9" };

    /// <summary>图片可选尺寸档位（agnes-image-2.1-flash 官方推荐 size：1K / 2K / 3K / 4K）</summary>
    public static readonly string[] ImageLevels = { "1K", "2K", "3K", "4K" };

    /// <summary>视频可选分辨率档位（agnes-video-2.5 非 Flash：720P/960P/2K）</summary>
    public static readonly string[] VideoLevels = { "720P", "960P", "2K" };

    /// <summary>视频可选比例（agnes-video 2.5 支持 21:9 / 16:9 / 4:3 / 1:1 / 3:4 / 9:16）</summary>
    public static readonly string[] VideoRatios = { "16:9", "9:16", "1:1", "4:3", "3:4", "21:9" };

    /// <summary>是否为 Flash 视频模型（Flash 固定 size=720P，且不支持参考视频）</summary>
    public static bool IsFlashVideoModel(string? model) =>
        model?.Contains("flash", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>是否为 Agnes 图像模型（支持档位式 size + ratio，输出尺寸由官方表确定）</summary>
    public static bool IsAgnesImageModel(string? model) =>
        model?.Contains("agnes-image", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>按视频模型返回可用分辨率档位：Flash 仅 720P，非 Flash 支持 720P/960P/2K</summary>
    public static string[] VideoLevelsForModel(string? model) =>
        IsFlashVideoModel(model) ? new[] { "720P" } : VideoLevels;

    /// <summary>
    /// 视频时长上限（秒）：agnes-video 2.5 / 2.5-flash 的 seconds 支持字符串 "4"–"12"。
    /// </summary>
    public static int CalcVideoMaxSeconds(string level, string ratio) => 12;

    /// <summary>
    /// agnes-image-2.1-flash 官方输出尺寸表（ratio × 档位 → 宽x高）。
    /// 同一档位（如 1K）在不同比例下的实际输出不同（如 16:9 为 1312x736，3:4 为 864x1152）。
    /// </summary>
    private static readonly Dictionary<(string Ratio, string Level), string> ImageSizeTable = new()
    {
        [("1:1", "1K")] = "1024x1024", [("1:1", "2K")] = "2048x2048",
        [("1:1", "3K")] = "3072x3072", [("1:1", "4K")] = "4096x4096",
        [("3:4", "1K")] = "864x1152",  [("3:4", "2K")] = "1728x2304",
        [("3:4", "3K")] = "2592x3456", [("3:4", "4K")] = "3456x4608",
        [("4:3", "1K")] = "1152x864",  [("4:3", "2K")] = "2304x1728",
        [("4:3", "3K")] = "3456x2592", [("4:3", "4K")] = "4608x3456",
        [("16:9", "1K")] = "1312x736", [("16:9", "2K")] = "2624x1472",
        [("16:9", "3K")] = "3936x2208", [("16:9", "4K")] = "5248x2944",
        [("9:16", "1K")] = "736x1312", [("9:16", "2K")] = "1472x2624",
        [("9:16", "3K")] = "2208x3936", [("9:16", "4K")] = "2944x5248",
        [("2:3", "1K")] = "832x1248",  [("2:3", "2K")] = "1664x2496",
        [("2:3", "3K")] = "2496x3744", [("2:3", "4K")] = "3328x4992",
        [("3:2", "1K")] = "1248x832",  [("3:2", "2K")] = "2496x1664",
        [("3:2", "3K")] = "3744x2496", [("3:2", "4K")] = "4992x3328",
        [("21:9", "1K")] = "1568x672", [("21:9", "2K")] = "3136x1344",
        [("21:9", "3K")] = "4704x2016", [("21:9", "4K")] = "6272x2688"
    };

    /// <summary>按官方尺寸表返回比例+档位对应的图片实际输出尺寸（宽x高）；未知组合退回简单估算。</summary>
    public static string CalcImageSize(string ratio, string level)
    {
        if (ImageSizeTable.TryGetValue((ratio, level), out var size)) return size;
        // 兜底：短边=档位像素（1K=1024），长边按比例取 8 的倍数
        int shortSide = level switch
        {
            "2K" => 2048, "3K" => 3072, "4K" => 4096,
            _ => 1024
        };
        var (rw, rh) = ratio switch
        {
            "3:4" => (3d, 4d), "4:3" => (4d, 3d), "16:9" => (16d, 9d),
            "9:16" => (9d, 16d), "2:3" => (2d, 3d), "3:2" => (3d, 2d), "21:9" => (21d, 9d),
            _ => (1d, 1d)
        };
        int w, h;
        if (rw >= rh) { h = shortSide; w = (int)Math.Round(shortSide * rw / rh / 8.0) * 8; }
        else { w = shortSide; h = (int)Math.Round(shortSide * rh / rw / 8.0) * 8; }
        return $"{w}x{h}";
    }

    /// <summary>
    /// 参考模式下按 agnes-video 2.5 文档补齐 <Picture N>/<Video N> 提示词引用
    /// imageCount/videoCount 为参考图/参考视频张数（仅当用户未自行书写时才自动追加，避免覆盖已有引用）。
    /// </summary>
    public static string BuildVideoPrompt(string prompt, int imageCount, int videoCount, int audioCount = 0)
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
        if (audioCount > 0 && !p.Contains("<Audio", StringComparison.OrdinalIgnoreCase))
        {
            var tags = Enumerable.Range(1, audioCount).Select(i => $"<Audio {i}>").ToList();
            suffix.Add(audioCount == 1
                ? "参考 <Audio 1> 的语气、情绪与节奏，保持声轨连贯"
                : $"参考 {string.Join("、", tags)} 的语气、情绪与节奏，让多段音频衔接自然");
        }
        if (suffix.Count > 0)
            p = (p.Length > 0 ? p + "，" : "") + string.Join("，", suffix);
        return p;
    }

    /// <summary>本地音频文件转 base64 data URL（供 reference 模式 audios[].url 使用）。</summary>
    public static string? AudioToBase64DataUrl(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var mime = ext switch
            {
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".aac" => "audio/aac",
                ".wav" => "audio/wav",
                ".flac" => "audio/flac",
                ".ogg" => "audio/ogg",
                ".wma" => "audio/x-ms-wma",
                ".amr" => "audio/amr",
                _ => "audio/mpeg"
            };
            var bytes = File.ReadAllBytes(filePath);
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch { return null; }
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

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".flv", ".m4v", ".ts"
    };

    /// <summary>判断文件是否为常见视频格式。</summary>
    public static bool IsVideoFile(string path)
    {
        try { return VideoExtensions.Contains(Path.GetExtension(path)); }
        catch { return false; }
    }

    /// <summary>
    /// 收集软件中已有的视频资产（顶层 Video 目录 + 小说 Videos 目录，递归、去重），
    /// 供「视频连贯（首尾帧）」从素材库选择已有视频。
    /// </summary>
    public static List<string> CollectProjectVideoPaths(string workRoot, string novelId)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scan(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (!IsVideoFile(f)) continue;
                    if (seen.Add(f)) list.Add(f);
                }
            }
            catch { }
        }
        Scan(FileService.VideoRoot(workRoot));
        Scan(FileService.NovelVideosPath(workRoot, novelId));
        return list;
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
    /// 添加参考图缩略图到 WrapPanel（立即渲染，UI 不卡顿）。
    /// base64 压缩放到后台线程异步完成，完成后按缩略图顺序追加到 refImages/refPaths。
    /// 每个缩略图为带 ✕ 的 44px 圆角图块，点击 ✕ 移除并同步删除对应数据。
    /// </summary>
    public static void AddReferenceThumb(
        System.Windows.Controls.WrapPanel panel, string filePath,
        System.Collections.Generic.List<string> refImages,
        Action onChanged, int maxCount = 6,
        System.Collections.Generic.List<string>? refPaths = null,
        Action<string>? onRefRemoved = null)
    {
        if (string.IsNullOrEmpty(filePath) || refImages.Count >= maxCount) return;
        AddReferenceThumbsAsync(panel, new[] { filePath }, refImages, onChanged, maxCount, refPaths, onRefRemoved);
    }

    /// <summary>
    /// 异步批量添加参考图缩略图：立即渲染全部缩略图并把路径记入 refPaths（不阻塞 UI），
    /// 后台压缩 base64，全部完成后按「原顺序」一次性写回 refImages，
    /// 保证参考图顺序与缩略图一致（1,2,3…）。
    /// 已被移除（如重建）或压缩失败的缩略图不会写回并自行移除。
    /// </summary>
    public static void AddReferenceThumbsAsync(
        System.Windows.Controls.WrapPanel panel, System.Collections.Generic.IReadOnlyList<string> filePaths,
        System.Collections.Generic.List<string> refImages,
        Action onChanged, int maxCount = 6,
        System.Collections.Generic.List<string>? refPaths = null,
        Action<string>? onRefRemoved = null)
    {
        // 缩略图路径立即生效，参考图数据（base64）异步补全；上限按已生效路径数计算
        int current = refPaths != null ? refPaths.Count : refImages.Count;
        var remaining = maxCount - current;
        var paths = new System.Collections.Generic.List<string>();
        foreach (var p in filePaths)
        {
            if (paths.Count >= remaining) break;
            if (!string.IsNullOrEmpty(p) && System.IO.File.Exists(p)) paths.Add(p);
        }
        if (paths.Count == 0) return;

        var thumbs = new System.Collections.Generic.List<(System.Windows.Controls.Border Border, string Path)>();
        foreach (var p in paths)
        {
            var b = BuildReferenceThumbUi(panel, p, refImages, onChanged, refPaths, onRefRemoved);
            if (b != null)
            {
                thumbs.Add((b, p));
                refPaths?.Add(p);
            }
        }
        if (thumbs.Count == 0) return;

        var results = new string?[thumbs.Count];
        int pending = thumbs.Count;
        var ui = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();
        for (int i = 0; i < thumbs.Count; i++)
        {
            int idx = i;
            var path = thumbs[idx].Path;
            System.Threading.Tasks.Task.Run(() => ImageToBase64DataUrl(path))
                .ContinueWith(t =>
                {
                    results[idx] = t.Result;
                    if (System.Threading.Interlocked.Decrement(ref pending) != 0) return;
                    // 全部压缩完成：按原顺序写回（已删除/失败的缩略图跳过并移除）
                    for (int k = 0; k < thumbs.Count; k++)
                    {
                        var (border, _) = thumbs[k];
                        if (!panel.Children.Contains(border)) continue;   // 缩略图已被删除（如重建）
                        var data = results[k];
                        if (string.IsNullOrEmpty(data)) { panel.Children.Remove(border); continue; }
                        refImages.Add(data);
                    }
                    onChanged?.Invoke();
                }, ui);
        }
    }

    /// <summary>构建参考图缩略图 UI（立即加入面板），删除时按缩略图相对顺序同步移除 refImages/refPaths。</summary>
    private static System.Windows.Controls.Border? BuildReferenceThumbUi(
        System.Windows.Controls.WrapPanel panel, string filePath,
        System.Collections.Generic.List<string> refImages,
        Action onChanged, System.Collections.Generic.List<string>? refPaths,
        Action<string>? onRefRemoved = null)
    {
        try
        {
            // 缩略图先以占位方式加入面板（保证顺序），图片在后台线程解码后再填充，
            // 避免 UI 线程同步解码大图导致「选中图片/打开窗口」卡顿。
            var img = new Image
            {
                Stretch = Stretch.UniformToFill,
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
                ClipToBounds = true,
                ToolTip = Path.GetFileName(filePath),
                ContextMenu = BuildAssetContextMenu(filePath, () => ShowImageViewer(filePath))
            };
            border.Child = new Grid
            {
                Children = { img, delBtn }
            };
            panel.Children.Add(border);

            delBtn.Click += (_, _) =>
            {
                var idx = IndexOfReferenceThumb(panel, border);
                if (idx >= 0)
                {
                    if (idx < refImages.Count) refImages.RemoveAt(idx);
                    if (refPaths != null && idx < refPaths.Count) refPaths.RemoveAt(idx);
                }
                panel.Children.Remove(border);
                onRefRemoved?.Invoke(filePath);
                onChanged?.Invoke();
            };

            // 后台解码缩略图，完成后回填到 UI（冻结后跨线程安全）；同时挂大图悬停预览
            var ui = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();
            System.Threading.Tasks.Task.Run(() => DecodeThumb(filePath))
                .ContinueWith(t =>
                {
                    try
                    {
                        var bmp = t.Result;
                        if (bmp == null || img.Source != null) return;
                        if (panel.Children.Contains(border)) img.Source = bmp;
                    }
                    catch { /* 解码失败则保留占位空白 */ }
                }, ui);
            AttachLargePreview(border, filePath, null);

            return border;
        }
        catch { return null; }
    }

    /// <summary>后台线程解码参考图缩略图（冻结后跨线程安全）</summary>
    private static BitmapImage? DecodeThumb(string filePath, int decodePixelWidth = 88)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(filePath);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = decodePixelWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>挂「大图预览」悬停浮层：悬停缩略图时显示原图放大预览。</summary>
    public static void AttachLargePreview(
        System.Windows.Controls.Border host, string? filePath, string? base64Data)
    {
        var ui = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();
        System.Threading.Tasks.Task.Run(() =>
        {
            if (!string.IsNullOrEmpty(base64Data) && base64Data.StartsWith("data:image"))
                return DecodeBase64Image(base64Data);
            if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                return DecodeThumb(filePath, 480);
            return null;
        }).ContinueWith(t =>
        {
            try
            {
                var src = t.Result;
                if (src == null) return;
                var pop = new System.Windows.Controls.Image
                {
                    Source = src, Stretch = Stretch.Uniform,
                    MaxWidth = 480, MaxHeight = 320
                };
                host.ToolTip = new System.Windows.Controls.Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x20)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x46)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6),
                    Child = pop
                };
                System.Windows.Controls.ToolTipService.SetShowDuration(host, 20000);
            }
            catch { /* 预览失败则保留文件名 ToolTip */ }
        }, ui);
    }

    /// <summary>折叠/展开按钮：与右侧项目资产面板的折叠按钮同款（非蓝色 SecondaryButtonStyle）。</summary>
    public static System.Windows.Controls.Button BuildAccentToggleButton(string tooltip = "展开 / 收起面板")
    {
        return new System.Windows.Controls.Button
        {
            Width = 22, Height = 22, Padding = new Thickness(0),
            FontSize = 9,
            Style = System.Windows.Application.Current.TryFindResource("SecondaryButtonStyle") as System.Windows.Style,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = tooltip
        };
    }

    /// <summary>返回目标缩略图在参考图序列中的相对下标（跳过非 refthumb 子元素），不存在返回 -1。</summary>
    private static int IndexOfReferenceThumb(
        System.Windows.Controls.Panel panel, System.Windows.UIElement target)
    {
        int idx = 0;
        foreach (var child in panel.Children)
        {
            if (child == target) return idx;
            if (child is System.Windows.Controls.Border b && b.Tag is string t && t == "refthumb") idx++;
        }
        return -1;
    }

    /// <summary>
    /// 更新参考图提示文字与清除按钮显隐。
    /// 有参考图时隐藏提示水印，避免把已选缩略图挤到右侧；仅 0 张时显示引导文字。
    /// </summary>
    public static void UpdateReferenceHint(
        System.Collections.Generic.IReadOnlyCollection<string> refImages,
        System.Windows.Controls.TextBlock hintText,
        System.Windows.Controls.Button clearBtn)
    {
        hintText.Text = "可添加 1 张（图生图）或多张（多图编辑）参考图";
        hintText.Visibility = refImages.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        clearBtn.Visibility = refImages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 为资产构建右键菜单：「查看图片」（可选）与「在文件夹中显示」。
    /// </summary>
    public static System.Windows.Controls.ContextMenu BuildAssetContextMenu(string path, Action? openView = null)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        if (openView != null)
        {
            var viewItem = new System.Windows.Controls.MenuItem { Header = "🖼️ 查看图片" };
            viewItem.Click += (_, _) => openView();
            menu.Items.Add(viewItem);
        }
        var locItem = new System.Windows.Controls.MenuItem { Header = "📂 在文件夹中显示" };
        locItem.Click += (_, _) => OpenInExplorer(path);
        menu.Items.Add(locItem);
        return menu;
    }

    /// <summary>
    /// 把一段「帧数据（base64 data URL）」作为参考图缩略图加入面板（视频续集尾帧使用）。
    /// label 用于提示词 @ 提及的名称；refPaths 中写入伪路径 "frame://label|源路径"，
    /// 以便 @ 候选名称提取与历史记录回填来源。删除/重建逻辑与普通参考图一致。
    /// </summary>
    public static void AddReferenceFrame(
        System.Windows.Controls.WrapPanel panel, string dataUrl, string label, string sourcePath,
        System.Collections.Generic.List<string> refImages,
        System.Collections.Generic.List<string>? refPaths,
        Action onChanged, int maxCount = 5)
    {
        if (string.IsNullOrEmpty(dataUrl)) return;
        int current = refPaths != null ? refPaths.Count : refImages.Count;
        if (current >= maxCount) { MainWindow.Notify($"⚠ 参考图最多 {maxCount} 张，请先移除部分已选", success: false); return; }

        var border = BuildFrameThumbUi(panel, dataUrl, label, sourcePath, refImages, refPaths, onChanged);
        if (border == null) return;
        refImages.Add(dataUrl);
        refPaths?.Add("frame://" + label + "|" + sourcePath);
        onChanged?.Invoke();
    }

    /// <summary>
    /// 按 label 移除「视频续集尾帧」参考图条目（含对应缩略图、base64 与伪路径）。
    /// </summary>
    public static void RemoveReferenceFrame(
        System.Windows.Controls.WrapPanel panel,
        System.Collections.Generic.List<string> refImages,
        System.Collections.Generic.List<string> refPaths,
        string label, Action onChanged)
    {
        var prefix = "frame://" + label + "|";
        int idx = -1;
        for (int i = 0; i < refPaths.Count; i++)
            if (refPaths[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        if (idx < 0) return;

        // 找到第 idx 个参考图缩略图并移除
        int thumbIdx = 0;
        System.Windows.UIElement? target = null;
        for (int i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is System.Windows.Controls.Border b && b.Tag is string t && t == "refthumb")
            {
                if (thumbIdx == idx) { target = panel.Children[i]; break; }
                thumbIdx++;
            }
        }
        if (target != null) panel.Children.Remove(target);
        if (idx < refImages.Count) refImages.RemoveAt(idx);
        refPaths.RemoveAt(idx);
        onChanged?.Invoke();
    }

    /// <summary>构建「帧数据」参考图缩略图 UI（样式与普通参考图一致，右键可定位源视频文件）。</summary>
    private static System.Windows.Controls.Border? BuildFrameThumbUi(
        System.Windows.Controls.WrapPanel panel, string dataUrl, string label, string sourcePath,
        System.Collections.Generic.List<string> refImages,
        System.Collections.Generic.List<string>? refPaths, Action onChanged)
    {
        try
        {
            var img = new Image
            {
                Stretch = Stretch.UniformToFill,
                Width = 44, Height = 44, Margin = new Thickness(0, 0, 6, 0),
                SnapsToDevicePixels = true,
                Clip = new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, 44, 44), 4, 4)
            };
            var delBtn = new System.Windows.Controls.Button
            {
                Content = "✕", FontSize = 9, Width = 16, Height = 16,
                Padding = new Thickness(0), Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Style = Application.Current.FindResource("SecondaryButtonStyle") as System.Windows.Style,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0)
            };
            var context = new System.Windows.Controls.ContextMenu();
            var locItem = new System.Windows.Controls.MenuItem { Header = "📂 在文件夹中显示源视频" };
            locItem.Click += (_, _) => OpenInExplorer(sourcePath);
            context.Items.Add(locItem);
            var border = new System.Windows.Controls.Border
            {
                Width = 44, Height = 44, Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new System.Windows.CornerRadius(4),
                Tag = "refthumb",
                ClipToBounds = true,
                ToolTip = label,
                ContextMenu = context
            };
            border.Child = new System.Windows.Controls.Grid { Children = { img, delBtn } };
            panel.Children.Add(border);

            delBtn.Click += (_, _) =>
            {
                var idx = IndexOfReferenceThumb(panel, border);
                if (idx >= 0)
                {
                    if (idx < refImages.Count) refImages.RemoveAt(idx);
                    if (refPaths != null && idx < refPaths.Count) refPaths.RemoveAt(idx);
                }
                panel.Children.Remove(border);
                onChanged?.Invoke();
            };

            // 后台解码 base64 帧并回填；同时挂大图悬停预览
            var ui = System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext();
            System.Threading.Tasks.Task.Run(() => DecodeBase64Image(dataUrl))
                .ContinueWith(t =>
                {
                    try
                    {
                        if (t.Result is System.Windows.Media.Imaging.BitmapImage bmp && panel.Children.Contains(border))
                            img.Source = bmp;
                    }
                    catch { }
                }, ui);
            AttachLargePreview(border, null, dataUrl);

            return border;
        }
        catch { return null; }
    }

    /// <summary>
    /// 本地图片文件转「压缩后」的 base64 data URL（供参考图使用）。
    /// 先按最长边缩放到 maxEdge（默认 1024），再编码：无透明通道→JPEG(q80)，有透明→PNG。
    /// 避免把超大原图（如 4K PNG 可达 18MB+，base64 后更大）直接上传，
    /// 导致多张参考图时请求体达到几十上百 MB，引发上传超时/请求超限而生成失败。
    /// </summary>
    public static string? ImageToBase64DataUrl(string filePath, int maxEdge = 1024)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return null;
            long stamp = fi.LastWriteTimeUtc.Ticks;
            // 命中缓存（文件未被修改）直接返回，避免每次重建参考图都重新读取并压缩大图
            if (RefImageCache.TryGetValue(filePath, out var cached) && cached.Stamp == stamp)
                return cached.Data;

            var src = new BitmapImage();
            src.BeginInit();
            src.UriSource = new Uri(filePath);
            src.CacheOption = BitmapCacheOption.OnLoad;
            src.EndInit();
            src.Freeze();

            double scale = Math.Min(1.0, Math.Min((double)maxEdge / src.PixelWidth, (double)maxEdge / src.PixelHeight));
            BitmapSource bmp = src;
            if (scale < 1.0)
            {
                var tb = new TransformedBitmap(src, new ScaleTransform(scale, scale));
                tb.Freeze();
                bmp = tb;
            }

            var result = BitmapSourceToBase64DataUrl(bmp);
            if (result == null) return null;
            RefImageCache[filePath] = (stamp, result);
            return result;
        }
        catch { return null; }
    }

    /// <summary>把任意 BitmapSource 压缩为 base64 Data URL（最长边 1024；含透明用 PNG，否则 JPEG q80）。失败返回 null。</summary>
    public static string? BitmapSourceToBase64DataUrl(BitmapSource bmp, int maxEdge = 1024)
    {
        try
        {
            BitmapSource source = bmp;
            double scale = Math.Min(1.0, Math.Min((double)maxEdge / bmp.PixelWidth, (double)maxEdge / bmp.PixelHeight));
            if (scale < 1.0)
            {
                var tb = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
                tb.Freeze();
                source = tb;
            }

            // 含透明通道用 PNG 保留透明，否则用 JPEG 大幅减小体积
            var fmt = source.Format;
            bool hasAlpha = fmt == PixelFormats.Pbgra32 || fmt == PixelFormats.Prgba64
                || fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Prgba128Float
                || fmt == PixelFormats.Rgba64;

            BitmapEncoder enc = hasAlpha
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = 80 };
            enc.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return $"data:{(hasAlpha ? "image/png" : "image/jpeg")};base64,{Convert.ToBase64String(ms.ToArray())}";
        }
        catch { return null; }
    }

    /// <summary>
    /// 提取视频在指定时间点的帧，压缩为 base64 Data URL（首尾帧提交用）。
    /// 必须在后台线程调用（WPF MediaPlayer + RenderTargetBitmap 阻塞渲染）。
    /// </summary>
    public static string? ExtractVideoFrameToBase64(string videoPath, double seconds, int maxEdge = 1024)
    {
        try
        {
            var bmp = ExtractVideoFrameCore(videoPath, seconds, 640, 360);
            if (bmp == null) return null;
            return BitmapSourceToBase64DataUrl(bmp, maxEdge);
        }
        catch { return null; }
    }

    /// <summary>读取视频时长（秒）。失败返回 null。必须在后台线程调用。</summary>
    public static double? GetVideoDurationSeconds(string videoPath)
    {
        System.Windows.Media.MediaPlayer? player = null;
        try
        {
            player = new System.Windows.Media.MediaPlayer { ScrubbingEnabled = true, Volume = 0 };
            player.Open(new Uri(videoPath));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!player.NaturalDuration.HasTimeSpan && sw.ElapsedMilliseconds < 5000)
                Thread.Sleep(100);
            if (!player.NaturalDuration.HasTimeSpan) return null;
            return player.NaturalDuration.TimeSpan.TotalSeconds;
        }
        catch { return null; }
        finally { player?.Close(); }
    }

    /// <summary>
    /// 复用单个 MediaPlayer 快速提取同一视频的多个帧。
    /// 打开一次播放器后即可连续取帧（无需每次新建 MediaPlayer 并等待解码器初始化），
    /// 大幅缩短「首帧显示」与「邻近帧胶片条」的等待时间。必须在后台线程使用，用完必须 Dispose。
    /// </summary>
    public sealed class VideoFrameExtractor : IDisposable
    {
        private readonly System.Windows.Media.MediaPlayer _player;
        private readonly object _lock = new();

        /// <summary>视频时长（秒），打开失败时为 null。</summary>
        public double? DurationSeconds { get; }

        private VideoFrameExtractor(System.Windows.Media.MediaPlayer player, double? duration)
        {
            _player = player;
            DurationSeconds = duration;
        }

        /// <summary>打开视频并读取时长（含等待解码器初始化，最多 5 秒）。失败返回 null。</summary>
        public static VideoFrameExtractor? Open(string path)
        {
            var player = new System.Windows.Media.MediaPlayer { ScrubbingEnabled = true, Volume = 0 };
            try
            {
                player.Open(new Uri(path));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!player.NaturalDuration.HasTimeSpan && sw.ElapsedMilliseconds < 5000)
                    Thread.Sleep(100);
                if (!player.NaturalDuration.HasTimeSpan) { player.Close(); return null; }
                return new VideoFrameExtractor(player, player.NaturalDuration.TimeSpan.TotalSeconds);
            }
            catch { try { player.Close(); } catch { } return null; }
        }

        /// <summary>提取指定时间点的帧（渲染为 w×h），失败返回 null。可连续调用，线程安全。</summary>
        public BitmapSource? ExtractFrame(double seconds, int w, int h)
        {
            lock (_lock)
            {
                try
                {
                    if (DurationSeconds is not { } dur || dur < 0.1) return null;
                    var pos = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, dur - 0.03)));
                    _player.Position = pos;
                    _player.Pause();
                    // 播放器已热：等待时间从 600ms 缩短到 160ms，首帧后续帧更快
                    Thread.Sleep(160);
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                        dc.DrawVideo(_player, new Rect(0, 0, w, h));
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    if (rtb.Width <= 1 || rtb.Height <= 1) return null;
                    rtb.Freeze();
                    return rtb;
                }
                catch { return null; }
            }
        }

        /// <summary>提取指定时间点的帧并压缩为 base64 Data URL（最长边 maxEdge）。失败返回 null。</summary>
        public string? ExtractFrameToBase64(double seconds, int maxEdge = 1024)
        {
            var bmp = ExtractFrame(seconds, 640, 360);
            return bmp == null ? null : BitmapSourceToBase64DataUrl(bmp, maxEdge);
        }

        public void Dispose()
        {
            try { _player.Close(); } catch { }
        }
    }

    /// <summary>用 MediaPlayer 定位并渲染视频在指定时间点的帧。</summary>
    private static BitmapSource? ExtractVideoFrameCore(string path, double seconds, int w, int h)
    {
        System.Windows.Media.MediaPlayer? player = null;
        try
        {
            player = new System.Windows.Media.MediaPlayer { ScrubbingEnabled = true, Volume = 0 };
            player.Open(new Uri(path));

            // 轮询等待解码器初始化（最多 5 秒）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!player.NaturalDuration.HasTimeSpan && sw.ElapsedMilliseconds < 5000)
                Thread.Sleep(100);
            if (!player.NaturalDuration.HasTimeSpan) return null;

            var dur = player.NaturalDuration.TimeSpan;
            if (dur.TotalSeconds < 0.1) return null;

            // 钳制到有效范围，末尾留 30ms 余量避免取到黑帧
            var pos = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, dur.TotalSeconds - 0.03)));
            player.Position = pos;
            player.Pause();
            Thread.Sleep(600);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawVideo(player, new Rect(0, 0, w, h));

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            if (rtb.Width <= 1 || rtb.Height <= 1) return null;
            rtb.Freeze();
            return rtb;
        }
        catch { return null; }
        finally { player?.Close(); }
    }

    /// <summary>把 base64 Data URL 解码为冻结的 BitmapImage（UI 线程调用）。失败返回 null。</summary>
    public static BitmapImage? DecodeBase64Image(string dataUrl)
    {
        try
        {
            var idx = dataUrl.IndexOf(',');
            var bytes = Convert.FromBase64String(idx >= 0 ? dataUrl[(idx + 1)..] : dataUrl);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>从参考图路径/帧伪路径提取「@ 提及名」：文件=去扩展名，帧=label 部分。</summary>
    public static string RefMentionName(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        if (path.StartsWith("frame://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = path["frame://".Length..].Trim();
            var pipe = rest.IndexOf('|');
            return (pipe >= 0 ? rest[..pipe] : rest).Trim();
        }
        var n = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(n) ? Path.GetFileName(path) : n;
    }

    /// <summary>
    /// 把提示词中的「@名」转换成模型可识别的参考图引用（与 images 数组位置对应）。
    /// markerStyle："ordinal" 用「第N张图」（图像 2.1 Flash 多图合成自然语言引用）；
    /// "picture" 用「&lt;Picture N&gt;」占位符（Agnes 官方统一的参考生成占位符，视频 2.5 系数位一致）。
    /// 例：@小明 挑起一条鱼 → 第2张图 挑起一条鱼 / &lt;Picture 2&gt; 挑起一条鱼。
    /// </summary>
    public static string ResolveRefMentions(string? prompt, IReadOnlyList<string>? refImages, string markerStyle = "ordinal")
    {
        if (string.IsNullOrWhiteSpace(prompt) || refImages == null || refImages.Count == 0)
            return prompt ?? string.Empty;

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < refImages.Count; i++)
        {
            var n = RefMentionName(refImages[i]);
            if (n.Length > 0 && !map.ContainsKey(n)) map[n] = i + 1;   // 1 基序号，同名前取第一个
        }
        if (map.Count == 0) return prompt;

        var names = map.Keys.OrderByDescending(k => k.Length).ToList();
        var sb = new System.Text.StringBuilder(prompt.Length + 16);
        int pos = 0;
        while (pos < prompt.Length)
        {
            char c = prompt[pos];
            if (c == '@')
            {
                string? matched = null; int idx = -1;
                foreach (var nm in names)
                {
                    int end = pos + 1 + nm.Length;
                    if (end > prompt.Length) continue;
                    if (string.Compare(prompt, pos + 1, nm, 0, nm.Length, StringComparison.OrdinalIgnoreCase) != 0)
                        continue;
                    // 其后不应紧跟字母/数字，避免把 @小明同学 误判成 @小明
                    if (end < prompt.Length && IsNameChar(prompt[end])) continue;
                    matched = nm; idx = map[nm]; break;
                }
                if (matched != null)
                {
                    if (string.Equals(markerStyle, "picture", StringComparison.OrdinalIgnoreCase))
                        sb.Append("<Picture ").Append(idx).Append('>');
                    else
                        sb.Append("第").Append(idx).Append("张图");
                    pos += 1 + matched.Length;
                    continue;
                }
            }
            sb.Append(c);
            pos++;
        }
        return sb.ToString();
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c);

    /// <summary>
    /// 根据用户自定义的优化 Skill 模板构建 (SystemPrompt, UserMessage)。
    /// 占位符：{hasRef} 参考图情况描述、{refCount} 参考图数量、{roleName} 角色名、{personality} 角色性格、{prompt} 原提示词（可选）。
    /// Skill 为空时回退到内置默认模板，避免空 System Prompt 导致模型行为异常。
    /// </summary>
    public static (string SystemPrompt, string UserMessage) BuildOptimizePrompt(
        string? skill, string rawPrompt, bool hasRef, int refCount,
        string roleName = "", string personality = "", string subject = "图像",
        string language = "zh", string markerStyle = "ordinal")
    {
        if (string.IsNullOrWhiteSpace(skill))
            skill = "你是一位专业的 AI 提示词优化师。请将用户提供的简短提示词扩展为一段详细、专业的提示词，只输出优化后的提示词，不要任何解释。";

        var hasRefText = hasRef
            ? $"用户提供了 {refCount} 张参考图，请仔细观察参考图的内容（主体外观、姿态、场景、构图、色彩风格），并结合用户文本，使生成结果与参考图风格统一；若文本与参考图冲突，以文本意图为主、参考图风格为辅"
            : "用户未提供参考图";

        var sys = skill
            .Replace("{hasRef}", hasRefText)
            .Replace("{refCount}", refCount.ToString())
            .Replace("{roleName}", roleName)
            .Replace("{personality}", personality)
            .Replace("{prompt}", rawPrompt);

        var userMsg = string.Empty;
        if (!string.IsNullOrEmpty(roleName) || !string.IsNullOrEmpty(personality))
            userMsg += $"角色名：{roleName}\n角色性格：{personality}\n\n";
        userMsg += (hasRef ? "请结合参考图" : "请") + $"优化以下{subject}生成提示词：\n{rawPrompt}\n\n";
        if (hasRef && (string.Equals(markerStyle, "picture", StringComparison.OrdinalIgnoreCase)
            ? System.Text.RegularExpressions.Regex.IsMatch(rawPrompt, "<Picture \\d+>")
            : System.Text.RegularExpressions.Regex.IsMatch(rawPrompt, "第\\d+张图")))
            userMsg += (string.Equals(markerStyle, "picture", StringComparison.OrdinalIgnoreCase)
                ? "提示词中的「<Picture N>」是参考图占位符，N 对应第 N 张参考图（images 数组顺序）；请在优化结果中保留这些「<Picture N>」占位符及其与参考图的对应关系，不要删除、改动或改变它们的指代序号。\n"
                : "提示词中的「第N张图」是参考图序数引用，N 对应第 N 张参考图（images 数组顺序）；请在优化结果中保留这些「第N张图」引用及其与参考图的对应关系，不要删除、改动或改变它们的指代序号。\n");
        userMsg += OptimizeLanguageInstruction(language);

        return (sys, userMsg);
    }

    /// <summary>根据优化输出语言返回追加到用户消息的输出语言指令。</summary>
    public static string OptimizeLanguageInstruction(string language)
    {
        bool en = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        return en
            ? "请用英文输出优化后的提示词，只输出英文结果，不要输出中文或任何解释。"
            : "请用中文输出优化后的提示词，不要输出英文或任何解释。";
    }

    /// <summary>
    /// 创建「优化输出语言」切换按钮（中文 / English），点击切换并持久化到配置，
    /// 切换时通过 notify 给出气泡提示（可为 null）。默认中文。
    /// </summary>
    public static System.Windows.Controls.Button BuildOptimizeLanguageToggle(System.Action<string>? notify = null)
    {
        var config = FileService.LoadConfig(App.WorkRoot);
        string lang = string.Equals(config.OptimizePromptLanguage, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";

        var btn = new System.Windows.Controls.Button
        {
            FontSize = 11,
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Style = Application.Current.TryFindResource("SecondaryButtonStyle") as Style,
            ToolTip = "优化输出语言：中文 / English（默认中文）"
        };
        void UpdateContent()
        {
            bool en = lang == "en";
            btn.Content = en ? "🌐 EN" : "🌐 中文";
            btn.ToolTip = en ? "当前输出英文，点击切换为中文" : "当前输出中文，点击切换为英文";
        }
        UpdateContent();
        btn.Click += (_, _) =>
        {
            lang = lang == "en" ? "zh" : "en";
            try
            {
                var cfg = FileService.LoadConfig(App.WorkRoot);
                cfg.OptimizePromptLanguage = lang;
                FileService.SaveConfig(App.WorkRoot, cfg);
            }
            catch { }
            UpdateContent();
            notify?.Invoke(lang == "en" ? "✓ 优化输出已切换为英文" : "✓ 优化输出已切换为中文");
        };
        return btn;
    }

    /// <summary>
    /// 创建「提示词设置」按钮（⚙️，置于「优化提示词」右侧），弹出菜单合并「中英文切换」与「实时自动匹配」。
    /// 设置会写入 AppConfig 并持久化；自动匹配状态同步到提示词编辑框。
    /// </summary>
    public static System.Windows.Controls.Button AttachQueueBadge(System.Windows.Controls.Button btn)
    {
        // 原按钮正文
        var label = new System.Windows.Controls.TextBlock
        {
            Text = (btn.Content is string s) ? s : "查看队列",
            FontSize = btn.FontSize,
            FontFamily = btn.FontFamily,
            Foreground = btn.Foreground,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        // 小红点：队列中待处理数量（排队 + 执行中）
        var dotText = new System.Windows.Controls.TextBlock
        {
            Text = "9",
            FontSize = 9,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var dot = new System.Windows.Controls.Border
        {
            Child = dotText,
            MinWidth = 15,
            Height = 15,
            CornerRadius = new CornerRadius(7.5),
            Background = new SolidColorBrush(Color.FromRgb(0xE8, 0x3A, 0x3A)),
            Padding = new Thickness(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 1, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Panel.SetZIndex(dot, 20);
        var grid = new Grid();
        grid.Children.Add(label);
        grid.Children.Add(dot);
        btn.Content = grid;

        void UpdateBadge()
        {
            int n = 0;
            foreach (var t in global::YangzaiWorkshop.Services.AiTaskManager.Tasks)
                if (t.Status is global::YangzaiWorkshop.Services.AiTaskStatus.Queued or global::YangzaiWorkshop.Services.AiTaskStatus.Running) n++;
            dot.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            dotText.Text = n > 99 ? "99+" : n.ToString();
        }
        UpdateBadge();
        global::YangzaiWorkshop.Services.AiTaskManager.Changed += UpdateBadge;
        btn.Unloaded += (_, _) => global::YangzaiWorkshop.Services.AiTaskManager.Changed -= UpdateBadge;
        return btn;
    }

    public static System.Windows.Controls.Button BuildGenSettingsButton(
        global::YangzaiWorkshop.Views.PromptMentionBox box,
        System.Action<string>? notify = null)
    {
        var config = FileService.LoadConfig(App.WorkRoot);
        bool en = string.Equals(config.OptimizePromptLanguage, "en", StringComparison.OrdinalIgnoreCase);
        box.AutoMatchLive = config.AutoMatchEnabled;

        var mZh = new System.Windows.Controls.MenuItem { Header = "优化输出：中文", IsCheckable = true, IsChecked = !en };
        var mEn = new System.Windows.Controls.MenuItem { Header = "优化输出：English", IsCheckable = true, IsChecked = en };
        var mAuto = new System.Windows.Controls.MenuItem
        {
            Header = "实时自动匹配（按参考图名）",
            IsCheckable = true,
            IsChecked = config.AutoMatchEnabled
        };

        void SyncLang()
        {
            config.OptimizePromptLanguage = mEn.IsChecked ? "en" : "zh";
            mZh.IsChecked = !mEn.IsChecked;
            mEn.IsChecked = config.OptimizePromptLanguage == "en";
            try { FileService.SaveConfig(App.WorkRoot, config); } catch { }
            notify?.Invoke(config.OptimizePromptLanguage == "en" ? "✓ 优化输出已切换为英文" : "✓ 优化输出已切换为中文");
        }
        mZh.Click += (_, _) => { mZh.IsChecked = true; mEn.IsChecked = false; SyncLang(); };
        mEn.Click += (_, _) => { mEn.IsChecked = true; mZh.IsChecked = false; SyncLang(); };
        mAuto.Click += (_, _) =>
        {
            config.AutoMatchEnabled = mAuto.IsChecked;
            box.AutoMatchLive = mAuto.IsChecked;
            try { FileService.SaveConfig(App.WorkRoot, config); } catch { }
            notify?.Invoke(mAuto.IsChecked ? "✓ 已开启实时自动匹配" : "○ 已暂停实时自动匹配（可点右下角「一键匹配」）");
        };

        // 统一深色风格，符合应用整体配色
        var itemStyle = new Style(typeof(System.Windows.Controls.MenuItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEE))));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 6, 12, 6)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        var hoverT = new System.Windows.Trigger { Property = System.Windows.Controls.MenuItem.IsHighlightedProperty, Value = true };
        hoverT.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x46))));
        itemStyle.Triggers.Add(hoverT);

        var menu = new System.Windows.Controls.ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x36)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEE)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x58)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };
        mZh.Style = itemStyle; mEn.Style = itemStyle; mAuto.Style = itemStyle;
        var mTitle = new System.Windows.Controls.MenuItem { Header = "⚙ 提示词设置", IsEnabled = false, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xAA)) };
        mTitle.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(mTitle);
        menu.Items.Add(new System.Windows.Controls.Separator { Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x58)), Margin = new Thickness(6, 2, 6, 2) });
        menu.Items.Add(mZh);
        menu.Items.Add(mEn);
        menu.Items.Add(new System.Windows.Controls.Separator { Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x58)), Margin = new Thickness(6, 2, 6, 2) });
        menu.Items.Add(mAuto);

        var btn = new System.Windows.Controls.Button
        {
            Content = "⚙️",
            FontSize = 12,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Style = Application.Current.TryFindResource("SecondaryButtonStyle") as Style,
            ToolTip = "提示词设置：中英文切换、实时自动匹配"
        };
        btn.Click += (_, _) =>
        {
            menu.PlacementTarget = btn;
            menu.IsOpen = true;
        };
        return btn;
    }

    /// <summary>
    /// 在文件资源管理器中定位资产：文件 → 选中该文件；目录 → 直接打开目录。
    /// </summary>
    public static void OpenInExplorer(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (System.IO.Directory.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path.TrimEnd('\\', '/')}\"");
                return;
            }
            if (System.IO.File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch { /* 打开失败静默，不打断用户操作 */ }
    }

    /// <summary>
    /// 把启用（勾选）的默认提示词追加到提示词末尾并返回，用于「每次生成时自动带上默认提示词」。
    /// key 为 "Image" 或 "Video"，分别读取配置中的 DefaultImagePrompts / DefaultVideoPrompts。
    /// 没有启用条目时原样返回。
    /// </summary>
    public static string AppendEnabledDefaultPrompts(string prompt, string key)
    {
        try
        {
            var config = FileService.LoadConfig(App.WorkRoot);
            var list = key == "Video" ? config.DefaultVideoPrompts : config.DefaultImagePrompts;
            var enabled = new System.Collections.Generic.List<string>();
            foreach (var item in list)
            {
                if (item.Enabled && !string.IsNullOrWhiteSpace(item.Text))
                    enabled.Add(item.Text.Trim());
            }
            if (enabled.Count == 0) return prompt;

            var sb = new System.Text.StringBuilder(prompt);
            if (!string.IsNullOrEmpty(prompt)) sb.Append('\n');
            sb.Append(string.Join("\n", enabled));
            return sb.ToString();
        }
        catch
        {
            return prompt; // 读取失败时不影响正常生成
        }
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
    /// 以 Win32 层把 child 窗口归属到 owner（等价于系统层的 owned 关系）。
    /// 相比 WPF 的 Owner 属性：子窗口可被鼠标选中、可被 Alt+Tab 切换，
    /// 且与主窗口在任务栏共用同一个图标（不新增独立任务栏按钮）；
    /// 同时避免 WPF 关闭 owned 窗口时误激活/最小化 AllowsTransparency 主窗口的已知问题。
    /// 注意：调用前不要设置 WPF 的 Owner 属性，二者会冲突。
    /// </summary>
    public static void SetWin32Owner(Window child, Window? owner)
    {
        if (child == null || owner == null) return;
        try
        {
            var childHwnd = new WindowInteropHelper(child).Handle;
            var ownerHwnd = new WindowInteropHelper(owner).Handle;
            if (childHwnd == IntPtr.Zero || ownerHwnd == IntPtr.Zero) return;
            const int GWL_HWNDPARENT = -8;
            SetWindowLongPtr(childHwnd, GWL_HWNDPARENT, ownerHwnd);
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

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

    // ===== 提示词水印 =====

    /// <summary>
    /// 为提示词输入框添加水印提示：文本框内容为空时显示灰色水印文字，输入内容后自动隐藏。
    /// 返回包裹文本框的宿主容器（挂载点处用宿主替换原文本框即可）。
    /// </summary>
    public static Grid PromptBoxWithWatermark(TextBox box, string watermark = "在此输入提示词…")
    {
        var wm = new TextBlock
        {
            Text = watermark,
            FontSize = box.FontSize,
            FontFamily = box.FontFamily,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Margin = new Thickness(box.Padding.Left + 4, box.Padding.Top + 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false,
            Opacity = 0.8
        };
        var host = new Grid();
        host.Children.Add(box);
        host.Children.Add(wm);
        box.TextChanged += (_, _) =>
            wm.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
        return host;
    }
}
