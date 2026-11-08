"""The mirror window on screen: Hyprland IPC, where the phone picture is,
screenshots of it, and finding the orange pointer in them.

Positions are fractions of the phone screen: (0, 0) top-left, (1, 1) bottom-right.
Requires Hyprland (window geometry and cursor come from its IPC socket) and grim.
"""
import glob
import io
import json
import os
import socket
import subprocess
import sys

import numpy as np
from PIL import Image

from .config import FRAMES_SOCKET, HUB_SOCKET, PHONE_ASPECT
from . import ipc
from .host_mode import native_linux

MIRROR_CLASS = "phoneuse-mirror"  # window class (Wayland app id) of the mirror window, window.py
HUB_RTP_PORT = 5590  # UxPlay sends the phone's video here, to the hub (hub.py)
RTP_PORT = 5600  # the hub sends it on here, for the mirror window (window.py)
WEB_RTP_PORT = 5602  # ...and here, for the browser mirror (web.py)
FRAMES_RTP_PORT = 5604  # ...and here, for the frames service (frames.py: settle, screenshots)
AIRPLAY_MIRROR_PORT = 7100  # UxPlay's mirroring stream port; a connection on it = the phone is mirroring
# The window's title says whether it shows the phone: input is only forwarded
# to the phone while it does (mirror_input.py, cli.py).
TITLE_LIVE = "phoneuse mirror"
TITLE_WAITING = "phoneuse mirror (not mirroring)"

# Bezel around the picture in the mirror window, in units of the screen's width.
# Presses on the bezel start edge swipes (mirror_input.py).
BEZEL = 30 / 498
SCREEN_RADIUS = 60 / 498  # the screen's corner radius, ~48 pt on the phone


def _find_hyprland():
    # tmux servers started before a Hyprland restart keep a stale signature, so
    # hyprctl fails (exit 4). Use the saved one only if its socket answers;
    # otherwise the newest instance whose socket does.
    run = os.environ.get("XDG_RUNTIME_DIR", "/run/user/%d" % os.getuid())

    def alive(sock):
        s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        try:
            s.connect(sock)
            return True
        except OSError:
            return False
        finally:
            s.close()

    sig = os.environ.get("HYPRLAND_INSTANCE_SIGNATURE")
    if sig and alive(os.path.join(run, "hypr", sig, ".socket.sock")):
        return
    socks = sorted(glob.glob(os.path.join(run, "hypr", "*", ".socket.sock")), key=os.path.getmtime, reverse=True)
    for sock in socks:
        if alive(sock):
            os.environ["HYPRLAND_INSTANCE_SIGNATURE"] = os.path.basename(os.path.dirname(sock))
            return


if sys.platform == "linux":
    _find_hyprland()


def hypr(cmd):
    """One Hyprland IPC request ('j/cursorpos', 'j/clients', ...) as parsed JSON (~0.03 ms)."""
    path = os.path.join(os.environ.get("XDG_RUNTIME_DIR", f"/run/user/{os.getuid()}"),
                        "hypr", os.environ.get("HYPRLAND_INSTANCE_SIGNATURE", ""), ".socket.sock")
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.connect(path)
    s.sendall(cmd.encode())
    data = b""
    while chunk := s.recv(65536):
        data += chunk
    s.close()
    return json.loads(data)


def hypr_eval(lua):
    """Run Lua in Hyprland (Lua-config builds). Returns False when unsupported."""
    r = subprocess.run(["hyprctl", "eval", lua], capture_output=True, text=True)
    return r.returncode == 0 and "unknown request" not in r.stdout and not r.stdout.startswith("error")


