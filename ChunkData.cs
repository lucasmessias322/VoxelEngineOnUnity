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
        [ReadOnly] public NativeArray<NoiseLayer> caveLayers;

        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [ReadOnly] public NativeArray<BlockEdit> blockEdits;
        //[ReadOnly] public NativeArray<TreeInstance> treeInstances;
        public TreeSettings treeSettings;
        public int treeMargin;
        public int border;
        public int maxTreeRadius;
        public int baseHeight;
        public float offsetX;
        public float offsetZ;
        public float seaLevel;
        public float caveThreshold;
        public int maxCaveDepthMultiplier;
        public CaveWormSettings caveWormSettings;


        public int CliffTreshold;
        public bool enableCave;
        public bool enableTrees;
        public NativeArray<int> heightCache;
        public NativeArray<BlockType> blockTypes;
        public NativeArray<bool> solids;

        public NativeArray<bool> subchunkNonEmpty;

        public void Execute()
        {
            int heightSize = SizeX + 2 * border;
            int totalHeightPoints = heightSize * heightSize;
            int heightStride = heightSize;

            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;



            // 2. Popular voxels (terreno, cavernas, Ã¡gua)
            //PopulateTerrainColumns(heightCache, blockTypes, solids, voxelSizeX, voxelSizeZ);

            if (enableCave)
            {
                GenerateCaves(heightCache, blockTypes, solids);
            }

            FillWaterAboveTerrain(heightCache, blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            if (enableTrees)
            {
                // NOVO: Gere as Ã¡rvores aqui
                NativeList<TreeInstance> trees = GenerateTreeInstances();

                // AplicaÃ§Ã£o existente (agora com trees local)
                TreePlacement.ApplyTreeInstancesToVoxels(
                    blockTypes, solids, blockMappings, trees.AsArray(), coord, border,
                    SizeX, SizeZ, SizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightCache, heightStride
                );

                trees.Dispose();  // Cleanup
            }

            ApplyBlockEditsToVoxels(blockTypes, solids, voxelSizeX, voxelSizeZ);

            // === NOVO: prÃ©-calcula quais subchunks tÃªm blocos ===
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
            int cellSize = math.max(1, treeSettings.minSpacing);
            int chunkMinX = coord.x * SizeX;
            int chunkMinZ = coord.y * SizeZ;
            int chunkMaxX = chunkMinX + SizeX - 1;
            int chunkMaxZ = chunkMinZ + SizeZ - 1;

            int searchMargin = treeSettings.canopyRadius + treeSettings.minSpacing;
            int cellX0 = FloorDiv(chunkMinX - searchMargin, cellSize);
            int cellX1 = FloorDiv(chunkMaxX + searchMargin, cellSize);
            int cellZ0 = FloorDiv(chunkMinZ - searchMargin, cellSize);
            int cellZ1 = FloorDiv(chunkMaxZ + searchMargin, cellSize);

            float freq = treeSettings.noiseScale;

            // Estime capacidade mÃ¡xima: nÃºmero de cells possÃ­veis
            int maxTrees = (cellX1 - cellX0 + 1) * (cellZ1 - cellZ0 + 1);
            NativeList<TreeInstance> trees = new NativeList<TreeInstance>(maxTrees, Allocator.Temp);

            for (int cx = cellX0; cx <= cellX1; cx++)
            {
                for (int cz = cellZ0; cz <= cellZ1; cz++)
                {
                    float noiseX = (cx * 12.9898f + treeSettings.seed) * freq;
                    float noiseZ = (cz * 78.233f + treeSettings.seed) * freq;

                    // Substitua Mathf.PerlinNoise por noise.cnoise (normalizado para [0,1])
                    float sample = noise.cnoise(new float2(noiseX, noiseZ)) * 0.5f + 0.5f;

                    if (sample > treeSettings.density) continue;

                    // Offsets dentro da cell (novamente com noise)
                    float offsetNoiseX = noise.cnoise(new float2(noiseX + 1f, noiseZ + 1f)) * 0.5f + 0.5f;
                    float offsetNoiseZ = noise.cnoise(new float2(noiseX + 2f, noiseZ + 2f)) * 0.5f + 0.5f;

                    int worldX = cx * cellSize + (int)math.round(offsetNoiseX * (cellSize - 1));
                    int worldZ = cz * cellSize + (int)math.round(offsetNoiseZ * (cellSize - 1));

                    if (worldX < chunkMinX - searchMargin || worldX > chunkMaxX + searchMargin ||
                        worldZ < chunkMinZ - searchMargin || worldZ > chunkMaxZ + searchMargin) continue;

                    // Use GetSurfaceHeight (jÃ¡ existe no job, mas ajuste para coords locais se necessÃ¡rio)
                    int surfaceY = GetCachedHeight(worldX, worldZ);
                    if (surfaceY <= 0 || surfaceY >= SizeY) continue;

                    // Cheque groundType e cliff (implemente GetSurfaceBlockType e IsCliff usando heightCache)
                    BlockType groundType = GetSurfaceBlockTypeInternal(worldX, worldZ);  // NOVO mÃ©todo (ver abaixo)
                    if (groundType != BlockType.Grass && groundType != BlockType.Dirt) continue;
                    if (IsCliffInternal(worldX, worldZ, CliffTreshold)) continue;  // NOVO mÃ©todo (ver abaixo)

                    // Height noise
                    float heightNoise = noise.cnoise(new float2((worldX + 0.1f) * 0.137f + treeSettings.seed * 0.001f, (worldZ + 0.1f) * 0.243f + treeSettings.seed * 0.001f)) * 0.5f + 0.5f;
                    int trunkH = treeSettings.minHeight + (int)math.round(heightNoise * (treeSettings.maxHeight - treeSettings.minHeight + 1));

                    trees.Add(new TreeInstance
                    {
                        worldX = worldX,
                        worldZ = worldZ,
                        trunkHeight = trunkH,
                        canopyRadius = treeSettings.canopyRadius,
                        canopyHeight = treeSettings.canopyHeight
                    });
                }
            }

            return trees;
        }

        // NOVO: VersÃ£o interna de GetSurfaceBlockType (usa heightCache e coords world)
        private BlockType GetSurfaceBlockTypeInternal(int worldX, int worldZ)
        {
            int h = GetSurfaceHeight(worldX, worldZ);
            bool isBeachArea = (h <= seaLevel + 2);
            bool isCliff = IsCliffInternal(worldX, worldZ, CliffTreshold);
            int mountainStoneHeight = baseHeight + 70;
            bool isHighMountain = h >= mountainStoneHeight;

            if (isHighMountain) return BlockType.Stone;
            else if (isCliff) return BlockType.Stone;
            else return isBeachArea ? BlockType.Sand : BlockType.Grass;
        }

        // NOVO: VersÃ£o interna de IsCliff (usa heightCache, ajuste coords locais)
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


        // Lookup rÃ¡pido no heightCache (usando o border jÃ¡ calculado)
        private int GetCachedHeight(int worldX, int worldZ)
        {
            int realLx = worldX - coord.x * SizeX;
            int realLz = worldZ - coord.y * SizeZ;
            int cacheX = realLx + border;
            int cacheZ = realLz + border;
            int heightStride = SizeX + 2 * border;

            // SeguranÃ§a (nunca deve cair aqui se o border estiver correto)
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
            warpX = (warpX - 0.5f) * 2f;
            warpZ = (warpZ - 0.5f) * 2f;

            // === Noise layers ===
            float totalNoise = 0f;
            float sumAmp = 0f;
            bool hasActiveLayers = false;

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

                totalNoise += sample * layer.amplitude;
                sumAmp += math.max(1e-5f, layer.amplitude);
            }

            if (!hasActiveLayers || sumAmp <= 0f)
            {
                float nx = (worldX + warpX) * 0.05f + offsetX;
                float nz = (worldZ + warpZ) * 0.05f + offsetZ;
                totalNoise = noise.cnoise(new float2(nx, nz)) * 0.5f + 0.5f;
                sumAmp = 1f;
            }

            float centered = totalNoise - sumAmp * 0.5f;
            return math.clamp(baseHeight + (int)math.floor(centered), 1, SizeY - 1);
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
        private void GenerateCaves(NativeArray<int> heightCache, NativeArray<BlockType> blockTypes, NativeArray<bool> solids)
        {
            if (caveLayers.Length == 0) return;

            if (caveWormSettings.enabled)
            {
                GenerateWormCaves(heightCache, blockTypes, solids);
            }
            else
            {
                GenerateThresholdCaves(heightCache, blockTypes, solids);
            }
        }

        private void GenerateThresholdCaves(NativeArray<int> heightCache, NativeArray<BlockType> blockTypes, NativeArray<bool> solids)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;
            int heightStride = SizeX + 2 * border;

            int baseWorldX = coord.x * SizeX;
            int baseWorldZ = coord.y * SizeZ;
            int maxCaveY = math.min(SizeY - 1, (int)seaLevel * math.max(1, maxCaveDepthMultiplier));

            for (int lx = -border; lx < SizeX + border; lx++)
            {
                int worldX = baseWorldX + lx;
                int cacheX = lx + border;

                for (int lz = -border; lz < SizeZ + border; lz++)
                {
                    int worldZ = baseWorldZ + lz;
                    int cacheZ = lz + border;
                    int surfaceY = heightCache[cacheX + cacheZ * heightStride] - 2;

                    for (int y = 11; y <= maxCaveY; y++)
                    {
                        if (y > surfaceY) continue;

                        int voxelIdx = cacheX + y * voxelSizeX + cacheZ * voxelPlaneSize;
                        if (!solids[voxelIdx]) continue;

                        float caveSample = ComputeCaveNoise(worldX, y, worldZ);
                        if (caveSample < caveThreshold)
                        {
                            blockTypes[voxelIdx] = BlockType.Air;
                            solids[voxelIdx] = false;
                        }
                    }
                }
            }
        }

        private void GenerateWormCaves(NativeArray<int> heightCache, NativeArray<BlockType> blockTypes, NativeArray<bool> solids)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;
            int heightStride = SizeX + 2 * border;

            int baseWorldX = coord.x * SizeX;
            int baseWorldZ = coord.y * SizeZ;
            int baseMaxCaveY = math.min(SizeY - 1, (int)seaLevel * math.max(1, maxCaveDepthMultiplier));
            int minCaveY = math.clamp(caveWormSettings.minY, 11, math.max(11, baseMaxCaveY - 2));
            if (baseMaxCaveY <= minCaveY) return;

            int cellSize = math.max(8, caveWormSettings.cellSize);
            int carveStride = math.max(1, caveWormSettings.carveStride);
            float spawnChance = math.clamp(caveWormSettings.spawnChance, 0f, 1f);
            int minSteps = math.max(8, caveWormSettings.minSteps);
            int maxSteps = math.max(minSteps, caveWormSettings.maxSteps);
            float stepLength = math.max(0.5f, caveWormSettings.stepLength);
            float minRadius = math.max(0.75f, caveWormSettings.minRadius);
            float maxRadius = math.max(minRadius, caveWormSettings.maxRadius);
            float verticalRadiusMultiplier = math.clamp(caveWormSettings.verticalRadiusMultiplier, 0.25f, 1.5f);
            float directionNoiseScale = math.max(0.001f, caveWormSettings.directionNoiseScale);
            float boundaryPullStrength = math.max(0.05f, caveWormSettings.boundaryPullStrength);
            float boundaryBand = math.max(0.005f, caveWormSettings.boundaryBand);
            float verticalJitter = math.clamp(caveWormSettings.verticalJitter, 0f, 1f);
            int boundarySearchIters = math.clamp(caveWormSettings.boundarySearchIters, 1, 8);

            float maxReach = maxSteps * stepLength + maxRadius + 4f;
            int seedPadding = (int)math.ceil(maxReach);

            int minSeedX = baseWorldX - seedPadding;
            int maxSeedX = baseWorldX + SizeX - 1 + seedPadding;
            int minSeedZ = baseWorldZ - seedPadding;
            int maxSeedZ = baseWorldZ + SizeZ - 1 + seedPadding;

            int cellX0 = FloorDiv(minSeedX, cellSize);
            int cellX1 = FloorDiv(maxSeedX, cellSize);
            int cellZ0 = FloorDiv(minSeedZ, cellSize);
            int cellZ1 = FloorDiv(maxSeedZ, cellSize);

            int yRange = math.max(1, baseMaxCaveY - minCaveY);

            for (int cellX = cellX0; cellX <= cellX1; cellX++)
            {
                for (int cellZ = cellZ0; cellZ <= cellZ1; cellZ++)
                {
                    uint spawnHash = math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 9127));
                    if (Hash01(spawnHash) > spawnChance) continue;

                    float startOffsetX = Hash01(math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 1223)));
                    float startOffsetZ = Hash01(math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 2141)));
                    float startOffsetY = Hash01(math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 3319)));

                    float startX = cellX * cellSize + startOffsetX * cellSize;
                    float startZ = cellZ * cellSize + startOffsetZ * cellSize;
                    float startY = minCaveY + startOffsetY * yRange;
                    int startSurfaceY = GetCachedHeight((int)math.floor(startX), (int)math.floor(startZ));
                    int cellMaxCaveY = math.min(SizeY - 1, math.max(baseMaxCaveY, startSurfaceY - 1));
                    if (cellMaxCaveY <= minCaveY + 1) continue;

                    int steps = minSteps + (int)math.floor(Hash01(math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 4019))) * (maxSteps - minSteps + 1));
                    float yaw = Hash01(math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 5171))) * math.PI * 2f;
                    float pitch = HashSigned(math.hash(new int4(cellX, cellZ, caveWormSettings.seed, 6131))) * verticalJitter;

                    float3 pos = new float3(startX, startY, startZ);
                    ProjectToBoundary(ref pos, boundaryBand, boundaryPullStrength, boundarySearchIters, minCaveY, cellMaxCaveY);
                    if (math.abs(ComputeCaveSignedNoise(pos.x, pos.y, pos.z)) > boundaryBand * 5f) continue;

                    float3 dir = math.normalizesafe(new float3(math.cos(yaw), pitch, math.sin(yaw)), new float3(1f, 0f, 0f));
                    int stepsSinceLastCarve = 0;

                    for (int step = 0; step < steps; step++)
                    {
                        float t = step / math.max(1f, steps - 1f);
                        float tunnelShape = math.sin(t * math.PI);
                        float radiusNoise = HashSigned(math.hash(new int4(cellX ^ step, cellZ + step * 17, caveWormSettings.seed, 7331)));
                        float radiusBlend = math.saturate(0.35f + 0.65f * tunnelShape + radiusNoise * 0.12f);
                        float radius = math.lerp(minRadius, maxRadius, radiusBlend);
                        stepsSinceLastCarve++;

                        bool shouldCarve = step == 0 || stepsSinceLastCarve >= carveStride || step == steps - 1;
                        if (shouldCarve)
                        {
                            float bridgeRadius = stepLength * math.max(0, stepsSinceLastCarve - 1) * 0.35f;
                            float carveRadius = radius + bridgeRadius;

                            CarveEllipsoidAtWorld(
                                pos, carveRadius, verticalRadiusMultiplier,
                                minCaveY, cellMaxCaveY,
                                baseWorldX, baseWorldZ,
                                voxelSizeX, voxelSizeZ, voxelPlaneSize, heightStride,
                                heightCache, blockTypes, solids
                            );

                            stepsSinceLastCarve = 0;
                        }

                        float signed = ComputeCaveSignedNoise(pos.x, pos.y, pos.z);
                        float3 grad = EstimateCaveGradient(pos.x, pos.y, pos.z);
                        float3 gradDir = math.normalizesafe(grad, new float3(0f, 0f, 0f));
                        if (math.lengthsq(gradDir) > 1e-5f)
                        {
                            pos -= gradDir * signed * boundaryPullStrength * 2f;
                        }

                        float3 flow = SampleDirectionFlow(pos, directionNoiseScale, caveWormSettings.seed);
                        flow.y *= verticalJitter;
                        dir = math.normalizesafe(dir * 0.82f + flow * 0.18f, dir);
                        pos += dir * stepLength;

                        if (pos.y < minCaveY + 0.5f)
                        {
                            pos.y = minCaveY + 0.5f;
                            dir.y = math.abs(dir.y);
                        }
                        else if (pos.y > cellMaxCaveY - 0.5f)
                        {
                            pos.y = cellMaxCaveY - 0.5f;
                            dir.y = -math.abs(dir.y);
                        }
                    }
                }
            }
        }

        private void ProjectToBoundary(ref float3 pos, float boundaryBand, float boundaryPullStrength, int searchIters, int minCaveY, int maxCaveY)
        {
            for (int i = 0; i < searchIters; i++)
            {
                float signed = ComputeCaveSignedNoise(pos.x, pos.y, pos.z);
                if (math.abs(signed) <= boundaryBand) break;

                float3 grad = EstimateCaveGradient(pos.x, pos.y, pos.z);
                float3 gradDir = math.normalizesafe(grad, new float3(0f, 0f, 0f));
                if (math.lengthsq(gradDir) < 1e-5f) break;

                pos -= gradDir * signed * boundaryPullStrength * 2.6f;
                pos.y = math.clamp(pos.y, minCaveY + 0.5f, maxCaveY - 0.5f);
            }
        }

        private void CarveEllipsoidAtWorld(
            float3 center,
            float radiusXZ,
            float verticalRadiusMultiplier,
            int minCaveY,
            int maxCaveY,
            int baseWorldX,
            int baseWorldZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int heightStride,
            NativeArray<int> heightCache,
            NativeArray<BlockType> blockTypes,
            NativeArray<bool> solids)
        {
            float radiusY = math.max(0.6f, radiusXZ * verticalRadiusMultiplier);
            float invRadiusXZ = 1f / math.max(0.001f, radiusXZ);
            float invRadiusY = 1f / math.max(0.001f, radiusY);

            int minWorldX = (int)math.floor(center.x - radiusXZ);
            int maxWorldX = (int)math.ceil(center.x + radiusXZ);
            int minWorldZ = (int)math.floor(center.z - radiusXZ);
            int maxWorldZ = (int)math.ceil(center.z + radiusXZ);

            for (int wz = minWorldZ; wz <= maxWorldZ; wz++)
            {
                int lz = wz - baseWorldZ + border;
                if (lz < 0 || lz >= voxelSizeZ) continue;

                float dz = (wz + 0.5f - center.z) * invRadiusXZ;
                float dzSq = dz * dz;
                if (dzSq > 1f) continue;

                for (int wx = minWorldX; wx <= maxWorldX; wx++)
                {
                    int lx = wx - baseWorldX + border;
                    if (lx < 0 || lx >= voxelSizeX) continue;

                    float dx = (wx + 0.5f - center.x) * invRadiusXZ;
                    float dxSq = dx * dx;
                    float radial = dxSq + dzSq;
                    if (radial > 1f) continue;

                    int surfaceY = heightCache[lx + lz * heightStride];
                    int topLimit = GetWormTopCarveLimit(wx, wz, surfaceY, center.y, radiusY);
                    int yStart = math.max(minCaveY, (int)math.floor(center.y - radiusY));
                    int yEnd = math.min(math.min(maxCaveY, topLimit), (int)math.ceil(center.y + radiusY));
                    if (yEnd < yStart) continue;

                    for (int y = yStart; y <= yEnd; y++)
                    {
                        if (y <= 10) continue;

                        float dy = (y + 0.5f - center.y) * invRadiusY;
                        if (radial + dy * dy > 1f) continue;

                        int idx = lx + y * voxelSizeX + lz * voxelPlaneSize;
                        if (!solids[idx]) continue;

                        blockTypes[idx] = BlockType.Air;
                        solids[idx] = false;
                    }
                }
            }
        }

        private float3 SampleDirectionFlow(float3 pos, float scale, int seed)
        {
            float sx = seed * 0.00117f;
            float sy = seed * -0.00091f;
            float sz = seed * 0.00063f;
            float3 p = pos * scale;

            float nx = noise.snoise(new float3(p.x + sx + 11.1f, p.y + sy, p.z + sz));
            float ny = noise.snoise(new float3(p.x + sx - 7.3f, p.y + sy + 21.7f, p.z + sz + 5.9f));
            float nz = noise.snoise(new float3(p.x + sx + 19.4f, p.y + sy - 13.5f, p.z + sz - 17.2f));

            return math.normalizesafe(new float3(nx, ny, nz), new float3(1f, 0f, 0f));
        }

        private float3 EstimateCaveGradient(float worldX, float worldY, float worldZ)
        {
            const float eps = 1.25f;

            float dx = ComputeCaveSignedNoise(worldX + eps, worldY, worldZ) - ComputeCaveSignedNoise(worldX - eps, worldY, worldZ);
            float dy = ComputeCaveSignedNoise(worldX, worldY + eps, worldZ) - ComputeCaveSignedNoise(worldX, worldY - eps, worldZ);
            float dz = ComputeCaveSignedNoise(worldX, worldY, worldZ + eps) - ComputeCaveSignedNoise(worldX, worldY, worldZ - eps);

            return new float3(dx, dy, dz);
        }

        private int GetWormTopCarveLimit(int worldX, int worldZ, int surfaceY, float centerY, float radiusY)
        {
            int closedLimit = surfaceY - 2;
            bool nearSurface = (surfaceY - centerY) <= (radiusY + 1.2f);
            if (!nearSurface) return closedLimit;

            float mask = noise.snoise(new float2(
                (worldX + caveWormSettings.seed * 0.37f) * 0.08f,
                (worldZ - caveWormSettings.seed * 0.21f) * 0.08f
            )) * 0.5f + 0.5f;

            return mask > 0.68f ? surfaceY : closedLimit;
        }

        private float ComputeCaveSignedNoise(float worldX, float worldY, float worldZ)
        {
            return ComputeCaveNoise(worldX, worldY, worldZ) - caveThreshold;
        }

        private float ComputeCaveNoise(int worldX, int worldY, int worldZ)
        {
            return ComputeCaveNoise((float)worldX, worldY, (float)worldZ);
        }

        private float ComputeCaveNoise(float worldX, float worldY, float worldZ)
        {
            float totalCave = 0f;
            float sumCaveAmp = 0f;

            for (int i = 0; i < caveLayers.Length; i++)
            {
                var layer = caveLayers[i];
                if (!layer.enabled) continue;

                float nx = worldX + layer.offset.x;
                float ny = worldY;
                float nz = worldZ + layer.offset.y;

                float finalSample = MyNoise.OctavePerlin3D(nx, ny, nz, layer);

                if (layer.redistributionModifier != 1f || layer.exponent != 1f)
                {
                    finalSample = MyNoise.Redistribution(finalSample, layer.redistributionModifier, layer.exponent);
                }

                totalCave += finalSample * layer.amplitude;
                sumCaveAmp += math.max(1e-5f, layer.amplitude);
            }

            return sumCaveAmp > 0f ? totalCave / sumCaveAmp : 0f;
        }

        private static float Hash01(uint hash)
        {
            return (hash & 0x00FFFFFFu) / 16777216f;
        }

        private static float HashSigned(uint hash)
        {
            return Hash01(hash) * 2f - 1f;
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

