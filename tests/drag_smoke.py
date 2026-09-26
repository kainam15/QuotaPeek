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
from outside_click_target import collapse_panel
from PIL import ImageGrab


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
u.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
u.GetWindowThreadProcessId.restype = wt.DWORD


class GuiInfo(ctypes.Structure):
    _fields_ = [("size", wt.DWORD), ("flags", wt.DWORD), ("active", wt.HWND),
                ("focus", wt.HWND), ("capture", wt.HWND), ("menu", wt.HWND),
                ("move", wt.HWND), ("caret", wt.HWND), ("rect", wt.RECT)]


u.GetGUIThreadInfo.argtypes = [wt.DWORD, ctypes.POINTER(GuiInfo)]
u.GetAsyncKeyState.argtypes = [ctypes.c_int]
u.GetAsyncKeyState.restype = ctypes.c_short
results = {"exe": str(Path(args.exe).resolve()), "checks": [], "physical": "not-run"}
proc = None
widget = None
mouse_down = False
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


def captured():
    info = GuiInfo()
    info.size = ctypes.sizeof(info)
    thread = u.GetWindowThreadProcessId(widget.handle, None)
    return bool(u.GetGUIThreadInfo(thread, ctypes.byref(info)) and info.capture == widget.handle)


class InputInterference(RuntimeError):
    pass


def ensure_pointer(point, held=False):
    actual = wt.POINT()
    u.GetCursorPos(ctypes.byref(actual))
    if (abs(actual.x - point[0]) > 2 or abs(actual.y - point[1]) > 2
            or held and not u.GetAsyncKeyState(1) & 0x8000):
        raise InputInterference("External mouse input interrupted the test; run on an idle desktop.")


def press(point):
    global mouse_down
    u.SetCursorPos(*point)
    u.mouse_event(0x2, 0, 0, 0, 0)
    mouse_down = True
    wait_until(3, .025, captured)
    ensure_pointer(point, held=True)


def release():
    global mouse_down
    if mouse_down:
        u.mouse_event(0x4, 0, 0, 0, 0)
        mouse_down = False


def screenshot(path):
    ImageGrab.grab(bbox=tuple(bounds()), include_layered_windows=True).save(path)


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
    # Send the press immediately instead of pywinauto's double-click delay.
    press(point)
    try:
        # Holding alone must not expand or resize the drag target.
        time.sleep(hold)
        ensure_pointer(point, held=True)
        held = bounds()
        held_cursor = wt.POINT()
        u.GetCursorPos(ctypes.byref(held_cursor))
        for step in range(1, steps + 1):
            target = (point[0] + delta[0] * step // steps, point[1] + delta[1] * step // steps)
            u.SetCursorPos(*target)
            time.sleep(.03)
            ensure_pointer(target, held=True)
        wait_until(3, .025, lambda: abs(bounds()[0] - before[0] - delta[0]) <= 8
                   and abs(bounds()[1] - before[1] - delta[1]) <= 8)
        during = bounds()
    finally:
        release()
    time.sleep(.45)
    after = bounds()
    check(name, held[2] - held[0] == before[2] - before[0]
          and held[3] - held[1] == before[3] - before[1]
          # Allow a few physical pixels of concurrent pointer movement while
          # Windows delivers the press; a stationary window still fails.
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
    press((point.x, point.y))
    time.sleep(.05)
    u.SetCursorPos(point.x + 1, point.y + 1)
    release()
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
    collapse_panel(widget)
    parked()
    wait_until(5, .05, lambda: widget.rectangle().width() == capsule_width)
    check("outside click collapses panel", True)

    # Hovering never expands; clicking even a non-button area of the capsule does.
    point = widget.child_window(auto_id="CapsuleText").rectangle().mid_point()
    mouse.move(coords=(point.x, point.y))
    for _ in range(10):
        time.sleep(.1)
        ensure_pointer((point.x, point.y))
    check("idle hover keeps capsule collapsed", widget.rectangle().width() == capsule_width)
    parked()
    time.sleep(.9)
    check("leaving capsule keeps it collapsed", widget.rectangle().width() == capsule_width)
    r = widget.rectangle()
    press((r.left + round(30 * scale), r.top + round(32 * scale)))
    time.sleep(.06)
    release()
    wait_until(5, .05, lambda: widget.rectangle().width() > capsule_width)
    parked()
    time.sleep(1)
    check("clicking capsule dot expands and leaving keeps cards open",
          widget.rectangle().width() > capsule_width)
    collapse_panel(widget)
    wait_until(5, .05, lambda: widget.rectangle().width() == capsule_width)

    position = bounds()
    screenshot(artifact / "capsule.png")
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
    if isinstance(error, InputInterference):
        results["result"] = "blocked"
        results["physical"] = "environment-blocked: external mouse input"
    results["error"] = str(error)
    if widget is not None:
        try:
            screenshot(artifact / "failure.png")
        except Exception:
            pass
    raise
finally:
    release()
    try:
        close()
    except Exception:
        if proc is not None and proc.poll() is None:
            proc.kill()  # Only the isolated process launched by this test.
            proc.wait(timeout=5)
    if results.get("result") != "blocked":
        mouse.move(coords=(original_cursor.x, original_cursor.y))
    (artifact / "result.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print("ARTIFACTS", artifact, flush=True)
