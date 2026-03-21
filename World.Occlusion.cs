using System.Collections.Generic;
using UnityEngine;

public partial class World : MonoBehaviour
{
    [Header("Minecraft Section Occlusion")]
    [Tooltip("Replica o culling incremental por secoes 16x16x16 do Minecraft Java para esconder cavernas desconectadas da camera.")]
    public bool enableMinecraftSectionOcclusion = true;

    [Tooltip("Replica o limite vertical do grafo de oclusao do Minecraft (usa renderDistance secoes acima/abaixo da camera).")]
    public bool matchMinecraftVerticalOcclusionDistance = true;

    [Tooltip("Replica o teste adicional de visibilidade a longa distancia usado pelo SectionOcclusionGraph do Minecraft.")]
    public bool enableMinecraftAdvancedOcclusionRay = true;

    [Tooltip("Usa Camera.main quando disponivel; se nao existir, tenta uma camera filha do player.")]
    public bool preferMainCameraForOcclusion = true;

    [Header("Minecraft Section Occlusion Performance")]
    [Min(32)]
    [Tooltip("Limita quantas secoes o BFS de oclusao processa por frame para evitar picos de CPU durante streaming de chunks.")]
    public int sectionOcclusionPropagationBudgetPerFrame = 512;

    private bool sectionOcclusionGraphDirty = true;
    private bool sectionOcclusionRebuildInProgress;
    private bool sectionOcclusionAllVisibleApplied = true;
    private bool lastEnableMinecraftSectionOcclusion = true;
    private bool lastMatchMinecraftVerticalOcclusionDistance = true;
    private bool lastEnableMinecraftAdvancedOcclusionRay = true;
    private Vector3Int lastOcclusionCameraSection = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
    private Camera cachedOcclusionCamera;
    private Vector3 sectionOcclusionBuildCameraPosition;
    private Vector3Int sectionOcclusionBuildCameraSection = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
    private Vector3 sectionOcclusionBuildCameraSectionCenter;

    private readonly Queue<SectionOcclusionNode> sectionOcclusionQueue = new Queue<SectionOcclusionNode>();
    private readonly Dictionary<Vector3Int, SectionOcclusionNode> sectionOcclusionNodes = new Dictionary<Vector3Int, SectionOcclusionNode>();
    private readonly HashSet<Vector3Int> sectionOcclusionVisibleSections = new HashSet<Vector3Int>();
    private readonly HashSet<Vector3Int> sectionOcclusionAppliedVisibleSections = new HashSet<Vector3Int>();
    private readonly List<SectionOcclusionNode> sectionOcclusionSeedBuffer = new List<SectionOcclusionNode>();
    private readonly List<Vector3Int> sectionOcclusionVisibilityDiffBuffer = new List<Vector3Int>();

    private struct SectionOcclusionNode
    {
        public Vector2Int chunkCoord;
        public int subchunkIndex;
        public byte sourceDirections;
        public byte directions;
        public int step;

        public SectionOcclusionNode(Vector2Int chunkCoord, int subchunkIndex, int sourceDirection, int step)
        {
            this.chunkCoord = chunkCoord;
            this.subchunkIndex = subchunkIndex;
            this.sourceDirections = 0;
            this.directions = 0;
            this.step = step;

            if (sourceDirection >= 0)
                AddSourceDirection(sourceDirection);
        }

        public void SetDirections(byte inheritedDirections, int direction)
        {
            directions = (byte)(directions | inheritedDirections | (1 << direction));
        }

        public bool HasDirection(int direction)
        {
            return (directions & (1 << direction)) != 0;
        }

        public void AddSourceDirection(int direction)
        {
            sourceDirections = (byte)(sourceDirections | (1 << direction));
        }

        public bool HasSourceDirection(int direction)
        {
            return (sourceDirections & (1 << direction)) != 0;
        }

        public bool HasSourceDirections()
        {
            return sourceDirections != 0;
        }
    }

    private void InvalidateSectionOcclusionGraph()
    {
        sectionOcclusionGraphDirty = true;
    }

