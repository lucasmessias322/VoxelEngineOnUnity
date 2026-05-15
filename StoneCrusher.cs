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

    [Header("Magnetic Separator Outputs")]
    [SerializeField] private OutputYield[] magneticSeparatorOutputs =
    {
        new OutputYield { resourceItemPath = "Itens/Item/Iron_ingot", fallbackBlockType = BlockType.IronOre, chancePercent = 5f, maxBufferAmount = 128 },
        new OutputYield { resourceItemPath = "Itens/Item/Gold_ingot", fallbackBlockType = BlockType.GoldOre, chancePercent = 1f, maxBufferAmount = 128 },
        new OutputYield { resourceItemPath = "Itens/Item/copper_ingot", fallbackBlockType = BlockType.Copper_ore, chancePercent = 10f, maxBufferAmount = 128 }
    };

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

    [Header("Machine Audio")]
    [SerializeField] private MachineLoopAudio workingAudio = new MachineLoopAudio();

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

    [System.Serializable]
    private sealed class OutputYield
    {
        public Item item;
        public string resourceItemPath;
        public BlockType fallbackBlockType = BlockType.Air;
        [Range(0f, 100f)] public float chancePercent = 100f;
        [Min(1)] public int maxBufferAmount = 128;
        [Min(0)] public int bufferedAmount;
    }

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
        public MagneticOutputRuntimeState[] magneticOutputs;
        public float crushTimer;
        public bool isCrushing;
    }

    private sealed class MagneticOutputRuntimeState
    {
        public Item item;
        public string resourceItemPath;
        public BlockType fallbackBlockType;
        public float chancePercent;
        public int maxBufferAmount;
        public int bufferedAmount;
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
    public int OutputSlotCount => UsesMagneticSeparatorOutputs() ? Mathf.Max(1, GetMagneticOutputCount()) : 1;
    public bool DropOutputToWorld => !storeOutputInInternalBuffer;
    public float CrushProgress01 => isCrushing ? Mathf.Clamp01(crushTimer / Mathf.Max(0.05f, crushDurationSeconds)) : 0f;
    public float EnergyCharge01 => requireElectricity ? GetEnergyChargeAtCrusherFootprint(World.Instance) : 1f;

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
        workingAudio.Stop();
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
        workingAudio.Stop();

        if (ShouldKeepStateAfterDespawn())
            StoreRuntimeState();
        else
            RemoveStoredState(ResolveCrusherBlockPosition());
    }

    private void Update()
    {
        rollersPoweredThisFrame = false;
        UpdateCrushing(Time.deltaTime);
        workingAudio.UpdatePlayback(transform, ShouldSpinRollers());

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
        bool hasOutput = UsesMagneticSeparatorOutputs()
            ? HasAnyMagneticSeparatorOutput()
            : HasSingleOutput();

        return hasInput &&
               hasOutput &&
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
        if (!storeOutputInInternalBuffer)
            return true;

        if (!UsesMagneticSeparatorOutputs())
            return bufferedOutputAmount < MaxOutputBufferAmount;

        int outputCount = GetMagneticOutputCount();
        for (int i = 0; i < outputCount; i++)
        {
            OutputYield output = magneticSeparatorOutputs[i];
            if (!IsValidMagneticSeparatorOutput(output))
                continue;

            if (output.bufferedAmount >= GetMagneticMaxBufferAmount(output))
                return false;
        }

        return true;
    }

    private bool TryStoreOrOutputCrushedGravel()
    {
        if (UsesMagneticSeparatorOutputs())
            return TryStoreOrOutputMagneticSeparatorOutputs();

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

    private bool TryStoreOrOutputMagneticSeparatorOutputs()
    {
        int outputCount = GetMagneticOutputCount();
        for (int i = 0; i < outputCount; i++)
        {
            OutputYield output = magneticSeparatorOutputs[i];
            if (!IsValidMagneticSeparatorOutput(output) || !ShouldCreateOutput(output.chancePercent))
                continue;

            if (storeOutputInInternalBuffer)
            {
                int maxAmount = GetMagneticMaxBufferAmount(output);
                if (output.bufferedAmount >= maxAmount)
                    return false;

                output.bufferedAmount = Mathf.Clamp(output.bufferedAmount + 1, 0, maxAmount);
                continue;
            }

            if (!TrySpawnMagneticSeparatorOutputDrop(output, 1))
                return false;
        }

        return true;
    }

    private bool ShouldCreateOutput()
    {
        return !useOutputChance || ShouldCreateOutput(outputChancePercent);
    }

    private static bool ShouldCreateOutput(float chancePercent)
    {
        float chance01 = Mathf.Clamp01(chancePercent / 100f);
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
        return TrySpawnOutputDrop(ResolveOutputItem(), ResolveOutputBlockType(), amount);
    }

    private bool TrySpawnMagneticSeparatorOutputDrop(OutputYield output, int amount)
    {
        if (!TrySpawnOutputDrop(ResolveMagneticOutputItem(output), ResolveMagneticOutputBlockType(output), amount))
        {
            Debug.LogWarning($"[StoneCrusher] Falha ao gerar {ResolveMagneticOutputName(output)} na saida.");
            return false;
        }

        return true;
    }

    private bool TrySpawnOutputDrop(Item item, BlockType blockType, int amount)
    {
        if (amount <= 0)
            return true;

        Vector3 exitPosition = ResolveExitPosition();
        Vector3 throwDirection = ResolveOutputThrowDirection();
        if (item != null)
        {
            if (item.TryGetBlockType(out BlockType itemBlockType))
                return BlockDrop.Spawn(World.Instance, exitPosition, itemBlockType, amount, throwDirection);

            return InventoryItemDrop.Spawn(item, amount, exitPosition, throwDirection);
        }

        return blockType != BlockType.Air &&
               blockType != BlockType.Bedrock &&
               BlockDrop.Spawn(World.Instance, exitPosition, blockType, amount, throwDirection);
    }

    public void SetDropOutputToWorld(bool dropToWorld)
    {
        bool newStoreOutput = !dropToWorld;
        if (storeOutputInInternalBuffer == newStoreOutput)
            return;

        storeOutputInInternalBuffer = newStoreOutput;
        if (dropToWorld)
            FlushOutputBuffersToWorld();

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
        return RemoveOutputBuffer(0, amount);
    }

    public int RemoveOutputBuffer(int outputIndex, int amount)
    {
        if (amount <= 0)
            return 0;

        if (UsesMagneticSeparatorOutputs())
            return RemoveMagneticOutputBuffer(outputIndex, amount);

        if (bufferedOutputAmount <= 0)
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
        return GetOutputBufferFreeSpace(0);
    }

    public int GetOutputBufferFreeSpace(int outputIndex)
    {
        if (UsesMagneticSeparatorOutputs())
        {
            OutputYield output = GetMagneticOutput(outputIndex);
            return output != null ? Mathf.Max(0, GetMagneticMaxBufferAmount(output) - output.bufferedAmount) : 0;
        }

        return Mathf.Max(0, MaxOutputBufferAmount - bufferedOutputAmount);
    }

    public Item GetOutputItem(int outputIndex)
    {
        if (!UsesMagneticSeparatorOutputs())
            return outputIndex == 0 ? ResolveOutputItem() : null;

        return ResolveMagneticOutputItem(GetMagneticOutput(outputIndex));
    }

    public BlockType GetOutputBlockType(int outputIndex)
    {
        if (!UsesMagneticSeparatorOutputs())
            return outputIndex == 0 ? ResolveOutputBlockType() : BlockType.Air;

        return ResolveMagneticOutputBlockType(GetMagneticOutput(outputIndex));
    }

    public int GetOutputBufferAmount(int outputIndex)
    {
        if (!UsesMagneticSeparatorOutputs())
            return outputIndex == 0 ? bufferedOutputAmount : 0;

        OutputYield output = GetMagneticOutput(outputIndex);
        return output != null ? Mathf.Max(0, output.bufferedAmount) : 0;
    }

    public int GetMaxOutputBufferAmount(int outputIndex)
    {
        if (!UsesMagneticSeparatorOutputs())
            return outputIndex == 0 ? MaxOutputBufferAmount : 0;

        OutputYield output = GetMagneticOutput(outputIndex);
        return output != null ? GetMagneticMaxBufferAmount(output) : 0;
    }

    private void FlushOutputBuffersToWorld()
    {
        if (!UsesMagneticSeparatorOutputs())
        {
            if (bufferedOutputAmount > 0 && TryOutputCrushedGravelDrop(bufferedOutputAmount))
                bufferedOutputAmount = 0;

            return;
        }

        int outputCount = GetMagneticOutputCount();
        for (int i = 0; i < outputCount; i++)
        {
            OutputYield output = magneticSeparatorOutputs[i];
            if (output == null || output.bufferedAmount <= 0)
                continue;

            if (TrySpawnMagneticSeparatorOutputDrop(output, output.bufferedAmount))
                output.bufferedAmount = 0;
        }
    }

    private int RemoveMagneticOutputBuffer(int outputIndex, int amount)
    {
        OutputYield output = GetMagneticOutput(outputIndex);
        if (output == null || output.bufferedAmount <= 0)
            return 0;

        int removed = Mathf.Min(amount, output.bufferedAmount);
        output.bufferedAmount -= removed;
        StoreRuntimeState();
        return removed;
    }

    private bool UsesMagneticSeparatorOutputs()
    {
        return IsMagneticSeparatorMachine() && GetMagneticOutputCount() > 0;
    }

    private int GetMagneticOutputCount()
    {
        return magneticSeparatorOutputs != null ? magneticSeparatorOutputs.Length : 0;
    }

    private OutputYield GetMagneticOutput(int outputIndex)
    {
        if (magneticSeparatorOutputs == null || outputIndex < 0 || outputIndex >= magneticSeparatorOutputs.Length)
            return null;

        return magneticSeparatorOutputs[outputIndex];
    }

    private bool HasSingleOutput()
    {
        bool hasOutputItem = ResolveOutputItem() != null;
        BlockType resolvedOutputBlockType = ResolveOutputBlockType();
        bool hasOutputBlock = resolvedOutputBlockType != BlockType.Air && resolvedOutputBlockType != BlockType.Bedrock;
        return hasOutputItem || hasOutputBlock;
    }

    private bool HasAnyMagneticSeparatorOutput()
    {
        int outputCount = GetMagneticOutputCount();
        for (int i = 0; i < outputCount; i++)
        {
            if (IsValidMagneticSeparatorOutput(magneticSeparatorOutputs[i]))
                return true;
        }

        return false;
    }

    private bool IsValidMagneticSeparatorOutput(OutputYield output)
    {
        if (output == null)
            return false;

        if (ResolveMagneticOutputItem(output) != null)
            return true;

        BlockType blockType = ResolveMagneticOutputBlockType(output);
        return blockType != BlockType.Air && blockType != BlockType.Bedrock;
    }

    private static int GetMagneticMaxBufferAmount(OutputYield output)
    {
        return output != null ? Mathf.Max(1, output.maxBufferAmount) : 0;
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
            magneticOutputs = CaptureMagneticOutputRuntimeState(),
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
        RestoreMagneticOutputRuntimeState(state.magneticOutputs);
        crushTimer = Mathf.Max(0f, state.crushTimer);
        isCrushing = state.isCrushing && bufferedInputAmount > 0;
    }

    private MagneticOutputRuntimeState[] CaptureMagneticOutputRuntimeState()
    {
        int outputCount = GetMagneticOutputCount();
        if (outputCount <= 0)
            return null;

        MagneticOutputRuntimeState[] states = new MagneticOutputRuntimeState[outputCount];
        for (int i = 0; i < outputCount; i++)
        {
            OutputYield output = magneticSeparatorOutputs[i];
            if (output == null)
                continue;

            states[i] = new MagneticOutputRuntimeState
            {
                item = output.item,
                resourceItemPath = output.resourceItemPath,
                fallbackBlockType = output.fallbackBlockType,
                chancePercent = output.chancePercent,
                maxBufferAmount = output.maxBufferAmount,
                bufferedAmount = output.bufferedAmount
            };
        }

        return states;
    }

    private void RestoreMagneticOutputRuntimeState(MagneticOutputRuntimeState[] states)
    {
        if (states == null || states.Length == 0)
            return;

        EnsureMagneticOutputArray(states.Length);
        int outputCount = Mathf.Min(states.Length, GetMagneticOutputCount());
        for (int i = 0; i < outputCount; i++)
        {
            MagneticOutputRuntimeState state = states[i];
            OutputYield output = magneticSeparatorOutputs[i];
            if (state == null || output == null)
                continue;

            output.item = state.item;
            output.resourceItemPath = state.resourceItemPath;
            output.fallbackBlockType = state.fallbackBlockType;
            output.chancePercent = state.chancePercent;
            output.maxBufferAmount = Mathf.Max(1, state.maxBufferAmount);
            output.bufferedAmount = Mathf.Clamp(state.bufferedAmount, 0, GetMagneticMaxBufferAmount(output));
        }
    }

    private void EnsureMagneticOutputArray(int count)
    {
        count = Mathf.Max(0, count);
        if (magneticSeparatorOutputs != null && magneticSeparatorOutputs.Length >= count)
            return;

        OutputYield[] oldOutputs = magneticSeparatorOutputs;
        magneticSeparatorOutputs = new OutputYield[count];
        if (oldOutputs != null)
        {
            for (int i = 0; i < oldOutputs.Length; i++)
                magneticSeparatorOutputs[i] = oldOutputs[i];
        }

        for (int i = 0; i < magneticSeparatorOutputs.Length; i++)
        {
            if (magneticSeparatorOutputs[i] == null)
                magneticSeparatorOutputs[i] = new OutputYield();
        }
    }

    private bool ShouldKeepStateAfterDespawn()
    {
        World world = World.Instance;
        if (world == null || world.IsShuttingDown)
            return false;

        Vector3Int origin = ResolveCrusherBlockPosition();
        return world.GetBlockAt(origin) == ResolveMachineBlockType();
    }

    private static void RemoveStoredState(Vector3Int origin)
    {
        StoredStates.Remove(origin);
    }

    public static void NotifyWorldBlockChanged(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        if (!IsCrusherMachineBlockType(previousType) || newType == previousType)
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

    private Item ResolveMagneticOutputItem(OutputYield output)
    {
        if (output == null)
            return null;

        if (output.item != null)
            return output.item;

        if (!string.IsNullOrWhiteSpace(output.resourceItemPath))
        {
            Item resourceItem = Resources.Load<Item>(output.resourceItemPath);
            if (resourceItem != null)
                return resourceItem;
        }

        BlockType blockType = ResolveMagneticOutputBlockType(output);
        return BlockItemCatalog.TryGetItemForBlock(blockType, out Item resolvedItem) ? resolvedItem : null;
    }

    private static BlockType ResolveMagneticOutputBlockType(OutputYield output)
    {
        if (output == null)
            return BlockType.Air;

        if (output.item != null && output.item.TryGetBlockType(out BlockType itemBlockType))
            return itemBlockType;

        return output.fallbackBlockType;
    }

    private string ResolveOutputName()
    {
        Item resolvedOutputItem = ResolveOutputItem();
        if (resolvedOutputItem != null)
            return resolvedOutputItem.name;

        return ResolveOutputBlockType().ToString();
    }

    private string ResolveMagneticOutputName(OutputYield output)
    {
        Item resolvedOutputItem = ResolveMagneticOutputItem(output);
        if (resolvedOutputItem != null)
            return resolvedOutputItem.name;

        return ResolveMagneticOutputBlockType(output).ToString();
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
            BlockType machineBlockType = ResolveMachineBlockType();
            BlockTextureMapping? mappingResult = world.blockData.GetMapping(machineBlockType);
            if (mappingResult.HasValue)
            {
                BlockPlacementAxis placementAxis = world.GetPlacementAxisAt(origin, machineBlockType);
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

    private BlockType ResolveMachineBlockType()
    {
        if (IsCrusherMachineBlockType(BlockType))
            return BlockType;

        World world = World.Instance;
        if (world != null)
        {
            BlockType worldBlockType = world.GetBlockAt(ResolveCrusherBlockPosition());
            if (IsCrusherMachineBlockType(worldBlockType))
                return worldBlockType;
        }

        if (IsNamedLikeMagneticSeparator())
            return BlockType.MagneticSeparator;

        return BlockType.StoneCrusher;
    }

    private bool IsMagneticSeparatorMachine()
    {
        if (BlockType == BlockType.MagneticSeparator)
            return true;

        World world = World.Instance;
        if (world != null && world.GetBlockAt(ResolveCrusherBlockPosition()) == BlockType.MagneticSeparator)
            return true;

        return IsNamedLikeMagneticSeparator();
    }

    private bool IsNamedLikeMagneticSeparator()
    {
        return name.Contains("MagneticSeparator") || name.Contains("Magnetic Separator");
    }

    private static bool IsCrusherMachineBlockType(BlockType blockType)
    {
        return blockType == BlockType.StoneCrusher ||
               blockType == BlockType.MagneticSeparator;
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
        BlockType machineBlockType = ResolveMachineBlockType();
        for (int y = 0; y <= 1; y++)
        {
            for (int z = -1; z <= 3; z++)
            {
                for (int x = -1; x <= 3; x++)
                {
                    Vector3Int candidate = origin + new Vector3Int(x, y, z);
                    if (candidate == origin ||
                        world.GetBlockAt(candidate) != machineBlockType ||
                        !ContainsWorldBlock(candidate))
                    {
                        continue;
                    }

                    if (world.TryConsumeElectricalEnergy(candidate, amount))
                        return true;
                }
            }
        }

        return false;
    }

    private bool HasEnergyAtAdjacentCrusherBlocks(World world, Vector3Int origin, float amount)
    {
        BlockType machineBlockType = ResolveMachineBlockType();
        for (int y = 0; y <= 1; y++)
        {
            for (int z = -1; z <= 3; z++)
            {
                for (int x = -1; x <= 3; x++)
                {
                    Vector3Int candidate = origin + new Vector3Int(x, y, z);
                    if (candidate == origin ||
                        world.GetBlockAt(candidate) != machineBlockType ||
                        !ContainsWorldBlock(candidate))
                    {
                        continue;
                    }

                    if (world.HasElectricalEnergy(candidate, amount))
                        return true;
                }
            }
        }

        return false;
    }

    private float GetEnergyChargeAtCrusherFootprint(World world)
    {
        if (world == null)
            return 0f;

        Vector3Int origin = ResolveCrusherBlockPosition();
        float charge01 = world.GetElectricalEnergyCharge01(origin);
        BlockType machineBlockType = ResolveMachineBlockType();
        for (int y = 0; y <= 1; y++)
        {
            for (int z = -1; z <= 3; z++)
            {
                for (int x = -1; x <= 3; x++)
                {
                    Vector3Int candidate = origin + new Vector3Int(x, y, z);
                    if (candidate == origin ||
                        world.GetBlockAt(candidate) != machineBlockType ||
                        !ContainsWorldBlock(candidate))
                    {
                        continue;
                    }

                    charge01 = Mathf.Max(charge01, world.GetElectricalEnergyCharge01(candidate));
                }
            }
        }

        return Mathf.Clamp01(charge01);
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

[System.Serializable]
public sealed class MachineLoopAudio
{
    [SerializeField] private AudioClip clip;
    [SerializeField, Range(0f, 1f)] private float volume = 0.75f;
    [SerializeField, Min(0.01f)] private float minDistance = 1.5f;
    [SerializeField, Min(0.01f)] private float maxDistance = 18f;
    [SerializeField, Range(0.1f, 3f)] private float pitch = 1f;
    [SerializeField] private Vector3 localOffset = Vector3.zero;
    [SerializeField] private AudioRolloffMode rolloffMode = AudioRolloffMode.Linear;

    [System.NonSerialized] private AudioSource audioSource;
    [System.NonSerialized] private GameObject audioObject;

    public void UpdatePlayback(Transform owner, bool shouldPlay)
    {
        if (!shouldPlay || clip == null || owner == null)
        {
            Stop();
            return;
        }

        EnsureAudioSource(owner);
        if (audioSource == null)
            return;

        audioObject.transform.localPosition = localOffset;
        ApplySettings();

        if (!audioSource.isPlaying)
            audioSource.Play();
    }

    public void Stop()
    {
        if (audioSource != null && audioSource.isPlaying)
            audioSource.Stop();
    }

    private void EnsureAudioSource(Transform owner)
    {
        if (audioSource != null)
            return;

        audioObject = new GameObject("MachineLoopAudio");
        audioObject.transform.SetParent(owner, false);
        audioObject.transform.localPosition = localOffset;

        audioSource = audioObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.loop = true;
        audioSource.spatialBlend = 1f;
        ApplySettings();
    }

    private void ApplySettings()
    {
        if (audioSource == null)
            return;

        audioSource.clip = clip;
        audioSource.volume = Mathf.Clamp01(volume);
        audioSource.pitch = Mathf.Max(0.1f, pitch);
        audioSource.spatialBlend = 1f;
        audioSource.rolloffMode = rolloffMode;
        audioSource.minDistance = Mathf.Max(0.01f, minDistance);
        audioSource.maxDistance = Mathf.Max(audioSource.minDistance, maxDistance);
        audioSource.dopplerLevel = 0f;
    }
}
