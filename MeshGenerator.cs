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
    Cactus = 2,
    BirchBroadleaf = 3,
    SavannaAcacia = 4,
    FancyOak = 5
}

public struct TreeInstance
{
    public int worldX;
    public int worldZ;
    public int trunkHeight;
    public int canopyRadius;
    public int canopyHeight;
    public int spacingRadius;
    public TreeStyle treeStyle;
}

public struct TreeSpawnRuleData
{
    public BiomeType biome;
    public TreeStyle treeStyle;
    public TreeSettings settings;
}

public static class TreeGenerationMetrics
{
    public static int GetHorizontalReach(TreeStyle treeStyle, TreeSettings settings)
    {
        return GetHorizontalReach(treeStyle, settings.maxHeight, settings.canopyRadius, settings.canopyHeight);
    }

    public static int GetPlacementSpacingRadius(TreeStyle treeStyle, TreeSettings settings)
    {
        return GetPlacementSpacingRadius(treeStyle, settings.maxHeight, settings.canopyRadius, settings.canopyHeight, settings.minSpacing);
    }

    public static int GetPlacementSpacingRadius(TreeStyle treeStyle, int heightValue, int canopyRadius, int canopyHeight, int minSpacing)
    {
        int spacingFromConfig = math.max(1, (math.max(1, minSpacing) + 1) / 2);
        int horizontalReach = GetHorizontalReach(treeStyle, heightValue, canopyRadius, canopyHeight);
        return math.max(spacingFromConfig, horizontalReach);
    }

    public static int GetHorizontalReach(TreeStyle treeStyle, int heightValue, int canopyRadius, int canopyHeight)
    {
        int safeHeight = math.max(1, heightValue);
        int safeRadius = math.max(0, canopyRadius);
        int safeCanopyHeight = math.max(1, canopyHeight);

        switch (treeStyle)
        {
            case TreeStyle.SavannaAcacia:
                return math.max(safeRadius + 2, 5);

            case TreeStyle.FancyOak:
                return GetFancyOakHorizontalReach(safeHeight, safeRadius, safeCanopyHeight);

            default:
                return safeRadius;
        }
    }

    public static int GetVerticalMargin(TreeStyle treeStyle, TreeSettings settings)
    {
        int safeMaxHeight = math.max(1, settings.maxHeight);
        int safeCanopyHeight = math.max(1, settings.canopyHeight);

        switch (treeStyle)
        {
            case TreeStyle.SavannaAcacia:
                return safeMaxHeight + math.max(4, safeCanopyHeight + 1);

            case TreeStyle.TaigaSpruce:
                return safeMaxHeight + math.max(3, safeCanopyHeight + 1);

            case TreeStyle.FancyOak:
                return safeMaxHeight + math.clamp(math.max(4, safeCanopyHeight), 4, 5) + 2;

            default:
                return safeMaxHeight + safeCanopyHeight + 2;
        }
    }

    private static int GetFancyOakHorizontalReach(int heightLimit, int canopyRadius, int canopyHeight)
    {
        int leafDistanceLimit = math.clamp(math.max(4, canopyHeight), 4, 5);
        float scaleWidth = math.max(1f, canopyRadius / 4f);
        float maxBranchReach = heightLimit * 0.25f * 1.328f * scaleWidth;
        return math.max(canopyRadius + 1, (int)math.ceil(maxBranchReach) + leafDistanceLimit);
    }
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
            int hNE = lx + 1 < paddedSize && lz + 1 < paddedSize ? heightCache[centerIdx + 1 + heightStride] : h;
            int hNW = lx > 0 && lz + 1 < paddedSize ? heightCache[centerIdx - 1 + heightStride] : h;
            int hSE = lx + 1 < paddedSize && lz > 0 ? heightCache[centerIdx + 1 - heightStride] : h;
            int hSW = lx > 0 && lz > 0 ? heightCache[centerIdx - 1 - heightStride] : h;

