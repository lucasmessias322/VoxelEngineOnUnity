using System.Collections.Generic;
using UnityEngine;

public enum BlockFace { Top = 0, Bottom = 1, Right = 2, Left = 3, Front = 4, Back = 5, Side = 6 }
public enum BlockRenderShape : byte { Cube = 0, Cross = 1, Cuboid = 2, Plane = 3 }
public enum BlockPlacementAxis : byte
{
    Y = 0,
    X = 1,
    Z = 2,
    XNegative = 3,
    ZNegative = 4
}
public enum BlockPlacementRotationAxes : byte { Vertical = 0, Horizontal = 1, Both = 2 }

public static class WirePlacementUtility
{
    public const byte SideWest = 16;
    public const byte SideEast = 17;
    public const byte SideSouth = 18;
    public const byte SideNorth = 19;
    public const byte TopWest = 20;
    public const byte TopEast = 21;
    public const byte TopSouth = 22;
    public const byte TopNorth = 23;

    public static bool IsEncodedState(byte rawValue)
    {
        return rawValue >= SideWest && rawValue <= TopNorth;
    }

    public static byte ResolvePlacementCode(Vector3Int hitNormal)
    {
        if (Mathf.Abs(hitNormal.x) > 0)
            return hitNormal.x > 0 ? SideWest : SideEast;

        if (Mathf.Abs(hitNormal.z) > 0)
            return hitNormal.z > 0 ? SideSouth : SideNorth;

        return (byte)BlockPlacementAxis.Y;
    }

    public static bool HasTop(byte rawValue)
    {
        return rawValue == (byte)BlockPlacementAxis.Y ||
               rawValue == (byte)BlockPlacementAxis.XNegative ||
               rawValue == (byte)BlockPlacementAxis.ZNegative ||
               rawValue == TopWest ||
               rawValue == TopEast ||
               rawValue == TopSouth ||
               rawValue == TopNorth;
    }

    public static bool TryGetWall(byte rawValue, out BlockPlacementAxis axis, out int attachmentSide)
    {
        switch (rawValue)
        {
            case SideWest:
            case TopWest:
                axis = BlockPlacementAxis.X;
                attachmentSide = -1;
                return true;

            case SideEast:
            case TopEast:
                axis = BlockPlacementAxis.X;
                attachmentSide = 1;
                return true;

            case SideSouth:
            case TopSouth:
                axis = BlockPlacementAxis.Z;
                attachmentSide = -1;
                return true;

            case SideNorth:
            case TopNorth:
                axis = BlockPlacementAxis.Z;
                attachmentSide = 1;
                return true;

            default:
                axis = BlockPlacementAxis.Y;
                attachmentSide = 0;
                return false;
        }
    }

    public static bool TryMerge(byte existingRawValue, byte requestedRawValue, out byte mergedRawValue)
    {
        mergedRawValue = existingRawValue;

        bool existingHasTop = HasTop(existingRawValue);
        bool requestedHasTop = HasTop(requestedRawValue);
        bool existingHasWall = TryGetWall(existingRawValue, out BlockPlacementAxis existingWallAxis, out int existingAttachmentSide);
        bool requestedHasWall = TryGetWall(requestedRawValue, out BlockPlacementAxis requestedWallAxis, out int requestedAttachmentSide);

        if (!requestedHasTop && !requestedHasWall)
            return false;

        if (requestedHasTop && !requestedHasWall)
        {
            if (existingHasTop)
                return true;

            if (!existingHasWall)
            {
                mergedRawValue = (byte)BlockPlacementAxis.Y;
                return true;
            }

            mergedRawValue = Compose(existingWallAxis, existingAttachmentSide, true);
            return true;
        }

        if (!requestedHasTop && requestedHasWall)
        {
            if (!existingHasWall)
            {
                mergedRawValue = Compose(requestedWallAxis, requestedAttachmentSide, existingHasTop);
                return true;
            }

            if (existingWallAxis != requestedWallAxis || existingAttachmentSide != requestedAttachmentSide)
                return false;

            if (existingHasTop)
                mergedRawValue = Compose(existingWallAxis, existingAttachmentSide, true);
            else
                mergedRawValue = Compose(existingWallAxis, existingAttachmentSide, false);

            return true;
        }

        if (!existingHasWall && !existingHasTop)
        {
            mergedRawValue = requestedRawValue;
            return true;
        }

        if (existingHasWall &&
            requestedHasWall &&
            (existingWallAxis != requestedWallAxis || existingAttachmentSide != requestedAttachmentSide))
        {
            return false;
        }

        mergedRawValue = Compose(
            existingHasWall ? existingWallAxis : requestedWallAxis,
            existingHasWall ? existingAttachmentSide : requestedAttachmentSide,
            existingHasTop || requestedHasTop);
        return true;
    }

