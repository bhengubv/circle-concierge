"""Vision service for `phone`: keeps OCR and a GUI grounding model loaded.

Runs in .venv-vision (its own deps; `python -m phoneuse.vision`). Listens on $XDG_RUNTIME_DIR/iphone-vision.sock;
one JSON request per connection, one JSON reply:
  {"op": "text", "image": PATH}            -> {"lines": [{"text", "x", "y", "w", "h", "conf"}]}
  {"op": "find", "image": PATH, "what": S} -> {"x", "y"}   (fractions of the image)

Models (chosen 2026-11-06 on real iPhone screenshots):
  OCR: RapidOCR with PP-OCRv6 small (ONNX; GPU with onnxruntime-gpu, else CPU).
       Exact on iOS UI text incl. arrows, ~0.18 s per screen on an RTX 5090
       (~1.2 s on the CPU); Tesseract garbled symbols.
  Grounding: GoClick-Large (Florence-2 based, ~0.8B, fp16 on GPU, CPU fallback).
       Finds an element from a plain description ("clear search (X) button"),
       matched OCR positions within ~1 px, ~50 ms per query once warm.
"""
import json
import os
import re
import socket
import sys
import threading
import time

from .config import VISION_SOCKET as SOCK
GROUNDER = "HongxinLi/GoClick-Large"


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, file=sys.stderr, flush=True)


class Vision:
    def __init__(self):
        from rapidocr import RapidOCR
        import logging
        import onnxruntime
        logging.getLogger("RapidOCR").setLevel(logging.WARNING)
        # No text-orientation classifier: iOS text is upright (saves ~75 ms). On an
        # NVIDIA GPU (onnxruntime-gpu) a screen takes ~0.18 s, on the CPU ~1.2 s.
        params = {"Global.use_cls": False, "Det.limit_side_len": 960,
                  "EngineConfig.onnxruntime.intra_op_num_threads": min(8, os.cpu_count() or 4)}
        if "CUDAExecutionProvider" in onnxruntime.get_available_providers():
            params["EngineConfig.onnxruntime.use_cuda"] = True
        self.ocr = RapidOCR(params=params)
        self.ocr_device = "cuda" if params.get("EngineConfig.onnxruntime.use_cuda") else "cpu"
        log("OCR on", self.ocr_device)
        self._g = None
        self.lock = threading.Lock()

    def grounder(self):
        if self._g is None:
            import torch
            from transformers import AutoModelForCausalLM, AutoProcessor
            dev = "cuda" if torch.cuda.is_available() else "cpu"
            dtype = torch.float16 if dev == "cuda" else torch.float32
            model = AutoModelForCausalLM.from_pretrained(GROUNDER, trust_remote_code=True, torch_dtype=dtype).to(dev).eval()
            proc = AutoProcessor.from_pretrained(GROUNDER, trust_remote_code=True)
            self._g = (model, proc, dev, dtype)
            log("grounder loaded on", dev)
        return self._g

    def text(self, image):
        from PIL import Image
        W, H = Image.open(image).size
        r = self.ocr(image)
        lines = []
        for box, txt, conf in zip(r.boxes if r.boxes is not None else [], r.txts or [], r.scores or []):
            xs = [float(p[0]) for p in box]
            ys = [float(p[1]) for p in box]
            lines.append({"text": txt, "x": round(sum(xs) / 4 / W, 4), "y": round(sum(ys) / 4 / H, 4),
                          "w": round((max(xs) - min(xs)) / W, 4), "h": round((max(ys) - min(ys)) / H, 4),
                          "conf": round(float(conf), 3)})
        return {"lines": lines}

    def find(self, image, what):
        import torch
        from PIL import Image
        model, proc, dev, dtype = self.grounder()
        img = Image.open(image).convert("RGB")
        prompt = f"Where is the {what} element? (Output the center coordinates of the target)"
        inp = proc(text=prompt, images=img, return_tensors="pt").to(dev, dtype)
        with torch.no_grad():
            out = model.generate(input_ids=inp["input_ids"], pixel_values=inp["pixel_values"],
                                 max_new_tokens=20, num_beams=1, do_sample=False)
        txt = proc.batch_decode(out, skip_special_tokens=False)[0]
        m = re.findall(r"<loc_(\d+)>,?\s*<loc_(\d+)>", txt) or re.findall(r"\((\d+),\s*(\d+)\)", txt)
        if not m:
            return {"error": "not found", "raw": txt}
        return {"x": int(m[0][0]) / 1000, "y": int(m[0][1]) / 1000}

    def handle(self, req):
        with self.lock:
            if req["op"] == "text":
                return self.text(req["image"])
            if req["op"] == "find":
                return self.find(req["image"], req["what"])
            if req["op"] == "ping":
                return {"ok": True}
            return {"error": "unknown op"}


def serve():
    v = Vision()
    threading.Thread(target=v.grounder, daemon=True).start()  # warm up in the background
    from . import ipc
    s = ipc.listen(SOCK, 8)
    log("vision service on", SOCK)
    while True:
        conn, _ = s.accept()
        threading.Thread(target=_client, args=(v, conn), daemon=True).start()


def _client(v, conn):  # requests: {"op": "text" | "find" | "ping", ...}
    with conn:
        try:
            req = json.loads(conn.makefile().readline())
            reply = v.handle(req)
        except Exception as e:
            reply = {"error": str(e)}
        try:
            data = json.dumps(reply)
        except TypeError as e:
            data = json.dumps({"error": f"bad reply: {e}"})
        conn.sendall((data + "\n").encode())


if __name__ == "__main__":
    serve()
