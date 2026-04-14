using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public sealed class MultiCuboidBlockEditorWindow : EditorWindow
{
    private const float DefaultSnapStep = 0.0625f;
    private const float PreviewHeight = 360f;
    private const float PreviewPadding = 34f;
    private const float MinCuboidSize = 0.01f;
    private const float TexturedPreviewPitch = 24f;

    private static readonly Vector3[] UnitCubeCorners =
    {
        new Vector3(0f, 0f, 0f),
        new Vector3(1f, 0f, 0f),
        new Vector3(1f, 1f, 0f),
        new Vector3(0f, 1f, 0f),
        new Vector3(0f, 0f, 1f),
        new Vector3(1f, 0f, 1f),
        new Vector3(1f, 1f, 1f),
        new Vector3(0f, 1f, 1f)
    };

    private static readonly int[,] EdgeIndices =
    {
        { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 },
        { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 },
        { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }
    };

    private BlockDataSO blockData;
    private BlockType selectedBlockType;
    private int selectedCuboidIndex = -1;
    private Vector2 scroll;
    private bool snapToGrid = true;
    private float snapStep = DefaultSnapStep;
    private float previewYaw = 35f;
    private bool showVoxelBounds = true;
    private PreviewRenderUtility previewRenderer;
    private Mesh texturedPreviewMesh;
    private Material texturedPreviewMaterial;
    private int texturedPreviewSignature = int.MinValue;
    private string previewStatusMessage;

    private struct PreviewFace
    {
        public Vector2[] points;
        public float depth;
        public Color fill;
        public Color outline;
        public bool selected;
    }

    [MenuItem("Tools/Voxel/Legacy Multi Cuboid Inspector")]
    public static void Open()
    {
        GetWindow<MultiCuboidBlockEditorWindow>("Legacy Multi Cuboid");
    }

    [MenuItem("Assets/Open Legacy Multi Cuboid Inspector", true)]
    private static bool ValidateOpenFromAsset()
    {
        return Selection.activeObject is BlockDataSO;
    }

    [MenuItem("Assets/Open Legacy Multi Cuboid Inspector")]
    private static void OpenFromAsset()
    {
        MultiCuboidBlockEditorWindow window = GetWindow<MultiCuboidBlockEditorWindow>("Legacy Multi Cuboid");
        window.blockData = Selection.activeObject as BlockDataSO;
        window.Focus();
    }

    private void OnEnable()
    {
        if (blockData == null && Selection.activeObject is BlockDataSO selectedBlockData)
            blockData = selectedBlockData;

        EnsurePreviewRenderer();
    }

    private void OnDisable()
    {
        DestroyTexturedPreviewResources();

        if (previewRenderer != null)
        {
            previewRenderer.Cleanup();
            previewRenderer = null;
        }
    }

    private void OnGUI()
    {
        DrawToolbar();

        if (blockData == null)
        {
            EditorGUILayout.HelpBox("Escolha um BlockDataSO para editar modelos MultiCuboid.", MessageType.Info);
            return;
        }

        EnsureDefinitionExists(false);
        DrawBlockControls();

        BlockMultiCuboidDefinition definition = GetDefinition(false);
        Rect previewRect = GUILayoutUtility.GetRect(10f, PreviewHeight, GUILayout.ExpandWidth(true));
        DrawPreview(previewRect, definition);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        DrawCuboidList(definition);
        EditorGUILayout.EndScrollView();
    }

    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        EditorGUI.BeginChangeCheck();
        blockData = (BlockDataSO)EditorGUILayout.ObjectField(blockData, typeof(BlockDataSO), false, GUILayout.MinWidth(180f));
        if (EditorGUI.EndChangeCheck())
        {
            selectedCuboidIndex = -1;
            if (blockData != null)
                blockData.InitializeDictionary();
        }

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Abrir Asset", EditorStyles.toolbarButton, GUILayout.Width(82f)) && blockData != null)
            Selection.activeObject = blockData;

        EditorGUILayout.EndHorizontal();
    }

    private void DrawBlockControls()
    {
        EditorGUILayout.Space(6f);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUI.BeginChangeCheck();
        BlockType newBlockType = (BlockType)EditorGUILayout.EnumPopup("Bloco", selectedBlockType);
        if (EditorGUI.EndChangeCheck())
        {
            selectedBlockType = newBlockType;
            selectedCuboidIndex = -1;
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Criar/usar entrada MultiCuboid"))
            EnsureDefinitionExists(true);

        if (GUILayout.Button("Aplicar preset bigorna simples"))
            ReplaceWithAnvilPreset(false);

        if (GUILayout.Button("Aplicar preset bigorna detalhada"))
            ReplaceWithAnvilPreset(true);

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        snapToGrid = EditorGUILayout.ToggleLeft("Snap", snapToGrid, GUILayout.Width(64f));
        snapStep = Mathf.Clamp(EditorGUILayout.FloatField("Passo", snapStep), 0.001f, 1f);
        previewYaw = EditorGUILayout.Slider("Rotacao preview", previewYaw, -180f, 180f);
        showVoxelBounds = EditorGUILayout.ToggleLeft("Limite 1x1", showVoxelBounds, GUILayout.Width(90f));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.HelpBox("Coordenadas locais usam 0..1. Um bloco como bigorna e apenas uma lista de caixas pequenas dentro desse voxel.", MessageType.None);
        EditorGUILayout.EndVertical();
    }

    private void DrawCuboidList(BlockMultiCuboidDefinition definition)
    {
        EditorGUILayout.Space(6f);

        if (definition == null)
        {
            EditorGUILayout.HelpBox("Este bloco ainda nao tem uma entrada MultiCuboid.", MessageType.Warning);
            return;
        }

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label($"Cuboides: {definition.cuboids.Count}", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Adicionar cuboide", GUILayout.Width(130f)))
            AddCuboid(definition, new BlockModelCuboid(new Vector3(0.25f, 0f, 0.25f), new Vector3(0.75f, 0.5f, 0.75f)));
        if (GUILayout.Button("Adicionar cubo inteiro", GUILayout.Width(145f)))
            AddCuboid(definition, new BlockModelCuboid(Vector3.zero, Vector3.one));
        EditorGUILayout.EndHorizontal();

        if (definition.cuboids.Count == 0)
        {
            EditorGUILayout.HelpBox("Adicione cuboides ou aplique um preset para comecar.", MessageType.Info);
            return;
        }

        selectedCuboidIndex = Mathf.Clamp(selectedCuboidIndex, 0, definition.cuboids.Count - 1);
        for (int i = 0; i < definition.cuboids.Count; i++)
            DrawCuboidEditor(definition, i);
    }

    private void DrawCuboidEditor(BlockMultiCuboidDefinition definition, int index)
    {
        BlockModelCuboid cuboid = definition.cuboids[index];
        bool selected = index == selectedCuboidIndex;

        Color previousBackgroundColor = GUI.backgroundColor;
        if (selected)
            GUI.backgroundColor = new Color(1f, 0.86f, 0.35f, 1f);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        GUI.backgroundColor = previousBackgroundColor;
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Toggle(selected, $"Cuboide {index + 1}", "Button", GUILayout.Width(110f)))
            selectedCuboidIndex = index;

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Duplicar", GUILayout.Width(74f)))
        {
            DuplicateCuboid(definition, index);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            return;
        }

        if (GUILayout.Button("Remover", GUILayout.Width(72f)))
        {
            RemoveCuboid(definition, index);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            return;
        }

        EditorGUILayout.EndHorizontal();

        EditorGUI.BeginChangeCheck();
        Vector3 min = EditorGUILayout.Vector3Field("Min", cuboid.min);
        Vector3 max = EditorGUILayout.Vector3Field("Max", cuboid.max);
        BlockCuboidFaceMask faces = (BlockCuboidFaceMask)EditorGUILayout.EnumFlagsField("Faces", cuboid.EffectiveFaces);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Snap agora", GUILayout.Width(95f)))
        {
            min = SnapVector(min);
            max = SnapVector(max);
            GUI.changed = true;
        }

        if (GUILayout.Button("Centralizar XZ", GUILayout.Width(105f)))
        {
            Vector3 size = max - min;
            min.x = 0.5f - size.x * 0.5f;
            max.x = 0.5f + size.x * 0.5f;
            min.z = 0.5f - size.z * 0.5f;
            max.z = 0.5f + size.z * 0.5f;
            GUI.changed = true;
        }

        if (GUILayout.Button("Altura total", GUILayout.Width(90f)))
        {
            min.y = 0f;
            max.y = 1f;
            GUI.changed = true;
        }

        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(blockData, "Edit Multi Cuboid Box");
            cuboid.min = snapToGrid ? SnapVector(min) : min;
            cuboid.max = snapToGrid ? SnapVector(max) : max;
            cuboid.faces = faces == BlockCuboidFaceMask.None ? BlockCuboidFaceMask.All : faces;
            definition.cuboids[index] = SanitizeCuboid(cuboid);
            MarkBlockDataDirty();
        }

        EditorGUILayout.EndVertical();
    }

    private BlockMultiCuboidDefinition EnsureDefinitionExists(bool recordUndo)
    {
        if (blockData == null)
            return null;

        if (blockData.multiCuboidShapes == null)
        {
            if (recordUndo)
                Undo.RecordObject(blockData, "Create Multi Cuboid List");

            blockData.multiCuboidShapes = new List<BlockMultiCuboidDefinition>();
        }

        BlockMultiCuboidDefinition definition = GetDefinition(false);
        if (definition != null)
        {
            if (recordUndo)
            {
                Undo.RecordObject(blockData, "Ensure Multi Cuboid Mapping");
                EnsureMappingUsesMultiCuboid();
                MarkBlockDataDirty();
            }

            return definition;
        }

        if (!recordUndo)
            return null;

        Undo.RecordObject(blockData, "Create Multi Cuboid Definition");
        definition = new BlockMultiCuboidDefinition
        {
            blockType = selectedBlockType,
            cuboids = new List<BlockModelCuboid>()
        };
        blockData.multiCuboidShapes.Add(definition);
        EnsureMappingUsesMultiCuboid();
        MarkBlockDataDirty();
        return definition;
    }

    private BlockMultiCuboidDefinition GetDefinition(bool create)
    {
        if (blockData == null || blockData.multiCuboidShapes == null)
            return create ? EnsureDefinitionExists(true) : null;

        for (int i = 0; i < blockData.multiCuboidShapes.Count; i++)
        {
            BlockMultiCuboidDefinition definition = blockData.multiCuboidShapes[i];
            if (definition != null && definition.blockType == selectedBlockType)
                return definition;
        }

        return create ? EnsureDefinitionExists(true) : null;
    }

    private void AddCuboid(BlockMultiCuboidDefinition definition, BlockModelCuboid cuboid)
    {
        if (definition == null)
            definition = EnsureDefinitionExists(true);
        if (definition == null)
            return;

        Undo.RecordObject(blockData, "Add Multi Cuboid Box");
        definition.cuboids.Add(SanitizeCuboid(cuboid));
        selectedCuboidIndex = definition.cuboids.Count - 1;
        EnsureMappingUsesMultiCuboid();
        MarkBlockDataDirty();
    }

    private void DuplicateCuboid(BlockMultiCuboidDefinition definition, int index)
    {
        if (definition == null || index < 0 || index >= definition.cuboids.Count)
            return;

        Undo.RecordObject(blockData, "Duplicate Multi Cuboid Box");
        definition.cuboids.Insert(index + 1, definition.cuboids[index]);
        selectedCuboidIndex = index + 1;
        MarkBlockDataDirty();
    }

    private void RemoveCuboid(BlockMultiCuboidDefinition definition, int index)
    {
        if (definition == null || index < 0 || index >= definition.cuboids.Count)
            return;

        Undo.RecordObject(blockData, "Remove Multi Cuboid Box");
        definition.cuboids.RemoveAt(index);
        selectedCuboidIndex = Mathf.Clamp(index - 1, 0, definition.cuboids.Count - 1);
        MarkBlockDataDirty();
    }

    private void ReplaceWithAnvilPreset(bool detailed)
    {
        BlockMultiCuboidDefinition definition = EnsureDefinitionExists(true);
        if (definition == null)
            return;

        Undo.RecordObject(blockData, detailed ? "Apply Detailed Anvil Multi Cuboid Preset" : "Apply Simple Anvil Multi Cuboid Preset");
        definition.cuboids.Clear();

        if (detailed)
        {
            definition.cuboids.Add(new BlockModelCuboid(new Vector3(0.125f, 0f, 0.125f), new Vector3(0.875f, 0.25f, 0.875f)));
            definition.cuboids.Add(new BlockModelCuboid(new Vector3(0.25f, 0.25f, 0.25f), new Vector3(0.75f, 0.5f, 0.75f)));
            definition.cuboids.Add(new BlockModelCuboid(new Vector3(0.1875f, 0.5f, 0.25f), new Vector3(0.8125f, 0.75f, 0.75f)));
            definition.cuboids.Add(new BlockModelCuboid(new Vector3(0f, 0.6875f, 0.3125f), new Vector3(0.25f, 0.9375f, 0.6875f)));
            definition.cuboids.Add(new BlockModelCuboid(new Vector3(0.25f, 0.75f, 0.1875f), new Vector3(0.75f, 1f, 0.8125f)));
            definition.cuboids.Add(new BlockModelCuboid(new Vector3(0.75f, 0.6875f, 0.3125f), new Vector3(1f, 0.9375f, 0.6875f)));
        }
        else
        {
            definition.cuboids.Add(new BlockModelCuboid(new Vector3(0.125f, 0f, 0.125f), new Vector3(0.875f, 0.25f, 0.875f)));
            definition.cuboids.Add(new BlockModelCuboid(new Vector3(0.25f, 0.25f, 0.25f), new Vector3(0.75f, 0.5f, 0.75f)));
            definition.cuboids.Add(new BlockModelCuboid(new Vector3(0.125f, 0.5f, 0.25f), new Vector3(0.875f, 0.75f, 0.75f)));
            definition.cuboids.Add(new BlockModelCuboid(new Vector3(0f, 0.75f, 0.125f), new Vector3(1f, 1f, 0.875f)));
        }

        selectedCuboidIndex = 0;
        EnsureMappingUsesMultiCuboid();
        MarkBlockDataDirty();
    }

    private void EnsureMappingUsesMultiCuboid()
    {
        if (blockData == null)
            return;

        if (blockData.blockTextures == null)
            blockData.blockTextures = new List<BlockTextureMapping>();

        for (int i = 0; i < blockData.blockTextures.Count; i++)
        {
            BlockTextureMapping mapping = blockData.blockTextures[i];
            if (mapping.blockType != selectedBlockType)
                continue;

            mapping.renderShape = BlockRenderShape.MultiCuboid;
            mapping.isEmpty = false;
            blockData.blockTextures[i] = mapping;
            return;
        }

        blockData.blockTextures.Add(new BlockTextureMapping
        {
            blockType = selectedBlockType,
            renderShape = BlockRenderShape.MultiCuboid,
            shapeMin = Vector3.zero,
            shapeMax = Vector3.one,
            isSolid = true,
            isEmpty = false,
            lightOpacity = 15,
            materialIndex = 0
        });
    }

    private void MarkBlockDataDirty()
    {
        if (blockData == null)
            return;

        blockData.InitializeDictionary();
        EditorUtility.SetDirty(blockData);
        Repaint();
    }

    private static BlockModelCuboid SanitizeCuboid(BlockModelCuboid cuboid)
    {
        Vector3 min = Clamp01(Vector3.Min(cuboid.min, cuboid.max));
        Vector3 max = Clamp01(Vector3.Max(cuboid.min, cuboid.max));

        if (max.x - min.x < MinCuboidSize) max.x = Mathf.Min(1f, min.x + MinCuboidSize);
        if (max.y - min.y < MinCuboidSize) max.y = Mathf.Min(1f, min.y + MinCuboidSize);
        if (max.z - min.z < MinCuboidSize) max.z = Mathf.Min(1f, min.z + MinCuboidSize);

        if (max.x <= min.x) min.x = Mathf.Max(0f, max.x - MinCuboidSize);
        if (max.y <= min.y) min.y = Mathf.Max(0f, max.y - MinCuboidSize);
        if (max.z <= min.z) min.z = Mathf.Max(0f, max.z - MinCuboidSize);

        BlockModelCuboid sanitized = new BlockModelCuboid
        {
            min = min,
            max = max,
            eulerRotation = BlockShapeUtility.NormalizeCuboidEulerRotation(cuboid.eulerRotation),
            faces = cuboid.faces == BlockCuboidFaceMask.None ? BlockCuboidFaceMask.All : cuboid.faces,
            textureOverrideFaces = cuboid.EffectiveTextureOverrideFaces,

            textureTop = cuboid.textureTop,
            textureBottom = cuboid.textureBottom,
            textureRight = cuboid.textureRight,
            textureLeft = cuboid.textureLeft,
            textureFront = cuboid.textureFront,
            textureBack = cuboid.textureBack,
          
        };
        sanitized.CopyUvRectOverrideDataFrom(cuboid);
        return sanitized;
    }

    private Vector3 SnapVector(Vector3 value)
    {
        float step = Mathf.Max(0.001f, snapStep);
        return Clamp01(new Vector3(
            Mathf.Round(value.x / step) * step,
            Mathf.Round(value.y / step) * step,
            Mathf.Round(value.z / step) * step));
    }

    private static Vector3 Clamp01(Vector3 value)
    {
        return new Vector3(
            Mathf.Clamp01(value.x),
            Mathf.Clamp01(value.y),
            Mathf.Clamp01(value.z));
    }

    private void DrawPreview(Rect rect, BlockMultiCuboidDefinition definition)
    {
        previewStatusMessage = null;
        GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);

        Rect labelRect = new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, 20f);
        GUI.Label(labelRect, definition == null ? "Preview vazio" : $"{selectedBlockType} - preview MultiCuboid", EditorStyles.miniBoldLabel);

        Rect contentRect = new Rect(rect.x + 6f, rect.y + 24f, rect.width - 12f, rect.height - 30f);
        bool drewTexturedPreview = definition != null &&
                                   definition.cuboids != null &&
                                   definition.cuboids.Count > 0 &&
                                   DrawTexturedPreview(contentRect, definition);

        if (!drewTexturedPreview)
            DrawLegacyPreviewContent(rect, definition);

        if (!string.IsNullOrEmpty(previewStatusMessage))
        {
            Rect statusRect = new Rect(rect.x + 8f, rect.yMax - 18f, rect.width - 16f, 14f);
            GUI.Label(statusRect, previewStatusMessage, EditorStyles.centeredGreyMiniLabel);
        }
    }

    private void DrawLegacyPreviewContent(Rect rect, BlockMultiCuboidDefinition definition)
    {
        List<PreviewFace> faces = new List<PreviewFace>(64);
        if (definition != null)
        {
            for (int i = 0; i < definition.cuboids.Count; i++)
                AddPreviewCuboidFaces(rect, faces, SanitizeCuboid(definition.cuboids[i]), i, i == selectedCuboidIndex);
        }

        faces.Sort((a, b) => a.depth.CompareTo(b.depth));

        Handles.BeginGUI();
        for (int i = 0; i < faces.Count; i++)
            DrawPreviewFace(faces[i]);

        if (showVoxelBounds)
            DrawPreviewBounds(rect);

        Handles.EndGUI();

        if (definition == null || definition.cuboids.Count == 0)
        {
            Rect centered = new Rect(rect.x, rect.center.y - 10f, rect.width, 20f);
            GUI.Label(centered, "Sem cuboides ainda", CenteredMiniLabel());
        }
    }

    private bool DrawTexturedPreview(Rect rect, BlockMultiCuboidDefinition definition)
    {
        previewStatusMessage = null;

        if (!EnsureTexturedPreviewResources())
            return false;

        if (previewRenderer == null || texturedPreviewMesh == null || texturedPreviewMaterial == null)
            return false;

        Rect safeRect = new Rect(rect.x, rect.y, Mathf.Max(1f, rect.width), Mathf.Max(1f, rect.height));
        previewRenderer.BeginPreview(safeRect, GUIStyle.none);

        Camera camera = previewRenderer.camera;
        camera.clearFlags = CameraClearFlags.Color;
        camera.backgroundColor = new Color(0.17f, 0.19f, 0.22f, 1f);
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 20f;
        camera.orthographic = true;
        camera.aspect = Mathf.Max(0.1f, safeRect.width / Mathf.Max(1f, safeRect.height));
        camera.orthographicSize = Mathf.Max(0.95f, 0.95f / camera.aspect);

        Quaternion rotation = Quaternion.Euler(TexturedPreviewPitch, previewYaw, 0f);
        Vector3 forward = rotation * Vector3.forward;
        camera.transform.position = -forward * 4f;
        camera.transform.rotation = rotation;

        previewRenderer.lights[0].intensity = 1.25f;
        previewRenderer.lights[0].transform.rotation = Quaternion.Euler(35f, 120f, 0f);
        previewRenderer.lights[1].intensity = 0.85f;
        previewRenderer.lights[1].transform.rotation = Quaternion.Euler(340f, 218f, 177f);
        previewRenderer.ambientColor = new Color(0.55f, 0.55f, 0.55f, 1f);

        for (int i = 0; i < definition.cuboids.Count; i++)
        {
            BlockModelCuboid cuboid = SanitizeCuboid(definition.cuboids[i]);
            Vector3 size = cuboid.max - cuboid.min;
            Vector3 center = (cuboid.min + cuboid.max) * 0.5f - Vector3.one * 0.5f;
            Matrix4x4 matrix = Matrix4x4.TRS(center, Quaternion.Euler(cuboid.eulerRotation), size);
            previewRenderer.DrawMesh(texturedPreviewMesh, matrix, texturedPreviewMaterial, 0);
        }

        previewRenderer.Render();
        Texture renderedTexture = previewRenderer.EndPreview();
        GUI.DrawTexture(safeRect, renderedTexture, ScaleMode.StretchToFill, false);
        previewStatusMessage = "Preview com textura do atlas.";
        return true;
    }

    private bool EnsureTexturedPreviewResources()
    {
        if (!MultiCuboidBlockPreviewResources.TryResolvePreviewConfig(blockData, selectedBlockType, out MultiCuboidBlockPreviewResources.PreviewConfig config))
        {
            previewStatusMessage = config.statusMessage;
            DestroyTexturedPreviewResources();
            return false;
        }

        previewStatusMessage = config.statusMessage;
        if (config.signature == texturedPreviewSignature &&
            texturedPreviewMesh != null &&
            texturedPreviewMaterial != null)
        {
            return true;
        }

        DestroyTexturedPreviewResources();
        texturedPreviewMesh = MultiCuboidBlockPreviewResources.CreateTexturedMesh(
            config.mapping,
            config.atlasTiles,
            config.atlasOriginTopLeft,
            config.atlasTexture,
            $"{selectedBlockType}_MultiCuboidPreviewMesh");
        texturedPreviewMaterial = MultiCuboidBlockPreviewResources.CreateTexturedMaterial(
            config.atlasTexture,
            $"{selectedBlockType}_MultiCuboidPreviewMaterial");
        texturedPreviewSignature = config.signature;
        return true;
    }

    private void DestroyTexturedPreviewResources()
    {
        texturedPreviewSignature = int.MinValue;

        if (texturedPreviewMaterial != null)
        {
            DestroyImmediate(texturedPreviewMaterial);
            texturedPreviewMaterial = null;
        }

        if (texturedPreviewMesh != null)
        {
            DestroyImmediate(texturedPreviewMesh);
            texturedPreviewMesh = null;
        }
    }

    private void EnsurePreviewRenderer()
    {
        if (previewRenderer != null)
            return;

        previewRenderer = new PreviewRenderUtility();
    }

    private void AddPreviewCuboidFaces(Rect rect, List<PreviewFace> faces, BlockModelCuboid cuboid, int index, bool selected)
    {
        Vector3 min = cuboid.min;
        Vector3 max = cuboid.max;
        Vector3[] p =
        {
            new Vector3(min.x, min.y, min.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(min.x, min.y, max.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(max.x, max.y, max.z),
            new Vector3(min.x, max.y, max.z)
        };

        Color baseColor = Color.HSVToRGB(Mathf.Repeat(index * 0.17f, 1f), selected ? 0.72f : 0.48f, selected ? 0.95f : 0.72f);
        AddPreviewFace(rect, faces, p, new[] { 0, 3, 2, 1 }, BlockFace.Back, baseColor, selected);
        AddPreviewFace(rect, faces, p, new[] { 4, 5, 6, 7 }, BlockFace.Front, baseColor, selected);
        AddPreviewFace(rect, faces, p, new[] { 0, 4, 7, 3 }, BlockFace.Left, baseColor, selected);
        AddPreviewFace(rect, faces, p, new[] { 1, 2, 6, 5 }, BlockFace.Right, baseColor, selected);
        AddPreviewFace(rect, faces, p, new[] { 3, 7, 6, 2 }, BlockFace.Top, baseColor, selected);
        AddPreviewFace(rect, faces, p, new[] { 0, 1, 5, 4 }, BlockFace.Bottom, baseColor, selected);
    }

    private void AddPreviewFace(Rect rect, List<PreviewFace> faces, Vector3[] corners, int[] faceIndices, BlockFace face, Color baseColor, bool selected)
    {
        Vector2[] points = new Vector2[faceIndices.Length];
        float depth = 0f;
        for (int i = 0; i < faceIndices.Length; i++)
        {
            Vector3 point = corners[faceIndices[i]];
            points[i] = ProjectPoint(rect, point);
            depth += ComputeDepth(point);
        }

        depth /= faceIndices.Length;
        float shade = face switch
        {
            BlockFace.Top => 1.18f,
            BlockFace.Bottom => 0.46f,
            BlockFace.Right => 0.86f,
            BlockFace.Left => 0.7f,
            BlockFace.Front => 0.96f,
            _ => 0.62f
        };

        Color fill = new Color(baseColor.r * shade, baseColor.g * shade, baseColor.b * shade, selected ? 0.92f : 0.82f);
        faces.Add(new PreviewFace
        {
            points = points,
            depth = depth,
            fill = fill,
            outline = selected ? new Color(1f, 0.82f, 0.18f, 1f) : new Color(0.05f, 0.06f, 0.07f, 0.82f),
            selected = selected
        });
    }

    private void DrawPreviewFace(PreviewFace face)
    {
        Vector3[] fillPoints = ToHandlePoints(face.points, false);
        Vector3[] outlinePoints = ToHandlePoints(face.points, true);
        Handles.color = face.fill;
        Handles.DrawAAConvexPolygon(fillPoints);

        Handles.color = face.outline;
        Handles.DrawAAPolyLine(face.selected ? 3f : 1.5f, outlinePoints);
    }

    private void DrawPreviewBounds(Rect rect)
    {
        Handles.color = new Color(1f, 1f, 1f, 0.42f);
        for (int i = 0; i < EdgeIndices.GetLength(0); i++)
        {
            Vector2 a = ProjectPoint(rect, UnitCubeCorners[EdgeIndices[i, 0]]);
            Vector2 b = ProjectPoint(rect, UnitCubeCorners[EdgeIndices[i, 1]]);
            Handles.DrawAAPolyLine(1.5f, new Vector3(a.x, a.y, 0f), new Vector3(b.x, b.y, 0f));
        }
    }

    private Vector2 ProjectPoint(Rect rect, Vector3 point)
    {
        Bounds projectedBounds = ComputeProjectedUnitBounds();
        Vector2 projected = ProjectUnit(point);

        float availableWidth = Mathf.Max(1f, rect.width - PreviewPadding * 2f);
        float availableHeight = Mathf.Max(1f, rect.height - PreviewPadding * 2f);
        float scale = Mathf.Min(availableWidth / projectedBounds.size.x, availableHeight / projectedBounds.size.y);
        Vector2 projectedCenter = new Vector2(projectedBounds.center.x, projectedBounds.center.y);
        Vector2 centered = (projected - projectedCenter) * scale;
        return rect.center + new Vector2(centered.x, centered.y + 12f);
    }

    private Bounds ComputeProjectedUnitBounds()
    {
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);
        for (int i = 0; i < UnitCubeCorners.Length; i++)
        {
            Vector2 projected = ProjectUnit(UnitCubeCorners[i]);
            min = Vector2.Min(min, projected);
            max = Vector2.Max(max, projected);
        }

        Vector2 size = max - min;
        return new Bounds(min + size * 0.5f, size);
    }

    private Vector2 ProjectUnit(Vector3 point)
    {
        Vector3 local = point - Vector3.one * 0.5f;
        float radians = previewYaw * Mathf.Deg2Rad;
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);

        float rx = local.x * cos - local.z * sin;
        float rz = local.x * sin + local.z * cos;
        float sx = (rx - rz) * 0.8660254f;
        float sy = (rx + rz) * 0.5f - local.y;
        return new Vector2(sx, sy);
    }

    private float ComputeDepth(Vector3 point)
    {
        Vector3 local = point - Vector3.one * 0.5f;
        float radians = previewYaw * Mathf.Deg2Rad;
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);
        float rx = local.x * cos - local.z * sin;
        float rz = local.x * sin + local.z * cos;
        return rx + rz - local.y * 0.02f;
    }

    private static Vector3[] ToHandlePoints(Vector2[] points, bool closed)
    {
        int length = points.Length + (closed ? 1 : 0);
        Vector3[] result = new Vector3[length];
        for (int i = 0; i < points.Length; i++)
            result[i] = new Vector3(points[i].x, points[i].y, 0f);

        if (closed)
            result[length - 1] = result[0];

        return result;
    }

    private static GUIStyle CenteredMiniLabel()
    {
        GUIStyle style = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
        {
            alignment = TextAnchor.MiddleCenter
        };
        return style;
    }
}

