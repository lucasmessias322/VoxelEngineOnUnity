using UnityEngine;

[RequireComponent(typeof(BlockSelector))]
public class BlockBreaker : MonoBehaviour
{
    public BlockSelector selector;
    void Awake()
    {
        if (selector == null) selector = GetComponent<BlockSelector>();
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            Vector3Int pos = selector.GetSelectedBlock();
            if (pos.x != int.MinValue)
            {
                bool ok = World.Instance.SetBlockAtWorld(pos.x, pos.y, pos.z, BlockType.Air);
                Debug.Log($"Break request at {pos} -> success: {ok}");
            }
            else
            {
                Debug.Log("No block selected to break.");
            }
        }
    }
}
