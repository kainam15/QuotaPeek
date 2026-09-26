"""Physical outside-click dismissal for a packaged EXE or the current taskbar instance."""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
from pathlib import Path
import subprocess
import time

from PIL import ImageGrab
from pywinauto import Desktop, mouse

from outside_click_target import OutsideClickTarget, require_input_desktop

parser = argparse.ArgumentParser()
parser.add_argument('--exe', default='dist/QuotaPeek.exe')
parser.add_argument('--pid', type=int, help='Check an existing taskbar instance without saving settings.')
parser.add_argument('--docked', action='store_true')
args = parser.parse_args()
artifact = Path(__file__).resolve().parents[1] / '.artifacts' / ('outside-click-' + time.strftime('%Y%m%d-%H%M%S'))
artifact.mkdir(parents=True)
u = ctypes.WinDLL('user32', use_last_error=True)
u.FindWindowW.argtypes = [wt.LPCWSTR, wt.LPCWSTR]; u.FindWindowW.restype = wt.HWND
u.FindWindowExW.argtypes = [wt.HWND, wt.HWND, wt.LPCWSTR, wt.LPCWSTR]; u.FindWindowExW.restype = wt.HWND
u.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
u.WindowFromPoint.argtypes = [wt.POINT]; u.WindowFromPoint.restype = wt.HWND
u.GetForegroundWindow.restype = wt.HWND
u.GetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int]; u.GetWindowLongPtrW.restype = ctypes.c_ssize_t
u.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
u.IsWindowVisible.argtypes = [wt.HWND]
u.IsWindow.argtypes = [wt.HWND]
u.GetDpiForWindow.argtypes = [wt.HWND]; u.GetDpiForWindow.restype = wt.UINT
results = {'exe': str(Path(args.exe).resolve()), 'pid': args.pid, 'docked': args.docked,
           'physical': 'not-run', 'checks': []}
target = None
proc = None
panel = None
settings = None
settings_handle = None
desktop = Desktop(backend='uia')
app_pid = args.pid


def check(name, passed=True):
    results['checks'].append({'name': name, 'passed': bool(passed)})
    print('PASS' if passed else 'FAIL', name, flush=True)
    if not passed:
        raise AssertionError(name)


def capsule_handle():
    shell = u.FindWindowW('Shell_TrayWnd', None)
    child = None
    while True:
        child = u.FindWindowExW(shell, child, None, 'QuotaPeek Taskbar Capsule')
        if not child:
            return None
        pid = wt.DWORD()
        u.GetWindowThreadProcessId(child, ctypes.byref(pid))
        if pid.value == app_pid:
            return child


def menus():
    found = []
    for window in Desktop(backend='win32').windows(process=app_pid, visible_only=True):
        if not window.window_text() and window.class_name().startswith('HwndWrapper'):
            menu = desktop.window(handle=window.handle)
            if menu.children(control_type='Menu'):
                found.append(menu)
    return found


