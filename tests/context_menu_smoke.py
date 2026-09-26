"""Physical menu dismissal checks against a packaged EXE or a running taskbar widget."""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
from pathlib import Path
import subprocess
import time
import tkinter as tk

from PIL import ImageGrab
from pywinauto import Desktop, keyboard, mouse


parser = argparse.ArgumentParser()
parser.add_argument('--exe', default='dist/QuotaPeek.exe')
parser.add_argument('--pid', type=int, help='Verify an existing taskbar instance without changing its settings.')
parser.add_argument('--repeat', type=int, default=3)
parser.add_argument('--floating', action='store_true', help='Check the floating widget right-click menu.')
args = parser.parse_args()
artifact = Path(__file__).resolve().parents[1] / '.artifacts' / ('context-menu-' + time.strftime('%Y%m%d-%H%M%S'))
artifact.mkdir(parents=True)
u = ctypes.WinDLL('user32', use_last_error=True)
u.FindWindowW.argtypes = [wt.LPCWSTR, wt.LPCWSTR]; u.FindWindowW.restype = wt.HWND
u.FindWindowExW.argtypes = [wt.HWND, wt.HWND, wt.LPCWSTR, wt.LPCWSTR]; u.FindWindowExW.restype = wt.HWND
u.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
u.GetForegroundWindow.restype = wt.HWND
u.SetForegroundWindow.argtypes = [wt.HWND]
u.IsWindow.argtypes = [wt.HWND]
u.GetCursorPos.argtypes = [ctypes.POINTER(wt.POINT)]
u.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
u.GetAncestor.argtypes = [wt.HWND, wt.UINT]; u.GetAncestor.restype = wt.HWND
u.WindowFromPoint.argtypes = [wt.POINT]; u.WindowFromPoint.restype = wt.HWND
u.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
u.OpenInputDesktop.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]; u.OpenInputDesktop.restype = wt.HANDLE
u.GetUserObjectInformationW.argtypes = [wt.HANDLE, ctypes.c_int, wt.LPVOID, wt.DWORD, ctypes.POINTER(wt.DWORD)]
u.CloseDesktop.argtypes = [wt.HANDLE]
original_cursor = wt.POINT()
u.GetCursorPos(ctypes.byref(original_cursor))
original_foreground = u.GetForegroundWindow()
results = {'exe': str(Path(args.exe).resolve()), 'pid': args.pid, 'floating': args.floating, 'physical': 'not-run', 'checks': []}
proc = None
target = None
app_pid = args.pid
desktop = Desktop(backend='uia')
taskbar = u.FindWindowW('Shell_TrayWnd', None)


def wait_for(predicate, timeout=5):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if target:
            target.update()
        value = predicate()
        if value:
            return value
        time.sleep(.04)
    raise AssertionError('Timed out: ' + predicate.__name__)


def check(name, passed, **evidence):
    results['checks'].append({'name': name, 'passed': bool(passed), **evidence})
    print('PASS' if passed else 'FAIL', name, evidence or '', flush=True)
    if not passed:
        raise AssertionError(name)


def capsule_handle():
    after = None
    while True:
        after = u.FindWindowExW(taskbar, after, None, 'QuotaPeek Taskbar Capsule')
        if not after:
            return None
        pid = wt.DWORD()
        u.GetWindowThreadProcessId(after, ctypes.byref(pid))
        if pid.value == app_pid:
            return after


def menus():
    # WPF exposes the popup HWND as a Window containing a Menu.
    candidates = Desktop(backend='win32').windows(process=app_pid, visible_only=True)
    result = []
    for window in candidates:
        if window.window_text() or not window.class_name().startswith('HwndWrapper'):
            continue
        popup = desktop.window(handle=window.handle).wrapper_object()
        if popup.children(control_type='Menu'):
            result.append(popup)
    return result


