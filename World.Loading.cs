using UnityEngine;

public partial class World : MonoBehaviour
{
    private WorldLoadingBootstrap loadingBootstrap;

    public bool IsInitialWorldReady
    {
        get
        {
            WorldLoadingBootstrap bootstrap = GetLoadingBootstrap();
            return bootstrap == null || !bootstrap.isActiveAndEnabled || bootstrap.IsInitialWorldReady;
        }
    }

    public bool IsInitialWorldLoading
    {
        get
        {
            WorldLoadingBootstrap bootstrap = GetLoadingBootstrap();
            return bootstrap != null && bootstrap.isActiveAndEnabled && bootstrap.IsInitialWorldLoading;
        }
    }

    public float InitialLoadProgress01
    {
        get
        {
            WorldLoadingBootstrap bootstrap = GetLoadingBootstrap();
            return bootstrap != null && bootstrap.isActiveAndEnabled
                ? bootstrap.InitialLoadProgress01
                : 1f;
        }
    }

    private WorldLoadingBootstrap GetLoadingBootstrap()
    {
        if (loadingBootstrap == null)
            loadingBootstrap = GetComponent<WorldLoadingBootstrap>();

        return loadingBootstrap;
    }

    private void EnsureLoadingBootstrapExists()
    {
        if (!Application.isPlaying)
            return;

        if (loadingBootstrap == null)
            loadingBootstrap = GetComponent<WorldLoadingBootstrap>();

        if (loadingBootstrap == null)
            loadingBootstrap = gameObject.AddComponent<WorldLoadingBootstrap>();
    }

    public bool IsChunkReady(Vector2Int coord)
    {
        if (!activeChunks.TryGetValue(coord, out Chunk chunk) || chunk == null)
            return false;

        return chunk.hasVoxelData && !IsChunkJobPending(coord);
    }

    public int SampleSurfaceHeight(int worldX, int worldZ)
    {
        return GetSurfaceHeight(worldX, worldZ);
    }

    public bool IsSolidBlock(BlockType blockType)
    {
        if (blockType == BlockType.Air || FluidBlockUtility.IsWater(blockType))
            return false;

        if (blockData != null)
        {
            BlockTextureMapping? mapping = blockData.GetMapping(blockType);
            if (mapping != null)
                return mapping.Value.isSolid;
        }

        return true;
    }

    public bool IsLiquidBlock(BlockType blockType)
    {
        if (FluidBlockUtility.IsWater(blockType))
            return true;

        return blockData != null && blockData.IsLiquid(blockType);
    }
}
