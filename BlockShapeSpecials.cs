using UnityEngine;
using Unity.Collections;

public enum StairFacing : byte
{
    North = 0,
    East = 1,
    South = 2,
    West = 3
}

public enum StairShapeVariant : byte
{
    Straight = 0,
    InnerLeft = 1,
    InnerRight = 2,
    OuterLeft = 3,
    OuterRight = 4
}

public enum RampShapeVariant : byte
{
    Straight = 0,
    InnerLeft = 1,
    InnerRight = 2,
    OuterLeft = 3,
    OuterRight = 4
}

public enum RampEdge : byte
{
    Left = 0,
    Right = 1,
    Front = 2,
    Back = 3
}

[System.Serializable]
public struct ShapeBox
{
    public Vector3 min;
    public Vector3 max;

    public ShapeBox(Vector3 min, Vector3 max)
    {
        this.min = min;
        this.max = max;
    }

    public Bounds ToWorldBounds(Vector3Int blockPos)
    {
        Vector3 boundsMin = blockPos + min;
        Vector3 boundsMax = blockPos + max;
        Vector3 size = boundsMax - boundsMin;
        return new Bounds(boundsMin + size * 0.5f, size);
    }
}

public static class StairPlacementUtility
{
    public const byte FirstEncodedState = 32;
    public const byte LastEncodedState = 39;

    public static bool IsEncodedState(byte rawValue)
    {
        return rawValue >= FirstEncodedState && rawValue <= LastEncodedState;
    }

    public static BlockPlacementAxis ResolvePlacementCode(Vector3Int hitNormal, Vector3 lookForward, Vector3 hitPoint)
    {
        StairFacing facing = ResolveFacingFromLook(lookForward);
        bool placeTopHalf = ShouldPlaceTopHalf(hitNormal, hitPoint);
        return Encode(facing, placeTopHalf);
    }

    public static BlockPlacementAxis Encode(StairFacing facing, bool topHalf)
    {
        byte rawValue = (byte)(FirstEncodedState + (topHalf ? 4 : 0) + ((int)facing & 3));
        return (BlockPlacementAxis)rawValue;
    }

    public static bool TryDecode(byte rawValue, out StairFacing facing, out bool topHalf)
    {
        if (!IsEncodedState(rawValue))
        {
            facing = StairFacing.North;
            topHalf = false;
            return false;
        }

        int decoded = rawValue - FirstEncodedState;
        topHalf = decoded >= 4;
        facing = (StairFacing)(decoded & 3);
        return true;
    }

    public static bool TryDecode(BlockPlacementAxis rawValue, out StairFacing facing, out bool topHalf)
    {
        return TryDecode((byte)rawValue, out facing, out topHalf);
    }

    public static StairFacing ResolveFacingFromLook(Vector3 lookForward)
    {
        Vector3 horizontal = new Vector3(lookForward.x, 0f, lookForward.z);
        if (horizontal.sqrMagnitude <= 0.0001f)
            return StairFacing.North;

        horizontal.Normalize();
        if (Mathf.Abs(horizontal.z) >= Mathf.Abs(horizontal.x))
            return horizontal.z >= 0f ? StairFacing.North : StairFacing.South;

        return horizontal.x >= 0f ? StairFacing.East : StairFacing.West;
    }

    public static bool ShouldPlaceTopHalf(Vector3Int hitNormal, Vector3 hitPoint)
    {
        if (hitNormal.y < 0)
            return true;

        if (hitNormal.y > 0)
            return false;

        float localY = hitPoint.y - Mathf.Floor(hitPoint.y);
        return localY > 0.5f;
    }

    public static StairFacing RotateLeft(StairFacing facing)
    {
        return facing switch
        {
            StairFacing.North => StairFacing.West,
            StairFacing.West => StairFacing.South,
            StairFacing.South => StairFacing.East,
            _ => StairFacing.North
        };
    }

    public static StairFacing RotateRight(StairFacing facing)
    {
        return facing switch
        {
            StairFacing.North => StairFacing.East,
            StairFacing.East => StairFacing.South,
            StairFacing.South => StairFacing.West,
            _ => StairFacing.North
        };
    }

    public static StairFacing Opposite(StairFacing facing)
    {
        return facing switch
        {
            StairFacing.North => StairFacing.South,
            StairFacing.East => StairFacing.West,
            StairFacing.South => StairFacing.North,
            _ => StairFacing.East
        };
    }

    public static Vector3Int ToOffset(StairFacing facing)
    {
        return facing switch
        {
            StairFacing.North => Vector3Int.forward,
            StairFacing.East => Vector3Int.right,
            StairFacing.South => Vector3Int.back,
            _ => Vector3Int.left
        };
    }

    public static bool IsPerpendicular(StairFacing a, StairFacing b)
    {
        return a != b && a != Opposite(b);
    }
}

public static class StairShapeUtility
{
    public static StairShapeVariant ResolveShapeVariant(
        StairFacing currentFacing,
        bool currentTopHalf,
        bool hasFrontNeighbor,
        StairFacing frontFacing,
        bool frontTopHalf,
        bool hasBackNeighbor,
        StairFacing backFacing,
        bool backTopHalf)
    {
        if (hasFrontNeighbor &&
            frontTopHalf == currentTopHalf &&
            StairPlacementUtility.IsPerpendicular(currentFacing, frontFacing))
        {
            return frontFacing == StairPlacementUtility.RotateLeft(currentFacing)
                ? StairShapeVariant.OuterLeft
                : StairShapeVariant.OuterRight;
        }

        if (hasBackNeighbor &&
            backTopHalf == currentTopHalf &&
            StairPlacementUtility.IsPerpendicular(currentFacing, backFacing))
        {
            return backFacing == StairPlacementUtility.RotateLeft(currentFacing)
                ? StairShapeVariant.InnerLeft
                : StairShapeVariant.InnerRight;
        }

        return StairShapeVariant.Straight;
    }

    public static void ResolveBoxes(
        byte rawState,
        StairShapeVariant variant,
        out int boxCount,
        out ShapeBox box0,
        out ShapeBox box1,
        out ShapeBox box2,
        out ShapeBox box3,
        out ShapeBox box4)
    {
        if (!StairPlacementUtility.TryDecode(rawState, out StairFacing facing, out bool topHalf))
        {
            boxCount = 1;
            box0 = new ShapeBox(Vector3.zero, Vector3.one);
            box1 = default;
            box2 = default;
            box3 = default;
            box4 = default;
            return;
        }

        const float oneThird = 1f / 3f;
        const float twoThirds = 2f / 3f;

        boxCount = 0;
        box0 = default;
        box1 = default;
        box2 = default;
        box3 = default;
        box4 = default;

        AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, 0f, 0f, 0f, 1f, oneThird, 1f);

