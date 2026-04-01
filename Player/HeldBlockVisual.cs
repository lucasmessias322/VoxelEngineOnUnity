using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Sprites;

[DisallowMultipleComponent]
public class HeldBlockVisual : MonoBehaviour
{
    private const string FollowRootName = "HeldItemFollowRoot";
    private const string VisualRootName = "HeldItemVisual";
    private const string BlockVisualName = "BlockVisual";
    private const string FlatItemVisualName = "FlatItemVisual";
    private const string HeldPrefabRootName = "HeldPrefabRoot";

    private enum HeldVisualKind
    {
        None,
        Block,
        FlatItem,
        Prefab
    }

    [Header("References")]
    [SerializeField] private HotbarMirror hotbar;
    [SerializeField] private Transform handAnchor;
    [SerializeField] private World world;
    [SerializeField] private ItemAtlasDataSO itemAtlasData;

    [Header("Default View")]
    [SerializeField] private Vector3 localPosition = new Vector3(0.35f, -0.3f, 0.55f);
    [SerializeField] private Vector3 localEulerAngles = new Vector3(20f, -25f, -8f);
    [SerializeField] private float heldScale = 0.28f;
    [SerializeField] private float flatItemScale = 0.85f;
    [SerializeField] private bool hideWhenInventoryOpen = true;
    [SerializeField] private bool castShadows = false;
    [SerializeField] private bool receiveShadows = false;

    private readonly Dictionary<BlockType, Mesh> blockMeshCache = new Dictionary<BlockType, Mesh>();
    private readonly Dictionary<BlockType, int> blockMaterialIndexCache = new Dictionary<BlockType, int>();
    private readonly Dictionary<Sprite, Mesh> spriteFlatItemMeshCache = new Dictionary<Sprite, Mesh>();
    private readonly Dictionary<Item, Mesh> atlasFlatItemMeshCache = new Dictionary<Item, Mesh>();

    private GameObject followRoot;
    private GameObject visualRoot;
    private GameObject blockVisualObject;
    private MeshFilter blockMeshFilter;
    private MeshRenderer blockMeshRenderer;
    private GameObject flatItemVisualObject;
    private MeshFilter flatItemMeshFilter;
    private MeshRenderer flatItemMeshRenderer;
    private Material flatItemMaterial;
    private GameObject heldPrefabInstance;
    private GameObject heldPrefabVisualObject;
    private GameObject heldPrefabSource;
    private Item shownItem;
    private BlockType shownBlockType = BlockType.Air;
    private HeldVisualKind shownVisualKind;
    private bool isVisible;

    private void Awake()
    {
        ResolveReferences();
        EnsureVisualObject();
        UpdateFollowRootTransform();
        ApplyViewTransform(null, HeldVisualKind.None);
        RefreshHeldBlock(forceRefresh: true);
    }

    private void LateUpdate()
    {
        ResolveReferences();
        EnsureVisualObject();
        UpdateFollowRootTransform();
        RefreshHeldBlock(forceRefresh: false);
    }

    [ContextMenu("Refresh Held Visual")]
    public void RefreshNow()
    {
        ResolveReferences();
        EnsureVisualObject();
        UpdateFollowRootTransform();
        ApplyViewTransform(null, HeldVisualKind.None);
        RefreshHeldBlock(forceRefresh: true);
    }

    private void OnValidate()
    {
        if (!Application.isPlaying)
            return;

        ApplyViewTransform(shownItem, shownVisualKind);
        UpdateFollowRootTransform();
        ApplyRendererSettings();
        RefreshHeldBlock(forceRefresh: true);
    }

    private void OnDestroy()
    {
        DestroyCachedMeshes(blockMeshCache);
        blockMaterialIndexCache.Clear();
        DestroyCachedMeshes(spriteFlatItemMeshCache);
        DestroyCachedMeshes(atlasFlatItemMeshCache);

        if (flatItemMaterial != null)
            Destroy(flatItemMaterial);

        ClearHeldPrefabInstance();

        if (followRoot != null)
            Destroy(followRoot);

        followRoot = null;
        visualRoot = null;
        blockVisualObject = null;
        flatItemVisualObject = null;
        heldPrefabInstance = null;
    }

