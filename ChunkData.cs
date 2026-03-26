using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;


public static class ChunkData
{
    private const int SizeX = Chunk.SizeX;
    private const int SizeY = Chunk.SizeY;
    private const int SizeZ = Chunk.SizeZ;
    private const int SpaghettiHorizontalCellSize = 4;
    private const int SpaghettiVerticalCellSize = 4;
    private const float DoubleNoiseWarp = 1.0181269f;
    private const float NoiseOffsetMagnitude = 2048f;

    [Flags]
    public enum ChunkDataStageFlags : byte
    {
        None = 0,
        Caves = 1 << 0,
        Ores = 1 << 1,
        Trees = 1 << 2,
        BlockEdits = 1 << 3
    }

    [BurstCompile]
    public struct ChunkDataJob : IJob
    {
        private struct TreeCandidateCacheEntry
        {
            public byte hasCandidate;
            public TreeInstance candidate;
        }

        public Vector2Int coord;

        [ReadOnly] public NativeArray<NoiseLayer> noiseLayers;
        [ReadOnly] public NativeArray<WarpLayer> warpLayers;

        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [ReadOnly] public NativeArray<BlockEdit> blockEdits;
        [ReadOnly] public NativeArray<OreSpawnSettings> oreSettings;
        [ReadOnly] public NativeArray<TreeSpawnRuleData> treeSpawnRules;
        public int oreSeed;
        public CaveGenerationMode caveGenerationMode;
        public WormCaveSettings caveSettings;
        public SpaghettiCaveSettings spaghettiCaveSettings;
        public int treeMargin;
        public int border;
        public int detailBorder;
        public int maxTreeRadius;
        public int baseHeight;
        public float offsetX;
        public float offsetZ;
        public float seaLevel;
        public BiomeNoiseSettings biomeNoiseSettings;


        public int CliffTreshold;
        public bool enableTrees;
        public NativeArray<int> heightCache;
        public NativeArray<byte> blockTypes;
        public NativeArray<bool> solids;

        [ReadOnly] public NativeArray<TerrainColumnContext> columnContextCache;
        public int columnContextCacheStride;
        [ReadOnly] public NativeArray<byte> spaghettiCarveMask;
        public int spaghettiCarveMaskVoxelSizeX;
        public int spaghettiCarveMaskVoxelPlaneSize;
        public int spaghettiCarveMaskOffsetX;
        public int spaghettiCarveMaskOffsetZ;
        public ChunkDataStageFlags stages;

        public void Execute()
        {
            int heightSize = SizeX + 2 * border;
            int heightStride = heightSize;

            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;



            // 2. Popular voxels (terreno, ÃƒÂ¡gua)
            //PopulateTerrainColumns(heightCache, blockTypes, solids, voxelSizeX, voxelSizeZ);

            if ((stages & ChunkDataStageFlags.Caves) != 0)
            {
                switch (caveGenerationMode)
                {
                    case CaveGenerationMode.LegacyWorms:
                        GenerateWormCaves(blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride);
                        break;

                    default:
                        if (HasSharedSpaghettiCarveMask())
                            ApplySharedSpaghettiCarveMask(blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride);
                        else
                            GenerateSpaghettiCaves(blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride);
                        break;
                }
            }

            if ((stages & ChunkDataStageFlags.Ores) != 0)
                GenerateOreVeins(blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride);

            if ((stages & ChunkDataStageFlags.Trees) != 0 && enableTrees)
            {
                // NOVO: Gere as ÃƒÂ¡rvores aqui
                NativeList<TreeInstance> trees = GenerateTreeInstances();

                // AplicaÃƒÂ§ÃƒÂ£o existente (agora com trees local)
                TreePlacement.ApplyTreeInstancesToVoxels(
                    blockTypes, solids, blockMappings, trees.AsArray(), coord, border,
                    detailBorder, SizeX, SizeZ, SizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightCache, heightStride
                );

                trees.Dispose();  // Cleanup
            }

            if ((stages & ChunkDataStageFlags.BlockEdits) != 0)
                ApplyBlockEditsToVoxels(blockTypes, solids, voxelSizeX, voxelSizeZ);

            // === NOVO: prÃƒÂ©-calcula quais subchunks tÃƒÂªm blocos ===
        }

        private bool HasSharedSpaghettiCarveMask()
        {
            return spaghettiCarveMask.IsCreated &&
                   spaghettiCarveMask.Length > 0 &&
                   spaghettiCaveSettings.enabled &&
                   spaghettiCarveMaskVoxelSizeX > 0 &&
                   spaghettiCarveMaskVoxelPlaneSize > 0;
        }

        private void ApplySharedSpaghettiCarveMask(
            NativeArray<byte> blockTypes,
            NativeArray<bool> solids,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int heightStride)
        {
            int maskVoxelSizeX = spaghettiCarveMaskVoxelSizeX;
            int maskVoxelPlaneSize = spaghettiCarveMaskVoxelPlaneSize;
            if (maskVoxelSizeX <= 0 ||
                maskVoxelPlaneSize <= 0 ||
                spaghettiCarveMaskOffsetX < 0 ||
                spaghettiCarveMaskOffsetZ < 0 ||
                spaghettiCarveMask.Length % maskVoxelPlaneSize != 0)
            {
                GenerateSpaghettiCaves(blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride);
                return;
            }

            int maskVoxelSizeZ = spaghettiCarveMask.Length / maskVoxelPlaneSize;
            if (spaghettiCarveMaskOffsetX + voxelSizeX > maskVoxelSizeX ||
                spaghettiCarveMaskOffsetZ + voxelSizeZ > maskVoxelSizeZ)
            {
                GenerateSpaghettiCaves(blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride);
                return;
            }

            for (int localZ = 0; localZ < voxelSizeZ; localZ++)
            {
                int maskZ = localZ + spaghettiCarveMaskOffsetZ;
                int targetRowBase = localZ * voxelPlaneSize;
                int maskRowBase = maskZ * maskVoxelPlaneSize + spaghettiCarveMaskOffsetX;

                for (int voxelY = 0; voxelY < SizeY; voxelY++)
                {
                    int targetIndex = targetRowBase + voxelY * voxelSizeX;
                    int maskIndex = maskRowBase + voxelY * maskVoxelSizeX;

                    for (int localX = 0; localX < voxelSizeX; localX++, targetIndex++, maskIndex++)
                    {
                        if (IsSpaghettiSeamColumn(localX, localZ))
                            continue;

                        if (spaghettiCarveMask[maskIndex] == 0)
                            continue;

                        BlockType existing = (BlockType)blockTypes[targetIndex];
                        if (existing == BlockType.Air || existing == BlockType.Water || existing == BlockType.Bedrock)
                            continue;

                        blockTypes[targetIndex] = (byte)BlockType.Air;
                        solids[targetIndex] = false;
                    }
                }
            }

            ApplySharedSpaghettiCarveMaskSeam(blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride);
        }

        private void ApplySharedSpaghettiCarveMaskSeam(
            NativeArray<byte> blockTypes,
            NativeArray<bool> solids,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int heightStride)
        {
            SpaghettiCaveSettings settings = spaghettiCaveSettings;
            if (!settings.enabled)
                return;

            int minY = math.clamp(math.min(settings.minY, settings.maxY), 3, SizeY - 2);
            int maxY = math.clamp(math.max(settings.minY, settings.maxY), 3, SizeY - 2);
            if (maxY < minY)
                return;

            int minSurfaceDepth = math.max(0, settings.minSurfaceDepth);
            int entranceSurfaceDepth = math.max(0, math.min(settings.entranceSurfaceDepth, minSurfaceDepth));
            int worldSeed = oreSeed ^ settings.seedOffset ^ 0x4b1d2e37;
            float densityBias = settings.densityBias;
            int chunkMinX = coord.x * SizeX;
            int chunkMinZ = coord.y * SizeZ;
            var caveNoiseSampler = SpaghettiCaveNoiseUtility.Create(worldSeed);

            CarveSpaghettiChunkBorderColumnsExact(
                blockTypes,
                solids,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                heightStride,
                chunkMinX,
                chunkMinZ,
                minY,
                maxY,
                minSurfaceDepth,
                entranceSurfaceDepth,
                densityBias,
                in caveNoiseSampler);
        }

        private bool IsSpaghettiSeamColumn(int localX, int localZ)
        {
            int westPaddingX = border - 1;
            int westInnerX = border;
            int eastInnerX = border + SizeX - 1;
            int eastPaddingX = border + SizeX;
            int northPaddingZ = border - 1;
            int northInnerZ = border;
            int southInnerZ = border + SizeZ - 1;
            int southPaddingZ = border + SizeZ;

            return localX == westPaddingX ||
                   localX == westInnerX ||
                   localX == eastInnerX ||
                   localX == eastPaddingX ||
                   localZ == northPaddingZ ||
                   localZ == northInnerZ ||
                   localZ == southInnerZ ||
                   localZ == southPaddingZ;
        }

