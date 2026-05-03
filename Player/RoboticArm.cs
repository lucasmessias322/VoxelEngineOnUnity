using UnityEngine;

public interface IRoboticArmGrabbable
{
    Transform DropTransform { get; }
    bool CanBeGrabbedByRoboticArm { get; }

    void AttachToRoboticArm(Transform parent, Vector3 localPosition, Quaternion localRotation, float localScale);
    void ReleaseFromRoboticArm(Vector3 worldPosition, Vector3 throwDirection);
}

public interface IRoboticArmItemStack
{
    bool TryGetRoboticArmItemStack(out Item item, out int amount);
    int RemoveFromRoboticArmStack(int amount);
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

    [Header("Chest Transfer")]
    [SerializeField] private bool enableChestExtraction = true;
    [SerializeField] private bool enableChestInsertion = true;
    [Tooltip("Quantidade maxima de itens/blocos retirada do bau em cada ciclo do braco.")]
    [SerializeField, Min(1)] private int itemsPerChestGrab = 1;

    [Header("Animation Events")]
    [SerializeField, Min(0.01f)] private float scanIntervalSeconds = 0.2f;
    [SerializeField, Min(0f)] private float initialDelaySeconds = 0.25f;
    [SerializeField, Min(0.01f)] private float cycleCooldownSeconds = 0.35f;
    [SerializeField] private string grabDropAnimatorBool = "GrabDrop";

    [Header("Electricity")]
    [SerializeField] private bool requireElectricity = true;
    [SerializeField, Min(0f)] private float energyPerTransferCycle = 8f;

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
        else if (TryFindChestItemInFront(out IRoboticArmGrabbable chestItem))
        {
            BeginTransfer(chestItem);
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
            if (targetDrop is RoboticArmChestItemTransfer chestTransfer)
                chestTransfer.DiscardIfUnused();

            nextScanTime = Time.time + scanIntervalSeconds;
            return;
        }

        if (!TryConsumeTransferEnergy())
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

        if (TryInsertHeldDropIntoChestBehind(releasedDrop))
            return;

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

        if (pendingDrop is RoboticArmChestItemTransfer pendingChestTransfer)
            pendingChestTransfer.DiscardIfUnused();

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

    private bool TryFindChestItemInFront(out IRoboticArmGrabbable grabbable)
    {
        grabbable = null;
        if (!enableChestExtraction)
            return false;

        World world = World.Instance;
        if (world == null)
            return false;

        ChestUIController chestUI = ChestUIController.Instance != null
            ? ChestUIController.Instance
            : ChestUIController.EnsureInstance();
        if (chestUI == null)
            return false;

        Vector3Int chestBlock = ResolvePickupBlockPosition();
        if (world.GetBlockAt(chestBlock) != BlockType.chest ||
            !chestUI.HasItemStackInChest(chestBlock))
        {
            return false;
        }

        GameObject transferObject = new GameObject($"RoboticArmChestTransfer_{chestBlock.x}_{chestBlock.y}_{chestBlock.z}");
        transferObject.transform.position = ResolvePickupCenter();

        RoboticArmChestItemTransfer transfer = transferObject.AddComponent<RoboticArmChestItemTransfer>();
        transfer.Initialize(chestUI, chestBlock, Mathf.Max(1, itemsPerChestGrab));
        grabbable = transfer;
        return true;
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

        if (requireInitialWorldReady && !world.IsInitialWorldReady)
            return false;

        if (!requireElectricity)
            return true;

        return world.HasElectricalEnergy(ResolveArmBlockPosition(), Mathf.Max(0f, energyPerTransferCycle));
    }

    private bool TryConsumeTransferEnergy()
    {
        if (!requireElectricity)
            return true;

        World world = World.Instance;
        if (world == null)
            return false;

        return world.TryConsumeElectricalEnergy(
            ResolveArmBlockPosition(),
            Mathf.Max(0f, energyPerTransferCycle));
    }

    private Vector3 ResolvePickupCenter()
    {
        Vector3Int pickupBlock = ResolvePickupBlockPosition();
        return new Vector3(
            pickupBlock.x + 0.5f,
            pickupBlock.y + pickupHeightOffset,
            pickupBlock.z + 0.5f);
    }

    private Vector3Int ResolvePickupBlockPosition()
    {
        Vector3Int armBlockPos = ResolveArmBlockPosition();
        Vector3Int forward = ResolveForwardInt();
        return armBlockPos + forward * Mathf.Max(1, grabDistanceBlocks);
    }

