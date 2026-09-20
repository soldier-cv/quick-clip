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

        // 1. 若配置并启用了 AI 大模型翻译
        if (_settings.TranslationEngine == TranslationEngineType.Ai)
        {
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
            else
            {
                DebugLog.Log("已选择 AI 翻译但未配置 AI 模型接口，回退公共通道");
            }
        }

        // 2. 公共免密通道分流（或 AI 失败回退）
        if (_settings.TranslationEngine == TranslationEngineType.Google)
        {
            var googleResult = await TranslateWithGooglePublicAsync(trimmed, target, srcLang);
            if (googleResult.Success)
            {
                return googleResult;
            }
            // 若 Google 翻译因网络环境失败，自动尝试微软通道作为双重保险
            DebugLog.Log("Google 翻译通道失败，尝试微软直连通道...");
            return await TranslateWithBingPublicAsync(trimmed, target);
        }

        // 默认及 Bing 通道
        return await TranslateWithBingPublicAsync(trimmed, target);
    }

    /// <summary>检测是否配置了可用的 AI 接口与翻译模型。</summary>
    private bool IsAiConfigured()
    {
        var profile = _settings.GetTranslationProfile();
        return !string.IsNullOrWhiteSpace(profile.ApiUrl) &&
               !string.IsNullOrWhiteSpace(_settings.TranslationModel);
    }

    /// <summary>使用 OpenAI 兼容协议或 Ollama 翻译。</summary>
    private async Task<TranslationResult> TranslateWithAiAsync(string text, string targetLang)
    {
        string targetName = targetLang switch
        {
            "en" => "英语 (English)",
            "zh" => "简体中文 (Simplified Chinese)",
            "ja" => "日语 (Japanese)",
            "ko" => "韩语 (Korean)",
            "fr" => "法语 (French)",
            "de" => "德语 (German)",
            "es" => "西班牙语 (Spanish)",
            "ru" => "俄语 (Russian)",
            _ => targetLang
        };

        string systemPrompt = _settings.TranslationPrompt;
        if (systemPrompt.Contains("{target_lang}", StringComparison.OrdinalIgnoreCase))
        {
            systemPrompt = systemPrompt.Replace("{target_lang}", targetName, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            systemPrompt += $"\n目标语言：{targetName}";
        }

        var profile = _settings.GetTranslationProfile();
        string url = profile.ApiUrl.Trim();
        bool isOllama = url.Contains("/api/generate", StringComparison.OrdinalIgnoreCase);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", profile.ApiKey);
        }

        string jsonPayload;
        if (isOllama)
        {
            var payload = new
            {
                model = _settings.TranslationModel,
                prompt = $"{systemPrompt}\n\nText:\n{text}",
                stream = false
            };
            jsonPayload = JsonSerializer.Serialize(payload);
        }
        else
        {
            var payload = new
            {
                model = _settings.TranslationModel,
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

        return TranslationResult.Succeeded(text, translated.Trim(), "auto", targetLang, $"AI ({_settings.TranslationModel})");
    }

    #region 微软翻译公共免密通道 (国内直连)

    private class BingAuth
    {
        public string Key { get; set; } = "";
        public string Token { get; set; } = "";
        public string Ig { get; set; } = "";
        public string Iid { get; set; } = "translator.5023";
        public DateTime ExpireAt { get; set; }
    }

    private BingAuth? _cachedBingAuth;
    private readonly SemaphoreSlim _bingLock = new(1, 1);

    private async Task<BingAuth?> GetBingAuthAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedBingAuth != null && DateTime.UtcNow < _cachedBingAuth.ExpireAt)
        {
            return _cachedBingAuth;
        }

        await _bingLock.WaitAsync();
        try
        {
            if (!forceRefresh && _cachedBingAuth != null && DateTime.UtcNow < _cachedBingAuth.ExpireAt)
            {
                return _cachedBingAuth;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://cn.bing.com/translator");
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            string html = await resp.Content.ReadAsStringAsync();

            var mHelper = Regex.Match(html, @"params_AbusePreventionHelper\s*=\s*\[([^,]+),""([^""]+)"",([^\]]+)\]");
            var mIg = Regex.Match(html, @"IG:""([a-zA-Z0-9]+)""");
            var mIid = Regex.Match(html, @"data-iid=""([^""]+)""");

            if (!mHelper.Success || !mIg.Success) return null;

            string key = mHelper.Groups[1].Value.Trim();
            string token = mHelper.Groups[2].Value.Trim();
            string ig = mIg.Groups[1].Value.Trim();
            string iid = mIid.Success ? mIid.Groups[1].Value.Trim() : "translator.5023";

            _cachedBingAuth = new BingAuth
            {
                Key = key,
                Token = token,
                Ig = ig,
                Iid = iid,
                ExpireAt = DateTime.UtcNow.AddMinutes(30)
            };
            return _cachedBingAuth;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("获取微软翻译凭据失败", ex);
            return null;
        }
        finally
        {
            _bingLock.Release();
        }
    }

    /// <summary>使用微软翻译公共免密通道（国内直连开箱即用）。</summary>
    private async Task<TranslationResult> TranslateWithBingPublicAsync(string text, string targetLang)
    {
        string bingTarget = targetLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : targetLang;

        for (int retry = 0; retry < 2; retry++)
        {
            try
            {
                var auth = await GetBingAuthAsync(forceRefresh: retry > 0);
                if (auth == null)
                {
                    continue;
                }

                string url = $"https://cn.bing.com/ttranslatev3?isVertical=1&&IG={auth.Ig}&IID={auth.Iid}";
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                var formData = new Dictionary<string, string>
                {
                    ["fromLang"] = "auto-detect",
                    ["text"] = text,
                    ["to"] = bingTarget,
                    ["token"] = auth.Token,
                    ["key"] = auth.Key
                };
                req.Content = new FormUrlEncodedContent(formData);

                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    _cachedBingAuth = null;
                    continue;
                }

                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var item = doc.RootElement[0];
                    if (item.TryGetProperty("translations", out var transArray) &&
                        transArray.ValueKind == JsonValueKind.Array &&
                        transArray.GetArrayLength() > 0)
                    {
                        var first = transArray[0];
                        if (first.TryGetProperty("text", out var textProp))
                        {
                            string result = textProp.GetString()?.Trim() ?? string.Empty;
                            if (!string.IsNullOrEmpty(result))
                            {
                                return TranslationResult.Succeeded(text, result, "auto", targetLang, "微软翻译");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog.LogException($"微软翻译通道异常 (重试 {retry})", ex);
                _cachedBingAuth = null;
            }
        }

        return TranslationResult.Failed(text, "网络连接失败或翻译服务暂不可用");
    }

    #endregion

    #region Google 翻译公共免密通道 (需代理)

    /// <summary>Google 翻译公共免密通道（需代理环境）。</summary>
    private async Task<TranslationResult> TranslateWithGooglePublicAsync(string text, string targetLang, string srcLang)
    {
        try
        {
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
                        return TranslationResult.Succeeded(text, result, srcLang, targetLang, "Google 翻译");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("Google 翻译通道异常", ex);
        }

        return TranslationResult.Failed(text, "Google 翻译连接失败（国内直连受限，需开启代理）");
    }

    #endregion

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