    public static byte Compose(BlockPlacementAxis wallAxis, int attachmentSide, bool includeTop)
    {
        if (wallAxis == BlockPlacementAxis.X)
        {
            if (attachmentSide < 0)
                return includeTop ? TopWest : SideWest;

            return includeTop ? TopEast : SideEast;
        }

        if (attachmentSide < 0)
            return includeTop ? TopSouth : SideSouth;

        return includeTop ? TopNorth : SideNorth;
    }
}

public static class BlockFaceUtility
{
    public static BlockFace FromCubeFaceIndex(int faceIndex)
    {
        switch (faceIndex)
        {
            case 0: return BlockFace.Right;
            case 1: return BlockFace.Left;
            case 2: return BlockFace.Top;
            case 3: return BlockFace.Bottom;
            case 4: return BlockFace.Front;
            case 5: return BlockFace.Back;
            default: return BlockFace.Side;
        }
    }

    public static BlockFace FromAxisNormal(int axis, int normalSign)
    {
        switch (axis)
        {
            case 0:
                return normalSign > 0 ? BlockFace.Right : BlockFace.Left;

            case 1:
                return normalSign > 0 ? BlockFace.Top : BlockFace.Bottom;

            case 2:
                return normalSign > 0 ? BlockFace.Front : BlockFace.Back;

            default:
                return BlockFace.Side;
        }
    }
}

[CreateAssetMenu(fileName = "BlockDataSO", menuName = "ScriptableObjects/BlockDataSO", order = 1)]
public class BlockDataSO : ScriptableObject
{
    [Header("Texturas")]
    public Vector2 atlasSize = new Vector2(4, 4); // numero de tiles X,Y no atlas
    [Tooltip("Quando ligado, tile (0,0) representa a linha de cima do atlas.")]
    public bool atlasCoordinatesStartTopLeft = true;
    public List<BlockTextureMapping> blockTextures = new List<BlockTextureMapping>();

    [System.NonSerialized]
    public BlockTextureMapping[] mappings;

    public static bool[] IsSolidCache;
    public static bool[] IsEmptyCache;

    private void OnEnable()
    {
        SyncDirectionalSideMappings();
    }

    private void OnValidate()
    {
        SyncDirectionalSideMappings();
    }

    /// <summary>
    /// Inicializa o array de mapeamentos.
    /// </summary>
    public void InitializeDictionary()
    {
        SyncDirectionalSideMappings();

        int maxBlockTypeValue = GetMaxBlockTypeValue();
        int mappingCount = maxBlockTypeValue + 1;
        mappings = new BlockTextureMapping[mappingCount];

        for (int i = 0; i < blockTextures.Count; i++)
        {
            BlockTextureMapping mapping = blockTextures[i];
            int index = (int)mapping.blockType;
            if (index >= 0 && index < mappingCount)
                mappings[index] = mapping;
        }

        PopulateTorchFallbackMappings();
        PopulateWaterFallbackMappings();
    }

    private static int GetMaxBlockTypeValue()
    {
        int maxValue = 0;
        System.Array values = System.Enum.GetValues(typeof(BlockType));
        for (int i = 0; i < values.Length; i++)
        {
            int value = (int)values.GetValue(i);
            if (value > maxValue)
                maxValue = value;
        }

        return maxValue;
    }

    /// <summary>
    /// Retorna o mapping para o tipo de bloco; se nao existir, retorna null.
    /// </summary>
    public BlockTextureMapping? GetMapping(BlockType type)
    {
        if (mappings == null || mappings.Length == 0)
            InitializeDictionary();

        int index = (int)type;
        if (index >= 0 && index < mappings.Length)
            return mappings[index];

        return null;
    }

