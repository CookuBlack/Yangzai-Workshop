using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace YangzaiWorkshop.Services;

/// <summary>生成类型</summary>
public enum AiGenType { Image, Video }

/// <summary>
/// 一条 AI 生成历史记录：保存提示词、参考媒体文件路径与生成参数，
/// 供再次打开生成窗口时一键回填（点击历史即把图像/参数/提示词填充到窗口）。
/// 参考媒体以文件路径保存（用户拖入的图片会先归入项目资产，路径稳定）。
/// </summary>
public class AiGenHistoryEntry
{
    public AiGenType Type { get; set; } = AiGenType.Image;
    public string Prompt { get; set; } = "";
    public string Ratio { get; set; } = "16:9";      // 比例
    public string Level { get; set; } = "";          // 档位（图片：像素档/K；视频：720P/960P/2K）
    public int Seconds { get; set; } = 0;            // 视频时长（秒）；图片为 0
    public List<string> RefImagePaths { get; set; } = new();   // 参考图片文件路径
    public string RefVideoPath { get; set; } = "";             // 参考视频文件路径（视频生成）
    public string EngineBadge { get; set; } = "";              // 生成引擎徽章（本地/云端/视频模型）
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>历史列表内展示的摘要标题</summary>
    public string Title => Type == AiGenType.Video
        ? $"{Level}·{Ratio}·{Seconds}s"
        : $"{(string.IsNullOrEmpty(Ratio) ? "" : Ratio + " ")}{Level}";
}

/// <summary>AI 生成历史记录的持久化服务（保存在 WorkData\Config\ai_gen_history.json）</summary>
public static class AiGenHistory
{
    private const int MaxEntries = 40;
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public static string HistoryFile(string workRoot) =>
        Path.Combine(workRoot, "Config", "ai_gen_history.json");

    /// <summary>读取历史（最新的在前）。读取失败或不存在返回空列表。</summary>
    public static List<AiGenHistoryEntry> Load(string workRoot)
    {
        try
        {
            var file = HistoryFile(workRoot);
            if (!File.Exists(file)) return new List<AiGenHistoryEntry>();
            var list = JsonSerializer.Deserialize<List<AiGenHistoryEntry>>(File.ReadAllText(file), _json);
            return list?.OrderByDescending(e => e.CreatedAt).ToList()
                ?? new List<AiGenHistoryEntry>();
        }
        catch { return new List<AiGenHistoryEntry>(); }
    }

    /// <summary>在列表头部插入一条记录（去重最近重复提示词），并裁剪、保存到磁盘。</summary>
    public static void Add(string workRoot, AiGenHistoryEntry entry)
    {
        try
        {
            var list = Load(workRoot);
            entry.CreatedAt = DateTime.Now;
            // 与最新一条提示词相同则视为重复，直接刷新该条时间
            if (list.Count > 0 && list[0].Prompt == entry.Prompt)
                list[0] = entry;
            else
                list.Insert(0, entry);
            if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            FileService.EnsureDirectory(Path.Combine(workRoot, "Config"));
            File.WriteAllText(HistoryFile(workRoot), JsonSerializer.Serialize(list, _json));
        }
        catch { /* 历史记录失败不影响生成主流程 */ }
    }
}