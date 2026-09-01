using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace YangzaiWorkshop.Views;

/// <summary>
/// 带「@ 提及图片」功能的提示词编辑器。
/// 特性：
/// 1. 输入 @ 时弹出下拉框，列出已上传的参考图片，选择后插入 "@图片名"；
/// 2. "@图片名" 以高亮颜色/底色与其余正文区分，点击可弹出下拉框更换为其他图片；
/// 3. 图像名称自动匹配：提示词中出现与某张参考图同名（不含扩展名）的文字时自动高亮为提及。
/// 编辑器基于 RichTextBox（Run 支持内联着色），文本内容通过 Text 属性读写，@ 提及保留在文本中，
/// 生成时随参考图一起交给模型理解。
/// </summary>
public sealed class PromptMentionBox : Grid
{
    private readonly RichTextBox _editor;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private readonly TextBlock _watermark;

    /// <summary>当前参考图提及候选（名称 → 路径）</summary>
    private readonly List<MentionCandidate> _candidates = new();

    /// <summary>与文档同步的提及 Run 元数据（每次重建后刷新）</summary>
    private readonly List<MentionRun> _mentionRuns = new();

    private bool _suppress;
    /// <summary>正在输入 @ 提及时 @ 在纯文本中的下标，-1 表示不在输入</summary>
    private int _typingAt = -1;
    /// <summary>点击某提及准备更换时的目标</summary>
    private ClickTarget? _clickTarget;

    /// <summary>候选缩略图缓存（名称 → 已解码缩略图），供悬停预览即时显示。</summary>
    private readonly Dictionary<string, BitmapImage?> _thumbCache = new();
    /// <summary>用户「取消映射」的裸名称出现（名称, 裸名称序号），重建高亮时跳过这些出现。</summary>
    private readonly List<(string Name, int BareOrdinal)> _excluded = new();
    /// <summary>悬停预览浮层：鼠标悬停在 @ 提及/自动匹配名称上时显示对应参考图大图与操作提示。
    /// 采用控件内叠加层（IsHitTestVisible=false）而非独立 Popup 窗口，避免独立窗口在光标处导致指针反复切换闪动。</summary>
    private readonly Image _previewImage;
    private readonly TextBlock _previewCaption;
    private readonly Border _previewCard;
    /// <summary>当前鼠标悬停的提及 Run（用于定位与去重）。</summary>
    private MentionRun? _hoveredRun;

    /// <summary>自动匹配开关：true=输入时实时按图名高亮；false=暂停，写完再点按钮启用。</summary>
    private bool _autoMatchLive = true;
    private Button _autoMatchBtn = null!;

    // 撤销/重做（模型层：纯文本 + 取消关联名单双快照）
    private readonly List<(string Text, List<(string Name, int Ordinal)> Excl)> _undoStack = new();
    private readonly List<(string Text, List<(string Name, int Ordinal)> Excl)> _redoStack = new();
    private string? _lastSnapshot;
    private bool _isUndoRedo;
    private const int MaxUndo = 60;
    /// <summary>退出预览前保存的编辑光标笔刷（预览时隐藏光标，避免在预览图上持续闪动）。</summary>
    private Brush? _savedCaretBrush;
    /// <summary>高亮重建防抖定时器：每次文本变化重启，停顿后才重建，避免每敲一个键就重建文档打断输入法。</summary>
    private readonly DispatcherTimer _rebuildTimer;
    

    /// <summary>纯文本内容（含 @提及 文本），读写均自动重建高亮</summary>
    public string Text
    {
        get => GetPlainText();
        set => SetPlainText(value ?? string.Empty, null);
    }

    /// <summary>内部编辑器</summary>
    public RichTextBox Editor => _editor;

    /// <summary>提示词水印文字</summary>
    public string Watermark
    {
        get => _watermark.Text;
        set => _watermark.Text = value;
    }

