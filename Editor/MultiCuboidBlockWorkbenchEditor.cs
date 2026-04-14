using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Plastic.Newtonsoft.Json.Linq;

[CustomEditor(typeof(MultiCuboidBlockWorkbench))]
public sealed class MultiCuboidBlockWorkbenchEditor : Editor
{
    private SerializedProperty blockDataProperty;
    private SerializedProperty blockTypeProperty;
    private SerializedProperty snapToGridProperty;
    private SerializedProperty snapStepProperty;
    private SerializedProperty syncContinuouslyProperty;
    private SerializedProperty showVoxelBoundsProperty;
    private SerializedProperty showHorizontalGridProperty;
    private SerializedProperty showVerticalGridProperty;
    private SerializedProperty voxelBoundsColorProperty;
    private SerializedProperty selectedBoundsColorProperty;
    private SerializedProperty showPlacementRotationPreviewProperty;
    private SerializedProperty placementPreviewAxisProperty;
    private SerializedProperty placementPreviewColorProperty;

    private void OnEnable()
    {
        blockDataProperty = serializedObject.FindProperty("blockData");
        blockTypeProperty = serializedObject.FindProperty("blockType");
        snapToGridProperty = serializedObject.FindProperty("snapToGrid");
        snapStepProperty = serializedObject.FindProperty("snapStep");
        syncContinuouslyProperty = serializedObject.FindProperty("syncContinuously");
        showVoxelBoundsProperty = serializedObject.FindProperty("showVoxelBounds");
        showHorizontalGridProperty = serializedObject.FindProperty("showHorizontalGrid");
        showVerticalGridProperty = serializedObject.FindProperty("showVerticalGrid");
        voxelBoundsColorProperty = serializedObject.FindProperty("voxelBoundsColor");
        selectedBoundsColorProperty = serializedObject.FindProperty("selectedBoundsColor");
        showPlacementRotationPreviewProperty = serializedObject.FindProperty("showPlacementRotationPreview");
        placementPreviewAxisProperty = serializedObject.FindProperty("placementPreviewAxis");
        placementPreviewColorProperty = serializedObject.FindProperty("placementPreviewColor");
    }

    public override void OnInspectorGUI()
    {
        MultiCuboidBlockWorkbench workbench = (MultiCuboidBlockWorkbench)target;

        serializedObject.Update();
        EditorGUILayout.LabelField("Multi Cuboid Workbench", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Edite os cuboides direto no Scene View. Selecione um Cuboid_XX e use Move/Rotate/Scale da Unity; a bancada converte isso para coordenadas 0..1 do bloco.",
            MessageType.Info);

        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(blockDataProperty, new GUIContent("BlockDataSO"));
        EditorGUILayout.PropertyField(blockTypeProperty, new GUIContent("Bloco"));
        EditorGUILayout.Space(4f);
        EditorGUILayout.PropertyField(snapToGridProperty, new GUIContent("Snap 1/16"));
        EditorGUILayout.PropertyField(snapStepProperty, new GUIContent("Passo do snap"));
        EditorGUILayout.PropertyField(syncContinuouslyProperty, new GUIContent("Sincronizar ao mover"));
        EditorGUILayout.Space(4f);
        EditorGUILayout.PropertyField(showVoxelBoundsProperty, new GUIContent("Mostrar limite 1x1"));
        EditorGUILayout.PropertyField(showHorizontalGridProperty, new GUIContent("Grid horizontal"));
        EditorGUILayout.PropertyField(showVerticalGridProperty, new GUIContent("Grid vertical"));
        EditorGUILayout.PropertyField(voxelBoundsColorProperty, new GUIContent("Cor limite"));
        EditorGUILayout.PropertyField(selectedBoundsColorProperty, new GUIContent("Cor selecionado"));
        bool settingsChanged = EditorGUI.EndChangeCheck();

        serializedObject.ApplyModifiedProperties();

        if (settingsChanged)
        {
            workbench.SyncFromChildren();
            workbench.RebuildVisuals();
            MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
        }

        DrawAssetButtons(workbench);
        DrawTextureControls(workbench);
        DrawPlacementRotationControls(workbench);
        DrawEditButtons(workbench);
        DrawStatus(workbench);

        int selectedIndex = workbench.GetSelectedCuboidIndex();
        if (selectedIndex >= 0)
            MultiCuboidWorkbenchEditorGui.DrawCuboidFields(workbench, selectedIndex);
        else
            EditorGUILayout.HelpBox("Selecione um Cuboid_XX na Hierarchy ou no Scene View para editar valores numericos e faces.", MessageType.None);
    }

    private void OnSceneGUI()
    {
        MultiCuboidWorkbenchEditorGui.DrawSceneOverlay((MultiCuboidBlockWorkbench)target);
    }

    private static void DrawAssetButtons(MultiCuboidBlockWorkbench workbench)
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Arquivo do bloco", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(workbench.blockData == null))
            {
                if (GUILayout.Button("Load do BlockData"))
                {
                    Undo.RecordObject(workbench, "Load Multi Cuboid Workbench");
                    bool found = workbench.LoadFromBlockData();
                    MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
                    if (!found)
                        Debug.LogWarning($"Nenhum modelo MultiCuboid encontrado para {workbench.blockType}. A bancada ficou vazia para voce criar um novo.");
                }

                if (GUILayout.Button("Save no BlockData"))
                {
                    workbench.SaveToBlockData();
                    MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
                }
            }

