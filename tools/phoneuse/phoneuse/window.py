"""The mirror window: an ordinary window filled with a dark bezel, with the
phone picture in the middle.

The video hub (hub.py) sends the phone's H.264 stream here as RTP over
localhost, starting with a keyframe when this window joins; this process decodes
it and draws each frame. Hyprland draws its usual
decorations around it; a Hyprland rule floats the window and keeps the aspect
ratio when you resize it. If the window is resized to another shape anyway, the
phone is drawn aspect-fit and centered, never stretched.

Mouse input over the window is handled by mirror_input.py (it reads the mouse
from /dev/input); presses on the bezel become touches at the nearest screen edge.
"""
import os
import subprocess
import sys
import threading
import time

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Gdk", "4.0")
gi.require_version("Gst", "1.0")
gi.require_version("Graphene", "1.0")
gi.require_version("Gsk", "4.0")
gi.require_version("Pango", "1.0")
gi.require_version("PangoCairo", "1.0")
from gi.repository import Gdk, GLib, Graphene, Gsk, Gst, Gtk, Pango, PangoCairo  # noqa: E402

from . import screen  # noqa: E402
from .config import CFG, NAME, decoder  # noqa: E402

APP_ID = screen.MIRROR_CLASS
RTP_PORT = screen.RTP_PORT
BEZEL = Gdk.RGBA(red=0x18 / 255, green=0x18 / 255, blue=0x1C / 255, alpha=1)
BLACK = Gdk.RGBA(red=0, green=0, blue=0, alpha=1)


def window_rule():
    """Hyprland: float the window at the phone's shape; resizing keeps the aspect
    ratio. Its decorations stay the user's own. The size isn't remembered, because
    keep_aspect_ratio would lock the shape of a remembered size. Lua-config
    Hyprland first; classic keywords as a fallback (untested)."""
    w, h = screen.window_size(CFG["mirror_width"])
    lua = (f'hl.window_rule({{ name = "phoneuse-float", match = {{ class = "^{APP_ID}$" }}, '
           "float = true, "
           f"keep_aspect_ratio = true, persistent_size = false, size = {{ {w}, {h} }} }})")
    if screen.hypr_eval(lua):
        return
    m = f"class:^({APP_ID})$"
    subprocess.run(["hyprctl", "--batch", " ; ".join(
        f"keyword windowrule {r}, {m}" for r in
        ("float", "keepaspectratio", f"size {w} {h}"))],
        capture_output=True)


def pipeline_desc(dec):
    caps = "application/x-rtp,media=video,encoding-name=H264,clock-rate=90000,payload=96"
    # sync=false: show each frame the moment it is decoded (the phone's timestamps
    # would add a buffer). glsinkbin keeps NVDEC output on the GPU.
    return (f'udpsrc address=127.0.0.1 port={RTP_PORT} buffer-size=8388608 caps="{caps}" '
            f"! rtph264depay ! h264parse ! {dec} "
            "! glsinkbin sink=\"gtk4paintablesink name=sink sync=false\"")


