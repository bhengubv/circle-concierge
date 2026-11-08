"""Touch gestures on the iPhone through hidd: exact taps, and drags that follow a path.

What iOS does with our HID reports (measured 2026-11-06, iPhone 17 Pro, iOS 26):
  * The absolute pointer (hidd 'a') lands exactly where asked, anywhere on screen.
  * Button presses count only from the absolute pointer.
  * While the button is held, an absolute move ends the touch after ~90 ms (reads
    as a click); relative moves (hidd 'm', button 0) drag it. So a drag = absolute
    press, relative motion, absolute release. Verified with touchlab/.
  * Relative motion is accelerated by report size and dropped when reports come
    faster than ~8 ms apart. One report per 8 ms, sized from DRAG_PTS below,
    moves the pointer a predictable distance.
"""
import bisect
import threading
import time

from . import phones
from .config import SCREEN_PT  # the phone screen in points (config: screen_points)
TICK = 0.008  # one relative report per tick
RAMP = 1.0  # no start-of-motion correction: touchlab shows requested distances land exactly

# Points moved by one relative report of N units (8 ms pacing), measured on the
# iPhone 17 Pro. Other phones (and Tracking Speed settings) move farther or less
# far: `phoneuse calibrate` measures the mirroring phone, and its table replaces
# this one while that phone mirrors (phones.json).
REF_PTS = [(1, 3.6), (2, 6.9), (3, 12.1), (4, 17.6), (5, 25.6), (6, 33.3), (8, 46.6),
           (10, 62.1), (12, 76.3), (16, 104.1), (20, 141.8), (28, 199.7)]
DRAG_PTS = REF_PTS
_PTS = [p for _, p in DRAG_PTS]
_stamp = None
_checked = 0.0


def refresh(force=False):
    """Use the mirroring phone's calibration, if it has one (checked at most once a
    second; long-running servers pick up a new phone or a new calibration)."""
    global DRAG_PTS, _PTS, _stamp, _checked
    now = time.time()
    if not force and now - _checked < 1.0:
        return
    _checked = now
    st = phones.stamp()
    if st == _stamp and not force:
        return
    _stamp = st
    table = phones.entry().get("drag_pts")
    DRAG_PTS = [tuple(x) for x in table] if table else REF_PTS
    _PTS = [p for _, p in DRAG_PTS]


def units_for(points):
    """Report size (units, may be fractional) that moves `points` in one report."""
    refresh()
    if points <= 0:
        return 0.0
    i = bisect.bisect_left(_PTS, points)
    if i == 0:
        return points / DRAG_PTS[0][1] * DRAG_PTS[0][0]
    if i >= len(DRAG_PTS):
        return DRAG_PTS[-1][0]  # cap: the rest goes in later ticks
    (u0, p0), (u1, p1) = DRAG_PTS[i - 1], DRAG_PTS[i]
    return u0 + (u1 - u0) * (points - p0) / (p1 - p0)


def points_for(units):
    """Points moved by a report of `units` (inverse of units_for)."""
    refresh()
    if units <= 0:
        return 0.0
    us = [u for u, _ in DRAG_PTS]
    i = bisect.bisect_left(us, units)
    if i == 0:
        return units * DRAG_PTS[0][1] / DRAG_PTS[0][0]
    if i >= len(DRAG_PTS):
        return DRAG_PTS[-1][1] * units / DRAG_PTS[-1][0]
    (u0, p0), (u1, p1) = DRAG_PTS[i - 1], DRAG_PTS[i]
    return p0 + (p1 - p0) * (units - u0) / (u1 - u0)


def whole_units(px, py):
    """Report (ux, uy) that moves at most (px, py) points: sizes round down, so the
    touch never gets ahead of the requested position and has to come back (the
    back-and-forth showed as a vibrating pointer during slow drags)."""
    refresh()
    dist = (px * px + py * py) ** 0.5
    if dist < DRAG_PTS[0][1]:  # less than one unit's worth so far: wait
        return 0, 0
    u = units_for(dist)
    ux, uy = int(u * px / dist), int(u * py / dist)  # truncate toward zero
    if ux == 0 and uy == 0:
        ux = (1 if px > 0 else -1) if abs(px) >= abs(py) else 0
        uy = (1 if py > 0 else -1) if abs(py) > abs(px) else 0
    return ux, uy


