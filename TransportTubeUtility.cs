using UnityEngine;
using Unity.Collections;

public static class TransportTubeUtility
{
    private const float TubeMin = 0.3125f;
    private const float TubeMax = 0.6875f;
    private const float TubeFloorY = 0f;
    private const float TubeTopY = 0.375f;

    public const byte ConnectWest = 1 << 0;
    public const byte ConnectEast = 1 << 1;
    public const byte ConnectSouth = 1 << 2;
    public const byte ConnectNorth = 1 << 3;
    public const byte ConnectDown = 1 << 4;
    public const byte ConnectUp = 1 << 5;

    public readonly struct TubeState
    {
        public readonly BlockType blockType;
        public readonly BlockPlacementAxis placementAxis;

        public TubeState(BlockType blockType, BlockPlacementAxis placementAxis)
        {
            this.blockType = blockType;
            this.placementAxis = placementAxis;
        }
    }

    public static bool IsTransportTubeBlock(BlockType blockType)
    {
        return blockType == BlockType.TransportTube ||
               blockType == BlockType.TransportTube_L ||
               blockType == BlockType.TransportTube_T;
    }

    public static BlockType GetInventoryDropBlockType(BlockType blockType)
    {
        return IsTransportTubeBlock(blockType) ? BlockType.TransportTube : blockType;
    }

    public static TubeState ResolveState(World world, Vector3Int tubePos, BlockPlacementAxis fallbackAxis)
    {
        return ResolveState(ResolveConnectionMask(world, tubePos), fallbackAxis);
    }

    public static TubeState ResolveState(byte connectionMask, BlockPlacementAxis fallbackAxis)
    {
        int connectionCount = CountConnections(connectionMask);

        if (connectionCount <= 0)
            return new TubeState(BlockType.TransportTube, ResolveFallbackStraightAxis(fallbackAxis));

        if (connectionCount == 1)
            return new TubeState(BlockType.TransportTube, ResolveSingleConnectionAxis(connectionMask));

        if (connectionCount == 2)
        {
            if (HasOppositePair(connectionMask, ConnectWest, ConnectEast))
                return new TubeState(BlockType.TransportTube, BlockPlacementAxis.X);

            if (HasOppositePair(connectionMask, ConnectSouth, ConnectNorth))
                return new TubeState(BlockType.TransportTube, BlockPlacementAxis.Z);

            if (HasOppositePair(connectionMask, ConnectDown, ConnectUp))
                return new TubeState(BlockType.TransportTube, BlockPlacementAxis.YNegative);

            return new TubeState(BlockType.TransportTube_L, ResolveCornerAxis(connectionMask));
        }

        return new TubeState(BlockType.TransportTube_T, ResolveTJunctionAxis(connectionMask));
    }

    public static bool HasVerticalConnection(byte connectionMask)
    {
        return IsConnectionActive(connectionMask, ConnectDown) ||
               IsConnectionActive(connectionMask, ConnectUp);
    }

    public static byte ResolveConnectionMask(World world, Vector3Int blockPos)
    {
        byte mask = 0;
        if (HasTubeAt(world, blockPos + Vector3Int.left)) mask |= ConnectWest;
        if (HasTubeAt(world, blockPos + Vector3Int.right)) mask |= ConnectEast;
        if (HasTubeAt(world, blockPos + Vector3Int.back)) mask |= ConnectSouth;
        if (HasTubeAt(world, blockPos + Vector3Int.forward)) mask |= ConnectNorth;
        if (HasTubeAt(world, blockPos + Vector3Int.down)) mask |= ConnectDown;
        if (HasTubeAt(world, blockPos + Vector3Int.up)) mask |= ConnectUp;
        return mask;
    }

