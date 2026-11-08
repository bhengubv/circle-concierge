"""Hardware-free contract checks for the cross-platform host path."""
import os
import json
import subprocess
import sys
import unittest
import shutil
from pathlib import Path
from unittest.mock import patch
from types import SimpleNamespace

from phoneuse import config
from phoneuse import ipc
from phoneuse.bridge_hid import BridgeHid, absolute
from phoneuse.web_origin import OriginPolicy
from phoneuse import vision_setup
from phoneuse import unlock
from phoneuse import desktop_approval
from phoneuse import host_mode
from phoneuse import bridge
try:
    from phoneuse import bridge_web
except ModuleNotFoundError as exc:
    if exc.name != "aiohttp":
        raise
    bridge_web = None


class FakeLink:
    def __init__(self, phone="Alice's iPhone"):
        self.phone = phone
        self.reports = []
        self.connects = 0
        self.async_reports = []

    def status(self):
        return {"id": "test-bridge", "connected": True, "phone": self.phone}

    def connect_phone(self):
        self.connects += 1

    def report(self, report_id, payload, skippable=False):
        self.reports.append((report_id, list(payload), skippable))
        return "sent"

    def send_async(self, report_id, payload):
        self.async_reports.append((report_id, list(payload)))
        return True


class PortableHostTest(unittest.TestCase):
    def setUp(self):
        self.link = FakeLink()
        self.hid = BridgeHid(self.link)
        self.phone_patch = patch("phoneuse.bridge_hid.phones.current", return_value=None)
        self.phone_patch.start()
        self.addCleanup(self.phone_patch.stop)

    def test_absolute_coordinates_are_clamped(self):
        self.assertEqual(absolute(-1, 2, 9), [1, 0, 0, 255, 127])

    def test_absolute_release_clears_button_state_before_later_motion(self):
        self.hid.cmd("a 0.5 0.5 1")
        self.assertEqual(self.hid.buttons, 1)
        self.hid.cmd("a 0.5 0.5 0")
        self.assertEqual(self.hid.buttons, 0)
        self.assertEqual(self.link.async_reports[-1], (3, absolute(0.5, 0.5, 0)))
        self.hid.cmd("m 3 4")
        self.assertEqual(self.link.reports[-1], (2, [0, 3, 4, 0], False))

    def test_reports_match_bridge_hid_protocol(self):
        self.assertEqual(self.hid.cmd("a 0.5 1 1"), "ok")
        self.assertEqual(self.link.reports[-1], (3, absolute(0.5, 1, 1), False))
        self.hid.cmd("m -2 3 0 1")
        self.assertEqual(self.link.reports[-1], (2, [1, 254, 3, 0], False))
        self.hid.cmd("k 8 4")
        self.assertEqual(self.link.reports[-1], (1, [8, 0, 4, 0, 0, 0, 0, 0], False))

    def test_text_and_consumer_release(self):
        with patch("phoneuse.bridge_hid.time.sleep"):
            self.hid.cmd("type A!")
            self.hid.cmd("eject")
        self.assertEqual(self.link.reports[0], (1, [2, 0, 4, 0, 0, 0, 0, 0], False))
        self.assertEqual(self.link.reports[-2:], [(4, [1], False), (4, [0], False)])

    def test_different_mirrored_phone_is_rejected(self):
        with patch("phoneuse.bridge_hid.phones.current", return_value="Bob's iPhone"):
            with self.assertRaisesRegex(RuntimeError, "bridge controls"):
                self.hid.cmd("a 0.5 0.5 1")
        self.assertEqual(self.link.reports, [])

    def test_processes_share_runtime_path(self):
        code = "from phoneuse.config import RUNTIME_DIR; print(RUNTIME_DIR)"
        value = subprocess.check_output([sys.executable, "-c", code], text=True).strip()
        self.assertEqual(value, config.RUNTIME_DIR)

    def test_vision_worker_uses_per_user_environment_or_explicit_override(self):
        with patch.dict(os.environ, {"PHONEUSE_VISION_PYTHON": "worker-python"}):
            self.assertEqual(vision_setup.python_path(), "worker-python")
        with patch.dict(os.environ, {}, clear=True):
            self.assertTrue(vision_setup.python_path().startswith(vision_setup.environment_dir()))

    def test_portable_passcode_uses_credential_store(self):
        class FakeKeyring:
            class errors:
                class PasswordDeleteError(Exception):
                    pass

            def __init__(self):
                self.values = {}

            def set_password(self, service, account, password):
                self.values[(service, account)] = password

            def get_password(self, service, account):
                return self.values.get((service, account))

            def delete_password(self, service, account):
                self.values.pop((service, account), None)

        fake = FakeKeyring()
        with patch.object(unlock.sys, "platform", "win32"), patch.object(unlock, "_keyring", return_value=fake):
            unlock.save("123456")
            self.assertTrue(unlock.saved())
            self.assertEqual(unlock._read(), "123456")
            unlock.forget()
            self.assertFalse(unlock.saved())

    def test_unlock_prompt_commands_are_available_on_desktop_oses(self):
        windows = desktop_approval.command(8777, "win32")
        mac = desktop_approval.command(8777, "darwin")
        linux = desktop_approval.command(8777, "linux")
        self.assertEqual(windows[0], "powershell.exe")
        self.assertIn("Button2", windows[-1])  # Deny is the default.
        self.assertEqual(mac[0], "osascript")
        self.assertIn('default button "Deny"', mac[-1])
        self.assertEqual(linux[0], "notify-send")
        self.assertTrue(desktop_approval.approved("Yes"))
        self.assertTrue(desktop_approval.approved("button returned:Unlock"))
        self.assertFalse(desktop_approval.approved("Deny"))

    @unittest.skipUnless(bridge_web, "aiohttp is not installed")
    def test_bridge_settings_close_temporary_link(self):
        class TemporaryLink:
            closed = False

            def __enter__(self):
                return self

            def __exit__(self, *_exc):
                self.closed = True

            def status(self):
                return {"connected": True}

        link = TemporaryLink()
        with patch("phoneuse.bridge_web.bridge.Link", return_value=link):
            self.assertEqual(bridge_web._bridge_call({"id": "test"}, "status"), {"connected": True})
        self.assertTrue(link.closed)

    def test_linux_browser_host_does_not_require_hyprland(self):
        with patch.object(host_mode.sys, "platform", "linux"), patch.dict(
            os.environ, {"PHONEUSE_HOST_MODE": "portable"}
        ):
            self.assertFalse(host_mode.native_linux())
            from phoneuse.hidclient import Hid
            with patch("phoneuse.bridge_hid.BridgeHid", return_value=self.hid):
                self.assertEqual(Hid().cmd("status"), "ok connected=True peer=\"Alice's iPhone\" bridge='test-bridge'")

    def test_esp_idf_arguments_do_not_pass_through_shell_text(self):
        with patch.object(bridge.sys, "platform", "darwin"), patch.object(
            bridge, "IDF", "/path with spaces/esp-idf"
        ), patch("phoneuse.bridge.os.path.exists", return_value=True), patch(
            "phoneuse.bridge.subprocess.call", return_value=0
        ) as call:
            self.assertEqual(bridge.idf("build", port="/dev/cu.usb serial"), 0)
        arguments = call.call_args.args[0]
        self.assertEqual(arguments[4:], ["/path with spaces/esp-idf", "-p", "/dev/cu.usb serial", "-b", "921600", "build"])

    def test_runtime_is_not_global_temp_on_non_linux(self):
        if sys.platform != "linux":
            self.assertTrue(os.path.basename(config.RUNTIME_DIR).startswith("phoneuse-"))

    def test_local_ipc_round_trip(self):
        import threading
        name = os.path.join(config.RUNTIME_DIR, "phoneuse-test-ipc.sock")
        listener = ipc.listen(name)
        try:
            result = []
            def server():
                conn, _ = listener.accept()
                with conn:
                    result.append(conn.recv(8))
                    conn.sendall(b"ok")
            thread = threading.Thread(target=server)
            thread.start()
            with ipc.connect(name, timeout=2) as client:
                client.sendall(b"hello")
                self.assertEqual(client.recv(2), b"ok")
            thread.join(2)
            self.assertEqual(result, [b"hello"])
        finally:
            listener.close()
            if sys.platform != "win32":
                os.unlink(name)

    @unittest.skipUnless(sys.platform == "win32", "Windows loopback authentication")
    def test_windows_ipc_rejects_wrong_secret(self):
        import socket
        import threading
        name = os.path.join(config.RUNTIME_DIR, "phoneuse-test-auth.sock")
        listener = ipc.listen(name)
        result = []
        try:
            def server():
                conn, _ = listener.accept()
                with conn:
                    result.append(conn.recv(2))
            thread = threading.Thread(target=server)
            thread.start()
            with socket.create_connection(("127.0.0.1", ipc._port(name)), timeout=2) as intruder:
                intruder.sendall(b"wrong\n")
            with ipc.connect(name, timeout=2) as client:
                client.sendall(b"ok")
            thread.join(2)
            self.assertEqual(result, [b"ok"])
        finally:
            listener.close()

    @unittest.skipUnless(shutil.which("node"), "Node is not installed")
    def test_browser_script_parses(self):
        page = (Path(__file__).resolve().parents[1] / "phoneuse" / "web" / "index.html").read_text()
        script = page.split("<script>", 1)[1].split("</script>", 1)[0]
        result = subprocess.run(["node", "--check", "-"], input=script, text=True, capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_browser_manifest_is_valid(self):
        manifest = Path(__file__).resolve().parents[1] / "phoneuse" / "web" / "manifest.webmanifest"
        data = json.loads(manifest.read_text())
        self.assertEqual(data["start_url"], "/")
        self.assertTrue(data["icons"])

    def test_remote_origin_and_local_cli_policy(self):
        policy = OriginPolicy(8777, "https://phoneuse.example")
        request = lambda origin, remote: SimpleNamespace(headers={"Origin": origin} if origin else {}, remote=remote)
        self.assertTrue(policy.same_origin(request("https://phoneuse.example", "127.0.0.1")))
        self.assertFalse(policy.same_origin(request("https://evil.example", "127.0.0.1")))
        self.assertTrue(policy.local_command(request(None, "127.0.0.1")))
        self.assertFalse(policy.local_command(request(None, "192.0.2.1")))
        self.assertFalse(policy.local_command(request("https://evil.example", "127.0.0.1")))
        with self.assertRaises(ValueError):
            OriginPolicy(8777, "http://phoneuse.example")
        with self.assertRaises(ValueError):
            OriginPolicy(8777, "https://phoneuse.example/other")


if __name__ == "__main__":
    unittest.main()
