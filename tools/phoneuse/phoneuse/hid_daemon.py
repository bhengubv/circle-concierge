#!/usr/bin/python
"""Bluetooth HID mouse + keyboard for an iPhone, driven over a Unix socket.

Run as root. bluetoothd must be started with --noplugin=input so this process
can own the HID PSMs (17 control, 19 interrupt). See install.sh.

Commands on /run/phoneuse-hid.sock, one per line, reply "ok ..." or "err ...":
  move DX DY        relative pointer move (split into small steps)
  click [left|right|middle]
  down [btn] / up   hold / release a button (for drags)
  scroll N          wheel; positive scrolls up
  key NAME [MOD..]  e.g. "key h gui" (Cmd+H = home), "key enter", "key tab shift"
  type TEXT         US-layout text
  corner            slam pointer to the top-left corner
  connect           page the paired iPhone now (the daemon also reconnects by itself)
  release [IDLE]    drop the link while nobody uses it, so the iPhone shows its own keyboard
                    (iOS hides it while a keyboard is connected); only if nothing was sent
                    for IDLE seconds (default 0). The next real input reconnects first.
  pair [SECONDS]    discoverable/pairable for SECONDS (0 = always, the startup default)
  m DX DY [W [BTN]] raw mouse report (live input)
  k MODS [CODE..]   raw keyboard report: held modifiers + up to 6 HID usage codes
  status
"""
import fcntl
import os
import socket
import struct
import termios
import threading
import time
import unicodedata

import dbus
import dbus.mainloop.glib
import dbus.service
from gi.repository import GLib

HERE = os.path.dirname(os.path.abspath(__file__))
from .config import HID_SOCKET as SOCK_PATH
from .hid_keys import BUTTONS, CHARS, CONSUMER_KEYS, MODS, NAMED
DEVICE_NAME = os.environ.get("PHONEUSE_NAME", "PhoneUse")  # what the phone sees in Bluetooth settings
PSM_CTRL, PSM_INTR = 0x11, 0x13
HID_UUID = "00001124-0000-1000-8000-00805f9b34fb"
PROFILE_PATH = "/io/phoneuse/hid/profile"
AGENT_PATH = "/io/phoneuse/hid/agent"

# Report 1: boot-style keyboard (mods, reserved, 6 keys). Report 2: mouse (buttons, x, y, wheel).
REPORT_MAP = bytes.fromhex(
    "05010906a1018501050719e029e71500250175019508810295017508810395067508150025650507190029658100c0"
    +"05010902a10185020901a1000509190129031500250195037501810295017505810305010930093109381581257f750895038106c0c0"
    # Report 3: absolute pointer (buttons, X and Y 0..32767). Experimental: tests
    # whether iOS will place the AssistiveTouch pointer at exact coordinates.
    + "05010902a10185030901a1000509190129031500250195037501810295017505810305010930093115002"
    "6ff7f751095028102c0c0"
    # Report 4: consumer control, one bit per key (CONSUMER_KEYS, in this order).
    + "050c0901a10185041500250175019501"
    + "09b88102" "09cf8102" "09408102" "0a21028102"  # Eject, Voice Command, Menu, AC Search
    + "95048103c0"
)

# Consumer keys are defined in hid_keys.py, shared with the bridge host.


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


# ---------- key tables (US layout) live in hid_keys.py ----------


# While set, input goes through the ESP32 bridge (phoneuse/bridge.py) over Wi-Fi
# instead of this PC's Bluetooth: the iPhone pairs with the bridge.
bridge_link = [None]
bridge_phone = [False]


