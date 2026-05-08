using System.Collections.Generic;
using UnityEngine;

public partial class World
{
    [Header("Saplings")]
    public bool enableSaplingGrowth = true;
    [Min(1f)] public float oakSaplingMinGrowSeconds = 90f;
    [Min(1f)] public float oakSaplingMaxGrowSeconds = 180f;
    [Min(1)] public int saplingRequiredClearanceBlocks = 10;
    [Range(0, 15)] public byte saplingRequiredLightLevel = 9;
    [Min(1)] public int saplingGrowthChecksPerFrame = 2;

    private readonly Queue<Vector3Int> queuedSaplingGrowth = new Queue<Vector3Int>(InitialInteractiveBlockLightRefreshCapacity);
    private readonly Dictionary<Vector3Int, float> queuedSaplingGrowTimes = new Dictionary<Vector3Int, float>(InitialInteractiveBlockLightRefreshCapacity);

    private void HandleSaplingBlockChange(Vector3Int worldPos, BlockType previousType, BlockType newType)
    {
        if (previousType == newType)
            return;

        if (SaplingBlockUtility.IsSapling(previousType))
            queuedSaplingGrowTimes.Remove(worldPos);

        if (newType == BlockType.oakTreeSapling)
            QueueOakSaplingGrowth(worldPos);
    }

    private void QueueOakSaplingGrowth(Vector3Int worldPos)
    {
        if (!enableSaplingGrowth)
            return;

        float minDelay = Mathf.Max(1f, oakSaplingMinGrowSeconds);
        float maxDelay = Mathf.Max(minDelay, oakSaplingMaxGrowSeconds);
        float delay = Mathf.Lerp(minDelay, maxDelay, Hash01(worldPos.x, worldPos.y, worldPos.z));
        queuedSaplingGrowTimes[worldPos] = Time.time + delay;
        queuedSaplingGrowth.Enqueue(worldPos);
    }

    private void ProcessQueuedSaplingGrowth()
    {
        if (!enableSaplingGrowth || queuedSaplingGrowth.Count == 0)
            return;

        int processed = 0;
        int attempts = queuedSaplingGrowth.Count;
        int perFrameLimit = Mathf.Max(1, saplingGrowthChecksPerFrame);
        float now = Time.time;

        while (processed < perFrameLimit && attempts-- > 0 && queuedSaplingGrowth.Count > 0)
        {
            Vector3Int pos = queuedSaplingGrowth.Dequeue();
            if (!queuedSaplingGrowTimes.TryGetValue(pos, out float growAt))
                continue;

            if (growAt > now)
            {
                queuedSaplingGrowth.Enqueue(pos);
                continue;
            }

            queuedSaplingGrowTimes.Remove(pos);
            if (GetBlockAt(pos) != BlockType.oakTreeSapling)
                continue;

            if (TryGrowOakSapling(pos))
            {
                processed++;
                continue;
            }

            QueueOakSaplingGrowth(pos);
            processed++;
        }
    }

    private bool TryGrowOakSapling(Vector3Int saplingPos)
    {
        Vector3Int groundPos = saplingPos + Vector3Int.down;
        if (!SaplingBlockUtility.CanPlantOn(GetBlockAt(groundPos)))
            return false;

        if (!HasSaplingGrowthClearance(saplingPos))
            return false;

        if (!HasSaplingGrowthLight(saplingPos))
            return false;

        int treeHash = HashInt(saplingPos.x, saplingPos.y, saplingPos.z);
        int heightLimit = 9 + Mathf.Abs(treeHash % 3);
        int canopyHeight = 4 + ((treeHash >> 5) & 1);
        int canopyRadius = 4;
        HashSet<Vector3Int> logBlocks = new HashSet<Vector3Int>();
        HashSet<Vector3Int> leafBlocks = new HashSet<Vector3Int>();

        if (!BuildRuntimeFancyOakTree(
                saplingPos.x,
                saplingPos.z,
                groundPos.y,
                heightLimit,
                canopyHeight,
                canopyRadius,
                treeHash,
                logBlocks,
                leafBlocks))
        {
            return false;
        }

        if (!CanPlaceRuntimeTree(logBlocks, leafBlocks))
            return false;

        foreach (Vector3Int logPos in logBlocks)
            SetBlockAt(logPos, BlockType.Log);

        foreach (Vector3Int leafPos in leafBlocks)
        {
            if (logBlocks.Contains(leafPos))
                continue;

            TryPlaceRuntimeLeaf(leafPos);
        }

        return true;
    }

