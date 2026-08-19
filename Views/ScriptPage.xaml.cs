using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Documents;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

public partial class ScriptPage : UserControl
{
    private List<NovelInfo> _novels = new();
    private List<Chapter> _chapters = new();
    private NovelInfo? _currentNovel;
    private Chapter? _currentChapter;
    private bool _isOriginalExpanded = true;
    private bool _isScriptExpanded = true;
    private bool _isImageExpanded = true;
    private double _savedOriginalWidth;
    private double _savedScriptWidth;
    private double _savedImageWidth;
    private DispatcherTimer? _autoSaveTimer;
    private static string _lastImageRatio = "16:9";
    private static string _lastImageLevel = "1K";
    private bool _multiSelectMode;
    private readonly HashSet<string> _selectedFiles = new();
    private string _scriptText = "";
    private string _promptText = "";
    private static string? _lastNovelId;
    private static int _lastChapterIdx = -1;
    private bool _contentDirty;
    private static readonly ConcurrentDictionary<string, BitmapSource> _imageCache = new();
    // 小说封面缓存：key 为 "novelId|lastWriteTime"，避免每次进入页面重复读盘解码封面
    private static readonly ConcurrentDictionary<string, BitmapSource> _coverCache = new();

    // ===== 撤销/重做 + 历史记录 =====
    private readonly TextHistoryService _history = TextHistoryService.Instance;
    private DispatcherTimer? _historyMergeTimer;
    private bool _historySuspend;          // 程序回填文本时挂起历史采集，避免撤销本身再产生记录
    private string _historyPendingKey = ""; // 待提交变动的编辑目标 key

    public ScriptPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ChapterPopup.Closed += (_, _) => _chapterPopupOpen = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _autoSaveTimer?.Stop();
        // 页面卸载前提交未完成的历史变动并强制保存
        FlushHistory();
        ForceSave();
    }

    /// <summary>强制保存当前内容（不受 AutoSave 开关限制，供 MainWindow 主题切换前调用）</summary>
    public void ForceSave()
    {
        if (_currentNovel == null || _currentChapter == null) return;
        try
        {
            // 先将 TextBox 中的实时编辑内容同步到字段
            if (_isScriptMode)
                _scriptText = ScriptEditBox.Text;
            else
                _promptText = ScriptEditBox.Text;

            _currentChapter.ScriptContent = _scriptText;
            _currentChapter.ScriptPrompt = _promptText;
            // 保存小说原文（Base64 编码的 Xaml 格式，保留标红等富文本）
            var range = new TextRange(OriginalTextBox.Document.ContentStart, OriginalTextBox.Document.ContentEnd);
            using var ms = new MemoryStream();
            range.Save(ms, DataFormats.Xaml);
            var b64 = Convert.ToBase64String(ms.ToArray());
            _currentChapter.OriginalContent = "$X:" + b64;
            FileService.SaveChapters(App.WorkRoot, _currentNovel.Id, _chapters);
        }
        catch (Exception ex)
        {
            // 保存失败不再静默：记录错误日志，便于用户排查数据丢失原因
            try
            {
                File.AppendAllText(Path.Combine(App.WorkRoot, "error.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 章节保存失败: {ex}\n");
            }
            catch { }
        }
    }

    /// <summary>更新编辑框文本（剧本/提示词切换时）</summary>
    private void UpdateScriptEditor()
    {
        ScriptEditBox.Text = _isScriptMode ? _scriptText : _promptText;
    }

    /// <summary>从设置页面同步字体大小到编辑器</summary>
    public void ApplyFontSize(int fontSize)
    {
        ScriptEditBox.FontSize = fontSize;
        OriginalTextBox.FontSize = fontSize;
        // 改字号后重新统一小说内容中的内联字体
        NormalizeOriginalTextFormat();
    }

    /// <summary>统一小说内容格式：清除内联 Margin / Foreground，统一 FontFamily/FontSize（保留背景高亮标记）</summary>
    private void NormalizeOriginalTextFormat()
    {
        // 不检查 IsLoaded：页面切到后台时 FlowDocument 仍可安全修改，保证设置页调整字号实时生效
        if (OriginalTextBox.Document == null) return;
        var doc = OriginalTextBox.Document;
        var defaultFontSize = OriginalTextBox.FontSize;
        var defaultFontFamily = OriginalTextBox.FontFamily;
        var defaultForeground = OriginalTextBox.Foreground;

        // 1. 统一 FlowDocument 自身的默认字体和前景色
        doc.FontFamily = defaultFontFamily;
        doc.FontSize = defaultFontSize;
        doc.Foreground = defaultForeground;

        // 2. 递归遍历所有段落和内联元素，统一字体和前景色（保留 Background 高亮标记）
        foreach (var block in doc.Blocks)
        {
            if (block is Paragraph para)
            {
                para.Margin = new Thickness(0);
                para.FontFamily = defaultFontFamily;
                para.FontSize = defaultFontSize;
                para.Foreground = defaultForeground;
                NormalizeInlineFormat(para.Inlines, defaultFontSize, defaultFontFamily, defaultForeground);
            }
        }
    }

    /// <summary>递归遍历 InlineCollection，统一 FontFamily / FontSize / Foreground（保留 Background 高亮标记）</summary>
    private static void NormalizeInlineFormat(InlineCollection inlines, double defaultFontSize, FontFamily defaultFontFamily, Brush defaultForeground)
    {
        var items = inlines.ToList();
        foreach (var inline in items)
        {
            inline.FontFamily = defaultFontFamily;
            inline.FontSize = defaultFontSize;
            inline.Foreground = defaultForeground; // 显式设为控件当前主题色（null 在 WPF 中会导致不可见）

            if (inline is Span span)
                NormalizeInlineFormat(span.Inlines, defaultFontSize, defaultFontFamily, defaultForeground);
        }
    }

    /// <summary>文件还原后刷新当前章节的图像网格</summary>
    public void RefreshContent()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_currentNovel != null && _currentChapter != null)
                RefreshImageGrid();
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private bool _loaded;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 每次进入页面都应用最新字体配置（支持设置页实时调整字号后切回即时生效）
        try
        {
            var config = FileService.LoadConfig(App.WorkRoot);
            var fontSize = config.FontSize;
            ScriptEditBox.FontSize = fontSize;
            OriginalTextBox.FontSize = fontSize;
            NormalizeOriginalTextFormat();
        }
        catch { }

        if (_loaded) return;
        _loaded = true;

        // 延迟到渲染后再加载小说列表，避免阻塞页面首次显示（封面/图片均已异步解码）
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                RefreshNovelList();
            }
            catch { /* 初始化静默失败 */ }
        }), DispatcherPriority.Loaded);

        _savedOriginalWidth = 300;
        _savedScriptWidth = 300;
        _savedImageWidth = 300;

        FixRichTextBoxCarets();

        // 自动保存定时器（输入停止 2 秒后保存）
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            SaveCurrentContent();
        };

        // 历史合并定时器：输入停顿 1.5 秒后把连续编辑合并为一次变动（类 Word 行为）
        _historyMergeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _historyMergeTimer.Tick += (_, _) =>
        {
            _historyMergeTimer.Stop();
            CommitPendingHistory();
        };

        ScriptEditBox.TextChanged += (_, _) =>
        {
            _contentDirty = true;
            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Stop();
                _autoSaveTimer.Start();
            }
            OnEditorTextChanged(isScript: true);
        };
        OriginalTextBox.TextChanged += (_, _) =>
        {
            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Stop();
                _autoSaveTimer.Start();
            }
            OnEditorTextChanged(isScript: false);
        };
    }

    /// <summary>修复暗色模式下 RichTextBox 光标不可见问题（WPF RichTextBox 无 CaretBrush 属性）</summary>
    public void FixRichTextBoxCarets()
    {
        FixCaret(OriginalTextBox);
    }

    private static void FixCaret(RichTextBox rtb)
    {
        rtb.GotFocus += (_, _) =>
        {
            rtb.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                try
                {
                    // 在键盘焦点进入后，将光标颜色设为当前前景色
                    var brush = rtb.Foreground;
                    // 通过视觉树查找 CaretElement 并设 Background
                    SetCaretBackground(rtb, brush);
                }
                catch { }
            });
        };
    }

    private static void SetCaretBackground(DependencyObject parent, Brush brush)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child.GetType().Name == "CaretElement" && child is Control caret)
            {
                caret.Background = brush;
                return;
            }
            SetCaretBackground(child, brush);
        }
    }

    // ===== 撤销/重做 + 历史记录采集 =====

    /// <summary>构造当前编辑目标的唯一标识（小说ID|章节索引|字段）</summary>
    private string GetHistoryKey(bool isScript)
    {
        string novelId = _currentNovel?.Id ?? "unknown";
        string chapterId = _currentChapter?.Index.ToString() ?? "unknown";
        // isScript=true 表示剧本/提示词模式（同一编辑框，需进一步区分模式）；false 表示小说原文
        string field = isScript ? (_isScriptMode ? "script" : "prompt") : "original";
        return $"{novelId}|{chapterId}|{field}";
    }

    /// <summary>文本变化时调用：启动停顿合并计时器，等待输入暂停后提交一次变动</summary>
    private void OnEditorTextChanged(bool isScript)
    {
        if (_historySuspend || _currentChapter == null) return;

        string key = GetHistoryKey(isScript);
        // 记录本次编辑的起点文本（由服务内部管理）；此处仅在首次触发时 BeginEdit
        _historyPendingKey = key;

        // 重启合并定时器：连续输入会不断推迟提交，停顿后才提交
        _historyMergeTimer?.Stop();
        _historyMergeTimer?.Start();
    }

    /// <summary>提交一次文本变动到历史服务（停顿合并后调用）</summary>
    private void CommitPendingHistory()
    {
        if (_historySuspend || _currentChapter == null || string.IsNullOrEmpty(_historyPendingKey)) return;

        string key = _historyPendingKey;
        string after = GetCurrentTextByKey(key);
        _history.CommitEdit(key, after);
        _historyPendingKey = "";
    }

    /// <summary>根据 key 获取当前编辑框文本</summary>
    private string GetCurrentTextByKey(string key)
    {
        // key 格式：novelId|chapterId|field
        var parts = key.Split('|');
        string field = parts.Length >= 3 ? parts[2] : "";
        return field switch
        {
            "script" => _isScriptMode ? ScriptEditBox.Text : _scriptText,
            "prompt" => _isScriptMode ? _promptText : ScriptEditBox.Text,
            "original" => GetOriginalText(),
            _ => ScriptEditBox.Text
        };
    }

    /// <summary>获取小说原文纯文本</summary>
    private string GetOriginalText()
    {
        try
        {
            return new TextRange(OriginalTextBox.Document.ContentStart, OriginalTextBox.Document.ContentEnd).Text;
        }
        catch { return ""; }
    }

    /// <summary>在切换章节/模式前，强制提交未完成的变动</summary>
    private void FlushHistory()
    {
        _historyMergeTimer?.Stop();
        CommitPendingHistory();
    }

    /// <summary>设置历史采集挂起状态（程序回填文本时暂停，避免撤销产生新记录）</summary>
    private void SetHistorySuspend(bool suspend)
    {
        _historySuspend = suspend;
        if (suspend) _historyMergeTimer?.Stop();
    }

    /// <summary>执行撤销（供全局按钮/快捷键调用）</summary>
    public void UndoCurrentEditor()
    {
        if (_currentChapter == null) return;
        // 优先针对当前焦点编辑框
        string key = GetFocusedEditorKey();
        if (string.IsNullOrEmpty(key)) return;

        string? result = _history.Undo(key);
        if (result == null) return;

        ApplyTextByKey(key, result);
        _contentDirty = true;
    }

    /// <summary>执行重做（供全局按钮/快捷键调用）</summary>
    public void RedoCurrentEditor()
    {
        if (_currentChapter == null) return;
        string key = GetFocusedEditorKey();
        if (string.IsNullOrEmpty(key)) return;

        string? result = _history.Redo(key);
        if (result == null) return;

        ApplyTextByKey(key, result);
        _contentDirty = true;
    }

    /// <summary>判断当前是否可撤销/重做（供全局按钮刷新状态）</summary>
    public (bool canUndo, bool canRedo) GetHistoryState()
    {
        string key = GetFocusedEditorKey();
        if (string.IsNullOrEmpty(key)) return (false, false);
        return (_history.CanUndo, _history.CanRedo);
    }

    /// <summary>获取当前焦点所在编辑框的 key（无焦点时默认剧本编辑框）</summary>
    private string GetFocusedEditorKey()
    {
        if (OriginalTextBox.IsKeyboardFocused || OriginalTextBox.IsFocused)
            return GetHistoryKey(isScript: false);
        return GetHistoryKey(isScript: true);
    }

    /// <summary>
    /// 从历史窗口回退：将指定编辑目标的文本恢复为历史快照内容。
    /// 供 TextHistoryWindow 调用。
    /// </summary>
    public void RestoreHistorySnapshot(string key, string content)
    {
        if (string.IsNullOrEmpty(key)) return;

        // 定位到对应章节（key 格式：novelId|chapterId|field）
        var parts = key.Split('|');
        if (parts.Length < 3) return;
        string novelId = parts[0];
        string chapterId = parts[1];

        // 若当前未选中对应章节，先切换过去
        if (_currentNovel == null || _currentNovel.Id != novelId)
        {
            var novel = _novels.FirstOrDefault(n => n.Id == novelId);
            if (novel != null) SelectNovel(novel);
            else return;
        }
        if (_currentChapter == null || _currentChapter.Index.ToString() != chapterId)
        {
            var chapter = _chapters.FirstOrDefault(c => c.Index.ToString() == chapterId);
            if (chapter != null) SelectChapter(chapter);
            else return;
        }

        // 回填文本
        ApplyTextByKey(key, content);

        // 更新历史服务中该 key 的已知文本，保证后续撤销/重做基于回退后的状态
        _history.RegisterBaseline(key, content);
    }

    /// <summary>根据 key 回填文本（撤销/重做/历史回退共用）</summary>
    private void ApplyTextByKey(string key, string text)
    {
        var parts = key.Split('|');
        string field = parts.Length >= 3 ? parts[2] : "";
        SetHistorySuspend(true);
        try
        {
            switch (field)
            {
                case "script":
                    _scriptText = text;
                    if (_isScriptMode) ScriptEditBox.Text = text;
                    _currentChapter!.ScriptContent = text;
                    break;
                case "prompt":
                    _promptText = text;
                    if (!_isScriptMode) ScriptEditBox.Text = text;
                    _currentChapter!.ScriptPrompt = text;
                    break;
                case "original":
                    var range = new TextRange(OriginalTextBox.Document.ContentStart, OriginalTextBox.Document.ContentEnd);
                    range.Text = text;
                    break;
            }
        }
        finally
        {
            SetHistorySuspend(false);
        }
        // 回填后触发保存
        SaveCurrentContent();
    }

    // ===== 小说列表 =====
    private void RefreshNovelList()
    {
        _novels = FileService.LoadAllNovels(App.WorkRoot);
        NovelListPanel.Children.Clear();

        foreach (var novel in _novels)
        {
            var card = CreateNovelCard(novel);
            NovelListPanel.Children.Add(card);
        }

        if (_novels.Count > 0 && _currentNovel == null)
        {
            // 主题切换等页面重建时，恢复上次选中的小说和章节
            if (_lastNovelId != null)
            {
                var prevNovel = _novels.Find(n => n.Id == _lastNovelId);
                if (prevNovel != null)
                {
                    SelectNovel(prevNovel);
                    if (_lastChapterIdx >= 0 && _lastChapterIdx < _chapters.Count)
                        SelectChapter(_chapters[_lastChapterIdx]);
                    return;
                }
            }
            SelectNovel(_novels[0]);
        }
    }

    private Border CreateNovelCard(NovelInfo novel)
    {
        var border = new Border
        {
            Style = (Style)FindResource("ListItemCardStyle"),
            Cursor = Cursors.Hand,
            Tag = novel,
            Width = 110
        };

        var stack = new StackPanel();

        // 封面
        var coverBorder = new Border
        {
            Width = 80, Height = 110,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 6)
        };

        // 封面：有封面图时先用 CoverColor 作为加载占位色，加载完成后由 LoadNovelCoverAsync
