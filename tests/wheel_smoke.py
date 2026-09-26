"""Physical wheel checks on isolated packaged floating and taskbar capsules."""
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

parser = argparse.ArgumentParser()
parser.add_argument('--exe', default='dist/QuotaPeek.exe')
args = parser.parse_args()
artifact = Path(__file__).resolve().parents[1] / '.artifacts' / ('wheel-' + time.strftime('%Y%m%d-%H%M%S'))
artifact.mkdir(parents=True)
settings_path = artifact / 'settings.json'
settings_path.write_text(json.dumps({
    'Version': 1, 'StartExpanded': False, 'LeftPixels': 240, 'TopPixels': 620,
    'Notifications': False, 'AutoHideFullscreen': False,
    'Providers': [
        {'Id': 'wallet-a', 'Name': '钱包 A', 'Type': 'HoneWallet', 'LowThreshold': 100},
        {'Id': 'disabled', 'Name': '停用账户', 'Type': 'Manual', 'Enabled': False},
        {'Id': 'wallet-b', 'Name': '钱包 B', 'Type': 'HoneWallet'},
        {'Id': 'codex', 'Name': 'Codex', 'Type': 'Codex', 'LowThreshold': 15},
    ],
}), encoding='utf-8')

u = ctypes.WinDLL('user32', use_last_error=True)
u.GetForegroundWindow.restype = wt.HWND
u.SetForegroundWindow.argtypes = [wt.HWND]
u.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
u.FindWindowW.argtypes = [wt.LPCWSTR, wt.LPCWSTR]; u.FindWindowW.restype = wt.HWND
u.FindWindowExW.argtypes = [wt.HWND, wt.HWND, wt.LPCWSTR, wt.LPCWSTR]; u.FindWindowExW.restype = wt.HWND
u.GetDpiForWindow.argtypes = [wt.HWND]; u.GetDpiForWindow.restype = wt.UINT
u.IsWindow.argtypes = [wt.HWND]
u.IsWindowVisible.argtypes = [wt.HWND]
u.GetCursorPos.argtypes = [ctypes.POINTER(wt.POINT)]
u.WindowFromPoint.argtypes = [wt.POINT]; u.WindowFromPoint.restype = wt.HWND
u.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
u.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
u.mouse_event.argtypes = [wt.DWORD, wt.DWORD, wt.DWORD, wt.DWORD, ctypes.c_size_t]
u.OpenInputDesktop.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]; u.OpenInputDesktop.restype = wt.HANDLE
u.GetUserObjectInformationW.argtypes = [wt.HANDLE, ctypes.c_int, wt.LPVOID, wt.DWORD, ctypes.POINTER(wt.DWORD)]
u.CloseDesktop.argtypes = [wt.HANDLE]

results = {'exe': str(Path(args.exe).resolve()), 'checks': [], 'physical': 'not-run'}
proc = main = surface = None
main_handle = None
label = button = label_surface = None
surface_handle = None
embedded = False
cursor = wt.POINT()
u.GetCursorPos(ctypes.byref(cursor))
foreground = u.GetForegroundWindow()
taskbar = u.FindWindowW('Shell_TrayWnd', None)


def check(name, condition, **evidence):
    results['checks'].append({'name': name, 'passed': bool(condition), **evidence})
    print('PASS' if condition else 'FAIL', name, evidence or '', flush=True)
    if not condition:
        raise AssertionError(name)


def bind_surface():
    global label, button, label_surface, surface_handle
    if label_surface is not surface:
        label = surface.child_window(auto_id='ProviderText' if embedded else 'CapsuleText').wrapper_object()
        button = surface.child_window(auto_id='TaskbarExpandButton' if embedded else 'ExpandButton').wrapper_object()
        surface_handle = surface.handle
        label_surface = surface


def text():
    bind_surface()
    return label.window_text()


def expect(name):
    wait_until(5, .05, lambda: text().startswith(name))


def centre(control):
    p = control.rectangle().mid_point()
    return p.x, p.y


