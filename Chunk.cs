using UnityEngine;
using Unity.Collections;
using System.Collections.Generic;

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


    public void ApplyMeshData(
     NativeArray<Vector3> vertices,
     NativeArray<int> opaqueTris,
     NativeArray<int> transparentTris, // 👈 NOVO
     NativeArray<int> waterTris,
     NativeArray<Vector2> uvs,
     NativeArray<Vector3> normals,
     NativeArray<byte> vertexLights,
     NativeArray<byte> tintFlags
 )

    {
        // Render mesh (mesma lógica de antes)
        mesh.Clear(false);

        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);

        if (normals.Length > 0)
            mesh.SetNormals(normals);
        else
            mesh.RecalculateNormals();

        mesh.subMeshCount = 3;

        // 0 → opacos
        mesh.SetIndices(opaqueTris, MeshTopology.Triangles, 0, false);

        // 1 → transparentes (vidro, folhas)
        mesh.SetIndices(transparentTris, MeshTopology.Triangles, 1, false);

        // 2 → água
        mesh.SetIndices(waterTris, MeshTopology.Triangles, 2, false);


        // Aplicar vertex color a partir dos bytes (0..15) com Face Shading + TINTING
        if (vertexLights.Length == vertices.Length)
        {
            Color[] cols = new Color[vertices.Length];

            const float ambientMin = 0.1f;
            const float shadeTop = 1.00f;
            const float shadeSide = 0.8f;
            const float shadeBottom = 0.60f;

            bool haveNormalsPerVertex = (normals.Length == vertices.Length);
            bool haveTintFlags = (tintFlags.Length == vertices.Length);  // NOVO

            for (int i = 0; i < vertices.Length; i++)
            {
                float raw = vertexLights[i] / 15f;
                float l = Mathf.Lerp(ambientMin, 1f, raw);

                float faceShade = 1f;
                if (haveNormalsPerVertex)
                {
                    Vector3 n = normals[i];
                    if (Mathf.Abs(n.y) > 0.5f)
                        faceShade = (n.y > 0f) ? shadeTop : shadeBottom;
                    else
                        faceShade = shadeSide;
                }
                else
                {
                    faceShade = shadeSide;
                }

                l *= faceShade;
                l = Mathf.Clamp01(l);

                // NOVO: Tinting para topo de grama (vermelho)
                // NOVO: Tinting para topo de grama (multiplicativo com iluminação)
                if (haveTintFlags && tintFlags[i] == 1 && normals.Length > 0)
                {
                    // l já está em 0..1 (depois de aplicar faceShade e clamp)
                    // Multiplicamos a cor base pela iluminação
                    Color tinted = World.Instance.grassTintBase * l;

                    // Opcional: evitar que fique muito escuro / sem vida
                    // tinted = Color.Lerp(tinted, grassTintBase * 0.3f, 0.15f); // leve desaturação em sombra

                    // Ou manter um mínimo de brilho/saturação
                    tinted.r = Mathf.Max(tinted.r, World.Instance.grassTintBase.r * 0.15f);
                    tinted.g = Mathf.Max(tinted.g, World.Instance.grassTintBase.g * 0.15f);
                    tinted.b = Mathf.Max(tinted.b, World.Instance.grassTintBase.b * 0.15f);

                    cols[i] = tinted;
                }
                else
                {
                    // cinza normal (iluminação padrão)
                    cols[i] = new Color(l, l, l, 1f);
                }
            }

            mesh.colors = cols;
        }
        mesh.RecalculateBounds();

        // === Atualizar MeshCollider com somente triângulos opacos ===
        // Reutiliza colliderMesh se possível, para reduzir alocações
      if (opaqueTris.Length > 0 || transparentTris.Length > 0)  // Só se houver triângulos sólidos
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

        // Use os mesmos vértices da mesh de render
        colliderMesh.SetVertices(vertices);

        // Combine opacos + transparentes em um único submesh para colisão
        // (Não precisamos de submeshes no collider; só geometria simples)
        var colliderTris = new List<int>(opaqueTris.Length + transparentTris.Length);
        colliderTris.AddRange(opaqueTris);
        colliderTris.AddRange(transparentTris);  // Inclui transparentes (ex.: folhas, vidro)

        colliderMesh.SetIndices(colliderTris.ToArray(), MeshTopology.Triangles, 0, false);
        colliderMesh.RecalculateBounds();

        // Atribua ao collider
        meshCollider.sharedMesh = colliderMesh;
        meshCollider.enabled = true;
    }
    else
    {
        // Sem triângulos sólidos -> sem colisão
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
