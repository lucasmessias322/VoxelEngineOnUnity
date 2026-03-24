using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public static class TreePlacement
{
    [BurstCompile]
    public static void ApplyTreeInstancesToVoxels(
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        NativeArray<TreeInstance> treeInstances,
        Vector2Int coord,
        int border,
        int writePadding,
        int chunkSizeX,
        int chunkSizeZ,
        int chunkSizeY,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        NativeArray<int> heightCache,
        int heightStride)
    {
        if (treeInstances.Length == 0)
            return;

        int baseWorldX = coord.x * chunkSizeX;
        int baseWorldZ = coord.y * chunkSizeZ;

        for (int i = 0; i < treeInstances.Length; i++)
        {
            TreeInstance t = treeInstances[i];
            bool isTaigaSpruce = t.treeStyle == TreeStyle.TaigaSpruce;
            bool isCactus = t.treeStyle == TreeStyle.Cactus;
            bool isSavannaAcacia = t.treeStyle == TreeStyle.SavannaAcacia;
            bool isFancyOak = t.treeStyle == TreeStyle.FancyOak;

            int treeHash = (t.worldX * 73856093) ^ (t.worldZ * 19349663) ^ (t.trunkHeight * 83492791) ^ ((int)t.treeStyle * 26544357);
            BlockType trunkType = GetTrunkBlockType(t.treeStyle, blockMappings);

            int localX = t.worldX - baseWorldX;
            int localZ = t.worldZ - baseWorldZ;
            int canopyH = math.max(1, t.canopyHeight);
            int canopyR = math.max(0, t.canopyRadius);
            int horizontalReach = TreeGenerationMetrics.GetHorizontalReach(t.treeStyle, t.trunkHeight, canopyR, canopyH);

            int activeMinX = -writePadding;
            int activeMaxX = chunkSizeX + writePadding - 1;
            int activeMinZ = -writePadding;
            int activeMaxZ = chunkSizeZ + writePadding - 1;

            bool missesActiveArea =
                localX + horizontalReach < activeMinX ||
                localX - horizontalReach > activeMaxX ||
                localZ + horizontalReach < activeMinZ ||
                localZ - horizontalReach > activeMaxZ;

            if (missesActiveArea)
                continue;

            int ix = localX + border;
            int iz = localZ + border;
            int surfaceY = t.surfaceY;
            if (surfaceY < 0 || surfaceY >= chunkSizeY)
                continue;

            int leafBottom = surfaceY + t.trunkHeight - 1;

            if (isFancyOak)
            {
                PlaceFancyOakTree(
                    ix, iz, surfaceY, t.trunkHeight, canopyH, canopyR, treeHash,
                    blockTypes, solids, blockMappings,
                    chunkSizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize, trunkType
                );
                continue;
            }

            for (int dy = 1; dy <= t.trunkHeight; dy++)
            {
                int ty = surfaceY + dy;
                if (ty >= chunkSizeY)
                    break;

                TryPlaceWoodBlock(ix, ty, iz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY, trunkType);
            }

            if (isCactus)
            {
                int armReach = math.clamp(math.max(1, canopyR), 1, 2);
                PlaceCactusStructure(
                    ix, iz, surfaceY, t.trunkHeight, armReach, treeHash,
                    blockTypes, solids, blockMappings,
                    chunkSizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize, trunkType
                );
            }
            else if (isTaigaSpruce)
            {
                PlaceTaigaSpruceCanopy(
                    ix, iz, surfaceY, t.trunkHeight, canopyH, canopyR, treeHash,
                    blockTypes, solids, blockMappings,
                    chunkSizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize
                );
            }
            else if (isSavannaAcacia)
            {
                PlaceSavannaAcaciaCanopy(
                    ix, iz, surfaceY, t.trunkHeight, canopyH, canopyR, treeHash,
                    blockTypes, solids, blockMappings,
                    chunkSizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize, trunkType
                );
            }
            else
            {
                PlaceOakBroadleafCanopy(
                    ix, iz, leafBottom, canopyH, canopyR, treeHash,
                    blockTypes, solids, blockMappings,
                    chunkSizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize
                );
            }

            if (isCactus)
                continue;
        }
    }

    private static BlockType GetTrunkBlockType(TreeStyle treeStyle, NativeArray<BlockTextureMapping> blockMappings)
    {
        switch (treeStyle)
        {
            case TreeStyle.Cactus:
                return GetCactusBlockType(blockMappings);
            case TreeStyle.BirchBroadleaf:
                return (int)BlockType.birch_log < blockMappings.Length
                    ? BlockType.birch_log
                    : BlockType.Log;
            case TreeStyle.SavannaAcacia:
                return (int)BlockType.acacia_log < blockMappings.Length
                    ? BlockType.acacia_log
                    : BlockType.Log;
            default:
                return BlockType.Log;
        }
    }

    private static BlockType GetCactusBlockType(NativeArray<BlockTextureMapping> blockMappings)
    {
        return (int)BlockType.Cactus < blockMappings.Length
            ? BlockType.Cactus
            : BlockType.Log;
    }

    private static bool IsWoodBlock(BlockType blockType)
    {
        return blockType == BlockType.Log ||
               blockType == BlockType.birch_log ||
               blockType == BlockType.acacia_log ||
               blockType == BlockType.Cactus;
    }

    private static int GetTreeTopOffset(TreeStyle treeStyle, int heightValue, int canopyHeight)
    {
        switch (treeStyle)
        {
            case TreeStyle.Cactus:
                return heightValue + 2;

            case TreeStyle.TaigaSpruce:
                return heightValue + math.max(2, canopyHeight);

            case TreeStyle.SavannaAcacia:
                return heightValue + math.max(3, canopyHeight);

            case TreeStyle.FancyOak:
                return math.max(6, heightValue);

            default:
                return heightValue + canopyHeight + 1;
        }
    }

    private static bool TryPlaceWoodBlock(
        int lx,
        int ly,
        int lz,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        int chunkSizeY,
        BlockType trunkType)
    {
        if (lx < 0 || lx >= voxelSizeX || lz < 0 || lz >= voxelSizeZ)
            return false;
        if (ly < 0 || ly >= chunkSizeY)
            return false;

        int mappingIndex = (int)trunkType;
        if (mappingIndex < 0 || mappingIndex >= blockMappings.Length)
            return false;

        int idx = lx + ly * voxelSizeX + lz * voxelPlaneSize;
        BlockType existing = (BlockType)blockTypes[idx];
        if (existing == trunkType)
            return true;
        if (IsWoodBlock(existing))
            return false;
        if (!(existing == BlockType.Air || existing == BlockType.Leaves))
            return false;

        blockTypes[idx] = (byte)trunkType;
        solids[idx] = blockMappings[mappingIndex].isSolid;
        return true;
    }

    private static void PlaceFancyOakTree(
        int ix,
        int iz,
        int surfaceY,
        int heightLimitValue,
        int canopyH,
        int canopyR,
        int treeHash,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int chunkSizeY,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        BlockType trunkType)
    {
        int heightLimit = math.max(6, heightLimitValue);
        int leafDistanceLimit = math.clamp(math.max(4, canopyH), 4, 5);
        int trunkHeight = math.max(1, (int)math.floor(heightLimit * 0.618f));
        if (trunkHeight >= heightLimit)
            trunkHeight = heightLimit - 1;

        int trunkTopY = surfaceY + trunkHeight;
        int topLeafY = surfaceY + heightLimit - leafDistanceLimit;
        float scaleWidth = math.max(1f, canopyR / 4f);
        float leafDensity = math.max(1f, canopyH / 4f);
        float nodeDensity = leafDensity * heightLimit / 13f;
        int nodesPerLayer = math.max(1, (int)(1.382f + nodeDensity * nodeDensity));
        nodesPerLayer = math.clamp(nodesPerLayer, 1, 4);

        PlaceFancyOakLeafNode(
            ix, topLeafY, iz, leafDistanceLimit,
            blockTypes, solids, blockMappings,
            voxelSizeX, voxelSizeZ, voxelPlaneSize
        );

        int minLeafY = surfaceY + (int)math.ceil(heightLimit * 0.3f);
        for (int layerY = topLeafY - 1; layerY >= minLeafY; layerY--)
        {
            float layerSize = GetFancyOakLayerSize(heightLimit, layerY - surfaceY);
            if (layerSize < 0f)
                continue;

            for (int nodeIndex = 0; nodeIndex < nodesPerLayer; nodeIndex++)
            {
                int nodeHash = treeHash ^ (layerY * 73428767) ^ (nodeIndex * 912367421);
                float distance = scaleWidth * layerSize * (0.328f + Hash01(nodeHash));
                float angle = Hash01(nodeHash ^ 1757151723) * 6.2831855f;

                int nodeX = ix + (int)math.floor(distance * math.sin(angle) + 0.5f);
                int nodeZ = iz + (int)math.floor(distance * math.cos(angle) + 0.5f);
                int nodeY = layerY;

                if (!IsFancyOakLeafNodeClear(nodeX, nodeY, nodeZ, leafDistanceLimit, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY))
                    continue;

                int dx = nodeX - ix;
                int dz = nodeZ - iz;
                float horizontalDistance = math.sqrt((float)(dx * dx + dz * dz));
                int branchBaseY;
                if ((float)nodeY - horizontalDistance * 0.381f > trunkTopY)
                    branchBaseY = trunkTopY;
                else
                    branchBaseY = (int)((float)nodeY - horizontalDistance * 0.381f);

                branchBaseY = math.clamp(branchBaseY, surfaceY + 1, trunkTopY);

                if (!IsWoodLineClear(ix, branchBaseY, iz, nodeX, nodeY, nodeZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY))
                    continue;

                PlaceFancyOakLeafNode(
                    nodeX, nodeY, nodeZ, leafDistanceLimit,
                    blockTypes, solids, blockMappings,
                    voxelSizeX, voxelSizeZ, voxelPlaneSize
                );

                if ((float)(nodeY - surfaceY) >= heightLimit * 0.2f)
                {
                    PlaceWoodLine(
                        ix, branchBaseY, iz, nodeX, nodeY, nodeZ,
                        blockTypes, solids, blockMappings,
                        voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY, trunkType
                    );
                }
            }
        }

        PlaceWoodLine(
            ix, surfaceY + 1, iz, ix, trunkTopY, iz,
            blockTypes, solids, blockMappings,
            voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY, trunkType
        );
    }

    private static void PlaceFancyOakLeafNode(
        int centerX,
        int centerY,
        int centerZ,
        int leafDistanceLimit,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        for (int yOffset = 0; yOffset < leafDistanceLimit; yOffset++)
        {
            float radius = GetFancyOakLeafSize(yOffset, leafDistanceLimit);
            if (radius < 0f)
                continue;

            PlaceFancyOakLeafDisc(
                centerX, centerY + yOffset, centerZ, radius,
                blockTypes, solids, blockMappings,
                voxelSizeX, voxelSizeZ, voxelPlaneSize
            );
        }
    }

    private static void PlaceFancyOakLeafDisc(
        int centerX,
        int layerY,
        int centerZ,
        float radius,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        int discRadius = (int)(radius + 0.618f);
        float radiusSq = radius * radius;

        for (int dx = -discRadius; dx <= discRadius; dx++)
        {
            float absDx = math.abs(dx) + 0.5f;
            float absDxSq = absDx * absDx;

            for (int dz = -discRadius; dz <= discRadius; dz++)
            {
                float absDz = math.abs(dz) + 0.5f;
                float distSq = absDxSq + absDz * absDz;
                if (distSq > radiusSq)
                    continue;

                TryPlaceLeaf(centerX + dx, layerY, centerZ + dz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
            }
        }
    }

    private static float GetFancyOakLayerSize(int heightLimit, int layerOffset)
    {
        if ((float)layerOffset < heightLimit * 0.3f)
            return -1.618f;

        float halfHeight = heightLimit * 0.5f;
        float distanceFromCenter = halfHeight - layerOffset;

        if (distanceFromCenter == 0f)
            return halfHeight * 0.5f;
        if (math.abs(distanceFromCenter) >= halfHeight)
            return 0f;

        return math.sqrt(halfHeight * halfHeight - distanceFromCenter * distanceFromCenter) * 0.5f;
    }

    private static float GetFancyOakLeafSize(int layerOffset, int leafDistanceLimit)
    {
        if (layerOffset < 0 || layerOffset >= leafDistanceLimit)
            return -1f;

        return (layerOffset != 0 && layerOffset != leafDistanceLimit - 1) ? 3f : 2f;
    }

    private static bool IsFancyOakLeafNodeClear(
        int centerX,
        int centerY,
        int centerZ,
        int leafDistanceLimit,
        NativeArray<byte> blockTypes,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        int chunkSizeY)
    {
        int maxY = centerY + leafDistanceLimit;
        for (int y = centerY; y <= maxY; y++)
        {
            if (!IsInsideVoxelBounds(centerX, y, centerZ, voxelSizeX, voxelSizeZ, chunkSizeY))
                continue;

            if (!CanLeafReplaceAt(centerX, y, centerZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY))
                return false;
        }

        return true;
    }

    private static bool IsWoodLineClear(
        int startX,
        int startY,
        int startZ,
        int endX,
        int endY,
        int endZ,
        NativeArray<byte> blockTypes,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        int chunkSizeY)
    {
        int dx = endX - startX;
        int dy = endY - startY;
        int dz = endZ - startZ;
        int steps = math.max(math.abs(dx), math.max(math.abs(dy), math.abs(dz)));

        if (steps == 0)
        {
            if (!IsInsideVoxelBounds(startX, startY, startZ, voxelSizeX, voxelSizeZ, chunkSizeY))
                return true;

            return CanWoodReplaceAt(startX, startY, startZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY);
        }

        for (int step = 0; step <= steps; step++)
        {
            float t = (float)step / steps;
            int x = (int)math.floor(startX + dx * t + 0.5f);
            int y = (int)math.floor(startY + dy * t + 0.5f);
            int z = (int)math.floor(startZ + dz * t + 0.5f);

            if (!IsInsideVoxelBounds(x, y, z, voxelSizeX, voxelSizeZ, chunkSizeY))
                continue;

            if (!CanWoodReplaceAt(x, y, z, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY))
                return false;
        }

        return true;
    }

    private static void PlaceWoodLine(
        int startX,
        int startY,
        int startZ,
        int endX,
        int endY,
        int endZ,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        int chunkSizeY,
        BlockType trunkType)
    {
        int dx = endX - startX;
        int dy = endY - startY;
        int dz = endZ - startZ;
        int steps = math.max(math.abs(dx), math.max(math.abs(dy), math.abs(dz)));

        if (steps == 0)
        {
            TryPlaceWoodBlock(startX, startY, startZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY, trunkType);
            return;
        }

        for (int step = 0; step <= steps; step++)
        {
            float t = (float)step / steps;
            int x = (int)math.floor(startX + dx * t + 0.5f);
            int y = (int)math.floor(startY + dy * t + 0.5f);
            int z = (int)math.floor(startZ + dz * t + 0.5f);

            TryPlaceWoodBlock(x, y, z, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY, trunkType);
        }
    }

    private static bool CanLeafReplaceAt(
        int x,
        int y,
        int z,
        NativeArray<byte> blockTypes,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        int chunkSizeY)
    {
        if (!IsInsideVoxelBounds(x, y, z, voxelSizeX, voxelSizeZ, chunkSizeY))
            return false;

        BlockType existing = (BlockType)blockTypes[x + y * voxelSizeX + z * voxelPlaneSize];
        return existing == BlockType.Air || existing == BlockType.Leaves;
    }

    private static bool CanWoodReplaceAt(
        int x,
        int y,
        int z,
        NativeArray<byte> blockTypes,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        int chunkSizeY)
    {
        if (!IsInsideVoxelBounds(x, y, z, voxelSizeX, voxelSizeZ, chunkSizeY))
            return false;

        BlockType existing = (BlockType)blockTypes[x + y * voxelSizeX + z * voxelPlaneSize];
        return existing == BlockType.Air || existing == BlockType.Leaves || IsWoodBlock(existing);
    }

    private static bool IsInsideVoxelBounds(int x, int y, int z, int voxelSizeX, int voxelSizeZ, int chunkSizeY)
    {
        return x >= 0 && x < voxelSizeX &&
               z >= 0 && z < voxelSizeZ &&
               y >= 0 && y < chunkSizeY;
    }

    private static float Hash01(int value)
    {
        uint x = (uint)value;
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return (x & 0x00FFFFFFu) / 16777215f;
    }

    private static void PlaceCactusStructure(
        int ix,
        int iz,
        int surfaceY,
        int trunkHeight,
        int armReach,
        int treeHash,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int chunkSizeY,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        BlockType cactusType)
    {
        int minArmY = surfaceY + math.max(2, trunkHeight / 2);
        int maxArmY = surfaceY + math.max(2, trunkHeight - 1);
        int primaryDir = treeHash & 3;
        int secondaryDir = (primaryDir + (((treeHash >> 2) & 1) == 0 ? 1 : 3)) & 3;
        int armCount = trunkHeight >= 5 ? 2 : (trunkHeight >= 3 ? 1 : 0);

        for (int arm = 0; arm < armCount; arm++)
        {
            int dir = arm == 0 ? primaryDir : secondaryDir;
            int dirX;
            int dirZ;
            GetCardinalDirection(dir, out dirX, out dirZ);

            int armBaseYOffset = 1 + ((treeHash >> (8 + arm * 3)) & 0x1);
            int armBaseY = math.clamp(minArmY + armBaseYOffset, minArmY, maxArmY);
            int length = math.clamp(1 + ((treeHash >> (14 + arm * 2)) & 0x1), 1, math.max(1, armReach));

            int tipX = ix;
            int tipZ = iz;
            for (int step = 1; step <= length; step++)
            {
                int lx = ix + dirX * step;
                int lz = iz + dirZ * step;
                if (!IsInsideVoxelBounds(lx, armBaseY, lz, voxelSizeX, voxelSizeZ, chunkSizeY))
                {
                    tipX = lx;
                    tipZ = lz;
                    continue;
                }

                if (!TryPlaceCactusBlock(lx, armBaseY, lz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY, cactusType))
                    break;

                tipX = lx;
                tipZ = lz;
            }

            bool raiseTip = ((treeHash >> (20 + arm)) & 1) == 1;
            if (raiseTip)
                TryPlaceCactusBlock(tipX, armBaseY + 1, tipZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY, cactusType);
        }
    }

    private static void GetCardinalDirection(int dir, out int dx, out int dz)
    {
        switch (dir & 3)
        {
            case 0:
                dx = 1;
                dz = 0;
                break;
            case 1:
                dx = -1;
                dz = 0;
                break;
            case 2:
                dx = 0;
                dz = 1;
                break;
            default:
                dx = 0;
                dz = -1;
                break;
        }
    }

    private static bool TryPlaceCactusBlock(
        int lx,
        int ly,
        int lz,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        int chunkSizeY,
        BlockType cactusType)
    {
        if (lx < 0 || lx >= voxelSizeX || lz < 0 || lz >= voxelSizeZ)
            return false;
        if (ly < 0 || ly >= chunkSizeY)
            return false;

        int mappingIndex = (int)cactusType;
        if (mappingIndex < 0 || mappingIndex >= blockMappings.Length)
            return false;

        int idx = lx + ly * voxelSizeX + lz * voxelPlaneSize;
        BlockType existing = (BlockType)blockTypes[idx];
        if (!(existing == BlockType.Air || existing == BlockType.Leaves))
            return false;

        blockTypes[idx] = (byte)cactusType;
        solids[idx] = blockMappings[mappingIndex].isSolid;
        return true;
    }

    private static void PlaceOakBroadleafCanopy(
        int ix,
        int iz,
        int leafBottom,
        int canopyH,
        int canopyR,
        int treeHash,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int chunkSizeY,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        for (int dy = 0; dy < canopyH; dy++)
        {
            int ly = leafBottom + dy;
            if (ly < 0 || ly >= chunkSizeY)
                continue;

            if (dy == canopyH - 1)
            {
                for (int dx = -1; dx <= 0; dx++)
                {
                    for (int dz = -1; dz <= 0; dz++)
                    {
                        TryPlaceLeaf(ix + dx, ly, iz + dz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
                    }
                }
                continue;
            }

            int shrink = dy / 2;
            int radius = math.max(0, canopyR - shrink);
            int layerHash = treeHash ^ (dy * 1234567);
            int cornerSkipMask = layerHash & 0xF;

            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int absDx = math.abs(dx);
                    int absDz = math.abs(dz);
                    bool isCorner = (absDx == radius && absDz == radius) && radius > 0;
                    if (isCorner)
                    {
                        int bit = (absDx + absDz + dy) & 3;
                        if (((cornerSkipMask >> bit) & 1) == 1)
                            continue;
                    }

                    TryPlaceLeaf(ix + dx, ly, iz + dz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
                }
            }
        }
    }

    private static void PlaceSavannaAcaciaCanopy(
        int ix,
        int iz,
        int surfaceY,
        int trunkHeight,
        int canopyH,
        int canopyR,
        int treeHash,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int chunkSizeY,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        BlockType trunkType)
    {
        int trunkTopY = surfaceY + trunkHeight;
        int primaryDir = treeHash & 3;
        int dirX;
        int dirZ;
        GetCardinalDirection(primaryDir, out dirX, out dirZ);

        int branchStartY = surfaceY + math.max(3, trunkHeight - 2);
        int primaryBranchLength = math.clamp(1 + ((treeHash >> 3) & 0x1), 1, math.max(1, canopyR - 2));
        int mainCanopyX = ix;
        int mainCanopyZ = iz;
        int mainCanopyY = trunkTopY;

        for (int step = 1; step <= primaryBranchLength; step++)
        {
            mainCanopyX += dirX;
            mainCanopyZ += dirZ;
            mainCanopyY = math.min(chunkSizeY - 1, branchStartY + step);
            TryPlaceWoodBlock(mainCanopyX, mainCanopyY, mainCanopyZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY, trunkType);
        }

        int mainRadius = math.max(2, canopyR - 1);
        PlaceSavannaCanopyPlatform(mainCanopyX, mainCanopyZ, mainCanopyY, mainRadius, treeHash, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize);
        PlaceSavannaCanopyPlatform(mainCanopyX, mainCanopyZ, mainCanopyY + 1, math.max(1, mainRadius - 1), treeHash ^ 2038074743, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize);
        PlaceSavannaLowerFringe(mainCanopyX, mainCanopyZ, mainCanopyY, mainRadius, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize);
        TryPlaceLeaf(mainCanopyX, mainCanopyY + 2, mainCanopyZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);

        if (trunkHeight >= 6)
        {
            int secondaryDir = (primaryDir + (((treeHash >> 6) & 1) == 0 ? 1 : 3)) & 3;
            int secondaryDirX;
            int secondaryDirZ;
            GetCardinalDirection(secondaryDir, out secondaryDirX, out secondaryDirZ);

            int secondaryLength = math.min(1 + ((treeHash >> 8) & 1), math.max(1, canopyR - 2));
            int secondaryBaseY = math.min(chunkSizeY - 1, surfaceY + math.max(3, trunkHeight - 3));
            int secondaryX = ix;
            int secondaryZ = iz;
            int secondaryY = secondaryBaseY;

            for (int step = 1; step <= secondaryLength; step++)
            {
                secondaryX += secondaryDirX;
                secondaryZ += secondaryDirZ;
                secondaryY = math.min(chunkSizeY - 1, secondaryBaseY + (step > 1 ? 1 : 0));
                TryPlaceWoodBlock(secondaryX, secondaryY, secondaryZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY, trunkType);
            }

            int secondaryRadius = math.max(1, mainRadius - 1);
            PlaceSavannaCanopyPlatform(secondaryX, secondaryZ, secondaryY + 1, secondaryRadius, treeHash ^ 461845907, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            TryPlaceLeaf(secondaryX, secondaryY + 2, secondaryZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        }

        PlaceLeafCross(ix, iz, trunkTopY, math.max(1, canopyH - 2), blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize);
    }

    private static void PlaceSavannaCanopyPlatform(
        int centerX,
        int centerZ,
        int layerY,
        int radius,
        int layerHash,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        if (radius <= 0)
        {
            TryPlaceLeaf(centerX, layerY, centerZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
            return;
        }

        int manhattanLimit = radius + (radius > 2 ? 1 : 0);
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                int absDx = math.abs(dx);
                int absDz = math.abs(dz);
                if (absDx + absDz > manhattanLimit)
                    continue;
                if (radius >= 2 && absDx == radius && absDz == radius)
                    continue;
                if (radius >= 3 && absDx == radius && absDz >= radius - 1)
                {
                    int bit = (absDz + layerHash + dx * dx) & 3;
                    if (((layerHash >> bit) & 1) == 1)
                        continue;
                }

                TryPlaceLeaf(centerX + dx, layerY, centerZ + dz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
            }
        }
    }

    private static void PlaceSavannaLowerFringe(
        int centerX,
        int centerZ,
        int canopyY,
        int radius,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        int fringeY = canopyY - 1;
        if (fringeY < 0 || radius <= 0)
            return;

        int fringeRadius = math.max(1, radius - 1);
        TryPlaceLeaf(centerX + fringeRadius, fringeY, centerZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        TryPlaceLeaf(centerX - fringeRadius, fringeY, centerZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        TryPlaceLeaf(centerX, fringeY, centerZ + fringeRadius, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        TryPlaceLeaf(centerX, fringeY, centerZ - fringeRadius, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);

        if (radius >= 3)
        {
            TryPlaceLeaf(centerX + 1, fringeY, centerZ + fringeRadius, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
            TryPlaceLeaf(centerX - 1, fringeY, centerZ - fringeRadius, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        }
    }

    private static void PlaceLeafCross(
        int centerX,
        int centerZ,
        int layerY,
        int radius,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        if (radius <= 0)
        {
            TryPlaceLeaf(centerX, layerY, centerZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
            return;
        }

        for (int step = 1; step <= radius; step++)
        {
            TryPlaceLeaf(centerX - step, layerY, centerZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
            TryPlaceLeaf(centerX + step, layerY, centerZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
            TryPlaceLeaf(centerX, layerY, centerZ - step, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
            TryPlaceLeaf(centerX, layerY, centerZ + step, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        }
    }

    private static void PlaceTaigaSpruceCanopy(
        int ix,
        int iz,
        int surfaceY,
        int trunkHeight,
        int canopyH,
        int canopyR,
        int treeHash,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int chunkSizeY,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        int trunkTopY = surfaceY + trunkHeight;
        int desiredFoliageLayers = math.max(5, canopyH);
        int foliageStartY = math.max(surfaceY + 2, trunkTopY - desiredFoliageLayers + 1);
        int foliageLayers = trunkTopY - foliageStartY + 1;
        int maxRadius = math.max(2, canopyR);

        if (foliageLayers <= 0)
            return;

        for (int layerFromTop = 0; layerFromTop < foliageLayers; layerFromTop++)
        {
            int ly = trunkTopY - layerFromTop;
            if (ly < 0 || ly >= chunkSizeY)
                continue;

            int radius = GetTaigaSpruceLayerRadius(layerFromTop, foliageLayers, maxRadius);

            PlaceSpruceLayer(
                ix, iz, ly, radius,
                blockTypes, solids, blockMappings,
                voxelSizeX, voxelSizeZ, voxelPlaneSize
            );
        }

        PlaceSpruceTopCrown(
            ix, iz, trunkTopY,
            blockTypes, solids, blockMappings,
            voxelSizeX, voxelSizeZ, voxelPlaneSize
        );
    }

    private static int GetTaigaSpruceLayerRadius(int layerFromTop, int foliageLayers, int maxRadius)
    {
        if (maxRadius <= 0)
            return 0;

        int largeRingRadius = math.min(3, maxRadius);
        int smallRingRadius = 1;

        int layerFromBottom = (foliageLayers - 1) - layerFromTop;
        bool useLargeRing = (layerFromBottom & 1) == 0;
        int radius = useLargeRing ? largeRingRadius : smallRingRadius;

        return math.clamp(radius, 1, maxRadius);
    }

    private static void PlaceSpruceTopCrown(
        int ix,
        int iz,
        int trunkTopY,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        TryPlaceLeaf(ix, trunkTopY + 1, iz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        TryPlaceLeaf(ix, trunkTopY + 2, iz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);

        PlaceSpruceTopCross(ix, iz, trunkTopY, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize);
        PlaceSpruceTopCross(ix, iz, trunkTopY + 1, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize);
    }

    private static void PlaceSpruceTopCross(
        int ix,
        int iz,
        int ly,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        TryPlaceLeaf(ix - 1, ly, iz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        TryPlaceLeaf(ix + 1, ly, iz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        TryPlaceLeaf(ix, ly, iz - 1, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        TryPlaceLeaf(ix, ly, iz + 1, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
    }

    private static void PlaceSpruceLayer(
        int ix,
        int iz,
        int ly,
        int radius,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                int absDx = math.abs(dx);
                int absDz = math.abs(dz);

                if (radius >= 2 && absDx == radius && absDz == radius)
                    continue;

                TryPlaceLeaf(ix + dx, ly, iz + dz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
            }
        }
    }

    private static void TryPlaceLeaf(
        int lx,
        int ly,
        int lz,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        bool skipCenter)
    {
        if (skipCenter)
            return;

        int chunkSizeY = voxelPlaneSize / voxelSizeX;
        if ((uint)lx >= (uint)voxelSizeX ||
            (uint)lz >= (uint)voxelSizeZ ||
            (uint)ly >= (uint)chunkSizeY)
        {
            return;
        }

        int lidx = lx + ly * voxelSizeX + lz * voxelPlaneSize;
        BlockType existing = (BlockType)blockTypes[lidx];
        if (IsWoodBlock(existing))
            return;
        if (!(existing == BlockType.Air || existing == BlockType.Leaves))
            return;

        blockTypes[lidx] = (byte)BlockType.Leaves;
        solids[lidx] = blockMappings[(int)BlockType.Leaves].isSolid;
    }
}

public static class TreeGroundSupport
{
    private const int FootprintRadius = 1;
    private const int SupportDepthProbe = 4;
    private const int NeighborSurfaceSearchUp = 1;
    private const int NeighborSurfaceSearchDown = 2;
    private const int MinCenterSupportDepth = 3;
    private const int MinNeighborSupportDepth = 2;
    private const int MinSupportedColumns = 6;
    private const int MinStrongColumns = 4;
    private const int MinSupportedCardinals = 3;
    private const int SideOpeningProbeDistance = 2;
    private const int SideOpeningMinOpenCells = 2;
    [BurstCompile]
    public static bool TryEvaluateStableSupportAtWorldColumn(
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        Vector2Int coord,
        int border,
        int chunkSizeX,
        int chunkSizeZ,
        int chunkSizeY,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        int worldX,
        int surfaceY,
        int worldZ,
        out bool hasStableSupport)
    {
        int baseWorldX = coord.x * chunkSizeX;
        int baseWorldZ = coord.y * chunkSizeZ;
        int ix = worldX - baseWorldX + border;
        int iz = worldZ - baseWorldZ + border;

        return TryEvaluateStableSupportAtLocalColumn(
            blockTypes,
            solids,
            ix,
            surfaceY,
            iz,
            chunkSizeY,
            voxelSizeX,
            voxelSizeZ,
            voxelPlaneSize,
            out hasStableSupport);
    }

    [BurstCompile]
    public static bool TryEvaluateStableSupportAtLocalColumn(
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        int ix,
        int surfaceY,
        int iz,
        int chunkSizeY,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        out bool hasStableSupport)
    {
        hasStableSupport = false;

        if (surfaceY <= 0 || surfaceY >= chunkSizeY - 1)
            return false;
        if (!IsInsideVoxelBounds(ix, surfaceY - 1, iz, voxelSizeX, voxelSizeZ, chunkSizeY))
            return false;
        if (!IsInsideVoxelBounds(ix, surfaceY, iz, voxelSizeX, voxelSizeZ, chunkSizeY))
            return false;
        if (!IsInsideVoxelBounds(ix, surfaceY + 1, iz, voxelSizeX, voxelSizeZ, chunkSizeY))
            return false;

        int belowIdx = ix + (surfaceY - 1) * voxelSizeX + iz * voxelPlaneSize;
        int groundIdx = ix + surfaceY * voxelSizeX + iz * voxelPlaneSize;
        int trunkBaseIdx = ix + (surfaceY + 1) * voxelSizeX + iz * voxelPlaneSize;

        // Exige um "pÃ©" realmente apoiado no terreno e nÃ£o em um teto fino de caverna.
        if (!IsStableGroundBlock((BlockType)blockTypes[groundIdx], solids[groundIdx]))
            return true;
        if (!IsStableGroundBlock((BlockType)blockTypes[belowIdx], solids[belowIdx]))
            return true;
        if (!IsTreeReplaceable((BlockType)blockTypes[trunkBaseIdx], solids[trunkBaseIdx]))
            return true;

        int centerDepth = MeasureSupportDepthAtGround(
            blockTypes,
            solids,
            ix,
            surfaceY,
            iz,
            voxelSizeX,
            voxelPlaneSize,
            MinCenterSupportDepth);
        if (centerDepth < MinCenterSupportDepth)
            return true;

        // Fill ratio over a 3x3 footprint keeps roots away from cave lips and thin overhangs.
        int supportedColumns = 0;
        int strongColumns = 0;
        int supportedCardinals = 0;

        for (int dz = -FootprintRadius; dz <= FootprintRadius; dz++)
        {
            for (int dx = -FootprintRadius; dx <= FootprintRadius; dx++)
            {
                int sampleDepth = MeasureSupportDepthNearSurface(
                    blockTypes,
                    solids,
                    ix + dx,
                    surfaceY,
                    iz + dz,
                    chunkSizeY,
                    voxelSizeX,
                    voxelSizeZ,
                    voxelPlaneSize);

                if (sampleDepth >= MinNeighborSupportDepth)
                {
                    supportedColumns++;
                    if ((dx == 0) != (dz == 0))
                        supportedCardinals++;
                }

                if (sampleDepth >= MinCenterSupportDepth)
                    strongColumns++;
            }
        }

        if (supportedColumns < MinSupportedColumns ||
            strongColumns < MinStrongColumns ||
            supportedCardinals < MinSupportedCardinals)
        {
            return true;
        }

        // A real cave mouth stays hollow for more than one block in a direction.
        if (HasSideOpening(blockTypes, solids, ix, surfaceY, iz, 1, 0, chunkSizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize) ||
            HasSideOpening(blockTypes, solids, ix, surfaceY, iz, -1, 0, chunkSizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize) ||
            HasSideOpening(blockTypes, solids, ix, surfaceY, iz, 0, 1, chunkSizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize) ||
            HasSideOpening(blockTypes, solids, ix, surfaceY, iz, 0, -1, chunkSizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize))
        {
            return true;
        }

        hasStableSupport = true;
        return true;
    }

    private static int MeasureSupportDepthNearSurface(
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        int ix,
        int surfaceY,
        int iz,
        int chunkSizeY,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        int maxGroundY = math.min(chunkSizeY - 2, surfaceY + NeighborSurfaceSearchUp);
        int minGroundY = math.max(1, surfaceY - NeighborSurfaceSearchDown);

        for (int groundY = maxGroundY; groundY >= minGroundY; groundY--)
        {
            if (!IsInsideVoxelBounds(ix, groundY, iz, voxelSizeX, voxelSizeZ, chunkSizeY))
                continue;
            if (!IsInsideVoxelBounds(ix, groundY + 1, iz, voxelSizeX, voxelSizeZ, chunkSizeY))
                continue;

            int groundIndex = ix + groundY * voxelSizeX + iz * voxelPlaneSize;
            int aboveIndex = ix + (groundY + 1) * voxelSizeX + iz * voxelPlaneSize;
            if (!IsStableGroundBlock((BlockType)blockTypes[groundIndex], solids[groundIndex]))
                continue;
            if (!IsTreeReplaceable((BlockType)blockTypes[aboveIndex], solids[aboveIndex]))
                continue;

            return MeasureSupportDepthAtGround(
                blockTypes,
                solids,
                ix,
                groundY,
                iz,
                voxelSizeX,
                voxelPlaneSize,
                SupportDepthProbe);
        }

        return 0;
    }

    private static int MeasureSupportDepthAtGround(
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        int ix,
        int groundY,
        int iz,
        int voxelSizeX,
        int voxelPlaneSize,
        int maxDepth)
    {
        int depth = 0;
        int minY = math.max(0, groundY - math.max(0, maxDepth - 1));

        for (int y = groundY; y >= minY; y--)
        {
            int index = ix + y * voxelSizeX + iz * voxelPlaneSize;
            if (!IsStableGroundBlock((BlockType)blockTypes[index], solids[index]))
                break;

            depth++;
        }

        return depth;
    }

    private static bool HasSideOpening(
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        int ix,
        int surfaceY,
        int iz,
        int dirX,
        int dirZ,
        int chunkSizeY,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        for (int step = 1; step <= SideOpeningProbeDistance; step++)
        {
            int sampleX = ix + dirX * step;
            int sampleZ = iz + dirZ * step;
            if (!IsInsideVoxelBounds(sampleX, surfaceY, sampleZ, voxelSizeX, voxelSizeZ, chunkSizeY))
                return false;
            if (!IsInsideVoxelBounds(sampleX, math.max(0, surfaceY - 2), sampleZ, voxelSizeX, voxelSizeZ, chunkSizeY))
                return false;

            int supportDepth = MeasureSupportDepthNearSurface(
                blockTypes,
                solids,
                sampleX,
                surfaceY,
                sampleZ,
                chunkSizeY,
                voxelSizeX,
                voxelSizeZ,
                voxelPlaneSize);
            if (supportDepth >= MinNeighborSupportDepth)
                return false;

            int openCells = CountOpenCellsNearSurface(
                blockTypes,
                solids,
                sampleX,
                surfaceY,
                sampleZ,
                voxelSizeX,
                voxelPlaneSize,
                3);
            if (openCells < SideOpeningMinOpenCells)
                return false;
        }

        return true;
    }

    private static int CountOpenCellsNearSurface(
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        int ix,
        int surfaceY,
        int iz,
        int voxelSizeX,
        int voxelPlaneSize,
        int sampleDepth)
    {
        int openCells = 0;
        int minY = math.max(0, surfaceY - math.max(0, sampleDepth - 1));

        for (int y = surfaceY; y >= minY; y--)
        {
            int index = ix + y * voxelSizeX + iz * voxelPlaneSize;
            if (IsOpenBelowGroundBlock((BlockType)blockTypes[index], solids[index]))
                openCells++;
        }

        return openCells;
    }

    private static bool IsOpenBelowGroundBlock(BlockType blockType, bool isSolid)
    {
        if (FluidBlockUtility.IsWater(blockType))
            return true;
        if (blockType == BlockType.Leaves)
            return true;

        return !isSolid || blockType == BlockType.Air;
    }

    private static bool IsStableGroundBlock(BlockType blockType, bool isSolid)
    {
        if (!isSolid)
            return false;
        if (blockType == BlockType.Air || blockType == BlockType.Leaves)
            return false;
        if (FluidBlockUtility.IsWater(blockType))
            return false;

        return !IsWoodBlock(blockType);
    }

    private static bool IsTreeReplaceable(BlockType blockType, bool isSolid)
    {
        if (blockType == BlockType.Air || blockType == BlockType.Leaves)
            return true;
        if (FluidBlockUtility.IsWater(blockType))
            return false;

        return !isSolid;
    }

    private static bool IsWoodBlock(BlockType blockType)
    {
        return blockType == BlockType.Log ||
               blockType == BlockType.birch_log ||
               blockType == BlockType.acacia_log ||
               blockType == BlockType.Cactus;
    }

    private static bool IsInsideVoxelBounds(int x, int y, int z, int voxelSizeX, int voxelSizeZ, int chunkSizeY)
    {
        return x >= 0 && x < voxelSizeX &&
               z >= 0 && z < voxelSizeZ &&
               y >= 0 && y < chunkSizeY;
    }
}



