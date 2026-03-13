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

            int treeHash = (t.worldX * 73856093) ^ (t.worldZ * 19349663) ^ (t.trunkHeight * 83492791);
            BlockType trunkType;
            if (isCactus)
            {
                trunkType = GetCactusBlockType(blockMappings);
            }
            else
            {
                trunkType = isTaigaSpruce
                    ? BlockType.Log
                    : ((treeHash & 3) == 0 ? BlockType.birch_log : BlockType.Log);
            }

            int localX = t.worldX - baseWorldX;
            int localZ = t.worldZ - baseWorldZ;

            if (localX < -t.canopyRadius || localX >= chunkSizeX + t.canopyRadius ||
                localZ < -t.canopyRadius || localZ >= chunkSizeZ + t.canopyRadius)
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
            int canopyH = math.max(1, t.canopyHeight);
            int canopyR = math.max(0, t.canopyRadius);
            if (isTaigaSpruce)
            {
                canopyH = math.max(5, canopyH);
                canopyR = math.max(2, canopyR);
            }

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

            int extraTop = isTaigaSpruce ? 1 : (isCactus ? 2 : 0);
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

                int tidx = ix + ty * voxelSizeX + iz * voxelPlaneSize;
                BlockType existing = blockTypes[tidx];
                if (existing == BlockType.Air || existing == BlockType.Leaves)
                {
                    blockTypes[tidx] = trunkType;
                    solids[tidx] = blockMappings[(int)trunkType].isSolid;
                }
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
                if (blockTypes[tidx] != BlockType.Log && blockTypes[tidx] != BlockType.birch_log)
                {
                    if (blockTypes[tidx] == BlockType.Air || blockTypes[tidx] == BlockType.Leaves)
                    {
                        blockTypes[tidx] = trunkType;
                        solids[tidx] = blockMappings[(int)trunkType].isSolid;
                    }
                }
            }
        }
    }

    private static BlockType GetCactusBlockType(NativeArray<BlockTextureMapping> blockMappings)
    {
        return (int)BlockType.Cactus < blockMappings.Length
            ? BlockType.Cactus
            : BlockType.Log;
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

        // Alterna de baixo para cima: grande -> pequeno -> grande -> pequeno...
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
        // Ponta da spruce.
        TryPlaceLeaf(ix, trunkTopY + 1, iz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);
        TryPlaceLeaf(ix, trunkTopY + 2, iz, blockTypes, solids, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize, false);

        // Folhas extras no topo para nao ficar com apenas um bloco.
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

                // Remove os cantos em raios grandes para evitar canopy quadrado.
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
        if (existing == BlockType.Log || existing == BlockType.birch_log)
            return;
        if (!(existing == BlockType.Air || existing == BlockType.Leaves))
            return;

        blockTypes[lidx] = BlockType.Leaves;
        solids[lidx] = blockMappings[(int)BlockType.Leaves].isSolid;
    }
}
