using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class MultiCuboidBlockWorkbench : MonoBehaviour
{
    public const float DefaultSnapStep = 0.0625f;
    private const float MinCuboidSize = 0.01f;
    private static readonly Vector3 HalfVoxelOffset = Vector3.one * 0.5f;

    [Header("Block Source")]
    public BlockDataSO blockData;
    public BlockType blockType;

    [Header("Scene Editing")]
    public bool snapToGrid = true;
    [Min(0.001f)] public float snapStep = DefaultSnapStep;
    public bool syncContinuously = true;

    [Header("Visual Helpers")]
    public bool showVoxelBounds = true;
    public bool showHorizontalGrid = true;
    public bool showVerticalGrid = true;
    public Color voxelBoundsColor = new Color(1f, 1f, 1f, 0.72f);
    public Color selectedBoundsColor = new Color(1f, 0.78f, 0.18f, 1f);

    [Header("Placement Rotation Preview")]
    public bool showPlacementRotationPreview = true;
    public BlockPlacementAxis placementPreviewAxis = BlockPlacementAxis.X;
    public Color placementPreviewColor = new Color(0.16f, 0.78f, 1f, 0.86f);

    [Header("Model")]
    public List<BlockModelCuboid> cuboids = new List<BlockModelCuboid>();

    private bool rebuildingVisuals;
    private Material previewMaterial;

    public int CuboidCount
    {
        get
        {
            EnsureCuboidList();
            return cuboids.Count;
        }
    }

    private void Reset()
    {
        EnsureCuboidList();
        RebuildVisuals();
    }

    private void OnEnable()
    {
        EnsureCuboidList();
        if (GetWorkbenchChildCount() != cuboids.Count)
            RebuildVisuals();
    }

    private void Update()
    {
        if (rebuildingVisuals)
            return;

        EnsureCuboidList();

        if (GetWorkbenchChildCount() != cuboids.Count)
        {
            RebuildVisuals();
            return;
        }

        if (syncContinuously)
            SyncFromChildren();
    }

    public bool LoadFromBlockData()
    {
        EnsureCuboidList();
        cuboids.Clear();

        BlockMultiCuboidDefinition definition = FindDefinition();
        if (definition != null && definition.cuboids != null)
        {
            for (int i = 0; i < definition.cuboids.Count; i++)
                cuboids.Add(SanitizeCuboid(definition.cuboids[i], snapToGrid, snapStep));
        }

        RebuildVisuals();
        return definition != null;
    }

    public void SaveToBlockData()
    {
        if (blockData == null)
            return;

        SyncFromChildren();

#if UNITY_EDITOR
        Undo.RecordObject(blockData, "Save Multi Cuboid Workbench");
#endif

        BlockMultiCuboidDefinition definition = GetOrCreateDefinition();
        definition.blockType = blockType;
        if (definition.cuboids == null)
            definition.cuboids = new List<BlockModelCuboid>();

        definition.cuboids.Clear();
        EnsureCuboidList();
        for (int i = 0; i < cuboids.Count; i++)
            definition.cuboids.Add(SanitizeCuboid(cuboids[i], snapToGrid, snapStep));

        EnsureMappingUsesMultiCuboid();
        blockData.InitializeDictionary();

#if UNITY_EDITOR
        EditorUtility.SetDirty(blockData);
        AssetDatabase.SaveAssets();
#endif
    }

    public void SyncFromChildren()
    {
        if (rebuildingVisuals)
            return;

        EnsureCuboidList();
        MultiCuboidWorkbenchCuboid[] childMarkers = GetChildMarkers();
        for (int i = 0; i < childMarkers.Length; i++)
        {
            MultiCuboidWorkbenchCuboid marker = childMarkers[i];
            if (marker == null || marker.index < 0 || marker.index >= cuboids.Count)
                continue;

            cuboids[marker.index] = ReadCuboidFromChild(marker);
            ApplyCuboidToChild(marker.index, marker.transform);
        }
    }

    public void RebuildVisuals()
    {
        EnsureCuboidList();

        rebuildingVisuals = true;
        DestroyWorkbenchChildren();

        for (int i = 0; i < cuboids.Count; i++)
            CreateCuboidChild(i);

        rebuildingVisuals = false;
    }

    public int AddCuboid()
    {
        return AddCuboid(new BlockModelCuboid(new Vector3(0.25f, 0f, 0.25f), new Vector3(0.75f, 0.5f, 0.75f)));
    }

    public int AddCuboid(BlockModelCuboid cuboid)
    {
        EnsureCuboidList();

#if UNITY_EDITOR
        Undo.RecordObject(this, "Add Workbench Cuboid");
#endif

        cuboids.Add(SanitizeCuboid(cuboid, snapToGrid, snapStep));
        RebuildVisuals();
        SelectCuboid(cuboids.Count - 1);
        return cuboids.Count - 1;
    }

    public void DuplicateSelectedCuboid()
    {
        EnsureCuboidList();
        int selectedIndex = GetSelectedCuboidIndex();
        if (selectedIndex < 0 && cuboids.Count > 0)
            selectedIndex = cuboids.Count - 1;

        if (selectedIndex < 0 || selectedIndex >= cuboids.Count)
            return;

#if UNITY_EDITOR
        Undo.RecordObject(this, "Duplicate Workbench Cuboid");
#endif

        cuboids.Insert(selectedIndex + 1, cuboids[selectedIndex]);
        RebuildVisuals();
        SelectCuboid(selectedIndex + 1);
    }

    public void RemoveSelectedCuboid()
    {
        EnsureCuboidList();
        int selectedIndex = GetSelectedCuboidIndex();
        if (selectedIndex < 0 || selectedIndex >= cuboids.Count)
            return;

#if UNITY_EDITOR
        Undo.RecordObject(this, "Remove Workbench Cuboid");
#endif

        cuboids.RemoveAt(selectedIndex);
        RebuildVisuals();
        SelectCuboid(Mathf.Clamp(selectedIndex - 1, 0, cuboids.Count - 1));
    }

    public void ClearCuboids()
    {
        EnsureCuboidList();

#if UNITY_EDITOR
        Undo.RecordObject(this, "Clear Multi Cuboid Workbench");
#endif

        cuboids.Clear();
        RebuildVisuals();

#if UNITY_EDITOR
        Selection.activeGameObject = gameObject;
#endif
    }

    public void ApplyAnvilPreset(bool detailed)
    {
        EnsureCuboidList();

#if UNITY_EDITOR
        Undo.RecordObject(this, detailed ? "Apply Detailed Anvil Workbench Preset" : "Apply Simple Anvil Workbench Preset");
#endif

        cuboids.Clear();

        if (detailed)
        {
            cuboids.Add(new BlockModelCuboid(new Vector3(0.125f, 0f, 0.125f), new Vector3(0.875f, 0.25f, 0.875f)));
            cuboids.Add(new BlockModelCuboid(new Vector3(0.25f, 0.25f, 0.25f), new Vector3(0.75f, 0.5f, 0.75f)));
            cuboids.Add(new BlockModelCuboid(new Vector3(0.1875f, 0.5f, 0.25f), new Vector3(0.8125f, 0.75f, 0.75f)));
            cuboids.Add(new BlockModelCuboid(new Vector3(0f, 0.6875f, 0.3125f), new Vector3(0.25f, 0.9375f, 0.6875f)));
            cuboids.Add(new BlockModelCuboid(new Vector3(0.25f, 0.75f, 0.1875f), new Vector3(0.75f, 1f, 0.8125f)));
            cuboids.Add(new BlockModelCuboid(new Vector3(0.75f, 0.6875f, 0.3125f), new Vector3(1f, 0.9375f, 0.6875f)));
        }
        else
        {
            cuboids.Add(new BlockModelCuboid(new Vector3(0.125f, 0f, 0.125f), new Vector3(0.875f, 0.25f, 0.875f)));
            cuboids.Add(new BlockModelCuboid(new Vector3(0.25f, 0.25f, 0.25f), new Vector3(0.75f, 0.5f, 0.75f)));
            cuboids.Add(new BlockModelCuboid(new Vector3(0.125f, 0.5f, 0.25f), new Vector3(0.875f, 0.75f, 0.75f)));
            cuboids.Add(new BlockModelCuboid(new Vector3(0f, 0.75f, 0.125f), new Vector3(1f, 1f, 0.875f)));
        }

        for (int i = 0; i < cuboids.Count; i++)
            cuboids[i] = SanitizeCuboid(cuboids[i], snapToGrid, snapStep);

        RebuildVisuals();
        SelectCuboid(0);
    }

    public bool TryGetCuboid(int index, out BlockModelCuboid cuboid)
    {
        EnsureCuboidList();
        if (index < 0 || index >= cuboids.Count)
        {
            cuboid = default;
            return false;
        }

        cuboid = cuboids[index];
        return true;
    }

    public void SetCuboid(int index, BlockModelCuboid cuboid)
    {
        EnsureCuboidList();
        if (index < 0 || index >= cuboids.Count)
            return;

        cuboids[index] = SanitizeCuboid(cuboid, snapToGrid, snapStep);
        Transform child = GetChildTransform(index);
        if (child != null)
            ApplyCuboidToChild(index, child);
    }

    public int GetSelectedCuboidIndex()
    {
#if UNITY_EDITOR
        GameObject selected = Selection.activeGameObject;
        if (selected == null)
            return -1;

        MultiCuboidWorkbenchCuboid marker = selected.GetComponent<MultiCuboidWorkbenchCuboid>();
        if (marker == null || marker.owner != this)
            return -1;

        return marker.index;
#else
        return -1;
#endif
    }

    public void SelectCuboid(int index)
    {
#if UNITY_EDITOR
        Transform child = GetChildTransform(index);
        if (child != null)
            Selection.activeGameObject = child.gameObject;
#endif
    }

    public bool TryGetTextureMapping(out BlockTextureMapping mapping)
    {
        mapping = default;
        if (blockData == null || blockData.blockTextures == null)
            return false;

        int mappingIndex = FindTextureMappingIndex();
        if (mappingIndex < 0)
            return false;

        mapping = blockData.blockTextures[mappingIndex];
        mapping.EnsureDirectionalSideData();
        return true;
    }

    public BlockTextureMapping GetOrCreateTextureMapping()
    {
        if (blockData == null)
            return default;

        if (blockData.blockTextures == null)
            blockData.blockTextures = new List<BlockTextureMapping>();

        int mappingIndex = FindTextureMappingIndex();
        if (mappingIndex >= 0)
        {
            BlockTextureMapping existing = blockData.blockTextures[mappingIndex];
            existing.EnsureDirectionalSideData();
            existing.blockType = blockType;
            existing.renderShape = BlockRenderShape.MultiCuboid;
            existing.isEmpty = false;
            blockData.blockTextures[mappingIndex] = existing;
            return existing;
        }

        BlockTextureMapping created = CreateDefaultTextureMapping(blockType);
        blockData.blockTextures.Add(created);
        return created;
    }

    public void SetTextureMapping(BlockTextureMapping mapping)
    {
        if (blockData == null)
            return;

#if UNITY_EDITOR
        Undo.RecordObject(blockData, "Edit Block Texture Coordinates");
#endif

        if (blockData.blockTextures == null)
            blockData.blockTextures = new List<BlockTextureMapping>();

        mapping.blockType = blockType;
        mapping.renderShape = BlockRenderShape.MultiCuboid;
        mapping.isEmpty = false;
        mapping.EnsureDirectionalSideData();

        int mappingIndex = FindTextureMappingIndex();
        if (mappingIndex >= 0)
            blockData.blockTextures[mappingIndex] = mapping;
        else
            blockData.blockTextures.Add(mapping);

        blockData.InitializeDictionary();

#if UNITY_EDITOR
        EditorUtility.SetDirty(blockData);
        AssetDatabase.SaveAssets();
#endif
    }

    public static Vector3 ToSceneLocalCenter(BlockModelCuboid cuboid)
    {
        return (cuboid.min + cuboid.max) * 0.5f - HalfVoxelOffset;
    }

    public static Vector3 ToSceneLocalSize(BlockModelCuboid cuboid)
    {
        return cuboid.max - cuboid.min;
    }

    private void OnDrawGizmos()
    {
        Matrix4x4 previousMatrix = Gizmos.matrix;
        Color previousColor = Gizmos.color;
        Gizmos.matrix = transform.localToWorldMatrix;

        if (showHorizontalGrid)
            DrawHorizontalGrid();

        if (showVerticalGrid)
            DrawVerticalGrid();

        if (showVoxelBounds)
        {
            Gizmos.color = voxelBoundsColor;
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
        }

        int selectedIndex = GetSelectedCuboidIndex();
        if (selectedIndex >= 0 && TryGetCuboid(selectedIndex, out BlockModelCuboid selectedCuboid))
        {
            Gizmos.color = selectedBoundsColor;
            DrawCuboidWireGizmo(selectedCuboid);
        }

        Gizmos.color = new Color(1f, 0.25f, 0.2f, 0.85f);
        Gizmos.DrawLine(new Vector3(-0.5f, -0.56f, -0.56f), new Vector3(0.5f, -0.56f, -0.56f));
        Gizmos.color = new Color(0.2f, 0.9f, 0.3f, 0.85f);
        Gizmos.DrawLine(new Vector3(-0.56f, -0.5f, -0.56f), new Vector3(-0.56f, 0.5f, -0.56f));
        Gizmos.color = new Color(0.25f, 0.45f, 1f, 0.85f);
        Gizmos.DrawLine(new Vector3(-0.56f, -0.56f, -0.5f), new Vector3(-0.56f, -0.56f, 0.5f));

        Gizmos.matrix = previousMatrix;
        Gizmos.color = previousColor;
    }

    private static void DrawCuboidWireGizmo(BlockModelCuboid cuboid)
    {
        Matrix4x4 previousMatrix = Gizmos.matrix;
        Gizmos.matrix = Gizmos.matrix * Matrix4x4.TRS(
            ToSceneLocalCenter(cuboid),
            Quaternion.Euler(cuboid.eulerRotation),
            Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, ToSceneLocalSize(cuboid));
        Gizmos.matrix = previousMatrix;
    }

    private void DrawHorizontalGrid()
    {
        Gizmos.color = new Color(1f, 1f, 1f, 0.16f);
        const int divisions = 16;
        for (int i = 0; i <= divisions; i++)
        {
            float p = -0.5f + i / (float)divisions;
            Gizmos.DrawLine(new Vector3(-0.5f, -0.5f, p), new Vector3(0.5f, -0.5f, p));
            Gizmos.DrawLine(new Vector3(p, -0.5f, -0.5f), new Vector3(p, -0.5f, 0.5f));
        }
    }

    private void DrawVerticalGrid()
    {
        Gizmos.color = new Color(1f, 1f, 1f, 0.12f);
        const int divisions = 16;
        for (int i = 0; i <= divisions; i++)
        {
            float p = -0.5f + i / (float)divisions;
            Gizmos.DrawLine(new Vector3(-0.5f, p, -0.5f), new Vector3(0.5f, p, -0.5f));
            Gizmos.DrawLine(new Vector3(p, -0.5f, -0.5f), new Vector3(p, 0.5f, -0.5f));
            Gizmos.DrawLine(new Vector3(-0.5f, -0.5f, p), new Vector3(-0.5f, 0.5f, p));
            Gizmos.DrawLine(new Vector3(-0.5f, p, -0.5f), new Vector3(-0.5f, p, 0.5f));
        }
    }

    private void EnsureCuboidList()
    {
        if (cuboids == null)
            cuboids = new List<BlockModelCuboid>();
    }

    private BlockMultiCuboidDefinition FindDefinition()
    {
        if (blockData == null || blockData.multiCuboidShapes == null)
            return null;

        for (int i = 0; i < blockData.multiCuboidShapes.Count; i++)
        {
            BlockMultiCuboidDefinition definition = blockData.multiCuboidShapes[i];
            if (definition != null && definition.blockType == blockType)
                return definition;
        }

        return null;
    }

    private int FindTextureMappingIndex()
    {
        if (blockData == null || blockData.blockTextures == null)
            return -1;

        for (int i = 0; i < blockData.blockTextures.Count; i++)
        {
            if (blockData.blockTextures[i].blockType == blockType)
                return i;
        }

        return -1;
    }

    private BlockMultiCuboidDefinition GetOrCreateDefinition()
    {
        if (blockData.multiCuboidShapes == null)
            blockData.multiCuboidShapes = new List<BlockMultiCuboidDefinition>();

        BlockMultiCuboidDefinition definition = FindDefinition();
        if (definition != null)
            return definition;

        definition = new BlockMultiCuboidDefinition
        {
            blockType = blockType,
            cuboids = new List<BlockModelCuboid>()
        };
        blockData.multiCuboidShapes.Add(definition);
        return definition;
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
            if (mapping.blockType != blockType)
                continue;

            mapping.renderShape = BlockRenderShape.MultiCuboid;
            mapping.isEmpty = false;
            blockData.blockTextures[i] = mapping;
            return;
        }

        blockData.blockTextures.Add(CreateDefaultTextureMapping(blockType));
    }

    private static BlockTextureMapping CreateDefaultTextureMapping(BlockType blockType)
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

    private BlockModelCuboid ReadCuboidFromChild(MultiCuboidWorkbenchCuboid marker)
    {
        Transform child = marker.transform;
        Quaternion localRotation = child.localRotation;

        Vector3 size = Abs(child.localScale);
        Vector3 center01 = child.localPosition + HalfVoxelOffset;
        BlockModelCuboid existing = cuboids[marker.index];
        BlockModelCuboid cuboid = new BlockModelCuboid(center01 - size * 0.5f, center01 + size * 0.5f)
        {
            eulerRotation = BlockShapeUtility.NormalizeCuboidEulerRotation(localRotation.eulerAngles),
            faces = existing.EffectiveFaces
        };

        return SanitizeCuboid(cuboid, snapToGrid, snapStep);
    }

    private void CreateCuboidChild(int index)
    {
        GameObject child = GameObject.CreatePrimitive(PrimitiveType.Cube);
        child.name = GetCuboidChildName(index);
        child.transform.SetParent(transform, false);

        MultiCuboidWorkbenchCuboid marker = child.AddComponent<MultiCuboidWorkbenchCuboid>();
        marker.owner = this;
        marker.index = index;

        Renderer renderer = child.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = GetPreviewMaterial();
            ApplyColor(renderer, index);
        }

        ApplyCuboidToChild(index, child.transform);
    }

    private void ApplyCuboidToChild(int index, Transform child)
    {
        if (child == null || index < 0 || index >= cuboids.Count)
            return;

        BlockModelCuboid cuboid = SanitizeCuboid(cuboids[index], snapToGrid, snapStep);
        cuboids[index] = cuboid;

        child.localPosition = ToSceneLocalCenter(cuboid);
        child.localRotation = Quaternion.Euler(cuboid.eulerRotation);
        child.localScale = ToSceneLocalSize(cuboid);

        MultiCuboidWorkbenchCuboid marker = child.GetComponent<MultiCuboidWorkbenchCuboid>();
        if (marker != null)
        {
            marker.owner = this;
            marker.index = index;
        }
    }

    private void ApplyColor(Renderer renderer, int index)
    {
        Color color = Color.HSVToRGB(Mathf.Repeat(index * 0.17f, 1f), 0.48f, 0.9f);
        MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(propertyBlock);
        propertyBlock.SetColor("_BaseColor", color);
        propertyBlock.SetColor("_Color", color);
        renderer.SetPropertyBlock(propertyBlock);
    }

    private Material GetPreviewMaterial()
    {
        if (previewMaterial != null)
            return previewMaterial;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");

        previewMaterial = new Material(shader)
        {
            name = "Multi Cuboid Workbench Preview",
            hideFlags = HideFlags.HideAndDontSave
        };

        return previewMaterial;
    }

    private void DestroyWorkbenchChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (child.GetComponent<MultiCuboidWorkbenchCuboid>() == null)
                continue;

            if (Application.isPlaying)
                Destroy(child.gameObject);
            else
                DestroyImmediate(child.gameObject);
        }
    }

    private int GetWorkbenchChildCount()
    {
        int count = 0;
        for (int i = 0; i < transform.childCount; i++)
        {
            if (transform.GetChild(i).GetComponent<MultiCuboidWorkbenchCuboid>() != null)
                count++;
        }

        return count;
    }

    private MultiCuboidWorkbenchCuboid[] GetChildMarkers()
    {
        List<MultiCuboidWorkbenchCuboid> markers = new List<MultiCuboidWorkbenchCuboid>();
        for (int i = 0; i < transform.childCount; i++)
        {
            MultiCuboidWorkbenchCuboid marker = transform.GetChild(i).GetComponent<MultiCuboidWorkbenchCuboid>();
            if (marker != null)
                markers.Add(marker);
        }

        markers.Sort((a, b) => a.index.CompareTo(b.index));
        return markers.ToArray();
    }

    private Transform GetChildTransform(int index)
    {
        for (int i = 0; i < transform.childCount; i++)
        {
            MultiCuboidWorkbenchCuboid marker = transform.GetChild(i).GetComponent<MultiCuboidWorkbenchCuboid>();
            if (marker != null && marker.index == index)
                return marker.transform;
        }

        return null;
    }

    private static string GetCuboidChildName(int index)
    {
        return "Cuboid_" + (index + 1).ToString("00");
    }

    private static BlockModelCuboid SanitizeCuboid(BlockModelCuboid cuboid, bool snapToGrid, float snapStep)
    {
        Vector3 min = Vector3.Min(cuboid.min, cuboid.max);
        Vector3 max = Vector3.Max(cuboid.min, cuboid.max);

        if (snapToGrid)
        {
            min = SnapVector(min, snapStep);
            max = SnapVector(max, snapStep);
        }

        min = Clamp01(min);
        max = Clamp01(max);

        EnsureAxisSize(ref min.x, ref max.x);
        EnsureAxisSize(ref min.y, ref max.y);
        EnsureAxisSize(ref min.z, ref max.z);

        return new BlockModelCuboid
        {
            min = min,
            max = max,
            eulerRotation = BlockShapeUtility.NormalizeCuboidEulerRotation(cuboid.eulerRotation),
            faces = cuboid.faces == BlockCuboidFaceMask.None ? BlockCuboidFaceMask.All : cuboid.faces
        };
    }

    private static void EnsureAxisSize(ref float min, ref float max)
    {
        if (max - min >= MinCuboidSize)
            return;

        float center = Mathf.Clamp01((min + max) * 0.5f);
        min = center - MinCuboidSize * 0.5f;
        max = center + MinCuboidSize * 0.5f;

        if (min < 0f)
        {
            max -= min;
            min = 0f;
        }

        if (max > 1f)
        {
            min -= max - 1f;
            max = 1f;
        }

        min = Mathf.Clamp01(min);
        max = Mathf.Clamp01(max);
    }

    private static Vector3 SnapVector(Vector3 value, float step)
    {
        step = Mathf.Max(0.001f, step);
        return new Vector3(
            Mathf.Round(value.x / step) * step,
            Mathf.Round(value.y / step) * step,
            Mathf.Round(value.z / step) * step);
    }

    private static Vector3 Clamp01(Vector3 value)
    {
        return new Vector3(Mathf.Clamp01(value.x), Mathf.Clamp01(value.y), Mathf.Clamp01(value.z));
    }

    private static Vector3 Abs(Vector3 value)
    {
        return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
    }
}
