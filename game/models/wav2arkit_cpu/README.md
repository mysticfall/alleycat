# Wav2ARKit - Audio to Facial Expression (ONNX)

ONNX model that converts audio waveforms into 52 ARKit blendshape coefficients at 30 fps, optimised for CPU inference.

## Download Instructions

Download the model files from the HuggingFace repository:

<https://huggingface.co/myned-ai/wav2arkit_cpu>

The following files should be present in this directory after download:

| File | Description |
| --- | --- |
| `wav2arkit_cpu.onnx` | ONNX model graph |
| `wav2arkit_cpu.onnx.data` | ONNX model weights |
| `config.json` | Model metadata |

> **Note:** `config.json` is already included in the repository and typically does not need to be downloaded separately.

### Quick Download Using the CLI

```bash
huggingface-cli download myned-ai/wav2arkit_cpu \
  wav2arkit_cpu.onnx \
  wav2arkit_cpu.onnx.data \
  --local-dir .
```

## Model Details

- **Format:** ONNX
- **Target:** CPU-optimised inference
- **Input:** 16 kHz mono audio waveform
- **Output:** 52 ARKit blendshape coefficients at 30 fps
