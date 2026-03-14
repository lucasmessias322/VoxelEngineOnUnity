using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public static class TreePlacement
{
    [BurstCompile]
    public static void ApplyTreeInstancesToVoxels(
        NativeArray<BlockType> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        NativeArray<TreeInstance> treeInstances,
        Vector2Int coord,
        int border,
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

            int treeHash = (t.worldX * 73856093) ^ (t.worldZ * 19349663) ^ (t.trunkHeight * 83492791) ^ ((int)t.treeStyle * 26544357);
            BlockType trunkType = GetTrunkBlockType(t.treeStyle, blockMappings);

            int localX = t.worldX - baseWorldX;
            int localZ = t.worldZ - baseWorldZ;
            int canopyH = math.max(1, t.canopyHeight);
            int canopyR = math.max(0, t.canopyRadius);

            if (localX < -canopyR || localX >= chunkSizeX + canopyR ||
                localZ < -canopyR || localZ >= chunkSizeZ + canopyR)
                continue;

            int ix = localX + border;
            int iz = localZ + border;

            if (ix < 0 || ix >= heightStride || iz < 0 || iz >= heightStride ||
                ix >= voxelSizeX || iz >= voxelSizeZ)
                continue;

            int cacheIdx = ix + iz * heightStride;
            int surfaceY = heightCache[cacheIdx];
            if (surfaceY < 0 || surfaceY >= chunkSizeY)
                continue;

            int groundIdx = ix + surfaceY * voxelSizeX + iz * voxelPlaneSize;
            BlockType groundType = blockTypes[groundIdx];
            if (isCactus)
            {
                if (groundType != BlockType.Sand)
                    continue;
            }
            else if (groundType != BlockType.Grass && groundType != BlockType.Dirt && groundType != BlockType.Snow)
            {
                continue;
            }

            int leafBottom = surfaceY + t.trunkHeight - 1;
            bool skipTree = false;

            for (int dy = 1; dy <= t.trunkHeight; dy++)
            {
                int ty = surfaceY + dy;
                if (ty >= chunkSizeY)
                    break;

                int tidx = ix + ty * voxelSizeX + iz * voxelPlaneSize;
                if (blockTypes[tidx] == BlockType.Water)
                {
                    skipTree = true;
                    break;
                }
            }

            if (skipTree)
                continue;

            int extraTop = isSavannaAcacia ? 3 : (isTaigaSpruce ? 2 : (isCactus ? 2 : 1));
            int maxCheckY = math.min(chunkSizeY - 1, surfaceY + t.trunkHeight + canopyH + extraTop);
            for (int yy = surfaceY + 1; yy <= maxCheckY && !skipTree; yy++)
            {
                for (int dx = -canopyR; dx <= canopyR && !skipTree; dx++)
                {
                    for (int dz = -canopyR; dz <= canopyR; dz++)
                    {
                        int cx = ix + dx;
                        int cz = iz + dz;
                        if (cx < 0 || cx >= voxelSizeX || cz < 0 || cz >= voxelSizeZ)
                            continue;

                        int cidx = cx + yy * voxelSizeX + cz * voxelPlaneSize;
                        if (blockTypes[cidx] == BlockType.Water)
                        {
                            skipTree = true;
                            break;
                        }
                    }
                }
            }

            if (skipTree)
                continue;

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

            for (int dy = 1; dy <= t.trunkHeight; dy++)
            {
                int ty = surfaceY + dy;
                if (ty >= chunkSizeY)
                    break;

                int tidx = ix + ty * voxelSizeX + iz * voxelPlaneSize;
                if (!IsWoodBlock(blockTypes[tidx]) &&
                    (blockTypes[tidx] == BlockType.Air || blockTypes[tidx] == BlockType.Leaves))
                {
                    blockTypes[tidx] = trunkType;
                    solids[tidx] = blockMappings[(int)trunkType].isSolid;
                }
            }
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
               blockType == BlockType.Cactus;
    }

    private static bool TryPlaceWoodBlock(
        int lx,
        int ly,
        int lz,
        NativeArray<BlockType> blockTypes,
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
        BlockType existing = blockTypes[idx];
        if (existing == trunkType)
            return true;
        if (IsWoodBlock(existing))
            return false;
        if (!(existing == BlockType.Air || existing == BlockType.Leaves))
            return false;

        blockTypes[idx] = trunkType;
        solids[idx] = blockMappings[mappingIndex].isSolid;
        return true;
    }

    private static void PlaceCactusStructure(
        int ix,
        int iz,
        int surfaceY,
        int trunkHeight,
        int armReach,
        int treeHash,
        NativeArray<BlockType> blockTypes,
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
        NativeArray<BlockType> blockTypes,
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
        BlockType existing = blockTypes[idx];
        if (!(existing == BlockType.Air || existing == BlockType.Leaves))
            return false;

        blockTypes[idx] = cactusType;
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
        NativeArray<BlockType> blockTypes,
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
        NativeArray<BlockType> blockTypes,
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
        NativeArray<BlockType> blockTypes,
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
        NativeArray<BlockType> blockTypes,
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
        NativeArray<BlockType> blockTypes,
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
        NativeArray<BlockType> blockTypes,
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
        NativeArray<BlockType> blockTypes,
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
        NativeArray<BlockType> blockTypes,
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
        NativeArray<BlockType> blockTypes,
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
        NativeArray<BlockType> blockTypes,
        NativeArray<bool> solids,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize,
        bool skipCenter)
    {
        if (skipCenter)
            return;

        if (lx < 0 || lx >= voxelSizeX || lz < 0 || lz >= voxelSizeZ)
            return;
        int maxY = blockTypes.Length / (voxelSizeX * voxelSizeZ);
        if (ly < 0 || ly >= maxY)
            return;

        int lidx = lx + ly * voxelSizeX + lz * voxelPlaneSize;
        BlockType existing = blockTypes[lidx];
        if (IsWoodBlock(existing))
            return;
        if (!(existing == BlockType.Air || existing == BlockType.Leaves))
            return;

        blockTypes[lidx] = BlockType.Leaves;
        solids[lidx] = blockMappings[(int)BlockType.Leaves].isSolid;
    }
}



