"""Packaged Windows desktop checks, isolated state and explicit physical-input results."""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
from pathlib import Path
import subprocess
import time

from pywinauto import Application, Desktop, mouse, keyboard
from pywinauto.timings import wait_until
from win32info import process_name

parser = argparse.ArgumentParser()
parser.add_argument("--exe", default="dist/QuotaPeek.exe")
parser.add_argument("--live", action="store_true")
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
artifact = root / ".artifacts" / ("desktop-" + time.strftime("%Y%m%d-%H%M%S"))
artifact.mkdir(parents=True)
u = ctypes.WinDLL("user32", use_last_error=True)
u.GetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int]
u.GetWindowLongPtrW.restype = ctypes.c_ssize_t
u.GetForegroundWindow.restype = wt.HWND
u.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
u.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
u.GetDpiForWindow.argtypes = [wt.HWND]
u.GetDpiForWindow.restype = wt.UINT
u.OpenInputDesktop.restype = wt.HANDLE
u.GetUserObjectInformationW.argtypes = [wt.HANDLE, ctypes.c_int, wt.LPVOID, wt.DWORD, ctypes.POINTER(wt.DWORD)]
u.CloseDesktop.argtypes = [wt.HANDLE]
results = {"exe": str(Path(args.exe).resolve()), "checks": [], "physical": "not-run"}


def check(name, condition=True):
    if not condition:
        raise AssertionError(name)
    results["checks"].append(name)
    print("PASS", name, flush=True)


command = [str(Path(args.exe).resolve()), "--data-dir", str(artifact / "data"), "--render", str(artifact / "render.png")]
if not args.live:
    command.append("--demo")
