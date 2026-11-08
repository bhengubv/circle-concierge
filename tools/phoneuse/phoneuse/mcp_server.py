"""phoneuse as an MCP server (stdio), for agents: see and operate the iPhone.

Every action returns the phone's screen in the same call. By default ("strip")
that is three frames taken 0.1, 0.4 and 0.8 s after the action, side by side in
one image (~625 tokens), plus the last frame as text lines "x y  text" with exact
positions from OCR: no waiting for the screen to settle, and motion (a page
sliding in, video) is visible. `look` can instead be "both" (one settled
screenshot + text, ~470 tokens + text), "text", "image" or "none". Positions are fractions of the screen (0..1) or phone points when a
value is above 1. Same engine as the `phoneuse` CLI.

Run it with bin/phoneuse-mcp; it needs `phoneuse up` running.
"""
import contextlib
import io
import os
import sys
from typing import Literal

from PIL import Image as PILImage
from mcp.server.mcpserver import Image, MCPServer

from . import cli, touch

Look = Literal["strip", "both", "text", "image", "none"]
STRIP_AT = (0.1, 0.4, 0.8)  # seconds after an action: frames in the strip

server = MCPServer(
    "phoneuse",
    instructions=(
        "Operate the user's real iPhone. Every action returns three frames from just after it (one image) "
        "plus the screen's text with positions, so you rarely need `look` on its own. If the last frame "
        "is still mid-transition, call look. Prefer tap_on for visible text. "
        "On the lock screen call unlock (the owner approves each unlock); never type a passcode. "
        "When your task is done, call end_session to hand the phone back to the user. "
        "This is the user's personal phone: ask before sending, posting, buying, deleting, or changing "
        "account or security settings. Don't tap the top centre (Dynamic Island): it can stop mirroring."),
)


def _quietly(fn):
    """Run a CLI helper: its prints are captured (stdout carries the protocol), and
    its errors (they exit) become a message."""
    out, err = io.StringIO(), io.StringIO()
    try:
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            result = fn()
        return True, result if result is not None else out.getvalue().strip()
    except SystemExit:
        return False, (err.getvalue() or out.getvalue()).strip() or "failed"
    except Exception as e:  # never crash the tool call; say what went wrong
        return False, f"{type(e).__name__}: {e}"


def _screen(look):
    if look == "none":
        return []
    ok, shot = _quietly(cli.fresh_screenshot)
    if not ok:
        return [shot]
    parts = []
    try:
        if look in ("both", "image"):
            img = PILImage.open(shot).convert("RGB").resize((cli.PT[0], cli.PT[1]), PILImage.LANCZOS)
            buf = io.BytesIO()
            img.save(buf, "JPEG", quality=85)
            parts.append(Image(data=buf.getvalue(), format="jpeg"))
        if look in ("both", "text"):
            ok, lines = _quietly(lambda: cli.screen_text(shot=shot))
            parts.append("\n".join(f"{x:.3f} {y:.3f}  {t}" for x, y, t in lines) if ok else lines)
    finally:
        os.unlink(shot)
    return parts


def _strip():
    """Three frames right after an action, side by side in one image (~625
    tokens), with the text of the last one. No waiting for the screen to settle:
    the agent sees what moved (a page sliding in, video, a spinner) itself."""
    import time
    from phoneuse import screen
    t0, frames = time.time(), []
    for at in STRIP_AT:
        time.sleep(max(0.0, t0 + at - time.time()))
        try:
            line, png = screen.frames_request("shot")
        except OSError as e:
            return [f"error: no frames ({e})"]
        if line.startswith("ok"):
            frames.append(PILImage.open(io.BytesIO(png)).convert("RGB"))
    if not frames:
        return ["error: no picture of the phone"]
    cli.save_look(frames[-1])  # the agent's newest view: taps by position are checked against it
    # Frames identical to the one before add nothing: keep only the ones that
    # changed. A still screen comes back as a single frame.
    import numpy as np
    def small(f):
        return np.asarray(f.convert("L").resize((50, 109)), dtype=np.int16)
    kept, times = [frames[0]], [STRIP_AT[0]]
    for f, at in zip(frames[1:], STRIP_AT[1:]):
        if (np.abs(small(f) - small(kept[-1])) > 12).mean() > 0.004:
            kept.append(f)
            times.append(at)
    if len(kept) == 1:
        kept, times = [frames[-1]], [STRIP_AT[-1]]
        w, h = cli.PT  # one frame: a full 402 x 874 screenshot (~470 tokens)
    else:
        w, h = cli.PT[0] * 2 // 3, cli.PT[1] * 2 // 3  # each frame at 268 x 583
    frames = kept
    strip = PILImage.new("RGB", (w * len(frames) + 6 * (len(frames) - 1), h), (40, 40, 40))
    for i, f in enumerate(frames):
        strip.paste(f.resize((w, h), PILImage.LANCZOS), (i * (w + 6), 0))
    buf = io.BytesIO()
    strip.save(buf, "JPEG", quality=82)
    from .config import RUNTIME_DIR
    path = os.path.join(RUNTIME_DIR, f"phoneuse-strip-{os.getpid()}.png")
    with open(path, "wb") as f:
        f.write(png)
    try:
        ok, lines = _quietly(lambda: cli.screen_text(shot=path))
        text = "\n".join(f"{x:.3f} {y:.3f}  {t}" for x, y, t in lines) if ok else lines
    finally:
        os.unlink(path)
    legend = (f"frames at +{', +'.join(str(a) for a in times)} s, left to right; text from the last"
              if len(times) > 1 else "the screen was still (one frame)")
    return [Image(data=buf.getvalue(), format="jpeg"), legend + "\n" + text]