class HID:
    def __init__(self):
        self.ctrl = None
        self.intr = None
        self.peer = None
        self.buttons = 0
        self.consumer_bits = 0
        self.lock = threading.Lock()

    @property
    def connected(self):
        if bridge_link[0]:
            return bridge_link[0].phone
        return self.intr is not None

    def _bridge(self, report_id, payload, skippable=False):
        r = bridge_link[0].report(report_id, payload, skippable)
        bridge_phone[0] = r != "no phone"
        if r == "no phone":
            raise RuntimeError("no iPhone connected to the bridge (pair it with PhoneUse Bridge)")
        return r == "sent"

    def send_skippable(self, report_id, payload):
        """Send a hover or motion report unless earlier ones still wait to go out
        (then the sender keeps the motion for its next report). True if sent."""
        if bridge_link[0]:
            link = bridge_link[0]
            if not link.phone:
                raise RuntimeError("no iPhone connected to the bridge (pair it with PhoneUse Bridge)")
            if report_id == 3:  # a hover: newest wins on the bridge, no answer
                link.send_latest(report_id, payload)
                return True
            if link.send_async(report_id, payload):  # motion: pipelined, counted
                return True
            busy.note(0, link.waiting)
            return False
        if self.backlog() or inflight.n >= 2:
            busy.note(self.backlog(), inflight.n)
            return False
        self.send(report_id, payload)
        return True

    def send(self, report_id, payload):
        if bridge_link[0]:
            self._bridge(report_id, payload)
            return
        with self.lock:
            if not self.intr:
                reconnect_now.set()
                raise RuntimeError("no iPhone connected, reconnecting")
            try:
                self.intr.send(bytes([0xA1, report_id]) + bytes(payload))
            except OSError as e:
                self.drop(f"send failed: {e}")
                raise RuntimeError("connection lost")

    def backlog(self):
        """Bytes of earlier reports still waiting in the kernel to reach the phone
        (TIOCOUTQ on a Bluetooth socket is the free send space)."""
        try:
            free = struct.unpack("i", fcntl.ioctl(self.intr.fileno(), termios.TIOCOUTQ, b"\0" * 4))[0]
            return max(0, self.intr.getsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF) - free)
        except (OSError, AttributeError):
            return 0

    def drop(self, why):
        log("disconnect:", why)
        for s in (self.intr, self.ctrl):
            try:
                s and s.close()
            except OSError:
                pass
        self.ctrl = self.intr = None
        reconnect_now.set()

    # mouse
    def mouse(self, dx=0, dy=0, wheel=0):
        b = lambda v: v & 0xFF
        payload = [self.buttons, b(dx), b(dy), b(wheel)]
        link = bridge_link[0]
        if link and link.phone:
            # Through the bridge, scripted swipes go pipelined: waiting for each
            # report's answer (14-35 ms over Wi-Fi) would stretch a 0.3 s flick
            # into a slow drag. Every report still goes out, in order: when 5 are
            # unanswered, wait for room instead of dropping one.
            end = time.time() + 0.3
            while not link.send_async(2, payload):
                if time.time() > end:
                    self._bridge(2, payload)
                    return
                time.sleep(0.002)
            return
        self.send(2, payload)

    def move(self, dx, dy, step=8, delay=0.008):
        # Small, evenly paced steps keep iOS pointer acceleration predictable.
        while dx or dy:
            sx = max(-step, min(step, dx))
            sy = max(-step, min(step, dy))
            self.mouse(sx, sy)
            dx -= sx
            dy -= sy
            time.sleep(delay)

    def corner(self):
        for _ in range(60):
            self.mouse(-127, -127)
            time.sleep(0.008)

    def press(self, btn):
        self.buttons |= BUTTONS[btn]
        self.mouse()

    def release(self):
        self.buttons = 0
        self.mouse()

    def click(self, btn="left"):
        self.press(btn)
        time.sleep(0.05)
        self.release()

    def scroll(self, n):
        sign = 1 if n > 0 else -1
        for _ in range(abs(n)):
            self.mouse(wheel=sign)
            time.sleep(0.02)

    # keyboard
    def consumer(self, name, hold=0.05):
        """Press a consumer key (CONSUMER_KEYS) for `hold` seconds, then release it."""
        self.consumer_set(name, True)
        time.sleep(hold)
        self.consumer_set(name, False)

    def consumer_set(self, name, down):
        """Hold or let go of one consumer key (None lets go of all of them)."""
        bit = 1 << CONSUMER_KEYS[name] if name else 0xFF
        self.consumer_bits = self.consumer_bits | bit if down else self.consumer_bits & ~bit
        self.send(4, [self.consumer_bits])

    def eject(self):
        """Press and release Eject: toggles the iPhone's on-screen keyboard."""
        self.consumer("eject")

    def key(self, code, mods=0):
        self.send(1, [mods, 0, code, 0, 0, 0, 0, 0])
        time.sleep(0.012)
        self.send(1, [0] * 8)
        time.sleep(0.012)

    def type(self, text):
        for ch in text:
            if ch not in CHARS:
                raise ValueError(f"cannot type {ch!r}")
            self.key(*CHARS[ch])


