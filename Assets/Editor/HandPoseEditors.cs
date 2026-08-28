// Place this file inside a folder named "Editor".
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(HandPoseRecorder))]
public sealed class HandPoseRecorderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var recorder = (HandPoseRecorder)target;

        EditorGUILayout.Space();

        string blocker = Blocker(recorder);

        // The button stays on screen even when it cannot be pressed. A control
        // that disappears reads as a missing feature; a greyed one with a reason
        // underneath reads as a prerequisite.
        if (recorder.CurrentPhase == HandPoseRecorder.Phase.Idle)
        {
            using (new EditorGUI.DisabledScope(blocker != null))
            {
                if (GUILayout.Button("Record Pose", GUILayout.Height(34)))
                    recorder.StartRecording();
            }

            if (blocker != null)
            {
                EditorGUILayout.HelpBox(blocker, MessageType.Info);
                DrawLastRecording(recorder);
                return;
            }
        }

        switch (recorder.CurrentPhase)
        {
            case HandPoseRecorder.Phase.Idle:
                EditorGUILayout.LabelField("Reading via", recorder.SourceDescription);
                break;

            case HandPoseRecorder.Phase.LeadIn:
                DrawProgress(recorder.Progress, "Get into the pose…");

                if (GUILayout.Button("Cancel"))
                    recorder.CancelRecording();
                break;

            case HandPoseRecorder.Phase.Recording:
                DrawProgress(
                    recorder.Progress,
                    $"Hold steady — {recorder.SampleCount} samples" +
                    (recorder.RejectedSamples > 0 ? $", {recorder.RejectedSamples} rejected" : string.Empty)
                );

                if (GUILayout.Button("Cancel"))
                    recorder.CancelRecording();
                break;
        }

        DrawLastRecording(recorder);
    }

    /// <summary>Why the button cannot be pressed, or null when it can.</summary>
    private static string Blocker(HandPoseRecorder recorder)
    {
        if (!Application.isPlaying)
        {
            return "Recording samples a live hand, so it needs Play Mode. Enter " +
                   "Play Mode, hold the pose through the lead-in, and keep it " +
                   "steady until sampling ends.";
        }

        if (recorder.Target == null)
            return "Assign a HandPose asset to record into.";

        if (!recorder.CanRecord)
            return "Assign either an IHand (usually a HandRef) or a FingerFeatureStateDebugger to read from.";

        return null;
    }

    private static void DrawLastRecording(HandPoseRecorder recorder)
    {
        if (recorder.Target == null || string.IsNullOrEmpty(recorder.Target.RecordedAt))
            return;

        EditorGUILayout.HelpBox(
            $"\"{recorder.Target.Key}\" last recorded {recorder.Target.RecordedAt} " +
            $"({recorder.Target.EnabledCount} of {recorder.Target.Constraints.Count} constraints enabled).",
            MessageType.None
        );
    }

    private static void DrawProgress(float progress, string label)
    {
        Rect rect = GUILayoutUtility.GetRect(18f, 22f);
        EditorGUI.ProgressBar(rect, progress, label);
    }

    public override bool RequiresConstantRepaint() => Application.isPlaying;
}

[CustomEditor(typeof(HandPoseRecognizer))]
public sealed class HandPoseRecognizerEditor : Editor
{
    private static readonly Color MetColor = new Color(0.35f, 0.8f, 0.4f);
    private static readonly Color FailedColor = new Color(0.9f, 0.45f, 0.4f);
    private static readonly Color UnreadableColor = new Color(0.65f, 0.6f, 0.35f);