    private void OnDisable()
    {
        if (followRoot != null)
            followRoot.SetActive(false);
    }

    private void OnEnable()
    {
        if (followRoot != null)
            followRoot.SetActive(true);
    }

    private void ResolveReferences()
    {
        if (hotbar == null)
            hotbar = FindAnyObjectByType<HotbarMirror>();

        if (world == null)
            world = World.Instance != null ? World.Instance : FindAnyObjectByType<World>();

        if (handAnchor == null)
            handAnchor = transform;
    }

    private void EnsureVisualObject()
    {
        Transform stableParent = handAnchor != null ? handAnchor : transform;

        if (followRoot == null && stableParent != null)
            followRoot = FindDirectChildByName(stableParent, FollowRootName)?.gameObject;

        if (followRoot == null && transform != null && transform != stableParent)
            followRoot = FindDirectChildByName(transform, FollowRootName)?.gameObject;

        if (followRoot == null)
        {
            followRoot = new GameObject(FollowRootName);
            followRoot.transform.SetParent(stableParent, false);
        }
        else if (followRoot.transform.parent != stableParent)
        {
            followRoot.transform.SetParent(stableParent, false);
        }

        DestroyDuplicateChildren(stableParent, FollowRootName, followRoot.transform);

        if (transform != null && transform != stableParent)
            DestroyDuplicateChildren(transform, FollowRootName, followRoot.transform.parent == transform ? followRoot.transform : null);

        if (visualRoot == null && followRoot != null)
            visualRoot = FindDirectChildByName(followRoot.transform, VisualRootName)?.gameObject;

        if (visualRoot == null)
        {
            visualRoot = new GameObject(VisualRootName);
            visualRoot.transform.SetParent(followRoot.transform, false);
            visualRoot.SetActive(false);
        }
        else if (visualRoot.transform.parent != followRoot.transform)
        {
            visualRoot.transform.SetParent(followRoot.transform, false);
        }

        DestroyDuplicateChildren(followRoot.transform, VisualRootName, visualRoot.transform);

        if (blockVisualObject == null && visualRoot != null)
            blockVisualObject = FindDirectChildByName(visualRoot.transform, BlockVisualName)?.gameObject;

        if (blockVisualObject == null)
        {
            blockVisualObject = new GameObject(BlockVisualName);
            blockVisualObject.transform.SetParent(visualRoot.transform, false);
            blockMeshFilter = blockVisualObject.AddComponent<MeshFilter>();
            blockMeshRenderer = blockVisualObject.AddComponent<MeshRenderer>();
            blockVisualObject.SetActive(false);
        }

        if (flatItemVisualObject == null && visualRoot != null)
            flatItemVisualObject = FindDirectChildByName(visualRoot.transform, FlatItemVisualName)?.gameObject;

        if (flatItemVisualObject == null)
        {
            flatItemVisualObject = new GameObject(FlatItemVisualName);
            flatItemVisualObject.transform.SetParent(visualRoot.transform, false);
            flatItemMeshFilter = flatItemVisualObject.AddComponent<MeshFilter>();
            flatItemMeshRenderer = flatItemVisualObject.AddComponent<MeshRenderer>();
            flatItemVisualObject.SetActive(false);
        }

        if (blockMeshFilter == null)
            blockMeshFilter = blockVisualObject.GetComponent<MeshFilter>();
        if (blockMeshRenderer == null)
            blockMeshRenderer = blockVisualObject.GetComponent<MeshRenderer>();
        if (flatItemMeshFilter == null)
            flatItemMeshFilter = flatItemVisualObject.GetComponent<MeshFilter>();
        if (flatItemMeshRenderer == null)
            flatItemMeshRenderer = flatItemVisualObject.GetComponent<MeshRenderer>();

        if (heldPrefabInstance == null)
        {
            heldPrefabInstance = new GameObject(HeldPrefabRootName);
            heldPrefabInstance.transform.SetParent(visualRoot.transform, false);
            heldPrefabInstance.SetActive(false);
        }
        else if (heldPrefabInstance.transform.parent != visualRoot.transform)
        {
            heldPrefabInstance.transform.SetParent(visualRoot.transform, false);
        }

        EnsureFlatItemMaterial();
        ApplyRendererSettings();
    }

