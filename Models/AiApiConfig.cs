using System;
using System.Collections.Generic;
using System.Linq;

namespace YangzaiWorkshop.Models;

/// <summary>AI 服务商类型。</summary>
public enum ApiProvider
{
    /// <summary>自定义（OpenAI 兼容格式，任意地址）</summary>
    Custom,
    /// <summary>Agnes AI（本软件内置服务）</summary>
    Agnes,
    /// <summary>OpenAI</summary>
    OpenAI,
    /// <summary>DeepSeek（深度求索）</summary>
    DeepSeek,
    /// <summary>字节跳动（火山方舟 / 豆包）</summary>
    ByteDance,
    /// <summary>千问（阿里云百炼 / DashScope）</summary>
    Qwen,
    /// <summary>魔搭社区（ModelScope）</summary>
    ModelScope
}

/// <summary>AI 接口通道：文本 / 图片 / 视频 各自独立配置。</summary>
public enum ApiChannel { Text, Image, Video }

/// <summary>
/// 一组独立的接口配置（文本、图片、视频三类通道各自独立管理）。
/// 文本接口用于聊天/剧本/提示词生成，图片接口用于 AI 生图，视频接口用于 AI 生视频。
/// </summary>
public sealed class AiApiProfile
{
    public ApiProvider Provider { get; set; } = ApiProvider.Agnes;
    /// <summary>接口地址（如 https://api.deepseek.com/v1）</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>API 密钥</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>模型 ID</summary>
    public string ModelId { get; set; } = "";
    /// <summary>备注（可选）</summary>
    public string Note { get; set; } = "";

    public AiApiProfile Clone() => new()
    {
        Provider = Provider,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        ModelId = ModelId,
        Note = Note
    };

    /// <summary>是否已配置（地址与模型均非空）——仅计算属性，不写入配置文件</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ModelId);
}

/// <summary>
/// 模型能力信息：用于在 AI 接口配置窗口中提示用户「该模型支持什么」，
/// 例如是否支持视觉理解（对话时上传图片）、是否支持多图参考（生图时传入多张参考图）等。
/// </summary>
public sealed class AiModelInfo
{
    public ApiProvider Provider { get; init; }
    public ApiChannel Channel { get; init; }
    public string ModelId { get; init; } = "";
    /// <summary>展示名称（如“DeepSeek V4 Flash”）</summary>
    public string DisplayName { get; init; } = "";
    /// <summary>是否支持视觉理解（文本对话时可将图片作为输入）</summary>
    public bool SupportsVision { get; init; }
    /// <summary>是否支持多图参考（生图时可传入多张参考图）</summary>
    public bool SupportsMultiImage { get; init; }
    /// <summary>最大参考图数量（0=不支持参考图，1=仅单图，&gt;1=多图）</summary>
    public int MaxReferenceImages { get; init; }
    /// <summary>是否为 agnes-image 系列（使用档位式 size + ratio）</summary>
    public bool IsAgnesImage { get; init; }
    /// <summary>是否为 Flash 视频模型（固定 720P，不支持参考视频）</summary>
    public bool IsFlashVideo { get; init; }
    /// <summary>可用视频分辨率档位（仅视频模型有意义）</summary>
    public string[] VideoLevels { get; init; } = Array.Empty<string>();
    /// <summary>能力说明（供 UI 展示）</summary>
    public string Description { get; init; } = "";
}

/// <summary>
/// 一条 ComfyUI 工作流条目：默认内置目录或用户手动导入。
/// 工作流文件本身（API 格式 JSON）与解析出的可调参数（Params）分开保存，
/// 用户在「工作流参数编辑」窗口修改后回写 Params 并应用到工作流文件。
/// </summary>
public sealed class ComfyWorkflowEntry
{
    /// <summary>显示名称</summary>
    public string Name { get; set; } = "";
    /// <summary>工作流 JSON 文件路径（API 格式，可直接提交到 ComfyUI /prompt）</summary>
    public string FilePath { get; set; } = "";
    /// <summary>来源：BuiltIn=默认内置 / User=用户导入</summary>
    public string Source { get; set; } = "User";
    /// <summary>自动解析出的可调参数（checkpoint / lora / clip / vae / ksampler 等），可编辑保存</summary>
    public ComfyWorkflowParams? Params { get; set; }

