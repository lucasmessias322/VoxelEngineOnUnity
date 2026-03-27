using System.Collections.Generic;
using UnityEngine;

public enum BlockFace { Top = 0, Bottom = 1, Right = 2, Left = 3, Front = 4, Back = 5, Side = 6 }
public enum BlockRenderShape : byte { Cube = 0, Cross = 1, Cuboid = 2 }

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

        int enumCount = System.Enum.GetValues(typeof(BlockType)).Length;
        mappings = new BlockTextureMapping[enumCount];

        for (int i = 0; i < blockTextures.Count; i++)
        {
            BlockTextureMapping mapping = blockTextures[i];
            int index = (int)mapping.blockType;
            if (index >= 0 && index < enumCount)
                mappings[index] = mapping;
        }

        PopulateTorchFallbackMappings();
        PopulateWaterFallbackMappings();
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
    [Tooltip("Cube = voxel normal, Cross = duas quads cruzadas para plantas, Cuboid = caixa menor dentro do voxel (bom para tochas/postes).")]
    public BlockRenderShape renderShape;
    [Tooltip("Canto minimo local do formato dentro do voxel (0..1). Usado em Cross e Cuboid.")]
    public Vector3 shapeMin;
    [Tooltip("Canto maximo local do formato dentro do voxel (0..1). Usado em Cross e Cuboid.")]
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

public static class BlockShapeUtility
{
    private static readonly Vector3 DefaultCrossMin = new Vector3(0.15f, 0f, 0.15f);
    private static readonly Vector3 DefaultCrossMax = new Vector3(0.85f, 1f, 0.85f);
    private static readonly Vector3 DefaultCuboidMin = new Vector3(0.375f, 0f, 0.375f);
    private static readonly Vector3 DefaultCuboidMax = new Vector3(0.625f, 0.75f, 0.625f);
    private const float BoundsEpsilon = 0.0001f;

    public static bool UsesCustomMesh(BlockTextureMapping mapping)
    {
        return mapping.renderShape != BlockRenderShape.Cube;
    }

    public static byte GetEffectiveLightOpacity(BlockTextureMapping mapping)
    {
        if (mapping.renderShape != BlockRenderShape.Cube && !mapping.isSolid && !mapping.isLiquid)
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

        switch (mapping.renderShape)
        {
            case BlockRenderShape.Cross:
                min = DefaultCrossMin;
                max = DefaultCrossMax;
                return;

            case BlockRenderShape.Cuboid:
                min = DefaultCuboidMin;
                max = DefaultCuboidMax;
                return;

            default:
                min = Vector3.zero;
                max = Vector3.one;
                return;
        }
    }

    public static Bounds GetWorldBounds(Vector3Int blockPos, BlockTextureMapping mapping)
    {
        return GetWorldBounds(blockPos, BlockType.Air, mapping);
    }

    public static Bounds GetWorldBounds(Vector3Int blockPos, BlockType blockType, BlockTextureMapping mapping)
    {
        if (TorchPlacementUtility.IsWallTorch(blockType))
            return TorchPlacementUtility.GetWorldBounds(blockPos, blockType, mapping);

        ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

        Vector3 worldMin = blockPos + min;
        Vector3 worldMax = blockPos + max;
        Vector3 size = worldMax - worldMin;
        Vector3 center = worldMin + size * 0.5f;
        return new Bounds(center, size);
    }

    private static Vector3 Clamp01(Vector3 value)
    {
        return new Vector3(
            Mathf.Clamp01(value.x),
            Mathf.Clamp01(value.y),
            Mathf.Clamp01(value.z));
    }
}
