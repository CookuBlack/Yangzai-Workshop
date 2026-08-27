using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace YangzaiWorkshop.Services;

public static class ApiService
{
    private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>调用大模型 API 生成内容（非流式）</summary>
    public static async Task<string?> ChatAsync(
        string endpoint, string apiKey, string model,
        string systemPrompt, string userMessage)
    {
        var url = endpoint.TrimEnd('/') + "/chat/completions";
        var body = new
        {
            model,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            temperature = 0.7,
            max_tokens = 4096
        };

        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var res = await _client.SendAsync(req);
        var respText = await res.Content.ReadAsStringAsync();

        HandleNonSuccess(res, respText, url, model);

        using var doc = JsonDocument.Parse(respText);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            throw new ApiException("API 未返回有效内容");

        return choices[0].GetProperty("message")
            .GetProperty("content").GetString();
    }

    /// <summary>
    /// 调用多模态大模型 API（OpenAI 兼容格式），支持传入图片作为视觉输入。
    /// imageDataUrls 可选：Data URI Base64 列表（data:image/png;base64,...），
    /// 为空时退化为纯文本对话。
    /// </summary>
    public static async Task<string?> ChatWithImagesAsync(
        string endpoint, string apiKey, string model,
        string systemPrompt, string userMessage,
        IReadOnlyList<string>? imageDataUrls = null)
    {
        var url = endpoint.TrimEnd('/') + "/chat/completions";

        // 用户消息：有图时用 content 数组（text + image_url），无图时用纯文本
        object userContent;
        if (imageDataUrls is { Count: > 0 })
        {
            var parts = new List<object>();
            if (!string.IsNullOrWhiteSpace(userMessage))
                parts.Add(new { type = "text", text = userMessage });
            foreach (var dataUrl in imageDataUrls)
                parts.Add(new { type = "image_url", image_url = new { url = dataUrl } });
            userContent = parts;
        }
        else
        {
            userContent = userMessage;
        }

        var body = new
        {
            model,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            },
            temperature = 0.7,
            max_tokens = 4096
        };

        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var res = await _client.SendAsync(req);
        var respText = await res.Content.ReadAsStringAsync();

        HandleNonSuccess(res, respText, url, model);

        using var doc = JsonDocument.Parse(respText);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            throw new ApiException("API 未返回有效内容");

