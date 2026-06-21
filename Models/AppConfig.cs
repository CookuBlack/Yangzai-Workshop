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
    public string LastUpdateDate { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public string Version { get; set; } = "2.2.0";
    public int GitHubStars { get; set; } = 128;
}