        switch (variant)
        {
            case StairShapeVariant.OuterLeft:
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, 0f, oneThird, oneThird, twoThirds, twoThirds, 1f);
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, 0f, twoThirds, twoThirds, oneThird, 1f, 1f);
                break;

            case StairShapeVariant.OuterRight:
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, oneThird, oneThird, oneThird, 1f, twoThirds, 1f);
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, twoThirds, twoThirds, twoThirds, 1f, 1f, 1f);
                break;

            case StairShapeVariant.InnerLeft:
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, 0f, oneThird, 0f, twoThirds, twoThirds, oneThird);
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, 0f, oneThird, oneThird, 1f, twoThirds, 1f);
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, 0f, twoThirds, 0f, oneThird, 1f, 1f);
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, oneThird, twoThirds, twoThirds, 1f, 1f, 1f);
                break;

            case StairShapeVariant.InnerRight:
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, oneThird, oneThird, 0f, 1f, twoThirds, oneThird);
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, 0f, oneThird, oneThird, 1f, twoThirds, 1f);
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, twoThirds, twoThirds, 0f, 1f, 1f, 1f);
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, 0f, twoThirds, twoThirds, twoThirds, 1f, 1f);
                break;

            default:
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, 0f, oneThird, oneThird, 1f, twoThirds, 1f);
                AddCanonicalBox(ref boxCount, ref box0, ref box1, ref box2, ref box3, ref box4, facing, topHalf, 0f, twoThirds, twoThirds, 1f, 1f, 1f);
                break;
        }
    }

    private static void AddCanonicalBox(
        ref int boxCount,
        ref ShapeBox box0,
        ref ShapeBox box1,
        ref ShapeBox box2,
        ref ShapeBox box3,
        ref ShapeBox box4,
        StairFacing facing,
        bool topHalf,
        float minX,
        float minY,
        float minZ,
        float maxX,
        float maxY,
        float maxZ)
    {
        ShapeBox rotated = RotateCanonicalBox(new ShapeBox(
            new Vector3(minX, minY, minZ),
            new Vector3(maxX, maxY, maxZ)),
            facing);

        if (topHalf)
            rotated = MirrorVertically(rotated);

        switch (boxCount)
        {
            case 0: box0 = rotated; break;
            case 1: box1 = rotated; break;
            case 2: box2 = rotated; break;
            case 3: box3 = rotated; break;
            default: box4 = rotated; break;
        }

        boxCount++;
    }

    private static ShapeBox MirrorVertically(ShapeBox box)
    {
        return new ShapeBox(
            new Vector3(box.min.x, 1f - box.max.y, box.min.z),
            new Vector3(box.max.x, 1f - box.min.y, box.max.z));
    }

    private static ShapeBox RotateCanonicalBox(ShapeBox box, StairFacing facing)
    {
        if (facing == StairFacing.North)
            return box;

        Vector2 a = RotateXZ(box.min.x, box.min.z, facing);
        Vector2 b = RotateXZ(box.max.x, box.min.z, facing);
        Vector2 c = RotateXZ(box.min.x, box.max.z, facing);
        Vector2 d = RotateXZ(box.max.x, box.max.z, facing);

        float minX = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x));
        float maxX = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
        float minZ = Mathf.Min(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y));
        float maxZ = Mathf.Max(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y));
        return new ShapeBox(
            new Vector3(minX, box.min.y, minZ),
            new Vector3(maxX, box.max.y, maxZ));
    }

    private static Vector2 RotateXZ(float x, float z, StairFacing facing)
    {
        return facing switch
        {
            // Canonical stairs sobe em +Z. East precisa girar +90 graus para subir em +X.
            StairFacing.East => new Vector2(z, 1f - x),
            StairFacing.South => new Vector2(1f - x, 1f - z),
            StairFacing.West => new Vector2(1f - z, x),
            _ => new Vector2(x, z)
        };
    }
}

public static class StairShapeRuntimeUtility
{
    public static StairShapeVariant ResolveShapeVariant(World world, Vector3Int blockPos, byte rawState)
    {
        if (world == null || !StairPlacementUtility.TryDecode(rawState, out StairFacing currentFacing, out bool topHalf))
            return StairShapeVariant.Straight;

        Vector3Int frontPos = blockPos + StairPlacementUtility.ToOffset(currentFacing);
        Vector3Int backPos = blockPos + StairPlacementUtility.ToOffset(StairPlacementUtility.Opposite(currentFacing));

        bool hasFrontNeighbor = TryGetNeighborState(world, frontPos, out StairFacing frontFacing, out bool frontTopHalf);
        bool hasBackNeighbor = TryGetNeighborState(world, backPos, out StairFacing backFacing, out bool backTopHalf);

        return StairShapeUtility.ResolveShapeVariant(
            currentFacing,
            topHalf,
            hasFrontNeighbor,
            frontFacing,
            frontTopHalf,
            hasBackNeighbor,
            backFacing,
            backTopHalf);
    }

    private static bool TryGetNeighborState(World world, Vector3Int blockPos, out StairFacing facing, out bool topHalf)
    {
        facing = StairFacing.North;
        topHalf = false;

        if (world == null || world.blockData == null)
            return false;

        BlockType blockType = world.GetBlockAt(blockPos);
        BlockTextureMapping? mappingResult = world.blockData.GetMapping(blockType);
        if (mappingResult == null || BlockShapeUtility.GetEffectiveRenderShape(mappingResult.Value) != BlockRenderShape.Stairs)
            return false;

        return StairPlacementUtility.TryDecode(world.GetPlacementAxisAt(blockPos, blockType), out facing, out topHalf);
    }
}

public static class RampShapeUtility
{
    public const int ColliderSliceCount = 4;
    private const float AmbientOcclusionSlopeInset = 0.125f;

    public const byte FirstEncodedState = 40;
    public const byte LastEncodedState = 47;

    public static bool IsEncodedState(byte rawValue)
    {
        return rawValue >= FirstEncodedState && rawValue <= LastEncodedState;
    }

    public static BlockPlacementAxis ResolvePlacementCode(Vector3Int hitNormal, Vector3 lookForward, Vector3 hitPoint)
    {
        StairFacing facing = StairPlacementUtility.ResolveFacingFromLook(lookForward);
        bool topHalf = StairPlacementUtility.ShouldPlaceTopHalf(hitNormal, hitPoint);
        return Encode(facing, topHalf);
    }

    public static BlockPlacementAxis Encode(StairFacing facing, bool topHalf)
    {
        byte rawValue = (byte)(FirstEncodedState + (topHalf ? 4 : 0) + ((int)facing & 3));
        return (BlockPlacementAxis)rawValue;
    }

    public static bool TryDecode(byte rawValue, out StairFacing facing, out bool topHalf)
    {
        if (IsEncodedState(rawValue))
        {
            int decoded = rawValue - FirstEncodedState;
            topHalf = decoded >= 4;
            facing = (StairFacing)(decoded & 3);
            return true;
        }

        topHalf = false;
        return TryGetFacingFromAxis((BlockPlacementAxis)rawValue, out facing);
    }

    public static bool TryDecode(BlockPlacementAxis rawValue, out StairFacing facing, out bool topHalf)
    {
        return TryDecode((byte)rawValue, out facing, out topHalf);
    }

    public static bool IsTopHalf(BlockPlacementAxis axis)
    {
        return TryDecode(axis, out _, out bool topHalf) && topHalf;
    }

    public static BlockPlacementAxis ResolvePlacementAxis(Vector3 lookForward)
    {
        Vector3 horizontal = new Vector3(lookForward.x, 0f, lookForward.z);
        if (horizontal.sqrMagnitude <= 0.0001f)
            return BlockPlacementAxis.Z;

        if (Mathf.Abs(horizontal.x) >= Mathf.Abs(horizontal.z))
            return horizontal.x >= 0f ? BlockPlacementAxis.X : BlockPlacementAxis.XNegative;

        return horizontal.z >= 0f ? BlockPlacementAxis.Z : BlockPlacementAxis.ZNegative;
    }

    public static BlockPlacementAxis SanitizeAxis(BlockPlacementAxis axis)
    {
        if (!TryDecode(axis, out StairFacing facing, out _))
            return BlockPlacementAxis.Z;

        return facing switch
        {
            StairFacing.East => BlockPlacementAxis.X,
            StairFacing.South => BlockPlacementAxis.ZNegative,
            StairFacing.West => BlockPlacementAxis.XNegative,
            _ => BlockPlacementAxis.Z
        };
    }

    public static bool TryGetFacing(BlockPlacementAxis axis, out StairFacing facing)
    {
        return TryDecode(axis, out facing, out _);
    }

