using UnityEngine;

public class AlignChildToViveTarget : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Parent object to move (A)")]
    public Transform parentA;
    [Tooltip("Child (or descendant) of A that must overlap target (B)")]
    public Transform childB;
    [Tooltip("Target to overlap (C) — the sync reference")]
    public Transform targetC;

    public bool useManualAlignment = false;
    public KeyCode alignKey = KeyCode.T;

    [ContextMenu("Align B (descendant) to C (target)")]
    public void AlignNow()
    {
        if (parentA == null || childB == null || targetC == null)
        {
            Debug.LogWarning("Assign Parent A, Child B, and Target C.");
            return;
        }

        var target = targetC;
        if (target == null)
        {
            Debug.LogError("[AlignChildToTarget] targetC has no RuntimeObject.");
            return;
        }

        if (!childB.IsChildOf(parentA))
        {
            Debug.LogWarning("[AlignChildToTarget] B is not a descendant of A.");
            return;
        }

        // B's local pose relative to A
        Vector3 localPosOfB = parentA.InverseTransformPoint(childB.position);
        Quaternion localRotOfB = Quaternion.Inverse(parentA.rotation) * childB.rotation;

        // Desired world pose of A so that B lands exactly on C
        Quaternion desiredRot = target.transform.rotation * Quaternion.Inverse(localRotOfB);
        Vector3 desiredPos = target.transform.position - desiredRot * localPosOfB;

        parentA.SetPositionAndRotation(desiredPos, desiredRot);
        Debug.Log("[AlignChildToTarget] Alignment complete: B now overlaps C.");
    }

    private void Update()
    {
        if (useManualAlignment && Input.GetKeyDown(alignKey))
            AlignNow();
    }
}
