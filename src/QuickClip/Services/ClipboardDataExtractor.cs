using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using QuickClip.Models;

namespace QuickClip.Services;

/// <summary>从剪贴板抓取的一次数据快照。</summary>
public sealed class CapturedClipboardData
{
    public ClipboardContentType ContentType { get; set; }
    public string? Text { get; set; }
    public string[]? Files { get; set; }
    public string? PreviewPath { get; set; }
    public long CharCount { get; set; }
    public string? DedupKey { get; set; }
}

/// <summary>
/// 从系统剪贴板提取文本/文件/图片（需在 STA 线程执行）。
/// 仅只读系统剪贴板：超限只表示「不写入 QuickClip 历史」，绝不 Clear/Set 剪贴板，
/// 因此用户仍可把原内容粘贴到任意程序。
/// </summary>
public static class ClipboardDataExtractor
{
    public static CapturedClipboardData? Capture(AppPaths paths)
    {
        try
        {
            // 优先级：文本 > 文件 > 图片
            if (System.Windows.Clipboard.ContainsText())
            {
                return CaptureText();
            }

            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                return CaptureFiles();
            }

            if (System.Windows.Clipboard.ContainsImage())
            {
                return CaptureImage(paths);
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            // 剪贴板被其他进程占用等瞬时错误，静默忽略
        }

        return null;
    }

    private static CapturedClipboardData? CaptureText()
    {
        string text = System.Windows.Clipboard.GetText() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 超大文本：不记历史；系统剪贴板未动，别处仍可粘贴
        if (text.Length > SettingsService.MaxCaptureTextChars)
        {
            DebugLog.Log(
                $"跳过历史记录：文本过长 {text.Length} 字符 " +
                $"(上限 {SettingsService.MaxCaptureTextChars})，系统剪贴板未改动");
            return null;
        }

        bool isLink = Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        return new CapturedClipboardData
        {
            ContentType = isLink ? ClipboardContentType.Link : ClipboardContentType.Text,
            Text = text,
            CharCount = text.Length,
            DedupKey = "text:" + text
        };
    }

    /// <summary>
    /// 文件复制：系统剪贴板只有路径列表，不拷贝文件本体。
    /// 我们同样只记路径；大文件/任意体积文件都不进 SQLite BLOB，粘贴仍走系统路径。
    /// </summary>
    private static CapturedClipboardData? CaptureFiles()
    {
        var files = System.Windows.Clipboard.GetFileDropList().Cast<string>().ToArray();
        if (files.Length == 0)
        {
            return null;
        }

        string joined = string.Join(Environment.NewLine, files);
        // 路径列表本身过大时跳过入库（极端：海量文件多选）；不影响系统粘贴
        if (joined.Length > SettingsService.MaxCaptureTextChars)
        {
            DebugLog.Log(
                $"跳过历史记录：文件路径列表过长 {joined.Length} 字符 " +
                $"(上限 {SettingsService.MaxCaptureTextChars})，系统剪贴板未改动");
            return null;
        }

        long totalBytes = 0;
        var key = "files:" + string.Join('|', files.Select(f =>
        {
            try
            {
                var info = new FileInfo(f);
                if (info.Exists)
                {
                    totalBytes += info.Length;
                }

                return info.Name + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                return f;
            }
        }));

        return new CapturedClipboardData
        {
            ContentType = ClipboardContentType.File,
            Files = files,
            Text = joined,
            // 展示用：总字节数（非文件个数）
            CharCount = totalBytes > 0 ? totalBytes : files.Length,
            DedupKey = key
        };
    }

    private static CapturedClipboardData? CaptureImage(AppPaths paths)
    {
        var image = System.Windows.Clipboard.GetImage();
        if (image == null)
        {
            return null;
        }

        // 先看像素规模，避免超大图编码拖死进程
        long pixels = (long)image.PixelWidth * image.PixelHeight;
        if (pixels > SettingsService.MaxCaptureImagePixels)
        {
            DebugLog.Log(
                $"跳过历史记录：图片像素过大 {image.PixelWidth}x{image.PixelHeight} " +
                $"({pixels} px, 上限 {SettingsService.MaxCaptureImagePixels})，系统剪贴板未改动");
            return null;
        }

        string fileName = $"img_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png";
        string fullPath = Path.Combine(paths.PreviewDir, fileName);

        try
        {
            using (var stream = File.Create(fullPath))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
            }

            long size = new FileInfo(fullPath).Length;
            if (size > SettingsService.MaxCaptureImageBytes)
            {
                TryDelete(fullPath);
                DebugLog.Log(
                    $"跳过历史记录：图片落盘过大 {size} 字节 " +
                    $"(上限 {SettingsService.MaxCaptureImageBytes})，系统剪贴板未改动");
                return null;
            }

            return new CapturedClipboardData
            {
                ContentType = ClipboardContentType.Image,
                PreviewPath = fullPath,
                CharCount = size,
                DedupKey = "img:" + ComputeHash(fullPath)
            };
        }
        catch
        {
            TryDelete(fullPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>计算文件 SHA256 哈希，用于图片去重。</summary>
    public static string ComputeHash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
