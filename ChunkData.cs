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
    // No topo da classe (junto com as outras const)
    private const int SubchunkHeight = Chunk.SubchunkHeight;
    private const int SubchunksPerColumn = Chunk.SubchunksPerColumn;


    [BurstCompile]
    public struct ChunkDataJob : IJob
    {
        public Vector2Int coord;

        [ReadOnly] public NativeArray<NoiseLayer> noiseLayers;
        [ReadOnly] public NativeArray<WarpLayer> warpLayers;

        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [ReadOnly] public NativeArray<BlockEdit> blockEdits;
        [ReadOnly] public NativeArray<OreSpawnSettings> oreSettings;
        [ReadOnly] public NativeArray<TreeSpawnRuleData> treeSpawnRules;
        public int oreSeed;
        public WormCaveSettings caveSettings;
        public int treeMargin;
        public int border;
        public int maxTreeRadius;
        public int baseHeight;
        public float offsetX;
        public float offsetZ;
        public float seaLevel;
        public BiomeNoiseSettings biomeNoiseSettings;


        public int CliffTreshold;
        public bool enableTrees;
        public NativeArray<int> heightCache;
        public NativeArray<BlockType> blockTypes;
        public NativeArray<bool> solids;

        public NativeArray<bool> subchunkNonEmpty;

        public void Execute()
        {
            int heightSize = SizeX + 2 * border;
            int heightStride = heightSize;

            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;



            // 2. Popular voxels (terreno, água)
            //PopulateTerrainColumns(heightCache, blockTypes, solids, voxelSizeX, voxelSizeZ);

            GenerateWormCaves(blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride);
            GenerateOreVeins(blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride);
            FillWaterAboveTerrain(heightCache, blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            if (enableTrees)
            {
                // NOVO: Gere as árvores aqui
                NativeList<TreeInstance> trees = GenerateTreeInstances();

                // Aplicação existente (agora com trees local)
                TreePlacement.ApplyTreeInstancesToVoxels(
                    blockTypes, solids, blockMappings, trees.AsArray(), coord, border,
                    SizeX, SizeZ, SizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightCache, heightStride
                );

                trees.Dispose();  // Cleanup
            }

            ApplyBlockEditsToVoxels(blockTypes, solids, voxelSizeX, voxelSizeZ);

            // === NOVO: pré-calcula quais subchunks têm blocos ===
            for (int s = 0; s < SubchunksPerColumn; s++)
                subchunkNonEmpty[s] = false;

            for (int s = 0; s < SubchunksPerColumn; s++)
            {
                int startY = s * SubchunkHeight;
                if (startY >= SizeY)
                {
                    subchunkNonEmpty[s] = false;
                    continue;
                }

                int endY = math.min(startY + SubchunkHeight, SizeY);
                bool hasBlock = false;

                for (int y = startY; y < endY && !hasBlock; y++)
                    for (int z = border; z < border + SizeZ && !hasBlock; z++)
                        for (int x = border; x < border + SizeX && !hasBlock; x++)
                        {
                            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
                            if (blockTypes[idx] != BlockType.Air)
                            {
                                hasBlock = true;
                                break;
                            }
                        }
                subchunkNonEmpty[s] = hasBlock;
            }
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

            int searchMargin = settings.canopyRadius + settings.minSpacing;
            int cellX0 = FloorDiv(chunkMinX - searchMargin, cellSize);
            int cellX1 = FloorDiv(chunkMaxX + searchMargin, cellSize);
            int cellZ0 = FloorDiv(chunkMinZ - searchMargin, cellSize);
            int cellZ1 = FloorDiv(chunkMaxZ + searchMargin, cellSize);

            float freq = math.max(1e-4f, settings.noiseScale);
            float density = math.saturate(settings.density);
            int ruleSeed = settings.seed != 0 ? settings.seed : oreSeed;
            int minTrunk = math.max(1, settings.minHeight);
            int maxTrunk = math.max(minTrunk, settings.maxHeight);
            int canopyRadius = math.max(0, settings.canopyRadius);
            int canopyHeight = math.max(1, settings.canopyHeight);
            int proximitySpacing = math.max(1, settings.minSpacing / 2);

            for (int cx = cellX0; cx <= cellX1; cx++)
            {
                for (int cz = cellZ0; cz <= cellZ1; cz++)
                {
                    float noiseX = (cx * 12.9898f + ruleSeed) * freq;
                    float noiseZ = (cz * 78.233f + ruleSeed) * freq;
                    float sample = noise.cnoise(new float2(noiseX, noiseZ)) * 0.5f + 0.5f;
                    if (sample > density) continue;

                    float offsetNoiseX = noise.cnoise(new float2(noiseX + 1f, noiseZ + 1f)) * 0.5f + 0.5f;
                    float offsetNoiseZ = noise.cnoise(new float2(noiseX + 2f, noiseZ + 2f)) * 0.5f + 0.5f;
                    int worldX = cx * cellSize + (int)math.round(offsetNoiseX * (cellSize - 1));
                    int worldZ = cz * cellSize + (int)math.round(offsetNoiseZ * (cellSize - 1));

                    if (worldX < chunkMinX - searchMargin || worldX > chunkMaxX + searchMargin ||
                        worldZ < chunkMinZ - searchMargin || worldZ > chunkMaxZ + searchMargin) continue;

                    int surfaceY = GetCachedHeight(worldX, worldZ);
                    if (surfaceY <= 0 || surfaceY >= SizeY) continue;

                    BiomeType biome = BiomeUtility.GetBiomeType(worldX, worldZ, biomeNoiseSettings);
                    if (biome != rule.biome) continue;

                    BlockType groundType = GetSurfaceBlockTypeInternal(worldX, worldZ);
                    bool isCactus = rule.treeStyle == TreeStyle.Cactus;
                    if (isCactus)
                    {
                        if (groundType != BlockType.Sand) continue;
                    }
                    else if (groundType != BlockType.Grass && groundType != BlockType.Dirt && groundType != BlockType.Snow)
                    {
                        continue;
                    }

                    if (IsCliffInternal(worldX, worldZ, CliffTreshold)) continue;
                    if (HasNearbyTreeXZ(in trees, worldX, worldZ, proximitySpacing)) continue;

                    float heightNoise = noise.cnoise(new float2((worldX + 0.1f) * 0.137f + ruleSeed * 0.001f, (worldZ + 0.1f) * 0.243f + ruleSeed * 0.001f)) * 0.5f + 0.5f;
                    int trunkH = minTrunk + (int)math.round(heightNoise * (maxTrunk - minTrunk + 1));

                    trees.Add(new TreeInstance
                    {
                        worldX = worldX,
                        worldZ = worldZ,
                        trunkHeight = trunkH,
                        canopyRadius = canopyRadius,
                        canopyHeight = canopyHeight,
                        treeStyle = rule.treeStyle
                    });
                }
            }
        }

        private static bool HasNearbyTreeXZ(in NativeList<TreeInstance> trees, int worldX, int worldZ, int minDistance)
        {
            int minDistSq = minDistance * minDistance;
            for (int i = 0; i < trees.Length; i++)
            {
                TreeInstance t = trees[i];
                int dx = t.worldX - worldX;
                int dz = t.worldZ - worldZ;
                if (dx * dx + dz * dz <= minDistSq)
                    return true;
            }

            return false;
        }

        // NOVO: Versão interna de GetSurfaceBlockType (usa heightCache e coords world)
        private BlockType GetSurfaceBlockTypeInternal(int worldX, int worldZ)
        {
            int h = GetSurfaceHeight(worldX, worldZ);
            bool isBeachArea = (h <= seaLevel + 2);
            bool isCliff = IsCliffInternal(worldX, worldZ, CliffTreshold);
            int mountainStoneHeight = baseHeight + 70;
            bool isHighMountain = h >= mountainStoneHeight;
            BiomeType biome = BiomeUtility.GetBiomeType(worldX, worldZ, biomeNoiseSettings);
            BlockType biomeSurfaceBlock = BiomeUtility.GetSurfaceBlock(biome, biomeNoiseSettings);

            if (isHighMountain) return BlockType.Stone;
            else if (isCliff) return BlockType.Stone;
            else return isBeachArea ? BlockType.Sand : biomeSurfaceBlock;
        }

        // NOVO: Versão interna de IsCliff (usa heightCache, ajuste coords locais)
        private bool IsCliffInternal(int worldX, int worldZ, int threshold = 2)
        {
            int realLx = worldX - coord.x * SizeX;
            int realLz = worldZ - coord.y * SizeZ;
            int cacheX = realLx + border;
            int cacheZ = realLz + border;
            int heightStride = SizeX + 2 * border;

            if (cacheX <= 0 || cacheZ <= 0 || cacheX >= heightStride - 1 || cacheZ >= heightCache.Length / heightStride - 1)
                return false;

            int centerIdx = cacheX + cacheZ * heightStride;
            int h = heightCache[centerIdx];

            int hN = heightCache[centerIdx + heightStride];
            int hS = heightCache[centerIdx - heightStride];
            int hE = heightCache[centerIdx + 1];
            int hW = heightCache[centerIdx - 1];

            int maxDiff = math.max(math.abs(h - hN), math.abs(h - hS));
            maxDiff = math.max(maxDiff, math.abs(h - hE));
            maxDiff = math.max(maxDiff, math.abs(h - hW));

            return maxDiff >= threshold;
        }


        // Lookup rápido no heightCache (usando o border já calculado)
        private int GetCachedHeight(int worldX, int worldZ)
        {
            int realLx = worldX - coord.x * SizeX;
            int realLz = worldZ - coord.y * SizeZ;
            int cacheX = realLx + border;
            int cacheZ = realLz + border;
            int heightStride = SizeX + 2 * border;

            // Segurança (nunca deve cair aqui se o border estiver correto)
            if (cacheX < 0 || cacheX >= heightStride || cacheZ < 0 || cacheZ >= heightStride)
                return GetSurfaceHeight(worldX, worldZ); // fallback raro

            return heightCache[cacheX + cacheZ * heightStride];
        }


        private int GetSurfaceHeight(int worldX, int worldZ)
        {
            // === Domain Warping ===
            float warpX = 0f;
            float warpZ = 0f;
            float sumWarpAmp = 0f;

            for (int i = 0; i < warpLayers.Length; i++)
            {
                var layer = warpLayers[i];
                if (!layer.enabled) continue;

                float baseNx = worldX + layer.offset.x;
                float baseNz = worldZ + layer.offset.y;

                float sampleX = MyNoise.OctavePerlin(baseNx + 100f, baseNz, layer);
                float sampleZ = MyNoise.OctavePerlin(baseNx, baseNz + 100f, layer);

                warpX += (sampleX * 2f - 1f) * layer.amplitude;
                warpZ += (sampleZ * 2f - 1f) * layer.amplitude;
                sumWarpAmp += layer.amplitude;
            }

            if (sumWarpAmp > 0f)
            {
                warpX /= sumWarpAmp;
                warpZ /= sumWarpAmp;
            }

            // === Noise layers ===
            float legacyNoiseTotal = 0f;
            float legacyNoiseWeight = 0f;
            bool hasActiveLayers = false;
            bool hasTypedRoles = false;

            float continentalTotal = 0f;
            float continentalWeight = 0f;
            float erosionTotal = 0f;
            float erosionWeight = 0f;
            float hillsTotal = 0f;
            float hillsWeight = 0f;
            float peaksValleysTotal = 0f;
            float peaksValleysWeight = 0f;
            float mountainTotal = 0f;
            float mountainWeight = 0f;

            for (int i = 0; i < noiseLayers.Length; i++)
            {
                var layer = noiseLayers[i];
                if (!layer.enabled) continue;

                hasActiveLayers = true;

                float nx = (worldX + warpX) + layer.offset.x;
                float nz = (worldZ + warpZ) + layer.offset.y;

                float sample = MyNoise.OctavePerlin(nx, nz, layer);

                if (layer.redistributionModifier != 1f || layer.exponent != 1f)
                {
                    sample = MyNoise.Redistribution(sample, layer.redistributionModifier, layer.exponent);
                }

                MyNoise.AccumulateLayerByRole(
                    layer,
                    sample,
                    ref hasTypedRoles,
                    ref legacyNoiseTotal,
                    ref legacyNoiseWeight,
                    ref continentalTotal,
                    ref continentalWeight,
                    ref erosionTotal,
                    ref erosionWeight,
                    ref hillsTotal,
                    ref hillsWeight,
                    ref peaksValleysTotal,
                    ref peaksValleysWeight,
                    ref mountainTotal,
                    ref mountainWeight
                );
            }

            if (!hasActiveLayers)
            {
                float nx = (worldX + warpX) * 0.05f + offsetX;
                float nz = (worldZ + warpZ) * 0.05f + offsetZ;
                legacyNoiseTotal = noise.cnoise(new float2(nx, nz)) * 0.5f + 0.5f;
                legacyNoiseWeight = 1f;
                hasTypedRoles = false;
            }

            float terrainSignal = hasTypedRoles
                ? MyNoise.ComposeMinecraftLikeTerrainSignal(
                    continentalTotal,
                    continentalWeight,
                    erosionTotal,
                    erosionWeight,
                    hillsTotal,
                    hillsWeight,
                    peaksValleysTotal,
                    peaksValleysWeight,
                    mountainTotal,
                    mountainWeight,
                    legacyNoiseTotal,
                    legacyNoiseWeight)
                : MyNoise.GetLegacyCenteredNoise(legacyNoiseTotal, legacyNoiseWeight);

            return math.clamp(baseHeight + (int)math.floor(terrainSignal), 1, SizeY - 1);
        }

        private void GenerateWormCaves(
            NativeArray<BlockType> blockTypes,
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
            int boundsMinX = chunkMinX - border;
            int boundsMaxX = chunkMinX + SizeX + border - 1;
            int boundsMinZ = chunkMinZ - border;
            int boundsMaxZ = chunkMinZ + SizeZ + border - 1;
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
            NativeArray<BlockType> blockTypes,
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
            NativeArray<BlockType> blockTypes,
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
            NativeArray<BlockType> blockTypes,
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
            int minLocalX = math.max(0, worldCenterX - maxOffset - chunkMinX + border);
            int maxLocalX = math.min(voxelSizeX - 1, worldCenterX + maxOffset - chunkMinX + border);
            int minLocalZ = math.max(0, worldCenterZ - maxOffset - chunkMinZ + border);
            int maxLocalZ = math.min(voxelSizeZ - 1, worldCenterZ + maxOffset - chunkMinZ + border);
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
                        BlockType existing = blockTypes[idx];
                        if (existing == BlockType.Air || existing == BlockType.Water || existing == BlockType.Bedrock)
                            continue;

                        blockTypes[idx] = BlockType.Air;
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

        private void GenerateOreVeins(
            NativeArray<BlockType> blockTypes,
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
            NativeArray<BlockType> blockTypes,
            NativeArray<bool> solids,
            ref uint state)
        {
            int worldCenterX = (int)math.floor(centerWorldX);
            int worldCenterY = (int)math.floor(centerWorldY);
            int worldCenterZ = (int)math.floor(centerWorldZ);
            int radiusSq = radius * radius;

            for (int oz = -radius; oz <= radius; oz++)
            {
                int worldZ = worldCenterZ + oz;
                int localZ = worldZ - chunkMinZ + border;
                if (localZ < 0 || localZ >= voxelSizeZ)
                    continue;

                for (int ox = -radius; ox <= radius; ox++)
                {
                    int worldX = worldCenterX + ox;
                    int localX = worldX - chunkMinX + border;
                    if (localX < 0 || localX >= voxelSizeX)
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
                        BlockType existing = blockTypes[idx];
                        if (!CanReplaceForRule(existing, rule))
                            continue;

                        blockTypes[idx] = rule.blockType;
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


        private void ApplyBlockEditsToVoxels(NativeArray<BlockType> blockTypes, NativeArray<bool> solids, int voxelSizeX, int voxelSizeZ)
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
                    BlockType bt = (BlockType)math.clamp(e.type, 0, 255);
                    blockTypes[idx] = bt;
                    BlockTextureMapping mapping = blockMappings[(int)bt];
                    solids[idx] = mapping.isSolid;
                }
            }
        }



        private void FillWaterAboveTerrain(NativeArray<int> heightCache, NativeArray<BlockType> blockTypes, NativeArray<bool> solids, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            int heightStride = SizeX + 2 * border;
            for (int lx = -border; lx < SizeX + border; lx++)
            {
                for (int lz = -border; lz < SizeZ + border; lz++)
                {
                    int cacheX = lx + border;
                    int cacheZ = lz + border;
                    int cacheIdx = cacheX + cacheZ * heightStride;
                    int h = heightCache[cacheIdx];

                    for (int y = h + 1; y <= seaLevel; y++)
                    {
                        int voxelIdx = cacheX + y * voxelSizeX + cacheZ * voxelPlaneSize;
                        blockTypes[voxelIdx] = BlockType.Water;
                        solids[voxelIdx] = blockMappings[(int)BlockType.Water].isSolid;
                    }
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

}



