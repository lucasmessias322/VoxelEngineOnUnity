using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
[RequireComponent(typeof(BoxCollider))]
[RequireComponent(typeof(SphereCollider))]
[RequireComponent(typeof(Rigidbody))]
public class BlockDrop : MonoBehaviour
{
    [Header("Drop")]
    [SerializeField] private float lifeTimeSeconds = 30f;
    [SerializeField] private float rotateSpeed = 110f;
    [SerializeField] private float dropScale = 0.35f;
    [SerializeField] private float launchForce = 2.2f;
    [SerializeField] private float pickupDelaySeconds = 0.25f;
    [SerializeField] private string playerTag = "Player";
    [SerializeField] private bool requirePlayerTag = false;
    [SerializeField] private bool debugPickupLogs = false;

    [Header("Stacking")]
    [SerializeField, Min(1)] private int stackAmount = 1;
    [SerializeField, Min(1)] private int maxStackAmount = 64;
    [SerializeField, Min(0f)] private float mergeRadius = 1.15f;
    [SerializeField, Min(0.02f)] private float mergeCheckInterval = 0.2f;
    [SerializeField, Min(0f)] private float mergeDelaySeconds = 0.15f;

    [Header("Runtime")]
    [SerializeField] private BlockType blockType;

    private float spawnTime;
    private float nextMergeCheckTime;
    private Rigidbody rb;
    private bool isCollected;
    private const int MergeBufferSize = 24;
    private static readonly Collider[] MergeBuffer = new Collider[MergeBufferSize];

    private struct FaceDef
    {
        public Vector3 normal3;
        public Vector3 v0;
        public Vector3 v1;
        public Vector3 v2;
        public Vector3 v3;
    }

    private static readonly FaceDef[] FaceDefs = new FaceDef[]
    {
        new FaceDef { normal3 = Vector3.right, v0 = new Vector3(1,0,0), v1 = new Vector3(1,1,0), v2 = new Vector3(1,1,1), v3 = new Vector3(1,0,1) },
        new FaceDef { normal3 = Vector3.left, v0 = new Vector3(0,0,1), v1 = new Vector3(0,1,1), v2 = new Vector3(0,1,0), v3 = new Vector3(0,0,0) },
        new FaceDef { normal3 = Vector3.up, v0 = new Vector3(0,1,1), v1 = new Vector3(1,1,1), v2 = new Vector3(1,1,0), v3 = new Vector3(0,1,0) },
        new FaceDef { normal3 = Vector3.down, v0 = new Vector3(0,0,0), v1 = new Vector3(1,0,0), v2 = new Vector3(1,0,1), v3 = new Vector3(0,0,1) },
        new FaceDef { normal3 = Vector3.forward, v0 = new Vector3(1,0,1), v1 = new Vector3(1,1,1), v2 = new Vector3(0,1,1), v3 = new Vector3(0,0,1) },
        new FaceDef { normal3 = Vector3.back, v0 = new Vector3(0,0,0), v1 = new Vector3(0,1,0), v2 = new Vector3(1,1,0), v3 = new Vector3(1,0,0) }
    };

    private static Material fallbackDropMaterial;
    private const int MaxPoolSize = 512;

    private struct CachedDropMesh
    {
        public Mesh mesh;
        public int materialIndex;
    }

    private static readonly Stack<BlockDrop> Pool = new Stack<BlockDrop>(128);
    private static readonly Dictionary<BlockType, CachedDropMesh> CachedMeshes = new Dictionary<BlockType, CachedDropMesh>();
    private static Transform poolContainer;

    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private BoxCollider solidCollider;
    private SphereCollider pickupCollider;
    private bool isPooled;

    public static bool Spawn(World world, Vector3Int blockPos, BlockType blockType, Vector3 throwDirection)
    {
        Vector3 spawnPosition = blockPos + Vector3.one * 0.5f + Vector3.up * 0.08f;
        return Spawn(world, spawnPosition, blockType, 1, throwDirection);
    }

    public static bool Spawn(World world, Vector3 worldPosition, BlockType blockType, int amount, Vector3 throwDirection)
    {
        if (world == null)
        {
            Debug.LogWarning("[BlockDrop] Spawn cancelado: World.Instance nulo.");
            return false;
        }

        if (blockType == BlockType.Air || blockType == BlockType.Bedrock)
            return false;

        blockType = TorchPlacementUtility.GetInventoryDropBlockType(blockType);

        if (world.blockData != null && (world.blockData.mappings == null || world.blockData.mappings.Length == 0))
            world.blockData.InitializeDictionary();

        BlockDrop drop = AcquireDrop();
        if (drop == null)
            return false;

        drop.transform.SetParent(null, false);
        drop.transform.position = worldPosition;
        drop.blockType = blockType;
        drop.maxStackAmount = Mathf.Max(1, drop.maxStackAmount);
        drop.stackAmount = Mathf.Clamp(amount, 1, drop.maxStackAmount);
        drop.isCollected = false;
        drop.isPooled = false;
        drop.transform.localScale = Vector3.one * drop.dropScale;
        drop.spawnTime = Time.time;
        drop.nextMergeCheckTime = Time.time + Random.Range(0f, drop.mergeCheckInterval);
        if (!drop.BuildVisual(world, blockType))
        {
            drop.ReturnToPool();
            return false;
        }

        drop.gameObject.SetActive(true);
        drop.SetupPhysics(throwDirection);
        drop.UpdateDropName();
        return true;
    }

