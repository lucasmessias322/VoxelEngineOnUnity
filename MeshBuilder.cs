using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

public class MeshBuilder : MonoBehaviour
{
    public static MeshBuilder Instance { get; private set; }

    // Quantos resultados processar por frame (tweakável)
    [Tooltip("Máximo de resultados de meshing processados por frame")]
    public int maxResultsPerFrame = 6;

    private readonly ConcurrentQueue<MeshJobResult> queue = new();

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // Thread-safe: pode ser chamado a partir de tasks/jobs
    public void QueueResult(MeshJobResult r)
    {
        if (r == null) return;
        queue.Enqueue(r);
    }

    void Update()
    {
        if (VoxelWorld.Instance == null) return;

        int processed = 0;
        while (processed < maxResultsPerFrame && queue.TryDequeue(out var res))
        {
            try
            {
                ProcessResult(res);
            }
            catch (Exception ex)
            {
                Debug.LogError($"MeshBuilder: erro processando chunk {res.coord}: {ex}");
            }
            processed++;
        }
    }

    private void ProcessResult(MeshJobResult res)
    {
        // Obter/instanciar chunk (VoxelWorld fornece helper)
        var vw = VoxelWorld.Instance;
        var chunk = vw.GetOrCreateChunk(res.coord); // ADICIONAR esse método em VoxelWorld

        // set blocks interno
        chunk.SetBlocks(res.blocks);

        // --- Agrupar faces por material (código adaptado de ProcessMeshResults) ---
        var faceVerts = res.solidVertices ?? new List<Vector3>();
        var faceBlockTypes = res.solidFaceBlockTypes ?? new List<int>();
        var faceNormals = res.solidFaceNormals ?? new List<int>();
        int faceCount = faceBlockTypes.Count;

        var vertsByMat = new List<List<Vector3>>();
        var trisByMat = new List<List<int>>();
        var uvsByMat = new List<List<Vector2>>();
        var normsByMat = new List<List<Vector3>>();

        void EnsureMaterialSlot(int idx)
        {
            while (vertsByMat.Count <= idx)
            {
                vertsByMat.Add(new List<Vector3>());
                trisByMat.Add(new List<int>());
                uvsByMat.Add(new List<Vector2>());
                normsByMat.Add(new List<Vector3>());
            }
        }

        // garantir caches do VoxelWorld (expor EnsureBlockTypeCaches como público)
        vw.EnsureBlockTypeCaches();

        int GetMaterialIndexForBlockType(int blockTypeInt)
        {
            if (blockTypeInt < 0) return 0;
            var arr = vw.GetCachedMaterialIndexByType(); // ADICIONAR getter em VoxelWorld
            if (arr == null) return 0;
            if (blockTypeInt >= arr.Length) return 0;
            return arr[blockTypeInt];
        }

        for (int fi = 0; fi < faceCount; fi++)
        {
            int matIndex = GetMaterialIndexForBlockType(faceBlockTypes[fi]);
            EnsureMaterialSlot(matIndex);

            var vlist = vertsByMat[matIndex];
            var tris = trisByMat[matIndex];
            var uvs = uvsByMat[matIndex];
            var norms = normsByMat[matIndex];

            int baseVertIndex = fi * 4;
            // adicionar 4 vértices
            vlist.Add(faceVerts[baseVertIndex + 0]);
            vlist.Add(faceVerts[baseVertIndex + 1]);
            vlist.Add(faceVerts[baseVertIndex + 2]);
            vlist.Add(faceVerts[baseVertIndex + 3]);

            int vi = vlist.Count - 4;
            tris.Add(vi + 0); tris.Add(vi + 1); tris.Add(vi + 2);
            tris.Add(vi + 0); tris.Add(vi + 2); tris.Add(vi + 3);

            // UVs
            var bt = (BlockType)faceBlockTypes[fi];
            var normal = NormalFromIndex(faceNormals[fi]);
            AddUVsTo(uvs, vw.blockDataSO, vw.atlasOrientation, bt, normal);

            // Normais
            norms.Add(normal); norms.Add(normal); norms.Add(normal); norms.Add(normal);
        }

        // montar finalMaterials (usa vw.materials se definido)
        Material[] finalMaterials = null;
        if (vw.materials != null && vw.materials.Length > 0)
        {
            finalMaterials = vw.materials;
        }
        else
        {
            var matsList = new List<Material>();
            if (vw.chunkMaterial != null) matsList.Add(vw.chunkMaterial);
            if (vw.leafMaterial != null) matsList.Add(vw.leafMaterial);
            if (vw.waterMaterial != null) matsList.Add(vw.waterMaterial);
            finalMaterials = matsList.ToArray();
        }

        // Aplicar no chunk (Chunk.ApplyMeshDataByMaterial já existente)
        chunk.ApplyMeshDataByMaterial(
            vertsByMat,
            trisByMat,
            uvsByMat,
            normsByMat,
            finalMaterials,
            res.width, res.height, res.depth, res.blockSize
        );

        // Aplicar MaterialPropertyBlocks (foliage/biome)
        try
        {
            int chunkWorldX = res.coord.x * vw.chunkWidth + (vw.chunkWidth / 2);
            int chunkWorldZ = res.coord.y * vw.chunkDepth + (vw.chunkDepth / 2);
            Color biomeColor = vw.GetFoliageColorAt(chunkWorldX, chunkWorldZ);

            int leafMatIndex = -1;
            int chunkMatIndex = -1;

            if (finalMaterials != null)
            {
                for (int i = 0; i < finalMaterials.Length; i++)
                {
                    if (finalMaterials[i] == vw.leafMaterial) leafMatIndex = i;
                    if (finalMaterials[i] == vw.chunkMaterial) chunkMatIndex = i;
                }
            }

            var renderer = chunk.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                if (leafMatIndex >= 0)
                {
                    var mpbLeaf = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(mpbLeaf, leafMatIndex);
                    mpbLeaf.SetColor("_FoliageColor", biomeColor);
                    renderer.SetPropertyBlock(mpbLeaf, leafMatIndex);
                }

                if (chunkMatIndex >= 0)
                {
                    var mpbChunk = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(mpbChunk, chunkMatIndex);
                    mpbChunk.SetColor("_BiomeColor", biomeColor);
                    renderer.SetPropertyBlock(mpbChunk, chunkMatIndex);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"MeshBuilder: erro ao aplicar MPB: {ex}");
        }
    }

