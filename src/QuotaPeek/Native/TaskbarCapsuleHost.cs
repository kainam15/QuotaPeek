using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuotaPeek.UI;
using Forms = System.Windows.Forms;

namespace QuotaPeek.Native;

// A disposable child HWND keeps Explorer's lifetime separate from the main window.
// No hooks/injection into Explorer, SetParent on the main window, or taskbar resizing.
public sealed class TaskbarCapsuleHost : IDisposable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private HwndSource? source;
    private IntPtr parent;
    private IntPtr foregroundHook, lastForeground;
    private readonly WinEventCallback foregroundCallback;
    private bool enabled, updating, disposed, visible = true, locked;
    private int generation;
    public TaskbarCapsuleView View { get; } = new();
    public PixelRect? Bounds { get; private set; }
    public bool TaskbarHidden { get; private set; }
    public string? Warning { get; private set; }
    public bool IsAttached => source is { IsDisposed: false } && IsWindow(source.Handle);
    public event Action? PlacementChanged;

    public TaskbarCapsuleHost()
    {
        timer.Tick += async (_, _) => await RefreshAsync();
        foregroundCallback = (_, _, hwnd, _, _, _, _) => RememberForeground(hwnd);
    }

    public void SetEnabled(bool value)
    {
        if (enabled == value || disposed) return;
        enabled = value; generation++;
        if (value)
        {
            RememberForeground(GetForegroundWindow());
            foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundCallback, 0, 0, 0); // out-of-context EVENT_SYSTEM_FOREGROUND
            timer.Start(); _ = RefreshAsync();
        }
        else { timer.Stop(); ReleaseForegroundHook(); ReleaseSource(); Warning = null; TaskbarHidden = false; }
    }

    public void SetVisible(bool value)
    {
        visible = value;
        if (IsAttached) ShowWindow(source!.Handle, value ? 4 : 0);
    }

    public void SetLocked(bool value)
    {
        locked = value;
        if (!IsAttached) return;
        var style = GetWindowLongPtr(source!.Handle, -20).ToInt64();
        SetWindowLongPtr(source.Handle, -20, (IntPtr)(value ? style | 0x20 : style & ~0x20));
    }

    public async Task RefreshAsync()
    {
        if (!enabled || disposed || updating) return;
        updating = true;
        var version = generation;
        try
        {
            // UI Automation talks to Explorer on a worker thread; a slow shell must
            // not block clicks, settings or the notification-area recovery menu.
            var layout = await Task.Run(ReadLayout);
            if (!enabled || disposed || version != generation) return;
            TaskbarHidden = layout.Hidden;
            Warning = layout.Warning;
            if (layout.Hidden) return; // The parent HWND clips/moves its child during auto-hide.
            if (layout.Bounds is not { } bounds)
            {
                ReleaseSource();
                return;
            }
            if (parent != layout.Parent || !IsAttached)
            {
                ReleaseSource();
                var parameters = new HwndSourceParameters("QuotaPeek Taskbar Capsule")
                {
                    ParentWindow = layout.Parent,
                    WindowStyle = 0x40000000 | 0x04000000, // WS_CHILD | WS_CLIPSIBLINGS
                    ExtendedWindowStyle = 0x08000080, // NOACTIVATE | TOOLWINDOW
                    UsesPerPixelTransparency = true,
                    PositionX = bounds.Left - layout.Taskbar.Left,
                    PositionY = bounds.Top - layout.Taskbar.Top,
                    Width = bounds.Width, Height = bounds.Height,
                    HwndSourceHook = Hook
                };
                source = new HwndSource(parameters);
                parent = layout.Parent;
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
                source.RootVisual = View;
                SetLocked(locked);
            }
            var scale = Math.Max(1, GetDpiForWindow(source!.Handle)) / 96.0;
            View.Width = bounds.Width / scale; View.Height = bounds.Height / scale;
            if (!SetWindowPos(source.Handle, IntPtr.Zero, bounds.Left - layout.Taskbar.Left, bounds.Top - layout.Taskbar.Top,
                bounds.Width, bounds.Height, 0x10 | (visible ? 0x40u : 0u)))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Bounds = bounds;
            if (!visible) ShowWindow(source.Handle, 0);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or COMException or ElementNotAvailableException)
        {
            if (version == generation && enabled && !disposed)
            {
                ReleaseSource();
                Warning = "暂时无法嵌入任务栏，已使用悬浮胶囊";
            }
        }
        finally
        {
            updating = false;
            if (version == generation && enabled && !disposed) PlacementChanged?.Invoke();
        }
    }

    private static Layout ReadLayout()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var r))
            return new(taskbar, default, null, false, "正在等待任务栏，暂用悬浮胶囊");
        var rect = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
        var screen = Forms.Screen.FromHandle(taskbar).Bounds;
        if (rect.Width <= rect.Height || rect.Top < screen.Top + screen.Height / 2)
            return new(taskbar, rect, null, false, "嵌入需要底部任务栏，暂用悬浮胶囊");
        if (!IsWindowVisible(taskbar) || rect.Top >= screen.Bottom - 3)
            return new(taskbar, rect, null, true, null);

        var cache = new CacheRequest();
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.ProcessIdProperty);
        var occupied = new List<PixelRect>();
        using (cache.Activate())
        {
            var buttons = AutomationElement.FromHandle(taskbar).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                var info = button.Cached;
                if (info.ProcessId == Environment.ProcessId || info.BoundingRectangle.IsEmpty) continue;
                var b = info.BoundingRectangle;
                occupied.Add(new((int)Math.Floor(b.Left), (int)Math.Floor(b.Top), (int)Math.Ceiling(b.Right), (int)Math.Ceiling(b.Bottom)));
            }
        }
        var space = occupied.Count > 0 ? TaskbarLayout.FindSpace(rect, Math.Max(96, GetDpiForWindow(taskbar)) / 96.0, occupied) : null;
        return new(taskbar, rect, space, false, space is null ? "任务栏左侧空间不足，暂用悬浮胶囊" : null);
    }

    private void RememberForeground(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && hwnd != parent && IsWindow(hwnd) && (GetWindowLongPtr(hwnd, -20).ToInt64() & 0x08000000) == 0)
            lastForeground = hwnd;
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == 0x200) RememberForeground(GetForegroundWindow());
        if (message == 0x21)
        {
            RememberForeground(GetForegroundWindow());
            var previous = lastForeground;
            // Clicking a cross-process taskbar child can clear the foreground BEFORE
            // WM_MOUSEACTIVATE arrives. Restore only that empty state, never a newly
            // activated app or a settings/menu window opened by the user.
            View.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (enabled && !disposed && GetForegroundWindow() == IntPtr.Zero && IsWindow(previous))
                    SetForegroundWindow(previous);
            });
            handled = true; return new IntPtr(3); // MA_NOACTIVATE
        }
        return IntPtr.Zero;
    }

    private void ReleaseForegroundHook()
    {
        if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
        foregroundHook = IntPtr.Zero; lastForeground = IntPtr.Zero;
    }

    private void ReleaseSource()
    {
        if (source is { IsDisposed: false }) { source.RootVisual = null; source.Dispose(); }
        source = null; parent = IntPtr.Zero; Bounds = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; generation++; timer.Stop(); ReleaseForegroundHook(); ReleaseSource();
    }

    private sealed record Layout(IntPtr Parent, PixelRect Taskbar, PixelRect? Bounds, bool Hidden, string? Warning);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
