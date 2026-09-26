"""Verify off-screen recovery, capsule position restoration and clean process exit."""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
from pathlib import Path
import subprocess
import time
from pywinauto import Application
from pywinauto.timings import wait_until

parser = argparse.ArgumentParser()
parser.add_argument('--exe', default='dist/QuotaPeek.exe')
args = parser.parse_args()

root = Path(__file__).resolve().parents[1]
artifact = root / '.artifacts' / ('lifecycle-' + time.strftime('%Y%m%d-%H%M%S'))
artifact.mkdir(parents=True)
settings_path = artifact / 'settings.json'
settings_path.write_text(json.dumps({'Version': 1, 'Providers': [], 'LeftPixels': 999999, 'TopPixels': 999999, 'StartExpanded': False, 'AutoHideFullscreen': False}), encoding='utf-8')
u = ctypes.WinDLL('user32', use_last_error=True)
u.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
u.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.UINT]
u.MonitorFromWindow.argtypes = [wt.HWND, wt.DWORD]; u.MonitorFromWindow.restype = wt.HANDLE
u.GetDpiForWindow.argtypes = [wt.HWND]; u.GetDpiForWindow.restype = wt.UINT


class MonitorInfo(ctypes.Structure):
    _fields_ = [('cbSize', wt.DWORD), ('rcMonitor', wt.RECT), ('rcWork', wt.RECT), ('dwFlags', wt.DWORD)]


u.GetMonitorInfoW.argtypes = [wt.HANDLE, ctypes.POINTER(MonitorInfo)]
results = []
position = None
for phase in range(2):
    p = subprocess.Popen([str(Path(args.exe).resolve()), '--data-dir', str(artifact)], creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        a = Application(backend='uia').connect(process=p.pid, timeout=20)
        w = a.window(title='QuotaPeek', control_type='Window'); w.wait('visible', timeout=15)
        w.child_window(auto_id='ExpandButton', control_type='Button').wait('visible', timeout=10)
        wait_until(10, .1, lambda: w.rectangle().width() == round(252 * u.GetDpiForWindow(w.handle) / 96))
        rect = w.rectangle()
        info = MonitorInfo(); info.cbSize = ctypes.sizeof(info)
        assert u.GetMonitorInfoW(u.MonitorFromWindow(w.handle, 2), ctypes.byref(info))
        work = info.rcWork
        if phase == 0:
            assert rect.left >= work.left and rect.top >= work.top and rect.right <= work.right and rect.bottom <= work.bottom
            results.append('off-screen saved position recovered inside work area')
            position = (work.left + 40, work.bottom - rect.height() - 40)
            assert u.SetWindowPos(w.handle, None, *position, 0, 0, 0x1 | 0x4 | 0x10)
        else:
            assert abs(rect.left-position[0]) <= 2 and abs(rect.top-position[1]) <= 2, (rect, position)
            results.append('capsule position and collapsed state restored without drift')
        u.PostMessageW(w.handle, 0x10, 0, 0)
        p.wait(timeout=10)
        assert p.returncode == 0
        results.append('clean shutdown ' + str(phase+1))
    finally:
        if p.poll() is None: p.kill(); p.wait()
(artifact/'result.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
print(json.dumps(results, indent=2))
print('ARTIFACTS', artifact)
