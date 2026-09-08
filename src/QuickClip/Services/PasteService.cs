using System.Drawing;
using System.IO;
using QuickClip.Native;

namespace QuickClip.Services;

/// <summary>
/// 粘贴服务：在后台 STA 线程回填剪贴板，并用 SendInput 模拟 Ctrl+V 粘贴到目标窗口，避免占用 UI 线程。
/// 剪贴板写入走原生 Win32 API：OpenClipboard 失败快速返回 + 短重试，
/// 不会像 OLE 那样在属主进程卡死时无限阻塞、进而锁死全系统剪贴板。
///
/// 自身写入用「剪贴板序列号」标记（<see cref="IsOwnSequence"/>），捕获侧据此忽略回写，
/// 不再用 2.5 秒盲窗抑制——那会把用户在窗口内的真实复制一起丢掉。
/// </summary>
public sealed class PasteService
{
    private volatile bool _isSelfWriting;

    /// <summary>最近一次自身成功写入后的剪贴板序列号。</summary>
    private int _ownSequence;

    private IntPtr _lastTargetWindow = IntPtr.Zero;

    /// <summary>纯文本粘贴路径下、粘贴完成后需要还原的文件列表。</summary>
    private string[]? _filesToRestore;

    /// <summary>是否正在写剪贴板（捕获侧据此跳过同步竞态窗口）。</summary>
    public bool IsSelfPasting => _isSelfWriting;

    /// <summary>粘贴/复制失败原因（剪贴板被占用、目标窗口未激活、内容缺失等），供托盘气泡提示。</summary>
    public event Action<string>? PasteFailed;

    /// <summary>记录唤起 QuickClip 之前的前台窗口，作为粘贴目标。</summary>
    public void RememberTargetWindow()
    {
        _lastTargetWindow = NativeMethods.GetForegroundWindow();
    }

    /// <summary>该序列号是否属于本服务最近一次成功写入（用于忽略自身回写）。</summary>
    public bool IsOwnSequence(uint sequence) =>
        sequence != 0 && (uint)Volatile.Read(ref _ownSequence) == sequence;

    // ---------- 粘贴（后台回填剪贴板后模拟 Ctrl+V，异常仅记录日志） ----------

    public void PasteText(string? text, bool plainOnly = false, string? html = null, string? rtf = null)
    {
        DebugLog.Log($"粘贴文本: plainOnly={plainOnly}, 长度={(text?.Length ?? 0)}, html={(html != null)}, rtf={(rtf != null)}");
        _ = RunPasteAsync(() => CopyTextCore(text, plainOnly, html, rtf));
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
    /// 将系统剪贴板中的内容以纯文本形式粘贴到前台窗口（全局 Ctrl+Shift+V）。
    /// 若剪贴板中为复制的文件列表，则提取各文件名（换行分隔）作为纯文本粘贴，
    /// 粘贴完成后还原原来的文件列表，避免用户丢失剪贴板里的文件对象。
    /// 剪贴板无对应内容时不做任何操作并给出提示。
    /// </summary>
    public void PastePlainTextFromClipboard()
    {
        _ = RunPasteAsync(CopyPlainTextFromClipboardCore);
    }

    // ---------- 仅覆盖系统剪贴板（Ctrl+C 路径，返回 Task 便于调用方按序刷新） ----------

    /// <summary>
    /// 写入文本；plainOnly=false 时若条目存有 CF_HTML / CF_RTF 原文，会一并写回以保留富文本格式。
    /// </summary>
    public Task<bool> CopyTextAsync(
        string? text,
        bool plainOnly = false,
        string? html = null,
        string? rtf = null) =>
        CopyCoreAsync(() => CopyTextCore(text, plainOnly, html, rtf));

    public Task<bool> CopyImageAsync(string? previewPath) =>
        CopyCoreAsync(() => CopyImageCore(previewPath));

    public Task<bool> CopyFilesAsync(string[]? files) =>
        CopyCoreAsync(() => CopyFilesCore(files));

    // ---------- 同步旧入口（内部转为后台执行，保持调用方签名不变） ----------

    public void CopyText(string? text, bool plainOnly = false, string? html = null, string? rtf = null) =>
        _ = CopyTextAsync(text, plainOnly, html, rtf);

    public void CopyImage(string? previewPath) =>
        _ = CopyImageAsync(previewPath);

    public void CopyFiles(string[]? files) =>
        _ = CopyFilesAsync(files);

    /// <summary>
    /// 等待目标窗口成为前台后再发送 Ctrl+V；固定 35ms 缓冲在重型聊天软件下偶发不足，轮询兜底。
    /// 目标窗口最终仍未成为前台时**不再盲发** Ctrl+V（否则会粘到别的窗口），返回 false。
    /// </summary>
    private bool SimulatePaste()
    {
        IntPtr target = _lastTargetWindow;
        if (target != IntPtr.Zero && NativeMethods.IsWindow(target))
        {
            NativeMethods.SetForegroundWindow(target);

            // 前台切换通常瞬时完成（QuickClip 已隐藏），首次检查即命中、无额外延迟；
            // 仅当 SetForegroundWindow 被前台锁延迟生效时轮询等待，避免 Ctrl+V 落到错误窗口。
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
            while (DateTime.UtcNow < deadline)
            {
                if (NativeMethods.GetForegroundWindow() == target)
                {
                    break;
                }

                System.Threading.Thread.Sleep(20);
            }

            if (NativeMethods.GetForegroundWindow() != target)
            {
                DebugLog.Log($"粘贴取消：目标窗口 {target} 未成为前台");
                return false;
            }
        }

        NativeMethods.SendCtrlV();
        return true;
    }

    /// <summary>在后台 STA 线程回填剪贴板，完成后模拟粘贴；异常仅记录日志，不影响主流程。</summary>
    private async Task RunPasteAsync(Func<bool> setter)
    {
        try
        {
            bool written = await CopyCoreAsync(setter);
            if (!written)
            {
                // 内容缺失/剪贴板被占用：具体原因已由各 Core 方法上报
                return;
            }

            // 关键：剪贴板回填完成后，留出微小的系统前台焦点稳定缓冲（约 35ms），
            // 确保 QuickClip 隐藏后目标第三方窗口已完成 WM_ACTIVATE 获得键盘焦点，再执行 SendInput
            await Task.Delay(35);
            if (!SimulatePaste())
            {
                ReportFailure("目标窗口未激活，已取消粘贴");
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("粘贴失败", ex);
            ReportFailure("粘贴失败：" + ex.Message);
        }
        finally
        {
            string[]? restore = Interlocked.Exchange(ref _filesToRestore, null);
            if (restore != null)
            {
                // 等目标程序读完剪贴板，再把原来的文件列表放回去
                await Task.Delay(180);
                if (NativeClipboard.TrySetFiles(restore))
                {
                    Volatile.Write(ref _ownSequence, (int)NativeClipboard.CurrentSequence);
                    DebugLog.Log("已还原剪贴板中的文件列表");
                }
            }
        }
    }

    /// <summary>在独立 STA 线程上执行剪贴板写入，避免剪贴板被占用时阻塞 UI 线程。</summary>
    private Task<bool> CopyCoreAsync(Func<bool> setter) => StaTask.Run(() => SetClipboard(setter));

    /// <summary>将文本覆盖到系统剪贴板（需在 STA 线程调用）；非纯文本模式下附带富文本格式。</summary>
    private bool CopyTextCore(string? text, bool plainOnly, string? html = null, string? rtf = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            ReportFailure("该条目没有可复制的文本");
            return false;
        }

        if (!NativeClipboard.TrySetText(text, plainOnly, html, rtf))
        {
            ReportFailure("剪贴板被其他程序占用，操作失败");
            return false;
        }

        return true;
    }

    /// <summary>将图片覆盖到系统剪贴板（需在 STA 线程调用）。</summary>
    private bool CopyImageCore(string? previewPath)
    {
        if (string.IsNullOrEmpty(previewPath) || !File.Exists(previewPath))
        {
            ReportFailure("图片文件已不存在，无法复制或粘贴");
            return false;
        }

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
            ReportFailure("剪贴板被其他程序占用，操作失败");
            return false;
        }

        return true;
    }

