

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
        float caveRarityScale,     // NOVO
float caveRarityThreshold, // NOVO
float caveMaskSmoothness,  // NOVO
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
        out NativeList<byte> tintFlags,
        out NativeList<byte> vertexAO,
        out NativeList<Vector4> extraUVs



    )
    {
        NativeArray<NoiseLayer> nativeNoiseLayers = new NativeArray<NoiseLayer>(noiseLayersArr, Allocator.TempJob);
        NativeArray<WarpLayer> nativeWarpLayers = new NativeArray<WarpLayer>(warpLayersArr, Allocator.TempJob);
        NativeArray<NoiseLayer> nativeCaveLayers = new NativeArray<NoiseLayer>(caveLayersArr, Allocator.TempJob);
        NativeArray<BlockTextureMapping> nativeBlockMappings = new NativeArray<BlockTextureMapping>(blockMappingsArr, Allocator.TempJob);

        vertices = new NativeList<Vector3>(4096, Allocator.TempJob);
        opaqueTriangles = new NativeList<int>(4096 * 3, Allocator.TempJob);
        waterTriangles = new NativeList<int>(4096 * 3, Allocator.TempJob);
        transparentTriangles = new NativeList<int>(4096 * 3, Allocator.TempJob);

        normals = new NativeList<Vector3>(4096, Allocator.TempJob);
        // Aloque a nova lista para o Vector4
        extraUVs = new NativeList<Vector4>(4096 * 4, Allocator.TempJob);
        vertexLights = new NativeList<byte>(4096 * 4, Allocator.TempJob);  // Novo: aloque aqui (4 verts por face)
        tintFlags = new NativeList<byte>(4096 * 4, Allocator.TempJob);
        vertexAO = new NativeList<byte>(4096 * 4, Allocator.TempJob);   // ← NOVO
        // UVs
        uvs = new NativeList<Vector2>(4096, Allocator.TempJob);
        uv2 = new NativeList<Vector2>(4096, Allocator.TempJob); // NOVO: canal 1
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
            caveRarityScale = caveRarityScale,         // NOVO
            caveRarityThreshold = caveRarityThreshold, // NOVO
            caveMaskSmoothness = caveMaskSmoothness,   // NOVO
            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            waterTriangles = waterTriangles,
            transparentTriangles = transparentTriangles,
            uvs = uvs,
            uv2 = uv2, // <- passe para o job
            normals = normals,
            extraUVs = extraUVs,
            vertexLights = vertexLights,
            tintFlags = tintFlags, // Novo: atribua ao job
            vertexAO = vertexAO,   // ← NOVO
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
        public float caveRarityScale;       // NOVO
        public float caveRarityThreshold;   // NOVO
        public float caveMaskSmoothness;    // NOVO

        public int CliffTreshold;

        public NativeList<Vector3> vertices;
        public NativeList<int> opaqueTriangles;
        public NativeList<int> waterTriangles;
        public NativeList<int> transparentTriangles;

        public NativeList<Vector2> uvs;
        public NativeList<Vector2> uv2; // UV channel 1: tile base (uMin, vMin) normalizado
        public NativeList<Vector3> normals;
        public NativeList<Vector4> extraUVs;
        public NativeList<byte> vertexLights; // 0..15 por vértice
        public NativeList<byte> tintFlags;  // NOVO: (WriteOnly implícito via Add)
        public NativeList<byte> vertexAO;   // ← NOVO
        [WriteOnly] public NativeArray<byte> voxelOutput;
        public void Execute()
        {
            float invAtlasTilesX = 1f / atlasTilesX;
            float invAtlasTilesY = 1f / atlasTilesY;
            int heightSize = SizeX + 2 * border;
            int totalHeightPoints = heightSize * heightSize;

            NativeArray<int> heightCache = new NativeArray<int>(totalHeightPoints, Allocator.Temp);

            // --- LOOP DO HEIGHTMAP OTIMIZADO ---
            // Agora chamamos a função GetSurfaceHeight diretamente no loop
            int heightStride = heightSize;

            for (int i = 0; i < totalHeightPoints; i++)
            {
                int lx = i % heightStride;
                int lz = i / heightStride;

                int realLx = lx - border;
                int realLz = lz - border;

                int worldX = coord.x * SizeX + realLx;
                int worldZ = coord.y * SizeZ + realLz;

                // Chama a nova função criada abaixo
                heightCache[i] = GetSurfaceHeight(worldX, worldZ);
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
            GenerateMesh(heightCache, blockTypes, solids, light, invAtlasTilesX, invAtlasTilesY);

            // Limpeza
            light.Dispose();
            heightCache.Dispose();
            blockTypes.Dispose();
            solids.Dispose();
        }


        // ==========================================
        //  NOVA FUNÇÃO PARA CALCULAR ALTURA
        // ==========================================
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

            // Cálculo final
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

            // Usa centerIdx para evitar multiplicar tudo de novo
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

                                // 1. Amostramos o ruído base e convertemos de (0 a 1) para (-1 a 1)
                                float n1 = MyNoise.OctavePerlin3D(nx, ny, nz, layer) * 2f - 1f;

                                // 2. Amostramos um segundo ruído com um grande deslocamento para criar o cruzamento
                                // Usamos offsets arbitrários para desalinhar completamente a segunda camada
                                float n2 = MyNoise.OctavePerlin3D(nx + 128.5f, ny + 256.1f, nz + 64.3f, layer) * 2f - 1f;

                                // 3. O "centro" do túnel é onde ambos n1 e n2 são próximos de 0.
                                // Usamos a distância ao quadrado para performance.
                                float tubeDistSq = (n1 * n1) + (n2 * n2);

                                // 4. Invertemos o valor para que o centro do túnel resulte num valor alto (próximo a 1).
                                // O multiplicador '5f' controla a grossura da parede (maior = túnel mais estreito).
                                float tubeCave = math.max(0f, 1f - (tubeDistSq * 5f));

                                // 5. Opcional: Mantemos as "bolhas" originais de forma suave para criar salões ("Cheese Caves")
                                float cheeseCave = MyNoise.OctavePerlin3D(nx, ny, nz, layer);
                                if (layer.redistributionModifier != 1f || layer.exponent != 1f)
                                {
                                    cheeseCave = MyNoise.Redistribution(cheeseCave, layer.redistributionModifier, layer.exponent);
                                }

                                // Misturamos os túneis com os salões grandes
                                float finalSample = math.max(tubeCave, cheeseCave * 0.45f); // 0.45 atenua as bolhas para os túneis brilharem mais

                                totalCave += finalSample * layer.amplitude;
                                sumCaveAmp += math.max(1e-5f, layer.amplitude);
                            }

                            if (sumCaveAmp > 0f) totalCave /= sumCaveAmp;
                            // === NOVA LÓGICA DA MÁSCARA AQUI ===
                            float maskNx = (worldX + offsetX) / caveRarityScale;
                            float maskNy = worldY / caveRarityScale;
                            float maskNz = (worldZ + offsetZ) / caveRarityScale;

                            float maskVal = noise.cnoise(new float3(maskNx, maskNy, maskNz));

                            // math.saturate é o equivalente Burst do Mathf.Clamp01
                            float maskWeight = math.saturate((maskVal - caveRarityThreshold) * caveMaskSmoothness);

                            totalCave *= maskWeight;
                            // ===================================

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
                            // ==========================================
                            // CORREÇÃO: Protege os primeiros 20 blocos (Bedrock)
                            // ==========================================
                            if (y <= 20) continue;
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


        private void GenerateMesh(
            NativeArray<int> heightCache,
            NativeArray<BlockType> blockTypes,
            NativeArray<bool> solids,
            NativeArray<byte> light, float invAtlasTilesX, float invAtlasTilesY)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;

            // Máscara agora guarda: BlockType (12 bits) | Light (4 bits) | AO (8 bits)
            // Total 24 bits usados de um int (32 bits), então cabe tranquilo.
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

                    // Pré-cálculo dos Steps para usar dentro do loop da máscara
                    Vector3Int stepU = new Vector3Int(u == 0 ? 1 : 0, u == 1 ? 1 : 0, u == 2 ? 1 : 0);
                    Vector3Int stepV = new Vector3Int(v == 0 ? 1 : 0, v == 1 ? 1 : 0, v == 2 ? 1 : 0);

                    for (int n = minN; n < maxN; n++)
                    {
                        // 1. Preenchimento da Máscara COM CÁLCULO DE AO
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
                                    isVisible = false;
                                else if (outside)
                                    isVisible = true;
                                else
                                {
                                    int nIdx = nx + ny * voxelSizeX + nz * voxelPlaneSize;
                                    BlockType neighbor = blockTypes[nIdx];
                                    if (blockMappings[(int)neighbor].isEmpty) isVisible = true;
                                    else if (blockMappings[(int)neighbor].isTransparent && !blockMappings[(int)current].isTransparent) isVisible = true;
                                    else isVisible = !blockMappings[(int)neighbor].isSolid;
                                }

                                if (isVisible)
                                {
                                    byte faceLight = outside ? (byte)15 : light[nx + ny * voxelSizeX + nz * voxelPlaneSize];

                                    // --- MUDANÇA: Calcular AO AGORA e salvar na máscara ---

                                    int aoPlaneN = n + normalSign;
                                    int ax = (u == 0 ? i : v == 0 ? j : aoPlaneN);
                                    int ay = (u == 1 ? i : v == 1 ? j : aoPlaneN);
                                    int az = (u == 2 ? i : v == 2 ? j : aoPlaneN);
                                    Vector3Int aoBase = new Vector3Int(ax, ay, az);

                                    // Calcula AO dos 4 cantos desta face específica (1x1)
                                    // Ordem: 0=BL, 1=BR, 2=TR, 3=TL (relativo ao plano UV)
                                    byte ao0 = GetVertexAO(aoBase, -stepU, -stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    byte ao1 = GetVertexAO(aoBase, stepU, -stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    byte ao2 = GetVertexAO(aoBase, stepU, stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    byte ao3 = GetVertexAO(aoBase, -stepU, stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);

                                    // Empacota os 4 AOs em 8 bits (2 bits cada, já que valor vai de 0 a 3)
                                    int packedAO = (ao0) | (ao1 << 2) | (ao2 << 4) | (ao3 << 6);

                                    // A máscara agora contém TUDO que precisa ser igual para mesclar
                                    mask[i + j * sizeU] = (int)current | ((int)faceLight << 12) | (packedAO << 16);
                                }
                                else
                                {
                                    mask[i + j * sizeU] = 0;
                                }
                            }
                        }

                        // 2. Greedy Meshing Loop
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

                                // Calcula largura (w)
                                // O loop verifica se "mask[...] == packedData". 
                                // Como packedData agora inclui o AO, ele SÓ vai crescer se o AO for IDÊNTICO.
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

                                // --- Extração de dados ---
                                BlockType bt = (BlockType)(packedData & 0xFFF);
                                byte finalLight = (byte)((packedData >> 12) & 0xF);

                                // --- MUDANÇA: Desempacotar o AO ---
                                int aoPackedData = (packedData >> 16) & 0xFF;
                                byte ao0 = (byte)(aoPackedData & 3);
                                byte ao1 = (byte)((aoPackedData >> 2) & 3);
                                byte ao2 = (byte)((aoPackedData >> 4) & 3);
                                byte ao3 = (byte)((aoPackedData >> 6) & 3);

                                // Não precisamos mais recalcular GetVertexAO aqui, pois já temos o valor correto
                                // para todo o bloco greedymeshed (já que todos são iguais)

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

                                    // vertices.Add(new Vector3(px, py, pz));
                                    // // vertexLights.Add(finalLight);

                                    // normals.Add(normal);

                                    // // Aplica o AO
                                    // if (l == 0) vertexAO.Add(ao0);
                                    // else if (l == 1) vertexAO.Add(ao1);
                                    // else if (l == 2) vertexAO.Add(ao2);
                                    // else vertexAO.Add(ao3);

                                    // // UVs
                                    // Vector2 uvCoord = axis == 0 ? new Vector2(rawV, rawU) :
                                    //                   axis == 1 ? new Vector2(rawV, rawU) :
                                    //                               new Vector2(rawU, rawV);
                                    // uvs.Add(uvCoord);

                                    // // Tint e Texture setup (mantido igual)
                                    // BlockTextureMapping m = blockMappings[(int)bt];
                                    // bool tint = faceType == BlockFace.Top ? m.tintTop :
                                    //             faceType == BlockFace.Bottom ? m.tintBottom : m.tintSide;
                                    // tintFlags.Add(tint ? (byte)1 : (byte)0);

                                    // Vector2Int tile = faceType == BlockFace.Top ? m.top :
                                    //                   faceType == BlockFace.Bottom ? m.bottom : m.side;

                                    // // Ajuste de UV para greedy mesh (tiling da textura)
                                    // uv2.Add(new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f));
                                    vertices.Add(new Vector3(px, py, pz));
                                    normals.Add(normal);

                                    // UVs normais
                                    Vector2 uvCoord = axis == 0 ? new Vector2(rawV, rawU) :
                                                      axis == 1 ? new Vector2(rawV, rawU) :
                                                                  new Vector2(rawU, rawV);
                                    uvs.Add(uvCoord);

                                    // Descobrindo o Tint da face
                                    BlockTextureMapping m = blockMappings[(int)bt];
                                    bool tint = faceType == BlockFace.Top ? m.tintTop :
                                                faceType == BlockFace.Bottom ? m.tintBottom : m.tintSide;

                                    // ================================================================
                                    // A MÁGICA AQUI: Processamento Matemático dentro do Burst Compiler!
                                    // ================================================================

                                    // 1. Pega o AO correto baseado no vértice atual (l)
                                    byte currentAO = l == 0 ? ao0 : (l == 1 ? ao1 : (l == 2 ? ao2 : ao3));

                                    // 2. Faz as divisões e conversões (O Burst resolve isso em milissegundos)
                                    float rawLight = finalLight / 15f;
                                    float floatTint = tint ? 1f : 0f;
                                    float floatAO = currentAO / 3f;

                                    // 3. Salva tudo empacotado no Vector4!
                                    extraUVs.Add(new Vector4(rawLight, floatTint, floatAO, 0f));

                                    // ================================================================

                                    Vector2Int tile = faceType == BlockFace.Top ? m.top :
                                                      faceType == BlockFace.Bottom ? m.bottom : m.side;

                                    // Ajuste de UV para greedy mesh (tiling da textura)
                                    uv2.Add(new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f));
                                }

                                // Triângulos (mantido igual)
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

                                // Limpar máscara
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


        // =========================================================================================
        // FUNÇÃO DE AO CORRIGIDA
        // =========================================================================================
        private bool IsOccluder(int x, int y, int z, NativeArray<bool> solids, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            // Se sair do array (incluindo borders), consideramos oclusor para evitar buracos de luz nas bordas do mapa
            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false; // Retornar FALSE aqui deixa as bordas claras. Retornar TRUE deixa bordas escuras.
                              // Como você tem BORDER, false é melhor pois a borda real já está em 'solids'.

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            return solids[idx];
        }

        private byte GetVertexAO(Vector3Int pos, Vector3Int d1, Vector3Int d2,
                                 NativeArray<bool> solids, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            // Verifica os 3 blocos vizinhos ao canto (side1, side2, corner)
            bool s1 = IsOccluder(pos.x + d1.x, pos.y + d1.y, pos.z + d1.z, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            bool s2 = IsOccluder(pos.x + d2.x, pos.y + d2.y, pos.z + d2.z, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            bool c = IsOccluder(pos.x + d1.x + d2.x, pos.y + d1.y + d2.y, pos.z + d1.z + d2.z, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            // Lógica clássica de Voxel:
            // Se os dois lados (s1 e s2) estão bloqueados, o canto está totalmente ocluso.
            // Isso também previne artefatos de anisotropia.
            if (s1 && s2) return 0;

            // Cálculo do nível de AO (0 a 3, onde 3 é claro e 0 é escuro)
            return (byte)(3 - (s1 ? 1 : 0) - (s2 ? 1 : 0) - (c ? 1 : 0));
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
