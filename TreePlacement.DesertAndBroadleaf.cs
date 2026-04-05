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
}
