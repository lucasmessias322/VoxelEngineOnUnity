using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ItemAtlasDataSO))]
public sealed class ItemAtlasDataSOEditor : Editor
{
    private SerializedProperty atlasSizeProp;
    private SerializedProperty atlasTextureProp;
    private SerializedProperty atlasMaterialProp;
    private SerializedProperty textureDatabaseProp;
    private SerializedProperty paddingPixelsProp;
    private SerializedProperty fallbackUvInsetProp;
    private SerializedProperty itemMappingsProp;

    private void OnEnable()
    {
        atlasSizeProp = serializedObject.FindProperty("atlasSize");
        atlasTextureProp = serializedObject.FindProperty("atlasTexture");
        atlasMaterialProp = serializedObject.FindProperty("atlasMaterial");
        textureDatabaseProp = serializedObject.FindProperty("textureDatabase");
        paddingPixelsProp = serializedObject.FindProperty("paddingPixels");
        fallbackUvInsetProp = serializedObject.FindProperty("fallbackUvInset");
        itemMappingsProp = serializedObject.FindProperty("itemMappings");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        ItemAtlasDataSO atlasData = (ItemAtlasDataSO)target;
        HashSet<string> databaseIds = BuildDatabaseEntryIdSet(atlasData.textureDatabase);

        DrawAtlasSection(databaseIds);
        EditorGUILayout.Space();
        DrawToolsSection(databaseIds);
        EditorGUILayout.Space();
        DrawMappingsSection();
        EditorGUILayout.Space();
        DrawValidationSection(databaseIds);

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawAtlasSection(HashSet<string> databaseIds)
    {
        EditorGUILayout.LabelField("Atlas", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(atlasSizeProp);
        EditorGUILayout.PropertyField(atlasTextureProp);
        EditorGUILayout.PropertyField(atlasMaterialProp);
        EditorGUILayout.PropertyField(textureDatabaseProp);
        EditorGUILayout.PropertyField(paddingPixelsProp);
        EditorGUILayout.PropertyField(fallbackUvInsetProp);

        if (textureDatabaseProp.objectReferenceValue == null)
        {
            EditorGUILayout.HelpBox(
                "Sem TextureAtlasDatabaseSO, os itens continuam dependendo de tile/tileSpan como fallback legado.",
                MessageType.Warning);
            return;
        }

        EditorGUILayout.HelpBox(
            $"Database carregado com {databaseIds.Count} entryIds. Prefira IDs como 'item/stick' no lugar de coordenadas manuais.",
            MessageType.Info);
    }

    private void DrawToolsSection(HashSet<string> databaseIds)
    {
        EditorGUILayout.LabelField("Tools", EditorStyles.boldLabel);

        using (new EditorGUI.DisabledScope(databaseIds.Count == 0))
        {
            if (GUILayout.Button("Sync ItemIconOnly Mappings From Database"))
            {
                int updated = SyncItemMappingsFromDatabase(databaseIds);
                Debug.Log($"ItemAtlasDataSO: {updated} mappings sincronizados com entryIds do database.", target);
            }
        }

        if (GUILayout.Button("Sort And Remove Null/Duplicate Mappings"))
        {
            int removed = SortAndRemoveInvalidMappings();
            Debug.Log($"ItemAtlasDataSO: limpeza concluida. {removed} mappings removidos.", target);
        }
    }

    private void DrawMappingsSection()
    {
        EditorGUILayout.LabelField("Mappings", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(itemMappingsProp, includeChildren: true);
    }

    private void DrawValidationSection(HashSet<string> databaseIds)
    {
        List<string> issues = new List<string>();
        int legacyFallbackCount = 0;

        for (int i = 0; i < itemMappingsProp.arraySize; i++)
        {
            SerializedProperty mappingProp = itemMappingsProp.GetArrayElementAtIndex(i);
            SerializedProperty itemProp = mappingProp.FindPropertyRelative("item");
            SerializedProperty entryIdProp = mappingProp.FindPropertyRelative("entryId");

            Item item = itemProp.objectReferenceValue as Item;
            if (item == null)
            {
                issues.Add($"Elemento {i}: item ausente.");
                continue;
            }

            string entryId = ItemAtlasEntryIdUtility.SanitizeEntryId(entryIdProp.stringValue);
            if (string.IsNullOrEmpty(entryId))
            {
                if (TryFindSuggestedEntryId(item, databaseIds, out string suggestedEntryId))
                {
                    issues.Add($"{item.name}: sem entryId. Sugestao: {suggestedEntryId}.");
                }
                else
                {
                    issues.Add($"{item.name}: sem entryId valido. Vai cair no fallback legado por tile.");
                }

                legacyFallbackCount++;
                continue;
            }

            if (databaseIds.Count > 0 && !databaseIds.Contains(entryId))
                issues.Add($"{item.name}: entryId '{entryId}' nao existe no TextureAtlasDatabaseSO.");
        }

        if (legacyFallbackCount == 0 && issues.Count == 0)
        {
            EditorGUILayout.HelpBox("Todos os mappings atuais estao resolvendo por entryId.", MessageType.Info);
            return;
        }

        if (legacyFallbackCount > 0)
        {
            EditorGUILayout.HelpBox(
                $"{legacyFallbackCount} mapping(s) ainda dependem de fallback legado. O ideal e preencher entryId em todos.",
                MessageType.Warning);
        }

        if (issues.Count == 0)
            return;

        EditorGUILayout.LabelField("Validation", EditorStyles.boldLabel);
        for (int i = 0; i < issues.Count; i++)
            EditorGUILayout.HelpBox(issues[i], MessageType.None);
    }

    private int SyncItemMappingsFromDatabase(HashSet<string> databaseIds)
    {
        if (databaseIds == null || databaseIds.Count == 0)
            return 0;

        Dictionary<Item, SerializedProperty> existingMappings = new Dictionary<Item, SerializedProperty>();
        for (int i = 0; i < itemMappingsProp.arraySize; i++)
        {
            SerializedProperty mappingProp = itemMappingsProp.GetArrayElementAtIndex(i);
            Item mappedItem = mappingProp.FindPropertyRelative("item").objectReferenceValue as Item;
            if (mappedItem == null || existingMappings.ContainsKey(mappedItem))
                continue;

            existingMappings[mappedItem] = mappingProp;
        }

        int changedCount = 0;
        string[] guids = AssetDatabase.FindAssets("t:Item");
        for (int i = 0; i < guids.Length; i++)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
            Item item = AssetDatabase.LoadAssetAtPath<Item>(assetPath);
            if (item == null || item.inventoryIconMode != InventoryIconMode.ItemIconOnly)
                continue;

            if (!TryFindSuggestedEntryId(item, databaseIds, out string suggestedEntryId))
                continue;

            if (!existingMappings.TryGetValue(item, out SerializedProperty mappingProp))
            {
                itemMappingsProp.arraySize++;
                mappingProp = itemMappingsProp.GetArrayElementAtIndex(itemMappingsProp.arraySize - 1);
                mappingProp.FindPropertyRelative("item").objectReferenceValue = item;
                existingMappings[item] = mappingProp;
                changedCount++;
            }

            SerializedProperty entryIdProp = mappingProp.FindPropertyRelative("entryId");
            if (!string.Equals(entryIdProp.stringValue, suggestedEntryId, StringComparison.Ordinal))
            {
                entryIdProp.stringValue = suggestedEntryId;
                changedCount++;
            }
        }

        if (changedCount > 0)
            MarkDirty();

        return changedCount;
    }

    private int SortAndRemoveInvalidMappings()
    {
        List<ItemAtlasMapping> cleanedMappings = new List<ItemAtlasMapping>();
        HashSet<Item> seenItems = new HashSet<Item>();
        int removedCount = 0;

        ItemAtlasDataSO atlasData = (ItemAtlasDataSO)target;
        for (int i = 0; i < atlasData.itemMappings.Count; i++)
        {
            ItemAtlasMapping mapping = atlasData.itemMappings[i];
            if (mapping.item == null || !seenItems.Add(mapping.item))
            {
                removedCount++;
                continue;
            }

            cleanedMappings.Add(mapping);
        }

        cleanedMappings.Sort(CompareMappings);
        atlasData.itemMappings = cleanedMappings;

        EditorUtility.SetDirty(atlasData);
        serializedObject.Update();
        return removedCount;
    }

    private void MarkDirty()
    {
        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
        serializedObject.Update();
    }

    private static int CompareMappings(ItemAtlasMapping a, ItemAtlasMapping b)
    {
        string nameA = a.item != null ? a.item.name : string.Empty;
        string nameB = b.item != null ? b.item.name : string.Empty;
        return string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> BuildDatabaseEntryIdSet(TextureAtlasDatabaseSO database)
    {
        HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
        if (database == null || database.entries == null)
            return ids;

        for (int i = 0; i < database.entries.Count; i++)
        {
            AtlasTextureEntry entry = database.entries[i];
            if (entry == null || string.IsNullOrWhiteSpace(entry.id))
                continue;

            ids.Add(entry.id.Trim());
        }

        return ids;
    }

    private static bool TryFindSuggestedEntryId(Item item, HashSet<string> databaseIds, out string entryId)
    {
        entryId = string.Empty;
        if (item == null || databaseIds == null || databaseIds.Count == 0)
            return false;

        if (ItemAtlasEntryIdUtility.TryBuildDefaultEntryId(item, out entryId) && databaseIds.Contains(entryId))
            return true;

        entryId = string.Empty;
        return false;
    }
}
