using UnityEngine;

public partial class World
{
    private readonly Vector2Int[] blockChangeChunksToRebuildBuffer = new Vector2Int[9];
    private bool refreshingConveyorSlopeConnections;
    private bool refreshingTransportTubeConnections;
    private static readonly Vector3Int[] conveyorSlopeRefreshHorizontalOffsets =
    {
        Vector3Int.zero,
        Vector3Int.left,
        Vector3Int.right,
        new Vector3Int(0, 0, -1),
        new Vector3Int(0, 0, 1)
    };
    private static readonly Vector3Int[] transportTubeRefreshHorizontalOffsets =
    {
        Vector3Int.zero,
        Vector3Int.left,
        Vector3Int.right,
        new Vector3Int(0, 0, -1),
        new Vector3Int(0, 0, 1),
        Vector3Int.down,
        Vector3Int.up
    };

    #region Block Interactions

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

        if (type != BlockType.Air && DoesBlockBreakWithoutSupport(type) && !CanBlockStayAt(worldPos, type))
        {
            if (current != type)
                return;

            type = BlockType.Air;
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
            if (!TransportTubeUtility.IsTransportTubeNetworkBlock(type) ||
                BlockPlacementRotationUtility.SanitizeStoredAxis(GetPlacementAxisAt(worldPos, type)) ==
                BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis))
            {
                return;
            }
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
        HandleSaplingBlockChange(worldPos, current, type);
        HandlePlayerPlacedLogBlockChange(worldPos, current, type, placedByPlayer);
        HandleLeafDecayBlockChange(worldPos, current, type, placedByPlayer);
        HandleWaterBlockChange(worldPos, current, type, placedByPlayer);
        HandleSupportDependentBlockChange(worldPos, current, type);
        HandleElectricityBlockChange(worldPos, current, type);
        if (waterWillPermanentlyDestroyBillboard)
            PermanentlySuppressGrassBillboardAt(worldPos, requestRebuild: false);
        torchFireParticleController?.NotifyBlockChanged(worldPos, current, type);
        TryConvertCoveredGrassToDirt(worldPos, type, placedByPlayer);

        RequestBlockEditRefresh(worldPos, chunkCoord, current, type);
        StoneCrusher.NotifyWorldBlockChanged(worldPos, current, type);
        BlockChanged?.Invoke(worldPos, current, type);

        if (!refreshingConveyorSlopeConnections)
            RefreshConveyorSlopeConnectionsAround(worldPos);

        if (!refreshingTransportTubeConnections)
            RefreshTransportTubeConnectionsAround(worldPos);
    }

    private void RefreshTransportTubeConnectionsAround(Vector3Int worldPos)
    {
        refreshingTransportTubeConnections = true;
        try
        {
            for (int i = 0; i < transportTubeRefreshHorizontalOffsets.Length; i++)
                TryRefreshTransportTubeConnection(worldPos + transportTubeRefreshHorizontalOffsets[i]);
        }
        finally
        {
            refreshingTransportTubeConnections = false;
        }
    }

    private void TryRefreshTransportTubeConnection(Vector3Int tubePos)
    {
        BlockType currentType = GetBlockAt(tubePos);
        if (currentType == BlockType.TransportTubeFilter)
        {
            if (!TryResolveTransportTubeFilterBackToChestAxis(tubePos, out BlockPlacementAxis targetFilterAxis))
                return;

            BlockPlacementAxis currentFilterAxis = GetPlacementAxisAt(tubePos, currentType);
            targetFilterAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(targetFilterAxis);
            if (BlockPlacementRotationUtility.SanitizeStoredAxis(currentFilterAxis) == targetFilterAxis)
                return;

            SetBlockAt(tubePos, currentType, false, targetFilterAxis);
            return;
        }

        if (!TransportTubeUtility.IsTransportTubeBlock(currentType))
            return;

        BlockPlacementAxis currentAxis = GetPlacementAxisAt(tubePos, currentType);
        TransportTubeUtility.TubeState targetState = TransportTubeUtility.ResolveState(this, tubePos, currentAxis);
        BlockPlacementAxis targetAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(targetState.placementAxis);

        if (currentType == targetState.blockType &&
            BlockPlacementRotationUtility.SanitizeStoredAxis(currentAxis) == targetAxis)
        {
            return;
        }

        SetBlockAt(tubePos, targetState.blockType, false, targetAxis);
    }

    private void RefreshConveyorSlopeConnectionsAround(Vector3Int worldPos)
    {
        refreshingConveyorSlopeConnections = true;
        try
        {
            for (int yOffset = -1; yOffset <= 1; yOffset++)
            {
                Vector3Int verticalOffset = new Vector3Int(0, yOffset, 0);
                for (int i = 0; i < conveyorSlopeRefreshHorizontalOffsets.Length; i++)
                    TryRefreshConveyorSlopeConnection(worldPos + verticalOffset + conveyorSlopeRefreshHorizontalOffsets[i]);
            }
        }
        finally
        {
            refreshingConveyorSlopeConnections = false;
        }
    }

    private void TryRefreshConveyorSlopeConnection(Vector3Int beltPos)
    {
        BlockType currentType = GetBlockAt(beltPos);
        if (!ConveyorBeltUtility.IsRegularConveyorBlock(currentType))
            return;

        BlockPlacementAxis currentAxis = ConveyorBeltUtility.ResolveConveyorAxis(this, beltPos, currentType);
        bool shouldSlope = ConveyorBeltUtility.ShouldUseSlopedConveyor(this, beltPos, currentAxis);
        BlockType targetType = shouldSlope ? BlockType.conveyorBelt_45deg : BlockType.ConveyorBelt;

        if (currentType == targetType)
            return;

        BlockPlacementAxis targetAxis = shouldSlope
            ? ConveyorBeltUtility.ConvertFlatAxisToSlopedAxis(currentAxis)
            : ConveyorBeltUtility.ConvertSlopedAxisToFlatAxis(currentAxis);

        SetBlockAt(beltPos, targetType, false, targetAxis);
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

    #endregion
}
