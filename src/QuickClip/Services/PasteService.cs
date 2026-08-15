using System.Drawing;
using System.IO;
using QuickClip.Native;

namespace QuickClip.Services;

/// <summary>
/// 粘贴服务：在后台 STA 线程回填剪贴板，并用 SendInput 模拟 Ctrl+V 粘贴到目标窗口，避免占用 UI 线程。
/// 剪贴板写入走原生 Win32 API：OpenClipboard 失败快速返回 + 短重试，
/// 不会像 OLE 那样在属主进程卡死时无限阻塞、进而锁死全系统剪贴板。
/// </summary>
public sealed class PasteService
{
    private volatile bool _isSelfPasting;
    private IntPtr _lastTargetWindow = IntPtr.Zero;

    /// <summary>是否处于自身粘贴回填状态（用于剪贴板监听过滤）。</summary>
    public bool IsSelfPasting => _isSelfPasting;

    /// <summary>记录唤起 QuickClip 之前的前台窗口，作为粘贴目标。</summary>
    public void RememberTargetWindow()
    {
        _lastTargetWindow = NativeMethods.GetForegroundWindow();
    }

    // ---------- 粘贴（后台回填剪贴板后模拟 Ctrl+V，异常仅记录日志） ----------

    public void PasteText(string? text, bool plainOnly = false)
    {
        DebugLog.Log($"粘贴文本: plainOnly={plainOnly}, 长度={(text?.Length ?? 0)}");
        _ = RunPasteAsync(() => CopyTextCore(text, plainOnly));
    }

    public void PasteImage(string? previewPath)
    {
        _ = RunPasteAsync(() => CopyImageCore(previewPath));
    }

    public void PasteFiles(string[]? files)
    {
        _ = RunPasteAsync(() => CopyFilesCore(files));
    }

    /// <summary>
    /// 将系统剪贴板中的文本以纯文本形式粘贴到前台窗口（全局 Ctrl+Shift+V）。
    /// 剪贴板无文本时不做任何操作。
    /// </summary>
    public void PastePlainTextFromClipboard()
    {
        _ = RunPasteAsync(() =>
        {
            string? text = NativeClipboard.TryGetText();
            if (!string.IsNullOrEmpty(text))
            {
                CopyTextCore(text, plainOnly: true);
            }
        });
    }

    // ---------- 仅覆盖系统剪贴板（Ctrl+C 路径，返回 Task 便于调用方按序刷新） ----------

    public Task CopyTextAsync(string? text, bool plainOnly = false) =>
        CopyCoreAsync(() => CopyTextCore(text, plainOnly));

    public Task CopyImageAsync(string? previewPath) =>
        CopyCoreAsync(() => CopyImageCore(previewPath));

    public Task CopyFilesAsync(string[]? files) =>
        CopyCoreAsync(() => CopyFilesCore(files));

    // ---------- 同步旧入口（内部转为后台执行，保持调用方签名不变） ----------

    public void CopyText(string? text, bool plainOnly = false) =>
        _ = CopyTextAsync(text, plainOnly);

    public void CopyImage(string? previewPath) =>
        _ = CopyImageAsync(previewPath);

    public void CopyFiles(string[]? files) =>
        _ = CopyFilesAsync(files);

    /// <summary>模拟 Ctrl+V 粘贴到之前记录的目标窗口。</summary>
    private void SimulatePaste()
    {
        if (_lastTargetWindow != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(_lastTargetWindow);
        }

        NativeMethods.SendCtrlV();
    }

    /// <summary>在后台 STA 线程回填剪贴板，完成后模拟粘贴；异常仅记录日志，不影响主流程。</summary>
    private async Task RunPasteAsync(Action setter)
    {
        try
        {
            await CopyCoreAsync(setter);
            SimulatePaste();
        }
        catch (Exception ex)
        {
            DebugLog.LogException("粘贴失败", ex);
        }
    }

    /// <summary>在独立 STA 线程上执行剪贴板写入，避免剪贴板被占用时阻塞 UI 线程。</summary>
    private Task CopyCoreAsync(Action setter) => StaTask.Run(() => SetClipboard(setter));

    /// <summary>将文本覆盖到系统剪贴板（需在 STA 线程调用）。</summary>
    private void CopyTextCore(string? text, bool plainOnly)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        SetClipboard(() =>
        {
            if (!NativeClipboard.TrySetText(text, plainOnly))
            {
                throw ClipboardBusyException();
            }
        });
    }

    /// <summary>将图片覆盖到系统剪贴板（需在 STA 线程调用）。</summary>
    private void CopyImageCore(string? previewPath)
    {
        if (string.IsNullOrEmpty(previewPath) || !File.Exists(previewPath))
        {
            return;
        }

        SetClipboard(() =>
        {
            byte[]? png = null;
            try
            {
                png = File.ReadAllBytes(previewPath);
            }
            catch
            {
                // PNG 副本读取失败不致命，DIB 仍可粘贴
            }

            using var bitmap = new Bitmap(previewPath);
            if (!NativeClipboard.TrySetBitmap(bitmap, png))
            {
                throw ClipboardBusyException();
            }
        });
    }

    /// <summary>将文件列表覆盖到系统剪贴板（需在 STA 线程调用）。</summary>
    private void CopyFilesCore(string[]? files)
    {
        if (files is not { Length: > 0 })
        {
            return;
        }

        SetClipboard(() =>
        {
            if (!NativeClipboard.TrySetFiles(files))
            {
                throw ClipboardBusyException();
            }
        });
    }

    /// <summary>剪贴板被其他进程占用（打开重试后仍失败）。</summary>
    private static Exception ClipboardBusyException() =>
        new InvalidOperationException("剪贴板被其他进程占用（打开失败）");

    /// <summary>
    /// 自身回写系统剪贴板前通知（流水线用来忽略捕获，防止列表再插一条到顶部）。
    /// </summary>
    public event Action? SelfClipboardWrite;

    /// <summary>设置剪贴板并短暂开启自粘贴标记，避免回填事件被重复捕获。</summary>
    private void SetClipboard(Action setter)
    {
        _isSelfPasting = true;
        try
        {
            // 先通知流水线进入抑制，再写剪贴板（顺序重要：避免 WM 先到）
            try
            {
                SelfClipboardWrite?.Invoke();
            }
            catch
            {
                // 监听方异常不影响写剪贴板
            }

            setter();
        }
        finally
        {
            // 略加长：异步捕获 + 双击时复制+粘贴两次写剪贴板
            _ = Task.Delay(2500).ContinueWith(_ => _isSelfPasting = false);
        }
    }
}
