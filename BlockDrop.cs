using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;

internal static class DropCollisionUtility
{
    private const float BoundsInset = 0.001f;

    public static bool IntersectsSolid(
        World world,
        Vector3 center,
        float collisionHalfExtent,
        BlockType ignoredBlockType = BlockType.Air)
    {
        if (world == null)
            return false;

        float half = Mathf.Max(0.05f, collisionHalfExtent);
        float testHalf = Mathf.Max(0.001f, half - BoundsInset);
        Bounds testBounds = new Bounds(center, Vector3.one * (testHalf * 2f));

        int minX = Mathf.FloorToInt(center.x - half + BoundsInset);
        int maxX = Mathf.FloorToInt(center.x + half - BoundsInset);
        int minY = Mathf.FloorToInt(center.y - half + BoundsInset);
        int maxY = Mathf.FloorToInt(center.y + half - BoundsInset);
        int minZ = Mathf.FloorToInt(center.z - half + BoundsInset);
        int maxZ = Mathf.FloorToInt(center.z + half - BoundsInset);

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    Vector3Int blockPos = new Vector3Int(x, y, z);
                    if (!world.TryGetLoadedBlockAt(blockPos, out BlockType blockType))
                        continue;

                    if (blockType == ignoredBlockType ||
                        (ignoredBlockType == BlockType.ConveyorBelt && ConveyorBeltUtility.IsConveyorBlock(blockType)))
                    {
                        continue;
                    }

                    if (!world.IsSolidBlock(blockType))
                        continue;

                    if (IntersectsBlockShape(world, blockPos, blockType, testBounds))
                        return true;
                }
            }
        }

        return false;
    }

    private static bool IntersectsBlockShape(World world, Vector3Int blockPos, BlockType blockType, Bounds testBounds)
    {
        if (world.blockData == null)
            return UnitBlockBounds(blockPos).Intersects(testBounds);

        BlockTextureMapping? mappingResult = world.blockData.GetMapping(blockType);
        if (mappingResult == null)
            return UnitBlockBounds(blockPos).Intersects(testBounds);

        BlockTextureMapping mapping = mappingResult.Value;
        BlockRenderShape shape = BlockShapeUtility.GetEffectiveRenderShape(mapping);
        if (mapping.renderAsDynamicPrefab || shape == BlockRenderShape.Cube)
            return UnitBlockBounds(blockPos).Intersects(testBounds);

        BlockPlacementAxis placementAxis = world.GetPlacementAxisAt(blockPos, blockType);
        if (shape == BlockRenderShape.MultiCuboid)
            return IntersectsMultiCuboidShape(world, blockPos, blockType, mapping, placementAxis, testBounds);

        Bounds shapeBounds = BlockShapeUtility.GetWorldBounds(blockPos, blockType, mapping, placementAxis);
        return shapeBounds.Intersects(testBounds);
    }

    private static bool IntersectsMultiCuboidShape(
        World world,
        Vector3Int blockPos,
        BlockType blockType,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        Bounds testBounds)
    {
        if (TransportTubeUtility.IsTransportTubeBlock(blockType))
        {
            byte connectionMask = TransportTubeUtility.ResolveConnectionMask(world, blockPos);
            FixedList512Bytes<ShapeBox> tubeBoxes = TransportTubeUtility.BuildVisualBoxes(connectionMask, placementAxis);
            for (int i = 0; i < tubeBoxes.Length; i++)
            {
                if (tubeBoxes[i].ToWorldBounds(blockPos).Intersects(testBounds))
                    return true;
            }

            return false;
        }

        if (FluidPipeUtility.IsFluidPipeBlock(blockType))
        {
            byte connectionMask = FluidPipeUtility.ResolveConnectionMask(world, blockPos);
            FixedList512Bytes<ShapeBox> pipeBoxes = FluidPipeUtility.BuildVisualBoxes(connectionMask, placementAxis);
            for (int i = 0; i < pipeBoxes.Length; i++)
            {
                if (pipeBoxes[i].ToWorldBounds(blockPos).Intersects(testBounds))
                    return true;
            }

            return false;
        }

        BlockModelCuboid[] cuboids = world.blockData != null ? world.blockData.runtimeMultiCuboidBoxes : null;
        int boxCount = BlockShapeUtility.GetMultiCuboidBoxCount(mapping, cuboids);
        if (boxCount <= 0)
        {
            Bounds fallbackBounds = BlockShapeUtility.GetWorldBounds(blockPos, blockType, mapping, placementAxis);
            return fallbackBounds.Intersects(testBounds);
        }

        for (int i = 0; i < boxCount; i++)
        {
            if (BlockShapeUtility.TryGetMultiCuboidBox(mapping, cuboids, i, placementAxis, blockType, out ShapeBox box) &&
                box.ToWorldBounds(blockPos).Intersects(testBounds))
            {
                return true;
            }
        }

        return false;
    }

    private static Bounds UnitBlockBounds(Vector3Int blockPos)
    {
        return new Bounds(blockPos + Vector3.one * 0.5f, Vector3.one);
    }
}

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class BlockDrop : MonoBehaviour, IRoboticArmGrabbable, IRoboticArmItemStack
{
    [Header("Drop")]
    [SerializeField] private float lifeTimeSeconds = 30f;
    [SerializeField] private bool preventDespawnOnConveyor = true;
    [SerializeField] private float dropScale = 0.35f;
    [SerializeField] private float launchForce = 2.2f;
    [SerializeField] private float pickupDelaySeconds = 0.5f;
    [SerializeField] private string playerTag = "Player";
    [SerializeField] private bool requirePlayerTag = false;
    [SerializeField] private bool debugPickupLogs = false;

    [Header("Lightweight Physics")]
    [SerializeField, Min(0f)] private float gravity = 24f;
    [SerializeField, Min(0f)] private float airDrag = 2.6f;
    [SerializeField, Min(0f)] private float groundedDrag = 14f;
    [SerializeField, Min(0f)] private float maxFallSpeed = 28f;
    [SerializeField, Range(0f, 1f)] private float bounceDamping = 0.22f;
    [SerializeField, Min(0.01f)] private float simulationStepSeconds = 0.02f;
    [SerializeField, Min(0.05f)] private float collisionHalfExtent = 0.18f;
    [SerializeField, Min(0.1f)] private float pickupRadius = 1.7f;
    [SerializeField, Min(0.02f)] private float pickupCheckInterval = 0.08f;
    [SerializeField, Min(0.25f)] private float mergeGridCellSize = 1.25f;
    [SerializeField, Min(0f)] private float conveyorSpeed = ConveyorBeltUtility.DefaultSpeed;
    [SerializeField, Min(0f)] private float conveyorCenteringStrength = ConveyorBeltUtility.DefaultCenteringStrength;
    [SerializeField, Min(0f)] private float conveyorMaxCenteringSpeed = ConveyorBeltUtility.DefaultMaxCenteringSpeed;

    [Header("Stacking")]
    [SerializeField, Min(1)] private int stackAmount = 1;
    [SerializeField, Min(1)] private int maxStackAmount = 100;
    [SerializeField, Min(0f)] private float mergeRadius = 1.15f;
    [SerializeField, Min(0.02f)] private float mergeCheckInterval = 0.2f;
    [SerializeField, Min(0f)] private float mergeDelaySeconds = 0.15f;

    [Header("Runtime")]
    [SerializeField] private BlockType blockType;

    private float spawnTime;
    private float despawnStartTime;
    private float nextMergeCheckTime;
    private float nextPickupCheckTime;
    private float simulationAccumulator;
    private Vector3 velocity;
    private Vector3Int mergeGridCell;
    private bool isCollected;
    private bool isGrounded;
    private bool isSleeping;
    private bool isRegisteredInMergeGrid;
    private bool isHeldByRoboticArm;
    private bool hasLastStackSplitSplitterPos;
    private Vector3Int lastStackSplitSplitterPos;
    private float mergeSuppressedUntil;
    private int conveyorRoutingKey;

    private struct FaceDef
    {
        public Vector3 normal3;
        public Vector3 v0;
        public Vector3 v1;
        public Vector3 v2;
        public Vector3 v3;
    }

    private struct ShapeFaceRect
    {
        public BlockFace face;
        public float plane;
        public float minA;
        public float maxA;
        public float minB;
        public float maxB;
        public int tileX;
        public int tileY;
        public bool tint;
        public bool usesExplicitAppearance;
        public Vector4 explicitUvRectData;
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
    private const float SplitterSplitMergeSuppressSeconds = 1.25f;
    private const float ShapeFaceEpsilon = 0.0001f;
    private static Vector2Int currentLegacyAtlasTiles = Vector2Int.one;
    private static bool currentAtlasOriginTopLeft;

    private struct CachedDropMesh
    {
        public Mesh mesh;
        public int materialIndex;
    }

    private static readonly Stack<BlockDrop> Pool = new Stack<BlockDrop>(128);
    private static readonly Dictionary<BlockType, CachedDropMesh> CachedMeshes = new Dictionary<BlockType, CachedDropMesh>();
    private static readonly Dictionary<Vector3Int, HashSet<BlockDrop>> ActiveDropsByCell = new Dictionary<Vector3Int, HashSet<BlockDrop>>();
    private static readonly List<BlockDrop> MergeCandidates = new List<BlockDrop>(64);
    private static Transform poolContainer;
    private static int nextConveyorRoutingKey;

    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private bool isPooled;
    private int currentMaterialIndex;

    public static bool Spawn(World world, Vector3Int blockPos, BlockType blockType, Vector3 throwDirection)
    {
        Vector3 spawnPosition = blockPos + Vector3.one * 0.5f + Vector3.up * 0.08f;
        return Spawn(world, spawnPosition, blockType, 1, throwDirection);
    }

    public static bool Spawn(World world, Vector3 worldPosition, BlockType blockType, int amount, Vector3 throwDirection)
    {
        return Spawn(world, worldPosition, blockType, amount, throwDirection, out _);
    }

    private static bool Spawn(
        World world,
        Vector3 worldPosition,
        BlockType blockType,
        int amount,
        Vector3 throwDirection,
        out BlockDrop spawnedDrop)
    {
        if (world == null)
        {
            Debug.LogWarning("[BlockDrop] Spawn cancelado: World.Instance nulo.");
            spawnedDrop = null;
            return false;
        }

        spawnedDrop = null;
        if (blockType == BlockType.Air || blockType == BlockType.Bedrock)
            return false;

        blockType = FluidPipeUtility.GetInventoryDropBlockType(
            TransportTubeUtility.GetInventoryDropBlockType(
                LeverUtility.GetInventoryDropBlockType(
                    TorchPlacementUtility.GetInventoryDropBlockType(blockType))));

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
        drop.isHeldByRoboticArm = false;
        drop.hasLastStackSplitSplitterPos = false;
        drop.mergeSuppressedUntil = 0f;
        drop.conveyorRoutingKey = AllocateConveyorRoutingKey();
        drop.transform.localScale = Vector3.one * drop.dropScale;
        drop.spawnTime = Time.time;
        drop.despawnStartTime = drop.spawnTime;
        drop.nextMergeCheckTime = Time.time + Random.Range(0f, drop.mergeCheckInterval);
        drop.nextPickupCheckTime = Time.time + Random.Range(0f, drop.pickupCheckInterval);
        drop.simulationAccumulator = 0f;
        drop.isGrounded = false;
        drop.isSleeping = false;
        if (!drop.BuildVisual(world, blockType))
        {
            drop.ReturnToPool();
            return false;
        }

        drop.gameObject.SetActive(true);
        drop.SetupPhysics(throwDirection);
        drop.RegisterInMergeGrid();
        drop.UpdateDropName();
        spawnedDrop = drop;
        return true;
    }

    public Transform DropTransform => transform;

    public BlockType DroppedBlockType => blockType;

    public bool CanBeGrabbedByRoboticArm =>
        gameObject.activeSelf &&
        !isPooled &&
        !isCollected &&
        !isHeldByRoboticArm &&
        blockType != BlockType.Air &&
        blockType != BlockType.Bedrock;

    public bool TryGetRoboticArmItemStack(out Item item, out int amount)
    {
        amount = 0;
        if (blockType == BlockType.Air ||
            blockType == BlockType.Bedrock ||
            stackAmount <= 0 ||
            !BlockItemCatalog.TryGetItemForBlock(blockType, out item))
        {
            item = null;
            return false;
        }

        amount = stackAmount;
        return item != null;
    }

    public int RemoveFromRoboticArmStack(int amountToRemove)
    {
        if (amountToRemove <= 0 || stackAmount <= 0)
            return 0;

        int removed = Mathf.Min(amountToRemove, stackAmount);
        stackAmount -= removed;

        if (stackAmount <= 0)
        {
            isCollected = true;
            ReturnToPool();
        }
        else
        {
            UpdateDropName();
        }

        return removed;
    }

    public void AttachToRoboticArm(Transform parent, Vector3 localPosition, Quaternion localRotation, float localScale)
    {
        if (parent == null || !CanBeGrabbedByRoboticArm)
            return;

        isHeldByRoboticArm = true;
        isCollected = false;
        isPooled = false;
        velocity = Vector3.zero;
        simulationAccumulator = 0f;
        isGrounded = false;
        isSleeping = true;
        UnregisterFromMergeGrid();

        transform.SetParent(parent, false);
        transform.localPosition = localPosition;
        transform.localRotation = localRotation;
        transform.localScale = Vector3.one * Mathf.Max(0.01f, localScale);
    }

    public void ReleaseFromRoboticArm(Vector3 worldPosition, Vector3 throwDirection)
    {
        if (isPooled)
            return;

        isHeldByRoboticArm = false;
        isCollected = false;
        hasLastStackSplitSplitterPos = false;
        mergeSuppressedUntil = 0f;
        conveyorRoutingKey = AllocateConveyorRoutingKey();
        transform.SetParent(null, true);
        transform.position = worldPosition;
        transform.rotation = Quaternion.identity;
        transform.localScale = Vector3.one * dropScale;
        spawnTime = Time.time;
        despawnStartTime = spawnTime;
        nextMergeCheckTime = Time.time + Random.Range(0f, mergeCheckInterval);
        nextPickupCheckTime = Time.time + Random.Range(0f, pickupCheckInterval);
        simulationAccumulator = 0f;
        isGrounded = false;
        isSleeping = false;

        SetupPhysics(throwDirection);
        gameObject.SetActive(true);
        RegisterInMergeGrid();
        UpdateDropName();
    }

    private bool BuildVisual(World world, BlockType blockType)
    {
        EnsureRuntimeComponents();

        Mesh mesh = GetOrCreateSharedMesh(world, blockType, out int materialIndex);
        if (mesh == null)
            return false;

        meshFilter.sharedMesh = mesh;

        Material mat = ResolveMaterial(world, materialIndex);
        if (mat == null)
            return false;

        currentMaterialIndex = materialIndex;
        meshRenderer.sharedMaterial = mat;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        meshRenderer.receiveShadows = true;
        return true;
    }

    public void RefreshVisualMaterial()
    {
        if (!gameObject.activeInHierarchy || isPooled)
            return;

        EnsureRuntimeComponents();

        World world = World.Instance;
        Material material = ResolveMaterial(world, currentMaterialIndex);
        if (material == null || meshRenderer == null)
            return;

        if (meshRenderer.sharedMaterial != material)
            meshRenderer.sharedMaterial = material;
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
        List<Vector2> uv3 = new List<Vector2>(24);
        List<int> tris = new List<int>(36);

        int mapIndex = (int)blockType;
        BlockTextureMapping mapping = default;
        if (world.blockData != null && world.blockData.mappings != null && mapIndex >= 0 && mapIndex < world.blockData.mappings.Length)
            mapping = world.blockData.mappings[mapIndex];

        float invAtlasTilesX = 1f / Mathf.Max(1, world.atlasTilesX);
        float invAtlasTilesY = 1f / Mathf.Max(1, world.atlasTilesY);
        currentLegacyAtlasTiles = new Vector2Int(Mathf.Max(1, world.atlasTilesX), Mathf.Max(1, world.atlasTilesY));
        currentAtlasOriginTopLeft = world.blockData != null && world.blockData.atlasCoordinatesStartTopLeft;
        Vector3 origin = -Vector3.one * 0.5f;
        BlockRenderShape effectiveShape = BlockShapeUtility.GetEffectiveRenderShape(mapping);

        switch (effectiveShape)
        {
            case BlockRenderShape.Cross:
                AppendCrossMesh(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, invAtlasTilesX, invAtlasTilesY);
                break;

            case BlockRenderShape.Cuboid:
                AppendCuboidMesh(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, invAtlasTilesX, invAtlasTilesY);
                break;

            case BlockRenderShape.MultiCuboid:
                AppendMultiCuboidMesh(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, world.blockData != null ? world.blockData.runtimeMultiCuboidBoxes : null, origin, invAtlasTilesX, invAtlasTilesY);
                break;

            case BlockRenderShape.Plane:
                AppendPlaneMesh(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, invAtlasTilesX, invAtlasTilesY);
                break;

            case BlockRenderShape.Stairs:
                AppendStairMesh(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, invAtlasTilesX, invAtlasTilesY);
                break;

            case BlockRenderShape.Fence:
                AppendFenceMesh(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, invAtlasTilesX, invAtlasTilesY);
                break;

            case BlockRenderShape.Fence2:
                AppendFence2Mesh(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, invAtlasTilesX, invAtlasTilesY);
                break;

            case BlockRenderShape.Slab:
                AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, SlabShapeUtility.GetVisualBox(BlockPlacementAxis.Y), invAtlasTilesX, invAtlasTilesY);
                break;

            case BlockRenderShape.Ramp:
                AppendRampMesh(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, invAtlasTilesX, invAtlasTilesY);
                break;

            case BlockRenderShape.VerticalRamp:
                AppendVerticalRampMesh(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, invAtlasTilesX, invAtlasTilesY);
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

                    ResolveAtlasRect(mapping, BlockFaceUtility.FromCubeFaceIndex(f), out Vector2 atlasUv, out Vector2 atlasSize);
                    AppendAtlasRect(uv1, uv3, atlasUv, atlasSize, 4);

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
        mesh.SetUVs(3, uv3);
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
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        BlockShapeUtility.ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

        ResolveAtlasRect(mapping, BlockFace.Front, out Vector2 atlasUv, out Vector2 atlasSize);
        float tint = mapping.GetTint(BlockFace.Front) ? 1f : 0f;

        Vector3 a0 = origin + new Vector3(min.x, min.y, min.z);
        Vector3 a1 = origin + new Vector3(max.x, min.y, max.z);
        Vector3 a2 = origin + new Vector3(max.x, max.y, max.z);
        Vector3 a3 = origin + new Vector3(min.x, max.y, min.z);
        AppendDoubleSidedQuad(vertices, normals, uv0, uv1, uv2, uv3, tris, a0, a1, a2, a3, atlasUv, atlasSize, tint);

        Vector3 b0 = origin + new Vector3(min.x, min.y, max.z);
        Vector3 b1 = origin + new Vector3(max.x, min.y, min.z);
        Vector3 b2 = origin + new Vector3(max.x, max.y, min.z);
        Vector3 b3 = origin + new Vector3(min.x, max.y, max.z);
        AppendDoubleSidedQuad(vertices, normals, uv0, uv1, uv2, uv3, tris, b0, b1, b2, b3, atlasUv, atlasSize, tint);
    }

    private static void AppendPlaneMesh(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        BlockShapeUtility.ResolvePlaneQuad(mapping, BlockPlacementAxis.Y, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3, out BlockFace sampledFace, out _);

        ResolveAtlasRect(mapping, sampledFace, out Vector2 atlasUv, out Vector2 atlasSize);
        float tint = mapping.GetTint(sampledFace) ? 1f : 0f;

        AppendDoubleSidedQuad(
            vertices,
            normals,
            uv0,
            uv1,
            uv2,
            uv3,
            tris,
            origin + p0,
            origin + p1,
            origin + p2,
            origin + p3,
            atlasUv,
            atlasSize,
            tint);
    }

    private static void AppendCuboidMesh(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        BlockShapeUtility.ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, uv3, tris,
            origin + new Vector3(max.x, min.y, min.z),
            origin + new Vector3(max.x, max.y, min.z),
            origin + new Vector3(max.x, max.y, max.z),
            origin + new Vector3(max.x, min.y, max.z),
            Vector3.right,
            mapping,
            BlockFace.Right,
            mapping.GetTint(BlockFace.Right),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, uv3, tris,
            origin + new Vector3(min.x, min.y, max.z),
            origin + new Vector3(min.x, max.y, max.z),
            origin + new Vector3(min.x, max.y, min.z),
            origin + new Vector3(min.x, min.y, min.z),
            Vector3.left,
            mapping,
            BlockFace.Left,
            mapping.GetTint(BlockFace.Left),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, uv3, tris,
            origin + new Vector3(min.x, max.y, max.z),
            origin + new Vector3(max.x, max.y, max.z),
            origin + new Vector3(max.x, max.y, min.z),
            origin + new Vector3(min.x, max.y, min.z),
            Vector3.up,
            mapping,
            BlockFace.Top,
            mapping.GetTint(BlockFace.Top),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, uv3, tris,
            origin + new Vector3(min.x, min.y, min.z),
            origin + new Vector3(max.x, min.y, min.z),
            origin + new Vector3(max.x, min.y, max.z),
            origin + new Vector3(min.x, min.y, max.z),
            Vector3.down,
            mapping,
            BlockFace.Bottom,
            mapping.GetTint(BlockFace.Bottom),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, uv3, tris,
            origin + new Vector3(max.x, min.y, max.z),
            origin + new Vector3(max.x, max.y, max.z),
            origin + new Vector3(min.x, max.y, max.z),
            origin + new Vector3(min.x, min.y, max.z),
            Vector3.forward,
            mapping,
            BlockFace.Front,
            mapping.GetTint(BlockFace.Front),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, uv3, tris,
            origin + new Vector3(min.x, min.y, min.z),
            origin + new Vector3(min.x, max.y, min.z),
            origin + new Vector3(max.x, max.y, min.z),
            origin + new Vector3(max.x, min.y, min.z),
            Vector3.back,
            mapping,
            BlockFace.Back,
            mapping.GetTint(BlockFace.Back),
            invAtlasTilesX,
            invAtlasTilesY);
    }

    private static void AppendMultiCuboidMesh(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        BlockModelCuboid[] cuboids,
        Vector3 origin,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        int boxCount = BlockShapeUtility.GetMultiCuboidBoxCount(mapping, cuboids);
        if (boxCount <= 0)
        {
            AppendCuboidMesh(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, invAtlasTilesX, invAtlasTilesY);
            return;
        }

        List<ShapeBox> boxes = new List<ShapeBox>(boxCount);
        List<ShapeFaceRect> faceRects = new List<ShapeFaceRect>(boxCount * 6);
        bool appendedAnyCuboid = false;

        for (int i = 0; i < boxCount; i++)
        {
            if (!BlockShapeUtility.TryGetMultiCuboidModelCuboid(mapping, cuboids, i, out BlockModelCuboid cuboid))
                continue;

            appendedAnyCuboid = true;
            if (BlockShapeUtility.HasCuboidRotation(cuboid))
            {
                AppendRotatedModelCuboid(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, cuboid, invAtlasTilesX, invAtlasTilesY);
                continue;
            }

            ShapeBox box = BlockShapeUtility.TransformShapeBoxForPlacement(cuboid.ToShapeBox(), mapping, BlockPlacementAxis.Y);
            boxes.Add(box);
            AppendModelCuboidFaceRects(faceRects, box, cuboid, mapping);
        }

        if (!appendedAnyCuboid)
        {
            AppendCuboidMesh(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, invAtlasTilesX, invAtlasTilesY);
            return;
        }

        if (boxes.Count == 0)
            return;

        CullHiddenShapeFaceRects(faceRects, boxes);
        MergeShapeFaceRects(faceRects);

        for (int i = 0; i < faceRects.Count; i++)
        {
            AppendShapeRect(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, faceRects[i], invAtlasTilesX, invAtlasTilesY);
        }
    }

    private static void AppendRotatedModelCuboid(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        BlockModelCuboid cuboid,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        ShapeBox box = cuboid.ToShapeBox();
        Vector3 center = (box.min + box.max) * 0.5f;
        Quaternion rotation = Quaternion.Euler(cuboid.eulerRotation);

        AppendRotatedModelCuboidFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, cuboid, box, center, rotation, BlockFace.Right, invAtlasTilesX, invAtlasTilesY);
        AppendRotatedModelCuboidFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, cuboid, box, center, rotation, BlockFace.Left, invAtlasTilesX, invAtlasTilesY);
        AppendRotatedModelCuboidFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, cuboid, box, center, rotation, BlockFace.Top, invAtlasTilesX, invAtlasTilesY);
        AppendRotatedModelCuboidFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, cuboid, box, center, rotation, BlockFace.Bottom, invAtlasTilesX, invAtlasTilesY);
        AppendRotatedModelCuboidFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, cuboid, box, center, rotation, BlockFace.Front, invAtlasTilesX, invAtlasTilesY);
        AppendRotatedModelCuboidFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, cuboid, box, center, rotation, BlockFace.Back, invAtlasTilesX, invAtlasTilesY);
    }

    private static void AppendRotatedModelCuboidFace(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        BlockModelCuboid cuboid,
        ShapeBox box,
        Vector3 center,
        Quaternion rotation,
        BlockFace face,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        if (!cuboid.HasFace(face))
            return;

        ResolveCuboidFace(box, face, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3, out Vector3 normal);
        p0 = RotateCuboidPoint(p0, center, rotation);
        p1 = RotateCuboidPoint(p1, center, rotation);
        p2 = RotateCuboidPoint(p2, center, rotation);
        p3 = RotateCuboidPoint(p3, center, rotation);
        normal = (rotation * normal).normalized;
        Vector4 explicitUvRectData = cuboid.TryGetUvRectData(face, mapping, out Vector4 cuboidUvRectData)
            ? cuboidUvRectData
            : default;

        AppendProjectedQuadFace(
            vertices,
            normals,
            uv0,
            uv1,
            uv2,
            uv3,
            tris,
            mapping,
            face,
            origin,
            p0,
            p1,
            p2,
            p3,
            normal,
            invAtlasTilesX,
            invAtlasTilesY,
            false,
            true,
            cuboid.GetTileCoord(face, mapping),
            mapping.GetTint(face),
            explicitUvRectData);
    }

    private static Vector3 RotateCuboidPoint(Vector3 point, Vector3 center, Quaternion rotation)
    {
        return center + rotation * (point - center);
    }

    private static void ResolveCuboidFace(
        ShapeBox box,
        BlockFace face,
        out Vector3 p0,
        out Vector3 p1,
        out Vector3 p2,
        out Vector3 p3,
        out Vector3 normal)
    {
        Vector3 min = box.min;
        Vector3 max = box.max;
        switch (face)
        {
            case BlockFace.Right:
                p0 = new Vector3(max.x, min.y, min.z);
                p1 = new Vector3(max.x, max.y, min.z);
                p2 = new Vector3(max.x, max.y, max.z);
                p3 = new Vector3(max.x, min.y, max.z);
                normal = Vector3.right;
                return;

            case BlockFace.Left:
                p0 = new Vector3(min.x, min.y, max.z);
                p1 = new Vector3(min.x, max.y, max.z);
                p2 = new Vector3(min.x, max.y, min.z);
                p3 = new Vector3(min.x, min.y, min.z);
                normal = Vector3.left;
                return;

            case BlockFace.Top:
                p0 = new Vector3(min.x, max.y, max.z);
                p1 = new Vector3(max.x, max.y, max.z);
                p2 = new Vector3(max.x, max.y, min.z);
                p3 = new Vector3(min.x, max.y, min.z);
                normal = Vector3.up;
                return;

            case BlockFace.Bottom:
                p0 = new Vector3(min.x, min.y, min.z);
                p1 = new Vector3(max.x, min.y, min.z);
                p2 = new Vector3(max.x, min.y, max.z);
                p3 = new Vector3(min.x, min.y, max.z);
                normal = Vector3.down;
                return;

            case BlockFace.Front:
                p0 = new Vector3(max.x, min.y, max.z);
                p1 = new Vector3(max.x, max.y, max.z);
                p2 = new Vector3(min.x, max.y, max.z);
                p3 = new Vector3(min.x, min.y, max.z);
                normal = Vector3.forward;
                return;

            default:
                p0 = new Vector3(min.x, min.y, min.z);
                p1 = new Vector3(min.x, max.y, min.z);
                p2 = new Vector3(max.x, max.y, min.z);
                p3 = new Vector3(max.x, min.y, min.z);
                normal = Vector3.back;
                return;
        }
    }

    private static void AppendStairMesh(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        StairShapeUtility.ResolveBoxes((byte)StairPlacementUtility.Encode(StairFacing.North, false), StairShapeVariant.Straight, out int boxCount, out ShapeBox box0, out ShapeBox box1, out ShapeBox box2, out ShapeBox box3, out ShapeBox box4);
        AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, box0, invAtlasTilesX, invAtlasTilesY);
        if (boxCount > 1) AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, box1, invAtlasTilesX, invAtlasTilesY);
        if (boxCount > 2) AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, box2, invAtlasTilesX, invAtlasTilesY);
        if (boxCount > 3) AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, box3, invAtlasTilesX, invAtlasTilesY);
        if (boxCount > 4) AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, box4, invAtlasTilesX, invAtlasTilesY);
    }

    private static void AppendFenceMesh(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, FenceShapeUtility.GetCenterPostVisualBox(), invAtlasTilesX, invAtlasTilesY);
        AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectWest, false), invAtlasTilesX, invAtlasTilesY);
        AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectWest, true), invAtlasTilesX, invAtlasTilesY);
        AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectEast, false), invAtlasTilesX, invAtlasTilesY);
        AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectEast, true), invAtlasTilesX, invAtlasTilesY);
    }

    private static void AppendFence2Mesh(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, FenceShapeUtility.GetCenterPostVisualBox(), invAtlasTilesX, invAtlasTilesY);
        AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, FenceShapeUtility.GetSingleRailVisualBox(FenceShapeUtility.ConnectWest), invAtlasTilesX, invAtlasTilesY);
        AppendShapeBox(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, FenceShapeUtility.GetSingleRailVisualBox(FenceShapeUtility.ConnectEast), invAtlasTilesX, invAtlasTilesY);
    }

    private static void AppendRampMesh(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        BlockPlacementAxis rampAxis = BlockPlacementAxis.Z;
        RampShapeVariant rampVariant = RampShapeVariant.Straight;

        RampShapeUtility.ResolveBottomQuad(rampAxis, out Vector3 bottom0, out Vector3 bottom1, out Vector3 bottom2, out Vector3 bottom3);
        AppendProjectedQuadFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, BlockFace.Bottom, origin, bottom0, bottom1, bottom2, bottom3, Vector3.down, invAtlasTilesX, invAtlasTilesY, false);

        RampShapeUtility.ResolveTopTriangles(rampAxis, rampVariant, out Vector3 top0a, out Vector3 top0b, out Vector3 top0c, out Vector3 top1a, out Vector3 top1b, out Vector3 top1c);
        Vector3 topNormal0 = Vector3.Normalize(Vector3.Cross(top0b - top0a, top0c - top0a));
        Vector3 topNormal1 = Vector3.Normalize(Vector3.Cross(top1b - top1a, top1c - top1a));
        AppendProjectedTriangleFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, BlockFace.Top, origin, top0a, top0b, top0c, topNormal0, invAtlasTilesX, invAtlasTilesY);
        AppendProjectedTriangleFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, BlockFace.Top, origin, top1a, top1b, top1c, topNormal1, invAtlasTilesX, invAtlasTilesY);

        AppendRampEdgeFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, rampAxis, rampVariant, RampEdge.Left, invAtlasTilesX, invAtlasTilesY);
        AppendRampEdgeFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, rampAxis, rampVariant, RampEdge.Right, invAtlasTilesX, invAtlasTilesY);
        AppendRampEdgeFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, rampAxis, rampVariant, RampEdge.Front, invAtlasTilesX, invAtlasTilesY);
        AppendRampEdgeFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, origin, rampAxis, rampVariant, RampEdge.Back, invAtlasTilesX, invAtlasTilesY);
    }

    private static void AppendRampEdgeFace(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        BlockPlacementAxis rampAxis,
        RampShapeVariant rampVariant,
        RampEdge edge,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        if (!RampShapeUtility.ResolveEdgeSurface(rampAxis, rampVariant, edge, out int vertexCount, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3, out BlockFace sampledFace))
            return;

        Vector3 normal = ResolveSpecialFaceNormal(sampledFace);
        if (vertexCount == 4)
        {
            AppendProjectedQuadFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, sampledFace, origin, p0, p1, p2, p3, normal, invAtlasTilesX, invAtlasTilesY, false);
            return;
        }

        AppendProjectedTriangleFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, sampledFace, origin, p0, p1, p2, normal, invAtlasTilesX, invAtlasTilesY);
    }

    private static void AppendVerticalRampMesh(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        BlockPlacementAxis axis = BlockPlacementAxis.ZNegative;

        VerticalRampShapeUtility.ResolveBottomTriangle(axis, out Vector3 bottom0, out Vector3 bottom1, out Vector3 bottom2);
        AppendProjectedTriangleFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, BlockFace.Bottom, origin, bottom0, bottom1, bottom2, Vector3.down, invAtlasTilesX, invAtlasTilesY);

        VerticalRampShapeUtility.ResolveTopTriangle(axis, out Vector3 top0, out Vector3 top1, out Vector3 top2);
        AppendProjectedTriangleFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, BlockFace.Top, origin, top0, top1, top2, Vector3.up, invAtlasTilesX, invAtlasTilesY);

        VerticalRampShapeUtility.ResolveSideQuad(axis, out Vector3 side0, out Vector3 side1, out Vector3 side2, out Vector3 side3, out BlockFace sideFace);
        AppendProjectedQuadFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, sideFace, origin, side0, side1, side2, side3, ResolveSpecialFaceNormal(sideFace), invAtlasTilesX, invAtlasTilesY, false);

        VerticalRampShapeUtility.ResolveFrontQuad(axis, out Vector3 front0, out Vector3 front1, out Vector3 front2, out Vector3 front3, out BlockFace frontFace);
        AppendProjectedQuadFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, frontFace, origin, front0, front1, front2, front3, ResolveSpecialFaceNormal(frontFace), invAtlasTilesX, invAtlasTilesY, false);

        VerticalRampShapeUtility.ResolveSlopeQuad(axis, out Vector3 slope0, out Vector3 slope1, out Vector3 slope2, out Vector3 slope3, out BlockFace slopeFace, out Vector3 slopeNormal);
        AppendProjectedQuadFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, slopeFace, origin, slope0, slope1, slope2, slope3, slopeNormal, invAtlasTilesX, invAtlasTilesY, false);
    }

    private static void AppendShapeBox(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        ShapeBox box,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        AppendShapeFace(vertices, normals, uv0, uv1, uv2, uv3, tris,
            origin + new Vector3(box.max.x, box.min.y, box.min.z),
            origin + new Vector3(box.max.x, box.max.y, box.min.z),
            origin + new Vector3(box.max.x, box.max.y, box.max.z),
            origin + new Vector3(box.max.x, box.min.y, box.max.z),
            Vector3.right,
            mapping,
            BlockFace.Right,
            mapping.GetTint(BlockFace.Right),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, uv3, tris,
            origin + new Vector3(box.min.x, box.min.y, box.max.z),
            origin + new Vector3(box.min.x, box.max.y, box.max.z),
            origin + new Vector3(box.min.x, box.max.y, box.min.z),
            origin + new Vector3(box.min.x, box.min.y, box.min.z),
            Vector3.left,
            mapping,
            BlockFace.Left,
            mapping.GetTint(BlockFace.Left),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, uv3, tris,
            origin + new Vector3(box.min.x, box.max.y, box.max.z),
            origin + new Vector3(box.max.x, box.max.y, box.max.z),
            origin + new Vector3(box.max.x, box.max.y, box.min.z),
            origin + new Vector3(box.min.x, box.max.y, box.min.z),
            Vector3.up,
            mapping,
            BlockFace.Top,
            mapping.GetTint(BlockFace.Top),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, uv3, tris,
            origin + new Vector3(box.min.x, box.min.y, box.min.z),
            origin + new Vector3(box.max.x, box.min.y, box.min.z),
            origin + new Vector3(box.max.x, box.min.y, box.max.z),
            origin + new Vector3(box.min.x, box.min.y, box.max.z),
            Vector3.down,
            mapping,
            BlockFace.Bottom,
            mapping.GetTint(BlockFace.Bottom),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, uv3, tris,
            origin + new Vector3(box.max.x, box.min.y, box.max.z),
            origin + new Vector3(box.max.x, box.max.y, box.max.z),
            origin + new Vector3(box.min.x, box.max.y, box.max.z),
            origin + new Vector3(box.min.x, box.min.y, box.max.z),
            Vector3.forward,
            mapping,
            BlockFace.Front,
            mapping.GetTint(BlockFace.Front),
            invAtlasTilesX,
            invAtlasTilesY);

        AppendShapeFace(vertices, normals, uv0, uv1, uv2, uv3, tris,
            origin + new Vector3(box.min.x, box.min.y, box.min.z),
            origin + new Vector3(box.min.x, box.max.y, box.min.z),
            origin + new Vector3(box.max.x, box.max.y, box.min.z),
            origin + new Vector3(box.max.x, box.min.y, box.min.z),
            Vector3.back,
            mapping,
            BlockFace.Back,
            mapping.GetTint(BlockFace.Back),
            invAtlasTilesX,
            invAtlasTilesY);
    }

    private static void AppendModelCuboidFaceRects(List<ShapeFaceRect> faceRects, ShapeBox box, BlockModelCuboid cuboid, BlockTextureMapping mapping)
    {
        AppendModelCuboidFaceRect(faceRects, box, cuboid, mapping, BlockFace.Right);
        AppendModelCuboidFaceRect(faceRects, box, cuboid, mapping, BlockFace.Left);
        AppendModelCuboidFaceRect(faceRects, box, cuboid, mapping, BlockFace.Top);
        AppendModelCuboidFaceRect(faceRects, box, cuboid, mapping, BlockFace.Bottom);
        AppendModelCuboidFaceRect(faceRects, box, cuboid, mapping, BlockFace.Front);
        AppendModelCuboidFaceRect(faceRects, box, cuboid, mapping, BlockFace.Back);
    }

    private static void AppendModelCuboidFaceRect(List<ShapeFaceRect> faceRects, ShapeBox box, BlockModelCuboid cuboid, BlockTextureMapping mapping, BlockFace face)
    {
        if (!cuboid.HasFace(face))
            return;

        Vector2Int tile = cuboid.GetTileCoord(face, mapping);
        bool tint = mapping.GetTint(face);
        AppendShapeFaceRect(
            faceRects,
            box,
            face,
            tile,
            tint,
            true,
            cuboid.TryGetUvRectData(face, mapping, out Vector4 explicitUvRectData)
                ? explicitUvRectData
                : default);
    }

    private static void AppendShapeFaceRect(List<ShapeFaceRect> faceRects, ShapeBox box, BlockFace face)
    {
        AppendShapeFaceRect(faceRects, box, face, default, false, false, default);
    }

    private static void AppendShapeFaceRect(
        List<ShapeFaceRect> faceRects,
        ShapeBox box,
        BlockFace face,
        Vector2Int tile,
        bool tint,
        bool usesExplicitAppearance,
        Vector4 explicitUvRectData)
    {
        switch (face)
        {
            case BlockFace.Right:
                faceRects.Add(new ShapeFaceRect
                {
                    face = BlockFace.Right,
                    plane = box.max.x,
                    minA = box.min.y,
                    maxA = box.max.y,
                    minB = box.min.z,
                    maxB = box.max.z,
                    tileX = tile.x,
                    tileY = tile.y,
                    tint = tint,
                    usesExplicitAppearance = usesExplicitAppearance,
                    explicitUvRectData = explicitUvRectData
                });
                return;

            case BlockFace.Left:
                faceRects.Add(new ShapeFaceRect
                {
                    face = BlockFace.Left,
                    plane = box.min.x,
                    minA = box.min.y,
                    maxA = box.max.y,
                    minB = box.min.z,
                    maxB = box.max.z,
                    tileX = tile.x,
                    tileY = tile.y,
                    tint = tint,
                    usesExplicitAppearance = usesExplicitAppearance,
                    explicitUvRectData = explicitUvRectData
                });
                return;

            case BlockFace.Top:
                faceRects.Add(new ShapeFaceRect
                {
                    face = BlockFace.Top,
                    plane = box.max.y,
                    minA = box.min.x,
                    maxA = box.max.x,
                    minB = box.min.z,
                    maxB = box.max.z,
                    tileX = tile.x,
                    tileY = tile.y,
                    tint = tint,
                    usesExplicitAppearance = usesExplicitAppearance,
                    explicitUvRectData = explicitUvRectData
                });
                return;

            case BlockFace.Bottom:
                faceRects.Add(new ShapeFaceRect
                {
                    face = BlockFace.Bottom,
                    plane = box.min.y,
                    minA = box.min.x,
                    maxA = box.max.x,
                    minB = box.min.z,
                    maxB = box.max.z,
                    tileX = tile.x,
                    tileY = tile.y,
                    tint = tint,
                    usesExplicitAppearance = usesExplicitAppearance,
                    explicitUvRectData = explicitUvRectData
                });
                return;

            case BlockFace.Front:
                faceRects.Add(new ShapeFaceRect
                {
                    face = BlockFace.Front,
                    plane = box.max.z,
                    minA = box.min.x,
                    maxA = box.max.x,
                    minB = box.min.y,
                    maxB = box.max.y,
                    tileX = tile.x,
                    tileY = tile.y,
                    tint = tint,
                    usesExplicitAppearance = usesExplicitAppearance,
                    explicitUvRectData = explicitUvRectData
                });
                return;

            case BlockFace.Back:
                faceRects.Add(new ShapeFaceRect
                {
                    face = BlockFace.Back,
                    plane = box.min.z,
                    minA = box.min.x,
                    maxA = box.max.x,
                    minB = box.min.y,
                    maxB = box.max.y,
                    tileX = tile.x,
                    tileY = tile.y,
                    tint = tint,
                    usesExplicitAppearance = usesExplicitAppearance,
                    explicitUvRectData = explicitUvRectData
                });
                return;
        }
    }

    private static void CullHiddenShapeFaceRects(List<ShapeFaceRect> faceRects, List<ShapeBox> shapeBoxes)
    {
        bool changed;
        do
        {
            changed = false;
            for (int i = 0; i < faceRects.Count && !changed; i++)
            {
                ShapeFaceRect rect = faceRects[i];
                for (int boxIndex = 0; boxIndex < shapeBoxes.Count; boxIndex++)
                {
                    if (!TryGetShapeBoxCoverage(rect, shapeBoxes[boxIndex], out ShapeFaceRect coverage))
                        continue;

                    if (!SubtractShapeFaceRectAt(faceRects, i, coverage))
                        continue;

                    changed = true;
                    break;
                }
            }
        }
        while (changed);

        do
        {
            changed = false;
            for (int i = 0; i < faceRects.Count && !changed; i++)
            {
                for (int j = i + 1; j < faceRects.Count; j++)
                {
                    if (!TryGetSameFaceOverlap(faceRects[i], faceRects[j], out ShapeFaceRect overlap))
                        continue;

                    if (!SubtractShapeFaceRectAt(faceRects, j, overlap))
                        continue;

                    changed = true;
                    break;
                }
            }
        }
        while (changed);
    }

    private static bool TryGetShapeBoxCoverage(ShapeFaceRect rect, ShapeBox box, out ShapeFaceRect coverage)
    {
        coverage = default;

        switch (rect.face)
        {
            case BlockFace.Right:
                if (box.min.x > rect.plane + ShapeFaceEpsilon || box.max.x <= rect.plane + ShapeFaceEpsilon)
                    return false;

                coverage = new ShapeFaceRect
                {
                    face = rect.face,
                    plane = rect.plane,
                    minA = box.min.y,
                    maxA = box.max.y,
                    minB = box.min.z,
                    maxB = box.max.z
                };
                break;

            case BlockFace.Left:
                if (box.max.x < rect.plane - ShapeFaceEpsilon || box.min.x >= rect.plane - ShapeFaceEpsilon)
                    return false;

                coverage = new ShapeFaceRect
                {
                    face = rect.face,
                    plane = rect.plane,
                    minA = box.min.y,
                    maxA = box.max.y,
                    minB = box.min.z,
                    maxB = box.max.z
                };
                break;

            case BlockFace.Top:
                if (box.min.y > rect.plane + ShapeFaceEpsilon || box.max.y <= rect.plane + ShapeFaceEpsilon)
                    return false;

                coverage = new ShapeFaceRect
                {
                    face = rect.face,
                    plane = rect.plane,
                    minA = box.min.x,
                    maxA = box.max.x,
                    minB = box.min.z,
                    maxB = box.max.z
                };
                break;

            case BlockFace.Bottom:
                if (box.max.y < rect.plane - ShapeFaceEpsilon || box.min.y >= rect.plane - ShapeFaceEpsilon)
                    return false;

                coverage = new ShapeFaceRect
                {
                    face = rect.face,
                    plane = rect.plane,
                    minA = box.min.x,
                    maxA = box.max.x,
                    minB = box.min.z,
                    maxB = box.max.z
                };
                break;

            case BlockFace.Front:
                if (box.min.z > rect.plane + ShapeFaceEpsilon || box.max.z <= rect.plane + ShapeFaceEpsilon)
                    return false;

                coverage = new ShapeFaceRect
                {
                    face = rect.face,
                    plane = rect.plane,
                    minA = box.min.x,
                    maxA = box.max.x,
                    minB = box.min.y,
                    maxB = box.max.y
                };
                break;

            case BlockFace.Back:
                if (box.max.z < rect.plane - ShapeFaceEpsilon || box.min.z >= rect.plane - ShapeFaceEpsilon)
                    return false;

                coverage = new ShapeFaceRect
                {
                    face = rect.face,
                    plane = rect.plane,
                    minA = box.min.x,
                    maxA = box.max.x,
                    minB = box.min.y,
                    maxB = box.max.y
                };
                break;

            default:
                return false;
        }

        return TryGetSameFaceOverlap(rect, coverage, out coverage);
    }

    private static bool TryGetSameFaceOverlap(ShapeFaceRect a, ShapeFaceRect b, out ShapeFaceRect overlap)
    {
        overlap = default;

        if (a.face != b.face || Mathf.Abs(a.plane - b.plane) > ShapeFaceEpsilon)
            return false;

        float minA = Mathf.Max(a.minA, b.minA);
        float maxA = Mathf.Min(a.maxA, b.maxA);
        float minB = Mathf.Max(a.minB, b.minB);
        float maxB = Mathf.Min(a.maxB, b.maxB);
        if (maxA <= minA + ShapeFaceEpsilon || maxB <= minB + ShapeFaceEpsilon)
            return false;

        overlap = new ShapeFaceRect
        {
            face = a.face,
            plane = a.plane,
            minA = minA,
            maxA = maxA,
            minB = minB,
            maxB = maxB
        };
        return true;
    }

    private static bool SubtractShapeFaceRectAt(List<ShapeFaceRect> faceRects, int index, ShapeFaceRect clip)
    {
        if (index < 0 || index >= faceRects.Count)
            return false;

        ShapeFaceRect source = faceRects[index];
        if (!TryGetSameFaceOverlap(source, clip, out ShapeFaceRect overlap))
            return false;

        faceRects.RemoveAt(index);
        AddShapeFaceRectFragment(faceRects, source, source.minA, source.maxA, source.minB, overlap.minB);
        AddShapeFaceRectFragment(faceRects, source, source.minA, source.maxA, overlap.maxB, source.maxB);
        AddShapeFaceRectFragment(faceRects, source, source.minA, overlap.minA, overlap.minB, overlap.maxB);
        AddShapeFaceRectFragment(faceRects, source, overlap.maxA, source.maxA, overlap.minB, overlap.maxB);
        return true;
    }

    private static void AddShapeFaceRectFragment(
        List<ShapeFaceRect> faceRects,
        ShapeFaceRect source,
        float minA,
        float maxA,
        float minB,
        float maxB)
    {
        if (maxA <= minA + ShapeFaceEpsilon || maxB <= minB + ShapeFaceEpsilon)
            return;

        faceRects.Add(new ShapeFaceRect
        {
            face = source.face,
            plane = source.plane,
            minA = minA,
            maxA = maxA,
            minB = minB,
            maxB = maxB,
            tileX = source.tileX,
            tileY = source.tileY,
            tint = source.tint,
            usesExplicitAppearance = source.usesExplicitAppearance,
            explicitUvRectData = source.explicitUvRectData
        });
    }

    private static void MergeShapeFaceRects(List<ShapeFaceRect> faceRects)
    {
        bool mergedAny;
        do
        {
            mergedAny = false;
            for (int i = 0; i < faceRects.Count && !mergedAny; i++)
            {
                for (int j = i + 1; j < faceRects.Count; j++)
                {
                    if (!TryMergeShapeFaceRects(faceRects[i], faceRects[j], out ShapeFaceRect merged))
                        continue;

                    faceRects[i] = merged;
                    faceRects.RemoveAt(j);
                    mergedAny = true;
                    break;
                }
            }
        }
        while (mergedAny);
    }

    private static bool TryMergeShapeFaceRects(ShapeFaceRect a, ShapeFaceRect b, out ShapeFaceRect merged)
    {
        merged = default;

        if (a.face != b.face ||
            Mathf.Abs(a.plane - b.plane) > ShapeFaceEpsilon ||
            !HasSameAppearance(a, b))
            return false;

        bool sameA = Mathf.Abs(a.minA - b.minA) <= ShapeFaceEpsilon && Mathf.Abs(a.maxA - b.maxA) <= ShapeFaceEpsilon;
        bool sameB = Mathf.Abs(a.minB - b.minB) <= ShapeFaceEpsilon && Mathf.Abs(a.maxB - b.maxB) <= ShapeFaceEpsilon;

        if (sameA && (Mathf.Abs(a.maxB - b.minB) <= ShapeFaceEpsilon || Mathf.Abs(b.maxB - a.minB) <= ShapeFaceEpsilon))
        {
            merged = a;
            merged.minB = Mathf.Min(a.minB, b.minB);
            merged.maxB = Mathf.Max(a.maxB, b.maxB);
            return true;
        }

        if (sameB && (Mathf.Abs(a.maxA - b.minA) <= ShapeFaceEpsilon || Mathf.Abs(b.maxA - a.minA) <= ShapeFaceEpsilon))
        {
            merged = a;
            merged.minA = Mathf.Min(a.minA, b.minA);
            merged.maxA = Mathf.Max(a.maxA, b.maxA);
            return true;
        }

        return false;
    }

    private static bool HasSameAppearance(ShapeFaceRect a, ShapeFaceRect b)
    {
        return a.usesExplicitAppearance == b.usesExplicitAppearance &&
               (!a.usesExplicitAppearance ||
                (a.tileX == b.tileX &&
                 a.tileY == b.tileY &&
                 a.tint == b.tint &&
                 Vector4.SqrMagnitude(a.explicitUvRectData - b.explicitUvRectData) <= 1e-10f));
    }

    private static void AppendShapeRect(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        Vector3 origin,
        ShapeFaceRect rect,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        bool hasExplicitAppearance = rect.usesExplicitAppearance;
        Vector2Int explicitTile = new Vector2Int(rect.tileX, rect.tileY);
        bool explicitTint = rect.tint;
        Vector4 explicitUvRectData = rect.explicitUvRectData;

        switch (rect.face)
        {
            case BlockFace.Right:
                AppendProjectedQuadFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, BlockFace.Right, origin,
                    new Vector3(rect.plane, rect.minA, rect.minB),
                    new Vector3(rect.plane, rect.maxA, rect.minB),
                    new Vector3(rect.plane, rect.maxA, rect.maxB),
                    new Vector3(rect.plane, rect.minA, rect.maxB),
                    Vector3.right,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    false,
                    hasExplicitAppearance,
                    explicitTile,
                    explicitTint,
                    explicitUvRectData);
                return;

            case BlockFace.Left:
                AppendProjectedQuadFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, BlockFace.Left, origin,
                    new Vector3(rect.plane, rect.minA, rect.maxB),
                    new Vector3(rect.plane, rect.maxA, rect.maxB),
                    new Vector3(rect.plane, rect.maxA, rect.minB),
                    new Vector3(rect.plane, rect.minA, rect.minB),
                    Vector3.left,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    false,
                    hasExplicitAppearance,
                    explicitTile,
                    explicitTint,
                    explicitUvRectData);
                return;

            case BlockFace.Top:
                AppendProjectedQuadFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, BlockFace.Top, origin,
                    new Vector3(rect.minA, rect.plane, rect.maxB),
                    new Vector3(rect.maxA, rect.plane, rect.maxB),
                    new Vector3(rect.maxA, rect.plane, rect.minB),
                    new Vector3(rect.minA, rect.plane, rect.minB),
                    Vector3.up,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    false,
                    hasExplicitAppearance,
                    explicitTile,
                    explicitTint,
                    explicitUvRectData);
                return;

            case BlockFace.Bottom:
                AppendProjectedQuadFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, BlockFace.Bottom, origin,
                    new Vector3(rect.minA, rect.plane, rect.minB),
                    new Vector3(rect.maxA, rect.plane, rect.minB),
                    new Vector3(rect.maxA, rect.plane, rect.maxB),
                    new Vector3(rect.minA, rect.plane, rect.maxB),
                    Vector3.down,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    false,
                    hasExplicitAppearance,
                    explicitTile,
                    explicitTint,
                    explicitUvRectData);
                return;

            case BlockFace.Front:
                AppendProjectedQuadFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, BlockFace.Front, origin,
                    new Vector3(rect.maxA, rect.minB, rect.plane),
                    new Vector3(rect.maxA, rect.maxB, rect.plane),
                    new Vector3(rect.minA, rect.maxB, rect.plane),
                    new Vector3(rect.minA, rect.minB, rect.plane),
                    Vector3.forward,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    false,
                    hasExplicitAppearance,
                    explicitTile,
                    explicitTint,
                    explicitUvRectData);
                return;

            case BlockFace.Back:
                AppendProjectedQuadFace(vertices, normals, uv0, uv1, uv2, uv3, tris, mapping, BlockFace.Back, origin,
                    new Vector3(rect.minA, rect.minB, rect.plane),
                    new Vector3(rect.minA, rect.maxB, rect.plane),
                    new Vector3(rect.maxA, rect.maxB, rect.plane),
                    new Vector3(rect.maxA, rect.minB, rect.plane),
                    Vector3.back,
                    invAtlasTilesX,
                    invAtlasTilesY,
                    false,
                    hasExplicitAppearance,
                    explicitTile,
                    explicitTint,
                    explicitUvRectData);
                return;
        }
    }

    private static void AppendShapeFace(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        Vector3 normal,
        BlockTextureMapping mapping,
        BlockFace textureFace,
        bool tint,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        int baseIndex = vertices.Count;
        ResolveAtlasRect(mapping, textureFace, out Vector2 atlasUv, out Vector2 atlasSize);

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

        AppendAtlasRect(uv1, uv3, atlasUv, atlasSize, 4);

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

    private static void AppendProjectedQuadFace(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        BlockFace sampledFace,
        Vector3 origin,
        Vector3 local0,
        Vector3 local1,
        Vector3 local2,
        Vector3 local3,
        Vector3 normal,
        float invAtlasTilesX,
        float invAtlasTilesY,
        bool invertWinding,
        bool hasExplicitAppearance = false,
        Vector2Int explicitTile = default(Vector2Int),
        bool explicitTint = false,
        Vector4 explicitUvRectData = default)
    {
        int baseIndex = vertices.Count;
        Vector2 atlasUv;
        Vector2 atlasSize;
        if (BlockAtlasUvUtility.IsValidUvRectData(explicitUvRectData))
        {
            atlasUv = new Vector2(explicitUvRectData.x, explicitUvRectData.y);
            atlasSize = new Vector2(explicitUvRectData.z, explicitUvRectData.w);
        }
        else if (hasExplicitAppearance)
        {
            ResolveAtlasRect(explicitTile, out atlasUv, out atlasSize);
        }
        else
        {
            ResolveAtlasRect(mapping, sampledFace, out atlasUv, out atlasSize);
        }
        Vector4 extra = new Vector4(1f, (hasExplicitAppearance ? explicitTint : mapping.GetTint(sampledFace)) ? 1f : 0f, 1f, 0f);

        vertices.Add(origin + local0);
        vertices.Add(origin + local1);
        vertices.Add(origin + local2);
        vertices.Add(origin + local3);

        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);

        Vector2 projectedUv0 = ResolveProjectedUv(sampledFace, local0);
        Vector2 projectedUv1 = ResolveProjectedUv(sampledFace, local1);
        Vector2 projectedUv2 = ResolveProjectedUv(sampledFace, local2);
        Vector2 projectedUv3 = ResolveProjectedUv(sampledFace, local3);
        if (BlockShapeUtility.GetEffectiveRenderShape(mapping) == BlockRenderShape.MultiCuboid)
            NormalizeProjectedQuadUv(ref projectedUv0, ref projectedUv1, ref projectedUv2, ref projectedUv3);

        uv0.Add(projectedUv0);
        uv0.Add(projectedUv1);
        uv0.Add(projectedUv2);
        uv0.Add(projectedUv3);

        AppendAtlasRect(uv1, uv3, atlasUv, atlasSize, 4);

        uv2.Add(extra);
        uv2.Add(extra);
        uv2.Add(extra);
        uv2.Add(extra);

        if (invertWinding)
        {
            tris.Add(baseIndex + 0);
            tris.Add(baseIndex + 2);
            tris.Add(baseIndex + 1);
            tris.Add(baseIndex + 0);
            tris.Add(baseIndex + 3);
            tris.Add(baseIndex + 2);
            return;
        }

        tris.Add(baseIndex + 0);
        tris.Add(baseIndex + 1);
        tris.Add(baseIndex + 2);
        tris.Add(baseIndex + 0);
        tris.Add(baseIndex + 2);
        tris.Add(baseIndex + 3);
    }

    private static void AppendProjectedTriangleFace(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        BlockTextureMapping mapping,
        BlockFace sampledFace,
        Vector3 origin,
        Vector3 local0,
        Vector3 local1,
        Vector3 local2,
        Vector3 normal,
        float invAtlasTilesX,
        float invAtlasTilesY)
    {
        int baseIndex = vertices.Count;
        ResolveAtlasRect(mapping, sampledFace, out Vector2 atlasUv, out Vector2 atlasSize);
        Vector4 extra = new Vector4(1f, mapping.GetTint(sampledFace) ? 1f : 0f, 1f, 0f);

        vertices.Add(origin + local0);
        vertices.Add(origin + local1);
        vertices.Add(origin + local2);

        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);

        uv0.Add(ResolveProjectedUv(sampledFace, local0));
        uv0.Add(ResolveProjectedUv(sampledFace, local1));
        uv0.Add(ResolveProjectedUv(sampledFace, local2));

        AppendAtlasRect(uv1, uv3, atlasUv, atlasSize, 3);

        uv2.Add(extra);
        uv2.Add(extra);
        uv2.Add(extra);

        tris.Add(baseIndex + 0);
        tris.Add(baseIndex + 1);
        tris.Add(baseIndex + 2);
    }

    private static void AppendDoubleSidedQuad(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uv0,
        List<Vector2> uv1,
        List<Vector4> uv2,
        List<Vector2> uv3,
        List<int> tris,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        Vector2 atlasUv,
        Vector2 atlasSize,
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

        AppendAtlasRect(uv1, uv3, atlasUv, atlasSize, 4);

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

    private static void ResolveAtlasRect(Vector2Int tile, out Vector2 atlasUv, out Vector2 atlasSize)
    {
        Vector4 uvRectData = BlockAtlasUvUtility.BuildLegacyUvRectData(tile, currentLegacyAtlasTiles, currentAtlasOriginTopLeft);
        atlasUv = new Vector2(uvRectData.x, uvRectData.y);
        atlasSize = new Vector2(uvRectData.z, uvRectData.w);
    }

    private static void ResolveAtlasRect(BlockTextureMapping mapping, BlockFace face, out Vector2 atlasUv, out Vector2 atlasSize)
    {
        Vector4 uvRectData = BlockAtlasUvUtility.ResolveUvRectData(mapping, face, currentLegacyAtlasTiles, currentAtlasOriginTopLeft);
        atlasUv = new Vector2(uvRectData.x, uvRectData.y);
        atlasSize = new Vector2(uvRectData.z, uvRectData.w);
    }

    private static void ResolveAtlasRect(BlockModelCuboid cuboid, BlockTextureMapping mapping, BlockFace face, out Vector2 atlasUv, out Vector2 atlasSize)
    {
        Vector4 uvRectData = BlockAtlasUvUtility.ResolveUvRectData(cuboid, face, mapping, currentLegacyAtlasTiles, currentAtlasOriginTopLeft);
        atlasUv = new Vector2(uvRectData.x, uvRectData.y);
        atlasSize = new Vector2(uvRectData.z, uvRectData.w);
    }

    private static void AppendAtlasRect(List<Vector2> uv1, List<Vector2> uv3, Vector2 atlasUv, Vector2 atlasSize, int vertexCount)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            uv1.Add(atlasUv);
            uv3.Add(atlasSize);
        }
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

    private static Vector2 ResolveProjectedUv(BlockFace sampledFace, Vector3 localPos)
    {
        return sampledFace switch
        {
            BlockFace.Top => new Vector2(localPos.x, localPos.z),
            BlockFace.Bottom => new Vector2(localPos.x, localPos.z),
            BlockFace.Right => new Vector2(localPos.z, localPos.y),
            BlockFace.Left => new Vector2(localPos.z, localPos.y),
            BlockFace.Front => new Vector2(localPos.x, localPos.y),
            BlockFace.Back => new Vector2(localPos.x, localPos.y),
            _ => new Vector2(localPos.x, localPos.y)
        };
    }

    private static void NormalizeProjectedQuadUv(ref Vector2 uv0, ref Vector2 uv1, ref Vector2 uv2, ref Vector2 uv3)
    {
        float minU = Mathf.Min(Mathf.Min(uv0.x, uv1.x), Mathf.Min(uv2.x, uv3.x));
        float maxU = Mathf.Max(Mathf.Max(uv0.x, uv1.x), Mathf.Max(uv2.x, uv3.x));
        float minV = Mathf.Min(Mathf.Min(uv0.y, uv1.y), Mathf.Min(uv2.y, uv3.y));
        float maxV = Mathf.Max(Mathf.Max(uv0.y, uv1.y), Mathf.Max(uv2.y, uv3.y));

        float invSpanU = 1f / Mathf.Max(maxU - minU, 1e-6f);
        float invSpanV = 1f / Mathf.Max(maxV - minV, 1e-6f);

        uv0 = new Vector2((uv0.x - minU) * invSpanU, (uv0.y - minV) * invSpanV);
        uv1 = new Vector2((uv1.x - minU) * invSpanU, (uv1.y - minV) * invSpanV);
        uv2 = new Vector2((uv2.x - minU) * invSpanU, (uv2.y - minV) * invSpanV);
        uv3 = new Vector2((uv3.x - minU) * invSpanU, (uv3.y - minV) * invSpanV);
    }

    private static Vector3 ResolveSpecialFaceNormal(BlockFace face)
    {
        return face switch
        {
            BlockFace.Right => Vector3.right,
            BlockFace.Left => Vector3.left,
            BlockFace.Top => Vector3.up,
            BlockFace.Bottom => Vector3.down,
            BlockFace.Front => Vector3.forward,
            _ => Vector3.back
        };
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
        UnregisterFromMergeGrid();
        ResetRuntimeState();
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
    }

    private void SetupPhysics(Vector3 throwDirection)
    {
        EnsureRuntimeComponents();

        Vector3 launchBase = throwDirection.sqrMagnitude > 0.0001f ? throwDirection.normalized : Vector3.forward;
        float launchSpeedMultiplier = Mathf.Max(1f, throwDirection.magnitude);
        Vector3 randomDir = new Vector3(Random.Range(-0.35f, 0.35f), 0f, Random.Range(-0.35f, 0.35f));
        Vector3 launchDir = (launchBase + randomDir + Vector3.up * 0.4f).normalized;

        velocity = launchDir * (launchForce * launchSpeedMultiplier);
        simulationAccumulator = 0f;
        isGrounded = false;
        isSleeping = false;
    }

    private void Awake()
    {
        gravity = Mathf.Max(0f, gravity);
        airDrag = Mathf.Max(0f, airDrag);
        groundedDrag = Mathf.Max(0f, groundedDrag);
        maxFallSpeed = Mathf.Max(0f, maxFallSpeed);
        simulationStepSeconds = Mathf.Max(0.01f, simulationStepSeconds);
        collisionHalfExtent = Mathf.Max(0.05f, collisionHalfExtent);
        pickupRadius = Mathf.Max(0.1f, pickupRadius);
        pickupCheckInterval = Mathf.Max(0.02f, pickupCheckInterval);
        mergeGridCellSize = Mathf.Max(0.25f, mergeGridCellSize);
        conveyorSpeed = Mathf.Max(0f, conveyorSpeed);
        conveyorCenteringStrength = Mathf.Max(0f, conveyorCenteringStrength);
        conveyorMaxCenteringSpeed = Mathf.Max(0f, conveyorMaxCenteringSpeed);
        mergeRadius = Mathf.Max(0.01f, mergeRadius);
        mergeCheckInterval = Mathf.Max(0.02f, mergeCheckInterval);
        mergeDelaySeconds = Mathf.Max(0f, mergeDelaySeconds);
        maxStackAmount = Mathf.Max(1, maxStackAmount);
        stackAmount = Mathf.Clamp(stackAmount, 1, maxStackAmount);
        if (conveyorRoutingKey == 0)
            conveyorRoutingKey = AllocateConveyorRoutingKey();
        EnsureRuntimeComponents();
    }

    private void Update()
    {
        if (!gameObject.activeSelf || isPooled || isCollected || isHeldByRoboticArm)
            return;

        WakeIfSupportWasRemoved();
        WakeIfOnConveyor();

        if (!isSleeping)
            SimulateMotion(Time.deltaTime);

        RefreshMergeGridCell();

        if (Time.time >= nextPickupCheckTime)
        {
            nextPickupCheckTime = Time.time + pickupCheckInterval;
            TryCollectNearbyPlayer();
        }

        if (Time.time >= nextMergeCheckTime)
        {
            nextMergeCheckTime = Time.time + mergeCheckInterval;
            TryMergeNearbyDrops();
        }

        UpdateDespawnTimer();
    }

    private void OnDestroy()
    {
        UnregisterFromMergeGrid();
    }

    private void TryCollectNearbyPlayer()
    {
        if (Time.time - spawnTime < pickupDelaySeconds)
            return;

        PlayerInventory inventory = PlayerInventory.Instance;
        if (inventory == null)
            return;

        if (!IsValidPickupTarget(inventory))
            return;

        float radius = Mathf.Max(0.1f, pickupRadius);
        Vector3 playerPos = ResolvePickupTargetPosition(inventory.transform);
        if ((playerPos - transform.position).sqrMagnitude > radius * radius)
            return;

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

    private bool IsValidPickupTarget(PlayerInventory inventory)
    {
        if (inventory == null)
            return false;

        if (!requirePlayerTag || string.IsNullOrEmpty(playerTag))
            return true;

        Transform target = inventory.transform;
        return target.CompareTag(playerTag) ||
               (target.root != null && target.root.CompareTag(playerTag));
    }

    private Vector3 ResolvePickupTargetPosition(Transform playerTransform)
    {
        if (playerTransform == null)
            return transform.position;

        CharacterController characterController = playerTransform.GetComponent<CharacterController>();
        if (characterController != null)
            return playerTransform.TransformPoint(characterController.center);

        return playerTransform.position + Vector3.up * 0.9f;
    }

    private void SimulateMotion(float deltaTime)
    {
        float clampedDelta = Mathf.Clamp(deltaTime, 0f, 0.1f);
        if (clampedDelta <= 0f)
            return;

        simulationAccumulator += clampedDelta;
        float step = Mathf.Max(0.01f, simulationStepSeconds);
        int maxSteps = 6;

        while (simulationAccumulator >= step && maxSteps-- > 0)
        {
            simulationAccumulator -= step;
            SimulateStep(step);
        }
    }

    private void SimulateStep(float deltaTime)
    {
        velocity.y = Mathf.Max(velocity.y - gravity * deltaTime, -maxFallSpeed);

        float drag = Mathf.Clamp01((isGrounded ? groundedDrag : airDrag) * deltaTime);
        velocity.x = Mathf.Lerp(velocity.x, 0f, drag);
        velocity.z = Mathf.Lerp(velocity.z, 0f, drag);

        World world = World.Instance;
        Vector3 nextPosition = transform.position;
        bool moveVerticalBeforeHorizontal = ApplyConveyorVelocity(world);

        if (moveVerticalBeforeHorizontal)
            ApplyVerticalMovement(world, ref nextPosition, deltaTime, ignoreConveyors: true);

        float moveX = velocity.x * deltaTime;
        if (Mathf.Abs(moveX) > 0.0001f)
        {
            Vector3 candidateX = nextPosition + new Vector3(moveX, 0f, 0f);
            if (IntersectsSolidIgnoringConveyors(world, candidateX))
                velocity.x = 0f;
            else
                nextPosition = candidateX;
        }

        float moveZ = velocity.z * deltaTime;
        if (Mathf.Abs(moveZ) > 0.0001f)
        {
            Vector3 candidateZ = nextPosition + new Vector3(0f, 0f, moveZ);
            if (IntersectsSolidIgnoringConveyors(world, candidateZ))
                velocity.z = 0f;
            else
                nextPosition = candidateZ;
        }

        if (!moveVerticalBeforeHorizontal)
            ApplyVerticalMovement(world, ref nextPosition, deltaTime, ignoreConveyors: false);

        if (Mathf.Abs(velocity.y * deltaTime) <= 0.0001f && isGrounded && !IsSupported(world, nextPosition))
        {
            isGrounded = false;
        }

        transform.position = nextPosition;

        if (isGrounded && velocity.sqrMagnitude < 0.0004f)
        {
            velocity = Vector3.zero;
            isSleeping = true;
        }
    }

    private bool IntersectsSolid(World world, Vector3 center)
    {
        return DropCollisionUtility.IntersectsSolid(world, center, collisionHalfExtent);
    }

    private bool IntersectsSolidIgnoringConveyors(World world, Vector3 center)
    {
        return DropCollisionUtility.IntersectsSolid(world, center, collisionHalfExtent, BlockType.ConveyorBelt);
    }

    private void ApplyVerticalMovement(World world, ref Vector3 nextPosition, float deltaTime, bool ignoreConveyors)
    {
        float moveY = velocity.y * deltaTime;
        if (Mathf.Abs(moveY) <= 0.0001f)
            return;

        Vector3 candidateY = nextPosition + new Vector3(0f, moveY, 0f);
        bool intersects = ignoreConveyors
            ? IntersectsSolidIgnoringConveyors(world, candidateY)
            : IntersectsSolid(world, candidateY);
        if (intersects)
        {
            if (moveY < 0f)
            {
                nextPosition.y = ResolveVerticalContactY(world, nextPosition.y, candidateY.y, nextPosition.x, nextPosition.z);
                float bounceSpeed = -velocity.y * bounceDamping;
                velocity.y = bounceSpeed > 0.35f ? bounceSpeed : 0f;
                isGrounded = true;
            }
            else
            {
                velocity.y = 0f;
            }
            return;
        }

        nextPosition = candidateY;
        isGrounded = false;
    }

    private float ResolveVerticalContactY(World world, float fromY, float toY, float x, float z)
    {
        if (world == null)
            return fromY;

        float lower = Mathf.Min(fromY, toY);
        float upper = Mathf.Max(fromY, toY);

        for (int i = 0; i < 6; i++)
        {
            float mid = (lower + upper) * 0.5f;
            Vector3 sample = new Vector3(x, mid, z);
            if (IntersectsSolid(world, sample))
                lower = mid;
            else
                upper = mid;
        }

        return upper;
    }

    private bool IsSupported(World world, Vector3 center)
    {
        Vector3 supportCheck = center + Vector3.down * 0.04f;
        return IntersectsSolid(world, supportCheck);
    }

    private void WakeIfSupportWasRemoved()
    {
        if (!isSleeping)
            return;

        World world = World.Instance;
        if (world != null && IsSupported(world, transform.position))
            return;

        isGrounded = false;
        isSleeping = false;
        simulationAccumulator = 0f;
    }

    private void UpdateDespawnTimer()
    {
        if (preventDespawnOnConveyor &&
            ConveyorBeltUtility.IsSupportedByConveyor(World.Instance, transform.position, collisionHalfExtent))
        {
            despawnStartTime = Time.time;
            return;
        }

        if (Time.time - despawnStartTime >= lifeTimeSeconds)
            ReturnToPool();
    }

    private void WakeIfOnConveyor()
    {
        if (!isSleeping)
            return;

        if (!ConveyorBeltUtility.TryGetConveyorVelocity(
                World.Instance,
                transform.position,
                collisionHalfExtent,
                conveyorSpeed,
                conveyorCenteringStrength,
                conveyorMaxCenteringSpeed,
                conveyorRoutingKey,
                ResolveItemForConveyorFilter(),
                out Vector3 conveyorVelocity) ||
            conveyorVelocity.sqrMagnitude <= 0.0001f)
        {
            return;
        }

        isGrounded = true;
        isSleeping = false;
        simulationAccumulator = 0f;
    }

    private bool ApplyConveyorVelocity(World world)
    {
        SplitStackAcrossSplitterIfNeeded(world);

        if (!ConveyorBeltUtility.TryGetConveyorVelocity(
                world,
                transform.position,
                collisionHalfExtent,
                conveyorSpeed,
                conveyorCenteringStrength,
                conveyorMaxCenteringSpeed,
                conveyorRoutingKey,
                ResolveItemForConveyorFilter(),
                out Vector3 conveyorVelocity))
        {
            return false;
        }

        velocity.x = conveyorVelocity.x;
        velocity.z = conveyorVelocity.z;
        if (Mathf.Abs(conveyorVelocity.y) > 0.0001f)
            velocity.y = conveyorVelocity.y;
        isGrounded = true;
        isSleeping = false;
        return conveyorVelocity.y > 0.0001f;
    }

    private void SplitStackAcrossSplitterIfNeeded(World world)
    {
        if (!ConveyorBeltUtility.TryGetSupportedSplitterOutputCount(
                world,
                transform.position,
                collisionHalfExtent,
                ResolveItemForConveyorFilter(),
                out Vector3Int splitterPos,
                out int outputCount))
        {
            hasLastStackSplitSplitterPos = false;
            return;
        }

        if (outputCount <= 1 || stackAmount <= 1)
            return;

        if (hasLastStackSplitSplitterPos && lastStackSplitSplitterPos == splitterPos)
            return;

        int segmentCount = Mathf.Min(outputCount, stackAmount);
        int baseAmount = stackAmount / segmentCount;
        int remainder = stackAmount % segmentCount;

        MarkStackSplitOnSplitter(splitterPos);
        stackAmount = baseAmount + (remainder > 0 ? 1 : 0);
        UpdateDropName();

        for (int i = 1; i < segmentCount; i++)
        {
            int segmentAmount = baseAmount + (i < remainder ? 1 : 0);
            if (segmentAmount <= 0)
                continue;

            if (!Spawn(world, transform.position, blockType, segmentAmount, Vector3.zero, out BlockDrop splitDrop) ||
                splitDrop == null)
            {
                stackAmount += segmentAmount;
                UpdateDropName();
                continue;
            }

            splitDrop.velocity = velocity;
            splitDrop.velocity.y = 0f;
            splitDrop.isGrounded = true;
            splitDrop.isSleeping = false;
            splitDrop.MarkStackSplitOnSplitter(splitterPos);
        }
    }

    private void MarkStackSplitOnSplitter(Vector3Int splitterPos)
    {
        hasLastStackSplitSplitterPos = true;
        lastStackSplitSplitterPos = splitterPos;
        mergeSuppressedUntil = Mathf.Max(mergeSuppressedUntil, Time.time + SplitterSplitMergeSuppressSeconds);
    }

    private Item ResolveItemForConveyorFilter()
    {
        return BlockItemCatalog.TryGetItemForBlock(blockType, out Item mappedItem) ? mappedItem : null;
    }

    private static int AllocateConveyorRoutingKey()
    {
        unchecked
        {
            nextConveyorRoutingKey++;
            if (nextConveyorRoutingKey == 0)
                nextConveyorRoutingKey++;

            return nextConveyorRoutingKey;
        }
    }

    private void TryMergeNearbyDrops()
    {
        if (stackAmount >= maxStackAmount)
            return;

        if (Time.time < mergeSuppressedUntil)
            return;

        if (Time.time - spawnTime < mergeDelaySeconds)
            return;

        CollectNearbyMergeCandidates(MergeCandidates);
        for (int i = 0; i < MergeCandidates.Count && stackAmount < maxStackAmount; i++)
        {
            BlockDrop other = MergeCandidates[i];
            if (!CanMergeWith(other))
                continue;

            if (GetInstanceID() > other.GetInstanceID())
                continue;

            AbsorbFrom(other);
        }

        MergeCandidates.Clear();
    }

    private void CollectNearbyMergeCandidates(List<BlockDrop> targetBuffer)
    {
        targetBuffer.Clear();
        if (!isRegisteredInMergeGrid)
            return;

        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    Vector3Int neighborCell = mergeGridCell + new Vector3Int(x, y, z);
                    if (!ActiveDropsByCell.TryGetValue(neighborCell, out HashSet<BlockDrop> dropsInCell))
                        continue;

                    foreach (BlockDrop drop in dropsInCell)
                    {
                        if (drop != null)
                            targetBuffer.Add(drop);
                    }
                }
            }
        }
    }

    private bool CanMergeWith(BlockDrop other)
    {
        if (other == null || other == this)
            return false;

        if (!other.gameObject.activeSelf || other.isPooled || other.isCollected)
            return false;

        if (other.blockType != blockType)
            return false;

        if (other.stackAmount <= 0)
            return false;

        if (Time.time < other.mergeSuppressedUntil)
            return false;

        if (Time.time - other.spawnTime < other.mergeDelaySeconds)
            return false;

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
        despawnStartTime = Mathf.Max(despawnStartTime, other.despawnStartTime);

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

    private void RegisterInMergeGrid()
    {
        if (isRegisteredInMergeGrid)
            return;

        mergeGridCell = WorldToMergeCell(transform.position);
        AddToMergeCell(mergeGridCell, this);
        isRegisteredInMergeGrid = true;
    }

    private void RefreshMergeGridCell()
    {
        if (!isRegisteredInMergeGrid)
        {
            RegisterInMergeGrid();
            return;
        }

        Vector3Int newCell = WorldToMergeCell(transform.position);
        if (newCell == mergeGridCell)
            return;

        RemoveFromMergeCell(mergeGridCell, this);
        mergeGridCell = newCell;
        AddToMergeCell(mergeGridCell, this);
    }

    private void UnregisterFromMergeGrid()
    {
        if (!isRegisteredInMergeGrid)
            return;

        RemoveFromMergeCell(mergeGridCell, this);
        isRegisteredInMergeGrid = false;
    }

    private Vector3Int WorldToMergeCell(Vector3 worldPosition)
    {
        float cellSize = Mathf.Max(0.25f, mergeGridCellSize);
        return new Vector3Int(
            Mathf.FloorToInt(worldPosition.x / cellSize),
            Mathf.FloorToInt(worldPosition.y / cellSize),
            Mathf.FloorToInt(worldPosition.z / cellSize));
    }

    private static void AddToMergeCell(Vector3Int cell, BlockDrop drop)
    {
        if (!ActiveDropsByCell.TryGetValue(cell, out HashSet<BlockDrop> cellDrops))
        {
            cellDrops = new HashSet<BlockDrop>();
            ActiveDropsByCell[cell] = cellDrops;
        }

        cellDrops.Add(drop);
    }

    private static void RemoveFromMergeCell(Vector3Int cell, BlockDrop drop)
    {
        if (!ActiveDropsByCell.TryGetValue(cell, out HashSet<BlockDrop> cellDrops))
            return;

        cellDrops.Remove(drop);
        if (cellDrops.Count == 0)
            ActiveDropsByCell.Remove(cell);
    }

    private void ResetRuntimeState()
    {
        velocity = Vector3.zero;
        despawnStartTime = 0f;
        simulationAccumulator = 0f;
        isGrounded = false;
        isSleeping = false;
        isHeldByRoboticArm = false;
        hasLastStackSplitSplitterPos = false;
        mergeSuppressedUntil = 0f;
        conveyorRoutingKey = 0;
    }
}

