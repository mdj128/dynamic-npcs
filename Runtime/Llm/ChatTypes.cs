using System;

namespace DynamicNpcs
{
    [Serializable]
    public class ChatMessage
    {
        public string role;
        public string content;

        public ChatMessage() { }

        public ChatMessage(string role, string content)
        {
            this.role = role;
            this.content = content;
        }
    }

    [Serializable]
    public class ChatRequest
    {
        public string model;
        public ChatMessage[] messages;
        public bool stream;
        public float temperature = 0.8f;
        public int max_tokens = 200;
    }

    // --- response DTOs (parsed with JsonUtility) ---

    [Serializable]
    internal class ChatResponse
    {
        public ChatChoice[] choices;
    }

    [Serializable]
    internal class ChatChoice
    {
        public ChatMessage message;
    }

    [Serializable]
    internal class StreamResponse
    {
        public StreamChoice[] choices;
    }

    [Serializable]
    internal class StreamChoice
    {
        public StreamDelta delta;
        public string finish_reason;
    }

    [Serializable]
    internal class StreamDelta
    {
        public string content;
    }
}