    private void PopulateTorchFallbackMappings()
    {
        if (!TryGetTorchTemplateMapping(out BlockTextureMapping template))
            return;

        EnsureFallbackMapping(BlockType.torch, template);
        EnsureFallbackMapping(BlockType.WallTorchEast, template);
        EnsureFallbackMapping(BlockType.WallTorchWest, template);
        EnsureFallbackMapping(BlockType.WallTorchSouth, template);
        EnsureFallbackMapping(BlockType.WallTorchNorth, template);
    }

    private void PopulateWaterFallbackMappings()
    {
        if (!TryGetExplicitMapping(BlockType.Water, out BlockTextureMapping template))
            return;

        EnsureFallbackMapping(BlockType.WaterFlow1, template);
        EnsureFallbackMapping(BlockType.WaterFlow2, template);
        EnsureFallbackMapping(BlockType.WaterFlow3, template);
        EnsureFallbackMapping(BlockType.WaterFlow4, template);
        EnsureFallbackMapping(BlockType.WaterFlow5, template);
        EnsureFallbackMapping(BlockType.WaterFlow6, template);
        EnsureFallbackMapping(BlockType.WaterFlow7, template);
        EnsureFallbackMapping(BlockType.WaterFall0, template);
        EnsureFallbackMapping(BlockType.WaterFall1, template);
        EnsureFallbackMapping(BlockType.WaterFall2, template);
        EnsureFallbackMapping(BlockType.WaterFall3, template);
        EnsureFallbackMapping(BlockType.WaterFall4, template);
        EnsureFallbackMapping(BlockType.WaterFall5, template);
        EnsureFallbackMapping(BlockType.WaterFall6, template);
        EnsureFallbackMapping(BlockType.WaterFall7, template);
    }

    private bool TryGetTorchTemplateMapping(out BlockTextureMapping template)
    {
        if (TryGetExplicitMapping(BlockType.torch, out template))
            return true;

        return TryGetExplicitMapping(BlockType.glowstone, out template);
    }

    private bool TryGetExplicitMapping(BlockType type, out BlockTextureMapping mapping)
    {
        mapping = default;
        if (mappings == null || mappings.Length == 0)
            return false;

        int index = (int)type;
        if (index < 0 || index >= mappings.Length)
            return false;

        BlockTextureMapping candidate = mappings[index];
        if (candidate.blockType != type)
            return false;

        mapping = candidate;
        return true;
    }

    private void EnsureFallbackMapping(BlockType type, BlockTextureMapping template)
    {
        int index = (int)type;
        if (index < 0 || index >= mappings.Length)
            return;

        if (mappings[index].blockType == type)
            return;

        template.blockType = type;
        mappings[index] = template;
    }

    /// <summary>
    /// Retorna as coordenadas do tile para a face desejada.
    /// </summary>
    public Vector2Int GetTileCoord(BlockType type, BlockFace face)
    {
        BlockTextureMapping? mapping = GetMapping(type);
        if (mapping == null)
            return new Vector2Int(0, 0);

        return mapping.Value.GetTileCoord(face);
    }

    /// <summary>
    /// Retorna se o bloco foi marcado como liquido no mapeamento.
    /// Mantem compatibilidade para agua mesmo sem mapeamento.
    /// </summary>
    public bool IsLiquid(BlockType type)
    {
        if (FluidBlockUtility.IsWater(type))
            return true;

        BlockTextureMapping? mapping = GetMapping(type);
        return mapping != null && mapping.Value.isLiquid;
    }

    private void SyncDirectionalSideMappings()
    {
        if (blockTextures == null)
            return;

        for (int i = 0; i < blockTextures.Count; i++)
        {
            BlockTextureMapping mapping = blockTextures[i];
            if (!mapping.EnsureDirectionalSideData())
                continue;

            blockTextures[i] = mapping;
        }
    }
}

[System.Serializable]
public struct BlockTextureMapping
{
    public BlockType blockType;
    public Vector2Int top;    // coordenada no atlas para a face de cima (tileX, tileY)
    public Vector2Int bottom; // coordenada no atlas para a face de baixo
    public Vector2Int right;  // face +X
    public Vector2Int left;   // face -X
    public Vector2Int front;  // face +Z
    public Vector2Int back;   // face -Z

