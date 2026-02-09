// PlayerBlockBreaker.cs
using UnityEngine;

[RequireComponent(typeof(BlockSelector))]
public class PlayerBlockBreaker : MonoBehaviour
{
    public BlockSelector selector;
    public Camera cam;
    [Header("Place settings")]
    public BlockType placeBlockType = BlockType.Stone; // tipo a ser colocado (ajuste no Inspector)
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

        // Colocar bloco (botão direito) - replace no seu PlayerBlockBreaker
        if (Input.GetMouseButtonDown(1))
        {
            Ray ray = new Ray(cam.transform.position, cam.transform.forward);

            if (Physics.Raycast(ray, out RaycastHit hit, selector.reach))
            {
                // bloco atingido (mesma lógica do selector)
                Vector3Int targetBlock = Vector3Int.FloorToInt(hit.point - hit.normal * 0.01f);

                // conversão segura da normal -> inteiro (-1,0,1)
                Vector3Int normalInt = new Vector3Int(
                    Mathf.RoundToInt(hit.normal.x),
                    Mathf.RoundToInt(hit.normal.y),
                    Mathf.RoundToInt(hit.normal.z)
                );

                Vector3Int placePos = targetBlock + normalInt;

                Debug.Log($"[Place] hit.point={hit.point} normal={hit.normal} target={targetBlock} normalInt={normalInt} placePos={placePos}");

                // proteção Y
                if (placePos.y <= 2 || placePos.y >= Chunk.SizeY)
                {
                    Debug.Log("[Place] posição inválida Y");
                    return;
                }


                Vector3 playerPos = World.Instance.player.position;
                Vector3Int playerBlock = Vector3Int.FloorToInt(playerPos);
                if (placePos == playerBlock + Vector3Int.up)
                {
                    Debug.Log("[Place] impedido: na cabeça do jogador");
                    return;
                }

                // Inside the if (Input.GetMouseButtonDown(1)) block, replace the existing check:
                BlockType blockAtPlacePos = World.Instance.GetBlockAt(placePos);
                if (blockAtPlacePos != BlockType.Air && blockAtPlacePos != BlockType.Water)
                {
                    Debug.Log($"[Place] local já ocupado por {blockAtPlacePos}");
                    return;
                }

                // Proceed to place
                World.Instance.SetBlockAt(placePos, placeBlockType);
                Debug.Log($"[Place] requested {placeBlockType} at {placePos}");
            }
        }


    }
}
