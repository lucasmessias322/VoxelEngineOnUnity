using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public enum TreeStyle : byte
{
    OakBroadleaf = 0,
    TaigaSpruce = 1,
    Cactus = 2
}

public struct TreeInstance
{
    public int worldX;
    public int worldZ;
    public int trunkHeight;
    public int canopyRadius;
    public int canopyHeight;
    public TreeStyle treeStyle;
}

public struct TreeSpawnRuleData
{
    public BiomeType biome;
    public TreeStyle treeStyle;
    public TreeSettings settings;
}

public struct BlockEdit
{
    public int x;
    public int y;
    public int z;
    public int type;
}
public static class MeshGenerator
{
    private const int SizeX = Chunk.SizeX;
    private const int SizeY = Chunk.SizeY;
    private const int SizeZ = Chunk.SizeZ;
    private const float DefaultAOCurveExponent = 1.12f;

    private const int SubchunksPerColumn = Chunk.SubchunksPerColumn;
    // ------------------- Tree Instance -------------------





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
        public BiomeNoiseSettings biomeNoiseSettings;

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
            return TerrainHeightSampler.SampleSurfaceHeight(
                worldX,
                worldZ,
                noiseLayers,
                warpLayers,
                baseHeight,
                offsetX,
                offsetZ,
                SizeY,
                biomeNoiseSettings);
        }
    }



    [BurstCompile]
    struct PopulateTerrainJob : IJobParallelFor
    {
        public Vector2Int coord;
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
        public BiomeNoiseSettings biomeNoiseSettings;

        public void Execute(int index)
        {
            int paddedSize = SizeX + 2 * border;
            int heightStride = paddedSize;

            int lx = index % paddedSize;   // cacheX
            int lz = index / paddedSize;   // cacheZ
            int realLx = lx - border;
            int realLz = lz - border;
            int worldX = coord.x * SizeX + realLx;
            int worldZ = coord.y * SizeZ + realLz;

            int h = heightCache[lx + lz * heightStride];
            int centerIdx = lx + lz * heightStride;
            int hN = lz + 1 < paddedSize ? heightCache[centerIdx + heightStride] : h;
            int hS = lz > 0 ? heightCache[centerIdx - heightStride] : h;
            int hE = lx + 1 < paddedSize ? heightCache[centerIdx + 1] : h;
            int hW = lx > 0 ? heightCache[centerIdx - 1] : h;

            TerrainColumnContext columnContext = TerrainColumnSampler.CreateFromNeighborHeights(
                worldX,
                worldZ,
                h,
                hN,
                hS,
                hE,
                hW,
                CliffTreshold,
                baseHeight,
                seaLevel,
                biomeNoiseSettings);
            TerrainSurfaceData surfaceData = columnContext.surface;

            int voxelSizeX = SizeX + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;

            int maxSolidY = math.min(h, SizeY - 1);
            int idx = lx + lz * voxelPlaneSize;

            for (int y = 0; y <= maxSolidY; y++, idx += voxelSizeX)
            {
                BlockType bt = TerrainSurfaceRules.GetBlockTypeAtHeight(y, surfaceData);
                blockTypes[idx] = bt;
                solids[idx] = blockMappings[(int)bt].isSolid;
            }
        }
    }

    public static void ScheduleDataJob(
        Vector2Int coord,
        NoiseLayer[] noiseLayersArr,
        WarpLayer[] warpLayersArr,
        BlockTextureMapping[] blockMappingsArr,
        int baseHeight,
        float globalOffsetX,
        float globalOffsetZ,
        float seaLevel,
        BiomeNoiseSettings biomeNoiseSettings,
        int oreSeed,


        NativeArray<BlockEdit> blockEdits,

        int treeMargin,
        int borderSize,
        int maxTreeRadius,
        int CliffTreshold,
        bool enableTrees,
        OreSpawnSettings[] oreSettingsArr,
        TreeSpawnRuleData[] treeSpawnRulesArr,
        WormCaveSettings caveSettings,
        NativeArray<byte> lightData, // <--- NOVA INJECAO DE DEPENDENCIA DE LUZ
        out JobHandle dataHandle,
        out NativeArray<int> heightCache,
        out NativeArray<BlockType> blockTypes,
        out NativeArray<bool> solids,
        out NativeArray<byte> light,
        out NativeArray<NoiseLayer> nativeNoiseLayers,
        out NativeArray<WarpLayer> nativeWarpLayers,
        out NativeArray<BlockTextureMapping> nativeBlockMappings,
        out NativeArray<OreSpawnSettings> nativeOreSettings,
        out NativeArray<TreeSpawnRuleData> nativeTreeSpawnRules,
        out NativeArray<bool> subchunkNonEmpty
    )
    {
        // 1. Fixar o borderSize em 1 (Padrão para Ambient Occlusion e Costura)

        // 2. Alocações Iniciais de Configuração
        nativeNoiseLayers = new NativeArray<NoiseLayer>(noiseLayersArr, Allocator.TempJob);
        nativeWarpLayers = new NativeArray<WarpLayer>(warpLayersArr, Allocator.TempJob);
        nativeBlockMappings = new NativeArray<BlockTextureMapping>(blockMappingsArr, Allocator.TempJob);
        if (oreSettingsArr != null && oreSettingsArr.Length > 0)
            nativeOreSettings = new NativeArray<OreSpawnSettings>(oreSettingsArr, Allocator.TempJob);
        else
            nativeOreSettings = new NativeArray<OreSpawnSettings>(0, Allocator.TempJob);
        if (treeSpawnRulesArr != null && treeSpawnRulesArr.Length > 0)
            nativeTreeSpawnRules = new NativeArray<TreeSpawnRuleData>(treeSpawnRulesArr, Allocator.TempJob);
        else
            nativeTreeSpawnRules = new NativeArray<TreeSpawnRuleData>(0, Allocator.TempJob);
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
            biomeNoiseSettings = biomeNoiseSettings,
            heightCache = heightCache,
            heightStride = heightSize
        };
        JobHandle heightHandle = heightJob.Schedule(totalHeightPoints, 32); // Batch size 64 para paralelismo (ajuste se necessário)




        // ==========================================
        // JOB 1a: Populate Terrain Columns (PARALELO!)
        // ==========================================
        var populateJob = new PopulateTerrainJob
        {
            coord = coord,
            heightCache = heightCache,
            blockTypes = blockTypes,
            solids = solids,
            blockMappings = nativeBlockMappings,
            border = borderSize,
            seaLevel = seaLevel,
            baseHeight = baseHeight,
            CliffTreshold = CliffTreshold,
            biomeNoiseSettings = biomeNoiseSettings
        };

        int paddedSize = SizeX + 2 * borderSize;
        int totalColumns = paddedSize * paddedSize;

        JobHandle populateHandle = populateJob.Schedule(totalColumns, 32, heightHandle); // batch 64 é ótimo




        // ==========================================
        // JOB 1: Geração de Dados (Terreno)
        // ==========================================
        var chunkDataJob = new ChunkData.ChunkDataJob
        {
            coord = coord,
            noiseLayers = nativeNoiseLayers,
            warpLayers = nativeWarpLayers,
            blockMappings = nativeBlockMappings,
            blockEdits = blockEdits,


            baseHeight = baseHeight,
            offsetX = globalOffsetX,
            offsetZ = globalOffsetZ,
            seaLevel = seaLevel,
            biomeNoiseSettings = biomeNoiseSettings,


            treeMargin = treeMargin,
            border = borderSize,
            maxTreeRadius = maxTreeRadius,
            CliffTreshold = CliffTreshold,

            heightCache = heightCache,
            blockTypes = blockTypes,
            solids = solids,
            treeSpawnRules = nativeTreeSpawnRules,
            oreSettings = nativeOreSettings,
            oreSeed = oreSeed,
            caveSettings = caveSettings,

            enableTrees = enableTrees,
            subchunkNonEmpty = subchunkNonEmpty
        };
        // JobHandle chunkDataHandle = chunkDataJob.Schedule(heightHandle); // Dependência no heightHandle
        JobHandle chunkDataHandle = chunkDataJob.Schedule(populateHandle);

        var lightJob = new ChunkLighting.ChunkLightingJob
        {
            blockTypes = blockTypes,
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
        NativeArray<int3> suppressedGrassBillboards,
        int atlasTilesX,
        int atlasTilesY,
        bool generateSides,
        int borderSize,
        int chunkCoordX,
        int chunkCoordZ,
        int startY,
        int endY,
        bool enableGrassBillboards,
        float grassBillboardChance,
        BlockType grassBillboardBlockType,
        float grassBillboardHeight,
        float grassBillboardNoiseScale,
        float grassBillboardJitter,
        float aoStrength,
        float aoCurveExponent,
        float aoMinLight,
        out JobHandle meshHandle,
        out NativeList<Vector3> vertices,
        out NativeList<int> opaqueTriangles,
        out NativeList<int> transparentTriangles,
        out NativeList<int> billboardTriangles,
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
        billboardTriangles = new NativeList<int>(2048 * 3, Allocator.TempJob);
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
            suppressedGrassBillboards = suppressedGrassBillboards,

            border = borderSize,
            atlasTilesX = atlasTilesX,
            atlasTilesY = atlasTilesY,
            generateSides = generateSides,
            chunkCoordX = chunkCoordX,
            chunkCoordZ = chunkCoordZ,
            enableGrassBillboards = enableGrassBillboards,
            grassBillboardChance = grassBillboardChance,
            grassBillboardBlockType = grassBillboardBlockType,
            grassBillboardHeight = grassBillboardHeight,
            grassBillboardNoiseScale = grassBillboardNoiseScale,
            grassBillboardJitter = grassBillboardJitter,
            aoStrength = aoStrength,
            aoCurveExponent = aoCurveExponent,
            aoMinLight = aoMinLight,

            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            waterTriangles = waterTriangles,
            transparentTriangles = transparentTriangles,
            billboardTriangles = billboardTriangles,
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
        [ReadOnly] public NativeArray<int3> suppressedGrassBillboards;

        public int border;
        public int atlasTilesX;
        public int atlasTilesY;
        public bool generateSides;
        public int chunkCoordX;
        public int chunkCoordZ;
        public bool enableGrassBillboards;
        public float grassBillboardChance;
        public BlockType grassBillboardBlockType;
        public float grassBillboardHeight;
        public float grassBillboardNoiseScale;
        public float grassBillboardJitter;
        public float aoStrength;
        public float aoCurveExponent;
        public float aoMinLight;

        // LIMITES DO SUBCHUNK
        public int startY;
        public int endY;

        public NativeList<Vector3> vertices;
        public NativeList<int> opaqueTriangles;
        public NativeList<int> waterTriangles;
        public NativeList<int> transparentTriangles;
        public NativeList<int> billboardTriangles;
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
            GenerateGrassBillboards(blockTypes, light, invAtlasTilesX, invAtlasTilesY);
        }
        private void GenerateGrassBillboards(
            NativeArray<BlockType> blockTypes,
            NativeArray<byte> light,
            float invAtlasTilesX,
            float invAtlasTilesY)
        {
            if (!enableGrassBillboards || grassBillboardChance <= 0f)
                return;

            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;
            float noiseScale = math.max(1e-4f, grassBillboardNoiseScale);
            float jitter = math.clamp(grassBillboardJitter, 0f, 0.35f);

            BlockTextureMapping mapping = blockMappings[(int)grassBillboardBlockType];
            Vector2 atlasUv = new Vector2(
                mapping.side.x * invAtlasTilesX + 0.001f,
                mapping.side.y * invAtlasTilesY + 0.001f
            );
            float tint = mapping.tintSide ? 1f : 0f;

            int minY = math.max(startY - 1, 0);
            int maxY = math.min(endY - 1, SizeY - 2);

            for (int y = minY; y <= maxY; y++)
            {
                int py = y + 1;
                if (py < startY || py >= endY)
                    continue;

                for (int z = border; z < border + SizeZ; z++)
                {
                    int worldZ = chunkCoordZ * SizeZ + (z - border);
                    for (int x = border; x < border + SizeX; x++)
                    {
                        int worldX = chunkCoordX * SizeX + (x - border);
                        int idx = x + y * voxelSizeX + z * voxelPlaneSize;

                        if (blockTypes[idx] != BlockType.Grass)
                            continue;

                        int upIdx = idx + voxelSizeX;
                        if (blockTypes[upIdx] != BlockType.Air)
                            continue;

                        float n = noise.snoise(new float2(
                            (worldX + 123.17f) * noiseScale,
                            (worldZ - 91.73f) * noiseScale
                        )) * 0.5f + 0.5f;

                        float effectiveChance = math.saturate(
                            grassBillboardChance * math.lerp(0.35f, 1.65f, n)
                        );

                        uint h = math.hash(new int3(worldX, py, worldZ));
                        float chance = (h & 0x00FFFFFF) / 16777215f;
                        if (chance > effectiveChance)
                            continue;

                        if (IsSuppressedGrassBillboard(worldX, py, worldZ))
                            continue;

                        byte packed = light[upIdx];
                        byte billboardLight = (byte)math.max(
                            (int)LightUtils.GetSkyLight(packed),
                            (int)LightUtils.GetBlockLight(packed)
                        );
                        float light01 = billboardLight / 15f;

                        uint h2 = math.hash(new int3(worldX * 17 + 3, py * 31 + 5, worldZ * 13 + 7));
                        float jx = ((((h2 >> 8) & 0xFF) / 255f) * 2f - 1f) * jitter;
                        float jz = ((((h2 >> 16) & 0xFF) / 255f) * 2f - 1f) * jitter;

                        Vector3 center = new Vector3((x - border) + 0.5f + jx, py - 0.02f, (z - border) + 0.5f + jz);
                        AddBillboardCross(center, grassBillboardHeight, atlasUv, light01, tint);
                    }
                }
            }
        }

        private void AddBillboardCross(Vector3 center, float height, Vector2 atlasUv, float light01, float tint)
        {
            const float halfWidth = 0.38f;
            Vector3 a0 = center + new Vector3(-halfWidth, 0f, -halfWidth);
            Vector3 a1 = center + new Vector3(halfWidth, 0f, halfWidth);
            Vector3 a2 = a1 + new Vector3(0f, height, 0f);
            Vector3 a3 = a0 + new Vector3(0f, height, 0f);
            AddDoubleSidedQuad(a0, a1, a2, a3, atlasUv, light01, tint);

            Vector3 b0 = center + new Vector3(-halfWidth, 0f, halfWidth);
            Vector3 b1 = center + new Vector3(halfWidth, 0f, -halfWidth);
            Vector3 b2 = b1 + new Vector3(0f, height, 0f);
            Vector3 b3 = b0 + new Vector3(0f, height, 0f);
            AddDoubleSidedQuad(b0, b1, b2, b3, atlasUv, light01, tint);
        }

        private bool IsSuppressedGrassBillboard(int worldX, int worldY, int worldZ)
        {
            for (int i = 0; i < suppressedGrassBillboards.Length; i++)
            {
                int3 p = suppressedGrassBillboards[i];
                if (p.x == worldX && p.y == worldY && p.z == worldZ)
                    return true;
            }
            return false;
        }

        private void AddDoubleSidedQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2 atlasUv,
            float light01,
            float tint)
        {
            int vIndex = vertices.Length;
            Vector3 upNormal = new Vector3(0f, 1f, 0f);

            vertices.Add(p0);
            vertices.Add(p1);
            vertices.Add(p2);
            vertices.Add(p3);

            normals.Add(upNormal);
            normals.Add(upNormal);
            normals.Add(upNormal);
            normals.Add(upNormal);

            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(1f, 1f));
            uvs.Add(new Vector2(0f, 1f));

            uv2.Add(atlasUv);
            uv2.Add(atlasUv);
            uv2.Add(atlasUv);
            uv2.Add(atlasUv);

            Vector4 e = new Vector4(light01, tint, 1f, 0f);
            extraUVs.Add(e);
            extraUVs.Add(e);
            extraUVs.Add(e);
            extraUVs.Add(e);

            billboardTriangles.Add(vIndex + 0);
            billboardTriangles.Add(vIndex + 1);
            billboardTriangles.Add(vIndex + 2);
            billboardTriangles.Add(vIndex + 0);
            billboardTriangles.Add(vIndex + 2);
            billboardTriangles.Add(vIndex + 3);

            billboardTriangles.Add(vIndex + 0);
            billboardTriangles.Add(vIndex + 2);
            billboardTriangles.Add(vIndex + 1);
            billboardTriangles.Add(vIndex + 0);
            billboardTriangles.Add(vIndex + 3);
            billboardTriangles.Add(vIndex + 2);
        }

        private bool IsFaceVisibleForCurrentBlock(BlockType current, BlockType neighbor)
        {
            if (current == neighbor &&
                (current == BlockType.Water || blockMappings[(int)current].isTransparent))
            {
                return false;
            }

            if (blockMappings[(int)neighbor].isEmpty)
                return true;

            bool neighborOpaque = blockMappings[(int)neighbor].isSolid &&
                                  !blockMappings[(int)neighbor].isTransparent;
            return !neighborOpaque;
        }

        private bool LeavesHasAnyExposedFace(int x, int y, int z, NativeArray<BlockType> blockTypes, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            // Renderiza "cubo completo" apenas quando houver ao menos uma face exposta.
            for (int d = 0; d < 6; d++)
            {
                int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                int ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                int nz = z + (d == 4 ? 1 : d == 5 ? -1 : 0);

                bool outside = nx < 0 || nx >= voxelSizeX || ny < 0 || ny >= SizeY || nz < 0 || nz >= voxelSizeZ;
                if (outside)
                    return true;

                int nIdx = nx + ny * voxelSizeX + nz * voxelPlaneSize;
                BlockType neighbor = blockTypes[nIdx];
                if (IsFaceVisibleForCurrentBlock(BlockType.Leaves, neighbor))
                    return true;
            }

            return false;
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

                    // Restrict scan to the active chunk footprint for X/Z axes.
                    // This avoids iterating the full lighting padding when only the core area can emit faces.
                    int minU = (u == 1) ? startY : (u == 0 ? border : border);
                    int maxU = (u == 1) ? endY : (u == 0 ? border + SizeX : border + SizeZ);

                    int minV = (v == 1) ? startY : (v == 0 ? border : border);
                    int maxV = (v == 1) ? endY : (v == 0 ? border + SizeX : border + SizeZ);

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

                                int idx = x + y * voxelSizeX + z * voxelPlaneSize;
                                BlockType current = blockTypes[idx];

                                if (current == BlockType.Air) { mask[i + j * sizeU] = 0; continue; }

                                int nx = x + (axis == 0 ? normalSign : 0);
                                int ny = y + (axis == 1 ? normalSign : 0);
                                int nz = z + (axis == 2 ? normalSign : 0);

                                bool outside = nx < 0 || nx >= voxelSizeX || ny < 0 || ny >= SizeY || nz < 0 || nz >= voxelSizeZ;

                                bool isVisible = false;


                                if (outside)
                                {
                                    isVisible = true;
                                }
                                else
                                {
                                    int nIdx = nx + ny * voxelSizeX + nz * voxelPlaneSize;
                                    BlockType neighbor = blockTypes[nIdx];

                                    isVisible = IsFaceVisibleForCurrentBlock(current, neighbor);
                                }

                                if (!isVisible && current == BlockType.Leaves)
                                {
                                    bool shouldRenderFullCube = LeavesHasAnyExposedFace(x, y, z, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    if (shouldRenderFullCube)
                                        isVisible = true;
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

                                    int packedAO;
                                    if (aoStrength <= 0f)
                                    {
                                        packedAO = 0xFF;
                                    }
                                    else
                                    {
                                        byte ao0 = GetVertexAO(aoPos, -stepU, -stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                        byte ao1 = GetVertexAO(aoPos, stepU, -stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                        byte ao2 = GetVertexAO(aoPos, stepU, stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                        byte ao3 = GetVertexAO(aoPos, -stepU, stepV, solids, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                        packedAO = (ao0) | (ao1 << 2) | (ao2 << 4) | (ao3 << 6);
                                    }

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
                                    float aoCurve = aoCurveExponent > 0f ? aoCurveExponent : DefaultAOCurveExponent;
                                    float aoBase = currentAO / 3f;
                                    float aoCurved = math.pow(aoBase, aoCurve);
                                    float aoDarkened = 1f - (1f - aoCurved) * math.max(0f, aoStrength);
                                    float floatAO = math.max(math.saturate(aoMinLight), math.saturate(aoDarkened));

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

    [BurstCompile]
    public struct DisposeSuppressedBillboardsJob : IJob
    {
        [DeallocateOnJobCompletion] public NativeArray<int3> suppressedGrassBillboards;
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










