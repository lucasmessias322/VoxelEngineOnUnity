using UnityEngine;
using Unity.Collections;
using System.Collections.Generic;


public class Chunk : MonoBehaviour
{
    public const int SizeX = 16;
    public const int SizeY = 384;
    public const int SizeZ = 16;
    public NativeArray<byte> voxelData;
    public const int SubchunkHeight = 256;
    public const int SubchunksPerColumn = SizeY / SubchunkHeight; // Resulta em 6

    [HideInInspector] // Impede que a Unity serialize isso incorretamente no Prefab
    public Subchunk[] subchunks;
    [HideInInspector] public Bounds worldBounds;
    public bool hasVoxelData = false;

    [HideInInspector] public MeshRenderer[] subRenderers;

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


    private void Awake()
    {

        int total = Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ;

        voxelData = new NativeArray<byte>(total, Allocator.Persistent);

        hasVoxelData = false; // ainda útil para saber se já tem dados válidos
    }
    private void OnDestroy()
    {
        if (jobScheduled && currentJob.IsCompleted == false)
        {
            currentJob.Complete();
        }


        // Segurança extra para pool
        if (voxelData.IsCreated) voxelData.Dispose();

    }

    public void InitializeSubchunks(Material[] materials)
    {
        // Cria os subchunks apenas na primeira vez (pooling)
        if (subchunks == null || subchunks.Length != SubchunksPerColumn)
        {
            subchunks = new Subchunk[SubchunksPerColumn];
            subRenderers = new MeshRenderer[SubchunksPerColumn];   // ← corrigido e ativado

            for (int i = 0; i < SubchunksPerColumn; i++)
            {
                GameObject subObj = new GameObject($"Subchunk_{i}");
                subObj.transform.SetParent(this.transform, false); // false = mantém posição world
                subObj.transform.localPosition = Vector3.zero;

                Subchunk sc = subObj.AddComponent<Subchunk>();
                sc.Initialize(materials, i);

                subchunks[i] = sc;
                subRenderers[i] = sc.meshRenderer;   // cache correto agora

                subObj.SetActive(true);
            }
        }

        // SEMPRE atualiza os bounds (CRÍTICO para pooling!)
        UpdateWorldBounds();
    }

    public void UpdateWorldBounds()
    {
        worldBounds = new Bounds(
            transform.position + new Vector3(8f, 192f, 8f),
            new Vector3(16f, 384f, 16f)
        );
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
