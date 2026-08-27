using System;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace DynamicNpcs
{
    /// <summary>
    /// Parses a server-sent-events (SSE) stream as it downloads, invoking a callback
    /// with each "data: {...}" payload. Used for streaming chat completions.
    /// </summary>
    public class SseDownloadHandler : DownloadHandlerScript
    {
        private readonly Action<string> _onPayload;
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _line = new StringBuilder();
        private readonly StringBuilder _raw = new StringBuilder();

        /// <summary>First few KB of the raw body, for error reporting on non-SSE responses.</summary>
        public string RawText => _raw.ToString();

        public SseDownloadHandler(Action<string> onPayload) : base(new byte[16 * 1024])
        {
            _onPayload = onPayload;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0)
                return false;

            var chars = new char[_decoder.GetCharCount(data, 0, dataLength)];
            int n = _decoder.GetChars(data, 0, dataLength, chars, 0);

            if (_raw.Length < 4096)
                _raw.Append(chars, 0, Math.Min(n, 4096 - _raw.Length));

            for (int i = 0; i < n; i++)
            {
                char c = chars[i];
                if (c == '\n')
                {
                    ProcessLine(_line.ToString());
                    _line.Length = 0;
                }
                else if (c != '\r')
                {
                    _line.Append(c);
                }
            }
            return true;
        }

        protected override void CompleteContent()
        {
            if (_line.Length > 0)
            {
                ProcessLine(_line.ToString());
                _line.Length = 0;
            }
        }

        private void ProcessLine(string line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                return;

            string payload = line.Substring(5).Trim();
            if (payload.Length == 0 || payload == "[DONE]")
                return;

            try
            {
                _onPayload?.Invoke(payload);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DynamicNPCs] Failed to handle SSE chunk: {e.Message}");
            }
        }
    }
}
