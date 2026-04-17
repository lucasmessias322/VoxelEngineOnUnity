using UnityEngine;
using Unity.Mathematics;

[RequireComponent(typeof(LineRenderer))]
public class BlockSelector : MonoBehaviour
{
    public Camera cam;
    public float reach = 6f;
    public BlockType CurrentBlock { get; private set; } = BlockType.Air;
    public Vector3Int CurrentHitNormal { get; private set; } = Vector3Int.zero;
    public Vector3 CurrentHitPoint { get; private set; }
    public bool HasBlock => hasBlock;
    public bool IsBillboardHit { get; private set; }
    public Vector3Int BillboardGroundBlockPos { get; private set; } = new Vector3Int(int.MinValue, 0, 0);

    private LineRenderer line;
    private Vector3Int currentBlock;
    private bool hasBlock;
    private readonly Vector3[] selectionLinePoints = new Vector3[18];

    void Awake()
    {
        line = GetComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.loop = false;

        line.startWidth = 0.025f;
        line.endWidth = 0.025f;

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
            CurrentHitPoint = Vector3.zero;
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
            out Vector3 hitPoint,
            out BlockType hitType,
            out bool isBillboard,
            out Vector3Int billboardGroundPos,
            false))
        {
            currentBlock = blockPos;
            CurrentHitNormal = hitNormal;
            CurrentHitPoint = hitPoint;
            CurrentBlock = hitType;
            IsBillboardHit = isBillboard;
            BillboardGroundBlockPos = billboardGroundPos;
           // DrawSelection(blockPos, hitType, isBillboard);
            hasBlock = true;
        }
        else
        {
            hasBlock = false;
            line.enabled = false;
            CurrentBlock = BlockType.Air;
            CurrentHitNormal = Vector3Int.zero;
            CurrentHitPoint = Vector3.zero;
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

        selectionLinePoints[0] = v000;
        selectionLinePoints[1] = v100;
        selectionLinePoints[2] = v110;
        selectionLinePoints[3] = v010;
        selectionLinePoints[4] = v000;
        selectionLinePoints[5] = v001;
        selectionLinePoints[6] = v101;
        selectionLinePoints[7] = v111;
        selectionLinePoints[8] = v011;
        selectionLinePoints[9] = v001;
        selectionLinePoints[10] = v000;
        selectionLinePoints[11] = v001;
        selectionLinePoints[12] = v100;
        selectionLinePoints[13] = v101;
        selectionLinePoints[14] = v110;
        selectionLinePoints[15] = v111;
        selectionLinePoints[16] = v010;
        selectionLinePoints[17] = v011;

        line.SetPositions(selectionLinePoints);
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

        BlockPlacementAxis placementAxis = world.GetPlacementAxisAt(pos, blockType);
        switch (BlockShapeUtility.GetEffectiveRenderShape(value))
        {
            case BlockRenderShape.Stairs:
            {
                StairShapeVariant variant = StairShapeRuntimeUtility.ResolveShapeVariant(world, pos, (byte)placementAxis);
                StairShapeUtility.ResolveBoxes((byte)placementAxis, variant, out int boxCount, out ShapeBox box0, out ShapeBox box1, out ShapeBox box2, out ShapeBox box3, out ShapeBox box4);
                bounds = box0.ToWorldBounds(pos);
                if (boxCount > 1) bounds.Encapsulate(box1.ToWorldBounds(pos));
                if (boxCount > 2) bounds.Encapsulate(box2.ToWorldBounds(pos));
                if (boxCount > 3) bounds.Encapsulate(box3.ToWorldBounds(pos));
                if (boxCount > 4) bounds.Encapsulate(box4.ToWorldBounds(pos));
                return true;
            }

            case BlockRenderShape.Fence:
            {
                byte connectionMask = FenceShapeUtility.ResolveConnectionMask(world, pos);
                bounds = FenceShapeUtility.GetCenterPostVisualBox().ToWorldBounds(pos);
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectWest))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectWest, false).ToWorldBounds(pos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectWest))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectWest, true).ToWorldBounds(pos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectEast))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectEast, false).ToWorldBounds(pos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectEast))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectEast, true).ToWorldBounds(pos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectSouth))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectSouth, false).ToWorldBounds(pos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectSouth))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectSouth, true).ToWorldBounds(pos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectNorth))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectNorth, false).ToWorldBounds(pos));
                if (FenceShapeUtility.IsFenceConnectionActive(connectionMask, FenceShapeUtility.ConnectNorth))
                    bounds.Encapsulate(FenceShapeUtility.GetRailVisualBox(FenceShapeUtility.ConnectNorth, true).ToWorldBounds(pos));
                return true;
            }

            case BlockRenderShape.MultiCuboid:
                if (BlockShapeUtility.TryGetMultiCuboidBounds(
                    pos,
                    value,
                    world.blockData.runtimeMultiCuboidBoxes,
                    placementAxis,
                    blockType,
                    out bounds))
                {
                    return true;
                }

                break;
        }

        if (BlockShapeUtility.IsFlatShape(value))
        {
            ResolvePlaneSupportFlags(pos, placementAxis, out bool hasNegativeSupport, out bool hasPositiveSupport);
            bounds = BlockShapeUtility.GetWorldBounds(
                pos,
                blockType,
                value,
                placementAxis,
                hasNegativeSupport,
                hasPositiveSupport);
            return true;
        }

        bounds = BlockShapeUtility.GetWorldBounds(pos, blockType, value, placementAxis);
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

    public bool TryGetPlacementTarget(
        out Vector3Int blockPos,
        out Vector3Int hitNormal,
        out Vector3 hitPoint,
        out BlockType blockType,
        out bool isBillboard)
    {
        blockPos = default;
        hitNormal = default;
        hitPoint = default;
        blockType = BlockType.Air;
        isBillboard = false;

        if (cam == null || World.Instance == null)
            return false;

        Ray ray = new Ray(cam.transform.position, cam.transform.forward);
        return TryRaycastVoxel(
            ray,
            reach,
            out blockPos,
            out hitNormal,
            out hitPoint,
            out blockType,
            out isBillboard,
            out _,
            true);
    }

    private bool TryRaycastVoxel(
        Ray ray,
        float maxDistance,
        out Vector3Int hitBlock,
        out Vector3Int hitNormal,
        out Vector3 hitPoint,
        out BlockType hitType,
        out bool isBillboardHit,
        out Vector3Int billboardGroundPos,
        bool ignoreLiquidsForPlacement)
    {
        hitBlock = default;
        hitNormal = Vector3Int.zero;
        hitPoint = Vector3.zero;
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

        World world = World.Instance;
        if (world == null)
            return false;

        for (int i = 0; i < maxSteps && traveled <= maxDistance; i++)
        {
            bool hasLoadedBlock = world.TryGetLoadedBlockAt(voxel, out BlockType blockType);
            bool ignoreLiquidHit = ignoreLiquidsForPlacement && world.IsLiquidBlock(blockType);
            if (blockType != BlockType.Air && !ignoreLiquidHit)
            {
                if (TryHitCustomBlock(ray, maxDistance, voxel, blockType, lastNormal, out Vector3Int customNormal, out Vector3 customPoint))
                {
                    hitBlock = voxel;
                    hitType = blockType;
                    hitNormal = customNormal;
                    hitPoint = customPoint;
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

                    hitPoint = ray.GetPoint(Mathf.Max(0f, traveled));
                    return true;
                }
            }

            if (hasLoadedBlock &&
                TryHitGrassBillboardInVoxel(
                    ray,
                    maxDistance,
                    voxel,
                    out Vector3Int billboardNormal,
                    out Vector3Int groundPos,
                    out BlockType billboardType))
            {
                hitBlock = voxel;
                hitNormal = billboardNormal;
                hitPoint = ray.GetPoint(Mathf.Max(0f, traveled));
                hitType = billboardType;
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
        out Vector3Int hitNormal,
        out Vector3 hitPoint)
    {
        hitNormal = Vector3Int.zero;
        hitPoint = Vector3.zero;

        World world = World.Instance;
        if (world == null || world.blockData == null)
            return false;

        BlockTextureMapping? mapping = world.blockData.GetMapping(blockType);
        if (mapping == null || !BlockShapeUtility.UsesCustomMesh(mapping.Value))
            return false;

        BlockTextureMapping value = mapping.Value;
        BlockPlacementAxis placementAxis = world.GetPlacementAxisAt(voxel, blockType);
        switch (BlockShapeUtility.GetEffectiveRenderShape(value))
        {
            case BlockRenderShape.Cuboid:
                return TryHitCuboidBlock(ray, maxDistance, voxel, blockType, value, placementAxis, lastNormal, out hitNormal, out hitPoint);

            case BlockRenderShape.MultiCuboid:
                return TryHitMultiCuboidBlock(ray, maxDistance, voxel, value, placementAxis, lastNormal, out hitNormal, out hitPoint);

            case BlockRenderShape.Cross:
                return TryHitCrossBlock(ray, maxDistance, voxel, value, lastNormal, out hitNormal, out hitPoint);

            case BlockRenderShape.Plane:
                return TryHitPlaneBlock(ray, maxDistance, voxel, value, placementAxis, lastNormal, out hitNormal, out hitPoint);

            case BlockRenderShape.Stairs:
                return TryHitStairBlock(ray, maxDistance, voxel, blockType, placementAxis, lastNormal, out hitNormal, out hitPoint);

            case BlockRenderShape.Ramp:
                return TryHitRampBlock(ray, maxDistance, voxel, placementAxis, lastNormal, out hitNormal, out hitPoint);

            case BlockRenderShape.VerticalRamp:
                return TryHitVerticalRampBlock(ray, maxDistance, voxel, placementAxis, lastNormal, out hitNormal, out hitPoint);

            case BlockRenderShape.Fence:
                return TryHitFenceBlock(ray, maxDistance, voxel, lastNormal, out hitNormal, out hitPoint);

            default:
                return false;
        }
    }

    private bool TryHitCuboidBlock(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        BlockType blockType,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        Vector3Int lastNormal,
        out Vector3Int hitNormal,
        out Vector3 hitPoint)
    {
        Bounds bounds = BlockShapeUtility.GetWorldBounds(voxel, blockType, mapping, placementAxis);
        if (!bounds.IntersectRay(ray, out float distance) || distance > maxDistance)
        {
            hitNormal = Vector3Int.zero;
            hitPoint = Vector3.zero;
            return false;
        }

        Vector3 point = ray.GetPoint(Mathf.Max(0f, distance));
        hitNormal = ResolveBoundsHitNormal(bounds, point, lastNormal, ray.direction);
        hitPoint = point;
        return true;
    }

    private bool TryHitMultiCuboidBlock(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        Vector3Int lastNormal,
        out Vector3Int hitNormal,
        out Vector3 hitPoint)
    {
        hitNormal = Vector3Int.zero;
        hitPoint = Vector3.zero;

        World world = World.Instance;
        if (world == null || world.blockData == null)
            return false;

        int boxCount = BlockShapeUtility.GetMultiCuboidBoxCount(mapping, world.blockData.runtimeMultiCuboidBoxes);
        if (boxCount <= 0)
        {
            Bounds fallbackBounds = BlockShapeUtility.GetWorldBounds(voxel, mapping.blockType, mapping, placementAxis);
            return TryHitShapeBox(ray, maxDistance, fallbackBounds, lastNormal, out _, out hitNormal, out hitPoint);
        }

        float bestDistance = float.PositiveInfinity;
        bool hit = false;
        for (int i = 0; i < boxCount; i++)
        {
            if (!BlockShapeUtility.TryGetMultiCuboidBox(mapping, world.blockData.runtimeMultiCuboidBoxes, i, placementAxis, mapping.blockType, out ShapeBox box))
                continue;

            if (!TryHitShapeBox(ray, maxDistance, box.ToWorldBounds(voxel), lastNormal, out float distance, out Vector3Int normal, out Vector3 point))
                continue;

            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            hitNormal = normal;
            hitPoint = point;
            hit = true;
        }

        return hit;
    }

    private bool TryHitPlaneBlock(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        BlockTextureMapping mapping,
        BlockPlacementAxis placementAxis,
        Vector3Int lastNormal,
        out Vector3Int hitNormal,
        out Vector3 hitPoint)
    {
        ResolvePlaneSupportFlags(voxel, placementAxis, out bool hasNegativeSupport, out bool hasPositiveSupport);
        BlockShapeUtility.ResolvePlaneQuad(
            mapping,
            placementAxis,
            hasNegativeSupport,
            hasPositiveSupport,
            out Vector3 p0,
            out Vector3 p1,
            out Vector3 p2,
            out Vector3 p3,
            out _,
            out _);
        Vector3 origin = voxel;

        return TryHitQuad(
            ray,
            origin + p0,
            origin + p1,
            origin + p2,
            origin + p3,
            maxDistance,
            lastNormal,
            out hitNormal,
            out hitPoint);
    }

    private void ResolvePlaneSupportFlags(Vector3Int voxel, BlockPlacementAxis placementAxis, out bool hasNegativeSupport, out bool hasPositiveSupport)
    {
        hasNegativeSupport = false;
        hasPositiveSupport = false;

        BlockPlacementAxis axis = BlockPlacementRotationUtility.SanitizeAxis(placementAxis);
        switch (axis)
        {
            case BlockPlacementAxis.X:
                hasNegativeSupport = IsPlaneSupportBlock(voxel + Vector3Int.left);
                hasPositiveSupport = IsPlaneSupportBlock(voxel + Vector3Int.right);
                break;

            case BlockPlacementAxis.Z:
                hasNegativeSupport = IsPlaneSupportBlock(voxel + Vector3Int.back);
                hasPositiveSupport = IsPlaneSupportBlock(voxel + Vector3Int.forward);
                break;
        }
    }

    private bool IsPlaneSupportBlock(Vector3Int voxel)
    {
        World world = World.Instance;
        if (world == null)
            return false;

        if (!world.TryGetLoadedBlockAt(voxel, out BlockType supportType))
            return false;

        if (supportType == BlockType.Air || FluidBlockUtility.IsWater(supportType))
            return false;

        if (world.blockData == null)
            return true;

        BlockTextureMapping? mapping = world.blockData.GetMapping(supportType);
        if (mapping == null)
            return false;

        BlockTextureMapping value = mapping.Value;
        return value.isSolid && !value.isEmpty && !value.isLiquid;
    }

    private bool TryHitCrossBlock(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        BlockTextureMapping mapping,
        Vector3Int lastNormal,
        out Vector3Int hitNormal,
        out Vector3 hitPoint)
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
            out hitNormal,
            out hitPoint))
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
            out hitNormal,
            out hitPoint);
    }

    private bool TryHitStairBlock(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        BlockType blockType,
        BlockPlacementAxis placementAxis,
        Vector3Int lastNormal,
        out Vector3Int hitNormal,
        out Vector3 hitPoint)
    {
        StairShapeVariant variant = StairShapeRuntimeUtility.ResolveShapeVariant(World.Instance, voxel, (byte)placementAxis);
        StairShapeUtility.ResolveBoxes((byte)placementAxis, variant, out int boxCount, out ShapeBox box0, out ShapeBox box1, out ShapeBox box2, out ShapeBox box3, out ShapeBox box4);
        return TryHitShapeBoxes(ray, maxDistance, voxel, lastNormal, out hitNormal, out hitPoint, boxCount, box0, box1, box2, box3, box4);
    }

    private bool TryHitRampBlock(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        BlockPlacementAxis placementAxis,
        Vector3Int lastNormal,
        out Vector3Int hitNormal,
        out Vector3 hitPoint)
    {
        float bestDistance = float.PositiveInfinity;
        hitNormal = Vector3Int.zero;
        hitPoint = Vector3.zero;
        bool hit = false;
        Vector3 origin = voxel;
        BlockPlacementAxis rampAxis = RampShapeUtility.SanitizeAxis(placementAxis);
        RampShapeVariant rampVariant = RampShapeRuntimeUtility.ResolveShapeVariant(World.Instance, voxel, rampAxis);

        RampShapeUtility.ResolveBottomQuad(rampAxis, out Vector3 bottom0, out Vector3 bottom1, out Vector3 bottom2, out Vector3 bottom3);
        hit = TryUpdateCustomHit(ray, maxDistance, lastNormal, origin + bottom0, origin + bottom1, origin + bottom2, origin + bottom3, ref bestDistance, ref hitNormal, ref hitPoint) || hit;

        RampShapeUtility.ResolveTopTriangles(rampAxis, rampVariant, out Vector3 top0a, out Vector3 top0b, out Vector3 top0c, out Vector3 top1a, out Vector3 top1b, out Vector3 top1c);
        hit = TryUpdateCustomHit(ray, maxDistance, lastNormal, origin + top0a, origin + top0b, origin + top0c, ref bestDistance, ref hitNormal, ref hitPoint) || hit;
        hit = TryUpdateCustomHit(ray, maxDistance, lastNormal, origin + top1a, origin + top1b, origin + top1c, ref bestDistance, ref hitNormal, ref hitPoint) || hit;

        hit = TryHitRampEdge(ray, maxDistance, origin, rampAxis, rampVariant, RampEdge.Left, lastNormal, ref bestDistance, ref hitNormal, ref hitPoint) || hit;
        hit = TryHitRampEdge(ray, maxDistance, origin, rampAxis, rampVariant, RampEdge.Right, lastNormal, ref bestDistance, ref hitNormal, ref hitPoint) || hit;
        hit = TryHitRampEdge(ray, maxDistance, origin, rampAxis, rampVariant, RampEdge.Front, lastNormal, ref bestDistance, ref hitNormal, ref hitPoint) || hit;
        hit = TryHitRampEdge(ray, maxDistance, origin, rampAxis, rampVariant, RampEdge.Back, lastNormal, ref bestDistance, ref hitNormal, ref hitPoint) || hit;

        return hit;
    }

    private bool TryHitVerticalRampBlock(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        BlockPlacementAxis placementAxis,
        Vector3Int lastNormal,
        out Vector3Int hitNormal,
        out Vector3 hitPoint)
    {
        float bestDistance = float.PositiveInfinity;
        hitNormal = Vector3Int.zero;
        hitPoint = Vector3.zero;
        bool hit = false;
        Vector3 origin = voxel;
        BlockPlacementAxis axis = VerticalRampShapeUtility.SanitizeAxis(placementAxis);

        VerticalRampShapeUtility.ResolveBottomTriangle(axis, out Vector3 bottom0, out Vector3 bottom1, out Vector3 bottom2);
        hit = TryUpdateCustomHit(ray, maxDistance, lastNormal, origin + bottom0, origin + bottom1, origin + bottom2, ref bestDistance, ref hitNormal, ref hitPoint) || hit;

        VerticalRampShapeUtility.ResolveTopTriangle(axis, out Vector3 top0, out Vector3 top1, out Vector3 top2);
        hit = TryUpdateCustomHit(ray, maxDistance, lastNormal, origin + top0, origin + top1, origin + top2, ref bestDistance, ref hitNormal, ref hitPoint) || hit;

        VerticalRampShapeUtility.ResolveSideQuad(axis, out Vector3 side0, out Vector3 side1, out Vector3 side2, out Vector3 side3, out _);
        hit = TryUpdateCustomHit(ray, maxDistance, lastNormal, origin + side0, origin + side1, origin + side2, origin + side3, ref bestDistance, ref hitNormal, ref hitPoint) || hit;

        VerticalRampShapeUtility.ResolveFrontQuad(axis, out Vector3 front0, out Vector3 front1, out Vector3 front2, out Vector3 front3, out _);
        hit = TryUpdateCustomHit(ray, maxDistance, lastNormal, origin + front0, origin + front1, origin + front2, origin + front3, ref bestDistance, ref hitNormal, ref hitPoint) || hit;

        VerticalRampShapeUtility.ResolveSlopeQuad(axis, out Vector3 slope0, out Vector3 slope1, out Vector3 slope2, out Vector3 slope3, out _, out _);
        hit = TryUpdateCustomHit(ray, maxDistance, lastNormal, origin + slope0, origin + slope1, origin + slope2, origin + slope3, ref bestDistance, ref hitNormal, ref hitPoint) || hit;

        return hit;
    }

    private bool TryHitRampEdge(
        Ray ray,
        float maxDistance,
        Vector3 origin,
        BlockPlacementAxis rampAxis,
        RampShapeVariant rampVariant,
        RampEdge edge,
        Vector3Int lastNormal,
        ref float bestDistance,
        ref Vector3Int bestNormal,
        ref Vector3 bestPoint)
    {
        if (!RampShapeUtility.ResolveEdgeSurface(rampAxis, rampVariant, edge, out int vertexCount, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3, out _))
            return false;

        if (vertexCount == 4)
            return TryUpdateCustomHit(ray, maxDistance, lastNormal, origin + p0, origin + p1, origin + p2, origin + p3, ref bestDistance, ref bestNormal, ref bestPoint);

        return TryUpdateCustomHit(ray, maxDistance, lastNormal, origin + p0, origin + p1, origin + p2, ref bestDistance, ref bestNormal, ref bestPoint);
    }

    private bool TryHitFenceBlock(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        Vector3Int lastNormal,
        out Vector3Int hitNormal,
        out Vector3 hitPoint)
    {
        byte connectionMask = FenceShapeUtility.ResolveConnectionMask(World.Instance, voxel);
        float bestDistance = float.PositiveInfinity;
        hitNormal = Vector3Int.zero;
        hitPoint = Vector3.zero;
        bool hit = false;

        if (TryHitShapeBox(ray, maxDistance, FenceShapeUtility.GetCenterPostVisualBox().ToWorldBounds(voxel), lastNormal, out float centerDistance, out Vector3Int centerNormal, out Vector3 centerPoint))
        {
            bestDistance = centerDistance;
            hitNormal = centerNormal;
            hitPoint = centerPoint;
            hit = true;
        }

        hit = TryHitFenceRail(ray, maxDistance, voxel, connectionMask, FenceShapeUtility.ConnectWest, lastNormal, ref bestDistance, ref hitNormal, ref hitPoint) || hit;
        hit = TryHitFenceRail(ray, maxDistance, voxel, connectionMask, FenceShapeUtility.ConnectEast, lastNormal, ref bestDistance, ref hitNormal, ref hitPoint) || hit;
        hit = TryHitFenceRail(ray, maxDistance, voxel, connectionMask, FenceShapeUtility.ConnectSouth, lastNormal, ref bestDistance, ref hitNormal, ref hitPoint) || hit;
        hit = TryHitFenceRail(ray, maxDistance, voxel, connectionMask, FenceShapeUtility.ConnectNorth, lastNormal, ref bestDistance, ref hitNormal, ref hitPoint) || hit;
        return hit;
    }

    private bool TryHitFenceRail(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        byte connectionMask,
        byte directionFlag,
        Vector3Int lastNormal,
        ref float bestDistance,
        ref Vector3Int bestNormal,
        ref Vector3 bestPoint)
    {
        if (!FenceShapeUtility.IsFenceConnectionActive(connectionMask, directionFlag))
            return false;

        bool lowerHit = TryHitShapeBox(ray, maxDistance, FenceShapeUtility.GetRailVisualBox(directionFlag, false).ToWorldBounds(voxel), lastNormal, out float lowerDistance, out Vector3Int lowerNormal, out Vector3 lowerPoint);
        bool upperHit = TryHitShapeBox(ray, maxDistance, FenceShapeUtility.GetRailVisualBox(directionFlag, true).ToWorldBounds(voxel), lastNormal, out float upperDistance, out Vector3Int upperNormal, out Vector3 upperPoint);

        bool hit = false;
        if (lowerHit && (float.IsPositiveInfinity(bestDistance) || lowerDistance < bestDistance))
        {
            bestDistance = lowerDistance;
            bestNormal = lowerNormal;
            bestPoint = lowerPoint;
            hit = true;
        }

        if (upperHit && (float.IsPositiveInfinity(bestDistance) || upperDistance < bestDistance))
        {
            bestDistance = upperDistance;
            bestNormal = upperNormal;
            bestPoint = upperPoint;
            hit = true;
        }

        return hit;
    }

    private bool TryUpdateCustomHit(
        Ray ray,
        float maxDistance,
        Vector3Int lastNormal,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        ref float bestDistance,
        ref Vector3Int bestNormal,
        ref Vector3 bestPoint)
    {
        if (!TryHitQuad(ray, p0, p1, p2, p3, maxDistance, lastNormal, out Vector3Int hitNormal, out Vector3 hitPoint))
            return false;

        float distance = Vector3.Distance(ray.origin, hitPoint);
        if (distance >= bestDistance)
            return false;

        bestDistance = distance;
        bestNormal = hitNormal;
        bestPoint = hitPoint;
        return true;
    }

    private bool TryUpdateCustomHit(
        Ray ray,
        float maxDistance,
        Vector3Int lastNormal,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        ref float bestDistance,
        ref Vector3Int bestNormal,
        ref Vector3 bestPoint)
    {
        if (!TryHitTriangle(ray, p0, p1, p2, maxDistance, lastNormal, out Vector3Int hitNormal, out Vector3 hitPoint))
            return false;

        float distance = Vector3.Distance(ray.origin, hitPoint);
        if (distance >= bestDistance)
            return false;

        bestDistance = distance;
        bestNormal = hitNormal;
        bestPoint = hitPoint;
        return true;
    }

    private bool TryHitShapeBoxes(
        Ray ray,
        float maxDistance,
        Vector3Int voxel,
        Vector3Int lastNormal,
        out Vector3Int hitNormal,
        out Vector3 hitPoint,
        int boxCount,
        ShapeBox box0,
        ShapeBox box1,
        ShapeBox box2,
        ShapeBox box3,
        ShapeBox box4)
    {
        float bestDistance = float.PositiveInfinity;
        hitNormal = Vector3Int.zero;
        hitPoint = Vector3.zero;
        bool hit = false;

        hit = TryHitShapeBox(ray, maxDistance, box0.ToWorldBounds(voxel), lastNormal, out float d0, out Vector3Int n0, out Vector3 p0) || hit;
        if (hit && d0 < bestDistance)
        {
            bestDistance = d0;
            hitNormal = n0;
            hitPoint = p0;
        }

        if (boxCount > 1 && TryHitShapeBox(ray, maxDistance, box1.ToWorldBounds(voxel), lastNormal, out float d1, out Vector3Int n1, out Vector3 p1) && d1 < bestDistance)
        {
            bestDistance = d1;
            hitNormal = n1;
            hitPoint = p1;
            hit = true;
        }

        if (boxCount > 2 && TryHitShapeBox(ray, maxDistance, box2.ToWorldBounds(voxel), lastNormal, out float d2, out Vector3Int n2, out Vector3 p2) && d2 < bestDistance)
        {
            bestDistance = d2;
            hitNormal = n2;
            hitPoint = p2;
            hit = true;
        }

        if (boxCount > 3 && TryHitShapeBox(ray, maxDistance, box3.ToWorldBounds(voxel), lastNormal, out float d3, out Vector3Int n3, out Vector3 p3) && d3 < bestDistance)
        {
            bestDistance = d3;
            hitNormal = n3;
            hitPoint = p3;
            hit = true;
        }

        if (boxCount > 4 && TryHitShapeBox(ray, maxDistance, box4.ToWorldBounds(voxel), lastNormal, out float d4, out Vector3Int n4, out Vector3 p4) && d4 < bestDistance)
        {
            hitNormal = n4;
            hitPoint = p4;
            hit = true;
        }

        return hit;
    }

    private static bool TryHitShapeBox(
        Ray ray,
        float maxDistance,
        Bounds bounds,
        Vector3Int lastNormal,
        out float distance,
        out Vector3Int hitNormal,
        out Vector3 hitPoint)
    {
        distance = 0f;
        hitNormal = Vector3Int.zero;
        hitPoint = Vector3.zero;

        if (!bounds.IntersectRay(ray, out float hitDistance) || hitDistance > maxDistance)
            return false;

        distance = Mathf.Max(0f, hitDistance);
        hitPoint = ray.GetPoint(distance);
        hitNormal = ResolveBoundsHitNormal(bounds, hitPoint, lastNormal, ray.direction);
        return true;
    }

    private bool TryHitQuad(
        Ray ray,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        float maxDistance,
        Vector3Int lastNormal,
        out Vector3Int hitNormal,
        out Vector3 hitPoint)
    {
        hitNormal = Vector3Int.zero;
        hitPoint = Vector3.zero;

        bool hitFirst = TryRayTriangle(ray, p0, p1, p2, maxDistance, out float t1, out Vector3 normal1);
        bool hitSecond = TryRayTriangle(ray, p0, p2, p3, maxDistance, out float t2, out Vector3 normal2);

        if (!hitFirst && !hitSecond)
            return false;

        Vector3 normal = normal1;
        float distance = t1;
        if (!hitFirst || (hitSecond && t2 < t1))
        {
            normal = normal2;
            distance = t2;
        }

        hitNormal = ResolveCustomSurfaceNormal(normal, lastNormal, ray.direction);
        hitPoint = ray.GetPoint(Mathf.Max(0f, distance));
        return true;
    }

    private bool TryHitTriangle(
        Ray ray,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        float maxDistance,
        Vector3Int lastNormal,
        out Vector3Int hitNormal,
        out Vector3 hitPoint)
    {
        hitNormal = Vector3Int.zero;
        hitPoint = Vector3.zero;

        if (!TryRayTriangle(ray, p0, p1, p2, maxDistance, out float distance, out Vector3 normal))
            return false;

        hitNormal = ResolveCustomSurfaceNormal(normal, lastNormal, ray.direction);
        hitPoint = ray.GetPoint(Mathf.Max(0f, distance));
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
        out Vector3Int groundPos,
        out BlockType billboardType)
    {
        hitNormal = Vector3Int.zero;
        groundPos = new Vector3Int(int.MinValue, 0, 0);
        billboardType = BlockType.Air;

        World world = World.Instance;
        if (world == null || !world.enableGrassBillboards || world.grassBillboardChance <= 0f)
            return false;

        if (voxel.y <= 0)
            return false;

        Vector3Int groundCandidate = new Vector3Int(voxel.x, voxel.y - 1, voxel.z);
        if (!world.TryResolveVegetationBillboardAt(voxel, out billboardType, out uint variationHash))
            return false;

        float jitter = math.clamp(world.grassBillboardJitter, 0f, 0.35f);
        float jx = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 8, jitter);
        float jz = VegetationBillboardUtility.ComputeJitterOffset(variationHash, 16, jitter);
        float height = VegetationBillboardUtility.ComputeHeight(world.grassBillboardHeight, variationHash);
        float halfWidth = VegetationBillboardUtility.ComputeHalfWidth(variationHash);
        float centerYOffset = VegetationBillboardUtility.ComputeBaseYOffset(variationHash);

        Vector3 center = new Vector3(voxel.x + 0.5f + jx, voxel.y + centerYOffset, voxel.z + 0.5f + jz);

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
