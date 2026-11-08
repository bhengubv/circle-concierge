"""HID usage codes shared by the BlueZ daemon and ESP32 bridge client."""

CONSUMER_KEYS = {"eject": 0, "siri": 1, "home": 2, "search": 3}
MODS = {"ctrl": 1, "shift": 2, "alt": 4, "gui": 8, "cmd": 8,
        "rctrl": 16, "rshift": 32, "ralt": 64, "rgui": 128}
NAMED = {"enter": 40, "return": 40, "esc": 41, "escape": 41, "backspace": 42,
         "tab": 43, "space": 44, "right": 79, "left": 80, "down": 81,
         "up": 82, "delete": 76, "home": 74, "end": 77,
         "pageup": 75, "pagedown": 78}
CHARS = {}
for i, c in enumerate("abcdefghijklmnopqrstuvwxyz"):
    CHARS[c] = (4 + i, 0)
    CHARS[c.upper()] = (4 + i, 2)
for i, c in enumerate("1234567890"):
    CHARS[c] = (30 + i, 0)
for i, c in enumerate("!@#$%^&*()"):
    CHARS[c] = (30 + i, 2)
for plain, shifted, code in [("-", "_", 45), ("=", "+", 46), ("[", "{", 47), ("]", "}", 48),
                             ("\\", "|", 49), (";", ":", 51), ("'", '"', 52), ("`", "~", 53),
                             (",", "<", 54), (".", ">", 55), ("/", "?", 56)]:
    CHARS[plain] = (code, 0)
    CHARS[shifted] = (code, 2)
CHARS[" "] = (44, 0)
CHARS["\n"] = (40, 0)
CHARS["\t"] = (43, 0)
BUTTONS = {"left": 1, "right": 2, "middle": 4}