hid = HID()


# ---------- link tuning ----------
# The iPhone is the master of the link and may poll us only every ~50 ms, which
# caps the pointer at ~20 updates/s (measured 2026-11-06: acks 51 ms apart).
# A QoS Setup asking for 8 ms latency brings that to ~2 ms, as real mice do.
HCIGETCONNINFO = 0x800448D5


def acl_handle(addr):
    import fcntl
    s = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_RAW, socket.BTPROTO_HCI)
    try:
        s.bind((0,))
        ba = bytes(int(x, 16) for x in reversed(addr.split(":")))
        buf = bytearray(ba + bytes([1]) + bytes(24))  # hci_conn_info_req, type ACL
        fcntl.ioctl(s.fileno(), HCIGETCONNINFO, buf, True)
        return struct.unpack_from("<H", buf, 8)[0]
    finally:
        s.close()


def tune_link(addr):
    try:
        h = acl_handle(addr)
        s = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_RAW, socket.BTPROTO_HCI)
        s.bind((0,))

        def cmd(ogf, ocf, p):
            s.send(bytes([1]) + struct.pack("<HB", (ogf << 10) | ocf, len(p)) + p)
        cmd(0x02, 0x000D, struct.pack("<HH", h, 0x0001))  # link policy: role switch only, no sniff
        time.sleep(0.02)
        # QoS Setup: guaranteed, 800 B/s, latency 8000 us
        cmd(0x02, 0x0007, struct.pack("<HBBIIII", h, 0, 0x02, 800, 0, 8000, 0xFFFFFFFF))
        s.close()
        return h
    except OSError as e:
        log("tune_link failed:", e)
        return None


def tune_loop():
    while True:
        time.sleep(20)
        if hid.connected and hid.peer:
            tune_link(hid.peer)


# ---------- L2CAP ----------
def handle_ctrl(sock):
    # Answer HID control-channel requests (SET_PROTOCOL, SET_IDLE, ...) with HANDSHAKE OK.
    while True:
        try:
            data = sock.recv(64)
        except OSError:
            break
        if not data:
            break
        t = data[0] >> 4
        if t == 0x0:  # HANDSHAKE from host: ignore
            continue
        if t == 0x1 and data[0] & 0x0F in (0x3, 0x4, 0x5):  # HID_CONTROL suspend/exit/unplug
            log("control op", hex(data[0]))
            continue
        try:
            if t == 0x4:  # GET_REPORT -> empty report of the asked id
                rid = data[1] if len(data) > 1 else 1
                sock.send(bytes([0xA1, rid]) + bytes(8 if rid == 1 else 4))
            elif t == 0x6:  # GET_PROTOCOL -> report protocol
                sock.send(b"\xa0\x01")
            else:
                sock.send(b"\x00")
        except OSError:
            break
    if hid.ctrl is sock:
        hid.drop("control channel closed")


def listen(psm):
    s = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_SEQPACKET, socket.BTPROTO_L2CAP)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind((socket.BDADDR_ANY, psm))
    s.listen(1)
    return s


def accept_loop():
    sc, si = listen(PSM_CTRL), listen(PSM_INTR)
    log("listening on PSM 17/19")
    while True:
        c, addr = sc.accept()
        if released.is_set():
            log("declined HID connection from", addr[0], "(released until phoneuse is used)")
            c.close()
            continue
        if not wanted(addr[0]):
            log("declined HID connection from", addr[0], f"(not the mirroring phone, {mirror_name[0]!r})")
            c.close()
            continue
        log("control from", addr[0])
        i, _ = si.accept()
        log("interrupt from", addr[0])
        if hid.connected:
            hid.drop("replaced by new connection")
        hid.ctrl, hid.intr, hid.peer = c, i, addr[0]
        threading.Thread(target=handle_ctrl, args=(c,), daemon=True).start()
        log("link tuned, handle", tune_link(addr[0]))


