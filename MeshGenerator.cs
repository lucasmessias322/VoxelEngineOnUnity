using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public enum TreeStyle : byte
{
    OakBroadleaf = 0,
    TaigaSpruce = 1,
    Cactus = 2,
    BirchBroadleaf = 3,
    SavannaAcacia = 4,
    FancyOak = 5
}

public struct TreeInstance
{
    public int worldX;
    public int worldZ;
    public int surfaceY;
    public int trunkHeight;
    public int canopyRadius;
    public int canopyHeight;
    public int trunkClearance;
    public int spacingRadius;
    public TreeStyle treeStyle;
}

public struct TreeSpawnRuleData
{
    public BiomeType biome;
    public TreeStyle treeStyle;
    public TreeSettings settings;
}

public static class TreeGenerationMetrics
{
    public static int GetHorizontalReach(TreeStyle treeStyle, TreeSettings settings)
    {
        return GetHorizontalReach(treeStyle, settings.maxHeight, settings.canopyRadius, settings.canopyHeight);
    }

    public static int GetPlacementSpacingRadius(TreeStyle treeStyle, TreeSettings settings)
    {
        return GetPlacementSpacingRadius(treeStyle, settings.maxHeight, settings.canopyRadius, settings.canopyHeight, settings.minSpacing);
    }

    public static int GetPlacementSpacingRadius(TreeStyle treeStyle, int heightValue, int canopyRadius, int canopyHeight, int minSpacing)
    {
        int spacingFromConfig = math.max(1, (math.max(1, minSpacing) + 1) / 2);
        int horizontalReach = GetHorizontalReach(treeStyle, heightValue, canopyRadius, canopyHeight);
        return math.max(spacingFromConfig, horizontalReach);
    }

    public static int GetHorizontalReach(TreeStyle treeStyle, int heightValue, int canopyRadius, int canopyHeight)
    {
        int safeHeight = math.max(1, heightValue);
        int safeRadius = math.max(0, canopyRadius);
        int safeCanopyHeight = math.max(1, canopyHeight);

        switch (treeStyle)
        {
            case TreeStyle.SavannaAcacia:
                return math.max(safeRadius + 2, 5);

            case TreeStyle.FancyOak:
                return GetFancyOakHorizontalReach(safeHeight, safeRadius, safeCanopyHeight);

            default:
                return safeRadius;
        }
    }

    public static int GetVerticalMargin(TreeStyle treeStyle, TreeSettings settings)
    {
        int safeMaxHeight = math.max(1, settings.maxHeight);
        int safeCanopyHeight = math.max(1, settings.canopyHeight);

        switch (treeStyle)
        {
            case TreeStyle.SavannaAcacia:
                return safeMaxHeight + math.max(4, safeCanopyHeight + 1);

            case TreeStyle.TaigaSpruce:
                return safeMaxHeight + math.max(3, safeCanopyHeight + 1);

            case TreeStyle.FancyOak:
                return safeMaxHeight + math.clamp(math.max(4, safeCanopyHeight), 4, 5) + 2;

            default:
                return safeMaxHeight + safeCanopyHeight + 2;
        }
    }

    private static int GetFancyOakHorizontalReach(int heightLimit, int canopyRadius, int canopyHeight)
    {
        int leafDistanceLimit = math.clamp(math.max(4, canopyHeight), 4, 5);
        float scaleWidth = math.max(1f, canopyRadius / 4f);
        float maxBranchReach = heightLimit * 0.25f * 1.328f * scaleWidth;
        return math.max(canopyRadius + 1, (int)math.ceil(maxBranchReach) + leafDistanceLimit);
    }
}

public struct BlockEdit
{
    public int x;
    public int y;
    public int z;
    public int type;
    public byte placementAxis;
}
public static class MeshGenerator
{
    private const int SizeX = Chunk.SizeX;
    private const int SizeY = Chunk.SizeY;
    private const int SizeZ = Chunk.SizeZ;
    private const float DefaultAOCurveExponent = 1.12f;
    private const int SpaghettiCarveMaskCacheMaxEntries = 24;
    private const int TempGenerationPoolMaxArraysPerSize = 8;

    private const int SubchunksPerColumn = Chunk.SubchunksPerColumn;
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

    public struct SubchunkMeshRange
    {
        public int vertexStart;
        public int vertexCount;
        public int opaqueStart;
        public int opaqueCount;
        public int transparentStart;
        public int transparentCount;
        public int billboardStart;
        public int billboardCount;
        public int waterStart;
        public int waterCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PackedChunkVertex
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector2 uv0;
        public Vector2 uv1;
        public Vector4 uv2;
    }
    // ------------------- Tree Instance -------------------





    // Primeiro job da pipeline: calcula a altura de cada coluna, inclusive o padding
    // usado por costura entre chunks, superficie e sistemas de iluminacao.
    [BurstCompile]
    private struct HeightmapJob : IJobParallelFor
    {
        public Vector2Int coord;

        [ReadOnly] public NativeArray<NoiseLayer> noiseLayers;

        public int baseHeight;
        public float offsetX;
        public float offsetZ;
        public int border;
        public BiomeNoiseSettings biomeNoiseSettings;

        public NativeArray<int> heightCache;
        public int heightStride;

        public void Execute(int i)
        {
            int lx = i % heightStride;
            int lz = i / heightStride;
            int realLx = lx - border;
            int realLz = lz - border;
            int worldX = coord.x * SizeX + realLx;
            int worldZ = coord.y * SizeZ + realLz;
            heightCache[i] = GetSurfaceHeight(worldX, worldZ);
        }

        private int GetSurfaceHeight(int worldX, int worldZ)
        {
            return TerrainHeightSampler.SampleSurfaceHeight(
                worldX,
                worldZ,
                noiseLayers,
                baseHeight,
                offsetX,
                offsetZ,
                SizeY,
                biomeNoiseSettings);
        }
    }



    // Reaproveita o height cache para gerar um contexto semantico por coluna
    // (slope, cliff, bioma e materiais de superficie) uma unica vez.
    [BurstCompile]
    private struct BuildTerrainColumnContextCacheJob : IJobParallelFor
    {
        public Vector2Int coord;
        [ReadOnly] public NativeArray<int> heightCache;
        [WriteOnly] public NativeArray<TerrainColumnContext> columnContexts;

        public int border;
        public float seaLevel;
        public int baseHeight;
        public int CliffTreshold;
        public BiomeNoiseSettings biomeNoiseSettings;

        public void Execute(int index)
        {
            if (!heightCache.IsCreated || !columnContexts.IsCreated)
                return;

            int paddedSize = ResolvePaddedSize(heightCache.Length, SizeX + 2 * border);
            if (paddedSize <= 0)
                return;

            int safeLength = math.min(heightCache.Length, columnContexts.Length);
            if ((uint)index >= (uint)safeLength)
                return;

            int effectiveBorder = math.max(0, (paddedSize - SizeX) / 2);
            int lx = index % paddedSize;
            int lz = index / paddedSize;
            int realLx = lx - effectiveBorder;
            int realLz = lz - effectiveBorder;
            int worldX = coord.x * SizeX + realLx;
            int worldZ = coord.y * SizeZ + realLz;

            int centerIdx = lx + lz * paddedSize;
            if ((uint)centerIdx >= (uint)heightCache.Length)
                return;

            int h = heightCache[centerIdx];
            int hN = SampleHeightSafe(lx, lz + 1, paddedSize, h);
            int hS = SampleHeightSafe(lx, lz - 1, paddedSize, h);
            int hE = SampleHeightSafe(lx + 1, lz, paddedSize, h);
            int hW = SampleHeightSafe(lx - 1, lz, paddedSize, h);
            int hNE = SampleHeightSafe(lx + 1, lz + 1, paddedSize, h);
            int hNW = SampleHeightSafe(lx - 1, lz + 1, paddedSize, h);
            int hSE = SampleHeightSafe(lx + 1, lz - 1, paddedSize, h);
            int hSW = SampleHeightSafe(lx - 1, lz - 1, paddedSize, h);

            columnContexts[index] = TerrainColumnSampler.CreateFromNeighborHeights(
                worldX,
                worldZ,
                h,
                hN,
                hS,
                hE,
                hW,
                hNE,
                hNW,
                hSE,
                hSW,
                CliffTreshold,
                baseHeight,
                seaLevel,
                biomeNoiseSettings);
        }

        private int SampleHeightSafe(int x, int z, int paddedSize, int fallback)
        {
            if (x < 0 || x >= paddedSize || z < 0 || z >= paddedSize)
                return fallback;

            int sampleIndex = x + z * paddedSize;
            if ((uint)sampleIndex >= (uint)heightCache.Length)
                return fallback;

            return heightCache[sampleIndex];
        }

        private static int ResolvePaddedSize(int arrayLength, int preferredSize)
        {
            if (preferredSize > 0 && preferredSize * preferredSize == arrayLength)
                return preferredSize;

            if (arrayLength <= 0)
                return 0;

            int inferredSize = (int)math.round(math.sqrt(arrayLength));
            if (inferredSize > 0 && inferredSize * inferredSize == arrayLength)
                return inferredSize;

            return 0;
        }
    }

    // Pipeline de base do terreno em 3 etapas lock-free:
    // 1) sample/classificacao de ruido de densidade por coluna;
    // 2) decisao final de solido/vazio (inclui amostragem exata quando necessario);
    // 3) pos-processamento de blocos base (bedrock/stone/deepslate).
    [BurstCompile]
    private struct SampleTerrainDensityClassificationJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> heightCache;
        // Layout SoA cache-friendly: [columnIndex * SizeY + y]
        [NativeDisableParallelForRestriction] public NativeArray<byte> densityClassifications;
        [NativeDisableParallelForRestriction] public NativeArray<TerrainDensitySettings> resolvedDensitySettingsByColumn;

        public Vector2Int coord;
        public int border;
        public BiomeNoiseSettings biomeNoiseSettings;
        public TerrainDensitySettings terrainDensitySettings;

