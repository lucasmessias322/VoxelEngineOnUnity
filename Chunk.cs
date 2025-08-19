
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class Chunk : MonoBehaviour
{
    public int width = 16;
    public int height = 128;
    public int depth = 16;
    public float blockSize = 1f;

    public BlockDataSO blockDataSO; // opcional
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    public BlockType[,,] blocks;

    private bool hasColliders = false;

    void Awake()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
    }

    public void SetBlocks(BlockType[,,] newBlocks)
    {
        blocks = newBlocks;
    }

    public void SetBlock(int x, int y, int z, BlockType blockType)
    {
        if (x < 0 || x >= width || y < 0 || y >= height || z < 0 || z >= depth)
            return;

        blocks[x, y, z] = blockType;
        RebuildMesh();
        if (hasColliders)
        {
            ApplyAABBCollidersFromBlocks();
        }
    }

    public BlockType GetBlock(int x, int y, int z)
    {
        if (x < 0 || x >= width || y < 0 || y >= height || z < 0 || z >= depth)
            return BlockType.Air;

        return blocks[x, y, z];
    }

    // // REBUILD: agora gera o mesh localmente a partir de this.blocks
    // public void RebuildMesh()
    // {
    //     if (blocks == null)
    //     {
    //         // nada a fazer — chama ApplyMeshData com nulls para limpar o mesh
    //         ApplyMeshData(null, null, null, null, null, null, null, null,
    //             new Material[] { }, width, height, depth, blockSize);
    //         return;
    //     }

    //     // Listas locais para sólidos e água
    //     var vertsSolid = new List<Vector3>();
    //     var trisSolid = new List<int>();
    //     var uvsSolidFaces = new List<BlockFaceInfo>(); // guardamos blockType + normal por face para UVs

    //     var vertsLeaves = new List<Vector3>();
    //     var trisLeaves = new List<int>();
    //     var uvsLeavesFaces = new List<BlockFaceInfo>();


    //     var vertsWater = new List<Vector3>();
    //     var trisWater = new List<int>();
    //     var uvsWaterFaces = new List<BlockFaceInfo>();

    //     float s = blockSize;

    //     // percorre todos os blocos e adiciona faces expostas (quad por face)
    //     for (int x = 0; x < width; x++)
    //     {
    //         for (int y = 0; y < height; y++)
    //         {
    //             for (int z = 0; z < depth; z++)
    //             {
    //                 BlockType bt = blocks[x, y, z];
    //                 if (bt == BlockType.Air) continue;

    //                 Vector3 basePos = new Vector3(x * s, y * s, z * s);
    //                 bool isWater = (bt == BlockType.Water);

    //                 // var vList = isWater ? vertsWater : vertsSolid;
    //                 // var tList = isWater ? trisWater : trisSolid;
    //                 // var faceInfos = isWater ? uvsWaterFaces : uvsSolidFaces;

    //                 bool isLeaf = (bt == BlockType.Leaves); // supondo que seu enum tenha Leaves

    //                 var vList = isWater ? vertsWater : isLeaf ? vertsLeaves : vertsSolid;
    //                 var tList = isWater ? trisWater : isLeaf ? trisLeaves : trisSolid;
    //                 var faceInfos = isWater ? uvsWaterFaces : isLeaf ? uvsLeavesFaces : uvsSolidFaces;


    //                 // front (+z)
    //                 if (IsNeighborAirOrEmpty(x, y, z + 1, isWater))
    //                 {
    //                     AddQuadNoAlloc(vList, tList,
    //                         basePos + new Vector3(0, 0, s),
    //                         basePos + new Vector3(s, 0, s),
    //                         basePos + new Vector3(s, s, s),
    //                         basePos + new Vector3(0, s, s));
    //                     faceInfos.Add(new BlockFaceInfo { blockType = bt, normal = 0 });
    //                 }

    //                 // back (-z)
    //                 if (IsNeighborAirOrEmpty(x, y, z - 1, isWater))
    //                 {
    //                     AddQuadNoAlloc(vList, tList,
    //                         basePos + new Vector3(s, 0, 0),
    //                         basePos + new Vector3(0, 0, 0),
    //                         basePos + new Vector3(0, s, 0),
    //                         basePos + new Vector3(s, s, 0));
    //                     faceInfos.Add(new BlockFaceInfo { blockType = bt, normal = 1 });
    //                 }

    //                 // top (+y)
    //                 if (IsNeighborAirOrEmpty(x, y + 1, z, isWater))
    //                 {
    //                     AddQuadNoAlloc(vList, tList,
    //                         basePos + new Vector3(0, s, 0),
    //                         basePos + new Vector3(0, s, s),
    //                         basePos + new Vector3(s, s, s),
    //                         basePos + new Vector3(s, s, 0));
    //                     faceInfos.Add(new BlockFaceInfo { blockType = bt, normal = 2 });
    //                 }

    //                 // bottom (-y)
    //                 if (IsNeighborAirOrEmpty(x, y - 1, z, isWater))
    //                 {
    //                     AddQuadNoAlloc(vList, tList,
    //                         basePos + new Vector3(0, 0, 0),
    //                         basePos + new Vector3(s, 0, 0),
    //                         basePos + new Vector3(s, 0, s),
    //                         basePos + new Vector3(0, 0, s));
    //                     faceInfos.Add(new BlockFaceInfo { blockType = bt, normal = 3 });
    //                 }

    //                 // right (+x)
    //                 if (IsNeighborAirOrEmpty(x + 1, y, z, isWater))
    //                 {
    //                     AddQuadNoAlloc(vList, tList,
    //                         basePos + new Vector3(s, 0, s),
    //                         basePos + new Vector3(s, 0, 0),
    //                         basePos + new Vector3(s, s, 0),
    //                         basePos + new Vector3(s, s, s));
    //                     faceInfos.Add(new BlockFaceInfo { blockType = bt, normal = 4 });
    //                 }

    //                 // left (-x)
    //                 if (IsNeighborAirOrEmpty(x - 1, y, z, isWater))
    //                 {
    //                     AddQuadNoAlloc(vList, tList,
    //                         basePos + new Vector3(0, 0, 0),
    //                         basePos + new Vector3(0, 0, s),
    //                         basePos + new Vector3(0, s, s),
    //                         basePos + new Vector3(0, s, 0));
    //                     faceInfos.Add(new BlockFaceInfo { blockType = bt, normal = 5 });
    //                 }
    //             }
    //         }
    //     }

    //     // Gerar UVs
    //     var world = VoxelWorld.Instance;
    //     Material[] mats = null;
    //     if (world != null)
    //     {
    //         if (world.waterMaterial != null)
    //             mats = new Material[] { world.chunkMaterial, world.waterMaterial };
    //         else
    //             mats = new Material[] { world.chunkMaterial };
    //     }
    //     else
    //     {
    //         mats = new Material[] { };
    //     }

    //     var solidUVs = new List<Vector2>(vertsSolid.Count);
    //     for (int i = 0; i < uvsSolidFaces.Count; i++)
    //     {
    //         var info = uvsSolidFaces[i];
    //         var normal = NormalFromIndex(info.normal);
    //         AddUVsTo(solidUVs, info.blockType, normal);
    //     }

    //     var leavesUVs = new List<Vector2>(vertsLeaves.Count);
    //     for (int i = 0; i < uvsLeavesFaces.Count; i++)
    //     {
    //         var info = uvsLeavesFaces[i];
    //         var normal = NormalFromIndex(info.normal);
    //         AddUVsTo(leavesUVs, info.blockType, normal);
    //     }




    //     var waterUVs = new List<Vector2>(vertsWater.Count);
    //     for (int i = 0; i < uvsWaterFaces.Count; i++)
    //     {
    //         var info = uvsWaterFaces[i];
    //         var normal = NormalFromIndex(info.normal);
    //         AddUVsTo(waterUVs, info.blockType, normal);
    //     }

    //     // --- Construir listas de normais por-vértice (4 cópias da normal por face) ---
    //     List<Vector3> solidNormals = null;
    //     if (uvsSolidFaces != null && uvsSolidFaces.Count > 0)
    //     {
    //         solidNormals = new List<Vector3>(uvsSolidFaces.Count * 4);
    //         for (int f = 0; f < uvsSolidFaces.Count; f++)
    //         {
    //             var n = NormalFromIndex(uvsSolidFaces[f].normal);
    //             solidNormals.Add(n);
    //             solidNormals.Add(n);
    //             solidNormals.Add(n);
    //             solidNormals.Add(n);
    //         }
    //     }

    //     List<Vector3> leavesNormals = null;
    //     if (uvsLeavesFaces.Count > 0)
    //     {
    //         leavesNormals = new List<Vector3>(uvsLeavesFaces.Count * 4);
    //         for (int f = 0; f < uvsLeavesFaces.Count; f++)
    //         {
    //             var n = NormalFromIndex(uvsLeavesFaces[f].normal);
    //             leavesNormals.Add(n);
    //             leavesNormals.Add(n);
    //             leavesNormals.Add(n);
    //             leavesNormals.Add(n);
    //         }
    //     }

    //     List<Vector3> waterNormals = null;
    //     if (uvsWaterFaces != null && uvsWaterFaces.Count > 0)
    //     {
    //         waterNormals = new List<Vector3>(uvsWaterFaces.Count * 4);
    //         for (int f = 0; f < uvsWaterFaces.Count; f++)
    //         {
    //             var n = NormalFromIndex(uvsWaterFaces[f].normal);
    //             waterNormals.Add(n);
    //             waterNormals.Add(n);
    //             waterNormals.Add(n);
    //             waterNormals.Add(n);
    //         }
    //     }

    //     // Aplicar mesh usando a nova assinatura (inclui normais)
    //     ApplyMeshData(
    //         vertsSolid, trisSolid, solidUVs, solidNormals,
    //         vertsWater, trisWater, waterUVs, waterNormals,
    //         mats,
    //         width, height, depth, blockSize);
    // }

    public void RebuildMesh()
    {
        if (blocks == null)
        {
            ApplyMeshData(null, null, null, null,
                          null, null, null, null,
                          null, null, null, null,
                          new Material[] { }, width, height, depth, blockSize);
            return;
        }

        // Listas locais para sólidos, folhas e água
        var vertsSolid = new List<Vector3>();
        var trisSolid = new List<int>();
        var uvsSolidFaces = new List<BlockFaceInfo>();

        var vertsLeaves = new List<Vector3>();
        var trisLeaves = new List<int>();
        var uvsLeavesFaces = new List<BlockFaceInfo>();

        var vertsWater = new List<Vector3>();
        var trisWater = new List<int>();
        var uvsWaterFaces = new List<BlockFaceInfo>();

        float s = blockSize;

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    BlockType bt = blocks[x, y, z];
                    if (bt == BlockType.Air) continue;

                    Vector3 basePos = new Vector3(x * s, y * s, z * s);
                    bool isWater = (bt == BlockType.Water);
                    bool isLeaf = (bt == BlockType.Leaves);

                    var vList = isWater ? vertsWater : isLeaf ? vertsLeaves : vertsSolid;
                    var tList = isWater ? trisWater : isLeaf ? trisLeaves : trisSolid;
                    var faceInfos = isWater ? uvsWaterFaces : isLeaf ? uvsLeavesFaces : uvsSolidFaces;

                    // front (+z)
                    if (IsNeighborAirOrEmpty(x, y, z + 1, isWater))
                    {
                        AddQuadNoAlloc(vList, tList,
                            basePos + new Vector3(0, 0, s),
                            basePos + new Vector3(s, 0, s),
                            basePos + new Vector3(s, s, s),
                            basePos + new Vector3(0, s, s));
                        faceInfos.Add(new BlockFaceInfo { blockType = bt, normal = 0 });
                    }
                    // back (-z)
                    if (IsNeighborAirOrEmpty(x, y, z - 1, isWater))
                    {
                        AddQuadNoAlloc(vList, tList,
                            basePos + new Vector3(s, 0, 0),
                            basePos + new Vector3(0, 0, 0),
                            basePos + new Vector3(0, s, 0),
                            basePos + new Vector3(s, s, 0));
                        faceInfos.Add(new BlockFaceInfo { blockType = bt, normal = 1 });
                    }
                    // top (+y)
                    if (IsNeighborAirOrEmpty(x, y + 1, z, isWater))
                    {
                        AddQuadNoAlloc(vList, tList,
                            basePos + new Vector3(0, s, 0),
                            basePos + new Vector3(0, s, s),
                            basePos + new Vector3(s, s, s),
                            basePos + new Vector3(s, s, 0));
                        faceInfos.Add(new BlockFaceInfo { blockType = bt, normal = 2 });
                    }
                    // bottom (-y)
                    if (IsNeighborAirOrEmpty(x, y - 1, z, isWater))
                    {
                        AddQuadNoAlloc(vList, tList,
                            basePos + new Vector3(0, 0, 0),
                            basePos + new Vector3(s, 0, 0),
                            basePos + new Vector3(s, 0, s),
                            basePos + new Vector3(0, 0, s));
                        faceInfos.Add(new BlockFaceInfo { blockType = bt, normal = 3 });
                    }
                    // right (+x)
                    if (IsNeighborAirOrEmpty(x + 1, y, z, isWater))
                    {
                        AddQuadNoAlloc(vList, tList,
                            basePos + new Vector3(s, 0, s),
                            basePos + new Vector3(s, 0, 0),
                            basePos + new Vector3(s, s, 0),
                            basePos + new Vector3(s, s, s));
                        faceInfos.Add(new BlockFaceInfo { blockType = bt, normal = 4 });
                    }
                    // left (-x)
                    if (IsNeighborAirOrEmpty(x - 1, y, z, isWater))
                    {
                        AddQuadNoAlloc(vList, tList,
                            basePos + new Vector3(0, 0, 0),
                            basePos + new Vector3(0, 0, s),
                            basePos + new Vector3(0, s, s),
                            basePos + new Vector3(0, s, 0));
                        faceInfos.Add(new BlockFaceInfo { blockType = bt, normal = 5 });
                    }
                }
            }
        }

        var world = VoxelWorld.Instance;
        Material[] mats = null;
        if (world != null)
        {
            var matList = new List<Material>();
            if (world.chunkMaterial != null) matList.Add(world.chunkMaterial);
            if (world.leafMaterial != null) matList.Add(world.leafMaterial);
            if (world.waterMaterial != null) matList.Add(world.waterMaterial);
            mats = matList.ToArray();
        }
        else
        {
            mats = new Material[] { };
        }

        // UVs
        var solidUVs = new List<Vector2>(vertsSolid.Count);
        foreach (var info in uvsSolidFaces) AddUVsTo(solidUVs, info.blockType, NormalFromIndex(info.normal));

        var leavesUVs = new List<Vector2>(vertsLeaves.Count);
        foreach (var info in uvsLeavesFaces) AddUVsTo(leavesUVs, info.blockType, NormalFromIndex(info.normal));

        var waterUVs = new List<Vector2>(vertsWater.Count);
        foreach (var info in uvsWaterFaces) AddUVsTo(waterUVs, info.blockType, NormalFromIndex(info.normal));

        // Normais
        List<Vector3> solidNormals = GenNormals(uvsSolidFaces);
        List<Vector3> leavesNormals = GenNormals(uvsLeavesFaces);
        List<Vector3> waterNormals = GenNormals(uvsWaterFaces);

        ApplyMeshData(
            vertsSolid, trisSolid, solidUVs, solidNormals,
            vertsLeaves, trisLeaves, leavesUVs, leavesNormals,
            vertsWater, trisWater, waterUVs, waterNormals,
            mats,
            width, height, depth, blockSize);
    }

    // helper para gerar normais
    private List<Vector3> GenNormals(List<BlockFaceInfo> faces)
    {
        if (faces == null || faces.Count == 0) return null;
        var normals = new List<Vector3>(faces.Count * 4);
        for (int f = 0; f < faces.Count; f++)
        {
            var n = NormalFromIndex(faces[f].normal);
            normals.Add(n); normals.Add(n); normals.Add(n); normals.Add(n);
        }
        return normals;
    }


    // estrutura auxiliar para armazenar info por face (blockType + normal index)
    private struct BlockFaceInfo
    {
        public BlockType blockType;
        public int normal;
    }

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
            _ => Vector3.forward,
        };
    }

    // // verifica vizinho dentro do chunk; fora do chunk = Air
    // private bool IsNeighborAirOrEmpty(int nx, int ny, int nz)
    // {
    //     if (ny < 0 || ny >= height) return true;
    //     if (nx < 0 || nx >= width) return true;
    //     if (nz < 0 || nz >= depth) return true;

    //     var nb = blocks[nx, ny, nz];
    //     return IsBlockEmptyByBlockData(nb);
    // }
    // verifica vizinho dentro do chunk; fora do chunk = consulta VoxelWorld (se existir) else Air

    // sem alocação de arrays temporários por face
    private void AddQuadNoAlloc(List<Vector3> vList, List<int> tList, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3)
    {
        int vi = vList.Count;
        vList.Add(v0);
        vList.Add(v1);
        vList.Add(v2);
        vList.Add(v3);
        tList.Add(vi + 0); tList.Add(vi + 1); tList.Add(vi + 2);
        tList.Add(vi + 0); tList.Add(vi + 2); tList.Add(vi + 3);
    }

    // AddUVsTo adaptado do VoxelWorld — adiciona 4 uvs por face
    private void AddUVsTo(List<Vector2> uvList, BlockType bt, Vector3 normal)
    {
        Vector2Int tile;

        var world = VoxelWorld.Instance;

        if (blockDataSO != null && blockDataSO.blockTextureDict.TryGetValue(bt, out var mapping))
        {
            tile = normal == Vector3.up ? mapping.top :
                   normal == Vector3.down ? mapping.bottom :
                   mapping.side;
        }
        else
        {
            // fallback silencioso (evitar debug.log em hot-path)
            if (blockDataSO != null && blockDataSO.blockTextureDict.TryGetValue(BlockType.Placeholder, out var fallbackMapping))
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
        if (world != null && world.atlasOrientation == VoxelWorld.AtlasOrientation.TopToBottom && blockDataSO != null)
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



    // // verifica vizinho dentro do chunk; fora do chunk = consulta VoxelWorld (se existir) else Air
    // private bool IsNeighborAirOrEmpty(int nx, int ny, int nz)
    // {
    //     if (ny < 0 || ny >= height) return true;

    //     // vizinho dentro do mesmo chunk
    //     if (nx >= 0 && nx < width && nz >= 0 && nz < depth)
    //     {
    //         var nb = blocks[nx, ny, nz];
    //         return IsBlockEmptyByBlockData(nb);
    //     }

    //     // fora do chunk: consultamos o mundo para ver se existe bloco ativo naquele local
    //     var world = VoxelWorld.Instance;
    //     if (world == null) return true;

    //     // coordenada do bloco vizinho em world space
    //     Vector3 neighborWorldPos = transform.position + new Vector3(nx * blockSize, ny * blockSize, nz * blockSize);
    //     var nbType = world.GetBlockAtWorld(neighborWorldPos);
    //     return IsBlockEmptyByBlockData(nbType);
    // }

    private bool IsNeighborAirOrEmpty(int nx, int ny, int nz, bool faceIsWater)
    {
        // Fora no Y => exposto
        if (ny < 0 || ny >= height) return true;

        // vizinho dentro do mesmo chunk
        if (nx >= 0 && nx < width && nz >= 0 && nz < depth)
        {
            var nb = blocks[nx, ny, nz];

            // se a face pertence à água: exposto somente se vizinho for ar
            if (faceIsWater) return (nb == BlockType.Air);

            // caso contrário (sólido): exposto se ar ou se o mapping declarar isEmpty
            return IsBlockEmptyByBlockData(nb);
        }

        // fora do chunk: consultamos o mundo para ver se existe bloco ativo naquele local
        var world = VoxelWorld.Instance;
        if (world == null)
        {
            // sem mundo, trate como ar (seguro)
            return true;
        }

        // coordenada do bloco vizinho em world space
        Vector3 neighborWorldPos = transform.position + new Vector3(nx * blockSize, ny * blockSize, nz * blockSize);
        var nbType = world.GetBlockAtWorld(neighborWorldPos);

        if (faceIsWater) return (nbType == BlockType.Air);

        return IsBlockEmptyByBlockData(nbType);
    }

    // // Retorna true se o BlockType deve ser tratado como "vazio" para fins de face-culling.
    // // Retorna true se o BlockType deve ser tratado como "vazio" para fins de face-culling.
    // private bool IsBlockEmptyByBlockData(BlockType bt)
    // {
    //     if (bt == BlockType.Air) return true;

    //     // Usa o BlockDataSO do próprio chunk se existir; senão tenta usar o do mundo.
    //     var so = blockDataSO;
    //     if (so == null)
    //     {
    //         var world = VoxelWorld.Instance;
    //         if (world != null) so = world.blockDataSO;
    //     }

    //     if (so != null && so.blockTextureDict.TryGetValue(bt, out var mapping))
    //     {
    //         return mapping.isEmpty;
    //     }

    //     // fallback seguro: por padrão não considerar água como "vazia".
    //     // (mantém comportamento consistente com o MeshGenJob quando blockDataSO == null)
    //     return false;
    // }

    private bool IsBlockEmptyByBlockData(BlockType bt)
    {
        if (bt == BlockType.Air) return true;

        // Usa o BlockDataSO do próprio chunk se existir; senão tenta usar o do mundo.
        var so = blockDataSO;
        if (so == null)
        {
            var world = VoxelWorld.Instance;
            if (world != null) so = world.blockDataSO;
        }

        if (so != null && so.blockTextureDict.TryGetValue(bt, out var mapping))
        {
            return mapping.isEmpty;
        }

        // fallback: por padrão NÃO considerar água como vazia (consistente com o Job)
        return false;
    }
    private List<BoxCollider> boxColliders = new List<BoxCollider>();

    private void ClearColliders()
    {
        foreach (var bc in boxColliders)
            if (bc != null) bc.enabled = false;
    }

    public void ApplyAABBCollidersFromBlocks()
    {
        ClearColliders();
        if (blocks == null) return;

        int w = width, h = height, d = depth;
        var occ = new bool[w, h, d];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                for (int z = 0; z < d; z++)
                {
                    BlockType bt = blocks[x, y, z];
                    occ[x, y, z] = IsSolid(bt);
                }

        int colliderIndex = 0;
        for (int z = 0; z < d; z++)
        {
            for (int y = 0; y < h; y++)
            {
                int x = 0;
                while (x < w)
                {
                    if (!occ[x, y, z]) { x++; continue; }

                    int x2 = x + 1;
                    while (x2 < w && occ[x2, y, z]) x2++;

                    int y2 = y + 1;
                    while (y2 < h)
                    {
                        bool rowAll = true;
                        for (int xi = x; xi < x2; xi++)
                            if (!occ[xi, y2, z]) { rowAll = false; break; }
                        if (!rowAll) break;
                        y2++;
                    }

                    int z2 = z + 1;
                    while (z2 < d)
                    {
                        bool slabAll = true;
                        for (int yi = y; yi < y2 && slabAll; yi++)
                            for (int xi = x; xi < x2; xi++)
                                if (!occ[xi, yi, z2]) { slabAll = false; break; }
                        if (!slabAll) break;
                        z2++;
                    }

                    for (int zi = z; zi < z2; zi++)
                        for (int yi = y; yi < y2; yi++)
                            for (int xi = x; xi < x2; xi++)
                                occ[xi, yi, zi] = false;

                    BoxCollider bc;
                    if (colliderIndex < boxColliders.Count)
                    {
                        bc = boxColliders[colliderIndex];
                        bc.enabled = true;
                    }
                    else
                    {
                        bc = gameObject.AddComponent<BoxCollider>();
                        boxColliders.Add(bc);
                    }
                    colliderIndex++;

                    float sx = (x2 - x) * blockSize;
                    float sy = (y2 - y) * blockSize;
                    float sz = (z2 - z) * blockSize;

                    float cx = (x * blockSize) + sx * 0.5f;
                    float cy = (y * blockSize) + sy * 0.5f;
                    float cz = (z * blockSize) + sz * 0.5f;

                    bc.center = new Vector3(cx, cy, cz);
                    bc.size = new Vector3(sx, sy, sz);

                    bc.sharedMaterial = null;
                    bc.isTrigger = false;

                    x = x2;
                }
            }
        }
    }

    public void EnableColliders()
    {
        if (!hasColliders)
        {
            ApplyAABBCollidersFromBlocks();
            hasColliders = true;
        }
    }

    public void DisableColliders()
    {
        if (hasColliders)
        {
            ClearColliders();
            hasColliders = false;
        }
    }

    private bool IsSolid(BlockType bt)
    {
        if (bt == BlockType.Air) return false;

        // Se tiver BlockDataSO, consultar lá
        if (blockDataSO != null && blockDataSO.blockTextureDict.TryGetValue(bt, out var data))
            return data.isSolid;

        // fallback: só tratar água como não sólidos
        return bt != BlockType.Water;
    }


    // public void ApplyMeshData(
    //     List<Vector3> solidVertices, List<int> solidTriangles, List<Vector2> solidUVs, List<Vector3> solidNormals,
    //     List<Vector3> waterVertices, List<int> waterTriangles, List<Vector2> waterUVs, List<Vector3> waterNormals,
    //     Material[] materials, int w, int h, int d, float bSize)
    // {
    //     width = w; height = h; depth = d; blockSize = bSize;

    //     int solidCount = (solidVertices != null) ? solidVertices.Count : 0;
    //     int waterCount = (waterVertices != null) ? waterVertices.Count : 0;

    //     // combinar vértices/uvs/normals em listas contíguas para o mesh
    //     int totalVerts = solidCount + waterCount;
    //     var allVerts = new List<Vector3>(totalVerts);
    //     var allUVs = new List<Vector2>(totalVerts);
    //     var allNormals = new List<Vector3>(totalVerts);

    //     if (solidVertices != null) allVerts.AddRange(solidVertices);
    //     if (waterVertices != null) allVerts.AddRange(waterVertices);

    //     if (solidUVs != null) allUVs.AddRange(solidUVs);
    //     if (waterUVs != null) allUVs.AddRange(waterUVs);

    //     if (solidNormals != null) allNormals.AddRange(solidNormals);
    //     if (waterNormals != null) allNormals.AddRange(waterNormals);

    //     // Reutilizar mesh existente quando possível para evitar GC / uploads desnecessários
    //     Mesh mesh = meshFilter.sharedMesh;
    //     if (mesh == null)
    //     {
    //         mesh = new Mesh();
    //         mesh.name = $"{gameObject.name}_mesh";
    //         // Permitir > 65535 vértices se necessário
    //         mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
    //         meshFilter.sharedMesh = mesh;
    //     }

    //     // Limpar mantendo capacidade interna (false) para reduzir reallocs
    //     mesh.Clear(false);

    //     // Escolher index format conforme número de vértices
    //     mesh.indexFormat = (totalVerts > 65535) ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;

    //     // Vértices
    //     if (allVerts.Count > 0)
    //         mesh.SetVertices(allVerts);
    //     else
    //         mesh.SetVertices(new List<Vector3>());

    //     // Normais: se o chamador já passou normais, apenas setar
    //     if (allNormals != null && allNormals.Count == allVerts.Count)
    //     {
    //         mesh.SetNormals(allNormals);
    //     }
    //     else
    //     {
    //         // fallback: computar normais manualmente a partir dos triangles (evita RecalculateNormals interno)
    //         // cria array de accumulação
    //         var accumNormals = new Vector3[allVerts.Count];
    //         for (int i = 0; i < accumNormals.Length; i++) accumNormals[i] = Vector3.zero;

    //         // montar lista de triangles combinados (ajustando offset para water)
    //         var trisCombined = new List<int>();
    //         if (solidTriangles != null) trisCombined.AddRange(solidTriangles);
    //         if (waterTriangles != null && waterTriangles.Count > 0)
    //         {
    //             for (int i = 0; i < waterTriangles.Count; i++)
    //                 trisCombined.Add(waterTriangles[i] + solidCount);
    //         }

    //         // para cada tri, calcular normal e somar nos vértices
    //         for (int ti = 0; ti + 2 < trisCombined.Count; ti += 3)
    //         {
    //             int i0 = trisCombined[ti + 0];
    //             int i1 = trisCombined[ti + 1];
    //             int i2 = trisCombined[ti + 2];
    //             if (i0 < 0 || i1 < 0 || i2 < 0 ||
    //                 i0 >= allVerts.Count || i1 >= allVerts.Count || i2 >= allVerts.Count)
    //                 continue;

    //             Vector3 v0 = allVerts[i0];
    //             Vector3 v1 = allVerts[i1];
    //             Vector3 v2 = allVerts[i2];
    //             Vector3 triNormal = Vector3.Cross(v1 - v0, v2 - v0);
    //             // soma (sem normalizar ainda)
    //             accumNormals[i0] += triNormal;
    //             accumNormals[i1] += triNormal;
    //             accumNormals[i2] += triNormal;
    //         }

    //         // normalizar e setar
    //         var finalNormals = new List<Vector3>(allVerts.Count);
    //         for (int i = 0; i < accumNormals.Length; i++)
    //         {
    //             Vector3 n = accumNormals[i];
    //             if (n.sqrMagnitude > 1e-8f) n.Normalize();
    //             else n = Vector3.up; // fallback seguro
    //             finalNormals.Add(n);
    //         }
    //         mesh.SetNormals(finalNormals);
    //     }

    //     // submeshes: 2 se houver água, senão 1
    //     int subMeshCount = (waterCount > 0) ? 2 : 1;
    //     mesh.subMeshCount = subMeshCount;

    //     // sólidos (submesh 0)
    //     if (solidTriangles != null && solidTriangles.Count > 0)
    //         mesh.SetTriangles(solidTriangles, 0);
    //     else
    //         mesh.SetTriangles(new int[0], 0);

    //     // água (submesh 1) — ajustar índices pelo offset dos vértices sólidos
    //     if (waterCount > 0 && waterTriangles != null && waterTriangles.Count > 0)
    //     {
    //         var waterTrisOffset = new List<int>(waterTriangles.Count);
    //         for (int i = 0; i < waterTriangles.Count; i++)
    //             waterTrisOffset.Add(waterTriangles[i] + solidCount);
    //         mesh.SetTriangles(waterTrisOffset, 1);
    //     }

    //     // UVs
    //     if (allUVs != null && allUVs.Count == allVerts.Count)
    //         mesh.SetUVs(0, allUVs);
    //     else
    //     {
    //         var fallback = new List<Vector2>(allVerts.Count);
    //         for (int i = 0; i < allVerts.Count; i++) fallback.Add(Vector2.zero);
    //         mesh.SetUVs(0, fallback);
    //     }

    //     // Recalcular bounds (barato comparado a recalcular normals)
    //     mesh.RecalculateBounds();

    //     // Aplicar o mesh ao filter (já atribuímos sharedMesh; garantir instância atualizada)
    //     meshFilter.sharedMesh = mesh;

    //     // configurar materiais corretamente
    //     if (materials != null && materials.Length > 0)
    //     {
    //         if (waterCount > 0 && materials.Length >= 2)
    //             meshRenderer.materials = new Material[] { materials[0], materials[1] };
    //         else
    //             meshRenderer.materials = new Material[] { materials[0] };
    //     }

    //     // Upload para GPU (manter readable = false torna não-readable e economiza memória; aqui mantemos false)
    //     mesh.UploadMeshData(false);
    // }


    // public void ApplyMeshData(
    //     List<Vector3> solidVertices, List<int> solidTriangles, List<Vector2> solidUVs, List<Vector3> solidNormals,
    //     List<Vector3> leafVertices, List<int> leafTriangles, List<Vector2> leafUVs, List<Vector3> leafNormals,
    //     List<Vector3> waterVertices, List<int> waterTriangles, List<Vector2> waterUVs, List<Vector3> waterNormals,
    //     Material[] materials, int w, int h, int d, float bSize)
    // {
    //     width = w; height = h; depth = d; blockSize = bSize;

    //     int solidCount = solidVertices?.Count ?? 0;
    //     int leafCount = leafVertices?.Count ?? 0;
    //     int waterCount = waterVertices?.Count ?? 0;

    //     int totalVerts = solidCount + leafCount + waterCount;
    //     var allVerts = new List<Vector3>(totalVerts);
    //     var allUVs = new List<Vector2>(totalVerts);
    //     var allNormals = new List<Vector3>(totalVerts);

    //     if (solidVertices != null) allVerts.AddRange(solidVertices);
    //     if (leafVertices != null) allVerts.AddRange(leafVertices);
    //     if (waterVertices != null) allVerts.AddRange(waterVertices);

    //     if (solidUVs != null) allUVs.AddRange(solidUVs);
    //     if (leafUVs != null) allUVs.AddRange(leafUVs);
    //     if (waterUVs != null) allUVs.AddRange(waterUVs);

    //     if (solidNormals != null) allNormals.AddRange(solidNormals);
    //     if (leafNormals != null) allNormals.AddRange(leafNormals);
    //     if (waterNormals != null) allNormals.AddRange(waterNormals);

    //     Mesh mesh = meshFilter.sharedMesh;
    //     if (mesh == null)
    //     {
    //         mesh = new Mesh();
    //         mesh.name = $"{gameObject.name}_mesh";
    //         mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
    //         meshFilter.sharedMesh = mesh;
    //     }
    //     mesh.Clear(false);
    //     mesh.indexFormat = (totalVerts > 65535) ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
    //     mesh.SetVertices(allVerts);

    //     if (allNormals.Count == allVerts.Count) mesh.SetNormals(allNormals);
    //     else mesh.RecalculateNormals();

    //     // Submeshes
    //     int subMeshCount = 1;
    //     if (leafCount > 0) subMeshCount++;
    //     if (waterCount > 0) subMeshCount++;
    //     mesh.subMeshCount = subMeshCount;

    //     // sólidos = submesh 0
    //     mesh.SetTriangles(solidTriangles ?? new List<int>(), 0);

    //     // folhas = submesh 1
    //     if (leafCount > 0 && leafTriangles != null)
    //     {
    //         var leafTrisOffset = new List<int>(leafTriangles.Count);
    //         for (int i = 0; i < leafTriangles.Count; i++)
    //             leafTrisOffset.Add(leafTriangles[i] + solidCount);
    //         mesh.SetTriangles(leafTrisOffset, 1);
    //     }

    //     // água = último submesh
    //     if (waterCount > 0 && waterTriangles != null)
    //     {
    //         var waterTrisOffset = new List<int>(waterTriangles.Count);
    //         for (int i = 0; i < waterTriangles.Count; i++)
    //             waterTrisOffset.Add(waterTriangles[i] + solidCount + leafCount);
    //         mesh.SetTriangles(waterTrisOffset, subMeshCount - 1);
    //     }

    //     mesh.SetUVs(0, allUVs.Count == allVerts.Count ? allUVs : new List<Vector2>(new Vector2[allVerts.Count]));
    //     mesh.RecalculateBounds();
    //     meshFilter.sharedMesh = mesh;

    //     if (materials != null && materials.Length == mesh.subMeshCount)
    //     {
    //         meshRenderer.materials = materials;
    //     }
    //     else
    //     {
    //         // fallback seguro: aplica só o material base
    //         meshRenderer.materials = new Material[] { materials != null && materials.Length > 0 ? materials[0] : null };
    //     }

    //     mesh.UploadMeshData(false);
    // }


    public void ApplyMeshData(
        List<Vector3> solidVertices, List<int> solidTriangles, List<Vector2> solidUVs, List<Vector3> solidNormals,
        List<Vector3> leafVertices, List<int> leafTriangles, List<Vector2> leafUVs, List<Vector3> leafNormals,
        List<Vector3> waterVertices, List<int> waterTriangles, List<Vector2> waterUVs, List<Vector3> waterNormals,
        Material[] materials, int w, int h, int d, float bSize)
    {
        // atualizar metadados do chunk
        width = w; height = h; depth = d; blockSize = bSize;

        int solidCount = solidVertices?.Count ?? 0;
        int leafCount = leafVertices?.Count ?? 0;
        int waterCount = waterVertices?.Count ?? 0;

        int totalVerts = solidCount + leafCount + waterCount;

        // Combinar vértices / uvs / normais em listas contíguas
        var allVerts = new List<Vector3>(totalVerts);
        var allUVs = new List<Vector2>(totalVerts);
        var allNormals = new List<Vector3>(totalVerts);

        if (solidVertices != null) allVerts.AddRange(solidVertices);
        if (leafVertices != null) allVerts.AddRange(leafVertices);
        if (waterVertices != null) allVerts.AddRange(waterVertices);

        if (solidUVs != null) allUVs.AddRange(solidUVs);
        if (leafUVs != null) allUVs.AddRange(leafUVs);
        if (waterUVs != null) allUVs.AddRange(waterUVs);

        if (solidNormals != null) allNormals.AddRange(solidNormals);
        if (leafNormals != null) allNormals.AddRange(leafNormals);
        if (waterNormals != null) allNormals.AddRange(waterNormals);

        // Reutilizar mesh existente quando possível
        Mesh mesh = meshFilter.sharedMesh;
        if (mesh == null)
        {
            mesh = new Mesh();
            mesh.name = $"{gameObject.name}_mesh";
            // permitir > 65535 vértices quando necessário
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            meshFilter.sharedMesh = mesh;
        }

        // Limpar mantendo capacidade interna (false) para reduzir reallocs
        mesh.Clear(false);

        // Escolher index format conforme número de vértices
        mesh.indexFormat = (totalVerts > 65535) ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;

        // Vértices
        if (allVerts.Count > 0)
            mesh.SetVertices(allVerts);
        else
            mesh.SetVertices(new List<Vector3>());

        // Normais: se o chamador já passou normais completas, usá-las; senão calcular a partir dos triângulos
        if (allNormals != null && allNormals.Count == allVerts.Count)
        {
            mesh.SetNormals(allNormals);
        }
        else
        {
            // calcular normais manualmente a partir dos triângulos (evita RecalculateNormals interno)
            var accumNormals = new Vector3[allVerts.Count];
            for (int i = 0; i < accumNormals.Length; i++) accumNormals[i] = Vector3.zero;

            // montar lista de triangles combinados (ajustando offsets para leaf e water)
            var trisCombined = new List<int>();
            if (solidTriangles != null) trisCombined.AddRange(solidTriangles);

            if (leafTriangles != null && leafTriangles.Count > 0)
            {
                int leafOffset = solidCount;
                for (int i = 0; i < leafTriangles.Count; i++)
                    trisCombined.Add(leafTriangles[i] + leafOffset);
            }

            if (waterTriangles != null && waterTriangles.Count > 0)
            {
                int waterOffset = solidCount + leafCount;
                for (int i = 0; i < waterTriangles.Count; i++)
                    trisCombined.Add(waterTriangles[i] + waterOffset);
            }

            // calcular normal por tri e acumular
            for (int ti = 0; ti + 2 < trisCombined.Count; ti += 3)
            {
                int i0 = trisCombined[ti + 0];
                int i1 = trisCombined[ti + 1];
                int i2 = trisCombined[ti + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 ||
                    i0 >= allVerts.Count || i1 >= allVerts.Count || i2 >= allVerts.Count)
                    continue;

                Vector3 v0 = allVerts[i0];
                Vector3 v1 = allVerts[i1];
                Vector3 v2 = allVerts[i2];
                Vector3 triNormal = Vector3.Cross(v1 - v0, v2 - v0);
                accumNormals[i0] += triNormal;
                accumNormals[i1] += triNormal;
                accumNormals[i2] += triNormal;
            }

            // normalizar e setar
            var finalNormals = new List<Vector3>(allVerts.Count);
            for (int i = 0; i < accumNormals.Length; i++)
            {
                Vector3 n = accumNormals[i];
                if (n.sqrMagnitude > 1e-8f) n.Normalize();
                else n = Vector3.up; // fallback seguro
                finalNormals.Add(n);
            }
            mesh.SetNormals(finalNormals);
        }

        // preparar submeshes: ordem = sólido(0), folhas(1 se existir), água(last se existir)
        int subMeshCount = 1;
        if (leafCount > 0) subMeshCount++;
        if (waterCount > 0) subMeshCount++;
        mesh.subMeshCount = subMeshCount;

        // sólidos = submesh 0
        if (solidTriangles != null && solidTriangles.Count > 0)
            mesh.SetTriangles(solidTriangles, 0);
        else
            mesh.SetTriangles(new int[0], 0);

        // folhas = submesh 1 (se existirem)
        if (leafCount > 0)
        {
            if (leafTriangles != null && leafTriangles.Count > 0)
            {
                var leafTrisOffset = new List<int>(leafTriangles.Count);
                for (int i = 0; i < leafTriangles.Count; i++)
                    leafTrisOffset.Add(leafTriangles[i] + solidCount);
                mesh.SetTriangles(leafTrisOffset, 1);
            }
            else
            {
                mesh.SetTriangles(new int[0], 1);
            }
        }

        // água = último submesh (se existir)
        if (waterCount > 0)
        {
            int waterSubIndex = (leafCount > 0) ? 2 : 1;
            if (waterTriangles != null && waterTriangles.Count > 0)
            {
                var waterTrisOffset = new List<int>(waterTriangles.Count);
                int waterOffset = solidCount + leafCount;
                for (int i = 0; i < waterTriangles.Count; i++)
                    waterTrisOffset.Add(waterTriangles[i] + waterOffset);
                mesh.SetTriangles(waterTrisOffset, waterSubIndex);
            }
            else
            {
                mesh.SetTriangles(new int[0], waterSubIndex);
            }
        }

        // UVs: garantir que o tamanho bata com allVerts
        if (allUVs != null && allUVs.Count == allVerts.Count)
            mesh.SetUVs(0, allUVs);
        else
        {
            var fallback = new List<Vector2>(allVerts.Count);
            for (int i = 0; i < allVerts.Count; i++) fallback.Add(Vector2.zero);
            mesh.SetUVs(0, fallback);
        }

        // Recalcular bounds
        mesh.RecalculateBounds();

        // Garantir que sharedMesh seja o mesh atual
        meshFilter.sharedMesh = mesh;

        // --- materials: garantir correspondência com subMeshCount ---
        Material[] finalMats = null;

        // Se o chamador passou um array e ele coincide com o número de submeshes, usar direto
        if (materials != null && materials.Length == mesh.subMeshCount)
        {
            finalMats = materials;
        }
        else
        {
            // Montar dinamicamente a partir do VoxelWorld (se disponível) ou do array parcial passado
            var world = VoxelWorld.Instance;
            var matList = new List<Material>();

            // base (sólidos)
            Material baseMat = (materials != null && materials.Length > 0) ? materials[0] : (world != null ? world.chunkMaterial : null);
            if (baseMat != null) matList.Add(baseMat);
            else matList.Add(new Material(Shader.Find("Standard"))); // fallback seguro

            bool hasLeaf = (leafCount > 0);
            bool hasWater = (waterCount > 0);

            if (hasLeaf)
            {
                Material leafMat = null;
                if (materials != null && materials.Length > 1) leafMat = materials[1];
                else if (world != null) leafMat = world.leafMaterial;

                if (leafMat != null) matList.Add(leafMat);
                else matList.Add(matList[0]); // fallback para evitar null
            }

            if (hasWater)
            {
                Material waterMat = null;
                if (materials != null)
                {
                    int expectedIndex = 1 + (hasLeaf ? 1 : 0);
                    if (materials.Length > expectedIndex) waterMat = materials[expectedIndex];
                }
                if (waterMat == null && world != null) waterMat = world.waterMaterial;
                if (waterMat != null) matList.Add(waterMat);
                else matList.Add(matList[0]);
            }

            finalMats = matList.ToArray();
        }

        // Aplicar materiais
        meshRenderer.materials = finalMats;

        // Upload para GPU (manter readable = false)
        mesh.UploadMeshData(false);
    }



}
