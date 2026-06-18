namespace YangzaiWorkshop.Models;

public class PlatformStats
{
    public string Platform { get; set; } = string.Empty; // 抖音/快手/Bilibili
    public List<DailyStats> DailyData { get; set; } = new();
}

public class DailyStats
{
    public DateTime Date { get; set; }
    public long Plays { get; set; }
    public long Likes { get; set; }
    public long Comments { get; set; }
}