    private readonly Dictionary<string, bool> expanded = new Dictionary<string, bool>();
    private readonly List<HandPose.ConstraintEvaluation> scratch = new List<HandPose.ConstraintEvaluation>();

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var recognizer = (HandPoseRecognizer)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play Mode to see live pose evaluation.", MessageType.Info);
            return;
        }

        EditorGUILayout.LabelField("Reading via", recognizer.SourceDescription);

        // Separates "no pose matched" from "no values are arriving at all".
        if (!recognizer.SourceIsReading)
        {
            EditorGUILayout.HelpBox(
                "The source is not producing any readings. Every pose will fail " +
                "regardless of its constraints — fix the source before reading " +
                "anything below.",
                MessageType.Error
            );
        }

        EditorGUILayout.LabelField(
            "Active",
            string.IsNullOrEmpty(recognizer.ActiveKey)
                ? "—"
                : $"{recognizer.ActiveKey}  ({recognizer.ActiveScore:F2})"
        );

        EditorGUILayout.Space(4f);

        if (recognizer.Poses.Count == 0)
        {
            EditorGUILayout.LabelField("No poses assigned.");
            return;
        }

        foreach (HandPose pose in recognizer.Poses)
        {
            if (pose == null)
                continue;

            DrawPose(recognizer, pose);
        }
    }

    private void DrawPose(HandPoseRecognizer recognizer, HandPose pose)
    {
        recognizer.DebugEvaluate(
            pose,
            scratch,
            out float score,
            out bool constraintsMet,
            out bool handednessMet
        );

        bool held = recognizer.ActivePose == pose;
        float threshold = pose.ConfidenceThreshold(held);
        bool scoreMet = score >= threshold;
        bool matching = constraintsMet && handednessMet && scoreMet;

        string id = pose.GetInstanceID().ToString();
        expanded.TryGetValue(id, out bool open);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            string header =
                $"{(string.IsNullOrEmpty(pose.Key) ? pose.name : pose.Key)}   " +
                $"{score:F2} / {threshold:F2}   {(matching ? "MATCH" : "no")}";

            Color previous = GUI.contentColor;
            GUI.contentColor = matching ? MetColor : previous;
            open = EditorGUILayout.Foldout(open, header, true, EditorStyles.foldout);
            GUI.contentColor = previous;

            expanded[id] = open;

            if (!open)
                return;

            if (scratch.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No enabled constraints. Record the pose, or tick constraints " +
                    "on the asset manually.",
                    MessageType.Warning
                );

                return;
            }

            if (!handednessMet)
            {
                EditorGUILayout.HelpBox(
                    $"Handedness mismatch: recorded on {pose.RecordedHandedness}, and " +
                    "this pose does not accept either hand.",
                    MessageType.Warning
                );
            }

            if (constraintsMet && !scoreMet)
            {
                EditorGUILayout.HelpBox(
                    $"Every constraint is satisfied but the score {score:F2} is under the " +
                    $"required {threshold:F2}. Lower requiredConfidence on the asset.",
                    MessageType.Warning
                );
            }

            foreach (HandPose.ConstraintEvaluation evaluation in scratch)
                DrawConstraint(evaluation);
        }
    }

    private static void DrawConstraint(HandPose.ConstraintEvaluation evaluation)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(
                $"{evaluation.Finger}.{evaluation.Feature}",
                EditorStyles.miniLabel,
                GUILayout.Width(130)
            );

            Color previous = GUI.contentColor;

            if (!evaluation.Readable)
            {
                GUI.contentColor = UnreadableColor;
                GUILayout.Label("unreadable", EditorStyles.miniLabel, GUILayout.Width(70));
                GUI.contentColor = previous;

                GUILayout.Label(
                    $"[{evaluation.EffectiveMin:F1} … {evaluation.EffectiveMax:F1}]",
                    EditorStyles.miniLabel
                );

                return;
            }

            GUI.contentColor = evaluation.Met ? MetColor : FailedColor;

            GUILayout.Label(
                evaluation.Met ? "✓" : "✗",
                EditorStyles.miniBoldLabel,
                GUILayout.Width(16)
            );

            GUILayout.Label(
                evaluation.Value.ToString("F2"),
                EditorStyles.miniBoldLabel,
                GUILayout.Width(56)
            );

            GUI.contentColor = previous;

            GUILayout.Label(
                $"[{evaluation.EffectiveMin:F2} … {evaluation.EffectiveMax:F2}]   d={evaluation.Distance:F2}",
                EditorStyles.miniLabel
            );
        }
    }

    public override bool RequiresConstantRepaint() => Application.isPlaying;
}
#endif