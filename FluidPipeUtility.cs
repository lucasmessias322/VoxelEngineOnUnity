using UnityEngine;
using Unity.Collections;

public static class FluidPipeUtility
{
    public readonly struct PipeState
    {
        public readonly BlockType blockType;
        public readonly BlockPlacementAxis placementAxis;

        public PipeState(BlockType blockType, BlockPlacementAxis placementAxis)
        {
            this.blockType = blockType;
            this.placementAxis = placementAxis;
        }
    }

    public static bool IsFluidPipeBlock(BlockType blockType)
    {
        return blockType == BlockType.FluidPipe ||
               blockType == BlockType.FluidPipe_ShapeL ||
               blockType == BlockType.FluidPipe_ShapeT;
    }

    public static BlockType GetInventoryDropBlockType(BlockType blockType)
    {
        return IsFluidPipeBlock(blockType) ? BlockType.FluidPipe : blockType;
    }

    public static PipeState ResolveState(World world, Vector3Int pipePos, BlockPlacementAxis fallbackAxis)
    {
        return ResolveState(ResolveConnectionMask(world, pipePos), fallbackAxis);
    }

    public static PipeState ResolveState(byte connectionMask, BlockPlacementAxis fallbackAxis)
    {
        int connectionCount = TransportTubeUtility.CountConnections(connectionMask);

        if (connectionCount <= 0)
            return new PipeState(BlockType.FluidPipe, ResolveFallbackStraightAxis(fallbackAxis));

        if (connectionCount == 1)
            return new PipeState(BlockType.FluidPipe, ResolveSingleConnectionAxis(connectionMask));

        if (connectionCount == 2)
        {
            if (HasOppositePair(connectionMask, TransportTubeUtility.ConnectWest, TransportTubeUtility.ConnectEast))
                return new PipeState(BlockType.FluidPipe, BlockPlacementAxis.X);

            if (HasOppositePair(connectionMask, TransportTubeUtility.ConnectSouth, TransportTubeUtility.ConnectNorth))
                return new PipeState(BlockType.FluidPipe, BlockPlacementAxis.Z);

            if (HasOppositePair(connectionMask, TransportTubeUtility.ConnectDown, TransportTubeUtility.ConnectUp))
                return new PipeState(BlockType.FluidPipe, BlockPlacementAxis.YNegative);

            return new PipeState(BlockType.FluidPipe_ShapeL, ResolveCornerAxis(connectionMask));
        }

        return new PipeState(BlockType.FluidPipe_ShapeT, ResolveTJunctionAxis(connectionMask));
    }

    public static byte ResolveConnectionMask(World world, Vector3Int blockPos)
    {
        byte mask = 0;
        if (CanConnect(world, blockPos, Vector3Int.left)) mask |= TransportTubeUtility.ConnectWest;
        if (CanConnect(world, blockPos, Vector3Int.right)) mask |= TransportTubeUtility.ConnectEast;
        if (CanConnect(world, blockPos, Vector3Int.back)) mask |= TransportTubeUtility.ConnectSouth;
        if (CanConnect(world, blockPos, Vector3Int.forward)) mask |= TransportTubeUtility.ConnectNorth;
        if (CanConnect(world, blockPos, Vector3Int.down)) mask |= TransportTubeUtility.ConnectDown;
        if (CanConnect(world, blockPos, Vector3Int.up)) mask |= TransportTubeUtility.ConnectUp;
        return mask;
    }

