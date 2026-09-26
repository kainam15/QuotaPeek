"""Physical mouse regression checks for the packaged floating widget."""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
from pathlib import Path
import subprocess
import time

from pywinauto import Application, mouse
from pywinauto.timings import wait_until


parser = argparse.ArgumentParser()
parser.add_argument("--exe", default="dist/QuotaPeek.exe")
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
artifact = root / ".artifacts" / ("drag-" + time.strftime("%Y%m%d-%H%M%S"))
artifact.mkdir(parents=True)
data_dir = artifact / "data"
data_dir.mkdir()
settings_path = data_dir / "settings.json"
settings_path.write_text(json.dumps({
    "Version": 1, "StartExpanded": False, "LeftPixels": 240, "TopPixels": 620,
    "Notifications": False, "AutoHideFullscreen": False,
    "Providers": [{"Id": "drag-check", "Name": "拖动验证", "Type": "Manual",
                   "ManualRemaining": 10.45, "LowThreshold": 0}],
}), encoding="utf-8")
u = ctypes.WinDLL("user32", use_last_error=True)
u.GetForegroundWindow.restype = wt.HWND
u.GetDpiForWindow.argtypes = [wt.HWND]
u.GetDpiForWindow.restype = wt.UINT
u.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
u.OpenInputDesktop.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
u.OpenInputDesktop.restype = wt.HANDLE
u.GetUserObjectInformationW.argtypes = [wt.HANDLE, ctypes.c_int, wt.LPVOID, wt.DWORD, ctypes.POINTER(wt.DWORD)]
u.CloseDesktop.argtypes = [wt.HANDLE]
u.GetCursorPos.argtypes = [ctypes.POINTER(wt.POINT)]
u.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
u.mouse_event.argtypes = [wt.DWORD, wt.DWORD, wt.DWORD, wt.DWORD, ctypes.c_size_t]
results = {"exe": str(Path(args.exe).resolve()), "checks": [], "physical": "not-run"}
proc = None
widget = None
original_cursor = wt.POINT()
u.GetCursorPos(ctypes.byref(original_cursor))


def check(name, condition, **evidence):
    results["checks"].append({"name": name, "passed": bool(condition), **evidence})
    print("PASS" if condition else "FAIL", name, evidence or "", flush=True)
    if not condition:
        raise AssertionError(name)


def bounds():
    r = widget.rectangle()
    return [r.left, r.top, r.right, r.bottom]


def parked():
    u.SetCursorPos(20, 20)


def launch():
    global proc, widget
    parked()
    proc = subprocess.Popen([results["exe"], "--demo", "--data-dir", str(data_dir)],
                            creationflags=subprocess.CREATE_NO_WINDOW)
    app = Application(backend="uia").connect(process=proc.pid, timeout=20)
    widget = app.window(title="QuotaPeek", control_type="Window")
    widget.wait("exists visible", timeout=15)
    widget = app.window(handle=widget.handle)
    widget.child_window(auto_id="ExpandButton", control_type="Button").wait("visible", timeout=10)
    wait_until(20, .1, lambda: widget.rectangle().width() == round(252 * u.GetDpiForWindow(widget.handle) / 96))


def close():
    if proc is not None and proc.poll() is None:
        u.PostMessageW(widget.handle, 0x10, 0, 0)
        proc.wait(timeout=8)
        check("clean shutdown", proc.returncode == 0)


