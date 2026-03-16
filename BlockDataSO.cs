using System.Collections.Generic;
using UnityEngine;

public enum BlockFace { Top = 0, Bottom = 1, Side = 2 }
public enum BlockRenderShape : byte { Cube = 0, Cross = 1, Cuboid = 2 }

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

    /// <summary>
    /// Inicializa o array de mapeamentos.
    /// </summary>
    public void InitializeDictionary()
    {
        int enumCount = System.Enum.GetValues(typeof(BlockType)).Length;
        mappings = new BlockTextureMapping[enumCount];

        foreach (BlockTextureMapping mapping in blockTextures)
        {
            int index = (int)mapping.blockType;
            if (index >= 0 && index < enumCount)
                mappings[index] = mapping;
        }
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

    /// <summary>
    /// Retorna as coordenadas do tile para a face desejada.
    /// </summary>
    public Vector2Int GetTileCoord(BlockType type, BlockFace face)
    {
        BlockTextureMapping? mapping = GetMapping(type);
        if (mapping == null)
            return new Vector2Int(0, 0);

        BlockTextureMapping value = mapping.Value;
        switch (face)
        {
            case BlockFace.Top:
                return value.top;
            case BlockFace.Bottom:
                return value.bottom;
            default:
                return value.side;
        }
    }

    /// <summary>
    /// Retorna se o bloco foi marcado como liquido no mapeamento.
    /// Mantem compatibilidade para agua mesmo sem mapeamento.
    /// </summary>
    public bool IsLiquid(BlockType type)
    {
        if (type == BlockType.Water)
            return true;

        BlockTextureMapping? mapping = GetMapping(type);
        return mapping != null && mapping.Value.isLiquid;
    }
}

[System.Serializable]
public struct BlockTextureMapping
{
    public BlockType blockType;
    public Vector2Int top;    // coordenada no atlas para a face de cima (tileX, tileY)
    public Vector2Int bottom; // coordenada no atlas para a face de baixo
    public Vector2Int side;   // coordenada no atlas para as laterais

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

    [Tooltip("Aplica cor do bioma nas laterais?")]
    public bool tintSide;
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
