using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace QuickClip.Services;

/// <summary>OCR 识别引擎：系统自带（离线）/ Ollama（本地大模型）/ OpenAI（云端 API）。</summary>
public enum OcrEngineType
{
    System,
    Ollama,
    OpenAi
}

/// <summary>
/// OCR 识别服务。默认使用 Windows 10/11 系统内置引擎（零模型、零联网）；
/// 也可配置 Ollama 本地视觉模型或 OpenAI 云端视觉 API 提升识别率，
/// AI 引擎失败时自动回退到系统 OCR，不影响使用。
/// </summary>
public sealed class OcrService
{
    // 应用生命周期内复用连接；不记录请求体中的密钥与图片内容
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly SettingsService _settings;

    public OcrService(SettingsService settings)
    {
        _settings = settings;
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

    /// <summary>最近一次识别是否发生降级（AI 引擎失败回退系统 OCR），供 UI 提示。</summary>
    public string? LastWarning { get; private set; }

    /// <summary>按配置的引擎识别图片文字；AI 引擎失败时自动回退系统 OCR。</summary>
    public async Task<string?> RecognizeAsync(string imagePath)
    {
        LastWarning = null;

        if (!File.Exists(imagePath))
        {
            return null;
        }

        if (_settings.OcrEngine == OcrEngineType.Ollama)
        {
            if (string.IsNullOrWhiteSpace(_settings.OllamaModel))
            {
                LastWarning = "未配置 Ollama 模型，已回退系统 OCR";
            }
            else
            {
                try
                {
                    string? text = await RecognizeWithOllamaAsync(imagePath);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }

                    LastWarning = "Ollama 未返回文字，已回退系统 OCR";
                }
                catch (Exception ex)
                {
                    DebugLog.LogException("Ollama OCR 失败，回退系统 OCR", ex);
                    LastWarning = "Ollama 识别失败（服务未启动或模型不可用），已回退系统 OCR";
                }
            }
        }
        else if (_settings.OcrEngine == OcrEngineType.OpenAi)
        {
            if (string.IsNullOrWhiteSpace(_settings.OpenAiApiKey))
            {
                LastWarning = "未配置 OpenAI API Key，已回退系统 OCR";
            }
            else
            {
                try
                {
                    string? text = await RecognizeWithOpenAiAsync(imagePath);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }

                    LastWarning = "OpenAI 未返回文字，已回退系统 OCR";
                }
                catch (Exception ex)
                {
                    DebugLog.LogException("OpenAI OCR 失败，回退系统 OCR", ex);
                    LastWarning = "OpenAI 识别失败，已回退系统 OCR";
                }
            }
        }

        return await RecognizeWithSystemAsync(imagePath);
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
            LastWarning = "系统 OCR 引擎不可用（请在系统设置中安装中文/英文 OCR 语言包）";
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

    /// <summary>Ollama 本地视觉模型：POST /api/generate，图片以 base64 传入。</summary>
    private async Task<string?> RecognizeWithOllamaAsync(string imagePath)
    {
        var request = new
        {
            model = _settings.OllamaModel,
            prompt = "请识别图片中的全部文字，仅返回识别出的文字内容。",
            images = new[] { ToBase64(imagePath) },
            stream = false
        };

        // 用户配置的是完整 URL，不再拼接 /api/generate
        string endpoint = NormalizeEndpoint(_settings.OllamaBaseUrl);
        var response = await Http.PostAsJsonAsync(endpoint, request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string? text = doc.RootElement.TryGetProperty("response", out var el) ? el.GetString() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>OpenAI 兼容视觉 API：对用户填写的完整 endpoint 原样 POST（须含 chat/completions 等路径）。</summary>
    private async Task<string?> RecognizeWithOpenAiAsync(string imagePath)
    {
        var request = new
        {
            model = _settings.OpenAiModel,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "请识别图片中的全部文字，仅返回识别出的文字内容。" },
                        new { type = "image_url", image_url = new { url = $"data:image/png;base64,{ToBase64(imagePath)}" } }
                    }
                }
            }
        };

        string endpoint = NormalizeEndpoint(_settings.OpenAiBaseUrl);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.OpenAiApiKey);
        httpRequest.Content = JsonContent.Create(request);

        var response = await Http.SendAsync(httpRequest);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string? text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>图片转 base64；超大图（长边 &gt; 2048px）先等比缩小再编码，控制 API 体积与耗时。</summary>
    private static string ToBase64(string imagePath)
    {
        using var original = new System.Drawing.Bitmap(imagePath);
        int longSide = Math.Max(original.Width, original.Height);
        const int maxLongSide = 2048;
        if (longSide <= maxLongSide)
        {
            return Convert.ToBase64String(File.ReadAllBytes(imagePath));
        }

        double scale = (double)maxLongSide / longSide;
        int width = Math.Max(1, (int)(original.Width * scale));
        int height = Math.Max(1, (int)(original.Height * scale));
        using var resized = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(original, 0, 0, width, height);
        }

        using var stream = new MemoryStream();
        resized.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return Convert.ToBase64String(stream.ToArray());
    }

    /// <summary>去掉首尾空白与末尾 /，原样作为请求地址（不追加任何路径）。</summary>
    private static string NormalizeEndpoint(string url) => url.Trim().TrimEnd('/');
}
