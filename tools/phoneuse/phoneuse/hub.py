"""The video hub: decodes the phone's stream once and encodes it again for every
viewer (the mirror window, the browser mirror, the frames service), with a fresh
keyframe whenever one of them joins.

iOS sends a full keyframe only when mirroring starts (none in a measured minute
of use), so anything that starts later couldn't decode the phone's own stream.
The hub keeps the last decoded frame and, when a viewer joins, encodes that
frame again as a keyframe, so the viewer shows the phone at once. Viewers can
therefore restart at any time. The hub itself can't without Screen Mirroring
being restarted, so it does only this and should rarely change: everything
else (settling, screenshots, drawing) lives in the viewers.

UxPlay sends RTP to the hub (screen.HUB_RTP_PORT); viewers receive RTP on their
own localhost ports. Control is a unix socket (config.HUB_SOCKET), one line per
request:

  join PORT    send the stream to 127.0.0.1:PORT, starting with a keyframe
  keep PORT    stay subscribed (at least every few seconds, or the viewer is
               dropped); a port the hub doesn't know is joined as above
  keyframe     encode the last frame again as a keyframe
  status       "ok live=1" while it has a picture of the phone, else live=0
"""
import os
import socket
import threading
import time

import gi

gi.require_version("Gst", "1.0")
gi.require_version("GstVideo", "1.0")
from gi.repository import GLib, Gst, GstVideo  # noqa: E402

from . import screen  # noqa: E402
from .config import HUB_SOCKET, decoder  # noqa: E402

VIEWER_TIMEOUT = 6.0  # drop a viewer that hasn't kept its subscription for this long (s)


def log(*a):
    print(time.strftime("%H:%M:%S"), "hub:", *a, flush=True)


def encoder(dec):
    """No B-frames or lookahead (each frame leaves the encoder as soon as it is
    encoded), keyframes only on request, high quality for text."""
    if dec.startswith("nvh264dec"):
        return ("nvh264enc preset=p2 tune=ultra-low-latency zerolatency=true bframes=0 gop-size=-1 "
                "rc-mode=vbr const-quality=18 max-bitrate=60000 repeat-sequence-header=true")
    return "x264enc tune=zerolatency speed-preset=veryfast key-int-max=0 pass=quant quantizer=18"


class Hub:
    def __init__(self, dec):
        self.last = None  # last decoded frame (a Gst.Buffer in system memory)
        self.caps = None
        self.viewers = {}  # port -> last join time
        self.lock = threading.Lock()
        rtp = "application/x-rtp,media=video,encoding-name=H264,clock-rate=90000,payload=96"
        # System-memory output: in CUDA output mode NVDEC hands out its own decode
        # surfaces, which iOS's long reference chains reuse while we still hold
        # the last one (green smears). Copying out costs ~1 ms per frame.
        download = "video/x-raw ! " if dec.startswith("nvh264dec") else ""
        # One pipeline for decode and encode, so they share a GPU context.
        self.pipe = Gst.parse_launch(
            f'udpsrc address=127.0.0.1 port={screen.HUB_RTP_PORT} buffer-size=8388608 caps="{rtp}" '
            f"! rtph264depay ! h264parse ! {dec} ! {download}appsink name=raw sync=false async=false emit-signals=true "
            f"appsrc name=src is-live=true format=time do-timestamp=true ! {encoder(dec)} "
            f"! h264parse ! rtph264pay pt=96 mtu=60000 config-interval=-1 "
            f"! multiudpsink name=out sync=false async=false")
        self.src = self.pipe.get_by_name("src")
        self.out = self.pipe.get_by_name("out")
        self.pipe.get_by_name("raw").connect("new-sample", self._decoded)
        bus = self.pipe.get_bus()
        bus.add_signal_watch()
        bus.connect("message::error", lambda _b, m: log("stream error:", m.parse_error()[0].message))
        self.pipe.set_state(Gst.State.PLAYING)

    def _decoded(self, sink):
        sample = sink.emit("pull-sample")
        if self.caps is None or not sample.get_caps().is_equal(self.caps):
            self.caps = sample.get_caps()
            self.src.set_property("caps", self.caps)
        self.last = sample.get_buffer()
        self.src.emit("push-buffer", self.last.copy())
        return Gst.FlowReturn.OK

    def keyframe(self):
        self.out.send_event(GstVideo.video_event_new_upstream_force_key_unit(Gst.CLOCK_TIME_NONE, True, 0))
        if self.last is not None:
            self.src.emit("push-buffer", self.last.copy())

    def join(self, port):
        """Add or refresh a viewer; True when it is new (it needs a keyframe)."""
        with self.lock:
            new = port not in self.viewers
            if new:
                self.out.emit("add", "127.0.0.1", port)
                log("viewer on port", port)
            self.viewers[port] = time.time()
            return new

    def expire(self):
        while True:
            time.sleep(1)
            with self.lock:
                for port, seen in list(self.viewers.items()):
                    if time.time() - seen > VIEWER_TIMEOUT:
                        self.out.emit("remove", "127.0.0.1", port)
                        del self.viewers[port]
                        log("viewer on port", port, "left")
            if self.last is not None and not screen.airplay_mirroring():
                self.last = None  # mirroring ended: the last frame no longer shows the phone

    def serve(self):
        from . import ipc
        s = ipc.listen(HUB_SOCKET, 16)
        log("control on", HUB_SOCKET)
        while True:
            conn, _ = s.accept()
            threading.Thread(target=self._client, args=(conn,), daemon=True).start()

    def _client(self, conn):
        with conn:
            for line in conn.makefile("rb"):
                cmd, _, arg = line.decode().strip().partition(" ")
                try:
                    if cmd == "join":
                        self.join(int(arg))
                        self.keyframe()
                    elif cmd == "keep":
                        if self.join(int(arg)):  # unknown viewer (the hub restarted): it needs a keyframe
                            self.keyframe()
                    elif cmd == "keyframe":
                        self.keyframe()
                    elif cmd == "status":
                        conn.sendall(f"ok live={int(self.last is not None)}\n".encode())
                        continue
                    else:
                        conn.sendall(b"err unknown command\n")
                        continue
                    conn.sendall(b"ok\n")
                except Exception as e:
                    log("control error:", e)
                    conn.sendall(f"err {e}\n".encode())


def main():
    Gst.init(None)
    hub = Hub(os.environ.get("PHONEUSE_DECODER") or decoder())
    threading.Thread(target=hub.serve, daemon=True).start()
    threading.Thread(target=hub.expire, daemon=True).start()
    log("receiving the phone's video on port", screen.HUB_RTP_PORT)
    GLib.MainLoop().run()


if __name__ == "__main__":
    main()
