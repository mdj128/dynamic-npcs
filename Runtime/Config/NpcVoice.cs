using System;
using UnityEngine;

namespace DynamicNpcs
{
    /// <summary>
    /// A cloneable NPC voice, defined by a reference sample of speech.
    /// XTTS clones the timbre/accent of whatever sample you provide
    /// (6-30 seconds of clean, single-speaker audio works best).
    /// Create via Assets > Create > Dynamic NPCs > NPC Voice.
    /// </summary>
    [CreateAssetMenu(fileName = "NpcVoice", menuName = "Dynamic NPCs/NPC Voice", order = 1)]
    public class NpcVoice : ScriptableObject
    {
        public string displayName = "New Voice";

        [Tooltip("Voice sample used for cloning. Import settings must use Load Type = Decompress On Load so sample data can be read. Sent to the server as base64 WAV.")]
        public AudioClip sampleClip;

        [Tooltip("Optional absolute path to a WAV file on disk. If set, this path is sent instead of the AudioClip and the TTS server reads the file itself (see 'Map Speaker Paths To Wsl' in settings). Avoids re-uploading the sample on every request.")]
        public string sampleWavPath = "";

        [Tooltip("Language code for this voice (XTTS codes like en/es/fr, mapped to espeak voices for NeuTTS). Empty = use the settings default.")]
        public string language = "en";

        [Header("NeuTTS reference (embedded TTS)")]
        [TextArea(2, 5)]
        [Tooltip("The exact words spoken in the voice sample. Required for NeuTTS voice cloning.")]
        public string sampleTranscript = "";

        [Tooltip("Baked NeuCodec codes of the voice sample (50 codes/second). Import a starter voice or bake with Tools~/bake_neutts_voice.py - see the inspector.")]
        [HideInInspector] public int[] neuttsRefCodes = Array.Empty<int>();

        [NonSerialized] private AudioClip _cachedClip;
        [NonSerialized] private string _cachedBase64;
        [NonSerialized] private string _cachedCodesString;
        [NonSerialized] private int[] _cachedCodesFor;
        [NonSerialized] private string _cachedRefPhones;
        [NonSerialized] private string _cachedRefPhonesFor;

        public bool HasNeuttsReference => neuttsRefCodes != null && neuttsRefCodes.Length > 0;

        public bool UsesFilePath => !string.IsNullOrWhiteSpace(sampleWavPath);

        /// <summary>
        /// Returns the sample clip encoded as base64 PCM16 WAV, cached per clip.
        /// </summary>
        public string GetSampleBase64()
        {
            if (sampleClip == null)
                throw new InvalidOperationException($"NpcVoice '{name}' has no sampleClip and no sampleWavPath.");

            if (_cachedBase64 != null && _cachedClip == sampleClip)
                return _cachedBase64;

            if (sampleClip.loadType != AudioClipLoadType.DecompressOnLoad)
                Debug.LogWarning($"[DynamicNPCs] Voice '{name}': sample clip '{sampleClip.name}' should use Load Type = Decompress On Load, otherwise sample data may be unreadable.", this);

            byte[] wav = WavUtility.FromAudioClip(sampleClip);
            _cachedBase64 = Convert.ToBase64String(wav);
            _cachedClip = sampleClip;
            return _cachedBase64;
        }

        /// <summary>The baked reference codes as "&lt;|speech_N|&gt;..." prompt text, cached.</summary>
        public string GetNeuttsCodesString()
        {
            if (_cachedCodesString != null && _cachedCodesFor == neuttsRefCodes)
                return _cachedCodesString;

            var sb = new System.Text.StringBuilder(neuttsRefCodes.Length * 14);
            foreach (int code in neuttsRefCodes)
                sb.Append("<|speech_").Append(code).Append("|>");
            _cachedCodesString = sb.ToString();
            _cachedCodesFor = neuttsRefCodes;
            return _cachedCodesString;
        }

        /// <summary>Phonemized sample transcript, cached (hit on every spoken sentence).</summary>
        public async System.Threading.Tasks.Task<string> GetNeuttsRefPhonesAsync(
            DynamicNpcSettings settings, System.Threading.CancellationToken cancellationToken)
        {
            if (_cachedRefPhones != null && _cachedRefPhonesFor == sampleTranscript)
                return _cachedRefPhones;

            string phones = await EspeakPhonemizer.PhonemizeAsync(
                settings, sampleTranscript, language, cancellationToken);
            _cachedRefPhones = phones;
            _cachedRefPhonesFor = sampleTranscript;
            return phones;
        }

        /// <summary>
        /// The speaker WAV path to send to the server, optionally mapped for WSL.
        /// </summary>
        public string GetSpeakerPath(bool mapToWsl)
        {
            string path = sampleWavPath.Trim();
            if (!mapToWsl)
                return path.Replace('\\', '/');

            // D:\foo\bar.wav -> /mnt/d/foo/bar.wav
            if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            {
                string rest = path.Substring(2).Replace('\\', '/').TrimStart('/');
                return $"/mnt/{char.ToLowerInvariant(path[0])}/{rest}";
            }
            return path.Replace('\\', '/');
        }
    }
}
