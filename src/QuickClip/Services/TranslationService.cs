using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuickClip.Models;

namespace QuickClip.Services;

/// <summary>
/// 多源翻译服务：
/// 1. 优先调用用户已配置的 AI 大模型接口（OpenAI 兼容协议 / Ollama）；
/// 2. 未配置 AI 时自动使用免秘钥公共翻译通道；
/// 3. 自动识别语种（中译英 / 外译中）。
/// </summary>
public sealed class TranslationService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly HttpClient _http;

    public TranslationService(SettingsService settings)
    {
        _settings = settings;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
    }

    /// <summary>执行文本翻译。</summary>
    public async Task<TranslationResult> TranslateAsync(string text, string? targetLang = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TranslationResult.Failed(text ?? string.Empty, "翻译内容为空");
        }

        string trimmed = text.Trim();

        // 智能语种判定：文本含较多汉字则翻译为英文，否则翻译为中文
        string target = targetLang ?? DetectTargetLanguage(trimmed);
        string srcLang = target == "en" ? "zh" : "auto";

        // 1. 若配置了 AI 视觉/LLM 服务，优先使用高质量大模型翻译
        if (IsAiConfigured())
        {
            try
            {
                var aiResult = await TranslateWithAiAsync(trimmed, target);
                if (aiResult.Success)
                {
                    return aiResult;
                }
                DebugLog.Log($"AI 翻译未成功，降级公共通道: {aiResult.ErrorMessage}");
            }
            catch (Exception ex)
            {
                DebugLog.LogException("AI 翻译异常，回退公共通道", ex);
            }
        }

        // 2. 回退到开箱即用的公共免密翻译通道
        return await TranslateWithPublicApiAsync(trimmed, target, srcLang);
    }

    /// <summary>检测是否配置了可用的 AI 接口。</summary>
    private bool IsAiConfigured()
    {
        return !string.IsNullOrWhiteSpace(_settings.VisionApiUrl) &&
               !string.IsNullOrWhiteSpace(_settings.VisionApiModel);
    }

    /// <summary>使用 OpenAI 兼容协议或 Ollama 翻译。</summary>
    private async Task<TranslationResult> TranslateWithAiAsync(string text, string targetLang)
    {
        string targetName = targetLang == "en" ? "English" : "Simplified Chinese";
        string systemPrompt = $"You are a professional, accurate translator. Translate the provided text into {targetName}. Preserve code snippets, links, and formatting. Output ONLY the translated text without explanations, quotes, or conversational filler.";

        string url = _settings.VisionApiUrl.Trim();
        bool isOllama = url.Contains("/api/generate", StringComparison.OrdinalIgnoreCase);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(_settings.VisionApiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.VisionApiKey);
        }

        string jsonPayload;
        if (isOllama)
        {
            var payload = new
            {
                model = _settings.VisionApiModel,
                prompt = $"{systemPrompt}\n\nText:\n{text}",
                stream = false
            };
            jsonPayload = JsonSerializer.Serialize(payload);
        }
        else
        {
            var payload = new
            {
                model = _settings.VisionApiModel,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = text }
                },
                temperature = 0.2
            };
            jsonPayload = JsonSerializer.Serialize(payload);
        }

        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return TranslationResult.Failed(text, $"AI 接口返回 HTTP {(int)response.StatusCode}");
        }

        string respJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);
        var root = doc.RootElement;

        string? translated = null;
        if (isOllama && root.TryGetProperty("response", out var respProp))
        {
            translated = respProp.GetString();
        }
        else if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var contentProp))
            {
                translated = contentProp.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(translated))
        {
            return TranslationResult.Failed(text, "AI 接口未返回有效翻译结果");
        }

        return TranslationResult.Succeeded(text, translated.Trim(), "auto", targetLang, $"AI ({_settings.VisionApiModel})");
    }

    /// <summary>公共免秘钥在线翻译通道。</summary>
    private async Task<TranslationResult> TranslateWithPublicApiAsync(string text, string targetLang, string srcLang)
    {
        try
        {
            // 使用公共开放翻译网关客户端
            string escaped = Uri.EscapeDataString(text);
            string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={srcLang}&tl={targetLang}&dt=t&q={escaped}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            using var response = await _http.SendAsync(req);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var segments = doc.RootElement[0];
                    var sb = new StringBuilder();
                    if (segments.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var seg in segments.EnumerateArray())
                        {
                            if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0)
                            {
                                sb.Append(seg[0].GetString());
                            }
                        }
                    }

                    string result = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(result))
                    {
                        return TranslationResult.Succeeded(text, result, srcLang, targetLang, "公共在线服务");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("公共翻译通道异常", ex);
        }

        return TranslationResult.Failed(text, "网络连接失败或翻译服务暂不可用");
    }

    /// <summary>根据字符特征自动推断目标语言。</summary>
    private static string DetectTargetLanguage(string text)
    {
        int cjkCount = 0;
        int latinCount = 0;

        foreach (char c in text)
        {
            if (c is >= '\u4e00' and <= '\u9fff')
            {
                cjkCount++;
            }
            else if (char.IsAsciiLetter(c))
            {
                latinCount++;
            }
        }

        // 如果中文字符占比较多，则翻译为英文；否则翻译为中文
        return cjkCount > latinCount * 0.3 ? "en" : "zh";
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
