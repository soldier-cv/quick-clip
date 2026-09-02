using System.Runtime.InteropServices;

namespace QuickClip.Native;

/// <summary>QuickClip 所需的 Win32 原生互操作定义。</summary>
internal static class NativeMethods
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const uint WM_HOTKEY = 0x0312;

    public const int VK_V = 0x56;
    public const int VK_SHIFT = 0x10;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_ESCAPE = 0x1B;

    /// <summary>RegisterHotKey 修饰键标志。</summary>
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    /// <summary>低级钩子事件中的 LLKHF_INJECTED 标志（SendInput 注入的按键）。</summary>
    public const uint LLKHF_INJECTED = 0x00000010;

    /// <summary>自身 SendInput 打在 dwExtraInfo 上的标记，用来和 RustDesk 等远程注入区分。</summary>
    public static readonly IntPtr InjectExtraInfo = new(0x51434C50);

    public const uint KEYEVENTF_KEYUP = 0x0002;

    public const uint INPUT_KEYBOARD = 1;

    /// <summary>SetWindowPos 的 z-order 目标：置顶 / 取消置顶。</summary>
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    /// <summary>SetWindowPos 标志：不移动、不改变大小、不激活、显示窗口。</summary>
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>小图标推荐宽度（托盘图标槽，含 DPI）。</summary>
    public const int SM_CXSMICON = 49;

    // ---------- 剪贴板格式 ----------

    /// <summary>ANSI 文本格式。</summary>
    public const uint CF_TEXT = 1;

    /// <summary>设备无关位图（BITMAPINFOHEADER + 像素数据）。</summary>
    public const uint CF_DIB = 8;

    /// <summary>Unicode 文本格式。</summary>
    public const uint CF_UNICODETEXT = 13;

    /// <summary>文件拖放列表（DROPFILES 结构 + 路径）。</summary>
    public const uint CF_HDROP = 15;

    /// <summary>BITMAPV5HEADER 版设备无关位图。</summary>
    public const uint CF_DIBV5 = 17;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    /// <summary>当前打开剪贴板的窗口句柄（无人打开时为 0）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern UIntPtr GlobalSize(IntPtr hMem);

    /// <summary>系统 ANSI 代码页（用于 CF_TEXT 编码）。</summary>
    [DllImport("kernel32.dll")]
    public static extern uint GetACP();

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    /// <summary>BITMAPV5HEADER（124 字节）：写剪贴板 CF_DIBV5 用，兼容聊天软件优先读取 DIBV5 的场景。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPV5HEADER
    {
        public uint bV5Size;
        public int bV5Width;
        public int bV5Height;
        public ushort bV5Planes;
        public ushort bV5BitCount;
        public uint bV5Compression;
        public uint bV5SizeImage;
        public int bV5XPelsPerMeter;
        public int bV5YPelsPerMeter;
        public uint bV5ClrUsed;
        public uint bV5ClrImportant;
        public uint bV5RedMask;
        public uint bV5GreenMask;
        public uint bV5BlueMask;
        public uint bV5AlphaMask;
        public uint bV5CSType;
        public int bV5Endpoints0;
        public int bV5Endpoints1;
        public int bV5Endpoints2;
        public int bV5Endpoints3;
        public int bV5Endpoints4;
        public int bV5Endpoints5;
        public int bV5Endpoints6;
        public int bV5Endpoints7;
        public int bV5Endpoints8;
        public uint bV5GammaRed;
        public uint bV5GammaGreen;
        public uint bV5GammaBlue;
        public uint bV5Intent;
        public uint bV5ProfileData;
        public uint bV5ProfileSize;
        public uint bV5Reserved;
    }

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    /// <summary>低级键盘钩子回调委托。</summary>
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    public const int SW_SHOW = 5;

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowApi(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// 热键唤起时进程往往没有“最近一次真实输入”，SetForegroundWindow 会被前台锁拒绝。
    /// 先把前台线程输入队列附到本线程，再置前；失败则返回 false，由调用方临时 TOPMOST。
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr foreground = GetForegroundWindow();
        if (foreground == hwnd)
        {
            return true;
        }

        ShowWindowApi(hwnd, SW_SHOW);
        BringWindowToTop(hwnd);

        uint fgThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = false;
        if (fgThread != 0 && fgThread != thisThread)
        {
            attached = AttachThreadInput(fgThread, thisThread, true);
        }

        bool ok = SetForegroundWindow(hwnd);
        if (attached)
        {
            AttachThreadInput(fgThread, thisThread, false);
        }

        return ok || GetForegroundWindow() == hwnd;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    public const int WM_QUERYENDSESSION = 0x0011;
    public const uint WM_QUIT = 0x0012;
    public const int WM_ENDSESSION = 0x0016;

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>指定虚拟键当前是否处于按下状态。</summary>
    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// 模拟一次 Ctrl+V 击键；若 Ctrl/Shift/Alt/Win 正被物理按住（如热键触发时），
    /// 先合成释放这些修饰键，避免目标程序把粘贴识别成 Shift+Ctrl+V 等其他组合。
    /// 不主动“恢复”按下，因为用户随后松开物理按键时系统会自然收到弹起事件。
    /// </summary>
    public static void SendCtrlV()
    {
        bool ctrl = IsKeyDown(VK_CONTROL);
        bool shift = IsKeyDown(VK_SHIFT);
        bool alt = IsKeyDown(VK_MENU);
        bool winLeft = IsKeyDown(VK_LWIN);
        bool winRight = IsKeyDown(VK_RWIN);

        var inputs = new INPUT[4 + (ctrl ? 1 : 0) + (shift ? 1 : 0) + (alt ? 1 : 0) + (winLeft ? 1 : 0) + (winRight ? 1 : 0)];
        int i = 0;
        if (ctrl) inputs[i++] = KeyInput(VK_CONTROL, KEYEVENTF_KEYUP);
        if (shift) inputs[i++] = KeyInput(VK_SHIFT, KEYEVENTF_KEYUP);
        if (alt) inputs[i++] = KeyInput(VK_MENU, KEYEVENTF_KEYUP);
        if (winLeft) inputs[i++] = KeyInput(VK_LWIN, KEYEVENTF_KEYUP);
        if (winRight) inputs[i++] = KeyInput(VK_RWIN, KEYEVENTF_KEYUP);
        inputs[i++] = KeyInput(VK_CONTROL, 0);
        inputs[i++] = KeyInput(VK_V, 0);
        inputs[i++] = KeyInput(VK_V, KEYEVENTF_KEYUP);
        inputs[i++] = KeyInput(VK_CONTROL, KEYEVENTF_KEYUP);
        uint sent = SendInput((uint)i, inputs, Marshal.SizeOf<INPUT>());
        if (sent != i)
        {
            Services.DebugLog.Log($"SendCtrlV 注入失败: sent={sent}/{i} LastError={Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>
    /// 注入完整的 Win 键按下/弹起：用于“单独按 Win”时重放，恢复系统打开开始菜单的行为
    /// （钩子为抑制误弹开始菜单吞掉了物理 Win 按下，需在此补发一次）。
    /// </summary>
    public static void TapWinKey()
    {
        SendInputs(new[]
        {
            KeyInput(VK_LWIN, 0),
            KeyInput(VK_LWIN, KEYEVENTF_KEYUP)
        });
    }

    /// <summary>注入一次 Win 键弹起：平衡重放时注入的 Win 按下，避免系统键状态残留。</summary>
    public static void InjectWinKeyUp()
    {
        SendInputs(new[] { KeyInput(VK_LWIN, KEYEVENTF_KEYUP) });
    }

    /// <summary>
    /// 注入 “Win + 指定键” 的和弦按下：用于重放被吞掉的真实系统快捷键（Win+E 等），
    /// 让系统识别到完整和弦，不会把 Win 判定为单独按下而弹出开始菜单。
    /// </summary>
    public static void InjectWinChord(uint otherVk)
    {
        SendInputs(new[]
        {
            KeyInput(VK_LWIN, 0),
            KeyInput((int)otherVk, 0)
        });
    }

    /// <summary>注入一次 ESC 按下/弹起：用于关闭系统剪贴板历史窗口（SendInput 不受 UIPI 权限隔离影响）。</summary>
    public static void SendEscape()
    {
        SendInputs(new[]
        {
            KeyInput(VK_ESCAPE, 0),
            KeyInput(VK_ESCAPE, KEYEVENTF_KEYUP)
        });
    }

    /// <summary>批量注入输入事件并记录失败信息。</summary>
    private static void SendInputs(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            Services.DebugLog.Log($"SendInput 注入失败: sent={sent}/{inputs.Length} LastError={Marshal.GetLastWin32Error()}");
        }
    }

    public static bool IsSelfInjected(in KBDLLHOOKSTRUCT hook) =>
        (hook.flags & LLKHF_INJECTED) != 0 && hook.dwExtraInfo == InjectExtraInfo;

    private static INPUT KeyInput(int vk, uint flags)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    dwFlags = flags,
                    dwExtraInfo = InjectExtraInfo
                }
            }
        };
    }

    public const int HWND_BROADCAST = 0xffff;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);
}

