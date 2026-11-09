# kev-4B decision server — llama.cpp with the native /v1/systemone endpoint.
#
# Uses the exact official prebuilt release validated by the local feasibility probe
# (game/temp/local-kev4b): llama-server 0.9.0-dev build 12110, CUDA 13.4, tarball
# sha256-pinned below. A plain ubuntu:24.04 base plus the bundled CUDA userspace
# libs is deliberate: the NVIDIA container runtime injects the driver libs from the
# host, so no CUDA base image is needed and nothing conflicts with the bundled
# userspace. The GGUF weights are NOT baked in — they download once into the
# kev-decision-data volume (first container start) and are sha256-verified by the
# entrypoint.
FROM ubuntu:24.04

ARG LLAMA_TARBALL_URL=https://github.com/ggml-org/llama.cpp/releases/download/b12110/llama-b12110-bin-ubuntu-cuda-13.4-x64.tar.gz
ARG LLAMA_TARBALL_SHA256=2afd2ca540da119570b1dac36b5a2c862bb03a3b789c46661dfa55e600f5b19b
ARG CUDART_TARBALL_URL=https://github.com/ggml-org/llama.cpp/releases/download/b12110/cudart-llama-b12110-bin-ubuntu-cuda-13.4-x64.tar.gz
ARG CUDART_TARBALL_SHA256=9b1110161943882ec85c8e9ae641075ee8c962a50f8f1e36534ee65ea1e56ed9

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        libbrotli1 \
        libgomp1 \
        libssl3t64 \
        zlib1g \
        libzstd1 \
    && rm -rf /var/lib/apt/lists/*

# Fetch both pinned release tarballs, verify each sha256, and unpack to fixed paths.
RUN set -eux; \
    curl -fsSL -o /tmp/llama.tar.gz "$LLAMA_TARBALL_URL"; \
    echo "$LLAMA_TARBALL_SHA256  /tmp/llama.tar.gz" | sha256sum -c -; \
    mkdir -p /opt/llama; \
    tar -xzf /tmp/llama.tar.gz -C /opt/llama; \
    curl -fsSL -o /tmp/cudart.tar.gz "$CUDART_TARBALL_URL"; \
    echo "$CUDART_TARBALL_SHA256  /tmp/cudart.tar.gz" | sha256sum -c -; \
    mkdir -p /opt/cudart; \
    tar -xzf /tmp/cudart.tar.gz -C /opt/cudart; \
    rm /tmp/llama.tar.gz /tmp/cudart.tar.gz; \
    /opt/llama/llama-b12110/llama-server --version

ENV LD_LIBRARY_PATH=/opt/cudart/cudart-llama-b12110-bin-ubuntu-cuda-13.4-x64 \
    MODEL_DIR=/models \
    MODEL_PATH=/models/Kev-4B-Q4_K_M.gguf \
    MODEL_URL=https://huggingface.co/ggml-org/Kev-4B-GGUF/resolve/main/Kev-4B-Q4_K_M.gguf \
    MODEL_SHA256=33ae6b18926502b2209a1bf7d3b61a350d65938441515c686bb87d226a19eff9

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
