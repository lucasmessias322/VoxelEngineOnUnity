using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "TextureAtlasDatabase", menuName = "ScriptableObjects/Texture Atlas Database", order = 4)]
public class TextureAtlasDatabaseSO : ScriptableObject
{
    [Header("Entries")]
    public List<AtlasTextureEntry> entries = new List<AtlasTextureEntry>();

    public List<AtlasTextureEntry> GetOrCreateEntries()
    {
        if (entries == null)
            entries = new List<AtlasTextureEntry>();

        return entries;
    }

    public bool HasValidEntries()
    {
        return entries != null && entries.Exists(entry => entry != null && entry.texture != null);
    }
}
