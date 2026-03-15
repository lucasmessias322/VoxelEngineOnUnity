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

        if (GUILayout.Button("Gerar Agora"))
        {
            generator.GenerateAtlas();
        }
    }
}
