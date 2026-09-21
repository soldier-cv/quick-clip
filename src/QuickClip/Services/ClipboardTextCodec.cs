using System.Text;

namespace QuickClip.Services;

/// <summary>
/// 剪贴板字节到字符串的解码策略。
/// CF_UNICODETEXT 始终是 UTF-16LE；CF_TEXT / RTF 在 CJK 系统上常见 ANSI(GBK)，
/// 但部分应用会写入 UTF-8。错误地一律按 ACP 或一律按 UTF-8 都会产生乱码。
/// </summary>
public static class ClipboardTextCodec
{
    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>解码 UTF-16LE（CF_UNICODETEXT），在双空终止符处截断。</summary>
    public static string? DecodeUnicode(byte[] bytes)
    {
        if (bytes.Length < 2)
        {
            return null;
        }

        int length = 0;
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0)
            {
                break;
            }

            length = i + 2;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        return Encoding.Unicode.GetString(bytes, 0, length);
    }

    /// <summary>解码 UTF-8（HTML Format），在单空终止符处截断。</summary>
    public static string? DecodeUtf8(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    /// <summary>
    /// 解码 CF_TEXT / RTF：优先识别合法 UTF-8（含 BOM），否则回退系统 ANSI 代码页。
    /// 这样既覆盖现代应用写入的 UTF-8，也覆盖记事本/老程序写入的 GBK。
    /// </summary>
    public static string? DecodeAnsiOrUtf8(byte[] bytes, Encoding ansiEncoding)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        if (LooksLikeUtf8Bom(bytes, length))
        {
            return Encoding.UTF8.GetString(bytes, 3, length - 3);
        }

        if (IsValidUtf8(bytes, length) && ContainsNonAscii(bytes, length))
        {
            return Encoding.UTF8.GetString(bytes, 0, length);
        }

        return ansiEncoding.GetString(bytes, 0, length);
    }

    private static bool LooksLikeUtf8Bom(byte[] bytes, int length) =>
        length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static bool ContainsNonAscii(byte[] bytes, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (bytes[i] >= 0x80)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidUtf8(byte[] bytes, int length)
    {
        try
        {
            Utf8Strict.GetString(bytes, 0, length);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
