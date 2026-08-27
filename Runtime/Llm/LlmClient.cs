using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace DynamicNpcs
{
    public class LlmException : Exception
    {
        public LlmException(string message) : base(message) { }
    }

    /// <summary>
    /// Client for any OpenAI-compatible chat completions API
    /// (Ollama /v1, llama.cpp server, LM Studio, vLLM, ...).
    /// </summary>
    public class LlmClient
    {
        /// <summary>
        /// Streams a chat completion. <paramref name="onDelta"/> is invoked on the main
        /// thread with each content fragment as it arrives. Returns the full reply.
        /// </summary>
        public async Task<string> StreamChatAsync(
            string baseUrl,
            ChatRequest request,
            Action<string> onDelta,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            request.stream = true;
            string json = JsonUtility.ToJson(request);
            var text = new StringBuilder();

            var handler = new SseDownloadHandler(payload =>
            {
                var chunk = JsonUtility.FromJson<StreamResponse>(payload);
                if (chunk?.choices == null || chunk.choices.Length == 0)
                    return;
                string content = chunk.choices[0].delta?.content;
                if (!string.IsNullOrEmpty(content))
                {
                    text.Append(content);
                    onDelta?.Invoke(content);
                }
            });

            using (var req = new UnityWebRequest(ChatUrl(baseUrl), UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = handler;
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Accept", "text/event-stream");
                req.timeout = timeoutSeconds;

                using (cancellationToken.Register(req.Abort))
                {
                    await req.SendWebRequest();
                    cancellationToken.ThrowIfCancellationRequested();

                    if (req.result != UnityWebRequest.Result.Success)
                        throw new LlmException(BuildError("LLM stream", req, handler.RawText));
                }
            }
            return text.ToString();
        }

        /// <summary>Non-streaming chat completion. Returns the full reply.</summary>
        public async Task<string> ChatAsync(
            string baseUrl,
            ChatRequest request,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            request.stream = false;
            string json = JsonUtility.ToJson(request);

            using (var req = new UnityWebRequest(ChatUrl(baseUrl), UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = timeoutSeconds;

                using (cancellationToken.Register(req.Abort))
                {
                    await req.SendWebRequest();
                    cancellationToken.ThrowIfCancellationRequested();

                    if (req.result != UnityWebRequest.Result.Success)
                        throw new LlmException(BuildError("LLM request", req, req.downloadHandler.text));

                    var response = JsonUtility.FromJson<ChatResponse>(req.downloadHandler.text);
                    if (response?.choices == null || response.choices.Length == 0 || response.choices[0].message == null)
                        throw new LlmException("LLM response contained no choices.");
                    return response.choices[0].message.content ?? string.Empty;
                }
            }
        }

        /// <summary>Simple reachability check: GET {baseUrl}/models.</summary>
        public async Task<string> CheckAsync(string baseUrl, int timeoutSeconds, CancellationToken cancellationToken)
        {
            using (var req = UnityWebRequest.Get(baseUrl.TrimEnd('/') + "/models"))
            {
                req.timeout = timeoutSeconds;
                using (cancellationToken.Register(req.Abort))
                {
                    await req.SendWebRequest();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (req.result != UnityWebRequest.Result.Success)
                        throw new LlmException(BuildError("LLM health check", req, req.downloadHandler.text));
                    return req.downloadHandler.text;
                }
            }
        }

        private static string ChatUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/chat/completions";

        private static string BuildError(string what, UnityWebRequest req, string body)
        {
            string detail = string.IsNullOrEmpty(body) ? req.error : $"{req.error} - {Truncate(body, 500)}";
            return $"{what} failed ({req.url}): {detail}";
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
