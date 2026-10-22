using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pulse.App.Native;

/// <summary>
/// One Pulse per user session. The running instance is usually elevated while a second launch (Start menu,
/// double-click) is not, so the named objects get a DACL and a Medium integrity label that let a
/// non-elevated process see the mutex and signal "show your window".
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Pulse.SingleInstance";
    private const string ShowEventName = @"Local\Pulse.ShowWindow";
    private const string Sddl = "D:(A;;GA;;;AU)S:(ML;;NW;;;ME)";

    private const uint SYNCHRONIZE = 0x00100000;
    private const uint EVENT_MODIFY_STATE = 0x0002;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_ALREADY_EXISTS = 183;

    private nint _mutex;
    private ShowHandle? _show;

    /// <summary>Signalled when another launch asks this instance to show its window.</summary>
    public WaitHandle? ShowRequested => _show;

    /// <summary>True if another instance holds the mutex (even one we lack rights to open).</summary>
    public static bool IsRunning()
    {
        nint h = OpenMutexW(SYNCHRONIZE, false, MutexName);
        if (h != 0)
        {
            CloseHandle(h);
            return true;
        }
        return Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED;
    }

    public static void SignalShow()
    {
        nint h = OpenEventW(EVENT_MODIFY_STATE, false, ShowEventName);
        if (h == 0)
            return;
        SetEvent(h);
        CloseHandle(h);
    }

    /// <returns>False if another instance won the race.</returns>
    public bool TryAcquire()
    {
        _mutex = CreateWithSecurity(sa => CreateMutexW(ref sa, false, MutexName), () => CreateMutexW(0, false, MutexName));
        int error = Marshal.GetLastPInvokeError();
        if (_mutex == 0)
            return false; // typically access denied: it exists and belongs to another instance
        if (error == ERROR_ALREADY_EXISTS)
        {
            CloseHandle(_mutex);
            _mutex = 0;
            return false;
        }
        nint ev = CreateWithSecurity(sa => CreateEventW(ref sa, false, false, ShowEventName), () => CreateEventW(0, false, false, ShowEventName));
        if (ev != 0)
            _show = new ShowHandle(ev);
        return true;
    }

    private static nint CreateWithSecurity(Func<SECURITY_ATTRIBUTES, nint> create, Func<nint> fallback)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out nint sd, 0))
            return fallback();
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
            nint h = create(sa);
            return h != 0 ? h : fallback();
        }
        finally
        {
            LocalFree(sd);
        }
    }

    public void Dispose()
    {
        _show?.Dispose();
        if (_mutex != 0)
        {
            CloseHandle(_mutex);
            _mutex = 0;
        }
    }

    private sealed class ShowHandle : WaitHandle
    {
        public ShowHandle(nint handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateMutexW(ref SECURITY_ATTRIBUTES sa, bool initialOwner, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateMutexW(nint sa, bool initialOwner, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenMutexW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateEventW(ref SECURITY_ATTRIBUTES sa, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateEventW(nint sa, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenEventW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint descriptor, nint size);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