        private NativeList<TreeInstance> GenerateTreeInstances()
        {
            NativeList<TreeInstance> trees = new NativeList<TreeInstance>(128, Allocator.Temp);
            for (int i = 0; i < treeSpawnRules.Length; i++)
                GenerateTreeInstancesForRule(treeSpawnRules[i], ref trees);

            return trees;
        }

        private void GenerateTreeInstancesForRule(in TreeSpawnRuleData rule, ref NativeList<TreeInstance> trees)
        {
            TreeSettings settings = rule.settings;
            int cellSize = math.max(1, settings.minSpacing);
            int chunkMinX = coord.x * SizeX;
            int chunkMinZ = coord.y * SizeZ;
            int chunkMaxX = chunkMinX + SizeX - 1;
            int chunkMaxZ = chunkMinZ + SizeZ - 1;

            int horizontalReach = TreeGenerationMetrics.GetHorizontalReach(rule.treeStyle, settings.maxHeight, settings.canopyRadius, settings.canopyHeight);
            int searchMargin = horizontalReach + settings.minSpacing;
            int cellX0 = FloorDiv(chunkMinX - searchMargin, cellSize);
            int cellX1 = FloorDiv(chunkMaxX + searchMargin, cellSize);
            int cellZ0 = FloorDiv(chunkMinZ - searchMargin, cellSize);
            int cellZ1 = FloorDiv(chunkMaxZ + searchMargin, cellSize);
            int maxSpacingRadius = TreeGenerationMetrics.GetPlacementSpacingRadius(rule.treeStyle, settings);
            int neighborCellRadius = math.max(1, (maxSpacingRadius + cellSize - 1) / cellSize);
            int cacheCapacity = GetTreeCandidateCacheCapacity(cellX0, cellX1, cellZ0, cellZ1, neighborCellRadius);
            NativeHashMap<long, TreeCandidateCacheEntry> candidateCache = new NativeHashMap<long, TreeCandidateCacheEntry>(cacheCapacity, Allocator.Temp);

            try
            {
                for (int cx = cellX0; cx <= cellX1; cx++)
                {
                    for (int cz = cellZ0; cz <= cellZ1; cz++)
                    {
                        if (!TryCreateTreeCandidateForCell(rule, cx, cz, ref candidateCache, out TreeInstance candidate))
                            continue;

                        if (candidate.worldX < chunkMinX - searchMargin || candidate.worldX > chunkMaxX + searchMargin ||
                            candidate.worldZ < chunkMinZ - searchMargin || candidate.worldZ > chunkMaxZ + searchMargin)
                        {
                            continue;
                        }

                        if (HasHigherPriorityConflictingCell(rule, cx, cz, candidate, neighborCellRadius, ref candidateCache))
                            continue;

                        if (HasNearbyTreeXZ(in trees, candidate.worldX, candidate.worldZ, candidate.spacingRadius))
                            continue;

                        trees.Add(candidate);
                    }
                }
            }
            finally
            {
                if (candidateCache.IsCreated)
                    candidateCache.Dispose();
            }
        }

        private bool TryCreateTreeCandidateForCell(
            in TreeSpawnRuleData rule,
            int cellX,
            int cellZ,
            ref NativeHashMap<long, TreeCandidateCacheEntry> candidateCache,
            out TreeInstance candidate)
        {
            long cacheKey = PackTreeCandidateCellKey(cellX, cellZ);
            if (candidateCache.TryGetValue(cacheKey, out TreeCandidateCacheEntry cached))
            {
                candidate = cached.candidate;
                return cached.hasCandidate != 0;
            }

            bool hasCandidate = TryCreateTreeCandidateForCellUncached(rule, cellX, cellZ, out candidate);
            candidateCache.TryAdd(cacheKey, new TreeCandidateCacheEntry
            {
                hasCandidate = (byte)(hasCandidate ? 1 : 0),
                candidate = candidate
            });
            return hasCandidate;
        }

        private bool TryCreateTreeCandidateForCellUncached(in TreeSpawnRuleData rule, int cellX, int cellZ, out TreeInstance candidate)
        {
            TreeSettings settings = rule.settings;
            int cellSize = math.max(1, settings.minSpacing);
            float freq = math.max(1e-4f, settings.noiseScale);
            float density = math.saturate(settings.density);
            int ruleSeed = settings.seed != 0 ? settings.seed : oreSeed;

            float noiseX = (cellX * 12.9898f + ruleSeed) * freq;
            float noiseZ = (cellZ * 78.233f + ruleSeed) * freq;
            float sample = noise.cnoise(new float2(noiseX, noiseZ)) * 0.5f + 0.5f;
            if (sample > density)
            {
                candidate = default;
                return false;
            }

            float offsetNoiseX = noise.cnoise(new float2(noiseX + 1f, noiseZ + 1f)) * 0.5f + 0.5f;
            float offsetNoiseZ = noise.cnoise(new float2(noiseX + 2f, noiseZ + 2f)) * 0.5f + 0.5f;
            int worldX = cellX * cellSize + (int)math.round(offsetNoiseX * (cellSize - 1));
            int worldZ = cellZ * cellSize + (int)math.round(offsetNoiseZ * (cellSize - 1));

            TerrainColumnContext columnContext = GetColumnContextInternal(worldX, worldZ);
            int surfaceY = columnContext.surfaceHeight;
            if (surfaceY <= 0 || surfaceY >= SizeY || surfaceY < seaLevel)
            {
                candidate = default;
                return false;
            }

            BiomeType biome = columnContext.surface.biome;
            if (biome != rule.biome)
            {
                candidate = default;
                return false;
            }

            BlockType groundType = columnContext.surface.surfaceBlock;
            bool isCactus = rule.treeStyle == TreeStyle.Cactus;
            if (isCactus)
            {
                if (groundType != BlockType.Sand)
                {
                    candidate = default;
                    return false;
                }
            }
            else if (groundType != BlockType.Grass && groundType != BlockType.Dirt && groundType != BlockType.Snow)
            {
                candidate = default;
                return false;
            }

            if (columnContext.surface.isCliff)
            {
                candidate = default;
                return false;
            }

            int minTrunk = math.max(1, settings.minHeight);
            int maxTrunk = math.max(minTrunk, settings.maxHeight);
            int canopyRadius = math.max(0, settings.canopyRadius);
            int canopyHeight = math.max(1, settings.canopyHeight);
            float heightNoise = noise.cnoise(new float2(
                (worldX + 0.1f) * 0.137f + ruleSeed * 0.001f,
                (worldZ + 0.1f) * 0.243f + ruleSeed * 0.001f)) * 0.5f + 0.5f;
            int trunkH = minTrunk + (int)math.round(heightNoise * (maxTrunk - minTrunk + 1));
            int spacingRadius = TreeGenerationMetrics.GetPlacementSpacingRadius(
                rule.treeStyle, trunkH, canopyRadius, canopyHeight, settings.minSpacing);

            candidate = new TreeInstance
            {
                worldX = worldX,
                worldZ = worldZ,
                surfaceY = surfaceY,
                trunkHeight = trunkH,
                canopyRadius = canopyRadius,
                canopyHeight = canopyHeight,
                spacingRadius = spacingRadius,
                treeStyle = rule.treeStyle
            };

            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;
            if (TreeGroundSupport.TryEvaluateStableSupportAtWorldColumn(
                    blockTypes,
                    solids,
                    coord,
                    border,
                    SizeX,
                    SizeZ,
                    SizeY,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    worldX,
                    surfaceY,
                    worldZ,
                    out bool hasStableSupport) &&
                !hasStableSupport)
            {
                candidate = default;
                return false;
            }

            return true;
        }

        private bool HasHigherPriorityConflictingCell(
            in TreeSpawnRuleData rule,
            int cellX,
            int cellZ,
            in TreeInstance candidate,
            int neighborCellRadius,
            ref NativeHashMap<long, TreeCandidateCacheEntry> candidateCache)
        {
            for (int neighborCellX = cellX - neighborCellRadius; neighborCellX <= cellX + neighborCellRadius; neighborCellX++)
            {
                for (int neighborCellZ = cellZ - neighborCellRadius; neighborCellZ <= cellZ + neighborCellRadius; neighborCellZ++)
                {
                    if (!IsHigherPriorityTreeCell(neighborCellX, neighborCellZ, cellX, cellZ))
                        continue;

                    if (!TryCreateTreeCandidateForCell(rule, neighborCellX, neighborCellZ, ref candidateCache, out TreeInstance other))
                        continue;

                    int dx = other.worldX - candidate.worldX;
                    int dz = other.worldZ - candidate.worldZ;
                    int requiredSpacing = math.max(candidate.spacingRadius, other.spacingRadius);
                    if (dx * dx + dz * dz <= requiredSpacing * requiredSpacing)
                        return true;
                }
            }

            return false;
        }