    private Vector3 ResolveReleaseCenter()
    {
        Vector3Int releaseBlock = ResolveReleaseBlockPosition();
        return new Vector3(
            releaseBlock.x + 0.5f,
            releaseBlock.y + releaseHeightOffset,
            releaseBlock.z + 0.5f);
    }

    private Vector3Int ResolveReleaseBlockPosition()
    {
        Vector3Int armBlockPos = ResolveArmBlockPosition();
        Vector3Int backward = -(transferInProgress ? activeTransferForward : ResolveForwardInt());
        return armBlockPos + backward * Mathf.Max(1, dropDistanceBlocks);
    }

    private bool TryInsertHeldDropIntoChestBehind(IRoboticArmGrabbable grabbable)
    {
        if (!enableChestInsertion || grabbable is not IRoboticArmItemStack itemStack)
            return false;

        World world = World.Instance;
        if (world == null)
            return false;

        Vector3Int chestBlock = ResolveReleaseBlockPosition();
        if (world.GetBlockAt(chestBlock) != BlockType.chest)
            return false;

        ChestUIController chestUI = ChestUIController.Instance != null
            ? ChestUIController.Instance
            : ChestUIController.EnsureInstance();
        if (chestUI == null || !itemStack.TryGetRoboticArmItemStack(out Item item, out int amount))
            return false;

        int remaining = chestUI.InsertItemStackIntoChest(chestBlock, item, amount);
        int inserted = amount - remaining;
        if (inserted <= 0)
            return false;

        itemStack.RemoveFromRoboticArmStack(inserted);
        return remaining <= 0;
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

internal sealed class RoboticArmChestItemTransfer : MonoBehaviour, IRoboticArmGrabbable, IRoboticArmItemStack
{
    private ChestUIController chestUI;
    private Vector3Int chestBlock;
    private int maxAmount;
    private Item item;
    private int amount;
    private bool isAttached;
    private bool isReleased;
    private SpriteRenderer spriteRenderer;

    public Transform DropTransform => transform;

    public bool CanBeGrabbedByRoboticArm =>
        !isAttached &&
        !isReleased &&
        chestUI != null &&
        chestUI.HasItemStackInChest(chestBlock);

    public void Initialize(ChestUIController ownerChestUI, Vector3Int sourceChestBlock, int sourceMaxAmount)
    {
        chestUI = ownerChestUI;
        chestBlock = sourceChestBlock;
        maxAmount = Mathf.Max(1, sourceMaxAmount);
    }

    public void AttachToRoboticArm(Transform parent, Vector3 localPosition, Quaternion localRotation, float localScale)
    {
        if (parent == null || !CanBeGrabbedByRoboticArm)
            return;

        if (!chestUI.TryTakeItemStackFromChest(chestBlock, maxAmount, out item, out amount))
            return;

        isAttached = true;
        transform.SetParent(parent, false);
        transform.localPosition = localPosition;
        transform.localRotation = localRotation;
        transform.localScale = Vector3.one * Mathf.Max(0.01f, localScale);
        EnsureHeldVisual();
    }

    public void ReleaseFromRoboticArm(Vector3 worldPosition, Vector3 throwDirection)
    {
        if (isReleased)
            return;

        isReleased = true;
        transform.SetParent(null, true);

        if (item != null && amount > 0)
            ChestUIController.TrySpawnItemStack(item, amount, worldPosition, throwDirection);

        Destroy(gameObject);
    }

    public bool TryGetRoboticArmItemStack(out Item stackItem, out int stackAmount)
    {
        stackItem = item;
        stackAmount = amount;
        return !isReleased && stackItem != null && stackAmount > 0;
    }

    public int RemoveFromRoboticArmStack(int amountToRemove)
    {
        if (amountToRemove <= 0 || item == null || amount <= 0)
            return 0;

        int removed = Mathf.Min(amountToRemove, amount);
        amount -= removed;
        if (amount <= 0)
        {
            isReleased = true;
            Destroy(gameObject);
        }

        return removed;
    }

    public void DiscardIfUnused()
    {
        if (isAttached)
            return;

        isReleased = true;
        Destroy(gameObject);
    }

    private void EnsureHeldVisual()
    {
        if (item == null)
            return;

        if (spriteRenderer == null)
        {
            GameObject visualObject = new GameObject("HeldChestItemVisual");
            visualObject.transform.SetParent(transform, false);
            visualObject.transform.localPosition = Vector3.zero;
            visualObject.transform.localRotation = Quaternion.identity;
            visualObject.transform.localScale = Vector3.one;
            spriteRenderer = visualObject.AddComponent<SpriteRenderer>();
        }

        spriteRenderer.sprite = ItemIconResolver.ResolveForUI(item);
        spriteRenderer.enabled = spriteRenderer.sprite != null;
    }
}
