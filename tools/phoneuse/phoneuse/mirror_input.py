#!/usr/bin/python
"""Use the mirror window like the iPhone's screen: the phone's pointer follows
your cursor while it is over the mirror, clicks tap, drags swipe, the wheel
scrolls (as a drag, since iOS ignores the wheel), and the
keyboard types on the phone while the window has focus. The gestures themselves
are in pointer.py, shared with the browser mirror.

Mouse buttons and keys are read from /dev/input read-only; nothing is grabbed,
so the PC's mouse and keyboard keep working normally everywhere. The cursor
position and window geometry come from Hyprland.

The mirror window (window.py) draws the phone screen inside a dark bezel.
Pressing on the bezel starts the touch at the nearest screen edge (press the
bottom bezel and drag up = swipe home; left bezel and drag right = back). While
the window doesn't show the phone (not mirroring), no mouse or keyboard input
goes to the phone; the Bluetooth link stays up.

iPhone settings: AssistiveTouch on, Dwell Control off.
"""
import glob
import os
import select
import struct
import threading
import time

from .hidclient import Hid
from .pointer import Keys, Pointer
from .screen import hypr, mirror_live, picture_rect, MIRROR_CLASS

EVENT = struct.Struct("llHHi")  # struct input_event on 64-bit Linux
EV_KEY, EV_REL, BTN_LEFT = 0x01, 0x02, 0x110
REL_HWHEEL, REL_WHEEL = 0x06, 0x08
IDLE_WAKE = 1 / 60  # also re-check this often when the mouse is still
NOTCH_PT = 30  # phone points per wheel notch: a small swipe


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


class Mice:
    """Left button, wheel and motion from every mouse, read-only. Calls on_press /
    on_release the moment the button changes, on_wheel(dx, dy) in notches, and
    sets `changed` on any event."""

    def __init__(self, on_press, on_release, on_wheel):
        self.changed = threading.Event()
        self.on_press, self.on_release, self.on_wheel = on_press, on_release, on_wheel
        paths = sorted(glob.glob("/dev/input/by-id/*event-mouse"))
        self.fds = [os.open(p, os.O_RDONLY | os.O_NONBLOCK) for p in paths]
        log("watching", len(paths), "mice")
        threading.Thread(target=self._run, daemon=True).start()

    def _run(self):
        while True:
            ready, _, _ = select.select(self.fds, [], [])
            for fd in ready:
                try:
                    data = os.read(fd, EVENT.size * 64)
                except BlockingIOError:
                    continue
                for i in range(0, len(data) - EVENT.size + 1, EVENT.size):
                    _, _, etype, code, value = EVENT.unpack_from(data, i)
                    if etype == EV_KEY and code == BTN_LEFT and value in (0, 1):
                        try:
                            (self.on_press if value else self.on_release)()
                        except Exception as e:
                            log("button error:", e)
                    if etype == EV_REL and code in (REL_WHEEL, REL_HWHEEL):
                        try:
                            self.on_wheel(value if code == REL_HWHEEL else 0, value if code == REL_WHEEL else 0)
                        except Exception as e:
                            log("wheel error:", e)
                    if etype in (EV_KEY, EV_REL):
                        self.changed.set()


# Linux evdev key codes -> USB HID usage codes (keyboard page)
_EV2HID = {}
for i, k in enumerate([30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50, 49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44]):
    _EV2HID[k] = 4 + i  # a..z
for i, k in enumerate([2, 3, 4, 5, 6, 7, 8, 9, 10, 11]):
    _EV2HID[k] = 30 + i  # 1..9, 0
_EV2HID.update({28: 40, 1: 41, 14: 42, 15: 43, 57: 44, 12: 45, 13: 46, 26: 47, 27: 48, 43: 49,
                39: 51, 40: 52, 41: 53, 51: 54, 52: 55, 53: 56, 58: 57,
                59: 58, 60: 59, 61: 60, 62: 61, 63: 62, 64: 63, 65: 64, 66: 65, 67: 66, 68: 67, 87: 68, 88: 69,
                110: 73, 102: 74, 104: 75, 111: 76, 107: 77, 109: 78,
                106: 79, 105: 80, 108: 81, 103: 82, 96: 88})
_EVMOD = {29: 0x01, 42: 0x02, 56: 0x04, 125: 0x08, 97: 0x10, 54: 0x20, 100: 0x40, 126: 0x80}