    private bool HasSaplingGrowthClearance(Vector3Int saplingPos)
    {
        int clearanceBlocks = Mathf.Max(1, saplingRequiredClearanceBlocks);
        for (int offsetY = 1; offsetY <= clearanceBlocks; offsetY++)
        {
            if (GetBlockAt(saplingPos + Vector3Int.up * offsetY) != BlockType.Air)
                return false;
        }

        return true;
    }

    private bool HasSaplingGrowthLight(Vector3Int saplingPos)
    {
        byte requiredLight = (byte)Mathf.Clamp(saplingRequiredLightLevel, 0, 15);
        if (requiredLight == 0)
            return true;

        if (TryGetRenderedBlockLightAt(saplingPos, out ushort renderedLight))
        {
            if (LightUtils.GetSkyLight(renderedLight) >= requiredLight ||
                LightUtils.GetBlockLight(renderedLight) >= requiredLight)
            {
                return true;
            }
        }

        ushort globalBlockLight = GetGlobalBlockLightAt(saplingPos);
        if (LightUtils.GetBlockLight(globalBlockLight) >= requiredLight)
            return true;

        return IsSaplingOpenToSky(saplingPos);
    }

    private bool IsSaplingOpenToSky(Vector3Int saplingPos)
    {
        for (int y = saplingPos.y + 1; y < Chunk.SizeY; y++)
        {
            BlockType blockType = GetBlockAt(new Vector3Int(saplingPos.x, y, saplingPos.z));
            if (blockType == BlockType.Air)
                continue;

            if (GetBlockOpacity(blockType) > 0)
                return false;
        }

        return true;
    }

    private bool BuildRuntimeFancyOakTree(
        int centerX,
        int centerZ,
        int surfaceY,
        int heightLimitValue,
        int canopyHeight,
        int canopyRadius,
        int treeHash,
        HashSet<Vector3Int> logBlocks,
        HashSet<Vector3Int> leafBlocks)
    {
        int heightLimit = Mathf.Max(6, heightLimitValue);
        int leafDistanceLimit = Mathf.Clamp(Mathf.Max(4, canopyHeight), 4, 5);
        int trunkHeight = Mathf.Max(1, Mathf.FloorToInt(heightLimit * 0.618f));
        if (trunkHeight >= heightLimit)
            trunkHeight = heightLimit - 1;

        int trunkTopY = surfaceY + trunkHeight;
        int topLeafY = surfaceY + heightLimit - leafDistanceLimit;
        float scaleWidth = Mathf.Max(1f, canopyRadius / 4f);
        float leafDensity = Mathf.Max(1f, canopyHeight / 4f);
        float nodeDensity = leafDensity * heightLimit / 13f;
        int nodesPerLayer = Mathf.Max(1, (int)(1.382f + nodeDensity * nodeDensity));
        nodesPerLayer = Mathf.Clamp(nodesPerLayer, 1, 4);

        AddRuntimeFancyOakLeafNode(centerX, topLeafY, centerZ, leafDistanceLimit, leafBlocks);

        int minLeafY = surfaceY + Mathf.CeilToInt(heightLimit * 0.3f);
        for (int layerY = topLeafY - 1; layerY >= minLeafY; layerY--)
        {
            float layerSize = GetRuntimeFancyOakLayerSize(heightLimit, layerY - surfaceY);
            if (layerSize < 0f)
                continue;

            for (int nodeIndex = 0; nodeIndex < nodesPerLayer; nodeIndex++)
            {
                int nodeHash = treeHash ^ (layerY * 73428767) ^ (nodeIndex * 912367421);
                float distance = scaleWidth * layerSize * (0.328f + Hash01(nodeHash));
                float angle = Hash01(nodeHash ^ 1757151723) * Mathf.PI * 2f;

                int nodeX = centerX + Mathf.FloorToInt(distance * Mathf.Sin(angle) + 0.5f);
                int nodeZ = centerZ + Mathf.FloorToInt(distance * Mathf.Cos(angle) + 0.5f);
                int nodeY = layerY;

                if (!IsRuntimeFancyOakLeafNodeClear(nodeX, nodeY, nodeZ, leafDistanceLimit, logBlocks, leafBlocks))
                    continue;

                int dx = nodeX - centerX;
                int dz = nodeZ - centerZ;
                float horizontalDistance = Mathf.Sqrt(dx * dx + dz * dz);
                int branchBaseY = (float)nodeY - horizontalDistance * 0.381f > trunkTopY
                    ? trunkTopY
                    : (int)((float)nodeY - horizontalDistance * 0.381f);
                branchBaseY = Mathf.Clamp(branchBaseY, surfaceY + 1, trunkTopY);

                if (!IsRuntimeWoodLineClear(centerX, branchBaseY, centerZ, nodeX, nodeY, nodeZ, logBlocks, leafBlocks))
                    continue;

                AddRuntimeFancyOakLeafNode(nodeX, nodeY, nodeZ, leafDistanceLimit, leafBlocks);

                if ((float)(nodeY - surfaceY) >= heightLimit * 0.2f)
                {
                    AddRuntimeWoodLine(centerX, branchBaseY, centerZ, nodeX, nodeY, nodeZ, logBlocks);
                }
            }
        }

        AddRuntimeWoodLine(centerX, surfaceY + 1, centerZ, centerX, trunkTopY, centerZ, logBlocks);
        return true;
    }

