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
    private partial struct ChunkMeshJob
    {
        private void GenerateDecorativeMeshes(
            NativeArray<byte> blockTypes,
            NativeArray<ushort> light,
            float invAtlasTilesX,
            float invAtlasTilesY)
        {
            int voxelSizeX = SizeX + 2 * border;
            int voxelSizeZ = SizeZ + 2 * border;
            int voxelPlaneSize = voxelSizeX * SizeY;
            bool generateGrassBillboards = enableGrassBillboards && grassBillboardChance > 0f;
            bool generateHighLeafFoliage = enableHighQualityLeafFoliage;
            bool generateUltraLeafFoliage = enableUltraLeafBillboards;
            bool generateAnyLeafFoliage = generateHighLeafFoliage || generateUltraLeafFoliage;
            float noiseScale = 0f;
            float jitter = 0f;
            Vector2 leavesAtlasUv = default;
            Vector2 leavesAtlasSize = new Vector2(invAtlasTilesX, invAtlasTilesY);
            float leavesTint = 0f;
            if (generateGrassBillboards)
            {
                noiseScale = math.max(1e-4f, grassBillboardNoiseScale);
                jitter = math.clamp(grassBillboardJitter, 0f, 0.35f);
            }

            if (generateAnyLeafFoliage)
            {
                int leavesMappingIndex = (int)BlockType.Leaves;
                if ((uint)leavesMappingIndex >= (uint)blockMappings.Length)
                {
                    generateHighLeafFoliage = false;
                    generateUltraLeafFoliage = false;
                }
                else
                {
                    BlockTextureMapping leavesMapping = blockMappings[leavesMappingIndex];
                    if (leavesMapping.isEmpty)
                    {
                        generateHighLeafFoliage = false;
                        generateUltraLeafFoliage = false;
                    }
                    else
                    {
                        ResolveAtlasRect(leavesMapping, BlockFace.Front, invAtlasTilesX, invAtlasTilesY, out leavesAtlasUv, out leavesAtlasSize);
                        leavesTint = leavesMapping.GetTint(BlockFace.Front) ? 1f : 0f;
                    }
                }
            }

            int minY = math.max(startY - 1, 0);
            int maxY = math.min(endY - 1, SizeY - 1);

            for (int y = minY; y <= maxY; y++)
            {
                for (int z = border; z < border + SizeZ; z++)
                {
                    for (int x = border; x < border + SizeX; x++)
                    {
                        int idx = x + y * voxelSizeX + z * voxelPlaneSize;
                        BlockType blockType = (BlockType)blockTypes[idx];

                        if (y >= startY && blockType != BlockType.Air)
                        {
                            BlockTextureMapping mapping = blockMappings[(int)blockType];
                            BlockRenderShape effectiveShape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
                            if (!mapping.isEmpty && !mapping.renderAsDynamicPrefab && effectiveShape != BlockRenderShape.Cube)
                            {
                                currentSubchunkSupportsLightingOnlyRebuild = false;
                                activeSpecialMeshLightChannels = GetSpecialMeshLightChannels01(x, y, z, voxelSizeX, voxelSizeZ, light);
                                float specialLight01 = GetResolvedLight01(activeSpecialMeshLightChannels);
                                Vector3 origin = new Vector3(x - border, y, z - border);
                                byte rawPlacementData = GetBlockPlacementAxisValue(idx);
                                BlockPlacementAxis placementAxis = blockType == BlockType.wire &&
                                                                   WirePlacementUtility.TryGetWall(rawPlacementData, out BlockPlacementAxis explicitWireAxis, out _)
                                    ? explicitWireAxis
                                    : BlockPlacementRotationUtility.SanitizeStoredAxis((BlockPlacementAxis)rawPlacementData);
                                placementAxis = ResolveConveyorRenderPlacementAxis(
                                    blockTypes,
                                    blockType,
                                    placementAxis,
                                    x,
                                    y,
                                    z,
                                    voxelSizeX,
                                    voxelSizeZ,
                                    voxelPlaneSize);

                                switch (effectiveShape)
                                {
                                    case BlockRenderShape.Cross:
                                        AddCrossShape(origin, mapping, blockType, invAtlasTilesX, invAtlasTilesY, specialLight01);
                                        break;

                                    case BlockRenderShape.Cuboid:
                                        AddCuboidShape(origin, mapping, blockType, placementAxis, x, y, z, invAtlasTilesX, invAtlasTilesY, specialLight01);
                                        break;

                                    case BlockRenderShape.MultiCuboid:
                                        AddMultiCuboidShape(
                                            origin,
                                            mapping,
                                            blockType,
                                            placementAxis,
                                            x,
                                            y,
                                            z,
                                            voxelSizeX,
                                            voxelSizeZ,
                                            voxelPlaneSize,
                                            invAtlasTilesX,
                                            invAtlasTilesY,
                                            specialLight01);
                                        break;

                                    case BlockRenderShape.Plane:
                                        AddPlaneShape(
                                            origin,
                                            mapping,
                                            blockType,
                                            placementAxis,
                                            rawPlacementData,
                                            x,
                                            y,
                                            z,
                                            blockTypes,
                                            voxelSizeX,
                                            voxelSizeZ,
                                            voxelPlaneSize,
                                            invAtlasTilesX,
                                            invAtlasTilesY,
                                            specialLight01);
                                        break;

                                    case BlockRenderShape.Stairs:
                                        AddStairShape(
                                            origin,
                                            mapping,
                                            rawPlacementData,
                                            x,
                                            y,
                                            z,
                                            blockTypes,
                                            voxelSizeX,
                                            voxelSizeZ,
                                            voxelPlaneSize,
                                            invAtlasTilesX,
                                            invAtlasTilesY,
                                            specialLight01);
                                        break;

                                    case BlockRenderShape.Ramp:
                                        AddRampShape(
                                            origin,
                                            mapping,
                                            (BlockPlacementAxis)rawPlacementData,
                                            x,
                                            y,
                                            z,
                                            voxelSizeX,
                                            voxelSizeZ,
                                            voxelPlaneSize,
                                            invAtlasTilesX,
                                            invAtlasTilesY,
                                            specialLight01);
                                        break;

                                    case BlockRenderShape.VerticalRamp:
                                        AddVerticalRampShape(
                                            origin,
                                            mapping,
                                            placementAxis,
                                            x,
                                            y,
                                            z,
                                            voxelSizeX,
                                            voxelSizeZ,
                                            voxelPlaneSize,
                                            invAtlasTilesX,
                                            invAtlasTilesY,
                                            specialLight01);
                                        break;

                                    case BlockRenderShape.Fence:
                                        AddFenceShape(
                                            origin,
                                            mapping,
                                            false,
                                            x,
                                            y,
                                            z,
                                            blockTypes,
                                            voxelSizeX,
                                            voxelSizeZ,
                                            voxelPlaneSize,
                                            invAtlasTilesX,
                                            invAtlasTilesY,
                                            specialLight01);
                                        break;

                                    case BlockRenderShape.Fence2:
                                        AddFenceShape(
                                            origin,
                                            mapping,
                                            true,
                                            x,
                                            y,
                                            z,
                                            blockTypes,
                                            voxelSizeX,
                                            voxelSizeZ,
                                            voxelPlaneSize,
                                            invAtlasTilesX,
                                            invAtlasTilesY,
                                            specialLight01);
                                        break;

                                    case BlockRenderShape.Slab:
                                        AddSlabShape(
                                            origin,
                                            mapping,
                                            placementAxis,
                                            invAtlasTilesX,
                                            invAtlasTilesY,
                                            specialLight01);
                                        break;
                                }
                            }
                        }

                        if ((generateHighLeafFoliage || generateUltraLeafFoliage) &&
                            y >= startY &&
                            blockType == BlockType.Leaves)
                        {
                            currentSubchunkSupportsLightingOnlyRebuild = false;
                            if (generateUltraLeafFoliage)
                            {
                                AddUltraLeafBillboardFoliage(
                                    x,
                                    y,
                                    z,
                                    light,
                                    voxelSizeX,
                                    voxelSizeZ,
                                    leavesAtlasUv,
                                    leavesAtlasSize,
                                    leavesTint);
                            }
                            else
                            {
                                TryAddHighQualityLeafFoliage(
                                    x,
                                    y,
                                    z,
                                    blockTypes,
                                    light,
                                    voxelSizeX,
                                    voxelSizeZ,
                                    voxelPlaneSize,
                                    leavesAtlasUv,
                                    leavesAtlasSize,
                                    leavesTint);
                            }
                        }

                        if (!generateGrassBillboards || y + 1 >= SizeY)
                        {
                            continue;
                        }

                        int py = y + 1;
                        if (py < startY || py >= endY)
                            continue;

                        int upIdx = idx + voxelSizeX;
                        if (blockTypes[upIdx] != (byte)BlockType.Air)
                            continue;

                        int worldX = chunkCoordX * SizeX + (x - border);
                        int worldZ = chunkCoordZ * SizeZ + (z - border);
                        if (!TryResolveVegetationBillboardRule(blockType, worldX, py, worldZ, noiseScale, out BlockType billboardBlockType, out uint variationHash))
                            continue;

                        currentSubchunkSupportsLightingOnlyRebuild = false;
                        int billboardMappingIndex = (int)billboardBlockType;
                        if ((uint)billboardMappingIndex >= (uint)blockMappings.Length)
                            continue;

                        BlockTextureMapping billboardMapping = blockMappings[billboardMappingIndex];
                        ResolveAtlasRect(billboardMapping, BlockFace.Front, invAtlasTilesX, invAtlasTilesY, out Vector2 billboardAtlasUv, out Vector2 billboardAtlasSize);
                        float billboardTint = billboardMapping.GetTint(BlockFace.Front) ? 1f : 0f;

                        float jx = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 8, jitter);
                        float jz = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 16, jitter);
                        float billboardHeight = VegetationBillboardUtility.ComputeHeight(grassBillboardHeight, variationHash);
                        float billboardHalfWidth = VegetationBillboardUtility.ComputeHalfWidth(variationHash);
                        float centerYOffset = VegetationBillboardUtility.ComputeBaseYOffset(variationHash);
                        VoxelLightChannels billboardLight = GetSpecialMeshLightChannels01(x, py, z, voxelSizeX, voxelSizeZ, light);
                        Vector3 center = new Vector3((x - border) + 0.5f + jx, py + centerYOffset, (z - border) + 0.5f + jz);
                        AddBillboardCross(center, billboardHeight, billboardHalfWidth, billboardAtlasUv, billboardAtlasSize, billboardLight.sky, billboardLight.block, billboardLight.blockLightColor, billboardTint);
                    }
                }
            }
        }

    }
}
