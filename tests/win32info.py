"""Small read-only Win32 helpers for desktop checks (no extra dependencies)."""
import ctypes
import ctypes.wintypes as wt
from pathlib import Path

k = ctypes.WinDLL("kernel32", use_last_error=True)
k.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
k.OpenProcess.restype = wt.HANDLE
k.CloseHandle.argtypes = [wt.HANDLE]
k.QueryFullProcessImageNameW.argtypes = [wt.HANDLE, wt.DWORD, wt.LPWSTR, ctypes.POINTER(wt.DWORD)]
k.GetProcessTimes.argtypes = [wt.HANDLE] + [ctypes.POINTER(wt.FILETIME)] * 4


def process_name(pid):
    handle = k.OpenProcess(0x1000, False, pid)
    if not handle:
        return "unknown"
    try:
        text = ctypes.create_unicode_buffer(32768)
        length = wt.DWORD(len(text))
        return Path(text.value).name if k.QueryFullProcessImageNameW(handle, 0, text, ctypes.byref(length)) else "unknown"
    finally:
        k.CloseHandle(handle)


class Memory(ctypes.Structure):
    _fields_ = [("cb", wt.DWORD), ("PageFaultCount", wt.DWORD)] + [
        (name, ctypes.c_size_t) for name in (
            "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage", "QuotaPagedPoolUsage",
            "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage", "PrivateUsage")]


def stats(handle):
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    psapi.GetProcessMemoryInfo.argtypes = [wt.HANDLE, ctypes.POINTER(Memory), wt.DWORD]
    memory = Memory(); memory.cb = ctypes.sizeof(memory)
    if not psapi.GetProcessMemoryInfo(handle, ctypes.byref(memory), memory.cb):
        raise ctypes.WinError(ctypes.get_last_error())
    times = [wt.FILETIME() for _ in range(4)]
    if not k.GetProcessTimes(handle, *[ctypes.byref(value) for value in times]):
        raise ctypes.WinError(ctypes.get_last_error())
    cpu = sum((value.dwHighDateTime << 32) + value.dwLowDateTime for value in times[2:]) / 1e7
    return memory.WorkingSetSize, memory.PrivateUsage, cpu