internal static class MultiCuboidBlockPreviewResources
{
    internal struct PreviewConfig
    {
        public BlockTextureMapping mapping;
        public Texture atlasTexture;
        public Vector2Int atlasTiles;
        public bool atlasOriginTopLeft;
        public int signature;
        public string statusMessage;
    }

    internal static bool TryResolvePreviewConfig(BlockDataSO blockData, BlockType blockType, out PreviewConfig config)
    {
        config = default;

        if (blockData == null)
        {
            config.statusMessage = "Escolha um BlockDataSO para ver textura no preview.";
            return false;
        }

        blockData.InitializeDictionary();
        BlockTextureMapping? mappingResult = blockData.GetMapping(blockType);
        if (mappingResult == null)
        {
            config.statusMessage = "Este bloco ainda nao tem mapping de textura.";
            return false;
        }

        BlockTextureMapping mapping = mappingResult.Value;
        mapping.EnsureDirectionalSideData();

        if (!TryResolveAtlasSource(blockData, mapping.materialIndex, out Texture atlasTexture, out Vector2Int atlasTiles))
        {
            config.statusMessage = "Abra uma cena com World usando este BlockDataSO para ver o atlas no preview.";
            return false;
        }

        config = new PreviewConfig
        {
            mapping = mapping,
            atlasTexture = atlasTexture,
            atlasTiles = atlasTiles,
            atlasOriginTopLeft = blockData.atlasCoordinatesStartTopLeft,
            signature = ComputeSignature(blockData, blockType, mapping, atlasTexture, atlasTiles, blockData.atlasCoordinatesStartTopLeft),
            statusMessage = "Preview com textura do atlas."
        };
        return true;
    }

