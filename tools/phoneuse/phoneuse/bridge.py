"""The Bluetooth bridge: an ESP32 (LilyGO T-Display) that the iPhone pairs with as
its Bluetooth keyboard and mouse, driven by phoneuse over Wi-Fi.

Three parts, in the order a new bridge meets them:

  1. Firmware over USB (developers): build bridge/ with ESP-IDF and flash it.
  2. Setup over BLE (everyone): find the bridge, exchange keys, compare the code on
     its screen, list the Wi-Fi networks it sees, send the chosen one's password
     encrypted. The bridge joins and phoneuse saves its id, address and key.
  3. The control link over UDP: signed HID reports, answered one by one
     (bridge/main/link.c has the packet format).

Saved in ~/.config/phoneuse/bridge.json: {"id", "ip", "key" (hex), "ssid"}.
"""
import asyncio
import hashlib
import hmac
import json
import os
import shutil
import socket
import struct
import subprocess
import sys
import threading
import time

from .config import config_path

HERE = os.path.dirname(os.path.abspath(__file__))
FIRMWARE = os.path.join(os.path.dirname(HERE), "bridge")
SAVED = os.path.join(os.path.dirname(config_path()), "bridge.json")
PORT = 47700
IDF = os.environ.get("IDF_PATH") or os.path.expanduser("~/esp/esp-idf-v6.1")


def _uuid(n):
    return f"c0de00{n:02x}-7068-6f6e-6575-736562726467"


SVC, INFO, KEX, SCAN, JOIN = (_uuid(n) for n in (1, 2, 3, 4, 5))


# ---------- saved bridge ----------

def saved():
    try:
        with open(SAVED) as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def save(d):
    os.makedirs(os.path.dirname(SAVED), exist_ok=True)
    tmp = SAVED + ".tmp"
    with open(os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "w") as f:
        json.dump(d, f, indent=1)
    os.replace(tmp, SAVED)


def resolve(cfg=None):
    """The saved bridge with a current address: asks the network for its id when
    the address is missing or doesn't answer (DHCP may have moved it). Saves it."""
    cfg = cfg or saved()
    if not cfg:
        return None
    if cfg.get("ip"):
        link = None
        try:
            link = Link(cfg)
            link.hello()
            return cfg
        except Exception:
            pass
        finally:
            if link:
                link.close()
    hits = [b for b in find_lan() if b["id"] == cfg["id"]]
    if hits:
        cfg["ip"] = hits[0]["ip"]
        save(cfg)
        return cfg
    return None


def forget():
    try:
        os.remove(SAVED)
    except OSError:
        pass


# ---------- 1. firmware over USB ----------

def usb_port():
    """The bridge's USB serial port, if one is plugged in (CH9102 / CP210x / CH340)."""
    import serial.tools.list_ports
    for p in serial.tools.list_ports.comports():
        if (p.vid, p.pid) in ((0x1A86, 0x55D4), (0x10C4, 0xEA60), (0x1A86, 0x7523)):
            return p.device
    return None


def idf(*args, port=None):
    """Run idf.py in bridge/ with ESP-IDF's environment."""
    if not os.path.isdir(FIRMWARE):
        raise SystemExit("Bridge firmware is not in this installation. Run bridge flash from a source checkout")
    if sys.platform == "win32":
        executable = shutil.which("idf.py")
        if not executable:
            raise SystemExit("Open an ESP-IDF PowerShell with idf.py on PATH, then retry bridge flash")
        return subprocess.call([executable, *(["-p", port, "-b", "921600"] if port else []), *args], cwd=FIRMWARE)
    if not os.path.exists(os.path.join(IDF, "export.sh")):
        raise SystemExit(f"ESP-IDF not found at {IDF} (set IDF_PATH)")
    arguments = ["-p", port, "-b", "921600"] if port else []
    # The ESP-IDF path and serial port are positional arguments, not shell text;
    # both can contain spaces on macOS and Linux.
    return subprocess.call(
        ["bash", "-c", '. "$1/export.sh" >/dev/null 2>&1 && shift && idf.py "$@"',
         "phoneuse", IDF, *arguments, *args], cwd=FIRMWARE)


