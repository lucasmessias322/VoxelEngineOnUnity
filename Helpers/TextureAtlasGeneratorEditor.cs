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

        if (generator.generateMipmaps && !generator.saveToFile)
        {
            EditorGUILayout.HelpBox(
                "As configuracoes avancadas de mipmap do atlas salvo (Box, Preserve Coverage, Alpha Cutoff e Replicate Border) so entram em efeito quando Save To File estiver ativo.",
                MessageType.Info);
        }

        if (generator.generateMipmaps && generator.generateMipmapsPerTile)
        {
            EditorGUILayout.HelpBox(
                "Generate Mipmaps Per Tile ainda esta experimental neste projeto. Se aparecer grade escura ou artefatos, desligue essa opcao e aumente o paddingPixels do atlas.",
                MessageType.Warning);
        }

        if (generator.generateMipmaps &&
            generator.saveToFile &&
            generator.preserveMipMapCoverage &&
            generator.mipMapAlphaCutoff > 0.7f)
        {
            EditorGUILayout.HelpBox(
                "Mip Map Alpha Cutoff muito alto pode deixar folhas e outros materiais alpha-clip agressivos demais a distancia. Para foliage, prefira algo perto do cutoff do shader, normalmente entre 0.4 e 0.6.",
                MessageType.Warning);
        }

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
            RecordGeneratorTargets(generator, "Generate Texture Atlas");
            generator.GenerateAtlas();
            MarkGeneratorTargetsDirty(generator);
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