def drag(name, point, delta=(100, 60), hold=.55, steps=8):
    before = bounds()
    foreground = u.GetForegroundWindow()
    # Send the press immediately: pywinauto's double-click delay otherwise lets
    # the capsule expand on hover before the physical button goes down.
    u.SetCursorPos(*point)
    u.mouse_event(0x2, 0, 0, 0, 0)
    try:
        # Holding beyond the 350 ms hover delay must not resize the drag target.
        time.sleep(hold)
        held = bounds()
        held_cursor = wt.POINT()
        u.GetCursorPos(ctypes.byref(held_cursor))
        for step in range(1, steps + 1):
            u.SetCursorPos(point[0] + delta[0] * step // steps, point[1] + delta[1] * step // steps)
            time.sleep(.03)
        during = bounds()
    finally:
        u.mouse_event(0x4, 0, 0, 0, 0)
    time.sleep(.45)
    after = bounds()
    check(name, held[2] - held[0] == before[2] - before[0]
          and held[3] - held[1] == before[3] - before[1]
          # Allow a few physical pixels of concurrent pointer movement while
          # Windows delivers the press; a missing drag still fails by 50+ px.
          and abs(after[0] - before[0] - delta[0]) <= 8
          and abs(after[1] - before[1] - delta[1]) <= 8
          and after[2] - after[0] == before[2] - before[0]
          and after[3] - after[1] == before[3] - before[1],
          before=before, press=list(point), held_cursor=[held_cursor.x, held_cursor.y], held=held, during=during, after=after)
    check(name + " preserves foreground", u.GetForegroundWindow() == foreground)
    parked()
    saved = json.loads(settings_path.read_text(encoding="utf-8-sig"))
    check(name + " saves position on release",
          abs(saved["LeftPixels"] - after[0]) <= 2 and abs(saved["TopPixels"] - after[1]) <= 2)


try:
    desktop = u.OpenInputDesktop(0, False, 1)
    desktop_name = ctypes.create_unicode_buffer(256)
    needed = wt.DWORD()
    available = bool(desktop and u.GetUserObjectInformationW(
        desktop, 2, desktop_name, ctypes.sizeof(desktop_name), ctypes.byref(needed)))
    if desktop:
        u.CloseDesktop(desktop)
    if not available or desktop_name.value != "Default":
        results["physical"] = "environment-blocked: interactive desktop unavailable"
        raise RuntimeError(results["physical"])
    results["physical"] = "running"
    launch()
    scale = u.GetDpiForWindow(widget.handle) / 96
    results["dpi"] = round(scale * 96)
    capsule_width = widget.rectangle().width()
    text = widget.child_window(auto_id="CapsuleText").rectangle().mid_point()
    drag("capsule text drags without expanding", (text.x, text.y))

    r = widget.rectangle()
    drag("capsule status dot drags", (r.left + round(30 * scale), r.top + round(32 * scale)), (-80, 40))
    r = widget.rectangle()
    drag("capsule padding drags", (r.left + round(125 * scale), r.top + round(11 * scale)), (60, -45))
    r = widget.rectangle()
    drag("capsule grip drags", (r.right - round(25 * scale), r.top + round(32 * scale)), (-50, -35))
    point = widget.child_window(auto_id="CapsuleText").rectangle().mid_point()
    drag("fast movement outside old bounds keeps capture", (point.x, point.y), (280, 180), .1, steps=1)

    # A small click jitter is still a click, and must not shift the saved anchor.
    before = bounds()
    point = widget.child_window(auto_id="CapsuleText").rectangle().mid_point()
    u.SetCursorPos(point.x, point.y)
    u.mouse_event(0x2, 0, 0, 0, 0)
    time.sleep(.05)
    u.SetCursorPos(point.x + 1, point.y + 1)
    u.mouse_event(0x4, 0, 0, 0, 0)
    wait_until(5, .05, lambda: widget.rectangle().width() > capsule_width)
    parked()
    time.sleep(1)
    after = bounds()
    check("text click with tiny jitter pins expanded cards",
          widget.rectangle().width() > capsule_width and before[2:] == after[2:]
          and json.loads(settings_path.read_text(encoding="utf-8-sig"))["StartExpanded"])

    header = widget.child_window(auto_id="Subtitle").rectangle().mid_point()
    drag("expanded title drags", (header.x, header.y), (55, 40), .1)
    card = widget.child_window(auto_id="CardsScroll").rectangle()
    drag("expanded card background drags", (card.left + 12, card.top + 60), (20, 20), .1)

    widget.child_window(auto_id="SettingsButton", control_type="Button").click_input()
    settings = widget.child_window(title="QuotaPeek 设置", control_type="Window")
    settings.wait("visible", timeout=5)
    check("settings button still clicks", settings.is_visible())
    settings.close()
    widget.child_window(auto_id="CollapseButton", control_type="Button").click_input()
    parked()
    wait_until(5, .05, lambda: widget.rectangle().width() == capsule_width)
    check("collapse button still clicks", True)

    # Existing idle-hover behavior must remain available after a drag.
    point = widget.child_window(auto_id="CapsuleText").rectangle().mid_point()
    mouse.move(coords=(point.x, point.y))
    wait_until(5, .05, lambda: widget.rectangle().width() > capsule_width)
    check("idle hover still expands", True)
    parked()
    wait_until(5, .05, lambda: widget.rectangle().width() == capsule_width)
    check("temporary hover expansion still collapses on leave", True)

    position = bounds()
    widget.capture_as_image().save(artifact / "capsule.png")
    close()
    launch()
    check("dragged position and collapsed state survive restart", bounds() == position,
          expected=position, actual=bounds())
    results["physical"] = "passed"
    results["result"] = "passed"
except Exception as error:
    results["result"] = "failed"
    if results["physical"] == "running":
        results["physical"] = "failed"
    results["error"] = str(error)
    if widget is not None:
        try:
            widget.capture_as_image().save(artifact / "failure.png")
        except Exception:
            pass
    raise
finally:
    try:
        close()
    except Exception:
        if proc is not None and proc.poll() is None:
            proc.kill()  # Only the isolated process launched by this test.
            proc.wait(timeout=5)
    mouse.move(coords=(original_cursor.x, original_cursor.y))
    (artifact / "result.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print("ARTIFACTS", artifact, flush=True)