    private void UpdateFollowRootTransform()
    {
        if (followRoot == null)
            return;

        Transform stableParent = handAnchor != null ? handAnchor : transform;
        if (followRoot.transform.parent != stableParent)
            followRoot.transform.SetParent(stableParent, false);

        // followRoot.transform.localPosition = Vector3.zero;
        // followRoot.transform.localRotation = Quaternion.identity;
        followRoot.transform.localScale = GetNeutralizedLocalScale();
    }

    private Vector3 GetNeutralizedLocalScale()
    {
        Transform parentTransform = followRoot != null ? followRoot.transform.parent : null;
        if (parentTransform == null)
            return Vector3.one;

        Vector3 parentLossyScale = parentTransform.lossyScale;
        return new Vector3(
            GetSafeInverseScale(parentLossyScale.x),
            GetSafeInverseScale(parentLossyScale.y),
            GetSafeInverseScale(parentLossyScale.z));
    }

    private void ApplyViewTransform(Item selectedItem, HeldVisualKind visualKind)
    {
        if (visualRoot == null)
            return;

        Vector3 targetPosition = localPosition;
        Vector3 targetEulerAngles = localEulerAngles;
        Vector3 targetScale = GetDefaultRootScaleFor(visualKind);

        if (selectedItem != null && selectedItem.overrideHeldTransform)
        {
            targetPosition = selectedItem.heldLocalPosition;
            targetEulerAngles = selectedItem.heldLocalEulerAngles;

            if (visualKind != HeldVisualKind.Prefab)
                targetScale = SanitizeScale(selectedItem.heldLocalScale);
        }

        visualRoot.transform.localPosition = targetPosition;
        visualRoot.transform.localRotation = Quaternion.Euler(targetEulerAngles);
        visualRoot.transform.localScale = targetScale;
    }

    private Vector3 GetDefaultRootScaleFor(HeldVisualKind visualKind)
    {
        switch (visualKind)
        {
            case HeldVisualKind.FlatItem:
                return Vector3.one * Mathf.Max(0.01f, flatItemScale);
            case HeldVisualKind.Prefab:
                return Vector3.one;
            case HeldVisualKind.Block:
            case HeldVisualKind.None:
            default:
                return Vector3.one * Mathf.Max(0.01f, heldScale);
        }
    }

    private void ApplyRendererSettings()
    {
        if (blockMeshRenderer != null)
        {
            blockMeshRenderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            blockMeshRenderer.receiveShadows = receiveShadows;
        }

        if (flatItemMeshRenderer != null)
        {
            flatItemMeshRenderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            flatItemMeshRenderer.receiveShadows = receiveShadows;
        }
    }

    private void RefreshHeldBlock(bool forceRefresh)
    {
        if (visualRoot == null)
            return;

        if (hideWhenInventoryOpen && PlayerInventory.Instance != null && PlayerInventory.Instance.IsInventoryOpen)
        {
            ClearShownState();
            SetVisible(false);
            return;
        }

        if (hotbar == null || !hotbar.TryGetSelectedItem(out Item selectedItem) || selectedItem == null)
        {
            ClearShownState();
            SetVisible(false);
            return;
        }

        bool hasBlockSelection = TryResolveHeldBlockType(selectedItem, out BlockType selectedBlockType);
        HeldVisualKind desiredKind = GetDesiredVisualKind(selectedItem, hasBlockSelection);
        if (desiredKind == HeldVisualKind.None)
        {
            ClearShownState();
            SetVisible(false);
            return;
        }

        ApplyViewTransform(selectedItem, desiredKind);

        if (!forceRefresh &&
            isVisible &&
            shownItem == selectedItem &&
            shownBlockType == selectedBlockType &&
            shownVisualKind == desiredKind)
        {
            return;
        }

        bool shownSuccessfully = false;

        switch (desiredKind)
        {
            case HeldVisualKind.Prefab:
                shownSuccessfully = ShowHeldPrefab(selectedItem);
                break;
            case HeldVisualKind.Block:
                shownSuccessfully = ShowBlock(selectedBlockType);
                break;
            case HeldVisualKind.FlatItem:
                shownSuccessfully = ShowFlatItem(selectedItem);
                break;
        }

        if (!shownSuccessfully)
        {
            ClearShownState();
            SetVisible(false);
            return;
        }

        shownItem = selectedItem;
        shownBlockType = selectedBlockType;
        shownVisualKind = desiredKind;
        SetVisible(true);
    }

