using UnityEngine;
using Unity.Collections;
using System.Collections.Generic;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class Chunk : MonoBehaviour
{
    public const int SizeX = 16;
    public const int SizeY = 384;
    public const int SizeZ = 16;
    public NativeArray<byte> voxelData; // ou BlockType se preferir enum
    public bool hasVoxelData = false;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh mesh; // reuso

    // Mesh usado exclusivamente para colisão (contém somente triângulos opacos)
    private Mesh colliderMesh;
    private MeshCollider meshCollider;

    [SerializeField] private Material[] materials;  // MODIFICAÇÃO: Nova
    public enum ChunkState
    {
        Requested,   // job agendado
        MeshReady,   // resultado chegou
        Active       // mesh aplicado
    }

    public ChunkState state;
    public NativeArray<BlockType> chunkBlocks;
    public NativeArray<byte> chunkLight; // combined light (max skylight, blocklight)
    private void Awake()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();

        mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.MarkDynamic();
        meshFilter.sharedMesh = mesh;

        // Obter ou criar MeshCollider
        meshCollider = GetComponent<MeshCollider>();
        if (meshCollider == null)
        {
            meshCollider = gameObject.AddComponent<MeshCollider>();
        }
        meshCollider.sharedMesh = null; // sem colisão até gerar mesh
        meshCollider.convex = false; // deve ser não-convexo para terrenos
                                     // opcionais: ajustar meshCollider.cookingOptions se necessário

        int total = Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ;
        chunkBlocks = new NativeArray<BlockType>(total, Allocator.Persistent);
        chunkLight = new NativeArray<byte>(total, Allocator.Persistent);

        // ← ADICIONE ESTA LINHA
        voxelData = new NativeArray<byte>(total, Allocator.Persistent);

        hasVoxelData = false; // ainda útil para saber se já tem dados válidos
    }
    private void OnDestroy()
    {
        if (chunkBlocks.IsCreated) chunkBlocks.Dispose();
        if (chunkLight.IsCreated) chunkLight.Dispose();
        // Segurança extra para pool
        if (voxelData.IsCreated) voxelData.Dispose();

    }


    public void SetMaterials(Material[] mats)  // MODIFICAÇÃO: Nova função (substitui SetMaterial)
    {
        materials = mats;
        if (meshRenderer != null)
            meshRenderer.sharedMaterials = mats;
    }

    public void ApplyMeshData(
        NativeList<Vector3> vertices,
        NativeList<int> opaqueTris,
        NativeList<int> transparentTris,
        NativeList<int> waterTris,
        NativeList<Vector2> uvs,
        NativeList<Vector2> uv2,
        NativeList<Vector3> normals,
        NativeList<byte> vertexLights,
        NativeList<byte> tintFlags
    )
    {
        // 1. Limpar e Configurar Mesh de Renderização
        mesh.Clear();

        // Passar NativeArrays diretamente evita alocações de GC
        mesh.SetVertices(vertices.AsArray());
        mesh.SetUVs(0, uvs.AsArray());
        mesh.SetUVs(1, uv2.AsArray());
        mesh.SetNormals(normals.AsArray());

        // 2. Cálculo de Cores OTIMIZADO (Sem alocações)
        int vertexCount = vertices.Length;
        /// var colors = new NativeArray<Color>(vertexCount, Allocator.Temp);

        Color grassTint = World.Instance.grassTintBase;

        // for (int i = 0; i < vertexCount; i++)
        // {
        //     float raw = vertexLights[i] / 15f;
        //     float l = Mathf.Lerp(0.15f, 1f, raw);

        //     // Simplificação do Shading
        //     Vector3 n = normals[i];
        //     float faceShade = (Mathf.Abs(n.y) > 0.5f) ? ((n.y > 0) ? 1.0f : 0.6f) : 0.5f;
        //     l = Mathf.Clamp01(l * faceShade);

        //     if (tintFlags[i] == 1)
        //     {
        //         Color tinted = grassTint * l;
        //         tinted.r = Mathf.Max(tinted.r, grassTint.r * 0.15f);
        //         tinted.g = Mathf.Max(tinted.g, grassTint.g * 0.15f);
        //         tinted.b = Mathf.Max(tinted.b, grassTint.b * 0.15f);
        //         tinted.a = 1f;
        //         colors[i] = tinted;
        //     }
        //     else
        //     {
        //         colors[i] = new Color(l, l, l, 1f);
        //     }
        // }
        // mesh.SetColors(colors);
        // colors.Dispose(); // Libera memória Temp

        var extraUV = new NativeList<Vector4>(vertexCount, Allocator.Temp);

        for (int i = 0; i < vertexCount; i++)
        {
            float raw = vertexLights[i] / 15f; // normalizado 0..1
            float tint = tintFlags[i];         // 0 ou 1
            extraUV.Add(new Vector4(raw, tint, 0f, 0f)); // guardamos em UV channel 2
        }

        // passar para o mesh no canal UV 2 (terceiro UV)
        mesh.SetUVs(2, extraUV.AsArray());
        extraUV.Dispose();

        // 3. Submeshes
        mesh.subMeshCount = 3;
        mesh.SetIndices(opaqueTris.AsArray(), MeshTopology.Triangles, 0, false);
        mesh.SetIndices(transparentTris.AsArray(), MeshTopology.Triangles, 1, false);
        mesh.SetIndices(waterTris.AsArray(), MeshTopology.Triangles, 2, false);

        mesh.RecalculateBounds();
        mesh.UploadMeshData(false);

        // 4. Collider OTIMIZADO (A CORREÇÃO DO ERRO ESTÁ AQUI)
        int solidCount = opaqueTris.Length + transparentTris.Length;

        if (solidCount > 0)
        {
            if (colliderMesh == null) colliderMesh = new Mesh();
            else colliderMesh.Clear();

            colliderMesh.SetVertices(vertices.AsArray());

            // Aloca array combinado
            var colliderIndices = new NativeArray<int>(solidCount, Allocator.Temp);

            // --- CORREÇÃO: Verificações de tamanho antes de copiar ---

            // Copia Opacos
            if (opaqueTris.Length > 0)
            {
                NativeArray<int>.Copy(opaqueTris.AsArray(), 0, colliderIndices, 0, opaqueTris.Length);
            }

            // Copia Transparentes (Só executa se houver triângulos transparentes)
            if (transparentTris.Length > 0)
            {
                // O índice de destino é exatamente onde os opacos terminaram
                NativeArray<int>.Copy(transparentTris.AsArray(), 0, colliderIndices, opaqueTris.Length, transparentTris.Length);
            }

            colliderMesh.SetIndices(colliderIndices, MeshTopology.Triangles, 0, false);
            colliderIndices.Dispose(); // Libera memória

            meshCollider.sharedMesh = null;
            meshCollider.sharedMesh = colliderMesh;
            meshCollider.enabled = true;
        }
        else
        {
            meshCollider.enabled = false;
        }
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

        // limpar collider
        if (meshCollider != null)
        {
            meshCollider.sharedMesh = null;
            meshCollider.enabled = false;
        }
        if (colliderMesh != null)
        {
            Destroy(colliderMesh);
            colliderMesh = null;
        }

        // Opcional: apenas marque que não tem dados válidos
        hasVoxelData = false;
    }

    public int generation;
}
