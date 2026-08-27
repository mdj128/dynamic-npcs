using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace DynamicNpcs
{
    /// <summary>Launch configuration for one llama-server process.</summary>
    public class LlamaServerConfig
    {
        public string exePath;
        public string modelPath;
        public int port;
        public int gpuLayers;
        public int contextSize;
        public string extraArgs;
        public int startupTimeoutSeconds = 180;

        public string RootUrl => $"http://127.0.0.1:{port}";

        public string Key => string.Join("|",
            DynamicNpcPaths.ResolveExecutable(exePath),
            DynamicNpcPaths.Resolve(modelPath),
            port, gpuLayers, contextSize, extraArgs);
    }

    /// <summary>
    /// Owns one llama-server child process: starts it on demand, waits for the model
    /// to load, reuses it across calls (and across editor domain reloads), restarts it
    /// when its config changes, and kills it on shutdown. The package keeps one host
    /// for the dialogue LLM and one for the NeuTTS backbone.
    /// All members must be called from the Unity main thread.
    /// </summary>
    public class LlamaServerHost
    {
        private const int LogCapacity = 32 * 1024;
        private const int ExternalPid = -1; // healthy server on our port that we didn't start

        private static readonly List<LlamaServerHost> All = new List<LlamaServerHost>();

        private readonly string _name;
        private readonly object _logLock = new object();
        private readonly StringBuilder _log = new StringBuilder();

        private Process _process;
        private int _ownedPid;
        private string _runningKey;
        private Task _startTask;
        private string _startTaskKey;

        public LlamaServerHost(string name)
        {
            _name = name;
            All.Add(this);
        }

        /// <summary>Recent stdout/stderr from the server process.</summary>
        public string LogText
        {
            get { lock (_logLock) return _log.ToString(); }
        }

        public bool IsRunning
        {
            get
            {
                if (_ownedPid == ExternalPid)
                    return true;
                if (_process != null)
                {
                    try { return !_process.HasExited; }
                    catch { return false; }
                }
                return _ownedPid > 0 && IsLlamaPidAlive(_ownedPid);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init()
        {
#if !UNITY_EDITOR
            Application.quitting -= ShutdownAll;
            Application.quitting += ShutdownAll;
#endif
            // In the editor, servers intentionally survive play-mode exits (fast
            // iteration; models stay loaded) and are killed when the editor quits.
        }

        public static void ShutdownAll()
        {
            foreach (var host in All)
                host.Shutdown();
        }

        /// <summary>
        /// Ensures a healthy server matching <paramref name="config"/> is listening.
        /// Concurrent callers share one startup. Restarts the server if config changed.
        /// </summary>
        public Task EnsureRunningAsync(LlamaServerConfig config, CancellationToken cancellationToken)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            string key = config.Key;
            bool reusable = _startTask != null && _startTaskKey == key &&
                            (!_startTask.IsCompleted ||
                             (_startTask.Status == TaskStatus.RanToCompletion && IsRunning));
            if (!reusable)
            {
                // StartAsync may call Shutdown() (which clears these fields) in its
                // synchronous prefix, so assign them after the task is created.
                var task = StartAsync(config, key);
                _startTask = task;
                _startTaskKey = key;
            }
            return WaitAsync(_startTask, cancellationToken);
        }

        /// <summary>Kills the owned server process (external servers are left alone).</summary>
        public void Shutdown()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill();
            }
            catch { /* already gone */ }

            if (_process == null && _ownedPid > 0)
                TryKillLlamaPid(_ownedPid);

            _process = null;
            _ownedPid = 0;
            _runningKey = null;
            _startTask = null;
            _startTaskKey = null;

#if UNITY_EDITOR
            UnityEditor.SessionState.SetInt(PidSessionKey, 0);
            UnityEditor.SessionState.SetString(ArgsSessionKey, "");
#endif
        }

        // --- internals ---

#if UNITY_EDITOR
        private string PidSessionKey => $"DynamicNpcs.LlamaHost.{_name}.Pid";
        private string ArgsSessionKey => $"DynamicNpcs.LlamaHost.{_name}.Key";
#endif

        private async Task StartAsync(LlamaServerConfig config, string key)
        {
            string rootUrl = config.RootUrl;

#if UNITY_EDITOR
            // Reattach to a server we started before the last domain reload.
            if (_process == null && _ownedPid == 0)
            {
                int pid = UnityEditor.SessionState.GetInt(PidSessionKey, 0);
                string prevKey = UnityEditor.SessionState.GetString(ArgsSessionKey, "");
                if (pid > 0 && IsLlamaPidAlive(pid))
                {
                    if (prevKey == key && await IsHealthyAsync(rootUrl, 3))
                    {
                        _ownedPid = pid;
                        _runningKey = key;
                        AppendLog($"[DynamicNPCs:{_name}] Reattached to llama-server (pid {pid}).");
                        return;
                    }
                    TryKillLlamaPid(pid); // config changed since it was started
                }
                UnityEditor.SessionState.SetInt(PidSessionKey, 0);
            }
#endif

            if (_runningKey == key && IsRunning && await IsHealthyAsync(rootUrl, 3))
                return;

            Shutdown(); // dead, or config changed

            // A server we didn't start is already answering on this port (e.g. the
            // developer launched their own) - use it rather than fight over the port.
            if (await IsHealthyAsync(rootUrl, 1))
            {
                AppendLog($"[DynamicNPCs:{_name}] Adopting existing healthy server at {rootUrl}.");
                _ownedPid = ExternalPid;
                _runningKey = key;
                return;
            }

            string exe = DynamicNpcPaths.ResolveExecutable(config.exePath);
            string model = DynamicNpcPaths.Resolve(config.modelPath);

            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                throw new LlmException(
                    $"[{_name}] llama-server executable not found at '{exe}'. " +
                    "Download it via Window > Dynamic NPCs > Embedded Server Setup.");
            if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
                throw new LlmException(
                    $"[{_name}] GGUF model not found at '{model}'. " +
                    "Set the model path via Window > Dynamic NPCs > Embedded Server Setup.");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments =
                    $"-m \"{model}\" --host 127.0.0.1 --port {config.port} " +
                    $"-ngl {config.gpuLayers} -c {config.contextSize} {config.extraArgs}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
            };

            AppendLog($"[DynamicNPCs:{_name}] Starting: {psi.FileName} {psi.Arguments}");
            Process process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception e)
            {
                throw new LlmException($"[{_name}] Failed to start llama-server ('{exe}'): {e.Message}");
            }

            process.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _process = process;
            _ownedPid = process.Id;
            _runningKey = key;

