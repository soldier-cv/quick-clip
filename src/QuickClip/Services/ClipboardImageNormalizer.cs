using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using QuickClip.Native;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace QuickClip.Services;

/// <summary>
/// 剪贴板位图规范化。
/// 原生读取的 DIB 经常 Alpha 全为 0，RGB 仍有效；
/// 直接存 PNG 后缩略图全透明，ZXing 也会当成全黑解不出二维码。
/// </summary>
public static class ClipboardImageNormalizer
{
    /// <summary>从系统剪贴板取出可落盘的位图（原生 DIB 读取，失败返回 null）。调用方须 Dispose。</summary>
    public static Bitmap? TryCaptureBitmap()
    {
        Bitmap? bitmap = NativeClipboard.TryGetBitmap();
        if (bitmap == null)
        {
            return null;
        }

        using (bitmap)
        {
            return CloneOpaqueIfNeeded(bitmap);
        }
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
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, bytes);
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