connect_lock = threading.Lock()
reconnect_now = threading.Event()
class InFlight:
    """Reports handed to the Bluetooth controller that it hasn't sent yet. The
    controller buffers a few ACL packets beyond the kernel's socket queue (which
    backlog() sees); on a slow link (35 reports/s at times, when the phone's
    radio is busy with the AirPlay video) four of them are ~0.15 s of delay.
    Counted from the kernel's HCI monitor channel: ACL packets sent minus the
    controller's Number of Completed Packets events."""
    AF_BLUETOOTH, BTPROTO_HCI, HCI_CHANNEL_MONITOR = 31, 1, 2

    def __init__(self):
        self.n = 0
        self.lock = threading.Lock()
        try:
            import ctypes
            self.sock = socket.socket(self.AF_BLUETOOTH, socket.SOCK_RAW, self.BTPROTO_HCI)
            addr = struct.pack("<HHH", self.AF_BLUETOOTH, 0xFFFF, self.HCI_CHANNEL_MONITOR)
            libc = ctypes.CDLL(None, use_errno=True)
            if libc.bind(self.sock.fileno(), addr, len(addr)) != 0:
                raise OSError(ctypes.get_errno(), "bind HCI monitor")
            threading.Thread(target=self._read, daemon=True).start()
        except OSError as e:
            log("in-flight count off:", e)

    def _read(self):
        while True:
            try:
                b = self.sock.recv(4096)
            except OSError:
                return
            if len(b) < 7:
                continue
            op = struct.unpack_from("<H", b)[0]
            if op == 4:  # ACL data sent to the controller
                with self.lock:
                    self.n += 1
            elif op == 3 and b[6] == 0x13 and len(b) >= 9:  # Number of Completed Packets
                done = sum(struct.unpack_from("<H", b, 9 + 4 * i + 2)[0] for i in range(b[8])
                           if 9 + 4 * i + 4 <= len(b))
                with self.lock:
                    self.n = max(0, self.n - done)


inflight = InFlight()


class Busy:
    """Counts reports skipped because the link was backed up, and logs once a
    second while it happens (how often the radio falls behind, and how far)."""

    def __init__(self):
        self.lock = threading.Lock()
        self.skips, self.worst, self.since, self.most = 0, 0, 0.0, 0

    def note(self, queued, waiting=0):
        with self.lock:
            now = time.time()
            if not self.skips:
                self.since = now
            self.skips += 1
            self.worst = max(self.worst, queued)
            self.most = max(self.most, waiting)
            if now - self.since >= 1.0:
                log(f"link busy: skipped {self.skips} reports in {now - self.since:.1f} s, "
                    f"up to {self.worst} bytes queued, {self.most} in the controller")
                self.skips, self.worst, self.most = 0, 0, 0


busy = Busy()
released = threading.Event()  # link dropped on purpose: no reconnecting until real input
last_use = [0.0]  # when a command last sent real input (not a hover)


def connect_out():
    """Page each paired iPhone once and open the HID channels."""
    if bridge_link[0]:
        return "input goes through the bridge"
    with connect_lock:
        released.clear()
        if hid.connected:
            return "already connected"
        errors = []
        for addr in [a for a in paired_devices() if wanted(a)]:
            socks = []
            try:
                for psm in (PSM_CTRL, PSM_INTR):
                    s = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_SEQPACKET, socket.BTPROTO_L2CAP)
                    socks.append(s)
                    s.settimeout(8)
                    s.connect((addr, psm))
                    s.settimeout(None)
            except OSError as e:
                for s in socks:
                    s.close()
                errors.append(f"{addr}: {e}")
                continue
            if hid.connected:  # the iPhone connected in by itself meanwhile
                for s in socks:
                    s.close()
                return "already connected"
            hid.ctrl, hid.intr = socks
            hid.peer = addr
            log("connected out to", addr, "- link tuned, handle", tune_link(addr))
            threading.Thread(target=handle_ctrl, args=(hid.ctrl,), daemon=True).start()
            return f"connected to {addr}"
        raise RuntimeError("; ".join(errors) or "no paired iPhone")


def reconnect_loop():
    # While disconnected, page the iPhone with a growing pause between tries
    # (5 s up to 30 s). Paging without pauses keeps the radio busy and makes it
    # miss the iPhone's own connection attempts. A disconnect, a command sent
    # while disconnected, or the iPhone coming into range (BlueZ sees it
    # connect) cuts the pause short.
    pause = 0
    while True:
        reconnect_now.wait(30 if hid.connected else pause)
        woke = reconnect_now.is_set()
        reconnect_now.clear()
        if hid.connected or released.is_set():
            pause = 0
            continue
        if woke:
            time.sleep(3)  # give the iPhone a moment to reconnect by itself
            if hid.connected or released.is_set():
                continue
        try:
            log("reconnect:", connect_out())
            pause = 0
        except Exception as e:
            pause = min(30, max(5, pause * 2))
            log(f"reconnect failed ({e}); next try in {pause}s")


