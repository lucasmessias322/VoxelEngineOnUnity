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
}
public static class MeshGenerator
{
    private const int SizeX = Chunk.SizeX;
    private const int SizeY = Chunk.SizeY;
    private const int SizeZ = Chunk.SizeZ;
    private const float DefaultAOCurveExponent = 1.12f;

    private const int SubchunksPerColumn = Chunk.SubchunksPerColumn;
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

    // Preenche o casco base do terreno; etapas como cavernas, agua, arvores e edits
    // entram depois para manter a pipeline previsivel.
    [BurstCompile]
    struct PopulateTerrainJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<TerrainColumnContext> columnContexts;
        [NativeDisableParallelForRestriction]
        [WriteOnly] public NativeArray<byte> blockTypes;

        [NativeDisableParallelForRestriction]
        [WriteOnly] public NativeArray<bool> solids;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;

        public int border;

        public void Execute(int index)
        {
            int paddedSize = SizeX + 2 * border;

            int lx = index % paddedSize;   // cacheX
            int lz = index / paddedSize;   // cacheZ
            TerrainColumnContext columnContext = columnContexts[index];
            TerrainSurfaceData surfaceData = columnContext.surface;

            int voxelSizeX = SizeX + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;

            int maxSolidY = math.min(columnContext.surfaceHeight, SizeY - 1);
            int idx = lx + lz * voxelPlaneSize;

            for (int y = 0; y <= maxSolidY; y++, idx += voxelSizeX)
            {
                BlockType bt = TerrainSurfaceRules.GetBlockTypeAtHeight(y, surfaceData);
                blockTypes[idx] = (byte)bt;
                solids[idx] = blockMappings[(int)bt].isSolid;
            }
        }
    }

    [BurstCompile]
    private struct PopulateLightOpacityJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<TerrainColumnContext> columnContexts;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<byte> opacity;
        [ReadOnly] public NativeArray<byte> effectiveOpacityByBlock;

        public int border;

        public void Execute(int index)
        {
            int paddedSize = SizeX + 2 * border;

            int lx = index % paddedSize;
            int lz = index / paddedSize;
            TerrainColumnContext columnContext = columnContexts[index];
            TerrainSurfaceData surfaceData = columnContext.surface;

            int voxelSizeX = SizeX + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;
            int maxSolidY = math.min(columnContext.surfaceHeight, SizeY - 1);
            int voxelIndex = lx + lz * voxelPlaneSize;

            for (int y = 0; y <= maxSolidY; y++, voxelIndex += voxelSizeX)
            {
                BlockType bt = TerrainSurfaceRules.GetBlockTypeAtHeight(y, surfaceData);
                opacity[voxelIndex] = effectiveOpacityByBlock[(int)bt];
            }
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
    private struct DisposeTerrainColumnContextArrayJob : IJob
    {
        [DeallocateOnJobCompletion] public NativeArray<TerrainColumnContext> values;
        public void Execute() { }
    }

    [BurstCompile]
    private struct BuildChunkSnapshotAndFlagsJob : IJob
    {
        [ReadOnly] public NativeArray<byte> blockTypes;
        [WriteOnly] public NativeArray<byte> voxelSnapshot;
        public NativeArray<bool> subchunkNonEmpty;
        public int borderSize;

        public void Execute()
        {
            for (int s = 0; s < SubchunksPerColumn; s++)
                subchunkNonEmpty[s] = false;

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
                    }
                }
            }
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
        BiomeNoiseSettings biomeNoiseSettings,
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
        CaveGenerationMode caveGenerationMode,
        WormCaveSettings caveSettings,
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
        out NativeArray<bool> subchunkNonEmpty
    )
    {
        // A pipeline trabalha com dois volumes padded:
        // um para dados do terreno e outro para opacidade/luz, que pode pedir mais borda.
        // 1. Fixar o borderSize em 1 (PadrÃƒÂ£o para Ambient Occlusion e Costura)

        // 2. AlocaÃƒÂ§ÃƒÂµes dos Arrays IntermÃƒÂ©dios que fluem entre os Jobs (TempJob)
        // Em cenas pesadas essa chain pode durar mais de 4 frames, entÃƒÆ’Ã‚Â£o os buffers
        // intermediÃƒÆ’Ã‚Â¡rios abaixo nÃƒÆ’Ã‚Â£o podem usar TempJob.
        lightBorderSize = math.max(lightBorderSize, dataBorderSize);
        subchunkNonEmpty = new NativeArray<bool>(SubchunksPerColumn, Allocator.Persistent);

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
        light = new NativeArray<byte>(dataTotalVoxels, Allocator.Persistent);
        lightOpacityData = default;
        NativeArray<byte> sharedSpaghettiCarveMask = new NativeArray<byte>(0, Allocator.Persistent);

        // ==========================================
        // JOB 0: GeraÃƒÂ§ÃƒÂ£o do Heightmap (Paralelo)
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
        JobHandle heightHandle = heightJob.Schedule(totalHeightPoints, 32); // Batch size 64 para paralelismo (ajuste se necessÃƒÂ¡rio)
        NativeArray<TerrainColumnContext> dataColumnContexts = new NativeArray<TerrainColumnContext>(dataTotalHeightPoints, Allocator.Persistent);
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
        JobHandle dataColumnContextHandle = buildDataColumnContextCacheJob.Schedule(dataTotalHeightPoints, 32, heightHandle);
        heightHandle = dataColumnContextHandle;

        // ==========================================
        // JOB 1a: Populate Terrain Columns (PARALELO!)
        // ==========================================
        var populateJob = new PopulateTerrainJob
        {
            columnContexts = dataColumnContexts,
            blockTypes = blockTypes,
            solids = solids,
            blockMappings = blockMappings,
            border = borderSize
        };

        int paddedSize = SizeX + 2 * borderSize;
        int totalColumns = paddedSize * paddedSize;

        JobHandle populateHandle = populateJob.Schedule(totalColumns, 32, heightHandle); // batch 64 ÃƒÂ© ÃƒÂ³timo




        // ==========================================
        // JOB 1: GeraÃƒÂ§ÃƒÂ£o de Dados (Terreno)
        // ==========================================
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
            caveGenerationMode = caveGenerationMode,
            caveSettings = caveSettings,
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
        // JobHandle chunkDataHandle = chunkDataJob.Schedule(heightHandle); // DependÃƒÂªncia no heightHandle
        // O PopulateTerrainJob ja escreveu o terreno base. A partir daqui encadeamos
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
            caveChunkDataHandle = caveChunkDataJob.Schedule(populateHandle);

            var oreChunkDataJob = baseChunkDataJob;
            oreChunkDataJob.stages = ChunkData.ChunkDataStageFlags.Ores;
            oreChunkDataHandle = oreChunkDataJob.Schedule(caveChunkDataHandle);

            var fillWaterAboveTerrainJob = new ChunkData.FillWaterAboveTerrainJob
            {
                heightCache = heightCache,
                blockTypes = blockTypes,
                solids = solids,
                border = dataBorderSize,
                seaLevel = math.min(SizeY - 1, (int)math.floor(seaLevel)),
                waterBlockId = (byte)BlockType.Water,
                waterIsSolid = blockMappings[(int)BlockType.Water].isSolid
            };
            waterChunkDataHandle = fillWaterAboveTerrainJob.Schedule(dataTotalHeightPoints, 32, oreChunkDataHandle);

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

            var snapshotJob = new BuildChunkSnapshotAndFlagsJob
            {
                blockTypes = blockTypes,
                voxelSnapshot = chunkVoxelSnapshot,
                subchunkNonEmpty = subchunkNonEmpty,
                borderSize = dataBorderSize
            };
            JobHandle snapshotHandle = snapshotJob.Schedule(finalChunkDataHandle);
            var disposeDataColumnContextCacheJob = new DisposeTerrainColumnContextArrayJob
            {
                values = dataColumnContexts
            };
            JobHandle disposeDataColumnContextHandle = disposeDataColumnContextCacheJob.Schedule(finalChunkDataHandle);
            byte fullBright = LightUtils.PackLight(15, 0);
            for (int i = 0; i < light.Length; i++)
                light[i] = fullBright;

            var disposeEmptySpaghettiCarveMaskJob = new DisposeByteArrayJob
            {
                values = sharedSpaghettiCarveMask
            };
            JobHandle disposeEmptySpaghettiCarveMaskHandle = disposeEmptySpaghettiCarveMaskJob.Schedule(finalChunkDataHandle);

            dataHandle = JobHandle.CombineDependencies(snapshotHandle, disposeDataColumnContextHandle);
            dataHandle = JobHandle.CombineDependencies(dataHandle, disposeEmptySpaghettiCarveMaskHandle);
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
        NativeArray<TerrainColumnContext> lightColumnContexts = new NativeArray<TerrainColumnContext>(lightTotalHeightPoints, Allocator.Persistent);
        var buildLightColumnContextCacheJob = new BuildTerrainColumnContextCacheJob
        {
            coord = coord,
            heightCache = lightHeightCache,
            columnContexts = lightColumnContexts,
            border = lightBorderSize,
            seaLevel = seaLevel,
            baseHeight = baseHeight,
            CliffTreshold = CliffTreshold,
            biomeNoiseSettings = biomeNoiseSettings
        };
        JobHandle lightColumnContextHandle = buildLightColumnContextCacheJob.Schedule(lightTotalHeightPoints, 32, lightHeightHandle);
        bool useSharedSpaghettiCarveMask = LightOpacitySpaghettiCaveUtility.ShouldApply(
            dataBorderSize,
            lightBorderSize,
            caveGenerationMode,
            spaghettiCaveSettings);
        JobHandle spaghettiCarveMaskHandle = default;
        if (useSharedSpaghettiCarveMask)
        {
            // A mesma mascara e compartilhada entre terreno e opacidade para que
            // cavernas e iluminacao concordem exatamente nas bordas.
            sharedSpaghettiCarveMask.Dispose();
            sharedSpaghettiCarveMask = new NativeArray<byte>(lightTotalVoxels, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var buildSpaghettiCaveCarveMaskJob = new BuildSpaghettiCaveCarveMaskJob
            {
                coord = coord,
                heightCache = lightHeightCache,
                carveMask = sharedSpaghettiCarveMask,
                borderSize = lightBorderSize,
                oreSeed = oreSeed,
                spaghettiCaveSettings = spaghettiCaveSettings
            };
            spaghettiCarveMaskHandle = buildSpaghettiCaveCarveMaskJob.Schedule(lightHeightHandle);

            baseChunkDataJob.spaghettiCarveMask = sharedSpaghettiCarveMask;
            baseChunkDataJob.spaghettiCarveMaskVoxelSizeX = lightVoxelSizeX;
            baseChunkDataJob.spaghettiCarveMaskVoxelPlaneSize = lightVoxelPlaneSize;
            baseChunkDataJob.spaghettiCarveMaskOffsetX = lightBorderSize - dataBorderSize;
            baseChunkDataJob.spaghettiCarveMaskOffsetZ = lightBorderSize - dataBorderSize;
        }

        JobHandle caveChunkDependency = useSharedSpaghettiCarveMask
            ? JobHandle.CombineDependencies(populateHandle, spaghettiCarveMaskHandle)
            : populateHandle;

        var stagedCaveChunkDataJob = baseChunkDataJob;
        stagedCaveChunkDataJob.stages = ChunkData.ChunkDataStageFlags.Caves;
        caveChunkDataHandle = stagedCaveChunkDataJob.Schedule(caveChunkDependency);

        var stagedOreChunkDataJob = baseChunkDataJob;
        stagedOreChunkDataJob.stages = ChunkData.ChunkDataStageFlags.Ores;
        oreChunkDataHandle = stagedOreChunkDataJob.Schedule(caveChunkDataHandle);

        var stagedFillWaterAboveTerrainJob = new ChunkData.FillWaterAboveTerrainJob
        {
            heightCache = heightCache,
            blockTypes = blockTypes,
            solids = solids,
            border = dataBorderSize,
            seaLevel = math.min(SizeY - 1, (int)math.floor(seaLevel)),
            waterBlockId = (byte)BlockType.Water,
            waterIsSolid = blockMappings[(int)BlockType.Water].isSolid
        };
        waterChunkDataHandle = stagedFillWaterAboveTerrainJob.Schedule(dataTotalHeightPoints, 32, oreChunkDataHandle);

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
        var buildChunkSnapshotJob = new BuildChunkSnapshotAndFlagsJob
        {
            blockTypes = blockTypes,
            voxelSnapshot = chunkVoxelSnapshot,
            subchunkNonEmpty = subchunkNonEmpty,
            borderSize = dataBorderSize
        };
        JobHandle buildChunkSnapshotHandle = buildChunkSnapshotJob.Schedule(finalChunkDataHandle);

        var populateLightOpacityJob = new PopulateLightOpacityJob
        {
            columnContexts = lightColumnContexts,
            opacity = lightOpacityData,
            effectiveOpacityByBlock = effectiveOpacityByBlock,
            border = lightBorderSize
        };
        JobHandle populateLightOpacityHandle = populateLightOpacityJob.Schedule(lightTotalHeightPoints, 32, lightColumnContextHandle);

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
        var disposeLightColumnContextCacheJob = new DisposeTerrainColumnContextArrayJob
        {
            values = lightColumnContexts
        };
        JobHandle disposeLightColumnContextHandle = disposeLightColumnContextCacheJob.Schedule(lightOpacityTerrainHandle);
        var disposeSpaghettiCarveMaskJob = new DisposeByteArrayJob
        {
            values = sharedSpaghettiCarveMask
        };
        JobHandle disposeSpaghettiCarveMaskHandle = disposeSpaghettiCarveMaskJob.Schedule(
            useSharedSpaghettiCarveMask
                ? JobHandle.CombineDependencies(finalChunkDataHandle, lightOpacityTerrainHandle)
                : finalChunkDataHandle);

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
            disposeLightColumnContextHandle);
        lightOpacityCleanupHandle = JobHandle.CombineDependencies(
            lightOpacityCleanupHandle,
            disposeSpaghettiCarveMaskHandle);
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
        var disposeDataColumnContextCacheAfterLightingJob = new DisposeTerrainColumnContextArrayJob
        {
            values = dataColumnContexts
        };
        JobHandle disposeDataColumnContextAfterLightingHandle = disposeDataColumnContextCacheAfterLightingJob.Schedule(finalChunkDataHandle);
        dataHandle = JobHandle.CombineDependencies(
            lightJob.Schedule(JobHandle.CombineDependencies(finalChunkDataHandle, lightOpacityHandle)),
            disposeDataColumnContextAfterLightingHandle,
            buildChunkSnapshotHandle);
    }

    public static void ScheduleMeshJob(
        NativeArray<int> heightCache,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<byte> light,
        NativeArray<BlockTextureMapping> nativeBlockMappings,
        NativeArray<int3> suppressedGrassBillboards,
        NativeArray<bool> subchunkNonEmpty,
        NativeArray<byte> knownVoxelData,
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
        float aoStrength,
        float aoCurveExponent,
        float aoMinLight,
        bool useFastBedrockStyleMeshing,
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
        // 1. AlocaÃƒÂ§ÃƒÂµes das Listas de Mesh (Output)
        vertices = new NativeList<PackedChunkVertex>(4096, Allocator.Persistent);
        opaqueTriangles = new NativeList<int>(4096 * 3, Allocator.Persistent);
        waterTriangles = new NativeList<int>(4096 * 3, Allocator.Persistent);
        transparentTriangles = new NativeList<int>(4096 * 3, Allocator.Persistent);
        billboardTriangles = new NativeList<int>(2048 * 3, Allocator.Persistent);
        subchunkRanges = new NativeArray<SubchunkMeshRange>(SubchunksPerColumn, Allocator.Persistent);
        subchunkVisibilityMasks = new NativeArray<ulong>(SubchunksPerColumn, Allocator.Persistent);

        // ==========================================
        // JOB 2: GeraÃƒÂ§ÃƒÂ£o da Malha (Mesh)
        // ==========================================
        var meshJob = new ChunkMeshJob
        {
            startY = 0,
            endY = 0,
            blockTypes = blockTypes,
            solids = solids,
            light = light, // Usa a luz previamente calculada e passada por parÃƒÂ¢metro
            heightCache = heightCache,
            blockMappings = nativeBlockMappings,
            suppressedGrassBillboards = suppressedGrassBillboards,
            subchunkNonEmpty = subchunkNonEmpty,
            knownVoxelData = knownVoxelData,

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
            aoStrength = aoStrength,
            aoCurveExponent = aoCurveExponent,
            aoMinLight = aoMinLight,
            useFastBedrockStyleMeshing = useFastBedrockStyleMeshing,
            dirtySubchunkMask = dirtySubchunkMask,
            subchunkRanges = subchunkRanges,

            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            waterTriangles = waterTriangles,
            transparentTriangles = transparentTriangles,
            billboardTriangles = billboardTriangles,
            subchunkVisibilityMasks = subchunkVisibilityMasks
        };
        // O MeshJob agora ÃƒÂ© agendado independentemente, assumindo que os dados intermediÃƒÂ¡rios jÃƒÂ¡ estÃƒÂ£o prontos
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
        [ReadOnly] public NativeArray<bool> solids;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [ReadOnly] public NativeArray<byte> light;
        [ReadOnly] public NativeArray<int3> suppressedGrassBillboards;
        [ReadOnly] public NativeArray<bool> subchunkNonEmpty;
        [ReadOnly] public NativeArray<byte> knownVoxelData;

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
        public float aoStrength;
        public float aoCurveExponent;
        public float aoMinLight;
        public bool useFastBedrockStyleMeshing;
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

            return mapping.isSolid && mapping.renderShape == BlockRenderShape.Cube;
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
            int voxelPlaneSize = voxelSizeX * SizeY;
            bool generateGrassBillboards = enableGrassBillboards && grassBillboardChance > 0f;
            float noiseScale = 0f;
            float jitter = 0f;
            Vector2 grassBillboardAtlasUv = default;
            float grassBillboardTint = 0f;
            if (generateGrassBillboards)
            {
                BlockTextureMapping grassBillboardMapping = blockMappings[(int)grassBillboardBlockType];
                Vector2Int grassBillboardTile = grassBillboardMapping.GetTileCoord(BlockFace.Front);
                grassBillboardAtlasUv = new Vector2(
                    grassBillboardTile.x * invAtlasTilesX + 0.001f,
                    grassBillboardTile.y * invAtlasTilesY + 0.001f
                );
                grassBillboardTint = grassBillboardMapping.GetTint(BlockFace.Front) ? 1f : 0f;
                noiseScale = math.max(1e-4f, grassBillboardNoiseScale);
                jitter = math.clamp(grassBillboardJitter, 0f, 0.35f);
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
                            if (!mapping.isEmpty && mapping.renderShape != BlockRenderShape.Cube)
                            {
                                float specialLight01 = GetSpecialMeshLight01(idx, voxelSizeX, light);
                                Vector3 origin = new Vector3(x - border, y, z - border);

                                switch (mapping.renderShape)
                                {
                                    case BlockRenderShape.Cross:
                                        AddCrossShape(origin, mapping, blockType, invAtlasTilesX, invAtlasTilesY, specialLight01);
                                        break;

                                    case BlockRenderShape.Cuboid:
                                        AddCuboidShape(origin, mapping, blockType, x, y, z, invAtlasTilesX, invAtlasTilesY, specialLight01);
                                        break;
                                }
                            }
                        }

                        if (!generateGrassBillboards ||
                            blockType != BlockType.Grass ||
                            y + 1 >= SizeY)
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
                        if (!ShouldGenerateGrassBillboard(worldX, py, worldZ, noiseScale))
                            continue;

                        uint h2 = math.hash(new int3(worldX * 17 + 3, py * 31 + 5, worldZ * 13 + 7));
                        float jx = ((((h2 >> 8) & 0xFF) / 255f) * 2f - 1f) * jitter;
                        float jz = ((((h2 >> 16) & 0xFF) / 255f) * 2f - 1f) * jitter;
                        byte packed = light[upIdx];
                        byte billboardLight = (byte)math.max(
                            (int)LightUtils.GetSkyLight(packed),
                            (int)LightUtils.GetBlockLight(packed)
                        );
                        float light01 = billboardLight / 15f;
                        Vector3 center = new Vector3((x - border) + 0.5f + jx, py - 0.02f, (z - border) + 0.5f + jz);
                        AddBillboardCross(center, grassBillboardHeight, grassBillboardAtlasUv, light01, grassBillboardTint);
                    }
                }
            }
        }

        private bool ShouldGenerateGrassBillboard(int worldX, int worldY, int worldZ, float noiseScale)
        {
            float n = noise.snoise(new float2(
                (worldX + 123.17f) * noiseScale,
                (worldZ - 91.73f) * noiseScale
            )) * 0.5f + 0.5f;

            float effectiveChance = math.saturate(
                grassBillboardChance * math.lerp(0.35f, 1.65f, n)
            );

            uint h = math.hash(new int3(worldX, worldY, worldZ));
            float chance = (h & 0x00FFFFFF) / 16777215f;
            if (chance > effectiveChance)
                return false;

            return !IsSuppressedGrassBillboard(worldX, worldY, worldZ);
        }

        private void AddBillboardCross(Vector3 center, float height, Vector2 atlasUv, float light01, float tint)
        {
            const float halfWidth = 0.38f;
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
            Vector3 upNormal = new Vector3(0f, 1f, 0f);

            Vector4 e = new Vector4(light01, tint, 1f, 0f);
            AddPackedVertex(p0, upNormal, new Vector2(0f, 0f), atlasUv, e);
            AddPackedVertex(p1, upNormal, new Vector2(1f, 0f), atlasUv, e);
            AddPackedVertex(p2, upNormal, new Vector2(1f, 1f), atlasUv, e);
            AddPackedVertex(p3, upNormal, new Vector2(0f, 1f), atlasUv, e);

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

        private void AddDoubleSidedShapeQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2 atlasUv,
            float light01,
            float tint,
            NativeList<int> tris)
        {
            int vIndex = GetCurrentSubchunkLocalVertexIndex();
            Vector3 upNormal = new Vector3(0f, 1f, 0f);

            Vector4 e = new Vector4(light01, tint, 1f, 0f);
            AddPackedVertex(p0, upNormal, new Vector2(0f, 0f), atlasUv, e);
            AddPackedVertex(p1, upNormal, new Vector2(1f, 0f), atlasUv, e);
            AddPackedVertex(p2, upNormal, new Vector2(1f, 1f), atlasUv, e);
            AddPackedVertex(p3, upNormal, new Vector2(0f, 1f), atlasUv, e);

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

        private float GetSpecialMeshLight01(int idx, int voxelSizeX, NativeArray<byte> light)
        {
            float light01 = GetResolvedLight01(light[idx]);
            int aboveIdx = idx + voxelSizeX;
            if (aboveIdx >= 0 && aboveIdx < light.Length)
                light01 = math.max(light01, GetResolvedLight01(light[aboveIdx]));

            return light01;
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

            switch (mapping.renderShape)
            {
                case BlockRenderShape.Cross:
                    min = new Vector3(0.15f, 0f, 0.15f);
                    max = new Vector3(0.85f, 1f, 0.85f);
                    return;

                case BlockRenderShape.Cuboid:
                    min = new Vector3(0.375f, 0f, 0.375f);
                    max = new Vector3(0.625f, 0.75f, 0.625f);
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

            bool neighborOpaque = blockMappings[(int)neighbor].renderShape == BlockRenderShape.Cube &&
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
                                if (currentMapping.renderShape != BlockRenderShape.Cube)
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

                                mask[maskIndex] = new GreedyFaceData
                                {
                                    blockId = (byte)current,
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
                                bool tint = m.GetTint(faceType);
                                Vector2Int tile = m.GetTileCoord(faceType);
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

                                    Vector2 uvCoord = axis == 0 ? new Vector2(rawV, rawU) :
                                                      axis == 1 ? new Vector2(rawV, rawU) :
                                                                  new Vector2(rawU, rawV);

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
                                        new Vector4(rawLight, floatTint, floatAO, 0f));
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

            if (!knownVoxelData.IsCreated)
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
            if (!knownVoxelData.IsCreated || knownVoxelData[idx] != 0)
                return true;

            int clampedX = math.clamp(x, border, border + SizeX - 1);
            int clampedZ = math.clamp(z, border, border + SizeZ - 1);
            idx = clampedX + y * voxelSizeX + clampedZ * voxelPlaneSize;
            return !knownVoxelData.IsCreated || knownVoxelData[idx] != 0;
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
            // AO deve vir de cubos cheios que realmente fecham a iluminaÃƒÂ§ÃƒÂ£o ambiente.
            // Folhas sÃƒÂ£o a exceÃƒÂ§ÃƒÂ£o: mesmo transparentes, devem sombrear como no Minecraft.
            if (mapping.renderShape != BlockRenderShape.Cube ||
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
        [DeallocateOnJobCompletion] public NativeArray<byte> knownVoxelData;
        [DeallocateOnJobCompletion] public NativeArray<bool> solids;
        [DeallocateOnJobCompletion] public NativeArray<byte> light;
        [DeallocateOnJobCompletion] public NativeArray<bool> subchunkNonEmpty; // Ã¢â€ Â NOVO
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

    public static bool ShouldApply(int dataBorderSize, int lightBorderSize, CaveGenerationMode caveGenerationMode, in SpaghettiCaveSettings settings)
    {
        return lightBorderSize > dataBorderSize &&
               caveGenerationMode == CaveGenerationMode.ModernSpaghetti &&
               settings.enabled;
    }

    public static void BuildCarveMask(
        Vector2Int coord,
        NativeArray<int> heightCache,
        NativeArray<byte> carveMask,
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












