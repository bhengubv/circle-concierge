"""Client for the phoneuse HID daemon's command socket (see hid_daemon.py)."""
import socket
import threading

from .config import HID_SOCKET
from .host_mode import native_linux


class Hid:
    """One connection to the daemon. Thread-safe: commands are serialized."""

    def __init__(self, path=HID_SOCKET):
        if not native_linux():
            from .bridge_hid import BridgeHid
            self.bridge = BridgeHid()
            return
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.connect(path)
        self.f = self.sock.makefile()
        self.lock = threading.Lock()

    def cmd(self, line):
        if not native_linux():
            return self.bridge.cmd(line)
        with self.lock:
            self.sock.sendall((line + "\n").encode())
            reply = self.f.readline().strip()
        if not reply.startswith("ok"):
            raise RuntimeError(reply)
        return reply
