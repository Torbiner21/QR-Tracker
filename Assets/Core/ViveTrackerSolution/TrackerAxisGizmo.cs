using UnityEngine;

[AddComponentMenu("SimSync/Tracker Axis Gizmo")]
public class TrackerAxisGizmo : MonoBehaviour
{
    [Header("Gizmo Settings")]
    public float axisLength = 0.2f;
    public float radius = 3f;
    public bool showLabels = true;

    [Header("Build Visibility")]
    public bool showInBuild = true;

    private LineRenderer _lrForward, _lrUp, _lrRight;

    private void Start()
    {
        if (!showInBuild) return;
        _lrForward = CreateLine("Axis_Forward", Color.blue);
        _lrUp = CreateLine("Axis_Up", Color.green);
        _lrRight = CreateLine("Axis_Right", Color.red);
    }

    private void Update()
    {
        if (!showInBuild) return;
        UpdateLine(_lrForward, transform.forward);
        UpdateLine(_lrUp, transform.up);
        UpdateLine(_lrRight, transform.right);
    }

    private LineRenderer CreateLine(string name, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform);
        var lr = go.AddComponent<LineRenderer>();
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = lr.endColor = color;
        lr.startWidth = lr.endWidth = axisLength * 0.04f;
        lr.positionCount = 2;
        lr.useWorldSpace = true;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return lr;
    }

    private void UpdateLine(LineRenderer lr, Vector3 dir)
    {
        lr.SetPosition(0, transform.position);
        lr.SetPosition(1, transform.position + dir * axisLength);
    }

    // Editor-only gizmos still work in scene view
    private void OnDrawGizmos()
    {
        Vector3 pos = transform.position;
        float r = axisLength * 0.08f;

        Gizmos.color = Color.blue;
        Gizmos.DrawLine(pos, pos + transform.forward * axisLength);
        Gizmos.DrawSphere(pos + transform.forward * axisLength, r);

        Gizmos.color = Color.green;
        Gizmos.DrawLine(pos, pos + transform.up * axisLength);
        Gizmos.DrawSphere(pos + transform.up * axisLength, r);

        Gizmos.color = Color.red;
        Gizmos.DrawLine(pos, pos + transform.right * axisLength);
        Gizmos.DrawSphere(pos + transform.right * axisLength, r);

        Gizmos.color = Color.yellow;
        Gizmos.DrawSphere(pos, r * 1.4f);

#if UNITY_EDITOR
        if (showLabels)
        {
            UnityEditor.Handles.Label(pos + transform.forward * axisLength, "F");
            UnityEditor.Handles.Label(pos + transform.up * axisLength, "U");
            UnityEditor.Handles.Label(pos + transform.right * axisLength, "R");
        }
#endif
    }
}