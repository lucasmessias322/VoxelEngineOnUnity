using Unity.Collections;
using Unity.Mathematics;
using Unity.Burst;

public static class LightingCalculator
{
    [BurstCompile]
    public static void CalculateLighting(
        NativeArray<BlockType> blockTypes,
        NativeArray<bool> solids,
        NativeArray<byte> light, // OUT: Luz final combinada
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int totalVoxels,
        int planeSize,
        int SizeY = 256
    )
    {
        
        // 1. Preparação de tabelas de consulta (Lookup Tables)
        int mapCount = blockMappings.Length;
        NativeArray<byte> opacity = new NativeArray<byte>(mapCount, Allocator.Temp);
        NativeArray<byte> emission = new NativeArray<byte>(mapCount, Allocator.Temp);

        for (int i = 0; i < mapCount; i++)
        {
            opacity[i] = blockMappings[i].lightOpacity;
            emission[i] = blockMappings[i].lightEmission;
        }

        // Fila estática para BFS - Evita realocação de NativeList (Gargalo do Burst)
        NativeArray<int> queue = new NativeArray<int>(totalVoxels, Allocator.Temp);
        int head = 0;
        int tail = 0;

        // Arrays de trabalho
        NativeArray<byte> skylight = new NativeArray<byte>(totalVoxels, Allocator.Temp);
        NativeArray<byte> blocklight = new NativeArray<byte>(totalVoxels, Allocator.Temp);

        // --- 1) SKYLIGHT VERTICAL (TOP-DOWN) ---
        for (int x = 0; x < voxelSizeX; x++)
        {
            for (int z = 0; z < voxelSizeZ; z++)
            {
                int idxBase = x + z * planeSize;
                bool blocked = false;
                for (int y = SizeY - 1; y >= 0; y--)
                {
                    int idx = idxBase + y * voxelSizeX;
                    int bti = (int)blockTypes[idx];
                    byte blockOp = (bti >= 0 && bti < mapCount) ? opacity[bti] : (byte)15;

                    if (!blocked && blockOp < 15)
                    {
                        skylight[idx] = 15;
                        // Adiciona à fila apenas os blocos que estão expostos ao céu
                        queue[tail++] = idx;
                    }
                    else
                    {
                        if (blockOp >= 15) blocked = true;
                    }
                }
            }
        }

        // --- 2) PROPAGAÇÃO SKYLIGHT (BFS) ---
        while (head < tail)
        {
            int cur = queue[head++];
            byte curL = skylight[cur];
            if (curL <= 1) continue;

            int y = (cur / voxelSizeX) % SizeY;
            int x = cur % voxelSizeX;
            int z = cur / planeSize;

            // Propaga para os 6 vizinhos
            if (x > 0) TrySpreadSky(cur - 1, curL, skylight, blockTypes, opacity, mapCount, queue, ref tail);
            if (x < voxelSizeX - 1) TrySpreadSky(cur + 1, curL, skylight, blockTypes, opacity, mapCount, queue, ref tail);
            if (y > 0) TrySpreadSky(cur - voxelSizeX, curL, skylight, blockTypes, opacity, mapCount, queue, ref tail);
            if (y < SizeY - 1) TrySpreadSky(cur + voxelSizeX, curL, skylight, blockTypes, opacity, mapCount, queue, ref tail);
            if (z > 0) TrySpreadSky(cur - planeSize, curL, skylight, blockTypes, opacity, mapCount, queue, ref tail);
            if (z < voxelSizeZ - 1) TrySpreadSky(cur + planeSize, curL, skylight, blockTypes, opacity, mapCount, queue, ref tail);
        }

        // --- 3) INICIALIZAR EMISSÃO DE BLOCOS ---
        head = 0; tail = 0; // Reseta a fila para o Blocklight
        for (int i = 0; i < totalVoxels; i++)
        {
            int bti = (int)blockTypes[i];
            if (bti >= 0 && bti < mapCount)
            {
                byte emit = emission[bti];
                if (emit > 0)
                {
                    blocklight[i] = emit;
                    queue[tail++] = i;
                }
            }
            // Já aproveita o loop para aplicar o skylight no array final
            light[i] = skylight[i];
        }

        // --- 4) PROPAGAÇÃO BLOCKLIGHT (BFS) ---
        while (head < tail)
        {
            int cur = queue[head++];
            byte curL = blocklight[cur];
            if (curL <= 1) continue;

            int y = (cur / voxelSizeX) % SizeY;
            int x = cur % voxelSizeX;
            int z = cur / planeSize;

            if (x > 0) TrySpreadBlock(cur - 1, curL, blocklight, light, blockTypes, opacity, mapCount, queue, ref tail);
            if (x < voxelSizeX - 1) TrySpreadBlock(cur + 1, curL, blocklight, light, blockTypes, opacity, mapCount, queue, ref tail);
            if (y > 0) TrySpreadBlock(cur - voxelSizeX, curL, blocklight, light, blockTypes, opacity, mapCount, queue, ref tail);
            if (y < SizeY - 1) TrySpreadBlock(cur + voxelSizeX, curL, blocklight, light, blockTypes, opacity, mapCount, queue, ref tail);
            if (z > 0) TrySpreadBlock(cur - planeSize, curL, blocklight, light, blockTypes, opacity, mapCount, queue, ref tail);
            if (z < voxelSizeZ - 1) TrySpreadBlock(cur + planeSize, curL, blocklight, light, blockTypes, opacity, mapCount, queue, ref tail);
        }

        // Clean up
        queue.Dispose();
        skylight.Dispose();
        blocklight.Dispose();
        opacity.Dispose();
        emission.Dispose();
    }

    private static void TrySpreadSky(int nIdx, byte curLight, NativeArray<byte> skylight, NativeArray<BlockType> blockTypes, NativeArray<byte> opacity, int mapCount, NativeArray<int> queue, ref int tail)
    {
        int nti = (int)blockTypes[nIdx];
        byte op = (nti >= 0 && nti < mapCount) ? opacity[nti] : (byte)15;
        if (op >= 15) return;

         byte cost = (op < 1) ? (byte)1 : op;
        //byte cost = 1;
        byte candidate = (byte)math.max(0, curLight - cost);

        if (candidate > skylight[nIdx])
        {
            skylight[nIdx] = candidate;
            queue[tail++] = nIdx;
        }
    }

    private static void TrySpreadBlock(int nIdx, byte curLight, NativeArray<byte> blocklight, NativeArray<byte> finalLight, NativeArray<BlockType> blockTypes, NativeArray<byte> opacity, int mapCount, NativeArray<int> queue, ref int tail)
    {
        int nti = (int)blockTypes[nIdx];
        byte op = (nti >= 0 && nti < mapCount) ? opacity[nti] : (byte)15;
        if (op >= 15) return;

        byte cost = (op < 1) ? (byte)1 : op;
       // byte cost = 1;
        byte candidate = (byte)math.max(0, curLight - cost);

        if (candidate >= blocklight[nIdx])
        {
            // Só enfileiramos novamente se for estritamente maior
            // ou igual (para permitir propagar em "fronteiras estáveis")
            bool shouldEnqueue = candidate > blocklight[nIdx];

            blocklight[nIdx] = candidate;
            if (candidate > finalLight[nIdx])
                finalLight[nIdx] = candidate;

            if (shouldEnqueue)
                queue[tail++] = nIdx;
        }
    }
}