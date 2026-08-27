using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicNpcs
{
    /// <summary>
    /// Converts text to IPA phonemes by invoking the espeak-ng CLI as a separate
    /// process (espeak-ng is GPL; process isolation keeps the game unencumbered).
    /// NeuTTS models are trained on espeak IPA-with-stress input, so this must match
    /// the reference pipeline: punctuation is preserved by splitting the text on
    /// punctuation, phonemizing each segment (one stdin line each), and reassembling.
    /// </summary>
    public static class EspeakPhonemizer
    {
        private const int CacheCap = 512;
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();
        private static readonly char[] PunctuationChars = { ',', ';', ':', '.', '!', '?', '…' };

        /// <summary>Maps package language codes to espeak-ng voice names.</summary>
        public static string ToEspeakVoice(string language)
        {
            switch ((language ?? "").Trim().ToLowerInvariant())
            {
                case "":
                case "en":
                case "en-us": return "en-us";
                case "fr": return "fr-fr";
                default: return language.Trim();
            }
        }

        public static async Task<string> PhonemizeAsync(
            DynamicNpcSettings settings, string text, string language, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string voice = ToEspeakVoice(language);
            string cacheKey = voice + "|#|" + text;
            lock (Cache)
            {
                if (Cache.TryGetValue(cacheKey, out string cached))
                    return cached;
            }

            string exe = DynamicNpcPaths.ResolveExecutable(settings.espeakPath);
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                throw new TtsException(
                    $"espeak-ng not found at '{exe}'. Install it via " +
                    "Window > Dynamic NPCs > Embedded Server Setup (NeuTTS section).");

            // Split into segments + punctuation, phonemize segments in one process call.
            var segments = new List<string>();
            var rebuilt = new List<(bool isSegment, string value)>();
            var current = new StringBuilder();
            foreach (char c in text)
            {
                if (Array.IndexOf(PunctuationChars, c) >= 0)
                {
                    FlushSegment(current, segments, rebuilt);
                    rebuilt.Add((false, c.ToString()));
                }
                else
                {
                    current.Append(c);
                }
            }
            FlushSegment(current, segments, rebuilt);

            if (segments.Count == 0)
                return text.Trim();

            string[] phonemized = await Task.Run(
                () => RunEspeak(exe, voice, segments, cancellationToken), cancellationToken);

            string result;
            if (phonemized.Length != segments.Count)
            {
                // Line counts diverged (espeak re-split a segment); fall back to a
                // simple join and keep only the final punctuation mark.
                result = string.Join(" ", phonemized).Trim();
                char last = text.TrimEnd()[text.TrimEnd().Length - 1];
                if (Array.IndexOf(PunctuationChars, last) >= 0)
                    result += last;
            }
            else
            {
                var sb = new StringBuilder();
                int seg = 0;
                foreach (var (isSegment, value) in rebuilt)
                {
                    if (isSegment)
                    {
                        if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                            sb.Append(' ');
                        sb.Append(phonemized[seg++].Trim());
                    }
                    else
                    {
                        sb.Append(value);
                    }
                }
                result = sb.ToString().Trim();
            }

            lock (Cache)
            {
                if (Cache.Count >= CacheCap)
                    Cache.Clear();
                Cache[cacheKey] = result;
            }
            return result;
        }

        private static void FlushSegment(
            StringBuilder current, List<string> segments, List<(bool, string)> rebuilt)
        {
            string s = current.ToString().Trim();
            current.Length = 0;
            if (s.Length == 0)
                return;
            segments.Add(s);
            rebuilt.Add((true, s));
        }

        private static string[] RunEspeak(
            string exe, string voice, List<string> lines, CancellationToken ct)
        {
            string exeDir = Path.GetDirectoryName(exe) ?? ".";
            string args = $"--stdin -q --ipa -v {voice}";
            if (Directory.Exists(Path.Combine(exeDir, "espeak-ng-data")))
                args += $" --path=\"{exeDir}\"";

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = exeDir,
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    throw new TtsException($"Failed to start espeak-ng ('{exe}').");

                using (ct.Register(() => { try { process.Kill(); } catch { } }))
                {
                    // Segments have had their punctuation stripped, so espeak would run
                    // consecutive stdin lines together into one sentence and emit a
                    // single output line. A terminal period per line forces one output
                    // line per segment (the period itself produces no phonemes).
                    var stdinText = new StringBuilder();
                    foreach (string line in lines)
                        stdinText.Append(line).Append(".\n");
                    byte[] input = Encoding.UTF8.GetBytes(stdinText.ToString());
                    process.StandardInput.BaseStream.Write(input, 0, input.Length);
                    process.StandardInput.BaseStream.Flush();
                    process.StandardInput.Close();

                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(30000))
                    {
                        try { process.Kill(); } catch { }
                        throw new TtsException("espeak-ng timed out.");
                    }
                    ct.ThrowIfCancellationRequested();

                    if (process.ExitCode != 0)
                        throw new TtsException($"espeak-ng failed (code {process.ExitCode}): {stderr}");

                    return stdout.Replace("\r", "").Split(
                        new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }
        }
    }
}
