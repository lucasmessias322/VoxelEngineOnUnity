using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

public static class ChunkLighting
{
    [BurstCompile]
    public struct ChunkLightingJob : IJob
    {
        [ReadOnly] public NativeArray<BlockType> blockTypes;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [ReadOnly] public NativeArray<byte> blockLightData;

        public NativeArray<byte> light;

        public int voxelSizeX;
        public int voxelSizeZ;
        public int totalVoxels;
        public int voxelPlaneSize;
        public int SizeX;
        public int SizeY;
        public int SizeZ;

        public void Execute()
        {
            NativeArray<byte> skyMap = new NativeArray<byte>(totalVoxels, Allocator.Temp);
            NativeQueue<int> lightQueue = new NativeQueue<int>(Allocator.Temp);

            // Step 1: vertical sun rays.
            for (int lx = 0; lx < voxelSizeX; lx++)
            {
                for (int lz = 0; lz < voxelSizeZ; lz++)
                {
                    byte currentSky = 15;
                    for (int y = SizeY - 1; y >= 0; y--)
                    {
                        if (currentSky == 0)
                        {
                            // Remaining voxels below stay 0 (skyMap is zero-initialized).
                            break;
                        }

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
                    }
                }
            }

            // Step 2a: enqueue only real propagation sources.
            for (int z = 0; z < voxelSizeZ; z++)
            {
                for (int y = 0; y < SizeY; y++)
                {
                    for (int x = 0; x < voxelSizeX; x++)
                    {
                        int idx = x + y * voxelSizeX + z * voxelPlaneSize;
                        byte currentLight = skyMap[idx];
                        if (currentLight <= 1) continue;

                        bool canPropagate = false;

                        if (x + 1 < voxelSizeX && CanImproveNeighbor(idx + 1, currentLight, skyMap)) canPropagate = true;
                        else if (x - 1 >= 0 && CanImproveNeighbor(idx - 1, currentLight, skyMap)) canPropagate = true;
                        else if (y - 1 >= 0 && CanImproveNeighbor(idx - voxelSizeX, currentLight, skyMap)) canPropagate = true;
                        else if (z + 1 < voxelSizeZ && CanImproveNeighbor(idx + voxelPlaneSize, currentLight, skyMap)) canPropagate = true;
                        else if (z - 1 >= 0 && CanImproveNeighbor(idx - voxelPlaneSize, currentLight, skyMap)) canPropagate = true;

                        if (canPropagate)
                        {
                            lightQueue.Enqueue(idx);
                        }
                    }
                }
            }

            // Step 2b: flood-fill smoothing (no +Y for skylight).
            while (lightQueue.TryDequeue(out int currentIndex))
            {
                byte currentLight = skyMap[currentIndex];
                if (currentLight <= 1) continue;

                int z = currentIndex / voxelPlaneSize;
                int rem = currentIndex % voxelPlaneSize;
                int y = rem / voxelSizeX;
                int x = rem % voxelSizeX;

                if (x + 1 < voxelSizeX) TryPropagate(currentIndex + 1, currentLight, skyMap, lightQueue);
                if (x - 1 >= 0) TryPropagate(currentIndex - 1, currentLight, skyMap, lightQueue);
                if (y - 1 >= 0) TryPropagate(currentIndex - voxelSizeX, currentLight, skyMap, lightQueue);
                if (z + 1 < voxelSizeZ) TryPropagate(currentIndex + voxelPlaneSize, currentLight, skyMap, lightQueue);
                if (z - 1 >= 0) TryPropagate(currentIndex - voxelPlaneSize, currentLight, skyMap, lightQueue);
            }

            // Step 3: pack sky + block light.
            for (int i = 0; i < totalVoxels; i++)
            {
                byte blockL = 0;
                if (blockLightData.IsCreated && i < blockLightData.Length)
                {
                    blockL = blockLightData[i];
                }

                light[i] = LightUtils.PackLight(skyMap[i], blockL);
            }

            skyMap.Dispose();
            lightQueue.Dispose();
        }

        private bool CanImproveNeighbor(int neighborIndex, byte currentLight, NativeArray<byte> skyMap)
        {
            byte opacity = blockMappings[(int)blockTypes[neighborIndex]].lightOpacity;
            int lightLoss = 1 + opacity;
            byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);
            return propagatedLight > skyMap[neighborIndex];
        }

        private void TryPropagate(int neighborIndex, byte currentLight, NativeArray<byte> skyMap, NativeQueue<int> lightQueue)
        {
            byte opacity = blockMappings[(int)blockTypes[neighborIndex]].lightOpacity;
            int lightLoss = 1 + opacity;
            byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);

            if (propagatedLight > skyMap[neighborIndex])
            {
                skyMap[neighborIndex] = propagatedLight;
                lightQueue.Enqueue(neighborIndex);
            }
        }
    }
}