    [HideInInspector] public Vector2Int side; // legado: usado para migrar assets antigos
    [SerializeField, HideInInspector] private bool directionalSideDataInitialized;

    [Header("Rendering")]
    [Tooltip("Cube = voxel normal, Cross = duas quads cruzadas para plantas, Cuboid = caixa menor dentro do voxel (bom para tochas/postes), Plane = quad dupla face (redstone/quadros/vinhas).")]
    public BlockRenderShape renderShape;
    [Tooltip("Quando ativo, o bloco usa malha plana (quad dupla face), similar a redstone/quadro/vinhas.")]
    public bool isFlat;
    [Tooltip("Canto minimo local do formato dentro do voxel (0..1). Usado em Cross, Cuboid e Plane.")]
    public Vector3 shapeMin;
    [Tooltip("Canto maximo local do formato dentro do voxel (0..1). Usado em Cross, Cuboid e Plane.")]
    public Vector3 shapeMax;

    [Header("Behavior (use to control face culling / water handling)")]
    public bool isEmpty;       // ex: true para agua/ar
    public bool isSolid;       // defina como true no Inspector para blocos solidos
    public bool isTransparent; // ex: true para vidro, folhas
    public bool isLiquid;      // true para agua e outros blocos liquidos
    public bool isLightSource; // ex: blocos que emitem luz, como tochas
    public int materialIndex;  // default: 0

    [Header("Breaking")]
    [Min(0f)] public float breakTimeMultiplier;
    public ToolType preferredTool;

    [Header("Placement Rotation")]
    [Tooltip("Quando ativo, o bloco gira o eixo de exibicao ao ser colocado (estilo tronco do Minecraft).")]
    public bool usePlacementAxisRotation;
    [Tooltip("Vertical = eixo Y, Horizontal = eixo X/Z, Both = permite os dois.")]
    public BlockPlacementRotationAxes placementRotationAxes;

    public byte lightOpacity;  // 0..15 (0 = nao reduz, 15 = bloqueia)
    public byte lightEmission; // 0..15 (Glowstone = 15, Torch = 14)

    [Header("Biome Tinting")]
    [Tooltip("Aplica cor do bioma nesta face?")]
    public bool tintTop;

    [Tooltip("Aplica cor do bioma nesta face?")]
    public bool tintBottom;

    [Tooltip("Aplica cor do bioma nesta face?")]
    public bool tintRight;

    [Tooltip("Aplica cor do bioma nesta face?")]
    public bool tintLeft;

    [Tooltip("Aplica cor do bioma nesta face?")]
    public bool tintFront;

    [Tooltip("Aplica cor do bioma nesta face?")]
    public bool tintBack;

    [HideInInspector] public bool tintSide; // legado: usado para migrar assets antigos

    public bool EnsureDirectionalSideData()
    {
        if (directionalSideDataInitialized)
            return false;

        right = side;
        left = side;
        front = side;
        back = side;
        tintRight = tintSide;
        tintLeft = tintSide;
        tintFront = tintSide;
        tintBack = tintSide;
        directionalSideDataInitialized = true;
        return true;
    }

    public Vector2Int GetTileCoord(BlockFace face)
    {
        switch (face)
        {
            case BlockFace.Top:
                return top;
            case BlockFace.Bottom:
                return bottom;
            case BlockFace.Right:
                return right;
            case BlockFace.Left:
                return left;
            case BlockFace.Front:
                return front;
            case BlockFace.Back:
                return back;
            default:
                return side;
        }
    }

    public bool GetTint(BlockFace face)
    {
        switch (face)
        {
            case BlockFace.Top:
                return tintTop;
            case BlockFace.Bottom:
                return tintBottom;
            case BlockFace.Right:
                return tintRight;
            case BlockFace.Left:
                return tintLeft;
            case BlockFace.Front:
                return tintFront;
            case BlockFace.Back:
                return tintBack;
            default:
                return tintSide;
        }
    }
}

