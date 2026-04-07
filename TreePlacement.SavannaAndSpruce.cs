using Unity.Collections;
using Unity.Mathematics;

public static partial class TreePlacement
{
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
        GetCardinalDirection(primaryDir, out int dirX, out int dirZ);

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
            GetCardinalDirection(secondaryDir, out int secondaryDirX, out int secondaryDirZ);

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
        int trunkClearance,
        int treeHash,
        NativeArray<byte> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int chunkSizeY,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        int totalHeight = math.max(4, trunkHeight);
        int topY = surfaceY + totalHeight;
        if (topY < 0 || surfaceY >= chunkSizeY - 1)
            return;

        int clampedTopY = math.min(chunkSizeY - 1, topY);
        uint rngState = InitializeTreeRandom(treeHash);

        // canopyH controls foliage depth; trunkClearance controls how much trunk stays exposed.
        bool wrapWholeTrunk = trunkClearance <= 0;
        int foliageBaseY;
        if (wrapWholeTrunk)
        {
            // trunkClearance == 0 means "start foliage right above ground".
            foliageBaseY = surfaceY + 1;
        }
        else
        {
            int availableLayers = math.max(1, clampedTopY - (surfaceY + 1) + 1);
            int desiredLayers = math.max(3, canopyH);
            int foliageLayers = math.clamp(desiredLayers, 1, availableLayers);

            int baseFromLayers = clampedTopY - foliageLayers + 1;
            int clampedClearance = math.clamp(trunkClearance, 0, totalHeight - 1);
            int baseFromClearance = surfaceY + 1 + clampedClearance;

            foliageBaseY = math.max(baseFromLayers, baseFromClearance);
            foliageBaseY += NextRandomInt(ref rngState, 2);
        }
        foliageBaseY = math.clamp(foliageBaseY, surfaceY + 1, clampedTopY);

        int configuredRadius = math.max(2, canopyR);
        int maxRadius = math.clamp(math.max(configuredRadius, 2 + NextRandomInt(ref rngState, 2)), 2, 3);

        int minimumLayerRadius = wrapWholeTrunk ? 1 : 0;
        int layerRadius = minimumLayerRadius + NextRandomInt(ref rngState, 2);
        int growthThreshold = 1;
        int resetRadius = minimumLayerRadius;

        for (int ly = clampedTopY; ly >= foliageBaseY; ly--)
        {
            PlaceSpruceLayer(ix, iz, ly, layerRadius, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize);

            if (layerRadius >= growthThreshold)
            {
                layerRadius = resetRadius;
                resetRadius = 1;
                growthThreshold = math.min(maxRadius, growthThreshold + 1);
            }
            else
            {
                layerRadius++;
            }
        }

        if (clampedTopY + 1 < chunkSizeY)
            TryPlaceLeaf(ix, clampedTopY + 1, iz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
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

    private static uint InitializeTreeRandom(int treeHash)
    {
        uint state = (uint)treeHash ^ 0x9E3779B9u;
        if (state == 0u)
            state = 0xA341316Cu;
        return state;
    }

    private static int NextRandomInt(ref uint state, int maxExclusive)
    {
        if (maxExclusive <= 1)
            return 0;

        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (int)(state % (uint)maxExclusive);
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
        if ((uint)lx >= (uint)voxelSizeX || (uint)lz >= (uint)voxelSizeZ || (uint)ly >= (uint)chunkSizeY)
            return;

        int index = lx + ly * voxelSizeX + lz * voxelPlaneSize;
        BlockType existing = (BlockType)blockTypes[index];
        if (IsWoodBlock(existing))
            return;
        if (!(existing == BlockType.Air || existing == BlockType.Leaves))
            return;

        blockTypes[index] = (byte)BlockType.Leaves;
        solids[index] = blockMappings[(int)BlockType.Leaves].isSolid;
    }
}
