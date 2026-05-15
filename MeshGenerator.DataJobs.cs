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
    // Primeiro job da pipeline: calcula a altura de cada coluna, inclusive o padding
    // usado por costura entre chunks, superficie e sistemas de iluminacao.
    [BurstCompile]
    private struct HeightmapJob : IJobParallelFor
    {
        public Vector2Int coord;

        [ReadOnly] public NativeArray<NoiseLayer> noiseLayers;

        public int baseHeight;
        public bool useFlatWorld;
        public int flatWorldHeight;
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
            if (useFlatWorld)
                return math.clamp(flatWorldHeight, 3, SizeY - 1);

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
        public bool useFlatWorld;
        public int flatWorldHeight;
        public BiomeType flatWorldBiome;
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

            if (useFlatWorld)
            {
                columnContexts[index] = FlatWorldUtility.CreateColumnContext(
                    worldX,
                    worldZ,
                    flatWorldHeight,
                    SizeY,
                    flatWorldBiome,
                    biomeNoiseSettings);
                return;
            }

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
        public bool useFlatWorld;

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

                blockTypes[voxelIndex] = (byte)(useFlatWorld || y > highestSolidY - TerrainSurfaceRules.StoneTransitionDepth
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
        public bool useFlatWorld;

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
            int maxSurfaceDepth = useFlatWorld
                ? FlatWorldUtility.SurfaceLayerDepth
                : math.max(1, surface.surfaceLayerDepth);
            BlockType surfaceBlock = surface.surfaceBlock;
            BlockType subsurfaceBlock = surface.subsurfaceBlock;
            int solidLayersPainted = 0;

            for (int y = surfaceY; y >= 3 && solidLayersPainted < maxSurfaceDepth; y--)
            {
                int voxelIndex = lx + y * voxelSizeX + lz * voxelPlaneSize;
                if (!solids[voxelIndex])
                    continue;

                blockTypes[voxelIndex] = (byte)(solidLayersPainted == 0 ? surfaceBlock : subsurfaceBlock);
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
    private struct CopyGeneratedEmissionToLightVolumeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> sourceBlockTypes;
        [ReadOnly] public NativeArray<ushort> lightEmissionByBlock;
        [NativeDisableParallelForRestriction] public NativeArray<ushort> targetBlockEmission;

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
            targetBlockEmission[targetIndex] = lightEmissionByBlock[(int)sourceBlockTypes[index]];
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
    private struct ApplyEmissionOverridesJob : IJob
    {
        [ReadOnly] public NativeArray<BlockEdit> overrides;
        [ReadOnly] public NativeArray<ushort> lightEmissionByBlock;
        public NativeArray<ushort> blockEmission;

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
                if (edit.type < 0 || edit.type >= lightEmissionByBlock.Length)
                    continue;

                int ix = edit.x - chunkMinX + borderSize;
                int iz = edit.z - chunkMinZ + borderSize;
                if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                    continue;

                int dstIndex = ix + edit.y * voxelSizeX + iz * voxelPlaneSize;
                blockEmission[dstIndex] = lightEmissionByBlock[edit.type];
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
            if (blockType == BlockType.Air || blockType == BlockType.Leaves || FluidBlockUtility.IsWater(blockType))
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
}
