using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class BlockSelector : MonoBehaviour
{
    public Camera cam;
    public float reach = 6f;
    public BlockType CurrentBlock { get; private set; } = BlockType.Air;
    private LineRenderer line;
    private Vector3Int currentBlock;
    private bool hasBlock;

    void Awake()
    {
        line = GetComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = false;

        line.startWidth = 0.03f;
        line.endWidth = 0.03f;

        line.positionCount = 16;
        line.enabled = false;
        line.numCapVertices = 4;     // ponta arredondada
        line.numCornerVertices = 4;  // cantos suaves
    }

    void Update()
    {
        UpdateSelection();
    }

   void UpdateSelection()
    {
        Ray ray = new Ray(cam.transform.position, cam.transform.forward);

        if (Physics.Raycast(ray, out RaycastHit hit, reach))
        {
            // 🔹 desloca um pouquinho para dentro do bloco
            Vector3 point = hit.point - hit.normal * 0.01f;

            Vector3Int blockPos = Vector3Int.FloorToInt(point);
            var blockType = World.Instance.GetBlockAt(blockPos); // Use a local var for the type, but don't shadow the property

            if (!hasBlock || blockPos != currentBlock)
            {
                currentBlock = blockPos;

                if (blockType == BlockType.Air)
                {
                    line.enabled = false;
                    hasBlock = false;
                    return;
                }
                
                CurrentBlock = blockType; // Only set if not air
                
                DrawCube(blockPos);
                hasBlock = true;
            }
        }
        else
        {
            hasBlock = false;
            line.enabled = false;
            // Removed: CurrentBlock = BlockType.Air; (prevents setting to air)
        }
    }


    void DrawCube(Vector3Int pos)
    {
        line.enabled = true;

        float offset = 0.002f; // MUITO pequeno

        Vector3 p = pos - Vector3.one * offset;
        Vector3 size = Vector3.one + Vector3.one * offset * 2f;


        // 8 vértices do cubo
        Vector3 v000 = p;
        Vector3 v100 = p + Vector3.right * size.x;
        Vector3 v010 = p + Vector3.up * size.y;
        Vector3 v110 = p + new Vector3(size.x, size.y, 0);

        Vector3 v001 = p + Vector3.forward * size.z;
        Vector3 v101 = p + new Vector3(size.x, 0, size.z);
        Vector3 v011 = p + new Vector3(0, size.y, size.z);
        Vector3 v111 = p + size;


        Vector3[] lines = new Vector3[]
        {
            v000, v100, v110, v010, v000, // face frente
            v001, v101, v111, v011, v001, // face trás
            v000, v001,                  // liga
            v100, v101,
            v110, v111,
            v010, v011
        };

        line.SetPositions(lines);
    }

    public Vector3Int GetSelectedBlock()
    {
        return hasBlock ? currentBlock : new Vector3Int(int.MinValue, 0, 0);
    }
}