#!/bin/sh
# kev decision server entrypoint: fetch (once) and sha256-pin the kev-4B Q4_K_M
# weights into the mounted volume, then run llama-server with the VRAM-bounded
# configuration the local feasibility probe validated. The default llama-server
# context (~243k tokens) would allocate ~11.4 GiB VRAM on a 16 GiB card; the
# bounded --ctx-size 8192 --parallel 1 footprint is ~3.9 GiB with full offload.
set -eu

model_path="${MODEL_PATH:-/models/Kev-4B-Q4_K_M.gguf}"
model_url="${MODEL_URL:?MODEL_URL must be set}"
expected_sha="${MODEL_SHA256:?MODEL_SHA256 must be set}"

if [ ! -f "$model_path" ]; then
    echo "[entrypoint] first start: downloading $model_url -> $model_path"
    started=$(date +%s)
    curl -fL --retry 3 -o "$model_path.part" "$model_url"
    mv "$model_path.part" "$model_path"
    echo "[entrypoint] weights download finished in $(( $(date +%s) - started )) s"
fi

actual_sha=$(sha256sum "$model_path" | awk '{print $1}')
if [ "$actual_sha" != "$expected_sha" ]; then
    echo "[entrypoint] FATAL: sha256 mismatch for $model_path" >&2
    echo "[entrypoint] expected $expected_sha" >&2
    echo "[entrypoint] actual   $actual_sha" >&2
    exit 1
fi
echo "[entrypoint] weights sha256 verified ($actual_sha)"

exec /opt/llama/llama-b12110/llama-server \
    -m "$model_path" \
    --alias kev-4b-q4km \
    --ctx-size 8192 \
    --parallel 1 \
    -ngl 99 \
    --host 0.0.0.0 --port 8080
