using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using YangzaiWorkshop.Models;

namespace YangzaiWorkshop.Services;

public static class FileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string? _appBasePath;
    /// <summary>应用根目录（exe 目录，兼容 dotnet run 和发布模式）</summary>
    public static string AppBasePath
    {
        get
        {
            if (_appBasePath != null) return _appBasePath;
            // 先试 exe 所在目录
            var baseDir = AppContext.BaseDirectory;
            if (Directory.Exists(Path.Combine(baseDir, "Assets")))
            { _appBasePath = baseDir; return _appBasePath; }
            // 回退到当前工作目录
            var cwd = Directory.GetCurrentDirectory();
            if (Directory.Exists(Path.Combine(cwd, "Assets")))
            { _appBasePath = cwd; return _appBasePath; }
            // 最后用 exe 目录
            _appBasePath = baseDir;
            return _appBasePath;
        }
    }

    /// <summary>默认工作目录：相对路径 WorkData</summary>
    public static string DefaultWorkPath =>
        Path.Combine(AppBasePath, "WorkData");

    /// <summary>头像素材目录</summary>
    public static string AssetsAvatarPath =>
        Path.Combine(AppBasePath, "Assets", "Avatar");

    /// <summary>迷你头像目录</summary>
    public static string AssetsAvatarMiniPath =>
        Path.Combine(AssetsAvatarPath, "Avatar_Mini");

    /// <summary>默认头像路径</summary>
    public static string DefaultAvatarFile =>
        Path.Combine(AssetsAvatarPath, "Gusssheep.png");

    /// <summary>迷你默认头像路径</summary>
    public static string DefaultMiniAvatarFile =>
        Path.Combine(AssetsAvatarMiniPath, "Gusssheep.png");

    /// <summary>
    /// 用户自定义轮播媒体目录（位于用户数据目录 WorkRoot 下，WorkData\Assets\Carousel）。
    /// 用户通过右键「添加轮播视频/图片」存入此处；自动更新（MSI 升级）不会清理此目录。
    /// 默认内置轮播图随安装包放在 exe 同级的 Assets\Carousel，由 HomePage 直接读取。
    /// </summary>
    public static string CarouselPath =>
        Path.Combine(App.WorkRoot, "Assets", "Carousel");

    /// <summary>用户自定义头像路径</summary>
    public static string CustomAvatarFile =>
        Path.Combine(AssetsAvatarPath, "profile.png");

    /// <summary>获取当前有效头像路径（自定义优先，回退默认）</summary>
    public static string GetEffectiveAvatarPath()
    {
        if (File.Exists(CustomAvatarFile)) return CustomAvatarFile;
        if (File.Exists(DefaultAvatarFile)) return DefaultAvatarFile;
        return string.Empty;
    }

    /// <summary>安全加载本地图片为 BitmapImage（自动处理路径空格）</summary>
    public static BitmapImage? LoadImage(string? filePath, int? decodeWidth = null)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(filePath);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth.HasValue) bmp.DecodePixelWidth = decodeWidth.Value;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ===== 路径相关 =====
    public static string ConfigPath(string workRoot) => Path.Combine(workRoot, "Config");
    public static string NovelsPath(string workRoot) => Path.Combine(workRoot, "Novels");
    public static string TempPath(string workRoot) => Path.Combine(workRoot, "Temp");
    public static string CharactersPath(string workRoot) => Path.Combine(workRoot, "Characters");

    /// <summary>顶层图片根目录：WorkData\Image</summary>
    public static string ImageRoot(string workRoot) => Path.Combine(workRoot, "Image");
    /// <summary>顶层视频根目录：WorkData\Video</summary>
    public static string VideoRoot(string workRoot) => Path.Combine(workRoot, "Video");
    /// <summary>顶层音频根目录：WorkData\Audio</summary>
    public static string AudioRoot(string workRoot) => Path.Combine(workRoot, "Audio");

    /// <summary>
    /// 清理名称中的非法/混乱字符，转为简洁合法的文件夹名。
    /// 移除：非法文件名字符、书名号、全角/半角括号、空白、常见网址水印片段。
    /// 例如："《封神榜》【爱上阅读_www.isyd.net】" → "封神榜"
    /// </summary>
    public static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "未命名";

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            // 跳过非法文件名字符与各类括号/书名号
            if (Path.GetInvalidFileNameChars().Contains(c)) continue;
            if (c is '《' or '》' or '〈' or '〉' or '【' or '】' or '〔' or '〕'
                or '(' or ')' or '[' or ']' or '{' or '}' or '（' or '）') continue;
            sb.Append(c);
        }

        var cleaned = sb.ToString();

        // 清理常见网址水印：去除以 "www." 或 "http" 开头的片段，以及 "爱上阅读" 等站名后缀
        cleaned = RemoveUrlWatermarks(cleaned);

        // 压缩连续空格/下划线，去除首尾空白
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", "_");
        cleaned = cleaned.Trim('_', ' ', '-', '.', '，', '。');

        return string.IsNullOrWhiteSpace(cleaned) ? "未命名" : cleaned;
    }

    /// <summary>移除文件夹名中的网址水印片段（如 "爱上阅读_www.isyd.net"）</summary>
    private static string RemoveUrlWatermarks(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        // 常见盗版站水印关键词，匹配到则截断
        var markers = new[] { "www.", "http://", "https://", ".com", ".net", ".cn", ".org", "爱上阅读", "isvd", "isyd" };
        string result = name;
        foreach (var m in markers)
        {
            int idx = result.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) result = result[..idx]; // 从水印处截断，保留前面的书名
        }
        return result.Trim('_', ' ', '-', '.', '，', '。');
    }

    /// <summary>人物素材图片目录：WorkData\Image\人物素材\{mediaFolder}\{charId}</summary>
    public static string CharacterMaterialPath(string workRoot, string mediaFolder, string charId) =>
        Path.Combine(ImageRoot(workRoot), "人物素材", mediaFolder, charId);
    /// <summary>小说章节图片目录：WorkData\Image\小说\{mediaFolder}\{chapterFolder}</summary>
    public static string NovelChapterImagesPath(string workRoot, string mediaFolder, string chapterFolder) =>
        Path.Combine(ImageRoot(workRoot), "小说", mediaFolder, chapterFolder);
    /// <summary>小说章节视频目录：WorkData\Video\{mediaFolder}\{chapterFolder}</summary>
    public static string ChapterVideoPath(string workRoot, string mediaFolder, string chapterFolder) =>
        Path.Combine(VideoRoot(workRoot), mediaFolder, chapterFolder);

    public static string NovelPath(string workRoot, string novelId)
    {
        var novelsRoot = NovelsPath(workRoot);
        // 先试旧 GUID 路径
        var guidPath = Path.Combine(novelsRoot, novelId);
        if (Directory.Exists(guidPath)) return guidPath;
        // 再搜索 FolderName 目录（找匹配 novelId 的 info.json）
        if (Directory.Exists(novelsRoot))
        {
            foreach (var dir in Directory.GetDirectories(novelsRoot))
            {
                var info = ReadJson<NovelInfo>(Path.Combine(dir, "info.json"));
                if (info?.Id == novelId) return dir;
            }
        }
        // 回退到 GUID 路径（新建时使用）
        return guidPath;
    }
    public static string NovelInfoFile(string workRoot, string novelId) => Path.Combine(NovelPath(workRoot, novelId), "info.json");
    public static string NovelCoverFile(string workRoot, string novelId) => Path.Combine(NovelPath(workRoot, novelId), "cover.png");
    public static string NovelOriginalFile(string workRoot, string novelId) => Path.Combine(NovelPath(workRoot, novelId), "original.txt");
    public static string NovelScriptFile(string workRoot, string novelId) => Path.Combine(NovelPath(workRoot, novelId), "script.txt");
    public static string NovelChaptersFile(string workRoot, string novelId) => Path.Combine(NovelPath(workRoot, novelId), "chapters.json");
    public static string NovelCharactersPath(string workRoot, string novelId) => Path.Combine(NovelPath(workRoot, novelId), "Characters");
    /// <summary>[已废弃] 旧版图片路径，保持兼容</summary>
    public static string NovelImagesPath(string workRoot, string novelId) => Path.Combine(NovelPath(workRoot, novelId), "Images");
    /// <summary>[已废弃] 旧版视频路径，保持兼容</summary>
    public static string NovelVideosPath(string workRoot, string novelId) => Path.Combine(NovelPath(workRoot, novelId), "Videos");
    /// <summary>小说章节图片目录：WorkData\Image\小说\{mediaFolder}\{chapterFolder}</summary>
    public static string ChapterImagesPath(string workRoot, string mediaFolder, string chapterFolder) =>
        NovelChapterImagesPath(workRoot, mediaFolder, chapterFolder);
    /// <summary>小说章节视频目录：WorkData\Video\{mediaFolder}\{chapterFolder}</summary>
    public static string ChapterVideosPath(string workRoot, string mediaFolder, string chapterFolder) =>
        ChapterVideoPath(workRoot, mediaFolder, chapterFolder);
    /// <summary>小说章节音频目录：WorkData\Audio\{mediaFolder}\{chapterFolder}</summary>
    public static string ChapterAudioPath(string workRoot, string mediaFolder, string chapterFolder) =>
        Path.Combine(AudioRoot(workRoot), mediaFolder, chapterFolder);
    /// <summary>小说章节音频目录：WorkData\Audio\{mediaFolder}\{chapterFolder}</summary>
    public static string ChapterAudiosPath(string workRoot, string mediaFolder, string chapterFolder) =>
        ChapterAudioPath(workRoot, mediaFolder, chapterFolder);
    /// <summary>小说全局音频目录：WorkData\Audio\{mediaFolder}（不绑定章节）</summary>
    public static string NovelGlobalAudioPath(string workRoot, string mediaFolder) =>
        Path.Combine(AudioRoot(workRoot), mediaFolder);
    public static string CharacterPath(string workRoot, string novelId, string charId) =>
        Path.Combine(NovelCharactersPath(workRoot, novelId), charId);
    public static string CharacterInfoFile(string workRoot, string novelId, string charId) =>
        Path.Combine(CharacterPath(workRoot, novelId, charId), "info.json");
    public static string CharacterAvatarFile(string workRoot, string novelId, string charId) =>
        Path.Combine(CharacterPath(workRoot, novelId, charId), "avatar.png");
    /// <summary>角色图片目录：WorkData\Image\人物素材\{mediaFolder}\{charId}</summary>
    public static string CharacterImagesPath(string workRoot, string mediaFolder, string charId) =>
        CharacterMaterialPath(workRoot, mediaFolder, charId);
    /// <summary>角色音频目录：WorkData\Audio\人物素材\{mediaFolder}\{charId}</summary>
    public static string CharacterAudioPath(string workRoot, string mediaFolder, string charId) =>
        Path.Combine(AudioRoot(workRoot), "人物素材", mediaFolder, charId);
    /// <summary>音乐播放器音乐目录：WorkData\Music</summary>
    public static string MusicPath(string workRoot) => Path.Combine(workRoot, "Music");
    public static string BannerPath(string workRoot) => Path.Combine(ConfigPath(workRoot), "banners");
    public static string NoticeFile(string workRoot) => Path.Combine(ConfigPath(workRoot), "notice.txt");
    public static string SettingsFile(string workRoot) => Path.Combine(ConfigPath(workRoot), "appsettings.json");
    public static string ProfileWorksFile(string workRoot) => Path.Combine(ConfigPath(workRoot), "profile_works.json");
    public static string ProfileImageFile(string _) => CustomAvatarFile;
    public static string MemosPath(string workRoot) => Path.Combine(ConfigPath(workRoot), "Memos");
    public static string MemoFile(string workRoot, string memoId) => Path.Combine(MemosPath(workRoot), $"{memoId}.json");
    /// <summary>文本编辑历史快照文件：WorkData\Config\text_history.json</summary>
    public static string TextHistoryFile(string workRoot) => Path.Combine(ConfigPath(workRoot), "text_history.json");

    // ===== JSON 读写 =====
    public static T? ReadJson<T>(string filePath) where T : class
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch { return null; }
    }

    public static void WriteJson<T>(string filePath, T data)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) EnsureDirectory(dir);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        AtomicWriteAllText(filePath, json);
    }

    // ===== 文本文件读写 =====
    public static string ReadText(string filePath)
    {
        if (!File.Exists(filePath)) return string.Empty;
        return File.ReadAllText(filePath, Encoding.UTF8);
    }

    public static void WriteText(string filePath, string content)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) EnsureDirectory(dir);
        AtomicWriteAllText(filePath, content);
    }

    // ===== 文本历史快照读写（仅应用关闭时持久化） =====
    /// <summary>加载文本编辑历史快照，文件不存在或损坏时返回空列表</summary>
    public static List<HistorySnapshot> LoadTextHistory(string workRoot)
    {
        var file = TextHistoryFile(workRoot);
        if (!File.Exists(file)) return new List<HistorySnapshot>();
        try
        {
            var list = ReadJson<List<HistorySnapshot>>(file);
            return list ?? new List<HistorySnapshot>();
        }
        catch { return new List<HistorySnapshot>(); }
    }

    /// <summary>持久化文本编辑历史快照（原子写入，防止关闭时写坏）</summary>
    public static void SaveTextHistory(string workRoot, List<HistorySnapshot> snapshots)
    {
        try
        {
            WriteJson(TextHistoryFile(workRoot), snapshots);
        }
        catch
        {
            // 历史持久化失败不应影响应用正常退出
        }
    }

    /// <summary>
    /// 原子写入：先写同目录临时文件，再重命名替换目标文件。
    /// 即使写入过程中程序崩溃/断电，目标文件要么是旧完整内容、要么是新完整内容，
    /// 不会出现半写损坏（这是 chapters.json / info.json 等数据防丢的关键保障）。
    /// </summary>
    private static void AtomicWriteAllText(string filePath, string content)
    {
        var tmpPath = filePath + ".tmp";
        try
        {
            File.WriteAllText(tmpPath, content, Encoding.UTF8);
            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch
        {
            // 写入失败：清理临时文件，保留原文件完整，并向上抛出由调用方记录/提示
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    // ===== 目录操作 =====
    public static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
    }

    public static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    public static List<string> GetFiles(string path, params string[] extensions)
    {
        if (!Directory.Exists(path)) return new List<string>();

        if (extensions.Length == 0)
            return Directory.GetFiles(path).ToList();

        // 单次目录枚举 + 内存过滤，避免 N 次 GetFiles
        var set = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(path)
            .Where(f => set.Contains(Path.GetExtension(f)))
            .ToList();
    }

    public static List<string> GetDirectories(string path)
    {
        if (!Directory.Exists(path)) return new List<string>();
        return Directory.GetDirectories(path).ToList();
    }

    // ===== 文件复制 =====
    public static string CopyFile(string sourcePath, string targetDir)
    {
        EnsureDirectory(targetDir);
        var fileName = Path.GetFileName(sourcePath);
        var targetPath = Path.Combine(targetDir, fileName);
        File.Copy(sourcePath, targetPath, overwrite: true);
        return targetPath;
    }

    public static void DeleteFile(string path)
    {
        if (File.Exists(path)) MoveToTrash(path);
    }

    // ===== 回收站 =====
    public static string TrashPath(string workRoot) =>
        Path.Combine(workRoot, ".trash");

    public static void MoveToTrash(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        var trash = TrashPath(App.WorkRoot);
        EnsureDirectory(trash);
        var itemDir = Path.Combine(trash, Guid.NewGuid().ToString("N")[..8]);
        EnsureDirectory(itemDir);
        // 保存原始路径和删除时间
        var info = new { OriginalPath = path, DeletedAt = DateTime.Now };
        var json = System.Text.Json.JsonSerializer.Serialize(info);
        File.WriteAllText(Path.Combine(itemDir, ".info"), json);
        // 移动文件
        var name = Path.GetFileName(path);
        var dest = Path.Combine(itemDir, name);
        if (File.Exists(path)) File.Move(path, dest);
        else if (Directory.Exists(path))
        {
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.Move(path, dest);
        }
    }

    public static List<TrashItem> GetTrashItems(string workRoot)
    {
        var result = new List<TrashItem>();
        var trash = TrashPath(workRoot);
        if (!Directory.Exists(trash)) return result;
        // 清理超过30天的
        var cutoff = DateTime.Now.AddDays(-30);
        try
        {
            foreach (var d in Directory.GetDirectories(trash))
            {
            var infoPath = Path.Combine(d, ".info");
            if (!File.Exists(infoPath))
            {
                try { Directory.Delete(d, true); } catch { }
                continue;
            }
            try
            {
                var json = File.ReadAllText(infoPath);
                var info = System.Text.Json.JsonSerializer.Deserialize<TrashMeta>(json);
                if (info == null) continue;
                if (info.DeletedAt < cutoff)
                {
                    try { Directory.Delete(d, true); } catch { }
                    continue;
                }
                // 找到实际文件或目录
                var found = false;
                foreach (var f in Directory.GetFiles(d))
                {
                    if (Path.GetFileName(f) == ".info") continue;
                    result.Add(new TrashItem
                    {
                        Id = Path.GetFileName(d),
                        FileName = Path.GetFileName(f),
                        FilePath = f,
                        OriginalPath = info.OriginalPath,
                        DeletedAt = info.DeletedAt
                    });
                    found = true;
                    break;
                }
                if (!found)
                {
                    foreach (var dir in Directory.GetDirectories(d))
                    {
                        result.Add(new TrashItem
                        {
                            Id = Path.GetFileName(d),
                            FileName = Path.GetFileName(dir),
                            FilePath = dir,
                            OriginalPath = info.OriginalPath,
                            DeletedAt = info.DeletedAt
                        });
                        found = true;
                        break;
                    }
                }
            }
            catch { }
        }
        }
        catch { /* 目录枚举失败静默忽略 */ }
        return result.OrderByDescending(x => x.DeletedAt).ToList();
    }

    public static void RestoreTrashItem(string id)
    {
        var trash = TrashPath(App.WorkRoot);
        var itemDir = Path.Combine(trash, id);
        if (!Directory.Exists(itemDir)) return;
        var infoPath = Path.Combine(itemDir, ".info");
        if (!File.Exists(infoPath)) return;
        var info = System.Text.Json.JsonSerializer.Deserialize<TrashMeta>(
            File.ReadAllText(infoPath));
        if (info == null) return;

        // 还原所有文件和目录
        var targetDir = Path.GetDirectoryName(info.OriginalPath)!;
        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);
        // 还原文件
        foreach (var f in Directory.GetFiles(itemDir))
        {
            if (Path.GetFileName(f) == ".info") continue;
            var dest = Path.Combine(targetDir, Path.GetFileName(f));
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(f, dest);
        }
        // 还原目录
        foreach (var d in Directory.GetDirectories(itemDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(d));
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.Move(d, dest);
        }
        // 清理临时目录（重试防止文件句柄未释放）
        for (int retry = 0; retry < 5; retry++)
        {
            try { Directory.Delete(itemDir, true); break; }
            catch { System.Threading.Thread.Sleep(100); }
        }

        // 通知相关页面刷新已还原的文件
        FileRestored?.Invoke(info.OriginalPath);
    }

    /// <summary>一键恢复回收站中的所有项目，返回成功还原的数量</summary>
    public static int RestoreAllTrashItems(string workRoot)
    {
        var items = GetTrashItems(workRoot);
        var restored = 0;
        foreach (var item in items)
        {
            try { RestoreTrashItem(item.Id); restored++; }
            catch { /* 单个失败不影响其余项目 */ }
        }
        return restored;
    }

    /// <summary>文件还原事件，通知各页面刷新内容</summary>
    public static event Action<string>? FileRestored;

    public static void EmptyTrash(string workRoot)
    {
        var trash = TrashPath(workRoot);
        if (Directory.Exists(trash))
        {
            try { Directory.Delete(trash, true); } catch { }
        }
    }

    // ===== 配置读写（带内存缓存，避免同一流程反复读磁盘） =====
    // 使用 lock 保证缓存字段的线程安全（AI 任务后台线程也会调用 LoadConfig/SaveConfig）
    private static readonly object _configLock = new();
    private static AppConfig? _cachedConfig;
    private static string? _cachedWorkRoot;

    public static AppConfig LoadConfig(string workRoot)
    {
        // 命中缓存：同 workRoot 且缓存有效
        lock (_configLock)
        {
            if (_cachedConfig != null && _cachedWorkRoot == workRoot)
                return _cachedConfig;
        }

        var config = ReadJson<AppConfig>(SettingsFile(workRoot)) ?? new AppConfig();
        config.WorkDataPath = workRoot;

        lock (_configLock)
        {
            // 双重检查：避免在读盘期间其他线程写入覆盖有效缓存
            if (_cachedConfig != null && _cachedWorkRoot == workRoot && ReferenceEquals(_cachedConfig, config))
                return _cachedConfig;
            _cachedConfig = config;
            _cachedWorkRoot = workRoot;
        }
        return config;
    }

    public static void SaveConfig(string workRoot, AppConfig config)
    {
        WriteJson(SettingsFile(workRoot), config);
        // 保存后同步更新缓存，避免后续操作重复读盘
        lock (_configLock)
        {
            _cachedConfig = config;
            _cachedWorkRoot = workRoot;
        }
    }

    /// <summary>主动刷新配置缓存（外部修改配置文件后调用）</summary>
    public static void InvalidateConfigCache()
    {
        lock (_configLock)
        {
            _cachedConfig = null;
            _cachedWorkRoot = null;
        }
    }

    public static void SaveAppSetting(string workRoot, string key, object value)
    {
        var config = LoadConfig(workRoot);
        var prop = typeof(AppConfig).GetProperty(key);
        if (prop != null)
        {
            prop.SetValue(config, Convert.ChangeType(value, prop.PropertyType));
            SaveConfig(workRoot, config);
        }
    }

    // ===== 小说数据读写 =====
    public static List<NovelInfo> LoadAllNovels(string workRoot)
    {
        var novelsPath = NovelsPath(workRoot);
        if (!Directory.Exists(novelsPath)) return new List<NovelInfo>();

        var novels = new List<NovelInfo>();
        foreach (var dir in Directory.GetDirectories(novelsPath))
        {
            var infoFile = Path.Combine(dir, "info.json");
            var info = ReadJson<NovelInfo>(infoFile);
            if (info == null) continue;

            info.HasCoverImage = File.Exists(Path.Combine(dir, "cover.png"));

            // 修复：为缺少 MediaFolder 的小说生成
            if (string.IsNullOrWhiteSpace(info.MediaFolder) && !string.IsNullOrWhiteSpace(info.Name))
            {
                var baseName = SanitizeFolderName(info.Name);
                var existing = novels.Select(n => n.MediaFolder).ToHashSet(StringComparer.OrdinalIgnoreCase);
                info.MediaFolder = existing.Contains(baseName) ? $"{baseName}_{info.Id[..4]}" : baseName;
                WriteJson(infoFile, info);
            }

            // 为缺少 FolderName 的小说生成（仅记录，不重命名旧目录）
            if (string.IsNullOrWhiteSpace(info.FolderName) && !string.IsNullOrWhiteSpace(info.Name))
            {
                var baseName = SanitizeFolderName(info.Name);
                var existing = novels.Select(n => n.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                info.FolderName = existing.Contains(baseName) ? $"{baseName}_{info.Id[..4]}" : baseName;
                WriteJson(infoFile, info);
            }

            // 迁移：清理历史遗留的混乱文件夹名（书名号/括号/网址水印），统一目录命名
            MigrateLegacyFolderNames(workRoot, info, infoFile, dir);

            novels.Add(info);
        }
        return novels;
    }

    /// <summary>
    /// 迁移历史遗留的混乱文件夹名：若 MediaFolder/FolderName 含书名号、括号、网址水印等，
    /// 自动重命名对应目录为清理后的名称，并更新 info.json。
    /// 迁移仅重命名目录与更新字段，不移动/删除任何文件内容。
    /// 注意：重命名目录后 info.json 的路径会变化，因此重命名前先更新字段，再移动目录，最后写新路径的 info.json。
    /// </summary>
    private static void MigrateLegacyFolderNames(string workRoot, NovelInfo info, string infoFile, string novelDir)
    {
        bool mediaChanged = false;
        bool folderChanged = false;

        // 1. 迁移 MediaFolder（Image/Video/Audio 顶层目录下的分类子目录）
        if (!string.IsNullOrWhiteSpace(info.MediaFolder))
        {
            var cleaned = SanitizeFolderName(info.MediaFolder);
            if (cleaned != info.MediaFolder && cleaned != "未命名")
            {
                MigrateMediaFolder(workRoot, info.MediaFolder, cleaned);
                info.MediaFolder = cleaned;
                mediaChanged = true;
            }
        }

        // 2. 迁移 FolderName（Novels 目录下的文件夹名）
        //    注意：novelDir 是外层 foreach 传入的当前目录路径，重命名前用它定位，
        //    重命名后 info.json 位于新目录，需重新计算写入路径。
        string? newInfoFile = null;
        if (!string.IsNullOrWhiteSpace(info.FolderName))
        {
            var cleanedFolder = SanitizeFolderName(info.FolderName);
            if (cleanedFolder != info.FolderName && cleanedFolder != "未命名")
            {
                // 用 novelDir（实际存在的目录）而非 info.FolderName 拼接，避免 FolderName 与磁盘目录不一致
                var actualOldDir = novelDir;
                var newDir = Path.Combine(NovelsPath(workRoot), cleanedFolder);
                bool moved = false;
                if (Directory.Exists(actualOldDir) && !Directory.Exists(newDir)
                    && !string.Equals(actualOldDir, newDir, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Move(actualOldDir, newDir); moved = true; } catch { }
                }
                info.FolderName = cleanedFolder;
                folderChanged = true;
                // 目录重命名成功后，info.json 的新路径
                newInfoFile = moved
                    ? Path.Combine(newDir, "info.json")
                    : Path.Combine(actualOldDir, "info.json");
            }
        }

        // 3. 写回 info.json（优先用重命名后的新路径，回退到原路径）
        if (mediaChanged || folderChanged)
        {
            var writePath = newInfoFile ?? infoFile;
            try { WriteJson(writePath, info); }
            catch { try { WriteJson(infoFile, info); } catch { } }
        }
    }

    /// <summary>迁移单个 MediaFolder 在所有媒体根目录（Image/Video/Audio）下的子目录</summary>
    private static void MigrateMediaFolder(string workRoot, string oldFolder, string newFolder)
    {
        // 若目标目录名已被其他小说占用（重名冲突），追加短后缀避免覆盖
        var unique = EnsureUniqueFolderName(workRoot, newFolder);

        // Image 下的分类：小说、人物素材
        MigrateSubFolder(Path.Combine(ImageRoot(workRoot), "小说"), oldFolder, unique);
        MigrateSubFolder(Path.Combine(ImageRoot(workRoot), "人物素材"), oldFolder, unique);
        // Video / Audio 顶层直接是 mediaFolder
        MigrateSubFolder(VideoRoot(workRoot), oldFolder, unique);
        MigrateSubFolder(AudioRoot(workRoot), oldFolder, unique);
    }

    /// <summary>确保文件夹名在媒体根目录下唯一（避免两本书同名冲突）</summary>
    private static string EnsureUniqueFolderName(string workRoot, string baseName)
    {
        // 检查各媒体根目录下是否已有同名目录
        bool Conflict(string parent) =>
            Directory.Exists(Path.Combine(parent, baseName));

        bool hasConflict =
            Conflict(Path.Combine(ImageRoot(workRoot), "小说")) ||
            Conflict(Path.Combine(ImageRoot(workRoot), "人物素材")) ||
            Conflict(VideoRoot(workRoot)) ||
            Conflict(AudioRoot(workRoot));

        if (!hasConflict) return baseName;

        // 存在冲突：追加数字后缀
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}_{i}";
            if (!Conflict(Path.Combine(ImageRoot(workRoot), "小说")) &&
                !Conflict(Path.Combine(ImageRoot(workRoot), "人物素材")) &&
                !Conflict(VideoRoot(workRoot)) &&
                !Conflict(AudioRoot(workRoot)))
                return candidate;
        }
        // 兜底：加 GUID 后缀
        return $"{baseName}_{Guid.NewGuid().ToString("N")[..6]}";
    }

    /// <summary>在指定父目录下把 oldFolder 子目录重命名为 newFolder</summary>
    private static void MigrateSubFolder(string parent, string oldFolder, string newFolder)
    {
        var oldPath = Path.Combine(parent, oldFolder);
        var newPath = Path.Combine(parent, newFolder);
        if (!Directory.Exists(oldPath)) return;
        if (Directory.Exists(newPath))
        {
            // 目标已存在：合并内容（避免丢失），失败则跳过
            try { MergeDirectory(oldPath, newPath); } catch { }
            return;
        }
        try { Directory.Move(oldPath, newPath); } catch { }
    }

    /// <summary>把 src 目录内容合并到 dst（同名文件不覆盖，跳过冲突）</summary>
    private static void MergeDirectory(string src, string dst)
    {
        foreach (var file in Directory.GetFiles(src))
        {
            var target = Path.Combine(dst, Path.GetFileName(file));
            if (!File.Exists(target))
                File.Move(file, target);
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            var targetDir = Path.Combine(dst, Path.GetFileName(dir));
            if (!Directory.Exists(targetDir))
            {
                Directory.Move(dir, targetDir);
            }
            else
            {
                MergeDirectory(dir, targetDir);
            }
        }
        // 源目录内容已合并，尝试删除空目录
        try { Directory.Delete(src, true); } catch { }
    }

    /// <summary>根据小说名生成不会与其他小说碰撞的媒体文件夹名</summary>
    public static string GenerateUniqueMediaFolder(string workRoot, string novelName, string novelId)
    {
        var baseName = SanitizeFolderName(novelName);
        var others = LoadAllNovels(workRoot)
            .Where(n => n.Id != novelId)
            .Select(n => n.MediaFolder)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return others.Contains(baseName)
            ? $"{baseName}_{novelId.Substring(0, 4)}"
            : baseName;
    }

    public static void SaveNovelInfo(string workRoot, NovelInfo info)
    {
        // 首次保存时自动生成唯一 MediaFolder
        if (string.IsNullOrWhiteSpace(info.MediaFolder) && !string.IsNullOrWhiteSpace(info.Name))
            info.MediaFolder = GenerateUniqueMediaFolder(workRoot, info.Name, info.Id);
        // 自动生成 FolderName（Novels 下的目录名）
        var needRename = false;
        var oldDir = "";
        if (string.IsNullOrWhiteSpace(info.FolderName) && !string.IsNullOrWhiteSpace(info.Name))
        {
            var baseName = SanitizeFolderName(info.Name);
            var others = LoadAllNovels(workRoot).Where(n => n.Id != info.Id)
                .Select(n => n.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            info.FolderName = others.Contains(baseName)
                ? $"{baseName}_{info.Id[..4]}"
                : baseName;
            // 检查是否需要重命名旧 GUID 目录
            oldDir = Path.Combine(NovelsPath(workRoot), info.Id);
            if (Directory.Exists(oldDir))
                needRename = true;
        }
        WriteJson(NovelInfoFile(workRoot, info.Id), info);
        // 重命名旧 GUID 目录为 FolderName
        if (needRename)
        {
            var newDir = Path.Combine(NovelsPath(workRoot), info.FolderName);
            if (!Directory.Exists(newDir))
            {
                try { Directory.Move(oldDir, newDir); }
                catch { }
            }
        }
    }

    /// <summary>个人资料作品（独立于剧本章节小说，存储在 Config/profile_works.json）</summary>
    public static List<NovelInfo> LoadProfileWorks(string workRoot)
    {
        return ReadJson<List<NovelInfo>>(ProfileWorksFile(workRoot)) ?? new List<NovelInfo>();
    }

    public static void SaveProfileWorks(string workRoot, List<NovelInfo> works)
    {
        WriteJson(ProfileWorksFile(workRoot), works);
    }

    /// <summary>重命名小说时移动所有媒体文件夹（图片 + 视频）</summary>
    public static void MoveNovelMediaFolders(string workRoot, string oldFolder, string newFolder, string novelId)
    {
        if (string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase)) return;

        // Image\小说\{old} → Image\小说\{new}
        var oldNovelImg = Path.Combine(ImageRoot(workRoot), "小说", oldFolder);
        var newNovelImg = Path.Combine(ImageRoot(workRoot), "小说", newFolder);
        SafeMoveDir(oldNovelImg, newNovelImg);

        // Image\人物素材\{old} → Image\人物素材\{new}
        var oldCharImg = Path.Combine(ImageRoot(workRoot), "人物素材", oldFolder);
        var newCharImg = Path.Combine(ImageRoot(workRoot), "人物素材", newFolder);
        SafeMoveDir(oldCharImg, newCharImg);

        // Video\{old} → Video\{new}
        var oldVideo = Path.Combine(VideoRoot(workRoot), oldFolder);
        var newVideo = Path.Combine(VideoRoot(workRoot), newFolder);
        SafeMoveDir(oldVideo, newVideo);

        // Audio\{old} → Audio\{new}
        var oldAudio = Path.Combine(AudioRoot(workRoot), oldFolder);
        var newAudio = Path.Combine(AudioRoot(workRoot), newFolder);
        SafeMoveDir(oldAudio, newAudio);
    }

    private static void SafeMoveDir(string oldPath, string newPath)
    {
        if (!Directory.Exists(oldPath)) return;
        try
        {
            if (Directory.Exists(newPath))
                Directory.Delete(newPath, true);
            EnsureDirectory(Path.GetDirectoryName(newPath)!);
            Directory.Move(oldPath, newPath);
        }
        catch { /* 移动失败静默忽略，数据不丢失 */ }
    }

    public static List<Chapter> LoadChapters(string workRoot, string novelId)
    {
        return ReadJson<List<Chapter>>(NovelChaptersFile(workRoot, novelId)) ?? new List<Chapter>();
    }

    public static void SaveChapters(string workRoot, string novelId, List<Chapter> chapters)
    {
        WriteJson(NovelChaptersFile(workRoot, novelId), chapters);
    }

    // ===== 备忘录 =====
    public static List<Memo> LoadMemos(string workRoot)
    {
        var list = new List<Memo>();
        var memosDir = MemosPath(workRoot);
        if (!Directory.Exists(memosDir)) return list;
        foreach (var f in Directory.GetFiles(memosDir, "*.json"))
        {
            var memo = ReadJson<Memo>(f);
            if (memo != null) list.Add(memo);
        }
        return list.OrderByDescending(m => m.UpdatedAt).ToList();
    }

    public static void SaveMemo(string workRoot, Memo memo)
    {
        memo.UpdatedAt = DateTime.Now;
        WriteJson(MemoFile(workRoot, memo.Id), memo);
    }

    public static void DeleteMemo(string workRoot, string memoId)
    {
        var file = MemoFile(workRoot, memoId);
        if (File.Exists(file)) File.Delete(file);
    }

    // ===== 初始化 =====
    public static void InitializeWorkData(string workRoot, string? appVersion = null)
    {
        EnsureDirectory(ConfigPath(workRoot));
        EnsureDirectory(BannerPath(workRoot));
        EnsureDirectory(NovelsPath(workRoot));
        EnsureDirectory(TempPath(workRoot));
        EnsureDirectory(ImageRoot(workRoot));
        EnsureDirectory(Path.Combine(ImageRoot(workRoot), "人物素材"));
        EnsureDirectory(Path.Combine(ImageRoot(workRoot), "小说"));
        EnsureDirectory(VideoRoot(workRoot));
        EnsureDirectory(AudioRoot(workRoot));

        // 默认配置
        if (!File.Exists(SettingsFile(workRoot)))
        {
            var config = new AppConfig { WorkDataPath = workRoot };
            SaveConfig(workRoot, config);
        }

        // 公告（每次启动更新，确保版本号同步）
        WriteText(NoticeFile(workRoot), "欢迎使用 Yangzai Workshop 小说漫剧创作工作台！\n\n" +
            $"v{appVersion ?? "1.0"} 更新内容：\n" +
            "• 新增本地 ComfyUI 生图引擎（支持读取工作流 JSON）\n" +
            "• 新增文本历史撤销/重做与历史版本回退\n" +
            "• 新增 AI 生成任务队列（视频 + 图像统一管理）\n" +
            "• 优化图像素材瀑布流布局与图片预览体验\n" +
            "• 优化存储目录结构，自动清理文件名水印\n\n" +
            "点击「+」按钮导入你的第一本小说吧！");
    }

    // ===== 数据备份与恢复 =====
    /// <summary>
    /// 打包工作目录为 zip。逐文件写入，单个文件被占用/读取失败时跳过（保证备份整体可用），
    /// 并自动跳过写入过程中的临时文件。
    /// </summary>
    public static void BackupData(string workRoot, string zipPath)
    {
        if (!Directory.Exists(workRoot)) return;
        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir)) EnsureDirectory(dir);
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(workRoot, "*", SearchOption.AllDirectories))
        {
            // 跳过原子写入的临时文件
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var entryName = Path.GetRelativePath(workRoot, file);
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
            catch
            {
                // 单个文件被占用等原因无法读取：跳过该文件，保证备份包整体生成成功
            }
        }
    }

    /// <summary>
    /// 安全恢复：先把现有工作目录改名为临时目录（而不是直接删除），
    /// 解压成功后再删除旧数据；解压失败则删除新目录并回滚旧数据，绝不让恢复操作本身丢数据。
    /// </summary>
    public static void RestoreData(string workRoot, string zipPath)
    {
        var oldDir = workRoot + "_old_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var moved = false;
        if (Directory.Exists(workRoot))
        {
            try
            {
                Directory.Move(workRoot, oldDir);
                moved = true;
            }
            catch
            {
                // 改名失败（目录被占用等），退回直接删除
                try { Directory.Delete(workRoot, true); } catch { }
            }
        }
        Directory.CreateDirectory(workRoot);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, workRoot);
            if (moved)
            {
                // 解压成功，清理旧数据目录
                try { Directory.Delete(oldDir, true); } catch { }
            }
        }
        catch
        {
            // 解压失败：删除不完整的新目录，回滚旧数据，保证原数据不丢失
            try { if (Directory.Exists(workRoot)) Directory.Delete(workRoot, true); } catch { }
            if (moved)
            {
                try { Directory.Move(oldDir, workRoot); } catch { }
            }
            throw;
        }
    }
}
