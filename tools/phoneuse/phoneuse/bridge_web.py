"""The browser mirror's routes for the Bluetooth bridge's setup (Settings > Bluetooth
bridge). The page walks through: find the bridge over BLE, compare the code on its
screen, pick a Wi-Fi network it sees, type that network's password, done. The BLE
conversation lives here between the page's requests."""
import asyncio
import time

from aiohttp import web

from . import bridge
from .hidclient import Hid
from .host_mode import native_linux


def _daemon(cmd):
    try:
        return Hid().cmd(cmd)
    except Exception as e:
        return f"err {e}"


def use_bridge(cfg):
    """Tell the HID daemon to send input through the bridge (or not)."""
    if not native_linux():
        # The portable HID client reads the saved enabled/address setting itself.
        return "ok bridge on" if cfg and cfg.get("use", True) else "ok bridge off"
    if cfg and cfg.get("use", True):
        return _daemon(f"bridge {cfg['ip']} {cfg['key']}")
    return _daemon("bridge off")


def _bridge_call(cfg, method, *args):
    """Use one bridge UDP link for a settings request, then stop its receiver."""
    with bridge.Link(cfg) as link:
        return getattr(link, method)(*args)


class BridgeRoutes:
    def __init__(self, same_origin):
        self.same_origin = same_origin
        self.session = None  # bridge.Setup while a setup runs
        self.bridge_id = None
        self.lock = asyncio.Lock()

    async def _close(self):
        if self.session:
            s, self.session = self.session, None
            await s.__aexit__(None, None, None)

    def _guard(self, req):
        if not self.same_origin(req):
            raise web.HTTPForbidden()

    async def state(self, req):
        cfg = bridge.saved()
        if req.method == "DELETE":
            self._guard(req)
            await asyncio.get_running_loop().run_in_executor(None, use_bridge, None)
            bridge.forget()
            return web.json_response({"saved": None})
        out = {"saved": None, "setup": bool(self.session)}
        if cfg:
            out["saved"] = {"id": cfg["id"], "ip": cfg.get("ip") or "", "ssid": cfg.get("ssid"), "use": cfg.get("use", True)}

            def ask():
                try:
                    c = bridge.resolve(cfg)
                    if not c:
                        return {"error": "not answering on this network"}
                    if c["ip"] != cfg.get("ip"):
                        use_bridge(c)  # it moved: tell the daemon the new address
                    return _bridge_call(c, "status")
                except Exception as e:
                    return {"error": str(e)}
            out["status"] = await asyncio.get_running_loop().run_in_executor(None, ask)
            out["daemon"] = await asyncio.get_running_loop().run_in_executor(None, _daemon, "status")
        return web.json_response(out)

    async def find(self, req):
        self._guard(req)
        async with self.lock:
            await self._close()
            try:
                found = await bridge.scan_ble(5)
            except Exception as e:
                return web.json_response({"error": f"Bluetooth on this PC: {e}"})
        return web.json_response({"bridges": [{"address": a, "name": n, "rssi": r} for a, n, r in found]})

    async def pair(self, req):
        self._guard(req)
        address = (await req.json())["address"]
        async with self.lock:
            await self._close()
            s = bridge.Setup(address)
            try:
                await s.__aenter__()
                info = await s.info()
                code = await s.exchange()
            except Exception as e:
                await s.__aexit__(None, None, None)
                return web.json_response({"error": f"couldn't talk to the bridge: {e}"})
            self.session, self.bridge_id = s, info.get("id")
        return web.json_response({"code": f"{code // 1000:03d} {code % 1000:03d}", "id": self.bridge_id})

    async def networks(self, req):
        self._guard(req)
        if not self.session:  # a set-up bridge: ask it over the network
            cfg = bridge.saved()
            if not cfg:
                return web.json_response({"error": "setup ended; start again"})
            try:
                nets = await asyncio.get_running_loop().run_in_executor(
                    None, lambda: _bridge_call(bridge.resolve(cfg) or cfg, "networks"))
            except Exception as e:
                return web.json_response({"error": f"the bridge didn't answer: {e}"})
            return web.json_response({"networks": nets})
        async with self.lock:
            try:
                nets = await self.session.networks()
            except Exception as e:
                await self._close()
                return web.json_response({"error": f"the bridge stopped answering: {e}"})
        return web.json_response({"networks": nets})

    async def join(self, req):
        self._guard(req)
        body = await req.json()
        ssid, password = body["ssid"], body.get("password", "")
        if not self.session:
            return await self.change_network(ssid, password)
        loop = asyncio.get_running_loop()
        async with self.lock:
            s = self.session
            # Saved before the answer: the bridge keeps this key once it is on the
            # network, so the PC must never lose it (the address can be found later).
            previous = bridge.saved()
            cfg = {"id": self.bridge_id, "ip": None, "key": s.keys["control"].hex(), "ssid": ssid, "use": True,
                   "set_up": time.strftime("%Y-%m-%d %H:%M")}
            bridge.save(cfg)
            try:
                info = await s.join(ssid, password)
            except Exception as e:
                info = {"err": str(e)}
            if info.get("state") == "failed":
                bridge.save(previous) if previous else bridge.forget()  # it kept its old key
                return web.json_response({"error": f"couldn't join {ssid}: {info.get('err')}", "retry": True})
            await self._close()
        cfg["ip"] = info.get("ip")
        bridge.save(cfg)
        found = None
        for _ in range(5):
            found = await loop.run_in_executor(None, bridge.resolve, cfg)
            if found:
                break
            await asyncio.sleep(1)
        if not found:
            return web.json_response({"error": "The bridge was set up but this PC can't reach it yet. Is this PC "
                                               "on the same network? phoneuse keeps looking for it."})
        cfg = found
        ip = cfg["ip"]
        answer = await loop.run_in_executor(None, use_bridge, cfg)
        return web.json_response({"ok": True, "ip": ip, "ssid": ssid, "daemon": answer})

    async def change_network(self, ssid, password):
        """A set-up bridge moves to another network over the control link; it goes
        back to the old one if the new one fails."""
        loop = asyncio.get_running_loop()
        cfg = bridge.resolve() or bridge.saved()
        if not cfg:
            return web.json_response({"error": "no bridge set up"})
        old = cfg.get("ssid")
        try:
            await loop.run_in_executor(None, lambda: _bridge_call(cfg, "change_network", ssid, password))
        except Exception as e:
            return web.json_response({"error": f"the bridge didn't answer: {e}"})
        await asyncio.sleep(4)  # it leaves the old network and tries the new one
        deadline = time.time() + 30
        while time.time() < deadline:
            found = await loop.run_in_executor(None, bridge.resolve, dict(cfg, ip=None))
            if found:
                try:
                    st = await loop.run_in_executor(None, lambda: _bridge_call(found, "status"))
                except Exception:
                    st = {}
                if st.get("online") and st.get("ssid") == ssid:
                    found["ssid"] = ssid
                    bridge.save(found)
                    await loop.run_in_executor(None, use_bridge, found)
                    return web.json_response({"ok": True, "ip": found["ip"], "ssid": ssid})
                if st.get("online") and st.get("err"):
                    await loop.run_in_executor(None, use_bridge, found)
                    return web.json_response({"error": f"{st['err']}; it's back on {old}", "retry": True})
            await asyncio.sleep(1.5)
        return web.json_response({"error": f"The bridge left {old} and this PC can't find it. If {ssid} is "
                                           "another network, this PC must be on it too."})

    async def cancel(self, req):
        self._guard(req)
        async with self.lock:
            await self._close()
        return web.json_response({"ok": True})

    async def use(self, req):
        self._guard(req)
        cfg = bridge.saved()
        if not cfg:
            return web.json_response({"error": "no bridge set up"})
        cfg["use"] = bool((await req.json()).get("on"))
        bridge.save(cfg)
        answer = await asyncio.get_running_loop().run_in_executor(None, use_bridge, cfg)
        return web.json_response({"use": cfg["use"], "daemon": answer})

    async def change(self, req):
        """Set the bridge up again over BLE (it keeps its network until a new one
        works). Changing networks doesn't need this: see change_network."""
        self._guard(req)
        cfg = bridge.saved()
        try:
            await asyncio.get_running_loop().run_in_executor(None, lambda: _bridge_call(cfg, "open_setup"))
        except Exception as e:
            return web.json_response({"error": f"the bridge didn't answer: {e}. Hold its lower button for 3 s."})
        return web.json_response({"ok": True})

    def add(self, app):
        r = app.router
        r.add_route("*", "/api/bridge", self.state)
        r.add_post("/api/bridge/find", self.find)
        r.add_post("/api/bridge/pair", self.pair)
        r.add_post("/api/bridge/networks", self.networks)
        r.add_post("/api/bridge/join", self.join)
        r.add_post("/api/bridge/cancel", self.cancel)
        r.add_post("/api/bridge/use", self.use)
        r.add_post("/api/bridge/change", self.change)
