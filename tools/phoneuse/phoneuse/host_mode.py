"""Select the native Hyprland/BlueZ host or portable browser/ESP32 host."""
import os
import shutil
import sys

from .config import CFG


def mode():
    if sys.platform != "linux":
        return "portable"
    choice = os.environ.get("PHONEUSE_HOST_MODE", CFG.get("host_mode", "auto"))
    if choice not in ("auto", "native", "portable"):
        raise ValueError("host_mode must be auto, native, or portable")
    if choice != "auto":
        return choice
    return ("native" if os.environ.get("HYPRLAND_INSTANCE_SIGNATURE") and shutil.which("hyprctl")
            else "portable")


def native_linux():
    return mode() == "native"