def on_device_changed(iface, changed, invalidated, path=None):
    if iface == "org.bluez.Device1" and changed.get("Connected") and not hid.connected and not released.is_set():
        reconnect_now.set()


# ---------- D-Bus: profile, agent, adapter ----------
bus = None


def adapter():
    return dbus.Interface(bus.get_object("org.bluez", "/org/bluez/hci0"), "org.freedesktop.DBus.Properties")


def device_name(addr):
    try:
        props = dbus.Interface(bus.get_object("org.bluez", "/org/bluez/hci0/dev_" + addr.upper().replace(":", "_")),
                               "org.freedesktop.DBus.Properties")
        return str(props.Get("org.bluez.Device1", "Alias"))
    except dbus.DBusException:
        return None


def same_name(a, b):
    n = lambda x: unicodedata.normalize("NFKC", x or "").replace("\u2019", "'").casefold().strip()
    return bool(a and b) and n(a) == n(b)


# The phone that is screen mirroring, by device name: `phoneuse up` reads it from UxPlay
# and sends "mirror NAME". AirPlay and Bluetooth both carry the name set in Settings ->
# General -> About -> Name, so HID goes to the phone whose screen we see.
mirror_name = [None]


def wanted(addr):
    """May this device use the HID channels? Only the mirroring phone, when it is paired;
    any paired device while no phone mirrors or the mirroring one isn't paired yet."""
    if not mirror_name[0] or not any(same_name(device_name(a), mirror_name[0]) for a in paired_devices()):
        return True
    return same_name(device_name(addr), mirror_name[0])


def set_mirror(name):
    mirror_name[0] = name.strip() or None
    save_state()
    log("mirroring phone:", mirror_name[0])
    if hid.connected and not wanted(hid.peer):
        hid.drop(f"{hid.peer} is not the mirroring phone")  # also wakes the reconnect loop
    else:
        reconnect_now.set()
    return f"mirror={mirror_name[0]!r} connected={hid.connected} peer={hid.peer}"


def disconnect_all():
    """Close this PC's Bluetooth connections to the paired phones (BlueZ Device1)."""
    om = dbus.Interface(bus.get_object("org.bluez", "/"), "org.freedesktop.DBus.ObjectManager")
    for path, ifaces in om.GetManagedObjects().items():
        d = ifaces.get("org.bluez.Device1")
        if d and d.get("Paired") and d.get("Connected"):
            try:
                dbus.Interface(bus.get_object("org.bluez", path), "org.bluez.Device1").Disconnect()
                log("disconnected", str(d["Address"]), "(input goes through the bridge)")
            except dbus.DBusException as e:
                log("couldn't disconnect", str(d["Address"]), e)


def paired_devices():
    om = dbus.Interface(bus.get_object("org.bluez", "/"), "org.freedesktop.DBus.ObjectManager")
    out = []
    for path, ifaces in om.GetManagedObjects().items():
        d = ifaces.get("org.bluez.Device1")
        if d and d.get("Paired"):
            out.append(str(d["Address"]))
    return out


class Profile(dbus.service.Object):
    @dbus.service.method("org.bluez.Profile1", in_signature="", out_signature="")
    def Release(self):
        pass

    @dbus.service.method("org.bluez.Profile1", in_signature="oha{sv}", out_signature="")
    def NewConnection(self, path, fd, props):
        os.close(fd.take())  # we own the PSMs directly

    @dbus.service.method("org.bluez.Profile1", in_signature="o", out_signature="")
    def RequestDisconnection(self, path):
        pass