def flash(port=None):
    port = port or usb_port()
    if not port:
        raise SystemExit("No bridge on USB. Plug the T-Display into this PC (data cable, not charge-only).")
    print(f"building and flashing to {port}...", flush=True)
    rc = idf("build", "flash", port=port)
    if rc:
        raise SystemExit(f"flash failed ({rc})")
    print("flashed; the bridge restarted")


def log(port=None, seconds=None):
    """Print the bridge's serial log (Ctrl+C stops)."""
    import serial
    port = port or usb_port()
    if not port:
        raise SystemExit("No bridge on USB.")
    s = serial.Serial(port, 115200, timeout=0.2)
    s.dtr, s.rts = False, True  # restart it, so the log starts at boot
    time.sleep(0.1)
    s.rts = False
    end = time.time() + seconds if seconds else None
    try:
        while end is None or time.time() < end:
            b = s.read(4096)
            if b:
                sys.stdout.write(b.decode("utf-8", "replace"))
                sys.stdout.flush()
    except KeyboardInterrupt:
        pass


# ---------- 2. setup over BLE ----------

class Setup:
    """One setup conversation with a bridge over BLE. Methods are coroutines."""

    def __init__(self, address):
        from bleak import BleakClient
        self.client = BleakClient(address, timeout=20)
        self.code = None
        self.keys = None

    async def __aenter__(self):
        await self.client.connect()
        return self

    async def __aexit__(self, *exc):
        try:
            await self.client.disconnect()
        except Exception:
            pass

    async def info(self):
        return json.loads(bytes(await self.client.read_gatt_char(INFO)).decode() or "{}")

    async def exchange(self):
        """X25519 with the bridge: returns the 6-digit code it now shows."""
        from cryptography.hazmat.primitives import hashes, serialization
        from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
        from cryptography.hazmat.primitives.kdf.hkdf import HKDF
        ours = X25519PrivateKey.generate()
        pub = ours.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)
        theirs = bytes(await self.client.read_gatt_char(KEX))
        await self.client.write_gatt_char(KEX, pub, response=True)
        shared = ours.exchange(X25519PublicKey.from_public_bytes(theirs))

        def derive(info, n=32):
            return HKDF(hashes.SHA256(), n, theirs + pub, info.encode()).derive(shared)

        self.code = int.from_bytes(derive("phoneuse code", 4), "big") % 1000000
        self.keys = {"wifi": derive("phoneuse wifi"), "control": derive("phoneuse control")}
        return self.code

    async def networks(self):
        await self.client.write_gatt_char(SCAN, b"1", response=True)
        for _ in range(40):
            await asyncio.sleep(0.25)
            v = bytes(await self.client.read_gatt_char(SCAN)).decode()
            if v.startswith("["):
                return json.loads(v)
        return []

    async def join(self, ssid, password, wait=25):
        """Send the network; wait until the bridge is online (returns its info) or failed."""
        from cryptography.hazmat.primitives.ciphers.aead import AESGCM
        nonce = os.urandom(12)
        sealed = nonce + AESGCM(self.keys["wifi"]).encrypt(nonce, ssid.encode() + b"\0" + password.encode(), None)
        await self.client.write_gatt_char(JOIN, sealed, response=True)
        end = time.time() + wait
        info = {}
        while time.time() < end:
            await asyncio.sleep(0.5)
            try:
                info = await self.info()
            except Exception:
                # it stops BLE once online: the link closing means it joined; ask it on the network
                break
            if info.get("state") == "failed" or (info.get("state") == "online" and info.get("ip")):
                break
        return info


async def scan_ble(seconds=5.0):
    """Bridges advertising setup nearby: [(address, name, rssi)]. Another program
    can stop BlueZ's scan under us (BlueZ then refuses our stop): keep what came in."""
    from bleak import BleakScanner
    found = {}

    def seen(d, adv):
        if SVC in [u.lower() for u in adv.service_uuids] or (adv.local_name or "").startswith("PhoneUse Bridge"):
            found[d.address] = (d.address, adv.local_name or d.name, adv.rssi)

    scanner = BleakScanner(seen)
    await scanner.start()
    await asyncio.sleep(seconds)
    try:
        await scanner.stop()
    except Exception:
        pass
    return sorted(found.values(), key=lambda x: -x[2])


