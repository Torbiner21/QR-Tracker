using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class FinalRectVisualizer : MonoBehaviour
{
    LineRenderer lr;

    void Awake()
    {
        lr = GetComponent<LineRenderer>();
        lr.useWorldSpace = false; // draw in this GO’s local space
    }

    // Draws a closed rectangle in local space
    public void SetRect(Rect r)
    {
        if (!lr) return;

        // 5 points to close the loop p0->p1->p2->p3->p0
        lr.positionCount = 5;
        Vector3 p0 = new(r.xMin, r.yMin, 0);
        Vector3 p1 = new(r.xMax, r.yMin, 0);
        Vector3 p2 = new(r.xMax, r.yMax, 0);
        Vector3 p3 = new(r.xMin, r.yMax, 0);
        lr.SetPosition(0, p0);
        lr.SetPosition(1, p1);
        lr.SetPosition(2, p2);
        lr.SetPosition(3, p3);
        lr.SetPosition(4, p0);
    }
}
