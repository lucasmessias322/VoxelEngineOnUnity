using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

[DisallowMultipleComponent]
public sealed class StoneCrusher : DynamicVoxelBlock
{
    private static readonly List<StoneCrusher> ActiveCrushers = new List<StoneCrusher>(32);
    private static readonly Dictionary<Vector3Int, CrusherRuntimeState> StoredStates = new Dictionary<Vector3Int, CrusherRuntimeState>();

    [Header("IO")]
    [SerializeField] private Transform entryPoint;
    [SerializeField] private Transform exitPoint;
    [SerializeField] private string entryPointFallbackName = "EntryPoint";
    [SerializeField] private string exitPointFallbackName = "ExitPoint";
    [SerializeField] private Vector3 fallbackEntryLocalPosition = new Vector3(0f, 1.35f, 0f);
    [SerializeField] private Vector3 fallbackExitLocalPosition = new Vector3(0f, -0.2f, 1.25f);

    [Header("Recipe")]
    [SerializeField] private Item inputItem;
    [SerializeField] private Item outputItem;
    [SerializeField, HideInInspector, FormerlySerializedAs("inputBlockType")] private BlockType legacyInputBlockType = BlockType.Stone;
    [SerializeField, HideInInspector, FormerlySerializedAs("outputBlockType")] private BlockType legacyOutputBlockType = BlockType.Gravel;

    [Header("Yield Chance")]
    [SerializeField] private bool useOutputChance;
    [SerializeField, Range(0f, 100f)] private float outputChancePercent = 100f;

    [Header("Detection")]
    [SerializeField, Min(0.05f)] private float entryRadius = 0.55f;
    [SerializeField, Min(0.05f)] private float entryVerticalTolerance = 2f;
    [SerializeField, Min(0.02f)] private float scanIntervalSeconds = 0.1f;

    [Header("Processing")]
    [SerializeField, Min(0.05f)] private float crushDurationSeconds = 5f;
    [SerializeField] private bool requireElectricity = true;
    [SerializeField, Min(0f)] private float energyPerSecond = 4f;
    [SerializeField, Min(1)] private int maxInputBufferAmount = 128;
    [SerializeField, Min(0)] private int bufferedInputAmount;
    [SerializeField, Min(1)] private int maxOutputBufferAmount = 128;
    [SerializeField, Min(0)] private int bufferedOutputAmount;
    [SerializeField] private bool storeOutputInInternalBuffer = true;

    [Header("Output")]
    [SerializeField] private Vector3 outputThrowLocalDirection = new Vector3(0f, 0.35f, 1f);
    [SerializeField, Min(0f)] private float outputThrowStrength = 0.8f;

    [Header("Crusher Rollers")]
    [SerializeField] private Transform rollerA;
    [SerializeField] private Transform rollerB;
    [SerializeField] private string rollerAFallbackName = "cylinder";
    [SerializeField] private string rollerBFallbackName = "cylinder_1";
    [SerializeField] private Vector3 rollerLocalRotationAxis = Vector3.forward;
    [SerializeField, Min(0f)] private float rollerDegreesPerSecond = 360f;
    [SerializeField] private bool rotateRollersInOppositeDirections = true;

    [Header("Crusher Quad")]
    [SerializeField] private Transform movingQuad;
    [SerializeField] private string movingQuadFallbackName = "Quad";
    [SerializeField] private Vector3 movingQuadLocalDirection = Vector3.forward;
    [SerializeField, Min(0f)] private float movingQuadTravelDistance = 0.25f;
    [SerializeField, Min(0f)] private float movingQuadCyclesPerSecond = 1f;
    [SerializeField] private bool moveQuadOnlyWhileCrushing = true;

    [Header("Animation Optimization")]
    [SerializeField] private bool disableVisualAnimationsOutsideSimulationDistance = true;
    [SerializeField, Min(0.05f)] private float visualSimulationCheckInterval = 0.25f;

