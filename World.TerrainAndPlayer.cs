using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public partial class World : MonoBehaviour
{
    public event System.Action<Vector3Int, BlockType, BlockType> BlockChanged;

    [Header("Leaf Decay")]
    [SerializeField] private bool enableLeafDecay = true;
    [SerializeField, Min(1)] private int leafDecaySupportDistance = 4;
    [SerializeField, Min(1)] private int leafDecayChecksPerFrame = 8;
    [SerializeField, Min(0f)] private float leafDecayTimeBudgetMS = 0.5f;
    [SerializeField, Min(0.05f)] private float leafDecayStepInterval = 0.15f;
    [SerializeField, Min(0f)] private float leafDecayGraceSeconds = 1.2f;

    private readonly Queue<TreeCapitatorBreakCandidate> queuedTreeCapitatorBreaks = new Queue<TreeCapitatorBreakCandidate>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> queuedTreeCapitatorSet = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Queue<Vector3Int> treeCapitatorSearchQueue = new Queue<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> treeCapitatorVisited = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly List<Vector3Int> treeCapitatorCollectedLogs = new List<Vector3Int>(64);
    private readonly HashSet<Vector3Int> playerPlacedLogs = new HashSet<Vector3Int>(InitialBlockEditCapacity);

    private readonly Queue<LeafDecayCandidate> queuedLeafDecay = new Queue<LeafDecayCandidate>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> queuedLeafDecaySet = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Dictionary<Vector3Int, float> leafDecayUnsupportedSince = new Dictionary<Vector3Int, float>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> persistentLeafBlocks = new HashSet<Vector3Int>(InitialBlockEditCapacity);
    private readonly Queue<LeafSupportSearchNode> leafSupportSearchQueue = new Queue<LeafSupportSearchNode>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> leafSupportVisited = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Vector2Int[] blockChangeChunksToRebuildBuffer = new Vector2Int[9];

    private static readonly Vector3Int[] TreeCapitatorNeighborOffsets = CreateTreeCapitatorNeighborOffsets();

    private static readonly Vector3Int[] LeafDecayNeighborOffsets =
    {
        Vector3Int.up,
        Vector3Int.down,
        Vector3Int.left,
        Vector3Int.right,
        new Vector3Int(0, 0, 1),
        new Vector3Int(0, 0, -1)
    };

    private struct LeafDecayCandidate
    {
        public Vector3Int position;
        public float nextCheckTime;
    }

    private struct TreeCapitatorBreakCandidate
    {
        public Vector3Int position;
        public BlockType expectedType;
        public bool shouldDrop;
        public Vector3 throwDirection;
    }

    private struct PendingInteractiveBlockLightRefresh
    {
        public Vector3Int position;
        public BlockType previousType;
        public BlockType newType;
        public float earliestRefreshTime;
    }

    private struct LeafSupportSearchNode
    {
        public Vector3Int position;
        public int distance;
    }

    #region Player Actions

    public bool IsGrassBillboardSuppressed(Vector3Int billboardPos)
    {
        return suppressedGrassBillboards.Contains(billboardPos);
    }

    public void SuppressGrassBillboardAt(Vector3Int billboardPos)
    {
        SetGrassBillboardSuppressed(billboardPos, permanent: false, requestRebuild: true);
    }

    private void PermanentlySuppressGrassBillboardAt(Vector3Int billboardPos, bool requestRebuild = true)
    {
        SetGrassBillboardSuppressed(billboardPos, permanent: true, requestRebuild: requestRebuild);
    }

    private void SetGrassBillboardSuppressed(Vector3Int billboardPos, bool permanent, bool requestRebuild)
    {
        if (billboardPos.y <= 0) return;
        if (permanent)
            permanentGrassBillboardSuppressions.Add(billboardPos);

        if (!suppressedGrassBillboards.Add(billboardPos)) return;
        IndexSuppressedGrassBillboard(billboardPos);

        if (requestRebuild)
        {
            Vector2Int coord = GetChunkCoordFromWorldXZ(billboardPos.x, billboardPos.z);
            RequestChunkRebuild(coord, GetDirtySubchunkMaskForWorldY(billboardPos.y), false);
        }
    }


    public void SetBlockAt(
        Vector3Int worldPos,
        BlockType type,
        bool placedByPlayer = false,
        BlockPlacementAxis placementAxis = BlockPlacementAxis.Y)
    {
        if (!enableWater && FluidBlockUtility.IsWater(type))
            type = BlockType.Air;

        if (worldPos.y <= 2)
        {
            Debug.Log("Tentativa de modificar Bedrock/abaixo ignorada: " + worldPos);
            return;
        }

        BlockType current = GetBlockAt(worldPos);
        bool waterWillPermanentlyDestroyBillboard =
            current == BlockType.Air &&
            FluidBlockUtility.IsWater(type) &&
            TryResolveVegetationBillboardAt(worldPos, out _, out _);

        if (current == BlockType.Bedrock)
        {
            Debug.Log("Tentativa de modificar Bedrock ignorada: " + worldPos);
            return;
        }

        if (type == BlockType.wire && current == BlockType.wire)
        {
            byte existingRawValue = blockPlacementAxes.TryGetValue(worldPos, out BlockPlacementAxis existingAxis)
                ? (byte)existingAxis
                : (byte)BlockPlacementAxis.Y;
            existingRawValue = ResolveStoredWirePlacementRaw(worldPos, existingRawValue);

            if (!WirePlacementUtility.TryMerge(existingRawValue, (byte)placementAxis, out byte mergedRawValue))
                return;

            if (mergedRawValue == existingRawValue)
                return;

            placementAxis = (BlockPlacementAxis)mergedRawValue;
        }
        else if (current == type)
        {
            return;
        }

        // Occupied positions clear temporary billboard suppression, but permanent
        // water destruction stays until the supporting ground changes.
        if (type != BlockType.Air)
            RemoveSuppressedGrassBillboard(worldPos);

        // Ground changed: clear suppression above so biome vegetation can be re-evaluated.
        RemoveSuppressedGrassBillboard(new Vector3Int(worldPos.x, worldPos.y + 1, worldPos.z), allowPermanentRemoval: true);

        // Keep explicit Air overrides so broken procedural terrain stays removed.
        // Removing the key would make GetBlockAt() fall back to procedural data again.
        blockOverrides[worldPos] = type;
        UpdateStoredPlacementAxis(worldPos, type, placementAxis);

        Vector2Int chunkCoord = new Vector2Int(
            Mathf.FloorToInt((float)worldPos.x / Chunk.SizeX),
            Mathf.FloorToInt((float)worldPos.z / Chunk.SizeZ)
        );

        EnsureTerrainOverrideIndexBuilt();
        IndexTerrainOverride(worldPos, chunkCoord);

        ApplyBlockToLoadedChunkCache(worldPos, chunkCoord, type);
        HandlePlayerPlacedLogBlockChange(worldPos, current, type, placedByPlayer);
        HandleLeafDecayBlockChange(worldPos, current, type, placedByPlayer);
        HandleWaterBlockChange(worldPos, current, type, placedByPlayer);
        if (waterWillPermanentlyDestroyBillboard)
            PermanentlySuppressGrassBillboardAt(worldPos, requestRebuild: false);
        torchFireParticleController?.NotifyBlockChanged(worldPos, current, type);
        TryConvertCoveredGrassToDirt(worldPos, type, placedByPlayer);

        RequestBlockEditRefresh(worldPos, chunkCoord, current, type);
        BlockChanged?.Invoke(worldPos, current, type);
    }

    private void TryConvertCoveredGrassToDirt(Vector3Int worldPos, BlockType placedType, bool placedByPlayer)
    {
        if (!placedByPlayer || !DoesBlockShadeGrass(placedType) || worldPos.y <= 0)
            return;

        Vector3Int belowPos = worldPos + Vector3Int.down;
        if (GetBlockAt(belowPos) != BlockType.Grass)
            return;

        SetBlockAt(belowPos, BlockType.Dirt);
    }

    private bool DoesBlockShadeGrass(BlockType blockType)
    {
        if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
            return false;

        if (blockData == null)
            return IsSolidBlock(blockType);

        BlockTextureMapping? mapping = blockData.GetMapping(blockType);
        if (mapping == null)
            return IsSolidBlock(blockType);

        BlockTextureMapping value = mapping.Value;
        return value.isSolid && !value.isTransparent && !value.isLiquid;
    }

    private void RequestBlockEditRefresh(
        Vector3Int worldPos,
        Vector2Int chunkCoord,
        BlockType previousType,
        BlockType newType)
    {
        int chunksToRebuildCount = CollectBlockEditRebuildChunks(worldPos, chunkCoord);
        int localX = worldPos.x - (chunkCoord.x * Chunk.SizeX);
        int localZ = worldPos.z - (chunkCoord.y * Chunk.SizeZ);
        bool isWaterChange = FluidBlockUtility.IsWater(previousType) || FluidBlockUtility.IsWater(newType);
        float refreshDelay = isWaterChange ? 0f : GetInteractiveBlockEditRefreshDelaySeconds();
        bool smoothRefresh = !isWaterChange && ShouldSmoothInteractiveBlockEditRefresh(refreshDelay);

        // Fora da altura simulada por chunk, mantemos apenas override:
        // evita custo de light propagation/rebuild de terrain data que nao cobre esse Y.
        if (worldPos.y >= Chunk.SizeY)
        {
            IndexHighOverride(worldPos, chunkCoord, newType);

            RequestHighBuildMeshRebuild(chunkCoord);
            if (localX == 0) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.left);
            if (localX == Chunk.SizeX - 1) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.right);
            if (localZ == 0) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.down);
            if (localZ == Chunk.SizeZ - 1) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.up);

            if (worldPos.y == Chunk.SizeY || worldPos.y == Chunk.SizeY + 1)
            {
                int topTerrainSubchunkMask = GetDirtySubchunkMaskForWorldY(Chunk.SizeY - 1);
                for (int i = 0; i < chunksToRebuildCount; i++)
                    RequestBlockEditChunkRebuild(blockChangeChunksToRebuildBuffer[i], topTerrainSubchunkMask, smoothRefresh, refreshDelay);
            }
            return;
        }

        int terrainDirtySubchunkMask = GetDirtySubchunkMaskForBlockChange(worldPos, previousType, newType);
        for (int i = 0; i < chunksToRebuildCount; i++)
            RequestBlockEditChunkRebuild(blockChangeChunksToRebuildBuffer[i], terrainDirtySubchunkMask, smoothRefresh, refreshDelay);

        if (smoothRefresh)
            QueueInteractiveBlockLightRefresh(worldPos, previousType, newType, refreshDelay);
        else
            ApplyBlockEditLightRefresh(worldPos, previousType, newType);

        // Mudanca no topo do chunk pode expor/ocultar a face inferior de construcoes altas.
        if (worldPos.y >= Chunk.SizeY - 1)
        {
            RequestHighBuildMeshRebuild(chunkCoord);
            if (localX == 0) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.left);
            if (localX == Chunk.SizeX - 1) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.right);
            if (localZ == 0) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.down);
            if (localZ == Chunk.SizeZ - 1) RequestHighBuildMeshRebuild(chunkCoord + Vector2Int.up);
        }
    }

    private int CollectBlockEditRebuildChunks(Vector3Int worldPos, Vector2Int chunkCoord)
    {
        int chunksToRebuildCount = 0;
        AddUniqueChunkCoordToBuffer(chunkCoord, blockChangeChunksToRebuildBuffer, ref chunksToRebuildCount);

        int localX = worldPos.x - (chunkCoord.x * Chunk.SizeX);
        int localZ = worldPos.z - (chunkCoord.y * Chunk.SizeZ);

        if (localX == 0) AddUniqueChunkCoordToBuffer(chunkCoord + Vector2Int.left, blockChangeChunksToRebuildBuffer, ref chunksToRebuildCount);
        if (localX == Chunk.SizeX - 1) AddUniqueChunkCoordToBuffer(chunkCoord + Vector2Int.right, blockChangeChunksToRebuildBuffer, ref chunksToRebuildCount);
        if (localZ == 0) AddUniqueChunkCoordToBuffer(chunkCoord + Vector2Int.down, blockChangeChunksToRebuildBuffer, ref chunksToRebuildCount);
        if (localZ == Chunk.SizeZ - 1) AddUniqueChunkCoordToBuffer(chunkCoord + Vector2Int.up, blockChangeChunksToRebuildBuffer, ref chunksToRebuildCount);
        if (localX == 0 && localZ == 0) AddUniqueChunkCoordToBuffer(chunkCoord + Vector2Int.left + Vector2Int.down, blockChangeChunksToRebuildBuffer, ref chunksToRebuildCount);
        if (localX == 0 && localZ == Chunk.SizeZ - 1) AddUniqueChunkCoordToBuffer(chunkCoord + Vector2Int.left + Vector2Int.up, blockChangeChunksToRebuildBuffer, ref chunksToRebuildCount);
        if (localX == Chunk.SizeX - 1 && localZ == 0) AddUniqueChunkCoordToBuffer(chunkCoord + Vector2Int.right + Vector2Int.down, blockChangeChunksToRebuildBuffer, ref chunksToRebuildCount);
        if (localX == Chunk.SizeX - 1 && localZ == Chunk.SizeZ - 1) AddUniqueChunkCoordToBuffer(chunkCoord + Vector2Int.right + Vector2Int.up, blockChangeChunksToRebuildBuffer, ref chunksToRebuildCount);

        return chunksToRebuildCount;
    }

    private void RequestBlockEditChunkRebuild(Vector2Int coord, int dirtySubchunkMask, bool smoothRefresh, float refreshDelay)
    {
        if (smoothRefresh)
            RequestChunkRebuildDelayed(coord, dirtySubchunkMask, true, refreshDelay);
        else
            RequestChunkRebuild(coord, dirtySubchunkMask);
    }

    private float GetInteractiveBlockEditRefreshDelaySeconds()
    {
        return Mathf.Max(0f, interactiveBlockEditRefreshDelaySeconds);
    }

    private bool ShouldSmoothInteractiveBlockEditRefresh(float refreshDelay)
    {
        return smoothInteractiveBlockEdits && refreshDelay > 0f;
    }

    private void QueueInteractiveBlockLightRefresh(
        Vector3Int worldPos,
        BlockType previousType,
        BlockType newType,
        float refreshDelay)
    {
        if (!DoesBlockEditNeedBlockLightRefresh(worldPos, previousType, newType))
            return;

        float requestedTime = Time.time + Mathf.Max(0f, refreshDelay);
        if (queuedInteractiveBlockLightRefreshesByPosition.TryGetValue(worldPos, out PendingInteractiveBlockLightRefresh existing))
        {
            existing.newType = newType;
            existing.earliestRefreshTime = Mathf.Min(existing.earliestRefreshTime, requestedTime);

            if (existing.previousType == existing.newType)
            {
                queuedInteractiveBlockLightRefreshesByPosition.Remove(worldPos);
                return;
            }

            queuedInteractiveBlockLightRefreshesByPosition[worldPos] = existing;
            return;
        }

        if (previousType == newType)
            return;

        queuedInteractiveBlockLightRefreshesByPosition[worldPos] = new PendingInteractiveBlockLightRefresh
        {
            position = worldPos,
            previousType = previousType,
            newType = newType,
            earliestRefreshTime = requestedTime
        };
        queuedInteractiveBlockLightRefreshes.Enqueue(worldPos);
    }

    private void ProcessQueuedInteractiveBlockLightRefreshes()
    {
        if (queuedInteractiveBlockLightRefreshes.Count == 0)
            return;

        float stepStartTime = Time.realtimeSinceStartup;
        float timeBudgetSeconds = interactiveBlockLightRefreshBudgetMS > 0f ? interactiveBlockLightRefreshBudgetMS / 1000f : 0f;
        int perFrameLimit = Mathf.Max(1, maxInteractiveBlockLightRefreshesPerFrame);
        int processed = 0;
        int attempts = queuedInteractiveBlockLightRefreshes.Count;
        float now = Time.time;

        while (processed < perFrameLimit && attempts-- > 0 && queuedInteractiveBlockLightRefreshes.Count > 0)
        {
            if (processed > 0 &&
                timeBudgetSeconds > 0f &&
                Time.realtimeSinceStartup - stepStartTime >= timeBudgetSeconds)
            {
                break;
            }

            Vector3Int worldPos = queuedInteractiveBlockLightRefreshes.Dequeue();
            if (!queuedInteractiveBlockLightRefreshesByPosition.TryGetValue(worldPos, out PendingInteractiveBlockLightRefresh request))
                continue;

            if (request.earliestRefreshTime > now)
            {
                queuedInteractiveBlockLightRefreshes.Enqueue(worldPos);
                continue;
            }

            queuedInteractiveBlockLightRefreshesByPosition.Remove(worldPos);
            ApplyBlockEditLightRefresh(request.position, request.previousType, request.newType);
            processed++;
        }
    }

    private bool DoesBlockEditNeedBlockLightRefresh(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        if (!enableVoxelLighting ||
            worldPos.y < 0 ||
            worldPos.y >= Chunk.SizeY ||
            previousType == newType)
        {
            return false;
        }

        ushort newEmission = GetBlockEmissionPacked(newType);
        ushort oldEmission = GetBlockEmissionPacked(previousType);
        if (LightUtils.HasBlockLight(newEmission) || LightUtils.HasBlockLight(oldEmission))
            return true;

        byte newOpacity = GetBlockOpacity(newType);
        byte oldOpacity = GetBlockOpacity(previousType);
        bool becameOpaque = oldOpacity < 15 && newOpacity >= 15;
        bool becameTransparent = oldOpacity >= 15 && newOpacity < 15;
        if (!becameOpaque && !becameTransparent)
            return false;

        return LightUtils.HasBlockLight(GetColumnLight(worldPos.x, worldPos.z, worldPos.y)) ||
               HasAdjacentLoadedBlockLight(worldPos);
    }

    private bool HasAdjacentLoadedBlockLight(Vector3Int worldPos)
    {
        for (int i = 0; i < sixDirections.Length; i++)
        {
            Vector3Int neighborPos = worldPos + sixDirections[i];
            if (neighborPos.y < 0 || neighborPos.y >= Chunk.SizeY)
                continue;

            Vector2Int neighborChunk = new Vector2Int(
                Mathf.FloorToInt((float)neighborPos.x / Chunk.SizeX),
                Mathf.FloorToInt((float)neighborPos.z / Chunk.SizeZ));
            if (!IsChunkLoaded(neighborChunk))
                continue;

            if (LightUtils.HasBlockLight(GetColumnLight(neighborPos.x, neighborPos.z, neighborPos.y)))
                return true;
        }

        return false;
    }

    private void ApplyBlockEditLightRefresh(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        if (!enableVoxelLighting || worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
            return;

        ushort newEmission = GetBlockEmissionPacked(newType);
        ushort oldEmission = GetBlockEmissionPacked(previousType);
        byte newOpacity = GetBlockOpacity(newType);
        byte oldOpacity = GetBlockOpacity(previousType);

        bool hadEmission = LightUtils.HasBlockLight(oldEmission);
        bool hasEmission = LightUtils.HasBlockLight(newEmission);

        if (hadEmission && hasEmission)
        {
            RemoveLightGlobal(worldPos, oldEmission);
            PropagateLightGlobal(worldPos, newEmission);
        }
        else if (hasEmission)
        {
            PropagateLightGlobal(worldPos, newEmission);
        }
        else if (hadEmission)
        {
            RemoveLightGlobal(worldPos, oldEmission);
        }
        else
        {
            bool becameOpaque = oldOpacity < 15 && newOpacity >= 15;
            bool becameTransparent = oldOpacity >= 15 && newOpacity < 15;

            if (becameOpaque)
                RemoveLightGlobal(worldPos);
            else if (becameTransparent)
                RefillLightGlobal(worldPos);
        }
    }

    public bool CanPlaceWireStateAt(Vector3Int worldPos, BlockPlacementAxis placementAxis)
    {
        BlockType current = GetBlockAt(worldPos);
        if (current != BlockType.Air &&
            !FluidBlockUtility.IsWater(current) &&
            current != BlockType.wire)
        {
            return false;
        }

        if (current != BlockType.wire)
            return true;

        byte existingRawValue = blockPlacementAxes.TryGetValue(worldPos, out BlockPlacementAxis existingAxis)
            ? (byte)existingAxis
            : (byte)BlockPlacementAxis.Y;
        existingRawValue = ResolveStoredWirePlacementRaw(worldPos, existingRawValue);

        return WirePlacementUtility.TryMerge(existingRawValue, (byte)placementAxis, out byte mergedRawValue) &&
               mergedRawValue != existingRawValue;
    }

    public bool TryPlaceWireStateAt(Vector3Int worldPos, BlockPlacementAxis placementAxis, bool placedByPlayer = false)
    {
        if (!CanPlaceWireStateAt(worldPos, placementAxis))
            return false;

        SetBlockAt(worldPos, BlockType.wire, placedByPlayer, placementAxis);
        return true;
    }

    private static void AddUniqueChunkCoordToBuffer(Vector2Int coord, Vector2Int[] buffer, ref int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (buffer[i] == coord)
                return;
        }

        if (count < buffer.Length)
            buffer[count++] = coord;
    }

    public bool TryQueueTreeCapitatorBreak(
        Vector3Int origin,
        BlockType brokenBlockType,
        bool shouldDrop,
        Vector3 throwDirection)
    {
        if (!enableTreeCapitator || !IsTreeLogBlock(brokenBlockType))
            return false;
        if (playerPlacedLogs.Contains(origin))
            return false;

        int connectedLogs = CollectTreeCapitatorLogs(origin, brokenBlockType);
        if (connectedLogs <= 1)
            return false;

        Vector3 normalizedThrowDirection = throwDirection.sqrMagnitude > 0.0001f
            ? throwDirection.normalized
            : Vector3.up;

        int queued = 0;
        for (int i = 0; i < treeCapitatorCollectedLogs.Count; i++)
        {
            Vector3Int logPos = treeCapitatorCollectedLogs[i];
            if (playerPlacedLogs.Contains(logPos))
                continue;

            if (!queuedTreeCapitatorSet.Add(logPos))
                continue;

            queuedTreeCapitatorBreaks.Enqueue(new TreeCapitatorBreakCandidate
            {
                position = logPos,
                expectedType = brokenBlockType,
                shouldDrop = shouldDrop,
                throwDirection = normalizedThrowDirection
            });
            queued++;
        }

        return queued > 0;
    }

    private int CollectTreeCapitatorLogs(Vector3Int origin, BlockType targetLogType)
    {
        treeCapitatorSearchQueue.Clear();
        treeCapitatorVisited.Clear();
        treeCapitatorCollectedLogs.Clear();

        int maxLogs = Mathf.Max(1, treeCapitatorMaxLogsPerTree);
        treeCapitatorSearchQueue.Enqueue(origin);
        treeCapitatorVisited.Add(origin);

        while (treeCapitatorSearchQueue.Count > 0 && treeCapitatorCollectedLogs.Count < maxLogs)
        {
            Vector3Int current = treeCapitatorSearchQueue.Dequeue();
            if (GetBlockAt(current) != targetLogType)
                continue;
            if (playerPlacedLogs.Contains(current))
                continue;

            treeCapitatorCollectedLogs.Add(current);

            for (int i = 0; i < TreeCapitatorNeighborOffsets.Length; i++)
            {
                Vector3Int neighborPos = current + TreeCapitatorNeighborOffsets[i];
                if (neighborPos.y <= 2 || neighborPos.y >= Chunk.SizeY)
                    continue;

                if (!treeCapitatorVisited.Add(neighborPos))
                    continue;

                if (GetBlockAt(neighborPos) != targetLogType)
                    continue;
                if (playerPlacedLogs.Contains(neighborPos))
                    continue;

                treeCapitatorSearchQueue.Enqueue(neighborPos);
            }
        }

        return treeCapitatorCollectedLogs.Count;
    }

    private void ProcessQueuedTreeCapitatorBreaks()
    {
        if (!enableTreeCapitator)
        {
            ClearQueuedTreeCapitatorBreaks();
            return;
        }

        if (queuedTreeCapitatorBreaks.Count == 0)
            return;

        int perFrameLimit = Mathf.Max(1, treeCapitatorBreaksPerFrame);
        float stepStartTime = Time.realtimeSinceStartup;
        float timeBudgetSeconds = treeCapitatorTimeBudgetMS > 0f ? treeCapitatorTimeBudgetMS / 1000f : 0f;
        for (int i = 0; i < perFrameLimit && queuedTreeCapitatorBreaks.Count > 0; i++)
        {
            if (timeBudgetSeconds > 0f && Time.realtimeSinceStartup - stepStartTime >= timeBudgetSeconds)
                break;

            TreeCapitatorBreakCandidate candidate = queuedTreeCapitatorBreaks.Dequeue();
            queuedTreeCapitatorSet.Remove(candidate.position);

            BlockType currentType = GetBlockAt(candidate.position);
            if (currentType != candidate.expectedType)
                continue;

            bool spawnedDrop = candidate.shouldDrop &&
                               BlockDrop.Spawn(this, candidate.position, currentType, candidate.throwDirection);
            SetBlockAt(candidate.position, BlockType.Air);
            InvalidateLoadedSubchunkCollidersAt(candidate.position);

            if (candidate.shouldDrop && !spawnedDrop)
            {
                bool addedToInventory = PlayerInventory.Instance != null &&
                                        PlayerInventory.Instance.TryAddBlockDrop(currentType, 1);
                if (!addedToInventory)
                    Debug.LogWarning($"[World] Falha ao gerar drop de {currentType} em {candidate.position}.");
            }
        }
    }

    private void ClearQueuedTreeCapitatorBreaks()
    {
        queuedTreeCapitatorBreaks.Clear();
        queuedTreeCapitatorSet.Clear();
    }

    private void InvalidateLoadedSubchunkCollidersAt(Vector3Int worldPos)
    {
        if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
            return;

        Vector2Int coord = GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z);
        if (!activeChunks.TryGetValue(coord, out Chunk chunk) || chunk == null)
            return;

        int subchunkIndex = Mathf.Clamp(worldPos.y / Chunk.SubchunkHeight, 0, Chunk.SubchunksPerColumn - 1);
        chunk.MarkSubchunkColliderDataDirty(subchunkIndex);
    }

    private void ApplyBlockToLoadedChunkCache(Vector3Int worldPos, Vector2Int chunkCoord, BlockType type)
    {
        if (!activeChunks.TryGetValue(chunkCoord, out Chunk chunk)) return;
        if (!CanChunkProvideVoxelSnapshot(chunk)) return;

        int lx = worldPos.x - chunkCoord.x * Chunk.SizeX;
        int lz = worldPos.z - chunkCoord.y * Chunk.SizeZ;
        int ly = worldPos.y;

        if (lx < 0 || lx >= Chunk.SizeX || lz < 0 || lz >= Chunk.SizeZ || ly < 0 || ly >= Chunk.SizeY)
            return;

        int idx = lx + lz * Chunk.SizeX + ly * Chunk.SizeX * Chunk.SizeZ;
        chunk.voxelData[idx] = (byte)type;
        chunk.hasVoxelSnapshot = true;
        chunk.SyncDynamicBlockVisualAt(blockData, worldPos);
    }

    private void ProcessQueuedLeafDecay()
    {
        if (!enableLeafDecay || queuedLeafDecay.Count == 0)
            return;

        float now = Time.time;
        float stepStartTime = Time.realtimeSinceStartup;
        float timeBudgetSeconds = leafDecayTimeBudgetMS > 0f ? leafDecayTimeBudgetMS / 1000f : 0f;
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();
        int processed = 0;
        int attempts = queuedLeafDecay.Count;

        while (processed < Mathf.Max(1, leafDecayChecksPerFrame) && attempts-- > 0)
        {
            if (timeBudgetSeconds > 0f && Time.realtimeSinceStartup - stepStartTime >= timeBudgetSeconds)
                break;

            LeafDecayCandidate candidate = queuedLeafDecay.Dequeue();
            queuedLeafDecaySet.Remove(candidate.position);

            if (candidate.nextCheckTime > now)
            {
                queuedLeafDecay.Enqueue(candidate);
                queuedLeafDecaySet.Add(candidate.position);
                continue;
            }

            if (!IsWorldPositionInsideSimulationDistance(candidate.position, simulationCenter))
            {
                TryQueueLeafDecay(candidate.position, Mathf.Max(leafDecayStepInterval, 0.5f));
                continue;
            }

            EvaluateLeafDecay(candidate.position, now);
            processed++;
        }
    }

    private void EvaluateLeafDecay(Vector3Int worldPos, float now)
    {
        BlockType blockType = GetBlockAt(worldPos);
        if (blockType != BlockType.Leaves)
        {
            leafDecayUnsupportedSince.Remove(worldPos);
            persistentLeafBlocks.Remove(worldPos);
            return;
        }

        if (persistentLeafBlocks.Contains(worldPos))
        {
            leafDecayUnsupportedSince.Remove(worldPos);
            return;
        }

        if (HasLeafSupport(worldPos))
        {
            leafDecayUnsupportedSince.Remove(worldPos);
            return;
        }

        if (!leafDecayUnsupportedSince.TryGetValue(worldPos, out float unsupportedSince))
        {
            leafDecayUnsupportedSince[worldPos] = now;
            TryQueueLeafDecay(worldPos, leafDecayStepInterval);
            return;
        }

        if (now - unsupportedSince < leafDecayGraceSeconds)
        {
            TryQueueLeafDecay(worldPos, leafDecayStepInterval);
            return;
        }

        SetBlockAt(worldPos, BlockType.Air);
    }

    private void HandleLeafDecayBlockChange(Vector3Int worldPos, BlockType previousType, BlockType newType, bool placedByPlayer)
    {
        if (!enableLeafDecay)
            return;

        bool wasLeaf = previousType == BlockType.Leaves;
        bool isLeaf = newType == BlockType.Leaves;
        bool wasLeafSupport = IsLeafSupportBlock(previousType);
        bool isLeafSupport = IsLeafSupportBlock(newType);

        if (isLeaf && placedByPlayer)
            persistentLeafBlocks.Add(worldPos);
        else if (!isLeaf || !placedByPlayer)
            persistentLeafBlocks.Remove(worldPos);

        if (!isLeaf)
            leafDecayUnsupportedSince.Remove(worldPos);

        if (wasLeafSupport != isLeafSupport)
            QueueNearbyLeavesForDecay(worldPos, Mathf.Max(1, leafDecaySupportDistance));

        if (wasLeaf != isLeaf)
        {
            QueueAdjacentLeavesForDecay(worldPos);

            if (isLeaf)
                TryQueueLeafDecay(worldPos, leafDecayStepInterval);
        }
    }

    private void QueueAdjacentLeavesForDecay(Vector3Int center)
    {
        for (int i = 0; i < LeafDecayNeighborOffsets.Length; i++)
        {
            Vector3Int neighborPos = center + LeafDecayNeighborOffsets[i];
            if (GetBlockAt(neighborPos) == BlockType.Leaves)
                TryQueueLeafDecay(neighborPos, leafDecayStepInterval);
        }
    }

    private void QueueNearbyLeavesForDecay(Vector3Int center, int radius)
    {
        int clampedRadius = Mathf.Max(1, radius);

        for (int dy = -clampedRadius; dy <= clampedRadius; dy++)
        {
            for (int dz = -clampedRadius; dz <= clampedRadius; dz++)
            {
                for (int dx = -clampedRadius; dx <= clampedRadius; dx++)
                {
                    Vector3Int pos = new Vector3Int(center.x + dx, center.y + dy, center.z + dz);
                    if (GetBlockAt(pos) == BlockType.Leaves)
                        TryQueueLeafDecay(pos, leafDecayStepInterval);
                }
            }
        }
    }

    private void TryQueueLeafDecay(Vector3Int worldPos, float delaySeconds = 0f)
    {
        if (!enableLeafDecay || !queuedLeafDecaySet.Add(worldPos))
            return;

        queuedLeafDecay.Enqueue(new LeafDecayCandidate
        {
            position = worldPos,
            nextCheckTime = Time.time + Mathf.Max(0f, delaySeconds)
        });
    }

    private bool HasLeafSupport(Vector3Int origin)
    {
        int maxDistance = Mathf.Max(1, leafDecaySupportDistance);

        leafSupportSearchQueue.Clear();
        leafSupportVisited.Clear();
        leafSupportVisited.Add(origin);
        leafSupportSearchQueue.Enqueue(new LeafSupportSearchNode
        {
            position = origin,
            distance = 0
        });

        while (leafSupportSearchQueue.Count > 0)
        {
            LeafSupportSearchNode current = leafSupportSearchQueue.Dequeue();
            if (current.distance >= maxDistance)
                continue;

            int nextDistance = current.distance + 1;
            for (int i = 0; i < LeafDecayNeighborOffsets.Length; i++)
            {
                Vector3Int neighborPos = current.position + LeafDecayNeighborOffsets[i];
                if (!leafSupportVisited.Add(neighborPos))
                    continue;

                BlockType neighborType = GetBlockAt(neighborPos);
                if (IsLeafSupportBlock(neighborType))
                    return true;

                if (neighborType == BlockType.Leaves)
                {
                    leafSupportSearchQueue.Enqueue(new LeafSupportSearchNode
                    {
                        position = neighborPos,
                        distance = nextDistance
                    });
                }
            }
        }

        return false;
    }

    private static bool IsLeafSupportBlock(BlockType blockType)
    {
        return IsTreeLogBlock(blockType);
    }

    private static bool IsTreeLogBlock(BlockType blockType)
    {
        return blockType == BlockType.Log ||
               blockType == BlockType.birch_log ||
               blockType == BlockType.acacia_log;
    }

    private void HandlePlayerPlacedLogBlockChange(
        Vector3Int worldPos,
        BlockType previousType,
        BlockType newType,
        bool placedByPlayer)
    {
        bool isLog = IsTreeLogBlock(newType);
        if (!isLog)
        {
            playerPlacedLogs.Remove(worldPos);
            return;
        }

        if (placedByPlayer)
            playerPlacedLogs.Add(worldPos);
    }

    private static Vector3Int[] CreateTreeCapitatorNeighborOffsets()
    {
        List<Vector3Int> offsets = new List<Vector3Int>(26);
        for (int y = -1; y <= 1; y++)
        {
            for (int z = -1; z <= 1; z++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    if (x == 0 && y == 0 && z == 0)
                        continue;

                    offsets.Add(new Vector3Int(x, y, z));
                }
            }
        }

        return offsets.ToArray();
    }

    [Header("Fluid Simulation")]
    [SerializeField] private bool enableWaterSimulation = true;
    [SerializeField, Min(1)] private int waterUpdatesPerFrame = 16;
    [SerializeField, Min(0f)] private float waterUpdateTimeBudgetMS = 1f;
    [SerializeField, Min(0.01f)] private float waterTickInterval = 0.25f;
    [SerializeField, Min(1)] private int waterSlopeSearchDistance = 4;

    private readonly Queue<WaterUpdateCandidate> queuedWaterUpdates = new Queue<WaterUpdateCandidate>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> queuedWaterUpdateSet = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly HashSet<Vector3Int> persistentWaterSources = new HashSet<Vector3Int>(InitialBlockEditCapacity);
    private readonly HashSet<Vector3Int> waterSlopeVisited = new HashSet<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);

    private static readonly Vector3Int[] WaterNeighborOffsets =
    {
        Vector3Int.up,
        Vector3Int.down,
        Vector3Int.left,
        Vector3Int.right,
        Vector3Int.forward,
        Vector3Int.back
    };

    private static readonly Vector3Int[] HorizontalWaterOffsets =
    {
        Vector3Int.right,
        Vector3Int.left,
        Vector3Int.forward,
        Vector3Int.back
    };

    private const int NoWaterSlopeDropCost = 1000;

    private struct WaterUpdateCandidate
    {
        public Vector3Int position;
        public float nextUpdateTime;
    }

    private void ProcessQueuedWaterUpdates()
    {
        if (!enableWater || !enableWaterSimulation || queuedWaterUpdates.Count == 0)
            return;

        int queuedCount = queuedWaterUpdates.Count;
        float now = Time.time;
        float stepStartTime = Time.realtimeSinceStartup;
        float effectiveWaterBudgetMS = waterUpdateTimeBudgetMS + Mathf.Clamp((queuedCount - waterUpdatesPerFrame) * 0.03f, 0f, 3f);
        float timeBudgetSeconds = effectiveWaterBudgetMS > 0f ? effectiveWaterBudgetMS / 1000f : 0f;
        Vector2Int simulationCenter = GetCurrentPlayerChunkCoord();
        int processed = 0;
        int attempts = queuedCount;
        int backlogBoost = Mathf.Clamp(queuedCount / 8, 0, 64);
        int perFrameLimit = Mathf.Clamp(Mathf.Max(8, waterUpdatesPerFrame) + backlogBoost, 8, 96);

        while (processed < perFrameLimit && attempts-- > 0)
        {
            if (timeBudgetSeconds > 0f && Time.realtimeSinceStartup - stepStartTime >= timeBudgetSeconds)
                break;

            WaterUpdateCandidate candidate = queuedWaterUpdates.Dequeue();
            queuedWaterUpdateSet.Remove(candidate.position);

            if (candidate.nextUpdateTime > now)
            {
                RequeueWaterCandidate(candidate);
                continue;
            }

            if (!IsWorldPositionInsideSimulationDistance(candidate.position, simulationCenter))
            {
                TryQueueWaterUpdate(candidate.position, Mathf.Max(waterTickInterval, 0.25f));
                continue;
            }

            if (EvaluateWaterAt(candidate.position))
                processed++;
        }
    }

    private bool EvaluateWaterAt(Vector3Int worldPos)
    {
        if (worldPos.y <= 2 || worldPos.y >= Chunk.SizeY)
            return false;

        BlockType current = GetBlockAt(worldPos);
        bool currentIsWater = FluidBlockUtility.IsWater(current);
        if (!currentIsWater && !CanWaterOccupy(current))
            return false;

        BlockType desiredState = GetDesiredWaterState(worldPos, current, currentIsWater);
        return SetWaterStateIfNeeded(worldPos, current, desiredState);
    }

    private BlockType GetDesiredWaterState(Vector3Int worldPos, BlockType current, bool currentIsWater)
    {
        if (IsFixedWaterSource(worldPos))
            return BlockType.Water;

        if (TryGetWaterStateFromAbove(worldPos, out BlockType fallingState))
            return fallingState;

        if (ShouldBecomeInfiniteWaterSource(worldPos))
            return BlockType.Water;

        if (TryGetHorizontalWaterState(worldPos, out BlockType flowingState))
            return flowingState;

        return currentIsWater ? BlockType.Air : current;
    }

    private bool TryGetWaterStateFromAbove(Vector3Int worldPos, out BlockType state)
    {
        BlockType above = GetBlockAt(worldPos + Vector3Int.up);
        if (!FluidBlockUtility.IsWater(above))
        {
            state = BlockType.Air;
            return false;
        }

        // Em Minecraft, quando a agua cai para um nivel inferior ela volta a
        // se comportar como uma nova coluna de queda, podendo espalhar mais
        // alguns blocos quando encontra apoio novamente.
        state = FluidBlockUtility.GetWaterBlockType(0, true);
        return true;
    }

    private bool TryGetHorizontalWaterState(Vector3Int worldPos, out BlockType state)
    {
        int bestDistance = int.MaxValue;
        bool found = false;
        bool targetShouldFall = CanWaterFlowDownInto(GetBlockAt(worldPos + Vector3Int.down));

        for (int i = 0; i < HorizontalWaterOffsets.Length; i++)
        {
            Vector3Int sourcePos = worldPos + HorizontalWaterOffsets[i];
            if (!TryGetHorizontalWaterContribution(sourcePos, worldPos, targetShouldFall, out int candidateDistance))
                continue;

            if (candidateDistance < bestDistance)
            {
                bestDistance = candidateDistance;
                found = true;
            }
        }

        if (!found)
        {
            state = BlockType.Air;
            return false;
        }

        if (targetShouldFall)
        {
            state = FluidBlockUtility.GetWaterBlockType(0, true);
            return true;
        }

        state = FluidBlockUtility.GetWaterBlockType(bestDistance, false);
        return true;
    }

    private bool TryGetHorizontalWaterContribution(
        Vector3Int sourcePos,
        Vector3Int targetPos,
        bool targetShouldFall,
        out int candidateDistance)
    {
        candidateDistance = int.MaxValue;

        BlockType sourceType = GetBlockAt(sourcePos);
        if (!CanWaterSpreadHorizontallyFrom(sourcePos, sourceType))
            return false;

        BlockType targetType = GetBlockAt(targetPos);
        if (!CanWaterOccupy(targetType) && !FluidBlockUtility.IsWater(targetType))
            return false;

        int directionIndex = GetHorizontalDirectionIndex(targetPos - sourcePos);
        if (directionIndex < 0)
            return false;

        // Suspended sources/waterfalls still cannot branch sideways because
        // CanWaterSpreadHorizontallyFrom() rejects any source that can already
        // flow downward. This keeps side-placed tower water vertical, while
        // still allowing supported surface water to spill over edges.
        if (!IsOptimalWaterSpreadDirection(sourcePos, directionIndex))
            return false;

        candidateDistance = FluidBlockUtility.GetWaterDistance(sourceType) + 1;
        return candidateDistance <= FluidBlockUtility.MaxWaterDistance;
    }

    private bool CanWaterSpreadHorizontallyFrom(Vector3Int sourcePos, BlockType sourceType)
    {
        if (!FluidBlockUtility.IsWater(sourceType))
            return false;

        if (FluidBlockUtility.GetWaterDistance(sourceType) >= FluidBlockUtility.MaxWaterDistance)
            return false;

        BlockType below = GetBlockAt(sourcePos + Vector3Int.down);
        if (CanWaterFlowDownInto(below))
            return false;

        bool supportedBelow = IsSolidBlock(below) || IsSourceWaterState(sourcePos + Vector3Int.down);
        if (!supportedBelow && FluidBlockUtility.IsWater(below))
            return false;

        // Falling columns should only spill at the bottom-most blocked cell, not from
        // every segment in the column.
        if (FluidBlockUtility.IsFallingWater(sourceType) && FluidBlockUtility.IsWater(below))
            return false;

        return true;
    }

    private bool IsOptimalWaterSpreadDirection(Vector3Int sourcePos, int directionIndex)
    {
        GetOptimalWaterFlowCosts(sourcePos, out int rightCost, out int leftCost, out int forwardCost, out int backCost, out int bestCost);
        if (bestCost == int.MaxValue)
            return false;

        int directionCost = directionIndex switch
        {
            0 => rightCost,
            1 => leftCost,
            2 => forwardCost,
            3 => backCost,
            _ => int.MaxValue
        };

        return directionCost == bestCost;
    }

    private void GetOptimalWaterFlowCosts(
        Vector3Int sourcePos,
        out int rightCost,
        out int leftCost,
        out int forwardCost,
        out int backCost,
        out int bestCost)
    {
        rightCost = GetFlowCostForDirection(sourcePos, 0);
        leftCost = GetFlowCostForDirection(sourcePos, 1);
        forwardCost = GetFlowCostForDirection(sourcePos, 2);
        backCost = GetFlowCostForDirection(sourcePos, 3);

        bestCost = Mathf.Min(rightCost, leftCost);
        bestCost = Mathf.Min(bestCost, forwardCost);
        bestCost = Mathf.Min(bestCost, backCost);
    }

    private int GetFlowCostForDirection(Vector3Int sourcePos, int directionIndex)
    {
        Vector3Int targetPos = sourcePos + HorizontalWaterOffsets[directionIndex];
        BlockType targetType = GetBlockAt(targetPos);
        if (!CanWaterOccupy(targetType))
            return int.MaxValue;

        if (CanWaterFlowDownInto(GetBlockAt(targetPos + Vector3Int.down)))
            return 0;

        waterSlopeVisited.Clear();
        waterSlopeVisited.Add(targetPos);
        int bestDistance = GetWaterSlopeDistance(targetPos, 1, GetOppositeHorizontalDirection(directionIndex));
        waterSlopeVisited.Clear();
        return bestDistance;
    }

    private int GetWaterSlopeDistance(Vector3Int origin, int depth, int blockedDirection)
    {
        int maxDepth = Mathf.Max(1, waterSlopeSearchDistance);
        if (depth >= maxDepth)
            return NoWaterSlopeDropCost;

        int bestDistance = NoWaterSlopeDropCost;

        for (int i = 0; i < HorizontalWaterOffsets.Length; i++)
        {
            if (i == blockedDirection)
                continue;

            Vector3Int nextPos = origin + HorizontalWaterOffsets[i];
            if (!waterSlopeVisited.Add(nextPos))
                continue;

            BlockType nextType = GetBlockAt(nextPos);
            if (!CanWaterOccupy(nextType))
            {
                waterSlopeVisited.Remove(nextPos);
                continue;
            }

            if (CanWaterFlowDownInto(GetBlockAt(nextPos + Vector3Int.down)))
            {
                waterSlopeVisited.Remove(nextPos);
                return depth;
            }

            int candidate = GetWaterSlopeDistance(nextPos, depth + 1, GetOppositeHorizontalDirection(i));
            if (candidate < bestDistance)
                bestDistance = candidate;

            waterSlopeVisited.Remove(nextPos);
        }

        return bestDistance;
    }

    private bool ShouldBecomeInfiniteWaterSource(Vector3Int worldPos)
    {
        BlockType below = GetBlockAt(worldPos + Vector3Int.down);
        bool supportedBelow = IsSolidBlock(below) || IsSourceWaterState(worldPos + Vector3Int.down);
        if (!supportedBelow)
            return false;

        int adjacentSources = 0;
        for (int i = 0; i < HorizontalWaterOffsets.Length; i++)
        {
            if (IsSourceWaterState(worldPos + HorizontalWaterOffsets[i]))
            {
                adjacentSources++;
                if (adjacentSources >= 2)
                    return true;
            }
        }

        return false;
    }

    private bool SetWaterStateIfNeeded(Vector3Int worldPos, BlockType current, BlockType desired)
    {
        if (current == desired)
            return false;

        if (desired == BlockType.Air && !FluidBlockUtility.IsWater(current))
            return false;

        SetBlockAt(worldPos, desired);
        return true;
    }

    private bool IsSourceWaterState(Vector3Int worldPos)
    {
        BlockType blockType = GetBlockAt(worldPos);
        return FluidBlockUtility.IsStillWater(blockType) && !FluidBlockUtility.IsFallingWater(blockType);
    }

    private bool IsFixedWaterSource(Vector3Int worldPos)
    {
        if (!enableWater)
            return false;

        if (persistentWaterSources.Contains(worldPos))
            return true;

        if (blockOverrides.ContainsKey(worldPos))
            return false;

        return GetProceduralBlockFast(worldPos) == BlockType.Water;
    }

    private bool CanWaterOccupy(BlockType blockType)
    {
        if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
            return true;

        if (TorchPlacementUtility.IsTorchLike(blockType))
            return true;

        if (blockData != null)
        {
            BlockTextureMapping? mapping = blockData.GetMapping(blockType);
            if (mapping != null)
            {
                BlockTextureMapping value = mapping.Value;
                return !value.isSolid && !value.isLiquid;
            }
        }

        return !IsSolidBlock(blockType) && !IsLiquidBlock(blockType);
    }

    private bool CanWaterFlowDownInto(BlockType blockType)
    {
        if (FluidBlockUtility.IsWater(blockType))
            return false;

        return CanWaterOccupy(blockType);
    }

    private static int GetHorizontalDirectionIndex(Vector3Int delta)
    {
        if (delta == Vector3Int.right) return 0;
        if (delta == Vector3Int.left) return 1;
        if (delta == Vector3Int.forward) return 2;
        if (delta == Vector3Int.back) return 3;
        return -1;
    }

    private static int GetOppositeHorizontalDirection(int directionIndex)
    {
        return directionIndex switch
        {
            0 => 1,
            1 => 0,
            2 => 3,
            3 => 2,
            _ => -1
        };
    }

    private void HandleWaterBlockChange(Vector3Int worldPos, BlockType previousType, BlockType newType, bool placedByPlayer)
    {
        if (!FluidBlockUtility.IsWater(newType))
        {
            persistentWaterSources.Remove(worldPos);
        }
        else if (placedByPlayer)
        {
            persistentWaterSources.Add(worldPos);
        }

        if (!enableWater || !enableWaterSimulation)
            return;

        float delay = Mathf.Max(0f, waterTickInterval);
        bool isWaterNow = FluidBlockUtility.IsWater(newType);
        bool wasWater = FluidBlockUtility.IsWater(previousType);
        bool becameWater = isWaterNow && !wasWater;

        TryQueueWaterUpdate(worldPos, becameWater ? 0f : delay);

        if (isWaterNow)
        {
            // Waterfalls should extend downward immediately instead of waiting for
            // the generic neighbor delay, which removes the odd "hanging surface"
            // feeling when placing water high on a wall.
            TryQueueWaterUpdate(worldPos + Vector3Int.down, 0f);
            TryQueueWaterUpdate(worldPos + Vector3Int.up, delay);
            for (int i = 0; i < HorizontalWaterOffsets.Length; i++)
                TryQueueWaterUpdate(worldPos + HorizontalWaterOffsets[i], delay);
            return;
        }

        for (int i = 0; i < WaterNeighborOffsets.Length; i++)
            TryQueueWaterUpdate(worldPos + WaterNeighborOffsets[i], delay);
    }

    private void TryQueueWaterUpdate(Vector3Int worldPos, float delaySeconds = 0f)
    {
        if (!enableWater || !enableWaterSimulation)
            return;

        if (worldPos.y <= 2 || worldPos.y >= Chunk.SizeY)
            return;

        if (!queuedWaterUpdateSet.Add(worldPos))
            return;

        queuedWaterUpdates.Enqueue(new WaterUpdateCandidate
        {
            position = worldPos,
            nextUpdateTime = Time.time + Mathf.Max(0f, delaySeconds)
        });
    }

    private void RequeueWaterCandidate(WaterUpdateCandidate candidate)
    {
        if (!queuedWaterUpdateSet.Add(candidate.position))
            return;

        queuedWaterUpdates.Enqueue(candidate);
    }

    #endregion

}










