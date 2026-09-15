namespace QuickClip.Models;

/// <summary>翻译响应结果。</summary>
public sealed class TranslationResult
{
    public bool Success { get; set; }

    /// <summary>原始文本。</summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>翻译后的文本。</summary>
    public string TranslatedText { get; set; } = string.Empty;

    /// <summary>源语言（例如 zh, en, ja 等）。</summary>
    public string SourceLanguage { get; set; } = string.Empty;

    /// <summary>目标语言。</summary>
    public string TargetLanguage { get; set; } = string.Empty;

    /// <summary>提供方或引擎说明（如 本地AI, 在线服务）。</summary>
    public string Engine { get; set; } = string.Empty;

    /// <summary>失败原因（当 Success 为 false 时）。</summary>
    public string? ErrorMessage { get; set; }

    public static TranslationResult Failed(string originalText, string errorMessage) => new()
    {
        Success = false,
        OriginalText = originalText,
        ErrorMessage = errorMessage
    };

    public static TranslationResult Succeeded(string originalText, string translatedText, string srcLang, string targetLang, string engine) => new()
    {
        Success = true,
        OriginalText = originalText,
        TranslatedText = translatedText,
        SourceLanguage = srcLang,
        TargetLanguage = targetLang,
        Engine = engine
    };
}
