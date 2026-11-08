"""Turn a pointer over the mirror into touches on the phone. Shared by the mirror
window (mirror_input.py) and the browser mirror (web.py).

Positions are fractions of the phone screen, (0, 0) top-left to (1, 1)
bottom-right. Positions outside 0..1 are on the bezel: a hover or press there
goes to the nearest screen edge, so pressing the bottom bezel and dragging up
swipes home.

Hover: the phone's pointer follows (absolute reports).
Press: touch down where the phone's pointer already is (absolute report,
button 1).
Held: every 8 ms a relative report moves the touch by exactly what the pointer
moved (sized from the measured table), button 0 on these reports.
Release: absolute report, button 0, where the touch is.
Scroll: iOS ignores the mouse wheel, so wheel scrolling becomes a drag at the
pointer. Wheel steps go into a backlog that the touch drains with
touch.scroll_step: quick while much is left, slow for the last points, so iOS
sees a slow finger lift and doesn't fling (one fast drag per notch moved Hacker
News over a screen per 145 pt notch). Near the screen edge the touch slows down
the same way, lifts, and grips again where there is room, so long scrolls
continue.

Verified on a touch-event test page (touchlab/): this delivers one touch whose
path matches the request (175 -> 175 pt, flicks 144 -> 141 pt). An absolute
move while pressed makes iOS end the touch after ~90 ms (it reads as a click),
and relative-only presses are ignored.
"""
import threading
import time

from . import touch

MIN_GAP = 1 / 250  # at most this many hover reports per second
SCROLL_STILL = touch.SCROLL_HOLD  # lift once the wheel scroll is done and the wheel idle this long (s)
SCROLL_MARGIN = 0.06  # regrip before the touch comes this close to an edge (screen fraction)
GRIP_DELAY = 0.024  # between the hover to the grip point and the press (s)
MIN_GRIP = 15.0  # grip for at least this much wheel scrolling: a shorter drag reads as a tap
WHEEL_FORGET = 0.3  # a remainder below MIN_GRIP is dropped once the wheel is idle this long (s)


def clamp(v):
    return min(max(v, 0.0), 1.0)


