using System.Collections.Generic;
using UnityEngine;

public enum BlockFace { Top = 0, Bottom = 1, Side = 2 }

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