    private HeldVisualKind GetDesiredVisualKind(Item selectedItem, bool hasBlockSelection)
    {
        if (selectedItem == null)
            return HeldVisualKind.None;

        if (selectedItem.heldPrefab != null)
            return HeldVisualKind.Prefab;

        if (hasBlockSelection)
            return HeldVisualKind.Block;

        if (HasFlatItemVisual(selectedItem))
            return HeldVisualKind.FlatItem;

        return HeldVisualKind.None;
    }

    private bool TryResolveHeldBlockType(Item selectedItem, out BlockType blockType)
    {
        blockType = BlockType.Air;
        if (selectedItem == null || selectedItem.inventoryIconMode == InventoryIconMode.ItemIconOnly)
            return false;

        PlayerInventory inventory = PlayerInventory.Instance != null
            ? PlayerInventory.Instance
            : FindAnyObjectByType<PlayerInventory>();

        return inventory != null &&
               inventory.TryGetBlockForItem(selectedItem, out blockType) &&
               blockType != BlockType.Air;
    }

    private bool HasFlatItemVisual(Item selectedItem)
    {
        return selectedItem != null && (selectedItem.icon != null || HasAtlasFlatItem(selectedItem));
    }

    private bool ShowBlock(BlockType blockType)
    {
        if (blockType == BlockType.Air)
            return false;

        if (world == null)
            world = World.Instance;

        if (world == null || world.Material == null || world.Material.Length == 0)
            return false;

        Mesh heldMesh = GetOrCreateBlockMesh(blockType, out int materialIndex);
        Material heldMaterial = ResolveHeldBlockMaterial(materialIndex);
        if (heldMesh == null || heldMaterial == null)
            return false;

        HideAllVisualChildren();
        blockMeshFilter.sharedMesh = heldMesh;
        blockMeshRenderer.sharedMaterial = heldMaterial;
        blockVisualObject.SetActive(true);
        return true;
    }

    private bool ShowFlatItem(Item selectedItem)
    {
        if (selectedItem == null || flatItemMeshFilter == null || flatItemMeshRenderer == null)
            return false;

        Mesh flatMesh = GetOrCreateFlatItemMesh(selectedItem, out Material renderMaterial);
        if (flatMesh == null || renderMaterial == null)
            return false;

        HideAllVisualChildren();
        flatItemMeshFilter.sharedMesh = flatMesh;
        flatItemMeshRenderer.sharedMaterial = renderMaterial;
        flatItemVisualObject.SetActive(true);
        return true;
    }

    private bool ShowHeldPrefab(Item selectedItem)
    {
        if (selectedItem == null || selectedItem.heldPrefab == null)
            return false;

        GameObject instance = EnsureHeldPrefabInstance(selectedItem.heldPrefab);
        if (instance == null)
            return false;

        HideAllVisualChildren();
        ApplyHeldPrefabScale(instance.transform, selectedItem);
        instance.SetActive(true);
        return true;
    }

