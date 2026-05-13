using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

public partial class World
{
    private static int GetFullSubchunkMask()
    {
        return (1 << Chunk.SubchunksPerColumn) - 1;
    }

    private static int GetSubchunkBitForWorldY(int worldY)
    {
        if (worldY < 0 || worldY >= Chunk.SizeY)
            return 0;

        int subchunkIndex = Mathf.Clamp(worldY / Chunk.SubchunkHeight, 0, Chunk.SubchunksPerColumn - 1);
        return 1 << subchunkIndex;
    }

    private static int SanitizeDirtySubchunkMask(int dirtySubchunkMask)
    {
        return dirtySubchunkMask & GetFullSubchunkMask();
    }

    private static int GetDirtySubchunkMaskForWorldY(int worldY)
    {
        if (worldY < 0 || worldY >= Chunk.SizeY)
            return 0;

        int subchunkIndex = Mathf.Clamp(worldY / Chunk.SubchunkHeight, 0, Chunk.SubchunksPerColumn - 1);
        int subchunkStartY = subchunkIndex * Chunk.SubchunkHeight;
        int subchunkEndY = Mathf.Min(subchunkStartY + Chunk.SubchunkHeight, Chunk.SizeY) - 1;

        int mask = 1 << subchunkIndex;
        if (worldY == subchunkStartY && subchunkIndex > 0)
            mask |= 1 << (subchunkIndex - 1);
        if (worldY == subchunkEndY && subchunkIndex + 1 < Chunk.SubchunksPerColumn)
            mask |= 1 << (subchunkIndex + 1);

        return mask;
    }

    private int GetDirtySubchunkMaskForBlockChange(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        int mask = GetDirtySubchunkMaskForWorldY(worldPos.y);

        if (BlockCanAffectAdjacentVerticalSubchunks(previousType) ||
            BlockCanAffectAdjacentVerticalSubchunks(newType))
        {
            mask |= GetSubchunkBitForWorldY(worldPos.y - 1);
            mask |= GetSubchunkBitForWorldY(worldPos.y + 1);
        }

        return SanitizeDirtySubchunkMask(mask);
    }

    private bool BlockCanAffectAdjacentVerticalSubchunks(BlockType blockType)
    {
        if (blockType == BlockType.Air || blockType == BlockType.Bedrock)
            return false;

        if (FluidBlockUtility.IsWater(blockType) ||
            TorchPlacementUtility.IsTorchLike(blockType) ||
            blockType == BlockType.wire ||
            blockType == BlockType.Leaves ||
            IsTreeLogBlock(blockType))
        {
            return true;
        }

        if (blockData == null || blockData.mappings == null)
            return false;

        int blockIndex = (int)blockType;
        if (blockIndex < 0 || blockIndex >= blockData.mappings.Length)
            return false;

        BlockTextureMapping mapping = blockData.mappings[blockIndex];
        return BlockShapeUtility.GetEffectiveRenderShape(mapping) != BlockRenderShape.Cube ||
               mapping.isLiquid ||
               mapping.isTransparent ||
               mapping.isLightSource ||
               mapping.lightEmission > 0;
    }

    private static void AddDirtySubchunkMask(Dictionary<Vector2Int, int> dirtyMasks, Vector2Int coord, int dirtySubchunkMask)
    {
        dirtySubchunkMask = SanitizeDirtySubchunkMask(dirtySubchunkMask);
        if (dirtySubchunkMask == 0)
            return;

        if (dirtyMasks.TryGetValue(coord, out int existingMask))
            dirtyMasks[coord] = existingMask | dirtySubchunkMask;
        else
            dirtyMasks[coord] = dirtySubchunkMask;
    }

    private void RequestFullChunkRebuild(Vector2Int coord, bool rebuildColliders = true)
    {
        RequestChunkRebuild(coord, GetFullSubchunkMask(), rebuildColliders);
    }

