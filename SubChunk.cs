using Unity.Collections;
using UnityEngine;

public sealed class Subchunk
{
    private readonly SubchunkColliderBuilder colliderBuilder = new SubchunkColliderBuilder();
    private GameObject colliderOwner;
    private bool hasColliderData;
    private bool canHaveColliders;
    private bool isVisible = true;

    public bool hasGeometry;

    public bool CanHaveColliders => canHaveColliders;
    public bool HasColliderData => hasColliderData;
    public bool IsVisible => isVisible;

    public Subchunk(GameObject owner, int subchunkIndex)
    {
        Initialize(owner, subchunkIndex);
    }

    public void Initialize(GameObject owner, int subchunkIndex)
    {
        colliderOwner = owner;
        Initialize(subchunkIndex);
    }

    public void Initialize(int subchunkIndex)
    {
        isVisible = true;
    }

    public void SetMeshState(bool geometryPresent, bool solidColliderGeometryPresent)
    {
        hasGeometry = geometryPresent;
        canHaveColliders = geometryPresent && solidColliderGeometryPresent;

        if (!canHaveColliders)
            ResetColliderState();
    }

    public void SetVisible(bool visible)
    {
        isVisible = visible;
    }

    public void SetColliderSystemEnabled(bool enabled)
    {
        colliderBuilder.SetEnabled(enabled && hasGeometry && hasColliderData);
    }

    public void ClearMesh()
    {
        hasGeometry = false;
        canHaveColliders = false;
        ResetColliderState();
    }

    public void ClearColliderData()
    {
        ResetColliderState();
    }

    public void RebuildColliders(
        NativeArray<byte> voxelData,
        BlockTextureMapping[] blockMappings,
        int startY,
        int endY)
    {
        if (!hasGeometry || !canHaveColliders || colliderOwner == null)
        {
            ResetColliderState();
            return;
        }

        hasColliderData = colliderBuilder.TryBuild(colliderOwner, voxelData, blockMappings, startY, endY);
    }

    private void ResetColliderState()
    {
        hasColliderData = false;
        colliderBuilder.Clear();
    }
}
