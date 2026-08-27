using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DynamicNpcs.Editor
{
    /// <summary>
    /// Adds NeuTTS reference management to the NpcVoice inspector: shows baked-code
    /// status, imports reference JSONs (from Tools~/bake_neutts_voice.py), and applies
    /// the starter voices bundled with the package.
    /// </summary>
    [CustomEditor(typeof(NpcVoice))]
    public class NpcVoiceEditor : UnityEditor.Editor
    {
        private const string StarterVoicesFolder = "Packages/com.mdj.dynamicnpcs/Editor/StarterVoices";

        [Serializable]
        private class VoiceReferenceJson
        {
            public string name;
            public string transcript;
            public int[] codes;
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var voice = (NpcVoice)target;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("NeuTTS reference (embedded TTS)", EditorStyles.boldLabel);

            if (voice.HasNeuttsReference)
            {
                EditorGUILayout.LabelField(
                    "Baked codes",
                    $"{voice.neuttsRefCodes.Length} ({voice.neuttsRefCodes.Length / 50f:0.0}s of reference audio)");
                if (string.IsNullOrWhiteSpace(voice.sampleTranscript))
                    EditorGUILayout.HelpBox("Sample Transcript is empty - NeuTTS needs the exact words spoken in the reference.", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "No baked reference codes. The embedded NeuTTS backend needs them for voice cloning. " +
                    "Set Sample Transcript to the exact words spoken in your sample, then click " +
                    "'Bake From Sample' (one-time, needs Python on this machine only - players never do). " +
                    "Or apply a bundled starter voice below.",
                    MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(VoiceBaker.IsBaking))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Bake From Sample (needs Python)..."))
                    BakeFromSample(voice);

                if (GUILayout.Button("Import NeuTTS Reference (JSON)..."))
                    ImportJson(voice);

                if (GUILayout.Button("Clear"))
                {
                    Undo.RecordObject(voice, "Clear NeuTTS reference");
                    voice.neuttsRefCodes = Array.Empty<int>();
                    EditorUtility.SetDirty(voice);
                }
            }
            if (VoiceBaker.IsBaking || !string.IsNullOrEmpty(VoiceBaker.Status))
                EditorGUILayout.HelpBox(VoiceBaker.Status, VoiceBaker.IsBaking ? MessageType.Info : MessageType.None);

            var starters = AssetDatabase.FindAssets("t:TextAsset", new[] { StarterVoicesFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".json"))
                .ToArray();
            if (starters.Length > 0)
            {
                EditorGUILayout.LabelField("Starter voices (from neuphonic/neutts samples, Apache-2.0):");
                using (new EditorGUILayout.HorizontalScope())
                {
                    foreach (string path in starters)
                    {
                        string label = Path.GetFileNameWithoutExtension(path);
                        if (GUILayout.Button($"Apply '{label}'"))
                            ApplyJson(voice, AssetDatabase.LoadAssetAtPath<TextAsset>(path).text, label);
                    }
                }
            }
        }

        private void BakeFromSample(NpcVoice voice)
        {
            if (string.IsNullOrWhiteSpace(voice.sampleTranscript))
            {
                EditorUtility.DisplayDialog(
                    "Transcript required",
                    "Fill in Sample Transcript first - the exact words spoken in the sample, " +
                    "punctuation included. NeuTTS aligns the reference audio against it.",
                    "OK");
                return;
            }

            // Prefer the assigned sample clip's source file; fall back to the wav path
            // field, then to a file picker.
            string wavPath = null;
            if (voice.sampleClip != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(voice.sampleClip);
                if (!string.IsNullOrEmpty(assetPath))
                    wavPath = Path.GetFullPath(assetPath);
            }
            if (wavPath == null && !string.IsNullOrWhiteSpace(voice.sampleWavPath) && File.Exists(voice.sampleWavPath))
                wavPath = voice.sampleWavPath;
            if (wavPath == null)
            {
                wavPath = EditorUtility.OpenFilePanel("Voice sample (3-15s of clean speech)", "", "wav,mp3,ogg,flac");
                if (string.IsNullOrEmpty(wavPath))
                    return;
            }

            VoiceBaker.BakeAsync(voice, wavPath, (codes, status) =>
            {
                if (voice == null)
                    return;
                Undo.RecordObject(voice, "Bake NeuTTS reference");
                voice.neuttsRefCodes = codes;
                EditorUtility.SetDirty(voice);
                AssetDatabase.SaveAssets();
                Debug.Log($"[DynamicNPCs] Voice '{voice.name}': {status}", voice);
            });
        }

        private void ImportJson(NpcVoice voice)
        {
            string picked = EditorUtility.OpenFilePanel("NeuTTS voice reference JSON", "", "json");
            if (string.IsNullOrEmpty(picked))
                return;
            ApplyJson(voice, File.ReadAllText(picked), Path.GetFileNameWithoutExtension(picked));
        }

        private void ApplyJson(NpcVoice voice, string json, string label)
        {
            VoiceReferenceJson data;
            try
            {
                data = JsonUtility.FromJson<VoiceReferenceJson>(json);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Import failed", $"Could not parse JSON: {e.Message}", "OK");
                return;
            }

            if (data?.codes == null || data.codes.Length == 0)
            {
                EditorUtility.DisplayDialog("Import failed", "JSON contains no 'codes' array.", "OK");
                return;
            }

            Undo.RecordObject(voice, "Import NeuTTS reference");
            voice.neuttsRefCodes = data.codes;
            if (!string.IsNullOrWhiteSpace(data.transcript))
                voice.sampleTranscript = data.transcript.Trim();
            EditorUtility.SetDirty(voice);
            AssetDatabase.SaveAssets();
            Debug.Log($"[DynamicNPCs] Voice '{voice.name}': imported {data.codes.Length} reference codes from '{label}'.", voice);
        }
    }
}
