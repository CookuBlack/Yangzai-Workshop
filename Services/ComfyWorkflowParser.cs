using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using YangzaiWorkshop.Models;

namespace YangzaiWorkshop.Services;

/// <summary>
/// ComfyUI 工作流解析器。
/// 负责：
///  1. 读取工作流 JSON（支持 ComfyUI 的 API 格式与网页 UI 格式）并自动提取可调参数
///     （checkpoint 模型 / LoRA 名称与权重 / CLIP / VAE / KSampler 采样算法·调度器·步数·CFG·降噪·种子 / 正负向提示词 / 图像尺寸）；
///  2. 将用户编辑后的参数回写进工作流 JSON（注入对应节点），供提交时使用。
/// 内部统一将 UI 格式转换为 API 格式（ComfyUI「Export (API)」格式）再读写，
/// 以保证生成时可直接提交到 /prompt 接口。
/// </summary>
public static class ComfyWorkflowParser
{
    /// <summary>默认内置工作流目录（位于应用用户数据目录下，升级不清理；用户把默认工作流放到此处）</summary>
    public static string DefaultWorkflowsDir =>
        Path.Combine(App.WorkRoot, "ComfyWorkflows");

    /// <summary>用户导入的工作流目录（保持与默认内置一致，便于统一管理）</summary>
    public static string UserWorkflowsDir => DefaultWorkflowsDir;

    // ===== 可选值（供编辑窗口下拉框使用） =====

    /// <summary>采样算法（KSampler sampler_name 可选值）</summary>
    public static readonly string[] SamplerNames =
    {
        "euler", "euler_ancestral", "heun", "heunpp2",
        "dpm_2", "dpm_2_ancestral", "lms", "dpm_fast", "dpm_adaptive",
        "dpmpp_2s_ancestral", "dpmpp_2m", "dpmpp_2m_sde", "dpmpp_3m_sde",
        "ddim", "uni_pc", "uni_pc_bh2"
    };

    /// <summary>调度器（KSampler scheduler 可选值）</summary>
    public static readonly string[] Schedulers =
    {
        "normal", "karras", "exponential", "sgm_uniform", "simple", "ddim_uniform"
    };

    // ===== 节点类型 → widget 顺序（UI 格式的 widgets_values 映射） =====