        private static long PackTreeCandidateCellKey(int cellX, int cellZ)
        {
            return ((long)cellX << 32) | (uint)cellZ;
        }

        private static int GetTreeCandidateCacheCapacity(int cellX0, int cellX1, int cellZ0, int cellZ1, int neighborCellRadius)
        {
            int searchWidth = math.max(1, cellX1 - cellX0 + 1);
            int searchDepth = math.max(1, cellZ1 - cellZ0 + 1);
            long expandedWidth = searchWidth + neighborCellRadius * 2L;
            long expandedDepth = searchDepth + neighborCellRadius * 2L;
            long capacity = expandedWidth * expandedDepth;

            if (capacity < 64L)
                capacity = 64L;
            if (capacity > int.MaxValue)
                capacity = int.MaxValue;

            return (int)capacity;
        }

        private static bool IsHigherPriorityTreeCell(int otherCellX, int otherCellZ, int cellX, int cellZ)
        {
            return otherCellX < cellX || (otherCellX == cellX && otherCellZ < cellZ);
        }

        private static bool HasNearbyTreeXZ(in NativeList<TreeInstance> trees, int worldX, int worldZ, int spacingRadius)
        {
            for (int i = 0; i < trees.Length; i++)
            {
                TreeInstance t = trees[i];
                int dx = t.worldX - worldX;
                int dz = t.worldZ - worldZ;
                int requiredSpacing = math.max(spacingRadius, t.spacingRadius);
                int minDistSq = requiredSpacing * requiredSpacing;
                if (dx * dx + dz * dz <= minDistSq)
                    return true;
            }

            return false;
        }

        private TerrainColumnContext GetColumnContextInternal(int worldX, int worldZ)
        {
            if (TryGetColumnContextFromCache(worldX, worldZ, out TerrainColumnContext cachedColumnContext))
                return cachedColumnContext;

            if (TerrainColumnSampler.TryCreateFromHeightCache(
                worldX,
                worldZ,
                coord.x,
                coord.y,
                SizeX,
                SizeZ,
                border,
                heightCache,
                CliffTreshold,
                baseHeight,
                seaLevel,
                biomeNoiseSettings,
                out TerrainColumnContext columnContext))
            {
                return columnContext;
            }

            return TerrainColumnSampler.SampleFromNoise(
                worldX,
                worldZ,
                noiseLayers,
                warpLayers,
                baseHeight,
                offsetX,
                offsetZ,
                SizeY,
                CliffTreshold,
                seaLevel,
                biomeNoiseSettings);
        }

        private bool TryGetColumnContextFromCache(int worldX, int worldZ, out TerrainColumnContext columnContext)
        {
            if (!columnContextCache.IsCreated || columnContextCacheStride <= 0 || columnContextCache.Length == 0)
            {
                columnContext = default;
                return false;
            }

            int cacheDepth = columnContextCache.Length / columnContextCacheStride;
            if (cacheDepth <= 0 || columnContextCache.Length % columnContextCacheStride != 0)
            {
                columnContext = default;
                return false;
            }

            int realLx = worldX - coord.x * SizeX;
            int realLz = worldZ - coord.y * SizeZ;
            int cacheX = realLx + border;
            int cacheZ = realLz + border;

            if (cacheX <= 0 || cacheX >= columnContextCacheStride - 1 || cacheZ <= 0 || cacheZ >= cacheDepth - 1)
            {
                columnContext = default;
                return false;
            }

            int index = cacheX + cacheZ * columnContextCacheStride;
            if (index < 0 || index >= columnContextCache.Length)
            {
                columnContext = default;
                return false;
            }

            columnContext = columnContextCache[index];
            return true;
        }

        private void GenerateWormCaves(
            NativeArray<byte> blockTypes,
            NativeArray<bool> solids,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int heightStride)
        {
            if (!caveSettings.enabled)
                return;

            float spawnChance = math.saturate(caveSettings.spawnChance);
            if (spawnChance <= 0f)
                return;

            int minY = math.clamp(math.min(caveSettings.minY, caveSettings.maxY), 3, SizeY - 2);
            int maxY = math.clamp(math.max(caveSettings.minY, caveSettings.maxY), 3, SizeY - 2);
            if (maxY < minY)
                return;

            int minWorms = math.max(1, math.min(caveSettings.minWormsPerChunk, caveSettings.maxWormsPerChunk));
            int maxWorms = math.max(minWorms, math.max(caveSettings.minWormsPerChunk, caveSettings.maxWormsPerChunk));
            int minLength = math.max(1, math.min(caveSettings.minLength, caveSettings.maxLength));
            int maxLength = math.max(minLength, math.max(caveSettings.minLength, caveSettings.maxLength));
            int sourceChunkRadius = math.max(0, caveSettings.sourceChunkRadius);

            float stepSize = math.max(0.1f, caveSettings.stepSize);
            float minRadius = math.max(0.5f, math.min(caveSettings.minRadius, caveSettings.maxRadius));
            float maxRadius = math.max(minRadius, math.max(caveSettings.minRadius, caveSettings.maxRadius));
            float radiusJitter = math.max(0f, caveSettings.radiusJitter);
            float initialVerticalRange = math.max(0f, caveSettings.initialVerticalRange);
            float verticalClamp = math.max(0.01f, caveSettings.verticalClamp);
            float turnRate = math.max(0f, caveSettings.turnRate);
            float verticalTurnRate = math.max(0f, caveSettings.verticalTurnRate);
            float forkChance = math.saturate(caveSettings.forkChance);
            int maxForks = math.max(0, caveSettings.maxForksPerWorm);
            int minSurfaceDepth = math.max(0, caveSettings.minSurfaceDepth);
            float surfaceEntranceChance = math.saturate(caveSettings.surfaceEntranceChance);
            float surfaceEntranceUpwardBias = math.saturate(caveSettings.surfaceEntranceUpwardBias);

            int chunkMinX = coord.x * SizeX;
            int chunkMinZ = coord.y * SizeZ;
            int activeBorder = math.max(0, math.min(detailBorder, border));
            int boundsMinX = chunkMinX - activeBorder;
            int boundsMaxX = chunkMinX + SizeX + activeBorder - 1;
            int boundsMinZ = chunkMinZ - activeBorder;
            int boundsMaxZ = chunkMinZ + SizeZ + activeBorder - 1;
            int boundsMinY = 3;
            int boundsMaxY = SizeY - 1;

            // Keep worm influence bounded by sourceChunkRadius to avoid border mismatches
            // (missing faces that only fix after neighbor rebuild).
            float chunkSpan = math.max(SizeX, SizeZ);
            float reachWorldUnits = sourceChunkRadius * chunkSpan - (maxRadius + 0.01f);
            if (reachWorldUnits <= 0f)
                reachWorldUnits = 1f;
            int maxReachSteps = math.max(1, (int)math.floor(reachWorldUnits / stepSize));

            int caveSeed = oreSeed ^ caveSettings.seedOffset ^ 0x6a3d39e9;

            for (int sourceChunkZ = coord.y - sourceChunkRadius; sourceChunkZ <= coord.y + sourceChunkRadius; sourceChunkZ++)
            {
                int sourceChunkMinZ = sourceChunkZ * SizeZ;
                for (int sourceChunkX = coord.x - sourceChunkRadius; sourceChunkX <= coord.x + sourceChunkRadius; sourceChunkX++)
                {
                    int sourceChunkMinX = sourceChunkX * SizeX;
                    uint sourceState = SeedState(sourceChunkX, sourceChunkZ, caveSeed, 7283, 0);
                    if (NextFloat01(ref sourceState) > spawnChance)
                        continue;

                    int wormCount = NextInt(ref sourceState, minWorms, maxWorms);
                    for (int wormIndex = 0; wormIndex < wormCount; wormIndex++)
                    {
                        uint wormState = SeedState(sourceChunkX, sourceChunkZ, caveSeed, 7283, wormIndex + 1);
                        float posX = sourceChunkMinX + NextFloat01(ref wormState) * (SizeX - 1) + 0.5f;
                        float posY = NextInt(ref wormState, minY, maxY) + 0.5f;
                        float posZ = sourceChunkMinZ + NextFloat01(ref wormState) * (SizeZ - 1) + 0.5f;

                        float horizontalAngle = NextFloat01(ref wormState) * math.PI * 2f;
                        float dirX = math.cos(horizontalAngle);
                        float dirZ = math.sin(horizontalAngle);
                        float dirY = NextSignedFloat(ref wormState) * initialVerticalRange;
                        bool canOpenSurface = NextFloat01(ref wormState) < surfaceEntranceChance;
                        if (canOpenSurface)
                            dirY = math.max(dirY, surfaceEntranceUpwardBias);
                        NormalizeDirection(ref dirX, ref dirY, ref dirZ);

                        int wormLength = math.min(NextInt(ref wormState, minLength, maxLength), maxReachSteps);
                        float baseRadius = math.lerp(minRadius, maxRadius, NextFloat01(ref wormState));
                        int wormMinSurfaceDepth = canOpenSurface ? 0 : minSurfaceDepth;

                        CarveWormPath(
                            posX,
                            posY,
                            posZ,
                            dirX,
                            dirY,
                            dirZ,
                            wormLength,
                            baseRadius,
                            minRadius,
                            maxRadius,
                            stepSize,
                            radiusJitter,
                            turnRate,
                            verticalTurnRate,
                            verticalClamp,
                            forkChance,
                            maxForks,
                            wormMinSurfaceDepth,
                            chunkMinX,
                            chunkMinZ,
                            voxelSizeX,
                            voxelSizeZ,
                            voxelPlaneSize,
                            heightStride,
                            boundsMinX,
                            boundsMaxX,
                            boundsMinY,
                            boundsMaxY,
                            boundsMinZ,
                            boundsMaxZ,
                            blockTypes,
                            solids,
                            ref wormState
                        );
                    }
                }
            }
        }