public class InventoryItemDrop : MonoBehaviour, IRoboticArmGrabbable, IRoboticArmItemStack
{
    [Header("Drop")]
    [SerializeField] private float lifeTimeSeconds = 30f;
    [SerializeField] private bool preventDespawnOnConveyor = true;
    [SerializeField] private float dropScale = 0.35f;
    [SerializeField, Min(0.05f)] private float itemVisualWorldHeight = 0.42f;
    [SerializeField, Min(0.05f)] private float itemVisualMaxWorldWidth = 0.7f;
    [SerializeField] private float launchForce = 2.2f;
    [SerializeField] private float pickupDelaySeconds = 0.25f;
    [SerializeField] private string playerTag = "Player";
    [SerializeField] private bool requirePlayerTag = false;
    [SerializeField] private bool debugPickupLogs = false;

    [Header("Lightweight Physics")]
    [SerializeField, Min(0f)] private float gravity = 24f;
    [SerializeField, Min(0f)] private float airDrag = 2.6f;
    [SerializeField, Min(0f)] private float groundedDrag = 14f;
    [SerializeField, Min(0f)] private float maxFallSpeed = 28f;
    [SerializeField, Range(0f, 1f)] private float bounceDamping = 0.22f;
    [SerializeField, Min(0.01f)] private float simulationStepSeconds = 0.02f;
    [SerializeField, Min(0.05f)] private float collisionHalfExtent = 0.18f;
    [SerializeField, Min(0.1f)] private float pickupRadius = 1.7f;
    [SerializeField, Min(0.02f)] private float pickupCheckInterval = 0.08f;
    [SerializeField, Min(0.25f)] private float mergeGridCellSize = 1.25f;
    [SerializeField, Min(0f)] private float conveyorSpeed = ConveyorBeltUtility.DefaultSpeed;
    [SerializeField, Min(0f)] private float conveyorCenteringStrength = ConveyorBeltUtility.DefaultCenteringStrength;
    [SerializeField, Min(0f)] private float conveyorMaxCenteringSpeed = ConveyorBeltUtility.DefaultMaxCenteringSpeed;