    public static RampShapeVariant ResolveShapeVariant(
        StairFacing currentFacing,
        bool currentTopHalf,
        bool hasFrontNeighbor,
        StairFacing frontFacing,
        bool frontTopHalf,
        bool hasBackNeighbor,
        StairFacing backFacing,
        bool backTopHalf)
    {
        if (hasFrontNeighbor &&
            frontTopHalf == currentTopHalf &&
            StairPlacementUtility.IsPerpendicular(currentFacing, frontFacing))
        {
            return frontFacing == StairPlacementUtility.RotateLeft(currentFacing)
                ? RampShapeVariant.OuterLeft
                : RampShapeVariant.OuterRight;
        }

        if (hasBackNeighbor &&
            backTopHalf == currentTopHalf &&
            StairPlacementUtility.IsPerpendicular(currentFacing, backFacing))
        {
            return backFacing == StairPlacementUtility.RotateLeft(currentFacing)
                ? RampShapeVariant.InnerLeft
                : RampShapeVariant.InnerRight;
        }

        return RampShapeVariant.Straight;
    }

    public static RampShapeVariant ResolveShapeVariant(
        StairFacing currentFacing,
        bool hasFrontNeighbor,
        StairFacing frontFacing,
        bool hasBackNeighbor,
        StairFacing backFacing)
    {
        return ResolveShapeVariant(
            currentFacing,
            false,
            hasFrontNeighbor,
            frontFacing,
            false,
            hasBackNeighbor,
            backFacing,
            false);
    }

    public static void ResolveBottomQuad(
        BlockPlacementAxis axis,
        out Vector3 p0,
        out Vector3 p1,
        out Vector3 p2,
        out Vector3 p3)
    {
        bool topHalf = IsTopHalf(axis);
        axis = SanitizeAxis(axis);
        float y = topHalf ? 1f : 0f;
        p0 = RotateCanonicalPoint(new Vector3(0f, y, 0f), axis);
        p1 = RotateCanonicalPoint(new Vector3(1f, y, 0f), axis);
        p2 = RotateCanonicalPoint(new Vector3(1f, y, 1f), axis);
        p3 = RotateCanonicalPoint(new Vector3(0f, y, 1f), axis);
        EnsureFaceWinding(ref p0, ref p1, ref p2, ref p3, topHalf ? Vector3.up : Vector3.down);
    }

    public static void ResolveTopTriangles(
        BlockPlacementAxis axis,
        RampShapeVariant variant,
        out Vector3 tri0a,
        out Vector3 tri0b,
        out Vector3 tri0c,
        out Vector3 tri1a,
        out Vector3 tri1b,
        out Vector3 tri1c)
    {
        bool topHalf = IsTopHalf(axis);
        axis = SanitizeAxis(axis);
        Vector3 t00 = RotateCanonicalPoint(new Vector3(0f, ResolveCanonicalSlopeY(0f, 0f, variant, topHalf), 0f), axis);
        Vector3 t10 = RotateCanonicalPoint(new Vector3(1f, ResolveCanonicalSlopeY(1f, 0f, variant, topHalf), 0f), axis);
        Vector3 t01 = RotateCanonicalPoint(new Vector3(0f, ResolveCanonicalSlopeY(0f, 1f, variant, topHalf), 1f), axis);
        Vector3 t11 = RotateCanonicalPoint(new Vector3(1f, ResolveCanonicalSlopeY(1f, 1f, variant, topHalf), 1f), axis);

        bool useLeftDiagonal = variant == RampShapeVariant.OuterLeft || variant == RampShapeVariant.InnerLeft;
        if (useLeftDiagonal)
        {
            tri0a = t00;
            tri0b = t01;
            tri0c = t10;
            tri1a = t01;
            tri1b = t11;
            tri1c = t10;
        }
        else
        {
            tri0a = t00;
            tri0b = t01;
            tri0c = t11;
            tri1a = t00;
            tri1b = t11;
            tri1c = t10;
        }

        EnsureSlopeTriangleWinding(ref tri0a, ref tri0b, ref tri0c, topHalf);
        EnsureSlopeTriangleWinding(ref tri1a, ref tri1b, ref tri1c, topHalf);
    }

    public static bool ResolveEdgeSurface(
        BlockPlacementAxis axis,
        RampShapeVariant variant,
        RampEdge edge,
        out int vertexCount,
        out Vector3 p0,
        out Vector3 p1,
        out Vector3 p2,
        out Vector3 p3,
        out BlockFace sampledFace)
    {
        bool topHalf = IsTopHalf(axis);
        axis = SanitizeAxis(axis);
        ResolveCanonicalEdge(edge, variant, out Vector3 bottom0, out Vector3 bottom1, out float height0, out float height1);
        sampledFace = ResolveEdgeFace(axis, edge);

        const float epsilon = 0.0001f;
        if (height0 <= epsilon && height1 <= epsilon)
        {
            vertexCount = 0;
            p0 = default;
            p1 = default;
            p2 = default;
            p3 = default;
            return false;
        }

        Vector3 lower0 = new Vector3(bottom0.x, topHalf ? 1f - height0 : 0f, bottom0.z);
        Vector3 lower1 = new Vector3(bottom1.x, topHalf ? 1f - height1 : 0f, bottom1.z);
        Vector3 upper0 = new Vector3(bottom0.x, topHalf ? 1f : height0, bottom0.z);
        Vector3 upper1 = new Vector3(bottom1.x, topHalf ? 1f : height1, bottom1.z);

        if (height0 > epsilon && height1 > epsilon)
        {
            p0 = RotateCanonicalPoint(lower0, axis);
            p1 = RotateCanonicalPoint(lower1, axis);
            p2 = RotateCanonicalPoint(upper1, axis);
            p3 = RotateCanonicalPoint(upper0, axis);
            vertexCount = 4;
            EnsureFaceWinding(ref p0, ref p1, ref p2, ref p3, ResolveFaceNormal(sampledFace));
            return true;
        }

        if (height0 > epsilon)
        {
            p0 = RotateCanonicalPoint(topHalf ? lower0 : bottom0, axis);
            p1 = RotateCanonicalPoint(topHalf ? upper1 : bottom1, axis);
            p2 = RotateCanonicalPoint(upper0, axis);
        }
        else
        {
            p0 = RotateCanonicalPoint(topHalf ? upper0 : bottom0, axis);
            p1 = RotateCanonicalPoint(topHalf ? lower1 : bottom1, axis);
            p2 = RotateCanonicalPoint(upper1, axis);
        }

        p3 = default;
        vertexCount = 3;
        EnsureFaceWinding(ref p0, ref p1, ref p2, ResolveFaceNormal(sampledFace));
        return true;
    }

    public static float GetCanonicalSurfaceHeight(float x, float z, RampShapeVariant variant)
    {
        x = Mathf.Clamp01(x);
        z = Mathf.Clamp01(z);

        return variant switch
        {
            RampShapeVariant.OuterLeft => Mathf.Min(z, 1f - x),
            RampShapeVariant.OuterRight => Mathf.Min(z, x),
            RampShapeVariant.InnerLeft => Mathf.Max(z, 1f - x),
            RampShapeVariant.InnerRight => Mathf.Max(z, x),
            _ => z
        };
    }

    public static bool ContainsLocalPoint(Vector3 localPos, BlockPlacementAxis axis, RampShapeVariant variant)
    {
        const float epsilon = 0.0001f;
        if (localPos.x <= epsilon || localPos.x >= 1f - epsilon ||
            localPos.y <= epsilon || localPos.y >= 1f - epsilon ||
            localPos.z <= epsilon || localPos.z >= 1f - epsilon)
        {
            return false;
        }

        bool topHalf = IsTopHalf(axis);
        Vector3 canonical = ToCanonical(localPos, axis);
        float surfaceHeight = GetCanonicalSurfaceHeight(canonical.x, canonical.z, variant);
        return topHalf
            ? canonical.y > 1f - surfaceHeight + epsilon
            : canonical.y < surfaceHeight - epsilon;
    }

