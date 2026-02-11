
// LightingCalculator.cs
using Unity.Collections;
using Unity.Mathematics;
using Unity.Burst;

public static class LightingCalculator
{
    [BurstCompile]
    public static void CalculateLighting(
        NativeArray<BlockType> blockTypes,
        NativeArray<bool> solids,
        NativeArray<byte> light, // OUT: final combined light (0..15)
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int totalVoxels,
        int planeSize,
        int SizeY = 256
    )
    {
        // --- Preparação: tabelas de opacidade e emissão ---
        int mapCount = blockMappings.Length;
        NativeArray<byte> opacity = new NativeArray<byte>(mapCount, Allocator.Temp);
        NativeArray<byte> emission = new NativeArray<byte>(mapCount, Allocator.Temp);

        for (int i = 0; i < mapCount; i++)
        {
            opacity[i] = blockMappings[i].lightOpacity;
            emission[i] = blockMappings[i].lightEmission;
        }

        // Arrays temporários
        NativeArray<byte> skylight = new NativeArray<byte>(totalVoxels, Allocator.Temp);
        NativeArray<byte> blocklight = new NativeArray<byte>(totalVoxels, Allocator.Temp);

        // --- 1) Skylight vertical fill (top-down) ---
        NativeArray<byte> skyExposed = new NativeArray<byte>(totalVoxels, Allocator.Temp); // 0/1
        for (int x = 0; x < voxelSizeX; x++)
        {
            for (int z = 0; z < voxelSizeZ; z++)
            {
                int idxBase = x + z * planeSize;
                bool blocked = false;
                for (int y = SizeY - 1; y >= 0; y--)
                {
                    int idx = idxBase + y * voxelSizeX;
                    BlockType bt = blockTypes[idx];
                    int bti = (int)bt;
                    byte blockOp = (bti >= 0 && bti < mapCount) ? opacity[bti] : (byte)15;

                    if (!blocked && blockOp < 15)
                    {
                        skylight[idx] = 15;
                        skyExposed[idx] = 1;
                    }
                    else
                    {
                        skylight[idx] = 0;
                        skyExposed[idx] = 0;
                        if (blockOp >= 15) blocked = true;
                    }
                }
            }
        }

        // BFS queue inicializada com todos voxels skylit
        NativeList<int> queue = new NativeList<int>(Allocator.Temp);
        for (int i = 0; i < totalVoxels; i++)
        {
            if (skylight[i] > 0 && skyExposed[i] == 1)
                queue.Add(i);
            // inicializa light final com skylight temporariamente
            light[i] = skylight[i];
        }

        // --- 2) Propagação do Skylight (6-neigh) com opacidade variável ---
        int read = 0;
        while (read < queue.Length)
        {
            int cur = queue[read++];
            byte curLight = skylight[cur];
            if (curLight <= 1) continue;

            int plane = voxelSizeX * SizeY;
            int y = (cur / voxelSizeX) % SizeY;
            int x = cur % voxelSizeX;
            int z = cur / plane;

            void TryPushSkylight(int tx, int ty, int tz)
            {
                if (tx < 0 || tx >= voxelSizeX || ty < 0 || ty >= SizeY || tz < 0 || tz >= voxelSizeZ) return;
                int nIdx = tx + ty * voxelSizeX + tz * plane;
                byte existing = skylight[nIdx];
                BlockType neighborType = blockTypes[nIdx];
                int nti = (int)neighborType;
                byte op = (nti >= 0 && nti < mapCount) ? opacity[nti] : (byte)15;

                byte cost = (op < 1) ? (byte)1 : op;
                int candInt = System.Math.Max(0, (int)curLight - (int)cost);
                byte candidate = (byte)candInt;

                if (candidate > existing)
                {
                    skylight[nIdx] = candidate;
                    if (candidate > 1)
                        queue.Add(nIdx);
                    // update final combined light if greater
                    if (candidate > light[nIdx]) light[nIdx] = candidate;
                }
            }

            TryPushSkylight(x - 1, y, z);
            TryPushSkylight(x + 1, y, z);
            TryPushSkylight(x, y - 1, z);
            TryPushSkylight(x, y + 1, z);
            TryPushSkylight(x, y, z - 1);
            TryPushSkylight(x, y, z + 1);
        }

        // --- 3) Block light: inicializar emissores ---
        queue.Clear();
        for (int i = 0; i < totalVoxels; i++)
        {
            BlockType bt = blockTypes[i];
            int bti = (int)bt;
            if (bti >= 0 && bti < mapCount)
            {
                byte emit = emission[bti];
                if (emit > 0)
                {
                    blocklight[i] = emit;
                    queue.Add(i);
                    // combine immediately com skylight
                    if (emit > light[i]) light[i] = emit;
                }
            }
        }

        // --- 4) Propagação do Block Light (BFS) ---
        read = 0;
        while (read < queue.Length)
        {
            int cur = queue[read++];
            byte curLight = blocklight[cur];
            if (curLight <= 1) continue;

            int plane = voxelSizeX * SizeY;
            int y = (cur / voxelSizeX) % SizeY;
            int x = cur % voxelSizeX;
            int z = cur / plane;

            void TryPushBlockLight(int tx, int ty, int tz)
            {
                if (tx < 0 || tx >= voxelSizeX || ty < 0 || ty >= SizeY || tz < 0 || tz >= voxelSizeZ) return;
                int nIdx = tx + ty * voxelSizeX + tz * plane;
                byte existing = blocklight[nIdx];
                BlockType neighborType = blockTypes[nIdx];
                int nti = (int)neighborType;
                byte op = (nti >= 0 && nti < mapCount) ? opacity[nti] : (byte)15;

                byte cost = (op < 1) ? (byte)1 : op;
                int candInt = System.Math.Max(0, (int)curLight - (int)cost);
                byte candidate = (byte)candInt;

                if (candidate > existing)
                {
                    blocklight[nIdx] = candidate;
                    if (candidate > 1)
                        queue.Add(nIdx);

                    // Combine with final light (max skylight, blocklight)
                    if (candidate > light[nIdx]) light[nIdx] = candidate;
                }
            }

            TryPushBlockLight(x - 1, y, z);
            TryPushBlockLight(x + 1, y, z);
            TryPushBlockLight(x, y - 1, z);
            TryPushBlockLight(x, y + 1, z);
            TryPushBlockLight(x, y, z - 1);
            TryPushBlockLight(x, y, z + 1);
        }

        // --- Clean up ---
        queue.Dispose();
        skylight.Dispose();
        blocklight.Dispose();
        skyExposed.Dispose();
        opacity.Dispose();
        emission.Dispose();
    }
}
