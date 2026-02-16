

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

    // EDIT: struct para representar uma edição (posição world + tipo do bloco)
    public struct BlockEdit
    {
        public int x;
        public int y;
        public int z;
        public int type; // enum BlockType como int
    }
    // =============================================
    // NOVO: Job paralelo para gerar o heightmap
    // =============================================
    [BurstCompile]
    private struct HeightJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<NoiseLayer> noiseLayers;
        [ReadOnly] public NativeArray<WarpLayer> warpLayers;

        public Vector2Int coord;
        public int border;
        public float offsetX;
        public float offsetZ;
        public int baseHeight;

        [WriteOnly] public NativeArray<int> heightCache;
        public int heightStride;   // SizeX + 2 * border

        public void Execute(int index)
        {
            int lx = index % heightStride;           // 0 → heightSizeX-1
            int lz = index / heightStride;

            int realLx = lx - border;                // -border → SizeX + border
            int realLz = lz - border;

            int worldX = coord.x * SizeX + realLx;
            int worldZ = coord.y * SizeZ + realLz;

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

            // Altura final
            int h = GetHeightFromNoise(totalNoise, sumAmp, baseHeight);
            heightCache[index] = h;
        }

        private static int GetHeightFromNoise(float noise, float sumAmp, int baseHeight)
        {
            float centered = noise - sumAmp * 0.5f;
            return math.clamp(baseHeight + (int)math.floor(centered), 1, Chunk.SizeY - 1);
        }
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
        // EDIT: receber NativeArray<BlockEdit> com edits locais (pode ter Length==0)
        NativeArray<BlockEdit> blockEdits,
        // NEW: árvores e margem dinâmica
        NativeArray<TreeInstance> treeInstances,
        int treeMargin,
        int borderSize,        // NOVO: tamanho do border
        int maxTreeRadius,
        int CliffTreshold,
    // === NOVO: saída de voxels ===
    NativeArray<byte> voxelOutput,
        out JobHandle handle,
        out NativeList<Vector3> vertices,
        out NativeList<int> opaqueTriangles,
        out NativeList<int> transparentTriangles,
        out NativeList<int> waterTriangles,
        out NativeList<Vector2> uvs,
        out NativeList<Vector2> uv2,
        out NativeList<Vector3> normals,
        out NativeList<byte> vertexLights,  // Novo: adicione isso
        out NativeList<byte> tintFlags

    )
    {
        NativeArray<NoiseLayer> nativeNoiseLayers = new NativeArray<NoiseLayer>(noiseLayersArr, Allocator.TempJob);
        NativeArray<WarpLayer> nativeWarpLayers = new NativeArray<WarpLayer>(warpLayersArr, Allocator.TempJob);
        NativeArray<NoiseLayer> nativeCaveLayers = new NativeArray<NoiseLayer>(caveLayersArr, Allocator.TempJob);
        NativeArray<BlockTextureMapping> nativeBlockMappings = new NativeArray<BlockTextureMapping>(blockMappingsArr, Allocator.TempJob);

        vertices = new NativeList<Vector3>(4096, Allocator.Persistent);
        opaqueTriangles = new NativeList<int>(4096 * 3, Allocator.Persistent);
        waterTriangles = new NativeList<int>(4096 * 3, Allocator.Persistent);
        transparentTriangles = new NativeList<int>(4096 * 3, Allocator.Persistent);
        uvs = new NativeList<Vector2>(4096, Allocator.Persistent);
        normals = new NativeList<Vector3>(4096, Allocator.Persistent);
        vertexLights = new NativeList<byte>(4096 * 4, Allocator.Persistent);  // Novo: aloque aqui (4 verts por face)
        tintFlags = new NativeList<byte>(4096 * 4, Allocator.Persistent);

        // UVs
        uvs = new NativeList<Vector2>(4096, Allocator.Persistent);
        uv2 = new NativeList<Vector2>(4096, Allocator.Persistent); // NOVO: canal 1
        var job = new ChunkMeshJob
        {
            coord = coord,
            noiseLayers = nativeNoiseLayers,
            warpLayers = nativeWarpLayers,

            caveLayers = nativeCaveLayers,
            blockMappings = nativeBlockMappings,
            baseHeight = baseHeight,

            offsetX = globalOffsetX,
            offsetZ = globalOffsetZ,
            atlasTilesX = atlasTilesX,
            atlasTilesY = atlasTilesY,
            generateSides = generateSides,
            seaLevel = seaLevel,
            caveThreshold = caveThreshold,
            caveStride = caveStride,
            maxCaveDepthMultiplier = maxCaveDepthMultiplier,
            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            waterTriangles = waterTriangles,
            transparentTriangles = transparentTriangles,
            uvs = uvs,
            uv2 = uv2, // <- passe para o job
            normals = normals,
            vertexLights = vertexLights,
            tintFlags = tintFlags, // Novo: atribua ao job
            blockEdits = blockEdits,       // EDIT: atribui edits ao job
            treeInstances = treeInstances, // NEW
            treeMargin = treeMargin,       // NEW
            border = borderSize,              // NOVO
            maxTreeRadius = maxTreeRadius,    // NOVO
            CliffTreshold = CliffTreshold,
            // === NOVO ===
            voxelOutput = voxelOutput,


        };

        handle = job.Schedule();
    }

    [BurstCompile]
    private struct ChunkMeshJob : IJob
    {
        public Vector2Int coord;
        [ReadOnly] public NativeArray<NoiseLayer> noiseLayers;
        [ReadOnly] public NativeArray<WarpLayer> warpLayers;
        [ReadOnly] public NativeArray<NoiseLayer> caveLayers;       // ← movido para cá
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [ReadOnly] public NativeArray<BlockEdit> blockEdits; // EDIT: lista de edits aplicáveis ao chunk
        [ReadOnly] public NativeArray<TreeInstance> treeInstances; // NEW
        public int treeMargin; // NEW
        public int border;                    // NOVO
        public int maxTreeRadius;             // NOVO
        public int baseHeight;

        public float offsetX;
        public float offsetZ;
        public int atlasTilesX;
        public int atlasTilesY;
        public bool generateSides;
        public float seaLevel;
        public float caveThreshold;
        public int caveStride;
        public int maxCaveDepthMultiplier;

        public int CliffTreshold;

        public NativeList<Vector3> vertices;
        public NativeList<int> opaqueTriangles;
        public NativeList<int> waterTriangles;
        public NativeList<int> transparentTriangles;

        public NativeList<Vector2> uvs;
        public NativeList<Vector2> uv2; // UV channel 1: tile base (uMin, vMin) normalizado
        public NativeList<Vector3> normals;
        public NativeList<byte> vertexLights; // 0..15 por vértice
        public NativeList<byte> tintFlags;  // NOVO: (WriteOnly implícito via Add)

        // === NOVO ===
        [WriteOnly] public NativeArray<byte> voxelOutput;
        public void Execute()
        {
            int heightSize = SizeX + 2 * border;
            int totalHeightPoints = heightSize * heightSize;

            NativeArray<int> heightCache = new NativeArray<int>(totalHeightPoints, Allocator.Temp);

            var heightJob = new HeightJob
            {
                noiseLayers = noiseLayers,
                warpLayers = warpLayers,
                coord = coord,
                border = border,
                offsetX = offsetX,
                offsetZ = offsetZ,
                baseHeight = baseHeight,
                heightCache = heightCache,
                heightStride = heightSize
            };

            // Executa sequencialmente dentro do mesmo Job (sem agendamento)
            for (int i = 0; i < totalHeightPoints; i++)
            {
                heightJob.Execute(i);
            }

            // Passo 2: Popular voxels (blockTypes e solids, flattened)
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int totalVoxels = voxelSizeX * SizeY * voxelSizeZ;
            int voxelPlaneSize = voxelSizeX * SizeY;

            NativeArray<BlockType> blockTypes = new NativeArray<BlockType>(totalVoxels, Allocator.Temp);
            NativeArray<bool> solids = new NativeArray<bool>(totalVoxels, Allocator.Temp);

            PopulateTerrainColumns(heightCache, blockTypes, solids, voxelSizeX, voxelSizeZ);

            GenerateCaves(heightCache, blockTypes, solids);

            FillWaterAboveTerrain(
                heightCache,
                blockTypes,
                solids,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize
            );
            int heightStride = voxelSizeX;


            TreePlacement.ApplyTreeInstancesToVoxels(
                blockTypes,
                solids,
                blockMappings,
                treeInstances,
                coord,
                border,           // o mesmo border que você usa
                SizeX,            // 16 normalmente
                SizeZ,            // 16 normalmente
                SizeY,            // 256 normalmente
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                heightCache,  // NOVO: passe heightCache
                heightStride  // NOVO: passe stride
            );

            // EDIT: aplicar edits vindos do World (substitui blocos na posição world)
            ApplyBlockEditsToVoxels(blockTypes, solids, voxelSizeX, voxelSizeZ);

            // === NOVO: copiar para voxelOutput COM CAST PARA BYTE ===
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


            // depois:
            NativeArray<byte> light = new NativeArray<byte>(totalVoxels, Allocator.Temp);
            LightingCalculator.CalculateLighting(blockTypes, solids, light, blockMappings, voxelSizeX, voxelSizeZ, totalVoxels, voxelPlaneSize, SizeY);

            // Passo 3: Gerar mesh (ao adicionar vértices guardamos o valor de luz por vértice)
            GenerateMesh(heightCache, blockTypes, solids, light);

            // Limpeza
            light.Dispose();
            heightCache.Dispose();
            blockTypes.Dispose();
            solids.Dispose();
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
                int localX = e.x - baseWorldX; // 0..15 expected
                int localZ = e.z - baseWorldZ;
                int y = e.y;

                int internalX = localX + border;
                int internalZ = localZ + border;

                if (internalX >= 0 && internalX < voxelSizeX && y >= 0 && y < SizeY && internalZ >= 0 && internalZ < voxelSizeZ)
                {
                    int idx = internalX + y * voxelSizeX + internalZ * voxelPlaneSize;
                    BlockType bt = (BlockType)math.clamp(e.type, 0, 255);
                    blockTypes[idx] = bt;
                    // atualizar solid flag a partir do mapping
                    BlockTextureMapping mapping = blockMappings[(int)bt];
                    solids[idx] = mapping.isSolid;
                }
            }
        }

        private void PopulateTerrainColumns(NativeArray<int> heightCache, NativeArray<BlockType> blockTypes, NativeArray<bool> solids, int voxelSizeX, int voxelSizeZ)
        {

            int voxelPlaneSize = voxelSizeX * SizeY;
            int heightStride = voxelSizeX;

            // Preencher sólidos iniciais e ar acima
            for (int lx = -border; lx < SizeX + border; lx++)
            {
                for (int lz = -border; lz < SizeZ + border; lz++)
                {
                    int cacheX = lx + border;
                    int cacheZ = lz + border;
                    int cacheIdx = cacheX + cacheZ * heightStride;
                    int h = heightCache[cacheIdx];
                    bool isBeachArea = (h <= seaLevel + 2);

                    // 🔥 NOVO: detectar cliff
                    bool isCliff = IsCliff(heightCache, cacheX, cacheZ, heightStride, CliffTreshold);
                    int mountainStoneHeight = baseHeight + 70; // ajuste como quiser
                    bool isHighMountain = h >= mountainStoneHeight;




                    for (int y = 0; y < SizeY; y++)
                    {
                        int voxelIdx = cacheX + y * voxelSizeX + cacheZ * voxelPlaneSize;

                        if (y <= h)
                        {
                            BlockType bt;
                            if (y == h)
                            {
                                if (isHighMountain)
                                {
                                    bt = BlockType.Stone; // ⛰️ topo de montanha alta
                                }
                                else if (isCliff)
                                {
                                    bt = BlockType.Stone;
                                }
                                else
                                {
                                    bt = isBeachArea ? BlockType.Sand : BlockType.Grass;
                                }

                            }
                            else if (y > h - 4)
                            {
                                if (isCliff)
                                    bt = BlockType.Stone;        // 👈 cliff wall
                                else
                                    bt = isBeachArea ? BlockType.Sand : BlockType.Dirt;
                            }

                            else if (y <= 2)
                            {
                                bt = BlockType.Bedrock;
                            }
                            else if (y > h - 50)
                            {
                                bt = BlockType.Stone;
                            }
                            else
                            {
                                bt = BlockType.Deepslate;
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


        private bool IsCliff(
            NativeArray<int> heightCache,
            int x,
            int z,
            int heightStride,
            int threshold = 2
        )
        {
            // 🔒 proteção de borda
            if (x <= 0 || z <= 0 || x >= heightStride - 1 || z >= heightCache.Length / heightStride - 1)
                return false;

            int h = heightCache[x + z * heightStride];

            int hN = heightCache[x + (z + 1) * heightStride];
            int hS = heightCache[x + (z - 1) * heightStride];
            int hE = heightCache[(x + 1) + z * heightStride];
            int hW = heightCache[(x - 1) + z * heightStride];

            int maxDiff = 0;
            maxDiff = math.max(maxDiff, math.abs(h - hN));
            maxDiff = math.max(maxDiff, math.abs(h - hS));
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

                                float sample = MyNoise.OctavePerlin3D(nx, ny, nz, layer);
                                if (layer.redistributionModifier != 1f || layer.exponent != 1f)
                                {
                                    sample = MyNoise.Redistribution(sample, layer.redistributionModifier, layer.exponent);
                                }
                                totalCave += sample * layer.amplitude;
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
                        int cacheIdx = cacheX + cacheZ * heightStride;
                        int h = heightCache[cacheIdx];

                        int maxCaveY = math.min(SizeY - 1, (int)seaLevel * maxCaveDepthMultiplier);

                        for (int y = 0; y <= maxCaveY; y++)
                        {
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
        private void FillWaterAboveTerrain(
            NativeArray<int> heightCache,
            NativeArray<BlockType> blockTypes,
            NativeArray<bool> solids,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
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

        //     private void GenerateMesh(
        //   NativeArray<int> heightCache,
        //   NativeArray<BlockType> blockTypes,
        //   NativeArray<bool> solids,
        //   NativeArray<byte> light)
        //     {
        //         int voxelSizeX = SizeX + 2 * border;
        //         int voxelSizeZ = SizeZ + 2 * border;
        //         int voxelPlaneSize = voxelSizeX * SizeY;

        //         int maxMask = math.max(voxelSizeX * SizeY,
        //                         math.max(voxelSizeX * voxelSizeZ, SizeY * voxelSizeZ));

        //         NativeArray<int> mask = new NativeArray<int>(maxMask, Allocator.Temp);

        //         for (int axis = 0; axis < 3; axis++)
        //         {
        //             for (int side = 0; side < 2; side++)
        //             {
        //                 int normalSign = side == 0 ? 1 : -1;

        //                 int u = (axis + 1) % 3;
        //                 int v = (axis + 2) % 3;

        //                 int sizeU = (u == 0 ? voxelSizeX : u == 1 ? SizeY : voxelSizeZ);
        //                 int sizeV = (v == 0 ? voxelSizeX : v == 1 ? SizeY : voxelSizeZ);
        //                 // int sizeD não é necessário para o loop, usado implicitamente

        //                 int chunkSize = (axis == 0 ? SizeX : axis == 1 ? SizeY : SizeZ);

        //                 // Define o intervalo de N (profundidade) restrito ao chunk ativo
        //                 int minN = (axis == 1) ? 0 : border;
        //                 int maxN = minN + chunkSize;

        //                 Vector3 normal = new Vector3(
        //                     axis == 0 ? normalSign : 0,
        //                     axis == 1 ? normalSign : 0,
        //                     axis == 2 ? normalSign : 0
        //                 );

        //                 BlockFace faceType =
        //                     axis == 1
        //                     ? (normalSign > 0 ? BlockFace.Top : BlockFace.Bottom)
        //                     : BlockFace.Side;

        //                 for (int n = minN; n < maxN; n++)
        //                 {
        //                     // ================= BUILD MASK =================
        //                     for (int j = 0; j < sizeV; j++)
        //                     {
        //                         for (int i = 0; i < sizeU; i++)
        //                         {
        //                             int x = (u == 0 ? i : v == 0 ? j : n);
        //                             int y = (u == 1 ? i : v == 1 ? j : n);
        //                             int z = (u == 2 ? i : v == 2 ? j : n);

        //                             // --- FIX 1: Ignorar voxels que são apenas dados de borda (padding) ---
        //                             // Nós não queremos gerar malha para eles, eles servem apenas de vizinhos.
        //                             bool isCurrentActive =
        //                                 x >= border && x < border + SizeX &&
        //                                 z >= border && z < border + SizeZ;
        //                             // y sempre é ativo pois não tem border vertical no seu setup atual

        //                             if (!isCurrentActive)
        //                             {
        //                                 mask[i + j * sizeU] = 0;
        //                                 continue;
        //                             }
        //                             // --------------------------------------------------------------------

        //                             int idx = x + y * voxelSizeX + z * voxelPlaneSize;
        //                             BlockType current = blockTypes[idx];

        //                             if (current == BlockType.Air)
        //                             {
        //                                 mask[i + j * sizeU] = 0;
        //                                 continue;
        //                             }

        //                             int dx = axis == 0 ? normalSign : 0;
        //                             int dy = axis == 1 ? normalSign : 0;
        //                             int dz = axis == 2 ? normalSign : 0;

        //                             int nx = x + dx;
        //                             int ny = y + dy;
        //                             int nz = z + dz;

        //                             // --- FIX 2: Ler corretamente o vizinho na área de borda ---
        //                             // Antes verificava se estava fora do interior (SizeX), agora verifica se
        //                             // está fora do ARRAY DE DADOS (voxelSizeX).
        //                             bool outsideData =
        //                                 nx < 0 || nx >= voxelSizeX ||
        //                                 nz < 0 || nz >= voxelSizeZ ||
        //                                 ny < 0 || ny >= SizeY;

        //                             BlockType neighbor;

        //                             if (outsideData)
        //                             {
        //                                 // Se saiu totalmente do array (ex: topo do mundo ou erro de logica), trata como Ar ou Current
        //                                 // Para bordas de chunk, isso quase nunca acontece pois temos o padding.
        //                                 neighbor = BlockType.Air;
        //                             }
        //                             else
        //                             {
        //                                 // Lê o dado do vizinho, MESMO que seja um voxel de borda (ghost)
        //                                 neighbor = blockTypes[nx + ny * voxelSizeX + nz * voxelPlaneSize];
        //                             }

        //                             BlockTextureMapping curMap = blockMappings[(int)current];
        //                             BlockTextureMapping nbMap = blockMappings[(int)neighbor];

        //                             bool neighborSolid = true;

        //                             if (curMap.isTransparent && nbMap.isTransparent)
        //                                 neighborSolid = true;
        //                             else if (curMap.isEmpty && nbMap.isEmpty)
        //                                 neighborSolid = true;
        //                             else if (!curMap.isEmpty && nbMap.isEmpty)
        //                                 neighborSolid = false;
        //                             else
        //                                 neighborSolid = nbMap.isSolid && !nbMap.isTransparent;

        //                             mask[i + j * sizeU] = (!neighborSolid) ? (int)current : 0;
        //                         }
        //                     }

        //                     // ================= GREEDY =================
        //                     for (int j = 0; j < sizeV; j++)
        //                     {
        //                         int i = 0;
        //                         while (i < sizeU)
        //                         {
        //                             int type = mask[i + j * sizeU];
        //                             if (type == 0)
        //                             {
        //                                 i++;
        //                                 continue;
        //                             }

        //                             int w = 1;
        //                             while (i + w < sizeU &&
        //                                     mask[i + w + j * sizeU] == type)
        //                                 w++;

        //                             int h = 1;
        //                             while (j + h < sizeV)
        //                             {
        //                                 bool can = true;
        //                                 for (int k = 0; k < w; k++)
        //                                 {
        //                                     if (mask[i + k + (j + h) * sizeU] != type)
        //                                     {
        //                                         can = false;
        //                                         break;
        //                                     }
        //                                 }
        //                                 if (!can) break;
        //                                 h++;
        //                             }

        //                             BlockType bt = (BlockType)type;
        //                             int vIndex = vertices.Length;

        //                             for (int l = 0; l < 4; l++)
        //                             {
        //                                 int du = (l == 1 || l == 2) ? w : 0;
        //                                 int dv = (l == 2 || l == 3) ? h : 0;

        //                                 float posU = i + du;
        //                                 float posV = j + dv;
        //                                 float posD = n + (normalSign > 0 ? 1f : 0f);

        //                                 // Ajustar posição subtraindo a borda para voltar ao espaço local correto
        //                                 float px = (u == 0 ? posU : v == 0 ? posV : posD) - border;
        //                                 float py = (u == 1 ? posU : v == 1 ? posV : posD);
        //                                 float pz = (u == 2 ? posU : v == 2 ? posV : posD) - border;

        //                                 Vector3 vert = new Vector3(px, py, pz);

        //                                 if (bt == BlockType.Water)
        //                                 {
        //                                     if (axis == 1 && normalSign > 0)
        //                                         vert.y -= 0.15f;
        //                                 }

        //                                 vertices.Add(vert);

        //                                 int sampleY = math.clamp((int)py, 0, SizeY - 1);
        //                                 int sx = (int)math.floor(px + border);
        //                                 int sz = (int)math.floor(pz + border);

        //                                 int sum = 0;
        //                                 int count = 0;

        //                                 void Sample(int sx0, int sy0, int sz0)
        //                                 {
        //                                     if (sx0 >= 0 && sx0 < voxelSizeX &&
        //                                         sy0 >= 0 && sy0 < SizeY &&
        //                                         sz0 >= 0 && sz0 < voxelSizeZ)
        //                                     {
        //                                         sum += light[sx0 + sy0 * voxelSizeX + sz0 * voxelPlaneSize];
        //                                         count++;
        //                                     }
        //                                 }

        //                                 Sample(sx, sampleY, sz);
        //                                 Sample(sx - 1, sampleY, sz);
        //                                 Sample(sx, sampleY, sz - 1);
        //                                 Sample(sx - 1, sampleY, sz - 1);

        //                                 vertexLights.Add(count > 0 ? (byte)(sum / count) : (byte)0);
        //                             }

        //                             bool tint =
        //                                 (bt == BlockType.Leaves) ||
        //                                 (bt == BlockType.Grass && axis == 1 && normalSign > 0);

        //                             byte tintByte = tint ? (byte)1 : (byte)0;
        //                             tintFlags.Add(tintByte);
        //                             tintFlags.Add(tintByte);
        //                             tintFlags.Add(tintByte);
        //                             tintFlags.Add(tintByte);

        //                             BlockTextureMapping m = blockMappings[(int)bt];
        //                             Vector2Int tile =
        //                                 faceType == BlockFace.Top ? m.top :
        //                                 faceType == BlockFace.Bottom ? m.bottom :
        //                                 m.side;

        //                             float tileW = 1f / atlasTilesX;
        //                             float tileH = 1f / atlasTilesY;
        //                             float padU = tileW * 0.01f;
        //                             float padV = tileH * 0.01f;

        //                             float uMin = tile.x * tileW + padU;
        //                             float vMin = tile.y * tileH + padV;

        //                             // for (int l = 0; l < 4; l++)
        //                             // {
        //                             //     int du = (l == 1 || l == 2) ? w : 0;
        //                             //     int dv = (l == 2 || l == 3) ? h : 0;

        //                             //     float uvU = i + du;
        //                             //     float uvV = j + dv;

        //                             //     // UVs precisam escalar com o tamanho do greedy mesh (w, h) se for tiled, 
        //                             //     // mas para Atlas geralmente usamos as coordenadas do vertice ou UV tileada.
        //                             //     // Aqui mantendo sua logica original:
        //                             //     uvs.Add(new Vector2(uvU, uvV));
        //                             //     uv2.Add(new Vector2(uMin, vMin));
        //                             //     normals.Add(normal);
        //                             // }

        //                             for (int l = 0; l < 4; l++)
        //                             {
        //                                 int du = (l == 1 || l == 2) ? w : 0;
        //                                 int dv = (l == 2 || l == 3) ? h : 0;

        //                                 float rawU = i + du;
        //                                 float rawV = j + dv;

        //                                 // --- CORREÇÃO DE ROTAÇÃO DE UV ---
        //                                 Vector2 finalUV;

        //                                 if (axis == 0)
        //                                 {
        //                                     // Eixo X (Faces laterais Leste/Oeste)
        //                                     // O algoritmo definiu u=Y (rawU) e v=Z (rawV).
        //                                     // Precisamos inverter para (Z, Y) para que Y seja a altura (V da textura).
        //                                     finalUV = new Vector2(rawV, rawU);
        //                                 }
        //                                 else if (axis == 1)
        //                                 {
        //                                     // Eixo Y (Topo/Baixo)
        //                                     // O algoritmo definiu u=Z (rawU) e v=X (rawV).
        //                                     // Geralmente queremos (X, Z). Invertemos.
        //                                     finalUV = new Vector2(rawV, rawU);
        //                                 }
        //                                 else // axis == 2
        //                                 {
        //                                     // Eixo Z (Faces laterais Norte/Sul)
        //                                     // O algoritmo definiu u=X (rawU) e v=Y (rawV).
        //                                     // X é horizontal e Y é vertical. Está correto, mantém (X, Y).
        //                                     finalUV = new Vector2(rawU, rawV);
        //                                 }

        //                                 uvs.Add(finalUV);

        //                                 // O Resto continua igual
        //                                 uv2.Add(new Vector2(uMin, vMin));
        //                                 normals.Add(normal);
        //                             }

        //                             NativeList<int> tris =
        //                                 (bt == BlockType.Water) ? waterTriangles :
        //                                 blockMappings[(int)bt].isTransparent ? transparentTriangles :
        //                                 opaqueTriangles;

        //                             if (normalSign > 0)
        //                             {
        //                                 tris.Add(vIndex + 0);
        //                                 tris.Add(vIndex + 1);
        //                                 tris.Add(vIndex + 2);
        //                                 tris.Add(vIndex + 0);
        //                                 tris.Add(vIndex + 2);
        //                                 tris.Add(vIndex + 3);
        //                             }
        //                             else
        //                             {
        //                                 tris.Add(vIndex + 0);
        //                                 tris.Add(vIndex + 3);
        //                                 tris.Add(vIndex + 2);
        //                                 tris.Add(vIndex + 0);
        //                                 tris.Add(vIndex + 2);
        //                                 tris.Add(vIndex + 1);
        //                             }

        //                             for (int y0 = 0; y0 < h; y0++)
        //                                 for (int x0 = 0; x0 < w; x0++)
        //                                     mask[i + x0 + (j + y0) * sizeU] = 0;

        //                             i += w;
        //                         }
        //                     }
        //                 }
        //             }
        //         }

        //         mask.Dispose();
        //     }




        private void GenerateMesh(
            NativeArray<int> heightCache,
            NativeArray<BlockType> blockTypes,
            NativeArray<bool> solids,
            NativeArray<byte> light)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;

            // A máscara armazena o BlockType e o nível de luz empacotados
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

                    for (int n = minN; n < maxN; n++)
                    {
                        // --- CONSTRUÇÃO DA MÁSCARA ---
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

                                BlockType neighbor = outside ? BlockType.Air : blockTypes[nx + ny * voxelSizeX + nz * voxelPlaneSize];
                                byte faceLight = outside ? (byte)15 : light[nx + ny * voxelSizeX + nz * voxelPlaneSize];

                                bool isVisible = false;
                                if (!blockMappings[(int)current].isSolid) isVisible = false;
                                else if (blockMappings[(int)neighbor].isEmpty) isVisible = true;
                                else if (blockMappings[(int)neighbor].isTransparent && !blockMappings[(int)current].isTransparent) isVisible = true;
                                else isVisible = !blockMappings[(int)neighbor].isSolid;

                                if (isVisible)
                                {
                                    // Empacota ID do Bloco (12 bits) + Luz (4 bits)
                                    mask[i + j * sizeU] = (int)current | ((int)faceLight << 12);
                                }
                                else mask[i + j * sizeU] = 0;
                            }
                        }

                        // --- ALGORITMO GREEDY MESH ---
                        for (int j = 0; j < sizeV; j++)
                        {
                            int i = 0;
                            while (i < sizeU)
                            {
                                int packedData = mask[i + j * sizeU];
                                if (packedData == 0) { i++; continue; }

                                int w = 1;
                                while (i + w < sizeU && mask[i + w + j * sizeU] == packedData) w++;

                                int h = 1;
                                while (j + h < sizeV)
                                {
                                    bool canGrow = true;
                                    for (int k = 0; k < w; k++)
                                    {
                                        if (mask[i + k + (j + h) * sizeU] != packedData) { canGrow = false; break; }
                                    }
                                    if (!canGrow) break;
                                    h++;
                                }

                                BlockType bt = (BlockType)(packedData & 0xFFF);
                                byte finalLight = (byte)(packedData >> 12);
                                int vIndex = vertices.Length;

                                // Adiciona os 4 vértices da face
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
                                    vertexLights.Add(finalLight);
                                    normals.Add(normal);

                                    // --- CORREÇÃO DE ROTAÇÃO UV ---
                                    Vector2 uvCoord;
                                    if (axis == 0) uvCoord = new Vector2(rawV, rawU);      // Lateral X (Z, Y)
                                    else if (axis == 1) uvCoord = new Vector2(rawV, rawU); // Topo/Baixo (X, Z)
                                    else uvCoord = new Vector2(rawU, rawV);                // Lateral Z (X, Y)
                                    uvs.Add(uvCoord);

                                    // Tinting e Atlas UV2
                                    bool tint = (bt == BlockType.Leaves) || (bt == BlockType.Grass && axis == 1 && normalSign > 0);
                                    tintFlags.Add(tint ? (byte)1 : (byte)0);

                                    BlockTextureMapping m = blockMappings[(int)bt];
                                    Vector2Int tile = faceType == BlockFace.Top ? m.top : faceType == BlockFace.Bottom ? m.bottom : m.side;
                                    uv2.Add(new Vector2(tile.x * (1f / atlasTilesX) + 0.00f, tile.y * (1f / atlasTilesY) + 0.00f));
                                }

                                // Triângulos
                                NativeList<int> tris = (bt == BlockType.Water) ? waterTriangles : blockMappings[(int)bt].isTransparent ? transparentTriangles : opaqueTriangles;
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

                                // Limpa a área processada na máscara
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





        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            int r = a % b;
            if (r != 0 && ((a < 0 && b > 0) || (a > 0 && b < 0))) q--;
            return q;
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
