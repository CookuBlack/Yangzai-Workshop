using System;
using System.IO;
using System.Linq;
using System.Windows;
using YangzaiWorkshop.Models;
using YangzaiWorkshop.Services;

namespace YangzaiWorkshop.Views;

/// <summary>
/// 工作流参数编辑窗口：可视化查看/编辑 ComfyUI 工作流中的关键参数
/// （模型 / LoRA 权重 / CLIP / VAE / KSampler 采样算法·调度器·步数·CFG·降噪·种子 / 正负向提示词 / 图像尺寸）。
/// 保存时把参数回写进工作流文件（UI 格式自动转换为 API 格式）。
/// </summary>
public partial class ComfyWorkflowEditorWindow : Window
{
    private readonly ComfyWorkflowEntry _entry;
    private bool _loading;

    public ComfyWorkflowEditorWindow(ComfyWorkflowEntry entry)
    {
        InitializeComponent();
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));

        // 下拉框可选项
        SamplerBox.ItemsSource = ComfyWorkflowParser.SamplerNames;
        SchedulerBox.ItemsSource = ComfyWorkflowParser.Schedulers;

        WorkflowNameText.Text = $"{_entry.Name}  ·  {_entry.FilePath}";
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadFromParams();
    }

    /// <summary>把工作流解析出的参数填充到控件。</summary>
    private void LoadFromParams()
    {
        _loading = true;
        try
        {
            var p = _entry.Params;
            if (p == null)
            {
                // 尚未解析：尝试从文件解析
                try
                {
                    p = ComfyWorkflowParser.ParseFile(_entry.FilePath, out var wasUi);
                    _entry.Params = p;
                    if (wasUi)
                    {
                        // 首次导入 UI 格式时自动转存为 API 格式，保证提交可用
                        _entry.FilePath = ComfyWorkflowParser.ConvertUiToApiFile(_entry.FilePath);
                        WorkflowNameText.Text = $"{_entry.Name}  ·  {_entry.FilePath}";
                    }
                }
                catch (Exception ex)
                {
                    SaveHintText.Text = $"解析失败：{ex.Message}";
                    return;
                }
            }

            CheckpointBox.Text = p.Checkpoint;
            LoraBox.Text = p.LoRA;
            LoraStrengthSlider.Value = Clamp(p.LoraStrength, 0, 2);
            LoraStrengthText.Text = p.LoraStrength.ToString("0.00");
            ClipBox.Text = p.Clip;
            VaeBox.Text = p.Vae;

            SelectItem(SamplerBox, p.SamplerName);
            SelectItem(SchedulerBox, p.Scheduler);
            StepsBox.Text = p.Steps.ToString();
            CfgBox.Text = p.Cfg.ToString("0.#");
            DenoiseBox.Text = p.Denoise.ToString("0.##");
            SeedBox.Text = p.Seed.ToString();
            WidthBox.Text = p.Width.ToString();
            HeightBox.Text = p.Height.ToString();
            PositivePromptBox.Text = p.PositivePrompt;
            NegativePromptBox.Text = p.NegativePrompt;

            SaveHintText.Text = $"解析到 {NodeSummaryText(p)}，修改后点击「保存参数」生效";
        }
        finally
        {
            _loading = false;
        }
    }

    private static string NodeSummaryText(ComfyWorkflowParams p)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(p.Checkpoint)) parts.Add(p.Checkpoint);
        if (!string.IsNullOrEmpty(p.LoRA)) parts.Add($"LoRA×{p.LoraStrength:0.0}");
        if (!string.IsNullOrEmpty(p.Vae)) parts.Add(p.Vae);
        parts.Add($"{p.SamplerName}/{p.Scheduler}");
        return string.Join(" · ", parts);
    }

    private static double Clamp(double v, double min, double max)
        => v < min ? min : v > max ? max : v;

    private static void SelectItem(System.Windows.Controls.ComboBox box, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var match = box.Items.Cast<object>()
            .FirstOrDefault(i => string.Equals(i?.ToString(), value, StringComparison.OrdinalIgnoreCase));
        box.SelectedItem = match ?? box.SelectedItem;
        if (box.SelectedItem == null && box.IsEditable)
            box.Text = value;
    }

    private void LoraStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        LoraStrengthText.Text = e.NewValue.ToString("0.00");
    }

    /// <summary>从文件重新解析并覆盖当前编辑内容。</summary>
    private void Reparse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _entry.Params = ComfyWorkflowParser.ParseFile(_entry.FilePath, out var wasUi);
            if (wasUi)
            {
                _entry.FilePath = ComfyWorkflowParser.ConvertUiToApiFile(_entry.FilePath);
                WorkflowNameText.Text = $"{_entry.Name}  ·  {_entry.FilePath}";
            }
            LoadFromParams();
        }
        catch (Exception ex)
        {
            MessageDialog.Show("解析失败", ex.Message);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 校验
        if (!TryReadPositiveInt(StepsBox.Text, out var steps) || steps < 1 || steps > 150)
        {
            MessageDialog.Show("参数错误", "采样步数应为 1~150 之间的整数");
            return;
        }
        if (!TryReadDouble(CfgBox.Text, out var cfg) || cfg < 0 || cfg > 30)
        {
            MessageDialog.Show("参数错误", "CFG 引导强度应为 0~30 之间的数值");
            return;
        }
        if (!TryReadDouble(DenoiseBox.Text, out var denoise) || denoise < 0 || denoise > 1)
        {
            MessageDialog.Show("参数错误", "重绘幅度 (Denoise) 应为 0~1 之间的数值");
            return;
        }
        if (!TryReadPositiveInt(WidthBox.Text, out var width) || width < 64 || width > 4096 ||
            !TryReadPositiveInt(HeightBox.Text, out var height) || height < 64 || height > 4096)
        {
            MessageDialog.Show("参数错误", "图像尺寸应为 64~4096 之间的整数");
            return;
        }

        var p = new ComfyWorkflowParams
        {
            Checkpoint = CheckpointBox.Text.Trim(),
            LoRA = LoraBox.Text.Trim(),
            LoraStrength = Math.Round(LoraStrengthSlider.Value, 2),
            Clip = ClipBox.Text.Trim(),
            Vae = VaeBox.Text.Trim(),
            SamplerName = SamplerBox.SelectedItem?.ToString() ?? "euler",
            Scheduler = SchedulerBox.SelectedItem?.ToString() ?? "normal",
            Steps = steps,
            Cfg = cfg,
            Denoise = denoise,
            Seed = int.TryParse(SeedBox.Text.Trim(), out var seed) ? seed : -1,
            Width = width,
            Height = height,
            PositivePrompt = PositivePromptBox.Text.TrimEnd(),
            NegativePrompt = NegativePromptBox.Text.TrimEnd()
        };

        try
        {
            // 回写工作流文件（UI 格式自动转换 API 格式）
            var target = ComfyWorkflowParser.SaveWithParams(_entry.FilePath, p);
            _entry.FilePath = target;
            _entry.Params = p;
            SaveHintText.Text = "✓ 参数已保存并写回工作流文件";
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageDialog.Show("保存失败", ex.Message);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private static bool TryReadPositiveInt(string text, out int value)
        => int.TryParse(text.Trim(), out value) && value > 0;

    private static bool TryReadDouble(string text, out double value)
        => double.TryParse(text.Trim(), out value);
}
