#!/usr/bin/python
"""phoneuse: see and operate an iPhone from a host computer.

Setup and running:
  phoneuse install              one-time: Bluetooth HID service (sudo), bluetoothd
                               config, and the local vision models
  phoneuse up                   mirror window + mouse control (click/drag on the mirror)
  phoneuse doctor               check host dependencies before starting
  phoneuse status               Bluetooth link and mirror window
  phoneuse done                 end the agent session: control goes back to the user
  phoneuse unlock               on the lock screen: ask the owner to approve entering the
                               passcode they saved in the browser mirror's settings
  phoneuse view [--print]       open the browser mirror (http://localhost:PORT) in the
                               default browser; --print only prints its address

Agent commands. Coordinates are fractions (0..1) of the screenshot, or phone
points if any value is > 1 (screen_points in ~/.config/phoneuse/config.toml):
  phoneuse screenshot [PATH]    PNG of the phone screen, once it has stopped changing
  phoneuse tap X Y              tap
  phoneuse longpress X Y [S]    press and hold (default 0.8 s)
  phoneuse swipe X0 Y0 X1 Y1 [S]  drag from one point to another over S seconds
  phoneuse scroll up|down [N]   scroll the content by N half-screens (default 1)
  phoneuse scroll-by DY [X]     scroll exactly DY: a screen fraction, or phone points if above 1;
                                positive shows what is below, negative what is above (X: column, default 0.5)
  phoneuse home | back | switcher | control-center | notifications | dismiss
  phoneuse open APP             open an app by name (search, type, Enter)
  phoneuse type [--append] TEXT  type text (US layout); \\n presses Enter. Replaces what
                               the field holds (Cmd+A first) unless --append
  phoneuse key COMBO            e.g. enter, escape, cmd+h, cmd+space, shift+tab
  phoneuse keyboard             show or hide the iPhone's on-screen keyboard (Eject key)
  phoneuse awake [on|off]       keep the screen awake: a tiny pointer nudge every second
                                while nobody uses the phone (also in the page's settings)
  phoneuse cursor [on|off]      whether the pointer works (AssistiveTouch); on/off asks
                                Siri out loud on this PC's speakers to switch it
  phoneuse bridge [status|setup|find|flash|log]
                                the ESP32 Bluetooth bridge: the phone pairs with it,
                                phoneuse drives it over Wi-Fi (setup is over BLE)
  phoneuse calibrate            measure how far the pointer moves on this phone (~10 s;
                                runs by itself the first time a phone's pointer is on)
  phoneuse where                debugging: where the pointer appears (needs Pointer
                               Control colour Orange)

Any action command can end with --text to print the screen as text afterwards
(as `phoneuse text`), e.g. `phoneuse tap 0.3 0.5 --text`.

Reading the screen without images (local models; a few hundred tokens instead
of ~1,300 for a screenshot):
  phoneuse text [--json] [--all] every line of text on screen with its centre (x, y), and
                               on/off switches as [switch on] / [switch off]; --all keeps
                               the status bar and stray icon glyphs
  phoneuse find WHAT            where an element is, from a plain description
                               ("clear search (X) button", "Settings app icon")
  phoneuse tap-on WHAT          find it and tap it; prints where it tapped

iPhone settings this relies on: AssistiveTouch on, Dwell Control off.
"""
import json
import importlib.util
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import webbrowser

from . import calibrate, phones, screen, touch
from .config import CFG, NAME, SCREEN_PT, VISION_SOCKET, RUNTIME_DIR
from .hidclient import Hid
from .host_mode import native_linux

PKG = os.path.dirname(os.path.realpath(__file__))
ROOT = os.path.dirname(PKG)  # the repository checkout

PT = SCREEN_PT
LAST_ACTION = os.path.join(RUNTIME_DIR, "phoneuse-last-action")
# UxPlay receives AirPlay and passes the phone's H.264 stream on as RTP to the
# video hub (hub.py), which serves the mirror window and the browser mirror.
MIRROR_ARGS = ["uxplay", "-p", "-n", NAME, "-nh",  # -nh: no "@hostname" after the name
               "-vrtp", f"config-interval=1 pt=96 mtu=60000 ! udpsink host=127.0.0.1 port={screen.HUB_RTP_PORT} sync=false",
               "-vsync", "no", "-s", f"{SCREEN_PT[0] * 3}x{SCREEN_PT[1] * 3}@60", "-fps", "60",
               # audio: mirrored audio already plays unsynced (-vsync no); keep the sink's
               # buffer at 40 ms (pulsesink default is up to 200 ms) and ask the phone to
               # plan for 0.1 s instead of 0.25 s
               "-as", ("pulsesink buffer-time=40000 latency-time=10000" if sys.platform == "linux"
                       else "autoaudiosink"), "-al", "0.1"]


# UxPlay buffers its output when it goes to a pipe; stdbuf makes it line-buffered.
UNBUFFERED = ["stdbuf", "-oL", "-eL"] if shutil.which("stdbuf") else []
UXPLAY_LOG = os.path.join(RUNTIME_DIR, "phoneuse-uxplay.log")
SENDER = re.compile(r"connection request from (.+) \((.*)\) with deviceID = (\S+)")


def follow_sender(stream):
    """Log UxPlay's output and tell the HID daemon which phone is mirroring, so the
    Bluetooth mouse and keyboard go to that phone."""
    with open(UXPLAY_LOG, "a") as log:
        for line in stream:
            log.write(line)
            log.flush()
            m = SENDER.search(line)
            if not m:
                continue
            print(time.strftime("%H:%M:%S"), f"mirroring: {m[1]} ({m[2]})", flush=True)
            phones.seen(m[1], m[2])
            try:
                print(time.strftime("%H:%M:%S"), Hid().cmd("mirror " + m[1]), flush=True)
            except Exception as e:
                print(time.strftime("%H:%M:%S"), f"could not tell the HID daemon: {e}", flush=True)
            if not phones.entry(m[1]).get("drag_pts"):
                threading.Thread(target=calibrate_when_ready, args=(m[1],), daemon=True).start()