    [Header("Stacking")]
    [SerializeField, Min(1)] private int stackAmount = 1;
    [SerializeField, Min(1)] private int maxStackAmount = 64;
    [SerializeField, Min(0f)] private float mergeRadius = 1.15f;
    [SerializeField, Min(0.02f)] private float mergeCheckInterval = 0.2f;
    [SerializeField, Min(0f)] private float mergeDelaySeconds = 0.15f;

    [Header("Runtime")]
    [SerializeField] private Item item;

    private const int MaxPoolSize = 1024;
    private const float SplitterSplitMergeSuppressSeconds = 1.25f;
    private static readonly Stack<InventoryItemDrop> Pool = new Stack<InventoryItemDrop>(128);
    private static readonly Dictionary<Vector3Int, HashSet<InventoryItemDrop>> ActiveDropsByCell = new Dictionary<Vector3Int, HashSet<InventoryItemDrop>>();
    private static readonly List<InventoryItemDrop> MergeCandidates = new List<InventoryItemDrop>(64);
    private static Transform poolContainer;
    private static int nextConveyorRoutingKey;

    private float spawnTime;
    private float despawnStartTime;
    private float nextMergeCheckTime;
    private float nextPickupCheckTime;
    private float simulationAccumulator;
    private float visualSpinAngle;
    private Vector3 velocity;
    private Vector3Int mergeGridCell;
    private bool isCollected;
    private bool isPooled;
    private bool isGrounded;
    private bool isSleeping;
    private bool isRegisteredInMergeGrid;
    private bool isHeldByRoboticArm;
    private bool hasLastStackSplitSplitterPos;
    private Vector3Int lastStackSplitSplitterPos;
    private float mergeSuppressedUntil;
    private int conveyorRoutingKey;
    private Transform visualRoot;
    private SpriteRenderer spriteRenderer;

