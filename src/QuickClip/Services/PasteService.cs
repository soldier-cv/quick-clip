using System.Drawing;
using System.IO;
using System.Windows.Threading;
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

    /// <summary>创建时所在 UI 调度器：置顶粘贴必须在 UI 线程转交前台，后台 STA 线程会被前台锁拒绝。</summary>
    private readonly Dispatcher? _uiDispatcher = Dispatcher.FromThread(Thread.CurrentThread);

    /// <summary>纯文本粘贴路径下、粘贴完成后需要还原的文件列表。</summary>
    private string[]? _filesToRestore;

    /// <summary>是否正在写剪贴板（捕获侧据此跳过同步竞态窗口）。</summary>
    public bool IsSelfPasting => _isSelfWriting;

    /// <summary>粘贴/复制失败原因（剪贴板被占用、目标窗口未激活、内容缺失等），供托盘气泡提示。</summary>
    public event Action<string>? PasteFailed;

    /// <summary>记录唤起 QuickClip 之前的前台窗口，作为粘贴目标。自动过滤自身进程、任务栏、IME 等不可输入窗口。</summary>
    public void RememberTargetWindow(IntPtr candidate = default)
    {
        uint ownPid = (uint)Environment.ProcessId;

        // 显式候选（来自 WM_ACTIVATE / 失焦事件等）：优先采用；候选无效（自身浮层、任务栏、IME）
        // 时保留既有目标，避免被瞬时窗口清空。
        if (candidate != IntPtr.Zero)
        {
            IntPtr hwnd = candidate;
            IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            if (root != IntPtr.Zero)
            {
                hwnd = root;
            }

            if (NativeMethods.IsEligiblePasteTarget(hwnd, ownPid))
            {
                SetTargetWindow(hwnd);
            }

            return;
        }

        // 无候选 = 唤起窗口前主动记录：必须以「当前真实前台」为准，绝不能沿用过期目标。
        // 否则用户切回飞书后按 Win+V，仍会把内容贴到上一次失焦时的浏览器窗口。
        // 注意：WPF 激活时 WM_ACTIVATE(WA_ACTIVE) 的 lParam 恒为 0，无法作为第二道兜底，
        // 所以这里必须强制刷新，而不能在既有目标仍有效时提前返回。
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (NativeMethods.IsEligiblePasteTarget(fg, ownPid))
        {
            SetTargetWindow(fg);
        }
    }

    private void SetTargetWindow(IntPtr hwnd)
    {
        if (hwnd == _lastTargetWindow)
        {
            return;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        _lastTargetWindow = hwnd;
        DebugLog.Log($"记录粘贴目标窗口: {hwnd}, pid={pid}, class={NativeMethods.GetWindowClassName(hwnd)}");
    }

    /// <summary>当前已记录的粘贴目标是否仍有效。</summary>
    public bool HasValidTargetWindow()
    {
        IntPtr target = _lastTargetWindow;
        return NativeMethods.IsEligiblePasteTarget(target, (uint)Environment.ProcessId);
    }

    /// <summary>该序列号是否属于本服务最近一次成功写入（用于忽略自身回写）。</summary>
    public bool IsOwnSequence(uint sequence) =>
        sequence != 0 && (uint)Volatile.Read(ref _ownSequence) == sequence;

    /// <summary>记录当前剪贴板序列号为自身写入（清空剪贴板后避免误入库一条空记录）。</summary>
    public void MarkOwnSequence()
    {
        Volatile.Write(ref _ownSequence, (int)NativeClipboard.CurrentSequence);
    }

    // ---------- 粘贴（后台回填剪贴板后模拟 Ctrl+V，异常仅记录日志） ----------

    public void PasteText(string? text, bool plainOnly = false, string? html = null, string? rtf = null) =>
        _ = PasteTextAsync(text, plainOnly, html, rtf, stayOnForeground: false);

    public void PasteText(string? text, bool plainOnly, string? html, string? rtf, bool stayOnForeground) =>
        _ = PasteTextAsync(text, plainOnly, html, rtf, stayOnForeground);

    public Task PasteTextAsync(string? text, bool plainOnly, string? html, string? rtf, bool stayOnForeground)
    {
        DebugLog.Log($"粘贴文本: plainOnly={plainOnly}, stayFg={stayOnForeground}, 长度={(text?.Length ?? 0)}, html={(html != null)}, rtf={(rtf != null)}");
        return RunPasteAsync(() => CopyTextCore(text, plainOnly, html, rtf), stayOnForeground);
    }

    public void PasteImage(string? previewPath, bool stayOnForeground = false) =>
        _ = PasteImageAsync(previewPath, stayOnForeground);

    public Task PasteImageAsync(string? previewPath, bool stayOnForeground = false) =>
        RunPasteAsync(() => CopyImageCore(previewPath), stayOnForeground);

    public void PasteFiles(string[]? files, bool stayOnForeground = false) =>
        _ = PasteFilesAsync(files, stayOnForeground);

    public Task PasteFilesAsync(string[]? files, bool stayOnForeground = false) =>
        RunPasteAsync(() => CopyFilesCore(files), stayOnForeground);

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
    /// 由 UI 线程主动将前台焦点转交给目标窗口（尤其在置顶且面板保持打开时）。
    /// 当前台窗口属主线程主动转交时，Windows 前台锁无条件放行。
    /// </summary>
    public void ActivateTargetWindow()
    {
        IntPtr target = ResolveTargetWindow();
        if (target == IntPtr.Zero)
        {
            return;
        }

        ForceForegroundOnUi(target);
    }

    /// <summary>
    /// 等待目标窗口成为前台后再发送 Ctrl+V。
    /// 支持同进程多 HWND 容错（如浏览器、现代聊天软件），置顶模式下协同 UI 线程焦点切换。
    /// </summary>
    private bool SimulatePaste(bool stayOnForeground)
    {
        uint ownPid = (uint)Environment.ProcessId;
        IntPtr currentFg = NativeMethods.GetForegroundWindow();
        if (stayOnForeground && NativeMethods.IsEligiblePasteTarget(currentFg, ownPid))
        {
            NativeMethods.SendCtrlV();
            return true;
        }

        IntPtr target = ResolveTargetWindow();
        if (target == IntPtr.Zero)
        {
            DebugLog.Log("粘贴取消：没有有效的外部目标窗口");
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(target, out uint targetPid);
        ForceForegroundOnUi(target);

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(450);
        bool foregroundReady = false;
        while (DateTime.UtcNow < deadline)
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == target)
            {
                foregroundReady = true;
                break;
            }

            if (fg != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(fg, out uint currentPid);
                if (currentPid == targetPid && currentPid != (uint)Environment.ProcessId)
                {
                    foregroundReady = true;
                    break;
                }
            }

            ForceForegroundOnUi(target);
            System.Threading.Thread.Sleep(20);
        }

        if (!foregroundReady)
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
            DebugLog.Log($"粘贴取消：目标窗口未能成为前台 target={target} fg={fg} fgPid={fgPid}");
            return false;
        }

        System.Threading.Thread.Sleep(40);

        var mouseDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(150);
        while (DateTime.UtcNow < mouseDeadline)
        {
            if (!NativeMethods.IsKeyDown(NativeMethods.VK_LBUTTON))
            {
                break;
            }
            System.Threading.Thread.Sleep(10);
        }

        NativeMethods.SendCtrlV();
        return true;
    }

    private IntPtr ResolveTargetWindow()
    {
        uint ownPid = (uint)Environment.ProcessId;
        IntPtr target = _lastTargetWindow;
        if (NativeMethods.IsEligiblePasteTarget(target, ownPid))
        {
            return target;
        }

        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (NativeMethods.IsEligiblePasteTarget(fg, ownPid))
        {
            _lastTargetWindow = fg;
            return fg;
        }

        return IntPtr.Zero;
    }

    private bool ForceForegroundOnUi(IntPtr target)
    {
        if (_uiDispatcher == null || _uiDispatcher.CheckAccess())
        {
            return NativeMethods.ForceForeground(target);
        }

        try
        {
            return _uiDispatcher.Invoke(() => NativeMethods.ForceForeground(target), DispatcherPriority.Send);
        }
        catch (Exception ex)
        {
            DebugLog.LogException("UI 线程激活目标窗口失败", ex);
            return NativeMethods.ForceForeground(target);
        }
    }

    /// <summary>在后台 STA 线程回填剪贴板，完成后模拟粘贴；异常仅记录日志，不影响主流程。</summary>
    private async Task RunPasteAsync(Func<bool> setter, bool stayOnForeground = false)
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
            await Task.Delay(stayOnForeground ? 15 : 35);
            if (!SimulatePaste(stayOnForeground))
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
