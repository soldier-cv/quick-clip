using System.Text;
using Microsoft.International.Converters.PinYinConverter;

namespace QuickClip.Services;

/// <summary>拼音工具：为中文搜索提供拼音首字母支持。</summary>
public static class PinyinUtil
{
    /// <summary>提取文本的拼音首字母（非汉字字符原样小写保留）。</summary>
    public static string GetInitials(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            if (ChineseChar.IsValidChar(ch))
            {
                var cc = new ChineseChar(ch);
                if (cc.PinyinCount > 0)
                {
                    sb.Append(char.ToLowerInvariant(cc.Pinyins[0][0]));
                    continue;
                }
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }
}
