using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace QuotaPeek.Native;

public static class WindowNative
{
    public const int HotkeyId = 0x514;
    public const int WmHotkey = 0x312;
    private const int GwlExStyle = -20;
    private const long ToolWindow = 0x80, NoActivate = 0x8000000, Transparent = 0x20, AppWindow = 0x40000;

    public static void Configure(Window window, bool locked)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style = (style | ToolWindow | NoActivate) & ~AppWindow;
        style = locked ? style | Transparent : style & ~Transparent;
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)style);
        SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x20);
    }

    public static string RegisterUnlock(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (RegisterHotKey(handle, HotkeyId, 0x4000 | 0x1 | 0x2, 0x51)) return "Ctrl+Alt+Q";
        if (RegisterHotKey(handle, HotkeyId, 0x4000 | 0x1 | 0x2 | 0x4, 0x51)) return "Ctrl+Alt+Shift+Q";
        return "托盘菜单";
    }
    public static void Unregister(Window window) => UnregisterHotKey(new WindowInteropHelper(window).Handle, HotkeyId);

    public static void ActivateMenu(ContextMenu menu)
    {
        // NOACTIVATE hosts cannot give their popup normal deactivation handling.
        // Activate only the menu's HWND, after Opened has created it, so WPF can
        // dismiss on outside clicks and receive Escape without activating the widget.
        if (PresentationSource.FromVisual(menu) is HwndSource source)
        {
            SetForegroundWindow(source.Handle);
            menu.Focus();
        }
    }

    public static bool FullscreenApp()
    {
        return SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;
    }

    public static (double X, double Y) PixelPosition(Window window)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds);
        return (bounds.Left, bounds.Top);
    }

    public static void Position(Window window, double? x, double? y, bool bottomRight = false)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var screen = x is not null && y is not null ? Forms.Screen.FromPoint(new((int)x, (int)y)) : Forms.Screen.PrimaryScreen!;
        var area = screen.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        var left = bottomRight || x is null ? area.Right - width - 18 : (int)x.Value;
        var top = bottomRight || y is null ? area.Bottom - height - 18 : (int)y.Value;
        left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - width));
        top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - height));
        // Configure establishes TOPMOST once. Repositioning must preserve the
        // popup/owner order, especially during the taskbar's periodic refresh.
        SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, 0x1 | 0x4 | 0x10); // NOSIZE | NOZORDER | NOACTIVATE
    }

    public static void ResizeAnchored(Window window, Action resize)
    {
        var handle = new WindowInteropHelper(window).Handle;
        GetWindowRect(handle, out var before);
        resize();
        window.UpdateLayout();
        GetWindowRect(handle, out var after);
        Position(window, before.Right - (after.Right - after.Left), before.Bottom - (after.Bottom - after.Top));
    }

    public static (int X, int Y) Cursor()
    {
        GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
