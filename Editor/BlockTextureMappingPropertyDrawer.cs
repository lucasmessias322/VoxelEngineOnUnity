using System;
using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(BlockTextureMapping))]
public sealed class BlockTextureMappingPropertyDrawer : PropertyDrawer
{
    private struct EntryIdField
    {
        public readonly BlockFace Face;
        public readonly string Label;

        public EntryIdField(BlockFace face, string label)
        {
            Face = face;
            Label = label;
        }
    }

    private static readonly EntryIdField[] EntryIdFields =
    {
        new EntryIdField(BlockFace.Top, "Top entryId"),
        new EntryIdField(BlockFace.Bottom, "Bottom entryId"),
        new EntryIdField(BlockFace.Right, "Right +X entryId"),
        new EntryIdField(BlockFace.Left, "Left -X entryId"),
        new EntryIdField(BlockFace.Front, "Front +Z entryId"),
        new EntryIdField(BlockFace.Back, "Back -Z entryId")
    };

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float line = EditorGUIUtility.singleLineHeight;
        if (!property.isExpanded)
            return line;

        float spacing = EditorGUIUtility.standardVerticalSpacing;
        float height = line + spacing;
        bool drewEntryIds = false;

        SerializedProperty iterator = property.Copy();
        SerializedProperty endProperty = iterator.GetEndProperty();
        bool enterChildren = true;

