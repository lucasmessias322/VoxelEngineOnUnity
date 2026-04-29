using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class VoxelMobPathAgent : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private World world;
    [SerializeField] private Transform target;
    [SerializeField] private bool findPlayerTargetOnStart = false;
    [SerializeField] private string playerTag = "Player";

    [Header("Mob Occupancy")]
    [SerializeField, Min(1)] private int occupiedWidthBlocks = 1;
    [SerializeField, Min(1)] private int occupiedDepthBlocks = 1;
    [SerializeField, Min(1)] private int occupiedHeightBlocks = 2;

    [Header("Pathfinding")]
    [SerializeField] private VoxelAStarPathSettings pathSettings = new VoxelAStarPathSettings();
    [SerializeField, Min(0.05f)] private float repathInterval = 0.45f;
    [SerializeField, Min(0.1f)] private float targetMoveRepathDistance = 1.25f;
    [SerializeField, Min(0.1f)] private float pathEndRepathDistance = 3f;
    [SerializeField] private bool repathWhenPathInvalid = true;
    [SerializeField, Min(0.05f)] private float pathValidationInterval = 0.35f;
    [SerializeField, Min(1)] private int pathValidationLookAheadNodes = 4;
    [SerializeField, Min(0.1f)] private float stopDistance = 1.25f;

    [Header("Moving Target Stabilization")]
    [SerializeField] private bool stabilizeMovingTargets = true;
    [SerializeField, Min(0.1f)] private float movingTargetRepathInterval = 1.1f;
    [SerializeField, Min(0.1f)] private float movingTargetMoveRepathDistance = 4f;
    [SerializeField, Min(0.1f)] private float movingTargetPathEndRepathDistance = 6f;

    [Header("Movement")]
    [SerializeField, Min(0f)] private float moveSpeed = 3f;
    [SerializeField, Min(0f)] private float turnSpeed = 12f;
    [SerializeField, Min(0.05f)] private float waypointReachDistance = 0.35f;
    [SerializeField] private bool faceMovement = true;
    [SerializeField] private bool smoothSteeringDirection = true;
    [SerializeField, Min(0.1f)] private float steeringSharpness = 8f;

    [Header("Path Smoothing")]
    [SerializeField] private bool usePathLookAhead = true;
    [SerializeField, Min(1)] private int lookAheadNodes = 4;
    [SerializeField, Min(0.25f)] private float lookAheadDistance = 3.5f;
    [SerializeField, Min(0.1f)] private float lookAheadSampleStep = 0.35f;

    [Header("Feet Bounds")]
    [SerializeField] private Collider bodyCollider;
    [SerializeField] private bool autoFindBodyCollider = true;
    [SerializeField] private bool useColliderBounds = true;

    [Header("Gravity")]
    [SerializeField, Min(0f)] private float gravity = 20f;
    [SerializeField, Min(0f)] private float maxFallSpeed = 40f;
    [SerializeField, Min(0f)] private float groundedStickVelocity = 0.1f;
    [SerializeField, Min(0f)] private float groundSnapDistance = 0.35f;
    [SerializeField] private bool useVoxelGravity = true;

    [Header("Debug")]
    [SerializeField] private bool drawDebugPath = false;
    [SerializeField] private bool drawGizmosOnlyWhenSelected = true;
    [SerializeField] private bool drawGizmoPath = true;
    [SerializeField] private bool drawGizmoGrid = false;
    [SerializeField] private bool usePathSearchRadiusForGizmoGrid = true;
    [SerializeField, Min(1)] private int gizmoGridRadius = 8;
    [SerializeField, Min(0)] private int gizmoVerticalProbe = 4;
    [SerializeField] private bool drawBlockedGridCells = true;
    [SerializeField] private bool drawUnloadedGridCells = false;
    [SerializeField] private Color gizmoPathColor = new Color(0f, 0.9f, 1f, 1f);
    [SerializeField] private Color gizmoCurrentWaypointColor = new Color(1f, 0.86f, 0.1f, 1f);
    [SerializeField] private Color gizmoWalkableCellColor = new Color(0.1f, 1f, 0.25f, 0.28f);
    [SerializeField] private Color gizmoBlockedCellColor = new Color(1f, 0.18f, 0.08f, 0.18f);
    [SerializeField] private Color gizmoUnloadedCellColor = new Color(0.55f, 0.55f, 0.55f, 0.16f);

    private readonly VoxelAStarPathfinder pathfinder = new VoxelAStarPathfinder();
    private readonly List<Vector3> path = new List<Vector3>(64);
    private Collider cachedFeetCollider;
    private Vector3 feetLocalPoint;
    private float verticalVelocity;
    private Vector3 lastTargetPathPosition;
    private int waypointIndex;
    private float nextRepathTime;
    private float nextPathValidationTime;
    private Vector3 smoothedSteeringDirection;
    private bool hasPath;
    private bool hasFeetLocalPoint;
    private bool forceRepathRequested;
    private bool hasSmoothedSteeringDirection;

    public bool HasPath => hasPath;
    public bool HasDestination => hasDestination;
    public Vector3 Destination => destination;
    public Vector3 FeetPosition => GetFeetWorldPosition();
    public float RemainingDistance => hasDestination ? Mathf.Sqrt(HorizontalDistanceSqr(GetFeetWorldPosition(), destination)) : 0f;
    public bool ReachedDestination => hasDestination &&
                                      !hasPath &&
                                      HorizontalDistanceSqr(GetFeetWorldPosition(), destination) <= stopDistance * stopDistance;
    public VoxelPathResult LastPathResult { get; private set; }

    private Vector3 destination;
    private bool hasDestination;

    private void Awake()
    {
        if (pathSettings == null)
            pathSettings = VoxelAStarPathSettings.Default;

        if (world == null)
            world = World.Instance;

        ResolveBodyCollider();
        RefreshFeetLocalPoint();
        ApplyOccupancyToPathSettings();
    }

    private void OnValidate()
    {
        ApplyOccupancyToPathSettings();
    }

    private void Start()
    {
        if (target == null && findPlayerTargetOnStart)
        {
            GameObject player = GameObject.FindGameObjectWithTag(playerTag);
            if (player != null)
                SetTarget(player.transform);
        }

        nextRepathTime = Time.time + Random.Range(0f, repathInterval);
    }

    private void Update()
    {
        if (world == null)
            world = World.Instance;

        ResolveBodyCollider();
        RefreshFeetLocalPoint();
        ApplyOccupancyToPathSettings();

        if (world != null && TryGetMoveGoal(out Vector3 moveGoal))
        {
            MaybeRefreshPath(moveGoal);
        }
        else if (!hasDestination && target == null)
        {
            hasPath = false;
            path.Clear();
        }

        FollowPath();

        if (drawDebugPath)
            DrawDebugPath();
    }

    public void SetTarget(Transform newTarget, bool forceRepath = true)
    {
        target = newTarget;
        if (target != null)
        {
            destination = target.position;
            hasDestination = true;
        }
        else
        {
            ResetPath();
            return;
        }

        if (forceRepath)
            ForceRepath();
    }

    public bool SetDestination(Vector3 newDestination, bool forceRepath = true)
    {
        target = null;
        destination = newDestination;
        hasDestination = true;

        if (forceRepath)
            ForceRepath();

        return true;
    }

    public void ResetPath()
    {
        target = null;
        hasDestination = false;
        hasPath = false;
        waypointIndex = 0;
        path.Clear();
        LastPathResult = VoxelPathResult.Failed(0);
        forceRepathRequested = false;
        hasSmoothedSteeringDirection = false;
    }

    public void ForceRepath()
    {
        nextRepathTime = 0f;
        forceRepathRequested = true;
    }

    public bool TryGetNearestStandablePosition(
        Vector3 preferredPosition,
        int horizontalRadius,
        int verticalRadius,
        out Vector3 standablePosition)
    {
        ApplyOccupancyToPathSettings();

        World activeWorld = world != null ? world : World.Instance;
        if (activeWorld == null)
        {
            standablePosition = default;
            return false;
        }

        Vector3Int center = VoxelAStarPathfinder.WorldToFeetCell(preferredPosition);
        horizontalRadius = Mathf.Max(0, horizontalRadius);
        verticalRadius = Mathf.Max(0, verticalRadius);

        for (int radius = 0; radius <= horizontalRadius; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != radius)
                        continue;

                    for (int dy = 0; dy <= verticalRadius; dy++)
                    {
                        if (TryGetStandableCandidate(activeWorld, center.x + dx, center.y + dy, center.z + dz, out standablePosition))
                            return true;

                        if (dy > 0 &&
                            TryGetStandableCandidate(activeWorld, center.x + dx, center.y - dy, center.z + dz, out standablePosition))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        standablePosition = default;
        return false;
    }

    private bool TryGetMoveGoal(out Vector3 moveGoal)
    {
        if (target != null)
        {
            destination = target.position;
            hasDestination = true;
            moveGoal = destination;
            return true;
        }

        if (hasDestination)
        {
            moveGoal = destination;
            return true;
        }

        moveGoal = default;
        return false;
    }

    private bool TryGetStandableCandidate(World activeWorld, int x, int y, int z, out Vector3 standablePosition)
    {
        Vector3Int feet = new Vector3Int(x, y, z);
        if (!pathfinder.IsStandable(activeWorld, feet, pathSettings))
        {
            standablePosition = default;
            return false;
        }

        standablePosition = CellToFeetWorldPosition(feet);
        return true;
    }

    private void ResolveBodyCollider()
    {
        if (!autoFindBodyCollider || bodyCollider != null)
            return;

        bodyCollider = GetComponent<Collider>();
        if (bodyCollider != null && !bodyCollider.isTrigger)
            return;

        Collider[] colliders = GetComponentsInChildren<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] == null || colliders[i].isTrigger)
                continue;

            bodyCollider = colliders[i];
            RefreshFeetLocalPoint();
            return;
        }
    }

    private void RefreshFeetLocalPoint()
    {
        if (!useColliderBounds || bodyCollider == null || !bodyCollider.enabled)
        {
            hasFeetLocalPoint = false;
            cachedFeetCollider = null;
            return;
        }

        if (hasFeetLocalPoint && cachedFeetCollider == bodyCollider)
            return;

        Bounds bounds = bodyCollider.bounds;
        if (bounds.size.sqrMagnitude <= 0.000001f)
        {
            hasFeetLocalPoint = false;
            cachedFeetCollider = null;
            return;
        }

        Vector3 feetWorldPoint = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        feetLocalPoint = transform.InverseTransformPoint(feetWorldPoint);
        cachedFeetCollider = bodyCollider;
        hasFeetLocalPoint = true;
    }

    private void ApplyOccupancyToPathSettings()
    {
        occupiedWidthBlocks = Mathf.Max(1, occupiedWidthBlocks);
        occupiedDepthBlocks = Mathf.Max(1, occupiedDepthBlocks);
        occupiedHeightBlocks = Mathf.Max(1, occupiedHeightBlocks);

        if (pathSettings == null)
            pathSettings = VoxelAStarPathSettings.Default;

        pathSettings.bodyWidthBlocks = occupiedWidthBlocks;
        pathSettings.bodyDepthBlocks = occupiedDepthBlocks;
        pathSettings.mobHeightBlocks = occupiedHeightBlocks;
    }

    private Vector3 GetFeetWorldPosition()
    {
        if (useColliderBounds && hasFeetLocalPoint)
            return transform.TransformPoint(feetLocalPoint);

        return transform.position;
    }

    private void MoveFeetTo(Vector3 targetFeetPosition)
    {
        Vector3 currentFeetPosition = GetFeetWorldPosition();
        transform.position += targetFeetPosition - currentFeetPosition;
    }

    private void MaybeRefreshPath(Vector3 moveGoal)
    {
        float stopDistanceSqr = stopDistance * stopDistance;
        Vector3 feetPosition = GetFeetWorldPosition();
        if (HorizontalDistanceSqr(feetPosition, moveGoal) <= stopDistanceSqr)
        {
            hasPath = false;
            hasSmoothedSteeringDirection = false;
            path.Clear();
            return;
        }

        bool pathInvalid = ShouldRepathBecausePathIsInvalid(feetPosition);
        bool shouldRepath = forceRepathRequested || pathInvalid || ShouldRepathForGoal(moveGoal);

        if (!shouldRepath)
            return;

        if (Time.time < nextRepathTime && !pathInvalid && !forceRepathRequested)
            return;

        nextRepathTime = Time.time + GetActiveRepathInterval();
        lastTargetPathPosition = moveGoal;
        LastPathResult = pathfinder.TryFindPath(world, feetPosition, moveGoal, pathSettings, path);
        forceRepathRequested = false;

        hasPath = LastPathResult.HasPath;
        waypointIndex = 0;
        SkipCurrentCellWaypoints(feetPosition);
        SkipReachedWaypoints();
    }

    private bool ShouldRepathForGoal(Vector3 moveGoal)
    {
        if (!hasPath || path.Count == 0 || waypointIndex >= path.Count)
            return true;

        float targetMoveDistance = GetActiveTargetMoveRepathDistance();
        float targetMoveDistanceSqr = targetMoveDistance * targetMoveDistance;
        if (HorizontalDistanceSqr(lastTargetPathPosition, moveGoal) < targetMoveDistanceSqr)
            return false;

        if (LastPathResult.status == VoxelPathStatus.Partial)
            return true;

        Vector3 pathEnd = path[path.Count - 1];
        float activePathEndRepathDistance = GetActivePathEndRepathDistance();
        float pathEndDistanceSqr = activePathEndRepathDistance * activePathEndRepathDistance;
        return HorizontalDistanceSqr(pathEnd, moveGoal) >= pathEndDistanceSqr;
    }

    private float GetActiveRepathInterval()
    {
        return stabilizeMovingTargets && target != null
            ? movingTargetRepathInterval
            : repathInterval;
    }

    private float GetActiveTargetMoveRepathDistance()
    {
        return stabilizeMovingTargets && target != null
            ? movingTargetMoveRepathDistance
            : targetMoveRepathDistance;
    }

    private float GetActivePathEndRepathDistance()
    {
        return stabilizeMovingTargets && target != null
            ? movingTargetPathEndRepathDistance
            : pathEndRepathDistance;
    }

    private bool ShouldRepathBecausePathIsInvalid(Vector3 feetPosition)
    {
        if (!repathWhenPathInvalid || !hasPath || world == null || path.Count == 0 || waypointIndex >= path.Count)
            return false;

        if (Time.time < nextPathValidationTime)
            return false;

        nextPathValidationTime = Time.time + pathValidationInterval;
        return !IsCurrentPathStillUsable(feetPosition);
    }

    private bool IsCurrentPathStillUsable(Vector3 feetPosition)
    {
        int lastIndex = Mathf.Min(path.Count - 1, waypointIndex + pathValidationLookAheadNodes);
        for (int i = waypointIndex; i <= lastIndex; i++)
        {
            Vector3Int feet = VoxelAStarPathfinder.WorldToFeetCell(path[i]);
            if (!pathfinder.IsStandable(world, feet, pathSettings))
                return false;
        }

        if (Mathf.Abs(feetPosition.y - path[waypointIndex].y) <= 0.2f &&
            !HasClearMovementLine(feetPosition, path[waypointIndex]))
        {
            return false;
        }

        return true;
    }

    private void FollowPath()
    {
        if (!hasPath || waypointIndex >= path.Count)
        {
            hasSmoothedSteeringDirection = false;
            ApplyIdleGravity();
            return;
        }

        SkipReachedWaypoints();
        if (!hasPath || waypointIndex >= path.Count)
        {
            hasSmoothedSteeringDirection = false;
            ApplyIdleGravity();
            return;
        }

        Vector3 feetPosition = GetFeetWorldPosition();
        Vector3 waypoint = GetSteeringWaypoint(feetPosition);
        Vector3 toWaypoint = waypoint - feetPosition;
        Vector3 horizontal = new Vector3(toWaypoint.x, 0f, toWaypoint.z);

        if (horizontal.sqrMagnitude <= 0.0001f)
        {
            hasSmoothedSteeringDirection = false;
            Move(Vector3.zero, waypoint);
            return;
        }

        Vector3 moveDirection = GetSmoothedMoveDirection(horizontal.normalized);
        Vector3 horizontalVelocity = moveDirection * moveSpeed;
        Move(horizontalVelocity, waypoint);

        if (faceMovement)
            RotateTowards(horizontalVelocity);
    }

    private Vector3 GetSmoothedMoveDirection(Vector3 desiredDirection)
    {
        if (!smoothSteeringDirection)
            return desiredDirection;

        if (!hasSmoothedSteeringDirection || smoothedSteeringDirection.sqrMagnitude <= 0.0001f)
        {
            smoothedSteeringDirection = desiredDirection;
            hasSmoothedSteeringDirection = true;
            return desiredDirection;
        }

        float blend = 1f - Mathf.Exp(-steeringSharpness * Time.deltaTime);
        smoothedSteeringDirection = Vector3.Lerp(smoothedSteeringDirection, desiredDirection, blend);
        if (smoothedSteeringDirection.sqrMagnitude <= 0.0001f)
            return desiredDirection;

        smoothedSteeringDirection.Normalize();
        return smoothedSteeringDirection;
    }

    private Vector3 GetSteeringWaypoint(Vector3 feetPosition)
    {
        if (path.Count == 0)
            return feetPosition;

        if (waypointIndex >= path.Count)
            return path[path.Count - 1];

        if (!usePathLookAhead)
            return path[waypointIndex];

        int bestIndex = waypointIndex;
        int lastIndex = Mathf.Min(path.Count - 1, waypointIndex + lookAheadNodes);
        float maxLookAheadDistanceSqr = lookAheadDistance * lookAheadDistance;

        for (int i = waypointIndex + 1; i <= lastIndex; i++)
        {
            if (HorizontalDistanceSqr(feetPosition, path[i]) > maxLookAheadDistanceSqr)
                break;

            if (Mathf.Abs(feetPosition.y - path[i].y) > 0.2f)
                break;

            if (!HasClearMovementLine(feetPosition, path[i]))
                break;

            bestIndex = i;
        }

        if (bestIndex > waypointIndex)
            waypointIndex = bestIndex;

        return path[waypointIndex];
    }

    private bool HasClearMovementLine(Vector3 fromFeetPosition, Vector3 toFeetPosition)
    {
        Vector3 delta = toFeetPosition - fromFeetPosition;
        float distance = delta.magnitude;
        if (distance <= 0.001f)
            return CanOccupyFeetPosition(toFeetPosition, true);

        int sampleCount = Mathf.Max(1, Mathf.CeilToInt(distance / lookAheadSampleStep));
        for (int i = 1; i <= sampleCount; i++)
        {
            Vector3 sample = Vector3.Lerp(fromFeetPosition, toFeetPosition, i / (float)sampleCount);
            if (!CanOccupyFeetPosition(sample, true))
                return false;
        }

        return true;
    }

    private void Move(Vector3 horizontalVelocity, Vector3 waypoint)
    {
        if (!useVoxelGravity)
        {
            transform.position += horizontalVelocity * Time.deltaTime;
            return;
        }

        MoveWithVoxelGravity(horizontalVelocity, waypoint, Time.deltaTime);
    }

    private void ApplyIdleGravity()
    {
        if (!useVoxelGravity)
            return;

        MoveWithVoxelGravity(Vector3.zero, GetFeetWorldPosition(), Time.deltaTime);
    }

    private void MoveWithVoxelGravity(Vector3 horizontalVelocity, Vector3 waypoint, float deltaTime)
    {
        if (world == null)
        {
            transform.position += horizontalVelocity * deltaTime;
            return;
        }

        Vector3 currentFeetPosition = GetFeetWorldPosition();
        bool isGrounded = IsVoxelGrounded(currentFeetPosition, out float groundY);
        if (isGrounded && verticalVelocity < 0f)
            verticalVelocity = -groundedStickVelocity;

        float yDelta = waypoint.y - currentFeetPosition.y;
        float verticalDelta;
        if (isGrounded && yDelta > 0.05f && yDelta <= Mathf.Max(1, pathSettings.stepHeightBlocks) + 0.25f)
        {
            verticalVelocity = 0f;
            verticalDelta = Mathf.Min(yDelta, Mathf.Max(moveSpeed, 0.01f) * 1.25f * deltaTime);
        }
        else
        {
            verticalVelocity = Mathf.Max(verticalVelocity - gravity * deltaTime, -maxFallSpeed);
            verticalDelta = verticalVelocity * deltaTime;
        }

        bool requireSupport = isGrounded && yDelta >= -0.05f;
        Vector3 nextFeetPosition = ResolveVoxelMove(
            currentFeetPosition,
            horizontalVelocity * deltaTime,
            verticalDelta,
            waypoint.y,
            requireSupport,
            out bool snappedToStep);

        if (snappedToStep)
            verticalVelocity = 0f;

        if (verticalDelta <= 0f && TryFindVoxelGroundBelow(nextFeetPosition, Mathf.Abs(verticalDelta) + groundSnapDistance, out float landingY))
        {
            nextFeetPosition.y = landingY;
            verticalVelocity = -groundedStickVelocity;
        }
        else if (isGrounded && verticalDelta <= 0f && Mathf.Abs(currentFeetPosition.y - groundY) <= groundSnapDistance)
        {
            nextFeetPosition.y = groundY;
            verticalVelocity = -groundedStickVelocity;
        }

        MoveFeetTo(nextFeetPosition);
    }

    private Vector3 ResolveVoxelMove(
        Vector3 currentPosition,
        Vector3 horizontalDelta,
        float verticalDelta,
        float waypointY,
        bool requireSupport,
        out bool snappedToStep)
    {
        snappedToStep = false;

        Vector3 desired = currentPosition + horizontalDelta + Vector3.up * verticalDelta;
        if (CanOccupyFeetPosition(desired, requireSupport))
            return desired;

        if (TryResolveStepUp(currentPosition, horizontalDelta, waypointY, requireSupport, out Vector3 stepPosition))
        {
            snappedToStep = true;
            return stepPosition;
        }

        Vector3 xOnly = currentPosition + new Vector3(horizontalDelta.x, verticalDelta, 0f);
        if (CanOccupyFeetPosition(xOnly, requireSupport))
            return xOnly;

        Vector3 zOnly = currentPosition + new Vector3(0f, verticalDelta, horizontalDelta.z);
        if (CanOccupyFeetPosition(zOnly, requireSupport))
            return zOnly;

        Vector3 verticalOnly = currentPosition + Vector3.up * verticalDelta;
        if (CanOccupyFeetPosition(verticalOnly, false))
            return verticalOnly;

        return currentPosition;
    }

    private bool TryResolveStepUp(
        Vector3 currentPosition,
        Vector3 horizontalDelta,
        float waypointY,
        bool requireSupport,
        out Vector3 stepPosition)
    {
        stepPosition = default;
        if (!requireSupport || horizontalDelta.sqrMagnitude <= 0.000001f)
            return false;

        float stepDelta = waypointY - currentPosition.y;
        float maxStep = Mathf.Max(0, pathSettings != null ? pathSettings.stepHeightBlocks : 1) + 0.35f;
        if (stepDelta <= 0.05f || stepDelta > maxStep)
            return false;

        Vector3 desired = new Vector3(currentPosition.x + horizontalDelta.x, waypointY, currentPosition.z + horizontalDelta.z);
        if (CanOccupyFeetPosition(desired, true))
        {
            stepPosition = desired;
            return true;
        }

        Vector3 xOnly = new Vector3(currentPosition.x + horizontalDelta.x, waypointY, currentPosition.z);
        if (CanOccupyFeetPosition(xOnly, true))
        {
            stepPosition = xOnly;
            return true;
        }

        Vector3 zOnly = new Vector3(currentPosition.x, waypointY, currentPosition.z + horizontalDelta.z);
        if (CanOccupyFeetPosition(zOnly, true))
        {
            stepPosition = zOnly;
            return true;
        }

        return false;
    }

    private bool CanOccupyFeetPosition(Vector3 feetPosition, bool requireSupport)
    {
        return IsVoxelBodyClear(feetPosition) &&
               (!requireSupport || HasVoxelSupport(feetPosition));
    }

    private bool IsVoxelGrounded(Vector3 position, out float groundY)
    {
        return TryFindVoxelGroundBelow(position, groundSnapDistance, out groundY);
    }

    private bool TryFindVoxelGroundBelow(Vector3 position, float probeDistance, out float groundY)
    {
        Vector3Int origin = VoxelAStarPathfinder.WorldToFeetCell(position);
        int minY = Mathf.FloorToInt(position.y - probeDistance + 0.05f);

        for (int y = origin.y; y >= minY; y--)
        {
            Vector3 feetPosition = new Vector3(position.x, y + GetWaypointYOffset(), position.z);
            if (!IsVoxelBodyClear(feetPosition) || !HasVoxelSupport(feetPosition))
                continue;

            groundY = y + GetWaypointYOffset();
            return true;
        }

        groundY = 0f;
        return false;
    }

    private bool IsVoxelStandable(Vector3Int feet)
    {
        return world != null && pathfinder.IsStandable(world, feet, pathSettings);
    }

    private bool IsVoxelBodyClear(Vector3Int feet)
    {
        return world == null || pathfinder.IsBodyClear(world, feet, pathSettings);
    }

    private bool IsVoxelBodyClear(Vector3 feetPosition)
    {
        if (world == null)
            return true;

        Vector3Int feet = VoxelAStarPathfinder.WorldToFeetCell(feetPosition);
        GetMovementFootprintBounds(feetPosition, out int minX, out int maxX, out int minZ, out int maxZ);
        int minY = feet.y;
        int maxY = minY + GetOccupiedHeightBlocks() - 1;
        bool allowWater = pathSettings != null && pathSettings.allowWater;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    if (!TryGetMovementBlock(new Vector3Int(x, y, z), out BlockType blockType))
                        return false;

                    if (world.IsSolidBlock(blockType))
                        return false;

                    if (!allowWater && world.IsLiquidBlock(blockType))
                        return false;
                }
            }
        }

        return true;
    }

    private bool HasVoxelSupport(Vector3 feetPosition)
    {
        if (world == null)
            return false;

        Vector3Int feet = VoxelAStarPathfinder.WorldToFeetCell(feetPosition);
        GetMovementFootprintBounds(feetPosition, out int minX, out int maxX, out int minZ, out int maxZ);
        int supportY = feet.y - 1;

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                if (!TryGetMovementBlock(new Vector3Int(x, supportY, z), out BlockType blockType))
                    continue;

                if (world.IsSolidBlock(blockType) && !world.IsLiquidBlock(blockType))
                    return true;
            }
        }

        return false;
    }

    private void GetMovementFootprintBounds(
        Vector3 feetPosition,
        out int minX,
        out int maxX,
        out int minZ,
        out int maxZ)
    {
        Vector3Int feet = VoxelAStarPathfinder.WorldToFeetCell(feetPosition);
        int width = GetOccupiedWidthBlocks();
        int depth = GetOccupiedDepthBlocks();

        minX = feet.x - (width - 1) / 2;
        maxX = feet.x + width / 2;
        minZ = feet.z - (depth - 1) / 2;
        maxZ = feet.z + depth / 2;
    }

    private int GetOccupiedWidthBlocks()
    {
        return Mathf.Max(1, occupiedWidthBlocks);
    }

    private int GetOccupiedDepthBlocks()
    {
        return Mathf.Max(1, occupiedDepthBlocks);
    }

    private int GetOccupiedHeightBlocks()
    {
        return Mathf.Max(1, occupiedHeightBlocks);
    }

    private float GetWaypointYOffset()
    {
        return pathSettings != null ? pathSettings.waypointYOffset : 0.05f;
    }

    private Vector3 CellToFeetWorldPosition(Vector3Int feet)
    {
        return new Vector3(feet.x + 0.5f, feet.y + GetWaypointYOffset(), feet.z + 0.5f);
    }

    private bool TryGetMovementBlock(Vector3Int position, out BlockType blockType)
    {
        if (pathSettings != null && pathSettings.loadedChunksOnly)
            return world.TryGetLoadedBlockAt(position, out blockType);

        blockType = world.GetBlockAt(position);
        return true;
    }

    private void RotateTowards(Vector3 horizontalVelocity)
    {
        Vector3 direction = new Vector3(horizontalVelocity.x, 0f, horizontalVelocity.z);
        if (direction.sqrMagnitude <= 0.0001f)
            return;

        Quaternion targetRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, turnSpeed * Time.deltaTime);
    }

    private void SkipReachedWaypoints()
    {
        float reachDistanceSqr = waypointReachDistance * waypointReachDistance;
        Vector3 feetPosition = GetFeetWorldPosition();
        Vector3Int feetCell = VoxelAStarPathfinder.WorldToFeetCell(feetPosition);
        while (waypointIndex < path.Count &&
               IsWaypointReachedOrCurrentCell(feetPosition, feetCell, path[waypointIndex], reachDistanceSqr))
        {
            waypointIndex++;
        }

        if (waypointIndex >= path.Count)
            hasPath = false;
    }

    private void SkipCurrentCellWaypoints(Vector3 feetPosition)
    {
        if (path.Count == 0)
            return;

        Vector3Int feetCell = VoxelAStarPathfinder.WorldToFeetCell(feetPosition);
        while (waypointIndex < path.Count &&
               VoxelAStarPathfinder.WorldToFeetCell(path[waypointIndex]) == feetCell)
        {
            waypointIndex++;
        }

        if (waypointIndex >= path.Count && path.Count > 0)
            waypointIndex = path.Count - 1;
    }

    private bool IsWaypointReachedOrCurrentCell(
        Vector3 feetPosition,
        Vector3Int feetCell,
        Vector3 waypoint,
        float reachDistanceSqr)
    {
        if (VoxelAStarPathfinder.WorldToFeetCell(waypoint) == feetCell)
            return true;

        return HorizontalDistanceSqr(feetPosition, waypoint) <= reachDistanceSqr &&
               Mathf.Abs(feetPosition.y - waypoint.y) <= 0.45f;
    }

    private void DrawDebugPath()
    {
        for (int i = 1; i < path.Count; i++)
            Debug.DrawLine(path[i - 1] + Vector3.up * 0.05f, path[i] + Vector3.up * 0.05f, Color.cyan);
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmosOnlyWhenSelected)
            DrawPathfindingGizmos();
    }

    private void OnDrawGizmosSelected()
    {
        if (drawGizmosOnlyWhenSelected)
            DrawPathfindingGizmos();
    }

    private void DrawPathfindingGizmos()
    {
        ApplyOccupancyToPathSettings();
        VoxelAStarPathSettings settings = pathSettings ?? VoxelAStarPathSettings.Default;

        if (drawGizmoGrid)
            DrawGridGizmos(settings);

        if (drawGizmoPath)
            DrawPathGizmos();
    }

    private void DrawPathGizmos()
    {
        if (path == null || path.Count == 0)
            return;

        Gizmos.color = gizmoPathColor;
        for (int i = 1; i < path.Count; i++)
            Gizmos.DrawLine(path[i - 1] + Vector3.up * 0.08f, path[i] + Vector3.up * 0.08f);

        for (int i = 0; i < path.Count; i++)
        {
            Gizmos.color = i == waypointIndex ? gizmoCurrentWaypointColor : gizmoPathColor;
            Gizmos.DrawSphere(path[i] + Vector3.up * 0.08f, i == waypointIndex ? 0.13f : 0.08f);
        }
    }

    private void DrawGridGizmos(VoxelAStarPathSettings settings)
    {
        World gizmoWorld = ResolveWorldForGizmos();
        if (gizmoWorld == null)
            return;

        Vector3Int origin = VoxelAStarPathfinder.WorldToFeetCell(GetFeetWorldPosition());
        int radius = usePathSearchRadiusForGizmoGrid ? settings.searchRadius : gizmoGridRadius;
        radius = Mathf.Clamp(radius, 1, 48);

        int verticalProbe = Mathf.Clamp(gizmoVerticalProbe, 0, Mathf.Max(1, settings.verticalSearchRange));
        Vector3 cellSize = new Vector3(0.92f, 0.08f, 0.92f);

        for (int x = origin.x - radius; x <= origin.x + radius; x++)
        {
            for (int z = origin.z - radius; z <= origin.z + radius; z++)
            {
                if (!TryGetGizmoColumnCell(
                        gizmoWorld,
                        x,
                        z,
                        origin,
                        settings,
                        verticalProbe,
                        out Vector3Int feet,
                        out VoxelAStarDebugCellState state))
                {
                    continue;
                }

                DrawGridCellGizmo(feet, state, cellSize);
            }
        }
    }

    private World ResolveWorldForGizmos()
    {
        if (world != null)
            return world;

        if (World.Instance != null)
            return World.Instance;

        return UnityEngine.Object.FindAnyObjectByType<World>();
    }

    private bool TryGetGizmoColumnCell(
        World gizmoWorld,
        int x,
        int z,
        Vector3Int origin,
        VoxelAStarPathSettings settings,
        int verticalProbe,
        out Vector3Int feet,
        out VoxelAStarDebugCellState state)
    {
        bool hasBlocked = false;
        bool hasUnloaded = false;
        Vector3Int blockedCell = new Vector3Int(x, origin.y, z);
        Vector3Int unloadedCell = blockedCell;

        for (int dy = 0; dy <= verticalProbe; dy++)
        {
            Vector3Int upCell = new Vector3Int(x, origin.y + dy, z);
            if (TryEvaluateGizmoCell(gizmoWorld, upCell, origin, settings, ref hasBlocked, ref blockedCell, ref hasUnloaded, ref unloadedCell, out feet, out state))
                return true;

            if (dy == 0)
                continue;

            Vector3Int downCell = new Vector3Int(x, origin.y - dy, z);
            if (TryEvaluateGizmoCell(gizmoWorld, downCell, origin, settings, ref hasBlocked, ref blockedCell, ref hasUnloaded, ref unloadedCell, out feet, out state))
                return true;
        }

        if (hasBlocked && drawBlockedGridCells)
        {
            feet = blockedCell;
            state = VoxelAStarDebugCellState.Blocked;
            return true;
        }

        if (hasUnloaded && drawUnloadedGridCells)
        {
            feet = unloadedCell;
            state = VoxelAStarDebugCellState.Unloaded;
            return true;
        }

        feet = default;
        state = default;
        return false;
    }

    private bool TryEvaluateGizmoCell(
        World gizmoWorld,
        Vector3Int candidate,
        Vector3Int origin,
        VoxelAStarPathSettings settings,
        ref bool hasBlocked,
        ref Vector3Int blockedCell,
        ref bool hasUnloaded,
        ref Vector3Int unloadedCell,
        out Vector3Int feet,
        out VoxelAStarDebugCellState state)
    {
        state = pathfinder.GetDebugCellState(gizmoWorld, candidate, origin, settings);
        if (state == VoxelAStarDebugCellState.Walkable)
        {
            feet = candidate;
            return true;
        }

        if (state == VoxelAStarDebugCellState.Blocked && !hasBlocked)
        {
            hasBlocked = true;
            blockedCell = candidate;
        }
        else if (state == VoxelAStarDebugCellState.Unloaded && !hasUnloaded)
        {
            hasUnloaded = true;
            unloadedCell = candidate;
        }

        feet = default;
        return false;
    }

    private void DrawGridCellGizmo(Vector3Int feet, VoxelAStarDebugCellState state, Vector3 cellSize)
    {
        Vector3 center = new Vector3(feet.x + 0.5f, feet.y + 0.02f, feet.z + 0.5f);

        switch (state)
        {
            case VoxelAStarDebugCellState.Walkable:
                Gizmos.color = gizmoWalkableCellColor;
                Gizmos.color = WithAlpha(gizmoWalkableCellColor, 0.85f);
                Gizmos.DrawWireCube(center, cellSize);
                break;

            case VoxelAStarDebugCellState.Blocked:
                Gizmos.color = gizmoBlockedCellColor;
                Gizmos.DrawWireCube(center, cellSize);
                break;

            case VoxelAStarDebugCellState.Unloaded:
                Gizmos.color = gizmoUnloadedCellColor;
                Gizmos.DrawWireCube(center, cellSize);
                break;
        }
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        color.a = alpha;
        return color;
    }

    private static float HorizontalDistanceSqr(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }
}