def stable(name, expected, duration=.25):
    deadline = time.monotonic() + duration
    while time.monotonic() < deadline:
        actual = text()
        if not actual.startswith(expected):
            check(name, False, actual=actual)
        time.sleep(.04)
    check(name, True, value=text())


def wheel(delta, point=None):
    bind_surface()
    point = point or centre(button)
    mouse.move(coords=point)
    actual = wt.POINT()
    u.GetCursorPos(ctypes.byref(actual))
    if abs(actual.x - point[0]) > 3 or abs(actual.y - point[1]) > 3:
        results['physical'] = 'environment-blocked: external pointer movement'
        raise RuntimeError(results['physical'])
    target = u.WindowFromPoint(actual)
    results.setdefault('inputs', []).append({'time': time.time(), 'delta': delta, 'point': point,
                                            'target': target, 'surface': surface_handle})
    if target != surface_handle:
        results['physical'] = 'environment-blocked: capsule is covered or moved'
        raise RuntimeError(results['physical'])
    u.mouse_event(0x800, 0, 0, delta & 0xffffffff, 0)


def cycle(name, delta, expected, point=None):
    before = u.GetForegroundWindow()
    wheel(delta, point)
    expect(expected)
    check(name, u.GetForegroundWindow() == before, value=text())


def capsule_handle():
    child = None
    while True:
        child = u.FindWindowExW(taskbar, child, None, 'QuotaPeek Taskbar Capsule')
        if not child:
            return None
        pid = wt.DWORD()
        u.GetWindowThreadProcessId(child, ctypes.byref(pid))
        if pid.value == proc.pid:
            return child


def screenshot(name):
    r = surface.rectangle()
    ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom), include_layered_windows=True).save(artifact / name)


def open_settings():
    if not main.child_window(auto_id='SettingsButton').exists(timeout=.2):
        surface.child_window(auto_id='TaskbarExpandButton' if embedded else 'ExpandButton').invoke()
    main.child_window(auto_id='SettingsButton').wait('visible', timeout=5)
    main.child_window(auto_id='SettingsButton').invoke()
    settings = main.child_window(title='QuotaPeek 设置', control_type='Window')
    settings.wait('visible', timeout=10)
    return Desktop(backend='uia').window(handle=settings.handle)


def close_settings(settings):
    handle = settings.handle
    settings.close()
    wait_until(5, .05, lambda: not u.IsWindow(handle))
    if foreground and u.IsWindow(foreground):
        u.SetForegroundWindow(foreground)


