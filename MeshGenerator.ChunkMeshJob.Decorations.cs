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
        private struct ShapeFaceRect
        {
            public BlockFace face;
            public float plane;
            public float minA;
            public float maxA;
            public float minB;
            public float maxB;
            public int tileX;
            public int tileY;
            public bool tint;
            public bool usesExplicitAppearance;
            public Vector4 explicitUvRectData;
        }

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
                            if (!mapping.isEmpty && effectiveShape != BlockRenderShape.Cube)
                            {
                                float specialLight01 = GetSpecialMeshLight01(x, y, z, voxelSizeX, voxelSizeZ, light);
                                Vector3 origin = new Vector3(x - border, y, z - border);
                                byte rawPlacementData = GetBlockPlacementAxisValue(idx);
                                BlockPlacementAxis placementAxis = blockType == BlockType.wire &&
                                                                   WirePlacementUtility.TryGetWall(rawPlacementData, out BlockPlacementAxis explicitWireAxis, out _)
                                    ? explicitWireAxis
                                    : BlockPlacementRotationUtility.SanitizeStoredAxis((BlockPlacementAxis)rawPlacementData);

                                switch (effectiveShape)
                                {
                                    case BlockRenderShape.Cross:
                                        AddCrossShape(origin, mapping, blockType, invAtlasTilesX, invAtlasTilesY, specialLight01);
                                        break;

                                    case BlockRenderShape.Cuboid:
                                        AddCuboidShape(origin, mapping, blockType, x, y, z, invAtlasTilesX, invAtlasTilesY, specialLight01);
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
                                }
                            }
                        }

                        if ((generateHighLeafFoliage || generateUltraLeafFoliage) &&
                            y >= startY &&
                            blockType == BlockType.Leaves)
                        {
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

        private void TryAddHighQualityLeafFoliage(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            NativeArray<ushort> light,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float tint)
        {
            int worldX = chunkCoordX * SizeX + (x - border);
            int worldZ = chunkCoordZ * SizeZ + (z - border);
            uint variationHash = VegetationBillboardUtility.ComputeVariantHash(worldX, y, worldZ);
            float chanceRoll = (variationHash & 0x0000FFFFu) / 65535f;
            float spawnChance = math.saturate(leafFoliageSpawnChance);
            if (chanceRoll > spawnChance)
                return;

            float heightMin = math.max(0.2f, math.min(leafFoliageHeightMin, leafFoliageHeightMax));
            float heightMax = math.max(heightMin, math.max(leafFoliageHeightMin, leafFoliageHeightMax));
            float halfWidthMin = math.max(0.5f, math.min(leafFoliageHalfWidthMin, leafFoliageHalfWidthMax));
            float halfWidthMax = math.max(halfWidthMin, math.max(leafFoliageHalfWidthMin, leafFoliageHalfWidthMax));
            float baseYOffsetMin = math.min(leafFoliageBaseYOffsetMin, leafFoliageBaseYOffsetMax);
            float baseYOffsetMax = math.max(leafFoliageBaseYOffsetMin, leafFoliageBaseYOffsetMax);
            float centerJitter = math.max(0f, leafFoliageCenterJitter);

            float jx = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 11, centerJitter);
            float jz = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 17, centerJitter);
            float baseYOffset = math.lerp(baseYOffsetMin, baseYOffsetMax, ((variationHash >> 16) & 0xFFu) / 255f);
            float height = math.lerp(heightMin, heightMax, ((variationHash >> 24) & 0xFFu) / 255f);
            float halfWidth = math.lerp(halfWidthMin, halfWidthMax, ((variationHash >> 8) & 0xFFu) / 255f);

            VoxelLightChannels billboardLight = GetSpecialMeshLightChannels01(x, y, z, voxelSizeX, voxelSizeZ, light);
            // Mantem o centro do billboard no centro do voxel para as quads cruzarem pelas diagonais do bloco.
            Vector3 center = new Vector3((x - border) + 0.5f + jx, y + baseYOffset, (z - border) + 0.5f + jz);
            AddBillboardCross(center, height, halfWidth, atlasUv, atlasSize, billboardLight.sky, billboardLight.block, billboardLight.blockLightColor, tint);
        }

        private void AddUltraLeafBillboardFoliage(
            int x,
            int y,
            int z,
            NativeArray<ushort> light,
            int voxelSizeX,
            int voxelSizeZ,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float tint)
        {
            int worldX = chunkCoordX * SizeX + (x - border);
            int worldZ = chunkCoordZ * SizeZ + (z - border);
            uint variationHash = VegetationBillboardUtility.ComputeVariantHash(worldX, y, worldZ);

            float centerJitter = math.max(0f, leafUltraCenterJitter);
            float jx = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 5, centerJitter);
            float jz = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 13, centerJitter);
            float height = math.max(0.4f, leafUltraBillboardHeight);
            float halfWidth = math.max(0.5f, leafUltraBillboardHalfWidth);
            float baseYOffset = math.clamp(leafUltraBaseYOffset, -0.4f, 0.4f);
            float baseRotationDeg = math.clamp(leafUltraRotationOffsetDegrees, 0f, 45f);
            float randomRotationRangeDeg = math.max(0f, leafUltraRotationRandomDegrees);
            float baseTiltDeg = math.clamp(leafUltraFaceTiltDegrees, 0f, 60f);
            float randomTiltRangeDeg = math.max(0f, leafUltraFaceTiltRandomDegrees);
            float random01 = ((variationHash >> 20) & 0x3FFu) / 1023f;
            float rotationDeg = baseRotationDeg + (random01 * 2f - 1f) * randomRotationRangeDeg;

            VoxelLightChannels billboardLight = GetSpecialMeshLightChannels01(x, y, z, voxelSizeX, voxelSizeZ, light);
            // Pivot no centro do voxel para manter volume aparente de qualquer angulo.
            Vector3 center = new Vector3((x - border) + 0.5f + jx, y + 0.5f + baseYOffset, (z - border) + 0.5f + jz);
            AddBillboardFourFaces(center, height, halfWidth, atlasUv, atlasSize, billboardLight.sky, billboardLight.block, billboardLight.blockLightColor, tint, rotationDeg, baseTiltDeg, randomTiltRangeDeg, variationHash);
        }

        private void AddBillboardFourFaces(
            Vector3 center,
            float height,
            float halfWidth,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float skyLight01,
            float blockLight01,
            uint blockLightColor,
            float tint,
            float rotationOffsetDegrees,
            float tiltBaseDegrees,
            float tiltRandomDegrees,
            uint variationHash)
        {
            float baseRad = math.radians(rotationOffsetDegrees);
            for (int i = 0; i < 4; i++)
            {
                float angle = baseRad + i * (math.PI * 0.25f);
                Vector2 dir = new Vector2(math.cos(angle), math.sin(angle));

                float tiltNoise = ((variationHash >> (i * 8)) & 0xFFu) / 255f;
                float tiltDegrees = tiltBaseDegrees + (tiltNoise * 2f - 1f) * tiltRandomDegrees;
                if ((i & 1) != 0)
                    tiltDegrees = -tiltDegrees;

                AddBillboardPlane(center, height, halfWidth, dir, tiltDegrees, atlasUv, atlasSize, skyLight01, blockLight01, blockLightColor, tint);
            }
        }

        private void AddBillboardPlane(
            Vector3 center,
            float height,
            float halfWidth,
            Vector2 horizontalDirectionXZ,
            float tiltDegrees,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float skyLight01,
            float blockLight01,
            uint blockLightColor,
            float tint)
        {
            Vector3 right = new Vector3(horizontalDirectionXZ.x, 0f, horizontalDirectionXZ.y) * halfWidth;
            Vector3 up = Vector3.up;

            if (math.abs(tiltDegrees) > 0.001f)
            {
                float3 axis = math.normalize(new float3(horizontalDirectionXZ.x, 0f, horizontalDirectionXZ.y));
                quaternion tilt = quaternion.AxisAngle(axis, math.radians(tiltDegrees));
                float3 tiltedUp = math.mul(tilt, new float3(0f, 1f, 0f));
                up = new Vector3(tiltedUp.x, tiltedUp.y, tiltedUp.z);
            }

            Vector3 halfUp = up * (height * 0.5f);
            Vector3 p0 = center - right - halfUp;
            Vector3 p1 = center + right - halfUp;
            Vector3 p2 = center + right + halfUp;
            Vector3 p3 = center - right + halfUp;
            AddDoubleSidedQuad(p0, p1, p2, p3, atlasUv, atlasSize, skyLight01, blockLight01, blockLightColor, tint);
        }

        private bool TryResolveVegetationBillboardRule(
            BlockType groundBlockType,
            int worldX,
            int worldY,
            int worldZ,
            float noiseScale,
            out BlockType billboardBlockType,
            out uint variationHash)
        {
            billboardBlockType = BlockType.Air;
            variationHash = 0u;

            if (IsSuppressedGrassBillboard(worldX, worldY, worldZ))
                return false;

            return VegetationBillboardUtility.TryResolveBillboardRule(
                biomeNoiseSettings,
                vegetationBillboardRules,
                worldX,
                worldY,
                worldZ,
                groundBlockType,
                grassBillboardChance,
                noiseScale,
                grassBillboardBlockType,
                out billboardBlockType,
                out variationHash);
        }

        private void AddBillboardCross(Vector3 center, float height, float halfWidth, Vector2 atlasUv, Vector2 atlasSize, float skyLight01, float blockLight01, uint blockLightColor, float tint)
        {
            Vector3 a0 = center + new Vector3(-halfWidth, 0f, -halfWidth);
            Vector3 a1 = center + new Vector3(halfWidth, 0f, halfWidth);
            Vector3 a2 = a1 + new Vector3(0f, height, 0f);
            Vector3 a3 = a0 + new Vector3(0f, height, 0f);
            AddDoubleSidedQuad(a0, a1, a2, a3, atlasUv, atlasSize, skyLight01, blockLight01, blockLightColor, tint);

            Vector3 b0 = center + new Vector3(-halfWidth, 0f, halfWidth);
            Vector3 b1 = center + new Vector3(halfWidth, 0f, -halfWidth);
            Vector3 b2 = b1 + new Vector3(0f, height, 0f);
            Vector3 b3 = b0 + new Vector3(0f, height, 0f);
            AddDoubleSidedQuad(b0, b1, b2, b3, atlasUv, atlasSize, skyLight01, blockLight01, blockLightColor, tint);
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

        private static Vector3 ComputeQuadPlaneNormal(Vector3 p0, Vector3 p1, Vector3 p2)
        {
            float3 edgeA = new float3(p1.x - p0.x, p1.y - p0.y, p1.z - p0.z);
            float3 edgeB = new float3(p2.x - p0.x, p2.y - p0.y, p2.z - p0.z);
            float3 n = math.cross(edgeA, edgeB);
            float lenSq = math.lengthsq(n);
            if (lenSq <= 1e-8f)
                return new Vector3(0f, 1f, 0f);

            float invLen = math.rsqrt(lenSq);
            n *= invLen;
            return new Vector3(n.x, n.y, n.z);
        }

        private void AddDoubleSidedQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2 atlasUv,
            Vector2 atlasSize,
            float skyLight01,
            float blockLight01,
            uint blockLightColor,
            float tint)
        {
            int vIndex = GetCurrentSubchunkLocalVertexIndex();
            Vector3 planeNormal = ComputeQuadPlaneNormal(p0, p1, p2);

            Vector4 e = new Vector4(math.saturate(skyLight01), tint, 1f, 0f);
            Vector4 atlasAndBlockLight = new Vector4(atlasSize.x, atlasSize.y, math.saturate(blockLight01), 0f);
            AddPackedVertex(p0, planeNormal, new Vector2(0f, 0f), atlasUv, e, atlasAndBlockLight, blockLightColor);
            AddPackedVertex(p1, planeNormal, new Vector2(1f, 0f), atlasUv, e, atlasAndBlockLight, blockLightColor);
            AddPackedVertex(p2, planeNormal, new Vector2(1f, 1f), atlasUv, e, atlasAndBlockLight, blockLightColor);
            AddPackedVertex(p3, planeNormal, new Vector2(0f, 1f), atlasUv, e, atlasAndBlockLight, blockLightColor);

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

            ResolveAtlasRect(mapping, BlockFace.Front, invAtlasTilesX, invAtlasTilesY, out Vector2 atlasUv, out Vector2 atlasSize);
            float tint = mapping.GetTint(BlockFace.Front) ? 1f : 0f;

            NativeList<int> tris = FluidBlockUtility.IsWater(blockType)
                ? waterTriangles
                : (mapping.isTransparent ? transparentTriangles : opaqueTriangles);

            Vector3 a0 = origin + new Vector3(min.x, min.y, min.z);
            Vector3 a1 = origin + new Vector3(max.x, min.y, max.z);
            Vector3 a2 = origin + new Vector3(max.x, max.y, max.z);
            Vector3 a3 = origin + new Vector3(min.x, max.y, min.z);
            AddDoubleSidedShapeQuad(a0, a1, a2, a3, atlasUv, atlasSize, light01, tint, tris);

            Vector3 b0 = origin + new Vector3(min.x, min.y, max.z);
            Vector3 b1 = origin + new Vector3(max.x, min.y, min.z);
            Vector3 b2 = origin + new Vector3(max.x, max.y, min.z);
            Vector3 b3 = origin + new Vector3(min.x, max.y, max.z);
            AddDoubleSidedShapeQuad(b0, b1, b2, b3, atlasUv, atlasSize, light01, tint, tris);
        }

        private void AddPlaneShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            BlockPlacementAxis placementAxis,
            byte rawPlacementData,
            int voxelX,
            int voxelY,
            int voxelZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            ResolvePlaneSupportFlags(
                voxelX,
                voxelY,
                voxelZ,
                placementAxis,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                out bool hasNegativeSupport,
                out bool hasPositiveSupport);

            BlockShapeUtility.ResolvePlaneQuad(
                mapping,
                placementAxis,
                hasNegativeSupport,
                hasPositiveSupport,
                out Vector3 p0,
                out Vector3 p1,
                out Vector3 p2,
                out Vector3 p3,
                out BlockFace sampledFace,
                out _);

            ResolveAtlasRect(mapping, sampledFace, invAtlasTilesX, invAtlasTilesY, out Vector2 atlasUv, out Vector2 atlasSize);
            float tint = mapping.GetTint(sampledFace) ? 1f : 0f;

            NativeList<int> tris = FluidBlockUtility.IsWater(blockType)
                ? waterTriangles
                : (mapping.isTransparent ? transparentTriangles : opaqueTriangles);

            if (blockType == BlockType.wire)
            {
                bool wireHasTopOnCurrentCell = WirePlacementUtility.HasTop(rawPlacementData);
                bool wireHasWallOnCurrentCell = WirePlacementUtility.TryGetWall(rawPlacementData, out BlockPlacementAxis wireSurfaceAxis, out int wireAttachmentSide);
                if (!wireHasWallOnCurrentCell)
                {
                    BlockPlacementAxis resolvedSurfaceAxis = ResolveWireSurfaceAxis(placementAxis, hasNegativeSupport, hasPositiveSupport);
                    if (resolvedSurfaceAxis == BlockPlacementAxis.Y)
                    {
                        wireHasTopOnCurrentCell = true;
                    }
                    else
                    {
                        wireHasWallOnCurrentCell = true;
                        wireSurfaceAxis = resolvedSurfaceAxis;
                        wireAttachmentSide = ResolveWireAttachmentSide(resolvedSurfaceAxis, hasNegativeSupport, hasPositiveSupport);
                    }
                }

                ResolveAtlasRect(mapping, BlockFace.Front, invAtlasTilesX, invAtlasTilesY, out Vector2 lineAtlasUv, out Vector2 lineAtlasSize);
                ResolveAtlasRect(mapping, BlockFace.Back, invAtlasTilesX, invAtlasTilesY, out Vector2 shortLineAtlasUv, out Vector2 shortLineAtlasSize);
                ResolveAtlasRect(mapping, BlockFace.Top, invAtlasTilesX, invAtlasTilesY, out Vector2 dotAtlasUv, out Vector2 dotAtlasSize);

                if (wireHasTopOnCurrentCell)
                {
                    BlockShapeUtility.ResolvePlaneQuad(
                        mapping,
                        BlockPlacementAxis.Y,
                        false,
                        false,
                        out Vector3 topP0,
                        out Vector3 topP1,
                        out Vector3 topP2,
                        out Vector3 topP3,
                        out _,
                        out _);

                    OffsetWireTopQuad(ref topP0, ref topP1, ref topP2, ref topP3);

                    RenderWireTopSurface(
                        origin,
                        topP0,
                        topP1,
                        topP2,
                        topP3,
                        wireHasWallOnCurrentCell ? wireSurfaceAxis : BlockPlacementAxis.Y,
                        wireHasWallOnCurrentCell ? wireAttachmentSide : 0,
                        lineAtlasUv,
                        lineAtlasSize,
                        shortLineAtlasUv,
                        shortLineAtlasSize,
                        dotAtlasUv,
                        dotAtlasSize,
                        light01,
                        tint,
                        tris,
                        voxelX,
                        voxelY,
                        voxelZ,
                        blockTypes,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize);
                }

                if (wireHasWallOnCurrentCell)
                {
                    bool wallHasNegativeSupport = wireAttachmentSide < 0;
                    bool wallHasPositiveSupport = wireAttachmentSide > 0;

                    BlockShapeUtility.ResolvePlaneQuad(
                        mapping,
                        wireSurfaceAxis,
                        wallHasNegativeSupport,
                        wallHasPositiveSupport,
                        out Vector3 wallP0,
                        out Vector3 wallP1,
                        out Vector3 wallP2,
                        out Vector3 wallP3,
                        out _,
                        out _);

                    OffsetWireWallQuad(ref wallP0, ref wallP1, ref wallP2, ref wallP3, wireSurfaceAxis, wireAttachmentSide);

                    RenderWireWallSurface(
                        origin,
                        wallP0,
                        wallP1,
                        wallP2,
                        wallP3,
                        wireSurfaceAxis,
                        wireAttachmentSide,
                        wireHasTopOnCurrentCell,
                        lineAtlasUv,
                        lineAtlasSize,
                        shortLineAtlasUv,
                        shortLineAtlasSize,
                        dotAtlasUv,
                        dotAtlasSize,
                        light01,
                        tint,
                        tris,
                        voxelX,
                        voxelY,
                        voxelZ,
                        blockTypes,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize);
                }

                return;
            }

            AddDoubleSidedShapeQuad(
                origin + p0,
                origin + p1,
                origin + p2,
                origin + p3,
                atlasUv,
                atlasSize,
                light01,
                tint,
                tris);
        }

        private void AddStairShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            byte rawPlacementData,
            int voxelX,
            int voxelY,
            int voxelZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            StairShapeVariant variant = ResolveStairShapeVariant(rawPlacementData, voxelX, voxelY, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            FixedList512Bytes<ShapeBox> shapeBoxes = BuildStairVisualBoxes(rawPlacementData, variant);
            NativeList<int> tris = mapping.isTransparent ? transparentTriangles : opaqueTriangles;
            AddAmbientOccludedShapeBoxes(
                origin,
                mapping,
                light01,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                shapeBoxes);
        }

        private void AddRampShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockPlacementAxis placementAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            BlockPlacementAxis rampAxis = RampShapeUtility.SanitizeAxis(placementAxis);
            RampShapeVariant rampVariant = ResolveRampShapeVariant(
                rampAxis,
                voxelX,
                voxelY,
                voxelZ,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);
            NativeList<int> tris = mapping.isTransparent ? transparentTriangles : opaqueTriangles;

            RampShapeUtility.ResolveBottomQuad(rampAxis, out Vector3 bottom0, out Vector3 bottom1, out Vector3 bottom2, out Vector3 bottom3);
            AddAmbientOccludedCustomQuad(
                origin,
                mapping,
                BlockFace.Bottom,
                bottom0,
                bottom1,
                bottom2,
                bottom3,
                ResolveShapeProjectedUv(BlockFace.Bottom, bottom0),
                ResolveShapeProjectedUv(BlockFace.Bottom, bottom1),
                ResolveShapeProjectedUv(BlockFace.Bottom, bottom2),
                ResolveShapeProjectedUv(BlockFace.Bottom, bottom3),
                Vector3.down,
                Vector3.down,
                Vector3.right,
                Vector3.forward,
                light01,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                BlockRenderShape.Ramp,
                rampAxis,
                rampVariant,
                false);

            RampShapeUtility.ResolveTopTriangles(rampAxis, rampVariant, out Vector3 top0a, out Vector3 top0b, out Vector3 top0c, out Vector3 top1a, out Vector3 top1b, out Vector3 top1c);
            Vector3 topNormal0 = Vector3.Normalize(Vector3.Cross(top0b - top0a, top0c - top0a));
            AddAmbientOccludedCustomTriangle(
                origin,
                mapping,
                BlockFace.Top,
                top0a,
                top0b,
                top0c,
                ResolveShapeProjectedUv(BlockFace.Top, top0a),
                ResolveShapeProjectedUv(BlockFace.Top, top0b),
                ResolveShapeProjectedUv(BlockFace.Top, top0c),
                topNormal0,
                topNormal0,
                (top0b - top0a).normalized,
                (top0c - top0a).normalized,
                light01,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                BlockRenderShape.Ramp,
                rampAxis,
                rampVariant,
                true);

            Vector3 topNormal1 = Vector3.Normalize(Vector3.Cross(top1b - top1a, top1c - top1a));
            AddAmbientOccludedCustomTriangle(
                origin,
                mapping,
                BlockFace.Top,
                top1a,
                top1b,
                top1c,
                ResolveShapeProjectedUv(BlockFace.Top, top1a),
                ResolveShapeProjectedUv(BlockFace.Top, top1b),
                ResolveShapeProjectedUv(BlockFace.Top, top1c),
                topNormal1,
                topNormal1,
                (top1b - top1a).normalized,
                (top1c - top1a).normalized,
                light01,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                BlockRenderShape.Ramp,
                rampAxis,
                rampVariant,
                true);

            AppendRampEdgeSurface(origin, mapping, rampAxis, rampVariant, RampEdge.Left, light01, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, tris);
            AppendRampEdgeSurface(origin, mapping, rampAxis, rampVariant, RampEdge.Right, light01, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, tris);
            AppendRampEdgeSurface(origin, mapping, rampAxis, rampVariant, RampEdge.Front, light01, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, tris);
            AppendRampEdgeSurface(origin, mapping, rampAxis, rampVariant, RampEdge.Back, light01, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, invAtlasTilesX, invAtlasTilesY, tris);
        }

        private void AppendRampEdgeSurface(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockPlacementAxis rampAxis,
            RampShapeVariant rampVariant,
            RampEdge edge,
            float light01,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            if (!RampShapeUtility.ResolveEdgeSurface(rampAxis, rampVariant, edge, out int vertexCount, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3, out BlockFace sampledFace))
                return;

            Vector3 normal = ResolveShapeFaceNormal(sampledFace);
            if (vertexCount == 4)
            {
                AddAmbientOccludedCustomQuad(
                    origin,
                    mapping,
                    sampledFace,
                    p0,
                    p1,
                    p2,
                    p3,
                    ResolveShapeProjectedUv(sampledFace, p0),
                    ResolveShapeProjectedUv(sampledFace, p1),
                    ResolveShapeProjectedUv(sampledFace, p2),
                    ResolveShapeProjectedUv(sampledFace, p3),
                    normal,
                    normal,
                    (p1 - p0).normalized,
                    (p3 - p0).normalized,
                    light01,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    tris,
                    BlockRenderShape.Ramp,
                    rampAxis,
                    rampVariant,
                    false);
                return;
            }

            AddAmbientOccludedCustomTriangle(
                origin,
                mapping,
                sampledFace,
                p0,
                p1,
                p2,
                ResolveShapeProjectedUv(sampledFace, p0),
                ResolveShapeProjectedUv(sampledFace, p1),
                ResolveShapeProjectedUv(sampledFace, p2),
                normal,
                normal,
                (p1 - p0).normalized,
                (p2 - p0).normalized,
                light01,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                BlockRenderShape.Ramp,
                rampAxis,
                rampVariant);
        }

        private RampShapeVariant ResolveRampShapeVariant(
            BlockPlacementAxis placementAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (!RampShapeUtility.TryGetFacing(placementAxis, out StairFacing currentFacing))
                return RampShapeVariant.Straight;

            Vector3Int frontOffset = StairPlacementUtility.ToOffset(currentFacing);
            Vector3Int backOffset = StairPlacementUtility.ToOffset(StairPlacementUtility.Opposite(currentFacing));

            bool hasFrontNeighbor = TryGetNeighborRampFacing(
                voxelX + frontOffset.x,
                voxelY + frontOffset.y,
                voxelZ + frontOffset.z,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                out StairFacing frontFacing);

            bool hasBackNeighbor = TryGetNeighborRampFacing(
                voxelX + backOffset.x,
                voxelY + backOffset.y,
                voxelZ + backOffset.z,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                out StairFacing backFacing);

            return RampShapeUtility.ResolveShapeVariant(
                currentFacing,
                hasFrontNeighbor,
                frontFacing,
                hasBackNeighbor,
                backFacing);
        }

        private bool TryGetNeighborRampFacing(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            out StairFacing facing)
        {
            facing = StairFacing.North;

            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            BlockType neighborType = (BlockType)blockTypes[idx];
            int mapIndex = (int)neighborType;
            if ((uint)mapIndex >= (uint)blockMappings.Length)
                return false;

            if (BlockShapeUtility.GetEffectiveRenderShape(blockMappings[mapIndex]) != BlockRenderShape.Ramp)
                return false;

            return RampShapeUtility.TryGetFacing(BlockPlacementRotationUtility.SanitizeStoredAxis(GetBlockPlacementAxisValue(idx)), out facing);
        }

        private void AddVerticalRampShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockPlacementAxis placementAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            BlockPlacementAxis axis = VerticalRampShapeUtility.SanitizeAxis(placementAxis);
            NativeList<int> tris = mapping.isTransparent ? transparentTriangles : opaqueTriangles;

            VerticalRampShapeUtility.ResolveBottomTriangle(axis, out Vector3 bottom0, out Vector3 bottom1, out Vector3 bottom2);
            AddAmbientOccludedCustomTriangle(
                origin,
                mapping,
                BlockFace.Bottom,
                bottom0,
                bottom1,
                bottom2,
                ResolveShapeProjectedUv(BlockFace.Bottom, bottom0),
                ResolveShapeProjectedUv(BlockFace.Bottom, bottom1),
                ResolveShapeProjectedUv(BlockFace.Bottom, bottom2),
                Vector3.down,
                Vector3.down,
                (bottom1 - bottom0).normalized,
                (bottom2 - bottom0).normalized,
                light01,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                BlockRenderShape.VerticalRamp,
                axis,
                RampShapeVariant.Straight);

            VerticalRampShapeUtility.ResolveTopTriangle(axis, out Vector3 top0, out Vector3 top1, out Vector3 top2);
            AddAmbientOccludedCustomTriangle(
                origin,
                mapping,
                BlockFace.Top,
                top0,
                top1,
                top2,
                ResolveShapeProjectedUv(BlockFace.Top, top0),
                ResolveShapeProjectedUv(BlockFace.Top, top1),
                ResolveShapeProjectedUv(BlockFace.Top, top2),
                Vector3.up,
                Vector3.up,
                (top1 - top0).normalized,
                (top2 - top0).normalized,
                light01,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                BlockRenderShape.VerticalRamp,
                axis,
                RampShapeVariant.Straight);

            VerticalRampShapeUtility.ResolveSideQuad(axis, out Vector3 side0, out Vector3 side1, out Vector3 side2, out Vector3 side3, out BlockFace sideFace);
            Vector3 sideNormal = ResolveShapeFaceNormal(sideFace);
            AddAmbientOccludedCustomQuad(
                origin,
                mapping,
                sideFace,
                side0,
                side1,
                side2,
                side3,
                ResolveShapeProjectedUv(sideFace, side0),
                ResolveShapeProjectedUv(sideFace, side1),
                ResolveShapeProjectedUv(sideFace, side2),
                ResolveShapeProjectedUv(sideFace, side3),
                sideNormal,
                sideNormal,
                (side1 - side0).normalized,
                (side3 - side0).normalized,
                light01,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                BlockRenderShape.VerticalRamp,
                axis,
                RampShapeVariant.Straight,
                false);

            VerticalRampShapeUtility.ResolveFrontQuad(axis, out Vector3 front0, out Vector3 front1, out Vector3 front2, out Vector3 front3, out BlockFace frontFace);
            Vector3 frontNormal = ResolveShapeFaceNormal(frontFace);
            AddAmbientOccludedCustomQuad(
                origin,
                mapping,
                frontFace,
                front0,
                front1,
                front2,
                front3,
                ResolveShapeProjectedUv(frontFace, front0),
                ResolveShapeProjectedUv(frontFace, front1),
                ResolveShapeProjectedUv(frontFace, front2),
                ResolveShapeProjectedUv(frontFace, front3),
                frontNormal,
                frontNormal,
                (front1 - front0).normalized,
                (front3 - front0).normalized,
                light01,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                BlockRenderShape.VerticalRamp,
                axis,
                RampShapeVariant.Straight,
                false);

            VerticalRampShapeUtility.ResolveSlopeQuad(axis, out Vector3 slope0, out Vector3 slope1, out Vector3 slope2, out Vector3 slope3, out BlockFace slopeFace, out Vector3 slopeNormal);
            AddAmbientOccludedCustomQuad(
                origin,
                mapping,
                slopeFace,
                slope0,
                slope1,
                slope2,
                slope3,
                ResolveShapeProjectedUv(slopeFace, slope0),
                ResolveShapeProjectedUv(slopeFace, slope1),
                ResolveShapeProjectedUv(slopeFace, slope2),
                ResolveShapeProjectedUv(slopeFace, slope3),
                slopeNormal,
                slopeNormal,
                (slope1 - slope0).normalized,
                (slope3 - slope0).normalized,
                light01,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                BlockRenderShape.VerticalRamp,
                axis,
                RampShapeVariant.Straight,
                false,
                true);
        }

        private StairShapeVariant ResolveStairShapeVariant(
            byte rawPlacementData,
            int voxelX,
            int voxelY,
            int voxelZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize)
        {
            if (!StairPlacementUtility.TryDecode(rawPlacementData, out StairFacing currentFacing, out bool topHalf))
                return StairShapeVariant.Straight;

            Vector3Int frontOffset = StairPlacementUtility.ToOffset(currentFacing);
            Vector3Int backOffset = StairPlacementUtility.ToOffset(StairPlacementUtility.Opposite(currentFacing));

            bool hasFrontNeighbor = TryGetNeighborStairState(
                voxelX + frontOffset.x,
                voxelY + frontOffset.y,
                voxelZ + frontOffset.z,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                out StairFacing frontFacing,
                out bool frontTopHalf);

            bool hasBackNeighbor = TryGetNeighborStairState(
                voxelX + backOffset.x,
                voxelY + backOffset.y,
                voxelZ + backOffset.z,
                blockTypes,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                out StairFacing backFacing,
                out bool backTopHalf);

            return StairShapeUtility.ResolveShapeVariant(
                currentFacing,
                topHalf,
                hasFrontNeighbor,
                frontFacing,
                frontTopHalf,
                hasBackNeighbor,
                backFacing,
                backTopHalf);
        }

        private bool TryGetNeighborStairState(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            out StairFacing facing,
            out bool topHalf)
        {
            facing = StairFacing.North;
            topHalf = false;

            if (x < 0 || x >= voxelSizeX || y < 0 || y >= SizeY || z < 0 || z >= voxelSizeZ)
                return false;

            int idx = x + y * voxelSizeX + z * voxelPlaneSize;
            BlockType neighborType = (BlockType)blockTypes[idx];
            int mapIndex = (int)neighborType;
            if ((uint)mapIndex >= (uint)blockMappings.Length)
                return false;

            if (BlockShapeUtility.GetEffectiveRenderShape(blockMappings[mapIndex]) != BlockRenderShape.Stairs)
                return false;

            return StairPlacementUtility.TryDecode(GetBlockPlacementAxisValue(idx), out facing, out topHalf);
        }

        private void AddFenceShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            int voxelX,
            int voxelY,
            int voxelZ,
            NativeArray<byte> blockTypes,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            byte connectionMask = FenceShapeUtility.ResolveConnectionMask(
                voxelX,
                voxelY,
                voxelZ,
                blockTypes,
                blockMappings,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);

            FixedList512Bytes<ShapeBox> shapeBoxes = BuildFenceVisualBoxes(connectionMask);
            NativeList<int> tris = mapping.isTransparent ? transparentTriangles : opaqueTriangles;
            AddAmbientOccludedShapeBoxes(
                origin,
                mapping,
                light01,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                shapeBoxes);
        }

        private FixedList512Bytes<ShapeBox> BuildStairVisualBoxes(byte rawPlacementData, StairShapeVariant variant)
        {
            StairShapeUtility.ResolveBoxes(
                rawPlacementData,
                variant,
                out int boxCount,
                out ShapeBox box0,
                out ShapeBox box1,
                out ShapeBox box2,
                out ShapeBox box3,
                out ShapeBox box4);

            FixedList512Bytes<ShapeBox> boxes = default;
            boxes.Add(box0);
            if (boxCount > 1) boxes.Add(box1);
            if (boxCount > 2) boxes.Add(box2);
            if (boxCount > 3) boxes.Add(box3);
            if (boxCount > 4) boxes.Add(box4);
            return boxes;
        }

        private FixedList512Bytes<ShapeBox> BuildFenceVisualBoxes(byte connectionMask)
        {
            FixedList512Bytes<ShapeBox> boxes = default;
            boxes.Add(FenceShapeUtility.GetCenterPostVisualBox());

            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectWest))
            {
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectWest, false));
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectWest, true));
            }

            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectEast))
            {
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectEast, false));
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectEast, true));
            }

            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectSouth))
            {
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectSouth, false));
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectSouth, true));
            }

            if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectNorth))
            {
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectNorth, false));
                boxes.Add(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectNorth, true));
            }

            return boxes;
        }

        private void AddFenceRailIfConnected(
            Vector3 origin,
            BlockTextureMapping mapping,
            byte connectionMask,
            byte directionFlag,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            if (!FenceShapeUtility.IsFenceConnectionActive(connectionMask, directionFlag))
                return;

            AddStaticLitShapeBox(origin, mapping, FenceShapeUtility.GetRailVisualBox(directionFlag, false), light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddStaticLitShapeBox(origin, mapping, FenceShapeUtility.GetRailVisualBox(directionFlag, true), light01, invAtlasTilesX, invAtlasTilesY, tris);
        }

        private void AddMultiCuboidShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            BlockPlacementAxis placementAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            float light01)
        {
            NativeList<int> tris = mapping.isTransparent ? transparentTriangles : opaqueTriangles;

            if (IsWallTorch(blockType))
            {
                float resolvedLight01 = math.max(light01, mapping.lightEmission / 15f);
                AddWallTorchMultiCuboidShape(
                    origin,
                    mapping,
                    blockType,
                    resolvedLight01,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    tris);
                return;
            }

            FixedList512Bytes<ShapeBox> shapeBoxes = default;
            FixedList4096Bytes<ShapeFaceRect> faceRects = default;

            int boxCount = GetNativeMultiCuboidBoxCount(mapping);
            bool appendedAnyCuboid = false;
            for (int i = 0; i < boxCount && i < 16; i++)
            {
                if (!TryGetNativeMultiCuboid(mapping, i, out BlockModelCuboid cuboid))
                    continue;

                appendedAnyCuboid = true;
                if (IsModelCuboidRotated(cuboid))
                {
                    AddRotatedMultiCuboid(
                        origin,
                        mapping,
                        cuboid,
                        placementAxis,
                        voxelX,
                        voxelY,
                        voxelZ,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize,
                        light01,
                        invAtlasTilesX,
                        invAtlasTilesY,
                        tris);
                    continue;
                }

                ShapeBox box = BlockShapeUtility.TransformShapeBoxForPlacement(cuboid.ToShapeBox(), mapping, placementAxis);
                shapeBoxes.Add(box);
                AppendModelCuboidFaceRects(ref faceRects, box, cuboid, mapping, placementAxis);
            }

            if (!appendedAnyCuboid)
            {
                ResolveShapeBounds(mapping, out Vector3 fallbackMin, out Vector3 fallbackMax);
                ShapeBox fallback = new ShapeBox(fallbackMin, fallbackMax);
                shapeBoxes.Add(fallback);
                AppendShapeFaceRects(ref faceRects, fallback);
            }

            if (shapeBoxes.Length == 0)
                return;

            CullHiddenShapeFaceRects(ref faceRects, shapeBoxes);
            MergeShapeFaceRects(ref faceRects);

            for (int i = 0; i < faceRects.Length; i++)
            {
                AddAmbientOccludedShapeRect(
                    origin,
                    mapping,
                    faceRects[i],
                    light01,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    tris,
                    shapeBoxes,
                    BlockRenderShape.MultiCuboid,
                    placementAxis,
                    RampShapeVariant.Straight);
            }
        }

        private void AddWallTorchMultiCuboidShape(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            int boxCount = GetNativeMultiCuboidBoxCount(mapping);
            bool appendedAnyCuboid = false;
            for (int i = 0; i < boxCount; i++)
            {
                if (!TryGetNativeMultiCuboid(mapping, i, out BlockModelCuboid cuboid))
                    continue;

                appendedAnyCuboid = true;
                AddWallTorchMultiCuboidFaces(origin, mapping, blockType, cuboid, light01, invAtlasTilesX, invAtlasTilesY, tris);
            }

            if (appendedAnyCuboid)
                return;

            ResolveShapeBounds(mapping, out Vector3 fallbackMin, out Vector3 fallbackMax);
            AddWallTorchMultiCuboidFaces(
                origin,
                mapping,
                blockType,
                new BlockModelCuboid(fallbackMin, fallbackMax),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);
        }

        private void AddWallTorchMultiCuboidFaces(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            BlockModelCuboid cuboid,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            ShapeBox box = cuboid.ToShapeBox();
            Vector3 center = (box.min + box.max) * 0.5f;
            bool hasCuboidRotation = IsModelCuboidRotated(cuboid);
            quaternion rotation = hasCuboidRotation
                ? CreateCuboidRotation(cuboid.eulerRotation)
                : quaternion.identity;

            AddWallTorchMultiCuboidFace(origin, mapping, blockType, cuboid, box, center, rotation, hasCuboidRotation, BlockFace.Right, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddWallTorchMultiCuboidFace(origin, mapping, blockType, cuboid, box, center, rotation, hasCuboidRotation, BlockFace.Left, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddWallTorchMultiCuboidFace(origin, mapping, blockType, cuboid, box, center, rotation, hasCuboidRotation, BlockFace.Top, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddWallTorchMultiCuboidFace(origin, mapping, blockType, cuboid, box, center, rotation, hasCuboidRotation, BlockFace.Bottom, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddWallTorchMultiCuboidFace(origin, mapping, blockType, cuboid, box, center, rotation, hasCuboidRotation, BlockFace.Front, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddWallTorchMultiCuboidFace(origin, mapping, blockType, cuboid, box, center, rotation, hasCuboidRotation, BlockFace.Back, light01, invAtlasTilesX, invAtlasTilesY, tris);
        }

        private void AddWallTorchMultiCuboidFace(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockType blockType,
            BlockModelCuboid cuboid,
            ShapeBox box,
            Vector3 center,
            quaternion rotation,
            bool hasCuboidRotation,
            BlockFace localFace,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            if (!cuboid.HasFace(localFace))
                return;

            ResolveCuboidFace(box, localFace, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3, out _);
            if (hasCuboidRotation)
            {
                p0 = RotateCuboidPoint(p0, center, rotation);
                p1 = RotateCuboidPoint(p1, center, rotation);
                p2 = RotateCuboidPoint(p2, center, rotation);
                p3 = RotateCuboidPoint(p3, center, rotation);
            }

            Vector3 t0 = TransformTorchVoxelPoint(blockType, p0);
            Vector3 t1 = TransformTorchVoxelPoint(blockType, p1);
            Vector3 t2 = TransformTorchVoxelPoint(blockType, p2);
            Vector3 t3 = TransformTorchVoxelPoint(blockType, p3);
            Vector3 transformedNormal = Vector3.Normalize(Vector3.Cross(t1 - t0, t2 - t0));
            if (transformedNormal.sqrMagnitude < 0.0001f)
                transformedNormal = Vector3.up;

            Vector2 uv0 = ResolveShapeProjectedUv(localFace, p0);
            Vector2 uv1 = ResolveShapeProjectedUv(localFace, p1);
            Vector2 uv2 = ResolveShapeProjectedUv(localFace, p2);
            Vector2 uv3 = ResolveShapeProjectedUv(localFace, p3);
            NormalizeProjectedQuadUv(ref uv0, ref uv1, ref uv2, ref uv3);

            Vector2Int tile = cuboid.GetTileCoord(localFace, mapping);
            bool tint = mapping.GetTint(localFace);
            Vector4 explicitUvRectData = cuboid.TryGetUvRectData(localFace, mapping, out Vector4 cuboidUvRectData)
                ? cuboidUvRectData
                : default;

            AddStaticLitCustomQuad(
                origin + t0,
                origin + t1,
                origin + t2,
                origin + t3,
                uv0,
                uv1,
                uv2,
                uv3,
                transformedNormal,
                tile,
                tint,
                explicitUvRectData,
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);
        }

        private void AddRotatedMultiCuboid(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockModelCuboid cuboid,
            BlockPlacementAxis placementAxis,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            ShapeBox box = cuboid.ToShapeBox();
            Vector3 center = (box.min + box.max) * 0.5f;
            quaternion rotation = CreateCuboidRotation(cuboid.eulerRotation);

            AddRotatedMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, placementAxis, BlockFace.Right, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddRotatedMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, placementAxis, BlockFace.Left, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddRotatedMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, placementAxis, BlockFace.Top, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddRotatedMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, placementAxis, BlockFace.Bottom, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddRotatedMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, placementAxis, BlockFace.Front, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris);
            AddRotatedMultiCuboidFace(origin, mapping, cuboid, box, center, rotation, placementAxis, BlockFace.Back, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, light01, invAtlasTilesX, invAtlasTilesY, tris);
        }

        private void AddRotatedMultiCuboidFace(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockModelCuboid cuboid,
            ShapeBox box,
            Vector3 center,
            quaternion rotation,
            BlockPlacementAxis placementAxis,
            BlockFace localFace,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            if (!cuboid.HasFace(localFace))
                return;

            ResolveCuboidFace(box, localFace, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3, out Vector3 normal);

            p0 = RotateCuboidPoint(p0, center, rotation);
            p1 = RotateCuboidPoint(p1, center, rotation);
            p2 = RotateCuboidPoint(p2, center, rotation);
            p3 = RotateCuboidPoint(p3, center, rotation);
            normal = RotateCuboidDirection(normal, rotation).normalized;

            p0 = BlockShapeUtility.TransformPointForPlacement(p0, mapping, placementAxis);
            p1 = BlockShapeUtility.TransformPointForPlacement(p1, mapping, placementAxis);
            p2 = BlockShapeUtility.TransformPointForPlacement(p2, mapping, placementAxis);
            p3 = BlockShapeUtility.TransformPointForPlacement(p3, mapping, placementAxis);
            normal = BlockShapeUtility.TransformDirectionForPlacement(normal, mapping, placementAxis).normalized;

            BlockFace worldFace = BlockShapeUtility.TransformFaceForPlacement(localFace, mapping, placementAxis);
            AddAmbientOccludedCustomQuad(
                origin,
                mapping,
                worldFace,
                p0,
                p1,
                p2,
                p3,
                ResolveShapeProjectedUv(worldFace, p0),
                ResolveShapeProjectedUv(worldFace, p1),
                ResolveShapeProjectedUv(worldFace, p2),
                ResolveShapeProjectedUv(worldFace, p3),
                normal,
                normal,
                (p1 - p0).normalized,
                (p3 - p0).normalized,
                light01,
                voxelX,
                voxelY,
                voxelZ,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize,
                invAtlasTilesX,
                invAtlasTilesY,
                tris,
                BlockRenderShape.MultiCuboid,
                placementAxis,
                RampShapeVariant.Straight,
                false,
                hasExplicitAppearance: true,
                explicitTile: cuboid.GetTileCoord(localFace, mapping),
                explicitTint: mapping.GetTint(localFace),
                explicitUvRectData: cuboid.TryGetUvRectData(localFace, mapping, out Vector4 cuboidUvRectData)
                    ? cuboidUvRectData
                    : default);
        }

        private int GetNativeMultiCuboidBoxCount(BlockTextureMapping mapping)
        {
            if (!blockModelCuboids.IsCreated || blockModelCuboids.Length == 0 || mapping.multiCuboidCount <= 0)
                return 0;

            if (mapping.multiCuboidStartIndex < 0 || mapping.multiCuboidStartIndex >= blockModelCuboids.Length)
                return 0;

            return math.min(mapping.multiCuboidCount, blockModelCuboids.Length - mapping.multiCuboidStartIndex);
        }

        private bool TryGetNativeMultiCuboid(BlockTextureMapping mapping, int localIndex, out BlockModelCuboid cuboid)
        {
            cuboid = default;
            int count = GetNativeMultiCuboidBoxCount(mapping);
            if (localIndex < 0 || localIndex >= count)
                return false;

            cuboid = blockModelCuboids[mapping.multiCuboidStartIndex + localIndex];
            return true;
        }

        private static bool IsModelCuboidRotated(BlockModelCuboid cuboid)
        {
            const float epsilon = 0.0001f;
            return math.abs(cuboid.eulerRotation.x) > epsilon ||
                   math.abs(cuboid.eulerRotation.y) > epsilon ||
                   math.abs(cuboid.eulerRotation.z) > epsilon;
        }

        private static quaternion CreateCuboidRotation(Vector3 eulerRotation)
        {
            return quaternion.EulerZXY(math.radians(new float3(eulerRotation.x, eulerRotation.y, eulerRotation.z)));
        }

        private static Vector3 RotateCuboidPoint(Vector3 point, Vector3 center, quaternion rotation)
        {
            float3 offset = new float3(point.x - center.x, point.y - center.y, point.z - center.z);
            float3 rotated = math.mul(rotation, offset);
            return new Vector3(center.x + rotated.x, center.y + rotated.y, center.z + rotated.z);
        }

        private static Vector3 RotateCuboidDirection(Vector3 direction, quaternion rotation)
        {
            float3 rotated = math.mul(rotation, new float3(direction.x, direction.y, direction.z));
            return new Vector3(rotated.x, rotated.y, rotated.z);
        }

        private static Vector3 TransformTorchVoxelPoint(BlockType blockType, Vector3 voxelPoint)
        {
            return TorchPlacementUtility.TransformVoxelPoint(blockType, voxelPoint);
        }

        private static ShapeBox GetRotatedMultiCuboidBounds(
            BlockModelCuboid cuboid,
            BlockTextureMapping mapping,
            BlockPlacementAxis placementAxis)
        {
            ShapeBox box = cuboid.ToShapeBox();
            Vector3 center = (box.min + box.max) * 0.5f;
            quaternion rotation = CreateCuboidRotation(cuboid.eulerRotation);
            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            EncapsulateRotatedMultiCuboidPoint(box.min.x, box.min.y, box.min.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.max.x, box.min.y, box.min.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.min.x, box.max.y, box.min.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.max.x, box.max.y, box.min.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.min.x, box.min.y, box.max.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.max.x, box.min.y, box.max.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.min.x, box.max.y, box.max.z, center, rotation, mapping, placementAxis, ref min, ref max);
            EncapsulateRotatedMultiCuboidPoint(box.max.x, box.max.y, box.max.z, center, rotation, mapping, placementAxis, ref min, ref max);

            return new ShapeBox(min, max);
        }

        private static void EncapsulateRotatedMultiCuboidPoint(
            float x,
            float y,
            float z,
            Vector3 center,
            quaternion rotation,
            BlockTextureMapping mapping,
            BlockPlacementAxis placementAxis,
            ref Vector3 min,
            ref Vector3 max)
        {
            Vector3 point = RotateCuboidPoint(new Vector3(x, y, z), center, rotation);
            point = BlockShapeUtility.TransformPointForPlacement(point, mapping, placementAxis);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        private static void ResolveCuboidFace(
            ShapeBox box,
            BlockFace face,
            out Vector3 p0,
            out Vector3 p1,
            out Vector3 p2,
            out Vector3 p3,
            out Vector3 normal)
        {
            Vector3 min = box.min;
            Vector3 max = box.max;
            switch (face)
            {
                case BlockFace.Right:
                    p0 = new Vector3(max.x, min.y, min.z);
                    p1 = new Vector3(max.x, max.y, min.z);
                    p2 = new Vector3(max.x, max.y, max.z);
                    p3 = new Vector3(max.x, min.y, max.z);
                    normal = Vector3.right;
                    return;

                case BlockFace.Left:
                    p0 = new Vector3(min.x, min.y, max.z);
                    p1 = new Vector3(min.x, max.y, max.z);
                    p2 = new Vector3(min.x, max.y, min.z);
                    p3 = new Vector3(min.x, min.y, min.z);
                    normal = Vector3.left;
                    return;

                case BlockFace.Top:
                    p0 = new Vector3(min.x, max.y, max.z);
                    p1 = new Vector3(max.x, max.y, max.z);
                    p2 = new Vector3(max.x, max.y, min.z);
                    p3 = new Vector3(min.x, max.y, min.z);
                    normal = Vector3.up;
                    return;

                case BlockFace.Bottom:
                    p0 = new Vector3(min.x, min.y, min.z);
                    p1 = new Vector3(max.x, min.y, min.z);
                    p2 = new Vector3(max.x, min.y, max.z);
                    p3 = new Vector3(min.x, min.y, max.z);
                    normal = Vector3.down;
                    return;

                case BlockFace.Front:
                    p0 = new Vector3(max.x, min.y, max.z);
                    p1 = new Vector3(max.x, max.y, max.z);
                    p2 = new Vector3(min.x, max.y, max.z);
                    p3 = new Vector3(min.x, min.y, max.z);
                    normal = Vector3.forward;
                    return;

                default:
                    p0 = new Vector3(min.x, min.y, min.z);
                    p1 = new Vector3(min.x, max.y, min.z);
                    p2 = new Vector3(max.x, max.y, min.z);
                    p3 = new Vector3(max.x, min.y, min.z);
                    normal = Vector3.back;
                    return;
            }
        }

        private FixedList512Bytes<ShapeBox> BuildNativeMultiCuboidShapeBoxes(
            BlockTextureMapping mapping,
            BlockPlacementAxis placementAxis)
        {
            FixedList512Bytes<ShapeBox> boxes = default;
            int boxCount = GetNativeMultiCuboidBoxCount(mapping);
            for (int i = 0; i < boxCount && i < 16; i++)
            {
                if (!TryGetNativeMultiCuboid(mapping, i, out BlockModelCuboid cuboid))
                    continue;

                ShapeBox box = IsModelCuboidRotated(cuboid)
                    ? GetRotatedMultiCuboidBounds(cuboid, mapping, placementAxis)
                    : BlockShapeUtility.TransformShapeBoxForPlacement(cuboid.ToShapeBox(), mapping, placementAxis);
                boxes.Add(box);
            }

            if (boxes.Length == 0)
            {
                ResolveShapeBounds(mapping, out Vector3 fallbackMin, out Vector3 fallbackMax);
                boxes.Add(new ShapeBox(fallbackMin, fallbackMax));
            }

            return boxes;
        }

        private void AddAmbientOccludedShapeBoxes(
            Vector3 origin,
            BlockTextureMapping mapping,
            float light01,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            in FixedList512Bytes<ShapeBox> shapeBoxes)
        {
            BlockRenderShape currentShape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
            FixedList4096Bytes<ShapeFaceRect> faceRects = default;
            for (int i = 0; i < shapeBoxes.Length; i++)
                AppendShapeFaceRects(ref faceRects, shapeBoxes[i]);

            CullHiddenShapeFaceRects(ref faceRects, shapeBoxes);
            MergeShapeFaceRects(ref faceRects);

            for (int i = 0; i < faceRects.Length; i++)
            {
                AddAmbientOccludedShapeRect(
                    origin,
                    mapping,
                    faceRects[i],
                    light01,
                    voxelX,
                    voxelY,
                    voxelZ,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    tris,
                    shapeBoxes,
                    currentShape,
                    BlockPlacementAxis.Y,
                    RampShapeVariant.Straight);
            }
        }

        private static void AppendShapeFaceRects(ref FixedList4096Bytes<ShapeFaceRect> faceRects, ShapeBox box)
        {
            AppendShapeFaceRect(ref faceRects, box, BlockFace.Right);
            AppendShapeFaceRect(ref faceRects, box, BlockFace.Left);
            AppendShapeFaceRect(ref faceRects, box, BlockFace.Top);
            AppendShapeFaceRect(ref faceRects, box, BlockFace.Bottom);
            AppendShapeFaceRect(ref faceRects, box, BlockFace.Front);
            AppendShapeFaceRect(ref faceRects, box, BlockFace.Back);
        }

        private static void AppendModelCuboidFaceRects(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            ShapeBox box,
            BlockModelCuboid cuboid,
            BlockTextureMapping mapping,
            BlockPlacementAxis placementAxis)
        {
            AppendModelCuboidFaceRect(ref faceRects, box, cuboid, mapping, placementAxis, BlockFace.Right);
            AppendModelCuboidFaceRect(ref faceRects, box, cuboid, mapping, placementAxis, BlockFace.Left);
            AppendModelCuboidFaceRect(ref faceRects, box, cuboid, mapping, placementAxis, BlockFace.Top);
            AppendModelCuboidFaceRect(ref faceRects, box, cuboid, mapping, placementAxis, BlockFace.Bottom);
            AppendModelCuboidFaceRect(ref faceRects, box, cuboid, mapping, placementAxis, BlockFace.Front);
            AppendModelCuboidFaceRect(ref faceRects, box, cuboid, mapping, placementAxis, BlockFace.Back);
        }

        private static void AppendModelCuboidFaceRect(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            ShapeBox box,
            BlockModelCuboid cuboid,
            BlockTextureMapping mapping,
            BlockPlacementAxis placementAxis,
            BlockFace localFace)
        {
            if (!cuboid.HasFace(localFace))
                return;

            BlockFace worldFace = BlockShapeUtility.TransformFaceForPlacement(localFace, mapping, placementAxis);
            Vector2Int tile = cuboid.GetTileCoord(localFace, mapping);
            bool tint = mapping.GetTint(localFace);
            Vector4 explicitUvRectData = default;
            if (cuboid.TryGetUvRectData(localFace, mapping, out Vector4 cuboidUvRectData))
                explicitUvRectData = cuboidUvRectData;

            AppendShapeFaceRect(ref faceRects, box, worldFace, tile, tint, true, explicitUvRectData);
        }

        private static void AppendShapeFaceRect(ref FixedList4096Bytes<ShapeFaceRect> faceRects, ShapeBox box, BlockFace face)
        {
            AppendShapeFaceRect(ref faceRects, box, face, default, false, false, default);
        }

        private static void AppendShapeFaceRect(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            ShapeBox box,
            BlockFace face,
            Vector2Int tile,
            bool tint,
            bool usesExplicitAppearance,
            Vector4 explicitUvRectData)
        {
            switch (face)
            {
                case BlockFace.Right:
                    faceRects.Add(new ShapeFaceRect
                    {
                        face = BlockFace.Right,
                        plane = box.max.x,
                        minA = box.min.y,
                        maxA = box.max.y,
                        minB = box.min.z,
                        maxB = box.max.z,
                        tileX = tile.x,
                        tileY = tile.y,
                        tint = tint,
                        usesExplicitAppearance = usesExplicitAppearance,
                        explicitUvRectData = explicitUvRectData
                    });
                    return;

                case BlockFace.Left:
                    faceRects.Add(new ShapeFaceRect
                    {
                        face = BlockFace.Left,
                        plane = box.min.x,
                        minA = box.min.y,
                        maxA = box.max.y,
                        minB = box.min.z,
                        maxB = box.max.z,
                        tileX = tile.x,
                        tileY = tile.y,
                        tint = tint,
                        usesExplicitAppearance = usesExplicitAppearance,
                        explicitUvRectData = explicitUvRectData
                    });
                    return;

                case BlockFace.Top:
                    faceRects.Add(new ShapeFaceRect
                    {
                        face = BlockFace.Top,
                        plane = box.max.y,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.z,
                        maxB = box.max.z,
                        tileX = tile.x,
                        tileY = tile.y,
                        tint = tint,
                        usesExplicitAppearance = usesExplicitAppearance,
                        explicitUvRectData = explicitUvRectData
                    });
                    return;

                case BlockFace.Bottom:
                    faceRects.Add(new ShapeFaceRect
                    {
                        face = BlockFace.Bottom,
                        plane = box.min.y,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.z,
                        maxB = box.max.z,
                        tileX = tile.x,
                        tileY = tile.y,
                        tint = tint,
                        usesExplicitAppearance = usesExplicitAppearance,
                        explicitUvRectData = explicitUvRectData
                    });
                    return;

                case BlockFace.Front:
                    faceRects.Add(new ShapeFaceRect
                    {
                        face = BlockFace.Front,
                        plane = box.max.z,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.y,
                        maxB = box.max.y,
                        tileX = tile.x,
                        tileY = tile.y,
                        tint = tint,
                        usesExplicitAppearance = usesExplicitAppearance,
                        explicitUvRectData = explicitUvRectData
                    });
                    return;

                case BlockFace.Back:
                    faceRects.Add(new ShapeFaceRect
                    {
                        face = BlockFace.Back,
                        plane = box.min.z,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.y,
                        maxB = box.max.y,
                        tileX = tile.x,
                        tileY = tile.y,
                        tint = tint,
                        usesExplicitAppearance = usesExplicitAppearance,
                        explicitUvRectData = explicitUvRectData
                    });
                    return;
            }
        }

        private static void MergeShapeFaceRects(ref FixedList4096Bytes<ShapeFaceRect> faceRects)
        {
            bool mergedAny;
            do
            {
                mergedAny = false;
                for (int i = 0; i < faceRects.Length && !mergedAny; i++)
                {
                    for (int j = i + 1; j < faceRects.Length; j++)
                    {
                        if (!TryMergeShapeFaceRects(faceRects[i], faceRects[j], out ShapeFaceRect merged))
                            continue;

                        faceRects[i] = merged;
                        faceRects.RemoveAt(j);
                        mergedAny = true;
                        break;
                    }
                }
            }
            while (mergedAny);
        }

        private static bool TryMergeShapeFaceRects(ShapeFaceRect a, ShapeFaceRect b, out ShapeFaceRect merged)
        {
            const float epsilon = 0.0001f;
            merged = default;

            if (a.face != b.face ||
                math.abs(a.plane - b.plane) > epsilon ||
                !HasSameAppearance(a, b))
                return false;

            bool sameA = math.abs(a.minA - b.minA) <= epsilon && math.abs(a.maxA - b.maxA) <= epsilon;
            bool sameB = math.abs(a.minB - b.minB) <= epsilon && math.abs(a.maxB - b.maxB) <= epsilon;

            if (sameA && (math.abs(a.maxB - b.minB) <= epsilon || math.abs(b.maxB - a.minB) <= epsilon))
            {
                merged = a;
                merged.minB = math.min(a.minB, b.minB);
                merged.maxB = math.max(a.maxB, b.maxB);
                return true;
            }

            if (sameB && (math.abs(a.maxA - b.minA) <= epsilon || math.abs(b.maxA - a.minA) <= epsilon))
            {
                merged = a;
                merged.minA = math.min(a.minA, b.minA);
                merged.maxA = math.max(a.maxA, b.maxA);
                return true;
            }

            return false;
        }

        private static bool HasSameAppearance(ShapeFaceRect a, ShapeFaceRect b)
        {
            return a.usesExplicitAppearance == b.usesExplicitAppearance &&
                   (!a.usesExplicitAppearance ||
                    (a.tint == b.tint &&
                     math.lengthsq(a.explicitUvRectData - b.explicitUvRectData) <= 1e-10f));
        }

        private static void CullHiddenShapeFaceRects(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            in FixedList512Bytes<ShapeBox> shapeBoxes)
        {
            bool changed;
            do
            {
                changed = false;
                for (int i = 0; i < faceRects.Length && !changed; i++)
                {
                    ShapeFaceRect rect = faceRects[i];
                    for (int boxIndex = 0; boxIndex < shapeBoxes.Length; boxIndex++)
                    {
                        if (!TryGetShapeBoxCoverage(rect, shapeBoxes[boxIndex], out ShapeFaceRect coverage))
                            continue;

                        if (!SubtractShapeFaceRectAt(ref faceRects, i, coverage))
                            continue;

                        changed = true;
                        break;
                    }
                }
            }
            while (changed);

            do
            {
                changed = false;
                for (int i = 0; i < faceRects.Length && !changed; i++)
                {
                    for (int j = i + 1; j < faceRects.Length; j++)
                    {
                        if (!TryGetSameFaceOverlap(faceRects[i], faceRects[j], out ShapeFaceRect overlap))
                            continue;

                        if (!SubtractShapeFaceRectAt(ref faceRects, j, overlap))
                            continue;

                        changed = true;
                        break;
                    }
                }
            }
            while (changed);
        }

        private static bool TryGetShapeBoxCoverage(ShapeFaceRect rect, ShapeBox box, out ShapeFaceRect coverage)
        {
            const float epsilon = 0.0001f;
            coverage = default;

            switch (rect.face)
            {
                case BlockFace.Right:
                    if (box.min.x > rect.plane + epsilon || box.max.x <= rect.plane + epsilon)
                        return false;

                    coverage = new ShapeFaceRect
                    {
                        face = rect.face,
                        plane = rect.plane,
                        minA = box.min.y,
                        maxA = box.max.y,
                        minB = box.min.z,
                        maxB = box.max.z
                    };
                    break;

                case BlockFace.Left:
                    if (box.max.x < rect.plane - epsilon || box.min.x >= rect.plane - epsilon)
                        return false;

                    coverage = new ShapeFaceRect
                    {
                        face = rect.face,
                        plane = rect.plane,
                        minA = box.min.y,
                        maxA = box.max.y,
                        minB = box.min.z,
                        maxB = box.max.z
                    };
                    break;

                case BlockFace.Top:
                    if (box.min.y > rect.plane + epsilon || box.max.y <= rect.plane + epsilon)
                        return false;

                    coverage = new ShapeFaceRect
                    {
                        face = rect.face,
                        plane = rect.plane,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.z,
                        maxB = box.max.z
                    };
                    break;

                case BlockFace.Bottom:
                    if (box.max.y < rect.plane - epsilon || box.min.y >= rect.plane - epsilon)
                        return false;

                    coverage = new ShapeFaceRect
                    {
                        face = rect.face,
                        plane = rect.plane,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.z,
                        maxB = box.max.z
                    };
                    break;

                case BlockFace.Front:
                    if (box.min.z > rect.plane + epsilon || box.max.z <= rect.plane + epsilon)
                        return false;

                    coverage = new ShapeFaceRect
                    {
                        face = rect.face,
                        plane = rect.plane,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.y,
                        maxB = box.max.y
                    };
                    break;

                case BlockFace.Back:
                    if (box.max.z < rect.plane - epsilon || box.min.z >= rect.plane - epsilon)
                        return false;

                    coverage = new ShapeFaceRect
                    {
                        face = rect.face,
                        plane = rect.plane,
                        minA = box.min.x,
                        maxA = box.max.x,
                        minB = box.min.y,
                        maxB = box.max.y
                    };
                    break;

                default:
                    return false;
            }

            return TryGetSameFaceOverlap(rect, coverage, out coverage);
        }

        private static bool TryGetSameFaceOverlap(ShapeFaceRect a, ShapeFaceRect b, out ShapeFaceRect overlap)
        {
            const float epsilon = 0.0001f;
            overlap = default;

            if (a.face != b.face || math.abs(a.plane - b.plane) > epsilon)
                return false;

            float minA = math.max(a.minA, b.minA);
            float maxA = math.min(a.maxA, b.maxA);
            float minB = math.max(a.minB, b.minB);
            float maxB = math.min(a.maxB, b.maxB);
            if (maxA <= minA + epsilon || maxB <= minB + epsilon)
                return false;

            overlap = new ShapeFaceRect
            {
                face = a.face,
                plane = a.plane,
                minA = minA,
                maxA = maxA,
                minB = minB,
                maxB = maxB
            };
            return true;
        }

        private static bool SubtractShapeFaceRectAt(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            int index,
            ShapeFaceRect clip)
        {
            if (index < 0 || index >= faceRects.Length)
                return false;

            ShapeFaceRect source = faceRects[index];
            if (!TryGetSameFaceOverlap(source, clip, out ShapeFaceRect overlap))
                return false;

            faceRects.RemoveAt(index);
            AddShapeFaceRectFragment(ref faceRects, source, source.minA, source.maxA, source.minB, overlap.minB);
            AddShapeFaceRectFragment(ref faceRects, source, source.minA, source.maxA, overlap.maxB, source.maxB);
            AddShapeFaceRectFragment(ref faceRects, source, source.minA, overlap.minA, overlap.minB, overlap.maxB);
            AddShapeFaceRectFragment(ref faceRects, source, overlap.maxA, source.maxA, overlap.minB, overlap.maxB);
            return true;
        }

        private static void AddShapeFaceRectFragment(
            ref FixedList4096Bytes<ShapeFaceRect> faceRects,
            ShapeFaceRect source,
            float minA,
            float maxA,
            float minB,
            float maxB)
        {
            const float epsilon = 0.0001f;
            if (maxA <= minA + epsilon || maxB <= minB + epsilon)
                return;

            if (faceRects.Length >= faceRects.Capacity)
                return;

            faceRects.Add(new ShapeFaceRect
            {
                face = source.face,
                plane = source.plane,
                minA = minA,
                maxA = maxA,
                minB = minB,
                maxB = maxB,
                tileX = source.tileX,
                tileY = source.tileY,
                tint = source.tint,
                usesExplicitAppearance = source.usesExplicitAppearance,
                explicitUvRectData = source.explicitUvRectData
            });
        }

        private static BlockFace ResolveShapeTextureFace(
            BlockTextureMapping mapping,
            BlockFace worldFace,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis)
        {
            if (currentShape != BlockRenderShape.MultiCuboid)
                return worldFace;

            return BlockPlacementRotationUtility.ResolveFaceForPlacement(mapping, worldFace, currentPlacementAxis);
        }

        private static void ResolveShapeRectAppearance(
            ShapeFaceRect rect,
            BlockTextureMapping mapping,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            out BlockFace textureFace,
            out Vector2Int tile,
            out bool tint,
            out Vector4 explicitUvRectData)
        {
            if (rect.usesExplicitAppearance)
            {
                textureFace = rect.face;
                tile = new Vector2Int(rect.tileX, rect.tileY);
                tint = rect.tint;
                explicitUvRectData = rect.explicitUvRectData;
                return;
            }

            textureFace = ResolveShapeTextureFace(mapping, rect.face, currentShape, currentPlacementAxis);
            tile = mapping.GetTileCoord(textureFace);
            tint = mapping.GetTint(textureFace);
            explicitUvRectData = default;
        }

        private void AddAmbientOccludedShapeRect(
            Vector3 origin,
            BlockTextureMapping mapping,
            ShapeFaceRect rect,
            float light01,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            in FixedList512Bytes<ShapeBox> shapeBoxes,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            RampShapeVariant currentRampVariant)
        {
            bool disableAOForCurrentBlock = aoStrength <= 0f || IsEmissiveBlock(mapping);
            ResolveShapeRectAppearance(
                rect,
                mapping,
                currentShape,
                currentPlacementAxis,
                out BlockFace textureFace,
                out Vector2Int tile,
                out bool tint,
                out Vector4 explicitUvRectData);

            switch (rect.face)
            {
                case BlockFace.Right:
                {
                    AddAmbientOccludedShapeFace(
                        origin + new Vector3(rect.plane, rect.minA, rect.minB),
                        origin + new Vector3(rect.plane, rect.maxA, rect.minB),
                        origin + new Vector3(rect.plane, rect.maxA, rect.maxB),
                        origin + new Vector3(rect.plane, rect.minA, rect.maxB),
                        Vector3.right,
                        BlockFace.Right,
                        Vector3Int.right,
                        Vector3Int.up,
                        Vector3Int.forward,
                        mapping,
                        textureFace,
                        tile,
                        explicitUvRectData,
                        tint,
                        light01,
                        voxelX,
                        voxelY,
                        voxelZ,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize,
                        invAtlasTilesX,
                        invAtlasTilesY,
                        tris,
                        disableAOForCurrentBlock,
                        shapeBoxes,
                        currentShape,
                        currentPlacementAxis,
                        currentRampVariant);
                    return;
                }

                case BlockFace.Left:
                {
                    AddAmbientOccludedShapeFace(
                        origin + new Vector3(rect.plane, rect.minA, rect.maxB),
                        origin + new Vector3(rect.plane, rect.maxA, rect.maxB),
                        origin + new Vector3(rect.plane, rect.maxA, rect.minB),
                        origin + new Vector3(rect.plane, rect.minA, rect.minB),
                        Vector3.left,
                        BlockFace.Left,
                        Vector3Int.left,
                        Vector3Int.up,
                        Vector3Int.back,
                        mapping,
                        textureFace,
                        tile,
                        explicitUvRectData,
                        tint,
                        light01,
                        voxelX,
                        voxelY,
                        voxelZ,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize,
                        invAtlasTilesX,
                        invAtlasTilesY,
                        tris,
                        disableAOForCurrentBlock,
                        shapeBoxes,
                        currentShape,
                        currentPlacementAxis,
                        currentRampVariant);
                    return;
                }

                case BlockFace.Top:
                {
                    AddAmbientOccludedShapeFace(
                        origin + new Vector3(rect.minA, rect.plane, rect.maxB),
                        origin + new Vector3(rect.maxA, rect.plane, rect.maxB),
                        origin + new Vector3(rect.maxA, rect.plane, rect.minB),
                        origin + new Vector3(rect.minA, rect.plane, rect.minB),
                        Vector3.up,
                        BlockFace.Top,
                        Vector3Int.up,
                        Vector3Int.right,
                        Vector3Int.back,
                        mapping,
                        textureFace,
                        tile,
                        explicitUvRectData,
                        tint,
                        light01,
                        voxelX,
                        voxelY,
                        voxelZ,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize,
                        invAtlasTilesX,
                        invAtlasTilesY,
                        tris,
                        disableAOForCurrentBlock,
                        shapeBoxes,
                        currentShape,
                        currentPlacementAxis,
                        currentRampVariant);
                    return;
                }

                case BlockFace.Bottom:
                {
                    AddAmbientOccludedShapeFace(
                        origin + new Vector3(rect.minA, rect.plane, rect.minB),
                        origin + new Vector3(rect.maxA, rect.plane, rect.minB),
                        origin + new Vector3(rect.maxA, rect.plane, rect.maxB),
                        origin + new Vector3(rect.minA, rect.plane, rect.maxB),
                        Vector3.down,
                        BlockFace.Bottom,
                        Vector3Int.down,
                        Vector3Int.right,
                        Vector3Int.forward,
                        mapping,
                        textureFace,
                        tile,
                        explicitUvRectData,
                        tint,
                        light01,
                        voxelX,
                        voxelY,
                        voxelZ,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize,
                        invAtlasTilesX,
                        invAtlasTilesY,
                        tris,
                        disableAOForCurrentBlock,
                        shapeBoxes,
                        currentShape,
                        currentPlacementAxis,
                        currentRampVariant);
                    return;
                }

                case BlockFace.Front:
                {
                    AddAmbientOccludedShapeFace(
                        origin + new Vector3(rect.maxA, rect.minB, rect.plane),
                        origin + new Vector3(rect.maxA, rect.maxB, rect.plane),
                        origin + new Vector3(rect.minA, rect.maxB, rect.plane),
                        origin + new Vector3(rect.minA, rect.minB, rect.plane),
                        Vector3.forward,
                        BlockFace.Front,
                        Vector3Int.forward,
                        Vector3Int.up,
                        Vector3Int.left,
                        mapping,
                        textureFace,
                        tile,
                        explicitUvRectData,
                        tint,
                        light01,
                        voxelX,
                        voxelY,
                        voxelZ,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize,
                        invAtlasTilesX,
                        invAtlasTilesY,
                        tris,
                        disableAOForCurrentBlock,
                        shapeBoxes,
                        currentShape,
                        currentPlacementAxis,
                        currentRampVariant);
                    return;
                }

                default:
                {
                    AddAmbientOccludedShapeFace(
                        origin + new Vector3(rect.minA, rect.minB, rect.plane),
                        origin + new Vector3(rect.minA, rect.maxB, rect.plane),
                        origin + new Vector3(rect.maxA, rect.maxB, rect.plane),
                        origin + new Vector3(rect.maxA, rect.minB, rect.plane),
                        Vector3.back,
                        BlockFace.Back,
                        Vector3Int.back,
                        Vector3Int.up,
                        Vector3Int.right,
                        mapping,
                        textureFace,
                        tile,
                        explicitUvRectData,
                        tint,
                        light01,
                        voxelX,
                        voxelY,
                        voxelZ,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize,
                        invAtlasTilesX,
                        invAtlasTilesY,
                        tris,
                        disableAOForCurrentBlock,
                        shapeBoxes,
                        currentShape,
                        currentPlacementAxis,
                        currentRampVariant);
                    return;
                }
            }
        }

        private void AddAmbientOccludedCustomQuad(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockFace sampledFace,
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2 uv0,
            Vector2 uv1,
            Vector2 uv2,
            Vector2 uv3,
            Vector3 normal,
            Vector3 aoNormal,
            Vector3 aoStepU,
            Vector3 aoStepV,
            float light01,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            RampShapeVariant currentRampVariant,
            bool invertWinding,
            bool suppressNeighborRampAO = false,
            bool hasExplicitAppearance = false,
            Vector2Int explicitTile = default(Vector2Int),
            bool explicitTint = false,
            Vector4 explicitUvRectData = default)
        {
            bool disableAOForCurrentBlock = aoStrength <= 0f || IsEmissiveBlock(mapping);
            Vector2Int tile;
            bool tint;
            Vector2 atlasUv;
            Vector2 atlasSize;
            if (hasExplicitAppearance)
            {
                tile = explicitTile;
                tint = explicitTint;
                if (BlockAtlasUvUtility.IsValidUvRectData(explicitUvRectData))
                {
                    atlasUv = new Vector2(explicitUvRectData.x, explicitUvRectData.y);
                    atlasSize = new Vector2(explicitUvRectData.z, explicitUvRectData.w);
                }
                else
                {
                    atlasUv = new Vector2(tile.x * invAtlasTilesX, tile.y * invAtlasTilesY);
                    atlasSize = new Vector2(invAtlasTilesX, invAtlasTilesY);
                }
            }
            else
            {
                BlockFace textureFace = ResolveShapeTextureFace(mapping, sampledFace, currentShape, currentPlacementAxis);
                tile = mapping.GetTileCoord(textureFace);
                tint = mapping.GetTint(textureFace);
                ResolveAtlasRect(mapping, textureFace, invAtlasTilesX, invAtlasTilesY, out atlasUv, out atlasSize);
            }
            FixedList512Bytes<ShapeBox> emptyShapeBoxes = default;
            int vIndex = GetCurrentSubchunkLocalVertexIndex();

            ResolveCustomFaceVertexAOFrame(sampledFace, p0, normal, out Vector3 aoNormal0, out Vector3 stepU0, out Vector3 stepV0);
            ResolveCustomFaceVertexAOFrame(sampledFace, p1, normal, out Vector3 aoNormal1, out Vector3 stepU1, out Vector3 stepV1);
            ResolveCustomFaceVertexAOFrame(sampledFace, p2, normal, out Vector3 aoNormal2, out Vector3 stepU2, out Vector3 stepV2);
            ResolveCustomFaceVertexAOFrame(sampledFace, p3, normal, out Vector3 aoNormal3, out Vector3 stepU3, out Vector3 stepV3);

            if (currentShape == BlockRenderShape.MultiCuboid)
                NormalizeProjectedQuadUv(ref uv0, ref uv1, ref uv2, ref uv3);

            AddAmbientOccludedShapeVertex(origin + p0, uv0, normal, aoNormal0, stepU0, stepV0, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
            AddAmbientOccludedShapeVertex(origin + p1, uv1, normal, aoNormal1, stepU1, stepV1, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
            AddAmbientOccludedShapeVertex(origin + p2, uv2, normal, aoNormal2, stepU2, stepV2, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
            AddAmbientOccludedShapeVertex(origin + p3, uv3, normal, aoNormal3, stepU3, stepV3, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);

            if (invertWinding)
            {
                tris.Add(vIndex + 0);
                tris.Add(vIndex + 2);
                tris.Add(vIndex + 1);
                tris.Add(vIndex + 0);
                tris.Add(vIndex + 3);
                tris.Add(vIndex + 2);
                return;
            }

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);
        }

        private void AddAmbientOccludedCustomTriangle(
            Vector3 origin,
            BlockTextureMapping mapping,
            BlockFace sampledFace,
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector2 uv0,
            Vector2 uv1,
            Vector2 uv2,
            Vector3 normal,
            Vector3 aoNormal,
            Vector3 aoStepU,
            Vector3 aoStepV,
            float light01,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            RampShapeVariant currentRampVariant,
            bool suppressNeighborRampAO = false)
        {
            bool disableAOForCurrentBlock = aoStrength <= 0f || IsEmissiveBlock(mapping);
            Vector2Int tile = mapping.GetTileCoord(sampledFace);
            bool tint = mapping.GetTint(sampledFace);
            ResolveAtlasRect(mapping, sampledFace, invAtlasTilesX, invAtlasTilesY, out Vector2 atlasUv, out Vector2 atlasSize);
            FixedList512Bytes<ShapeBox> emptyShapeBoxes = default;
            int vIndex = GetCurrentSubchunkLocalVertexIndex();

            ResolveCustomFaceVertexAOFrame(sampledFace, p0, normal, out Vector3 aoNormal0, out Vector3 stepU0, out Vector3 stepV0);
            ResolveCustomFaceVertexAOFrame(sampledFace, p1, normal, out Vector3 aoNormal1, out Vector3 stepU1, out Vector3 stepV1);
            ResolveCustomFaceVertexAOFrame(sampledFace, p2, normal, out Vector3 aoNormal2, out Vector3 stepU2, out Vector3 stepV2);

            AddAmbientOccludedShapeVertex(origin + p0, uv0, normal, aoNormal0, stepU0, stepV0, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
            AddAmbientOccludedShapeVertex(origin + p1, uv1, normal, aoNormal1, stepU1, stepV1, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
            AddAmbientOccludedShapeVertex(origin + p2, uv2, normal, aoNormal2, stepU2, stepV2, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, emptyShapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
        }

        private static void ResolveCustomFaceVertexAOFrame(
            BlockFace sampledFace,
            Vector3 localPos,
            Vector3 geometricNormal,
            out Vector3 aoNormal,
            out Vector3 stepU,
            out Vector3 stepV)
        {
            switch (sampledFace)
            {
                case BlockFace.Top:
                    aoNormal = Vector3.up;
                    stepU = localPos.x >= 0.5f ? Vector3.right : Vector3.left;
                    stepV = localPos.z >= 0.5f ? Vector3.forward : Vector3.back;
                    return;

                case BlockFace.Bottom:
                    aoNormal = Vector3.down;
                    stepU = localPos.x >= 0.5f ? Vector3.right : Vector3.left;
                    stepV = localPos.z >= 0.5f ? Vector3.forward : Vector3.back;
                    return;

                case BlockFace.Right:
                    aoNormal = Vector3.right;
                    stepU = localPos.y >= 0.5f ? Vector3.up : Vector3.down;
                    stepV = localPos.z >= 0.5f ? Vector3.forward : Vector3.back;
                    return;

                case BlockFace.Left:
                    aoNormal = Vector3.left;
                    stepU = localPos.y >= 0.5f ? Vector3.up : Vector3.down;
                    stepV = localPos.z >= 0.5f ? Vector3.forward : Vector3.back;
                    return;

                case BlockFace.Front:
                    aoNormal = Vector3.forward;
                    stepU = localPos.y >= 0.5f ? Vector3.up : Vector3.down;
                    stepV = localPos.x >= 0.5f ? Vector3.right : Vector3.left;
                    return;

                case BlockFace.Back:
                    aoNormal = Vector3.back;
                    stepU = localPos.y >= 0.5f ? Vector3.up : Vector3.down;
                    stepV = localPos.x >= 0.5f ? Vector3.right : Vector3.left;
                    return;

                default:
                    Vector3 fallbackNormal = geometricNormal.sqrMagnitude > 0.0001f ? geometricNormal.normalized : Vector3.up;
                    aoNormal = fallbackNormal;
                    stepU = Vector3.right;
                    stepV = Vector3.forward;
                    return;
            }
        }

        private void AddAmbientOccludedShapeFace(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector3 normal,
            BlockFace sampledFace,
            Vector3 aoNormal,
            Vector3 aoStepU,
            Vector3 aoStepV,
            BlockTextureMapping mapping,
            BlockFace textureFace,
            Vector2Int tile,
            Vector4 explicitUvRectData,
            bool tint,
            float light01,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            bool disableAOForCurrentBlock,
            in FixedList512Bytes<ShapeBox> shapeBoxes,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            RampShapeVariant currentRampVariant)
        {
            int vIndex = GetCurrentSubchunkLocalVertexIndex();
            Vector2 atlasUv;
            Vector2 atlasSize;
            if (BlockAtlasUvUtility.IsValidUvRectData(explicitUvRectData))
            {
                atlasUv = new Vector2(explicitUvRectData.x, explicitUvRectData.y);
                atlasSize = new Vector2(explicitUvRectData.z, explicitUvRectData.w);
            }
            else
            {
                ResolveAtlasRect(mapping, textureFace, invAtlasTilesX, invAtlasTilesY, out atlasUv, out atlasSize);
            }
            Vector3 blockOrigin = new Vector3(voxelX - border, voxelY, voxelZ - border);

            Vector2 uv0 = ResolveShapeProjectedUv(sampledFace, p0 - blockOrigin);
            Vector2 uv1 = ResolveShapeProjectedUv(sampledFace, p1 - blockOrigin);
            Vector2 uv2 = ResolveShapeProjectedUv(sampledFace, p2 - blockOrigin);
            Vector2 uv3 = ResolveShapeProjectedUv(sampledFace, p3 - blockOrigin);
            if (currentShape == BlockRenderShape.MultiCuboid)
                NormalizeProjectedQuadUv(ref uv0, ref uv1, ref uv2, ref uv3);

            AddAmbientOccludedShapeVertex(p0, uv0, normal, aoNormal, -aoStepU, -aoStepV, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant);
            AddAmbientOccludedShapeVertex(p1, uv1, normal, aoNormal, aoStepU, -aoStepV, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant);
            AddAmbientOccludedShapeVertex(p2, uv2, normal, aoNormal, aoStepU, aoStepV, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant);
            AddAmbientOccludedShapeVertex(p3, uv3, normal, aoNormal, -aoStepU, aoStepV, tint, light01, disableAOForCurrentBlock, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, atlasUv, atlasSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant);

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);
        }

        private void AddAmbientOccludedShapeVertex(
            Vector3 position,
            Vector2 uv,
            Vector3 normal,
            Vector3 aoNormal,
            Vector3 stepU,
            Vector3 stepV,
            bool tint,
            float light01,
            bool disableAO,
            int voxelX,
            int voxelY,
            int voxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            Vector2 atlasUv,
            Vector2 atlasSize,
            in FixedList512Bytes<ShapeBox> shapeBoxes,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            RampShapeVariant currentRampVariant,
            bool suppressNeighborRampAO = false)
        {
            byte aoValue = 3;
            if (!disableAO)
            {
                const float normalOffset = 0.1f;
                const float tangentOffset = 0.45f;
                Vector3 sampleSpaceOffset = new Vector3(border, 0f, border);
                Vector3 sampleOrigin = position + sampleSpaceOffset + aoNormal * normalOffset;
                bool side1 = IsAmbientOccluderAtPoint(sampleOrigin + stepU * tangentOffset, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
                bool side2 = IsAmbientOccluderAtPoint(sampleOrigin + stepV * tangentOffset, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
                bool corner = IsAmbientOccluderAtPoint(sampleOrigin + (stepU + stepV) * tangentOffset, voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, shapeBoxes, currentShape, currentPlacementAxis, currentRampVariant, suppressNeighborRampAO);
                aoValue = ResolveShapeVertexAO(side1, side2, corner);
            }

            float floatTint = tint ? 1f : 0f;
            AddPackedVertex(
                position,
                normal,
                uv,
                atlasUv,
                new Vector4(light01, floatTint, ResolveAmbientOcclusionFactor(aoValue), 0f),
                atlasSize);
        }

        private bool IsAmbientOccluderAtPoint(
            Vector3 samplePos,
            int currentVoxelX,
            int currentVoxelY,
            int currentVoxelZ,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            in FixedList512Bytes<ShapeBox> currentShapeBoxes,
            BlockRenderShape currentShape,
            BlockPlacementAxis currentPlacementAxis,
            RampShapeVariant currentRampVariant,
            bool suppressNeighborRampAO)
        {
            int cellX = (int)math.floor(samplePos.x);
            int cellY = (int)math.floor(samplePos.y);
            int cellZ = (int)math.floor(samplePos.z);
            Vector3 localPos = samplePos - new Vector3(cellX, cellY, cellZ);

            if (cellX == currentVoxelX &&
                cellY == currentVoxelY &&
                cellZ == currentVoxelZ)
            {
                if (currentShape == BlockRenderShape.Ramp)
                {
                    if (suppressNeighborRampAO)
                        return false;

                    return RampShapeUtility.ContainsLocalPoint(localPos, currentPlacementAxis, currentRampVariant);
                }

                if (currentShape == BlockRenderShape.VerticalRamp)
                {
                    if (suppressNeighborRampAO)
                        return false;

                    return VerticalRampShapeUtility.ContainsLocalPoint(localPos, currentPlacementAxis);
                }

                return IsPointInsideShapeBoxes(localPos, currentShapeBoxes);
            }

            return IsAmbientOcclusionVolumeAtLocalPoint(cellX, cellY, cellZ, localPos, voxelSizeX, voxelSizeZ, voxelPlaneSize, suppressNeighborRampAO);
        }

        private bool IsAmbientOcclusionVolumeAtLocalPoint(
            int voxelX,
            int voxelY,
            int voxelZ,
            Vector3 localPos,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            bool suppressNeighborRampAO)
        {
            if (!TryGetResolvedVoxelIndex(voxelX, voxelY, voxelZ, voxelSizeX, voxelSizeZ, voxelPlaneSize, out int idx))
                return false;

            if (!solids[idx])
                return false;

            BlockType blockType = (BlockType)blockTypes[idx];
            int mapIndex = (int)blockType;
            if ((uint)mapIndex >= (uint)blockMappings.Length)
                return false;

            BlockTextureMapping mapping = blockMappings[mapIndex];
            if (!CastsShapeAmbientOcclusion(blockType, mapping))
                return false;

            BlockRenderShape shape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
            switch (shape)
            {
                case BlockRenderShape.Cube:
                    return true;

                case BlockRenderShape.Cuboid:
                    ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);
                    return IsPointInsideBox(localPos, new ShapeBox(min, max));

                case BlockRenderShape.MultiCuboid:
                {
                    BlockPlacementAxis multiAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(GetBlockPlacementAxisValue(idx));
                    FixedList512Bytes<ShapeBox> multiBoxes = BuildNativeMultiCuboidShapeBoxes(mapping, multiAxis);
                    return IsPointInsideShapeBoxes(localPos, multiBoxes);
                }

                case BlockRenderShape.Stairs:
                {
                    byte rawPlacementData = GetBlockPlacementAxisValue(idx);
                    StairShapeVariant variant = ResolveStairShapeVariant(rawPlacementData, voxelX, voxelY, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize);
                    FixedList512Bytes<ShapeBox> stairBoxes = BuildStairVisualBoxes(rawPlacementData, variant);
                    return IsPointInsideShapeBoxes(localPos, stairBoxes);
                }

                case BlockRenderShape.Ramp:
                {
                    if (suppressNeighborRampAO)
                        return false;

                    BlockPlacementAxis rampAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(GetBlockPlacementAxisValue(idx));
                    RampShapeVariant rampVariant = ResolveRampShapeVariant(
                        rampAxis,
                        voxelX,
                        voxelY,
                        voxelZ,
                        blockTypes,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize);
                    return RampShapeUtility.ContainsAmbientOcclusionPoint(localPos, rampAxis, rampVariant);
                }

                case BlockRenderShape.VerticalRamp:
                {
                    if (suppressNeighborRampAO)
                        return false;

                    BlockPlacementAxis verticalRampAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(GetBlockPlacementAxisValue(idx));
                    return VerticalRampShapeUtility.ContainsAmbientOcclusionPoint(localPos, verticalRampAxis);
                }

                case BlockRenderShape.Fence:
                {
                    byte connectionMask = FenceShapeUtility.ResolveConnectionMask(
                        voxelX,
                        voxelY,
                        voxelZ,
                        blockTypes,
                        blockMappings,
                        voxelSizeX,
                        voxelSizeZ,
                        voxelPlaneSize);
                    FixedList512Bytes<ShapeBox> fenceBoxes = BuildFenceVisualBoxes(connectionMask);
                    return IsPointInsideShapeBoxes(localPos, fenceBoxes);
                }

                default:
                    ResolveShapeBounds(mapping, out Vector3 fallbackMin, out Vector3 fallbackMax);
                    return IsPointInsideBox(localPos, new ShapeBox(fallbackMin, fallbackMax));
            }
        }

        private static bool CastsShapeAmbientOcclusion(BlockType blockType, BlockTextureMapping mapping)
        {
            BlockRenderShape shape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
            if (shape == BlockRenderShape.Cube)
                return CastsAmbientOcclusion(blockType, mapping);

            if (mapping.isEmpty ||
                mapping.isLiquid ||
                mapping.lightOpacity == 0 ||
                mapping.isTransparent ||
                !mapping.isSolid ||
                TorchPlacementUtility.IsTorchLike(blockType))
            {
                return false;
            }

            return shape != BlockRenderShape.Cross && shape != BlockRenderShape.Plane;
        }

        private static bool IsPointInsideShapeBoxes(Vector3 localPos, in FixedList512Bytes<ShapeBox> boxes)
        {
            for (int i = 0; i < boxes.Length; i++)
            {
                if (IsPointInsideBox(localPos, boxes[i]))
                    return true;
            }

            return false;
        }

        private static bool IsPointInsideBox(Vector3 localPos, ShapeBox box)
        {
            const float epsilon = 0.0001f;
            return localPos.x > box.min.x + epsilon && localPos.x < box.max.x - epsilon &&
                   localPos.y > box.min.y + epsilon && localPos.y < box.max.y - epsilon &&
                   localPos.z > box.min.z + epsilon && localPos.z < box.max.z - epsilon;
        }

        private static byte ResolveShapeVertexAO(bool side1, bool side2, bool corner)
        {
            if (side1 && side2)
                return 0;

            return (byte)(3 - (side1 ? 1 : 0) - (side2 ? 1 : 0) - (corner ? 1 : 0));
        }

        private static Vector2 ResolveShapeProjectedUv(BlockFace sampledFace, Vector3 localPos)
        {
            return sampledFace switch
            {
                BlockFace.Top => new Vector2(localPos.x, localPos.z),
                BlockFace.Bottom => new Vector2(localPos.x, localPos.z),
                BlockFace.Right => new Vector2(localPos.z, localPos.y),
                BlockFace.Left => new Vector2(localPos.z, localPos.y),
                BlockFace.Front => new Vector2(localPos.x, localPos.y),
                BlockFace.Back => new Vector2(localPos.x, localPos.y),
                _ => new Vector2(localPos.x, localPos.y)
            };
        }

        private static void NormalizeProjectedQuadUv(ref Vector2 uv0, ref Vector2 uv1, ref Vector2 uv2, ref Vector2 uv3)
        {
            float minU = math.min(math.min(uv0.x, uv1.x), math.min(uv2.x, uv3.x));
            float maxU = math.max(math.max(uv0.x, uv1.x), math.max(uv2.x, uv3.x));
            float minV = math.min(math.min(uv0.y, uv1.y), math.min(uv2.y, uv3.y));
            float maxV = math.max(math.max(uv0.y, uv1.y), math.max(uv2.y, uv3.y));

            float invSpanU = 1f / math.max(maxU - minU, 1e-6f);
            float invSpanV = 1f / math.max(maxV - minV, 1e-6f);

            uv0 = new Vector2((uv0.x - minU) * invSpanU, (uv0.y - minV) * invSpanV);
            uv1 = new Vector2((uv1.x - minU) * invSpanU, (uv1.y - minV) * invSpanV);
            uv2 = new Vector2((uv2.x - minU) * invSpanU, (uv2.y - minV) * invSpanV);
            uv3 = new Vector2((uv3.x - minU) * invSpanU, (uv3.y - minV) * invSpanV);
        }

        private static Vector3 ResolveShapeFaceNormal(BlockFace face)
        {
            return face switch
            {
                BlockFace.Right => Vector3.right,
                BlockFace.Left => Vector3.left,
                BlockFace.Top => Vector3.up,
                BlockFace.Bottom => Vector3.down,
                BlockFace.Front => Vector3.forward,
                _ => Vector3.back
            };
        }

        private float ResolveAmbientOcclusionFactor(byte aoValue)
        {
            float aoCurve = aoCurveExponent > 0f ? aoCurveExponent : DefaultAOCurveExponent;
            float aoBase = aoValue / 3f;
            float aoCurved = math.pow(aoBase, aoCurve);
            float aoDarkened = 1f - (1f - aoCurved) * math.max(0f, aoStrength);
            return math.max(math.saturate(aoMinLight), math.saturate(aoDarkened));
        }

        private void AddStaticLitCustomQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector2 uv0,
            Vector2 uv1,
            Vector2 uv2,
            Vector2 uv3,
            Vector3 normal,
            Vector2Int tile,
            bool tint,
            Vector4 explicitUvRectData,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris,
            bool invertWinding = false)
        {
            if (normal.sqrMagnitude < 0.0001f)
                normal = Vector3.up;

            Vector2 atlasUv;
            Vector2 atlasSize;
            if (BlockAtlasUvUtility.IsValidUvRectData(explicitUvRectData))
            {
                atlasUv = new Vector2(explicitUvRectData.x, explicitUvRectData.y);
                atlasSize = new Vector2(explicitUvRectData.z, explicitUvRectData.w);
            }
            else
            {
                atlasUv = new Vector2(tile.x * invAtlasTilesX, tile.y * invAtlasTilesY);
                atlasSize = new Vector2(invAtlasTilesX, invAtlasTilesY);
            }

            int vIndex = GetCurrentSubchunkLocalVertexIndex();
            Vector4 extra = new Vector4(light01, tint ? 1f : 0f, 1f, 0f);
            AddPackedVertex(p0, normal, uv0, atlasUv, extra, atlasSize);
            AddPackedVertex(p1, normal, uv1, atlasUv, extra, atlasSize);
            AddPackedVertex(p2, normal, uv2, atlasUv, extra, atlasSize);
            AddPackedVertex(p3, normal, uv3, atlasUv, extra, atlasSize);

            if (invertWinding)
            {
                tris.Add(vIndex + 0);
                tris.Add(vIndex + 2);
                tris.Add(vIndex + 1);
                tris.Add(vIndex + 0);
                tris.Add(vIndex + 3);
                tris.Add(vIndex + 2);
                return;
            }

            tris.Add(vIndex + 0);
            tris.Add(vIndex + 1);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 0);
            tris.Add(vIndex + 2);
            tris.Add(vIndex + 3);
        }

        private void AddStaticLitShapeBox(
            Vector3 origin,
            BlockTextureMapping mapping,
            ShapeBox box,
            float light01,
            float invAtlasTilesX,
            float invAtlasTilesY,
            NativeList<int> tris)
        {
            AddStaticLitShapeFace(
                origin + new Vector3(box.max.x, box.min.y, box.min.z),
                origin + new Vector3(box.max.x, box.max.y, box.min.z),
                origin + new Vector3(box.max.x, box.max.y, box.max.z),
                origin + new Vector3(box.max.x, box.min.y, box.max.z),
                mapping,
                BlockFace.Right,
                mapping.GetTint(BlockFace.Right),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddStaticLitShapeFace(
                origin + new Vector3(box.min.x, box.min.y, box.max.z),
                origin + new Vector3(box.min.x, box.max.y, box.max.z),
                origin + new Vector3(box.min.x, box.max.y, box.min.z),
                origin + new Vector3(box.min.x, box.min.y, box.min.z),
                mapping,
                BlockFace.Left,
                mapping.GetTint(BlockFace.Left),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddStaticLitShapeFace(
                origin + new Vector3(box.min.x, box.max.y, box.max.z),
                origin + new Vector3(box.max.x, box.max.y, box.max.z),
                origin + new Vector3(box.max.x, box.max.y, box.min.z),
                origin + new Vector3(box.min.x, box.max.y, box.min.z),
                mapping,
                BlockFace.Top,
                mapping.GetTint(BlockFace.Top),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddStaticLitShapeFace(
                origin + new Vector3(box.min.x, box.min.y, box.min.z),
                origin + new Vector3(box.max.x, box.min.y, box.min.z),
                origin + new Vector3(box.max.x, box.min.y, box.max.z),
                origin + new Vector3(box.min.x, box.min.y, box.max.z),
                mapping,
                BlockFace.Bottom,
                mapping.GetTint(BlockFace.Bottom),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddStaticLitShapeFace(
                origin + new Vector3(box.max.x, box.min.y, box.max.z),
                origin + new Vector3(box.max.x, box.max.y, box.max.z),
                origin + new Vector3(box.min.x, box.max.y, box.max.z),
                origin + new Vector3(box.min.x, box.min.y, box.max.z),
                mapping,
                BlockFace.Front,
                mapping.GetTint(BlockFace.Front),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);

            AddStaticLitShapeFace(
                origin + new Vector3(box.min.x, box.min.y, box.min.z),
                origin + new Vector3(box.min.x, box.max.y, box.min.z),
                origin + new Vector3(box.max.x, box.max.y, box.min.z),
                origin + new Vector3(box.max.x, box.min.y, box.min.z),
                mapping,
                BlockFace.Back,
                mapping.GetTint(BlockFace.Back),
                light01,
                invAtlasTilesX,
                invAtlasTilesY,
                tris);
        }
    }
}
