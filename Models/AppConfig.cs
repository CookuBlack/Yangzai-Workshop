namespace YangzaiWorkshop.Models;

public class AppConfig
{
    public string Theme { get; set; } = "Light";
    public bool FollowSystemTheme { get; set; } = true;
    public string UserName { get; set; } = "创作者";
    public string UserSignature { get; set; } = "用漫剧讲述精彩故事";
    public string WorkDataPath { get; set; } = "WorkData";
    public string AvatarPath { get; set; } = string.Empty;
    public string ImageDirectoryPath { get; set; } = string.Empty;
    public string VideoDirectoryPath { get; set; } = string.Empty;
    public bool AutoSaveScript { get; set; } = true;
    public int FontSize { get; set; } = 14;
    public bool AutoPlayBanner { get; set; } = true;
    public int BannerIntervalSeconds { get; set; } = 5;
    public bool AutoBackup { get; set; } = false;
    public int BackupIntervalHours { get; set; } = 24;
    public bool AutoStart { get; set; } = false;
    // ====== 分体式 AI 接口配置（文本 / 图片 / 视频 各自独立） ======
    // 说明：以下三个通道各自独立管理「服务商 + 地址 + 密钥 + 模型」，
    // 可在独立的「AI 接口配置」窗口中编辑。旧版扁平配置字段（ApiEndpoint 等）保留用于迁移。
    /// <summary>文本接口配置（聊天/剧本/提示词生成）</summary>
    public AiApiProfile TextApi { get; set; } = new()
    {
        Provider = ApiProvider.Agnes,
        BaseUrl = "https://api.agnes-ai.cn/v1",
        ModelId = "gpt-4o-mini"
    };
    /// <summary>图片生成接口配置</summary>
    public AiApiProfile ImageApi { get; set; } = new()
    {
        Provider = ApiProvider.Agnes,
        BaseUrl = "https://api.agnes-ai.cn/v1",
        ModelId = "agnes-image-2.1-flash"
    };
    /// <summary>视频生成接口配置</summary>
    public AiApiProfile VideoApi { get; set; } = new()
    {
        Provider = ApiProvider.Agnes,
        BaseUrl = "https://api.agnes-ai.cn/v1",
        ModelId = "agnes-video-2.5-flash"
    };

    // ====== 旧版扁平 API 配置（仅用于向后兼容与一次性迁移，迁移后不再使用） ======
    /// <summary>大模型 API 地址（兼容 OpenAI 格式）</summary>
    public string ApiEndpoint { get; set; } = "https://api.agnes-ai.cn/v1";
    /// <summary>API 密钥</summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>文本模型名称（聊天/剧本/提示词）</summary>
    public string ApiModel { get; set; } = "gpt-4o-mini";
    /// <summary>图片生成模型名称</summary>
    public string ImageModel { get; set; } = "agnes-image-2.1-flash";
    /// <summary>视频生成模型名称</summary>
    public string VideoModel { get; set; } = "agnes-video-2.5-flash";
    /// <summary>是否已完成旧版扁平 API 配置 → 分体式 TextApi/ImageApi/VideoApi 的一次性迁移。</summary>
    public bool ApiProfileMigrated { get; set; }
    /// <summary>默认图片生成引擎：Api=云端在线 API，ComfyUI=本地 ComfyUI</summary>
    public string DefaultImageProvider { get; set; } = "Api";

