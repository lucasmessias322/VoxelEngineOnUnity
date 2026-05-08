using System;
using System.Collections.Generic;
using UnityEngine;

public static class ConveyorBeltUtility
{
    public const float DefaultSpeed = 2.75f;
    public const float DefaultCenteringStrength = 8f;
    public const float DefaultMaxCenteringSpeed = 1.75f;

    private const float SupportProbeInset = 0.02f;
    private const float FootprintProbeInset = 0.01f;
    private const int SplitterRoutePruneIntervalFrames = 240;
    private const int SplitterRouteMaxIdleFrames = 480;

    private static readonly Dictionary<Vector3Int, int> splitterNextOutputIndices = new Dictionary<Vector3Int, int>();
    private static readonly Dictionary<SplitterRouteKey, SplitterRouteAssignment> splitterRouteAssignments =
        new Dictionary<SplitterRouteKey, SplitterRouteAssignment>();
    private static int lastSplitterRoutePruneFrame;

    private readonly struct SplitterRouteKey : IEquatable<SplitterRouteKey>
    {
        private readonly Vector3Int splitterPos;
        private readonly int itemRoutingKey;

        public SplitterRouteKey(Vector3Int splitterPos, int itemRoutingKey)
        {
            this.splitterPos = splitterPos;
            this.itemRoutingKey = itemRoutingKey;
        }

        public bool Equals(SplitterRouteKey other)
        {
            return splitterPos == other.splitterPos && itemRoutingKey == other.itemRoutingKey;
        }

        public override bool Equals(object obj)
        {
            return obj is SplitterRouteKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (splitterPos.GetHashCode() * 397) ^ itemRoutingKey;
            }
        }
    }

    private struct SplitterRouteAssignment
    {
        public Vector3Int outputStep;
        public int lastSeenFrame;
    }

    private struct SplitterOutputSet
    {
        private Vector3Int first;
        private Vector3Int second;
        private Vector3Int third;

        public int Count { get; private set; }

        public void Add(Vector3Int step)
        {
            switch (Count)
            {
                case 0:
                    first = step;
                    break;

                case 1:
                    second = step;
                    break;

                default:
                    third = step;
                    break;
            }

            Count++;
        }

        public bool Contains(Vector3Int step)
        {
            return (Count > 0 && first == step) ||
                   (Count > 1 && second == step) ||
                   (Count > 2 && third == step);
        }

        public Vector3Int Get(int index)
        {
            return index switch
            {
                0 => first,
                1 => second,
                _ => third
            };
        }
    }

    public static BlockPlacementAxis ResolvePlacementAxis(Vector3 lookForward)
    {
        Vector3 horizontal = new Vector3(lookForward.x, 0f, lookForward.z);
        if (horizontal.sqrMagnitude <= 0.0001f)
            return BlockPlacementAxis.Z;

        return Mathf.Abs(horizontal.x) >= Mathf.Abs(horizontal.z)
            ? (horizontal.x >= 0f ? BlockPlacementAxis.XNegative : BlockPlacementAxis.X)
            : (horizontal.z >= 0f ? BlockPlacementAxis.Z : BlockPlacementAxis.ZNegative);
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
        return TryGetConveyorVelocity(
            world,
            itemCenter,
            collisionHalfExtent,
            speed,
            centeringStrength,
            maxCenteringSpeed,
            0,
            out conveyorVelocity);
    }

    public static bool TryGetConveyorVelocity(
        World world,
        Vector3 itemCenter,
        float collisionHalfExtent,
        float speed,
        float centeringStrength,
        float maxCenteringSpeed,
        int itemRoutingKey,
        out Vector3 conveyorVelocity)
    {
        conveyorVelocity = Vector3.zero;
        if (world == null)
            return false;

        if (!TryFindSupportConveyor(world, itemCenter, collisionHalfExtent, out Vector3Int beltPos, out BlockType conveyorType))
            return false;

        if (conveyorType == BlockType.conveyorBelt_splitter)
        {
            return TryGetSplitterVelocity(
                world,
                itemCenter,
                beltPos,
                speed,
                centeringStrength,
                maxCenteringSpeed,
                itemRoutingKey,
                out conveyorVelocity);
        }

        BlockPlacementAxis placementAxis = ResolveConveyorAxis(world, beltPos, conveyorType);
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
               TryFindSupportConveyor(world, itemCenter, collisionHalfExtent, out _, out _);
    }

    public static bool TryGetSupportedSplitterOutputCount(
        World world,
        Vector3 itemCenter,
        float collisionHalfExtent,
        out Vector3Int splitterPos,
        out int outputCount)
    {
        splitterPos = default;
        outputCount = 0;
        if (world == null)
            return false;

        if (!TryFindSupportConveyor(world, itemCenter, collisionHalfExtent, out splitterPos, out BlockType conveyorType) ||
            conveyorType != BlockType.conveyorBelt_splitter)
        {
            return false;
        }

        BlockPlacementAxis placementAxis = ResolveConveyorAxis(world, splitterPos, BlockType.conveyorBelt_splitter);
        Vector3Int placementForwardStep = ResolveHorizontalStep(GetForwardDirection(placementAxis));
        Vector3Int outputForwardStep = ResolveSplitterOutputForwardStep(world, splitterPos, placementForwardStep);
        outputCount = GetAvailableSplitterOutputs(world, splitterPos, outputForwardStep).Count;
        return true;
    }

    public static bool IsConveyorBlock(BlockType blockType)
    {
        return blockType == BlockType.ConveyorBelt ||
               blockType == BlockType.conveyorBelt_splitter;
    }

    private static bool TryFindSupportConveyor(
        World world,
        Vector3 itemCenter,
        float collisionHalfExtent,
        out Vector3Int beltPos,
        out BlockType conveyorType)
    {
        float halfExtent = Mathf.Max(0.01f, collisionHalfExtent);
        int supportY = Mathf.FloorToInt(itemCenter.y - halfExtent - SupportProbeInset);

        if (TryFindSupportConveyorAtY(world, itemCenter, halfExtent, supportY, out beltPos, out conveyorType))
            return true;

        return TryFindSupportConveyorAtY(world, itemCenter, halfExtent, supportY + 1, out beltPos, out conveyorType);
    }

    private static bool TryFindSupportConveyorAtY(
        World world,
        Vector3 itemCenter,
        float halfExtent,
        int supportY,
        out Vector3Int beltPos,
        out BlockType conveyorType)
    {
        beltPos = new Vector3Int(
            Mathf.FloorToInt(itemCenter.x),
            supportY,
            Mathf.FloorToInt(itemCenter.z));

        if (TryGetConveyorAt(world, beltPos, out conveyorType))
            return true;

        float probeHalfExtent = Mathf.Max(0.01f, halfExtent - FootprintProbeInset);
        int minX = Mathf.FloorToInt(itemCenter.x - probeHalfExtent);
        int maxX = Mathf.FloorToInt(itemCenter.x + probeHalfExtent);
        int minZ = Mathf.FloorToInt(itemCenter.z - probeHalfExtent);
        int maxZ = Mathf.FloorToInt(itemCenter.z + probeHalfExtent);

        bool found = false;
        float bestScore = float.MaxValue;
        Vector2 itemXZ = new Vector2(itemCenter.x, itemCenter.z);
        conveyorType = BlockType.Air;

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                Vector3Int candidate = new Vector3Int(x, supportY, z);
                if (!TryGetConveyorAt(world, candidate, out BlockType candidateType))
                    continue;

                Vector2 candidateCenter = new Vector2(candidate.x + 0.5f, candidate.z + 0.5f);
                float score = (candidateCenter - itemXZ).sqrMagnitude;
                if (score >= bestScore)
                    continue;

                bestScore = score;
                beltPos = candidate;
                conveyorType = candidateType;
                found = true;
            }
        }

        return found;
    }

    private static bool TryGetSplitterVelocity(
        World world,
        Vector3 itemCenter,
        Vector3Int splitterPos,
        float speed,
        float centeringStrength,
        float maxCenteringSpeed,
        int itemRoutingKey,
        out Vector3 conveyorVelocity)
    {
        BlockPlacementAxis placementAxis = ResolveConveyorAxis(world, splitterPos, BlockType.conveyorBelt_splitter);
        Vector3Int placementForwardStep = ResolveHorizontalStep(GetForwardDirection(placementAxis));
        Vector3Int forwardStep = ResolveSplitterOutputForwardStep(world, splitterPos, placementForwardStep);
        Vector3 forward = new Vector3(forwardStep.x, 0f, forwardStep.z);
        SplitterOutputSet outputs = GetAvailableSplitterOutputs(world, splitterPos, forwardStep);

        if (outputs.Count == 0)
        {
            conveyorVelocity = GetLaneCenteringVelocity(itemCenter, splitterPos, forward, centeringStrength, maxCenteringSpeed);
            return conveyorVelocity.sqrMagnitude > 0.0001f;
        }

        Vector3Int selectedStep = ResolveSplitterOutputStep(splitterPos, outputs, itemRoutingKey);
        Vector3 selectedDirection = new Vector3(selectedStep.x, 0f, selectedStep.z);

        if (selectedStep != forwardStep && ShouldCarrySplitterItemToCenter(itemCenter, splitterPos, forward))
            selectedDirection = forward;

        Vector3 centering = GetLaneCenteringVelocity(itemCenter, splitterPos, selectedDirection, centeringStrength, maxCenteringSpeed);
        if (IsStepBlockedByUnloadedChunk(world, splitterPos, selectedStep))
        {
            conveyorVelocity = centering;
            return true;
        }

        conveyorVelocity = selectedDirection * Mathf.Max(0f, speed) + centering;
        return conveyorVelocity.sqrMagnitude > 0.0001f;
    }

    private static SplitterOutputSet GetAvailableSplitterOutputs(World world, Vector3Int splitterPos, Vector3Int forwardStep)
    {
        SplitterOutputSet outputs = default;
        Vector3Int leftStep = GetLeftStep(forwardStep);
        Vector3Int rightStep = -leftStep;

        AddSplitterOutputIfConveyor(world, splitterPos, forwardStep, ref outputs);
        AddSplitterOutputIfConveyor(world, splitterPos, leftStep, ref outputs);
        AddSplitterOutputIfConveyor(world, splitterPos, rightStep, ref outputs);

        return outputs;
    }

    private static Vector3Int ResolveSplitterOutputForwardStep(
        World world,
        Vector3Int splitterPos,
        Vector3Int placementForwardStep)
    {
        if (placementForwardStep == Vector3Int.zero)
            return placementForwardStep;

        if (IsSplitterFrontOutputCandidate(world, splitterPos, placementForwardStep))
            return placementForwardStep;

        Vector3Int oppositeStep = -placementForwardStep;
        if (IsSplitterFrontOutputCandidate(world, splitterPos, oppositeStep))
            return oppositeStep;

        return placementForwardStep;
    }

    private static bool IsSplitterFrontOutputCandidate(World world, Vector3Int splitterPos, Vector3Int step)
    {
        return IsConveyorOrientedAwayFromSplitter(world, splitterPos, step);
    }

    private static bool IsConveyorOrientedAwayFromSplitter(World world, Vector3Int splitterPos, Vector3Int outputStep)
    {
        if (outputStep == Vector3Int.zero)
            return false;

        Vector3Int conveyorPos = splitterPos + outputStep;
        if (!TryGetConveyorAt(world, conveyorPos, out BlockType conveyorType))
            return false;

        BlockPlacementAxis axis = ResolveConveyorAxis(world, conveyorPos, conveyorType);
        Vector3Int conveyorForwardStep = ResolveHorizontalStep(GetForwardDirection(axis));
        return conveyorForwardStep == outputStep;
    }

    private static void AddSplitterOutputIfConveyor(
        World world,
        Vector3Int splitterPos,
        Vector3Int outputStep,
        ref SplitterOutputSet outputs)
    {
        if (outputStep == Vector3Int.zero)
            return;

        if (IsConveyorOrientedAwayFromSplitter(world, splitterPos, outputStep))
            outputs.Add(outputStep);
    }

    private static Vector3Int ResolveSplitterOutputStep(
        Vector3Int splitterPos,
        SplitterOutputSet outputs,
        int itemRoutingKey)
    {
        if (outputs.Count <= 1)
            return outputs.Get(0);

        PruneStaleSplitterRoutes();
        int frame = Time.frameCount;

        if (itemRoutingKey != 0)
        {
            SplitterRouteKey routeKey = new SplitterRouteKey(splitterPos, itemRoutingKey);
            if (splitterRouteAssignments.TryGetValue(routeKey, out SplitterRouteAssignment assignment) &&
                outputs.Contains(assignment.outputStep))
            {
                assignment.lastSeenFrame = frame;
                splitterRouteAssignments[routeKey] = assignment;
                return assignment.outputStep;
            }

            Vector3Int outputStep = GetNextSplitterOutputStep(splitterPos, outputs);
            splitterRouteAssignments[routeKey] = new SplitterRouteAssignment
            {
                outputStep = outputStep,
                lastSeenFrame = frame
            };
            return outputStep;
        }

        return GetNextSplitterOutputStep(splitterPos, outputs);
    }

    private static Vector3Int GetNextSplitterOutputStep(Vector3Int splitterPos, SplitterOutputSet outputs)
    {
        splitterNextOutputIndices.TryGetValue(splitterPos, out int nextIndex);
        Vector3Int outputStep = outputs.Get(Mathf.Abs(nextIndex) % outputs.Count);
        splitterNextOutputIndices[splitterPos] = nextIndex + 1;
        return outputStep;
    }

    private static bool ShouldCarrySplitterItemToCenter(Vector3 itemCenter, Vector3Int splitterPos, Vector3 forward)
    {
        Vector3 splitterCenter = splitterPos + Vector3.one * 0.5f;
        return Vector3.Dot(itemCenter - splitterCenter, forward) < -0.05f;
    }

    private static void PruneStaleSplitterRoutes()
    {
        int frame = Time.frameCount;
        if (frame - lastSplitterRoutePruneFrame < SplitterRoutePruneIntervalFrames)
            return;

        lastSplitterRoutePruneFrame = frame;
        List<SplitterRouteKey> staleKeys = null;
        foreach (KeyValuePair<SplitterRouteKey, SplitterRouteAssignment> pair in splitterRouteAssignments)
        {
            if (frame - pair.Value.lastSeenFrame <= SplitterRouteMaxIdleFrames)
                continue;

            staleKeys ??= new List<SplitterRouteKey>();
            staleKeys.Add(pair.Key);
        }

        if (staleKeys == null)
            return;

        for (int i = 0; i < staleKeys.Count; i++)
            splitterRouteAssignments.Remove(staleKeys[i]);
    }

    private static BlockPlacementAxis ResolveConveyorAxis(World world, Vector3Int beltPos, BlockType conveyorType)
    {
        BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeStoredAxis(
            world.GetPlacementAxisAt(beltPos, conveyorType));
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
        if (!TryGetConveyorAt(world, neighborPos, out BlockType conveyorType))
            return false;

        axis = BlockPlacementRotationUtility.SanitizeStoredAxis(
            world.GetPlacementAxisAt(neighborPos, conveyorType));
        return axis != BlockPlacementAxis.Y;
    }

    private static bool TryGetConveyorAt(World world, Vector3Int blockPos, out BlockType conveyorType)
    {
        conveyorType = BlockType.Air;
        if (!IsColumnLoaded(world, blockPos))
            return false;

        if (!world.TryGetLoadedBlockAt(blockPos, out BlockType loadedBlock) ||
            !IsConveyorBlock(loadedBlock))
        {
            return false;
        }

        conveyorType = loadedBlock;
        return true;
    }

    private static bool IsForwardBlockedByUnloadedChunk(World world, Vector3Int beltPos, Vector3 forward)
    {
        Vector3Int forwardStep = ResolveHorizontalStep(forward);
        return IsStepBlockedByUnloadedChunk(world, beltPos, forwardStep);
    }

    private static bool IsStepBlockedByUnloadedChunk(World world, Vector3Int beltPos, Vector3Int forwardStep)
    {
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

    private static Vector3Int GetLeftStep(Vector3Int forwardStep)
    {
        if (forwardStep == Vector3Int.zero)
            return Vector3Int.zero;

        return new Vector3Int(-forwardStep.z, 0, forwardStep.x);
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