public static class BlockPlacementRotationUtility
{
    public static BlockPlacementAxis SanitizeAxis(BlockPlacementAxis axis)
    {
        return axis switch
        {
            BlockPlacementAxis.X => BlockPlacementAxis.X,
            BlockPlacementAxis.XNegative => BlockPlacementAxis.X,
            BlockPlacementAxis.Z => BlockPlacementAxis.Z,
            BlockPlacementAxis.ZNegative => BlockPlacementAxis.Z,
            _ => BlockPlacementAxis.Y
        };
    }

    public static BlockPlacementAxis SanitizeAxis(byte axis)
    {
        return SanitizeAxis((BlockPlacementAxis)axis);
    }

    public static byte SanitizeAxisByte(byte axis)
    {
        return (byte)SanitizeAxis(axis);
    }

    public static BlockPlacementAxis SanitizeStoredAxis(BlockPlacementAxis axis)
    {
        return axis switch
        {
            BlockPlacementAxis.X => BlockPlacementAxis.X,
            BlockPlacementAxis.XNegative => BlockPlacementAxis.XNegative,
            BlockPlacementAxis.Z => BlockPlacementAxis.Z,
            BlockPlacementAxis.ZNegative => BlockPlacementAxis.ZNegative,
            _ => BlockPlacementAxis.Y
        };
    }

    public static BlockPlacementAxis SanitizeStoredAxis(byte axis)
    {
        return SanitizeStoredAxis((BlockPlacementAxis)axis);
    }

    public static byte SanitizeStoredAxisByte(byte axis)
    {
        return (byte)SanitizeStoredAxis(axis);
    }

    public static BlockPlacementAxis ResolvePlacementAxis(
        BlockTextureMapping mapping,
        Vector3Int hitNormal,
        Vector3 lookForward)
    {
        if (!mapping.usePlacementAxisRotation)
            return BlockPlacementAxis.Y;

        if (BlockShapeUtility.IsFlatShape(mapping) &&
            mapping.placementRotationAxes == BlockPlacementRotationAxes.Both)
        {
            return ResolveFlatPlanePlacementAxis(hitNormal, lookForward);
        }

        return ResolvePlacementAxis(mapping.placementRotationAxes, hitNormal, lookForward);
    }

    public static BlockPlacementAxis ResolvePlacementAxis(
        BlockPlacementRotationAxes allowedAxes,
        Vector3Int hitNormal,
        Vector3 lookForward)
    {
        return allowedAxes switch
        {
            BlockPlacementRotationAxes.Vertical => BlockPlacementAxis.Y,
            BlockPlacementRotationAxes.Horizontal => ResolveHorizontalAxisFromLookForward(lookForward),
            BlockPlacementRotationAxes.Both => ResolveAxisFromHitNormalOrFallback(hitNormal, lookForward),
            _ => BlockPlacementAxis.Y
        };
    }

    public static BlockFace ResolveFaceForPlacement(BlockTextureMapping mapping, BlockFace worldFace, BlockPlacementAxis axis)
    {
        if (!mapping.usePlacementAxisRotation)
            return worldFace;

        if (mapping.placementRotationAxes == BlockPlacementRotationAxes.Horizontal &&
            BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.Cube)
        {
            return RemapHorizontalFacingFace(worldFace, axis);
        }

        return RemapFace(worldFace, axis);
    }

    public static BlockFace RemapFace(BlockFace worldFace, BlockPlacementAxis axis)
    {
        axis = SanitizeAxis(axis);
        if (axis == BlockPlacementAxis.Y)
            return worldFace;

        if (axis == BlockPlacementAxis.X)
        {
            return worldFace switch
            {
                BlockFace.Right => BlockFace.Top,
                BlockFace.Left => BlockFace.Bottom,
                BlockFace.Top => BlockFace.Right,
                BlockFace.Bottom => BlockFace.Left,
                BlockFace.Front => BlockFace.Front,
                BlockFace.Back => BlockFace.Back,
                _ => worldFace
            };
        }

        return worldFace switch
        {
            BlockFace.Right => BlockFace.Right,
            BlockFace.Left => BlockFace.Left,
            BlockFace.Top => BlockFace.Back,
            BlockFace.Bottom => BlockFace.Front,
            BlockFace.Front => BlockFace.Top,
            BlockFace.Back => BlockFace.Bottom,
            _ => worldFace
        };
    }