    internal static Material CreateTexturedMaterial(Texture atlasTexture, string materialName)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
            shader = Shader.Find("Unlit/Texture");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");

        Material material = new Material(shader)
        {
            name = materialName,
            hideFlags = HideFlags.HideAndDontSave
        };

        if (material.HasProperty("_Atlas"))
            material.SetTexture("_Atlas", atlasTexture);
        if (material.HasProperty("_BaseMap"))
            material.SetTexture("_BaseMap", atlasTexture);
        if (material.HasProperty("_MainTex"))
            material.SetTexture("_MainTex", atlasTexture);

        material.mainTexture = atlasTexture;

        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", Color.white);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", Color.white);
        if (material.HasProperty("_Glossiness"))
            material.SetFloat("_Glossiness", 0f);
        if (material.HasProperty("_Smoothness"))
            material.SetFloat("_Smoothness", 0f);
        if (material.HasProperty("_Cull"))
            material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        if (material.HasProperty("_CullMode"))
            material.SetFloat("_CullMode", (float)UnityEngine.Rendering.CullMode.Off);
        if (material.HasProperty("_Surface"))
            material.SetFloat("_Surface", 0f);

        material.doubleSidedGI = true;
        return material;
    }

    internal static Mesh CreateTexturedMesh(
        BlockTextureMapping mapping,
        Vector2Int atlasTiles,
        bool atlasOriginTopLeft,
        Texture atlasTexture,
        string meshName)
    {
        Mesh mesh = new Mesh
        {
            name = meshName,
            hideFlags = HideFlags.HideAndDontSave
        };

        Vector3[] vertices =
        {
            new Vector3(0.5f, -0.5f, -0.5f),
            new Vector3(0.5f, 0.5f, -0.5f),
            new Vector3(0.5f, 0.5f, 0.5f),
            new Vector3(0.5f, -0.5f, 0.5f),

            new Vector3(-0.5f, -0.5f, 0.5f),
            new Vector3(-0.5f, 0.5f, 0.5f),
            new Vector3(-0.5f, 0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f, -0.5f),

            new Vector3(-0.5f, 0.5f, -0.5f),
            new Vector3(-0.5f, 0.5f, 0.5f),
            new Vector3(0.5f, 0.5f, 0.5f),
            new Vector3(0.5f, 0.5f, -0.5f),

            new Vector3(-0.5f, -0.5f, 0.5f),
            new Vector3(-0.5f, -0.5f, -0.5f),
            new Vector3(0.5f, -0.5f, -0.5f),
            new Vector3(0.5f, -0.5f, 0.5f),

            new Vector3(0.5f, -0.5f, 0.5f),
            new Vector3(0.5f, 0.5f, 0.5f),
            new Vector3(-0.5f, 0.5f, 0.5f),
            new Vector3(-0.5f, -0.5f, 0.5f),

            new Vector3(-0.5f, -0.5f, -0.5f),
            new Vector3(-0.5f, 0.5f, -0.5f),
            new Vector3(0.5f, 0.5f, -0.5f),
            new Vector3(0.5f, -0.5f, -0.5f)
        };

        Vector3[] normals =
        {
            Vector3.right, Vector3.right, Vector3.right, Vector3.right,
            Vector3.left, Vector3.left, Vector3.left, Vector3.left,
            Vector3.up, Vector3.up, Vector3.up, Vector3.up,
            Vector3.down, Vector3.down, Vector3.down, Vector3.down,
            Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward,
            Vector3.back, Vector3.back, Vector3.back, Vector3.back
        };

        Vector2[] uvs = new Vector2[vertices.Length];
        ApplyFaceUvs(uvs, 0, BlockFace.Right, mapping, atlasTiles, atlasOriginTopLeft, atlasTexture, vertices[0], vertices[1], vertices[2], vertices[3]);
        ApplyFaceUvs(uvs, 4, BlockFace.Left, mapping, atlasTiles, atlasOriginTopLeft, atlasTexture, vertices[4], vertices[5], vertices[6], vertices[7]);
        ApplyFaceUvs(uvs, 8, BlockFace.Top, mapping, atlasTiles, atlasOriginTopLeft, atlasTexture, vertices[8], vertices[9], vertices[10], vertices[11]);
        ApplyFaceUvs(uvs, 12, BlockFace.Bottom, mapping, atlasTiles, atlasOriginTopLeft, atlasTexture, vertices[12], vertices[13], vertices[14], vertices[15]);
        ApplyFaceUvs(uvs, 16, BlockFace.Front, mapping, atlasTiles, atlasOriginTopLeft, atlasTexture, vertices[16], vertices[17], vertices[18], vertices[19]);
        ApplyFaceUvs(uvs, 20, BlockFace.Back, mapping, atlasTiles, atlasOriginTopLeft, atlasTexture, vertices[20], vertices[21], vertices[22], vertices[23]);

        int[] triangles =
        {
            0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7, 4, 6, 5, 4, 7, 6,
            8, 9, 10, 8, 10, 11, 8, 10, 9, 8, 11, 10,
            12, 13, 14, 12, 14, 15, 12, 14, 13, 12, 15, 14,
            16, 17, 18, 16, 18, 19, 16, 18, 17, 16, 19, 18,
            20, 21, 22, 20, 22, 23, 20, 22, 21, 20, 23, 22
        };

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void ApplyFaceUvs(
        Vector2[] uvs,
        int startIndex,
        BlockFace face,
        BlockTextureMapping mapping,
        Vector2Int atlasTiles,
        bool atlasOriginTopLeft,
        Texture atlasTexture,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3)
    {
        Rect uvRect = BlockAtlasUvUtility.ResolveUvRect(mapping, face, atlasTiles, atlasOriginTopLeft);
        if (atlasTexture != null && atlasTexture.width > 0 && atlasTexture.height > 0)
        {
            float insetU = 0.5f / atlasTexture.width;
            float insetV = 0.5f / atlasTexture.height;
            uvRect = Rect.MinMaxRect(
                uvRect.xMin + insetU,
                uvRect.yMin + insetV,
                uvRect.xMax - insetU,
                uvRect.yMax - insetV);
        }

        uvs[startIndex + 0] = RemapUv(ResolveLocalFaceUv(face, p0), uvRect);
        uvs[startIndex + 1] = RemapUv(ResolveLocalFaceUv(face, p1), uvRect);
        uvs[startIndex + 2] = RemapUv(ResolveLocalFaceUv(face, p2), uvRect);
        uvs[startIndex + 3] = RemapUv(ResolveLocalFaceUv(face, p3), uvRect);
    }

    private static Vector2 ResolveLocalFaceUv(BlockFace face, Vector3 localVertex)
    {
        Vector3 normalized = localVertex + Vector3.one * 0.5f;
        switch (face)
        {
            case BlockFace.Top:
                return new Vector2(Mathf.Clamp01(normalized.x), Mathf.Clamp01(normalized.z));

            case BlockFace.Bottom:
                return new Vector2(Mathf.Clamp01(normalized.x), 1f - Mathf.Clamp01(normalized.z));

            case BlockFace.Front:
                return new Vector2(Mathf.Clamp01(normalized.x), Mathf.Clamp01(normalized.y));

            case BlockFace.Back:
                return new Vector2(1f - Mathf.Clamp01(normalized.x), Mathf.Clamp01(normalized.y));

            case BlockFace.Right:
                return new Vector2(Mathf.Clamp01(normalized.z), Mathf.Clamp01(normalized.y));

            case BlockFace.Left:
                return new Vector2(1f - Mathf.Clamp01(normalized.z), Mathf.Clamp01(normalized.y));

            default:
                return Vector2.zero;
        }
    }

    private static Vector2 RemapUv(Vector2 localUv, Rect uvRect)
    {
        return new Vector2(
            Mathf.Lerp(uvRect.xMin, uvRect.xMax, Mathf.Clamp01(localUv.x)),
            Mathf.Lerp(uvRect.yMin, uvRect.yMax, Mathf.Clamp01(localUv.y)));
    }

    private static bool TryResolveAtlasSource(BlockDataSO blockData, int preferredMaterialIndex, out Texture atlasTexture, out Vector2Int atlasTiles)
    {
        atlasTexture = null;
        atlasTiles = Vector2Int.zero;

        if (!TryFindWorld(blockData, out World world) || world == null)
            return false;

        atlasTexture = ResolveAtlasTexture(world, preferredMaterialIndex);
        if (atlasTexture == null)
            return false;

        Vector2Int configuredTiles = ResolveConfiguredAtlasTiles(blockData, world);
        if (!TryResolveAtlasLayout(atlasTexture, configuredTiles, out atlasTiles))
            return false;

        return true;
    }

    private static bool TryFindWorld(BlockDataSO blockData, out World world)
    {
        world = null;
        World[] worlds = Resources.FindObjectsOfTypeAll<World>();
        for (int i = 0; i < worlds.Length; i++)
        {
            World candidate = worlds[i];
            if (candidate == null || EditorUtility.IsPersistent(candidate))
                continue;

            if (candidate.blockData == blockData)
            {
                world = candidate;
                return true;
            }
        }

        return false;
    }

    private static Texture ResolveAtlasTexture(World world, int preferredMaterialIndex)
    {
        if (world == null)
            return null;

        if (world.blockItemIconAtlasTexture != null)
            return world.blockItemIconAtlasTexture;

        Material[] materials = world.Material;
        if (materials == null || materials.Length == 0)
            return null;

        int safePreferredIndex = Mathf.Clamp(preferredMaterialIndex, 0, materials.Length - 1);
        Texture preferredTexture = TryGetAtlasTextureFromMaterial(materials[safePreferredIndex]);
        if (preferredTexture != null)
            return preferredTexture;

        for (int i = 0; i < materials.Length; i++)
        {
            if (i == safePreferredIndex)
                continue;

            Texture texture = TryGetAtlasTextureFromMaterial(materials[i]);
            if (texture != null)
                return texture;
        }

        return null;
    }

    private static Texture TryGetAtlasTextureFromMaterial(Material material)
    {
        if (material == null)
            return null;

        Texture atlasTexture = material.HasProperty("_Atlas") ? material.GetTexture("_Atlas") : null;
        if (atlasTexture == null && material.HasProperty("_BaseMap"))
            atlasTexture = material.GetTexture("_BaseMap");
        if (atlasTexture == null && material.HasProperty("_MainTex"))
            atlasTexture = material.GetTexture("_MainTex");
        if (atlasTexture == null)
            atlasTexture = material.mainTexture;

        return atlasTexture;
    }

    private static Vector2Int ResolveConfiguredAtlasTiles(BlockDataSO blockData, World world)
    {
        if (world != null && world.atlasTilesX > 0 && world.atlasTilesY > 0)
            return new Vector2Int(world.atlasTilesX, world.atlasTilesY);

        return new Vector2Int(
            Mathf.Max(1, Mathf.RoundToInt(blockData.atlasSize.x)),
            Mathf.Max(1, Mathf.RoundToInt(blockData.atlasSize.y)));
    }

    private static bool TryResolveAtlasLayout(Texture atlasTexture, Vector2Int atlasSetting, out Vector2Int resolvedAtlasTiles)
    {
        resolvedAtlasTiles = atlasSetting;
        if (atlasTexture == null || atlasSetting.x <= 0 || atlasSetting.y <= 0)
            return false;

        int interpretedTileWidth = atlasTexture.width / atlasSetting.x;
        int interpretedTileHeight = atlasTexture.height / atlasSetting.y;
        if (interpretedTileWidth <= 0 || interpretedTileHeight <= 0)
            return false;

        bool canInterpretAsTilePixelSize =
            atlasTexture.width % atlasSetting.x == 0 &&
            atlasTexture.height % atlasSetting.y == 0;

        bool configuredTileSizeIsSquare = atlasSetting.x == atlasSetting.y;
        bool interpretedTilesAreNonSquare = interpretedTileWidth != interpretedTileHeight;

        if (canInterpretAsTilePixelSize && configuredTileSizeIsSquare && interpretedTilesAreNonSquare)
        {
            resolvedAtlasTiles = new Vector2Int(
                Mathf.Max(1, atlasTexture.width / atlasSetting.x),
                Mathf.Max(1, atlasTexture.height / atlasSetting.y));
        }

        resolvedAtlasTiles.x = Mathf.Max(1, resolvedAtlasTiles.x);
        resolvedAtlasTiles.y = Mathf.Max(1, resolvedAtlasTiles.y);
        return true;
    }

    private static int ComputeSignature(
        BlockDataSO blockData,
        BlockType blockType,
        BlockTextureMapping mapping,
        Texture atlasTexture,
        Vector2Int atlasTiles,
        bool atlasOriginTopLeft)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (blockData != null ? blockData.GetHashCode() : 0);
            hash = hash * 31 + (int)blockType;
            hash = hash * 31 + (atlasTexture != null ? atlasTexture.GetHashCode() : 0);
            hash = hash * 31 + atlasTiles.x;
            hash = hash * 31 + atlasTiles.y;
            hash = hash * 31 + (atlasOriginTopLeft ? 1 : 0);
            hash = hash * 31 + mapping.materialIndex;
            hash = hash * 31 + mapping.top.x;
            hash = hash * 31 + mapping.top.y;
            hash = hash * 31 + mapping.bottom.x;
            hash = hash * 31 + mapping.bottom.y;
            hash = hash * 31 + mapping.right.x;
            hash = hash * 31 + mapping.right.y;
            hash = hash * 31 + mapping.left.x;
            hash = hash * 31 + mapping.left.y;
            hash = hash * 31 + mapping.front.x;
            hash = hash * 31 + mapping.front.y;
            hash = hash * 31 + mapping.back.x;
            hash = hash * 31 + mapping.back.y;
            return hash;
        }
    }
}