    public void SetVoxelSkyLightMultiplier(float multiplier, bool forceRebuild = false)
    {
        multiplier = Mathf.Clamp01(multiplier);
        if (Mathf.Approximately(voxelSkyLightMultiplier, multiplier))
            return;

        voxelSkyLightMultiplier = multiplier;
    }

    private void RequestChunkRebuild(Vector2Int coord, int dirtySubchunkMask)
    {
        RequestChunkRebuild(coord, dirtySubchunkMask, true);
    }

    private void RequestChunkRebuild(Vector2Int coord, int dirtySubchunkMask, bool rebuildColliders)
    {
        RequestChunkRebuild(coord, dirtySubchunkMask, rebuildColliders, 0f);
    }

    private void RequestChunkRebuildDelayed(Vector2Int coord, int dirtySubchunkMask, bool rebuildColliders, float delaySeconds)
    {
        RequestChunkRebuild(coord, dirtySubchunkMask, rebuildColliders, delaySeconds);
    }

    private void RequestChunkRebuild(Vector2Int coord, int dirtySubchunkMask, bool rebuildColliders, float delaySeconds)
    {
        dirtySubchunkMask = SanitizeDirtySubchunkMask(dirtySubchunkMask);
        if (dirtySubchunkMask == 0)
            return;

        if (queuedChunkRebuildMasks.TryGetValue(coord, out int existingMask))
            queuedChunkRebuildMasks[coord] = existingMask | dirtySubchunkMask;
        else
            queuedChunkRebuildMasks[coord] = dirtySubchunkMask;

        if (queuedChunkRebuildRequiresCollider.TryGetValue(coord, out bool existingRequiresCollider))
            queuedChunkRebuildRequiresCollider[coord] = existingRequiresCollider || rebuildColliders;
        else
            queuedChunkRebuildRequiresCollider[coord] = rebuildColliders;

        if (delaySeconds > 0f)
        {
            float requestedTime = Time.time + delaySeconds;
            if (queuedChunkRebuildEarliestProcessTime.TryGetValue(coord, out float existingTime))
                queuedChunkRebuildEarliestProcessTime[coord] = Mathf.Min(existingTime, requestedTime);
            else if (!queuedChunkRebuildsSet.Contains(coord))
                queuedChunkRebuildEarliestProcessTime[coord] = requestedTime;
        }
        else
        {
            queuedChunkRebuildEarliestProcessTime.Remove(coord);
        }

        if (queuedChunkRebuildsSet.Add(coord))
            queuedChunkRebuilds.Enqueue(coord);
    }

    private void RequestLightingOnlyChunkRebuild(Vector2Int coord, int dirtySubchunkMask)
    {
        if (!enableVoxelLighting)
            return;

        dirtySubchunkMask = SanitizeDirtySubchunkMask(dirtySubchunkMask);
        if (dirtySubchunkMask == 0 || HasQueuedChunkRebuild(coord))
            return;

        if (queuedLightingOnlyChunkRebuildMasks.TryGetValue(coord, out int existingMask))
            queuedLightingOnlyChunkRebuildMasks[coord] = existingMask | dirtySubchunkMask;
        else
            queuedLightingOnlyChunkRebuildMasks[coord] = dirtySubchunkMask;

        if (queuedLightingOnlyChunkRebuildsSet.Add(coord))
            queuedLightingOnlyChunkRebuilds.Enqueue(coord);
    }

