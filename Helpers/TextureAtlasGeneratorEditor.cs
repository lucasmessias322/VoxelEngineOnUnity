#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(TextureAtlasGenerator))]
public class TextureAtlasGeneratorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        TextureAtlasGenerator generator = (TextureAtlasGenerator)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Gerar Atlas", EditorStyles.boldLabel);

        if (GUILayout.Button("Migrar Legacy -> textureEntries"))
        {
            Undo.RecordObject(generator, "Fill textureEntries From Legacy");
            generator.FillTextureEntriesFromLegacy();
            EditorUtility.SetDirty(generator);
        }

        if (GUILayout.Button("Aplicar Preset Minecraft Moderno"))
        {
            Undo.RecordObject(generator, "Apply Modern Minecraft Atlas Preset");
            generator.ApplyModernMinecraftPreset();
            EditorUtility.SetDirty(generator);
        }

        if (GUILayout.Button("Gerar Agora"))
        {
            generator.GenerateAtlas();
        }
    }
}
#endif