    public static bool Spawn(Item item, int amount, Vector3 worldPosition, Vector3 throwDirection)
    {
        return Spawn(item, amount, worldPosition, throwDirection, out _);
    }

    private static bool Spawn(
        Item item,
        int amount,
        Vector3 worldPosition,
        Vector3 throwDirection,
        out InventoryItemDrop spawnedDrop)
    {
        if (item == null || amount <= 0)
        {
            spawnedDrop = null;
            return false;
        }

        InventoryItemDrop drop = AcquireDrop();
        if (drop == null)
        {
            spawnedDrop = null;
            return false;
        }

        drop.transform.SetParent(null, false);
        drop.Initialize(item, amount, worldPosition, throwDirection);
        spawnedDrop = drop;
        return true;
    }

    private static InventoryItemDrop AcquireDrop()
    {
        while (Pool.Count > 0)
        {
            InventoryItemDrop pooledDrop = Pool.Pop();
            if (pooledDrop == null)
                continue;

            pooledDrop.isPooled = false;
            return pooledDrop;
        }

        GameObject go = new GameObject("ItemDrop_Pooled");
        go.transform.SetParent(GetPoolContainer(), false);
        InventoryItemDrop drop = go.AddComponent<InventoryItemDrop>();
        drop.EnsureRuntimeComponents();
        go.SetActive(false);
        return drop;
    }