    public static bool ContainsAmbientOcclusionPoint(Vector3 localPos, BlockPlacementAxis axis, RampShapeVariant variant)
    {
        const float epsilon = 0.0001f;
        if (localPos.x <= epsilon || localPos.x >= 1f - epsilon ||
            localPos.y <= epsilon || localPos.y >= 1f - epsilon ||
            localPos.z <= epsilon || localPos.z >= 1f - epsilon)
        {
            return false;
        }

        bool topHalf = IsTopHalf(axis);
        Vector3 canonical = ToCanonical(localPos, axis);
        float surfaceHeight = GetCanonicalSurfaceHeight(canonical.x, canonical.z, variant);
        return topHalf
            ? canonical.y > 1f - surfaceHeight + AmbientOcclusionSlopeInset + epsilon
            : canonical.y < surfaceHeight - AmbientOcclusionSlopeInset - epsilon;
    }

    public static FixedList512Bytes<ShapeBox> BuildColliderBoxes(BlockPlacementAxis axis, RampShapeVariant variant)
    {
        bool topHalf = IsTopHalf(axis);
        axis = SanitizeAxis(axis);
        FixedList512Bytes<ShapeBox> boxes = default;
        const float epsilon = 0.0001f;
        float step = 1f / ColliderSliceCount;

        for (int zIndex = 0; zIndex < ColliderSliceCount; zIndex++)
        {
            float minZ = zIndex * step;
            float maxZ = minZ + step;
            int xIndex = 0;

            while (xIndex < ColliderSliceCount)
            {
                float minX = xIndex * step;
                float maxX = minX + step;
                float maxHeight = ResolveCellMaxHeight(minX, maxX, minZ, maxZ, variant);
                if (maxHeight <= epsilon)
                {
                    xIndex++;
                    continue;
                }

                int runEnd = xIndex + 1;
                while (runEnd < ColliderSliceCount)
                {
                    float nextMinX = runEnd * step;
                    float nextMaxX = nextMinX + step;
                    float nextHeight = ResolveCellMaxHeight(nextMinX, nextMaxX, minZ, maxZ, variant);
                    if (nextHeight <= epsilon || Mathf.Abs(nextHeight - maxHeight) > epsilon)
                        break;

                    runEnd++;
                }

                ShapeBox canonicalBox = new ShapeBox(
                    new Vector3(minX, topHalf ? 1f - maxHeight : 0f, minZ),
                    new Vector3(runEnd * step, topHalf ? 1f : maxHeight, maxZ));
                boxes.Add(RotateCanonicalBox(canonicalBox, axis));
                xIndex = runEnd;
            }
        }

        return boxes;
    }

    private static void ResolveCanonicalEdge(
        RampEdge edge,
        RampShapeVariant variant,
        out Vector3 bottom0,
        out Vector3 bottom1,
        out float height0,
        out float height1)
    {
        switch (edge)
        {
            case RampEdge.Left:
                bottom0 = new Vector3(0f, 0f, 0f);
                bottom1 = new Vector3(0f, 0f, 1f);
                height0 = GetCanonicalSurfaceHeight(0f, 0f, variant);
                height1 = GetCanonicalSurfaceHeight(0f, 1f, variant);
                return;

            case RampEdge.Right:
                bottom0 = new Vector3(1f, 0f, 0f);
                bottom1 = new Vector3(1f, 0f, 1f);
                height0 = GetCanonicalSurfaceHeight(1f, 0f, variant);
                height1 = GetCanonicalSurfaceHeight(1f, 1f, variant);
                return;

            case RampEdge.Back:
                bottom0 = new Vector3(0f, 0f, 0f);
                bottom1 = new Vector3(1f, 0f, 0f);
                height0 = GetCanonicalSurfaceHeight(0f, 0f, variant);
                height1 = GetCanonicalSurfaceHeight(1f, 0f, variant);
                return;

            default:
                bottom0 = new Vector3(0f, 0f, 1f);
                bottom1 = new Vector3(1f, 0f, 1f);
                height0 = GetCanonicalSurfaceHeight(0f, 1f, variant);
                height1 = GetCanonicalSurfaceHeight(1f, 1f, variant);
                return;
        }
    }

    private static BlockFace ResolveEdgeFace(BlockPlacementAxis axis, RampEdge edge)
    {
        axis = SanitizeAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.X => edge switch
            {
                RampEdge.Left => BlockFace.Front,
                RampEdge.Right => BlockFace.Back,
                RampEdge.Back => BlockFace.Left,
                _ => BlockFace.Right
            },
            BlockPlacementAxis.ZNegative => edge switch
            {
                RampEdge.Left => BlockFace.Right,
                RampEdge.Right => BlockFace.Left,
                RampEdge.Back => BlockFace.Front,
                _ => BlockFace.Back
            },
            BlockPlacementAxis.XNegative => edge switch
            {
                RampEdge.Left => BlockFace.Back,
                RampEdge.Right => BlockFace.Front,
                RampEdge.Back => BlockFace.Right,
                _ => BlockFace.Left
            },
            _ => edge switch
            {
                RampEdge.Left => BlockFace.Left,
                RampEdge.Right => BlockFace.Right,
                RampEdge.Back => BlockFace.Back,
                _ => BlockFace.Front
            }
        };
    }

    private static float ResolveCellMaxHeight(float minX, float maxX, float minZ, float maxZ, RampShapeVariant variant)
    {
        float h00 = GetCanonicalSurfaceHeight(minX, minZ, variant);
        float h10 = GetCanonicalSurfaceHeight(maxX, minZ, variant);
        float h01 = GetCanonicalSurfaceHeight(minX, maxZ, variant);
        float h11 = GetCanonicalSurfaceHeight(maxX, maxZ, variant);
        return Mathf.Max(Mathf.Max(h00, h10), Mathf.Max(h01, h11));
    }

    private static ShapeBox RotateCanonicalBox(ShapeBox box, BlockPlacementAxis axis)
    {
        Vector3 a = RotateCanonicalPoint(new Vector3(box.min.x, box.min.y, box.min.z), axis);
        Vector3 b = RotateCanonicalPoint(new Vector3(box.max.x, box.min.y, box.min.z), axis);
        Vector3 c = RotateCanonicalPoint(new Vector3(box.min.x, box.min.y, box.max.z), axis);
        Vector3 d = RotateCanonicalPoint(new Vector3(box.max.x, box.min.y, box.max.z), axis);

        float minX = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x));
        float maxX = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
        float minZ = Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z));
        float maxZ = Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z));
        return new ShapeBox(
            new Vector3(minX, box.min.y, minZ),
            new Vector3(maxX, box.max.y, maxZ));
    }

    private static Vector3 RotateCanonicalPoint(Vector3 point, BlockPlacementAxis axis)
    {
        axis = SanitizeAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.X => new Vector3(point.z, point.y, 1f - point.x),
            BlockPlacementAxis.ZNegative => new Vector3(1f - point.x, point.y, 1f - point.z),
            BlockPlacementAxis.XNegative => new Vector3(1f - point.z, point.y, point.x),
            _ => point
        };
    }

    private static bool TryGetFacingFromAxis(BlockPlacementAxis axis, out StairFacing facing)
    {
        switch (axis)
        {
            case BlockPlacementAxis.X:
                facing = StairFacing.East;
                return true;

            case BlockPlacementAxis.XNegative:
                facing = StairFacing.West;
                return true;

            case BlockPlacementAxis.ZNegative:
                facing = StairFacing.South;
                return true;

            default:
                facing = StairFacing.North;
                return true;
        }
    }

    private static float ResolveCanonicalSlopeY(float x, float z, RampShapeVariant variant, bool topHalf)
    {
        float surfaceHeight = GetCanonicalSurfaceHeight(x, z, variant);
        return topHalf ? 1f - surfaceHeight : surfaceHeight;
    }

    private static void EnsureSlopeTriangleWinding(ref Vector3 p0, ref Vector3 p1, ref Vector3 p2, bool topHalf)
    {
        float normalY = Vector3.Cross(p1 - p0, p2 - p0).y;
        if ((!topHalf && normalY >= 0f) || (topHalf && normalY <= 0f))
            return;

        (p1, p2) = (p2, p1);
    }

    private static void EnsureFaceWinding(ref Vector3 p0, ref Vector3 p1, ref Vector3 p2, Vector3 expectedNormal)
    {
        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), expectedNormal) >= 0f)
            return;

        (p1, p2) = (p2, p1);
    }

    private static void EnsureFaceWinding(ref Vector3 p0, ref Vector3 p1, ref Vector3 p2, ref Vector3 p3, Vector3 expectedNormal)
    {
        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), expectedNormal) >= 0f)
            return;

        (p1, p3) = (p3, p1);
    }

    private static Vector3 ResolveFaceNormal(BlockFace face)
    {
        return face switch
        {
            BlockFace.Right => Vector3.right,
            BlockFace.Left => Vector3.left,
            BlockFace.Top => Vector3.up,
            BlockFace.Bottom => Vector3.down,
            BlockFace.Front => Vector3.forward,
            _ => Vector3.back
        };
    }

    private static Vector3 ToCanonical(Vector3 point, BlockPlacementAxis axis)
    {
        axis = SanitizeAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.X => new Vector3(1f - point.z, point.y, point.x),
            BlockPlacementAxis.ZNegative => new Vector3(1f - point.x, point.y, 1f - point.z),
            BlockPlacementAxis.XNegative => new Vector3(point.z, point.y, 1f - point.x),
            _ => point
        };
    }
}

