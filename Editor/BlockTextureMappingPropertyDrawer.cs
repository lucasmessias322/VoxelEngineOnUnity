using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(BlockTextureMapping))]
public sealed class BlockTextureMappingPropertyDrawer : PropertyDrawer
{
    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float line = EditorGUIUtility.singleLineHeight;
        if (!property.isExpanded)
            return line;

        float spacing = EditorGUIUtility.standardVerticalSpacing;
        float height = line + spacing;

        SerializedProperty iterator = property.Copy();
        SerializedProperty endProperty = iterator.GetEndProperty();
        bool enterChildren = true;

        while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, endProperty))
        {
            enterChildren = false;
            if (iterator.depth != property.depth + 1)
                continue;

            SerializedProperty child = iterator.Copy();
            height += EditorGUI.GetPropertyHeight(child, true) + spacing;
        }

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

        while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, endProperty))
        {
            enterChildren = false;
            if (iterator.depth != property.depth + 1)
                continue;

            SerializedProperty child = iterator.Copy();
            float childHeight = EditorGUI.GetPropertyHeight(child, true);
            Rect childRect = new Rect(position.x, y, position.width, childHeight);
            EditorGUI.PropertyField(childRect, child, true);
            y += childHeight + spacing;
        }

        EditorGUI.indentLevel--;
        EditorGUI.EndProperty();
    }

    private static string GetDisplayName(SerializedProperty property, GUIContent fallbackLabel)
    {
        SerializedProperty blockTypeProperty = property.FindPropertyRelative("blockType");
        if (blockTypeProperty != null && blockTypeProperty.propertyType == SerializedPropertyType.Enum)
        {
            int enumIndex = blockTypeProperty.enumValueIndex;
            if (enumIndex >= 0 && enumIndex < blockTypeProperty.enumNames.Length)
                return blockTypeProperty.enumNames[enumIndex];
        }

        return fallbackLabel.text;
    }
}