    private float nextScanTime;
    private float crushTimer;
    private bool isCrushing;
    private bool rollersPoweredThisFrame;
    private bool visualAnimationsPausedBySimulationDistance;
    private float nextVisualSimulationCheckTime;
    private float lastVisualAnimationTime;
    private float movingQuadPhase;
    private Transform initialMovingQuadTransform;
    private Vector3 initialMovingQuadLocalPosition;
    private bool hasInitialMovingQuadLocalPosition;

    private sealed class CrusherRuntimeState
    {
        public Item inputItem;
        public Item outputItem;
        public BlockType legacyInputBlockType;
        public BlockType legacyOutputBlockType;
        public bool useOutputChance;
        public float outputChancePercent;
        public float crushDurationSeconds;
        public bool requireElectricity;
        public float energyPerSecond;
        public int maxInputBufferAmount;
        public int bufferedInputAmount;
        public int maxOutputBufferAmount;
        public int bufferedOutputAmount;
        public bool storeOutputInInternalBuffer;
        public float crushTimer;
        public bool isCrushing;
    }

    public Item InputItem => ResolveInputItem();
    public Item OutputItem => ResolveOutputItem();
    public BlockType InputBlockType => ResolveInputBlockType();
    public BlockType OutputBlockType => ResolveOutputBlockType();
    public bool UseOutputChance => useOutputChance;
    public float OutputChancePercent => Mathf.Clamp(outputChancePercent, 0f, 100f);
    public int InputBufferAmount => bufferedInputAmount;
    public int OutputBufferAmount => bufferedOutputAmount;
    public int MaxInputBufferAmount => Mathf.Max(1, maxInputBufferAmount);
    public int MaxOutputBufferAmount => Mathf.Max(1, maxOutputBufferAmount);
    public bool DropOutputToWorld => !storeOutputInInternalBuffer;
    public float CrushProgress01 => isCrushing ? Mathf.Clamp01(crushTimer / Mathf.Max(0.05f, crushDurationSeconds)) : 0f;

    private void Awake()
    {
        SyncLegacyRecipeFromItems();
        RegisterCrusher();
        ResolveReferences();
        ResetScanTimer();
        ResetVisualAnimationTiming();
    }

    private void OnEnable()
    {
        RegisterCrusher();
        ResetScanTimer();
        ResetVisualAnimationTiming();
    }

    private void OnDisable()
    {
        ActiveCrushers.Remove(this);
    }

    protected override void OnDynamicBlockSpawned()
    {
        SyncLegacyRecipeFromItems();
        RestoreStoredState();
        RegisterCrusher();
        ResolveReferences();
        ResetScanTimer();
        ResetVisualAnimationTiming();
    }

    protected override void OnDynamicBlockDespawned()
    {
        ActiveCrushers.Remove(this);
        ResetMovingQuadPosition();
        ResetVisualAnimationTiming();

        if (ShouldKeepStateAfterDespawn())
            StoreRuntimeState();
        else
            RemoveStoredState(ResolveCrusherBlockPosition());
    }

    private void Update()
    {
        rollersPoweredThisFrame = false;
        UpdateCrushing(Time.deltaTime);

        float visualDeltaTime = ConsumeVisualAnimationDeltaTime();
        if (visualDeltaTime <= 0f)
            return;

        UpdateRollers(visualDeltaTime);
        UpdateMovingQuad(visualDeltaTime);
    }

    private void UpdateCrushing(float deltaTime)
    {
        if (!CanConvert())
        {
            ResetCrushing();
            return;
        }

        AbsorbInputDropsInEntry();

        if (bufferedInputAmount <= 0)
        {
            StopCrushing();
            return;
        }

        if (!isCrushing)
        {
            isCrushing = true;
            crushTimer = 0f;
        }

        if (!CanAcceptCrushedOutput())
            return;

        if (!TryConsumeProcessingEnergy(deltaTime))
            return;

        crushTimer += Mathf.Max(0f, deltaTime);
        float duration = Mathf.Max(0.05f, crushDurationSeconds);
        if (crushTimer < duration)
            return;

        if (!TryStoreOrOutputCrushedGravel())
            return;

        bufferedInputAmount = Mathf.Max(0, bufferedInputAmount - 1);
        crushTimer -= duration;

        if (bufferedInputAmount <= 0)
            ResetCrushing();
    }

