"""Origin rules for the local browser and an authenticated HTTPS proxy."""
from urllib.parse import urlsplit


class OriginPolicy:
    def __init__(self, port, public_origin=""):
        self.origins = {f"http://localhost:{port}", f"http://127.0.0.1:{port}"}
        if public_origin:
            parsed = urlsplit(public_origin)
            if (parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password
                    or parsed.path not in ("", "/") or parsed.query or parsed.fragment):
                raise ValueError("public_origin must be a single HTTPS origin")
            self.origins.add(public_origin.rstrip("/"))

    def same_origin(self, req):
        return req.headers.get("Origin") in self.origins

    def local_command(self, req):
        return self.same_origin(req) or (not req.headers.get("Origin") and req.remote in ("127.0.0.1", "::1"))
