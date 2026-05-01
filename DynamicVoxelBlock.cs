using UnityEngine;

public class DynamicVoxelBlock : MonoBehaviour
{
    public Chunk OwnerChunk { get; private set; }
    public Vector3Int WorldPosition { get; private set; }
    public BlockType BlockType { get; private set; }

    public void Initialize(Chunk ownerChunk, Vector3Int worldPosition, BlockType blockType)
    {
        OwnerChunk = ownerChunk;
        WorldPosition = worldPosition;
        BlockType = blockType;
        OnDynamicBlockSpawned();
    }

    public void Despawn()
    {
        OnDynamicBlockDespawned();
        OwnerChunk = null;
    }

    protected virtual void OnDynamicBlockSpawned()
    {
    }

    protected virtual void OnDynamicBlockDespawned()
    {
    }
}
