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
    public string LastUpdateDate { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public string Version { get; set; } = "2.1.1";
    public int GitHubStars { get; set; } = 128;
    /// <summary>GitHub Personal Access Token（可选，用于提高 API 速率限制至 5000 次/小时）</summary>
    public string GitHubToken { get; set; } = string.Empty;
}