            TerrainColumnContext columnContext = TerrainColumnSampler.CreateFromNeighborHeights(
                worldX,
                worldZ,
                h,
                hN,
                hS,
                hE,
                hW,
                hNE,
                hNW,
                hSE,
                hSW,
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
        NativeArray<NoiseLayer> noiseLayers,
        NativeArray<WarpLayer> warpLayers,
        NativeArray<BlockTextureMapping> blockMappings,
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
        NativeArray<OreSpawnSettings> oreSettings,
        NativeArray<TreeSpawnRuleData> treeSpawnRules,
        WormCaveSettings caveSettings,
        NativeArray<byte> lightData, // <--- NOVA INJECAO DE DEPENDENCIA DE LUZ
        out JobHandle dataHandle,
        out NativeArray<int> heightCache,
        out NativeArray<BlockType> blockTypes,
        out NativeArray<bool> solids,
        out NativeArray<byte> light,
        out NativeArray<bool> subchunkNonEmpty
    )
    {
        // 1. Fixar o borderSize em 1 (PadrÃ£o para Ambient Occlusion e Costura)

        // 2. AlocaÃ§Ãµes dos Arrays IntermÃ©dios que fluem entre os Jobs (TempJob)
        subchunkNonEmpty = new NativeArray<bool>(SubchunksPerColumn, Allocator.TempJob);

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
        // JOB 0: GeraÃ§Ã£o do Heightmap (Paralelo)
        // ==========================================
        var heightJob = new HeightmapJob
        {
            coord = coord,
            noiseLayers = noiseLayers,
            warpLayers = warpLayers,
            baseHeight = baseHeight,
            offsetX = globalOffsetX,
            offsetZ = globalOffsetZ,
            border = borderSize,
            biomeNoiseSettings = biomeNoiseSettings,
            heightCache = heightCache,
            heightStride = heightSize
        };
        JobHandle heightHandle = heightJob.Schedule(totalHeightPoints, 32); // Batch size 64 para paralelismo (ajuste se necessÃ¡rio)




        // ==========================================
        // JOB 1a: Populate Terrain Columns (PARALELO!)
        // ==========================================
        var populateJob = new PopulateTerrainJob
        {
            coord = coord,
            heightCache = heightCache,
            blockTypes = blockTypes,
            solids = solids,
            blockMappings = blockMappings,
            border = borderSize,
            seaLevel = seaLevel,
            baseHeight = baseHeight,
            CliffTreshold = CliffTreshold,
            biomeNoiseSettings = biomeNoiseSettings
        };

        int paddedSize = SizeX + 2 * borderSize;
        int totalColumns = paddedSize * paddedSize;

        JobHandle populateHandle = populateJob.Schedule(totalColumns, 32, heightHandle); // batch 64 Ã© Ã³timo




        // ==========================================
        // JOB 1: GeraÃ§Ã£o de Dados (Terreno)
        // ==========================================
        var chunkDataJob = new ChunkData.ChunkDataJob
        {
            coord = coord,
            noiseLayers = noiseLayers,
            warpLayers = warpLayers,
            blockMappings = blockMappings,
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
            treeSpawnRules = treeSpawnRules,
            oreSettings = oreSettings,
            oreSeed = oreSeed,
            caveSettings = caveSettings,

            enableTrees = enableTrees,
            subchunkNonEmpty = subchunkNonEmpty
        };
        // JobHandle chunkDataHandle = chunkDataJob.Schedule(heightHandle); // DependÃªncia no heightHandle
        JobHandle chunkDataHandle = chunkDataJob.Schedule(populateHandle);

        var lightJob = new ChunkLighting.ChunkLightingJob
        {
            blockTypes = blockTypes,
            light = light, // Output calculado
            blockLightData = lightData, // Injeta block light do global
            blockMappings = blockMappings,
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
        // 1. AlocaÃ§Ãµes das Listas de Mesh (Output)
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
        // JOB 2: GeraÃ§Ã£o da Malha (Mesh)
        // ==========================================
        var meshJob = new ChunkMeshJob
        {
            startY = startY,
            endY = endY,
            blockTypes = blockTypes,
            solids = solids,
            light = light, // Usa a luz previamente calculada e passada por parÃ¢metro
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
        // O MeshJob agora Ã© agendado independentemente, assumindo que os dados intermediÃ¡rios jÃ¡ estÃ£o prontos
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

        private struct GreedyFaceData
        {
            public int blockId;
            public byte valid;
            public byte faceLight;
            public byte ao0;
            public byte ao1;
            public byte ao2;
            public byte ao3;
        }

        public void Execute()
        {
            float invAtlasTilesX = 1f / atlasTilesX;
            float invAtlasTilesY = 1f / atlasTilesY;

            GenerateMesh(heightCache, blockTypes, solids, light, invAtlasTilesX, invAtlasTilesY);
            GenerateSpecialMeshes(blockTypes, light, invAtlasTilesX, invAtlasTilesY);
            GenerateGrassBillboards(blockTypes, light, invAtlasTilesX, invAtlasTilesY);
        }

        private static bool HasFace(in GreedyFaceData face)
        {
            return face.valid != 0;
        }

        private static bool HasSameSurface(in GreedyFaceData a, in GreedyFaceData b)
        {
            return HasFace(a) &&
                   HasFace(b) &&
                   a.blockId == b.blockId &&
                   a.faceLight == b.faceLight;
        }

        private static bool CanMergeAlongU(in GreedyFaceData left, in GreedyFaceData right)
        {
            // Merge only when the shared edge keeps the same AO signature.
            return HasSameSurface(left, right) &&
                   left.ao1 == right.ao0 &&
                   left.ao2 == right.ao3;
        }

        private static bool CanMergeAlongV(in GreedyFaceData bottom, in GreedyFaceData top)
        {
            return HasSameSurface(bottom, top) &&
                   bottom.ao3 == top.ao0 &&
                   bottom.ao2 == top.ao1;
        }

        private static Vector3Int GetFaceVertexPlanePos(
            int axis,
            int u,
            int v,
            int n,
            int normalSign,
            int i,
            int j,
            int du,
            int dv)
        {
            int planeN = n + normalSign;
            int uCoord = i + du;
            int vCoord = j + dv;

            return new Vector3Int(
                axis == 0 ? planeN : (u == 0 ? uCoord : v == 0 ? vCoord : n),
                axis == 1 ? planeN : (u == 1 ? uCoord : v == 1 ? vCoord : n),
                axis == 2 ? planeN : (u == 2 ? uCoord : v == 2 ? vCoord : n)
            );
        }

        private static byte GetRectVertexAO(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int width,
            int height,
            int localX,
            int localY)
        {
            int cellX = localX == width ? startI + width - 1 : startI + localX;
            int cellY = localY == height ? startJ + height - 1 : startJ + localY;
            GreedyFaceData face = mask[cellX + cellY * sizeU];

            if (localX == width)
                return localY == height ? face.ao2 : face.ao1;

            return localY == height ? face.ao3 : face.ao0;
        }

        private static bool MatchesQuadInterpolationForAO(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int width,
            int height,
            bool flipTriangle)
        {
            if (width <= 0 || height <= 0)
                return false;

            int scale = width * height;
            int ao00 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, 0, 0);
            int ao10 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, width, 0);
            int ao11 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, width, height);
            int ao01 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, 0, height);