    public ComfyWorkflowEntry Clone() => new()
    {
        Name = Name,
        FilePath = FilePath,
        Source = Source,
        Params = Params?.Clone()
    };
}

/// <summary>
/// ComfyUI 工作流可调参数：由 ComfyWorkflowParser 从工作流 JSON 自动解析，
/// 用户可在工作流参数编辑窗口手动调整（采样算法/调度器可选，LoRA 权重可调等）后保存。
/// </summary>
public sealed class ComfyWorkflowParams
{
    /// <summary>基础模型名称（CheckpointLoaderSimple 的 ckpt_name）</summary>
    public string Checkpoint { get; set; } = "";
    /// <summary>LoRA 名称（LoraLoader 的 lora_name，无则留空）</summary>
    public string LoRA { get; set; } = "";
    /// <summary>LoRA 权重（LoraLoader 的 strength_model / strength_clip，默认 0.7）</summary>
    public double LoraStrength { get; set; } = 0.7;
    /// <summary>CLIP 名称（CLIPLoader 的 clip_name，无则留空）</summary>
    public string Clip { get; set; } = "";
    /// <summary>VAE 名称（VAELoader 的 vae_name，无则留空）</summary>
    public string Vae { get; set; } = "";
    /// <summary>采样算法（KSampler 的 sampler_name，用户可选）</summary>
    public string SamplerName { get; set; } = "euler";
    /// <summary>调度器（KSampler 的 scheduler，用户可选）</summary>
    public string Scheduler { get; set; } = "normal";
    /// <summary>采样步数（KSampler 的 steps）</summary>
    public int Steps { get; set; } = 20;
    /// <summary>CFG 引导强度（KSampler 的 cfg）</summary>
    public double Cfg { get; set; } = 7.0;
    /// <summary>重绘幅度（KSampler 的 denoise，0~1）</summary>
    public double Denoise { get; set; } = 1.0;
    /// <summary>随机种子（KSampler 的 seed，-1 表示随机）</summary>
    public int Seed { get; set; } = -1;
    /// <summary>正向提示词（正向 CLIPTextEncode 的 text；生图时会被用户输入覆盖）</summary>
    public string PositivePrompt { get; set; } = "";
    /// <summary>负向提示词（负向 CLIPTextEncode 的 text，无则留空）</summary>
    public string NegativePrompt { get; set; } = "";
    /// <summary>输出宽度（EmptyLatentImage 的 width）</summary>
    public int Width { get; set; } = 1024;
    /// <summary>输出高度（EmptyLatentImage 的 height）</summary>
    public int Height { get; set; } = 1024;

    public ComfyWorkflowParams Clone() => new()
    {
        Checkpoint = Checkpoint,
        LoRA = LoRA,
        LoraStrength = LoraStrength,
        Clip = Clip,
        Vae = Vae,
        SamplerName = SamplerName,
        Scheduler = Scheduler,
        Steps = Steps,
        Cfg = Cfg,
        Denoise = Denoise,
        Seed = Seed,
        PositivePrompt = PositivePrompt,
        NegativePrompt = NegativePrompt,
        Width = Width,
        Height = Height
    };
}

/// <summary>
/// AI 服务商预设与模型能力库。
/// 能力数据基于各官方文档整理（DeepSeek / 火山方舟 / 千问百炼 / ModelScope / OpenAI / ComfyUI）。
/// </summary>
public static class AiModelCatalog
{
    // ===== 服务商基础信息 =====

    /// <summary>服务商显示名称</summary>
    public static string ProviderName(ApiProvider p) => p switch
    {
        ApiProvider.Custom => "自定义",
        ApiProvider.Agnes => "Agnes AI",
        ApiProvider.OpenAI => "OpenAI",
        ApiProvider.DeepSeek => "DeepSeek",
        ApiProvider.ByteDance => "字节跳动",
        ApiProvider.Qwen => "千问",
        ApiProvider.ModelScope => "ModelScope",
        _ => "未知"
    };

    /// <summary>服务商默认接口地址（文本/图片/视频多为 OpenAI 兼容，Qwen 生图/生视频为 DashScope 原生）</summary>
    public static string DefaultBaseUrl(ApiProvider p, ApiChannel channel) => p switch
    {
        ApiProvider.Agnes => "https://api.agnes-ai.cn/v1",
        ApiProvider.OpenAI => "https://api.openai.com/v1",
        ApiProvider.DeepSeek => "https://api.deepseek.com/v1",
        ApiProvider.ByteDance => "https://ark.cn-beijing.volces.com/api/v3",
        ApiProvider.Qwen => channel is ApiChannel.Image or ApiChannel.Video
            ? "https://dashscope.aliyuncs.com/api/v1"
            : "https://dashscope.aliyuncs.com/compatible-mode/v1",
        ApiProvider.ModelScope => "https://api-inference.modelscope.cn/v1",
        _ => ""
    };