    private static BlockFace RemapHorizontalFacingFace(BlockFace worldFace, BlockPlacementAxis axis)
    {
        axis = SanitizeStoredAxis(axis);
        return axis switch
        {
            BlockPlacementAxis.X => worldFace switch
            {
                BlockFace.Right => BlockFace.Front,
                BlockFace.Left => BlockFace.Back,
                BlockFace.Front => BlockFace.Left,
                BlockFace.Back => BlockFace.Right,
                _ => worldFace
            },

            BlockPlacementAxis.XNegative => worldFace switch
            {
                BlockFace.Right => BlockFace.Back,
                BlockFace.Left => BlockFace.Front,
                BlockFace.Front => BlockFace.Right,
                BlockFace.Back => BlockFace.Left,
                _ => worldFace
            },

            BlockPlacementAxis.ZNegative => worldFace switch
            {
                BlockFace.Right => BlockFace.Left,
                BlockFace.Left => BlockFace.Right,
                BlockFace.Front => BlockFace.Back,
                BlockFace.Back => BlockFace.Front,
                _ => worldFace
            },

            _ => worldFace
        };
    }

    private static BlockPlacementAxis ResolveAxisFromHitNormalOrFallback(Vector3Int hitNormal, Vector3 lookForward)
    {
        if (hitNormal.x > 0)
            return BlockPlacementAxis.XNegative;

        if (hitNormal.x < 0)
            return BlockPlacementAxis.X;

        if (hitNormal.z > 0)
            return BlockPlacementAxis.ZNegative;

        if (hitNormal.z < 0)
            return BlockPlacementAxis.Z;

        if (Mathf.Abs(hitNormal.y) > 0)
            return BlockPlacementAxis.Y;

        return ResolveHorizontalAxisFromLookForward(lookForward);
    }

    private static BlockPlacementAxis ResolveHorizontalAxis(Vector3Int hitNormal, Vector3 lookForward)
    {
        if (Mathf.Abs(hitNormal.x) > 0)
            return BlockPlacementAxis.X;

        if (Mathf.Abs(hitNormal.z) > 0)
            return BlockPlacementAxis.Z;

        return ResolveHorizontalAxisFromLookForward(lookForward);
    }

    private static BlockPlacementAxis ResolveHorizontalAxisFromLookForward(Vector3 lookForward)
    {
        float absX = Mathf.Abs(lookForward.x);
        float absZ = Mathf.Abs(lookForward.z);
        if (absX >= absZ)
            return lookForward.x >= 0f ? BlockPlacementAxis.X : BlockPlacementAxis.XNegative;

        return lookForward.z >= 0f ? BlockPlacementAxis.Z : BlockPlacementAxis.ZNegative;
    }

    private static BlockPlacementAxis ResolveFlatPlanePlacementAxis(Vector3Int hitNormal, Vector3 lookForward)
    {
        if (Mathf.Abs(hitNormal.x) > 0)
            return BlockPlacementAxis.X;

        if (Mathf.Abs(hitNormal.z) > 0)
            return BlockPlacementAxis.Z;

        BlockPlacementAxis horizontalAxis = ResolveHorizontalAxisFromLookForward(lookForward);
        return horizontalAxis == BlockPlacementAxis.Z || horizontalAxis == BlockPlacementAxis.ZNegative
            ? BlockPlacementAxis.ZNegative
            : BlockPlacementAxis.XNegative;
    }
}

public static class BlockShapeUtility
{
    private static readonly Vector3 DefaultCrossMin = new Vector3(0.15f, 0f, 0.15f);
    private static readonly Vector3 DefaultCrossMax = new Vector3(0.85f, 1f, 0.85f);
    private static readonly Vector3 DefaultCuboidMin = new Vector3(0.375f, 0f, 0.375f);
    private static readonly Vector3 DefaultCuboidMax = new Vector3(0.625f, 0.75f, 0.625f);
    private static readonly Vector3 DefaultPlaneMin = new Vector3(0f, 0f, 0f);
    private static readonly Vector3 DefaultPlaneMax = new Vector3(1f, 0.0625f, 1f);
    private const float PlaneBoundsHalfThickness = 0.01f;
    private const float PlaneAttachmentInset01 = 0.001f;
    private const float BoundsEpsilon = 0.0001f;

