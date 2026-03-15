using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Sprites;

[DisallowMultipleComponent]
public class HeldBlockVisual : MonoBehaviour
{
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

    [Header("Default View")]
    [SerializeField] private Vector3 localPosition = new Vector3(0.35f, -0.3f, 0.55f);
    [SerializeField] private Vector3 localEulerAngles = new Vector3(20f, -25f, -8f);
    [SerializeField] private float heldScale = 0.28f;
    [SerializeField] private float flatItemScale = 0.85f;
    [SerializeField] private float prefabScale = 1f;
    [SerializeField] private bool hideWhenInventoryOpen = true;
    [SerializeField] private bool castShadows = false;
    [SerializeField] private bool receiveShadows = false;

    private readonly Dictionary<BlockType, Mesh> blockMeshCache = new Dictionary<BlockType, Mesh>();
    private readonly Dictionary<Sprite, Mesh> flatItemMeshCache = new Dictionary<Sprite, Mesh>();

    private GameObject visualRoot;
    private GameObject blockVisualObject;
    private MeshFilter blockMeshFilter;
    private MeshRenderer blockMeshRenderer;
    private GameObject flatItemVisualObject;
    private MeshFilter flatItemMeshFilter;
    private MeshRenderer flatItemMeshRenderer;
    private Material flatItemMaterial;
    private GameObject heldPrefabInstance;
    private GameObject heldPrefabSource;
    private Item shownItem;
    private BlockType shownBlockType = BlockType.Air;
    private HeldVisualKind shownVisualKind;
    private bool isVisible;

    private void Awake()
    {
        ResolveReferences();
        EnsureVisualObject();
        ApplyViewTransform(null, HeldVisualKind.None);
        RefreshHeldBlock(forceRefresh: true);
    }

    private void LateUpdate()
    {
        RefreshHeldBlock(forceRefresh: false);
    }

    [ContextMenu("Refresh Held Visual")]
    public void RefreshNow()
    {
        ResolveReferences();
        EnsureVisualObject();
        ApplyViewTransform(null, HeldVisualKind.None);
        RefreshHeldBlock(forceRefresh: true);
    }

    private void OnValidate()
    {
        if (!Application.isPlaying)
            return;

        ApplyViewTransform(shownItem, shownVisualKind);
        ApplyRendererSettings();
        RefreshHeldBlock(forceRefresh: true);
    }