    private void AddRuntimeFancyOakLeafNode(
        int centerX,
        int centerY,
        int centerZ,
        int leafDistanceLimit,
        HashSet<Vector3Int> leafBlocks)
    {
        for (int yOffset = 0; yOffset < leafDistanceLimit; yOffset++)
        {
            float radius = GetRuntimeFancyOakLeafSize(yOffset, leafDistanceLimit);
            if (radius < 0f)
                continue;

            AddRuntimeFancyOakLeafDisc(centerX, centerY + yOffset, centerZ, radius, leafBlocks);
        }
    }

    private void AddRuntimeFancyOakLeafDisc(int centerX, int layerY, int centerZ, float radius, HashSet<Vector3Int> leafBlocks)
    {
        int discRadius = (int)(radius + 0.618f);
        float radiusSq = radius * radius;

        for (int dx = -discRadius; dx <= discRadius; dx++)
        {
            float absDx = Mathf.Abs(dx) + 0.5f;
            float absDxSq = absDx * absDx;

            for (int dz = -discRadius; dz <= discRadius; dz++)
            {
                float absDz = Mathf.Abs(dz) + 0.5f;
                float distSq = absDxSq + absDz * absDz;
                if (distSq > radiusSq)
                    continue;

                leafBlocks.Add(new Vector3Int(centerX + dx, layerY, centerZ + dz));
            }
        }
    }

    private static float GetRuntimeFancyOakLayerSize(int heightLimit, int layerOffset)
    {
        if ((float)layerOffset < heightLimit * 0.3f)
            return -1.618f;

        float halfHeight = heightLimit * 0.5f;
        float distanceFromCenter = halfHeight - layerOffset;

        if (distanceFromCenter == 0f)
            return halfHeight * 0.5f;
        if (Mathf.Abs(distanceFromCenter) >= halfHeight)
            return 0f;

        return Mathf.Sqrt(halfHeight * halfHeight - distanceFromCenter * distanceFromCenter) * 0.5f;
    }

    private static float GetRuntimeFancyOakLeafSize(int layerOffset, int leafDistanceLimit)
    {
        if (layerOffset < 0 || layerOffset >= leafDistanceLimit)
            return -1f;

        return (layerOffset != 0 && layerOffset != leafDistanceLimit - 1) ? 3f : 2f;
    }

    private bool IsRuntimeFancyOakLeafNodeClear(
        int centerX,
        int centerY,
        int centerZ,
        int leafDistanceLimit,
        HashSet<Vector3Int> logBlocks,
        HashSet<Vector3Int> leafBlocks)
    {
        int maxY = centerY + leafDistanceLimit;
        for (int y = centerY; y <= maxY; y++)
        {
            Vector3Int pos = new Vector3Int(centerX, y, centerZ);
            if (!CanRuntimeLeafReplaceAt(pos, logBlocks, leafBlocks))
                return false;
        }

        return true;
    }

