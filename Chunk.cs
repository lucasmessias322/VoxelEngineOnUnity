using UnityEngine;
using Unity.Collections;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class Chunk : MonoBehaviour
{
    public const int SizeX = 16;
    public const int SizeY = 256;
    public const int SizeZ = 16;

    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh mesh; // reuso

    [SerializeField] private Material[] materials;  // MODIFICAÇÃO: Nova
    public enum ChunkState
    {
        Requested,   // job agendado
        MeshReady,   // resultado chegou
        Active       // mesh aplicado
    }

    public ChunkState state;

    private void Awake()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();

        mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.MarkDynamic();
        meshFilter.sharedMesh = mesh;
    }

    public void SetMaterials(Material[] mats)  // MODIFICAÇÃO: Nova função (substitui SetMaterial)
    {
        materials = mats;
        if (meshRenderer != null)
            meshRenderer.sharedMaterials = mats;
    }

    public void ApplyMeshData(NativeArray<Vector3> vertices, NativeArray<int> opaqueTris, NativeArray<int> waterTris, NativeArray<Vector2> uvs, NativeArray<Vector3> normals)  // Atualizado para NativeArray
    {
        mesh.Clear(false);

        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);

        if (normals.Length > 0)
            mesh.SetNormals(normals);
        else
            mesh.RecalculateNormals();

        mesh.subMeshCount = 2;  // MODIFICAÇÃO: Define 2 submeshes
        mesh.SetIndices(opaqueTris, MeshTopology.Triangles, 0, false);
        mesh.SetIndices(waterTris, MeshTopology.Triangles, 1, false);

        mesh.RecalculateBounds();
    }

    public Vector2Int coord;
    public void SetCoord(Vector2Int c)
    {
        coord = c;
        gameObject.name = $"Chunk_{c.x}_{c.y}";
    }
    public void ResetChunk()
    {
        gameObject.SetActive(false);
        generation = 0;
        // opcional: limpar coord
        // coord = new Vector2Int(int.MinValue, int.MinValue);
    }

    public int generation;
}