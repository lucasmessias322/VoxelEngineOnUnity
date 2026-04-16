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
        NativeArray<byte> light,
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
}
