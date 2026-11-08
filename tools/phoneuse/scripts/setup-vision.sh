#!/bin/sh
# Local screen-reading models for `phoneuse text` / `phoneuse find`: RapidOCR (PP-OCRv6)
# and GoClick-Large (GUI grounding). Separate venv: GoClick needs transformers 4.45.
set -e
cd "$(dirname "$0")/.."
uv venv -q --python 3.12 .venv-vision
# --no-cache: a corrupted uv cache once installed tqdm/packaging as empty dirs
# onnxruntime-gpu runs OCR on an NVIDIA GPU (~0.18 s a screen against ~1.5 s on
# the CPU); it falls back to the CPU by itself when there is no GPU.
ORT=onnxruntime
command -v nvidia-smi >/dev/null 2>&1 && ORT=onnxruntime-gpu
uv pip install -q --no-cache --python .venv-vision/bin/python torch "transformers==4.45.0" \
    timm einops huggingface_hub rapidocr "$ORT" pillow numpy
.venv-vision/bin/python -c "
from huggingface_hub import snapshot_download
snapshot_download('HongxinLi/GoClick-Large', allow_patterns=['*.json', '*.py', 'model.safetensors'])"
echo "vision models ready"
