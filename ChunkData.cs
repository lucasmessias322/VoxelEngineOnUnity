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
        public int caveStride;
        public int maxCaveDepthMultiplier;
        public WormTunnelSettings wormSettings;


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



            // 2. Popular voxels (terreno, cavernas, água)
            //PopulateTerrainColumns(heightCache, blockTypes, solids, voxelSizeX, voxelSizeZ);

            if (enableCave)
            {
                GenerateCaves(blockTypes, solids);
            }

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



        private void GenerateCaves(NativeArray<BlockType> blockTypes, NativeArray<bool> solids)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;

            int baseWorldX = coord.x * SizeX;
            int baseWorldZ = coord.y * SizeZ;

            // Worm mode: Voronoi vira apenas mascara/guia do trajeto dos worms.
            // Nao executa escavacao volumetrica por threshold para evitar custo alto.
            if (wormSettings.enabled)
            {
                CarveVoronoiEdgeWormTunnels(blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, baseWorldX, baseWorldZ);
                return;
            }

            if (caveLayers.Length > 0 && caveStride >= 1)
            {
                int stride = math.max(1, caveStride);

                int minWorldX = baseWorldX - border;
                int maxWorldX = baseWorldX + SizeX + border - 1;
                int minWorldZ = baseWorldZ - border;
                int maxWorldZ = baseWorldZ + SizeZ + border - 1;
                int minWorldY = 0;
                int maxWorldY = SizeY - 1;

                int coarseCountX = FloorDiv(maxWorldX - minWorldX, stride) + 2;
                int coarseCountY = FloorDiv(maxWorldY - minWorldY, stride) + 2;
                int coarseCountZ = FloorDiv(maxWorldZ - minWorldZ, stride) + 2;

                NativeArray<float> coarseCaveNoise = new NativeArray<float>(coarseCountX * coarseCountY * coarseCountZ, Allocator.Temp);
                int coarseStrideX = coarseCountX;
                int coarsePlaneSize = coarseCountX * coarseCountY;

                for (int cy = 0; cy < coarseCountY; cy++)
                {
                    int worldY = minWorldY + cy * stride;
                    for (int cx = 0; cx < coarseCountX; cx++)
                    {
                        int worldX = minWorldX + cx * stride;
                        for (int cz = 0; cz < coarseCountZ; cz++)
                        {
                            int worldZ = minWorldZ + cz * stride;

                            float totalCave = 0f;
                            float sumCaveAmp = 0f;


                            for (int i = 0; i < caveLayers.Length; i++)
                            {
                                var layer = caveLayers[i];
                                if (!layer.enabled) continue;

                                float nx = worldX + layer.offset.x;
                                float ny = worldY;
                                float nz = worldZ + layer.offset.y;

                                // Usa o nosso novo Worley/Cellular Noise que já cria os formatos de túnel nativamente
                                float finalSample = MyNoise.OctaveVoronoi3D(nx, ny, nz, layer);

                                // Mantemos o suporte ao Redistribution Modifier para que você
                                // possa controlar o tamanho/formato dos túneis no seu ScriptableObject/Inspector!
                                if (layer.redistributionModifier != 1f || layer.exponent != 1f)
                                {
                                    finalSample = MyNoise.Redistribution(finalSample, layer.redistributionModifier, layer.exponent);
                                }

                                totalCave += finalSample * layer.amplitude;
                                sumCaveAmp += math.max(1e-5f, layer.amplitude);
                            }

                            if (sumCaveAmp > 0f) totalCave /= sumCaveAmp;

                            int coarseIdx = cx + cy * coarseStrideX + cz * coarsePlaneSize;
                            coarseCaveNoise[coarseIdx] = totalCave;
                        }
                    }
                }

                // Interpolação para voxels
                for (int lx = -border; lx < SizeX + border; lx++)
                {
                    for (int lz = -border; lz < SizeZ + border; lz++)
                    {
                        int cacheX = lx + border;
                        int cacheZ = lz + border;

                        int maxCaveY = math.min(SizeY - 1, (int)seaLevel * maxCaveDepthMultiplier);

                        for (int y = 0; y <= maxCaveY; y++)
                        {
                            if (y <= 10) continue; // Protege o Bedrock
                            int voxelIdx = cacheX + y * voxelSizeX + cacheZ * voxelPlaneSize;
                            if (!solids[voxelIdx]) continue;

                            int worldX = baseWorldX + lx;
                            int worldY = y;
                            int worldZ = baseWorldZ + lz;

                            int cx0 = FloorDiv(worldX - minWorldX, stride);
                            int cy0 = FloorDiv(worldY - minWorldY, stride);
                            int cz0 = FloorDiv(worldZ - minWorldZ, stride);

                            int cx1 = cx0 + 1;
                            int cy1 = cy0 + 1;
                            int cz1 = cz0 + 1;

                            float fracX = (float)(worldX - (minWorldX + cx0 * stride)) / stride;
                            float fracY = (float)(worldY - (minWorldY + cy0 * stride)) / stride;
                            float fracZ = (float)(worldZ - (minWorldZ + cz0 * stride)) / stride;

                            float c000 = coarseCaveNoise[cx0 + cy0 * coarseStrideX + cz0 * coarsePlaneSize];
                            float c001 = coarseCaveNoise[cx0 + cy0 * coarseStrideX + cz1 * coarsePlaneSize];
                            float c010 = coarseCaveNoise[cx0 + cy1 * coarseStrideX + cz0 * coarsePlaneSize];
                            float c011 = coarseCaveNoise[cx0 + cy1 * coarseStrideX + cz1 * coarsePlaneSize];
                            float c100 = coarseCaveNoise[cx1 + cy0 * coarseStrideX + cz0 * coarsePlaneSize];
                            float c101 = coarseCaveNoise[cx1 + cy0 * coarseStrideX + cz1 * coarsePlaneSize];
                            float c110 = coarseCaveNoise[cx1 + cy1 * coarseStrideX + cz0 * coarsePlaneSize];
                            float c111 = coarseCaveNoise[cx1 + cy1 * coarseStrideX + cz1 * coarsePlaneSize];

                            float c00 = math.lerp(c000, c100, fracX);
                            float c01 = math.lerp(c001, c101, fracX);
                            float c10 = math.lerp(c010, c110, fracX);
                            float c11 = math.lerp(c011, c111, fracX);

                            float c0 = math.lerp(c00, c10, fracY);
                            float c1 = math.lerp(c01, c11, fracY);

                            float interpolatedCave = math.lerp(c0, c1, fracZ);

                            float adjustedThreshold = caveThreshold;
                            if (interpolatedCave < adjustedThreshold)
                            {
                                blockTypes[voxelIdx] = BlockType.Air;
                                solids[voxelIdx] = false;
                            }
                        }
                    }
                }

                coarseCaveNoise.Dispose();
            }

            CarveVoronoiEdgeWormTunnels(blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize, baseWorldX, baseWorldZ);

        }

        private void CarveVoronoiEdgeWormTunnels(
            NativeArray<BlockType> blockTypes,
            NativeArray<bool> solids,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int baseWorldX,
            int baseWorldZ)
        {
            if (!wormSettings.enabled) return;
            if (!TryGetPrimaryCaveLayer(out NoiseLayer guideLayer)) return;

            int maxCaveY = math.min(SizeY - 1, (int)seaLevel * math.max(1, maxCaveDepthMultiplier));
            if (maxCaveY <= 11) return;

            int minWorldX = baseWorldX - border;
            int maxWorldX = baseWorldX + SizeX + border - 1;
            int minWorldZ = baseWorldZ - border;
            int maxWorldZ = baseWorldZ + SizeZ + border - 1;

            int regionSize = math.max(8, wormSettings.regionSize);
            int wormsPerRegion = math.max(1, wormSettings.wormsPerRegion);
            int minSteps = math.max(4, wormSettings.minSteps);
            int maxSteps = math.max(minSteps, wormSettings.maxSteps);
            float stepLength = math.max(0.6f, wormSettings.stepLength);
            float baseRadius = math.max(0.75f, wormSettings.baseRadius);
            float radiusJitter = math.max(0f, wormSettings.radiusJitter);
            float maxTravelPerWorm = regionSize * 1.35f;
            int cappedMaxSteps = math.max(minSteps, math.min(maxSteps, (int)math.ceil(maxTravelPerWorm / stepLength)));
            int stepRange = cappedMaxSteps - minSteps + 1;

            // Processa somente regioes locais e vizinhas para manter custo previsivel.
            int regionRing = 1;
            int regionMinX = FloorDiv(minWorldX, regionSize) - regionRing;
            int regionMaxX = FloorDiv(maxWorldX, regionSize) + regionRing;
            int regionMinZ = FloorDiv(minWorldZ, regionSize) - regionRing;
            int regionMaxZ = FloorDiv(maxWorldZ, regionSize) + regionRing;

            for (int rx = regionMinX; rx <= regionMaxX; rx++)
            {
                for (int rz = regionMinZ; rz <= regionMaxZ; rz++)
                {
                    for (int wormIndex = 0; wormIndex < wormsPerRegion; wormIndex++)
                    {
                        uint state = Hash4((uint)wormSettings.seed, rx, rz, wormIndex);
                        float3 startPos = SelectVoronoiEdgeSeed(rx, rz, regionSize, maxCaveY, guideLayer, ref state);

                        float radiusRnd = Hash01(NextHash(ref state)) * 2f - 1f;
                        float radius = math.max(0.75f, baseRadius + radiusRnd * radiusJitter);

                        int steps = minSteps + (int)math.floor(Hash01(NextHash(ref state)) * stepRange);

                        float3 dir = DirectionFromHash(NextHash(ref state));
                        float verticalDamping = math.clamp(wormSettings.verticalDamping, 0f, 1f);
                        dir.y *= verticalDamping;
                        dir = SafeNormalize(dir, new float3(1f, 0f, 0f));

                        CarveSingleWorm(
                            blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize,
                            baseWorldX, baseWorldZ, maxCaveY, guideLayer,
                            startPos, dir, steps, radius, state
                        );

                        CarveSingleWorm(
                            blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize,
                            baseWorldX, baseWorldZ, maxCaveY, guideLayer,
                            startPos, -dir, steps, radius, state ^ 0x9E3779B9u
                        );
                    }
                }
            }
        }

        private void CarveSingleWorm(
            NativeArray<BlockType> blockTypes,
            NativeArray<bool> solids,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int baseWorldX,
            int baseWorldZ,
            int maxCaveY,
            NoiseLayer guideLayer,
            float3 startPos,
            float3 startDir,
            int steps,
            float radius,
            uint seedState)
        {
            float stepLength = math.max(0.6f, wormSettings.stepLength);
            float edgeAttraction = math.max(0f, wormSettings.edgeAttraction);
            float tangentStrength = math.max(0f, wormSettings.tangentStrength);
            float noiseStrength = math.max(0f, wormSettings.noiseStrength);
            float verticalDamping = math.clamp(wormSettings.verticalDamping, 0f, 1f);
            float smoothing = math.clamp(wormSettings.directionSmoothing, 0.05f, 1f);

            float3 pos = startPos;
            float3 dir = SafeNormalize(startDir, new float3(1f, 0f, 0f));

            float minY = 11f;
            float maxY = math.max(minY + 1f, maxCaveY - 1f);

            for (int step = 0; step < steps; step++)
            {
                CarveSphereAtWorld(
                    blockTypes, solids,
                    voxelSizeX, voxelSizeZ, voxelPlaneSize,
                    baseWorldX, baseWorldZ,
                    pos, radius, maxCaveY
                );

                float edgeDistance = SampleVoronoiEdgeDistance(pos, guideLayer);
                float3 gradient = SampleVoronoiEdgeGradient(pos, guideLayer);
                float3 gradN = SafeNormalize(gradient, new float3(0f, 1f, 0f));

                float3 tangent = dir - gradN * math.dot(dir, gradN);
                tangent = SafeNormalize(tangent, Orthogonal(gradN));

                float3 noiseDir = SampleNoiseDirection(pos, seedState, step);
                float pull = math.saturate(edgeDistance * 3.5f);
                float3 targetDir = tangent * tangentStrength
                                 + noiseDir * noiseStrength
                                 + (-gradN) * edgeAttraction * pull;

                targetDir.y *= verticalDamping;
                targetDir = SafeNormalize(targetDir, dir);

                dir = SafeNormalize(math.lerp(dir, targetDir, smoothing), targetDir);
                pos += dir * stepLength;
                pos.y = math.clamp(pos.y, minY, maxY);
            }
        }

        private void CarveSphereAtWorld(
            NativeArray<BlockType> blockTypes,
            NativeArray<bool> solids,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            int baseWorldX,
            int baseWorldZ,
            float3 center,
            float radius,
            int maxCaveY)
        {
            int minX = (int)math.floor(center.x - radius);
            int maxX = (int)math.ceil(center.x + radius);
            int minY = math.max(11, (int)math.floor(center.y - radius));
            int maxY = math.min(maxCaveY, (int)math.ceil(center.y + radius));
            int minZ = (int)math.floor(center.z - radius);
            int maxZ = (int)math.ceil(center.z + radius);

            float radiusSq = radius * radius;

            for (int wy = minY; wy <= maxY; wy++)
            {
                float dy = wy - center.y;
                float dySq = dy * dy;

                for (int wx = minX; wx <= maxX; wx++)
                {
                    int ix = wx - baseWorldX + border;
                    if (ix < 0 || ix >= voxelSizeX) continue;

                    float dx = wx - center.x;
                    float dxSq = dx * dx;
                    if (dxSq + dySq > radiusSq) continue;

                    for (int wz = minZ; wz <= maxZ; wz++)
                    {
                        int iz = wz - baseWorldZ + border;
                        if (iz < 0 || iz >= voxelSizeZ) continue;

                        float dz = wz - center.z;
                        float distSq = dxSq + dySq + dz * dz;
                        if (distSq > radiusSq) continue;

                        int idx = ix + wy * voxelSizeX + iz * voxelPlaneSize;
                        if (!solids[idx]) continue;

                        blockTypes[idx] = BlockType.Air;
                        solids[idx] = false;
                    }
                }
            }
        }

        private bool TryGetPrimaryCaveLayer(out NoiseLayer layer)
        {
            for (int i = 0; i < caveLayers.Length; i++)
            {
                if (!caveLayers[i].enabled) continue;
                layer = caveLayers[i];
                return true;
            }

            layer = default;
            return false;
        }

        private float3 SelectVoronoiEdgeSeed(int regionX, int regionZ, int regionSize, int maxCaveY, NoiseLayer guideLayer, ref uint state)
        {
            float minY = 11f;
            float maxY = math.max(minY + 1f, maxCaveY - 1f);
            float3 best = new float3(regionX * regionSize + regionSize * 0.5f, math.lerp(minY, maxY, 0.5f), regionZ * regionSize + regionSize * 0.5f);
            float bestEdge = float.MaxValue;

            for (int i = 0; i < 6; i++)
            {
                float fx = Hash01(NextHash(ref state));
                float fy = Hash01(NextHash(ref state));
                float fz = Hash01(NextHash(ref state));

                float3 candidate = new float3(
                    regionX * regionSize + fx * regionSize,
                    math.lerp(minY, maxY, fy),
                    regionZ * regionSize + fz * regionSize
                );

                float edge = SampleVoronoiEdgeDistance(candidate, guideLayer);
                if (edge < bestEdge)
                {
                    bestEdge = edge;
                    best = candidate;
                }
            }

            return best;
        }

        private static float3 SampleNoiseDirection(float3 worldPos, uint seedState, int step)
        {
            float seedOffset = (seedState & 0xFFFFu) * 0.00091f;
            float stepOffset = step * 0.173f;

            float nx = noise.snoise(new float3(worldPos.x * 0.041f + seedOffset, worldPos.y * 0.029f + stepOffset, worldPos.z * 0.041f - seedOffset));
            float ny = noise.snoise(new float3(worldPos.x * 0.037f - seedOffset, worldPos.y * 0.033f + 13.37f + stepOffset, worldPos.z * 0.037f + seedOffset));
            float nz = noise.snoise(new float3(worldPos.x * 0.043f + 29.1f + seedOffset, worldPos.y * 0.027f + stepOffset, worldPos.z * 0.043f));

            return SafeNormalize(new float3(nx, ny, nz), new float3(0.707f, 0f, 0.707f));
        }

        private static float SampleVoronoiEdgeDistance(float3 worldPos, NoiseLayer guideLayer)
        {
            float scale = math.max(1e-4f, guideLayer.scale);
            float verticalScale = guideLayer.verticalScale > 0f ? guideLayer.verticalScale : scale;

            float3 p = new float3(
                (worldPos.x + guideLayer.offset.x) / scale,
                worldPos.y / verticalScale,
                (worldPos.z + guideLayer.offset.y) / scale
            );

            float2 cell = noise.cellular(p);
            return math.max(0f, cell.y - cell.x);
        }

        private static float3 SampleVoronoiEdgeGradient(float3 worldPos, NoiseLayer guideLayer)
        {
            const float delta = 1.25f;
            float3 dx = new float3(delta, 0f, 0f);
            float3 dy = new float3(0f, delta, 0f);
            float3 dz = new float3(0f, 0f, delta);

            float gx = SampleVoronoiEdgeDistance(worldPos + dx, guideLayer) - SampleVoronoiEdgeDistance(worldPos - dx, guideLayer);
            float gy = SampleVoronoiEdgeDistance(worldPos + dy, guideLayer) - SampleVoronoiEdgeDistance(worldPos - dy, guideLayer);
            float gz = SampleVoronoiEdgeDistance(worldPos + dz, guideLayer) - SampleVoronoiEdgeDistance(worldPos - dz, guideLayer);

            return new float3(gx, gy, gz) * (0.5f / delta);
        }

        private static float3 DirectionFromHash(uint hash)
        {
            float x = Hash01(math.hash(new uint2(hash, 0xA341316Cu))) * 2f - 1f;
            float y = Hash01(math.hash(new uint2(hash, 0xC8013EA4u))) * 2f - 1f;
            float z = Hash01(math.hash(new uint2(hash, 0xAD90777Du))) * 2f - 1f;
            return SafeNormalize(new float3(x, y, z), new float3(1f, 0f, 0f));
        }

        private static float3 Orthogonal(float3 v)
        {
            float3 axis = math.abs(v.y) < 0.95f ? new float3(0f, 1f, 0f) : new float3(1f, 0f, 0f);
            return SafeNormalize(math.cross(v, axis), new float3(1f, 0f, 0f));
        }

        private static float3 SafeNormalize(float3 v, float3 fallback)
        {
            float lenSq = math.lengthsq(v);
            if (lenSq <= 1e-8f) return fallback;
            return v * math.rsqrt(lenSq);
        }

        private static uint Hash4(uint seed, int a, int b, int c)
        {
            return math.hash(new uint4(seed, (uint)a, (uint)b, (uint)c));
        }

        private static uint NextHash(ref uint state)
        {
            state = math.hash(new uint2(state, 0x9E3779B9u));
            return state;
        }

        private static float Hash01(uint h)
        {
            return (h & 0x00FFFFFFu) * (1f / 16777215f);
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

