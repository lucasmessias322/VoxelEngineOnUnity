// PlayerBlockBreaker.cs
using UnityEngine;

[RequireComponent(typeof(BlockSelector))]
public class PlayerBlockBreaker : MonoBehaviour
{
    public BlockSelector selector;
    public Camera cam;

    void Awake()
    {
        if (selector == null) selector = GetComponent<BlockSelector>();
        if (cam == null && selector != null) cam = selector.cam;
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(0)) // clique esquerdo -> quebrar
        {
            Vector3Int sel = selector.GetSelectedBlock();
            if (sel.x != int.MinValue)
            {
                // opcional: checar alcance (mas BlockSelector já faz raycast com reach)
                BlockType current = World.Instance.GetBlockAt(sel);

                // 🔒 não quebrável
                if (current == BlockType.Bedrock)
                {
                    Debug.Log("Tentou quebrar Bedrock 😈");
                    return;
                }

                World.Instance.SetBlockAt(sel, BlockType.Air);
                Debug.Log($"Break request at {sel} -> success");

                Debug.Log($"Break request at {sel} -> queued");
            }
        }
    }
}
