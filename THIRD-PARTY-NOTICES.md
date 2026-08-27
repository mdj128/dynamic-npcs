# Third-party notices

This package's own source is MIT licensed (see `LICENSE`). It downloads, drives, or
interoperates with the components below, none of which are bundled in this repository.
If you ship a game with the embedded stack, you are redistributing them and their terms apply.

| Component | Used for | License | Notes |
|---|---|---|---|
| [llama.cpp](https://github.com/ggml-org/llama.cpp) (`llama-server`) | Runs the dialogue LLM and the NeuTTS backbone | MIT | Downloaded into `StreamingAssets` by the setup window; shippable |
| [NeuTTS Air](https://huggingface.co/neuphonic/neutts-air) weights | Voice-cloned speech synthesis | Apache-2.0 | Developer-supplied GGUF; shippable |
| [NeuCodec](https://huggingface.co/neuphonic/neucodec) + its ONNX decoder | Decodes speech codes to audio | Apache-2.0 | Downloaded by the setup window; shippable |
| Starter voices (`dave`, `jo`) | Ready-made reference voices | Apache-2.0 | Reference codes derived from the samples in the NeuTTS repo; attribution appreciated |
| [espeak-ng](https://github.com/espeak-ng/espeak-ng) | Grapheme-to-phoneme conversion | **GPL-3.0** | Invoked strictly as a **separate process**, never linked, so it does not encumber your game. If you ship it, include its license text and a source offer/link |
| [Unity Inference Engine / Sentis](https://docs.unity3d.com/Packages/com.unity.ai.inference@latest) | Runs the codec decoder in-engine | Unity Companion License | Unity package dependency |
| [XTTS v2](https://huggingface.co/coqui/XTTS-v2) (optional remote backend) | Alternative TTS during development | Coqui Public Model License (**non-commercial**) | **Development/prototyping only** - do not ship |
| Your chat GGUF (Qwen, Gemma, Llama, ...) | Dialogue generation | varies | Check the model card for redistribution terms before shipping it |
| [PyTorch](https://pytorch.org/) / [`neucodec`](https://pypi.org/project/neucodec/) / [`onnx`](https://pypi.org/project/onnx/) | Baking custom voices, patching the codec ONNX | BSD-3-Clause / Apache-2.0 / Apache-2.0 | Developer machine only, via `Tools~/`; never ships |
