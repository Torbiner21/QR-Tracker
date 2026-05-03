#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(TrackerDeviceMount))]
public class TrackerDeviceMountEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var mount = (TrackerDeviceMount)target;

        EditorGUILayout.Space(6);

        GUI.backgroundColor = mount.HasCapture ? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.85f, 0.3f);

        if (GUILayout.Button(mount.HasCapture
                ? "✓  Re-Capture Local Mount Offset"
                : "⚠  Capture Local Mount Offset  (not captured yet)", GUILayout.Height(32)))
        {
            mount.CaptureMount();
        }

        GUI.backgroundColor = Color.white;

        if (!mount.HasCapture)
        {
            EditorGUILayout.HelpBox(
                "Not yet captured!\n\n" +
                "1. Press R to resync the tracker to the real world.\n" +
                "2. In the scene view, drag AmbuMesh until it visually matches the real device.\n" +
                "3. Click 'Capture Local Mount Offset'.\n\n" +
                "AmbuMesh must be a CHILD of the TrackerModel (trackerTarget).",
                MessageType.Warning);
        }
        else
        {
            EditorGUILayout.HelpBox(
                "Local offset captured.\n\n" +
                "If the mesh faces the wrong direction:\n" +
                "→ Adjust 'Rotation Correction' (Y = spin, X = tilt, Z = roll).\n" +
                "This rotates around the tracker attachment point — position will NOT drift.",
                MessageType.Info);

            GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
            if (GUILayout.Button("Clear Mount Offset"))
                mount.ClearMount();
            GUI.backgroundColor = Color.white;
        }
    }
}
#endif
