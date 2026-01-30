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
    }

    public void SetMaterials(Material[] mats)  // MODIFICAÇÃO: Nova função (substitui SetMaterial)
    {
        materials = mats;
        if (meshRenderer != null)
            meshRenderer.sharedMaterials = mats;
    }


    public void ApplyMeshData(NativeArray<Vector3> vertices, NativeArray<int> opaqueTris, NativeArray<int> waterTris, NativeArray<Vector2> uvs, NativeArray<Vector3> normals, NativeArray<byte> vertexLights)
    {
        // Render mesh (mesma lógica de antes)
        mesh.Clear(false);

        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);

        if (normals.Length > 0)
            mesh.SetNormals(normals);
        else
            mesh.RecalculateNormals();

        mesh.subMeshCount = 2;
        mesh.SetIndices(opaqueTris, MeshTopology.Triangles, 0, false);
        mesh.SetIndices(waterTris, MeshTopology.Triangles, 1, false);

        // Aplicar vertex color a partir dos bytes (0..15) com Face Shading
        if (vertexLights.Length == vertices.Length)
        {
            Color[] cols = new Color[vertices.Length];

            const float ambientMin = 0.1f; // ajuste global de ambiência (0.1 - 0.25)
            const float shadeTop = 1.00f;
            const float shadeSide = 0.25f;
            const float shadeBottom = 0.60f;

            bool haveNormalsPerVertex = (normals.Length == vertices.Length);

            for (int i = 0; i < vertices.Length; i++)
            {
                float raw = vertexLights[i] / 15f;
                float l = Mathf.Lerp(ambientMin, 1f, raw);

                // Determina tipo de face a partir da normal do vértice
                float faceShade = 1f;
                if (haveNormalsPerVertex)
                {
                    Vector3 n = normals[i]; // normal por vértice (de MeshGenerator)
                                            // normal deve ser eixo principal (0/±1); usamos thresholds simples
                    if (Mathf.Abs(n.y) > 0.5f)
                    {
                        // cima ou baixo
                        faceShade = (n.y > 0f) ? shadeTop : shadeBottom;
                    }
                    else
                    {
                        // laterais (x/z)
                        faceShade = shadeSide;
                    }
                }
                else
                {
                    // fallback simples — trata tudo como lateral
                    faceShade = shadeSide;
                }

                l *= faceShade;
                l = Mathf.Clamp01(l);
                cols[i] = new Color(l, l, l, 1f);
            }

            mesh.colors = cols;
        }

        mesh.RecalculateBounds();

        // === Atualizar MeshCollider com somente triângulos opacos ===
        // Reutiliza colliderMesh se possível, para reduzir alocações
        if (opaqueTris.Length > 0)
        {
            if (colliderMesh == null)
            {
                colliderMesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                colliderMesh.name = $"ColliderMesh_{gameObject.name}";
            }
            else
            {
                colliderMesh.Clear(false);
            }

            // IMPORTANTE: o collider precisa dos mesmos vértices que o mesh de render.
            // Usamos os mesmos vertices e apenas os índices opacos.
            // Note que isso duplica memória do mesh (render + collider). Se for um problema,
            // podemos gerar um mesh de colisão simplificado (greedy boxes / octree).
            colliderMesh.SetVertices(vertices);
            colliderMesh.SetIndices(opaqueTris, MeshTopology.Triangles, 0, false);
            colliderMesh.RecalculateBounds();

            // Assign collider mesh
            meshCollider.sharedMesh = colliderMesh;
            meshCollider.enabled = true;
        }
        else
        {
            // sem triângulos opacos -> sem colisão
            if (meshCollider != null)
            {
                meshCollider.sharedMesh = null;
                meshCollider.enabled = false;
            }
            if (colliderMesh != null)
            {
                // opcional: destruir mesh de colisão para liberar memória
                Destroy(colliderMesh);
                colliderMesh = null;
            }
        }

        // Observação: não chamamos mesh.UploadMeshData(true) porque precisamos que o mesh seja legível
        // (leitura necessária caso queira criar collider a partir dos vértices).
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

        // opcional: limpar mesh de render se quiser liberar memória (cuidado com instâncias compartilhadas)
        // mesh.Clear(false);
        // meshFilter.sharedMesh = mesh;
    }

    public int generation;
}
