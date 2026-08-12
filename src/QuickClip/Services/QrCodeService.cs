using System.Drawing;
using QRCoder;
using ZXing;
using ZXing.Windows.Compatibility;

namespace QuickClip.Services;

/// <summary>纯离线二维码生成与识别服务。</summary>
public sealed class QrCodeService
{
    /// <summary>使用 QRCoder 将文本生成为 PNG 位图字节。</summary>
    public byte[] GeneratePng(string content, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(data);
        return qrCode.GetGraphic(pixelsPerModule);
    }

    /// <summary>使用 ZXing 识别图片文件中的二维码，返回文本内容（识别失败返回 null）。</summary>
    public string? Decode(string imagePath)
    {
        try
        {
            using var bitmap = new Bitmap(imagePath);
            var reader = new BarcodeReader
            {
                AutoRotate = true,
                Options =
                {
                    TryHarder = true,
                    PossibleFormats = new[] { BarcodeFormat.QR_CODE }
                }
            };
            var result = reader.Decode(bitmap);
            return result?.Text;
        }
        catch (Exception ex)
        {
            // 损坏图片 / 非二维码等：静默失败即可，详细原因进诊断日志
            DebugLog.LogException("二维码识别失败", ex);
            return null;
        }
    }
}
