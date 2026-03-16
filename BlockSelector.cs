using UnityEngine;
using Unity.Mathematics;

[RequireComponent(typeof(LineRenderer))]
public class BlockSelector : MonoBehaviour
{
    public Camera cam;
    public float reach = 6f;
    public BlockType CurrentBlock { get; private set; } = BlockType.Air;
    public Vector3Int CurrentHitNormal { get; private set; } = Vector3Int.zero;
    public bool HasBlock => hasBlock;
    public bool IsBillboardHit { get; private set; }
    public Vector3Int BillboardGroundBlockPos { get; private set; } = new Vector3Int(int.MinValue, 0, 0);

    private LineRenderer line;
    private Vector3Int currentBlock;
    private bool hasBlock;

    void Awake()
    {
        line = GetComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = false;

        line.startWidth = 0.05f;
        line.endWidth = 0.05f;

        line.positionCount = 18;
        line.enabled = false;
        line.numCapVertices = 4;
        line.numCornerVertices = 4;
    }

    void Update()
    {
        UpdateSelection();
    }

    void UpdateSelection()
    {
        if (cam == null || World.Instance == null)
        {
            hasBlock = false;
            line.enabled = false;
            CurrentBlock = BlockType.Air;
            CurrentHitNormal = Vector3Int.zero;
            IsBillboardHit = false;
            BillboardGroundBlockPos = new Vector3Int(int.MinValue, 0, 0);
            return;
        }

        Ray ray = new Ray(cam.transform.position, cam.transform.forward);

        if (TryRaycastVoxel(
            ray,
            reach,
            out Vector3Int blockPos,
            out Vector3Int hitNormal,
            out BlockType hitType,
            out bool isBillboard,
            out Vector3Int billboardGroundPos))
        {
            currentBlock = blockPos;
            CurrentHitNormal = hitNormal;
            CurrentBlock = hitType;
            IsBillboardHit = isBillboard;
            BillboardGroundBlockPos = billboardGroundPos;
            DrawSelection(blockPos, hitType, isBillboard);
            hasBlock = true;
        }
        else
        {
            hasBlock = false;
            line.enabled = false;
            CurrentBlock = BlockType.Air;
            CurrentHitNormal = Vector3Int.zero;
            IsBillboardHit = false;
            BillboardGroundBlockPos = new Vector3Int(int.MinValue, 0, 0);
        }
    }

    void DrawSelection(Vector3Int pos, BlockType blockType, bool isBillboard)
    {
        line.enabled = true;

        float offset = 0.0025f;

        Bounds bounds = new Bounds(pos + Vector3.one * 0.5f, Vector3.one);
        if (!isBillboard && TryGetCustomBounds(pos, blockType, out Bounds customBounds))
            bounds = customBounds;

        Vector3 p = bounds.min - Vector3.one * offset;
        Vector3 size = bounds.size + Vector3.one * offset * 2f;

        Vector3 v000 = p;
        Vector3 v100 = p + Vector3.right * size.x;
        Vector3 v010 = p + Vector3.up * size.y;
        Vector3 v110 = p + new Vector3(size.x, size.y, 0f);

        Vector3 v001 = p + Vector3.forward * size.z;
        Vector3 v101 = p + new Vector3(size.x, 0f, size.z);
        Vector3 v011 = p + new Vector3(0f, size.y, size.z);
        Vector3 v111 = p + size;

        Vector3[] lines = new Vector3[]
        {
            v000, v100, v110, v010, v000,
            v001, v101, v111, v011, v001,
            v000, v001,
            v100, v101,
            v110, v111,
            v010, v011
        };

        line.SetPositions(lines);
    }

    private bool TryGetCustomBounds(Vector3Int pos, BlockType blockType, out Bounds bounds)
    {
        bounds = default;

        World world = World.Instance;
        if (world == null || world.blockData == null)
            return false;

        BlockTextureMapping? mapping = world.blockData.GetMapping(blockType);
        if (mapping == null)
            return false;

        BlockTextureMapping value = mapping.Value;
        if (!BlockShapeUtility.UsesCustomMesh(value))
            return false;

        bounds = BlockShapeUtility.GetWorldBounds(pos, value);
        return true;
    }