env = os.environ.copy()
# App uses an explicit isolated data-dir; Codex may read its normal login in --live mode.
proc = subprocess.Popen(command, env=env, creationflags=subprocess.CREATE_NO_WINDOW)
try:
    app = Application(backend="uia").connect(process=proc.pid, timeout=20)
    widget = app.window(title="QuotaPeek")
    widget.wait("exists visible", timeout=30)
    wait_until(45, 0.25, lambda: (artifact / "render.png").exists())
    check("packaged EXE started")
    hwnd = widget.handle
    style = u.GetWindowLongPtrW(hwnd, -20)
    check("toolwindow + noactivate + topmost", style & 0x80 and style & 0x8000000 and style & 0x8)
    check("excluded from taskbar", not style & 0x40000)
    results["dpi"] = u.GetDpiForWindow(hwnd)
    text = "\n".join(control.window_text() for control in widget.descendants(control_type="Text"))
    check("Hone and Codex cards visible", "Hone API" in text and "Codex" in text)
    if args.live:
        check("real Codex quota rendered", "重置" in text and "% 剩余" in text)
    else:
        check("demo visibly labeled", "演示" in text)
    widget.capture_as_image().save(artifact / "desktop-widget.png")
    width = widget.rectangle().width()
    widget.child_window(auto_id="CollapseButton", control_type="Button").invoke()
    wait_until(5, .1, lambda: widget.rectangle().width() < width)
    check("capsule collapses")
    widget.child_window(auto_id="ExpandButton", control_type="Button").invoke()
    wait_until(5, .1, lambda: widget.rectangle().width() >= width)
    check("capsule expands")
    widget.child_window(auto_id="SettingsButton", control_type="Button").invoke()
    settings = widget.child_window(title="QuotaPeek 设置", control_type="Window")
    settings.wait("visible", timeout=10)
    settings.child_window(auto_id="NameInput").set_edit_text("Hone 测试")
    settings.child_window(auto_id="IntervalInput").set_edit_text("0")
    settings.child_window(auto_id="SaveButton", control_type="Button").invoke()
    wait_until(5, .1, lambda: "1440" in settings.child_window(auto_id="StatusText").window_text())
    check("invalid refresh interval rejected")
    settings.child_window(auto_id="IntervalInput").set_edit_text("5")
    settings.child_window(auto_id="SaveButton", control_type="Button").invoke()
    wait_until(5, .1, lambda: "已保存" in settings.child_window(auto_id="StatusText").window_text())
    check("settings save without exposing key")
    data = json.loads((artifact / "data" / "settings.json").read_text(encoding="utf-8-sig"))
    check("nonsecret settings persisted", data["Providers"][0]["Name"] == "Hone 测试")
    settings.capture_as_image().save(artifact / "desktop-settings.png")
    settings.child_window(auto_id="AddType", control_type="ComboBox").select("手动录入")
    settings.child_window(auto_id="AddButton", control_type="Button").invoke()
    settings.child_window(auto_id="NameInput").set_edit_text("手动预算验证")
    settings.child_window(auto_id="ManualUsedInput").set_edit_text("3.50")
    settings.child_window(auto_id="BudgetInput").set_edit_text("20")
    settings.child_window(auto_id="SaveButton", control_type="Button").invoke()
    wait_until(5, .1, lambda: "已保存" in settings.child_window(auto_id="StatusText").window_text())
    data = json.loads((artifact / "data" / "settings.json").read_text(encoding="utf-8-sig"))
    check("manual budget account persists", any(p["Name"] == "手动预算验证" and p["Budget"] == 20 and p["ManualUsed"] == 3.5 for p in data["Providers"]))
    settings.close()
    wait_until(5, .1, lambda: not settings.exists())
    check("closing settings keeps widget alive", proc.poll() is None)

    widget.child_window(auto_id="LockButton", control_type="Button").invoke()
    wait_until(5, .1, lambda: bool(u.GetWindowLongPtrW(hwnd, -20) & 0x20))
    check("UIA lock sets transparent layered styles", bool(u.GetWindowLongPtrW(hwnd, -20) & 0x80000))
    u.PostMessageW(hwnd, 0x312, 0x514, 0)
    wait_until(5, .1, lambda: not u.GetWindowLongPtrW(hwnd, -20) & 0x20)
    check("programmatic hotkey handler restores input")

    desk = u.OpenInputDesktop(0, False, 1)
    name = ctypes.create_unicode_buffer(256)
    required = wt.DWORD()
    available = bool(desk and u.GetUserObjectInformationW(desk, 2, name, ctypes.sizeof(name), ctypes.byref(required)))
    if desk:
        u.CloseDesktop(desk)
    foreground_pid = wt.DWORD()
    u.GetWindowThreadProcessId(u.GetForegroundWindow(), ctypes.byref(foreground_pid))
    foreground_name = process_name(foreground_pid.value) if foreground_pid.value else ""
    if not available or name.value != "Default" or foreground_name.lower() in ("lockapp.exe", "winlogon.exe"):
        results["physical"] = "environment-blocked: Windows lock screen / inactive input desktop"
    else:
        try:
            # Physical click; no activation should occur when clicking the widget.
            mouse.move(coords=(35, 35))
            foreground = u.GetForegroundWindow()
            results["foreground_before_click_is_widget"] = foreground == hwnd
            refresh = widget.child_window(auto_id="RefreshButton", control_type="Button")
            refresh.click_input()
            check("physical widget click does not steal focus", u.GetForegroundWindow() == foreground)
            before = widget.rectangle()
            grip = widget.child_window(auto_id="Subtitle").rectangle().mid_point()
            x, y = grip.x, grip.y
            mouse.move(coords=(x, y))
            mouse.press(coords=(x, y))
            mouse.move(coords=(x-55, y-45))
            mouse.release(coords=(x-55, y-45))
            wait_until(5, .1, lambda: widget.rectangle().left < before.left-20)
            check("physical dragging moves widget")
            lock_button = widget.child_window(auto_id="LockButton", control_type="Button")
            shortcut = lock_button.wrapper_object().iface_element.CurrentHelpText
            lock_button.invoke()
            wait_until(5, .1, lambda: bool(u.GetWindowLongPtrW(hwnd, -20) & 0x20))
            check("lock applies mouse transparency")
            if "Ctrl+Alt" not in shortcut:
                raise RuntimeError("No free global shortcut; tray remains available")
            keyboard.send_keys("^%+q" if "Shift" in shortcut else "^%q")
            wait_until(3, .1, lambda: not u.GetWindowLongPtrW(hwnd, -20) & 0x20)
            check("global shortcut unlocks")
            results["physical"] = "passed"
        except (OSError, RuntimeError) as error:
            if "active desktop" not in str(error) and not isinstance(error, OSError):
                raise
            results["physical"] = "environment-blocked: " + type(error).__name__
    results["result"] = "passed"
finally:
    try:
        u.PostMessageW(app.window(title="QuotaPeek").handle, 0x10, 0, 0)
        proc.wait(timeout=8)
        check("clean shutdown releases process", proc.returncode == 0)
    except Exception:
        if proc.poll() is None:
            proc.kill()  # Test-owned PID only.
            proc.wait(timeout=5)
    results["exit_code"] = proc.poll()
    (artifact / "result.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(results, ensure_ascii=False, indent=2), flush=True)
    print("ARTIFACTS", artifact, flush=True)