try:
    desktop = u.OpenInputDesktop(0, False, 1)
    name = ctypes.create_unicode_buffer(128)
    needed = wt.DWORD()
    available = bool(desktop and u.GetUserObjectInformationW(desktop, 2, name, ctypes.sizeof(name), ctypes.byref(needed)))
    if desktop:
        u.CloseDesktop(desktop)
    if not available or name.value != 'Default':
        results['physical'] = 'environment-blocked: interactive desktop unavailable'
        raise RuntimeError(results['physical'])
    proc = subprocess.Popen([str(Path(args.exe).resolve()), '--demo', '--data-dir', str(artifact)], creationflags=subprocess.CREATE_NO_WINDOW)
    app = Application(backend='uia').connect(process=proc.pid, timeout=20)
    main = app.window(title='QuotaPeek')
    main.wait('visible', timeout=15)
    main_handle = main.handle
    surface = main
    results['physical'] = 'running'
    scale = u.GetDpiForWindow(main.handle) / 96
    results['dpi'] = int(scale * 96)
    expect('钱包 A')
    cycle('floating wheel down skips disabled account without focus', -120, '钱包 B')
    cycle('floating wheel up returns to previous wallet', 120, '钱包 A')
    cycle('floating wheel up wraps to Codex', 120, 'Codex')
    check('Codex uses percentage rather than wallet currency', '54%' in text())
    cycle('floating wheel down wraps to first wallet', -120, '钱包 A')
    r = main.rectangle()
    cycle('floating status dot accepts wheel', -120, '钱包 B', (r.left + round(30 * scale), r.top + round(32 * scale)))
    cycle('floating padding accepts wheel', -120, 'Codex', (r.left + round(125 * scale), r.top + round(11 * scale)))
    cycle('floating grip accepts wheel', -120, '钱包 A', (r.right - round(25 * scale), r.top + round(32 * scale)))
    wheel(-60)
    stable('half notch does not change wallet', '钱包 A')
    cycle('two half notches change exactly one wallet', -60, '钱包 B')
    wheel(-60)
    wheel(60)
    stable('direction reversal starts a fresh partial notch', '钱包 B')
    cycle('reversed partial notch completes normally', 60, '钱包 A')
    cycle('combined two-notch event advances twice', -240, 'Codex')
    stable('selection survives periodic refresh', 'Codex', 16)
    screenshot('floating-codex.png')
    main.child_window(auto_id='ExpandButton').invoke()
    main.child_window(auto_id='CollapseButton').wait('visible', timeout=5)
    r = main.child_window(auto_id='CardsScroll').rectangle()
    mouse.scroll(coords=(r.left + 50, r.top + 50), wheel_dist=-1)
    main.child_window(auto_id='CollapseButton').invoke()
    stable('expanded card wheel leaves capsule selection unchanged', 'Codex')

    settings = open_settings()
    settings.child_window(auto_id='SavePreferencesButton').invoke()
    close_settings(settings)
    main.child_window(auto_id='CollapseButton').invoke()
    stable('settings apply preserves selected provider by ID', 'Codex')
    settings = open_settings()
    # WPF's ListBoxItem automation name can be the record's ToString(), while
    # DisplayMemberPath renders the visible name. Select the known fixture row.
    settings.child_window(auto_id='ProviderList').get_item(3).select()
    wait_until(5, .05, lambda: settings.child_window(auto_id='NameInput').get_value() == 'Codex')
    settings.child_window(auto_id='EnabledCheck').toggle()
    settings.child_window(auto_id='SaveButton').invoke()
    close_settings(settings)
    main.child_window(auto_id='CollapseButton').invoke()
    expect('钱包 A')
    check('disabled selected provider falls back safely', text().startswith('钱包 A'))
    cycle('cycle excludes newly disabled Codex', 120, '钱包 B')

    settings = open_settings()
    settings.child_window(auto_id='TaskbarDockCheck').toggle()
    settings.child_window(auto_id='SavePreferencesButton').invoke()
    wait_until(15, .1, lambda: bool(capsule_handle()))
    close_settings(settings)
    embedded = True
    surface = Desktop(backend='uia').window(handle=capsule_handle())
    expect('钱包 B')
    cycle('taskbar hover wheel changes wallet without activating', -120, '钱包 A')
    cycle('taskbar menu button accepts wheel', 120, '钱包 B', centre(surface.child_window(auto_id='TaskbarMenuButton')))
    cycle('taskbar value text accepts wheel', -120, '钱包 A', centre(surface.child_window(auto_id='BalanceText')))
    check('wheel keeps taskbar popup collapsed', not u.IsWindowVisible(main_handle))
    surface.child_window(auto_id='TaskbarExpandButton').invoke()
    wait_until(5, .05, lambda: bool(u.IsWindowVisible(main_handle)))
    cycle('taskbar wheel also works while cards are expanded', -120, '钱包 B')
    surface.child_window(auto_id='TaskbarExpandButton').invoke()
    wait_until(5, .05, lambda: not u.IsWindowVisible(main_handle))
    screenshot('taskbar-wallet.png')
    results['physical'] = 'passed'
except Exception as error:
    if results['physical'] == 'running':
        results['physical'] = 'failed'
    results['error'] = repr(error)
    if surface:
        try:
            screenshot('failure.png')
        except Exception:
            pass
    raise
finally:
    if proc and proc.poll() is None:
        try:
            u.PostMessageW(main_handle, 0x10, 0, 0)
            proc.wait(timeout=8)
        except Exception:
            proc.kill(); proc.wait()
    u.SetCursorPos(cursor.x, cursor.y)
    (artifact / 'result.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    print('ARTIFACTS', artifact, flush=True)
