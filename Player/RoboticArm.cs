using UnityEngine;

public interface IRoboticArmGrabbable
{
    Transform DropTransform { get; }
    bool CanBeGrabbedByRoboticArm { get; }

    void AttachToRoboticArm(Transform parent, Vector3 localPosition, Quaternion localRotation, float localScale);
    void ReleaseFromRoboticArm(Vector3 worldPosition, Vector3 throwDirection);
}

[DisallowMultipleComponent]
public class RoboticArm : DynamicVoxelBlock
{
    [Header("Detection")]
    [SerializeField, Min(0.05f)] private float pickupRadius = 0.75f;
    [SerializeField, Min(0.05f)] private float pickupVerticalTolerance = 1.6f;
    [SerializeField, Min(0f)] private float pickupHeightOffset = 1.15f;
    [SerializeField, Min(0f)] private float releaseHeightOffset = 1.15f;
    [SerializeField, Min(1)] private int grabDistanceBlocks = 1;
    [SerializeField, Min(1)] private int dropDistanceBlocks = 1;
    [SerializeField, Min(0f)] private float releasedDropIgnoreSeconds = 0.75f;
    [SerializeField] private bool requireInitialWorldReady = true;

    [Header("Animation Events")]
    [SerializeField, Min(0.01f)] private float scanIntervalSeconds = 0.2f;
    [SerializeField, Min(0f)] private float initialDelaySeconds = 0.25f;
    [SerializeField, Min(0.01f)] private float cycleCooldownSeconds = 0.35f;
    [SerializeField] private string grabDropAnimatorBool = "GrabDrop";

    [Header("Held Drop")]
    [SerializeField] private Transform gripAnchor;
    [SerializeField] private Vector3 heldLocalPosition = Vector3.zero;
    [SerializeField] private Vector3 heldLocalEulerAngles = Vector3.zero;
    [SerializeField, Min(0.01f)] private float heldLocalScale = 0.42f;
    [SerializeField, Min(0f)] private float releaseThrowStrength = 1.25f;

    private Animator animator;
    private int grabDropAnimatorId;
    private bool hasGrabDropAnimatorBool;
    private Transform resolvedGripAnchor;
    private IRoboticArmGrabbable pendingDrop;
    private IRoboticArmGrabbable heldDrop;
    private IRoboticArmGrabbable recentlyReleasedDrop;
    private Vector3Int activeTransferForward = Vector3Int.forward;
    private bool transferInProgress;
    private float nextScanTime;
    private float recentlyReleasedDropIgnoreUntil;

    private void Awake()
    {
        CacheComponents();
    }

    private void OnEnable()
    {
        nextScanTime = Time.time + Mathf.Max(0f, initialDelaySeconds);
    }

    private void Start()
    {
        CacheComponents();
        SetGrabDropAnimation(false);
    }

    protected override void OnDynamicBlockSpawned()
    {
        CacheComponents();
        nextScanTime = Time.time + Mathf.Max(0f, initialDelaySeconds);
    }

    protected override void OnDynamicBlockDespawned()
    {
        CancelTransfer(releaseHeldDrop: true);
    }

    private void OnDisable()
    {
        CancelTransfer(releaseHeldDrop: true);
    }

    private void Update()
    {
        if (transferInProgress)
            return;

        if (Time.time < nextScanTime)
            return;

        if (!CanOperate())
        {
            nextScanTime = Time.time + scanIntervalSeconds;
            return;
        }

        if (TryFindDropInFront(out IRoboticArmGrabbable targetDrop))
        {
            BeginTransfer(targetDrop);
        }
        else
        {
            nextScanTime = Time.time + scanIntervalSeconds;
        }
    }

    public void GrabDropFromAnimation()
    {
        if (!transferInProgress)
            return;

        if (!TryAttachPendingDrop())
            CancelTransfer(releaseHeldDrop: false);
    }

    public void ReleaseDropFromAnimation()
    {
        if (!transferInProgress)
            return;

        if (heldDrop != null)
            ReleaseHeldDropBehind();

        FinishTransfer();
    }

    private void BeginTransfer(IRoboticArmGrabbable targetDrop)
    {
        if (targetDrop == null || !targetDrop.CanBeGrabbedByRoboticArm)
        {
            nextScanTime = Time.time + scanIntervalSeconds;
            return;
        }

        pendingDrop = targetDrop;
        heldDrop = null;
        activeTransferForward = ResolveForwardInt();
        transferInProgress = true;
        SetGrabDropAnimation(true);
    }

