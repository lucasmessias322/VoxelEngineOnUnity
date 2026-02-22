// PlayerBlockBreaker.cs
using UnityEngine;

[RequireComponent(typeof(BlockSelector))]
[RequireComponent(typeof(AudioSource))]
public class PlayerBlockBreaker : MonoBehaviour
{
    public BlockSelector selector;
    public Camera cam;
    [Header("Place settings")]
    public BlockType placeBlockType = BlockType.Stone; // tipo a ser colocado (ajuste no Inspector)

    private AudioSource audioSource;
    public AudioClip placeBlockClip;
    public AudioClip breakBlockClip;




    void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        if (selector == null) selector = GetComponent<BlockSelector>();
        if (cam == null && selector != null) cam = selector.cam;
    }

    void Update()
    {
        for (int i = 1; i <= 9; i++)
        {
            if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + (i - 1))))
            {
                if (i == 1) placeBlockType = BlockType.Stone;
                else if (i == 2) placeBlockType = BlockType.Dirt;
                else if (i == 3) placeBlockType = BlockType.Grass;
                else if (i == 4) placeBlockType = BlockType.oak_planks;
                else if (i == 5) placeBlockType = BlockType.Log;
                else if (i == 6) placeBlockType = BlockType.glowstone;
                else if (i == 7) placeBlockType = BlockType.glass;
                else if (i == 8) placeBlockType = BlockType.Snow;
                else if (i == 9) placeBlockType = BlockType.Leaves;


                Debug.Log($"Selected block type for placing: {placeBlockType}");
            }
        }


        HandleBreakBlock();
        HandlePlaceBlock();



    }

    void HandleBreakBlock()
    {
        if (Input.GetMouseButtonDown(0)) // clique esquerdo -> quebrar
        {
            Vector3Int sel = selector.GetSelectedBlock();
            if (sel.x != int.MinValue)
            {
                // opcional: checar alcance (mas BlockSelector já faz raycast com reach)
                BlockType current = World.Instance.GetBlockAt(sel);

                // 🔒 não quebrável
                if (current == BlockType.Bedrock || current == BlockType.Air || current == BlockType.Water)
                {
                    Debug.Log("Tentou quebrar Bedrock 😈");
                    return;
                }

                World.Instance.SetBlockAt(sel, BlockType.Air);
                Debug.Log($"Break request at {sel} -> success");

                Debug.Log($"Break request at {sel} -> queued");

                audioSource.PlayOneShot(breakBlockClip);
            }
        }
    }
    void HandlePlaceBlock()
    {
        if (Input.GetMouseButtonDown(1))
        {
            Ray ray = new Ray(cam.transform.position, cam.transform.forward);

            if (Physics.Raycast(ray, out RaycastHit hit, selector.reach))
            {
                // 🔹 Cálculo mais robusto do bloco atingido
                // conversão segura da normal -> inteiro (-1,0,1)
                Vector3Int normalInt = new Vector3Int(
                    Mathf.RoundToInt(hit.normal.x),
                    Mathf.RoundToInt(hit.normal.y),
                    Mathf.RoundToInt(hit.normal.z)
                );



                Vector3Int targetBlock = Vector3Int.FloorToInt(hit.point - hit.normal * 0.01f);
                Vector3Int placePos = targetBlock + normalInt;

                // 🔹 Proteção Y
                if (placePos.y <= 2 || placePos.y >= Chunk.SizeY)
                    return;

                // 🔹 Verifica se já existe bloco sólido
                BlockType blockAtPlacePos = World.Instance.GetBlockAt(placePos);
                if (blockAtPlacePos != BlockType.Air && blockAtPlacePos != BlockType.Water)
                    return;

                // ================================
                // 🔥 VERIFICAÇÃO PROFISSIONAL
                // ================================

                Vector3 blockCenter = placePos + Vector3.one * 0.5f;
                Vector3 halfExtents = Vector3.one * 0.5f;

                Collider[] hits = Physics.OverlapBox(blockCenter, halfExtents);

                foreach (var col in hits)
                {
                    if (col.transform == transform)
                    {
                        // Está colidindo com o player
                        return;
                    }
                }

                // ================================

                World.Instance.SetBlockAt(placePos, placeBlockType);
                audioSource.PlayOneShot(placeBlockClip);
            }
        }
    }
}