    private void ProcessQueuedChunkRebuilds()
    {
        if (queuedChunkRebuilds.Count == 0)
            return;

        int perFrameLimit = Mathf.Max(1, maxChunkRebuildsPerFrame);
        int processed = 0;
        int attempts = queuedChunkRebuilds.Count;

        while (processed < perFrameLimit && attempts-- > 0)
        {
            Vector2Int coord = queuedChunkRebuilds.Dequeue();
            queuedChunkRebuildsSet.Remove(coord);

            if (queuedChunkRebuildEarliestProcessTime.TryGetValue(coord, out float earliestProcessTime) &&
                Time.time < earliestProcessTime)
            {
                if (queuedChunkRebuildsSet.Add(coord))
                    queuedChunkRebuilds.Enqueue(coord);
                continue;
            }

            queuedChunkRebuildEarliestProcessTime.Remove(coord);

            if (!queuedChunkRebuildMasks.TryGetValue(coord, out int dirtySubchunkMask))
            {
                queuedChunkRebuildRequiresCollider.Remove(coord);
                continue;
            }

            queuedChunkRebuildMasks.Remove(coord);
            bool requiresColliderRebuild = queuedChunkRebuildRequiresCollider.TryGetValue(coord, out bool queuedRequiresCollider) && queuedRequiresCollider;
            queuedChunkRebuildRequiresCollider.Remove(coord);

            if (IsChunkJobPending(coord))
            {
                RequestChunkRebuild(coord, dirtySubchunkMask, requiresColliderRebuild);
                continue;
            }

            RequestChunkRebuildImmediate(coord, dirtySubchunkMask, requiresColliderRebuild);
            processed++;
        }
    }

    private void ProcessQueuedLightingOnlyChunkRebuilds()
    {
        if (!enableVoxelLighting || queuedLightingOnlyChunkRebuilds.Count == 0)
            return;

        int perFrameLimit = Mathf.Max(1, maxChunkRebuildsPerFrame);
        int processed = 0;
        int attempts = queuedLightingOnlyChunkRebuilds.Count;

        while (processed < perFrameLimit && attempts-- > 0)
        {
            Vector2Int coord = queuedLightingOnlyChunkRebuilds.Dequeue();
            queuedLightingOnlyChunkRebuildsSet.Remove(coord);

            if (!queuedLightingOnlyChunkRebuildMasks.TryGetValue(coord, out int dirtySubchunkMask))
                continue;

            queuedLightingOnlyChunkRebuildMasks.Remove(coord);
            if (HasQueuedChunkRebuild(coord))
                continue;

            if (IsChunkJobPending(coord))
            {
                RequestLightingOnlyChunkRebuild(coord, dirtySubchunkMask);
                continue;
            }

            if (RequestLightingOnlyChunkRebuildImmediate(coord, dirtySubchunkMask))
                processed++;
        }
    }

    private bool SupportsLightingOnlyRebuildForVisualSlice(Chunk chunk, int visualSliceIndex)
    {
        if (chunk == null)
            return false;

        return chunk.TryGetVisualSliceLightingOnlyRebuildSupport(visualSliceIndex, out bool supports) && supports;
    }

