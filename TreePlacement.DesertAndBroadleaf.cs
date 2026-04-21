using Unity.Collections;
using Unity.Mathematics;

public static partial class TreePlacement
{
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
            GetCardinalDirection(dir, out int dirX, out int dirZ);

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
            case 0: dx = 1; dz = 0; break;
            case 1: dx = -1; dz = 0; break;
            case 2: dx = 0; dz = 1; break;
            default: dx = 0; dz = -1; break;
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
        if (lx < 0 || lx >= voxelSizeX || lz < 0 || lz >= voxelSizeZ || ly < 0 || ly >= chunkSizeY)
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
                        TryPlaceLeaf(ix + dx, ly, iz + dz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
                }

                continue;
            }

            int shrink = dy / 2;
            int radius = math.max(0, canopyR - shrink);
            int cornerSkipMask = (treeHash ^ (dy * 1234567)) & 0xF;

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

    private static void PlaceBirchBroadleafCanopy(
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
        int widestRadius = math.max(2, canopyR);
        int totalLayers = math.max(6, canopyH + 3);
        int trunkTopY = surfaceY + trunkHeight;
        int canopyBaseY = trunkTopY;

        PlaceBirchInternalWood(
            ix, iz, canopyBaseY, totalLayers, widestRadius, treeHash,
            blockTypes, solids, blockMappings,
            chunkSizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize, trunkType);

        for (int layer = 0; layer < totalLayers; layer++)
        {
            int ly = canopyBaseY + layer;
            if (ly < 0 || ly >= chunkSizeY)
                continue;

            int radius = GetBirchLayerRadius(widestRadius, totalLayers, layer);
            int layerHash = treeHash ^ (layer * 73428767);

            PlaceBirchLeafLayer(
                ix, iz, ly, radius, widestRadius, layer, totalLayers, layerHash,
                blockTypes, solids, blockMappings,
                voxelSizeX, voxelSizeZ, voxelPlaneSize);
        }
    }

    private static void PlaceBirchInternalWood(
        int centerX,
        int centerZ,
        int canopyBaseY,
        int totalLayers,
        int widestRadius,
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
        int spineExtraLayers = math.clamp(totalLayers / 2, 2, 4);
        int spineTopY = math.min(chunkSizeY - 1, canopyBaseY + spineExtraLayers);

        PlaceWoodLine(
            centerX, canopyBaseY, centerZ, centerX, spineTopY, centerZ,
            blockTypes, solids, blockMappings,
            voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY, trunkType);

        int branchCount = 2 + ((treeHash >> 5) & 1);
        int canopyTopY = math.min(chunkSizeY - 1, canopyBaseY + totalLayers - 2);

        for (int branchIndex = 0; branchIndex < branchCount; branchIndex++)
        {
            int branchHash = treeHash ^ (branchIndex * 912367421);
            float branchLevelT = branchCount == 1
                ? 0.45f
                : math.lerp(0.24f, 0.72f, (float)branchIndex / (branchCount - 1));
            int branchBaseOffset = math.clamp(
                (int)math.round(branchLevelT * math.max(2, totalLayers - 2)),
                1,
                math.max(1, totalLayers - 2));
            int branchBaseY = math.min(spineTopY, canopyBaseY + branchBaseOffset);
            int desiredReach = math.clamp(
                1 + (int)math.round(Hash01(branchHash ^ 0x3AB5C7D1) * math.max(1, widestRadius - 1)),
                1,
                math.max(1, widestRadius));
            float angle = Hash01(branchHash) * 6.2831855f;
            int desiredRise = Hash01(branchHash ^ 0x61C88647) > 0.62f ? 1 : 0;
            int endX = centerX;
            int endZ = centerZ;
            int endY = branchBaseY;
            bool foundValidEndpoint = false;

            for (int reach = desiredReach; reach >= 1 && !foundValidEndpoint; reach--)
            {
                int candidateEndX = centerX + (int)math.round(math.cos(angle) * reach);
                int candidateEndZ = centerZ + (int)math.round(math.sin(angle) * reach);

                for (int rise = desiredRise; rise >= 0; rise--)
                {
                    int candidateEndY = math.min(canopyTopY, branchBaseY + rise);
                    if (!DoesBirchWoodLineFitCanopy(
                            centerX, centerZ, canopyBaseY, totalLayers, widestRadius, treeHash,
                            centerX, branchBaseY, centerZ, candidateEndX, candidateEndY, candidateEndZ))
                    {
                        continue;
                    }

                    endX = candidateEndX;
                    endZ = candidateEndZ;
                    endY = candidateEndY;
                    foundValidEndpoint = true;
                    break;
                }
            }

            if (!foundValidEndpoint)
                continue;

            PlaceWoodLine(
                centerX, branchBaseY, centerZ, endX, endY, endZ,
                blockTypes, solids, blockMappings,
                voxelSizeX, voxelSizeZ, voxelPlaneSize, chunkSizeY, trunkType);

            PlaceBirchInnerLeafPocket(
                endX, endY, endZ, branchHash,
                blockTypes, solids, blockMappings,
                voxelSizeX, voxelSizeZ, voxelPlaneSize);
        }
    }

    private static bool DoesBirchWoodLineFitCanopy(
        int centerX,
        int centerZ,
        int canopyBaseY,
        int totalLayers,
        int widestRadius,
        int treeHash,
        int startX,
        int startY,
        int startZ,
        int endX,
        int endY,
        int endZ)
    {
        int dx = endX - startX;
        int dy = endY - startY;
        int dz = endZ - startZ;
        int steps = math.max(math.abs(dx), math.max(math.abs(dy), math.abs(dz)));

        if (steps == 0)
        {
            return IsInsideBirchCanopyEnvelope(
                centerX, centerZ, canopyBaseY, totalLayers, widestRadius, treeHash,
                startX, startY, startZ, 0.68f);
        }

        for (int step = 0; step <= steps; step++)
        {
            float t = (float)step / steps;
            int x = (int)math.floor(startX + dx * t + 0.5f);
            int y = (int)math.floor(startY + dy * t + 0.5f);
            int z = (int)math.floor(startZ + dz * t + 0.5f);
            float threshold = step == steps ? 0.52f : 0.68f;

            if (!IsInsideBirchCanopyEnvelope(
                    centerX, centerZ, canopyBaseY, totalLayers, widestRadius, treeHash,
                    x, y, z, threshold))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsInsideBirchCanopyEnvelope(
        int centerX,
        int centerZ,
        int canopyBaseY,
        int totalLayers,
        int widestRadius,
        int treeHash,
        int x,
        int y,
        int z,
        float threshold)
    {
        int layerIndex = y - canopyBaseY;
        if (layerIndex < 0 || layerIndex >= totalLayers)
            return false;

        int radius = GetBirchLayerRadius(widestRadius, totalLayers, layerIndex);
        if (radius <= 0)
            return x == centerX && z == centerZ;

        int layerHash = treeHash ^ (layerIndex * 73428767);
        float xScale = ((layerHash >> 2) & 1) == 0 ? 0.95f : 1.08f;
        float zScale = ((layerHash >> 3) & 1) == 0 ? 1.08f : 0.95f;
        float radiusPadding = math.max(0.5f, radius + 0.35f);
        float normalizedX = (math.abs(x - centerX) + 0.25f) / (radiusPadding * xScale);
        float normalizedZ = (math.abs(z - centerZ) + 0.25f) / (radiusPadding * zScale);
        float dist = normalizedX * normalizedX + normalizedZ * normalizedZ;
        return dist <= threshold;
    }

    private static void PlaceBirchInnerLeafPocket(
        int centerX,
        int centerY,
        int centerZ,
        int pocketHash,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        TryPlaceLeaf(centerX + 1, centerY, centerZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        TryPlaceLeaf(centerX - 1, centerY, centerZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        TryPlaceLeaf(centerX, centerY, centerZ + 1, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        TryPlaceLeaf(centerX, centerY, centerZ - 1, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);

        if (Hash01(pocketHash ^ 0x51ED270B) > 0.35f)
            TryPlaceLeaf(centerX, centerY + 1, centerZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);

        if (Hash01(pocketHash ^ 0x2F3A5C71) > 0.55f)
            TryPlaceLeaf(centerX, centerY - 1, centerZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
    }

    private static int GetBirchLayerRadius(int widestRadius, int totalLayers, int layer)
    {
        if (layer >= totalLayers - 1)
            return 0;

        float t = totalLayers <= 1 ? 0f : (float)layer / (totalLayers - 1);
        float radiusFactor;

        if (t < 0.18f)
        {
            radiusFactor = math.lerp(0.45f, 0.7f, t / 0.18f);
        }
        else if (t < 0.42f)
        {
            radiusFactor = math.lerp(0.7f, 1f, (t - 0.18f) / 0.24f);
        }
        else if (t < 0.72f)
        {
            radiusFactor = math.lerp(1f, 0.82f, (t - 0.42f) / 0.30f);
        }
        else if (t < 0.9f)
        {
            radiusFactor = math.lerp(0.82f, 0.45f, (t - 0.72f) / 0.18f);
        }
        else
        {
            radiusFactor = math.lerp(0.45f, 0.12f, (t - 0.9f) / 0.1f);
        }

        int radius = (int)math.round(widestRadius * radiusFactor);
        if (radiusFactor > 0.18f)
            radius = math.max(1, radius);

        return math.clamp(radius, 0, widestRadius);
    }

    private static void PlaceBirchLeafLayer(
        int centerX,
        int centerZ,
        int layerY,
        int radius,
        int widestRadius,
        int layerIndex,
        int totalLayers,
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

        float xScale = ((layerHash >> 2) & 1) == 0 ? 0.95f : 1.08f;
        float zScale = ((layerHash >> 3) & 1) == 0 ? 1.08f : 0.95f;
        float radiusPadding = radius + 0.35f;

        for (int dx = -radius; dx <= radius; dx++)
        {
            float normalizedX = (math.abs(dx) + 0.25f) / (radiusPadding * xScale);

            for (int dz = -radius; dz <= radius; dz++)
            {
                float normalizedZ = (math.abs(dz) + 0.25f) / (radiusPadding * zScale);
                float dist = normalizedX * normalizedX + normalizedZ * normalizedZ;
                if (dist > 1f)
                    continue;

                bool isEdge = dist > 0.58f;
                bool isCorner = math.abs(dx) == radius && math.abs(dz) == radius;
                int cellHash = layerHash ^ (dx * 912367421) ^ (dz * 73428767);

                float skipChance = 0f;
                if (isCorner)
                    skipChance += 0.5f;
                else if (isEdge)
                    skipChance += radius >= 2 ? 0.18f : 0.08f;

                if (layerIndex == 0)
                    skipChance += 0.16f;
                else if (layerIndex == totalLayers - 2)
                    skipChance += 0.22f;

                if (skipChance > 0f && Hash01(cellHash) < skipChance)
                    continue;

                int leafX = centerX + dx;
                int leafZ = centerZ + dz;
                TryPlaceLeaf(leafX, layerY, leafZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);

                bool canDroop =
                    isEdge &&
                    radius >= math.max(1, widestRadius - 1) &&
                    layerIndex > 0 &&
                    layerIndex < totalLayers - 2;

                if (canDroop && Hash01(cellHash ^ 0x51ED270B) > 0.84f)
                {
                    TryPlaceLeaf(leafX, layerY - 1, leafZ, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
                }
            }
        }
    }

}
