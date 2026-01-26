using UnityEngine;
using Unity.Collections;

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class Chunk : MonoBehaviour
{
    public const int SizeX = 16;
    public const int SizeY = 256;
    public const int SizeZ = 16;
    public NativeArray<byte> skylight; // tamanho: voxelSizeX * SizeY * voxelSizeZ
    public bool HasSkylight => skylight.IsCreated;

    public int surfaceSubY;

    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh mesh; // reuso
    public const int SubChunkSize = 16;
    public const int SubChunkCountY = Chunk.SizeY / SubChunkSize; // 256 / 16 = 16

    public SubChunk[] subChunks = new SubChunk[SubChunkCountY];

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

        // Inicializar subchunks (child gameobjects com MeshFilter/Renderer)
        for (int i = 0; i < SubChunkCountY; i++)
        {
            var go = new GameObject($"SubChunk_{i}");
            go.transform.SetParent(transform, false);
            // keep local position zero — vertices estão em espaço do chunk
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();

            Mesh m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.MarkDynamic();
            mf.sharedMesh = m;

            if (materials != null && materials.Length > 0)
                mr.sharedMaterials = materials;

            subChunks[i] = new SubChunk
            {
                yIndex = i,
                mesh = m,
                filter = mf,
                renderer = mr,
                isEmpty = true,
                bounds = new Bounds(
         new Vector3(
             SizeX * 0.5f,
             i * SubChunkSize + SubChunkSize * 0.5f,
             SizeZ * 0.5f
         ),
         new Vector3(SizeX, SubChunkSize, SizeZ)
     )
            };

        }
    }

    public void SetMaterials(Material[] mats)  // MODIFICAÇÃO: Nova função (substitui SetMaterial)
    {
        materials = mats;
        if (meshRenderer != null)
            meshRenderer.sharedMaterials = mats;

        // aplicar para subchunks existentes
        if (subChunks != null)
        {
            foreach (var sc in subChunks)
            {
                if (sc != null && sc.renderer != null)
                    sc.renderer.sharedMaterials = mats;
            }
        }
    }


    public void ApplyMeshData(NativeArray<Vector3> vertices, NativeArray<int> opaqueTris, NativeArray<int> waterTris, NativeArray<Vector2> uvs, NativeArray<Vector3> normals, NativeArray<byte> vertexLights)
    {
        // Mantido para compatibilidade (mesh único)
        mesh.Clear(false);

        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);

        if (normals.Length > 0)
            mesh.SetNormals(normals);

        mesh.subMeshCount = 2;
        mesh.SetIndices(opaqueTris, MeshTopology.Triangles, 0, false);
        mesh.SetIndices(waterTris, MeshTopology.Triangles, 1, false);

        // Aplicar vertex color a partir dos bytes (0..15) com Face Shading
        if (vertexLights.Length == vertices.Length)
        {
            Color[] cols = new Color[vertices.Length];

            const float ambientMin = 0.15f; // ajuste global de ambiência (0.1 - 0.25)
                                            // constantes de shading por face (ajuste se quiser)
            const float shadeTop = 1.00f;
            const float shadeBottom = 0.50f;

            // diferencia X/Z como no Java
            const float shadeZ = 0.50f; // north/south
            const float shadeX = 0.25f; // east/west


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
                        faceShade = (n.y > 0f) ? shadeTop : shadeBottom;
                    }
                    else if (Mathf.Abs(n.z) > 0.5f)
                    {
                        faceShade = shadeZ;
                    }
                    else
                    {
                        faceShade = shadeX;
                    }

                }
                else
                {
                    faceShade = shadeX;
                }

                l *= faceShade;
                l = Mathf.Clamp01(l);
                cols[i] = new Color(l, l, l, 1f);
            }

            mesh.colors = cols;
        }

        mesh.RecalculateBounds();
    }

    // Novo: aplica mesh para um subchunk específico (arrays gerenciados)
    public void ApplySubChunkMeshData(int subIndex, Vector3[] verts, int[] opaqueTris, int[] waterTris, Vector2[] uvsArr, Vector3[] normalsArr, byte[] vertexLightsArr)
    {
        if (subIndex < 0 || subIndex >= SubChunkCountY) return;
        var sc = subChunks[subIndex];
        if (sc == null) return;

        if (verts == null || verts.Length == 0)
        {
            // marca vazio e desligar renderer
            sc.isEmpty = true;
            if (sc.renderer != null) sc.renderer.enabled = false;
            // limpar mesh
            if (sc.mesh != null) sc.mesh.Clear(false);
            return;
        }

        sc.isEmpty = false;

        Mesh m = sc.mesh;
        m.Clear(false);
        m.SetVertices(verts);

        if (uvsArr != null && uvsArr.Length == verts.Length)
            m.SetUVs(0, uvsArr);

        if (normalsArr != null && normalsArr.Length == verts.Length)
            m.SetNormals(normalsArr);

        m.subMeshCount = 2;
        if (opaqueTris != null && opaqueTris.Length > 0)
            m.SetIndices(opaqueTris, MeshTopology.Triangles, 0, false);
        else
            m.SetIndices(new int[0], MeshTopology.Triangles, 0, false);

        if (waterTris != null && waterTris.Length > 0)
            m.SetIndices(waterTris, MeshTopology.Triangles, 1, false);
        else
            m.SetIndices(new int[0], MeshTopology.Triangles, 1, false);

        // colors
        if (vertexLightsArr != null && vertexLightsArr.Length == verts.Length)
        {
            Color[] cols = new Color[verts.Length];
            const float ambientMin = 0.15f;
            const float shadeTop = 1f; const float shadeBottom = 0.5f;
            const float shadeZ = 0.5f; const float shadeX = 0.25f;

            bool haveNormalsPerVertex = (normalsArr != null && normalsArr.Length == verts.Length);

            for (int i = 0; i < verts.Length; i++)
            {
                float raw = vertexLightsArr[i] / 15f;
                float l = Mathf.Lerp(ambientMin, 1f, raw);
                float faceShade = haveNormalsPerVertex ? (Mathf.Abs(normalsArr[i].y) > 0.5f ? (normalsArr[i].y > 0f ? shadeTop : shadeBottom) : (Mathf.Abs(normalsArr[i].z) > 0.5f ? shadeZ : shadeX)) : shadeX;
                l *= faceShade;
                l = Mathf.Clamp01(l);
                cols[i] = new Color(l, l, l, 1f);
            }

            m.colors = cols;
        }

        m.RecalculateBounds();

        if (sc.filter != null) sc.filter.sharedMesh = m;
        if (sc.renderer != null)
        {
            sc.renderer.enabled = true;
            if (materials != null && materials.Length > 0) sc.renderer.sharedMaterials = materials;
        }
    }

    // Opcional: update culling baseado na câmera

    // public void UpdateSubchunkVisibility(
    //     int playerSubY,
    //     int up,
    //     int down,
    //     Plane[] planes
    // )
    // {
    //     for (int i = 0; i < SubChunkCountY; i++)
    //     {
    //         var sc = subChunks[i];

    //         if (sc == null || sc.isEmpty)
    //         {
    //             sc.renderer.enabled = false;
    //             continue;
    //         }

    //         // 🔥 CORTE VERTICAL BEDROCK
    //         if (i < playerSubY - down || i > playerSubY + up)
    //         {
    //             sc.renderer.enabled = false;
    //             continue;
    //         }

    //         // frustum opcional (Bedrock usa, mas depois do corte)
    //         Bounds worldBounds = sc.bounds;
    //         worldBounds.center = transform.TransformPoint(sc.bounds.center);

    //         sc.renderer.enabled =
    //             GeometryUtility.TestPlanesAABB(planes, worldBounds);
    //     }
    // }

    public void UpdateSubchunkVisibilityBedrock(
      int playerSubY,
      int surfaceSubY,
      int up,
      int down,
      Plane[] planes
  )
    {
        // 🔥 centro dinâmico (igual Bedrock)
        int centerY = playerSubY < surfaceSubY
            ? playerSubY
            : surfaceSubY;

        for (int i = 0; i < SubChunkCountY; i++)
        {
            var sc = subChunks[i];

            if (sc == null || sc.isEmpty)
            {
                sc.renderer.enabled = false;
                continue;
            }

            // corte vertical
            if (i < centerY - down || i > centerY + up)
            {
                sc.renderer.enabled = false;
                continue;
            }

            // frustum depois
            Bounds worldBounds = sc.bounds;
            worldBounds.center = transform.TransformPoint(sc.bounds.center);

            sc.renderer.enabled =
                GeometryUtility.TestPlanesAABB(planes, worldBounds);
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
        // opcional: limpar coord
        // coord = new Vector2Int(int.MinValue, int.MinValue);

        // desligar subchunks
        if (subChunks != null)
        {
            foreach (var sc in subChunks)
            {
                if (sc != null && sc.renderer != null)
                    sc.renderer.enabled = false;
                if (sc != null) sc.isEmpty = true;
            }
        }
    }

    public int generation;
}
public class SubChunk
{
    public int yIndex; // 0..15
    public Mesh mesh;
    public MeshRenderer renderer;
    public MeshFilter filter;
    public bool isEmpty;

    public Bounds bounds;
}
