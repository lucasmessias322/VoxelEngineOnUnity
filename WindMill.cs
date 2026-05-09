using UnityEngine;

[DisallowMultipleComponent]
public sealed class WindMill : DynamicVoxelBlock
{
    [Header("Head Rotation")]
    [SerializeField] private Transform rotatingHead;
    [SerializeField] private string rotatingHeadFallbackName = "group2";
    [SerializeField] private Vector3 localRotationAxis = Vector3.forward;
    [SerializeField, Min(0f)] private float degreesPerSecond = 220f;
    [SerializeField] private bool randomizeInitialRotation = true;

    [Header("Animation Optimization")]
    [Tooltip("Para a rotacao visual fora da simulation distance do World sem afetar a geracao de energia.")]
    [SerializeField] private bool disableRotationOutsideSimulationDistance = true;
    [SerializeField, Min(0.05f)] private float rotationSimulationCheckInterval = 0.25f;

    private Transform initialRotationTransform;
    private Quaternion initialLocalRotation;
    private bool hasInitialLocalRotation;
    private bool rotationPausedBySimulationDistance;
    private float nextRotationSimulationCheckTime;
    private float lastVisualRotationTime;

    private void Awake()
    {
        ResolveRotatingHead();
        CacheInitialRotation();
        ResetRotationTiming();
    }

    private void OnEnable()
    {
        ResetRotationTiming();
    }

    private void Update()
    {
        if (!ShouldUpdateVisualRotation())
            return;

        if (rotatingHead == null && !ResolveRotatingHead())
        {
            lastVisualRotationTime = Time.time;
            return;
        }

        if (degreesPerSecond <= 0f)
        {
            lastVisualRotationTime = Time.time;
            return;
        }

        World world = World.Instance;
        if (world == null || !world.CanWindMillGenerateAt(ResolveWindMillBlockPosition()))
        {
            lastVisualRotationTime = Time.time;
            return;
        }

        Vector3 axis = GetRotationAxis();
        if (axis.sqrMagnitude <= 0.0001f)
        {
            lastVisualRotationTime = Time.time;
            return;
        }

        float elapsed = Mathf.Max(0f, Time.time - lastVisualRotationTime);
        lastVisualRotationTime = Time.time;
        if (elapsed <= 0f)
            return;

        float spinDegrees = Mathf.Repeat(degreesPerSecond * elapsed, 360f);
        if (spinDegrees > 0.0001f)
            rotatingHead.Rotate(axis.normalized, spinDegrees, Space.Self);
    }

    protected override void OnDynamicBlockSpawned()
    {
        ResolveRotatingHead();
        CacheInitialRotation();
        ApplyInitialSpinOffset();
        ResetRotationTiming();
    }

    protected override void OnDynamicBlockDespawned()
    {
        if (rotatingHead != null && hasInitialLocalRotation)
            rotatingHead.localRotation = initialLocalRotation;

        ResetRotationTiming();
    }

    private bool ShouldUpdateVisualRotation()
    {
        if (!disableRotationOutsideSimulationDistance)
        {
            rotationPausedBySimulationDistance = false;
            return true;
        }

        if (Time.time < nextRotationSimulationCheckTime)
            return !rotationPausedBySimulationDistance;

        nextRotationSimulationCheckTime = Time.time + Mathf.Max(0.05f, rotationSimulationCheckInterval);

        World world = World.Instance;
        if (world == null)
        {
            rotationPausedBySimulationDistance = false;
            return true;
        }

        bool insideSimulationDistance = world.IsWorldPositionInsidePlayerSimulationDistance(ResolveWindMillBlockPosition());
        rotationPausedBySimulationDistance = !insideSimulationDistance;
        return insideSimulationDistance;
    }

    private void ResetRotationTiming()
    {
        lastVisualRotationTime = Time.time;
        nextRotationSimulationCheckTime = 0f;
        rotationPausedBySimulationDistance = false;
    }

    private bool ResolveRotatingHead()
    {
        if (rotatingHead != null)
            return true;

        if (!string.IsNullOrWhiteSpace(rotatingHeadFallbackName))
            rotatingHead = FindDeepChild(transform, rotatingHeadFallbackName);

        if (rotatingHead == null && transform.childCount > 0)
            rotatingHead = transform.GetChild(0);

        return rotatingHead != null;
    }

    private void CacheInitialRotation()
    {
        if (rotatingHead == null)
            return;

        if (hasInitialLocalRotation && initialRotationTransform == rotatingHead)
            return;

        initialRotationTransform = rotatingHead;
        initialLocalRotation = rotatingHead.localRotation;
        hasInitialLocalRotation = true;
    }

    private void ApplyInitialSpinOffset()
    {
        if (!randomizeInitialRotation || rotatingHead == null || !hasInitialLocalRotation)
            return;

        Vector3 axis = GetRotationAxis();
        if (axis.sqrMagnitude <= 0.0001f)
            return;

        rotatingHead.localRotation = initialLocalRotation *
            Quaternion.AngleAxis(GetDeterministicSpinOffset(), axis.normalized);
    }

    private Vector3 GetRotationAxis()
    {
        return localRotationAxis.sqrMagnitude > 0.0001f
            ? localRotationAxis
            : Vector3.forward;
    }

    private float GetDeterministicSpinOffset()
    {
        Vector3Int windMillPos = ResolveWindMillBlockPosition();
        unchecked
        {
            uint hash = 2166136261u;
            hash = (hash ^ (uint)windMillPos.x) * 16777619u;
            hash = (hash ^ (uint)windMillPos.y) * 16777619u;
            hash = (hash ^ (uint)windMillPos.z) * 16777619u;
            return (hash % 36000u) / 100f;
        }
    }

    private Vector3Int ResolveWindMillBlockPosition()
    {
        if (OwnerChunk != null)
            return WorldPosition;

        return Vector3Int.FloorToInt(transform.position);
    }

    private static Transform FindDeepChild(Transform parent, string childName)
    {
        if (parent == null)
            return null;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == childName)
                return child;

            Transform nested = FindDeepChild(child, childName);
            if (nested != null)
                return nested;
        }

        return null;
    }
}