    private bool RequestLightingOnlyChunkRebuildImmediate(Vector2Int coord, int dirtySubchunkMask)
    {
        if (!enableVoxelLighting || !activeChunks.TryGetValue(coord, out Chunk chunk) || chunk == null)
            return false;

        dirtySubchunkMask = SanitizeDirtySubchunkMask(dirtySubchunkMask);
        if (dirtySubchunkMask == 0)
            return false;

        if (chunk.jobScheduled)
        {
            if (!chunk.currentJob.IsCompleted)
            {
                RequestLightingOnlyChunkRebuild(coord, dirtySubchunkMask);
                return false;
            }

            chunk.CompleteTrackedJob();
            chunk.jobScheduled = false;
            chunk.currentJob = default;
        }

        if (!chunk.HasInitializedSubchunks || !chunk.hasVoxelData || !chunk.hasVoxelSnapshot)
        {
            RequestChunkRebuild(coord, dirtySubchunkMask, false);
            return true;
        }

        int touchedVisualSliceMask = 0;
        bool hasUnsupportedLightingOnlySlice = false;
        int visualSliceCount = chunk.visualSlices != null
            ? chunk.visualSlices.Length
            : Chunk.GetVisualSliceCount(chunk.visualSubchunksPerRenderer);

        for (int sliceIndex = 0; sliceIndex < visualSliceCount; sliceIndex++)
        {
            int sliceMask = chunk.GetVisualSliceMask(sliceIndex);
            if ((sliceMask & dirtySubchunkMask) == 0)
                continue;

            touchedVisualSliceMask |= sliceMask;
            if (!SupportsLightingOnlyRebuildForVisualSlice(chunk, sliceIndex))
                hasUnsupportedLightingOnlySlice = true;
        }

        if (hasUnsupportedLightingOnlySlice)
        {
            RequestChunkRebuild(coord, touchedVisualSliceMask, false);
            return true;
        }

        int lightBorderSize = GetMeshNeighborPadding();
        int voxelSizeX = Chunk.SizeX + 2 * lightBorderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * lightBorderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        NativeArray<ushort> chunkBlockLightData = MeshGenerator.RentUshortBuffer(voxelSizeX * Chunk.SizeY * voxelSizeZ, true);
        if (globalLightColumns.Count > 0)
            InjectGlobalLightColumns(chunkBlockLightData, coord.x * Chunk.SizeX, coord.y * Chunk.SizeZ, lightBorderSize, voxelSizeX, voxelSizeZ, voxelPlaneSize);

        chunk.UpdateBlockLightSnapshot(chunkBlockLightData, lightBorderSize);

        JobHandle combinedRelightHandle = default;
        bool hasScheduledRelightJobs = false;
        int expectedGen = chunk.generation;

        for (int sliceIndex = 0; sliceIndex < visualSliceCount; sliceIndex++)
        {
            int sliceDirtySubchunkMask = chunk.GetVisualSliceMask(sliceIndex) & dirtySubchunkMask;
            if (sliceDirtySubchunkMask == 0)
                continue;

            if (!chunk.TryGetVisualSlice(sliceIndex, out ChunkRenderSlice visualSlice) ||
                visualSlice == null ||
                !visualSlice.HasGeometry)
            {
                continue;
            }

            NativeList<MeshGenerator.PackedChunkVertex> relitVertices = MeshGenerator.RentMeshVertexList(Mathf.Max(1, visualSlice.VertexCount));
            if (!visualSlice.TryCaptureVertexData(relitVertices))
            {
                MeshGenerator.ReturnMeshVertexList(ref relitVertices);
                continue;
            }

            MeshGenerator.ScheduleRelightJob(relitVertices, chunkBlockLightData, lightBorderSize, out JobHandle relightHandle);
            combinedRelightHandle = hasScheduledRelightJobs
                ? JobHandle.CombineDependencies(combinedRelightHandle, relightHandle)
                : relightHandle;
            hasScheduledRelightJobs = true;

            pendingMeshes.Add(new PendingMesh
            {
                handle = relightHandle,
                jobCompleted = false,
                vertices = relitVertices,
                opaqueTriangles = default,
                transparentTriangles = default,
                billboardTriangles = default,
                waterTriangles = default,
                coord = coord,
                expectedGen = expectedGen,
                parentChunk = chunk,
                subchunkRanges = default,
                subchunkVisibilityMasks = default,
                dirtySubchunkMask = sliceDirtySubchunkMask,
                visualSliceIndex = sliceIndex,
                heightCache = default,
                blockTypes = default,
                solids = default,
                light = default,
                suppressedBillboards = default,
                buildColliders = false,
                lightingOnlyRebuild = true
            });
            pendingJobPrioritiesDirty = true;
        }

        if (!hasScheduledRelightJobs)
        {
            MeshGenerator.ReturnUshortBuffer(ref chunkBlockLightData);
            return true;
        }

        pendingChunkDataBufferReturns.Add(new PendingChunkDataBufferReturn
        {
            handle = combinedRelightHandle,
            light = chunkBlockLightData
        });

        chunk.currentJob = combinedRelightHandle;
        chunk.jobScheduled = true;
        return true;
    }

