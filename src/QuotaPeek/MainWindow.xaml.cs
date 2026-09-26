using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QuotaPeek.Native;
using QuotaPeek.UI;
using Forms = System.Windows.Forms;

namespace QuotaPeek;

public partial class MainWindow : Window
{
    private readonly App app;
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer hover = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer collapse = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private bool expanded = true, hoverExpanded, locked, userHidden, fullscreenHidden, sessionLocked, sleeping, exiting;
    private (int X, int Y)? dragFrom;
    private (double X, double Y) dragWindow;
    private bool dragMoved, dragExpands;
    private string unlock = "托盘菜单";
    private bool rendered, positioned;

    public MainWindow(App app)
    {
        InitializeComponent();
        this.app = app;
        tray = new Forms.NotifyIcon { Icon = MakeIcon(), Text = "QuotaPeek", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, (_, _) => Dispatcher.Invoke(ToggleVisible));
        menu.Items.Add("立即刷新", null, (_, _) => Dispatcher.InvokeAsync(async () => await app.Monitor.RefreshAsync(true)));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(app.OpenSettings));
        menu.Items.Add("锁定 / 解锁穿透", null, (_, _) => Dispatcher.Invoke(ToggleLock));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 QuotaPeek", null, (_, _) => Dispatcher.Invoke(Exit));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { userHidden = false; Show(); SetExpanded(true); });
        app.Monitor.Changed += Render;
        app.Monitor.LowQuota += NotifyLow;
        SourceInitialized += (_, _) =>
        {
            WindowNative.Configure(this, false);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(Hook);
            unlock = WindowNative.RegisterUnlock(this);
            LockButton.ToolTip = "锁定穿透 · " + unlock + " 解锁";
            System.Windows.Automation.AutomationProperties.SetHelpText(LockButton, unlock);
        };
        Loaded += async (_, _) =>
        {
            CardsScroll.MaxHeight = Math.Max(160, Math.Min(560, SystemParameters.WorkArea.Height - 150));
            SetExpanded(app.Monitor.Settings.StartExpanded, false);
            Render(); UpdateLayout();
            timer.Start();
            await app.Monitor.RefreshAsync();
            CaptureRequested();
        };
        ContentRendered += (_, _) =>
        {
            if (positioned) return;
            // Let WPF commit the initial capsule size before native positioning;
            // otherwise WM_WINDOWPOSCHANGED can restore the old expanded width.
            // Show() can overwrite a Width change made by the first Loaded event.
            SetExpanded(expanded, false);
            UpdateLayout();
            positioned = true;
            WindowNative.Position(this, app.Monitor.Settings.LeftPixels, app.Monitor.Settings.TopPixels);
            SavePosition();
        };
        timer.Tick += async (_, _) =>
        {
            if (!sessionLocked && !sleeping)
            {
                CheckFullscreen();
                await app.Monitor.RefreshAsync();
                Render();
            }
        };
        hover.Tick += (_, _) => { hover.Stop(); if (dragFrom is null && !locked && IsMouseOver && !expanded) { hoverExpanded = true; SetExpanded(true, false); } };
        collapse.Tick += (_, _) => { collapse.Stop(); if (dragFrom is null && !IsMouseOver && hoverExpanded) { SetExpanded(false, false); hoverExpanded = false; } };
        SystemEvents.SessionSwitch += SessionSwitch;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        Closing += OnClosing;
        if (app.Monitor.Demo) Subtitle.Text = "演示模式 · 非真实账户数据";
    }

    private void Render()
    {
        if (exiting) return;
        var previous = positioned ? WindowNative.PixelPosition(this) : ((double X, double Y)?)null;
        var cards = app.Monitor.Settings.Providers.Where(p => p.Enabled)
            .Select(p => new CardViewModel(p, app.Monitor.Snapshots.GetValueOrDefault(p.Id), app.Monitor.History(p.Id))).ToList();
        Cards.ItemsSource = cards;
        EmptyText.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var priority = cards.OrderBy(c => c.Snapshot?.HasValue == true ? 0 : 1)
            .ThenBy(c => c.Snapshot?.Status == SnapshotStatus.Stale ? 0 : 1)
            .ThenBy(c => c.Snapshot?.Windows.Count > 0 ? (double)(c.Snapshot.Windows.Min(w => w.RemainingPercent) / Math.Max(1, c.Config.LowThreshold))
                : c.Snapshot?.Remaining is { } money ? (double)(money / Math.Max(0.01m, c.Config.LowThreshold)) : double.MaxValue).FirstOrDefault();
        CapsuleText.Text = priority is null ? "QuotaPeek · 添加账户" : priority.Name + "  " + priority.PrimaryValue;
        if (priority is not null) CapsuleDot.Fill = priority.StatusBrush;
        var tooltip = string.Join("\n", cards.Select(c => c.Name + " " + c.PrimaryValue + " · " + c.StatusText));
        tray.Text = tooltip.Length > 120 ? tooltip[..120] : tooltip.Length == 0 ? "QuotaPeek" : tooltip;
        FooterText.Text = app.Monitor.Demo ? "演示数据 · 仅用于预览" : locked ? "已锁定 · " + unlock + " 解锁" : app.Monitor.Paused ? "暂停刷新" : app.Monitor.IsRefreshing ? "正在同步…" : app.Monitor.Warning ?? "自动刷新 · 数据保存在本机";
        RefreshButton.IsEnabled = !app.Monitor.IsRefreshing;
        if (previous is { } position) { UpdateLayout(); WindowNative.Position(this, position.X, position.Y); }
    }

    private void NotifyLow(ProviderConfig config, QuotaSnapshot snapshot)
    {
        var text = snapshot.Kind == QuotaKind.RateWindow ? "剩余额度低于设置的阈值。" : "当前剩余 " + CardViewModel.Money(snapshot.Remaining, snapshot.Currency);
        tray.ShowBalloonTip(6000, config.Name + " 额度提醒", text, Forms.ToolTipIcon.Warning);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
        if (message == WindowNative.WmHotkey && wparam.ToInt32() == WindowNative.HotkeyId)
        {
            locked = false; WindowNative.Configure(this, false); userHidden = false; Show(); SetExpanded(true); Render(); handled = true;
        }
        if (message == 0x2E0 && positioned) Dispatcher.BeginInvoke(() => { var p = WindowNative.PixelPosition(this); WindowNative.Position(this, p.X, p.Y); });
        return IntPtr.Zero;
    }
    private void SetExpanded(bool value, bool remember = true)
    {
        if (locked && value) return;
        FinishDrag();
        expanded = value;
        void Resize() { Width = value ? 370 : 252; Expanded.Visibility = value ? Visibility.Visible : Visibility.Collapsed; Capsule.Visibility = value ? Visibility.Collapsed : Visibility.Visible; }
        if (positioned) WindowNative.ResizeAnchored(this, Resize); else Resize();
        if (remember)
        {
            hoverExpanded = false;
            app.Monitor.Settings.StartExpanded = value;
            SavePosition();
        }
    }
    private void ToggleLock()
    {
        FinishDrag();
        locked = !locked;
        if (locked) { hover.Stop(); collapse.Stop(); SetExpanded(false, false); }
        WindowNative.Configure(this, locked);
        Render();
    }
    private void ToggleVisible() { userHidden = !userHidden; if (userHidden) Hide(); else { Show(); CheckFullscreen(); } }
    private void CheckFullscreen()
    {
        var hide = app.Monitor.Settings.AutoHideFullscreen && WindowNative.FullscreenApp();
        if (hide && !fullscreenHidden) { fullscreenHidden = true; Hide(); }
        else if (!hide && fullscreenHidden) { fullscreenHidden = false; if (!userHidden) Show(); }
    }
    private void SessionSwitch(object sender, SessionSwitchEventArgs e) => Dispatcher.BeginInvoke(async () =>
    {
        if (e.Reason == SessionSwitchReason.SessionLock) sessionLocked = true;
        if (e.Reason == SessionSwitchReason.SessionUnlock) sessionLocked = false;
        app.Monitor.SetPaused(sessionLocked || sleeping);
        if (!app.Monitor.Paused) await app.Monitor.RefreshAsync();
    });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(async () =>
    {
        if (e.Mode == PowerModes.Suspend) sleeping = true;
        if (e.Mode == PowerModes.Resume) sleeping = false;
        app.Monitor.SetPaused(sessionLocked || sleeping);
        if (!app.Monitor.Paused) await app.Monitor.RefreshAsync();
    });
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!positioned) return;
        var p = WindowNative.PixelPosition(this); WindowNative.Position(this, p.X, p.Y);
    });
    private void SavePosition()
    {
        if (!positioned) return;
        var p = WindowNative.PixelPosition(this);
        app.Monitor.Settings.LeftPixels = p.X; app.Monitor.Settings.TopPixels = p.Y;
        try { app.Store.Save(app.Monitor.Settings); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { FooterText.Text = "窗口位置未保存：数据目录不可写"; }
    }
    private void DragStart(object sender, MouseButtonEventArgs e)
    {
        if (locked || dragFrom is not null) return;
        var control = FindDragControl(e.OriginalSource as DependencyObject);
        if (control is not null && control != ExpandButton) return;
        hover.Stop(); collapse.Stop();
        dragFrom = WindowNative.Cursor(); dragWindow = WindowNative.PixelPosition(this);
        dragMoved = false; dragExpands = control == ExpandButton;
        // Capture on the stable outer surface before ButtonBase consumes the press.
        if (!Outer.CaptureMouse()) { FinishDrag(); return; }
        e.Handled = true;
    }
    private static DependencyObject? FindDragControl(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is ButtonBase or ScrollBar or Thumb) return d;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
    private void DragMove(object sender, MouseEventArgs e)
    {
        if (dragFrom is not { } from) return;
        // WPF can deliver a queued move after the physical release, before MouseUp.
        // Keep the pending click until MouseUp (or LostMouseCapture) completes it.
        if (e.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;
        var cursor = WindowNative.Cursor();
        var dx = cursor.X - from.X; var dy = cursor.Y - from.Y;
        var dpi = VisualTreeHelper.GetDpi(this);
        if (!dragMoved && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX
            && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY) return;
        dragMoved = true;
        WindowNative.Position(this, dragWindow.X + dx, dragWindow.Y + dy);
    }
    private void DragEnd(object sender, MouseButtonEventArgs e)
    {
        if (dragFrom is null) return;
        var expandOnClick = dragExpands && !dragMoved && ExpandButton.InputHitTest(e.GetPosition(ExpandButton)) is not null;
        e.Handled = true;
        FinishDrag();
        if (expandOnClick) SetExpanded(true);
    }
    private void DragLostCapture(object sender, MouseEventArgs e)
    {
        if (dragFrom is not null && !Outer.IsMouseCaptured) FinishDrag();
    }
    private void FinishDrag()
    {
        if (dragFrom is null) return;
        var moved = dragMoved;
        dragFrom = null; dragMoved = false; dragExpands = false;
        if (Outer.IsMouseCaptured) Outer.ReleaseMouseCapture();
        if (moved) SavePosition();
        if (hoverExpanded && !IsMouseOver) collapse.Start();
    }
    private void Root_MouseEnter(object sender, MouseEventArgs e) { collapse.Stop(); if (dragFrom is null && !expanded && !locked) hover.Start(); }
    private void Root_MouseLeave(object sender, MouseEventArgs e) { hover.Stop(); if (dragFrom is null && hoverExpanded) collapse.Start(); }
    private void Expand_Click(object sender, RoutedEventArgs e) => SetExpanded(true);
    private void Collapse_Click(object sender, RoutedEventArgs e) => SetExpanded(false);
    private void Lock_Click(object sender, RoutedEventArgs e) => ToggleLock();
    private void Settings_Click(object sender, RoutedEventArgs e) => app.OpenSettings();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await app.Monitor.RefreshAsync(true);
    private void Exit_Click(object sender, RoutedEventArgs e) => Exit();
    private void Exit() { exiting = true; app.Shutdown(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        FinishDrag();
        SavePosition(); timer.Stop(); hover.Stop(); collapse.Stop(); tray.Visible = false; tray.Dispose();
        WindowNative.Unregister(this);
        SystemEvents.SessionSwitch -= SessionSwitch; SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        app.Monitor.Changed -= Render; app.Monitor.LowQuota -= NotifyLow;
        if (!exiting) { exiting = true; app.Shutdown(); }
    }
    private static Icon MakeIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);
        using var background = new SolidBrush(System.Drawing.Color.FromArgb(166, 237, 207));
        g.FillEllipse(background, 1, 1, 30, 30);
        using var font = new Font("Segoe UI", 19, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(System.Drawing.Color.FromArgb(17, 24, 32));
        g.DrawString("Q", font, ink, 5, 3);
        var handle = bitmap.GetHicon();
        var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    private void CaptureRequested()
    {
        if (rendered || app.RenderPath is not { } path) return;
        rendered = true;
        Dispatcher.InvokeAsync(() =>
        {
            UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var stream = File.Create(path); encoder.Save(stream);
        }, DispatcherPriority.ContextIdle);
    }
}
