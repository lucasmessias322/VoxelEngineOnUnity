using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Mesh Scheduling

    private void ScheduleSubchunkMeshJobs(ref PendingData pd, Chunk activeChunk)
    {
        int borderSize = Mathf.Max(1, pd.borderSize);
        int dirtySubchunkMask = SanitizeDirtySubchunkMask(pd.dirtySubchunkMask);
        activeChunk.UpdateSubchunkColliderOccupancy(pd.subchunkColliderOccupancy, dirtySubchunkMask);
        MeshGenerator.ReturnUlongBuffer(ref pd.subchunkColliderOccupancy);
        suppressedGrassBillboardInt3Buffer.Clear();
        CollectSuppressedGrassBillboardsForChunk(pd.coord, suppressedGrassBillboardInt3Buffer);
        NativeArray<int3> nativeSuppressedBillboards = new NativeArray<int3>(suppressedGrassBillboardInt3Buffer.Count, Allocator.Persistent);
        for (int s = 0; s < suppressedGrassBillboardInt3Buffer.Count; s++)
            nativeSuppressedBillboards[s] = suppressedGrassBillboardInt3Buffer[s];

        int nonEmptySubchunkMask = 0;
        int affectedVisualSliceMask = 0;
        bool updatedEmptySectionVisibility = false;
        for (int sub = 0; sub < Chunk.SubchunksPerColumn; sub++)
        {
            if (pd.subchunkNonEmpty[sub])
                nonEmptySubchunkMask |= 1 << sub;

            if ((dirtySubchunkMask & (1 << sub)) == 0)
                continue;

            affectedVisualSliceMask |= 1 << activeChunk.GetVisualSliceIndexForSubchunk(sub);

            if (!pd.subchunkNonEmpty[sub])
            {
                activeChunk.ClearSubchunkMesh(sub);
                if (activeChunk.SetSubchunkVisibilityData(sub, SubchunkOcclusion.AllVisibleMask))
                    updatedEmptySectionVisibility = true;
                continue;
            }
        }

        if (updatedEmptySectionVisibility)
            InvalidateSectionOcclusionGraph();

        JobHandle combinedMeshHandle = default;
        bool hasScheduledMeshJobs = false;
        if (affectedVisualSliceMask != 0)
        {
            bool useDetailedGeneration = pd.targetDetailedGeneration;
            bool generateDetailedLeafFoliage = ShouldGenerateDetailedLeafFoliageForChunk(useDetailedGeneration);
            float effectiveAoStrength = enableAmbientOcclusion ? aoStrength : 0f;
            float leafFoliageSpawnChance = Mathf.Clamp01(treeLeafFoliageSpawnChance);
            float leafFoliageHeightMin = Mathf.Clamp(treeLeafFoliageHeightMin, 0.2f, 2f);
            float leafFoliageHeightMax = Mathf.Max(leafFoliageHeightMin, Mathf.Clamp(treeLeafFoliageHeightMax, 0.2f, 2f));
            float leafFoliageHalfWidthMin = Mathf.Clamp(treeLeafFoliageHalfWidthMin, 0.5f, 1f);
            float leafFoliageHalfWidthMax = Mathf.Max(leafFoliageHalfWidthMin, Mathf.Clamp(treeLeafFoliageHalfWidthMax, 0.5f, 1f));
            float leafFoliageBaseYOffsetMin = Mathf.Clamp(treeLeafFoliageBaseYOffsetMin, -0.2f, 0.4f);
            float leafFoliageBaseYOffsetMax = Mathf.Max(leafFoliageBaseYOffsetMin, Mathf.Clamp(treeLeafFoliageBaseYOffsetMax, -0.2f, 0.4f));
            float leafFoliageCenterJitter = Mathf.Clamp(treeLeafFoliageCenterJitter, 0f, 0.2f);
            float leafUltraHeight = Mathf.Clamp(treeLeafUltraBillboardHeight, 0.4f, 2.5f);
            float leafUltraHalfWidth = Mathf.Clamp(treeLeafUltraBillboardHalfWidth, 0.5f, 1.6f);
            float leafUltraBaseYOffset = Mathf.Clamp(treeLeafUltraBaseYOffset, -0.4f, 0.4f);
            float leafUltraCenterJitter = Mathf.Clamp(treeLeafUltraCenterJitter, 0f, 0.2f);
            float leafUltraRotationOffsetDegrees = Mathf.Clamp(treeLeafUltraRotationOffsetDegrees, 0f, 45f);
            float leafUltraRotationRandomDegrees = Mathf.Clamp(treeLeafUltraRotationRandomDegrees, 0f, 30f);
            float leafUltraFaceTiltDegrees = Mathf.Clamp(treeLeafUltraFaceTiltDegrees, 0f, 60f);
            float leafUltraFaceTiltRandomDegrees = Mathf.Clamp(treeLeafUltraFaceTiltRandomDegrees, 0f, 30f);

            int visualSliceCount = activeChunk.visualSlices != null
                ? activeChunk.visualSlices.Length
                : Chunk.GetVisualSliceCount(activeChunk.visualSubchunksPerRenderer);

            for (int sliceIndex = 0; sliceIndex < visualSliceCount; sliceIndex++)
            {
                int visualSliceBit = 1 << sliceIndex;
                if ((affectedVisualSliceMask & visualSliceBit) == 0)
                    continue;

                int scheduledSubchunkMask = activeChunk.GetVisualSliceMask(sliceIndex) & nonEmptySubchunkMask;
                if (scheduledSubchunkMask == 0)
                {
                    if (activeChunk.TryGetVisualSlice(sliceIndex, out ChunkRenderSlice emptySlice))
                        emptySlice.ClearMesh();
                    activeChunk.RefreshVisualSliceVisibility(sliceIndex);
                    continue;
                }

                MeshGenerator.ScheduleMeshJob(
                    pd.heightCache, pd.blockTypes, pd.blockPlacementAxes, pd.solids, pd.light, cachedNativeBlockMappings, cachedNativeBlockModelCuboids, nativeSuppressedBillboards,
                    pd.subchunkNonEmpty, pd.knownVoxelData, pd.useKnownVoxelData,
                    atlasTilesX, atlasTilesY, true, borderSize,
                    pd.coord.x, pd.coord.y,
                    scheduledSubchunkMask,
                    ShouldGenerateGrassBillboardsForChunk(useDetailedGeneration), grassBillboardChance, grassBillboardBlockType, grassBillboardHeight,
                    grassBillboardNoiseScale, grassBillboardJitter, cachedNativeVegetationBillboardRules, GetBiomeNoiseSettings(),
                    effectiveAoStrength, aoCurveExponent, aoMinLight, useFastBedrockStyleMeshing,
                    generateDetailedLeafFoliage && treeLeafQuality == TreeLeafQualityMode.High,
                    generateDetailedLeafFoliage && treeLeafQuality == TreeLeafQualityMode.Ultra,
                    leafFoliageSpawnChance, leafFoliageHeightMin, leafFoliageHeightMax,
                    leafFoliageHalfWidthMin, leafFoliageHalfWidthMax,
                    leafFoliageBaseYOffsetMin, leafFoliageBaseYOffsetMax,
                    leafFoliageCenterJitter,
                    leafUltraHeight, leafUltraHalfWidth, leafUltraBaseYOffset, leafUltraCenterJitter,
                    leafUltraRotationOffsetDegrees, leafUltraRotationRandomDegrees,
                    leafUltraFaceTiltDegrees, leafUltraFaceTiltRandomDegrees,
                    out JobHandle meshHandle,
                    out NativeList<MeshGenerator.PackedChunkVertex> vertices,
                    out NativeList<int> opaqueTriangles,
                    out NativeList<int> transparentTriangles,
                    out NativeList<int> billboardTriangles,
                    out NativeList<int> waterTriangles,
                    out NativeArray<MeshGenerator.SubchunkMeshRange> subchunkRanges,
                    out NativeArray<ulong> subchunkVisibilityMasks
                );

                combinedMeshHandle = hasScheduledMeshJobs
                    ? JobHandle.CombineDependencies(combinedMeshHandle, meshHandle)
                    : meshHandle;
                hasScheduledMeshJobs = true;

                pendingMeshes.Add(new PendingMesh
                {
                    handle = meshHandle,
                    jobCompleted = false,
                    vertices = vertices,
                    opaqueTriangles = opaqueTriangles,
                    transparentTriangles = transparentTriangles,
                    billboardTriangles = billboardTriangles,
                    waterTriangles = waterTriangles,
                    coord = pd.coord,
                    expectedGen = pd.expectedGen,
                    parentChunk = activeChunk,
                    subchunkRanges = subchunkRanges,
                    subchunkVisibilityMasks = subchunkVisibilityMasks,
                    dirtySubchunkMask = scheduledSubchunkMask,
                    visualSliceIndex = sliceIndex,
                    heightCache = default,
                    blockTypes = default,
                    solids = default,
                    light = default,
                    suppressedBillboards = default,
                    buildColliders = pd.rebuildColliders
                });
                pendingJobPrioritiesDirty = true;
            }
        }

        var disposeSuppressedBillboardsJob = new MeshGenerator.DisposeSuppressedBillboardsJob
        {
            suppressedGrassBillboards = nativeSuppressedBillboards
        };
        JobHandle suppressedDisposeHandle = disposeSuppressedBillboardsJob.Schedule(combinedMeshHandle);

        QueueChunkDataBufferReturn(combinedMeshHandle, ref pd);
        activeChunk.currentJob = JobHandle.CombineDependencies(combinedMeshHandle, suppressedDisposeHandle);
    }

    private void CollectSuppressedGrassBillboardsForChunk(Vector2Int chunkCoord, List<int3> output)
    {
        if (output == null)
            return;

        output.Clear();
        if (!suppressedGrassBillboardsByChunk.TryGetValue(chunkCoord, out HashSet<Vector3Int> positions) || positions.Count == 0)
            return;

        foreach (Vector3Int pos in positions)
        {
            if (pos.y >= 0 && pos.y < Chunk.SizeY)
                output.Add(new int3(pos.x, pos.y, pos.z));
        }
    }

    private static Vector2Int GetChunkCoordFromWorldXZ(int worldX, int worldZ)
    {
        return new Vector2Int(
            Mathf.FloorToInt((float)worldX / Chunk.SizeX),
            Mathf.FloorToInt((float)worldZ / Chunk.SizeZ)
        );
    }

    private void IndexSuppressedGrassBillboard(Vector3Int pos)
    {
        Vector2Int coord = GetChunkCoordFromWorldXZ(pos.x, pos.z);
        if (!suppressedGrassBillboardsByChunk.TryGetValue(coord, out HashSet<Vector3Int> set))
        {
            set = new HashSet<Vector3Int>(InitialPerChunkBlockEditCapacity);
            suppressedGrassBillboardsByChunk[coord] = set;
        }

        set.Add(pos);
    }

    private bool RemoveSuppressedGrassBillboard(Vector3Int pos, bool allowPermanentRemoval = false)
    {
        if (allowPermanentRemoval)
            permanentGrassBillboardSuppressions.Remove(pos);
        else if (permanentGrassBillboardSuppressions.Contains(pos))
            return false;

        if (!suppressedGrassBillboards.Remove(pos))
            return false;

        Vector2Int coord = GetChunkCoordFromWorldXZ(pos.x, pos.z);
        if (suppressedGrassBillboardsByChunk.TryGetValue(coord, out HashSet<Vector3Int> set))
        {
            set.Remove(pos);
            if (set.Count == 0)
                suppressedGrassBillboardsByChunk.Remove(coord);
        }

        return true;
    }

    private void InjectGlobalLightColumns(
        NativeArray<ushort> chunkLightData,
        int chunkMinX,
        int chunkMinZ,
        int borderSize,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        if (globalLightColumns.Count == 0) return;

        int minWX = chunkMinX - borderSize;
        int maxWX = chunkMinX + Chunk.SizeX + borderSize - 1;
        int minWZ = chunkMinZ - borderSize;
        int maxWZ = chunkMinZ + Chunk.SizeZ + borderSize - 1;

        int areaColumns = voxelSizeX * voxelSizeZ;

        // Sparse mode: iterate only existing global columns (better when emissive lights are sparse).
        if (globalLightColumns.Count < areaColumns)
        {
            foreach (var kv in globalLightColumns)
            {
                int wx = kv.Key.x;
                int wz = kv.Key.y;
                if (wx < minWX || wx > maxWX || wz < minWZ || wz > maxWZ) continue;

                ushort[] column = kv.Value;
                int padX = wx - chunkMinX + borderSize;
                int padZ = wz - chunkMinZ + borderSize;

                int idx = padX + padZ * voxelPlaneSize;
                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    chunkLightData[idx] = column[y];
                    idx += voxelSizeX;
                }
            }
            return;
        }

        // Dense mode: bounded grid lookup (better when many lit columns exist).
        for (int wx = minWX; wx <= maxWX; wx++)
        {
            for (int wz = minWZ; wz <= maxWZ; wz++)
            {
                var key = new Vector2Int(wx, wz);
                if (!globalLightColumns.TryGetValue(key, out ushort[] column)) continue;

                int padX = wx - chunkMinX + borderSize;
                int padZ = wz - chunkMinZ + borderSize;

                int idx = padX + padZ * voxelPlaneSize;
                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    chunkLightData[idx] = column[y];
                    idx += voxelSizeX;
                }
            }
        }
    }

    #endregion

}
