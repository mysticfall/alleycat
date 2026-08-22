# Supertonic 3 - On-Device Multilingual TTS (ONNX)

ONNX models for Supertonic 3: a lightweight, on-device text-to-speech system supporting 31 languages, optimised for CPU inference.

## Download Instructions

Download the model files from the HuggingFace repository:

<https://huggingface.co/Supertone/supertonic-3>

The following files should be present in this directory after download:

### ONNX Model Files (`onnx/` directory)

| File | Description |
| --- | --- |
| `duration_predictor.onnx` | Duration prediction model |
| `text_encoder.onnx` | Text encoder model |
| `vector_estimator.onnx` | Vector estimator model |
| `vocoder.onnx` | Vocoder model |
| `tts.json` | TTS configuration |
| `unicode_indexer.json` | Unicode indexer mapping |

### Voice Style Files (`voice_styles/` directory)

| File | Description |
| --- | --- |
| `M1.json` - `M5.json` | Male voice style embeddings |
| `F1.json` - `F5.json` | Female voice style embeddings |

### Root Configuration

| File | Description |
| --- | --- |
| `config.json` | Model metadata (already included) |

> **Note:** `config.json` is already included in the repository and typically does not need to be downloaded separately.

### Quick Download Using the CLI

```bash
# Download ONNX model files
huggingface-cli download Supertone/supertonic-3 \
  onnx/duration_predictor.onnx \
  onnx/text_encoder.onnx \
  onnx/vector_estimator.onnx \
  onnx/vocoder.onnx \
  onnx/tts.json \
  onnx/unicode_indexer.json \
  --local-dir .

# Download voice style files
huggingface-cli download Supertone/supertonic-3 \
  voice_styles/M1.json \
  voice_styles/M2.json \
  voice_styles/M3.json \
  voice_styles/M4.json \
  voice_styles/M5.json \
  voice_styles/F1.json \
  voice_styles/F2.json \
  voice_styles/F3.json \
  voice_styles/F4.json \
  voice_styles/F5.json \
  --local-dir .
```

## Model Details

- **Format:** ONNX
- **Target:** CPU-optimised inference
- **Languages:** 31 (en, ko, ja, ar, bg, cs, da, de, el, es, et, fi, fr, hi, hr, hu, id, it, lt, lv, nl, pl, pt, ro, ru, sk, sl, sv, tr, uk, vi)
- **Input:** Text + voice style embedding
- **Output:** Audio waveform (16 kHz)
- **Parameters:** ~99M across ONNX assets