#if UNITY_EDITOR
            UnityEditor.SessionState.SetInt(PidSessionKey, process.Id);
            UnityEditor.SessionState.SetString(ArgsSessionKey, key);
#endif

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < config.startupTimeoutSeconds)
            {
                if (process.HasExited)
                {
                    string tail = LogTail();
                    Shutdown();
                    throw new LlmException($"[{_name}] llama-server exited during startup (code {process.ExitCode}). Log tail:\n{tail}");
                }
                if (await IsHealthyAsync(rootUrl, 3))
                {
                    AppendLog($"[DynamicNPCs:{_name}] llama-server ready in {sw.Elapsed.TotalSeconds:0.0}s.");
                    return;
                }
                await Task.Delay(500);
            }

            string timeoutTail = LogTail();
            Shutdown();
            throw new LlmException(
                $"[{_name}] llama-server did not become healthy within {config.startupTimeoutSeconds}s. Log tail:\n{timeoutTail}");
        }

        private static async Task<bool> IsHealthyAsync(string rootUrl, int timeoutSeconds)
        {
            using (var req = UnityWebRequest.Get(rootUrl + "/health"))
            {
                req.timeout = timeoutSeconds;
                await req.SendWebRequest();
                return req.result == UnityWebRequest.Result.Success && req.responseCode == 200;
            }
        }

        private static async Task WaitAsync(Task task, CancellationToken ct)
        {
            if (!task.IsCompleted)
            {
                var cancelTcs = new TaskCompletionSource<bool>();
                using (ct.Register(() => cancelTcs.TrySetCanceled()))
                    await Task.WhenAny(task, cancelTcs.Task);
                ct.ThrowIfCancellationRequested();
            }
            await task; // propagate any startup exception
        }

        private static bool IsLlamaPidAlive(int pid)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                return !p.HasExited &&
                       p.ProcessName.IndexOf("llama", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static void TryKillLlamaPid(int pid)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                if (!p.HasExited && p.ProcessName.IndexOf("llama", StringComparison.OrdinalIgnoreCase) >= 0)
                    p.Kill();
            }
            catch { /* already gone or not ours */ }
        }

        private void AppendLog(string line)
        {
            lock (_logLock)
            {
                _log.AppendLine(line);
                if (_log.Length > LogCapacity)
                    _log.Remove(0, _log.Length - LogCapacity / 2);
            }
        }

        private string LogTail()
        {
            string text = LogText;
            return text.Length <= 2000 ? text : text.Substring(text.Length - 2000);
        }
    }
}
