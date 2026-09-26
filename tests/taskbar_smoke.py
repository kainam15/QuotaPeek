"""Packaged taskbar embedding, physical clicks and isolated restart checks."""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
from pathlib import Path
import subprocess
import time

from PIL import ImageGrab
from pywinauto import Application, Desktop, mouse
from pywinauto.timings import wait_until
from outside_click_target import collapse_panel

parser = argparse.ArgumentParser()
parser.add_argument('--exe', default='dist/QuotaPeek.exe')
args = parser.parse_args()
artifact = Path(__file__).resolve().parents[1] / '.artifacts' / ('taskbar-' + time.strftime('%Y%m%d-%H%M%S'))
artifact.mkdir(parents=True)
settings_path = artifact / 'settings.json'
settings_path.write_text(json.dumps({
    'Version': 1, 'StartExpanded': False, 'TaskbarDocked': False,
    'LeftPixels': 240, 'TopPixels': 620, 'Notifications': False, 'AutoHideFullscreen': False,
    'Providers': [{'Id': 'taskbar-check', 'Name': 'Hone 测试钱包', 'Type': 'Manual', 'ManualRemaining': 10.45, 'LowThreshold': 0}]
}), encoding='utf-8')
u = ctypes.WinDLL('user32', use_last_error=True)
u.FindWindowW.argtypes = [wt.LPCWSTR, wt.LPCWSTR]; u.FindWindowW.restype = wt.HWND
u.FindWindowExW.argtypes = [wt.HWND, wt.HWND, wt.LPCWSTR, wt.LPCWSTR]; u.FindWindowExW.restype = wt.HWND
u.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
u.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
enum_callback = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
u.EnumWindows.argtypes = [enum_callback, wt.LPARAM]
u.GetParent.argtypes = [wt.HWND]; u.GetParent.restype = wt.HWND
u.GetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int]; u.GetWindowLongPtrW.restype = ctypes.c_ssize_t
u.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
u.GetDpiForWindow.argtypes = [wt.HWND]; u.GetDpiForWindow.restype = wt.UINT
u.IsWindowVisible.argtypes = [wt.HWND]
u.IsWindow.argtypes = [wt.HWND]
u.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
u.GetForegroundWindow.restype = wt.HWND
u.SetForegroundWindow.argtypes = [wt.HWND]
u.GetCursorPos.argtypes = [ctypes.POINTER(wt.POINT)]
u.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
u.OpenInputDesktop.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]; u.OpenInputDesktop.restype = wt.HANDLE
u.GetUserObjectInformationW.argtypes = [wt.HANDLE, ctypes.c_int, wt.LPVOID, wt.DWORD, ctypes.POINTER(wt.DWORD)]
u.CloseDesktop.argtypes = [wt.HANDLE]
taskbar = u.FindWindowW('Shell_TrayWnd', None)
results = {'exe': str(Path(args.exe).resolve()), 'checks': [], 'physical': 'not-run'}
proc = None
main = None
original_cursor = wt.POINT()
u.GetCursorPos(ctypes.byref(original_cursor))
original_foreground = u.GetForegroundWindow()


def check(name, condition, **evidence):
    results['checks'].append({'name': name, 'passed': bool(condition), **evidence})
    print('PASS' if condition else 'FAIL', name, evidence or '', flush=True)
    if not condition:
        raise AssertionError(name)


def rect(hwnd):
    r = wt.RECT()
    assert u.GetWindowRect(hwnd, ctypes.byref(r))
    return [r.left, r.top, r.right, r.bottom]


def capsule_handle():
    after = None
    while True:
        after = u.FindWindowExW(taskbar, after, None, 'QuotaPeek Taskbar Capsule')
        if not after:
            return None
        pid = wt.DWORD()
        u.GetWindowThreadProcessId(after, ctypes.byref(pid))
        if pid.value == proc.pid:
            return after


def capture(name):
    r = rect(taskbar)
    ImageGrab.grab(bbox=(r[0], r[1] - 130, r[2], r[3]), include_layered_windows=True).save(artifact / name)