    private GameObject EnsureHeldPrefabInstance(GameObject prefab)
    {
        if (prefab == null || heldPrefabInstance == null)
            return null;

        if (heldPrefabVisualObject != null && heldPrefabSource == prefab)
            return heldPrefabInstance;

        ClearHeldPrefabInstance();

        heldPrefabVisualObject = Instantiate(prefab, heldPrefabInstance.transform, false);
        heldPrefabVisualObject.name = prefab.name + "_Held";
        heldPrefabVisualObject.transform.localPosition = Vector3.zero;
        heldPrefabVisualObject.transform.localRotation = Quaternion.identity;
        heldPrefabVisualObject.transform.localScale = SanitizeScale(prefab.transform.localScale);
        heldPrefabSource = prefab;
        return heldPrefabInstance;
    }

    private void ApplyHeldPrefabScale(Transform prefabTransform, Item selectedItem)
    {
        if (prefabTransform == null || selectedItem == null || selectedItem.heldPrefab == null || heldPrefabVisualObject == null)
            return;

        Vector3 sourceScale = SanitizeScale(selectedItem.heldPrefab.transform.localScale);
        if (!selectedItem.overrideHeldTransform)
        {
            prefabTransform.localScale = Vector3.one;
            heldPrefabVisualObject.transform.localScale = sourceScale;
            return;
        }

        Vector3 scaleMultiplier = SanitizeScale(selectedItem.heldLocalScale);
        prefabTransform.localScale = Vector3.one;
        heldPrefabVisualObject.transform.localScale = new Vector3(
            sourceScale.x * scaleMultiplier.x,
            sourceScale.y * scaleMultiplier.y,
            sourceScale.z * scaleMultiplier.z);
    }

    private void ClearHeldPrefabInstance()
    {
        if (heldPrefabVisualObject != null)
            Destroy(heldPrefabVisualObject);

        heldPrefabVisualObject = null;
        heldPrefabSource = null;
    }

    private void HideAllVisualChildren()
    {
        if (blockVisualObject != null && blockVisualObject.activeSelf)
            blockVisualObject.SetActive(false);

        if (flatItemVisualObject != null && flatItemVisualObject.activeSelf)
            flatItemVisualObject.SetActive(false);

        if (heldPrefabInstance != null && heldPrefabInstance.activeSelf)
            heldPrefabInstance.SetActive(false);
    }

    private Mesh GetOrCreateBlockMesh(BlockType blockType, out int materialIndex)
    {
        materialIndex = 0;
        if (blockMeshCache.TryGetValue(blockType, out Mesh cachedMesh) && cachedMesh != null)
        {
            if (blockMaterialIndexCache.TryGetValue(blockType, out int cachedMaterialIndex))
                materialIndex = cachedMaterialIndex;

            return cachedMesh;
        }

        Mesh mesh = BlockDrop.BuildBlockMesh(world, blockType, out materialIndex);
        if (mesh == null)
            return null;

        CollapseToSingleSubmesh(mesh, materialIndex);
        mesh.name = $"HeldMesh_{blockType}";
        blockMeshCache[blockType] = mesh;
        blockMaterialIndexCache[blockType] = materialIndex;
        return mesh;
    }

    private Material ResolveHeldBlockMaterial(int preferredMaterialIndex)
    {
        if (world == null || world.Material == null || world.Material.Length == 0)
            return null;

        int clampedIndex = Mathf.Clamp(preferredMaterialIndex, 0, world.Material.Length - 1);
        if (world.Material[clampedIndex] != null)
            return world.Material[clampedIndex];

        for (int i = 0; i < world.Material.Length; i++)
        {
            if (world.Material[i] != null)
                return world.Material[i];
        }

        return null;
    }

    private static void CollapseToSingleSubmesh(Mesh mesh, int submeshIndex)
    {
        if (mesh == null || mesh.subMeshCount <= 1)
            return;

        int clampedIndex = Mathf.Clamp(submeshIndex, 0, mesh.subMeshCount - 1);
        int[] triangles = mesh.GetTriangles(clampedIndex);
        mesh.subMeshCount = 1;
        mesh.SetTriangles(triangles, 0, false);
    }

    private bool HasAtlasFlatItem(Item item)
    {
        if (item == null || !TryGetActiveItemAtlasData(out ItemAtlasDataSO atlasData))
            return false;

        return atlasData.HasMapping(item);
    }

