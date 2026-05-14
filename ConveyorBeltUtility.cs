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
    private static readonly Dictionary<Vector3Int, SplitterFilterState> splitterFilterStates =
        new Dictionary<Vector3Int, SplitterFilterState>();
    private static int lastSplitterRoutePruneFrame;

    public enum SplitterFilterFace
    {
        Front,
        Back,
        Left,
        Right
    }

    public readonly struct SplitterFilterOutputInfo
    {
        public readonly bool front;
        public readonly bool back;
        public readonly bool left;
        public readonly bool right;

        public SplitterFilterOutputInfo(bool front, bool back, bool left, bool right)
        {
            this.front = front;
            this.back = back;
            this.left = left;
            this.right = right;
        }

        public bool HasAny => front || back || left || right;
    }

    private struct SplitterFilterState
    {
        public Item filterItem;
        public bool front;
        public bool back;
        public bool left;
        public bool right;
    }

    private readonly struct SplitterRouteKey : IEquatable<SplitterRouteKey>
    {
        private readonly Vector3Int splitterPos;
        private readonly int itemRoutingKey;

        public SplitterRouteKey(Vector3Int splitterPos, int itemRoutingKey)
        {
            this.splitterPos = splitterPos;
            this.itemRoutingKey = itemRoutingKey;
        }

        public Vector3Int SplitterPos => splitterPos;

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
        private Vector3Int fourth;

        public int Count { get; private set; }

        public void Add(Vector3Int step)
        {
            if (Contains(step) || Count >= 4)
                return;

            switch (Count)
            {
                case 0:
                    first = step;
                    break;

                case 1:
                    second = step;
                    break;

                case 2:
                    third = step;
                    break;

                default:
                    fourth = step;
                    break;
            }

            Count++;
        }

        public bool Contains(Vector3Int step)
        {
            return (Count > 0 && first == step) ||
                   (Count > 1 && second == step) ||
                   (Count > 2 && third == step) ||
                   (Count > 3 && fourth == step);
        }

        public Vector3Int Get(int index)
        {
            return index switch
            {
                0 => first,
                1 => second,
                2 => third,
                _ => fourth
            };
        }

        public bool TryGetFiltered(SplitterFilterState state, Vector3Int placementForwardStep, out SplitterOutputSet filtered)
        {
            filtered = default;
            if (Count <= 0)
                return false;

            Vector3Int leftStep = GetLeftStep(placementForwardStep);
            Vector3Int rightStep = -leftStep;

            if (state.front && Contains(placementForwardStep))
                filtered.Add(placementForwardStep);

            if (state.back && Contains(-placementForwardStep))
                filtered.Add(-placementForwardStep);

            if (state.left && Contains(leftStep))
                filtered.Add(leftStep);

            if (state.right && Contains(rightStep))
                filtered.Add(rightStep);

            return filtered.Count > 0;
        }

        public SplitterOutputSet Excluding(SplitterOutputSet excluded)
        {
            SplitterOutputSet result = default;
            for (int i = 0; i < Count; i++)
            {
                Vector3Int step = Get(i);
                if (!excluded.Contains(step))
                    result.Add(step);
            }

            return result;
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

    public static Vector3 GetForwardDirection(BlockType conveyorType, BlockPlacementAxis placementAxis)
    {
        return GetForwardDirection(placementAxis);
    }

    public static BlockPlacementAxis ConvertFlatAxisToSlopedAxis(BlockPlacementAxis placementAxis)
    {
        return BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
    }

    public static BlockPlacementAxis ConvertSlopedAxisToFlatAxis(BlockPlacementAxis placementAxis)
    {
        return BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
    }

    public static BlockPlacementAxis ResolveSlopedConveyorRampAxis(World world, Vector3Int beltPos, BlockPlacementAxis conveyorAxis)
    {
        Vector3 forward = GetForwardDirection(conveyorAxis);
        Vector3Int forwardStep = ResolveHorizontalStep(forward);
        if (forwardStep == Vector3Int.zero)
            return BlockPlacementAxis.Z;

        if (TryGetConveyorAt(world, beltPos + forwardStep + Vector3Int.up, out _))
            return ResolveRampAxisFromStep(forwardStep);

        if (TryGetConveyorAt(world, beltPos - forwardStep + Vector3Int.up, out _))
            return ResolveRampAxisFromStep(-forwardStep);

        return ResolveRampAxisFromStep(forwardStep);
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
        return TryGetConveyorVelocity(
            world,
            itemCenter,
            collisionHalfExtent,
            speed,
            centeringStrength,
            maxCenteringSpeed,
            itemRoutingKey,
            null,
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
        Item carriedItem,
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
                carriedItem,
                out conveyorVelocity);
        }

        BlockPlacementAxis placementAxis = ResolveConveyorAxis(world, beltPos, conveyorType);
        Vector3 forward = GetForwardDirection(conveyorType, placementAxis);
        Vector3 centering = GetLaneCenteringVelocity(itemCenter, beltPos, forward, centeringStrength, maxCenteringSpeed);

        if (IsForwardBlockedByUnloadedChunk(world, beltPos, forward))
        {
            conveyorVelocity = centering;
            return true;
        }

        Vector3 travelDirection = ResolveConveyorTravelDirection(world, beltPos, conveyorType, placementAxis, forward);
        conveyorVelocity = travelDirection * Mathf.Max(0f, speed) + centering;
        return conveyorVelocity.sqrMagnitude > 0.0001f;
    }

    public static bool IsSupportedByConveyor(World world, Vector3 itemCenter, float collisionHalfExtent)
    {
        return world != null &&
               TryFindSupportConveyor(world, itemCenter, collisionHalfExtent, out _, out _);
    }

    public static bool TryGetSupportConveyorVisualFrame(
        World world,
        Vector3 itemCenter,
        float collisionHalfExtent,
        out Vector3 surfaceNormal,
        out Vector3 visualForward)
    {
        surfaceNormal = Vector3.up;
        visualForward = Vector3.forward;

        if (world == null ||
            !TryFindSupportConveyor(world, itemCenter, collisionHalfExtent, out Vector3Int beltPos, out BlockType conveyorType))
        {
            return false;
        }

        BlockPlacementAxis placementAxis = ResolveConveyorAxis(world, beltPos, conveyorType);
        Vector3 beltForward = GetForwardDirection(conveyorType, placementAxis);
        Vector3 travelDirection = conveyorType == BlockType.conveyorBelt_splitter
            ? beltForward
            : ResolveConveyorTravelDirection(world, beltPos, conveyorType, placementAxis, beltForward);

        if (travelDirection.sqrMagnitude <= 0.0001f)
            travelDirection = beltForward;

        visualForward = travelDirection.normalized;
        Vector3 lateral = Mathf.Abs(beltForward.x) > Mathf.Abs(beltForward.z)
            ? Vector3.forward
            : Vector3.right;

        surfaceNormal = Vector3.Cross(visualForward, lateral).normalized;
        if (surfaceNormal.sqrMagnitude <= 0.0001f)
            surfaceNormal = Vector3.up;
        else if (surfaceNormal.y < 0f)
            surfaceNormal = -surfaceNormal;

        return true;
    }

    public static bool TryGetSupportedSplitterOutputCount(
        World world,
        Vector3 itemCenter,
        float collisionHalfExtent,
        out Vector3Int splitterPos,
        out int outputCount)
    {
        return TryGetSupportedSplitterOutputCount(
            world,
            itemCenter,
            collisionHalfExtent,
            null,
            out splitterPos,
            out outputCount);
    }

    public static bool TryGetSupportedSplitterOutputCount(
        World world,
        Vector3 itemCenter,
        float collisionHalfExtent,
        Item carriedItem,
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
        SplitterOutputSet outputs = GetAvailableSplitterOutputs(world, splitterPos, outputForwardStep);
        outputCount = ResolveFilteredSplitterOutputs(splitterPos, placementForwardStep, outputs, carriedItem).Count;
        return true;
    }

    public static void SetSplitterFilterItem(Vector3Int splitterPos, Item filterItem)
    {
        SplitterFilterState state = GetSplitterFilterState(splitterPos);
        state.filterItem = filterItem;
        splitterFilterStates[splitterPos] = state;
        ClearSplitterRouteCache(splitterPos);
    }

    public static Item GetSplitterFilterItem(Vector3Int splitterPos)
    {
        return splitterFilterStates.TryGetValue(splitterPos, out SplitterFilterState state)
            ? state.filterItem
            : null;
    }

    public static void SetSplitterFilterFaceEnabled(Vector3Int splitterPos, SplitterFilterFace face, bool enabled)
    {
        SplitterFilterState state = GetSplitterFilterState(splitterPos);
        switch (face)
        {
            case SplitterFilterFace.Front:
                state.front = enabled;
                break;

            case SplitterFilterFace.Back:
                state.back = enabled;
                break;

            case SplitterFilterFace.Left:
                state.left = enabled;
                break;

            case SplitterFilterFace.Right:
                state.right = enabled;
                break;
        }

        splitterFilterStates[splitterPos] = state;
        ClearSplitterRouteCache(splitterPos);
    }

    public static bool GetSplitterFilterFaceEnabled(Vector3Int splitterPos, SplitterFilterFace face)
    {
        SplitterFilterState state = GetSplitterFilterState(splitterPos);
        switch (face)
        {
            case SplitterFilterFace.Front:
                return state.front;

            case SplitterFilterFace.Back:
                return state.back;

            case SplitterFilterFace.Left:
                return state.left;

            case SplitterFilterFace.Right:
                return state.right;

            default:
                return false;
        }
    }

    public static SplitterFilterOutputInfo GetSplitterFilterOutputInfo(World world, Vector3Int splitterPos)
    {
        if (world == null ||
            !TryGetConveyorAt(world, splitterPos, out BlockType conveyorType) ||
            conveyorType != BlockType.conveyorBelt_splitter)
        {
            return default;
        }

        BlockPlacementAxis placementAxis = ResolveConveyorAxis(world, splitterPos, BlockType.conveyorBelt_splitter);
        Vector3Int placementForwardStep = ResolveHorizontalStep(GetForwardDirection(placementAxis));
        Vector3Int outputForwardStep = ResolveSplitterOutputForwardStep(world, splitterPos, placementForwardStep);
        SplitterOutputSet outputs = GetAvailableSplitterOutputs(world, splitterPos, outputForwardStep);
        Vector3Int leftStep = GetLeftStep(placementForwardStep);
        Vector3Int rightStep = -leftStep;

        return new SplitterFilterOutputInfo(
            outputs.Contains(placementForwardStep),
            outputs.Contains(-placementForwardStep),
            outputs.Contains(leftStep),
            outputs.Contains(rightStep));
    }

    public static bool IsConveyorBlock(BlockType blockType)
    {
        return blockType == BlockType.ConveyorBelt ||
               blockType == BlockType.conveyorBelt_splitter ||
               blockType == BlockType.conveyorBelt_45deg;
    }

    public static bool IsRegularConveyorBlock(BlockType blockType)
    {
        return blockType == BlockType.ConveyorBelt ||
               blockType == BlockType.conveyorBelt_45deg;
    }

    public static bool ShouldUseSlopedConveyor(World world, Vector3Int beltPos, BlockPlacementAxis placementAxis)
    {
        if (world == null)
            return false;

        BlockType currentType = world.GetBlockAt(beltPos);
        if (!IsRegularConveyorBlock(currentType))
            return false;

        Vector3 forward = GetForwardDirection(currentType, placementAxis);
        Vector3Int forwardStep = ResolveHorizontalStep(forward);
        if (forwardStep == Vector3Int.zero)
            return false;

        return TryGetConveyorAt(world, beltPos + forwardStep + Vector3Int.up, out _) ||
               TryGetConveyorAt(world, beltPos - forwardStep + Vector3Int.up, out _);
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
        Item carriedItem,
        out Vector3 conveyorVelocity)
    {
        BlockPlacementAxis placementAxis = ResolveConveyorAxis(world, splitterPos, BlockType.conveyorBelt_splitter);
        Vector3Int placementForwardStep = ResolveHorizontalStep(GetForwardDirection(placementAxis));
        Vector3Int forwardStep = ResolveSplitterOutputForwardStep(world, splitterPos, placementForwardStep);
        Vector3 forward = new Vector3(forwardStep.x, 0f, forwardStep.z);
        SplitterOutputSet outputs = GetAvailableSplitterOutputs(world, splitterPos, forwardStep);
        SplitterOutputSet routingOutputs = ResolveFilteredSplitterOutputs(splitterPos, placementForwardStep, outputs, carriedItem);

        if (routingOutputs.Count == 0)
        {
            conveyorVelocity = GetLaneCenteringVelocity(itemCenter, splitterPos, forward, centeringStrength, maxCenteringSpeed);
            return conveyorVelocity.sqrMagnitude > 0.0001f;
        }

        Vector3Int selectedStep = ResolveSplitterOutputStep(splitterPos, routingOutputs, itemRoutingKey);
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

    private static SplitterOutputSet ResolveFilteredSplitterOutputs(
        Vector3Int splitterPos,
        Vector3Int placementForwardStep,
        SplitterOutputSet outputs,
        Item carriedItem)
    {
        if (outputs.Count <= 0 || carriedItem == null)
            return outputs;

        if (!splitterFilterStates.TryGetValue(splitterPos, out SplitterFilterState state) ||
            state.filterItem == null ||
            !outputs.TryGetFiltered(state, placementForwardStep, out SplitterOutputSet filteredOutputs))
        {
            return outputs;
        }

        if (state.filterItem == carriedItem)
            return filteredOutputs;

        return outputs.Excluding(filteredOutputs);
    }

    private static SplitterFilterState GetSplitterFilterState(Vector3Int splitterPos)
    {
        if (splitterFilterStates.TryGetValue(splitterPos, out SplitterFilterState state))
            return state;

        state = new SplitterFilterState();
        splitterFilterStates[splitterPos] = state;
        return state;
    }

    private static void ClearSplitterRouteCache(Vector3Int splitterPos)
    {
        splitterNextOutputIndices.Remove(splitterPos);

        List<SplitterRouteKey> keysToRemove = null;
        foreach (SplitterRouteKey key in splitterRouteAssignments.Keys)
        {
            if (key.SplitterPos != splitterPos)
                continue;

            keysToRemove ??= new List<SplitterRouteKey>();
            keysToRemove.Add(key);
        }

        if (keysToRemove == null)
            return;

        for (int i = 0; i < keysToRemove.Count; i++)
            splitterRouteAssignments.Remove(keysToRemove[i]);
    }

    private static SplitterOutputSet GetAvailableSplitterOutputs(World world, Vector3Int splitterPos, Vector3Int forwardStep)
    {
        SplitterOutputSet outputs = default;
        Vector3Int leftStep = GetLeftStep(forwardStep);
        Vector3Int rightStep = -leftStep;
        Vector3Int backStep = -forwardStep;

        AddSplitterOutputIfConveyor(world, splitterPos, forwardStep, ref outputs);
        AddSplitterOutputIfConveyor(world, splitterPos, leftStep, ref outputs);
        AddSplitterOutputIfConveyor(world, splitterPos, rightStep, ref outputs);
        AddSplitterOutputIfConveyor(world, splitterPos, backStep, ref outputs);

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
        Vector3Int conveyorForwardStep = ResolveHorizontalStep(GetForwardDirection(conveyorType, axis));
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

    public static BlockPlacementAxis ResolveConveyorAxis(World world, Vector3Int beltPos, BlockType conveyorType)
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

    private static Vector3 ResolveConveyorTravelDirection(
        World world,
        Vector3Int beltPos,
        BlockType conveyorType,
        BlockPlacementAxis placementAxis,
        Vector3 forward)
    {
        if (conveyorType != BlockType.conveyorBelt_45deg)
            return forward;

        Vector3Int forwardStep = ResolveHorizontalStep(forward);
        if (forwardStep == Vector3Int.zero)
            return forward;

        if (TryGetConveyorAt(world, beltPos + forwardStep + Vector3Int.up, out _))
            return (forward + Vector3.up).normalized;

        if (TryGetConveyorAt(world, beltPos - forwardStep + Vector3Int.up, out _))
            return (forward + Vector3.down).normalized;

        return GetForwardDirection(conveyorType, placementAxis);
    }

    private static BlockPlacementAxis ResolveRampAxisFromStep(Vector3Int step)
    {
        if (Mathf.Abs(step.x) >= Mathf.Abs(step.z))
            return step.x >= 0 ? BlockPlacementAxis.X : BlockPlacementAxis.XNegative;

        return step.z >= 0 ? BlockPlacementAxis.Z : BlockPlacementAxis.ZNegative;
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
