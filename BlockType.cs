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

    DiamondOre = 21,
    EmeraldOre = 22,
    Cactus = 23,
    acacia_log = 24,
    torch = 25,
    WallTorchEast = 26,
    WallTorchWest = 27,
    WallTorchSouth = 28,
    WallTorchNorth = 29,
    WaterFlow1 = 30,
    WaterFlow2 = 31,
    WaterFlow3 = 32,
    WaterFlow4 = 33,
    WaterFlow5 = 34,
    WaterFlow6 = 35,
    WaterFlow7 = 36,
    WaterFall0 = 37,
    WaterFall1 = 38,
    WaterFall2 = 39,
    WaterFall3 = 40,
    WaterFall4 = 41,
    WaterFall5 = 42,
    WaterFall6 = 43,
    WaterFall7 = 44,
    StoneFurnance = 45,
    dead_bush = 46,
    Copper_ore = 47,
    wire = 48,
    Oak_Leader = 49,
    Oak_Ramp = 50,
    Oak_VerticalRamp = 51,
    Anvil = 52,
    SolarPanel = 53,
    pipe = 54,
    blueGowstone = 55,
    redGowstone = 56,
    Builders_Workbench = 57,
    Stone_Ramp = 58,
    Stone_VerticalRamp = 59,
    Stone_Leader = 60,
    RoboticArm = 61,
    ConveyorBelt = 62,
    chest = 63,
    EletricConnector = 64,
    batteryBlock = 65,
    windmill = 66,
    ledWhiteBlock = 67,
    ledRedBlock = 68,
    oakPlanks_fence = 69,
    oakPlanks_fence2 = 70,
    oakPlanks_slab = 71,
    conveyorBelt_splitter = 72,
    LeverOn = 73,
    LeverOff = 74,
    button = 75




}

public static class MachineBlockUtility
{
    public static bool IsMachineBlock(BlockType type)
    {
        switch (type)
        {
            case BlockType.SolarPanel:
            case BlockType.pipe:
            case BlockType.RoboticArm:
            case BlockType.ConveyorBelt:
            case BlockType.EletricConnector:
            case BlockType.batteryBlock:
            case BlockType.windmill:
            case BlockType.conveyorBelt_splitter:
                return true;

            default:
                return false;
        }
    }
}

public static class LeverUtility
{
    public static bool IsLeverBlock(BlockType type)
    {
        return type == BlockType.LeverOn ||
               type == BlockType.LeverOff;
    }

    public static bool IsLeverOn(BlockType type)
    {
        return type == BlockType.LeverOn;
    }

    public static BlockType Toggle(BlockType type)
    {
        if (type == BlockType.LeverOn)
            return BlockType.LeverOff;

        if (type == BlockType.LeverOff)
            return BlockType.LeverOn;

        return type;
    }

    public static BlockType GetPlacementBlockType(BlockType type)
    {
        return IsLeverBlock(type) ? BlockType.LeverOff : type;
    }

    public static BlockType GetInventoryDropBlockType(BlockType type)
    {
        return IsLeverBlock(type) ? BlockType.LeverOff : type;
    }
}

public static class FluidBlockUtility
{
    public const int MaxWaterDistance = 7;
    public const int FullWaterSurfaceStep = 9;
    private const float SurfaceStepDivisor = 9f;

    public static bool IsWater(BlockType type)
    {
        switch (type)
        {
            case BlockType.Water:
            case BlockType.WaterFlow1:
            case BlockType.WaterFlow2:
            case BlockType.WaterFlow3:
            case BlockType.WaterFlow4:
            case BlockType.WaterFlow5:
            case BlockType.WaterFlow6:
            case BlockType.WaterFlow7:
            case BlockType.WaterFall0:
            case BlockType.WaterFall1:
            case BlockType.WaterFall2:
            case BlockType.WaterFall3:
            case BlockType.WaterFall4:
            case BlockType.WaterFall5:
            case BlockType.WaterFall6:
            case BlockType.WaterFall7:
                return true;

            default:
                return false;
        }
    }

    public static bool IsFallingWater(BlockType type)
    {
        switch (type)
        {
            case BlockType.WaterFall0:
            case BlockType.WaterFall1:
            case BlockType.WaterFall2:
            case BlockType.WaterFall3:
            case BlockType.WaterFall4:
            case BlockType.WaterFall5:
            case BlockType.WaterFall6:
            case BlockType.WaterFall7:
                return true;

            default:
                return false;
        }
    }

    public static bool IsStillWater(BlockType type)
    {
        return type == BlockType.Water;
    }