    public static byte ResolveConnectionMask(
        int voxelX,
        int voxelY,
        int voxelZ,
        NativeArray<byte> blockTypes,
        NativeArray<byte> blockPlacementAxes,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        byte mask = 0;
        if (CanConnect(voxelX, voxelY, voxelZ, Vector3Int.left, blockTypes, blockPlacementAxes, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= TransportTubeUtility.ConnectWest;
        if (CanConnect(voxelX, voxelY, voxelZ, Vector3Int.right, blockTypes, blockPlacementAxes, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= TransportTubeUtility.ConnectEast;
        if (CanConnect(voxelX, voxelY, voxelZ, Vector3Int.back, blockTypes, blockPlacementAxes, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= TransportTubeUtility.ConnectSouth;
        if (CanConnect(voxelX, voxelY, voxelZ, Vector3Int.forward, blockTypes, blockPlacementAxes, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= TransportTubeUtility.ConnectNorth;
        if (CanConnect(voxelX, voxelY, voxelZ, Vector3Int.down, blockTypes, blockPlacementAxes, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= TransportTubeUtility.ConnectDown;
        if (CanConnect(voxelX, voxelY, voxelZ, Vector3Int.up, blockTypes, blockPlacementAxes, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= TransportTubeUtility.ConnectUp;
        return mask;
    }

    public static FixedList512Bytes<ShapeBox> BuildVisualBoxes(byte connectionMask, BlockPlacementAxis fallbackAxis)
    {
        return TransportTubeUtility.BuildVisualBoxes(connectionMask, fallbackAxis);
    }

    public static bool TryGetVisualBounds(World world, Vector3Int blockPos, BlockPlacementAxis fallbackAxis, out Bounds bounds)
    {
        bounds = default;
        if (world == null)
            return false;

        FixedList512Bytes<ShapeBox> boxes = BuildVisualBoxes(ResolveConnectionMask(world, blockPos), fallbackAxis);
        if (boxes.Length <= 0)
            return false;

        bounds = boxes[0].ToWorldBounds(blockPos);
        for (int i = 1; i < boxes.Length; i++)
            bounds.Encapsulate(boxes[i].ToWorldBounds(blockPos));

        return true;
    }

    private static bool CanConnect(World world, Vector3Int blockPos, Vector3Int directionToNeighbor)
    {
        if (world == null)
            return false;

        Vector3Int neighborPos = blockPos + directionToNeighbor;
        BlockType neighborType = world.GetBlockAt(neighborPos);
        if (IsFluidPipeBlock(neighborType))
            return true;

        if (neighborType != BlockType.WaterPump)
            return false;

        BlockPlacementAxis pumpAxis = world.GetPlacementAxisAt(neighborPos, neighborType);
        return IsWaterPumpConnectionDirection(pumpAxis, -directionToNeighbor);
    }

    private static bool CanConnect(
        int voxelX,
        int voxelY,
        int voxelZ,
        Vector3Int directionToNeighbor,
        NativeArray<byte> blockTypes,
        NativeArray<byte> blockPlacementAxes,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        int x = voxelX + directionToNeighbor.x;
        int y = voxelY + directionToNeighbor.y;
        int z = voxelZ + directionToNeighbor.z;
        if (x < 0 || x >= voxelSizeX || y < 0 || y >= Chunk.SizeY || z < 0 || z >= voxelSizeZ)
            return false;

        int idx = x + y * voxelSizeX + z * voxelPlaneSize;
        BlockType neighborType = (BlockType)blockTypes[idx];
        if (IsFluidPipeBlock(neighborType))
            return true;

        if (neighborType != BlockType.WaterPump)
            return false;

        BlockPlacementAxis pumpAxis = BlockPlacementAxis.Y;
        if (blockPlacementAxes.IsCreated && (uint)idx < (uint)blockPlacementAxes.Length)
            pumpAxis = (BlockPlacementAxis)blockPlacementAxes[idx];

        return IsWaterPumpConnectionDirection(pumpAxis, -directionToNeighbor);
    }

    private static bool IsWaterPumpConnectionDirection(BlockPlacementAxis pumpAxis, Vector3Int directionFromPump)
    {
        Vector3Int front = GetWaterPumpFrontDirection(pumpAxis);
        return directionFromPump == front || directionFromPump == -front;
    }

    private static Vector3Int GetWaterPumpFrontDirection(BlockPlacementAxis pumpAxis)
    {
        switch (BlockPlacementRotationUtility.SanitizeStoredAxis(pumpAxis))
        {
            case BlockPlacementAxis.X:
                return Vector3Int.right;

            case BlockPlacementAxis.XNegative:
                return Vector3Int.left;

            case BlockPlacementAxis.ZNegative:
                return Vector3Int.back;

            default:
                return Vector3Int.forward;
        }
    }

    private static BlockPlacementAxis ResolveFallbackStraightAxis(BlockPlacementAxis fallbackAxis)
    {
        fallbackAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(fallbackAxis);
        if (fallbackAxis == BlockPlacementAxis.YNegative)
            return BlockPlacementAxis.YNegative;

        return fallbackAxis == BlockPlacementAxis.X || fallbackAxis == BlockPlacementAxis.XNegative
            ? BlockPlacementAxis.X
            : BlockPlacementAxis.Z;
    }

    private static BlockPlacementAxis ResolveSingleConnectionAxis(byte connectionMask)
    {
        if (TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectDown) ||
            TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectUp))
        {
            return BlockPlacementAxis.YNegative;
        }

        return TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectWest) ||
               TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectEast)
            ? BlockPlacementAxis.X
            : BlockPlacementAxis.Z;
    }

    private static BlockPlacementAxis ResolveCornerAxis(byte connectionMask)
    {
        bool left = TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectWest);
        bool right = TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectEast);
        bool back = TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectSouth);
        bool forward = TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectNorth);

        if (left && back)
            return BlockPlacementAxis.Z;

        if (left && forward)
            return BlockPlacementAxis.X;

        if (right && back)
            return BlockPlacementAxis.XNegative;

        if (right && forward)
            return BlockPlacementAxis.ZNegative;

        return BlockPlacementAxis.Z;
    }

    private static BlockPlacementAxis ResolveTJunctionAxis(byte connectionMask)
    {
        bool left = TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectWest);
        bool right = TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectEast);
        bool back = TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectSouth);
        bool forward = TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectNorth);

        if (!forward && left && right && back)
            return BlockPlacementAxis.Z;

        if (!right && left && back && forward)
            return BlockPlacementAxis.X;

        if (!left && right && back && forward)
            return BlockPlacementAxis.XNegative;

        if (!back && left && right && forward)
            return BlockPlacementAxis.ZNegative;

        if (TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectDown) ||
            TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectUp))
        {
            return BlockPlacementAxis.YNegative;
        }

        return BlockPlacementAxis.Z;
    }

    private static bool HasOppositePair(byte connectionMask, byte negativeFlag, byte positiveFlag)
    {
        return TransportTubeUtility.CountConnections(connectionMask) == 2 &&
               TransportTubeUtility.IsConnectionActive(connectionMask, negativeFlag) &&
               TransportTubeUtility.IsConnectionActive(connectionMask, positiveFlag);
    }
}