    private bool TryGetActiveItemAtlasData(out ItemAtlasDataSO atlasData)
    {
        atlasData = itemAtlasData;
        if (atlasData == null && PlayerInventory.Instance != null)
            PlayerInventory.Instance.TryGetItemAtlasData(out atlasData);

        return atlasData != null;
    }

    private Mesh GetOrCreateFlatItemMesh(Item item, out Material renderMaterial)
    {
        renderMaterial = null;
        if (item == null)
            return null;

        if (TryGetActiveItemAtlasData(out ItemAtlasDataSO atlasData) &&
            atlasData.TryGetUvRect(item, out Rect atlasUvRect) &&
            atlasData.TryGetAspect(item, out float atlasAspect))
        {
            Mesh atlasMesh = GetOrCreateAtlasFlatItemMesh(item, atlasUvRect, atlasAspect);
            if (atlasMesh == null)
                return null;

            if (atlasData.TryGetTexture(out Texture2D atlasTexture) && atlasTexture != null)
                ConfigurePixelArtTexture(atlasTexture);

            if (atlasData.TryGetMaterial(out Material atlasMaterial) && atlasMaterial != null)
            {
                renderMaterial = atlasMaterial;
                return atlasMesh;
            }

            if (atlasData.TryGetTexture(out Texture2D atlasTextureFallback) && atlasTextureFallback != null)
            {
                EnsureFlatItemMaterial();
                if (flatItemMaterial == null)
                    return null;

                ApplyFlatItemTexture(atlasTextureFallback);
                renderMaterial = flatItemMaterial;
                return atlasMesh;
            }
        }

        if (item.icon == null)
            return null;

        Mesh spriteMesh = GetOrCreateSpriteFlatItemMesh(item.icon);
        if (spriteMesh == null)
            return null;

        EnsureFlatItemMaterial();
        if (flatItemMaterial == null)
            return null;

        ApplyFlatItemTexture(item.icon.texture);
        renderMaterial = flatItemMaterial;
        return spriteMesh;
    }

    private Mesh GetOrCreateSpriteFlatItemMesh(Sprite sprite)
    {
        if (sprite == null)
            return null;

        if (spriteFlatItemMeshCache.TryGetValue(sprite, out Mesh cachedMesh) && cachedMesh != null)
            return cachedMesh;

        Mesh mesh = BuildFlatItemMesh(sprite);
        if (mesh == null)
            return null;

        mesh.name = $"HeldFlatItem_{sprite.name}";
        spriteFlatItemMeshCache[sprite] = mesh;
        return mesh;
    }

    private Mesh GetOrCreateAtlasFlatItemMesh(Item item, Rect uvRect, float aspect)
    {
        if (item == null)
            return null;

        if (atlasFlatItemMeshCache.TryGetValue(item, out Mesh cachedMesh) && cachedMesh != null)
            return cachedMesh;

        Mesh mesh = BuildFlatItemMesh(aspect, uvRect);
        if (mesh == null)
            return null;

        mesh.name = $"HeldFlatItemAtlas_{item.name}";
        atlasFlatItemMeshCache[item] = mesh;
        return mesh;
    }

    private static Mesh BuildFlatItemMesh(Sprite sprite)
    {
        if (sprite == null)
            return null;

        float safeHeight = Mathf.Max(1f, sprite.rect.height);
        float aspect = Mathf.Max(0.01f, sprite.rect.width / safeHeight);
        Vector4 outerUv = DataUtility.GetOuterUV(sprite);
        Rect uvRect = Rect.MinMaxRect(outerUv.x, outerUv.y, outerUv.z, outerUv.w);
        return BuildFlatItemMesh(aspect, uvRect);
    }