    public static byte ResolveConnectionMask(
        int voxelX,
        int voxelY,
        int voxelZ,
        NativeArray<byte> blockTypes,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        byte mask = 0;
        if (CanConnect(voxelX - 1, voxelY, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= ConnectWest;
        if (CanConnect(voxelX + 1, voxelY, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= ConnectEast;
        if (CanConnect(voxelX, voxelY, voxelZ - 1, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= ConnectSouth;
        if (CanConnect(voxelX, voxelY, voxelZ + 1, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= ConnectNorth;
        if (CanConnect(voxelX, voxelY - 1, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= ConnectDown;
        if (CanConnect(voxelX, voxelY + 1, voxelZ, blockTypes, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= ConnectUp;
        return mask;
    }

    public static bool IsConnectionActive(byte connectionMask, byte flag)
    {
        return (connectionMask & flag) != 0;
    }

    public static int CountConnections(byte connectionMask)
    {
        int count = 0;
        if (IsConnectionActive(connectionMask, ConnectWest)) count++;
        if (IsConnectionActive(connectionMask, ConnectEast)) count++;
        if (IsConnectionActive(connectionMask, ConnectSouth)) count++;
        if (IsConnectionActive(connectionMask, ConnectNorth)) count++;
        if (IsConnectionActive(connectionMask, ConnectDown)) count++;
        if (IsConnectionActive(connectionMask, ConnectUp)) count++;
        return count;
    }

    public static ShapeBox GetCenterVisualBox()
    {
        return new ShapeBox(new Vector3(TubeMin, TubeFloorY, TubeMin), new Vector3(TubeMax, TubeTopY, TubeMax));
    }

    public static ShapeBox GetArmVisualBox(byte directionFlag)
    {
        return directionFlag switch
        {
            ConnectWest => new ShapeBox(new Vector3(0f, TubeFloorY, TubeMin), new Vector3(0.5f, TubeTopY, TubeMax)),
            ConnectEast => new ShapeBox(new Vector3(0.5f, TubeFloorY, TubeMin), new Vector3(1f, TubeTopY, TubeMax)),
            ConnectSouth => new ShapeBox(new Vector3(TubeMin, TubeFloorY, 0f), new Vector3(TubeMax, TubeTopY, 0.5f)),
            ConnectNorth => new ShapeBox(new Vector3(TubeMin, TubeFloorY, 0.5f), new Vector3(TubeMax, TubeTopY, 1f)),
            ConnectDown => new ShapeBox(new Vector3(TubeMin, 0f, TubeMin), new Vector3(TubeMax, TubeTopY, TubeMax)),
            _ => new ShapeBox(new Vector3(TubeMin, TubeTopY, TubeMin), new Vector3(TubeMax, 1f, TubeMax))
        };
    }

    public static FixedList512Bytes<ShapeBox> BuildVisualBoxes(byte connectionMask, BlockPlacementAxis fallbackAxis)
    {
        FixedList512Bytes<ShapeBox> boxes = default;
        boxes.Add(GetCenterVisualBox());

        int connectionCount = CountConnections(connectionMask);
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

        AddArmIfConnected(ref boxes, connectionMask, ConnectWest);
        AddArmIfConnected(ref boxes, connectionMask, ConnectEast);
        AddArmIfConnected(ref boxes, connectionMask, ConnectSouth);
        AddArmIfConnected(ref boxes, connectionMask, ConnectNorth);
        AddArmIfConnected(ref boxes, connectionMask, ConnectDown);
        AddArmIfConnected(ref boxes, connectionMask, ConnectUp);
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

    private static bool HasTubeAt(World world, Vector3Int pos)
    {
        return world != null && IsTransportTubeBlock(world.GetBlockAt(pos));
    }

    private static bool CanConnect(
        int x,
        int y,
        int z,
        NativeArray<byte> blockTypes,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        if (x < 0 || x >= voxelSizeX || y < 0 || y >= Chunk.SizeY || z < 0 || z >= voxelSizeZ)
            return false;

        int idx = x + y * voxelSizeX + z * voxelPlaneSize;
        return IsTransportTubeBlock((BlockType)blockTypes[idx]);
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
        if (IsConnectionActive(connectionMask, ConnectDown) || IsConnectionActive(connectionMask, ConnectUp))
            return BlockPlacementAxis.YNegative;

        return IsConnectionActive(connectionMask, ConnectWest) || IsConnectionActive(connectionMask, ConnectEast)
            ? BlockPlacementAxis.X
            : BlockPlacementAxis.Z;
    }

    private static BlockPlacementAxis ResolveCornerAxis(byte connectionMask)
    {
        bool left = IsConnectionActive(connectionMask, ConnectWest);
        bool right = IsConnectionActive(connectionMask, ConnectEast);
        bool back = IsConnectionActive(connectionMask, ConnectSouth);
        bool forward = IsConnectionActive(connectionMask, ConnectNorth);

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
        bool left = IsConnectionActive(connectionMask, ConnectWest);
        bool right = IsConnectionActive(connectionMask, ConnectEast);
        bool back = IsConnectionActive(connectionMask, ConnectSouth);
        bool forward = IsConnectionActive(connectionMask, ConnectNorth);

        if (!forward && left && right && back)
            return BlockPlacementAxis.Z;

        if (!right && left && back && forward)
            return BlockPlacementAxis.X;

        if (!left && right && back && forward)
            return BlockPlacementAxis.XNegative;

        if (!back && left && right && forward)
            return BlockPlacementAxis.ZNegative;

        if (IsConnectionActive(connectionMask, ConnectDown) || IsConnectionActive(connectionMask, ConnectUp))
            return BlockPlacementAxis.YNegative;

        return BlockPlacementAxis.Z;
    }

    private static bool HasOppositePair(byte connectionMask, byte negativeFlag, byte positiveFlag)
    {
        return CountConnections(connectionMask) == 2 &&
               IsConnectionActive(connectionMask, negativeFlag) &&
               IsConnectionActive(connectionMask, positiveFlag);
    }

    private static void AddArmIfConnected(ref FixedList512Bytes<ShapeBox> boxes, byte connectionMask, byte directionFlag)
    {
        if (IsConnectionActive(connectionMask, directionFlag))
            boxes.Add(GetArmVisualBox(directionFlag));
    }

    private static void AddFallbackStraightArms(ref FixedList512Bytes<ShapeBox> boxes, BlockPlacementAxis fallbackAxis)
    {
        fallbackAxis = ResolveFallbackStraightAxis(fallbackAxis);
        switch (fallbackAxis)
        {
            case BlockPlacementAxis.X:
            case BlockPlacementAxis.XNegative:
                boxes.Add(GetArmVisualBox(ConnectWest));
                boxes.Add(GetArmVisualBox(ConnectEast));
                break;

            case BlockPlacementAxis.YNegative:
                boxes.Add(GetArmVisualBox(ConnectDown));
                boxes.Add(GetArmVisualBox(ConnectUp));
                break;

            default:
                boxes.Add(GetArmVisualBox(ConnectSouth));
                boxes.Add(GetArmVisualBox(ConnectNorth));
                break;
        }
    }

    private static void AddSingleConnectionStraightArms(ref FixedList512Bytes<ShapeBox> boxes, byte connectionMask)
    {
        if (IsConnectionActive(connectionMask, ConnectWest) || IsConnectionActive(connectionMask, ConnectEast))
        {
            boxes.Add(GetArmVisualBox(ConnectWest));
            boxes.Add(GetArmVisualBox(ConnectEast));
            return;
        }

        if (IsConnectionActive(connectionMask, ConnectDown) || IsConnectionActive(connectionMask, ConnectUp))
        {
            boxes.Add(GetArmVisualBox(ConnectDown));
            boxes.Add(GetArmVisualBox(ConnectUp));
            return;
        }

        boxes.Add(GetArmVisualBox(ConnectSouth));
        boxes.Add(GetArmVisualBox(ConnectNorth));
    }
}