    /// <summary>默认模型 ID</summary>
    public static string DefaultModel(ApiProvider p, ApiChannel channel) => p switch
    {
        ApiProvider.Agnes => channel switch
        {
            ApiChannel.Text => "gpt-4o-mini",
            ApiChannel.Image => "agnes-image-2.1-flash",
            _ => "agnes-video-2.5-flash"
        },
        ApiProvider.OpenAI => channel switch
        {
            ApiChannel.Text => "gpt-4o-mini",
            ApiChannel.Image => "gpt-image-1",
            _ => "sora-2"
        },
        ApiProvider.DeepSeek => channel switch
        {
            ApiChannel.Text => "deepseek-v4-flash",
            _ => ""
        },
        ApiProvider.ByteDance => channel switch
        {
            ApiChannel.Text => "doubao-seed-1-6-250615",
            ApiChannel.Image => "doubao-seedream-4-0-250828",
            _ => "doubao-seedance-1-0-pro-250528"
        },
        ApiProvider.Qwen => channel switch
        {
            ApiChannel.Text => "qwen3.7-plus",
            ApiChannel.Image => "qwen-image-3.0",
            _ => "qwen-video-max"
        },
        ApiProvider.ModelScope => channel switch
        {
            ApiChannel.Text => "Qwen/Qwen3.8-27B",
            _ => ""
        },
        _ => ""
    };

    // ===== 模型能力库 =====

