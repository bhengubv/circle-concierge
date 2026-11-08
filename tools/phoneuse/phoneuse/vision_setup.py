"""Cross-platform setup for the optional screen-reading models.

The vision worker has its own Python environment because GoClick-Large requires
a particular transformers version. Keep it outside the source checkout so a
wheel installation and a source checkout use the same worker.
"""
import os
import shutil
import subprocess
import sys

from .config import user_dir


def environment_dir():
    return os.path.join(user_dir(), "vision-venv")


def python_path(root=None):
    override = os.environ.get("PHONEUSE_VISION_PYTHON")
    if override:
        return override
    name = "python.exe" if sys.platform == "win32" else "python"
    folder = "Scripts" if sys.platform == "win32" else "bin"
    installed = os.path.join(environment_dir(), folder, name)
    if os.path.exists(installed):
        return installed
    # Existing Linux source checkouts installed with scripts/setup-vision.sh.
    if root:
        legacy = os.path.join(root, ".venv-vision", folder, name)
        if os.path.exists(legacy):
            return legacy
    return installed


def setup():
    directory = environment_dir()
    os.makedirs(os.path.dirname(directory), exist_ok=True)
    subprocess.run([sys.executable, "-m", "venv", directory], check=True)
    python = python_path()
    runtime = "onnxruntime-gpu" if shutil.which("nvidia-smi") else "onnxruntime"
    packages = ["torch", "transformers==4.45.0", "timm", "einops",
                "huggingface_hub", "rapidocr", runtime, "pillow", "numpy"]
    subprocess.run([python, "-m", "pip", "install", "--no-cache-dir", *packages], check=True)
    subprocess.run([python, "-c", "from huggingface_hub import snapshot_download; "
                    "snapshot_download('HongxinLi/GoClick-Large', "
                    "allow_patterns=['*.json', '*.py', 'model.safetensors'])"], check=True)
    print(f"Vision models ready in {directory}")


if __name__ == "__main__":
    setup()