    private static Transform GetPoolContainer()
    {
        if (poolContainer != null)
            return poolContainer;

        GameObject existing = GameObject.Find("InventoryItemDropPool");
        if (existing == null)
            existing = new GameObject("InventoryItemDropPool");

        poolContainer = existing.transform;
        return poolContainer;
    }

    private void Awake()
    {
        gravity = Mathf.Max(0f, gravity);
        airDrag = Mathf.Max(0f, airDrag);
        groundedDrag = Mathf.Max(0f, groundedDrag);
        maxFallSpeed = Mathf.Max(0f, maxFallSpeed);
        simulationStepSeconds = Mathf.Max(0.01f, simulationStepSeconds);
        collisionHalfExtent = Mathf.Max(0.05f, collisionHalfExtent);
        pickupRadius = Mathf.Max(0.1f, pickupRadius);
        pickupCheckInterval = Mathf.Max(0.02f, pickupCheckInterval);
        mergeGridCellSize = Mathf.Max(0.25f, mergeGridCellSize);
        conveyorSpeed = Mathf.Max(0f, conveyorSpeed);
        conveyorCenteringStrength = Mathf.Max(0f, conveyorCenteringStrength);
        conveyorMaxCenteringSpeed = Mathf.Max(0f, conveyorMaxCenteringSpeed);
        mergeRadius = Mathf.Max(0.01f, mergeRadius);
        mergeCheckInterval = Mathf.Max(0.02f, mergeCheckInterval);
        mergeDelaySeconds = Mathf.Max(0f, mergeDelaySeconds);
        maxStackAmount = Mathf.Max(1, maxStackAmount);
        stackAmount = Mathf.Clamp(stackAmount, 1, maxStackAmount);
        if (conveyorRoutingKey == 0)
            conveyorRoutingKey = AllocateConveyorRoutingKey();
        EnsureRuntimeComponents();
    }

