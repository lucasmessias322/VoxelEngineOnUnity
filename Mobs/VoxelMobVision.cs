using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class VoxelMobVision : MonoBehaviour
{
    [Header("Vision")]
    [SerializeField, Min(0.1f)] private float viewDistance = 12f;
    [SerializeField, Range(1f, 360f)] private float viewAngle = 120f;
    [SerializeField] private Vector3 eyeOffset = new Vector3(0f, 1.2f, 0f);
    [SerializeField, Min(0f)] private float verticalTolerance = 4f;
    [SerializeField, Min(0.02f)] private float scanInterval = 0.2f;

    [Header("Targets")]
    [SerializeField] private LayerMask targetLayers = ~0;
    [SerializeField] private bool filterByTags = true;
    [SerializeField] private string[] targetTags = { "Player", "Mob" };
    [SerializeField] private QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore;
    [SerializeField, Min(1)] private int maxDetectedColliders = 32;

    [Header("Line Of Sight")]
    [SerializeField] private World world;
    [SerializeField] private bool useVoxelLineOfSight = true;
    [SerializeField] private bool loadedChunksOnly = true;
    [SerializeField] private bool blockVisionOnUnloadedChunks = true;

    [Header("Debug")]
    [SerializeField] private bool drawVisionGizmos = true;
    [SerializeField] private Color visionConeColor = new Color(1f, 0.82f, 0.1f, 0.35f);
    [SerializeField] private Color visibleTargetColor = new Color(0.1f, 1f, 0.25f, 0.9f);
    [SerializeField] private Color blockedTargetColor = new Color(1f, 0.12f, 0.08f, 0.65f);

    private readonly List<Transform> visibleTargets = new List<Transform>(16);
    private readonly List<Vector3> visibleTargetPoints = new List<Vector3>(16);
    private Collider[] overlapResults;
    private float nextScanTime;

    public IReadOnlyList<Transform> VisibleTargets => visibleTargets;
    public Transform ClosestVisibleTarget { get; private set; }
    public bool HasVisibleTarget => ClosestVisibleTarget != null;
    public float ViewDistance => viewDistance;
    public float ViewAngle => viewAngle;

    private void Awake()
    {
        if (world == null)
            world = World.Instance;

        EnsureOverlapBuffer();
        nextScanTime = Time.time + Random.Range(0f, scanInterval);
    }

    private void OnValidate()
    {
        viewDistance = Mathf.Max(0.1f, viewDistance);
        verticalTolerance = Mathf.Max(0f, verticalTolerance);
        scanInterval = Mathf.Max(0.02f, scanInterval);
        maxDetectedColliders = Mathf.Max(1, maxDetectedColliders);
    }

    private void Update()
    {
        if (Time.time < nextScanTime)
            return;

        nextScanTime = Time.time + scanInterval;
        ScanNow();
    }

    public void ScanNow()
    {
        EnsureOverlapBuffer();

        if (world == null)
            world = World.Instance;

        visibleTargets.Clear();
        visibleTargetPoints.Clear();
        ClosestVisibleTarget = null;

        Vector3 eyePosition = GetEyePosition();
        int hitCount = Physics.OverlapSphereNonAlloc(
            eyePosition,
            viewDistance,
            overlapResults,
            targetLayers,
            triggerInteraction);

        float closestDistanceSqr = float.PositiveInfinity;
        for (int i = 0; i < hitCount; i++)
        {
            Collider candidateCollider = overlapResults[i];
            if (candidateCollider == null)
                continue;

            Transform target = ResolveTargetTransform(candidateCollider.transform);
            if (target == null || target == transform || target.IsChildOf(transform))
                continue;

            if (visibleTargets.Contains(target))
                continue;

            if (!TargetPassesTagFilter(target, candidateCollider.transform))
                continue;

            Vector3 targetPoint = candidateCollider.bounds.center;
            if (!IsPointInsideVision(targetPoint))
                continue;

            if (useVoxelLineOfSight && IsVoxelLineBlocked(eyePosition, targetPoint))
                continue;

            visibleTargets.Add(target);
            visibleTargetPoints.Add(targetPoint);

            float distanceSqr = (targetPoint - eyePosition).sqrMagnitude;
            if (distanceSqr < closestDistanceSqr)
            {
                closestDistanceSqr = distanceSqr;
                ClosestVisibleTarget = target;
            }
        }
    }

    public bool CanSee(Transform target)
    {
        if (target == null)
            return false;

        return CanSeePoint(target.position);
    }

    public bool CanSeePoint(Vector3 worldPoint)
    {
        Vector3 eyePosition = GetEyePosition();
        if (!IsPointInsideVision(worldPoint))
            return false;

        return !useVoxelLineOfSight || !IsVoxelLineBlocked(eyePosition, worldPoint);
    }

    public bool IsPointInsideVision(Vector3 worldPoint)
    {
        Vector3 eyePosition = GetEyePosition();
        Vector3 toPoint = worldPoint - eyePosition;
        if (toPoint.sqrMagnitude > viewDistance * viewDistance)
            return false;

        if (verticalTolerance > 0f && Mathf.Abs(toPoint.y) > verticalTolerance)
            return false;

        if (viewAngle >= 359.9f)
            return true;

        Vector3 flatForward = new Vector3(transform.forward.x, 0f, transform.forward.z);
        Vector3 flatToPoint = new Vector3(toPoint.x, 0f, toPoint.z);
        if (flatForward.sqrMagnitude <= 0.0001f || flatToPoint.sqrMagnitude <= 0.0001f)
            return true;

        float dot = Vector3.Dot(flatForward.normalized, flatToPoint.normalized);
        float minDot = Mathf.Cos(viewAngle * 0.5f * Mathf.Deg2Rad);
        return dot >= minDot;
    }

    public Vector3 GetEyePosition()
    {
        return transform.position + transform.TransformVector(eyeOffset);
    }

    private void EnsureOverlapBuffer()
    {
        if (overlapResults == null || overlapResults.Length != maxDetectedColliders)
            overlapResults = new Collider[maxDetectedColliders];
    }

    private Transform ResolveTargetTransform(Transform hitTransform)
    {
        if (!filterByTags)
            return hitTransform;

        Transform current = hitTransform;
        while (current != null)
        {
            if (HasAllowedTag(current))
                return current;

            current = current.parent;
        }

        return null;
    }

    private bool TargetPassesTagFilter(Transform target, Transform hitTransform)
    {
        if (!filterByTags)
            return true;

        return HasAllowedTag(target) || HasAllowedTag(hitTransform);
    }

    private bool HasAllowedTag(Transform candidate)
    {
        if (candidate == null || targetTags == null || targetTags.Length == 0)
            return false;

        string candidateTag = candidate.gameObject.tag;
        for (int i = 0; i < targetTags.Length; i++)
        {
            string targetTag = targetTags[i];
            if (!string.IsNullOrEmpty(targetTag) && candidateTag == targetTag)
                return true;
        }

        return false;
    }

    private bool IsVoxelLineBlocked(Vector3 start, Vector3 end)
    {
        if (world == null)
            return false;

        Vector3 delta = end - start;
        float maxDistance = delta.magnitude;
        if (maxDistance <= 0.001f)
            return false;

        Vector3 direction = delta / maxDistance;
        Vector3Int current = WorldToCell(start);
        Vector3Int startCell = current;
        Vector3Int endCell = WorldToCell(end);

        int stepX = GetStep(direction.x);
        int stepY = GetStep(direction.y);
        int stepZ = GetStep(direction.z);

        float tMaxX = GetInitialRayT(start.x, current.x, direction.x, stepX);
        float tMaxY = GetInitialRayT(start.y, current.y, direction.y, stepY);
        float tMaxZ = GetInitialRayT(start.z, current.z, direction.z, stepZ);

        float tDeltaX = GetRayTDelta(direction.x);
        float tDeltaY = GetRayTDelta(direction.y);
        float tDeltaZ = GetRayTDelta(direction.z);
        float traveled = 0f;

        while (traveled <= maxDistance)
        {
            if (current != startCell && current != endCell && IsVisionBlockingBlock(current))
                return true;

            if (current == endCell)
                return false;

            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                current.x += stepX;
                traveled = tMaxX;
                tMaxX += tDeltaX;
            }
            else if (tMaxY <= tMaxZ)
            {
                current.y += stepY;
                traveled = tMaxY;
                tMaxY += tDeltaY;
            }
            else
            {
                current.z += stepZ;
                traveled = tMaxZ;
                tMaxZ += tDeltaZ;
            }
        }

        return false;
    }

    private bool IsVisionBlockingBlock(Vector3Int cell)
    {
        if (loadedChunksOnly)
        {
            if (!world.TryGetLoadedBlockAt(cell, out BlockType loadedBlockType))
                return blockVisionOnUnloadedChunks;

            return world.IsSolidBlock(loadedBlockType) && !world.IsLiquidBlock(loadedBlockType);
        }

        BlockType blockType = world.GetBlockAt(cell);
        return world.IsSolidBlock(blockType) && !world.IsLiquidBlock(blockType);
    }

    private static Vector3Int WorldToCell(Vector3 position)
    {
        return new Vector3Int(
            Mathf.FloorToInt(position.x),
            Mathf.FloorToInt(position.y),
            Mathf.FloorToInt(position.z));
    }

    private static int GetStep(float value)
    {
        if (value > 0f)
            return 1;

        if (value < 0f)
            return -1;

        return 0;
    }

    private static float GetInitialRayT(float start, int cell, float direction, int step)
    {
        if (step == 0)
            return float.PositiveInfinity;

        float nextBoundary = step > 0 ? cell + 1f : cell;
        return (nextBoundary - start) / direction;
    }

    private static float GetRayTDelta(float direction)
    {
        return Mathf.Abs(direction) <= 0.000001f ? float.PositiveInfinity : Mathf.Abs(1f / direction);
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawVisionGizmos)
            return;

        Vector3 eyePosition = GetEyePosition();
        Vector3 flatForward = new Vector3(transform.forward.x, 0f, transform.forward.z);
        if (flatForward.sqrMagnitude <= 0.0001f)
            flatForward = Vector3.forward;

        DrawVisionConeGizmo(eyePosition, flatForward.normalized);

        if (!Application.isPlaying)
            return;

        for (int i = 0; i < visibleTargetPoints.Count; i++)
        {
            Gizmos.color = visibleTargetColor;
            Gizmos.DrawLine(eyePosition, visibleTargetPoints[i]);
            Gizmos.DrawSphere(visibleTargetPoints[i], 0.12f);
        }
    }

    private void DrawVisionConeGizmo(Vector3 eyePosition, Vector3 forward)
    {
        Gizmos.color = visionConeColor;
        Gizmos.DrawWireSphere(eyePosition, 0.12f);

        if (viewAngle >= 359.9f)
        {
            Gizmos.DrawWireSphere(eyePosition, viewDistance);
            return;
        }

        int segments = 24;
        float halfAngle = viewAngle * 0.5f;
        Vector3 previous = eyePosition + Quaternion.AngleAxis(-halfAngle, Vector3.up) * forward * viewDistance;
        Gizmos.DrawLine(eyePosition, previous);

        for (int i = 1; i <= segments; i++)
        {
            float t = i / (float)segments;
            float angle = Mathf.Lerp(-halfAngle, halfAngle, t);
            Vector3 next = eyePosition + Quaternion.AngleAxis(angle, Vector3.up) * forward * viewDistance;
            Gizmos.DrawLine(previous, next);
            previous = next;
        }

        Gizmos.DrawLine(eyePosition, previous);

        if (!Application.isPlaying)
            return;

        Collider[] debugHits = Physics.OverlapSphere(eyePosition, viewDistance, targetLayers, triggerInteraction);
        for (int i = 0; i < debugHits.Length; i++)
        {
            Collider candidate = debugHits[i];
            if (candidate == null)
                continue;

            Vector3 point = candidate.bounds.center;
            if (IsPointInsideVision(point) && useVoxelLineOfSight && IsVoxelLineBlocked(eyePosition, point))
            {
                Gizmos.color = blockedTargetColor;
                Gizmos.DrawLine(eyePosition, point);
            }
        }
    }
}