    private bool CanConvert()
    {
        bool hasInput = ResolveInputItem() != null || ResolveInputBlockType() != BlockType.Air;
        bool hasOutputItem = ResolveOutputItem() != null;
        BlockType resolvedOutputBlockType = ResolveOutputBlockType();
        bool hasOutputBlock = resolvedOutputBlockType != BlockType.Air && resolvedOutputBlockType != BlockType.Bedrock;

        return hasInput &&
               (hasOutputItem || hasOutputBlock) &&
               World.Instance != null;
    }

    private void AbsorbInputDropsInEntry()
    {
        if (Time.time < nextScanTime)
            return;

        nextScanTime = Time.time + Mathf.Max(0.02f, scanIntervalSeconds);
        if (GetInputBufferFreeSpace() <= 0)
            return;

        int absorbed = AbsorbBlockDropInputs() + AbsorbItemDropInputs();
        if (absorbed > 0)
            bufferedInputAmount = Mathf.Clamp(bufferedInputAmount + absorbed, 0, MaxInputBufferAmount);
    }

    private int AbsorbBlockDropInputs()
    {
        int absorbed = 0;
        BlockDrop[] drops = FindObjectsByType<BlockDrop>(FindObjectsInactive.Exclude);
        for (int i = 0; i < drops.Length; i++)
        {
            BlockDrop drop = drops[i];
            if (GetInputBufferFreeSpace() <= 0)
                break;

            if (!IsValidBlockInput(drop))
                continue;

            absorbed += AbsorbInputStack(drop);
        }

        return absorbed;
    }

    private int AbsorbItemDropInputs()
    {
        int absorbed = 0;
        InventoryItemDrop[] drops = FindObjectsByType<InventoryItemDrop>(FindObjectsInactive.Exclude);
        for (int i = 0; i < drops.Length; i++)
        {
            InventoryItemDrop drop = drops[i];
            if (GetInputBufferFreeSpace() <= 0)
                break;

            if (!IsValidItemInput(drop))
                continue;

            absorbed += AbsorbInputStack(drop);
        }

        return absorbed;
    }

    private int AbsorbInputStack(IRoboticArmItemStack stack)
    {
        if (stack == null || !stack.TryGetRoboticArmItemStack(out _, out int amount) || amount <= 0)
            return 0;

        int acceptedAmount = Mathf.Min(amount, GetInputBufferFreeSpace());
        return acceptedAmount > 0 ? stack.RemoveFromRoboticArmStack(acceptedAmount) : 0;
    }

    private bool IsValidBlockInput(BlockDrop drop)
    {
        BlockType resolvedInputBlockType = ResolveInputBlockType();
        return drop != null &&
               drop.CanBeGrabbedByRoboticArm &&
               resolvedInputBlockType != BlockType.Air &&
               drop.DroppedBlockType == resolvedInputBlockType &&
               IsInsideEntry(drop.DropTransform) &&
               drop.TryGetRoboticArmItemStack(out _, out int amount) &&
               amount > 0;
    }

    private bool IsValidItemInput(InventoryItemDrop drop)
    {
        return drop != null &&
               drop.CanBeGrabbedByRoboticArm &&
               IsInsideEntry(drop.DropTransform) &&
               drop.TryGetRoboticArmItemStack(out Item item, out int amount) &&
               amount > 0 &&
               IsValidInputItem(item);
    }

    public bool IsValidInputItem(Item item)
    {
        if (item == null)
            return false;

        Item resolvedInputItem = ResolveInputItem();
        if (resolvedInputItem != null && item == resolvedInputItem)
            return true;

        BlockType resolvedInputBlockType = ResolveInputBlockType();
        return resolvedInputBlockType != BlockType.Air &&
               BlockItemCatalog.TryGetBlockForItem(item, out BlockType blockType) &&
               blockType == resolvedInputBlockType;
    }