    private void UpdateSectionOcclusionVisibility()
    {
        bool togglesChanged = lastEnableMinecraftSectionOcclusion != enableMinecraftSectionOcclusion ||
                              lastMatchMinecraftVerticalOcclusionDistance != matchMinecraftVerticalOcclusionDistance ||
                              lastEnableMinecraftAdvancedOcclusionRay != enableMinecraftAdvancedOcclusionRay;

        lastEnableMinecraftSectionOcclusion = enableMinecraftSectionOcclusion;
        lastMatchMinecraftVerticalOcclusionDistance = matchMinecraftVerticalOcclusionDistance;
        lastEnableMinecraftAdvancedOcclusionRay = enableMinecraftAdvancedOcclusionRay;

        if (!enableMinecraftSectionOcclusion)
        {
            CancelSectionOcclusionRebuild();
            EnsureAllSubchunksVisibleApplied();
            sectionOcclusionGraphDirty = true;
            lastOcclusionCameraSection = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
            return;
        }

        Camera visibilityCamera = GetOcclusionCamera();
        if (visibilityCamera == null)
        {
            CancelSectionOcclusionRebuild();
            EnsureAllSubchunksVisibleApplied();
            sectionOcclusionGraphDirty = true;
            return;
        }

        Vector3 cameraPosition = visibilityCamera.transform.position;
        Vector3Int cameraSection = GetSectionCoordFromWorld(cameraPosition);
        bool cameraSectionChanged = cameraSection != lastOcclusionCameraSection;
        if (cameraSectionChanged)
        {
            lastOcclusionCameraSection = cameraSection;
            sectionOcclusionGraphDirty = true;
        }

        if (togglesChanged)
            sectionOcclusionGraphDirty = true;

        if (sectionOcclusionGraphDirty &&
            (!sectionOcclusionRebuildInProgress || cameraSectionChanged || togglesChanged))
        {
            BeginSectionOcclusionVisibilityRebuild(cameraPosition, cameraSection);
        }

        if (sectionOcclusionRebuildInProgress)
            ProcessSectionOcclusionVisibilityRebuild();
    }

    private Camera GetOcclusionCamera()
    {
        if (preferMainCameraForOcclusion)
        {
            Camera mainCamera = Camera.main;
            if (mainCamera != null)
            {
                cachedOcclusionCamera = mainCamera;
                return mainCamera;
            }
        }

        if (cachedOcclusionCamera != null)
            return cachedOcclusionCamera;

        if (player == null)
            return null;

        cachedOcclusionCamera = player.GetComponentInChildren<Camera>();
        return cachedOcclusionCamera;
    }

    private void BeginSectionOcclusionVisibilityRebuild(Vector3 cameraPosition, Vector3Int cameraSection)
    {
        sectionOcclusionQueue.Clear();
        sectionOcclusionNodes.Clear();
        sectionOcclusionVisibleSections.Clear();
        sectionOcclusionSeedBuffer.Clear();

        sectionOcclusionBuildCameraPosition = cameraPosition;
        sectionOcclusionBuildCameraSection = cameraSection;
        sectionOcclusionBuildCameraSectionCenter = new Vector3(
            cameraSection.x * Chunk.SizeX + Chunk.SizeX * 0.5f,
            cameraSection.y * Chunk.SubchunkHeight + Chunk.SubchunkHeight * 0.5f,
            cameraSection.z * Chunk.SizeZ + Chunk.SizeZ * 0.5f);

        sectionOcclusionGraphDirty = false;

        if (!InitializeSectionOcclusionQueue(cameraPosition, cameraSection))
        {
            sectionOcclusionRebuildInProgress = false;
            EnsureAllSubchunksVisibleApplied();
            return;
        }

        sectionOcclusionRebuildInProgress = true;
    }

    private void ProcessSectionOcclusionVisibilityRebuild()
    {
        int budget = Mathf.Max(32, sectionOcclusionPropagationBudgetPerFrame);
        while (budget-- > 0 && sectionOcclusionQueue.Count > 0)
        {
            SectionOcclusionNode node = sectionOcclusionQueue.Dequeue();
            ProcessSectionOcclusionNode(node, sectionOcclusionBuildCameraPosition, sectionOcclusionBuildCameraSection, sectionOcclusionBuildCameraSectionCenter);
        }

        if (sectionOcclusionQueue.Count > 0)
            return;

        sectionOcclusionRebuildInProgress = false;
        ApplySectionOcclusionVisibility();
    }

