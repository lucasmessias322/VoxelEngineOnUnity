// using UnityEngine;

// public class PlayerVoxelInteraction : MonoBehaviour
// {
//     [Header("Config")]
//     public float reachDistance = 5f;              // até onde o player consegue interagir
//     public BlockType placeBlockType = BlockType.Stone; // tipo de bloco a ser colocado

//     private Camera cam;
//     private VoxelWorld voxelWorld;
//     private CharacterController characterController;

//     void Start()
//     {
//         characterController = GetComponent<CharacterController>();
//         cam = Camera.main;
//         voxelWorld = FindObjectOfType<VoxelWorld>();
//     }

//     void Update()
//     {
//         if (voxelWorld == null || cam == null) return;

//         // Quebrar bloco (botão esquerdo)
//         // Quebrar bloco (botão esquerdo)
//         if (Input.GetMouseButtonDown(0))
//         {
//             if (Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, reachDistance))
//             {
//                 Vector3 targetPos = hit.point - hit.normal * 0.5f;

//                 // Obter tipo do bloco atual
//                 BlockType current = voxelWorld.GetBlockAtWorld(targetPos);

//                 // Impedir quebra de blocos indestrutíveis
//                 if (current == BlockType.Bedrock || current == BlockType.Water)
//                 {
//                     return; // não quebra
//                 }

//                 voxelWorld.SetBlockAtWorld(targetPos, BlockType.Air);
//             }
//         }


//         // Colocar bloco (botão direito)
//         if (Input.GetMouseButtonDown(1))
//         {
//             if (Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, reachDistance))
//             {
//                 // Ponto logo ao lado do bloco atingido
//                 Vector3 targetPos = hit.point + hit.normal * 0.5f;
//                 voxelWorld.SetBlockAtWorld(targetPos, placeBlockType);
//             }
//         }
//     }
// }
using UnityEngine;

public class PlayerVoxelInteraction : MonoBehaviour
{
    [Header("Config")]
    public float reachDistance = 5f;              // até onde o player consegue interagir
    public BlockType placeBlockType = BlockType.Stone; // tipo de bloco a ser colocado

    [Header("Checks")]
    [Tooltip("Layers que bloqueiam a colocação (opcional). Por padrão todos.")]
    public LayerMask blockingLayers = ~0;
    [Tooltip("Reduz ligeiramente a caixa de checagem para evitar false-positives por precisão.")]
    public float overlapPadding = 0.01f;

    private Camera cam;
    private VoxelWorld voxelWorld;
    private CharacterController characterController;

    void Start()
    {
        characterController = GetComponent<CharacterController>();
        cam = Camera.main;
        voxelWorld = FindObjectOfType<VoxelWorld>();
    }

    void Update()
    {
        if (voxelWorld == null || cam == null) return;

        // Quebrar bloco (botão esquerdo)
        if (Input.GetMouseButtonDown(0))
        {
            if (Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, reachDistance))
            {
                Vector3 targetPos = hit.point - hit.normal * 0.5f;
                BlockType current = voxelWorld.GetBlockAtWorld(targetPos);

                if (current == BlockType.Bedrock || current == BlockType.Water)
                {
                    return; // não quebra
                }

                voxelWorld.SetBlockAtWorld(targetPos, BlockType.Air);
            }
        }

        // Colocar bloco (botão direito)
        if (Input.GetMouseButtonDown(1))
        {
            if (Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, reachDistance))
            {
                Vector3 targetPos = hit.point + hit.normal * 0.5f;

                // alinhamos o centro para evitar problemas de precisão
                Vector3 blockCenter = GetBlockCenter(targetPos);

                // só coloca se não colidir com o jogador
                if (CanPlaceBlockAt(blockCenter))
                {
                    voxelWorld.SetBlockAtWorld(blockCenter, placeBlockType);
                }
                else
                {
                    // opcional: feedback pro jogador (som/tooltip) dizendo "sem espaço"
                    Debug.Log("Não há espaço para colocar o bloco aqui.");
                }
            }
        }
    }

    // => retorna o centro do bloco (assumindo blocos 1x1x1 com centro em n + 0.5)
    private Vector3 GetBlockCenter(Vector3 worldPos)
    {
        return new Vector3(
            Mathf.Floor(worldPos.x) + 0.5f,
            Mathf.Floor(worldPos.y) + 0.5f,
            Mathf.Floor(worldPos.z) + 0.5f
        );
    }

    // Checa se colocar um bloco com centro em `blockCenter` colidiria com o CharacterController
    private bool CanPlaceBlockAt(Vector3 blockCenter)
    {
        if (characterController == null)
        {
            // sem controle do player, assume que pode
            return true;
        }

        // meio-extensões (metade do bloco) - reduzir um pouco para evitar problemas de float
        Vector3 halfExtents = Vector3.one * (0.5f - overlapPadding);

        // checa sobreposição (ignorando triggers)
        Collider[] overlaps = Physics.OverlapBox(blockCenter, halfExtents, Quaternion.identity, blockingLayers, QueryTriggerInteraction.Ignore);

        foreach (var col in overlaps)
        {
            if (col == null) continue;

            // se a colisão é o próprio CharacterController do jogador -> não pode colocar
            if (col == characterController) return false;

            // se for um collider filho do jogador (por segurança), também bloqueia
            if (col.transform.IsChildOf(transform)) return false;
        }

        // não colidiu com o jogador -> permitido
        return true;
    }

    // (Opcional) - desenha a caixa para debug na Scene view
    private void OnDrawGizmosSelected()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying) return;
        // desenhar último bloco checado? (não guardamos, mas se quiser, armazene num campo e desenhe aqui)
#endif
    }
}