        private void CarveWormPath(
            float startX,
            float startY,
            float startZ,
            float dirX,
            float dirY,
            float dirZ,
            int length,
            float baseRadius,
            float minRadius,
            float maxRadius,
            float stepSize,
            float radiusJitter,
            float turnRate,
            float verticalTurnRate,
            float verticalClamp,
            float forkChance,
            int maxForks,
            int minSurfaceDepth,
            int chunkMinX,
            int chunkMinZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int heightStride,
            int boundsMinX,
            int boundsMaxX,
            int boundsMinY,
            int boundsMaxY,
            int boundsMinZ,
            int boundsMaxZ,
            NativeArray<byte> blockTypes,
            NativeArray<bool> solids,
            ref uint state)
        {
            float posX = startX;
            float posY = startY;
            float posZ = startZ;
            int forksLeft = maxForks;
            int outsideSteps = 0;
            float outsideBreakMargin = math.max(maxRadius, stepSize * 6f) + 1f;

            for (int step = 0; step < length; step++)
            {
                float radius = math.clamp(baseRadius + NextSignedFloat(ref state) * radiusJitter, minRadius, maxRadius);
                bool intersectsChunk = SphereIntersectsBounds(
                    posX, posY, posZ, radius,
                    boundsMinX, boundsMaxX,
                    boundsMinY, boundsMaxY,
                    boundsMinZ, boundsMaxZ
                );

                if (intersectsChunk)
                {
                    outsideSteps = 0;
                    CarveWormSphere(
                        posX,
                        posY,
                        posZ,
                        radius,
                        minSurfaceDepth,
                        chunkMinX,
                        chunkMinZ,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize,
                        heightStride,
                        boundsMinX,
                        boundsMaxX,
                        boundsMinY,
                        boundsMaxY,
                        boundsMinZ,
                        boundsMaxZ,
                        blockTypes,
                        solids
                    );
                }
                else
                {
                    outsideSteps++;
                    if (outsideSteps > 18)
                    {
                        bool farLeft = posX < boundsMinX - outsideBreakMargin && dirX <= 0f;
                        bool farRight = posX > boundsMaxX + outsideBreakMargin && dirX >= 0f;
                        bool farBelow = posY < boundsMinY - outsideBreakMargin && dirY <= 0f;
                        bool farAbove = posY > boundsMaxY + outsideBreakMargin && dirY >= 0f;
                        bool farBack = posZ < boundsMinZ - outsideBreakMargin && dirZ <= 0f;
                        bool farFront = posZ > boundsMaxZ + outsideBreakMargin && dirZ >= 0f;
                        if (farLeft || farRight || farBelow || farAbove || farBack || farFront)
                            break;
                    }
                }

                float move = stepSize * (0.8f + NextFloat01(ref state) * 0.4f);
                posX += dirX * move;
                posY += dirY * move;
                posZ += dirZ * move;

                dirX += NextSignedFloat(ref state) * turnRate;
                dirY += NextSignedFloat(ref state) * verticalTurnRate;
                dirZ += NextSignedFloat(ref state) * turnRate;
                dirY = math.clamp(dirY, -verticalClamp, verticalClamp);
                NormalizeDirection(ref dirX, ref dirY, ref dirZ);

                if (forksLeft <= 0 || forkChance <= 0f || NextFloat01(ref state) >= forkChance)
                    continue;

                forksLeft--;
                int forkLength = math.max(8, (int)(length * (0.35f + NextFloat01(ref state) * 0.40f)));
                float forkDirX = dirX + NextSignedFloat(ref state) * (turnRate * 2.2f + 0.05f);
                float forkDirY = math.clamp(
                    dirY + NextSignedFloat(ref state) * (verticalTurnRate * 1.9f + 0.03f),
                    -verticalClamp,
                    verticalClamp
                );
                float forkDirZ = dirZ + NextSignedFloat(ref state) * (turnRate * 2.2f + 0.05f);
                NormalizeDirection(ref forkDirX, ref forkDirY, ref forkDirZ);

                float forkRadius = math.clamp(radius * (0.75f + NextFloat01(ref state) * 0.45f), minRadius * 0.8f, maxRadius);
                CarveBranchPath(
                    posX,
                    posY,
                    posZ,
                    forkDirX,
                    forkDirY,
                    forkDirZ,
                    forkLength,
                    forkRadius,
                    minRadius,
                    maxRadius,
                    stepSize,
                    radiusJitter,
                    turnRate * 1.15f,
                    verticalTurnRate * 1.15f,
                    verticalClamp,
                    minSurfaceDepth,
                    chunkMinX,
                    chunkMinZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    heightStride,
                    boundsMinX,
                    boundsMaxX,
                    boundsMinY,
                    boundsMaxY,
                    boundsMinZ,
                    boundsMaxZ,
                    blockTypes,
                    solids,
                    ref state
                );
            }
        }

        private void CarveBranchPath(
            float startX,
            float startY,
            float startZ,
            float dirX,
            float dirY,
            float dirZ,
            int length,
            float baseRadius,
            float minRadius,
            float maxRadius,
            float stepSize,
            float radiusJitter,
            float turnRate,
            float verticalTurnRate,
            float verticalClamp,
            int minSurfaceDepth,
            int chunkMinX,
            int chunkMinZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int heightStride,
            int boundsMinX,
            int boundsMaxX,
            int boundsMinY,
            int boundsMaxY,
            int boundsMinZ,
            int boundsMaxZ,
            NativeArray<byte> blockTypes,
            NativeArray<bool> solids,
            ref uint state)
        {
            float posX = startX;
            float posY = startY;
            float posZ = startZ;

            for (int step = 0; step < length; step++)
            {
                float radius = math.clamp(baseRadius + NextSignedFloat(ref state) * radiusJitter, minRadius * 0.75f, maxRadius);
                if (SphereIntersectsBounds(
                    posX, posY, posZ, radius,
                    boundsMinX, boundsMaxX,
                    boundsMinY, boundsMaxY,
                    boundsMinZ, boundsMaxZ))
                {
                    CarveWormSphere(
                        posX,
                        posY,
                        posZ,
                        radius,
                        minSurfaceDepth,
                        chunkMinX,
                        chunkMinZ,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize,
                        heightStride,
                        boundsMinX,
                        boundsMaxX,
                        boundsMinY,
                        boundsMaxY,
                        boundsMinZ,
                        boundsMaxZ,
                        blockTypes,
                        solids
                    );
                }

                float move = stepSize * (0.75f + NextFloat01(ref state) * 0.45f);
                posX += dirX * move;
                posY += dirY * move;
                posZ += dirZ * move;

                dirX += NextSignedFloat(ref state) * turnRate;
                dirY += NextSignedFloat(ref state) * verticalTurnRate;
                dirZ += NextSignedFloat(ref state) * turnRate;
                dirY = math.clamp(dirY, -verticalClamp, verticalClamp);
                NormalizeDirection(ref dirX, ref dirY, ref dirZ);
            }
        }