class Pointer:
    def __init__(self, hid, log=print):
        self.hid = hid  # callable returning a Hid client
        self.log = log
        self.lock = threading.Lock()
        self.pressed = False
        self.at = None  # latest pointer position (unclamped)
        self.sent = None  # last hover position sent = where the phone's pointer is
        self.last_send = 0.0
        self.touch = None  # where the touch is during a drag
        self.carry = [0.0, 0.0]  # phone points still to move
        self.dragged_from = None  # pointer position the carry is measured from
        self.scrolling = False  # the touch is a wheel scroll, not a held button
        self.scrolled_at = 0.0
        self.wheel = [0.0, 0.0]  # wheel scrolling not yet handed to the touch (phone points)
        self.moved_at = 0.0  # last time the scroll touch moved
        self.grip = None  # (position, time) of a hover waiting to become a scroll press
        self.crawl = 0  # ticks counted while the scroll crawls (touch.SCROLL_CRAWL)
        self.stopped = False
        self.last_error = None
        threading.Thread(target=self._tick, daemon=True).start()

    def hover(self, fx, fy):
        # Only note the newest position: the tick sends it within 8 ms. Sending here
        # would hold up the caller (the page server's event loop) on Bluetooth, and
        # the moves behind it would queue up and trail the mouse.
        with self.lock:
            self.at = (fx, fy)

    def _send_hover(self):
        # (called with the lock held; also from _tick for a position that was rate-limited)
        pos = (round(clamp(self.at[0]), 4), round(clamp(self.at[1]), 4))
        if pos != self.sent:
            r = self.hid().cmd(f"a {pos[0]:.5f} {pos[1]:.5f} 0")
            if "dropped" not in r:  # skipped while the link was busy: the next tick sends it again
                self.sent = pos
            self.last_send = time.time()

    def scroll(self, dx_pt, dy_pt):
        """Scroll by dragging the content dx_pt, dy_pt phone points (positive dy
        moves the content down, like a finger dragging down). The tick does the
        dragging; this only adds to the backlog."""
        with self.lock:
            if self.pressed and not self.scrolling:
                return  # a held button is already dragging
            if self.at is None:
                return
            self.wheel[0] += dx_pt
            self.wheel[1] += dy_pt
            self.scrolled_at = time.time()

    def _grip_point(self):
        # (lock held) Where a scroll touch goes down: at the pointer, unless that
        # leaves too little room in the scroll's direction; never on the edges
        # (back swipe, home bar).
        fx, fy = clamp(self.at[0]), clamp(self.at[1])
        if self.wheel[1] < 0 and fy < 0.45:
            fy = 0.75
        elif self.wheel[1] > 0 and fy > 0.55:
            fy = 0.25
        if self.wheel[0] < 0 and fx < 0.4:
            fx = 0.7
        elif self.wheel[0] > 0 and fx > 0.6:
            fx = 0.3
        return (round(min(max(fx, 0.08), 0.92), 4), round(min(max(fy, 0.12), 0.85), 4))

    def _scroll_step(self):
        # (lock held) The part of the backlog the touch moves this tick, at the
        # speed touch.scroll_step gives for what is left: the backlog, or the room
        # before the touch nears an edge if that is less (so it slows down there
        # too). Returns the step and whether the edge stopped it.
        w = (self.wheel[0] ** 2 + self.wheel[1] ** 2) ** 0.5
        if w < 0.5:
            return [0.0, 0.0], False
        d = (self.wheel[0] / w, self.wheel[1] / w)
        room = w
        for i in (0, 1):
            if abs(d[i]) > 1e-6:
                size = touch.SCREEN_PT[i]
                at = self.touch[i] * size + self.carry[i]
                edge = (1 - SCROLL_MARGIN) * size - at if d[i] > 0 else at - SCROLL_MARGIN * size
                room = min(room, max(edge, 0.0) / abs(d[i]))
        if room < 0.5:
            return [0.0, 0.0], True
        if room < touch.SCROLL_CRAWL:  # the last points crawl: one report every few ticks
            self.crawl += 1
            if self.crawl % touch.SCROLL_CRAWL_TICKS:
                return [0.0, 0.0], False
        v = touch.scroll_step(room)
        return [d[0] * v, d[1] * v], False

    def _grip_tick(self, now):
        # (lock held) Start a scroll touch: hover to the grip point, then press
        # there a little later (a press that also moves makes iOS end the touch).
        if self.grip is None:
            pos = self._grip_point()
            if pos != self.sent:
                self.hid().cmd(f"a {pos[0]:.5f} {pos[1]:.5f} 0")
                self.sent = pos
            self.grip = (pos, now)
            return
        pos, since = self.grip
        if now - since < GRIP_DELAY:
            return
        self.grip = None
        self.hid().cmd(f"a {pos[0]:.5f} {pos[1]:.5f} 1")
        self.touch = list(pos)
        self.carry = [0.0, 0.0]
        self.dragged_from = self.at
        self.pressed = self.scrolling = True
        self.moved_at = now
        self.log("scroll down", f"({pos[0]:.3f}, {pos[1]:.3f})", f"wheel {self.wheel[0]:.0f},{self.wheel[1]:.0f} pt")

    def _down(self):
        # (lock held) Touch down exactly where the phone's pointer already is (the
        # last hover report), not at the new position: a press that also changes
        # the absolute position makes iOS end the touch after ~90 ms (drags die,
        # clicks get flaky). Any gap is sent as ordinary drag motion.
        fx, fy = self.at
        px, py = self.sent or (clamp(fx), clamp(fy))
        self.hid().cmd(f"a {px:.5f} {py:.5f} 1")
        self.touch = [px, py]
        self.carry = [(clamp(fx) - px) * touch.SCREEN_PT[0], (clamp(fy) - py) * touch.SCREEN_PT[1]]
        self.dragged_from = (fx, fy)
        self.pressed = True

    def _up(self):
        # (lock held) lift where the touch is, so the release carries no position change
        self.pressed = self.scrolling = False
        fx, fy = self.touch
        self.hid().cmd(f"a {fx:.5f} {fy:.5f} 0")
        self.sent = (round(fx, 4), round(fy, 4))

    def press(self, fx, fy):
        with self.lock:
            self.wheel = [0.0, 0.0]  # a click ends a wheel scroll first
            self.grip = None
            if self.scrolling:
                self._up()
            if self.pressed:
                return
            self.at = (fx, fy)
            self._down()
            self.log("down", f"({self.touch[0]:.3f}, {self.touch[1]:.3f})")

    def release(self):
        """The button went up (a wheel scroll lifts by itself)."""
        with self.lock:
            if not self.pressed or self.scrolling:
                return
            self._up()
            self.log("up", f"({self.touch[0]:.3f}, {self.touch[1]:.3f})")

    def close(self):
        with self.lock:
            if self.pressed:
                self._up()
        self.stopped = True

    def _tick(self):
        # While pressed: every 8 ms, move the touch by exactly what the pointer
        # moved, converted to phone points and then to the report size that moves
        # that far (touch.DRAG_PTS, measured; iOS accelerates by report size and
        # drops reports sent faster than ~8 ms apart). Remainders carry over. One
        # report every tick, even with no motion: steady timing keeps iOS's
        # acceleration predictable. The button stays 0 on these reports: iOS
        # holds the touch from the absolute press, and a button here reads as a
        # second press/release (double tap sounds).
        while not self.stopped:
            time.sleep(touch.TICK)
            try:
                with self.lock:
                    now = time.time()
                    if not self.pressed:
                        if (self.wheel[0] ** 2 + self.wheel[1] ** 2) ** 0.5 >= MIN_GRIP:
                            self._grip_tick(now)
                            continue
                        if self.wheel != [0.0, 0.0] and now - self.scrolled_at > WHEEL_FORGET:
                            self.wheel = [0.0, 0.0]
                            self.grip = None
                        if self.grip is not None:
                            continue
                        elif self.at is not None:
                            self._send_hover()
                        continue
                    fx, fy = self.at
                    lx, ly = self.dragged_from
                    self.dragged_from = (fx, fy)
                    clipped = False
                    if self.scrolling:  # a wheel scroll ignores the pointer's motion
                        step, clipped = self._scroll_step()
                        for i in (0, 1):
                            self.wheel[i] -= step[i]
                            self.carry[i] += step[i]
                    else:
                        self.carry[0] += (fx - lx) * touch.SCREEN_PT[0]
                        self.carry[1] += (fy - ly) * touch.SCREEN_PT[1]
                    ux, uy = touch.whole_units(*self.carry)  # never more than the pointer moved
                    if self.scrolling:
                        if ux or uy:
                            self.moved_at = now
                        elif now - self.moved_at > SCROLL_STILL and (clipped or now - self.scrolled_at > SCROLL_STILL):
                            self._up()  # at rest, so no fling; leftover wheel regrips
                            self.carry = [0.0, 0.0]
                            self.log("scroll up", f"({self.touch[0]:.3f}, {self.touch[1]:.3f})",
                                     f"left {self.wheel[0]:.0f},{self.wheel[1]:.0f} pt")
                            continue
                    r = self.hid().cmd(f"md {ux} {uy} 0 0")
                    if "dropped" in r:  # the radio is behind: keep the motion for the next tick
                        continue
                    if ux or uy:
                        mag = (ux * ux + uy * uy) ** 0.5
                        moved = touch.points_for(mag)
                        self.carry[0] -= moved * ux / mag
                        self.carry[1] -= moved * uy / mag
                        self.touch[0] = clamp(self.touch[0] + moved * ux / mag / touch.SCREEN_PT[0])
                        self.touch[1] = clamp(self.touch[1] + moved * uy / mag / touch.SCREEN_PT[1])
            except Exception as e:
                if str(e) != self.last_error:  # say it once, not every tick
                    self.log("pointer error:", e)
                    self.last_error = str(e)
                time.sleep(0.5)


class Keys:
    """Keyboard state for the phone: modifiers plus up to six held keys (USB HID
    usage codes). Sends a full keyboard report on every change."""

    def __init__(self, hid):
        self.hid = hid
        self.mods = 0
        self.held = []
        self.consumers = set()  # held consumer keys: siri, home, search, eject

    def modifier(self, bit, down):
        self.mods = self.mods | bit if down else self.mods & ~bit
        self._send()

    def key(self, usage, down):
        if down and usage not in self.held:
            self.held.append(usage)
        elif not down and usage in self.held:
            self.held.remove(usage)
        else:
            return
        self._send()

    def consumer(self, name, down):
        """Hold or let go of a consumer key (Siri, Home, Spotlight, Eject)."""
        if down == (name in self.consumers):
            return
        (self.consumers.add if down else self.consumers.discard)(name)
        self.hid().cmd(f"consumer {name} {'down' if down else 'up'}")

    def release_all(self):
        if self.mods or self.held:
            self.mods, self.held = 0, []
            self._send()
        if self.consumers:
            self.consumers.clear()
            self.hid().cmd("consumer up")

    def _send(self):
        self.hid().cmd("k %d %s" % (self.mods, " ".join(str(c) for c in self.held[-6:])))