def calibrate_when_ready(name, wait=180):
    """A phone without a calibration started mirroring: once its Bluetooth link is
    up, it is unlocked and its pointer is on, measure it (about 10 s, hovering
    only). If the pointer is off, `phoneuse cursor on` calibrates after switching it."""
    until = time.time() + wait
    while time.time() < until:
        time.sleep(5)
        if phones.current() != name or phones.entry(name).get("drag_pts"):
            return
        try:
            st = Hid().cmd("status")
            from . import unlock
            if "connected=True" not in st or not screen.mirror_live() or unlock.locked(screen_text):
                continue
            h = hid()
            state = cursor_state(h)
            if state == "unknown":
                continue
            if state == "on":
                print(time.strftime("%H:%M:%S"), calibrate.calibrate(h, name, log=lambda *_: None), flush=True)
            agent_done()
            return
        except Exception as e:
            print(time.strftime("%H:%M:%S"), f"calibration skipped: {e}", flush=True)
            return


def die(msg):
    print(msg, file=sys.stderr)
    sys.exit(1)


def point(xs, ys):
    """Parse a coordinate pair: fractions, or iPhone points if either is > 1."""
    x, y = float(xs), float(ys)
    if x > 1 or y > 1:
        x, y = x / PT[0], y / PT[1]
    if not (0 <= x <= 1 and 0 <= y <= 1):
        die(f"coordinate out of range: {xs} {ys}")
    return x, y


def need_mirror():
    """Stop unless the mirror shows the phone: acting or reading blind is unsafe."""
    if not screen.mirror_live():
        die("The mirror isn't showing the phone, so nothing was done. Start Screen Mirroring "
            f"to {NAME} on the iPhone (Control Center > Screen Mirroring), then check `phoneuse status`.")


LAST_SEEN = os.path.join(RUNTIME_DIR, "phoneuse-last-seen")
NOTES = []  # remarks for the agent, printed (CLI) or returned (MCP) with the action


def agent_turn():
    """Before an agent action: tell the browser mirror an agent is driving (it
    glows and locks its own input), and stop if the user has taken over. Also
    notes when the screen changed since the agent last looked without it acting
    (the user may be using the phone, or something is animating)."""
    import json
    req = urllib.request.Request(f"http://127.0.0.1:{CFG['web_port']}/api/agent/act", data=b"{}", method="POST",
                                 headers={"Content-Type": "application/json"})
    try:
        urllib.request.urlopen(req, timeout=2).close()
    except urllib.error.HTTPError as e:
        if e.code == 409:
            die(json.loads(e.read()).get("result", "the user took over the phone; stop and wait"))
    except OSError:
        pass  # no browser mirror running: nothing to tell
    try:
        seen_at = os.path.getmtime(LAST_SEEN)
        acted_at = os.path.getmtime(LAST_ACTION) if os.path.exists(LAST_ACTION) else 0
        # only when the agent looked after its own last action (else its own frames count)
        if time.time() - seen_at < 600 and seen_at >= acted_at:
            seen, now = int(open(LAST_SEEN).read()), frame_seq()
            if now is not None and now > seen + 1:  # one late change is usually the end of an animation
                NOTES.append("note: the screen changed since your last look without an action from you "
                             "(someone may be using the phone, or it is animating); look before relying on it")
    except (OSError, ValueError):
        pass


def hid():
    """The Bluetooth link, for commands that act on the phone."""
    need_mirror()
    agent_turn()
    try:
        h = Hid()
    except OSError as e:
        die(str(e) if not native_linux() else "Bluetooth HID service isn't running. Run: phoneuse install")
    st = h.cmd("status")
    if "connected=True" not in st:
        try:
            h.cmd("connect")
        except RuntimeError as e:
            die(f"iPhone not connected over Bluetooth ({e}). On the phone: Settings > Bluetooth > {NAME}.")
    return AgentHid(h)


class DotPoster:
    """Posts the agent's dot position to the browser mirror: the latest one only,
    one post at a time and in order, from one shared thread, so an 8 ms drag
    never waits on the page."""

    def __init__(self):
        self.url = f"http://127.0.0.1:{CFG['web_port']}/api/agent/cursor"
        self.latest = None
        self.event = threading.Event()
        self.lock = threading.Lock()  # guards latest (quick)
        self.posting = threading.Lock()  # one post at a time
        threading.Thread(target=self._run, daemon=True).start()

    def show(self, dot):
        with self.lock:
            self.latest = dot
        self.event.set()

    def show_now(self, dot):
        # Presses and releases post before returning: the process may end next.
        with self.posting:
            with self.lock:
                self.latest = dot
            self._post(dot)

    def _run(self):
        while True:
            self.event.wait()
            self.event.clear()
            with self.posting:
                with self.lock:
                    dot = self.latest
                self._post(dot)
            time.sleep(1 / 60)

    def _post(self, dot):
        try:
            x, y, down = dot
            body = f'{{"x": {x}, "y": {y}, "down": {str(down).lower()}}}'.encode()
            req = urllib.request.Request(self.url, data=body, method="POST", headers={"Content-Type": "application/json"})
            urllib.request.urlopen(req, timeout=0.3).close()
        except (OSError, ValueError, TypeError):
            pass  # the dot is a courtesy; never slow or fail the action


_dots = None


class AgentHid:
    """The Bluetooth link for agent actions: also shows where the agent points on
    the browser mirror (a glowing dot). Absolute pointer commands place the dot;
    relative drag reports move it by the points they move the touch, so it
    follows swipes and scrolls."""

    def __init__(self, h):
        global _dots
        self.h = h
        self.first_seq = None  # picture-change count just before the action's first command
        self.dot = None  # (x, y, down) where the touch or pointer is
        if _dots is None:
            _dots = DotPoster()
        self.dots = _dots

    def cmd(self, line):
        if self.first_seq is None and line != "status":
            # Taken here, after any OCR or grounding lookup the action did first:
            # changes during a lookup must not count as this action's result.
            self.first_seq = frame_seq()
        r = self.h.cmd(line)
        try:
            parts = line.split()
            if parts[0] == "a":
                self.dot = (float(parts[1]), float(parts[2]), parts[3] != "0")
                self.dots.show_now(self.dot)
            elif parts[0] == "m" and self.dot:
                ux, uy = int(parts[1]), int(parts[2])
                mag = (ux * ux + uy * uy) ** 0.5
                if mag:
                    moved = touch.points_for(mag)
                    x, y, down = self.dot
                    self.dot = (min(max(x + moved * ux / mag / SCREEN_PT[0], 0.0), 1.0),
                                min(max(y + moved * uy / mag / SCREEN_PT[1], 0.0), 1.0), down)
                    self.dots.show(self.dot)
        except (IndexError, ValueError):
            pass
        return r


