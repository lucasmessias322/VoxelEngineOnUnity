using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public static partial class MeshGenerator
{
    private struct SpaghettiCarveMaskCacheEntry
    {
        public NativeArray<byte> mask;
        public JobHandle readyHandle;
        public int voxelSizeX;
        public int voxelSizeZ;
        public int voxelPlaneSize;
        public int borderSize;
        public int settingsHash;
        public int lastTouchedFrame;
    }

    private static readonly Dictionary<Vector2Int, SpaghettiCarveMaskCacheEntry> spaghettiCarveMaskNeighborCache = new Dictionary<Vector2Int, SpaghettiCarveMaskCacheEntry>();
    private static readonly List<KeyValuePair<Vector2Int, int>> spaghettiCarveMaskCacheSortBuffer = new List<KeyValuePair<Vector2Int, int>>(SpaghettiCarveMaskCacheMaxEntries + 8);
    private static readonly Dictionary<int, Stack<NativeArray<byte>>> pooledTempByteArrays = new Dictionary<int, Stack<NativeArray<byte>>>();
    private static readonly Dictionary<int, Stack<NativeArray<TerrainDensitySettings>>> pooledTempDensitySettingsArrays = new Dictionary<int, Stack<NativeArray<TerrainDensitySettings>>>();
    private static readonly Dictionary<int, Stack<NativeArray<TerrainColumnContext>>> pooledTempColumnContextArrays = new Dictionary<int, Stack<NativeArray<TerrainColumnContext>>>();

    public struct DataJobTempBuffers
    {
        public NativeArray<byte> densityClassifications;
        public NativeArray<TerrainDensitySettings> resolvedDensitySettingsByColumn;
        public NativeArray<byte> terrainSolidMaskByColumn;
        public NativeArray<TerrainColumnContext> dataColumnContexts;
    }

    public static void ClearSpaghettiCarveMaskNeighborCache()
    {
        if (spaghettiCarveMaskNeighborCache.Count == 0)
            return;

        foreach (KeyValuePair<Vector2Int, SpaghettiCarveMaskCacheEntry> pair in spaghettiCarveMaskNeighborCache)
        {
            SpaghettiCarveMaskCacheEntry entry = pair.Value;
            DisposeSpaghettiCarveMaskCacheEntry(ref entry);
        }

        spaghettiCarveMaskNeighborCache.Clear();
        spaghettiCarveMaskCacheSortBuffer.Clear();
    }

    public static void ClearDataJobTempBufferPool()
    {
        DisposePooledNativeArrays(pooledTempByteArrays);
        DisposePooledNativeArrays(pooledTempDensitySettingsArrays);
        DisposePooledNativeArrays(pooledTempColumnContextArrays);
    }

    private static DataJobTempBuffers RentDataJobTempBuffers(int totalVoxels, int totalHeightPoints)
    {
        DataJobTempBuffers buffers;
        buffers.densityClassifications = RentPooledNativeArray(pooledTempByteArrays, totalVoxels);
        buffers.resolvedDensitySettingsByColumn = RentPooledNativeArray(pooledTempDensitySettingsArrays, totalHeightPoints);
        buffers.terrainSolidMaskByColumn = RentPooledNativeArray(pooledTempByteArrays, totalVoxels);
        buffers.dataColumnContexts = RentPooledNativeArray(pooledTempColumnContextArrays, totalHeightPoints);
        return buffers;
    }

    public static void ReleaseDataJobTempBuffers(ref DataJobTempBuffers buffers)
    {
        ReturnPooledNativeArray(pooledTempByteArrays, ref buffers.densityClassifications);
        ReturnPooledNativeArray(pooledTempDensitySettingsArrays, ref buffers.resolvedDensitySettingsByColumn);
        ReturnPooledNativeArray(pooledTempByteArrays, ref buffers.terrainSolidMaskByColumn);
        ReturnPooledNativeArray(pooledTempColumnContextArrays, ref buffers.dataColumnContexts);
        buffers = default;
    }

    private static NativeArray<T> RentPooledNativeArray<T>(
        Dictionary<int, Stack<NativeArray<T>>> pool,
        int length) where T : struct
    {
        if (length <= 0)
            return new NativeArray<T>(0, Allocator.Persistent);

        if (pool.TryGetValue(length, out Stack<NativeArray<T>> stack))
        {
            while (stack.Count > 0)
            {
                NativeArray<T> candidate = stack.Pop();
                if (candidate.IsCreated && candidate.Length == length)
                    return candidate;

                if (candidate.IsCreated)
                    candidate.Dispose();
            }
        }

        return new NativeArray<T>(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
    }

    private static void ReturnPooledNativeArray<T>(
        Dictionary<int, Stack<NativeArray<T>>> pool,
        ref NativeArray<T> array) where T : struct
    {
        if (!array.IsCreated)
        {
            array = default;
            return;
        }

        int length = array.Length;
        if (length <= 0)
        {
            array.Dispose();
            array = default;
            return;
        }

        if (!pool.TryGetValue(length, out Stack<NativeArray<T>> stack))
        {
            stack = new Stack<NativeArray<T>>(TempGenerationPoolMaxArraysPerSize);
            pool[length] = stack;
        }

        if (stack.Count >= TempGenerationPoolMaxArraysPerSize)
            array.Dispose();
        else
            stack.Push(array);

        array = default;
    }

    private static void DisposePooledNativeArrays<T>(Dictionary<int, Stack<NativeArray<T>>> pool) where T : struct
    {
        foreach (KeyValuePair<int, Stack<NativeArray<T>>> pair in pool)
        {
            Stack<NativeArray<T>> stack = pair.Value;
            while (stack.Count > 0)
            {
                NativeArray<T> array = stack.Pop();
                if (array.IsCreated)
                    array.Dispose();
            }
        }

        pool.Clear();
    }

    private static void DisposeSpaghettiCarveMaskCacheEntry(ref SpaghettiCarveMaskCacheEntry entry)
    {
        if (entry.mask.IsCreated)
        {
            entry.readyHandle.Complete();
            entry.mask.Dispose();
        }

        entry = default;
    }

    private static void RemoveSpaghettiCarveMaskCacheEntry(Vector2Int coord)
    {
        if (!spaghettiCarveMaskNeighborCache.TryGetValue(coord, out SpaghettiCarveMaskCacheEntry entry))
            return;

        DisposeSpaghettiCarveMaskCacheEntry(ref entry);
        spaghettiCarveMaskNeighborCache.Remove(coord);
    }

    private static int GetSpaghettiCacheTouchStamp()
    {
        return Application.isPlaying ? Time.frameCount : Environment.TickCount;
    }

    private static int ComputeSpaghettiCarveMaskSettingsHash(int oreSeed, int borderSize, in SpaghettiCaveSettings settings)
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + oreSeed;
            hash = (hash * 31) + borderSize;
            hash = (hash * 31) + (settings.enabled ? 1 : 0);
            hash = (hash * 31) + settings.minY;
            hash = (hash * 31) + settings.maxY;
            hash = (hash * 31) + settings.minSurfaceDepth;
            hash = (hash * 31) + settings.entranceSurfaceDepth;
            hash = (hash * 31) + math.asint(settings.densityBias);
            hash = (hash * 31) + settings.seedOffset;
            return hash;
        }
    }

    private static int ComputeSpaghettiCarveMaskSettingsHash(
        int oreSeed,
        int borderSize,
        in SpaghettiCaveSettings settings,
        NativeArray<NoiseLayer> noiseLayers,
        int baseHeight,
        float offsetX,
        float offsetZ,
        in BiomeNoiseSettings biomeNoiseSettings)
    {
        unchecked
        {
            int hash = ComputeSpaghettiCarveMaskSettingsHash(oreSeed, borderSize, in settings);
            AddHash(ref hash, baseHeight);
            AddHash(ref hash, math.asint(offsetX));
            AddHash(ref hash, math.asint(offsetZ));
            AddHash(ref hash, biomeNoiseSettings.GetHashCode());

            int layerCount = noiseLayers.IsCreated ? noiseLayers.Length : 0;
            AddHash(ref hash, layerCount);
            for (int i = 0; i < layerCount; i++)
                AddNoiseLayerHash(ref hash, noiseLayers[i]);

            return hash;
        }
    }

    private static void AddNoiseLayerHash(ref int hash, in NoiseLayer layer)
    {
        unchecked
        {
            AddHash(ref hash, layer.enabled ? 1 : 0);
            AddHash(ref hash, (int)layer.role);
            AddHash(ref hash, math.asint(layer.scale));
            AddHash(ref hash, math.asint(layer.amplitude));
            AddHash(ref hash, layer.octaves);
            AddHash(ref hash, math.asint(layer.persistence));
            AddHash(ref hash, math.asint(layer.lacunarity));
            AddHash(ref hash, math.asint(layer.offset.x));
            AddHash(ref hash, math.asint(layer.offset.y));
            AddHash(ref hash, math.asint(layer.maxAmp));
            AddHash(ref hash, math.asint(layer.redistributionModifier));
            AddHash(ref hash, math.asint(layer.exponent));
            AddHash(ref hash, math.asint(layer.ridgeFactor));
            AddHash(ref hash, math.asint(layer.domainWarpStrength));
            AddHash(ref hash, math.asint(layer.domainWarpScale));
            AddHash(ref hash, layer.domainWarpOctaves);
            AddHash(ref hash, math.asint(layer.domainWarpGain));
            AddHash(ref hash, math.asint(layer.domainWarpLacunarity));
        }
    }

    private static void AddHash(ref int hash, int value)
    {
        unchecked
        {
            hash = (hash * 31) + value;
        }
    }

    private static bool TryGetReusableSpaghettiCarveMaskCacheEntry(
        Vector2Int coord,
        int expectedSettingsHash,
        int expectedVoxelSizeX,
        int expectedVoxelSizeZ,
        int expectedVoxelPlaneSize,
        int expectedBorderSize,
        out SpaghettiCarveMaskCacheEntry entry)
    {
        if (!spaghettiCarveMaskNeighborCache.TryGetValue(coord, out entry))
            return false;

        int expectedLength = expectedVoxelPlaneSize * expectedVoxelSizeZ;
        bool shapeMismatch =
            entry.voxelSizeX != expectedVoxelSizeX ||
            entry.voxelSizeZ != expectedVoxelSizeZ ||
            entry.voxelPlaneSize != expectedVoxelPlaneSize ||
            entry.borderSize != expectedBorderSize ||
            entry.settingsHash != expectedSettingsHash ||
            !entry.mask.IsCreated ||
            entry.mask.Length != expectedLength;

        if (shapeMismatch)
        {
            RemoveSpaghettiCarveMaskCacheEntry(coord);
            entry = default;
            return false;
        }

        if (!entry.readyHandle.IsCompleted)
            return false;

        entry.readyHandle.Complete();
        entry.lastTouchedFrame = GetSpaghettiCacheTouchStamp();
        spaghettiCarveMaskNeighborCache[coord] = entry;
        return true;
    }

    private static int CopySpaghettiColumnsFromCache(
        in SpaghettiCarveMaskCacheEntry sourceEntry,
        NativeArray<byte> targetMask,
        NativeArray<byte> prefilledColumns,
        int targetVoxelSizeX,
        int targetVoxelPlaneSize,
        int sourceStartX,
        int sourceStartZ,
        int targetStartX,
        int targetStartZ,
        int width,
        int depth)
    {
        if (!sourceEntry.mask.IsCreated ||
            !targetMask.IsCreated ||
            !prefilledColumns.IsCreated ||
            width <= 0 ||
            depth <= 0)
        {
            return 0;
        }

        int filledColumns = 0;
        for (int dz = 0; dz < depth; dz++)
        {
            int sourceZ = sourceStartZ + dz;
            int targetZ = targetStartZ + dz;
            int prefilledRowBase = targetZ * targetVoxelSizeX;

            for (int dx = 0; dx < width; dx++)
            {
                int sourceX = sourceStartX + dx;
                int targetX = targetStartX + dx;
                int prefilledIndex = prefilledRowBase + targetX;
                if (prefilledColumns[prefilledIndex] != 0)
                    continue;

                prefilledColumns[prefilledIndex] = 1;
                filledColumns++;

                int sourceIndex = sourceX + sourceZ * sourceEntry.voxelPlaneSize;
                int targetIndex = targetX + targetZ * targetVoxelPlaneSize;
                for (int y = 0; y < SizeY; y++, sourceIndex += sourceEntry.voxelSizeX, targetIndex += targetVoxelSizeX)
                    targetMask[targetIndex] = sourceEntry.mask[sourceIndex];
            }
        }

        return filledColumns;
    }

    private static int TryCopySpaghettiOverlapFromNeighbor(
        Vector2Int neighborCoord,
        int settingsHash,
        NativeArray<byte> targetMask,
        NativeArray<byte> prefilledColumns,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        int borderSize,
        int sourceStartX,
        int sourceStartZ,
        int targetStartX,
        int targetStartZ,
        int width,
        int depth)
    {
        if (!TryGetReusableSpaghettiCarveMaskCacheEntry(
                neighborCoord,
                settingsHash,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                borderSize,
                out SpaghettiCarveMaskCacheEntry entry))
        {
            return 0;
        }

        return CopySpaghettiColumnsFromCache(
            in entry,
            targetMask,
            prefilledColumns,
            voxelSizeX,
            voxelPlaneSize,
            sourceStartX,
            sourceStartZ,
            targetStartX,
            targetStartZ,
            width,
            depth);
    }

    private static int PrefillSpaghettiCarveMaskFromNeighborCache(
        Vector2Int coord,
        int settingsHash,
        NativeArray<byte> targetMask,
        NativeArray<byte> prefilledColumns,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        int borderSize)
    {
        if (borderSize <= 0 || !targetMask.IsCreated || !prefilledColumns.IsCreated)
            return 0;

        int overlapX = math.min(voxelSizeX, borderSize * 2);
        int overlapZ = math.min(voxelSizeZ, borderSize * 2);
        if (overlapX <= 0 || overlapZ <= 0)
            return 0;

        int filledColumns = 0;
        filledColumns += TryCopySpaghettiOverlapFromNeighbor(
            coord + Vector2Int.left,
            settingsHash,
            targetMask,
            prefilledColumns,
            voxelSizeX,
            voxelSizeZ,
            voxelPlaneSize,
            borderSize,
            voxelSizeX - overlapX,
            0,
            0,
            0,
            overlapX,
            voxelSizeZ);

        filledColumns += TryCopySpaghettiOverlapFromNeighbor(
            coord + Vector2Int.right,
            settingsHash,
            targetMask,
            prefilledColumns,
            voxelSizeX,
            voxelSizeZ,
            voxelPlaneSize,
            borderSize,
            0,
            0,
            voxelSizeX - overlapX,
            0,
            overlapX,
            voxelSizeZ);

        filledColumns += TryCopySpaghettiOverlapFromNeighbor(
            coord + Vector2Int.down,
            settingsHash,
            targetMask,
            prefilledColumns,
            voxelSizeX,
            voxelSizeZ,
            voxelPlaneSize,
            borderSize,
            0,
            voxelSizeZ - overlapZ,
            0,
            0,
            voxelSizeX,
            overlapZ);

        filledColumns += TryCopySpaghettiOverlapFromNeighbor(
            coord + Vector2Int.up,
            settingsHash,
            targetMask,
            prefilledColumns,
            voxelSizeX,
            voxelSizeZ,
            voxelPlaneSize,
            borderSize,
            0,
            0,
            0,
            voxelSizeZ - overlapZ,
            voxelSizeX,
            overlapZ);

        return filledColumns;
    }

    private static void PruneSpaghettiCarveMaskNeighborCache()
    {
        if (spaghettiCarveMaskNeighborCache.Count <= SpaghettiCarveMaskCacheMaxEntries)
            return;

        spaghettiCarveMaskCacheSortBuffer.Clear();
        foreach (KeyValuePair<Vector2Int, SpaghettiCarveMaskCacheEntry> pair in spaghettiCarveMaskNeighborCache)
            spaghettiCarveMaskCacheSortBuffer.Add(new KeyValuePair<Vector2Int, int>(pair.Key, pair.Value.lastTouchedFrame));

        spaghettiCarveMaskCacheSortBuffer.Sort((a, b) => a.Value.CompareTo(b.Value));
        int removeCount = spaghettiCarveMaskNeighborCache.Count - SpaghettiCarveMaskCacheMaxEntries;
        for (int i = 0; i < removeCount && i < spaghettiCarveMaskCacheSortBuffer.Count; i++)
            RemoveSpaghettiCarveMaskCacheEntry(spaghettiCarveMaskCacheSortBuffer[i].Key);

        spaghettiCarveMaskCacheSortBuffer.Clear();
    }

    private static void StoreSpaghettiCarveMaskNeighborCacheEntry(
        Vector2Int coord,
        NativeArray<byte> mask,
        JobHandle readyHandle,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        int borderSize,
        int settingsHash)
    {
        RemoveSpaghettiCarveMaskCacheEntry(coord);

        spaghettiCarveMaskNeighborCache[coord] = new SpaghettiCarveMaskCacheEntry
        {
            mask = mask,
            readyHandle = readyHandle,
            voxelSizeX = voxelSizeX,
            voxelSizeZ = voxelSizeZ,
            voxelPlaneSize = voxelPlaneSize,
            borderSize = borderSize,
            settingsHash = settingsHash,
            lastTouchedFrame = GetSpaghettiCacheTouchStamp()
        };

        PruneSpaghettiCarveMaskNeighborCache();
    }
}
