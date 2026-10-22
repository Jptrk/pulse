using System.Runtime.InteropServices;

namespace Pulse.App.Native;

/// <summary>
/// PID → exe name map from a toolhelp snapshot. Never opens a handle to any process,
/// so anti-cheat drivers see nothing touching the game.
/// </summary>
internal sealed class ProcessTable
{
    private Dictionary<int, string> _names = [];

    public void Refresh()
    {
        nint snapshot = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPPROCESS, 0);
        if (snapshot == Win32.INVALID_HANDLE_VALUE)
            return;
        try
        {
            var names = new Dictionary<int, string>(_names.Count + 16);
            var entry = new Win32.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Win32.PROCESSENTRY32W>() };
            if (Win32.Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    names[(int)entry.th32ProcessID] = entry.szExeFile;
                } while (Win32.Process32NextW(snapshot, ref entry));
            }
            _names = names;
        }
        finally
        {
            Win32.CloseHandle(snapshot);
        }
    }

    public string? NameOf(int pid)
    {
        if (_names.TryGetValue(pid, out string? name))
            return name;
        Refresh();
        return _names.GetValueOrDefault(pid);
    }

    public bool IsAlive(int pid) => _names.ContainsKey(pid);
}