    private void RequestChunkRebuildImmediate(Vector2Int coord, int dirtySubchunkMask, bool rebuildColliders)
    {
        if (!activeChunks.TryGetValue(coord, out Chunk chunk))
            return;

        dirtySubchunkMask = SanitizeDirtySubchunkMask(dirtySubchunkMask);
        if (dirtySubchunkMask == 0)
            return;

        if (chunk.jobScheduled)
        {
            if (!chunk.currentJob.IsCompleted)
            {
                RequestChunkRebuild(coord, dirtySubchunkMask, rebuildColliders);
                return;
            }

            chunk.CompleteTrackedJob();
            chunk.jobScheduled = false;
            chunk.currentJob = default;
        }

        int expectedGen = nextChunkGeneration++;
        chunk.generation = expectedGen;
        bool useDetailedGeneration = ShouldChunkUseDetailedGeneration(coord);
        PrepareChunkDetailGenerationTarget(chunk, useDetailedGeneration, expectedGen);

        if (HasCompatibleGenerationDataForRequestedDetail(chunk, useDetailedGeneration) &&
            TryScheduleFastChunkRebuild(coord, chunk, expectedGen, dirtySubchunkMask, rebuildColliders, useDetailedGeneration))
            return;

        chunk.hasVoxelData = false;
        chunk.hasVoxelSnapshot = false;

        rebuildChunkEditsBuffer.Clear();
        int dataBorderSize = GetDetailedGenerationBorderSize();
        int lightBorderSize = Mathf.Max(dataBorderSize, GetLightSmoothingBorderSize());
        int overrideBorderSize = Mathf.Max(dataBorderSize, lightBorderSize);
        AppendRelevantBlockEdits(coord, overrideBorderSize, rebuildChunkEditsBuffer);

        NativeArray<BlockEdit> nativeEdits;
        if (rebuildChunkEditsBuffer.Count > 0)
        {
            nativeEdits = new NativeArray<BlockEdit>(rebuildChunkEditsBuffer.Count, Allocator.Persistent);
            for (int i = 0; i < rebuildChunkEditsBuffer.Count; i++)
                nativeEdits[i] = rebuildChunkEditsBuffer[i];
        }
        else
        {
            nativeEdits = new NativeArray<BlockEdit>(0, Allocator.Persistent);
        }

        int detailBorderSize = GetDetailedGenerationBorderSize();
        int treeMargin = GetMaxTreeMarginForGeneration();
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        EnsureNativeGenerationCaches();

        int voxelSizeX = Chunk.SizeX + 2 * lightBorderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * lightBorderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        NativeArray<ushort> chunkLightData = default;
        if (enableVoxelLighting)
        {
            chunkLightData = MeshGenerator.RentUshortBuffer(voxelSizeX * Chunk.SizeY * voxelSizeZ, true);
            if (globalLightColumns.Count > 0)
                InjectGlobalLightColumns(chunkLightData, chunkMinX, chunkMinZ, lightBorderSize, voxelSizeX, voxelSizeZ, voxelPlaneSize);
        }

        MeshGenerator.ScheduleDataJob(
            coord,
            cachedNativeNoiseLayers,
            cachedNativeBlockMappings,
            cachedNativeEffectiveLightOpacityByBlock,
            cachedNativeLightEmissionByBlock,
            baseHeight,
            IsFlatWorldMode(),
            GetResolvedFlatWorldHeight(),
            GetResolvedFlatWorldBiome(),
            offsetX,
            offsetZ,
            seaLevel,
            enableWater,
            GetBiomeNoiseSettings(),
            GetTerrainDensitySettings(),
            seed,
            nativeEdits,
            treeMargin,
            dataBorderSize,
            lightBorderSize,
            detailBorderSize,
            GetMaxTreeRadiusForGeneration(),
            CliffTreshold,
            enableTrees,
            cachedNativeOreSettings,
            cachedNativeTreeSpawnRules,
            GetSpaghettiCaveSettingsForChunk(useDetailedGeneration),
            enableVoxelLighting,
            enableHorizontalSkylight,
            horizontalSkylightStepLoss,
            chunkLightData,
            chunk.voxelData,
            out JobHandle dataHandle,
            out JobHandle terrainDataHandle,
            out JobHandle lightingHandle,
            out NativeArray<int> heightCache,
            out NativeArray<byte> blockTypes,
            out NativeArray<bool> solids,
            out NativeArray<ushort> light,
            out NativeArray<ushort> blockEmissionData,
            out NativeArray<byte> lightOpacityData,
            out NativeArray<bool> subchunkNonEmpty,
            out NativeArray<ulong> subchunkColliderOccupancy,
            out MeshGenerator.DataJobTempBuffers dataJobTempBuffers);

        NativeArray<byte> knownVoxelData = CreateKnownVoxelPlaceholder();
        int dataVoxelSizeX = Chunk.SizeX + 2 * dataBorderSize;
        int dataVoxelSizeZ = Chunk.SizeZ + 2 * dataBorderSize;
        int dataVoxelPlaneSize = dataVoxelSizeX * Chunk.SizeY;
        NativeArray<byte> blockPlacementAxes = CreateDefaultPlacementAxisArray(blockTypes.Length);
        ApplyPlacementAxesFromBlockEdits(
            nativeEdits,
            blockPlacementAxes,
            coord.x * Chunk.SizeX,
            coord.y * Chunk.SizeZ,
            dataBorderSize,
            dataVoxelSizeX,
            dataVoxelSizeZ,
            dataVoxelPlaneSize);

        pendingDataJobs.Add(new PendingData
        {
            handle = dataHandle,
            terrainHandle = terrainDataHandle,
            lightingHandle = lightingHandle,
            heightCache = heightCache,
            blockTypes = blockTypes,
            blockPlacementAxes = blockPlacementAxes,
            knownVoxelData = knownVoxelData,
            useKnownVoxelData = false,
            solids = solids,
            light = light,
            borderSize = dataBorderSize,
            chunk = chunk,
            coord = coord,
            expectedGen = expectedGen,
            targetDetailedGeneration = useDetailedGeneration,
            chunkLightData = chunkLightData,
            blockEmissionData = blockEmissionData,
            lightOpacityData = lightOpacityData,
            edits = nativeEdits,
            fastRebuildSnapshotVoxelData = default,
            fastRebuildSnapshotLoadedChunks = default,
            fastRebuildOverrides = default,
            tempBuffers = dataJobTempBuffers,
            subchunkColliderOccupancy = subchunkColliderOccupancy,
            subchunkNonEmpty = subchunkNonEmpty,
            dirtySubchunkMask = dirtySubchunkMask,
            rebuildColliders = rebuildColliders,
            terrainStageCompleted = false,
            lightingStageCompleted = false
        });
        pendingJobPrioritiesDirty = true;

        chunk.currentJob = dataHandle;
        chunk.jobScheduled = true;
    }

