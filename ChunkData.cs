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
        //[ReadOnly] public NativeArray<TreeInstance> treeInstances;
        public TreeSettings treeSettings;
        public int oreSeed;
        public int treeMargin;
        public int border;
        public int maxTreeRadius;
        public int baseHeight;
        public float offsetX;
        public float offsetZ;
        public float seaLevel;


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

            // Estime capacidade máxima: número de cells possíveis
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

                    // Use GetSurfaceHeight (já existe no job, mas ajuste para coords locais se necessário)
                    int surfaceY = GetCachedHeight(worldX, worldZ);
                    if (surfaceY <= 0 || surfaceY >= SizeY) continue;

                    // Cheque groundType e cliff (implemente GetSurfaceBlockType e IsCliff usando heightCache)
                    BlockType groundType = GetSurfaceBlockTypeInternal(worldX, worldZ);  // NOVO método (ver abaixo)
                    if (groundType != BlockType.Grass && groundType != BlockType.Dirt) continue;
                    if (IsCliffInternal(worldX, worldZ, CliffTreshold)) continue;  // NOVO método (ver abaixo)

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

        // NOVO: Versão interna de GetSurfaceBlockType (usa heightCache e coords world)
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