def airplay_mirroring():
    """True while a phone is mirroring to UxPlay (an open connection on its
    mirroring port)."""
    if sys.platform != "linux":
        # psutil obtains established TCP connections on Windows and macOS;
        # a listening UxPlay port alone is not evidence of a phone.
        try:
            import psutil
            def connected(connections):
                return any(c.status == "ESTABLISHED" and c.laddr and c.laddr.port == AIRPLAY_MIRROR_PORT
                           for c in connections)
            try:
                return connected(psutil.net_connections(kind="tcp"))
            except psutil.AccessDenied:
                # macOS restricts system-wide connection enumeration for a
                # regular user; inspect only this user's UxPlay processes.
                for process in psutil.process_iter(["name", "username"]):
                    try:
                        if "uxplay" in (process.info["name"] or "").lower() and connected(process.net_connections(kind="tcp")):
                            return True
                    except (psutil.AccessDenied, psutil.NoSuchProcess):
                        continue
                return False
        except ImportError:
            raise RuntimeError("psutil is required to detect AirPlay on this host")
    port = f":{AIRPLAY_MIRROR_PORT:04X}"
    for path in ("/proc/net/tcp6", "/proc/net/tcp"):
        try:
            for line in open(path).readlines()[1:]:
                f = line.split()
                if f[1].endswith(port) and f[3] == "01":  # local port, ESTABLISHED
                    return True
        except OSError:
            pass
    return False


def mirror_live(client=None):
    """True when the mirror window shows the phone's screen."""
    if not native_linux():
        try:
            line, _ = frames_request("status", timeout=1)
            return line.startswith("ok ") and "live=1" in line and airplay_mirroring()
        except (OSError, RuntimeError):
            return False
    client = client or mirror_client()
    return bool(client) and client["title"] == TITLE_LIVE


def _request(path, cmd, timeout):
    s = ipc.connect(path, timeout=timeout)
    with s:
        s.sendall(cmd.encode() + b"\n")
        f = s.makefile("rb")
        line = f.readline().decode().strip()
        data = f.read(int(line.split()[1])) if cmd == "shot" and line.startswith("ok ") else b""
        return line, data


def hub_request(cmd, timeout=3.0):
    """One control request to the video hub (hub.py); returns (reply line, payload)."""
    return _request(HUB_SOCKET, cmd, timeout)


def frames_request(cmd, timeout=3.0):
    """One request to the frames service (frames.py): status, settle, shot."""
    return _request(FRAMES_SOCKET, cmd, timeout)


def mirror_client():
    if not native_linux():
        return None
    return next((c for c in hypr("j/clients") if c["class"] == MIRROR_CLASS), None)


def mirror_window():
    c = mirror_client()
    if c is None:
        raise RuntimeError("mirror window not found")
    return c["at"], c["size"]


def design_size():
    """(width, height) of the window in screen-width units."""
    return 1 + 2 * BEZEL, 1 / PHONE_ASPECT + 2 * BEZEL


def window_size(screen_width):
    dw, dh = design_size()
    return round(dw * screen_width), round(dh * screen_width)


def picture_rect(at, size):
    """Where the phone picture sits on screen, for a mirror window at `at` with
    `size`. The rest of the window is bezel. The picture is aspect-fit and
    centered, so a window of another shape gets a wider bezel on two sides, never
    a squished picture."""
    (x, y), (w, h) = at, size
    dw, dh = design_size()
    u = min(w / dw, h / dh)  # pixels per screen width
    return x + (w - u) / 2, y + (h - u / PHONE_ASPECT) / 2, u, u / PHONE_ASPECT


def grab_png():
    """PNG of the phone's screen. From the frames service when it runs (the
    current picture, so windows on top don't matter); otherwise a capture of the
    mirror window's area of the screen, which must not be covered."""
    try:
        line, png = frames_request("shot")
        if line.startswith("ok"):
            return png
    except OSError:
        pass
    if not native_linux():
        raise RuntimeError("frames service has no live screenshot; start the browser mirror and connect the iPhone")
    x, y, w, h = picture_rect(*mirror_window())
    return subprocess.check_output(["grim", "-g", f"{round(x)},{round(y)} {round(w)}x{round(h)}", "-"])