    private static Mesh BuildFlatItemMesh(float aspect, Rect uvRect)
    {
        float halfWidth = Mathf.Max(0.01f, aspect) * 0.5f;
        float halfHeight = 0.5f;

        Vector2[] uv =
        {
            new Vector2(uvRect.xMin, uvRect.yMin),
            new Vector2(uvRect.xMin, uvRect.yMax),
            new Vector2(uvRect.xMax, uvRect.yMax),
            new Vector2(uvRect.xMax, uvRect.yMin)
        };

        Mesh mesh = new Mesh();
        mesh.vertices = new[]
        {
            new Vector3(-halfWidth, -halfHeight, 0f),
            new Vector3(-halfWidth, halfHeight, 0f),
            new Vector3(halfWidth, halfHeight, 0f),
            new Vector3(halfWidth, -halfHeight, 0f)
        };
        mesh.uv = uv;
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.normals = new[]
        {
            Vector3.forward,
            Vector3.forward,
            Vector3.forward,
            Vector3.forward
        };
        mesh.RecalculateBounds();
        return mesh;
    }

    private void EnsureFlatItemMaterial()
    {
        if (flatItemMaterial != null)
            return;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");
        if (shader == null)
            shader = Shader.Find("Unlit/Transparent");

        if (shader == null)
            return;

        flatItemMaterial = new Material(shader)
        {
            name = "HeldFlatItemMaterial"
        };

        if (flatItemMaterial.HasProperty("_Surface"))
            flatItemMaterial.SetFloat("_Surface", 1f);
        if (flatItemMaterial.HasProperty("_Blend"))
            flatItemMaterial.SetFloat("_Blend", 0f);
        if (flatItemMaterial.HasProperty("_Cull"))
            flatItemMaterial.SetFloat("_Cull", (float)CullMode.Off);
        if (flatItemMaterial.HasProperty("_AlphaClip"))
            flatItemMaterial.SetFloat("_AlphaClip", 0f);
    }

    private void ApplyFlatItemTexture(Texture texture)
    {
        if (flatItemMaterial == null)
            return;

        ConfigurePixelArtTexture(texture);

        if (flatItemMaterial.HasProperty("_BaseMap"))
            flatItemMaterial.SetTexture("_BaseMap", texture);

        if (flatItemMaterial.HasProperty("_MainTex"))
            flatItemMaterial.SetTexture("_MainTex", texture);

        flatItemMaterial.mainTexture = texture;
        if (flatItemMaterial.HasProperty("_BaseColor"))
            flatItemMaterial.SetColor("_BaseColor", Color.white);
        if (flatItemMaterial.HasProperty("_Color"))
            flatItemMaterial.SetColor("_Color", Color.white);
    }

    private static void ConfigurePixelArtTexture(Texture texture)
    {
        if (texture == null)
            return;

        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.anisoLevel = 0;
    }

    private static void DestroyCachedMeshes<T>(Dictionary<T, Mesh> cache)
    {
        foreach (KeyValuePair<T, Mesh> pair in cache)
        {
            if (pair.Value != null)
                Destroy(pair.Value);
        }

        cache.Clear();
    }

    private void SetVisible(bool visible)
    {
        if (visualRoot != null && visualRoot.activeSelf != visible)
            visualRoot.SetActive(visible);

        isVisible = visible;
    }

    private void ClearShownState()
    {
        shownItem = null;
        shownBlockType = BlockType.Air;
        shownVisualKind = HeldVisualKind.None;
        HideAllVisualChildren();
    }

    private static Vector3 SanitizeScale(Vector3 scale)
    {
        return new Vector3(
            SanitizeScaleAxis(scale.x),
            SanitizeScaleAxis(scale.y),
            SanitizeScaleAxis(scale.z));
    }

    private static float SanitizeScaleAxis(float value)
    {
        return Mathf.Approximately(value, 0f) ? 0.01f : value;
    }

    private static float GetSafeInverseScale(float value)
    {
        return Mathf.Approximately(value, 0f) ? 1f : 1f / value;
    }

    private static Transform FindDirectChildByName(Transform parent, string childName)
    {
        if (parent == null)
            return null;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child != null && child.name == childName)
                return child;
        }

        return null;
    }

    private static void DestroyDuplicateChildren(Transform parent, string childName, Transform keep)
    {
        if (parent == null)
            return;

        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform child = parent.GetChild(i);
            if (child == null || child == keep || child.name != childName)
                continue;

            Destroy(child.gameObject);
        }
    }
}
