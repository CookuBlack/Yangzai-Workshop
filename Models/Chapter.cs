namespace YangzaiWorkshop.Models;

public class Chapter
{
    public int Index { get; set; }
    public string Title { get; set; } = string.Empty;
    public string OriginalContent { get; set; } = string.Empty;
    public string ScriptContent { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }

    public string DisplayName => $"{(IsCompleted ? "\u2713 " : "")}第{Index}章：{Title}";

    public string FolderName => $"第{Index}章";
}
