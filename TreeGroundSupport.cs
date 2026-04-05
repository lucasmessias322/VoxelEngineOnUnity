using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

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

        // Exige um "pe" realmente apoiado no terreno e nao em um teto fino de caverna.
        if (!IsStableGroundBlock((BlockType)blockTypes[groundIdx], solids[groundIdx]))
            return true;
        if (!IsStableGroundBlock((BlockType)blockTypes[belowIdx], solids[belowIdx]))
            return true;
        if (!IsTreeReplaceable((BlockType)blockTypes[trunkBaseIdx], solids[trunkBaseIdx]))
            return true;

        int centerDepth = MeasureSupportDepthAtGround(blockTypes, solids, ix, surfaceY, iz, voxelSizeX, voxelPlaneSize, MinCenterSupportDepth);
        if (centerDepth < MinCenterSupportDepth)
            return true;

        int supportedColumns = 0;
        int strongColumns = 0;
        int supportedCardinals = 0;

        for (int dz = -FootprintRadius; dz <= FootprintRadius; dz++)
        {
            for (int dx = -FootprintRadius; dx <= FootprintRadius; dx++)
            {
                int sampleDepth = MeasureSupportDepthNearSurface(blockTypes, solids, ix + dx, surfaceY, iz + dz, chunkSizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize);
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

        if (supportedColumns < MinSupportedColumns || strongColumns < MinStrongColumns || supportedCardinals < MinSupportedCardinals)
            return true;

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
            if (!IsInsideVoxelBounds(ix, groundY, iz, voxelSizeX, voxelSizeZ, chunkSizeY) ||
                !IsInsideVoxelBounds(ix, groundY + 1, iz, voxelSizeX, voxelSizeZ, chunkSizeY))
            {
                continue;
            }

            int groundIndex = ix + groundY * voxelSizeX + iz * voxelPlaneSize;
            int aboveIndex = ix + (groundY + 1) * voxelSizeX + iz * voxelPlaneSize;
            if (!IsStableGroundBlock((BlockType)blockTypes[groundIndex], solids[groundIndex]))
                continue;
            if (!IsTreeReplaceable((BlockType)blockTypes[aboveIndex], solids[aboveIndex]))
                continue;

            return MeasureSupportDepthAtGround(blockTypes, solids, ix, groundY, iz, voxelSizeX, voxelPlaneSize, SupportDepthProbe);
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
            if (!IsInsideVoxelBounds(sampleX, surfaceY, sampleZ, voxelSizeX, voxelSizeZ, chunkSizeY) ||
                !IsInsideVoxelBounds(sampleX, math.max(0, surfaceY - 2), sampleZ, voxelSizeX, voxelSizeZ, chunkSizeY))
            {
                return false;
            }

            int supportDepth = MeasureSupportDepthNearSurface(blockTypes, solids, sampleX, surfaceY, sampleZ, chunkSizeY, voxelSizeX, voxelSizeZ, voxelPlaneSize);
            if (supportDepth >= MinNeighborSupportDepth)
                return false;

            int openCells = CountOpenCellsNearSurface(blockTypes, solids, sampleX, surfaceY, sampleZ, voxelSizeX, voxelPlaneSize, 3);
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
        if (FluidBlockUtility.IsWater(blockType) || blockType == BlockType.Leaves)
            return true;

        return !isSolid || blockType == BlockType.Air;
    }

    private static bool IsStableGroundBlock(BlockType blockType, bool isSolid)
    {
        if (!isSolid || blockType == BlockType.Air || blockType == BlockType.Leaves || FluidBlockUtility.IsWater(blockType))
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
