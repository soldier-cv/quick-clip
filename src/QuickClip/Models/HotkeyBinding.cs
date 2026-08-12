using System.Windows.Input;
using QuickClip.Native;

namespace QuickClip.Models;

/// <summary>热键绑定：修饰键 + 主键，用于全局注册与面板内快捷键匹配。</summary>
public sealed record HotkeyBinding(ModifierKeys Modifiers, Key Key)
{
    // ---------- 默认组合 ----------

    /// <summary>默认“全局纯文本粘贴”：Ctrl+Shift+V。</summary>
    public static HotkeyBinding PlainPasteDefault => new(ModifierKeys.Control | ModifierKeys.Shift, Key.V);

    /// <summary>系统保留的唤起组合：Win+V（不可修改）。</summary>
    public static HotkeyBinding WinV => new(ModifierKeys.Windows, Key.V);

    public static HotkeyBinding PasteSelectedDefault => new(ModifierKeys.None, Key.Enter);
    public static HotkeyBinding PasteSelectedPlainDefault => new(ModifierKeys.Shift, Key.Enter);
    public static HotkeyBinding CopySelectedDefault => new(ModifierKeys.Control, Key.C);
    public static HotkeyBinding TogglePinDefault => new(ModifierKeys.Control, Key.P);
    public static HotkeyBinding DeleteSelectedDefault => new(ModifierKeys.None, Key.Delete);
    public static HotkeyBinding HidePanelDefault => new(ModifierKeys.None, Key.Escape);
    public static HotkeyBinding MoveUpDefault => new(ModifierKeys.None, Key.Up);
    public static HotkeyBinding MoveDownDefault => new(ModifierKeys.None, Key.Down);

    /// <summary>是否具备可注册性（至少一个修饰键且主键有效）——仅全局 RegisterHotKey 使用。</summary>
    public bool IsValid => Modifiers != ModifierKeys.None && Key != Key.None;

    /// <summary>主键是否有效（面板快捷键允许无修饰键）。</summary>
    public bool HasKey => Key != Key.None;

    /// <summary>转成 RegisterHotKey 所需的修饰键标志。</summary>
    public uint ToModifierFlags()
    {
        uint flags = 0;
        if ((Modifiers & ModifierKeys.Alt) != 0) flags |= NativeMethods.MOD_ALT;
        if ((Modifiers & ModifierKeys.Control) != 0) flags |= NativeMethods.MOD_CONTROL;
        if ((Modifiers & ModifierKeys.Shift) != 0) flags |= NativeMethods.MOD_SHIFT;
        if ((Modifiers & ModifierKeys.Windows) != 0) flags |= NativeMethods.MOD_WIN;
        return flags;
    }

    /// <summary>与当前按键事件是否精确匹配（修饰键与主键均一致）。</summary>
    public bool Matches(Key key, ModifierKeys modifiers)
    {
        if (!HasKey)
        {
            return false;
        }

        Key pressed = NormalizeKey(key);
        Key expected = NormalizeKey(Key);
        ModifierKeys mods = modifiers &
                            (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);
        return pressed == expected && mods == Modifiers;
    }

    /// <summary>显示文本，例如 “Ctrl + Shift + V”、“↑”、“Esc”。</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(FormatKey(Key));
        return string.Join(" + ", parts);
    }

    public static Key NormalizeKey(Key key) =>
        key is Key.Return or Key.Enter ? Key.Enter : key;

    public static string FormatKey(Key key)
    {
        key = NormalizeKey(key);
        return key switch
        {
            Key.None => "（未设置）",
            Key.Enter => "Enter",
            Key.Escape => "Esc",
            Key.Delete => "Delete",
            Key.Back => "Backspace",
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Up => "↑",
            Key.Down => "↓",
            Key.Left => "←",
            Key.Right => "→",
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            Key.Home => "Home",
            Key.End => "End",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            _ when key is >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            _ when key is >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
            _ => key.ToString()
        };
    }
}

/// <summary>面板内可配置快捷键动作（Win+V / 1~9 除外）。</summary>
public enum PanelHotkeyAction
{
    PasteSelected,
    PasteSelectedPlain,
    CopySelected,
    TogglePin,
    DeleteSelected,
    HidePanel,
    MoveUp,
    MoveDown
}