        private void CarveWormSphere(
            float centerWorldX,
            float centerWorldY,
            float centerWorldZ,
            float radius,
            int minSurfaceDepth,
            int chunkMinX,
            int chunkMinZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int heightStride,
            int boundsMinX,
            int boundsMaxX,
            int boundsMinY,
            int boundsMaxY,
            int boundsMinZ,
            int boundsMaxZ,
            NativeArray<byte> blockTypes,
            NativeArray<bool> solids)
        {
            if (radius <= 0f)
                return;

            if (!SphereIntersectsBounds(
                centerWorldX, centerWorldY, centerWorldZ, radius,
                boundsMinX, boundsMaxX,
                boundsMinY, boundsMaxY,
                boundsMinZ, boundsMaxZ))
                return;

            int worldCenterX = (int)math.floor(centerWorldX);
            int worldCenterY = (int)math.floor(centerWorldY);
            int worldCenterZ = (int)math.floor(centerWorldZ);
            int maxOffset = math.max(1, (int)math.ceil(radius));
            float radiusSq = radius * radius;
            int activeMinLocalX = math.max(0, border - detailBorder);
            int activeMaxLocalX = math.min(voxelSizeX - 1, border + SizeX + detailBorder - 1);
            int activeMinLocalZ = math.max(0, border - detailBorder);
            int activeMaxLocalZ = math.min(voxelSizeZ - 1, border + SizeZ + detailBorder - 1);
            int minLocalX = math.max(activeMinLocalX, worldCenterX - maxOffset - chunkMinX + border);
            int maxLocalX = math.min(activeMaxLocalX, worldCenterX + maxOffset - chunkMinX + border);
            int minLocalZ = math.max(activeMinLocalZ, worldCenterZ - maxOffset - chunkMinZ + border);
            int maxLocalZ = math.min(activeMaxLocalZ, worldCenterZ + maxOffset - chunkMinZ + border);
            int minWorldY = math.max(3, worldCenterY - maxOffset);
            int maxWorldY = math.min(SizeY - 1, worldCenterY + maxOffset);

            if (minLocalX > maxLocalX || minLocalZ > maxLocalZ || minWorldY > maxWorldY)
                return;

            for (int localZ = minLocalZ; localZ <= maxLocalZ; localZ++)
            {
                int worldZ = localZ + chunkMinZ - border;
                int oz = worldZ - worldCenterZ;
                float dzSq = oz * oz;
                for (int localX = minLocalX; localX <= maxLocalX; localX++)
                {
                    int worldX = localX + chunkMinX - border;
                    int ox = worldX - worldCenterX;
                    float dxSq = ox * ox;
                    float radialSq = dxSq + dzSq;
                    if (radialSq > radiusSq)
                        continue;

                    int columnSurfaceY = heightCache[localX + localZ * heightStride];
                    int maxCarveY = columnSurfaceY - minSurfaceDepth;
                    int carveYMin = minWorldY;
                    int carveYMax = math.min(maxWorldY, maxCarveY);
                    if (carveYMin > carveYMax)
                        continue;

                    int maxDy = (int)math.floor(math.sqrt(radiusSq - radialSq));
                    carveYMin = math.max(carveYMin, worldCenterY - maxDy);
                    carveYMax = math.min(carveYMax, worldCenterY + maxDy);
                    if (carveYMin > carveYMax)
                        continue;

                    for (int worldY = carveYMin; worldY <= carveYMax; worldY++)
                    {
                        int idx = localX + worldY * voxelSizeX + localZ * voxelPlaneSize;
                        BlockType existing = (BlockType)blockTypes[idx];
                        if (existing == BlockType.Air || existing == BlockType.Water || existing == BlockType.Bedrock)
                            continue;

                        blockTypes[idx] = (byte)BlockType.Air;
                        solids[idx] = false;
                    }
                }
            }
        }

        private static bool SphereIntersectsBounds(
            float centerX,
            float centerY,
            float centerZ,
            float radius,
            int minX,
            int maxX,
            int minY,
            int maxY,
            int minZ,
            int maxZ)
        {
            return centerX + radius >= minX &&
                   centerX - radius <= maxX &&
                   centerY + radius >= minY &&
                   centerY - radius <= maxY &&
                   centerZ + radius >= minZ &&
                   centerZ - radius <= maxZ;
        }