    private bool HasScheduledChunkPipelineWork(Vector2Int coord)
    {
        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            if (pendingMeshes[i].coord == coord)
                return true;
        }

        for (int i = 0; i < pendingMeshBuildRequests.Count; i++)
        {
            if (pendingMeshBuildRequests[i].coord == coord)
                return true;
        }

        for (int i = 0; i < pendingDataJobs.Count; i++)
        {
            if (pendingDataJobs[i].coord == coord)
                return true;
        }

        return false;
    }

    private bool IsChunkJobPending(Vector2Int coord)
    {
        if (HasScheduledChunkPipelineWork(coord))
            return true;

        return pendingChunkSet.Contains(coord);
    }

    private bool HasQueuedChunkRebuild(Vector2Int coord)
    {
        return queuedChunkRebuildMasks.ContainsKey(coord);
    }

    private void RefreshChunkJobTracking(Vector2Int coord, Chunk chunk)
    {
        if (chunk == null || IsChunkJobPending(coord))
            return;

        if (chunk.jobScheduled && !IsChunkJobCompletedWithoutBlocking(chunk))
        {
            EnqueueChunkJobTrackingRefresh(coord);
            return;
        }

        if (chunk.jobScheduled)
            chunk.CompleteTrackedJob();

        chunk.jobScheduled = false;
        chunk.currentJob = default;

        if (chunk.state == Chunk.ChunkState.MeshReady)
        {
            chunk.TryCommitPendingDetailedGenerationSwap(chunk.generation);
            chunk.state = Chunk.ChunkState.Active;
        }
    }

    private void EnqueueChunkJobTrackingRefresh(Vector2Int coord)
    {
        if (queuedChunkJobTrackingRefreshSet.Add(coord))
            queuedChunkJobTrackingRefreshes.Enqueue(coord);
    }

    private void ProcessQueuedChunkJobTrackingRefreshes()
    {
        if (queuedChunkJobTrackingRefreshes.Count == 0)
            return;

        int attempts = queuedChunkJobTrackingRefreshes.Count;
        int processed = 0;
        int perFrameLimit = Mathf.Max(1, maxDataCompletionsPerFrame + maxLightingCompletionsPerFrame + maxMeshAppliesPerFrame);

        while (processed < perFrameLimit && attempts-- > 0 && queuedChunkJobTrackingRefreshes.Count > 0)
        {
            Vector2Int coord = queuedChunkJobTrackingRefreshes.Dequeue();
            queuedChunkJobTrackingRefreshSet.Remove(coord);

            if (!activeChunks.TryGetValue(coord, out Chunk chunk) || chunk == null)
                continue;

            if (IsChunkJobPending(coord))
                continue;

            if (chunk.jobScheduled && !IsChunkJobCompletedWithoutBlocking(chunk))
            {
                EnqueueChunkJobTrackingRefresh(coord);
                continue;
            }

            RefreshChunkJobTracking(coord, chunk);
            processed++;
        }
    }

    internal void CompletePendingJobsForChunk(Chunk chunk)
    {
        if (chunk == null)
            return;

        for (int i = pendingMeshes.Count - 1; i >= 0; i--)
        {
            PendingMesh pendingMesh = pendingMeshes[i];
            if (!ReferenceEquals(pendingMesh.parentChunk, chunk))
                continue;

            pendingMesh.handle.Complete();
            DisposePendingMesh(pendingMesh);
            RemovePendingMeshAtSwapBack(i);
        }

        for (int i = pendingDataJobs.Count - 1; i >= 0; i--)
        {
            PendingData pendingData = pendingDataJobs[i];
            if (!ReferenceEquals(pendingData.chunk, chunk))
                continue;

            pendingData.handle.Complete();
            DisposeDataJobResources(ref pendingData);
            RemovePendingDataJobAtSwapBack(i);
        }

        for (int i = pendingMeshBuildRequests.Count - 1; i >= 0; i--)
        {
            PendingData pendingData = pendingMeshBuildRequests[i];
            if (!ReferenceEquals(pendingData.chunk, chunk))
                continue;

            pendingData.handle.Complete();
            DisposeDataJobResources(ref pendingData);
            RemovePendingMeshBuildRequestAtSwapBack(i);
        }
    }

    private void RemovePendingDataJobAtSwapBack(int index)
    {
        int last = pendingDataJobs.Count - 1;
        if (index < 0 || index > last)
            return;

        if (index != last)
            pendingDataJobs[index] = pendingDataJobs[last];

        pendingDataJobs.RemoveAt(last);
        pendingJobPrioritiesDirty = true;
    }

    private void RemovePendingMeshAtSwapBack(int index)
    {
        int last = pendingMeshes.Count - 1;
        if (index < 0 || index > last)
            return;

        if (index != last)
            pendingMeshes[index] = pendingMeshes[last];

        pendingMeshes.RemoveAt(last);
        pendingJobPrioritiesDirty = true;
    }

    private void RemovePendingMeshBuildRequestAtSwapBack(int index)
    {
        int last = pendingMeshBuildRequests.Count - 1;
        if (index < 0 || index > last)
            return;

        if (index != last)
            pendingMeshBuildRequests[index] = pendingMeshBuildRequests[last];

        pendingMeshBuildRequests.RemoveAt(last);
        pendingJobPrioritiesDirty = true;
    }
}
