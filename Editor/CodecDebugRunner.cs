using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DynamicNpcs.Editor
{
    /// <summary>
    /// Standalone codec-decoder diagnostic: decodes the bundled dave starter voice's
    /// reference codes through the in-engine decoder and dumps numerical stats +
    /// a WAV next to the project's Assets folder. Runs from the menu, or headless:
    ///   Unity -batchmode -projectPath ... -executeMethod DynamicNpcs.Editor.CodecDebugRunner.Run -quit
    /// </summary>
    public static class CodecDebugRunner
    {
        private const string ModelAssetPath = "Assets/DynamicNPCs/neucodec-decoder.onnx";
        private const string DaveJsonPath = "Packages/com.mdj.dynamicnpcs/Editor/StarterVoices/dave.json";

        [Serializable]
        private class VoiceReferenceJson
        {
            public string name;
            public string transcript;
            public int[] codes;
        }

        [MenuItem("Window/Dynamic NPCs/Debug/Run Codec Decoder Diagnostic")]
        public static void Run()
        {
            var report = new StringBuilder();
            try
            {
                var modelAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ModelAssetPath);
                report.AppendLine($"model asset: {(modelAsset == null ? "NULL" : modelAsset.GetType().FullName)}");

                var json = File.ReadAllText(Path.GetFullPath(DaveJsonPath));
                var voice = JsonUtility.FromJson<VoiceReferenceJson>(json);
                report.AppendLine($"codes: {voice.codes.Length}");

                var watch = System.Diagnostics.Stopwatch.StartNew();
                using (var decoder = new SentisNeuCodecDecoder(modelAsset))
                {
                    float[] samples = decoder.Decode(voice.codes);
                    report.AppendLine($"decode: {watch.ElapsedMilliseconds} ms, {samples.Length} samples");
                    report.AppendLine(Stats("output", samples));

                    var clip = AudioClip.Create("codec_debug", samples.Length, 1, decoder.SampleRate, false);
                    clip.SetData(samples, 0);
                    string wavPath = Path.GetFullPath("codec_debug.wav");
                    File.WriteAllBytes(wavPath, WavUtility.FromAudioClip(clip));
                    report.AppendLine($"wav: {wavPath}");
                }
            }
            catch (Exception e)
            {
                report.AppendLine("EXCEPTION: " + e);
            }

            string reportPath = Path.GetFullPath("codec_debug.txt");
            File.WriteAllText(reportPath, report.ToString());
            Debug.Log("[DynamicNPCs] codec diagnostic:\n" + report);
            if (Application.isBatchMode || Environment.CommandLine.Contains("CodecDebugRunner"))
                EditorApplication.Exit(0);
        }

        [MenuItem("Window/Dynamic NPCs/Debug/Run Full TTS Diagnostic")]
        public static void RunSpeak()
        {
            RunSpeakAsync();
        }

        private static async void RunSpeakAsync()
        {
            var report = new StringBuilder();
            int exitCode = 0;
            try
            {
                var guids = AssetDatabase.FindAssets("t:DynamicNpcSettings");
                if (guids.Length == 0)
                    throw new Exception("no DynamicNpcSettings asset in project");
                var settings = AssetDatabase.LoadAssetAtPath<DynamicNpcSettings>(
                    AssetDatabase.GUIDToAssetPath(guids[0]));
                report.AppendLine($"settings: {AssetDatabase.GUIDToAssetPath(guids[0])}");
                report.AppendLine($"  ttsBackend={settings.ttsBackend} ttsModelPath={settings.ttsModelPath}");
                report.AppendLine($"  ttsPort={settings.ttsPort} ttsExtraServerArgs='{settings.ttsExtraServerArgs}'");
                report.AppendLine($"  espeakPath={settings.espeakPath}");
                report.AppendLine($"  neuCodecDecoder={(settings.neuCodecDecoder == null ? "NULL" : settings.neuCodecDecoder.GetType().Name)}");

                var dave = JsonUtility.FromJson<VoiceReferenceJson>(
                    File.ReadAllText(Path.GetFullPath(DaveJsonPath)));
                var voice = ScriptableObject.CreateInstance<NpcVoice>();
                voice.name = "dave-debug";
                voice.neuttsRefCodes = dave.codes;
                voice.sampleTranscript = dave.transcript;
                voice.language = "en";

                NeuTtsClient.DebugTap += (stage, value) =>
                {
                    string v = value ?? "(null)";
                    if (v.Length > 1500) v = v.Substring(0, 1500) + $"... [{value.Length} chars total]";
                    report.AppendLine($"[{stage}] {v}");
                };

                var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(150));
                report.AppendLine("ensuring TTS server...");
                await EmbeddedTtsServer.EnsureRunningAsync(settings, cts.Token);
                report.AppendLine("server healthy: " + settings.EmbeddedTtsRootUrl);

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var clip = await new NeuTtsClient().SynthesizeAsync(
                    settings, "Hello there! This is a test of the dynamic voice system.", voice, cts.Token);
                report.AppendLine($"synthesize: {watch.ElapsedMilliseconds} ms");

                var samples = new float[clip.samples];
                clip.GetData(samples, 0);
                report.AppendLine(Stats("clip", samples));
                string wavPath = Path.GetFullPath("speak_debug.wav");
                File.WriteAllBytes(wavPath, WavUtility.FromAudioClip(clip));
                report.AppendLine($"wav: {wavPath}");

                if (!Application.isBatchMode)
                {
                    // Audible proof: play the synthesized line through an AudioSource
                    // (same mechanism as in-game playback and the Test Console).
                    var go = new GameObject("dn_audio_probe") { hideFlags = HideFlags.HideAndDontSave };
                    try
                    {
                        go.AddComponent<AudioListener>();
                        var src = go.AddComponent<AudioSource>();
                        src.clip = clip;
                        src.spatialBlend = 0f;
                        src.Play();
                        report.AppendLine("playing through AudioSource...");
                        float waited = 0f;
                        while (src != null && src.isPlaying && waited < clip.length + 1f)
                        {
                            await System.Threading.Tasks.Task.Delay(250);
                            waited += 0.25f;
                        }
                        report.AppendLine($"playback finished after {waited:0.0}s (clip {clip.length:0.0}s)");
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(go);
                    }
                }
            }
            catch (Exception e)
            {
                report.AppendLine("EXCEPTION: " + e);
                exitCode = 1;
            }

            File.WriteAllText(Path.GetFullPath("speak_debug.txt"), report.ToString());
            Debug.Log("[DynamicNPCs] tts diagnostic:\n" + report);
            if (Application.isBatchMode || Environment.CommandLine.Contains("CodecDebugRunner"))
            {
                LlamaServerHost.ShutdownAll();
                EditorApplication.Exit(exitCode);
            }
        }

        [MenuItem("Window/Dynamic NPCs/Debug/Run Playback Test")]
        public static void RunPlayback()
        {
            RunPlaybackAsync();
        }

        private static async void RunPlaybackAsync()
        {
            var report = new StringBuilder();
            try
            {
                report.AppendLine($"batchMode={Application.isBatchMode} (audio is disabled in batch mode - run headful)");

                var audioUtil = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");
                report.AppendLine("AudioUtil found: " + (audioUtil != null));
                if (audioUtil != null)
                {
                    var flags = System.Reflection.BindingFlags.Static |
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic;
                    foreach (var m in audioUtil.GetMethods(flags))
                        if (m.Name.Contains("Preview") || m.Name.Contains("PlayClip"))
                            report.AppendLine($"  method: {m.Name}({string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name))})");
                }

                const int sr = 48000;
                var data = new float[sr * 3 / 2];
                for (int i = 0; i < data.Length; i++)
                    data[i] = Mathf.Sin(2f * Mathf.PI * 880f * i / sr) * 0.5f;
                var beep = AudioClip.Create("beep880", data.Length, 1, sr, false);
                beep.SetData(data, 0);

                var play = audioUtil?.GetMethod("PlayPreviewClip",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { typeof(AudioClip), typeof(int), typeof(bool) }, null);
                var isPlaying = audioUtil?.GetMethod("IsPreviewClipPlaying",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                var getPos = audioUtil?.GetMethod("GetPreviewClipPosition",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                report.AppendLine($"resolved: play={play != null} isPlaying={isPlaying != null} getPos={getPos != null}");

                var hasPreview = audioUtil?.GetMethod("HasPreview",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { typeof(AudioClip) }, null);
                report.AppendLine($"audioMasterMute={EditorUtility.audioMasterMute}");
                report.AppendLine($"HasPreview(procedural beep)={hasPreview?.Invoke(null, new object[] { beep }) ?? "?"}");

                if (play != null)
                {
                    report.AppendLine("--- A: PlayPreviewClip(procedural clip), HIGH 880Hz beep ---");
                    play.Invoke(null, new object[] { beep, 0, false });
                    for (int i = 0; i < 6; i++)
                    {
                        await System.Threading.Tasks.Task.Delay(250);
                        report.AppendLine($"  t+{(i + 1) * 250}ms playing={isPlaying?.Invoke(null, null) ?? "?"} pos={getPos?.Invoke(null, null) ?? "?"}");
                    }

                    var clipGuid = AssetDatabase.FindAssets("t:AudioClip");
                    if (clipGuid.Length > 0)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(clipGuid[0]);
                        var imported = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                        report.AppendLine($"--- B: PlayPreviewClip(imported asset: {path}) ---");
                        report.AppendLine($"HasPreview(imported)={hasPreview?.Invoke(null, new object[] { imported }) ?? "?"}");
                        play.Invoke(null, new object[] { imported, 0, false });
                        for (int i = 0; i < 6; i++)
                        {
                            await System.Threading.Tasks.Task.Delay(250);
                            report.AppendLine($"  t+{(i + 1) * 250}ms playing={isPlaying?.Invoke(null, null) ?? "?"} pos={getPos?.Invoke(null, null) ?? "?"}");
                        }
                        audioUtil.GetMethod("StopAllPreviewClips",
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                            null, Type.EmptyTypes, null)?.Invoke(null, null);
                    }
                    else
                        report.AppendLine("--- B: skipped, no imported AudioClip assets in project ---");
                }

                report.AppendLine("--- C: AudioSource.Play(procedural clip), LOW 440Hz beep ---");
                var low = new float[sr * 3 / 2];
                for (int i = 0; i < low.Length; i++)
                    low[i] = Mathf.Sin(2f * Mathf.PI * 440f * i / sr) * 0.5f;
                var beepLow = AudioClip.Create("beep440", low.Length, 1, sr, false);
                beepLow.SetData(low, 0);
                var go = new GameObject("dn_audio_probe") { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    if (UnityEngine.Object.FindFirstObjectByType<AudioListener>() == null)
                        go.AddComponent<AudioListener>();
                    var src = go.AddComponent<AudioSource>();
                    src.clip = beepLow;
                    src.spatialBlend = 0f;
                    src.volume = 1f;
                    src.Play();
                    for (int i = 0; i < 8; i++)
                    {
                        await System.Threading.Tasks.Task.Delay(250);
                        report.AppendLine($"  t+{(i + 1) * 250}ms isPlaying={src != null && src.isPlaying} time={(src != null ? src.time : -1f):0.00}");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            catch (Exception e)
            {
                report.AppendLine("EXCEPTION: " + e);
            }

            File.WriteAllText(Path.GetFullPath("playback_debug.txt"), report.ToString());
            Debug.Log("[DynamicNPCs] playback diagnostic:\n" + report);
            if (Application.isBatchMode || Environment.CommandLine.Contains("CodecDebugRunner"))
                EditorApplication.Exit(0);
        }

        private static string Stats(string label, float[] v)
        {
            int nan = 0, inf = 0, zero = 0;
            float min = float.MaxValue, max = float.MinValue, sumAbs = 0f;
            int firstNanAt = -1;
            for (int i = 0; i < v.Length; i++)
            {
                float x = v[i];
                if (float.IsNaN(x)) { nan++; if (firstNanAt < 0) firstNanAt = i; continue; }
                if (float.IsInfinity(x)) { inf++; if (firstNanAt < 0) firstNanAt = i; continue; }
                if (x == 0f) zero++;
                if (x < min) min = x;
                if (x > max) max = x;
                sumAbs += x < 0 ? -x : x;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"{label}: len={v.Length} nan={nan} inf={inf} zero={zero} firstBadAt={firstNanAt}");
            sb.AppendLine($"  min={min} max={max} meanAbs={(v.Length > 0 ? sumAbs / v.Length : 0)}");
            sb.Append("  head:");
            for (int i = 0; i < Math.Min(12, v.Length); i++)
                sb.Append(' ').Append(v[i].ToString("G6"));
            return sb.ToString();
        }
    }
}
