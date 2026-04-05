using Unity.Collections;

public static partial class TreePlacement
{
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
}