class Phone(Gtk.Widget):
    """Fills the window with the bezel colour and draws the phone picture,
    aspect-fit, in the middle, with the screen's rounded corners."""

    def __init__(self):
        super().__init__()
        self.paintable = None
        self.waiting = True

    def set_paintable(self, p):
        self.paintable = p
        p.connect("invalidate-contents", self.on_frame)
        p.connect("invalidate-size", self.on_frame)

    def on_frame(self, *_):
        if self.waiting and self.paintable.get_intrinsic_width() > 0 and screen.airplay_mirroring():
            self.set_waiting(False)
        self.queue_draw()

    def set_waiting(self, waiting):
        """Waiting = not showing the phone (no stream yet, or mirroring ended).
        The title tells mirror_input and the CLI not to send input meanwhile."""
        self.waiting = waiting
        root = self.get_root()
        if root:
            root.set_title(screen.TITLE_WAITING if waiting else screen.TITLE_LIVE)
        self.queue_draw()

    def check_mirroring(self):
        # The picture freezes on its last frame when mirroring ends; go back to
        # waiting. A later stream needs a fresh keyframe, which iOS sends when
        # mirroring starts, so the next frame after that shows it.
        if not self.waiting and not screen.airplay_mirroring():
            self.set_waiting(True)
        elif self.waiting:
            self.queue_draw()  # the hint depends on whether the phone is mirroring
        return True

    def do_snapshot(self, snap):
        w, h = self.get_width(), self.get_height()
        sx, sy, sw, sh = screen.picture_rect((0, 0), (w, h))
        snap.append_color(BEZEL, Graphene.Rect().init(0, 0, w, h))
        # the screen, clipped to its rounded corners
        rect = Graphene.Rect().init(sx, sy, sw, sh)
        clip = Gsk.RoundedRect()
        clip.init_from_rect(rect, screen.SCREEN_RADIUS * sw)
        snap.push_rounded_clip(clip)
        snap.append_color(BLACK, rect)
        if not self.waiting:
            snap.translate(Graphene.Point().init(sx, sy))
            self.paintable.snapshot(snap, sw, sh)
            snap.translate(Graphene.Point().init(-sx, -sy))
        snap.pop()
        if not self.waiting:
            return
        cr = snap.append_cairo(rect)
        # Until the first frame: how to start mirroring. Pango picks a fallback
        # font for any glyph the default font lacks.
        pl = PangoCairo.create_layout(cr)
        pl.set_font_description(Pango.FontDescription.from_string(f"sans {max(9.0, sw * 0.03):.1f}px"))
        pl.set_alignment(Pango.Alignment.CENTER)
        pl.set_width(int(sw * 0.86 * Pango.SCALE))
        if screen.airplay_mirroring():  # this window started after mirroring did: no keyframe yet
            pl.set_markup("The iPhone is mirroring, but this window started later.\n"
                          "Stop and start <b>Screen Mirroring</b> once to show it here.", -1)
        else:
            pl.set_markup("On the iPhone, open Control Center\n"
                          f"<b>Screen Mirroring</b>  ›  <b>{GLib.markup_escape_text(NAME)}</b>", -1)
        _ink, logical = pl.get_pixel_extents()
        cr.set_source_rgba(1, 1, 1, 0.55)
        cr.move_to(sx + sw * 0.07, sy + (sh - logical.height) / 2)
        PangoCairo.show_layout(cr, pl)


class Mirror(Gtk.Application):
    def __init__(self, dec):
        super().__init__()
        self.dec = dec
        self.pipe = None
        self.phone = None

    def do_activate(self):
        win = Gtk.ApplicationWindow(application=self, title=screen.TITLE_WAITING)
        win.set_decorated(False)
        w, h = screen.window_size(CFG["mirror_width"])
        win.set_default_size(w, h)
        self.phone = Phone()
        win.set_child(self.phone)
        self.start()
        win.present()
        GLib.timeout_add(500, self.phone.check_mirroring)
        threading.Thread(target=self.stay_joined, daemon=True).start()

    def stay_joined(self):
        """Subscribe to the hub's stream and keep the subscription alive. The first
        join (and any after the hub restarts) brings a keyframe."""
        joined = False
        while True:
            try:
                line, _ = screen.hub_request(f"{'keep' if joined else 'join'} {RTP_PORT}")
                joined = line.startswith("ok")
            except OSError:
                joined = False
            time.sleep(2)

    def start(self):
        self.pipe = Gst.parse_launch(pipeline_desc(self.dec))
        sink = self.pipe.get_by_name("sink")
        self.phone.set_paintable(sink.get_property("paintable"))
        bus = self.pipe.get_bus()
        bus.add_signal_watch()
        bus.connect("message::error", self.on_error)
        self.pipe.set_state(Gst.State.PLAYING)

    def on_error(self, _bus, msg):
        err, _debug = msg.parse_error()
        print("mirror window: stream error:", err.message, file=sys.stderr, flush=True)
        # Start over and wait for the next stream (UxPlay restarted, phone reconnected).
        self.pipe.set_state(Gst.State.NULL)
        self.phone.set_waiting(True)
        GLib.timeout_add(500, self.restart)

    def restart(self):
        self.start()
        return False


def main():
    Gst.init(None)
    GLib.set_prgname(APP_ID)  # Wayland app id = Hyprland window class
    window_rule()
    dec = os.environ.get("PHONEUSE_DECODER") or decoder()
    Mirror(dec).run([])


if __name__ == "__main__":
    main()
