using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using QuickClip.Services;

namespace QuickClip.Native;

/// <summary>
/// 原生 Win32 剪贴板读写（OpenClipboard / GetClipboardData / SetClipboardData）。
/// 相比 WPF/OLE 的 Clipboard API：
/// · 打开失败立即返回（带短重试），不会在属主进程卡死时无限阻塞；
/// · 写入前 EmptyClipboard 直接接管，不等待上一个 OLE 属主完成延迟渲染；
/// · 剪贴板被占用时通过 GetOpenClipboardWindow 把占用进程记入日志，便于排查。
/// </summary>
public static class NativeClipboard
{
    /// <summary>写入（用户操作）：多试几次再放弃。</summary>
    private const int WriteOpenAttempts = 8;

    /// <summary>读取（捕获事件）：快速失败即可，不阻塞监听。</summary>
    private const int ReadOpenAttempts = 2;

    private const int OpenRetryDelayMs = 60;

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;

    /// <summary>文本 / 文件列表读取上限（字节），防分配炸弹。</summary>
    private const long MaxTextReadBytes = 16 * 1024 * 1024;

    /// <summary>位图 DIB 读取上限（字节），4K 截图约 33MB，8K 约 132MB。</summary>
    private const long MaxDibReadBytes = 512 * 1024 * 1024;

    private static readonly Encoding AnsiEncoding = CreateAnsiEncoding();

    private static readonly object OccupantLogLock = new();
    private static DateTime _lastOccupantLogUtc = DateTime.MinValue;

    private static uint? _pngFormatId;