    private bool BuildVisual(World world, BlockType blockType)
    {
        EnsureRuntimeComponents();

        Mesh mesh = GetOrCreateSharedMesh(world, blockType, out int materialIndex);
        if (mesh == null)
            return false;

        meshFilter.sharedMesh = mesh;
        if (solidCollider != null)
        {
            Bounds bounds = mesh.bounds;
            solidCollider.center = bounds.center;
            solidCollider.size = Vector3.Max(bounds.size, Vector3.one * 0.05f);
        }

        Material mat = ResolveMaterial(world, materialIndex);
        if (mat == null)
            return false;

        meshRenderer.sharedMaterial = mat;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        meshRenderer.receiveShadows = true;
        return true;
    }

    private static Mesh GetOrCreateSharedMesh(World world, BlockType blockType, out int materialIndex)
    {
        materialIndex = 0;
        if (CachedMeshes.TryGetValue(blockType, out CachedDropMesh cached) && cached.mesh != null)
        {
            materialIndex = cached.materialIndex;
            return cached.mesh;
        }

        Mesh mesh = BuildBlockMesh(world, blockType, out materialIndex);
        if (mesh == null)
            return null;

        CollapseToSingleSubmesh(mesh, materialIndex);
        CachedMeshes[blockType] = new CachedDropMesh
        {
            mesh = mesh,
            materialIndex = materialIndex
        };
        return mesh;
    }

    private static void CollapseToSingleSubmesh(Mesh mesh, int submeshIndex)
    {
        if (mesh == null || mesh.subMeshCount <= 1)
            return;

        int clamped = Mathf.Clamp(submeshIndex, 0, mesh.subMeshCount - 1);
        int[] tris = mesh.GetTriangles(clamped);

        mesh.subMeshCount = 1;
        mesh.SetTriangles(tris, 0, false);
    }

    private static Material ResolveMaterial(World world, int preferredMaterialIndex)
    {
        if (world != null && world.Material != null && world.Material.Length > 0)
        {
            int clamped = Mathf.Clamp(preferredMaterialIndex, 0, world.Material.Length - 1);
            if (world.Material[clamped] != null)
                return world.Material[clamped];

            for (int i = 0; i < world.Material.Length; i++)
            {
                if (world.Material[i] != null)
                    return world.Material[i];
            }
        }

        return GetFallbackMaterial();
    }

    private static Material GetFallbackMaterial()
    {
        if (fallbackDropMaterial != null)
            return fallbackDropMaterial;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
            return null;

        fallbackDropMaterial = new Material(shader)
        {
            name = "BlockDrop_FallbackMaterial"
        };
        return fallbackDropMaterial;
    }

    public static Mesh BuildBlockMesh(World world, BlockType blockType, out int submeshIndex)
    {
        submeshIndex = 0;
        if (world == null)
            return null;

        Mesh mesh = new Mesh();
        mesh.name = $"DropMesh_{blockType}";

        List<Vector3> vertices = new List<Vector3>(24);
        List<Vector3> normals = new List<Vector3>(24);
        List<Vector2> uv0 = new List<Vector2>(24);
        List<Vector2> uv1 = new List<Vector2>(24);
        List<Vector4> uv2 = new List<Vector4>(24);
        List<int> tris = new List<int>(36);

        int mapIndex = (int)blockType;
        BlockTextureMapping mapping = default;
        if (world.blockData != null && world.blockData.mappings != null && mapIndex >= 0 && mapIndex < world.blockData.mappings.Length)
            mapping = world.blockData.mappings[mapIndex];

        float invAtlasTilesX = 1f / Mathf.Max(1, world.atlasTilesX);
        float invAtlasTilesY = 1f / Mathf.Max(1, world.atlasTilesY);
        Vector3 origin = -Vector3.one * 0.5f;

        switch (mapping.renderShape)
        {
            case BlockRenderShape.Cross:
                AppendCrossMesh(vertices, normals, uv0, uv1, uv2, tris, mapping, origin, invAtlasTilesX, invAtlasTilesY);
                break;

            case BlockRenderShape.Cuboid:
                AppendCuboidMesh(vertices, normals, uv0, uv1, uv2, tris, mapping, origin, invAtlasTilesX, invAtlasTilesY);
                break;

            default:
                for (int f = 0; f < 6; f++)
                {
                    FaceDef face = FaceDefs[f];
                    int baseIndex = vertices.Count;

                    vertices.Add(face.v0 + origin);
                    vertices.Add(face.v1 + origin);
                    vertices.Add(face.v2 + origin);
                    vertices.Add(face.v3 + origin);

                    normals.Add(face.normal3);
                    normals.Add(face.normal3);
                    normals.Add(face.normal3);
                    normals.Add(face.normal3);

                    uv0.Add(GetFaceBaseUv(f, face.v0));
                    uv0.Add(GetFaceBaseUv(f, face.v1));
                    uv0.Add(GetFaceBaseUv(f, face.v2));
                    uv0.Add(GetFaceBaseUv(f, face.v3));

                    Vector2Int tile = GetTileForFace(mapping, f);
                    Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
                    uv1.Add(atlasUv);
                    uv1.Add(atlasUv);
                    uv1.Add(atlasUv);
                    uv1.Add(atlasUv);

                    float tint = GetTintForFace(mapping, f) ? 1f : 0f;
                    bool useGrassSideOverlay = blockType == BlockType.Grass && face.normal3.y == 0f;
                    Vector4 extra = new Vector4(1f, tint, 1f, useGrassSideOverlay ? 0.25f : 0f);
                    uv2.Add(extra);
                    uv2.Add(extra);
                    uv2.Add(extra);
                    uv2.Add(extra);

                    tris.Add(baseIndex + 0);
                    tris.Add(baseIndex + 1);
                    tris.Add(baseIndex + 2);
                    tris.Add(baseIndex + 0);
                    tris.Add(baseIndex + 2);
                    tris.Add(baseIndex + 3);
                }
                break;
        }

        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uv0);
        mesh.SetUVs(1, uv1);
        mesh.SetUVs(2, uv2);
        mesh.subMeshCount = 3;

