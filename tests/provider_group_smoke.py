"""Verify provider grouping and live settings updates in an isolated packaged EXE."""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
from pathlib import Path
import subprocess
import time

from pywinauto import Application
from pywinauto.timings import wait_until
from outside_click_target import collapse_panel

parser = argparse.ArgumentParser()
parser.add_argument("--exe", default="dist/QuotaPeek.exe")
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
artifact = root / ".artifacts" / ("provider-group-" + time.strftime("%Y%m%d-%H%M%S"))
data_dir = artifact / "data"
data_dir.mkdir(parents=True)
(data_dir / "settings.json").write_text(json.dumps({
    "Notifications": False, "AutoHideFullscreen": False, "TaskbarDocked": False,
    "StartExpanded": True, "LeftPixels": 100, "TopPixels": 100,
    "Providers": [
        {"Id": "key", "Name": "Hone API", "Type": "Hone"},
        {"Id": "codex", "Name": "Codex", "Type": "Codex", "LowThreshold": 15},
        {"Id": "wallet", "Name": "Hone 钱包", "Type": "HoneWallet"},
    ],
}, ensure_ascii=False), encoding="utf-8")
results = {"exe": str(Path(args.exe).resolve()), "checks": []}
u = ctypes.WinDLL("user32", use_last_error=True)
u.PostMessageW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
proc = subprocess.Popen([results["exe"], "--demo", "--data-dir", str(data_dir),
                         "--render", str(artifact / "render.png")],
                        creationflags=subprocess.CREATE_NO_WINDOW)
widget = None


def check(name, condition=True):
    if not condition:
        raise AssertionError(name)
    results["checks"].append(name)
    print("PASS", name, flush=True)


def names():
    return [c.window_text() for c in widget.descendants(control_type="Text")
            if c.element_info.automation_id == "ProviderName"]


try:
    app = Application(backend="uia").connect(process=proc.pid, timeout=20)
    widget = app.window(title="QuotaPeek")
    widget.wait("exists visible", timeout=30)
    wait_until(15, .2, lambda: names() == ["Hone", "Codex"])
    wait_until(15, .2, lambda: (artifact / "render.png").exists())
    check("three data sources render as two provider cards")
    texts = [c.window_text() for c in widget.descendants(control_type="Text")]
    check("wallet, account spend and API service remain labeled",
          "钱包余额" in texts and any("账户累计消费" in text for text in texts) and "API key" in texts)
    check("Codex rate windows survive grouped rendering", "5 小时" in texts and "每周" in texts and "72% 剩余" in texts)
    widget.capture_as_image().save(artifact / "grouped-window.png")
    before = widget.rectangle().width()
    collapse_panel(widget)
    wait_until(5, .1, lambda: widget.rectangle().width() < before)
    widget.child_window(auto_id="ExpandButton", control_type="Button").invoke()
    wait_until(5, .1, lambda: widget.rectangle().width() == before)
    check("collapse and expand preserve grouped cards", names() == ["Hone", "Codex"])

    widget.child_window(auto_id="SettingsButton", control_type="Button").invoke()
    settings = widget.child_window(title="QuotaPeek 设置", control_type="Window")
    settings.wait("visible", timeout=10)
    settings.child_window(auto_id="ProviderList", control_type="List").children(control_type="ListItem")[2].select()
    settings.child_window(auto_id="BaseUrlInput").set_edit_text("https://other.example.test")
    settings.child_window(auto_id="SaveButton", control_type="Button").invoke()
    wait_until(5, .1, lambda: len(names()) == 3)
    check("changing a wallet site immediately separates its card")

    settings.child_window(auto_id="BaseUrlInput").set_edit_text("https://HONE.vvvv.ee:443/v1/")
    settings.child_window(auto_id="SaveButton", control_type="Button").invoke()
    wait_until(5, .1, lambda: names() == ["Hone", "Codex"])
    check("equivalent provider address regroups without restart")

    settings.child_window(auto_id="EnabledCheck", control_type="CheckBox").toggle()
    settings.child_window(auto_id="SaveButton", control_type="Button").invoke()
    wait_until(5, .1, lambda: names() == ["Hone API", "Codex"])
    check("disabling wallet preserves the independent API card")
    settings.child_window(auto_id="EnabledCheck", control_type="CheckBox").toggle()
    settings.child_window(auto_id="SaveButton", control_type="Button").invoke()
    wait_until(5, .1, lambda: names() == ["Hone", "Codex"])
    check("reenabling wallet restores its group")
    settings.close()
    wait_until(5, .1, lambda: not settings.exists())
    persisted = json.loads((data_dir / "settings.json").read_text(encoding="utf-8-sig"))
    check("grouping retains three independent source configurations", len(persisted["Providers"]) == 3)
    results["result"] = "passed"
finally:
    if widget is not None:
        u.PostMessageW(widget.handle, 0x10, 0, 0)
    try:
        proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=5)
    results["exit_code"] = proc.returncode
    (artifact / "result.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print("ARTIFACT", artifact, flush=True)

check("isolated EXE exits cleanly", proc.returncode == 0)
