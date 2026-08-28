// Place this file inside a folder named "Editor".
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Oculus.Interaction.Input;
using Oculus.Interaction.PoseDetection;

/// <summary>
/// Draws a finger x feature grid under the default inspector: one row per
/// watched finger, one column per watched feature, each cell showing the
/// current state name and raw value. Repaints continuously in Play Mode so it
/// reads live without needing to click back into the inspector.
/// </summary>
[CustomEditor(typeof(FingerFeatureStateDebugger))]
public sealed class FingerFeatureStateDebuggerEditor : Editor
{
    private static readonly Color DefinedColor = new Color(0.24f, 0.24f, 0.24f);
    private static readonly Color UndefinedColor = new Color(0.15f, 0.15f, 0.15f);
    private static readonly Color ActiveStateColor = new Color(0.16f, 0.4f, 0.2f);

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var debugger = (FingerFeatureStateDebugger)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Live Readout", EditorStyles.boldLabel);

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "Enter Play Mode to see live finger states.",
                MessageType.Info
            );

            return;
        }

        if (!debugger.HasPolled)
        {
            EditorGUILayout.HelpBox(
                "No data yet — check that a state provider is assigned.",
                MessageType.Warning
            );

            return;
        }

        DrawGrid(debugger);
    }

    private static void DrawGrid(FingerFeatureStateDebugger debugger)
    {
        float labelWidth = 70f;
        float cellWidth = 108f;

        // Header row.
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(string.Empty, GUILayout.Width(labelWidth));

            foreach (FingerFeature feature in FingerFeatureStateDebugger.AllFeatures)
            {
                if (!debugger.IsFeatureWatched(feature))
                    continue;

                GUILayout.Label(feature.ToString(), EditorStyles.miniBoldLabel, GUILayout.Width(cellWidth));
            }
        }

        foreach (HandFinger finger in FingerFeatureStateDebugger.AllFingers)
        {
            if (!debugger.IsFingerWatched(finger))
                continue;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(finger.ToString(), EditorStyles.boldLabel, GUILayout.Width(labelWidth));

                foreach (FingerFeature feature in FingerFeatureStateDebugger.AllFeatures)
                {
                    if (!debugger.IsFeatureWatched(feature))
                        continue;

                    DrawCell(debugger, finger, feature, cellWidth);
                }
            }
        }
    }

    private static void DrawCell(
        FingerFeatureStateDebugger debugger,
        HandFinger finger,
        FingerFeature feature,
        float width)
    {
        bool hasSnapshot =
            debugger.TryGetSnapshot(finger, feature, out FingerFeatureStateDebugger.FingerFeatureSnapshot snapshot) &&
            snapshot.HasValue;

        Color background = !hasSnapshot
            ? UndefinedColor
            : string.IsNullOrEmpty(snapshot.State) ? DefinedColor : ActiveStateColor;

        Rect rect = GUILayoutUtility.GetRect(width, 34f, GUILayout.Width(width));
        EditorGUI.DrawRect(rect, background);

        if (!hasSnapshot)
        {
            GUI.Label(rect, "n/a", CenteredStyle());
            return;
        }

        string stateText = string.IsNullOrEmpty(snapshot.State) ? "-" : snapshot.State;
        string text = $"{stateText}\n{snapshot.Value:F2}";

        GUI.Label(rect, text, CenteredStyle());
    }

    private static GUIStyle centeredStyle;

    private static GUIStyle CenteredStyle()
    {
        if (centeredStyle == null)
        {
            centeredStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = Color.white }
            };
        }

        return centeredStyle;
    }

    public override bool RequiresConstantRepaint() => Application.isPlaying;
}
#endif
