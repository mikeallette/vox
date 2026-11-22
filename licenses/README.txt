License texts for the third-party parts that ship with Vox
==========================================================

Vox itself: MIT, see LICENSE.txt next to Vox.exe (LICENSE in the source repository).
The full list of components, versions and download sources is in THIRD-PARTY-NOTICES.md.

File                              Covers
--------------------------------  ----------------------------------------------------------------
whisper.cpp-LICENSE.txt           whisper.cpp / ggml / parakeet.dll native engine (MIT, The ggml authors)
Whisper.net-LICENSE.txt           Whisper.net and Whisper.net.Runtime (MIT)
NAudio-LICENSE.txt                NAudio audio capture library (MIT)
onnxruntime-LICENSE.txt           ONNX Runtime, shipped inside sherpa-onnx (MIT, Microsoft Corporation)
Apache-2.0.txt                    sherpa-onnx (k2-fsa), NVIDIA NeMo TitaNet-Small speaker model,
                                  and the optional Qwen3-ASR model (Apache License 2.0)
CC-BY-4.0.txt                     NVIDIA Parakeet TDT 0.6B v3 speech model (attribution notice)
dotnet-runtime-LICENSE.txt        .NET runtime bundled with the app (MIT, .NET Foundation)
dotnet-ThirdPartyNotices.txt      Third-party notices of the .NET runtime

Also shipped next to the parts they cover:
  asr\licenses\                   transcribe.cpp, ggml, miniz and Silero VAD (MIT)
  models\speakers\pyannote-segmentation-LICENSE.txt   pyannote segmentation 3.0 (MIT)

The Microsoft Visual C++ runtime DLLs in engine\ are redistributed under the Microsoft Visual C++
Redistributable terms.
