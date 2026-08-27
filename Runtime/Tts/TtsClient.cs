using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace DynamicNpcs
{
    public class TtsException : Exception
    {
        public TtsException(string message) : base(message) { }
    }

    /// <summary>
    /// Client for the local XTTS FastAPI server (sounds/tts_server/server.py).
    /// Sends text plus a per-voice speaker sample (base64 WAV or a server-readable
    /// file path) and returns the synthesized speech as an AudioClip.
    /// </summary>
    public class TtsClient
    {
        public async Task<AudioClip> SynthesizeAsync(
            DynamicNpcSettings settings,
            string text,
            NpcVoice voice,
            CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is empty.", nameof(text));
            if (voice == null)
                throw new TtsException("No NpcVoice assigned - XTTS v2 needs a speaker sample to clone.");

            string json = BuildRequestJson(settings, text, voice);
            string url = settings.ttsBaseUrl.TrimEnd('/') + "/tts";

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
                        throw new TtsException($"TTS request failed ({url}): {detail}");
                    }

                    byte[] wav = req.downloadHandler.data;
                    return WavUtility.ToAudioClip(wav, $"tts_{voice.name}");
                }
            }
        }

        /// <summary>GET /health on the TTS server; returns the response body.</summary>
        public async Task<string> CheckAsync(DynamicNpcSettings settings, CancellationToken cancellationToken)
        {
            using (var req = UnityWebRequest.Get(settings.ttsBaseUrl.TrimEnd('/') + "/health"))
            {
                req.timeout = 10;
                using (cancellationToken.Register(req.Abort))
                {
                    await req.SendWebRequest();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (req.result != UnityWebRequest.Result.Success)
                        throw new TtsException($"TTS health check failed ({req.url}): {req.error}");
                    return req.downloadHandler.text;
                }
            }
        }

        private static string BuildRequestJson(DynamicNpcSettings settings, string text, NpcVoice voice)
        {
            string language = string.IsNullOrWhiteSpace(voice.language)
                ? settings.defaultLanguage
                : voice.language;

            var sb = new StringBuilder();
            sb.Append("{\"text\":\"").Append(JsonText.Escape(text)).Append('"');
            sb.Append(",\"language\":\"").Append(JsonText.Escape(language)).Append('"');

            if (voice.UsesFilePath)
            {
                string path = voice.GetSpeakerPath(settings.mapSpeakerPathsToWsl);
                sb.Append(",\"speaker_wav_path\":\"").Append(JsonText.Escape(path)).Append('"');
            }
            else
            {
                sb.Append(",\"speaker_audio_base64\":\"").Append(voice.GetSampleBase64()).Append('"');
            }

            sb.Append('}');
            return sb.ToString();
        }
    }
}