// 清空 Background 以保留原图（PNG）透明区域真实透明；无封面图时才永久用 CoverColor
        if (novel.HasCoverImage)
        {
            coverBorder.Background = ViewHelpers.ParseColor(novel.CoverColor);
            LoadNovelCoverAsync(novel, coverBorder);
        }
        else
        {
            coverBorder.Background = ViewHelpers.ParseColor(novel.CoverColor);
            coverBorder.Child = new TextBlock
            {
                Text = novel.Name.Length > 0 ? novel.Name[..Math.Min(2, novel.Name.Length)] : "书",
                Foreground = Brushes.White,
                FontSize = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        stack.Children.Add(coverBorder);

        // 书名
        stack.Children.Add(new TextBlock
        {
            Text = novel.Name,
            FontSize = 11,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 120
        });

        border.Child = stack;
        border.MouseLeftButtonDown += (s, e) => SelectNovel(novel);
        border.MouseRightButtonDown += (s, e) =>
        {
            var menu = new ContextMenu();
            var coverItem = new MenuItem { Header = "修改封面" };
            coverItem.Click += (_, _) => ChangeNovelCover(novel);
            menu.Items.Add(coverItem);
            var renameItem = new MenuItem { Header = "重命名" };
            renameItem.Click += (_, _) => RenameNovel(novel);
            menu.Items.Add(renameItem);
            var delItem = new MenuItem { Header = "删除小说" };
            delItem.Click += (_, _) => DeleteNovel(novel);
            menu.Items.Add(delItem);
            menu.IsOpen = true;
        };
        return border;
    }

    /// <summary>
    /// 异步加载小说封面：优先命中缓存，否则后台读盘解码后回填。
    /// 封面加载不再阻塞 UI 线程，大幅降低进入剧本页的卡顿。
    /// </summary>
    private void LoadNovelCoverAsync(NovelInfo novel, Border coverBorder)
    {
        var coverFile = FileService.NovelCoverFile(App.WorkRoot, novel.Id);
        string cacheKey;
        try
        {
            var fi = new FileInfo(coverFile);
            // 以文件最后修改时间作为缓存键，封面更新后能自动失效
            cacheKey = $"{novel.Id}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch { cacheKey = $"{novel.Id}|0"; }

        // 命中缓存：立即同步回填，并清空 coverBorder 的 Background（保留原图透明区域）
        if (_coverCache.TryGetValue(cacheKey, out var cached))
        {
            coverBorder.Background = Brushes.Transparent;
            coverBorder.Child = new Image { Source = cached, Stretch = Stretch.UniformToFill };
            return;
        }

        // 未命中：后台加载，完成后切回 UI 线程回填
        Task.Run(() =>
        {
            try
            {
                var data = File.ReadAllBytes(coverFile);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                using var ms = new MemoryStream(data);
                bmp.StreamSource = ms;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 160; // 封面仅 80x110 显示，降采样减少解码开销
                bmp.EndInit();
                bmp.Freeze();
                _coverCache[cacheKey] = bmp;
                return (BitmapSource)bmp;
            }
            catch { return null; }
        }).ContinueWith(t =>
        {
            if (t.Result == null) return;
            Dispatcher.BeginInvoke(() =>
            {
                // 卡片可能已被重建，检查控件是否仍挂在视觉树上
                if (coverBorder.IsLoaded || coverBorder.Parent != null)
                {
                    // 清空 coverBorder 的 Background，保留原图（PNG）透明区域真实透明
                    coverBorder.Background = Brushes.Transparent;
                    coverBorder.Child = new Image { Source = t.Result, Stretch = Stretch.UniformToFill };
                }
            });
        }, TaskScheduler.Default);
    }

    private void RenameNovel(NovelInfo novel)
    {
        var dialog = new InputDialog("重命名小说", "请输入新的小说名称：", novel.Name);
        dialog.Owner = Window.GetWindow(this);
        dialog.Confirmed += name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var oldFolder = novel.MediaFolder;
            novel.Name = EnsureUniqueNovelName(name.Trim(), novel.Id);
            novel.MediaFolder = FileService.GenerateUniqueMediaFolder(App.WorkRoot, novel.Name, novel.Id);
            FileService.SaveNovelInfo(App.WorkRoot, novel);
            FileService.MoveNovelMediaFolders(App.WorkRoot, oldFolder, novel.MediaFolder, novel.Id);
            RefreshNovelList();
            NotifyNovelsChanged();
        };
        dialog.Show();
    }

    private void DeleteNovel(NovelInfo novel)
    {
        if (!MessageDialog.Confirm("删除小说",
            $"确定要删除《{novel.Name}》吗？\n\n小说将移至回收站，可在回收站中恢复。")) return;
        try
        {
            FileService.MoveToTrash(FileService.NovelPath(App.WorkRoot, novel.Id));
            bool wasCurrent = _currentNovel?.Id == novel.Id;
            if (wasCurrent)
            { _currentNovel = null; _currentChapter = null; _chapters.Clear(); }
            RefreshNovelList();
            // 如果还有其他小说，RefreshNovelList 已自动选中第一部；只在无小说时清空 UI
            if (_currentNovel == null)
            {
                ChapterTabsPanel.Children.Clear();
                try { OriginalTextBox.Document.Blocks.Clear(); } catch { }
                _scriptText = ""; _promptText = ""; UpdateScriptEditor();
                ImageGrid.Children.Clear();
            }
        }
        catch (Exception ex)
        {
            MessageDialog.Show("错误", $"删除失败：{ex.Message}");
            return;
        }
        NotifyNovelsChanged();
    }

    /// <summary>通知其他页面小说列表已变更（人物素材、视频文件需刷新）</summary>
    private static void NotifyNovelsChanged()
    {
        NavigationService.Instance.ClearPage("Character");
        NavigationService.Instance.ClearPage("Video");
        NavigationService.Instance.ClearPage("Audio");
    }

    private void ChangeNovelCover(NovelInfo novel)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp",
            Title = "选择封面图片"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var cw = new CropWindow(dlg.FileName, square: false)
            { Owner = Window.GetWindow(this), Title = "裁剪封面" };
            cw.Cropped += img =>
            {
                var novelDir = FileService.NovelPath(App.WorkRoot, novel.Id);
                FileService.EnsureDirectory(novelDir);
                var destPath = FileService.NovelCoverFile(App.WorkRoot, novel.Id);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                enc.Save(fs);
                fs.Flush(true);
                novel.HasCoverImage = true;
                FileService.SaveNovelInfo(App.WorkRoot, novel);
                var frozen = img.Clone();
                frozen.Freeze();
                UpdateNovelCardCover(novel.Id, frozen);
                if (_currentNovel?.Id == novel.Id)
                    _currentNovel.HasCoverImage = true;
                // 清除人物素材、视频文件和音频文件页面缓存，确保下次访问时重新加载封面
                var nav = NavigationService.Instance;
                nav.ClearPage("Character");
                nav.ClearPage("Video");
                nav.ClearPage("Audio");
                // 如果用户当前正在这些页面，刷新内容
                var currentPage = nav.CurrentPageName;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (currentPage == "Character" && nav.CurrentPage is CharacterPage cp)
                        cp.RefreshContent();
                    else if (currentPage == "Video" && nav.CurrentPage is VideoPage vp)
                        vp.RefreshContent();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            };
            cw.Show();
        }
        catch (Exception ex)
        {
            MessageDialog.Show("错误", $"设置封皮失败：{ex.Message}");
        }
    }

    /// <summary>直接更新小说列表中指定小说的封面（不重建列表）</summary>
    private void UpdateNovelCardCover(string novelId, BitmapSource source)
    {
        foreach (Border card in NovelListPanel.Children)
        {
            if (card.Tag is NovelInfo ni && ni.Id == novelId)
            {
                // 卡片结构：Border > StackPanel > [0] Border(cover)
                if (card.Child is StackPanel sp && sp.Children.Count > 0 &&
                    sp.Children[0] is Border cover)
                {
                    cover.Background = null;
                    cover.Child = new Image { Source = source, Stretch = Stretch.UniformToFill };
                }
                break;
            }
        }
    }

    private void SelectNovel(NovelInfo novel)
    {
        _currentNovel = novel;
        _currentChapter = null;
        foreach (Border child in NovelListPanel.Children)
        {
            if (child.Tag is NovelInfo ni)
                child.BorderBrush = ni.Id == novel.Id
                    ? (Brush)FindResource("PrimaryBrush")
                    : (Brush)FindResource("BorderBrush");
        }
        LoadChapters();
    }

    private void LoadChapters()
    {
        if (_currentNovel == null) return;
        _chapters = FileService.LoadChapters(App.WorkRoot, _currentNovel.Id);
        var originalFile = FileService.NovelOriginalFile(App.WorkRoot, _currentNovel.Id);
        if (_chapters.Count == 0 && File.Exists(originalFile))
        {
            _chapters = ChapterParserService.ParseNovel(originalFile);
            FileService.SaveChapters(App.WorkRoot, _currentNovel.Id, _chapters);
        }
        RefreshChapterTabs();
        if (_chapters.Count > 0)
        {
            SelectChapter(_chapters[0]);
        }
        else
        {
            _currentChapter = null;
            try { OriginalTextBox.Document.Blocks.Clear(); } catch { }
            _scriptText = ""; _promptText = ""; UpdateScriptEditor();
            ImageGrid.Children.Clear();
            ImageGrid.RowDefinitions.Clear();
            ImageGrid.ColumnDefinitions.Clear();
            ImageGrid.RowDefinitions.Add(new RowDefinition());
            ImageGrid.ColumnDefinitions.Add(new ColumnDefinition());
            ImageGrid.Children.Add(new TextBlock
            {
                Text = "暂无章节\n请先导入小说或新建章节",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12, TextAlignment = TextAlignment.Center
            });
        }
    }

    // ===== 章节导航栏 =====
    private void ChapterTabsScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
            ChapterTabsScroller.ScrollToHorizontalOffset(ChapterTabsScroller.HorizontalOffset - 60);
        else
            ChapterTabsScroller.ScrollToHorizontalOffset(ChapterTabsScroller.HorizontalOffset + 60);
        e.Handled = true;
    }

    private bool _chapterPopupOpen;

    private void ChapterExpandBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_chapterPopupOpen)
        {
            ChapterPopup.IsOpen = false;
            return;
        }

        ChapterPopupList.Children.Clear();
        var displayOrder = _chapters
            .OrderBy(c => c.IsCompleted ? 1 : 0)
            .ThenBy(c => c.Index)
            .ToList();

        // 弹窗宽度延伸到右侧「书籍列表」面板左边界
        double leftEdge = ChapterExpandBtn.TranslatePoint(new Point(0, 0), this).X;
        double rightEdge = ActualWidth - NovelListCol.ActualWidth - 16;
        double popupWidth = Math.Max(400, rightEdge - leftEdge + 40);
        ChapterPopupBorder.MaxWidth = popupWidth;
        ChapterPopupBorder.MinWidth = Math.Min(400, popupWidth);

        foreach (var ch in displayOrder)
        {
            bool isSelected = ch == _currentChapter;
            var btn = new Button
            {
                Content = ch.DisplayName,
                Tag = ch,
                Style = (Style)FindResource("SecondaryButtonStyle"),
                Margin = new Thickness(2),
                FontSize = 11,
                Padding = new Thickness(8, 5, 8, 5),
                Background = isSelected ? (Brush)FindResource("PrimaryBrush") : null,
                Foreground = isSelected ? Brushes.White : (Brush)FindResource("TextPrimaryBrush")
            };
            btn.Click += (s, _) =>
            {
                _chapterPopupOpen = false;
                ChapterPopup.IsOpen = false;
                SelectChapter(ch);
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Loaded,
                    () => ScrollTabToChapter(ch));
            };
            ChapterPopupList.Children.Add(btn);
        }
        _chapterPopupOpen = true;
        ChapterPopup.IsOpen = true;
    }

    /// <summary>
    /// 将顶部章节 Tab 条横向滚动到指定章节按钮可见
    /// </summary>
    private void ScrollTabToChapter(Chapter chapter)
    {
        var tabBtn = ChapterTabsPanel.Children
            .OfType<Button>()
            .FirstOrDefault(b => b.Tag is Chapter ch && ch == chapter);
        tabBtn?.BringIntoView();
    }

    private void RefreshChapterTabs()
    {
        ChapterTabsPanel.Children.Clear();
        var displayOrder = _chapters
            .OrderBy(c => c.IsCompleted ? 1 : 0)
            .ThenBy(c => c.Index)
            .ToList();

        foreach (var ch in displayOrder)
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            if (ch.IsCompleted)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "\u2713 ",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("SuccessBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            stack.Children.Add(new TextBlock
            {
                Text = $"第{ch.Index}章：{ch.Title}",
                FontSize = 12,
                Foreground = ch.IsCompleted ? (Brush)FindResource("TextSecondaryBrush") : (Brush)FindResource("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });

            var btn = new Button
            {
                Content = stack,
                Tag = ch,
                Style = (Style)FindResource("SecondaryButtonStyle"),
                Margin = new Thickness(2, 0, 2, 0),
                Padding = new Thickness(12, 4, 12, 4),
                Opacity = ch.IsCompleted ? 0.6 : 1.0
            };
            btn.Click += (s, e) => SelectChapter(ch);
            btn.MouseRightButtonDown += (s, e) =>
            {
                var menu = new ContextMenu();
                var header = ch.IsCompleted ? "取消完成" : "标记完成";
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => ToggleChapterComplete(ch);
                menu.Items.Add(item);
                var delItem = new MenuItem { Header = "删除章节", Foreground = (Brush)FindResource("DangerBrush") };
                delItem.Click += (_, _) => DeleteChapter(ch);
                menu.Items.Add(delItem);
                menu.IsOpen = true;
            };
            ChapterTabsPanel.Children.Add(btn);
        }

        var addBtn = new Button
        {
            Content = "+",
            Style = (Style)FindResource("SecondaryButtonStyle"),
            Width = 36, Height = 30, Margin = new Thickness(4, 0, 0, 0),
            FontSize = 18, FontWeight = FontWeights.Bold, Padding = new Thickness(0)
        };
        addBtn.Click += AddChapter_Click;
        ChapterTabsPanel.Children.Add(addBtn);
    }

    private void ToggleChapterComplete(Chapter chapter)
    {
        chapter.IsCompleted = !chapter.IsCompleted;
        FileService.SaveChapters(App.WorkRoot, _currentNovel!.Id, _chapters);
        RefreshChapterTabs();
    }

    private void SelectChapter(Chapter chapter)
    {
        // 切换章节前先保存当前编辑内容，并提交未完成的历史变动
        if (_currentChapter != null && _currentChapter != chapter)
        {
            FlushHistory();
            if (_isScriptMode) _scriptText = ScriptEditBox.Text;
            else _promptText = ScriptEditBox.Text;
            SaveCurrentContent();
        }
        _currentChapter = chapter;
        // 记住当前选中状态，供主题切换后恢复
        _lastNovelId = _currentNovel?.Id;
        _lastChapterIdx = _chapters.IndexOf(chapter);
        foreach (Button btn in ChapterTabsPanel.Children.OfType<Button>())
        {
            if (btn.Tag is Chapter ch)
            {
                btn.Background = ch == chapter ? (Brush)FindResource("PrimaryBrush") : Brushes.Transparent;
                btn.Foreground = ch == chapter ? Brushes.White : (Brush)FindResource("TextPrimaryBrush");
            }
        }
        UpdateContent();
    }

    private void UpdateContent()
    {
        if (_currentChapter == null) return;
        try
        {
            // 加载小说原文（RichTextBox）
            var textRange = new TextRange(OriginalTextBox.Document.ContentStart, OriginalTextBox.Document.ContentEnd);
            var content = _currentChapter.OriginalContent ?? "";
            if (!string.IsNullOrEmpty(content))
            {
                try
                {
                    if (content.StartsWith("$X:"))
                    {
                        // Base64 编码的 Xaml 格式（保留标红等富文本）
                        var xaml = Encoding.UTF8.GetString(Convert.FromBase64String(content.Substring(3)));
                        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xaml));
                        textRange.Load(ms, DataFormats.Xaml);
                    }
                    else if (content.StartsWith("<Section"))
                    {
                        // 旧版纯 Xaml 格式
                        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
                        textRange.Load(ms, DataFormats.Xaml);
                    }
                    else if (IsLikelyBinaryGarbage(content))
                    {
                        textRange.Text = "";
                    }
                    else
                    {
                        textRange.Text = content;
                    }
                }
                catch { textRange.Text = ""; }
            }

            // 统一小说内容格式（清除内联 Margin + 字号统一为控件默认值）
            NormalizeOriginalTextFormat();
        }
        catch { }

        _contentDirty = false;
        _scriptText = _currentChapter.ScriptContent ?? "";
        _promptText = _currentChapter.ScriptPrompt ?? "";
        UpdateScriptEditor();
        RefreshImageGrid();

        // 注册历史基准文本（作为第一次编辑的 before）
        if (_currentNovel != null && _currentChapter != null)
        {
            string novelId = _currentNovel.Id;
            string chapterId = _currentChapter.Index.ToString();
            _history.RegisterBaseline($"{novelId}|{chapterId}|script", _scriptText);
            _history.RegisterBaseline($"{novelId}|{chapterId}|prompt", _promptText);
            _history.RegisterBaseline($"{novelId}|{chapterId}|original", GetOriginalText());
        }
    }

    // ===== 剧本/提示词切换 =====
    private bool _isScriptMode = true;
    private bool _toggling;

    private void ToggleScriptMode()
    {
        if (_toggling || _currentChapter == null) return;
        _toggling = true;

        // 切换前提交未完成的历史变动，并同步字段
        FlushHistory();
        if (_isScriptMode)
        {
            _scriptText = ScriptEditBox.Text;
            _currentChapter.ScriptContent = _scriptText;
        }
        else
        {
            _promptText = ScriptEditBox.Text;
            _currentChapter.ScriptPrompt = _promptText;
        }

        // 翻转动画：缩小 → 切换内容 → 放大
        var shrinkAnim = new System.Windows.Media.Animation.DoubleAnimation(
            1.0, 0.0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
        };

        shrinkAnim.Completed += (_, _) =>
        {
            // 切换内容
            _isScriptMode = !_isScriptMode;
            if (_isScriptMode)
            {
                ScriptModeIcon.Text = "\uE70B";
                ScriptModeLabel.Text = "剧本内容";
            }
            else
            {
                ScriptModeIcon.Text = "\uE943";
                ScriptModeLabel.Text = "创造提示词";
            }
            UpdateScriptEditor();

            // 放大动画
            var growAnim = new System.Windows.Media.Animation.DoubleAnimation(
                0.0, 1.0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };

            growAnim.Completed += (_, _) => _toggling = false;
            ScriptPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, growAnim);
        };

        ScriptPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkAnim);
    }

    // ===== 文本区双击切换剧本/提示词模式 =====
    private void ScriptEditBox_PreviewMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ToggleScriptMode();
        e.Handled = true;
    }

    // ===== AI 模型生成 =====
    private CancellationTokenSource? _aiCts;

    private async void AiGenerateScript_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null || _currentChapter == null) return;

        // 如果正在生成中，点击变为取消
        if (_aiCts != null)
        {
            _aiCts.Cancel();
            return;
        }

        var config = FileService.LoadConfig(App.WorkRoot);
        if (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(config.ApiEndpoint))
        {
            ShowCopyToast("⚠ 请先在「设置→AI 模型配置」中填入 API 地址和密钥");
            return;
        }

        var isPromptMode = !_isScriptMode;
        var targetName = isPromptMode ? "创作提示词" : "剧本内容";

        // 准备提示词
        string userMsg;
        string sysPrompt;
        if (isPromptMode)
        {
            if (string.IsNullOrWhiteSpace(_scriptText))
            {
                ShowCopyToast("⚠ 剧本内容为空，请先生成或编写剧本");
                return;
            }
            sysPrompt = config.PromptSkill;
            userMsg = $"请根据以下漫剧剧本内容，为每个场景生成创作提示词（包括画面构图、角色动作、表情、光影氛围等描述）：\n\n"
                + $"章节：第{_currentChapter.Index}章 {_currentChapter.Title}\n\n"
                + $"剧本内容：\n{_scriptText}\n\n"
                + "请用纯文本输出，每个场景用「场景N：标题」作为分隔，场景描述用自然段落，不要使用 Markdown 标记符号。";
        }
        else
        {
            var original = new TextRange(OriginalTextBox.Document.ContentStart,
                OriginalTextBox.Document.ContentEnd).Text;
            if (string.IsNullOrWhiteSpace(original))
            {
                ShowCopyToast("⚠ 小说内容为空，请先导入小说");
                return;
            }
            sysPrompt = config.ScriptSkill;
            userMsg = $"请将以下小说章节内容改编为漫剧剧本：\n\n"
                + $"章节：第{_currentChapter.Index}章 {_currentChapter.Title}\n\n"
                + $"原文：\n{original}\n\n"
                + "剧本格式要求：用「场景N」分隔每个场景，每场景包含「画面描述」「对话」「动作指导」三部分，用自然段落表述，不要使用 Markdown 标记符号。";
        }

        // 开始生成
        _aiCts = new CancellationTokenSource();
        AiScriptBtn.Content = "⏹ 取消生成";
        var sb = new System.Text.StringBuilder();
        var tokenCount = 0;

        try
        {
            await ApiService.ChatStreamAsync(
                config.ApiEndpoint, config.ApiKey, config.ApiModel,
                sysPrompt, userMsg,
                onToken: token =>
                {
                    tokenCount++;
                    lock (sb) sb.Append(token);
                    // 每10个token刷新一次预览
                    if (tokenCount % 10 == 0)
                    {
                        var soFar = sb.ToString();
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (isPromptMode) _promptText = soFar;
                            else _scriptText = soFar;
                            UpdateScriptEditor();
                        });
                    }
                },
                cancel: _aiCts.Token);

            // 生成完成，写入最终内容
            _ = Dispatcher.BeginInvoke(() =>
            {
                var result = sb.ToString();
                if (isPromptMode) _promptText = result;
                else _scriptText = result;
                UpdateScriptEditor();
                SaveCurrentContent();
                ShowCopyToast($"✓ {targetName}生成完成（{tokenCount} tokens）");
            });
        }
        catch (OperationCanceledException)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (isPromptMode) _promptText = sb.ToString();
                else _scriptText = sb.ToString();
                SaveCurrentContent();
                ShowCopyToast($"⚠ 已取消生成（已生成 {tokenCount} tokens）");
            });
        }
        catch (ApiException ex)
        {
            _ = Dispatcher.BeginInvoke(() =>
                ShowCopyToast($"⚠ {ex.Message}"));
        }
        catch (Exception ex)
        {
            _ = Dispatcher.BeginInvoke(() =>
                ShowCopyToast($"⚠ 生成失败：{ex.Message}"));
        }
        finally
        {
            _aiCts?.Dispose();
            _aiCts = null;
            AiScriptBtn.Content = "🤖 AI 生成";
        }
    }

    private void PromptTextBox_LostFocus(object sender, RoutedEventArgs e) { }

    // ===== RichTextBox 文字高亮功能 =====
    private void HighlightRed_Click(object sender, RoutedEventArgs e)
    {
        ApplyHighlight("#E81123");
    }

    private void HighlightColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string color)
            ApplyHighlight(color);
    }

    private void ApplyHighlight(string colorHex)
    {
        if (OriginalTextBox.Selection.IsEmpty) return;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHex);
            OriginalTextBox.Selection.ApplyPropertyValue(TextElement.BackgroundProperty,
                new SolidColorBrush(color) { Opacity = 0.3 });
            _contentDirty = true;
        }
        catch { }
    }

    private void ClearHighlight_Click(object sender, RoutedEventArgs e)
    {
        if (OriginalTextBox.Selection.IsEmpty) return;
        OriginalTextBox.Selection.ApplyPropertyValue(TextElement.BackgroundProperty,
            Brushes.Transparent);
        _contentDirty = true;
    }

    // ===== 图像素材 =====
    private void AddImage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null || _currentChapter == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp",
            Multiselect = true,
            Title = "选择要添加的图像素材"
        };
        if (dlg.ShowDialog() != true) return;
        var targetDir = FileService.ChapterImagesPath(
            App.WorkRoot, _currentNovel.MediaFolder, _currentChapter.FolderName);
        foreach (var file in dlg.FileNames)
            FileService.CopyFile(file, targetDir);
        RefreshImageGrid();
    }

    private void AiGenerateImage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null || _currentChapter == null) return;

        // 不再强制要求 API Key：使用 ComfyUI 本地引擎时无需 API Key，进入对话框后再按引擎校验
        var config = FileService.LoadConfig(App.WorkRoot);

        // 关键：不设置 Owner！WPF 关闭 owned 子窗口时会激活/最小化 AllowsTransparency 主窗口。
        // 去掉 Owner 彻底切断 owned 关系，用 Topmost + 手动居中保持使用体验。
        var win = new Window
        {
            Title = "AI 生成图片",
            Width = 560, Height = 470,
            MinWidth = 480, MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Topmost = true,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.CanResize,
            Background = (Brush)FindResource("WindowBackgroundBrush")
        };
        ViewHelpers.CenterWindowOnOwner(win, Window.GetWindow(this));

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 标题（带当前引擎徽章 + 优化按钮）
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.Children.Add(new TextBlock
        {
            Text = "输入图片生成提示词",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        // 顶部引擎标识（紧凑胶囊样式，提示当前生效的生图引擎）
        var providerBadge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("PrimaryLowBrush"),
            BorderBrush = (Brush)FindResource("PrimaryBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var providerBadgeText = new TextBlock
        {
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("PrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (config.DefaultImageProvider == "ComfyUI")
            providerBadgeText.Text = "🖥️ 本地 ComfyUI";
        else
            providerBadgeText.Text = "☁️ 云端 API";
        providerBadgeText.ToolTip = "本次图片生成将使用此引擎（在设置→AI 模型配置中切换）";
        providerBadge.Child = providerBadgeText;
        Grid.SetColumn(providerBadge, 1);
        headerGrid.Children.Add(providerBadge);

        var optimizeBtn = new Button
        {
            Content = "✨ 优化提示词",
            FontSize = 12, Padding = new Thickness(14, 5, 14, 5),
            Style = (Style)FindResource("PrimaryButtonStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "AI 将您的简短提示词丰富为高质量图片生成提示词"
        };
        Grid.SetColumn(optimizeBtn, 2);
        headerGrid.Children.Add(optimizeBtn);
        Grid.SetRow(headerGrid, 0);
        grid.Children.Add(headerGrid);

        // 提示词输入框（加大字号、统一背景与圆角，四角带阴影）
        var promptBox = new TextBox
        {
            Text = "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13.5, FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            VerticalContentAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(promptBox, 2);
        grid.Children.Add(promptBox);

        // ===== 参考图区域（0 张=文生图，1 张=图生图，多张=多图编辑） =====
        var refImages = new List<string>(); // Data URI Base64
        var refPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        var addRefBtn = new Button
        {
            Content = "🖼️ 添加参考图", FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Style = (Style)FindResource("SecondaryButtonStyle"),
            ToolTip = "选择本地图片作为参考：1 张=图生图，多张=多图编辑/合成（在提示词中说明组合方式）"
        };
        refPanel.Children.Add(addRefBtn);
        var assetRefBtn = new Button
        {
            Content = "📁 项目资产", FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Style = (Style)FindResource("SecondaryButtonStyle"),
            ToolTip = "从当前项目的图片资产中选择参考图（章节图片 / 人物素材 / 封面 / 头像）"
        };
        refPanel.Children.Add(assetRefBtn);
        var clearRefBtn = new Button
        {
            Content = "✕ 清除", FontSize = 11,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed,
            Style = (Style)FindResource("SecondaryButtonStyle")
        };
        refPanel.Children.Add(clearRefBtn);
        var refHintText = new TextBlock
        {
            Text = "可添加 1 张（图生图）或多张（多图编辑）参考图",
            FontSize = 10.5,
            Foreground = (Brush)FindResource("TextTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        refPanel.Children.Add(refHintText);

        addRefBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif",
                Multiselect = true,
                Title = "选择参考图片（可多选）"
            };
            // 显式绑定 owner 为 AI 小窗口，避免对话框关闭后激活主窗口触发其误最小化
            if (dlg.ShowDialog(win) != true) return;
            foreach (var file in dlg.FileNames)
            {
                ViewHelpers.AddReferenceThumb(refPanel, file, refImages,
                    () => ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn));
            }
            ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn);
        };
        // 从项目资产中选择参考图（owner 传 AI 小窗口，避免模态选择器关闭时激活主窗口触发其误最小化）
        assetRefBtn.Click += (_, _) =>
        {
            try
            {
                if (_currentNovel == null) { ShowCopyToast("⚠ 请先选择小说"); return; }
                var path = ViewHelpers.PickProjectImage(
                    win, "选择项目图片作为参考图",
                    App.WorkRoot, _currentNovel.Id, _currentNovel.MediaFolder);
                if (path == null) return;
                ViewHelpers.AddReferenceThumb(refPanel, path, refImages,
                    () => ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn));
                ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn);
            }
            catch (Exception ex)
            {
                ShowCopyToast($"⚠ 无法打开项目资产：{ex.Message}");
            }
        };
        clearRefBtn.Click += (_, _) =>
        {
            refImages.Clear();
            // 移除所有缩略图（保留按钮/提示）
            for (int i = refPanel.Children.Count - 1; i >= 0; i--)
            {
                if (refPanel.Children[i] is Border b && b.Tag is string t && t == "refthumb")
                    refPanel.Children.RemoveAt(i);
            }
            ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn);
        };

        var refRow = new DockPanel();
        DockPanel.SetDock(refPanel, Dock.Left);
        refRow.Children.Add(refPanel);
        Grid.SetRow(refRow, 4);
        grid.Children.Add(refRow);

        // 底部 footer：两行布局（避免单行过宽导致按钮被遮挡）
        // 生成引擎由设置中的「默认生图引擎」单选框决定
        var footer = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        // 第一行：尺寸选择区域
        var footerTopRow = new DockPanel();

        // 尺寸选择区域：比例 + 像素档位（1K=1024，16:9 + 1K → 1824x1024）
        var sizeCard = new Border
        {
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        var sizePanel = new StackPanel { Orientation = Orientation.Horizontal };
        sizePanel.Children.Add(new TextBlock
        {
            Text = "📐", FontSize = 12, FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        // 比例选择（宽度需≥76 才能让「16:9」完整渲染：内部可用 = Width - 34）
        var ratioBox = new ComboBox
        {
            Width = 76, Height = 28, FontSize = 12,
            ItemsSource = ViewHelpers.ImageRatios,
            SelectedItem = _lastImageRatio,
            ToolTip = "选择宽高比例",
            Style = (Style)Application.Current.FindResource("ModernComboBoxStyle"),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Padding = new Thickness(6, 0, 6, 0)
        };
        sizePanel.Children.Add(ratioBox);
        // 像素档位选择（宽度需≥60 让「1K」完整显示）
        var levelBox = new ComboBox
        {
            Width = 62, Height = 28, FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            ItemsSource = ViewHelpers.ImageLevels,
            SelectedItem = _lastImageLevel,
            ToolTip = "像素档位（短边像素，1K=1024）",
            Style = (Style)Application.Current.FindResource("ModernComboBoxStyle"),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Padding = new Thickness(6, 0, 6, 0)
        };
        sizePanel.Children.Add(levelBox);
        // 计算结果展示：紧凑布局，避免「1824x1024」挤压右侧按钮
        var sizeResultText = new TextBlock
        {
            Text = "", FontSize = 10,
            Foreground = (Brush)FindResource("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            MinWidth = 60,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei UI")
        };
        sizePanel.Children.Add(sizeResultText);

        void UpdateSizeResult()
        {
            var ratio = ratioBox.SelectedItem?.ToString() ?? "1:1";
            var level = levelBox.SelectedItem?.ToString() ?? "1K";
            sizeResultText.Text = ViewHelpers.CalcImageSize(ratio, level);
        }
        ratioBox.SelectionChanged += (_, _) => UpdateSizeResult();
        levelBox.SelectionChanged += (_, _) => UpdateSizeResult();
        UpdateSizeResult();

        sizeCard.Child = sizePanel;
        DockPanel.SetDock(sizeCard, Dock.Left);
        footerTopRow.Children.Add(sizeCard);

        footer.Children.Add(footerTopRow);

        // 第二行：按钮组（靠右） — 独立成行，不再被遮挡
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var queueBtn = new Button
        {
            Content = "📋 查看队列",
            FontSize = 12, Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)FindResource("SecondaryButtonStyle")
        };
        queueBtn.Click += (_, _) => OpenQueueWindow();
        btnPanel.Children.Add(queueBtn);
        var genBtn = new Button
        {
            Content = "🎨 开始生成",
            FontSize = 13, Padding = new Thickness(16, 6, 16, 6),
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        btnPanel.Children.Add(genBtn);
        footer.Children.Add(btnPanel);
        Grid.SetRow(footer, 6);
        grid.Children.Add(footer);

        win.Content = grid;

        // 优化提示词按钮事件
        optimizeBtn.Click += async (_, _) =>
        {
            var rawPrompt = promptBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(rawPrompt))
            {
                ShowCopyToast("⚠ 请先输入提示词再优化");
                return;
            }
            optimizeBtn.IsEnabled = false;
            optimizeBtn.Content = "⏳ 优化中...";
            try
            {
                // 是否有参考图：0 张=文生图（仅依据文本优化），≥1 张=图生图/多图编辑（依据文本 + 图像内容优化）
                bool hasRef = refImages.Count > 0;

                var sys = "你是一位专业的 AI 图像生成提示词优化师。"
                    + (hasRef
                        ? "用户提供了参考图，请仔细观察参考图的内容（主体外观、姿态、场景、构图、色彩风格），"
                          + "并结合用户文本，扩展为一段详细、专业的图像生成提示词，使生成结果与参考图风格统一。"
                          + "要求：1. 准确提炼参考图中的主体特征、构图与色调并融入提示词 2. 若文本与参考图冲突，以文本意图为主、参考图风格为辅 "
                          + "3. 详细描述主体外观、表情、姿态、服饰 4. 描述场景背景、构图、景深 "
                          + "5. 丰富光影、色彩、氛围 6. 指定摄影/绘画风格（如电影剧照、肖像摄影、动漫风）"
                          + "7. 使用流畅的英文或中英混合（英文术语更准确） 8. 保持原意同时让画面更具视觉冲击力 9. 只输出优化后的提示词，不要任何解释。"
                        : "请根据用户提供的简短提示词，扩展为一段详细、专业的图像生成提示词。"
                          + "要求：1. 详细描述主体外观、表情、姿态、服饰 2. 描述场景背景、构图、景深 "
                          + "3. 丰富光影、色彩、氛围 4. 指定摄影/绘画风格（如电影剧照、肖像摄影、动漫风）"
                          + "5. 使用流畅的英文或中英混合（英文术语更准确）"
                          + "6. 保持原意的同时让画面更具视觉冲击力 7. 只输出优化后的提示词，不要任何解释。");

                // 有参考图时，将参考图作为视觉输入一起交给模型；否则仅用文本
                var result = hasRef
                    ? await ApiService.ChatWithImagesAsync(
                        config.ApiEndpoint, config.ApiKey, config.ApiModel,
                        sys, $"请结合参考图优化以下图像生成提示词：\n{rawPrompt}",
                        refImages)
                    : await ApiService.ChatAsync(
                        config.ApiEndpoint, config.ApiKey, config.ApiModel,
                        sys, $"请优化以下图像生成提示词：\n{rawPrompt}");

                if (!string.IsNullOrWhiteSpace(result))
                {
                    promptBox.Text = result.Trim();
                    ShowCopyToast(hasRef ? "✓ 提示词已结合参考图优化" : "✓ 提示词已优化");
                }
            }
            catch (ApiException ex) { ShowCopyToast($"⚠ {ex.Message}"); }
            catch (Exception ex) { ShowCopyToast($"⚠ 优化失败：{ex.Message}"); }
            finally
            {
                optimizeBtn.IsEnabled = true;
                optimizeBtn.Content = "✨ 优化提示词";
            }
        };

        // 生成按钮：创建任务并入队后立即关闭窗口，生成交给后台队列串行执行
        genBtn.Click += (_, _) =>
        {
            var prompt = promptBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                ShowCopyToast("⚠ 请输入提示词");
                return;
            }

            bool useComfy = config.DefaultImageProvider == "ComfyUI";

            // 在线 API 引擎需要 API Key；ComfyUI 引擎需要已配置服务地址
            if (!useComfy && (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(config.ApiEndpoint)))
            {
                ShowCopyToast("⚠ 使用在线 API 需先在「设置→AI 模型配置」中填入 API 地址和密钥");
                return;
            }
            if (useComfy && (string.IsNullOrWhiteSpace(config.ComfyUiEndpoint) || string.IsNullOrWhiteSpace(config.ComfyUiWorkflowFile)))
            {
                ShowCopyToast("⚠ 使用本地 ComfyUI 需先在「设置→AI 模型配置」中配置 ComfyUI 地址和工作流文件");
                return;
            }

            var ratio = ratioBox.SelectedItem?.ToString() ?? "1:1";
            var level = levelBox.SelectedItem?.ToString() ?? "1K";
            _lastImageRatio = ratio;
            _lastImageLevel = level;
            var size = ViewHelpers.CalcImageSize(ratio, level);

            // 快照当前小说/章节，防止用户切换后任务保存到错误目录
            var novel = _currentNovel;
            var chapter = _currentChapter;
            var providerLabel = useComfy ? "ComfyUI" : "API";
            var task = new AiTask
            {
                Type = AiTaskType.Image,
                Provider = useComfy ? ImageProvider.ComfyUI : ImageProvider.Api,
                Prompt = prompt,
                Detail = useComfy
                    ? $"ComfyUI·{size}" + (refImages.Count > 0 ? $"·参考图×{refImages.Count}" : "")
                    : (refImages.Count > 0 ? $"参考图×{refImages.Count}·{size}" : size),
                ReferenceImages = refImages.Count > 0 ? new List<string>(refImages) : null,
                ApiEndpoint = useComfy ? config.ComfyUiEndpoint : config.ApiEndpoint,
                ApiKey = config.ApiKey,
                Model = config.ImageModel,
                ComfyWorkflowFile = config.ComfyUiWorkflowFile,
                TargetDir = FileService.ChapterImagesPath(App.WorkRoot, novel.MediaFolder, chapter.FolderName),
                FileNameBase = $"AI_{DateTime.Now:yyyyMMdd_HHmmss}",
                ImageSize = size,
                NovelName = novel.Name,
                ScopeName = $"第{chapter.Index}章 {chapter.Title}"
            };
            AiTaskManager.Enqueue(task);
            ShowCopyToast($"✓ 已加入 AI 任务队列（{providerLabel}）");
            try { win.Close(); } catch { }
        };

        // 注册到浮动窗口管理器：最小化时自动隐藏，可通过快捷键恢复
        FloatingWindowManager.Instance.Register(win);

        // 异步窗口：非模态显示且无 Owner，用户可关闭窗口/离开页面做其他事，生成在后台队列中继续
        win.Show();
    }

    /// <summary>打开 AI 任务队列窗口（Topmost 置顶显示，不设 Owner 避免遮挡与主窗口最小化问题）</summary>
    private void OpenQueueWindow()
    {
        try
        {
            var qw = new AiTaskQueueWindow();
            qw.Show();
        }
        catch (Exception ex)
        {
            ShowCopyToast($"⚠ 无法打开队列：{ex.Message}");
        }
    }

    /// <summary>AI 任务完成后，若目标目录是当前章节的图像目录则实时刷新素材列表</summary>
    public void TryRefreshAfterAiTask(AiTask task)
    {
        if (_currentNovel == null || _currentChapter == null) return;
        var target = FileService.ChapterImagesPath(App.WorkRoot, _currentNovel.MediaFolder, _currentChapter.FolderName);
        if (string.Equals(target.TrimEnd('\\', '/'), task.TargetDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            RefreshImageGrid();
    }

    private void RefreshImageGrid()
    {
        ImageGrid.Children.Clear();
        ImageGrid.RowDefinitions.Clear();
        ImageGrid.ColumnDefinitions.Clear();

        if (_currentNovel == null || _currentChapter == null) return;

        var chapterPath = FileService.ChapterImagesPath(
            App.WorkRoot, _currentNovel.MediaFolder, _currentChapter.FolderName);
        var images = FileService.GetFiles(chapterPath, ".png", ".jpg", ".jpeg", ".webp");

        if (images.Count == 0)
        {
            // 恢复一行一列以便占位文字居中显示
            ImageGrid.RowDefinitions.Add(new RowDefinition());
            ImageGrid.ColumnDefinitions.Add(new ColumnDefinition());
            ImageGrid.Children.Add(new TextBlock
            {
                Text = "暂无素材\n拖拽图片或点击「添加」导入",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12, TextAlignment = TextAlignment.Center
            });
            return;
        }

        // ===== 瀑布流布局 =====
        // 根据可用宽度自适应列数（1~4 列），每列独立纵向堆叠，按原始宽高比显示
        int cols = 3;
        if (ImagePanel.IsLoaded && ImagePanel.ActualWidth > 0)
        {
            double availW = ImagePanel.ActualWidth - 16; // 减去内边距
            if (availW < 260) cols = 1;
            else if (availW < 460) cols = 2;
            else if (availW < 720) cols = 3;
            else cols = 4;
        }

        // 每列创建一个 StackPanel 作为垂直瀑布流容器
        for (int i = 0; i < cols; i++)
            ImageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ImageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var columnPanels = new StackPanel[cols];
        for (int c = 0; c < cols; c++)
        {
            columnPanels[c] = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(columnPanels[c], c);
            ImageGrid.Children.Add(columnPanels[c]);
        }

        // 瀑布流列高累加器（用于"最短列优先"贪心分配）
        var columnHeights = new double[cols];

        foreach (var imgPath in images)
        {
            string imgName = Path.GetFileName(imgPath);
            try
            {
                // 读取原始尺寸，按列宽换算显示高度（用于均衡列高）
                double ratio = GetImageAspectRatio(imgPath); // 宽/高，未知时默认 1
                double displayHeight = ratio > 0 ? (220.0 / ratio) : 170.0;

                // 找到当前最矮的列
                int targetCol = 0;
                for (int c = 1; c < cols; c++)
                    if (columnHeights[c] < columnHeights[targetCol]) targetCol = c;
                columnHeights[targetCol] += displayHeight;

                // 解码宽度按列宽设置（瀑布流图片更小更轻）
                int decodeWidth = cols == 1 ? 400 : 240;
                var cacheKey = $"{imgPath}@{decodeWidth}w";

                var img = new Image
                {
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Tag = imgPath,
                    Cursor = Cursors.Hand
                };

                // 命中缓存：立即回填；未命中：后台异步解码，避免阻塞 UI
                if (_imageCache.TryGetValue(cacheKey, out var cached) && cached != null)
                {
                    img.Source = cached;
                }
                else
                {
                    // 先占位（保持瀑布流高度），后台解码后回填
                    img.MinHeight = 120;
                    LoadImageAsync(imgPath, cacheKey, decodeWidth, img);
                }

                // 选中角标（仅多选时显示，右上角小圆角方块 + 勾）
                var selBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x4A, 0x90, 0xE2)),
                    CornerRadius = new CornerRadius(12),
                    Width = 24, Height = 24,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 4, 4, 0),
                    Visibility = Visibility.Collapsed,
                    Child = new TextBlock
                    {
                        Text = "\uE73E", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 14, Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };

                // 卡片容器：图片 + 名称 + 悬停操作栏
                var card = new Border
                {
                    Margin = new Thickness(4),
                    CornerRadius = new CornerRadius(6),
                    ClipToBounds = true,
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(2),
                    Tag = imgPath
                };
                card.Loaded += (s, _) => ViewHelpers.ApplyRoundedClip(card);
                card.SizeChanged += (s, _) => ViewHelpers.ApplyRoundedClip(card);

                var cardStack = new StackPanel();

                // 图片区域（带悬停工具栏 + 选中角标 + 圆角裁剪）
                var imageArea = new Grid { ClipToBounds = true };
                img.ClipToBounds = true;
                imageArea.Children.Add(img);
                imageArea.Children.Add(selBadge);

                var toolbar = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 6),
                    Opacity = 0
                };
                toolbar.Children.Add(CreateImageBtn("\uE8C8", "复制", () => CopyImageFile(imgPath)));
                toolbar.Children.Add(CreateImageBtn("\uE8AC", "重命名", () => RenameImageFile(imgPath)));
                toolbar.Children.Add(CreateImageBtn("\uE74D", "删除", () => DeleteImageFile(imgPath)));
                imageArea.Children.Add(toolbar);
                cardStack.Children.Add(imageArea);

                // 图片名称
                cardStack.Children.Add(new TextBlock
                {
                    Text = imgName,
                    FontSize = 10,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0),
                    MaxWidth = 200
                });

                card.Child = cardStack;
                card.MouseEnter += (_, _) => toolbar.Opacity = 1;
                card.MouseLeave += (_, _) => toolbar.Opacity = 0;
                card.MouseLeftButtonDown += (_, _) =>
                {
                    if (_multiSelectMode)
                        ToggleFileSelection(imgPath, card, selBadge);
                    else
                        ViewHelpers.ShowImageViewer(imgPath, Window.GetWindow(this));
                };

                columnPanels[targetCol].Children.Add(card);
            }
            catch { /* 单张加载失败不影响其他 */ }
        }
    }

    /// <summary>
    /// 读取图片原始宽高比（宽/高）。仅读取文件头，不完整解码，速度快。
    /// 返回 0 表示无法解析（默认按 1:1 处理）。
    /// </summary>
    private static double GetImageAspectRatio(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count > 0)
            {
                int w = decoder.Frames[0].PixelWidth;
                int h = decoder.Frames[0].PixelHeight;
                if (w > 0 && h > 0) return (double)w / h;
            }
        }
        catch { /* 忽略解析失败 */ }
        return 0;
    }

    /// <summary>
    /// 后台异步解码图片并回填到 Image 控件。解码在 Task.Run 线程完成，
    /// 通过 Dispatcher 切回 UI 线程设置 Source，避免阻塞界面。
    /// </summary>
    private void LoadImageAsync(string imgPath, string cacheKey, int decodeWidth, Image img)
    {
        Task.Run(() =>
        {
            try
            {
                var data = File.ReadAllBytes(imgPath);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                using var msImg = new MemoryStream(data);
                bmp.StreamSource = msImg;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = decodeWidth;
                bmp.EndInit();
                bmp.Freeze();
                _imageCache[cacheKey] = bmp;
                return (BitmapSource)bmp;
            }
            catch { return null; }
        }).ContinueWith(t =>
        {
            if (t.Result == null) return;
            Dispatcher.BeginInvoke(() =>
            {
                // 控件可能已被重建，检查是否仍可用
                if (img.IsLoaded || img.Parent != null)
                {
                    img.Source = t.Result;
                    img.MinHeight = 0;
                }
            });
        }, TaskScheduler.Default);
    }

    private Button CreateImageBtn(string icon, string tooltip, Action click)
    {
        var btn = new Button
        {
            Content = icon,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11, Width = 26, Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 0, 2, 0),
            ToolTip = tooltip,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x33, 0x33, 0x33)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        btn.Click += (_, _) => click();
        btn.MouseEnter += (s, _) => ((Button)s).Background =
            new SolidColorBrush(Color.FromArgb(0xF0, 0x55, 0x55, 0x55));
        btn.MouseLeave += (s, _) => ((Button)s).Background =
            new SolidColorBrush(Color.FromArgb(0xD0, 0x33, 0x33, 0x33));
        return btn;
    }

    private void CopyImageFile(string path)
    {
        try
        {
            // 不压缩像素，保留原始尺寸
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // 不设置 DecodePixelWidth，保持原始分辨率
            bmp.EndInit();
            Clipboard.SetImage(bmp);
            ShowCopyToast("✓ 图像已复制到剪贴板");
        }
        catch { ShowCopyToast("✗ 复制失败"); }
    }

    private void RenameImageFile(string path)
    {
        var currentName = Path.GetFileNameWithoutExtension(path);
        var dialog = new InputDialog("重命名素材", "请输入新的名称（不含扩展名）：", currentName);
        dialog.Owner = Window.GetWindow(this);
        dialog.Confirmed += name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                var dir = Path.GetDirectoryName(path)!;
                var ext = Path.GetExtension(path);
                var newPath = Path.Combine(dir, name.Trim() + ext);
                if (!string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(newPath)) FileService.DeleteFile(newPath);
                    File.Move(path, newPath);
                    RefreshImageGrid();
                }
            }
            catch { }
        };
        dialog.Show();
    }

    private void DeleteImageFile(string path)
    {
        try { FileService.DeleteFile(path); RefreshImageGrid(); }
        catch { }
    }

    private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Image img && img.Tag is string path)
        { try { System.Diagnostics.Process.Start("explorer.exe", path); } catch { } }
    }

    private void ShowImageFullScreen(string path)
    {
        try
        {
            var workArea = SystemParameters.WorkArea;
            var winW = Math.Min(workArea.Width * 0.85, 1400);
            var winH = Math.Min(workArea.Height * 0.85, 900);
            var win = new Window
            {
                Title = Path.GetFileName(path),
                Width = winW, Height = winH,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = Brushes.Black,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            var bmp = new BitmapImage(); bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            var img = new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(12) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var zoomXform = new ScaleTransform(1, 1);
            var panXform = new TranslateTransform(0, 0);
            var group = new TransformGroup();
            group.Children.Add(zoomXform);
            group.Children.Add(panXform);
            img.RenderTransform = group;
            img.RenderTransformOrigin = new Point(0.5, 0.5);

            var outer = new Border();
            outer.SizeChanged += (s, e) =>
            {
                if (outer.ActualWidth > 0 && outer.ActualHeight > 0)
                    outer.Clip = new RectangleGeometry(
                        new Rect(0, 0, outer.ActualWidth, outer.ActualHeight), 12, 12);
            };
            outer.Child = img;
            win.Content = outer;

            // 滚轮缩放
            outer.MouseWheel += (_, e) =>
            {
                double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
                double ns = Math.Max(0.2, Math.Min(8, zoomXform.ScaleX * factor));
                zoomXform.ScaleX = ns;
                zoomXform.ScaleY = ns;
            };

            // 左键拖动平移
            Point? panStart = null;
            double startTx = 0, startTy = 0;
            img.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 1)
                { panStart = e.GetPosition(outer); startTx = panXform.X; startTy = panXform.Y; img.CaptureMouse(); }
                else { win.Close(); }
            };
            img.MouseMove += (_, e) =>
            {
                if (panStart.HasValue && e.LeftButton == MouseButtonState.Pressed)
                {
                    var cur = e.GetPosition(outer);
                    panXform.X = startTx + (cur.X - panStart.Value.X);
                    panXform.Y = startTy + (cur.Y - panStart.Value.Y);
                }
            };
            img.MouseLeftButtonUp += (_, _) => { panStart = null; img.ReleaseMouseCapture(); };

            win.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) win.Close(); };
            win.Show();
        }
        catch { }
    }

    private void Image_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Image img && img.Tag is string path)
        {
            var menu = new ContextMenu();
            var delItem = new MenuItem { Header = "删除素材" };
            delItem.Click += (s, a) => { FileService.DeleteFile(path); RefreshImageGrid(); };
            menu.Items.Add(delItem);
            menu.IsOpen = true;
        }
    }

    // ===== 面板折叠/展开（展开后自动按比例压缩，防止溢出） =====
    private void ToggleOriginal_Click(object sender, RoutedEventArgs e)
    {
        _isOriginalExpanded = !_isOriginalExpanded;
        if (_isOriginalExpanded)
        {
            double w = _savedOriginalWidth > 200 ? _savedOriginalWidth : 300;
            OriginalCol.Width = new GridLength(w, GridUnitType.Pixel);
            OriginalCol.MinWidth = 200;
            OriginalPanel.Visibility = Visibility.Visible;
            Splitter1.Visibility = Visibility.Visible;
        }
        else
        {
            _savedOriginalWidth = OriginalCol.ActualWidth > 40 ? OriginalCol.ActualWidth : _savedOriginalWidth;
            OriginalCol.Width = new GridLength(40, GridUnitType.Pixel);
            OriginalCol.MinWidth = 0;
            Splitter1.Visibility = Visibility.Collapsed;
            OriginalContentGrid.Visibility = Visibility.Collapsed;
            OriginalCollapsedView.Visibility = Visibility.Visible;
            return;
        }
        OriginalContentGrid.Visibility = Visibility.Visible;
        OriginalCollapsedView.Visibility = Visibility.Collapsed;
        FitColumnsToContainer();
    }

    private void ToggleScript_Click(object sender, RoutedEventArgs e)
    {
        _isScriptExpanded = !_isScriptExpanded;
        if (_isScriptExpanded)
        {
            double w = _savedScriptWidth > 200 ? _savedScriptWidth : 300;
            ScriptCol.Width = new GridLength(w, GridUnitType.Pixel);
            ScriptCol.MinWidth = 200;
            ScriptPanel.Visibility = Visibility.Visible;
            Splitter2.Visibility = Visibility.Visible;
        }
        else
        {
            _savedScriptWidth = ScriptCol.ActualWidth > 40 ? ScriptCol.ActualWidth : _savedScriptWidth;
            ScriptCol.Width = new GridLength(40, GridUnitType.Pixel);
            ScriptCol.MinWidth = 0;
            Splitter2.Visibility = Visibility.Collapsed;
            ScriptContentGrid.Visibility = Visibility.Collapsed;
            ScriptCollapsedView.Visibility = Visibility.Visible;
            return;
        }
        ScriptContentGrid.Visibility = Visibility.Visible;
        ScriptCollapsedView.Visibility = Visibility.Collapsed;
        FitColumnsToContainer();
    }

    private void ToggleImage_Click(object sender, RoutedEventArgs e)
    {
        _isImageExpanded = !_isImageExpanded;
        if (_isImageExpanded)
        {
            double w = _savedImageWidth > 220 ? _savedImageWidth : 300;
            ImageCol.Width = new GridLength(w, GridUnitType.Pixel);
            ImageCol.MinWidth = 220;
            ImagePanel.Visibility = Visibility.Visible;
            Splitter3.Visibility = Visibility.Visible;
        }
        else
        {
            _savedImageWidth = ImageCol.ActualWidth > 40 ? ImageCol.ActualWidth : _savedImageWidth;
            ImageCol.Width = new GridLength(40, GridUnitType.Pixel);
            ImageCol.MinWidth = 0;
            ImageContentGrid.Visibility = Visibility.Collapsed;
            ImageCollapsedView.Visibility = Visibility.Visible;
            Splitter3.Visibility = Visibility.Collapsed;
            return;
        }
        ImageContentGrid.Visibility = Visibility.Visible;
        ImageCollapsedView.Visibility = Visibility.Collapsed;
        FitColumnsToContainer();
    }

    /// <summary>
    /// 按当前容器宽度，将已保存的列宽等比压缩到不溢出
    /// </summary>
    private void FitColumnsToContainer()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            () => ClampColumnsNow());
    }

    /// <summary>
    /// 窗口大小变化时硬约束：三列总宽决不超过书籍列表左边界
    /// </summary>
    private void ContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ClampColumnsNow();
    }

    /// <summary>
    /// 窗口缩放时：仅当三列总宽即将溢出时等比压缩，不主动扩展
    /// </summary>
    private void ClampColumnsNow()
    {
        double total = GetContentAvailableWidth();
        if (total <= 0) return;
        int visible = (_isOriginalExpanded ? 1 : 0) + (_isScriptExpanded ? 1 : 0) + (_isImageExpanded ? 1 : 0);
        if (visible == 0) return;
        double collapsed = (3 - visible) * 40;
        // splitter 宽度：每个展开的面板间有 1 个 splitter（共 2 个内部），图像右侧还有 1 个
        int splitterCount = (_isOriginalExpanded && _isScriptExpanded ? 1 : 0)
            + (_isScriptExpanded && _isImageExpanded ? 1 : 0)
            + (_isImageExpanded ? 1 : 0);
        double available = total - collapsed - splitterCount * 4;
        // 各面板最小宽度：小说/剧本200，图像220
        double minTotal = (_isOriginalExpanded ? 200.0 : 0)
            + (_isScriptExpanded ? 200.0 : 0) + (_isImageExpanded ? 220.0 : 0);
        if (available < minTotal) return;

        double[] saved = { _savedOriginalWidth, _savedScriptWidth, _savedImageWidth };
        bool[] show = { _isOriginalExpanded, _isScriptExpanded, _isImageExpanded };
        double savedSum = 0;
        for (int j = 0; j < 3; j++) if (show[j]) savedSum += saved[j];
        if (savedSum <= 0) return;

        // 用已保存宽度判断是否溢出，避免 ActualWidth 未刷新导致误判
        if (savedSum <= available + 2) return;

        double ratio = available / savedSum;
        if (_isOriginalExpanded)
            OriginalCol.Width = new GridLength(Math.Max(200, saved[0] * ratio), GridUnitType.Pixel);
        if (_isScriptExpanded)
            ScriptCol.Width = new GridLength(Math.Max(200, saved[1] * ratio), GridUnitType.Pixel);
        if (_isImageExpanded)
            ImageCol.Width = new GridLength(Math.Max(220, saved[2] * ratio), GridUnitType.Pixel);
    }

    /// <summary>计算三列内容区可用总宽度（窗口级，不受子列溢出影响）</summary>
    private double GetContentAvailableWidth()
    {
        double w = ActualWidth - NovelListCol.ActualWidth - 24;
        return w > 0 ? w : 800;
    }

    /// <summary>
    /// 拖拽 Splitter1 时：不让脚本列缩到 200 以下，也不让图像列被挤出边界
    /// </summary>
    private void Splitter1_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newOriginalW = OriginalCol.ActualWidth + e.HorizontalChange;
        double newScriptW = ScriptCol.ActualWidth - e.HorizontalChange;
        // 脚本列最小 200
        if (newScriptW < 200) { e.Handled = true; return; }
        // 图像列不能被挤出：必须保留至少 220
        double collapsed = 0;
        if (!_isScriptExpanded) collapsed += 40;
        if (!_isImageExpanded) collapsed += 40;
        double total = GetContentAvailableWidth();
        double targetImgW = total - newOriginalW - newScriptW - collapsed - 8;
        if (_isImageExpanded && targetImgW < 220) { e.Handled = true; return; }
        // 约束图像列不溢出（设置 MaxWidth 防止实际渲染溢出）
        if (_isImageExpanded && targetImgW > 0)
            ImageCol.MaxWidth = targetImgW;
    }

    /// <summary>
    /// 拖拽 Splitter2 时：图像列不超出容器，脚本列也不缩到 200 以下
    /// </summary>
    private void Splitter2_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newScriptW = ScriptCol.ActualWidth - e.HorizontalChange;
        // 脚本列最小 200
        if (newScriptW < 200) { e.Handled = true; return; }
        // 图像列不超出右边界
        double collapsed = 0;
        if (!_isOriginalExpanded) collapsed += 40;
        double origW = _isOriginalExpanded ? OriginalCol.ActualWidth : 40;
        double total = GetContentAvailableWidth();
        double maxImg = total - origW - newScriptW - collapsed - 8;
        if (_isImageExpanded && ImageCol.ActualWidth + e.HorizontalChange > maxImg)
            e.Handled = true;
        // 约束图像列不溢出
        if (_isImageExpanded && maxImg > 220)
            ImageCol.MaxWidth = maxImg;
    }

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        // 清除拖拽中设置的 MaxWidth 约束，让 ClampColumnsNow 统一管理
        ImageCol.ClearValue(ColumnDefinition.MaxWidthProperty);
        if (OriginalCol.ActualWidth > 200) _savedOriginalWidth = OriginalCol.ActualWidth;
        if (ScriptCol.ActualWidth > 200) _savedScriptWidth = ScriptCol.ActualWidth;
        if (ImageCol.ActualWidth > 220) _savedImageWidth = ImageCol.ActualWidth;
        // 拖拽完成后做一次等比压缩，确保不溢出
        ClampColumnsNow();
    }

    /// <summary>
    /// 拖拽 Splitter3（图像面板右边界）时：完全手动控制列宽，阻止 GridSplitter 默认行为
    /// </summary>
    private void Splitter3_DragDelta(object sender, DragDeltaEventArgs e)
    {
        e.Handled = true; // 阻止 GridSplitter 默认列宽调整
        if (!_isImageExpanded) return;
        double newImgW = ImageCol.ActualWidth + e.HorizontalChange;
        // 计算最大可用宽度
        double collapsed = 0;
        if (!_isOriginalExpanded) collapsed += 40;
        if (!_isScriptExpanded) collapsed += 40;
        double origW = _isOriginalExpanded ? OriginalCol.ActualWidth : 40;
        double scriptW = _isScriptExpanded ? ScriptCol.ActualWidth : 40;
        double total = GetContentAvailableWidth();
        // splitter 宽度：Splitter1(4) + Splitter2(4) + Splitter3(4) = 12
        double maxImgW = total - origW - scriptW - collapsed - 12;
        // 钳制到 [220, maxImgW] 范围
        if (newImgW < 220) newImgW = 220;
        if (newImgW > maxImgW) newImgW = maxImgW;
        ImageCol.Width = new GridLength(newImgW, GridUnitType.Pixel);
    }

    private void Splitter3_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        ImageCol.ClearValue(ColumnDefinition.MaxWidthProperty);
        if (_isImageExpanded && ImageCol.ActualWidth > 220)
            _savedImageWidth = ImageCol.ActualWidth;
        ClampColumnsNow();
    }

    /// <summary>
    /// 双击任意边界 → 将三个面板等分可用空间
    /// </summary>
    private void Splitter_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        int visible = (_isOriginalExpanded ? 1 : 0) + (_isScriptExpanded ? 1 : 0) + (_isImageExpanded ? 1 : 0);
        if (visible == 0) return;
        double total = GetContentAvailableWidth();
        double collapsed = (3 - visible) * 40;
        // splitter 数量：每个展开面板间 1 个 + 图像右侧 1 个（若展开）
        int sc = (_isOriginalExpanded && _isScriptExpanded ? 1 : 0)
               + (_isScriptExpanded && _isImageExpanded ? 1 : 0)
               + (_isImageExpanded ? 1 : 0);
        double available = total - collapsed - sc * 4;
        double each = Math.Floor(available / visible);

        if (_isOriginalExpanded)
        {
            OriginalCol.Width = new GridLength(each, GridUnitType.Pixel);
            _savedOriginalWidth = each;
        }
        if (_isScriptExpanded)
        {
            ScriptCol.Width = new GridLength(each, GridUnitType.Pixel);
            _savedScriptWidth = each;
        }
        if (_isImageExpanded)
        {
            ImageCol.Width = new GridLength(each, GridUnitType.Pixel);
            _savedImageWidth = each;
        }
        e.Handled = true;
    }

    // ===== 导入小说 =====
    private void ImportNovel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        { Filter = "文本文件|*.txt", Title = "选择要导入的小说文件" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(dlg.FileName);
            // 防重名：如有同名小说，追加序号
            fileName = EnsureUniqueNovelName(fileName);
            var novelId = Guid.NewGuid().ToString();
            FileService.EnsureDirectory(FileService.NovelPath(App.WorkRoot, novelId));
            var novelInfo = new NovelInfo { Id = novelId, Name = fileName, Description = $"导入时间: {DateTime.Now:yyyy-MM-dd HH:mm}" };
            var encoding = ChapterParserService.DetectEncoding(dlg.FileName);
            var rawText = File.ReadAllText(dlg.FileName, encoding);
            File.WriteAllText(FileService.NovelOriginalFile(App.WorkRoot, novelId), rawText, System.Text.Encoding.UTF8);
            FileService.SaveNovelInfo(App.WorkRoot, novelInfo);
            var chapters = ChapterParserService.ParseNovel(dlg.FileName);
            FileService.SaveChapters(App.WorkRoot, novelId, chapters);
            RefreshNovelList();
            SelectNovel(novelInfo);
            NotifyNovelsChanged();
            MessageDialog.Show("导入完成", $"导入成功！\n\n小说：《{fileName}》\n自动识别 {chapters.Count} 个章节\n编码：{encoding.EncodingName}");
        }
        catch (Exception ex)
        { MessageDialog.Show("导入错误", $"导入失败：{ex.Message}"); }
    }

    // ===== 新建空白小说 =====
    private void CreateNovel_Click(object sender, RoutedEventArgs e)
    {
        var defaultName = EnsureUniqueNovelName("新小说");
        var dialog = new InputDialog("新建小说", "请输入小说名称：", defaultName);
        dialog.Owner = Window.GetWindow(this);
        dialog.Confirmed += name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = EnsureUniqueNovelName(name.Trim());
        var novelId = Guid.NewGuid().ToString();
        FileService.EnsureDirectory(FileService.NovelPath(App.WorkRoot, novelId));
        var novelInfo = new NovelInfo { Id = novelId, Name = name, Description = $"创建时间: {DateTime.Now:yyyy-MM-dd HH:mm}" };
        FileService.SaveNovelInfo(App.WorkRoot, novelInfo);
        FileService.WriteText(FileService.NovelOriginalFile(App.WorkRoot, novelId), string.Empty);
        var firstChapter = new Chapter { Index = 1, Title = "第一章", OriginalContent = string.Empty, ScriptContent = string.Empty };
        var chapters = new List<Chapter> { firstChapter };
        FileService.SaveChapters(App.WorkRoot, novelId, chapters);
        RefreshNovelList();
        SelectNovel(novelInfo);
        NotifyNovelsChanged();
        };
        dialog.Show();
    }

    /// <summary>确保小说名称唯一：如已存在则追加序号 (2), (3)...</summary>
    private string EnsureUniqueNovelName(string baseName, string? excludeId = null)
    {
        var existingNames = new HashSet<string>(
            _novels.Where(n => n.Id != excludeId).Select(n => n.Name),
            StringComparer.OrdinalIgnoreCase);
        if (!existingNames.Contains(baseName)) return baseName;

        for (int i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!existingNames.Contains(candidate)) return candidate;
        }
    }

    private void AddChapter_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null) return;
        var idx = _chapters.Count > 0 ? _chapters.Max(c => c.Index) + 1 : 1;
        var newChapter = new Chapter { Index = idx, Title = $"第{idx}章", OriginalContent = string.Empty };
        _chapters.Add(newChapter);
        FileService.SaveChapters(App.WorkRoot, _currentNovel.Id, _chapters);
        RefreshChapterTabs();
        SelectChapter(newChapter);
    }

    private void DeleteChapter(Chapter chapter)
    {
        if (_currentNovel == null) return;
        _chapters.Remove(chapter);
        FileService.SaveChapters(App.WorkRoot, _currentNovel.Id, _chapters);
        if (_currentChapter == chapter)
        {
            _currentChapter = _chapters.FirstOrDefault();
            if (_currentChapter != null)
            {
                RefreshChapterTabs();
                SelectChapter(_currentChapter);
            }
            else
            {
                ChapterTabsPanel.Children.Clear();
                try { OriginalTextBox.Document.Blocks.Clear(); } catch { }
                _scriptText = ""; _promptText = ""; UpdateScriptEditor();
                ImageGrid.Children.Clear();
            }
        }
        else { RefreshChapterTabs(); }
    }

    /// <summary>
    /// 复制小说原文到剪贴板
    /// </summary>
    private void CopyOriginal_Click(object sender, RoutedEventArgs e)
    {
        var textRange = new TextRange(OriginalTextBox.Document.ContentStart, OriginalTextBox.Document.ContentEnd);
        Clipboard.SetText(textRange.Text);
        ShowCopyToast("\u2713 原文已复制");
    }

    /// <summary>
    /// 复制剧本内容到剪贴板
    /// </summary>
    private void CopyScript_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_isScriptMode ? _scriptText : _promptText);
        ShowCopyToast("\u2713 已复制");
    }

    /// <summary>
    /// 底部浮动提示，淡入→停留→淡出
    /// </summary>
    private async void ShowCopyToast(string message)
    {
        if (CopyToastText == null || CopyToast == null) return;
        CopyToastText.Text = message;
        CopyToast.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2)));
        try
        {
            await Task.Delay(1500);
            CopyToast.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.35)));
        }
        catch { /* 页面卸载时忽略 */ }
    }

    /// <summary>检测字符串是否为二进制乱码（旧版 XamlPackage 残留数据）</summary>
    private static bool IsLikelyBinaryGarbage(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int bad = 0;
        int limit = Math.Min(s.Length, 200);
        for (int i = 0; i < limit; i++)
        {
            char c = s[i];
            // 控制字符除了 \r \n \t 之外都是非正常可显文本
            if (c < 0x20 && c != '\r' && c != '\n' && c != '\t') bad++;
            // 替换字符 U+FFFD 或私有区 U+E000-U+F8FF
            if (c == '\uFFFD' || (c >= '\uE000' && c <= '\uF8FF')) bad++;
        }
        return bad > limit / 10; // 异常字符超过 10%
    }

    private void SaveCurrentContent()
    {
        if (_currentNovel == null || _currentChapter == null) return;
        var config = FileService.LoadConfig(App.WorkRoot);
        if (!config.AutoSaveScript) return;
        // 同步 TextBox 实时编辑内容到字段
        if (_isScriptMode) _scriptText = ScriptEditBox.Text;
        else _promptText = ScriptEditBox.Text;
        _currentChapter.ScriptContent = _scriptText;
        _currentChapter.ScriptPrompt = _promptText;
        // 仅在内容变更时才序列化 RichTextBox（Base64+Xaml 开销大）
        if (_contentDirty)
        {
            try
            {
                var range = new TextRange(OriginalTextBox.Document.ContentStart, OriginalTextBox.Document.ContentEnd);
                using var ms = new MemoryStream();
                range.Save(ms, DataFormats.Xaml);
                var b64 = Convert.ToBase64String(ms.ToArray());
                _currentChapter.OriginalContent = "$X:" + b64;
                _contentDirty = false;
            }
            catch { }
        }

        try { FileService.SaveChapters(App.WorkRoot, _currentNovel.Id, _chapters); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[自动保存] {ex.Message}");
            try
            {
                File.AppendAllText(Path.Combine(App.WorkRoot, "error.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 自动保存失败: {ex}\n");
            }
            catch { }
        }
    }

    private void ScriptTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 已由 SaveCurrentContent / ExitEditMode 统一处理
    }

    private void OriginalTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null || _currentChapter == null) return;
        var config = FileService.LoadConfig(App.WorkRoot);
        if (!config.AutoSaveScript) return;
        var range = new TextRange(OriginalTextBox.Document.ContentStart, OriginalTextBox.Document.ContentEnd);
        using var ms = new MemoryStream();
        range.Save(ms, DataFormats.Xaml);
        var b64 = Convert.ToBase64String(ms.ToArray());
        _currentChapter.OriginalContent = "$X:" + b64;
        FileService.SaveChapters(App.WorkRoot, _currentNovel.Id, _chapters);
    }

    // ===== 多选模式 =====
    private void ToggleMultiSelect_Click(object sender, RoutedEventArgs e)
    {
        _multiSelectMode = !_multiSelectMode;
        _selectedFiles.Clear();
        MultiSelectToggleBtn.Content = _multiSelectMode
            ? "☑ 退出多选" : "☐ 多选";
        CopySelectedBtn.Visibility = _multiSelectMode ? Visibility.Visible : Visibility.Collapsed;
        RefreshImageGrid();
    }

    private void ToggleFileSelection(string filePath, Border card, Border badge)
    {
        if (_selectedFiles.Contains(filePath))
        {
            _selectedFiles.Remove(filePath);
            badge.Visibility = Visibility.Collapsed;
            card.BorderBrush = Brushes.Transparent;
        }
        else
        {
            _selectedFiles.Add(filePath);
            badge.Visibility = Visibility.Visible;
            card.BorderBrush = (Brush)FindResource("PrimaryBrush");
        }
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFiles.Count == 0) return;
        try
        {
            var files = _selectedFiles.ToArray();
            var data = new DataObject(DataFormats.FileDrop, files);
            Clipboard.SetDataObject(data);
            ShowCopyToast($"✓ 已复制 {files.Length} 个文件");
        }
        catch
        {
            ShowCopyToast("✗ 复制失败");
        }
    }
    private void ImageGrid_Drop(object sender, DragEventArgs e)
    {
        if (_currentNovel == null || _currentChapter == null) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var imgDir = FileService.ChapterImagesPath(App.WorkRoot, _currentNovel.MediaFolder, _currentChapter.FolderName);
            var vidDir = FileService.ChapterVideosPath(App.WorkRoot, _currentNovel.MediaFolder, _currentChapter.FolderName);
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLower();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".webp")
                    FileService.CopyFile(file, imgDir);
                else if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv")
                    FileService.CopyFile(file, vidDir);
            }
            RefreshImageGrid();
        }
    }

    private void ImageGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

}
