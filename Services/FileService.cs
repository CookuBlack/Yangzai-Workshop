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

    /// <summary>轮播视频目录</summary>
    public static string CarouselPath =>
        Path.Combine(AppBasePath, "Assets", "Carousel");

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

    /// <summary>清理名称中的非法文件名字符，转为合法的文件夹名</summary>
    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "未命名" : sanitized;
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
    public static string CharacterPath(string workRoot, string novelId, string charId) =>
        Path.Combine(NovelCharactersPath(workRoot, novelId), charId);
    public static string CharacterInfoFile(string workRoot, string novelId, string charId) =>
        Path.Combine(CharacterPath(workRoot, novelId, charId), "info.json");
    public static string CharacterAvatarFile(string workRoot, string novelId, string charId) =>
        Path.Combine(CharacterPath(workRoot, novelId, charId), "avatar.png");
    /// <summary>角色图片目录：WorkData\Image\人物素材\{mediaFolder}\{charId}</summary>
    public static string CharacterImagesPath(string workRoot, string mediaFolder, string charId) =>
        CharacterMaterialPath(workRoot, mediaFolder, charId);
    public static string BannerPath(string workRoot) => Path.Combine(ConfigPath(workRoot), "banners");
    public static string NoticeFile(string workRoot) => Path.Combine(ConfigPath(workRoot), "notice.txt");
    public static string SettingsFile(string workRoot) => Path.Combine(ConfigPath(workRoot), "appsettings.json");
    public static string ProfileWorksFile(string workRoot) => Path.Combine(ConfigPath(workRoot), "profile_works.json");
    public static string ProfileImageFile(string _) => CustomAvatarFile;
    public static string MemosPath(string workRoot) => Path.Combine(ConfigPath(workRoot), "Memos");
    public static string MemoFile(string workRoot, string memoId) => Path.Combine(MemosPath(workRoot), $"{memoId}.json");

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
        File.WriteAllText(filePath, json, Encoding.UTF8);
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
        File.WriteAllText(filePath, content, Encoding.UTF8);
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

        return extensions
            .SelectMany(ext => Directory.GetFiles(path, $"*{ext}"))
            .Distinct()
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

    // ===== 配置读写 =====
    public static AppConfig LoadConfig(string workRoot)
    {
        var config = ReadJson<AppConfig>(SettingsFile(workRoot)) ?? new AppConfig();
        config.WorkDataPath = workRoot;
        return config;
    }

    public static void SaveConfig(string workRoot, AppConfig config)
    {
        WriteJson(SettingsFile(workRoot), config);
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

            novels.Add(info);
        }
        return novels;
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

        // 默认配置
        if (!File.Exists(SettingsFile(workRoot)))
        {
            var config = new AppConfig { WorkDataPath = workRoot };
            SaveConfig(workRoot, config);
        }

        // 默认公告
        if (!File.Exists(NoticeFile(workRoot)))
        {
            WriteText(NoticeFile(workRoot), "欢迎使用 Yangzai Workshop 小说漫剧创作工作台！\n\n" +
                $"v{appVersion ?? "1.0"} 版本功能：\n" +
                "• 支持小说导入与智能分章\n" +
                "• 剧本改编与素材管理\n" +
                "• 平台数据统计与可视化\n\n" +
                "点击「+」按钮导入你的第一本小说吧！");
        }
    }

    // ===== 数据备份与恢复 =====
    public static void BackupData(string workRoot, string zipPath)
    {
        ZipFile.CreateFromDirectory(workRoot, zipPath, CompressionLevel.Optimal, false);
    }

    public static void RestoreData(string workRoot, string zipPath)
    {
        if (Directory.Exists(workRoot))
            Directory.Delete(workRoot, true);
        Directory.CreateDirectory(workRoot);
        ZipFile.ExtractToDirectory(zipPath, workRoot);
    }
}
