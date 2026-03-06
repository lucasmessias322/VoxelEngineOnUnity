using System.Collections.Generic;
using UnityEngine;

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

    [Header("Runtime")]
    [SerializeField] private BlockType blockType;

    private float spawnTime;
    private Rigidbody rb;

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

    public static void Spawn(World world, Vector3Int blockPos, BlockType blockType, Vector3 throwDirection)
    {
        if (world == null || world.blockData == null || world.Material == null || world.Material.Length == 0)
            return;

        GameObject go = new GameObject($"Drop_{blockType}");
        go.transform.position = blockPos + Vector3.one * 0.5f + Vector3.up * 0.08f;

        BlockDrop drop = go.AddComponent<BlockDrop>();
        drop.blockType = blockType;
        go.transform.localScale = Vector3.one * drop.dropScale;
        drop.BuildVisual(world, blockType);
        drop.SetupPhysics(throwDirection);
    }

    private void BuildVisual(World world, BlockType blockType)
    {
        MeshFilter mf = gameObject.AddComponent<MeshFilter>();
        MeshRenderer mr = gameObject.AddComponent<MeshRenderer>();

        Mesh mesh = BuildDropMesh(world, blockType, out int submeshIndex);
        mf.sharedMesh = mesh;

        mr.materials = world.Material;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        mr.receiveShadows = true;

        if (submeshIndex != 0)
        {
            // Keep renderer using the correct pipeline submesh (opaque/transparent/water).
            int[] empty = new int[0];
            if (submeshIndex != 0) mesh.SetTriangles(empty, 0, false);
            if (submeshIndex != 1) mesh.SetTriangles(empty, 1, false);
            if (submeshIndex != 2) mesh.SetTriangles(empty, 2, false);
        }
    }

    private Mesh BuildDropMesh(World world, BlockType blockType, out int submeshIndex)
    {
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

        for (int f = 0; f < 6; f++)
        {
            FaceDef face = FaceDefs[f];
            int baseIndex = vertices.Count;

            vertices.Add(face.v0 - Vector3.one * 0.5f);
            vertices.Add(face.v1 - Vector3.one * 0.5f);
            vertices.Add(face.v2 - Vector3.one * 0.5f);
            vertices.Add(face.v3 - Vector3.one * 0.5f);

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
        }

        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uv0);
        mesh.SetUVs(1, uv1);
        mesh.SetUVs(2, uv2);
        mesh.subMeshCount = 3;

        submeshIndex = 0;
        if (blockType == BlockType.Water) submeshIndex = 2;
        else if (mapping.isTransparent) submeshIndex = 1;

        List<int> empty = new List<int>(0);
        mesh.SetTriangles(submeshIndex == 0 ? tris : empty, 0, false);
        mesh.SetTriangles(submeshIndex == 1 ? tris : empty, 1, false);
        mesh.SetTriangles(submeshIndex == 2 ? tris : empty, 2, false);
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Vector2Int GetTileForFace(BlockTextureMapping mapping, int faceIndex)
    {
        if (faceIndex == 2) return mapping.top;
        if (faceIndex == 3) return mapping.bottom;
        return mapping.side;
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
        if (faceIndex == 2) return mapping.tintTop;
        if (faceIndex == 3) return mapping.tintBottom;
        return mapping.tintSide;
    }

    private void SetupPhysics(Vector3 throwDirection)
    {
        BoxCollider box = gameObject.AddComponent<BoxCollider>();
        box.size = Vector3.one;

        SphereCollider pickup = gameObject.AddComponent<SphereCollider>();
        pickup.radius = 1f;
        pickup.isTrigger = true;

        rb = gameObject.AddComponent<Rigidbody>();
        rb.mass = 0.25f;
        rb.linearDamping = 1.4f;
        rb.angularDamping = 0.7f;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        Vector3 randomDir = new Vector3(Random.Range(-0.35f, 0.35f), 0f, Random.Range(-0.35f, 0.35f));
        Vector3 launchDir = (throwDirection.normalized + randomDir + Vector3.up * 0.4f).normalized;
        rb.AddForce(launchDir * launchForce, ForceMode.Impulse);
    }

    private void Awake()
    {
        spawnTime = Time.time;
    }

    private void Update()
    {
        transform.Rotate(Vector3.up, rotateSpeed * Time.deltaTime, Space.World);

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
        if (other == null) return;
        if (Time.time - spawnTime < pickupDelaySeconds) return;

        PlayerInventory inventory = ResolveInventory(other);
        if (inventory == null)
        {
            Debug.LogWarning($"[BlockDrop] Colisão com {other.name} mas PlayerInventory nao encontrado para coletar {blockType}.");
            return;
        }

        if (requirePlayerTag && !string.IsNullOrEmpty(playerTag))
        {
            bool isPlayer = other.CompareTag(playerTag) ||
                            (other.transform.root != null && other.transform.root.CompareTag(playerTag)) ||
                            inventory.CompareTag(playerTag);
            if (!isPlayer) return;
        }

        if (inventory.TryAddBlockDrop(blockType, 1))
        {
            if (debugPickupLogs)
                Debug.Log($"[BlockDrop] Coletado: {blockType} -> inventario {inventory.name}");
            Destroy(gameObject);
        }
        else if (debugPickupLogs)
        {
            Debug.LogWarning($"[BlockDrop] Falha ao coletar {blockType}. Verifique mapeamento BlockType->Item e espaco no inventario.");
        }
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

        Transform root = other.transform.root;
        if (root != null)
        {
            inventory = root.GetComponent<PlayerInventory>();
            if (inventory != null) return inventory;

            inventory = root.GetComponentInChildren<PlayerInventory>();
            if (inventory != null) return inventory;
        }

        return Object.FindObjectOfType<PlayerInventory>();
    }
}