def grab_rgb():
    return np.asarray(Image.open(io.BytesIO(grab_png())).convert("RGB"))


def _components(mask, cell=4):
    """Connected components of a boolean mask on a coarse grid: (cells, xs, ys) each."""
    h, w = mask.shape
    g = mask[: h // cell * cell, : w // cell * cell].reshape(h // cell, cell, w // cell, cell).any(axis=(1, 3))
    lab = np.zeros(g.shape, np.int32)
    out = []
    for y0, x0 in zip(*np.nonzero(g)):
        if lab[y0, x0]:
            continue
        lab[y0, x0] = len(out) + 1
        stack, pts = [(y0, x0)], []
        while stack:
            cy, cx = stack.pop()
            pts.append((cy, cx))
            for ny in (cy - 1, cy, cy + 1):
                for nx in (cx - 1, cx, cx + 1):
                    if 0 <= ny < g.shape[0] and 0 <= nx < g.shape[1] and g[ny, nx] and not lab[ny, nx]:
                        lab[ny, nx] = lab[y0, x0]
                        stack.append((ny, nx))
        ys, xs = (np.array(v) for v in zip(*pts))
        out.append((len(pts), (xs + 0.5) * cell, (ys + 0.5) * cell))
    return out


def find_switches(frame_rgb):
    """On/off switches (iOS toggles) in a screenshot: [(x, y, on)], positions as
    fractions. A toggle is a pill about 61 x 31 pt: green when on, grey when off
    (dark-mode grey; light-mode toggles aren't detected). OCR can't read them."""
    a = frame_rgb.astype(np.int16)
    h, w = a.shape[:2]
    pt = w / 402  # pixels per phone point (portrait)
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    masks = {
        True: (g > 170) & (r < 110) & (b < 140) & (g - r > 90),  # green track
        False: (abs(r - 90) < 14) & (abs(g - r) < 6) & (abs(b - r) < 10),  # grey track
    }
    found = []
    for on, mask in masks.items():
        for n, xs, ys in _components(mask, cell=2):
            bw, bh = xs.max() - xs.min(), ys.max() - ys.min()
            if 48 * pt <= bw <= 72 * pt and 22 * pt <= bh <= 36 * pt and xs.mean() > 0.6 * w:  # toggles sit at the right
                found.append((float(xs.mean() / w), float(ys.mean() / h), on))
    return sorted(found, key=lambda t: t[1])


def orange_mask(frame_rgb):
    r, g, b = (frame_rgb[..., i].astype(np.int16) for i in range(3))
    return (r > 200) & (g > 100) & (g < 185) & (b < 90) & (r - g > 60)


def find_orange(frame_rgb, baseline_rgb=None):
    """Centre (fractions) of the orange Pointer Control ring, or None.

    Needs Pointer Control: Color = Orange, large size. The ring is the orange
    component that is big (about 22% of the screen width across) and hollow,
    unlike orange app icons, which are small and solid.
    """
    mask = orange_mask(frame_rgb)
    if baseline_rgb is not None:  # drop orange that was already there (icons etc.)
        mask &= ~orange_mask(baseline_rgb)
    h, w = mask.shape
    best = None
    for n, xs, ys in _components(mask):
        bw, bh = np.ptp(xs) + 4, np.ptp(ys) + 4
        size = max(bw, bh) / w
        fill = n * 16 / (bw * bh)
        if 0.12 < size < 0.35 and fill < 0.5 and (best is None or n > best[0]):
            # a ring cut off by a screen edge: centre from the full-size side
            cx = (xs.min() + xs.max()) / 2
            cy = (ys.min() + ys.max()) / 2
            d = max(bw, bh)
            if bw < d * 0.9:
                cx = xs.max() - d / 2 if xs.min() <= 4 else xs.min() + d / 2
            if bh < d * 0.9:
                cy = ys.max() - d / 2 if ys.min() <= 4 else ys.min() + d / 2
            best = (n, cx / w, cy / h)
    return best[1:] if best else None
