using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Voxel Data Copy

    private void ApplyCurrentBlockOverridesToChunkData(
        Vector2Int coord,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<bool> subchunkNonEmpty,
        NativeArray<int> heightCache)
    {
        ApplyCurrentBlockOverridesToChunkData(
            coord,
            blockTypes,
            solids,
            subchunkNonEmpty,
            heightCache,
            InferBorderSizeFromChunkArrays(blockTypes, heightCache),
            default);
    }

    private static NativeArray<byte> CreateFullyKnownVoxelMask(int length)
    {
        NativeArray<byte> knownVoxelData = MeshGenerator.RentByteBuffer(length);
        for (int i = 0; i < knownVoxelData.Length; i++)
            knownVoxelData[i] = 1;

        return knownVoxelData;
    }

    private static NativeArray<byte> CreateKnownVoxelPlaceholder()
    {
        NativeArray<byte> knownVoxelData = MeshGenerator.RentByteBuffer(1);
        knownVoxelData[0] = 1;
        return knownVoxelData;
    }

    private void ApplyCurrentBlockOverridesToChunkData(
        Vector2Int coord,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<bool> subchunkNonEmpty,
        NativeArray<int> heightCache,
        int borderSize,
        NativeArray<byte> voxelSnapshot)
    {
        if (blockOverrides.Count == 0 ||
            !blockTypes.IsCreated ||
            !solids.IsCreated ||
            !subchunkNonEmpty.IsCreated ||
            !heightCache.IsCreated ||
            blockData == null ||
            blockData.mappings == null ||
            blockData.mappings.Length == 0)
        {
            return;
        }

        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;

        CollectRelevantTerrainOverridePositions(coord, borderSize, relevantTerrainOverridePositions);
        if (relevantTerrainOverridePositions.Count == 0)
            return;

        bool hasRelevantOverrides = false;
        for (int i = 0; i < relevantTerrainOverridePositions.Count; i++)
        {
            Vector3Int worldPos = relevantTerrainOverridePositions[i];
            if (!blockOverrides.TryGetValue(worldPos, out BlockType overrideType))
                continue;
            if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
                continue;
            overrideType = ResolveWaterStateForDebug(overrideType);

            int ix = worldPos.x - chunkMinX + borderSize;
            int iz = worldPos.z - chunkMinZ + borderSize;
            if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                continue;

            int idx = ix + worldPos.y * voxelSizeX + iz * voxelPlaneSize;
            if (idx < 0 || idx >= blockTypes.Length)
                continue;

            blockTypes[idx] = (byte)overrideType;
            UpdateVoxelSnapshotCell(voxelSnapshot, chunkMinX, chunkMinZ, worldPos, overrideType);
            hasRelevantOverrides = true;
        }

        if (!hasRelevantOverrides)
            return;

        RefreshChunkDerivedData(coord, blockTypes, solids, subchunkNonEmpty, heightCache, borderSize);
    }

    private bool TrySchedulePostCompletionOverrideRefresh(ref PendingData pd, Chunk chunk)
    {
        if (pd.postOverrideRefreshScheduled ||
            blockOverrides.Count == 0 ||
            blockData == null ||
            blockData.mappings == null ||
            blockData.mappings.Length == 0 ||
            !pd.blockTypes.IsCreated ||
            !pd.solids.IsCreated ||
            !pd.heightCache.IsCreated ||
            !pd.subchunkNonEmpty.IsCreated ||
            !pd.subchunkColliderOccupancy.IsCreated ||
            chunk == null ||
            !chunk.voxelData.IsCreated)
        {
            return false;
        }

        NativeArray<BlockEdit> currentOverrides = BuildFastRebuildOverrideArray(pd.coord, pd.borderSize);
        if (!currentOverrides.IsCreated || currentOverrides.Length == 0)
        {
            SafeDisposeNativeArray(ref currentOverrides);
            return false;
        }

        int voxelSizeX = Chunk.SizeX + 2 * pd.borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * pd.borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        NativeArray<byte> dirtyColumns = MeshGenerator.RentByteBuffer(voxelSizeX * voxelSizeZ, true);

        var overrideRefreshJob = new PostApplyCurrentOverridesJob
        {
            overrides = currentOverrides,
            blockMappings = cachedNativeBlockMappings,
            blockTypes = pd.blockTypes,
            blockPlacementAxes = pd.blockPlacementAxes,
            solids = pd.solids,
            heightCache = pd.heightCache,
            subchunkNonEmpty = pd.subchunkNonEmpty,
            subchunkColliderOccupancy = pd.subchunkColliderOccupancy,
            dirtyColumns = dirtyColumns,
            chunkMinX = pd.coord.x * Chunk.SizeX,
            chunkMinZ = pd.coord.y * Chunk.SizeZ,
            borderSize = pd.borderSize,
            voxelSizeX = voxelSizeX,
            voxelSizeZ = voxelSizeZ,
            voxelPlaneSize = voxelPlaneSize
        };

        pd.handle = overrideRefreshJob.Schedule();
        pd.terrainHandle = pd.handle;
        pd.lightingHandle = pd.handle;
        pd.postCompletionOverrides = currentOverrides;
        pd.postCompletionDirtyColumns = dirtyColumns;
        pd.postOverrideRefreshScheduled = true;
        pd.terrainStageCompleted = false;
        pd.lightingStageCompleted = false;

        chunk.currentJob = pd.handle;
        chunk.jobScheduled = true;
        pendingJobPrioritiesDirty = true;
        return true;
    }

    private void SyncCurrentBlockOverridesToVoxelSnapshot(
        Vector2Int coord,
        int borderSize,
        NativeArray<byte> voxelSnapshot)
    {
        if (blockOverrides.Count == 0 || !voxelSnapshot.IsCreated)
            return;

        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        CollectRelevantTerrainOverridePositions(coord, borderSize, relevantTerrainOverridePositions);
        for (int i = 0; i < relevantTerrainOverridePositions.Count; i++)
        {
            Vector3Int worldPos = relevantTerrainOverridePositions[i];
            if (!blockOverrides.TryGetValue(worldPos, out BlockType overrideType))
                continue;
            overrideType = ResolveWaterStateForDebug(overrideType);

            UpdateVoxelSnapshotCell(voxelSnapshot, chunkMinX, chunkMinZ, worldPos, overrideType);
        }
    }

    private static void UpdateVoxelSnapshotCell(
        NativeArray<byte> voxelSnapshot,
        int chunkMinX,
        int chunkMinZ,
        Vector3Int worldPos,
        BlockType blockType)
    {
        if (!voxelSnapshot.IsCreated)
            return;

        int localX = worldPos.x - chunkMinX;
        int localZ = worldPos.z - chunkMinZ;
        int localY = worldPos.y;
        if (localX < 0 || localX >= Chunk.SizeX ||
            localZ < 0 || localZ >= Chunk.SizeZ ||
            localY < 0 || localY >= Chunk.SizeY)
        {
            return;
        }

        int snapshotIndex = localX + localZ * Chunk.SizeX + localY * Chunk.SizeX * Chunk.SizeZ;
        if (snapshotIndex < 0 || snapshotIndex >= voxelSnapshot.Length)
            return;

        voxelSnapshot[snapshotIndex] = (byte)blockType;
    }

    private static int InferBorderSizeFromChunkArrays(NativeArray<byte> blockTypes, NativeArray<int> heightCache)
    {
        if (heightCache.IsCreated && heightCache.Length > 0)
        {
            int paddedSize = Mathf.RoundToInt(Mathf.Sqrt(heightCache.Length));
            if (paddedSize >= Chunk.SizeX)
                return Mathf.Max(0, (paddedSize - Chunk.SizeX) / 2);
        }

        if (blockTypes.IsCreated && blockTypes.Length > 0 && Chunk.SizeY > 0)
        {
            int paddedArea = blockTypes.Length / Chunk.SizeY;
            int paddedSize = Mathf.RoundToInt(Mathf.Sqrt(paddedArea));
            if (paddedSize >= Chunk.SizeX)
                return Mathf.Max(0, (paddedSize - Chunk.SizeX) / 2);
        }

        return 0;
    }

    private void RefreshChunkDerivedData(
        Vector2Int coord,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<bool> subchunkNonEmpty,
        NativeArray<int> heightCache,
        int borderSize)
    {
        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int heightStride = voxelSizeX;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        BlockTextureMapping[] mappings = blockData.mappings;

        for (int s = 0; s < Chunk.SubchunksPerColumn; s++)
            subchunkNonEmpty[s] = false;

        for (int lz = -borderSize; lz < Chunk.SizeZ + borderSize; lz++)
        {
            int worldZ = chunkMinZ + lz;
            int iz = lz + borderSize;

            for (int lx = -borderSize; lx < Chunk.SizeX + borderSize; lx++)
            {
                int worldX = chunkMinX + lx;
                int ix = lx + borderSize;
                int highestSolidY = 0;

                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    int idx = ix + y * voxelSizeX + iz * voxelPlaneSize;
                    BlockType bt = (BlockType)blockTypes[idx];
                    bool isSolid = mappings[(int)bt].isSolid;
                    solids[idx] = isSolid;
                    if (isSolid)
                        highestSolidY = y;

                    if (worldX >= chunkMinX &&
                        worldX < chunkMinX + Chunk.SizeX &&
                        worldZ >= chunkMinZ &&
                        worldZ < chunkMinZ + Chunk.SizeZ &&
                        bt != BlockType.Air)
                    {
                        int subIdx = y / Chunk.SubchunkHeight;
                        if (subIdx >= 0 && subIdx < Chunk.SubchunksPerColumn)
                            subchunkNonEmpty[subIdx] = true;
                    }
                }

                heightCache[ix + iz * heightStride] = highestSolidY;
            }
        }
    }

    #endregion

}
