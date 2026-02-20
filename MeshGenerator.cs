
using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public static class MeshGenerator
{
    private const int SizeX = Chunk.SizeX;
    private const int SizeY = Chunk.SizeY;
    private const int SizeZ = Chunk.SizeZ;

    // ------------------- Tree Instance -------------------
    public struct TreeInstance
    {
        public int worldX;
        public int worldZ;
        public int trunkHeight;
        public int canopyRadius;
        public int canopyHeight;
    }

    // struct para representar uma edição (posição world + tipo do bloco)
    public struct BlockEdit
    {
        public int x;
        public int y;
        public int z;
        public int type; // enum BlockType como int
    }

    public static void ScheduleMeshJob(
        Vector2Int coord,
        NoiseLayer[] noiseLayersArr,
        WarpLayer[] warpLayersArr,
        NoiseLayer[] caveLayersArr,
        BlockTextureMapping[] blockMappingsArr,
        int baseHeight,
        float globalOffsetX,
        float globalOffsetZ,
        int atlasTilesX,
        int atlasTilesY,
        bool generateSides,
        float seaLevel,
        float caveThreshold,
        int caveStride,
        int maxCaveDepthMultiplier,
        float caveRarityScale,
        float caveRarityThreshold,
        float caveMaskSmoothness,
        NativeArray<BlockEdit> blockEdits,
        NativeArray<TreeInstance> treeInstances,
        int treeMargin,
        int maxTreeRadius,
        int CliffTreshold,
        NativeArray<byte> voxelOutput,
        NativeArray<byte> lightData, // <--- NOVA INJEÇÃO DE DEPENDÊNCIA DE LUZ
        out JobHandle handle,
        out NativeList<Vector3> vertices,
        out NativeList<int> opaqueTriangles,
        out NativeList<int> transparentTriangles,
        out NativeList<int> waterTriangles,
        out NativeList<Vector2> uvs,
        out NativeList<Vector2> uv2,
        out NativeList<Vector3> normals,
        out NativeList<byte> vertexLights,
        out NativeList<byte> tintFlags,
        out NativeList<byte> vertexAO,
        out NativeList<Vector4> extraUVs
    )
    {
        // 1. Fixar o borderSize em 1 (Padrão para Ambient Occlusion e Costura)
        int borderSize = 1;

        // 2. Alocações Iniciais de Configuração
        NativeArray<NoiseLayer> nativeNoiseLayers = new NativeArray<NoiseLayer>(noiseLayersArr, Allocator.TempJob);
        NativeArray<WarpLayer> nativeWarpLayers = new NativeArray<WarpLayer>(warpLayersArr, Allocator.TempJob);
        NativeArray<NoiseLayer> nativeCaveLayers = new NativeArray<NoiseLayer>(caveLayersArr, Allocator.TempJob);
        NativeArray<BlockTextureMapping> nativeBlockMappings = new NativeArray<BlockTextureMapping>(blockMappingsArr, Allocator.TempJob);

        // 3. Alocações das Listas de Mesh (Output)
        vertices = new NativeList<Vector3>(4096, Allocator.TempJob);
        opaqueTriangles = new NativeList<int>(4096 * 3, Allocator.TempJob);
        waterTriangles = new NativeList<int>(4096 * 3, Allocator.TempJob);
        transparentTriangles = new NativeList<int>(4096 * 3, Allocator.TempJob);
        normals = new NativeList<Vector3>(4096, Allocator.TempJob);
        extraUVs = new NativeList<Vector4>(4096 * 4, Allocator.TempJob);
        vertexLights = new NativeList<byte>(4096 * 4, Allocator.TempJob);
        tintFlags = new NativeList<byte>(4096 * 4, Allocator.TempJob);
        vertexAO = new NativeList<byte>(4096 * 4, Allocator.TempJob);
        uvs = new NativeList<Vector2>(4096, Allocator.TempJob);
        uv2 = new NativeList<Vector2>(4096, Allocator.TempJob);

        // 4. Alocações dos Arrays Intermédios que fluem entre os Jobs (TempJob)
        int heightSize = SizeX + 2 * borderSize;
        int totalHeightPoints = heightSize * heightSize;
        int voxelSizeX = SizeX + 2 * borderSize;
        int voxelSizeZ = SizeZ + 2 * borderSize;
        int totalVoxels = voxelSizeX * SizeY * voxelSizeZ;

        NativeArray<int> heightCache = new NativeArray<int>(totalHeightPoints, Allocator.TempJob);
        NativeArray<BlockType> blockTypes = new NativeArray<BlockType>(totalVoxels, Allocator.TempJob);
        NativeArray<bool> solids = new NativeArray<bool>(totalVoxels, Allocator.TempJob);




        // ==========================================
        // JOB 1: Geração de Dados (Terreno)
        // ==========================================
        var dataJob = new ChunkDataJob
        {
            coord = coord,
            noiseLayers = nativeNoiseLayers,
            warpLayers = nativeWarpLayers,
            caveLayers = nativeCaveLayers,
            blockMappings = nativeBlockMappings,
            blockEdits = blockEdits,
            treeInstances = treeInstances,

            baseHeight = baseHeight,
            offsetX = globalOffsetX,
            offsetZ = globalOffsetZ,
            seaLevel = seaLevel,
            caveThreshold = caveThreshold,
            caveStride = caveStride,
            maxCaveDepthMultiplier = maxCaveDepthMultiplier,
            caveRarityScale = caveRarityScale,
            caveRarityThreshold = caveRarityThreshold,
            caveMaskSmoothness = caveMaskSmoothness,
            treeMargin = treeMargin,
            border = borderSize,
            maxTreeRadius = maxTreeRadius,
            CliffTreshold = CliffTreshold,

            heightCache = heightCache,
            blockTypes = blockTypes,
            solids = solids,
            voxelOutput = voxelOutput
        };
        JobHandle dataHandle = dataJob.Schedule(); // Inicia sem dependências






        // ==========================================
        // JOB 2: Geração da Malha (Mesh)
        // ==========================================
        var meshJob = new ChunkMeshJob
        {
            blockTypes = blockTypes,
            solids = solids,
            light = lightData, // Usa a luz previamente calculada e passada por parâmetro
            heightCache = heightCache,
            blockMappings = nativeBlockMappings,

            border = borderSize,
            atlasTilesX = atlasTilesX,
            atlasTilesY = atlasTilesY,
            generateSides = generateSides,

            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            waterTriangles = waterTriangles,
            transparentTriangles = transparentTriangles,
            uvs = uvs,
            uv2 = uv2,
            normals = normals,
            extraUVs = extraUVs,
            vertexLights = vertexLights,
            tintFlags = tintFlags,
            vertexAO = vertexAO
        };
        // O MeshJob agora espera o DataJob terminar diretamente (Iluminação removida da fila)
        handle = meshJob.Schedule(dataHandle);
    }

    // =========================================================================
    // JOB 1: CHUNK DATA JOB (Gera Terreno, Cavernas, Água, Árvores)
    // =========================================================================
    [BurstCompile]
    private struct ChunkDataJob : IJob
    {
        public Vector2Int coord;

        [DeallocateOnJobCompletion][ReadOnly] public NativeArray<NoiseLayer> noiseLayers;
        [DeallocateOnJobCompletion][ReadOnly] public NativeArray<WarpLayer> warpLayers;
        [DeallocateOnJobCompletion][ReadOnly] public NativeArray<NoiseLayer> caveLayers;

        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [ReadOnly] public NativeArray<BlockEdit> blockEdits;
        [ReadOnly] public NativeArray<TreeInstance> treeInstances;

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
        public float caveRarityScale;
        public float caveRarityThreshold;
        public float caveMaskSmoothness;
        public int CliffTreshold;

        public NativeArray<int> heightCache;
        public NativeArray<BlockType> blockTypes;
        public NativeArray<bool> solids;
        [WriteOnly] public NativeArray<byte> voxelOutput;

        public void Execute()
        {
            int heightSize = SizeX + 2 * border;
            int totalHeightPoints = heightSize * heightSize;
            int heightStride = heightSize;

            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;

            // 1. Heightmap
            for (int i = 0; i < totalHeightPoints; i++)
            {
                int lx = i % heightStride;
                int lz = i / heightStride;
                int realLx = lx - border;
                int realLz = lz - border;
                int worldX = coord.x * SizeX + realLx;
                int worldZ = coord.y * SizeZ + realLz;
                heightCache[i] = GetSurfaceHeight(worldX, worldZ);
            }

            // 2. Popular voxels (terreno, cavernas, água)
            PopulateTerrainColumns(heightCache, blockTypes, solids, voxelSizeX, voxelSizeZ);
            GenerateCaves(heightCache, blockTypes, solids);
            FillWaterAboveTerrain(heightCache, blockTypes, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            // 3. Árvores e Edições
            TreePlacement.ApplyTreeInstancesToVoxels(
                blockTypes, solids, blockMappings, treeInstances, coord, border,
                SizeX, SizeZ, SizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize, heightCache, heightStride
            );

            ApplyBlockEditsToVoxels(blockTypes, solids, voxelSizeX, voxelSizeZ);

            // 4. Copiar para voxelOutput COM CAST PARA BYTE
            if (voxelOutput.IsCreated && voxelOutput.Length == SizeX * SizeY * SizeZ)
            {
                int dstIdx = 0;
                for (int y = 0; y < SizeY; y++)
                {
                    for (int z = 0; z < SizeZ; z++)
                    {
                        int srcZOffset = (z + border) * voxelPlaneSize;
                        for (int x = 0; x < SizeX; x++)
                        {
                            int srcIdx = (x + border) + y * voxelSizeX + srcZOffset;
                            voxelOutput[dstIdx++] = (byte)blockTypes[srcIdx];
                        }
                    }
                }
            }
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

        private void PopulateTerrainColumns(NativeArray<int> heightCache, NativeArray<BlockType> blockTypes, NativeArray<bool> solids, int voxelSizeX, int voxelSizeZ)
        {
            int voxelPlaneSize = voxelSizeX * SizeY;
            int heightStride = voxelSizeX;

            for (int lx = -border; lx < SizeX + border; lx++)
            {
                for (int lz = -border; lz < SizeZ + border; lz++)
                {
                    int cacheX = lx + border;
                    int cacheZ = lz + border;
                    int cacheIdx = cacheX + cacheZ * heightStride;
                    int h = heightCache[cacheIdx];
                    bool isBeachArea = (h <= seaLevel + 2);

                    bool isCliff = IsCliff(heightCache, cacheX, cacheZ, heightStride, CliffTreshold);
                    int mountainStoneHeight = baseHeight + 70;
                    bool isHighMountain = h >= mountainStoneHeight;

                    for (int y = 0; y < SizeY; y++)
                    {
                        int voxelIdx = cacheX + y * voxelSizeX + cacheZ * voxelPlaneSize;

                        if (y <= h)
                        {
                            BlockType bt;
                            if (y == h)
                            {
                                if (isHighMountain) bt = BlockType.Stone;
                                else if (isCliff) bt = BlockType.Stone;
                                else bt = isBeachArea ? BlockType.Sand : BlockType.Grass;
                            }
                            else if (y > h - 4)
                            {
                                if (isCliff) bt = BlockType.Stone;
                                else bt = isBeachArea ? BlockType.Sand : BlockType.Dirt;
                            }
                            else if (y <= 2)
                            {
                                bt = BlockType.Bedrock;
                            }
                            else
                            {
                                bt = BlockType.Stone;
                            }
                            blockTypes[voxelIdx] = bt;
                            solids[voxelIdx] = blockMappings[(int)bt].isSolid;
                        }
                        else
                        {
                            blockTypes[voxelIdx] = BlockType.Air;
                            solids[voxelIdx] = false;
                        }
                    }
                }
            }
        }

        private bool IsCliff(NativeArray<int> heightCache, int x, int z, int heightStride, int threshold = 2)
        {
            if (x <= 0 || z <= 0 || x >= heightStride - 1 || z >= heightCache.Length / heightStride - 1)
                return false;

            int centerIdx = x + z * heightStride;
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

        private void GenerateCaves(NativeArray<int> heightCache, NativeArray<BlockType> blockTypes, NativeArray<bool> solids)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;
            int heightStride = SizeX + 2 * border;

            int baseWorldX = coord.x * SizeX;
            int baseWorldZ = coord.y * SizeZ;

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

                                float n1 = MyNoise.OctavePerlin3D(nx, ny, nz, layer) * 2f - 1f;
                                float n2 = MyNoise.OctavePerlin3D(nx + 128.5f, ny + 256.1f, nz + 64.3f, layer) * 2f - 1f;

                                float tubeDistSq = (n1 * n1) + (n2 * n2);
                                float tubeCave = math.max(0f, 1f - (tubeDistSq * 5f));

                                float cheeseCave = MyNoise.OctavePerlin3D(nx, ny, nz, layer);
                                if (layer.redistributionModifier != 1f || layer.exponent != 1f)
                                {
                                    cheeseCave = MyNoise.Redistribution(cheeseCave, layer.redistributionModifier, layer.exponent);
                                }

                                float finalSample = math.max(tubeCave, cheeseCave * 0.45f);

                                totalCave += finalSample * layer.amplitude;
                                sumCaveAmp += math.max(1e-5f, layer.amplitude);
                            }

                            if (sumCaveAmp > 0f) totalCave /= sumCaveAmp;

                            float maskNx = (worldX + offsetX) / caveRarityScale;
                            float maskNy = worldY / caveRarityScale;
                            float maskNz = (worldZ + offsetZ) / caveRarityScale;

                            float maskVal = noise.cnoise(new float3(maskNx, maskNy, maskNz));
                            float maskWeight = math.saturate((maskVal - caveRarityThreshold) * caveMaskSmoothness);

                            totalCave *= maskWeight;

                            int coarseIdx = cx + cy * coarseStrideX + cz * coarsePlaneSize;
                            coarseCaveNoise[coarseIdx] = totalCave;
                        }
                    }
                }

                // Interpolação
                for (int lx = -border; lx < SizeX + border; lx++)
                {
                    for (int lz = -border; lz < SizeZ + border; lz++)
                    {
                        int cacheX = lx + border;
                        int cacheZ = lz + border;
                        int cacheIdx = cacheX + cacheZ * heightStride;
                        int h = heightCache[cacheIdx];

                        int maxCaveY = math.min(SizeY - 1, (int)seaLevel * maxCaveDepthMultiplier);

                        for (int y = 0; y <= maxCaveY; y++)
                        {
                            if (y <= 20) continue; // Protege o Bedrock

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

                            float maxPossibleY = math.max(1f, h);
                            float relativeHeight = (float)y / maxPossibleY;
                            float surfaceBias = 0.001f * relativeHeight;
                            if (y < 5) surfaceBias -= 0.08f;

                            float adjustedThreshold = caveThreshold - surfaceBias;
                            if (interpolatedCave > adjustedThreshold)
                            {
                                blockTypes[voxelIdx] = BlockType.Air;
                                solids[voxelIdx] = false;
                            }
                        }
                    }
                }
                coarseCaveNoise.Dispose();
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

    // =========================================================================
    // JOB 2: CHUNK MESH JOB (Greedy Meshing e Arrays Visuais)
    // =========================================================================
    [BurstCompile]
    private struct ChunkMeshJob : IJob
    {
        // DeallocateOnJobCompletion limpa todos estes arrays criados no Schedule.
        [DeallocateOnJobCompletion][ReadOnly] public NativeArray<int> heightCache;
        [DeallocateOnJobCompletion][ReadOnly] public NativeArray<BlockType> blockTypes;
        [DeallocateOnJobCompletion][ReadOnly] public NativeArray<bool> solids;
        [DeallocateOnJobCompletion][ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;

        // O lightData não tem [DeallocateOnJobCompletion] aqui porque ele é injetado de fora.
        // O World.cs (ou quem chamar ScheduleMeshJob) deve ser responsável por limpar este array.
        [ReadOnly] public NativeArray<byte> light;

        public int border;
        public int atlasTilesX;
        public int atlasTilesY;
        public bool generateSides;

        public NativeList<Vector3> vertices;
        public NativeList<int> opaqueTriangles;
        public NativeList<int> waterTriangles;
        public NativeList<int> transparentTriangles;
        public NativeList<Vector2> uvs;
        public NativeList<Vector2> uv2;
        public NativeList<Vector3> normals;
        public NativeList<Vector4> extraUVs;
        public NativeList<byte> vertexLights;
        public NativeList<byte> tintFlags;
        public NativeList<byte> vertexAO;

        public void Execute()
        {
            float invAtlasTilesX = 1f / atlasTilesX;
            float invAtlasTilesY = 1f / atlasTilesY;

            GenerateMesh(heightCache, blockTypes, solids, light, invAtlasTilesX, invAtlasTilesY);
        }

        private void GenerateMesh(NativeArray<int> heightCache, NativeArray<BlockType> blockTypes, NativeArray<bool> solids, NativeArray<byte> light, float invAtlasTilesX, float invAtlasTilesY)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;

            int maxMask = math.max(voxelSizeX * SizeY, math.max(voxelSizeX * voxelSizeZ, SizeY * voxelSizeZ));
            NativeArray<int> mask = new NativeArray<int>(maxMask, Allocator.Temp);

            for (int axis = 0; axis < 3; axis++)
            {
                for (int side = 0; side < 2; side++)
                {
                    int normalSign = side == 0 ? 1 : -1;

                    int u = (axis + 1) % 3;
                    int v = (axis + 2) % 3;

                    int sizeU = (u == 0 ? voxelSizeX : u == 1 ? SizeY : voxelSizeZ);
                    int sizeV = (v == 0 ? voxelSizeX : v == 1 ? SizeY : voxelSizeZ);
                    int chunkSize = (axis == 0 ? SizeX : axis == 1 ? SizeY : SizeZ);

                    int minN = (axis == 1) ? 0 : border;
                    int maxN = minN + chunkSize;

                    Vector3 normal = new Vector3(axis == 0 ? normalSign : 0, axis == 1 ? normalSign : 0, axis == 2 ? normalSign : 0);
                    BlockFace faceType = axis == 1 ? (normalSign > 0 ? BlockFace.Top : BlockFace.Bottom) : BlockFace.Side;

                    Vector3Int stepU = new Vector3Int(u == 0 ? 1 : 0, u == 1 ? 1 : 0, u == 2 ? 1 : 0);
                    Vector3Int stepV = new Vector3Int(v == 0 ? 1 : 0, v == 1 ? 1 : 0, v == 2 ? 1 : 0);

                    for (int n = minN; n < maxN; n++)
                    {
                        for (int j = 0; j < sizeV; j++)
                        {
                            for (int i = 0; i < sizeU; i++)
                            {
                                int x = (u == 0 ? i : v == 0 ? j : n);
                                int y = (u == 1 ? i : v == 1 ? j : n);
                                int z = (u == 2 ? i : v == 2 ? j : n);

                                bool isCurrentActive = x >= border && x < border + SizeX && z >= border && z < border + SizeZ;
                                if (!isCurrentActive) { mask[i + j * sizeU] = 0; continue; }

                                int idx = x + y * voxelSizeX + z * voxelPlaneSize;
                                BlockType current = blockTypes[idx];

                                if (current == BlockType.Air) { mask[i + j * sizeU] = 0; continue; }

                                int nx = x + (axis == 0 ? normalSign : 0);
                                int ny = y + (axis == 1 ? normalSign : 0);
                                int nz = z + (axis == 2 ? normalSign : 0);

                                bool outside = nx < 0 || nx >= voxelSizeX || ny < 0 || ny >= SizeY || nz < 0 || nz >= voxelSizeZ;

                                bool isVisible = false;
                                if (!blockMappings[(int)current].isSolid)
                                {
                                    isVisible = false;
                                }
                                else if (outside)
                                {
                                    isVisible = true;
                                }
                                else
                                {
                                    int nIdx = nx + ny * voxelSizeX + nz * voxelPlaneSize;
                                    BlockType neighbor = blockTypes[nIdx];
                                    if (blockMappings[(int)neighbor].isEmpty)
                                    {
                                        isVisible = true;
                                    }
                                    else
                                    {
                                        bool neighborOpaque = blockMappings[(int)neighbor].isSolid && !blockMappings[(int)neighbor].isTransparent;
                                        isVisible = !neighborOpaque;
                                    }
                                }

                                if (isVisible)
                                {
                                    byte faceLight = outside ? (byte)15 : light[nx + ny * voxelSizeX + nz * voxelPlaneSize];

                                    int aoPlaneN = n + normalSign;
                                    int ax = (u == 0 ? i : v == 0 ? j : aoPlaneN);
                                    int ay = (u == 1 ? i : v == 1 ? j : aoPlaneN);
                                    int az = (u == 2 ? i : v == 2 ? j : aoPlaneN);
                                    Vector3Int aoBase = new Vector3Int(ax, ay, az);

                                    byte ao0 = GetVertexAO(aoBase, -stepU, -stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    byte ao1 = GetVertexAO(aoBase, stepU, -stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    byte ao2 = GetVertexAO(aoBase, stepU, stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    byte ao3 = GetVertexAO(aoBase, -stepU, stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);

                                    int packedAO = (ao0) | (ao1 << 2) | (ao2 << 4) | (ao3 << 6);
                                    mask[i + j * sizeU] = (int)current | ((int)faceLight << 12) | (packedAO << 16);
                                }
                                else
                                {
                                    mask[i + j * sizeU] = 0;
                                }
                            }
                        }

                        for (int j = 0; j < sizeV; j++)
                        {
                            int i = 0;
                            while (i < sizeU)
                            {
                                int packedData = mask[i + j * sizeU];
                                if (packedData == 0)
                                {
                                    i++;
                                    continue;
                                }

                                int w = 1;
                                while (i + w < sizeU && mask[i + w + j * sizeU] == packedData) w++;

                                int h = 1;
                                while (j + h < sizeV)
                                {
                                    bool canGrow = true;
                                    for (int k = 0; k < w; k++)
                                    {
                                        if (mask[i + k + (j + h) * sizeU] != packedData)
                                        {
                                            canGrow = false;
                                            break;
                                        }
                                    }
                                    if (!canGrow) break;
                                    h++;
                                }

                                BlockType bt = (BlockType)(packedData & 0xFFF);
                                byte finalLight = (byte)((packedData >> 12) & 0xF);

                                int aoPackedData = (packedData >> 16) & 0xFF;
                                byte ao0 = (byte)(aoPackedData & 3);
                                byte ao1 = (byte)((aoPackedData >> 2) & 3);
                                byte ao2 = (byte)((aoPackedData >> 4) & 3);
                                byte ao3 = (byte)((aoPackedData >> 6) & 3);

                                int vIndex = vertices.Length;

                                for (int l = 0; l < 4; l++)
                                {
                                    int du = (l == 1 || l == 2) ? w : 0;
                                    int dv = (l == 2 || l == 3) ? h : 0;

                                    float rawU = i + du;
                                    float rawV = j + dv;
                                    float posD = n + (normalSign > 0 ? 1f : 0f);

                                    float px = (u == 0 ? rawU : v == 0 ? rawV : posD) - border;
                                    float py = (u == 1 ? rawU : v == 1 ? rawV : posD);
                                    float pz = (u == 2 ? rawU : v == 2 ? rawV : posD) - border;

                                    if (bt == BlockType.Water && axis == 1 && normalSign > 0) py -= 0.15f;

                                    vertices.Add(new Vector3(px, py, pz));
                                    normals.Add(normal);

                                    Vector2 uvCoord = axis == 0 ? new Vector2(rawV, rawU) :
                                                      axis == 1 ? new Vector2(rawV, rawU) :
                                                                  new Vector2(rawU, rawV);
                                    uvs.Add(uvCoord);

                                    BlockTextureMapping m = blockMappings[(int)bt];
                                    bool tint = faceType == BlockFace.Top ? m.tintTop :
                                                faceType == BlockFace.Bottom ? m.tintBottom : m.tintSide;

                                    byte currentAO = l == 0 ? ao0 : (l == 1 ? ao1 : (l == 2 ? ao2 : ao3));

                                    float rawLight = finalLight / 15f;
                                    float floatTint = tint ? 1f : 0f;
                                    float floatAO = currentAO / 3f;

                                    extraUVs.Add(new Vector4(rawLight, floatTint, floatAO, 0f));

                                    Vector2Int tile = faceType == BlockFace.Top ? m.top :
                                                      faceType == BlockFace.Bottom ? m.bottom : m.side;

                                    uv2.Add(new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f));
                                }

                                NativeList<int> tris = (bt == BlockType.Water) ? waterTriangles :
                                                       blockMappings[(int)bt].isTransparent ? transparentTriangles : opaqueTriangles;

                                if (normalSign > 0)
                                {
                                    tris.Add(vIndex + 0); tris.Add(vIndex + 1); tris.Add(vIndex + 2);
                                    tris.Add(vIndex + 0); tris.Add(vIndex + 2); tris.Add(vIndex + 3);
                                }
                                else
                                {
                                    tris.Add(vIndex + 0); tris.Add(vIndex + 3); tris.Add(vIndex + 2);
                                    tris.Add(vIndex + 0); tris.Add(vIndex + 2); tris.Add(vIndex + 1);
                                }

                                for (int y0 = 0; y0 < h; y0++)
                                    for (int x0 = 0; x0 < w; x0++)
                                        mask[i + x0 + (j + y0) * sizeU] = 0;

                                i += w;
                            }
                        }
                    }
                }
            }
            mask.Dispose();
        }

        private bool IsOccluder(int x, int y, int z, NativeArray<bool> solids, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            return solids[idx];
        }

        private byte GetVertexAO(Vector3Int pos, Vector3Int d1, Vector3Int d2, NativeArray<bool> solids, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            bool s1 = IsOccluder(pos.x + d1.x, pos.y + d1.y, pos.z + d1.z, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            bool s2 = IsOccluder(pos.x + d2.x, pos.y + d2.y, pos.z + d2.z, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            bool c = IsOccluder(pos.x + d1.x + d2.x, pos.y + d1.y + d2.y, pos.z + d1.z + d2.z, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            if (s1 && s2) return 0;
            return (byte)(3 - (s1 ? 1 : 0) - (s2 ? 1 : 0) - (c ? 1 : 0));
        }
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