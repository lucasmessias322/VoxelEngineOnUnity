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

    private void RequestChunkRebuild(Vector2Int coord)
    {
        RequestChunkRebuild(coord, GetFullSubchunkMask(), true);
    }

    private void RequestChunkRebuild(Vector2Int coord, int dirtySubchunkMask)
    {
        RequestChunkRebuild(coord, dirtySubchunkMask, true);
    }

    private void RequestChunkRebuild(Vector2Int coord, int dirtySubchunkMask, bool rebuildColliders)
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

        if (queuedChunkRebuildsSet.Add(coord))
            queuedChunkRebuilds.Enqueue(coord);
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

        if (TryScheduleFastChunkRebuild(coord, chunk, expectedGen, dirtySubchunkMask, rebuildColliders))
            return;

        chunk.hasVoxelData = false;

        var editsList = new List<BlockEdit>();
        int dataBorderSize = GetDetailedGenerationBorderSize();
        int lightBorderSize = Mathf.Max(dataBorderSize, GetLightSmoothingBorderSize());
        int overrideBorderSize = Mathf.Max(dataBorderSize, lightBorderSize);
        AppendRelevantBlockEdits(coord, overrideBorderSize, editsList);

        NativeArray<BlockEdit> nativeEdits;
        if (editsList.Count > 0)
        {
            nativeEdits = new NativeArray<BlockEdit>(editsList.Count, Allocator.Persistent);
            for (int i = 0; i < editsList.Count; i++)
                nativeEdits[i] = editsList[i];
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
        NativeArray<byte> chunkLightData = default;
        if (enableVoxelLighting)
        {
            chunkLightData = new NativeArray<byte>(voxelSizeX * Chunk.SizeY * voxelSizeZ, Allocator.Persistent);
            InjectGlobalLightColumns(chunkLightData, chunkMinX, chunkMinZ, lightBorderSize, voxelSizeX, voxelSizeZ, voxelPlaneSize);
        }

        MeshGenerator.ScheduleDataJob(
            coord,
            cachedNativeNoiseLayers,
            cachedNativeBlockMappings,
            cachedNativeEffectiveLightOpacityByBlock,
            baseHeight,
            offsetX,
            offsetZ,
            seaLevel,
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
            caveGenerationMode,
            caveWormSettings,
            caveSpaghettiSettings,
            enableVoxelLighting,
            enableHorizontalSkylight,
            horizontalSkylightStepLoss,
            chunkLightData,
            chunk.voxelData,
            out JobHandle dataHandle,
            out NativeArray<int> heightCache,
            out NativeArray<byte> blockTypes,
            out NativeArray<bool> solids,
            out NativeArray<byte> light,
            out NativeArray<byte> lightOpacityData,
            out NativeArray<bool> subchunkNonEmpty);

        NativeArray<byte> knownVoxelData = CreateFullyKnownVoxelMask(blockTypes.Length);

        pendingDataJobs.Add(new PendingData
        {
            handle = dataHandle,
            heightCache = heightCache,
            blockTypes = blockTypes,
            knownVoxelData = knownVoxelData,
            solids = solids,
            light = light,
            borderSize = dataBorderSize,
            chunk = chunk,
            coord = coord,
            expectedGen = expectedGen,
            chunkLightData = chunkLightData,
            lightOpacityData = lightOpacityData,
            edits = nativeEdits,
            fastRebuildSnapshotVoxelData = default,
            fastRebuildSnapshotLoadedChunks = default,
            fastRebuildOverrides = default,
            subchunkNonEmpty = subchunkNonEmpty,
            dirtySubchunkMask = dirtySubchunkMask,
            rebuildColliders = rebuildColliders
        });

        chunk.currentJob = dataHandle;
        chunk.jobScheduled = true;
    }

    private bool IsChunkJobPending(Vector2Int coord)
    {
        for (int i = 0; i < pendingMeshes.Count; i++)
        {
            if (pendingMeshes[i].coord == coord)
                return true;
        }

        for (int i = 0; i < pendingDataJobs.Count; i++)
        {
            if (pendingDataJobs[i].coord == coord)
                return true;
        }

        for (int i = 0; i < pendingChunks.Count; i++)
        {
            if (pendingChunks[i].coord == coord)
                return true;
        }

        return false;
    }

    private bool HasQueuedChunkRebuild(Vector2Int coord)
    {
        return queuedChunkRebuildMasks.ContainsKey(coord);
    }

    private void RefreshChunkJobTracking(Vector2Int coord, Chunk chunk)
    {
        if (chunk == null || IsChunkJobPending(coord))
            return;

        chunk.CompleteTrackedJob();
        chunk.jobScheduled = false;
        chunk.currentJob = default;

        if (chunk.state == Chunk.ChunkState.MeshReady)
            chunk.state = Chunk.ChunkState.Active;
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
    }

    private void RemovePendingDataJobAtSwapBack(int index)
    {
        int last = pendingDataJobs.Count - 1;
        if (index < 0 || index > last)
            return;

        if (index != last)
            pendingDataJobs[index] = pendingDataJobs[last];

        pendingDataJobs.RemoveAt(last);
    }

    private void RemovePendingMeshAtSwapBack(int index)
    {
        int last = pendingMeshes.Count - 1;
        if (index < 0 || index > last)
            return;

        if (index != last)
            pendingMeshes[index] = pendingMeshes[last];

        pendingMeshes.RemoveAt(last);
    }
}
