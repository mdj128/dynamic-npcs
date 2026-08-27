# Basic NPC Chat sample

1. Run **Window > Dynamic NPCs > Embedded Server Setup** once and complete it: download a
   llama-server build, pick a dialogue GGUF, and set up the NeuTTS section (backbone GGUF,
   espeak-ng, Inference Engine + codec decoder). See the package README for detail.
2. Create the config assets: a `DynamicNpcSettings` (point the setup window at it), an
   `NpcVoice` (apply a starter voice, or bake your own from a sample), and an `NpcPersona`
   (assign the voice).
3. In a scene, create an empty GameObject and add **NPC Dialogue Agent** (an AudioSource is
   added automatically). Assign the settings and persona assets.
4. Add `NpcChatDemo` to any GameObject and assign the agent (or leave it empty to auto-find).
5. Press Play, type in the chat box at the bottom of the Game view, and press Enter. The
   servers start lazily on the first line, so expect a few seconds of model load before the
   first reply; later lines are much faster.

Using the remote dev backends instead? Point `Llm Base Url` at Ollama or LM Studio and
`Tts Base Url` at an XTTS server, and make sure both are running before you press Play.
