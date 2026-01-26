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


    public void ApplyMeshData(NativeArray<Vector3> vertices, NativeArray<int> opaqueTris, NativeArray<int> waterTris, NativeArray<Vector2> uvs, NativeArray<Vector3> normals, NativeArray<byte> vertexLights)
    {
        mesh.Clear(false);

        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);

        if (normals.Length > 0)
            mesh.SetNormals(normals);
        // else
        //     // mesh.RecalculateNormals();

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