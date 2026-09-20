namespace QuickClip.Models;

/// <summary>
/// 翻译引擎类型：公共免密通道（微软翻译/Google翻译） / AI 大模型翻译。
/// 
/// @author xudong.hua,gemini
/// @since 2026-09-20 15:35 星期日
/// </summary>
public enum TranslationEngineType
{
    /// <summary>微软翻译公共免密通道（国内直连开箱即用）。</summary>
    Bing = 0,

    /// <summary>Google 翻译公共免密通道（需代理或海外网络）。</summary>
    Google = 1,

    /// <summary>AI 大模型翻译（使用统一配置的 AI 接口与模型）。</summary>
    Ai = 2,

    /// <summary>兼容旧版本公共通道配置（默认指向微软翻译）。</summary>
    Public = 0
}