def physical_click(button):
    # click_input() may focus the wrapper first; send only actual mouse input.
    b = button.rectangle()
    point = (b.left + b.width() // 2, b.top + b.height() // 2)
    mouse.press(coords=point)
    mouse.release(coords=point)


def enable_docking():
    main.child_window(auto_id='ExpandButton', control_type='Button').invoke()
    main.child_window(auto_id='SettingsButton', control_type='Button').invoke()
    settings = main.child_window(title='QuotaPeek 设置', control_type='Window')
    settings.wait('visible', timeout=10)
    settings = Desktop(backend='uia').window(handle=settings.handle)
    box = settings.child_window(auto_id='TaskbarDockCheck', control_type='CheckBox')
    if box.get_toggle_state() == 0:
        box.toggle()
    settings.child_window(auto_id='SavePreferencesButton', control_type='Button').invoke()
    wait_until(15, .1, lambda: bool(capsule_handle()))
    settings_handle = settings.handle
    settings.close()
    wait_until(5, .1, lambda: not u.IsWindow(settings_handle))


def start():
    global proc, main
    proc = subprocess.Popen([str(Path(args.exe).resolve()), '--data-dir', str(artifact)], creationflags=subprocess.CREATE_NO_WINDOW)
    app = Application(backend='uia').connect(process=proc.pid, timeout=20)
    handles = []
    @enum_callback
    def find_main(hwnd, _):
        pid = wt.DWORD()
        u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == proc.pid:
            title = ctypes.create_unicode_buffer(256)
            u.GetWindowTextW(hwnd, title, len(title))
            if title.value == 'QuotaPeek':
                handles.append(hwnd)
        return True
    def ready():
        handles.clear()
        u.EnumWindows(find_main, 0)
        return bool(handles)
    wait_until(20, .1, ready)
    main = app.window(handle=handles[0])


def stop():
    u.PostMessageW(main.handle, 0x10, 0, 0)
    proc.wait(timeout=10)
    check('clean exit removes taskbar child', proc.returncode == 0 and not capsule_handle())


try:
    start()
    main.wait('visible', timeout=15)
    wait_until(10, .1, lambda: rect(main.handle)[0:2] == [240, 620])
    original_position = rect(main.handle)[0:2]
    enable_docking()
    wait_until(5, .1, lambda: not u.IsWindowVisible(main.handle))
    hwnd = capsule_handle()
    capsule = Desktop(backend='uia').window(handle=hwnd)
    capsule.child_window(auto_id='TaskbarExpandButton', control_type='Button').wait('visible', timeout=10)
    check('real child of Shell_TrayWnd', u.GetParent(hwnd) == taskbar and bool(u.GetWindowLongPtrW(hwnd, -16) & 0x40000000))
    check('capsule does not activate or add an app button', u.GetWindowLongPtrW(hwnd, -20) & 0x08000080 == 0x08000080 and not u.GetWindowLongPtrW(hwnd, -20) & 0x40000)
    r, t = rect(hwnd), rect(taskbar)
    check('capsule fully inside left half of taskbar', t[0] <= r[0] < r[2] <= (t[0] + t[2]) // 2 and t[1] <= r[1] < r[3] <= t[3], capsule=r, taskbar=t)
    check('capsule and Explorer use same DPI', u.GetDpiForWindow(hwnd) == u.GetDpiForWindow(taskbar), dpi=u.GetDpiForWindow(hwnd))
    overlap = []
    for button in Desktop(backend='uia').window(handle=taskbar).descendants(control_type='Button'):
        if button.element_info.process_id == proc.pid:
            continue
        b = button.rectangle()
        if max(r[0], b.left) < min(r[2], b.right) and max(r[1], b.top) < min(r[3], b.bottom):
            overlap.append(button.element_info.automation_id)
    check('capsule avoids all existing taskbar buttons', not overlap, overlapping=overlap)
    check('live balance reaches embedded view', '$10.45' in capsule.child_window(auto_id='BalanceText').window_text())
    capture('embedded.png')

    desktop = u.OpenInputDesktop(0, False, 1)
    name = ctypes.create_unicode_buffer(128)
    needed = wt.DWORD()
    physical = bool(desktop and u.GetUserObjectInformationW(desktop, 2, name, ctypes.sizeof(name), ctypes.byref(needed)) and name.value == 'Default')
    if desktop:
        u.CloseDesktop(desktop)
    button = capsule.child_window(auto_id='TaskbarExpandButton', control_type='Button')
    if physical and original_foreground and u.IsWindow(original_foreground):
        u.SetForegroundWindow(original_foreground)
        wait_until(5, .1, lambda: u.GetForegroundWindow() == original_foreground)
    before = u.GetForegroundWindow()
    if physical:
        physical_click(button)
    else:
        button.invoke()
    wait_until(5, .1, lambda: bool(u.IsWindowVisible(main.handle)))
    main.child_window(auto_id='SettingsButton', control_type='Button').wait('visible', timeout=5)
    check('click opens card above taskbar', rect(main.handle)[3] <= t[1], popup=rect(main.handle))
    if physical:
        wait_until(2, .025, lambda: u.GetForegroundWindow() == before)
        check('physical taskbar click preserves foreground', u.GetForegroundWindow() == before, before=before, after=u.GetForegroundWindow())
        physical_click(button)
    else:
        button.invoke()
    wait_until(5, .1, lambda: not u.IsWindowVisible(main.handle))
    check('second click closes card and keeps embedded capsule', bool(u.IsWindowVisible(hwnd)))
    results['physical'] = 'passed' if physical else 'environment-blocked'

    # Closing just our child HWND simulates losing the embedded surface. Explorer is untouched.
    u.PostMessageW(hwnd, 0x10, 0, 0)
    wait_until(10, .1, lambda: bool(capsule_handle()) and capsule_handle() != hwnd)
    check('lost child surface recreated without exiting main app', proc.poll() is None)
    capsule = Desktop(backend='uia').window(handle=capsule_handle())
    capsule.child_window(auto_id='TaskbarMenuButton', control_type='Button').invoke()
    Desktop(backend='uia').window(title='切回自由悬浮', control_type='MenuItem', process=proc.pid, top_level_only=False).invoke()
    wait_until(5, .1, lambda: not capsule_handle() and bool(u.IsWindowVisible(main.handle)))
    # Opening settings expanded the floating card; remember its anchored coordinates.
    current = json.loads(settings_path.read_text(encoding='utf-8-sig'))
    check('undock restores saved floating position', rect(main.handle)[:2] == [current['LeftPixels'], current['TopPixels']])
    check('undock preference persisted', current['TaskbarDocked'] is False)
    collapse_panel(main)
    check('floating capsule returns to its original position', rect(main.handle)[:2] == original_position)
    enable_docking()
    before_restart = json.loads(settings_path.read_text(encoding='utf-8-sig'))
    stop()
    start()
    wait_until(15, .1, lambda: bool(capsule_handle()))
    check('restart restores embedded capsule with hidden popup', not u.IsWindowVisible(main.handle))
    after_restart = json.loads(settings_path.read_text(encoding='utf-8-sig'))
    check('restart keeps floating coordinates and provider configuration', all(before_restart[k] == after_restart[k] for k in ['LeftPixels', 'TopPixels', 'StartExpanded', 'Providers']))
    capture('restart.png')
    stop()
except Exception as error:
    results['error'] = repr(error)
    capture('failure.png')
    raise
finally:
    if proc and proc.poll() is None:
        if main:
            try:
                u.PostMessageW(main.handle, 0x10, 0, 0)
                proc.wait(timeout=5)
            except Exception:
                proc.kill(); proc.wait()
    u.SetCursorPos(original_cursor.x, original_cursor.y)
    (artifact / 'result.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    print('ARTIFACTS', artifact, flush=True)
