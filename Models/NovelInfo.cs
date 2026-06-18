namespace YangzaiWorkshop.Models;

public class NovelInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CoverColor { get; set; } = "#4A90E2";
    public bool HasCoverImage { get; set; } = false;

    /// <summary>图片/视频存储用的文件夹名（从 Name 自动生成）</summary>
    public string MediaFolder { get; set; } = string.Empty;

    // 统计数据
    public long TotalComments { get; set; }
    public long TotalFavorites { get; set; }
    public long TotalPlays { get; set; }
    public long TotalForwards { get; set; }
    public long TotalLikes { get; set; }
    public decimal TotalIncome { get; set; }
    public double TotalProductionHours { get; set; }
}