            if (GUILayout.Button("Importar Blockbench"))
            {
                if (BlockbenchMultiCuboidImporter.ImportIntoWorkbench(workbench))
                    MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
            }
        }

        if (workbench.blockData == null)
            EditorGUILayout.HelpBox("Arraste o asset BlockDataSO aqui para carregar/salvar um bloco real da engine. Sem ele, a importacao do Blockbench ainda traz a geometria, mas nao consegue gravar o mapping base do atlas.", MessageType.Warning);
    }

    private static void DrawTextureControls(MultiCuboidBlockWorkbench workbench)
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Texturas", EditorStyles.boldLabel);

        if (workbench.blockData == null)
        {
            EditorGUILayout.HelpBox("Escolha um BlockDataSO para editar as coordenadas do atlas deste bloco.", MessageType.None);
            return;
        }

        Vector2 atlasSize = workbench.blockData.atlasSize;
        string origin = workbench.blockData.atlasCoordinatesStartTopLeft ? "origem no topo esquerdo" : "origem embaixo esquerdo";
        EditorGUILayout.LabelField($"Atlas: {Mathf.RoundToInt(atlasSize.x)} x {Mathf.RoundToInt(atlasSize.y)} tiles ({origin})", EditorStyles.miniLabel);

        if (!workbench.TryGetTextureMapping(out BlockTextureMapping mapping))
        {
            EditorGUILayout.HelpBox("Este BlockType ainda nao tem mapping de textura. Crie um mapping para escolher as coordenadas usadas no atlas.", MessageType.Warning);
            if (GUILayout.Button("Criar mapping de textura"))
            {
                mapping = workbench.GetOrCreateTextureMapping();
                SaveTextureMapping(workbench, mapping);
            }

            return;
        }

        EditorGUI.BeginChangeCheck();
        int materialIndex = EditorGUILayout.IntField("Material Index", mapping.materialIndex);
        Vector2Int top = EditorGUILayout.Vector2IntField("Top", mapping.top);
        Vector2Int bottom = EditorGUILayout.Vector2IntField("Bottom", mapping.bottom);
        Vector2Int right = EditorGUILayout.Vector2IntField("Right +X", mapping.right);
        Vector2Int left = EditorGUILayout.Vector2IntField("Left -X", mapping.left);
        Vector2Int front = EditorGUILayout.Vector2IntField("Front +Z", mapping.front);
        Vector2Int back = EditorGUILayout.Vector2IntField("Back -Z", mapping.back);
        bool changed = EditorGUI.EndChangeCheck();

        if (changed)
        {
            mapping.materialIndex = Mathf.Max(0, materialIndex);
            mapping.top = ClampTile(workbench, top);
            mapping.bottom = ClampTile(workbench, bottom);
            mapping.right = ClampTile(workbench, right);
            mapping.left = ClampTile(workbench, left);
            mapping.front = ClampTile(workbench, front);
            mapping.back = ClampTile(workbench, back);
            SaveTextureMapping(workbench, mapping);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Top em todas"))
            {
                Vector2Int tile = ClampTile(workbench, mapping.top);
                mapping.top = tile;
                mapping.bottom = tile;
                mapping.right = tile;
                mapping.left = tile;
                mapping.front = tile;
                mapping.back = tile;
                SaveTextureMapping(workbench, mapping);
            }

            if (GUILayout.Button("Front nas laterais"))
            {
                Vector2Int tile = ClampTile(workbench, mapping.front);
                mapping.right = tile;
                mapping.left = tile;
                mapping.front = tile;
                mapping.back = tile;
                SaveTextureMapping(workbench, mapping);
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Bottom = Top"))
            {
                mapping.bottom = ClampTile(workbench, mapping.top);
                SaveTextureMapping(workbench, mapping);
            }

            if (GUILayout.Button("Zerar coordenadas"))
            {
                mapping.top = Vector2Int.zero;
                mapping.bottom = Vector2Int.zero;
                mapping.right = Vector2Int.zero;
                mapping.left = Vector2Int.zero;
                mapping.front = Vector2Int.zero;
                mapping.back = Vector2Int.zero;
                SaveTextureMapping(workbench, mapping);
            }
        }
    }

    private void DrawPlacementRotationControls(MultiCuboidBlockWorkbench workbench)
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Rotação ao colocar", EditorStyles.boldLabel);

        if (workbench.blockData == null)
        {
            EditorGUILayout.HelpBox("Escolha um BlockDataSO para salvar a rotação deste bloco.", MessageType.None);
            return;
        }

        if (!workbench.TryGetTextureMapping(out BlockTextureMapping mapping))
        {
            EditorGUILayout.HelpBox("Crie um mapping de textura para ativar rotação neste bloco.", MessageType.None);
            if (GUILayout.Button("Criar mapping com rotação horizontal"))
            {
                mapping = workbench.GetOrCreateTextureMapping();
                mapping.usePlacementAxisRotation = true;
                mapping.placementRotationAxes = BlockPlacementRotationAxes.Horizontal;
                SaveTextureMapping(workbench, mapping);
            }

            return;
        }

        int mode = ResolvePlacementRotationMode(mapping);
        EditorGUI.BeginChangeCheck();
        int selectedMode = EditorGUILayout.Popup(
            "Modo",
            mode,
            new[]
            {
                "Desligada",
                "Horizontal 4 lados",
                "Face clicada (avancado)"
            });
        bool changed = EditorGUI.EndChangeCheck();

        if (changed)
        {
            ApplyPlacementRotationMode(ref mapping, selectedMode);
            SaveTextureMapping(workbench, mapping);
        }

        if (selectedMode == 1)
            EditorGUILayout.HelpBox("Horizontal 4 lados usa a direcao da camera na hora de colocar. E o modo certo para bigorna, bau, fornalha e blocos assimetricos no chao.", MessageType.Info);
        else if (selectedMode == 2)
            EditorGUILayout.HelpBox("Face clicada usa a face alvo como orientacao. Use para modelos que precisam de comportamento mais parecido com blocos presos em parede/teto.", MessageType.Warning);
        else
            EditorGUILayout.HelpBox("Com a rotacao desligada, todo bloco MultiCuboid fica sempre na orientacao original do editor.", MessageType.None);

        serializedObject.Update();
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(showPlacementRotationPreviewProperty, new GUIContent("Preview no Scene View"));
        using (new EditorGUI.DisabledScope(!workbench.showPlacementRotationPreview || selectedMode == 0))
        {
            EditorGUILayout.PropertyField(placementPreviewAxisProperty, new GUIContent("Direcao preview"));
            EditorGUILayout.PropertyField(placementPreviewColorProperty, new GUIContent("Cor preview"));
        }

        bool previewChanged = EditorGUI.EndChangeCheck();
        serializedObject.ApplyModifiedProperties();
        if (previewChanged)
            MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
    }

    private static void DrawEditButtons(MultiCuboidBlockWorkbench workbench)
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Modelagem", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Adicionar"))
            {
                workbench.AddCuboid();
                MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
            }

            if (GUILayout.Button("Duplicar selecionado"))
            {
                workbench.DuplicateSelectedCuboid();
                MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Remover selecionado"))
            {
                workbench.RemoveSelectedCuboid();
                MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
            }

            if (GUILayout.Button("Sincronizar cena"))
            {
                Undo.RecordObject(workbench, "Sync Multi Cuboid Workbench");
                workbench.SyncFromChildren();
                MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
            }
        }

        if (GUILayout.Button("Zerar modelo"))
        {
            bool clear = EditorUtility.DisplayDialog(
                "Zerar modelo Multi Cuboid",
                "Isso remove todos os cuboides da bancada. O BlockDataSO so muda quando voce clicar em Save no BlockData.",
                "Zerar",
                "Cancelar");

            if (clear)
            {
                workbench.ClearCuboids();
                MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Bigorna simples"))
            {
                workbench.ApplyAnvilPreset(false);
                MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
            }

            if (GUILayout.Button("Bigorna detalhada"))
            {
                workbench.ApplyAnvilPreset(true);
                MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
            }
        }

        if (GUILayout.Button("Recriar cubos visuais"))
        {
            Undo.RecordObject(workbench, "Rebuild Multi Cuboid Workbench Visuals");
            workbench.RebuildVisuals();
            MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
        }
    }

    private static void DrawStatus(MultiCuboidBlockWorkbench workbench)
    {
        EditorGUILayout.Space(8f);
        int selectedIndex = workbench.GetSelectedCuboidIndex();
        string selectedText = selectedIndex >= 0 ? $"selecionado #{selectedIndex + 1}" : "nenhum selecionado";
        EditorGUILayout.LabelField($"Cuboides: {workbench.CuboidCount} ({selectedText})", EditorStyles.miniBoldLabel);
    }

    private static void SaveTextureMapping(MultiCuboidBlockWorkbench workbench, BlockTextureMapping mapping)
    {
        mapping.top = ClampTile(workbench, mapping.top);
        mapping.bottom = ClampTile(workbench, mapping.bottom);
        mapping.right = ClampTile(workbench, mapping.right);
        mapping.left = ClampTile(workbench, mapping.left);
        mapping.front = ClampTile(workbench, mapping.front);
        mapping.back = ClampTile(workbench, mapping.back);
        mapping.side = mapping.right;
        mapping.materialIndex = Mathf.Max(0, mapping.materialIndex);

        workbench.SetTextureMapping(mapping);
        MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
    }

    private static int ResolvePlacementRotationMode(BlockTextureMapping mapping)
    {
        if (!mapping.usePlacementAxisRotation)
            return 0;

        return mapping.placementRotationAxes == BlockPlacementRotationAxes.Both ? 2 : 1;
    }

    private static void ApplyPlacementRotationMode(ref BlockTextureMapping mapping, int mode)
    {
        switch (mode)
        {
            case 1:
                mapping.usePlacementAxisRotation = true;
                mapping.placementRotationAxes = BlockPlacementRotationAxes.Horizontal;
                return;

            case 2:
                mapping.usePlacementAxisRotation = true;
                mapping.placementRotationAxes = BlockPlacementRotationAxes.Both;
                return;

            default:
                mapping.usePlacementAxisRotation = false;
                mapping.placementRotationAxes = BlockPlacementRotationAxes.Horizontal;
                return;
        }
    }

    internal static Vector2Int ClampTile(MultiCuboidBlockWorkbench workbench, Vector2Int tile)
    {
        if (workbench.blockData == null)
            return new Vector2Int(Mathf.Max(0, tile.x), Mathf.Max(0, tile.y));

        int atlasX = Mathf.RoundToInt(workbench.blockData.atlasSize.x);
        int atlasY = Mathf.RoundToInt(workbench.blockData.atlasSize.y);
        int x = atlasX > 0 ? Mathf.Clamp(tile.x, 0, atlasX - 1) : Mathf.Max(0, tile.x);
        int y = atlasY > 0 ? Mathf.Clamp(tile.y, 0, atlasY - 1) : Mathf.Max(0, tile.y);
        return new Vector2Int(x, y);
    }
}

