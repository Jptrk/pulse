using System.Windows.Input;
using System.Windows.Interop;

namespace Pulse.App.Native;

/// <summary>System-wide hotkeys via RegisterHotKey on a message-only window. Works while a game has focus, no hooks.</summary>
internal sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 0xB000;

    public HotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("PulseHotkeys") { ParentWindow = Win32.HWND_MESSAGE, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    /// <returns>Null on success, otherwise a reason to show the user.</returns>
    public string? Register(string gesture, Action action)
    {
        if (string.IsNullOrWhiteSpace(gesture))
            return null;
        if (!TryParse(gesture, out uint modifiers, out uint vk))
            return $"Can't read hotkey \"{gesture}\".";

        int id = _nextId++;
        if (!Win32.RegisterHotKey(_source.Handle, id, modifiers | Win32.MOD_NOREPEAT, vk))
            return $"Hotkey {gesture} is already used by another app.";
        _actions[id] = action;
        return null;
    }

    public void UnregisterAll()
    {
        foreach (int id in _actions.Keys)
            Win32.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (string raw in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Win32.MOD_CONTROL; continue;
                case "alt": modifiers |= Win32.MOD_ALT; continue;
                case "shift": modifiers |= Win32.MOD_SHIFT; continue;
                case "win" or "windows": modifiers |= Win32.MOD_WIN; continue;
            }
            string keyName = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
            if (vk != 0 || !Enum.TryParse(keyName, ignoreCase: true, out Key key) || key == Key.None)
                return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        return vk != 0 && modifiers != 0;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _actions.TryGetValue((int)wParam, out Action? action))
        {
            handled = true;
            action();
        }
        return 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
