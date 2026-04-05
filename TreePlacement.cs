using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public static partial class TreePlacement
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
        // Converte instancias abstratas em troncos/folhas dentro do volume padded do chunk.
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

            // Mesmo que o tronco nasca fora da area ativa, ainda vale gerar se a copa entrar no chunk.
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
        }
    }

    private static BlockType GetTrunkBlockType(TreeStyle treeStyle, NativeArray<BlockTextureMapping> blockMappings)
    {
        switch (treeStyle)
        {
            case TreeStyle.Cactus:
                return GetCactusBlockType(blockMappings);
            case TreeStyle.BirchBroadleaf:
                return (int)BlockType.birch_log < blockMappings.Length ? BlockType.birch_log : BlockType.Log;
            case TreeStyle.SavannaAcacia:
                return (int)BlockType.acacia_log < blockMappings.Length ? BlockType.acacia_log : BlockType.Log;
            default:
                return BlockType.Log;
        }
    }

    private static BlockType GetCactusBlockType(NativeArray<BlockTextureMapping> blockMappings)
    {
        return (int)BlockType.Cactus < blockMappings.Length ? BlockType.Cactus : BlockType.Log;
    }

    private static bool IsWoodBlock(BlockType blockType)
    {
        return blockType == BlockType.Log ||
               blockType == BlockType.birch_log ||
               blockType == BlockType.acacia_log ||
               blockType == BlockType.Cactus;
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
        // Troncos podem substituir ar e folhas, mas nunca sobrescrevem outro tronco.
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
}
