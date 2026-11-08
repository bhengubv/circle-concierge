"""Measure how far one relative mouse report moves this phone's pointer, per
report size. iOS scales relative motion by AssistiveTouch's Tracking Speed, which
differs between phones (and settings), so drags need each phone's own table
(touch.REF_PTS is the iPhone 17 Pro's, at its setting). Saved per phone in
phones.json; touch.py uses the mirroring phone's table.

Each size: the pointer goes to a start point (absolute report, exact on every
phone), N relative reports of that size move it up (button 0: a hover, nothing is
tapped), then it goes back to the start. The pointer is drawn into the mirrored
picture, so the pixels that changed between "at start" and "moved" but match
between "at start" and "back" are the pointer at both places; animations on the
screen differ between the two "at start" frames and drop out.
"""
import time

import numpy as np

from . import phones, screen, touch
from .config import SCREEN_PT

SIZES = [28, 20, 12, 8, 4, 2, 1]  # largest first: each one's travel is aimed from the last
MAX_REPORTS = 150  # ~1.2 s at most per size
START = (0.5, 0.85)
TRAVEL = 0.3  # aim each measurement at about this share of the screen height
SETTLE = 0.35  # the mirror trails the phone by ~0.1-0.2 s


def _settle(seconds=SETTLE):
    time.sleep(seconds)
    return screen.grab_rgb().astype(np.int16)


def _pointer_spots(at_start, moved, back):
    """Centres (fractions) of the pointer-shaped changes: changed between at_start
    and moved, unchanged between at_start and back."""
    if not (at_start.shape == moved.shape == back.shape):
        return []
    h, w = at_start.shape[:2]
    mask = (np.abs(moved - at_start).max(axis=2) > 40) & (np.abs(back - at_start).max(axis=2) < 20)
    spots = []
    for cells, xs, ys in screen._components(mask):
        if cells >= 3:  # a few 4-px cells at least: not noise
            spots.append((cells, float(xs.mean()) / w, float(ys.mean()) / h))
    return spots


def _to_start(h):
    """Put the pointer on START. An absolute report with the same position as the
    previous one is ignored after relative moves (iOS sees no change in the
    absolute axes; measured on an iPhone 15 Pro), so step beside it first."""
    h.cmd(f"a {START[0] + 0.03} {START[1]} 0")
    time.sleep(0.03)
    h.cmd(f"a {START[0]} {START[1]} 0")


def measure(h, units, reports):
    """Points (fraction * SCREEN_PT) that `reports` relative reports of `units`
    moved the pointer straight up, or None if the pointer wasn't found."""
    _to_start(h)
    at_start = _settle()
    for _ in range(reports):
        h.cmd(f"m 0 {-units} 0 0")
        time.sleep(touch.TICK)
    moved = _settle()
    _to_start(h)
    back = _settle()
    spots = _pointer_spots(at_start, moved, back)
    if not spots:
        return None
    # The spot farthest from the start is where the pointer went.
    far = max(spots, key=lambda s: (s[1] - START[0]) ** 2 + (s[2] - START[1]) ** 2)
    dx, dy = (far[1] - START[0]) * SCREEN_PT[0], (far[2] - START[1]) * SCREEN_PT[1]
    if (dx * dx + dy * dy) ** 0.5 < 6:  # that's the start spot itself: it didn't move visibly
        return None
    return -dy


def _ref(units):
    """Points per report in the 17 Pro table (touch.REF_PTS), interpolated."""
    us = [u for u, _ in touch.REF_PTS]
    for (u0, p0), (u1, p1) in zip(touch.REF_PTS, touch.REF_PTS[1:]):
        if u0 <= units <= u1:
            return p0 + (p1 - p0) * (units - u0) / (u1 - u0)
    return touch.REF_PTS[-1][1] * units / us[-1]


def run(h, sizes=SIZES, log=print):
    """Measure points per report for each size; returns [(units, points)] sorted by
    size, or None if the measurement didn't hold together. Phones differ a lot (an
    iPhone 15 Pro moved 2-22% as far as the 17 Pro table, least for small reports),
    so each size's report count comes from the size measured before it."""
    table, last = [], None
    for u in sizes:
        guess = last[1] * u / last[0] if last else _ref(u)
        reports = min(MAX_REPORTS, max(2, round(TRAVEL * SCREEN_PT[1] / guess)))
        moved = measure(h, u, reports)
        if moved is None:
            log(f"  {u:2d} units: pointer not found")
            continue
        per = moved / reports
        last = (u, per)
        table.append((u, round(per, 3)))
        log(f"  {u:2d} units x {reports:3d}: {per:7.3f} pt per report ({per / _ref(u):.2f}x the 17 Pro)")
    table.sort()
    if len(table) < len(sizes) - 1 or any(b[1] <= a[1] for a, b in zip(table, table[1:])):
        log("  the sizes didn't grow steadily: something else moved on screen")
        return None
    return table


def calibrate(h, name=None, log=print):
    """Measure the mirroring phone and save its table. Returns a one-line result."""
    name = name or phones.current()
    if not name:
        return "calibrate: no phone is mirroring"
    table = run(h, log=log)
    _to_start(h)
    if not table:
        return "calibrate: failed (the screen kept changing, or the pointer is off); kept the old table"
    phones.save_calibration(name, table)
    touch.refresh(force=True)
    k = sorted(p / _ref(u) for u, p in table)
    return (f"calibrated {name}: a report moves the pointer {k[0]:.2f}x-{k[-1]:.2f}x as far as "
            f"on the iPhone 17 Pro; drags and swipes now use this phone's table")
