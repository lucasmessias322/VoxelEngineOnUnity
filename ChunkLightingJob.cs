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
        [ReadOnly] public NativeArray<byte> blockTypes;
        [ReadOnly] public NativeArray<BlockTextureMapping> blockMappings;
        [ReadOnly] public NativeArray<ushort> blockLightData;
        [ReadOnly] public NativeArray<ushort> blockEmissionData;

        public NativeArray<ushort> light;
        public bool enableHorizontalSkylight;
        public int horizontalSkylightStepLoss;

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
            NativeArray<ushort> blockMap = new NativeArray<ushort>(totalVoxels, Allocator.Temp);
            NativeQueue<int> blockQueue = new NativeQueue<int>(Allocator.Temp);

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

            if (enableHorizontalSkylight)
            {
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

                            if (x + 1 < voxelSizeX && CanImproveHorizontalNeighbor(idx + 1, currentLight, skyMap)) canPropagate = true;
                            else if (x - 1 >= 0 && CanImproveHorizontalNeighbor(idx - 1, currentLight, skyMap)) canPropagate = true;
                            else if (y - 1 >= 0 && CanImproveDownwardNeighbor(idx - voxelSizeX, currentLight, skyMap)) canPropagate = true;
                            else if (z + 1 < voxelSizeZ && CanImproveHorizontalNeighbor(idx + voxelPlaneSize, currentLight, skyMap)) canPropagate = true;
                            else if (z - 1 >= 0 && CanImproveHorizontalNeighbor(idx - voxelPlaneSize, currentLight, skyMap)) canPropagate = true;

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

                    if (x + 1 < voxelSizeX) TryPropagateHorizontal(currentIndex + 1, currentLight, skyMap, lightQueue);
                    if (x - 1 >= 0) TryPropagateHorizontal(currentIndex - 1, currentLight, skyMap, lightQueue);
                    if (y - 1 >= 0) TryPropagateDownward(currentIndex - voxelSizeX, currentLight, skyMap, lightQueue);
                    if (z + 1 < voxelSizeZ) TryPropagateHorizontal(currentIndex + voxelPlaneSize, currentLight, skyMap, lightQueue);
                    if (z - 1 >= 0) TryPropagateHorizontal(currentIndex - voxelPlaneSize, currentLight, skyMap, lightQueue);
                }
            }

            for (int i = 0; i < totalVoxels; i++)
            {
                ushort blockL = 0;
                if (blockLightData.IsCreated && i < blockLightData.Length)
                    blockL = blockLightData[i];

                ushort blockEmission = 0;
                if (blockEmissionData.IsCreated && i < blockEmissionData.Length)
                    blockEmission = blockEmissionData[i];
                else if (i < blockTypes.Length)
                {
                    BlockTextureMapping mapping = blockMappings[(int)blockTypes[i]];
                    blockEmission = LightUtils.PackEmission(
                        mapping.lightEmission,
                        mapping.lightColor.r,
                        mapping.lightColor.g,
                        mapping.lightColor.b);
                }

                ushort initialBlockLight = LightUtils.MaxBlockLight(blockL, blockEmission);
                blockMap[i] = initialBlockLight;
                if (LightUtils.GetBlockLight(initialBlockLight) <= 1)
                    continue;

                bool canPropagate = false;
                int z = i / voxelPlaneSize;
                int rem = i % voxelPlaneSize;
                int y = rem / voxelSizeX;
                int x = rem % voxelSizeX;

                if (x + 1 < voxelSizeX && CanImproveBlockNeighbor(i + 1, initialBlockLight, blockMap)) canPropagate = true;
                else if (x - 1 >= 0 && CanImproveBlockNeighbor(i - 1, initialBlockLight, blockMap)) canPropagate = true;
                else if (y + 1 < SizeY && CanImproveBlockNeighbor(i + voxelSizeX, initialBlockLight, blockMap)) canPropagate = true;
                else if (y - 1 >= 0 && CanImproveBlockNeighbor(i - voxelSizeX, initialBlockLight, blockMap)) canPropagate = true;
                else if (z + 1 < voxelSizeZ && CanImproveBlockNeighbor(i + voxelPlaneSize, initialBlockLight, blockMap)) canPropagate = true;
                else if (z - 1 >= 0 && CanImproveBlockNeighbor(i - voxelPlaneSize, initialBlockLight, blockMap)) canPropagate = true;

                if (canPropagate)
                    blockQueue.Enqueue(i);
            }

            while (blockQueue.TryDequeue(out int currentBlockIndex))
            {
                ushort currentLight = blockMap[currentBlockIndex];
                if (LightUtils.GetBlockLight(currentLight) <= 1)
                    continue;

                int z = currentBlockIndex / voxelPlaneSize;
                int rem = currentBlockIndex % voxelPlaneSize;
                int y = rem / voxelSizeX;
                int x = rem % voxelSizeX;

                if (x + 1 < voxelSizeX) TryPropagateBlock(currentBlockIndex + 1, currentLight, blockMap, blockQueue);
                if (x - 1 >= 0) TryPropagateBlock(currentBlockIndex - 1, currentLight, blockMap, blockQueue);
                if (y + 1 < SizeY) TryPropagateBlock(currentBlockIndex + voxelSizeX, currentLight, blockMap, blockQueue);
                if (y - 1 >= 0) TryPropagateBlock(currentBlockIndex - voxelSizeX, currentLight, blockMap, blockQueue);
                if (z + 1 < voxelSizeZ) TryPropagateBlock(currentBlockIndex + voxelPlaneSize, currentLight, blockMap, blockQueue);
                if (z - 1 >= 0) TryPropagateBlock(currentBlockIndex - voxelPlaneSize, currentLight, blockMap, blockQueue);
            }

            // Step 3: pack sky + block light.
            for (int i = 0; i < totalVoxels; i++)
            {
                ushort block = blockMap[i];
                light[i] = LightUtils.PackLightRgb(
                    skyMap[i],
                    LightUtils.GetBlockLightR(block),
                    LightUtils.GetBlockLightG(block),
                    LightUtils.GetBlockLightB(block));
            }

            skyMap.Dispose();
            lightQueue.Dispose();
            blockMap.Dispose();
            blockQueue.Dispose();
        }

        private bool CanImproveHorizontalNeighbor(int neighborIndex, byte currentLight, NativeArray<byte> skyMap)
        {
            byte opacity = GetEffectiveOpacity(blockMappings[(int)blockTypes[neighborIndex]]);
            int lightLoss = math.max(1, horizontalSkylightStepLoss) + opacity;
            byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);
            return propagatedLight > skyMap[neighborIndex];
        }

        private bool CanImproveDownwardNeighbor(int neighborIndex, byte currentLight, NativeArray<byte> skyMap)
        {
            byte opacity = GetEffectiveOpacity(blockMappings[(int)blockTypes[neighborIndex]]);
            int lightLoss = 1 + opacity;
            byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);
            return propagatedLight > skyMap[neighborIndex];
        }

        private void TryPropagateHorizontal(int neighborIndex, byte currentLight, NativeArray<byte> skyMap, NativeQueue<int> lightQueue)
        {
            byte opacity = GetEffectiveOpacity(blockMappings[(int)blockTypes[neighborIndex]]);
            int lightLoss = math.max(1, horizontalSkylightStepLoss) + opacity;
            byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);

            if (propagatedLight > skyMap[neighborIndex])
            {
                skyMap[neighborIndex] = propagatedLight;
                lightQueue.Enqueue(neighborIndex);
            }
        }

        private void TryPropagateDownward(int neighborIndex, byte currentLight, NativeArray<byte> skyMap, NativeQueue<int> lightQueue)
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

        private bool CanImproveBlockNeighbor(int neighborIndex, ushort currentLight, NativeArray<ushort> blockMap)
        {
            byte opacity = GetEffectiveOpacity(blockMappings[(int)blockTypes[neighborIndex]]);
            int lightLoss = 1 + opacity;
            ushort propagatedLight = LightUtils.AttenuateBlockLight(currentLight, lightLoss);
            return LightUtils.IsBlockLightGreater(propagatedLight, blockMap[neighborIndex]);
        }

        private void TryPropagateBlock(int neighborIndex, ushort currentLight, NativeArray<ushort> blockMap, NativeQueue<int> blockQueue)
        {
            byte opacity = GetEffectiveOpacity(blockMappings[(int)blockTypes[neighborIndex]]);
            int lightLoss = 1 + opacity;
            ushort propagatedLight = LightUtils.AttenuateBlockLight(currentLight, lightLoss);

            if (LightUtils.IsBlockLightGreater(propagatedLight, blockMap[neighborIndex]))
            {
                blockMap[neighborIndex] = LightUtils.MaxBlockLight(blockMap[neighborIndex], propagatedLight);
                blockQueue.Enqueue(neighborIndex);
            }
        }

    }

    [BurstCompile]
    public struct CroppedChunkLightingJob : IJob
    {
        [ReadOnly] public NativeArray<byte> opacity;
        [ReadOnly] public NativeArray<ushort> blockLightData;
        [ReadOnly] public NativeArray<ushort> blockEmissionData;

        public NativeArray<ushort> light;
        public bool enableHorizontalSkylight;
        public int horizontalSkylightStepLoss;

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
            NativeArray<ushort> blockMap = new NativeArray<ushort>(inputTotalVoxels, Allocator.Temp);
            NativeQueue<int> blockQueue = new NativeQueue<int>(Allocator.Temp);

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

            if (enableHorizontalSkylight)
            {
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

                            if (x + 1 < inputVoxelSizeX && CanImproveHorizontalNeighbor(idx + 1, currentLight, skyMap)) canPropagate = true;
                            else if (x - 1 >= 0 && CanImproveHorizontalNeighbor(idx - 1, currentLight, skyMap)) canPropagate = true;
                            else if (y - 1 >= 0 && CanImproveDownwardNeighbor(idx - inputVoxelSizeX, currentLight, skyMap)) canPropagate = true;
                            else if (z + 1 < inputVoxelSizeZ && CanImproveHorizontalNeighbor(idx + inputVoxelPlaneSize, currentLight, skyMap)) canPropagate = true;
                            else if (z - 1 >= 0 && CanImproveHorizontalNeighbor(idx - inputVoxelPlaneSize, currentLight, skyMap)) canPropagate = true;

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

                    if (x + 1 < inputVoxelSizeX) TryPropagateHorizontal(currentIndex + 1, currentLight, skyMap, lightQueue);
                    if (x - 1 >= 0) TryPropagateHorizontal(currentIndex - 1, currentLight, skyMap, lightQueue);
                    if (y - 1 >= 0) TryPropagateDownward(currentIndex - inputVoxelSizeX, currentLight, skyMap, lightQueue);
                    if (z + 1 < inputVoxelSizeZ) TryPropagateHorizontal(currentIndex + inputVoxelPlaneSize, currentLight, skyMap, lightQueue);
                    if (z - 1 >= 0) TryPropagateHorizontal(currentIndex - inputVoxelPlaneSize, currentLight, skyMap, lightQueue);
                }
            }

            for (int i = 0; i < inputTotalVoxels; i++)
            {
                ushort blockL = 0;
                if (blockLightData.IsCreated && i < blockLightData.Length)
                    blockL = blockLightData[i];

                ushort blockEmission = 0;
                if (blockEmissionData.IsCreated && i < blockEmissionData.Length)
                    blockEmission = blockEmissionData[i];

                ushort initialBlockLight = LightUtils.MaxBlockLight(blockL, blockEmission);
                blockMap[i] = initialBlockLight;
                if (LightUtils.GetBlockLight(initialBlockLight) <= 1)
                    continue;

                int z = i / inputVoxelPlaneSize;
                int rem = i % inputVoxelPlaneSize;
                int y = rem / inputVoxelSizeX;
                int x = rem % inputVoxelSizeX;
                bool canPropagate = false;

                if (x + 1 < inputVoxelSizeX && CanImproveBlockNeighbor(i + 1, initialBlockLight, blockMap)) canPropagate = true;
                else if (x - 1 >= 0 && CanImproveBlockNeighbor(i - 1, initialBlockLight, blockMap)) canPropagate = true;
                else if (y + 1 < SizeY && CanImproveBlockNeighbor(i + inputVoxelSizeX, initialBlockLight, blockMap)) canPropagate = true;
                else if (y - 1 >= 0 && CanImproveBlockNeighbor(i - inputVoxelSizeX, initialBlockLight, blockMap)) canPropagate = true;
                else if (z + 1 < inputVoxelSizeZ && CanImproveBlockNeighbor(i + inputVoxelPlaneSize, initialBlockLight, blockMap)) canPropagate = true;
                else if (z - 1 >= 0 && CanImproveBlockNeighbor(i - inputVoxelPlaneSize, initialBlockLight, blockMap)) canPropagate = true;

                if (canPropagate)
                    blockQueue.Enqueue(i);
            }

            while (blockQueue.TryDequeue(out int currentBlockIndex))
            {
                ushort currentLight = blockMap[currentBlockIndex];
                if (LightUtils.GetBlockLight(currentLight) <= 1)
                    continue;

                int z = currentBlockIndex / inputVoxelPlaneSize;
                int rem = currentBlockIndex % inputVoxelPlaneSize;
                int y = rem / inputVoxelSizeX;
                int x = rem % inputVoxelSizeX;

                if (x + 1 < inputVoxelSizeX) TryPropagateBlock(currentBlockIndex + 1, currentLight, blockMap, blockQueue);
                if (x - 1 >= 0) TryPropagateBlock(currentBlockIndex - 1, currentLight, blockMap, blockQueue);
                if (y + 1 < SizeY) TryPropagateBlock(currentBlockIndex + inputVoxelSizeX, currentLight, blockMap, blockQueue);
                if (y - 1 >= 0) TryPropagateBlock(currentBlockIndex - inputVoxelSizeX, currentLight, blockMap, blockQueue);
                if (z + 1 < inputVoxelSizeZ) TryPropagateBlock(currentBlockIndex + inputVoxelPlaneSize, currentLight, blockMap, blockQueue);
                if (z - 1 >= 0) TryPropagateBlock(currentBlockIndex - inputVoxelPlaneSize, currentLight, blockMap, blockQueue);
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
                        ushort block = blockMap[inputIdx];
                        light[outputIdx] = LightUtils.PackLightRgb(
                            skyMap[inputIdx],
                            LightUtils.GetBlockLightR(block),
                            LightUtils.GetBlockLightG(block),
                            LightUtils.GetBlockLightB(block));
                    }
                }
            }

            skyMap.Dispose();
            lightQueue.Dispose();
            blockMap.Dispose();
            blockQueue.Dispose();
        }

        private bool CanImproveHorizontalNeighbor(int neighborIndex, byte currentLight, NativeArray<byte> skyMap)
        {
            int lightLoss = math.max(1, horizontalSkylightStepLoss) + opacity[neighborIndex];
            byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);
            return propagatedLight > skyMap[neighborIndex];
        }

        private bool CanImproveDownwardNeighbor(int neighborIndex, byte currentLight, NativeArray<byte> skyMap)
        {
            int lightLoss = 1 + opacity[neighborIndex];
            byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);
            return propagatedLight > skyMap[neighborIndex];
        }

        private void TryPropagateHorizontal(int neighborIndex, byte currentLight, NativeArray<byte> skyMap, NativeQueue<int> lightQueue)
        {
            int lightLoss = math.max(1, horizontalSkylightStepLoss) + opacity[neighborIndex];
            byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);

            if (propagatedLight > skyMap[neighborIndex])
            {
                skyMap[neighborIndex] = propagatedLight;
                lightQueue.Enqueue(neighborIndex);
            }
        }

        private void TryPropagateDownward(int neighborIndex, byte currentLight, NativeArray<byte> skyMap, NativeQueue<int> lightQueue)
        {
            int lightLoss = 1 + opacity[neighborIndex];
            byte propagatedLight = (byte)math.max(0, currentLight - lightLoss);

            if (propagatedLight > skyMap[neighborIndex])
            {
                skyMap[neighborIndex] = propagatedLight;
                lightQueue.Enqueue(neighborIndex);
            }
        }

        private bool CanImproveBlockNeighbor(int neighborIndex, ushort currentLight, NativeArray<ushort> blockMap)
        {
            int lightLoss = 1 + opacity[neighborIndex];
            ushort propagatedLight = LightUtils.AttenuateBlockLight(currentLight, lightLoss);
            return LightUtils.IsBlockLightGreater(propagatedLight, blockMap[neighborIndex]);
        }

        private void TryPropagateBlock(int neighborIndex, ushort currentLight, NativeArray<ushort> blockMap, NativeQueue<int> blockQueue)
        {
            int lightLoss = 1 + opacity[neighborIndex];
            ushort propagatedLight = LightUtils.AttenuateBlockLight(currentLight, lightLoss);

            if (LightUtils.IsBlockLightGreater(propagatedLight, blockMap[neighborIndex]))
            {
                blockMap[neighborIndex] = LightUtils.MaxBlockLight(blockMap[neighborIndex], propagatedLight);
                blockQueue.Enqueue(neighborIndex);
            }
        }
    }

    public static byte GetEffectiveOpacity(BlockTextureMapping mapping)
    {
        return BlockShapeUtility.GetEffectiveLightOpacity(mapping);
    }
}
