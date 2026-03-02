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
    // No topo da classe (junto com as outras const)
    private const int SubchunkHeight = Chunk.SubchunkHeight;
    private const int SubchunksPerColumn = Chunk.SubchunksPerColumn;
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
        public int type;
    }

    [BurstCompile]
    private struct HeightmapJob : IJobParallelFor
    {
        public Vector2Int coord;

        [ReadOnly] public NativeArray<NoiseLayer> noiseLayers;
        [ReadOnly] public NativeArray<WarpLayer> warpLayers;

        public int baseHeight;
        public float offsetX;
        public float offsetZ;
        public int border;

        public NativeArray<int> heightCache;
        public int heightStride;

        public void Execute(int i)
        {
            int lx = i % heightStride;
            int lz = i / heightStride;
            int realLx = lx - border;
            int realLz = lz - border;
            int worldX = coord.x * SizeX + realLx;
            int worldZ = coord.y * SizeZ + realLz;
            heightCache[i] = GetSurfaceHeight(worldX, worldZ);
        }

        private int GetSurfaceHeight(int worldX, int worldZ)
        {
            // === Domain Warping ===
            float warpX = 0f;
            float warpZ = 0f;
            float sumWarpAmp = 0f;

            for (int j = 0; j < warpLayers.Length; j++)
            {
                var layer = warpLayers[j];
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

            for (int j = 0; j < noiseLayers.Length; j++)
            {
                var layer = noiseLayers[j];
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
    }



    [BurstCompile]
    struct PopulateTerrainJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> heightCache;
        [NativeDisableParallelForRestriction]
        [WriteOnly] public NativeArray<BlockType> blockTypes;

        [NativeDisableParallelForRestriction]
        [WriteOnly] public NativeArray<bool> solids;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;

        public int border;
        public float seaLevel;
        public int baseHeight;
        public int CliffTreshold;

        public void Execute(int index)
        {
            int paddedSize = SizeX + 2 * border;
            int heightStride = paddedSize;

            int lx = index % paddedSize;   // cacheX
            int lz = index / paddedSize;   // cacheZ

            int h = heightCache[lx + lz * heightStride];

            bool isBeachArea = h <= seaLevel + 2f;

            // === IsCliff local (corrigido - não precisa de coord) ===
            bool isCliff = false;
            if (lx > 0 && lx < paddedSize - 1 && lz > 0 && lz < paddedSize - 1)
            {
                int centerIdx = lx + lz * heightStride;
                int hN = heightCache[centerIdx + heightStride];
                int hS = heightCache[centerIdx - heightStride];
                int hE = heightCache[centerIdx + 1];
                int hW = heightCache[centerIdx - 1];

                int maxDiff = math.max(math.abs(h - hN), math.abs(h - hS));
                maxDiff = math.max(maxDiff, math.abs(h - hE));
                maxDiff = math.max(maxDiff, math.abs(h - hW));

                isCliff = maxDiff >= CliffTreshold;
            }

            int mountainStoneHeight = baseHeight + 70;
            bool isHighMountain = h >= mountainStoneHeight;

            int voxelSizeX = SizeX + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;

            for (int y = 0; y < SizeY; y++)
            {
                int idx = lx + y * voxelSizeX + lz * voxelPlaneSize;

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

                    blockTypes[idx] = bt;
                    solids[idx] = blockMappings[(int)bt].isSolid;
                }
                else
                {
                    blockTypes[idx] = BlockType.Air;
                    solids[idx] = false;
                }
            }
        }
    }
    public static void ScheduleDataJob(
        Vector2Int coord,
        NoiseLayer[] noiseLayersArr,
        WarpLayer[] warpLayersArr,
        NoiseLayer[] caveLayersArr,
        BlockTextureMapping[] blockMappingsArr,
        int baseHeight,
        float globalOffsetX,
        float globalOffsetZ,
        float seaLevel,
        float caveThreshold,
        int caveStride,
        int maxCaveDepthMultiplier,
        float caveRarityScale,
        float caveRarityThreshold,
        float caveMaskSmoothness,
        NativeArray<BlockEdit> blockEdits,

        int treeMargin,
        int borderSize,
        int maxTreeRadius,
        int CliffTreshold,
        bool enableCave,
        bool enableTrees,
        NativeArray<byte> lightData, // <--- NOVA INJEÇÃO DE DEPENDÊNCIA DE LUZ
        out JobHandle dataHandle,
        out NativeArray<int> heightCache,
        out NativeArray<BlockType> blockTypes,
        out NativeArray<bool> solids,
        out NativeArray<byte> light,
        out NativeArray<NoiseLayer> nativeNoiseLayers,
        out NativeArray<WarpLayer> nativeWarpLayers,
        out NativeArray<NoiseLayer> nativeCaveLayers,
        out NativeArray<BlockTextureMapping> nativeBlockMappings,
        out NativeArray<bool> subchunkNonEmpty,
        TreeSettings treeSettings
    )
    {
        // 1. Fixar o borderSize em 1 (Padrão para Ambient Occlusion e Costura)

        // 2. Alocações Iniciais de Configuração
        nativeNoiseLayers = new NativeArray<NoiseLayer>(noiseLayersArr, Allocator.TempJob);
        nativeWarpLayers = new NativeArray<WarpLayer>(warpLayersArr, Allocator.TempJob);
        nativeCaveLayers = new NativeArray<NoiseLayer>(caveLayersArr, Allocator.TempJob);
        nativeBlockMappings = new NativeArray<BlockTextureMapping>(blockMappingsArr, Allocator.TempJob);
        subchunkNonEmpty = new NativeArray<bool>(SubchunksPerColumn, Allocator.TempJob);

        // 3. Alocações dos Arrays Intermédios que fluem entre os Jobs (TempJob)
        int heightSize = SizeX + 2 * borderSize;
        int totalHeightPoints = heightSize * heightSize;
        int voxelSizeX = SizeX + 2 * borderSize;
        int voxelSizeZ = SizeZ + 2 * borderSize;
        int voxelPlaneSize = voxelSizeX * SizeY;
        int totalVoxels = voxelSizeX * SizeY * voxelSizeZ;

        heightCache = new NativeArray<int>(totalHeightPoints, Allocator.TempJob);
        blockTypes = new NativeArray<BlockType>(totalVoxels, Allocator.TempJob);
        solids = new NativeArray<bool>(totalVoxels, Allocator.TempJob);
        light = new NativeArray<byte>(totalVoxels, Allocator.TempJob);




        // ==========================================
        // JOB 0: Geração do Heightmap (Paralelo)
        // ==========================================
        var heightJob = new HeightmapJob
        {
            coord = coord,
            noiseLayers = nativeNoiseLayers,
            warpLayers = nativeWarpLayers,
            baseHeight = baseHeight,
            offsetX = globalOffsetX,
            offsetZ = globalOffsetZ,
            border = borderSize,
            heightCache = heightCache,
            heightStride = heightSize
        };
        JobHandle heightHandle = heightJob.Schedule(totalHeightPoints, 32); // Batch size 64 para paralelismo (ajuste se necessário)




        // ==========================================
        // JOB 1a: Populate Terrain Columns (PARALELO!)
        // ==========================================
        var populateJob = new PopulateTerrainJob
        {
            heightCache = heightCache,
            blockTypes = blockTypes,
            solids = solids,
            blockMappings = nativeBlockMappings,
            border = borderSize,
            seaLevel = seaLevel,
            baseHeight = baseHeight,
            CliffTreshold = CliffTreshold
        };

        int paddedSize = SizeX + 2 * borderSize;
        int totalColumns = paddedSize * paddedSize;

        JobHandle populateHandle = populateJob.Schedule(totalColumns, 64, heightHandle); // batch 64 é ótimo




        // ==========================================
        // JOB 1: Geração de Dados (Terreno)
        // ==========================================
        var chunkDataJob = new ChunkDataJob
        {
            coord = coord,
            noiseLayers = nativeNoiseLayers,
            warpLayers = nativeWarpLayers,
            caveLayers = nativeCaveLayers,
            blockMappings = nativeBlockMappings,
            blockEdits = blockEdits,


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
            treeSettings = treeSettings,

            enableTrees = enableTrees,
            enableCave = enableCave,
            subchunkNonEmpty = subchunkNonEmpty
        };
        // JobHandle chunkDataHandle = chunkDataJob.Schedule(heightHandle); // Dependência no heightHandle
        JobHandle chunkDataHandle = chunkDataJob.Schedule(populateHandle);

        var lightJob = new ChunkLightingJob
        {
            blockTypes = blockTypes,
            solids = solids,
            light = light, // Output calculado
            blockLightData = lightData, // Injeta block light do global
            blockMappings = nativeBlockMappings,
            voxelSizeX = voxelSizeX,
            voxelSizeZ = voxelSizeZ,
            totalVoxels = totalVoxels,
            voxelPlaneSize = voxelPlaneSize,
            SizeX = SizeX,
            SizeY = SizeY,
            SizeZ = SizeZ
        };
        dataHandle = lightJob.Schedule(chunkDataHandle);
    }

    public static void ScheduleMeshJob(
        NativeArray<int> heightCache,
        NativeArray<BlockType> blockTypes,
        NativeArray<bool> solids,
        NativeArray<byte> light,
        NativeArray<BlockTextureMapping> nativeBlockMappings,
        int atlasTilesX,
        int atlasTilesY,
        bool generateSides,
        int borderSize,
        int startY,
    int endY,
        out JobHandle meshHandle,
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
        // 1. Alocações das Listas de Mesh (Output)
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

        // ==========================================
        // JOB 2: Geração da Malha (Mesh)
        // ==========================================
        var meshJob = new ChunkMeshJob
        {
            startY = startY,
            endY = endY,
            blockTypes = blockTypes,
            solids = solids,
            light = light, // Usa a luz previamente calculada e passada por parâmetro
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
        // O MeshJob agora é agendado independentemente, assumindo que os dados intermediários já estão prontos
        meshHandle = meshJob.Schedule();
    }


    // =========================================================================
    // JOB 1: CHUNK DATA JOB (Gera Terreno, Cavernas, Água, Árvores)
    // =========================================================================
    [BurstCompile]
    private struct ChunkDataJob : IJob
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
        public float caveRarityScale;
        public float caveRarityThreshold;
        public float caveMaskSmoothness;
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
                GenerateCaves(heightCache, blockTypes, solids);
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
                int endY = startY + SubchunkHeight;
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

                                // Usa o nosso novo Worley/Cellular Noise que já cria os formatos de túnel nativamente
                                float finalSample = MyNoise.OctaveCellular3D(nx, ny, nz, layer);

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
                        int cacheIdx = cacheX + cacheZ * heightStride;
                        int h = heightCache[cacheIdx];

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
        [ReadOnly] public NativeArray<int> heightCache;
        [ReadOnly] public NativeArray<BlockType> blockTypes;
        [ReadOnly] public NativeArray<bool> solids;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [ReadOnly] public NativeArray<byte> light;

        public int border;
        public int atlasTilesX;
        public int atlasTilesY;
        public bool generateSides;

        // LIMITES DO SUBCHUNK
        public int startY;
        public int endY;

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
            if (IsSubchunkEmpty())
            {
                return; // pula TODO o greedy meshing
            }
            float invAtlasTilesX = 1f / atlasTilesX;
            float invAtlasTilesY = 1f / atlasTilesY;

            GenerateMesh(heightCache, blockTypes, solids, light, invAtlasTilesX, invAtlasTilesY);
        }

        private bool IsSubchunkEmpty()
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;

            for (int y = startY; y < endY; y++)
            {
                for (int z = border; z < border + SizeZ; z++)
                {
                    for (int x = border; x < border + SizeX; x++)
                    {
                        int idx = x + y * voxelSizeX + z * voxelPlaneSize;
                        if (blockTypes[idx] != BlockType.Air)
                        {
                            return false; // tem bloco (terra, água, árvore, etc)
                        }
                    }
                }
            }
            return true; // 100% ar → pula
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

                    // AQUI ACONTECE A MÁGICA DE RESTRIÇÃO PARA SUBCHUNKS (Limites de Y controlados)
                    int minN = (axis == 1) ? startY : border;
                    int maxN = (axis == 1) ? endY : border + chunkSize;

                    int minU = (u == 1) ? startY : 0;
                    int maxU = (u == 1) ? endY : sizeU;

                    int minV = (v == 1) ? startY : 0;
                    int maxV = (v == 1) ? endY : sizeV;

                    Vector3 normal = new Vector3(axis == 0 ? normalSign : 0, axis == 1 ? normalSign : 0, axis == 2 ? normalSign : 0);
                    BlockFace faceType = axis == 1 ? (normalSign > 0 ? BlockFace.Top : BlockFace.Bottom) : BlockFace.Side;

                    Vector3Int stepU = new Vector3Int(u == 0 ? 1 : 0, u == 1 ? 1 : 0, u == 2 ? 1 : 0);
                    Vector3Int stepV = new Vector3Int(v == 0 ? 1 : 0, v == 1 ? 1 : 0, v == 2 ? 1 : 0);

                    for (int n = minN; n < maxN; n++)
                    {
                        for (int j = minV; j < maxV; j++)
                        {
                            for (int i = minU; i < maxU; i++)
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

                                // if (outside)
                                // {
                                //     isVisible = true;
                                // }
                                // else
                                // {
                                //     int nIdx = nx + ny * voxelSizeX + nz * voxelPlaneSize;
                                //     BlockType neighbor = blockTypes[nIdx];

                                //     if (current == BlockType.Water && neighbor == BlockType.Water)
                                //     {
                                //         isVisible = false;
                                //     }
                                //     else if (current != BlockType.Water && !blockMappings[(int)current].isSolid)
                                //     {
                                //         isVisible = false;
                                //     }
                                //     else if (blockMappings[(int)neighbor].isEmpty)
                                //     {
                                //         isVisible = true;
                                //     }
                                //     else
                                //     {
                                //         bool neighborOpaque = blockMappings[(int)neighbor].isSolid && !blockMappings[(int)neighbor].isTransparent;
                                //         isVisible = !neighborOpaque;
                                //     }
                                // }

                                if (outside)
                                {
                                    isVisible = true;
                                }
                                else
                                {
                                    int nIdx = nx + ny * voxelSizeX + nz * voxelPlaneSize;
                                    BlockType neighbor = blockTypes[nIdx];

                                    // === CORREÇÃO PRINCIPAL ===
                                    // Corta faces entre blocos transparentes do MESMO tipo
                                    // (folhas com folhas, água com água, vidro com vidro, etc.)
                                    if (current == neighbor &&
                                        (current == BlockType.Water || blockMappings[(int)current].isTransparent))
                                    {
                                        isVisible = false;
                                    }
                                    else if (blockMappings[(int)neighbor].isEmpty)
                                    {
                                        isVisible = true;
                                    }
                                    else
                                    {
                                        bool neighborOpaque = blockMappings[(int)neighbor].isSolid &&
                                                              !blockMappings[(int)neighbor].isTransparent;
                                        isVisible = !neighborOpaque;
                                    }
                                }

                                if (isVisible)
                                {
                                    byte packed = outside
                                        ? LightUtils.PackLight(15, 0)
                                        : light[nx + ny * voxelSizeX + nz * voxelPlaneSize];

                                    byte faceLight = (byte)math.max(
                                        (int)LightUtils.GetSkyLight(packed),
                                        (int)LightUtils.GetBlockLight(packed)
                                    );

                                    // --- NOVO: CALCULANDO AO POR BLOCO 1x1 ---
                                    int aoPlaneN = n + normalSign;
                                    Vector3Int aoPos = new Vector3Int(
                                        axis == 0 ? aoPlaneN : x,
                                        axis == 1 ? aoPlaneN : y,
                                        axis == 2 ? aoPlaneN : z
                                    );

                                    byte ao0 = GetVertexAO(aoPos, -stepU, -stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    byte ao1 = GetVertexAO(aoPos, stepU, -stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    byte ao2 = GetVertexAO(aoPos, stepU, stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    byte ao3 = GetVertexAO(aoPos, -stepU, stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);

                                    // Empacota os 4 valores de AO (2 bits cada = 8 bits totais)
                                    int packedAO = (ao0) | (ao1 << 2) | (ao2 << 4) | (ao3 << 6);

                                    // Mask agora guarda: [8 bits AO] | [4 bits Luz] | [12 bits Bloco]
                                    mask[i + j * sizeU] = (int)current | ((int)faceLight << 12) | (packedAO << 16);
                                }
                                else
                                {
                                    mask[i + j * sizeU] = 0;
                                }
                            }
                        }

                        // GREEDY MESHING
                        for (int j = minV; j < maxV; j++)
                        {
                            int i = minU;
                            while (i < maxU)
                            {
                                int packedData = mask[i + j * sizeU];
                                if (packedData == 0)
                                {
                                    i++;
                                    continue;
                                }

                                int w = 1;
                                while (i + w < maxU && mask[i + w + j * sizeU] == packedData) w++;

                                int h = 1;
                                while (j + h < maxV)
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

                                // Extrai os dados que foram empacotados
                                BlockType bt = (BlockType)(packedData & 0xFFF);
                                byte finalLight = (byte)((packedData >> 12) & 0xF);
                                int packedAO = (packedData >> 16) & 0xFF;

                                // Desempacota o AO do quad gigante (ele será idêntico por toda a superfície mesclada)
                                byte ao0 = (byte)(packedAO & 0x3);
                                byte ao1 = (byte)((packedAO >> 2) & 0x3);
                                byte ao2 = (byte)((packedAO >> 4) & 0x3);
                                byte ao3 = (byte)((packedAO >> 6) & 0x3);

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

                                // Flip da diagonal para evitar artefatos no AO
                                bool flipTriangle = (ao0 + ao2) > (ao1 + ao3);

                                if (normalSign > 0)
                                {
                                    if (flipTriangle)
                                    {
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 1); tris.Add(vIndex + 3);
                                        tris.Add(vIndex + 1); tris.Add(vIndex + 2); tris.Add(vIndex + 3);
                                    }
                                    else
                                    {
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 1); tris.Add(vIndex + 2);
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 2); tris.Add(vIndex + 3);
                                    }
                                }
                                else
                                {
                                    if (flipTriangle)
                                    {
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 3); tris.Add(vIndex + 1);
                                        tris.Add(vIndex + 1); tris.Add(vIndex + 3); tris.Add(vIndex + 2);
                                    }
                                    else
                                    {
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 3); tris.Add(vIndex + 2);
                                        tris.Add(vIndex + 0); tris.Add(vIndex + 2); tris.Add(vIndex + 1);
                                    }
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

        private Vector3Int GetAoBase(int uCoord, int vCoord, int aoN, int uAxis, int vAxis)
        {
            int ax = (uAxis == 0 ? uCoord : vAxis == 0 ? vCoord : aoN);
            int ay = (uAxis == 1 ? uCoord : vAxis == 1 ? vCoord : aoN);
            int az = (uAxis == 2 ? uCoord : vAxis == 2 ? vCoord : aoN);
            return new Vector3Int(ax, ay, az);
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


    [BurstCompile]
    private struct ChunkLightingJob : IJob
    {
        [ReadOnly] public NativeArray<BlockType> blockTypes;
        [ReadOnly] public NativeArray<bool> solids;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [ReadOnly] public NativeArray<byte> blockLightData;

        public NativeArray<byte> light;

        public int voxelSizeX;
        public int voxelSizeZ;
        public int totalVoxels;
        public int voxelPlaneSize; // Assumindo que seja voxelSizeX * SizeY
        public int SizeX;
        public int SizeY;
        public int SizeZ;

        public void Execute()
        {
            // 1. Criamos um mapa temporário para a Skylight e uma fila para a propagação
            NativeArray<byte> skyMap = new NativeArray<byte>(totalVoxels, Allocator.Temp);
            NativeQueue<int> lightQueue = new NativeQueue<int>(Allocator.Temp);

            // 2. PASSO 1: Raio de Sol Vertical (Luz Direta)
            for (int lx = 0; lx < voxelSizeX; lx++)
            {
                for (int lz = 0; lz < voxelSizeZ; lz++)
                {
                    byte currentSky = 15;
                    for (int y = SizeY - 1; y >= 0; y--)
                    {
                        int idx = lx + y * voxelSizeX + lz * voxelPlaneSize;
                        byte opacity = blockMappings[(int)blockTypes[idx]].lightOpacity;

                        if (opacity >= 15)
                        {
                            currentSky = 0;
                        }
                        else if (opacity > 0)
                        {
                            currentSky = (byte)math.max(0, (int)currentSky - (int)opacity);
                        }

                        skyMap[idx] = currentSky;

                        // Enfileira os nós que receberam luz para o passo de suavização
                        if (currentSky > 0)
                        {
                            lightQueue.Enqueue(idx);
                        }
                    }
                }
            }

            // 3. PASSO 2: Suavização / Propagação (Flood Fill BFS)
            // Offsets 1D para os 6 vizinhos: +X, -X, +Y, -Y, +Z, -Z
            NativeArray<int> neighborOffsets = new NativeArray<int>(6, Allocator.Temp);
            neighborOffsets[0] = 1;                  // Direita (+X)
            neighborOffsets[1] = -1;                 // Esquerda (-X)
            neighborOffsets[2] = voxelSizeX;         // Cima (+Y)
            neighborOffsets[3] = -voxelSizeX;        // Baixo (-Y)
            neighborOffsets[4] = voxelPlaneSize;     // Frente (+Z)
            neighborOffsets[5] = -voxelPlaneSize;    // Trás (-Z)

            while (lightQueue.TryDequeue(out int currentIndex))
            {
                byte currentLight = skyMap[currentIndex];

                // Se a luz já é muito fraca, não pode propagar
                if (currentLight <= 1) continue;

                // Decodificando 1D para 3D para evitar propagar para fora do Chunk atual
                int z = currentIndex / voxelPlaneSize;
                int rem = currentIndex % voxelPlaneSize;
                int y = rem / voxelSizeX;
                int x = rem % voxelSizeX;

                for (int i = 0; i < 6; i++)
                {
                    // Checagem de limites do chunk (para não acessar memória indevida)
                    if (x == 0 && i == 1) continue;
                    if (x == voxelSizeX - 1 && i == 0) continue;
                    if (y == 0 && i == 3) continue;
                    if (y == SizeY - 1 && i == 2) continue;
                    if (z == 0 && i == 5) continue;
                    if (z == voxelSizeZ - 1 && i == 4) continue;

                    int neighborIndex = currentIndex + neighborOffsets[i];
                    byte opacity = blockMappings[(int)blockTypes[neighborIndex]].lightOpacity;

                    // A luz perde 1 de intensidade ao se mover para o lado/cima/baixo + a opacidade do bloco
                    int lightLoss = 1 + opacity;
                    byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);

                    // Se a luz que chega no vizinho é maior que a luz que ele já tem, atualizamos e enfileiramos
                    if (propagatedLight > skyMap[neighborIndex])
                    {
                        skyMap[neighborIndex] = propagatedLight;
                        lightQueue.Enqueue(neighborIndex);
                    }
                }
            }

            // 4. PASSO 3: Mesclar Skylight suavizada com BlockLight
            for (int i = 0; i < totalVoxels; i++)
            {
                byte blockL = 0;
                if (blockLightData.IsCreated && i < blockLightData.Length)
                {
                    blockL = blockLightData[i];
                }

                // Empacota (0-15 de sky, 0-15 de block) no mesmo byte!
                light[i] = LightUtils.PackLight(skyMap[i], blockL);
            }

            // 5. Cleanup dos Allocator.Temp
            skyMap.Dispose();
            lightQueue.Dispose();
            neighborOffsets.Dispose();
        }
    }




    [BurstCompile]
    public struct DisposeChunkDataJob : IJob
    {
        [DeallocateOnJobCompletion] public NativeArray<int> heightCache;
        [DeallocateOnJobCompletion] public NativeArray<BlockType> blockTypes;
        [DeallocateOnJobCompletion] public NativeArray<bool> solids;
        [DeallocateOnJobCompletion] public NativeArray<byte> light;
        [DeallocateOnJobCompletion] public NativeArray<BlockTextureMapping> blockMappings;
        [DeallocateOnJobCompletion] public NativeArray<bool> subchunkNonEmpty; // ← NOVO
        public void Execute() { }
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

