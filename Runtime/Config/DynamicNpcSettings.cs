using UnityEngine;

namespace DynamicNpcs
{
    public enum LlmBackendMode
    {
        /// <summary>Talk to a server the developer runs themselves (Ollama, LM Studio, llama.cpp, ...).</summary>
        RemoteServer,

        /// <summary>Auto-launch a llama-server binary bundled with the game. No external installs.</summary>
        EmbeddedLlamaServer,
    }

    public enum TtsBackendMode
    {
        /// <summary>The XTTS FastAPI server (Python; dev/prototyping only - non-commercial model license).</summary>
        RemoteXtts,

        /// <summary>NeuTTS on an embedded llama-server + Sentis codec decoding. Fully shippable, no installs.</summary>
        EmbeddedNeuTts,
    }

    /// <summary>
    /// Global connection / pipeline settings shared by all NPCs.
    /// Create via Assets > Create > Dynamic NPCs > Settings.
    /// </summary>
    [CreateAssetMenu(fileName = "DynamicNpcSettings", menuName = "Dynamic NPCs/Settings", order = 0)]
    public class DynamicNpcSettings : ScriptableObject
    {
        [Header("LLM backend")]
        [Tooltip("EmbeddedLlamaServer (default): auto-launch a llama-server binary shipped with the game - players install nothing. RemoteServer: use an already-running server (Ollama, LM Studio, ...) during development.")]
        public LlmBackendMode llmBackend = LlmBackendMode.EmbeddedLlamaServer;

        [Header("Remote LLM server (any OpenAI-compatible API)")]
        [Tooltip("Ollama: http://localhost:11434/v1 - LM Studio: http://localhost:1234/v1 - llama.cpp server: http://localhost:8080/v1")]
        public string llmBaseUrl = "http://localhost:11434/v1";

        [Tooltip("Model name/tag as known by the remote server, e.g. 'gemma4' or 'llama3.1:8b'. Ignored by the embedded server (it serves the loaded GGUF). Personas can override this.")]
        public string llmModel = "gemma4";

        [Header("Embedded llama-server")]
        [Tooltip("llama-server executable. Relative paths resolve under StreamingAssets so the binary ships inside builds. Use Window > Dynamic NPCs > Embedded Server Setup to download it.")]
        public string llamaServerPath = "DynamicNPCs/llama-server/llama-server.exe";

        [Tooltip("GGUF chat model. Relative paths resolve under StreamingAssets (put the model there to include it in builds). Absolute paths work for development, e.g. a model already downloaded by LM Studio.")]
        public string llmModelPath = "";

        [Tooltip("Localhost port the embedded server listens on.")]
        public int embeddedPort = 8090;

        [Tooltip("Model layers offloaded to the GPU. 99 = entire model on GPU, 0 = CPU only.")]
        [Range(0, 99)] public int gpuLayers = 99;

        [Tooltip("Context window in tokens. Smaller = less VRAM.")]
        public int contextSize = 8192;

        [Tooltip("Extra llama-server command-line arguments.")]
        public string extraServerArgs = "--jinja";

        [Tooltip("How long to wait for the embedded server to load the model on first start.")]
        [Min(10)] public int embeddedStartupTimeoutSeconds = 180;

        [Header("LLM request")]
        [Min(1)] public int llmTimeoutSeconds = 120;

        [Header("TTS backend")]
        [Tooltip("RemoteXtts: the Python XTTS server (dev only). EmbeddedNeuTts: NeuTTS GGUF on an embedded llama-server + Sentis codec - shippable, players install nothing.")]
        public TtsBackendMode ttsBackend = TtsBackendMode.EmbeddedNeuTts;

        [Header("Remote XTTS server")]
        [Tooltip("Base URL of the XTTS server (endpoints /health and /tts).")]
        public string ttsBaseUrl = "http://localhost:8000";

