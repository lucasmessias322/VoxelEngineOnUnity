// Novo arquivo: LightingCalculator.cs
using Unity.Collections;
using Unity.Mathematics;
using Unity.Burst;

public static class LightingCalculator
{
    [BurstCompile]
    public static void CalculateSkylight(
        NativeArray<BlockType> blockTypes,
        NativeArray<bool> solids,
        NativeArray<byte> sunlight,
        NativeArray<BlockTextureMapping> blockMappings, // Adicionado: blockMappings é usado para opacity
        int voxelSizeX,
        int voxelSizeZ,
        int totalVoxels,
        int planeSize,
        int SizeY = 256 // Adicionado: SizeY como parâmetro com valor padrão
    )
    {
       



        // Construir tabela de opacidades (usando blockMappings)
        int mapCount = blockMappings.Length;
        NativeArray<byte> opacity = new NativeArray<byte>(mapCount, Allocator.Temp);
        for (int i = 0; i < mapCount; i++)
        {
            opacity[i] = blockMappings[i].lightOpacity;
        }

        // skyExposed: marca voxels que realmente veem o céu
        NativeArray<byte> skyExposed = new NativeArray<byte>(totalVoxels, Allocator.Temp); // 0/1 em vez de bool p/ Burst

        // Vertical fill: top -> bottom; marque skyExposed e inicialize sunlight=15 nas colunas visíveis
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
                        sunlight[idx] = 15;
                        skyExposed[idx] = 1;
                    }
                    else
                    {
                        sunlight[idx] = 0;
                        skyExposed[idx] = 0;
                        if (blockOp >= 15) blocked = true;
                    }
                }
            }
        }

        // BFS propagation (6-neighbors) com opacidade variável e custo mínimo 1
        NativeList<int> queue = new NativeList<int>(Allocator.Temp);
        for (int i = 0; i < totalVoxels; i++)
        {
            if (sunlight[i] > 0 && skyExposed[i] == 1)
                queue.Add(i);
        }

        int read = 0;
        while (read < queue.Length)
        {
            int cur = queue[read++];
            byte curLight = sunlight[cur];
            if (curLight <= 1) continue;

            int plane = voxelSizeX * SizeY;
            int y = (cur / voxelSizeX) % SizeY;
            int x = cur % voxelSizeX;
            int z = cur / plane;

            // helper local
            void TryPush(int tx, int ty, int tz)
            {
                if (tx < 0 || tx >= voxelSizeX || ty < 0 || ty >= SizeY || tz < 0 || tz >= voxelSizeZ) return;
                int nIdx = tx + ty * voxelSizeX + tz * plane;
                byte existing = sunlight[nIdx];
                // compute opacidade do bloco destino
                BlockType neighborType = blockTypes[nIdx];
                int nti = (int)neighborType;
                byte op = (nti >= 0 && nti < mapCount) ? opacity[nti] : (byte)15;

                // custo mínimo 1 (mesmo no ar) — evitar math.max com byte para não gerar ambiguidade
                byte cost = (op < 1) ? (byte)1 : op;

                // fazer cálculo em int para evitar ambiguidade e underflow
                int candInt = System.Math.Max(0, (int)curLight - (int)cost);
                byte candidate = (byte)candInt;

                if (candidate > existing)
                {
                    sunlight[nIdx] = candidate;
                    if (candidate > 1)
                        queue.Add(nIdx);
                }
            }

            TryPush(x - 1, y, z);
            TryPush(x + 1, y, z);
            TryPush(x, y - 1, z);
            TryPush(x, y + 1, z);
            TryPush(x, y, z - 1);
            TryPush(x, y, z + 1);
        }

        queue.Dispose();
        skyExposed.Dispose();
        opacity.Dispose();
    }
}