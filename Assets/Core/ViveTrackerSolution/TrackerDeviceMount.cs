using UnityEngine;

/// <summary>
/// Place this on the device MESH (e.g. AmbuMesh) that is a CHILD of the tracked object.
///
/// HIERARCHY EXPECTED:
///   AmbuContainer              ← OpenVrTrackerReader lives here
///     └─ TrackerModel          ← trackerTarget on the reader (gets moved automatically)
///          └─ AmbuMesh         ← put THIS component here
///
/// HOW IT WORKS:
///   OpenVrTrackerReader moves TrackerModel every frame.
///   Because AmbuMesh is a CHILD, it inherits that movement — no extra script needed.
///   This component ONLY locks the local position/rotation offset of the mesh
///   relative to the TrackerModel. You capture it once, it is saved in the scene.
///
/// DO NOT need to assign trackerReader — movement comes from child inheritance.
///
/// SETUP (once per device per mounting position):
///   Step 1 – Press R to Resync the tracker (world sync as normal).
///   Step 2 – In the scene view, move/rotate AmbuMesh until it visually lines up
///             with the real-world device position and orientation.
///   Step 3 – Right-click this component → "Capture Local Mount Offset"
///             (or press M at runtime).
///   Step 4 – Done. The local offset is saved into the scene.
///             AmbuMesh will always sit in the same relationship to TrackerModel.
///
/// ADDING MORE DEVICES:
///   Each device mesh just needs its own TrackerDeviceMount component.
///   No shared state, fully independent per-device.
/// </summary>
public class TrackerDeviceMount : MonoBehaviour
{
    [Header("Capture Key (runtime)")]
    public KeyCode captureKey = KeyCode.M;

    [Header("Status")]
    [SerializeField] private bool hasCapturedMount = false;

    [Header("Captured Local Offset  (auto-filled — do not edit manually)")]
    [SerializeField] private Vector3    capturedLocalPosition = Vector3.zero;
    [SerializeField] private Quaternion capturedLocalRotation = Quaternion.identity;

    [Header("Facing Correction  (tweak after capture — rotates around tracker attachment point)")]
    [Tooltip("Rotates the mesh around the tracker's own origin (the attachment point), NOT around " +
             "the mesh pivot. This means adjusting these values will never shift the position — " +
             "only facing changes. Use Y to spin left/right, X to tilt forward/back, Z to roll.")]
    public Vector3 rotationCorrection = Vector3.zero;

    public bool HasCapture => hasCapturedMount;

    // ─────────────────────────────────────────────────────────

    private void Update()
    {
        if (Input.GetKeyDown(captureKey))
            CaptureMount();

        // Lock the local offset every frame so scene edits don't drift at runtime
        if (hasCapturedMount)
        {
            // Apply rotationCorrection around the TRACKER'S origin (parent = 0,0,0),
            // not the mesh pivot. Rotating both the position vector and the rotation
            // by the same quaternion keeps the mesh pinned to the attachment point.
            var correction = Quaternion.Euler(rotationCorrection);
            transform.localPosition = correction * capturedLocalPosition;
            transform.localRotation = correction * capturedLocalRotation;
        }
    }

    // ── Public API ────────────────────────────────────────────

    /// <summary>
    /// Records the current localPosition and localRotation of this mesh
    /// relative to its parent (TrackerModel). Call once when the mesh is
    /// correctly aligned to the real device in the scene view.
    /// </summary>
    [ContextMenu("Capture Local Mount Offset")]
    public void CaptureMount()
    {
        capturedLocalPosition = transform.localPosition;
        capturedLocalRotation = transform.localRotation;
        hasCapturedMount      = true;

        Debug.Log($"[TrackerDeviceMount] ✓ Captured local offset for '{gameObject.name}'. " +
                  $"localPos={capturedLocalPosition}  localRot={capturedLocalRotation.eulerAngles}", this);

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    /// <summary>Clears the captured offset. Use if you physically remount the tracker.</summary>
    [ContextMenu("Clear Mount Offset")]
    public void ClearMount()
    {
        capturedLocalPosition = Vector3.zero;
        capturedLocalRotation = Quaternion.identity;
        hasCapturedMount      = false;
        Debug.Log($"[TrackerDeviceMount] Mount cleared on '{gameObject.name}'.", this);
    }

    // ── Gizmos ────────────────────────────────────────────────
    private void OnDrawGizmos() => DrawAxisGizmos(transform, 0.25f, hasCapturedMount);
    private void OnDrawGizmosSelected() => DrawAxisGizmos(transform, 0.35f, hasCapturedMount);

    private static void DrawAxisGizmos(Transform t, float len, bool captured)
    {
        if (t == null) return;
        Vector3 pos = t.position;
        float r = len * 0.1f;

        // Forward – blue
        Gizmos.color = Color.blue;
        Gizmos.DrawLine(pos, pos + t.forward * len);
        Gizmos.DrawSphere(pos + t.forward * len, r);

        // Up – green
        Gizmos.color = Color.green;
        Gizmos.DrawLine(pos, pos + t.up * len);
        Gizmos.DrawSphere(pos + t.up * len, r);

        // Right – red
        Gizmos.color = Color.red;
        Gizmos.DrawLine(pos, pos + t.right * len);
        Gizmos.DrawSphere(pos + t.right * len, r);

        // Origin – yellow
        Gizmos.color = Color.yellow;
        Gizmos.DrawSphere(pos, r * 1.4f);

#if UNITY_EDITOR
        UnityEditor.Handles.Label(pos + t.forward * len, " F");
        UnityEditor.Handles.Label(pos + t.up      * len, " U");
        UnityEditor.Handles.Label(pos + t.right   * len, " R");
        UnityEditor.Handles.Label(pos + t.up * (len + 0.06f),
            captured ? "[CAPTURED]" : "[NOT CAPTURED - align me then capture]");
#endif
    }
}
