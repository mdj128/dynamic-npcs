using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace DynamicNpcs
{
    /// <summary>
    /// Drop-in NPC brain: give it a settings asset, a persona, and an AudioSource,
    /// then call <see cref="Ask"/> with the player's line. The local LLM writes the
    /// reply in character while sentences are pipelined through the local TTS server
    /// and played in order, so the NPC starts speaking before the full reply exists.
    /// </summary>
    [AddComponentMenu("Dynamic NPCs/NPC Dialogue Agent")]
    [RequireComponent(typeof(AudioSource))]
    public class NpcDialogueAgent : MonoBehaviour
    {
        [Serializable] public class StringEvent : UnityEvent<string> { }

        [Tooltip("Global connection settings (LLM + TTS endpoints).")]
        public DynamicNpcSettings settings;

        [Tooltip("Who this NPC is: prompt, voice, LLM tuning.")]
        public NpcPersona persona;

        [Tooltip("AudioSource the NPC speaks through. Defaults to the one on this GameObject.")]
        public AudioSource audioSource;

        [Header("Events")]
        [Tooltip("Fired when a sentence starts playing - ideal for subtitles.")]
        public StringEvent onSentenceStarted;

        [Tooltip("Fired with the full reply text once the LLM finishes generating.")]
        public StringEvent onResponseText;

        [Tooltip("Fired after the last audio clip of a reply finishes playing.")]
        public UnityEvent onSpeechFinished;

        public StringEvent onError;

        /// <summary>True while a reply is being generated or spoken.</summary>
        public bool IsBusy { get; private set; }

        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private readonly LlmClient _llm = new LlmClient();
        private CancellationTokenSource _cts;

        private void Reset() => audioSource = GetComponent<AudioSource>();

        private void Awake()
        {
            if (audioSource == null)
                audioSource = GetComponent<AudioSource>();
        }

        private void OnDestroy() => _cts?.Cancel();

        /// <summary>Fire-and-forget version of <see cref="AskAsync"/>, wireable from UnityEvents.</summary>
        public void Ask(string playerLine) => _ = AskAsync(playerLine);

        /// <summary>Fire-and-forget version of <see cref="SayAsync"/>: speaks exact text without the LLM.</summary>
        public void Say(string text) => _ = SayAsync(text);

        /// <summary>Stops current playback and cancels any in-flight generation.</summary>
        public void CancelSpeech()
        {
            _cts?.Cancel();
            _cts = null;
            if (audioSource != null)
                audioSource.Stop();
            IsBusy = false;
        }

        /// <summary>Clears the conversation history (the NPC forgets the exchange so far).</summary>
        public void ResetConversation() => _history.Clear();

        /// <summary>
        /// Sends the player's line to the LLM and speaks the reply.
        /// Returns the full reply text (null if cancelled or failed).
        /// </summary>
        public async Task<string> AskAsync(string playerLine)
        {
            if (string.IsNullOrWhiteSpace(playerLine))
                return null;

            CancelSpeech();
            var cts = new CancellationTokenSource();
            _cts = cts;
            var ct = cts.Token;
            IsBusy = true;

            try
            {
                ValidateConfig();

                if (settings.UsesEmbeddedLlm)
                    await EmbeddedLlmServer.EnsureRunningAsync(settings, ct);

                _history.Add(new ChatMessage("user", playerLine));
                TrimHistory();

                var request = new ChatRequest
                {
                    model = persona.ResolveModel(settings),
                    messages = BuildMessages(),
                    temperature = persona.temperature,
                    max_tokens = persona.maxTokens,
                };

                var chunker = new SentenceChunker(settings.minChunkChars);
                var sentenceQueue = new Queue<string>();
                bool generationDone = false;

                // Speech pipeline runs concurrently with LLM streaming.
                Task speechTask = SpeakQueueAsync(sentenceQueue, () => generationDone, ct);

                string fullText;
                try
                {
                    fullText = await _llm.StreamChatAsync(
                        settings.ResolveLlmBaseUrl(),
                        request,
                        delta =>
                        {
                            foreach (string sentence in chunker.Feed(delta))
                                sentenceQueue.Enqueue(sentence);
                        },
                        settings.llmTimeoutSeconds,
                        ct);

                    string tail = chunker.Flush();
                    if (!string.IsNullOrEmpty(tail))
                        sentenceQueue.Enqueue(tail);
                }
                finally
                {
                    generationDone = true;
                }

                _history.Add(new ChatMessage("assistant", fullText));
                if (this != null)
                    onResponseText?.Invoke(fullText);

                await speechTask;
                ct.ThrowIfCancellationRequested();

                if (this != null)
                    onSpeechFinished?.Invoke();
                return fullText;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception e)
            {
                cts.Cancel(); // stop the speech pipeline if generation failed midway
                ReportError(e);
                return null;
            }
            finally
            {
                // Intentionally not disposing cts: the speech task may still be
                // observing the token, and CTS without timers holds no native resources.
                if (_cts == cts)
                {
                    IsBusy = false;
                    _cts = null;
                }
            }
        }

        /// <summary>Speaks exact text in this NPC's voice, bypassing the LLM.</summary>
        public async Task SayAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            CancelSpeech();
            var cts = new CancellationTokenSource();
            _cts = cts;
            var ct = cts.Token;
            IsBusy = true;

            try
            {
                ValidateConfig();
                AudioClip clip = await SpeechSynthesizer.SynthesizeAsync(settings, text, persona.voice, ct);
                if (this == null)
                    return;
                onSentenceStarted?.Invoke(text);
                await PlayClipAsync(clip, ct);
                if (this != null)
                    onSpeechFinished?.Invoke();
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                ReportError(e);
            }
            finally
            {
                if (_cts == cts)
                {
                    IsBusy = false;
                    _cts = null;
                }
            }
        }

        // --- internals ---

        private void ValidateConfig()
        {
            if (settings == null)
                throw new InvalidOperationException($"{name}: NpcDialogueAgent has no DynamicNpcSettings assigned.");
            if (persona == null)
                throw new InvalidOperationException($"{name}: NpcDialogueAgent has no NpcPersona assigned.");
            if (persona.voice == null)
                throw new InvalidOperationException($"{name}: persona '{persona.name}' has no NpcVoice assigned.");
            if (audioSource == null)
                throw new InvalidOperationException($"{name}: NpcDialogueAgent has no AudioSource.");
        }

        private ChatMessage[] BuildMessages()
        {
            var messages = new List<ChatMessage>(_history.Count + 1)
            {
                new ChatMessage("system", persona.BuildSystemPrompt())
            };
            messages.AddRange(_history);
            return messages.ToArray();
        }

        private void TrimHistory()
        {
            int maxMessages = Mathf.Max(1, persona.maxHistoryTurns) * 2;
            if (_history.Count > maxMessages)
                _history.RemoveRange(0, _history.Count - maxMessages);
        }

        /// <summary>
        /// Consumes sentences as the LLM produces them: synthesizes each via TTS
        /// (prefetching the next while the current plays) and plays them in order.
        /// </summary>
        private async Task SpeakQueueAsync(Queue<string> queue, Func<bool> producerDone, CancellationToken ct)
        {
            try
            {
                Task<AudioClip> pending = null;
                string pendingText = null;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    if (pending == null)
                    {
                        if (queue.Count > 0)
                        {
                            pendingText = queue.Dequeue();
                            pending = SpeechSynthesizer.SynthesizeAsync(settings, pendingText, persona.voice, ct);
                        }
                        else if (producerDone())
                        {
                            break;
                        }
                        else
                        {
                            await Task.Yield();
                            continue;
                        }
                    }

                    AudioClip clip;
                    string text = pendingText;
                    try
                    {
                        clip = await pending;
                    }
                    finally
                    {
                        pending = null;
                        pendingText = null;
                    }

                    // Start synthesizing the next sentence while this one plays.
                    if (queue.Count > 0)
                    {
                        pendingText = queue.Dequeue();
                        pending = SpeechSynthesizer.SynthesizeAsync(settings, pendingText, persona.voice, ct);
                    }

                    if (this == null)
                        return;
                    onSentenceStarted?.Invoke(text);
                    await PlayClipAsync(clip, ct);

                    // Sentences are separate clips; restore the pause a speaker would
                    // take at a sentence boundary (skipped after the last one).
                    bool moreToSpeak = pending != null || queue.Count > 0 || !producerDone();
                    if (moreToSpeak && settings.interSentencePause > 0f)
                        await Task.Delay(
                            TimeSpan.FromSeconds(settings.interSentencePause), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                ReportError(e);
            }
        }

        private async Task PlayClipAsync(AudioClip clip, CancellationToken ct)
        {
            if (clip == null || audioSource == null)
                return;

            audioSource.clip = clip;
            audioSource.Play();

            while (this != null && audioSource != null && audioSource.isPlaying)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        private void ReportError(Exception e)
        {
            Debug.LogError($"[DynamicNPCs] {e.Message}", this);
            if (this != null)
                onError?.Invoke(e.Message);
        }
    }
}