def frame_seq():
    """The count of picture changes (frames service; None without it): taken
    before an action, so a later read can wait for that action's result."""
    try:
        line, _ = screen.frames_request("status", timeout=1)
        return int(line.split("seq=")[1].split()[0])
    except (OSError, IndexError, ValueError):
        return None


def acted(seq_before=None):
    """Note that an action happened, and the frame count from before it."""
    with open(LAST_ACTION, "w") as f:
        f.write("" if seq_before is None else str(seq_before))


def settle():
    """Wait until the phone's screen is still: after a recent action, until its
    first frame has arrived and frames have stopped; otherwise at once. Driven
    by the hub's frame clock, no fixed delays."""
    since = None
    try:
        if time.time() - os.path.getmtime(LAST_ACTION) < 5:
            txt = open(LAST_ACTION).read().strip()
            since = int(txt) if txt else None
    except (FileNotFoundError, ValueError):
        pass
    try:
        line, _ = screen.frames_request(f"settle {since}" if since is not None else "settle", timeout=6)
        with open(LAST_SEEN, "w") as f:  # what the agent has now seen (see agent_turn)
            f.write(line.split("seq=")[1].split()[0])
        note = None
        if "still=0" in line:
            note = "note: the screen was still changing when read; look again if it matters"
        elif "ambient=1" in line or float(line.split("ignored=")[1].split()[0]) > 0.05:
            note = "note: parts of the screen keep moving (video or animation); read the rest as settled"
        if note and note not in NOTES:
            NOTES.append(note)
        if since is not None:  # this action's result has been waited for: later reads needn't wait again
            acted(None)
    except (OSError, IndexError):
        time.sleep(0.3)  # no hub (older setup): a short pause instead


def key_combo(h, combo):
    parts = combo.lower().replace("-", "+").split("+")
    mods = {"cmd": "gui", "command": "gui", "ctrl": "ctrl", "control": "ctrl", "alt": "alt",
            "option": "alt", "opt": "alt", "shift": "shift", "gui": "gui"}
    key = parts[-1]
    h.cmd("key " + " ".join([key] + [mods[p] for p in parts[:-1]]))


# ---------- gestures (recipes verified on iOS 26, see RECIPES.md) ----------

def home(h):
    h.cmd("key h gui")


def back(h):
    # finger sliding in from the very left edge (touch.press settles the pointer first)
    touch.swipe(h, 0.0, 0.5, 0.8, 0.5, duration=0.35)


def switcher(h):
    touch.swipe(h, 0.5, 1.0, 0.5, 0.62, duration=0.45, hold_end=0.6)


def control_center(h):
    touch.swipe(h, 0.88, 0.0, 0.88, 0.45, duration=0.6)


def notifications(h):
    touch.swipe(h, 0.15, 0.0, 0.15, 0.7, duration=0.6)


def dismiss(h):
    """Close Control Center, Notification Center, the switcher, or go home from an app."""
    touch.swipe(h, 0.5, 1.0, 0.5, 0.15, duration=0.12)


def scroll(h, direction, n=1.0):
    """Scroll the content by n half-screens (0.45 of the screen height each)."""
    scroll_by(h, 0.45 * n if direction == "down" else -0.45 * n)


def scroll_by(h, dy, x=0.5):
    """Scroll exactly dy (screen fraction, or phone points if |dy| > 1): positive
    shows what is below. A drag that slows down before it lifts, so iOS doesn't
    fling and the content stops where the finger lifts; long scrolls take
    several drags."""
    if abs(dy) > 1:
        dy /= SCREEN_PT[1]
    left = abs(dy)
    while left > 0.005:
        d = min(left, 0.7)
        y0, y1 = (0.85, 0.85 - d) if dy > 0 else (0.15, 0.15 + d)
        touch.scroll_drag(h, x, y0, x, y1)
        left -= d


def steplog(*a):
    if os.environ.get("PHONEUSE_STEPLOG"):
        print(f"{time.time():.3f}", *a, file=sys.stderr, flush=True)


def step(h, command, quiet=0.1, change=0.6):
    """Send one HID command and wait until the phone has shown its result (the
    frames service), instead of a fixed pause. `quiet`: how long the picture
    must stay still, longer where results stream in (search)."""
    seq = frame_seq()
    steplog("send", command)
    h.cmd(command)
    if seq is None:
        time.sleep(0.8)  # no frames service (older setup)
        return
    try:
        screen.frames_request(f"settle {seq} {quiet} {change}", timeout=6)
        acted(None)  # settled: reads in the same command needn't wait for a change again
        steplog("settled", command)
    except OSError:
        time.sleep(0.8)


def refuse_if_locked():
    """Stop before typing into the lock screen: typed text and Enter there would
    go to the passcode field, and a submitted wrong passcode counts as a failed
    attempt. Only the owner may unlock the phone."""
    from . import unlock
    if unlock.locked(screen_text):  # the passcode prompt, or the lock screen's clock and date
        die("The phone is locked (passcode screen). Nothing was typed. Run `phoneuse unlock` to "
            "ask the owner to approve an unlock, or ask them to unlock it.")


# Spoken to Siri to switch AssistiveTouch, which the phone's pointer needs (rendered
# with Piper, voice en_US-lessac-medium).
SOUNDS = os.path.join(PKG, "sounds")


def play_wav(path):
    if sys.platform == "win32":
        import winsound
        winsound.PlaySound(path, winsound.SND_FILENAME)
    else:
        subprocess.run(["afplay" if sys.platform == "darwin" else "pw-play", path], check=True, timeout=15)


