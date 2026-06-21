using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

        respText ??= Task.Run(() => res.Content.ReadAsStringAsync()).Result;
        var statusInfo = (int)res.StatusCode switch
        {
            503 => $"503 服务不可用\n可能原因：地址错误、模型名「{model}」不存在或服务商临时故障\n请求地址：{url}",
            401 or 403 => $"{(int)res.StatusCode} 认证失败，请检查 API 密钥是否正确",
            404 => $"404 接口不存在，请检查 API 地址是否正确（应以 /v1 结尾）",
            429 => "429 请求频率过高，请稍后重试",
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

    /// <summary>调用图片生成 API，返回图片 URL</summary>
    public static async Task<string> GenerateImageAsync(
        string endpoint, string apiKey,
        string prompt, string model, string size = "1024x768",
        CancellationToken cancel = default)
    {
        var url = endpoint.TrimEnd('/') + "/images/generations";
        var body = new
        {
            model,
            prompt,
            size,
            extra_body = new { response_format = "url" }
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

    /// <summary>创建视频生成任务，返回 video_id</summary>
    public static async Task<string> CreateVideoTaskAsync(
        string endpoint, string apiKey, string model,
        string prompt, int width = 1152, int height = 768,
        int numFrames = 121, int frameRate = 24,
        CancellationToken cancel = default)
    {
        var url = endpoint.TrimEnd('/') + "/videos";
        var body = new
        {
            model,
            prompt,
            width,
            height,
            num_frames = numFrames,
            frame_rate = frameRate
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
        var root = doc.RootElement;
        if (root.TryGetProperty("video_id", out var vid))
            return vid.GetString()!;
        if (root.TryGetProperty("task_id", out var tid))
            return tid.GetString()!;

        throw new ApiException("视频 API 未返回 video_id");
    }

    /// <summary>轮询视频结果直到完成或失败，返回视频 URL</summary>
    public static async Task<string> PollVideoResultAsync(
        string endpoint, string apiKey,
        string videoId,
        IProgress<string>? progress = null,
        CancellationToken cancel = default)
    {
        var baseUrl = endpoint.TrimEnd('/');
        // 去掉 /v1 后缀得到根地址
        if (baseUrl.EndsWith("/v1"))
            baseUrl = baseUrl.Substring(0, baseUrl.Length - 3);
        baseUrl = baseUrl.TrimEnd('/');

        var queryUrl = $"{baseUrl}/agnesapi?video_id={videoId}";

        while (true)
        {
            cancel.ThrowIfCancellationRequested();

            using var req = new HttpRequestMessage(HttpMethod.Get, queryUrl);
            req.Headers.Add("Authorization", $"Bearer {apiKey}");

            using var res = await _client.SendAsync(req, cancel);
            var respText = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                HandleNonSuccess(res, respText, queryUrl, "video");

            using var doc = JsonDocument.Parse(respText);
            var root = doc.RootElement;
            var status = root.GetProperty("status").GetString();

            if (status == "completed")
            {
                var videoUrl = root.GetProperty("remixed_from_video_id").GetString()!;
                return videoUrl;
            }

            if (status == "failed")
            {
                var errMsg = "未知错误";
                if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                    errMsg = err.ToString();
                throw new ApiException($"视频生成失败：{errMsg}");
            }

            // 仍在排队或处理中
            var prog = 0;
            if (root.TryGetProperty("progress", out var p))
                prog = p.GetInt32();
            progress?.Report($"{status} ({prog}%)");

            // 每 5 秒轮询一次
            await Task.Delay(5000, cancel);
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
}

public class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}
