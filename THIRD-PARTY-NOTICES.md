# Third-party notices

This package's own source is MIT licensed (see `LICENSE`). It downloads, drives, or
interoperates with the components below, none of which are bundled in this repository.
If you ship a game with the embedded stack, you are redistributing them and their terms apply.

| Component | Used for | License | Notes |
|---|---|---|---|
| [llama.cpp](https://github.com/ggml-org/llama.cpp) (`llama-server`) | Runs the dialogue LLM and the NeuTTS backbone | MIT | Downloaded into `StreamingAssets` by the setup window; shippable |
| [NeuTTS Air](https://huggingface.co/neuphonic/neutts-air) weights | Voice-cloned speech synthesis | Apache-2.0 | Downloaded by the setup window; shippable. **Redistributed unmodified** - see the note below |
| [NeuCodec](https://huggingface.co/neuphonic/neucodec) + its [ONNX decoder](https://huggingface.co/neuphonic/neucodec-onnx-decoder) | Decodes speech codes to audio | Apache-2.0 | Downloaded by the setup window; shippable. **Redistributed** - see the note below |
| Starter voices (`dave`, `jo`) | Ready-made reference voices | Apache-2.0 | Reference codes derived from the samples in the NeuTTS repo; attribution appreciated |
| [espeak-ng](https://github.com/espeak-ng/espeak-ng) | Grapheme-to-phoneme conversion | **GPL-3.0** | Invoked strictly as a **separate process**, never linked, so it does not encumber your game. If you ship it, include its license text and a source offer/link |
| [Unity Inference Engine / Sentis](https://docs.unity3d.com/Packages/com.unity.ai.inference@latest) | Runs the codec decoder in-engine | Unity Companion License | Unity package dependency |
| [XTTS v2](https://huggingface.co/coqui/XTTS-v2) (optional remote backend) | Alternative TTS during development | Coqui Public Model License (**non-commercial**) | **Development/prototyping only** - do not ship |
| Your chat GGUF (Qwen, Gemma, Llama, ...) | Dialogue generation | varies | Check the model card for redistribution terms before shipping it |
| [PyTorch](https://pytorch.org/) / [`neucodec`](https://pypi.org/project/neucodec/) / [`onnx`](https://pypi.org/project/onnx/) | Baking custom voices, patching the codec ONNX | BSD-3-Clause / Apache-2.0 / Apache-2.0 | Developer machine only, via `Tools~/`; never ships |

## Redistributed: the NeuCodec decoder and the NeuTTS backbone

`neucodec-decoder-unity.onnx`, attached to this repository's
[`codec-v1` release](https://github.com/mdj128/dynamic-npcs/releases/tag/codec-v1), is
**[neuphonic/neucodec-onnx-decoder](https://huggingface.co/neuphonic/neucodec-onnx-decoder)**
by Neuphonic, Copyright the Neuphonic authors, licensed under the Apache License,
Version 2.0. A copy of the License is available at
<http://www.apache.org/licenses/LICENSE-2.0>.

**It is a modified version.** The upstream export implements each attention block's
`qkv.unbind(0)` as `SplitToSequence` + `SequenceAt`, which Unity's ONNX importer does
not support; those pairs are rewritten to equivalent `Gather` ops. The weights and graph
semantics are otherwise untouched, and the transformation was verified bit-exact against
onnxruntime. It is reproducible from the upstream file with `Tools~/patch_neucodec_onnx.py`
in this repository.

It is mirrored here because the upstream repo is gated, and because the upstream file
cannot be imported by Unity without this patch either way. Credit for the model belongs
to Neuphonic; please cite them rather than this repository. Setup can be pointed back at
the upstream file at any time via *Fetch from upstream Hugging Face* in the setup window.

`neutts-air-Q4_0.gguf`, attached to the same release, is
**[neuphonic/neutts-air-q4-gguf](https://huggingface.co/neuphonic/neutts-air-q4-gguf)**
byte-for-byte, Copyright the Neuphonic authors, licensed under the Apache License,
Version 2.0. It is **unmodified**, and mirrored only because the upstream repo is gated.
Setup can be pointed at your own copy at any time via *Browse NeuTTS .gguf...*.

`neucodec-encoder.bin`, attached to the same release, is the `pytorch_model.bin` of
**[neuphonic/neucodec](https://huggingface.co/neuphonic/neucodec)**, Copyright the
Neuphonic authors, licensed under the Apache License, Version 2.0. It is **unmodified**
(sha256 `30c3ea13ceeb2de693c56e5e33a1b7e00d44c95dcdd08a4ed0d552d0bf59ebdf`) and is used
only on developer machines, to bake a custom voice; it never ships in a build.

It is mirrored because that repo became gated, and because `NeuCodec.from_pretrained`
hard-codes the gated repo id, leaving no supported way to load the encoder from anywhere
else. Since the gate blocks verification against the original, the copy was taken from an
ungated third-party mirror and cross-checked against a second, independent mirror: both
report the same size and the same sha256 as above, and the weights were confirmed to load
and encode correctly.