    private bool TryConsumeProcessingEnergy(float deltaTime)
    {
        if (!requireElectricity)
            return true;

        World world = World.Instance;
        if (world == null)
            return false;

        float energyRequired = Mathf.Max(0f, energyPerSecond) * Mathf.Max(0f, deltaTime);
        if (!TryConsumeEnergyAtCrusherFootprint(world, energyRequired))
            return false;

        rollersPoweredThisFrame = true;
        return true;
    }

    private bool CanAcceptCrushedOutput()
    {
        return !storeOutputInInternalBuffer || bufferedOutputAmount < MaxOutputBufferAmount;
    }

    private bool TryStoreOrOutputCrushedGravel()
    {
        if (!ShouldCreateOutput())
            return true;

        if (storeOutputInInternalBuffer)
        {
            if (bufferedOutputAmount >= MaxOutputBufferAmount)
                return false;

            bufferedOutputAmount = Mathf.Clamp(bufferedOutputAmount + 1, 0, MaxOutputBufferAmount);
            return true;
        }

        return TryOutputCrushedGravelDrop();
    }

    private bool ShouldCreateOutput()
    {
        if (!useOutputChance)
            return true;

        float chance01 = Mathf.Clamp01(outputChancePercent / 100f);
        return chance01 >= 1f || (chance01 > 0f && Random.value < chance01);
    }

    private bool TryOutputCrushedGravelDrop()
    {
        if (!TrySpawnOutputDrop(1))
        {
            Debug.LogWarning($"[StoneCrusher] Falha ao gerar {ResolveOutputName()} na saida.");
            return false;
        }

        return true;
    }

    private bool TryOutputCrushedGravelDrop(int amount)
    {
        if (amount <= 0)
            return true;

        if (!TrySpawnOutputDrop(amount))
        {
            Debug.LogWarning($"[StoneCrusher] Falha ao gerar {ResolveOutputName()} na saida.");
            return false;
        }

        return true;
    }

    private bool TrySpawnOutputDrop(int amount)
    {
        if (amount <= 0)
            return true;

        Item resolvedOutputItem = ResolveOutputItem();
        Vector3 exitPosition = ResolveExitPosition();
        Vector3 throwDirection = ResolveOutputThrowDirection();
        if (resolvedOutputItem != null)
        {
            if (resolvedOutputItem.TryGetBlockType(out BlockType itemBlockType))
                return BlockDrop.Spawn(World.Instance, exitPosition, itemBlockType, amount, throwDirection);

            return InventoryItemDrop.Spawn(resolvedOutputItem, amount, exitPosition, throwDirection);
        }

        BlockType resolvedOutputBlockType = ResolveOutputBlockType();
        return resolvedOutputBlockType != BlockType.Air &&
               resolvedOutputBlockType != BlockType.Bedrock &&
               BlockDrop.Spawn(World.Instance, exitPosition, resolvedOutputBlockType, amount, throwDirection);
    }

    public void SetDropOutputToWorld(bool dropToWorld)
    {
        bool newStoreOutput = !dropToWorld;
        if (storeOutputInInternalBuffer == newStoreOutput)
            return;

        storeOutputInInternalBuffer = newStoreOutput;
        if (dropToWorld && bufferedOutputAmount > 0 && TryOutputCrushedGravelDrop(bufferedOutputAmount))
            bufferedOutputAmount = 0;

        StoreRuntimeState();
    }

    public int TryInsertInputBuffer(int amount)
    {
        if (amount <= 0)
            return 0;

        int accepted = Mathf.Min(amount, GetInputBufferFreeSpace());
        if (accepted <= 0)
            return 0;

        bufferedInputAmount += accepted;
        StoreRuntimeState();
        return accepted;
    }

    public int RemoveInputBuffer(int amount)
    {
        if (amount <= 0 || bufferedInputAmount <= 0)
            return 0;

        int removed = Mathf.Min(amount, bufferedInputAmount);
        bufferedInputAmount -= removed;
        if (bufferedInputAmount <= 0)
            ResetCrushing();

        StoreRuntimeState();
        return removed;
    }

