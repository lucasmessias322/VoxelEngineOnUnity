using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(TerrainDensitySettings))]
public sealed class TerrainDensitySettingsPropertyDrawer : PropertyDrawer
{
    private static readonly string[] CoreFields =
    {
        "enabled",
        "verticalSampleStep",
        "solidThreshold",
        "surfaceSearchHeight",
        "baseSolidBias"
    };

    private static readonly string[] MassNoiseFields =
    {
        "massNoiseHorizontalScale",
        "massNoiseVerticalScale",
        "massNoiseOctaves",
        "massNoisePersistence",
        "massNoiseLacunarity",
        "massNoiseAmplitude"
    };

    private static readonly string[] RidgeNoiseFields =
    {
        "ridgeNoiseHorizontalScale",
        "ridgeNoiseVerticalScale",
        "ridgeNoiseOctaves",
        "ridgeNoisePersistence",
        "ridgeNoiseLacunarity",
        "ridgeNoiseAmplitude"
    };

    private static readonly string[] CaveCarveFields =
    {
        "caveNoiseHorizontalScale",
        "caveNoiseVerticalScale",
        "caveNoiseOctaves",
        "caveNoisePersistence",
        "caveNoiseLacunarity",
        "caveWarpScale",
        "caveWarpAmplitudeXZ",
        "caveWarpAmplitudeY",
        "caveThreshold",
        "caveSharpness",
        "caveCarveStrength",
        "caveBottomFadeStartY",
        "caveBottomFadeRange",
        "caveTopFadeStartY",
        "caveTopFadeRange"
    };

    private static readonly string[] VerticalSlideFields =
    {
        "bottomSlideStartY",
        "bottomSlideEndY",
        "bottomSlideFromValue",
        "bottomSlideToValue",
        "topSlideRange",
        "topSlideFromValue",
        "topSlideToValue"
    };

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float line = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;
        float height = line;

        if (!property.isExpanded)
            return height;

        height += spacing;
        height += GetGroupHeight(property, "Core", CoreFields, line, spacing);
        height += GetGroupHeight(property, "Mass Noise", MassNoiseFields, line, spacing);
        height += GetGroupHeight(property, "Ridge Noise", RidgeNoiseFields, line, spacing);
        height += GetGroupHeight(property, "Cave Carve", CaveCarveFields, line, spacing);
        height += GetGroupHeight(property, "Vertical Slides", VerticalSlideFields, line, spacing);

        return height;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);

        float line = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;
        float y = position.y;

        Rect foldoutRect = new Rect(position.x, y, position.width, line);
        property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, label, true);

        if (!property.isExpanded)
        {
            EditorGUI.EndProperty();
            return;
        }

        EditorGUI.indentLevel++;
        y += line + spacing;

        DrawGroup(position.x, position.width, ref y, line, spacing, property, "Core", CoreFields);
        DrawGroup(position.x, position.width, ref y, line, spacing, property, "Mass Noise", MassNoiseFields);
        DrawGroup(position.x, position.width, ref y, line, spacing, property, "Ridge Noise", RidgeNoiseFields);
        DrawGroup(position.x, position.width, ref y, line, spacing, property, "Cave Carve", CaveCarveFields);
        DrawGroup(position.x, position.width, ref y, line, spacing, property, "Vertical Slides", VerticalSlideFields);

        EditorGUI.indentLevel--;
        EditorGUI.EndProperty();
    }

    private static float GetGroupHeight(
        SerializedProperty parent,
        string groupLabel,
        string[] fieldNames,
        float line,
        float spacing)
    {
        float height = line + spacing; // Group title

        for (int i = 0; i < fieldNames.Length; i++)
        {
            SerializedProperty field = parent.FindPropertyRelative(fieldNames[i]);
            if (field == null)
                continue;

            height += EditorGUI.GetPropertyHeight(field, true) + spacing;
        }

        return height;
    }

    private static void DrawGroup(
        float x,
        float width,
        ref float y,
        float line,
        float spacing,
        SerializedProperty parent,
        string groupLabel,
        string[] fieldNames)
    {
        Rect titleRect = new Rect(x, y, width, line);
        EditorGUI.LabelField(titleRect, groupLabel, EditorStyles.boldLabel);
        y += line + spacing;

        for (int i = 0; i < fieldNames.Length; i++)
        {
            SerializedProperty field = parent.FindPropertyRelative(fieldNames[i]);
            if (field == null)
                continue;

            float fieldHeight = EditorGUI.GetPropertyHeight(field, true);
            Rect fieldRect = new Rect(x, y, width, fieldHeight);
            EditorGUI.PropertyField(fieldRect, field, true);
            y += fieldHeight + spacing;
        }
    }
}
