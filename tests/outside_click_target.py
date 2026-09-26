"""A harmless external window for physical click tests; never click another user's app."""
import ctypes
import ctypes.wintypes as wt
import os
import time
import tkinter as tk

from pywinauto import mouse

u = ctypes.WinDLL('user32', use_last_error=True)
u.OpenInputDesktop.restype = wt.HANDLE
u.GetUserObjectInformationW.argtypes = [wt.HANDLE, ctypes.c_int, wt.LPVOID, wt.DWORD, ctypes.POINTER(wt.DWORD)]
u.CloseDesktop.argtypes = [wt.HANDLE]
u.GetForegroundWindow.restype = wt.HWND
u.SetForegroundWindow.argtypes = [wt.HWND]
u.GetCursorPos.argtypes = [ctypes.POINTER(wt.POINT)]
u.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
u.WindowFromPoint.argtypes = [wt.POINT]; u.WindowFromPoint.restype = wt.HWND
u.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]


def require_input_desktop():
    handle = u.OpenInputDesktop(0, False, 1)
    name = ctypes.create_unicode_buffer(128)
    needed = wt.DWORD()
    try:
        if not (handle and u.GetUserObjectInformationW(handle, 2, name, ctypes.sizeof(name), ctypes.byref(needed))
                and name.value == 'Default'):
            raise RuntimeError('environment-blocked: an unlocked input desktop is required')
    finally:
        if handle:
            u.CloseDesktop(handle)


class OutsideClickTarget:
    def __init__(self):
        require_input_desktop()
        self.original_foreground = u.GetForegroundWindow()
        self.original_cursor = wt.POINT()
        u.GetCursorPos(ctypes.byref(self.original_cursor))
        self.root = tk.Tk()
        self.root.title('QuotaPeek outside-click test target')
        self.root.geometry('280x120+20+20')
        self.root.attributes('-topmost', True)
        self.root.configure(background='#edf3f7')
        self.presses = 0
        self.root.bind('<ButtonPress>', self._pressed)
        self.root.update()

    def _pressed(self, _):
        self.presses += 1

    def wait(self, condition, timeout=5):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            self.root.update()
            if condition():
                return
            time.sleep(.03)
        raise AssertionError('Timed out: ' + condition.__name__)

    def click(self, panel=None, button='left'):
        # Put the small target on the opposite side of the panel before clicking.
        if panel is not None:
            r = panel.rectangle()
            screen_width = self.root.winfo_screenwidth()
            x = 20 if r.left > 330 else screen_width - 300
            self.root.geometry(f'280x120+{x}+20')
            self.root.update()
        point = (self.root.winfo_rootx() + 100, self.root.winfo_rooty() + 60)
        hit_pid = wt.DWORD()
        u.GetWindowThreadProcessId(u.WindowFromPoint(wt.POINT(*point)), ctypes.byref(hit_pid))
        if hit_pid.value != os.getpid():
            raise RuntimeError('environment-blocked: outside-click target is obscured')
        before = self.presses
        mouse.click(button=button, coords=point)
        self.wait(lambda: self.presses == before + 1)

    def close(self):
        self.root.destroy()
        u.SetCursorPos(self.original_cursor.x, self.original_cursor.y)
        u.SetForegroundWindow(self.original_foreground)


def collapse_panel(panel):
    target = OutsideClickTarget()
    try:
        before = panel.rectangle().width()
        target.click(panel)
        target.wait(lambda: not panel.is_visible() or panel.rectangle().width() < before)
    finally:
        target.close()
