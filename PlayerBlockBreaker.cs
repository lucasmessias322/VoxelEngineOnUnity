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
        if (Input.GetMouseButtonDown(0))
        {
            if (!selector.TryGetSelectedBlock(out Vector3Int sel, out _))
                return;

            if (selector.IsBillboardHit)
            {
                // Billboard de grama nao existe como voxel real.
                // Suprime apenas o billboard nessa celula, mantendo o bloco de grama-base.
                World.Instance.SuppressGrassBillboardAt(sel);
                if (breakBlockClip != null)
                    audioSource.PlayOneShot(breakBlockClip);
                return;
            }

            BlockType current = World.Instance.GetBlockAt(sel);

            if (current == BlockType.Bedrock || current == BlockType.Air || current == BlockType.Water)
            {
                Debug.Log("Tentou quebrar Bedrock ??");
                return;
            }

            World.Instance.SetBlockAt(sel, BlockType.Air);
            Debug.Log($"Break request at {sel} -> success");
            Debug.Log($"Break request at {sel} -> queued");

            if (breakBlockClip != null)
                audioSource.PlayOneShot(breakBlockClip);
        }
    }

    void HandlePlaceBlock()
    {
        if (Input.GetMouseButtonDown(1))
        {
            if (!selector.TryGetSelectedBlock(out Vector3Int targetBlock, out Vector3Int hitNormal))
                return;

            // Se o alvo for billboard, coloca exatamente na celula do billboard (substitui).
            Vector3Int placePos = selector.IsBillboardHit
                ? targetBlock
                : targetBlock + hitNormal;

            if (placePos.y <= 2)
                return;

            BlockType blockAtPlacePos = World.Instance.GetBlockAt(placePos);
            if (blockAtPlacePos != BlockType.Air && blockAtPlacePos != BlockType.Water)
                return;

            Vector3 blockCenter = placePos + Vector3.one * 0.5f;
            Vector3 halfExtents = Vector3.one * 0.5f;

            Collider[] hits = Physics.OverlapBox(blockCenter, halfExtents);

            foreach (var col in hits)
            {
                if (col.transform == transform)
                {
                    return;
                }
            }

            World.Instance.SetBlockAt(placePos, placeBlockType);
            if (placeBlockClip != null)
                audioSource.PlayOneShot(placeBlockClip);
        }
    }
}
