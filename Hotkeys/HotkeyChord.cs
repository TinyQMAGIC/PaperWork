using System;
using System.Collections.Generic;
using System.Windows.Input;
using Paperwork.Shell;

namespace Paperwork.Hotkeys;

/// <summary>
/// 一个热键组合。存的是 Win32 的 modifier 位 + 虚拟键码，
/// 同时保留一份人类可读文本用于设置页显示。
/// </summary>
internal readonly record struct HotkeyChord(uint Modifiers, uint VirtualKey, string Display)
{
    public uint ToModifierFlags() => Modifiers | Native.MOD_NOREPEAT;

    /// <summary>默认 Ctrl+Alt+D。是否会被中文输入法抢，由 Spike S4 实机验证。</summary>
    public static HotkeyChord Default { get; } = new(
        Native.MOD_CONTROL | Native.MOD_ALT,
        (uint)KeyInterop.VirtualKeyFromKey(Key.D),
        "Ctrl+Alt+D");

    public static HotkeyChord FromInput(Key key, ModifierKeys mods)
    {
        uint m = 0;
        if (mods.HasFlag(ModifierKeys.Control)) m |= Native.MOD_CONTROL;
        if (mods.HasFlag(ModifierKeys.Alt)) m |= Native.MOD_ALT;
        if (mods.HasFlag(ModifierKeys.Shift)) m |= Native.MOD_SHIFT;
        if (mods.HasFlag(ModifierKeys.Windows)) m |= Native.MOD_WIN;

        return new HotkeyChord(m, (uint)KeyInterop.VirtualKeyFromKey(key), BuildDisplay(m, KeyName(key)));
    }

    /// <summary>
    /// 这些键不能当热键。<see cref="Key.System"/> 不是"某个键"，而是 WPF 给 Alt 组合打的
    /// 一个类别标记（真键码在 <c>KeyEventArgs.SystemKey</c>）；IME/死键同理。
    /// 把它们转成虚拟键码会得到 0 或垃圾值，注册必然失败。
    /// </summary>
    public static bool IsUsableKey(Key key) =>
        key is not (Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed
                 or Key.LWin or Key.RWin);

    /// <summary>
    /// 键名 → 键帽上真正印的东西。<c>Key.ToString()</c> 对符号键给的是 <c>OemComma</c>、
    /// <c>D1</c> 这类内部名，直接显示出来就是"Ctro+OemComma"，用户看不懂。
    /// 只覆盖"名字和键帽不一致"的那些；字母与 F1–F24 原样返回。
    /// </summary>
    public static string KeyName(Key key) => key switch
    {
        Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
        Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",

        Key.NumPad0 => "Num0", Key.NumPad1 => "Num1", Key.NumPad2 => "Num2",
        Key.NumPad3 => "Num3", Key.NumPad4 => "Num4", Key.NumPad5 => "Num5",
        Key.NumPad6 => "Num6", Key.NumPad7 => "Num7", Key.NumPad8 => "Num8",
        Key.NumPad9 => "Num9",

        // Oem1..Oem7 与下面这批别名指向同一个虚拟键码，WPF 给哪一边取决于映射表顺序，
        // 两边都写上，免得换键盘布局后显示突然变成 Oem3。
        Key.Oem1 or Key.OemSemicolon => ";",
        Key.Oem2 or Key.OemQuestion => "/",
        Key.Oem3 or Key.OemTilde => "`",
        Key.Oem4 or Key.OemOpenBrackets => "[",
        Key.Oem5 or Key.OemPipe or Key.OemBackslash => "\\",
        Key.Oem6 or Key.OemCloseBrackets => "]",
        Key.Oem7 or Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemMinus => "-",
        Key.OemPlus => "=",

        Key.Space => "Space",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down",

        _ => key.ToString()
    };

    /// <summary>
    /// <see cref="KeyName"/> 的反方向。存进 state.json 的是显示文本（如 <c>Ctrl+;</c>），
    /// 光靠 <c>Enum.TryParse</c> 认不出分号，所以符号名要有个回表；表里没有的再退回枚举解析。
    /// </summary>
    public static bool TryParseKey(string? text, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "0": key = Key.D0; return true;
            case "1": key = Key.D1; return true;
            case "2": key = Key.D2; return true;
            case "3": key = Key.D3; return true;
            case "4": key = Key.D4; return true;
            case "5": key = Key.D5; return true;
            case "6": key = Key.D6; return true;
            case "7": key = Key.D7; return true;
            case "8": key = Key.D8; return true;
            case "9": key = Key.D9; return true;

            case "num0": key = Key.NumPad0; return true;
            case "num1": key = Key.NumPad1; return true;
            case "num2": key = Key.NumPad2; return true;
            case "num3": key = Key.NumPad3; return true;
            case "num4": key = Key.NumPad4; return true;
            case "num5": key = Key.NumPad5; return true;
            case "num6": key = Key.NumPad6; return true;
            case "num7": key = Key.NumPad7; return true;
            case "num8": key = Key.NumPad8; return true;
            case "num9": key = Key.NumPad9; return true;

            case ";": key = Key.Oem1; return true;
            case "/": key = Key.Oem2; return true;
            case "`": key = Key.Oem3; return true;
            case "[": key = Key.Oem4; return true;
            case "\\": key = Key.Oem5; return true;
            case "]": key = Key.Oem6; return true;
            case "'": key = Key.Oem7; return true;
            case ",": key = Key.OemComma; return true;
            case ".": key = Key.OemPeriod; return true;
            case "-": key = Key.OemMinus; return true;
            case "=": key = Key.OemPlus; return true;

            case "space": key = Key.Space; return true;
            case "enter": case "return": key = Key.Return; return true;
            case "esc": case "escape": key = Key.Escape; return true;
            case "backspace": key = Key.Back; return true;
            case "delete": key = Key.Delete; return true;
            case "insert": key = Key.Insert; return true;
            case "home": key = Key.Home; return true;
            case "end": key = Key.End; return true;
            case "pageup": key = Key.PageUp; return true;
            case "pagedown": key = Key.PageDown; return true;
            case "left": key = Key.Left; return true;
            case "right": key = Key.Right; return true;
            case "up": key = Key.Up; return true;
            case "down": key = Key.Down; return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out key) && IsUsableKey(key);
    }

    /// <summary>
    /// 解析 "Ctrl+Alt+D" 形式。必须至少带一个修饰键——
    /// 裸键做全局热键会把系统里所有该键的输入吞掉。
    /// </summary>
    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;   // 无修饰键，拒绝

        uint m = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control": m |= Native.MOD_CONTROL; break;
                case "alt": m |= Native.MOD_ALT; break;
                case "shift": m |= Native.MOD_SHIFT; break;
                case "win":
                case "windows": m |= Native.MOD_WIN; break;
                default: return false;
            }
        }

        if (m == 0) return false;

        // Win 组合由系统保留，一律不收。已存进 state.json 的 Win 键位在这里被判为不合法，
        // 启动时就会退回默认——这正是"不收 Win"落到的自愈分支。
        if ((m & Native.MOD_WIN) != 0) return false;

        if (!TryParseKey(parts[^1], out Key key) || !IsUsableKey(key)) return false;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return false;

        chord = new HotkeyChord(m, vk, BuildDisplay(m, KeyName(key)));
        return true;
    }

    private static string BuildDisplay(uint modifiers, string keyName)
    {
        var order = new (uint Flag, string Text)[]
        {
            (Native.MOD_WIN, "Win"), (Native.MOD_CONTROL, "Ctrl"),
            (Native.MOD_ALT, "Alt"), (Native.MOD_SHIFT, "Shift")
        };
        var pieces = new List<string>(4);
        foreach (var (flag, text) in order)
            if ((modifiers & flag) != 0) pieces.Add(text);
        pieces.Add(keyName);
        return string.Join('+', pieces);
    }

    public override string ToString() => Display;
}