    /// <summary>CF_TEXT 使用系统 ANSI 代码页编码，避免 CJK 系统下乱码。</summary>
    private static Encoding CreateAnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)NativeMethods.GetACP());
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    // ---------- 打开（失败重试 + 占用人诊断） ----------

    private static bool TryOpen(int attempts)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            if (i < attempts - 1)
            {
                Thread.Sleep(OpenRetryDelayMs);
            }
        }

        LogClipboardOccupant();
        return false;
    }

    /// <summary>记录当前占用剪贴板的窗口/进程（限频，避免捕获事件风暴刷日志）。</summary>
    private static void LogClipboardOccupant()
    {
        lock (OccupantLogLock)
        {
            DateTime nowUtc = DateTime.UtcNow;
            if ((nowUtc - _lastOccupantLogUtc).TotalSeconds < 10)
            {
                return;
            }

            _lastOccupantLogUtc = nowUtc;
        }

        int lastError = Marshal.GetLastWin32Error();
        IntPtr hwnd = NativeMethods.GetOpenClipboardWindow();
        string process = "（无窗口进程，或本进程自身挂起的读取）";
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                process = $"{p.ProcessName} (PID {pid})";
            }
            catch
            {
                process = $"PID {pid}";
            }
        }

        Services.DebugLog.Log($"剪贴板被占用: LastError={lastError}, 占用窗口={hwnd}, 进程={process}");
    }

    // ---------- 读取 ----------

    public static string? TryGetText()
    {
        if (!TryOpen(ReadOpenAttempts))
        {
            return null;
        }

        try
        {
            if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
            {
                string? text = ReadUnicodeText(NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT));
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }

            if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_TEXT))
            {
                return ReadAnsiText(NativeMethods.GetClipboardData(NativeMethods.CF_TEXT));
            }

            return null;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public static string[]? TryGetFiles()
    {
        if (!TryOpen(ReadOpenAttempts))
        {
            return null;
        }

        try
        {
            if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP))
            {
                return null;
            }

            return ReadFileDropList(NativeMethods.GetClipboardData(NativeMethods.CF_HDROP));
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>从剪贴板取出位图（DIBV5 优先，回退 DIB）。调用方须 Dispose。</summary>
    public static Bitmap? TryGetBitmap()
    {
        if (!TryOpen(ReadOpenAttempts))
        {
            return null;
        }

        try
        {
            foreach (uint format in new[] { NativeMethods.CF_DIBV5, NativeMethods.CF_DIB })
            {
                if (!NativeMethods.IsClipboardFormatAvailable(format))
                {
                    continue;
                }

                Bitmap? bmp = DibToBitmap(NativeMethods.GetClipboardData(format));
                if (bmp != null)
                {
                    return bmp;
                }
            }

            return null;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    // ---------- 写入 ----------

    public static bool TrySetText(string text, bool plainOnly)
    {
        if (!TryOpen(WriteOpenAttempts))
        {
            return false;
        }

        try
        {
            if (!NativeMethods.EmptyClipboard())
            {
                return false;
            }

            IntPtr hUnicode = StringToHGlobalUnicode(text);
            if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hUnicode) == IntPtr.Zero)
            {
                FreeHandle(hUnicode);
                return false;
            }

            if (!plainOnly)
            {
                // ANSI 副本写入失败不致命（Unicode 已成功）
                IntPtr hAnsi = StringToHGlobalAnsi(text);
                if (NativeMethods.SetClipboardData(NativeMethods.CF_TEXT, hAnsi) == IntPtr.Zero)
                {
                    FreeHandle(hAnsi);
                }
            }

            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public static bool TrySetFiles(string[] paths)
    {
        if (!TryOpen(WriteOpenAttempts))
        {
            return false;
        }

        try
        {
            if (!NativeMethods.EmptyClipboard())
            {
                return false;
            }

            IntPtr hDrop = FileDropListToHGlobal(paths);
            if (NativeMethods.SetClipboardData(NativeMethods.CF_HDROP, hDrop) == IntPtr.Zero)
            {
                FreeHandle(hDrop);
                return false;
            }

            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>写入 DIB（pngBytes 为预览 PNG 文件内容，可选附带注册格式 PNG）。</summary>
    public static bool TrySetBitmap(Bitmap bitmap, byte[]? pngBytes = null)
    {
        if (!TryOpen(WriteOpenAttempts))
        {
            return false;
        }

        try
        {
            if (!NativeMethods.EmptyClipboard())
            {
                return false;
            }

            // 优先写 CF_DIBV5：多数聊天软件（微信/QQ/钉钉）粘贴图片时优先读取 DIBV5，
            // 缺失时部分应用不识别 CF_DIB，导致粘贴失败（表现为“截图无法直接粘贴”）。
            IntPtr hDibV5 = BitmapToDibV5HGlobal(bitmap);
            if (hDibV5 != IntPtr.Zero && NativeMethods.SetClipboardData(NativeMethods.CF_DIBV5, hDibV5) == IntPtr.Zero)
            {
                FreeHandle(hDibV5);
            }

            IntPtr hDib = BitmapToDibHGlobal(bitmap);
            if (hDib == IntPtr.Zero || NativeMethods.SetClipboardData(NativeMethods.CF_DIB, hDib) == IntPtr.Zero)
            {
                FreeHandle(hDib);
                return false;
            }

            if (pngBytes is { Length: > 0 })
            {
                IntPtr hPng = BytesToHGlobal(pngBytes);
                if (NativeMethods.SetClipboardData(GetPngFormatId(), hPng) == IntPtr.Zero)
                {
                    FreeHandle(hPng);
                }
            }

            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static uint GetPngFormatId()
    {
        if (_pngFormatId is null)
        {
            _pngFormatId = NativeMethods.RegisterClipboardFormat("PNG");
        }

        return _pngFormatId.Value;
    }

    // ---------- 内存读取辅助 ----------

    private static string? ReadUnicodeText(IntPtr hGlobal)
    {
        byte[]? bytes = ReadAllBytes(hGlobal, MaxTextReadBytes);
        if (bytes == null || bytes.Length < 2)
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

        return Encoding.Unicode.GetString(bytes, 0, length);
    }

    private static string? ReadAnsiText(IntPtr hGlobal)
    {
        byte[]? bytes = ReadAllBytes(hGlobal, MaxTextReadBytes);
        if (bytes == null || bytes.Length == 0)
        {
            return null;
        }

        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        return AnsiEncoding.GetString(bytes, 0, length);
    }

    private static string[]? ReadFileDropList(IntPtr hGlobal)
    {
        byte[]? bytes = ReadAllBytes(hGlobal, MaxTextReadBytes);
        if (bytes == null || bytes.Length < 20)
        {
            return null;
        }

        int pFiles = BitConverter.ToInt32(bytes, 0);
        bool wide = BitConverter.ToInt32(bytes, 16) != 0;
        if (pFiles < 20 || pFiles >= bytes.Length)
        {
            return null;
        }

        string[] paths;
        if (wide)
        {
            int byteLen = (bytes.Length - pFiles) & ~1;
            paths = Encoding.Unicode.GetString(bytes, pFiles, byteLen)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            paths = AnsiEncoding.GetString(bytes, pFiles, bytes.Length - pFiles)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        return paths.Length > 0 ? paths : null;
    }

    private static Bitmap? DibToBitmap(IntPtr hGlobal)
    {
        byte[]? bytes = ReadAllBytes(hGlobal, MaxDibReadBytes);
        if (bytes == null || bytes.Length < 44)
        {
            return null;
        }

        try
        {
            int biSize = BitConverter.ToInt32(bytes, 0);
            if (biSize < 40 || biSize + 14 > bytes.Length)
            {
                return null;
            }

            // BI_BITFIELDS（32bpp v3 头）时颜色掩码跟在头部之后，需计入像素偏移
            int bitCount = BitConverter.ToInt16(bytes, 14);
            int compression = BitConverter.ToInt32(bytes, 16);
            int offBits = biSize;
            if ((bitCount == 16 || bitCount == 32) && biSize == 40 && compression == 3)
            {
                offBits += 12;
            }

            if (offBits + 14 > bytes.Length)
            {
                return null;
            }

            // 合成 BITMAPFILEHEADER + DIB，交给 GDI+ 解码（v3/v4/v5、负高度自顶向下均可处理）
            using var ms = new MemoryStream(bytes.Length + 14);
            var header = new byte[14];
            header[0] = 0x42; // 'B'
            header[1] = 0x4D; // 'M'
            BitConverter.GetBytes((uint)(bytes.Length + 14)).CopyTo(header, 2);
            BitConverter.GetBytes((uint)(offBits + 14)).CopyTo(header, 10);
            ms.Write(header, 0, 14);
            ms.Write(bytes, 0, bytes.Length);
            ms.Position = 0;

            using var fromStream = new Bitmap(ms);
            // 完整克隆：解除对临时流的依赖（GDI+ 位图可能延迟读流）
            return new Bitmap(fromStream);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReadAllBytes(IntPtr hGlobal, long maxBytes)
    {
        if (hGlobal == IntPtr.Zero)
        {
            return null;
        }

        IntPtr p = NativeMethods.GlobalLock(hGlobal);
        if (p == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            long size = (long)NativeMethods.GlobalSize(hGlobal);
            if (size <= 0 || size > maxBytes)
            {
                return null;
            }

            var bytes = new byte[(int)size];
            Marshal.Copy(p, bytes, 0, bytes.Length);
            return bytes;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.GlobalUnlock(hGlobal);
        }
    }

    // ---------- 内存写入辅助 ----------

    private static IntPtr StringToHGlobalUnicode(string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
        return BytesToHGlobal(bytes);
    }

    private static IntPtr StringToHGlobalAnsi(string text)
    {
        byte[] bytes = AnsiEncoding.GetBytes(text + "\0");
        return BytesToHGlobal(bytes);
    }

    private static IntPtr FileDropListToHGlobal(string[] paths)
    {
        // DROPFILES 结构（pFiles=20 字节） + 宽字符路径（每个以 \0 结尾，整体再补一个 \0）
        const int structSize = 20;
        var sb = new StringBuilder();
        foreach (string path in paths)
        {
            sb.Append(path).Append('\0');
        }

        sb.Append('\0');

        byte[] chars = Encoding.Unicode.GetBytes(sb.ToString());
        IntPtr h = NativeMethods.GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)(structSize + chars.Length));
        if (h == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr p = NativeMethods.GlobalLock(h);
        if (p == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(h);
            return IntPtr.Zero;
        }

        try
        {
            Marshal.WriteInt32(p, 0, structSize); // pFiles：文件名列表起始偏移
            Marshal.WriteInt32(p, 4, 0);          // pt.x
            Marshal.WriteInt32(p, 8, 0);          // pt.y
            Marshal.WriteInt32(p, 12, 0);         // fNC = FALSE
            Marshal.WriteInt32(p, 16, 1);         // fWide = TRUE（宽字符路径）
            Marshal.Copy(chars, 0, p + structSize, chars.Length);
            return h;
        }
        finally
        {
            NativeMethods.GlobalUnlock(h);
        }
    }

    /// <summary>生成 CF_DIBV5（BITMAPV5HEADER 124 字节 + 32bpp BGRA 像素），与 CF_DIB 像素一致。</summary>
    private static IntPtr BitmapToDibV5HGlobal(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            int rowBytes = width * 4;
            const int headerSize = 124;
            int sizeImage = rowBytes * height;

            IntPtr h = NativeMethods.GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(headerSize + sizeImage));
            if (h == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr p = NativeMethods.GlobalLock(h);
            if (p == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(h);
                return IntPtr.Zero;
            }

            try
            {
                var header = new NativeMethods.BITMAPV5HEADER
                {
                    bV5Size = headerSize,
                    bV5Width = width,
                    bV5Height = height, // 正高度 = 自底向上
                    bV5Planes = 1,
                    bV5BitCount = 32,
                    bV5Compression = 0, // BI_RGB
                    bV5SizeImage = (uint)sizeImage,
                    bV5CSType = 0
                };
                Marshal.StructureToPtr(header, p, false);

                // 与 CF_DIB 相同的 32bpp BGRA 像素，按行拷贝
                var row = new byte[rowBytes];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, rowBytes);
                    Marshal.Copy(row, 0, p + headerSize + (height - 1 - y) * rowBytes, rowBytes);
                }

                return h;
            }
            finally
            {
                NativeMethods.GlobalUnlock(h);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static IntPtr BitmapToDibHGlobal(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            int rowBytes = width * 4;
            const int headerSize = 40;
            int sizeImage = rowBytes * height;

            IntPtr h = NativeMethods.GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(headerSize + sizeImage));
            if (h == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr p = NativeMethods.GlobalLock(h);
            if (p == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(h);
                return IntPtr.Zero;
            }

            try
            {
                var header = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = headerSize,
                    biWidth = width,
                    biHeight = height, // 正高度 = 自底向上
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                    biSizeImage = (uint)sizeImage
                };
                Marshal.StructureToPtr(header, p, false);

                // Format32bppArgb 与 32bpp BI_RGB 均为 BGRA 字节序，可按行直接拷贝
                var row = new byte[rowBytes];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, rowBytes);
                    Marshal.Copy(row, 0, p + headerSize + (height - 1 - y) * rowBytes, rowBytes);
                }

                return h;
            }
            finally
            {
                NativeMethods.GlobalUnlock(h);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static IntPtr BytesToHGlobal(byte[] bytes)
    {
        IntPtr h = NativeMethods.GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (h == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr p = NativeMethods.GlobalLock(h);
        if (p == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(h);
            return IntPtr.Zero;
        }

        try
        {
            Marshal.Copy(bytes, 0, p, bytes.Length);
            return h;
        }
        finally
        {
            NativeMethods.GlobalUnlock(h);
        }
    }

    private static void FreeHandle(IntPtr h)
    {
        if (h != IntPtr.Zero)
        {
            NativeMethods.GlobalFree(h);
        }
    }
}