        public void Execute(int index)
        {
            int paddedSize = SizeX + 2 * border;
            int lx = index % paddedSize;
            int lz = index / paddedSize;
            int baseSurfaceHeight = math.clamp(heightCache[index], 1, SizeY - 1);
            int columnBase = index * SizeY;

            int worldX = coord.x * SizeX + (lx - border);
            int worldZ = coord.y * SizeZ + (lz - border);
            TerrainDensitySettings resolvedDensitySettings = TerrainDensitySampler.ResolveBiomeDensitySettings(
                worldX,
                worldZ,
                terrainDensitySettings,
                biomeNoiseSettings);
            resolvedDensitySettingsByColumn[index] = resolvedDensitySettings;

            for (int y = 0; y <= 2; y++)
                densityClassifications[columnBase + y] = (byte)TerrainDensityClassification.Solid;

            if (!resolvedDensitySettings.enabled)
            {
                for (int y = 3; y < SizeY; y++)
                {
                    densityClassifications[columnBase + y] = (byte)(y <= baseSurfaceHeight
                        ? TerrainDensityClassification.Solid
                        : TerrainDensityClassification.Air);
                }

                return;
            }

            int guaranteedSolidY = math.min(SizeY - 1, TerrainDensitySampler.GetGuaranteedSolidY(baseSurfaceHeight, resolvedDensitySettings));
            int densityTopY = TerrainDensitySampler.GetDensityBandTopY(baseSurfaceHeight, SizeY, resolvedDensitySettings);

            for (int y = 3; y <= guaranteedSolidY; y++)
                densityClassifications[columnBase + y] = (byte)TerrainDensityClassification.Solid;

            for (int y = guaranteedSolidY + 1; y <= densityTopY; y++)
                densityClassifications[columnBase + y] = (byte)TerrainDensitySampler.ClassifyDensityWithoutNoise(y, baseSurfaceHeight, resolvedDensitySettings);

            for (int y = densityTopY + 1; y < SizeY; y++)
                densityClassifications[columnBase + y] = (byte)TerrainDensityClassification.Air;
        }
    }

    [BurstCompile]
    private struct ResolveTerrainSolidStateJob : IJobParallelFor
    {
        [NativeDisableParallelForRestriction] public NativeArray<int> heightCache;
        [ReadOnly] public NativeArray<byte> densityClassifications;
        [ReadOnly] public NativeArray<TerrainDensitySettings> resolvedDensitySettingsByColumn;
        // Layout SoA cache-friendly: [columnIndex * SizeY + y]
        [NativeDisableParallelForRestriction] public NativeArray<byte> solidMaskByColumn;

        public Vector2Int coord;
        public int border;
        public float offsetX;
        public float offsetZ;

        public void Execute(int index)
        {
            int paddedSize = SizeX + 2 * border;
            int lx = index % paddedSize;
            int lz = index / paddedSize;
            int baseSurfaceHeight = math.clamp(heightCache[index], 1, SizeY - 1);
            TerrainDensitySettings resolvedDensitySettings = resolvedDensitySettingsByColumn[index];
            int columnBase = index * SizeY;

            int worldX = coord.x * SizeX + (lx - border);
            int worldZ = coord.y * SizeZ + (lz - border);
            int highestSolidY = 2;
            for (int y = 0; y < SizeY; y++)
            {
                TerrainDensityClassification classification = (TerrainDensityClassification)densityClassifications[columnBase + y];
                bool isSolid = classification == TerrainDensityClassification.Solid;

                if (!isSolid && classification == TerrainDensityClassification.RequiresExactSample)
                {
                    float density = TerrainDensitySampler.SampleTerrainDensity(
                        worldX,
                        y,
                        worldZ,
                        baseSurfaceHeight,
                        offsetX,
                        offsetZ,
                        resolvedDensitySettings);
                    isSolid = density > resolvedDensitySettings.solidThreshold;
                }

                solidMaskByColumn[columnBase + y] = (byte)(isSolid ? 1 : 0);
                if (isSolid)
                    highestSolidY = y;
            }

            heightCache[index] = math.max(2, highestSolidY);
        }
    }

    [BurstCompile]
    private struct PostProcessTerrainSolidBlocksJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> heightCache;
        [ReadOnly] public NativeArray<byte> solidMaskByColumn;
        [NativeDisableParallelForRestriction] public NativeArray<bool> solids;
        [NativeDisableParallelForRestriction] public NativeArray<byte> blockTypes;

        public int border;

        public void Execute(int index)
        {
            int paddedSize = SizeX + 2 * border;
            int lx = index % paddedSize;
            int lz = index / paddedSize;
            int voxelSizeX = paddedSize;
            int voxelPlaneSize = voxelSizeX * SizeY;
            int highestSolidY = math.max(2, heightCache[index]);
            int columnBase = index * SizeY;

            int voxelIndex = lx + lz * voxelPlaneSize;
            for (int y = 0; y <= 2; y++, voxelIndex += voxelSizeX)
            {
                solids[voxelIndex] = true;
                blockTypes[voxelIndex] = (byte)BlockType.Bedrock;
            }

            for (int y = 3; y < SizeY; y++, voxelIndex += voxelSizeX)
            {
                bool isSolid = solidMaskByColumn[columnBase + y] != 0;
                solids[voxelIndex] = isSolid;
                if (!isSolid)
                {
                    blockTypes[voxelIndex] = (byte)BlockType.Air;
                    continue;
                }

                blockTypes[voxelIndex] = (byte)(y > highestSolidY - TerrainSurfaceRules.StoneTransitionDepth
                    ? BlockType.Stone
                    : BlockType.Deepslate);
            }
        }
    }

    [BurstCompile]
    private struct ApplySurfaceMaterialsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<TerrainColumnContext> columnContexts;
        [ReadOnly] public NativeArray<bool> solids;
        [NativeDisableParallelForRestriction] public NativeArray<byte> blockTypes;

        public int border;

        public void Execute(int index)
        {
            int paddedSize = SizeX + 2 * border;
            int lx = index % paddedSize;
            int lz = index / paddedSize;
            TerrainSurfaceData surface = columnContexts[index].surface;
            int surfaceY = math.min(surface.surfaceHeight, SizeY - 1);
            if (surfaceY <= 2)
                return;

            int voxelSizeX = SizeX + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;
            int maxSurfaceDepth = math.max(1, surface.surfaceLayerDepth);
            int solidLayersPainted = 0;

            for (int y = surfaceY; y >= 3 && solidLayersPainted < maxSurfaceDepth; y--)
            {
                int voxelIndex = lx + y * voxelSizeX + lz * voxelPlaneSize;
                if (!solids[voxelIndex])
                    continue;

                blockTypes[voxelIndex] = (byte)(solidLayersPainted == 0 ? surface.surfaceBlock : surface.subsurfaceBlock);
                solidLayersPainted++;
            }
        }
    }

    [BurstCompile]
    private struct PopulateLightOpacityJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> heightCache;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<byte> opacity;
        [ReadOnly] public NativeArray<byte> effectiveOpacityByBlock;

        public Vector2Int coord;
        public int border;
        public float offsetX;
        public float offsetZ;
        public BiomeNoiseSettings biomeNoiseSettings;
        public TerrainDensitySettings terrainDensitySettings;
        public int skipInnerMin;
        public int skipInnerMaxExclusive;

        public void Execute(int index)
        {
            int paddedSize = SizeX + 2 * border;
            int lx = index % paddedSize;
            int lz = index / paddedSize;

            if (skipInnerMaxExclusive > skipInnerMin
                && lx >= skipInnerMin && lx < skipInnerMaxExclusive
                && lz >= skipInnerMin && lz < skipInnerMaxExclusive)
            {
                return;
            }

            int baseSurfaceHeight = math.clamp(heightCache[index], 1, SizeY - 1);
            int worldX = coord.x * SizeX + (lx - border);
            int worldZ = coord.y * SizeZ + (lz - border);
            TerrainDensitySettings resolvedDensitySettings = TerrainDensitySampler.ResolveBiomeDensitySettings(
                worldX,
                worldZ,
                terrainDensitySettings,
                biomeNoiseSettings);

            int voxelSizeX = SizeX + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;
            byte solidOpacity = effectiveOpacityByBlock[(int)BlockType.Stone];
            byte airOpacity = effectiveOpacityByBlock[(int)BlockType.Air];

            int voxelIndex = lx + lz * voxelPlaneSize;
            for (int y = 0; y <= 2; y++, voxelIndex += voxelSizeX)
                opacity[voxelIndex] = solidOpacity;

            if (!resolvedDensitySettings.enabled)
            {
                for (int y = 3; y < SizeY; y++, voxelIndex += voxelSizeX)
                    opacity[voxelIndex] = y <= baseSurfaceHeight ? solidOpacity : airOpacity;

                return;
            }

            int guaranteedSolidY = math.min(SizeY - 1, TerrainDensitySampler.GetGuaranteedSolidY(baseSurfaceHeight, resolvedDensitySettings));
            int densityTopY = TerrainDensitySampler.GetDensityBandTopY(baseSurfaceHeight, SizeY, resolvedDensitySettings);
            int sampleStep = math.max(1, resolvedDensitySettings.verticalSampleStep);

            int fillIndex = lx + 3 * voxelSizeX + lz * voxelPlaneSize;
            for (int y = 3; y <= guaranteedSolidY; y++, fillIndex += voxelSizeX)
                opacity[fillIndex] = solidOpacity;

            int sampleStartY = math.max(guaranteedSolidY + 1, 3);
            if (sampleStartY <= densityTopY)
            {
                int previousY = sampleStartY;

                while (previousY <= densityTopY)
                {
                    int nextY = math.min(densityTopY, previousY + sampleStep);
                    int sampleIndex = lx + previousY * voxelSizeX + lz * voxelPlaneSize;
                    bool needsExactSampling = false;
                    for (int y = previousY; y <= nextY; y++, sampleIndex += voxelSizeX)
                    {
                        TerrainDensityClassification classification = TerrainDensitySampler.ClassifyDensityWithoutNoise(y, baseSurfaceHeight, resolvedDensitySettings);
                        if (classification == TerrainDensityClassification.Solid)
                        {
                            opacity[sampleIndex] = solidOpacity;
                            continue;
                        }

                        if (classification == TerrainDensityClassification.Air)
                        {
                            opacity[sampleIndex] = airOpacity;
                            continue;
                        }

                        needsExactSampling = true;
                    }

                    if (needsExactSampling)
                    {
                        sampleIndex = lx + previousY * voxelSizeX + lz * voxelPlaneSize;
                        for (int y = previousY; y <= nextY; y++, sampleIndex += voxelSizeX)
                        {
                            if (TerrainDensitySampler.ClassifyDensityWithoutNoise(y, baseSurfaceHeight, resolvedDensitySettings) != TerrainDensityClassification.RequiresExactSample)
                                continue;

                            float density = TerrainDensitySampler.SampleTerrainDensity(
                                worldX,
                                y,
                                worldZ,
                                baseSurfaceHeight,
                                offsetX,
                                offsetZ,
                                resolvedDensitySettings);
                            opacity[sampleIndex] = density > resolvedDensitySettings.solidThreshold ? solidOpacity : airOpacity;
                        }
                    }

                    if (nextY == densityTopY)
                        break;

                    previousY = nextY;
                }
            }
            int airStartY = math.max(densityTopY + 1, 3);
            if (airStartY >= SizeY)
                return;

            voxelIndex = lx + airStartY * voxelSizeX + lz * voxelPlaneSize;
            for (int y = airStartY; y < SizeY; y++, voxelIndex += voxelSizeX)
                opacity[voxelIndex] = airOpacity;
        }
    }

    [BurstCompile]
    private struct CopyGeneratedOpacityToLightVolumeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> sourceBlockTypes;
        [ReadOnly] public NativeArray<byte> effectiveOpacityByBlock;
        [NativeDisableParallelForRestriction] public NativeArray<byte> targetOpacity;

        public int sourceVoxelSizeX;
        public int targetVoxelSizeX;
        public int targetVoxelPlaneSize;
        public int sourceBorder;
        public int targetBorder;

        public void Execute(int index)
        {
            int x = index % sourceVoxelSizeX;
            int temp = index / sourceVoxelSizeX;
            int y = temp % SizeY;
            int z = temp / SizeY;

            int targetX = x + (targetBorder - sourceBorder);
            int targetZ = z + (targetBorder - sourceBorder);
            int targetIndex = targetX + y * targetVoxelSizeX + targetZ * targetVoxelPlaneSize;
            targetOpacity[targetIndex] = effectiveOpacityByBlock[(int)sourceBlockTypes[index]];
        }
    }

    [BurstCompile]
    private struct ApplyOpacityOverridesJob : IJob
    {
        [ReadOnly] public NativeArray<BlockEdit> overrides;
        [ReadOnly] public NativeArray<byte> effectiveOpacityByBlock;
        public NativeArray<byte> opacity;

        public int chunkMinX;
        public int chunkMinZ;
        public int borderSize;
        public int voxelSizeX;
        public int voxelSizeZ;
        public int voxelPlaneSize;

        public void Execute()
        {
            for (int index = 0; index < overrides.Length; index++)
            {
                BlockEdit edit = overrides[index];
                if (edit.y < 0 || edit.y >= SizeY)
                    continue;
                if (edit.type < 0 || edit.type >= effectiveOpacityByBlock.Length)
                    continue;

                int ix = edit.x - chunkMinX + borderSize;
                int iz = edit.z - chunkMinZ + borderSize;
                if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                    continue;

                int dstIndex = ix + edit.y * voxelSizeX + iz * voxelPlaneSize;
                opacity[dstIndex] = effectiveOpacityByBlock[edit.type];
            }
        }
    }

    [BurstCompile]
    private struct BuildSpaghettiCaveCarveMaskJob : IJob
    {
        [ReadOnly] public NativeArray<int> heightCache;
        [NativeDisableParallelForRestriction] public NativeArray<byte> carveMask;
        [ReadOnly] public NativeArray<byte> prefilledColumns;
        public int prefilledColumnsStride;

        public Vector2Int coord;
        public int borderSize;
        public int oreSeed;
        public SpaghettiCaveSettings spaghettiCaveSettings;

        public void Execute()
        {
            LightOpacitySpaghettiCaveUtility.BuildCarveMask(
                coord,
                heightCache,
                carveMask,
                prefilledColumns,
                prefilledColumnsStride,
                borderSize,
                oreSeed,
                spaghettiCaveSettings);
        }
    }

    [BurstCompile]
    private struct ApplySpaghettiCaveCarveMaskToOpacityJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> carveMask;
        [NativeDisableParallelForRestriction] public NativeArray<byte> opacity;
        public byte airOpacity;

        public void Execute(int index)
        {
            if (carveMask[index] != 0)
                opacity[index] = airOpacity;
        }
    }

    [BurstCompile]
    private struct CopyBoolArrayJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<bool> source;
        [NativeDisableParallelForRestriction] public NativeArray<bool> destination;

        public void Execute(int index)
        {
            destination[index] = source[index];
        }
    }

    [BurstCompile]
    private struct CopyByteArrayJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> source;
        [NativeDisableParallelForRestriction] public NativeArray<byte> destination;

        public void Execute(int index)
        {
            destination[index] = source[index];
        }
    }

    [BurstCompile]
    private struct DisposeIntArrayJob : IJob
    {
        [DeallocateOnJobCompletion] public NativeArray<int> values;
        public void Execute() { }
    }

    [BurstCompile]
    private struct DisposeByteArrayJob : IJob
    {
        [DeallocateOnJobCompletion] public NativeArray<byte> values;
        public void Execute() { }
    }

    [BurstCompile]
    private struct BuildChunkSnapshotAndFlagsJob : IJob
    {
        [ReadOnly] public NativeArray<byte> blockTypes;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [WriteOnly] public NativeArray<byte> voxelSnapshot;
        public NativeArray<bool> subchunkNonEmpty;
        public NativeArray<ulong> subchunkColliderOccupancy;
        public int borderSize;

        public void Execute()
        {
            for (int s = 0; s < SubchunksPerColumn; s++)
                subchunkNonEmpty[s] = false;

            for (int i = 0; i < subchunkColliderOccupancy.Length; i++)
                subchunkColliderOccupancy[i] = 0UL;

            int voxelSizeX = SizeX + 2 * borderSize;
            int voxelPlaneSize = voxelSizeX * SizeY;
            int dstIndex = 0;

            for (int y = 0; y < SizeY; y++)
            {
                int subchunkIndex = y / Chunk.SubchunkHeight;
                for (int z = 0; z < SizeZ; z++)
                {
                    int srcBase = (z + borderSize) * voxelPlaneSize + y * voxelSizeX + borderSize;
                    for (int x = 0; x < SizeX; x++, dstIndex++)
                    {
                        BlockType blockType = (BlockType)blockTypes[srcBase + x];
                        voxelSnapshot[dstIndex] = (byte)blockType;
                        if (blockType != BlockType.Air)
                            subchunkNonEmpty[subchunkIndex] = true;

                        if (!IsBlockCollidable(blockType))
                            continue;

                        int localY = y - subchunkIndex * Chunk.SubchunkHeight;
                        int localIndex = x + localY * Chunk.SizeX + z * Chunk.SizeX * Chunk.SubchunkHeight;
                        int wordIndex = subchunkIndex * Chunk.ColliderOccupancyWordsPerSubchunk + (localIndex >> 6);
                        subchunkColliderOccupancy[wordIndex] |= 1UL << (localIndex & 63);
                    }
                }
            }
        }

        private bool IsBlockCollidable(BlockType blockType)
        {
            if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
                return false;

            if (TorchPlacementUtility.IsTorchLike(blockType))
                return false;

            int mapIndex = (int)blockType;
            if (mapIndex < 0 || mapIndex >= blockMappings.Length)
                return false;

            BlockTextureMapping mapping = blockMappings[mapIndex];
            return mapping.isSolid && !mapping.isEmpty;
        }
    }

    public static void ScheduleDataJob(
        Vector2Int coord,
        NativeArray<NoiseLayer> noiseLayers,
        NativeArray<BlockTextureMapping> blockMappings,
        NativeArray<byte> effectiveOpacityByBlock,
        int baseHeight,
        float globalOffsetX,
        float globalOffsetZ,
        float seaLevel,
        bool enableWater,
        BiomeNoiseSettings biomeNoiseSettings,
        TerrainDensitySettings terrainDensitySettings,
        int oreSeed,


        NativeArray<BlockEdit> blockEdits,

        int treeMargin,
        int dataBorderSize,
        int lightBorderSize,
        int detailGenerationBorder,
        int maxTreeRadius,
        int CliffTreshold,
        bool enableTrees,
        NativeArray<OreSpawnSettings> oreSettings,
        NativeArray<TreeSpawnRuleData> treeSpawnRules,
        SpaghettiCaveSettings spaghettiCaveSettings,
        bool enableVoxelLighting,
        bool enableHorizontalSkylight,
        int horizontalSkylightStepLoss,
        NativeArray<byte> lightData,
        NativeArray<byte> chunkVoxelSnapshot,
        out JobHandle dataHandle,
        out NativeArray<int> heightCache,
        out NativeArray<byte> blockTypes,
        out NativeArray<bool> solids,
        out NativeArray<byte> light,
        out NativeArray<byte> lightOpacityData,
        out NativeArray<bool> subchunkNonEmpty,
        out NativeArray<ulong> subchunkColliderOccupancy,
        out DataJobTempBuffers tempBuffers
    )
    {
        // A pipeline trabalha com dois volumes padded:
        // um para dados do terreno e outro para opacidade/luz, que pode pedir mais borda.
        // 1. Fixar o borderSize em 1 (PadrÃƒÆ’Ã‚Â£o para Ambient Occlusion e Costura)

        // 2. AlocaÃƒÆ’Ã‚Â§ÃƒÆ’Ã‚Âµes dos Arrays IntermÃƒÆ’Ã‚Â©dios que fluem entre os Jobs (TempJob)
        // Em cenas pesadas essa chain pode durar mais de 4 frames, entÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â£o os buffers
        // intermediÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¡rios abaixo nÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â£o podem usar TempJob.
        lightBorderSize = math.max(lightBorderSize, dataBorderSize);
        subchunkNonEmpty = new NativeArray<bool>(SubchunksPerColumn, Allocator.Persistent);
        subchunkColliderOccupancy = new NativeArray<ulong>(
            SubchunksPerColumn * Chunk.ColliderOccupancyWordsPerSubchunk,
            Allocator.Persistent,
            NativeArrayOptions.ClearMemory);

        int dataHeightSize = SizeX + 2 * dataBorderSize;
        int dataTotalHeightPoints = dataHeightSize * dataHeightSize;
        int dataVoxelSizeX = SizeX + 2 * dataBorderSize;
        int dataVoxelSizeZ = SizeZ + 2 * dataBorderSize;
        int dataVoxelPlaneSize = dataVoxelSizeX * SizeY;
        int dataTotalVoxels = dataVoxelSizeX * SizeY * dataVoxelSizeZ;

        int lightHeightSize = SizeX + 2 * lightBorderSize;
        int lightTotalHeightPoints = lightHeightSize * lightHeightSize;
        int lightVoxelSizeX = SizeX + 2 * lightBorderSize;
        int lightVoxelSizeZ = SizeZ + 2 * lightBorderSize;
        int lightVoxelPlaneSize = lightVoxelSizeX * SizeY;
        int lightTotalVoxels = lightVoxelSizeX * SizeY * lightVoxelSizeZ;

        int borderSize = dataBorderSize;
        int heightSize = dataHeightSize;
        int totalHeightPoints = dataTotalHeightPoints;
        int voxelSizeX = dataVoxelSizeX;
        int voxelSizeZ = dataVoxelSizeZ;
        int voxelPlaneSize = dataVoxelPlaneSize;
        int totalVoxels = dataTotalVoxels;

        heightCache = new NativeArray<int>(dataTotalHeightPoints, Allocator.Persistent);
        blockTypes = new NativeArray<byte>(dataTotalVoxels, Allocator.Persistent);
        solids = new NativeArray<bool>(dataTotalVoxels, Allocator.Persistent);
        NativeArray<bool> baseTerrainSolids = new NativeArray<bool>(dataTotalVoxels, Allocator.Persistent);
        light = new NativeArray<byte>(dataTotalVoxels, Allocator.Persistent);
        lightOpacityData = default;
        NativeArray<byte> sharedSpaghettiCarveMask = new NativeArray<byte>(0, Allocator.Persistent);
        tempBuffers = RentDataJobTempBuffers(dataTotalVoxels, dataTotalHeightPoints);
        NativeArray<byte> densityClassifications = tempBuffers.densityClassifications;
        NativeArray<TerrainDensitySettings> resolvedDensitySettingsByColumn = tempBuffers.resolvedDensitySettingsByColumn;
        NativeArray<byte> terrainSolidMaskByColumn = tempBuffers.terrainSolidMaskByColumn;

        // ==========================================
        // JOB 0: GeraÃƒÆ’Ã‚Â§ÃƒÆ’Ã‚Â£o do Heightmap (Paralelo)
        // ==========================================
        var heightJob = new HeightmapJob
        {
            coord = coord,
            noiseLayers = noiseLayers,
            baseHeight = baseHeight,
            offsetX = globalOffsetX,
            offsetZ = globalOffsetZ,
            border = dataBorderSize,
            biomeNoiseSettings = biomeNoiseSettings,
            heightCache = heightCache,
            heightStride = dataHeightSize
        };
        JobHandle heightHandle = heightJob.Schedule(totalHeightPoints, 32); // Batch size 64 para paralelismo (ajuste se necessÃƒÆ’Ã‚Â¡rio)
        // ==========================================
        // JOB 1a: Sample/classificacao de densidade (PARALELO)
        // ==========================================
        var sampleTerrainDensityJob = new SampleTerrainDensityClassificationJob
        {
            coord = coord,
            heightCache = heightCache,
            densityClassifications = densityClassifications,
            resolvedDensitySettingsByColumn = resolvedDensitySettingsByColumn,
            border = borderSize,
            biomeNoiseSettings = biomeNoiseSettings,
            terrainDensitySettings = terrainDensitySettings
        };

        int paddedSize = SizeX + 2 * borderSize;
        int totalColumns = paddedSize * paddedSize;
        JobHandle sampleTerrainDensityHandle = sampleTerrainDensityJob.Schedule(totalColumns, 32, heightHandle);

        var resolveTerrainSolidStateJob = new ResolveTerrainSolidStateJob
        {
            coord = coord,
            heightCache = heightCache,
            densityClassifications = densityClassifications,
            resolvedDensitySettingsByColumn = resolvedDensitySettingsByColumn,
            solidMaskByColumn = terrainSolidMaskByColumn,
            border = borderSize,
            offsetX = globalOffsetX,
            offsetZ = globalOffsetZ
        };
        JobHandle resolveTerrainSolidHandle = resolveTerrainSolidStateJob.Schedule(totalColumns, 32, sampleTerrainDensityHandle);

        var postProcessTerrainSolidBlocksJob = new PostProcessTerrainSolidBlocksJob
        {
            heightCache = heightCache,
            solidMaskByColumn = terrainSolidMaskByColumn,
            solids = solids,
            blockTypes = blockTypes,
            border = borderSize
        };
        JobHandle postProcessTerrainSolidBlocksHandle = postProcessTerrainSolidBlocksJob.Schedule(totalColumns, 32, resolveTerrainSolidHandle);

        NativeArray<TerrainColumnContext> dataColumnContexts = tempBuffers.dataColumnContexts;
        var buildDataColumnContextCacheJob = new BuildTerrainColumnContextCacheJob
        {
            coord = coord,
            heightCache = heightCache,
            columnContexts = dataColumnContexts,
            border = dataBorderSize,
            seaLevel = seaLevel,
            baseHeight = baseHeight,
            CliffTreshold = CliffTreshold,
            biomeNoiseSettings = biomeNoiseSettings
        };
        JobHandle dataColumnContextHandle = buildDataColumnContextCacheJob.Schedule(dataTotalHeightPoints, 32, resolveTerrainSolidHandle);

        var applySurfaceMaterialsJob = new ApplySurfaceMaterialsJob
        {
            columnContexts = dataColumnContexts,
            solids = solids,
            blockTypes = blockTypes,
            border = borderSize
        };
        JobHandle surfaceMaterialHandle = applySurfaceMaterialsJob.Schedule(
            totalColumns,
            32,
            JobHandle.CombineDependencies(dataColumnContextHandle, postProcessTerrainSolidBlocksHandle));
        var baseChunkDataJob = new ChunkData.ChunkDataJob
        {
            coord = coord,
            noiseLayers = noiseLayers,
            blockMappings = blockMappings,
            blockEdits = blockEdits,


            baseHeight = baseHeight,
            offsetX = globalOffsetX,
            offsetZ = globalOffsetZ,
            seaLevel = seaLevel,
            biomeNoiseSettings = biomeNoiseSettings,
            terrainDensitySettings = terrainDensitySettings,


            treeMargin = treeMargin,
            border = borderSize,
            detailBorder = math.min(detailGenerationBorder, borderSize),
            maxTreeRadius = maxTreeRadius,
            CliffTreshold = CliffTreshold,

            heightCache = heightCache,
            blockTypes = blockTypes,
            solids = solids,
            treeSpawnRules = treeSpawnRules,
            oreSettings = oreSettings,
            oreSeed = oreSeed,
            spaghettiCaveSettings = spaghettiCaveSettings,

            enableTrees = enableTrees,
            columnContextCache = dataColumnContexts,
            columnContextCacheStride = dataHeightSize,
            spaghettiCarveMask = sharedSpaghettiCarveMask,
            spaghettiCarveMaskVoxelSizeX = 0,
            spaghettiCarveMaskVoxelPlaneSize = 0,
            spaghettiCarveMaskOffsetX = 0,
            spaghettiCarveMaskOffsetZ = 0,
            stages = ChunkData.ChunkDataStageFlags.None
        };
        var copyBaseTerrainSolidsJob = new CopyBoolArrayJob
        {
            source = solids,
            destination = baseTerrainSolids
        };
        JobHandle copyBaseTerrainSolidsHandle = copyBaseTerrainSolidsJob.Schedule(dataTotalVoxels, 128, postProcessTerrainSolidBlocksHandle);
        // JobHandle chunkDataHandle = chunkDataJob.Schedule(heightHandle); // DependÃƒÆ’Ã‚Âªncia no heightHandle
        // A pipeline de densidade ja escreveu o terreno base. A partir daqui encadeamos
        // apenas estagios mutaveis: cavernas, minerios, agua, arvores e block edits.
        JobHandle caveChunkDataHandle;
        JobHandle oreChunkDataHandle;
        JobHandle waterChunkDataHandle;
        JobHandle treeChunkDataHandle;
        JobHandle finalChunkDataHandle;
        if (!enableVoxelLighting)
        {
            // Caminho mais barato: sem voxel lighting nao precisamos do segundo volume de opacidade.
            var caveChunkDataJob = baseChunkDataJob;
            caveChunkDataJob.stages = ChunkData.ChunkDataStageFlags.Caves;
            caveChunkDataHandle = caveChunkDataJob.Schedule(
                JobHandle.CombineDependencies(surfaceMaterialHandle, copyBaseTerrainSolidsHandle));

            var oreChunkDataJob = baseChunkDataJob;
            oreChunkDataJob.stages = ChunkData.ChunkDataStageFlags.Ores;
            oreChunkDataHandle = oreChunkDataJob.Schedule(caveChunkDataHandle);

            if (enableWater)
            {
                var fillWaterBelowSeaLevelJob = new ChunkData.FillTerrainVoidWaterBelowSeaLevelJob
                {
                    baseSolids = baseTerrainSolids,
                    blockTypes = blockTypes,
                    solids = solids,
                    border = dataBorderSize,
                    seaLevel = math.min(SizeY - 1, (int)math.floor(seaLevel)),
                    waterBlockId = (byte)BlockType.Water,
                    waterIsSolid = blockMappings[(int)BlockType.Water].isSolid
                };
                waterChunkDataHandle = fillWaterBelowSeaLevelJob.Schedule(
                    dataTotalHeightPoints,
                    32,
                    JobHandle.CombineDependencies(oreChunkDataHandle, copyBaseTerrainSolidsHandle));
            }
            else
            {
                waterChunkDataHandle = JobHandle.CombineDependencies(oreChunkDataHandle, copyBaseTerrainSolidsHandle);
            }

            treeChunkDataHandle = waterChunkDataHandle;
            if (enableTrees)
            {
                var treeChunkDataJob = baseChunkDataJob;
                treeChunkDataJob.stages = ChunkData.ChunkDataStageFlags.Trees;
                treeChunkDataHandle = treeChunkDataJob.Schedule(waterChunkDataHandle);
            }

            finalChunkDataHandle = treeChunkDataHandle;
            if (blockEdits.IsCreated && blockEdits.Length > 0)
            {
                var blockEditChunkDataJob = baseChunkDataJob;
                blockEditChunkDataJob.stages = ChunkData.ChunkDataStageFlags.BlockEdits;
                finalChunkDataHandle = blockEditChunkDataJob.Schedule(treeChunkDataHandle);
            }

            JobHandle disposeBaseTerrainSolidsHandle = baseTerrainSolids.Dispose(finalChunkDataHandle);

            var snapshotJob = new BuildChunkSnapshotAndFlagsJob
            {
                blockTypes = blockTypes,
                blockMappings = blockMappings,
                voxelSnapshot = chunkVoxelSnapshot,
                subchunkNonEmpty = subchunkNonEmpty,
                subchunkColliderOccupancy = subchunkColliderOccupancy,
                borderSize = dataBorderSize
            };
            JobHandle snapshotHandle = snapshotJob.Schedule(finalChunkDataHandle);
            byte fullBright = LightUtils.PackLight(15, 0);
            for (int i = 0; i < light.Length; i++)
                light[i] = fullBright;

            var disposeEmptySpaghettiCarveMaskJob = new DisposeByteArrayJob
            {
                values = sharedSpaghettiCarveMask
            };
            JobHandle disposeEmptySpaghettiCarveMaskHandle = disposeEmptySpaghettiCarveMaskJob.Schedule(finalChunkDataHandle);

            dataHandle = snapshotHandle;
            dataHandle = JobHandle.CombineDependencies(dataHandle, disposeEmptySpaghettiCarveMaskHandle);
            dataHandle = JobHandle.CombineDependencies(dataHandle, disposeBaseTerrainSolidsHandle);
            return;
        }

        lightOpacityData = new NativeArray<byte>(lightTotalVoxels, Allocator.Persistent);
        NativeArray<int> lightHeightCache = new NativeArray<int>(lightTotalHeightPoints, Allocator.Persistent);
        var lightHeightJob = new HeightmapJob
        {
            coord = coord,
            noiseLayers = noiseLayers,
            baseHeight = baseHeight,
            offsetX = globalOffsetX,
            offsetZ = globalOffsetZ,
            border = lightBorderSize,
            biomeNoiseSettings = biomeNoiseSettings,
            heightCache = lightHeightCache,
            heightStride = lightHeightSize
        };
        JobHandle lightHeightHandle = lightHeightJob.Schedule(lightTotalHeightPoints, 32);
        bool useSharedSpaghettiCarveMask = LightOpacitySpaghettiCaveUtility.ShouldApply(
            dataBorderSize,
            lightBorderSize,
            spaghettiCaveSettings);
        JobHandle spaghettiCarveMaskHandle = default;
        JobHandle disposePrefilledSpaghettiColumnsHandle = default;
        JobHandle spaghettiMaskCacheStoreHandle = default;
        if (useSharedSpaghettiCarveMask)
        {
            // A mesma mascara e compartilhada entre terreno e opacidade para que
            // cavernas e iluminacao concordem exatamente nas bordas.
            sharedSpaghettiCarveMask.Dispose();
            sharedSpaghettiCarveMask = new NativeArray<byte>(lightTotalVoxels, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            int spaghettiSettingsHash = ComputeSpaghettiCarveMaskSettingsHash(oreSeed, lightBorderSize, spaghettiCaveSettings);
            NativeArray<byte> prefilledSpaghettiColumns = new NativeArray<byte>(lightVoxelSizeX * lightVoxelSizeZ, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            int prefilledColumnCount = PrefillSpaghettiCarveMaskFromNeighborCache(
                coord,
                spaghettiSettingsHash,
                sharedSpaghettiCarveMask,
                prefilledSpaghettiColumns,
                lightVoxelSizeX,
                lightVoxelSizeZ,
                lightVoxelPlaneSize,
                lightBorderSize);

            var buildSpaghettiCaveCarveMaskJob = new BuildSpaghettiCaveCarveMaskJob
            {
                coord = coord,
                heightCache = lightHeightCache,
                carveMask = sharedSpaghettiCarveMask,
                prefilledColumns = prefilledSpaghettiColumns,
                prefilledColumnsStride = lightVoxelSizeX,
                borderSize = lightBorderSize,
                oreSeed = oreSeed,
                spaghettiCaveSettings = spaghettiCaveSettings
            };
            spaghettiCarveMaskHandle = prefilledColumnCount < prefilledSpaghettiColumns.Length
                ? buildSpaghettiCaveCarveMaskJob.Schedule(lightHeightHandle)
                : lightHeightHandle;

            var disposePrefilledSpaghettiColumnsJob = new DisposeByteArrayJob
            {
                values = prefilledSpaghettiColumns
            };
            disposePrefilledSpaghettiColumnsHandle = disposePrefilledSpaghettiColumnsJob.Schedule(spaghettiCarveMaskHandle);

            NativeArray<byte> cachedMaskCopy = new NativeArray<byte>(lightTotalVoxels, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var copySpaghettiMaskToCacheJob = new CopyByteArrayJob
            {
                source = sharedSpaghettiCarveMask,
                destination = cachedMaskCopy
            };
            spaghettiMaskCacheStoreHandle = copySpaghettiMaskToCacheJob.Schedule(lightTotalVoxels, 128, spaghettiCarveMaskHandle);
            StoreSpaghettiCarveMaskNeighborCacheEntry(
                coord,
                cachedMaskCopy,
                spaghettiMaskCacheStoreHandle,
                lightVoxelSizeX,
                lightVoxelSizeZ,
                lightVoxelPlaneSize,
                lightBorderSize,
                spaghettiSettingsHash);

            baseChunkDataJob.spaghettiCarveMask = sharedSpaghettiCarveMask;
            baseChunkDataJob.spaghettiCarveMaskVoxelSizeX = lightVoxelSizeX;
            baseChunkDataJob.spaghettiCarveMaskVoxelPlaneSize = lightVoxelPlaneSize;
            baseChunkDataJob.spaghettiCarveMaskOffsetX = lightBorderSize - dataBorderSize;
            baseChunkDataJob.spaghettiCarveMaskOffsetZ = lightBorderSize - dataBorderSize;
        }

        JobHandle caveChunkDependency = useSharedSpaghettiCarveMask
            ? JobHandle.CombineDependencies(surfaceMaterialHandle, spaghettiCarveMaskHandle, copyBaseTerrainSolidsHandle)
            : JobHandle.CombineDependencies(surfaceMaterialHandle, copyBaseTerrainSolidsHandle);

        var stagedCaveChunkDataJob = baseChunkDataJob;
        stagedCaveChunkDataJob.stages = ChunkData.ChunkDataStageFlags.Caves;
        caveChunkDataHandle = stagedCaveChunkDataJob.Schedule(caveChunkDependency);

        var stagedOreChunkDataJob = baseChunkDataJob;
        stagedOreChunkDataJob.stages = ChunkData.ChunkDataStageFlags.Ores;
        oreChunkDataHandle = stagedOreChunkDataJob.Schedule(caveChunkDataHandle);

        if (enableWater)
        {
            var stagedFillWaterBelowSeaLevelJob = new ChunkData.FillTerrainVoidWaterBelowSeaLevelJob
            {
                baseSolids = baseTerrainSolids,
                blockTypes = blockTypes,
                solids = solids,
                border = dataBorderSize,
                seaLevel = math.min(SizeY - 1, (int)math.floor(seaLevel)),
                waterBlockId = (byte)BlockType.Water,
                waterIsSolid = blockMappings[(int)BlockType.Water].isSolid
            };
            waterChunkDataHandle = stagedFillWaterBelowSeaLevelJob.Schedule(
                dataTotalHeightPoints,
                32,
                JobHandle.CombineDependencies(oreChunkDataHandle, copyBaseTerrainSolidsHandle));
        }
        else
        {
            waterChunkDataHandle = JobHandle.CombineDependencies(oreChunkDataHandle, copyBaseTerrainSolidsHandle);
        }

        treeChunkDataHandle = waterChunkDataHandle;
        if (enableTrees)
        {
            var stagedTreeChunkDataJob = baseChunkDataJob;
            stagedTreeChunkDataJob.stages = ChunkData.ChunkDataStageFlags.Trees;
            treeChunkDataHandle = stagedTreeChunkDataJob.Schedule(waterChunkDataHandle);
        }

        finalChunkDataHandle = treeChunkDataHandle;
        if (blockEdits.IsCreated && blockEdits.Length > 0)
        {
            var stagedBlockEditChunkDataJob = baseChunkDataJob;
            stagedBlockEditChunkDataJob.stages = ChunkData.ChunkDataStageFlags.BlockEdits;
            finalChunkDataHandle = stagedBlockEditChunkDataJob.Schedule(treeChunkDataHandle);
        }
        JobHandle disposeBaseTerrainSolidsAfterLightingHandle = baseTerrainSolids.Dispose(finalChunkDataHandle);
        var buildChunkSnapshotJob = new BuildChunkSnapshotAndFlagsJob
        {
            blockTypes = blockTypes,
            blockMappings = blockMappings,
            voxelSnapshot = chunkVoxelSnapshot,
            subchunkNonEmpty = subchunkNonEmpty,
            subchunkColliderOccupancy = subchunkColliderOccupancy,
            borderSize = dataBorderSize
        };
        JobHandle buildChunkSnapshotHandle = buildChunkSnapshotJob.Schedule(finalChunkDataHandle);

        var populateLightOpacityJob = new PopulateLightOpacityJob
        {
            heightCache = lightHeightCache,
            opacity = lightOpacityData,
            effectiveOpacityByBlock = effectiveOpacityByBlock,
            coord = coord,
            border = lightBorderSize,
            offsetX = globalOffsetX,
            offsetZ = globalOffsetZ,
            biomeNoiseSettings = biomeNoiseSettings,
            terrainDensitySettings = terrainDensitySettings,
            skipInnerMin = lightBorderSize > dataBorderSize ? lightBorderSize - dataBorderSize : 0,
            skipInnerMaxExclusive = lightBorderSize > dataBorderSize ? (lightBorderSize - dataBorderSize) + dataVoxelSizeX : 0
        };
        JobHandle populateLightOpacityHandle = populateLightOpacityJob.Schedule(lightTotalHeightPoints, 32, lightHeightHandle);

        JobHandle lightOpacityTerrainHandle = populateLightOpacityHandle;
        if (useSharedSpaghettiCarveMask)
        {
            var spaghettiCaveOpacityJob = new ApplySpaghettiCaveCarveMaskToOpacityJob
            {
                carveMask = sharedSpaghettiCarveMask,
                opacity = lightOpacityData,
                airOpacity = effectiveOpacityByBlock[(int)BlockType.Air]
            };
            lightOpacityTerrainHandle = spaghettiCaveOpacityJob.Schedule(
                lightTotalVoxels,
                128,
                JobHandle.CombineDependencies(populateLightOpacityHandle, spaghettiCarveMaskHandle));
        }

        var disposeLightHeightCacheJob = new DisposeIntArrayJob
        {
            values = lightHeightCache
        };
        JobHandle disposeLightHeightCacheHandle = disposeLightHeightCacheJob.Schedule(lightOpacityTerrainHandle);
        var disposeSpaghettiCarveMaskJob = new DisposeByteArrayJob
        {
            values = sharedSpaghettiCarveMask
        };
        JobHandle spaghettiMaskDisposeDependency = useSharedSpaghettiCarveMask
            ? JobHandle.CombineDependencies(finalChunkDataHandle, lightOpacityTerrainHandle)
            : finalChunkDataHandle;
        if (useSharedSpaghettiCarveMask)
            spaghettiMaskDisposeDependency = JobHandle.CombineDependencies(spaghettiMaskDisposeDependency, spaghettiMaskCacheStoreHandle);

        JobHandle disposeSpaghettiCarveMaskHandle = disposeSpaghettiCarveMaskJob.Schedule(
            spaghettiMaskDisposeDependency);

        var copyGeneratedOpacityJob = new CopyGeneratedOpacityToLightVolumeJob
        {
            sourceBlockTypes = blockTypes,
            effectiveOpacityByBlock = effectiveOpacityByBlock,
            targetOpacity = lightOpacityData,
            sourceVoxelSizeX = dataVoxelSizeX,
            targetVoxelSizeX = lightVoxelSizeX,
            targetVoxelPlaneSize = lightVoxelPlaneSize,
            sourceBorder = dataBorderSize,
            targetBorder = lightBorderSize
        };
        JobHandle copyGeneratedOpacityHandle = copyGeneratedOpacityJob.Schedule(
            dataTotalVoxels,
            128,
            JobHandle.CombineDependencies(finalChunkDataHandle, lightOpacityTerrainHandle));

        JobHandle lightOpacityCleanupHandle = JobHandle.CombineDependencies(
            disposeLightHeightCacheHandle,
            disposeSpaghettiCarveMaskHandle);
        if (useSharedSpaghettiCarveMask)
            lightOpacityCleanupHandle = JobHandle.CombineDependencies(lightOpacityCleanupHandle, disposePrefilledSpaghettiColumnsHandle);
        JobHandle lightOpacityHandle = JobHandle.CombineDependencies(
            copyGeneratedOpacityHandle,
            lightOpacityCleanupHandle);
        if (blockEdits.IsCreated && blockEdits.Length > 0)
        {
            var opacityOverrideJob = new ApplyOpacityOverridesJob
            {
                overrides = blockEdits,
                effectiveOpacityByBlock = effectiveOpacityByBlock,
                opacity = lightOpacityData,
                chunkMinX = coord.x * SizeX,
                chunkMinZ = coord.y * SizeZ,
                borderSize = lightBorderSize,
                voxelSizeX = lightVoxelSizeX,
                voxelSizeZ = lightVoxelSizeZ,
                voxelPlaneSize = lightVoxelPlaneSize
            };
            lightOpacityHandle = opacityOverrideJob.Schedule(lightOpacityHandle);
        }

        var lightJob = new ChunkLighting.CroppedChunkLightingJob
        {
            opacity = lightOpacityData,
            light = light,
            blockLightData = lightData,
            enableHorizontalSkylight = enableHorizontalSkylight,
            horizontalSkylightStepLoss = horizontalSkylightStepLoss,
            inputVoxelSizeX = lightVoxelSizeX,
            inputVoxelSizeZ = lightVoxelSizeZ,
            inputTotalVoxels = lightTotalVoxels,
            inputVoxelPlaneSize = lightVoxelPlaneSize,
            outputVoxelSizeX = dataVoxelSizeX,
            outputVoxelSizeZ = dataVoxelSizeZ,
            outputVoxelPlaneSize = dataVoxelPlaneSize,
            outputOffsetX = lightBorderSize - dataBorderSize,
            outputOffsetZ = lightBorderSize - dataBorderSize,
            SizeY = SizeY
        };
        dataHandle = JobHandle.CombineDependencies(
            lightJob.Schedule(JobHandle.CombineDependencies(finalChunkDataHandle, lightOpacityHandle)),
            buildChunkSnapshotHandle);
        dataHandle = JobHandle.CombineDependencies(dataHandle, disposeBaseTerrainSolidsAfterLightingHandle);
    }

    public static void ScheduleMeshJob(
        NativeArray<int> heightCache,
        NativeArray<byte> blockTypes,
        NativeArray<byte> blockPlacementAxes,
        NativeArray<bool> solids,
        NativeArray<byte> light,
        NativeArray<BlockTextureMapping> nativeBlockMappings,
        NativeArray<int3> suppressedGrassBillboards,
        NativeArray<bool> subchunkNonEmpty,
        NativeArray<byte> knownVoxelData,
        bool useKnownVoxelData,
        int atlasTilesX,
        int atlasTilesY,
        bool generateSides,
        int borderSize,
        int chunkCoordX,
        int chunkCoordZ,
        int dirtySubchunkMask,
        bool enableGrassBillboards,
        float grassBillboardChance,
        BlockType grassBillboardBlockType,
        float grassBillboardHeight,
        float grassBillboardNoiseScale,
        float grassBillboardJitter,
        NativeArray<VegetationBillboardRuleData> vegetationBillboardRules,
        BiomeNoiseSettings biomeNoiseSettings,
        float aoStrength,
        float aoCurveExponent,
        float aoMinLight,
        bool useFastBedrockStyleMeshing,
        bool enableHighQualityLeafFoliage,
        bool enableUltraLeafBillboards,
        float leafFoliageSpawnChance,
        float leafFoliageHeightMin,
        float leafFoliageHeightMax,
        float leafFoliageHalfWidthMin,
        float leafFoliageHalfWidthMax,
        float leafFoliageBaseYOffsetMin,
        float leafFoliageBaseYOffsetMax,
        float leafFoliageCenterJitter,
        float leafUltraBillboardHeight,
        float leafUltraBillboardHalfWidth,
        float leafUltraBaseYOffset,
        float leafUltraCenterJitter,
        float leafUltraRotationOffsetDegrees,
        float leafUltraRotationRandomDegrees,
        float leafUltraFaceTiltDegrees,
        float leafUltraFaceTiltRandomDegrees,
        out JobHandle meshHandle,
        out NativeList<PackedChunkVertex> vertices,
        out NativeList<int> opaqueTriangles,
        out NativeList<int> transparentTriangles,
        out NativeList<int> billboardTriangles,
        out NativeList<int> waterTriangles,
        out NativeArray<SubchunkMeshRange> subchunkRanges,
        out NativeArray<ulong> subchunkVisibilityMasks
    )
    {
        // 1. AlocaÃƒÆ’Ã‚Â§ÃƒÆ’Ã‚Âµes das Listas de Mesh (Output)
        vertices = new NativeList<PackedChunkVertex>(4096, Allocator.Persistent);
        opaqueTriangles = new NativeList<int>(4096 * 3, Allocator.Persistent);
        waterTriangles = new NativeList<int>(4096 * 3, Allocator.Persistent);
        transparentTriangles = new NativeList<int>(4096 * 3, Allocator.Persistent);
        billboardTriangles = new NativeList<int>(2048 * 3, Allocator.Persistent);
        subchunkRanges = new NativeArray<SubchunkMeshRange>(SubchunksPerColumn, Allocator.Persistent);
        subchunkVisibilityMasks = new NativeArray<ulong>(SubchunksPerColumn, Allocator.Persistent);

        // ==========================================
        // JOB 2: GeraÃƒÆ’Ã‚Â§ÃƒÆ’Ã‚Â£o da Malha (Mesh)
        // ==========================================
        var meshJob = new ChunkMeshJob
        {
            startY = 0,
            endY = 0,
            blockTypes = blockTypes,
            blockPlacementAxes = blockPlacementAxes,
            solids = solids,
            light = light, // Usa a luz previamente calculada e passada por parÃƒÆ’Ã‚Â¢metro
            heightCache = heightCache,
            blockMappings = nativeBlockMappings,
            suppressedGrassBillboards = suppressedGrassBillboards,
            subchunkNonEmpty = subchunkNonEmpty,
            knownVoxelData = knownVoxelData,
            useKnownVoxelData = useKnownVoxelData,

            border = borderSize,
            atlasTilesX = atlasTilesX,
            atlasTilesY = atlasTilesY,
            generateSides = generateSides,
            chunkCoordX = chunkCoordX,
            chunkCoordZ = chunkCoordZ,
            enableGrassBillboards = enableGrassBillboards,
            grassBillboardChance = grassBillboardChance,
            grassBillboardBlockType = grassBillboardBlockType,
            grassBillboardHeight = grassBillboardHeight,
            grassBillboardNoiseScale = grassBillboardNoiseScale,
            grassBillboardJitter = grassBillboardJitter,
            vegetationBillboardRules = vegetationBillboardRules,
            biomeNoiseSettings = biomeNoiseSettings,
            aoStrength = aoStrength,
            aoCurveExponent = aoCurveExponent,
            aoMinLight = aoMinLight,
            useFastBedrockStyleMeshing = useFastBedrockStyleMeshing,
            enableHighQualityLeafFoliage = enableHighQualityLeafFoliage,
            enableUltraLeafBillboards = enableUltraLeafBillboards,
            leafFoliageSpawnChance = leafFoliageSpawnChance,
            leafFoliageHeightMin = leafFoliageHeightMin,
            leafFoliageHeightMax = leafFoliageHeightMax,
            leafFoliageHalfWidthMin = leafFoliageHalfWidthMin,
            leafFoliageHalfWidthMax = leafFoliageHalfWidthMax,
            leafFoliageBaseYOffsetMin = leafFoliageBaseYOffsetMin,
            leafFoliageBaseYOffsetMax = leafFoliageBaseYOffsetMax,
            leafFoliageCenterJitter = leafFoliageCenterJitter,
            leafUltraBillboardHeight = leafUltraBillboardHeight,
            leafUltraBillboardHalfWidth = leafUltraBillboardHalfWidth,
            leafUltraBaseYOffset = leafUltraBaseYOffset,
            leafUltraCenterJitter = leafUltraCenterJitter,
            leafUltraRotationOffsetDegrees = leafUltraRotationOffsetDegrees,
            leafUltraRotationRandomDegrees = leafUltraRotationRandomDegrees,
            leafUltraFaceTiltDegrees = leafUltraFaceTiltDegrees,
            leafUltraFaceTiltRandomDegrees = leafUltraFaceTiltRandomDegrees,
            dirtySubchunkMask = dirtySubchunkMask,
            subchunkRanges = subchunkRanges,

            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            waterTriangles = waterTriangles,
            transparentTriangles = transparentTriangles,
            billboardTriangles = billboardTriangles,
            subchunkVisibilityMasks = subchunkVisibilityMasks
        };
        // O MeshJob agora ÃƒÆ’Ã‚Â© agendado independentemente, assumindo que os dados intermediÃƒÆ’Ã‚Â¡rios jÃƒÆ’Ã‚Â¡ estÃƒÆ’Ã‚Â£o prontos
        meshHandle = meshJob.Schedule();
    }




    // =========================================================================
    // JOB 2: CHUNK MESH JOB (Greedy Meshing e Arrays Visuais)
    // =========================================================================
    [BurstCompile]
    private struct ChunkMeshJob : IJob
    {
        // DeallocateOnJobCompletion limpa todos estes arrays criados no Schedule.
        [ReadOnly] public NativeArray<int> heightCache;
        [ReadOnly] public NativeArray<byte> blockTypes;
        [ReadOnly] public NativeArray<byte> blockPlacementAxes;
        [ReadOnly] public NativeArray<bool> solids;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [ReadOnly] public NativeArray<byte> light;
        [ReadOnly] public NativeArray<int3> suppressedGrassBillboards;
        [ReadOnly] public NativeArray<VegetationBillboardRuleData> vegetationBillboardRules;
        [ReadOnly] public NativeArray<bool> subchunkNonEmpty;
        [ReadOnly] public NativeArray<byte> knownVoxelData;
        public bool useKnownVoxelData;

        public int border;
        public int atlasTilesX;
        public int atlasTilesY;
        public bool generateSides;
        public int chunkCoordX;
        public int chunkCoordZ;
        public bool enableGrassBillboards;
        public float grassBillboardChance;
        public BlockType grassBillboardBlockType;
        public float grassBillboardHeight;
        public float grassBillboardNoiseScale;
        public float grassBillboardJitter;
        public BiomeNoiseSettings biomeNoiseSettings;
        public float aoStrength;
        public float aoCurveExponent;
        public float aoMinLight;
        public bool useFastBedrockStyleMeshing;
        public bool enableHighQualityLeafFoliage;
        public bool enableUltraLeafBillboards;
        public float leafFoliageSpawnChance;
        public float leafFoliageHeightMin;
        public float leafFoliageHeightMax;
        public float leafFoliageHalfWidthMin;
        public float leafFoliageHalfWidthMax;
        public float leafFoliageBaseYOffsetMin;
        public float leafFoliageBaseYOffsetMax;
        public float leafFoliageCenterJitter;
        public float leafUltraBillboardHeight;
        public float leafUltraBillboardHalfWidth;
        public float leafUltraBaseYOffset;
        public float leafUltraCenterJitter;
        public float leafUltraRotationOffsetDegrees;
        public float leafUltraRotationRandomDegrees;
        public float leafUltraFaceTiltDegrees;
        public float leafUltraFaceTiltRandomDegrees;
        public int dirtySubchunkMask;
        public NativeArray<SubchunkMeshRange> subchunkRanges;

        // LIMITES DO SUBCHUNK
        public int startY;
        public int endY;

        public NativeList<PackedChunkVertex> vertices;
        public NativeList<int> opaqueTriangles;
        public NativeList<int> waterTriangles;
        public NativeList<int> transparentTriangles;
        public NativeList<int> billboardTriangles;
        public NativeArray<ulong> subchunkVisibilityMasks;
        private int currentSubchunkVertexStart;

        private struct GreedyFaceData
        {
            public byte blockId;
            public byte placementAxis;
            public byte valid;
            public byte faceLight;
            public byte surfaceHeight;
            public byte ao0;
            public byte ao1;
            public byte ao2;
            public byte ao3;
            public byte light0;
            public byte light1;
            public byte light2;
            public byte light3;
        }

        public void Execute()
        {
            float invAtlasTilesX = 1f / atlasTilesX;
            float invAtlasTilesY = 1f / atlasTilesY;
            NativeArray<byte> occlusionState = new NativeArray<byte>(4096, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<int> occlusionQueue = new NativeArray<int>(4096, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            try
            {
                for (int sub = 0; sub < SubchunksPerColumn; sub++)
                {
                    if ((dirtySubchunkMask & (1 << sub)) == 0)
                    {
                        subchunkRanges[sub] = default;
                        continue;
                    }

                    startY = sub * Chunk.SubchunkHeight;
                    endY = math.min(startY + Chunk.SubchunkHeight, SizeY);

                    if (!subchunkNonEmpty[sub])
                    {
                        subchunkVisibilityMasks[sub] = SubchunkOcclusion.AllVisibleMask;
                        subchunkRanges[sub] = default;
                        continue;
                    }

                    SubchunkMeshRange range = new SubchunkMeshRange
                    {
                        vertexStart = vertices.Length,
                        opaqueStart = opaqueTriangles.Length,
                        transparentStart = transparentTriangles.Length,
                        billboardStart = billboardTriangles.Length,
                        waterStart = waterTriangles.Length
                    };
                    currentSubchunkVertexStart = range.vertexStart;

                    subchunkVisibilityMasks[sub] = ComputeVisibilityMask(occlusionState, occlusionQueue);

                    GenerateMesh(heightCache, blockTypes, solids, light, invAtlasTilesX, invAtlasTilesY);
                    GenerateDecorativeMeshes(blockTypes, light, invAtlasTilesX, invAtlasTilesY);

                    range.vertexCount = vertices.Length - range.vertexStart;
                    range.opaqueCount = opaqueTriangles.Length - range.opaqueStart;
                    range.transparentCount = transparentTriangles.Length - range.transparentStart;
                    range.billboardCount = billboardTriangles.Length - range.billboardStart;
                    range.waterCount = waterTriangles.Length - range.waterStart;
                    subchunkRanges[sub] = range;
                }
            }
            finally
            {
                if (occlusionState.IsCreated) occlusionState.Dispose();
                if (occlusionQueue.IsCreated) occlusionQueue.Dispose();
            }
        }

        private int GetCurrentSubchunkLocalVertexIndex()
        {
            return vertices.Length - currentSubchunkVertexStart;
        }

        private void AddPackedVertex(Vector3 position, Vector3 normal, Vector2 uv0, Vector2 uv1, Vector4 uv2)
        {
            vertices.Add(new PackedChunkVertex
            {
                position = position,
                normal = normal,
                uv0 = uv0,
                uv1 = uv1,
                uv2 = uv2
            });
        }

        private ulong ComputeVisibilityMask(NativeArray<byte> occlusionState, NativeArray<int> queue)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;
            int opaqueCount = 0;

            for (int localY = 0; localY < Chunk.SubchunkHeight; localY++)
            {
                int worldY = startY + localY;
                for (int localZ = 0; localZ < SizeZ; localZ++)
                {
                    int sampleZ = localZ + border;
                    for (int localX = 0; localX < SizeX; localX++)
                    {
                        int sampleX = localX + border;
                        int sampleIndex = sampleX + worldY * voxelSizeX + sampleZ * voxelPlaneSize;
                        int visIndex = localX | (localY << 8) | (localZ << 4);

                        if (IsOcclusionOpaque((BlockType)blockTypes[sampleIndex]))
                        {
                            occlusionState[visIndex] = 1;
                            opaqueCount++;
                        }
                        else
                        {
                            occlusionState[visIndex] = 0;
                        }
                    }
                }
            }

            if (opaqueCount == 4096)
                return 0UL;

            ulong visibilityMask = 0UL;
            for (int localY = 0; localY < Chunk.SubchunkHeight; localY++)
            {
                for (int localZ = 0; localZ < SizeZ; localZ++)
                {
                    for (int localX = 0; localX < SizeX; localX++)
                    {
                        bool isBoundary = localX == 0 || localX == SizeX - 1 ||
                                          localY == 0 || localY == Chunk.SubchunkHeight - 1 ||
                                          localZ == 0 || localZ == SizeZ - 1;
                        if (!isBoundary)
                            continue;

                        int startIndex = localX | (localY << 8) | (localZ << 4);
                        if (occlusionState[startIndex] != 0)
                            continue;

                        visibilityMask = FloodFillVisibilityMask(startIndex, occlusionState, queue, visibilityMask);
                    }
                }
            }

            return visibilityMask;
        }

        private ulong FloodFillVisibilityMask(int startIndex, NativeArray<byte> occlusionState, NativeArray<int> queue, ulong visibilityMask)
        {
            int head = 0;
            int tail = 0;
            byte faceMask = 0;

            queue[tail++] = startIndex;
            occlusionState[startIndex] = 1;

            while (head < tail)
            {
                int index = queue[head++];
                AddOcclusionEdges(index, ref faceMask);

                for (int face = 0; face < SubchunkOcclusion.FaceCount; face++)
                {
                    int neighborIndex = GetNeighborIndexAtFace(index, face);
                    if (neighborIndex >= 0 && occlusionState[neighborIndex] == 0)
                    {
                        occlusionState[neighborIndex] = 1;
                        queue[tail++] = neighborIndex;
                    }
                }
            }

            return SubchunkOcclusion.AddFaceSet(visibilityMask, faceMask);
        }

        private static void AddOcclusionEdges(int index, ref byte faceMask)
        {
            int x = index & 15;
            if (x == 0)
                faceMask = (byte)(faceMask | (1 << SubchunkOcclusion.West));
            else if (x == 15)
                faceMask = (byte)(faceMask | (1 << SubchunkOcclusion.East));

            int y = (index >> 8) & 15;
            if (y == 0)
                faceMask = (byte)(faceMask | (1 << SubchunkOcclusion.Down));
            else if (y == 15)
                faceMask = (byte)(faceMask | (1 << SubchunkOcclusion.Up));

            int z = (index >> 4) & 15;
            if (z == 0)
                faceMask = (byte)(faceMask | (1 << SubchunkOcclusion.North));
            else if (z == 15)
                faceMask = (byte)(faceMask | (1 << SubchunkOcclusion.South));
        }

        private static int GetNeighborIndexAtFace(int index, int face)
        {
            switch (face)
            {
                case SubchunkOcclusion.Down:
                    return ((index >> 8) & 15) == 0 ? -1 : index - 256;
                case SubchunkOcclusion.Up:
                    return ((index >> 8) & 15) == 15 ? -1 : index + 256;
                case SubchunkOcclusion.North:
                    return ((index >> 4) & 15) == 0 ? -1 : index - 16;
                case SubchunkOcclusion.South:
                    return ((index >> 4) & 15) == 15 ? -1 : index + 16;
                case SubchunkOcclusion.West:
                    return (index & 15) == 0 ? -1 : index - 1;
                case SubchunkOcclusion.East:
                    return (index & 15) == 15 ? -1 : index + 1;
                default:
                    return -1;
            }
        }

        private bool IsOcclusionOpaque(BlockType blockType)
        {
            if (blockType == BlockType.Air)
                return false;

            int blockIndex = (int)blockType;
            if (blockIndex < 0 || blockIndex >= blockMappings.Length)
                return false;

            BlockTextureMapping mapping = blockMappings[blockIndex];
            if (mapping.isEmpty || mapping.isTransparent || mapping.isLiquid)
                return false;

            return mapping.isSolid && BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Cube;
        }

        private byte GetBlockPlacementAxisValue(int voxelIndex)
        {
            if (!blockPlacementAxes.IsCreated)
                return (byte)BlockPlacementAxis.Y;

            if ((uint)voxelIndex >= (uint)blockPlacementAxes.Length)
                return (byte)BlockPlacementAxis.Y;

            return blockPlacementAxes[voxelIndex];
        }

        private static bool HasFace(in GreedyFaceData face)
        {
            return face.valid != 0;
        }

        private static bool HasSameSurface(in GreedyFaceData a, in GreedyFaceData b)
        {
            return HasFace(a) &&
                   HasFace(b) &&
                   a.blockId == b.blockId &&
                   a.placementAxis == b.placementAxis &&
                   a.faceLight == b.faceLight &&
                   a.surfaceHeight == b.surfaceHeight;
        }

        private static bool CanMergeAlongU(in GreedyFaceData left, in GreedyFaceData right)
        {
            // Merge only when the shared edge keeps the same AO signature.
            return HasSameSurface(left, right) &&
                   left.ao1 == right.ao0 &&
                   left.ao2 == right.ao3 &&
                   left.light1 == right.light0 &&
                   left.light2 == right.light3;
        }

        private static bool CanMergeAlongV(in GreedyFaceData bottom, in GreedyFaceData top)
        {
            return HasSameSurface(bottom, top) &&
                   bottom.ao3 == top.ao0 &&
                   bottom.ao2 == top.ao1 &&
                   bottom.light3 == top.light0 &&
                   bottom.light2 == top.light1;
        }

        private static Vector2 ComputePlacementAwareUv(
            int u,
            int v,
            float rawU,
            float rawV,
            float posD,
            BlockFace sampledFace,
            BlockPlacementAxis placementAxis)
        {
            Vector3 worldCoords = new Vector3(
                u == 0 ? rawU : v == 0 ? rawV : posD,
                u == 1 ? rawU : v == 1 ? rawV : posD,
                u == 2 ? rawU : v == 2 ? rawV : posD);

            Vector3 canonicalCoords = ToCanonicalCoords(worldCoords, placementAxis);
            return sampledFace switch
            {
                BlockFace.Top => new Vector2(canonicalCoords.x, canonicalCoords.z),
                BlockFace.Bottom => new Vector2(canonicalCoords.x, canonicalCoords.z),
                BlockFace.Right => new Vector2(canonicalCoords.z, canonicalCoords.y),
                BlockFace.Left => new Vector2(canonicalCoords.z, canonicalCoords.y),
                BlockFace.Front => new Vector2(canonicalCoords.x, canonicalCoords.y),
                BlockFace.Back => new Vector2(canonicalCoords.x, canonicalCoords.y),
                _ => new Vector2(canonicalCoords.x, canonicalCoords.y)
            };
        }

        private static BlockPlacementAxis ResolveUvPlacementAxis(BlockTextureMapping mapping, BlockPlacementAxis placementAxis)
        {
            if (!mapping.usePlacementAxisRotation)
                return BlockPlacementAxis.Y;

            if (mapping.placementRotationAxes == BlockPlacementRotationAxes.Horizontal &&
                BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Cube)
            {
                // Furnace/crafter style: rotate which face uses each texture,
                // but keep UV orientation in the default cube space.
                return BlockPlacementAxis.Y;
            }

            return placementAxis;
        }

        private static BlockFace ResolveUvSamplingFace(
            BlockTextureMapping mapping,
            BlockFace worldFace,
            BlockFace sampledFace)
        {
            if (!mapping.usePlacementAxisRotation)
                return worldFace;

            if (mapping.placementRotationAxes == BlockPlacementRotationAxes.Horizontal &&
                BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Cube)
            {
                // Horizontal-facing cubes (furnace/crafter): rotate face-to-texture mapping,
                // but keep UV orientation anchored to the actual world face plane.
                return worldFace;
            }

            return sampledFace;
        }

        private static Vector3 ToCanonicalCoords(Vector3 worldCoords, BlockPlacementAxis placementAxis)
        {
            return BlockPlacementRotationUtility.SanitizeAxis(placementAxis) switch
            {
                BlockPlacementAxis.X => new Vector3(-worldCoords.y, worldCoords.x, worldCoords.z),
                BlockPlacementAxis.Z => new Vector3(worldCoords.x, worldCoords.z, -worldCoords.y),
                _ => worldCoords
            };
        }

        private static Vector3Int GetFaceVertexPlanePos(
            int axis,
            int u,
            int v,
            int n,
            int normalSign,
            int i,
            int j,
            int du,
            int dv)
        {
            int planeN = n + normalSign;
            int uCoord = i + du;
            int vCoord = j + dv;

            return new Vector3Int(
                axis == 0 ? planeN : (u == 0 ? uCoord : v == 0 ? vCoord : n),
                axis == 1 ? planeN : (u == 1 ? uCoord : v == 1 ? vCoord : n),
                axis == 2 ? planeN : (u == 2 ? uCoord : v == 2 ? vCoord : n)
            );
        }

        private static byte GetRectVertexAO(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int width,
            int height,
            int localX,
            int localY)
        {
            int cellX = localX == width ? startI + width - 1 : startI + localX;
            int cellY = localY == height ? startJ + height - 1 : startJ + localY;
            GreedyFaceData face = mask[cellX + cellY * sizeU];

            if (localX == width)
                return localY == height ? face.ao2 : face.ao1;

            return localY == height ? face.ao3 : face.ao0;
        }

        private static bool MatchesQuadInterpolationForAO(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int width,
            int height,
            bool flipTriangle)
        {
            if (width <= 0 || height <= 0)
                return false;

            int scale = width * height;
            int ao00 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, 0, 0);
            int ao10 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, width, 0);
            int ao11 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, width, height);
            int ao01 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, 0, height);

            for (int y = 0; y <= height; y++)
            {
                for (int x = 0; x <= width; x++)
                {
                    int actual = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, x, y);
                    int expectedScaled;

                    if (!flipTriangle)
                    {
                        if (x * height >= y * width)
                        {
                            expectedScaled = ao00 * scale +
                                             (ao10 - ao00) * x * height +
                                             (ao11 - ao10) * y * width;
                        }
                        else
                        {
                            expectedScaled = ao00 * scale +
                                             (ao11 - ao01) * x * height +
                                             (ao01 - ao00) * y * width;
                        }
                    }
                    else
                    {
                        if (x * height + y * width <= scale)
                        {
                            expectedScaled = ao00 * scale +
                                             (ao10 - ao00) * x * height +
                                             (ao01 - ao00) * y * width;
                        }
                        else
                        {
                            expectedScaled = ao11 * scale +
                                             (ao10 - ao11) * width * (height - y) +
                                             (ao01 - ao11) * height * (width - x);
                        }
                    }

                    if (actual * scale != expectedScaled)
                        return false;
                }
            }

            return true;
        }

        private static byte GetRectVertexLight(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int width,
            int height,
            int localX,
            int localY)
        {
            int cellX = localX == width ? startI + width - 1 : startI + localX;
            int cellY = localY == height ? startJ + height - 1 : startJ + localY;
            GreedyFaceData face = mask[cellX + cellY * sizeU];

            if (localX == width)
                return localY == height ? face.light2 : face.light1;

            return localY == height ? face.light3 : face.light0;
        }

        private static bool MatchesQuadInterpolationForLight(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int width,
            int height,
            bool flipTriangle)
        {
            if (width <= 0 || height <= 0)
                return false;

            int scale = width * height;
            int light00 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, 0, 0);
            int light10 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, width, 0);
            int light11 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, width, height);
            int light01 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, 0, height);

            for (int y = 0; y <= height; y++)
            {
                for (int x = 0; x <= width; x++)
                {
                    int actual = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, x, y);
                    int expectedScaled;

                    if (!flipTriangle)
                    {
                        if (x * height >= y * width)
                        {
                            expectedScaled = light00 * scale +
                                             (light10 - light00) * x * height +
                                             (light11 - light10) * y * width;
                        }
                        else
                        {
                            expectedScaled = light00 * scale +
                                             (light11 - light01) * x * height +
                                             (light01 - light00) * y * width;
                        }
                    }
                    else
                    {
                        if (x * height + y * width <= scale)
                        {
                            expectedScaled = light00 * scale +
                                             (light10 - light00) * x * height +
                                             (light01 - light00) * y * width;
                        }
                        else
                        {
                            expectedScaled = light11 * scale +
                                             (light10 - light11) * width * (height - y) +
                                             (light01 - light11) * height * (width - x);
                        }
                    }

                    if (actual * scale != expectedScaled)
                        return false;
                }
            }

            return true;
        }

        private static bool TryGetRepresentableRectFlip(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int width,
            int height,
            out bool flipTriangle)
        {
            bool noFlipMatches =
                MatchesQuadInterpolationForAO(mask, sizeU, startI, startJ, width, height, false) &&
                MatchesQuadInterpolationForLight(mask, sizeU, startI, startJ, width, height, false);
            bool flipMatches =
                MatchesQuadInterpolationForAO(mask, sizeU, startI, startJ, width, height, true) &&
                MatchesQuadInterpolationForLight(mask, sizeU, startI, startJ, width, height, true);

            int ao00 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, 0, 0);
            int ao10 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, width, 0);
            int ao11 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, width, height);
            int ao01 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, 0, height);
            int light00 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, 0, 0);
            int light10 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, width, 0);
            int light11 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, width, height);
            int light01 = GetRectVertexLight(mask, sizeU, startI, startJ, width, height, 0, height);
            bool heuristicFlip = (ao00 + ao11 + light00 + light11) > (ao10 + ao01 + light10 + light01);

            if (noFlipMatches && flipMatches)
            {
                flipTriangle = heuristicFlip;
                return true;
            }

            if (flipMatches)
            {
                flipTriangle = true;
                return true;
            }

            if (noFlipMatches)
            {
                flipTriangle = false;
                return true;
            }

            flipTriangle = heuristicFlip;
            return false;
        }

        private static bool TryGetRepresentableRectFast(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int maxWidth,
            int maxHeight,
            out int resolvedWidth,
            out int resolvedHeight,
            out bool flipTriangle)
        {
            if (TryGetRepresentableRectFlip(mask, sizeU, startI, startJ, maxWidth, maxHeight, out flipTriangle))
            {
                resolvedWidth = maxWidth;
                resolvedHeight = maxHeight;
                return true;
            }

            int widthFirstW;
            int widthFirstH;
            bool widthFirstFlip;
            ShrinkRectUntilRepresentable(
                mask, sizeU, startI, startJ, maxWidth, maxHeight, true,
                out widthFirstW, out widthFirstH, out widthFirstFlip);

            int heightFirstW;
            int heightFirstH;
            bool heightFirstFlip;
            ShrinkRectUntilRepresentable(
                mask, sizeU, startI, startJ, maxWidth, maxHeight, false,
                out heightFirstW, out heightFirstH, out heightFirstFlip);

            int widthFirstArea = widthFirstW * widthFirstH;
            int heightFirstArea = heightFirstW * heightFirstH;

            if (widthFirstArea >= heightFirstArea)
            {
                resolvedWidth = widthFirstW;
                resolvedHeight = widthFirstH;
                flipTriangle = widthFirstFlip;
            }
            else
            {
                resolvedWidth = heightFirstW;
                resolvedHeight = heightFirstH;
                flipTriangle = heightFirstFlip;
            }

            return true;
        }

        private static void ShrinkRectUntilRepresentable(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int maxWidth,
            int maxHeight,
            bool shrinkWidthFirst,
            out int resolvedWidth,
            out int resolvedHeight,
            out bool flipTriangle)
        {
            int width = maxWidth;
            int height = maxHeight;

            while (true)
            {
                if (TryGetRepresentableRectFlip(mask, sizeU, startI, startJ, width, height, out flipTriangle))
                {
                    resolvedWidth = width;
                    resolvedHeight = height;
                    return;
                }

                if (width <= 1 && height <= 1)
                    break;

                if (shrinkWidthFirst)
                {
                    if (width > 1)
                        width--;
                    else
                        height--;
                }
                else
                {
                    if (height > 1)
                        height--;
                    else
                        width--;
                }
            }

            resolvedWidth = 1;
            resolvedHeight = 1;
            flipTriangle = (mask[startI + startJ * sizeU].ao0 + mask[startI + startJ * sizeU].ao2) >
                           (mask[startI + startJ * sizeU].ao1 + mask[startI + startJ * sizeU].ao3);
        }

        private void GenerateDecorativeMeshes(
            NativeArray<byte> blockTypes,
            NativeArray<byte> light,
            float invAtlasTilesX,
            float invAtlasTilesY)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;
            bool generateGrassBillboards = enableGrassBillboards && grassBillboardChance > 0f;
            bool generateHighLeafFoliage = enableHighQualityLeafFoliage;
            bool generateUltraLeafFoliage = enableUltraLeafBillboards;
            bool generateAnyLeafFoliage = generateHighLeafFoliage || generateUltraLeafFoliage;
            float noiseScale = 0f;
            float jitter = 0f;
            Vector2 leavesAtlasUv = default;
            float leavesTint = 0f;
            if (generateGrassBillboards)
            {
                noiseScale = math.max(1e-4f, grassBillboardNoiseScale);
                jitter = math.clamp(grassBillboardJitter, 0f, 0.35f);
            }

            if (generateAnyLeafFoliage)
            {
                int leavesMappingIndex = (int)BlockType.Leaves;
                if ((uint)leavesMappingIndex >= (uint)blockMappings.Length)
                {
                    generateHighLeafFoliage = false;
                    generateUltraLeafFoliage = false;
                }
                else
                {
                    BlockTextureMapping leavesMapping = blockMappings[leavesMappingIndex];
                    if (leavesMapping.isEmpty)
                    {
                        generateHighLeafFoliage = false;
                        generateUltraLeafFoliage = false;
                    }
                    else
                    {
                        Vector2Int leavesTile = leavesMapping.GetTileCoord(BlockFace.Front);
                        leavesAtlasUv = new Vector2(
                            leavesTile.x * invAtlasTilesX + 0.001f,
                            leavesTile.y * invAtlasTilesY + 0.001f);
                        leavesTint = leavesMapping.GetTint(BlockFace.Front) ? 1f : 0f;
                    }
                }
            }

            int minY = math.max(startY - 1, 0);
            int maxY = math.min(endY - 1, SizeY - 1);

            for (int y = minY; y <= maxY; y++)
            {
                for (int z = border; z < border + SizeZ; z++)
                {
                    for (int x = border; x < border + SizeX; x++)
                    {
                        int idx = x + y * voxelSizeX + z * voxelPlaneSize;
                        BlockType blockType = (BlockType)blockTypes[idx];

                        if (y >= startY && blockType != BlockType.Air)
                        {
                            BlockTextureMapping mapping = blockMappings[(int)blockType];
                            BlockRenderShape effectiveShape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
                            if (!mapping.isEmpty && effectiveShape != BlockRenderShape.Cube)
                            {
                                float specialLight01 = GetSpecialMeshLight01(x, y, z, voxelSizeX, voxelSizeZ, light);
                                Vector3 origin = new Vector3(x - border, y, z - border);
                                byte rawPlacementData = GetBlockPlacementAxisValue(idx);
                                BlockPlacementAxis placementAxis = blockType == BlockType.wire &&
                                                                   WirePlacementUtility.TryGetWall(rawPlacementData, out BlockPlacementAxis explicitWireAxis, out _)
                                    ? explicitWireAxis
                                    : BlockPlacementRotationUtility.SanitizeStoredAxis((BlockPlacementAxis)rawPlacementData);

                                switch (effectiveShape)
                                {
                                    case BlockRenderShape.Cross:
                                        AddCrossShape(origin, mapping, blockType, invAtlasTilesX, invAtlasTilesY, specialLight01);
                                        break;

                                    case BlockRenderShape.Cuboid:
                                        AddCuboidShape(origin, mapping, blockType, x, y, z, invAtlasTilesX, invAtlasTilesY, specialLight01);
                                        break;

                                    case BlockRenderShape.Plane:
                                        AddPlaneShape(
                                            origin,
                                            mapping,
                                            blockType,
                                            placementAxis,
                                            rawPlacementData,
                                            x,
                                            y,
                                            z,
                                            blockTypes,
                                            voxelSizeX,
                                            voxelSizeZ,
                                            voxelPlaneSize,
                                            invAtlasTilesX,
                                            invAtlasTilesY,
                                            specialLight01);
                                        break;
                                }
                            }
                        }

                        if ((generateHighLeafFoliage || generateUltraLeafFoliage) &&
                            y >= startY &&
                            blockType == BlockType.Leaves)
                        {
                            if (generateUltraLeafFoliage)
                            {
                                AddUltraLeafBillboardFoliage(
                                    x,
                                    y,
                                    z,
                                    light,
                                    voxelSizeX,
                                    voxelSizeZ,
                                    leavesAtlasUv,
                                    leavesTint);
                            }
                            else
                            {
                                TryAddHighQualityLeafFoliage(
                                    x,
                                    y,
                                    z,
                                    blockTypes,
                                    light,
                                    voxelSizeX,
                                    voxelSizeZ,
                                    voxelPlaneSize,
                                    leavesAtlasUv,
                                    leavesTint);
                            }
                        }

                        if (!generateGrassBillboards || y + 1 >= SizeY)
                        {
                            continue;
                        }

                        int py = y + 1;
                        if (py < startY || py >= endY)
                            continue;

                        int upIdx = idx + voxelSizeX;
                        if (blockTypes[upIdx] != (byte)BlockType.Air)
                            continue;

                        int worldX = chunkCoordX * SizeX + (x - border);
                        int worldZ = chunkCoordZ * SizeZ + (z - border);
                        if (!TryResolveVegetationBillboardRule(blockType, worldX, py, worldZ, noiseScale, out BlockType billboardBlockType, out uint variationHash))
                            continue;

                        int billboardMappingIndex = (int)billboardBlockType;
                        if ((uint)billboardMappingIndex >= (uint)blockMappings.Length)
                            continue;

                        BlockTextureMapping billboardMapping = blockMappings[billboardMappingIndex];
                        Vector2Int billboardTile = billboardMapping.GetTileCoord(BlockFace.Front);
                        Vector2 billboardAtlasUv = new Vector2(
                            billboardTile.x * invAtlasTilesX + 0.001f,
                            billboardTile.y * invAtlasTilesY + 0.001f);
                        float billboardTint = billboardMapping.GetTint(BlockFace.Front) ? 1f : 0f;

                        float jx = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 8, jitter);
                        float jz = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 16, jitter);
                        float billboardHeight = VegetationBillboardUtility.ComputeHeight(grassBillboardHeight, variationHash);
                        float billboardHalfWidth = VegetationBillboardUtility.ComputeHalfWidth(variationHash);
                        float centerYOffset = VegetationBillboardUtility.ComputeBaseYOffset(variationHash);
                        byte packed = light[upIdx];
                        byte billboardLight = (byte)math.max(
                            (int)LightUtils.GetSkyLight(packed),
                            (int)LightUtils.GetBlockLight(packed)
                        );
                        float light01 = billboardLight / 15f;
                        Vector3 center = new Vector3((x - border) + 0.5f + jx, py + centerYOffset, (z - border) + 0.5f + jz);
                        AddBillboardCross(center, billboardHeight, billboardHalfWidth, billboardAtlasUv, light01, billboardTint);
                    }
                }
            }
        }

        private void TryAddHighQualityLeafFoliage(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            NativeArray<byte> light,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            Vector2 atlasUv,
            float tint)
        {
            int worldX = chunkCoordX * SizeX + (x - border);
            int worldZ = chunkCoordZ * SizeZ + (z - border);
            uint variationHash = VegetationBillboardUtility.ComputeVariantHash(worldX, y, worldZ);
            float chanceRoll = (variationHash & 0x0000FFFFu) / 65535f;
            float spawnChance = math.saturate(leafFoliageSpawnChance);
            if (chanceRoll > spawnChance)
                return;

            float heightMin = math.max(0.2f, math.min(leafFoliageHeightMin, leafFoliageHeightMax));
            float heightMax = math.max(heightMin, math.max(leafFoliageHeightMin, leafFoliageHeightMax));
            float halfWidthMin = math.max(0.5f, math.min(leafFoliageHalfWidthMin, leafFoliageHalfWidthMax));
            float halfWidthMax = math.max(halfWidthMin, math.max(leafFoliageHalfWidthMin, leafFoliageHalfWidthMax));
            float baseYOffsetMin = math.min(leafFoliageBaseYOffsetMin, leafFoliageBaseYOffsetMax);
            float baseYOffsetMax = math.max(leafFoliageBaseYOffsetMin, leafFoliageBaseYOffsetMax);
            float centerJitter = math.max(0f, leafFoliageCenterJitter);

            float jx = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 11, centerJitter);
            float jz = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 17, centerJitter);
            float baseYOffset = math.lerp(baseYOffsetMin, baseYOffsetMax, ((variationHash >> 16) & 0xFFu) / 255f);
            float height = math.lerp(heightMin, heightMax, ((variationHash >> 24) & 0xFFu) / 255f);
            float halfWidth = math.lerp(halfWidthMin, halfWidthMax, ((variationHash >> 8) & 0xFFu) / 255f);

            float light01 = GetSpecialMeshLight01(x, y, z, voxelSizeX, voxelSizeZ, light);
            // Mantem o centro do billboard no centro do voxel para as quads cruzarem pelas diagonais do bloco.
            Vector3 center = new Vector3((x - border) + 0.5f + jx, y + baseYOffset, (z - border) + 0.5f + jz);
            AddBillboardCross(center, height, halfWidth, atlasUv, light01, tint);
        }

        private void AddUltraLeafBillboardFoliage(
            int x,
            int y,
            int z,
            NativeArray<byte> light,
            int voxelSizeX,
            int voxelSizeZ,
            Vector2 atlasUv,
            float tint)
        {
            int worldX = chunkCoordX * SizeX + (x - border);
            int worldZ = chunkCoordZ * SizeZ + (z - border);
            uint variationHash = VegetationBillboardUtility.ComputeVariantHash(worldX, y, worldZ);

            float centerJitter = math.max(0f, leafUltraCenterJitter);
            float jx = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 5, centerJitter);
            float jz = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 13, centerJitter);
            float height = math.max(0.4f, leafUltraBillboardHeight);
            float halfWidth = math.max(0.5f, leafUltraBillboardHalfWidth);
            float baseYOffset = math.clamp(leafUltraBaseYOffset, -0.4f, 0.4f);
            float baseRotationDeg = math.clamp(leafUltraRotationOffsetDegrees, 0f, 45f);
            float randomRotationRangeDeg = math.max(0f, leafUltraRotationRandomDegrees);
            float baseTiltDeg = math.clamp(leafUltraFaceTiltDegrees, 0f, 60f);
            float randomTiltRangeDeg = math.max(0f, leafUltraFaceTiltRandomDegrees);
            float random01 = ((variationHash >> 20) & 0x3FFu) / 1023f;
            float rotationDeg = baseRotationDeg + (random01 * 2f - 1f) * randomRotationRangeDeg;

            float light01 = GetSpecialMeshLight01(x, y, z, voxelSizeX, voxelSizeZ, light);
            // Pivot no centro do voxel para manter volume aparente de qualquer angulo.
            Vector3 center = new Vector3((x - border) + 0.5f + jx, y + 0.5f + baseYOffset, (z - border) + 0.5f + jz);
            AddBillboardFourFaces(center, height, halfWidth, atlasUv, light01, tint, rotationDeg, baseTiltDeg, randomTiltRangeDeg, variationHash);
        }

        private void AddBillboardFourFaces(
            Vector3 center,
            float height,
            float halfWidth,
            Vector2 atlasUv,
            float light01,
            float tint,
            float rotationOffsetDegrees,
            float tiltBaseDegrees,
            float tiltRandomDegrees,
            uint variationHash)
        {
            float baseRad = math.radians(rotationOffsetDegrees);
            for (int i = 0; i < 4; i++)
            {
                float angle = baseRad + i * (math.PI * 0.25f);
                Vector2 dir = new Vector2(math.cos(angle), math.sin(angle));

                float tiltNoise = ((variationHash >> (i * 8)) & 0xFFu) / 255f;
                float tiltDegrees = tiltBaseDegrees + (tiltNoise * 2f - 1f) * tiltRandomDegrees;
                if ((i & 1) != 0)
                    tiltDegrees = -tiltDegrees;

                AddBillboardPlane(center, height, halfWidth, dir, tiltDegrees, atlasUv, light01, tint);
            }
        }

        private void AddBillboardPlane(
            Vector3 center,
            float height,
            float halfWidth,
            Vector2 horizontalDirectionXZ,
            float tiltDegrees,
            Vector2 atlasUv,
            float light01,
            float tint)
        {
            Vector3 right = new Vector3(horizontalDirectionXZ.x, 0f, horizontalDirectionXZ.y) * halfWidth;
            Vector3 up = Vector3.up;

            if (math.abs(tiltDegrees) > 0.001f)
            {
                float3 axis = math.normalize(new float3(horizontalDirectionXZ.x, 0f, horizontalDirectionXZ.y));
                quaternion tilt = quaternion.AxisAngle(axis, math.radians(tiltDegrees));
                float3 tiltedUp = math.mul(tilt, new float3(0f, 1f, 0f));
                up = new Vector3(tiltedUp.x, tiltedUp.y, tiltedUp.z);
            }

            Vector3 halfUp = up * (height * 0.5f);
            Vector3 p0 = center - right - halfUp;
            Vector3 p1 = center + right - halfUp;
            Vector3 p2 = center + right + halfUp;
            Vector3 p3 = center - right + halfUp;
            AddDoubleSidedQuad(p0, p1, p2, p3, atlasUv, light01, tint);
        }

        private bool TryResolveVegetationBillboardRule(
            BlockType groundBlockType,
            int worldX,
            int worldY,
            int worldZ,
            float noiseScale,
            out BlockType billboardBlockType,
            out uint variationHash)
        {
            billboardBlockType = BlockType.Air;
            variationHash = 0u;

            if (IsSuppressedGrassBillboard(worldX, worldY, worldZ))
                return false;

            return VegetationBillboardUtility.TryResolveBillboardRule(
                biomeNoiseSettings,
                vegetationBillboardRules,
                worldX,
                worldY,
                worldZ,
                groundBlockType,
                grassBillboardChance,
                noiseScale,
                grassBillboardBlockType,
                out billboardBlockType,
                out variationHash);
        }

        private void AddBillboardCross(Vector3 center, float height, float halfWidth, Vector2 atlasUv, float light01, float tint)
        {
            Vector3 a0 = center + new Vector3(-halfWidth, 0f, -halfWidth);
            Vector3 a1 = center + new Vector3(halfWidth, 0f, halfWidth);
            Vector3 a2 = a1 + new Vector3(0f, height, 0f);
            Vector3 a3 = a0 + new Vector3(0f, height, 0f);
            AddDoubleSidedQuad(a0, a1, a2, a3, atlasUv, light01, tint);

            Vector3 b0 = center + new Vector3(-halfWidth, 0f, halfWidth);
            Vector3 b1 = center + new Vector3(halfWidth, 0f, -halfWidth);
            Vector3 b2 = b1 + new Vector3(0f, height, 0f);
            Vector3 b3 = b0 + new Vector3(0f, height, 0f);
            AddDoubleSidedQuad(b0, b1, b2, b3, atlasUv, light01, tint);
        }

        private bool IsSuppressedGrassBillboard(int worldX, int worldY, int worldZ)
        {
            for (int i = 0; i < suppressedGrassBillboards.Length; i++)
            {
                int3 p = suppressedGrassBillboards[i];
                if (p.x == worldX && p.y == worldY && p.z == worldZ)
                    return true;
            }
            return false;
        }

        private static Vector3 ComputeQuadPlaneNormal(Vector3 p0, Vector3 p1, Vector3 p2)
        {
            float3 edgeA = new float3(p1.x - p0.x, p1.y - p0.y, p1.z - p0.z);
            float3 edgeB = new float3(p2.x - p0.x, p2.y - p0.y, p2.z - p0.z);
            float3 n = math.cross(edgeA, edgeB);
            float lenSq = math.lengthsq(n);
            if (lenSq <= 1e-8f)
                return new Vector3(0f, 1f, 0f);

            float invLen = math.rsqrt(lenSq);
            n *= invLen;
            return new Vector3(n.x, n.y, n.z);
        }

        private void AddDoubleSidedQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2 atlasUv,
            float light01,
            float tint)
        {
            int vIndex = GetCurrentSubchunkLocalVertexIndex();
            Vector3 planeNormal = ComputeQuadPlaneNormal(p0, p1, p2);

            Vector4 e = new Vector4(light01, tint, 1f, 0f);
            AddPackedVertex(p0, planeNormal, new Vector2(0f, 0f), atlasUv, e);
            AddPackedVertex(p1, planeNormal, new Vector2(1f, 0f), atlasUv, e);
            AddPackedVertex(p2, planeNormal, new Vector2(1f, 1f), atlasUv, e);
            AddPackedVertex(p3, planeNormal, new Vector2(0f, 1f), atlasUv, e);

            billboardTriangles.Add(vIndex + 0);
            billboardTriangles.Add(vIndex + 1);
            billboardTriangles.Add(vIndex + 2);
            billboardTriangles.Add(vIndex + 0);
            billboardTriangles.Add(vIndex + 2);
            billboardTriangles.Add(vIndex + 3);

            billboardTriangles.Add(vIndex + 0);
            billboardTriangles.Add(vIndex + 2);
            billboardTriangles.Add(vIndex + 1);
            billboardTriangles.Add(vIndex + 0);
            billboardTriangles.Add(vIndex + 3);
            billboardTriangles.Add(vIndex + 2);
        }

        private void AddCrossShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

            Vector2Int tile = mapping.GetTileCoord(BlockFace.Front);
            Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
            float tint = mapping.GetTint(BlockFace.Front) ? 1f : 0f;

            NativeList<int> tris = FluidBlockUtility.IsWater(blockType)
                ? waterTriangles
                : (mapping.isTransparent ? transparentTriangles : opaqueTriangles);

            Vector3 a0 = origin + new Vector3(min.x, min.y, min.z);
            Vector3 a1 = origin + new Vector3(max.x, min.y, max.z);
            Vector3 a2 = origin + new Vector3(max.x, max.y, max.z);
            Vector3 a3 = origin + new Vector3(min.x, max.y, min.z);
            AddDoubleSidedShapeQuad(a0, a1, a2, a3, atlasUv, light01, tint, tris);

            Vector3 b0 = origin + new Vector3(min.x, min.y, max.z);
            Vector3 b1 = origin + new Vector3(max.x, min.y, min.z);
            Vector3 b2 = origin + new Vector3(max.x, max.y, min.z);
            Vector3 b3 = origin + new Vector3(min.x, max.y, max.z);
            AddDoubleSidedShapeQuad(b0, b1, b2, b3, atlasUv, light01, tint, tris);
        }

        private void AddPlaneShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            BlockPlacementAxis placementAxis,
            byte rawPlacementData,
            int voxelX,
            int voxelY,
            int voxelZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            ResolvePlaneSupportFlags(
                voxelX,
                voxelY,
                voxelZ,
                placementAxis,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                out bool hasNegativeSupport,
                out bool hasPositiveSupport);

            BlockShapeUtility.ResolvePlaneQuad(
                mapping,
                placementAxis,
                hasNegativeSupport,
                hasPositiveSupport,
                out Vector3 p0,
                out Vector3 p1,
                out Vector3 p2,
                out Vector3 p3,
                out BlockFace sampledFace,
                out _);

            Vector2Int tile = mapping.GetTileCoord(sampledFace);
            Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
            float tint = mapping.GetTint(sampledFace) ? 1f : 0f;

            NativeList<int> tris = FluidBlockUtility.IsWater(blockType)
                ? waterTriangles
                : (mapping.isTransparent ? transparentTriangles : opaqueTriangles);

            if (blockType == BlockType.wire)
            {
                bool wireHasTopOnCurrentCell = WirePlacementUtility.HasTop(rawPlacementData);
                bool wireHasWallOnCurrentCell = WirePlacementUtility.TryGetWall(rawPlacementData, out BlockPlacementAxis wireSurfaceAxis, out int wireAttachmentSide);
                if (!wireHasWallOnCurrentCell)
                {
                    BlockPlacementAxis resolvedSurfaceAxis = ResolveWireSurfaceAxis(placementAxis, hasNegativeSupport, hasPositiveSupport);
                    if (resolvedSurfaceAxis == BlockPlacementAxis.Y)
                    {
                        wireHasTopOnCurrentCell = true;
                    }
                    else
                    {
                        wireHasWallOnCurrentCell = true;
                        wireSurfaceAxis = resolvedSurfaceAxis;
                        wireAttachmentSide = ResolveWireAttachmentSide(resolvedSurfaceAxis, hasNegativeSupport, hasPositiveSupport);
                    }
                }

                Vector2Int lineTile = mapping.GetTileCoord(BlockFace.Front);
                Vector2 lineAtlasUv = new Vector2(lineTile.x * invAtlasTilesX + 0.001f, lineTile.y * invAtlasTilesY + 0.001f);
                Vector2Int dotTile = ResolveWireDotTile(mapping, lineTile);
                Vector2 dotAtlasUv = new Vector2(dotTile.x * invAtlasTilesX + 0.001f, dotTile.y * invAtlasTilesY + 0.001f);

                if (wireHasTopOnCurrentCell)
                {
                    BlockShapeUtility.ResolvePlaneQuad(
                        mapping,
                        BlockPlacementAxis.Y,
                        false,
                        false,
                        out Vector3 topP0,
                        out Vector3 topP1,
                        out Vector3 topP2,
                        out Vector3 topP3,
                        out _,
                        out _);

                    RenderWireTopSurface(
                        origin,
                        topP0,
                        topP1,
                        topP2,
                        topP3,
                        wireHasWallOnCurrentCell ? wireSurfaceAxis : BlockPlacementAxis.Y,
                        wireHasWallOnCurrentCell ? wireAttachmentSide : 0,
                        lineAtlasUv,
                        dotAtlasUv,
                        light01,
                        tint,
                        tris,
                        voxelX,
                        voxelY,
                        voxelZ,
                        blockTypes,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize);
                }

                if (wireHasWallOnCurrentCell)
                {
                    bool wallHasNegativeSupport = wireAttachmentSide < 0;
                    bool wallHasPositiveSupport = wireAttachmentSide > 0;

                    BlockShapeUtility.ResolvePlaneQuad(
                        mapping,
                        wireSurfaceAxis,
                        wallHasNegativeSupport,
                        wallHasPositiveSupport,
                        out Vector3 wallP0,
                        out Vector3 wallP1,
                        out Vector3 wallP2,
                        out Vector3 wallP3,
                        out _,
                        out _);

                    RenderWireWallSurface(
                        origin,
                        wallP0,
                        wallP1,
                        wallP2,
                        wallP3,
                        wireSurfaceAxis,
                        wireAttachmentSide,
                        wireHasTopOnCurrentCell,
                        lineAtlasUv,
                        dotAtlasUv,
                        light01,
                        tint,
                        tris,
                        voxelX,
                        voxelY,
                        voxelZ,
                        blockTypes,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize);
                }

                return;
            }

            AddDoubleSidedShapeQuad(
                origin + p0,
                origin + p1,
                origin + p2,
                origin + p3,
                atlasUv,
                light01,
                tint,
                tris);
        }

        private void RenderWireTopSurface(
            Vector3 origin,
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            BlockPlacementAxis wallSurfaceAxis,
            int wallAttachmentSide,
            Vector2 lineAtlasUv,
            Vector2 dotAtlasUv,
            float light01,
            float tint,
            NativeList<int> tris,
            int voxelX,
            int voxelY,
            int voxelZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            const float armHalfWidth = 0.12f;
            const float centerHalfSize = 0.18f;

            bool connectEast = IsTopSurfaceWireAt(voxelX + 1, voxelY, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            bool connectWest = IsTopSurfaceWireAt(voxelX - 1, voxelY, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            bool connectNorth = IsTopSurfaceWireAt(voxelX, voxelY, voxelZ + 1, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            bool connectSouth = IsTopSurfaceWireAt(voxelX, voxelY, voxelZ - 1, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            if (wallSurfaceAxis == BlockPlacementAxis.X)
            {
                if (wallAttachmentSide > 0)
                    connectEast = true;
                else if (wallAttachmentSide < 0)
                    connectWest = true;
            }
            else if (wallSurfaceAxis == BlockPlacementAxis.Z)
            {
                if (wallAttachmentSide > 0)
                    connectNorth = true;
                else if (wallAttachmentSide < 0)
                    connectSouth = true;
            }

            bool straightX = connectEast && connectWest && !connectNorth && !connectSouth;
            bool straightZ = connectNorth && connectSouth && !connectEast && !connectWest;

            if (straightX)
            {
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0f, 1f, 0.5f - armHalfWidth, 0.5f + armHalfWidth, lineAtlasUv, light01, tint, tris, 0);
                return;
            }

            if (straightZ)
            {
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - armHalfWidth, 0.5f + armHalfWidth, 0f, 1f, lineAtlasUv, light01, tint, tris, 1);
                return;
            }

            AddWireSurfaceQuad(
                origin,
                p0,
                p1,
                p2,
                p3,
                0.5f - centerHalfSize,
                0.5f + centerHalfSize,
                0.5f - centerHalfSize,
                0.5f + centerHalfSize,
                dotAtlasUv,
                light01,
                tint,
                tris,
                0);

            if (connectEast)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f, 1f, 0.5f - armHalfWidth, 0.5f + armHalfWidth, lineAtlasUv, light01, tint, tris, 0);

            if (connectWest)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0f, 0.5f, 0.5f - armHalfWidth, 0.5f + armHalfWidth, lineAtlasUv, light01, tint, tris, 0);

            if (connectNorth)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - armHalfWidth, 0.5f + armHalfWidth, 0.5f, 1f, lineAtlasUv, light01, tint, tris, 1);

            if (connectSouth)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - armHalfWidth, 0.5f + armHalfWidth, 0f, 0.5f, lineAtlasUv, light01, tint, tris, 1);
        }

        private void RenderWireWallSurface(
            Vector3 origin,
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            BlockPlacementAxis wireSurfaceAxis,
            int wireAttachmentSide,
            bool hasTopOnCurrentCell,
            Vector2 lineAtlasUv,
            Vector2 dotAtlasUv,
            float light01,
            float tint,
            NativeList<int> tris,
            int voxelX,
            int voxelY,
            int voxelZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            const float armHalfWidth = 0.12f;
            const float centerHalfSize = 0.18f;

            ResolveWireConnectionOffsets(
                wireSurfaceAxis,
                out Vector3Int negSOffset,
                out Vector3Int posSOffset,
                out Vector3Int negTOffset,
                out Vector3Int posTOffset);

            bool connectNegS = IsWireConnectedAlongSurface(
                voxelX + negSOffset.x,
                voxelY + negSOffset.y,
                voxelZ + negSOffset.z,
                wireSurfaceAxis,
                wireAttachmentSide,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            bool connectPosS = IsWireConnectedAlongSurface(
                voxelX + posSOffset.x,
                voxelY + posSOffset.y,
                voxelZ + posSOffset.z,
                wireSurfaceAxis,
                wireAttachmentSide,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            bool connectNegT = IsWireConnectedAlongSurface(
                voxelX + negTOffset.x,
                voxelY + negTOffset.y,
                voxelZ + negTOffset.z,
                wireSurfaceAxis,
                wireAttachmentSide,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            bool connectPosT = IsWireConnectedAlongSurface(
                voxelX + posTOffset.x,
                voxelY + posTOffset.y,
                voxelZ + posTOffset.z,
                wireSurfaceAxis,
                wireAttachmentSide,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            if (hasTopOnCurrentCell)
            {
                if (wireSurfaceAxis == BlockPlacementAxis.X)
                    connectPosT = true;
                else if (wireSurfaceAxis == BlockPlacementAxis.Z)
                    connectPosS = true;
            }

            bool straightS = connectNegS && connectPosS && !connectNegT && !connectPosT;
            bool straightT = connectNegT && connectPosT && !connectNegS && !connectPosS;

            if (straightS)
            {
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0f, 1f, 0.5f - armHalfWidth, 0.5f + armHalfWidth, lineAtlasUv, light01, tint, tris, 0);
                return;
            }

            if (straightT)
            {
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - armHalfWidth, 0.5f + armHalfWidth, 0f, 1f, lineAtlasUv, light01, tint, tris, 1);
                return;
            }

            AddWireSurfaceQuad(
                origin,
                p0,
                p1,
                p2,
                p3,
                0.5f - centerHalfSize,
                0.5f + centerHalfSize,
                0.5f - centerHalfSize,
                0.5f + centerHalfSize,
                dotAtlasUv,
                light01,
                tint,
                tris,
                0);

            if (connectPosS)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f, 1f, 0.5f - armHalfWidth, 0.5f + armHalfWidth, lineAtlasUv, light01, tint, tris, 0);

            if (connectNegS)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0f, 0.5f, 0.5f - armHalfWidth, 0.5f + armHalfWidth, lineAtlasUv, light01, tint, tris, 0);

            if (connectPosT)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - armHalfWidth, 0.5f + armHalfWidth, 0.5f, 1f, lineAtlasUv, light01, tint, tris, 1);

            if (connectNegT)
                AddWireSurfaceQuad(origin, p0, p1, p2, p3, 0.5f - armHalfWidth, 0.5f + armHalfWidth, 0f, 0.5f, lineAtlasUv, light01, tint, tris, 1);
        }

        private enum WireTopConnectionMode : byte
        {
            None = 0,
            Flat = 1,
            Up = 2,
            Down = 3
        }

        private WireTopConnectionMode ResolveWireTopConnectionMode(
            int voxelX,
            int voxelY,
            int voxelZ,
            int dirX,
            int dirZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            int nx = voxelX + dirX;
            int nz = voxelZ + dirZ;

            if (IsTopSurfaceWireAt(nx, voxelY, nz, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize))
                return WireTopConnectionMode.Flat;

            if (HasSameLevelWallWireConnectionFromTop(
                    voxelX,
                    voxelY,
                    voxelZ,
                    dirX,
                    dirZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize))
            {
                return WireTopConnectionMode.Down;
            }

            bool adjacentIsSolid = IsSolidSupportBlock(nx, voxelY, nz, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            if (adjacentIsSolid &&
                IsTopSurfaceWireAt(nx, voxelY + 1, nz, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize))
            {
                return WireTopConnectionMode.Up;
            }

            if (!adjacentIsSolid &&
                IsTopSurfaceWireAt(nx, voxelY - 1, nz, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize))
            {
                return WireTopConnectionMode.Down;
            }

            if (HasWallWireConnectionFromTop(
                    voxelX,
                    voxelY,
                    voxelZ,
                    dirX,
                    dirZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize))
            {
                return WireTopConnectionMode.Down;
            }

            return WireTopConnectionMode.None;
        }

        private bool HasSameLevelWallWireConnectionFromTop(
            int voxelX,
            int voxelY,
            int voxelZ,
            int dirX,
            int dirZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            int wallX = voxelX + dirX;
            int wallZ = voxelZ + dirZ;

            if (!TryGetWireSurfaceAt(
                    wallX,
                    voxelY,
                    wallZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    out BlockPlacementAxis wallSurfaceAxis,
                    out int wallAttachmentSide))
            {
                return false;
            }

            if (dirX != 0)
                return wallSurfaceAxis == BlockPlacementAxis.X && wallAttachmentSide == dirX;

            return wallSurfaceAxis == BlockPlacementAxis.Z && wallAttachmentSide == dirZ;
        }

        private bool HasWallWireConnectionFromTop(
            int voxelX,
            int voxelY,
            int voxelZ,
            int dirX,
            int dirZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            return MatchesWallWireForTopConnection(
                       voxelX,
                       voxelY - 1,
                       voxelZ,
                       dirX,
                       dirZ,
                       1,
                       blockTypes,
                       voxelSizeX,
                       voxelSizeZ,
                       voxelPlaneSize)
                || MatchesWallWireForTopConnection(
                       voxelX + dirX,
                       voxelY - 1,
                       voxelZ + dirZ,
                       dirX,
                       dirZ,
                       -1,
                       blockTypes,
                       voxelSizeX,
                       voxelSizeZ,
                       voxelPlaneSize);
        }

        private bool MatchesWallWireForTopConnection(
            int wallX,
            int wallY,
            int wallZ,
            int dirX,
            int dirZ,
            int attachmentDirectionMultiplier,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (!TryGetWireSurfaceAt(
                    wallX,
                    wallY,
                    wallZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    out BlockPlacementAxis wallSurfaceAxis,
                    out int wallAttachmentSide))
            {
                return false;
            }

            if (dirX != 0)
                return wallSurfaceAxis == BlockPlacementAxis.X && wallAttachmentSide == dirX * attachmentDirectionMultiplier;

            return wallSurfaceAxis == BlockPlacementAxis.Z && wallAttachmentSide == dirZ * attachmentDirectionMultiplier;
        }

        private bool IsTopSurfaceWireAt(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            return TryGetWireStateAt(
                       x,
                       y,
                       z,
                       blockTypes,
                       voxelSizeX,
                       voxelSizeZ,
                       voxelPlaneSize,
                       out bool hasTop,
                       out _,
                       out _)
                   && hasTop;
        }

        private bool IsSolidSupportBlock(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            if ((uint)idx >= (uint)blockTypes.Length)
                return false;

            BlockType type = (BlockType)blockTypes[idx];
            if (type == BlockType.Air || FluidBlockUtility.IsWater(type))
                return false;

            int mapIndex = (int)type;
            if ((uint)mapIndex >= (uint)blockMappings.Length)
                return false;

            BlockTextureMapping supportMapping = blockMappings[mapIndex];
            return supportMapping.isSolid && !supportMapping.isEmpty && !supportMapping.isLiquid;
        }

        private bool HasTopWireConnectionForWall(
            int voxelX,
            int voxelY,
            int voxelZ,
            BlockPlacementAxis surfaceAxis,
            int attachmentSide,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (surfaceAxis == BlockPlacementAxis.Y || attachmentSide == 0)
                return false;

            if (IsTopSurfaceWireAt(
                    voxelX,
                    voxelY + 1,
                    voxelZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize))
            {
                return true;
            }

            int supportTopX = voxelX;
            int supportTopZ = voxelZ;
            if (surfaceAxis == BlockPlacementAxis.X)
                supportTopX += attachmentSide;
            else
                supportTopZ += attachmentSide;

            return IsTopSurfaceWireAt(
                supportTopX,
                voxelY + 1,
                supportTopZ,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);
        }

        private bool HasGroundWireConnectionForWall(
            int voxelX,
            int voxelY,
            int voxelZ,
            BlockPlacementAxis surfaceAxis,
            int attachmentSide,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (surfaceAxis == BlockPlacementAxis.Y || attachmentSide == 0)
                return false;

            int outwardX = voxelX;
            int outwardZ = voxelZ;
            if (surfaceAxis == BlockPlacementAxis.X)
                outwardX -= attachmentSide;
            else
                outwardZ -= attachmentSide;

            return IsTopSurfaceWireAt(
                outwardX,
                voxelY,
                outwardZ,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);
        }

        private void AddWireWallGroundBridge(
            Vector3 origin,
            BlockPlacementAxis surfaceAxis,
            Vector2 atlasUv,
            float light01,
            float tint,
            NativeList<int> tris)
        {
            const float armHalfWidth = 0.12f;
            const float floorInset = 0.0015f;

            if (surfaceAxis == BlockPlacementAxis.X)
            {
                AddDoubleSidedShapeQuad(
                    origin + new Vector3(0f, floorInset, 0.5f - armHalfWidth),
                    origin + new Vector3(0f, floorInset, 0.5f + armHalfWidth),
                    origin + new Vector3(1f, floorInset, 0.5f + armHalfWidth),
                    origin + new Vector3(1f, floorInset, 0.5f - armHalfWidth),
                    atlasUv,
                    light01,
                    tint,
                    tris,
                    0);
                return;
            }

            AddDoubleSidedShapeQuad(
                origin + new Vector3(0.5f - armHalfWidth, floorInset, 0f),
                origin + new Vector3(0.5f - armHalfWidth, floorInset, 1f),
                origin + new Vector3(0.5f + armHalfWidth, floorInset, 1f),
                origin + new Vector3(0.5f + armHalfWidth, floorInset, 0f),
                atlasUv,
                light01,
                tint,
                tris,
                1);
        }

        private void AddWireTopVerticalBridge(
            Vector3 origin,
            float baseY,
            int dirX,
            int dirZ,
            bool ascend,
            Vector2 atlasUv,
            float light01,
            float tint,
            NativeList<int> tris)
        {
            const float armHalfWidth = 0.12f;
            const float faceInset = 0.0015f;

            float y0 = ascend ? baseY : baseY - 1f;
            float y1 = ascend ? baseY + 1f : baseY;

            Vector3 p0;
            Vector3 p1;
            Vector3 p2;
            Vector3 p3;

            if (dirX > 0)
            {
                float x = 1f - faceInset;
                float z0 = 0.5f - armHalfWidth;
                float z1 = 0.5f + armHalfWidth;
                p0 = new Vector3(x, y0, z0);
                p1 = new Vector3(x, y1, z0);
                p2 = new Vector3(x, y1, z1);
                p3 = new Vector3(x, y0, z1);
            }
            else if (dirX < 0)
            {
                float x = faceInset;
                float z0 = 0.5f - armHalfWidth;
                float z1 = 0.5f + armHalfWidth;
                p0 = new Vector3(x, y0, z0);
                p1 = new Vector3(x, y1, z0);
                p2 = new Vector3(x, y1, z1);
                p3 = new Vector3(x, y0, z1);
            }
            else if (dirZ > 0)
            {
                float z = 1f - faceInset;
                float x0 = 0.5f - armHalfWidth;
                float x1 = 0.5f + armHalfWidth;
                p0 = new Vector3(x0, y0, z);
                p1 = new Vector3(x0, y1, z);
                p2 = new Vector3(x1, y1, z);
                p3 = new Vector3(x1, y0, z);
            }
            else
            {
                float z = faceInset;
                float x0 = 0.5f - armHalfWidth;
                float x1 = 0.5f + armHalfWidth;
                p0 = new Vector3(x0, y0, z);
                p1 = new Vector3(x0, y1, z);
                p2 = new Vector3(x1, y1, z);
                p3 = new Vector3(x1, y0, z);
            }

            AddDoubleSidedShapeQuad(
                origin + p0,
                origin + p1,
                origin + p2,
                origin + p3,
                atlasUv,
                light01,
                tint,
                tris,
                0);
        }

        private static BlockPlacementAxis ResolveWireSurfaceAxis(
            BlockPlacementAxis placementAxis,
            bool hasNegativeSupport,
            bool hasPositiveSupport)
        {
            placementAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
            return placementAxis switch
            {
                BlockPlacementAxis.X => hasNegativeSupport || hasPositiveSupport ? BlockPlacementAxis.X : BlockPlacementAxis.Y,
                BlockPlacementAxis.Z => hasNegativeSupport || hasPositiveSupport ? BlockPlacementAxis.Z : BlockPlacementAxis.Y,
                _ => BlockPlacementAxis.Y
            };
        }

        private static int ResolveWireAttachmentSide(
            BlockPlacementAxis surfaceAxis,
            bool hasNegativeSupport,
            bool hasPositiveSupport)
        {
            if (surfaceAxis == BlockPlacementAxis.Y)
                return 0;

            if (hasNegativeSupport == hasPositiveSupport)
                return 0;

            return hasNegativeSupport ? -1 : 1;
        }

        private static void ResolveWireConnectionOffsets(
            BlockPlacementAxis surfaceAxis,
            out Vector3Int negSOffset,
            out Vector3Int posSOffset,
            out Vector3Int negTOffset,
            out Vector3Int posTOffset)
        {
            switch (surfaceAxis)
            {
                case BlockPlacementAxis.X:
                    negSOffset = new Vector3Int(0, 0, -1);
                    posSOffset = new Vector3Int(0, 0, 1);
                    negTOffset = new Vector3Int(0, -1, 0);
                    posTOffset = new Vector3Int(0, 1, 0);
                    return;

                case BlockPlacementAxis.Z:
                    negSOffset = new Vector3Int(0, -1, 0);
                    posSOffset = new Vector3Int(0, 1, 0);
                    negTOffset = new Vector3Int(-1, 0, 0);
                    posTOffset = new Vector3Int(1, 0, 0);
                    return;

                default:
                    negSOffset = new Vector3Int(-1, 0, 0);
                    posSOffset = new Vector3Int(1, 0, 0);
                    negTOffset = new Vector3Int(0, 0, -1);
                    posTOffset = new Vector3Int(0, 0, 1);
                    return;
            }
        }

        private bool IsWireConnectedAlongSurface(
            int neighborX,
            int neighborY,
            int neighborZ,
            BlockPlacementAxis surfaceAxis,
            int attachmentSide,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (!TryGetWireSurfaceAt(
                    neighborX,
                    neighborY,
                    neighborZ,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    out BlockPlacementAxis neighborSurfaceAxis,
                    out int neighborAttachmentSide))
            {
                return false;
            }

            return AreWireSurfacesCompatible(
                surfaceAxis,
                attachmentSide,
                neighborSurfaceAxis,
                neighborAttachmentSide);
        }

        private bool TryGetWireStateAt(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            out bool hasTop,
            out BlockPlacementAxis wallSurfaceAxis,
            out int wallAttachmentSide)
        {
            hasTop = false;
            wallSurfaceAxis = BlockPlacementAxis.Y;
            wallAttachmentSide = 0;

            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            if ((uint)idx >= (uint)blockTypes.Length)
                return false;

            if ((BlockType)blockTypes[idx] != BlockType.wire)
                return false;

            byte rawPlacementData = GetBlockPlacementAxisValue(idx);
            hasTop = WirePlacementUtility.HasTop(rawPlacementData);
            if (WirePlacementUtility.TryGetWall(rawPlacementData, out wallSurfaceAxis, out wallAttachmentSide))
                return true;

            BlockPlacementAxis neighborPlacementAxis = BlockPlacementRotationUtility.SanitizeStoredAxis((BlockPlacementAxis)rawPlacementData);
            ResolvePlaneSupportFlags(
                x,
                y,
                z,
                neighborPlacementAxis,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                out bool hasNegativeSupport,
                out bool hasPositiveSupport);

            BlockPlacementAxis resolvedSurfaceAxis = ResolveWireSurfaceAxis(neighborPlacementAxis, hasNegativeSupport, hasPositiveSupport);
            if (resolvedSurfaceAxis == BlockPlacementAxis.Y)
            {
                hasTop = true;
                return true;
            }

            wallSurfaceAxis = resolvedSurfaceAxis;
            wallAttachmentSide = ResolveWireAttachmentSide(resolvedSurfaceAxis, hasNegativeSupport, hasPositiveSupport);
            return true;
        }

        private bool TryGetWireSurfaceAt(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            out BlockPlacementAxis surfaceAxis,
            out int attachmentSide)
        {
            surfaceAxis = BlockPlacementAxis.Y;
            attachmentSide = 0;

            if (!TryGetWireStateAt(
                    x,
                    y,
                    z,
                    blockTypes,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    out bool hasTop,
                    out BlockPlacementAxis wallSurfaceAxis,
                    out int wallAttachmentSide))
            {
                return false;
            }

            if (wallSurfaceAxis != BlockPlacementAxis.Y)
            {
                surfaceAxis = wallSurfaceAxis;
                attachmentSide = wallAttachmentSide;
                return true;
            }

            if (!hasTop)
                return false;

            surfaceAxis = BlockPlacementAxis.Y;
            return true;
        }

        private static bool AreWireSurfacesCompatible(
            BlockPlacementAxis surfaceAxis,
            int attachmentSide,
            BlockPlacementAxis neighborSurfaceAxis,
            int neighborAttachmentSide)
        {
            if (surfaceAxis != neighborSurfaceAxis)
                return false;

            if (surfaceAxis == BlockPlacementAxis.Y)
                return true;

            if (attachmentSide == 0 || neighborAttachmentSide == 0)
                return true;

            return attachmentSide == neighborAttachmentSide;
        }

        private static Vector2Int ResolveWireDotTile(BlockTextureMapping mapping, Vector2Int lineTile)
        {
            Vector2Int configuredDotTile = mapping.GetTileCoord(BlockFace.Top);
            if (configuredDotTile != lineTile)
                return configuredDotTile;

            if (lineTile.x > 0)
                return new Vector2Int(lineTile.x - 1, lineTile.y);

            return lineTile;
        }

        private void AddWireSurfaceQuad(
            Vector3 origin,
            Vector3 planeP0,
            Vector3 planeP1,
            Vector3 planeP2,
            Vector3 planeP3,
            float minS,
            float maxS,
            float minT,
            float maxT,
            Vector2 atlasUv,
            float light01,
            float tint,
            NativeList<int> tris,
            int uvQuarterTurns)
        {
            Vector3 axisS = planeP3 - planeP0;
            Vector3 axisT = planeP1 - planeP0;
            Vector3 q0 = planeP0 + axisS * minS + axisT * minT;
            Vector3 q1 = planeP0 + axisS * minS + axisT * maxT;
            Vector3 q2 = planeP0 + axisS * maxS + axisT * maxT;
            Vector3 q3 = planeP0 + axisS * maxS + axisT * minT;

            AddDoubleSidedShapeQuad(
                origin + q0,
                origin + q1,
                origin + q2,
                origin + q3,
                atlasUv,
                light01,
                tint,
                tris,
                uvQuarterTurns);
        }

        private void ResolvePlaneSupportFlags(
            int voxelX,
            int voxelY,
            int voxelZ,
            BlockPlacementAxis placementAxis,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            out bool hasNegativeSupport,
            out bool hasPositiveSupport)
        {
            hasNegativeSupport = false;
            hasPositiveSupport = false;

            switch (BlockPlacementRotationUtility.SanitizeAxis(placementAxis))
            {
                case BlockPlacementAxis.X:
                    hasNegativeSupport = IsPlaneSupportBlock(voxelX - 1, voxelY, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                    hasPositiveSupport = IsPlaneSupportBlock(voxelX + 1, voxelY, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                    break;

                case BlockPlacementAxis.Z:
                    hasNegativeSupport = IsPlaneSupportBlock(voxelX, voxelY, voxelZ - 1, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                    hasPositiveSupport = IsPlaneSupportBlock(voxelX, voxelY, voxelZ + 1, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                    break;
            }
        }

        private bool IsPlaneSupportBlock(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            BlockType type = (BlockType)blockTypes[idx];
            if (type == BlockType.Air || FluidBlockUtility.IsWater(type))
                return false;

            int mapIndex = (int)type;
            if ((uint)mapIndex >= (uint)blockMappings.Length)
                return false;

            BlockTextureMapping mapping = blockMappings[mapIndex];
            return mapping.isSolid && !mapping.isEmpty && !mapping.isLiquid;
        }

        private void AddCuboidShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            int voxelX,
            int voxelY,
            int voxelZ,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

            NativeList<int> tris = FluidBlockUtility.IsWater(blockType)
                ? waterTriangles
                : (mapping.isTransparent ? transparentTriangles : opaqueTriangles);
            byte emission = mapping.lightEmission;

            if (IsWallTorch(blockType))
            {
                float resolvedLight01 = math.max(light01, emission / 15f);
                AddWallTorchShape(origin, mapping, blockType, invAtlasTilesX, invAtlasTilesY, resolvedLight01, tris);
                return;
            }

            AddShapeFace(
                origin + new Vector3(max.x, min.y, min.z),
                origin + new Vector3(max.x, max.y, min.z),
                origin + new Vector3(max.x, max.y, max.z),
                origin + new Vector3(max.x, min.y, max.z),
                Vector3.right,
                mapping.GetTileCoord(BlockFace.Right),
                mapping.GetTint(BlockFace.Right),
                new Vector3Int(voxelX + 1, voxelY, voxelZ),
                Vector3Int.up,
                Vector3Int.forward,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(min.x, min.y, max.z),
                origin + new Vector3(min.x, max.y, max.z),
                origin + new Vector3(min.x, max.y, min.z),
                origin + new Vector3(min.x, min.y, min.z),
                Vector3.left,
                mapping.GetTileCoord(BlockFace.Left),
                mapping.GetTint(BlockFace.Left),
                new Vector3Int(voxelX, voxelY, voxelZ),
                Vector3Int.up,
                Vector3Int.forward,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(min.x, max.y, max.z),
                origin + new Vector3(max.x, max.y, max.z),
                origin + new Vector3(max.x, max.y, min.z),
                origin + new Vector3(min.x, max.y, min.z),
                Vector3.up,
                mapping.GetTileCoord(BlockFace.Top),
                mapping.GetTint(BlockFace.Top),
                new Vector3Int(voxelX, voxelY + 1, voxelZ),
                Vector3Int.right,
                Vector3Int.forward,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(min.x, min.y, min.z),
                origin + new Vector3(max.x, min.y, min.z),
                origin + new Vector3(max.x, min.y, max.z),
                origin + new Vector3(min.x, min.y, max.z),
                Vector3.down,
                mapping.GetTileCoord(BlockFace.Bottom),
                mapping.GetTint(BlockFace.Bottom),
                new Vector3Int(voxelX, voxelY, voxelZ),
                Vector3Int.right,
                Vector3Int.forward,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(max.x, min.y, max.z),
                origin + new Vector3(max.x, max.y, max.z),
                origin + new Vector3(min.x, max.y, max.z),
                origin + new Vector3(min.x, min.y, max.z),
                Vector3.forward,
                mapping.GetTileCoord(BlockFace.Front),
                mapping.GetTint(BlockFace.Front),
                new Vector3Int(voxelX, voxelY, voxelZ + 1),
                Vector3Int.right,
                Vector3Int.up,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(min.x, min.y, min.z),
                origin + new Vector3(min.x, max.y, min.z),
                origin + new Vector3(max.x, max.y, min.z),
                origin + new Vector3(max.x, min.y, min.z),
                Vector3.back,
                mapping.GetTileCoord(BlockFace.Back),
                mapping.GetTint(BlockFace.Back),
                new Vector3Int(voxelX, voxelY, voxelZ),
                Vector3Int.right,
                Vector3Int.up,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);
        }

        private void AddWallTorchShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01,
            NativeList<int> tris)
        {
            ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

            Vector3 modelMin = new Vector3(min.x - 0.5f, min.y, min.z - 0.5f);
            Vector3 modelMax = new Vector3(max.x - 0.5f, max.y, max.z - 0.5f);

            Vector3 p000 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMin.x, modelMin.y, modelMin.z));
            Vector3 p001 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMin.x, modelMin.y, modelMax.z));
            Vector3 p010 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMin.x, modelMax.y, modelMin.z));
            Vector3 p011 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMin.x, modelMax.y, modelMax.z));
            Vector3 p100 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMax.x, modelMin.y, modelMin.z));
            Vector3 p101 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMax.x, modelMin.y, modelMax.z));
            Vector3 p110 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMax.x, modelMax.y, modelMin.z));
            Vector3 p111 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMax.x, modelMax.y, modelMax.z));

            AddStaticLitShapeFace(p100, p110, p111, p101, mapping.GetTileCoord(BlockFace.Right), mapping.GetTint(BlockFace.Right), light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddStaticLitShapeFace(p001, p011, p010, p000, mapping.GetTileCoord(BlockFace.Left), mapping.GetTint(BlockFace.Left), light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddStaticLitShapeFace(p011, p111, p110, p010, mapping.GetTileCoord(BlockFace.Top), mapping.GetTint(BlockFace.Top), light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddStaticLitShapeFace(p000, p100, p101, p001, mapping.GetTileCoord(BlockFace.Bottom), mapping.GetTint(BlockFace.Bottom), light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddStaticLitShapeFace(p101, p111, p011, p001, mapping.GetTileCoord(BlockFace.Front), mapping.GetTint(BlockFace.Front), light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddStaticLitShapeFace(p000, p010, p110, p100, mapping.GetTileCoord(BlockFace.Back), mapping.GetTint(BlockFace.Back), light01, invAtlasTilesX, invAtlasTilesY, tris);
        }

        private void AddStaticLitShapeFace(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2Int tile,
            bool tint,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            int vIndex = GetCurrentSubchunkLocalVertexIndex();
            Vector3 normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
            if (normal.sqrMagnitude < 0.0001f)
                normal = Vector3.up;

            Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
            Vector4 extra = new Vector4(light01, tint ? 1f : 0f, 1f, 0f);
            AddPackedVertex(p0, normal, new Vector2(0f, 0f), atlasUv, extra);
            AddPackedVertex(p1, normal, new Vector2(1f, 0f), atlasUv, extra);
            AddPackedVertex(p2, normal, new Vector2(1f, 1f), atlasUv, extra);
            AddPackedVertex(p3, normal, new Vector2(0f, 1f), atlasUv, extra);

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);
        }

        private static bool IsWallTorch(BlockType blockType)
        {
            return blockType == BlockType.WallTorchEast ||
                   blockType == BlockType.WallTorchWest ||
                   blockType == BlockType.WallTorchSouth ||
                   blockType == BlockType.WallTorchNorth;
        }

        private static Vector3 TransformTorchModelPoint(BlockType blockType, Vector3 modelPoint)
        {
            if (!IsWallTorch(blockType))
                return modelPoint + new Vector3(0.5f, 0f, 0.5f);

            const float angleRadians = 0.3926991f;
            const float anchorHeight = TorchPlacementUtility.WallAnchorHeight;
            const float anchorOffset = TorchPlacementUtility.WallAnchorOffset;

            float sin = math.sin(angleRadians);
            float cos = math.cos(angleRadians);

            Vector3 rotated = modelPoint;
            switch (blockType)
            {
                case BlockType.WallTorchEast:
                    rotated = new Vector3(
                        modelPoint.x * cos + modelPoint.y * sin,
                        -modelPoint.x * sin + modelPoint.y * cos,
                        modelPoint.z);
                    return rotated + new Vector3(0.5f - anchorOffset, anchorHeight, 0.5f);

                case BlockType.WallTorchWest:
                    rotated = new Vector3(
                        modelPoint.x * cos - modelPoint.y * sin,
                        modelPoint.x * sin + modelPoint.y * cos,
                        modelPoint.z);
                    return rotated + new Vector3(0.5f + anchorOffset, anchorHeight, 0.5f);

                case BlockType.WallTorchSouth:
                    rotated = new Vector3(
                        modelPoint.x,
                        modelPoint.y * cos - modelPoint.z * sin,
                        modelPoint.y * sin + modelPoint.z * cos);
                    return rotated + new Vector3(0.5f, anchorHeight, 0.5f - anchorOffset);

                case BlockType.WallTorchNorth:
                    rotated = new Vector3(
                        modelPoint.x,
                        modelPoint.y * cos + modelPoint.z * sin,
                        -modelPoint.y * sin + modelPoint.z * cos);
                    return rotated + new Vector3(0.5f, anchorHeight, 0.5f + anchorOffset);

                default:
                    return modelPoint + new Vector3(0.5f, 0f, 0.5f);
            }
        }

        private void AddShapeFace(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector3 normal,
            Vector2Int tile,
            bool tint,
            Vector3Int lightPlanePos,
            Vector3Int lightStepU,
            Vector3Int lightStepV,
            byte emission,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            int vertexGlobalStart = vertices.Length;
            int vIndex = GetCurrentSubchunkLocalVertexIndex();
            Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
            AddPackedVertex(p0, normal, new Vector2(0f, 0f), atlasUv, default);
            AddPackedVertex(p1, normal, new Vector2(1f, 0f), atlasUv, default);
            AddPackedVertex(p2, normal, new Vector2(1f, 1f), atlasUv, default);
            AddPackedVertex(p3, normal, new Vector2(0f, 1f), atlasUv, default);

            for (int corner = 0; corner < 4; corner++)
            {
                Vector3Int stepU = (corner == 1 || corner == 2) ? lightStepU : -lightStepU;
                Vector3Int stepV = (corner == 2 || corner == 3) ? lightStepV : -lightStepV;
                byte vertexLight = GetVertexLight(lightPlanePos, stepU, stepV, light, SizeX + 2 * border, SizeZ + 2 * border, (SizeX + 2 * border) * SizeY);
                if (emission > 0)
                    vertexLight = (byte)math.max((int)vertexLight, (int)emission);

                PackedChunkVertex vertex = vertices[vertexGlobalStart + corner];
                vertex.uv2 = new Vector4(vertexLight / 15f, tint ? 1f : 0f, 1f, 0f);
                vertices[vertexGlobalStart + corner] = vertex;
            }

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);
        }

        private static void ResolveShapeQuadUv(
            int uvQuarterTurns,
            out Vector2 uv0,
            out Vector2 uv1,
            out Vector2 uv2,
            out Vector2 uv3)
        {
            switch (uvQuarterTurns & 3)
            {
                case 1:
                    uv0 = new Vector2(0f, 1f);
                    uv1 = new Vector2(0f, 0f);
                    uv2 = new Vector2(1f, 0f);
                    uv3 = new Vector2(1f, 1f);
                    return;

                case 2:
                    uv0 = new Vector2(1f, 1f);
                    uv1 = new Vector2(0f, 1f);
                    uv2 = new Vector2(0f, 0f);
                    uv3 = new Vector2(1f, 0f);
                    return;

                case 3:
                    uv0 = new Vector2(1f, 0f);
                    uv1 = new Vector2(1f, 1f);
                    uv2 = new Vector2(0f, 1f);
                    uv3 = new Vector2(0f, 0f);
                    return;

                default:
                    uv0 = new Vector2(0f, 0f);
                    uv1 = new Vector2(1f, 0f);
                    uv2 = new Vector2(1f, 1f);
                    uv3 = new Vector2(0f, 1f);
                    return;
            }
        }

        private void AddDoubleSidedShapeQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2 atlasUv,
            float light01,
            float tint,
            NativeList<int> tris,
            int uvQuarterTurns = 0)
        {
            int vIndex = GetCurrentSubchunkLocalVertexIndex();
            Vector3 planeNormal = ComputeQuadPlaneNormal(p0, p1, p2);
            ResolveShapeQuadUv(uvQuarterTurns, out Vector2 uv0, out Vector2 uv1, out Vector2 uv2, out Vector2 uv3);

            Vector4 e = new Vector4(light01, tint, 1f, 0f);
            AddPackedVertex(p0, planeNormal, uv0, atlasUv, e);
            AddPackedVertex(p1, planeNormal, uv1, atlasUv, e);
            AddPackedVertex(p2, planeNormal, uv2, atlasUv, e);
            AddPackedVertex(p3, planeNormal, uv3, atlasUv, e);

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 3);
            tris.Add(vIndex + 2);
        }

        private float GetSpecialMeshLight01(
            int x,
            int y,
            int z,
            int voxelSizeX,
            int voxelSizeZ,
            NativeArray<byte> light)
        {
            float light01 = SampleSpecialMeshLight01(x, y, z, voxelSizeX, voxelSizeZ, light);
            light01 = math.max(light01, SampleSpecialMeshLight01(x, y + 1, z, voxelSizeX, voxelSizeZ, light));
            light01 = math.max(light01, SampleSpecialMeshLight01(x + 1, y, z, voxelSizeX, voxelSizeZ, light));
            light01 = math.max(light01, SampleSpecialMeshLight01(x - 1, y, z, voxelSizeX, voxelSizeZ, light));
            light01 = math.max(light01, SampleSpecialMeshLight01(x, y, z + 1, voxelSizeX, voxelSizeZ, light));
            light01 = math.max(light01, SampleSpecialMeshLight01(x, y, z - 1, voxelSizeX, voxelSizeZ, light));
            return light01;
        }

        private float SampleSpecialMeshLight01(
            int x,
            int y,
            int z,
            int voxelSizeX,
            int voxelSizeZ,
            NativeArray<byte> light)
        {
            if ((uint)x >= (uint)voxelSizeX ||
                (uint)y >= (uint)SizeY ||
                (uint)z >= (uint)voxelSizeZ)
            {
                return 0f;
            }

            int voxelPlaneSize = voxelSizeX * SizeY;
            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            if ((uint)idx >= (uint)light.Length)
                return 0f;

            return GetResolvedLight01(light[idx]);
        }

        private static float GetResolvedLight01(byte packed)
        {
            byte lightValue = (byte)math.max(
                (int)LightUtils.GetSkyLight(packed),
                (int)LightUtils.GetBlockLight(packed));
            return lightValue / 15f;
        }

        private void ResolveShapeBounds(BlockTextureMapping mapping, out Vector3 min, out Vector3 max)
        {
            float3 clampedMin = math.clamp(
                new float3(mapping.shapeMin.x, mapping.shapeMin.y, mapping.shapeMin.z),
                0f,
                1f);
            float3 clampedMax = math.clamp(
                new float3(mapping.shapeMax.x, mapping.shapeMax.y, mapping.shapeMax.z),
                0f,
                1f);

            bool valid =
                clampedMax.x > clampedMin.x + 0.0001f &&
                clampedMax.y > clampedMin.y + 0.0001f &&
                clampedMax.z > clampedMin.z + 0.0001f;

            if (valid)
            {
                min = new Vector3(clampedMin.x, clampedMin.y, clampedMin.z);
                max = new Vector3(clampedMax.x, clampedMax.y, clampedMax.z);
                return;
            }

            switch (BlockShapeUtility.GetEffectiveRenderShape(mapping))
            {
                case BlockRenderShape.Cross:
                    min = new Vector3(0.15f, 0f, 0.15f);
                    max = new Vector3(0.85f, 1f, 0.85f);
                    return;

                case BlockRenderShape.Cuboid:
                    min = new Vector3(0.375f, 0f, 0.375f);
                    max = new Vector3(0.625f, 0.75f, 0.625f);
                    return;

                case BlockRenderShape.Plane:
                    min = new Vector3(0f, 0f, 0f);
                    max = new Vector3(1f, 0.0625f, 1f);
                    return;

                default:
                    min = Vector3.zero;
                    max = Vector3.one;
                    return;
            }
        }

        private bool IsFaceVisibleForCurrentBlock(BlockType current, BlockType neighbor)
        {
            if (FluidBlockUtility.IsWater(current) && FluidBlockUtility.IsWater(neighbor))
                return false;

            if (current == neighbor && blockMappings[(int)current].isTransparent)
                return false;

            if (blockMappings[(int)neighbor].isEmpty)
                return true;

            bool neighborOpaque = BlockShapeUtility.GetEffectiveRenderShape(blockMappings[(int)neighbor]) == BlockRenderShape.Cube &&
                                  blockMappings[(int)neighbor].isSolid &&
                                  !blockMappings[(int)neighbor].isTransparent;
            return !neighborOpaque;
        }

        private void GenerateMesh(NativeArray<int> heightCache, NativeArray<byte> blockTypes, NativeArray<bool> solids, NativeArray<byte> light, float invAtlasTilesX, float invAtlasTilesY)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;

            int maxMask = math.max(voxelSizeX * SizeY, math.max(voxelSizeX * voxelSizeZ, SizeY * voxelSizeZ));
            NativeArray<GreedyFaceData> mask = new NativeArray<GreedyFaceData>(maxMask, Allocator.Temp);

            for (int axis = 0; axis < 3; axis++)
            {
                for (int side = 0; side < 2; side++)
                {
                    int normalSign = side == 0 ? 1 : -1;

                    int u = (axis + 1) % 3;
                    int v = (axis + 2) % 3;

                    int sizeU = u == 0 ? voxelSizeX : u == 1 ? SizeY : voxelSizeZ;
                    int sizeV = v == 0 ? voxelSizeX : v == 1 ? SizeY : voxelSizeZ;
                    int chunkSize = axis == 0 ? SizeX : axis == 1 ? SizeY : SizeZ;

                    int minN = axis == 1 ? startY : border;
                    int maxN = axis == 1 ? endY : border + chunkSize;

                    int minU = u == 1 ? startY : border;
                    int maxU = u == 1 ? endY : (u == 0 ? border + SizeX : border + SizeZ);

                    int minV = v == 1 ? startY : border;
                    int maxV = v == 1 ? endY : (v == 0 ? border + SizeX : border + SizeZ);

                    Vector3 normal = new Vector3(axis == 0 ? normalSign : 0, axis == 1 ? normalSign : 0, axis == 2 ? normalSign : 0);
                    BlockFace faceType = BlockFaceUtility.FromAxisNormal(axis, normalSign);

                    Vector3Int stepU = new Vector3Int(u == 0 ? 1 : 0, u == 1 ? 1 : 0, u == 2 ? 1 : 0);
                    Vector3Int stepV = new Vector3Int(v == 0 ? 1 : 0, v == 1 ? 1 : 0, v == 2 ? 1 : 0);

                    for (int n = minN; n < maxN; n++)
                    {
                        for (int j = minV; j < maxV; j++)
                        {
                            for (int i = minU; i < maxU; i++)
                            {
                                int x = u == 0 ? i : v == 0 ? j : n;
                                int y = u == 1 ? i : v == 1 ? j : n;
                                int z = u == 2 ? i : v == 2 ? j : n;

                                int maskIndex = i + j * sizeU;
                                int idx = x + y * voxelSizeX + z * voxelPlaneSize;
                                BlockType current = (BlockType)blockTypes[idx];

                                if (current == BlockType.Air)
                                {
                                    mask[maskIndex] = default;
                                    continue;
                                }

                                BlockTextureMapping currentMapping = blockMappings[(int)current];
                                if (enableUltraLeafBillboards && current == BlockType.Leaves)
                                {
                                    mask[maskIndex] = default;
                                    continue;
                                }

                                if (BlockShapeUtility.GetEffectiveRenderShape(currentMapping) != BlockRenderShape.Cube)
                                {
                                    mask[maskIndex] = default;
                                    continue;
                                }

                                int nx = x + (axis == 0 ? normalSign : 0);
                                int ny = y + (axis == 1 ? normalSign : 0);
                                int nz = z + (axis == 2 ? normalSign : 0);

                                bool outside = nx < 0 || nx >= voxelSizeX || ny < 0 || ny >= SizeY || nz < 0 || nz >= voxelSizeZ;
                                bool isVisible;
                                if (outside)
                                {
                                    isVisible = true;
                                }
                                else
                                {
                                    if (!IsVoxelSampleKnown(nx, ny, nz, voxelSizeX, voxelSizeZ, voxelPlaneSize))
                                    {
                                        mask[maskIndex] = default;
                                        continue;
                                    }

                                    int nIdx = nx + ny * voxelSizeX + nz * voxelPlaneSize;
                                    BlockType neighbor = (BlockType)blockTypes[nIdx];
                                    isVisible = IsFaceVisibleForCurrentBlock(current, neighbor);
                                }

                                if (!isVisible)
                                {
                                    mask[maskIndex] = default;
                                    continue;
                                }

                                byte packed = outside
                                    ? LightUtils.PackLight(15, 0)
                                    : light[nx + ny * voxelSizeX + nz * voxelPlaneSize];

                                byte faceLight = (byte)math.max(
                                    (int)LightUtils.GetSkyLight(packed),
                                    (int)LightUtils.GetBlockLight(packed)
                                );

                                int aoPlaneN = n + normalSign;
                                Vector3Int aoPos = new Vector3Int(
                                    axis == 0 ? aoPlaneN : x,
                                    axis == 1 ? aoPlaneN : y,
                                    axis == 2 ? aoPlaneN : z
                                );

                                byte ao0;
                                byte ao1;
                                byte ao2;
                                byte ao3;
                                bool disableAOForCurrentBlock = aoStrength <= 0f || IsEmissiveBlock(currentMapping);
                                if (disableAOForCurrentBlock)
                                {
                                    ao0 = 3;
                                    ao1 = 3;
                                    ao2 = 3;
                                    ao3 = 3;
                                }
                                else
                                {
                                    ao0 = GetVertexAO(aoPos, -stepU, -stepV, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    ao1 = GetVertexAO(aoPos, stepU, -stepV, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    ao2 = GetVertexAO(aoPos, stepU, stepV, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    ao3 = GetVertexAO(aoPos, -stepU, stepV, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                }

                                byte light0 = GetVertexLight(aoPos, -stepU, -stepV, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                byte light1 = GetVertexLight(aoPos, stepU, -stepV, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                byte light2 = GetVertexLight(aoPos, stepU, stepV, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                byte light3 = GetVertexLight(aoPos, -stepU, stepV, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                if (IsEmissiveBlock(currentMapping))
                                {
                                    byte emission = currentMapping.lightEmission;
                                    light0 = (byte)math.max((int)light0, (int)emission);
                                    light1 = (byte)math.max((int)light1, (int)emission);
                                    light2 = (byte)math.max((int)light2, (int)emission);
                                    light3 = (byte)math.max((int)light3, (int)emission);
                                }

                                light0 = (byte)math.max((int)light0, (int)faceLight);
                                light1 = (byte)math.max((int)light1, (int)faceLight);
                                light2 = (byte)math.max((int)light2, (int)faceLight);
                                light3 = (byte)math.max((int)light3, (int)faceLight);
                                byte placementAxis = GetBlockPlacementAxisValue(idx);

                                mask[maskIndex] = new GreedyFaceData
                                {
                                    blockId = (byte)current,
                                    placementAxis = placementAxis,
                                    valid = 1,
                                    faceLight = faceLight,
                                    surfaceHeight = 0,
                                    ao0 = ao0,
                                    ao1 = ao1,
                                    ao2 = ao2,
                                    ao3 = ao3,
                                    light0 = light0,
                                    light1 = light1,
                                    light2 = light2,
                                    light3 = light3
                                };
                            }
                        }

                        for (int j = minV; j < maxV; j++)
                        {
                            int i = minU;
                            while (i < maxU)
                            {
                                GreedyFaceData startFace = mask[i + j * sizeU];
                                if (!HasFace(startFace))
                                {
                                    i++;
                                    continue;
                                }

                                bool isWaterFace = FluidBlockUtility.IsWater((BlockType)startFace.blockId);
                                int w = 1;
                                while (!isWaterFace && i + w < maxU && CanMergeAlongU(mask[i + w - 1 + j * sizeU], mask[i + w + j * sizeU]))
                                    w++;

                                int h = 1;
                                while (!isWaterFace && j + h < maxV)
                                {
                                    bool canGrow = true;
                                    for (int k = 0; k < w; k++)
                                    {
                                        GreedyFaceData candidate = mask[i + k + (j + h) * sizeU];
                                        if (!HasFace(candidate) ||
                                            !CanMergeAlongV(mask[i + k + (j + h - 1) * sizeU], candidate) ||
                                            (k > 0 && !CanMergeAlongU(mask[i + k - 1 + (j + h) * sizeU], candidate)))
                                        {
                                            canGrow = false;
                                            break;
                                        }
                                    }

                                    if (!canGrow)
                                        break;

                                    h++;
                                }

                                bool flipTriangle = (startFace.ao0 + startFace.ao2) > (startFace.ao1 + startFace.ao3);
                                if (!useFastBedrockStyleMeshing)
                                {
                                    int maxW = w;
                                    int maxH = h;
                                    int bestW = 1;
                                    int bestH = 1;
                                    int bestArea = 1;

                                    // Keep the largest sub-rectangle whose AO and light gradient still fit one quad.
                                    for (int testH = 1; testH <= maxH; testH++)
                                    {
                                        for (int testW = 1; testW <= maxW; testW++)
                                        {
                                            int area = testW * testH;
                                            if (area < bestArea || (area == bestArea && testW <= bestW))
                                                continue;

                                            if (!TryGetRepresentableRectFlip(mask, sizeU, i, j, testW, testH, out bool candidateFlip))
                                                continue;

                                            bestW = testW;
                                            bestH = testH;
                                            bestArea = area;
                                            flipTriangle = candidateFlip;
                                        }
                                    }

                                    w = bestW;
                                    h = bestH;
                                }
                                else
                                {
                                    TryGetRepresentableRectFast(mask, sizeU, i, j, w, h, out w, out h, out flipTriangle);
                                }

                                GreedyFaceData bottomLeftFace = mask[i + j * sizeU];
                                GreedyFaceData bottomRightFace = mask[i + (w - 1) + j * sizeU];
                                GreedyFaceData topRightFace = mask[i + (w - 1) + (j + h - 1) * sizeU];
                                GreedyFaceData topLeftFace = mask[i + (j + h - 1) * sizeU];

                                BlockType bt = (BlockType)bottomLeftFace.blockId;
                                byte ao0 = bottomLeftFace.ao0;
                                byte ao1 = bottomRightFace.ao1;
                                byte ao2 = topRightFace.ao2;
                                byte ao3 = topLeftFace.ao3;
                                byte light0 = bottomLeftFace.light0;
                                byte light1 = bottomRightFace.light1;
                                byte light2 = topRightFace.light2;
                                byte light3 = topLeftFace.light3;
                                int baseBlockY = u == 1 ? i : v == 1 ? j : n;
                                int blockX = u == 0 ? i : v == 0 ? j : n;
                                int blockY = u == 1 ? i : v == 1 ? j : n;
                                int blockZ = u == 2 ? i : v == 2 ? j : n;

                                int vIndex = GetCurrentSubchunkLocalVertexIndex();
                                BlockTextureMapping m = blockMappings[(int)bt];
                                BlockPlacementAxis placementAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(bottomLeftFace.placementAxis);
                                BlockFace sampledFace = BlockPlacementRotationUtility.ResolveFaceForPlacement(m, faceType, placementAxis);
                                BlockPlacementAxis uvPlacementAxis = ResolveUvPlacementAxis(m, placementAxis);
                                BlockFace uvSamplingFace = ResolveUvSamplingFace(m, faceType, sampledFace);
                                bool tint = m.GetTint(sampledFace);
                                bool useGrassSideOverlay =
                                    bt == BlockType.Grass &&
                                    sampledFace != BlockFace.Top &&
                                    sampledFace != BlockFace.Bottom;
                                int faceSubchunkIndex = math.clamp(baseBlockY / Chunk.SubchunkHeight, 0, Chunk.SubchunksPerColumn - 1);
                                float packedSubchunkAndOverlay =
                                    faceSubchunkIndex + (useGrassSideOverlay ? 0.25f : 0f);
                                Vector2Int tile = m.GetTileCoord(sampledFace);
                                Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);

                                for (int l = 0; l < 4; l++)
                                {
                                    int du = (l == 1 || l == 2) ? w : 0;
                                    int dv = (l == 2 || l == 3) ? h : 0;

                                    float rawU = i + du;
                                    float rawV = j + dv;
                                    float posD = n + (normalSign > 0 ? 1f : 0f);
                                    int cornerUOffset = du > 0 ? 1 : 0;
                                    int cornerVOffset = dv > 0 ? 1 : 0;

                                    float px = (u == 0 ? rawU : v == 0 ? rawV : posD) - border;
                                    float py = (u == 1 ? rawU : v == 1 ? rawV : posD);
                                    float pz = (u == 2 ? rawU : v == 2 ? rawV : posD) - border;

                                    if (FluidBlockUtility.IsWater(bt) && py > baseBlockY + 0.5f)
                                        py = baseBlockY + GetWaterVertexHeight01(bt, blockX, blockY, blockZ, axis, normalSign, cornerUOffset, cornerVOffset, voxelSizeX, voxelSizeZ, voxelPlaneSize);

                                    Vector2 uvCoord = ComputePlacementAwareUv(
                                        u,
                                        v,
                                        rawU,
                                        rawV,
                                        posD,
                                        uvSamplingFace,
                                        uvPlacementAxis);

                                    byte currentAO = l == 0 ? ao0 : (l == 1 ? ao1 : (l == 2 ? ao2 : ao3));
                                    byte currentLight = l == 0 ? light0 : (l == 1 ? light1 : (l == 2 ? light2 : light3));
                                    float rawLight = currentLight / 15f;
                                    float floatTint = tint ? 1f : 0f;
                                    float aoCurve = aoCurveExponent > 0f ? aoCurveExponent : DefaultAOCurveExponent;
                                    float aoBase = currentAO / 3f;
                                    float aoCurved = math.pow(aoBase, aoCurve);
                                    float aoDarkened = 1f - (1f - aoCurved) * math.max(0f, aoStrength);
                                    float floatAO = math.max(math.saturate(aoMinLight), math.saturate(aoDarkened));
                                    AddPackedVertex(
                                        new Vector3(px, py, pz),
                                        normal,
                                        uvCoord,
                                        atlasUv,
                                        new Vector4(rawLight, floatTint, floatAO, packedSubchunkAndOverlay));
                                }

                                NativeList<int> tris = FluidBlockUtility.IsWater(bt)
                                    ? waterTriangles
                                    : (blockMappings[(int)bt].isTransparent ? transparentTriangles : opaqueTriangles);

                                if (normalSign > 0)
                                {
                                    if (flipTriangle)
                                    {
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 1); tris.Add(vIndex + 3);
                                        tris.Add(vIndex + 1); tris.Add(vIndex + 2); tris.Add(vIndex + 3);
                                    }
                                    else
                                    {
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 1); tris.Add(vIndex + 2);
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 2); tris.Add(vIndex + 3);
                                    }
                                }
                                else
                                {
                                    if (flipTriangle)
                                    {
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 3); tris.Add(vIndex + 1);
                                        tris.Add(vIndex + 1); tris.Add(vIndex + 3); tris.Add(vIndex + 2);
                                    }
                                    else
                                    {
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 3); tris.Add(vIndex + 2);
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 2); tris.Add(vIndex + 1);
                                    }
                                }

                                for (int y0 = 0; y0 < h; y0++)
                                {
                                    for (int x0 = 0; x0 < w; x0++)
                                        mask[i + x0 + (j + y0) * sizeU] = default;
                                }

                                i += w;
                            }
                        }
                    }
                }
            }

            mask.Dispose();
        }
        private bool IsOccluder(int x, int y, int z, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            if (!TryGetResolvedVoxelIndex(x, y, z, voxelSizeX, voxelSizeZ, voxelPlaneSize, out int idx))
                return false;

            if (!solids[idx])
                return false;

            BlockType blockType = (BlockType)blockTypes[idx];
            BlockTextureMapping mapping = blockMappings[(int)blockType];
            return CastsAmbientOcclusion(blockType, mapping);
        }

        private bool IsVoxelSampleKnown(int x, int y, int z, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            if (!useKnownVoxelData)
                return true;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            return knownVoxelData[idx] != 0;
        }

        private bool TryGetResolvedVoxelIndex(int x, int y, int z, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize, out int idx)
        {
            idx = -1;
            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            idx = x + y * voxelSizeX + z * voxelPlaneSize;
            if (!useKnownVoxelData || knownVoxelData[idx] != 0)
                return true;

            int clampedX = math.clamp(x, border, border + SizeX - 1);
            int clampedZ = math.clamp(z, border, border + SizeZ - 1);
            idx = clampedX + y * voxelSizeX + clampedZ * voxelPlaneSize;
            return !useKnownVoxelData || knownVoxelData[idx] != 0;
        }

        private byte GetVertexAO(Vector3Int pos, Vector3Int d1, Vector3Int d2, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            bool s1 = IsOccluder(pos.x + d1.x, pos.y + d1.y, pos.z + d1.z, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            bool s2 = IsOccluder(pos.x + d2.x, pos.y + d2.y, pos.z + d2.z, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            bool c = IsOccluder(pos.x + d1.x + d2.x, pos.y + d1.y + d2.y, pos.z + d1.z + d2.z, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            if (s1 && s2) return 0;
            return (byte)(3 - (s1 ? 1 : 0) - (s2 ? 1 : 0) - (c ? 1 : 0));
        }

        private static bool CastsAmbientOcclusion(BlockType blockType, BlockTextureMapping mapping)
        {
            // AO deve vir de cubos cheios que realmente fecham a iluminaÃƒÆ’Ã‚Â§ÃƒÆ’Ã‚Â£o ambiente.
            // Folhas sÃƒÆ’Ã‚Â£o a exceÃƒÆ’Ã‚Â§ÃƒÆ’Ã‚Â£o: mesmo transparentes, devem sombrear como no Minecraft.
            if (BlockShapeUtility.GetEffectiveRenderShape(mapping) != BlockRenderShape.Cube ||
                mapping.isEmpty ||
                mapping.isLiquid ||
                mapping.lightOpacity == 0)
            {
                return false;
            }

            return !mapping.isTransparent || blockType == BlockType.Leaves;
        }

        private float GetWaterVertexHeight01(
            BlockType blockType,
            int x,
            int y,
            int z,
            int axis,
            int normalSign,
            int cornerUOffset,
            int cornerVOffset,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (!FluidBlockUtility.IsWater(blockType))
                return 1f;

            if (HasWaterAbove(x, y, z, voxelSizeX, voxelSizeZ, voxelPlaneSize))
                return 1f;

            ResolveWaterCornerOffsets(axis, normalSign, cornerUOffset, cornerVOffset, out int cornerXOffset, out int cornerZOffset);
            return GetWaterCornerHeight01(x, y, z, cornerXOffset, cornerZOffset, voxelSizeX, voxelSizeZ, voxelPlaneSize);
        }

        private void ResolveWaterCornerOffsets(
            int axis,
            int normalSign,
            int cornerUOffset,
            int cornerVOffset,
            out int cornerXOffset,
            out int cornerZOffset)
        {
            if (axis == 0)
            {
                cornerXOffset = normalSign > 0 ? 1 : 0;
                cornerZOffset = cornerVOffset;
                return;
            }

            if (axis == 1)
            {
                cornerXOffset = cornerVOffset;
                cornerZOffset = cornerUOffset;
                return;
            }

            cornerXOffset = cornerUOffset;
            cornerZOffset = normalSign > 0 ? 1 : 0;
        }

        private float GetWaterCornerHeight01(
            int x,
            int y,
            int z,
            int cornerXOffset,
            int cornerZOffset,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            int sampleMinX = x + cornerXOffset - 1;
            int sampleMinZ = z + cornerZOffset - 1;
            float accumulatedHeight = 0f;
            int accumulatedWeight = 0;

            for (int dz = 0; dz < 2; dz++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    int sampleX = sampleMinX + dx;
                    int sampleZ = sampleMinZ + dz;

                    if (HasWaterAbove(sampleX, y, sampleZ, voxelSizeX, voxelSizeZ, voxelPlaneSize))
                        return 1f;

                    BlockType sampleType = GetBlockTypeSafe(sampleX, y, sampleZ, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                    if (FluidBlockUtility.IsWater(sampleType))
                    {
                        float sampleHeight = GetWaterOwnHeight01(sampleType, sampleX, y, sampleZ, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                        if (sampleHeight >= 0.8f)
                        {
                            accumulatedHeight += sampleHeight * 10f;
                            accumulatedWeight += 10;
                        }
                        else
                        {
                            accumulatedHeight += sampleHeight;
                            accumulatedWeight += 1;
                        }
                    }
                    else if (!IsSolidWaterNeighbor(sampleType))
                    {
                        accumulatedWeight += 1;
                    }
                }
            }

            if (accumulatedWeight <= 0)
                return FluidBlockUtility.GetWaterSurfaceHeight01(BlockType.WaterFlow7);

            return accumulatedHeight / accumulatedWeight;
        }

        private float GetWaterOwnHeight01(
            BlockType blockType,
            int x,
            int y,
            int z,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (!FluidBlockUtility.IsWater(blockType))
                return 0f;

            if (HasWaterAbove(x, y, z, voxelSizeX, voxelSizeZ, voxelPlaneSize))
                return 1f;

            if (FluidBlockUtility.IsFallingWater(blockType))
                return 1f;

            return FluidBlockUtility.GetWaterSurfaceHeight01(blockType);
        }

        private bool HasWaterAbove(int x, int y, int z, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            if (x < 0 || x >= voxelSizeX || z < 0 || z >= voxelSizeZ)
                return false;

            if (y + 1 >= SizeY || y < 0)
                return false;

            if (!TryGetResolvedVoxelIndex(x, y + 1, z, voxelSizeX, voxelSizeZ, voxelPlaneSize, out int aboveIndex))
                return false;

            return FluidBlockUtility.IsWater((BlockType)blockTypes[aboveIndex]);
        }

        private BlockType GetBlockTypeSafe(int x, int y, int z, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            if (!TryGetResolvedVoxelIndex(x, y, z, voxelSizeX, voxelSizeZ, voxelPlaneSize, out int index))
                return BlockType.Air;

            return (BlockType)blockTypes[index];
        }

        private bool IsSolidWaterNeighbor(BlockType blockType)
        {
            if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
                return false;

            if (TorchPlacementUtility.IsTorchLike(blockType))
                return false;

            BlockTextureMapping mapping = blockMappings[(int)blockType];
            return mapping.isSolid;
        }

        private byte GetVertexLight(Vector3Int pos, Vector3Int d1, Vector3Int d2, NativeArray<byte> light, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            int l0 = SampleLightValue(pos, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            int l1 = SampleLightValue(pos + d1, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            int l2 = SampleLightValue(pos + d2, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            int l3 = SampleLightValue(pos + d1 + d2, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            return (byte)((l0 + l1 + l2 + l3 + 2) / 4);
        }

        private int SampleLightValue(Vector3Int pos, NativeArray<byte> light, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            if (pos.y < 0)
                return 0;
            if (pos.y >= SizeY)
                return 15;

            int clampedX = math.clamp(pos.x, 0, voxelSizeX - 1);
            int clampedZ = math.clamp(pos.z, 0, voxelSizeZ - 1);
            if (!TryGetResolvedVoxelIndex(clampedX, pos.y, clampedZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, out int idx))
                return 15;

            byte packed = light[idx];
            return math.max((int)LightUtils.GetSkyLight(packed), (int)LightUtils.GetBlockLight(packed));
        }

        private static bool IsEmissiveBlock(BlockTextureMapping mapping)
        {
            return mapping.isLightSource || mapping.lightEmission > 0;
        }
    }


    [BurstCompile]
    public struct DisposeChunkDataJob : IJob
    {
        [DeallocateOnJobCompletion] public NativeArray<int> heightCache;
        [DeallocateOnJobCompletion] public NativeArray<byte> blockTypes;
        [DeallocateOnJobCompletion] public NativeArray<byte> blockPlacementAxes;
        [DeallocateOnJobCompletion] public NativeArray<byte> knownVoxelData;
        [DeallocateOnJobCompletion] public NativeArray<bool> solids;
        [DeallocateOnJobCompletion] public NativeArray<byte> light;
        [DeallocateOnJobCompletion] public NativeArray<bool> subchunkNonEmpty; // ÃƒÂ¢Ã¢â‚¬Â Ã‚Â NOVO
        public void Execute() { }
    }

    [BurstCompile]
    public struct DisposeSuppressedBillboardsJob : IJob
    {
        [DeallocateOnJobCompletion] public NativeArray<int3> suppressedGrassBillboards;
        public void Execute() { }
    }

}

public class MeshBuildResult
{
    public Vector2Int coord;
    public int expectedGen;
    public List<Vector3> vertices;
    public List<int> opaqueTriangles;
    public List<int> waterTriangles;
    public List<int> transparentTriangles;
    public List<Vector2> uvs;
    public List<Vector3> normals;

    public MeshBuildResult(Vector2Int coord, List<Vector3> v, List<int> opaqueT, List<int> waterT, List<int> transparentT, List<Vector2> u, List<Vector3> n)
    {
        this.coord = coord;
        vertices = v;
        opaqueTriangles = opaqueT;
        waterTriangles = waterT;
        transparentTriangles = transparentT;
        uvs = u;
        normals = n;
    }
}

public static class LightOpacitySpaghettiCaveUtility
{
    private const int SizeX = Chunk.SizeX;
    private const int SizeY = Chunk.SizeY;
    private const int SizeZ = Chunk.SizeZ;
    private const int SpaghettiHorizontalCellSize = 4;
    private const int SpaghettiVerticalCellSize = 4;
    private const float DoubleNoiseWarp = 1.0181269f;
    private const float NoiseOffsetMagnitude = 2048f;

    public static bool ShouldApply(int dataBorderSize, int lightBorderSize, in SpaghettiCaveSettings settings)
    {
        return lightBorderSize > dataBorderSize &&
               settings.enabled;
    }

    public static void BuildCarveMask(
        Vector2Int coord,
        NativeArray<int> heightCache,
        NativeArray<byte> carveMask,
        NativeArray<byte> prefilledColumns,
        int prefilledColumnsStride,
        int border,
        int oreSeed,
        SpaghettiCaveSettings settings)
    {
        if (!heightCache.IsCreated || !carveMask.IsCreated || !settings.enabled || border <= 0)
            return;

        int minY = math.clamp(math.min(settings.minY, settings.maxY), 3, SizeY - 2);
        int maxY = math.clamp(math.max(settings.minY, settings.maxY), 3, SizeY - 2);
        if (maxY < minY)
            return;

        int voxelSizeX = SizeX + 2 * border;
        int voxelSizeZ = SizeZ + 2 * border;
        int voxelPlaneSize = voxelSizeX * SizeY;
        int heightStride = voxelSizeX;
        int minSurfaceDepth = math.max(0, settings.minSurfaceDepth);
        int entranceSurfaceDepth = math.max(0, math.min(settings.entranceSurfaceDepth, minSurfaceDepth));
        float densityBias = settings.densityBias;
        int worldSeed = oreSeed ^ settings.seedOffset ^ 0x4b1d2e37;
        var caveNoiseSampler = SpaghettiCaveNoiseUtility.Create(worldSeed);
        int chunkMinX = coord.x * SizeX;
        int chunkMinZ = coord.y * SizeZ;

        int globalEntranceMaxY = minY - 1;
        for (int localZ = 0; localZ < voxelSizeZ; localZ++)
        {
            for (int localX = 0; localX < voxelSizeX; localX++)
            {
                int columnSurfaceY = heightCache[localX + localZ * heightStride];
                int entranceMaxY = math.min(maxY, columnSurfaceY - entranceSurfaceDepth);
                globalEntranceMaxY = math.max(globalEntranceMaxY, entranceMaxY);
            }
        }

        if (globalEntranceMaxY < minY)
            return;

        int sampleMaxY = math.min(maxY, globalEntranceMaxY);
        int gridCountX = GetGridPointCount(0, voxelSizeX - 1, SpaghettiHorizontalCellSize);
        int gridCountY = GetGridPointCount(minY, sampleMaxY, SpaghettiVerticalCellSize);
        int gridCountZ = GetGridPointCount(0, voxelSizeZ - 1, SpaghettiHorizontalCellSize);
        NativeArray<float2> densityGrid = new NativeArray<float2>(gridCountX * gridCountY * gridCountZ, Allocator.Temp);

        try
        {
            for (int gridZ = 0; gridZ < gridCountZ; gridZ++)
            {
                int localZ = GetGridCoordinate(0, voxelSizeZ - 1, SpaghettiHorizontalCellSize, gridZ);
                float sampleZ = localZ + chunkMinZ - border + 0.5f;

                for (int gridX = 0; gridX < gridCountX; gridX++)
                {
                    int localX = GetGridCoordinate(0, voxelSizeX - 1, SpaghettiHorizontalCellSize, gridX);
                    float sampleX = localX + chunkMinX - border + 0.5f;

                    for (int gridY = 0; gridY < gridCountY; gridY++)
                    {
                        int voxelY = GetGridCoordinate(minY, sampleMaxY, SpaghettiVerticalCellSize, gridY);
                        float sampleY = voxelY + 0.5f;
                        densityGrid[GetGridIndex(gridX, gridY, gridZ, gridCountX, gridCountY)] =
                            SpaghettiCaveNoiseUtility.SampleDensityPair(in caveNoiseSampler, sampleX, sampleY, sampleZ, densityBias);
                    }
                }
            }

            for (int cellZ = 0; cellZ < gridCountZ - 1; cellZ++)
            {
                int localZ0 = GetGridCoordinate(0, voxelSizeZ - 1, SpaghettiHorizontalCellSize, cellZ);
                int localZ1 = GetGridCoordinate(0, voxelSizeZ - 1, SpaghettiHorizontalCellSize, cellZ + 1);
                int zSpan = math.max(1, localZ1 - localZ0);
                int localZMax = cellZ == gridCountZ - 2 ? localZ1 : localZ1 - 1;

                for (int cellX = 0; cellX < gridCountX - 1; cellX++)
                {
                    int localX0 = GetGridCoordinate(0, voxelSizeX - 1, SpaghettiHorizontalCellSize, cellX);
                    int localX1 = GetGridCoordinate(0, voxelSizeX - 1, SpaghettiHorizontalCellSize, cellX + 1);
                    int xSpan = math.max(1, localX1 - localX0);
                    int localXMax = cellX == gridCountX - 2 ? localX1 : localX1 - 1;

                    for (int cellY = 0; cellY < gridCountY - 1; cellY++)
                    {
                        int voxelY0 = GetGridCoordinate(minY, sampleMaxY, SpaghettiVerticalCellSize, cellY);
                        int voxelY1 = GetGridCoordinate(minY, sampleMaxY, SpaghettiVerticalCellSize, cellY + 1);
                        int ySpan = math.max(1, voxelY1 - voxelY0);
                        int voxelYMax = cellY == gridCountY - 2 ? voxelY1 : voxelY1 - 1;

                        float2 d000 = densityGrid[GetGridIndex(cellX, cellY, cellZ, gridCountX, gridCountY)];
                        float2 d100 = densityGrid[GetGridIndex(cellX + 1, cellY, cellZ, gridCountX, gridCountY)];
                        float2 d010 = densityGrid[GetGridIndex(cellX, cellY + 1, cellZ, gridCountX, gridCountY)];
                        float2 d110 = densityGrid[GetGridIndex(cellX + 1, cellY + 1, cellZ, gridCountX, gridCountY)];
                        float2 d001 = densityGrid[GetGridIndex(cellX, cellY, cellZ + 1, gridCountX, gridCountY)];
                        float2 d101 = densityGrid[GetGridIndex(cellX + 1, cellY, cellZ + 1, gridCountX, gridCountY)];
                        float2 d011 = densityGrid[GetGridIndex(cellX, cellY + 1, cellZ + 1, gridCountX, gridCountY)];
                        float2 d111 = densityGrid[GetGridIndex(cellX + 1, cellY + 1, cellZ + 1, gridCountX, gridCountY)];

                        bool requiresExactCellSampling =
                            cellX == 0 ||
                            cellX == gridCountX - 2 ||
                            cellZ == 0 ||
                            cellZ == gridCountZ - 2;

                        if (GetMinCarveDensity(d000, d100, d010, d110, d001, d101, d011, d111) >= 0f)
                        {
                            float centerWorldX = chunkMinX - border + (localX0 + localX1) * 0.5f + 0.5f;
                            float centerWorldY = (voxelY0 + voxelY1) * 0.5f + 0.5f;
                            float centerWorldZ = chunkMinZ - border + (localZ0 + localZ1) * 0.5f + 0.5f;
                            float2 centerDensity = SpaghettiCaveNoiseUtility.SampleDensityPair(in caveNoiseSampler, centerWorldX, centerWorldY, centerWorldZ, densityBias);
                            if (centerDensity.x >= 0f)
                                continue;

                            requiresExactCellSampling = true;
                        }

                        for (int localZ = localZ0; localZ <= localZMax; localZ++)
                        {
                            float tz = (localZ - localZ0) / (float)zSpan;

                            for (int localX = localX0; localX <= localXMax; localX++)
                            {
                                if (IsColumnPrefilled(localX, localZ, prefilledColumns, prefilledColumnsStride))
                                    continue;

                                int columnSurfaceY = heightCache[localX + localZ * heightStride];
                                int regularMaxY = math.min(maxY, columnSurfaceY - minSurfaceDepth);
                                int entranceMaxY = math.min(maxY, columnSurfaceY - entranceSurfaceDepth);
                                if (entranceMaxY < voxelY0)
                                    continue;

                                float tx = (localX - localX0) / (float)xSpan;
                                int maxVoxelYForColumn = math.min(voxelYMax, entranceMaxY);
                                int voxelIndex = localX + voxelY0 * voxelSizeX + localZ * voxelPlaneSize;
                                float worldX = localX + chunkMinX - border + 0.5f;
                                float worldZ = localZ + chunkMinZ - border + 0.5f;

                                for (int voxelY = voxelY0; voxelY <= maxVoxelYForColumn; voxelY++, voxelIndex += voxelSizeX)
                                {
                                    float2 density;
                                    if (requiresExactCellSampling)
                                    {
                                        density = SpaghettiCaveNoiseUtility.SampleDensityPair(in caveNoiseSampler, worldX, voxelY + 0.5f, worldZ, densityBias);
                                    }
                                    else
                                    {
                                        float ty = (voxelY - voxelY0) / (float)ySpan;
                                        density = TrilinearInterpolate(d000, d100, d010, d110, d001, d101, d011, d111, tx, ty, tz);
                                    }

                                    if (voxelY > regularMaxY && density.y >= 0f)
                                        continue;
                                    if (density.x >= 0f)
                                        continue;

                                    carveMask[voxelIndex] = 1;
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            if (densityGrid.IsCreated)
                densityGrid.Dispose();
        }
    }

    private static bool IsColumnPrefilled(int localX, int localZ, NativeArray<byte> prefilledColumns, int prefilledColumnsStride)
    {
        if (!prefilledColumns.IsCreated || prefilledColumns.Length == 0 || prefilledColumnsStride <= 0)
            return false;

        int index = localX + localZ * prefilledColumnsStride;
        return (uint)index < (uint)prefilledColumns.Length && prefilledColumns[index] != 0;
    }

    private static int GetGridPointCount(int minInclusive, int maxInclusive, int step)
    {
        int safeStep = math.max(1, step);
        return math.max(2, ((maxInclusive - minInclusive) / safeStep) + 2);
    }

    private static int GetGridCoordinate(int minInclusive, int maxInclusive, int step, int index)
    {
        return math.min(maxInclusive, minInclusive + index * math.max(1, step));
    }

    private static int GetGridIndex(int x, int y, int z, int xCount, int yCount)
    {
        return x + y * xCount + z * xCount * yCount;
    }

    private static float GetMinCarveDensity(float2 d000, float2 d100, float2 d010, float2 d110, float2 d001, float2 d101, float2 d011, float2 d111)
    {
        float minA = math.min(math.min(d000.x, d100.x), math.min(d010.x, d110.x));
        float minB = math.min(math.min(d001.x, d101.x), math.min(d011.x, d111.x));
        return math.min(minA, minB);
    }

    private static float2 TrilinearInterpolate(float2 d000, float2 d100, float2 d010, float2 d110, float2 d001, float2 d101, float2 d011, float2 d111, float tx, float ty, float tz)
    {
        float2 x00 = math.lerp(d000, d100, tx);
        float2 x10 = math.lerp(d010, d110, tx);
        float2 x01 = math.lerp(d001, d101, tx);
        float2 x11 = math.lerp(d011, d111, tx);
        float2 y0 = math.lerp(x00, x10, ty);
        float2 y1 = math.lerp(x01, x11, ty);
        return math.lerp(y0, y1, tz);
    }

    private static float2 SampleDensityPair(float worldX, float worldY, float worldZ, int worldSeed, float densityBias)
    {
        float roughness = SampleRoughness(worldX, worldY, worldZ, worldSeed);
        float spaghetti2d = SampleSpaghetti2d(worldX, worldY, worldZ, worldSeed);
        float entrancesDensity = SampleEntrances(worldX, worldY, worldZ, worldSeed, roughness);
        float carveDensity = math.min(spaghetti2d + roughness, entrancesDensity);
        return new float2(carveDensity + densityBias, entrancesDensity + densityBias);
    }

    private static float SampleSpaghetti2d(float worldX, float worldY, float worldZ, int worldSeed)
    {
        float modulator = SampleSingleAmplitudeNoise(worldX, worldY, worldZ, worldSeed, 0x4d0f12a1, -11, 2f, 1f);
        float thicknessMod = -0.95f - 0.35f * SampleSingleAmplitudeNoise(worldX, worldY, worldZ, worldSeed, 0x63be5ab9, -11, 2f, 1f);
        float weirdScaled = SampleWeirdScaledSampler(worldX, worldY, worldZ, worldSeed, 0x7c159d51, modulator, false, -7);
        float elevationNoise = SampleSingleAmplitudeNoise(worldX, worldY, worldZ, worldSeed, 0x1f3d8a77, -8, 1f, 0f);
        float elevationGradient = SampleYClampedGradient(worldY, -64f, 320f, 8f, -40f);
        float elevationBand = math.abs(elevationNoise * 8f + elevationGradient);

        float longitudinal = weirdScaled + 0.083f * thicknessMod;
        float elevationTerm = Cube(elevationBand + thicknessMod);
        return math.clamp(math.max(longitudinal, elevationTerm), -1f, 1f);
    }

    private static float SampleRoughness(float worldX, float worldY, float worldZ, int worldSeed)
    {
        float roughnessModulator = SampleSingleAmplitudeNoise(worldX, worldY, worldZ, worldSeed, 0x2c8f3bd3, -8, 1f, 1f);
        float roughness = SampleSingleAmplitudeNoise(worldX, worldY, worldZ, worldSeed, 0x54e21f79, -5, 1f, 1f);
        return (-0.05f - 0.05f * roughnessModulator) * (-0.4f + math.abs(roughness));
    }

    private static float SampleEntrances(float worldX, float worldY, float worldZ, int worldSeed, float roughness)
    {
        float entranceNoise = SampleOctavedNoise(worldX, worldY, worldZ, worldSeed, 0x2714c2d1, -7, 0.75f, 0.5f, 3, 0.4f, 0.5f, 1f);
        float entranceHead = 0.37f + entranceNoise + SampleYClampedGradient(worldY, -10f, 30f, 0.3f, 0f);

        float rarityNoise = SampleSingleAmplitudeNoise(worldX, worldY, worldZ, worldSeed, 0x1495f0e3, -11, 2f, 1f);
        float spaghettiA = SampleWeirdScaledSampler(worldX, worldY, worldZ, worldSeed, 0x6a31dcb7, rarityNoise, true, -7);
        float spaghettiB = SampleWeirdScaledSampler(worldX, worldY, worldZ, worldSeed, 0x0dc7612f, rarityNoise, true, -7);
        float thicknessMod = -0.0765f - 0.0115f * SampleSingleAmplitudeNoise(worldX, worldY, worldZ, worldSeed, 0x45fa21cd, -8, 1f, 1f);
        float entranceBody = roughness + math.clamp(math.max(spaghettiA, spaghettiB) + thicknessMod, -1f, 1f);

        return math.min(entranceHead, entranceBody);
    }

    private static float SampleWeirdScaledSampler(float worldX, float worldY, float worldZ, int worldSeed, int noiseSalt, float inputValue, bool useType1Rarity, int firstOctave)
    {
        float rarity = useType1Rarity ? GetRarity3D(inputValue) : GetRarity2D(inputValue);
        float sample = SampleSingleAmplitudeNoise(worldX / rarity, worldY / rarity, worldZ / rarity, worldSeed, noiseSalt, firstOctave, 1f, 1f);
        return rarity * math.abs(sample);
    }

    private static float GetRarity2D(float rarity)
    {
        if (rarity < -0.75f) return 0.5f;
        if (rarity < -0.5f) return 0.75f;
        if (rarity < 0.5f) return 1f;
        if (rarity < 0.75f) return 2f;
        return 3f;
    }

    private static float GetRarity3D(float rarity)
    {
        if (rarity < -0.5f) return 0.75f;
        if (rarity < 0f) return 1f;
        if (rarity < 0.5f) return 1.5f;
        return 2f;
    }

    private static float SampleSingleAmplitudeNoise(float worldX, float worldY, float worldZ, int worldSeed, int noiseSalt, int firstOctave, float xzScale, float yScale)
    {
        return SampleOctavedNoise(worldX, worldY, worldZ, worldSeed, noiseSalt, firstOctave, xzScale, yScale, 1, 1f, 0f, 0f);
    }

    private static float SampleOctavedNoise(float worldX, float worldY, float worldZ, int worldSeed, int noiseSalt, int firstOctave, float xzScale, float yScale, int amplitudeCount, float amplitude0, float amplitude1, float amplitude2)
    {
        float total = 0f;
        float weightSum = 0f;

        for (int octaveIndex = 0; octaveIndex < amplitudeCount; octaveIndex++)
        {
            float amplitude = octaveIndex == 0 ? amplitude0 : octaveIndex == 1 ? amplitude1 : octaveIndex == 2 ? amplitude2 : 0f;
            if (math.abs(amplitude) <= 1e-5f)
                continue;

            total += amplitude * SampleDoubleSimplexNoise(worldX, worldY, worldZ, worldSeed, noiseSalt + octaveIndex * 977, firstOctave + octaveIndex, xzScale, yScale);
            weightSum += math.abs(amplitude);
        }

        if (weightSum <= 1e-5f)
            return 0f;

        return math.clamp(total / weightSum, -1f, 1f);
    }

    private static float SampleDoubleSimplexNoise(float worldX, float worldY, float worldZ, int worldSeed, int noiseSalt, int octave, float xzScale, float yScale)
    {
        float frequency = math.exp2((float)octave);
        float3 samplePos = new float3(worldX * xzScale * frequency, worldY * yScale * frequency, worldZ * xzScale * frequency);

        uint state = Hash((uint)(worldSeed ^ noiseSalt));
        float3 primaryOffset = NextNoiseOffset(ref state);
        float3 secondaryOffset = NextNoiseOffset(ref state);

        float primary = noise.snoise(samplePos + primaryOffset);
        float secondary = noise.snoise(samplePos * DoubleNoiseWarp + secondaryOffset);
        return math.clamp((primary + secondary) * 0.5f, -1f, 1f);
    }

    private static float3 NextNoiseOffset(ref uint state)
    {
        return new float3(NextSignedFloat(ref state), NextSignedFloat(ref state), NextSignedFloat(ref state)) * NoiseOffsetMagnitude;
    }

    private static float SampleYClampedGradient(float y, float fromY, float toY, float fromValue, float toValue)
    {
        float t = math.saturate((y - fromY) / math.max(1e-5f, toY - fromY));
        return math.lerp(fromValue, toValue, t);
    }

    private static float Cube(float value)
    {
        return value * value * value;
    }

    private static uint Hash(uint v)
    {
        v ^= v >> 16;
        v *= 0x7feb352du;
        v ^= v >> 15;
        v *= 0x846ca68bu;
        v ^= v >> 16;
        return v;
    }

    private static float NextFloat01(ref uint state)
    {
        state = Hash(state + 0x9e3779b9u);
        return (state & 0x00ffffffu) / 16777215f;
    }

private static float NextSignedFloat(ref uint state)
{
    return NextFloat01(ref state) * 2f - 1f;
}
}

public static class SpaghettiCaveNoiseUtility
{
    private const float DoubleNoiseWarp = 1.0181269f;
    private const float NoiseOffsetMagnitude = 2048f;

    public struct DoubleSimplexNoiseSampler
    {
        public float xzScale;
        public float yScale;
        public float frequency;
        public float3 primaryOffset;
        public float3 secondaryOffset;
    }

    public struct SpaghettiCaveNoiseSampler
    {
        public DoubleSimplexNoiseSampler spaghetti2dModulator;
        public DoubleSimplexNoiseSampler spaghetti2dThickness;
        public DoubleSimplexNoiseSampler spaghetti2dWeirdScaled;
        public DoubleSimplexNoiseSampler spaghetti2dElevation;
        public DoubleSimplexNoiseSampler roughnessModulator;
        public DoubleSimplexNoiseSampler roughness;
        public DoubleSimplexNoiseSampler entrancesOctave0;
        public DoubleSimplexNoiseSampler entrancesOctave1;
        public DoubleSimplexNoiseSampler entrancesOctave2;
        public DoubleSimplexNoiseSampler entranceRarity;
        public DoubleSimplexNoiseSampler entranceSpaghettiA;
        public DoubleSimplexNoiseSampler entranceSpaghettiB;
        public DoubleSimplexNoiseSampler entranceThickness;
    }

    public static SpaghettiCaveNoiseSampler Create(int worldSeed)
    {
        SpaghettiCaveNoiseSampler sampler;
        sampler.spaghetti2dModulator = CreateDoubleSimplexNoiseSampler(worldSeed, 0x4d0f12a1, -11, 2f, 1f);
        sampler.spaghetti2dThickness = CreateDoubleSimplexNoiseSampler(worldSeed, 0x63be5ab9, -11, 2f, 1f);
        sampler.spaghetti2dWeirdScaled = CreateDoubleSimplexNoiseSampler(worldSeed, 0x7c159d51, -7, 1f, 1f);
        sampler.spaghetti2dElevation = CreateDoubleSimplexNoiseSampler(worldSeed, 0x1f3d8a77, -8, 1f, 0f);
        sampler.roughnessModulator = CreateDoubleSimplexNoiseSampler(worldSeed, 0x2c8f3bd3, -8, 1f, 1f);
        sampler.roughness = CreateDoubleSimplexNoiseSampler(worldSeed, 0x54e21f79, -5, 1f, 1f);
        sampler.entrancesOctave0 = CreateDoubleSimplexNoiseSampler(worldSeed, 0x2714c2d1, -7, 0.75f, 0.5f);
        sampler.entrancesOctave1 = CreateDoubleSimplexNoiseSampler(worldSeed, 0x2714c2d1 + 977, -6, 0.75f, 0.5f);
        sampler.entrancesOctave2 = CreateDoubleSimplexNoiseSampler(worldSeed, 0x2714c2d1 + 1954, -5, 0.75f, 0.5f);
        sampler.entranceRarity = CreateDoubleSimplexNoiseSampler(worldSeed, 0x1495f0e3, -11, 2f, 1f);
        sampler.entranceSpaghettiA = CreateDoubleSimplexNoiseSampler(worldSeed, 0x6a31dcb7, -7, 1f, 1f);
        sampler.entranceSpaghettiB = CreateDoubleSimplexNoiseSampler(worldSeed, 0x0dc7612f, -7, 1f, 1f);
        sampler.entranceThickness = CreateDoubleSimplexNoiseSampler(worldSeed, 0x45fa21cd, -8, 1f, 1f);
        return sampler;
    }

    public static float2 SampleDensityPair(
        in SpaghettiCaveNoiseSampler sampler,
        float worldX,
        float worldY,
        float worldZ,
        float densityBias)
    {
        float roughness = SampleRoughness(in sampler, worldX, worldY, worldZ);
        float spaghetti2d = SampleSpaghetti2d(in sampler, worldX, worldY, worldZ);
        float entrancesDensity = SampleEntrances(in sampler, worldX, worldY, worldZ, roughness);
        float carveDensity = math.min(spaghetti2d + roughness, entrancesDensity);
        return new float2(carveDensity + densityBias, entrancesDensity + densityBias);
    }

    private static DoubleSimplexNoiseSampler CreateDoubleSimplexNoiseSampler(
        int worldSeed,
        int noiseSalt,
        int octave,
        float xzScale,
        float yScale)
    {
        float frequency = math.exp2((float)octave);
        uint state = Hash((uint)(worldSeed ^ noiseSalt));

        DoubleSimplexNoiseSampler sampler;
        sampler.xzScale = xzScale;
        sampler.yScale = yScale;
        sampler.frequency = frequency;
        sampler.primaryOffset = NextNoiseOffset(ref state);
        sampler.secondaryOffset = NextNoiseOffset(ref state);
        return sampler;
    }

    private static float SampleSpaghetti2d(in SpaghettiCaveNoiseSampler sampler, float worldX, float worldY, float worldZ)
    {
        float modulator = SampleDoubleSimplexNoise(in sampler.spaghetti2dModulator, worldX, worldY, worldZ);
        float thicknessMod = -0.95f - 0.35f * SampleDoubleSimplexNoise(in sampler.spaghetti2dThickness, worldX, worldY, worldZ);
        float weirdScaled = SampleWeirdScaledSampler(in sampler.spaghetti2dWeirdScaled, worldX, worldY, worldZ, modulator, false);
        float elevationNoise = SampleDoubleSimplexNoise(in sampler.spaghetti2dElevation, worldX, worldY, worldZ);
        float elevationGradient = SampleYClampedGradient(worldY, -64f, 320f, 8f, -40f);
        float elevationBand = math.abs(elevationNoise * 8f + elevationGradient);

        float longitudinal = weirdScaled + 0.083f * thicknessMod;
        float elevationTerm = Cube(elevationBand + thicknessMod);
        return math.clamp(math.max(longitudinal, elevationTerm), -1f, 1f);
    }

    private static float SampleRoughness(in SpaghettiCaveNoiseSampler sampler, float worldX, float worldY, float worldZ)
    {
        float roughnessModulator = SampleDoubleSimplexNoise(in sampler.roughnessModulator, worldX, worldY, worldZ);
        float roughness = SampleDoubleSimplexNoise(in sampler.roughness, worldX, worldY, worldZ);
        return (-0.05f - 0.05f * roughnessModulator) * (-0.4f + math.abs(roughness));
    }

    private static float SampleEntrances(in SpaghettiCaveNoiseSampler sampler, float worldX, float worldY, float worldZ, float roughness)
    {
        float entranceNoise = SampleNormalizedOctavedNoise(
            in sampler.entrancesOctave0,
            in sampler.entrancesOctave1,
            in sampler.entrancesOctave2,
            worldX,
            worldY,
            worldZ,
            0.4f,
            0.5f,
            1f);
        float entranceHead = 0.37f + entranceNoise + SampleYClampedGradient(worldY, -10f, 30f, 0.3f, 0f);

        float rarityNoise = SampleDoubleSimplexNoise(in sampler.entranceRarity, worldX, worldY, worldZ);
        float spaghettiA = SampleWeirdScaledSampler(in sampler.entranceSpaghettiA, worldX, worldY, worldZ, rarityNoise, true);
        float spaghettiB = SampleWeirdScaledSampler(in sampler.entranceSpaghettiB, worldX, worldY, worldZ, rarityNoise, true);
        float thicknessMod = -0.0765f - 0.0115f * SampleDoubleSimplexNoise(in sampler.entranceThickness, worldX, worldY, worldZ);
        float entranceBody = roughness + math.clamp(math.max(spaghettiA, spaghettiB) + thicknessMod, -1f, 1f);

        return math.min(entranceHead, entranceBody);
    }

    private static float SampleWeirdScaledSampler(
        in DoubleSimplexNoiseSampler sampler,
        float worldX,
        float worldY,
        float worldZ,
        float inputValue,
        bool useType1Rarity)
    {
        float rarity = useType1Rarity
            ? GetRarity3D(inputValue)
            : GetRarity2D(inputValue);

        float sample = SampleDoubleSimplexNoise(
            in sampler,
            worldX / rarity,
            worldY / rarity,
            worldZ / rarity);

        return rarity * math.abs(sample);
    }

    private static float SampleNormalizedOctavedNoise(
        in DoubleSimplexNoiseSampler octave0,
        in DoubleSimplexNoiseSampler octave1,
        in DoubleSimplexNoiseSampler octave2,
        float worldX,
        float worldY,
        float worldZ,
        float amplitude0,
        float amplitude1,
        float amplitude2)
    {
        float total = 0f;
        float weightSum = 0f;

        if (math.abs(amplitude0) > 1e-5f)
        {
            total += amplitude0 * SampleDoubleSimplexNoise(in octave0, worldX, worldY, worldZ);
            weightSum += math.abs(amplitude0);
        }

        if (math.abs(amplitude1) > 1e-5f)
        {
            total += amplitude1 * SampleDoubleSimplexNoise(in octave1, worldX, worldY, worldZ);
            weightSum += math.abs(amplitude1);
        }

        if (math.abs(amplitude2) > 1e-5f)
        {
            total += amplitude2 * SampleDoubleSimplexNoise(in octave2, worldX, worldY, worldZ);
            weightSum += math.abs(amplitude2);
        }

        if (weightSum <= 1e-5f)
            return 0f;

        return math.clamp(total / weightSum, -1f, 1f);
    }

    private static float SampleDoubleSimplexNoise(in DoubleSimplexNoiseSampler sampler, float worldX, float worldY, float worldZ)
    {
        float3 samplePos = new float3(
            worldX * sampler.xzScale * sampler.frequency,
            worldY * sampler.yScale * sampler.frequency,
            worldZ * sampler.xzScale * sampler.frequency);

        float primary = noise.snoise(samplePos + sampler.primaryOffset);
        float secondary = noise.snoise(samplePos * DoubleNoiseWarp + sampler.secondaryOffset);
        return math.clamp((primary + secondary) * 0.5f, -1f, 1f);
    }

    private static float GetRarity2D(float rarity)
    {
        if (rarity < -0.75f)
            return 0.5f;
        if (rarity < -0.5f)
            return 0.75f;
        if (rarity < 0.5f)
            return 1f;
        if (rarity < 0.75f)
            return 2f;
        return 3f;
    }

    private static float GetRarity3D(float rarity)
    {
        if (rarity < -0.5f)
            return 0.75f;
        if (rarity < 0f)
            return 1f;
        if (rarity < 0.5f)
            return 1.5f;
        return 2f;
    }

    private static float SampleYClampedGradient(float y, float fromY, float toY, float fromValue, float toValue)
    {
        float t = math.saturate((y - fromY) / math.max(1e-5f, toY - fromY));
        return math.lerp(fromValue, toValue, t);
    }

    private static float Cube(float value)
    {
        return value * value * value;
    }

    private static float3 NextNoiseOffset(ref uint state)
    {
        return new float3(
            NextSignedFloat(ref state),
            NextSignedFloat(ref state),
            NextSignedFloat(ref state)) * NoiseOffsetMagnitude;
    }

    private static uint Hash(uint v)
    {
        v ^= v >> 16;
        v *= 0x7feb352du;
        v ^= v >> 15;
        v *= 0x846ca68bu;
        v ^= v >> 16;
        return v;
    }

    private static float NextFloat01(ref uint state)
    {
        state = Hash(state + 0x9e3779b9u);
        return (state & 0x00ffffffu) / 16777215f;
    }

    private static float NextSignedFloat(ref uint state)
    {
        return NextFloat01(ref state) * 2f - 1f;
    }
}