def _act(action, look):
    def run():
        h = cli.hid()
        r = action(h)
        cli.acted(h.first_seq)
        return r if isinstance(r, str) else "ok"
    cli.NOTES.clear()
    ok, msg = _quietly(run)
    if not ok:
        return [f"error: {msg}"]
    screen_parts = _strip() if look == "strip" else _screen(look)  # may add notes
    return ["\n".join([msg] + cli.NOTES)] + screen_parts


def _pt(x, y):
    return cli.point(str(x), str(y))


@server.tool(structured_output=False)
def look(look: Look = "both") -> list:
    """The phone's screen now: one settled screenshot and text lines with positions
    ("both"), or "strip": three frames over 0.8 s, to watch something move."""
    cli.NOTES.clear()
    parts = _strip() if look == "strip" else _screen(look)
    return parts + (["\n".join(cli.NOTES)] if cli.NOTES else [])


@server.tool(structured_output=False)
def wait(seconds: float = 1.0, look: Look = "strip") -> list:
    """Wait, then look again: for a page or result still loading in the last frames.
    Decide from what you saw how long (0.5-10 s)."""
    import time
    time.sleep(min(max(seconds, 0.0), 10.0))
    cli.NOTES.clear()
    return _strip() if look == "strip" else _screen(look)


def _guard(x, y, force):
    """Refuse a positional tap when the area moved since the agent's last view."""
    why = None if force else cli.moved_since_look(*_pt(x, y))
    return [f"Not tapped: {why}"] if why else None


@server.tool(structured_output=False)
def tap(x: float, y: float, look: Look = "strip", force: bool = False) -> list:
    """Tap at (x, y): fractions of the screen (0..1), or phone points if a value is above 1.
    Refused when the area around (x, y) changed since your last view (look again);
    force=True taps anyway (e.g. a fixed tab bar after a page change)."""
    return _guard(x, y, force) or _act(lambda h: touch.tap(h, *_pt(x, y)), look)


@server.tool(structured_output=False)
def tap_on(target: str, look: Look = "strip") -> list:
    """Tap an element by description. Visible text is tapped exactly; text that isn't on
    screen is refused. Visual descriptions ("gear icon", "back button", "share button")
    use a grounding model: check the returned screen."""
    return _act(lambda h: cli.tap_on(h, target), look)


@server.tool(structured_output=False)
def long_press(x: float, y: float, seconds: float = 0.8, look: Look = "strip", force: bool = False) -> list:
    """Press and hold at (x, y). Refused like tap when the area moved since your last view."""
    return _guard(x, y, force) or _act(lambda h: touch.long_press(h, *_pt(x, y), hold=seconds), look)


@server.tool(structured_output=False)
def swipe(x0: float, y0: float, x1: float, y1: float, seconds: float = 0.3, look: Look = "strip") -> list:
    """Drag from (x0, y0) to (x1, y1) over `seconds` (sliders, custom gestures)."""
    return _act(lambda h: touch.swipe(h, *_pt(x0, y0), *_pt(x1, y1), duration=seconds), look)