    private bool TryAttachPendingDrop()
    {
        if (heldDrop != null)
            return true;

        if (pendingDrop == null || !pendingDrop.CanBeGrabbedByRoboticArm)
            return false;

        Transform anchor = ResolveGripAnchor();
        if (anchor == null)
            return false;

        pendingDrop.AttachToRoboticArm(
            anchor,
            heldLocalPosition,
            Quaternion.Euler(heldLocalEulerAngles),
            heldLocalScale);

        if (!pendingDrop.CanBeGrabbedByRoboticArm && pendingDrop.DropTransform != null && pendingDrop.DropTransform.parent == anchor)
        {
            heldDrop = pendingDrop;
            pendingDrop = null;
            return true;
        }

        if (pendingDrop.DropTransform != null && pendingDrop.DropTransform.parent == anchor)
        {
            heldDrop = pendingDrop;
            pendingDrop = null;
            return true;
        }

        return false;
    }

    private void ReleaseHeldDropBehind()
    {
        if (heldDrop == null)
            return;

        Vector3 releasePosition = ResolveReleaseCenter();
        Vector3 throwDirection = -(Vector3)activeTransferForward * Mathf.Max(0f, releaseThrowStrength);
        IRoboticArmGrabbable releasedDrop = heldDrop;
        heldDrop = null;
        releasedDrop.ReleaseFromRoboticArm(releasePosition, throwDirection);
        IgnoreRecentlyReleasedDrop(releasedDrop);
    }

    private void ReleaseHeldDropAtGrip(Vector3 throwDirection)
    {
        if (heldDrop == null)
            return;

        Transform anchor = ResolveGripAnchor();
        Vector3 releasePosition = anchor != null ? anchor.position : transform.position + Vector3.up;
        IRoboticArmGrabbable releasedDrop = heldDrop;
        heldDrop = null;
        releasedDrop.ReleaseFromRoboticArm(releasePosition, throwDirection);
        IgnoreRecentlyReleasedDrop(releasedDrop);
    }

    private void FinishTransfer()
    {
        pendingDrop = null;
        transferInProgress = false;
        SetGrabDropAnimation(false);
        nextScanTime = Time.time + Mathf.Max(0.01f, cycleCooldownSeconds);
    }

    private void CancelTransfer(bool releaseHeldDrop)
    {
        if (releaseHeldDrop)
            ReleaseHeldDropAtGrip(Vector3.zero);

        pendingDrop = null;
        transferInProgress = false;
        SetGrabDropAnimation(false);
        nextScanTime = Time.time + Mathf.Max(0.01f, scanIntervalSeconds);
    }

    private bool TryFindDropInFront(out IRoboticArmGrabbable grabbable)
    {
        grabbable = null;
        Vector3 pickupCenter = ResolvePickupCenter();
        float bestDistanceSqr = pickupRadius * pickupRadius;

        BlockDrop[] blockDrops = FindObjectsByType<BlockDrop>(FindObjectsInactive.Exclude);
        for (int i = 0; i < blockDrops.Length; i++)
            TryUseCloserDrop(blockDrops[i], pickupCenter, ref grabbable, ref bestDistanceSqr);

        InventoryItemDrop[] itemDrops = FindObjectsByType<InventoryItemDrop>(FindObjectsInactive.Exclude);
        for (int i = 0; i < itemDrops.Length; i++)
            TryUseCloserDrop(itemDrops[i], pickupCenter, ref grabbable, ref bestDistanceSqr);

        return grabbable != null;
    }

    private void TryUseCloserDrop(
        IRoboticArmGrabbable candidate,
        Vector3 pickupCenter,
        ref IRoboticArmGrabbable bestDrop,
        ref float bestDistanceSqr)
    {
        if (candidate == null || !candidate.CanBeGrabbedByRoboticArm || candidate.DropTransform == null)
            return;

        if (ShouldIgnoreRecentlyReleasedDrop(candidate))
            return;

        Vector3 delta = candidate.DropTransform.position - pickupCenter;
        if (Mathf.Abs(delta.y) > pickupVerticalTolerance)
            return;

        float horizontalDistanceSqr = delta.x * delta.x + delta.z * delta.z;
        if (horizontalDistanceSqr > bestDistanceSqr)
            return;

        bestDistanceSqr = horizontalDistanceSqr;
        bestDrop = candidate;
    }

    private bool CanOperate()
    {
        World world = World.Instance;
        if (world == null)
            return false;

        return !requireInitialWorldReady || world.IsInitialWorldReady;
    }

    private Vector3 ResolvePickupCenter()
    {
        Vector3Int armBlockPos = ResolveArmBlockPosition();
        Vector3Int forward = ResolveForwardInt();
        Vector3Int pickupBlock = armBlockPos + forward * Mathf.Max(1, grabDistanceBlocks);
        return new Vector3(
            pickupBlock.x + 0.5f,
            pickupBlock.y + pickupHeightOffset,
            pickupBlock.z + 0.5f);
    }

    private Vector3 ResolveReleaseCenter()
    {
        Vector3Int armBlockPos = ResolveArmBlockPosition();
        Vector3Int backward = -(transferInProgress ? activeTransferForward : ResolveForwardInt());
        Vector3Int releaseBlock = armBlockPos + backward * Mathf.Max(1, dropDistanceBlocks);
        return new Vector3(
            releaseBlock.x + 0.5f,
            releaseBlock.y + releaseHeightOffset,
            releaseBlock.z + 0.5f);
    }

