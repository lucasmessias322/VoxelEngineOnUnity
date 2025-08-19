using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class BlockHighlighter : MonoBehaviour
{
    private LineRenderer lr;

    public void HighlightCube(Vector3 center, Vector3 size, Color color)
    {
        if (lr == null)
        {
            lr = GetComponent<LineRenderer>();
            lr.loop = false;
            lr.useWorldSpace = true;
            lr.widthMultiplier = 0.02f;
            lr.positionCount = 16; // 12 are enough, but we'll duplicate some to close lines cleanly
        }

        lr.startColor = color;
        lr.endColor = color;

        Vector3 half = size * 0.5f;

        // corners
        Vector3 p0 = center + new Vector3(-half.x, -half.y, -half.z);
        Vector3 p1 = center + new Vector3(half.x, -half.y, -half.z);
        Vector3 p2 = center + new Vector3(half.x, -half.y, half.z);
        Vector3 p3 = center + new Vector3(-half.x, -half.y, half.z);

        Vector3 p4 = center + new Vector3(-half.x, half.y, -half.z);
        Vector3 p5 = center + new Vector3(half.x, half.y, -half.z);
        Vector3 p6 = center + new Vector3(half.x, half.y, half.z);
        Vector3 p7 = center + new Vector3(-half.x, half.y, half.z);

        // edges in sequence
        Vector3[] points = new Vector3[]
        {
            p0,p1,p2,p3,p0, // bottom
            p4,p5,p6,p7,p4, // top
            p5,p1,p2,p6,p7,p3 // verticals
        };

        lr.positionCount = points.Length;
        lr.SetPositions(points);
    }

    public void Hide()
    {
        if (lr != null) lr.positionCount = 0;
    }
}
