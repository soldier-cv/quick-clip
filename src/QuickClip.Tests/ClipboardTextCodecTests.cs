using System.Text;
using QuickClip.Services;
using Xunit;

namespace QuickClip.Tests;

public class ClipboardTextCodecTests
{
    private static readonly Encoding Gbk = CreateGbk();

    private static Encoding CreateGbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }

    [Fact]
    public void DecodeUnicode_reads_utf16le_until_double_null()
    {
        byte[] bytes = Encoding.Unicode.GetBytes("你好\0trailing");
        Assert.Equal("你好", ClipboardTextCodec.DecodeUnicode(bytes));
    }

    [Fact]
    public void DecodeUtf8_reads_until_null()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("Hello 世界\0xx");
        Assert.Equal("Hello 世界", ClipboardTextCodec.DecodeUtf8(bytes));
    }

    [Fact]
    public void DecodeAnsiOrUtf8_prefers_utf8_for_valid_multibyte_payload()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("中文 UTF-8");
        Assert.Equal("中文 UTF-8", ClipboardTextCodec.DecodeAnsiOrUtf8(utf8, Gbk));
    }

    [Fact]
    public void DecodeAnsiOrUtf8_falls_back_to_gbk_when_utf8_is_invalid()
    {
        byte[] gbkBytes = Gbk.GetBytes("剪贴板乱码排查");
        Assert.Equal("剪贴板乱码排查", ClipboardTextCodec.DecodeAnsiOrUtf8(gbkBytes, Gbk));
    }

    [Fact]
    public void DecodeAnsiOrUtf8_strips_utf8_bom()
    {
        var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
        bytes.AddRange(Encoding.UTF8.GetBytes("BOM 文本"));
        Assert.Equal("BOM 文本", ClipboardTextCodec.DecodeAnsiOrUtf8(bytes.ToArray(), Gbk));
    }

    [Fact]
    public void DecodeAnsiOrUtf8_does_not_misread_typical_gbk_as_utf8()
    {
        byte[] gbkBytes = Gbk.GetBytes("你好世界");
        string decoded = ClipboardTextCodec.DecodeAnsiOrUtf8(gbkBytes, Gbk)!;
        Assert.Equal("你好世界", decoded);
        Assert.NotEqual(Encoding.UTF8.GetString(gbkBytes), decoded);
    }

    [Fact]
    public void DecodeAnsiOrUtf8_keeps_ascii_as_ansi_path_without_false_utf8()
    {
        byte[] ascii = "abc123"u8.ToArray();
        Assert.Equal("abc123", ClipboardTextCodec.DecodeAnsiOrUtf8(ascii, Gbk));
    }
}