[CustomEditor(typeof(MultiCuboidWorkbenchCuboid))]
public sealed class MultiCuboidWorkbenchCuboidEditor : Editor
{
    public override void OnInspectorGUI()
    {
        MultiCuboidWorkbenchCuboid marker = (MultiCuboidWorkbenchCuboid)target;
        MultiCuboidBlockWorkbench owner = marker.owner;

        if (owner == null)
        {
            EditorGUILayout.HelpBox("Este cuboide perdeu a referencia da bancada. Use Recriar cubos visuais no objeto Workbench.", MessageType.Warning);
            return;
        }

        owner.SyncFromChildren();
        EditorGUILayout.LabelField($"Cuboide {marker.index + 1}", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Selecionar bancada"))
                Selection.activeGameObject = owner.gameObject;

            if (GUILayout.Button("Salvar no BlockData"))
            {
                owner.SaveToBlockData();
                MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(owner);
            }
        }

        MultiCuboidWorkbenchEditorGui.DrawCuboidFields(owner, marker.index);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Duplicar"))
            {
                owner.SelectCuboid(marker.index);
                owner.DuplicateSelectedCuboid();
                MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(owner);
            }

            if (GUILayout.Button("Remover"))
            {
                owner.SelectCuboid(marker.index);
                owner.RemoveSelectedCuboid();
                MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(owner);
            }
        }
    }

    private void OnSceneGUI()
    {
        MultiCuboidWorkbenchCuboid marker = (MultiCuboidWorkbenchCuboid)target;
        if (marker.owner != null)
            MultiCuboidWorkbenchEditorGui.DrawSceneOverlay(marker.owner);
    }
}

