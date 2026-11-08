"""Portable HID command adapter for the existing signed ESP32 bridge link.

The Linux BlueZ daemon remains the native backend. Windows and macOS use the
same bridge firmware and report protocol without a privileged Bluetooth daemon.
"""
import threading
import time

from . import bridge, phones
from .hid_keys import BUTTONS, CHARS, CONSUMER_KEYS, MODS, NAMED


def absolute(x, y, buttons=0):
    x, y = (max(0, min(32767, round(float(v) * 32767))) for v in (x, y))
    return [buttons & 7, x & 255, x >> 8, y & 255, y >> 8]


class BridgeHid:
    """One controller using the saved bridge. `cmd` matches hidclient.Hid."""

    def __init__(self, link=None):
        self._managed = link is None
        if link is None:
            cfg = bridge.resolve()
            if not cfg or not cfg.get("use", True):
                raise OSError("No ESP32 bridge configured or reachable. Run: phoneuse bridge setup")
            link = bridge.Link(cfg)
            self._address = (cfg["ip"], cfg["key"])
        self.link = link
        self.lock = threading.RLock()
        self.buttons = 0
        self.consumer_bits = 0
        self._checked_current = None

    def _report(self, report_id, payload, skippable=False):
        if skippable:
            return "ok" if self.link.send_async(report_id, payload) else "ok dropped"
        result = self.link.report(report_id, payload, skippable)
        if result == "no phone":
            raise RuntimeError("No iPhone connected to the ESP32 bridge")
        return "ok dropped" if result == "skipped" else "ok"

    def _key(self, code, mods=0):
        self._report(1, [mods, 0, code, 0, 0, 0, 0, 0])
        time.sleep(0.012)
        self._report(1, [0] * 8)

    def _consumer(self, name, down):
        bit = 1 << CONSUMER_KEYS[name] if name else 255
        self.consumer_bits = (self.consumer_bits | bit) if down else (self.consumer_bits & ~bit)
        self._report(4, [self.consumer_bits])

    def _mouse(self, dx=0, dy=0, wheel=0):
        return self._report(2, [self.buttons, dx & 255, dy & 255, wheel & 255])

    def cmd(self, line):
        with self.lock:
            return self._cmd(line)

    def _cmd(self, line):
        command, _, rest = line.strip().partition(" ")
        command = command.lower()
        args = rest.split()
        if self._managed:
            cfg = bridge.saved()
            if not cfg or not cfg.get("use", True):
                raise RuntimeError("ESP32 bridge input is disabled")
            address = (cfg.get("ip"), cfg["key"])
            if address != self._address:
                self.link.close()
                self.link = bridge.Link(cfg)
                self._address = address
                self._checked_current = None
        if command == "status":
            st = self.link.status()
            return (f"ok connected={bool(st.get('connected'))} peer={st.get('phone')!r} "
                    f"bridge={st.get('id')!r}")
        if command == "connect":
            self.link.connect_phone()
            return "ok connecting"
        if command == "mirror":
            st = self.link.status()
            if rest and st.get("phone") and rest.casefold() != st["phone"].casefold():
                raise RuntimeError(f"Mirroring {rest!r}, but the bridge controls {st['phone']!r}")
            return "ok"
        if command == "release":
            self._report(1, [0] * 8)
            self._mouse()
            return "ok"
        # Never send input when the bridge belongs to a different phone from
        # the current AirPlay sender. `phoneuse status` remains available.
        current = phones.current()
        if current and current != self._checked_current:
            st = self.link.status()
            if st.get("phone") and current.casefold() != st["phone"].casefold():
                raise RuntimeError(f"Mirroring {current!r}, but the bridge controls {st['phone']!r}")
            self._checked_current = current
        if command == "a":
            self.buttons = (int(args[2]) if len(args) > 2 else 0) & 7
            return self._report(3, absolute(args[0], args[1], self.buttons),
                                skippable=self.buttons == 0)
        if command in ("m", "md"):
            dx, dy = (max(-127, min(127, int(v))) for v in args[:2])
            wheel = max(-127, min(127, int(args[2]))) if len(args) > 2 else 0
            if len(args) > 3:
                self.buttons = int(args[3]) & 7
            return self._report(2, [self.buttons, dx & 255, dy & 255, wheel & 255],
                                skippable=command == "md")
        if command == "k":
            codes = [int(v) & 255 for v in args[1:7]]
            return self._report(1, [int(args[0]) & 255, 0] + codes + [0] * (6 - len(codes)))
        if command == "key":
            name = args[0].lower()
            mods = 0
            for mod in args[1:]:
                mods |= MODS[mod.lower()]
            if name in NAMED:
                code = NAMED[name]
            else:
                code, shift = CHARS[name]
                mods |= shift
            self._key(code, mods)
            return "ok"
        if command == "type":
            for char in rest:
                if char not in CHARS:
                    raise ValueError(f"Cannot type {char!r} using the US HID layout")
                code, mods = CHARS[char]
                self._key(code, mods)
            return "ok"
        if command in ("eject", "consumer"):
            name = "eject" if command == "eject" else args[0]
            how = "0.05" if command == "eject" or len(args) == 1 else args[1]
            if command == "consumer" and args == ["up"]:
                self._consumer(None, False)
            elif how in ("up", "down"):
                self._consumer(name, how == "down")
            else:
                self._consumer(name, True)
                time.sleep(float(how))
                self._consumer(name, False)
            return "ok"
        if command == "move":
            dx, dy = map(int, args[:2])
            while dx or dy:
                x, y = max(-8, min(8, dx)), max(-8, min(8, dy))
                self._mouse(x, y)
                dx, dy = dx - x, dy - y
                time.sleep(0.008)
            return "ok"
        if command == "corner":
            for _ in range(60):
                self._mouse(-127, -127)
                time.sleep(0.008)
            return "ok"
        if command in ("click", "down", "up"):
            if command != "up":
                self.buttons |= BUTTONS[args[0] if args else "left"]
                self._mouse()
            if command == "click":
                time.sleep(0.05)
            if command != "down":
                self.buttons = 0
                self._mouse()
            return "ok"
        if command == "scroll":
            for _ in range(abs(int(args[0]))):
                self._mouse(wheel=1 if int(args[0]) > 0 else -1)
                time.sleep(0.02)
            return "ok"
        raise RuntimeError(f"Unknown HID command: {command}")
