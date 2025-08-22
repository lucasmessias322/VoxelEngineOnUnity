using System.Collections.Generic;
using UnityEngine;

// Representa o resultado do job de meshing — deve ser público para que MeshBuilder possa acessá-lo
public class MeshJobResult
{
    public Vector2Int coord;
    public BlockType[,,] blocks;

    // faces sólidas
    public List<Vector3> solidVertices;
    public List<int> solidTriangles;
    public List<int> solidFaceBlockTypes;
    public List<int> solidFaceNormals;

    // folhas (separado)
    public List<Vector3> leafVertices;
    public List<int> leafTriangles;
    public List<int> leafFaceBlockTypes;
    public List<int> leafFaceNormals;

    // água (separado)
    public List<Vector3> waterVertices;
    public List<int> waterTriangles;
    public List<int> waterFaceBlockTypes;
    public List<int> waterFaceNormals;

    public int width;
    public int height;
    public int depth;
    public float blockSize;
}
