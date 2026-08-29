using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace DynamicNpcs.Editor
{
    /// <summary>Kills the embedded llama-server when the editor itself quits.</summary>
    [InitializeOnLoad]
    internal static class EmbeddedServerEditorLifecycle
    {
        static EmbeddedServerEditorLifecycle()
        {
            EditorApplication.quitting -= LlamaServerHost.ShutdownAll;
            EditorApplication.quitting += LlamaServerHost.ShutdownAll;
        }
    }

    /// <summary>
    /// One-stop setup for the embedded LLM backend: download llama.cpp server
    /// binaries into StreamingAssets, pick a GGUF model, and start/stop/benchmark
    /// the server.
    /// </summary>
    public class EmbeddedServerSetupWindow : EditorWindow
    {
        private const string ReleaseApiUrl = "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest";
        private const string InstallDirRelative = "DynamicNPCs/llama-server";
        private const string ModelsDirRelative = "DynamicNPCs/models";

        private const string EspeakDirRelative = "DynamicNPCs/espeak-ng";
        private const string EspeakMsiUrl = "https://github.com/espeak-ng/espeak-ng/releases/download/1.52.0/espeak-ng.msi";
        private const string CodecAssetPath = "Assets/DynamicNPCs/neucodec-decoder.onnx";
        // Neuphonic's decoder, Apache-2.0, with the SplitToSequence/SequenceAt pairs
        // rewritten to Gather so Unity's ONNX importer accepts it. Mirrored on this
        // package's releases because the upstream repo is gated and the upstream file
        // needs patching before Unity can load it either way.
        private const string CodecUrl = "https://github.com/mdj128/dynamic-npcs/releases/download/codec-v1/neucodec-decoder-unity.onnx";
        private const long CodecSizeBytes = 782465015L;
        private const string CodecSha256 = "d1147333ee3ef2fae0ad82a0530a8ee0f06ea67418f1d8bbf5046e73f4505659";
        private const string CodecReleasePage = "https://github.com/mdj128/dynamic-npcs/releases/tag/codec-v1";
        private const string CodecUpstreamUrl = "https://huggingface.co/neuphonic/neucodec-onnx-decoder/resolve/main/model.onnx";
        private const string CodecRepoPage = "https://huggingface.co/neuphonic/neucodec-onnx-decoder";
        private const string HfTokensPage = "https://huggingface.co/settings/tokens";
        // Per-machine, per-user. Deliberately NOT stored on the settings asset, which
        // would put the token in source control.
        private const string HfTokenPrefKey = "DynamicNpcs.HuggingFaceToken";
        private const string NeuttsGgufPage = "https://huggingface.co/neuphonic/neutts-air-q4-gguf/tree/main";
        // Neuphonic's NeuTTS Air Q4_0 backbone, Apache-2.0, mirrored unmodified so setup
        // needs no Hugging Face account (that repo is gated too).
        private const string TtsGgufUrl = "https://github.com/mdj128/dynamic-npcs/releases/download/codec-v1/neutts-air-Q4_0.gguf";
        private const string TtsGgufSha256 = "bf66dc21b7588fe720cbdfeac1595e7b7c780515f8d8f1ff9a29062e4ac9119e";
        private const string TtsGgufRelative = "DynamicNPCs/models/neutts-air-Q4_0.gguf";
        private const string StarterAssetsFolder = "Assets/DynamicNPCs";
        private const string DaveStarterJson = "Packages/com.mdj.dynamicnpcs/Editor/StarterVoices/dave.json";

        private DynamicNpcSettings _settings;
        private string _status = "Idle";
        private bool _busy;
        private Vector2 _logScroll;
        private Vector2 _scroll;

        private GitHubRelease _release;
        private GitHubAsset[] _binaryAssets = Array.Empty<GitHubAsset>();
        private string[] _binaryAssetLabels = Array.Empty<string>();
        private int _selectedAsset;

        private readonly LlmClient _llm = new LlmClient();
        private CancellationTokenSource _cts;

        private string _hfToken = "";
        private bool _codecUseUpstream;

        // Memo for the on-disk codec check, keyed on path/size/mtime so OnGUI does not
        // reopen a ~750 MB file every repaint.
        private string _codecCheckedPath;
        private long _codecCheckedLength;
        private DateTime _codecCheckedTime;
        private bool _codecCheckedResult;
        private string _codecCheckedReason;

        [MenuItem("Window/Dynamic NPCs/Embedded Server Setup")]
        private static void Open() => GetWindow<EmbeddedServerSetupWindow>("Embedded LLM Setup");

        private void OnEnable() => _hfToken = EditorPrefs.GetString(HfTokenPrefKey, "");

        private void OnDisable() => _cts?.Cancel();

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            _settings = (DynamicNpcSettings)EditorGUILayout.ObjectField("Settings", _settings, typeof(DynamicNpcSettings), false);
            if (_settings == null)
            {
                EditorGUILayout.HelpBox(
                    "No settings assigned. 'Create Starter Assets' makes a settings asset, a voice " +
                    "using the bundled 'dave' reference, and a persona wired to it - everything an " +
                    "NPC Dialogue Agent needs.",
                    MessageType.Info);
                if (GUILayout.Button("Create Starter Assets"))
                    CreateStarterAssets();
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            using (new EditorGUI.DisabledScope(_busy))
            {
                DrawQuickSetupSection();
                DrawBinarySection();
                DrawModelSection();
                DrawBackendSection();
            }
            DrawServerSection();
            using (new EditorGUI.DisabledScope(_busy))
                DrawTtsSection();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(_status, _busy ? MessageType.Info : MessageType.None);
        }

        // --- 0. quick setup ---

        /// <summary>
        /// A single checklist of everything the embedded stack needs, so the state of a
        /// half-finished setup is visible at a glance rather than spread over four sections.
        /// </summary>
        private void DrawQuickSetupSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("0. Quick setup", EditorStyles.boldLabel);

            bool haveServer = File.Exists(DynamicNpcPaths.ResolveExecutable(_settings.llamaServerPath) ?? "");
            bool haveChatModel = !string.IsNullOrWhiteSpace(_settings.llmModelPath) &&
                                 File.Exists(DynamicNpcPaths.Resolve(_settings.llmModelPath) ?? "");
            bool haveTtsModel = !string.IsNullOrWhiteSpace(_settings.ttsModelPath) &&
                                File.Exists(DynamicNpcPaths.Resolve(_settings.ttsModelPath) ?? "");
            bool haveEspeak = File.Exists(DynamicNpcPaths.ResolveExecutable(_settings.espeakPath) ?? "");
            bool haveInference =
                Type.GetType("Unity.InferenceEngine.ModelAsset, Unity.InferenceEngine") != null ||
                Type.GetType("Unity.Sentis.ModelAsset, Unity.Sentis") != null;
            bool haveCodec = _settings.neuCodecDecoder != null && !(_settings.neuCodecDecoder is DefaultAsset);

            Row("llama-server binary", haveServer, "section 1");
            Row("Dialogue GGUF (yours)", haveChatModel, "section 2");
            Row("Inference Engine package", haveInference, "section 4");
            Row("NeuTTS backbone GGUF", haveTtsModel, "section 4");
            Row("espeak-ng", haveEspeak, "section 4");
            Row("NeuCodec decoder", haveCodec, "section 4");

            int done = (haveServer ? 1 : 0) + (haveChatModel ? 1 : 0) + (haveInference ? 1 : 0) +
                       (haveTtsModel ? 1 : 0) + (haveEspeak ? 1 : 0) + (haveCodec ? 1 : 0);
            if (done == 6)
                EditorGUILayout.HelpBox("Everything is in place. Start the servers below, or use the Test Console.", MessageType.Info);
            else if (!haveInference)
                EditorGUILayout.HelpBox(
                    "Install the Inference Engine package first (section 4). It recompiles the project, " +
                    "which would interrupt the other downloads.",
                    MessageType.Warning);
            else
                EditorGUILayout.HelpBox(
                    "'Download Everything Missing' fetches the llama-server binary, the NeuTTS backbone, " +
                    "espeak-ng and the codec decoder - roughly 1.5 GB in total. The dialogue GGUF is " +
                    "yours to choose and is not downloaded.",
                    MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!haveInference || done == 6))
                    if (GUILayout.Button("Download Everything Missing"))
                        _ = RunQuickSetupAsync();
                if (GUILayout.Button("Create Starter Assets"))
                    CreateStarterAssets();
            }

            void Row(string label, bool ok, string where)
            {
                EditorGUILayout.LabelField(
                    (ok ? "\u2713  " : "\u2717  ") + label,
                    ok ? "ready" : "missing - " + where);
            }
        }

        /// <summary>
        /// Runs the download steps back to back. Deliberately excludes installing the
        /// Inference Engine package: that triggers a domain reload, which would tear down
        /// this async chain part-way through.
        /// </summary>
        private async Task RunQuickSetupAsync()
        {
            try
            {
                if (!File.Exists(DynamicNpcPaths.ResolveExecutable(_settings.llamaServerPath) ?? ""))
                {
                    await FetchReleaseAsync();
                    if (_binaryAssets.Length == 0)
                        throw new Exception("could not list llama.cpp builds - use section 1 manually");
                    await DownloadBinaryAsync(_binaryAssets[Mathf.Clamp(_selectedAsset, 0, _binaryAssets.Length - 1)]);
                }

                if (string.IsNullOrWhiteSpace(_settings.ttsModelPath) ||
                    !File.Exists(DynamicNpcPaths.Resolve(_settings.ttsModelPath) ?? ""))
                    await DownloadTtsGgufAsync();

#if UNITY_EDITOR_WIN
                if (!File.Exists(DynamicNpcPaths.ResolveExecutable(_settings.espeakPath) ?? ""))
                    await InstallEspeakAsync();
#endif

                if (_settings.neuCodecDecoder == null || _settings.neuCodecDecoder is DefaultAsset)
                    await DownloadCodecAsync();

                End("Quick setup finished. Pick a dialogue GGUF in section 2 if you have not yet.");
            }
            catch (Exception e) { End("Quick setup stopped: " + e.Message); }
        }

        /// <summary>
        /// Creates the three assets an NPC needs, already wired together: settings (embedded
        /// backends), a voice carrying the bundled 'dave' reference codes, and a persona.
        /// </summary>
        private void CreateStarterAssets()
        {
            if (!AssetDatabase.IsValidFolder(StarterAssetsFolder))
                AssetDatabase.CreateFolder("Assets", "DynamicNPCs");

            var settings = _settings;
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<DynamicNpcSettings>();
                AssetDatabase.CreateAsset(settings, AssetDatabase.GenerateUniqueAssetPath(
                    StarterAssetsFolder + "/DynamicNpcSettings.asset"));
                _settings = settings;
            }

            var voice = ScriptableObject.CreateInstance<NpcVoice>();
            voice.displayName = "Dave";
            voice.language = "en";
            string davePath = Path.GetFullPath(DaveStarterJson);
            if (File.Exists(davePath))
            {
                var data = JsonUtility.FromJson<StarterVoiceJson>(File.ReadAllText(davePath));
                if (data?.codes != null)
                {
                    voice.neuttsRefCodes = data.codes;
                    voice.sampleTranscript = (data.transcript ?? "").Trim();
                }
            }
            AssetDatabase.CreateAsset(voice, AssetDatabase.GenerateUniqueAssetPath(
                StarterAssetsFolder + "/DaveVoice.asset"));

            var persona = ScriptableObject.CreateInstance<NpcPersona>();
            persona.npcName = "Villager";
            persona.voice = voice;
            AssetDatabase.CreateAsset(persona, AssetDatabase.GenerateUniqueAssetPath(
                StarterAssetsFolder + "/VillagerPersona.asset"));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorGUIUtility.PingObject(persona);
            Selection.activeObject = persona;

            _status = voice.HasNeuttsReference
                ? $"Created settings, voice and persona in {StarterAssetsFolder}. Add an NPC Dialogue Agent to a GameObject and assign them."
                : $"Created assets in {StarterAssetsFolder}, but the bundled 'dave' reference was not found - apply a starter voice in the voice inspector.";
        }

        [Serializable]
        private class StarterVoiceJson
        {
            public string name;
            public string transcript;
            public int[] codes;
        }

        // --- 1. server binary ---

        private void DrawBinarySection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("1. Server binary", EditorStyles.boldLabel);

            string exe = DynamicNpcPaths.ResolveExecutable(_settings.llamaServerPath);
            bool exists = !string.IsNullOrEmpty(exe) && File.Exists(exe);
            EditorGUILayout.LabelField("Path", _settings.llamaServerPath);
            EditorGUILayout.LabelField("Found", exists ? "Yes" : "No - download below or fix the path");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fetch Available Builds"))
                    _ = FetchReleaseAsync();

                if (_binaryAssets.Length > 0)
                {
                    _selectedAsset = EditorGUILayout.Popup(_selectedAsset, _binaryAssetLabels);
                    if (GUILayout.Button("Download", GUILayout.Width(90)))
                        _ = DownloadBinaryAsync(_binaryAssets[Mathf.Clamp(_selectedAsset, 0, _binaryAssets.Length - 1)]);
                }
            }

            if (_release != null && _binaryAssets.Length > 0)
                EditorGUILayout.HelpBox(
                    $"llama.cpp {_release.tag_name}. Vulkan builds work on NVIDIA/AMD/Intel GPUs with no extra runtime and are the easiest choice. " +
                    "CUDA builds are fastest on NVIDIA (the matching cudart archive is downloaded automatically). CPU builds need no GPU.",
                    MessageType.None);
        }

        private async Task FetchReleaseAsync()
        {
            Begin("Fetching latest llama.cpp release...");
            try
            {
                using (var req = UnityWebRequest.Get(ReleaseApiUrl))
                {
                    req.SetRequestHeader("User-Agent", "DynamicNPCs-Unity");
                    req.timeout = 30;
                    await req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                        throw new Exception($"GitHub API request failed: {req.error}");
                    _release = JsonUtility.FromJson<GitHubRelease>(req.downloadHandler.text);
                }

                _binaryAssets = (_release.assets ?? Array.Empty<GitHubAsset>())
                    .Where(IsBinaryForCurrentPlatform)
                    .OrderBy(a => AssetSortRank(a.name))
                    .ToArray();
                _binaryAssetLabels = _binaryAssets
                    .Select(a => $"{a.name} ({a.size / (1024f * 1024f):0} MB)")
                    .ToArray();
                _selectedAsset = 0;

                End(_binaryAssets.Length > 0
                    ? $"Found {_binaryAssets.Length} builds for this platform in {_release.tag_name}."
                    : "No matching builds found for this platform.");
            }
            catch (Exception e) { End("Error: " + e.Message); }
        }

        private static bool IsBinaryForCurrentPlatform(GitHubAsset a)
        {
            string n = a.name.ToLowerInvariant();
            if (!n.EndsWith(".zip") || n.Contains("cudart"))
                return false;
#if UNITY_EDITOR_WIN
            return n.Contains("bin-win") && n.Contains("x64") && !n.Contains("arm64");
#elif UNITY_EDITOR_OSX
            return n.Contains("bin-macos");
#else
            return n.Contains("bin-ubuntu");
#endif
        }

        private static int AssetSortRank(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("vulkan")) return 0; // recommended default first
            if (n.Contains("cuda")) return 1;
            if (n.Contains("cpu")) return 2;
            return 3;
        }

        private async Task DownloadBinaryAsync(GitHubAsset asset)
        {
            Begin($"Downloading {asset.name}...");
            try
            {
                string installDir = Path.Combine(Application.streamingAssetsPath, InstallDirRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(installDir);

                await DownloadAndExtractAsync(asset, installDir);

                // CUDA builds need the matching cudart runtime archive as well.
                string lower = asset.name.ToLowerInvariant();
                if (lower.Contains("cuda") && _release?.assets != null)
                {
                    var cudart = _release.assets.FirstOrDefault(x =>
                        x.name.ToLowerInvariant().Contains("cudart") &&
                        x.name.ToLowerInvariant().EndsWith(".zip"));
                    if (cudart != null)
                    {
                        _status = $"Downloading {cudart.name}...";
                        Repaint();
                        await DownloadAndExtractAsync(cudart, installDir);
                    }
                }

                string exe = FindServerExecutable(installDir);
                if (exe == null)
                    throw new Exception("Archive extracted, but no llama-server executable was found inside it.");

                string relative = InstallDirRelative + exe.Substring(installDir.Length).Replace('\\', '/');
                _settings.llamaServerPath = relative;
                EditorUtility.SetDirty(_settings);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                End($"Installed. Server path set to StreamingAssets/{relative}");
            }
            catch (Exception e) { End("Error: " + e.Message); }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private async Task DownloadAndExtractAsync(GitHubAsset asset, string installDir)
        {
            string zipPath = Path.Combine(Path.GetTempPath(), asset.name);
            using (var req = new UnityWebRequest(asset.browser_download_url, UnityWebRequest.kHttpVerbGET))
            {
                req.downloadHandler = new DownloadHandlerFile(zipPath);
                req.SetRequestHeader("User-Agent", "DynamicNPCs-Unity");
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    EditorUtility.DisplayProgressBar("Dynamic NPCs", $"Downloading {asset.name}", req.downloadProgress);
                    await Task.Yield();
                }
                if (req.result != UnityWebRequest.Result.Success)
                    throw new Exception($"Download failed: {req.error}");
            }

            EditorUtility.DisplayProgressBar("Dynamic NPCs", $"Extracting {asset.name}", 1f);
            string installDirFull = Path.GetFullPath(installDir);
            using (var stream = File.OpenRead(zipPath))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) // directory entry
                        continue;
                    string dest = Path.GetFullPath(Path.Combine(installDirFull, entry.FullName));
                    if (!dest.StartsWith(installDirFull, StringComparison.OrdinalIgnoreCase))
                        continue; // zip-slip guard
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using (var src = entry.Open())
                    using (var dst = File.Create(dest))
                        src.CopyTo(dst);
                }
            }
            File.Delete(zipPath);
        }

        private static string FindServerExecutable(string dir)
        {
            var candidates = Directory.GetFiles(dir, "llama-server*", SearchOption.AllDirectories);
            return candidates.FirstOrDefault(f =>
            {
                string name = Path.GetFileName(f).ToLowerInvariant();
                return name == "llama-server.exe" || name == "llama-server";
            });
        }

        // --- 2. model ---

        private void DrawModelSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("2. GGUF model", EditorStyles.boldLabel);

            string model = DynamicNpcPaths.Resolve(_settings.llmModelPath);
            bool exists = !string.IsNullOrWhiteSpace(model) && File.Exists(model);
            EditorGUILayout.LabelField("Path", string.IsNullOrWhiteSpace(_settings.llmModelPath) ? "(not set)" : _settings.llmModelPath);
            if (exists)
                EditorGUILayout.LabelField("Size", $"{new FileInfo(model).Length / (1024f * 1024f * 1024f):0.00} GB");
            else
                EditorGUILayout.LabelField("Found", "No");

            if (exists && Path.IsPathRooted(_settings.llmModelPath))
                EditorGUILayout.HelpBox(
                    "Absolute path: fine for development, but the model will NOT be included in builds. " +
                    "Before shipping, copy it under StreamingAssets and use a relative path.",
                    MessageType.Warning);

            if (GUILayout.Button("Browse for .gguf..."))
                PickModel();
        }

        private void PickModel() => PickGguf("Choose a GGUF chat model", p => _settings.llmModelPath = p);

        private void PickGguf(string title, Action<string> assign)
        {
            string picked = EditorUtility.OpenFilePanel(title, "", "gguf");
            if (string.IsNullOrEmpty(picked))
                return;

            int choice = EditorUtility.DisplayDialogComplex(
                "Use model",
                "Reference the file where it is (dev only, not included in builds), or copy it into StreamingAssets so it ships with the game?",
                "Reference in place", "Cancel", "Copy into StreamingAssets");

            if (choice == 1)
                return;

            if (choice == 2)
            {
                string modelsDir = Path.Combine(Application.streamingAssetsPath, ModelsDirRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(modelsDir);
                string dest = Path.Combine(modelsDir, Path.GetFileName(picked));
                FileUtil.ReplaceFile(picked, dest);
                AssetDatabase.Refresh();
                assign(ModelsDirRelative + "/" + Path.GetFileName(picked));
            }
            else
            {
                assign(picked);
            }

            EditorUtility.SetDirty(_settings);
            AssetDatabase.SaveAssets();
        }

        // --- 3. backend mode ---

        private void DrawBackendSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("3. Backend", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Current mode", _settings.llmBackend.ToString());
            if (_settings.llmBackend != LlmBackendMode.EmbeddedLlamaServer &&
                GUILayout.Button("Switch settings to Embedded backend"))
            {
                _settings.llmBackend = LlmBackendMode.EmbeddedLlamaServer;
                EditorUtility.SetDirty(_settings);
                AssetDatabase.SaveAssets();
            }
        }

        // --- 4. server control ---

        private void DrawServerSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("4. Server", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Status", EmbeddedLlmServer.IsRunning ? "Running" : "Stopped");
            EditorGUILayout.LabelField("URL", _settings.EmbeddedServerRootUrl);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_busy))
                {
                    if (GUILayout.Button("Start / Ensure Running"))
                        _ = StartServerAsync();
                    if (GUILayout.Button("Stop"))
                    {
                        EmbeddedLlmServer.Shutdown();
                        _status = "Server stopped.";
                    }
                    if (GUILayout.Button("Benchmark"))
                        _ = BenchmarkAsync();
                }
                if (_busy && GUILayout.Button("Cancel"))
                {
                    _cts?.Cancel();
                    _busy = false;
                    _status = "Cancelled";
                }
            }

            string log = EmbeddedLlmServer.LogText;
            if (!string.IsNullOrEmpty(log))
            {
                EditorGUILayout.LabelField("Server log", EditorStyles.boldLabel);
                _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.MinHeight(120));
                EditorGUILayout.TextArea(log, EditorStyles.miniLabel);
                EditorGUILayout.EndScrollView();
            }
        }

        private async Task StartServerAsync()
        {
            Begin("Starting llama-server (first start loads the model - can take a while)...");
            try
            {
                _cts = new CancellationTokenSource();
                await EmbeddedLlmServer.EnsureRunningAsync(_settings, _cts.Token);
                End("Server is running and healthy.");
            }
            catch (OperationCanceledException) { End("Cancelled."); }
            catch (Exception e) { End("Error: " + e.Message); }
        }

        private async Task BenchmarkAsync()
        {
            Begin("Benchmarking...");
            try
            {
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;

                var startupWatch = System.Diagnostics.Stopwatch.StartNew();
                await EmbeddedLlmServer.EnsureRunningAsync(_settings, ct);
                long startupMs = startupWatch.ElapsedMilliseconds;

                var request = new ChatRequest
                {
                    model = _settings.llmModel,
                    messages = new[]
                    {
                        new ChatMessage("system", "You are a villager NPC in a fantasy game. Reply with 2-3 short spoken sentences only."),
                        new ChatMessage("user", "Have you seen anything strange in the woods lately?"),
                    },
                    temperature = 0.8f,
                    max_tokens = 120,
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                long firstTokenMs = 0;
                int chars = 0;
                string reply = await _llm.StreamChatAsync(
                    _settings.ResolveLlmBaseUrl(), request,
                    delta =>
                    {
                        if (firstTokenMs == 0) firstTokenMs = sw.ElapsedMilliseconds;
                        chars += delta.Length;
                    },
                    _settings.llmTimeoutSeconds, ct);
                long totalMs = sw.ElapsedMilliseconds;

                float estTokens = chars / 4f;
                float tokPerSec = totalMs > firstTokenMs ? estTokens / ((totalMs - firstTokenMs) / 1000f) : 0;

                End($"Server ready check: {startupMs} ms{(startupMs > 1000 ? " (includes model load)" : "")}\n" +
                    $"First token: {firstTokenMs} ms\n" +
                    $"Full reply ({chars} chars): {totalMs} ms  (~{tokPerSec:0} tok/s, estimated)\n\n" +
                    $"Reply: {reply}");
            }
            catch (OperationCanceledException) { End("Cancelled."); }
            catch (Exception e) { End("Error: " + e.Message); }
        }

        // --- 5. embedded TTS (NeuTTS) ---

        private void DrawTtsSection()
        {
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("5. Embedded TTS (NeuTTS)", EditorStyles.boldLabel);

            EditorGUILayout.LabelField("Current mode", _settings.ttsBackend.ToString());
            if (_settings.ttsBackend != TtsBackendMode.EmbeddedNeuTts &&
                GUILayout.Button("Switch settings to Embedded NeuTTS backend"))
            {
                _settings.ttsBackend = TtsBackendMode.EmbeddedNeuTts;
                EditorUtility.SetDirty(_settings);
                AssetDatabase.SaveAssets();
            }

            // a) backbone model
            string ttsModel = DynamicNpcPaths.Resolve(_settings.ttsModelPath);
            bool ttsModelExists = !string.IsNullOrWhiteSpace(ttsModel) && File.Exists(ttsModel);
            EditorGUILayout.LabelField("NeuTTS GGUF", string.IsNullOrWhiteSpace(_settings.ttsModelPath) ? "(not set)" : _settings.ttsModelPath + (ttsModelExists ? "" : "  [missing]"));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (!ttsModelExists && GUILayout.Button("Download NeuTTS Backbone (503 MB)"))
                    _ = DownloadTtsGgufAsync();
                if (GUILayout.Button("Browse NeuTTS .gguf..."))
                    PickGguf("Choose the NeuTTS backbone GGUF", p => _settings.ttsModelPath = p);
                if (GUILayout.Button("Open Upstream Page"))
                    Application.OpenURL(NeuttsGgufPage);
            }
            if (!ttsModelExists)
                EditorGUILayout.HelpBox(
                    "Downloads Neuphonic's NeuTTS Air Q4_0 backbone (Apache-2.0) into StreamingAssets " +
                    "so it ships with your build. Mirrored on this package's releases - no Hugging " +
                    "Face account needed - and checksum-verified.",
                    MessageType.None);

            // b) phonemizer
            string espeak = DynamicNpcPaths.ResolveExecutable(_settings.espeakPath);
            bool espeakExists = !string.IsNullOrWhiteSpace(espeak) && File.Exists(espeak);
            EditorGUILayout.LabelField("espeak-ng", espeakExists ? _settings.espeakPath : $"{_settings.espeakPath}  [missing]");
#if UNITY_EDITOR_WIN
            if (!espeakExists && GUILayout.Button("Download + Install espeak-ng into StreamingAssets"))
                _ = InstallEspeakAsync();
#else
            if (!espeakExists)
                EditorGUILayout.HelpBox(
                    "Install espeak-ng (e.g. 'brew install espeak-ng' / 'apt install espeak-ng') and point " +
                    "Espeak Path in settings at the binary, or copy it under StreamingAssets for builds.",
                    MessageType.Info);
#endif

            // c) codec decoder. Sentis was renamed to Inference Engine
            // (com.unity.ai.inference) in Unity 6.1; installing the old id there
            // fails with an "invalid signature" error.
#if UNITY_6000_1_OR_NEWER
            const string inferencePackage = "com.unity.ai.inference";
#else
            const string inferencePackage = "com.unity.sentis";
#endif
            bool inferenceInstalled =
                Type.GetType("Unity.InferenceEngine.ModelAsset, Unity.InferenceEngine") != null ||
                Type.GetType("Unity.Sentis.ModelAsset, Unity.Sentis") != null;
            EditorGUILayout.LabelField("Inference Engine package", inferenceInstalled ? "Installed" : "NOT installed (required for codec decoding)");
            if (!inferenceInstalled && GUILayout.Button($"Install {inferencePackage}"))
            {
                UnityEditor.PackageManager.Client.Add(inferencePackage);
                _status = $"Installing {inferencePackage} via Package Manager...";
            }

            EditorGUILayout.LabelField("Codec decoder", _settings.neuCodecDecoder == null ? "(not assigned)" : _settings.neuCodecDecoder.name);

            string codecFullPath = Path.GetFullPath(CodecAssetPath);
            bool codecOnDisk = File.Exists(codecFullPath);
            bool codecUsable = codecOnDisk && CachedLooksLikeOnnx(codecFullPath, out string codecProblem);

            // The model repo is gated on Hugging Face: an unauthenticated fetch returns a
            // short 401 text body, which older versions happily wrote out as the .onnx and
            // then failed to import with a protobuf "invalid wire type" exception.
            if (codecOnDisk && !codecUsable)
            {
                EditorGUILayout.HelpBox(
                    $"{CodecAssetPath} is not a valid ONNX model - {codecProblem}. This is almost always " +
                    "a failed download - most often an older version of this package saving Hugging " +
                    "Face's gate page under the .onnx name. Delete it and download again below; the " +
                    "current download needs no account and is checksum-verified.",
                    MessageType.Error);
                if (GUILayout.Button("Delete Broken File"))
                {
                    AssetDatabase.DeleteAsset(CodecAssetPath);
                    AssetDatabase.Refresh();
                    _codecCheckedPath = null;
                    _status = "Deleted the invalid " + CodecAssetPath + ".";
                    Repaint();
                }
            }

            if (_settings.neuCodecDecoder == null)
            {
                if (!_codecUseUpstream)
                {
                    EditorGUILayout.HelpBox(
                        "Downloads Neuphonic's NeuCodec decoder (Apache-2.0, ~746 MB) from this " +
                        "package's releases, already patched for Unity's ONNX importer. No account " +
                        "or token needed, and the file is checksum-verified after download.",
                        MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Upstream is a gated repo: sign in, accept the terms on the model page, and " +
                        "paste a read access token below. The upstream file also needs patching with " +
                        "Tools~/patch_neucodec_onnx.py before Unity can import it.",
                        MessageType.Warning);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Open Model Page (accept terms)"))
                            Application.OpenURL(CodecRepoPage);
                        if (GUILayout.Button("Get Access Token"))
                            Application.OpenURL(HfTokensPage);
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        _hfToken = EditorGUILayout.PasswordField("HF Access Token", _hfToken);
                        if (EditorGUI.EndChangeCheck())
                            EditorPrefs.SetString(HfTokenPrefKey, _hfToken ?? "");
                        if (GUILayout.Button("Clear", GUILayout.Width(50)))
                        {
                            _hfToken = "";
                            EditorPrefs.DeleteKey(HfTokenPrefKey);
                            GUI.FocusControl(null);
                        }
                    }
                    EditorGUILayout.LabelField(" ", "Stored in EditorPrefs on this machine only - never in the project.", EditorStyles.miniLabel);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(_codecUseUpstream
                            ? "Download from Hugging Face (unpatched)"
                            : "Download NeuCodec ONNX Decoder (Apache-2.0)"))
                        _ = DownloadCodecAsync();
                    if (GUILayout.Button("Browse for Existing .onnx..."))
                        PickCodecOnnx();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    _codecUseUpstream = EditorGUILayout.ToggleLeft(
                        "Fetch from upstream Hugging Face instead", _codecUseUpstream);
                    if (GUILayout.Button("What is this file?", GUILayout.Width(130)))
                        Application.OpenURL(_codecUseUpstream ? CodecRepoPage : CodecReleasePage);
                }
            }

            if (codecUsable && (_settings.neuCodecDecoder == null || _settings.neuCodecDecoder is DefaultAsset))
            {
                EditorGUILayout.HelpBox(
                    "The .onnx file is on disk but is not imported as an Inference Engine model. Usually " +
                    "the Inference Engine package above is missing - install it, then click below. If the " +
                    "Console shows \"SplitToSequence not supported\" this is an unpatched upstream file: " +
                    "run Tools~/patch_neucodec_onnx.py on it (one-time, dev machine only) or re-download " +
                    "the pre-patched copy.",
                    MessageType.Warning);
                if (GUILayout.Button("Reimport + Reassign Codec Decoder"))
                {
                    AssetDatabase.ImportAsset(CodecAssetPath, ImportAssetOptions.ForceUpdate);
                    var reimported = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(CodecAssetPath);
                    if (reimported != null && !(reimported is DefaultAsset))
                    {
                        _settings.neuCodecDecoder = reimported;
                        EditorUtility.SetDirty(_settings);
                        AssetDatabase.SaveAssets();
                        _status = "Codec decoder imported and assigned.";
                    }
                    else
                    {
                        _status = "Codec decoder still failed to import - see the Console for the importer error.";
                    }
                }
            }

            // d) server control
            EditorGUILayout.LabelField("TTS server", EmbeddedTtsServer.IsRunning ? $"Running ({_settings.EmbeddedTtsRootUrl})" : "Stopped");
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Start / Ensure Running"))
                    _ = StartTtsServerAsync();
                if (GUILayout.Button("Stop"))
                {
                    EmbeddedTtsServer.Shutdown();
                    _status = "TTS server stopped.";
                }
            }
        }

        private async Task StartTtsServerAsync()
        {
            Begin("Starting NeuTTS llama-server...");
            try
            {
                _cts = new CancellationTokenSource();
                await EmbeddedTtsServer.EnsureRunningAsync(_settings, _cts.Token);
                End("NeuTTS server is running and healthy.");
            }
            catch (OperationCanceledException) { End("Cancelled."); }
            catch (Exception e) { End("Error: " + e.Message); }
        }

        /// <summary>
        /// Picks an already-downloaded neucodec model.onnx and copies it into the project.
        /// The escape hatch for the gated repo: grab the file in a browser, point at it here.
        /// </summary>
        private void PickCodecOnnx()
        {
            string picked = EditorUtility.OpenFilePanel("Choose the NeuCodec decoder model.onnx", "", "onnx");
            if (string.IsNullOrEmpty(picked))
                return;

            if (!LooksLikeOnnx(picked, out string why))
            {
                EditorUtility.DisplayDialog("Dynamic NPCs",
                    $"That file is not a valid ONNX model - {why}.\n\nIf you downloaded it from " +
                    "Hugging Face while signed out, you saved the gate's error page rather than the " +
                    "model. Accept the terms on the model page, then download it again.", "OK");
                return;
            }

            string fullPath = Path.GetFullPath(CodecAssetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            if (File.Exists(fullPath) && !EditorUtility.DisplayDialog("Dynamic NPCs",
                    CodecAssetPath + " already exists. Overwrite it?", "Overwrite", "Cancel"))
                return;

            File.Copy(picked, fullPath, true);
            AssetDatabase.Refresh();
            AssignCodecAfterImport($"Copied {Path.GetFileName(picked)} into {CodecAssetPath}");
        }

        private async Task DownloadCodecAsync()
        {
            Begin("Downloading NeuCodec ONNX decoder...");
            string tempPath = Path.Combine(Path.GetTempPath(), "dynamicnpcs-neucodec-decoder.onnx");
            try
            {
                string fullPath = Path.GetFullPath(CodecAssetPath);
                if (File.Exists(fullPath) && !EditorUtility.DisplayDialog(
                        "Dynamic NPCs",
                        CodecAssetPath + " already exists - if it was patched with " +
                        "patch_neucodec_onnx.py, re-downloading reverts it to the upstream model, " +
                        "which fails to import (\"SplitToSequence not supported\"). Overwrite it?",
                        "Overwrite", "Keep Existing"))
                {
                    End("Kept the existing file. Use 'Reimport + Reassign Codec Decoder' to assign it.");
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

                // Download to a temp file first: the repo is gated, and a rejected request
                // still has a body (the error text), which must never land on the .onnx path
                // where Unity's ONNX importer would try to parse it as protobuf.
                string url = _codecUseUpstream ? CodecUpstreamUrl : CodecUrl;
                long responseCode;
                using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET))
                {
                    req.downloadHandler = new DownloadHandlerFile(tempPath);
                    req.SetRequestHeader("User-Agent", "DynamicNPCs-Unity");
                    if (_codecUseUpstream && !string.IsNullOrWhiteSpace(_hfToken))
                        req.SetRequestHeader("Authorization", "Bearer " + _hfToken.Trim());
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        EditorUtility.DisplayProgressBar("Dynamic NPCs", "Downloading neucodec decoder", req.downloadProgress);
                        await Task.Yield();
                    }
                    responseCode = req.responseCode;
                    if (req.result != UnityWebRequest.Result.Success && responseCode != 401 && responseCode != 403)
                        throw new Exception($"Download failed: {req.error}");
                }

                if (responseCode == 401 || responseCode == 403)
                    throw new Exception(
                        $"Hugging Face refused the download (HTTP {responseCode}). {CodecRepoPage} is a " +
                        "gated repo: sign in, accept the terms on the model page, create an access " +
                        "token with read permission, and paste it into the HF Access Token field. " +
                        "You can also download model.onnx in the browser and use 'Browse for Existing .onnx...'.");

                if (!LooksLikeOnnx(tempPath, out string why))
                    throw new Exception(
                        $"The download is not a valid ONNX model - {why}. " + (_codecUseUpstream
                            ? "This usually means Hugging Face returned a gate or error page instead of " +
                              "the file. Accept the terms at " + CodecRepoPage + " and supply an access token."
                            : "The download may have been interrupted - try again.") +
                        " You can also download the file in a browser and use 'Browse for Existing .onnx...'.");

                // The mirrored file is a known, fixed artifact, so verify it. A truncated or
                // substituted download should fail here rather than as an importer error later.
                if (!_codecUseUpstream)
                {
                    EditorUtility.DisplayProgressBar("Dynamic NPCs", "Verifying checksum...", 1f);
                    string actual = Sha256(tempPath);
                    if (!string.Equals(actual, CodecSha256, StringComparison.OrdinalIgnoreCase))
                        throw new Exception(
                            "The downloaded decoder does not match its expected checksum and was " +
                            $"discarded.\nexpected {CodecSha256}\nactual   {actual}\n" +
                            "Try again; if it keeps failing, report it at " + CodecReleasePage);
                }

                File.Copy(tempPath, fullPath, true);
                AssetDatabase.Refresh();
                AssignCodecAfterImport($"Downloaded to {CodecAssetPath}");
            }
            catch (Exception e) { End("Error: " + e.Message); }
            finally
            {
                EditorUtility.ClearProgressBar();
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort temp cleanup */ }
            }
        }

        /// <summary>Assigns the freshly imported codec asset to settings, or explains why it did not import.</summary>
        private void AssignCodecAfterImport(string what)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(CodecAssetPath);
            bool imported = asset != null && !(asset is DefaultAsset);
            if (imported)
            {
                _settings.neuCodecDecoder = asset;
                EditorUtility.SetDirty(_settings);
                AssetDatabase.SaveAssets();
            }
            End(imported
                ? $"Codec decoder imported and assigned ({CodecAssetPath})."
                : what + ", but it failed to import. If the Console shows " +
                  "\"SplitToSequence not supported\", run Tools~/patch_neucodec_onnx.py on the file, " +
                  "then use Reimport + Reassign above; also check the Inference Engine package is installed.");
        }

        /// <summary>
        /// Fetches the mirrored NeuTTS backbone into StreamingAssets. Same shape as the codec
        /// download: temp file, status check, checksum, then move into place.
        /// </summary>
        private async Task DownloadTtsGgufAsync()
        {
            Begin("Downloading NeuTTS backbone GGUF...");
            string tempPath = Path.Combine(Path.GetTempPath(), "dynamicnpcs-neutts-air-Q4_0.gguf");
            try
            {
                string dest = Path.Combine(Application.streamingAssetsPath,
                    TtsGgufRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));

                using (var req = new UnityWebRequest(TtsGgufUrl, UnityWebRequest.kHttpVerbGET))
                {
                    req.downloadHandler = new DownloadHandlerFile(tempPath);
                    req.SetRequestHeader("User-Agent", "DynamicNPCs-Unity");
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        EditorUtility.DisplayProgressBar("Dynamic NPCs",
                            $"Downloading NeuTTS backbone ({req.downloadedBytes / (1024 * 1024)} MB)", req.downloadProgress);
                        await Task.Yield();
                    }
                    if (req.result != UnityWebRequest.Result.Success)
                        throw new Exception($"Download failed: {req.error}");
                }

                // A GGUF starts with the magic "GGUF"; anything else is an error page.
                using (var fs = File.OpenRead(tempPath))
                {
                    var magic = new byte[4];
                    if (fs.Read(magic, 0, 4) != 4 || System.Text.Encoding.ASCII.GetString(magic) != "GGUF")
                        throw new Exception("the download is not a GGUF file - it may have been interrupted; try again");
                }

                EditorUtility.DisplayProgressBar("Dynamic NPCs", "Verifying checksum...", 1f);
                string actual = Sha256(tempPath);
                if (!string.Equals(actual, TtsGgufSha256, StringComparison.OrdinalIgnoreCase))
                    throw new Exception(
                        "The downloaded backbone does not match its expected checksum and was " +
                        $"discarded.\nexpected {TtsGgufSha256}\nactual   {actual}");

                File.Copy(tempPath, dest, true);
                _settings.ttsModelPath = TtsGgufRelative;
                EditorUtility.SetDirty(_settings);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                End($"NeuTTS backbone downloaded to StreamingAssets/{TtsGgufRelative}.");
            }
            catch (Exception e) { End("Error: " + e.Message); }
            finally
            {
                EditorUtility.ClearProgressBar();
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort temp cleanup */ }
            }
        }

        private static string Sha256(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary><see cref="LooksLikeOnnx"/>, memoized on path + size + last-write time.</summary>
        private bool CachedLooksLikeOnnx(string path, out string reason)
        {
            var info = new FileInfo(path);
            if (path != _codecCheckedPath || info.Length != _codecCheckedLength || info.LastWriteTimeUtc != _codecCheckedTime)
            {
                _codecCheckedResult = LooksLikeOnnx(path, out _codecCheckedReason);
                _codecCheckedPath = path;
                _codecCheckedLength = info.Length;
                _codecCheckedTime = info.LastWriteTimeUtc;
            }
            reason = _codecCheckedReason;
            return _codecCheckedResult;
        }

        /// <summary>
        /// Cheap sanity check that a file is a real ONNX model rather than an HTML/JSON error
        /// page, a Hugging Face gate response, or a Git LFS pointer saved under an .onnx name.
        /// Those parse as protobuf garbage and surface as an opaque InvalidProtocolBufferException
        /// from the importer, so they are worth catching before the file reaches the AssetDatabase.
        /// </summary>
        private static bool LooksLikeOnnx(string path, out string reason)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                reason = "the file does not exist";
                return false;
            }

            byte[] head = new byte[(int)Math.Min(info.Length, 512)];
            if (head.Length > 0)
                using (var fs = File.OpenRead(path))
                {
                    int read = fs.Read(head, 0, head.Length);
                    if (read < head.Length)
                        Array.Resize(ref head, read);
                }

            bool isText = head.Length > 0 && head.All(b =>
                b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b <= 0x7E));
            if (isText)
            {
                string text = System.Text.Encoding.UTF8.GetString(head).Trim();
                if (text.Length > 160) text = text.Substring(0, 160) + "...";
                reason = $"it is text, not a model: \"{text}\"";
                return false;
            }

            // The real decoder is ~750 MB; anything in the kilobytes is a failed transfer.
            if (info.Length < 1024 * 1024)
            {
                reason = $"it is only {info.Length} bytes";
                return false;
            }

            reason = null;
            return true;
        }

