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

    private Transform initialRotationTransform;
    private Quaternion initialLocalRotation;
    private bool hasInitialLocalRotation;

    private void Awake()
    {
        ResolveRotatingHead();
        CacheInitialRotation();
    }

    private void Update()
    {
        if (rotatingHead == null && !ResolveRotatingHead())
            return;

        if (degreesPerSecond <= 0f)
            return;

        World world = World.Instance;
        if (world == null || !world.CanWindMillGenerateAt(WorldPosition))
            return;

        Vector3 axis = GetRotationAxis();
        if (axis.sqrMagnitude <= 0.0001f)
            return;

        rotatingHead.Rotate(axis.normalized, degreesPerSecond * Time.deltaTime, Space.Self);
    }

    protected override void OnDynamicBlockSpawned()
    {
        ResolveRotatingHead();
        CacheInitialRotation();
        ApplyInitialSpinOffset();
    }

    protected override void OnDynamicBlockDespawned()
    {
        if (rotatingHead != null && hasInitialLocalRotation)
            rotatingHead.localRotation = initialLocalRotation;
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
        unchecked
        {
            uint hash = 2166136261u;
            hash = (hash ^ (uint)WorldPosition.x) * 16777619u;
            hash = (hash ^ (uint)WorldPosition.y) * 16777619u;
            hash = (hash ^ (uint)WorldPosition.z) * 16777619u;
            return (hash % 36000u) / 100f;
        }
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
