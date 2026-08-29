# Dynamic NPCs

[![Unity 2021.3+](https://img.shields.io/badge/unity-2021.3%2B-black?logo=unity)](https://unity.com/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Install: git URL](https://img.shields.io/badge/install-git%20URL-brightgreen.svg)](#installation)

A Unity package for fully local, dynamic, **voiced** NPC dialogue that ships inside your game — players install nothing:

- A **local LLM** writes each reply in character (embedded llama-server, or any OpenAI-compatible server during development).
- **Local TTS with voice cloning** speaks the reply — embedded NeuTTS (Apache-2.0, shippable) or the XTTS dev server.
- The LLM response is **streamed sentence-by-sentence into TTS**, so the NPC starts talking while the rest of the reply is still generating.

**Models are never bundled.** You bring your own GGUF files and include them in `StreamingAssets` when you ship.

```
Player line ──► dialogue LLM (llama-server #1, GPU) ──streamed──► sentence chunker
                                                                       │ sentence
                          espeak-ng (process) ◄── phonemize ◄──────────┘
                                 │ IPA
baked voice codes ──► NeuTTS (llama-server #2, CPU, --special) ──► speech codes
                                 │
                    NeuCodec ONNX decoder (Unity Sentis) ──► AudioClip ──► AudioSource
```

## Requirements

- Unity 2021.3+ (desktop; the embedded servers use `System.Diagnostics.Process`)
- A GGUF chat model (3–4B at Q4 recommended, e.g. Qwen3-4B / Gemma-3-4B)
- A NeuTTS backbone GGUF (e.g. `neuphonic/neutts-air-q4-gguf`, ~750M params, realtime on CPU)
- [Unity Inference Engine](https://docs.unity3d.com/Packages/com.unity.ai.inference@latest) (`com.unity.ai.inference` on Unity 6.1+; its former name `com.unity.sentis` on older versions) for codec decoding — the setup window installs the right one for you

## Installation

### Package Manager (recommended)

**Window > Package Manager > + > Install package from git URL...** and paste:

```
https://github.com/mdj128/dynamic-npcs.git
```

To pin a version, append a tag: `https://github.com/mdj128/dynamic-npcs.git#v0.3.5`

Or add it to `Packages/manifest.json` directly:

```json
"com.mdj.dynamicnpcs": "https://github.com/mdj128/dynamic-npcs.git#v0.3.5"
```

Git URL installs need [Git](https://git-scm.com/) on your PATH and a restart of Unity if it
was installed while the editor was open.

### From disk (for hacking on the package)

Clone the repo anywhere, then **Window > Package Manager > + > Add package from disk...** and
pick its `package.json`, or point `Packages/manifest.json` at the folder:

```json
"com.mdj.dynamicnpcs": "file:../../dynamic-npcs"
```

## Setup: Window > Dynamic NPCs > Embedded Server Setup

One window drives the whole embedded stack:

1. **Server binary** — *Fetch Available Builds* lists the latest llama.cpp release; download one into `StreamingAssets` (Vulkan = easy default on any GPU; CUDA = fastest on NVIDIA, cudart fetched automatically; CPU = no GPU use). One binary serves both the LLM and TTS.
2. **Dialogue GGUF** — *Browse for .gguf...*: reference in place (dev only — e.g. straight out of LM Studio's models folder) or copy into `StreamingAssets` (ships in builds).
3. **Switch to Embedded backend**, **Start**, **Benchmark** (first-token latency + tok/s).
4. **Embedded TTS (NeuTTS)** section:
   - **NeuTTS GGUF**: download from the model page (button provided), then browse to it.
   - **espeak-ng**: one click downloads and unpacks it into `StreamingAssets` (Windows; on macOS/Linux install via brew/apt and set the path). Used for phonemization, as a separate process.
   - **Inference Engine + codec decoder**: one click installs the inference package (`com.unity.ai.inference`, or `com.unity.sentis` pre-6.1), another downloads the NeuCodec ONNX decoder and assigns it. The decoder repo is **gated** (free, but you must accept its terms): open the model page from the window, sign in and accept, then paste a [read access token](https://huggingface.co/settings/tokens) into the *HF Access Token* field before downloading. The token is kept in `EditorPrefs` on your machine, never in the project. If you would rather not use a token, download `model.onnx` in your browser and point the window at it with *Browse for Existing .onnx...*.
   - **Switch settings to Embedded NeuTTS backend.**

Embedded server behavior (both instances): started lazily on first use (warm them from a loading screen via `EmbeddedLlmServer` / `EmbeddedTtsServer.EnsureRunningAsync`), survive editor play-mode restarts (killed when the editor quits), restart automatically when config changes, and adopt an already-running server on their port.

### Remote mode (development)

Both backends also have remote modes: point `Llm Base Url` at Ollama (`http://localhost:11434/v1`) / LM Studio (`http://localhost:1234/v1`), and/or `Tts Base Url` at the XTTS server from the `sounds` repo. **XTTS is dev-only**: its weights are non-commercially licensed and it needs Python.

## NPC assets

### Voices — *Assets > Create > Dynamic NPCs > NPC Voice*

For the **embedded NeuTTS backend**, a voice = baked NeuCodec reference codes + the transcript of the sample:

- Quickest start: the voice inspector has **starter voices** (dave, jo — from the NeuTTS repo, Apache-2.0) you can apply with one click.
- Custom voices: record 3–15 s of clean, single-speaker speech, set `Sample Transcript` to the exact words spoken (punctuation included), and click **Bake From Sample** in the voice inspector. It needs a Python 3 on the dev machine and handles the rest itself: creates a cached venv under `Library/`, installs `neucodec` (first bake downloads PyTorch + the NeuCodec encoder weights, several minutes; later bakes take seconds), encodes the sample, and applies the codes to the asset. Players never need Python — the baked codes ship as plain data in the asset. (`Tools~/bake_neutts_voice.py` remains available for CLI/CI use, imported via **Import NeuTTS Reference (JSON)**.)

For the **XTTS dev backend**, assign a sample AudioClip (import Load Type = *Decompress On Load*) or an absolute WAV path (WSL path mapping supported).

### Personas — *Assets > Create > Dynamic NPCs > NPC Persona*

Name, personality prompt, world context, voice, and per-NPC LLM tuning (model override, temperature, max tokens, history). Keep `maxTokens` small — it bounds worst-case latency.

### The agent

Add **Dynamic NPCs > NPC Dialogue Agent** to the NPC GameObject, assign settings + persona:

```csharp
agent.Ask("Have you seen any wolves lately?");   // LLM reply, spoken aloud
string reply = await agent.AskAsync("...");        // awaitable, returns full text
agent.Say("Welcome, traveler.");                   // exact text, no LLM
agent.CancelSpeech();  agent.ResetConversation();
```

Events: **On Sentence Started (string)** (subtitles), **On Response Text**, **On Speech Finished**, **On Error**.

## Tuning the voice

- **Punctuation drives cadence.** NeuTTS reads pauses and rhythm from punctuation embedded
  in the phoneme string, so personas should write naturally punctuated dialogue. Commas,
  periods, ellipses, and question marks all audibly change delivery.
- **The reference sample dominates prosody.** The voice speaks with the cadence of its
  3–15 s reference. A rushed or rambling sample clones rushed, rambling speech; a measured,
  clearly-paused sample clones that instead. For a custom voice, record the style you want
  to hear and make `Sample Transcript` match the recording word-for-word, punctuation included.
- **`Tts Temperature`** (default 1.0, the reference value): lower it to 0.7–0.9 for steadier,
  more deliberate pacing; raise it for livelier but less predictable delivery.
  **`Tts Top K`** (default 50): lowering to 20–40 also stabilizes prosody.
- **`Inter Sentence Pause`** (default 0.3 s): silence the agent inserts between sentence
  clips during playback — raise it if sentences run into each other, lower for snappier
  back-and-forth.

## Performance notes (12 GB-class GPU)

- Dialogue LLM on GPU (`Gpu Layers = 99`), **NeuTTS on CPU** (`Tts Gpu Layers = 0`, the default): the TTS model is small and realtime on CPU, keeping VRAM and GPU time for rendering + the LLM.
- Per-voice prompt prefixes (the reference codes) are constant, so llama-server's prompt cache (`cache_prompt`) skips re-prefilling them after each voice's first sentence.
- **Test Console > Measure Latency** reports first token / first sentence / TTS time / **time-to-first-word** for a persona on your machine.
- Masking tricks: pre-baked filler barks on submit, `Ask` when the dialogue UI opens (not on confirm), pre-`Say()` greetings at scene load.

## Shipping in a build

- Everything under `StreamingAssets` (llama-server, models, espeak-ng) ships automatically; absolute paths don't.
- macOS/Linux: restore the executable bit on `llama-server`/`espeak-ng` post-build (`chmod +x`).
- Use the Mono scripting backend, or verify `System.Diagnostics.Process` on IL2CPP for your Unity version.

### Licensing (read before shipping)

Full detail, with links and source offers, in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

| Component | License | Note |
|---|---|---|
| NeuTTS Air weights, NeuCodec + ONNX decoder | Apache-2.0 | Shippable |
| llama.cpp (llama-server) | MIT | Shippable |
| espeak-ng | **GPL-3.0** | Runs strictly as a separate process (never linked), which keeps your game unencumbered; ship its license text + source link alongside |
| Starter voices (dave, jo) | Apache-2.0 (neuphonic/neutts samples) | Attribution appreciated |
| XTTS v2 weights | Coqui non-commercial | Dev/prototyping only |
| Your GGUF chat model | varies | Check its card for redistribution terms |

## Troubleshooting

- **"NeuTTS produced no speech tokens"** — the TTS server must run with `--special` (default in `Tts Extra Server Args`); also confirm the GGUF is a NeuTTS backbone, not a chat model.
- **"espeak-ng not found"** — setup window, NeuTTS section (Windows one-click) or install via brew/apt and set the path.
- **"requires the Unity Inference Engine package"** — install `com.unity.ai.inference` (Unity 6.1+) or `com.unity.sentis` (older), then re-download/reimport the codec ONNX and assign it in settings.
- **"package has an invalid signature" installing com.unity.sentis** — you're on Unity 6.1+, where Sentis was renamed to Inference Engine; remove `com.unity.sentis` from `Packages/manifest.json` and install `com.unity.ai.inference` instead.
- **`InvalidProtocolBufferException: Protocol message contained a tag with an invalid wire type`** — the `.onnx` on disk is not a model. The NeuCodec repo is gated, so an unauthenticated download saves Hugging Face's short error page under the `.onnx` name and the importer tries to parse that text as protobuf. Use *Delete Broken File* in the setup window, accept the terms on the model page, and download again with an access token (or browse to a manually downloaded copy). The setup window now validates downloads before they reach your `Assets` folder, so this only affects files fetched by older versions.
- **"SplitToSequence not supported" importing the codec ONNX** — Unity's ONNX importer doesn't support the sequence ops the upstream export uses for attention `unbind`. Run `Tools~/patch_neucodec_onnx.py` on the downloaded file (one-time, dev machine only: `pip install onnx`; rewrites them to equivalent `Gather` ops — verified bit-exact), then use **Reimport + Reassign Codec Decoder** in the setup window.
- **"llama-server exited during startup"** — see the log view; usual suspects: corrupt GGUF, not enough VRAM (lower `Gpu Layers`), CUDA build missing cudart.
- **Voice sounds wrong / robotic** — reference quality matters most: 3–15 s, clean, mono, natural speech, accurate transcript.
- **Port conflicts** — LLM (8090) and TTS (8091) ports must differ; whatever answers `/health` on a port gets adopted.

## Contributing & support

Issues and pull requests are welcome at
[github.com/mdj128/dynamic-npcs](https://github.com/mdj128/dynamic-npcs). When reporting a
problem, please include your Unity version, GPU, the backend you are on (embedded vs remote),
and the relevant llama-server log from the setup window - most reports come down to model,
driver, or path specifics.

## License

MIT - see [LICENSE](LICENSE). The models, binaries, and tools this package drives carry their
own licenses and are **not** bundled here; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) before shipping a build.