    public int RemoveOutputBuffer(int amount)
    {
        if (amount <= 0 || bufferedOutputAmount <= 0)
            return 0;

        int removed = Mathf.Min(amount, bufferedOutputAmount);
        bufferedOutputAmount -= removed;
        StoreRuntimeState();
        return removed;
    }

    public int GetInputBufferFreeSpace()
    {
        return Mathf.Max(0, MaxInputBufferAmount - bufferedInputAmount);
    }

    public int GetOutputBufferFreeSpace()
    {
        return Mathf.Max(0, MaxOutputBufferAmount - bufferedOutputAmount);
    }

    private void ResetCrushing()
    {
        StopCrushing();
        nextScanTime = Time.time + Mathf.Max(0.02f, scanIntervalSeconds);
    }

    private void StopCrushing()
    {
        isCrushing = false;
        crushTimer = 0f;
    }

    private void StoreRuntimeState()
    {
        Vector3Int origin = ResolveCrusherBlockPosition();
        StoredStates[origin] = new CrusherRuntimeState
        {
            inputItem = inputItem,
            outputItem = outputItem,
            legacyInputBlockType = legacyInputBlockType,
            legacyOutputBlockType = legacyOutputBlockType,
            useOutputChance = useOutputChance,
            outputChancePercent = outputChancePercent,
            crushDurationSeconds = crushDurationSeconds,
            requireElectricity = requireElectricity,
            energyPerSecond = energyPerSecond,
            maxInputBufferAmount = maxInputBufferAmount,
            bufferedInputAmount = bufferedInputAmount,
            maxOutputBufferAmount = maxOutputBufferAmount,
            bufferedOutputAmount = bufferedOutputAmount,
            storeOutputInInternalBuffer = storeOutputInInternalBuffer,
            crushTimer = crushTimer,
            isCrushing = isCrushing
        };
    }

    private void RestoreStoredState()
    {
        Vector3Int origin = ResolveCrusherBlockPosition();
        if (!StoredStates.TryGetValue(origin, out CrusherRuntimeState state) || state == null)
            return;

        inputItem = state.inputItem;
        outputItem = state.outputItem;
        legacyInputBlockType = state.legacyInputBlockType;
        legacyOutputBlockType = state.legacyOutputBlockType;
        useOutputChance = state.useOutputChance;
        outputChancePercent = state.outputChancePercent;
        crushDurationSeconds = state.crushDurationSeconds;
        requireElectricity = state.requireElectricity;
        energyPerSecond = state.energyPerSecond;
        maxInputBufferAmount = Mathf.Max(1, state.maxInputBufferAmount);
        bufferedInputAmount = Mathf.Clamp(state.bufferedInputAmount, 0, MaxInputBufferAmount);
        maxOutputBufferAmount = Mathf.Max(1, state.maxOutputBufferAmount);
        bufferedOutputAmount = Mathf.Clamp(state.bufferedOutputAmount, 0, MaxOutputBufferAmount);
        storeOutputInInternalBuffer = state.storeOutputInInternalBuffer;
        crushTimer = Mathf.Max(0f, state.crushTimer);
        isCrushing = state.isCrushing && bufferedInputAmount > 0;
    }

    private bool ShouldKeepStateAfterDespawn()
    {
        World world = World.Instance;
        if (world == null || world.IsShuttingDown)
            return false;

        Vector3Int origin = ResolveCrusherBlockPosition();
        if (world.GetBlockAt(origin) == BlockType.StoneCrusher)
            return true;

        return false;
    }

    private static void RemoveStoredState(Vector3Int origin)
    {
        StoredStates.Remove(origin);
    }

    public static void NotifyWorldBlockChanged(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        if (previousType != BlockType.StoneCrusher || newType == BlockType.StoneCrusher)
            return;

        RemoveStoredState(worldPos);
    }