class Agent(dbus.service.Object):
    # NoInputNoOutput: accept every pairing ("Just Works"). Pairing is only possible while
    # the adapter is discoverable, which the "pair" command opens for a limited time.
    @dbus.service.method("org.bluez.Agent1", in_signature="", out_signature="")
    def Release(self):
        pass

    @dbus.service.method("org.bluez.Agent1", in_signature="os", out_signature="")
    def AuthorizeService(self, device, uuid):
        pass

    @dbus.service.method("org.bluez.Agent1", in_signature="ou", out_signature="")
    def RequestConfirmation(self, device, passkey):
        log("pairing confirm", device, passkey)

    @dbus.service.method("org.bluez.Agent1", in_signature="o", out_signature="")
    def RequestAuthorization(self, device):
        pass

    @dbus.service.method("org.bluez.Agent1", in_signature="o", out_signature="s")
    def RequestPinCode(self, device):
        return "0000"

    @dbus.service.method("org.bluez.Agent1", in_signature="o", out_signature="u")
    def RequestPasskey(self, device):
        return dbus.UInt32(0)

    @dbus.service.method("org.bluez.Agent1", in_signature="ouq", out_signature="")
    def DisplayPasskey(self, device, passkey, entered):
        log("passkey", passkey)

    @dbus.service.method("org.bluez.Agent1", in_signature="os", out_signature="")
    def DisplayPinCode(self, device, pin):
        log("pin", pin)

    @dbus.service.method("org.bluez.Agent1", in_signature="", out_signature="")
    def Cancel(self):
        pass




def open_pairing(seconds):
    a = adapter()
    a.Set("org.bluez.Adapter1", "PairableTimeout", dbus.UInt32(seconds))
    a.Set("org.bluez.Adapter1", "DiscoverableTimeout", dbus.UInt32(seconds))
    a.Set("org.bluez.Adapter1", "Pairable", True)
    a.Set("org.bluez.Adapter1", "Discoverable", True)


def setup_dbus():
    global bus
    dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
    bus = dbus.SystemBus()
    record = (open(os.path.join(HERE, "sdp_record.xml")).read()
              .replace("@REPORT_MAP@", REPORT_MAP.hex()).replace("@NAME@", DEVICE_NAME))
    Profile(bus, PROFILE_PATH)
    pm = dbus.Interface(bus.get_object("org.bluez", "/org/bluez"), "org.bluez.ProfileManager1")
    pm.RegisterProfile(PROFILE_PATH, HID_UUID, {
        "ServiceRecord": record, "Role": "server",
        "RequireAuthentication": False, "RequireAuthorization": False})
    Agent(bus, AGENT_PATH)
    am = dbus.Interface(bus.get_object("org.bluez", "/org/bluez"), "org.bluez.AgentManager1")
    am.RegisterAgent(AGENT_PATH, "NoInputNoOutput")
    am.RequestDefaultAgent(AGENT_PATH)
    a = adapter()
    a.Set("org.bluez.Adapter1", "Powered", True)
    a.Set("org.bluez.Adapter1", "Alias", DEVICE_NAME)
    bus.add_signal_receiver(on_device_changed, "PropertiesChanged",
                            "org.freedesktop.DBus.Properties", "org.bluez", path_keyword="path")
    log("profile + agent registered")
    # Always discoverable and pairable, so any iPhone can (re)pair any time. Any
    # paired device may connect; a new connection replaces the current one.
    open_pairing(0)


# ---------- command socket ----------
def passive(cmd, args):
    """Reports that change nothing on the phone by themselves: hovers, motion
    without a button, all keys up. While released they are dropped instead of
    reconnecting, so moving the mouse over the mirror doesn't bring the
    keyboard back."""
    try:
        if cmd == "a":
            return len(args) < 3 or int(args[2]) == 0
        if cmd in ("m", "md"):
            return (len(args) < 3 or int(args[2]) == 0) and (len(args) < 4 or int(args[3]) == 0)
        if cmd == "k":
            return int(args[0]) == 0 and not any(int(c) for c in args[1:])
        if cmd == "consumer":
            return args[-1:] == ["up"]
    except ValueError:
        return False
    return cmd in ("move", "corner", "up")


def absolute(fx, fy, buttons):
    """Report 3 payload: buttons, then x and y as 0..32767."""
    x, y = (max(0, min(32767, round(float(v) * 32767))) for v in (fx, fy))
    return [buttons & 0x07, x & 0xFF, x >> 8, y & 0xFF, y >> 8]


def release(idle):
    with connect_lock:
        if time.time() - last_use[0] < idle:
            return "in use"
        released.set()
        if hid.connected:
            hid.drop("released: nobody is using phoneuse, so the iPhone shows its own keyboard")
        return "released"


STATE = "/var/lib/phoneuse/daemon.json"  # survives restarts: bridge and mirroring phone