    private void Initialize(Item droppedItem, int amount, Vector3 worldPosition, Vector3 throwDirection)
    {
        EnsureRuntimeComponents();

        item = droppedItem;
        maxStackAmount = Mathf.Max(1, droppedItem.maxStack);
        stackAmount = Mathf.Clamp(amount, 1, maxStackAmount);
        isCollected = false;
        isPooled = false;
        isHeldByRoboticArm = false;
        hasLastStackSplitSplitterPos = false;
        mergeSuppressedUntil = 0f;
        conveyorRoutingKey = AllocateConveyorRoutingKey();
        visualSpinAngle = Random.Range(0f, 360f);

        transform.position = worldPosition;
        transform.localScale = Vector3.one * dropScale;
        spawnTime = Time.time;
        despawnStartTime = spawnTime;
        nextMergeCheckTime = Time.time + Random.Range(0f, mergeCheckInterval);
        nextPickupCheckTime = Time.time + Random.Range(0f, pickupCheckInterval);
        simulationAccumulator = 0f;
        isGrounded = false;
        isSleeping = false;

        UpdateVisual();
        SetupPhysics(throwDirection);
        gameObject.SetActive(true);
        RegisterInMergeGrid();
        UpdateDropName();
    }

    public Transform DropTransform => transform;

