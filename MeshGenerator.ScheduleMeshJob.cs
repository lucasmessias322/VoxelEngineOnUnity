using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public static partial class MeshGenerator
{
    public static void ScheduleMeshJob(
        NativeArray<int> heightCache,
        NativeArray<byte> blockTypes,
        NativeArray<byte> blockPlacementAxes,
        NativeArray<bool> solids,
        NativeArray<ushort> light,
        NativeArray<BlockTextureMapping> nativeBlockMappings,
        NativeArray<BlockModelCuboid> nativeBlockModelCuboids,
        NativeArray<int3> suppressedGrassBillboards,
        NativeArray<bool> subchunkNonEmpty,
        NativeArray<byte> knownVoxelData,
        bool useKnownVoxelData,
        int atlasTilesX,
        int atlasTilesY,
        bool generateSides,
        int borderSize,
        int chunkCoordX,
        int chunkCoordZ,
        int dirtySubchunkMask,
        bool enableGrassBillboards,
        float grassBillboardChance,
        BlockType grassBillboardBlockType,
        float grassBillboardHeight,
        float grassBillboardNoiseScale,
        float grassBillboardJitter,
        NativeArray<VegetationBillboardRuleData> vegetationBillboardRules,
        BiomeNoiseSettings biomeNoiseSettings,
        float aoStrength,
        float aoCurveExponent,
        float aoMinLight,
        bool useFastBedrockStyleMeshing,
        bool enableHighQualityLeafFoliage,
        bool enableUltraLeafBillboards,
        float leafFoliageSpawnChance,
        float leafFoliageHeightMin,
        float leafFoliageHeightMax,
        float leafFoliageHalfWidthMin,
        float leafFoliageHalfWidthMax,
        float leafFoliageBaseYOffsetMin,
        float leafFoliageBaseYOffsetMax,
        float leafFoliageCenterJitter,
        float leafUltraBillboardHeight,
        float leafUltraBillboardHalfWidth,
        float leafUltraBaseYOffset,
        float leafUltraCenterJitter,
        float leafUltraRotationOffsetDegrees,
        float leafUltraRotationRandomDegrees,
        float leafUltraFaceTiltDegrees,
        float leafUltraFaceTiltRandomDegrees,
        out JobHandle meshHandle,
        out NativeList<PackedChunkVertex> vertices,
        out NativeList<int> opaqueTriangles,
        out NativeList<int> transparentTriangles,
        out NativeList<int> billboardTriangles,
        out NativeList<int> waterTriangles,
        out NativeArray<SubchunkMeshRange> subchunkRanges,
        out NativeArray<ulong> subchunkVisibilityMasks
    )
    {
        // 1. AlocaÃƒÆ’Ã‚Â§ÃƒÆ’Ã‚Âµes das Listas de Mesh (Output)
        vertices = RentMeshVertexList(4096);
        opaqueTriangles = RentMeshIndexList(4096 * 3);
        waterTriangles = RentMeshIndexList(4096 * 3);
        transparentTriangles = RentMeshIndexList(4096 * 3);
        billboardTriangles = RentMeshIndexList(2048 * 3);
        subchunkRanges = RentSubchunkRangeBuffer(SubchunksPerColumn);
        subchunkVisibilityMasks = RentUlongBuffer(SubchunksPerColumn);

        // ==========================================
        // JOB 2: GeraÃƒÆ’Ã‚Â§ÃƒÆ’Ã‚Â£o da Malha (Mesh)
        // ==========================================
        var meshJob = new ChunkMeshJob
        {
            startY = 0,
            endY = 0,
            blockTypes = blockTypes,
            blockPlacementAxes = blockPlacementAxes,
            solids = solids,
            light = light, // Usa a luz previamente calculada e passada por parÃƒÆ’Ã‚Â¢metro
            heightCache = heightCache,
            blockMappings = nativeBlockMappings,
            blockModelCuboids = nativeBlockModelCuboids,
            suppressedGrassBillboards = suppressedGrassBillboards,
            subchunkNonEmpty = subchunkNonEmpty,
            knownVoxelData = knownVoxelData,
            useKnownVoxelData = useKnownVoxelData,

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
            vegetationBillboardRules = vegetationBillboardRules,
            biomeNoiseSettings = biomeNoiseSettings,
            aoStrength = aoStrength,
            aoCurveExponent = aoCurveExponent,
            aoMinLight = aoMinLight,
            useFastBedrockStyleMeshing = useFastBedrockStyleMeshing,
            enableHighQualityLeafFoliage = enableHighQualityLeafFoliage,
            enableUltraLeafBillboards = enableUltraLeafBillboards,
            leafFoliageSpawnChance = leafFoliageSpawnChance,
            leafFoliageHeightMin = leafFoliageHeightMin,
            leafFoliageHeightMax = leafFoliageHeightMax,
            leafFoliageHalfWidthMin = leafFoliageHalfWidthMin,
            leafFoliageHalfWidthMax = leafFoliageHalfWidthMax,
            leafFoliageBaseYOffsetMin = leafFoliageBaseYOffsetMin,
            leafFoliageBaseYOffsetMax = leafFoliageBaseYOffsetMax,
            leafFoliageCenterJitter = leafFoliageCenterJitter,
            leafUltraBillboardHeight = leafUltraBillboardHeight,
            leafUltraBillboardHalfWidth = leafUltraBillboardHalfWidth,
            leafUltraBaseYOffset = leafUltraBaseYOffset,
            leafUltraCenterJitter = leafUltraCenterJitter,
            leafUltraRotationOffsetDegrees = leafUltraRotationOffsetDegrees,
            leafUltraRotationRandomDegrees = leafUltraRotationRandomDegrees,
            leafUltraFaceTiltDegrees = leafUltraFaceTiltDegrees,
            leafUltraFaceTiltRandomDegrees = leafUltraFaceTiltRandomDegrees,
            dirtySubchunkMask = dirtySubchunkMask,
            subchunkRanges = subchunkRanges,

            vertices = vertices,
            opaqueTriangles = opaqueTriangles,
            waterTriangles = waterTriangles,
            transparentTriangles = transparentTriangles,
            billboardTriangles = billboardTriangles,
            subchunkVisibilityMasks = subchunkVisibilityMasks
        };
        // O MeshJob agora ÃƒÆ’Ã‚Â© agendado independentemente, assumindo que os dados intermediÃƒÆ’Ã‚Â¡rios jÃƒÆ’Ã‚Â¡ estÃƒÆ’Ã‚Â£o prontos
        meshHandle = meshJob.Schedule();
    }

    public static void ScheduleRelightJob(
        NativeList<PackedChunkVertex> vertices,
        NativeArray<ushort> blockLight,
        int borderSize,
        out JobHandle relightHandle)
    {
        if (!vertices.IsCreated || vertices.Length == 0 || !blockLight.IsCreated)
        {
            relightHandle = default;
            return;
        }

        int voxelSizeX = Chunk.SizeX + 2 * borderSize;
        int voxelSizeZ = Chunk.SizeZ + 2 * borderSize;
        var relightJob = new ChunkRelightJob
        {
            vertices = vertices.AsArray(),
            blockLight = blockLight,
            border = borderSize,
            voxelSizeX = voxelSizeX,
            voxelSizeZ = voxelSizeZ,
            voxelPlaneSize = voxelSizeX * Chunk.SizeY
        };

        relightHandle = relightJob.Schedule(vertices.Length, 128);
    }

    [BurstCompile]
    private struct ChunkRelightJob : IJobParallelFor
    {
        public NativeArray<PackedChunkVertex> vertices;
        [ReadOnly] public NativeArray<ushort> blockLight;
        public int border;
        public int voxelSizeX;
        public int voxelSizeZ;
        public int voxelPlaneSize;

        public void Execute(int index)
        {
            PackedChunkVertex vertex = vertices[index];
            ushort packedBlockLight = SampleVertexBlockLight(vertex.position, vertex.normal);

            Vector4 uv3 = vertex.uv3;
            uv3.z = GetBlockLight01(packedBlockLight);
            vertex.uv3 = uv3;
            vertex.blockLightColor = LightUtils.EncodeBlockLightColor32(packedBlockLight);

            vertices[index] = vertex;
        }

        private ushort SampleVertexBlockLight(Vector3 position, Vector3 normal)
        {
            int sampleX = GetBiasedSampleX(position.x, normal.x);
            int sampleY = GetBiasedSampleY(position.y, normal.y);
            int sampleZ = GetBiasedSampleZ(position.z, normal.z);

            ushort packed = SampleBlockLight(sampleX, sampleY, sampleZ);
            if (!IsAxisAlignedNormal(normal))
            {
                packed = LightUtils.MaxPackedLight(packed, SampleBlockLight(sampleX, sampleY + 1, sampleZ));
                packed = LightUtils.MaxPackedLight(packed, SampleBlockLight(sampleX, sampleY - 1, sampleZ));
                packed = LightUtils.MaxPackedLight(packed, SampleBlockLight(sampleX + 1, sampleY, sampleZ));
                packed = LightUtils.MaxPackedLight(packed, SampleBlockLight(sampleX - 1, sampleY, sampleZ));
                packed = LightUtils.MaxPackedLight(packed, SampleBlockLight(sampleX, sampleY, sampleZ + 1));
                packed = LightUtils.MaxPackedLight(packed, SampleBlockLight(sampleX, sampleY, sampleZ - 1));
            }

            return LightUtils.GetBlockLightPacked(packed);
        }

        private int GetBiasedSampleX(float x, float normalX)
        {
            return (int)math.floor(x + border + normalX * 0.5f + 0.0001f);
        }

        private static int GetBiasedSampleY(float y, float normalY)
        {
            return (int)math.floor(y + normalY * 0.5f + 0.0001f);
        }

        private int GetBiasedSampleZ(float z, float normalZ)
        {
            return (int)math.floor(z + border + normalZ * 0.5f + 0.0001f);
        }

        private ushort SampleBlockLight(int x, int y, int z)
        {
            if (y < 0 || y >= Chunk.SizeY)
                return 0;

            int clampedX = math.clamp(x, 0, voxelSizeX - 1);
            int clampedZ = math.clamp(z, 0, voxelSizeZ - 1);
            int idx = clampedX + y * voxelSizeX + clampedZ * voxelPlaneSize;
            if ((uint)idx >= (uint)blockLight.Length)
                return 0;

            return blockLight[idx];
        }

        private static bool IsAxisAlignedNormal(Vector3 normal)
        {
            float ax = math.abs(normal.x);
            float ay = math.abs(normal.y);
            float az = math.abs(normal.z);
            float dominant = math.max(ax, math.max(ay, az));
            return dominant >= 0.9f;
        }

        private static float GetBlockLight01(ushort packedLight)
        {
            return LightUtils.GetBlockLight(packedLight) / 15f;
        }
    }
}
