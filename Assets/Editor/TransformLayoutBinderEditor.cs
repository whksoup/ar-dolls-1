// Place this file inside a folder named "Editor".
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(TransformLayoutBinder))]
public sealed class TransformLayoutBinderEditor : Editor
{
    private string spawnKey = string.Empty;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var binder = (TransformLayoutBinder)target;

        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Save Layout", GUILayout.Height(26)))
                binder.SaveLayout();

            if (GUILayout.Button("Apply Layout", GUILayout.Height(26)))
                binder.ApplyLayout();
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            spawnKey = EditorGUILayout.TextField("Spawn Key", spawnKey);

            using (new EditorGUI.DisabledScope(binder.Catalogue == null))
            {
                if (GUILayout.Button("Spawn", GUILayout.Width(80)))
                {
                    GameObject spawned = binder.Spawn(spawnKey);

                    if (spawned != null)
                    {
                        Undo.RegisterCreatedObjectUndo(spawned, "Spawn Layout Member");
                        Selection.activeGameObject = spawned;
                    }
                }
            }
        }

        EditorGUILayout.HelpBox(
            Application.isPlaying
                ? "Play Mode: Save writes into the asset, which is not reverted on exit. " +
                  "Arrange and spawn objects in the headset, hit Save, stop playing, then " +
                  "hit Apply to reproduce it all in the scene."
                : "Edit Mode: Apply rebuilds the hierarchy from the layout — spawning what is " +
                  "missing, pruning members that were removed — undoably. Mirror clones are " +
                  "excluded and regenerate themselves.",
            MessageType.Info
        );

        if (GUILayout.Button("Reveal Runtime Override Folder"))
            EditorUtility.RevealInFinder(Application.persistentDataPath);
    }
}
#endif