    public static int GetWaterDistance(BlockType type)
    {
        switch (type)
        {
            case BlockType.Water:
            case BlockType.WaterFall0:
                return 0;

            case BlockType.WaterFlow1:
            case BlockType.WaterFall1:
                return 1;

            case BlockType.WaterFlow2:
            case BlockType.WaterFall2:
                return 2;

            case BlockType.WaterFlow3:
            case BlockType.WaterFall3:
                return 3;

            case BlockType.WaterFlow4:
            case BlockType.WaterFall4:
                return 4;

            case BlockType.WaterFlow5:
            case BlockType.WaterFall5:
                return 5;

            case BlockType.WaterFlow6:
            case BlockType.WaterFall6:
                return 6;

            case BlockType.WaterFlow7:
            case BlockType.WaterFall7:
                return 7;

            default:
                return MaxWaterDistance;
        }
    }

    public static BlockType GetWaterBlockType(int distance, bool falling)
    {
        int clampedDistance = Mathf.Clamp(distance, 0, MaxWaterDistance);
        if (!falling && clampedDistance == 0)
            return BlockType.Water;

        if (falling)
        {
            switch (clampedDistance)
            {
                case 0: return BlockType.WaterFall0;
                case 1: return BlockType.WaterFall1;
                case 2: return BlockType.WaterFall2;
                case 3: return BlockType.WaterFall3;
                case 4: return BlockType.WaterFall4;
                case 5: return BlockType.WaterFall5;
                case 6: return BlockType.WaterFall6;
                default: return BlockType.WaterFall7;
            }
        }

        switch (clampedDistance)
        {
            case 0: return BlockType.Water;
            case 1: return BlockType.WaterFlow1;
            case 2: return BlockType.WaterFlow2;
            case 3: return BlockType.WaterFlow3;
            case 4: return BlockType.WaterFlow4;
            case 5: return BlockType.WaterFlow5;
            case 6: return BlockType.WaterFlow6;
            default: return BlockType.WaterFlow7;
        }
    }

    public static int GetWaterSurfaceStep(BlockType type)
    {
        if (!IsWater(type))
            return 0;

        int distance = GetWaterDistance(type);
        return Mathf.Clamp(8 - distance, 1, 8);
    }

    public static float GetWaterSurfaceHeight01(BlockType type)
    {
        return GetWaterSurfaceStep(type) / SurfaceStepDivisor;
    }

    public static BlockType NormalizeWaterType(BlockType type)
    {
        return IsWater(type) ? BlockType.Water : type;
    }
}

public static class BatteryBlockUtility
{
    public static bool IsBatteryBlock(BlockType type)
    {
        return type == BlockType.batteryBlock;
    }

    public static BlockType GetInventoryDropBlockType(BlockType type)
    {
        return IsBatteryBlock(type) ? BlockType.batteryBlock : type;
    }

    public static BlockVisualStateCondition GetChargeVisualState(float charge01)
    {
        return BlockVisualStateUtility.GetBatteryChargeState(charge01);
    }
}

public static class TorchPlacementUtility
{
    public const float WallAngleDegrees = 22.5f;
    public const float WallAnchorHeight = 0.22f;
    public const float WallAnchorOffset = 0.5f;
    public const float FireAnchorHeight = 0.78f;

    public static bool IsTorchLike(BlockType type)
    {
        return type == BlockType.torch ||
               IsWallTorch(type);
    }

    public static bool IsWallTorch(BlockType type)
    {
        return type == BlockType.WallTorchEast ||
               type == BlockType.WallTorchWest ||
               type == BlockType.WallTorchSouth ||
               type == BlockType.WallTorchNorth;
    }

    public static bool IsTorchFireSource(BlockType type)
    {
        return type == BlockType.torch || IsWallTorch(type);
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

    public static Vector3 TransformVoxelPoint(BlockType type, Vector3 voxelPoint)
    {
        if (!IsWallTorch(type))
            return voxelPoint;

        Vector3 modelPoint = new Vector3(voxelPoint.x - 0.5f, voxelPoint.y, voxelPoint.z - 0.5f);
        return TransformModelPoint(type, modelPoint);
    }

    public static Vector3 GetFireAnchorWorldPosition(Vector3Int blockPos, BlockType type)
    {
        return blockPos +
               TransformVoxelPoint(type, new Vector3(0.5f, FireAnchorHeight, 0.5f)) +
               BlockSupportSurfaceUtility.GetTorchVisualWorldOffset(World.Instance, blockPos, type);
    }

    public static Bounds GetWorldBounds(Vector3Int blockPos, BlockType type, BlockTextureMapping mapping)
    {
        BlockShapeUtility.ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);
        Vector3 visualOffset = BlockSupportSurfaceUtility.GetTorchVisualWorldOffset(World.Instance, blockPos, type);

        if (!IsWallTorch(type))
        {
            Vector3 worldMin = blockPos + min + visualOffset;
            Vector3 worldMax = blockPos + max + visualOffset;
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
            Vector3 worldPoint = blockPos + TransformModelPoint(type, corners[i]) + visualOffset;
            boundsMin = Vector3.Min(boundsMin, worldPoint);
            boundsMax = Vector3.Max(boundsMax, worldPoint);
        }

        Vector3 boundsSize = boundsMax - boundsMin;
        return new Bounds(boundsMin + boundsSize * 0.5f, boundsSize);
    }
}