internal static class MultiCuboidWorkbenchEditorGui
{
    public static void DrawCuboidFields(MultiCuboidBlockWorkbench workbench, int index)
    {
        if (!workbench.TryGetCuboid(index, out BlockModelCuboid cuboid))
            return;

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField($"Cuboide #{index + 1}", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        Vector3 min = EditorGUILayout.Vector3Field("Min 0..1", cuboid.min);
        Vector3 max = EditorGUILayout.Vector3Field("Max 0..1", cuboid.max);
        Vector3 rotation = EditorGUILayout.Vector3Field("Rotacao", cuboid.eulerRotation);
        BlockCuboidFaceMask faces = (BlockCuboidFaceMask)EditorGUILayout.EnumFlagsField("Faces", cuboid.EffectiveFaces);
        bool changed = EditorGUI.EndChangeCheck();

        DrawCuboidTextureFields(workbench, ref cuboid, ref changed);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Centralizar XZ"))
            {
                Undo.RecordObject(workbench, "Center Workbench Cuboid");
                Vector3 size = max - min;
                min.x = 0.5f - size.x * 0.5f;
                max.x = 0.5f + size.x * 0.5f;
                min.z = 0.5f - size.z * 0.5f;
                max.z = 0.5f + size.z * 0.5f;
                changed = true;
            }

            if (GUILayout.Button("Altura total"))
            {
                Undo.RecordObject(workbench, "Stretch Workbench Cuboid Height");
                min.y = 0f;
                max.y = 1f;
                changed = true;
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Zerar rotacao"))
            {
                rotation = Vector3.zero;
                changed = true;
            }

            if (GUILayout.Button("X +15"))
            {
                rotation.x += 15f;
                changed = true;
            }

            if (GUILayout.Button("X -15"))
            {
                rotation.x -= 15f;
                changed = true;
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Y +90"))
            {
                rotation.y += 90f;
                changed = true;
            }

            if (GUILayout.Button("Z +15"))
            {
                rotation.z += 15f;
                changed = true;
            }

            if (GUILayout.Button("Z -15"))
            {
                rotation.z -= 15f;
                changed = true;
            }
        }

        if (!changed)
            return;

        Undo.RecordObject(workbench, "Edit Workbench Cuboid");
        workbench.SetCuboid(index, new BlockModelCuboid
        {
            min = min,
            max = max,
            eulerRotation = BlockShapeUtility.NormalizeCuboidEulerRotation(rotation),
            faces = faces == BlockCuboidFaceMask.None ? BlockCuboidFaceMask.All : faces,
            textureOverrideFaces = cuboid.EffectiveTextureOverrideFaces,
           
            textureTop = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, cuboid.textureTop),
            textureBottom = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, cuboid.textureBottom),
            textureRight = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, cuboid.textureRight),
            textureLeft = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, cuboid.textureLeft),
            textureFront = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, cuboid.textureFront),
            textureBack = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, cuboid.textureBack),
           
        });
        MarkWorkbenchDirty(workbench);
    }

    private static void DrawCuboidTextureFields(MultiCuboidBlockWorkbench workbench, ref BlockModelCuboid cuboid, ref bool changed)
    {
        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Textura por face", EditorStyles.miniBoldLabel);

        if (workbench.blockData != null)
        {
            Vector2 atlasSize = workbench.blockData.atlasSize;
            EditorGUILayout.LabelField(
                $"Atlas: {Mathf.RoundToInt(atlasSize.x)} x {Mathf.RoundToInt(atlasSize.y)} tiles",
                EditorStyles.miniLabel);
        }

        EditorGUI.BeginChangeCheck();
        BlockCuboidFaceMask overrideFaces = (BlockCuboidFaceMask)EditorGUILayout.EnumFlagsField(
            "Faces com override",
            cuboid.EffectiveTextureOverrideFaces);
        bool fieldsChanged = EditorGUI.EndChangeCheck();
        if (fieldsChanged)
        {
            cuboid.textureOverrideFaces = overrideFaces & BlockCuboidFaceMask.All;
            changed = true;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Copiar do bloco"))
            {
                if (workbench.TryGetTextureMapping(out BlockTextureMapping mapping))
                {
                    cuboid.textureOverrideFaces = BlockCuboidFaceMask.All;
                    cuboid.textureTop = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, mapping.top);
                    cuboid.textureBottom = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, mapping.bottom);
                    cuboid.textureRight = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, mapping.right);
                    cuboid.textureLeft = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, mapping.left);
                    cuboid.textureFront = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, mapping.front);
                    cuboid.textureBack = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, mapping.back);
                    changed = true;
                }
            }

            if (GUILayout.Button("Limpar overrides"))
            {
                cuboid.textureOverrideFaces = BlockCuboidFaceMask.None;
                changed = true;
            }
        }

        using (new EditorGUI.DisabledScope(cuboid.EffectiveTextureOverrideFaces == BlockCuboidFaceMask.None))
        {
            EditorGUI.BeginChangeCheck();
            Vector2Int textureTop = EditorGUILayout.Vector2IntField("Top tile", cuboid.textureTop);
            Vector2Int textureBottom = EditorGUILayout.Vector2IntField("Bottom tile", cuboid.textureBottom);
            Vector2Int textureRight = EditorGUILayout.Vector2IntField("Right +X tile", cuboid.textureRight);
            Vector2Int textureLeft = EditorGUILayout.Vector2IntField("Left -X tile", cuboid.textureLeft);
            Vector2Int textureFront = EditorGUILayout.Vector2IntField("Front +Z tile", cuboid.textureFront);
            Vector2Int textureBack = EditorGUILayout.Vector2IntField("Back -Z tile", cuboid.textureBack);
            if (EditorGUI.EndChangeCheck())
            {
                cuboid.textureTop = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, textureTop);
                cuboid.textureBottom = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, textureBottom);
                cuboid.textureRight = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, textureRight);
                cuboid.textureLeft = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, textureLeft);
                cuboid.textureFront = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, textureFront);
                cuboid.textureBack = MultiCuboidBlockWorkbenchEditor.ClampTile(workbench, textureBack);
                changed = true;
            }
        }

        EditorGUILayout.HelpBox(
            "Faces marcadas usam o tile deste cuboide. Faces desmarcadas continuam usando o mapping global do bloco.",
            MessageType.None);
    }

    public static void DrawSceneOverlay(MultiCuboidBlockWorkbench workbench)
    {
        if (workbench == null)
            return;

        workbench.SyncFromChildren();

        Matrix4x4 previousMatrix = Handles.matrix;
        Color previousColor = Handles.color;
        CompareFunction previousZTest = Handles.zTest;
        Handles.matrix = workbench.transform.localToWorldMatrix;
        Handles.zTest = CompareFunction.Always;

        int selectedIndex = workbench.GetSelectedCuboidIndex();
        for (int i = 0; i < workbench.CuboidCount; i++)
        {
            if (!workbench.TryGetCuboid(i, out BlockModelCuboid cuboid))
                continue;

            bool selected = i == selectedIndex;
            Handles.color = selected ? workbench.selectedBoundsColor : new Color(1f, 1f, 1f, 0.34f);
            DrawCuboidWireHandle(cuboid);

            Vector3 labelPosition = MultiCuboidBlockWorkbench.ToSceneLocalCenter(cuboid);
            labelPosition.y += MultiCuboidBlockWorkbench.ToSceneLocalSize(cuboid).y * 0.5f + 0.04f;
            Handles.Label(labelPosition, "#" + (i + 1));
        }

        DrawPlacementRotationPreview(workbench);

        Handles.matrix = previousMatrix;
        Handles.color = previousColor;
        Handles.zTest = previousZTest;

        Handles.BeginGUI();
        GUILayout.BeginArea(new Rect(12f, 12f, 292f, 126f), "Multi Cuboid", GUI.skin.window);
        GUILayout.Label("Use Move/Rotate/Scale no Cuboid selecionado.", EditorStyles.miniLabel);
        GUILayout.Label(selectedIndex >= 0 ? $"Selecionado: #{selectedIndex + 1}" : "Selecione um Cuboid_XX para editar.", EditorStyles.miniBoldLabel);
        using (new GUILayout.HorizontalScope())
        {
            if (GUILayout.Button("+", GUILayout.Width(40f)))
            {
                workbench.AddCuboid();
                MarkWorkbenchDirty(workbench);
            }

            if (GUILayout.Button("Duplicar"))
            {
                workbench.DuplicateSelectedCuboid();
                MarkWorkbenchDirty(workbench);
            }

            if (GUILayout.Button("Salvar"))
            {
                workbench.SaveToBlockData();
                MarkWorkbenchDirty(workbench);
            }
        }

        using (new GUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(selectedIndex < 0))
            {
                if (GUILayout.Button("Remover"))
                {
                    workbench.RemoveSelectedCuboid();
                    MarkWorkbenchDirty(workbench);
                }
            }

            if (GUILayout.Button("Zerar"))
            {
                workbench.ClearCuboids();
                MarkWorkbenchDirty(workbench);
            }
        }
        GUILayout.Label("Atalho: Delete/Backspace remove o selecionado.", EditorStyles.miniLabel);
        GUILayout.EndArea();
        Handles.EndGUI();

        HandleSceneKeyboard(workbench, selectedIndex);
    }

    private static void DrawPlacementRotationPreview(MultiCuboidBlockWorkbench workbench)
    {
        if (!workbench.showPlacementRotationPreview)
            return;

        if (!workbench.TryGetTextureMapping(out BlockTextureMapping mapping) ||
            !mapping.usePlacementAxisRotation)
            return;

        BlockPlacementAxis previewAxis = BlockPlacementRotationUtility.SanitizeStoredAxis(workbench.placementPreviewAxis);
        if (previewAxis == BlockPlacementAxis.Y)
            return;

        Handles.color = workbench.placementPreviewColor;
        for (int i = 0; i < workbench.CuboidCount; i++)
        {
            if (!workbench.TryGetCuboid(i, out BlockModelCuboid cuboid))
                continue;

            DrawPlacementPreviewWireHandle(cuboid, mapping, previewAxis);
        }

        Handles.Label(new Vector3(-0.5f, 1.08f, -0.5f), $"Preview rotacao: {FormatPlacementAxis(previewAxis)}");
    }

    private static void DrawCuboidWireHandle(BlockModelCuboid cuboid)
    {
        Matrix4x4 previousMatrix = Handles.matrix;
        Handles.matrix = Handles.matrix * Matrix4x4.TRS(
            MultiCuboidBlockWorkbench.ToSceneLocalCenter(cuboid),
            Quaternion.Euler(cuboid.eulerRotation),
            Vector3.one);
        Handles.DrawWireCube(Vector3.zero, MultiCuboidBlockWorkbench.ToSceneLocalSize(cuboid));
        Handles.matrix = previousMatrix;
    }

    private static void DrawPlacementPreviewWireHandle(
        BlockModelCuboid cuboid,
        BlockTextureMapping mapping,
        BlockPlacementAxis previewAxis)
    {
        Vector3 center = (cuboid.min + cuboid.max) * 0.5f;
        Quaternion rotation = Quaternion.Euler(cuboid.eulerRotation);

        Vector3 p000 = TransformPreviewCorner(cuboid.min.x, cuboid.min.y, cuboid.min.z, center, rotation, mapping, previewAxis);
        Vector3 p100 = TransformPreviewCorner(cuboid.max.x, cuboid.min.y, cuboid.min.z, center, rotation, mapping, previewAxis);
        Vector3 p010 = TransformPreviewCorner(cuboid.min.x, cuboid.max.y, cuboid.min.z, center, rotation, mapping, previewAxis);
        Vector3 p110 = TransformPreviewCorner(cuboid.max.x, cuboid.max.y, cuboid.min.z, center, rotation, mapping, previewAxis);
        Vector3 p001 = TransformPreviewCorner(cuboid.min.x, cuboid.min.y, cuboid.max.z, center, rotation, mapping, previewAxis);
        Vector3 p101 = TransformPreviewCorner(cuboid.max.x, cuboid.min.y, cuboid.max.z, center, rotation, mapping, previewAxis);
        Vector3 p011 = TransformPreviewCorner(cuboid.min.x, cuboid.max.y, cuboid.max.z, center, rotation, mapping, previewAxis);
        Vector3 p111 = TransformPreviewCorner(cuboid.max.x, cuboid.max.y, cuboid.max.z, center, rotation, mapping, previewAxis);

        DrawCuboidWireLinesLocal(p000, p100, p010, p110, p001, p101, p011, p111);
    }

    private static Vector3 TransformPreviewCorner(
        float x,
        float y,
        float z,
        Vector3 center,
        Quaternion rotation,
        BlockTextureMapping mapping,
        BlockPlacementAxis previewAxis)
    {
        Vector3 point = new Vector3(x, y, z);
        point = center + rotation * (point - center);
        point = BlockShapeUtility.TransformPointForPlacement(point, mapping, previewAxis);
        return point - Vector3.one * 0.5f;
    }

    private static void DrawCuboidWireLinesLocal(
        Vector3 p000,
        Vector3 p100,
        Vector3 p010,
        Vector3 p110,
        Vector3 p001,
        Vector3 p101,
        Vector3 p011,
        Vector3 p111)
    {
        Handles.DrawLine(p000, p100);
        Handles.DrawLine(p100, p110);
        Handles.DrawLine(p110, p010);
        Handles.DrawLine(p010, p000);

        Handles.DrawLine(p001, p101);
        Handles.DrawLine(p101, p111);
        Handles.DrawLine(p111, p011);
        Handles.DrawLine(p011, p001);

        Handles.DrawLine(p000, p001);
        Handles.DrawLine(p100, p101);
        Handles.DrawLine(p010, p011);
        Handles.DrawLine(p110, p111);
    }

    private static string FormatPlacementAxis(BlockPlacementAxis axis)
    {
        return axis switch
        {
            BlockPlacementAxis.X => "+X / 90 graus",
            BlockPlacementAxis.XNegative => "-X / 270 graus",
            BlockPlacementAxis.ZNegative => "-Z / 180 graus",
            BlockPlacementAxis.Z => "+Z / original",
            _ => "original"
        };
    }

    private static void HandleSceneKeyboard(MultiCuboidBlockWorkbench workbench, int selectedIndex)
    {
        Event current = Event.current;
        if (current == null || current.type != EventType.KeyDown)
            return;

        if (selectedIndex < 0)
            return;

        if (current.keyCode != KeyCode.Delete && current.keyCode != KeyCode.Backspace)
            return;

        workbench.RemoveSelectedCuboid();
        MarkWorkbenchDirty(workbench);
        current.Use();
    }

    public static void MarkWorkbenchDirty(MultiCuboidBlockWorkbench workbench)
    {
        if (workbench == null)
            return;

        EditorUtility.SetDirty(workbench);
        if (!Application.isPlaying && workbench.gameObject.scene.IsValid())
            EditorSceneManager.MarkSceneDirty(workbench.gameObject.scene);

        SceneView.RepaintAll();
    }
}