        if (FluidBlockUtility.IsWater(blockType)) submeshIndex = 2;
        else if (mapping.isTransparent) submeshIndex = 1;

        List<int> empty = new List<int>(0);
        mesh.SetTriangles(submeshIndex == 0 ? tris : empty, 0, false);
        mesh.SetTriangles(submeshIndex == 1 ? tris : empty, 1, false);
        mesh.SetTriangles(submeshIndex == 2 ? tris : empty, 2, false);
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void AppendCrossMesh(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        BlockShapeUtility.ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

        Vector2Int tile = mapping.GetTileCoord(BlockFace.Front);
        Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
        float tint = mapping.GetTint(BlockFace.Front) ? 1f : 0f;

        Vector3 a0 = origin + new Vector3(min.x, min.y, min.z);
        Vector3 a1 = origin + new Vector3(max.x, min.y, max.z);
        Vector3 a2 = origin + new Vector3(max.x, max.y, max.z);
        Vector3 a3 = origin + new Vector3(min.x, max.y, min.z);
        AppendDoubleSidedQuad(vertices, normals, uv0, uv1, uv2, tris, a0, a1, a2, a3, atlasUv, tint);

        Vector3 b0 = origin + new Vector3(min.x, min.y, max.z);
        Vector3 b1 = origin + new Vector3(max.x, min.y, min.z);
        Vector3 b2 = origin + new Vector3(max.x, max.y, min.z);
        Vector3 b3 = origin + new Vector3(min.x, max.y, max.z);
        AppendDoubleSidedQuad(vertices, normals, uv0, uv1, uv2, tris, b0, b1, b2, b3, atlasUv, tint);
    }

