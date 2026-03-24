using Unity.Collections;
using UnityEngine;

public class Subchunk : MonoBehaviour
{
    private readonly SubchunkColliderBuilder colliderBuilder = new SubchunkColliderBuilder();
    private bool hasColliderData;
    private bool canHaveColliders;
    private bool isVisible = true;

    [HideInInspector]
    public bool hasGeometry;

    public bool CanHaveColliders => canHaveColliders;
    public bool HasColliderData => hasColliderData;
    public bool IsVisible => isVisible;

    public void Initialize(int subchunkIndex)
    {
        gameObject.name = $"SubchunkLogic_{subchunkIndex}";
        transform.localPosition = Vector3.zero;
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
        if (!hasGeometry || !canHaveColliders)
        {
            ResetColliderState();
            return;
        }

        hasColliderData = colliderBuilder.TryBuild(gameObject, voxelData, blockMappings, startY, endY);
    }

    private void ResetColliderState()
    {
        hasColliderData = false;
        colliderBuilder.Clear();
    }
}