internal static class BlockbenchMultiCuboidImporter
{
    private const string LastDirectoryKey = "Voxel.BlockbenchImport.LastDirectory";
    private const float ModelUnitsPerVoxel = 16f;
    private const float TileAlignmentEpsilon = 0.01f;
    private const float PivotEpsilon = 0.001f;

    private static readonly BlockFace[] SupportedFaces =
    {
        BlockFace.Top,
        BlockFace.Bottom,
        BlockFace.Right,
        BlockFace.Left,
        BlockFace.Front,
        BlockFace.Back
    };

    private sealed class ImportedCuboidData
    {
        public BlockModelCuboid cuboid;
        public Dictionary<BlockFace, Vector2Int> faceTiles = new Dictionary<BlockFace, Vector2Int>();
    }

    private sealed class ImportContext
    {
        public BlockDataSO blockData;
        public Vector2Int atlasTiles;
        public Vector2Int textureSize;
        public int convertedFaceCount;
        public List<string> warnings = new List<string>();

        private readonly HashSet<string> warningKeys = new HashSet<string>(StringComparer.Ordinal);

        public bool CanConvertTiles
        {
            get
            {
                return blockData != null &&
                       atlasTiles.x > 0 &&
                       atlasTiles.y > 0 &&
                       textureSize.x > 0 &&
                       textureSize.y > 0;
            }
        }

        public void AddWarning(string key, string message)
        {
            if (warningKeys.Add(key))
                warnings.Add(message);
        }
    }

