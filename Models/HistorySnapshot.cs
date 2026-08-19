using System;
using System.Text.Json.Serialization;

namespace YangzaiWorkshop.Models;

/// <summary>
/// 文本历史快照：记录某一时刻的文本内容，用于历史浏览与回退。
/// </summary>
public class HistorySnapshot
{
    /// <summary>编辑目标标识（如 "小说ID|章节ID|字段名"）</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>该时刻的文本内容</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    /// <summary>变动发生时间</summary>
    [JsonPropertyName("time")]
    public DateTime Time { get; set; }

    /// <summary>展示名（由 key 解析，供历史窗口显示，运行时填充）</summary>
    [JsonIgnore]
    public string DisplayName { get; set; } = "";
}