    private static void AppendCuboidMesh(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        BlockShapeUtility.ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, tris,
            origin + new Vector3(max.x, min.y, min.z),
            origin + new Vector3(max.x, max.y, min.z),
            origin + new Vector3(max.x, max.y, max.z),
            origin + new Vector3(max.x, min.y, max.z),
            Vector3.right,
            mapping.GetTileCoord(BlockFace.Right),
            mapping.GetTint(BlockFace.Right),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, tris,
            origin + new Vector3(min.x, min.y, max.z),
            origin + new Vector3(min.x, max.y, max.z),
            origin + new Vector3(min.x, max.y, min.z),
            origin + new Vector3(min.x, min.y, min.z),
            Vector3.left,
            mapping.GetTileCoord(BlockFace.Left),
            mapping.GetTint(BlockFace.Left),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, tris,
            origin + new Vector3(min.x, max.y, max.z),
            origin + new Vector3(max.x, max.y, max.z),
            origin + new Vector3(max.x, max.y, min.z),
            origin + new Vector3(min.x, max.y, min.z),
            Vector3.up,
            mapping.GetTileCoord(BlockFace.Top),
            mapping.GetTint(BlockFace.Top),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, tris,
            origin + new Vector3(min.x, min.y, min.z),
            origin + new Vector3(max.x, min.y, min.z),
            origin + new Vector3(max.x, min.y, max.z),
            origin + new Vector3(min.x, min.y, max.z),
            Vector3.down,
            mapping.GetTileCoord(BlockFace.Bottom),
            mapping.GetTint(BlockFace.Bottom),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, tris,
            origin + new Vector3(max.x, min.y, max.z),
            origin + new Vector3(max.x, max.y, max.z),
            origin + new Vector3(min.x, max.y, max.z),
            origin + new Vector3(min.x, min.y, max.z),
            Vector3.forward,
            mapping.GetTileCoord(BlockFace.Front),
            mapping.GetTint(BlockFace.Front),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, tris,
            origin + new Vector3(min.x, min.y, min.z),
            origin + new Vector3(min.x, max.y, min.z),
            origin + new Vector3(max.x, max.y, min.z),
            origin + new Vector3(max.x, min.y, min.z),
            Vector3.back,
            mapping.GetTileCoord(BlockFace.Back),
            mapping.GetTint(BlockFace.Back),
            invAtlasTilesX,
            invAtlasTilesY);
    }

    private static void AppendShapeFace(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<int> tris,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        Vector3 normal,
        Vector2Int tile,
        bool tint,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        int baseIndex = vertices.Count;

        vertices.Add(p0);
        vertices.Add(p1);
        vertices.Add(p2);
        vertices.Add(p3);

        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);

        uv0.Add(new Vector2(0f, 0f));
        uv0.Add(new Vector2(1f, 0f));
        uv0.Add(new Vector2(1f, 1f));
        uv0.Add(new Vector2(0f, 1f));

        Vector2 atlasUv = new Vector2(tile.x * invAtlasTilesX + 0.001f, tile.y * invAtlasTilesY + 0.001f);
        uv1.Add(atlasUv);
        uv1.Add(atlasUv);
        uv1.Add(atlasUv);
        uv1.Add(atlasUv);

        Vector4 extra = new Vector4(1f, tint ? 1f : 0f, 1f, 0f);
        uv2.Add(extra);
        uv2.Add(extra);
        uv2.Add(extra);
        uv2.Add(extra);

        tris.Add(baseIndex + 0);
        tris.Add(baseIndex + 1);
        tris.Add(baseIndex + 2);
        tris.Add(baseIndex + 0);
        tris.Add(baseIndex + 2);
        tris.Add(baseIndex + 3);
    }

    private static void AppendDoubleSidedQuad(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<int> tris,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        Vector2 atlasUv,
        float tint)
    {
        int baseIndex = vertices.Count;
        Vector3 upNormal = Vector3.up;

        vertices.Add(p0);
        vertices.Add(p1);
        vertices.Add(p2);
        vertices.Add(p3);

        normals.Add(upNormal);
        normals.Add(upNormal);
        normals.Add(upNormal);
        normals.Add(upNormal);

        uv0.Add(new Vector2(0f, 0f));
        uv0.Add(new Vector2(1f, 0f));
        uv0.Add(new Vector2(1f, 1f));
        uv0.Add(new Vector2(0f, 1f));

        uv1.Add(atlasUv);
        uv1.Add(atlasUv);
        uv1.Add(atlasUv);
        uv1.Add(atlasUv);

        Vector4 extra = new Vector4(1f, tint, 1f, 0f);
        uv2.Add(extra);
        uv2.Add(extra);
        uv2.Add(extra);
        uv2.Add(extra);

        tris.Add(baseIndex + 0);
        tris.Add(baseIndex + 1);
        tris.Add(baseIndex + 2);
        tris.Add(baseIndex + 0);
        tris.Add(baseIndex + 2);
        tris.Add(baseIndex + 3);

        tris.Add(baseIndex + 0);
        tris.Add(baseIndex + 2);
        tris.Add(baseIndex + 1);
        tris.Add(baseIndex + 0);
        tris.Add(baseIndex + 3);
        tris.Add(baseIndex + 2);
    }

    private static Vector2Int GetTileForFace(BlockTextureMapping mapping, int faceIndex)
    {
        return mapping.GetTileCoord(BlockFaceUtility.FromCubeFaceIndex(faceIndex));
    }

    private static Vector2 GetFaceBaseUv(int faceIndex, Vector3 vertex01)
    {
        // Match the same axis convention used by MeshGenerator:
        // X faces -> (z, y), Y faces -> (x, z), Z faces -> (x, y).
        if (faceIndex == 0 || faceIndex == 1)
            return new Vector2(vertex01.z, vertex01.y);
        if (faceIndex == 2 || faceIndex == 3)
            return new Vector2(vertex01.x, vertex01.z);
        return new Vector2(vertex01.x, vertex01.y);
    }

    private static bool GetTintForFace(BlockTextureMapping mapping, int faceIndex)
    {
        return mapping.GetTint(BlockFaceUtility.FromCubeFaceIndex(faceIndex));
    }

    private static BlockDrop AcquireDrop()
    {
        while (Pool.Count > 0)
        {
            BlockDrop pooledDrop = Pool.Pop();
            if (pooledDrop == null)
                continue;

            pooledDrop.isPooled = false;
            return pooledDrop;
        }

        GameObject go = new GameObject("Drop_Pooled");
        go.transform.SetParent(GetPoolContainer(), false);
        BlockDrop drop = go.AddComponent<BlockDrop>();
        drop.EnsureRuntimeComponents();
        go.SetActive(false);
        return drop;
    }

    private static Transform GetPoolContainer()
    {
        if (poolContainer != null)
            return poolContainer;

        GameObject existing = GameObject.Find("BlockDropPool");
        if (existing == null)
            existing = new GameObject("BlockDropPool");

        poolContainer = existing.transform;
        return poolContainer;
    }

    private void ReturnToPool()
    {
        if (this == null || isPooled)
            return;

        isCollected = true;
        DisableSelfColliders();
        transform.SetParent(GetPoolContainer(), false);
        gameObject.SetActive(false);

        if (Pool.Count < MaxPoolSize)
        {
            isPooled = true;
            Pool.Push(this);
            return;
        }

        Destroy(gameObject);
    }

    private void EnsureRuntimeComponents()
    {
        meshFilter = GetComponent<MeshFilter>();
        if (meshFilter == null)
            meshFilter = gameObject.AddComponent<MeshFilter>();

        meshRenderer = GetComponent<MeshRenderer>();
        if (meshRenderer == null)
            meshRenderer = gameObject.AddComponent<MeshRenderer>();

        solidCollider = GetComponent<BoxCollider>();
        if (solidCollider == null)
            solidCollider = gameObject.AddComponent<BoxCollider>();

        pickupCollider = GetComponent<SphereCollider>();
        if (pickupCollider == null)
            pickupCollider = gameObject.AddComponent<SphereCollider>();
        if (pickupCollider != null)
        {
            pickupCollider.radius = 4f;
            pickupCollider.isTrigger = true;
        }

        rb = GetComponent<Rigidbody>();
        if (rb == null)
            rb = gameObject.AddComponent<Rigidbody>();
        if (rb != null)
        {
            rb.mass = 0.25f;
            rb.linearDamping = 1.4f;
            rb.angularDamping = 0.7f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }
    }

    private void SetupPhysics(Vector3 throwDirection)
    {
        EnsureRuntimeComponents();

        if (solidCollider != null)
            solidCollider.enabled = true;
        if (pickupCollider != null)
            pickupCollider.enabled = true;

        if (rb == null)
            return;

        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = false;
        rb.detectCollisions = true;

        Vector3 randomDir = new Vector3(Random.Range(-0.35f, 0.35f), 0f, Random.Range(-0.35f, 0.35f));
        Vector3 launchDir = (throwDirection.normalized + randomDir + Vector3.up * 0.4f).normalized;
        rb.AddForce(launchDir * launchForce, ForceMode.Impulse);
    }

    private void Awake()
    {
        mergeRadius = Mathf.Max(0.01f, mergeRadius);
        mergeCheckInterval = Mathf.Max(0.02f, mergeCheckInterval);
        mergeDelaySeconds = Mathf.Max(0f, mergeDelaySeconds);
        maxStackAmount = Mathf.Max(1, maxStackAmount);
        stackAmount = Mathf.Clamp(stackAmount, 1, maxStackAmount);
        EnsureRuntimeComponents();
    }

    private void Update()
    {
        if (!gameObject.activeSelf)
            return;

        transform.Rotate(Vector3.up, rotateSpeed * Time.deltaTime, Space.World);

        if (!isCollected && Time.time >= nextMergeCheckTime)
        {
            nextMergeCheckTime = Time.time + mergeCheckInterval;
            TryMergeNearbyDrops();
        }

        if (Time.time - spawnTime >= lifeTimeSeconds)
            ReturnToPool();
    }

    private void OnTriggerEnter(Collider other)
    {
        TryCollect(other);
    }

    private void OnTriggerStay(Collider other)
    {
        TryCollect(other);
    }
    private void TryCollect(Collider other)
    {
        if (other == null) return;
        if (isCollected) return;
        if (Time.time - spawnTime < pickupDelaySeconds) return;

        PlayerInventory inventory = ResolveInventory(other);
        if (inventory == null)
        {
            if (debugPickupLogs)
                Debug.LogWarning($"[BlockDrop] Colisao com {other.name} sem PlayerInventory para coletar {blockType}.");
            return;
        }

        if (requirePlayerTag && !string.IsNullOrEmpty(playerTag))
        {
            bool isPlayer = other.CompareTag(playerTag) ||
                            (other.transform.root != null && other.transform.root.CompareTag(playerTag)) ||
                            inventory.CompareTag(playerTag);
            if (!isPlayer) return;
        }

        int collectedAmount = 0;
        while (stackAmount > 0 && inventory.TryAddBlockDrop(blockType, 1))
        {
            stackAmount--;
            collectedAmount++;
        }

        if (collectedAmount > 0)
        {
            if (debugPickupLogs)
                Debug.Log($"[BlockDrop] Coletado: {blockType} x{collectedAmount} -> inventario {inventory.name}");

            if (stackAmount <= 0)
            {
                isCollected = true;
                ReturnToPool();
                return;
            }

            UpdateDropName();
            return;
        }

        if (debugPickupLogs)
        {
            Debug.LogWarning($"[BlockDrop] Falha ao coletar {blockType}. Verifique mapeamento BlockType->Item e espaco no inventario.");
        }
    }

    private void TryMergeNearbyDrops()
    {
        if (stackAmount >= maxStackAmount) return;
        if (Time.time - spawnTime < mergeDelaySeconds) return;

        int hits = Physics.OverlapSphereNonAlloc(
            transform.position,
            mergeRadius,
            MergeBuffer,
            ~0,
            QueryTriggerInteraction.Collide
        );

        for (int i = 0; i < hits && stackAmount < maxStackAmount; i++)
        {
            Collider col = MergeBuffer[i];
            MergeBuffer[i] = null;
            if (col == null) continue;

            BlockDrop other = col.GetComponent<BlockDrop>();
            if (other == null)
                other = col.GetComponentInParent<BlockDrop>();

            if (!CanMergeWith(other)) continue;
            if (GetInstanceID() > other.GetInstanceID()) continue;

            AbsorbFrom(other);
        }
    }

    private bool CanMergeWith(BlockDrop other)
    {
        if (other == null || other == this) return false;
        if (other.isCollected) return false;
        if (other.blockType != blockType) return false;
        if (other.stackAmount <= 0) return false;
        if (Time.time - other.spawnTime < other.mergeDelaySeconds) return false;

        float mergeDistanceSqr = mergeRadius * mergeRadius;
        return (other.transform.position - transform.position).sqrMagnitude <= mergeDistanceSqr;
    }

    private void AbsorbFrom(BlockDrop other)
    {
        int stackLimit = Mathf.Max(1, Mathf.Min(maxStackAmount, other.maxStackAmount));
        int freeSpace = Mathf.Max(0, stackLimit - stackAmount);
        if (freeSpace <= 0) return;

        int moved = Mathf.Min(freeSpace, other.stackAmount);
        if (moved <= 0) return;

        stackAmount += moved;
        other.stackAmount -= moved;
        spawnTime = Mathf.Max(spawnTime, other.spawnTime);

        UpdateDropName();

        if (other.stackAmount <= 0)
        {
            other.isCollected = true;
            other.ReturnToPool();
        }
        else
        {
            other.UpdateDropName();
        }
    }

    private void UpdateDropName()
    {
        gameObject.name = $"Drop_{blockType}_x{stackAmount}";
    }

    private static PlayerInventory ResolveInventory(Collider other)
    {
        if (other == null) return null;

        PlayerInventory inventory = other.GetComponent<PlayerInventory>();
        if (inventory != null) return inventory;

        inventory = other.GetComponentInParent<PlayerInventory>();
        if (inventory != null) return inventory;

        if (other.attachedRigidbody != null)
        {
            inventory = other.attachedRigidbody.GetComponent<PlayerInventory>();
            if (inventory != null) return inventory;

            inventory = other.attachedRigidbody.GetComponentInParent<PlayerInventory>();
            if (inventory != null) return inventory;
        }

        return null;
    }

    private void DisableSelfColliders()
    {
        if (solidCollider != null)
            solidCollider.enabled = false;
        if (pickupCollider != null)
            pickupCollider.enabled = false;

        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }
    }
}