    /// <summary>内置模型能力描述（按服务商 + 通道 + 模型 ID 前缀匹配）</summary>
    private static readonly List<AiModelInfo> ModelInfos = new()
    {
        // ===== 文本 / 视觉 =====
        new() { Provider = ApiProvider.DeepSeek, Channel = ApiChannel.Text, ModelId = "deepseek-v4-flash", DisplayName = "DeepSeek V4 Flash", Description = "通用文本模型，不支持视觉理解", SupportsVision = false },
        new() { Provider = ApiProvider.DeepSeek, Channel = ApiChannel.Text, ModelId = "deepseek-v4-pro", DisplayName = "DeepSeek V4 Pro", Description = "旗舰文本模型，不支持视觉理解", SupportsVision = false },
        new() { Provider = ApiProvider.DeepSeek, Channel = ApiChannel.Text, ModelId = "deepseek-v4-flash-vision-exp", DisplayName = "DeepSeek V4 Flash Vision（实验）", Description = "支持视觉理解（图片输入），不支持生图/生视频", SupportsVision = true },
        new() { Provider = ApiProvider.DeepSeek, Channel = ApiChannel.Text, ModelId = "deepseek-chat", DisplayName = "DeepSeek Chat（旧）", Description = "旧版通用对话模型", SupportsVision = false },
        new() { Provider = ApiProvider.DeepSeek, Channel = ApiChannel.Text, ModelId = "deepseek-reasoner", DisplayName = "DeepSeek Reasoner（旧）", Description = "旧版推理模型", SupportsVision = false },

        new() { Provider = ApiProvider.Qwen, Channel = ApiChannel.Text, ModelId = "qwen3.8-max", DisplayName = "千问 3.8 Max", Description = "旗舰文本/视觉模型，支持图片理解", SupportsVision = true },
        new() { Provider = ApiProvider.Qwen, Channel = ApiChannel.Text, ModelId = "qwen3.7-plus", DisplayName = "千问 3.7 Plus", Description = "均衡文本/视觉模型，支持图片理解", SupportsVision = true },
        new() { Provider = ApiProvider.Qwen, Channel = ApiChannel.Text, ModelId = "qwen3.8-flash", DisplayName = "千问 3.8 Flash", Description = "高性价比文本/视觉模型", SupportsVision = true },
        new() { Provider = ApiProvider.Qwen, Channel = ApiChannel.Text, ModelId = "qwen-vl-max", DisplayName = "千问 VL Max", Description = "专业视觉理解模型", SupportsVision = true },
        new() { Provider = ApiProvider.Qwen, Channel = ApiChannel.Text, ModelId = "qwen3-vl-plus", DisplayName = "千问 3 VL Plus", Description = "视觉理解模型", SupportsVision = true },
        new() { Provider = ApiProvider.Qwen, Channel = ApiChannel.Text, ModelId = "qwen-vl-plus", DisplayName = "千问 VL Plus", Description = "视觉理解模型", SupportsVision = true },

        new() { Provider = ApiProvider.OpenAI, Channel = ApiChannel.Text, ModelId = "gpt-4o", DisplayName = "GPT-4o", Description = "支持视觉理解", SupportsVision = true },
        new() { Provider = ApiProvider.OpenAI, Channel = ApiChannel.Text, ModelId = "gpt-4o-mini", DisplayName = "GPT-4o mini", Description = "支持视觉理解", SupportsVision = true },
        new() { Provider = ApiProvider.OpenAI, Channel = ApiChannel.Text, ModelId = "gpt-4.1", DisplayName = "GPT-4.1", Description = "支持视觉理解", SupportsVision = true },
        new() { Provider = ApiProvider.OpenAI, Channel = ApiChannel.Text, ModelId = "gpt-5", DisplayName = "GPT-5", Description = "支持视觉理解", SupportsVision = true },

        new() { Provider = ApiProvider.ByteDance, Channel = ApiChannel.Text, ModelId = "doubao-seed-1-6-250615", DisplayName = "豆包 Seed 1.6", Description = "支持视觉理解", SupportsVision = true },
        new() { Provider = ApiProvider.ByteDance, Channel = ApiChannel.Text, ModelId = "doubao-seed-1-6-flash-250615", DisplayName = "豆包 Seed 1.6 Flash", Description = "支持视觉理解", SupportsVision = true },
        new() { Provider = ApiProvider.ByteDance, Channel = ApiChannel.Text, ModelId = "doubao-pro-32k", DisplayName = "豆包 Pro 32K", Description = "支持视觉理解", SupportsVision = true },

        new() { Provider = ApiProvider.ModelScope, Channel = ApiChannel.Text, ModelId = "Qwen/Qwen3.8-27B", DisplayName = "Qwen3.8-27B（开源）", Description = "原生支持图像/视频/文档理解", SupportsVision = true },

        // ===== Agnes 文本 / 视觉 =====
        new() { Provider = ApiProvider.Agnes, Channel = ApiChannel.Text, ModelId = "agnes-2.5-flash", DisplayName = "Agnes 2.5 Flash", Description = "通用文本对话模型（Agnes AI）", SupportsVision = true },
        new() { Provider = ApiProvider.Agnes, Channel = ApiChannel.Text, ModelId = "agnes-2.5", DisplayName = "Agnes 2.5", Description = "通用文本对话模型（Agnes AI）", SupportsVision = true },
        new() { Provider = ApiProvider.Agnes, Channel = ApiChannel.Text, ModelId = "agnes-2.5-pro", DisplayName = "Agnes 2.5 Pro", Description = "旗舰文本对话模型（Agnes AI）", SupportsVision = true },
        new() { Provider = ApiProvider.Agnes, Channel = ApiChannel.Text, ModelId = "gpt-4o-mini", DisplayName = "GPT-4o mini（Agnes 通道）", Description = "通用文本/视觉模型", SupportsVision = true },

        // ===== 图片生成 =====
        new() { Provider = ApiProvider.Agnes, Channel = ApiChannel.Image, ModelId = "agnes-image-2.1-flash", DisplayName = "Agnes Image 2.1 Flash", IsAgnesImage = true, SupportsMultiImage = true, MaxReferenceImages = 6, Description = "档位式尺寸（1K~4K）+ 比例，支持最多 6 张参考图（0=文生图 / 1=图生图 / 多=多图编辑）" },
        new() { Provider = ApiProvider.Agnes, Channel = ApiChannel.Image, ModelId = "agnes-image-2.5-flash", DisplayName = "Agnes Image 2.5 Flash", IsAgnesImage = true, SupportsMultiImage = true, MaxReferenceImages = 6, Description = "最新代图像模型，全面超越 2.1；请求参数/尺寸档位/参考图与 2.1 完全一致，支持最多 6 张参考图（0=文生图 / 1=图生图 / 多=多图编辑）" },

        new() { Provider = ApiProvider.ByteDance, Channel = ApiChannel.Image, ModelId = "doubao-seedream-4-0-250828", DisplayName = "Seedream 4.0", SupportsMultiImage = true, MaxReferenceImages = 14, Description = "文生图/单多图生图/组图，多图参考 2-14 张" },
        new() { Provider = ApiProvider.ByteDance, Channel = ApiChannel.Image, ModelId = "doubao-seedream-4-5-251128", DisplayName = "Seedream 4.5", SupportsMultiImage = true, MaxReferenceImages = 14, Description = "文生图/单多图生图/组图，多图参考 2-14 张" },
        new() { Provider = ApiProvider.ByteDance, Channel = ApiChannel.Image, ModelId = "doubao-seedream-5-0-260128", DisplayName = "Seedream 5.0 Lite", SupportsMultiImage = true, MaxReferenceImages = 14, Description = "文生图/单多图生图/组图，多图参考 2-14 张" },
        new() { Provider = ApiProvider.ByteDance, Channel = ApiChannel.Image, ModelId = "doubao-seedream-5-0-pro-260628", DisplayName = "Seedream 5.0 Pro", SupportsMultiImage = true, MaxReferenceImages = 10, Description = "单/多图生图（2-10 张），支持交互编辑与图层拆分" },

        new() { Provider = ApiProvider.Qwen, Channel = ApiChannel.Image, ModelId = "qwen-image-3.0", DisplayName = "千问图像 3.0", SupportsMultiImage = true, MaxReferenceImages = 3, Description = "文生图 + 图生图/编辑（1-3 张参考图）" },
        new() { Provider = ApiProvider.Qwen, Channel = ApiChannel.Image, ModelId = "qwen-image-3.0-pro", DisplayName = "千问图像 3.0 Pro", SupportsMultiImage = true, MaxReferenceImages = 3, Description = "文生图 + 图生图/编辑（1-3 张参考图）" },
        new() { Provider = ApiProvider.Qwen, Channel = ApiChannel.Image, ModelId = "qwen-image-2.0-pro", DisplayName = "千问图像 2.0 Pro", SupportsMultiImage = true, MaxReferenceImages = 3, Description = "文生图 + 图生图/编辑（1-3 张参考图）" },
        new() { Provider = ApiProvider.Qwen, Channel = ApiChannel.Image, ModelId = "qwen-image-max", DisplayName = "千问图像 Max", SupportsMultiImage = false, MaxReferenceImages = 0, Description = "纯文生图，不支持参考图" },
        new() { Provider = ApiProvider.Qwen, Channel = ApiChannel.Image, ModelId = "qwen-image-plus", DisplayName = "千问图像 Plus", SupportsMultiImage = false, MaxReferenceImages = 0, Description = "纯文生图，不支持参考图" },

        new() { Provider = ApiProvider.OpenAI, Channel = ApiChannel.Image, ModelId = "gpt-image-1", DisplayName = "GPT Image 1", SupportsMultiImage = false, MaxReferenceImages = 0, Description = "文生图（编辑需走 /images/edits）" },
        new() { Provider = ApiProvider.OpenAI, Channel = ApiChannel.Image, ModelId = "gpt-image-2", DisplayName = "GPT Image 2", SupportsMultiImage = true, MaxReferenceImages = 16, Description = "支持多图输入编辑（最多 16 张）" },
        new() { Provider = ApiProvider.OpenAI, Channel = ApiChannel.Image, ModelId = "dall-e-3", DisplayName = "DALL·E 3", SupportsMultiImage = false, MaxReferenceImages = 0, Description = "纯文生图（已停止新部署）" },

        // ===== 视频生成 =====
        new() { Provider = ApiProvider.Agnes, Channel = ApiChannel.Video, ModelId = "agnes-video-2.5", DisplayName = "Agnes Video 2.5", VideoLevels = new[] { "720P", "960P", "2K" }, Description = "文生/图生/参考/首尾帧，支持参考视频" },
        new() { Provider = ApiProvider.Agnes, Channel = ApiChannel.Video, ModelId = "agnes-video-2.5-flash", DisplayName = "Agnes Video 2.5 Flash", IsFlashVideo = true, VideoLevels = new[] { "720P" }, Description = "固定 720P，支持参考图，不支持参考视频" },

        new() { Provider = ApiProvider.ByteDance, Channel = ApiChannel.Video, ModelId = "doubao-seedance-1-0-pro-250528", DisplayName = "Seedance 1.0 Pro", VideoLevels = new[] { "720P", "1080P" }, Description = "文生视频 / 图生视频（火山方舟）" },
        new() { Provider = ApiProvider.ByteDance, Channel = ApiChannel.Video, ModelId = "doubao-seedance-1-0-lite-250528", DisplayName = "Seedance 1.0 Lite", VideoLevels = new[] { "720P" }, Description = "文生视频 / 图生视频（火山方舟）" },

        new() { Provider = ApiProvider.OpenAI, Channel = ApiChannel.Video, ModelId = "sora-2", DisplayName = "Sora 2", VideoLevels = new[] { "1080p", "720p" }, Description = "文生视频/图生视频，支持视频输入" },
        new() { Provider = ApiProvider.OpenAI, Channel = ApiChannel.Video, ModelId = "sora-2-pro", DisplayName = "Sora 2 Pro", VideoLevels = new[] { "1080p" }, Description = "旗舰视频模型" },
        new() { Provider = ApiProvider.OpenAI, Channel = ApiChannel.Video, ModelId = "sora-2-mini", DisplayName = "Sora 2 Mini", VideoLevels = new[] { "720p", "480p" }, Description = "高性价比视频模型" },

        new() { Provider = ApiProvider.Qwen, Channel = ApiChannel.Video, ModelId = "qwen-video-max", DisplayName = "千问视频 Max（Wan）", VideoLevels = new[] { "1080P", "720P" }, Description = "文生视频 / 图生视频（DashScope 异步任务）" },
        new() { Provider = ApiProvider.Qwen, Channel = ApiChannel.Video, ModelId = "wan2.2-t2v-flash", DisplayName = "Wan2.2 T2V Flash", VideoLevels = new[] { "720P" }, Description = "文生视频（DashScope 异步任务）" },
    };