    // converte índice de normal para vector3 (mesma convenção do job)
    private Vector3 NormalFromIndex(int idx)
    {
        return idx switch
        {
            0 => Vector3.forward,
            1 => Vector3.back,
            2 => Vector3.up,
            3 => Vector3.down,
            4 => Vector3.right,
            5 => Vector3.left,
            _ => Vector3.forward
        };
    }

    // versão independente de AddUVsTo (usa BlockDataSO público do VoxelWorld)
    private void AddUVsTo(List<Vector2> uvList, BlockDataSO blockDataSO, VoxelWorld.AtlasOrientation atlasOrientation, BlockType bt, Vector3 normal)
    {
        Vector2Int tile;

        if (blockDataSO != null && blockDataSO.blockTextureDict.TryGetValue(bt, out var mapping))
        {
            tile = normal == Vector3.up ? mapping.top :
                   normal == Vector3.down ? mapping.bottom :
                   mapping.side;
        }
        else
        {
            if (blockDataSO == null)
            {
                tile = new Vector2Int(1, 0);
            }
            else if (blockDataSO.blockTextureDict.TryGetValue(BlockType.Placeholder, out var fallbackMapping))
            {
                tile = normal == Vector3.up ? fallbackMapping.top :
                       normal == Vector3.down ? fallbackMapping.bottom :
                       fallbackMapping.side;
            }
            else
            {
                tile = new Vector2Int(1, 0);
            }
        }

        int tileY = tile.y;
        if (blockDataSO != null && atlasOrientation == VoxelWorld.AtlasOrientation.TopToBottom)
        {
            tileY = (int)blockDataSO.atlasSize.y - 1 - tile.y;
        }

        float invX = 1f;
        float invY = 1f;
        if (blockDataSO != null)
        {
            invX = 1f / blockDataSO.atlasSize.x;
            invY = 1f / blockDataSO.atlasSize.y;
        }

        Vector2 uv00 = new Vector2(tile.x * invX, tileY * invY);
        Vector2 uv10 = new Vector2((tile.x + 1) * invX, tileY * invY);
        Vector2 uv11 = new Vector2((tile.x + 1) * invX, (tileY + 1) * invY);
        Vector2 uv01 = new Vector2(tile.x * invX, (tileY + 1) * invY);

        uvList.Add(uv00);
        uvList.Add(uv10);
        uvList.Add(uv11);
        uvList.Add(uv01);
    }


}
