using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class StoneCrusher : DynamicVoxelBlock
{
    private static readonly List<StoneCrusher> ActiveCrushers = new List<StoneCrusher>(32);

    [Header("IO")]
    [SerializeField] private Transform entryPoint;
    [SerializeField] private Transform exitPoint;
    [SerializeField] private string entryPointFallbackName = "EntryPoint";
    [SerializeField] private string exitPointFallbackName = "ExitPoint";
    [SerializeField] private Vector3 fallbackEntryLocalPosition = new Vector3(0f, 1.35f, 0f);
    [SerializeField] private Vector3 fallbackExitLocalPosition = new Vector3(0f, -0.2f, 1.25f);

    [Header("Recipe")]
    [SerializeField] private BlockType inputBlockType = BlockType.Stone;
    [SerializeField] private BlockType outputBlockType = BlockType.Gravel;

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

    private float nextScanTime;
    private float crushTimer;
    private bool isCrushing;
    private bool rollersPoweredThisFrame;

    public BlockType InputBlockType => inputBlockType;
    public BlockType OutputBlockType => outputBlockType;
    public int InputBufferAmount => bufferedInputAmount;
    public int OutputBufferAmount => bufferedOutputAmount;
    public int MaxInputBufferAmount => Mathf.Max(1, maxInputBufferAmount);
    public int MaxOutputBufferAmount => Mathf.Max(1, maxOutputBufferAmount);
    public bool DropOutputToWorld => !storeOutputInInternalBuffer;
    public float CrushProgress01 => isCrushing ? Mathf.Clamp01(crushTimer / Mathf.Max(0.05f, crushDurationSeconds)) : 0f;

    private void Awake()
    {
        RegisterCrusher();
        ResolveReferences();
        ResetScanTimer();
    }

    private void OnEnable()
    {
        RegisterCrusher();
        ResetScanTimer();
    }

    private void OnDisable()
    {
        ActiveCrushers.Remove(this);
    }

    protected override void OnDynamicBlockSpawned()
    {
        RegisterCrusher();
        ResolveReferences();
        ResetScanTimer();
    }

    private void Update()
    {
        rollersPoweredThisFrame = false;
        UpdateCrushing(Time.deltaTime);
        UpdateRollers();
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
        return inputBlockType != BlockType.Air &&
               outputBlockType != BlockType.Air &&
               outputBlockType != BlockType.Bedrock &&
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
        return drop != null &&
               drop.CanBeGrabbedByRoboticArm &&
               drop.DroppedBlockType == inputBlockType &&
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
               BlockItemCatalog.TryGetBlockForItem(item, out BlockType blockType) &&
               blockType == inputBlockType;
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
        if (storeOutputInInternalBuffer)
        {
            if (bufferedOutputAmount >= MaxOutputBufferAmount)
                return false;

            bufferedOutputAmount = Mathf.Clamp(bufferedOutputAmount + 1, 0, MaxOutputBufferAmount);
            return true;
        }

        return TryOutputCrushedGravelDrop();
    }

    private bool TryOutputCrushedGravelDrop()
    {
        if (!BlockDrop.Spawn(World.Instance, ResolveExitPosition(), outputBlockType, 1, ResolveOutputThrowDirection()))
        {
            Debug.LogWarning($"[StoneCrusher] Falha ao gerar {outputBlockType} na saida.");
            return false;
        }

        return true;
    }

    private bool TryOutputCrushedGravelDrop(int amount)
    {
        if (amount <= 0)
            return true;

        if (!BlockDrop.Spawn(World.Instance, ResolveExitPosition(), outputBlockType, amount, ResolveOutputThrowDirection()))
        {
            Debug.LogWarning($"[StoneCrusher] Falha ao gerar {outputBlockType} na saida.");
            return false;
        }

        return true;
    }

    public void SetDropOutputToWorld(bool dropToWorld)
    {
        bool newStoreOutput = !dropToWorld;
        if (storeOutputInInternalBuffer == newStoreOutput)
            return;

        storeOutputInInternalBuffer = newStoreOutput;
        if (dropToWorld && bufferedOutputAmount > 0 && TryOutputCrushedGravelDrop(bufferedOutputAmount))
            bufferedOutputAmount = 0;
    }

    public int TryInsertInputBuffer(int amount)
    {
        if (amount <= 0)
            return 0;

        int accepted = Mathf.Min(amount, GetInputBufferFreeSpace());
        if (accepted <= 0)
            return 0;

        bufferedInputAmount += accepted;
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

        return removed;
    }

    public int RemoveOutputBuffer(int amount)
    {
        if (amount <= 0 || bufferedOutputAmount <= 0)
            return 0;

        int removed = Mathf.Min(amount, bufferedOutputAmount);
        bufferedOutputAmount -= removed;
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

    private void ResolveReferences()
    {
        ResolveIOPoints();
        ResolveRollers();
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

    private void UpdateRollers()
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

        float angle = rollerDegreesPerSecond * Time.deltaTime;
        if (rollerA != null)
            rollerA.Rotate(axis, angle, Space.Self);

        if (rollerB != null)
            rollerB.Rotate(axis, rotateRollersInOppositeDirections ? -angle : angle, Space.Self);
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
