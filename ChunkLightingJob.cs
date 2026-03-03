using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

public static class ChunkLighting
{
    [BurstCompile]
    public struct ChunkLightingJob : IJob
    {
        [ReadOnly] public NativeArray<BlockType> blockTypes;
        [ReadOnly] public NativeArray<bool> solids;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [ReadOnly] public NativeArray<byte> blockLightData;

        public NativeArray<byte> light;

        public int voxelSizeX;
        public int voxelSizeZ;
        public int totalVoxels;
        public int voxelPlaneSize; // Assumindo que seja voxelSizeX * SizeY
        public int SizeX;
        public int SizeY;
        public int SizeZ;

        public void Execute()
        {
            // 1. Criamos um mapa temporário para a Skylight e uma fila para a propagação
            NativeArray<byte> skyMap = new NativeArray<byte>(totalVoxels, Allocator.Temp);
            NativeQueue<int> lightQueue = new NativeQueue<int>(Allocator.Temp);

            // 2. PASSO 1: Raio de Sol Vertical (Luz Direta)
            for (int lx = 0; lx < voxelSizeX; lx++)
            {
                for (int lz = 0; lz < voxelSizeZ; lz++)
                {
                    byte currentSky = 15;
                    for (int y = SizeY - 1; y >= 0; y--)
                    {
                        int idx = lx + y * voxelSizeX + lz * voxelPlaneSize;
                        byte opacity = blockMappings[(int)blockTypes[idx]].lightOpacity;

                        if (opacity >= 15)
                        {
                            currentSky = 0;
                        }
                        else if (opacity > 0)
                        {
                            currentSky = (byte)math.max(0, (int)currentSky - (int)opacity);
                        }

                        skyMap[idx] = currentSky;

                        // Enfileira os nós que receberam luz para o passo de suavização
                        if (currentSky > 0)
                        {
                            lightQueue.Enqueue(idx);
                        }
                    }
                }
            }

            // 3. PASSO 2: Suavização / Propagação (Flood Fill BFS)
            // Offsets 1D para os 6 vizinhos: +X, -X, +Y, -Y, +Z, -Z
            NativeArray<int> neighborOffsets = new NativeArray<int>(6, Allocator.Temp);
            neighborOffsets[0] = 1;                  // Direita (+X)
            neighborOffsets[1] = -1;                 // Esquerda (-X)
            neighborOffsets[2] = voxelSizeX;         // Cima (+Y)
            neighborOffsets[3] = -voxelSizeX;        // Baixo (-Y)
            neighborOffsets[4] = voxelPlaneSize;     // Frente (+Z)
            neighborOffsets[5] = -voxelPlaneSize;    // Trás (-Z)

            while (lightQueue.TryDequeue(out int currentIndex))
            {
                byte currentLight = skyMap[currentIndex];

                // Se a luz já é muito fraca, não pode propagar
                if (currentLight <= 1) continue;

                // Decodificando 1D para 3D para evitar propagar para fora do Chunk atual
                int z = currentIndex / voxelPlaneSize;
                int rem = currentIndex % voxelPlaneSize;
                int y = rem / voxelSizeX;
                int x = rem % voxelSizeX;

                for (int i = 0; i < 6; i++)
                {
                    // Checagem de limites do chunk (para não acessar memória indevida)
                    if (x == 0 && i == 1) continue;
                    if (x == voxelSizeX - 1 && i == 0) continue;
                    if (y == 0 && i == 3) continue;
                    if (y == SizeY - 1 && i == 2) continue;
                    if (z == 0 && i == 5) continue;
                    if (z == voxelSizeZ - 1 && i == 4) continue;

                    int neighborIndex = currentIndex + neighborOffsets[i];
                    byte opacity = blockMappings[(int)blockTypes[neighborIndex]].lightOpacity;

                    // A luz perde 1 de intensidade ao se mover para o lado/cima/baixo + a opacidade do bloco
                    int lightLoss = 1 + opacity;
                    byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);

                    // Se a luz que chega no vizinho é maior que a luz que ele já tem, atualizamos e enfileiramos
                    if (propagatedLight > skyMap[neighborIndex])
                    {
                        skyMap[neighborIndex] = propagatedLight;
                        lightQueue.Enqueue(neighborIndex);
                    }
                }
            }

            // 4. PASSO 3: Mesclar Skylight suavizada com BlockLight
            for (int i = 0; i < totalVoxels; i++)
            {
                byte blockL = 0;
                if (blockLightData.IsCreated && i < blockLightData.Length)
                {
                    blockL = blockLightData[i];
                }

                // Empacota (0-15 de sky, 0-15 de block) no mesmo byte!
                light[i] = LightUtils.PackLight(skyMap[i], blockL);
            }

            // 5. Cleanup dos Allocator.Temp
            skyMap.Dispose();
            lightQueue.Dispose();
            neighborOffsets.Dispose();
        }
    }


}