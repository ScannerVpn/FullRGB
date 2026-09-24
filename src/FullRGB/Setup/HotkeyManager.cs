using System.Runtime.InteropServices;

namespace FullRGB.Setup;

/// <summary>
/// System-wide hotkeys (Win32 RegisterHotKey).
///
/// Why this exists: an RGB app spends most of its life in the background — a game, a film, a
/// browser is in front. Reaching "blackout" or "next profile" through the tray still needs a mouse
/// and a window switch, so those actions were effectively unreachable exactly when they matter.
/// </summary>
public static class HotkeyManager
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>WM_HOTKEY — the message a registered hotkey arrives on.</summary>
    public const int WmHotkey = 0x0312;

    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;
    /// <summary>Held key must not auto-repeat: a hotkey that toggles must fire once per press.</summary>
    private const uint ModNoRepeat = 0x4000;

    /// <summary>Which action a registered hotkey performs. The value doubles as the Win32 id.</summary>
    public enum Action
    {
        ToggleEffects = 1,
        Blackout = 2,
        NextProfile = 3,
    }

    /// <summary>
    /// Parses "Ctrl+Alt+L" (case-insensitive, spaces tolerated) into Win32 modifiers + a virtual
    /// key. Returns false for anything unusable so the UI can say so instead of silently binding
    /// nothing. Requires at least one modifier: a bare letter would swallow normal typing.
    /// </summary>
    public static bool TryParse(string? combo, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;

        var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries)
                         .Select(p => p.Trim())
                         .Where(p => p.Length > 0)
                         .ToArray();
        if (parts.Length < 2) return false;   // needs a modifier + a key

        foreach (var raw in parts[..^1])
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win": case "windows": case "meta": modifiers |= ModWin; break;
                default: return false;
            }
        }
        if (modifiers == 0) return false;

        string key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z') vk = key[0];
        else if (key.Length == 1 && key[0] is >= '0' and <= '9') vk = key[0];
        else if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key[1..], out int fn)
                 && fn is >= 1 and <= 24) vk = (uint)(0x70 + fn - 1);
        else return false;

        modifiers |= ModNoRepeat;
        return true;
    }

    /// <summary>Human-readable form of a combo, or the input unchanged when it does not parse.</summary>
    public static string Describe(string? combo) => TryParse(combo, out _, out _) ? (combo ?? "").Trim() : "";

    /// <summary>Registers one action. Returns false when Windows already owns the combination.</summary>
    public static bool Register(IntPtr hwnd, Action action, string combo, out string error)
    {
        error = "";
        if (hwnd == IntPtr.Zero) { error = "window not ready"; return false; }
        if (!TryParse(combo, out uint mods, out uint key))
        {
            error = "needs a modifier and a key, e.g. Ctrl+Alt+L";
            return false;
        }
        if (!RegisterHotKey(hwnd, (int)action, mods, key))
        {
            error = "already taken by another program";
            return false;
        }
        return true;
    }

    public static void Unregister(IntPtr hwnd, Action action)
    {
        try { if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, (int)action); } catch { }
    }
}
