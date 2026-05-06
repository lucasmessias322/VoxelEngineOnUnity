using UnityEngine;

public static class ConveyorBeltUtility
{
    public const float DefaultSpeed = 2.75f;
    public const float DefaultCenteringStrength = 8f;
    public const float DefaultMaxCenteringSpeed = 1.75f;

    private const float SupportProbeInset = 0.02f;
    private const float FootprintProbeInset = 0.01f;

    public static BlockPlacementAxis ResolvePlacementAxis(Vector3 lookForward)
    {
        Vector3 horizontal = new Vector3(lookForward.x, 0f, lookForward.z);
        if (horizontal.sqrMagnitude <= 0.0001f)
            return BlockPlacementAxis.Z;

        return Mathf.Abs(horizontal.x) >= Mathf.Abs(horizontal.z)
            ? (horizontal.x >= 0f ? BlockPlacementAxis.XNegative : BlockPlacementAxis.X)
            : (horizontal.z >= 0f ? BlockPlacementAxis.ZNegative : BlockPlacementAxis.Z);
    }

    public static Vector3 GetForwardDirection(BlockPlacementAxis placementAxis)
    {
        switch (BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis))
        {
            case BlockPlacementAxis.X:
                return Vector3.right;

            case BlockPlacementAxis.XNegative:
                return Vector3.left;

            case BlockPlacementAxis.ZNegative:
                return Vector3.forward;

            default:
                return Vector3.back;
        }
    }

    public static bool TryGetConveyorVelocity(
        World world,
        Vector3 itemCenter,
        float collisionHalfExtent,
        float speed,
        float centeringStrength,
        float maxCenteringSpeed,
        out Vector3 conveyorVelocity)
    {
        conveyorVelocity = Vector3.zero;
        if (world == null)
            return false;

        if (!TryFindSupportConveyor(world, itemCenter, collisionHalfExtent, out Vector3Int beltPos))
            return false;

        BlockPlacementAxis placementAxis = ResolveConveyorAxis(world, beltPos);
        Vector3 forward = GetForwardDirection(placementAxis);
        Vector3 centering = GetLaneCenteringVelocity(itemCenter, beltPos, forward, centeringStrength, maxCenteringSpeed);

        if (IsForwardBlockedByUnloadedChunk(world, beltPos, forward))
        {
            conveyorVelocity = centering;
            return true;
        }

        conveyorVelocity = forward * Mathf.Max(0f, speed) + centering;
        return conveyorVelocity.sqrMagnitude > 0.0001f;
    }

    public static bool IsSupportedByConveyor(World world, Vector3 itemCenter, float collisionHalfExtent)
    {
        return world != null &&
               TryFindSupportConveyor(world, itemCenter, collisionHalfExtent, out _);
    }

    private static bool TryFindSupportConveyor(World world, Vector3 itemCenter, float collisionHalfExtent, out Vector3Int beltPos)
    {
        float halfExtent = Mathf.Max(0.01f, collisionHalfExtent);
        int supportY = Mathf.FloorToInt(itemCenter.y - halfExtent - SupportProbeInset);

        if (TryFindSupportConveyorAtY(world, itemCenter, halfExtent, supportY, out beltPos))
            return true;

        return TryFindSupportConveyorAtY(world, itemCenter, halfExtent, supportY + 1, out beltPos);
    }

    private static bool TryFindSupportConveyorAtY(
        World world,
        Vector3 itemCenter,
        float halfExtent,
        int supportY,
        out Vector3Int beltPos)
    {
        beltPos = new Vector3Int(
            Mathf.FloorToInt(itemCenter.x),
            supportY,
            Mathf.FloorToInt(itemCenter.z));

        if (IsConveyorAt(world, beltPos))
            return true;

        float probeHalfExtent = Mathf.Max(0.01f, halfExtent - FootprintProbeInset);
        int minX = Mathf.FloorToInt(itemCenter.x - probeHalfExtent);
        int maxX = Mathf.FloorToInt(itemCenter.x + probeHalfExtent);
        int minZ = Mathf.FloorToInt(itemCenter.z - probeHalfExtent);
        int maxZ = Mathf.FloorToInt(itemCenter.z + probeHalfExtent);

        bool found = false;
        float bestScore = float.MaxValue;
        Vector2 itemXZ = new Vector2(itemCenter.x, itemCenter.z);

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                Vector3Int candidate = new Vector3Int(x, supportY, z);
                if (!IsConveyorAt(world, candidate))
                    continue;

                Vector2 candidateCenter = new Vector2(candidate.x + 0.5f, candidate.z + 0.5f);
                float score = (candidateCenter - itemXZ).sqrMagnitude;
                if (score >= bestScore)
                    continue;

                bestScore = score;
                beltPos = candidate;
                found = true;
            }
        }

        return found;
    }

    private static BlockPlacementAxis ResolveConveyorAxis(World world, Vector3Int beltPos)
    {
        BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeStoredAxis(
            world.GetPlacementAxisAt(beltPos, BlockType.ConveyorBelt));
        if (axis != BlockPlacementAxis.Y)
            return axis;

        if (TryGetNeighborConveyorAxis(world, beltPos + Vector3Int.left, out axis) ||
            TryGetNeighborConveyorAxis(world, beltPos + Vector3Int.right, out axis) ||
            TryGetNeighborConveyorAxis(world, beltPos + new Vector3Int(0, 0, -1), out axis) ||
            TryGetNeighborConveyorAxis(world, beltPos + new Vector3Int(0, 0, 1), out axis))
        {
            return axis;
        }

        return BlockPlacementAxis.Z;
    }

    private static bool TryGetNeighborConveyorAxis(World world, Vector3Int neighborPos, out BlockPlacementAxis axis)
    {
        axis = BlockPlacementAxis.Y;
        if (!IsConveyorAt(world, neighborPos))
            return false;

        axis = BlockPlacementRotationUtility.SanitizeStoredAxis(
            world.GetPlacementAxisAt(neighborPos, BlockType.ConveyorBelt));
        return axis != BlockPlacementAxis.Y;
    }

    private static bool IsConveyorAt(World world, Vector3Int blockPos)
    {
        if (!IsColumnLoaded(world, blockPos))
            return false;

        return world.TryGetLoadedBlockAt(blockPos, out BlockType loadedBlock) &&
               loadedBlock == BlockType.ConveyorBelt;
    }

    private static bool IsForwardBlockedByUnloadedChunk(World world, Vector3Int beltPos, Vector3 forward)
    {
        Vector3Int forwardStep = ResolveHorizontalStep(forward);
        return forwardStep != Vector3Int.zero &&
               !IsColumnLoaded(world, beltPos + forwardStep);
    }

    private static bool IsColumnLoaded(World world, Vector3Int blockPos)
    {
        return world != null && world.IsWorldColumnLoaded(blockPos.x, blockPos.z);
    }

    private static Vector3Int ResolveHorizontalStep(Vector3 direction)
    {
        if (direction.sqrMagnitude <= 0.0001f)
            return Vector3Int.zero;

        if (Mathf.Abs(direction.x) >= Mathf.Abs(direction.z))
            return direction.x > 0f ? Vector3Int.right : Vector3Int.left;

        return direction.z > 0f ? new Vector3Int(0, 0, 1) : new Vector3Int(0, 0, -1);
    }

    private static Vector3 GetLaneCenteringVelocity(
        Vector3 itemCenter,
        Vector3Int beltPos,
        Vector3 forward,
        float centeringStrength,
        float maxCenteringSpeed)
    {
        float strength = Mathf.Max(0f, centeringStrength);
        float maxSpeed = Mathf.Max(0f, maxCenteringSpeed);
        if (strength <= 0f || maxSpeed <= 0f)
            return Vector3.zero;

        Vector3 beltCenter = beltPos + Vector3.one * 0.5f;
        Vector3 lateral = Mathf.Abs(forward.x) > Mathf.Abs(forward.z)
            ? Vector3.forward * (beltCenter.z - itemCenter.z)
            : Vector3.right * (beltCenter.x - itemCenter.x);

        return Vector3.ClampMagnitude(lateral * strength, maxSpeed);
    }
}