    private void OnDestroy()
    {
        DestroyCachedMeshes(blockMeshCache);
        DestroyCachedMeshes(flatItemMeshCache);

        if (flatItemMaterial != null)
            Destroy(flatItemMaterial);

        ClearHeldPrefabInstance();
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
        Transform parent = handAnchor != null ? handAnchor : transform;

        if (visualRoot == null)
        {
            visualRoot = new GameObject("HeldItemVisual");
            visualRoot.transform.SetParent(parent, false);
            visualRoot.SetActive(false);
        }
        else if (visualRoot.transform.parent != parent)
        {
            visualRoot.transform.SetParent(parent, false);
        }

        if (blockVisualObject == null)
        {
            blockVisualObject = new GameObject("BlockVisual");
            blockVisualObject.transform.SetParent(visualRoot.transform, false);
            blockMeshFilter = blockVisualObject.AddComponent<MeshFilter>();
            blockMeshRenderer = blockVisualObject.AddComponent<MeshRenderer>();
            blockVisualObject.SetActive(false);
        }

        if (flatItemVisualObject == null)
        {
            flatItemVisualObject = new GameObject("FlatItemVisual");
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

        EnsureFlatItemMaterial();
        ApplyRendererSettings();
    }

    private void ApplyViewTransform(Item selectedItem, HeldVisualKind visualKind)
    {
        if (visualRoot == null)
            return;

        Vector3 targetPosition = localPosition;
        Vector3 targetEulerAngles = localEulerAngles;
        Vector3 targetScale = Vector3.one * GetDefaultScaleFor(visualKind);

        if (selectedItem != null && selectedItem.overrideHeldTransform)
        {
            targetPosition = selectedItem.heldLocalPosition;
            targetEulerAngles = selectedItem.heldLocalEulerAngles;
            targetScale = SanitizeScale(selectedItem.heldLocalScale);
        }

        visualRoot.transform.localPosition = targetPosition;
        visualRoot.transform.localRotation = Quaternion.Euler(targetEulerAngles);
        visualRoot.transform.localScale = targetScale;
    }

    private float GetDefaultScaleFor(HeldVisualKind visualKind)
    {
        switch (visualKind)
        {
            case HeldVisualKind.FlatItem:
                return Mathf.Max(0.01f, flatItemScale);
            case HeldVisualKind.Prefab:
                return Mathf.Max(0.01f, prefabScale);
            case HeldVisualKind.Block:
            case HeldVisualKind.None:
            default:
                return Mathf.Max(0.01f, heldScale);
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

        BlockType selectedBlockType = BlockType.Air;
        bool hasBlockSelection = hotbar.TryGetSelectedBlockType(out selectedBlockType) && selectedBlockType != BlockType.Air;
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
                shownSuccessfully = ShowFlatItem(selectedItem.icon);
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

        if (selectedItem.icon != null)
            return HeldVisualKind.FlatItem;

        return HeldVisualKind.None;
    }

    private bool ShowBlock(BlockType blockType)
    {
        if (blockType == BlockType.Air)
            return false;

        if (world == null)
            world = World.Instance;

        if (world == null || world.Material == null || world.Material.Length == 0)
            return false;

        Mesh heldMesh = GetOrCreateBlockMesh(blockType);
        if (heldMesh == null)
            return false;

        HideAllVisualChildren();
        blockMeshFilter.sharedMesh = heldMesh;
        blockMeshRenderer.materials = world.Material;
        blockVisualObject.SetActive(true);
        return true;
    }

    private bool ShowFlatItem(Sprite iconSprite)
    {
        if (iconSprite == null || flatItemMeshFilter == null || flatItemMeshRenderer == null)
            return false;

        EnsureFlatItemMaterial();
        if (flatItemMaterial == null)
            return false;

        Mesh flatMesh = GetOrCreateFlatItemMesh(iconSprite);
        if (flatMesh == null)
            return false;

        HideAllVisualChildren();
        ApplyFlatItemTexture(iconSprite.texture);
        flatItemMeshFilter.sharedMesh = flatMesh;
        flatItemMeshRenderer.sharedMaterial = flatItemMaterial;
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
        instance.SetActive(true);
        return true;
    }

    private GameObject EnsureHeldPrefabInstance(GameObject prefab)
    {
        if (prefab == null || visualRoot == null)
            return null;

        if (heldPrefabInstance != null && heldPrefabSource == prefab)
            return heldPrefabInstance;

        ClearHeldPrefabInstance();

        heldPrefabInstance = Instantiate(prefab, visualRoot.transform, false);
        heldPrefabInstance.name = prefab.name + "_Held";
        heldPrefabInstance.transform.localPosition = Vector3.zero;
        heldPrefabInstance.transform.localRotation = Quaternion.identity;
        heldPrefabInstance.transform.localScale = Vector3.one;
        heldPrefabSource = prefab;
        return heldPrefabInstance;
    }

    private void ClearHeldPrefabInstance()
    {
        if (heldPrefabInstance != null)
            Destroy(heldPrefabInstance);

        heldPrefabInstance = null;
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

    private Mesh GetOrCreateBlockMesh(BlockType blockType)
    {
        if (blockMeshCache.TryGetValue(blockType, out Mesh cachedMesh) && cachedMesh != null)
            return cachedMesh;

        Mesh mesh = BlockDrop.BuildBlockMesh(world, blockType, out _);
        if (mesh == null)
            return null;

        mesh.name = $"HeldMesh_{blockType}";
        blockMeshCache[blockType] = mesh;
        return mesh;
    }

    private Mesh GetOrCreateFlatItemMesh(Sprite sprite)
    {
        if (sprite == null)
            return null;

        if (flatItemMeshCache.TryGetValue(sprite, out Mesh cachedMesh) && cachedMesh != null)
            return cachedMesh;

        Mesh mesh = BuildFlatItemMesh(sprite);
        if (mesh == null)
            return null;

        mesh.name = $"HeldFlatItem_{sprite.name}";
        flatItemMeshCache[sprite] = mesh;
        return mesh;
    }

    private static Mesh BuildFlatItemMesh(Sprite sprite)
    {
        if (sprite == null)
            return null;

        float safeHeight = Mathf.Max(1f, sprite.rect.height);
        float aspect = Mathf.Max(0.01f, sprite.rect.width / safeHeight);
        float halfWidth = aspect * 0.5f;
        float halfHeight = 0.5f;

        Vector4 outerUv = DataUtility.GetOuterUV(sprite);
        Vector2[] uv =
        {
            new Vector2(outerUv.x, outerUv.y),
            new Vector2(outerUv.x, outerUv.w),
            new Vector2(outerUv.z, outerUv.w),
            new Vector2(outerUv.z, outerUv.y)
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
}