[RequireComponent(typeof(BoxCollider))]
[RequireComponent(typeof(SphereCollider))]
[RequireComponent(typeof(Rigidbody))]
public class InventoryItemDrop : MonoBehaviour
{
    [Header("Drop")]
    [SerializeField] private float lifeTimeSeconds = 30f;
    [SerializeField] private float rotateSpeed = 110f;
    [SerializeField] private float dropScale = 0.35f;
    [SerializeField] private float launchForce = 2.2f;
    [SerializeField] private float pickupDelaySeconds = 0.25f;
    [SerializeField] private string playerTag = "Player";
    [SerializeField] private bool requirePlayerTag = false;
    [SerializeField] private bool debugPickupLogs = false;

    [Header("Stacking")]
    [SerializeField, Min(1)] private int stackAmount = 1;
    [SerializeField, Min(1)] private int maxStackAmount = 64;
    [SerializeField, Min(0f)] private float mergeRadius = 1.15f;
    [SerializeField, Min(0.02f)] private float mergeCheckInterval = 0.2f;
    [SerializeField, Min(0f)] private float mergeDelaySeconds = 0.15f;

    [Header("Runtime")]
    [SerializeField] private Item item;

    private const int MergeBufferSize = 24;
    private static readonly Collider[] MergeBuffer = new Collider[MergeBufferSize];

