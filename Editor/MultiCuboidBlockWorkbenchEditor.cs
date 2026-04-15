using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
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
            EditorGUILayout.HelpBox("Escolha um BlockDataSO para editar os entry IDs de textura deste bloco.", MessageType.None);
            return;
        }

        Vector2 atlasSize = workbench.blockData.atlasSize;
        string origin = workbench.blockData.atlasCoordinatesStartTopLeft ? "origem no topo esquerdo" : "origem embaixo esquerdo";
        EditorGUILayout.LabelField($"Atlas: {Mathf.RoundToInt(atlasSize.x)} x {Mathf.RoundToInt(atlasSize.y)} tiles ({origin})", EditorStyles.miniLabel);

        if (!workbench.TryGetTextureMapping(out BlockTextureMapping mapping))
        {
            EditorGUILayout.HelpBox("Este BlockType ainda nao tem mapping de textura. Crie um mapping para definir as texturas por face.", MessageType.Warning);
            if (GUILayout.Button("Criar mapping de textura"))
            {
                mapping = workbench.GetOrCreateTextureMapping();
                SaveTextureMapping(workbench, mapping);
            }

            return;
        }

        EditorGUI.BeginChangeCheck();
        int materialIndex = EditorGUILayout.IntField("Material Index", mapping.materialIndex);
        bool changed = EditorGUI.EndChangeCheck();

        if (changed)
        {
            mapping.materialIndex = Mathf.Max(0, materialIndex);
            SaveTextureMapping(workbench, mapping);
        }

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Entry IDs por face", EditorStyles.miniBoldLabel);
        EditorGUILayout.HelpBox(
            "Os entry IDs sao a fonte de verdade do bloco. As coordenadas antigas ficam escondidas so para compatibilidade de migracao.",
            MessageType.None);

        EditorGUI.BeginChangeCheck();
        string topEntryId = EditorGUILayout.DelayedTextField("Top entryId", GetBaseTextureEntryId(workbench, BlockFace.Top));
        string bottomEntryId = EditorGUILayout.DelayedTextField("Bottom entryId", GetBaseTextureEntryId(workbench, BlockFace.Bottom));
        string rightEntryId = EditorGUILayout.DelayedTextField("Right +X entryId", GetBaseTextureEntryId(workbench, BlockFace.Right));
        string leftEntryId = EditorGUILayout.DelayedTextField("Left -X entryId", GetBaseTextureEntryId(workbench, BlockFace.Left));
        string frontEntryId = EditorGUILayout.DelayedTextField("Front +Z entryId", GetBaseTextureEntryId(workbench, BlockFace.Front));
        string backEntryId = EditorGUILayout.DelayedTextField("Back -Z entryId", GetBaseTextureEntryId(workbench, BlockFace.Back));
        if (EditorGUI.EndChangeCheck())
        {
            SaveBaseTextureEntryIds(
                workbench,
                ref mapping,
                new BlockFaceTextureEntryIdSet
                {
                    top = topEntryId,
                    bottom = bottomEntryId,
                    right = rightEntryId,
                    left = leftEntryId,
                    front = frontEntryId,
                    back = backEntryId
                });
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Top em todas"))
            {
                SaveBaseTextureEntryIds(
                    workbench,
                    ref mapping,
                    new BlockFaceTextureEntryIdSet
                    {
                        top = topEntryId,
                        bottom = topEntryId,
                        right = topEntryId,
                        left = topEntryId,
                        front = topEntryId,
                        back = topEntryId
                    });
            }

            if (GUILayout.Button("Front nas laterais"))
            {
                SaveBaseTextureEntryIds(
                    workbench,
                    ref mapping,
                    new BlockFaceTextureEntryIdSet
                    {
                        top = topEntryId,
                        bottom = bottomEntryId,
                        right = frontEntryId,
                        left = frontEntryId,
                        front = frontEntryId,
                        back = frontEntryId
                    });
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Bottom = Top"))
            {
                SaveBaseTextureEntryIds(
                    workbench,
                    ref mapping,
                    new BlockFaceTextureEntryIdSet
                    {
                        top = topEntryId,
                        bottom = topEntryId,
                        right = rightEntryId,
                        left = leftEntryId,
                        front = frontEntryId,
                        back = backEntryId
                    });
            }

            if (GUILayout.Button("Limpar entryIds"))
            {
                SaveBaseTextureEntryIds(workbench, ref mapping, new BlockFaceTextureEntryIdSet());
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
        SyncAllMappingFaceUvRects(workbench, ref mapping);

        workbench.SetTextureMapping(mapping);
        MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
    }

    internal static string GetBaseTextureEntryId(MultiCuboidBlockWorkbench workbench, BlockFace face)
    {
        if (workbench == null || workbench.blockData == null)
            return string.Empty;

        return workbench.blockData.TryGetTextureEntryId(workbench.blockType, face, out string entryId)
            ? entryId
            : string.Empty;
    }

    private static void SaveBaseTextureEntryIds(
        MultiCuboidBlockWorkbench workbench,
        ref BlockTextureMapping mapping,
        BlockFaceTextureEntryIdSet entryIds)
    {
        if (workbench == null || workbench.blockData == null || entryIds == null)
            return;

        Undo.RecordObject(workbench.blockData, "Edit Block Texture Entry IDs");

        bool changed = false;
        changed |= SaveBaseTextureEntryId(workbench, BlockFace.Top, entryIds.top);
        changed |= SaveBaseTextureEntryId(workbench, BlockFace.Bottom, entryIds.bottom);
        changed |= SaveBaseTextureEntryId(workbench, BlockFace.Right, entryIds.right);
        changed |= SaveBaseTextureEntryId(workbench, BlockFace.Left, entryIds.left);
        changed |= SaveBaseTextureEntryId(workbench, BlockFace.Front, entryIds.front);
        changed |= SaveBaseTextureEntryId(workbench, BlockFace.Back, entryIds.back);

        if (!changed)
            return;

        SaveTextureMapping(workbench, mapping);
    }

    private static bool SaveBaseTextureEntryId(MultiCuboidBlockWorkbench workbench, BlockFace face, string entryId)
    {
        return workbench != null &&
               workbench.blockData != null &&
               workbench.blockData.SetTextureEntryId(workbench.blockType, face, entryId);
    }

    internal static void SyncAllMappingFaceUvRects(MultiCuboidBlockWorkbench workbench, ref BlockTextureMapping mapping)
    {
        TextureAtlasGenerator generator = ResolveLoadedAtlasGenerator(workbench);
        ApplyResolvedMappingEntryIdUv(workbench, generator, BlockFace.Top, GetResolvedBaseTextureEntryId(workbench, generator, BlockFace.Top), ref mapping);
        ApplyResolvedMappingEntryIdUv(workbench, generator, BlockFace.Bottom, GetResolvedBaseTextureEntryId(workbench, generator, BlockFace.Bottom), ref mapping);
        ApplyResolvedMappingEntryIdUv(workbench, generator, BlockFace.Right, GetResolvedBaseTextureEntryId(workbench, generator, BlockFace.Right), ref mapping);
        ApplyResolvedMappingEntryIdUv(workbench, generator, BlockFace.Left, GetResolvedBaseTextureEntryId(workbench, generator, BlockFace.Left), ref mapping);
        ApplyResolvedMappingEntryIdUv(workbench, generator, BlockFace.Front, GetResolvedBaseTextureEntryId(workbench, generator, BlockFace.Front), ref mapping);
        ApplyResolvedMappingEntryIdUv(workbench, generator, BlockFace.Back, GetResolvedBaseTextureEntryId(workbench, generator, BlockFace.Back), ref mapping);
    }

    internal static string GetResolvedBaseTextureEntryId(
        MultiCuboidBlockWorkbench workbench,
        TextureAtlasGenerator generator,
        BlockFace face)
    {
        if (workbench == null || workbench.blockData == null)
            return string.Empty;

        return workbench.blockData.TryGetResolvedTextureEntryId(generator, workbench.blockType, face, out string entryId)
            ? entryId
            : string.Empty;
    }

    internal static void ApplyResolvedMappingEntryIdUv(
        MultiCuboidBlockWorkbench workbench,
        TextureAtlasGenerator generator,
        BlockFace face,
        string entryId,
        ref BlockTextureMapping mapping)
    {
        if (TryResolveAtlasEntryIdUv(workbench, generator, entryId, out Rect entryUv))
        {
            mapping.SetUvRectData(face, BlockAtlasUvUtility.RectToUvRectData(entryUv));
            return;
        }

        if (generator == null)
            return;

        mapping.SetUvRectData(face, Vector4.zero);
    }

    internal static bool TryResolveAtlasEntryIdUv(
        MultiCuboidBlockWorkbench workbench,
        TextureAtlasGenerator generator,
        string entryId,
        out Rect uvRect)
    {
        uvRect = default;
        if (string.IsNullOrWhiteSpace(entryId))
            return false;

        generator ??= ResolveLoadedAtlasGenerator(workbench);
        return generator != null && generator.TryGetUv(entryId.Trim(), out uvRect);
    }

    internal static TextureAtlasGenerator ResolveLoadedAtlasGenerator(MultiCuboidBlockWorkbench workbench)
    {
        if (workbench == null)
            return null;

#if UNITY_2023_1_OR_NEWER
        TextureAtlasGenerator[] generators = UnityEngine.Object.FindObjectsByType<TextureAtlasGenerator>(FindObjectsInactive.Include);
#else
        TextureAtlasGenerator[] generators = UnityEngine.Object.FindObjectsOfType<TextureAtlasGenerator>(true);
#endif

        TextureAtlasGenerator bestGenerator = null;
        int bestScore = int.MinValue;
        Scene targetScene = workbench.gameObject.scene;

        for (int i = 0; i < generators.Length; i++)
        {
            TextureAtlasGenerator candidate = generators[i];
            if (candidate == null)
                continue;

            int score = 0;
            if (candidate.gameObject.scene == targetScene)
                score += 1000;
            if (candidate.UsesTextureDatabase)
                score += 200;
            if (candidate.HasConfiguredTextureEntries())
                score += 120;
            if (candidate.GeneratedAtlas != null)
                score += 80;
            if (candidate.enabled)
                score += 20;

            if (score > bestScore)
            {
                bestScore = score;
                bestGenerator = candidate;
            }
        }

        return bestGenerator;
    }

    private static Vector2Int GetAtlasTileGridSize(BlockDataSO blockData)
    {
        if (blockData == null)
            return Vector2Int.one;

        return new Vector2Int(
            Mathf.Max(1, Mathf.RoundToInt(blockData.atlasSize.x)),
            Mathf.Max(1, Mathf.RoundToInt(blockData.atlasSize.y)));
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

        DrawCuboidTextureFields(workbench, index, ref cuboid, ref changed);

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
        BlockModelCuboid updatedCuboid = new BlockModelCuboid
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
           
        };
        updatedCuboid.CopyUvRectOverrideDataFrom(cuboid);
        workbench.SetCuboid(index, updatedCuboid);
        MarkWorkbenchDirty(workbench);
    }

    private static void DrawCuboidTextureFields(MultiCuboidBlockWorkbench workbench, int index, ref BlockModelCuboid cuboid, ref bool changed)
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
            TextureAtlasGenerator generator = MultiCuboidBlockWorkbenchEditor.ResolveLoadedAtlasGenerator(workbench);
            ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Top);
            ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Bottom);
            ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Right);
            ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Left);
            ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Front);
            ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Back);
            changed = true;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Copiar do bloco"))
            {
                if (workbench.TryGetTextureMapping(out BlockTextureMapping mapping))
                {
                    cuboid.textureOverrideFaces = BlockCuboidFaceMask.All;
                    TextureAtlasGenerator generator = MultiCuboidBlockWorkbenchEditor.ResolveLoadedAtlasGenerator(workbench);
                    Undo.RecordObject(workbench, "Copy Cuboid Texture Entry IDs From Block");
                    workbench.SetCuboidTextureEntryId(index, BlockFace.Top, MultiCuboidBlockWorkbenchEditor.GetResolvedBaseTextureEntryId(workbench, generator, BlockFace.Top));
                    workbench.SetCuboidTextureEntryId(index, BlockFace.Bottom, MultiCuboidBlockWorkbenchEditor.GetResolvedBaseTextureEntryId(workbench, generator, BlockFace.Bottom));
                    workbench.SetCuboidTextureEntryId(index, BlockFace.Right, MultiCuboidBlockWorkbenchEditor.GetResolvedBaseTextureEntryId(workbench, generator, BlockFace.Right));
                    workbench.SetCuboidTextureEntryId(index, BlockFace.Left, MultiCuboidBlockWorkbenchEditor.GetResolvedBaseTextureEntryId(workbench, generator, BlockFace.Left));
                    workbench.SetCuboidTextureEntryId(index, BlockFace.Front, MultiCuboidBlockWorkbenchEditor.GetResolvedBaseTextureEntryId(workbench, generator, BlockFace.Front));
                    workbench.SetCuboidTextureEntryId(index, BlockFace.Back, MultiCuboidBlockWorkbenchEditor.GetResolvedBaseTextureEntryId(workbench, generator, BlockFace.Back));
                    ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Top);
                    ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Bottom);
                    ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Right);
                    ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Left);
                    ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Front);
                    ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Back);
                    changed = true;
                }
            }

            if (GUILayout.Button("Limpar overrides"))
            {
                cuboid.textureOverrideFaces = BlockCuboidFaceMask.None;
                cuboid.SetOverrideUvRectData(BlockFace.Top, Vector4.zero);
                cuboid.SetOverrideUvRectData(BlockFace.Bottom, Vector4.zero);
                cuboid.SetOverrideUvRectData(BlockFace.Right, Vector4.zero);
                cuboid.SetOverrideUvRectData(BlockFace.Left, Vector4.zero);
                cuboid.SetOverrideUvRectData(BlockFace.Front, Vector4.zero);
                cuboid.SetOverrideUvRectData(BlockFace.Back, Vector4.zero);
                Undo.RecordObject(workbench, "Clear Cuboid Texture Entry IDs");
                workbench.ClearCuboidTextureEntryIds(index);
                changed = true;
            }
        }

        using (new EditorGUI.DisabledScope(cuboid.EffectiveTextureOverrideFaces == BlockCuboidFaceMask.None))
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Entry IDs do cuboide", EditorStyles.miniBoldLabel);

            EditorGUI.BeginChangeCheck();
            string topEntryId = EditorGUILayout.DelayedTextField("Top entryId", GetCuboidTextureEntryId(workbench, index, BlockFace.Top));
            string bottomEntryId = EditorGUILayout.DelayedTextField("Bottom entryId", GetCuboidTextureEntryId(workbench, index, BlockFace.Bottom));
            string rightEntryId = EditorGUILayout.DelayedTextField("Right +X entryId", GetCuboidTextureEntryId(workbench, index, BlockFace.Right));
            string leftEntryId = EditorGUILayout.DelayedTextField("Left -X entryId", GetCuboidTextureEntryId(workbench, index, BlockFace.Left));
            string frontEntryId = EditorGUILayout.DelayedTextField("Front +Z entryId", GetCuboidTextureEntryId(workbench, index, BlockFace.Front));
            string backEntryId = EditorGUILayout.DelayedTextField("Back -Z entryId", GetCuboidTextureEntryId(workbench, index, BlockFace.Back));
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(workbench, "Edit Cuboid Texture Entry IDs");
                SetCuboidTextureEntryId(workbench, index, BlockFace.Top, topEntryId);
                SetCuboidTextureEntryId(workbench, index, BlockFace.Bottom, bottomEntryId);
                SetCuboidTextureEntryId(workbench, index, BlockFace.Right, rightEntryId);
                SetCuboidTextureEntryId(workbench, index, BlockFace.Left, leftEntryId);
                SetCuboidTextureEntryId(workbench, index, BlockFace.Front, frontEntryId);
                SetCuboidTextureEntryId(workbench, index, BlockFace.Back, backEntryId);

                TextureAtlasGenerator generator = MultiCuboidBlockWorkbenchEditor.ResolveLoadedAtlasGenerator(workbench);
                ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Top);
                ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Bottom);
                ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Right);
                ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Left);
                ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Front);
                ApplyCuboidFaceUvRect(workbench, index, generator, ref cuboid, BlockFace.Back);
                changed = true;
            }
        }

        EditorGUILayout.HelpBox(
            "Faces marcadas podem ter entryId proprio. Se uma face do cuboide estiver vazia, ela reutiliza a textura resolvida do bloco base.",
            MessageType.None);
    }

    private static string GetCuboidTextureEntryId(MultiCuboidBlockWorkbench workbench, int index, BlockFace face)
    {
        return workbench != null && workbench.TryGetCuboidTextureEntryId(index, face, out string entryId)
            ? entryId
            : string.Empty;
    }

    private static void SetCuboidTextureEntryId(MultiCuboidBlockWorkbench workbench, int index, BlockFace face, string entryId)
    {
        if (workbench == null)
            return;

        workbench.SetCuboidTextureEntryId(index, face, entryId);
    }

    private static void ApplyCuboidFaceUvRect(
        MultiCuboidBlockWorkbench workbench,
        int index,
        TextureAtlasGenerator generator,
        ref BlockModelCuboid cuboid,
        BlockFace face)
    {
        if (!cuboid.HasTextureOverride(face))
            return;

        if (MultiCuboidBlockWorkbenchEditor.TryResolveAtlasEntryIdUv(
                workbench,
                generator,
                GetCuboidTextureEntryId(workbench, index, face),
                out Rect entryUv))
        {
            cuboid.SetOverrideUvRectData(face, BlockAtlasUvUtility.RectToUvRectData(entryUv));
            return;
        }

        BlockTextureMapping fallbackMapping = default;
        if (workbench.TryGetTextureMapping(out fallbackMapping) &&
            fallbackMapping.TryGetUvRectData(face, out Vector4 baseUvRectData))
        {
            cuboid.SetOverrideUvRectData(face, baseUvRectData);
            return;
        }

        if (generator == null)
            return;

        cuboid.SetOverrideUvRectData(face, Vector4.zero);
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
        public Dictionary<BlockFace, Vector4> faceUvRects = new Dictionary<BlockFace, Vector4>();
        public Dictionary<BlockFace, string> faceEntryIds = new Dictionary<BlockFace, string>();
        public Dictionary<BlockFace, string> faceTextureRequests = new Dictionary<BlockFace, string>();
    }

    private sealed class BlockbenchTextureSource
    {
        public string key;
        public string displayName;
        public Vector2Int size;
        public Texture2D texture;
        public string assetPath;
        public bool destroyTextureOnCleanup = true;
    }

    private sealed class FaceTextureRequest
    {
        public string key;
        public string hash;
        public string entryId;
        public BlockbenchTextureSource source;
        public float u0;
        public float v0;
        public float u1;
        public float v1;
        public float rotation;
        public Vector2Int tile;
        public bool hasAssignedTile;
        public Vector4 uvRectData;
        public bool hasAssignedUvRect;
    }

    private sealed class ImportContext
    {
        public string modelPath;
        public BlockDataSO blockData;
        public Vector2Int atlasTiles;
        public Vector2Int textureSize;
        public int convertedFaceCount;
        public int importedTextureCount;
        public int importedFaceCount;
        public TextureAtlasGenerator atlasGenerator;
        public World world;
        public string atlasScenePath;
        public string atlasSceneName;
        public Scene previousActiveScene;
        public Scene temporaryAtlasScene;
        public bool openedTemporaryAtlasScene;
        public List<string> warnings = new List<string>();
        public Dictionary<string, BlockbenchTextureSource> textureSources = new Dictionary<string, BlockbenchTextureSource>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> textureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, FaceTextureRequest> faceTextureRequests = new Dictionary<string, FaceTextureRequest>(StringComparer.Ordinal);

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

        public bool CanImportTexturesToAtlas
        {
            get
            {
                return blockData != null &&
                       atlasGenerator != null &&
                       atlasTiles.x > 0 &&
                       atlasTiles.y > 0;
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
        string path = EditorUtility.OpenFilePanel("Importar JSON/BBModel do Blockbench", initialDirectory, string.Empty);
        if (string.IsNullOrEmpty(path))
            return false;

        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            EditorPrefs.SetString(LastDirectoryKey, directory);

        ImportContext context = null;
        try
        {
            string json = File.ReadAllText(path);
            JObject root = JObject.Parse(json);
            context = CreateContext(root, workbench.blockData, path);
            List<ImportedCuboidData> importedCuboids = ParseElements(root, context);
            if (importedCuboids.Count == 0)
                throw new InvalidDataException("O arquivo nao trouxe nenhum cuboide utilizavel para o formato MultiCuboid.");

            FinalizeFaceTextureRequests(context, importedCuboids, workbench.blockType);

            bool hasExistingMapping = workbench.TryGetTextureMapping(out BlockTextureMapping existingMapping);
            BlockTextureMapping? existingMappingValue = hasExistingMapping ? existingMapping : (BlockTextureMapping?)null;
            Dictionary<BlockFace, Vector2Int> baseTiles = BuildBaseTiles(importedCuboids, existingMappingValue);
            Dictionary<BlockFace, string> baseEntryIds = BuildBaseEntryIds(workbench, importedCuboids);
            Dictionary<BlockFace, Vector4> baseUvRects = BuildBaseUvRects(importedCuboids, baseTiles, baseEntryIds, existingMappingValue);
            List<BlockModelCuboid> cuboids = BuildCuboids(importedCuboids, baseTiles, baseEntryIds);

            workbench.ReplaceCuboids(cuboids);

            bool appliedTextureMapping = ApplyTextureMapping(workbench, baseTiles, baseUvRects, existingMappingValue);
            bool appliedBaseEntryIds = ApplyBaseTextureEntryIds(workbench, baseEntryIds);
            bool appliedCuboidEntryIds = ApplyImportedCuboidTextureEntryIds(workbench, importedCuboids, baseTiles, baseEntryIds);
            string message = BuildSummary(
                path,
                cuboids.Count,
                context,
                appliedTextureMapping || appliedBaseEntryIds || appliedCuboidEntryIds,
                workbench.blockData != null);

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
        finally
        {
            CleanupImportContext(context);
        }
    }

    private static string ResolveInitialDirectory()
    {
        string lastDirectory = EditorPrefs.GetString(LastDirectoryKey, string.Empty);
        if (!string.IsNullOrEmpty(lastDirectory) && Directory.Exists(lastDirectory))
            return lastDirectory;

        return Application.dataPath;
    }

    private static ImportContext CreateContext(JObject root, BlockDataSO blockData, string modelPath)
    {
        ImportContext context = new ImportContext
        {
            modelPath = modelPath,
            blockData = blockData,
            atlasTiles = ResolveAtlasTiles(blockData)
        };

        try
        {
            RegisterTextureSources(root, context);
            TryResolveAtlasImportTargets(context);

            if (!TryResolveTextureSize(root, out context.textureSize))
            {
                foreach (BlockbenchTextureSource source in context.textureSources.Values)
                {
                    if (source == null || source.size.x <= 0 || source.size.y <= 0)
                        continue;

                    context.textureSize = source.size;
                    break;
                }

                if (context.textureSize.x <= 0 || context.textureSize.y <= 0)
                {
                    context.AddWarning(
                        "texture_size",
                        "O JSON nao informou resolucao de textura utilizavel; a conversao automatica de UV para tiles pode ficar incompleta.");
                }
            }

            return context;
        }
        catch
        {
            CleanupImportContext(context);
            throw;
        }
    }

    private static void RegisterTextureSources(JObject root, ImportContext context)
    {
        if (!(root["textures"] is JToken texturesToken))
            return;

        if (texturesToken is JArray texturesArray)
        {
            for (int i = 0; i < texturesArray.Count; i++)
            {
                if (!(texturesArray[i] is JObject textureObject))
                    continue;

                string fallbackKey = i.ToString(CultureInfo.InvariantCulture);
                RegisterTextureSource(fallbackKey, textureObject, context);
            }

            return;
        }

        if (!(texturesToken is JObject texturesObject))
            return;

        foreach (JProperty property in texturesObject.Properties())
            RegisterTextureSource(property.Name, property.Value, context);
    }

    private static void RegisterTextureSource(string fallbackKey, JToken token, ImportContext context)
    {
        if (string.IsNullOrWhiteSpace(fallbackKey) || token == null)
            return;

        string safeFallbackKey = fallbackKey.Trim();
        if (token.Type == JTokenType.String)
        {
            string rawValue = token.Value<string>();
            if (string.IsNullOrWhiteSpace(rawValue))
                return;

            if (rawValue.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                context.textureAliases[safeFallbackKey] = rawValue.Trim();
                return;
            }

            if (TryLoadTextureSource(safeFallbackKey, safeFallbackKey, rawValue, null, context, out BlockbenchTextureSource directSource))
                context.textureSources[safeFallbackKey] = directSource;
            return;
        }

        if (!(token is JObject textureObject))
            return;

        string idKey = ResolveTextureKey(textureObject["id"], safeFallbackKey);
        string displayName = textureObject["name"] != null
            ? textureObject["name"].ToString()
            : idKey;

        string sourceCandidate = ReadTrimmedString(textureObject["source"]);
        string pathCandidate = ReadTrimmedString(textureObject["path"]);
        string relativePathCandidate = ReadTrimmedString(textureObject["relative_path"]);

        if (TryLoadTextureSource(idKey, displayName, sourceCandidate, textureObject, context, out BlockbenchTextureSource source))
            context.textureSources[idKey] = source;
        else if (TryLoadTextureSource(idKey, displayName, pathCandidate, textureObject, context, out source))
            context.textureSources[idKey] = source;
        else if (TryLoadTextureSource(idKey, displayName, relativePathCandidate, textureObject, context, out source))
            context.textureSources[idKey] = source;
        else
            context.AddWarning("missing_texture_source", "O arquivo referencia texturas do Blockbench, mas algumas nao puderam ser localizadas para entrar no atlas.");

        if (!string.Equals(idKey, safeFallbackKey, StringComparison.OrdinalIgnoreCase))
            context.textureAliases[safeFallbackKey] = idKey;
    }

    private static bool TryLoadTextureSource(
        string key,
        string displayName,
        string candidate,
        JObject textureObject,
        ImportContext context,
        out BlockbenchTextureSource source)
    {
        source = null;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        if (!TryLoadTextureFromCandidate(
                candidate,
                context.modelPath,
                out Texture2D texture,
                out string assetPath,
                out bool destroyTextureOnCleanup))
        {
            return false;
        }

        Vector2Int size = texture != null
            ? new Vector2Int(texture.width, texture.height)
            : Vector2Int.zero;

        if ((size.x <= 0 || size.y <= 0) && textureObject != null)
            TryReadNamedVector2(textureObject, "uv_width", "uv_height", out size);
        if ((size.x <= 0 || size.y <= 0) && textureObject != null)
            TryReadNamedVector2(textureObject, "width", "height", out size);

        source = new BlockbenchTextureSource
        {
            key = string.IsNullOrWhiteSpace(key) ? displayName : key.Trim(),
            displayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName.Trim(),
            size = size,
            texture = texture,
            assetPath = assetPath,
            destroyTextureOnCleanup = destroyTextureOnCleanup
        };
        return true;
    }

    private static bool TryLoadTextureFromCandidate(
        string candidate,
        string modelPath,
        out Texture2D texture,
        out string assetPath,
        out bool destroyTextureOnCleanup)
    {
        texture = null;
        assetPath = null;
        destroyTextureOnCleanup = true;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        string trimmed = candidate.Trim();
        byte[] data = null;

        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int base64Index = trimmed.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
            if (base64Index >= 0)
            {
                string base64 = trimmed.Substring(base64Index + "base64,".Length);
                try
                {
                    data = Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                    return false;
                }
            }
        }
        else
        {
            if (!TryResolveTextureFilePath(trimmed, modelPath, out string resolvedPath))
                return false;

            if (TryConvertAbsolutePathToAssetPath(resolvedPath, out string existingAssetPath))
            {
                AssetDatabase.ImportAsset(existingAssetPath, ImportAssetOptions.ForceUpdate);
                Texture2D existingAssetTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(existingAssetPath);
                if (existingAssetTexture != null)
                {
                    texture = existingAssetTexture;
                    assetPath = existingAssetPath;
                    destroyTextureOnCleanup = false;
                    return true;
                }
            }

            data = File.ReadAllBytes(resolvedPath);
        }

        if (data == null || data.Length == 0)
            return false;

        texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
        {
            name = Path.GetFileNameWithoutExtension(trimmed),
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        if (!ImageConversion.LoadImage(texture, data, false))
        {
            UnityEngine.Object.DestroyImmediate(texture);
            texture = null;
            return false;
        }

        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        return true;
    }

    private static bool TryConvertAbsolutePathToAssetPath(string absolutePath, out string assetPath)
    {
        assetPath = null;
        if (string.IsNullOrWhiteSpace(absolutePath))
            return false;

        string fullAssetsPath = Path.GetFullPath(Application.dataPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullCandidatePath = Path.GetFullPath(absolutePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!fullCandidatePath.StartsWith(fullAssetsPath, StringComparison.OrdinalIgnoreCase))
            return false;

        string relativePath = fullCandidatePath.Substring(fullAssetsPath.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        assetPath = string.IsNullOrEmpty(relativePath) ? "Assets" : $"Assets/{relativePath}";
        return true;
    }

    private static bool TryResolveTextureFilePath(string candidate, string modelPath, out string resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        List<string> candidates = new List<string>();
        string trimmed = candidate.Trim().Trim('"');

        if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                trimmed = new Uri(trimmed).LocalPath;
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        if (Path.IsPathRooted(trimmed))
        {
            candidates.Add(trimmed);
            if (!Path.HasExtension(trimmed))
                candidates.Add(trimmed + ".png");
        }
        else
        {
            string modelDirectory = Path.GetDirectoryName(modelPath);
            if (!string.IsNullOrEmpty(modelDirectory))
            {
                string combined = Path.Combine(modelDirectory, trimmed);
                candidates.Add(combined);
                if (!Path.HasExtension(combined))
                    candidates.Add(combined + ".png");

                string fileNameCandidate = Path.GetFileName(trimmed);
                if (!string.IsNullOrEmpty(fileNameCandidate))
                {
                    string siblingCandidate = Path.Combine(modelDirectory, fileNameCandidate);
                    candidates.Add(siblingCandidate);
                    if (!Path.HasExtension(siblingCandidate))
                        candidates.Add(siblingCandidate + ".png");
                }
            }
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            string current = candidates[i];
            if (string.IsNullOrWhiteSpace(current))
                continue;

            string normalized = current.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (!File.Exists(normalized))
                continue;

            resolvedPath = normalized;
            return true;
        }

        return false;
    }

    private static string ResolveTextureKey(JToken token, string fallbackKey)
    {
        if (token == null)
            return fallbackKey;

        return token.Type switch
        {
            JTokenType.Integer => token.Value<long>().ToString(CultureInfo.InvariantCulture),
            JTokenType.Float => Mathf.RoundToInt(token.Value<float>()).ToString(CultureInfo.InvariantCulture),
            JTokenType.String => string.IsNullOrWhiteSpace(token.Value<string>()) ? fallbackKey : token.Value<string>().Trim(),
            _ => fallbackKey
        };
    }

    private static string ReadTrimmedString(JToken token)
    {
        return token != null && token.Type == JTokenType.String
            ? token.Value<string>()?.Trim()
            : null;
    }

    private static void TryResolveAtlasImportTargets(ImportContext context)
    {
        TryResolveAtlasImportTargetsFromLoadedScenes(context);
        if (context.atlasGenerator == null &&
            context.blockData != null &&
            context.textureSources.Count > 0)
        {
            TryResolveAtlasImportTargetsFromProjectScenes(context);
        }

        if (context.textureSources.Count > 0 && context.blockData != null && context.atlasGenerator == null)
        {
            context.AddWarning(
                "missing_block_generator",
                "As texturas do Blockbench foram detectadas, mas nao encontrei um TextureAtlasGenerator de blocos nem nas cenas abertas nem nas cenas do projeto; mantive a geometria e tentei reaproveitar apenas os tiles compativeis.");
        }
    }

    private static void TryResolveAtlasImportTargetsFromLoadedScenes(ImportContext context)
    {
#if UNITY_2023_1_OR_NEWER
        World[] worlds = UnityEngine.Object.FindObjectsByType<World>(FindObjectsInactive.Include);
        TextureAtlasGenerator[] generators = UnityEngine.Object.FindObjectsByType<TextureAtlasGenerator>(FindObjectsInactive.Include);
#else
        World[] worlds = UnityEngine.Object.FindObjectsOfType<World>(true);
        TextureAtlasGenerator[] generators = UnityEngine.Object.FindObjectsOfType<TextureAtlasGenerator>(true);
#endif

        context.world = SelectBestWorld(context, worlds);
        context.atlasGenerator = SelectBestGenerator(context, generators, context.world);
        UpdateResolvedAtlasSceneInfo(context, context.atlasGenerator, context.world);
    }

    private static void TryResolveAtlasImportTargetsFromProjectScenes(ImportContext context)
    {
        List<string> candidateScenePaths = BuildAtlasImportCandidateScenePaths();
        for (int i = 0; i < candidateScenePaths.Count; i++)
        {
            string scenePath = candidateScenePaths[i];
            if (string.IsNullOrWhiteSpace(scenePath))
                continue;

            Scene alreadyLoadedScene = SceneManager.GetSceneByPath(scenePath);
            if (alreadyLoadedScene.IsValid() && alreadyLoadedScene.isLoaded)
                continue;

            Scene previousActiveScene = EditorSceneManager.GetActiveScene();
            Scene openedScene;
            try
            {
                openedScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            }
            catch
            {
                continue;
            }

            if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                EditorSceneManager.SetActiveScene(previousActiveScene);

            World sceneWorld = null;
            TextureAtlasGenerator sceneGenerator = null;
            try
            {
                sceneWorld = SelectBestWorld(context, CollectSceneComponents<World>(openedScene));
                sceneGenerator = SelectBestGenerator(context, CollectSceneComponents<TextureAtlasGenerator>(openedScene), sceneWorld);
                if (sceneGenerator == null)
                {
                    EditorSceneManager.CloseScene(openedScene, true);
                    continue;
                }

                context.previousActiveScene = previousActiveScene;
                context.temporaryAtlasScene = openedScene;
                context.openedTemporaryAtlasScene = true;
                context.world = sceneWorld;
                context.atlasGenerator = sceneGenerator;
                UpdateResolvedAtlasSceneInfo(context, sceneGenerator, sceneWorld);
                return;
            }
            catch
            {
                if (openedScene.IsValid() && openedScene.isLoaded)
                    EditorSceneManager.CloseScene(openedScene, true);
                throw;
            }
        }
    }

    private static List<T> CollectSceneComponents<T>(Scene scene) where T : Component
    {
        List<T> components = new List<T>();
        if (!scene.IsValid() || !scene.isLoaded)
            return components;

        GameObject[] roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            GameObject root = roots[i];
            if (root == null)
                continue;

            components.AddRange(root.GetComponentsInChildren<T>(true));
        }

        return components;
    }

    private static World SelectBestWorld(ImportContext context, IList<World> worlds)
    {
        World bestWorld = null;
        int bestWorldScore = int.MinValue;
        for (int i = 0; i < worlds.Count; i++)
        {
            World candidate = worlds[i];
            int score = ScoreImportWorldCandidate(context, candidate);
            if (score > bestWorldScore)
            {
                bestWorldScore = score;
                bestWorld = candidate;
            }
        }

        return bestWorld;
    }

    private static TextureAtlasGenerator SelectBestGenerator(
        ImportContext context,
        IList<TextureAtlasGenerator> generators,
        World bestWorld)
    {
        TextureAtlasGenerator bestGenerator = null;
        int bestGeneratorScore = int.MinValue;
        for (int i = 0; i < generators.Count; i++)
        {
            TextureAtlasGenerator candidate = generators[i];
            int score = ScoreImportGeneratorCandidate(context, candidate, bestWorld);
            if (score > bestGeneratorScore)
            {
                bestGeneratorScore = score;
                bestGenerator = candidate;
            }
        }

        return bestGenerator;
    }

    private static int ScoreImportWorldCandidate(ImportContext context, World candidate)
    {
        if (candidate == null)
            return int.MinValue;

        int score = 0;
        if (candidate.blockData == context.blockData && context.blockData != null)
            score += 1000;
        if (candidate.blockAtlasGenerator != null)
            score += 100;

        string sceneName = candidate.gameObject.scene.name.ToLowerInvariant();
        if (sceneName.Contains("game"))
            score += 80;
        if (sceneName.Contains("menu") || sceneName.Contains("workbench"))
            score -= 120;

        return score;
    }

    private static int ScoreImportGeneratorCandidate(
        ImportContext context,
        TextureAtlasGenerator candidate,
        World bestWorld)
    {
        if (candidate == null)
            return int.MinValue;

        int score = 0;
        if (bestWorld != null && ReferenceEquals(candidate, bestWorld.blockAtlasGenerator))
            score += 1000;

        string candidateName = candidate.name.ToLowerInvariant();
        if (candidateName.Contains("bloco") || candidateName.Contains("block"))
            score += 150;
        if (candidateName.Contains("item"))
            score -= 150;

        string sceneName = candidate.gameObject.scene.name.ToLowerInvariant();
        if (sceneName.Contains("game"))
            score += 80;
        if (sceneName.Contains("menu") || sceneName.Contains("workbench"))
            score -= 120;

        if (bestWorld != null && bestWorld.Material != null && candidate.targetMaterials != null)
        {
            for (int m = 0; m < candidate.targetMaterials.Count; m++)
            {
                Material candidateMaterial = candidate.targetMaterials[m];
                if (candidateMaterial == null)
                    continue;

                for (int w = 0; w < bestWorld.Material.Length; w++)
                {
                    if (ReferenceEquals(candidateMaterial, bestWorld.Material[w]))
                        score += 40;
                }
            }
        }

        return score;
    }

    private static List<string> BuildAtlasImportCandidateScenePaths()
    {
        List<string> scenePaths = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;
        for (int i = 0; i < buildScenes.Length; i++)
        {
            EditorBuildSettingsScene scene = buildScenes[i];
            if (scene == null || string.IsNullOrWhiteSpace(scene.path))
                continue;

            if (seen.Add(scene.path))
                scenePaths.Add(scene.path);
        }

        string[] sceneGuids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets/Scenes" });
        for (int i = 0; i < sceneGuids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(sceneGuids[i]);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (seen.Add(path))
                scenePaths.Add(path);
        }

        scenePaths.Sort((left, right) => ScoreScenePathForAtlasImport(right).CompareTo(ScoreScenePathForAtlasImport(left)));
        return scenePaths;
    }

    private static int ScoreScenePathForAtlasImport(string scenePath)
    {
        if (string.IsNullOrWhiteSpace(scenePath))
            return int.MinValue;

        string normalized = scenePath.Replace('\\', '/').ToLowerInvariant();
        string fileName = Path.GetFileNameWithoutExtension(normalized);
        int score = 0;

        if (fileName.Contains("game"))
            score += 1000;
        if (fileName.Contains("main"))
            score += 100;
        if (fileName.Contains("workbench"))
            score -= 1000;
        if (fileName.Contains("menu"))
            score -= 800;
        if (normalized.Contains("/_recovery/"))
            score -= 2000;

        return score;
    }

    private static void UpdateResolvedAtlasSceneInfo(
        ImportContext context,
        TextureAtlasGenerator atlasGenerator,
        World world)
    {
        Scene scene = atlasGenerator != null
            ? atlasGenerator.gameObject.scene
            : world != null
                ? world.gameObject.scene
                : default;
        if (!scene.IsValid())
            return;

        context.atlasScenePath = scene.path;
        context.atlasSceneName = scene.name;
    }

    private static void CleanupImportContext(ImportContext context)
    {
        if (context == null)
            return;

        HashSet<Texture2D> destroyedTextures = new HashSet<Texture2D>();
        foreach (BlockbenchTextureSource source in context.textureSources.Values)
        {
            if (source?.texture == null || !source.destroyTextureOnCleanup)
                continue;

            if (!destroyedTextures.Add(source.texture))
                continue;

            UnityEngine.Object.DestroyImmediate(source.texture);
        }

        if (context.openedTemporaryAtlasScene &&
            context.temporaryAtlasScene.IsValid() &&
            context.temporaryAtlasScene.isLoaded)
        {
            if (context.previousActiveScene.IsValid() &&
                context.previousActiveScene.isLoaded &&
                context.previousActiveScene != context.temporaryAtlasScene)
            {
                EditorSceneManager.SetActiveScene(context.previousActiveScene);
            }

            EditorSceneManager.CloseScene(context.temporaryAtlasScene, true);
        }
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

    private static void FinalizeFaceTextureRequests(
        ImportContext context,
        List<ImportedCuboidData> importedCuboids,
        BlockType blockType)
    {
        if (context == null || importedCuboids == null || context.faceTextureRequests.Count == 0)
            return;

        if (!context.CanImportTexturesToAtlas)
        {
            if (context.blockData == null)
            {
                context.AddWarning(
                    "missing_blockdata_for_texture_import",
                    "As texturas do Blockbench foram detectadas, mas faltou um BlockDataSO para reservar tiles e salvar o mapping base do bloco.");
            }

            return;
        }

        if (context.blockData != null &&
            context.atlasGenerator != null &&
            VoxelAtlasCompatibility.CaptureStableEntryIds(
                context.atlasGenerator,
                context.blockData,
                context.atlasTiles,
                context.blockData.atlasCoordinatesStartTopLeft))
        {
            EditorUtility.SetDirty(context.blockData);
        }

        EnsureTextureEntriesEnabled(context.atlasGenerator);
        bool changedAtlasGenerator = false;

        foreach (FaceTextureRequest request in context.faceTextureRequests.Values)
        {
            if (request == null || request.source == null || request.source.texture == null)
                continue;

            EnsureFaceTextureIdentity(blockType, request);
            Texture2D sourceTexture = EnsureSourceTextureAsset(blockType, request.source);
            if (sourceTexture == null)
                continue;

            if (!TryBuildRequestSourceRect(request, out RectInt sourceRect))
            {
                context.AddWarning(
                    "invalid_face_uv",
                    "Algumas faces usam UV invalido ou fora da textura-fonte; essas regioes precisam de ajuste manual.");
                continue;
            }

            int legacyIndex = RegisterTextureEntry(context.atlasGenerator, request, sourceTexture, sourceRect);
            if (legacyIndex < 0)
                continue;

            if (!TryConvertLegacyIndexToTile(legacyIndex, context.atlasTiles, context.blockData.atlasCoordinatesStartTopLeft, out Vector2Int tile))
            {
                context.AddWarning(
                    "atlas_capacity",
                    "O atlas configurado no BlockDataSO nao tem espaco suficiente para reservar mais tiles do import do Blockbench.");
                continue;
            }

            request.tile = tile;
            request.hasAssignedTile = true;
            if (context.atlasGenerator.TryGetLegacyTileUv(
                    tile,
                    context.atlasTiles,
                    context.blockData.atlasCoordinatesStartTopLeft,
                    out Rect uvRect))
            {
                request.uvRectData = BlockAtlasUvUtility.RectToUvRectData(uvRect);
                request.hasAssignedUvRect = true;
            }

            context.importedTextureCount++;
            changedAtlasGenerator = true;
        }

        if (changedAtlasGenerator)
        {
            EditorUtility.SetDirty(context.atlasGenerator);
            if (context.atlasGenerator.textureDatabase != null)
                EditorUtility.SetDirty(context.atlasGenerator.textureDatabase);
            context.atlasGenerator.GenerateAtlas();

            foreach (FaceTextureRequest request in context.faceTextureRequests.Values)
            {
                if (request == null || string.IsNullOrWhiteSpace(request.entryId))
                    continue;

                if (context.atlasGenerator.TryGetUv(request.entryId, out Rect uvRect))
                {
                    request.uvRectData = BlockAtlasUvUtility.RectToUvRectData(uvRect);
                    request.hasAssignedUvRect = true;
                }
            }

            if (context.world != null)
            {
                EditorUtility.SetDirty(context.world);
                context.world.RebuildBlockAtlasCompatibility();
            }
            else if (context.blockData != null)
            {
                VoxelAtlasCompatibility.Apply(
                    context.atlasGenerator,
                    context.blockData,
                    context.atlasTiles,
                    context.blockData.atlasCoordinatesStartTopLeft);
            }

            if (context.blockData != null)
                EditorUtility.SetDirty(context.blockData);

            Scene generatorScene = context.atlasGenerator.gameObject.scene;
            if (generatorScene.IsValid() && generatorScene.isLoaded)
                EditorSceneManager.MarkSceneDirty(generatorScene);

            if (context.world != null)
            {
                Scene worldScene = context.world.gameObject.scene;
                if (worldScene.IsValid() && worldScene.isLoaded)
                    EditorSceneManager.MarkSceneDirty(worldScene);
            }

            AssetDatabase.SaveAssets();
            if (context.openedTemporaryAtlasScene &&
                context.temporaryAtlasScene.IsValid() &&
                context.temporaryAtlasScene.isLoaded)
            {
                EditorSceneManager.SaveScene(context.temporaryAtlasScene);
            }
        }

        for (int i = 0; i < importedCuboids.Count; i++)
        {
            ImportedCuboidData cuboid = importedCuboids[i];
            foreach (KeyValuePair<BlockFace, string> pair in cuboid.faceTextureRequests)
            {
                if (!context.faceTextureRequests.TryGetValue(pair.Value, out FaceTextureRequest request) || !request.hasAssignedTile)
                    continue;

                cuboid.faceTiles[pair.Key] = request.tile;
                if (!string.IsNullOrWhiteSpace(request.entryId))
                    cuboid.faceEntryIds[pair.Key] = request.entryId;
                if (request.hasAssignedUvRect)
                    cuboid.faceUvRects[pair.Key] = request.uvRectData;
                context.importedFaceCount++;
            }
        }
    }

    private static void EnsureFaceTextureIdentity(BlockType blockType, FaceTextureRequest request)
    {
        if (request == null)
            return;

        string safeBlockName = AtlasKeyUtility.NormalizePathSegment(blockType.ToString());
        request.entryId ??= $"blockbench/{safeBlockName}/{request.hash}";
    }

    private static void EnsureTextureEntriesEnabled(TextureAtlasGenerator generator)
    {
        if (generator == null)
            return;

        if (!ShouldUseTextureEntries(generator))
        {
            if (generator.blockTextures != null && generator.blockTextures.Exists(texture => texture != null))
                generator.FillTextureEntriesFromLegacy();
        }

        if (CanRetireLegacyBlockTextures(generator))
            generator.blockTextures.Clear();

        generator.GetWritableTextureEntries();
    }

    private static bool ShouldUseTextureEntries(TextureAtlasGenerator generator)
    {
        return generator != null && generator.HasConfiguredTextureEntries();
    }

    private static bool CanRetireLegacyBlockTextures(TextureAtlasGenerator generator)
    {
        if (generator == null || generator.blockTextures == null || generator.blockTextures.Count == 0)
            return false;

        List<AtlasTextureEntry> configuredEntries = generator.GetAllTextureEntriesSnapshot();
        if (configuredEntries == null || configuredEntries.Count == 0)
            return false;

        for (int i = 0; i < generator.blockTextures.Count; i++)
        {
            Texture2D legacyTexture = generator.blockTextures[i];
            if (legacyTexture == null)
                continue;

            bool foundMatch = false;
            for (int e = 0; e < configuredEntries.Count; e++)
            {
                AtlasTextureEntry entry = configuredEntries[e];
                if (entry == null ||
                    entry.texture == null ||
                    entry.useSourceRect ||
                    entry.useUvSampling)
                {
                    continue;
                }

                if (ReferenceEquals(entry.texture, legacyTexture))
                {
                    foundMatch = true;
                    break;
                }
            }

            if (!foundMatch)
                return false;
        }

        return true;
    }

    private static Texture2D EnsureSourceTextureAsset(BlockType blockType, BlockbenchTextureSource source)
    {
        if (source == null || source.texture == null)
            return null;

        if (!string.IsNullOrWhiteSpace(source.assetPath))
        {
            Texture2D existingAssetTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(source.assetPath);
            if (existingAssetTexture != null)
            {
                ReplaceSourceTextureWithAsset(source, existingAssetTexture);
                return existingAssetTexture;
            }
        }

        EnsureSourceTextureIdentity(blockType, source);
        if (string.IsNullOrWhiteSpace(source.assetPath))
            return source.texture;

        string fullPath = ToProjectAbsolutePath(source.assetPath);
        string directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllBytes(fullPath, source.texture.EncodeToPNG());
        AssetDatabase.ImportAsset(source.assetPath, ImportAssetOptions.ForceUpdate);
        ConfigureImportedTextureAsset(source.assetPath);
        Texture2D importedAssetTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(source.assetPath);
        if (importedAssetTexture == null)
            return source.texture;

        ReplaceSourceTextureWithAsset(source, importedAssetTexture);
        return importedAssetTexture;
    }

    private static void EnsureSourceTextureIdentity(BlockType blockType, BlockbenchTextureSource source)
    {
        if (source == null || !string.IsNullOrWhiteSpace(source.assetPath))
            return;

        string safeBlockName = AtlasKeyUtility.NormalizePathSegment(blockType.ToString());
        string safeSourceName = AtlasKeyUtility.NormalizePathSegment(
            !string.IsNullOrWhiteSpace(source.displayName) ? source.displayName : source.key);
        string identityHash = Hash128.Compute(
            $"{source.key}|{source.displayName}|{source.size.x}|{source.size.y}").ToString();
        source.assetPath = $"Assets/Generated/BlockbenchImports/{safeBlockName}/textures/{safeSourceName}_{identityHash}.png";
    }

    private static void ReplaceSourceTextureWithAsset(BlockbenchTextureSource source, Texture2D assetTexture)
    {
        if (source == null || assetTexture == null)
            return;

        Texture2D previousTexture = source.texture;
        bool shouldDestroyPrevious =
            source.destroyTextureOnCleanup &&
            previousTexture != null &&
            !ReferenceEquals(previousTexture, assetTexture);

        source.texture = assetTexture;
        source.size = new Vector2Int(assetTexture.width, assetTexture.height);
        source.destroyTextureOnCleanup = false;

        if (shouldDestroyPrevious)
            UnityEngine.Object.DestroyImmediate(previousTexture);
    }

    private static bool TryBuildRequestSourceRect(FaceTextureRequest request, out RectInt sourceRect)
    {
        sourceRect = default;
        if (request == null || request.source == null)
            return false;

        int sourceWidth = request.source.size.x;
        int sourceHeight = request.source.size.y;
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return false;

        float minU = Mathf.Min(request.u0, request.u1);
        float maxU = Mathf.Max(request.u0, request.u1);
        float minV = Mathf.Min(request.v0, request.v1);
        float maxV = Mathf.Max(request.v0, request.v1);

        int xMin = Mathf.Clamp(Mathf.FloorToInt(minU + 0.0001f), 0, sourceWidth - 1);
        int xMax = Mathf.Clamp(Mathf.CeilToInt(maxU - 0.0001f), xMin + 1, sourceWidth);
        int yMinFromTop = Mathf.Clamp(Mathf.FloorToInt(minV + 0.0001f), 0, sourceHeight - 1);
        int yMaxFromTop = Mathf.Clamp(Mathf.CeilToInt(maxV - 0.0001f), yMinFromTop + 1, sourceHeight);

        sourceRect = new RectInt(
            xMin,
            sourceHeight - yMaxFromTop,
            xMax - xMin,
            yMaxFromTop - yMinFromTop);
        return sourceRect.width > 0 && sourceRect.height > 0;
    }

    private static int RegisterTextureEntry(
        TextureAtlasGenerator generator,
        FaceTextureRequest request,
        Texture2D texture,
        RectInt sourceRect)
    {
        if (generator == null || request == null || texture == null)
            return -1;

        List<AtlasTextureEntry> writableEntries = generator.GetWritableTextureEntries();
        if (writableEntries == null)
            return -1;

        int entryIndex = -1;
        for (int i = 0; i < writableEntries.Count; i++)
        {
            AtlasTextureEntry existingEntry = writableEntries[i];
            if (existingEntry == null || !string.Equals(existingEntry.id, request.entryId, StringComparison.Ordinal))
                continue;

            existingEntry.texture = texture;
            existingEntry.useUvSampling = true;
            existingEntry.sampledUvRect = new Vector4(request.u0, request.v0, request.u1, request.v1);
            existingEntry.useSourceRect = true;
            existingEntry.sourceRect = sourceRect;
            writableEntries[i] = existingEntry;
            entryIndex = i;
            break;
        }

        if (entryIndex < 0)
        {
            writableEntries.Add(new AtlasTextureEntry
            {
                id = request.entryId,
                texture = texture,
                useUvSampling = true,
                sampledUvRect = new Vector4(request.u0, request.v0, request.u1, request.v1),
                useSourceRect = true,
                sourceRect = sourceRect
            });
            entryIndex = writableEntries.Count - 1;
        }

        List<AtlasTextureEntry> configuredEntries = generator.GetAllTextureEntriesSnapshot();
        int legacyIndex = 0;
        for (int i = 0; i < configuredEntries.Count; i++)
        {
            AtlasTextureEntry current = configuredEntries[i];
            if (current == null || current.texture == null)
                continue;

            if (string.Equals(current.id, request.entryId, StringComparison.Ordinal))
                return legacyIndex;

            legacyIndex++;
        }

        return -1;
    }

    private static int RegisterLegacyTexture(TextureAtlasGenerator generator, Texture2D texture)
    {
        if (generator == null || texture == null)
            return -1;

        if (generator.blockTextures == null)
            generator.blockTextures = new List<Texture2D>();

        int entryIndex = -1;
        for (int i = 0; i < generator.blockTextures.Count; i++)
        {
            if (!ReferenceEquals(generator.blockTextures[i], texture))
                continue;

            entryIndex = i;
            break;
        }

        if (entryIndex < 0)
        {
            generator.blockTextures.Add(texture);
            entryIndex = generator.blockTextures.Count - 1;
        }

        int legacyIndex = 0;
        for (int i = 0; i < generator.blockTextures.Count; i++)
        {
            if (generator.blockTextures[i] == null)
                continue;

            if (i == entryIndex)
                return legacyIndex;

            legacyIndex++;
        }

        return -1;
    }

    private static bool TryConvertLegacyIndexToTile(
        int legacyIndex,
        Vector2Int atlasTiles,
        bool atlasOriginTopLeft,
        out Vector2Int tile)
    {
        tile = Vector2Int.zero;
        int capacity = Mathf.Max(0, atlasTiles.x) * Mathf.Max(0, atlasTiles.y);
        if (legacyIndex < 0 || capacity <= 0 || legacyIndex >= capacity)
            return false;

        int x = legacyIndex % atlasTiles.x;
        int yFromBottom = legacyIndex / atlasTiles.x;
        int y = atlasOriginTopLeft
            ? atlasTiles.y - 1 - yFromBottom
            : yFromBottom;
        tile = new Vector2Int(x, y);
        return true;
    }

    private static void ConfigureImportedTextureAsset(string assetPath)
    {
        if (!(AssetImporter.GetAtPath(assetPath) is TextureImporter importer))
            return;

        bool changed = false;
        if (!importer.isReadable)
        {
            importer.isReadable = true;
            changed = true;
        }

        if (importer.filterMode != FilterMode.Point)
        {
            importer.filterMode = FilterMode.Point;
            changed = true;
        }

        if (importer.wrapMode != TextureWrapMode.Clamp)
        {
            importer.wrapMode = TextureWrapMode.Clamp;
            changed = true;
        }

        if (importer.mipmapEnabled)
        {
            importer.mipmapEnabled = false;
            changed = true;
        }

        if (changed)
            importer.SaveAndReimport();
    }

    private static string ToProjectAbsolutePath(string assetPath)
    {
        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        string relativePath = assetPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(projectRoot, relativePath);
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

                Vector2Int faceTextureSize = context.textureSize;
                bool capturedFaceTextureRequest = TryCaptureFaceTextureRequest(
                    faceObject,
                    context,
                    out string requestKey,
                    out Vector2Int resolvedTextureSize);
                if (capturedFaceTextureRequest)
                {
                    importedCuboid.faceTextureRequests[face] = requestKey;
                    if (resolvedTextureSize.x > 0 && resolvedTextureSize.y > 0)
                        faceTextureSize = resolvedTextureSize;
                }

                if (!capturedFaceTextureRequest &&
                    TryResolveFaceTile(faceObject, context, faceTextureSize, out Vector2Int tile))
                {
                    importedCuboid.faceTiles[face] = tile;
                }
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
        if (usesRescale)
        {
            context.AddWarning(
                "rescale_rotation",
                "Rotacao com 'rescale' do Blockbench nao tem equivalente direto aqui; a importacao manteve a rotacao, mas sem o ajuste extra de escala.");
        }

        Vector3 normalizedEuler = BlockShapeUtility.NormalizeCuboidEulerRotation(euler);
        if (usedCustomPivot && (pivotModel - centerModel).sqrMagnitude > PivotEpsilon * PivotEpsilon)
        {
            Quaternion rotation = Quaternion.Euler(normalizedEuler);
            Vector3 rotatedCenterModel = pivotModel + rotation * (centerModel - pivotModel);
            Vector3 halfSizeModel = (maxModel - minModel) * 0.5f;
            cuboid.min = (rotatedCenterModel - halfSizeModel) / ModelUnitsPerVoxel;
            cuboid.max = (rotatedCenterModel + halfSizeModel) / ModelUnitsPerVoxel;
        }

        cuboid.eulerRotation = normalizedEuler;
    }

    private static bool TryCaptureFaceTextureRequest(
        JObject faceObject,
        ImportContext context,
        out string requestKey,
        out Vector2Int resolvedTextureSize)
    {
        requestKey = null;
        resolvedTextureSize = Vector2Int.zero;

        if (faceObject == null || faceObject["texture"] == null)
            return false;

        if (!context.CanImportTexturesToAtlas)
            return false;

        if (!TryResolveTextureReference(faceObject["texture"], context, out BlockbenchTextureSource source))
            return false;

        if (!TryReadRawUvCoordinates(faceObject, out float u0, out float v0, out float u1, out float v1))
            return false;

        if (Mathf.Abs(u1 - u0) <= 0.0001f || Mathf.Abs(v1 - v0) <= 0.0001f)
            return false;

        float rotation = ReadFloat(faceObject["rotation"]);
        if (Mathf.Abs(rotation) > 0.0001f)
        {
            context.AddWarning(
                "uv_rotation",
                "Rotacao de UV por face no Blockbench ainda nao e convertida automaticamente; a textura foi recortada sem girar.");
        }

        string canonicalKey =
            $"{source.key}|{u0.ToString("0.####", CultureInfo.InvariantCulture)}|{v0.ToString("0.####", CultureInfo.InvariantCulture)}|" +
            $"{u1.ToString("0.####", CultureInfo.InvariantCulture)}|{v1.ToString("0.####", CultureInfo.InvariantCulture)}|" +
            $"{rotation.ToString("0.####", CultureInfo.InvariantCulture)}";

        if (!context.faceTextureRequests.TryGetValue(canonicalKey, out FaceTextureRequest request))
        {
            request = new FaceTextureRequest
            {
                key = canonicalKey,
                hash = Hash128.Compute(canonicalKey).ToString(),
                source = source,
                u0 = u0,
                v0 = v0,
                u1 = u1,
                v1 = v1,
                rotation = rotation
            };
            context.faceTextureRequests[canonicalKey] = request;
        }

        requestKey = canonicalKey;
        resolvedTextureSize = source.size;
        return true;
    }

    private static bool TryResolveTextureReference(JToken textureToken, ImportContext context, out BlockbenchTextureSource source)
    {
        source = null;
        string reference = ResolveTextureKey(textureToken, string.Empty);
        if (string.IsNullOrWhiteSpace(reference))
            return false;

        string current = reference.Trim().TrimStart('#');
        HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
        {
            if (context.textureSources.TryGetValue(current, out source) && source != null)
                return true;

            if (!context.textureAliases.TryGetValue(current, out string alias))
                break;

            current = alias.Trim().TrimStart('#');
        }

        return false;
    }

    private static bool TryReadRawUvCoordinates(
        JObject faceObject,
        out float u0,
        out float v0,
        out float u1,
        out float v1)
    {
        u0 = 0f;
        v0 = 0f;
        u1 = 0f;
        v1 = 0f;

        if (faceObject["uv"] is JArray uvArray && uvArray.Count >= 4)
        {
            u0 = ReadFloat(uvArray[0]);
            v0 = ReadFloat(uvArray[1]);
            u1 = ReadFloat(uvArray[2]);
            v1 = ReadFloat(uvArray[3]);
            return true;
        }

        if (faceObject["uv"] is JArray originArray &&
            originArray.Count >= 2 &&
            faceObject["uv_size"] is JArray sizeArray &&
            sizeArray.Count >= 2)
        {
            u0 = ReadFloat(originArray[0]);
            v0 = ReadFloat(originArray[1]);
            u1 = u0 + ReadFloat(sizeArray[0]);
            v1 = v0 + ReadFloat(sizeArray[1]);
            return true;
        }

        return false;
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

    private static bool TryResolveFaceTile(JObject faceObject, ImportContext context, Vector2Int faceTextureSize, out Vector2Int tile)
    {
        tile = Vector2Int.zero;
        if (context.blockData == null ||
            context.atlasTiles.x <= 0 ||
            context.atlasTiles.y <= 0 ||
            faceTextureSize.x <= 0 ||
            faceTextureSize.y <= 0)
        {
            return false;
        }

        if (!TryReadUvRect(faceObject, out Rect uvRect))
            return false;

        float tileWidth = (float)faceTextureSize.x / context.atlasTiles.x;
        float tileHeight = (float)faceTextureSize.y / context.atlasTiles.y;
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

    private static Dictionary<BlockFace, Vector4> BuildBaseUvRects(
        List<ImportedCuboidData> importedCuboids,
        Dictionary<BlockFace, Vector2Int> baseTiles,
        Dictionary<BlockFace, string> baseEntryIds,
        BlockTextureMapping? existingMapping)
    {
        Dictionary<BlockFace, Vector4> baseUvRects = new Dictionary<BlockFace, Vector4>();

        for (int i = 0; i < SupportedFaces.Length; i++)
        {
            BlockFace face = SupportedFaces[i];

            if (baseEntryIds.TryGetValue(face, out string baseEntryId) && !string.IsNullOrWhiteSpace(baseEntryId))
            {
                for (int c = 0; c < importedCuboids.Count; c++)
                {
                    ImportedCuboidData imported = importedCuboids[c];
                    if (!imported.faceEntryIds.TryGetValue(face, out string importedEntryId) ||
                        !string.Equals(importedEntryId, baseEntryId, StringComparison.Ordinal) ||
                        !imported.faceUvRects.TryGetValue(face, out Vector4 importedUvRect) ||
                        !BlockAtlasUvUtility.IsValidUvRectData(importedUvRect))
                    {
                        continue;
                    }

                    baseUvRects[face] = importedUvRect;
                    break;
                }
            }

            if (baseUvRects.ContainsKey(face))
                continue;

            if (baseTiles.TryGetValue(face, out Vector2Int baseTile))
            {
                for (int c = 0; c < importedCuboids.Count; c++)
                {
                    ImportedCuboidData imported = importedCuboids[c];
                    if (!imported.faceTiles.TryGetValue(face, out Vector2Int importedTile) ||
                        importedTile != baseTile ||
                        !imported.faceUvRects.TryGetValue(face, out Vector4 importedUvRect) ||
                        !BlockAtlasUvUtility.IsValidUvRectData(importedUvRect))
                    {
                        continue;
                    }

                    baseUvRects[face] = importedUvRect;
                    break;
                }
            }

            if (baseUvRects.ContainsKey(face))
                continue;

            if (existingMapping.HasValue &&
                existingMapping.Value.TryGetUvRectData(face, out Vector4 existingUvRect) &&
                BlockAtlasUvUtility.IsValidUvRectData(existingUvRect))
            {
                baseUvRects[face] = existingUvRect;
            }
        }

        return baseUvRects;
    }

    private static Dictionary<BlockFace, string> BuildBaseEntryIds(
        MultiCuboidBlockWorkbench workbench,
        List<ImportedCuboidData> importedCuboids)
    {
        Dictionary<BlockFace, string> baseEntryIds = new Dictionary<BlockFace, string>();

        for (int i = 0; i < SupportedFaces.Length; i++)
        {
            BlockFace face = SupportedFaces[i];
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int c = 0; c < importedCuboids.Count; c++)
            {
                if (!importedCuboids[c].faceEntryIds.TryGetValue(face, out string entryId) ||
                    string.IsNullOrWhiteSpace(entryId))
                {
                    continue;
                }

                counts.TryGetValue(entryId, out int currentCount);
                counts[entryId] = currentCount + 1;
            }

            string preferredEntryId = string.Empty;
            if (workbench != null && workbench.blockData != null)
                workbench.blockData.TryGetTextureEntryId(workbench.blockType, face, out preferredEntryId);

            if (counts.Count > 0)
            {
                baseEntryIds[face] = ResolveMostCommonEntryId(counts, preferredEntryId);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(preferredEntryId))
                baseEntryIds[face] = preferredEntryId;
        }

        return baseEntryIds;
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

    private static string ResolveMostCommonEntryId(Dictionary<string, int> counts, string preferredEntryId)
    {
        string bestEntryId = string.Empty;
        int bestCount = int.MinValue;
        bool found = false;

        foreach (KeyValuePair<string, int> pair in counts)
        {
            bool betterCount = !found || pair.Value > bestCount;
            bool preferredTie =
                !string.IsNullOrWhiteSpace(preferredEntryId) &&
                pair.Value == bestCount &&
                string.Equals(pair.Key, preferredEntryId, StringComparison.Ordinal);
            bool lexicalTie =
                pair.Value == bestCount &&
                string.CompareOrdinal(pair.Key, bestEntryId) < 0;

            if (betterCount || preferredTie || lexicalTie)
            {
                bestEntryId = pair.Key;
                bestCount = pair.Value;
                found = true;
            }
        }

        return bestEntryId;
    }

    private static List<BlockModelCuboid> BuildCuboids(
        List<ImportedCuboidData> importedCuboids,
        Dictionary<BlockFace, Vector2Int> baseTiles,
        Dictionary<BlockFace, string> baseEntryIds)
    {
        List<BlockModelCuboid> cuboids = new List<BlockModelCuboid>(importedCuboids.Count);
        for (int i = 0; i < importedCuboids.Count; i++)
        {
            ImportedCuboidData imported = importedCuboids[i];
            BlockModelCuboid cuboid = imported.cuboid;
            cuboid.textureOverrideFaces = BlockCuboidFaceMask.None;

            foreach (KeyValuePair<BlockFace, Vector2Int> pair in imported.faceTiles)
            {
                if (IsImportedFaceUsingBaseTexture(imported, pair.Key, baseTiles, baseEntryIds))
                    continue;

                cuboid.textureOverrideFaces |= GetFaceMask(pair.Key);
                SetCuboidFaceTile(ref cuboid, pair.Key, pair.Value);
                if (imported.faceUvRects.TryGetValue(pair.Key, out Vector4 uvRectData) &&
                    BlockAtlasUvUtility.IsValidUvRectData(uvRectData))
                {
                    cuboid.SetOverrideUvRectData(pair.Key, uvRectData);
                }
            }

            cuboids.Add(cuboid);
        }

        return cuboids;
    }

    private static bool ApplyBaseTextureEntryIds(
        MultiCuboidBlockWorkbench workbench,
        Dictionary<BlockFace, string> baseEntryIds)
    {
        if (workbench == null || workbench.blockData == null || baseEntryIds == null || baseEntryIds.Count == 0)
            return false;

        bool willChange = false;
        for (int i = 0; i < SupportedFaces.Length; i++)
        {
            BlockFace face = SupportedFaces[i];
            if (!baseEntryIds.TryGetValue(face, out string entryId) || string.IsNullOrWhiteSpace(entryId))
                continue;

            if (!workbench.blockData.TryGetTextureEntryId(workbench.blockType, face, out string currentId) ||
                !string.Equals(currentId, entryId, StringComparison.Ordinal))
            {
                willChange = true;
                break;
            }
        }

        if (!willChange)
            return false;

        Undo.RecordObject(workbench.blockData, "Update Block Texture Entry IDs");
        bool changed = false;
        for (int i = 0; i < SupportedFaces.Length; i++)
        {
            BlockFace face = SupportedFaces[i];
            if (!baseEntryIds.TryGetValue(face, out string entryId) || string.IsNullOrWhiteSpace(entryId))
                continue;

            changed |= workbench.blockData.SetTextureEntryId(workbench.blockType, face, entryId);
        }

        if (!changed)
            return false;

        EditorUtility.SetDirty(workbench.blockData);
        AssetDatabase.SaveAssets();
        return true;
    }

    private static bool ApplyImportedCuboidTextureEntryIds(
        MultiCuboidBlockWorkbench workbench,
        List<ImportedCuboidData> importedCuboids,
        Dictionary<BlockFace, Vector2Int> baseTiles,
        Dictionary<BlockFace, string> baseEntryIds)
    {
        if (workbench == null)
            return false;

        bool changed = false;
        int cuboidCount = workbench.CuboidCount;

        for (int i = 0; i < cuboidCount; i++)
        {
            ImportedCuboidData imported = i < importedCuboids.Count ? importedCuboids[i] : null;
            for (int faceIndex = 0; faceIndex < SupportedFaces.Length; faceIndex++)
            {
                BlockFace face = SupportedFaces[faceIndex];
                string desiredEntryId = string.Empty;
                if (imported != null &&
                    imported.faceEntryIds.TryGetValue(face, out string importedEntryId) &&
                    !string.IsNullOrWhiteSpace(importedEntryId) &&
                    !IsImportedFaceUsingBaseTexture(imported, face, baseTiles, baseEntryIds))
                {
                    desiredEntryId = importedEntryId.Trim();
                }

                workbench.TryGetCuboidTextureEntryId(i, face, out string currentEntryId);
                currentEntryId = string.IsNullOrWhiteSpace(currentEntryId) ? string.Empty : currentEntryId.Trim();
                if (!string.Equals(currentEntryId, desiredEntryId, StringComparison.Ordinal))
                {
                    changed = true;
                    break;
                }
            }

            if (changed)
                break;
        }

        if (!changed)
            return false;

        Undo.RecordObject(workbench, "Update Workbench Cuboid Texture Entry IDs");
        for (int i = 0; i < cuboidCount; i++)
            workbench.ClearCuboidTextureEntryIds(i);

        for (int i = 0; i < importedCuboids.Count; i++)
        {
            ImportedCuboidData imported = importedCuboids[i];
            foreach (KeyValuePair<BlockFace, string> pair in imported.faceEntryIds)
            {
                if (string.IsNullOrWhiteSpace(pair.Value) ||
                    IsImportedFaceUsingBaseTexture(imported, pair.Key, baseTiles, baseEntryIds))
                {
                    continue;
                }

                workbench.SetCuboidTextureEntryId(i, pair.Key, pair.Value);
            }
        }

        MultiCuboidWorkbenchEditorGui.MarkWorkbenchDirty(workbench);
        return true;
    }

    private static bool IsImportedFaceUsingBaseTexture(
        ImportedCuboidData imported,
        BlockFace face,
        Dictionary<BlockFace, Vector2Int> baseTiles,
        Dictionary<BlockFace, string> baseEntryIds)
    {
        if (imported == null)
            return true;

        if (baseEntryIds != null &&
            baseEntryIds.TryGetValue(face, out string baseEntryId) &&
            !string.IsNullOrWhiteSpace(baseEntryId) &&
            imported.faceEntryIds.TryGetValue(face, out string importedEntryId) &&
            !string.IsNullOrWhiteSpace(importedEntryId))
        {
            return string.Equals(baseEntryId, importedEntryId, StringComparison.Ordinal);
        }

        return baseTiles != null &&
               baseTiles.TryGetValue(face, out Vector2Int baseTile) &&
               imported.faceTiles.TryGetValue(face, out Vector2Int importedTile) &&
               importedTile == baseTile;
    }

    private static bool ApplyTextureMapping(
        MultiCuboidBlockWorkbench workbench,
        Dictionary<BlockFace, Vector2Int> baseTiles,
        Dictionary<BlockFace, Vector4> baseUvRects,
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
            if (baseUvRects != null &&
                baseUvRects.TryGetValue(face, out Vector4 uvRectData) &&
                BlockAtlasUvUtility.IsValidUvRectData(uvRectData))
            {
                changed |= SetMappingFaceUvRect(ref mapping, face, uvRectData);
            }
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

        if (context.importedTextureCount > 0)
        {
            builder.Append("Texturas do Blockbench viraram ");
            builder.Append(context.importedTextureCount);
            builder.Append(" entrada(s) reservadas no atlas");
            if (context.atlasGenerator != null)
            {
                builder.Append(" via ");
                builder.Append(context.atlasGenerator.name);
            }

            builder.AppendLine(".");
        }

        if (context.openedTemporaryAtlasScene && !string.IsNullOrWhiteSpace(context.atlasSceneName))
        {
            builder.Append("O atlas foi atualizado automaticamente na cena ");
            builder.Append(context.atlasSceneName);
            builder.AppendLine(".");
        }

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
            if (context.importedFaceCount > 0)
                builder.AppendLine("As faces detectadas ja foram ligadas aos novos tiles importados.");
            else
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

    private static bool SetMappingFaceUvRect(ref BlockTextureMapping mapping, BlockFace face, Vector4 uvRectData)
    {
        if (!BlockAtlasUvUtility.IsValidUvRectData(uvRectData))
            return false;

        if (mapping.TryGetUvRectData(face, out Vector4 existingUvRect) &&
            Vector4.SqrMagnitude(existingUvRect - uvRectData) <= 1e-10f)
        {
            return false;
        }

        mapping.SetUvRectData(face, uvRectData);
        return true;
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