def cursor_state(h):
    """Is the phone's pointer working (AssistiveTouch on)? Moves the pointer between
    two spots twice (A, B, A, B). With AssistiveTouch on, the pointer is part of
    the mirrored picture: near each spot, the two frames with the pointer there
    match each other and differ from the two with it away, in a pointer-sized blob
    centred on the spot. A moving wallpaper or video changes between visits too,
    so it can't pass for the pointer. Returns "on", "off", or "unknown" when the
    screen keeps changing by itself near the spots."""
    import numpy as np
    spots = [(0.30, 0.42), (0.70, 0.58)]
    shots = []
    for x, y in spots + spots:
        h.cmd(f"a {x} {y} 0")
        time.sleep(0.4)
        shots.append(screen.grab_rgb().astype(np.int16))
    if any(s.shape != shots[0].shape for s in shots):
        return "unknown"
    H, W, _ = shots[0].shape
    r = int(W * 0.07)

    def near(img, spot):
        cx, cy = int(spot[0] * W), int(spot[1] * H)
        return img[max(cy - r, 0):cy + r, max(cx - r, 0):cx + r]

    def diff(a, b):
        return np.abs(a - b).max(axis=2) > 40

    seen = []
    for i, spot in enumerate(spots):
        here = [near(shots[k], spot) for k in (i, i + 2)]  # frames with the pointer at this spot
        away = [near(shots[k], spot) for k in (1 - i, 3 - i)]
        if diff(here[0], here[1]).mean() > 0.005 or diff(away[0], away[1]).mean() > 0.005:
            return "unknown"  # something else moves here
        moved = diff(here[0], away[0]) & diff(here[1], away[1])
        ys, xs = np.nonzero(moved)
        # the pointer: a blob of some size, centred on the spot (the crop's middle)
        seen.append(moved.mean() > 0.02 and abs(xs.mean() - moved.shape[1] / 2) < r * 0.4
                    and abs(ys.mean() - moved.shape[0] / 2) < r * 0.4)
    return "on" if all(seen) else "off"


def siri_open(before, wait=2.5):
    """Wait until Siri is listening: its orb appears at the bottom centre about
    1-1.4 s after the Siri key goes down. True once the picture there changed."""
    import numpy as np
    H, W = before.shape[:2]
    box = (slice(int(H * 0.85), H), slice(int(W * 0.3), int(W * 0.7)))
    until = time.time() + wait
    while time.time() < until:
        time.sleep(0.2)
        now = screen.grab_rgb().astype(np.int16)
        if now.shape == before.shape and (np.abs(now[box] - before[box]).max(axis=2) > 40).mean() > 0.08:
            return True
    return False


def cursor_set(h, on):
    """Turn AssistiveTouch on or off by asking Siri out loud: hold the Siri key,
    wait until Siri shows it is listening, play the request on this PC's speakers
    (the phone must hear them), let go. Asks a second time if the first request
    didn't take."""
    import numpy as np
    refuse_if_locked()
    want = "on" if on else "off"
    state = cursor_state(h)
    if state == want:
        return f"cursor {state} (already)"
    wav = os.path.join(SOUNDS, f"assistivetouch-{want}.wav")
    heard = False
    for _ in range(2):
        before = screen.grab_rgb().astype(np.int16)
        h.cmd("consumer siri down")
        try:
            if not siri_open(before):
                continue  # let go (finally) and hold again
            heard = True
            play_wav(wav)
            time.sleep(0.3)
        finally:
            h.cmd("consumer siri up")
        # Siri switches it, answers, and closes; its animation reads as "unknown" meanwhile.
        state, until = "unknown", time.time() + 10
        while time.time() < until:
            time.sleep(1.0)
            state = cursor_state(h)
            if state == want:
                if on and not phones.entry().get("drag_pts"):  # first time on: measure it
                    return f"cursor {state}; " + calibrate.calibrate(h)
                return f"cursor {state}"
    if not heard:
        return (f"cursor {state} (wanted {want}): Siri didn't open when the Siri key was held. "
                "Check Settings → Siri: Press Side Button for Siri on, and Siri not restricted.")
    return (f"cursor {state} (wanted {want}): Siri opened but didn't switch it. Check that the phone "
            "is near this PC's speakers and their volume is up, then try again.")


