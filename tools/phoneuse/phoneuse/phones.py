"""The phones phoneuse has driven, and which one is screen mirroring now.

~/.config/phoneuse/phones.json keeps one entry per phone, keyed by the name it
mirrors under (UxPlay prints it on every connection): its model, when it was
first and last seen, and its pointer calibration (calibrate.py), which drags use
in place of the iPhone 17 Pro table in touch.py. `phoneuse up` writes the
mirroring phone's name to a runtime file, so every process (CLI, MCP, the browser
mirror's server) reads the same answer.
"""
import json
import os
import time

from .config import RUNTIME_DIR, config_path

PHONES = os.path.join(os.path.dirname(config_path()), "phones.json")
CURRENT = os.path.join(RUNTIME_DIR, "phoneuse-phone")


def load():
    try:
        with open(PHONES) as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def _write(phones):
    os.makedirs(os.path.dirname(PHONES), exist_ok=True)
    tmp = PHONES + ".tmp"
    with open(tmp, "w") as f:
        json.dump(phones, f, indent=1, ensure_ascii=False)
    os.replace(tmp, PHONES)


def seen(name, model):
    """A phone started mirroring: record it, and make it the current phone.
    Returns True the first time this phone is seen."""
    phones = load()
    now = time.strftime("%Y-%m-%d %H:%M")
    new = name not in phones
    entry = phones.setdefault(name, {"first_seen": now})
    entry["model"] = model
    entry["last_seen"] = now
    _write(phones)
    with open(CURRENT, "w") as f:
        f.write(name)
    return new


def current():
    """Name of the phone that is screen mirroring (or last mirrored), or None."""
    try:
        with open(CURRENT) as f:
            return f.read().strip() or None
    except OSError:
        return None


def entry(name=None):
    name = name or current()
    return load().get(name, {}) if name else {}


def save_calibration(name, table):
    phones = load()
    e = phones.setdefault(name, {"first_seen": time.strftime("%Y-%m-%d %H:%M")})
    e["drag_pts"] = [[u, p] for u, p in table]
    e["calibrated"] = time.strftime("%Y-%m-%d %H:%M")
    _write(phones)


def stamp():
    """Changes whenever the current phone or the phone list changes."""
    out = []
    for p in (CURRENT, PHONES):
        try:
            out.append(os.stat(p).st_mtime_ns)
        except OSError:
            out.append(0)
    return tuple(out)
