using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DynamicNpcs.Editor
{
    /// <summary>
    /// One-click NeuTTS voice baking from the NpcVoice inspector. Encoding a WAV to
    /// NeuCodec reference codes needs the NeuCodec *encoder*, which only exists in
    /// PyTorch - so this automates the dev-machine Python step: finds a Python 3,
    /// provisions a cached venv under Library/ (never shipped), runs
    /// Tools~/bake_neutts_voice.py, and applies the resulting codes to the asset.
    /// Players never need any of this - baked codes ship as plain asset data.
    /// </summary>
    public static class VoiceBaker
    {
        public static bool IsBaking { get; private set; }
        public static string Status { get; private set; } = "";

        private static string _lastLine = "";
        private static readonly object LineLock = new object();

        private static string VenvDir => Path.GetFullPath("Library/DynamicNpcs/bake-venv");

        private static string VenvPython =>
#if UNITY_EDITOR_WIN
            Path.Combine(VenvDir, "Scripts", "python.exe");
#else
            Path.Combine(VenvDir, "bin", "python");
#endif

        public static async void BakeAsync(NpcVoice voice, string wavPath, Action<int[], string> onBaked)
        {
            if (IsBaking)
                return;
            IsBaking = true;
            try
            {
                string script = FindBakeScript();
                string outJson = Path.Combine(Path.GetTempPath(), $"dnpc_bake_{Guid.NewGuid():N}.json");
                string transcriptFile = Path.Combine(Path.GetTempPath(), $"dnpc_transcript_{Guid.NewGuid():N}.txt");
                File.WriteAllText(transcriptFile, voice.sampleTranscript.Trim(), new UTF8Encoding(false));

                using (var cts = new CancellationTokenSource())
                {
                    if (!File.Exists(VenvPython))
                    {
                        string sysPython = await FindSystemPythonAsync(cts.Token);
                        await RunStepAsync(sysPython, $"-m venv \"{VenvDir}\"",
                            "Creating Python environment...", cts);
                    }

                    // Cheap check whether deps are already installed in the cached venv.
                    bool depsReady = await RunSilentlyAsync(VenvPython, "-c \"import neucodec, librosa\"");
                    if (!depsReady)
                        // torchao is pinned: neucodec asks for torchao>=0.12 with no upper
                        // bound, but 0.18 moved NF4Tensor out of torchao.dtypes, which the
                        // torchtune that neucodec imports still expects. Unpinned, pip picks
                        // 0.18 and the bake dies on "No module named torchao.dtypes.nf4tensor".
                        await RunStepAsync(VenvPython, "-m pip install neucodec librosa \"torchao<0.18\"",
                            "Installing neucodec + librosa (one-time, downloads PyTorch - several minutes)...", cts);

                    await RunStepAsync(VenvPython,
                        $"\"{script}\" \"{wavPath}\" \"{transcriptFile}\" \"{outJson}\"",
                        "Encoding sample (first bake downloads NeuCodec weights)...", cts);
                }

                var data = JsonUtility.FromJson<BakeJson>(File.ReadAllText(outJson));
                if (data?.codes == null || data.codes.Length == 0)
                    throw new Exception("bake produced no codes");
                File.Delete(outJson);
                File.Delete(transcriptFile);

                Status = $"Baked {data.codes.Length} codes ({data.codes.Length / 50f:0.0}s of reference).";
                onBaked(data.codes, Status);
            }
            catch (OperationCanceledException)
            {
                Status = "Bake cancelled.";
            }
            catch (Exception e)
            {
                string message = Explain(e.Message);
                Status = "Bake failed: " + message;
                Debug.LogError("[DynamicNPCs] " + Status);
                EditorUtility.DisplayDialog("Voice bake failed", message, "OK");
            }
            finally
            {
                IsBaking = false;
                EditorUtility.ClearProgressBar();
            }
        }

        [Serializable]
        private class BakeJson
        {
            public string name;
            public string transcript;
            public int[] codes;
        }

        private static string FindBakeScript()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(VoiceBaker).Assembly);
            string path = package != null
                ? Path.Combine(package.resolvedPath, "Tools~", "bake_neutts_voice.py")
                : Path.GetFullPath("Packages/com.mdj.dynamicnpcs/Tools~/bake_neutts_voice.py");
            if (!File.Exists(path))
                throw new Exception($"bake script not found at {path}");
            return path;
        }

        private static async Task<string> FindSystemPythonAsync(CancellationToken ct)
        {
#if UNITY_EDITOR_WIN
            string[][] candidates = { new[] { "py", "-3" }, new[] { "python", "" }, new[] { "python3", "" } };
#else
            string[][] candidates = { new[] { "python3", "" }, new[] { "python", "" } };
#endif
            foreach (var c in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (await RunSilentlyAsync(c[0], (c[1] + " --version").Trim()))
#if UNITY_EDITOR_WIN
                    // "py -3" is split back into exe + arg prefix by SplitLauncher.
                    return c[0] == "py" ? "py -3" : c[0];
#else
                    return c[0];
#endif
            }
            throw new Exception(
                "No Python 3 found on this machine. Install it (e.g. from python.org or " +
                "'winget install Python.Python.3.12'), then bake again. Python is only " +
                "needed on the dev machine, never by players.");
        }

        /// <summary>Runs a process and reports success, swallowing all output.</summary>
        private static Task<bool> RunSilentlyAsync(string exe, string args)
        {
            return Task.Run(() =>
            {
                try
                {
                    var psi = SplitLauncher(exe, args);
                    using (var p = Process.Start(psi))
                    {
                        p.StandardOutput.ReadToEnd();
                        p.StandardError.ReadToEnd();
                        return p.WaitForExit(15000) && p.ExitCode == 0;
                    }
                }
                catch
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// Runs a long process with a cancelable progress bar fed by its output lines.
        /// </summary>
        private static async Task RunStepAsync(string exe, string args, string title, CancellationTokenSource cts)
        {
            Status = title;
            lock (LineLock) _lastLine = "";

            var psi = SplitLauncher(exe, args);
            using (var process = Process.Start(psi))
            {
                if (process == null)
                    throw new Exception($"failed to start {exe}");

                process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (LineLock) _lastLine = e.Data; };
                var stderr = new StringBuilder();
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (LineLock) { _lastLine = e.Data; stderr.AppendLine(e.Data); }
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                float pulse = 0f;
                while (!process.HasExited)
                {
                    string line;
                    lock (LineLock) line = _lastLine;
                    pulse = (pulse + 0.01f) % 1f;
                    if (EditorUtility.DisplayCancelableProgressBar("Dynamic NPCs - baking voice", $"{title}\n{line}", pulse))
                    {
                        try { process.Kill(); } catch { }
                        cts.Cancel();
                        throw new OperationCanceledException();
                    }
                    await Task.Delay(150);
                }

                if (process.ExitCode != 0)
                {
                    string tail = stderr.ToString();
                    if (tail.Length > 800) tail = tail.Substring(tail.Length - 800);
                    throw new Exception($"'{Path.GetFileName(psi.FileName)} {psi.Arguments}' failed (code {process.ExitCode}):\n{tail}");
                }
            }
        }

        /// <summary>Splits launcher expressions like "py -3" into file name + prefixed args.</summary>
        /// <summary>
        /// Turns the more common Python stack traces into something actionable. The raw
        /// traceback is still in the Console for anything not recognised here.
        /// </summary>
        private static string Explain(string raw)
        {
            // Baking loads mirrored encoder weights from disk, so a gated-repo error means
            // something reached for neuphonic's repos anyway - a stale cached script, or a
            // neucodec version whose from_pretrained path is being used somewhere new.
            if (raw.Contains("GatedRepoError") || raw.Contains("gated repo") ||
                raw.Contains("is restricted"))
                return
                    "Something tried to download from a gated Hugging Face repo. Baking should not " +
                    "need an account: it loads mirrored encoder weights from disk.\n\n" +
                    "Delete Library/DynamicNpcs/bake-venv and bake again to rebuild the environment. " +
                    "If it persists, a Hugging Face read token in the setup window works around it.\n\n" +
                    raw;

            if (raw.Contains("torchao.dtypes.nf4tensor"))
                return
                    "The bake environment has an incompatible torchao. Delete " +
                    "Library/DynamicNpcs/bake-venv and bake again to rebuild it with the pinned " +
                    "version.\n\n" + raw;

            return raw;
        }

        private static ProcessStartInfo SplitLauncher(string exe, string args)
        {
            string file = exe;
            int space = exe.IndexOf(' ');
            if (space > 0)
            {
                file = exe.Substring(0, space);
                args = exe.Substring(space + 1) + " " + args;
            }
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // The NeuCodec encoder lives in a gated repo. huggingface_hub picks the token up
            // from either of these; passing it per-process avoids touching the user's global
            // HF login or writing the token anywhere on disk.
            string hfToken = EditorPrefs.GetString(EmbeddedServerSetupWindow.HfTokenPrefKey, "");
            if (!string.IsNullOrWhiteSpace(hfToken))
            {
                psi.Environment["HF_TOKEN"] = hfToken.Trim();
                psi.Environment["HUGGING_FACE_HUB_TOKEN"] = hfToken.Trim();
            }
            return psi;
        }
    }
}