        return choices[0].GetProperty("message")
            .GetProperty("content").GetString();
    }

    /// <summary>流式调用大模型 API，逐 token 回调 onToken</summary>
    public static async Task<string> ChatStreamAsync(
        string endpoint, string apiKey, string model,
        string systemPrompt, string userMessage,
        Action<string> onToken,
        CancellationToken cancel = default)
    {
        var url = endpoint.TrimEnd('/') + "/chat/completions";
        var body = new
        {
            model,
            stream = true,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            temperature = 0.7,
            max_tokens = 4096
        };

        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var res = await _client.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead, cancel);

        // 错误响应时，读完整个 body 再报错
        if (!res.IsSuccessStatusCode)
        {
            var errBody = await res.Content.ReadAsStringAsync();
            HandleNonSuccess(res, errBody, url, model);
        }

        using var stream = await res.Content.ReadAsStreamAsync(cancel);
        var sb = new StringBuilder();
        var lineBuf = new StringBuilder();
        var byteBuf = new byte[1024];
        var decoder = Encoding.UTF8.GetDecoder();
        var charBuf = new char[1024];

        void FlushLine()
        {
            var line = lineBuf.ToString();
            lineBuf.Clear();

            if (line.Length == 0) return;
            if (!line.StartsWith("data: ")) return;

            var data = line.Substring(6);
            if (data == "[DONE]") return;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) return;
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var content))
                {
                    var token = content.GetString();
                    if (!string.IsNullOrEmpty(token))
                    {
                        sb.Append(token);
                        onToken(token);
                    }
                }
            }
            catch (JsonException) { /* 忽略解析失败的行 */ }
        }

        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(byteBuf, 0, byteBuf.Length, cancel);
            if (read == 0) break;

            // UTF-8 解码为字符
            var charsUsed = decoder.GetChars(byteBuf, 0, read, charBuf, 0);

            for (int i = 0; i < charsUsed; i++)
            {
                var c = charBuf[i];
                if (c == '\n')
                {
                    FlushLine();
                }
                else if (c != '\r')
                {
                    lineBuf.Append(c);
                }
            }
        }
        // 处理最后一行（可能没有换行符）
        if (lineBuf.Length > 0) FlushLine();

        var result = sb.ToString();
        if (string.IsNullOrWhiteSpace(result))
            throw new ApiException("API 未返回有效内容");

        return result;
    }

    private static void HandleNonSuccess(
        HttpResponseMessage res, string? respText,
        string url, string model)
    {
        if (res.IsSuccessStatusCode) return;

        respText ??= SafeReadContent(res);
        var statusInfo = (int)res.StatusCode switch
        {
            503 => $"503 服务不可用\n可能原因：地址错误、模型名「{model}」不存在或服务商临时故障\n请求地址：{url}",
            401 or 403 => $"{(int)res.StatusCode} 认证失败，请检查 API 密钥是否正确",
            404 => $"404 接口不存在，请检查 API 地址是否正确（应以 /v1 结尾）",
            429 => "429 触发接口限流（瞬态，应用会自动退避重试；若持续出现，请确认没有同时运行多个应用实例或过快地连续生成）",
            _ => $"{(int)res.StatusCode} {res.ReasonPhrase}"
        };
        try
        {
            using var errDoc = JsonDocument.Parse(respText);
            var errMsg = errDoc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg)
                ? msg.GetString() : respText;
            if (!string.IsNullOrEmpty(errMsg) && errMsg != respText)
                throw new ApiException($"API 错误：{statusInfo}\n{errMsg}");
        }
        catch (ApiException) { throw; }
        catch { throw new ApiException($"API 错误 ({(int)res.StatusCode})：{respText}"); }
    }

    /// <summary>
    /// 同步读取 HttpResponseMessage 内容。避免在 UI 线程上调用 .Result 造成死锁。
    /// 在 ASP.NET/WPF 等有 SynchronizationContext 的环境下，使用 Task.Run + GetAwaiter().GetResult()
    /// 而非直接 .Result，并捕获 AggregateException 中可能的底层异常。
    /// </summary>
    private static string SafeReadContent(HttpResponseMessage res)
    {
        try
        {
            return Task.Run(() => res.Content.ReadAsStringAsync()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // 解包 AggregateException，记录真实异常以方便排查
            var inner = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
            return $"[读取响应内容失败] {inner.GetType().Name}: {inner.Message}";
        }
    }

    /// <summary>
    /// 调用图片生成 API，返回图片 URL。
    /// referenceImages 可选：传入一张或多张参考图（Data URI Base64，形如 data:image/png;base64,...）。
    /// 1 张 → 图生图；多张 → 多图编辑/合成（由 prompt 描述组合方式）。
    /// </summary>
    public static async Task<string> GenerateImageAsync(
        string endpoint, string apiKey,
        string prompt, string model, string size = "1024x768",
        IReadOnlyList<string>? referenceImages = null,
        CancellationToken cancel = default)
    {
        var url = endpoint.TrimEnd('/') + "/images/generations";
        var extra = new Dictionary<string, object> { ["response_format"] = "url" };
        if (referenceImages is { Count: > 0 })
            extra["image"] = referenceImages;
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["size"] = size,
            ["extra_body"] = extra
        };

        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var res = await _client.SendAsync(req, cancel);
        var respText = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            HandleNonSuccess(res, respText, url, model);

        using var doc = JsonDocument.Parse(respText);
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0)
            throw new ApiException("图片 API 未返回有效结果");

        var first = data[0];
        if (first.TryGetProperty("url", out var imgUrl) && imgUrl.ValueKind == JsonValueKind.String)
            return imgUrl.GetString()!;

        throw new ApiException("图片 API 未返回 URL");
    }

    /// <summary>下载图片到字节数组</summary>
    public static async Task<byte[]> DownloadImageAsync(string imageUrl, CancellationToken cancel = default)
    {
        using var res = await _client.GetAsync(imageUrl, cancel);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync();
    }

    /// <summary>创建视频生成任务（agnes-video-2.5 / agnes-video-2.5-flash），返回 video_id。
    /// mode：text=文生视频，keyframe=首尾帧控制，reference=参考生成。
    /// 参考媒体传入 base64 Data URL（与图生图一致）；videos 仅 agnes-video-2.5（非 Flash）支持。</summary>
    public static async Task<string> CreateVideoTaskAsync(
        string endpoint, string apiKey, string model,
        string prompt, string mode = "text",
        int seconds = 5, string size = "720P", string aspectRatio = "16:9",
        string? firstFrame = null, string? lastFrame = null,
        IReadOnlyList<string>? referenceImages = null,
        IReadOnlyList<string>? referenceAudios = null,
        IReadOnlyList<VideoReference>? referenceVideos = null,
        CancellationToken cancel = default)
    {
        var url = endpoint.TrimEnd('/') + "/videos";
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["mode"] = mode,
            ["seconds"] = seconds.ToString(),
            ["size"] = size,
            ["aspect_ratio"] = aspectRatio
        };

        if (mode == "keyframe")
        {
            if (!string.IsNullOrWhiteSpace(firstFrame)) body["first_frame"] = firstFrame;
            if (!string.IsNullOrWhiteSpace(lastFrame)) body["last_frame"] = lastFrame;
        }
        else if (mode == "reference")
        {
            if (referenceImages is { Count: > 0 }) body["images"] = referenceImages;
            if (referenceAudios is { Count: > 0 }) body["audios"] = referenceAudios;
            if (referenceVideos is { Count: > 0 })
                body["videos"] = referenceVideos
                    .Select(v => new Dictionary<string, object>
                    {
                        ["url"] = v.Url,
                        ["start_seconds"] = v.StartSeconds ?? 0,
                        ["require_audio"] = v.RequireAudio ?? false
                    })
                    .ToList();
        }

        var json = JsonSerializer.Serialize(body);

        // 创建任务偶发 429（限流为瞬态，免费档位阈值较低）：退避重试而不是直接失败
        const int maxCreateRetries = 4;
        for (int attempt = 0; ; attempt++)
        {
            cancel.ThrowIfCancellationRequested();

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Authorization", $"Bearer {apiKey}");

            using var res = await _client.SendAsync(req, cancel);
            var respText = await res.Content.ReadAsStringAsync();

            if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < maxCreateRetries)
            {
                // 按 3s×n 递增退避（上限 15s），避免被持续限流判定失败
                var wait = TimeSpan.FromSeconds(3 * (attempt + 1));
                if (wait > TimeSpan.FromSeconds(15)) wait = TimeSpan.FromSeconds(15);
                await Task.Delay(wait, cancel);
                continue;
            }

            if (!res.IsSuccessStatusCode)
                HandleNonSuccess(res, respText, url, model);

            using var doc = JsonDocument.Parse(respText);
            var root = doc.RootElement;
            if (root.TryGetProperty("video_id", out var vid))
                return vid.GetString()!;
            if (root.TryGetProperty("task_id", out var tid))
                return tid.GetString()!;

            throw new ApiException("视频 API 未返回 video_id");
        }
    }

    /// <summary>轮询视频结果直到完成或失败，返回视频 URL。
    /// 按 agnes-video-2.5 / 2.5-flash 文档使用 video_id + model_name 查询（keyframe/reference 模式必须带 model_name）。</summary>
    public static async Task<string> PollVideoResultAsync(
        string endpoint, string apiKey,
        string videoId, string model,
        IProgress<string>? progress = null,
        CancellationToken cancel = default)
    {
        var baseUrl = endpoint.TrimEnd('/');
        // 去掉 /v1 后缀得到根地址
        if (baseUrl.EndsWith("/v1"))
            baseUrl = baseUrl.Substring(0, baseUrl.Length - 3);
        baseUrl = baseUrl.TrimEnd('/');

        var queryUrl = $"{baseUrl}/agnesapi?video_id={Uri.EscapeDataString(videoId)}&model_name={Uri.EscapeDataString(model)}";

        // 连续限流计数：429 为瞬态，退避重试而不中断任务；连续多次仍失败再放弃
        int rateLimitStreak = 0;

        while (true)
        {
            cancel.ThrowIfCancellationRequested();

            using var req = new HttpRequestMessage(HttpMethod.Get, queryUrl);
            req.Headers.Add("Authorization", $"Bearer {apiKey}");

            using var res = await _client.SendAsync(req, cancel);
            var respText = await res.Content.ReadAsStringAsync();

            if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // 按 3s×n 递增退避（上限 30s），让队列窗口感知到重试中
                rateLimitStreak++;
                if (rateLimitStreak >= 10)
                    HandleNonSuccess(res, respText, queryUrl, "video");
                var wait = TimeSpan.FromSeconds(3 * rateLimitStreak);
                if (wait > TimeSpan.FromSeconds(30)) wait = TimeSpan.FromSeconds(30);
                progress?.Report($"触发限流，{wait.TotalSeconds:0} 秒后重试…");
                await Task.Delay(wait, cancel);
                continue;
            }

            if (!res.IsSuccessStatusCode)
                HandleNonSuccess(res, respText, queryUrl, "video");

            rateLimitStreak = 0;

            using var doc = JsonDocument.Parse(respText);
            var root = doc.RootElement;
            var status = root.GetProperty("status").GetString();

            if (status == "completed")
            {
                // 视频 URL 位于 metadata.url（兼容顶层 url 兜底）
                string? videoUrl = null;
                if (root.TryGetProperty("metadata", out var md) &&
                    md.TryGetProperty("url", out var mu) && mu.ValueKind == JsonValueKind.String)
                    videoUrl = mu.GetString();
                if (videoUrl == null && root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    videoUrl = u.GetString();
                if (string.IsNullOrWhiteSpace(videoUrl))
                    throw new ApiException("视频已完成但响应中未找到视频 URL");
                return videoUrl;
            }

            if (status == "failed")
            {
                var errMsg = "未知错误";
                if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                {
                    // 兼容 error 为对象 { message: ... } 或字符串
                    if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var em))
                        errMsg = em.GetString() ?? err.ToString();
                    else
                        errMsg = err.ToString();
                }
                throw new ApiException($"视频生成失败：{errMsg}");
            }

            // 仍在排队或处理中
            var prog = 0;
            if (root.TryGetProperty("progress", out var p))
                prog = p.GetInt32();
            progress?.Report($"{status} ({prog}%)");

            // 文档建议 1–2 秒轮询一次
            await Task.Delay(2000, cancel);
        }
    }

    /// <summary>下载视频到字节数组</summary>
    public static async Task<byte[]> DownloadVideoAsync(string videoUrl, CancellationToken cancel = default)
    {
        using var res = await _client.GetAsync(videoUrl, cancel);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync();
    }

    /// <summary>获取可用模型列表</summary>
    public static async Task<List<string>> FetchModelsAsync(string endpoint, string apiKey)
    {
        var url = endpoint.TrimEnd('/') + "/models";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var res = await _client.SendAsync(req);
        var respText = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            throw new ApiException($"获取模型失败 ({(int)res.StatusCode})：{respText}");

        using var doc = JsonDocument.Parse(respText);
        var data = doc.RootElement.GetProperty("data");
        var models = new List<string>();
        foreach (var m in data.EnumerateArray())
        {
            var id = m.GetProperty("id").GetString();
            if (!string.IsNullOrEmpty(id)) models.Add(id);
        }
        models.Sort();
        return models;
    }

    // ==================== ComfyUI 本地生图 ====================

    /// <summary>
    /// 读取 ComfyUI 工作流 JSON 文件（API 格式），注入用户提示词、图像尺寸与参考图后提交。
    /// 返回 prompt_id。
    /// </summary>
    /// <param name="workflowFile">工作流 JSON 文件路径（ComfyUI 网页「Export (API)」导出）</param>
    /// <param name="prompt">正向提示词，注入到正向 CLIPTextEncode 节点</param>
    /// <param name="width">目标宽度，注入到 EmptyLatentImage 节点</param>
    /// <param name="height">目标高度，注入到 EmptyLatentImage 节点</param>
    /// <param name="referenceImageBase64">参考图 base64（不含 data: 前缀），注入到 LoadImage 节点；为空则保持原样</param>
    public static async Task<string> SubmitComfyWorkflowFileAsync(
        string endpoint, string workflowFile, string prompt,
        int width, int height,
        string? referenceImageBase64 = null,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(workflowFile) || !File.Exists(workflowFile))
            throw new ApiException("ComfyUI 工作流文件不存在，请先在设置中指定有效的 workflow JSON 文件");

        // 读取工作流 JSON
        string jsonText;
        try { jsonText = File.ReadAllText(workflowFile); }
        catch (Exception ex) { throw new ApiException($"读取工作流文件失败：{ex.Message}"); }

        // 解析工作流 JSON（使用 JsonNode，保留节点结构且可正确识别嵌套对象类型）
        JsonNode? workflowNode;
        try { workflowNode = JsonNode.Parse(jsonText); }
        catch (Exception ex) { throw new ApiException($"工作流 JSON 解析失败：{ex.Message}"); }

        if (workflowNode is not JsonObject workflowObj)
            throw new ApiException("工作流 JSON 格式错误（顶层必须是对象）");

        // ComfyUI 工作流有两种格式：
        //   - UI 格式：顶层含 "nodes" 数组（网页「Export」导出）
        //   - API 格式：顶层直接是节点 ID 字典（网页「Export (API)」导出）
        // 只有 API 格式能直接提交到 /prompt，因此 UI 格式需提示用户改用「Export (API)」导出。
        if (workflowObj.ContainsKey("nodes"))
        {
            throw new ApiException(
                "工作流是 UI 格式，无法直接提交。请在 ComfyUI 网页中右键选择「Export (API)」重新导出为 API 格式的 JSON 文件。");
        }

        // 注入提示词、图像尺寸与参考图
        InjectWorkflowInputs(workflowObj, prompt, width, height, referenceImageBase64);

        var url = endpoint.TrimEnd('/') + "/prompt";
        var bodyObj = new JsonObject
        {
            ["prompt"] = workflowObj,
            ["client_id"] = "yangzai-workshop"
        };
        var payload = bodyObj.ToJsonString();

        // 调试日志：记录实际发送给 ComfyUI 的 payload，便于排查注入是否生效
        try
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            File.WriteAllText(Path.Combine(logDir, $"comfy_payload_{DateTime.Now:yyyyMMdd_HHmmss}.json"), payload);
        }
        catch { /* 日志失败不影响主流程 */ }

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var res = await _client.SendAsync(req, cancel);
        var respText = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            throw new ApiException($"ComfyUI 提交失败 ({(int)res.StatusCode})：{respText}");

        using var respDoc = JsonDocument.Parse(respText);
        if (respDoc.RootElement.TryGetProperty("prompt_id", out var pid))
            return pid.GetString()!;

        throw new ApiException("ComfyUI 未返回 prompt_id");
    }

    /// <summary>
    /// 向工作流注入用户输入：正向提示词、图像尺寸、参考图。
    /// 使用 JsonObject 直接修改节点，避免反序列化为 object 时嵌套对象变成 JsonElement 导致无法识别。
    /// </summary>
    private static void InjectWorkflowInputs(
        JsonObject workflow, string prompt,
        int width, int height, string? referenceImageBase64)
    {
        // 1. 收集所有 CLIPTextEncode 节点，识别正向/负向
        var clipNodes = new List<(string Id, JsonObject Inputs, string Text)>();
        foreach (var kv in workflow)
        {
            if (kv.Value is not JsonObject node) continue;
            if (node["class_type"]?.GetValue<string>() != "CLIPTextEncode") continue;
            if (node["inputs"] is not JsonObject inputs) continue;

            string text = inputs["text"]?.GetValue<string>() ?? "";
            clipNodes.Add((kv.Key, inputs, text));
        }

        // 识别正向提示词节点（跳过明显是负向提示词的节点）
        string? positiveNodeId = null;
        foreach (var (id, _, text) in clipNodes)
        {
            if (IsNegativePrompt(text)) continue;
            positiveNodeId = id;
            break;
        }
        if (positiveNodeId == null && clipNodes.Count > 0)
            positiveNodeId = clipNodes[0].Id; // 兜底：全部像负向时退回第一个

        // 注入正向提示词
        if (positiveNodeId != null && workflow[positiveNodeId] is JsonObject posNode && posNode["inputs"] is JsonObject posInputs)
        {
            posInputs["text"] = prompt;
        }

        // 2. 注入图像尺寸：覆盖所有 EmptyLatentImage（无论宽高之前是数字还是连线引用）
        if (width > 0 && height > 0)
        {
            foreach (var kv in workflow)
            {
                if (kv.Value is not JsonObject node) continue;
                if (node["class_type"]?.GetValue<string>() != "EmptyLatentImage") continue;
                if (node["inputs"] is not JsonObject inputs) continue;

                // 直接覆盖 width/height：无论是数字还是数组（连线引用），都会被替换为用户选择的尺寸
                inputs["width"] = width;
                inputs["height"] = height;
            }

            // 同时调整 ResolutionSelector 的 megapixels（如果工作流用其决定尺寸）
            // 这样如果其他地方引用 ResolutionSelector 输出，也会得到一致的尺寸
            double megapixels = Math.Round(width * height / 1_000_000.0, 2);
            foreach (var kv in workflow)
            {
                if (kv.Value is not JsonObject node) continue;
                if (node["class_type"]?.GetValue<string>() != "ResolutionSelector") continue;
                if (node["inputs"] is not JsonObject inputs) continue;
                if (megapixels > 0) inputs["megapixels"] = megapixels;
            }
        }

        // 3. 注入参考图（LoadImage 的 image）
        if (referenceImageBase64 != null)
        {
            foreach (var kv in workflow)
            {
                if (kv.Value is not JsonObject node) continue;
                if (node["class_type"]?.GetValue<string>() != "LoadImage") continue;
                if (node["inputs"] is not JsonObject inputs) continue;

                inputs["image"] = referenceImageBase64;
            }
        }
    }

    /// <summary>判断文本是否为负向提示词（含常见负面词）</summary>
    private static bool IsNegativePrompt(string text)
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

    /// <summary>轮询 ComfyUI 历史记录，返回生成图片的文件名（filename）；失败抛异常</summary>
    public static async Task<string> PollComfyResultAsync(
        string endpoint, string promptId,
        IProgress<string>? progress = null,
        CancellationToken cancel = default)
    {
        var url = endpoint.TrimEnd('/') + $"/history/{promptId}";

        while (true)
        {
            cancel.ThrowIfCancellationRequested();

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await _client.SendAsync(req, cancel);
            var respText = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                throw new ApiException($"ComfyUI 查询失败 ({(int)res.StatusCode})：{respText}");

            using var doc = JsonDocument.Parse(respText);
            var root = doc.RootElement;

            // 历史为空：任务可能尚未完成或已从历史中清除
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(promptId, out var entry))
            {
                progress?.Report("排队中…");
                await Task.Delay(1000, cancel);
                continue;
            }

            // 检查状态
            if (entry.TryGetProperty("status", out var status) && status.TryGetProperty("status_str", out var statusStr))
            {
                var s = statusStr.GetString();
                if (s == "error")
                {
                    var errMsg = "未知错误";
                    if (status.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0)
                        errMsg = msgs[0].ToString();
                    throw new ApiException($"ComfyUI 生成失败：{errMsg}");
                }
            }

            // 查找输出图片文件名
            if (entry.TryGetProperty("outputs", out var outputs))
            {
                foreach (var outProp in outputs.EnumerateObject())
                {
                    if (outProp.Value.TryGetProperty("images", out var images) && images.GetArrayLength() > 0)
                    {
                        var img = images[0];
                        if (img.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String)
                            return fn.GetString()!;
                    }
                }
            }

            progress?.Report("生成中…");
            await Task.Delay(1000, cancel);
        }
    }

    /// <summary>从 ComfyUI 下载图片字节。view 端点支持 filename/subfolder/type 参数。</summary>
    public static async Task<byte[]> DownloadComfyImageAsync(
        string endpoint, string filename,
        string subfolder = "", string type = "output",
        CancellationToken cancel = default)
    {
        var url = endpoint.TrimEnd('/') + $"/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
        using var res = await _client.GetAsync(url, cancel);
        if (!res.IsSuccessStatusCode)
            throw new ApiException($"ComfyUI 下载图片失败 ({(int)res.StatusCode})");
        return await res.Content.ReadAsByteArrayAsync(cancel);
    }
}

public class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}

/// <summary>参考视频对象（agnes-video-2.5 reference 模式 videos[].url）。
/// 仅 agnes-video-2.5（非 Flash）支持；Flash 传入有效 videos 会返回 HTTP 400。</summary>
public sealed class VideoReference
{
    /// <summary>可访问的视频地址（本应用使用 base64 Data URL）。</summary>
    public string Url { get; init; } = "";
    /// <summary>从参考视频的指定秒数开始读取，默认 0。</summary>
    public double? StartSeconds { get; init; }
    /// <summary>是否要求参考视频必须包含音轨，默认 false。</summary>
    public bool? RequireAudio { get; init; }
}
