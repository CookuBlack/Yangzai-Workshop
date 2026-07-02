namespace YangzaiWorkshop.Models;

public class AppConfig
{
    public string Theme { get; set; } = "Light";
    public bool FollowSystemTheme { get; set; } = true;
    public string UserName { get; set; } = "创作者";
    public string UserSignature { get; set; } = "用漫剧讲述精彩故事";
    public string WorkDataPath { get; set; } = "WorkData";
    public string AvatarPath { get; set; } = string.Empty;
    public string ImageDirectoryPath { get; set; } = string.Empty;
    public string VideoDirectoryPath { get; set; } = string.Empty;
    public bool AutoSaveScript { get; set; } = true;
    public int FontSize { get; set; } = 14;
    public bool AutoPlayBanner { get; set; } = true;
    public int BannerIntervalSeconds { get; set; } = 5;
    public bool AutoBackup { get; set; } = false;
    public int BackupIntervalHours { get; set; } = 24;
    /// <summary>大模型 API 地址（兼容 OpenAI 格式）</summary>
    public string ApiEndpoint { get; set; } = "https://api.openai.com/v1";
    /// <summary>API 密钥</summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>文本模型名称（聊天/剧本/提示词）</summary>
    public string ApiModel { get; set; } = "gpt-4o-mini";
    /// <summary>图片生成模型名称</summary>
    public string ImageModel { get; set; } = "agnes-image-2.0-flash";
    /// <summary>视频生成模型名称</summary>
    public string VideoModel { get; set; } = "agnes-video-v2.0";
    /// <summary>生成剧本的 System Prompt（基于当前章节原文生成剧本）</summary>
    public string ScriptSkill { get; set; } = "你是一位专业的漫剧编剧。请将以下小说章节内容改编为漫剧剧本。\n要求：\n1. 采用分镜脚本格式，每个场景标注【场景X：地点 - 时间】\n2. 对话前标注角色名，例如「角色名：台词」\n3. 动作描述用括号括起，例如（推门走进房间）\n4. 保留原著的精彩对白和情节，适当精简描述性文字\n5. 输出完整的剧本，不要省略";
    /// <summary>生成提示词的 System Prompt（基于剧本内容生成场景提示词）</summary>
    public string PromptSkill { get; set; } = "你是一位专业的漫剧分镜提示词工程师。请根据以下剧本内容，为每个场景生成对应的创作提示词。\n要求：\n1. 为每个场景单独生成提示词，标注对应场景编号\n2. 每个提示词应包含：画面构图、角色位置与动作、表情神态、光影氛围、色彩倾向\n3. 提示词应具体详细，适合直接用于AI绘图\n4. 格式：【场景X提示词】\n画面构图：...\n角色动作：...\n光影氛围：...\n色彩倾向：...";
    /// <summary>常用 AI 网站书签</summary>
    public List<AiBookmark> AiBookmarks { get; set; } = new()
    {
        new() { Name = "ChatGPT", Url = "https://chat.openai.com" },
        new() { Name = "Claude", Url = "https://claude.ai" },
        new() { Name = "Gemini", Url = "https://gemini.google.com" },
        new() { Name = "Midjourney", Url = "https://www.midjourney.com" },
        new() { Name = "Stable Diffusion", Url = "https://stability.ai" },
        new() { Name = "Hugging Face", Url = "https://huggingface.co" },
    };
    public string LastUpdateDate { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public string Version { get; set; } = "3.3.0";
    public int GitHubStars { get; set; } = 128;

    // ====== 自定义主题：单一背景色或背景图 ======
    /// <summary>自定义背景色（HEX，例如 #EDEDED）</summary>
    public string CustomBgColor { get; set; } = "#EDEDED";
    public string CustomBgImagePath { get; set; } = string.Empty;
    public double CustomBgOpacity { get; set; } = 0.35;
    public double CustomBgBlur { get; set; } = 15;
    /// <summary>背景图模式下前景风格：Light=奶白 / Dark=暗色</summary>
    public string ImageForeground { get; set; } = "Light";

    // ====== 音乐播放器 ======
    /// <summary>音乐音量（0.0 ~ 1.0）</summary>
    public double MusicVolume { get; set; } = 0.7;
    /// <summary>启动时自动播放音乐</summary>
    public bool MusicAutoPlay { get; set; } = false;
    /// <summary>播放模式：RepeatAll / Shuffle</summary>
    public string MusicPlayMode { get; set; } = "RepeatAll";
}
