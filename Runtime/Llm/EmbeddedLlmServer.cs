using System.Threading;
using System.Threading.Tasks;

namespace DynamicNpcs
{
    /// <summary>The embedded llama-server that runs the dialogue LLM.</summary>
    public static class EmbeddedLlmServer
    {
        private static readonly LlamaServerHost Host = new LlamaServerHost("llm");

        public static string LogText => Host.LogText;
        public static bool IsRunning => Host.IsRunning;

        public static Task EnsureRunningAsync(DynamicNpcSettings settings, CancellationToken cancellationToken)
            => Host.EnsureRunningAsync(ConfigFrom(settings), cancellationToken);

        public static void Shutdown() => Host.Shutdown();

        private static LlamaServerConfig ConfigFrom(DynamicNpcSettings s) => new LlamaServerConfig
        {
            exePath = s.llamaServerPath,
            modelPath = s.llmModelPath,
            port = s.embeddedPort,
            gpuLayers = s.gpuLayers,
            contextSize = s.contextSize,
            extraArgs = s.extraServerArgs,
            startupTimeoutSeconds = s.embeddedStartupTimeoutSeconds,
        };
    }
}
