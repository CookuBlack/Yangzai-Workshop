using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Views;

namespace YangzaiWorkshop.Services;

/// <summary>
/// 宠物桥接服务：把宠物（DesktopPet 类库）的回调节点指向主程序实现。
/// 提供宠物启动、音乐控制、AI 生成图片/视频/对话、队列与资源管理等入口。
/// AI 生成结果统一保存到宠物资源目录（FileService.PetResourcesPath）。
/// </summary>
public static class PetService
{
    private static bool _initialized;

    /// <summary>启动时调用一次：把宠物回调节点接到主程序实现。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        DesktopPet.PetActions.ToggleMusic = () => MusicPlayerService.Instance.TogglePlayPause();
        DesktopPet.PetActions.OpenGenerateImage = OpenGenerateImage;
        DesktopPet.PetActions.OpenGenerateVideo = OpenGenerateVideo;
        DesktopPet.PetActions.OpenChat = OpenChat;
        DesktopPet.PetActions.OpenQueue = OpenQueue;
        DesktopPet.PetActions.OpenResources = OpenResources;

        // 常驻系统托盘图标（小羊图标），应用启动即存在，独立于宠物窗口
        try
        {
            DesktopPet.PetTray.Initialize();
            System.Windows.Application.Current.Exit += (_, _) => DesktopPet.PetTray.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[宠物托盘] {ex.Message}");
        }
    }

    // ===== 宠物显示 / 隐藏 =====

    public static void ShowPet() => DesktopPet.PetHost.Show();
    public static void HidePet() => DesktopPet.PetHost.Hide();
    public static bool IsPetVisible => DesktopPet.PetHost.IsVisible;

    public static void TogglePet() => DesktopPet.PetHost.Toggle();

    public static void ClosePet() => DesktopPet.PetHost.Close();

    // ===== 队列 / 资源 =====

    public static void OpenQueue()
    {
        try
        {
            new AiTaskQueueWindow().Show();
        }
        catch (Exception ex)
        {
            MainWindow.Notify($"⚠ 无法打开队列：{ex.Message}", success: false);
        }
    }

    public static void OpenResources()
    {
        try
        {
            new PetResourceWindow().Show();
        }
        catch (Exception ex)
        {
            MainWindow.Notify($"⚠ 无法打开宠物资源：{ex.Message}", success: false);
        }
    }

    // ===== AI 生成图片 =====

    private static void OpenGenerateImage()
    {
        var config = FileService.LoadConfig(App.WorkRoot);
        var win = CreateDialog("AI 生成图片", 1010, 620);
        win.MinWidth = 980;
        win.MinHeight = 520;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 标题
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });                    // 1 间距
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 2 主体（左栏|中央|右栏）
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });                    // 3 间距
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 4 底部
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                  // 0 左栏
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 1 中央
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                  // 2 右栏

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var header = new TextBlock
        {
            Text = config.DefaultImageProvider == "ComfyUI" ? "输入图片生成提示词 · 本地 ComfyUI" : "输入图片生成提示词 · 云端 API",
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(header, 0);
        headerGrid.Children.Add(header);
        Grid.SetRow(headerGrid, 0);
        Grid.SetColumnSpan(headerGrid, 3);
        Grid.SetColumnSpan(headerGrid, 3);
        grid.Children.Add(headerGrid);

        var promptBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13,
            Foreground = Brush("TextPrimaryBrush"),
            Background = Brush("CardBackgroundBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10)
        };
        // 提示词放入中央子网格（三栏布局构建 center 时再挂载）

        // ===== 参考图区域（0 张=文生图，1 张=图生图，多张=多图编辑） =====
        var refImages = new List<string>(); // Data URI Base64
        var refPaths = new List<string>();  // 与 refImages 对应的源文件路径（用于历史回填）
        var refPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        var addRefBtn = new Button
        {
            Content = "🖼️ 添加参考图", FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Style = Style("SecondaryButtonStyle"),
            ToolTip = "选择本地图片作为参考：1 张=图生图，多张=多图编辑/合成（在提示词中说明组合方式）"
        };
        refPanel.Children.Add(addRefBtn);
        var assetRefBtn = new Button
        {
            Content = "📁 宠物资产", FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Style = Style("SecondaryButtonStyle"),
            ToolTip = "从宠物资源目录中选择参考图"
        };
        refPanel.Children.Add(assetRefBtn);
        var clearRefBtn = new Button
        {
            Content = "✕ 清除", FontSize = 11,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed,
            Style = Style("SecondaryButtonStyle")
        };
        refPanel.Children.Add(clearRefBtn);
        var refHintText = new TextBlock
        {
            Text = "可添加 1 张（图生图）或多张（多图编辑）参考图",
            FontSize = 10.5,
            Foreground = Brush("TextTertiaryBrush"),
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
            // owner 传 AI 小窗口，避免对话框关闭后激活主窗口触发其误最小化
            if (dlg.ShowDialog(win) != true) return;
            foreach (var file in dlg.FileNames)
            {
                ViewHelpers.AddReferenceThumb(refPanel, file, refImages,
                    () => ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn),
                    refPaths: refPaths);
            }
            ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn);
        };
        assetRefBtn.Click += (_, _) =>
        {
            try
            {
                var dir = FileService.PetResourcesPath(App.WorkRoot);
                var paths = new List<string>();
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif")
                            paths.Add(f);
                    }
                }
                if (paths.Count == 0) { MainWindow.Notify("⚠ 宠物资源目录还没有图片，请先通过 AI 生图生成", success: false); return; }
                var picker = new AssetPickerWindow(paths, "选择宠物资源图片作为参考图（可多选）", multiSelect: true) { Owner = win };
                if (picker.ShowDialog() != true) return;
                ViewHelpers.AddReferenceThumbsAsync(refPanel, picker.OrderedPaths, refImages,
                    () => ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn),
                    refPaths: refPaths);
                ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn);
            }
            catch (Exception ex)
            {
                MainWindow.Notify($"⚠ 无法打开宠物资产：{ex.Message}", success: false);
            }
        };
        clearRefBtn.Click += (_, _) =>
        {
            refImages.Clear();
            refPaths.Clear();
            for (int i = refPanel.Children.Count - 1; i >= 0; i--)
            {
                if (refPanel.Children[i] is Border b && b.Tag is string t && t == "refthumb")
                    refPanel.Children.RemoveAt(i);
            }
            ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn);
        };

        var refRow = new Grid();
        refRow.Children.Add(refPanel);

        // ===== 右侧边栏：宠物资产（点击按顺序编号，作为参考图顺序） =====
        var petDir = FileService.PetResourcesPath(App.WorkRoot);
        var petPaths = new List<string>();
        if (Directory.Exists(petDir))
            foreach (var f in Directory.GetFiles(petDir))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif") petPaths.Add(f);
            }
        var assetPanel = new AssetPanel("宠物资产", petPaths, maxCount: 6);

        void RemoveRefThumbs()
        {
            for (int i = refPanel.Children.Count - 1; i >= 0; i--)
                if (refPanel.Children[i] is Border b && b.Tag is string t && t == "refthumb")
                    refPanel.Children.RemoveAt(i);
        }
        // 右侧栏选择顺序 → 重建参考图列表（含手动添加的本地参考图共存于 refImages）
        void RebuildRefsFromAssets()
        {
            refImages.Clear();
            refPaths.Clear();
            RemoveRefThumbs();
            ViewHelpers.AddReferenceThumbsAsync(refPanel, assetPanel.SelectedOrder, refImages,
                () => ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn),
                refPaths: refPaths);
            ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn);
        }
        assetPanel.SelectionChanged = RebuildRefsFromAssets;
        // 清除参考图（同时清空右侧栏资产选择）
        clearRefBtn.Click += (_, _) =>
        {
            assetPanel.ClearSelection();
            refImages.Clear();
            refPaths.Clear();
            RemoveRefThumbs();
            ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn);
        };

        // ===== 左侧边栏：文本导入/编辑/选区加入/导出 + 默认提示词 =====
        void AppendPromptText(string t)
        {
            var cur = promptBox.Text;
            promptBox.Text = string.IsNullOrWhiteSpace(cur) ? t : cur.TrimEnd() + "\n" + t;
            promptBox.CaretIndex = promptBox.Text.Length;
            promptBox.Focus();
        }
        var promptPanel = new PromptPanel(win, "Image")
        {
            AppendToPrompt = AppendPromptText
        };

        // ===== 中央：提示词输入 + 参考图区 =====
        var center = new Grid();
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 0 提示词
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });                  // 1 间距
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // 2 参考图
        Grid.SetRow(promptBox, 0);
        center.Children.Add(promptBox);
        Grid.SetRow(refRow, 2);
        center.Children.Add(refRow);

        // 「优化提示词」按钮（标题行右侧），有参考图时结合参考图内容优化
        var optimizeBtn = CreateOptimizeButton(promptBox,
            () => refImages.Count > 0 ? (IReadOnlyList<string>)refImages.ToArray() : null,
            "image");
        Grid.SetColumn(optimizeBtn, 1);
        headerGrid.Children.Add(optimizeBtn);

        // ===== 底部 =====
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ratioBox = Combo(ViewHelpers.ImageRatios, "1:1", 76);
        var levelBox = Combo(ViewHelpers.ImageLevels, "1K", 62);
        footer.Children.Add(ratioBox);
        footer.Children.Add(levelBox);
        var historyBtn = new Button
        {
            Content = "🕘 历史", FontSize = 12, Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(10, 0, 0, 0),
            Style = Style("SecondaryButtonStyle"),
            ToolTip = "查看历史记录，点击一条自动回填提示词、参数与参考图"
        };
        historyBtn.Click += (_, _) =>
        {
            try
            {
                var history = AiGenHistory.Load(App.WorkRoot)
                    .Where(h => h.Type == AiGenType.Image).ToList();
                var picker = new AiGenHistoryWindow(history) { Owner = win };
                if (picker.ShowDialog() != true) return;
                var e = picker.SelectedEntry;
                if (e == null) return;
                promptBox.Text = e.Prompt;
                ratioBox.SelectedItem = e.Ratio;
                if (e.Level is { Length: > 0 })
                {
                    var lv = levelBox.Items.OfType<string>().FirstOrDefault(x => x == e.Level);
                    if (lv != null) levelBox.SelectedItem = lv;
                }
                // 回填参考图：先同步右侧栏选择顺序（存在的资产），再补充非资产路径
                assetPanel.SetSelection(e.RefImagePaths);
                refImages.Clear(); refPaths.Clear();
                RemoveRefThumbs();
                var ordered = assetPanel.SelectedOrder.ToList();
                foreach (var p in e.RefImagePaths)
                    if (!ordered.Contains(p) && System.IO.File.Exists(p)) ordered.Add(p);
                ViewHelpers.AddReferenceThumbsAsync(refPanel, ordered, refImages,
                    () => ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn),
                    refPaths: refPaths);
                ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn);
                MainWindow.Notify("✓ 已从历史回填");
            }
            catch (Exception ex) { MainWindow.Notify($"⚠ 历史回填失败：{ex.Message}", success: false); }
        };
        footer.Children.Add(historyBtn);
        var genBtn = new Button
        {
            Content = "🎨 开始生成",
            FontSize = 13, Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(12, 0, 0, 0),
            Style = Style("PrimaryButtonStyle")
        };
        footer.Children.Add(genBtn);
        Grid.SetRow(footer, 4);
        Grid.SetColumnSpan(footer, 3);
        grid.Children.Add(footer);

        // 三栏布局（左右栏可拖拽调整宽度并持久化）：左栏（提示词素材） | 中央（提示词+参考图） | 右栏（宠物资产）
        var bodyRow = GenPanelLayout.CreateThreeColumn(win, promptPanel.Root, center, assetPanel.Root);
        Grid.SetRow(bodyRow, 2);
        Grid.SetColumnSpan(bodyRow, 3);
        grid.Children.Add(bodyRow);

        genBtn.Click += (_, _) =>
        {
            var prompt = ViewHelpers.AppendEnabledDefaultPrompts(promptBox.Text.Trim(), "Image");
            if (string.IsNullOrWhiteSpace(prompt)) { MainWindow.Notify("⚠ 请输入提示词", success: false); return; }

            bool useComfy = config.DefaultImageProvider == "ComfyUI";
            if (!useComfy && (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(config.ApiEndpoint)))
            {
                MainWindow.Notify("⚠ 使用在线 API 需先在「设置→AI 模型配置」中填入 API 地址和密钥", success: false);
                return;
            }
            if (useComfy && (string.IsNullOrWhiteSpace(config.ComfyUiEndpoint) || string.IsNullOrWhiteSpace(config.ComfyUiWorkflowFile)))
            {
                MainWindow.Notify("⚠ 使用本地 ComfyUI 需先配置 ComfyUI 地址和工作流文件", success: false);
                return;
            }

            var ratio = ratioBox.SelectedItem?.ToString() ?? "1:1";
            var level = levelBox.SelectedItem?.ToString() ?? "1K";
            var size = ViewHelpers.CalcImageSize(ratio, level);

            var task = new AiTask
            {
                Type = AiTaskType.Image,
                Provider = useComfy ? ImageProvider.ComfyUI : ImageProvider.Api,
                Prompt = prompt,
                Detail = useComfy
                    ? $"ComfyUI·{size}" + (refImages.Count > 0 ? $"·参考图×{refImages.Count}" : "")
                    : (refImages.Count > 0 ? $"参考图×{refImages.Count}·{size}" : size),
                ApiEndpoint = useComfy ? config.ComfyUiEndpoint : config.ApiEndpoint,
                ApiKey = config.ApiKey,
                Model = config.ImageModel,
                ComfyWorkflowFile = config.ComfyUiWorkflowFile,
                TargetDir = FileService.PetResourcesPath(App.WorkRoot),
                FileNameBase = $"Pet_Image_{DateTime.Now:yyyyMMdd_HHmmss_fff}",
                ImageSize = size,
                ImageLevel = level,
                ImageRatio = ratio,
                ReferenceImages = refImages.Count > 0 ? new List<string>(refImages) : null,
                NovelName = "宠物",
                ScopeName = "宠物生成"
            };
            AiTaskManager.Enqueue(task);
            AiGenHistory.Add(App.WorkRoot, new AiGenHistoryEntry
            {
                Type = AiGenType.Image,
                Prompt = prompt,
                Ratio = ratio,
                Level = level,
                RefImagePaths = new List<string>(refPaths),
                EngineBadge = useComfy ? "ComfyUI" : "API"
            });
            MainWindow.Notify($"✓ 已加入 AI 任务队列（{(useComfy ? "ComfyUI" : "API")}），窗口保持打开");
        };

        win.Content = grid;
        // 拖拽图片到窗口 → 自动归入宠物资源目录并作为参考图加入
        ViewHelpers.EnableImageDrop(grid,
            assetImportDir: FileService.PetResourcesPath(App.WorkRoot),
            onImported: path =>
            {
                ViewHelpers.AddReferenceThumb(refPanel, path, refImages,
                    () => ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn),
                    refPaths: refPaths);
                ViewHelpers.UpdateReferenceHint(refImages, refHintText, clearRefBtn);
                assetPanel.SelectImported(path);
                MainWindow.Notify("✓ 已加入参考图并归入宠物资源");
            },
            onInvalid: () => MainWindow.Notify("⚠ 请拖入支持格式的图片", success: false));
        win.Show();
    }

    // ===== AI 生成视频 =====

    private static void OpenGenerateVideo()
    {
        var config = FileService.LoadConfig(App.WorkRoot);
        if (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(config.ApiEndpoint))
        {
            MainWindow.Notify("⚠ 请先在「设置→AI 模型配置」中填入 API 地址和密钥", success: false);
            return;
        }
        // Flash 模型固定 720P 且不支持参考视频；非 Flash（agnes-video-2.5）支持 720P/960P/2K 与参考视频
        var isFlashModel = ViewHelpers.IsFlashVideoModel(config.VideoModel);

        var win = CreateDialog("AI 生成视频", 1010, 620);
        win.MinWidth = 980;
        win.MinHeight = 520;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 标题
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });                    // 1 间距
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 2 主体（左栏|中央|右栏）
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });                    // 3 间距
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 4 底部
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                  // 0 左栏
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 1 中央
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                  // 2 右栏

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var videoHeader = new TextBlock
        {
            Text = "输入视频生成提示词 · 云端 API",
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(videoHeader, 0);
        headerGrid.Children.Add(videoHeader);
        Grid.SetRow(headerGrid, 0);
        Grid.SetColumnSpan(headerGrid, 3);
        grid.Children.Add(headerGrid);

        var promptBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13,
            Foreground = Brush("TextPrimaryBrush"),
            Background = Brush("CardBackgroundBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10)
        };
        // 提示词放入中央子网格（三栏布局构建 center 时再挂载）

        // ===== 参考图区域（图生视频，可选，支持多张） =====
        var refPanel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var refImages = new List<string>();
        var refBtn = new Button
        {
            Content = "🖼️ 参考图（可选）", FontSize = 11, Padding = new Thickness(10, 4, 10, 4),
            Style = Style("SecondaryButtonStyle"),
            ToolTip = "选择一张或多张参考图片，作为生成视频的画面参考（图生视频 / 多图参考）"
        };
        refPanel.Children.Add(refBtn);
        var assetRefBtn = new Button
        {
            Content = "📁 宠物资产", FontSize = 11, Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            Style = Style("SecondaryButtonStyle"),
            ToolTip = "从宠物资源目录中选择参考图"
        };
        refPanel.Children.Add(assetRefBtn);
        var refWrap = new WrapPanel { MaxWidth = 400, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var refPaths = new List<string>(); // 与 refImages 对应的源文件路径（用于历史回填）
        refPanel.Children.Add(refWrap);
        var clearRefBtn = new Button
        {
            Content = "✕", FontSize = 11, Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed,
            Style = Style("SecondaryButtonStyle"),
            ToolTip = "清除全部参考图"
        };
        refPanel.Children.Add(clearRefBtn);

        // 应用参考图（缩略图流，支持多张；Flash 最多 5 张）
        void ApplyRefImage(string path)
        {
            ViewHelpers.AddReferenceThumb(refWrap, path, refImages, UpdateRefState, maxCount: 5, refPaths: refPaths);
            UpdateRefState();
        }

        void UpdateRefState()
        {
            clearRefBtn.Visibility = refImages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 从本地文件选择参考图（支持多选）
        refBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif",
                Title = "选择参考图片",
                Multiselect = true
            };
            // owner 传 AI 小窗口，避免对话框关闭后激活主窗口触发其误最小化
            if (dlg.ShowDialog(win) != true) return;
            foreach (var f in dlg.FileNames) ApplyRefImage(f);
        };

        // 从宠物资源中选择参考图（支持多选，按选择顺序排序）
        assetRefBtn.Click += (_, _) =>
        {
            try
            {
                var dir = FileService.PetResourcesPath(App.WorkRoot);
                var paths = new List<string>();
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif")
                            paths.Add(f);
                    }
                }
                if (paths.Count == 0) { MainWindow.Notify("⚠ 宠物资源目录还没有图片，请先通过 AI 生图生成", success: false); return; }
                var picker = new AssetPickerWindow(paths, "选择宠物资源图片作为参考图（可多选）", multiSelect: true) { Owner = win };
                if (picker.ShowDialog() != true) return;
                foreach (var p in picker.OrderedPaths) ApplyRefImage(p);
            }
            catch (Exception ex)
            {
                MainWindow.Notify($"⚠ 无法打开宠物资产：{ex.Message}", success: false);
            }
        };

        clearRefBtn.Click += (_, _) =>
        {
            refImages.Clear();
            refPaths.Clear();
            refWrap.Children.Clear();
            UpdateRefState();
        };

        // ===== 视频参考（仅 agnes-video-2.5 非 Flash 支持）=====
        string? refVideoData = null;
        string? refVideoPath = null;
        var videoRefBtn = new Button
        {
            Content = "🎞️ 参考视频（可选）", FontSize = 11, Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            Style = Style("SecondaryButtonStyle"),
            IsEnabled = !isFlashModel,
            ToolTip = isFlashModel
                ? "agnes-video-2.5-flash 不支持参考视频（videos 参数返回 400），请改用 agnes-video-2.5"
                : "选择一段参考视频，延续其动作、镜头节奏与视觉表现（仅 agnes-video-2.5 支持）"
        };
        refPanel.Children.Add(videoRefBtn);
        var videoRefName = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            MaxWidth = 140,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        refPanel.Children.Add(videoRefName);
        var clearVideoRefBtn = new Button
        {
            Content = "✕", FontSize = 11, Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed,
            Style = Style("SecondaryButtonStyle"),
            ToolTip = "清除参考视频"
        };
        refPanel.Children.Add(clearVideoRefBtn);

        void ApplyRefVideo(string path)
        {
            var data = ViewHelpers.VideoToBase64DataUrl(path);
            if (data == null) { MainWindow.Notify("⚠ 参考视频读取失败", success: false); return; }
            refVideoData = data;
            refVideoPath = path;
            videoRefName.Text = Path.GetFileName(path);
            videoRefName.Visibility = Visibility.Visible;
            clearVideoRefBtn.Visibility = Visibility.Visible;
            MainWindow.Notify("✓ 已添加参考视频");
        }
        videoRefBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.webm;*.wmv",
                Title = "选择参考视频"
            };
            if (dlg.ShowDialog(win) != true) return;
            ApplyRefVideo(dlg.FileName);
        };
        clearVideoRefBtn.Click += (_, _) =>
        {
            refVideoData = null;
            refVideoPath = null;
            videoRefName.Text = "";
            videoRefName.Visibility = Visibility.Collapsed;
            clearVideoRefBtn.Visibility = Visibility.Collapsed;
        };

        var refRow = new Grid();
        refRow.Children.Add(refPanel);

        // ===== 右侧边栏：宠物资产（点击按顺序编号，作为参考图顺序） =====
        var petDir = FileService.PetResourcesPath(App.WorkRoot);
        var petPaths = new List<string>();
        if (Directory.Exists(petDir))
            foreach (var f in Directory.GetFiles(petDir))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif") petPaths.Add(f);
            }
        var assetPanel = new AssetPanel("宠物资产", petPaths, maxCount: 5);

        // 右侧栏选择顺序 → 重建参考图列表（含手动添加的本地参考图共存于 refImages）
        void RebuildRefsFromAssets()
        {
            refImages.Clear();
            refPaths.Clear();
            refWrap.Children.Clear();
            ViewHelpers.AddReferenceThumbsAsync(refWrap, assetPanel.SelectedOrder, refImages,
                UpdateRefState, maxCount: 5, refPaths: refPaths);
            UpdateRefState();
        }
        assetPanel.SelectionChanged = RebuildRefsFromAssets;
        // 清除参考图（同时清空右侧栏资产选择）
        clearRefBtn.Click += (_, _) =>
        {
            assetPanel.ClearSelection();
            refImages.Clear();
            refPaths.Clear();
            refWrap.Children.Clear();
            UpdateRefState();
        };

        // ===== 左侧边栏：文本导入/编辑/选区加入/导出 + 默认提示词 =====
        void AppendPromptText(string t)
        {
            var cur = promptBox.Text;
            promptBox.Text = string.IsNullOrWhiteSpace(cur) ? t : cur.TrimEnd() + "\n" + t;
            promptBox.CaretIndex = promptBox.Text.Length;
            promptBox.Focus();
        }
        var promptPanel = new PromptPanel(win, "Video")
        {
            AppendToPrompt = AppendPromptText
        };

        // ===== 中央：提示词输入 + 参考图区 =====
        var center = new Grid();
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 0 提示词
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });                  // 1 间距
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // 2 参考图
        Grid.SetRow(promptBox, 0);
        center.Children.Add(promptBox);
        Grid.SetRow(refRow, 2);
        center.Children.Add(refRow);

        // 「优化提示词」按钮（标题行右侧），有参考图时结合参考图内容优化
        var optimizeBtn = CreateOptimizeButton(promptBox,
            () => refImages.Count > 0 ? refImages : null,
            "video");
        Grid.SetColumn(optimizeBtn, 1);
        headerGrid.Children.Add(optimizeBtn);

        // ===== 底部 =====
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var levelBox = Combo(ViewHelpers.VideoLevelsForModel(config.VideoModel), "720P", 80);
        var ratioBox = Combo(ViewHelpers.VideoRatios, "16:9", 76);
        var secondsBox = Combo(new[] { "4", "5", "8", "10", "12" }, "5", 62);
        footer.Children.Add(levelBox);
        footer.Children.Add(ratioBox);
        footer.Children.Add(secondsBox);

        var historyBtn = new Button
        {
            Content = "🕘 历史", FontSize = 12, Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(10, 0, 0, 0),
            Style = Style("SecondaryButtonStyle"),
            ToolTip = "查看历史记录，点击一条自动回填提示词、参数与参考素材"
        };
        historyBtn.Click += (_, _) =>
        {
            try
            {
                var history = AiGenHistory.Load(App.WorkRoot)
                    .Where(h => h.Type == AiGenType.Video).ToList();
                var picker = new AiGenHistoryWindow(history) { Owner = win };
                if (picker.ShowDialog() != true) return;
                var e = picker.SelectedEntry;
                if (e == null) return;
                promptBox.Text = e.Prompt;
                if (e.Level is { Length: > 0 })
                {
                    var lv = levelBox.Items.OfType<string>().FirstOrDefault(x => x == e.Level);
                    if (lv != null) levelBox.SelectedItem = lv;
                }
                if (e.Ratio is { Length: > 0 })
                {
                    var rt = ratioBox.Items.OfType<string>().FirstOrDefault(x => x == e.Ratio);
                    if (rt != null) ratioBox.SelectedItem = rt;
                }
                if (e.Seconds > 0)
                {
                    var sc = secondsBox.Items.OfType<string>().FirstOrDefault(x => x == e.Seconds.ToString());
                    if (sc != null) secondsBox.SelectedItem = sc;
                }
                // 回填参考图：先同步右侧栏选择顺序（存在的资产），再补充非资产路径
                assetPanel.SetSelection(e.RefImagePaths);
                refImages.Clear(); refPaths.Clear(); refWrap.Children.Clear();
                UpdateRefState();
                var ordered = assetPanel.SelectedOrder.ToList();
                foreach (var p in e.RefImagePaths)
                    if (!ordered.Contains(p) && System.IO.File.Exists(p)) ordered.Add(p);
                foreach (var p in ordered)
                    if (System.IO.File.Exists(p)) ApplyRefImage(p);
                // 回填参考视频
                if (!string.IsNullOrWhiteSpace(e.RefVideoPath) && System.IO.File.Exists(e.RefVideoPath))
                    ApplyRefVideo(e.RefVideoPath);
                MainWindow.Notify("✓ 已从历史回填");
            }
            catch (Exception ex) { MainWindow.Notify($"⚠ 历史回填失败：{ex.Message}", success: false); }
        };
        footer.Children.Add(historyBtn);

        var genBtn = new Button
        {
            Content = "🎬 生成视频",
            FontSize = 13, Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(12, 0, 0, 0),
            Style = Style("PrimaryButtonStyle")
        };
        footer.Children.Add(genBtn);
        Grid.SetRow(footer, 4);
        Grid.SetColumnSpan(footer, 3);
        grid.Children.Add(footer);

        // 三栏布局（左右栏可拖拽调整宽度并持久化）：左栏（提示词素材） | 中央（提示词+参考图） | 右栏（宠物资产）
        var bodyRow = GenPanelLayout.CreateThreeColumn(win, promptPanel.Root, center, assetPanel.Root);
        Grid.SetRow(bodyRow, 2);
        Grid.SetColumnSpan(bodyRow, 3);
        grid.Children.Add(bodyRow);

        genBtn.Click += (_, _) =>
        {
            var prompt = ViewHelpers.AppendEnabledDefaultPrompts(promptBox.Text.Trim(), "Video");
            if (string.IsNullOrWhiteSpace(prompt)) { MainWindow.Notify("⚠ 请输入提示词", success: false); return; }

            var level = levelBox.SelectedItem?.ToString() ?? "720P";
            var ratio = ratioBox.SelectedItem?.ToString() ?? "16:9";
            var seconds = int.TryParse(secondsBox.SelectedItem?.ToString(), out var s) ? s : 5;
            seconds = Math.Clamp(seconds, 4, ViewHelpers.CalcVideoMaxSeconds(level, ratio));
            var hasImageRef = refImages.Count > 0;
            var hasVideoRef = !string.IsNullOrWhiteSpace(refVideoData);
            // 参考模式按文档补齐 <Picture N>/<Video 1> 提示词引用
            var finalPrompt = ViewHelpers.BuildVideoPrompt(prompt, refImages.Count, hasVideoRef ? 1 : 0);

            var detail = $"{level}·{ratio}·{seconds}s";
            if (hasImageRef) detail += $"·参考图{refImages.Count}";
            if (hasVideoRef) detail += "·参考视频";

            var task = new AiTask
            {
                Type = AiTaskType.Video,
                Prompt = finalPrompt,
                Detail = detail,
                ApiEndpoint = config.ApiEndpoint,
                ApiKey = config.ApiKey,
                Model = config.VideoModel,
                TargetDir = FileService.PetResourcesPath(App.WorkRoot),
                FileNameBase = $"Pet_Video_{DateTime.Now:yyyyMMdd_HHmmss_fff}",
                VideoSize = level, VideoRatio = ratio, VideoSeconds = seconds,
                ReferenceImages = hasImageRef ? new List<string>(refImages) : null,
                ReferenceVideos = hasVideoRef ? new List<string> { refVideoData! } : null,
                NovelName = "宠物",
                ScopeName = "宠物生成"
            };
            AiTaskManager.Enqueue(task);
            AiGenHistory.Add(App.WorkRoot, new AiGenHistoryEntry
            {
                Type = AiGenType.Video,
                Prompt = prompt,
                Level = level,
                Ratio = ratio,
                Seconds = seconds,
                RefImagePaths = new List<string>(refPaths),
                RefVideoPath = refVideoPath ?? "",
                EngineBadge = "API"
            });
            MainWindow.Notify("✓ 已加入 AI 任务队列，窗口保持打开");
        };

        win.Content = grid;
        // 拖拽图片到窗口 → 自动归入宠物资源目录并作为参考图加入
        ViewHelpers.EnableImageDrop(grid,
            assetImportDir: FileService.PetResourcesPath(App.WorkRoot),
            onImported: path =>
            {
                ApplyRefImage(path);
                assetPanel.SelectImported(path);
                MainWindow.Notify("✓ 已加入参考图并归入宠物资源");
            },
            onInvalid: () => MainWindow.Notify("⚠ 请拖入支持格式的图片", success: false));
        win.Show();
    }

    // ===== AI 对话 =====

    private static void OpenChat()
    {
        var config = FileService.LoadConfig(App.WorkRoot);
        if (string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(config.ApiEndpoint))
        {
            MainWindow.Notify("⚠ 请先在「设置→AI 模型配置」中填入 API 地址和密钥", success: false);
            return;
        }

        var win = CreateDialog("AI 对话", 640, 580);

        var root = new Grid { Margin = new Thickness(0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 头部
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 消息区
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 输入区
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮行

        // ===== 头部（毛玻璃质感标题栏） =====
        var header = new Border
        {
            Background = Brush("SidebarBackgroundBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 14, 18, 14)
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Child = headerGrid;

        var headerIcon = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(12),
            Background = Brush("PrimaryBrush"),
            Child = new TextBlock
            {
                Text = "🤖", FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(headerIcon, 0);
        headerGrid.Children.Add(headerIcon);

        var titleStack = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = "AI 对话", FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush")
        });
        var modelLabel = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(config.ApiModel) ? "未设置模型" : $"模型：{config.ApiModel}",
            FontSize = 11, Foreground = Brush("TextTertiaryBrush"), Margin = new Thickness(0, 3, 0, 0)
        };
        titleStack.Children.Add(modelLabel);
        Grid.SetColumn(titleStack, 1);
        headerGrid.Children.Add(titleStack);

        var clearBtn = new Button
        {
            Content = "🗑 清空", FontSize = 11, Padding = new Thickness(10, 4, 10, 4),
            Style = Style("SecondaryButtonStyle"), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(clearBtn, 2);
        headerGrid.Children.Add(clearBtn);
        root.Children.Add(header);

        // ===== 消息区（气泡） =====
        var messagePanel = new StackPanel { Margin = new Thickness(18, 14, 18, 4) };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = messagePanel,
            Background = Brushes.Transparent
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // 欢迎气泡
        AddAiBubble(messagePanel, "你好呀～我是你的 AI 小助手 🐑\n有什么问题都可以问我，也可以让我帮你写故事、改文案。");

        // ===== 输入区 =====
        var inputGrid = new Grid { Margin = new Thickness(18, 8, 18, 0) };
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(inputGrid, 2);
        root.Children.Add(inputGrid);

        var inputBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 66,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Style = Style("ModernTextBoxStyle"),
            VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 13
        };
        Grid.SetColumn(inputBox, 0);
        inputGrid.Children.Add(inputBox);

        var sendBtn = new Button
        {
            Content = "发送 ➤", FontSize = 13, Padding = new Thickness(18, 6, 18, 6),
            Style = Style("PrimaryButtonStyle"), Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetColumn(sendBtn, 1);
        inputGrid.Children.Add(sendBtn);

        var hintText = new TextBlock
        {
            Text = "Enter 发送 · Shift+Enter 换行 · 对话自动保存到宠物资源",
            FontSize = 10.5, Foreground = Brush("TextTertiaryBrush"),
            Margin = new Thickness(2, 5, 0, 0)
        };
        Grid.SetRow(hintText, 1);
        Grid.SetColumnSpan(hintText, 2);
        inputGrid.Children.Add(hintText);

        // ===== 底部按钮行 =====
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(18, 10, 18, 14)
        };
        var saveBtn = new Button
        {
            Content = "💾 保存对话", FontSize = 12, Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0), Style = Style("SecondaryButtonStyle")
        };
        btnRow.Children.Add(saveBtn);
        Grid.SetRow(btnRow, 3);
        root.Children.Add(btnRow);

        var history = new StringBuilder();
        var isSending = false;

        saveBtn.Click += (_, _) =>
        {
            if (history.Length == 0) { MainWindow.Notify("⚠ 暂无对话内容", success: false); return; }
            try
            {
                var dir = FileService.PetResourcesPath(App.WorkRoot);
                FileService.EnsureDirectory(dir);
                var file = Path.Combine(dir, $"Pet_Chat_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(file, history.ToString(), Encoding.UTF8);
                MainWindow.Notify($"✓ 对话已保存：{Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                MainWindow.Notify($"⚠ 保存失败：{ex.Message}", success: false);
            }
        };

        clearBtn.Click += (_, _) =>
        {
            messagePanel.Children.Clear();
            history.Clear();
            AddAiBubble(messagePanel, "对话已清空，让我们重新开始吧～");
        };

        async Task SendAsync()
        {
            var question = inputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(question) || isSending) return;

            isSending = true;
            inputBox.Clear();
            sendBtn.IsEnabled = false;
            sendBtn.Content = "⏳ 生成中…";

            history.AppendLine($"我：{question}").AppendLine();
            AddUserBubble(messagePanel, question);

            var loading = AddLoadingBubble(messagePanel, "AI 正在思考…");

            try
            {
                var answer = await ApiService.ChatAsync(
                    config.ApiEndpoint, config.ApiKey, config.ApiModel,
                    "你是一个乐于助人的创意助手，请用简洁清晰的中文回答。", question);
                var text = string.IsNullOrWhiteSpace(answer) ? "（未返回内容）" : answer.Trim();
                history.AppendLine($"AI：{text}").AppendLine();
                ReplaceLoadingWithAi(loading, text);
            }
            catch (Exception ex)
            {
                var msg = $"⚠ 请求失败：{ex.Message}";
                history.AppendLine(msg).AppendLine();
                ReplaceLoadingWithAi(loading, msg, isError: true);
            }
            finally
            {
                isSending = false;
                sendBtn.IsEnabled = true;
                sendBtn.Content = "发送 ➤";
                scroll.ScrollToEnd();
                inputBox.Focus();
            }
        }

        sendBtn.Click += async (_, _) => await SendAsync();
        // 用 PreviewKeyDown：在 TextBox 内部“Enter 换行”处理之前拦截，才能让 Enter 发送
        inputBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                _ = SendAsync();
            }
        };

        win.Content = root;
        win.Show();
        inputBox.Focus();
    }

    // ===== AI 对话辅助（气泡） =====

    private static void AddAiBubble(StackPanel panel, string text, bool isError = false)
    {
        var wrap = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 430,
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(4, 14, 14, 14),
            Background = isError
                ? new SolidColorBrush(Color.FromArgb(0x22, 0xF4, 0x43, 0x36))
                : Brush("CardBackgroundBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1)
        };
        wrap.Child = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = isError ? Brush("DangerBrush") : Brush("TextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21
        };
        panel.Children.Add(wrap);
    }

    private static void AddUserBubble(StackPanel panel, string text)
    {
        var wrap = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 430,
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(14, 4, 14, 14),
            Background = Brush("PrimaryBrush"),
            BorderThickness = new Thickness(0)
        };
        wrap.Child = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21
        };
        panel.Children.Add(wrap);
    }

    private static Border AddLoadingBubble(StackPanel panel, string text)
    {
        var wrap = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 430,
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(4, 14, 14, 14),
            Background = Brush("CardBackgroundBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1)
        };
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = Brush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        wrap.Child = textBlock;
        panel.Children.Add(wrap);
        return wrap;
    }

    private static void ReplaceLoadingWithAi(Border loading, string text, bool isError = false)
    {
        if (loading.Parent is StackPanel panel)
        {
            panel.Children.Remove(loading);
            AddAiBubble(panel, text, isError);
        }
    }

    // ===== 工具方法 =====

    /// <summary>
    /// 创建「✨ 优化提示词」按钮：复用主程序设计，有参考图时结合参考图内容优化。
    /// </summary>
    /// <param name="promptBox">提示词输入框</param>
    /// <param name="getRefImages">返回当前参考图 Data URI 列表（无参考图返回 null/空）</param>
    /// <param name="kind">"image"=生图提示词优化，"video"=生视频提示词优化</param>
    private static Button CreateOptimizeButton(TextBox promptBox, Func<IReadOnlyList<string>?> getRefImages, string kind)
    {
        var config = FileService.LoadConfig(App.WorkRoot);
        bool isVideo = kind == "video";

        var btn = new Button
        {
            Content = "✨ 优化提示词", FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Style = Style("SecondaryButtonStyle"),
            ToolTip = "调用 AI 把简短提示词扩展为更详细、专业的提示词"
        };

        btn.Click += async (_, _) =>
        {
            var rawPrompt = promptBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(rawPrompt))
            {
                MainWindow.Notify("⚠ 请先输入提示词再优化", success: false);
                return;
            }

            btn.IsEnabled = false;
            btn.Content = "⏳ 优化中...";
            try
            {
                var refImages = getRefImages();
                bool hasRef = refImages is { Count: > 0 };

                // 使用用户自定义的优化 Skill（设置 → AI 生成 Skill 中编辑）
                var skill = isVideo ? config.VideoOptimizeSkill : config.ImageOptimizeSkill;
                var (sys, userPrompt) = ViewHelpers.BuildOptimizePrompt(
                    skill, rawPrompt, hasRef, refImages?.Count ?? 0,
                    subject: isVideo ? "视频" : "图像");

                var result = hasRef
                    ? await ApiService.ChatWithImagesAsync(
                        config.ApiEndpoint, config.ApiKey, config.ApiModel, sys, userPrompt, refImages)
                    : await ApiService.ChatAsync(
                        config.ApiEndpoint, config.ApiKey, config.ApiModel, sys, userPrompt);

                if (!string.IsNullOrWhiteSpace(result))
                {
                    promptBox.Text = result.Trim();
                    MainWindow.Notify(hasRef ? "✓ 提示词已结合参考图优化" : "✓ 提示词已优化");
                }
            }
            catch (ApiException ex) { MainWindow.Notify($"⚠ {ex.Message}", success: false); }
            catch (Exception ex) { MainWindow.Notify($"⚠ 优化失败：{ex.Message}", success: false); }
            finally
            {
                btn.IsEnabled = true;
                btn.Content = "✨ 优化提示词";
            }
        };

        return btn;
    }

    private static Window CreateDialog(string title, double width, double height, Window? owner = null)
    {
        // 用 Win32 层归属（SetWin32Owner）代替 WPF Owner：子窗口可被鼠标选中、可被 Alt+Tab 切换，
        // 并在任务栏与主窗口共用同一图标（不脱离主进程）；同时规避 WPF 关闭 owned 窗口
        // 误激活/最小化 AllowsTransparency 主窗口的问题。
        owner ??= System.Windows.Application.Current.MainWindow;
        var win = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = 440,
            MinHeight = 340,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = true,
            ResizeMode = ResizeMode.CanResize,
            Background = Brush("WindowBackgroundBrush")
        };
        ViewHelpers.SetWin32Owner(win, owner);
        ViewHelpers.CenterWindowOnOwner(win, owner);
        return win;
    }

    private static ComboBox Combo(string[] items, string selected, double width)
    {
        var box = new ComboBox
        {
            ItemsSource = items,
            SelectedItem = selected,
            Width = width,
            Height = 28,
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0)
        };
        box.Style = Style("ModernComboBoxStyle");
        box.Foreground = Brush("TextPrimaryBrush");
        box.Background = Brush("CardBackgroundBrush");
        box.BorderBrush = Brush("BorderBrush");
        return box;
    }

    private static Brush Brush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Gray;

    private static Style? Style(string key) =>
        Application.Current.TryFindResource(key) as Style;
}