    private bool IsInsideEntry(Transform dropTransform)
    {
        if (dropTransform == null)
            return false;

        Vector3 delta = dropTransform.position - ResolveEntryPosition();
        if (Mathf.Abs(delta.y) > entryVerticalTolerance)
            return false;

        delta.y = 0f;
        float radius = Mathf.Max(0.05f, entryRadius);
        return delta.sqrMagnitude <= radius * radius;
    }

    private Vector3 ResolveEntryPosition()
    {
        return entryPoint != null ? entryPoint.position : transform.TransformPoint(fallbackEntryLocalPosition);
    }

    private Vector3 ResolveExitPosition()
    {
        return exitPoint != null ? exitPoint.position : transform.TransformPoint(fallbackExitLocalPosition);
    }

    private Vector3 ResolveOutputThrowDirection()
    {
        Vector3 localDirection = outputThrowLocalDirection.sqrMagnitude > 0.0001f
            ? outputThrowLocalDirection
            : Vector3.forward;

        return transform.TransformDirection(localDirection.normalized) * Mathf.Max(0f, outputThrowStrength);
    }

    private Item ResolveInputItem()
    {
        if (inputItem != null)
            return inputItem;

        return BlockItemCatalog.TryGetItemForBlock(legacyInputBlockType, out Item resolvedItem) ? resolvedItem : null;
    }

    private Item ResolveOutputItem()
    {
        if (outputItem != null)
            return outputItem;

        return BlockItemCatalog.TryGetItemForBlock(legacyOutputBlockType, out Item resolvedItem) ? resolvedItem : null;
    }

    private BlockType ResolveInputBlockType()
    {
        if (inputItem != null && inputItem.TryGetBlockType(out BlockType itemBlockType))
            return itemBlockType;

        return legacyInputBlockType;
    }

    private BlockType ResolveOutputBlockType()
    {
        if (outputItem != null && outputItem.TryGetBlockType(out BlockType itemBlockType))
            return itemBlockType;

        return legacyOutputBlockType;
    }

    private string ResolveOutputName()
    {
        Item resolvedOutputItem = ResolveOutputItem();
        if (resolvedOutputItem != null)
            return resolvedOutputItem.name;

        return ResolveOutputBlockType().ToString();
    }

    private void SyncLegacyRecipeFromItems()
    {
        if (inputItem != null && inputItem.TryGetBlockType(out BlockType inputBlockType))
            legacyInputBlockType = inputBlockType;

        if (outputItem != null && outputItem.TryGetBlockType(out BlockType outputBlockType))
            legacyOutputBlockType = outputBlockType;
    }

    private void ResolveReferences()
    {
        ResolveIOPoints();
        ResolveRollers();
        ResolveMovingQuad();
    }

    private void RegisterCrusher()
    {
        if (!ActiveCrushers.Contains(this))
            ActiveCrushers.Add(this);
    }

    public bool ContainsWorldBlock(Vector3Int blockPosition)
    {
        Vector3Int origin = ResolveCrusherBlockPosition();
        if (blockPosition == origin)
            return true;

        World world = World.Instance;
        if (world != null && world.blockData != null)
        {
            BlockTextureMapping? mappingResult = world.blockData.GetMapping(BlockType.StoneCrusher);
            if (mappingResult.HasValue)
            {
                BlockPlacementAxis placementAxis = world.GetPlacementAxisAt(origin, BlockType.StoneCrusher);
                Vector3Int localOffset = blockPosition - origin;
                return BlockShapeUtility.IsLocalOffsetInsideDynamicOccupancy(
                    localOffset,
                    mappingResult.Value,
                    placementAxis);
            }
        }

        Vector3Int delta = blockPosition - origin;
        return delta.x == 0 && delta.y == 0 && delta.z == 0;
    }

