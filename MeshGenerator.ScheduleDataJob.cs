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
    public static void ScheduleDataJob(
        Vector2Int coord,
        NativeArray<NoiseLayer> noiseLayers,
        NativeArray<BlockTextureMapping> blockMappings,
        NativeArray<byte> effectiveOpacityByBlock,
        NativeArray<ushort> lightEmissionByBlock,
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
        NativeArray<ushort> lightData,
        NativeArray<byte> chunkVoxelSnapshot,
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
        subchunkNonEmpty = RentBoolBuffer(SubchunksPerColumn);
        subchunkColliderOccupancy = RentUlongBuffer(SubchunksPerColumn * Chunk.ColliderOccupancyWordsPerSubchunk);

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

        heightCache = RentIntBuffer(dataTotalHeightPoints);
        blockTypes = RentByteBuffer(dataTotalVoxels);
        solids = RentBoolBuffer(dataTotalVoxels);
        NativeArray<bool> baseTerrainSolids = new NativeArray<bool>(dataTotalVoxels, Allocator.Persistent);
        light = RentUshortBuffer(dataTotalVoxels);
        blockEmissionData = default;
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
            JobHandle dataSpaghettiCarveMaskHandle = default;
            JobHandle dataSpaghettiMaskCacheStoreHandle = default;
            JobHandle dataDisposePrefilledSpaghettiColumnsHandle = default;
            bool useDataSharedSpaghettiCarveMask = false;
            if (spaghettiCaveSettings.enabled)
            {
                if (sharedSpaghettiCarveMask.IsCreated)
                    sharedSpaghettiCarveMask.Dispose();

                useDataSharedSpaghettiCarveMask = TryScheduleSharedSpaghettiCarveMask(
                    coord,
                    noiseLayers,
                    baseHeight,
                    globalOffsetX,
                    globalOffsetZ,
                    biomeNoiseSettings,
                    oreSeed,
                    spaghettiCaveSettings,
                    heightCache,
                    heightHandle,
                    dataBorderSize,
                    dataVoxelSizeX,
                    dataVoxelSizeZ,
                    dataVoxelPlaneSize,
                    dataTotalVoxels,
                    out sharedSpaghettiCarveMask,
                    out dataSpaghettiCarveMaskHandle,
                    out dataSpaghettiMaskCacheStoreHandle,
                    out dataDisposePrefilledSpaghettiColumnsHandle);

                if (useDataSharedSpaghettiCarveMask)
                {
                    baseChunkDataJob.spaghettiCarveMask = sharedSpaghettiCarveMask;
                    baseChunkDataJob.spaghettiCarveMaskVoxelSizeX = dataVoxelSizeX;
                    baseChunkDataJob.spaghettiCarveMaskVoxelPlaneSize = dataVoxelPlaneSize;
                    baseChunkDataJob.spaghettiCarveMaskOffsetX = 0;
                    baseChunkDataJob.spaghettiCarveMaskOffsetZ = 0;
                }
            }

            JobHandle caveDependency = JobHandle.CombineDependencies(surfaceMaterialHandle, copyBaseTerrainSolidsHandle);
            if (useDataSharedSpaghettiCarveMask)
                caveDependency = JobHandle.CombineDependencies(caveDependency, dataSpaghettiCarveMaskHandle);

            var caveChunkDataJob = baseChunkDataJob;
            caveChunkDataJob.stages = ChunkData.ChunkDataStageFlags.Caves;
            caveChunkDataHandle = caveChunkDataJob.Schedule(caveDependency);

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
            ushort fullBright = LightUtils.PackLight(15, 0);
            for (int i = 0; i < light.Length; i++)
                light[i] = fullBright;

            var disposeEmptySpaghettiCarveMaskJob = new DisposeByteArrayJob
            {
                values = sharedSpaghettiCarveMask
            };
            JobHandle disposeSpaghettiCarveMaskDependency = useDataSharedSpaghettiCarveMask
                ? JobHandle.CombineDependencies(finalChunkDataHandle, dataSpaghettiMaskCacheStoreHandle)
                : finalChunkDataHandle;
            JobHandle disposeEmptySpaghettiCarveMaskHandle = disposeEmptySpaghettiCarveMaskJob.Schedule(disposeSpaghettiCarveMaskDependency);

            dataHandle = snapshotHandle;
            dataHandle = JobHandle.CombineDependencies(dataHandle, disposeEmptySpaghettiCarveMaskHandle);
            if (useDataSharedSpaghettiCarveMask)
                dataHandle = JobHandle.CombineDependencies(dataHandle, dataDisposePrefilledSpaghettiColumnsHandle);
            dataHandle = JobHandle.CombineDependencies(dataHandle, disposeBaseTerrainSolidsHandle);
            terrainDataHandle = dataHandle;
            lightingHandle = dataHandle;
            return;
        }

        blockEmissionData = RentUshortBuffer(lightTotalVoxels, true);
        lightOpacityData = RentByteBuffer(lightTotalVoxels);
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
        bool useSharedSpaghettiCarveMask = spaghettiCaveSettings.enabled;
        JobHandle spaghettiCarveMaskHandle = default;
        JobHandle disposePrefilledSpaghettiColumnsHandle = default;
        JobHandle spaghettiMaskCacheStoreHandle = default;
        if (useSharedSpaghettiCarveMask)
        {
            // A mesma mascara e compartilhada entre terreno e opacidade para que
            // cavernas e iluminacao concordem exatamente nas bordas.
            if (sharedSpaghettiCarveMask.IsCreated)
                sharedSpaghettiCarveMask.Dispose();

            useSharedSpaghettiCarveMask = TryScheduleSharedSpaghettiCarveMask(
                coord,
                noiseLayers,
                baseHeight,
                globalOffsetX,
                globalOffsetZ,
                biomeNoiseSettings,
                oreSeed,
                spaghettiCaveSettings,
                lightHeightCache,
                lightHeightHandle,
                lightBorderSize,
                lightVoxelSizeX,
                lightVoxelSizeZ,
                lightVoxelPlaneSize,
                lightTotalVoxels,
                out sharedSpaghettiCarveMask,
                out spaghettiCarveMaskHandle,
                out spaghettiMaskCacheStoreHandle,
                out disposePrefilledSpaghettiColumnsHandle);

            if (useSharedSpaghettiCarveMask)
            {
                baseChunkDataJob.spaghettiCarveMask = sharedSpaghettiCarveMask;
                baseChunkDataJob.spaghettiCarveMaskVoxelSizeX = lightVoxelSizeX;
                baseChunkDataJob.spaghettiCarveMaskVoxelPlaneSize = lightVoxelPlaneSize;
                baseChunkDataJob.spaghettiCarveMaskOffsetX = lightBorderSize - dataBorderSize;
                baseChunkDataJob.spaghettiCarveMaskOffsetZ = lightBorderSize - dataBorderSize;
            }
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

        var copyGeneratedEmissionJob = new CopyGeneratedEmissionToLightVolumeJob
        {
            sourceBlockTypes = blockTypes,
            lightEmissionByBlock = lightEmissionByBlock,
            targetBlockEmission = blockEmissionData,
            sourceVoxelSizeX = dataVoxelSizeX,
            targetVoxelSizeX = lightVoxelSizeX,
            targetVoxelPlaneSize = lightVoxelPlaneSize,
            sourceBorder = dataBorderSize,
            targetBorder = lightBorderSize
        };
        JobHandle blockEmissionHandle = copyGeneratedEmissionJob.Schedule(
            dataTotalVoxels,
            128,
            finalChunkDataHandle);
        if (blockEdits.IsCreated && blockEdits.Length > 0)
        {
            var emissionOverrideJob = new ApplyEmissionOverridesJob
            {
                overrides = blockEdits,
                lightEmissionByBlock = lightEmissionByBlock,
                blockEmission = blockEmissionData,
                chunkMinX = coord.x * SizeX,
                chunkMinZ = coord.y * SizeZ,
                borderSize = lightBorderSize,
                voxelSizeX = lightVoxelSizeX,
                voxelSizeZ = lightVoxelSizeZ,
                voxelPlaneSize = lightVoxelPlaneSize
            };
            blockEmissionHandle = emissionOverrideJob.Schedule(blockEmissionHandle);
        }

        var lightJob = new ChunkLighting.CroppedChunkLightingJob
        {
            opacity = lightOpacityData,
            light = light,
            blockLightData = lightData,
            blockEmissionData = blockEmissionData,
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
        terrainDataHandle = JobHandle.CombineDependencies(
            finalChunkDataHandle,
            buildChunkSnapshotHandle,
            disposeBaseTerrainSolidsAfterLightingHandle);
        lightingHandle = lightJob.Schedule(JobHandle.CombineDependencies(finalChunkDataHandle, lightOpacityHandle, blockEmissionHandle));
        dataHandle = JobHandle.CombineDependencies(terrainDataHandle, lightingHandle);
    }

    private static bool TryScheduleSharedSpaghettiCarveMask(
        Vector2Int coord,
        NativeArray<NoiseLayer> noiseLayers,
        int baseHeight,
        float globalOffsetX,
        float globalOffsetZ,
        BiomeNoiseSettings biomeNoiseSettings,
        int oreSeed,
        SpaghettiCaveSettings spaghettiCaveSettings,
        NativeArray<int> heightCache,
        JobHandle heightHandle,
        int borderSize,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        int totalVoxels,
        out NativeArray<byte> carveMask,
        out JobHandle carveMaskHandle,
        out JobHandle cacheStoreHandle,
        out JobHandle disposePrefilledColumnsHandle)
    {
        carveMask = default;
        carveMaskHandle = default;
        cacheStoreHandle = default;
        disposePrefilledColumnsHandle = default;

        if (!spaghettiCaveSettings.enabled ||
            !heightCache.IsCreated ||
            borderSize <= 0 ||
            voxelSizeX <= 0 ||
            voxelSizeZ <= 0 ||
            voxelPlaneSize <= 0 ||
            totalVoxels <= 0)
        {
            carveMask = new NativeArray<byte>(0, Allocator.Persistent);
            return false;
        }

        carveMask = new NativeArray<byte>(totalVoxels, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        int spaghettiSettingsHash = ComputeSpaghettiCarveMaskSettingsHash(
            oreSeed,
            borderSize,
            in spaghettiCaveSettings,
            noiseLayers,
            baseHeight,
            globalOffsetX,
            globalOffsetZ,
            in biomeNoiseSettings);

        if (TryGetReusableSpaghettiCarveMaskCacheEntry(
                coord,
                spaghettiSettingsHash,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                borderSize,
                out SpaghettiCarveMaskCacheEntry cachedEntry))
        {
            NativeArray<byte>.Copy(cachedEntry.mask, carveMask, totalVoxels);
            return true;
        }

        NativeArray<byte> prefilledSpaghettiColumns = new NativeArray<byte>(
            voxelSizeX * voxelSizeZ,
            Allocator.Persistent,
            NativeArrayOptions.ClearMemory);
        int prefilledColumnCount = PrefillSpaghettiCarveMaskFromNeighborCache(
            coord,
            spaghettiSettingsHash,
            carveMask,
            prefilledSpaghettiColumns,
            voxelSizeX,
            voxelSizeZ,
            voxelPlaneSize,
            borderSize);

        if (prefilledColumnCount < prefilledSpaghettiColumns.Length)
        {
            var buildSpaghettiCaveCarveMaskJob = new BuildSpaghettiCaveCarveMaskJob
            {
                coord = coord,
                heightCache = heightCache,
                carveMask = carveMask,
                prefilledColumns = prefilledSpaghettiColumns,
                prefilledColumnsStride = voxelSizeX,
                borderSize = borderSize,
                oreSeed = oreSeed,
                spaghettiCaveSettings = spaghettiCaveSettings
            };
            carveMaskHandle = buildSpaghettiCaveCarveMaskJob.Schedule(heightHandle);
        }

        var disposePrefilledSpaghettiColumnsJob = new DisposeByteArrayJob
        {
            values = prefilledSpaghettiColumns
        };
        disposePrefilledColumnsHandle = disposePrefilledSpaghettiColumnsJob.Schedule(carveMaskHandle);

        NativeArray<byte> cachedMaskCopy = RentByteBuffer(totalVoxels);
        var copySpaghettiMaskToCacheJob = new CopyByteArrayJob
        {
            source = carveMask,
            destination = cachedMaskCopy
        };
        cacheStoreHandle = copySpaghettiMaskToCacheJob.Schedule(totalVoxels, 128, carveMaskHandle);
        StoreSpaghettiCarveMaskNeighborCacheEntry(
            coord,
            cachedMaskCopy,
            cacheStoreHandle,
            voxelSizeX,
            voxelSizeZ,
            voxelPlaneSize,
            borderSize,
            spaghettiSettingsHash);

        return true;
    }
}
