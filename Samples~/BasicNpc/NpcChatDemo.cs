using UnityEngine;
using DynamicNpcs;

/// <summary>
/// Minimal on-screen chat box for talking to an NpcDialogueAgent.
/// Add to any GameObject, assign the agent, press Play, type, hit Enter.
/// </summary>
public class NpcChatDemo : MonoBehaviour
{
    public NpcDialogueAgent agent;

    private string _input = "";
    private string _subtitle = "";
    private string _status = "";

    private void OnEnable()
    {
        if (agent == null)
            agent = FindObjectOfType<NpcDialogueAgent>();
        if (agent != null)
        {
            agent.onSentenceStarted.AddListener(s => _subtitle = s);
            agent.onSpeechFinished.AddListener(() => _subtitle = "");
            agent.onError.AddListener(e => _status = "Error: " + e);
        }
    }

    private void OnGUI()
    {
        const int w = 560;
        GUILayout.BeginArea(new Rect(10, Screen.height - 130, w, 120), GUI.skin.box);

        GUILayout.Label(agent != null && agent.IsBusy ? "..." : _status);
        GUILayout.Label(_subtitle, GUI.skin.box, GUILayout.Width(w - 20));

        GUILayout.BeginHorizontal();
        GUI.SetNextControlName("chat");
        _input = GUILayout.TextField(_input, GUILayout.Width(w - 90));

        bool submit = GUILayout.Button("Say", GUILayout.Width(60)) ||
                      (Event.current.type == EventType.KeyDown &&
                       Event.current.keyCode == KeyCode.Return &&
                       GUI.GetNameOfFocusedControl() == "chat");

        if (submit && !string.IsNullOrWhiteSpace(_input) && agent != null && !agent.IsBusy)
        {
            _status = "You: " + _input;
            agent.Ask(_input);
            _input = "";
        }
        GUILayout.EndHorizontal();

        GUILayout.EndArea();
    }
}