    public static bool TryFindAtWorldBlock(Vector3Int blockPosition, out StoneCrusher crusher)
    {
        for (int i = ActiveCrushers.Count - 1; i >= 0; i--)
        {
            StoneCrusher candidate = ActiveCrushers[i];
            if (candidate == null)
            {
                ActiveCrushers.RemoveAt(i);
                continue;
            }

            if (!candidate.ContainsWorldBlock(blockPosition))
                continue;

            crusher = candidate;
            return true;
        }

        StoneCrusher[] sceneCrushers = FindObjectsByType<StoneCrusher>(FindObjectsInactive.Exclude);
        for (int i = 0; i < sceneCrushers.Length; i++)
        {
            StoneCrusher candidate = sceneCrushers[i];
            if (candidate == null || !candidate.ContainsWorldBlock(blockPosition))
                continue;

            candidate.RegisterCrusher();
            crusher = candidate;
            return true;
        }

        crusher = null;
        return false;
    }

    private void ResolveIOPoints()
    {
        if (entryPoint == null)
            entryPoint = FindDeepChild(transform, entryPointFallbackName);

        if (exitPoint == null)
            exitPoint = FindDeepChild(transform, exitPointFallbackName);
    }

    private void ResolveRollers()
    {
        if (rollerA == null)
            rollerA = FindDeepChild(transform, rollerAFallbackName);

        if (rollerB == null)
            rollerB = FindDeepChild(transform, rollerBFallbackName);
    }

    private void ResolveMovingQuad()
    {
        if (movingQuad == null)
            movingQuad = FindDeepChild(transform, movingQuadFallbackName);

        CacheInitialMovingQuadPosition();
    }

    private void CacheInitialMovingQuadPosition()
    {
        if (movingQuad == null)
            return;

        if (hasInitialMovingQuadLocalPosition && initialMovingQuadTransform == movingQuad)
            return;

        initialMovingQuadTransform = movingQuad;
        initialMovingQuadLocalPosition = movingQuad.localPosition;
        hasInitialMovingQuadLocalPosition = true;
    }

    private float ConsumeVisualAnimationDeltaTime()
    {
        if (!ShouldUpdateVisualAnimations())
        {
            lastVisualAnimationTime = Time.time;
            return 0f;
        }

        float deltaTime = Mathf.Max(0f, Time.time - lastVisualAnimationTime);
        lastVisualAnimationTime = Time.time;
        return deltaTime;
    }

    private bool ShouldUpdateVisualAnimations()
    {
        if (!disableVisualAnimationsOutsideSimulationDistance)
        {
            visualAnimationsPausedBySimulationDistance = false;
            return true;
        }

        if (Time.time < nextVisualSimulationCheckTime)
            return !visualAnimationsPausedBySimulationDistance;

        nextVisualSimulationCheckTime = Time.time + Mathf.Max(0.05f, visualSimulationCheckInterval);

        World world = World.Instance;
        if (world == null)
        {
            visualAnimationsPausedBySimulationDistance = false;
            return true;
        }

        bool insideSimulationDistance = world.IsWorldPositionInsidePlayerSimulationDistance(ResolveCrusherBlockPosition());
        visualAnimationsPausedBySimulationDistance = !insideSimulationDistance;
        return insideSimulationDistance;
    }

    private void ResetVisualAnimationTiming()
    {
        lastVisualAnimationTime = Time.time;
        nextVisualSimulationCheckTime = 0f;
        visualAnimationsPausedBySimulationDistance = false;
    }

    private void UpdateRollers(float deltaTime)
    {
        if (rollerDegreesPerSecond <= 0f)
            return;

        if (!ShouldSpinRollers())
            return;

        if (rollerA == null || rollerB == null)
            ResolveRollers();

        Vector3 axis = rollerLocalRotationAxis.sqrMagnitude > 0.0001f
            ? rollerLocalRotationAxis.normalized
            : Vector3.forward;

        float angle = rollerDegreesPerSecond * Mathf.Max(0f, deltaTime);
        if (rollerA != null)
            rollerA.Rotate(axis, angle, Space.Self);

        if (rollerB != null)
            rollerB.Rotate(axis, rotateRollersInOppositeDirections ? -angle : angle, Space.Self);
    }

