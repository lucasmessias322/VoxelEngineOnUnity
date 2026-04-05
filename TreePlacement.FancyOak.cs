using Unity.Collections;
using Unity.Mathematics;

public static partial class TreePlacement
{
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

        PlaceFancyOakLeafNode(ix, topLeafY, iz, leafDistanceLimit, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize);

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
                int branchBaseY = (float)nodeY - horizontalDistance * 0.381f > trunkTopY
                    ? trunkTopY
                    : (int)((float)nodeY - horizontalDistance * 0.381f);
                branchBaseY = math.clamp(branchBaseY, surfaceY + 1, trunkTopY);

                if (!IsWoodLineClear(ix, branchBaseY, iz, nodeX, nodeY, nodeZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY))
                    continue;

                PlaceFancyOakLeafNode(nodeX, nodeY, nodeZ, leafDistanceLimit, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize);

                if ((float)(nodeY - surfaceY) >= heightLimit * 0.2f)
                {
                    PlaceWoodLine(
                        ix, branchBaseY, iz, nodeX, nodeY, nodeZ,
                        blockTypes, solids, blockMappings,
                        voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY, trunkType);
                }
            }
        }

        PlaceWoodLine(
            ix, surfaceY + 1, iz, ix, trunkTopY, iz,
            blockTypes, solids, blockMappings,
            voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY, trunkType);
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

            PlaceFancyOakLeafDisc(centerX, centerY + yOffset, centerZ, radius, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize);
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
}