    private Vector3Int ResolveArmBlockPosition()
    {
        if (OwnerChunk != null)
            return WorldPosition;

        return Vector3Int.FloorToInt(transform.position);
    }

    private Vector3 ResolveForwardDirection()
    {
        Transform anchor = ResolveGripAnchor();
        if (TryGetGripAnchorForward(anchor, ResolveArmBlockPosition(), out Vector3 anchorForward))
            return anchorForward;

        World world = World.Instance;
        if (world != null)
        {
            Vector3Int armBlockPos = ResolveArmBlockPosition();
            BlockPlacementAxis placementAxis = world.GetPlacementAxisAt(armBlockPos, BlockType.RoboticArm);
            Vector3 axisForward = ConveyorBeltUtility.GetForwardDirection(placementAxis);
            if (axisForward.sqrMagnitude > 0.0001f)
                return axisForward.normalized;
        }

        Vector3 horizontalForward = new Vector3(transform.forward.x, 0f, transform.forward.z);
        return horizontalForward.sqrMagnitude > 0.0001f ? horizontalForward.normalized : Vector3.forward;
    }

    private Vector3Int ResolveForwardInt()
    {
        Vector3 horizontalForward = ResolveForwardDirection();
        if (horizontalForward.sqrMagnitude <= 0.0001f)
            return Vector3Int.forward;

        horizontalForward.Normalize();
        if (Mathf.Abs(horizontalForward.x) >= Mathf.Abs(horizontalForward.z))
            return horizontalForward.x >= 0f ? Vector3Int.right : Vector3Int.left;

        return horizontalForward.z >= 0f ? Vector3Int.forward : Vector3Int.back;
    }

    private bool TryGetGripAnchorForward(Transform anchor, Vector3Int armBlockPos, out Vector3 forward)
    {
        forward = Vector3.zero;
        if (anchor == null || anchor == transform)
            return false;

        Vector3 armCenter = new Vector3(armBlockPos.x + 0.5f, anchor.position.y, armBlockPos.z + 0.5f);
        Vector3 delta = anchor.position - armCenter;
        delta.y = 0f;
        if (delta.sqrMagnitude < 0.04f)
            return false;

        forward = delta.normalized;
        return true;
    }

    private void IgnoreRecentlyReleasedDrop(IRoboticArmGrabbable releasedDrop)
    {
        recentlyReleasedDrop = releasedDrop;
        recentlyReleasedDropIgnoreUntil = Time.time + Mathf.Max(0f, releasedDropIgnoreSeconds);
    }

    private bool ShouldIgnoreRecentlyReleasedDrop(IRoboticArmGrabbable candidate)
    {
        if (candidate == null || !ReferenceEquals(candidate, recentlyReleasedDrop))
            return false;

        if (Time.time < recentlyReleasedDropIgnoreUntil)
            return true;

        recentlyReleasedDrop = null;
        recentlyReleasedDropIgnoreUntil = 0f;
        return false;
    }

    private Transform ResolveGripAnchor()
    {
        if (gripAnchor != null)
            return gripAnchor;

        if (resolvedGripAnchor != null)
            return resolvedGripAnchor;

        resolvedGripAnchor = FindDeepChild(transform, "RoboticArmGripAnchor");
        if (resolvedGripAnchor != null)
            return resolvedGripAnchor;

        resolvedGripAnchor = FindDeepChild(transform, "Head");
        if (resolvedGripAnchor != null)
            return resolvedGripAnchor;

        resolvedGripAnchor = transform;
        return resolvedGripAnchor;
    }

    private static Transform FindDeepChild(Transform root, string childName)
    {
        if (root == null || string.IsNullOrEmpty(childName))
            return null;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (child.name == childName)
                return child;

            Transform nested = FindDeepChild(child, childName);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private void CacheComponents()
    {
        if (animator == null)
            animator = GetComponent<Animator>();

        grabDropAnimatorId = string.IsNullOrEmpty(grabDropAnimatorBool)
            ? 0
            : Animator.StringToHash(grabDropAnimatorBool);

        hasGrabDropAnimatorBool = HasAnimatorBool(animator, grabDropAnimatorId);
    }

    private static bool HasAnimatorBool(Animator animatorRef, int parameterId)
    {
        if (animatorRef == null || parameterId == 0)
            return false;

        AnimatorControllerParameter[] parameters = animatorRef.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            AnimatorControllerParameter parameter = parameters[i];
            if (parameter.nameHash == parameterId && parameter.type == AnimatorControllerParameterType.Bool)
                return true;
        }

        return false;
    }

    private void SetGrabDropAnimation(bool active)
    {
        if (!hasGrabDropAnimatorBool || animator == null)
            return;

        animator.SetBool(grabDropAnimatorId, active);
    }
}
