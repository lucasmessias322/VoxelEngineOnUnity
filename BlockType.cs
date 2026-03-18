using UnityEngine;

public enum BlockType
{
    Air = 0,
    Grass = 1,
    Dirt = 2,
    Stone = 3,
    Sand = 4,
    Water = 5,
    Bedrock = 6,
    Leaves = 7,
    Log = 8,
    Deepslate = 9,
    Snow = 10,
    glass = 11,
    glowstone = 12,
    oak_planks = 13,
    short_grass4 = 14,
    birch_log = 15,
    Crafter = 16,
    CoalOre = 17,
    IronOre = 18,
    GoldOre = 19,
    RedstoneOre = 20,
    DiamondOre = 21,
    EmeraldOre = 22,
    Cactus = 23,
    acacia_log = 24,
    torch = 25,
    WallTorchEast = 26,
    WallTorchWest = 27,
    WallTorchSouth = 28,
    WallTorchNorth = 29,
}

public static class TorchPlacementUtility
{
    public const float WallAngleDegrees = 22.5f;
    public const float WallAnchorHeight = 0.22f;
    public const float WallAnchorOffset = 0.32f;

    public static bool IsTorchLike(BlockType type)
    {
        return type == BlockType.glowstone ||
               type == BlockType.torch ||
               IsWallTorch(type);
    }

    public static bool IsWallTorch(BlockType type)
    {
        return type == BlockType.WallTorchEast ||
               type == BlockType.WallTorchWest ||
               type == BlockType.WallTorchSouth ||
               type == BlockType.WallTorchNorth;
    }

    public static BlockType GetStandingTorchType(BlockType type)
    {
        if (type == BlockType.torch)
            return BlockType.torch;

        return BlockType.glowstone;
    }

    public static BlockType GetPlacementBlockType(BlockType selectedBlockType, Vector3Int hitNormal)
    {
        if (!IsTorchLike(selectedBlockType))
            return selectedBlockType;

        if (TryGetWallVariantFromNormal(hitNormal, out BlockType wallTorchType))
            return wallTorchType;

        return GetStandingTorchType(selectedBlockType);
    }

    public static BlockType GetInventoryDropBlockType(BlockType type)
    {
        if (!IsTorchLike(type))
            return type;

        if (type == BlockType.torch)
            return BlockType.torch;

        return BlockType.glowstone;
    }

    public static Vector3Int GetSupportDirection(BlockType type)
    {
        if (!IsWallTorch(type))
            return Vector3Int.down;

        return -GetWallNormal(type);
    }

    public static Vector3Int GetWallNormal(BlockType type)
    {
        switch (type)
        {
            case BlockType.WallTorchEast:
                return Vector3Int.right;

            case BlockType.WallTorchWest:
                return Vector3Int.left;

            case BlockType.WallTorchSouth:
                return Vector3Int.forward;

            case BlockType.WallTorchNorth:
                return Vector3Int.back;

            default:
                return Vector3Int.zero;
        }
    }

    public static bool TryGetWallVariantFromNormal(Vector3Int hitNormal, out BlockType wallTorchType)
    {
        if (hitNormal == Vector3Int.right)
        {
            wallTorchType = BlockType.WallTorchEast;
            return true;
        }

        if (hitNormal == Vector3Int.left)
        {
            wallTorchType = BlockType.WallTorchWest;
            return true;
        }

        if (hitNormal == Vector3Int.forward)
        {
            wallTorchType = BlockType.WallTorchSouth;
            return true;
        }

        if (hitNormal == Vector3Int.back)
        {
            wallTorchType = BlockType.WallTorchNorth;
            return true;
        }

        wallTorchType = BlockType.Air;
        return false;
    }

    public static Vector3 TransformModelPoint(BlockType type, Vector3 modelPoint)
    {
        if (!IsWallTorch(type))
            return modelPoint + new Vector3(0.5f, 0f, 0.5f);

        Vector3Int wallNormal = GetWallNormal(type);
        float angleRadians = WallAngleDegrees * Mathf.Deg2Rad;
        float sin = Mathf.Sin(angleRadians);
        float cos = Mathf.Cos(angleRadians);

        Vector3 rotated = modelPoint;
        if (wallNormal.x > 0)
        {
            rotated = new Vector3(
                modelPoint.x * cos + modelPoint.y * sin,
                -modelPoint.x * sin + modelPoint.y * cos,
                modelPoint.z);
        }
        else if (wallNormal.x < 0)
        {
            rotated = new Vector3(
                modelPoint.x * cos - modelPoint.y * sin,
                modelPoint.x * sin + modelPoint.y * cos,
                modelPoint.z);
        }
        else if (wallNormal.z > 0)
        {
            rotated = new Vector3(
                modelPoint.x,
                modelPoint.y * cos - modelPoint.z * sin,
                modelPoint.y * sin + modelPoint.z * cos);
        }
        else if (wallNormal.z < 0)
        {
            rotated = new Vector3(
                modelPoint.x,
                modelPoint.y * cos + modelPoint.z * sin,
                -modelPoint.y * sin + modelPoint.z * cos);
        }

        Vector3 anchor = new Vector3(
            0.5f - wallNormal.x * WallAnchorOffset,
            WallAnchorHeight,
            0.5f - wallNormal.z * WallAnchorOffset);

        return anchor + rotated;
    }

    public static Bounds GetWorldBounds(Vector3Int blockPos, BlockType type, BlockTextureMapping mapping)
    {
        BlockShapeUtility.ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);
        if (!IsWallTorch(type))
        {
            Vector3 worldMin = blockPos + min;
            Vector3 worldMax = blockPos + max;
            Vector3 size = worldMax - worldMin;
            return new Bounds(worldMin + size * 0.5f, size);
        }

        Vector3 modelMin = new Vector3(min.x - 0.5f, min.y, min.z - 0.5f);
        Vector3 modelMax = new Vector3(max.x - 0.5f, max.y, max.z - 0.5f);

        Vector3[] corners =
        {
            new Vector3(modelMin.x, modelMin.y, modelMin.z),
            new Vector3(modelMax.x, modelMin.y, modelMin.z),
            new Vector3(modelMax.x, modelMax.y, modelMin.z),
            new Vector3(modelMin.x, modelMax.y, modelMin.z),
            new Vector3(modelMin.x, modelMin.y, modelMax.z),
            new Vector3(modelMax.x, modelMin.y, modelMax.z),
            new Vector3(modelMax.x, modelMax.y, modelMax.z),
            new Vector3(modelMin.x, modelMax.y, modelMax.z)
        };

        Vector3 boundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 boundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 worldPoint = blockPos + TransformModelPoint(type, corners[i]);
            boundsMin = Vector3.Min(boundsMin, worldPoint);
            boundsMax = Vector3.Max(boundsMax, worldPoint);
        }

        Vector3 boundsSize = boundsMax - boundsMin;
        return new Bounds(boundsMin + boundsSize * 0.5f, boundsSize);
    }
}