#if UNITY_EDITOR_WIN
        private async Task InstallEspeakAsync()
        {
            Begin("Downloading espeak-ng.msi...");
            try
            {
                string msiPath = Path.Combine(Path.GetTempPath(), "espeak-ng.msi");
                using (var req = new UnityWebRequest(EspeakMsiUrl, UnityWebRequest.kHttpVerbGET))
                {
                    req.downloadHandler = new DownloadHandlerFile(msiPath);
                    req.SetRequestHeader("User-Agent", "DynamicNPCs-Unity");
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        EditorUtility.DisplayProgressBar("Dynamic NPCs", "Downloading espeak-ng", req.downloadProgress);
                        await Task.Yield();
                    }
                    if (req.result != UnityWebRequest.Result.Success)
                        throw new Exception($"Download failed: {req.error}");
                }

                // Administrative extraction: unpacks the MSI without installing (no admin needed).
                _status = "Extracting espeak-ng...";
                Repaint();
                string extractDir = Path.Combine(Path.GetTempPath(), "espeak-ng-extract-" + DateTime.Now.Ticks);
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "msiexec",
                    Arguments = $"/a \"{msiPath}\" /qn TARGETDIR=\"{extractDir}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    while (proc != null && !proc.HasExited)
                        await Task.Yield();
                    if (proc == null || proc.ExitCode != 0)
                        throw new Exception($"msiexec extraction failed (code {proc?.ExitCode}).");
                }

                string exe = Directory.GetFiles(extractDir, "espeak-ng.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (exe == null)
                    throw new Exception("espeak-ng.exe not found in the extracted MSI.");
                string sourceDir = Path.GetDirectoryName(exe);

                string destDir = Path.Combine(Application.streamingAssetsPath, EspeakDirRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(destDir);
                CopyTree(sourceDir, destDir);

                // espeak-ng-data may sit next to or above the exe in the MSI layout.
                if (!Directory.Exists(Path.Combine(destDir, "espeak-ng-data")))
                {
                    string data = Directory.GetDirectories(extractDir, "espeak-ng-data", SearchOption.AllDirectories).FirstOrDefault();
                    if (data != null)
                        CopyTree(data, Path.Combine(destDir, "espeak-ng-data"));
                }

                _settings.espeakPath = EspeakDirRelative + "/espeak-ng.exe";
                EditorUtility.SetDirty(_settings);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                try { Directory.Delete(extractDir, true); File.Delete(msiPath); } catch { }

                End($"espeak-ng installed to StreamingAssets/{EspeakDirRelative}. " +
                    "Note: espeak-ng is GPL-3.0; it ships as a separate executable (see README licensing notes).");
            }
            catch (Exception e) { End("Error: " + e.Message); }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private static void CopyTree(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
            foreach (string dir in Directory.GetDirectories(source))
                CopyTree(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
#endif

        private void Begin(string status)
        {
            _busy = true;
            _status = status;
            Repaint();
        }

        private void End(string status)
        {
            _busy = false;
            _status = status;
            Repaint();
        }

        [Serializable]
        private class GitHubRelease
        {
            public string tag_name;
            public GitHubAsset[] assets;
        }

        [Serializable]
        private class GitHubAsset
        {
            public string name;
            public string browser_download_url;
            public long size;
        }
    }
}
