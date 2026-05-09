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

    [Header("Factorio Style Transfer")]
    [Tooltip("Quantidade maxima que o braco pega e carrega por ciclo. Use 1 para o comportamento padrao do braco do Factorio.")]
    [SerializeField, Min(1)] private int itemsPerGrab = 1;
    [Tooltip("Impede o braco de passar do limite configurado no slot de input da fornalha.")]
    [SerializeField] private bool limitFurnaceInputFill = true;
    [Tooltip("Quantidade maxima que o braco deixa no slot de input da fornalha.")]
    [SerializeField, Min(1)] private int maxFurnaceInputItems = 5;
    [Tooltip("Impede o braco de passar do limite configurado no slot de combustivel da fornalha.")]
    [SerializeField] private bool limitFurnaceFuelFill = true;
    [Tooltip("Quantidade maxima que o braco deixa no slot de combustivel da fornalha.")]
    [SerializeField, Min(1)] private int maxFurnaceFuelItems = 5;

    [Header("Chest Transfer")]
    [SerializeField] private bool enableChestExtraction = true;
    [SerializeField] private bool enableChestInsertion = true;

    [Header("Furnace Transfer")]
    [SerializeField] private bool enableFurnaceExtraction = true;
    [SerializeField] private bool enableFurnaceInsertion = true;

    [Header("Animation Events")]
    [SerializeField] private bool playTransferAnimation = true;
    [Tooltip("Quando ligado, o proprio braco chama Grab/Drop pelos tempos abaixo. Os eventos da animacao ainda podem chamar as mesmas funcoes, sem duplicar a acao.")]
    [SerializeField] private bool useTimedTransferEvents = true;
    [Tooltip("Tempo em segundos desde o inicio do ciclo para pegar o item.")]
    [SerializeField, Min(0f)] private float grabEventTimeSeconds = 0.17f;
    [Tooltip("Tempo em segundos desde o inicio do ciclo para soltar/inserir o item.")]
    [SerializeField, Min(0f)] private float releaseEventTimeSeconds = 0.67f;
    [Tooltip("Tempo total do ciclo antes do braco voltar a procurar outro item e desligar o bool da animacao.")]
    [SerializeField, Min(0.01f)] private float transferCycleDurationSeconds = 1f;
    [SerializeField, Min(0.01f)] private float scanIntervalSeconds = 0.2f;
    [SerializeField, Min(0f)] private float initialDelaySeconds = 0.25f;
    [SerializeField, Min(0.01f)] private float cycleCooldownSeconds = 0.35f;
    [SerializeField] private string grabDropAnimatorBool = "GrabDrop";

    [Header("Animation Optimization")]
    [Tooltip("Desliga apenas o Animator quando o braco esta fora da simulation distance do World. A logica de transferencia continua pelo tempo.")]
    [SerializeField] private bool disableAnimatorOutsideSimulationDistance = true;
    [SerializeField, Min(0.05f)] private float animationSimulationCheckInterval = 0.25f;
    [SerializeField] private string idleAnimatorStateName = "Idle";
    [SerializeField] private string transferAnimatorStateName = "GrabDrop";

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
    private int idleAnimatorStateId;
    private int transferAnimatorStateId;
    private bool hasIdleAnimatorState;
    private bool hasTransferAnimatorState;
    private bool grabDropAnimationActive;
    private bool animatorDisabledBySimulationDistance;
    private bool forceTimedEventsForCurrentTransfer;
    private float nextAnimationSimulationCheckTime;
    private float transferAnimationLengthSeconds;
    private Transform resolvedGripAnchor;
    private IRoboticArmGrabbable pendingDrop;
    private IRoboticArmGrabbable heldDrop;
    private IRoboticArmGrabbable recentlyReleasedDrop;
    private Vector3Int activeTransferForward = Vector3Int.forward;
    private bool transferInProgress;
    private bool grabEventProcessed;
    private bool releaseEventProcessed;
    private float transferStartTime;
    private float nextScanTime;
    private float recentlyReleasedDropIgnoreUntil;

    private void Awake()
    {
        CacheComponents();
    }

    private void OnEnable()
    {
        nextScanTime = Time.time + Mathf.Max(0f, initialDelaySeconds);
        nextAnimationSimulationCheckTime = 0f;
    }

    private void Start()
    {
        CacheComponents();
        SetGrabDropAnimation(false);
        RefreshAnimatorSimulationDistanceState(force: true);
    }

    protected override void OnDynamicBlockSpawned()
    {
        CacheComponents();
        nextScanTime = Time.time + Mathf.Max(0f, initialDelaySeconds);
        nextAnimationSimulationCheckTime = 0f;
        RefreshAnimatorSimulationDistanceState(force: true);
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
        RefreshAnimatorSimulationDistanceState(force: false);

        if (transferInProgress)
        {
            UpdateTransferTimeline();
            return;
        }

        if (Time.time < nextScanTime)
            return;

        if (!CanOperate())
        {
            nextScanTime = Time.time + scanIntervalSeconds;
            return;
        }

        if (TryFindFurnaceOutputInFront(out IRoboticArmGrabbable furnaceItem))
        {
            BeginTransfer(furnaceItem);
        }
        else if (TryFindDropInFront(out IRoboticArmGrabbable targetDrop))
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
        TriggerGrabEvent();
    }

    public void ReleaseDropFromAnimation()
    {
        TriggerReleaseEvent();
    }

    private void BeginTransfer(IRoboticArmGrabbable targetDrop)
    {
        if (targetDrop == null || !targetDrop.CanBeGrabbedByRoboticArm)
        {
            if (targetDrop is RoboticArmChestItemTransfer chestTransfer)
                chestTransfer.DiscardIfUnused();
            else if (targetDrop is RoboticArmFurnaceOutputTransfer furnaceTransfer)
                furnaceTransfer.DiscardIfUnused();
            else if (targetDrop is RoboticArmLimitedStackPickup limitedStackTransfer)
                limitedStackTransfer.DiscardIfUnused();

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
        grabEventProcessed = false;
        releaseEventProcessed = false;
        forceTimedEventsForCurrentTransfer = animatorDisabledBySimulationDistance;
        transferStartTime = Time.time;
        SetGrabDropAnimation(true);
    }

    private void UpdateTransferTimeline()
    {
        if (!transferInProgress)
            return;

        float elapsed = Time.time - transferStartTime;
        if (ShouldDriveTimedTransferEvents())
        {
            if (!grabEventProcessed && elapsed >= Mathf.Max(0f, grabEventTimeSeconds))
                TriggerGrabEvent();

            if (transferInProgress &&
                grabEventProcessed &&
                !releaseEventProcessed &&
                elapsed >= ResolveReleaseEventTime())
            {
                TriggerReleaseEvent();
            }
        }

        if (transferInProgress &&
            releaseEventProcessed &&
            elapsed >= ResolveTransferCycleDuration())
        {
            FinishTransfer();
        }
    }

    private float ResolveReleaseEventTime()
    {
        return Mathf.Max(Mathf.Max(0f, grabEventTimeSeconds), Mathf.Max(0f, releaseEventTimeSeconds));
    }

    private float ResolveTransferCycleDuration()
    {
        return Mathf.Max(ResolveReleaseEventTime(), Mathf.Max(0.01f, transferCycleDurationSeconds));
    }

    private bool ShouldDriveTimedTransferEvents()
    {
        return useTimedTransferEvents ||
               animatorDisabledBySimulationDistance ||
               forceTimedEventsForCurrentTransfer ||
               !playTransferAnimation;
    }

    private void RefreshAnimatorSimulationDistanceState(bool force)
    {
        if (animator == null)
            return;

        if (!disableAnimatorOutsideSimulationDistance)
        {
            if (animatorDisabledBySimulationDistance || !animator.enabled)
                EnableAnimatorAndSyncToTimeline();

            animatorDisabledBySimulationDistance = false;
            return;
        }

        if (!force && Time.time < nextAnimationSimulationCheckTime)
            return;

        nextAnimationSimulationCheckTime = Time.time + Mathf.Max(0.05f, animationSimulationCheckInterval);
        bool shouldAnimate = IsInsideAnimationSimulationDistance();
        if (shouldAnimate)
        {
            if (animatorDisabledBySimulationDistance || !animator.enabled)
                EnableAnimatorAndSyncToTimeline();

            animatorDisabledBySimulationDistance = false;
            return;
        }

        if (animator.enabled)
            animator.enabled = false;

        animatorDisabledBySimulationDistance = true;
        if (transferInProgress)
            forceTimedEventsForCurrentTransfer = true;
    }

    private bool IsInsideAnimationSimulationDistance()
    {
        World world = World.Instance;
        if (world == null)
            return true;

        return world.IsWorldPositionInsidePlayerSimulationDistance(ResolveArmBlockPosition());
    }

    private void EnableAnimatorAndSyncToTimeline()
    {
        if (animator == null)
            return;

        animator.enabled = true;
        ApplyGrabDropAnimatorBool();
        PlayAnimatorStateAtCurrentLogicalTime();
        animator.Update(0f);
    }

    private void PlayAnimatorStateAtCurrentLogicalTime()
    {
        if (animator == null || !animator.enabled)
            return;

        if (!playTransferAnimation || !grabDropAnimationActive || !transferInProgress)
        {
            if (hasIdleAnimatorState)
                animator.Play(idleAnimatorStateId, 0, 0f);
            return;
        }

        if (hasTransferAnimatorState)
            animator.Play(transferAnimatorStateId, 0, ResolveTransferAnimationNormalizedTime());
    }

    private float ResolveTransferAnimationNormalizedTime()
    {
        if (!transferInProgress)
            return 0f;

        float animationDuration = transferAnimationLengthSeconds > 0.001f
            ? transferAnimationLengthSeconds
            : ResolveTransferCycleDuration();
        float normalizedTime = (Time.time - transferStartTime) / Mathf.Max(0.001f, animationDuration);
        return Mathf.Clamp(normalizedTime, 0f, 0.999f);
    }

    private void TriggerGrabEvent()
    {
        if (!transferInProgress || grabEventProcessed)
            return;

        grabEventProcessed = true;
        if (!TryAttachPendingDrop())
            CancelTransfer(releaseHeldDrop: false);
    }

    private void TriggerReleaseEvent()
    {
        if (!transferInProgress || releaseEventProcessed)
            return;

        if (!grabEventProcessed)
            TriggerGrabEvent();

        if (!transferInProgress)
            return;

        releaseEventProcessed = true;
        if (heldDrop != null)
            ReleaseHeldDropBehind();
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

        if (TryInsertHeldDropIntoFurnaceBehind(releasedDrop))
            return;

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
        grabEventProcessed = false;
        releaseEventProcessed = false;
        forceTimedEventsForCurrentTransfer = false;
        transferStartTime = 0f;
        SetGrabDropAnimation(false);
        nextScanTime = Time.time + Mathf.Max(0.01f, cycleCooldownSeconds);
    }

    private void CancelTransfer(bool releaseHeldDrop)
    {
        if (releaseHeldDrop)
            ReleaseHeldDropAtGrip(Vector3.zero);

        if (pendingDrop is RoboticArmChestItemTransfer pendingChestTransfer)
            pendingChestTransfer.DiscardIfUnused();
        else if (pendingDrop is RoboticArmFurnaceOutputTransfer pendingFurnaceTransfer)
            pendingFurnaceTransfer.DiscardIfUnused();
        else if (pendingDrop is RoboticArmLimitedStackPickup pendingLimitedStackTransfer)
            pendingLimitedStackTransfer.DiscardIfUnused();

        pendingDrop = null;
        transferInProgress = false;
        grabEventProcessed = false;
        releaseEventProcessed = false;
        forceTimedEventsForCurrentTransfer = false;
        transferStartTime = 0f;
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

        if (grabbable == null)
            return false;

        return TryPrepareDropPickup(grabbable, out grabbable);
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
        int grabAmount = ResolveItemsPerGrab();
        if (world.GetBlockAt(chestBlock) != BlockType.chest ||
            !chestUI.HasItemStackInChest(chestBlock))
        {
            return false;
        }

        Item preferredChestItem = null;
        if (TryResolveFurnaceBehind(out FurnaceUIController furnaceBehindUI, out Vector3Int furnaceBehindBlock))
        {
            if (!chestUI.TryPeekItemStackFromChest(
                    chestBlock,
                    grabAmount,
                    item => furnaceBehindUI.GetInsertableItemCountForFurnace(
                        furnaceBehindBlock,
                        item,
                        grabAmount,
                        ResolveFurnaceInputInsertionLimit(),
                        ResolveFurnaceFuelInsertionLimit()) > 0,
                    out Item peekItem,
                    out int peekAmount))
            {
                return false;
            }

            int acceptedAmount = furnaceBehindUI.GetInsertableItemCountForFurnace(
                furnaceBehindBlock,
                peekItem,
                Mathf.Min(grabAmount, peekAmount),
                ResolveFurnaceInputInsertionLimit(),
                ResolveFurnaceFuelInsertionLimit());
            if (acceptedAmount <= 0)
                return false;

            preferredChestItem = peekItem;
            grabAmount = Mathf.Min(grabAmount, acceptedAmount);
        }

        GameObject transferObject = new GameObject($"RoboticArmChestTransfer_{chestBlock.x}_{chestBlock.y}_{chestBlock.z}");
        transferObject.transform.position = ResolvePickupCenter();

        RoboticArmChestItemTransfer transfer = transferObject.AddComponent<RoboticArmChestItemTransfer>();
        transfer.Initialize(chestUI, chestBlock, grabAmount, preferredChestItem);
        grabbable = transfer;
        return true;
    }

    private bool TryFindFurnaceOutputInFront(out IRoboticArmGrabbable grabbable)
    {
        grabbable = null;
        if (!enableFurnaceExtraction)
            return false;

        World world = World.Instance;
        if (world == null)
            return false;

        FurnaceUIController furnaceUI = FurnaceUIController.Instance != null
            ? FurnaceUIController.Instance
            : FindAnyObjectByType<FurnaceUIController>();
        if (furnaceUI == null)
            return false;

        Vector3Int furnaceBlock = ResolvePickupBlockPosition();
        int grabAmount = ResolveItemsPerGrab();
        if (world.GetBlockAt(furnaceBlock) != BlockType.StoneFurnance ||
            !furnaceUI.HasOutputStackInFurnace(furnaceBlock))
        {
            return false;
        }

        if (TryResolveFurnaceBehind(out FurnaceUIController furnaceBehindUI, out Vector3Int furnaceBehindBlock) &&
            furnaceUI.TryPeekOutputStackFromFurnace(furnaceBlock, grabAmount, out Item peekItem, out int peekAmount))
        {
            int acceptedAmount = furnaceBehindUI.GetInsertableItemCountForFurnace(
                furnaceBehindBlock,
                peekItem,
                Mathf.Min(grabAmount, peekAmount),
                ResolveFurnaceInputInsertionLimit(),
                ResolveFurnaceFuelInsertionLimit());
            if (acceptedAmount <= 0)
                return false;

            grabAmount = Mathf.Min(grabAmount, acceptedAmount);
        }

        GameObject transferObject = new GameObject($"RoboticArmFurnaceOutputTransfer_{furnaceBlock.x}_{furnaceBlock.y}_{furnaceBlock.z}");
        transferObject.transform.position = ResolvePickupCenter();

        RoboticArmFurnaceOutputTransfer transfer = transferObject.AddComponent<RoboticArmFurnaceOutputTransfer>();
        transfer.Initialize(furnaceUI, furnaceBlock, grabAmount);
        grabbable = transfer;
        return true;
    }

    private bool TryPrepareDropPickup(IRoboticArmGrabbable source, out IRoboticArmGrabbable prepared)
    {
        prepared = source;
        if (source == null || source is not IRoboticArmItemStack sourceStack)
            return source != null;

        if (!sourceStack.TryGetRoboticArmItemStack(out Item item, out int amount))
            return false;

        int grabAmount = ResolveMaxGrabAmountForItem(item, amount);
        if (grabAmount <= 0)
            return false;

        Transform sourceTransform = source.DropTransform;
        GameObject transferObject = new GameObject($"RoboticArmLimitedPickup_{(item != null ? item.name : "Item")}");
        transferObject.transform.position = sourceTransform != null ? sourceTransform.position : ResolvePickupCenter();

        RoboticArmLimitedStackPickup transfer = transferObject.AddComponent<RoboticArmLimitedStackPickup>();
        transfer.Initialize(source, sourceStack, grabAmount);
        prepared = transfer;
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

        if (!CanUseDropForCurrentTarget(candidate))
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

    private bool CanUseDropForCurrentTarget(IRoboticArmGrabbable candidate)
    {
        if (candidate is not IRoboticArmItemStack itemStack)
            return true;

        if (!itemStack.TryGetRoboticArmItemStack(out Item item, out int amount))
            return false;

        return ResolveMaxGrabAmountForItem(item, amount) > 0;
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

    private bool TryInsertHeldDropIntoFurnaceBehind(IRoboticArmGrabbable grabbable)
    {
        if (!enableFurnaceInsertion || grabbable is not IRoboticArmItemStack itemStack)
            return false;

        World world = World.Instance;
        if (world == null)
            return false;

        Vector3Int furnaceBlock = ResolveReleaseBlockPosition();
        if (world.GetBlockAt(furnaceBlock) != BlockType.StoneFurnance)
            return false;

        FurnaceUIController furnaceUI = FurnaceUIController.Instance != null
            ? FurnaceUIController.Instance
            : FindAnyObjectByType<FurnaceUIController>();
        if (furnaceUI == null || !itemStack.TryGetRoboticArmItemStack(out Item item, out int amount))
            return false;

        int insertAmount = Mathf.Min(amount, ResolveItemsPerGrab());
        int remaining = furnaceUI.InsertItemStackIntoFurnace(
            furnaceBlock,
            item,
            insertAmount,
            ResolveFurnaceInputInsertionLimit(),
            ResolveFurnaceFuelInsertionLimit());
        int inserted = insertAmount - remaining;
        if (inserted <= 0)
            return false;

        itemStack.RemoveFromRoboticArmStack(inserted);
        return remaining <= 0;
    }

    private int ResolveItemsPerGrab()
    {
        return Mathf.Max(1, itemsPerGrab);
    }

    private int ResolveFurnaceInputInsertionLimit()
    {
        return limitFurnaceInputFill ? Mathf.Max(1, maxFurnaceInputItems) : int.MaxValue;
    }

    private int ResolveFurnaceFuelInsertionLimit()
    {
        return limitFurnaceFuelFill ? Mathf.Max(1, maxFurnaceFuelItems) : int.MaxValue;
    }

    private int ResolveMaxGrabAmountForItem(Item item, int availableAmount)
    {
        if (item == null || availableAmount <= 0)
            return 0;

        int grabAmount = Mathf.Min(ResolveItemsPerGrab(), availableAmount);
        if (grabAmount <= 0)
            return 0;

        if (!TryResolveFurnaceBehind(out FurnaceUIController furnaceUI, out Vector3Int furnaceBlock))
            return grabAmount;

        int acceptedAmount = furnaceUI.GetInsertableItemCountForFurnace(
            furnaceBlock,
            item,
            grabAmount,
            ResolveFurnaceInputInsertionLimit(),
            ResolveFurnaceFuelInsertionLimit());
        return Mathf.Min(grabAmount, acceptedAmount);
    }

    private bool TryResolveFurnaceBehind(out FurnaceUIController furnaceUI, out Vector3Int furnaceBlock)
    {
        furnaceUI = null;
        furnaceBlock = ResolveReleaseBlockPosition();

        World world = World.Instance;
        if (world == null || world.GetBlockAt(furnaceBlock) != BlockType.StoneFurnance)
            return false;

        furnaceUI = FurnaceUIController.Instance != null
            ? FurnaceUIController.Instance
            : FindAnyObjectByType<FurnaceUIController>();
        return furnaceUI != null;
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
        idleAnimatorStateId = ResolveAnimatorStateId(idleAnimatorStateName);
        transferAnimatorStateId = ResolveAnimatorStateId(transferAnimatorStateName);
        hasIdleAnimatorState = idleAnimatorStateId != 0;
        hasTransferAnimatorState = transferAnimatorStateId != 0;
        transferAnimationLengthSeconds = ResolveAnimationClipLengthSeconds(transferAnimatorStateName);
    }

    private int ResolveAnimatorStateId(string stateName)
    {
        if (animator == null || string.IsNullOrWhiteSpace(stateName) || animator.layerCount <= 0)
            return 0;

        int directStateId = Animator.StringToHash(stateName);
        if (animator.HasState(0, directStateId))
            return directStateId;

        string layerName = animator.GetLayerName(0);
        int fullStateId = Animator.StringToHash($"{layerName}.{stateName}");
        return animator.HasState(0, fullStateId) ? fullStateId : 0;
    }

    private float ResolveAnimationClipLengthSeconds(string clipName)
    {
        if (animator == null ||
            animator.runtimeAnimatorController == null ||
            string.IsNullOrWhiteSpace(clipName))
        {
            return 0f;
        }

        AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
        for (int i = 0; i < clips.Length; i++)
        {
            AnimationClip clip = clips[i];
            if (clip != null && clip.name == clipName)
                return Mathf.Max(0.01f, clip.length);
        }

        return 0f;
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
        if (!playTransferAnimation)
            active = false;

        grabDropAnimationActive = active;
        ApplyGrabDropAnimatorBool();
    }

    private void ApplyGrabDropAnimatorBool()
    {
        if (!hasGrabDropAnimatorBool || animator == null || !animator.enabled)
            return;

        animator.SetBool(grabDropAnimatorId, grabDropAnimationActive);
    }
}

internal static class RoboticArmHeldItemVisual
{
    private static Material fallbackMaterial;

    public static void Ensure(Transform root, Item item, string visualName)
    {
        if (root == null || item == null)
            return;

        if (TryCreateBlockVisual(root, item, visualName))
            return;

        CreateSpriteVisual(root, item, visualName);
    }

    private static bool TryCreateBlockVisual(Transform root, Item item, string visualName)
    {
        if (!BlockItemCatalog.TryGetBlockForItem(item, out BlockType blockType))
            return false;

        World world = World.Instance;
        if (world == null)
            return false;

        Mesh mesh = BlockDrop.BuildBlockMesh(world, blockType, out int materialIndex);
        if (mesh == null)
            return false;

        GameObject visualObject = CreateVisualObject(root, visualName);
        MeshFilter meshFilter = visualObject.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = mesh;

        MeshRenderer meshRenderer = visualObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = ResolveMaterial(world, materialIndex);
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        meshRenderer.receiveShadows = true;
        return meshRenderer.sharedMaterial != null;
    }

    private static void CreateSpriteVisual(Transform root, Item item, string visualName)
    {
        GameObject visualObject = CreateVisualObject(root, visualName);
        SpriteRenderer spriteRenderer = visualObject.AddComponent<SpriteRenderer>();
        spriteRenderer.sprite = ItemIconResolver.ResolveForUI(item);
        spriteRenderer.enabled = spriteRenderer.sprite != null;
    }

    private static GameObject CreateVisualObject(Transform root, string visualName)
    {
        GameObject visualObject = new GameObject(string.IsNullOrWhiteSpace(visualName) ? "HeldItemVisual" : visualName);
        visualObject.transform.SetParent(root, false);
        visualObject.transform.localPosition = Vector3.zero;
        visualObject.transform.localRotation = Quaternion.identity;
        visualObject.transform.localScale = Vector3.one;
        return visualObject;
    }

    private static Material ResolveMaterial(World world, int preferredMaterialIndex)
    {
        if (world != null && world.Material != null && world.Material.Length > 0)
        {
            int clamped = Mathf.Clamp(preferredMaterialIndex, 0, world.Material.Length - 1);
            if (world.Material[clamped] != null)
                return world.Material[clamped];

            for (int i = 0; i < world.Material.Length; i++)
            {
                if (world.Material[i] != null)
                    return world.Material[i];
            }
        }

        return GetFallbackMaterial();
    }

    private static Material GetFallbackMaterial()
    {
        if (fallbackMaterial != null)
            return fallbackMaterial;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
            return null;

        fallbackMaterial = new Material(shader)
        {
            name = "RoboticArmHeldItem_FallbackMaterial"
        };
        return fallbackMaterial;
    }
}

internal sealed class RoboticArmLimitedStackPickup : MonoBehaviour, IRoboticArmGrabbable, IRoboticArmItemStack
{
    private IRoboticArmGrabbable sourceGrabbable;
    private IRoboticArmItemStack sourceStack;
    private int maxAmount;
    private Item item;
    private int amount;
    private bool isAttached;
    private bool isReleased;

    public Transform DropTransform => transform;

    public bool CanBeGrabbedByRoboticArm =>
        !isAttached &&
        !isReleased &&
        sourceGrabbable != null &&
        sourceStack != null &&
        sourceGrabbable.CanBeGrabbedByRoboticArm &&
        sourceStack.TryGetRoboticArmItemStack(out Item sourceItem, out int sourceAmount) &&
        sourceItem != null &&
        sourceAmount > 0;

    public void Initialize(IRoboticArmGrabbable source, IRoboticArmItemStack stack, int sourceMaxAmount)
    {
        sourceGrabbable = source;
        sourceStack = stack;
        maxAmount = Mathf.Max(1, sourceMaxAmount);
    }

    public void AttachToRoboticArm(Transform parent, Vector3 localPosition, Quaternion localRotation, float localScale)
    {
        if (parent == null || !CanBeGrabbedByRoboticArm)
            return;

        if (!sourceStack.TryGetRoboticArmItemStack(out Item sourceItem, out int sourceAmount))
            return;

        int amountToTake = Mathf.Min(maxAmount, sourceAmount);
        int removed = sourceStack.RemoveFromRoboticArmStack(amountToTake);
        if (removed <= 0)
            return;

        item = sourceItem;
        amount = removed;
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
        RoboticArmHeldItemVisual.Ensure(transform, item, "HeldLimitedStackVisual");
    }
}

internal sealed class RoboticArmChestItemTransfer : MonoBehaviour, IRoboticArmGrabbable, IRoboticArmItemStack
{
    private ChestUIController chestUI;
    private Vector3Int chestBlock;
    private int maxAmount;
    private Item preferredItem;
    private Item item;
    private int amount;
    private bool isAttached;
    private bool isReleased;

    public Transform DropTransform => transform;

    public bool CanBeGrabbedByRoboticArm =>
        !isAttached &&
        !isReleased &&
        chestUI != null &&
        chestUI.HasItemStackInChest(chestBlock);

    public void Initialize(ChestUIController ownerChestUI, Vector3Int sourceChestBlock, int sourceMaxAmount, Item sourcePreferredItem = null)
    {
        chestUI = ownerChestUI;
        chestBlock = sourceChestBlock;
        maxAmount = Mathf.Max(1, sourceMaxAmount);
        preferredItem = sourcePreferredItem;
    }

    public void AttachToRoboticArm(Transform parent, Vector3 localPosition, Quaternion localRotation, float localScale)
    {
        if (parent == null || !CanBeGrabbedByRoboticArm)
            return;

        if (!chestUI.TryTakeItemStackFromChest(chestBlock, maxAmount, preferredItem, out item, out amount))
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
        RoboticArmHeldItemVisual.Ensure(transform, item, "HeldChestItemVisual");
    }
}

internal sealed class RoboticArmFurnaceOutputTransfer : MonoBehaviour, IRoboticArmGrabbable, IRoboticArmItemStack
{
    private FurnaceUIController furnaceUI;
    private Vector3Int furnaceBlock;
    private int maxAmount;
    private Item item;
    private int amount;
    private bool isAttached;
    private bool isReleased;

    public Transform DropTransform => transform;

    public bool CanBeGrabbedByRoboticArm =>
        !isAttached &&
        !isReleased &&
        furnaceUI != null &&
        furnaceUI.HasOutputStackInFurnace(furnaceBlock);

    public void Initialize(FurnaceUIController ownerFurnaceUI, Vector3Int sourceFurnaceBlock, int sourceMaxAmount)
    {
        furnaceUI = ownerFurnaceUI;
        furnaceBlock = sourceFurnaceBlock;
        maxAmount = Mathf.Max(1, sourceMaxAmount);
    }

    public void AttachToRoboticArm(Transform parent, Vector3 localPosition, Quaternion localRotation, float localScale)
    {
        if (parent == null || !CanBeGrabbedByRoboticArm)
            return;

        if (!furnaceUI.TryTakeOutputStackFromFurnace(furnaceBlock, maxAmount, out item, out amount))
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
        RoboticArmHeldItemVisual.Ensure(transform, item, "HeldFurnaceItemVisual");
    }
}