    /// <summary>常见节点类型的 widget 顺序（UI 格式 widgets_values 按此顺序对应）</summary>
    private static readonly Dictionary<string, string[]> NodeWidgetOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CheckpointLoaderSimple"] = new[] { "ckpt_name" },
        ["CheckpointLoader"] = new[] { "ckpt_name", "config_name" },
        ["LoraLoader"] = new[] { "lora_name", "strength_model", "strength_clip" },
        ["LoraLoaderModelOnly"] = new[] { "lora_name", "strength_model" },
        ["CLIPLoader"] = new[] { "clip_name", "type", "device" },
        ["VAELoader"] = new[] { "vae_name" },
        ["CLIPTextEncode"] = new[] { "text" },
        ["KSampler"] = new[] { "seed", "control_after_generate", "steps", "cfg", "sampler_name", "scheduler", "denoise" },
        ["KSamplerAdvanced"] = new[] { "seed", "control_after_generate", "steps", "cfg", "sampler_name", "scheduler", "start_at_step", "end_at_step", "add_noise" },
        ["EmptyLatentImage"] = new[] { "width", "height", "batch_size" },
        ["EmptySD3LatentImage"] = new[] { "width", "height", "batch_size" },
        ["LoadImage"] = new[] { "image", "upload" },
        ["SaveImage"] = new[] { "filename_prefix" },
        ["SaveAnimatedWEBP"] = new[] { "filename_prefix", "fps", "lossless", "quality", "method" },
        ["SaveAnimatedPNG"] = new[] { "filename_prefix", "fps", "compress_level" },
        ["ImageScale"] = new[] { "upscale_method", "width", "height", "crop" },
        ["ImageScaleBy"] = new[] { "upscale_method", "scale_by" },
        ["ControlNetLoader"] = new[] { "control_net_name" },
        ["ConditioningZeroOut"] = Array.Empty<string>(),
        ["VAEEncode"] = Array.Empty<string>(),
        ["VAEDecode"] = Array.Empty<string>(),
        ["PreviewImage"] = Array.Empty<string>(),
    };

    // ===== 主入口 =====

    /// <summary>
    /// 解析工作流文件并提取参数。内部会把 UI 格式转换为 API 格式，
    /// 若需要（原文件为 UI 格式），可调用 <see cref="ConvertUiToApiFile"/> 落盘。
    /// </summary>
    public static ComfyWorkflowParams ParseFile(string filePath, out bool wasUiFormat)
    {
        wasUiFormat = false;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("工作流文件不存在", filePath);

        var jsonText = File.ReadAllText(filePath);
        return Parse(jsonText, out wasUiFormat);
    }

    /// <summary>解析工作流 JSON 文本并提取参数。</summary>
    public static ComfyWorkflowParams Parse(string jsonText, out bool wasUiFormat)
    {
        wasUiFormat = false;
        var obj = NormalizeToApi(jsonText, out wasUiFormat);
        if (obj == null)
            throw new InvalidDataException("工作流 JSON 格式错误（顶层必须是对象）");

        var p = new ComfyWorkflowParams();

        foreach (var kv in obj)
        {
            if (kv.Value is not JsonObject node) continue;
            var classType = node["class_type"]?.GetValue<string>() ?? "";
            if (node["inputs"] is not JsonObject inputs) continue;

            switch (classType)
            {
                case "CheckpointLoaderSimple":
                    p.Checkpoint = ReadString(inputs, "ckpt_name") ?? p.Checkpoint;
                    break;
                case "CheckpointLoader":
                    p.Checkpoint = ReadString(inputs, "ckpt_name") ?? p.Checkpoint;
                    break;
                case "LoraLoader":
                    p.LoRA = ReadString(inputs, "lora_name") ?? p.LoRA;
                    p.LoraStrength = ReadDouble(inputs, "strength_model", p.LoraStrength);
                    break;
                case "LoraLoaderModelOnly":
                    p.LoRA = ReadString(inputs, "lora_name") ?? p.LoRA;
                    p.LoraStrength = ReadDouble(inputs, "strength_model", p.LoraStrength);
                    break;
                case "CLIPLoader":
                    p.Clip = ReadString(inputs, "clip_name") ?? p.Clip;
                    break;
                case "VAELoader":
                    p.Vae = ReadString(inputs, "vae_name") ?? p.Vae;
                    break;
                case "KSampler":
                case "KSamplerAdvanced":
                    p.SamplerName = ReadString(inputs, "sampler_name") ?? p.SamplerName;
                    p.Scheduler = ReadString(inputs, "scheduler") ?? p.Scheduler;
                    p.Steps = ReadInt(inputs, "steps", p.Steps);
                    p.Cfg = ReadDouble(inputs, "cfg", p.Cfg);
                    p.Denoise = ReadDouble(inputs, "denoise", p.Denoise);
                    p.Seed = ReadInt(inputs, "seed", p.Seed);
                    break;
                case "CLIPTextEncode":
                    var text = ReadString(inputs, "text") ?? "";
                    if (IsNegativePrompt(text)) p.NegativePrompt = text;
                    else if (string.IsNullOrEmpty(p.PositivePrompt)) p.PositivePrompt = text;
                    break;
                case "EmptyLatentImage":
                case "EmptySD3LatentImage":
                    p.Width = ReadInt(inputs, "width", p.Width);
                    p.Height = ReadInt(inputs, "height", p.Height);
                    break;
            }
        }

        return p;
    }

    /// <summary>
    /// 将用户编辑后的参数回写进工作流 JSON 文本并返回新文本（不落盘）。
    /// 参数为 null 或对应节点不存在时跳过该项，保持工作流其它内容不变。
    /// </summary>
    public static string Apply(string jsonText, ComfyWorkflowParams p)
    {
        var obj = NormalizeToApi(jsonText, out _);
        if (obj == null)
            throw new InvalidDataException("工作流 JSON 格式错误（顶层必须是对象）");

        foreach (var kv in obj)
        {
            if (kv.Value is not JsonObject node) continue;
            var classType = node["class_type"]?.GetValue<string>() ?? "";
            if (node["inputs"] is not JsonObject inputs) continue;

            switch (classType)
            {
                case "CheckpointLoaderSimple":
                case "CheckpointLoader":
                    if (!string.IsNullOrEmpty(p.Checkpoint)) inputs["ckpt_name"] = p.Checkpoint;
                    break;
                case "LoraLoader":
                    if (!string.IsNullOrEmpty(p.LoRA)) inputs["lora_name"] = p.LoRA;
                    inputs["strength_model"] = p.LoraStrength;
                    if (inputs.ContainsKey("strength_clip")) inputs["strength_clip"] = p.LoraStrength;
                    break;
                case "LoraLoaderModelOnly":
                    if (!string.IsNullOrEmpty(p.LoRA)) inputs["lora_name"] = p.LoRA;
                    inputs["strength_model"] = p.LoraStrength;
                    break;
                case "CLIPLoader":
                    if (!string.IsNullOrEmpty(p.Clip)) inputs["clip_name"] = p.Clip;
                    break;
                case "VAELoader":
                    if (!string.IsNullOrEmpty(p.Vae)) inputs["vae_name"] = p.Vae;
                    break;
                case "KSampler":
                case "KSamplerAdvanced":
                    inputs["sampler_name"] = p.SamplerName;
                    inputs["scheduler"] = p.Scheduler;
                    inputs["steps"] = p.Steps;
                    inputs["cfg"] = p.Cfg;
                    inputs["denoise"] = p.Denoise;
                    inputs["seed"] = p.Seed;
                    break;
                case "CLIPTextEncode":
                    var text = ReadString(inputs, "text") ?? "";
                    if (IsNegativePrompt(text))
                    {
                        if (!string.IsNullOrEmpty(p.NegativePrompt)) inputs["text"] = p.NegativePrompt;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(p.PositivePrompt)) inputs["text"] = p.PositivePrompt;
                    }
                    break;
                case "EmptyLatentImage":
                case "EmptySD3LatentImage":
                    inputs["width"] = p.Width;
                    inputs["height"] = p.Height;
                    break;
            }
        }

        return obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// 将工作流解析并应用参数后保存为文件。
    /// 若原文件为 UI 格式，则同时转换为 API 格式（ComfyUI「Export (API)」）保存，
    /// 保证提交 /prompt 时可用。
    /// </summary>
    /// <returns>实际保存的工作流文件路径（UI 格式会转换成新 API 文件并返回新路径）</returns>
    public static string SaveWithParams(string filePath, ComfyWorkflowParams p)
    {
        var jsonText = File.ReadAllText(filePath);
        var apiText = Apply(jsonText, p);

        var target = filePath;
        if (NormalizeToApi(jsonText, out var wasUi) == null) wasUi = true;
        if (wasUi)
        {
            // UI 格式 → 另存为 .api.json（原文件保留，便于用户回到 ComfyUI 再次编辑）
            target = Path.Combine(
                Path.GetDirectoryName(filePath) ?? App.WorkRoot,
                Path.GetFileNameWithoutExtension(filePath) + ".api.json");
        }
        File.WriteAllText(target, apiText);
        return target;
    }

    /// <summary>把 UI 格式工作流转换为 API 格式文本；已是 API 格式则原样返回。</summary>
    public static string ConvertUiToApiText(string jsonText)
    {
        var obj = NormalizeToApi(jsonText, out _);
        return obj?.ToJsonString() ?? jsonText;
    }

    /// <summary>把 UI 格式工作流文件转换为 API 格式文件，返回新文件路径；已是 API 格式返回原路径。</summary>
    public static string ConvertUiToApiFile(string filePath)
    {
        var jsonText = File.ReadAllText(filePath);
        if (!IsUiFormat(jsonText)) return filePath;
        var apiText = ConvertUiToApiText(jsonText);
        var target = Path.Combine(
            Path.GetDirectoryName(filePath) ?? App.WorkRoot,
            Path.GetFileNameWithoutExtension(filePath) + ".api.json");
        File.WriteAllText(target, apiText);
        return target;
    }

    /// <summary>判断工作流 JSON 是否为网页 UI 格式（顶层含 nodes 数组）。</summary>
    public static bool IsUiFormat(string jsonText)
    {
        try
        {
            if (JsonNode.Parse(jsonText) is JsonObject obj)
                return obj.ContainsKey("nodes");
        }
        catch { }
        return false;
    }

    // ===== 内部工具 =====

    /// <summary>
    /// 把工作流统一归一化为 API 格式的 JsonObject（nodeId → {class_type, inputs}）。
    /// 输入若是 UI 格式（顶层 nodes 数组 + links），先转换为 API 格式。
    /// </summary>
    private static JsonObject? NormalizeToApi(string jsonText, out bool wasUiFormat)
    {
        wasUiFormat = false;
        try
        {
            var root = JsonNode.Parse(jsonText) as JsonObject;
            if (root == null) return null;

            // API 格式：顶层直接是 nodeId 字典
            if (!root.ContainsKey("nodes"))
            {
                foreach (var kv in root)
                {
                    if (kv.Value is JsonObject node && node["class_type"] is JsonValue)
                        return root;
                }
                return root; // 仍是对象，交给调用方后续处理
            }

            wasUiFormat = true;
            return UiToApi(root);
        }
        catch { return null; }
    }

    /// <summary>UI 格式 → API 格式转换（解析 links 连接关系 + widgets_values 常量值）。</summary>
    private static JsonObject? UiToApi(JsonObject root)
    {
        if (root["nodes"] is not JsonArray uiNodes) return null;

        // links: [linkId, originNodeId, originSlot, targetNodeId, targetSlot, type]
        var linkTargets = new Dictionary<int, (string Node, int Slot)>();
        if (root["links"] is JsonArray links)
        {
            foreach (var l in links)
            {
                if (l is not JsonArray arr || arr.Count < 4) continue;
                var linkId = arr[0]?.GetValue<int>() ?? -1;
                var originNode = arr[1]?.GetValue<int>() ?? -1;
                var originSlot = arr[2]?.GetValue<int>() ?? -1;
                if (linkId >= 0) linkTargets[linkId] = (originNode.ToString(), originSlot);
            }
        }

        var api = new JsonObject();
        foreach (var n in uiNodes)
        {
            if (n is not JsonObject node) continue;
            var id = node["id"]?.GetValue<int>() ?? -1;
            var type = node["type"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(type) || id < 0) continue;

            var inputs = new JsonObject();
            var widgetValues = new List<JsonNode?>();
            if (node["widgets_values"] is JsonArray wv)
                foreach (var v in wv) widgetValues.Add(v?.DeepClone());

            // 收集连接的输入端口名称（connected: {name, link}）
            var connectedInputs = new Dictionary<string, int>();
            var plainInputs = new List<string>();
            if (node["inputs"] is JsonArray nodeInputs)
            {
                foreach (var i in nodeInputs)
                {
                    if (i is not JsonObject input) continue;
                    var name = input["name"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrEmpty(name)) continue;
                    if (input["link"]?.GetValue<int?>() is int linkId && linkTargets.TryGetValue(linkId, out var t))
                        connectedInputs[name] = linkId;
                    else
                        plainInputs.Add(name);
                }
            }

            // 连接的输入 → 引用数组 [nodeId, slot]
            foreach (var (name, linkId) in connectedInputs)
            {
                var t = linkTargets[linkId];
                inputs[name] = new JsonArray(t.Node, t.Slot);
            }

            // 未连接的输入 → 按节点类型 widget 顺序取 widgets_values
            if (widgetValues.Count > 0)
            {
                var order = NodeWidgetOrder.TryGetValue(type, out var o) ? o : Array.Empty<string>();
                var widgetIndex = 0;
                // 先填充已声明 widget 顺序中且未连接的输入
                foreach (var wname in order)
                {
                    if (connectedInputs.ContainsKey(wname)) continue;
                    if (widgetIndex >= widgetValues.Count) break;
                    var val = widgetValues[widgetIndex];
                    if (val != null && val is not JsonObject && val is not JsonArray)
                        inputs[wname] = val;
                    widgetIndex++;
                }
                // 剩余 widgets_values 兜底给其它未连接输入
                foreach (var pname in plainInputs)
                {
                    if (inputs.ContainsKey(pname)) continue;
                    if (widgetIndex >= widgetValues.Count) break;
                    var val = widgetValues[widgetIndex];
                    if (val != null && val is not JsonObject && val is not JsonArray)
                        inputs[pname] = val;
                    widgetIndex++;
                }
            }

            api[id.ToString()] = new JsonObject
            {
                ["class_type"] = type,
                ["inputs"] = inputs
            };
        }
        return api;
    }

    private static string? ReadString(JsonObject inputs, string key)
    {
        if (inputs[key] is not JsonValue v) return null;
        try
        {
            var s = v.GetValue<string>();
            if (string.IsNullOrWhiteSpace(s)) return null;
            return s;
        }
        catch { return null; }
    }

    private static int ReadInt(JsonObject inputs, string key, int fallback)
    {
        if (inputs[key] is not JsonValue v) return fallback;
        try { return v.GetValue<int>(); }
        catch { try { return (int)v.GetValue<double>(); } catch { return fallback; } }
    }

    private static double ReadDouble(JsonObject inputs, string key, double fallback)
    {
        if (inputs[key] is not JsonValue v) return fallback;
        try { return v.GetValue<double>(); }
        catch { return fallback; }
    }

    /// <summary>判断文本是否为负向提示词（含常见负面词）</summary>
    public static bool IsNegativePrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.ToLowerInvariant();
        return t.Contains("lowres")
            || t.Contains("bad anatomy")
            || t.Contains("bad hands")
            || t.Contains("worst quality")
            || t.Contains("low quality")
            || t.Contains("negative prompt")
            || t.Contains("negative");
    }

    /// <summary>列出工作流目录（默认内置目录）中的工作流 JSON 文件。</summary>
    public static List<string> ListWorkflowFiles(string dir)
    {
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f)
            .ToList();
    }
}
