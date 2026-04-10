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
            NativeArray<byte> light,
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
                        Vector2Int leavesTile = leavesMapping.GetTileCoord(BlockFace.Front);
                        leavesAtlasUv = new Vector2(
                            leavesTile.x * invAtlasTilesX + 0.001f,
                            leavesTile.y * invAtlasTilesY + 0.001f);
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
                        Vector2Int billboardTile = billboardMapping.GetTileCoord(BlockFace.Front);
                        Vector2 billboardAtlasUv = new Vector2(
                            billboardTile.x * invAtlasTilesX + 0.001f,
                            billboardTile.y * invAtlasTilesY + 0.001f);
                        float billboardTint = billboardMapping.GetTint(BlockFace.Front) ? 1f : 0f;

                        float jx = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 8, jitter);
                        float jz = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 16, jitter);
                        float billboardHeight = VegetationBillboardUtility.ComputeHeight(grassBillboardHeight, variationHash);
                        float billboardHalfWidth = VegetationBillboardUtility.ComputeHalfWidth(variationHash);
                        float centerYOffset = VegetationBillboardUtility.ComputeBaseYOffset(variationHash);
                        byte packed = light[upIdx];
                        byte billboardLight = (byte)math.max(
                            (int)LightUtils.GetSkyLight(packed),
                            (int)LightUtils.GetBlockLight(packed)
                        );
                        float light01 = billboardLight / 15f;
                        Vector3 center = new Vector3((x - border) + 0.5f + jx, py + centerYOffset, (z - border) + 0.5f + jz);
                        AddBillboardCross(center, billboardHeight, billboardHalfWidth, billboardAtlasUv, light01, billboardTint);
                    }
                }
            }
        }

        private void TryAddHighQualityLeafFoliage(
            int x,
            int y,
            int z,
            NativeArray<byte> blockTypes,
            NativeArray<byte> light,
            int voxelSizeX,
            int voxelSizeZ,
            int voxelPlaneSize,
            Vector2 atlasUv,
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

            float light01 = GetSpecialMeshLight01(x, y, z, voxelSizeX, voxelSizeZ, light);
            // Mantem o centro do billboard no centro do voxel para as quads cruzarem pelas diagonais do bloco.
            Vector3 center = new Vector3((x - border) + 0.5f + jx, y + baseYOffset, (z - border) + 0.5f + jz);
            AddBillboardCross(center, height, halfWidth, atlasUv, light01, tint);
        }

        private void AddUltraLeafBillboardFoliage(
            int x,
            int y,
            int z,
            NativeArray<byte> light,
            int voxelSizeX,
            int voxelSizeZ,
            Vector2 atlasUv,
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

            float light01 = GetSpecialMeshLight01(x, y, z, voxelSizeX, voxelSizeZ, light);
            // Pivot no centro do voxel para manter volume aparente de qualquer angulo.
            Vector3 center = new Vector3((x - border) + 0.5f + jx, y + 0.5f + baseYOffset, (z - border) + 0.5f + jz);
            AddBillboardFourFaces(center, height, halfWidth, atlasUv, light01, tint, rotationDeg, baseTiltDeg, randomTiltRangeDeg, variationHash);
        }

        private void AddBillboardFourFaces(
            Vector3 center,
            float height,
            float halfWidth,
            Vector2 atlasUv,
            float light01,
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

                AddBillboardPlane(center, height, halfWidth, dir, tiltDegrees, atlasUv, light01, tint);
            }
        }

        private void AddBillboardPlane(
            Vector3 center,
            float height,
            float halfWidth,
            Vector2 horizontalDirectionXZ,
            float tiltDegrees,
            Vector2 atlasUv,
            float light01,
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
            AddDoubleSidedQuad(p0, p1, p2, p3, atlasUv, light01, tint);
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

        private void AddBillboardCross(Vector3 center, float height, float halfWidth, Vector2 atlasUv, float light01, float tint)
        {
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
            float light01,
            float tint)
        {
            int vIndex = GetCurrentSubchunkLocalVertexIndex();
            Vector3 planeNormal = ComputeQuadPlaneNormal(p0, p1, p2);

            Vector4 e = new Vector4(light01, tint, 1f, 0f);
            AddPackedVertex(p0, planeNormal, new Vector2(0f, 0f), atlasUv, e);
            AddPackedVertex(p1, planeNormal, new Vector2(1f, 0f), atlasUv, e);
            AddPackedVertex(p2, planeNormal, new Vector2(1f, 1f), atlasUv, e);
            AddPackedVertex(p3, planeNormal, new Vector2(0f, 1f), atlasUv, e);

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

            Vector2Int tile = mapping.GetTileCoord(BlockFace.Front);
            Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
            float tint = mapping.GetTint(BlockFace.Front) ? 1f : 0f;

            NativeList<int> tris = FluidBlockUtility.IsWater(blockType)
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

            Vector2Int tile = mapping.GetTileCoord(sampledFace);
            Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
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

                Vector2Int lineTile = mapping.GetTileCoord(BlockFace.Front);
                Vector2 lineAtlasUv = new Vector2(lineTile.x * invAtlasTilesX + 0.001f, lineTile.y * invAtlasTilesY + 0.001f);
                Vector2Int shortLineTile = mapping.GetTileCoord(BlockFace.Back);
                Vector2 shortLineAtlasUv = new Vector2(shortLineTile.x * invAtlasTilesX + 0.001f, shortLineTile.y * invAtlasTilesY + 0.001f);
                Vector2Int dotTile = ResolveWireDotTile(mapping, lineTile);
                Vector2 dotAtlasUv = new Vector2(dotTile.x * invAtlasTilesX + 0.001f, dotTile.y * invAtlasTilesY + 0.001f);

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
                        shortLineAtlasUv,
                        dotAtlasUv,
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
                        shortLineAtlasUv,
                        dotAtlasUv,
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
                light01,
                tint,
                tris);
        }
    }
}