    private float spawnTime;
    private float nextMergeCheckTime;
    private float visualSpinAngle;
    private bool isCollected;
    private Rigidbody rb;
    private BoxCollider solidCollider;
    private SphereCollider pickupCollider;
    private Transform visualRoot;
    private SpriteRenderer spriteRenderer;

    public static bool Spawn(Item item, int amount, Vector3 worldPosition, Vector3 throwDirection)
    {
        if (item == null || amount <= 0)
            return false;

        GameObject dropObject = new GameObject("ItemDrop");
        InventoryItemDrop drop = dropObject.AddComponent<InventoryItemDrop>();
        if (drop == null)
        {
            Object.Destroy(dropObject);
            return false;
        }

        drop.Initialize(item, amount, worldPosition, throwDirection);
        return true;
    }

    private void Awake()
    {
        mergeRadius = Mathf.Max(0.01f, mergeRadius);
        mergeCheckInterval = Mathf.Max(0.02f, mergeCheckInterval);
        mergeDelaySeconds = Mathf.Max(0f, mergeDelaySeconds);
        maxStackAmount = Mathf.Max(1, maxStackAmount);
        stackAmount = Mathf.Clamp(stackAmount, 1, maxStackAmount);
        EnsureRuntimeComponents();
    }

    private void Initialize(Item droppedItem, int amount, Vector3 worldPosition, Vector3 throwDirection)
    {
        EnsureRuntimeComponents();

        item = droppedItem;
        maxStackAmount = Mathf.Max(1, droppedItem.maxStack);
        stackAmount = Mathf.Clamp(amount, 1, maxStackAmount);
        isCollected = false;
        visualSpinAngle = Random.Range(0f, 360f);

        transform.position = worldPosition;
        transform.localScale = Vector3.one * dropScale;
        spawnTime = Time.time;
        nextMergeCheckTime = Time.time + Random.Range(0f, mergeCheckInterval);

        UpdateVisual();
        SetupPhysics(throwDirection);
        UpdateDropName();
    }

