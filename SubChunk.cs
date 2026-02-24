using UnityEngine;
using Unity.Collections;
using System.Collections.Generic;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class Subchunk : MonoBehaviour
{
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private MeshCollider meshCollider;
    private Mesh mesh;
    private Mesh colMesh; // Se você usa um mesh separado para colisão

    public void Initialize(Material[] materials, int subchunkIndex)
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        meshRenderer.materials = materials;

        meshCollider = gameObject.GetComponent<MeshCollider>();
        if (meshCollider == null) meshCollider = gameObject.AddComponent<MeshCollider>();
        meshCollider.convex = false;

        mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.MarkDynamic();
        meshFilter.sharedMesh = mesh;

        // // Opcional: ajustar a posição Y do GameObject filho para ele ficar no lugar certo
        // transform.localPosition = new Vector3(0, subchunkIndex * 64, 0); 
        // Deixe zerado, pois os vértices já vêm com a coordenada Y correta do Job!
        transform.localPosition = Vector3.zero;
    }

    // Adapte este método para receber os mesmos parâmetros que o seu ApplyMeshData original recebia no Chunk.cs
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

        // === FIX PRINCIPAL: SUBCHUNK VAZIO ===
        if (vertices.Length == 0)
        {
            gameObject.SetActive(false);
            meshCollider.enabled = false;
            meshCollider.sharedMesh = null;
            return;
        }

        // Preenche o mesh visual normalmente
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

        // Collider só se tiver geometria sólida
        bool hasSolid = opaqueTris.Length > 0 || transparentTris.Length > 0;

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

        gameObject.SetActive(true);
    }

    public void ClearMesh()
    {
        if (mesh != null) mesh.Clear();

        if (meshCollider != null)
        {
            meshCollider.sharedMesh = null;
            meshCollider.enabled = false;
        }

        gameObject.SetActive(false);   // opcional, mas recomendado
    }
}