    public Item DroppedItem => item;

    public bool CanBeGrabbedByRoboticArm =>
        gameObject.activeSelf &&
        !isPooled &&
        !isCollected &&
        !isHeldByRoboticArm &&
        item != null &&
        stackAmount > 0;

    public bool TryGetRoboticArmItemStack(out Item stackItem, out int amount)
    {
        stackItem = item;
        amount = stackAmount;
        return stackItem != null && amount > 0;
    }

    public int RemoveFromRoboticArmStack(int amountToRemove)
    {
        if (amountToRemove <= 0 || stackAmount <= 0)
            return 0;

        int removed = Mathf.Min(amountToRemove, stackAmount);
        stackAmount -= removed;

        if (stackAmount <= 0)
        {
            isCollected = true;
            ReturnToPool();
        }
        else
        {
            UpdateDropName();
        }

        return removed;
    }

    public void AttachToRoboticArm(Transform parent, Vector3 localPosition, Quaternion localRotation, float localScale)
    {
        if (parent == null || !CanBeGrabbedByRoboticArm)
            return;

        isHeldByRoboticArm = true;
        isCollected = false;
        isPooled = false;
        velocity = Vector3.zero;
        simulationAccumulator = 0f;
        isGrounded = false;
        isSleeping = true;
        UnregisterFromMergeGrid();

        transform.SetParent(parent, false);
        transform.localPosition = localPosition;
        transform.localRotation = localRotation;
        transform.localScale = Vector3.one * Mathf.Max(0.01f, localScale);
    }

    public void ReleaseFromRoboticArm(Vector3 worldPosition, Vector3 throwDirection)
    {
        if (isPooled)
            return;

        isHeldByRoboticArm = false;
        isCollected = false;
        hasLastStackSplitSplitterPos = false;
        mergeSuppressedUntil = 0f;
        conveyorRoutingKey = AllocateConveyorRoutingKey();
        transform.SetParent(null, true);
        transform.position = worldPosition;
        transform.rotation = Quaternion.identity;
        transform.localScale = Vector3.one * dropScale;
        spawnTime = Time.time;
        despawnStartTime = spawnTime;
        nextMergeCheckTime = Time.time + Random.Range(0f, mergeCheckInterval);
        nextPickupCheckTime = Time.time + Random.Range(0f, pickupCheckInterval);
        simulationAccumulator = 0f;
        isGrounded = false;
        isSleeping = false;

        SetupPhysics(throwDirection);
        gameObject.SetActive(true);
        RegisterInMergeGrid();
        UpdateDropName();
    }

    private void EnsureRuntimeComponents()
    {
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

        Vector3 launchBase = throwDirection.sqrMagnitude > 0.0001f ? throwDirection.normalized : Vector3.forward;
        float launchSpeedMultiplier = Mathf.Max(1f, throwDirection.magnitude);
        Vector3 randomDir = new Vector3(Random.Range(-0.35f, 0.35f), 0f, Random.Range(-0.35f, 0.35f));
        Vector3 launchDir = (launchBase + randomDir + Vector3.up * 0.4f).normalized;

        velocity = launchDir * (launchForce * launchSpeedMultiplier);
        simulationAccumulator = 0f;
        isGrounded = false;
        isSleeping = false;
    }

    private void ReturnToPool()
    {
        if (this == null || isPooled)
            return;

        isCollected = true;
        UnregisterFromMergeGrid();
        ResetRuntimeState();
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

    private void Update()
    {
        if (!gameObject.activeSelf || isPooled || isCollected || isHeldByRoboticArm)
            return;

        if (item == null)
        {
            ReturnToPool();
            return;
        }

        WakeIfSupportWasRemoved();
        WakeIfOnConveyor();

        if (!isSleeping)
            SimulateMotion(Time.deltaTime);

        RefreshMergeGridCell();
        UpdateVisualTransform();

        if (Time.time >= nextPickupCheckTime)
        {
            nextPickupCheckTime = Time.time + pickupCheckInterval;
            TryCollectNearbyPlayer();
        }

        if (Time.time >= nextMergeCheckTime)
        {
            nextMergeCheckTime = Time.time + mergeCheckInterval;
            TryMergeNearbyDrops();
        }

        UpdateDespawnTimer();
    }

    private void OnDestroy()
    {
        UnregisterFromMergeGrid();
    }

    private void TryCollectNearbyPlayer()
    {
        if (Time.time - spawnTime < pickupDelaySeconds)
            return;

        PlayerInventory inventory = PlayerInventory.Instance;
        if (inventory == null || item == null)
            return;

        if (!IsValidPickupTarget(inventory))
            return;

        float radius = Mathf.Max(0.1f, pickupRadius);
        Vector3 playerPos = ResolvePickupTargetPosition(inventory.transform);
        if ((playerPos - transform.position).sqrMagnitude > radius * radius)
            return;

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
            ReturnToPool();
            return;
        }

        UpdateDropName();
    }

    private bool IsValidPickupTarget(PlayerInventory inventory)
    {
        if (inventory == null)
            return false;

        if (!requirePlayerTag || string.IsNullOrEmpty(playerTag))
            return true;

        Transform target = inventory.transform;
        return target.CompareTag(playerTag) ||
               (target.root != null && target.root.CompareTag(playerTag));
    }

    private Vector3 ResolvePickupTargetPosition(Transform playerTransform)
    {
        if (playerTransform == null)
            return transform.position;

        CharacterController characterController = playerTransform.GetComponent<CharacterController>();
        if (characterController != null)
            return playerTransform.TransformPoint(characterController.center);

        return playerTransform.position + Vector3.up * 0.9f;
    }

    private void SimulateMotion(float deltaTime)
    {
        float clampedDelta = Mathf.Clamp(deltaTime, 0f, 0.1f);
        if (clampedDelta <= 0f)
            return;

        simulationAccumulator += clampedDelta;
        float step = Mathf.Max(0.01f, simulationStepSeconds);
        int maxSteps = 6;

        while (simulationAccumulator >= step && maxSteps-- > 0)
        {
            simulationAccumulator -= step;
            SimulateStep(step);
        }
    }

    private void SimulateStep(float deltaTime)
    {
        velocity.y = Mathf.Max(velocity.y - gravity * deltaTime, -maxFallSpeed);

        float drag = Mathf.Clamp01((isGrounded ? groundedDrag : airDrag) * deltaTime);
        velocity.x = Mathf.Lerp(velocity.x, 0f, drag);
        velocity.z = Mathf.Lerp(velocity.z, 0f, drag);

        World world = World.Instance;
        Vector3 nextPosition = transform.position;
        bool moveVerticalBeforeHorizontal = ApplyConveyorVelocity(world);

        if (moveVerticalBeforeHorizontal)
            ApplyVerticalMovement(world, ref nextPosition, deltaTime, ignoreConveyors: true);

        float moveX = velocity.x * deltaTime;
        if (Mathf.Abs(moveX) > 0.0001f)
        {
            Vector3 candidateX = nextPosition + new Vector3(moveX, 0f, 0f);
            if (IntersectsSolidIgnoringConveyors(world, candidateX))
                velocity.x = 0f;
            else
                nextPosition = candidateX;
        }

        float moveZ = velocity.z * deltaTime;
        if (Mathf.Abs(moveZ) > 0.0001f)
        {
            Vector3 candidateZ = nextPosition + new Vector3(0f, 0f, moveZ);
            if (IntersectsSolidIgnoringConveyors(world, candidateZ))
                velocity.z = 0f;
            else
                nextPosition = candidateZ;
        }

        if (!moveVerticalBeforeHorizontal)
            ApplyVerticalMovement(world, ref nextPosition, deltaTime, ignoreConveyors: false);

        if (Mathf.Abs(velocity.y * deltaTime) <= 0.0001f && isGrounded && !IsSupported(world, nextPosition))
        {
            isGrounded = false;
        }

        transform.position = nextPosition;

        if (isGrounded && velocity.sqrMagnitude < 0.0004f)
        {
            velocity = Vector3.zero;
            isSleeping = true;
        }
    }

    private bool IntersectsSolid(World world, Vector3 center)
    {
        return DropCollisionUtility.IntersectsSolid(world, center, collisionHalfExtent);
    }

    private bool IntersectsSolidIgnoringConveyors(World world, Vector3 center)
    {
        return DropCollisionUtility.IntersectsSolid(world, center, collisionHalfExtent, BlockType.ConveyorBelt);
    }

    private void ApplyVerticalMovement(World world, ref Vector3 nextPosition, float deltaTime, bool ignoreConveyors)
    {
        float moveY = velocity.y * deltaTime;
        if (Mathf.Abs(moveY) <= 0.0001f)
            return;

        Vector3 candidateY = nextPosition + new Vector3(0f, moveY, 0f);
        bool intersects = ignoreConveyors
            ? IntersectsSolidIgnoringConveyors(world, candidateY)
            : IntersectsSolid(world, candidateY);
        if (intersects)
        {
            if (moveY < 0f)
            {
                nextPosition.y = ResolveVerticalContactY(world, nextPosition.y, candidateY.y, nextPosition.x, nextPosition.z);
                float bounceSpeed = -velocity.y * bounceDamping;
                velocity.y = bounceSpeed > 0.35f ? bounceSpeed : 0f;
                isGrounded = true;
            }
            else
            {
                velocity.y = 0f;
            }
            return;
        }

        nextPosition = candidateY;
        isGrounded = false;
    }

    private float ResolveVerticalContactY(World world, float fromY, float toY, float x, float z)
    {
        if (world == null)
            return fromY;

        float lower = Mathf.Min(fromY, toY);
        float upper = Mathf.Max(fromY, toY);

        for (int i = 0; i < 6; i++)
        {
            float mid = (lower + upper) * 0.5f;
            Vector3 sample = new Vector3(x, mid, z);
            if (IntersectsSolid(world, sample))
                lower = mid;
            else
                upper = mid;
        }

        return upper;
    }

    private bool IsSupported(World world, Vector3 center)
    {
        Vector3 supportCheck = center + Vector3.down * 0.04f;
        return IntersectsSolid(world, supportCheck);
    }

    private void WakeIfSupportWasRemoved()
    {
        if (!isSleeping)
            return;

        World world = World.Instance;
        if (world != null && IsSupported(world, transform.position))
            return;

        isGrounded = false;
        isSleeping = false;
        simulationAccumulator = 0f;
    }

    private void UpdateDespawnTimer()
    {
        if (preventDespawnOnConveyor &&
            ConveyorBeltUtility.IsSupportedByConveyor(World.Instance, transform.position, collisionHalfExtent))
        {
            despawnStartTime = Time.time;
            return;
        }

        if (Time.time - despawnStartTime >= lifeTimeSeconds)
            ReturnToPool();
    }

    private void WakeIfOnConveyor()
    {
        if (!isSleeping)
            return;

        if (!ConveyorBeltUtility.TryGetConveyorVelocity(
                World.Instance,
                transform.position,
                collisionHalfExtent,
                conveyorSpeed,
                conveyorCenteringStrength,
                conveyorMaxCenteringSpeed,
                conveyorRoutingKey,
                item,
                out Vector3 conveyorVelocity) ||
            conveyorVelocity.sqrMagnitude <= 0.0001f)
        {
            return;
        }

        isGrounded = true;
        isSleeping = false;
        simulationAccumulator = 0f;
    }