    /// <summary>将文件列表覆盖到系统剪贴板（需在 STA 线程调用）。</summary>
    private bool CopyFilesCore(string[]? files)
    {
        if (files is not { Length: > 0 })
        {
            ReportFailure("该条目没有可粘贴的文件");
            return false;
        }

        // 历史里的文件可能已被移动/删除；全部失效时直接提示，避免粘贴出「空的文件操作」
        int missing = files.Count(f => !File.Exists(f));
        if (missing == files.Length)
        {
            ReportFailure("文件已不存在（可能被移动或删除），可用 Shift+Enter 粘贴路径文本");
            return false;
        }

        if (missing > 0)
        {
            DebugLog.Log($"粘贴文件：{missing}/{files.Length} 个路径已不存在，按原列表写入剪贴板");
        }

        if (!NativeClipboard.TrySetFiles(files))
        {
            ReportFailure("剪贴板被其他程序占用，操作失败");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 读取系统剪贴板并以纯文本写回（全局 Ctrl+Shift+V）。
    /// 文件列表会被临时替换成文件名文本，随后在粘贴完成后还原（见 RunPasteAsync 的 finally）。
    /// </summary>
    private bool CopyPlainTextFromClipboardCore()
    {
        string[]? files = NativeClipboard.TryGetFiles();
        if (files is { Length: > 0 })
        {
            var names = files.Select(f =>
            {
                string? name = Path.GetFileName(f);
                return string.IsNullOrEmpty(name) ? f : name;
            });
            string fileNamesText = string.Join(Environment.NewLine, names);
            if (string.IsNullOrEmpty(fileNamesText))
            {
                ReportFailure("剪贴板中的文件列表为空");
                return false;
            }

            if (!NativeClipboard.TrySetText(fileNamesText, plainOnly: true))
            {
                ReportFailure("剪贴板被其他程序占用，操作失败");
                return false;
            }

            _filesToRestore = files;
            return true;
        }

        string? text = NativeClipboard.TryGetText();
        if (string.IsNullOrEmpty(text))
        {
            ReportFailure("剪贴板中没有文本，无法以纯文本粘贴");
            return false;
        }

        if (!NativeClipboard.TrySetText(text, plainOnly: true))
        {
            ReportFailure("剪贴板被其他程序占用，操作失败");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 写入剪贴板并记录自身序列号：捕获侧用序列号判断「是不是自己写的」，
    /// 因此这里不再需要长时间抑制窗口。
    /// </summary>
    private bool SetClipboard(Func<bool> setter)
    {
        _isSelfWriting = true;
        try
        {
            bool written = setter();
            if (written)
            {
                Volatile.Write(ref _ownSequence, (int)NativeClipboard.CurrentSequence);
            }

            return written;
        }
        finally
        {
            _isSelfWriting = false;
        }
    }

    private void ReportFailure(string message)
    {
        DebugLog.Log("粘贴服务：" + message);
        try
        {
            PasteFailed?.Invoke(message);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("粘贴失败回调异常", ex);
        }
    }
}