    public static bool ImportIntoWorkbench(MultiCuboidBlockWorkbench workbench)
    {
        if (workbench == null)
            return false;

        string initialDirectory = ResolveInitialDirectory();
        string path = EditorUtility.OpenFilePanel("Importar JSON do Blockbench", initialDirectory, "json");
        if (string.IsNullOrEmpty(path))
            return false;

        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            EditorPrefs.SetString(LastDirectoryKey, directory);

        try
        {
            string json = File.ReadAllText(path);
            JObject root = JObject.Parse(json);
            ImportContext context = CreateContext(root, workbench.blockData);
            List<ImportedCuboidData> importedCuboids = ParseElements(root, context);
            if (importedCuboids.Count == 0)
                throw new InvalidDataException("O arquivo nao trouxe nenhum cuboide utilizavel para o formato MultiCuboid.");

            bool hasExistingMapping = workbench.TryGetTextureMapping(out BlockTextureMapping existingMapping);
            BlockTextureMapping? existingMappingValue = hasExistingMapping ? existingMapping : (BlockTextureMapping?)null;
            Dictionary<BlockFace, Vector2Int> baseTiles = BuildBaseTiles(importedCuboids, existingMappingValue);
            List<BlockModelCuboid> cuboids = BuildCuboids(importedCuboids, baseTiles);

            workbench.ReplaceCuboids(cuboids);

            bool appliedTextureMapping = ApplyTextureMapping(workbench, baseTiles, existingMappingValue);
            string message = BuildSummary(path, cuboids.Count, context, appliedTextureMapping, workbench.blockData != null);

            Debug.Log(message, workbench);
            EditorUtility.DisplayDialog("Importar Blockbench", message, "OK");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, workbench);
            EditorUtility.DisplayDialog(
                "Importar Blockbench",
                "Falha ao importar o JSON do Blockbench.\n\n" + ex.Message,
                "OK");
            return false;
        }
    }

    private static string ResolveInitialDirectory()
    {
        string lastDirectory = EditorPrefs.GetString(LastDirectoryKey, string.Empty);
        if (!string.IsNullOrEmpty(lastDirectory) && Directory.Exists(lastDirectory))
            return lastDirectory;

        return Application.dataPath;
    }

    private static ImportContext CreateContext(JObject root, BlockDataSO blockData)
    {
        ImportContext context = new ImportContext
        {
            blockData = blockData,
            atlasTiles = ResolveAtlasTiles(blockData)
        };

        if (!TryResolveTextureSize(root, out context.textureSize))
        {
            context.AddWarning(
                "texture_size",
                "O JSON nao informou resolucao de textura utilizavel; a geometria foi importada, mas a conversao automatica de UV para tiles pode ficar incompleta.");
        }

        return context;
    }

    private static Vector2Int ResolveAtlasTiles(BlockDataSO blockData)
    {
        if (blockData == null)
            return Vector2Int.zero;

        return new Vector2Int(
            Mathf.Max(0, Mathf.RoundToInt(blockData.atlasSize.x)),
            Mathf.Max(0, Mathf.RoundToInt(blockData.atlasSize.y)));
    }

    private static bool TryResolveTextureSize(JObject root, out Vector2Int textureSize)
    {
        textureSize = Vector2Int.zero;

        if (TryReadNamedVector2(root["resolution"], "width", "height", out textureSize))
            return true;

        if (TryReadNamedVector2(root, "texture_width", "texture_height", out textureSize))
            return true;

        if (root["textures"] is JArray texturesArray)
        {
            for (int i = 0; i < texturesArray.Count; i++)
            {
                if (!(texturesArray[i] is JObject textureObject))
                    continue;

                if (TryReadNamedVector2(textureObject, "uv_width", "uv_height", out textureSize))
                    return true;

                if (TryReadNamedVector2(textureObject, "width", "height", out textureSize))
                    return true;
            }
        }

        return false;
    }

    private static bool TryReadNamedVector2(JToken token, string xName, string yName, out Vector2Int value)
    {
        value = Vector2Int.zero;
        if (!(token is JObject obj))
            return false;

        return TryReadNamedVector2(obj, xName, yName, out value);
    }

    private static bool TryReadNamedVector2(JObject obj, string xName, string yName, out Vector2Int value)
    {
        value = Vector2Int.zero;
        if (obj == null)
            return false;

        if (!obj.TryGetValue(xName, out JToken xToken) || !obj.TryGetValue(yName, out JToken yToken))
            return false;

        int x = Mathf.RoundToInt(ReadFloat(xToken));
        int y = Mathf.RoundToInt(ReadFloat(yToken));
        if (x <= 0 || y <= 0)
            return false;

        value = new Vector2Int(x, y);
        return true;
    }

    private static List<ImportedCuboidData> ParseElements(JObject root, ImportContext context)
    {
        if (!(root["elements"] is JArray elements))
        {
            if (root["minecraft:geometry"] != null)
            {
                throw new InvalidDataException(
                    "Este arquivo parece ser Bedrock geometry JSON. O importador atual espera um JSON do Blockbench com array 'elements' (Java Block / bbmodel simples).");
            }

            throw new InvalidDataException(
                "O arquivo nao tem array 'elements'. Exporte do Blockbench em um formato de bloco que gere elementos/cubes compativeis.");
        }

        List<ImportedCuboidData> importedCuboids = new List<ImportedCuboidData>(elements.Count);
        for (int i = 0; i < elements.Count; i++)
        {
            if (!(elements[i] is JObject element))
                continue;

            if (!ShouldImportElement(element))
                continue;

            if (TryParseElement(element, context, out ImportedCuboidData importedCuboid))
                importedCuboids.Add(importedCuboid);
        }

        return importedCuboids;
    }

    private static bool ShouldImportElement(JObject element)
    {
        if (element.TryGetValue("export", out JToken exportToken) && !ReadBool(exportToken, true))
            return false;

        if (element.TryGetValue("visibility", out JToken visibilityToken) && !ReadBool(visibilityToken, true))
            return false;

        return true;
    }

    private static bool TryParseElement(JObject element, ImportContext context, out ImportedCuboidData importedCuboid)
    {
        importedCuboid = null;
        if (!TryReadVector3(element["from"], out Vector3 from) || !TryReadVector3(element["to"], out Vector3 to))
        {
            context.AddWarning(
                "invalid_from_to",
                "Um ou mais elementos foram ignorados porque nao tinham vetores 'from'/'to' validos.");
            return false;
        }

        float inflate = ReadFloat(element["inflate"]);
        Vector3 minModel = Vector3.Min(from, to) - Vector3.one * inflate;
        Vector3 maxModel = Vector3.Max(from, to) + Vector3.one * inflate;

        BlockModelCuboid cuboid = new BlockModelCuboid(minModel / ModelUnitsPerVoxel, maxModel / ModelUnitsPerVoxel);
        importedCuboid = new ImportedCuboidData
        {
            cuboid = cuboid
        };

        bool hadExplicitFaces = false;
        BlockCuboidFaceMask faceMask = BlockCuboidFaceMask.None;
        if (element["faces"] is JObject facesObject)
        {
            foreach (JProperty property in facesObject.Properties())
            {
                hadExplicitFaces = true;
                if (!(property.Value is JObject faceObject))
                    continue;

                if (!TryMapFaceName(property.Name, out BlockFace face))
                    continue;

                if (faceObject.TryGetValue("enabled", out JToken enabledToken) && !ReadBool(enabledToken, true))
                    continue;

                faceMask |= GetFaceMask(face);

                if (TryResolveFaceTile(faceObject, context, out Vector2Int tile))
                    importedCuboid.faceTiles[face] = tile;
            }
        }

        if (hadExplicitFaces)
        {
            if (faceMask == BlockCuboidFaceMask.None)
            {
                context.AddWarning(
                    "empty_faces",
                    "Alguns elementos tinham apenas faces desativadas e foram ignorados.");
                return false;
            }

            cuboid.faces = faceMask;
        }

        TryApplyRotation(element, minModel, maxModel, ref cuboid, context);
        importedCuboid.cuboid = cuboid;
        return true;
    }

    private static void TryApplyRotation(
        JObject element,
        Vector3 minModel,
        Vector3 maxModel,
        ref BlockModelCuboid cuboid,
        ImportContext context)
    {
        if (!TryResolveRotation(element, (minModel + maxModel) * 0.5f, out Vector3 euler, out Vector3 pivotModel, out bool usedCustomPivot, out bool usesRescale))
            return;

        if (Mathf.Abs(euler.x) <= 0.0001f &&
            Mathf.Abs(euler.y) <= 0.0001f &&
            Mathf.Abs(euler.z) <= 0.0001f)
        {
            return;
        }

        Vector3 centerModel = (minModel + maxModel) * 0.5f;
        if (usedCustomPivot && (pivotModel - centerModel).sqrMagnitude > PivotEpsilon * PivotEpsilon)
        {
            context.AddWarning(
                "pivot_rotation",
                "Rotacoes com pivot fora do centro do cuboide ainda nao tem equivalente direto na engine; nesses casos a importacao manteve apenas a caixa sem rotacao.");
            return;
        }

        if (usesRescale)
        {
            context.AddWarning(
                "rescale_rotation",
                "Rotacao com 'rescale' do Blockbench nao tem equivalente direto aqui; a importacao manteve a rotacao, mas sem o ajuste extra de escala.");
        }

        cuboid.eulerRotation = BlockShapeUtility.NormalizeCuboidEulerRotation(euler);
    }

    private static bool TryResolveRotation(
        JObject element,
        Vector3 defaultPivotModel,
        out Vector3 euler,
        out Vector3 pivotModel,
        out bool usedCustomPivot,
        out bool usesRescale)
    {
        euler = Vector3.zero;
        pivotModel = defaultPivotModel;
        usedCustomPivot = false;
        usesRescale = false;

        JToken rotationToken = element["rotation"];
        if (rotationToken == null)
            return false;

        if (rotationToken is JObject rotationObject)
        {
            float angle = ReadFloat(rotationObject["angle"]);
            if (rotationObject.TryGetValue("origin", out JToken originToken) && TryReadVector3(originToken, out Vector3 parsedOrigin))
            {
                pivotModel = parsedOrigin;
                usedCustomPivot = true;
            }

            usesRescale = ReadBool(rotationObject["rescale"], false);
            string axis = rotationObject["axis"] != null ? rotationObject["axis"].ToString() : string.Empty;
            switch (axis.ToLowerInvariant())
            {
                case "x":
                    euler = new Vector3(angle, 0f, 0f);
                    return true;

                case "y":
                    euler = new Vector3(0f, angle, 0f);
                    return true;

                case "z":
                    euler = new Vector3(0f, 0f, angle);
                    return true;

                default:
                    return false;
            }
        }

        if (TryReadVector3(rotationToken, out euler))
        {
            if (element.TryGetValue("origin", out JToken originToken) && TryReadVector3(originToken, out Vector3 parsedOrigin))
            {
                pivotModel = parsedOrigin;
                usedCustomPivot = true;
            }

            usesRescale = ReadBool(element["rescale"], false);
            return true;
        }

        return false;
    }

    private static bool TryResolveFaceTile(JObject faceObject, ImportContext context, out Vector2Int tile)
    {
        tile = Vector2Int.zero;
        if (!context.CanConvertTiles)
            return false;

        if (!TryReadUvRect(faceObject, out Rect uvRect))
            return false;

        float tileWidth = (float)context.textureSize.x / context.atlasTiles.x;
        float tileHeight = (float)context.textureSize.y / context.atlasTiles.y;
        if (tileWidth <= 0f || tileHeight <= 0f)
            return false;

        float tileX = uvRect.xMin / tileWidth;
        float tileYFromTop = uvRect.yMin / tileHeight;
        float spanX = uvRect.width / tileWidth;
        float spanY = uvRect.height / tileHeight;

        if (!NearlyInteger(tileX) ||
            !NearlyInteger(tileYFromTop) ||
            !NearlyInteger(spanX) ||
            !NearlyInteger(spanY) ||
            Mathf.RoundToInt(spanX) != 1 ||
            Mathf.RoundToInt(spanY) != 1)
        {
            context.AddWarning(
                "partial_uv",
                "Algumas faces usam UV que nao cai exatamente em um tile inteiro do atlas atual; essas texturas precisam de ajuste manual depois da importacao.");
            return false;
        }

        int x = Mathf.RoundToInt(tileX);
        int yFromTop = Mathf.RoundToInt(tileYFromTop);
        int y = context.blockData != null && context.blockData.atlasCoordinatesStartTopLeft
            ? yFromTop
            : context.atlasTiles.y - 1 - yFromTop;

        if (x < 0 || x >= context.atlasTiles.x || y < 0 || y >= context.atlasTiles.y)
        {
            context.AddWarning(
                "uv_out_of_range",
                "Algumas faces apontam para fora do atlas configurado no BlockDataSO e nao puderam ser convertidas.");
            return false;
        }

        float uvRotation = ReadFloat(faceObject["rotation"]);
        if (Mathf.Abs(uvRotation) > 0.0001f)
        {
            context.AddWarning(
                "uv_rotation",
                "Rotacao de UV por face no Blockbench ainda nao e convertida automaticamente; o tile foi importado sem girar a textura.");
        }

        tile = new Vector2Int(x, y);
        context.convertedFaceCount++;
        return true;
    }

    private static bool TryReadUvRect(JObject faceObject, out Rect uvRect)
    {
        uvRect = default;

        if (faceObject["uv"] is JArray uvArray && uvArray.Count >= 4)
        {
            float u0 = ReadFloat(uvArray[0]);
            float v0 = ReadFloat(uvArray[1]);
            float u1 = ReadFloat(uvArray[2]);
            float v1 = ReadFloat(uvArray[3]);

            uvRect = Rect.MinMaxRect(
                Mathf.Min(u0, u1),
                Mathf.Min(v0, v1),
                Mathf.Max(u0, u1),
                Mathf.Max(v0, v1));
            return true;
        }

        if (faceObject["uv"] is JArray originArray &&
            originArray.Count >= 2 &&
            faceObject["uv_size"] is JArray sizeArray &&
            sizeArray.Count >= 2)
        {
            float u = ReadFloat(originArray[0]);
            float v = ReadFloat(originArray[1]);
            float w = ReadFloat(sizeArray[0]);
            float h = ReadFloat(sizeArray[1]);

            uvRect = Rect.MinMaxRect(
                Mathf.Min(u, u + w),
                Mathf.Min(v, v + h),
                Mathf.Max(u, u + w),
                Mathf.Max(v, v + h));
            return true;
        }

        return false;
    }

    private static Dictionary<BlockFace, Vector2Int> BuildBaseTiles(
        List<ImportedCuboidData> importedCuboids,
        BlockTextureMapping? existingMapping)
    {
        Dictionary<BlockFace, Vector2Int> baseTiles = new Dictionary<BlockFace, Vector2Int>();

        for (int i = 0; i < SupportedFaces.Length; i++)
        {
            BlockFace face = SupportedFaces[i];
            Dictionary<Vector2Int, int> counts = new Dictionary<Vector2Int, int>();
            for (int c = 0; c < importedCuboids.Count; c++)
            {
                if (!importedCuboids[c].faceTiles.TryGetValue(face, out Vector2Int tile))
                    continue;

                counts.TryGetValue(tile, out int currentCount);
                counts[tile] = currentCount + 1;
            }

            if (counts.Count > 0)
            {
                baseTiles[face] = ResolveMostCommonTile(
                    counts,
                    existingMapping.HasValue ? (Vector2Int?)existingMapping.Value.GetTileCoord(face) : null);
                continue;
            }

            if (existingMapping.HasValue)
                baseTiles[face] = existingMapping.Value.GetTileCoord(face);
        }

        return baseTiles;
    }

    private static Vector2Int ResolveMostCommonTile(Dictionary<Vector2Int, int> counts, Vector2Int? preferredTile)
    {
        Vector2Int bestTile = Vector2Int.zero;
        int bestCount = int.MinValue;
        bool found = false;

        foreach (KeyValuePair<Vector2Int, int> pair in counts)
        {
            bool betterCount = !found || pair.Value > bestCount;
            bool preferredTie = preferredTile.HasValue && pair.Value == bestCount && pair.Key == preferredTile.Value;
            bool smallerTie =
                pair.Value == bestCount &&
                (pair.Key.y < bestTile.y || (pair.Key.y == bestTile.y && pair.Key.x < bestTile.x));

            if (betterCount || preferredTie || (!preferredTie && smallerTie))
            {
                bestTile = pair.Key;
                bestCount = pair.Value;
                found = true;
            }
        }

        return bestTile;
    }

    private static List<BlockModelCuboid> BuildCuboids(
        List<ImportedCuboidData> importedCuboids,
        Dictionary<BlockFace, Vector2Int> baseTiles)
    {
        List<BlockModelCuboid> cuboids = new List<BlockModelCuboid>(importedCuboids.Count);
        for (int i = 0; i < importedCuboids.Count; i++)
        {
            ImportedCuboidData imported = importedCuboids[i];
            BlockModelCuboid cuboid = imported.cuboid;
            cuboid.textureOverrideFaces = BlockCuboidFaceMask.None;

            foreach (KeyValuePair<BlockFace, Vector2Int> pair in imported.faceTiles)
            {
                if (!baseTiles.TryGetValue(pair.Key, out Vector2Int baseTile) || baseTile == pair.Value)
                    continue;

                cuboid.textureOverrideFaces |= GetFaceMask(pair.Key);
                SetCuboidFaceTile(ref cuboid, pair.Key, pair.Value);
            }

            cuboids.Add(cuboid);
        }

        return cuboids;
    }

    private static bool ApplyTextureMapping(
        MultiCuboidBlockWorkbench workbench,
        Dictionary<BlockFace, Vector2Int> baseTiles,
        BlockTextureMapping? existingMapping)
    {
        if (workbench == null || workbench.blockData == null || baseTiles.Count == 0)
            return false;

        BlockTextureMapping mapping = existingMapping ?? CreateDefaultImportMapping(workbench.blockType);
        mapping.EnsureDirectionalSideData();

        bool changed = !existingMapping.HasValue;
        for (int i = 0; i < SupportedFaces.Length; i++)
        {
            BlockFace face = SupportedFaces[i];
            if (!baseTiles.TryGetValue(face, out Vector2Int tile))
                continue;

            changed |= SetMappingFaceTile(ref mapping, face, tile);
        }

        mapping.side = mapping.right;
        if (!changed)
            return false;

        workbench.SetTextureMapping(mapping);
        return true;
    }

    private static BlockTextureMapping CreateDefaultImportMapping(BlockType blockType)
    {
        return new BlockTextureMapping
        {
            blockType = blockType,
            renderShape = BlockRenderShape.MultiCuboid,
            shapeMin = Vector3.zero,
            shapeMax = Vector3.one,
            isSolid = true,
            isEmpty = false,
            usePlacementAxisRotation = true,
            placementRotationAxes = BlockPlacementRotationAxes.Horizontal,
            lightOpacity = 15,
            materialIndex = 0
        };
    }

    private static string BuildSummary(
        string path,
        int cuboidCount,
        ImportContext context,
        bool appliedTextureMapping,
        bool hasBlockData)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append("Importou ");
        builder.Append(cuboidCount);
        builder.Append(" cuboides de ");
        builder.Append(Path.GetFileName(path));
        builder.AppendLine(" para a bancada.");

        if (context.convertedFaceCount > 0)
        {
            if (appliedTextureMapping)
                builder.AppendLine("Tiles compativeis do atlas foram aplicados no mapping base do bloco.");
            else if (!hasBlockData)
                builder.AppendLine("Tiles foram detectados, mas faltou um BlockDataSO para salvar o mapping base.");
            else
                builder.AppendLine("Os tiles detectados ja batiam com o mapping atual ou ficaram apenas como override por cuboide.");
        }
        else
        {
            builder.AppendLine("A geometria entrou, mas nenhuma UV compativel com tiles inteiros do atlas foi encontrada.");
        }

        builder.AppendLine("Clique em 'Save no BlockData' para gravar a geometria no asset.");

        if (context.warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Avisos:");
            int shown = Mathf.Min(4, context.warnings.Count);
            for (int i = 0; i < shown; i++)
                builder.Append("- ").AppendLine(context.warnings[i]);

            if (context.warnings.Count > shown)
            {
                builder.Append("- Mais ");
                builder.Append(context.warnings.Count - shown);
                builder.AppendLine(" aviso(s) foram resumidos.");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static bool TryMapFaceName(string name, out BlockFace face)
    {
        switch (name.ToLowerInvariant())
        {
            case "up":
            case "top":
                face = BlockFace.Top;
                return true;

            case "down":
            case "bottom":
                face = BlockFace.Bottom;
                return true;

            case "east":
            case "right":
                face = BlockFace.Right;
                return true;

            case "west":
            case "left":
                face = BlockFace.Left;
                return true;

            case "south":
            case "front":
                face = BlockFace.Front;
                return true;

            case "north":
            case "back":
                face = BlockFace.Back;
                return true;

            default:
                face = BlockFace.Side;
                return false;
        }
    }

    private static BlockCuboidFaceMask GetFaceMask(BlockFace face)
    {
        switch (face)
        {
            case BlockFace.Top:
                return BlockCuboidFaceMask.Top;

            case BlockFace.Bottom:
                return BlockCuboidFaceMask.Bottom;

            case BlockFace.Right:
                return BlockCuboidFaceMask.Right;

            case BlockFace.Left:
                return BlockCuboidFaceMask.Left;

            case BlockFace.Front:
                return BlockCuboidFaceMask.Front;

            case BlockFace.Back:
                return BlockCuboidFaceMask.Back;

            default:
                return BlockCuboidFaceMask.None;
        }
    }

    private static void SetCuboidFaceTile(ref BlockModelCuboid cuboid, BlockFace face, Vector2Int tile)
    {
        switch (face)
        {
            case BlockFace.Top:
                cuboid.textureTop = tile;
                return;

            case BlockFace.Bottom:
                cuboid.textureBottom = tile;
                return;

            case BlockFace.Right:
                cuboid.textureRight = tile;
                return;

            case BlockFace.Left:
                cuboid.textureLeft = tile;
                return;

            case BlockFace.Front:
                cuboid.textureFront = tile;
                return;

            case BlockFace.Back:
                cuboid.textureBack = tile;
                return;
        }
    }

    private static bool SetMappingFaceTile(ref BlockTextureMapping mapping, BlockFace face, Vector2Int tile)
    {
        switch (face)
        {
            case BlockFace.Top:
                if (mapping.top == tile)
                    return false;
                mapping.top = tile;
                return true;

            case BlockFace.Bottom:
                if (mapping.bottom == tile)
                    return false;
                mapping.bottom = tile;
                return true;

            case BlockFace.Right:
                if (mapping.right == tile)
                    return false;
                mapping.right = tile;
                return true;

            case BlockFace.Left:
                if (mapping.left == tile)
                    return false;
                mapping.left = tile;
                return true;

            case BlockFace.Front:
                if (mapping.front == tile)
                    return false;
                mapping.front = tile;
                return true;

            case BlockFace.Back:
                if (mapping.back == tile)
                    return false;
                mapping.back = tile;
                return true;

            default:
                return false;
        }
    }

    private static bool TryReadVector3(JToken token, out Vector3 value)
    {
        value = Vector3.zero;
        if (!(token is JArray array) || array.Count < 3)
            return false;

        value = new Vector3(
            ReadFloat(array[0]),
            ReadFloat(array[1]),
            ReadFloat(array[2]));
        return true;
    }

    private static float ReadFloat(JToken token, float fallback = 0f)
    {
        if (token == null)
            return fallback;

        switch (token.Type)
        {
            case JTokenType.Integer:
            case JTokenType.Float:
                return token.Value<float>();

            case JTokenType.String:
                if (float.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                    return parsed;
                break;
        }

        return fallback;
    }

    private static bool ReadBool(JToken token, bool fallback)
    {
        if (token == null)
            return fallback;

        switch (token.Type)
        {
            case JTokenType.Boolean:
                return token.Value<bool>();

            case JTokenType.Integer:
                return token.Value<int>() != 0;

            case JTokenType.String:
                if (bool.TryParse(token.Value<string>(), out bool parsedBool))
                    return parsedBool;
                break;
        }

        return fallback;
    }

    private static bool NearlyInteger(float value)
    {
        return Mathf.Abs(value - Mathf.Round(value)) <= TileAlignmentEpsilon;
    }
}
