using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using QuickClip.Models;
using QuickClip.Native;

namespace QuickClip.Services;

/// <summary>
/// 全局热键服务，两层接管策略：
/// 1. RegisterHotKey + 隐藏消息窗口（WM_HOTKEY）：接管可配置的“纯文本粘贴”热键（默认 Ctrl+Shift+V）；
/// 2. WH_KEYBOARD_LL 低级钩子：Win+V 因系统剪贴板历史已占用无法用 RegisterHotKey 抢占，
///    改由钩子拦截：吞掉 Win 键按下抑制开始菜单误弹，V 进入和弦判定后触发面板切换；
///    若用户实际按下的是其它 Win 快捷键（Win+E 等），则重放注入完整和弦保留系统行为。
///    RegisterHotKey 注册失败的组合同样回退到钩子。
/// 
/// @author xudong.hua,gemini
/// @since 2026-08-19 16:00 星期三
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyIdPlainPaste = 0x5101;
    private const int HotkeyIdWinV = 0x5102;

    private readonly object _lock = new();
    private readonly NativeMethods.LowLevelKeyboardProc _proc;

    private Dispatcher? _uiDispatcher;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd = IntPtr.Zero;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookId = IntPtr.Zero;

    // 各热键是否已由 RegisterHotKey 接管（否则钩子回退）
    private volatile bool _winVRegistered;
    private volatile bool _plainPasteRegistered;

    /// <summary>
    /// Win+V 是否已成功由系统级 RegisterHotKey 独占接管（若为 false 则当前由低级键盘钩子接管）。
    /// </summary>
    public bool IsWinVRegistered => _winVRegistered;

    // Win 键状态机（钩子线程）：吞掉 Win 按下抑制开始菜单，必要时重放保留系统快捷键
    private volatile bool _winKeyDown;
    private volatile bool _winKeySwallowed;
    private volatile bool _winKeyReplayed;
    private volatile bool _winChordHandled;

    // 钩子读取的当前配置快照
    private volatile HotkeyBinding _plainPasteBinding = HotkeyBinding.PlainPasteDefault;
    private volatile bool _plainPasteEnabled = true;

    // 钩子按下标记：过滤系统按键自动重复，避免长按 Win+V / 纯文本组合导致反复触发
    private volatile bool _winVKeyDown;
    private volatile bool _plainPasteKeyDown;


    /// <summary>Win+V 被按下时触发（在 UI 线程回调），用于唤起/隐藏面板。</summary>
    public event Action? ToggleRequested;

    /// <summary>全局纯文本粘贴热键被按下时触发（在 UI 线程回调）。</summary>
    public event Action? PastePlainRequested;

    /// <summary>钩子安装失败时触发（提示用户可能无法接管 Win+V）。</summary>
    public event Action<string>? HotkeyInstallFailed;

    public HotkeyService()
    {
        // 保持委托引用，防止被 GC 回收
        _proc = HookCallback;
    }

    /// <summary>启动服务：创建隐藏消息窗口并注册热键，随后启动低级钩子线程。</summary>
    public void Start(Dispatcher uiDispatcher, SettingsService settings)
    {
        lock (_lock)
        {
            _uiDispatcher = uiDispatcher;
            _plainPasteBinding = settings.PlainPasteHotkey;
            _plainPasteEnabled = settings.PlainPasteEnabled;

            // 隐藏消息窗口必须在 UI 线程（STA）创建，WM_HOTKEY 才会投递到 UI 线程
            uiDispatcher.Invoke(() =>
            {
                if (_hwndSource != null)
                {
                    return;
                }

                var parameters = new HwndSourceParameters("QuickClipHotkeyWindow")
                {
                    Width = 0,
                    Height = 0,
                    WindowStyle = 0,
                    HwndSourceHook = WndProc
                };
                _hwndSource = new HwndSource(parameters);
                _hwnd = _hwndSource.Handle;
                DebugLog.Log($"热键消息窗口已创建: {_hwnd}");
            });

            ApplyHotkeysCore();

            if (_hookThread is { IsAlive: true })
            {
                return;
            }

            _hookThread = new Thread(HookThreadMain)
            {
                Name = "QuickClip.HotkeyHook",
                IsBackground = true
            };
            _hookThread.Start();
        }
    }

    /// <summary>设置变更后重新应用热键注册（保留窗口与钩子线程）。</summary>
    public void ApplyHotkeys(SettingsService settings)
    {
        lock (_lock)
        {
            _plainPasteBinding = settings.PlainPasteHotkey;
            _plainPasteEnabled = settings.PlainPasteEnabled;
            ApplyHotkeysCore();
        }
    }

    /// <summary>重新尝试应用热键注册（供系统剪贴板冲突配置变更后即时刷新）。</summary>
    public void RefreshHotkeys()
    {
        lock (_lock)
        {
            ApplyHotkeysCore();
        }
    }

    private void ApplyHotkeysCore()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        // 先卸载旧注册，避免残留
        if (_winVRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyIdWinV);
            _winVRegistered = false;
        }

        if (_plainPasteRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyIdPlainPaste);
            _plainPasteRegistered = false;
        }

        // Win+V：系统开启剪切板历史时会注册失败（错误 1409），此时由钩子接管
        _winVRegistered = TryRegisterHotKey(HotkeyBinding.WinV, HotkeyIdWinV, "Win+V");
        if (!_winVRegistered)
        {
            DebugLog.Log("Win+V 已被系统占用，改用低级钩子接管");
        }

        // 可配置纯文本粘贴热键：注册失败（组合被占用）时回退钩子
        _plainPasteRegistered = false;
        if (_plainPasteEnabled && _plainPasteBinding.IsValid)
        {
            _plainPasteRegistered = TryRegisterHotKey(_plainPasteBinding, HotkeyIdPlainPaste, "纯文本粘贴");
            if (!_plainPasteRegistered)
            {
                DebugLog.Log($"纯文本粘贴组合 [{_plainPasteBinding}] 注册失败，改用低级钩子接管");
            }
        }
        else
        {
            DebugLog.Log("纯文本粘贴热键未启用");
        }
    }

    private bool TryRegisterHotKey(HotkeyBinding binding, int id, string label)
    {
        uint mods = binding.ToModifierFlags() | NativeMethods.MOD_NOREPEAT;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(binding.Key);
        bool ok = NativeMethods.RegisterHotKey(_hwnd, id, mods, vk);
        DebugLog.Log($"注册热键 {label} [{binding}] => {ok} (LastError={Marshal.GetLastWin32Error()})");
        return ok;
    }

    /// <summary>WM_HOTKEY 处理：RegisterHotKey 接管的热键在这里触发。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyIdPlainPaste)
            {
                handled = true;
                DebugLog.Log("收到 WM_HOTKEY：纯文本粘贴");
                PastePlainRequested?.Invoke();
            }
            else if (id == HotkeyIdWinV)
            {
                handled = true;
                DebugLog.Log("收到 WM_HOTKEY：Win+V 切换");
                ToggleRequested?.Invoke();
            }
        }

        return IntPtr.Zero;
    }

    private void HookThreadMain()
    {
        _hookThreadId = NativeMethods.GetCurrentThreadId();
        DebugLog.Log("热键钩子线程已启动");

        try
        {
            string moduleName = Process.GetCurrentProcess().MainModule?.ModuleName ?? string.Empty;
            _hookId = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _proc,
                NativeMethods.GetModuleHandle(moduleName),
                0);
            DebugLog.Log($"安装低级键盘钩子: HookId={_hookId}, LastError={Marshal.GetLastWin32Error()}, Module={moduleName}");
        }
        catch (Exception ex)
        {
            DebugLog.LogException("键盘钩子安装失败", ex);
        }

        if (_hookId == IntPtr.Zero)
        {
            string error = $"键盘钩子安装失败 (Win32 错误 {Marshal.GetLastWin32Error()})，Win+V 可能无法接管";
            DebugLog.Log(error);
            _uiDispatcher?.BeginInvoke(() => HotkeyInstallFailed?.Invoke(error));
        }

        // 标准 Win32 消息循环：低级钩子回调依赖 GetMessage 检索消息
        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        DebugLog.Log("热键钩子线程消息泵退出");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            uint msg = (uint)wParam.ToInt64();
            if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN or
                NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                var hook = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                bool isInjected = (hook.flags & NativeMethods.LLKHF_INJECTED) != 0;
                bool isKeyDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                bool isWinKey = hook.vkCode is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN;

                // 自身注入的事件（重放 Win 和弦）直接放行，避免递归处理
                if (isInjected)
                {
                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                // ---------- Win 键状态机：吞掉 Win 按下抑制开始菜单，必要时重放保留系统快捷键 ----------
                if (isWinKey)
                {
                    if (isKeyDown)
                    {
                        if (!_winKeyDown)
                        {
                            _winKeyDown = true;
                            _winKeySwallowed = true;
                            _winKeyReplayed = false;
                            _winChordHandled = false;
                            DebugLog.LogDetail("捕获 Win 键按下（已吞掉，等待和弦判定）");
                        }

                        return new IntPtr(1);
                    }

                    bool wasDown = _winKeyDown;
                    bool replayed = _winKeyReplayed;
                    bool chordHandled = _winChordHandled;
                    _winKeyDown = false;
                    _winKeySwallowed = false;
                    _winKeyReplayed = false;
                    _winChordHandled = false;

                    if (wasDown)
                    {
                        if (replayed)
                        {
                            // 已重放 Win 按下（Win+其它键）：注入对应弹起，保持系统键状态平衡
                            NativeMethods.InjectWinKeyUp();
                        }
                        else if (!chordHandled)
                        {
                            // 单独按 Win：重放完整按下/弹起，恢复系统“打开开始菜单”行为
                            DebugLog.LogDetail("Win 单独按下，重放以恢复开始菜单");
                            NativeMethods.TapWinKey();
                        }

                        // chordHandled（Win+V）无需注入任何事件，吞掉物理弹起即可
                        return new IntPtr(1);
                    }

                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                // ---------- Win 已被吞掉、正在等待和弦判定 ----------
                if (_winKeySwallowed && !_winKeyReplayed && _winKeyDown)
                {
                    if (hook.vkCode == NativeMethods.VK_V)
                    {
                        if (isKeyDown)
                        {
                            if (!_winVKeyDown)
                            {
                                _winVKeyDown = true;
                                _winChordHandled = true;
                                DebugLog.Log("捕获 Win+V（钩子接管，已抑制开始菜单）");
                                _uiDispatcher?.BeginInvoke(() => ToggleRequested?.Invoke());
                            }

                            return new IntPtr(1);
                        }

                        // V 键弹起：复位按下标记并吞掉事件，避免键状态泄漏给应用
                        _winVKeyDown = false;
                        return new IntPtr(1);
                    }

                    if (isKeyDown)
                    {
                        // Win + 其它键：真实系统快捷键（Win+E 等），重放 Win 与当前键让系统识别和弦
                        _winKeyReplayed = true;
                        DebugLog.LogDetail($"Win+{hook.vkCode} 和弦，重放按键以保留系统快捷键");
                        NativeMethods.InjectWinChord(hook.vkCode);
                        return new IntPtr(1);
                    }

                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                bool winHeld = IsWinHeld();
                bool ctrlDown = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
                bool shiftDown = NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);
                bool altDown = NativeMethods.IsKeyDown(NativeMethods.VK_MENU);

                DebugLog.LogDetail($"HookCallback: vk={hook.vkCode} msg={msg} injected={isInjected} win={winHeld} ctrl={ctrlDown} shift={shiftDown}");

                // ---------- Win+V 兜底拦截（在 RegisterHotKey 失败且处于和弦重放/多键并发状态时） ----------
                if (!_winVRegistered && hook.vkCode == NativeMethods.VK_V && winHeld)
                {
                    if (isKeyDown)
                    {
                        if (!_winVKeyDown)
                        {
                            _winVKeyDown = true;
                            _winChordHandled = true;
                            DebugLog.Log("捕获 Win+V（钩子兜底接管，已拦截系统调用）");
                            _uiDispatcher?.BeginInvoke(() => ToggleRequested?.Invoke());
                        }
                    }
                    else
                    {
                        _winVKeyDown = false;
                    }

                    return new IntPtr(1);
                }

                // V 键但 Win 已松开（普通输入或 Win 先松开）：复位按下标记，允许下一次 Win+V 触发
                if (hook.vkCode == NativeMethods.VK_V && !isKeyDown)
                {
                    _winVKeyDown = false;
                }

                // 可配置纯文本粘贴组合（RegisterHotKey 注册失败时回退）。
                // 注入事件已在前面放行，此处只处理物理按键，避免把自身合成 Ctrl+V 再次当成组合触发形成回环；
                // _plainPasteKeyDown 过滤自动重复，防止长按连续粘贴。
                if (!_plainPasteRegistered && _plainPasteEnabled &&
                    hook.vkCode == (uint)KeyInterop.VirtualKeyFromKey(_plainPasteBinding.Key))
                {
                    if (MatchesPlainPaste(hook.vkCode, ctrlDown, shiftDown, altDown, winHeld))
                    {
                        if (isKeyDown)
                        {
                            if (!_plainPasteKeyDown)
                            {
                                _plainPasteKeyDown = true;
                                DebugLog.Log($"捕获纯文本粘贴组合（钩子回退）: {_plainPasteBinding}");
                                _uiDispatcher?.BeginInvoke(() => PastePlainRequested?.Invoke());
                            }
                        }
                        else
                        {
                            _plainPasteKeyDown = false;
                        }

                        return new IntPtr(1);
                    }

                    // 组合不匹配（如修饰键先松开）：复位标记，允许下一次完整组合触发
                    if (!isKeyDown)
                    {
                        _plainPasteKeyDown = false;
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool IsWinHeld() =>
        NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) || NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);

    private bool MatchesPlainPaste(uint vk, bool ctrl, bool shift, bool alt, bool win)
    {
        var binding = _plainPasteBinding;
        if (vk != (uint)KeyInterop.VirtualKeyFromKey(binding.Key))
        {
            return false;
        }

        bool wantCtrl = (binding.Modifiers & ModifierKeys.Control) != 0;
        bool wantShift = (binding.Modifiers & ModifierKeys.Shift) != 0;
        bool wantAlt = (binding.Modifiers & ModifierKeys.Alt) != 0;
        bool wantWin = (binding.Modifiers & ModifierKeys.Windows) != 0;
        return ctrl == wantCtrl && shift == wantShift && alt == wantAlt && win == wantWin;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DebugLog.Log("开始卸载热键服务");

            if (_hwnd != IntPtr.Zero)
            {
                if (_winVRegistered)
                {
                    NativeMethods.UnregisterHotKey(_hwnd, HotkeyIdWinV);
                    _winVRegistered = false;
                }

                if (_plainPasteRegistered)
                {
                    NativeMethods.UnregisterHotKey(_hwnd, HotkeyIdPlainPaste);
                    _plainPasteRegistered = false;
                }
            }

            _hwndSource?.Dispose();
            _hwndSource = null;
            _hwnd = IntPtr.Zero;

            if (_hookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }

            if (_hookThreadId != 0)
            {
                NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            if (_hookThread is { IsAlive: true })
            {
                _hookThread.Join(2000);
            }

            _hookThread = null;
            DebugLog.Log("热键服务已卸载");
        }
    }
}
