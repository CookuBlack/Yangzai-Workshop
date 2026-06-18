namespace YangzaiWorkshop.Models;

public class TrashMeta
{
    public string OriginalPath { get; set; } = string.Empty;
    public DateTime DeletedAt { get; set; }
}

public class TrashItem
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public DateTime DeletedAt { get; set; }
}