    // ====== ComfyUI 本地生图 ======
    /// <summary>ComfyUI 服务地址（例如 http://127.0.0.1:8188）</summary>
    public string ComfyUiEndpoint { get; set; } = "http://127.0.0.1:8188";
    /// <summary>当前选用的 ComfyUI 工作流 JSON 文件路径（API 格式）</summary>
    public string ComfyUiWorkflowFile { get; set; } = "";
    /// <summary>ComfyUI 工作流库（默认内置目录 + 用户导入），可在「AI 接口配置」中管理</summary>
    public List<ComfyWorkflowEntry> ComfyWorkflows { get; set; } = new();
    /// <summary>生成剧本的 System Prompt（基于当前章节原文生成剧本）</summary>
    public string ScriptSkill { get; set; } = "你是一位专业的漫剧编剧。请将以下小说章节内容改编为漫剧剧本。\n要求：\n1. 采用分镜脚本格式，每个场景标注【场景X：地点 - 时间】\n2. 对话前标注角色名，例如「角色名：台词」\n3. 动作描述用括号括起，例如（推门走进房间）\n4. 保留原著的精彩对白和情节，适当精简描述性文字\n5. 输出完整的剧本，不要省略";
    /// <summary>生成提示词的 System Prompt（基于剧本内容生成场景提示词）</summary>
    public string PromptSkill { get; set; } = "你是一位专业的漫剧分镜提示词工程师。请根据以下剧本内容，为每个场景生成对应的创作提示词。\n要求：\n1. 为每个场景单独生成提示词，标注对应场景编号\n2. 每个提示词应包含：画面构图、角色位置与动作、表情神态、光影氛围、色彩倾向\n3. 提示词应具体详细，适合直接用于AI绘图\n4. 格式：【场景X提示词】\n画面构图：...\n角色动作：...\n光影氛围：...\n色彩倾向：...";
    /// <summary>常用 AI 网站书签</summary>
    public List<AiBookmark> AiBookmarks { get; set; } = new()
    {
        new() { Name = "ChatGPT", Url = "https://chat.openai.com" },
        new() { Name = "Claude", Url = "https://claude.ai" },
        new() { Name = "Gemini", Url = "https://gemini.google.com" },
        new() { Name = "Midjourney", Url = "https://www.midjourney.com" },
        new() { Name = "Stable Diffusion", Url = "https://stability.ai" },
        new() { Name = "Hugging Face", Url = "https://huggingface.co" },
    };
    public string LastUpdateDate { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    public string Version { get; set; } = "4.0.0";
    public int GitHubStars { get; set; } = 128;

    // ====== 自定义主题：单一背景色或背景图 ======
    /// <summary>自定义背景色（HEX，例如 #EDEDED）</summary>
    public string CustomBgColor { get; set; } = "#EDEDED";
    public string CustomBgImagePath { get; set; } = string.Empty;
    public double CustomBgOpacity { get; set; } = 0.35;
    public double CustomBgBlur { get; set; } = 15;
    /// <summary>背景图模式下前景风格：Light=奶白 / Dark=暗色</summary>
    public string ImageForeground { get; set; } = "Light";

    // ====== 音乐播放器 ======
    /// <summary>音乐音量（0.0 ~ 1.0）</summary>
    public double MusicVolume { get; set; } = 0.7;
    /// <summary>启动时自动播放音乐</summary>
    public bool MusicAutoPlay { get; set; } = false;
    /// <summary>播放模式：RepeatAll / Shuffle</summary>
    public string MusicPlayMode { get; set; } = "RepeatAll";

    // ====== 文本历史（撤销/重做 + 历史版本） ======
    /// <summary>文本历史记录的最大变动次数（默认 50，范围 1~500）</summary>
    public int TextHistoryMaxCount { get; set; } = 50;

    // ====== AI 生成默认提示词 ======
    /// <summary>图片生成默认提示词库（可在生成窗口左侧边栏管理编辑）</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(DefaultPromptListConverter))]
    public List<DefaultPromptItem> DefaultImagePrompts { get; set; } = new();
    /// <summary>视频生成默认提示词库（可在生成窗口左侧边栏管理编辑）</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(DefaultPromptListConverter))]
    public List<DefaultPromptItem> DefaultVideoPrompts { get; set; } = new();

    // ====== AI 提示词优化 Skill（用户可在设置中自定义） ======
    /// <summary>图片生成提示词优化的 System Prompt 模板。
    /// 可用占位符：{prompt} 原提示词、{hasRef} 参考图情况描述、{refCount} 参考图数量、{roleName} 角色名、{personality} 角色性格。</summary>
    public string ImageOptimizeSkill { get; set; } = DefaultImageOptimizeSkill;
    /// <summary>视频生成提示词优化的 System Prompt 模板（占位符同上）</summary>
    public string VideoOptimizeSkill { get; set; } = DefaultVideoOptimizeSkill;
    /// <summary>提示词优化输出语言："zh"=中文（默认），"en"=英文。</summary>
    public string OptimizePromptLanguage { get; set; } = "zh";
    /// <summary>生成窗口提示词「实时自动匹配」开关（true=输入时按图名实时高亮；false=暂停，点一键匹配再关联）。</summary>
    public bool AutoMatchEnabled { get; set; } = true;

    // ====== 视频生成失败自动重试 ======
    /// <summary>视频生成失败后是否自动重试（默认开启）</summary>
    public bool VideoRetryEnabled { get; set; } = true;
    /// <summary>视频生成总尝试次数（含首次，默认 3 次）</summary>
    public int VideoRetryMaxAttempts { get; set; } = 3;
    /// <summary>视频失败后重试间隔（秒，默认 60）</summary>
    public int VideoRetryIntervalSeconds { get; set; } = 60;

    /// <summary>图片提示词优化 Skill 默认模板</summary>
    public const string DefaultImageOptimizeSkill =
        "你是一位专业的 AI 图像生成提示词优化师。{hasRef}。请将用户提供的简短提示词扩展为一段详细、专业的图像生成提示词。\n"
        + "要求：\n"
        + "1. 详细描述主体外观、表情、姿态、服饰\n"
        + "2. 描述场景背景、构图、景深\n"
        + "3. 丰富光影、色彩、氛围\n"
        + "4. 指定摄影/绘画风格（如电影剧照、肖像摄影、动漫风）\n"
        + "5. 使用流畅的英文或中英混合（英文术语更准确）\n"
        + "6. 保持原意的同时让画面更具视觉冲击力\n"
        + "7. 只输出优化后的提示词，不要任何解释。";

    /// <summary>视频提示词优化 Skill 默认模板</summary>
    public const string DefaultVideoOptimizeSkill =
        "你是一位专业的 AI 视频生成提示词优化师。{hasRef}。请将用户提供的简短提示词扩展为一段详细、专业的视频生成提示词。\n"
        + "要求：\n"
        + "1. 添加镜头描述（如特写、全景、跟踪镜头）\n"
        + "2. 描述光影和色彩氛围\n"
        + "3. 丰富动作和场景细节\n"
        + "4. 使用流畅的英文或中英混合（英文术语更准确）\n"
        + "5. 保持原意的同时让画面更具电影质感\n"
        + "6. 只输出优化后的提示词，不要任何解释。";

    // ====== AI 生成窗口三栏布局（可拖拽调整并持久化） ======
    /// <summary>左侧栏（提示词素材/默认提示词）宽度</summary>
    public double GenLeftPanelWidth { get; set; } = 230;
    /// <summary>右侧栏（项目资产）宽度</summary>
    public double GenRightPanelWidth { get; set; } = 280;
    /// <summary>中央栏宽度（与右侧栏按比例分配剩余空间）</summary>
    public double GenCenterPanelWidth { get; set; } = 460;
}

/// <summary>
/// 一条默认提示词：Text 为提示词内容，Enabled 表示是否在每次生成时自动追加到提示词末尾。
/// </summary>
public sealed class DefaultPromptItem
{
    public string Text { get; set; } = "";
    public bool Enabled { get; set; }
}

/// <summary>
/// 默认提示词列表转换器：兼容旧配置（纯字符串数组，自动转为未启用条目）与新配置（对象数组，含启用标记）。
/// </summary>
public sealed class DefaultPromptListConverter : System.Text.Json.Serialization.JsonConverter<List<DefaultPromptItem>>
{
    public override List<DefaultPromptItem> Read(
        ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options)
    {
        var list = new List<DefaultPromptItem>();
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartArray) { reader.Skip(); return list; }
        while (reader.Read())
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.EndArray) break;
            if (reader.TokenType == System.Text.Json.JsonTokenType.String)
                list.Add(new DefaultPromptItem { Text = reader.GetString() ?? "" });
            else if (reader.TokenType == System.Text.Json.JsonTokenType.StartObject)
            {
                var item = System.Text.Json.JsonSerializer.Deserialize<DefaultPromptItem>(ref reader, options);
                if (item != null) list.Add(item);
            }
            else reader.Skip();
        }
        return list;
    }

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer,
        List<DefaultPromptItem> value,
        System.Text.Json.JsonSerializerOptions options)
        => System.Text.Json.JsonSerializer.Serialize(writer, value, options);
}
