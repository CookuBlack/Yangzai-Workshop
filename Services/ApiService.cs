using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace YangzaiWorkshop.Services;

public static class ApiService
{
    private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>调用大模型 API 生成内容</summary>
    public static async Task<string?> ChatAsync(
        string endpoint, string apiKey, string model,
        string systemPrompt, string userMessage,
        IProgress<string>? progress = null)
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

        progress?.Report("请求中...");
        using var res = await _client.SendAsync(req);
        var respText = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
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

        using var doc = JsonDocument.Parse(respText);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            throw new ApiException("API 未返回有效内容");

        return choices[0].GetProperty("message")
            .GetProperty("content").GetString();
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