def click(control, button='left'):
    r = control.rectangle()
    point = (r.left + r.width() // 2, r.top + r.height() // 2)
    hit_pid = wt.DWORD()
    u.GetWindowThreadProcessId(u.WindowFromPoint(wt.POINT(*point)), ctypes.byref(hit_pid))
    if hit_pid.value != app_pid:
        raise RuntimeError(f'Test control is obscured by another process: {hit_pid.value}; point={point}')
    mouse.click(button=button, coords=point)


def capture(name):
    r = Desktop(backend='win32').window(handle=taskbar).rectangle()
    ImageGrab.grab(bbox=(r.left, r.top - 320, min(r.left + 950, r.right), r.bottom),
                   include_layered_windows=True).save(artifact / name)


def settings_window():
    for window in Desktop(backend='win32').windows(process=app_pid, visible_only=True):
        if window.window_text() == 'QuotaPeek 设置':
            return desktop.window(handle=window.handle)
    return None


def cleanup_menu():
    # The broken build cannot reliably receive Escape. Invoke Settings and close
    # only the window we opened, without saving preferences or calling providers.
    for menu in menus():
        already_open = settings_window() is not None
        desktop.window(handle=menu.handle).child_window(title='设置', control_type='MenuItem').invoke()
        settings = wait_for(settings_window)
        if not already_open:
            settings.close()


try:
    input_desktop = u.OpenInputDesktop(0, False, 1)
    name = ctypes.create_unicode_buffer(128)
    needed = wt.DWORD()
    physical = bool(input_desktop and u.GetUserObjectInformationW(input_desktop, 2, name, ctypes.sizeof(name), ctypes.byref(needed)) and name.value == 'Default')
    if input_desktop:
        u.CloseDesktop(input_desktop)
    if not physical:
        results['physical'] = 'environment-blocked'
        raise SystemExit('An unlocked input desktop is required; UIA invocation is not a substitute.')

    if not app_pid:
        data = artifact / 'data'
        data.mkdir()
        (data / 'settings.json').write_text(json.dumps({
            'Version': 1, 'TaskbarDocked': not args.floating, 'StartExpanded': False,
            'LeftPixels': 240, 'TopPixels': 620,
            'Notifications': False, 'AutoHideFullscreen': False,
            'Providers': [{'Id': 'menu-check', 'Name': '菜单验证', 'Type': 'Manual', 'ManualRemaining': 10.45, 'LowThreshold': 0}]
        }), encoding='utf-8')
        proc = subprocess.Popen([str(Path(args.exe).resolve()), '--data-dir', str(data)], creationflags=subprocess.CREATE_NO_WINDOW)
        app_pid = proc.pid
        results['pid'] = app_pid
    if args.floating:
        capsule = desktop.window(title='QuotaPeek', process=app_pid)
        capsule.wait('visible', timeout=20)
        if not args.pid:
            wait_for(lambda: capsule.rectangle().left == 240 and capsule.rectangle().top == 620)
        menu_button = None
        expand_button = capsule.child_window(auto_id='ExpandButton', control_type='Button')
    else:
        capsule = desktop.window(handle=wait_for(capsule_handle, 20))
        menu_button = capsule.child_window(auto_id='TaskbarMenuButton', control_type='Button')
        expand_button = capsule.child_window(auto_id='TaskbarExpandButton', control_type='Button')

    # A controlled external window proves clicks reach another process, including
    # when that process was already foreground before opening the menu.
    target = tk.Tk()
    target.title('QuotaPeek menu test surface')
    target.geometry('420x180+650+350')
    target.attributes('-topmost', True)
    target.configure(bg='#d5e5ee')
    tk.Label(target, text='QuotaPeek 菜单关闭验证', bg='#d5e5ee').pack(pady=60)
    target.update()
    target_hwnd = u.GetAncestor(target.winfo_id(), 2)
    received = []
    target.bind('<ButtonPress>', lambda event: received.append(event.num))
    cleanup_menu()
    wait_for(lambda: not menus())
    results['physical'] = 'running'

    for iteration in range(args.repeat):
        scenarios = ([('right-click', 'left'), ('right-click', 'right'), ('right-click', 'escape')]
                     if args.floating else [('button', 'left'), ('right-click', 'left'), ('button', 'right'), ('button', 'escape')])
        for opening, dismissal in scenarios:
            u.SetForegroundWindow(target_hwnd)
            wait_for(lambda: u.GetForegroundWindow() == target_hwnd)
            click(menu_button if opening == 'button' else expand_button, 'left' if opening == 'button' else 'right')
            menu = wait_for(menus)[0]
            check(f'{iteration + 1}: {opening} opens menu', menu.is_visible(), foreground=u.GetForegroundWindow(), menu=menu.handle)
            if iteration == 0 and opening == 'button' and dismissal == 'left':
                capture('opened.png')
            if dismissal == 'escape':
                keyboard.send_keys('{ESC}')
            else:
                before = len(received)
                mouse.click(button=dismissal, coords=(target.winfo_rootx() + 30, target.winfo_rooty() + 30))
            try:
                wait_for(lambda: not menus(), 2)
                closed = True
            except AssertionError:
                closed = False
            forwarded = dismissal != 'escape' and len(received) > before
            check(f'{iteration + 1}: {opening} / {dismissal} dismisses menu', closed,
                  target_received_click=forwarded if dismissal != 'escape' else None)
            # Native menu capture may consume the dismissal click. If it reaches
            # the target, ensure the widget does not take foreground back.
            if forwarded:
                check('outside click keeps target foreground', u.GetForegroundWindow() == target_hwnd)

    click(expand_button if args.floating else menu_button, 'right' if args.floating else 'left')
    menu = wait_for(menus)[0]
    check('menu stays open before selecting Settings', menu.is_visible())
    menu.capture_as_image().save(artifact / 'before-settings.png')
    settings_was_open = settings_window() is not None
    click(desktop.window(handle=menu.handle).child_window(title='设置', control_type='MenuItem'))
    settings = wait_for(settings_window, 8)
    wait_for(lambda: not menus())
    check('inside Settings item still executes and dismisses menu', settings.is_visible())
    if not settings_was_open:
        settings.close()
    results['physical'] = 'passed'
    capture('dismissed.png')
except Exception as error:
    results['physical'] = 'failed'
    results['error'] = repr(error)
    try:
        capture('failure.png')
    except OSError:
        results['screenshot'] = 'unavailable (desktop locked or disconnected)'
    raise
finally:
    if app_pid:
        cleanup_menu()
    if target:
        target.destroy()
    if proc and proc.poll() is None:
        for window in Desktop(backend='win32').windows(process=app_pid, visible_only=False):
            if window.window_text() == 'QuotaPeek':
                u.PostMessageW(window.handle, 0x10, 0, 0)
        try:
            proc.wait(timeout=8)
        except subprocess.TimeoutExpired:
            proc.kill(); proc.wait()
    if original_foreground and u.IsWindow(original_foreground):
        u.SetForegroundWindow(original_foreground)
    u.SetCursorPos(original_cursor.x, original_cursor.y)
    (artifact / 'result.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    print('ARTIFACTS', artifact, flush=True)