        private void GenerateSpaghettiCaves(
            NativeArray<byte> blockTypes,
            NativeArray<bool> solids,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int heightStride)
        {
            SpaghettiCaveSettings settings = spaghettiCaveSettings;
            if (!settings.enabled)
                return;

            int minY = math.clamp(math.min(settings.minY, settings.maxY), 3, SizeY - 2);
            int maxY = math.clamp(math.max(settings.minY, settings.maxY), 3, SizeY - 2);
            if (maxY < minY)
                return;

            int minSurfaceDepth = math.max(0, settings.minSurfaceDepth);
            int entranceSurfaceDepth = math.max(0, math.min(settings.entranceSurfaceDepth, minSurfaceDepth));
            float densityBias = settings.densityBias;
            int worldSeed = oreSeed ^ settings.seedOffset ^ 0x4b1d2e37;
            var caveNoiseSampler = SpaghettiCaveNoiseUtility.Create(worldSeed);

            int chunkMinX = coord.x * SizeX;
            int chunkMinZ = coord.y * SizeZ;
            int activeMinLocalX = math.max(0, border - detailBorder);
            int activeMaxLocalX = math.min(voxelSizeX - 1, border + SizeX + detailBorder - 1);
            int activeMinLocalZ = math.max(0, border - detailBorder);
            int activeMaxLocalZ = math.min(voxelSizeZ - 1, border + SizeZ + detailBorder - 1);
            int globalEntranceMaxY = minY - 1;
            for (int localZ = activeMinLocalZ; localZ <= activeMaxLocalZ; localZ++)
            {
                for (int localX = activeMinLocalX; localX <= activeMaxLocalX; localX++)
                {
                    int columnSurfaceY = heightCache[localX + localZ * heightStride];
                    int entranceMaxY = math.min(maxY, columnSurfaceY - entranceSurfaceDepth);
                    globalEntranceMaxY = math.max(globalEntranceMaxY, entranceMaxY);
                }
            }

            if (globalEntranceMaxY < minY)
                return;

            int sampleMaxY = math.min(maxY, globalEntranceMaxY);
            int gridCountX = GetSpaghettiGridPointCount(activeMinLocalX, activeMaxLocalX, SpaghettiHorizontalCellSize);
            int gridCountY = GetSpaghettiGridPointCount(minY, sampleMaxY, SpaghettiVerticalCellSize);
            int gridCountZ = GetSpaghettiGridPointCount(activeMinLocalZ, activeMaxLocalZ, SpaghettiHorizontalCellSize);
            NativeArray<float2> densityGrid = new NativeArray<float2>(gridCountX * gridCountY * gridCountZ, Allocator.Temp);

            try
            {
                for (int gridZ = 0; gridZ < gridCountZ; gridZ++)
                {
                    int localZ = GetSpaghettiGridCoordinate(activeMinLocalZ, activeMaxLocalZ, SpaghettiHorizontalCellSize, gridZ);
                    float sampleZ = localZ + chunkMinZ - border + 0.5f;

                    for (int gridX = 0; gridX < gridCountX; gridX++)
                    {
                        int localX = GetSpaghettiGridCoordinate(activeMinLocalX, activeMaxLocalX, SpaghettiHorizontalCellSize, gridX);
                        float sampleX = localX + chunkMinX - border + 0.5f;

                        for (int gridY = 0; gridY < gridCountY; gridY++)
                        {
                            int voxelY = GetSpaghettiGridCoordinate(minY, sampleMaxY, SpaghettiVerticalCellSize, gridY);
                            float sampleY = voxelY + 0.5f;
                            densityGrid[GetSpaghettiGridIndex(gridX, gridY, gridZ, gridCountX, gridCountY)] =
                                SpaghettiCaveNoiseUtility.SampleDensityPair(in caveNoiseSampler, sampleX, sampleY, sampleZ, densityBias);
                        }
                    }
                }

                for (int cellZ = 0; cellZ < gridCountZ - 1; cellZ++)
                {
                    int localZ0 = GetSpaghettiGridCoordinate(activeMinLocalZ, activeMaxLocalZ, SpaghettiHorizontalCellSize, cellZ);
                    int localZ1 = GetSpaghettiGridCoordinate(activeMinLocalZ, activeMaxLocalZ, SpaghettiHorizontalCellSize, cellZ + 1);
                    int zSpan = math.max(1, localZ1 - localZ0);
                    int localZMax = cellZ == gridCountZ - 2 ? localZ1 : localZ1 - 1;

                    for (int cellX = 0; cellX < gridCountX - 1; cellX++)
                    {
                        int localX0 = GetSpaghettiGridCoordinate(activeMinLocalX, activeMaxLocalX, SpaghettiHorizontalCellSize, cellX);
                        int localX1 = GetSpaghettiGridCoordinate(activeMinLocalX, activeMaxLocalX, SpaghettiHorizontalCellSize, cellX + 1);
                        int xSpan = math.max(1, localX1 - localX0);
                        int localXMax = cellX == gridCountX - 2 ? localX1 : localX1 - 1;

                        for (int cellY = 0; cellY < gridCountY - 1; cellY++)
                        {
                            int voxelY0 = GetSpaghettiGridCoordinate(minY, sampleMaxY, SpaghettiVerticalCellSize, cellY);
                            int voxelY1 = GetSpaghettiGridCoordinate(minY, sampleMaxY, SpaghettiVerticalCellSize, cellY + 1);
                            int ySpan = math.max(1, voxelY1 - voxelY0);
                            int voxelYMax = cellY == gridCountY - 2 ? voxelY1 : voxelY1 - 1;

                            float2 d000 = densityGrid[GetSpaghettiGridIndex(cellX, cellY, cellZ, gridCountX, gridCountY)];
                            float2 d100 = densityGrid[GetSpaghettiGridIndex(cellX + 1, cellY, cellZ, gridCountX, gridCountY)];
                            float2 d010 = densityGrid[GetSpaghettiGridIndex(cellX, cellY + 1, cellZ, gridCountX, gridCountY)];
                            float2 d110 = densityGrid[GetSpaghettiGridIndex(cellX + 1, cellY + 1, cellZ, gridCountX, gridCountY)];
                            float2 d001 = densityGrid[GetSpaghettiGridIndex(cellX, cellY, cellZ + 1, gridCountX, gridCountY)];
                            float2 d101 = densityGrid[GetSpaghettiGridIndex(cellX + 1, cellY, cellZ + 1, gridCountX, gridCountY)];
                            float2 d011 = densityGrid[GetSpaghettiGridIndex(cellX, cellY + 1, cellZ + 1, gridCountX, gridCountY)];
                            float2 d111 = densityGrid[GetSpaghettiGridIndex(cellX + 1, cellY + 1, cellZ + 1, gridCountX, gridCountY)];
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
                                        BlockType existing = (BlockType)blockTypes[voxelIndex];
                                        if (existing == BlockType.Air || existing == BlockType.Water || existing == BlockType.Bedrock)
                                            continue;

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

                                        blockTypes[voxelIndex] = (byte)BlockType.Air;
                                        solids[voxelIndex] = false;
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

            CarveSpaghettiChunkBorderColumnsExact(
                blockTypes,
                solids,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                heightStride,
                chunkMinX,
                chunkMinZ,
                minY,
                maxY,
                minSurfaceDepth,
                entranceSurfaceDepth,
                densityBias,
                in caveNoiseSampler);
        }

        private void CarveSpaghettiChunkBorderColumnsExact(
            NativeArray<byte> blockTypes,
            NativeArray<bool> solids,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int heightStride,
            int chunkMinX,
            int chunkMinZ,
            int minY,
            int maxY,
            int minSurfaceDepth,
            int entranceSurfaceDepth,
            float densityBias,
            in SpaghettiCaveNoiseUtility.SpaghettiCaveNoiseSampler caveNoiseSampler)
        {
            // Only the chunk edge columns and the immediate padding columns can hide
            // faces when neighboring chunks disagree, so we re-sample just that seam.
            int westPaddingX = border - 1;
            int westInnerX = border;
            int eastInnerX = border + SizeX - 1;
            int eastPaddingX = border + SizeX;
            int northPaddingZ = border - 1;
            int northInnerZ = border;
            int southInnerZ = border + SizeZ - 1;
            int southPaddingZ = border + SizeZ;

            for (int localZ = 0; localZ < voxelSizeZ; localZ++)
            {
                CarveSpaghettiColumnExact(localZ, westPaddingX, true, blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride, chunkMinX, chunkMinZ, minY, maxY, minSurfaceDepth, entranceSurfaceDepth, densityBias, in caveNoiseSampler);
                CarveSpaghettiColumnExact(localZ, westInnerX, true, blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride, chunkMinX, chunkMinZ, minY, maxY, minSurfaceDepth, entranceSurfaceDepth, densityBias, in caveNoiseSampler);
                CarveSpaghettiColumnExact(localZ, eastInnerX, true, blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride, chunkMinX, chunkMinZ, minY, maxY, minSurfaceDepth, entranceSurfaceDepth, densityBias, in caveNoiseSampler);
                CarveSpaghettiColumnExact(localZ, eastPaddingX, true, blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride, chunkMinX, chunkMinZ, minY, maxY, minSurfaceDepth, entranceSurfaceDepth, densityBias, in caveNoiseSampler);
            }

            for (int localX = 0; localX < voxelSizeX; localX++)
            {
                CarveSpaghettiColumnExact(localX, northPaddingZ, false, blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride, chunkMinX, chunkMinZ, minY, maxY, minSurfaceDepth, entranceSurfaceDepth, densityBias, in caveNoiseSampler);
                CarveSpaghettiColumnExact(localX, northInnerZ, false, blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride, chunkMinX, chunkMinZ, minY, maxY, minSurfaceDepth, entranceSurfaceDepth, densityBias, in caveNoiseSampler);
                CarveSpaghettiColumnExact(localX, southInnerZ, false, blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride, chunkMinX, chunkMinZ, minY, maxY, minSurfaceDepth, entranceSurfaceDepth, densityBias, in caveNoiseSampler);
                CarveSpaghettiColumnExact(localX, southPaddingZ, false, blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride, chunkMinX, chunkMinZ, minY, maxY, minSurfaceDepth, entranceSurfaceDepth, densityBias, in caveNoiseSampler);
            }
        }

        private void CarveSpaghettiColumnExact(
            int primary,
            int secondary,
            bool primaryIsZ,
            NativeArray<byte> blockTypes,
            NativeArray<bool> solids,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int heightStride,
            int chunkMinX,
            int chunkMinZ,
            int minY,
            int maxY,
            int minSurfaceDepth,
            int entranceSurfaceDepth,
            float densityBias,
            in SpaghettiCaveNoiseUtility.SpaghettiCaveNoiseSampler caveNoiseSampler)
        {
            int localX = primaryIsZ ? secondary : primary;
            int localZ = primaryIsZ ? primary : secondary;
            if (localX < 0 || localX >= voxelSizeX || localZ < 0 || localZ >= voxelSizeZ)
                return;

            int columnSurfaceY = heightCache[localX + localZ * heightStride];
            int regularMaxY = math.min(maxY, columnSurfaceY - minSurfaceDepth);
            int entranceMaxY = math.min(maxY, columnSurfaceY - entranceSurfaceDepth);
            if (entranceMaxY < minY)
                return;

            float worldX = localX + chunkMinX - border + 0.5f;
            float worldZ = localZ + chunkMinZ - border + 0.5f;
            int voxelIndex = localX + minY * voxelSizeX + localZ * voxelPlaneSize;

            for (int voxelY = minY; voxelY <= entranceMaxY; voxelY++, voxelIndex += voxelSizeX)
            {
                BlockType existing = (BlockType)blockTypes[voxelIndex];
                if (existing == BlockType.Air || existing == BlockType.Water || existing == BlockType.Bedrock)
                    continue;

                float2 density = SpaghettiCaveNoiseUtility.SampleDensityPair(in caveNoiseSampler, worldX, voxelY + 0.5f, worldZ, densityBias);
                if (voxelY > regularMaxY && density.y >= 0f)
                    continue;
                if (density.x >= 0f)
                    continue;

                blockTypes[voxelIndex] = (byte)BlockType.Air;
                solids[voxelIndex] = false;
            }
        }

        private static int GetSpaghettiGridPointCount(int minInclusive, int maxInclusive, int step)
        {
            int safeStep = math.max(1, step);
            return math.max(2, ((maxInclusive - minInclusive) / safeStep) + 2);
        }

        private static int GetSpaghettiGridCoordinate(int minInclusive, int maxInclusive, int step, int index)
        {
            return math.min(maxInclusive, minInclusive + index * math.max(1, step));
        }

        private static int GetSpaghettiGridIndex(int x, int y, int z, int xCount, int yCount)
        {
            return x + y * xCount + z * xCount * yCount;
        }

        private static float GetMinCarveDensity(
            float2 d000,
            float2 d100,
            float2 d010,
            float2 d110,
            float2 d001,
            float2 d101,
            float2 d011,
            float2 d111)
        {
            float minA = math.min(math.min(d000.x, d100.x), math.min(d010.x, d110.x));
            float minB = math.min(math.min(d001.x, d101.x), math.min(d011.x, d111.x));
            return math.min(minA, minB);
        }

        private static float2 TrilinearInterpolate(
            float2 d000,
            float2 d100,
            float2 d010,
            float2 d110,
            float2 d001,
            float2 d101,
            float2 d011,
            float2 d111,
            float tx,
            float ty,
            float tz)
        {
            float2 x00 = math.lerp(d000, d100, tx);
            float2 x10 = math.lerp(d010, d110, tx);
            float2 x01 = math.lerp(d001, d101, tx);
            float2 x11 = math.lerp(d011, d111, tx);
            float2 y0 = math.lerp(x00, x10, ty);
            float2 y1 = math.lerp(x01, x11, ty);
            return math.lerp(y0, y1, tz);
        }

        private static float2 SampleSpaghettiDensityPair(float worldX, float worldY, float worldZ, int worldSeed, float densityBias)
        {
            float roughness = SampleSpaghettiRoughness(worldX, worldY, worldZ, worldSeed);
            float spaghetti2d = SampleSpaghetti2d(worldX, worldY, worldZ, worldSeed);
            float entrancesDensity = SampleSpaghettiEntrances(worldX, worldY, worldZ, worldSeed, roughness);
            float carveDensity = math.min(spaghetti2d + roughness, entrancesDensity);
            return new float2(carveDensity + densityBias, entrancesDensity + densityBias);
        }

        private static float SampleSpaghetti2d(float worldX, float worldY, float worldZ, int worldSeed)
        {
            float modulator = SampleSingleAmplitudeCaveNoise(worldX, worldY, worldZ, worldSeed, 0x4d0f12a1, -11, 2f, 1f);
            float thicknessMod = -0.95f - 0.35f * SampleSingleAmplitudeCaveNoise(worldX, worldY, worldZ, worldSeed, 0x63be5ab9, -11, 2f, 1f);
            float weirdScaled = SampleWeirdScaledSampler(worldX, worldY, worldZ, worldSeed, 0x7c159d51, modulator, false, -7);
            float elevationNoise = SampleSingleAmplitudeCaveNoise(worldX, worldY, worldZ, worldSeed, 0x1f3d8a77, -8, 1f, 0f);
            float elevationGradient = SampleYClampedGradient(worldY, -64f, 320f, 8f, -40f);
            float elevationBand = math.abs(elevationNoise * 8f + elevationGradient);

            float longitudinal = weirdScaled + 0.083f * thicknessMod;
            float elevationTerm = Cube(elevationBand + thicknessMod);
            return math.clamp(math.max(longitudinal, elevationTerm), -1f, 1f);
        }

        private static float SampleSpaghettiRoughness(float worldX, float worldY, float worldZ, int worldSeed)
        {
            float roughnessModulator = SampleSingleAmplitudeCaveNoise(worldX, worldY, worldZ, worldSeed, 0x2c8f3bd3, -8, 1f, 1f);
            float roughness = SampleSingleAmplitudeCaveNoise(worldX, worldY, worldZ, worldSeed, 0x54e21f79, -5, 1f, 1f);
            return (-0.05f - 0.05f * roughnessModulator) * (-0.4f + math.abs(roughness));
        }

        private static float SampleSpaghettiEntrances(float worldX, float worldY, float worldZ, int worldSeed, float roughness)
        {
            float entranceNoise = SampleOctavedCaveNoise(worldX, worldY, worldZ, worldSeed, 0x2714c2d1, -7, 0.75f, 0.5f, 3, 0.4f, 0.5f, 1f);
            float entranceHead = 0.37f + entranceNoise + SampleYClampedGradient(worldY, -10f, 30f, 0.3f, 0f);

            float rarityNoise = SampleSingleAmplitudeCaveNoise(worldX, worldY, worldZ, worldSeed, 0x1495f0e3, -11, 2f, 1f);
            float spaghettiA = SampleWeirdScaledSampler(worldX, worldY, worldZ, worldSeed, 0x6a31dcb7, rarityNoise, true, -7);
            float spaghettiB = SampleWeirdScaledSampler(worldX, worldY, worldZ, worldSeed, 0x0dc7612f, rarityNoise, true, -7);
            float thicknessMod = -0.0765f - 0.0115f * SampleSingleAmplitudeCaveNoise(worldX, worldY, worldZ, worldSeed, 0x45fa21cd, -8, 1f, 1f);
            float entranceBody = roughness + math.clamp(math.max(spaghettiA, spaghettiB) + thicknessMod, -1f, 1f);

            return math.min(entranceHead, entranceBody);
        }

        private static float SampleWeirdScaledSampler(
            float worldX,
            float worldY,
            float worldZ,
            int worldSeed,
            int noiseSalt,
            float inputValue,
            bool useType1Rarity,
            int firstOctave)
        {
            float rarity = useType1Rarity
                ? GetSpaghettiRarity3D(inputValue)
                : GetSpaghettiRarity2D(inputValue);

            float sample = SampleSingleAmplitudeCaveNoise(
                worldX / rarity,
                worldY / rarity,
                worldZ / rarity,
                worldSeed,
                noiseSalt,
                firstOctave,
                1f,
                1f);

            return rarity * math.abs(sample);
        }

        private static float GetSpaghettiRarity2D(float rarity)
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

        private static float GetSpaghettiRarity3D(float rarity)
        {
            if (rarity < -0.5f)
                return 0.75f;
            if (rarity < 0f)
                return 1f;
            if (rarity < 0.5f)
                return 1.5f;
            return 2f;
        }

        private static float SampleSingleAmplitudeCaveNoise(
            float worldX,
            float worldY,
            float worldZ,
            int worldSeed,
            int noiseSalt,
            int firstOctave,
            float xzScale,
            float yScale)
        {
            return SampleOctavedCaveNoise(worldX, worldY, worldZ, worldSeed, noiseSalt, firstOctave, xzScale, yScale, 1, 1f, 0f, 0f);
        }

        private static float SampleOctavedCaveNoise(
            float worldX,
            float worldY,
            float worldZ,
            int worldSeed,
            int noiseSalt,
            int firstOctave,
            float xzScale,
            float yScale,
            int amplitudeCount,
            float amplitude0,
            float amplitude1,
            float amplitude2)
        {
            float total = 0f;
            float weightSum = 0f;

            for (int octaveIndex = 0; octaveIndex < amplitudeCount; octaveIndex++)
            {
                float amplitude;
                switch (octaveIndex)
                {
                    case 0:
                        amplitude = amplitude0;
                        break;
                    case 1:
                        amplitude = amplitude1;
                        break;
                    case 2:
                        amplitude = amplitude2;
                        break;
                    default:
                        amplitude = 0f;
                        break;
                }

                if (math.abs(amplitude) <= 1e-5f)
                    continue;

                total += amplitude * SampleDoubleSimplexNoise(
                    worldX,
                    worldY,
                    worldZ,
                    worldSeed,
                    noiseSalt + octaveIndex * 977,
                    firstOctave + octaveIndex,
                    xzScale,
                    yScale);
                weightSum += math.abs(amplitude);
            }

            if (weightSum <= 1e-5f)
                return 0f;

            return math.clamp(total / weightSum, -1f, 1f);
        }

        private static float SampleDoubleSimplexNoise(
            float worldX,
            float worldY,
            float worldZ,
            int worldSeed,
            int noiseSalt,
            int octave,
            float xzScale,
            float yScale)
        {
            float frequency = math.exp2((float)octave);
            float3 samplePos = new float3(
                worldX * xzScale * frequency,
                worldY * yScale * frequency,
                worldZ * xzScale * frequency);

            uint state = Hash((uint)(worldSeed ^ noiseSalt));
            float3 primaryOffset = NextNoiseOffset(ref state);
            float3 secondaryOffset = NextNoiseOffset(ref state);

            float primary = noise.snoise(samplePos + primaryOffset);
            float secondary = noise.snoise(samplePos * DoubleNoiseWarp + secondaryOffset);
            return math.clamp((primary + secondary) * 0.5f, -1f, 1f);
        }

        private static float3 NextNoiseOffset(ref uint state)
        {
            return new float3(
                NextSignedFloat(ref state),
                NextSignedFloat(ref state),
                NextSignedFloat(ref state)) * NoiseOffsetMagnitude;
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

        private void GenerateOreVeins(
            NativeArray<byte> blockTypes,
            NativeArray<bool> solids,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int heightStride)
        {
            if (oreSettings.Length == 0)
                return;

            int chunkMinX = coord.x * SizeX;
            int chunkMinZ = coord.y * SizeZ;

            for (int ruleIndex = 0; ruleIndex < oreSettings.Length; ruleIndex++)
            {
                OreSpawnSettings rule = oreSettings[ruleIndex];
                if (!rule.enabled || rule.veinsPerChunk <= 0)
                    continue;

                int oreBlockIndex = (int)rule.blockType;
                if (oreBlockIndex < 0 || oreBlockIndex >= blockMappings.Length)
                    continue;
                bool oreIsSolid = blockMappings[oreBlockIndex].isSolid;

                int minY = math.clamp(math.min(rule.minY, rule.maxY), 3, SizeY - 1);
                int maxY = math.clamp(math.max(rule.minY, rule.maxY), 3, SizeY - 1);
                if (maxY < minY)
                    continue;

                int minVeinSize = math.max(1, math.min(rule.minVeinSize, rule.maxVeinSize));
                int maxVeinSize = math.max(minVeinSize, math.max(rule.minVeinSize, rule.maxVeinSize));
                int minSurfaceDepth = math.max(0, rule.minSurfaceDepth);
                int veinsPerChunk = math.max(0, rule.veinsPerChunk);

                for (int sourceChunkZ = coord.y - 1; sourceChunkZ <= coord.y + 1; sourceChunkZ++)
                {
                    int sourceChunkMinZ = sourceChunkZ * SizeZ;
                    for (int sourceChunkX = coord.x - 1; sourceChunkX <= coord.x + 1; sourceChunkX++)
                    {
                        int sourceChunkMinX = sourceChunkX * SizeX;
                        for (int vein = 0; vein < veinsPerChunk; vein++)
                        {
                            uint state = SeedState(sourceChunkX, sourceChunkZ, oreSeed, ruleIndex, vein);

                            float px = sourceChunkMinX + NextInt(ref state, 0, SizeX - 1) + 0.5f;
                            float py = NextInt(ref state, minY, maxY) + 0.5f;
                            float pz = sourceChunkMinZ + NextInt(ref state, 0, SizeZ - 1) + 0.5f;

                            int steps = NextInt(ref state, minVeinSize, maxVeinSize);
                            float dirX = NextSignedFloat(ref state);
                            float dirY = NextSignedFloat(ref state) * 0.55f;
                            float dirZ = NextSignedFloat(ref state);
                            NormalizeDirection(ref dirX, ref dirY, ref dirZ);

                            for (int step = 0; step < steps; step++)
                            {
                                int radius = 1;
                                if ((step & 1) == 0 && NextFloat01(ref state) < 0.28f)
                                    radius = 2;

                                PlaceOreBlob(
                                    rule,
                                    minY,
                                    maxY,
                                    minSurfaceDepth,
                                    px,
                                    py,
                                    pz,
                                    radius,
                                    chunkMinX,
                                    chunkMinZ,
                                    voxelSizeX,
                                    voxelSizeZ,
                                    voxelPlaneSize,
                                    heightStride,
                                    oreIsSolid,
                                    blockTypes,
                                    solids,
                                    ref state
                                );

                                px += dirX * (0.65f + NextFloat01(ref state) * 0.65f);
                                py += dirY * (0.40f + NextFloat01(ref state) * 0.45f);
                                pz += dirZ * (0.65f + NextFloat01(ref state) * 0.65f);

                                dirX = math.lerp(dirX, NextSignedFloat(ref state), 0.34f);
                                dirY = math.lerp(dirY, NextSignedFloat(ref state) * 0.55f, 0.34f);
                                dirZ = math.lerp(dirZ, NextSignedFloat(ref state), 0.34f);
                                NormalizeDirection(ref dirX, ref dirY, ref dirZ);
                            }
                        }
                    }
                }
            }
        }

        private void PlaceOreBlob(
            OreSpawnSettings rule,
            int minY,
            int maxY,
            int minSurfaceDepth,
            float centerWorldX,
            float centerWorldY,
            float centerWorldZ,
            int radius,
            int chunkMinX,
            int chunkMinZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int heightStride,
            bool oreIsSolid,
            NativeArray<byte> blockTypes,
            NativeArray<bool> solids,
            ref uint state)
        {
            int worldCenterX = (int)math.floor(centerWorldX);
            int worldCenterY = (int)math.floor(centerWorldY);
            int worldCenterZ = (int)math.floor(centerWorldZ);
            int radiusSq = radius * radius;
            int activeMinLocalX = math.max(0, border - detailBorder);
            int activeMaxLocalX = math.min(voxelSizeX - 1, border + SizeX + detailBorder - 1);
            int activeMinLocalZ = math.max(0, border - detailBorder);
            int activeMaxLocalZ = math.min(voxelSizeZ - 1, border + SizeZ + detailBorder - 1);

            for (int oz = -radius; oz <= radius; oz++)
            {
                int worldZ = worldCenterZ + oz;
                int localZ = worldZ - chunkMinZ + border;
                if (localZ < activeMinLocalZ || localZ > activeMaxLocalZ)
                    continue;

                for (int ox = -radius; ox <= radius; ox++)
                {
                    int worldX = worldCenterX + ox;
                    int localX = worldX - chunkMinX + border;
                    if (localX < activeMinLocalX || localX > activeMaxLocalX)
                        continue;

                    int columnSurfaceY = heightCache[localX + localZ * heightStride];
                    int maxAllowedY = columnSurfaceY - minSurfaceDepth;

                    for (int oy = -radius; oy <= radius; oy++)
                    {
                        int worldY = worldCenterY + oy;
                        if (worldY < minY || worldY > maxY || worldY < 3 || worldY >= SizeY)
                            continue;
                        if (worldY > maxAllowedY)
                            continue;

                        int distSq = ox * ox + oy * oy + oz * oz;
                        if (distSq > radiusSq)
                            continue;

                        // Deixa os blobs menos esfericos e reduz custo de overdraw de minerio.
                        if (radius > 1 && NextFloat01(ref state) < (distSq / (float)(radiusSq + 1)))
                            continue;

                        int idx = localX + worldY * voxelSizeX + localZ * voxelPlaneSize;
                        BlockType existing = (BlockType)blockTypes[idx];
                        if (!CanReplaceForRule(existing, rule))
                            continue;

                        blockTypes[idx] = (byte)rule.blockType;
                        solids[idx] = oreIsSolid;
                    }
                }
            }
        }

        private static bool CanReplaceForRule(BlockType existing, OreSpawnSettings rule)
        {
            return (existing == BlockType.Stone && rule.replaceStone) ||
                   (existing == BlockType.Deepslate && rule.replaceDeepslate);
        }

        private static void NormalizeDirection(ref float x, ref float y, ref float z)
        {
            float lenSq = x * x + y * y + z * z;
            if (lenSq <= 1e-5f)
            {
                x = 1f;
                y = 0f;
                z = 0f;
                return;
            }

            float invLen = math.rsqrt(lenSq);
            x *= invLen;
            y *= invLen;
            z *= invLen;
        }

        private static uint SeedState(int chunkX, int chunkZ, int seed, int ruleIndex, int veinIndex)
        {
            uint h = 2166136261u;
            h = (h ^ (uint)chunkX) * 16777619u;
            h = (h ^ (uint)chunkZ) * 16777619u;
            h = (h ^ (uint)seed) * 16777619u;
            h = (h ^ (uint)ruleIndex) * 16777619u;
            h = (h ^ (uint)veinIndex) * 16777619u;
            return Hash(h);
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

        private static int NextInt(ref uint state, int minInclusive, int maxInclusive)
        {
            if (maxInclusive <= minInclusive)
                return minInclusive;

            state = Hash(state + 0x68bc21ebu);
            uint range = (uint)(maxInclusive - minInclusive + 1);
            return minInclusive + (int)(state % range);
        }


        private void ApplyBlockEditsToVoxels(NativeArray<byte> blockTypes, NativeArray<bool> solids, int voxelSizeX, int voxelSizeZ)
        {
            if (blockEdits.Length == 0) return;

            int voxelPlaneSize = voxelSizeX * SizeY;
            int baseWorldX = coord.x * SizeX;
            int baseWorldZ = coord.y * SizeZ;

            for (int i = 0; i < blockEdits.Length; i++)
            {
                var e = blockEdits[i];
                int localX = e.x - baseWorldX;
                int localZ = e.z - baseWorldZ;
                int y = e.y;

                int internalX = localX + border;
                int internalZ = localZ + border;

                if (internalX >= 0 && internalX < voxelSizeX && y >= 0 && y < SizeY && internalZ >= 0 && internalZ < voxelSizeZ)
                {
                    int idx = internalX + y * voxelSizeX + internalZ * voxelPlaneSize;
                    BlockType bt = (BlockType)math.clamp(e.type, 0, byte.MaxValue);
                    blockTypes[idx] = (byte)bt;
                    BlockTextureMapping mapping = blockMappings[(int)bt];
                    solids[idx] = mapping.isSolid;
                }
            }
        }
        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            int r = a % b;
            if (r != 0 && ((a < 0 && b > 0) || (a > 0 && b < 0))) q--;
            return q;
        }
    }

    [BurstCompile]
    public struct FillWaterAboveTerrainJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> heightCache;
        [NativeDisableParallelForRestriction] public NativeArray<byte> blockTypes;
        [NativeDisableParallelForRestriction] public NativeArray<bool> solids;

        public int border;
        public int seaLevel;
        public byte waterBlockId;
        public bool waterIsSolid;

        public void Execute(int index)
        {
            int heightStride = SizeX + 2 * border;
            int maxY = math.min(seaLevel, SizeY - 1);
            if (maxY < 0)
                return;

            int cacheX = index % heightStride;
            int cacheZ = index / heightStride;
            int h = heightCache[index];
            int startY = math.max(0, h + 1);
            if (startY > maxY)
                return;

            int voxelSizeX = heightStride;
            int voxelPlaneSize = voxelSizeX * SizeY;
            int voxelIndex = cacheX + startY * voxelSizeX + cacheZ * voxelPlaneSize;
            for (int y = startY; y <= maxY; y++, voxelIndex += voxelSizeX)
            {
                blockTypes[voxelIndex] = waterBlockId;
                solids[voxelIndex] = waterIsSolid;
            }
        }
    }

}








