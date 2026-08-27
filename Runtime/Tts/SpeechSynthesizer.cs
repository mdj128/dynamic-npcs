using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DynamicNpcs
{
    /// <summary>
    /// Routes speech synthesis to the configured TTS backend:
    /// the remote XTTS server, or the embedded NeuTTS pipeline.
    /// </summary>
    public static class SpeechSynthesizer
    {
        private static readonly TtsClient Xtts = new TtsClient();
        private static readonly NeuTtsClient NeuTts = new NeuTtsClient();

        public static async Task<AudioClip> SynthesizeAsync(
            DynamicNpcSettings settings,
            string text,
            NpcVoice voice,
            CancellationToken cancellationToken)
        {
            if (settings.ttsBackend == TtsBackendMode.EmbeddedNeuTts)
            {
                await EmbeddedTtsServer.EnsureRunningAsync(settings, cancellationToken);
                return await NeuTts.SynthesizeAsync(settings, text, voice, cancellationToken);
            }
            return await Xtts.SynthesizeAsync(settings, text, voice, cancellationToken);
        }
    }
}
