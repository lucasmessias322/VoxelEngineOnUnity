using Unity.Collections;
using UnityEngine;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
[RequireComponent(typeof(MeshCollider))]

public class Subchunk : MonoBehaviour
{
    private MeshFilter meshFilter;
   [HideInInspector] public MeshRenderer meshRenderer;
    private MeshCollider meshCollider;
    private Mesh mesh;

    // ==================== NOVO PARA CULLING ====================
    [HideInInspector]
    public bool hasGeometry = false;           // sabemos se tem mesh sólida/transparente

    public void Initialize(Material[] materials, int subchunkIndex)
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        meshRenderer.materials = materials;

        meshCollider = gameObject.GetComponent<MeshCollider>() ?? gameObject.AddComponent<MeshCollider>();
        meshCollider.convex = false;

        mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.MarkDynamic();
        meshFilter.sharedMesh = mesh;

        transform.localPosition = Vector3.zero;
    }

    public void ApplyMeshData(
        NativeList<Vector3> vertices,
        NativeList<int> opaqueTris,
        NativeList<int> transparentTris,
        NativeList<int> waterTris,
        NativeList<Vector2> uvs,
        NativeList<Vector2> uv2,
        NativeList<Vector3> normals,
        NativeList<Vector4> extraUVs)
    {
        mesh.Clear();

        if (vertices.Length == 0)
        {
            hasGeometry = false;
            gameObject.SetActive(false);           // vazio → desativa tudo (sem collider)
            return;
        }

        // === PREENCHE O MESH ===
        mesh.SetVertices(vertices.AsArray());
        mesh.SetNormals(normals.AsArray());
        mesh.SetUVs(0, uvs.AsArray());
        mesh.SetUVs(1, uv2.AsArray());
        mesh.SetUVs(2, extraUVs.AsArray());

        mesh.subMeshCount = 3;
        mesh.SetIndices(opaqueTris.AsArray(), MeshTopology.Triangles, 0, false);
        mesh.SetIndices(transparentTris.AsArray(), MeshTopology.Triangles, 1, false);
        mesh.SetIndices(waterTris.AsArray(), MeshTopology.Triangles, 2, false);

        mesh.RecalculateBounds();

        bool hasSolid = opaqueTris.Length > 0 || transparentTris.Length > 0;

        hasGeometry = true;
        gameObject.SetActive(true);
        meshRenderer.enabled = true;               // o culling vai controlar depois

        if (hasSolid)
        {
            meshCollider.sharedMesh = mesh;
            meshCollider.enabled = true;
        }
        else
        {
            meshCollider.sharedMesh = null;
            meshCollider.enabled = false;
        }
    }

    // ==================== MÉTODO DE CULLING ====================
    public void UpdateVisibility(Plane[] frustumPlanes)
    {
        if (!hasGeometry || meshRenderer == null) return;

        // Bounds do MeshRenderer já é em world space (subchunk está em localPosition = 0)
        meshRenderer.enabled = GeometryUtility.TestPlanesAABB(frustumPlanes, meshRenderer.bounds);
    }

    public void SetVisible(bool visible)
    {
        if (meshRenderer == null) return;

        // Só muda se for diferente (evita custo desnecessário do Unity)
        if (meshRenderer.enabled != visible)
            meshRenderer.enabled = visible;
    }

    public void ClearMesh()
    {
        if (mesh != null) mesh.Clear();
        hasGeometry = false;
        if (meshRenderer != null) meshRenderer.enabled = false;
        if (meshCollider != null)
        {
            meshCollider.sharedMesh = null;
            meshCollider.enabled = false;
        }
        gameObject.SetActive(false);
    }
}