    public static BlockRenderShape GetEffectiveRenderShape(BlockTextureMapping mapping)
    {
        return mapping.isFlat ? BlockRenderShape.Plane : mapping.renderShape;
    }

    public static bool IsFlatShape(BlockTextureMapping mapping)
    {
        return GetEffectiveRenderShape(mapping) == BlockRenderShape.Plane;
    }

    public static bool UsesCustomMesh(BlockTextureMapping mapping)
    {
        return GetEffectiveRenderShape(mapping) != BlockRenderShape.Cube;
    }

    public static byte GetEffectiveLightOpacity(BlockTextureMapping mapping)
    {
        if (GetEffectiveRenderShape(mapping) != BlockRenderShape.Cube && !mapping.isSolid && !mapping.isLiquid)
            return 0;

        return mapping.lightOpacity;
    }

    public static void ResolveShapeBounds(BlockTextureMapping mapping, out Vector3 min, out Vector3 max)
    {
        Vector3 clampedMin = Clamp01(mapping.shapeMin);
        Vector3 clampedMax = Clamp01(mapping.shapeMax);

        bool valid =
            clampedMax.x > clampedMin.x + BoundsEpsilon &&
            clampedMax.y > clampedMin.y + BoundsEpsilon &&
            clampedMax.z > clampedMin.z + BoundsEpsilon;

        if (valid)
        {
            min = clampedMin;
            max = clampedMax;
            return;
        }

        switch (GetEffectiveRenderShape(mapping))
        {
            case BlockRenderShape.Cross:
                min = DefaultCrossMin;
                max = DefaultCrossMax;
                return;

            case BlockRenderShape.Cuboid:
                min = DefaultCuboidMin;
                max = DefaultCuboidMax;
                return;

            case BlockRenderShape.Plane:
                min = DefaultPlaneMin;
                max = DefaultPlaneMax;
                return;

            default:
                min = Vector3.zero;
                max = Vector3.one;
                return;
        }
    }

    public static Bounds GetWorldBounds(Vector3Int blockPos, BlockTextureMapping mapping)
    {
        return GetWorldBounds(blockPos, BlockType.Air, mapping, BlockPlacementAxis.Y);
    }

    public static Bounds GetWorldBounds(Vector3Int blockPos, BlockType blockType, BlockTextureMapping mapping)
    {
        return GetWorldBounds(blockPos, blockType, mapping, BlockPlacementAxis.Y);
    }

    public static Bounds GetWorldBounds(
        Vector3Int blockPos,
        BlockType blockType,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis)
    {
        return GetWorldBounds(blockPos, blockType, mapping, placementAxis, false, false);
    }

    public static Bounds GetWorldBounds(
        Vector3Int blockPos,
        BlockType blockType,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        bool hasNegativeSupport,
        bool hasPositiveSupport)
    {
        if (TorchPlacementUtility.IsWallTorch(blockType))
            return TorchPlacementUtility.GetWorldBounds(blockPos, blockType, mapping);

        if (IsFlatShape(mapping))
        {
            ResolvePlaneQuad(
                mapping,
                placementAxis,
                hasNegativeSupport,
                hasPositiveSupport,
                out Vector3 p0,
                out Vector3 p1,
                out Vector3 p2,
                out Vector3 p3,
                out _,
                out _);

            Vector3 worldP0 = blockPos + p0;
            Vector3 worldP1 = blockPos + p1;
            Vector3 worldP2 = blockPos + p2;
            Vector3 worldP3 = blockPos + p3;

            Vector3 planeWorldMin = Vector3.Min(Vector3.Min(worldP0, worldP1), Vector3.Min(worldP2, worldP3));
            Vector3 planeWorldMax = Vector3.Max(Vector3.Max(worldP0, worldP1), Vector3.Max(worldP2, worldP3));

            BlockPlacementAxis axis = ResolvePlaneAxis(mapping, placementAxis);
            switch (axis)
            {
                case BlockPlacementAxis.X:
                    planeWorldMin.x -= PlaneBoundsHalfThickness;
                    planeWorldMax.x += PlaneBoundsHalfThickness;
                    break;

                case BlockPlacementAxis.Z:
                    planeWorldMin.z -= PlaneBoundsHalfThickness;
                    planeWorldMax.z += PlaneBoundsHalfThickness;
                    break;

                default:
                    planeWorldMin.y -= PlaneBoundsHalfThickness;
                    planeWorldMax.y += PlaneBoundsHalfThickness;
                    break;
            }

            Vector3 planeSize = planeWorldMax - planeWorldMin;
            return new Bounds(planeWorldMin + planeSize * 0.5f, planeSize);
        }

        ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

        Vector3 boundsMin = blockPos + min;
        Vector3 boundsMax = blockPos + max;
        Vector3 size = boundsMax - boundsMin;
        Vector3 center = boundsMin + size * 0.5f;
        return new Bounds(center, size);
    }