def _local_subnet():
    """This PC's address on its default-route network, as a /24 prefix."""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("192.0.2.1", 9))  # no packet is sent; picks the outgoing interface
        return s.getsockname()[0].rsplit(".", 1)[0]
    except OSError:
        return None
    finally:
        s.close()


def find_lan(timeout=1.0):
    """Bridges on the local network: [{"id","ip",...}]. Broadcast first; a host
    firewall drops answers to broadcasts (they match no outgoing packet), so then
    ask every address on this /24 directly."""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    s.settimeout(0.1)
    pkt = b"PU\x01\x10" + bytes(8)
    out = {}

    def listen(until):
        while time.time() < until:
            try:
                b, (ip, _) = s.recvfrom(512)
            except socket.timeout:
                continue
            if b[:4] == b"PU\x01\x90":
                try:
                    d = json.loads(b[12:])
                    d["ip"] = d.get("ip") or ip
                    out[d["id"]] = d
                except ValueError:
                    pass

    s.sendto(pkt, ("255.255.255.255", PORT))
    listen(time.time() + timeout / 2)
    net = _local_subnet()
    if not out and net:
        for i in range(1, 255):
            try:
                s.sendto(pkt, (f"{net}.{i}", PORT))
            except OSError:
                pass
        listen(time.time() + timeout)
    s.close()
    return list(out.values())


# ---------- 3. the control link ----------

