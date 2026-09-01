using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

/// <summary>
/// 独立「AI 接口配置」窗口：文本 / 图片 / 视频 三类接口各自独立配置
/// （服务商预设 + 地址 + 密钥 + 模型 + 能力提示），并集中管理 ComfyUI 本地生图
/// （服务地址、默认内置/用户导入工作流、工作流参数编辑）。
/// </summary>
public partial class AiApiConfigWindow : Window
{
    private readonly AppConfig _config;
    private readonly Dictionary<ApiChannel, ProfileEditor> _editors = new();

    /// <summary>加载表单期间为 true，抑制控件赋值触发的联动刷新，防止覆盖已保存的 ApiKey 等字段。</summary>
    private bool _loadingEditor;

    // 工作流列表项视图模型
    private sealed class WorkflowItem
    {
        public ComfyWorkflowEntry Entry { get; }
        public WorkflowItem(ComfyWorkflowEntry entry) => Entry = entry;
        public string Name => Entry.Name;
        public string FilePath => Entry.FilePath;
        public string Source => Entry.Source;
        public string ParamsSummary
        {
            get
            {
                var p = Entry.Params;
                if (p == null) return "尚未解析参数，点击「编辑参数」自动解析";
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(p.Checkpoint)) parts.Add(p.Checkpoint);
                if (!string.IsNullOrEmpty(p.LoRA)) parts.Add($"LoRA {p.LoRA}×{p.LoraStrength:0.0}");
                if (!string.IsNullOrEmpty(p.Vae)) parts.Add(p.Vae);
                parts.Add($"{p.SamplerName}/{p.Scheduler} {p.Steps}步");
                return string.Join(" · ", parts);
            }
        }
        public bool IsDefault { get; set; }
    }

    public AiApiConfigWindow()
    {
        InitializeComponent();
        _config = FileService.LoadConfig(App.WorkRoot);

        // 三个接口通道的独立配置 Tab（用代码构建，结构一致且避免 XAML 大量重复）
        TextTabHost.Children.Add(BuildProfileTab(ApiChannel.Text));
        ImageTabHost.Children.Add(BuildProfileTab(ApiChannel.Image));
        VideoTabHost.Children.Add(BuildProfileTab(ApiChannel.Video));

        // ComfyUI 配置（已内置到图片生成 Tab，依据引擎选择展示）
        _config.DefaultImageProvider ??= "Api";
        if (_config.DefaultImageProvider == "ComfyUI")
            ProviderComfyRadio.IsChecked = true;
        else
            ProviderApiRadio.IsChecked = true;
        ComfyEndpointBox.Text = _config.ComfyUiEndpoint;
        WorkflowDirHint.Text = $"默认工作流目录：{ComfyWorkflowParser.DefaultWorkflowsDir}\n把「Export (API)」导出的 JSON 放到此目录，或点击「导入工作流」。";

        RefreshWorkflowList();

        // 依据生图引擎选择展示对应的配置区（云端 API 表单 / ComfyUI 配置）
        UpdateImageEngineViews();
    }

    // ==================== 接口通道配置编辑器 ====================

    /// <summary>单个通道（文本/图片/视频）的配置表单控件与状态。</summary>
    private sealed class ProfileEditor
    {
        public ApiChannel Channel;
        public AiApiProfile Profile = null!;
        public ApiProvider SelectedProviderValue = ApiProvider.Custom;
        public WrapPanel ProviderChips = null!;
        public TextBox BaseUrlBox = null!;
        public System.Windows.Controls.PasswordBox ApiKeyBox = null!;
        public ComboBox ModelBox = null!;
        public TextBlock CapabilityText = null!;
        public TextBox NoteBox = null!;
        public Button FetchBtn = null!;
    }

    /// <summary>构建一个接口通道配置页（服务商/地址/密钥/模型/能力提示/备注）。</summary>
    private FrameworkElement BuildProfileTab(ApiChannel channel)
    {
        var profile = (channel switch
        {
            ApiChannel.Text => _config.TextApi,
            ApiChannel.Image => _config.ImageApi,
            _ => _config.VideoApi
        }).Clone();

        var ed = new ProfileEditor
        {
            Channel = channel,
            Profile = profile,
            SelectedProviderValue = profile.Provider
        };
        _editors[channel] = ed;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };

        // ---- 基础连接卡片 ----
        var connCard = SectionCard();
        var connStack = new StackPanel();
        connCard.Child = connStack;
        connStack.Children.Add(SectionTitle("🔑 服务商与连接"));

        // 服务商：可视化 chip 选择（选中高亮，自动填充地址与推荐模型）
        connStack.Children.Add(FieldLabel("服务商"));
        ed.ProviderChips = BuildProviderChips(ed);
        connStack.Children.Add(ed.ProviderChips);

        // 地址
        connStack.Children.Add(FieldLabel("接口地址（Base URL）", top: 2));
        ed.BaseUrlBox = new TextBox
        {
            FontSize = 12, Style = GetStyle("ModernTextBoxStyle")
        };
        ed.BaseUrlBox.TextChanged += (_, _) => OnModelOrBaseChanged(ed);
        connStack.Children.Add(ed.BaseUrlBox);

        // 密钥
        connStack.Children.Add(FieldLabel("API 密钥", top: 12));
        ed.ApiKeyBox = new System.Windows.Controls.PasswordBox
        {
            FontSize = 12, Style = GetStyle("ModernPasswordBoxStyle")
        };
        connStack.Children.Add(ed.ApiKeyBox);

        // 模型 + 获取列表
        connStack.Children.Add(FieldLabel("模型", top: 12));
        var modelGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        modelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ed.ModelBox = new ComboBox
        {
            IsEditable = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = GetStyle("ModernComboBoxStyle")
        };
        // 可编辑 ComboBox 没有 TextChanged 事件，需监听其内部文本框
        ed.ModelBox.Loaded += (_, _) =>
        {
            if (ed.ModelBox.Template.FindName("PART_EditableTextBox", ed.ModelBox) is TextBox innerTb)
                innerTb.TextChanged += (_, _) => OnModelOrBaseChanged(ed);
        };
        ed.ModelBox.SelectionChanged += (_, _) => OnModelOrBaseChanged(ed);
        Grid.SetColumn(ed.ModelBox, 0);
        modelGrid.Children.Add(ed.ModelBox);

        ed.FetchBtn = new Button
        {
            Content = "⬇ 获取模型列表",
            FontSize = 11, Padding = new Thickness(10, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Style = GetStyle("SecondaryButtonStyle")
        };
        ed.FetchBtn.Click += async (_, _) => await FetchModelsAsync(ed);
        Grid.SetColumn(ed.FetchBtn, 1);
        modelGrid.Children.Add(ed.FetchBtn);
        connStack.Children.Add(modelGrid);

        // 能力提示
        connStack.Children.Add(FieldLabel("模型能力（依据官方资料）", top: 4));
        var capBorder = new Border
        {
            Background = Brush("WindowBackgroundBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 10)
        };
        ed.CapabilityText = new TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondaryBrush")
        };
        capBorder.Child = ed.CapabilityText;
        connStack.Children.Add(capBorder);

        // 备注
        connStack.Children.Add(FieldLabel("备注（可选）"));
        ed.NoteBox = new TextBox
        {
            FontSize = 12, Style = GetStyle("ModernTextBoxStyle")
        };
        connStack.Children.Add(ed.NoteBox);

        // ---- 通道说明卡片 ----
        var hintCard = SectionCard();
        var hintStack = new StackPanel();
        hintCard.Child = hintStack;
        hintStack.Children.Add(SectionTitle("ℹ️ 通道说明"));
        hintStack.Children.Add(new TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondaryBrush"),
            Text = ChannelHint(channel)
        });

        root.Children.Add(connCard);
        root.Children.Add(hintCard);
        scroll.Content = root;

        // 初始化控件值：加载期间抑制联动刷新（否则 BaseUrl 赋值触发的能力刷新会在
        // PasswordBox 填充前读取到空密钥，把已保存的 ApiKey 覆盖为空导致配置丢失）
        _loadingEditor = true;
        try
        {
            foreach (var child in ed.ProviderChips.Children.OfType<Border>())
                SetChipState(child, (ApiProvider)child.Tag == ed.SelectedProviderValue);
            ed.BaseUrlBox.Text = ed.Profile.BaseUrl;
            ed.ApiKeyBox.Password = ed.Profile.ApiKey;
            ed.NoteBox.Text = ed.Profile.Note;
            ed.ModelBox.ItemsSource = BuildModelSuggestions(ed.Profile.Provider, ed.Channel);
            ed.ModelBox.Text = ed.Profile.ModelId;
            if (string.IsNullOrEmpty(ed.Profile.ModelId))
            {
                var defModel = AiModelCatalog.DefaultModel(ed.Profile.Provider, ed.Channel);
                if (!string.IsNullOrEmpty(defModel)) ed.ModelBox.Text = defModel;
            }
        }
        finally
        {
            _loadingEditor = false;
        }
        UpdateCapability(ed);

        return scroll;
    }

    /// <summary>构建服务商 chip 选择条。</summary>
    private WrapPanel BuildProviderChips(ProfileEditor ed)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var p in Enum.GetValues<ApiProvider>())
        {
            var chip = new Border
            {
                Tag = p,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                BorderBrush = Brush("BorderBrush"),
                Background = Brush("WindowBackgroundBrush"),
                Padding = new Thickness(13, 6, 13, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = AiModelCatalog.ProviderName(p),
                    FontSize = 11.5,
                    Foreground = Brush("TextPrimaryBrush")
                }
            };
            chip.MouseLeftButtonDown += (_, _) =>
            {
                if (ed.SelectedProviderValue == p) return;
                ed.SelectedProviderValue = p;
                foreach (var child in wrap.Children.OfType<Border>())
                    SetChipState(child, (ApiProvider)child.Tag == p);
                OnProviderChanged(ed);
            };
            wrap.Children.Add(chip);
        }
        return wrap;
    }

    /// <summary>设置 chip 的选中/未选中视觉状态。</summary>
    private static void SetChipState(Border chip, bool selected)
    {
        if (selected)
        {
            chip.BorderBrush = Brush("PrimaryBrush");
            chip.BorderThickness = new Thickness(1.5);
            chip.Background = Brush("PrimaryLowBrush");
            if (chip.Child is TextBlock tb) tb.Foreground = Brush("PrimaryBrush");
        }
        else
        {
            chip.BorderBrush = Brush("BorderBrush");
            chip.BorderThickness = new Thickness(1);
            chip.Background = Brush("WindowBackgroundBrush");
            if (chip.Child is TextBlock tb) tb.Foreground = Brush("TextPrimaryBrush");
        }
    }

    private static string ChannelHint(ApiChannel channel) => channel switch
    {
        ApiChannel.Text => "用于聊天对话、剧本生成、提示词生成与提示词优化。所有主流服务商（OpenAI / DeepSeek / 豆包 / 千问 / ModelScope）均提供 OpenAI 兼容的 chat/completions 接口，可直接使用。\n\n视觉理解：选支持视觉的模型（如 GPT-4o、千问 VL、豆包 Seed、DeepSeek V4 Flash Vision）即可在对话中上传图片。",
        ApiChannel.Image => "用于 AI 生图。服务商之间格式差异较大，本软件已按各服务商适配：\n· Agnes：档位式尺寸 + 比例，最多 6 张参考图；\n· 字节豆包 Seedream：单/多图参考（组图）；\n· 千问图像：文生图 + 图生图/编辑；\n· OpenAI / ModelScope：OpenAI 兼容格式。\n\n「支持多图参考」的模型可在生图时传入多张参考图合成。",
        _ => "用于 AI 生视频。Agnes 视频模型走本软件内置的异步任务接口；字节 Seedance / 千问 Wan / OpenAI Sora 走各自服务商的任务接口。\n\n各模型对参考图/首尾帧的支持不同，选择模型后上方会显示能力提示。"
    };

    // ==================== 服务商 / 模型联动 ====================

    private void OnProviderChanged(ProfileEditor ed)
    {
        var provider = SelectedProvider(ed);
        ed.Profile.Provider = provider;

        // 自动填充默认地址与推荐模型
        var defUrl = AiModelCatalog.DefaultBaseUrl(provider, ed.Channel);
        if (!string.IsNullOrEmpty(defUrl)) ed.BaseUrlBox.Text = defUrl;
        var suggestions = BuildModelSuggestions(provider, ed.Channel);
        ed.ModelBox.ItemsSource = suggestions;
        var defModel = AiModelCatalog.DefaultModel(provider, ed.Channel);
        // 当前模型不是新服务商的推荐模型时，自动切换为默认模型，避免沿用旧服务商模型导致请求失败
        var current = ed.ModelBox.Text?.Trim() ?? "";
        var isSuggested = !string.IsNullOrEmpty(current) &&
            suggestions.OfType<string>().Any(s => string.Equals(s, current, StringComparison.OrdinalIgnoreCase));
        if (!isSuggested && !string.IsNullOrEmpty(defModel))
            ed.ModelBox.Text = defModel;

        UpdateCapability(ed);
    }

    private void OnModelOrBaseChanged(ProfileEditor ed)
    {
        if (_loadingEditor) return; // 加载期间跳过，避免中间状态覆盖已保存的密钥
        UpdateCapability(ed);
    }

    private static List<object> BuildModelSuggestions(ApiProvider provider, ApiChannel channel)
    {
        var list = new List<object>();
        foreach (var m in AiModelCatalog.SuggestModels(provider, channel))
            list.Add(m);
        return list;
    }

    private void UpdateCapability(ProfileEditor ed)
    {
        var model = ed.ModelBox.Text?.Trim() ?? "";
        ed.Profile.ModelId = model;
        ed.Profile.BaseUrl = ed.BaseUrlBox.Text.Trim();
        ed.Profile.ApiKey = ed.ApiKeyBox.Password;
        ed.CapabilityText.Text = AiModelCatalog.CapabilityText(ed.Profile.Provider, ed.Channel, model);
        // 命中内置库时高亮「能力明确」，否则提示按通用方式调用
        var info = AiModelCatalog.Find(ed.Profile.Provider, ed.Channel, model);
        ed.CapabilityText.Foreground = info != null
            ? Brush("TextSecondaryBrush")
            : (Brush)new SolidColorBrush(Color.FromRgb(0x8A, 0x6D, 0x3B));
        if (info == null)
            ed.CapabilityText.Text += "\n（提示：模型库未收录该模型，将按服务商通用方式调用）";
    }

    private static ApiProvider SelectedProvider(ProfileEditor ed) => ed.SelectedProviderValue;

    private async Task FetchModelsAsync(ProfileEditor ed)
    {
        var endpoint = ed.BaseUrlBox.Text.Trim();
        var apiKey = ed.ApiKeyBox.Password.Trim();
        if (string.IsNullOrEmpty(endpoint))
        {
            MessageDialog.Show("提示", "请先填写接口地址");
            return;
        }

        ed.FetchBtn.IsEnabled = false;
        ed.FetchBtn.Content = "⏳ 获取中...";
        try
        {
            var models = await ApiService.FetchModelsAsync(endpoint, apiKey);
            var items = new List<object>(BuildModelSuggestions(ed.Profile.Provider, ed.Channel));
            foreach (var m in models)
                if (!items.Contains(m)) items.Add(m);
            ed.ModelBox.ItemsSource = items;
            if (!string.IsNullOrEmpty(ed.Profile.ModelId))
                ed.ModelBox.Text = ed.Profile.ModelId;
            SaveHintText.Text = $"✓ 获取到 {models.Count} 个模型（已合并推荐列表）";
        }
        catch (ApiException ex)
        {
            MessageDialog.Show("获取失败", ex.Message);
        }
        catch (Exception ex)
        {
            MessageDialog.Show("获取失败", $"网络错误：{ex.Message}");
        }
        finally
        {
            ed.FetchBtn.IsEnabled = true;
            ed.FetchBtn.Content = "⬇ 获取模型列表";
        }
    }

    // ==================== ComfyUI 工作流管理 ====================

    private ComfyWorkflowEntry? SelectedWorkflow =>
        (WorkflowList.SelectedItem as WorkflowItem)?.Entry;

    private void RefreshWorkflowList()
    {
        // 同步目录中的工作流文件到配置列表（内置目录 + 用户导入）
        SyncWorkflowEntries();

        // 当前默认工作流
        var defaultPath = NormalizePath(_config.ComfyUiWorkflowFile);

        var items = _config.ComfyWorkflows.Select(w => new WorkflowItem(w)).ToList();
        foreach (var it in items)
            it.IsDefault = NormalizePath(it.Entry.FilePath) == defaultPath && !string.IsNullOrEmpty(defaultPath);

        WorkflowList.ItemsSource = items;
        UpdateWorkflowButtons();

        // 默认项前移
        if (items.Count > 0 && WorkflowList.Items.Count > 0)
            WorkflowList.SelectedIndex = 0;
    }

    private void SyncWorkflowEntries()
    {
        // 目录中实际存在的 JSON 文件
        var files = ComfyWorkflowParser.ListWorkflowFiles(ComfyWorkflowParser.DefaultWorkflowsDir);
        var known = _config.ComfyWorkflows
            .Where(w => File.Exists(w.FilePath))
            .Select(w => NormalizePath(w.FilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 新增目录中发现的工作流（默认内置）
        foreach (var f in files)
        {
            if (known.Contains(NormalizePath(f))) continue;
            var isApi = !ComfyWorkflowParser.IsUiFormat(File.ReadAllText(f));
            _config.ComfyWorkflows.Add(new ComfyWorkflowEntry
            {
                Name = Path.GetFileNameWithoutExtension(f) + (isApi ? "" : "（UI）"),
                FilePath = f,
                Source = "BuiltIn"
            });
        }

        // 清理已失效条目（文件被删除）
        for (int i = _config.ComfyWorkflows.Count - 1; i >= 0; i--)
        {
            if (!File.Exists(_config.ComfyWorkflows[i].FilePath))
                _config.ComfyWorkflows.RemoveAt(i);
        }
    }

    private void UpdateWorkflowButtons()
    {
        var hasSelection = SelectedWorkflow != null;
        SetDefaultBtn.IsEnabled = hasSelection;
        EditParamsBtn.IsEnabled = hasSelection;
        ReparseBtn.IsEnabled = hasSelection;
        DeleteWorkflowBtn.IsEnabled = hasSelection;
    }

    private void WorkflowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateWorkflowButtons();

    private void ImportWorkflow_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入 ComfyUI 工作流 JSON（建议使用网页「Export (API)」导出）",
            Filter = "ComfyUI 工作流 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var isUi = ComfyWorkflowParser.IsUiFormat(json);
            if (isUi && !MessageDialog.Confirm("检测到 UI 格式",
                    "该文件是 ComfyUI 网页「Export」导出的 UI 格式，不能直接提交。\n\n是否自动转换为 API 格式后导入？（原始文件不会被修改）"))
                return;

            // 复制到工作流目录，统一管理
            var dir = ComfyWorkflowParser.UserWorkflowsDir;
            FileService.EnsureDirectory(dir);
            var destName = Path.GetFileName(dlg.FileName);
            var dest = Path.Combine(dir, destName);
            if (NormalizePath(dest) != NormalizePath(dlg.FileName))
                File.Copy(dlg.FileName, dest, overwrite: true);
            else
                dest = dlg.FileName;

            // UI 格式 → 生成 API 副本
            var entryPath = ComfyWorkflowParser.ConvertUiToApiFile(dest);
            var entryName = Path.GetFileNameWithoutExtension(entryPath);

            var entry = new ComfyWorkflowEntry
            {
                Name = entryName,
                FilePath = entryPath,
                Source = "User"
            };
            _config.ComfyWorkflows.Add(entry);

            // 自动解析参数
            try
            {
                entry.Params = ComfyWorkflowParser.ParseFile(entryPath, out _);
            }
            catch { /* 参数解析失败不阻断导入 */ }

            RefreshWorkflowList();
            // 选中新导入的工作流并打开编辑器
            foreach (var it in WorkflowList.Items)
                if (it is WorkflowItem wi && wi.Entry == entry)
                {
                    WorkflowList.SelectedItem = it;
                    break;
                }
            EditParams_Click(sender, e);
        }
        catch (Exception ex)
        {
            MessageDialog.Show("导入失败", ex.Message);
        }
    }

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        var w = SelectedWorkflow;
        if (w == null) return;
        _config.ComfyUiWorkflowFile = w.FilePath;
        // 若当前引擎还是云端，切到 ComfyUI 更直观
        SaveHintText.Text = $"✓ 已将「{w.Name}」设为默认工作流";
        RefreshWorkflowList();
    }

    private void EditParams_Click(object sender, RoutedEventArgs e)
    {
        var w = SelectedWorkflow;
        if (w == null) return;
        var editor = new ComfyWorkflowEditorWindow(w) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            RefreshWorkflowList();
            SaveHintText.Text = $"✓ 已保存「{w.Name}」的参数";
        }
    }

    private void Reparse_Click(object sender, RoutedEventArgs e)
    {
        var w = SelectedWorkflow;
        if (w == null) return;
        try
        {
            w.Params = ComfyWorkflowParser.ParseFile(w.FilePath, out var wasUi);
            if (wasUi)
            {
                w.FilePath = ComfyWorkflowParser.ConvertUiToApiFile(w.FilePath);
                w.Name = Path.GetFileNameWithoutExtension(w.FilePath);
            }
            SaveHintText.Text = $"✓ 已重新解析「{w.Name}」的参数";
            RefreshWorkflowList();
        }
        catch (Exception ex)
        {
            MessageDialog.Show("解析失败", ex.Message);
        }
    }

    private void DeleteWorkflow_Click(object sender, RoutedEventArgs e)
    {
        var w = SelectedWorkflow;
        if (w == null) return;
        if (!MessageDialog.Confirm("删除工作流",
                $"确定从列表移除「{w.Name}」吗？\n仅从列表移除，不会删除工作流文件。"))
            return;

        _config.ComfyWorkflows.Remove(w);
        if (NormalizePath(_config.ComfyUiWorkflowFile) == NormalizePath(w.FilePath))
            _config.ComfyUiWorkflowFile = "";
        RefreshWorkflowList();
    }

    private void OpenWorkflowDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = ComfyWorkflowParser.DefaultWorkflowsDir;
        FileService.EnsureDirectory(dir);
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch (Exception ex) { MessageDialog.Show("打开失败", ex.Message); }
    }

    // ==================== 保存 / 取消 / 恢复默认 ====================

    private void ProviderRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string tag)
        {
            _config.DefaultImageProvider = tag;
            UpdateImageEngineViews();
        }
    }

    /// <summary>依据默认生图引擎切换图片生成 Tab 中的配置区（云端 API 表单 ↔ ComfyUI 配置）。</summary>
    private void UpdateImageEngineViews()
    {
        var comfy = _config.DefaultImageProvider == "ComfyUI";
        if (ImageTabHost != null)
            ImageTabHost.Visibility = comfy ? Visibility.Collapsed : Visibility.Visible;
        if (ComfyUiHost != null)
            ComfyUiHost.Visibility = comfy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 收集三个通道的编辑结果
        foreach (var ed in _editors.Values)
        {
            ed.Profile.BaseUrl = ed.BaseUrlBox.Text.Trim();
            ed.Profile.ApiKey = ed.ApiKeyBox.Password;
            ed.Profile.ModelId = ed.ModelBox.Text?.Trim() ?? "";
            ed.Profile.Note = ed.NoteBox.Text?.Trim() ?? "";
        }
        _config.TextApi = _editors[ApiChannel.Text].Profile;
        _config.ImageApi = _editors[ApiChannel.Image].Profile;
        _config.VideoApi = _editors[ApiChannel.Video].Profile;

        _config.DefaultImageProvider = ProviderComfyRadio.IsChecked == true ? "ComfyUI" : "Api";
        _config.ComfyUiEndpoint = ComfyEndpointBox.Text.Trim();

        FileService.SaveConfig(App.WorkRoot, _config);
        SaveHintText.Text = "✓ 配置已保存";
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Confirm("恢复默认",
                "确定将全部 AI 接口配置恢复为默认（Agnes AI）吗？\n自定义的地址、密钥、模型与工作流列表将被覆盖。"))
            return;

        _config.TextApi = new AiApiProfile
        {
            Provider = ApiProvider.Agnes,
            BaseUrl = "https://api.agnes-ai.cn/v1",
            ModelId = "gpt-4o-mini"
        };
        _config.ImageApi = new AiApiProfile
        {
            Provider = ApiProvider.Agnes,
            BaseUrl = "https://api.agnes-ai.cn/v1",
            ModelId = "agnes-image-2.1-flash"
        };
        _config.VideoApi = new AiApiProfile
        {
            Provider = ApiProvider.Agnes,
            BaseUrl = "https://api.agnes-ai.cn/v1",
            ModelId = "agnes-video-2.5-flash"
        };
        _config.DefaultImageProvider = "Api";
        _config.ComfyUiEndpoint = "http://127.0.0.1:8188";
        _config.ComfyUiWorkflowFile = "";
        _config.ComfyWorkflows.Clear();

        // 重建三个通道表单
        foreach (var ch in new[] { ApiChannel.Text, ApiChannel.Image, ApiChannel.Video })
        {
            var host = ch switch
            {
                ApiChannel.Text => TextTabHost,
                ApiChannel.Image => ImageTabHost,
                _ => VideoTabHost
            };
            host.Children.Clear();
            host.Children.Add(BuildProfileTab(ch));
        }
        ProviderApiRadio.IsChecked = true;
        ComfyEndpointBox.Text = _config.ComfyUiEndpoint;
        RefreshWorkflowList();
        UpdateImageEngineViews();
        SaveHintText.Text = "已恢复默认配置（尚未保存，点击「保存配置」生效）";
    }

    // ==================== 小工具 ====================

    private static string NormalizePath(string? p)
        => string.IsNullOrWhiteSpace(p) ? "" : Path.GetFullPath(p.Trim());

    private static Brush Brush(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static Style? GetStyle(string key)
        => Application.Current.TryFindResource(key) as Style;

    private static Border SectionCard() => new()
    {
        Background = Brush("CardBackgroundBrush"),
        BorderBrush = Brush("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(20, 16, 20, 16),
        Margin = new Thickness(0, 0, 0, 14)
    };

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 10)
    };

    private static TextBlock FieldLabel(string text, double top = 0) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Brush("TextPrimaryBrush"),
        Margin = new Thickness(0, top, 0, 4)
    };
}
