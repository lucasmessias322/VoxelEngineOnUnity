
using System.Collections.Generic;
using UnityEngine;

public enum BlockFace { Top = 0, Bottom = 1, Side = 2 }

[CreateAssetMenu(fileName = "BlockDataSO", menuName = "ScriptableObjects/BlockDataSO", order = 1)]
public class BlockDataSO : ScriptableObject
{
    [Header("Texturas")]
    public Vector2 atlasSize = new Vector2(4, 4); // número de tiles X,Y no atlas
    public List<BlockTextureMapping> blockTextures = new List<BlockTextureMapping>();

    // 🔹 Array para lookup rápido em vez de dicionário
    [System.NonSerialized]
    public BlockTextureMapping[] mappings;

    // New static caches
    public static bool[] IsSolidCache;
    public static bool[] IsEmptyCache;

    /// <summary>
    /// Inicializa o array de mapeamentos.
    /// </summary>
    public void InitializeDictionary()
    {
        // pega o número máximo de valores no enum
        int enumCount = System.Enum.GetValues(typeof(BlockType)).Length;
        mappings = new BlockTextureMapping[enumCount];

        foreach (var mapping in blockTextures)
        {
            int index = (int)mapping.blockType;
            if (index >= 0 && index < enumCount)
            {
                mappings[index] = mapping;
            }
        }

        
    }

    /// <summary>
    /// Retorna o mapping para o tipo de bloco; se não existir, retorna null
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
        var m = GetMapping(type);
        if (m == null) return new Vector2Int(0, 0);

        var mValue = m.Value;
        switch (face)
        {
            case BlockFace.Top: return mValue.top;
            case BlockFace.Bottom: return mValue.bottom;
            default: return mValue.side;
        }
    }
}


[System.Serializable]
public struct BlockTextureMapping
{
    public BlockType blockType;
    public Vector2Int top;     // coordenada no atlas para a face de cima (tileX, tileY)
    public Vector2Int bottom;  // coordenada no atlas para a face de baixo
    public Vector2Int side;    // coordenada no atlas para as laterais

    [Header("Behavior (use to control face culling / water handling)")]
    public bool isEmpty;   // default: false (ex: true para água/ar)
    public bool isSolid;   // default: false (defina como true no Inspector para blocos sólidos)
    public int materialIndex;  // default: 0
}