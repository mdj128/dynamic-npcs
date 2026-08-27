using System;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace DynamicNpcs.Editor
{
    /// <summary>
    /// Editor console for testing the LLM + TTS pipeline without entering play mode:
    /// check server connectivity, generate a line in character, and audition voices.
    /// </summary>
    public class DynamicNpcTestWindow : EditorWindow
    {
        private DynamicNpcSettings _settings;
        private NpcPersona _persona;
        private string _input = "Hello there! What's the news around here?";
        private string _lastResponse = "";
        private string _status = "Idle";
        private bool _busy;
        private Vector2 _scroll;
        private AudioClip _lastClip;

        private readonly LlmClient _llm = new LlmClient();
        private readonly TtsClient _tts = new TtsClient();
        private CancellationTokenSource _cts;

        [MenuItem("Window/Dynamic NPCs/Test Console")]
        private static void Open() => GetWindow<DynamicNpcTestWindow>("Dynamic NPCs");

        private void OnDisable()
        {
            _cts?.Cancel();
            if (_previewGo != null)
                DestroyImmediate(_previewGo);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            _settings = (DynamicNpcSettings)EditorGUILayout.ObjectField("Settings", _settings, typeof(DynamicNpcSettings), false);
            _persona = (NpcPersona)EditorGUILayout.ObjectField("Persona", _persona, typeof(NpcPersona), false);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Player says:");
            _input = EditorGUILayout.TextArea(_input, GUILayout.MinHeight(40));

            using (new EditorGUI.DisabledScope(_busy || _settings == null))
            {
                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Check Servers"))
                        _ = CheckServersAsync();

                    using (new EditorGUI.DisabledScope(_persona == null))
                    {
                        if (GUILayout.Button("Generate Text"))
                            _ = GenerateAsync(speak: false);
                        if (GUILayout.Button("Generate + Speak"))
                            _ = GenerateAsync(speak: true);
                        if (GUILayout.Button("Speak Input Directly"))
                            _ = SpeakAsync(_input);
                        if (GUILayout.Button("Measure Latency"))
                            _ = MeasureLatencyAsync();
                        if (_settings != null && _settings.UsesEmbeddedTts &&
                            GUILayout.Button("Test Codec Decoder"))
                            TestCodecDecoder();
                    }
                }
            }

            if (_busy && GUILayout.Button("Cancel"))
            {
                _cts?.Cancel();
                _busy = false;
                _status = "Cancelled";
            }

            if (_lastClip != null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Replay Last Clip"))
                        _status = PlayPreviewClip(_lastClip)
                            ? "Replaying: " + ClipStats(_lastClip)
                            : "Editor preview unavailable - save as WAV to listen.";
                    if (GUILayout.Button("Save Last Clip as WAV..."))
                        SaveLastClip();
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(_status, _busy ? MessageType.Info : MessageType.None);

            if (!string.IsNullOrEmpty(_lastResponse))
            {
                EditorGUILayout.LabelField("NPC reply:", EditorStyles.boldLabel);
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(60));
                EditorGUILayout.TextArea(_lastResponse, EditorStyles.wordWrappedLabel);
                EditorGUILayout.EndScrollView();
            }
        }

        private CancellationToken BeginOperation(string status)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _busy = true;
            _status = status;
            Repaint();
            return _cts.Token;
        }

        private void EndOperation(string status)
        {
            _busy = false;
            _status = status;
            Repaint();
        }

        private async System.Threading.Tasks.Task CheckServersAsync()
        {
            var ct = BeginOperation("Checking servers...");
            string llmResult, ttsResult;
            try
            {
                if (_settings.UsesEmbeddedLlm)
                    await EmbeddedLlmServer.EnsureRunningAsync(_settings, ct);
                await _llm.CheckAsync(_settings.ResolveLlmBaseUrl(), 10, ct);
                llmResult = _settings.UsesEmbeddedLlm ? "LLM (embedded): OK" : "LLM: OK";
            }
            catch (Exception e) { llmResult = $"LLM: FAILED - {e.Message}"; }
            try
            {
                if (_settings.UsesEmbeddedTts)
                {
                    await EmbeddedTtsServer.EnsureRunningAsync(_settings, ct);
                    ttsResult = "TTS (embedded NeuTTS): OK";
                }
                else
                {
                    string body = await _tts.CheckAsync(_settings, ct);
                    ttsResult = $"TTS: OK - {body}";
                }
            }
            catch (Exception e) { ttsResult = $"TTS: FAILED - {e.Message}"; }
            EndOperation($"{llmResult}\n{ttsResult}");
        }

        private async System.Threading.Tasks.Task GenerateAsync(bool speak)
        {
            var ct = BeginOperation("Generating...");
            try
            {
                if (_settings.UsesEmbeddedLlm)
                    await EmbeddedLlmServer.EnsureRunningAsync(_settings, ct);

                var request = new ChatRequest
                {
                    model = _persona.ResolveModel(_settings),
                    messages = new[]
                    {
                        new ChatMessage("system", _persona.BuildSystemPrompt()),
                        new ChatMessage("user", _input),
                    },
                    temperature = _persona.temperature,
                    max_tokens = _persona.maxTokens,
                };

                string reply = await _llm.ChatAsync(_settings.ResolveLlmBaseUrl(), request, _settings.llmTimeoutSeconds, ct);
                _lastResponse = reply;

                if (speak)
                {
                    _status = "Synthesizing speech...";
                    Repaint();
                    var clip = await SpeechSynthesizer.SynthesizeAsync(_settings, reply, _persona.voice, ct);
                    _lastClip = clip;
                    bool played = PlayPreviewClip(clip);
                    EndOperation((played ? "Playing: " : "Synthesized (editor preview unavailable): ") + ClipStats(clip));
                    return;
                }
                EndOperation("Done.");
            }
            catch (OperationCanceledException) { EndOperation("Cancelled."); }
            catch (Exception e) { EndOperation($"Error: {e.Message}"); }
        }

        private async System.Threading.Tasks.Task SpeakAsync(string text)
        {
            var ct = BeginOperation("Synthesizing speech...");
            try
            {
                var clip = await SpeechSynthesizer.SynthesizeAsync(_settings, text, _persona.voice, ct);
                _lastClip = clip;
                bool played = PlayPreviewClip(clip);
                EndOperation((played ? "Playing: " : "Synthesized (editor preview unavailable): ") + ClipStats(clip));
            }
            catch (OperationCanceledException) { EndOperation("Cancelled."); }
            catch (Exception e) { EndOperation($"Error: {e.Message}"); }
        }

        /// <summary>
        /// Measures the realistic time-to-first-word for this persona: streams the LLM,
        /// times the first token and first complete sentence, then times TTS on that
        /// sentence. In the runtime pipeline TTS overlaps generation, so
        /// "first sentence + TTS" is what the player actually waits.
        /// </summary>
        private async System.Threading.Tasks.Task MeasureLatencyAsync()
        {
            var ct = BeginOperation("Measuring latency...");
            try
            {
                if (_settings.UsesEmbeddedLlm)
                {
                    _status = "Ensuring embedded server is running (excluded from measurement)...";
                    Repaint();
                    await EmbeddedLlmServer.EnsureRunningAsync(_settings, ct);
                }

                var request = new ChatRequest
                {
                    model = _persona.ResolveModel(_settings),
                    messages = new[]
                    {
                        new ChatMessage("system", _persona.BuildSystemPrompt()),
                        new ChatMessage("user", _input),
                    },
                    temperature = _persona.temperature,
                    max_tokens = _persona.maxTokens,
                };

                var chunker = new SentenceChunker(_settings.minChunkChars);
                string firstSentence = null;
                long firstTokenMs = 0, firstSentenceMs = 0;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                string reply = await _llm.StreamChatAsync(
                    _settings.ResolveLlmBaseUrl(), request,
                    delta =>
                    {
                        if (firstTokenMs == 0) firstTokenMs = sw.ElapsedMilliseconds;
                        if (firstSentence == null)
                        {
                            var done = chunker.Feed(delta);
                            if (done.Count > 0)
                            {
                                firstSentence = done[0];
                                firstSentenceMs = sw.ElapsedMilliseconds;
                            }
                        }
                    },
                    _settings.llmTimeoutSeconds, ct);
                long fullReplyMs = sw.ElapsedMilliseconds;

                if (firstSentence == null)
                {
                    firstSentence = chunker.Flush();
                    firstSentenceMs = fullReplyMs;
                }
                if (string.IsNullOrEmpty(firstSentence))
                    throw new Exception("LLM returned no text.");

                _lastResponse = reply;
                _status = "Synthesizing first sentence...";
                Repaint();

                var ttsWatch = System.Diagnostics.Stopwatch.StartNew();
                var clip = await SpeechSynthesizer.SynthesizeAsync(_settings, firstSentence, _persona.voice, ct);
                long ttsMs = ttsWatch.ElapsedMilliseconds;

                _lastClip = clip;
                PlayPreviewClip(clip);
                EndOperation(
                    $"First token: {firstTokenMs} ms\n" +
                    $"First sentence generated: {firstSentenceMs} ms\n" +
                    $"TTS of first sentence: {ttsMs} ms\n" +
                    $"=> Time-to-first-word: ~{firstSentenceMs + ttsMs} ms\n" +
                    $"(full reply finished generating at {fullReplyMs} ms; later sentences synthesize during playback)");
            }
            catch (OperationCanceledException) { EndOperation("Cancelled."); }
            catch (Exception e) { EndOperation($"Error: {e.Message}"); }
        }

        /// <summary>
        /// Decodes the persona voice's baked reference codes directly - no espeak, no
        /// llama-server. The result should sound exactly like the voice's original
        /// sample; silence here means the in-engine codec decoder is at fault.
        /// </summary>
        private void TestCodecDecoder()
        {
            try
            {
                var voice = _persona.voice;
                if (voice == null || !voice.HasNeuttsReference)
                {
                    _status = "Persona's voice has no baked NeuTTS reference codes.";
                    return;
                }
                int min = int.MaxValue, max = int.MinValue;
                foreach (int c in voice.neuttsRefCodes)
                {
                    if (c < min) min = c;
                    if (c > max) max = c;
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                _lastClip = NeuTtsClient.DecodeCodesToClip(
                    _settings, voice.neuttsRefCodes, $"codec_test_{voice.name}");
                long ms = watch.ElapsedMilliseconds;
                bool played = PlayPreviewClip(_lastClip);
                _status =
                    $"Decoded {voice.neuttsRefCodes.Length} baked codes (values {min}..{max}) in {ms} ms\n" +
                    (played ? "Playing: " : "Editor preview unavailable: ") + ClipStats(_lastClip) + "\n" +
                    "This should sound like the voice's original sample.";
            }
            catch (Exception e)
            {
                _status = "Error: " + e.Message;
            }
            Repaint();
        }

        private const string PreviewObjectName = "DynamicNpcs Audio Preview";
        private GameObject _previewGo;
        private AudioSource _previewSource;

        /// <summary>
        /// Plays a clip in edit mode through a hidden AudioSource. UnityEditor.AudioUtil's
        /// PlayPreviewClip cannot be used here: since Unity 6 it only plays clips with
        /// imported preview data, and silently ignores procedurally created clips
        /// (AudioClip.Create) - which is every clip TTS produces.
        /// </summary>
        private bool PlayPreviewClip(AudioClip clip)
        {
            if (clip == null)
                return false;

            if (_previewSource == null)
            {
                // Domain reloads reset these fields but HideAndDontSave objects survive:
                // reuse a leftover probe instead of stacking hidden AudioListeners.
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go.name == PreviewObjectName && (go.hideFlags & HideFlags.HideAndDontSave) != 0)
                    {
                        _previewGo = go;
                        break;
                    }
                }
                if (_previewGo == null)
                    _previewGo = new GameObject(PreviewObjectName) { hideFlags = HideFlags.HideAndDontSave };

#if UNITY_2023_1_OR_NEWER
                bool hasListener = FindFirstObjectByType<AudioListener>() != null;
#else
                bool hasListener = FindObjectOfType<AudioListener>() != null;
#endif
                if (!hasListener && _previewGo.GetComponent<AudioListener>() == null)
                    _previewGo.AddComponent<AudioListener>();

                _previewSource = _previewGo.GetComponent<AudioSource>();
                if (_previewSource == null)
                    _previewSource = _previewGo.AddComponent<AudioSource>();
                _previewSource.spatialBlend = 0f;
                _previewSource.volume = 1f;
            }

            _previewSource.Stop();
            _previewSource.clip = clip;
            _previewSource.Play();
            return true;
        }

        /// <summary>Duration/sample/peak summary; a near-zero peak means the decoder produced silence.</summary>
        private static string ClipStats(AudioClip clip)
        {
            if (clip == null)
                return "no clip";
            var data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);
            float peak = 0f;
            int nan = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if (float.IsNaN(data[i]) || float.IsInfinity(data[i])) { nan++; continue; }
                float a = data[i] < 0 ? -data[i] : data[i];
                if (a > peak) peak = a;
            }
            return $"{clip.length:0.00}s, {clip.samples} samples @ {clip.frequency} Hz, peak {peak:0.000}" +
                   (nan > 0 ? $", {nan} NaN/Inf samples (numerical blow-up in the decoder)"
                    : peak < 0.001f ? " (SILENT - decoder output is all zeros)" : "");
        }

        private void SaveLastClip()
        {
            if (_lastClip == null)
                return;
            string path = EditorUtility.SaveFilePanel("Save clip as WAV", "", "npc-tts.wav", "wav");
            if (string.IsNullOrEmpty(path))
                return;
            System.IO.File.WriteAllBytes(path, WavUtility.FromAudioClip(_lastClip));
            _status = "Saved " + path;
        }
    }
}