class Link:
    """Signed UDP session with a set-up bridge.

    Two ways to send a report:
      report(): waits for the bridge's answer and resends if it got lost; for
          keys, presses and releases, which must arrive exactly once.
      send_async(): sends without waiting, at most CREDIT unanswered at a time;
          for hovers and motion. False means "not now" (the sender keeps the
          motion for its next report), so a slow Wi-Fi moment costs one report
          instead of stalling everything behind it.
    One thread reads every answer; callers never read the socket themselves.
    """

    TIMEOUT = 0.06   # first wait for an answer; doubles on each retry
    CREDIT = 5       # unanswered async reports allowed at once (measured: 6 -> 107/s)
    EXPIRE = 0.25    # an async report unanswered this long counts as lost

    def __init__(self, cfg=None):
        self.cfg = cfg or saved()
        if not self.cfg:
            raise RuntimeError("no bridge set up")
        if not self.cfg.get("ip"):
            raise RuntimeError("the bridge's address isn't known yet")
        self.key = bytes.fromhex(self.cfg["key"])
        self.addr = (self.cfg["ip"], PORT)
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.settimeout(0.5)
        self.lock = threading.Lock()   # seq, session and sends stay in order
        self.session = 0
        self.seq = 0
        self.waiting = 0               # reports the bridge still has for Bluetooth
        self.phone = True              # the last answer said a phone is connected
        self.rtt = []
        self.waiters = {}              # seq or ("hello", nonce) -> [event, answer body]
        self.inflight = {}             # async seq -> send time
        self.closed = False
        threading.Thread(target=self._rx, daemon=True).start()

    def close(self):
        self.closed = True
        self.sock.close()

    def __enter__(self):
        return self

    def __exit__(self, *_exc):
        self.close()

    def _pack(self, type_, seq, body):
        head = b"PU\x01" + bytes([type_]) + struct.pack("<II", self.session, seq)
        return head + body + hmac.new(self.key, head + body, hashlib.sha256).digest()[:8]

    def _ok(self, b):
        return len(b) >= 20 and hmac.compare_digest(
            b[-8:], hmac.new(self.key, b[:-8], hashlib.sha256).digest()[:8])

    def _rx(self):
        while not self.closed:
            try:
                b = self.sock.recv(1024)
            except socket.timeout:
                continue
            except OSError:
                return
            if not self._ok(b):
                continue
            type_, seq, body = b[3], struct.unpack_from("<I", b, 8)[0], b[12:-8]
            if type_ == 0x81:
                w = self.waiters.get(("hello", body[:8]))
                if w:
                    w[1] = struct.unpack_from("<I", b, 4)[0]
                    w[0].set()
                continue
            if type_ == 0x82 and len(body) >= 2:
                self.waiting, self.phone = body[1], body[0] != 2
            sent = self.inflight.pop(seq, None)
            if sent:
                self.rtt = (self.rtt + [time.time() - sent])[-200:]
            w = self.waiters.get(seq)
            if w and not w[0].is_set():
                w[1] = body
                w[0].set()

    def hello(self):
        nonce = os.urandom(8)
        w = self.waiters[("hello", nonce)] = [threading.Event(), None]
        try:
            for _ in range(5):
                with self.lock:
                    self.session, self.seq = 0, 0
                    self.sock.sendto(self._pack(0x01, 0, nonce), self.addr)
                if w[0].wait(0.3):
                    with self.lock:
                        self.session = w[1]
                        self.inflight.clear()
                    return True
            raise RuntimeError(f"the bridge at {self.addr[0]} didn't answer")
        finally:
            self.waiters.pop(("hello", nonce), None)

    def _call(self, type_, body=b"", tries=4, wait=None):
        if not self.session:
            self.hello()
        for attempt in range(2):
            with self.lock:
                self.seq += 1
                seq = self.seq
                pkt = self._pack(type_, seq, body)
            w = self.waiters[seq] = [threading.Event(), None]
            try:
                t0 = time.time()
                for i in range(tries):
                    self.sock.sendto(pkt, self.addr)  # the same seq: the bridge answers again, does nothing twice
                    if w[0].wait(wait or self.TIMEOUT * (2 ** i)):
                        self.rtt = (self.rtt + [time.time() - t0])[-200:]
                        return w[1]
            finally:
                self.waiters.pop(seq, None)
            if attempt == 0:
                self.hello()  # the bridge restarted (its session is gone): once more
        raise RuntimeError("the bridge didn't answer")

    def send_async(self, report_id, payload):
        """Send a hover or motion report without waiting. False: not now."""
        if not self.session:
            self.hello()
        now = time.time()
        with self.lock:
            for seq, t in list(self.inflight.items()):
                if now - t > self.EXPIRE:
                    del self.inflight[seq]
                    self.lost = getattr(self, "lost", 0) + 1
            if len(self.inflight) >= self.CREDIT or self.waiting >= self.CREDIT:
                return False
            self.seq += 1
            self.inflight[self.seq] = now
            self.sock.sendto(self._pack(0x02, self.seq, bytes([0, report_id]) + bytes(payload)), self.addr)
        if getattr(self, "lost", 0) >= 8:  # many lost in a row: maybe a new session is needed
            self.lost = 0
            threading.Thread(target=self.hello, daemon=True).start()
        return True

    def send_latest(self, report_id, payload):
        """A hover: sent without waiting and without an answer. The bridge keeps
        only the newest and sends it once Bluetooth is free."""
        if not self.session:
            self.hello()
        with self.lock:
            self.seq += 1
            self.sock.sendto(self._pack(0x02, self.seq, bytes([3, report_id]) + bytes(payload)), self.addr)

    def report(self, report_id, payload, skippable=False):
        r = self._call(0x02, bytes([1 if skippable else 0, report_id]) + bytes(payload))
        self.waiting, self.phone = r[1], r[0] != 2
        return ("sent", "skipped", "no phone")[min(r[0], 2)]

    def status(self):
        return json.loads(self._call(0x03).decode())

    def connect_phone(self):
        self._call(0x04)

    def open_setup(self):
        self._call(0x05)

    def networks(self):
        """Wi-Fi networks the bridge sees (it scans while staying online, ~2 s)."""
        return json.loads(self._call(0x06, tries=2, wait=6).decode() or "[]")

    def change_network(self, ssid, password):
        """Move the bridge to another network. It answers first, then switches, and
        goes back to the current network if the new one fails (status "err")."""
        from cryptography.hazmat.primitives.ciphers.aead import AESGCM
        k = hmac.new(self.key, b"phoneuse wifi change", hashlib.sha256).digest()
        nonce = os.urandom(12)
        sealed = nonce + AESGCM(k).encrypt(nonce, ssid.encode() + b"\0" + password.encode(), None)
        if self._call(0x07, sealed)[:1] != b"\x01":
            raise RuntimeError("the bridge couldn't read the network details")


