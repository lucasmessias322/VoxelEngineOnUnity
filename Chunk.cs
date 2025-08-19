
using System;
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

    public void RebuildMesh()
    {
        if (blocks == null)
        {
            // limpa mesh
            ApplyMeshDataByMaterial(
                new List<List<Vector3>>(),
                new List<List<int>>(),
                new List<List<Vector2>>(),
                new List<List<Vector3>>(),
                new Material[] { }, width, height, depth, blockSize);
            return;
        }

        var world = VoxelWorld.Instance;
        var soForLookup = blockDataSO != null ? blockDataSO : (world != null ? world.blockDataSO : null);

        // listas por material (cresceremos dinamicamente conforme encontramos materialIndex)
        var vertsByMat = new List<List<Vector3>>();
        var trisByMat = new List<List<int>>();
        var uvsByMat = new List<List<Vector2>>();
        var normsByMat = new List<List<Vector3>>();

        void EnsureSlot(int idx)
        {
            while (vertsByMat.Count <= idx)
            {
                vertsByMat.Add(new List<Vector3>());
                trisByMat.Add(new List<int>());
                uvsByMat.Add(new List<Vector2>());
                normsByMat.Add(new List<Vector3>());
            }
        }

        // Helper para resolver materialIndex de um BlockType (usa blockDataSO local ou do world)
        int GetMaterialIndexForBlockType(BlockType bt)
        {
            if (soForLookup != null && soForLookup.blockTextureDict.TryGetValue(bt, out var mapping))
            {
                // mapping.materialIndex deve existir no seu BlockTextureMapping (fallback 0)
                try
                {
                    return Mathf.Max(0, mapping.materialIndex);
                }
                catch
                {
                    return 0;
                }
            }
            return 0;
        }

        float s = blockSize;

        // função para processar uma face: adiciona quad ao slot do material, UVs e normais
        void ProcessFace(int matIndex, BlockType bt, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 normalVec)
        {
            EnsureSlot(matIndex);
            var vlist = vertsByMat[matIndex];
            var tlist = trisByMat[matIndex];
            var uvlist = uvsByMat[matIndex];
            var nlist = normsByMat[matIndex];

            // AddQuadNoAlloc já cuida do offset dos índices relativamente ao vlist
            AddQuadNoAlloc(vlist, tlist, v0, v1, v2, v3);

            // UVs (4 por face)
            AddUVsTo(uvlist, bt, normalVec);

            // Normais (4 cópias)
            nlist.Add(normalVec); nlist.Add(normalVec); nlist.Add(normalVec); nlist.Add(normalVec);
        }

        // Percorrer blocos e gerar faces agrupadas por materialIndex
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

                    int matIndex = GetMaterialIndexForBlockType(bt);

                    // front (+z) normal = forward
                    if (IsNeighborAirOrEmpty(x, y, z + 1, isWater))
                    {
                        ProcessFace(matIndex, bt,
                            basePos + new Vector3(0, 0, s),
                            basePos + new Vector3(s, 0, s),
                            basePos + new Vector3(s, s, s),
                            basePos + new Vector3(0, s, s),
                            NormalFromIndex(0));
                    }

                    // back (-z) normal = back
                    if (IsNeighborAirOrEmpty(x, y, z - 1, isWater))
                    {
                        ProcessFace(matIndex, bt,
                            basePos + new Vector3(s, 0, 0),
                            basePos + new Vector3(0, 0, 0),
                            basePos + new Vector3(0, s, 0),
                            basePos + new Vector3(s, s, 0),
                            NormalFromIndex(1));
                    }

                    // top (+y) normal = up
                    if (IsNeighborAirOrEmpty(x, y + 1, z, isWater))
                    {
                        ProcessFace(matIndex, bt,
                            basePos + new Vector3(0, s, 0),
                            basePos + new Vector3(0, s, s),
                            basePos + new Vector3(s, s, s),
                            basePos + new Vector3(s, s, 0),
                            NormalFromIndex(2));
                    }

                    // bottom (-y) normal = down
                    if (IsNeighborAirOrEmpty(x, y - 1, z, isWater))
                    {
                        ProcessFace(matIndex, bt,
                            basePos + new Vector3(0, 0, 0),
                            basePos + new Vector3(s, 0, 0),
                            basePos + new Vector3(s, 0, s),
                            basePos + new Vector3(0, 0, s),
                            NormalFromIndex(3));
                    }

                    // right (+x) normal = right
                    if (IsNeighborAirOrEmpty(x + 1, y, z, isWater))
                    {
                        ProcessFace(matIndex, bt,
                            basePos + new Vector3(s, 0, s),
                            basePos + new Vector3(s, 0, 0),
                            basePos + new Vector3(s, s, 0),
                            basePos + new Vector3(s, s, s),
                            NormalFromIndex(4));
                    }

                    // left (-x) normal = left
                    if (IsNeighborAirOrEmpty(x - 1, y, z, isWater))
                    {
                        ProcessFace(matIndex, bt,
                            basePos + new Vector3(0, 0, 0),
                            basePos + new Vector3(0, 0, s),
                            basePos + new Vector3(0, s, s),
                            basePos + new Vector3(0, s, 0),
                            NormalFromIndex(5));
                    }
                }
            }
        }

        // montar array de materiais final: preferir world.materials (mais geral), senão usar chunk/leaf/water antigos
        Material[] finalMaterials = null;
        if (world != null && world.materials != null && world.materials.Length > 0)
        {
            finalMaterials = world.materials;
        }
        else
        {
            var mats = new List<Material>();
            if (world != null)
            {
                if (world.chunkMaterial != null) mats.Add(world.chunkMaterial);
                if (world.leafMaterial != null) mats.Add(world.leafMaterial);
                if (world.waterMaterial != null) mats.Add(world.waterMaterial);
            }
            finalMaterials = mats.ToArray();
        }

        // garantir listas não-nulas (ApplyMeshDataByMaterial aceita listas vazias)
        if (vertsByMat.Count == 0)
        {
            vertsByMat.Add(new List<Vector3>());
            trisByMat.Add(new List<int>());
            uvsByMat.Add(new List<Vector2>());
            normsByMat.Add(new List<Vector3>());
        }

        // chamar a versão genérica que monta o mesh com N submeshes
        ApplyMeshDataByMaterial(vertsByMat, trisByMat, uvsByMat, normsByMat, finalMaterials, width, height, depth, blockSize);
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


    /// <summary>
    /// Implementação genérica que monta o mesh a partir de listas por-material.
    /// verticesByMaterial[i], trianglesByMaterial[i], uvsByMaterial[i], normalsByMaterial[i]
    /// representam os dados do material de índice i (pode haver listas vazias).
    /// </summary>
    public void ApplyMeshDataByMaterial(
        List<List<Vector3>> verticesByMaterial,
        List<List<int>> trianglesByMaterial,
        List<List<Vector2>> uvsByMaterial,
        List<List<Vector3>> normalsByMaterial,
        Material[] materials,
        int w, int h, int d, float bSize)
    {
        // atualizar metadados do chunk
        width = w; height = h; depth = d; blockSize = bSize;

        // decidir quantos "slots" (materiais) vamos considerar
        int materialCount = (materials != null) ? materials.Length : 0;
        if (materialCount == 0)
        {
            materialCount = Math.Max(
                verticesByMaterial != null ? verticesByMaterial.Count : 0,
                Math.Max(
                    trianglesByMaterial != null ? trianglesByMaterial.Count : 0,
                    Math.Max(uvsByMaterial != null ? uvsByMaterial.Count : 0,
                             normalsByMaterial != null ? normalsByMaterial.Count : 0)
                )
            );
        }
        materialCount = Math.Max(1, materialCount);

        // preparar combinados
        var allVerts = new List<Vector3>();
        var allUVs = new List<Vector2>();
        var allNormals = new List<Vector3>();

        var submeshTriangles = new List<int>[materialCount];
        for (int i = 0; i < materialCount; i++) submeshTriangles[i] = new List<int>();

        // concatenar dados por material e ajustar índices dos triangles
        for (int mat = 0; mat < materialCount; mat++)
        {
            var vlist = (verticesByMaterial != null && mat < verticesByMaterial.Count) ? verticesByMaterial[mat] : null;
            var tris = (trianglesByMaterial != null && mat < trianglesByMaterial.Count) ? trianglesByMaterial[mat] : null;
            var uvs = (uvsByMaterial != null && mat < uvsByMaterial.Count) ? uvsByMaterial[mat] : null;
            var norms = (normalsByMaterial != null && mat < normalsByMaterial.Count) ? normalsByMaterial[mat] : null;

            int offset = allVerts.Count;

            if (vlist != null && vlist.Count > 0)
            {
                allVerts.AddRange(vlist);

                if (uvs != null && uvs.Count == vlist.Count) allUVs.AddRange(uvs);
                else
                {
                    for (int i = 0; i < vlist.Count; i++) allUVs.Add(Vector2.zero);
                }

                if (norms != null && norms.Count == vlist.Count) allNormals.AddRange(norms);
                else
                {
                    for (int i = 0; i < vlist.Count; i++) allNormals.Add(Vector3.zero);
                }
            }

            if (tris != null && tris.Count > 0)
            {
                for (int ti = 0; ti < tris.Count; ti++)
                    submeshTriangles[mat].Add(tris[ti] + offset);
            }
        }

        // criar/reutilizar mesh
        Mesh mesh = meshFilter.sharedMesh;
        if (mesh == null)
        {
            mesh = new Mesh();
            mesh.name = $"{gameObject.name}_mesh";
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            meshFilter.sharedMesh = mesh;
        }

        mesh.Clear(false);
        mesh.indexFormat = (allVerts.Count > 65535) ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;

        if (allVerts.Count > 0) mesh.SetVertices(allVerts);
        else mesh.SetVertices(new List<Vector3>());

        // normais: usar se válidas, senão calcular
        bool hasValidNormals = allNormals != null && allNormals.Count == allVerts.Count && allNormals.Exists(n => n.sqrMagnitude > 1e-8f);
        if (hasValidNormals)
        {
            mesh.SetNormals(allNormals);
        }
        else
        {
            var accum = new Vector3[allVerts.Count];
            for (int i = 0; i < accum.Length; i++) accum[i] = Vector3.zero;

            var combinedTris = new List<int>();
            for (int i = 0; i < materialCount; i++) combinedTris.AddRange(submeshTriangles[i]);

            for (int ti = 0; ti + 2 < combinedTris.Count; ti += 3)
            {
                int i0 = combinedTris[ti + 0];
                int i1 = combinedTris[ti + 1];
                int i2 = combinedTris[ti + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= allVerts.Count || i1 >= allVerts.Count || i2 >= allVerts.Count) continue;
                Vector3 v0 = allVerts[i0];
                Vector3 v1 = allVerts[i1];
                Vector3 v2 = allVerts[i2];
                Vector3 triN = Vector3.Cross(v1 - v0, v2 - v0);
                accum[i0] += triN; accum[i1] += triN; accum[i2] += triN;
            }

            var finalNormals = new List<Vector3>(allVerts.Count);
            for (int i = 0; i < accum.Length; i++)
            {
                Vector3 n = accum[i];
                if (n.sqrMagnitude > 1e-8f) n.Normalize(); else n = Vector3.up;
                finalNormals.Add(n);
            }
            mesh.SetNormals(finalNormals);
        }

        // configurar submeshes
        int usedSubMeshCount = materialCount;
        mesh.subMeshCount = usedSubMeshCount;

        for (int i = 0; i < usedSubMeshCount; i++)
        {
            var tri = submeshTriangles[i];
            if (tri != null && tri.Count > 0) mesh.SetTriangles(tri, i);
            else mesh.SetTriangles(new int[0], i);
        }

        // UVs
        if (allUVs != null && allUVs.Count == allVerts.Count) mesh.SetUVs(0, allUVs);
        else
        {
            var fallback = new List<Vector2>(allVerts.Count);
            for (int i = 0; i < allVerts.Count; i++) fallback.Add(Vector2.zero);
            mesh.SetUVs(0, fallback);
        }

        mesh.RecalculateBounds();
        meshFilter.sharedMesh = mesh;

        // materiais: alinhar com subMeshCount (usar VoxelWorld.materials como fallback se existir)
        Material[] finalMats;
        if (materials != null && materials.Length >= mesh.subMeshCount)
        {
            finalMats = new Material[mesh.subMeshCount];
            for (int i = 0; i < mesh.subMeshCount; i++) finalMats[i] = materials[i];
        }
        else
        {
            var world = VoxelWorld.Instance;
            var list = new List<Material>();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                Material m = null;
                if (materials != null && i < materials.Length) m = materials[i];
                else if (world != null && world.materials != null && i < world.materials.Length) m = world.materials[i];
                if (m == null) m = (world != null && world.chunkMaterial != null) ? world.chunkMaterial : new Material(Shader.Find("Standard"));
                list.Add(m);
            }
            finalMats = list.ToArray();
        }

        meshRenderer.materials = finalMats;

        mesh.UploadMeshData(false);
    }




}
