using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public static class MeshGenerator
{
    private const int SizeX = 16;
    private const int SizeY = 256;
    private const int SizeZ = 16;

    // Subchunk config
    private const int SubChunkSize = 16; // 16 high -> 256/16 = 16 subchunks
    public const int SubChunkCountY = SizeY / SubChunkSize;

    // Ajuste de border: aumentamos para 2 para reduzir seams quando chunks vizinhos ainda não existem.
    private const int Border = 2;

    public static void ScheduleMeshJob(
        Vector2Int coord,
        NoiseLayer[] noiseLayersArr,
        WarpLayer[] warpLayersArr,
        NoiseLayer[] caveLayersArr,
        BlockTextureMapping[] blockMappingsArr,
        int baseHeight,
        int heightVariation,
        float globalOffsetX,
        float globalOffsetZ,
        int atlasTilesX,
        int atlasTilesY,
        bool generateSides,
        float seaLevel,
        float caveThreshold,
        int caveStride,
        int maxCaveDepthMultiplier,
        out JobHandle handle,
        out NativeList<Vector3> vertices,
        out NativeList<int> opaqueTriangles,
        out NativeList<int> waterTriangles,
        out NativeList<Vector2> uvs,
        out NativeList<Vector3> normals,
        out NativeList<byte> vertexLights,  // Novo: saída com luz por vértice
        out NativeList<byte> vertexSubchunkIds, // Novo: para identificar a qual subchunk cada vértice pertence
        out NativeArray<int> surfaceSubY,
        out NativeArray<byte> voxelBytes
    )
    {
        NativeArray<NoiseLayer> nativeNoiseLayers = new NativeArray<NoiseLayer>(noiseLayersArr, Allocator.TempJob);
        NativeArray<WarpLayer> nativeWarpLayers = new NativeArray<WarpLayer>(warpLayersArr, Allocator.TempJob);
        NativeArray<NoiseLayer> nativeCaveLayers = new NativeArray<NoiseLayer>(caveLayersArr, Allocator.TempJob);
        NativeArray<BlockTextureMapping> nativeBlockMappings = new NativeArray<BlockTextureMapping>(blockMappingsArr, Allocator.TempJob);

        vertices = new NativeList<Vector3>(4096, Allocator.Persistent);
        opaqueTriangles = new NativeList<int>(4096 * 3, Allocator.Persistent);
        waterTriangles = new NativeList<int>(4096 * 3, Allocator.Persistent);
        uvs = new NativeList<Vector2>(4096, Allocator.Persistent);
        normals = new NativeList<Vector3>(4096, Allocator.Persistent);
        vertexLights = new NativeList<byte>(4096 * 4, Allocator.Persistent);  // 4 vértices por face
        vertexSubchunkIds = new NativeList<byte>(4096 * 4, Allocator.Persistent); // 4 ids por face
        surfaceSubY = new NativeArray<int>(1, Allocator.Persistent);
        // 🔥 AQUI ESTÁ O PONTO-CHAVE PARA OS COLLIDERS
        voxelBytes = new NativeArray<byte>(SizeX * SizeY * SizeZ, Allocator.Persistent);
        var job = new ChunkMeshJob
        {
            coord = coord,
            noiseLayers = nativeNoiseLayers,
            warpLayers = nativeWarpLayers,
            caveLayers = nativeCaveLayers,
            blockMappings = nativeBlockMappings,
            baseHeight = baseHeight,
            heightVariation = heightVariation,
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
            uvs = uvs,
            normals = normals,
            vertexLights = vertexLights,  // atribui ao job
            vertexSubchunkIds = vertexSubchunkIds,
            surfaceSubY = surfaceSubY,
            voxelBytes = voxelBytes
        };

        handle = job.Schedule();
    }

    [BurstCompile]
    private struct ChunkMeshJob : IJob
    {
        public Vector2Int coord;
        [ReadOnly] public NativeArray<NoiseLayer> noiseLayers;
        [ReadOnly] public NativeArray<WarpLayer> warpLayers;
        [ReadOnly] public NativeArray<NoiseLayer> caveLayers;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;

        public int baseHeight;
        public int heightVariation;
        public float offsetX;
        public float offsetZ;
        public int atlasTilesX;
        public int atlasTilesY;
        public bool generateSides;
        public float seaLevel;
        public float caveThreshold;
        public int caveStride;
        public int maxCaveDepthMultiplier;

        public NativeList<Vector3> vertices;
        public NativeList<int> opaqueTriangles;
        public NativeList<int> waterTriangles;
        public NativeList<Vector2> uvs;
        public NativeList<Vector3> normals;
        public NativeList<byte> vertexLights; // 0..15 por vértice
        public NativeList<byte> vertexSubchunkIds; // 0..15 por vértice
        public NativeArray<int> surfaceSubY;
        public NativeArray<byte> voxelBytes;
        public void Execute()
        {
            // Passo 1: Gerar heightCache (flattened) — agora cobrimos o padding (Border)
            NativeArray<int> heightCache = GenerateHeightCache();
            CalculateSurfaceSubY(heightCache); // 👈 AQUI
            // Passo 2: Popular voxels (blockTypes e solids, flattened)
            const int border = Border;
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int planeSize = voxelSizeX * SizeY;
            int totalVoxels = voxelSizeX * SizeY * voxelSizeZ;

            NativeArray<BlockType> blockTypes = new NativeArray<BlockType>(totalVoxels, Allocator.Temp);
            NativeArray<bool> solids = new NativeArray<bool>(totalVoxels, Allocator.Temp);
            PopulateVoxels(heightCache, blockTypes, solids);

            // Passo 2.5: Calcular skylight (vertical fill + BFS propagation)
            NativeArray<byte> sunlight = new NativeArray<byte>(totalVoxels, Allocator.Temp);
            CalculateSkylight(blockTypes, solids, sunlight, voxelSizeX, voxelSizeZ);

            // Passo 3: Gerar mesh (ao adicionar vértices guardamos o valor de luz por vértice)
            GenerateMesh(heightCache, blockTypes, solids, sunlight);
            for (int x = 0; x < SizeX; x++)
            {
                for (int z = 0; z < SizeZ; z++)
                {
                    for (int y = 0; y < SizeY; y++)
                    {
                        int src = (x + border) + y * voxelSizeX + (z + border) * planeSize;
                        int dst = x + y * SizeX + z * (SizeX * SizeY);
                        voxelBytes[dst] = (byte)blockTypes[src];
                    }
                }
            }
            // Limpeza
            sunlight.Dispose();
            heightCache.Dispose();
            blockTypes.Dispose();
            solids.Dispose();
        }
        private void CalculateSurfaceSubY(NativeArray<int> heightCache)
        {
            const int border = Border;
            int heightStride = SizeX + 2 * border;

            int maxSurfaceY = 0;

            // ⚠️ IMPORTANTÍSSIMO:
            // percorre SOMENTE a área útil do chunk (sem o border)
            for (int x = 0; x < SizeX; x++)
            {
                for (int z = 0; z < SizeZ; z++)
                {
                    int idx =
                        (x + border) +
                        (z + border) * heightStride;

                    int h = heightCache[idx];
                    if (h > maxSurfaceY)
                        maxSurfaceY = h;
                }
            }

            int subY = maxSurfaceY / SubChunkSize;
            subY = math.clamp(subY, 0, SubChunkCountY - 1);

            surfaceSubY[0] = subY;
        }

        private NativeArray<int> GenerateHeightCache()
        {
            const int border = Border;
            int heightSizeX = SizeX + 2 * border;
            int heightSizeZ = SizeZ + 2 * border;
            NativeArray<int> heightCache = new NativeArray<int>(heightSizeX * heightSizeZ, Allocator.Temp);
            int heightStride = heightSizeX;

            int baseWorldX = coord.x * SizeX;
            int baseWorldZ = coord.y * SizeZ;

            // Looping de lx/lz cobrindo o padding -border .. SizeX+border-1
            for (int lx = -border; lx <= SizeX + border - 1; lx++)
            {
                for (int lz = -border; lz <= SizeZ + border - 1; lz++)
                {
                    int cacheIdx = (lx + border) + (lz + border) * heightStride;
                    int worldX = baseWorldX + lx;
                    int worldZ = baseWorldZ + lz;

                    // Compute domain warping
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
                        warpX += sampleX * layer.amplitude;

                        float sampleZ = MyNoise.OctavePerlin(baseNx, baseNz + 100f, layer);
                        warpZ += sampleZ * layer.amplitude;

                        sumWarpAmp += math.max(1e-5f, layer.amplitude);
                    }

                    if (sumWarpAmp > 0f)
                    {
                        warpX /= sumWarpAmp;
                        warpZ /= sumWarpAmp;
                    }
                    warpX = (warpX - 0.5f) * 2f;
                    warpZ = (warpZ - 0.5f) * 2f;

                    // Compute total noise
                    float totalNoise = 0f;
                    float sumAmp = 0f;

                    for (int i = 0; i < noiseLayers.Length; i++)
                    {
                        var layer = noiseLayers[i];
                        if (!layer.enabled) continue;

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

                    if (sumAmp > 0f) totalNoise /= sumAmp;
                    else
                    {
                        float nx = (worldX + warpX) * 0.05f + offsetX;
                        float nz = (worldZ + warpZ) * 0.05f + offsetZ;
                        totalNoise = noise.cnoise(new float2(nx, nz)) * 0.5f + 0.5f;
                    }

                    heightCache[cacheIdx] = GetHeightFromNoise(totalNoise);
                }
            }

            return heightCache;
        }

        private void PopulateVoxels(NativeArray<int> heightCache, NativeArray<BlockType> blockTypes, NativeArray<bool> solids)
        {
            const int border = Border;
            const int voxelSizeX = SizeX + 2 * border;
            const int voxelSizeZ = SizeZ + 2 * border;
            const int voxelPlaneSize = voxelSizeX * SizeY;
            int heightStride = SizeX + 2 * border;

            int baseWorldX = coord.x * SizeX;
            int baseWorldZ = coord.y * SizeZ;

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

                    for (int y = 0; y < SizeY; y++)
                    {
                        int voxelIdx = cacheX + y * voxelSizeX + cacheZ * voxelPlaneSize;

                        if (y <= h)
                        {
                            BlockType bt;
                            if (y == h)
                            {
                                bt = isBeachArea ? BlockType.Sand : BlockType.Grass;
                            }
                            else if (y > h - 4)
                            {
                                bt = isBeachArea ? BlockType.Sand : BlockType.Dirt;
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

            // Cavernas (mesma lógica sua original)
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

            // Preencher água
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

        private void CalculateSkylight(NativeArray<BlockType> blockTypes, NativeArray<bool> solids, NativeArray<byte> sunlight, int voxelSizeX, int voxelSizeZ)
        {
            // tamanho helpers
            int planeSize = voxelSizeX * SizeY;
            int totalVoxels = voxelSizeX * SizeY * voxelSizeZ;

            // Inicializar
            for (int i = 0; i < totalVoxels; i++) sunlight[i] = 0;

            // Vertical fill: colunas X,Z
            for (int x = 0; x < voxelSizeX; x++)
            {
                for (int z = 0; z < voxelSizeZ; z++)
                {
                    byte light = 15;
                    for (int y = SizeY - 1; y >= 0; y--)
                    {
                        int idx = x + y * voxelSizeX + z * planeSize;
                        if (solids[idx])
                        {
                            sunlight[idx] = 0;
                            light = 0;
                        }
                        else
                        {
                            sunlight[idx] = light;
                        }
                    }
                }
            }

            // BFS propagation (6-neighbors), decresce 1 por bloco
            NativeList<int> queue = new NativeList<int>(Allocator.Temp);
            // Inicialmente, enfileira todas posições que tem luz > 0
            for (int i = 0; i < totalVoxels; i++)
            {
                if (sunlight[i] > 0)
                    queue.Add(i);
            }

            int read = 0;
            while (read < queue.Length)
            {
                int cur = queue[read++];
                byte curLight = sunlight[cur];
                if (curLight <= 1) continue; // sem energia para propagar

                // computar coords do index
                int plane = voxelSizeX * SizeY;
                int y = (cur / voxelSizeX) % SizeY;
                int x = cur % voxelSizeX;
                int z = cur / plane;

                // vizinhos 6
                int nx, ny, nz, nIdx;
                byte newLight = (byte)(curLight - 1);

                // +/-X
                nx = x - 1; ny = y; nz = z;
                if (nx >= 0)
                {
                    nIdx = nx + ny * voxelSizeX + nz * plane;
                    if (!solids[nIdx] && sunlight[nIdx] < newLight)
                    {
                        sunlight[nIdx] = newLight;
                        queue.Add(nIdx);
                    }
                }
                nx = x + 1;
                if (nx < voxelSizeX)
                {
                    nIdx = nx + ny * voxelSizeX + nz * plane;
                    if (!solids[nIdx] && sunlight[nIdx] < newLight)
                    {
                        sunlight[nIdx] = newLight;
                        queue.Add(nIdx);
                    }
                }

                // +/-Y
                ny = y - 1; nx = x; nz = z;
                if (ny >= 0)
                {
                    nIdx = nx + ny * voxelSizeX + nz * plane;
                    if (!solids[nIdx] && sunlight[nIdx] < newLight)
                    {
                        sunlight[nIdx] = newLight;
                        queue.Add(nIdx);
                    }
                }
                ny = y + 1;
                if (ny < SizeY)
                {
                    nIdx = nx + ny * voxelSizeX + nz * plane;
                    if (!solids[nIdx] && sunlight[nIdx] < newLight)
                    {
                        sunlight[nIdx] = newLight;
                        queue.Add(nIdx);
                    }
                }

                // +/-Z
                ny = y; nx = x; nz = z - 1;
                if (nz >= 0)
                {
                    nIdx = nx + ny * voxelSizeX + nz * plane;
                    if (!solids[nIdx] && sunlight[nIdx] < newLight)
                    {
                        sunlight[nIdx] = newLight;
                        queue.Add(nIdx);
                    }
                }
                nz = z + 1;
                if (nz < voxelSizeZ)
                {
                    nIdx = nx + ny * voxelSizeX + nz * plane;
                    if (!solids[nIdx] && sunlight[nIdx] < newLight)
                    {
                        sunlight[nIdx] = newLight;
                        queue.Add(nIdx);
                    }
                }
            }

            queue.Dispose();
        }

        private void GenerateMesh(NativeArray<int> heightCache, NativeArray<BlockType> blockTypes, NativeArray<bool> solids, NativeArray<byte> sunlight)
        {
            const int border = Border;
            const int voxelSizeX = SizeX + 2 * border;
            const int voxelSizeZ = SizeZ + 2 * border;
            const int voxelPlaneSize = voxelSizeX * SizeY;
            int heightStride = SizeX + 2 * border;

            NativeArray<Vector3Int> faceChecks = new NativeArray<Vector3Int>(6, Allocator.Temp);
            faceChecks[0] = new Vector3Int(0, 0, 1);
            faceChecks[1] = new Vector3Int(0, 0, -1);
            faceChecks[2] = new Vector3Int(0, 1, 0);
            faceChecks[3] = new Vector3Int(0, -1, 0);
            faceChecks[4] = new Vector3Int(1, 0, 0);
            faceChecks[5] = new Vector3Int(-1, 0, 0);

            NativeArray<Vector3> faceVerts = new NativeArray<Vector3>(24, Allocator.Temp);
            // Frente (dir 0)
            faceVerts[0] = new Vector3(0, 0, 1);
            faceVerts[1] = new Vector3(1, 0, 1);
            faceVerts[2] = new Vector3(1, 1, 1);
            faceVerts[3] = new Vector3(0, 1, 1);
            // Trás (dir 1)
            faceVerts[4] = new Vector3(1, 0, 0);
            faceVerts[5] = new Vector3(0, 0, 0);
            faceVerts[6] = new Vector3(0, 1, 0);
            faceVerts[7] = new Vector3(1, 1, 0);
            // Cima (dir 2)
            faceVerts[8] = new Vector3(0, 1, 1);
            faceVerts[9] = new Vector3(1, 1, 1);
            faceVerts[10] = new Vector3(1, 1, 0);
            faceVerts[11] = new Vector3(0, 1, 0);
            // Baixo (dir 3)
            faceVerts[12] = new Vector3(0, 0, 0);
            faceVerts[13] = new Vector3(1, 0, 0);
            faceVerts[14] = new Vector3(1, 0, 1);
            faceVerts[15] = new Vector3(0, 0, 1);
            // Direita (dir 4)
            faceVerts[16] = new Vector3(1, 0, 1);
            faceVerts[17] = new Vector3(1, 0, 0);
            faceVerts[18] = new Vector3(1, 1, 0);
            faceVerts[19] = new Vector3(1, 1, 1);
            // Esquerda (dir 5)
            faceVerts[20] = new Vector3(0, 0, 0);
            faceVerts[21] = new Vector3(0, 0, 1);
            faceVerts[22] = new Vector3(0, 1, 1);
            faceVerts[23] = new Vector3(0, 1, 0);

            for (int x = 0; x < SizeX; x++)
            {
                for (int z = 0; z < SizeZ; z++)
                {
                    int cacheIdx = (x + border) + (z + border) * heightStride;
                    int h = heightCache[cacheIdx];
                    int maxY = math.max(h, (int)seaLevel);

                    for (int y = 0; y <= maxY; y++)
                    {
                        int internalX = x + border;
                        int internalZ = z + border;
                        int voxelIdx = internalX + y * voxelSizeX + internalZ * voxelPlaneSize;

                        if (!solids[voxelIdx] && blockTypes[voxelIdx] != BlockType.Water) continue;

                        for (int dir = 0; dir < 6; dir++)
                        {
                            if (!generateSides && (dir == 4 || dir == 5)) continue;

                            Vector3Int check = faceChecks[dir];
                            int nx = internalX + check.x;
                            int ny = y + check.y;
                            int nz = internalZ + check.z;

                            bool neighborSolid = true;

                            if (nx >= 0 && nx < voxelSizeX && ny >= 0 && ny < SizeY && nz >= 0 && nz < voxelSizeZ)
                            {
                                int nIdx = nx + ny * voxelSizeX + nz * voxelPlaneSize;
                                BlockType nbType = blockTypes[nIdx];
                                BlockTextureMapping nbMap = blockMappings[(int)nbType];
                                BlockType curType = blockTypes[voxelIdx];
                                BlockTextureMapping curMap = blockMappings[(int)curType];

                                if (curMap.isEmpty && nbMap.isEmpty)
                                {
                                    neighborSolid = true;
                                }
                                else if (curMap.isEmpty && !nbMap.isEmpty)
                                {
                                    neighborSolid = nbMap.isSolid;
                                }
                                else if (!curMap.isEmpty && nbMap.isEmpty)
                                {
                                    neighborSolid = false;
                                }
                                else
                                {
                                    neighborSolid = nbMap.isSolid;
                                }
                            }

                            if (!neighborSolid)
                            {
                                BlockType currentType = blockTypes[voxelIdx];
                                int vIndex = vertices.Length;

                                float waterOffset = 0.15f;

                                // Adiciona vértices da face
                                for (int i = 0; i < 4; i++)
                                {
                                    Vector3 vertPos = new Vector3(x, y, z) + faceVerts[dir * 4 + i];

                                    if (currentType == BlockType.Water)
                                    {
                                        if (dir == 2)
                                        {
                                            vertPos.y -= waterOffset;
                                        }
                                        else if (dir != 3)
                                        {
                                            if (faceVerts[dir * 4 + i].y >= 1f)
                                            {
                                                vertPos.y -= waterOffset;
                                            }
                                        }
                                    }

                                    vertices.Add(vertPos);
                                }

                                // compute subchunk index based on voxel Y (0..255)
                                int subchunkIndex = y / SubChunkSize;
                                subchunkIndex = math.clamp(subchunkIndex, 0, SubChunkCountY - 1);

                                // --- Suavização por vértice: média de 4 amostras em XZ (mesma Y) ---
                                for (int vi = 0; vi < 4; vi++)
                                {
                                    Vector3 lv = faceVerts[dir * 4 + vi];

                                    // coordenadas do vértice em espaço de voxels (inteiros)
                                    // se lv.x > 0.5 -> usamos o lado +1; caso contrário usamos o lado 0
                                    int vx = internalX + (lv.x > 0.5f ? 1 : 0);
                                    int vz = internalZ + (lv.z > 0.5f ? 1 : 0);
                                    int vy = y + (lv.y > 0.5f ? 1 : 0);

                                    // amostras ao redor do canto em XZ (mantendo Y)
                                    int s0x = vx;
                                    int s0z = vz;

                                    int s1x = vx - 1;
                                    int s1z = vz;

                                    int s2x = vx;
                                    int s2z = vz - 1;

                                    int s3x = vx - 1;
                                    int s3z = vz - 1;

                                    // clamp coordenadas para ficar dentro do array sunlight
                                    s0x = math.clamp(s0x, 0, voxelSizeX - 1);
                                    s1x = math.clamp(s1x, 0, voxelSizeX - 1);
                                    s2x = math.clamp(s2x, 0, voxelSizeX - 1);
                                    s3x = math.clamp(s3x, 0, voxelSizeX - 1);

                                    int cy = math.clamp(vy, 0, SizeY - 1);

                                    s0z = math.clamp(s0z, 0, voxelSizeZ - 1);
                                    s1z = math.clamp(s1z, 0, voxelSizeZ - 1);
                                    s2z = math.clamp(s2z, 0, voxelSizeZ - 1);
                                    s3z = math.clamp(s3z, 0, voxelSizeZ - 1);

                                    int sampleIdx0 = s0x + cy * voxelSizeX + s0z * voxelPlaneSize;
                                    int sampleIdx1 = s1x + cy * voxelSizeX + s1z * voxelPlaneSize;
                                    int sampleIdx2 = s2x + cy * voxelSizeX + s2z * voxelPlaneSize;
                                    int sampleIdx3 = s3x + cy * voxelSizeX + s3z * voxelPlaneSize;

                                    byte a = sunlight[sampleIdx0];
                                    byte b = sunlight[sampleIdx1];
                                    byte c = sunlight[sampleIdx2];
                                    byte d = sunlight[sampleIdx3];

                                    int ia = a;
                                    int ib = b;
                                    int ic = c;
                                    int id = d;

                                    int min = math.min(math.min(ia, ib), math.min(ic, id));
                                    int max = math.max(math.max(ia, ib), math.max(ic, id));

                                    int vert = (ia + ib + ic + id + max) / 5;


                                    byte vertLight = (byte)vert;

                                    // store vertex light and subchunk id for each vertex
                                    vertexLights.Add(vertLight);
                                    vertexSubchunkIds.Add((byte)subchunkIndex);
                                }

                                // UVs
                                BlockFace face = (dir == 2) ? BlockFace.Top : (dir == 3) ? BlockFace.Bottom : BlockFace.Side;
                                BlockTextureMapping m = blockMappings[(int)currentType];
                                Vector2Int tileCoord;
                                switch (face)
                                {
                                    case BlockFace.Top: tileCoord = m.top; break;
                                    case BlockFace.Bottom: tileCoord = m.bottom; break;
                                    default: tileCoord = m.side; break;
                                }

                                float tileW = 1f / atlasTilesX;
                                float tileH = 1f / atlasTilesY;
                                float padU = tileW * 0.001f;
                                float padV = tileH * 0.001f;

                                float uMin = tileCoord.x * tileW + padU;
                                float vMin = tileCoord.y * tileH + padV;
                                float uRange = tileW - 2f * padU;
                                float vRange = tileH - 2f * padV;

                                for (int i = 0; i < 4; i++)
                                {
                                    Vector3 lv = faceVerts[dir * 4 + i];
                                    float pu = 0f, pv = 0f;
                                    switch (dir)
                                    {
                                        case 0: pu = lv.x; pv = lv.y; break;
                                        case 1: pu = 1f - lv.x; pv = lv.y; break;
                                        case 2: pu = lv.x; pv = 1f - lv.z; break;
                                        case 3: pu = lv.x; pv = lv.z; break;
                                        case 4: pu = 1f - lv.z; pv = lv.y; break;
                                        case 5: pu = lv.z; pv = lv.y; break;
                                    }

                                    float u = uMin + pu * uRange;
                                    float v = vMin + pv * vRange;
                                    uvs.Add(new Vector2(u, v));
                                }

                                // Normals
                                Vector3 normal = new Vector3(faceChecks[dir].x, faceChecks[dir].y, faceChecks[dir].z);
                                normals.Add(normal);
                                normals.Add(normal);
                                normals.Add(normal);
                                normals.Add(normal);

                                // Triangles
                                NativeList<int> targetTris = (currentType == BlockType.Water) ? waterTriangles : opaqueTriangles;
                                targetTris.Add(vIndex + 0);
                                targetTris.Add(vIndex + 1);
                                targetTris.Add(vIndex + 2);
                                targetTris.Add(vIndex + 2);
                                targetTris.Add(vIndex + 3);
                                targetTris.Add(vIndex + 0);
                            }
                        }
                    }
                }
            }

            faceChecks.Dispose();
            faceVerts.Dispose();
        }

        private int GetHeightFromNoise(float noise)
        {
            return math.clamp(baseHeight + (int)math.floor((noise - 0.5f) * 2f * heightVariation), 1, SizeY - 1);
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
    public List<Vector2> uvs;
    public List<Vector3> normals;

    public MeshBuildResult(Vector2Int coord, List<Vector3> v, List<int> opaqueT, List<int> waterT, List<Vector2> u, List<Vector3> n)
    {
        this.coord = coord;
        vertices = v;
        opaqueTriangles = opaqueT;
        waterTriangles = waterT;
        uvs = u;
        normals = n;
    }
}
