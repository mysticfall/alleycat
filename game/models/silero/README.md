# Silero VAD Model

This directory holds the Silero voice-activity-detection (VAD) ONNX model used by the automatic player voice input
feature (SPCH-008). The model is a committed repository asset stored through Git LFS: there is no user download or
configuration step, and the model path is fixed in code at `res://models/silero/silero_vad.onnx`.

## Provenance

| Field | Value |
| --- | --- |
| Upstream project | `snakers4/silero-vad` (https://github.com/snakers4/silero-vad) |
| Release tag | `v6.2.1` |
| Tag commit | `7e30209a3e901f9842f81b225f3e93d8199902b1` |
| Upstream path | `src/silero_vad/data/silero_vad.onnx` |
| Upstream URL | https://github.com/snakers4/silero-vad/blob/7e30209a3e901f9842f81b225f3e93d8199902b1/src/silero_vad/data/silero_vad.onnx |
| File size | 2,327,524 bytes |
| SHA-256 | `1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3` |
| Licence | MIT — Silero Team (see `LICENSE` in this directory) |

Unlike the downloaded speech-generation and lip-sync models (for example `supertonic-3` and `wav2arkit_cpu`), this
model is deliberately committed to the repository: it is small, required for the default voice-input experience, and
pinning its checksum keeps local detection reproducible and fully offline.

## Runtime Contract

The model is executed locally on CPU through the `Microsoft.ML.OnnxRuntime` dependency (explicit CPU execution
provider; no CUDA requirement). The game drives it with the following v6 framing and state contract:

- Sample rate: 16,000 Hz mono.
- Frame size: exactly 512 samples per inference.
- `input` tensor: `[1, 576]` float — the 64 most recent input samples (retained context) followed by the current
  512-sample frame.
- `state` tensor: `[2, 1, 128]` float — the model's recurrent state; the `stateN` output of frame N must be fed back
  as the `state` input of frame N + 1.
- `sr` input: scalar `16000` (supplied as a one-element `int64` tensor).
- `output` tensor: `[1, 1]` float — speech probability for the current frame.

A detector reset (for example after an abandoned utterance) zeroes both the recurrent state and the retained context.
