using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

public partial class CharacterPage : UserControl
{
    private List<NovelInfo> _novels = new();
    private NovelInfo? _currentNovel;
    private List<CharacterInfo> _characters = new();
    private CharacterInfo? _currentCharacter;
    private bool _isEditingPersonality;
    private static string _lastImageSize = "1024x768";
    private bool _multiSelectMode;
    private readonly HashSet<string> _selectedFiles = new();
    private CancellationTokenSource? _aiCts;

    public CharacterPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private bool _loaded;

    /// <summary>外部触发刷新：重新加载小说列表（含封面），保持当前选中状态</summary>
    public void RefreshContent()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var curNovelId = _currentNovel?.Id;
            var curCharId = _currentCharacter?.Id;
            RefreshNovels();
            // 重新选中之前的小说和角色
            if (curNovelId != null)
            {
                var novel = _novels.FirstOrDefault(n => n.Id == curNovelId);
                if (novel != null)
                {
                    SelectNovel(novel);
                    if (curCharId != null)
                    {
                        var character = _characters.FirstOrDefault(c => c.Id == curCharId);
                        if (character != null) SelectCharacter(character);
                    }
                }
            }
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        RefreshNovels();
    }

    private void NovelScroller_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        NovelScroller.ScrollToHorizontalOffset(NovelScroller.HorizontalOffset - e.Delta / 2);
        e.Handled = true;
    }

    // ===== Toast 反馈 =====
    private async void Toast(string msg)
    {
        if (ToastText == null || ToastBorder == null) return;
        ToastText.Text = msg;
        ToastBorder.Opacity = 1;
        try
        {
            await System.Threading.Tasks.Task.Delay(1500);
            var a = new System.Windows.Media.Animation.DoubleAnimation(1, 0,
                TimeSpan.FromSeconds(0.3));
            ToastBorder.BeginAnimation(UIElement.OpacityProperty, a);
        }
        catch { /* 页面已卸载时忽略 */ }
    }

    // ===== 小说选择 =====
    private void RefreshNovels()
    {
        _novels = FileService.LoadAllNovels(App.WorkRoot);
        NovelCardPanel.Children.Clear();
        foreach (var novel in _novels)
        {
            var card = new Border
            {
                Background = (Brush)FindResource("CardBackgroundBrush"),
                CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand,
                Tag = novel, Width = 120,
                Margin = new Thickness(2, 0, 2, 0),
                Padding = new Thickness(8, 6, 8, 6),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("BorderBrush")
            };
            var stack = new StackPanel();
            var coverBox = new Border
            {
                Width = 70, Height = 95, CornerRadius = new CornerRadius(4),
                Background = ParseColor(novel.CoverColor),
                Margin = new Thickness(0, 0, 0, 6), ClipToBounds = true
            };
            if (novel.HasCoverImage)
            {
                try
                {
                    var data = File.ReadAllBytes(FileService.NovelCoverFile(App.WorkRoot, novel.Id));
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    using var ms = new MemoryStream(data);
                    bmp.StreamSource = ms;
                    bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
                    coverBox.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                }
                catch { coverBox.Child = CoverFb(novel.Name); }
            }
            else coverBox.Child = CoverFb(novel.Name);
            stack.Children.Add(coverBox);
            stack.Children.Add(new TextBlock
            {
                Text = novel.Name, FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 110
            });
            card.Child = stack;
            card.MouseLeftButtonDown += (s, _) => SelectNovel(novel);
            card.MouseEnter += (s, _) =>
            { if (s is Border b && b.Tag is NovelInfo ni && ni != _currentNovel) b.Opacity = 0.85; };
            card.MouseLeave += (s, _) =>
            { if (s is Border b && b.Tag is NovelInfo ni && ni != _currentNovel) b.Opacity = 0.65; };
            NovelCardPanel.Children.Add(card);
        }
        if (_novels.Count > 0 && _currentNovel == null) SelectNovel(_novels[0]);
    }

    private static TextBlock CoverFb(string n) => new()
    {
        Text = n.Length > 0 ? n[..System.Math.Min(2, n.Length)] : "书",
        Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.Bold,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    private void SelectNovel(NovelInfo novel)
    {
        _currentNovel = novel; _currentCharacter = null;
        foreach (Border c in NovelCardPanel.Children)
        {
            if (c.Tag is NovelInfo ni)
            {
                bool a = ni.Id == novel.Id;
                c.Opacity = a ? 1.0 : 0.65;
                c.BorderBrush = a ? (Brush)FindResource("PrimaryBrush") : (Brush)FindResource("BorderBrush");
                c.BorderThickness = a ? new Thickness(1.5) : new Thickness(1);
            }
        }
        RefreshCharacterList();
    }

    private static SolidColorBrush ParseColor(string h)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)); }
        catch { return new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2)); }
    }

    // ===== 人物列表（WrapPanel 左上到右下） =====
    private void RefreshCharacterList()
    {
        CharacterPanel.Children.Clear(); _characters.Clear();
        if (_currentNovel == null) { ClearDetail(); return; }
        var p = FileService.NovelCharactersPath(App.WorkRoot, _currentNovel.Id);
        if (!Directory.Exists(p)) { ClearDetail(); return; }
        foreach (var d in FileService.GetDirectories(p))
        {
            var info = FileService.ReadJson<CharacterInfo>(Path.Combine(d, "info.json"));
            _characters.Add(info ?? new CharacterInfo
            { Id = Path.GetFileName(d), Name = Path.GetFileName(d), Personality = "暂无性格设定" });
        }
        if (_characters.Count == 0) { ClearDetail(); return; }
        foreach (var c in _characters) CharacterPanel.Children.Add(CreateCharacterCard(c));
        if (_currentCharacter == null || !_characters.Any(c => c.Id == _currentCharacter.Id))
            SelectCharacter(_characters[0]);
    }

    private void ClearDetail()
    {
        DetailScroll.Visibility = Visibility.Collapsed;
        NoSelectionHint.Visibility = Visibility.Visible;
    }

    private Border CreateCharacterCard(CharacterInfo ch)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand,
            Tag = ch, Width = 90, Height = 105,
            Margin = new Thickness(3), Padding = new Thickness(4),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            ClipToBounds = true
        };
        // 显式 RectangleGeometry Clip 确保圆角在任何渲染模式下都生效
        card.Loaded += (s, _) =>
        {
            var b = (Border)s;
            b.Clip = new RectangleGeometry(new Rect(0, 0, b.ActualWidth, b.ActualHeight), 12, 12);
        };
        card.SizeChanged += (s, _) =>
        {
            var b = (Border)s;
            b.Clip = new RectangleGeometry(new Rect(0, 0, b.ActualWidth, b.ActualHeight), 12, 12);
        };
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var av = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(32),
            Background = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0xC0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0), ClipToBounds = true
        };
        var avP = FileService.CharacterAvatarFile(App.WorkRoot, _currentNovel!.Id, ch.Id);
        if (File.Exists(avP))
        {
            try
            {
                var data = File.ReadAllBytes(avP);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                using var ms = new MemoryStream(data);
                bmp.StreamSource = ms;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 80; bmp.EndInit();
                bmp.Freeze();
                av.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
            catch { av.Child = Fallback(ch.Name); }
        }
        else av.Child = Fallback(ch.Name);
        Grid.SetRow(av, 0);
        g.Children.Add(av);

        var name = new TextBlock
        {
            Text = ch.Name, FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2), MaxWidth = 82
        };
        Grid.SetRow(name, 1);
        g.Children.Add(name);

        card.Child = g;
        card.MouseLeftButtonDown += (s, e) => SelectCharacter(ch);
        card.MouseRightButtonDown += (s, e) =>
        {
            var m = new ContextMenu();
            var d = new MenuItem { Header = "删除角色" };
            d.Click += (_, _) => ConfirmDeleteCharacter(ch);
            m.Items.Add(d); m.IsOpen = true;
        };
        return card;
    }

    private static TextBlock Fallback(string n) => new()
    {
        Text = n.Length > 0 ? n[..System.Math.Min(1, n.Length)] : "?",
        Foreground = Brushes.White, FontSize = 26, FontWeight = FontWeights.Bold,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    private void SelectCharacter(CharacterInfo ch)
    {
        _currentCharacter = ch;
        DetailScroll.Visibility = Visibility.Visible;
        NoSelectionHint.Visibility = Visibility.Collapsed;
        CharNameDisplay.Text = ch.Name;
        PersonalityDisplay.Text = string.IsNullOrWhiteSpace(ch.Personality) ? "暂无性格设定" : ch.Personality;
        _isEditingPersonality = false;
        PersonalityEditArea.Visibility = Visibility.Collapsed;
        PersonalityDisplayBorder.Visibility = Visibility.Visible;
        RefreshCharacterAvatar();
        RefreshCharacterImages();
        foreach (Border c in CharacterPanel.Children)
        {
            if (c.Tag is CharacterInfo ci)
            {
                bool a = ci.Id == ch.Id;
                c.Opacity = a ? 1.0 : 0.6;
                c.BorderThickness = a ? new Thickness(1.5) : new Thickness(1);
            }
        }
    }

    private void RefreshCharacterAvatar()
    {
        if (_currentNovel == null || _currentCharacter == null) return;
        var p = FileService.CharacterAvatarFile(App.WorkRoot, _currentNovel.Id, _currentCharacter.Id);
        if (File.Exists(p))
        {
            try
            {
                var data = File.ReadAllBytes(p);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                using var ms = new MemoryStream(data);
                bmp.StreamSource = ms;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                AvatarBorder.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                CharAvatarPlaceholder.Visibility = Visibility.Collapsed; return;
            }
            catch { }
        }
        AvatarBorder.Background = (Brush)FindResource("PrimaryBrush");
        CharAvatarPlaceholder.Visibility = Visibility.Visible;
    }

    private void Avatar_Click(object sender, MouseButtonEventArgs e)
    {
        if (_currentNovel == null || _currentCharacter == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp", Title = "选择角色头像" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var cw = new CropWindow(dlg.FileName) { Owner = Window.GetWindow(this) };
            cw.Cropped += img =>
            {
                var dir = FileService.CharacterPath(App.WorkRoot, _currentNovel.Id, _currentCharacter.Id);
                FileService.EnsureDirectory(dir);
                var ap = FileService.CharacterAvatarFile(App.WorkRoot, _currentNovel.Id, _currentCharacter.Id);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using var fs = new FileStream(ap, FileMode.Create, FileAccess.Write, FileShare.Read);
                enc.Save(fs);
                fs.Flush(true);
                var frozen = img.Clone();
                frozen.Freeze();
                AvatarBorder.Background = new ImageBrush(frozen) { Stretch = Stretch.UniformToFill };
                CharAvatarPlaceholder.Visibility = Visibility.Collapsed;
                UpdateCharacterCardAvatar(_currentCharacter.Id, frozen);
            };
            cw.Show();
        }
        catch { }
    }

    private static void ApplyCardClip(Border card)
    {
        if (card.ActualWidth > 0 && card.ActualHeight > 0)
            card.Clip = new RectangleGeometry(
                new Rect(0, 0, card.ActualWidth, card.ActualHeight), 6, 6);
    }

    /// <summary>更新人物列表中指定角色的头像（不重建整个列表）</summary>
    private void UpdateCharacterCardAvatar(string charId, BitmapSource source)
    {
        foreach (Border card in CharacterPanel.Children)
        {
            if (card.Tag is CharacterInfo ci && ci.Id == charId)
            {
                if (card.Child is Grid g && g.Children.Count > 0 && g.Children[0] is Border av)
                {
                    av.Child = new Image { Source = source, Stretch = Stretch.UniformToFill };
                    av.Background = Brushes.Transparent;
                    av.ClipToBounds = true;
                }
                break;
            }
        }
    }

    // ===== 重命名 =====
    private void RenameCharacter_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null || _currentCharacter == null) return;
        var dlg = new InputDialog("重命名角色", "新名称：", _currentCharacter.Name)
        { Owner = Window.GetWindow(this) };
        dlg.Confirmed += name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return;
        var nn = name.Trim();
        var oldCharId = _currentCharacter.Id;
        var sid = Sanitize(nn);
        var old = FileService.CharacterPath(App.WorkRoot, _currentNovel.Id, oldCharId);
        var np = Path.Combine(Path.GetDirectoryName(old)!, sid);
        if (!string.Equals(old, np, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(np))
            { sid += "_" + System.Guid.NewGuid().ToString()[..6];
              np = Path.Combine(Path.GetDirectoryName(old)!, sid); }
            try { Directory.Move(old, np); }
            catch
            { Toast("✗ 重命名失败"); return; }
        }
        _currentCharacter.Name = nn; _currentCharacter.Id = sid;
        FileService.WriteJson(Path.Combine(np, "info.json"), _currentCharacter);

        // 同步移动 Image/人物素材 文件夹
        var oldImgDir = FileService.CharacterImagesPath(App.WorkRoot, _currentNovel.MediaFolder, oldCharId);
        if (Directory.Exists(oldImgDir))
        {
            var newImgDir = FileService.CharacterImagesPath(App.WorkRoot, _currentNovel.MediaFolder, sid);
            try
            {
                FileService.EnsureDirectory(Path.GetDirectoryName(newImgDir)!);
                if (Directory.Exists(newImgDir))
                    Directory.Delete(newImgDir, true);
                Directory.Move(oldImgDir, newImgDir);
            }
            catch { /* 移动失败不阻断流程 */ }
        }

        RefreshCharacterList(); SelectCharacter(_currentCharacter);
        Toast("✓ 重命名成功");
        };
        dlg.Show();
    }

    private static string Sanitize(string n)
    {
        var inv = Path.GetInvalidFileNameChars();
        var s = new string(n.Select(c => inv.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(s) ? "unnamed" : s;
    }

    private void DeleteCharacter_Click(object sender, RoutedEventArgs e)
    { if (_currentCharacter != null) ConfirmDeleteCharacter(_currentCharacter); }

    private void ConfirmDeleteCharacter(CharacterInfo ch)
    {
        if (_currentNovel == null) return;
        if (!MessageDialog.Confirm("确认删除", $"确定删除「{ch.Name}」？")) return;
        try
        {
            var p = FileService.CharacterPath(App.WorkRoot, _currentNovel.Id, ch.Id);
            if (Directory.Exists(p)) FileService.MoveToTrash(p);
        }
        catch { Toast("✗ 删除失败"); return; }
        _currentCharacter = null; RefreshCharacterList();
        Toast("✓ 已删除");
    }

    // ===== 性格设定 =====
    private void EditPersonality_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCharacter == null) return;
        _isEditingPersonality = true;
        PersonalityEditBox.Text = _currentCharacter.Personality ?? "";
        PersonalityDisplayBorder.Visibility = Visibility.Collapsed;
        PersonalityEditArea.Visibility = Visibility.Visible;
    }

    private void SavePersonality_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null || _currentCharacter == null) return;
        _currentCharacter.Personality = PersonalityEditBox.Text;
        var cp = FileService.CharacterPath(App.WorkRoot, _currentNovel.Id, _currentCharacter.Id);
        FileService.EnsureDirectory(cp);
        FileService.WriteJson(Path.Combine(cp, "info.json"), _currentCharacter);
        _isEditingPersonality = false;
        PersonalityDisplay.Text = string.IsNullOrWhiteSpace(_currentCharacter.Personality)
            ? "暂无性格设定" : _currentCharacter.Personality;
        PersonalityDisplayBorder.Visibility = Visibility.Visible;
        PersonalityEditArea.Visibility = Visibility.Collapsed;
        Toast("✓ 已保存");
    }

    private void CancelPersonality_Click(object sender, RoutedEventArgs e)
    {
        _isEditingPersonality = false;
        PersonalityDisplayBorder.Visibility = Visibility.Visible;
        PersonalityEditArea.Visibility = Visibility.Collapsed;
    }

    private async void AiGeneratePersonality_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null || _currentCharacter == null) return;

        // 如果正在生成中，点击变为取消
        if (_aiCts != null)
        {
            _aiCts.Cancel();
            return;
        }

        var config = FileService.LoadConfig(App.WorkRoot);
        if (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(config.ApiEndpoint))
        {
            Toast("⚠ 请先在「设置→AI 模型配置」中填入 API 地址和密钥");
            return;
        }

        // 收集小说原文作为上下文
        string novelContext = "";
        try
        {
            var originalFile = FileService.NovelOriginalFile(App.WorkRoot, _currentNovel.Id);
            if (File.Exists(originalFile))
            {
                var fullText = File.ReadAllText(originalFile, System.Text.Encoding.UTF8);
                // 取前 4000 字作为上下文参考
                novelContext = fullText.Length > 4000 ? fullText[..4000] + "\n…(后续内容省略)" : fullText;
            }
        }
        catch { /* 读不到原文就用空上下文 */ }

        var sysPrompt = "你是一位资深的文学角色分析师，擅长从小说中提炼角色的性格特点、行为模式和心理动机。请用流畅优美的中文撰写，使用纯文本格式，不要使用任何 Markdown 标记符号（如 ##、**、- 等），用自然段落加小标题的方式组织内容。";

        var userMsg = $"请为以下角色撰写详细的性格设定：\n\n"
            + $"角色名称：{_currentCharacter.Name}\n"
            + $"所属小说：{_currentNovel.Name}\n\n";

        if (!string.IsNullOrWhiteSpace(novelContext))
        {
            userMsg += $"小说原文片段（供参考）：\n---\n{novelContext}\n---\n\n";
        }

        userMsg += "请从以下维度进行全面分析，每个维度用「【】」括起的小标题开头，下面跟 2-4 句描述：\n"
            + "【性格特点】外向/内向、理性/感性、主导/随和等维度\n"
            + "【行为习惯】日常举止、说话方式、待人接物的风格\n"
            + "【价值观与动机】人生追求、核心信念、行为驱动力\n"
            + "【优点与缺点】闪光点与性格弱点\n"
            + "【外貌特征】体态、容貌、着装风格\n"
            + "【背景故事】成长经历、关键事件\n\n"
            + "请用纯文本输出，不要使用任何 Markdown 标记符号，每个小节之间空一行，总字数 800 字以内。";

        // 自动进入编辑模式
        _isEditingPersonality = true;
        PersonalityDisplayBorder.Visibility = Visibility.Collapsed;
        PersonalityEditArea.Visibility = Visibility.Visible;
        PersonalityEditBox.Text = "";

        _aiCts = new CancellationTokenSource();
        AiPersonalityBtn.Content = "⏹ 取消";
        var sb = new System.Text.StringBuilder();

        try
        {
            await ApiService.ChatStreamAsync(
                config.ApiEndpoint, config.ApiKey, config.ApiModel,
                sysPrompt, userMsg,
                onToken: token =>
                {
                    lock (sb) sb.Append(token);
                    var soFar = sb.ToString();
                    Dispatcher.BeginInvoke(() =>
                    {
                        PersonalityEditBox.Text = soFar;
                        PersonalityEditBox.ScrollToEnd();
                    });
                },
                cancel: _aiCts.Token);

            _ = Dispatcher.BeginInvoke(() =>
            {
                var result = sb.ToString();
                PersonalityEditBox.Text = result;
                Toast("✓ 性格设定生成完成");
            });
        }
        catch (OperationCanceledException)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                PersonalityEditBox.Text = sb.ToString();
                Toast("⚠ 已取消生成");
            });
        }
        catch (ApiException ex)
        {
            _ = Dispatcher.BeginInvoke(() => Toast($"⚠ {ex.Message}"));
        }
        catch (Exception ex)
        {
            _ = Dispatcher.BeginInvoke(() => Toast($"⚠ 生成失败：{ex.Message}"));
        }
        finally
        {
            _aiCts?.Dispose();
            _aiCts = null;
            AiPersonalityBtn.Content = "🤖 AI 生成";
        }
    }

    private void CopyPersonality_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_isEditingPersonality ? PersonalityEditBox.Text : PersonalityDisplay.Text);
        Toast("✓ 已复制");
    }

    // ===== 外/内滚动路由 =====
    private void DetailScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 优先：图像区内 → 滚内层图像列表，到边界后滚动外层
        if (CharImageScroller.IsVisible && CharImageScroller.IsMouseOver)
        {
            double delta = e.Delta;
            var inner = CharImageScroller;
            double newOff = inner.VerticalOffset - delta;
            double maxOff = inner.ScrollableHeight;
            if (newOff < 0) { inner.ScrollToVerticalOffset(0); delta = -inner.VerticalOffset; }
            else if (newOff > maxOff) { inner.ScrollToVerticalOffset(maxOff); delta = inner.VerticalOffset - maxOff; }
            else { inner.ScrollToVerticalOffset(newOff); delta = 0; }
            // 剩余滚动量传给外层
            if (delta != 0)
                DetailScroll.ScrollToVerticalOffset(DetailScroll.VerticalOffset - delta);
            e.Handled = true;
        }
    }

    /// <summary>性格设定区滚动透传到外层</summary>
    private void PersonalityInner_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer inner)
        {
            double newOff = inner.VerticalOffset - e.Delta;
            double maxOff = inner.ScrollableHeight;
            if (newOff < 0 || newOff > maxOff)
            {
                // 内层到边界，余量给外层
                double remain = newOff < 0 ? -newOff : (newOff - maxOff);
                DetailScroll.ScrollToVerticalOffset(DetailScroll.VerticalOffset - (e.Delta > 0 ? -remain : remain));
            }
        }
    }

    // ===== 角色图像素材 =====
    private void CharImageScroller_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void RefreshCharacterImages()
    {
        CharImagePanel.Children.Clear();
        if (_currentNovel == null || _currentCharacter == null) return;
        var p = FileService.CharacterImagesPath(App.WorkRoot, _currentNovel.MediaFolder, _currentCharacter.Id);
        var imgs = FileService.GetFiles(p, ".png", ".jpg", ".jpeg", ".webp");
        foreach (var ip in imgs)
        {
            string nm = Path.GetFileName(ip);
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.UriSource = new Uri(ip);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 180; bmp.EndInit();

                var card = new Border
                {
                    Width = 170, Margin = new Thickness(4),
                    CornerRadius = new CornerRadius(8),
                    ClipToBounds = true, Background = Brushes.Transparent
                };
                card.Loaded += (_, _) => ViewHelpers.ApplyRoundedClip(card);
                card.SizeChanged += (_, _) => ViewHelpers.ApplyRoundedClip(card);
                var cs = new StackPanel();
                var ia = new Grid();
                var charImg = new Image { Source = bmp, Stretch = Stretch.Uniform, MaxHeight = 150, Tag = ip, Cursor = Cursors.Hand };
                // 选中遮罩
                var selOverlay = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x40, 0x4A, 0x90, 0xE2)),
                    BorderBrush = (Brush)FindResource("PrimaryBrush"),
                    BorderThickness = new Thickness(3),
                    CornerRadius = new CornerRadius(4),
                    Visibility = _selectedFiles.Contains(ip) ? Visibility.Visible : Visibility.Collapsed,
                    Child = new TextBlock
                    {
                        Text = "\uE73E", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 24, Foreground = (Brush)FindResource("PrimaryBrush"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                charImg.MouseLeftButtonDown += (s, me) =>
                {
                    if (_multiSelectMode)
                    {
                        ToggleFileSelection(ip, selOverlay);
                        me.Handled = true; // 阻止冒泡到卡片层打开查看器
                    }
                };
                card.MouseLeftButtonDown += (_, _) =>
                {
                    if (!_multiSelectMode)
                        ViewHelpers.ShowImageViewer(ip, Window.GetWindow(this));
                };
                ia.Children.Add(charImg);
                ia.Children.Add(selOverlay);

                var tb = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 4), Opacity = 0
                };
                tb.Children.Add(ImgBtn("复制", () =>
                {
                    try
                    {
                        var data = File.ReadAllBytes(ip);
                        var full = new BitmapImage();
                        full.BeginInit();
                        using var msCopy = new MemoryStream(data);
                        full.StreamSource = msCopy;
                        full.CacheOption = BitmapCacheOption.OnLoad;
                        full.EndInit();
                        Clipboard.SetImage(full);
                        Toast("✓ 已复制");
                    }
                    catch { Toast("✗ 复制失败"); }
                }));
                tb.Children.Add(ImgBtn("改名", () =>
                {
                    var d = new InputDialog("重命名", "名称（不含扩展名）：",
                        Path.GetFileNameWithoutExtension(ip))
                    { Owner = Window.GetWindow(this) };
                    d.Confirmed += name =>
                    {
                        if (string.IsNullOrWhiteSpace(name)) return;
                        var np2 = Path.Combine(Path.GetDirectoryName(ip)!,
                            name.Trim() + Path.GetExtension(ip));
                        if (!string.Equals(ip, np2, StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(np2)) FileService.DeleteFile(np2);
                            File.Move(ip, np2);
                            RefreshCharacterImages();
                            Toast("✓ 已改名");
                        }
                    };
                    d.Show();
                }));
                tb.Children.Add(ImgBtn("删除", () =>
                {
                    try { FileService.DeleteFile(ip); RefreshCharacterImages(); Toast("✓ 已删除"); }
                    catch { Toast("✗ 删除失败"); }
                }));
                ia.Children.Add(tb);
                cs.Children.Add(ia);
                cs.Children.Add(new TextBlock
                {
                    Text = nm, FontSize = 10,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0), MaxWidth = 160
                });
                card.Child = cs;
                card.MouseEnter += (_, _) => tb.Opacity = 1;
                card.MouseLeave += (_, _) => tb.Opacity = 0;
                CharImagePanel.Children.Add(card);
            }
            catch { }
        }
    }

    private static Button ImgBtn(string text, Action click)
    {
        var b = new Button
        {
            Content = text, FontSize = 10,
            Width = 36, Height = 22, Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x33, 0x33, 0x33)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0)
        };
        b.Click += (_, _) => click();
        b.MouseEnter += (s, _) => ((Button)s).Background =
            new SolidColorBrush(Color.FromArgb(0xF0, 0x55, 0x55, 0x55));
        b.MouseLeave += (s, _) => ((Button)s).Background =
            new SolidColorBrush(Color.FromArgb(0xD0, 0x33, 0x33, 0x33));
        return b;
    }

    private void UploadImage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null || _currentCharacter == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp", Multiselect = true, Title = "选择角色图片素材" };
        if (dlg.ShowDialog() != true) return;
        var t = FileService.CharacterImagesPath(App.WorkRoot, _currentNovel.MediaFolder, _currentCharacter.Id);
        foreach (var f in dlg.FileNames) FileService.CopyFile(f, t);
        RefreshCharacterImages();
        Toast("✓ 已添加");
    }

    private void CharAiGenerateImage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null || _currentCharacter == null) return;

        var config = FileService.LoadConfig(App.WorkRoot);
        if (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(config.ApiEndpoint))
        {
            Toast("⚠ 请先在「设置→AI 模型配置」中填入 API 地址和密钥");
            return;
        }

        // 预填角色性格设定作为提示词参考
        var personalityHint = _currentCharacter.Personality ?? "";

        var win = new Window
        {
            Title = $"AI 生成角色图片 - {_currentCharacter.Name}",
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

        grid.Children.Add(new TextBlock
        {
            Text = $"为角色「{_currentCharacter.Name}」输入图片生成提示词",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });

        var promptBox = new TextBox
        {
            Text = personalityHint,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
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

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
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
                Toast("⚠ 请输入提示词");
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

                var targetDir = FileService.CharacterImagesPath(
                    App.WorkRoot, _currentNovel.MediaFolder, _currentCharacter.Id);
                FileService.EnsureDirectory(targetDir);
                var fileName = $"AI_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                var filePath = Path.Combine(targetDir, fileName);
                await File.WriteAllBytesAsync(filePath, imageBytes, cts.Token);

                // 已经回到 UI 线程，直接操作 UI
                RefreshCharacterImages();
                Toast("✓ 图片已生成并保存");
                try { win.Close(); } catch { }
            }
            catch (OperationCanceledException)
            {
                Toast("⚠ 已取消");
            }
            catch (ApiException ex)
            {
                Toast($"⚠ {ex.Message}");
            }
            catch (Exception ex)
            {
                Toast($"⚠ 生成失败：{ex.Message}");
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

    // ===== 多选模式 =====
    private void ToggleCharMultiSelect_Click(object sender, RoutedEventArgs e)
    {
        _multiSelectMode = !_multiSelectMode;
        _selectedFiles.Clear();
        CharMultiSelectBtn.Content = _multiSelectMode
            ? "☑ 退出多选" : "☐ 多选";
        CharCopySelectedBtn.Visibility = _multiSelectMode ? Visibility.Visible : Visibility.Collapsed;
        RefreshCharacterImages();
    }

    private void ToggleFileSelection(string filePath, Border overlay)
    {
        if (_selectedFiles.Contains(filePath))
        {
            _selectedFiles.Remove(filePath);
            overlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            _selectedFiles.Add(filePath);
            overlay.Visibility = Visibility.Visible;
        }
    }

    private void CopyCharSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFiles.Count == 0) return;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, _selectedFiles.ToArray());
            Clipboard.SetDataObject(data);
            Toast($"✓ 已复制 {_selectedFiles.Count} 个文件");
        }
        catch { Toast("✗ 复制失败"); }
    }

    private void Avatar_Drop(object sender, DragEventArgs e)
    {
        if (_currentNovel == null || _currentCharacter == null) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var firstImg = files.FirstOrDefault(f =>
            {
                var ext = Path.GetExtension(f).ToLower();
                return ext is ".png" or ".jpg" or ".jpeg" or ".webp";
            });
            if (firstImg == null) return;
            try
            {
                var cw = new CropWindow(firstImg) { Owner = Window.GetWindow(this) };
                cw.Cropped += img =>
                {
                    var dir = FileService.CharacterPath(App.WorkRoot, _currentNovel.Id, _currentCharacter.Id);
                    FileService.EnsureDirectory(dir);
                    var ap = FileService.CharacterAvatarFile(App.WorkRoot, _currentNovel.Id, _currentCharacter.Id);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(img));
                    using var fs = new FileStream(ap, FileMode.Create, FileAccess.Write, FileShare.Read);
                    enc.Save(fs);
                    fs.Flush(true);
                    var frozen = img.Clone();
                    frozen.Freeze();
                    Dispatcher.Invoke(() =>
                    {
                        AvatarBorder.Background = new ImageBrush(frozen) { Stretch = Stretch.UniformToFill };
                        CharAvatarPlaceholder.Visibility = Visibility.Collapsed;
                    });
                    Toast("✓ 头像已更新");
                };
                cw.Show();
            }
            catch { Toast("✗ 头像导入失败"); }
        }
    }

    private void Avatar_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void CharImage_Drop(object sender, DragEventArgs e)
    {
        if (_currentNovel == null || _currentCharacter == null) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var targetDir = FileService.CharacterImagesPath(App.WorkRoot, _currentNovel.MediaFolder, _currentCharacter.Id);
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLower();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".webp")
                    FileService.CopyFile(file, targetDir);
            }
            RefreshCharacterImages();
            Toast("✓ 素材已导入");
        }
    }

    private void CharImage_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void AddCharacter_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNovel == null) return;
        var nc = new CharacterInfo
        { Id = "新角色_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss"), Name = "新角色", Personality = "" };
        var cp = FileService.CharacterPath(App.WorkRoot, _currentNovel.Id, nc.Id);
        if (Directory.Exists(cp))
        { nc.Id += "_" + System.Guid.NewGuid().ToString()[..4];
          cp = FileService.CharacterPath(App.WorkRoot, _currentNovel.Id, nc.Id); }
        FileService.EnsureDirectory(cp);
        FileService.EnsureDirectory(Path.Combine(cp, "images"));
        FileService.WriteJson(Path.Combine(cp, "info.json"), nc);
        RefreshCharacterList(); SelectCharacter(nc);
    }
}