    private void UpdateMovingQuad(float deltaTime)
    {
        if (moveQuadOnlyWhileCrushing && !ShouldSpinRollers())
        {
            ResetMovingQuadPosition();
            return;
        }

        if (movingQuadCyclesPerSecond <= 0f || movingQuadTravelDistance <= 0f)
            return;

        if (movingQuad == null || !hasInitialMovingQuadLocalPosition)
            ResolveMovingQuad();

        if (movingQuad == null || !hasInitialMovingQuadLocalPosition)
            return;

        Vector3 direction = movingQuadLocalDirection.sqrMagnitude > 0.0001f
            ? movingQuadLocalDirection.normalized
            : Vector3.forward;

        movingQuadPhase = Mathf.Repeat(movingQuadPhase + movingQuadCyclesPerSecond * Mathf.Max(0f, deltaTime), 1f);
        float signedOffset = Mathf.Sin(movingQuadPhase * Mathf.PI * 2f) * movingQuadTravelDistance;
        movingQuad.localPosition = initialMovingQuadLocalPosition + direction * signedOffset;
    }

    private void ResetMovingQuadPosition()
    {
        if (movingQuad == null || !hasInitialMovingQuadLocalPosition)
            return;

        movingQuad.localPosition = initialMovingQuadLocalPosition;
    }

    private bool ShouldSpinRollers()
    {
        if (!requireElectricity)
            return isCrushing && bufferedInputAmount > 0;

        return rollersPoweredThisFrame;
    }

    private Vector3Int ResolveCrusherBlockPosition()
    {
        if (OwnerChunk != null)
            return WorldPosition;

        return Vector3Int.FloorToInt(transform.position);
    }

    private bool TryConsumeEnergyAtCrusherFootprint(World world, float amount)
    {
        if (world == null)
            return false;

        Vector3Int origin = ResolveCrusherBlockPosition();
        if (world.TryConsumeElectricalEnergy(origin, amount))
            return true;

        return TryConsumeEnergyAtAdjacentCrusherBlocks(world, origin, amount);
    }

    private bool HasEnergyAtCrusherFootprint(World world, float amount)
    {
        if (world == null)
            return false;

        Vector3Int origin = ResolveCrusherBlockPosition();
        if (world.HasElectricalEnergy(origin, amount))
            return true;

        return HasEnergyAtAdjacentCrusherBlocks(world, origin, amount);
    }

    private bool TryConsumeEnergyAtAdjacentCrusherBlocks(World world, Vector3Int origin, float amount)
    {
        for (int y = 0; y <= 1; y++)
        {
            for (int z = -1; z <= 3; z++)
            {
                for (int x = -1; x <= 3; x++)
                {
                    Vector3Int candidate = origin + new Vector3Int(x, y, z);
                    if (candidate == origin || world.GetBlockAt(candidate) != BlockType.StoneCrusher)
                        continue;

                    if (world.TryConsumeElectricalEnergy(candidate, amount))
                        return true;
                }
            }
        }

        return false;
    }

    private bool HasEnergyAtAdjacentCrusherBlocks(World world, Vector3Int origin, float amount)
    {
        for (int y = 0; y <= 1; y++)
        {
            for (int z = -1; z <= 3; z++)
            {
                for (int x = -1; x <= 3; x++)
                {
                    Vector3Int candidate = origin + new Vector3Int(x, y, z);
                    if (candidate == origin || world.GetBlockAt(candidate) != BlockType.StoneCrusher)
                        continue;

                    if (world.HasElectricalEnergy(candidate, amount))
                        return true;
                }
            }
        }

        return false;
    }

    private void ResetScanTimer()
    {
        nextScanTime = Time.time + Mathf.Max(0.02f, scanIntervalSeconds);
    }

    private static Transform FindDeepChild(Transform parent, string childName)
    {
        if (parent == null || string.IsNullOrWhiteSpace(childName))
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

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(ResolveEntryPosition(), Mathf.Max(0.05f, entryRadius));

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(ResolveExitPosition(), 0.18f);
    }
}