    private bool IsRuntimeWoodLineClear(
        int startX,
        int startY,
        int startZ,
        int endX,
        int endY,
        int endZ,
        HashSet<Vector3Int> logBlocks,
        HashSet<Vector3Int> leafBlocks)
    {
        int dx = endX - startX;
        int dy = endY - startY;
        int dz = endZ - startZ;
        int steps = Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz)));

        if (steps == 0)
            return CanRuntimeWoodReplaceAt(new Vector3Int(startX, startY, startZ), logBlocks, leafBlocks);

        for (int step = 0; step <= steps; step++)
        {
            float t = (float)step / steps;
            int x = Mathf.FloorToInt(startX + dx * t + 0.5f);
            int y = Mathf.FloorToInt(startY + dy * t + 0.5f);
            int z = Mathf.FloorToInt(startZ + dz * t + 0.5f);

            if (!CanRuntimeWoodReplaceAt(new Vector3Int(x, y, z), logBlocks, leafBlocks))
                return false;
        }

        return true;
    }

    private static void AddRuntimeWoodLine(
        int startX,
        int startY,
        int startZ,
        int endX,
        int endY,
        int endZ,
        HashSet<Vector3Int> logBlocks)
    {
        int dx = endX - startX;
        int dy = endY - startY;
        int dz = endZ - startZ;
        int steps = Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz)));

        if (steps == 0)
        {
            logBlocks.Add(new Vector3Int(startX, startY, startZ));
            return;
        }

        for (int step = 0; step <= steps; step++)
        {
            float t = (float)step / steps;
            int x = Mathf.FloorToInt(startX + dx * t + 0.5f);
            int y = Mathf.FloorToInt(startY + dy * t + 0.5f);
            int z = Mathf.FloorToInt(startZ + dz * t + 0.5f);

            logBlocks.Add(new Vector3Int(x, y, z));
        }
    }

    private bool CanPlaceRuntimeTree(HashSet<Vector3Int> logBlocks, HashSet<Vector3Int> leafBlocks)
    {
        foreach (Vector3Int logPos in logBlocks)
        {
            if (!CanRuntimeWoodReplaceAt(logPos, logBlocks, leafBlocks))
                return false;
        }

        foreach (Vector3Int leafPos in leafBlocks)
        {
            if (!CanRuntimeLeafReplaceAt(leafPos, logBlocks, leafBlocks))
                return false;
        }

        return true;
    }

    private bool CanRuntimeLeafReplaceAt(Vector3Int pos, HashSet<Vector3Int> logBlocks, HashSet<Vector3Int> leafBlocks)
    {
        if (pos.y <= 2)
            return false;

        if (leafBlocks.Contains(pos) || logBlocks.Contains(pos))
            return true;

        BlockType existing = GetBlockAt(pos);
        return existing == BlockType.Air ||
               existing == BlockType.Leaves ||
               existing == BlockType.oakTreeSapling;
    }

    private bool CanRuntimeWoodReplaceAt(Vector3Int pos, HashSet<Vector3Int> logBlocks, HashSet<Vector3Int> leafBlocks)
    {
        if (pos.y <= 2)
            return false;

        if (logBlocks.Contains(pos) || leafBlocks.Contains(pos))
            return true;

        BlockType existing = GetBlockAt(pos);
        return existing == BlockType.Air ||
               existing == BlockType.Leaves ||
               existing == BlockType.oakTreeSapling ||
               existing == BlockType.Log;
    }

    private void TryPlaceRuntimeLeaf(Vector3Int pos)
    {
        BlockType existing = GetBlockAt(pos);
        if (existing == BlockType.Air || existing == BlockType.Leaves)
            SetBlockAt(pos, BlockType.Leaves);
    }

    private static float Hash01(int x, int y, int z)
    {
        return (HashInt(x, y, z) & 0x00FFFFFF) / 16777215f;
    }

    private static float Hash01(int value)
    {
        unchecked
        {
            uint x = (uint)value;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0x00FFFFFFu) / 16777215f;
        }
    }

    private static int HashInt(int x, int y, int z)
    {
        unchecked
        {
            int hash = x * 73856093;
            hash ^= y * 83492791;
            hash ^= z * 19349663;
            hash ^= 0x51ED270B;
            hash ^= hash >> 16;
            hash *= unchecked((int)0x7FEB352D);
            hash ^= hash >> 15;
            hash *= unchecked((int)0x846CA68B);
            hash ^= hash >> 16;
            return hash;
        }
    }
}
