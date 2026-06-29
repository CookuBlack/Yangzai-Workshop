using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
    private static string _lastImageSize = "1024x768";
    private bool _multiSelectMode;
    private readonly HashSet<string> _selectedFiles = new();
    private string _scriptText = "";
    private string _promptText = "";

    public ScriptPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        ChapterPopup.Closed += (_, _) => _chapterPopupOpen = false;
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
        if (_loaded) return;
        _loaded = true;
        try
        {
            RefreshNovelList();
        }
        catch { /* 初始化静默失败 */ }

        // 加载保存的字体大小
        try
        {
            var config = FileService.LoadConfig(App.WorkRoot);
            OriginalTextBox.FontSize = config.FontSize;
        }
        catch { }

        _savedOriginalWidth = 300;
        _savedScriptWidth = 300;
        _savedImageWidth = 300;

        FixRichTextBoxCarets();
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
            SelectNovel(_novels[0]);
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

        if (novel.HasCoverImage)
        {
            try
            {
                var data = File.ReadAllBytes(FileService.NovelCoverFile(App.WorkRoot, novel.Id));
                var bmp = new BitmapImage();
                bmp.BeginInit();
                using var ms1 = new MemoryStream(data);
                bmp.StreamSource = ms1;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                coverBorder.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
            }
            catch
            {
                coverBorder.Background = ViewHelpers.ParseColor(novel.CoverColor);
            }
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
            if (_currentNovel?.Id == novel.Id)
            { _currentNovel = null; _currentChapter = null; _chapters.Clear(); }
            RefreshNovelList();
            ChapterTabsPanel.Children.Clear();
            try { OriginalTextBox.Document.Blocks.Clear(); } catch { }
            _scriptText = ""; _promptText = "";
            ImageGrid.Children.Clear();
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
                // 清除人物素材和视频文件页面缓存，确保下次访问时重新加载封面
                var nav = NavigationService.Instance;
                nav.ClearPage("Character");
                nav.ClearPage("Video");
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
            _scriptText = ""; _promptText = "";
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
        _currentChapter = chapter;
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
            textRange.Text = _currentChapter.OriginalContent ?? "";
        }
        catch { }

        _scriptText = _currentChapter.ScriptContent ?? "";
        _promptText = _currentChapter.ScriptPrompt ?? "";
        UpdateScriptEditor();
        RefreshImageGrid();
    }

    // ===== 剧本/提示词切换 =====
    private bool _isScriptMode = true;
    private bool _toggling;

    private void ToggleScriptMode()
    {
        if (_toggling || _currentChapter == null) return;
        _toggling = true;

        // 切换前保存当前编辑内容
        if (_isScriptMode)
            _currentChapter.ScriptContent = _scriptText;
        else
            _currentChapter.ScriptPrompt = _promptText;

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
        }
        catch { }
    }

    private void ClearHighlight_Click(object sender, RoutedEventArgs e)
    {
        if (OriginalTextBox.Selection.IsEmpty) return;
        OriginalTextBox.Selection.ApplyPropertyValue(TextElement.BackgroundProperty,
            Brushes.Transparent);
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

        var config = FileService.LoadConfig(App.WorkRoot);
        if (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(config.ApiEndpoint))
        {
            ShowCopyToast("⚠ 请先在「设置→AI 模型配置」中填入 API 地址和密钥");
            return;
        }

        var win = new Window
        {
            Title = "AI 生成图片",
            Width = 500, Height = 360,
            MinWidth = 400, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.CanResize,
            Background = (Brush)FindResource("WindowBackgroundBrush")
        };

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 标题
        grid.Children.Add(new TextBlock
        {
            Text = "输入图片生成提示词",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });

        // 提示词输入框
        var promptBox = new TextBox
        {
            Text = "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13, FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10)
        };
        Grid.SetRow(promptBox, 2);
        grid.Children.Add(promptBox);

        // 底部：尺寸选择 + 生成按钮
        var footer = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };

        // 尺寸选择区域（带边框卡片样式）
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
            Text = "📐 尺寸", FontSize = 12, FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        var sizeBox = new ComboBox
        {
            Width = 140, Height = 28, FontSize = 13,
            IsEditable = true, IsReadOnly = false,
            Text = _lastImageSize,
            ItemsSource = new[] { "1024x768", "1024x1024", "768x1024", "1280x720", "1920x1080", "512x512", "2560x1440" },
            Style = (Style)Application.Current.FindResource("ModernComboBoxStyle"),
            Background = (Brush)FindResource("CardBackgroundBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Padding = new Thickness(8, 0, 8, 0)
        };
        sizePanel.Children.Add(sizeBox);
        sizeCard.Child = sizePanel;
        DockPanel.SetDock(sizeCard, Dock.Left);
        footer.Children.Add(sizeCard);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var genBtn = new Button
        {
            Content = "🎨 开始生成",
            FontSize = 13, Padding = new Thickness(16, 6, 16, 6),
            Style = (Style)FindResource("PrimaryButtonStyle")
        };
        btnPanel.Children.Add(genBtn);
        footer.Children.Add(btnPanel);
        Grid.SetRow(footer, 4);
        grid.Children.Add(footer);

        win.Content = grid;

        var cts = new CancellationTokenSource();

        genBtn.Click += async (_, _) =>
        {
            var prompt = promptBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                ShowCopyToast("⚠ 请输入提示词");
                return;
            }

            genBtn.IsEnabled = false;
            genBtn.Content = "⏳ 生成中...";
            sizeBox.IsEnabled = false;

            try
            {
                var size = sizeBox.Text.Trim();
                _lastImageSize = size;
                var imageUrl = await ApiService.GenerateImageAsync(
                    config.ApiEndpoint, config.ApiKey, prompt, config.ImageModel, size, cts.Token);

                var imageBytes = await ApiService.DownloadImageAsync(imageUrl, cts.Token);

                // 保存到当前章节图像目录
                var targetDir = FileService.ChapterImagesPath(
                    App.WorkRoot, _currentNovel.MediaFolder, _currentChapter.FolderName);
                FileService.EnsureDirectory(targetDir);
                var fileName = $"AI_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                var filePath = Path.Combine(targetDir, fileName);
                await File.WriteAllBytesAsync(filePath, imageBytes, cts.Token);

                // 已经回到 UI 线程，直接操作 UI
                RefreshImageGrid();
                ShowCopyToast("✓ 图片已生成并保存");
                try { win.Close(); } catch { }
            }
            catch (OperationCanceledException)
            {
                ShowCopyToast("⚠ 已取消");
            }
            catch (ApiException ex)
            {
                ShowCopyToast($"⚠ {ex.Message}");
            }
            catch (Exception ex)
            {
                ShowCopyToast($"⚠ 生成失败：{ex.Message}");
            }
            finally
            {
                try { cts.Dispose(); } catch { }
                genBtn.IsEnabled = true;
                genBtn.Content = "🎨 开始生成";
                sizeBox.IsEnabled = true;
            }
        };

        win.Closed += (_, _) => cts.Cancel();
        win.ShowDialog();
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

        int cols = 3;
        for (int i = 0; i < cols; i++)
            ImageGrid.ColumnDefinitions.Add(new ColumnDefinition());

        for (int i = 0; i < images.Count; i++)
        {
            ImageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            string imgPath = images[i];
            string imgName = Path.GetFileName(imgPath);
            try
            {
                // 使用 MemoryStream 加载，避免 Uri 路径编码问题
                var data = File.ReadAllBytes(imgPath);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                using var msImg = new MemoryStream(data);
                bmp.StreamSource = msImg;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 240;
                bmp.EndInit();
                bmp.Freeze();

                var img = new Image
                {
                    Source = bmp, Stretch = Stretch.Uniform,
                    MaxHeight = 170, Tag = imgPath,
                    Cursor = Cursors.Hand
                };

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

                Grid.SetRow(card, i / cols);
                Grid.SetColumn(card, i % cols);
                ImageGrid.Children.Add(card);
            }
            catch { /* 单张加载失败不影响其他 */ }
        }
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
            double w = _savedOriginalWidth > 80 ? _savedOriginalWidth : 300;
            OriginalCol.Width = new GridLength(w, GridUnitType.Pixel);
            OriginalCol.MinWidth = 80;
            OriginalPanel.Visibility = Visibility.Visible;
            Splitter1.Visibility = Visibility.Visible;
        }
        else
        {
            _savedOriginalWidth = OriginalCol.ActualWidth > 40 ? OriginalCol.ActualWidth : _savedOriginalWidth;
            OriginalCol.Width = new GridLength(40, GridUnitType.Pixel);
            OriginalCol.MinWidth = 0;
            Splitter1.Visibility = Visibility.Collapsed;
            OriginalExpandBtn.Visibility = Visibility.Visible;
            return;
        }
        OriginalExpandBtn.Visibility = Visibility.Collapsed;
        FitColumnsToContainer();
    }

    private void ToggleScript_Click(object sender, RoutedEventArgs e)
    {
        _isScriptExpanded = !_isScriptExpanded;
        if (_isScriptExpanded)
        {
            double w = _savedScriptWidth > 80 ? _savedScriptWidth : 300;
            ScriptCol.Width = new GridLength(w, GridUnitType.Pixel);
            ScriptCol.MinWidth = 80;
            ScriptPanel.Visibility = Visibility.Visible;
            Splitter2.Visibility = Visibility.Visible;
        }
        else
        {
            _savedScriptWidth = ScriptCol.ActualWidth > 40 ? ScriptCol.ActualWidth : _savedScriptWidth;
            ScriptCol.Width = new GridLength(40, GridUnitType.Pixel);
            ScriptCol.MinWidth = 0;
            Splitter2.Visibility = Visibility.Collapsed;
            ScriptExpandBtn.Visibility = Visibility.Visible;
            return;
        }
        ScriptExpandBtn.Visibility = Visibility.Collapsed;
        FitColumnsToContainer();
    }

    private void ToggleImage_Click(object sender, RoutedEventArgs e)
    {
        _isImageExpanded = !_isImageExpanded;
        if (_isImageExpanded)
        {
            double w = _savedImageWidth > 80 ? _savedImageWidth : 300;
            ImageCol.Width = new GridLength(w, GridUnitType.Pixel);
            ImageCol.MinWidth = 80;
            ImagePanel.Visibility = Visibility.Visible;
        }
        else
        {
            _savedImageWidth = ImageCol.ActualWidth > 40 ? ImageCol.ActualWidth : _savedImageWidth;
            ImageCol.Width = new GridLength(40, GridUnitType.Pixel);
            ImageCol.MinWidth = 0;
            ImageExpandBtn.Visibility = Visibility.Visible;
            return;
        }
        ImageExpandBtn.Visibility = Visibility.Collapsed;
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
        // 始终从窗口尺寸计算可用空间，避免 ActualWidth 被子列撑大导致误判
        double total = ActualWidth - NovelListCol.ActualWidth - 24;
        if (total <= 0) return;
        int visible = (_isOriginalExpanded ? 1 : 0) + (_isScriptExpanded ? 1 : 0) + (_isImageExpanded ? 1 : 0);
        if (visible == 0) return;
        double collapsed = (3 - visible) * 40;
        double available = total - collapsed - 8;
        if (available < visible * 80) return;

        double[] saved = { _savedOriginalWidth, _savedScriptWidth, _savedImageWidth };
        bool[] show = { _isOriginalExpanded, _isScriptExpanded, _isImageExpanded };
        double savedSum = 0;
        for (int j = 0; j < 3; j++) if (show[j]) savedSum += saved[j];
        if (savedSum <= 0) return;

        // 用已保存宽度判断是否溢出，避免 ActualWidth 未刷新导致误判
        if (savedSum <= available + 2) return;

        double ratio = available / savedSum;
        if (_isOriginalExpanded)
            OriginalCol.Width = new GridLength(Math.Max(80, saved[0] * ratio), GridUnitType.Pixel);
        if (_isScriptExpanded)
            ScriptCol.Width = new GridLength(Math.Max(80, saved[1] * ratio), GridUnitType.Pixel);
        if (_isImageExpanded)
            ImageCol.Width = new GridLength(Math.Max(80, saved[2] * ratio), GridUnitType.Pixel);
    }

    /// <summary>
    /// 拖拽 Splitter1 时：不让脚本列缩到 80 以下，也不让图像列被挤出边界
    /// </summary>
    private void Splitter1_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newScriptW = ScriptCol.ActualWidth - e.HorizontalChange;
        // 脚本列最小 80
        if (newScriptW < 80) { e.Handled = true; return; }
        // 图像列不能被挤出：必须保留至少 80
        double spaceForImage = ContentGrid.ActualWidth -
            (OriginalCol.ActualWidth + e.HorizontalChange) - newScriptW - 8;
        if (_isImageExpanded && spaceForImage < 80) e.Handled = true;
    }

    /// <summary>
    /// 拖拽 Splitter2 时：图像列不超出容器，脚本列也不缩到 80 以下
    /// </summary>
    private void Splitter2_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newScriptW = ScriptCol.ActualWidth - e.HorizontalChange;
        // 脚本列最小 80
        if (newScriptW < 80) { e.Handled = true; return; }
        // 图像列不超出右边界
        double used = (_isOriginalExpanded ? OriginalCol.ActualWidth : 40) + newScriptW;
        double maxImg = ContentGrid.ActualWidth - used - 8;
        if (ImageCol.ActualWidth + e.HorizontalChange > maxImg) e.Handled = true;
    }

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        ImageCol.ClearValue(ColumnDefinition.MaxWidthProperty);
        if (OriginalCol.ActualWidth > 80) _savedOriginalWidth = OriginalCol.ActualWidth;
        if (ScriptCol.ActualWidth > 80) _savedScriptWidth = ScriptCol.ActualWidth;
        if (ImageCol.ActualWidth > 80) _savedImageWidth = ImageCol.ActualWidth;
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

    private void SaveCurrentContent()
    {
        if (_currentNovel == null || _currentChapter == null) return;
        var config = FileService.LoadConfig(App.WorkRoot);
        if (!config.AutoSaveScript) return;
        _currentChapter.ScriptContent = _scriptText;
        _currentChapter.ScriptPrompt = _promptText;
        try { FileService.SaveChapters(App.WorkRoot, _currentNovel.Id, _chapters); }
        catch (Exception ex) { Debug.WriteLine($"[自动保存] {ex.Message}"); }
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
        _currentChapter.OriginalContent = range.Text;
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