    public PromptMentionBox()
    {
        _editor = new RichTextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            FontSize = 13.5,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = Brush("TextPrimaryBrush", Color.FromRgb(0xE8, 0xE8, 0xEE)),
            Background = Brush("CardBackgroundBrush", Color.FromRgb(0x24, 0x24, 0x2E)),
            BorderBrush = Brush("BorderBrush", Color.FromRgb(0x3A, 0x3A, 0x46)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _editor.Document = new FlowDocument();
        _editor.TextChanged += OnTextChanged;
        // 高亮重建走防抖：停顿 500ms 无输入后才重建文档，避免每敲一个键就重建而打断中文输入法组字
        _rebuildTimer = new DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _rebuildTimer.Tick += (_, _) =>
        {
            _rebuildTimer.Stop();
            // 停顿后文本已定型（中文输入法已组字提交），在此记录一次撤销快照，避免把组字过程中的拼音中间态记入撤销栈
            if (!_isUndoRedo) CaptureForUndo();
            try { RebuildDocument(GetPlainText(), GetCaretOffset()); } catch { }
        };
        _editor.PreviewMouseLeftButtonUp += OnEditorMouseUp;
        _editor.PreviewKeyDown += OnEditorKeyDown;
        _editor.PreviewMouseMove += OnEditorMouseMove;
        _editor.MouseLeave += (_, _) => ClosePreview();
        _editor.PreviewMouseLeftButtonDown += (_, _) =>
        {
            // 用鼠标在正文点选时关闭下拉候选（点击 @ 提及由 mouse-up 逻辑重新打开）
            ClosePopup();
            ClosePreview();
        };
        _editor.LostKeyboardFocus += (_, _) =>
        {
            // 仅当焦点离开本控件（进入其他窗口/控件）时才收起浮层；点进下拉框本身不算失焦
            if (IsFocusOnPopup(_popup)) { ClosePreview(); return; }
            ClosePopup();
            ClosePreview();
        };

        // 水印
        _watermark = new TextBlock
        {
            Text = "在此输入提示词…",
            FontSize = _editor.FontSize,
            FontFamily = _editor.FontFamily,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Margin = new Thickness(_editor.Padding.Left + 4, _editor.Padding.Top + 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap
        };

        // 提及下拉框
        _list = new ListBox
        {
            Background = Brush("WindowBackgroundBrush", Color.FromRgb(0x28, 0x28, 0x32)),
            Foreground = Brush("TextPrimaryBrush", Colors.White),
            BorderThickness = new Thickness(0),
            FontSize = 12,
            MaxHeight = 220,
            Padding = new Thickness(4)
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        _list.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem lbi && lbi.Tag is MentionCandidate c)
            {
                if (c.IsCancel) CancelMapping();
                else SelectCandidate(c);
            }
        };
        var popupCard = new Border
        {
            Background = Brush("WindowBackgroundBrush", Color.FromRgb(0x28, 0x28, 0x32)),
            BorderBrush = Brush("BorderBrush", Color.FromRgb(0x3A, 0x3A, 0x46)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            Child = _list,
            MaxWidth = 300
        };
        _popup = new Popup
        {
            PlacementTarget = _editor,
            Placement = PlacementMode.RelativePoint,
            StaysOpen = true,               // 保持打开，允许在下拉内选中（StaysOpen=false 会因失焦立刻关闭）
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = popupCard
        };

        // 悬停预览浮层：鼠标悬停在 @ 提及/自动匹配名称上时显示对应参考图大图与操作提示
        _previewImage = new Image
        {
            Stretch = Stretch.Uniform,
            MinWidth = 150, MaxWidth = 240,
            MinHeight = 100, MaxHeight = 180
        };
        _previewCaption = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush("TextPrimaryBrush", Colors.White),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 240,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var previewPanel = new StackPanel
        {
            Background = Brush("WindowBackgroundBrush", Color.FromRgb(0x28, 0x28, 0x32)),
            Children = { _previewImage, _previewCaption }
        };
        _previewCard = new Border
        {
            Background = Brush("WindowBackgroundBrush", Color.FromRgb(0x28, 0x28, 0x32)),
            BorderBrush = Brush("PrimaryBrush", Color.FromRgb(0x4A, 0x90, 0xE2)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = previewPanel,
            IsHitTestVisible = false,                 // 不拦截鼠标，避免遮挡正文点击
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 12, 12),
            MaxWidth = 264,
            Visibility = Visibility.Collapsed
        };
        System.Windows.Controls.Panel.SetZIndex(_previewCard, 100);   // 置于编辑器之上

        // 右下角悬浮「一键匹配」按钮：立即对当前文本按图名匹配并高亮；无参考图时自动隐藏（避免点了没反应）
        var matchStyle = new Style(typeof(Button));
        matchStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
        matchStyle.Setters.Add(new Setter(Button.FontSizeProperty, 12.0));
        matchStyle.Setters.Add(new Setter(Button.FontWeightProperty, FontWeights.SemiBold));
        matchStyle.Setters.Add(new Setter(Button.FontFamilyProperty, new FontFamily("Microsoft YaHei UI")));
        matchStyle.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xF0, 0xF4, 0xFF))));
        var matchBorder = new Setter(Button.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2)));
        matchStyle.Setters.Add(matchBorder);
        // 圆角矩形、浅色底，扁平观感；圆角适中避免字体溢出
        var matchBg = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
        matchBg.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x3A, 0x63, 0xA0))));
        matchStyle.Triggers.Add(matchBg);
        matchStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x45, 0x6D))));
        matchStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
        matchStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 14, 12)));
        matchStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 6, 14, 6)));
        var round = (System.Windows.Controls.ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            @"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                              xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                              TargetType='Button'>
                <Border CornerRadius='6'
                        Background='{TemplateBinding Background}'
                        BorderBrush='{TemplateBinding BorderBrush}'
                        BorderThickness='{TemplateBinding BorderThickness}'
                        Padding='{TemplateBinding Padding}'>
                  <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                </Border>
              </ControlTemplate>");
        matchStyle.Setters.Add(new Setter(Button.TemplateProperty, round));

        _autoMatchBtn = new Button { Style = matchStyle, Content = "⚡ 一键匹配", ToolTip = "立即把提示词中与参考图名称相同的文字高亮关联（Ctrl+Z 可撤销）" };
        _autoMatchBtn.Click += (_, _) => { RunAutoMatch(); MainWindow.Notify("✓ 已执行自动匹配，结果可 Ctrl+Z 撤销"); };
        _autoMatchBtn.HorizontalAlignment = HorizontalAlignment.Right;
        _autoMatchBtn.VerticalAlignment = VerticalAlignment.Bottom;
        System.Windows.Controls.Panel.SetZIndex(_autoMatchBtn, 90);

        Children.Add(_editor);
        Children.Add(_watermark);
        Children.Add(_autoMatchBtn);
        Children.Add(_previewCard);
        UpdateWatermark();
    }

    // ===== 对外接口 =====
    public void SetRefImages(IReadOnlyList<string> paths)
    {
        _candidates.Clear();
        if (paths != null)
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                var name = CandidateName(p);
                if (name.Length == 0) continue;
                if (_candidates.Any(c => c.Name == name)) continue;
                _candidates.Add(new MentionCandidate(name, p));
            }
        }
        PreloadThumbs();
        _clickTarget = null;
        _typingAt = -1;
        _hoveredRun = null;
        // 无参考图时隐藏「一键匹配」按钮，避免点了没反应的假象
        if (_autoMatchBtn != null)
            _autoMatchBtn.Visibility = _candidates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var text = GetPlainText();
        RebuildDocument(text, GetCaretOffset());
        ClosePopup();
        ClosePreview();
    }

    /// <summary>后台预解码全部候选缩略图到缓存，悬停预览时即时显示（候选通常 ≤6 张）。</summary>
    private void PreloadThumbs()
    {
        _thumbCache.Clear();
        foreach (var c in _candidates)
        {
            var name = c.Name;
            _thumbCache[name] = null;
            Task.Run(() => DecodeImage(c.Path, 160)).ContinueWith(t =>
            {
                try
                {
                    if (_thumbCache.ContainsKey(name))
                        _thumbCache[name] = t.Result;
                }
                catch { }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>追加文本到末尾（自动换行），并触发同名自动匹配。</summary>
    public void AppendText(string text)
    {
        var cur = GetPlainText();
        var merged = string.IsNullOrWhiteSpace(cur) ? text : cur.TrimEnd() + "\n" + text;
        SetPlainText(merged, merged.Length);
        Focus();
    }

    /// <summary>聚焦编辑器并把光标移到末尾。</summary>
    public new bool Focus()
    {
        _editor.Focus();
        try { _editor.CaretPosition = _editor.Document.ContentEnd; } catch { }
        return _editor.IsKeyboardFocused;
    }

    // ===== 文本 / 文档 =====

    private string GetPlainText()
    {
        var sb = new StringBuilder();
        bool firstBlock = true;
        foreach (var block in _editor.Document.Blocks)
        {
            if (!firstBlock) sb.Append('\n');
            firstBlock = false;
            if (block is not Paragraph p) continue;
            foreach (var inline in p.Inlines)
            {
                if (inline is Run r) sb.Append(r.Text);
                else if (inline is LineBreak) sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    private int GetCaretOffset()
    {
        try { return TextIndexAt(_editor.CaretPosition); }
        catch { return 0; }
    }

    private Paragraph? FirstParagraph => _editor.Document.Blocks.FirstBlock as Paragraph;

    /// <summary>
    /// 将 TextPointer 映射为「纯文本下标」（Run 内字符各计 1，LineBreak 计 1）。
    /// 不能用 GetOffsetToPosition(ContentStart)：它会额外计入 FlowDocument/Paragraph/LineBreak 等元素边界符号，
    /// 导致下标与 GetPlainText 的字符下标错位（此前 @ 提及与自动匹配因此全部失效）。
    /// </summary>
    private int TextIndexAt(TextPointer tp)
    {
        var para = FirstParagraph;
        if (para == null) return 0;
        int cur = 0;
        foreach (var inline in para.Inlines)
        {
            if (inline is Run r)
            {
                if (tp.CompareTo(r.ContentEnd) <= 0)
                    return cur + Math.Max(0, r.ContentStart.GetOffsetToPosition(tp));
                cur += r.Text.Length;
            }
            else if (inline is LineBreak lb)
            {
                if (tp.CompareTo(lb.ContentEnd) <= 0) return cur;
                cur += 1;
            }
        }
        return cur;
    }

    /// <summary>将「纯文本下标」映射为 TextPointer（与 TextIndexAt 互逆），用于重建后恢复光标。</summary>
    private TextPointer TextPointerAt(int index)
    {
        var para = FirstParagraph;
        if (para == null) return _editor.Document.ContentEnd;
        int cur = 0;
        foreach (var inline in para.Inlines)
        {
            if (inline is Run r)
            {
                int len = r.Text.Length;
                if (index <= cur + len)
                    return r.ContentStart.GetPositionAtOffset(index - cur, LogicalDirection.Forward);
                cur += len;
            }
            else if (inline is LineBreak lb)
            {
                if (index <= cur) return lb.ContentStart;
                cur += 1;
            }
        }
        return _editor.Document.ContentEnd;
    }

    /// <summary>统一换行符：\r\n 与 \r 归一为 \n，保证文本下标与文档结构一一对应。</summary>
    private static string NormalizeNewlines(string s)
        => s.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>把 [start,end) 的普通文本拆成 Run + LineBreak 加入段落（换行不再坍缩为普通字符）。</summary>
    private static void AddTextSegment(Paragraph para, string text, int start, int end)
    {
        int segStart = start;
        for (int i = start; i < end; i++)
        {
            if (text[i] == '\n')
            {
                if (i > segStart) para.Inlines.Add(new Run(text[segStart..i]));
                para.Inlines.Add(new LineBreak());
                segStart = i + 1;
            }
        }
        if (segStart < end) para.Inlines.Add(new Run(text[segStart..end]));
    }

    private void SetPlainText(string text, int? caret)
    {
        RebuildDocument(text, caret);
    }

    /// <summary>把纯文本重建为富文本文档：同名/提及文字高亮，其余为普通文字，并恢复光标。</summary>
    private void RebuildDocument(string text, int? caret)
    {
        var normalized = NormalizeNewlines(text ?? string.Empty);
        _suppress = true;
        ClosePreview();
        _hoveredRun = null;
        try
        {
            _editor.Document.Blocks.Clear();
            _mentionRuns.Clear();
            var para = new Paragraph { Margin = new Thickness(0) };
            var ranges = ComputeMentionRanges(normalized, _autoMatchLive);
            int pos = 0;
            foreach (var m in ranges)
            {
                if (m.Start > pos) AddTextSegment(para, normalized, pos, m.Start);
                var run = BuildMentionRun(normalized[m.Start..m.End]);
                _mentionRuns.Add(new MentionRun(run, m.Name, m.HasAt, m.Start, m.End));
                para.Inlines.Add(run);
                pos = m.End;
            }
            if (pos < normalized.Length) AddTextSegment(para, normalized, pos, normalized.Length);
            _editor.Document.Blocks.Add(para);
        }
        finally
        {
            _suppress = false;
        }

        if (caret is int c)
        {
            try
            {
                var tp = TextPointerAt(Math.Clamp(c, 0, normalized.Length));
                _editor.CaretPosition = tp;
            }
            catch { }
        }
        UpdateWatermark();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        _clickTarget = null;
        var caret = GetCaretOffset();
        var text = GetPlainText();
        UpdateTypingState(text, caret);
        // 高亮重建改为“停顿 500ms 后”触发（防抖），避免每次击键重建文档打断中文输入法
        _rebuildTimer.Stop();
        _rebuildTimer.Start();

        if (_typingAt >= 0)
        {
            var partial = _typingAt + 1 <= caret ? text.Substring(_typingAt + 1, caret - _typingAt - 1) : string.Empty;
            ShowPopup(FilterCandidates(partial), caret, partial, clickMode: false, highlightIndex: 0);
        }
        else
        {
            ClosePopup();
        }
    }

    /// <summary>更新「正在输入 @提及」状态：以光标前最后一个 @ 为起点，@ 后均为普通字则为输入中。</summary>
    private void UpdateTypingState(string text, int caret)
    {
        if (_typingAt >= 0)
        {
            if (caret <= _typingAt || _typingAt >= text.Length || text[_typingAt] != '@')
            {
                _typingAt = -1;
                return;
            }
            var partial = text.Substring(_typingAt + 1, Math.Min(caret, text.Length) - _typingAt - 1);
            if (!IsWordPartial(partial)) { _typingAt = -1; return; }
            return;
        }
        if (caret > 0 && caret <= text.Length && text[caret - 1] == '@')
        {
            _typingAt = caret - 1;
            return;
        }
        _typingAt = -1;
    }

    /// <summary>@ 后的输入片段是否仍是「单词」：全为字母/数字/中日韩/下划线/连字符视为还在输入提及名。</summary>
    private static bool IsWordPartial(string s)
    {
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') continue;
            return false;
        }
        return true;
    }

    private static string CandidateName(string pathOrLabel)
    {
        if (pathOrLabel.StartsWith("frame://", StringComparison.OrdinalIgnoreCase))
        {
            // 帧伪路径格式 frame://label|源视频，名称仅取 label 部分
            var rest = pathOrLabel["frame://".Length..].Trim();
            var pipe = rest.IndexOf('|');
            return (pipe >= 0 ? rest[..pipe] : rest).Trim();
        }
        var n = Path.GetFileNameWithoutExtension(pathOrLabel);
        return string.IsNullOrEmpty(n) ? Path.GetFileName(pathOrLabel) : n;
    }

    // ===== 提及匹配（自动匹配 + @ 显式提及） =====

    private List<MentionRange> ComputeMentionRanges(string text, bool includeBareAuto)
    {
        var result = new List<MentionRange>();
        if (string.IsNullOrEmpty(text) || _candidates.Count == 0) return result;

        var occupied = new bool[text.Length];
        var names = _candidates.Select(c => c.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .OrderByDescending(n => n.Length)
            .ToList();

        // 第一遍：显式 "@名称"
        foreach (var name in names)
        {
            var at = "@" + name;
            int i = 0;
            while ((i = text.IndexOf(at, i, StringComparison.Ordinal)) >= 0)
            {
                if (!IsOccupied(occupied, i, i + at.Length))
                {
                    Mark(occupied, i, i + at.Length);
                    result.Add(new MentionRange(i, i + at.Length, name, true));
                }
                i += at.Length;
            }
        }
        // 第二遍：裸名称自动匹配（跳过已被占用的位置与紧跟 @ 的位置，并跳过用户已「取消映射」的出现）
        // 仅在「自动匹配：开」时进行实时裸名高亮；关闭时保留 `@名` 显式提及的高亮，裸名不做匹配
        if (includeBareAuto)
        foreach (var name in names)
        {
            int i = 0;
            int bareOrdinal = 0;   // 当前裸出现（前一个字符非 @）的序号，与取消映射时计数一致
            while ((i = text.IndexOf(name, i, StringComparison.Ordinal)) >= 0)
            {
                var end = i + name.Length;
                bool bare = !(i > 0 && text[i - 1] == '@');
                if (bare) bareOrdinal++;
                if (bare
                    && !IsOccupied(occupied, i, end)
                    && !IsBareExcluded(name, bareOrdinal - 1))
                {
                    Mark(occupied, i, end);
                    result.Add(new MentionRange(i, end, name, false));
                }
                i += name.Length;
            }
        }
        return result.OrderBy(r => r.Start).ThenBy(r => r.End).ToList();
    }

    /// <summary>是否已被用户取消映射（裸名称 + 裸出现序号）。</summary>
    private bool IsBareExcluded(string name, int bareOrdinal)
    {
        foreach (var (n, o) in _excluded)
            if (n == name && o == bareOrdinal) return true;
        return false;
    }

    /// <summary>统计 position 之前 name 的「裸出现」个数（前一个字符非 @），用于取消映射时定位出现序号。</summary>
    private static int CountBareOccurrencesBefore(string text, string name, int position)
    {
        int count = 0;
        int i = 0;
        while ((i = text.IndexOf(name, i, StringComparison.Ordinal)) >= 0 && i < position)
        {
            if (!(i > 0 && text[i - 1] == '@')) count++;
            i += name.Length;
        }
        return count;
    }

    private static bool IsOccupied(bool[] occupied, int start, int end)
    {
        for (int i = start; i < end; i++)
            if (occupied[i]) return true;
        return false;
    }

    private static void Mark(bool[] occupied, int start, int end)
    {
        for (int i = start; i < end && i < occupied.Length; i++) occupied[i] = true;
    }

    private static Run BuildMentionRun(string segment)
    {
        return new Run(segment)
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xC9, 0xFF)),
            Background = new SolidColorBrush(Color.FromArgb(0x44, 0x2E, 0x6F, 0xC8)),
            FontWeight = FontWeights.SemiBold
        };
    }

    // ===== 下拉框 =====

    private List<MentionCandidate> FilterCandidates(string partial)
    {
        if (_candidates.Count == 0) return new List<MentionCandidate>();
        if (string.IsNullOrEmpty(partial))
            return new List<MentionCandidate>(_candidates);
        var p = partial.ToLowerInvariant();
        return _candidates
            .Where(c => c.Name.ToLowerInvariant().Contains(p))
            .ToList();
    }

    private void ShowPopup(List<MentionCandidate> items, int caretOffset, string partial, bool clickMode, int highlightIndex)
    {
        #region debug-point B:showpopup
        Dbg($"B:ShowPopup clickMode={clickMode} items={items.Count} partial=‘{partial}’");
        #endregion
        if (items.Count == 0) { Dbg("B:ShowPopup items==0 -> ClosePopup (clickMode 无法显示取消行)"); ClosePopup(); return; }

        _list.Items.Clear();

        // 点击模式：在顶部提供「取消关联」，把当前提及还原为普通文字
        if (clickMode)
        {
            _list.Items.Add(new ListBoxItem
            {
                Content = BuildCancelRow(),
                Tag = MentionCandidate.CancelMarker,
                Padding = new Thickness(4, 2, 4, 2),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            });
        }

        for (int i = 0; i < items.Count; i++)
        {
            var c = items[i];
            var lbi = new ListBoxItem
            {
                Content = BuildCandidateRow(c, isCurrent: clickMode && i == highlightIndex),
                Tag = c,
                Padding = new Thickness(4, 2, 4, 2),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            _list.Items.Add(lbi);
            LoadCandidateThumb(lbi, c);
        }
        if (highlightIndex >= 0 && highlightIndex < items.Count)
            _list.SelectedIndex = clickMode ? highlightIndex + 1 : highlightIndex;   // 点击模式顶部多了「取消关联」项
        else if (!clickMode)
            _list.SelectedIndex = 0;

        try
        {
            // GetCharacterRect 返回相对编辑内容区（含 Padding）的坐标，RelativePoint 以编辑器左上角为原点，
            // 需把内边距加回，才能让下拉对齐到 @ 光标正下方
            var rect = _editor.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
            _popup.HorizontalOffset = Math.Max(0, rect.X + _editor.Padding.Left + _editor.BorderThickness.Left);
            _popup.VerticalOffset = Math.Max(0, rect.Bottom + _editor.Padding.Top + _editor.BorderThickness.Top + 4);
        }
        catch { }
        _popup.IsOpen = true;
    }

    /// <summary>下拉框条目：左侧缩略图 + 名称 +（当前项）提示。</summary>
    private UIElement BuildCandidateRow(MentionCandidate c, bool isCurrent)
    {
        var name = new TextBlock
        {
            Text = c.Name,
            FontSize = 12,
            Foreground = Brush("TextPrimaryBrush", Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 4, 0)
        };
        var current = new TextBlock
        {
            Text = isCurrent ? "当前" : "",
            FontSize = 10,
            Foreground = Brush("PrimaryBrush", Color.FromRgb(0x4A, 0x90, 0xE2)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var thumb = new Border
        {
            Width = 26, Height = 26,
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x28)),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = new Image { Stretch = Stretch.UniformToFill }
        };

        var dock = new DockPanel { MinHeight = 24 };
        DockPanel.SetDock(thumb, Dock.Left);
        DockPanel.SetDock(current, Dock.Right);
        dock.Children.Add(thumb);
        dock.Children.Add(current);
        dock.Children.Add(name);
        return dock;
    }

    /// <summary>后台解码候选缩略图，回填到仍在列表中的条目。</summary>
    private void LoadCandidateThumb(ListBoxItem lbi, MentionCandidate c)
    {
        var thumb = (lbi.Content as DockPanel)?.Children.OfType<Border>().FirstOrDefault()?.Child as Image;
        if (thumb == null) return;
        Task.Run(() => DecodeImage(c.Path, 48)).ContinueWith(t =>
        {
            try
            {
                if (t.Result is BitmapImage bmp && _list.Items.Contains(lbi))
                    thumb.Source = bmp;
            }
            catch { }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        // 撤销 / 重做（Ctrl+Z / Ctrl+Y）
        if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; return; }

        // 原子化删除：Backspace/Delete 落在某个 @提及/自动匹配名内时，删除整个提及而不是单个字符
        if (!ctrl && e.Key == Key.Back && _editor.Selection.Text.Length == 0) { if (TryAtomicDelete(delta: -1)) { e.Handled = true; return; } }
        if (!ctrl && e.Key == Key.Delete && _editor.Selection.Text.Length == 0) { if (TryAtomicDelete(delta: 0)) { e.Handled = true; return; } }

        if (!_popup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down:
                if (_list.Items.Count > 0)
                    _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Up:
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                if (_list.SelectedItem is ListBoxItem lbi && lbi.Tag is MentionCandidate c)
                {
                    if (c.IsCancel) CancelMapping();
                    else SelectCandidate(c);
                }
                e.Handled = true;
                break;
            case Key.Escape:
                ClosePopup();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 原子化删除：若 Backspace(delta=-1)/Delete(delta=0) 要删的字符落在某个 @提及或自动匹配名内，
    /// 则删除整个提及区间；否则返回 false 走普通逐字符删除。
    /// </summary>
    private bool TryAtomicDelete(int delta)
    {
        var caret = GetCaretOffset();
        if (caret <= 0 && delta == -1) return false;
        int charIdx = delta == -1 ? caret - 1 : caret;
        var text = GetPlainText();
        if (charIdx < 0 || charIdx >= text.Length) return false;
        var m = _mentionRuns.FirstOrDefault(r => r.Start <= charIdx && charIdx < r.End);
        if (m == null) return false;
        CaptureForUndo();
        var newText = text.Remove(m.Start, m.End - m.Start);
        int newCaret = m.Start;
        ClosePopup(); ClosePreview();
        SetPlainText(newText, newCaret);
        _editor.Focus();
        return true;
    }

    /// <summary>是否开启「输入时实时按图名自动匹配」。由窗口「设置」控制并持久化。</summary>
    public bool AutoMatchLive
    {
        get => _autoMatchLive;
        set
        {
            if (_autoMatchLive == value) return;
            _autoMatchLive = value;
            var t = GetPlainText();
            SetPlainText(t, GetCaretOffset());
        }
    }

    /// <summary>一键自动匹配：立即对当前文本执行一轮按图名匹配并高亮（不受实时开关影响）。</summary>
    public void RunAutoMatch()
    {
        var text = GetPlainText();
        var caret = GetCaretOffset();
        ClosePreview();
        // 一键匹配应重新关联当前所有可匹配文字：清除此前「取消关联」留下的排除项，而非沿用被禁用的状态
        if (_excluded.Count > 0)
        {
            CaptureForUndo();
            _excluded.Clear();
        }
        bool saved = _autoMatchLive;
        _autoMatchLive = true;
        try { SetPlainText(text, caret); }
        finally { _autoMatchLive = saved; }
    }

    // ===== 撤销 / 重做（模型层：纯文本快照） =====

    /// <summary>在文本发生实质变化前记录快照（文本 + 排除名单，避免重复快照，清空重做栈）。</summary>
    private void CaptureForUndo()
    {
        var text = GetPlainText();
        if (_lastSnapshot == text) return;
        _undoStack.Add((_lastSnapshot ?? string.Empty, new List<(string Name, int Ordinal)>(_excluded)));
        if (_undoStack.Count > MaxUndo) _undoStack.RemoveAt(0);
        _redoStack.Clear();
        _lastSnapshot = text;
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        var cur = (_lastSnapshot ?? GetPlainText(), new List<(string Name, int Ordinal)>(_excluded));
        var (prevText, prevExcl) = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(cur);
        if (_redoStack.Count > MaxUndo) _redoStack.RemoveAt(0);
        ApplySnapshot(prevText, prevExcl);
        _lastSnapshot = prevText;
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        var cur = (_lastSnapshot ?? GetPlainText(), new List<(string Name, int Ordinal)>(_excluded));
        var (nextText, nextExcl) = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(cur);
        if (_undoStack.Count > MaxUndo) _undoStack.RemoveAt(0);
        ApplySnapshot(nextText, nextExcl);
        _lastSnapshot = nextText;
    }

    /// <summary>应用历史快照：同时还原文本与「取消关联」名单（保证 Ctrl+Z 能撤销取消关联、重新关联）。</summary>
    private void ApplySnapshot(string text, List<(string Name, int Ordinal)> excl)
    {
        _excluded.Clear();
        _excluded.AddRange(excl);
        _isUndoRedo = true;
        try { SetPlainText(text, null); }
        finally { _isUndoRedo = false; }
    }

    private void OnEditorMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // 用鼠标位置命中提及（与悬停预览同一套 HitTestMention，比依赖点击后光标位置更可靠）
        var run = HitTestMention(e.GetPosition(_editor));
        var caret = GetCaretOffset();
        #region debug-point A:click
        Dbg($"A:click caret={caret} runs={_mentionRuns.Count} hit={(run?.Name ?? "null")}");
        #endregion
        if (run != null)
        {
            ClosePreview();
            _clickTarget = new ClickTarget(run.Start, run.End, run.HasAt, run.Name);
            _typingAt = -1;
            var items = FilterCandidates(string.Empty);
            var highlight = items.FindIndex(x => x.Name == run.Name);
            ShowPopup(items, caret, string.Empty, clickMode: true, highlightIndex: highlight);
        }
        else
        {
            _clickTarget = null;
            ClosePopup();
        }
    }

    /// <summary>选中某个候选：插入新的 @提及 或替换当前点击的提及。</summary>
    private void SelectCandidate(MentionCandidate c)
    {
        CaptureForUndo();
        var text = GetPlainText();
        #region debug-point D:select
        Dbg($"D:Select clickTg={( _clickTarget is { } ct ? $"{ct.Name}@{ct.Start}-{ct.End}" : "null")} typingAt={_typingAt} pick={c.Name}");
        #endregion
        if (_clickTarget is { } target)
        {
            var replacement = (target.HasAt ? "@" : "") + c.Name;
            #region debug-point D:select2
            Dbg($"D:Replacing [{target.Start},{target.End}) → ‘{replacement}’ oldTextLen={text.Length}");
            #endregion
            var newText = text.Remove(target.Start, target.End - target.Start).Insert(target.Start, replacement);
            SetPlainText(newText, target.Start + replacement.Length);
            _clickTarget = null;
        }
        else if (_typingAt >= 0)
        {
            var caret = GetCaretOffset();
            var end = Math.Max(_typingAt + 1, caret);
            var replacement = "@" + c.Name;
            var newText = text.Remove(_typingAt, end - _typingAt).Insert(_typingAt, replacement);
            SetPlainText(newText, _typingAt + replacement.Length);
            _typingAt = -1;
        }
        else return;
        ClosePopup();
        _editor.Focus();
    }

    private void ClosePopup()
    {
        _popup.IsOpen = false;
    }

    /// <summary>键盘焦点是否落在某 Popup 的内容子树内（下拉框/预览浮层本身）。</summary>
    private static bool IsFocusOnPopup(Popup popup)
    {
        if (popup?.Child == null) return false;
        if (Keyboard.FocusedElement is not DependencyObject fo) return false;
        for (var cur = fo; cur != null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur.Equals(popup.Child)) return true;
        }
        return false;
    }

    // ===== 悬停预览 =====

    /// <summary>命中鼠标当前位置的提及 Run（无则返回 null）。</summary>
    private MentionRun? HitTestMention(Point point)
    {
        TextPointer? tp;
        try { tp = _editor.GetPositionFromPoint(point, snapToText: true); }
        catch { return null; }
        if (tp == null) return null;
        int offset;
        try { offset = TextIndexAt(tp); }
        catch { return null; }
        return _mentionRuns.FirstOrDefault(r => r.Start < r.End && offset >= r.Start && offset <= r.End);
    }

    /// <summary>鼠标移动：命中提及则展示悬停大图预览，未命中/离开则关闭。</summary>
    private void OnEditorMouseMove(object sender, MouseEventArgs e)
    {
        if (_candidates.Count == 0 || _mentionRuns.Count == 0) { ClosePreview(); return; }
        var run = HitTestMention(e.GetPosition(_editor));
        if (run == null) { ClosePreview(); return; }
        if (ReferenceEquals(_hoveredRun?.Run, run.Run)) { EnsurePreviewImage(run); return; }
        _hoveredRun = run;
        ShowPreview(run);
    }

    /// <summary>弹出悬停预览：显示对应参考图缩略图 + 名称/来源 + 操作提示。</summary>
    private void ShowPreview(MentionRun run)
    {
        var candidate = _candidates.FirstOrDefault(c => c.Name == run.Name);
        string? path = candidate?.Path;
        bool isFrame = path != null && path.StartsWith("frame://", StringComparison.OrdinalIgnoreCase);

        BitmapImage? thumb = null;
        if (!isFrame && run.Name != null && _thumbCache.TryGetValue(run.Name, out var cached))
            thumb = cached;
        _previewImage.Source = thumb;

        var nameText = run.HasAt ? "@" + run.Name : run.Name;
        if (isFrame && path != null)
        {
            // 视频帧：无独立图片文件，仅展示来源视频与名称
            var src = path[(path.IndexOf('|') + 1)..];
            _previewCaption.Text = $"{nameText}\n来自视频片段帧 · 点击可更换或取消关联";
            if (src.Length > 0) _previewCaption.Text += $"\n{Path.GetFileName(src)}";
        }
        else
        {
            var srcName = path != null && File.Exists(path) ? Path.GetFileName(path) : "";
            _previewCaption.Text = string.IsNullOrEmpty(srcName)
                ? $"{nameText}\n点击更换映射或取消关联"
                : $"{nameText}  →  {srcName}\n点击更换映射，或选择「取消关联」还原为普通文字";
        }
        // 预览显示时隐藏编辑光标，避免光标在预览图上持续闪动（预览结束后恢复）
        if (_savedCaretBrush == null && _editor.CaretBrush != null)
            _savedCaretBrush = _editor.CaretBrush;
        if (_editor.CaretBrush != null) _editor.CaretBrush = Brushes.Transparent;
        #region debug-point E:preview
        Dbg($"E:ShowPreview name={nameText} caretBrushNull={_editor.CaretBrush == null}");
        #endregion
        _previewCard.Visibility = Visibility.Visible;
    }

    /// <summary>缩略图异步就绪后，若仍悬停原提及则补齐预览图。</summary>
    private void EnsurePreviewImage(MentionRun run)
    {
        if (_previewImage.Source != null) return;
        if (run.Name != null && _thumbCache.TryGetValue(run.Name, out var bmp))
        {
            if (bmp != null) { _previewImage.Source = bmp; _previewCard.Visibility = Visibility.Visible; }
        }
    }

    private void ClosePreview()
    {
        _previewCard.Visibility = Visibility.Collapsed;
        _hoveredRun = null;
        // 预览关闭后恢复编辑光标
        if (_savedCaretBrush != null)
        {
            try { if (_editor.CaretBrush != null) _editor.CaretBrush = _savedCaretBrush; } catch { }
            _savedCaretBrush = null;
        }
    }

    // ===== 取消映射 =====

    /// <summary>取消当前点击提及的图片映射：@提及去掉 @，裸名称仅解除关联（保持普通文字），并持久化跳过。</summary>
    private void CancelMapping()
    {
        var text = GetPlainText();
        if (_clickTarget is not { } target) { ClosePopup(); return; }
        CaptureForUndo();

        string newText;
        int caret;
        if (target.HasAt)
        {
            newText = text.Remove(target.Start, 1);   // 去掉 '@'，保留名称本身
            caret = target.Start;
            AddExclusion(target.Name, CountBareOccurrencesBefore(newText, target.Name, target.Start));
        }
        else
        {
            newText = text;                          // 文本不变，仅解除高亮映射
            caret = target.Start;
            AddExclusion(target.Name, CountBareOccurrencesBefore(text, target.Name, target.Start));
        }

        _clickTarget = null;
        _typingAt = -1;
        SetPlainText(newText, caret);
        ClosePopup();
        _editor.Focus();
    }

    private void AddExclusion(string name, int bareOrdinal)
    {
        foreach (var (n, o) in _excluded)
            if (n == name && o == bareOrdinal) return;
        _excluded.Add((name, bareOrdinal));
    }

    /// <summary>「取消关联」下拉项 UI。</summary>
    private static UIElement BuildCancelRow()
    {
        var mark = new TextBlock
        {
            Text = "✕", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x6A, 0x6A)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 8, 0)
        };
        var label = new TextBlock
        {
            Text = "取消关联（还原为普通文字）", FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEE)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var dock = new DockPanel { MinHeight = 24 };
        dock.Children.Add(mark);
        dock.Children.Add(label);
        return dock;
    }

    private void UpdateWatermark()
    {
        _watermark.Visibility = string.IsNullOrEmpty(GetPlainText())
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ===== 工具 =====

    #region debug-point T:log-helper
    /// <summary>临时运行时日志（写入项目根目录文件，供本轮 bug 排查使用，修复后移除）。</summary>
    internal static void Dbg(string msg) => System.IO.File.AppendAllText(
        System.IO.Path.Combine(App.WorkRoot, "debug-prompt-mention-flash.log"),
        $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
    #endregion

    private static BitmapImage? DecodeImage(string path, int decodePixelWidth)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = decodePixelWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static Brush Brush(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is Brush b ? b : new SolidColorBrush(fallback);

    // ===== 内部数据结构 =====

    private sealed class MentionCandidate
    {
        public string Name;
        public string Path;
        public bool IsCancel;
        public MentionCandidate(string name, string path) { Name = name; Path = path; }

        /// <summary>下拉框中的「取消关联」占位项。</summary>
        public static readonly MentionCandidate CancelMarker = new("", "") { IsCancel = true };
    }

    private sealed class MentionRun
    {
        public Run Run;
        public string Name;
        public bool HasAt;
        public int Start;
        public int End;
        public MentionRun(Run run, string name, bool hasAt, int start, int end)
        {
            Run = run; Name = name; HasAt = hasAt; Start = start; End = end;
        }
    }

    private sealed class MentionRange
    {
        public int Start;
        public int End;
        public string Name;
        public bool HasAt;
        public MentionRange(int start, int end, string name, bool hasAt)
        {
            Start = start; End = end; Name = name; HasAt = hasAt;
        }
    }

    private sealed class ClickTarget
    {
        public int Start;
        public int End;
        public bool HasAt;
        public string Name;
        public ClickTarget(int start, int end, bool hasAt, string name)
        {
            Start = start; End = end; HasAt = hasAt; Name = name;
        }
    }
}