def keyboard_paths():
    """keyd's virtual keyboard if present (keyd grabs the physical ones), else real keyboards."""
    devs, name, handlers = [], None, ""
    for line in open("/proc/bus/input/devices"):
        if line.startswith("N: Name="):
            name = line.split("=", 1)[1].strip().strip('"')
        elif line.startswith("H: Handlers="):
            handlers = line
        elif line.startswith("B: EV=") and "kbd" in handlers and "leds" in handlers:
            ev = next((h for h in handlers.split() if h.startswith("event")), None)
            if ev:
                devs.append((name, "/dev/input/" + ev))
    keyd = [p for n, p in devs if n == "keyd virtual keyboard"]
    return keyd or [p for n, p in devs if "keyd" not in n]


class KeyForwarder:
    """Types on the iPhone while the mirror window has focus (and shows the phone).
    Read-only: keys still reach the PC as usual (the mirror window ignores them)."""

    def __init__(self, keys, focused):
        self.keys = keys
        self.focused = focused
        self.sending = False
        paths = keyboard_paths()
        self.fds = [os.open(p, os.O_RDONLY | os.O_NONBLOCK) for p in paths]
        log("typing to the phone from", len(paths), "keyboard(s) while the mirror is focused")
        threading.Thread(target=self._run, daemon=True).start()

    def _run(self):
        while True:
            ready, _, _ = select.select(self.fds, [], [])
            for fd in ready:
                try:
                    data = os.read(fd, EVENT.size * 64)
                except BlockingIOError:
                    continue
                for i in range(0, len(data) - EVENT.size + 1, EVENT.size):
                    _, _, etype, code, value = EVENT.unpack_from(data, i)
                    if etype != EV_KEY or value == 2:  # ignore autorepeat; the phone repeats itself
                        continue
                    try:
                        self._key(code, value == 1)
                    except Exception as e:
                        log("key error:", e)

    def _key(self, code, down):
        if code not in _EVMOD and code not in _EV2HID:
            return
        if not self.focused():
            if self.sending:  # focus left the mirror: let go of everything on the phone
                self.keys.release_all()
                self.sending = False
            return
        self.sending = True
        if code in _EVMOD:
            self.keys.modifier(_EVMOD[code], down)
        else:
            self.keys.key(_EV2HID[code], down)


def run(stop=None):
    """Mirror-window mouse and keyboard handling until `stop` (a threading.Event) is set."""
    shared = {"hid": None}
    win = {"body": None, "pic": None, "live": False}

    def hid():
        if shared["hid"] is None:
            shared["hid"] = Hid()
        return shared["hid"]

    def mirror_focused():
        try:
            a = hypr("j/activewindow")
            return a.get("class") == MIRROR_CLASS and mirror_live(a)
        except Exception:
            return False

    def cursor():
        """(fx, fy, inside): the cursor as fractions of the phone picture (outside
        0..1 on the bezel) and whether it is over the window. None while the window
        doesn't show the phone: nothing is sent to it then."""
        if win["body"] is None or not win["live"]:
            return None
        cur = hypr("j/cursorpos")
        bx, by, bw, bh = win["body"]
        x, y, w, h = win["pic"]
        inside = bx <= cur["x"] <= bx + bw and by <= cur["y"] <= by + bh
        return (cur["x"] - x) / w, (cur["y"] - y) / h, inside

    pointer = Pointer(hid, log)

    def on_press():
        c = cursor()
        if c and c[2]:
            pointer.press(c[0], c[1])

    def on_wheel(dx, dy):
        # Wheel up scrolls up: the content moves down, as under a finger dragging
        # down. Tilting right scrolls right: the content moves left.
        c = cursor()
        if c and c[2]:
            pointer.hover(c[0], c[1])
            pointer.scroll(-dx * NOTCH_PT, dy * NOTCH_PT)

    mice = Mice(on_press, pointer.release, on_wheel)
    KeyForwarder(Keys(hid), mirror_focused)

    window_checked = 0.0
    while stop is None or not stop.is_set():
        try:
            now = time.time()
            if now - window_checked > 0.5:
                client = next((c for c in hypr("j/clients") if c["class"] == MIRROR_CLASS), None)
                win["live"] = mirror_live(client) if client else False
                win["body"] = (*client["at"], *client["size"]) if client else None
                win["pic"] = picture_rect(client["at"], client["size"]) if client else None
                window_checked = now
            c = cursor()
            if c and (c[2] or pointer.pressed):  # a drag may leave the window
                pointer.hover(c[0], c[1])
        except Exception as e:
            log("error:", e)
            shared["hid"] = None
            time.sleep(1)
        mice.changed.wait(IDLE_WAKE)
        mice.changed.clear()


if __name__ == "__main__":
    run()