            for (int y = 0; y <= height; y++)
            {
                for (int x = 0; x <= width; x++)
                {
                    int actual = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, x, y);
                    int expectedScaled;

                    if (!flipTriangle)
                    {
                        if (x * height >= y * width)
                        {
                            expectedScaled = ao00 * scale +
                                             (ao10 - ao00) * x * height +
                                             (ao11 - ao10) * y * width;
                        }
                        else
                        {
                            expectedScaled = ao00 * scale +
                                             (ao11 - ao01) * x * height +
                                             (ao01 - ao00) * y * width;
                        }
                    }
                    else
                    {
                        if (x * height + y * width <= scale)
                        {
                            expectedScaled = ao00 * scale +
                                             (ao10 - ao00) * x * height +
                                             (ao01 - ao00) * y * width;
                        }
                        else
                        {
                            expectedScaled = ao11 * scale +
                                             (ao10 - ao11) * width * (height - y) +
                                             (ao01 - ao11) * height * (width - x);
                        }
                    }

                    if (actual * scale != expectedScaled)
                        return false;
                }
            }

            return true;
        }

        private static bool TryGetRepresentableRectFlip(
            NativeArray<GreedyFaceData> mask,
            int sizeU,
            int startI,
            int startJ,
            int width,
            int height,
            out bool flipTriangle)
        {
            bool noFlipMatches = MatchesQuadInterpolationForAO(mask, sizeU, startI, startJ, width, height, false);
            bool flipMatches = MatchesQuadInterpolationForAO(mask, sizeU, startI, startJ, width, height, true);

            int ao00 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, 0, 0);
            int ao10 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, width, 0);
            int ao11 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, width, height);
            int ao01 = GetRectVertexAO(mask, sizeU, startI, startJ, width, height, 0, height);
            bool heuristicFlip = (ao00 + ao11) > (ao10 + ao01);

            if (noFlipMatches && flipMatches)
            {
                flipTriangle = heuristicFlip;
                return true;
            }

            if (flipMatches)
            {
                flipTriangle = true;
                return true;
            }

            if (noFlipMatches)
            {
                flipTriangle = false;
                return true;
            }

            flipTriangle = heuristicFlip;
            return false;
        }

        private void GenerateSpecialMeshes(
            NativeArray<BlockType> blockTypes,
            NativeArray<byte> light,
            float invAtlasTilesX,
            float invAtlasTilesY)
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
                        BlockType blockType = blockTypes[idx];
                        if (blockType == BlockType.Air)
                            continue;

                        BlockTextureMapping mapping = blockMappings[(int)blockType];
                        if (mapping.isEmpty || mapping.renderShape == BlockRenderShape.Cube)
                            continue;

                        float light01 = GetSpecialMeshLight01(idx, voxelSizeX, light);
                        Vector3 origin = new Vector3(x - border, y, z - border);

                        switch (mapping.renderShape)
                        {
                            case BlockRenderShape.Cross:
                                AddCrossShape(origin, mapping, blockType, invAtlasTilesX, invAtlasTilesY, light01);
                                break;

                            case BlockRenderShape.Cuboid:
                                AddCuboidShape(origin, mapping, blockType, x, y, z, invAtlasTilesX, invAtlasTilesY, light01);
                                break;
                        }
                    }
                }
            }
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

        private void AddCrossShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

            Vector2Int tile = mapping.side;
            Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
            float tint = mapping.tintSide ? 1f : 0f;

            NativeList<int> tris = blockType == BlockType.Water
                ? waterTriangles
                : (mapping.isTransparent ? transparentTriangles : opaqueTriangles);

            Vector3 a0 = origin + new Vector3(min.x, min.y, min.z);
            Vector3 a1 = origin + new Vector3(max.x, min.y, max.z);
            Vector3 a2 = origin + new Vector3(max.x, max.y, max.z);
            Vector3 a3 = origin + new Vector3(min.x, max.y, min.z);
            AddDoubleSidedShapeQuad(a0, a1, a2, a3, atlasUv, light01, tint, tris);

            Vector3 b0 = origin + new Vector3(min.x, min.y, max.z);
            Vector3 b1 = origin + new Vector3(max.x, min.y, min.z);
            Vector3 b2 = origin + new Vector3(max.x, max.y, min.z);
            Vector3 b3 = origin + new Vector3(min.x, max.y, max.z);
            AddDoubleSidedShapeQuad(b0, b1, b2, b3, atlasUv, light01, tint, tris);
        }

        private void AddCuboidShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            int voxelX,
            int voxelY,
            int voxelZ,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

            NativeList<int> tris = blockType == BlockType.Water
                ? waterTriangles
                : (mapping.isTransparent ? transparentTriangles : opaqueTriangles);
            byte emission = mapping.lightEmission;

            if (IsWallTorch(blockType))
            {
                float resolvedLight01 = math.max(light01, emission / 15f);
                AddWallTorchShape(origin, mapping, blockType, invAtlasTilesX, invAtlasTilesY, resolvedLight01, tris);
                return;
            }

            AddShapeFace(
                origin + new Vector3(max.x, min.y, min.z),
                origin + new Vector3(max.x, max.y, min.z),
                origin + new Vector3(max.x, max.y, max.z),
                origin + new Vector3(max.x, min.y, max.z),
                Vector3.right,
                mapping.side,
                mapping.tintSide,
                new Vector3Int(voxelX + 1, voxelY, voxelZ),
                Vector3Int.up,
                Vector3Int.forward,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(min.x, min.y, max.z),
                origin + new Vector3(min.x, max.y, max.z),
                origin + new Vector3(min.x, max.y, min.z),
                origin + new Vector3(min.x, min.y, min.z),
                Vector3.left,
                mapping.side,
                mapping.tintSide,
                new Vector3Int(voxelX, voxelY, voxelZ),
                Vector3Int.up,
                Vector3Int.forward,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(min.x, max.y, max.z),
                origin + new Vector3(max.x, max.y, max.z),
                origin + new Vector3(max.x, max.y, min.z),
                origin + new Vector3(min.x, max.y, min.z),
                Vector3.up,
                mapping.top,
                mapping.tintTop,
                new Vector3Int(voxelX, voxelY + 1, voxelZ),
                Vector3Int.right,
                Vector3Int.forward,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(min.x, min.y, min.z),
                origin + new Vector3(max.x, min.y, min.z),
                origin + new Vector3(max.x, min.y, max.z),
                origin + new Vector3(min.x, min.y, max.z),
                Vector3.down,
                mapping.bottom,
                mapping.tintBottom,
                new Vector3Int(voxelX, voxelY, voxelZ),
                Vector3Int.right,
                Vector3Int.forward,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(max.x, min.y, max.z),
                origin + new Vector3(max.x, max.y, max.z),
                origin + new Vector3(min.x, max.y, max.z),
                origin + new Vector3(min.x, min.y, max.z),
                Vector3.forward,
                mapping.side,
                mapping.tintSide,
                new Vector3Int(voxelX, voxelY, voxelZ + 1),
                Vector3Int.right,
                Vector3Int.up,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddShapeFace(
                origin + new Vector3(min.x, min.y, min.z),
                origin + new Vector3(min.x, max.y, min.z),
                origin + new Vector3(max.x, max.y, min.z),
                origin + new Vector3(max.x, min.y, min.z),
                Vector3.back,
                mapping.side,
                mapping.tintSide,
                new Vector3Int(voxelX, voxelY, voxelZ),
                Vector3Int.right,
                Vector3Int.up,
                emission,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);
        }

        private void AddWallTorchShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01,
            NativeList<int> tris)
        {
            ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

            Vector3 modelMin = new Vector3(min.x - 0.5f, min.y, min.z - 0.5f);
            Vector3 modelMax = new Vector3(max.x - 0.5f, max.y, max.z - 0.5f);

            Vector3 p000 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMin.x, modelMin.y, modelMin.z));
            Vector3 p001 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMin.x, modelMin.y, modelMax.z));
            Vector3 p010 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMin.x, modelMax.y, modelMin.z));
            Vector3 p011 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMin.x, modelMax.y, modelMax.z));
            Vector3 p100 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMax.x, modelMin.y, modelMin.z));
            Vector3 p101 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMax.x, modelMin.y, modelMax.z));
            Vector3 p110 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMax.x, modelMax.y, modelMin.z));
            Vector3 p111 = origin + TransformTorchModelPoint(blockType, new Vector3(modelMax.x, modelMax.y, modelMax.z));

            AddStaticLitShapeFace(p100, p110, p111, p101, mapping.side, mapping.tintSide, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddStaticLitShapeFace(p001, p011, p010, p000, mapping.side, mapping.tintSide, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddStaticLitShapeFace(p011, p111, p110, p010, mapping.top, mapping.tintTop, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddStaticLitShapeFace(p000, p100, p101, p001, mapping.bottom, mapping.tintBottom, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddStaticLitShapeFace(p101, p111, p011, p001, mapping.side, mapping.tintSide, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddStaticLitShapeFace(p000, p010, p110, p100, mapping.side, mapping.tintSide, light01, invAtlasTilesX, invAtlasTilesY, tris);
        }

        private void AddStaticLitShapeFace(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2Int tile,
            bool tint,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            int vIndex = vertices.Length;
            Vector3 normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
            if (normal.sqrMagnitude < 0.0001f)
                normal = Vector3.up;

            vertices.Add(p0);
            vertices.Add(p1);
            vertices.Add(p2);
            vertices.Add(p3);

            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);

            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(1f, 1f));
            uvs.Add(new Vector2(0f, 1f));

            Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
            uv2.Add(atlasUv);
            uv2.Add(atlasUv);
            uv2.Add(atlasUv);
            uv2.Add(atlasUv);

            Vector4 extra = new Vector4(light01, tint ? 1f : 0f, 1f, 0f);
            extraUVs.Add(extra);
            extraUVs.Add(extra);
            extraUVs.Add(extra);
            extraUVs.Add(extra);

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);
        }

        private static bool IsWallTorch(BlockType blockType)
        {
            return blockType == BlockType.WallTorchEast ||
                   blockType == BlockType.WallTorchWest ||
                   blockType == BlockType.WallTorchSouth ||
                   blockType == BlockType.WallTorchNorth;
        }

        private static Vector3 TransformTorchModelPoint(BlockType blockType, Vector3 modelPoint)
        {
            if (!IsWallTorch(blockType))
                return modelPoint + new Vector3(0.5f, 0f, 0.5f);

            const float angleRadians = 0.3926991f;
            const float anchorHeight = TorchPlacementUtility.WallAnchorHeight;
            const float anchorOffset = TorchPlacementUtility.WallAnchorOffset;

            float sin = math.sin(angleRadians);
            float cos = math.cos(angleRadians);

            Vector3 rotated = modelPoint;
            switch (blockType)
            {
                case BlockType.WallTorchEast:
                    rotated = new Vector3(
                        modelPoint.x * cos + modelPoint.y * sin,
                        -modelPoint.x * sin + modelPoint.y * cos,
                        modelPoint.z);
                    return rotated + new Vector3(0.5f - anchorOffset, anchorHeight, 0.5f);

                case BlockType.WallTorchWest:
                    rotated = new Vector3(
                        modelPoint.x * cos - modelPoint.y * sin,
                        modelPoint.x * sin + modelPoint.y * cos,
                        modelPoint.z);
                    return rotated + new Vector3(0.5f + anchorOffset, anchorHeight, 0.5f);

                case BlockType.WallTorchSouth:
                    rotated = new Vector3(
                        modelPoint.x,
                        modelPoint.y * cos - modelPoint.z * sin,
                        modelPoint.y * sin + modelPoint.z * cos);
                    return rotated + new Vector3(0.5f, anchorHeight, 0.5f - anchorOffset);

                case BlockType.WallTorchNorth:
                    rotated = new Vector3(
                        modelPoint.x,
                        modelPoint.y * cos + modelPoint.z * sin,
                        -modelPoint.y * sin + modelPoint.z * cos);
                    return rotated + new Vector3(0.5f, anchorHeight, 0.5f + anchorOffset);

                default:
                    return modelPoint + new Vector3(0.5f, 0f, 0.5f);
            }
        }

        private void AddShapeFace(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector3 normal,
            Vector2Int tile,
            bool tint,
            Vector3Int lightPlanePos,
            Vector3Int lightStepU,
            Vector3Int lightStepV,
            byte emission,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            int vIndex = vertices.Length;

            vertices.Add(p0);
            vertices.Add(p1);
            vertices.Add(p2);
            vertices.Add(p3);

            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);

            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(1f, 1f));
            uvs.Add(new Vector2(0f, 1f));

            Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
            uv2.Add(atlasUv);
            uv2.Add(atlasUv);
            uv2.Add(atlasUv);
            uv2.Add(atlasUv);

            for (int corner = 0; corner < 4; corner++)
            {
                Vector3Int stepU = (corner == 1 || corner == 2) ? lightStepU : -lightStepU;
                Vector3Int stepV = (corner == 2 || corner == 3) ? lightStepV : -lightStepV;
                byte vertexLight = GetVertexLight(lightPlanePos, stepU, stepV, light, SizeX + 2 * border, SizeZ + 2 * border, (SizeX + 2 * border) * SizeY);
                if (emission > 0)
                    vertexLight = (byte)math.max((int)vertexLight, (int)emission);

                Vector4 extra = new Vector4(vertexLight / 15f, tint ? 1f : 0f, 1f, 0f);
                extraUVs.Add(extra);
            }

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);
        }

        private void AddDoubleSidedShapeQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2 atlasUv,
            float light01,
            float tint,
            NativeList<int> tris)
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

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 3);
            tris.Add(vIndex + 2);
        }

        private float GetSpecialMeshLight01(int idx, int voxelSizeX, NativeArray<byte> light)
        {
            float light01 = GetResolvedLight01(light[idx]);
            int aboveIdx = idx + voxelSizeX;
            if (aboveIdx >= 0 && aboveIdx < light.Length)
                light01 = math.max(light01, GetResolvedLight01(light[aboveIdx]));

            return light01;
        }

        private static float GetResolvedLight01(byte packed)
        {
            byte lightValue = (byte)math.max(
                (int)LightUtils.GetSkyLight(packed),
                (int)LightUtils.GetBlockLight(packed));
            return lightValue / 15f;
        }

        private void ResolveShapeBounds(BlockTextureMapping mapping, out Vector3 min, out Vector3 max)
        {
            float3 clampedMin = math.clamp(
                new float3(mapping.shapeMin.x, mapping.shapeMin.y, mapping.shapeMin.z),
                0f,
                1f);
            float3 clampedMax = math.clamp(
                new float3(mapping.shapeMax.x, mapping.shapeMax.y, mapping.shapeMax.z),
                0f,
                1f);

            bool valid =
                clampedMax.x > clampedMin.x + 0.0001f &&
                clampedMax.y > clampedMin.y + 0.0001f &&
                clampedMax.z > clampedMin.z + 0.0001f;

            if (valid)
            {
                min = new Vector3(clampedMin.x, clampedMin.y, clampedMin.z);
                max = new Vector3(clampedMax.x, clampedMax.y, clampedMax.z);
                return;
            }

            switch (mapping.renderShape)
            {
                case BlockRenderShape.Cross:
                    min = new Vector3(0.15f, 0f, 0.15f);
                    max = new Vector3(0.85f, 1f, 0.85f);
                    return;

                case BlockRenderShape.Cuboid:
                    min = new Vector3(0.375f, 0f, 0.375f);
                    max = new Vector3(0.625f, 0.75f, 0.625f);
                    return;

                default:
                    min = Vector3.zero;
                    max = Vector3.one;
                    return;
            }
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

            bool neighborOpaque = blockMappings[(int)neighbor].renderShape == BlockRenderShape.Cube &&
                                  blockMappings[(int)neighbor].isSolid &&
                                  !blockMappings[(int)neighbor].isTransparent;
            return !neighborOpaque;
        }

        private void GenerateMesh(NativeArray<int> heightCache, NativeArray<BlockType> blockTypes, NativeArray<bool> solids, NativeArray<byte> light, float invAtlasTilesX, float invAtlasTilesY)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;

            int maxMask = math.max(voxelSizeX * SizeY, math.max(voxelSizeX * voxelSizeZ, SizeY * voxelSizeZ));
            NativeArray<GreedyFaceData> mask = new NativeArray<GreedyFaceData>(maxMask, Allocator.Temp);

            for (int axis = 0; axis < 3; axis++)
            {
                for (int side = 0; side < 2; side++)
                {
                    int normalSign = side == 0 ? 1 : -1;

                    int u = (axis + 1) % 3;
                    int v = (axis + 2) % 3;

                    int sizeU = u == 0 ? voxelSizeX : u == 1 ? SizeY : voxelSizeZ;
                    int sizeV = v == 0 ? voxelSizeX : v == 1 ? SizeY : voxelSizeZ;
                    int chunkSize = axis == 0 ? SizeX : axis == 1 ? SizeY : SizeZ;

                    int minN = axis == 1 ? startY : border;
                    int maxN = axis == 1 ? endY : border + chunkSize;

                    int minU = u == 1 ? startY : border;
                    int maxU = u == 1 ? endY : (u == 0 ? border + SizeX : border + SizeZ);

                    int minV = v == 1 ? startY : border;
                    int maxV = v == 1 ? endY : (v == 0 ? border + SizeX : border + SizeZ);

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
                                int x = u == 0 ? i : v == 0 ? j : n;
                                int y = u == 1 ? i : v == 1 ? j : n;
                                int z = u == 2 ? i : v == 2 ? j : n;

                                int maskIndex = i + j * sizeU;
                                int idx = x + y * voxelSizeX + z * voxelPlaneSize;
                                BlockType current = blockTypes[idx];

                                if (current == BlockType.Air)
                                {
                                    mask[maskIndex] = default;
                                    continue;
                                }

                                BlockTextureMapping currentMapping = blockMappings[(int)current];
                                if (currentMapping.renderShape != BlockRenderShape.Cube)
                                {
                                    mask[maskIndex] = default;
                                    continue;
                                }

                                int nx = x + (axis == 0 ? normalSign : 0);
                                int ny = y + (axis == 1 ? normalSign : 0);
                                int nz = z + (axis == 2 ? normalSign : 0);

                                bool outside = nx < 0 || nx >= voxelSizeX || ny < 0 || ny >= SizeY || nz < 0 || nz >= voxelSizeZ;
                                bool isVisible;
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

                                if (!isVisible)
                                {
                                    mask[maskIndex] = default;
                                    continue;
                                }

                                byte packed = outside
                                    ? LightUtils.PackLight(15, 0)
                                    : light[nx + ny * voxelSizeX + nz * voxelPlaneSize];

                                byte faceLight = (byte)math.max(
                                    (int)LightUtils.GetSkyLight(packed),
                                    (int)LightUtils.GetBlockLight(packed)
                                );

                                int aoPlaneN = n + normalSign;
                                Vector3Int aoPos = new Vector3Int(
                                    axis == 0 ? aoPlaneN : x,
                                    axis == 1 ? aoPlaneN : y,
                                    axis == 2 ? aoPlaneN : z
                                );

                                byte ao0;
                                byte ao1;
                                byte ao2;
                                byte ao3;
                                bool disableAOForCurrentBlock = aoStrength <= 0f || IsEmissiveBlock(currentMapping);
                                if (disableAOForCurrentBlock)
                                {
                                    ao0 = 3;
                                    ao1 = 3;
                                    ao2 = 3;
                                    ao3 = 3;
                                }
                                else
                                {
                                    ao0 = GetVertexAO(aoPos, -stepU, -stepV, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    ao1 = GetVertexAO(aoPos, stepU, -stepV, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    ao2 = GetVertexAO(aoPos, stepU, stepV, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    ao3 = GetVertexAO(aoPos, -stepU, stepV, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                }

                                mask[maskIndex] = new GreedyFaceData
                                {
                                    blockId = (int)current,
                                    valid = 1,
                                    faceLight = faceLight,
                                    ao0 = ao0,
                                    ao1 = ao1,
                                    ao2 = ao2,
                                    ao3 = ao3
                                };
                            }
                        }

                        for (int j = minV; j < maxV; j++)
                        {
                            int i = minU;
                            while (i < maxU)
                            {
                                GreedyFaceData startFace = mask[i + j * sizeU];
                                if (!HasFace(startFace))
                                {
                                    i++;
                                    continue;
                                }

                                int w = 1;
                                while (i + w < maxU && CanMergeAlongU(mask[i + w - 1 + j * sizeU], mask[i + w + j * sizeU]))
                                    w++;

                                int h = 1;
                                while (j + h < maxV)
                                {
                                    bool canGrow = true;
                                    for (int k = 0; k < w; k++)
                                    {
                                        GreedyFaceData candidate = mask[i + k + (j + h) * sizeU];
                                        if (!HasFace(candidate) ||
                                            !CanMergeAlongV(mask[i + k + (j + h - 1) * sizeU], candidate) ||
                                            (k > 0 && !CanMergeAlongU(mask[i + k - 1 + (j + h) * sizeU], candidate)))
                                        {
                                            canGrow = false;
                                            break;
                                        }
                                    }

                                    if (!canGrow)
                                        break;

                                    h++;
                                }

                                int maxW = w;
                                int maxH = h;
                                int bestW = 1;
                                int bestH = 1;
                                int bestArea = 1;
                                bool flipTriangle = (startFace.ao0 + startFace.ao2) > (startFace.ao1 + startFace.ao3);

                                // Keep the largest sub-rectangle whose AO still fits one quad.
                                for (int testH = 1; testH <= maxH; testH++)
                                {
                                    for (int testW = 1; testW <= maxW; testW++)
                                    {
                                        int area = testW * testH;
                                        if (area < bestArea || (area == bestArea && testW <= bestW))
                                            continue;

                                        if (!TryGetRepresentableRectFlip(mask, sizeU, i, j, testW, testH, out bool candidateFlip))
                                            continue;

                                        bestW = testW;
                                        bestH = testH;
                                        bestArea = area;
                                        flipTriangle = candidateFlip;
                                    }
                                }

                                w = bestW;
                                h = bestH;

                                GreedyFaceData bottomLeftFace = mask[i + j * sizeU];
                                GreedyFaceData bottomRightFace = mask[i + (w - 1) + j * sizeU];
                                GreedyFaceData topRightFace = mask[i + (w - 1) + (j + h - 1) * sizeU];
                                GreedyFaceData topLeftFace = mask[i + (j + h - 1) * sizeU];

                                BlockType bt = (BlockType)bottomLeftFace.blockId;
                                byte finalLight = bottomLeftFace.faceLight;

                                byte ao0 = bottomLeftFace.ao0;
                                byte ao1 = bottomRightFace.ao1;
                                byte ao2 = topRightFace.ao2;
                                byte ao3 = topLeftFace.ao3;

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

                                    if (bt == BlockType.Water && axis == 1 && normalSign > 0)
                                        py -= 0.15f;

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
                                    Vector3Int vertexPlanePos = GetFaceVertexPlanePos(axis, u, v, n, normalSign, i, j, du, dv);
                                    Vector3Int lightStepU = (l == 1 || l == 2) ? stepU : -stepU;
                                    Vector3Int lightStepV = (l == 2 || l == 3) ? stepV : -stepV;
                                    byte vertexLight = GetVertexLight(vertexPlanePos, lightStepU, lightStepV, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                                    if (IsEmissiveBlock(m))
                                        vertexLight = (byte)math.max((int)vertexLight, (int)m.lightEmission);

                                    float rawLight = math.max(finalLight / 15f, vertexLight / 15f);
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

                                NativeList<int> tris = bt == BlockType.Water ? waterTriangles :
                                                       blockMappings[(int)bt].isTransparent ? transparentTriangles : opaqueTriangles;

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
                                {
                                    for (int x0 = 0; x0 < w; x0++)
                                        mask[i + x0 + (j + y0) * sizeU] = default;
                                }

                                i += w;
                            }
                        }
                    }
                }
            }

            mask.Dispose();
        }
        private bool IsOccluder(int x, int y, int z, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            if (!solids[idx])
                return false;

            BlockType blockType = blockTypes[idx];
            BlockTextureMapping mapping = blockMappings[(int)blockType];
            return CastsAmbientOcclusion(blockType, mapping);
        }

        private byte GetVertexAO(Vector3Int pos, Vector3Int d1, Vector3Int d2, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            bool s1 = IsOccluder(pos.x + d1.x, pos.y + d1.y, pos.z + d1.z, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            bool s2 = IsOccluder(pos.x + d2.x, pos.y + d2.y, pos.z + d2.z, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            bool c = IsOccluder(pos.x + d1.x + d2.x, pos.y + d1.y + d2.y, pos.z + d1.z + d2.z, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            if (s1 && s2) return 0;
            return (byte)(3 - (s1 ? 1 : 0) - (s2 ? 1 : 0) - (c ? 1 : 0));
        }

        private static bool CastsAmbientOcclusion(BlockType blockType, BlockTextureMapping mapping)
        {
            // AO deve vir de cubos cheios que realmente fecham a iluminaÃ§Ã£o ambiente.
            // Folhas sÃ£o a exceÃ§Ã£o: mesmo transparentes, devem sombrear como no Minecraft.
            if (mapping.renderShape != BlockRenderShape.Cube ||
                mapping.isEmpty ||
                mapping.isLiquid ||
                mapping.lightOpacity == 0)
            {
                return false;
            }

            return !mapping.isTransparent || blockType == BlockType.Leaves;
        }

        private byte GetVertexLight(Vector3Int pos, Vector3Int d1, Vector3Int d2, NativeArray<byte> light, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            int l0 = SampleLightValue(pos, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            int l1 = SampleLightValue(pos + d1, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            int l2 = SampleLightValue(pos + d2, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            int l3 = SampleLightValue(pos + d1 + d2, light, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            return (byte)((l0 + l1 + l2 + l3 + 2) / 4);
        }

        private int SampleLightValue(Vector3Int pos, NativeArray<byte> light, int voxelSizeX, int voxelSizeZ, int voxelPlaneSize)
        {
            if (pos.y < 0)
                return 0;
            if (pos.y >= SizeY)
                return 15;
            if (pos.x < 0 || pos.x >= voxelSizeX || pos.z < 0 || pos.z >= voxelSizeZ)
                return 15;

            int idx = pos.x + pos.y * voxelSizeX + pos.z * voxelPlaneSize;
            byte packed = light[idx];
            return math.max((int)LightUtils.GetSkyLight(packed), (int)LightUtils.GetBlockLight(packed));
        }

        private static bool IsEmissiveBlock(BlockTextureMapping mapping)
        {
            return mapping.isLightSource || mapping.lightEmission > 0;
        }
    }


    [BurstCompile]
    public struct DisposeChunkDataJob : IJob
    {
        [DeallocateOnJobCompletion] public NativeArray<int> heightCache;
        [DeallocateOnJobCompletion] public NativeArray<BlockType> blockTypes;
        [DeallocateOnJobCompletion] public NativeArray<bool> solids;
        [DeallocateOnJobCompletion] public NativeArray<byte> light;
        [DeallocateOnJobCompletion] public NativeArray<bool> subchunkNonEmpty; // â† NOVO
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












