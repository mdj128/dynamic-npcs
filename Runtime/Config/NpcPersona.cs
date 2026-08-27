using System.Text;
using UnityEngine;

namespace DynamicNpcs
{
    /// <summary>
    /// Who an NPC is: personality prompt, voice, and per-NPC LLM tuning.
    /// Create via Assets > Create > Dynamic NPCs > NPC Persona.
    /// </summary>
    [CreateAssetMenu(fileName = "NpcPersona", menuName = "Dynamic NPCs/NPC Persona", order = 2)]
    public class NpcPersona : ScriptableObject
    {
        public string npcName = "Villager";

        [TextArea(4, 14)]
        [Tooltip("Who this character is: personality, speech style, knowledge, secrets, goals.")]
        public string personality =
            "A weary but kind innkeeper who has lived in this village all their life. " +
            "Speaks plainly, enjoys gossip, and worries about the wolves in the northern woods.";

        [TextArea(2, 8)]
        [Tooltip("Optional shared world/lore context appended to the system prompt.")]
        public string worldContext = "";

        [Tooltip("Voice used to speak this persona's lines.")]
        public NpcVoice voice;

        [Header("LLM overrides")]
        [Tooltip("Overrides the model from settings when non-empty.")]
        public string modelOverride = "";

        [Range(0f, 2f)] public float temperature = 0.8f;

        [Tooltip("Cap on tokens per reply. Keep small for snappy spoken lines.")]
        [Min(16)] public int maxTokens = 200;

        [Tooltip("How many user/assistant exchanges to keep in the conversation history.")]
        [Min(1)] public int maxHistoryTurns = 12;

        public string ResolveModel(DynamicNpcSettings settings)
            => string.IsNullOrWhiteSpace(modelOverride) ? settings.llmModel : modelOverride;

        public virtual string BuildSystemPrompt()
        {
            var sb = new StringBuilder();
            sb.Append("You are ").Append(npcName).Append(", a character in a video game.\n");
            sb.Append(personality.Trim());
            if (!string.IsNullOrWhiteSpace(worldContext))
                sb.Append("\n\nWorld context:\n").Append(worldContext.Trim());
            sb.Append("\n\nRules: Stay in character at all times. ");
            sb.Append("Reply ONLY with the exact words ").Append(npcName).Append(" speaks aloud - ");
            sb.Append("no narration, no stage directions, no asterisks, no markdown, no emojis. ");
            sb.Append("Keep replies short and conversational, 1-3 sentences, unless asked to elaborate.");
            return sb.ToString();
        }
    }
}
