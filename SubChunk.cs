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
    private Mesh colliderMesh;
    private bool hasColliderData = false;

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

        colliderMesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        colliderMesh.MarkDynamic();

        transform.localPosition = Vector3.zero;
    }
    public void ApplyMeshData(
     NativeList<Vector3> vertices,
     NativeList<int> opaqueTris,
     NativeList<int> transparentTris,
     NativeList<int> billboardTris,
     NativeList<int> waterTris,
     NativeList<Vector2> uvs,
     NativeList<Vector2> uv2,
     NativeList<Vector3> normals,
     NativeList<Vector4> extraUVs,
     bool enableBlockColliders)
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
        int totalIndices = opaqueTris.Length + transparentTris.Length + billboardTris.Length + waterTris.Length;
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
        int transparentStart = indexOffset;
        int transparentCount = transparentTris.Length + billboardTris.Length;
        if (transparentTris.Length > 0)
        {
            indexData.Slice(indexOffset, transparentTris.Length).CopyFrom(transparentTris.AsArray());
            indexOffset += transparentTris.Length;
        }
        if (billboardTris.Length > 0)
        {
            indexData.Slice(indexOffset, billboardTris.Length).CopyFrom(billboardTris.AsArray());
            indexOffset += billboardTris.Length;
        }
        meshData.SetSubMesh(1, new SubMeshDescriptor(transparentStart, transparentCount, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);

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

        if (enableBlockColliders && hasSolid)
        {
            int colliderIndicesCount = opaqueTris.Length + transparentTris.Length;
            var colliderDataArray = Mesh.AllocateWritableMeshData(1);
            var colliderData = colliderDataArray[0];

            var colliderVertexAttributes = new NativeArray<VertexAttributeDescriptor>(1, Allocator.Temp);
            colliderVertexAttributes[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
            colliderData.SetVertexBufferParams(vertexCount, colliderVertexAttributes);
            colliderVertexAttributes.Dispose();

            var colliderVertData = colliderData.GetVertexData<Vector3>();
            colliderVertData.CopyFrom(vertices.AsArray());

            colliderData.SetIndexBufferParams(colliderIndicesCount, IndexFormat.UInt32);
            var colliderIndexData = colliderData.GetIndexData<int>();
            int colliderOffset = 0;
            colliderIndexData.Slice(colliderOffset, opaqueTris.Length).CopyFrom(opaqueTris.AsArray());
            colliderOffset += opaqueTris.Length;
            colliderIndexData.Slice(colliderOffset, transparentTris.Length).CopyFrom(transparentTris.AsArray());

            colliderData.subMeshCount = 1;
            colliderData.SetSubMesh(0, new SubMeshDescriptor(0, colliderIndicesCount, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);

            colliderMesh.Clear();
            Mesh.ApplyAndDisposeWritableMeshData(colliderDataArray, colliderMesh,
                MeshUpdateFlags.DontRecalculateBounds |
                MeshUpdateFlags.DontValidateIndices |
                MeshUpdateFlags.DontNotifyMeshUsers);
            colliderMesh.RecalculateBounds();

            // Force recook when geometry changed.
            meshCollider.sharedMesh = null;
            meshCollider.sharedMesh = colliderMesh;
            meshCollider.enabled = true;
            hasColliderData = true;
        }
        else
        {
            // Keep any existing collider mesh cached, but disable physics when system is off.
            if (!hasSolid)
            {
                meshCollider.sharedMesh = null;
                hasColliderData = false;
            }
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

    public void SetColliderSystemEnabled(bool enabled)
    {
        if (meshCollider == null) return;

        if (!enabled)
        {
            meshCollider.enabled = false;
            return;
        }

        meshCollider.enabled = hasGeometry && hasColliderData;
    }

    public void ClearMesh()
    {
        if (mesh != null) mesh.Clear();
        if (colliderMesh != null) colliderMesh.Clear();
        hasGeometry = false;
        hasColliderData = false;
        if (meshRenderer != null) meshRenderer.enabled = false;
        if (meshCollider != null)
        {
            meshCollider.sharedMesh = null;
            meshCollider.enabled = false;
        }
        gameObject.SetActive(false);
    }
}
