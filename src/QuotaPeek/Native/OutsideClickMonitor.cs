using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace QuotaPeek.Native;

// NOACTIVATE windows do not reliably receive Deactivated. Observe button presses
// only while the panel is visible; never consume input intended for another app.
internal sealed class OutsideClickMonitor : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Action clickedOutside;
    private readonly HookProc callback;
    private readonly uint processId = (uint)Environment.ProcessId;
    private IntPtr hook;
    private int generation;
    private bool disposed;

    public OutsideClickMonitor(Dispatcher dispatcher, Action clickedOutside)
    {
        this.dispatcher = dispatcher;
        this.clickedOutside = clickedOutside;
        callback = OnMouseInput;
    }

    public void SetEnabled(bool enabled)
    {
        if (disposed || enabled == (hook != IntPtr.Zero)) return;
        generation++;
        if (enabled)
        {
            hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0); // WH_MOUSE_LL
            if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        else
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }

    private IntPtr OnMouseInput(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt64() is 0x201 or 0x204 or 0x207 or 0x20B)
        {
            // POINT is the first member of MSLLHOOKSTRUCT, in physical screen pixels.
            // Resolve the target now, before a menu closes or a capsule toggles.
            var point = Marshal.PtrToStructure<Point>(data);
            var target = WindowFromPoint(point);
            GetWindowThreadProcessId(target, out var targetProcess);
            // Includes our taskbar child HWND, settings and popup menus, even though
            // their screen rectangles are outside the expanded panel.
            if (target != IntPtr.Zero && targetProcess != processId)
            {
                var version = generation;
                dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (!disposed && hook != IntPtr.Zero && version == generation) clickedOutside();
                }));
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    public void Dispose()
    {
        SetEnabled(false);
        disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
