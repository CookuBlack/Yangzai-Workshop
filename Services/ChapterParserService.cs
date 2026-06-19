using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using YangzaiWorkshop.Models;

namespace YangzaiWorkshop.Services;

public static class ChapterParserService
{
    // ===== 编码检测 =====
    /// <summary>自动检测文件编码：UTF-8 BOM → UTF-8 → GBK</summary>
    public static Encoding DetectEncoding(string filePath)
    {
        if (!File.Exists(filePath)) return Encoding.UTF8;

        var bytes = File.ReadAllBytes(filePath);
        if (bytes.Length == 0) return Encoding.UTF8;

        // BOM 检测
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
            return new UTF8Encoding(true);   // UTF-8 BOM
        if (bytes is [0xFF, 0xFE, ..])
            return Encoding.Unicode;          // UTF-16 LE
        if (bytes is [0xFE, 0xFF, ..])
            return Encoding.BigEndianUnicode;

        // UTF-8 有效性验证
        try
        {
            var test = Encoding.UTF8.GetString(bytes);
            var reEncoded = Encoding.UTF8.GetBytes(test);
            if (bytes.SequenceEqual(reEncoded))
                return Encoding.UTF8;
        }
        catch { }

        // 回退 GBK（中文 TXT 最常见的非 UTF 编码）
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GBK");
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    // ===== 章节匹配正则（四种模式并行，取并集） =====

    /// <summary>模式0（优先）：第X部/卷/篇 第Y章 — 组合章节格式，如"第一部 第1章 标题"</summary>
    private static readonly Regex PartChapterRegex = new(
        @"^\s*第\s*([一二三四五六七八九十百千零两\d]+)\s*[部卷篇册][\s:：]*第\s*([一二三四五六七八九十百千零两\d]+)\s*[章节回]\s*[:：\s]*(.*?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>模式1：第X章/回/卷/集/部/篇 — 覆盖95%+的中文小说</summary>
    private static readonly Regex StandardRegex = new(
        @"^\s*第\s*([一二三四五六七八九十百千零两\d]+)\s*[章节回卷集部篇册]\s*[:：\s]*(.*?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>模式2：序章/楔子/尾声/番外 等特殊章节名</summary>
    private static readonly Regex SpecialRegex = new(
        @"^\s*(序章|楔子|序幕|引子|尾声|终章|结局|后记|完结感言|番外[篇]?|外传|特别篇|幕间)\s*[:：\s]*(.*?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>模式3：Chapter X 英文章节（网译小说常见）</summary>
    private static readonly Regex EnglishRegex = new(
        @"^\s*[Cc]hapter\s+(\d+)\s*[:：-]?\s*(.*?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // ===== 中文数字映射 =====
    private static readonly Dictionary<char, int> ChineseNumberMap = new()
    {
        {'零', 0}, {'一', 1}, {'二', 2}, {'两', 2}, {'三', 3}, {'四', 4},
        {'五', 5}, {'六', 6}, {'七', 7}, {'八', 8}, {'九', 9},
        {'十', 10}, {'百', 100}, {'千', 1000}
    };

    // ===== 内部数据结构 =====
    private readonly struct ChapterMarker
    {
        public int Position { get; init; }
        public int Number { get; init; }    // 序号（特殊章节为临时编号）
        public string Title { get; init; }
        public bool IsSpecial { get; init; }
    }

    // ===== 核心解析方法 =====
    /// <summary>解析TXT小说文件，按章节自动拆分</summary>
    public static List<Chapter> ParseNovel(string filePath)
    {
        if (!File.Exists(filePath)) return new List<Chapter>();

        // 1. 检测编码并读取全文
        var encoding = DetectEncoding(filePath);
        string content;
        try
        {
            content = File.ReadAllText(filePath, encoding);
        }
        catch
        {
            // 编码解析失败，回退 UTF-8
            content = File.ReadAllText(filePath, Encoding.UTF8);
        }

        if (string.IsNullOrWhiteSpace(content))
            return new List<Chapter>();

        // 2. 找到所有章节标记（三种模式取并集，按位置排序去重）
        var markers = FindAllMarkers(content);

        // 3. 无章节标记 → 整文件作为单章
        if (markers.Count == 0)
        {
            return new List<Chapter>
            {
                new()
                {
                    Index = 1,
                    Title = "正文",
                    OriginalContent = content.Trim()
                }
            };
        }

        // 4. 按标记构建章节
        var chapters = new List<Chapter>();

        // 4a. 处理第一章之前的文字（简介/序言）作为独立章节
        int firstMarkerPos = markers[0].Position;
        if (firstMarkerPos > 0)
        {
            string preContent = content[..firstMarkerPos].Trim();
            if (!string.IsNullOrWhiteSpace(preContent) && preContent.Length > 20)
            {
                chapters.Add(new Chapter
                {
                    Index = 0, // 临时，后面统一重编号
                    Title = "简介/序",
                    OriginalContent = preContent
                });
            }
            else if (!string.IsNullOrWhiteSpace(preContent))
            {
                // 太短的序言内容合并到第一章
                markers[0] = markers[0] with
                {
                    Position = 0
                };
            }
        }

        // 4b. 切割章节内容（按文本位置顺序，保持原文结构）
        var rawChapters = new List<Chapter>();
        for (int i = 0; i < markers.Count; i++)
        {
            var marker = markers[i];
            int start = marker.Position;
            int end = (i < markers.Count - 1) ? markers[i + 1].Position : content.Length;

            if (start >= content.Length) continue;
            if (end > content.Length) end = content.Length;

            string chapterContent = content[start..end].Trim();
            string displayTitle = string.IsNullOrWhiteSpace(marker.Title) ? "无题" : marker.Title;

            rawChapters.Add(new Chapter
            {
                Index = marker.Number,
                Title = displayTitle,
                OriginalContent = chapterContent
            });
        }

        // 4c. 智能过滤：移除目录页/TOC 伪章节
        //     当连续的章节标记间距极近（<200字符），且实质为目录列表而非正文时，
        //     将这些标记对应的内容合并到其后第一个有效章节中
        chapters = FilterArtifactChapters(rawChapters);

        // 5. 按文本位置顺序重新编号（确保连续、从1开始、顺序正确）
        for (int i = 0; i < chapters.Count; i++)
            chapters[i].Index = i + 1;

        return chapters;
    }

    /// <summary>过滤目录页产生的虚假章节（连续短章节 → 合并到后续正文）</summary>
    private static List<Chapter> FilterArtifactChapters(List<Chapter> rawChapters)
    {
        if (rawChapters.Count <= 1) return rawChapters;

        var result = new List<Chapter>();
        var pendingMerge = new List<Chapter>(); // 疑似目录条目的短章节

        foreach (var ch in rawChapters)
        {
            // 章节内容极短（<150字）→ 疑似目录条目，暂存待合并
            if (ch.OriginalContent.Length < 150)
            {
                pendingMerge.Add(ch);
            }
            else
            {
                if (pendingMerge.Count > 0)
                {
                    // 将之前暂存的短章节合并到当前正文章节前部
                    foreach (var shortCh in pendingMerge)
                    {
                        // 短章节的标题行（标记本身）可以去掉，只保留有意义的文字
                        string trimmed = shortCh.OriginalContent.Trim();
                        // 如果短章节除了标记行外没有实质内容，直接丢弃
                        if (trimmed.Length > 5)
                        {
                            ch.OriginalContent = trimmed + "\n\n" + ch.OriginalContent;
                        }
                    }
                    pendingMerge.Clear();
                }
                result.Add(ch);
            }
        }

        // 末尾残留的短章节：如果只有标题没有内容，丢弃
        foreach (var leftovers in pendingMerge)
        {
            if (leftovers.OriginalContent.Trim().Length > 20)
            {
                result.Add(leftovers);
            }
        }

        return result.Count > 0 ? result : rawChapters;
    }

    // ===== 标记查找（三种模式合并，去重，按位置排序） =====
    private static List<ChapterMarker> FindAllMarkers(string content)
    {
        var dict = new Dictionary<int, ChapterMarker>(); // 按位置去重

        // 模式0（优先）：组合章节 — "第X部 第Y章 标题"
        foreach (Match m in PartChapterRegex.Matches(content))
        {
            int num = ChineseNumberToInt(m.Groups[2].Value); // 章序号
            if (num <= 0) num = dict.Count + 1;
            string title = m.Groups[3].Value.Trim();

            if (!dict.ContainsKey(m.Index))
            {
                dict[m.Index] = new ChapterMarker
                {
                    Position = m.Index,
                    Number = num,
                    Title = title,
                    IsSpecial = false
                };
            }
        }

        // 模式1：标准章节 — "第X章/回/部 标题"
        foreach (Match m in StandardRegex.Matches(content))
        {
            if (dict.ContainsKey(m.Index)) continue; // 已被组合格式覆盖

            int num = ChineseNumberToInt(m.Groups[1].Value);
            if (num <= 0) num = dict.Count + 1;
            string title = m.Groups[2].Value.Trim();

            dict[m.Index] = new ChapterMarker
            {
                Position = m.Index,
                Number = num,
                Title = title,
                IsSpecial = false
            };
        }

        // 模式2：特殊章节（序章/番外等）
        int specialIdx = 0;
        foreach (Match m in SpecialRegex.Matches(content))
        {
            if (dict.ContainsKey(m.Index)) continue;

            specialIdx++;
            string specialName = m.Groups[1].Value;
            string title = m.Groups[2].Value.Trim();

            dict[m.Index] = new ChapterMarker
            {
                Position = m.Index,
                Number = -specialIdx,
                Title = string.IsNullOrWhiteSpace(title) ? specialName : $"{specialName}：{title}",
                IsSpecial = true
            };
        }

        // 模式3：英文章节 — "Chapter X: Title"
        foreach (Match m in EnglishRegex.Matches(content))
        {
            if (dict.ContainsKey(m.Index)) continue;

            if (int.TryParse(m.Groups[1].Value, out int num) && num > 0)
            {
                string title = m.Groups[2].Value.Trim();

                dict[m.Index] = new ChapterMarker
                {
                    Position = m.Index,
                    Number = num,
                    Title = title,
                    IsSpecial = false
                };
            }
        }

        // 按位置排序
        return dict.Values.OrderBy(m => m.Position).ToList();
    }

    // ===== 中文数字 → 整数 =====
    /// <summary>
    /// 中文数字转阿拉伯数字。
    /// 支持："一"→1、"十一"→11、"二十"→20、"一百二十"→120、"两千零二十"→2020
    /// </summary>
    public static int ChineseNumberToInt(string chineseNum)
    {
        if (string.IsNullOrWhiteSpace(chineseNum)) return -1;

        // 纯阿拉伯数字
        if (int.TryParse(chineseNum, out int result))
            return result;

        int total = 0;
        int current = 0;

        foreach (char c in chineseNum)
        {
            if (!ChineseNumberMap.TryGetValue(c, out int value))
                continue;

            if (value >= 10) // 十/百/千
            {
                if (current == 0) current = 1; // 如"十"=10、"千"=1000
                total += current * value;
                current = 0;
            }
            else // 0~9
            {
                current = value;
            }
        }

        total += current; // 剩余个位数
        return total > 0 ? total : -1;
    }

    // ===== 手动拆分章节 =====
    public static List<Chapter> ManualSplit(List<Chapter> chapters, int chapterIndex, int splitPosition, string newTitle)
    {
        if (chapterIndex < 0 || chapterIndex >= chapters.Count) return chapters;

        var chapter = chapters[chapterIndex];
        var content = chapter.OriginalContent;

        if (splitPosition <= 0 || splitPosition >= content.Length) return chapters;

        var part1 = content[..splitPosition].Trim();
        var part2 = content[splitPosition..].Trim();

        if (string.IsNullOrWhiteSpace(part2)) return chapters;

        chapter.OriginalContent = part1;

        var newChapters = chapters.Take(chapterIndex + 1).ToList();

        var newChapter = new Chapter
        {
            Index = chapter.Index + 1,
            Title = string.IsNullOrWhiteSpace(newTitle) ? "新章节" : newTitle,
            OriginalContent = part2
        };
        newChapters.Add(newChapter);

        var remaining = chapters.Skip(chapterIndex + 1).ToList();
        for (int i = 0; i < remaining.Count; i++)
            remaining[i].Index = newChapter.Index + 1 + i;
        newChapters.AddRange(remaining);

        return newChapters;
    }

    // ===== 合并相邻章节 =====
    public static List<Chapter> MergeChapters(List<Chapter> chapters, int index1, int index2)
    {
        if (index1 < 0 || index2 < 0 || index1 >= chapters.Count || index2 >= chapters.Count)
            return chapters;
        if (Math.Abs(index1 - index2) != 1) // 只能合并相邻
            return chapters;

        var c1 = chapters[Math.Min(index1, index2)];
        var c2 = chapters[Math.Max(index1, index2)];
        c1.OriginalContent += "\n\n" + c2.OriginalContent;
        c1.Title = $"{c1.Title} / {c2.Title}";

        chapters.Remove(c2);

        // 重新编号
        for (int i = 0; i < chapters.Count; i++)
            chapters[i].Index = i + 1;

        return chapters;
    }
}