    /// <summary>按服务商 + 通道返回推荐模型列表</summary>
    public static List<string> SuggestModels(ApiProvider provider, ApiChannel channel) =>
        ModelInfos
            .Where(m => m.Provider == provider && m.Channel == channel)
            .Select(m => m.ModelId)
            .Distinct()
            .ToList();

    /// <summary>查询模型能力（未命中内置库时返回保守默认：不支持视觉/多图）</summary>
    public static AiModelInfo? Find(ApiProvider provider, ApiChannel channel, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        // 精确匹配
        var exact = ModelInfos.FirstOrDefault(m =>
            m.Provider == provider && m.Channel == channel &&
            string.Equals(m.ModelId, modelId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        // 前缀匹配（日期后缀的模型 ID）
        var prefix = ModelInfos.FirstOrDefault(m =>
            m.Provider == provider && m.Channel == channel &&
            modelId.StartsWith(m.ModelId.Split('-')[0] + "-", StringComparison.OrdinalIgnoreCase));
        if (prefix != null) return prefix;
        // 关键字匹配（如 deepseek-v4 前缀）
        return ModelInfos.FirstOrDefault(m =>
            m.Provider == provider && m.Channel == channel &&
            modelId.StartsWith(m.ModelId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>生成能力描述文本（用于配置窗口提示）</summary>
    public static string CapabilityText(ApiProvider provider, ApiChannel channel, string? modelId)
    {
        var info = Find(provider, channel, modelId);
        if (info != null)
        {
            var tags = new List<string>();
            if (info.Channel == ApiChannel.Text)
                tags.Add(info.SupportsVision ? "✅ 支持视觉理解（可上传图片）" : "❌ 不支持视觉理解");
            if (info.Channel == ApiChannel.Image)
            {
                if (info.MaxReferenceImages > 1) tags.Add($"✅ 支持多图参考（最多 {info.MaxReferenceImages} 张）");
                else if (info.MaxReferenceImages == 1) tags.Add("✅ 支持单图参考");
                else tags.Add("❌ 不支持参考图（纯文生图）");
            }
            if (info.Channel == ApiChannel.Video)
                tags.Add(info.IsFlashVideo ? "固定 720P" : "支持多种分辨率");
            var desc = string.IsNullOrEmpty(info.Description) ? "" : info.Description;
            return string.Join("　", tags) + (desc.Length > 0 ? $"\n{desc}" : "");
        }
        return "未收录该模型的能力信息，将按通用（OpenAI 兼容）方式调用。";
    }
}
