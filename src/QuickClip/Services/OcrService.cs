using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace QuickClip.Services;

/// <summary>OCR 识别引擎：系统自带 / 离线模型包 / 视觉接口（Ollama 与 OpenAI 兼容接口共用）。</summary>
public enum OcrEngineType
{
    System,
    Local,
    VisionApi
}

/// <summary>
/// OCR 识别服务。默认使用 Windows 10/11 系统内置引擎（零模型、零联网）；
/// 也可配置 PP-OCRv6 离线包，或一个视觉 HTTP 接口（Ollama 原生 / OpenAI 兼容）。
/// 非系统引擎失败时回退系统 OCR。
/// </summary>
public sealed class OcrService
{
    // 应用生命周期内复用连接；不记录请求体中的密钥与图片内容
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly SettingsService _settings;
    private readonly OcrModelPackService _packs;

    public OcrService(SettingsService settings, OcrModelPackService packs)
    {
        _settings = settings;
        _packs = packs;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            $"QuickClip/{UpdateService.CurrentVersion}");
        return client;
    }

    /// <summary>OCR 仅在 Windows 10+ 可用（仅系统引擎需要）。</summary>
    public bool IsSupported => Environment.OSVersion.Version.Major >= 10;

    /// <summary>当前是否使用系统内置引擎。</summary>
    public bool IsSystemEngine => _settings.OcrEngine == OcrEngineType.System;

    /// <summary>当前是否使用下载的离线模型包。</summary>
    public bool IsLocalEngine => _settings.OcrEngine == OcrEngineType.Local;

    /// <summary>最近一次识别是否发生降级（AI 引擎失败回退系统 OCR），供 UI 提示。</summary>
    public string? LastWarning { get; private set; }

    /// <summary>实际产出文字的引擎标题（离线识别 / ollama-模型 / OpenAI 模型名）。</summary>
    public string LastEngineTitle { get; private set; } = "离线识别";

    /// <summary>当前配置对应的标题（尚未识别时也可用于测试提示）。</summary>
    public string ConfiguredEngineTitle => DescribeEngine(_settings.OcrEngine);

    /// <summary>按配置的引擎识别图片文字；AI 引擎失败时自动回退系统 OCR。</summary>
    public async Task<string?> RecognizeAsync(string imagePath)
    {
        LastWarning = null;
        LastEngineTitle = DescribeEngine(_settings.OcrEngine);

        if (!File.Exists(imagePath))
        {
            LastWarning = "图片文件不存在";
            return null;
        }

        ClipboardImageNormalizer.RepairFileIfFullyTransparent(imagePath);

        if (_settings.OcrEngine == OcrEngineType.Local)
        {
            try
            {
                string? text = await _packs.RecognizeAsync(imagePath);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    LastEngineTitle = DescribeEngine(OcrEngineType.Local);
                    return text;
                }

                LastWarning = "离线模型未返回文字，已回退系统 OCR";
            }
            catch (Exception ex)
            {
                DebugLog.LogException("离线模型 OCR 失败，回退系统 OCR", ex);
                LastWarning = "离线模型识别失败：" + SummarizeError(ex) + "，已回退系统 OCR";
            }
        }
        else if (_settings.OcrEngine == OcrEngineType.VisionApi)
        {
            if (string.IsNullOrWhiteSpace(_settings.VisionApiModel))
            {
                LastWarning = "未配置视觉模型，已回退系统 OCR";
                DebugLog.Log("视觉 OCR 跳过：未填写模型名");
            }
            else if (string.IsNullOrWhiteSpace(_settings.VisionApiUrl))
            {
                LastWarning = "未配置视觉接口地址，已回退系统 OCR";
                DebugLog.Log("视觉 OCR 跳过：未填写接口地址");
            }
            else
            {
                try
                {
                    string? text = await RecognizeWithVisionApiAsync(imagePath);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        LastEngineTitle = DescribeEngine(OcrEngineType.VisionApi);
                        return text;
                    }

                    LastWarning = "视觉接口未返回文字，已回退系统 OCR";
                    DebugLog.Log("视觉 OCR 空响应，回退系统 OCR endpoint=" +
                                 DebugLog.DescribeUrl(_settings.VisionApiUrl));
                }
                catch (Exception ex)
                {
                    DebugLog.LogException("视觉 OCR 失败，回退系统 OCR", ex);
                    LastWarning = "视觉接口识别失败：" + SummarizeError(ex) + "，已回退系统 OCR";
                }
            }
        }

        string? systemText = await RecognizeWithSystemAsync(imagePath);
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            LastEngineTitle = DescribeEngine(OcrEngineType.System);
            return systemText;
        }

        if (string.IsNullOrEmpty(LastWarning))
        {
            LastWarning = "系统 OCR 未识别到文字";
        }
        else if (!LastWarning.Contains("系统 OCR", StringComparison.Ordinal))
        {
            LastWarning += "；系统 OCR 也未识别到文字";
        }

        return null;
    }

    /// <summary>用当前配置探测接口（不回退系统 OCR），供设置页测试按钮。</summary>
    public async Task<string> ProbeConfiguredEngineAsync()
    {
        if (_settings.OcrEngine == OcrEngineType.System)
        {
            return "系统离线 OCR 无需测试接口。请在列表里对图片点 OCR。";
        }

        if (_settings.OcrEngine == OcrEngineType.Local)
        {
            var resolved = _packs.ResolveCurrent();
            if (resolved.Error != null)
            {
                return "失败：" + resolved.Error;
            }

            string localTemp = Path.Combine(Path.GetTempPath(), $"quickclip-ocr-probe-{Guid.NewGuid():N}.png");
            try
            {
                WriteProbeImage(localTemp);
                string? text = await _packs.RecognizeAsync(localTemp);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return $"已加载 {resolved.Title}，但未返回文字。";
                }

                string snippet = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (snippet.Length > 80)
                {
                    snippet = snippet[..80] + "…";
                }

                return $"成功（{resolved.Title}）：{snippet}";
            }
            catch (Exception ex)
            {
                DebugLog.LogException("离线 OCR 测试失败", ex);
                return "失败：" + SummarizeError(ex);
            }
            finally
            {
                try
                {
                    if (File.Exists(localTemp))
                    {
                        File.Delete(localTemp);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        if (_settings.OcrEngine == OcrEngineType.VisionApi &&
            string.IsNullOrWhiteSpace(_settings.VisionApiModel))
        {
            return "失败：未填写视觉模型。";
        }

        if (_settings.OcrEngine == OcrEngineType.VisionApi &&
            string.IsNullOrWhiteSpace(_settings.VisionApiUrl))
        {
            return "失败：未填写接口地址。";
        }

        string temp = Path.Combine(Path.GetTempPath(), $"quickclip-ocr-probe-{Guid.NewGuid():N}.png");
        try
        {
            WriteProbeImage(temp);
            string? text = await RecognizeWithVisionApiAsync(temp);

            if (string.IsNullOrWhiteSpace(text))
            {
                return $"接口已连通（{ConfiguredEngineTitle}），但未返回文字。请确认模型支持看图。";
            }

            string snippet = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (snippet.Length > 80)
            {
                snippet = snippet[..80] + "…";
            }

            return $"成功（{ConfiguredEngineTitle}）：{snippet}";
        }
        catch (Exception ex)
        {
            DebugLog.LogException("OCR 接口测试失败", ex);
            return "失败：" + SummarizeError(ex);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    public string DescribeEngine(OcrEngineType engine) => engine switch
    {
        OcrEngineType.Local => _packs.DescribeCurrentPack(),
        OcrEngineType.VisionApi => DescribeVisionTitle(),
        _ => "离线识别"
    };

    private string DescribeVisionTitle()
    {
        string model = _settings.VisionApiModel.Trim();
        if (model.Length == 0)
        {
            return "视觉接口";
        }

        return IsOllamaNativeEndpoint(_settings.VisionApiUrl) ? "ollama-" + model : model;
    }

    /// <summary>Windows 内置 OCR：识别图片文件中的文字（离线）。</summary>
    private async Task<string?> RecognizeWithSystemAsync(string imagePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(imagePath);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        // OCR 引擎要求特定像素格式，必要时转换
        if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans"))
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (engine == null)
        {
            string lang = "系统 OCR 引擎不可用（请在系统设置中安装中文/英文 OCR 语言包）";
            LastWarning = string.IsNullOrEmpty(LastWarning) ? lang : LastWarning + "；" + lang;
            DebugLog.Log("系统 OCR 引擎创建失败：无可用语言包");
            return null;
        }

        // 预处理缩放：
        // - 小图（长边 < 1000px）放大后再识别，Windows OCR 对小字体的识别率会显著提升；
        // - 超大图缩到引擎支持的最大尺寸，否则引擎直接报错。
        double scale = 1.0;
        if (decoder.PixelWidth > 0 && decoder.PixelHeight > 0)
        {
            double longSide = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            if (longSide < 1000)
            {
                scale = Math.Min(4.0, 1000.0 / longSide);
            }
            else if (longSide > OcrEngine.MaxImageDimension)
            {
                scale = (double)OcrEngine.MaxImageDimension / longSide;
            }
        }

        if (Math.Abs(scale - 1.0) > 0.001)
        {
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
                ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale)
            };
            softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        }

        var result = await engine.RecognizeAsync(softwareBitmap);
        return string.IsNullOrWhiteSpace(result.Text) ? null : result.Text;
    }

    /// <summary>
    /// 按 URL 判断协议：含 /api/generate 或 /api/chat 走 Ollama 原生；其余按 OpenAI chat/completions。
    /// Ollama 的 /v1/chat/completions 走兼容协议。
    /// </summary>
    internal static bool IsOllamaNativeEndpoint(string url)
    {
        string trimmed = url.Trim();
        return trimmed.Contains("/api/generate", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("/api/chat", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> RecognizeWithVisionApiAsync(string imagePath)
    {
        string endpoint = NormalizeEndpoint(_settings.VisionApiUrl);
        bool ollama = IsOllamaNativeEndpoint(endpoint);
        DebugLog.Log(
            $"视觉 OCR 请求 protocol={(ollama ? "ollama" : "openai")} model={_settings.VisionApiModel.Trim()} " +
            $"endpoint={DebugLog.DescribeUrl(endpoint)} hasKey={!string.IsNullOrWhiteSpace(_settings.VisionApiKey)}");

        return ollama
            ? await RecognizeWithOllamaAsync(imagePath, endpoint)
            : await RecognizeWithOpenAiAsync(imagePath, endpoint);
    }

    /// <summary>Ollama 原生：POST /api/generate，图片以 base64 传入。</summary>
    private async Task<string?> RecognizeWithOllamaAsync(string imagePath, string endpoint)
    {
        var request = new
        {
            model = _settings.VisionApiModel,
            prompt = "请识别图片中的全部文字，仅返回识别出的文字内容。",
            images = new[] { ToBase64(imagePath) },
            stream = false
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        AttachOptionalBearer(httpRequest);
        httpRequest.Content = JsonContent.Create(request);

        var response = await Http.SendAsync(httpRequest);
        await EnsureSuccessWithBodyAsync(response);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string? text = doc.RootElement.TryGetProperty("response", out var el) ? el.GetString() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>OpenAI 兼容视觉 API：对用户填写的完整 endpoint 原样 POST（须含 chat/completions 等路径）。</summary>
    private async Task<string?> RecognizeWithOpenAiAsync(string imagePath, string endpoint)
    {
        var request = new
        {
            model = _settings.VisionApiModel,
            max_tokens = 2048,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "请识别图片中的全部文字，仅返回识别出的文字内容。" },
                        new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{ToJpegBase64(imagePath)}" } }
                    }
                }
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        AttachOptionalBearer(httpRequest);
        httpRequest.Content = JsonContent.Create(request);

        var response = await Http.SendAsync(httpRequest);
        await EnsureSuccessWithBodyAsync(response);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string? text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private void AttachOptionalBearer(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_settings.VisionApiKey))
        {
            return;
        }

        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.VisionApiKey);
    }

    /// <summary>图片转 JPEG base64；全透明 Alpha 先铺白底，长边超过 1280 再缩小，减轻网关 502。</summary>
    private static string ToBase64(string imagePath) => ToJpegBase64(imagePath);

    private static string ToJpegBase64(string imagePath)
    {
        ClipboardImageNormalizer.RepairFileIfFullyTransparent(imagePath);
        using var original = new System.Drawing.Bitmap(imagePath);
        int longSide = Math.Max(original.Width, original.Height);
        const int maxLongSide = 1280;
        double scale = longSide > maxLongSide ? (double)maxLongSide / longSide : 1.0;
        int width = Math.Max(1, (int)(original.Width * scale));
        int height = Math.Max(1, (int)(original.Height * scale));

        using var canvas = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(canvas))
        {
            g.Clear(System.Drawing.Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(original, 0, 0, width, height);
        }

        using var stream = new MemoryStream();
        canvas.Save(stream, System.Drawing.Imaging.ImageFormat.Jpeg);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync();
        }
        catch
        {
            // ignore
        }

        body = CompactJsonError(body);
        string suffix = string.IsNullOrEmpty(body) ? string.Empty : " " + body;
        DebugLog.Log(
            $"视觉 OCR HTTP {(int)response.StatusCode} {response.ReasonPhrase} " +
            $"url={DebugLog.DescribeUrl(response.RequestMessage?.RequestUri?.ToString())} body={suffix}");
        throw new HttpRequestException(
            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.{suffix}");
    }

    private static string CompactJsonError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return TrimOneLine(error.GetString());
                }

                if (error.TryGetProperty("message", out JsonElement message))
                {
                    return TrimOneLine(message.GetString());
                }
            }
        }
        catch
        {
            // 非 JSON 则截断原文
        }

        return TrimOneLine(body);
    }

    private static string TrimOneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= 180 ? one : one[..180] + "…";
    }

    private static string SummarizeError(Exception ex)
    {
        if (ex is HttpRequestException http && !string.IsNullOrWhiteSpace(http.Message))
        {
            return TrimOneLine(http.Message);
        }

        if (ex is TaskCanceledException)
        {
            return "请求超时";
        }

        return TrimOneLine(ex.Message);
    }

    private static void WriteProbeImage(string path)
    {
        using var bmp = new System.Drawing.Bitmap(320, 80, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.White);
            using var font = new System.Drawing.Font("Segoe UI", 22, System.Drawing.FontStyle.Bold);
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
            g.DrawString("QuickClip OCR 123", font, brush, 12, 18);
        }

        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>去掉首尾空白与末尾 /，原样作为请求地址（不追加任何路径）。</summary>
    private static string NormalizeEndpoint(string url) => url.Trim().TrimEnd('/');
}
