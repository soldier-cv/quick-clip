using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace QuickClip.Services;

/// <summary>
/// 剪贴板位图规范化。
/// WPF <c>Clipboard.GetImage()</c> 读到的 DIB 经常 Alpha 全为 0，RGB 仍有效；
/// 直接存 PNG 后缩略图全透明，ZXing 也会当成全黑解不出二维码。
/// </summary>
public static class ClipboardImageNormalizer
{
    /// <summary>从系统剪贴板取出可落盘的位图（已去掉「全透明 Alpha」）。调用方须 Dispose。</summary>
    public static Bitmap? TryCaptureBitmap()
    {
        if (TryLoadPng() is { } png)
        {
            using (png)
            {
                return CloneOpaqueIfNeeded(png);
            }
        }

        try
        {
            using var gdi = System.Windows.Forms.Clipboard.ContainsImage()
                ? System.Windows.Forms.Clipboard.GetImage()
                : null;
            if (gdi is Bitmap bmp)
            {
                return CloneOpaqueIfNeeded(bmp);
            }

            if (gdi != null)
            {
                using var copy = new Bitmap(gdi);
                return CloneOpaqueIfNeeded(copy);
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("GDI 读取剪贴板图片失败", ex);
        }

        var wpf = System.Windows.Clipboard.GetImage();
        if (wpf == null)
        {
            return null;
        }

        using var fromWpf = FromBitmapSource(wpf);
        return CloneOpaqueIfNeeded(fromWpf);
    }

    /// <summary>
    /// 若 PNG 每个像素 Alpha 都是 0，则改写成 24 位不透明图并返回 true。
    /// 文件被 Bitmap 占用时先读到内存再覆盖。
    /// </summary>
    public static bool RepairFileIfFullyTransparent(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            Bitmap? rewritten = null;
            using (var src = new Bitmap(path))
            {
                if (!IsFullyTransparent(src))
                {
                    return false;
                }

                rewritten = src.Clone(
                    new Rectangle(0, 0, src.Width, src.Height),
                    PixelFormat.Format24bppRgb);
            }

            using (rewritten)
            {
                rewritten.Save(path, ImageFormat.Png);
            }

            DebugLog.Log($"已修复全透明预览图: {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.LogException("修复全透明预览图失败", ex);
            return false;
        }
    }

    internal static Bitmap CloneOpaqueIfNeeded(Bitmap src)
    {
        if (IsFullyTransparent(src))
        {
            DebugLog.Log($"剪贴板图片 Alpha 全 0，按不透明写入 {src.Width}x{src.Height}");
            return src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format24bppRgb);
        }

        return (Bitmap)src.Clone();
    }

    private static Bitmap? TryLoadPng()
    {
        foreach (string format in new[] { "PNG", "image/png" })
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsData(format))
                {
                    continue;
                }

                using var stream = CopyClipboardStream(System.Windows.Clipboard.GetData(format));
                if (stream == null)
                {
                    continue;
                }

                return new Bitmap(stream);
            }
            catch (Exception ex)
            {
                DebugLog.LogException($"读取剪贴板 {format} 失败", ex);
            }
        }

        return null;
    }

    private static MemoryStream? CopyClipboardStream(object? data)
    {
        switch (data)
        {
            case MemoryStream ms:
                ms.Position = 0;
                var copy = new MemoryStream();
                ms.CopyTo(copy);
                copy.Position = 0;
                return copy;
            case Stream stream:
                var fromStream = new MemoryStream();
                stream.CopyTo(fromStream);
                fromStream.Position = 0;
                return fromStream;
            case byte[] bytes:
                return new MemoryStream(bytes);
            default:
                return null;
        }
    }

    private static Bitmap FromBitmapSource(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bits = bmp.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(pixels, 0, bits.Scan0, pixels.Length);
        }
        finally
        {
            bmp.UnlockBits(bits);
        }

        return bmp;
    }

    internal static bool IsFullyTransparent(Bitmap bmp)
    {
        if (!HasAlpha(bmp.PixelFormat))
        {
            return false;
        }

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(data.Stride) * bmp.Height;
            var buffer = new byte[bytes];
            Marshal.Copy(data.Scan0, buffer, 0, bytes);
            int stride = Math.Abs(data.Stride);
            for (int y = 0; y < bmp.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (buffer[row + x * 4 + 3] != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static bool HasAlpha(PixelFormat format) =>
        format is PixelFormat.Format32bppArgb
            or PixelFormat.Format32bppPArgb
            or PixelFormat.Format64bppArgb
            or PixelFormat.Format64bppPArgb;
}
