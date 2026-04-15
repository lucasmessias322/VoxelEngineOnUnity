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

        if (generator.textureDatabase == null)
        {
            EditorGUILayout.HelpBox(
                "Para escalar a configuracao do atlas, crie um TextureAtlasDatabaseSO e atribua no campo Entries Database.",
                MessageType.Info);
        }
        else if (generator.HasEmbeddedTextureEntries())
        {
            EditorGUILayout.HelpBox(
                "Este componente ainda possui textureEntries embutidos. O atlas prioriza a database; mova essas entradas para centralizar os dados e evitar duplicidades.",
                MessageType.Warning);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Gerar Atlas", EditorStyles.boldLabel);

        if (generator.textureDatabase != null && generator.HasEmbeddedTextureEntries())
        {
            if (GUILayout.Button("Mover Lista Embutida -> Database"))
            {
                RecordGeneratorTargets(generator, "Move Embedded textureEntries To Database");
                generator.MoveEmbeddedTextureEntriesToDatabase();
                MarkGeneratorTargetsDirty(generator);
            }
        }

        if (GUILayout.Button("Aplicar Preset Minecraft Moderno"))
        {
            RecordGeneratorTargets(generator, "Apply Modern Minecraft Atlas Preset");
            generator.ApplyModernMinecraftPreset();
            MarkGeneratorTargetsDirty(generator);
        }

        if (GUILayout.Button("Gerar Agora"))
        {
            generator.GenerateAtlas();
        }
    }

    private static void RecordGeneratorTargets(TextureAtlasGenerator generator, string actionName)
    {
        if (generator == null)
            return;

        if (generator.textureDatabase != null)
            Undo.RecordObjects(new UnityEngine.Object[] { generator, generator.textureDatabase }, actionName);
        else
            Undo.RecordObject(generator, actionName);
    }

    private static void MarkGeneratorTargetsDirty(TextureAtlasGenerator generator)
    {
        if (generator == null)
            return;

        EditorUtility.SetDirty(generator);
        if (generator.textureDatabase != null)
            EditorUtility.SetDirty(generator.textureDatabase);
    }
}
#endif
