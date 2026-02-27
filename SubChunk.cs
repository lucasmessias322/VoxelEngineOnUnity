using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
[RequireComponent(typeof(MeshCollider))]

public class Subchunk : MonoBehaviour
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ChunkVertex
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector2 uv0;      // uvs (TEXCOORD0)
        public Vector2 uv1;      // uv2 / atlas (TEXCOORD1)
        public Vector4 uv2;      // extraUVs (light, tint, AO) → TEXCOORD2
    }
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
        if (vertices.Length == 0)
        {
            hasGeometry = false;
            gameObject.SetActive(false);
            return;
        }

        int vertexCount = vertices.Length;

        // ====================== MESH DATA API ======================
        var meshDataArray = Mesh.AllocateWritableMeshData(1);
        var meshData = meshDataArray[0];

        // Vertex layout
        var vertexAttributes = new NativeArray<VertexAttributeDescriptor>(5, Allocator.Temp);
        vertexAttributes[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
        vertexAttributes[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3);
        vertexAttributes[2] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2);
        vertexAttributes[3] = new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2);
        vertexAttributes[4] = new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4);

        meshData.SetVertexBufferParams(vertexCount, vertexAttributes);
        vertexAttributes.Dispose();

        // Copia dos vértices
        var vertData = meshData.GetVertexData<ChunkVertex>();
        for (int i = 0; i < vertexCount; i++)
        {
            vertData[i] = new ChunkVertex
            {
                position = vertices[i],
                normal = normals[i],
                uv0 = uvs[i],
                uv1 = uv2[i],
                uv2 = extraUVs[i]
            };
        }

        // ====================== ÍNDICES ======================
        int totalIndices = opaqueTris.Length + transparentTris.Length + waterTris.Length;
        meshData.SetIndexBufferParams(totalIndices, IndexFormat.UInt32);

        var indexData = meshData.GetIndexData<int>();
        int indexOffset = 0;

        // <<< CORREÇÃO PRINCIPAL >>>
        meshData.subMeshCount = 3;   // SEMPRE definir ANTES de qualquer SetSubMesh

        // SubMesh 0 - Opaque (sempre definido, mesmo se vazio)
        int count = opaqueTris.Length;
        indexData.Slice(indexOffset, count).CopyFrom(opaqueTris.AsArray());
        meshData.SetSubMesh(0, new SubMeshDescriptor(indexOffset, count, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);
        indexOffset += count;

        // SubMesh 1 - Transparent
        count = transparentTris.Length;
        indexData.Slice(indexOffset, count).CopyFrom(transparentTris.AsArray());
        meshData.SetSubMesh(1, new SubMeshDescriptor(indexOffset, count, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);
        indexOffset += count;

        // SubMesh 2 - Water
        count = waterTris.Length;
        indexData.Slice(indexOffset, count).CopyFrom(waterTris.AsArray());
        meshData.SetSubMesh(2, new SubMeshDescriptor(indexOffset, count, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);

        // ====================== APLICA ======================
        mesh.Clear();

        Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh,
            MeshUpdateFlags.DontRecalculateBounds |
            MeshUpdateFlags.DontValidateIndices |
            MeshUpdateFlags.DontNotifyMeshUsers);

        mesh.RecalculateBounds();

        // ====================== FINALIZAÇÃO ======================
        bool hasSolid = opaqueTris.Length > 0 || transparentTris.Length > 0;

        hasGeometry = true;
        gameObject.SetActive(true);
        meshRenderer.enabled = true;

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


    public void SetVisible(bool visible)
    {
        if (meshRenderer == null) return;
        bool shouldShow = visible && hasGeometry;
        if (meshRenderer.enabled != shouldShow)
            meshRenderer.enabled = shouldShow;
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