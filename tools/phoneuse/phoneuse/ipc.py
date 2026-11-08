"""Local control sockets: Unix domain sockets or authenticated loopback on Windows."""
import hashlib
import os
import secrets
import socket
import sys

from .config import RUNTIME_DIR, user_dir


def _port(name):
    identity = os.path.expanduser("~") + ":" + name
    return 49152 + int.from_bytes(hashlib.sha256(identity.encode()).digest()[:4], "big") % 12000


def _secret(name, create):
    path = os.path.join(user_dir(), "ipc-" + os.path.basename(name) + ".key")
    if create:
        os.makedirs(os.path.dirname(path), mode=0o700, exist_ok=True)
        try:
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        except FileExistsError:
            pass
        else:
            with os.fdopen(fd, "w") as out:
                out.write(secrets.token_hex(32))
    with open(path, encoding="ascii") as source:
        return source.read().strip()


class _WindowsListener:
    def __init__(self, sock, secret):
        self.sock = sock
        self.secret = secret.encode() + b"\n"

    def accept(self):
        while True:
            conn, address = self.sock.accept()
            conn.settimeout(2)
            try:
                auth = b""
                while len(auth) <= len(self.secret) and not auth.endswith(b"\n"):
                    chunk = conn.recv(1)
                    if not chunk:
                        break
                    auth += chunk
                if secrets.compare_digest(auth, self.secret):
                    conn.settimeout(None)
                    return conn, address
            except OSError:
                pass
            conn.close()

    def close(self):
        self.sock.close()


def listen(name, backlog=16):
    if sys.platform == "win32":
        secret = _secret(name, create=True)
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.bind(("127.0.0.1", _port(name)))
        sock.listen(backlog)
        return _WindowsListener(sock, secret)
    os.makedirs(RUNTIME_DIR, mode=0o700, exist_ok=True)
    try:
        os.unlink(name)
    except FileNotFoundError:
        pass
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    sock.bind(name)
    os.chmod(name, 0o600)
    sock.listen(backlog)
    return sock


def connect(name, timeout=None):
    if sys.platform == "win32":
        secret = _secret(name, create=False)
        sock = socket.create_connection(("127.0.0.1", _port(name)), timeout=timeout)
        sock.sendall(secret.encode() + b"\n")
        return sock
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    if timeout is not None:
        sock.settimeout(timeout)
    sock.connect(name)
    return sock
