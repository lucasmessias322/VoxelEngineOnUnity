using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Chunk Requests & Rebuilds

    private bool RequestChunk(Vector2Int coord, bool allowEmergencyPoolCreate = false)
    {
        if (chunkPool.Count == 0)
            ProcessRetiredChunksAwaitingRecycle();

        // Reuse or create chunk
        Chunk chunk = null;
        while (chunkPool.Count > 0)
        {
            Chunk candidate = chunkPool.Dequeue();
            if (!CanUsePooledChunk(candidate))
                continue;

            chunk = candidate;
            break;
        }

        if (chunk != null)
        {
            try { chunk.ResetChunk(); } catch { }
            chunk.pendingRecycle = false;
            chunk.jobScheduled = false;
            chunk.hasVoxelData = false;
            chunk.currentJob = default;
            chunk.state = Chunk.ChunkState.Inactive;
        }
        else
        {
            if (!allowEmergencyPoolCreate &&
                chunkPoolCreatesThisFrame >= Mathf.Max(0, maxEmergencyChunkPoolCreatesPerFrame))
            {
                return false;
            }

            chunk = CreateChunkPoolEntry();
            if (chunk == null)
                return false;

            chunkPoolCreatesThisFrame++;
        }

        Vector3 pos = new Vector3(coord.x * Chunk.SizeX, 0, coord.y * Chunk.SizeZ);
        chunk.transform.position = pos;
        chunk.UpdateWorldBounds(); // garante bounds atualizado
        chunk.SetCoord(coord);

        int expectedGen = nextChunkGeneration++;
        chunk.generation = expectedGen;
        Vector2Int currentChunkCoord = GetCurrentPlayerChunkCoord();
        bool useDetailedGeneration = ShouldChunkStartDetailedGeneration(coord, currentChunkCoord);
        bool shouldQueueDetailPromotion = enableChunkDetailLod &&
                                         !useDetailedGeneration &&
                                         ShouldChunkUseDetailedGeneration(coord, currentChunkCoord);
        PrepareChunkDetailGenerationTarget(chunk, useDetailedGeneration, expectedGen);

        if (!chunk.voxelData.IsCreated)
        {
            chunk.voxelData = new NativeArray<byte>(Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ, Allocator.Persistent);
            chunk.hasVoxelData = false;
        }

        activeChunks.Add(coord, chunk);
        if (shouldQueueDetailPromotion)
            EnqueueChunkDetailPromotion(coord);
        RequestHighBuildMeshRebuild(coord);

        // Coleta overrides do jogador antes da geracao para que o chunk ja nasca consistente.
        requestChunkEditsBuffer.Clear();
        int dataBorderSize = GetDetailedGenerationBorderSize();
        int lightBorderSize = Mathf.Max(dataBorderSize, GetLightSmoothingBorderSize());
        int overrideBorderSize = Mathf.Max(dataBorderSize, lightBorderSize);
        PrimeNearbyOverrideLightSources(coord, lightBorderSize);
        AppendRelevantBlockEdits(coord, overrideBorderSize, requestChunkEditsBuffer);

        NativeArray<BlockEdit> nativeEdits;
        if (requestChunkEditsBuffer.Count > 0)
        {
            nativeEdits = new NativeArray<BlockEdit>(requestChunkEditsBuffer.Count, Allocator.Persistent);
            for (int i = 0; i < requestChunkEditsBuffer.Count; i++)
                nativeEdits[i] = requestChunkEditsBuffer[i];
        }
        else
        {
            nativeEdits = new NativeArray<BlockEdit>(0, Allocator.Persistent);
        }

        int treeMargin = GetMaxTreeMarginForGeneration();
        int detailBorderSize = GetDetailedGenerationBorderSize();
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        EnsureNativeGenerationCaches();

        // InjeÃ§Ã£o da luz global
        // Light injection corrected for rebuild (uses borderSize)
        // O volume de luz precisa do mesmo recorte padded do chunk para manter costura
        // com vizinhos e considerar colunas globais ja conhecidas.
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
        if (chunk.jobScheduled)
        {
            chunk.CompleteTrackedJob();
            chunk.jobScheduled = false;
            chunk.currentJob = default;
        }

        // Agendamento do data job
        // A partir daqui a geracao entra na pipeline Burst/Jobs:
        // heightmap, superficie, cavernas, minerios, agua, arvores, edits e iluminacao.
        MeshGenerator.ScheduleDataJob(
            coord, cachedNativeNoiseLayers, cachedNativeBlockMappings, cachedNativeEffectiveLightOpacityByBlock, cachedNativeLightEmissionByBlock,
            baseHeight, IsFlatWorldMode(), GetResolvedFlatWorldHeight(), offsetX, offsetZ, seaLevel, enableWater,
            GetBiomeNoiseSettings(),
            GetTerrainDensitySettings(),
            seed,
            nativeEdits, treeMargin, dataBorderSize, lightBorderSize, detailBorderSize,
            GetMaxTreeRadiusForGeneration(), CliffTreshold, enableTrees,
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
            out MeshGenerator.DataJobTempBuffers dataJobTempBuffers
        );
        NativeArray<byte> knownVoxelData = CreateKnownVoxelPlaceholder();
        int dataVoxelSizeX = Chunk.SizeX + 2 * dataBorderSize;
        int dataVoxelSizeZ = Chunk.SizeZ + 2 * dataBorderSize;
        int dataVoxelPlaneSize = dataVoxelSizeX * Chunk.SizeY;
        NativeArray<byte> blockPlacementAxes = CreateDefaultPlacementAxisArray(blockTypes.Length);
        ApplyPlacementAxesFromBlockEdits(
            nativeEdits,
            blockPlacementAxes,
            chunkMinX,
            chunkMinZ,
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
            dirtySubchunkMask = GetFullSubchunkMask(),
            rebuildColliders = enableBlockColliders,
            terrainStageCompleted = false,
            lightingStageCompleted = false
        });
        pendingJobPrioritiesDirty = true;

        chunk.currentJob = dataHandle;
        chunk.jobScheduled = true;
        chunk.gameObject.SetActive(true);
        return true;
    }

    private bool TryScheduleFastChunkRebuild(Vector2Int coord, Chunk chunk, int expectedGen, int dirtySubchunkMask, bool rebuildColliders, bool targetDetailedGeneration)
    {
        if (blockData == null || blockData.mappings == null || blockData.mappings.Length == 0)
            return false;
        if (!CanChunkProvideVoxelSnapshot(chunk))
            return false;

        dirtySubchunkMask = SanitizeDirtySubchunkMask(dirtySubchunkMask);
        if (dirtySubchunkMask == 0)
            return false;

        int copyBorderSize = GetMeshNeighborPadding();
        int lightBorderSize = GetLightSmoothingBorderSize();
        int copyVoxelSizeX = Chunk.SizeX + 2 * copyBorderSize;
        int copyVoxelSizeZ = Chunk.SizeZ + 2 * copyBorderSize;
        int copyVoxelPlaneSize = copyVoxelSizeX * Chunk.SizeY;
        int copyTotalVoxels = copyVoxelSizeX * Chunk.SizeY * copyVoxelSizeZ;
        int copyTotalHeightPoints = copyVoxelSizeX * copyVoxelSizeZ;

        int lightVoxelSizeX = Chunk.SizeX + 2 * lightBorderSize;
        int lightVoxelSizeZ = Chunk.SizeZ + 2 * lightBorderSize;
        int lightVoxelPlaneSize = lightVoxelSizeX * Chunk.SizeY;
        int lightTotalVoxels = lightVoxelSizeX * Chunk.SizeY * lightVoxelSizeZ;

        int maxSnapshotBorder = Mathf.Max(copyBorderSize, lightBorderSize);
        int snapshotChunkRadius = Mathf.Max(1, Mathf.CeilToInt(maxSnapshotBorder / (float)Chunk.SizeX));
        int snapshotChunkDiameter = snapshotChunkRadius * 2 + 1;
        int snapshotChunkCount = snapshotChunkDiameter * snapshotChunkDiameter;

        EnsureNativeGenerationCaches();

        NativeArray<int> heightCache = MeshGenerator.RentIntBuffer(copyTotalHeightPoints);
        NativeArray<byte> blockTypes = MeshGenerator.RentByteBuffer(copyTotalVoxels);
        NativeArray<byte> blockPlacementAxes = CreateDefaultPlacementAxisArray(copyTotalVoxels);
        NativeArray<byte> knownVoxelData = MeshGenerator.RentByteBuffer(copyTotalVoxels);
        NativeArray<bool> solids = MeshGenerator.RentBoolBuffer(copyTotalVoxels);
        NativeArray<ushort> light = MeshGenerator.RentUshortBuffer(copyTotalVoxels);
        NativeArray<bool> subchunkNonEmpty = MeshGenerator.RentBoolBuffer(Chunk.SubchunksPerColumn);
        NativeArray<ulong> subchunkColliderOccupancy = MeshGenerator.RentUlongBuffer(Chunk.SubchunksPerColumn * Chunk.ColliderOccupancyWordsPerSubchunk);
        NativeArray<byte> lightOpacityData = default;
        NativeArray<ushort> blockLightData = default;
        NativeArray<ushort> blockEmissionData = default;
        NativeArray<byte> snapshotVoxelData = MeshGenerator.RentByteBuffer(snapshotChunkCount * FastRebuildChunkVoxelCount);
        NativeArray<byte> snapshotLoadedChunks = MeshGenerator.RentByteBuffer(snapshotChunkCount);
        NativeArray<BlockEdit> nativeOverrides = BuildFastRebuildOverrideArray(coord, maxSnapshotBorder);

        CaptureFastRebuildSnapshot(coord, snapshotChunkRadius, snapshotVoxelData, snapshotLoadedChunks);

        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        ApplyPlacementAxesFromBlockEdits(
            nativeOverrides,
            blockPlacementAxes,
            chunkMinX,
            chunkMinZ,
            copyBorderSize,
            copyVoxelSizeX,
            copyVoxelSizeZ,
            copyVoxelPlaneSize);
        if (enableVoxelLighting)
        {
            lightOpacityData = MeshGenerator.RentByteBuffer(lightTotalVoxels);
            blockLightData = MeshGenerator.RentUshortBuffer(lightTotalVoxels, true);
            blockEmissionData = MeshGenerator.RentUshortBuffer(lightTotalVoxels, true);
            if (globalLightColumns.Count > 0)
                InjectGlobalLightColumns(blockLightData, chunkMinX, chunkMinZ, lightBorderSize, lightVoxelSizeX, lightVoxelSizeZ, lightVoxelPlaneSize);
        }

        var copyPopulateJob = new FastRebuildPopulateBlocksJob
        {
            snapshotVoxelData = snapshotVoxelData,
            snapshotLoadedChunks = snapshotLoadedChunks,
            blockTypes = blockTypes,
            knownVoxelData = knownVoxelData,
            disableWater = !enableWater || IsFlatWorldMode(),
            borderSize = copyBorderSize,
            voxelSizeX = copyVoxelSizeX,
            voxelPlaneSize = copyVoxelPlaneSize,
            snapshotChunkRadius = snapshotChunkRadius,
            snapshotChunkDiameter = snapshotChunkDiameter
        };
        JobHandle copyPopulateHandle = copyPopulateJob.Schedule(copyTotalVoxels, 128);

        JobHandle copyOverrideHandle = copyPopulateHandle;
        if (nativeOverrides.IsCreated && nativeOverrides.Length > 0)
        {
            var copyOverrideJob = new FastRebuildApplyBlockOverridesJob
            {
                overrides = nativeOverrides,
                blockTypes = blockTypes,
                knownVoxelData = knownVoxelData,
                chunkMinX = chunkMinX,
                chunkMinZ = chunkMinZ,
                borderSize = copyBorderSize,
                voxelSizeX = copyVoxelSizeX,
                voxelSizeZ = copyVoxelSizeZ,
                voxelPlaneSize = copyVoxelPlaneSize
            };
            copyOverrideHandle = copyOverrideJob.Schedule(copyPopulateHandle);
        }

        var deriveDataJob = new FastRebuildDerivedDataJob
        {
            blockTypes = blockTypes,
            blockMappings = cachedNativeBlockMappings,
            solids = solids,
            heightCache = heightCache,
            subchunkNonEmpty = subchunkNonEmpty,
            subchunkColliderOccupancy = subchunkColliderOccupancy,
            borderSize = copyBorderSize,
            voxelSizeX = copyVoxelSizeX,
            voxelSizeZ = copyVoxelSizeZ,
            voxelPlaneSize = copyVoxelPlaneSize
        };
        JobHandle deriveDataHandle = deriveDataJob.Schedule(copyOverrideHandle);

        JobHandle visualDataHandle;
        if (enableVoxelLighting)
        {
            var opacityPopulateJob = new FastRebuildPopulateOpacityJob
            {
                snapshotVoxelData = snapshotVoxelData,
                snapshotLoadedChunks = snapshotLoadedChunks,
                effectiveOpacityByBlock = cachedNativeEffectiveLightOpacityByBlock,
                opacity = lightOpacityData,
                disableWater = !enableWater || IsFlatWorldMode(),
                borderSize = lightBorderSize,
                voxelSizeX = lightVoxelSizeX,
                snapshotChunkRadius = snapshotChunkRadius,
                snapshotChunkDiameter = snapshotChunkDiameter
            };
            JobHandle opacityPopulateHandle = opacityPopulateJob.Schedule(lightTotalVoxels, 128);

            JobHandle opacityOverrideHandle = opacityPopulateHandle;
            if (nativeOverrides.IsCreated && nativeOverrides.Length > 0)
            {
                var opacityOverrideJob = new FastRebuildApplyOpacityOverridesJob
                {
                    overrides = nativeOverrides,
                    effectiveOpacityByBlock = cachedNativeEffectiveLightOpacityByBlock,
                    opacity = lightOpacityData,
                    chunkMinX = chunkMinX,
                    chunkMinZ = chunkMinZ,
                    borderSize = lightBorderSize,
                    voxelSizeX = lightVoxelSizeX,
                    voxelSizeZ = lightVoxelSizeZ,
                    voxelPlaneSize = lightVoxelPlaneSize
                };
                opacityOverrideHandle = opacityOverrideJob.Schedule(opacityPopulateHandle);
            }

            var emissionPopulateJob = new FastRebuildPopulateEmissionJob
            {
                snapshotVoxelData = snapshotVoxelData,
                snapshotLoadedChunks = snapshotLoadedChunks,
                lightEmissionByBlock = cachedNativeLightEmissionByBlock,
                blockEmission = blockEmissionData,
                disableWater = !enableWater || IsFlatWorldMode(),
                borderSize = lightBorderSize,
                voxelSizeX = lightVoxelSizeX,
                snapshotChunkRadius = snapshotChunkRadius,
                snapshotChunkDiameter = snapshotChunkDiameter
            };
            JobHandle emissionPopulateHandle = emissionPopulateJob.Schedule(lightTotalVoxels, 128);

            JobHandle emissionOverrideHandle = emissionPopulateHandle;
            if (nativeOverrides.IsCreated && nativeOverrides.Length > 0)
            {
                var emissionOverrideJob = new FastRebuildApplyEmissionOverridesJob
                {
                    overrides = nativeOverrides,
                    lightEmissionByBlock = cachedNativeLightEmissionByBlock,
                    blockEmission = blockEmissionData,
                    chunkMinX = chunkMinX,
                    chunkMinZ = chunkMinZ,
                    borderSize = lightBorderSize,
                    voxelSizeX = lightVoxelSizeX,
                    voxelSizeZ = lightVoxelSizeZ,
                    voxelPlaneSize = lightVoxelPlaneSize
                };
                emissionOverrideHandle = emissionOverrideJob.Schedule(emissionPopulateHandle);
            }

            var lightJob = new ChunkLighting.CroppedChunkLightingJob
            {
                opacity = lightOpacityData,
                light = light,
                blockLightData = blockLightData,
                blockEmissionData = blockEmissionData,
                enableHorizontalSkylight = enableHorizontalSkylight,
                horizontalSkylightStepLoss = horizontalSkylightStepLoss,
                inputVoxelSizeX = lightVoxelSizeX,
                inputVoxelSizeZ = lightVoxelSizeZ,
                inputTotalVoxels = lightTotalVoxels,
                inputVoxelPlaneSize = lightVoxelPlaneSize,
                outputVoxelSizeX = copyVoxelSizeX,
                outputVoxelSizeZ = copyVoxelSizeZ,
                outputVoxelPlaneSize = copyVoxelPlaneSize,
                outputOffsetX = lightBorderSize - copyBorderSize,
                outputOffsetZ = lightBorderSize - copyBorderSize,
                SizeY = Chunk.SizeY,
            };

            visualDataHandle = lightJob.Schedule(JobHandle.CombineDependencies(deriveDataHandle, opacityOverrideHandle, emissionOverrideHandle));
        }
        else
        {
            ushort fullBright = LightUtils.PackLight(15, 0);
            for (int i = 0; i < light.Length; i++)
                light[i] = fullBright;

            visualDataHandle = deriveDataHandle;
        }

        pendingDataJobs.Add(new PendingData
        {
            handle = visualDataHandle,
            terrainHandle = deriveDataHandle,
            lightingHandle = visualDataHandle,
            heightCache = heightCache,
            blockTypes = blockTypes,
            blockPlacementAxes = blockPlacementAxes,
            knownVoxelData = knownVoxelData,
            useKnownVoxelData = true,
            solids = solids,
            light = light,
            borderSize = copyBorderSize,
            chunk = chunk,
            coord = coord,
            expectedGen = expectedGen,
            targetDetailedGeneration = targetDetailedGeneration,
            chunkLightData = blockLightData,
            blockEmissionData = blockEmissionData,
            lightOpacityData = lightOpacityData,
            edits = default,
            fastRebuildSnapshotVoxelData = snapshotVoxelData,
            fastRebuildSnapshotLoadedChunks = snapshotLoadedChunks,
            fastRebuildOverrides = nativeOverrides,
            tempBuffers = default,
            subchunkColliderOccupancy = subchunkColliderOccupancy,
            subchunkNonEmpty = subchunkNonEmpty,
            dirtySubchunkMask = dirtySubchunkMask,
            rebuildColliders = rebuildColliders,
            terrainStageCompleted = false,
            lightingStageCompleted = false
        });
        pendingJobPrioritiesDirty = true;

        chunk.currentJob = visualDataHandle;
        chunk.jobScheduled = true;
        chunk.state = Chunk.ChunkState.MeshReady;
        return true;
    }

    private void CaptureFastRebuildSnapshot(
        Vector2Int coord,
        int chunkRadius,
        NativeArray<byte> snapshotVoxelData,
        NativeArray<byte> snapshotLoadedChunks)
    {
        if (!snapshotVoxelData.IsCreated || !snapshotLoadedChunks.IsCreated)
            return;

        int slot = 0;
        for (int dz = -chunkRadius; dz <= chunkRadius; dz++)
        {
            for (int dx = -chunkRadius; dx <= chunkRadius; dx++, slot++)
            {
                Vector2Int sourceCoord = new Vector2Int(coord.x + dx, coord.y + dz);
                bool isLoaded = activeChunks.TryGetValue(sourceCoord, out Chunk sourceChunk) &&
                                CanChunkProvideVoxelSnapshot(sourceChunk);

                snapshotLoadedChunks[slot] = isLoaded ? (byte)1 : (byte)0;
                if (!isLoaded)
                    continue;

                NativeArray<byte>.Copy(
                    sourceChunk.voxelData,
                    0,
                    snapshotVoxelData,
                    slot * FastRebuildChunkVoxelCount,
                    FastRebuildChunkVoxelCount);
            }
        }
    }

    private NativeArray<BlockEdit> BuildFastRebuildOverrideArray(Vector2Int coord, int borderSize)
    {
        if (blockOverrides.Count == 0)
            return new NativeArray<BlockEdit>(0, Allocator.Persistent);

        fastRebuildOverrideEditsBuffer.Clear();
        AppendRelevantBlockEdits(coord, borderSize, fastRebuildOverrideEditsBuffer);
        NativeArray<BlockEdit> nativeOverrides = new NativeArray<BlockEdit>(fastRebuildOverrideEditsBuffer.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        for (int i = 0; i < fastRebuildOverrideEditsBuffer.Count; i++)
            nativeOverrides[i] = fastRebuildOverrideEditsBuffer[i];

        return nativeOverrides;
    }

    private void FillFastRebuildArraysFromLoadedChunks(
        Vector2Int coord,
        int borderSize,
        NativeArray<int> heightCache,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<bool> subchunkNonEmpty)
    {
        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
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

                bool hasLoadedColumn = TryResolveLoadedColumn(worldX, worldZ, out Chunk srcChunk, out int srcX, out int srcZ);
                int srcColumnBase = srcX + srcZ * Chunk.SizeX;
                int highestSolidY = 0;

                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    BlockType bt;
                    if (hasLoadedColumn)
                    {
                        int srcIdx = srcColumnBase + y * Chunk.SizeX * Chunk.SizeZ;
                        bt = ResolveWaterStateForDebug((BlockType)srcChunk.voxelData[srcIdx]);
                    }
                    else
                    {
                        bt = y <= 2 ? BlockType.Bedrock : BlockType.Air;
                    }

                    int idx = ix + y * voxelSizeX + iz * voxelPlaneSize;
                    blockTypes[idx] = (byte)bt;

                    bool isSolid = mappings[(int)bt].isSolid;
                    solids[idx] = isSolid;
                    if (isSolid) highestSolidY = y;

                    if (lx >= 0 && lx < Chunk.SizeX && lz >= 0 && lz < Chunk.SizeZ && bt != BlockType.Air)
                    {
                        int subIdx = y / Chunk.SubchunkHeight;
                        if (subIdx >= 0 && subIdx < Chunk.SubchunksPerColumn)
                            subchunkNonEmpty[subIdx] = true;
                    }
                }

                heightCache[ix + iz * heightStride] = highestSolidY;
            }
        }

        ApplyCurrentBlockOverridesToChunkData(coord, blockTypes, solids, subchunkNonEmpty, heightCache, borderSize, default);
    }

    private void FillFastRebuildLightOpacityFromLoadedChunks(
        Vector2Int coord,
        int borderSize,
        NativeArray<byte> opacity)
    {
        if (!opacity.IsCreated || blockData == null || blockData.mappings == null || blockData.mappings.Length == 0)
            return;

        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * Chunk.SizeY;
        int chunkMinX = coord.x * Chunk.SizeX;
        int chunkMinZ = coord.y * Chunk.SizeZ;
        BlockTextureMapping[] mappings = blockData.mappings;

        for (int lz = -borderSize; lz < Chunk.SizeZ + borderSize; lz++)
        {
            int worldZ = chunkMinZ + lz;
            int iz = lz + borderSize;

            for (int lx = -borderSize; lx < Chunk.SizeX + borderSize; lx++)
            {
                int worldX = chunkMinX + lx;
                int ix = lx + borderSize;

                bool hasLoadedColumn = TryResolveLoadedColumn(worldX, worldZ, out Chunk srcChunk, out int srcX, out int srcZ);
                int srcColumnBase = srcX + srcZ * Chunk.SizeX;

                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    BlockType bt;
                    if (hasLoadedColumn)
                    {
                        int srcIdx = srcColumnBase + y * Chunk.SizeX * Chunk.SizeZ;
                        bt = ResolveWaterStateForDebug((BlockType)srcChunk.voxelData[srcIdx]);
                    }
                    else
                    {
                        bt = y <= 2 ? BlockType.Bedrock : BlockType.Air;
                    }

                    int idx = ix + y * voxelSizeX + iz * voxelPlaneSize;
                    opacity[idx] = ChunkLighting.GetEffectiveOpacity(mappings[(int)bt]);
                }
            }
        }

        ApplyCurrentBlockOverridesToLightOpacity(coord, borderSize, opacity);
    }

    private void ApplyCurrentBlockOverridesToLightOpacity(
        Vector2Int coord,
        int borderSize,
        NativeArray<byte> opacity)
    {
        if (blockOverrides.Count == 0 ||
            !opacity.IsCreated ||
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
        for (int i = 0; i < relevantTerrainOverridePositions.Count; i++)
        {
            Vector3Int worldPos = relevantTerrainOverridePositions[i];
            if (worldPos.y < 0 || worldPos.y >= Chunk.SizeY)
                continue;
            if (!blockOverrides.TryGetValue(worldPos, out BlockType overrideType))
                continue;
            overrideType = ResolveWaterStateForDebug(overrideType);

            int ix = worldPos.x - chunkMinX + borderSize;
            int iz = worldPos.z - chunkMinZ + borderSize;
            if (ix < 0 || ix >= voxelSizeX || iz < 0 || iz >= voxelSizeZ)
                continue;

            int idx = ix + worldPos.y * voxelSizeX + iz * voxelPlaneSize;
            opacity[idx] = ChunkLighting.GetEffectiveOpacity(blockData.mappings[(int)overrideType]);
        }
    }

    private bool TryResolveLoadedColumn(int worldX, int worldZ, out Chunk chunk, out int localX, out int localZ)
    {
        Vector2Int coord = new Vector2Int(
            Mathf.FloorToInt((float)worldX / Chunk.SizeX),
            Mathf.FloorToInt((float)worldZ / Chunk.SizeZ)
        );

        if (activeChunks.TryGetValue(coord, out chunk) && CanChunkProvideVoxelSnapshot(chunk))
        {
            localX = worldX - coord.x * Chunk.SizeX;
            localZ = worldZ - coord.y * Chunk.SizeZ;

            if (localX >= 0 && localX < Chunk.SizeX && localZ >= 0 && localZ < Chunk.SizeZ)
                return true;
        }

        chunk = null;
        localX = 0;
        localZ = 0;
        return false;
    }

    #endregion

}