def save_state():
    import json
    link = bridge_link[0]
    d = {"mirror": mirror_name[0], "bridge": [link.addr[0], link.key.hex()] if link else None}
    try:
        os.makedirs(os.path.dirname(STATE), exist_ok=True)
        fd = os.open(STATE + ".tmp", os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as f:
            json.dump(d, f)
        os.replace(STATE + ".tmp", STATE)
    except OSError as e:
        log("couldn't save state:", e)


def restore_state():
    import json
    try:
        with open(STATE) as f:
            d = json.load(f)
    except (OSError, ValueError):
        return
    if d.get("mirror"):
        mirror_name[0] = d["mirror"]
    if d.get("bridge"):
        for _ in range(10):  # the network may still be coming up
            try:
                log("restoring bridge:", set_bridge(d["bridge"]))
                return
            except Exception as e:
                log("bridge not answering yet:", e)
                time.sleep(3)


def set_bridge(args):
    """Switch input to the bridge (its address and control key) or back to this PC."""
    try:
        return _set_bridge(args)
    finally:
        save_state()


def _set_bridge(args):
    if args[:1] == ["off"] or not args:
        if bridge_link[0]:
            bridge_link[0].close()
        bridge_link[0] = None
        released.clear()
        reconnect_now.set()
        log("bridge off: input goes through this PC's Bluetooth again")
        return "bridge off"
    from .bridge import Link
    link = Link({"ip": args[0], "key": args[1]})
    link.hello()  # raises if it doesn't answer or the key is wrong
    st = link.status()
    if bridge_link[0]:
        bridge_link[0].close()
    bridge_link[0] = link
    bridge_phone[0] = link.phone = bool(st.get("connected"))
    released.set()  # this PC's own link to the phone would be a second keyboard: drop it
    if hid.intr:
        hid.drop("input goes through the bridge now")
    disconnect_all()  # the Bluetooth link itself too, or the phone still lists PhoneUse as connected
    log(f"bridge on: {args[0]}, phone {st.get('phone') or 'not paired'}")
    threading.Thread(target=watch_bridge, args=(link,), daemon=True).start()
    return f"bridge {args[0]} phone={st.get('phone')!r} connected={st.get('connected')}"


def watch_bridge(link):
    """Keep the bridge's phone state current, and while a phone is screen
    mirroring but not connected to the bridge, ask the bridge to page it."""
    while bridge_link[0] is link:
        time.sleep(2)
        try:
            bridge_phone[0] = link.phone = bool(link.status().get("connected"))
            if not link.phone and mirror_name[0]:
                link.connect_phone()
        except Exception as e:
            bridge_phone[0] = False
            log("bridge: no answer:", e)


def run_command(line):
    parts = line.strip().split(" ", 1)
    cmd, rest = parts[0].lower(), (parts[1] if len(parts) > 1 else "")
    args = rest.split()
    if cmd == "status":
        via = f" bridge={bridge_link[0].addr[0]}" if bridge_link[0] else ""
        return (f"ok connected={hid.connected} released={released.is_set()} peer={hid.peer} "
                f"paired={paired_devices()} mirror={mirror_name[0]!r}{via}")
    if cmd == "bridge":  # bridge IP KEYHEX | bridge off: send input through the ESP32 bridge
        return "ok " + set_bridge(args)
    if cmd == "connect":
        return "ok " + connect_out()
    if cmd == "mirror":  # mirror NAME: the phone that is screen mirroring
        return "ok " + set_mirror(rest)
    if cmd == "release":
        return "ok " + release(float(args[0]) if args else 0.0)
    if cmd == "pair":
        pass
    elif passive(cmd, args):
        if released.is_set() and not hid.connected:
            return "ok released"
    else:
        last_use[0] = time.time()
        if released.is_set() and not hid.connected and not bridge_link[0]:
            try:
                log("reconnect on use:", connect_out())
            except Exception:
                released.set()  # still released: no paging until the next try
                raise
            if cmd == "a":  # put the pointer there before the press (a press that moves ends the touch)
                hid.send(3, absolute(args[0], args[1], 0))
                time.sleep(0.05)
    if cmd == "eject":
        hid.eject()
        return "ok"
    if cmd == "consumer":  # consumer NAME [HOLD_SECONDS | down | up]: siri, home, search, eject
        if args == ["up"]:  # let go of every consumer key
            hid.consumer_set(None, False)
            return "ok"
        if not args or args[0] not in CONSUMER_KEYS:
            return "err consumer keys: " + ", ".join(CONSUMER_KEYS)
        how = args[1] if len(args) > 1 else "0.05"
        if how in ("down", "up"):
            hid.consumer_set(args[0], how == "down")
        else:
            hid.consumer(args[0], float(how))
        return "ok"
    if cmd == "pair":
        secs = int(args[0]) if args else 120
        GLib.idle_add(open_pairing, secs)
        return f"ok discoverable for {secs}s as '{DEVICE_NAME}'"
    if cmd == "a":  # absolute pointer: a FX FY [BUTTONS], FX/FY are 0..1 screen fractions
        buttons = int(args[2]) if len(args) > 2 else 0
        if not buttons and not hid.buttons:
            # A hover while earlier reports still wait to go out (in the socket, the
            # controller, or the bridge): skip it, so the pointer jumps to the newest
            # position instead of trailing a queue. The sender sends its newest
            # position again on its next tick.
            return "ok" if hid.send_skippable(3, absolute(args[0], args[1], 0)) else "ok dropped"
        hid.send(3, absolute(args[0], args[1], buttons))
        return "ok"
    if cmd in ("m", "md"):  # raw mouse report: m DX DY [WHEEL [BUTTONS]], for live input
        # md: the same, but skipped while earlier reports still wait to go out
        # (a slow radio); the sender keeps the motion and sends it in its next
        # report, so the touch jumps to the newest position instead of trailing.
        dx, dy = (max(-127, min(127, int(v))) for v in args[:2])
        wheel = max(-127, min(127, int(args[2]))) if len(args) > 2 else 0
        if len(args) > 3:
            hid.buttons = int(args[3]) & 0x07
        if cmd == "md":
            sent = hid.send_skippable(2, [hid.buttons, dx & 0xFF, dy & 0xFF, wheel & 0xFF])
            return "ok" if sent else "ok dropped"
        hid.mouse(dx, dy, wheel)
        return "ok"
    if cmd == "k":  # raw keyboard report: k MODS [CODE...], up to 6 held keys
        codes = [int(c) & 0xFF for c in args[1:7]]
        hid.send(1, [int(args[0]) & 0xFF, 0] + codes + [0] * (6 - len(codes)))
        return "ok"
    if cmd == "move":
        hid.move(int(args[0]), int(args[1]))
    elif cmd == "corner":
        hid.corner()
    elif cmd == "click":
        hid.click(args[0] if args else "left")
    elif cmd == "down":
        hid.press(args[0] if args else "left")
    elif cmd == "up":
        hid.release()
    elif cmd == "scroll":
        hid.scroll(int(args[0]))
    elif cmd == "key":
        name, mods = args[0].lower(), 0
        for m in args[1:]:
            mods |= MODS[m.lower()]
        if name in NAMED:
            code = NAMED[name]
        else:
            code, shift = CHARS[name]
            mods |= shift
        hid.key(code, mods)
    elif cmd == "type":
        hid.type(rest)
    else:
        return f"err unknown command {cmd!r}"
    return "ok"


def serve_client(conn):
    with conn, conn.makefile("rw") as f:
        for line in f:
            if not line.strip():
                continue
            try:
                reply = run_command(line)
            except Exception as e:
                reply = f"err {e}"
            f.write(reply + "\n")
            f.flush()


def command_server():
    try:
        os.unlink(SOCK_PATH)
    except FileNotFoundError:
        pass
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.bind(SOCK_PATH)
    os.chmod(SOCK_PATH, 0o660)
    gid = int(os.environ.get("HID_GID", "0"))
    os.chown(SOCK_PATH, 0, gid)
    s.listen(4)
    log("commands on", SOCK_PATH)
    while True:
        conn, _ = s.accept()
        threading.Thread(target=serve_client, args=(conn,), daemon=True).start()


def main():
    setup_dbus()
    threading.Thread(target=accept_loop, daemon=True).start()
    threading.Thread(target=command_server, daemon=True).start()
    threading.Thread(target=tune_loop, daemon=True).start()
    threading.Thread(target=restore_state, daemon=True).start()
    threading.Thread(target=reconnect_loop, daemon=True).start()
    reconnect_now.set()
    GLib.MainLoop().run()


if __name__ == "__main__":
    main()
