using UnityEngine;
using Unity.Collections;

public static class FluidPipeUtility
{
    private const float PipeMin = 0.3125f;
    private const float PipeMax = 0.6875f;

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
               blockType == BlockType.FluidPipe_ShapeT ||
               blockType == BlockType.FluidPipe_ShapeCross;
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

        if (connectionCount == 4)
            return new PipeState(BlockType.FluidPipe_ShapeCross, BlockPlacementAxis.Z);

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
        FixedList512Bytes<ShapeBox> boxes = default;
        boxes.Add(GetCenterVisualBox());

        int connectionCount = TransportTubeUtility.CountConnections(connectionMask);
        if (connectionCount == 0)
        {
            AddFallbackStraightArms(ref boxes, fallbackAxis);
            return boxes;
        }

        if (connectionCount == 1)
        {
            AddSingleConnectionStraightArms(ref boxes, connectionMask);
            return boxes;
        }

        AddArmIfConnected(ref boxes, connectionMask, TransportTubeUtility.ConnectWest);
        AddArmIfConnected(ref boxes, connectionMask, TransportTubeUtility.ConnectEast);
        AddArmIfConnected(ref boxes, connectionMask, TransportTubeUtility.ConnectSouth);
        AddArmIfConnected(ref boxes, connectionMask, TransportTubeUtility.ConnectNorth);
        AddArmIfConnected(ref boxes, connectionMask, TransportTubeUtility.ConnectDown);
        AddArmIfConnected(ref boxes, connectionMask, TransportTubeUtility.ConnectUp);
        return boxes;
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

    public static bool CanConnect(World world, Vector3Int blockPos, Vector3Int directionToNeighbor)
    {
        if (world == null)
            return false;

        Vector3Int neighborPos = blockPos + directionToNeighbor;
        BlockType neighborType = world.GetBlockAt(neighborPos);
        if (IsFluidPipeBlock(neighborType))
            return true;

        if (neighborType == BlockType.SteamEngine)
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

        if (neighborType == BlockType.SteamEngine)
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

    private static ShapeBox GetCenterVisualBox()
    {
        return new ShapeBox(
            new Vector3(PipeMin, PipeMin, PipeMin),
            new Vector3(PipeMax, PipeMax, PipeMax));
    }

    private static ShapeBox GetArmVisualBox(byte directionFlag)
    {
        switch (directionFlag)
        {
            case TransportTubeUtility.ConnectWest:
                return new ShapeBox(
                    new Vector3(0f, PipeMin, PipeMin),
                    new Vector3(PipeMin, PipeMax, PipeMax));

            case TransportTubeUtility.ConnectEast:
                return new ShapeBox(
                    new Vector3(PipeMax, PipeMin, PipeMin),
                    new Vector3(1f, PipeMax, PipeMax));

            case TransportTubeUtility.ConnectSouth:
                return new ShapeBox(
                    new Vector3(PipeMin, PipeMin, 0f),
                    new Vector3(PipeMax, PipeMax, PipeMin));

            case TransportTubeUtility.ConnectNorth:
                return new ShapeBox(
                    new Vector3(PipeMin, PipeMin, PipeMax),
                    new Vector3(PipeMax, PipeMax, 1f));

            case TransportTubeUtility.ConnectDown:
                return new ShapeBox(
                    new Vector3(PipeMin, 0f, PipeMin),
                    new Vector3(PipeMax, PipeMin, PipeMax));

            default:
                return new ShapeBox(
                    new Vector3(PipeMin, PipeMax, PipeMin),
                    new Vector3(PipeMax, 1f, PipeMax));
        }
    }

    private static void AddArmIfConnected(ref FixedList512Bytes<ShapeBox> boxes, byte connectionMask, byte directionFlag)
    {
        if (TransportTubeUtility.IsConnectionActive(connectionMask, directionFlag))
            boxes.Add(GetArmVisualBox(directionFlag));
    }

    private static void AddFallbackStraightArms(ref FixedList512Bytes<ShapeBox> boxes, BlockPlacementAxis fallbackAxis)
    {
        fallbackAxis = ResolveFallbackStraightAxis(fallbackAxis);
        switch (fallbackAxis)
        {
            case BlockPlacementAxis.X:
            case BlockPlacementAxis.XNegative:
                boxes.Add(GetArmVisualBox(TransportTubeUtility.ConnectWest));
                boxes.Add(GetArmVisualBox(TransportTubeUtility.ConnectEast));
                break;

            case BlockPlacementAxis.YNegative:
                boxes.Add(GetArmVisualBox(TransportTubeUtility.ConnectDown));
                boxes.Add(GetArmVisualBox(TransportTubeUtility.ConnectUp));
                break;

            default:
                boxes.Add(GetArmVisualBox(TransportTubeUtility.ConnectSouth));
                boxes.Add(GetArmVisualBox(TransportTubeUtility.ConnectNorth));
                break;
        }
    }

    private static void AddSingleConnectionStraightArms(ref FixedList512Bytes<ShapeBox> boxes, byte connectionMask)
    {
        if (TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectWest) ||
            TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectEast))
        {
            boxes.Add(GetArmVisualBox(TransportTubeUtility.ConnectWest));
            boxes.Add(GetArmVisualBox(TransportTubeUtility.ConnectEast));
            return;
        }

        if (TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectDown) ||
            TransportTubeUtility.IsConnectionActive(connectionMask, TransportTubeUtility.ConnectUp))
        {
            boxes.Add(GetArmVisualBox(TransportTubeUtility.ConnectDown));
            boxes.Add(GetArmVisualBox(TransportTubeUtility.ConnectUp));
            return;
        }

        boxes.Add(GetArmVisualBox(TransportTubeUtility.ConnectSouth));
        boxes.Add(GetArmVisualBox(TransportTubeUtility.ConnectNorth));
    }
}