    private bool InitializeSectionOcclusionQueue(Vector3 cameraPosition, Vector3Int cameraSection)
    {
        Vector2Int cameraChunkCoord = new Vector2Int(cameraSection.x, cameraSection.z);
        if (TryGetLoadedSection(cameraChunkCoord, cameraSection.y, out _))
        {
            EnqueueSectionOcclusionNode(new SectionOcclusionNode(cameraChunkCoord, cameraSection.y, -1, 0));
            return sectionOcclusionQueue.Count > 0;
        }

        bool isBelowWorld = cameraSection.y < 0;
        int seedSubchunk = isBelowWorld ? 0 : Chunk.SubchunksPerColumn - 1;
        int sourceDirection = isBelowWorld ? SubchunkOcclusion.Up : SubchunkOcclusion.Down;

        for (int dx = -renderDistance; dx <= renderDistance; dx++)
        {
            for (int dz = -renderDistance; dz <= renderDistance; dz++)
            {
                Vector2Int coord = new Vector2Int(cameraChunkCoord.x + dx, cameraChunkCoord.y + dz);
                if (!TryGetLoadedSection(coord, seedSubchunk, out _))
                    continue;

                SectionOcclusionNode node = new SectionOcclusionNode(coord, seedSubchunk, sourceDirection, 0);
                node.SetDirections(node.directions, sourceDirection);

                if (dx > 0)
                    node.SetDirections(node.directions, SubchunkOcclusion.East);
                else if (dx < 0)
                    node.SetDirections(node.directions, SubchunkOcclusion.West);

                if (dz > 0)
                    node.SetDirections(node.directions, SubchunkOcclusion.South);
                else if (dz < 0)
                    node.SetDirections(node.directions, SubchunkOcclusion.North);

                sectionOcclusionSeedBuffer.Add(node);
            }
        }

        sectionOcclusionSeedBuffer.Sort((a, b) =>
            (GetSectionCenter(a.chunkCoord, a.subchunkIndex) - cameraPosition).sqrMagnitude
                .CompareTo((GetSectionCenter(b.chunkCoord, b.subchunkIndex) - cameraPosition).sqrMagnitude));

        for (int i = 0; i < sectionOcclusionSeedBuffer.Count; i++)
            EnqueueSectionOcclusionNode(sectionOcclusionSeedBuffer[i]);

        return sectionOcclusionQueue.Count > 0;
    }

    private void ProcessSectionOcclusionNode(SectionOcclusionNode node, Vector3 cameraPosition, Vector3Int cameraSection, Vector3 cameraSectionCenter)
    {
        if (!TryGetLoadedSection(node.chunkCoord, node.subchunkIndex, out Chunk chunk))
            return;

        Subchunk subchunk = chunk.subchunks != null &&
                            node.subchunkIndex >= 0 &&
                            node.subchunkIndex < chunk.subchunks.Length
            ? chunk.subchunks[node.subchunkIndex]
            : null;

        if (subchunk != null && subchunk.hasGeometry)
            sectionOcclusionVisibleSections.Add(GetSectionKey(node.chunkCoord, node.subchunkIndex));

        if (!chunk.TryGetSubchunkVisibilityData(node.subchunkIndex, out ulong visibilityMask))
            return;

        bool distantFromCamera =
            Mathf.Abs(node.chunkCoord.x - cameraSection.x) > SubchunkOcclusion.MinimumAdvancedCullingSectionDistance ||
            Mathf.Abs(node.subchunkIndex - cameraSection.y) > SubchunkOcclusion.MinimumAdvancedCullingSectionDistance ||
            Mathf.Abs(node.chunkCoord.y - cameraSection.z) > SubchunkOcclusion.MinimumAdvancedCullingSectionDistance;

        for (int face = 0; face < SubchunkOcclusion.FaceCount; face++)
        {
            if (!TryGetNeighborSection(node.chunkCoord, node.subchunkIndex, face, cameraSection, out Vector2Int neighborChunkCoord, out int neighborSubchunkIndex))
                continue;

            if (node.HasDirection(SubchunkOcclusion.GetOppositeFace(face)))
                continue;

            if (node.HasSourceDirections())
            {
                bool visible = false;
                for (int sourceFace = 0; sourceFace < SubchunkOcclusion.FaceCount; sourceFace++)
                {
                    if (!node.HasSourceDirection(sourceFace))
                        continue;

                    if (SubchunkOcclusion.FacesCanSeeEachOther(visibilityMask, SubchunkOcclusion.GetOppositeFace(sourceFace), face))
                    {
                        visible = true;
                        break;
                    }
                }

                if (!visible)
                    continue;
            }

            if (enableMinecraftAdvancedOcclusionRay &&
                distantFromCamera &&
                !PassesAdvancedOcclusionRay(node.chunkCoord, node.subchunkIndex, face, cameraPosition, cameraSectionCenter))
            {
                continue;
            }

            Vector3Int neighborKey = GetSectionKey(neighborChunkCoord, neighborSubchunkIndex);
            if (sectionOcclusionNodes.TryGetValue(neighborKey, out SectionOcclusionNode existingNode))
            {
                existingNode.AddSourceDirection(face);
                sectionOcclusionNodes[neighborKey] = existingNode;
                continue;
            }

            SectionOcclusionNode newNode = new SectionOcclusionNode(neighborChunkCoord, neighborSubchunkIndex, face, node.step + 1);
            newNode.SetDirections(node.directions, face);
            EnqueueSectionOcclusionNode(newNode);
        }
    }

