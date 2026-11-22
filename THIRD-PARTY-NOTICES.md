# Third-party notices

Vox's own code is MIT licensed (see `LICENSE`). Vox also ships, or can load, the components below. Each keeps its own
license. Download sources and SHA-256 hashes are in the `SOURCES.txt` file next to each vendored part, and the full
license texts ship with the app in the `licenses` folder (see `licenses/README.txt`).

## Speech and audio engines (shipped)

| Component | Used for | License | Source |
|---|---|---|---|
| whisper.cpp v1.9.2 (ggml, parakeet.dll) | Parakeet speech engine | MIT | https://github.com/ggml-org/whisper.cpp |
| transcribe.cpp v0.4.1 | Accurate mode (Qwen3-ASR) | MIT | https://github.com/handy-computer/transcribe.cpp (licenses in `asr\licenses`) |
| Whisper.net 1.9.1 | Whisper models, speech detector | MIT | https://github.com/sandrohanea/whisper.net |
| sherpa-onnx 1.13.8 | Meeting speaker labels | Apache-2.0 | https://github.com/k2-fsa/sherpa-onnx |
| ONNX Runtime 1.28 (via sherpa-onnx) | Meeting speaker labels | MIT | https://github.com/microsoft/onnxruntime |
| NAudio 2.2.1 | Microphone and meeting audio capture | MIT | https://github.com/naudio/NAudio |
| .NET 8 runtime (self-contained) | App runtime | MIT | https://github.com/dotnet/runtime |
| Newsreader typeface (Production Type) | App text | SIL OFL 1.1 | https://github.com/productiontype/Newsreader |
| IBM Plex Mono typeface (IBM) | App labels | SIL OFL 1.1 | https://github.com/IBM/plex |
| Microsoft Visual C++ runtime DLLs | Needed by the native engines | Microsoft Visual C++ Redistributable license | https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist |

## Models

| Model | Shipped | License | Source |
|---|---|---|---|
| NVIDIA Parakeet TDT 0.6B v3, ggml q8_0 conversion | In the installer | CC BY 4.0 (model); conversion repo MIT | https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3, https://huggingface.co/ggml-org/parakeet-GGUF |
| Silero VAD v5.1.2 (ggml) | In the installer | MIT | https://huggingface.co/ggml-org/whisper-vad |
| pyannote segmentation 3.0 (ONNX, by k2-fsa) | In the installer | MIT | https://huggingface.co/pyannote/segmentation-3.0 |
| NVIDIA NeMo TitaNet-Small (ONNX, by k2-fsa) | In the installer | Apache-2.0 (NeMo Toolkit license, per NVIDIA's model card) | https://catalog.ngc.nvidia.com/orgs/nvidia/teams/nemo/models/titanet_small |
| Qwen3-ASR 1.7B, Q4_K_M GGUF | Separate optional download | Apache-2.0 | https://huggingface.co/Qwen/Qwen3-ASR-1.7B, https://huggingface.co/handy-computer/qwen3-asr-1.7b-gguf |

**Attribution (CC BY 4.0):** Parakeet TDT 0.6B v3 was created by NVIDIA. Vox uses the ggml q8_0 quantization published
by ggml-org. No other changes were made to the model.


NVIDIA, Parakeet and NeMo are trademarks of NVIDIA Corporation. Qwen is a trademark of Alibaba Cloud. Windows, Teams,
Outlook and Word are trademarks of Microsoft. Zoom and Google Meet are trademarks of their owners. These names are used
only to describe compatibility and the models Vox uses; Vox is not affiliated with or endorsed by any of them.
