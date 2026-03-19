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
                        byte opacity = GetEffectiveOpacity(blockMappings[(int)blockTypes[idx]]);

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
            byte opacity = GetEffectiveOpacity(blockMappings[(int)blockTypes[neighborIndex]]);
            int lightLoss = 1 + opacity;
            byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);
            return propagatedLight > skyMap[neighborIndex];
        }

        private void TryPropagate(int neighborIndex, byte currentLight, NativeArray<byte> skyMap, NativeQueue<int> lightQueue)
        {
            byte opacity = GetEffectiveOpacity(blockMappings[(int)blockTypes[neighborIndex]]);
            int lightLoss = 1 + opacity;
            byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);

            if (propagatedLight > skyMap[neighborIndex])
            {
                skyMap[neighborIndex] = propagatedLight;
                lightQueue.Enqueue(neighborIndex);
            }
        }

    }

    [BurstCompile]
    public struct CroppedChunkLightingJob : IJob
    {
        [ReadOnly] public NativeArray<byte> opacity;
        [ReadOnly] public NativeArray<byte> blockLightData;

        public NativeArray<byte> light;

        public int inputVoxelSizeX;
        public int inputVoxelSizeZ;
        public int inputTotalVoxels;
        public int inputVoxelPlaneSize;

        public int outputVoxelSizeX;
        public int outputVoxelSizeZ;
        public int outputVoxelPlaneSize;
        public int outputOffsetX;
        public int outputOffsetZ;
        public int SizeY;

        public void Execute()
        {
            NativeArray<byte> skyMap = new NativeArray<byte>(inputTotalVoxels, Allocator.Temp);
            NativeQueue<int> lightQueue = new NativeQueue<int>(Allocator.Temp);

            for (int lx = 0; lx < inputVoxelSizeX; lx++)
            {
                for (int lz = 0; lz < inputVoxelSizeZ; lz++)
                {
                    byte currentSky = 15;
                    for (int y = SizeY - 1; y >= 0; y--)
                    {
                        if (currentSky == 0)
                            break;

                        int idx = lx + y * inputVoxelSizeX + lz * inputVoxelPlaneSize;
                        byte cellOpacity = opacity[idx];

                        if (cellOpacity >= 15)
                        {
                            currentSky = 0;
                        }
                        else if (cellOpacity > 0)
                        {
                            currentSky = (byte)math.max(0, (int)currentSky - (int)cellOpacity);
                        }

                        skyMap[idx] = currentSky;
                    }
                }
            }

            for (int z = 0; z < inputVoxelSizeZ; z++)
            {
                for (int y = 0; y < SizeY; y++)
                {
                    for (int x = 0; x < inputVoxelSizeX; x++)
                    {
                        int idx = x + y * inputVoxelSizeX + z * inputVoxelPlaneSize;
                        byte currentLight = skyMap[idx];
                        if (currentLight <= 1) continue;

                        bool canPropagate = false;

                        if (x + 1 < inputVoxelSizeX && CanImproveNeighbor(idx + 1, currentLight, skyMap)) canPropagate = true;
                        else if (x - 1 >= 0 && CanImproveNeighbor(idx - 1, currentLight, skyMap)) canPropagate = true;
                        else if (y - 1 >= 0 && CanImproveNeighbor(idx - inputVoxelSizeX, currentLight, skyMap)) canPropagate = true;
                        else if (z + 1 < inputVoxelSizeZ && CanImproveNeighbor(idx + inputVoxelPlaneSize, currentLight, skyMap)) canPropagate = true;
                        else if (z - 1 >= 0 && CanImproveNeighbor(idx - inputVoxelPlaneSize, currentLight, skyMap)) canPropagate = true;

                        if (canPropagate)
                            lightQueue.Enqueue(idx);
                    }
                }
            }

            while (lightQueue.TryDequeue(out int currentIndex))
            {
                byte currentLight = skyMap[currentIndex];
                if (currentLight <= 1) continue;

                int z = currentIndex / inputVoxelPlaneSize;
                int rem = currentIndex % inputVoxelPlaneSize;
                int y = rem / inputVoxelSizeX;
                int x = rem % inputVoxelSizeX;

                if (x + 1 < inputVoxelSizeX) TryPropagate(currentIndex + 1, currentLight, skyMap, lightQueue);
                if (x - 1 >= 0) TryPropagate(currentIndex - 1, currentLight, skyMap, lightQueue);
                if (y - 1 >= 0) TryPropagate(currentIndex - inputVoxelSizeX, currentLight, skyMap, lightQueue);
                if (z + 1 < inputVoxelSizeZ) TryPropagate(currentIndex + inputVoxelPlaneSize, currentLight, skyMap, lightQueue);
                if (z - 1 >= 0) TryPropagate(currentIndex - inputVoxelPlaneSize, currentLight, skyMap, lightQueue);
            }

            for (int oz = 0; oz < outputVoxelSizeZ; oz++)
            {
                int inputZ = oz + outputOffsetZ;
                for (int y = 0; y < SizeY; y++)
                {
                    for (int ox = 0; ox < outputVoxelSizeX; ox++)
                    {
                        int inputX = ox + outputOffsetX;
                        int inputIdx = inputX + y * inputVoxelSizeX + inputZ * inputVoxelPlaneSize;
                        int outputIdx = ox + y * outputVoxelSizeX + oz * outputVoxelPlaneSize;

                        byte blockL = 0;
                        if (blockLightData.IsCreated && inputIdx < blockLightData.Length)
                            blockL = blockLightData[inputIdx];

                        light[outputIdx] = LightUtils.PackLight(skyMap[inputIdx], blockL);
                    }
                }
            }

            skyMap.Dispose();
            lightQueue.Dispose();
        }

        private bool CanImproveNeighbor(int neighborIndex, byte currentLight, NativeArray<byte> skyMap)
        {
            int lightLoss = 1 + opacity[neighborIndex];
            byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);
            return propagatedLight > skyMap[neighborIndex];
        }

        private void TryPropagate(int neighborIndex, byte currentLight, NativeArray<byte> skyMap, NativeQueue<int> lightQueue)
        {
            int lightLoss = 1 + opacity[neighborIndex];
            byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);

            if (propagatedLight > skyMap[neighborIndex])
            {
                skyMap[neighborIndex] = propagatedLight;
                lightQueue.Enqueue(neighborIndex);
            }
        }
    }

    public static byte GetEffectiveOpacity(BlockTextureMapping mapping)
    {
        if (mapping.renderShape != BlockRenderShape.Cube && !mapping.isSolid && !mapping.isLiquid)
            return 0;

        return mapping.lightOpacity;
    }
}
