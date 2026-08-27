# Changelog

## [0.3.3] - 2026-08-27

- First public release: the package now lives at
  [github.com/mdj128/dynamic-npcs](https://github.com/mdj128/dynamic-npcs) and installs via
  Package Manager git URL. No runtime changes from 0.3.2.
- Added MIT `LICENSE`, `THIRD-PARTY-NOTICES.md` covering every model/binary the embedded
  stack pulls in, and package.json metadata (license, repository, documentation/changelog
  URLs) so Package Manager links resolve.

## [0.3.2] - 2026-07-09

- Fix (prosody): interior punctuation was silently dropped during phonemization.
  Punctuation-stripped segments were fed to espeak-ng as stdin lines, which merged
  them into one sentence and returned fewer lines than sent, triggering the
  join-and-keep-last-mark fallback on every call. NeuTTS therefore never saw
  comma/period cues - the main cause of flat, rushed cadence. Each segment now
  carries a terminal period so espeak emits one line per segment and punctuation
  is reassembled correctly (this also fixes the voice reference phonemes).
- New: `ttsTemperature` (default 1.0) and `ttsTopK` (default 50) settings for
  NeuTTS sampling; lower values give steadier, more conservative delivery.
- New: `interSentencePause` (default 0.3 s) - sentences are separate TTS clips, so
  the agent now restores the natural pause at sentence boundaries during playback.
- New: "Bake From Sample" button in the NpcVoice inspector - one-click voice baking.
  Finds a system Python 3, provisions a cached venv under Library/ (dev-only, never
  shipped), runs Tools~/bake_neutts_voice.py with a cancelable progress bar, and
  applies the resulting reference codes to the asset. Uses the assigned sample clip,
  the WAV path field, or a file picker as the source.

## [0.3.1] - 2026-07-08

- Fix: support Unity Inference Engine (com.unity.ai.inference), the Unity 6.1+ rename of
  Sentis - installing com.unity.sentis there fails with an "invalid signature" error and
  leaves Unity.Sentis unresolved. The runtime now compiles against either package
  (DYNAMICNPCS_INFERENCE / DYNAMICNPCS_SENTIS version defines), and the setup window
  detects/installs the correct package id for the running editor version.
- Fix: don't dispose the worker-owned tensor returned by PeekOutput in the codec decoder.
- Fix: the neucodec ONNX decoder failed to import ("SplitToSequence not supported") -
  added Tools~/patch_neucodec_onnx.py which rewrites SplitToSequence+SequenceAt
  (torch qkv unbind) into Gather ops Unity supports, verified bit-exact vs onnxruntime.
  Setup window gained a "Reimport + Reassign Codec Decoder" button and no longer
  assigns a failed (DefaultAsset) import to settings.
- Fix: Test Console previews were inaudible on Unity 6 - AudioUtil.PlayPreviewClip only
  plays clips with imported preview data and silently ignores procedural clips
  (AudioClip.Create), i.e. every TTS clip. Editor preview now plays through a hidden
  AudioSource (same mechanism as in-game playback). In-game playback was never affected.
- Guard: Unity's ONNX importer drops unsupported ops but still creates a usable-looking
  ModelAsset, which decodes to pure silence. The decoder now throws a descriptive error
  on silent output instead of playing nothing. Test Console reports clip duration/peak
  (and NaN counts), can replay/save the last clip as WAV, has a "Test Codec Decoder"
  button that decodes the voice's baked codes in isolation, and a headless diagnostic
  (Window > Dynamic NPCs > Debug, also -executeMethod DynamicNpcs.Editor.CodecDebugRunner.Run).

## [0.3.0] - 2026-07-07

- Embedded NeuTTS backend: fully shippable, Python-free voice-cloned TTS. NeuTTS
  backbone GGUF runs on a second embedded llama-server (--special, CPU by default);
  NeuCodec ONNX decoder runs in-engine via Unity Sentis; phonemization via espeak-ng
  as a separate process (GPL isolation). Prompt format/sampling mirror
  neuphonic/neutts _infer_ggml, with per-voice prompt-cache reuse.
- NpcVoice: sample transcript + baked NeuCodec reference codes; inspector imports
  reference JSONs and ships two Apache-2.0 starter voices (dave, jo). Custom voices
  bake once via Tools~/bake_neutts_voice.py (dev machine only).
- Setup window: NeuTTS section - GGUF picker, one-click espeak-ng install (Windows,
  MSI admin-extract into StreamingAssets), Sentis install, codec ONNX download,
  TTS server start/stop.
- Refactor: generic LlamaServerHost powers both embedded server instances;
  SpeechSynthesizer routes between XTTS (dev) and NeuTTS (shipping) backends.

## [0.2.0] - 2026-07-07

- Embedded LLM backend: auto-launches a llama-server binary shipped in StreamingAssets,
  so players need no Ollama/Python installs. Models are developer-supplied GGUF files
  (dev-time absolute paths or StreamingAssets for builds) - never bundled in the package.
- Window > Dynamic NPCs > Embedded Server Setup: downloads llama.cpp release binaries
  (Vulkan/CUDA/CPU) into StreamingAssets, picks a GGUF model, start/stop/log/benchmark.
- Test Console: "Measure Latency" button reports first-token, first-sentence, TTS, and
  time-to-first-word for a persona.
- Embedded server survives editor play-mode restarts (killed on editor quit) and is
  shared by all agents; restarts automatically when its config changes.

## [0.1.0] - 2026-07-07

- Initial release: NpcDialogueAgent, NpcPersona / NpcVoice / DynamicNpcSettings assets,
  streaming OpenAI-compatible LLM client, XTTS voice-cloning TTS client with
  sentence-level pipelining, editor test console, basic chat sample.
