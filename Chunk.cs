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
                                        // NOVAS CONSTANTES PARA OS SUBCHUNKS
    public const int SubchunkHeight = 64;
    public const int SubchunksPerColumn = SizeY / SubchunkHeight; // Resulta em 6

    [HideInInspector] // Impede que a Unity serialize isso incorretamente no Prefab
    public Subchunk[] subchunks;
    public bool hasVoxelData = false;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh mesh; // reuso

    // Mesh usado exclusivamente para colisão (contém somente triângulos opacos)
    private Mesh colliderMesh;
    private MeshCollider meshCollider;

    [SerializeField] private Material[] materials;  // MODIFICAÇÃO: Nova
                                                    // Controle de Job ativo para este chunk
    public Unity.Jobs.JobHandle currentJob;
    public bool jobScheduled;


    public enum ChunkState
    {
        Requested,   // job agendado
        MeshReady,   // resultado chegou
        Active,       // mesh aplicado
        Inactive
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
        if (jobScheduled && currentJob.IsCompleted == false)
        {
            currentJob.Complete();
        }
        if (chunkBlocks.IsCreated) chunkBlocks.Dispose();
        if (chunkLight.IsCreated) chunkLight.Dispose();
        // Segurança extra para pool
        if (voxelData.IsCreated) voxelData.Dispose();

    }

    public void InitializeSubchunks(Material[] materials)
    {
        // Verifica se é nulo OU se a Unity o carregou como um array vazio de tamanho 0
        if (subchunks != null && subchunks.Length == SubchunksPerColumn)
            return;

        subchunks = new Subchunk[SubchunksPerColumn];
        for (int i = 0; i < SubchunksPerColumn; i++)
        {
            GameObject subObj = new GameObject($"Subchunk_{i}");
            subObj.transform.SetParent(this.transform);
            subObj.transform.localPosition = Vector3.zero;

            Subchunk sc = subObj.AddComponent<Subchunk>();
            sc.Initialize(materials, i);
            subchunks[i] = sc;

            subObj.SetActive(true);
        }
    }

    public void SetMaterials(Material[] mats)  // MODIFICAÇÃO: Nova função (substitui SetMaterial)
    {
        materials = mats;
        if (meshRenderer != null)
            meshRenderer.sharedMaterials = mats;
    }


    public Vector2Int coord;
    public void SetCoord(Vector2Int c)
    {
        coord = c;
        gameObject.name = $"Chunk_{c.x}_{c.y}";
    }
    public void ResetChunk()
    {
        if (jobScheduled && !currentJob.IsCompleted)
            currentJob.Complete();

        jobScheduled = false;
        state = ChunkState.Inactive;
        generation = -1;
        hasVoxelData = false;

        if (mesh != null) mesh.Clear();
        if (meshCollider != null)
        {
            meshCollider.sharedMesh = null;
            meshCollider.enabled = false;
        }
        if (colliderMesh != null) colliderMesh.Clear();

        if (subchunks != null)
        {
            foreach (var sc in subchunks)
            {
                if (sc != null)
                {
                    sc.ClearMesh();
                    sc.gameObject.SetActive(false);
                }
            }
        }

        gameObject.SetActive(false);
    }
    public int generation;
}