class Touch:
    """One finger. Positions are screen fractions (0..1)."""

    def __init__(self, hid):
        self.hid = hid
        self.down = False
        self.pos = None  # where we believe the pointer is (fractions)
        self.target = None
        self.lock = threading.Lock()

    def hover(self, fx, fy):
        fx, fy = _clamp(fx), _clamp(fy)
        self.hid.cmd(f"a {fx:.5f} {fy:.5f} 0")
        self.pos = self.target = (fx, fy)

    def press(self, fx, fy, settle=0.1):
        """Touch down at (fx, fy). The pointer is moved there first and given `settle`
        seconds: pressing in the same instant as a jump lets iOS start the touch on
        the way, which turns an edge swipe into a drag of the content."""
        fx, fy = _clamp(fx), _clamp(fy)
        if self.pos is None or abs(self.pos[0] - fx) > 0.002 or abs(self.pos[1] - fy) > 0.002:
            self.hid.cmd(f"a {fx:.5f} {fy:.5f} 0")
            # A new Touch doesn't know where the pointer is: it may be parked at the
            # left edge after a screenshot, a long jump. Taps right after such
            # jumps were lost twice in 15 tries at 0.1 s; give unknown starts longer.
            time.sleep(settle if self.pos is not None else max(settle, 0.2))
        self.hid.cmd(f"a {fx:.5f} {fy:.5f} 1")
        self.pos = self.target = (fx, fy)
        self.down = True

    def step(self):
        """Send one relative report moving toward the target. Returns remaining distance (pt)."""
        if not self.down or self.pos is None or self.target is None:
            return 0.0
        ex = (self.target[0] - self.pos[0]) * SCREEN_PT[0]
        ey = (self.target[1] - self.pos[1]) * SCREEN_PT[1]
        dist = (ex * ex + ey * ey) ** 0.5
        if dist < 1.0:
            return dist
        ux, uy = whole_units(ex, ey)
        if ux == 0 and uy == 0:
            return dist
        mag = (ux * ux + uy * uy) ** 0.5
        moved = points_for(mag) * RAMP
        self.hid.cmd(f"m {ux} {uy} 0 0")
        self.pos = (self.pos[0] + moved * ux / mag / SCREEN_PT[0],
                    self.pos[1] + moved * uy / mag / SCREEN_PT[1])
        return max(0.0, dist - moved)

    def release(self):
        if not self.down:
            return
        fx, fy = self.target or self.pos
        self.hid.cmd(f"a {fx:.5f} {fy:.5f} 0")
        self.down = False
        self.pos = (fx, fy)


def _clamp(v):
    return min(max(float(v), 0.0), 1.0)


# ---------- scrolling: drag, slow down, release (no fling) ----------
# iOS flings when the touch is still fast in its last moves before the lift;
# resting before lifting doesn't help (a resting touch sends no moves, so the
# fast ones are still the last). So scroll drags slow down instead: the last
# SCROLL_TAIL points go at one unit per tick (3.6 pt per 8 ms, ~450 pt/s), and
# the content stops where the finger lifts.
SCROLL_TAIL = 60.0
SCROLL_SHARE = 0.12  # before the tail: this share of the remaining distance per tick...
SCROLL_MAX = 30.0  # ...at most this many points
SCROLL_CRAWL = 20.0  # the very last points go one unit every SCROLL_CRAWL_TICKS ticks
SCROLL_CRAWL_TICKS = 3  # (~150 pt/s)
SCROLL_HOLD = 0.15  # rest before lifting (s)


def scroll_step(remaining):
    """Points to move this tick when `remaining` points are left of a scroll drag."""
    if remaining <= SCROLL_TAIL:
        return min(remaining, DRAG_PTS[0][1])
    return min(max(remaining * SCROLL_SHARE, DRAG_PTS[0][1]), SCROLL_MAX)


def scroll_drag(hid, fx0, fy0, fx1, fy1):
    """Drag from (fx0, fy0) to (fx1, fy1), slowing down at the end, and lift: the
    content moves exactly as far as the finger, with no fling."""
    t = Touch(hid)
    t.press(fx0, fy0)
    time.sleep(0.02)
    dx, dy = (fx1 - fx0) * SCREEN_PT[0], (fy1 - fy0) * SCREEN_PT[1]
    total = (dx * dx + dy * dy) ** 0.5
    done = 0.0
    while done < total - 0.5:
        done = min(total, done + scroll_step(total - done))
        k = done / total
        t.target = (fx0 + (fx1 - fx0) * k, fy0 + (fy1 - fy0) * k)
        t.step()
        time.sleep(TICK * (SCROLL_CRAWL_TICKS if total - done < SCROLL_CRAWL else 1))
    for _ in range(10):  # finish any remainder below one report
        if t.step() < 1.0:
            break
        time.sleep(TICK)
    time.sleep(SCROLL_HOLD)
    t.release()


# ---------- scripted gestures (for agents) ----------

def tap(hid, fx, fy, hold=0.06):
    t = Touch(hid)
    t.press(fx, fy)
    time.sleep(hold)
    t.release()


def long_press(hid, fx, fy, hold=0.8):
    tap(hid, fx, fy, hold)


def swipe(hid, fx0, fy0, fx1, fy1, duration=0.3, hold_end=0.0):
    """Finger down at (fx0, fy0), move to (fx1, fy1) over `duration`, lift."""
    t = Touch(hid)
    t.press(fx0, fy0)
    time.sleep(0.02)
    t0 = time.time()
    while True:
        k = min(1.0, (time.time() - t0) / max(duration, TICK))
        t.target = (fx0 + (fx1 - fx0) * k, fy0 + (fy1 - fy0) * k)
        left = t.step()
        # Done once what's left is less than the smallest report can move: waiting
        # longer holds the finger still before the lift, and a resting finger
        # doesn't fling (it used to sit here for a second, until the timeout).
        if k >= 1.0 and left < max(1.0, DRAG_PTS[0][1]):
            break
        if k >= 1.0 and time.time() - t0 > duration + 1.0:
            break
        time.sleep(TICK)
    time.sleep(hold_end)
    t.release()