        [Header("Embedded NeuTTS")]
        [Tooltip("NeuTTS backbone GGUF (e.g. neutts-air Q4/Q8). Relative paths resolve under StreamingAssets.")]
        public string ttsModelPath = "DynamicNPCs/models/neutts-air-Q4_0.gguf";

        [Tooltip("Optional separate llama-server binary for TTS. Empty = reuse the LLM's binary.")]
        public string ttsServerPath = "";

        [Tooltip("Localhost port for the TTS llama-server (must differ from the LLM port).")]
        public int ttsPort = 8091;

        [Tooltip("NeuTTS layers offloaded to GPU. 0 (CPU) is recommended: the model is small, realtime on CPU, and this keeps the GPU free for rendering + the LLM.")]
        [Range(0, 99)] public int ttsGpuLayers = 0;

        [Tooltip("NeuTTS context. 2048 matches the model's training context.")]
        public int ttsContextSize = 2048;

        [Tooltip("Extra args for the TTS server. --special is required so speech tokens appear in completions.")]
        public string ttsExtraServerArgs = "--special";

        [Tooltip("Max NeuCodec codes generated per sentence (50 codes = 1 second of speech).")]
        [Min(50)] public int ttsMaxCodes = 1000;

        [Tooltip("NeuTTS sampling temperature. 1.0 matches the reference implementation. Lower (0.6-0.9) = steadier, more deliberate delivery; higher = livelier but less stable pacing.")]
        [Range(0.1f, 1.5f)] public float ttsTemperature = 1.0f;

        [Tooltip("NeuTTS top-k sampling. 50 matches the reference implementation. Lower (20-40) = more conservative, consistent prosody.")]
        [Min(1)] public int ttsTopK = 50;

        [Tooltip("The neuphonic/neucodec-onnx-decoder model.onnx imported as a Sentis ModelAsset.")]
        public UnityEngine.Object neuCodecDecoder;

        [Tooltip("espeak-ng executable used for phonemization (relative = under StreamingAssets). Runs as a separate process.")]
        public string espeakPath = "DynamicNPCs/espeak-ng/espeak-ng.exe";

        [Min(1)] public int ttsTimeoutSeconds = 180;

        [Tooltip("When an NpcVoice uses a WAV file path, translate Windows paths (D:\\foo\\bar.wav) to WSL paths (/mnt/d/foo/bar.wav) before sending, for a TTS server running inside WSL.")]
        public bool mapSpeakerPathsToWsl = true;

        [Tooltip("Language code sent to XTTS when a voice does not specify one.")]
        public string defaultLanguage = "en";

        [Header("Speech pipeline")]
        [Tooltip("Minimum characters accumulated before a sentence chunk is sent to TTS. Larger = fewer, longer clips; smaller = lower latency but choppier prosody.")]
        [Min(1)] public int minChunkChars = 24;

        [Tooltip("Silence between spoken sentences, in seconds. Each sentence is synthesized as a separate clip, so this restores the natural pause at sentence boundaries.")]
        [Range(0f, 1.5f)] public float interSentencePause = 0.3f;

        public bool UsesEmbeddedLlm => llmBackend == LlmBackendMode.EmbeddedLlamaServer;
        public bool UsesEmbeddedTts => ttsBackend == TtsBackendMode.EmbeddedNeuTts;

        /// <summary>Root URL of the embedded LLM server, without the /v1 suffix.</summary>
        public string EmbeddedServerRootUrl => $"http://127.0.0.1:{embeddedPort}";

        /// <summary>Root URL of the embedded NeuTTS server.</summary>
        public string EmbeddedTtsRootUrl => $"http://127.0.0.1:{ttsPort}";

        /// <summary>The OpenAI-compatible base URL for whichever backend is active.</summary>
        public string ResolveLlmBaseUrl()
            => UsesEmbeddedLlm ? EmbeddedServerRootUrl + "/v1" : llmBaseUrl;
    }
}