def physical_click(control, button='left'):
    r = control.rectangle()
    point = (r.left + r.width() // 2, r.top + r.height() // 2)
    pid = wt.DWORD()
    u.GetWindowThreadProcessId(u.WindowFromPoint(wt.POINT(*point)), ctypes.byref(pid))
    if pid.value != app_pid:
        raise RuntimeError('environment-blocked: QuotaPeek control is obscured')
    # Give WPF time to establish capture before release (the capsule uses a
    # press/drag/release gesture rather than a Button.Click handler).
    mouse.press(button=button, coords=point)
    mouse.release(button=button, coords=point)


def expanded():
    return bool(u.IsWindowVisible(panel.handle)) and panel.rectangle().width() == round(370 * scale)


def expand():
    control = desktop.window(handle=capsule_handle()).child_window(auto_id='TaskbarExpandButton') if args.docked else panel.child_window(auto_id='ExpandButton')
    before = u.GetForegroundWindow()
    physical_click(control)
    try:
        target.wait(expanded)
    except AssertionError:
        print('EXPAND STATE', {'bounds': str(panel.rectangle()), 'dpi': u.GetDpiForWindow(panel.handle),
                               'expected_scale': scale, 'visible': bool(u.IsWindowVisible(panel.handle)),
                               'buttons': [b.element_info.automation_id for b in panel.descendants(control_type='Button')]}, flush=True)
        raise
    check('capsule click expands without taking focus', u.GetForegroundWindow() == before)


def collapsed():
    return not u.IsWindowVisible(panel.handle) if args.docked else panel.rectangle().width() == round(252 * scale)


def stable_expanded():
    end = time.monotonic() + .4
    while time.monotonic() < end:
        target.root.update()
        if not expanded():
            return False
        time.sleep(.03)
    return True


try:
    require_input_desktop()
    target = OutsideClickTarget()
    if not app_pid:
        data = artifact / 'data'
        data.mkdir()
        (data / 'settings.json').write_text(json.dumps({
            'TaskbarDocked': args.docked, 'StartExpanded': False, 'LeftPixels': 750, 'TopPixels': 300,
            'AutoHideFullscreen': False, 'Notifications': False,
            'Providers': [{'Id': 'outside-check', 'Name': '点击验证钱包', 'Type': 'Manual', 'ManualRemaining': 10.45, 'LowThreshold': 0}]
        }, ensure_ascii=False), encoding='utf-8')
        proc = subprocess.Popen([results['exe'], '--data-dir', str(data), '--render', str(artifact / 'render.png')],
                                creationflags=subprocess.CREATE_NO_WINDOW)
        app_pid = proc.pid
        results['pid'] = app_pid
    native_panel = Desktop(backend='win32').window(title='QuotaPeek', process=app_pid, visible_only=False)
    native_panel.wait('exists', timeout=20)
    panel = desktop.window(handle=native_panel.handle)
    scale = u.GetDpiForWindow(panel.handle) / 96
    if args.docked:
        target.wait(lambda: bool(capsule_handle()), timeout=20)
    if expanded():
        target.click(panel)
        target.wait(collapsed)
    target.click(panel)
    results['physical'] = 'running'
    expand()
    check('collapse arrow removed', not panel.child_window(auto_id='CollapseButton').exists(timeout=.2))
    check('panel keeps NOACTIVATE, TOPMOST and TOOLWINDOW',
          u.GetWindowLongPtrW(panel.handle, -20) & 0x8000088 == 0x8000088)
    physical_click(panel.child_window(auto_id='Subtitle'))
    check('click inside header keeps panel open', stable_expanded())
    physical_click(panel.child_window(auto_id='CardsScroll'))
    check('click inside card keeps panel open', stable_expanded())
    r = panel.rectangle()
    mouse.move(coords=(target.root.winfo_rootx() + 60, target.root.winfo_rooty() + 60))
    check('moving outside without clicking keeps panel open', stable_expanded())
    ImageGrab.grab(bbox=(r.left, r.top, r.right, r.bottom), include_layered_windows=True).save(artifact / 'expanded.png')
    for button in ('left', 'right', 'middle'):
        before = target.presses
        target.click(panel, button)
        target.wait(collapsed)
        check(button + ' outside click dismisses and reaches target', target.presses == before + 1)
        expand()
    physical_click(panel.child_window(auto_id='Subtitle'), 'right')
    target.wait(lambda: bool(menus()))
    check('right-click menu keeps panel open', expanded())
    menu = menus()[0]
    # Popup placement may still move to fit the work area after its HWND appears.
    item = menu.child_window(title='设置', control_type='MenuItem')
    last = None
    settled = 0
    def menu_settled():
        global last, settled
        current = str(item.rectangle())
        settled = settled + 1 if current == last else 0
        last = current
        return settled >= 3
    target.wait(menu_settled)
    mr = menu.rectangle()
    ImageGrab.grab(bbox=(mr.left, mr.top, mr.right, mr.bottom), include_layered_windows=True).save(artifact / 'menu.png')
    # Let the taskbar's two-second placement refresh run with the menu open.
    deadline = time.monotonic() + 2.5
    while time.monotonic() < deadline:
        target.root.update()
        time.sleep(.03)
    ir = item.rectangle()
    hit = u.WindowFromPoint(wt.POINT(ir.left + ir.width() // 2, ir.top + ir.height() // 2))
    check('menu stays clickable across taskbar placement refresh', hit == menu.handle)
    physical_click(item)
    native_settings = Desktop(backend='win32').window(title='QuotaPeek 设置', process=app_pid)
    try:
        native_settings.wait('visible', timeout=10)
    except Exception:
        print('SETTINGS STATE', [{'title': w.window_text(), 'visible': w.is_visible(), 'handle': w.handle}
                                for w in Desktop(backend='win32').windows(process=app_pid, visible_only=False)], flush=True)
        raise
    settings = desktop.window(handle=native_settings.handle)
    settings_handle = settings.handle
    physical_click(settings.child_window(auto_id='NameInput'))
    check('menu item opens usable settings without dismissing panel', stable_expanded() and settings.is_visible())
    target.click(panel)
    check('external click does not hide owned settings', stable_expanded() and settings.is_visible())
    settings.close()
    target.wait(lambda: not u.IsWindow(settings_handle))
    settings = None
    physical_click(panel.child_window(auto_id='Subtitle'), 'right')
    target.wait(lambda: bool(menus()))
    target.click(panel)
    target.wait(lambda: collapsed() and not menus())
    check('one outside click closes menu and expanded panel')
    expand()
    if args.docked:
        physical_click(desktop.window(handle=capsule_handle()).child_window(auto_id='TaskbarExpandButton'))
        target.wait(collapsed)
        check('second capsule click stays closed', not u.IsWindowVisible(panel.handle))
        expand()
    target.click(panel)
    target.wait(collapsed)
    check('repeated reopen and dismiss works')
    check('process remains resident', proc is None or proc.poll() is None)
    results['physical'] = 'passed'
except BaseException as error:
    results['error'] = str(error)
    results['physical'] = 'environment-blocked' if 'environment-blocked' in str(error) else 'failed'
    raise
finally:
    if settings_handle and u.IsWindow(settings_handle):
        u.PostMessageW(settings_handle, 0x10, 0, 0)
    if proc and proc.poll() is None:
        if panel is not None:
            u.PostMessageW(panel.handle, 0x10, 0, 0)
        try:
            proc.wait(timeout=8)
            results['exit_code'] = proc.returncode
        except subprocess.TimeoutExpired:
            proc.kill(); proc.wait()
    if target:
        target.close()
    (artifact / 'result.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    print('ARTIFACTS', artifact, flush=True)