    private bool ApplyConveyorVelocity(World world)
    {
        SplitStackAcrossSplitterIfNeeded(world);

        if (!ConveyorBeltUtility.TryGetConveyorVelocity(
                world,
                transform.position,
                collisionHalfExtent,
                conveyorSpeed,
                conveyorCenteringStrength,
                conveyorMaxCenteringSpeed,
                conveyorRoutingKey,
                item,
                out Vector3 conveyorVelocity))
        {
            return false;
        }

        velocity.x = conveyorVelocity.x;
        velocity.z = conveyorVelocity.z;
        if (Mathf.Abs(conveyorVelocity.y) > 0.0001f)
            velocity.y = conveyorVelocity.y;
        isGrounded = true;
        isSleeping = false;
        return conveyorVelocity.y > 0.0001f;
    }

    private void SplitStackAcrossSplitterIfNeeded(World world)
    {
        if (!ConveyorBeltUtility.TryGetSupportedSplitterOutputCount(
                world,
                transform.position,
                collisionHalfExtent,
                item,
                out Vector3Int splitterPos,
                out int outputCount))
        {
            hasLastStackSplitSplitterPos = false;
            return;
        }

        if (outputCount <= 1 || stackAmount <= 1)
            return;

        if (hasLastStackSplitSplitterPos && lastStackSplitSplitterPos == splitterPos)
            return;

        int segmentCount = Mathf.Min(outputCount, stackAmount);
        int baseAmount = stackAmount / segmentCount;
        int remainder = stackAmount % segmentCount;

        MarkStackSplitOnSplitter(splitterPos);
        stackAmount = baseAmount + (remainder > 0 ? 1 : 0);
        UpdateDropName();

        for (int i = 1; i < segmentCount; i++)
        {
            int segmentAmount = baseAmount + (i < remainder ? 1 : 0);
            if (segmentAmount <= 0)
                continue;

            if (!Spawn(item, segmentAmount, transform.position, Vector3.zero, out InventoryItemDrop splitDrop) ||
                splitDrop == null)
            {
                stackAmount += segmentAmount;
                UpdateDropName();
                continue;
            }

            splitDrop.velocity = velocity;
            splitDrop.velocity.y = 0f;
            splitDrop.isGrounded = true;
            splitDrop.isSleeping = false;
            splitDrop.MarkStackSplitOnSplitter(splitterPos);
        }
    }

    private void MarkStackSplitOnSplitter(Vector3Int splitterPos)
    {
        hasLastStackSplitSplitterPos = true;
        lastStackSplitSplitterPos = splitterPos;
        mergeSuppressedUntil = Mathf.Max(mergeSuppressedUntil, Time.time + SplitterSplitMergeSuppressSeconds);
    }

    private static int AllocateConveyorRoutingKey()
    {
        unchecked
        {
            nextConveyorRoutingKey++;
            if (nextConveyorRoutingKey == 0)
                nextConveyorRoutingKey++;

            return nextConveyorRoutingKey;
        }
    }

    private void TryMergeNearbyDrops()
    {
        if (stackAmount >= maxStackAmount)
            return;

        if (Time.time < mergeSuppressedUntil)
            return;

        if (Time.time - spawnTime < mergeDelaySeconds)
            return;

        CollectNearbyMergeCandidates(MergeCandidates);
        for (int i = 0; i < MergeCandidates.Count && stackAmount < maxStackAmount; i++)
        {
            InventoryItemDrop other = MergeCandidates[i];

            if (!CanMergeWith(other))
                continue;

            if (GetInstanceID() > other.GetInstanceID())
                continue;

            AbsorbFrom(other);
        }

        MergeCandidates.Clear();
    }

    private void CollectNearbyMergeCandidates(List<InventoryItemDrop> targetBuffer)
    {
        targetBuffer.Clear();
        if (!isRegisteredInMergeGrid)
            return;

        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    Vector3Int neighborCell = mergeGridCell + new Vector3Int(x, y, z);
                    if (!ActiveDropsByCell.TryGetValue(neighborCell, out HashSet<InventoryItemDrop> dropsInCell))
                        continue;

                    foreach (InventoryItemDrop drop in dropsInCell)
                    {
                        if (drop != null)
                            targetBuffer.Add(drop);
                    }
                }
            }
        }
    }

    private bool CanMergeWith(InventoryItemDrop other)
    {
        if (other == null || other == this)
            return false;

        if (!other.gameObject.activeSelf || other.isPooled || other.isCollected || other.item == null)
            return false;

        if (other.item != item)
            return false;

        if (other.stackAmount <= 0)
            return false;

        if (Time.time < other.mergeSuppressedUntil)
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
        despawnStartTime = Mathf.Max(despawnStartTime, other.despawnStartTime);

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

    private void UpdateVisual()
    {
        if (spriteRenderer == null)
            return;

        Sprite icon = ItemIconResolver.ResolveForUI(item);

        spriteRenderer.sprite = icon;
        spriteRenderer.enabled = icon != null;

        if (visualRoot == null)
            return;

        if (icon != null)
        {
            visualRoot.localScale = ResolveItemVisualLocalScale(icon);
        }
        else
        {
            visualRoot.localScale = Vector3.one * ResolveVisualLocalScale(itemVisualWorldHeight, 1f);
        }
    }

    private Vector3 ResolveItemVisualLocalScale(Sprite icon)
    {
        if (icon == null)
            return Vector3.one * ResolveVisualLocalScale(itemVisualWorldHeight, 1f);

        Vector2 spriteSize = icon.bounds.size;
        float spriteHeight = Mathf.Max(0.0001f, spriteSize.y);
        float spriteWidth = Mathf.Max(0.0001f, spriteSize.x);
        float scaleByHeight = ResolveVisualLocalScale(itemVisualWorldHeight, spriteHeight);
        float scaleByWidth = ResolveVisualLocalScale(itemVisualMaxWorldWidth, spriteWidth);
        float uniformScale = Mathf.Min(scaleByHeight, scaleByWidth);
        return Vector3.one * Mathf.Max(0.0001f, uniformScale);
    }

    private float ResolveVisualLocalScale(float targetWorldSize, float spriteLocalSize)
    {
        float parentScale = Mathf.Max(0.0001f, Mathf.Max(
            Mathf.Abs(transform.lossyScale.x),
            Mathf.Abs(transform.lossyScale.y)));
        return Mathf.Max(0.0001f, targetWorldSize) / (parentScale * Mathf.Max(0.0001f, spriteLocalSize));
    }

    private void UpdateVisualTransform()
    {
        if (visualRoot == null)
            return;

        bool supportedByConveyor = ConveyorBeltUtility.TryGetSupportConveyorVisualFrame(
            World.Instance,
            transform.position,
            collisionHalfExtent,
            out Vector3 conveyorSurfaceNormal,
            out Vector3 conveyorVisualForward);

        visualRoot.localPosition = new Vector3(0f, 0.18f, 0f);

        if (supportedByConveyor)
        {
            visualRoot.rotation = Quaternion.LookRotation(conveyorSurfaceNormal, conveyorVisualForward) *
                                  Quaternion.Euler(0f, 0f, visualSpinAngle);
            return;
        }

        visualRoot.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward) *
                              Quaternion.Euler(0f, 0f, visualSpinAngle);
    }

    private void UpdateDropName()
    {
        string itemName = item != null ? item.name : "Unknown";
        gameObject.name = $"Drop_{itemName}_x{stackAmount}";
    }

    private void RegisterInMergeGrid()
    {
        if (isRegisteredInMergeGrid)
            return;

        mergeGridCell = WorldToMergeCell(transform.position);
        AddToMergeCell(mergeGridCell, this);
        isRegisteredInMergeGrid = true;
    }

    private void RefreshMergeGridCell()
    {
        if (!isRegisteredInMergeGrid)
        {
            RegisterInMergeGrid();
            return;
        }

        Vector3Int newCell = WorldToMergeCell(transform.position);
        if (newCell == mergeGridCell)
            return;

        RemoveFromMergeCell(mergeGridCell, this);
        mergeGridCell = newCell;
        AddToMergeCell(mergeGridCell, this);
    }

    private void UnregisterFromMergeGrid()
    {
        if (!isRegisteredInMergeGrid)
            return;

        RemoveFromMergeCell(mergeGridCell, this);
        isRegisteredInMergeGrid = false;
    }

    private Vector3Int WorldToMergeCell(Vector3 worldPosition)
    {
        float cellSize = Mathf.Max(0.25f, mergeGridCellSize);
        return new Vector3Int(
            Mathf.FloorToInt(worldPosition.x / cellSize),
            Mathf.FloorToInt(worldPosition.y / cellSize),
            Mathf.FloorToInt(worldPosition.z / cellSize));
    }

    private static void AddToMergeCell(Vector3Int cell, InventoryItemDrop drop)
    {
        if (!ActiveDropsByCell.TryGetValue(cell, out HashSet<InventoryItemDrop> cellDrops))
        {
            cellDrops = new HashSet<InventoryItemDrop>();
            ActiveDropsByCell[cell] = cellDrops;
        }

        cellDrops.Add(drop);
    }

    private static void RemoveFromMergeCell(Vector3Int cell, InventoryItemDrop drop)
    {
        if (!ActiveDropsByCell.TryGetValue(cell, out HashSet<InventoryItemDrop> cellDrops))
            return;

        cellDrops.Remove(drop);
        if (cellDrops.Count == 0)
            ActiveDropsByCell.Remove(cell);
    }

    private void ResetRuntimeState()
    {
        velocity = Vector3.zero;
        despawnStartTime = 0f;
        simulationAccumulator = 0f;
        isGrounded = false;
        isSleeping = false;
        isHeldByRoboticArm = false;
        hasLastStackSplitSplitterPos = false;
        mergeSuppressedUntil = 0f;
        conveyorRoutingKey = 0;
        item = null;

        if (spriteRenderer != null)
        {
            spriteRenderer.sprite = null;
            spriteRenderer.enabled = false;
        }
    }
}

public static class BlockBreakDropResolver
{
    public static bool TrySpawnDrop(World world, Vector3Int blockPos, BlockType brokenBlockType, Vector3 throwDirection)
    {
        Vector3 spawnPosition = blockPos + Vector3.one * 0.5f + Vector3.up * 0.08f;
        return TrySpawnDrop(world, spawnPosition, brokenBlockType, throwDirection);
    }

    public static bool TrySpawnDrop(World world, Vector3 worldPosition, BlockType brokenBlockType, Vector3 throwDirection)
    {
        if (brokenBlockType == BlockType.Leaves)
            return TrySpawnOakLeafSaplingDrop(world, worldPosition, throwDirection);

        if (!TryResolveDrop(world, brokenBlockType, out Item dropItem, out BlockType dropBlockType, out int amount))
            return false;

        if (dropItem != null)
            return TrySpawnItemDrop(world, dropItem, amount, worldPosition, throwDirection);

        return BlockDrop.Spawn(world, worldPosition, dropBlockType, amount, throwDirection);
    }

    public static bool TryAddDropToInventory(PlayerInventory inventory, World world, BlockType brokenBlockType)
    {
        if (inventory == null)
            return false;

        if (brokenBlockType == BlockType.Leaves)
            return TryAddOakLeafSaplingDropToInventory(inventory, world);

        if (!TryResolveDrop(world, brokenBlockType, out Item dropItem, out BlockType dropBlockType, out int amount))
            return false;

        if (dropItem != null)
            return inventory.InsertItem(dropItem, amount) == 0;

        return inventory.TryAddBlockDrop(dropBlockType, amount);
    }

    public static bool TryResolveDrop(
        World world,
        BlockType brokenBlockType,
        out Item dropItem,
        out BlockType dropBlockType,
        out int amount)
    {
        dropItem = null;
        dropBlockType = BlockType.Air;
        amount = 1;

        if (brokenBlockType == BlockType.Air || brokenBlockType == BlockType.Bedrock)
            return false;

        if (brokenBlockType == BlockType.Leaves)
            return false;

        if (world != null &&
            world.blockData != null &&
            world.blockData.TryGetCustomDrop(brokenBlockType, out dropItem, out amount))
        {
            amount = Mathf.Max(1, amount);
            return dropItem != null;
        }

        dropBlockType = ResolveDefaultDropBlockType(brokenBlockType);
        return dropBlockType != BlockType.Air && dropBlockType != BlockType.Bedrock;
    }

    public static int RollOakLeafSaplingDropCount(World world, int leafCount)
    {
        int clampedLeafCount = Mathf.Max(0, leafCount);
        int drops = 0;
        for (int i = 0; i < clampedLeafCount; i++)
        {
            if (RollOakLeafSaplingDrop(world))
                drops++;
        }

        return drops;
    }

    private static bool TrySpawnOakLeafSaplingDrop(World world, Vector3 worldPosition, Vector3 throwDirection)
    {
        if (!RollOakLeafSaplingDrop(world))
            return true;

        return BlockDrop.Spawn(world, worldPosition, BlockType.oakTreeSapling, 1, throwDirection);
    }

    private static bool TryAddOakLeafSaplingDropToInventory(PlayerInventory inventory, World world)
    {
        if (!RollOakLeafSaplingDrop(world))
            return true;

        return inventory.TryAddBlockDrop(BlockType.oakTreeSapling, 1);
    }

    private static bool RollOakLeafSaplingDrop(World world)
    {
        int oneIn = world != null ? world.OakLeafSaplingDropOneIn : 40;
        return Random.Range(0, Mathf.Max(1, oneIn)) == 0;
    }

    private static bool TrySpawnItemDrop(
        World world,
        Item dropItem,
        int amount,
        Vector3 worldPosition,
        Vector3 throwDirection)
    {
        if (dropItem == null)
            return false;

        amount = Mathf.Max(1, amount);
        if (dropItem.TryGetBlockType(out BlockType itemBlockType))
            return BlockDrop.Spawn(world, worldPosition, itemBlockType, amount, throwDirection);

        return InventoryItemDrop.Spawn(dropItem, amount, worldPosition, throwDirection);
    }

    private static BlockType ResolveDefaultDropBlockType(BlockType blockType)
    {
        BlockType dropBlockType = BatteryBlockUtility.GetInventoryDropBlockType(blockType);
        dropBlockType = TransportTubeUtility.GetInventoryDropBlockType(dropBlockType);
        dropBlockType = FluidPipeUtility.GetInventoryDropBlockType(dropBlockType);
        dropBlockType = TorchPlacementUtility.GetInventoryDropBlockType(dropBlockType);
        return LeverUtility.GetInventoryDropBlockType(dropBlockType);
    }
}
