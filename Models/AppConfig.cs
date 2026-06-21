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
    /// <summary>模型名称</summary>
    public string ApiModel { get; set; } = "gpt-4o-mini";
    /// <summary>生成剧本的 System Prompt（{content} = 小说内容）</summary>
    public string ScriptSkill { get; set; } = "你是一位专业的漫剧编剧。请将小说内容改编为漫剧剧本。\n要求：\n1. 采用分镜脚本格式，每个场景标注【场景X：地点 - 时间】\n2. 对话前标注角色名，例如「角色名：台词」\n3. 动作描述用括号括起，例如（推门走进房间）\n4. 保留原著的精彩对白和情节，适当精简描述性文字\n5. 输出完整的剧本，不要省略";
    /// <summary>生成提示词的 System Prompt（{content} = 小说内容）</summary>
    public string PromptSkill { get; set; } = "你是一位专业的漫剧剧本提示词工程师。请根据小说内容，生成一段创作提示词，用于指导AI生成漫剧剧本。提示词应包含：风格设定、角色描述、场景氛围、改编要点等。";
    public string LastUpdateDate { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public string Version { get; set; } = "2.2.0";
    public int GitHubStars { get; set; } = 128;
}