        while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, endProperty))
        {
            enterChildren = false;
            if (iterator.depth != property.depth + 1)
                continue;

            SerializedProperty child = iterator.Copy();
            if (ShouldHideChild(child))
                continue;

            height += EditorGUI.GetPropertyHeight(child, true) + spacing;
            if (!drewEntryIds && child.name == "blockType")
            {
                height += GetEntryIdSectionHeight(line, spacing);
                drewEntryIds = true;
            }
        }

        if (!drewEntryIds)
            height += GetEntryIdSectionHeight(line, spacing);

        return height;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);

        float line = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;
        float y = position.y;

        GUIContent displayLabel = new GUIContent(GetDisplayName(property, label), label.tooltip);
        Rect foldoutRect = new Rect(position.x, y, position.width, line);
        property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, displayLabel, true);

        if (!property.isExpanded)
        {
            EditorGUI.EndProperty();
            return;
        }

        EditorGUI.indentLevel++;
        y += line + spacing;

        SerializedProperty iterator = property.Copy();
        SerializedProperty endProperty = iterator.GetEndProperty();
        bool enterChildren = true;
        bool drewEntryIds = false;

        while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, endProperty))
        {
            enterChildren = false;
            if (iterator.depth != property.depth + 1)
                continue;

            SerializedProperty child = iterator.Copy();
            if (ShouldHideChild(child))
                continue;

            float childHeight = EditorGUI.GetPropertyHeight(child, true);
            Rect childRect = new Rect(position.x, y, position.width, childHeight);

            if (child.name == "blockType")
            {
                EditorGUI.BeginChangeCheck();
                EditorGUI.PropertyField(childRect, child, true);
                if (EditorGUI.EndChangeCheck())
                    RefreshAtlasCompatibility(property);
            }
            else
            {
                EditorGUI.PropertyField(childRect, child, true);
            }

            y += childHeight + spacing;

            if (!drewEntryIds && child.name == "blockType")
            {
                y = DrawEntryIdSection(new Rect(position.x, y, position.width, position.height - (y - position.y)), property, line, spacing);
                drewEntryIds = true;
            }
        }

        if (!drewEntryIds)
            DrawEntryIdSection(new Rect(position.x, y, position.width, position.height - (y - position.y)), property, line, spacing);

        EditorGUI.indentLevel--;
        EditorGUI.EndProperty();
    }

    private static bool ShouldHideChild(SerializedProperty child)
    {
        switch (child.name)
        {
            case "top":
            case "bottom":
            case "right":
            case "left":
            case "front":
            case "back":
            case "side":
                return true;
            default:
                return false;
        }
    }

    private static float GetEntryIdSectionHeight(float line, float spacing)
    {
        return (EntryIdFields.Length + 1) * (line + spacing);
    }

    private static float DrawEntryIdSection(Rect position, SerializedProperty property, float line, float spacing)
    {
        float y = position.y;
        Rect labelRect = new Rect(position.x, y, position.width, line);
        EditorGUI.LabelField(labelRect, "Face Entry IDs", EditorStyles.miniBoldLabel);
        y += line + spacing;

        BlockDataSO blockData = property.serializedObject.targetObject as BlockDataSO;
        BlockType blockType = GetBlockType(property);

        for (int i = 0; i < EntryIdFields.Length; i++)
        {
            EntryIdField field = EntryIdFields[i];
            Rect fieldRect = new Rect(position.x, y, position.width, line);
            string currentValue = GetEntryIdValue(blockData, blockType, field.Face);

            EditorGUI.BeginChangeCheck();
            string updatedValue = EditorGUI.DelayedTextField(fieldRect, field.Label, currentValue);
            if (EditorGUI.EndChangeCheck() && blockData != null)
            {
                Undo.RecordObject(blockData, "Edit Block Texture Entry IDs");
                blockData.SetTextureEntryId(blockType, field.Face, updatedValue);
                RefreshAtlasCompatibility(property);
                EditorUtility.SetDirty(blockData);
                property.serializedObject.Update();
            }

            y += line + spacing;
        }

        return y;
    }

    private static string GetEntryIdValue(BlockDataSO blockData, BlockType blockType, BlockFace face)
    {
        return blockData != null && blockData.TryGetTextureEntryId(blockType, face, out string entryId)
            ? entryId
            : string.Empty;
    }

    private static BlockType GetBlockType(SerializedProperty property)
    {
        SerializedProperty blockTypeProperty = property.FindPropertyRelative("blockType");
        if (blockTypeProperty == null)
            return BlockType.Air;

        return (BlockType)blockTypeProperty.intValue;
    }

    private static string GetDisplayName(SerializedProperty property, GUIContent fallbackLabel)
    {
        string enumName = Enum.GetName(typeof(BlockType), (int)GetBlockType(property));
        return string.IsNullOrEmpty(enumName) ? fallbackLabel.text : enumName;
    }

    private static void RefreshAtlasCompatibility(SerializedProperty property)
    {
        property.serializedObject.ApplyModifiedProperties();

        BlockDataSO blockData = property.serializedObject.targetObject as BlockDataSO;
        if (blockData == null)
            return;

        TextureAtlasGenerator generator = ResolveLoadedAtlasGenerator();
        if (generator == null)
        {
            blockData.InitializeDictionary();
            return;
        }

        if ((generator.GeneratedAtlas == null || generator.UvMap.Count == 0))
            generator.GenerateAtlas();

        VoxelAtlasCompatibility.Apply(
            generator,
            blockData,
            GetAtlasTileGridSize(blockData),
            blockData.atlasCoordinatesStartTopLeft);
    }

    private static TextureAtlasGenerator ResolveLoadedAtlasGenerator()
    {
#if UNITY_2023_1_OR_NEWER
        TextureAtlasGenerator[] generators = UnityEngine.Object.FindObjectsByType<TextureAtlasGenerator>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        TextureAtlasGenerator[] generators = UnityEngine.Object.FindObjectsOfType<TextureAtlasGenerator>(true);
#endif
        return generators != null && generators.Length > 0 ? generators[0] : null;
    }

    private static Vector2Int GetAtlasTileGridSize(BlockDataSO blockData)
    {
        if (blockData == null)
            return Vector2Int.one;

        return new Vector2Int(
            Mathf.Max(1, Mathf.RoundToInt(blockData.atlasSize.x)),
            Mathf.Max(1, Mathf.RoundToInt(blockData.atlasSize.y)));
    }
}