@server.tool(structured_output=False)
def scroll(direction: Literal["up", "down"] = "down", amount: float = 1.0, look: Look = "strip") -> list:
    """Scroll the content by `amount` half-screens. It stops where the drag ends
    (no fling)."""
    return _act(lambda h: cli.scroll(h, direction, amount), look)


@server.tool(structured_output=False)
def scroll_by(dy: float, x: float = 0.5, look: Look = "strip") -> list:
    """Scroll exactly `dy`: a fraction of the screen height, or phone points if
    above 1. Positive shows what is below, negative what is above. For lining
    something up: move it by its distance from where you want it."""
    return _act(lambda h: cli.scroll_by(h, dy, x), look)


@server.tool(structured_output=False)
def type_text(text: str, replace: bool = True, look: Look = "strip") -> list:
    """Type text into the focused field (US layout); "\\n" presses Enter. By default
    the text replaces what the field already holds (Cmd+A first): fields often keep
    earlier text. replace=false adds to it instead. The phone may autocorrect:
    check the result before sending."""
    def act(h):
        if replace:
            cli.key_combo(h, "cmd+a")
        cli.type_text(h, text)
    return _act(act, look)


@server.tool(structured_output=False)
def press_key(combo: str, look: Look = "strip") -> list:
    """Press a key or shortcut: enter, escape, backspace, tab, arrows, cmd+space, shift+tab..."""
    return _act(lambda h: cli.key_combo(h, combo), look)


@server.tool(structured_output=False)
def toggle_keyboard(look: Look = "strip") -> list:
    """Show or hide the iPhone's on-screen keyboard (it hides while phoneuse's
    Bluetooth keyboard is connected). Useful to see suggestions or emoji."""
    return _act(lambda h: h.cmd("eject"), look)


@server.tool(structured_output=False)
def cursor(on: bool | None = None, look: Look = "strip") -> list:
    """Whether the phone's pointer works (AssistiveTouch), or switch it. Taps and
    drags need it on; a newly connected phone often has it off. on=True/False asks
    Siri out loud through this PC's speakers, so the phone must be unlocked and
    within earshot. Returns "cursor on", "cursor off" or "cursor unknown"."""
    if on is None:
        return _act(lambda h: "cursor " + cli.cursor_state(h), look)
    return _act(lambda h: cli.cursor_set(h, on), look)


@server.tool(structured_output=False)
def calibrate(look: Look = "strip") -> list:
    """Measure how far the pointer moves on the mirroring phone (about 10 s of
    hovering; nothing is tapped). Drags and swipes use the result. Runs by itself
    the first time a phone's pointer is on; call it again after changing
    AssistiveTouch's Tracking Speed."""
    from . import calibrate as cal
    return _act(lambda h: cal.calibrate(h, log=lambda *_: None), look)


@server.tool(structured_output=False)
def open_app(name: str, look: Look = "strip") -> list:
    """Open an app by name (home, Spotlight, type, Enter). Apps reopen where they were left."""
    return _act(lambda h: cli.open_app(h, name), look)


@server.tool(structured_output=False)
def navigate(to: Literal["home", "back", "switcher", "control-center", "notifications", "dismiss"],
             look: Look = "strip") -> list:
    """System gestures: home screen, back (edge swipe), app switcher, Control Center,
    Notification Center, or dismiss those overlays."""
    fn = {"home": cli.home, "back": cli.back, "switcher": cli.switcher, "control-center": cli.control_center,
          "notifications": cli.notifications, "dismiss": cli.dismiss}[to]
    return _act(fn, look)


@server.tool(structured_output=False)
def unlock(look: Look = "text") -> list:
    """On the lock screen: ask the owner to approve an unlock with their saved passcode.
    Waits up to 2 minutes for their answer. You never see the passcode."""
    ok, msg = _quietly(cli.unlock_cmd)
    msg = msg.splitlines()[-1] if msg else msg
    return [msg if ok else f"error: {msg}"] + _screen(look)


@server.tool(structured_output=False)
def end_session() -> str:
    """Call when your task on the phone is done: the user's view stops showing an
    agent at work and their own mouse and keyboard reach the phone again at once.
    (A session starts by itself with your first action.)"""
    return _quietly(cli.agent_done)[1]


@server.tool(structured_output=False)
def status() -> str:
    """Bluetooth link and mirror state; run first if anything fails."""
    return _quietly(cli.status)[1]


def main():
    os.chdir(cli.ROOT)
    server.run("stdio")


if __name__ == "__main__":
    sys.exit(main())