    public static void ResolvePlaneQuad(
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        out Vector3 p0,
        out Vector3 p1,
        out Vector3 p2,
        out Vector3 p3,
        out BlockFace sampledFace,
        out Vector3 normal)
    {
        ResolvePlaneQuad(
            mapping,
            placementAxis,
            false,
            false,
            out p0,
            out p1,
            out p2,
            out p3,
            out sampledFace,
            out normal);
    }

    public static void ResolvePlaneQuad(
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        bool hasNegativeSupport,
        bool hasPositiveSupport,
        out Vector3 p0,
        out Vector3 p1,
        out Vector3 p2,
        out Vector3 p3,
        out BlockFace sampledFace,
        out Vector3 normal)
    {
        ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);
        BlockPlacementAxis axis = ResolvePlaneAxis(mapping, placementAxis);

        switch (axis)
        {
            case BlockPlacementAxis.X:
            {
                float x = ResolveSidePlaneCoordinate(hasNegativeSupport, hasPositiveSupport);
                p0 = new Vector3(x, min.x, min.z);
                p1 = new Vector3(x, max.x, min.z);
                p2 = new Vector3(x, max.x, max.z);
                p3 = new Vector3(x, min.x, max.z);
                sampledFace = BlockFace.Right;
                normal = Vector3.right;
                return;
            }

            case BlockPlacementAxis.Z:
            {
                float z = ResolveSidePlaneCoordinate(hasNegativeSupport, hasPositiveSupport);
                p0 = new Vector3(min.x, min.z, z);
                p1 = new Vector3(max.x, min.z, z);
                p2 = new Vector3(max.x, max.z, z);
                p3 = new Vector3(min.x, max.z, z);
                sampledFace = BlockFace.Front;
                normal = Vector3.forward;
                return;
            }

            default:
            {
                float y = (min.y + max.y) * 0.5f;
                p0 = new Vector3(min.x, y, min.z);
                p1 = new Vector3(min.x, y, max.z);
                p2 = new Vector3(max.x, y, max.z);
                p3 = new Vector3(max.x, y, min.z);
                sampledFace = BlockFace.Top;
                normal = Vector3.up;
                return;
            }
        }
    }

    private static BlockPlacementAxis ResolvePlaneAxis(BlockTextureMapping mapping, BlockPlacementAxis placementAxis)
    {
        if (!mapping.usePlacementAxisRotation)
            return BlockPlacementAxis.Y;

        if (GetEffectiveRenderShape(mapping) == BlockRenderShape.Plane &&
            mapping.placementRotationAxes == BlockPlacementRotationAxes.Both)
        {
            BlockPlacementAxis storedAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(placementAxis);
            return storedAxis switch
            {
                BlockPlacementAxis.X => BlockPlacementAxis.X,
                BlockPlacementAxis.Z => BlockPlacementAxis.Z,
                _ => BlockPlacementAxis.Y
            };
        }

        return BlockPlacementRotationUtility.SanitizeAxis(placementAxis);
    }

    private static float ResolveSidePlaneCoordinate(bool hasNegativeSupport, bool hasPositiveSupport)
    {
        if (hasNegativeSupport == hasPositiveSupport)
            return 0.5f;

        return hasNegativeSupport ? PlaneAttachmentInset01 : 1f - PlaneAttachmentInset01;
    }

    private static Vector3 Clamp01(Vector3 value)
    {
        return new Vector3(
            Mathf.Clamp01(value.x),
            Mathf.Clamp01(value.y),
            Mathf.Clamp01(value.z));
    }
}
