# Docker Services

## Purpose

This directory hosts the local Docker services needed to run the AlleyCat platform: speech-to-text (WhisperLive,
served REST-only) and audio-to-face lip-sync (Audio2Face). Voice detection runs locally in the game, so this stack
only transcribes finished utterances. Run all commands below from this `docker/` directory.

## Prerequisites

- Git is installed.
- Docker and Docker Compose are installed and running.
- An NVIDIA GPU with the NVIDIA Container Toolkit is configured — both services reserve all available GPUs.

## Setup

Clone the Audio2Face server repository into the current directory (it is git-ignored, so each machine clones it
once):

```sh
git clone https://github.com/mysticfall/audio2face-api-server.git
```

Start the services in the background:

```sh
docker compose up -d
```

The compose project name is pinned to `alleycat` in `docker-compose.yml`, so all commands target the same
project regardless of the directory they run from. To manage the stack from the repository root instead, pass the
file explicitly:

```sh
docker compose -p alleycat -f docker/docker-compose.yml up -d
```

## Services

| Service          | Image | Port | Volume |
|------------------| --- | --- | --- |
| `whisper-api`    | `hwdsl2/whisper-live-server:cuda@sha256:5c92fd5e3305d98d3ade705aaf57664daf1fa8c3d97b5a31797a1f4edac05538` | `8000` (REST) | `whisper-live-data` → `/var/lib/whisper-live` (model cache) |
| `audio2face-api` | `audio2face-api` (locally built) | `8765` | `audio2face-data` → `/app/models` |

### WhisperLive (`alleycat-whisper-api`)

Speech-to-text backend for the automatic voice-detection feature. The game sends one whole-utterance transcription
request per utterance to the OpenAI-compatible REST endpoint `http://localhost:8000/v1/audio/transcriptions`; there
is no streaming session. The game's `OpenAITranscriber` reads all backend settings from the `STT` section of
`game/AlleyCat.yaml` — no backend settings are exported on the transcriber node.

Environment variables:

| Variable | Value | Purpose |
| --- | --- | --- |
| `WHISPERLIVE_MODEL` | `small` | Multilingual recognition model; the server-side default applies only to requests that omit a model. |
| `WHISPERLIVE_LANGUAGE` | `auto` | Default recognition language; applies only to requests that omit `language`. Gameplay requests always send the explicit language resolved from the selected content pack, so this default is a fallback for non-game clients. |
| `WHISPERLIVE_API_KEY` | *(empty)* | Local unauthenticated use; empty disables API-key enforcement. |
| `WHISPERLIVE_DISABLE_USAGE_COUNTS` | `1` | Disables anonymous usage counting. |

See the [image documentation](https://github.com/hwdsl2/docker-whisper-live) for the full environment-variable
reference.

### One-Time Multilingual Migration

The compose defaults ship the multilingual `small` model with per-request language selection, so an English or
Korean content pack can be selected at each game launch with **no Docker configuration change, container restart,
or container recreation** after this one-time migration:

1. Apply the environment-variable change above to your `docker/docker-compose.yml` (this repository already ships
   it; only deployments created from an older revision need the edit).
2. Recreate the WhisperLive service once: `docker compose up -d whisper-api`. The first multilingual start
   downloads the `small` model into the persisted `whisper-data` volume cache, which can take a few minutes.
3. From then on, switch spoken language by selecting a different content pack at game launch. The game sends the
   pack-resolved language and model on every transcription request, and later pack selections require no container
   changes.

See [Content Packs](../docs/content-packs.md) for how each pack declares its spoken language.