    public Vector3Int GetSelectedBlock()
    {
        return hasBlock ? currentBlock : new Vector3Int(int.MinValue, 0, 0);
    }

    public bool TryGetSelectedBlock(out Vector3Int blockPos, out Vector3Int hitNormal)
    {
        if (hasBlock)
        {
            blockPos = currentBlock;
            hitNormal = CurrentHitNormal;
            return true;
        }

        blockPos = default;
        hitNormal = default;
        return false;
    }

    private bool TryRaycastVoxel(
        Ray ray,
        float maxDistance,
        out Vector3Int hitBlock,
        out Vector3Int hitNormal,
        out BlockType hitType,
        out bool isBillboardHit,
        out Vector3Int billboardGroundPos)
    {
        hitBlock = default;
        hitNormal = Vector3Int.zero;
        hitType = BlockType.Air;
        isBillboardHit = false;
        billboardGroundPos = new Vector3Int(int.MinValue, 0, 0);

        Vector3 direction = ray.direction;
        if (direction.sqrMagnitude < 0.0001f)
            return false;

        direction.Normalize();
        Vector3 origin = ray.origin;

        Vector3Int voxel = Vector3Int.FloorToInt(origin);

        int stepX = direction.x > 0f ? 1 : (direction.x < 0f ? -1 : 0);
        int stepY = direction.y > 0f ? 1 : (direction.y < 0f ? -1 : 0);
        int stepZ = direction.z > 0f ? 1 : (direction.z < 0f ? -1 : 0);

        float tMaxX = GetInitialTMax(origin.x, direction.x, voxel.x, stepX);
        float tMaxY = GetInitialTMax(origin.y, direction.y, voxel.y, stepY);
        float tMaxZ = GetInitialTMax(origin.z, direction.z, voxel.z, stepZ);

        float tDeltaX = GetTDelta(direction.x);
        float tDeltaY = GetTDelta(direction.y);
        float tDeltaZ = GetTDelta(direction.z);

        int maxSteps = Mathf.CeilToInt(maxDistance * 4f) + 8;
        float traveled = 0f;
        Vector3Int lastNormal = Vector3Int.zero;

        for (int i = 0; i < maxSteps && traveled <= maxDistance; i++)
        {
            World world = World.Instance;
            BlockType blockType = world.GetBlockAt(voxel);
            if (blockType != BlockType.Air)
            {
                if (TryHitCustomBlock(ray, maxDistance, voxel, blockType, lastNormal, out Vector3Int customNormal))
                {
                    hitBlock = voxel;
                    hitType = blockType;
                    hitNormal = customNormal;
                    isBillboardHit = false;
                    billboardGroundPos = new Vector3Int(int.MinValue, 0, 0);
                    return true;
                }

                if (!IsCustomShapeBlock(blockType))
                {
                    hitBlock = voxel;
                    hitType = blockType;
                    isBillboardHit = false;
                    billboardGroundPos = new Vector3Int(int.MinValue, 0, 0);

                    if (lastNormal == Vector3Int.zero)
                    {
                        hitNormal = new Vector3Int(
                            -Mathf.RoundToInt(direction.x),
                            -Mathf.RoundToInt(direction.y),
                            -Mathf.RoundToInt(direction.z)
                        );

                        if (hitNormal == Vector3Int.zero)
                            hitNormal = Vector3Int.up;
                    }
                    else
                    {
                        hitNormal = lastNormal;
                    }

                    return true;
                }
            }

            if (TryHitGrassBillboardInVoxel(
                ray,
                maxDistance,
                voxel,
                out Vector3Int billboardNormal,
                out Vector3Int groundPos))
            {
                hitBlock = voxel;
                hitNormal = billboardNormal;
                hitType = World.Instance.grassBillboardBlockType;
                isBillboardHit = true;
                billboardGroundPos = groundPos;
                return true;
            }

            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                voxel.x += stepX;
                traveled = tMaxX;
                tMaxX += tDeltaX;
                lastNormal = new Vector3Int(-stepX, 0, 0);
            }
            else if (tMaxY < tMaxZ)
            {
                voxel.y += stepY;
                traveled = tMaxY;
                tMaxY += tDeltaY;
                lastNormal = new Vector3Int(0, -stepY, 0);
            }
            else
            {
                voxel.z += stepZ;
                traveled = tMaxZ;
                tMaxZ += tDeltaZ;
                lastNormal = new Vector3Int(0, 0, -stepZ);
            }
        }

