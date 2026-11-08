"""phoneuse settings: defaults, overridden by ~/.config/phoneuse/config.toml.

Example config.toml:

    name = "PhoneUse"                 # Bluetooth device and AirPlay receiver name
    screen_points = [402, 874]       # your iPhone's screen in points (iPhone 17 Pro)
    decoder = "auto"                 # GStreamer H.264 decoder; auto picks NVDEC if present
    mirror_width = 498               # mirror window width in logical pixels
    web_port = 8777                  # browser mirror: http://localhost:8777
    host_mode = "auto"               # Linux: native Hyprland/BlueZ or portable browser/ESP32
"""
import os
import shutil
import subprocess
import sys
import tempfile
import tomllib
import hashlib

DEFAULTS = {
    "name": "PhoneUse",
    "screen_points": [402, 874],
    "decoder": "auto",
    "mirror_width": 498,
    "web_port": 8777,
    "public_origin": "",  # optional HTTPS origin behind an authenticated reverse proxy
    "host_mode": "auto",  # Linux: Hyprland/BlueZ when available, otherwise browser/ESP32
}

# Runtime paths shared by the daemon (root) and the user-side tools.
HID_SOCKET = "/run/phoneuse-hid.sock"  # Linux BlueZ daemon only


def user_dir():
    if sys.platform == "win32":
        return os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "phoneuse")
    if sys.platform == "darwin":
        return os.path.expanduser("~/Library/Application Support/phoneuse")
    return os.path.join(os.environ.get("XDG_CONFIG_HOME", os.path.expanduser("~/.config")), "phoneuse")


def runtime_dir():
    if sys.platform == "linux":
        return os.environ.get("XDG_RUNTIME_DIR", "/tmp")
    # AF_UNIX paths have a short platform limit. Use a per-user, private temp
    # directory instead of the often long LOCALAPPDATA path.
    identity = hashlib.sha256(os.path.expanduser("~").encode()).hexdigest()[:10]
    base = os.path.join(tempfile.gettempdir(), f"phoneuse-{identity}")
    return os.environ.get("PHONEUSE_RUNTIME_DIR", base)


RUNTIME_DIR = runtime_dir()
VISION_SOCKET = os.path.join(RUNTIME_DIR, "phoneuse-vision.sock")
HUB_SOCKET = os.path.join(RUNTIME_DIR, "phoneuse-hub.sock")
FRAMES_SOCKET = os.path.join(RUNTIME_DIR, "phoneuse-frames.sock")


def config_path():
    return os.path.join(user_dir(), "config.toml")


def load():
    cfg = dict(DEFAULTS)
    try:
        with open(config_path(), "rb") as f:
            cfg.update(tomllib.load(f))
    except FileNotFoundError:
        pass
    return cfg


CFG = load()
NAME = CFG["name"]
SCREEN_PT = tuple(CFG["screen_points"])
PHONE_ASPECT = SCREEN_PT[0] / SCREEN_PT[1]


def decoder():
    """H.264 decoder for the mirror. Frames must leave the decoder as soon as they
    are decoded: iOS only sends a frame when the screen changes and doesn't declare
    'no reordering', so a default decoder holds each frame until the next arrives
    (measured 200 ms). nvh264dec with compliance=flexible releases them in ~1 ms;
    the software decoder releases them immediately."""
    d = CFG["decoder"]
    if d != "auto":
        return d
    if shutil.which("gst-inspect-1.0"):
        r = subprocess.run(["gst-inspect-1.0", "nvh264dec"], capture_output=True)
        if r.returncode == 0:
            return "nvh264dec max-display-delay=0 compliance=flexible"
    return "avdec_h264"