public static class RampShapeRuntimeUtility
{
    public static RampShapeVariant ResolveShapeVariant(World world, Vector3Int blockPos, BlockPlacementAxis placementAxis)
    {
        if (world == null || !RampShapeUtility.TryDecode(placementAxis, out StairFacing currentFacing, out bool currentTopHalf))
            return RampShapeVariant.Straight;

        Vector3Int frontPos = blockPos + StairPlacementUtility.ToOffset(currentFacing);
        Vector3Int backPos = blockPos + StairPlacementUtility.ToOffset(StairPlacementUtility.Opposite(currentFacing));

        bool hasFrontNeighbor = TryGetNeighborState(world, frontPos, out StairFacing frontFacing, out bool frontTopHalf);
        bool hasBackNeighbor = TryGetNeighborState(world, backPos, out StairFacing backFacing, out bool backTopHalf);

        return RampShapeUtility.ResolveShapeVariant(
            currentFacing,
            currentTopHalf,
            hasFrontNeighbor,
            frontFacing,
            frontTopHalf,
            hasBackNeighbor,
            backFacing,
            backTopHalf);
    }

    private static bool TryGetNeighborState(World world, Vector3Int blockPos, out StairFacing facing, out bool topHalf)
    {
        facing = StairFacing.North;
        topHalf = false;

        if (world == null || world.blockData == null)
            return false;

        BlockType blockType = world.GetBlockAt(blockPos);
        BlockTextureMapping? mappingResult = world.blockData.GetMapping(blockType);
        if (mappingResult == null || BlockShapeUtility.GetEffectiveRenderShape(mappingResult.Value) != BlockRenderShape.Ramp)
            return false;

        return RampShapeUtility.TryDecode(world.GetPlacementAxisAt(blockPos, blockType), out facing, out topHalf);
    }
}

public static class VerticalRampShapeUtility
{
    public const int ColliderSliceCount = 4;
    private const float AmbientOcclusionDiagonalInset = 0.125f;
    private static readonly Vector3 CanonicalSlopeNormal = new Vector3(1f, 0f, -1f).normalized;

    public static BlockPlacementAxis ResolvePlacementAxis(Vector3 lookForward)
    {
        return RampShapeUtility.ResolvePlacementAxis(lookForward);
    }

    public static BlockPlacementAxis SanitizeAxis(BlockPlacementAxis axis)
    {
        return RampShapeUtility.SanitizeAxis(axis);
    }

    public static void ResolveTopTriangle(
        BlockPlacementAxis axis,
        out Vector3 p0,
        out Vector3 p1,
        out Vector3 p2)
    {
        axis = SanitizeAxis(axis);
        p0 = RotateCanonicalPoint(new Vector3(0f, 1f, 0f), axis);
        p1 = RotateCanonicalPoint(new Vector3(0f, 1f, 1f), axis);
        p2 = RotateCanonicalPoint(new Vector3(1f, 1f, 1f), axis);
        EnsureTriangleWinding(ref p0, ref p1, ref p2, Vector3.up);
    }

    public static void ResolveBottomTriangle(
        BlockPlacementAxis axis,
        out Vector3 p0,
        out Vector3 p1,
        out Vector3 p2)
    {
        axis = SanitizeAxis(axis);
        p0 = RotateCanonicalPoint(new Vector3(0f, 0f, 0f), axis);
        p1 = RotateCanonicalPoint(new Vector3(1f, 0f, 1f), axis);
        p2 = RotateCanonicalPoint(new Vector3(0f, 0f, 1f), axis);
        EnsureTriangleWinding(ref p0, ref p1, ref p2, Vector3.down);
    }

    public static void ResolveSideQuad(
        BlockPlacementAxis axis,
        out Vector3 p0,
        out Vector3 p1,
        out Vector3 p2,
        out Vector3 p3,
        out BlockFace sampledFace)
    {
        axis = SanitizeAxis(axis);
        p0 = RotateCanonicalPoint(new Vector3(0f, 0f, 0f), axis);
        p1 = RotateCanonicalPoint(new Vector3(0f, 0f, 1f), axis);
        p2 = RotateCanonicalPoint(new Vector3(0f, 1f, 1f), axis);
        p3 = RotateCanonicalPoint(new Vector3(0f, 1f, 0f), axis);
        sampledFace = RotateHorizontalFace(axis, BlockFace.Left);
        EnsureQuadWinding(ref p0, ref p1, ref p2, ref p3, ResolveCardinalFaceNormal(sampledFace));
    }

    public static void ResolveFrontQuad(
        BlockPlacementAxis axis,
        out Vector3 p0,
        out Vector3 p1,
        out Vector3 p2,
        out Vector3 p3,
        out BlockFace sampledFace)
    {
        axis = SanitizeAxis(axis);
        p0 = RotateCanonicalPoint(new Vector3(0f, 0f, 1f), axis);
        p1 = RotateCanonicalPoint(new Vector3(1f, 0f, 1f), axis);
        p2 = RotateCanonicalPoint(new Vector3(1f, 1f, 1f), axis);
        p3 = RotateCanonicalPoint(new Vector3(0f, 1f, 1f), axis);
        sampledFace = RotateHorizontalFace(axis, BlockFace.Front);
        EnsureQuadWinding(ref p0, ref p1, ref p2, ref p3, ResolveCardinalFaceNormal(sampledFace));
    }

    public static void ResolveSlopeQuad(
        BlockPlacementAxis axis,
        out Vector3 p0,
        out Vector3 p1,
        out Vector3 p2,
        out Vector3 p3,
        out BlockFace sampledFace,
        out Vector3 normal)
    {
        axis = SanitizeAxis(axis);
        p0 = RotateCanonicalPoint(new Vector3(0f, 0f, 0f), axis);
        p1 = RotateCanonicalPoint(new Vector3(0f, 1f, 0f), axis);
        p2 = RotateCanonicalPoint(new Vector3(1f, 1f, 1f), axis);
        p3 = RotateCanonicalPoint(new Vector3(1f, 0f, 1f), axis);
        normal = RotateCanonicalVector(CanonicalSlopeNormal, axis).normalized;
        sampledFace = ResolveSlopeSampledFace(normal);
        EnsureQuadWinding(ref p0, ref p1, ref p2, ref p3, normal);
    }

