using UnityEngine;
using System.Collections.Generic;

[CreateAssetMenu(fileName = "BlockDataSO", menuName = "ScriptableObjects/BlockDataSO", order = 1)]
public class BlockDataSO : ScriptableObject

{

    [Header("Texturas")]
    public Vector2 atlasSize = new Vector2(4, 4);
    public List<BlockTextureMapping> blockTextures = new List<BlockTextureMapping>();

    // Dicionário para acesso rápido (opcional, se você quiser manter o mesmo padrão do Chunk)
    [System.NonSerialized]
    public Dictionary<BlockType, BlockTextureMapping> blockTextureDict;


    // Método para inicializar o dicionário (chamado quando necessário)
    public void InitializeDictionary()
    {
        blockTextureDict = new Dictionary<BlockType, BlockTextureMapping>();
        foreach (var mapping in blockTextures)
        {
            if (!blockTextureDict.ContainsKey(mapping.blockType))
                blockTextureDict.Add(mapping.blockType, mapping);
        }
    }
}

[System.Serializable]
public class BlockTextureMapping
{
    public BlockType blockType;
    public Vector2Int top;     // coordenada no atlas para a face de cima
    public Vector2Int bottom;  // coordenada no atlas para a face de baixo
    public Vector2Int side;    // coordenada no atlas para as laterais

    // NOVO: se true, renderiza faces internas (igual água)
    [Header("Behavior (use to control face culling / water handling)")]
    public bool isEmpty = false;
    public bool isSolid = true;  // padrão: sólidos

     public int materialIndex = 0;
}
