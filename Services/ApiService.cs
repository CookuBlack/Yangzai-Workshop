using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using YangzaiWorkshop.Models;

namespace YangzaiWorkshop.Services;

public static class ApiService
{
    // 统一 Http 客户端：显式跟随系统代理，保证 API 域名若走代理可访问时应用能正常连通
    private static readonly HttpClient _client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = WebRequest.GetSystemWebProxy()
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
    }

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
    /// 调用图片生成 API，返回图片 URL（个别服务商返回 Data URI，下载器会自动处理）。
    /// 按服务商适配请求格式（依据各官方文档）：
    ///   Agnes：档位式 size + ratio，最多 6 张参考图；
    ///   字节 Seedream：image 数组（单图=图生图 / 多图=多图融合），1K/2K/4K 或精确尺寸；
    ///   千问 qwen-image-3.x / 2.x：DashScope 同步 multimodal-generation（图生图/编辑）；旧版走异步任务；
    ///   OpenAI：gpt-image 文生图 / edits（多图编辑，base64 返回）；
    ///   自定义 / ModelScope / DeepSeek：OpenAI 兼容 images/generations。
    /// referenceImages：Data URI Base64 列表，1 张=图生图，多张=多图编辑/合成。
    /// </summary>
    public static async Task<string> GenerateImageAsync(
        string endpoint, string apiKey,
        string prompt, string model,
        ApiProvider provider = ApiProvider.Agnes,
        string size = "1024x768",
        IReadOnlyList<string>? referenceImages = null,
        string? ratio = null,
        CancellationToken cancel = default)
    {
        return provider switch
        {
            ApiProvider.ByteDance => await GenerateByteDanceImageAsync(endpoint, apiKey, prompt, model, size, referenceImages, cancel),
            ApiProvider.Qwen => await GenerateQwenImageAsync(endpoint, apiKey, prompt, model, size, referenceImages, cancel),
            ApiProvider.OpenAI => await GenerateOpenAIImageAsync(endpoint, apiKey, prompt, model, size, referenceImages, cancel),
            ApiProvider.Agnes => await GenerateAgnesImageAsync(endpoint, apiKey, prompt, model, size, referenceImages, ratio, cancel),
            // 自定义 / ModelScope / DeepSeek：OpenAI 兼容格式
            _ => await GenerateOpenAICompatibleImageAsync(endpoint, apiKey, prompt, model, size, referenceImages, cancel)
        };
    }

    // ==================== 图片生成：各服务商适配 ====================

    /// <summary>Agnes 图片：档位式 size + ratio。</summary>
    private static async Task<string> GenerateAgnesImageAsync(
        string endpoint, string apiKey, string prompt, string model,
        string size, IReadOnlyList<string>? referenceImages, string? ratio, CancellationToken cancel)
    {
        var url = endpoint.TrimEnd('/') + "/images/generations";
        var extra = new Dictionary<string, object> { ["response_format"] = "url" };
        if (referenceImages is { Count: > 0 })
            extra["image"] = referenceImages;
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["extra_body"] = extra
        };
        // agnes-image 系列按官方推荐使用「档位式 size + ratio」，输出尺寸可预期（如 2K+16:9 → 2624x1472）。
        if (ViewHelpers.IsAgnesImageModel(model) && !string.IsNullOrWhiteSpace(ratio))
        {
            body["size"] = size;   // size 传档位，如 "2K"
            body["ratio"] = ratio; // 如 "16:9"
        }
        else
        {
            body["size"] = size;
        }

        return await PostImageAndGetUrl(url, apiKey, body, model, cancel);
    }

    /// <summary>OpenAI 兼容图片（自定义 / ModelScope / DeepSeek 等）：images/generations，返回 URL。</summary>
    private static async Task<string> GenerateOpenAICompatibleImageAsync(
        string endpoint, string apiKey, string prompt, string model,
        string size, IReadOnlyList<string>? referenceImages, CancellationToken cancel)
    {
        var url = endpoint.TrimEnd('/') + "/images/generations";
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["size"] = size,
            ["response_format"] = "url"
        };
        // 部分聚合/中转服务支持以 image 数组传参考图（OpenAI 兼容 Image API 风格）
        if (referenceImages is { Count: > 0 })
            body["image"] = referenceImages.ToList();
        return await PostImageAndGetUrl(url, apiKey, body, model, cancel);
    }

    /// <summary>字节 Seedream：image 数组参考图（单图=图生图、多图=多图融合），1K/2K/4K 档位或精确尺寸。</summary>
    private static async Task<string> GenerateByteDanceImageAsync(
        string endpoint, string apiKey, string prompt, string model,
        string size, IReadOnlyList<string>? referenceImages, CancellationToken cancel)
    {
        var url = endpoint.TrimEnd('/') + "/images/generations";
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["size"] = size,          // 支持 1K/2K/4K 档位或 2560x1440 精确尺寸
            ["response_format"] = "url",
            ["watermark"] = false,
            ["sequential_image_generation"] = "disabled" // 本软件一次生成单张
        };
        if (referenceImages is { Count: > 0 })
            body["image"] = referenceImages.ToList();
        return await PostImageAndGetUrl(url, apiKey, body, model, cancel);
    }

    /// <summary>OpenAI：gpt-image 文生图（base64 返回）。有参考图时走 /images/edits（多图编辑）。</summary>
    private static async Task<string> GenerateOpenAIImageAsync(
        string endpoint, string apiKey, string prompt, string model,
        string size, IReadOnlyList<string>? referenceImages, CancellationToken cancel)
    {
        if (referenceImages is { Count: > 0 })
            return await GenerateOpenAIEditsAsync(endpoint, apiKey, prompt, model, size, referenceImages, cancel);

        var url = endpoint.TrimEnd('/') + "/images/generations";
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["size"] = size
        };
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var res = await _client.SendAsync(req, cancel);
        var respText = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) HandleNonSuccess(res, respText, url, model);

        using var doc = JsonDocument.Parse(respText);
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0) throw new ApiException("图片 API 未返回有效结果");
        var first = data[0];
        // gpt-image 系列总是返回 base64；dall-e 等兼容 URL
        if (first.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
            return "data:image/png;base64," + b64.GetString()!;
        if (first.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            return u.GetString()!;
        throw new ApiException("图片 API 未返回结果");
    }

    /// <summary>OpenAI /images/edits：多图编辑（multipart，最多 16 张）。</summary>
    private static async Task<string> GenerateOpenAIEditsAsync(
        string endpoint, string apiKey, string prompt, string model,
        string size, IReadOnlyList<string>? referenceImages, CancellationToken cancel)
    {
        var url = endpoint.TrimEnd('/') + "/images/edits";
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent(prompt), "prompt");
        if (!string.IsNullOrWhiteSpace(size) && size.Contains('x'))
            content.Add(new StringContent(size), "size");

        int i = 0;
        foreach (var dataUrl in referenceImages!)
        {
            var (mime, bytes) = DecodeDataUrl(dataUrl);
            var ext = mime switch { "image/png" => "png", "image/webp" => "webp", _ => "jpg" };
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
            content.Add(part, "image", $"ref_{i++}.{ext}");
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.Add("Authorization", $"Bearer {apiKey}");
        using var res = await _client.SendAsync(req, cancel);
        var respText = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) HandleNonSuccess(res, respText, url, model);

        using var doc = JsonDocument.Parse(respText);
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0) throw new ApiException("图片 API 未返回有效结果");
        var first = data[0];
        if (first.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
            return "data:image/png;base64," + b64.GetString()!;
        if (first.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            return u.GetString()!;
        throw new ApiException("图片 API 未返回结果");
    }

    /// <summary>千问图片：qwen-image-3.x / 2.x 走同步 multimodal-generation；旧版走异步 text2image 任务。</summary>
    private static async Task<string> GenerateQwenImageAsync(
        string endpoint, string apiKey, string prompt, string model,
        string size, IReadOnlyList<string>? referenceImages, CancellationToken cancel)
    {
        if (IsQwenMultimodalImage(model))
            return await GenerateQwenMultimodalImageAsync(endpoint, apiKey, prompt, model, size, referenceImages, cancel);
        return await GenerateQwenAsyncTaskImageAsync(endpoint, apiKey, prompt, model, size, cancel);
    }

    /// <summary>判断是否为 qwen-image 系列的多模态模型（走同步 multimodal-generation 接口）。</summary>
    private static bool IsQwenMultimodalImage(string model) =>
        model.StartsWith("qwen-image-3", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("qwen-image-2", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("qwen-image-edit", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("wanx2.1-imageedit", StringComparison.OrdinalIgnoreCase);

    /// <summary>把通用尺寸字符串转为千问 DashScope 尺寸格式（如 "1024x768" → "1024*768"，档位式 → "1024*1024"）。</summary>
    private static string ToQwenSize(string size)
    {
        if (string.IsNullOrWhiteSpace(size)) return "1024*1024";
        var s = size.Trim().ToLowerInvariant();
        if (s is "1k" or "2k" or "3k" or "4k") return "1024*1024";
        var sep = s.IndexOf('x');
        if (sep < 0) sep = s.IndexOf('*');
        if (sep > 0 && int.TryParse(s[..sep], out var w) && int.TryParse(s[(sep + 1)..], out var h))
            return $"{w}*{h}";
        return s.Replace('x', '*');
    }

    /// <summary>qwen-image-3.x / 2.x：同步 multimodal-generation（文生图 + 图生图/编辑，支持 1-3 张参考图）。</summary>
    private static async Task<string> GenerateQwenMultimodalImageAsync(
        string endpoint, string apiKey, string prompt, string model,
        string size, IReadOnlyList<string>? referenceImages, CancellationToken cancel)
    {
        var url = endpoint.TrimEnd('/') + "/services/aigc/multimodal-generation/generation";
        var content = new List<object>();
        if (referenceImages is { Count: > 0 })
            foreach (var img in referenceImages)
                content.Add(new { image = img }); // URL 或 Data URI
        content.Add(new { text = prompt });

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = new Dictionary<string, object>
            {
                ["messages"] = new[] { new { role = "user", content = (object)content } }
            },
            ["parameters"] = new Dictionary<string, object>
            {
                ["size"] = ToQwenSize(size),
                ["watermark"] = false
            }
        };
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {apiKey}");
        using var res = await _client.SendAsync(req, cancel);
        var respText = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) HandleNonSuccess(res, respText, url, model);

        using var doc = JsonDocument.Parse(respText);
        var root = doc.RootElement;
        if (root.TryGetProperty("output", out var output) &&
            output.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var msg) &&
            msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in c.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.String)
                    return img.GetString()!;
        }
        throw new ApiException("千问图片 API 未返回结果");
    }

    /// <summary>千问旧版（qwen-image / plus / max / wan）：异步 text2image 任务，提交后轮询。</summary>
    private static async Task<string> GenerateQwenAsyncTaskImageAsync(
        string endpoint, string apiKey, string prompt, string model,
        string size, CancellationToken cancel)
    {
        var submitUrl = endpoint.TrimEnd('/') + "/services/aigc/text2image/image-synthesis";
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = new Dictionary<string, object> { ["prompt"] = prompt },
            ["parameters"] = new Dictionary<string, object>
            {
                ["size"] = ToQwenSize(size),
                ["n"] = 1,
                ["watermark"] = false
            }
        };
        var json = JsonSerializer.Serialize(body);

        string taskId;
        using (var req = new HttpRequestMessage(HttpMethod.Post, submitUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        })
        {
            req.Headers.Add("Authorization", $"Bearer {apiKey}");
            req.Headers.Add("X-DashScope-Async", "enable");
            using var res = await _client.SendAsync(req, cancel);
            var respText = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) HandleNonSuccess(res, respText, submitUrl, model);
            using var doc = JsonDocument.Parse(respText);
            var output = doc.RootElement.GetProperty("output");
            taskId = output.TryGetProperty("task_id", out var tid) && tid.ValueKind == JsonValueKind.String
                ? tid.GetString()!
                : throw new ApiException("千问图片任务未返回 task_id");
        }

        var pollUrl = endpoint.TrimEnd('/') + "/tasks/" + Uri.EscapeDataString(taskId);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var (status, jsonResult, raw) = await GetJsonAsync(pollUrl, apiKey, cancel);
            if (status == System.Net.HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(3000, cancel);
                continue;
            }
            if (status != System.Net.HttpStatusCode.OK)
                HandleNonSuccess(new HttpResponseMessage(status), raw, pollUrl, model);
            if (jsonResult is not { } root || !root.TryGetPropertyValue("output", out var outputNode) || outputNode is not JsonObject output)
                throw new ApiException($"千问图片任务查询失败：{raw}");

            var taskStatus = output["task_status"]?.GetValue<string>() ?? "";
            if (taskStatus is "SUCCEEDED" or "SUCCESS")
            {
                if (output["results"] is JsonArray results && results.Count > 0 && results[0] is JsonObject r)
                {
                    if (r["url"] is JsonValue u && u.TryGetValue<string>(out var uStr)) return uStr;
                    if (r["b64_image"] is JsonValue b && b.TryGetValue<string>(out var bStr))
                        return "data:image/png;base64," + bStr;
                }
                if (output["url"] is JsonValue ou && ou.TryGetValue<string>(out var ouStr)) return ouStr;
                throw new ApiException("千问图片任务成功但未找到图片");
            }
            if (taskStatus is "FAILED" or "FAILURE" or "CANCELED" or "UNKNOWN")
            {
                var msg = output["message"]?.GetValue<string>() ?? "未知错误";
                throw new ApiException($"千问图片生成失败：{msg}");
            }
            await Task.Delay(2000, cancel);
        }
    }

    /// <summary>POST 图片请求并解析 data[0].url（OpenAI 兼容返回结构）。</summary>
    private static async Task<string> PostImageAndGetUrl(
        string url, string apiKey, Dictionary<string, object> body,
        string model, CancellationToken cancel)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var res = await _client.SendAsync(req, cancel);
        var respText = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) HandleNonSuccess(res, respText, url, model);

        using var doc = JsonDocument.Parse(respText);
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0) throw new ApiException("图片 API 未返回有效结果");
        var first = data[0];
        if (first.TryGetProperty("url", out var imgUrl) && imgUrl.ValueKind == JsonValueKind.String)
            return imgUrl.GetString()!;
        if (first.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
            return "data:image/png;base64," + b64.GetString()!;
        throw new ApiException("图片 API 未返回结果");
    }

    /// <summary>下载图片到字节数组。支持普通 URL 与 Data URI（个别服务商直接返回 base64）。</summary>
    public static async Task<byte[]> DownloadImageAsync(string imageUrl, CancellationToken cancel = default)
    {
        if (imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return DecodeDataUrl(imageUrl).Bytes;
        using var res = await _client.GetAsync(imageUrl, cancel);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync();
    }

    /// <summary>
    /// 创建视频生成任务，返回任务 ID。按服务商适配请求格式（依据各官方文档）：
    ///   Agnes：agnes-video-2.5 / 2.5-flash，返回 video_id；
    ///   千问 Wan：DashScope 异步任务（text2video / image2video），返回 task_id；
    ///   字节 Seedance：火山方舟异步任务，返回任务 id；
    ///   OpenAI Sora：/videos 创建，返回视频 id；
    ///   其它（自定义 / ModelScope / DeepSeek）：尝试 OpenAI 兼容 /videos，失败时提示未适配。
    /// </summary>
    public static async Task<string> CreateVideoTaskAsync(
        string endpoint, string apiKey, string model,
        string prompt, string mode = "text",
        int seconds = 5, string size = "720P", string aspectRatio = "16:9",
        string? firstFrame = null, string? lastFrame = null,
        IReadOnlyList<string>? referenceImages = null,
        IReadOnlyList<string>? referenceAudios = null,
        IReadOnlyList<VideoReference>? referenceVideos = null,
        ApiProvider provider = ApiProvider.Agnes,
        CancellationToken cancel = default)
    {
        return provider switch
        {
            ApiProvider.Qwen => await CreateQwenVideoTaskAsync(endpoint, apiKey, model, prompt, seconds, size, referenceImages, cancel),
            ApiProvider.OpenAI => await CreateOpenAIVideoTaskAsync(endpoint, apiKey, model, prompt, size, referenceImages, cancel),
            ApiProvider.ByteDance => await CreateByteDanceVideoTaskAsync(endpoint, apiKey, model, prompt, referenceImages, cancel),
            _ => await CreateAgnesVideoTaskAsync(endpoint, apiKey, model, prompt, mode, seconds, size, aspectRatio, firstFrame, lastFrame, referenceImages, referenceAudios, referenceVideos, cancel)
        };
    }

    /// <summary>Agnes 视频：创建任务（agnes-video-2.5 / agnes-video-2.5-flash），返回 video_id。
    /// mode：text=文生视频，keyframe=首尾帧控制，reference=参考生成。
    /// 参考媒体传入 base64 Data URL（与图生图一致）；videos 仅 agnes-video-2.5（非 Flash）支持。</summary>
    private static async Task<string> CreateAgnesVideoTaskAsync(
        string endpoint, string apiKey, string model,
        string prompt, string mode,
        int seconds, string size, string aspectRatio,
        string? firstFrame, string? lastFrame,
        IReadOnlyList<string>? referenceImages,
        IReadOnlyList<string>? referenceAudios,
        IReadOnlyList<VideoReference>? referenceVideos,
        CancellationToken cancel)
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

    /// <summary>轮询视频结果直到完成或失败，返回视频 URL。按服务商适配查询接口。</summary>
    public static async Task<string> PollVideoResultAsync(
        string endpoint, string apiKey,
        string videoId, string model,
        IProgress<string>? progress = null,
        ApiProvider provider = ApiProvider.Agnes,
        CancellationToken cancel = default)
    {
        return provider switch
        {
            ApiProvider.Qwen => await PollQwenVideoTaskAsync(endpoint, apiKey, videoId, progress, cancel),
            ApiProvider.OpenAI => await PollOpenAIVideoTaskAsync(endpoint, apiKey, videoId, progress, cancel),
            ApiProvider.ByteDance => await PollByteDanceVideoTaskAsync(endpoint, apiKey, videoId, progress, cancel),
            _ => await PollAgnesVideoResultAsync(endpoint, apiKey, videoId, model, progress, cancel)
        };
    }

    /// <summary>Agnes 视频：轮询结果。
    /// 按 agnes-video-2.5 / 2.5-flash 文档使用 video_id + model_name 查询（keyframe/reference 模式必须带 model_name）。</summary>
    private static async Task<string> PollAgnesVideoResultAsync(
        string endpoint, string apiKey,
        string videoId, string model,
        IProgress<string>? progress,
        CancellationToken cancel)
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
                // 触发限流固定等 5s 后重试，让队列窗口感知到重试中
                rateLimitStreak++;
                if (rateLimitStreak >= 10)
                    HandleNonSuccess(res, respText, queryUrl, "video");
                progress?.Report("触发限流，5 秒后重试…");
                await Task.Delay(TimeSpan.FromSeconds(5), cancel);
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

    // ==================== 视频生成：各服务商适配（创建 + 轮询） ====================

    /// <summary>千问 Wan：创建视频任务（DashScope 异步任务），返回 task_id。
    /// 文生视频走 /video-synthesis，图生视频走 multimodal-generation；均带 X-DashScope-Async: enable。</summary>
    private static async Task<string> CreateQwenVideoTaskAsync(
        string endpoint, string apiKey, string model, string prompt,
        int seconds, string size, IReadOnlyList<string>? referenceImages, CancellationToken cancel)
    {
        var hasRef = referenceImages is { Count: > 0 };
        object input;
        if (hasRef)
        {
            var content = new List<object>();
            foreach (var img in referenceImages!)
                content.Add(new { image = img }); // URL 或 Data URI
            content.Add(new { text = prompt });
            input = new Dictionary<string, object>
            {
                ["messages"] = new[] { new { role = "user", content = (object)content } }
            };
        }
        else
        {
            input = new Dictionary<string, object> { ["prompt"] = prompt };
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = input,
            ["parameters"] = new Dictionary<string, object>
            {
                ["size"] = ToQwenVideoSize(size),
                ["duration"] = Math.Clamp(seconds, 1, 30)
            }
        };

        var url = endpoint.TrimEnd('/') + "/services/aigc/video-generation/video-synthesis";
        var json = JsonSerializer.Serialize(body);
        var respText = await PostJsonTextAsync(url, apiKey, json, new Dictionary<string, string>
        {
            ["X-DashScope-Async"] = "enable"
        }, cancel);

        using var doc = JsonDocument.Parse(respText);
        var root = doc.RootElement;
        if (root.TryGetProperty("output", out var output) && output.TryGetProperty("task_id", out var tid))
            return tid.GetString()!;
        throw new ApiException("千问视频任务未返回 task_id");
    }

    /// <summary>千问 Wan：轮询异步任务结果，返回视频 URL。</summary>
    private static async Task<string> PollQwenVideoTaskAsync(
        string endpoint, string apiKey, string taskId,
        IProgress<string>? progress, CancellationToken cancel)
    {
        var url = endpoint.TrimEnd('/') + "/tasks/" + Uri.EscapeDataString(taskId);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var (status, json, raw) = await GetJsonAsync(url, apiKey, cancel);
            if (status == System.Net.HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(3000, cancel);
                continue;
            }
            if (status != System.Net.HttpStatusCode.OK)
                HandleNonSuccess(new HttpResponseMessage(status), raw, url, "video");
            if (json is not { } root || root["output"] is not JsonObject output)
                throw new ApiException($"千问视频任务查询失败：{raw}");

            var taskStatus = output["task_status"]?.GetValue<string>() ?? "";
            if (taskStatus is "SUCCEEDED" or "SUCCESS")
            {
                var videoUrl = output["video_url"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(videoUrl) && output["results"] is JsonArray results && results.Count > 0 && results[0] is JsonObject r0)
                    videoUrl = r0["url"]?.GetValue<string>() ?? r0["video_url"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(videoUrl)) return videoUrl!;
                throw new ApiException("千问视频任务成功但未找到视频 URL");
            }
            if (taskStatus is "FAILED" or "FAILURE" or "CANCELED" or "UNKNOWN")
            {
                var msg = output["message"]?.GetValue<string>() ?? output["code"]?.GetValue<string>() ?? "未知错误";
                throw new ApiException($"千问视频生成失败：{msg}");
            }

            progress?.Report(taskStatus);
            await Task.Delay(3000, cancel);
        }
    }

    /// <summary>OpenAI Sora：创建视频任务，返回视频 id。</summary>
    private static async Task<string> CreateOpenAIVideoTaskAsync(
        string endpoint, string apiKey, string model, string prompt,
        string size, IReadOnlyList<string>? referenceImages, CancellationToken cancel)
    {
        var url = endpoint.TrimEnd('/') + "/videos";
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["prompt"] = prompt
        };
        if (!string.IsNullOrWhiteSpace(size))
            body["size"] = size.ToLowerInvariant();
        if (referenceImages is { Count: > 0 })
            body["input_image_url"] = referenceImages[0]; // 图生视频（Sora 2 支持首帧图）

        var json = JsonSerializer.Serialize(body);
        var respText = await PostJsonTextAsync(url, apiKey, json, null, cancel);

        using var doc = JsonDocument.Parse(respText);
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var id))
            return id.GetString()!;
        throw new ApiException("OpenAI 视频 API 未返回 id");
    }

    /// <summary>OpenAI Sora：轮询视频结果，返回视频 URL。</summary>
    private static async Task<string> PollOpenAIVideoTaskAsync(
        string endpoint, string apiKey, string videoId,
        IProgress<string>? progress, CancellationToken cancel)
    {
        var url = endpoint.TrimEnd('/') + "/videos/" + Uri.EscapeDataString(videoId);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var (status, json, raw) = await GetJsonAsync(url, apiKey, cancel);
            if (status == System.Net.HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(3000, cancel);
                continue;
            }
            if (status != System.Net.HttpStatusCode.OK)
                HandleNonSuccess(new HttpResponseMessage(status), raw, url, "video");
            if (json is not { } root)
                throw new ApiException($"OpenAI 视频任务查询失败：{raw}");

            var taskStatus = root["status"]?.GetValue<string>() ?? "";
            if (taskStatus == "completed")
            {
                string? videoUrl = null;
                if (root["assets"] is JsonObject assets)
                    videoUrl = assets["video_file_url"]?.GetValue<string>() ?? assets["video_url"]?.GetValue<string>();
                if (videoUrl == null)
                    videoUrl = root["output"]?.GetValue<string>() ?? root["url"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(videoUrl)) return videoUrl!;
                throw new ApiException("OpenAI 视频已完成但响应中未找到视频 URL");
            }
            if (taskStatus is "failed" or "cancelled" or "expired")
            {
                var msg = root["error"]?.GetValue<string>() ?? "未知错误";
                throw new ApiException($"OpenAI 视频生成失败：{msg}");
            }

            progress?.Report(taskStatus);
            await Task.Delay(3000, cancel);
        }
    }

    /// <summary>字节 Seedance（火山方舟）：创建视频任务，返回任务 id。</summary>
    private static async Task<string> CreateByteDanceVideoTaskAsync(
        string endpoint, string apiKey, string model, string prompt,
        IReadOnlyList<string>? referenceImages, CancellationToken cancel)
    {
        var url = endpoint.TrimEnd('/') + "/contents/generations/tasks";
        var content = new List<object>();
        if (referenceImages is { Count: > 0 })
            content.Add(new { type = "image_url", image_url = new { url = referenceImages[0] } });
        content.Add(new { type = "text", text = prompt });

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["content"] = content
        };

        var json = JsonSerializer.Serialize(body);
        var respText = await PostJsonTextAsync(url, apiKey, json, null, cancel);

        using var doc = JsonDocument.Parse(respText);
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var id))
            return id.GetString()!;
        throw new ApiException("字节视频 API 未返回任务 id");
    }

    /// <summary>字节 Seedance（火山方舟）：轮询任务结果，返回视频 URL。</summary>
    private static async Task<string> PollByteDanceVideoTaskAsync(
        string endpoint, string apiKey, string taskId,
        IProgress<string>? progress, CancellationToken cancel)
    {
        var url = endpoint.TrimEnd('/') + "/contents/generations/tasks/" + Uri.EscapeDataString(taskId);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var (status, json, raw) = await GetJsonAsync(url, apiKey, cancel);
            if (status == System.Net.HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(3000, cancel);
                continue;
            }
            if (status != System.Net.HttpStatusCode.OK)
                HandleNonSuccess(new HttpResponseMessage(status), raw, url, "video");
            if (json is not { } root)
                throw new ApiException($"字节视频任务查询失败：{raw}");

            var taskStatus = root["status"]?.GetValue<string>() ?? "";
            if (taskStatus == "succeeded")
            {
                string? videoUrl = null;
                if (root["content"] is JsonObject content)
                    videoUrl = content["video_url"]?.GetValue<string>() ?? content["url"]?.GetValue<string>();
                if (videoUrl == null)
                    videoUrl = root["video_url"]?.GetValue<string>() ?? root["url"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(videoUrl)) return videoUrl!;
                throw new ApiException("字节视频已完成但响应中未找到视频 URL");
            }
            if (taskStatus is "failed" or "cancelled")
            {
                var msg = root["error"]?.GetValue<string>() ?? "未知错误";
                throw new ApiException($"字节视频生成失败：{msg}");
            }

            progress?.Report(taskStatus);
            await Task.Delay(3000, cancel);
        }
    }

    /// <summary>把视频分辨率档位转为千问 Wan 尺寸（DashScope 用 宽*高）。</summary>
    private static string ToQwenVideoSize(string size)
    {
        var s = (size ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "1080p" or "2k" => "1920*1080",
            "540p" => "960*540",
            _ => "1280*720"
        };
    }

    /// <summary>POST JSON 请求并返回响应文本（统一带 Bearer 认证，可附加额外请求头）。</summary>
    private static async Task<string> PostJsonTextAsync(
        string url, string apiKey, string json,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {apiKey}");
        if (headers != null)
        {
            foreach (var kv in headers)
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        using var res = await _client.SendAsync(req, cancel);
        var respText = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            HandleNonSuccess(res, respText, url, "video");
        return respText;
    }

    /// <summary>下载视频到字节数组。支持普通 URL 与 Data URI。</summary>
    public static async Task<byte[]> DownloadVideoAsync(string videoUrl, CancellationToken cancel = default)
    {
        if (videoUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return DecodeDataUrl(videoUrl).Bytes;
        using var res = await _client.GetAsync(videoUrl, cancel);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync();
    }

    /// <summary>GET 请求并解析 JSON（用于轮询异步任务等场景）。</summary>
    private static async Task<(System.Net.HttpStatusCode Status, JsonObject? Json, string Raw)> GetJsonAsync(
        string url, string apiKey, CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"Bearer {apiKey}");
        using var res = await _client.SendAsync(req, cancel);
        var raw = await res.Content.ReadAsStringAsync();
        JsonObject? json = null;
        try { json = JsonNode.Parse(raw) as JsonObject; } catch { /* 非 JSON 响应 */ }
        return (res.StatusCode, json, raw);
    }

    /// <summary>解析 Data URI（data:image/png;base64,XXXX）为 MIME 类型与原始字节。</summary>
    private static (string Mime, byte[] Bytes) DecodeDataUrl(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0)
            throw new ApiException("无效的 Data URI（缺少分隔符 ,）");

        var mime = "image/png";
        var header = dataUrl[..comma];
        if (header.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = header.Substring(5).Split(';');
            if (parts.Length > 0 && parts[0].Contains('/'))
                mime = parts[0].Trim().ToLowerInvariant();
        }

        try
        {
            return (mime, Convert.FromBase64String(dataUrl[(comma + 1)..]));
        }
        catch (FormatException ex)
        {
            throw new ApiException($"Data URI 的 Base64 内容无效：{ex.Message}");
        }
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