        return false;
    }

    private bool IsCustomShapeBlock(BlockType blockType)
    {
        World world = World.Instance;
        if (world == null || world.blockData == null)
            return false;

        BlockTextureMapping? mapping = world.blockData.GetMapping(blockType);
        return mapping != null && BlockShapeUtility.UsesCustomMesh(mapping.Value);
    }

    private bool TryHitCustomBlock(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        BlockType blockType,
        Vector3Int lastNormal,
        out Vector3Int hitNormal)
    {
        hitNormal = Vector3Int.zero;

        World world = World.Instance;
        if (world == null || world.blockData == null)
            return false;

        BlockTextureMapping? mapping = world.blockData.GetMapping(blockType);
        if (mapping == null || !BlockShapeUtility.UsesCustomMesh(mapping.Value))
            return false;

        BlockTextureMapping value = mapping.Value;
        switch (value.renderShape)
        {
            case BlockRenderShape.Cuboid:
                return TryHitCuboidBlock(ray, maxDistance, voxel, value, lastNormal, out hitNormal);

            case BlockRenderShape.Cross:
                return TryHitCrossBlock(ray, maxDistance, voxel, value, lastNormal, out hitNormal);

            default:
                return false;
        }
    }

    private bool TryHitCuboidBlock(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        BlockTextureMapping mapping,
        Vector3Int lastNormal,
        out Vector3Int hitNormal)
    {
        Bounds bounds = BlockShapeUtility.GetWorldBounds(voxel, mapping);
        if (!bounds.IntersectRay(ray, out float distance) || distance > maxDistance)
        {
            hitNormal = Vector3Int.zero;
            return false;
        }

        Vector3 point = ray.GetPoint(Mathf.Max(0f, distance));
        hitNormal = ResolveBoundsHitNormal(bounds, point, lastNormal, ray.direction);
        return true;
    }

    private bool TryHitCrossBlock(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        BlockTextureMapping mapping,
        Vector3Int lastNormal,
        out Vector3Int hitNormal)
    {
        BlockShapeUtility.ResolveShapeBounds(mapping, out Vector3 min, out Vector3 max);
        Vector3 origin = voxel;

        if (TryHitQuad(
            ray,
            origin + new Vector3(min.x, min.y, min.z),
            origin + new Vector3(max.x, min.y, max.z),
            origin + new Vector3(max.x, max.y, max.z),
            origin + new Vector3(min.x, max.y, min.z),
            maxDistance,
            lastNormal,
            out hitNormal))
        {
            return true;
        }

        return TryHitQuad(
            ray,
            origin + new Vector3(min.x, min.y, max.z),
            origin + new Vector3(max.x, min.y, min.z),
            origin + new Vector3(max.x, max.y, min.z),
            origin + new Vector3(min.x, max.y, max.z),
            maxDistance,
            lastNormal,
            out hitNormal);
    }

    private bool TryHitQuad(
        Ray ray,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        float maxDistance,
        Vector3Int lastNormal,
        out Vector3Int hitNormal)
    {
        hitNormal = Vector3Int.zero;

        bool hitFirst = TryRayTriangle(ray, p0, p1, p2, maxDistance, out float t1, out Vector3 normal1);
        bool hitSecond = TryRayTriangle(ray, p0, p2, p3, maxDistance, out float t2, out Vector3 normal2);

        if (!hitFirst && !hitSecond)
            return false;

        Vector3 normal = normal1;
        if (!hitFirst || (hitSecond && t2 < t1))
            normal = normal2;

        hitNormal = ResolveCustomSurfaceNormal(normal, lastNormal, ray.direction);
        return true;
    }

    private static bool TryRayTriangle(
        Ray ray,
        Vector3 v0,
        Vector3 v1,
        Vector3 v2,
        float maxDistance,
        out float distance,
        out Vector3 normal)
    {
        const float epsilon = 0.000001f;
        distance = 0f;
        normal = Vector3.zero;

        Vector3 edge1 = v1 - v0;
        Vector3 edge2 = v2 - v0;
        Vector3 pvec = Vector3.Cross(ray.direction, edge2);
        float det = Vector3.Dot(edge1, pvec);

        if (Mathf.Abs(det) < epsilon)
            return false;

        float invDet = 1f / det;
        Vector3 tvec = ray.origin - v0;
        float u = Vector3.Dot(tvec, pvec) * invDet;
        if (u < 0f || u > 1f)
            return false;

        Vector3 qvec = Vector3.Cross(tvec, edge1);
        float v = Vector3.Dot(ray.direction, qvec) * invDet;
        if (v < 0f || u + v > 1f)
            return false;

        float t = Vector3.Dot(edge2, qvec) * invDet;
        if (t < 0f || t > maxDistance)
            return false;

        distance = t;
        normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));
        if (Vector3.Dot(normal, ray.direction) > 0f)
            normal = -normal;
        return true;
    }

    private static Vector3Int ResolveBoundsHitNormal(Bounds bounds, Vector3 point, Vector3Int lastNormal, Vector3 rayDirection)
    {
        float dxMin = Mathf.Abs(point.x - bounds.min.x);
        float dxMax = Mathf.Abs(point.x - bounds.max.x);
        float dyMin = Mathf.Abs(point.y - bounds.min.y);
        float dyMax = Mathf.Abs(point.y - bounds.max.y);
        float dzMin = Mathf.Abs(point.z - bounds.min.z);
        float dzMax = Mathf.Abs(point.z - bounds.max.z);

        float best = dxMin;
        Vector3Int normal = Vector3Int.left;

        if (dxMax < best) { best = dxMax; normal = Vector3Int.right; }
        if (dyMin < best) { best = dyMin; normal = Vector3Int.down; }
        if (dyMax < best) { best = dyMax; normal = Vector3Int.up; }
        if (dzMin < best) { best = dzMin; normal = Vector3Int.back; }
        if (dzMax < best) { normal = Vector3Int.forward; }

        if (normal == Vector3Int.zero)
            return ResolveFallbackHitNormal(lastNormal, rayDirection);

        return normal;
    }

    private static Vector3Int ResolveCustomSurfaceNormal(Vector3 surfaceNormal, Vector3Int lastNormal, Vector3 rayDirection)
    {
        Vector3 abs = new Vector3(Mathf.Abs(surfaceNormal.x), Mathf.Abs(surfaceNormal.y), Mathf.Abs(surfaceNormal.z));
        if (abs.x >= abs.y && abs.x >= abs.z)
            return surfaceNormal.x >= 0f ? Vector3Int.right : Vector3Int.left;
        if (abs.y >= abs.x && abs.y >= abs.z)
            return surfaceNormal.y >= 0f ? Vector3Int.up : Vector3Int.down;
        if (abs.z >= abs.x && abs.z >= abs.y)
            return surfaceNormal.z >= 0f ? Vector3Int.forward : Vector3Int.back;

        return ResolveFallbackHitNormal(lastNormal, rayDirection);
    }

    private static Vector3Int ResolveFallbackHitNormal(Vector3Int lastNormal, Vector3 rayDirection)
    {
        if (lastNormal != Vector3Int.zero)
            return lastNormal;

        Vector3 fallback = -rayDirection;
        float absX = Mathf.Abs(fallback.x);
        float absY = Mathf.Abs(fallback.y);
        float absZ = Mathf.Abs(fallback.z);

        if (absX >= absY && absX >= absZ)
            return fallback.x >= 0f ? Vector3Int.right : Vector3Int.left;
        if (absY >= absX && absY >= absZ)
            return fallback.y >= 0f ? Vector3Int.up : Vector3Int.down;
        return fallback.z >= 0f ? Vector3Int.forward : Vector3Int.back;
    }

    private bool TryHitGrassBillboardInVoxel(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        out Vector3Int hitNormal,
        out Vector3Int groundPos)
    {
        hitNormal = Vector3Int.zero;
        groundPos = new Vector3Int(int.MinValue, 0, 0);

        World world = World.Instance;
        if (world == null || !world.enableGrassBillboards || world.grassBillboardChance <= 0f)
            return false;

        if (voxel.y <= 0)
            return false;

        if (world.GetBlockAt(voxel) != BlockType.Air)
            return false;

        if (world.IsGrassBillboardSuppressed(voxel))
            return false;

        Vector3Int groundCandidate = new Vector3Int(voxel.x, voxel.y - 1, voxel.z);
        if (world.GetBlockAt(groundCandidate) != BlockType.Grass)
            return false;

        int worldX = voxel.x;
        int worldZ = voxel.z;
        int py = voxel.y;

        float noiseScale = math.max(1e-4f, world.grassBillboardNoiseScale);
        float jitter = math.clamp(world.grassBillboardJitter, 0f, 0.35f);

        float n = noise.snoise(new float2(
            (worldX + 123.17f) * noiseScale,
            (worldZ - 91.73f) * noiseScale
        )) * 0.5f + 0.5f;

        float effectiveChance = math.saturate(
            world.grassBillboardChance * math.lerp(0.35f, 1.65f, n)
        );

        uint h = math.hash(new int3(worldX, py, worldZ));
        float chance = (h & 0x00FFFFFF) / 16777215f;
        if (chance > effectiveChance)
            return false;

        uint h2 = math.hash(new int3(worldX * 17 + 3, py * 31 + 5, worldZ * 13 + 7));
        float jx = ((((h2 >> 8) & 0xFF) / 255f) * 2f - 1f) * jitter;
        float jz = ((((h2 >> 16) & 0xFF) / 255f) * 2f - 1f) * jitter;

        Vector3 center = new Vector3(worldX + 0.5f + jx, py - 0.02f, worldZ + 0.5f + jz);
        float height = world.grassBillboardHeight;
        const float halfWidth = 0.38f;

        if (RayHitsBillboardPlane(ray, center, height, halfWidth, new Vector3(1f, 0f, -1f), new Vector3(1f, 0f, 1f), maxDistance) ||
            RayHitsBillboardPlane(ray, center, height, halfWidth, new Vector3(1f, 0f, 1f), new Vector3(1f, 0f, -1f), maxDistance))
        {
            Vector3 fallback = -ray.direction;
            hitNormal = new Vector3Int(
                Mathf.RoundToInt(Mathf.Sign(fallback.x)),
                0,
                Mathf.RoundToInt(Mathf.Sign(fallback.z))
            );

            if (hitNormal == Vector3Int.zero)
                hitNormal = Vector3Int.up;

            groundPos = groundCandidate;
            return true;
        }

        return false;
    }

    private static bool RayHitsBillboardPlane(
        Ray ray,
        Vector3 center,
        float height,
        float halfWidth,
        Vector3 planeNormalRaw,
        Vector3 axisRaw,
        float maxDistance)
    {
        Vector3 planeNormal = planeNormalRaw.normalized;
        Vector3 axis = axisRaw.normalized;

        float denom = Vector3.Dot(ray.direction, planeNormal);
        if (Mathf.Abs(denom) < 0.0001f)
            return false;

        float t = Vector3.Dot(center - ray.origin, planeNormal) / denom;
        if (t < 0f || t > maxDistance)
            return false;

        Vector3 p = ray.origin + ray.direction * t;
        float minY = center.y;
        float maxY = center.y + height;
        if (p.y < minY || p.y > maxY)
            return false;

        float along = Vector3.Dot(p - center, axis);
        float halfLen = halfWidth * Mathf.Sqrt(2f);
        return Mathf.Abs(along) <= halfLen;
    }

    private static float GetInitialTMax(float originAxis, float directionAxis, int voxelAxis, int stepAxis)
    {
        if (stepAxis == 0)
            return float.PositiveInfinity;

        float nextBoundary = stepAxis > 0 ? (voxelAxis + 1f) : voxelAxis;
        return (nextBoundary - originAxis) / directionAxis;
    }

    private static float GetTDelta(float directionAxis)
    {
        if (Mathf.Abs(directionAxis) < 0.0001f)
            return float.PositiveInfinity;

        return Mathf.Abs(1f / directionAxis);
    }
}