# ---------- CLI ----------

def _ask(prompt):
    try:
        return input(prompt)
    except EOFError:
        raise SystemExit(1)


def setup_cli():
    """Interactive setup in the terminal (the browser mirror's settings do the same)."""
    async def run():
        print("Looking for bridges in setup mode (5 s)...")
        found = await scan_ble()
        if not found:
            raise SystemExit("None found. A new bridge starts in setup mode; a set-up one enters it when you "
                             "hold its lower button for 3 s.")
        for i, (addr, name, rssi) in enumerate(found):
            print(f"  {i + 1}. {name}  ({addr}, {rssi} dBm)")
        pick = found[int(_ask("Which one? [1] ") or 1) - 1]
        async with Setup(pick[0]) as s:
            info = await s.info()
            code = await s.exchange()
            print(f"\nThe bridge's screen should show {code // 1000:03d} {code % 1000:03d}.")
            if _ask("Same code? [y/N] ").strip().lower() != "y":
                raise SystemExit("Stopped: a different code means something else answered.")
            print("Wi-Fi networks the bridge sees:")
            nets = await s.networks()
            for i, n in enumerate(nets):
                print(f"  {i + 1}. {n['s']}  ({n['r']} dBm{', open' if not n['l'] else ''})")
            choice = _ask("Network number (or a name): ").strip()
            ssid = nets[int(choice) - 1]["s"] if choice.isdigit() else choice
            import getpass
            password = getpass.getpass(f"Password for {ssid}: ")
            print("Joining...")
            info2 = await s.join(ssid, password)
        if info2.get("state") == "failed":
            raise SystemExit(f"The bridge couldn't join {ssid}: {info2.get('err')}")
        bridge_id = info.get("id")
        ip = info2.get("ip")
        if not ip:
            for _ in range(10):
                hits = [b for b in find_lan() if b["id"] == bridge_id]
                if hits:
                    ip = hits[0]["ip"]
                    break
        if not ip:
            raise SystemExit("The bridge joined but isn't answering on this network (is this PC on the same one?)")
        save({"id": bridge_id, "ip": ip, "key": s.keys["control"].hex(), "ssid": ssid})
        st = Link().status()
        print(f"Set up: bridge {bridge_id} at {ip} on {ssid}. Phone: {st.get('phone') or 'not paired yet'}")
    asyncio.run(run())


def status_cli():
    cfg = saved()
    if not cfg:
        print("No bridge set up (phoneuse bridge setup).")
        return
    try:
        link = Link(cfg)
        st = link.status()
    except Exception as e:
        lan = [b for b in find_lan() if b["id"] == cfg["id"]]
        if lan and lan[0]["ip"] != cfg["ip"]:
            cfg["ip"] = lan[0]["ip"]
            save(cfg)
            st = Link(cfg).status()
        else:
            print(f"Bridge {cfg['id']} at {cfg['ip']}: no answer ({e})")
            return
    rtt = sorted(link.rtt)[len(link.rtt) // 2] * 1000 if link.rtt else 0
    print(f"Bridge {st['id']} (firmware {st['fw']}) at {cfg['ip']} on {st['ssid']}, Wi-Fi {st['rssi']} dBm, "
          f"round trip {rtt:.1f} ms")
    print(f"Phone: {st['phone'] + (' (connected)' if st['connected'] else ' (not connected)') if st['phone'] else 'not paired'}")
    if st.get("battery_mv"):
        print(f"Battery: {st['battery_mv'] / 1000:.2f} V")


def main(args):
    sub = args[0] if args else "status"
    if sub == "flash":
        return flash(args[1] if len(args) > 1 else None)
    if sub == "build":
        return idf("build")
    if sub == "log":
        return log(seconds=float(args[1]) if len(args) > 1 else None)
    if sub == "setup":
        return setup_cli()
    if sub == "status":
        return status_cli()
    if sub == "find":
        print("On the network:", find_lan() or "none")
        print("In setup mode nearby:", asyncio.run(scan_ble()) or "none")
        return
    if sub == "forget":
        forget()
        return print("forgot the bridge")
    raise SystemExit("usage: phoneuse bridge [status|setup|find|flash|build|log [SECONDS]|forget]")
