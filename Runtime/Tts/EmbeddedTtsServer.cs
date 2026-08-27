using System.Threading;
using System.Threading.Tasks;

namespace DynamicNpcs
{
    /// <summary>
    /// The embedded llama-server that runs the NeuTTS speech backbone.
    /// Same llama-server binary as the LLM, second process, own port,
    /// launched with --special so speech tokens appear in completions.
    /// </summary>
    public static class EmbeddedTtsServer
    {
        private static readonly LlamaServerHost Host = new LlamaServerHost("tts");

        public static string LogText => Host.LogText;
        public static bool IsRunning => Host.IsRunning;

        public static Task EnsureRunningAsync(DynamicNpcSettings settings, CancellationToken cancellationToken)
            => Host.EnsureRunningAsync(ConfigFrom(settings), cancellationToken);

        public static void Shutdown() => Host.Shutdown();

        private static LlamaServerConfig ConfigFrom(DynamicNpcSettings s) => new LlamaServerConfig
        {
            // Reuses the dialogue LLM's binary unless a separate one is configured
            // (e.g. CUDA build for the LLM, CPU build for TTS).
            exePath = string.IsNullOrWhiteSpace(s.ttsServerPath) ? s.llamaServerPath : s.ttsServerPath,
            modelPath = s.ttsModelPath,
            port = s.ttsPort,
            gpuLayers = s.ttsGpuLayers,
            contextSize = s.ttsContextSize,
            extraArgs = s.ttsExtraServerArgs,
            startupTimeoutSeconds = s.embeddedStartupTimeoutSeconds,
        };
    }
}
