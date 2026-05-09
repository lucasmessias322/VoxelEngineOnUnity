using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

public partial class World
{
    #region Runtime Settings

    private int GetEffectiveSimulationDistance()
    {
        return Mathf.Clamp(simulationDistance, 0, Mathf.Max(0, renderDistance));
    }

    public int GetEffectiveSimulationDistanceInChunks()
    {
        return GetEffectiveSimulationDistance();
    }

    public bool IsChunkInsidePlayerSimulationDistance(Vector2Int coord)
    {
        Vector2Int center = GetCurrentPlayerChunkCoord();
        if (center == InvalidChunkCoord)
            return true;

        return IsCoordInsideSimulationDistance(coord, center);
    }

    public bool IsWorldPositionInsidePlayerSimulationDistance(Vector3Int worldPos)
    {
        return IsChunkInsidePlayerSimulationDistance(GetChunkCoordFromWorldXZ(worldPos.x, worldPos.z));
    }

    private int GetEffectiveColliderDistance()
    {
        return Mathf.Clamp(colliderDistance, 0, Mathf.Max(0, renderDistance));
    }

    public void SetRenderDistance(int value)
    {
        int clampedDistance = Mathf.Clamp(value, MinRenderDistance, MaxRenderDistance);
        if (renderDistance == clampedDistance)
            return;

        renderDistance = clampedDistance;
        GameSettingsStorage.SetRenderDistance(renderDistance);
        simulationDistance = Mathf.Clamp(simulationDistance, 0, renderDistance);
        _lastChunkCoord = InvalidChunkCoord;
        pendingJobPrioritiesDirty = true;
    }

    public WorldMaterialProfile GetMaterialProfile()
    {
        return materialProfile;
    }

    public TreeLeafQualityMode GetTreeLeafQuality()
    {
        return treeLeafQuality;
    }

    public void SetMaterialProfile(WorldMaterialProfile profile)
    {
        if (materialProfile == profile)
            return;

        materialProfile = profile;
        GameSettingsStorage.SetMaterialProfile(materialProfile);
        lastWorldMaterialProfileHash = ComputeWorldMaterialProfileHash();
        RefreshWorldMaterialProfileOnRenderers();
        RefreshRuntimeMaterialProfileConsumers();
    }

    public void SetTreeLeafQuality(TreeLeafQualityMode quality)
    {
        if (treeLeafQuality == quality)
            return;

        treeLeafQuality = quality;
        GameSettingsStorage.SetTreeLeafQuality(treeLeafQuality);
        lastTreeLeafQuality = treeLeafQuality;
        lastTreeLeafFoliageSettingsHash = ComputeTreeLeafFoliageSettingsHash();

        if (!Application.isPlaying || activeChunks == null || activeChunks.Count == 0)
            return;

        loadedChunkCoordsBuffer.Clear();
        foreach (var kv in activeChunks)
            loadedChunkCoordsBuffer.Add(kv.Key);

        for (int i = 0; i < loadedChunkCoordsBuffer.Count; i++)
            RequestFullChunkRebuild(loadedChunkCoordsBuffer[i], false);
    }

    private void RefreshRuntimeMaterialProfileConsumers()
    {
        HeldBlockVisual[] heldBlockVisuals = FindObjectsByType<HeldBlockVisual>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < heldBlockVisuals.Length; i++)
        {
            if (heldBlockVisuals[i] != null)
                heldBlockVisuals[i].RefreshNow();
        }

        BlockDrop[] blockDrops = FindObjectsByType<BlockDrop>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < blockDrops.Length; i++)
        {
            if (blockDrops[i] != null)
                blockDrops[i].RefreshVisualMaterial();
        }
    }

    private static bool IsCoordInsideCircularDistance(Vector2Int coord, Vector2Int center, int distanceInChunks)
    {
        int clampedDistance = Mathf.Max(0, distanceInChunks);
        int dx = coord.x - center.x;
        int dz = coord.y - center.y;
        return dx * dx + dz * dz <= clampedDistance * clampedDistance;
    }

    private static bool IsCoordInsideDistance(Vector2Int coord, Vector2Int center, int distanceInChunks)
    {
        int clampedDistance = Mathf.Max(0, distanceInChunks);
        int dx = Mathf.Abs(coord.x - center.x);
        int dz = Mathf.Abs(coord.y - center.y);
        return dx <= clampedDistance && dz <= clampedDistance;
    }

    private bool IsCoordInsideSimulationDistance(Vector2Int coord, Vector2Int center)
    {
        return IsCoordInsideDistance(coord, center, GetEffectiveSimulationDistance());
    }

    private bool IsCoordInsideColliderDistance(Vector2Int coord, Vector2Int center)
    {
        return IsCoordInsideDistance(coord, center, GetEffectiveColliderDistance());
    }

    #endregion

}