    public static bool ContainsLocalPoint(Vector3 localPos, BlockPlacementAxis axis)
    {
        const float epsilon = 0.0001f;
        if (localPos.x <= epsilon || localPos.x >= 1f - epsilon ||
            localPos.y <= epsilon || localPos.y >= 1f - epsilon ||
            localPos.z <= epsilon || localPos.z >= 1f - epsilon)
        {
            return false;
        }

        Vector3 canonical = ToCanonical(localPos, axis);
        return canonical.x < canonical.z - epsilon;
    }

    public static bool ContainsAmbientOcclusionPoint(Vector3 localPos, BlockPlacementAxis axis)
    {
        const float epsilon = 0.0001f;
        if (localPos.x <= epsilon || localPos.x >= 1f - epsilon ||
            localPos.y <= epsilon || localPos.y >= 1f - epsilon ||
            localPos.z <= epsilon || localPos.z >= 1f - epsilon)
        {
            return false;
        }

        Vector3 canonical = ToCanonical(localPos, axis);
        return canonical.x < canonical.z - AmbientOcclusionDiagonalInset - epsilon;
    }

    public static FixedList512Bytes<ShapeBox> BuildColliderBoxes(BlockPlacementAxis axis)
    {
        axis = SanitizeAxis(axis);
        FixedList512Bytes<ShapeBox> boxes = default;
        float step = 1f / ColliderSliceCount;

        for (int zIndex = 0; zIndex < ColliderSliceCount; zIndex++)
        {
            float minZ = zIndex * step;
            float maxZ = minZ + step;
            boxes.Add(RotateCanonicalBox(
                new ShapeBox(
                    new Vector3(0f, 0f, minZ),
                    new Vector3(maxZ, 1f, maxZ)),
                axis));
        }

        return boxes;
    }

    private static BlockFace RotateHorizontalFace(BlockPlacementAxis axis, BlockFace face)
    {
        axis = SanitizeAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.X => face switch
            {
                BlockFace.Left => BlockFace.Front,
                BlockFace.Front => BlockFace.Right,
                BlockFace.Right => BlockFace.Back,
                BlockFace.Back => BlockFace.Left,
                _ => face
            },
            BlockPlacementAxis.ZNegative => face switch
            {
                BlockFace.Left => BlockFace.Right,
                BlockFace.Front => BlockFace.Back,
                BlockFace.Right => BlockFace.Left,
                BlockFace.Back => BlockFace.Front,
                _ => face
            },
            BlockPlacementAxis.XNegative => face switch
            {
                BlockFace.Left => BlockFace.Back,
                BlockFace.Front => BlockFace.Left,
                BlockFace.Right => BlockFace.Front,
                BlockFace.Back => BlockFace.Right,
                _ => face
            },
            _ => face
        };
    }

    private static BlockFace ResolveSlopeSampledFace(Vector3 normal)
    {
        if (Mathf.Abs(normal.x) >= Mathf.Abs(normal.z))
            return normal.x >= 0f ? BlockFace.Right : BlockFace.Left;

        return normal.z >= 0f ? BlockFace.Front : BlockFace.Back;
    }

    private static Vector3 ResolveCardinalFaceNormal(BlockFace face)
    {
        return face switch
        {
            BlockFace.Right => Vector3.right,
            BlockFace.Left => Vector3.left,
            BlockFace.Top => Vector3.up,
            BlockFace.Bottom => Vector3.down,
            BlockFace.Front => Vector3.forward,
            _ => Vector3.back
        };
    }

    private static ShapeBox RotateCanonicalBox(ShapeBox box, BlockPlacementAxis axis)
    {
        Vector3 a = RotateCanonicalPoint(new Vector3(box.min.x, box.min.y, box.min.z), axis);
        Vector3 b = RotateCanonicalPoint(new Vector3(box.max.x, box.min.y, box.min.z), axis);
        Vector3 c = RotateCanonicalPoint(new Vector3(box.min.x, box.min.y, box.max.z), axis);
        Vector3 d = RotateCanonicalPoint(new Vector3(box.max.x, box.min.y, box.max.z), axis);

        float minX = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x));
        float maxX = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
        float minZ = Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z));
        float maxZ = Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z));
        return new ShapeBox(
            new Vector3(minX, box.min.y, minZ),
            new Vector3(maxX, box.max.y, maxZ));
    }

    private static Vector3 RotateCanonicalPoint(Vector3 point, BlockPlacementAxis axis)
    {
        axis = SanitizeAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.X => new Vector3(point.z, point.y, 1f - point.x),
            BlockPlacementAxis.ZNegative => new Vector3(1f - point.x, point.y, 1f - point.z),
            BlockPlacementAxis.XNegative => new Vector3(1f - point.z, point.y, point.x),
            _ => point
        };
    }

    private static Vector3 RotateCanonicalVector(Vector3 vector, BlockPlacementAxis axis)
    {
        axis = SanitizeAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.X => new Vector3(vector.z, vector.y, -vector.x),
            BlockPlacementAxis.ZNegative => new Vector3(-vector.x, vector.y, -vector.z),
            BlockPlacementAxis.XNegative => new Vector3(-vector.z, vector.y, vector.x),
            _ => vector
        };
    }

    private static Vector3 ToCanonical(Vector3 point, BlockPlacementAxis axis)
    {
        axis = SanitizeAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.X => new Vector3(1f - point.z, point.y, point.x),
            BlockPlacementAxis.ZNegative => new Vector3(1f - point.x, point.y, 1f - point.z),
            BlockPlacementAxis.XNegative => new Vector3(point.z, point.y, 1f - point.x),
            _ => point
        };
    }

    private static void EnsureTriangleWinding(ref Vector3 p0, ref Vector3 p1, ref Vector3 p2, Vector3 expectedNormal)
    {
        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), expectedNormal) >= 0f)
            return;

        (p1, p2) = (p2, p1);
    }

    private static void EnsureQuadWinding(ref Vector3 p0, ref Vector3 p1, ref Vector3 p2, ref Vector3 p3, Vector3 expectedNormal)
    {
        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), expectedNormal) >= 0f)
            return;

        (p1, p3) = (p3, p1);
    }
}

public static class FenceShapeUtility
{
    public const byte ConnectWest = 1 << 0;
    public const byte ConnectEast = 1 << 1;
    public const byte ConnectSouth = 1 << 2;
    public const byte ConnectNorth = 1 << 3;

    public static bool IsFenceConnectionActive(byte connectionMask, byte flag)
    {
        return (connectionMask & flag) != 0;
    }

    public static byte ResolveConnectionMask(World world, Vector3Int blockPos)
    {
        byte mask = 0;
        if (CanConnect(world, blockPos + Vector3Int.left)) mask |= ConnectWest;
        if (CanConnect(world, blockPos + Vector3Int.right)) mask |= ConnectEast;
        if (CanConnect(world, blockPos + Vector3Int.back)) mask |= ConnectSouth;
        if (CanConnect(world, blockPos + Vector3Int.forward)) mask |= ConnectNorth;
        return mask;
    }