    private void EnsureRuntimeComponents()
    {
        solidCollider = GetComponent<BoxCollider>();
        if (solidCollider == null)
            solidCollider = gameObject.AddComponent<BoxCollider>();

        pickupCollider = GetComponent<SphereCollider>();
        if (pickupCollider == null)
            pickupCollider = gameObject.AddComponent<SphereCollider>();
        if (pickupCollider != null)
        {
            pickupCollider.radius = 4f;
            pickupCollider.isTrigger = true;
        }

        rb = GetComponent<Rigidbody>();
        if (rb == null)
            rb = gameObject.AddComponent<Rigidbody>();
        if (rb != null)
        {
            rb.mass = 0.25f;
            rb.linearDamping = 1.4f;
            rb.angularDamping = 0.7f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        if (visualRoot == null)
        {
            GameObject visual = new GameObject("Visual");
            visualRoot = visual.transform;
            visualRoot.SetParent(transform, false);
            visualRoot.localPosition = Vector3.up * 0.18f;
            spriteRenderer = visual.AddComponent<SpriteRenderer>();
        }
        else if (spriteRenderer == null)
        {
            spriteRenderer = visualRoot.GetComponent<SpriteRenderer>();
            if (spriteRenderer == null)
                spriteRenderer = visualRoot.gameObject.AddComponent<SpriteRenderer>();
        }
    }

    private void SetupPhysics(Vector3 throwDirection)
    {
        EnsureRuntimeComponents();

        if (solidCollider != null)
        {
            solidCollider.enabled = true;
            solidCollider.center = new Vector3(0f, 0.16f, 0f);
            solidCollider.size = new Vector3(0.28f, 0.28f, 0.28f);
            solidCollider.isTrigger = false;
        }

        if (pickupCollider != null)
            pickupCollider.enabled = true;

        if (rb == null)
            return;

        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = false;
        rb.detectCollisions = true;

        Vector3 launchBase = throwDirection.sqrMagnitude > 0.0001f ? throwDirection.normalized : Vector3.forward;
        Vector3 randomDir = new Vector3(Random.Range(-0.35f, 0.35f), 0f, Random.Range(-0.35f, 0.35f));
        Vector3 launchDir = (launchBase + randomDir + Vector3.up * 0.4f).normalized;
        rb.AddForce(launchDir * launchForce, ForceMode.Impulse);
    }

    private void Update()
    {
        if (isCollected || item == null)
        {
            if (!isCollected)
                Destroy(gameObject);

            return;
        }

        visualSpinAngle = (visualSpinAngle + rotateSpeed * Time.deltaTime) % 360f;
        UpdateVisualTransform();

        if (Time.time >= nextMergeCheckTime)
        {
            nextMergeCheckTime = Time.time + mergeCheckInterval;
            TryMergeNearbyDrops();
        }

        if (Time.time - spawnTime >= lifeTimeSeconds)
            Destroy(gameObject);
    }

    private void OnTriggerEnter(Collider other)
    {
        TryCollect(other);
    }

    private void OnTriggerStay(Collider other)
    {
        TryCollect(other);
    }

    private void TryCollect(Collider other)
    {
        if (other == null || isCollected || item == null)
            return;

        if (Time.time - spawnTime < pickupDelaySeconds)
            return;

        PlayerInventory inventory = ResolveInventory(other);
        if (inventory == null)
        {
            if (debugPickupLogs)
                Debug.LogWarning($"[InventoryItemDrop] Colisao com {other.name} sem PlayerInventory para coletar {item.name}.");
            return;
        }

        if (requirePlayerTag && !string.IsNullOrEmpty(playerTag))
        {
            bool isPlayer = other.CompareTag(playerTag) ||
                            (other.transform.root != null && other.transform.root.CompareTag(playerTag)) ||
                            inventory.CompareTag(playerTag);
            if (!isPlayer)
                return;
        }

        int remaining = inventory.InsertItem(item, stackAmount);
        int collected = stackAmount - remaining;
        if (collected <= 0)
            return;

        stackAmount = remaining;
        if (debugPickupLogs)
            Debug.Log($"[InventoryItemDrop] Coletado: {item.name} x{collected} -> inventario {inventory.name}");

        if (stackAmount <= 0)
        {
            isCollected = true;
            Destroy(gameObject);
            return;
        }

        UpdateDropName();
    }

    private void TryMergeNearbyDrops()
    {
        if (stackAmount >= maxStackAmount)
            return;

        if (Time.time - spawnTime < mergeDelaySeconds)
            return;

        int hits = Physics.OverlapSphereNonAlloc(
            transform.position,
            mergeRadius,
            MergeBuffer,
            ~0,
            QueryTriggerInteraction.Collide);

        for (int i = 0; i < hits && stackAmount < maxStackAmount; i++)
        {
            Collider col = MergeBuffer[i];
            MergeBuffer[i] = null;
            if (col == null)
                continue;

            InventoryItemDrop other = col.GetComponent<InventoryItemDrop>();
            if (other == null)
                other = col.GetComponentInParent<InventoryItemDrop>();

            if (!CanMergeWith(other))
                continue;

            if (GetInstanceID() > other.GetInstanceID())
                continue;

            AbsorbFrom(other);
        }
    }

    private bool CanMergeWith(InventoryItemDrop other)
    {
        if (other == null || other == this)
            return false;

        if (other.isCollected || other.item == null)
            return false;

        if (other.item != item)
            return false;

        if (other.stackAmount <= 0)
            return false;

        if (Time.time - other.spawnTime < other.mergeDelaySeconds)
            return false;

        float mergeDistanceSqr = mergeRadius * mergeRadius;
        return (other.transform.position - transform.position).sqrMagnitude <= mergeDistanceSqr;
    }

    private void AbsorbFrom(InventoryItemDrop other)
    {
        int stackLimit = Mathf.Max(1, Mathf.Min(maxStackAmount, other.maxStackAmount));
        int freeSpace = Mathf.Max(0, stackLimit - stackAmount);
        if (freeSpace <= 0)
            return;

        int moved = Mathf.Min(freeSpace, other.stackAmount);
        if (moved <= 0)
            return;

        stackAmount += moved;
        other.stackAmount -= moved;
        spawnTime = Mathf.Max(spawnTime, other.spawnTime);

        UpdateDropName();

        if (other.stackAmount <= 0)
        {
            other.isCollected = true;
            Destroy(other.gameObject);
        }
        else
        {
            other.UpdateDropName();
        }
    }

    private void UpdateVisual()
    {
        if (spriteRenderer == null)
            return;

        Sprite icon = ItemIconResolver.ResolveForUI(item);
        if (icon == null && item != null)
            icon = item.icon;

        spriteRenderer.sprite = icon;
        spriteRenderer.enabled = icon != null;

        if (visualRoot == null)
            return;

        if (icon != null)
        {
            float height = Mathf.Max(1f, icon.rect.height);
            float aspect = icon.rect.width / height;
            visualRoot.localScale = new Vector3(0.45f * aspect, 0.45f, 0.45f);
        }
        else
        {
            visualRoot.localScale = Vector3.one * 0.45f;
        }
    }

    private void UpdateVisualTransform()
    {
        if (visualRoot == null)
            return;

        float bob = Mathf.Sin((Time.time - spawnTime) * 5f) * 0.05f;
        visualRoot.localPosition = new Vector3(0f, 0.18f + bob, 0f);

        Camera cameraRef = Camera.main;
        if (cameraRef != null)
        {
            visualRoot.rotation = cameraRef.transform.rotation * Quaternion.Euler(0f, 0f, visualSpinAngle);
            return;
        }

        visualRoot.rotation = Quaternion.Euler(0f, visualSpinAngle, 0f);
    }

    private void UpdateDropName()
    {
        string itemName = item != null ? item.name : "Unknown";
        gameObject.name = $"Drop_{itemName}_x{stackAmount}";
    }

    private static PlayerInventory ResolveInventory(Collider other)
    {
        if (other == null)
            return null;

        PlayerInventory inventory = other.GetComponent<PlayerInventory>();
        if (inventory != null)
            return inventory;

        inventory = other.GetComponentInParent<PlayerInventory>();
        if (inventory != null)
            return inventory;

        if (other.attachedRigidbody != null)
        {
            inventory = other.attachedRigidbody.GetComponent<PlayerInventory>();
            if (inventory != null)
                return inventory;

            inventory = other.attachedRigidbody.GetComponentInParent<PlayerInventory>();
            if (inventory != null)
                return inventory;
        }

        return null;
    }
}
