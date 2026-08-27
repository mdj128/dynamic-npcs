using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace DynamicNpcs
{
    /// <summary>
    /// Synthesizes speech with NeuTTS: phonemizes the text (espeak-ng), prompts the
    /// NeuTTS backbone GGUF on the embedded llama-server (/completion, --special),
    /// extracts the generated NeuCodec codes, and decodes them to an AudioClip.
    /// Voice cloning comes from the NpcVoice's baked reference codes + transcript.
    /// Prompt format and sampling mirror neuphonic/neutts (neutts.py::_infer_ggml).
    /// </summary>
    public class NeuTtsClient
    {
        /// <summary>Diagnostic tap: (stage, value) for each pipeline intermediate. Editor tooling only.</summary>
        public static event Action<string, string> DebugTap;

        private static readonly Regex SpeechTokenRegex =
            new Regex(@"<\|speech_(\d+)\|>", RegexOptions.Compiled);

        private static readonly Dictionary<UnityEngine.Object, INeuCodecDecoder> DecoderCache =
            new Dictionary<UnityEngine.Object, INeuCodecDecoder>();

        public async Task<AudioClip> SynthesizeAsync(
            DynamicNpcSettings settings,
            string text,
            NpcVoice voice,
            CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is empty.", nameof(text));
            if (voice == null)
                throw new TtsException("No NpcVoice assigned - NeuTTS needs a baked voice reference.");
            if (!voice.HasNeuttsReference)
                throw new TtsException(
                    $"NpcVoice '{voice.name}' has no baked NeuTTS reference codes. " +
                    "Import a starter voice or bake one (see the NpcVoice inspector).");
            if (string.IsNullOrWhiteSpace(voice.sampleTranscript))
                throw new TtsException(
                    $"NpcVoice '{voice.name}' has no Sample Transcript. NeuTTS needs the " +
                    "exact text spoken in the reference sample.");

            INeuCodecDecoder decoder = GetDecoder(settings);

            string refPhones = await voice.GetNeuttsRefPhonesAsync(settings, cancellationToken);
            string textPhones = await EspeakPhonemizer.PhonemizeAsync(
                settings, text, voice.language, cancellationToken);
            DebugTap?.Invoke("refPhones", refPhones);
            DebugTap?.Invoke("textPhones", textPhones);

            // Prompt format from neutts.py::_infer_ggml (temperature 1.0, top_k 50).
            string prompt =
                "user: Convert the text to speech:<|TEXT_PROMPT_START|>" +
                refPhones + " " + textPhones +
                "<|TEXT_PROMPT_END|>\nassistant:<|SPEECH_GENERATION_START|>" +
                voice.GetNeuttsCodesString();

            string json = BuildCompletionJson(prompt, settings);
            string url = settings.EmbeddedTtsRootUrl + "/completion";

            string content;
            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = settings.ttsTimeoutSeconds;

                using (cancellationToken.Register(req.Abort))
                {
                    await req.SendWebRequest();
                    cancellationToken.ThrowIfCancellationRequested();

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        string body = req.downloadHandler.text;
                        string detail = string.IsNullOrEmpty(body) ? req.error : $"{req.error} - {body}";
                        throw new TtsException($"NeuTTS request failed ({url}): {detail}");
                    }

                    var response = JsonUtility.FromJson<CompletionResponse>(req.downloadHandler.text);
                    content = response?.content;
                }
            }

            DebugTap?.Invoke("content", content);
            var codes = ParseSpeechCodes(content);
            DebugTap?.Invoke("codes", DescribeCodes(codes));
            if (codes.Length == 0)
                throw new TtsException(
                    "NeuTTS produced no speech tokens. Make sure the TTS server runs with " +
                    "--special (settings: Tts Extra Server Args) and the model is a NeuTTS GGUF.");

            float[] samples = decoder.Decode(codes);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAudible(samples);

            var clip = AudioClip.Create($"neutts_{voice.name}", samples.Length, 1, decoder.SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Runs only the codec decode step on known codes (e.g. a voice's baked
        /// reference codes) - isolates the in-engine decoder from the TTS server.
        /// </summary>
        public static AudioClip DecodeCodesToClip(DynamicNpcSettings settings, int[] codes, string clipName)
        {
            if (codes == null || codes.Length == 0)
                throw new TtsException("No codes to decode.");
            var decoder = GetDecoder(settings);
            float[] samples = decoder.Decode(codes);
            EnsureAudible(samples);
            var clip = AudioClip.Create(clipName, samples.Length, 1, decoder.SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static string DescribeCodes(int[] codes)
        {
            if (codes == null || codes.Length == 0)
                return "0 codes";
            var unique = new HashSet<int>(codes);
            int min = int.MaxValue, max = int.MinValue;
            foreach (int c in codes) { if (c < min) min = c; if (c > max) max = c; }
            var head = new StringBuilder();
            for (int i = 0; i < Math.Min(16, codes.Length); i++)
                head.Append(codes[i]).Append(' ');
            return $"{codes.Length} codes, {unique.Count} unique, range {min}..{max}, head: {head}";
        }

        /// <summary>
        /// Real speech never decodes to pure silence (NaN also fails both comparisons
        /// and counts as silent). The usual cause: the codec .onnx was imported with
        /// errors and Unity silently dropped the unsupported ops.
        /// </summary>
        private static void EnsureAudible(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
                if (samples[i] > 1e-4f || samples[i] < -1e-4f)
                    return;
            throw new TtsException(
                "NeuCodec decoder produced only silence. The codec ModelAsset was likely " +
                "imported with errors (Console: \"SplitToSequence not supported\") - Unity " +
                "drops unsupported ops but still creates the asset. Run " +
                "Tools~/patch_neucodec_onnx.py on the .onnx file, then Reimport + Reassign " +
                "it in the Embedded Server Setup window.");
        }

        public static int[] ParseSpeechCodes(string content)
        {
            if (string.IsNullOrEmpty(content))
                return Array.Empty<int>();
            var matches = SpeechTokenRegex.Matches(content);
            var codes = new int[matches.Count];
            for (int i = 0; i < matches.Count; i++)
                codes[i] = int.Parse(matches[i].Groups[1].Value);
            return codes;
        }

        private static string BuildCompletionJson(string prompt, DynamicNpcSettings settings)
        {
            var sb = new StringBuilder();
            sb.Append("{\"prompt\":\"").Append(JsonText.Escape(prompt)).Append('"');
            sb.Append(",\"n_predict\":").Append(settings.ttsMaxCodes);
            sb.Append(",\"temperature\":").Append(
                settings.ttsTemperature.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"top_k\":").Append(settings.ttsTopK);
            sb.Append(",\"stop\":[\"<|SPEECH_GENERATION_END|>\"]");
            // The per-voice prompt prefix (reference codes) is identical across
            // sentences, so llama-server's prompt cache skips re-prefilling it.
            sb.Append(",\"cache_prompt\":true}");
            return sb.ToString();
        }

        private static INeuCodecDecoder GetDecoder(DynamicNpcSettings settings)
        {
#if DYNAMICNPCS_SENTIS || DYNAMICNPCS_INFERENCE
            if (settings.neuCodecDecoder == null)
                throw new TtsException(
                    "No Neu Codec Decoder assigned in settings. Download/import the " +
                    "neucodec ONNX decoder via Window > Dynamic NPCs > Embedded Server Setup.");

            if (!DecoderCache.TryGetValue(settings.neuCodecDecoder, out var decoder))
            {
                decoder = new SentisNeuCodecDecoder(settings.neuCodecDecoder);
                DecoderCache[settings.neuCodecDecoder] = decoder;
            }
            return decoder;
#else
            throw new TtsException(
                "Embedded NeuTTS requires the Unity Inference Engine package " +
                "(com.unity.ai.inference; named com.unity.sentis before Unity 6.1) for " +
                "codec decoding. Install it via the Package Manager (the Embedded Server " +
                "Setup window has a shortcut), then re-open the project.");
#endif
        }

        [Serializable]
        private class CompletionResponse
        {
            public string content;
        }
    }
}