    private bool PassesAdvancedOcclusionRay(Vector2Int chunkCoord, int subchunkIndex, int face, Vector3 cameraPosition, Vector3 cameraSectionCenter)
    {
        float originX = chunkCoord.x * Chunk.SizeX;
        float originY = subchunkIndex * Chunk.SubchunkHeight;
        float originZ = chunkCoord.y * Chunk.SizeZ;

        bool maxX = (face == SubchunkOcclusion.West || face == SubchunkOcclusion.East)
            ? cameraSectionCenter.x > originX
            : cameraSectionCenter.x < originX;
        bool maxY = (face == SubchunkOcclusion.Down || face == SubchunkOcclusion.Up)
            ? cameraSectionCenter.y > originY
            : cameraSectionCenter.y < originY;
        bool maxZ = (face == SubchunkOcclusion.North || face == SubchunkOcclusion.South)
            ? cameraSectionCenter.z > originZ
            : cameraSectionCenter.z < originZ;

        Vector3 checkPosition = new Vector3(
            originX + (maxX ? Chunk.SizeX : 0f),
            originY + (maxY ? Chunk.SubchunkHeight : 0f),
            originZ + (maxZ ? Chunk.SizeZ : 0f));

        Vector3 step = (cameraPosition - checkPosition).normalized * SubchunkOcclusion.CeiledSectionDiagonal;
        if (step.sqrMagnitude <= Mathf.Epsilon)
            return true;

        while ((checkPosition - cameraPosition).sqrMagnitude > 3600f)
        {
            checkPosition += step;
            if (checkPosition.y >= Chunk.SizeY || checkPosition.y < 0f)
                break;

            Vector3Int checkSection = GetSectionCoordFromWorld(checkPosition);
            if (!sectionOcclusionNodes.ContainsKey(GetSectionKey(new Vector2Int(checkSection.x, checkSection.z), checkSection.y)))
                return false;
        }

        return true;
    }

    private void EnqueueSectionOcclusionNode(SectionOcclusionNode node)
    {
        Vector3Int key = GetSectionKey(node.chunkCoord, node.subchunkIndex);
        if (sectionOcclusionNodes.ContainsKey(key))
            return;

        sectionOcclusionNodes.Add(key, node);
        sectionOcclusionQueue.Enqueue(node);
    }

    private bool TryGetNeighborSection(Vector2Int chunkCoord, int subchunkIndex, int face, Vector3Int cameraSection, out Vector2Int neighborChunkCoord, out int neighborSubchunkIndex)
    {
        if (!SubchunkOcclusion.TryStep(chunkCoord, subchunkIndex, face, out neighborChunkCoord, out neighborSubchunkIndex))
            return false;

        if (neighborSubchunkIndex < 0 || neighborSubchunkIndex >= Chunk.SubchunksPerColumn)
            return false;

        Vector2Int cameraChunkCoord = new Vector2Int(cameraSection.x, cameraSection.z);
        if (!IsCoordInsideRenderDistance(neighborChunkCoord, cameraChunkCoord))
            return false;

        if (matchMinecraftVerticalOcclusionDistance &&
            Mathf.Abs(neighborSubchunkIndex - cameraSection.y) > renderDistance)
        {
            return false;
        }

        return TryGetLoadedSection(neighborChunkCoord, neighborSubchunkIndex, out _);
    }

    private bool TryGetLoadedSection(Vector2Int chunkCoord, int subchunkIndex, out Chunk chunk)
    {
        if (subchunkIndex < 0 || subchunkIndex >= Chunk.SubchunksPerColumn)
        {
            chunk = null;
            return false;
        }

        if (!activeChunks.TryGetValue(chunkCoord, out chunk) || chunk == null || !chunk.HasInitializedSubchunks)
            return false;

        return true;
    }