def awake_cmd(arg):
    """Keep the screen awake (or not): the browser mirror's server nudges the pointer
    about once a second while nobody uses the phone, so Auto-Lock never fires."""
    if arg not in (None, "on", "off"):
        die("usage: phoneuse awake [on|off]")
    url = f"http://127.0.0.1:{CFG['web_port']}/api/awake"
    data = None if arg is None else json.dumps({"on": arg == "on"}).encode()
    req = urllib.request.Request(url, data=data, method="GET" if data is None else "POST",
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            on = json.load(r)["on"]
    except OSError as e:
        die(f"The browser mirror's server isn't running ({e}); start `phoneuse up`.")
    return "keep awake " + ("on" if on else "off")


def open_app(h, name):
    step(h, "key h gui", change=0.3)  # often already home (no change); also wakes a locked phone
    refuse_if_locked()
    step(h, "key space gui")
    step(h, "type " + name, quiet=0.35)  # search results arrive in waves
    step(h, "key enter", quiet=0.3)  # wait for the app to come up


def type_text(h, text):
    refuse_if_locked()
    for i, chunk in enumerate(text.replace("\\n", "\n").split("\n")):
        if i:
            h.cmd("key enter")
        if chunk:
            h.cmd("type " + chunk)


# ---------- screen ----------

LOOK = os.path.join(RUNTIME_DIR, "phoneuse-look.npy")


def _small(rgb):
    """Grey, a quarter of the size: enough to tell whether an area moved."""
    import numpy as np
    return np.asarray(rgb, dtype=np.float32)[::4, ::4].mean(axis=2)


def save_look(rgb):
    """Remember what the agent was just shown (a screenshot, a text read, or the
    frame an MCP action returned): taps by position are checked against it."""
    import numpy as np
    try:
        np.save(LOOK, _small(rgb))
    except OSError:
        pass


def moved_since_look(fx, fy):
    """Has the area around (fx, fy) changed since the agent's last look? Taps go
    by position, so a tap computed from an old picture of a feed that has since
    scrolled lands on something else. Returns a reason, or None."""
    import numpy as np
    try:
        if time.time() - os.path.getmtime(LOOK) > 300:
            return None  # no recent look: the agent isn't working from a picture
        then = np.load(LOOK)
        settle()
        now = _small(screen.grab_rgb())
    except (OSError, ValueError, RuntimeError):
        return None
    if now.shape != then.shape:
        return None
    h, w = now.shape
    x0, x1 = int(max(0, fx - 0.08) * w), int(min(1, fx + 0.08) * w)
    y0, y1 = int(max(0, fy - 0.04) * h), int(min(1, fy + 0.04) * h)
    a, b = then[y0:y1, x0:x1], now[y0:y1, x0:x1]
    # Compare content, not background: text on a plain page is a few percent of
    # the pixels, so measure the changed pixels against the content ones.
    ink = (np.abs(a - np.median(a)) > 24) | (np.abs(b - np.median(b)) > 24)
    if ink.sum() < 30:
        return None  # an empty area: nothing there to miss
    changed = float(((np.abs(b - a) > 24) & ink).sum() / ink.sum())
    if changed > 0.4:  # the pointer alone is far smaller than this
        return (f"the screen around ({fx:.2f}, {fy:.2f}) changed since your last look ({changed:.0%} of it): "
                "the content moved, so this position may hit something else. Look again (screenshot or "
                "--text), then tap. Add --force to tap anyway (e.g. a fixed tab bar after a page change).")
    return None


def screenshot(path):
    need_mirror()
    settle()
    try:
        png = screen.grab_png()
    except RuntimeError as e:
        die(str(e))
    with open(path, "wb") as f:
        f.write(png)
    from PIL import Image
    import io
    img = Image.open(io.BytesIO(png))
    save_look(img.convert("RGB"))
    w, hgt = img.size
    print(f"{path} ({w}x{hgt} px = {PT[0]}x{PT[1]} pt; tap with fractions of this image or points)")


# ---------- vision (OCR + grounding, see vision.py) ----------

VISION_SOCK = VISION_SOCKET


def vision(req, timeout=90):
    import json
    import socket

    def ask():
        from . import ipc
        s = ipc.connect(VISION_SOCK, timeout=timeout)
        s.sendall((json.dumps(req) + "\n").encode())
        reply = json.loads(s.makefile().readline())
        s.close()
        return reply

    try:
        return ask()
    except (FileNotFoundError, ConnectionRefusedError):
        pass
    from .vision_setup import python_path
    py = python_path(ROOT)
    if not os.path.exists(py):
        die("Vision models aren't installed. Run: python -m phoneuse.vision_setup")
    logf = open(os.path.join(RUNTIME_DIR, "phoneuse-vision.log"), "a")
    env = dict(os.environ)
    env["PYTHONPATH"] = ROOT + (os.pathsep + env["PYTHONPATH"] if env.get("PYTHONPATH") else "")
    subprocess.Popen([py, "-m", "phoneuse.vision"], cwd=ROOT, env=env,
                     stdout=logf, stderr=logf, start_new_session=True)
    for _ in range(120):
        time.sleep(0.5)
        try:
            return ask()
        except (FileNotFoundError, ConnectionRefusedError):
            continue
    die(f"vision service didn't start; see {os.path.join(RUNTIME_DIR, 'phoneuse-vision.log')}")


def fresh_screenshot():
    """A screenshot for reading, once the screen has settled (see settle)."""
    path = os.path.join(RUNTIME_DIR, f"phoneuse-shot-{os.getpid()}.png")
    import contextlib, io
    with contextlib.redirect_stdout(io.StringIO()):
        screenshot(path)
    return path


STATUS_BAR = 0.055  # lines above this (time, signal, battery) are left out of `text`
BACK_GLYPHS = {"く", "<", "‹", "〈", "〈"}  # how OCR reads the back chevron at the top left


def screen_text(show_all=False, shot=None):
    """The screen as text lines [(x, y, text)]: OCR plus the state of on/off
    switches, which OCR can't read. Drops the status bar and icon glyphs OCR
    mistakes for text (lines without a letter or digit) unless show_all. Takes a
    settled screenshot unless given one (`shot`, a PNG path, which it leaves)."""
    import numpy as np
    from PIL import Image
    own = shot is None
    shot = shot or fresh_screenshot()
    r = vision({"op": "text", "image": shot})
    rgb = np.asarray(Image.open(shot).convert("RGB"))
    if own:
        os.unlink(shot)
    if "error" in r:
        die(r["error"])
    lines = [(ln["x"], ln["y"], "[back]" if ln["text"].strip() in BACK_GLYPHS and ln["x"] < 0.15 else ln["text"])
             for ln in r["lines"]
             if show_all or (ln["y"] >= STATUS_BAR and any(c.isalnum() for c in ln["text"]))]
    lines += [(x, y, "[switch on]" if on else "[switch off]") for x, y, on in screen.find_switches(rgb)]
    return sorted(lines, key=lambda t: (round(t[1] / 0.012), t[0]))  # rows top to bottom, then left to right


def text_cmd(args):
    import json
    lines = screen_text("--all" in args)
    if "--json" in args:
        print(json.dumps([{"x": x, "y": y, "text": t} for x, y, t in lines]))
        return
    for x, y, t in lines:
        print(f"{x:.3f} {y:.3f}  {t}")
    if NOTES:
        print("\n".join(NOTES))


FILLER = {"row", "button", "icon", "link", "tab", "cell", "item", "the", "label", "option"}
# Descriptions of things that aren't text; only these fall back to the grounding model.
VISUAL = {"icon", "button", "image", "picture", "logo", "symbol", "glyph", "arrow", "switch",
          "toggle", "avatar", "photo", "thumbnail", "chevron", "x", "plus", "+", "gear", "badge"}


def text_target(what):
    """Position of an on-screen text line matching `what` (ignoring words like
    "row" or "button"), or None. Exact, unlike the grounding model, which always
    answers even when the element isn't on screen."""
    words = [w for w in what.lower().replace('"', "").split() if w not in FILLER]
    if not words:
        return None
    want = " ".join(words)
    lines = [(x, y, t.lower().strip()) for x, y, t in screen_text() if y >= STATUS_BAR]
    exact = [(x, y) for x, y, t in lines if t == want]
    if exact:
        return exact[0]
    starts = [(x, y) for x, y, t in lines if t.startswith(want)]
    if len(starts) == 1:
        return starts[0]
    # OCR misreads a character now and then ("Message" for "iMessage" when the
    # text cursor covers the i): take the single closest line if it is close.
    import difflib
    scored = sorted(((difflib.SequenceMatcher(None, want, t).ratio(), x, y) for x, y, t in lines), reverse=True)
    if scored and scored[0][0] >= 0.85 and (len(scored) == 1 or scored[1][0] < scored[0][0] - 0.1):
        return scored[0][1], scored[0][2]
    return None


def tap_on(h, what):
    """Tap an element by description: on-screen text exactly (OCR), else, for
    visual descriptions only, the grounding model. Returns what it did."""
    hit = text_target(what)
    if hit:
        x, y = hit
        how = "text"
    elif not VISUAL.intersection(what.lower().replace('"', "").split()):
        die(f"'{what}' isn't on screen as text, so nothing was tapped. Scroll to it, or describe "
            "something visual (\"gear icon\", \"back button\") to use the grounding model.")
    else:
        x, y = find(what)
        how = "grounding (check the result)"
        # find always answers, even when the element isn't there; it then tends to
        # point at the status bar. Never tap there: the Dynamic Island in its middle
        # opens the Stop Mirroring popup.
        if y < 0.06:
            die(f"refusing to tap the status bar at {x:.3f} {y:.3f}: '{what}' is probably not on screen")
    touch.tap(h, x, y)
    return f"tapped {x:.3f} {y:.3f} by {how}"


def find(what):
    shot = fresh_screenshot()
    r = vision({"op": "find", "image": shot, "what": what})
    os.unlink(shot)
    if "error" in r:
        die(f"not found: {what}")
    return r["x"], r["y"]


# ---------- up / install / status ----------

def host_requirements():
    """Missing prerequisites for the selected host; do not start partial workers."""
    missing = []
    if not shutil.which("uxplay"):
        missing.append("UxPlay executable is not on PATH")
    if not shutil.which("gst-inspect-1.0"):
        missing.append("GStreamer is not on PATH")
    else:
        elements = ("udpsrc", "rtph264depay", "h264parse", "avdec_h264", "appsink", "appsrc",
                    "videoconvert", "videoscale", "pngenc", "x264enc", "rtph264pay", "multiudpsink")
        absent = [name for name in elements if subprocess.run(["gst-inspect-1.0", name],
                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode != 0]
        if absent:
            missing.append("GStreamer elements missing: " + ", ".join(absent))
    if importlib.util.find_spec("gi") is None:
        missing.append("PyGObject/GStreamer Python bindings (gi) are unavailable to this Python")
    else:
        try:
            import gi
            gi.require_version("Gst", "1.0")
            from gi.repository import Gst, GLib  # noqa: F401
        except (ImportError, OSError, ValueError) as e:
            missing.append(f"GStreamer Python bindings cannot load: {e}")
    if importlib.util.find_spec("aiohttp") is None:
        missing.append("aiohttp is not installed in this Python")
    if not native_linux():
        if sys.platform != "linux" and importlib.util.find_spec("psutil") is None:
            missing.append("psutil is not installed in this Python")
        from . import bridge
        if not bridge.saved():
            missing.append("ESP32 HID bridge is not configured (phoneuse bridge setup)")
    return missing


def doctor():
    print(f"Host: {sys.platform}; Python: {sys.executable}")
    print(f"Input: {'BlueZ daemon or ESP32 bridge' if native_linux() else 'ESP32 bridge'}")
    print(f"Display: {'Hyprland window and browser' if native_linux() else 'browser mirror'}")
    missing = host_requirements()
    if missing:
        for item in missing:
            print(f"MISSING: {item}")
        raise SystemExit(1)
    print("Host requirements found. AirPlay and HID device behavior still require a live iPhone test.")

def up():
    missing = host_requirements()
    if missing:
        die("Cannot start the mirror:\n  " + "\n  ".join(missing))
    os.makedirs(RUNTIME_DIR, mode=0o700, exist_ok=True)
    if native_linux() and subprocess.run(["systemctl", "is-active", "--quiet", "phoneuse-hid"]).returncode != 0:
        die("Bluetooth HID service isn't running. Run: phoneuse install")
    print(f"Starting the mirror (AirPlay receiver '{NAME}'). On the iPhone: Control Center > Screen Mirroring > {NAME}.")
    stop = threading.Event()
    procs = {}

    def supervise(module, label, watch):
        """Run phoneuse.<module>; start it again when it exits or when one of the
        `watch` files changes. (The page itself is served fresh on every load.)"""
        files = [os.path.join(PKG, f) for f in watch]
        mtimes = lambda: [os.path.getmtime(f) for f in files if os.path.exists(f)]
        while not stop.is_set():
            seen = mtimes()
            procs[module] = subprocess.Popen([sys.executable, "-m", f"phoneuse.{module}"], cwd=ROOT)
            while procs[module].poll() is None and not stop.is_set():
                time.sleep(1)
                if mtimes() != seen:
                    print(time.strftime("%H:%M:%S"), f"{label} code changed; restarting it", flush=True)
                    procs[module].terminate()
                    procs[module].wait(5)
                    break
            else:
                if stop.is_set():
                    break
                print(time.strftime("%H:%M:%S"), f"{label} exited ({procs[module].returncode}); starting it again.", flush=True)
                time.sleep(1)

    def start_vision():
        # The screen-reading models (OCR and grounding) load as soon as phoneuse
        # starts, not on an agent's first look. Skipped if they aren't installed.
        from .vision_setup import python_path
        vision_python = python_path(ROOT)
        if os.path.exists(vision_python):
            try:
                import contextlib, io
                with contextlib.redirect_stderr(io.StringIO()):
                    vision({"op": "ping"}, timeout=120)
            except SystemExit:
                pass
    threading.Thread(target=start_vision, daemon=True).start()

    def keep_hub():
        # The video hub (hub.py): decodes the phone's stream and serves it to the
        # window and the browser mirror. Started first; rarely restarts.
        while not stop.is_set():
            procs["hub"] = subprocess.Popen([sys.executable, "-m", "phoneuse.hub"], cwd=ROOT)
            rc = procs["hub"].wait()
            if stop.is_set():
                break
            print(time.strftime("%H:%M:%S"), f"video hub exited ({rc}); starting it again. "
                  "Reconnect Screen Mirroring on the iPhone.", flush=True)
            time.sleep(1)
    threading.Thread(target=keep_hub, daemon=True).start()

    def keep_frames():
        # The frames service (frames.py): settle and screenshots for agents. It
        # joins the hub as a viewer, so it restarts freely, also on code changes.
        supervise("frames", "frames service", ["frames.py", "screen.py"])
    threading.Thread(target=keep_frames, daemon=True).start()

    def keep_window():
        # The mirror window (window.py) outlives UxPlay restarts and phone
        # reconnects; it waits for the next stream by itself.
        while not stop.is_set():
            procs["window"] = subprocess.Popen([sys.executable, "-m", "phoneuse.window"], cwd=ROOT)
            rc = procs["window"].wait()
            if stop.is_set():
                break
            print(time.strftime("%H:%M:%S"), f"mirror window exited ({rc}); opening it again.", flush=True)
            time.sleep(1)
    if native_linux():
        threading.Thread(target=keep_window, daemon=True).start()

    def keep_web():
        # The browser mirror (web.py), on localhost only. Restarts when its code
        # changes: it rejoins the hub, so the phone keeps mirroring.
        supervise("web", "browser mirror", ["web.py", "pointer.py", "touch.py", "phones.py", "unlock.py", "screen.py"])
    threading.Thread(target=keep_web, daemon=True).start()

    def keep_mirror():
        # If UxPlay dies, start it again so the phone can reconnect without
        # restarting `phoneuse up`.
        while not stop.is_set():
            env = dict(os.environ)
            out = subprocess.DEVNULL
            if os.environ.get("PHONEUSE_TRACE"):  # per-element GStreamer latency, for tuning
                env.update(GST_TRACERS="latency(flags=pipeline+element)", GST_DEBUG="GST_TRACER:7")
                out = open(os.path.join(RUNTIME_DIR, "phoneuse-gst-trace.log"), "w")
            if out is subprocess.DEVNULL:  # read UxPlay's output for the sender's name
                procs["mirror"] = subprocess.Popen(UNBUFFERED + MIRROR_ARGS, stdout=subprocess.PIPE,
                                                   stderr=subprocess.STDOUT, env=env, text=True, errors="replace")
                threading.Thread(target=follow_sender, args=(procs["mirror"].stdout,), daemon=True).start()
            else:
                procs["mirror"] = subprocess.Popen(MIRROR_ARGS, stdout=out, stderr=out, env=env)
            rc = procs["mirror"].wait()
            if stop.is_set():
                break
            print(time.strftime("%H:%M:%S"), f"mirror exited ({rc}); restarting it. Reconnect Screen Mirroring on the iPhone.", flush=True)
            time.sleep(1)
    threading.Thread(target=keep_mirror, daemon=True).start()
    try:
        h = Hid()
        from . import bridge, bridge_web
        if native_linux() and bridge.saved() and bridge.saved().get("use", True):  # daemon owns Linux bridge
            found = bridge.resolve()
            print("Bridge:", bridge_web.use_bridge(found).replace("ok ", "") if found
                  else "set up, but not answering on this network", flush=True)
        print("Bluetooth:", h.cmd("status").replace("ok ", ""))
        print("Mouse control on: hover, click and drag in the browser mirror. Ctrl+C stops everything."
              if not native_linux() else
              "Mouse control on: hover, click and drag on the mirror window. Ctrl+C stops everything.")
        print(f"Browser mirror: http://localhost:{CFG['web_port']}")
        if not native_linux():
            while not stop.wait(1):
                pass
            return
        # The clicker runs as its own process and restarts by itself when its code
        # changes, so edits don't drop the AirPlay connection.
        watched = [os.path.join(PKG, f) for f in ("mirror_input.py", "pointer.py", "touch.py", "screen.py", "hidclient.py")]
        mtimes = lambda: [os.path.getmtime(f) for f in watched]
        while not stop.is_set():
            seen = mtimes()
            procs["clicker"] = subprocess.Popen([sys.executable, "-m", "phoneuse.mirror_input"], cwd=ROOT)
            while procs["clicker"].poll() is None:
                time.sleep(1)
                if mtimes() != seen:
                    print(time.strftime("%H:%M:%S"), "clicker code changed; reloading it (mirror stays up)", flush=True)
                    procs["clicker"].terminate()
                    procs["clicker"].wait(3)
                    break
            else:
                print(time.strftime("%H:%M:%S"), "clicker exited; restarting it", flush=True)
                time.sleep(1)
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        if procs.get("clicker") and procs["clicker"].poll() is None:
            procs["clicker"].terminate()
        for name in ("mirror", "window", "web", "frames", "hub"):
            p = procs.get(name)
            if p and p.poll() is None:
                p.terminate()
                try:
                    p.wait(3)
                except subprocess.TimeoutExpired:
                    p.kill()


def status():
    if not native_linux():
        try:
            print("Bridge:", Hid().cmd("status").replace("ok ", ""))
        except (OSError, RuntimeError) as e:
            print("Bridge:", e)
        try:
            print("AirPlay:", "mirroring" if screen.airplay_mirroring() else "not mirroring")
        except RuntimeError as e:
            print("AirPlay:", e)
        print("Frames:", "live" if screen.mirror_live() else "not live")
        return
    svc = subprocess.run(["systemctl", "is-active", "phoneuse-hid"], capture_output=True, text=True).stdout.strip()
    print("HID service:", svc)
    if svc == "active":
        print("Bluetooth:", Hid().cmd("status").replace("ok ", ""))
    try:
        at, size = screen.mirror_window()
        live = "showing the phone" if screen.mirror_live() else "not mirroring (input to the phone is off)"
        print("Mirror window:", at, size, live)
    except RuntimeError:
        print(f"Mirror window: none (run `phoneuse up`, then Screen Mirroring > {NAME} on the iPhone)")


def view(print_only):
    """The browser mirror's address; opens it in the default browser unless print_only."""
    url = f"http://localhost:{CFG['web_port']}"
    try:
        urllib.request.urlopen(url, timeout=2).close()
    except OSError:
        die(f"The browser mirror isn't running at {url}. Start `phoneuse up`.")
    if not print_only:
        webbrowser.open(url)
    print(url)


def agent_done():
    """End the agent's session: the browser mirror stops glowing and its mouse and
    keyboard reach the phone again, at once (not after 20 idle seconds)."""
    req = urllib.request.Request(f"http://127.0.0.1:{CFG['web_port']}/api/agent/done", data=b"{}", method="POST",
                                 headers={"Content-Type": "application/json"})
    try:
        urllib.request.urlopen(req, timeout=2).close()
    except OSError:
        pass
    print("ok: session ended; control is back with the user")


def unlock_cmd():
    """Ask the owner to unlock the phone with the saved passcode. Waits for their
    answer (up to 2 minutes); the passcode never passes through here."""
    import json
    req = urllib.request.Request(f"http://127.0.0.1:{CFG['web_port']}/api/unlock", data=b"{}", method="POST",
                                 headers={"Content-Type": "application/json"})
    print("Asked the owner to approve an unlock (browser mirror or notification); waiting...", flush=True)
    try:
        with urllib.request.urlopen(req, timeout=150) as r:
            print(json.loads(r.read())["result"])
    except urllib.error.HTTPError as e:
        die(json.loads(e.read()).get("result", str(e)))
    except OSError as e:
        die(f"The browser mirror isn't running ({e}). Start `phoneuse up`.")


def main():
    a = sys.argv[1:]
    if not a or a[0] in ("-h", "--help", "help"):
        print(__doc__)
        return
    cmd, args = a[0], a[1:]
    # `--text` at the end of an action: print the screen as text afterwards, saving
    # a second call (only as the last argument, so `type` can still type it).
    then_text = bool(args) and args[-1] == "--text" and cmd not in ("text", "find", "screenshot")
    if then_text:
        args = args[:-1]
    if cmd == "install":
        if not native_linux():
            die("The Linux installer cannot run here. Run `phoneuse doctor` for host requirements.")
        if not os.path.exists(os.path.join(ROOT, "scripts", "install.sh")):
            die("Linux service installation requires a phoneuse source checkout")
        subprocess.run(["sudo", os.path.join(ROOT, "scripts", "install.sh")], check=True)
        if not os.path.exists(os.path.join(ROOT, ".venv-vision")):
            import shutil
            if shutil.which("uv"):
                subprocess.run([os.path.join(ROOT, "scripts", "setup-vision.sh")], check=True)
            else:
                print("skipping the screen-reading models (phoneuse text/find): install uv, then run scripts/setup-vision.sh")
        return
    if cmd == "up":
        return up()
    if cmd == "doctor":
        return doctor()
    if cmd == "status":
        return status()
    if cmd == "unlock":
        return unlock_cmd()
    if cmd == "done":
        return agent_done()
    if cmd == "bridge":  # the ESP32 Bluetooth bridge: setup, status, firmware
        from . import bridge
        return bridge.main(args)
    if cmd == "awake":  # awake [on|off]: keep the screen awake (the page server nudges the pointer)
        return print(awake_cmd(args[0] if args else None))
    if cmd == "view":
        return view("--print" in args)
    if cmd == "text":
        return text_cmd(args)
    if cmd == "find":
        x, y = find(" ".join(args))
        print(f"{x:.3f} {y:.3f}")
        return
    if cmd == "screenshot":
        paths = [x for x in args if not x.startswith("--")]  # (--keep-pointer: now always)
        return screenshot(paths[0] if paths else os.path.join(RUNTIME_DIR, "phoneuse.png"))
    force = "--force" in args
    args = [a for a in args if a != "--force"]
    if cmd in ("tap", "longpress", "swipe") and not force and len(args) >= 2:
        why = moved_since_look(*point(*args[:2]))
        if why:
            die("Not tapped: " + why)
    h = hid()
    seq_before = frame_seq()
    if cmd == "tap":
        touch.tap(h, *point(*args[:2]))
    elif cmd == "longpress":
        touch.long_press(h, *point(*args[:2]), hold=float(args[2]) if len(args) > 2 else 0.8)
    elif cmd == "swipe":
        x0, y0 = point(*args[0:2]); x1, y1 = point(*args[2:4])
        touch.swipe(h, x0, y0, x1, y1, duration=float(args[4]) if len(args) > 4 else 0.3)
    elif cmd == "scroll":
        scroll(h, args[0] if args else "down", float(args[1]) if len(args) > 1 else 1.0)
    elif cmd == "scroll-by":
        scroll_by(h, float(args[0]), float(args[1]) if len(args) > 1 else 0.5)
    elif cmd in ("home", "back", "switcher", "dismiss", "notifications"):
        globals()[cmd](h)
    elif cmd == "control-center":
        control_center(h)
    elif cmd == "open":
        open_app(h, " ".join(args))
    elif cmd == "type":
        # The text replaces what the field holds (Cmd+A first): fields often keep
        # earlier text. --append adds to it instead.
        if args and args[0] == "--append":
            args = args[1:]
        else:
            key_combo(h, "cmd+a")
        type_text(h, " ".join(args))
    elif cmd == "key":
        key_combo(h, args[0])
    elif cmd == "keyboard":  # Eject: show or hide the on-screen keyboard
        h.cmd("eject")
    elif cmd == "cursor":  # cursor [on|off]: report, or switch AssistiveTouch through Siri
        if not args:
            print("cursor", cursor_state(h))
        elif args[0] in ("on", "off"):
            print(cursor_set(h, args[0] == "on"))
        else:
            die("usage: phoneuse cursor [on|off]")
    elif cmd == "calibrate":  # measure the mirroring phone's pointer (drags use it)
        print(calibrate.calibrate(h))
    elif cmd == "tap-on":
        print(tap_on(h, " ".join(args)))
    elif cmd == "where":
        p = screen.find_orange(screen.grab_rgb())
        print(f"{p[0]:.3f} {p[1]:.3f}" if p else "not found")
        return
    else:
        die(f"unknown command: {cmd} (phoneuse --help)")
    acted(h.first_seq if getattr(h, "first_seq", None) is not None else seq_before)
    print("ok")
    if then_text:
        for x, y, t in screen_text():
            print(f"{x:.3f} {y:.3f}  {t}")
    if NOTES:
        print("\n".join(NOTES))


if __name__ == "__main__":
    main()