    public static byte ResolveConnectionMask(
        int voxelX,
        int voxelY,
        int voxelZ,
        NativeArray<byte> blockTypes,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        byte mask = 0;
        if (CanConnect(voxelX - 1, voxelY, voxelZ, blockTypes, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= ConnectWest;
        if (CanConnect(voxelX + 1, voxelY, voxelZ, blockTypes, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= ConnectEast;
        if (CanConnect(voxelX, voxelY, voxelZ - 1, blockTypes, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= ConnectSouth;
        if (CanConnect(voxelX, voxelY, voxelZ + 1, blockTypes, blockMappings, voxelSizeX, voxelSizeZ, voxelPlaneSize)) mask |= ConnectNorth;
        return mask;
    }

    public static ShapeBox GetCenterPostVisualBox()
    {
        return new ShapeBox(new Vector3(0.375f, 0f, 0.375f), new Vector3(0.625f, 1f, 0.625f));
    }

    public static ShapeBox GetRailVisualBox(byte directionFlag, bool upperRail)
    {
        float minY = upperRail ? 0.75f : 0.375f;
        float maxY = upperRail ? 0.9375f : 0.5625f;
        return directionFlag switch
        {
            ConnectWest => new ShapeBox(new Vector3(0f, minY, 0.4375f), new Vector3(0.625f, maxY, 0.5625f)),
            ConnectEast => new ShapeBox(new Vector3(0.375f, minY, 0.4375f), new Vector3(1f, maxY, 0.5625f)),
            ConnectSouth => new ShapeBox(new Vector3(0.4375f, minY, 0f), new Vector3(0.5625f, maxY, 0.625f)),
            _ => new ShapeBox(new Vector3(0.4375f, minY, 0.375f), new Vector3(0.5625f, maxY, 1f))
        };
    }

    public static ShapeBox GetSingleRailVisualBox(byte directionFlag)
    {
        return directionFlag switch
        {
            ConnectWest => new ShapeBox(new Vector3(0f, 0f, 0.375f), new Vector3(0.625f, 1f, 0.625f)),
            ConnectEast => new ShapeBox(new Vector3(0.375f, 0f, 0.375f), new Vector3(1f, 1f, 0.625f)),
            ConnectSouth => new ShapeBox(new Vector3(0.375f, 0f, 0f), new Vector3(0.625f, 1f, 0.625f)),
            _ => new ShapeBox(new Vector3(0.375f, 0f, 0.375f), new Vector3(0.625f, 1f, 1f))
        };
    }

    public static ShapeBox GetCenterPostColliderBox()
    {
        return new ShapeBox(new Vector3(0.375f, 0f, 0.375f), new Vector3(0.625f, 1.5f, 0.625f));
    }

    public static ShapeBox GetArmColliderBox(byte directionFlag)
    {
        return directionFlag switch
        {
            ConnectWest => new ShapeBox(new Vector3(0f, 0f, 0.375f), new Vector3(0.625f, 1.5f, 0.625f)),
            ConnectEast => new ShapeBox(new Vector3(0.375f, 0f, 0.375f), new Vector3(1f, 1.5f, 0.625f)),
            ConnectSouth => new ShapeBox(new Vector3(0.375f, 0f, 0f), new Vector3(0.625f, 1.5f, 0.625f)),
            _ => new ShapeBox(new Vector3(0.375f, 0f, 0.375f), new Vector3(0.625f, 1.5f, 1f))
        };
    }

    private static bool CanConnect(World world, Vector3Int neighborPos)
    {
        if (world == null || world.blockData == null)
            return false;

        BlockType neighborType = world.GetBlockAt(neighborPos);
        if (neighborType == BlockType.Air || FluidBlockUtility.IsWater(neighborType))
            return false;

        BlockTextureMapping? mappingResult = world.blockData.GetMapping(neighborType);
        if (mappingResult == null)
            return false;

        return CanConnect(neighborType, mappingResult.Value);
    }

    private static bool CanConnect(
        int x,
        int y,
        int z,
        NativeArray<byte> blockTypes,
        NativeArray<BlockTextureMapping> blockMappings,
        int voxelSizeX,
        int voxelSizeZ,
        int voxelPlaneSize)
    {
        if (x < 0 || x >= voxelSizeX || y < 0 || y >= Chunk.SizeY || z < 0 || z >= voxelSizeZ)
            return false;

        int idx = x + y * voxelSizeX + z * voxelPlaneSize;
        BlockType neighborType = (BlockType)blockTypes[idx];
        if (neighborType == BlockType.Air || FluidBlockUtility.IsWater(neighborType))
            return false;

        int mapIndex = (int)neighborType;
        if ((uint)mapIndex >= (uint)blockMappings.Length)
            return false;

        return CanConnect(neighborType, blockMappings[mapIndex]);
    }

    private static bool CanConnect(BlockType neighborType, BlockTextureMapping mapping)
    {
        BlockRenderShape shape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
        if (shape == BlockRenderShape.Fence || shape == BlockRenderShape.Fence2)
            return true;

        return shape == BlockRenderShape.Cube &&
               mapping.isSolid &&
               !mapping.isEmpty &&
               !mapping.isLiquid;
    }
}

public static class SlabShapeUtility
{
    public static BlockPlacementAxis ResolvePlacementCode(Vector3Int hitNormal, Vector3 hitPoint)
    {
        if (hitNormal.y < 0)
            return BlockPlacementAxis.YNegative;

        if (hitNormal.y > 0)
            return BlockPlacementAxis.Y;

        float localY = hitPoint.y - Mathf.Floor(hitPoint.y);
        return localY > 0.5f ? BlockPlacementAxis.YNegative : BlockPlacementAxis.Y;
    }

    public static bool IsTopHalf(BlockPlacementAxis placementAxis)
    {
        return (byte)placementAxis == (byte)BlockPlacementAxis.YNegative;
    }

    public static ShapeBox GetVisualBox(BlockPlacementAxis placementAxis)
    {
        return IsTopHalf(placementAxis)
            ? new ShapeBox(new Vector3(0f, 0.5f, 0f), Vector3.one)
            : new ShapeBox(Vector3.zero, new Vector3(1f, 0.5f, 1f));
    }

    public static bool TryGetDoubleSlabBlockType(BlockType slabType, out BlockType fullBlockType)
    {
        switch (slabType)
        {
            case BlockType.oakPlanks_slab:
                fullBlockType = BlockType.oak_planks;
                return true;

            default:
                fullBlockType = BlockType.Air;
                return false;
        }
    }
}

public static class BlockSupportSurfaceUtility
{
    public static Vector3 GetSurfaceAlignedWorldOffset(
        World world,
        Vector3Int blockPos,
        BlockType blockType,
        BlockPlacementAxis placementAxis)
    {
        if (world == null || blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
            return Vector3.zero;

        if (TorchPlacementUtility.IsWallTorch(blockType))
            return GetWallTorchSurfaceOffset(world, blockPos, blockType);

        BlockPlacementAxis supportAxis = placementAxis;
        if (world.blockData != null)
        {
            BlockTextureMapping? mapping = world.blockData.GetMapping(blockType);
            if (mapping != null)
                supportAxis = ResolveSurfaceSupportAxis(blockType, mapping.Value, placementAxis);
            else
                supportAxis = ResolveSurfaceSupportAxis(blockType, placementAxis);
        }
        else
        {
            supportAxis = ResolveSurfaceSupportAxis(blockType, placementAxis);
        }

        return GetSurfaceAlignedWorldOffset(world, blockPos, supportAxis);
    }

    public static Vector3 GetSurfaceAlignedWorldOffset(
        World world,
        Vector3Int blockPos,
        BlockType blockType,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis)
    {
        if (world == null || blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
            return Vector3.zero;

        if (TorchPlacementUtility.IsWallTorch(blockType))
            return GetWallTorchSurfaceOffset(world, blockPos, blockType);

        return GetSurfaceAlignedWorldOffset(world, blockPos, ResolveSurfaceSupportAxis(blockType, mapping, placementAxis));
    }

    public static BlockPlacementAxis ResolveSurfaceSupportAxis(BlockType blockType, BlockPlacementAxis placementAxis)
    {
        switch (blockType)
        {
            case BlockType.ConveyorBelt:
            case BlockType.conveyorBelt_splitter:
            case BlockType.SolarPanel:
                return BlockPlacementAxis.Y;

            default:
                return placementAxis;
        }
    }

    public static BlockPlacementAxis ResolveSurfaceSupportAxis(
        BlockType blockType,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis)
    {
        switch (blockType)
        {
            case BlockType.ConveyorBelt:
            case BlockType.conveyorBelt_splitter:
            case BlockType.SolarPanel:
                return BlockPlacementAxis.Y;
        }

        if (mapping.usePlacementAxisRotation &&
            mapping.placementRotationAxes == BlockPlacementRotationAxes.Horizontal)
        {
            return BlockPlacementAxis.Y;
        }

        return ResolveSurfaceSupportAxis(blockType, placementAxis);
    }

    public static Vector3 GetSurfaceAlignedWorldOffset(
        World world,
        Vector3Int blockPos,
        BlockPlacementAxis placementAxis)
    {
        if (world == null)
            return Vector3.zero;

        Vector3Int supportPos = GetSupportPosition(blockPos, placementAxis);
        if (!TryGetVisualBounds(world, supportPos, out Bounds supportBounds))
            return Vector3.zero;

        Vector3 offset = Vector3.zero;
        switch (placementAxis)
        {
            case BlockPlacementAxis.YNegative:
                offset.y = supportBounds.min.y - (blockPos.y + 1f);
                break;

            case BlockPlacementAxis.XNegative:
                offset.x = supportBounds.max.x - blockPos.x;
                offset.y = supportBounds.center.y - (blockPos.y + 0.5f);
                break;

            case BlockPlacementAxis.X:
                offset.x = supportBounds.min.x - (blockPos.x + 1f);
                offset.y = supportBounds.center.y - (blockPos.y + 0.5f);
                break;

            case BlockPlacementAxis.ZNegative:
                offset.z = supportBounds.max.z - blockPos.z;
                offset.y = supportBounds.center.y - (blockPos.y + 0.5f);
                break;

            case BlockPlacementAxis.Z:
                offset.z = supportBounds.min.z - (blockPos.z + 1f);
                offset.y = supportBounds.center.y - (blockPos.y + 0.5f);
                break;

            default:
                offset.y = Mathf.Min(0f, supportBounds.max.y - blockPos.y);
                break;
        }

        return offset;
    }

    public static Vector3 GetTorchVisualWorldOffset(World world, Vector3Int blockPos, BlockType torchType)
    {
        if (torchType == BlockType.torch)
            return GetSurfaceAlignedWorldOffset(world, blockPos, BlockPlacementAxis.Y);

        if (TorchPlacementUtility.IsWallTorch(torchType))
            return GetWallTorchSurfaceOffset(world, blockPos, torchType);

        return Vector3.zero;
    }

    private static Vector3Int GetSupportPosition(Vector3Int blockPos, BlockPlacementAxis placementAxis)
    {
        return placementAxis switch
        {
            BlockPlacementAxis.YNegative => blockPos + Vector3Int.up,
            BlockPlacementAxis.XNegative => blockPos + Vector3Int.left,
            BlockPlacementAxis.X => blockPos + Vector3Int.right,
            BlockPlacementAxis.ZNegative => blockPos + Vector3Int.back,
            BlockPlacementAxis.Z => blockPos + Vector3Int.forward,
            _ => blockPos + Vector3Int.down
        };
    }

    private static Vector3 GetWallTorchSurfaceOffset(World world, Vector3Int blockPos, BlockType torchType)
    {
        if (world == null)
            return Vector3.zero;

        Vector3Int wallNormal = TorchPlacementUtility.GetWallNormal(torchType);
        if (wallNormal == Vector3Int.zero)
            return Vector3.zero;

        Vector3Int supportPos = blockPos - wallNormal;
        if (!TryGetVisualBounds(world, supportPos, out Bounds supportBounds))
            return Vector3.zero;

        Vector3 offset = Vector3.zero;
        if (wallNormal.x > 0)
            offset.x = supportBounds.max.x - blockPos.x;
        else if (wallNormal.x < 0)
            offset.x = supportBounds.min.x - (blockPos.x + 1f);
        else if (wallNormal.z > 0)
            offset.z = supportBounds.max.z - blockPos.z;
        else if (wallNormal.z < 0)
            offset.z = supportBounds.min.z - (blockPos.z + 1f);

        offset.y = supportBounds.center.y - (blockPos.y + 0.5f);
        return offset;
    }

    private static bool TryGetVisualBounds(World world, Vector3Int blockPos, out Bounds bounds)
    {
        bounds = default;
        if (world == null || world.blockData == null)
            return false;

        BlockType blockType = world.GetBlockAt(blockPos);
        if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
            return false;

        BlockTextureMapping? mappingResult = world.blockData.GetMapping(blockType);
        if (mappingResult == null)
            return false;

        BlockTextureMapping mapping = mappingResult.Value;
        switch (BlockShapeUtility.GetEffectiveRenderShape(mapping))
        {
            case BlockRenderShape.Slab:
                bounds = SlabShapeUtility.GetVisualBox(world.GetPlacementAxisAt(blockPos, blockType)).ToWorldBounds(blockPos);
                return true;

            case BlockRenderShape.Fence:
                bounds = ResolveFenceVisualBounds(world, blockPos, false);
                return true;

            case BlockRenderShape.Fence2:
                bounds = ResolveFenceVisualBounds(world, blockPos, true);
                return true;

            case BlockRenderShape.Cuboid:
                bounds = BlockShapeUtility.GetWorldBounds(blockPos, blockType, mapping, world.GetPlacementAxisAt(blockPos, blockType));
                return true;

            case BlockRenderShape.MultiCuboid:
                if (BlockShapeUtility.TryGetMultiCuboidBounds(
                    blockPos,
                    mapping,
                    world.blockData.runtimeMultiCuboidBoxes,
                    world.GetPlacementAxisAt(blockPos, blockType),
                    blockType,
                    out bounds))
                {
                    return true;
                }

                bounds = BlockShapeUtility.GetWorldBounds(blockPos, blockType, mapping, world.GetPlacementAxisAt(blockPos, blockType));
                return true;

            case BlockRenderShape.Cube:
                bounds = new Bounds(blockPos + Vector3.one * 0.5f, Vector3.one);
                return true;

            default:
                bounds = BlockShapeUtility.GetWorldBounds(blockPos, blockType, mapping, world.GetPlacementAxisAt(blockPos, blockType));
                return true;
        }
    }

    private static Bounds ResolveFenceVisualBounds(World world, Vector3Int blockPos, bool singleRail)
    {
        byte connectionMask = FenceShapeUtility.ResolveConnectionMask(world, blockPos);
        Bounds bounds = FenceShapeUtility.GetCenterPostVisualBox().ToWorldBounds(blockPos);
        EncapsulateFenceRail(ref bounds, blockPos, connectionMask, FenceShapeUtility.ConnectWest, singleRail);
        EncapsulateFenceRail(ref bounds, blockPos, connectionMask, FenceShapeUtility.ConnectEast, singleRail);
        EncapsulateFenceRail(ref bounds, blockPos, connectionMask, FenceShapeUtility.ConnectSouth, singleRail);
        EncapsulateFenceRail(ref bounds, blockPos, connectionMask, FenceShapeUtility.ConnectNorth, singleRail);
        return bounds;
    }

    private static void EncapsulateFenceRail(ref Bounds bounds, Vector3Int blockPos, byte connectionMask, byte directionFlag, bool singleRail)
    {
        if (!FenceShapeUtility.IsFenceConnectionActive(connectionMask, directionFlag))
            return;

        if (singleRail)
        {
            bounds.Encapsulate(FenceShapeUtility.GetSingleRailVisualBox(directionFlag).ToWorldBounds(blockPos));
            return;
        }

        bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(directionFlag, false).ToWorldBounds(blockPos));
        bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(directionFlag, true).ToWorldBounds(blockPos));
    }
}