    private Vector3Int GetSectionCoordFromWorld(Vector3 worldPosition)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPosition.x / Chunk.SizeX),
            Mathf.FloorToInt(worldPosition.y / Chunk.SubchunkHeight),
            Mathf.FloorToInt(worldPosition.z / Chunk.SizeZ));
    }

    private Vector3Int GetSectionKey(Vector2Int chunkCoord, int subchunkIndex)
    {
        return new Vector3Int(chunkCoord.x, subchunkIndex, chunkCoord.y);
    }

    private Vector3 GetSectionCenter(Vector2Int chunkCoord, int subchunkIndex)
    {
        return new Vector3(
            chunkCoord.x * Chunk.SizeX + Chunk.SizeX * 0.5f,
            subchunkIndex * Chunk.SubchunkHeight + Chunk.SubchunkHeight * 0.5f,
            chunkCoord.y * Chunk.SizeZ + Chunk.SizeZ * 0.5f);
    }

    private void ApplySectionOcclusionVisibility()
    {
        sectionOcclusionVisibilityDiffBuffer.Clear();

        foreach (Vector3Int key in sectionOcclusionAppliedVisibleSections)
        {
            if (!sectionOcclusionVisibleSections.Contains(key))
                sectionOcclusionVisibilityDiffBuffer.Add(key);
        }

        for (int i = 0; i < sectionOcclusionVisibilityDiffBuffer.Count; i++)
            SetSectionVisibility(sectionOcclusionVisibilityDiffBuffer[i], false);

        sectionOcclusionVisibilityDiffBuffer.Clear();

        foreach (Vector3Int key in sectionOcclusionVisibleSections)
        {
            if (!sectionOcclusionAppliedVisibleSections.Contains(key))
                sectionOcclusionVisibilityDiffBuffer.Add(key);
        }

        for (int i = 0; i < sectionOcclusionVisibilityDiffBuffer.Count; i++)
            SetSectionVisibility(sectionOcclusionVisibilityDiffBuffer[i], true);

        sectionOcclusionAppliedVisibleSections.Clear();
        foreach (Vector3Int key in sectionOcclusionVisibleSections)
            sectionOcclusionAppliedVisibleSections.Add(key);

        sectionOcclusionAllVisibleApplied = false;
    }

    private void ApplyCachedSectionVisibility(Vector2Int chunkCoord, int subchunkIndex, Subchunk subchunk)
    {
        if (subchunk == null)
            return;

        if (!enableMinecraftSectionOcclusion || sectionOcclusionAllVisibleApplied)
        {
            subchunk.SetVisible(true);
            return;
        }

        subchunk.SetVisible(sectionOcclusionAppliedVisibleSections.Contains(GetSectionKey(chunkCoord, subchunkIndex)));
    }

    private void SetSectionVisibility(Vector3Int key, bool visible)
    {
        Vector2Int chunkCoord = new Vector2Int(key.x, key.z);
        if (!TryGetLoadedSection(chunkCoord, key.y, out Chunk chunk) ||
            chunk.subchunks == null ||
            key.y < 0 ||
            key.y >= chunk.subchunks.Length)
        {
            return;
        }

        Subchunk subchunk = chunk.subchunks[key.y];
        if (subchunk != null)
            subchunk.SetVisible(visible);
    }

    private void EnsureAllSubchunksVisibleApplied()
    {
        if (sectionOcclusionAllVisibleApplied)
            return;

        SetAllSubchunkVisibility(true);
        sectionOcclusionVisibleSections.Clear();
        sectionOcclusionAppliedVisibleSections.Clear();
        sectionOcclusionAllVisibleApplied = true;
    }

    private void CancelSectionOcclusionRebuild()
    {
        sectionOcclusionRebuildInProgress = false;
        sectionOcclusionQueue.Clear();
        sectionOcclusionNodes.Clear();
        sectionOcclusionVisibleSections.Clear();
        sectionOcclusionSeedBuffer.Clear();
        sectionOcclusionVisibilityDiffBuffer.Clear();
    }

    private void SetAllSubchunkVisibility(bool visible)
    {
        foreach (var kv in activeChunks)
        {
            Chunk chunk = kv.Value;
            if (chunk == null || chunk.subchunks == null)
                continue;

            for (int sub = 0; sub < chunk.subchunks.Length; sub++)
            {
                Subchunk subchunk = chunk.subchunks[sub];
                if (subchunk == null)
                    continue;

                subchunk.SetVisible(visible);
            }
        }
    }
}
