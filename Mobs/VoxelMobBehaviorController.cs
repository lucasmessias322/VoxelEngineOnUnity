using UnityEngine;

public enum VoxelMobBehaviorType
{
    None = 0,
    Wander = 1,
    Chase = 2
}

[DisallowMultipleComponent]
[RequireComponent(typeof(VoxelMobPathAgent))]
public sealed class VoxelMobBehaviorController : MonoBehaviour
{
    [Header("Behavior")]
    [SerializeField] private VoxelMobBehaviorType currentBehavior = VoxelMobBehaviorType.Wander;
    [SerializeField, Min(0.05f)] private float thinkInterval = 0.25f;

    [Header("Vision")]
    [SerializeField] private VoxelMobVision vision;
    [SerializeField] private bool autoFindVision = true;
    [SerializeField] private bool chaseVisibleTargets = true;

    [Header("Wander")]
    [SerializeField, Min(1f)] private float wanderRadius = 8f;
    [SerializeField, Min(0f)] private float minWanderDistance = 2f;
    [SerializeField, Min(0f)] private float minIdleTime = 1f;
    [SerializeField, Min(0f)] private float maxIdleTime = 3f;
    [SerializeField, Min(1)] private int destinationAttempts = 10;
    [SerializeField, Min(0)] private int standableSampleRadius = 2;
    [SerializeField, Min(0)] private int standableVerticalProbe = 4;
    [SerializeField, Min(0.5f)] private float destinationTimeout = 8f;
    [SerializeField] private bool wanderAroundStartPosition = false;

    [Header("Chase")]
    [SerializeField, Min(1f)] private float maxChaseDistance = 50f;
    [SerializeField] private bool switchToNearestVisibleTargetWhileChasing = false;
    [SerializeField] private bool returnToPreviousBehaviorAfterChase = true;

    [Header("Animation")]
    [SerializeField] private Animator animator;
    [SerializeField] private bool autoFindAnimator = true;
    [SerializeField] private bool updateAnimatorBools = true;
    [SerializeField] private string walkingBoolParameter = "isWalking";
    [SerializeField] private string runningBoolParameter = "isRunning";
    [SerializeField] private string idleBoolParameter = "isIdle";
    [SerializeField, Min(0f)] private float movingSpeedThreshold = 0.03f;

    [Header("Debug")]
    [SerializeField] private bool drawWanderGizmos = true;
    [SerializeField] private bool drawChaseGizmos = true;
    [SerializeField] private Color wanderRadiusColor = new Color(0.2f, 0.8f, 1f, 0.35f);
    [SerializeField] private Color wanderDestinationColor = new Color(1f, 0.75f, 0.1f, 0.9f);
    [SerializeField] private Color chaseTargetColor = new Color(1f, 0.12f, 0.08f, 0.95f);
    [SerializeField] private Color lastKnownTargetColor = new Color(1f, 0.55f, 0.05f, 0.85f);

    private VoxelMobPathAgent agent;
    private Vector3 startPosition;
    private Vector3 currentWanderDestination;
    private Vector3 lastKnownTargetPosition;
    private Transform chaseTarget;
    private float nextThinkTime;
    private float waitUntilTime;
    private float destinationExpireTime;
    private bool hasWanderDestination;
    private bool hasLastKnownTargetPosition;
    private bool hasAnimationPositionSample;
    private Vector3 lastAnimationPosition;
    private Animator cachedAnimatorForParameters;
    private string cachedWalkingBoolParameter;
    private string cachedRunningBoolParameter;
    private string cachedIdleBoolParameter;
    private int walkingBoolHash;
    private int runningBoolHash;
    private int idleBoolHash;
    private bool hasWalkingBoolParameter;
    private bool hasRunningBoolParameter;
    private bool hasIdleBoolParameter;
    private VoxelMobBehaviorType behaviorBeforeChase = VoxelMobBehaviorType.Wander;

    public VoxelMobBehaviorType CurrentBehavior => currentBehavior;
    public Transform ChaseTarget => chaseTarget;
    public bool HasChaseTarget => chaseTarget != null;

    private void Awake()
    {
        agent = GetComponent<VoxelMobPathAgent>();
        ResolveVision();
        ResolveAnimator();
        startPosition = transform.position;
        lastAnimationPosition = transform.position;
        hasAnimationPositionSample = true;
    }

    private void OnEnable()
    {
        lastAnimationPosition = transform.position;
        hasAnimationPositionSample = true;
    }

    private void OnDisable()
    {
        ApplyAnimatorBools(false, false, true);
        hasAnimationPositionSample = false;
    }

    private void OnValidate()
    {
        wanderRadius = Mathf.Max(1f, wanderRadius);
        minWanderDistance = Mathf.Clamp(minWanderDistance, 0f, wanderRadius);
        maxIdleTime = Mathf.Max(minIdleTime, maxIdleTime);
        maxChaseDistance = Mathf.Max(1f, maxChaseDistance);
        movingSpeedThreshold = Mathf.Max(0f, movingSpeedThreshold);
    }

    private void Update()
    {
        if (Time.time < nextThinkTime)
            return;

        nextThinkTime = Time.time + thinkInterval;
        ResolveVision();

        if (chaseVisibleTargets && currentBehavior != VoxelMobBehaviorType.Chase && TryGetVisibleChaseTarget(out Transform visibleTarget))
            EnterChase(visibleTarget);

        switch (currentBehavior)
        {
            case VoxelMobBehaviorType.Wander:
                TickWander();
                break;

            case VoxelMobBehaviorType.Chase:
                TickChase();
                break;

            case VoxelMobBehaviorType.None:
                agent.ResetPath();
                hasWanderDestination = false;
                break;
        }
    }

    private void LateUpdate()
    {
        UpdateAnimatorState();
    }

    public void SetBehavior(VoxelMobBehaviorType newBehavior)
    {
        if (currentBehavior == newBehavior)
            return;

        if (newBehavior == VoxelMobBehaviorType.Chase && currentBehavior != VoxelMobBehaviorType.Chase)
            behaviorBeforeChase = currentBehavior;

        currentBehavior = newBehavior;
        ResetBehaviorState();
    }

    public void SetChaseTarget(Transform target)
    {
        if (target == null)
        {
            ExitChase();
            return;
        }

        EnterChase(target);
    }

    private void TickWander()
    {
        if (hasWanderDestination)
        {
            if (agent.ReachedDestination)
            {
                agent.ResetPath();
                hasWanderDestination = false;
                waitUntilTime = Time.time + Random.Range(minIdleTime, maxIdleTime);
                return;
            }

            if (Time.time < destinationExpireTime && agent.HasPath)
                return;

            if (Time.time < destinationExpireTime && agent.HasDestination)
                return;

            agent.ResetPath();
            hasWanderDestination = false;
        }

        if (Time.time < waitUntilTime)
            return;

        TryChooseWanderDestination();
    }

    private void TryChooseWanderDestination()
    {
        Vector3 center = wanderAroundStartPosition ? startPosition : transform.position;
        float minDistanceSqr = minWanderDistance * minWanderDistance;

        for (int i = 0; i < destinationAttempts; i++)
        {
            Vector2 offset = Random.insideUnitCircle;
            if (offset.sqrMagnitude <= 0.0001f)
                continue;

            offset = offset.normalized * Random.Range(minWanderDistance, wanderRadius);
            Vector3 candidate = center + new Vector3(offset.x, 0f, offset.y);

            if (!agent.TryGetNearestStandablePosition(candidate, standableSampleRadius, standableVerticalProbe, out Vector3 destination))
                continue;

            if (HorizontalDistanceSqr(agent.FeetPosition, destination) < minDistanceSqr)
                continue;

            currentWanderDestination = destination;
            hasWanderDestination = agent.SetDestination(destination);
            destinationExpireTime = Time.time + destinationTimeout;
            return;
        }

        waitUntilTime = Time.time + Random.Range(minIdleTime, maxIdleTime);
    }

    private void TickChase()
    {
        if (chaseTarget == null)
        {
            ExitChase();
            return;
        }

        if (switchToNearestVisibleTargetWhileChasing &&
            TryGetVisibleChaseTarget(out Transform visibleTarget) &&
            visibleTarget != chaseTarget)
        {
            SetActiveChaseTarget(visibleTarget);
            return;
        }

        lastKnownTargetPosition = chaseTarget.position;
        hasLastKnownTargetPosition = true;

        if (IsChaseTargetTooFar())
        {
            ExitChase();
            return;
        }

        agent.SetTarget(chaseTarget, false);
    }

    private bool TryGetVisibleChaseTarget(out Transform visibleTarget)
    {
        visibleTarget = null;
        if (vision == null)
            return false;

        vision.ScanNow();
        visibleTarget = vision.ClosestVisibleTarget;
        return visibleTarget != null;
    }

    private void EnterChase(Transform target)
    {
        if (currentBehavior != VoxelMobBehaviorType.Chase)
        {
            behaviorBeforeChase = currentBehavior;
            ClearWanderState();
        }

        currentBehavior = VoxelMobBehaviorType.Chase;
        SetActiveChaseTarget(target);
    }

    private void SetActiveChaseTarget(Transform target)
    {
        if (target == null)
            return;

        chaseTarget = target;
        lastKnownTargetPosition = target.position;
        hasLastKnownTargetPosition = true;
        agent.SetTarget(chaseTarget);
    }

    private bool IsChaseTargetTooFar()
    {
        if (chaseTarget == null)
            return true;

        return HorizontalDistanceSqr(transform.position, chaseTarget.position) >
               maxChaseDistance * maxChaseDistance;
    }

    private void ExitChase()
    {
        chaseTarget = null;
        hasLastKnownTargetPosition = false;
        agent.ResetPath();

        if (!returnToPreviousBehaviorAfterChase)
        {
            currentBehavior = VoxelMobBehaviorType.None;
            return;
        }

        currentBehavior = behaviorBeforeChase == VoxelMobBehaviorType.Chase
            ? VoxelMobBehaviorType.Wander
            : behaviorBeforeChase;

        waitUntilTime = Time.time + Random.Range(minIdleTime, maxIdleTime);
    }

    private void ResolveVision()
    {
        if (!autoFindVision || vision != null)
            return;

        vision = GetComponent<VoxelMobVision>();
        if (vision == null)
            vision = GetComponentInChildren<VoxelMobVision>();
    }

    private void ResolveAnimator()
    {
        if (!autoFindAnimator || animator != null)
        {
            RefreshAnimatorParameterCacheIfNeeded();
            return;
        }

        animator = GetComponent<Animator>();
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        RefreshAnimatorParameterCacheIfNeeded();
    }

    private void UpdateAnimatorState()
    {
        if (!updateAnimatorBools)
            return;

        ResolveAnimator();
        if (animator == null)
            return;

        Vector3 currentPosition = transform.position;
        if (!hasAnimationPositionSample)
        {
            lastAnimationPosition = currentPosition;
            hasAnimationPositionSample = true;
            ApplyAnimatorBools(false, false, true);
            return;
        }

        Vector3 horizontalDelta = currentPosition - lastAnimationPosition;
        horizontalDelta.y = 0f;
        float horizontalSpeed = Time.deltaTime > 0.0001f
            ? horizontalDelta.magnitude / Time.deltaTime
            : 0f;

        bool isMoving = horizontalSpeed > movingSpeedThreshold;
        bool isRunning = isMoving && currentBehavior == VoxelMobBehaviorType.Chase;
        bool isWalking = isMoving && currentBehavior == VoxelMobBehaviorType.Wander;
        bool isIdle = !isWalking && !isRunning;

        ApplyAnimatorBools(isWalking, isRunning, isIdle);
        lastAnimationPosition = currentPosition;
    }

    private void RefreshAnimatorParameterCacheIfNeeded()
    {
        if (animator == cachedAnimatorForParameters &&
            walkingBoolParameter == cachedWalkingBoolParameter &&
            runningBoolParameter == cachedRunningBoolParameter &&
            idleBoolParameter == cachedIdleBoolParameter)
        {
            return;
        }

        cachedAnimatorForParameters = animator;
        cachedWalkingBoolParameter = walkingBoolParameter;
        cachedRunningBoolParameter = runningBoolParameter;
        cachedIdleBoolParameter = idleBoolParameter;

        walkingBoolHash = string.IsNullOrWhiteSpace(walkingBoolParameter)
            ? 0
            : Animator.StringToHash(walkingBoolParameter);
        runningBoolHash = string.IsNullOrWhiteSpace(runningBoolParameter)
            ? 0
            : Animator.StringToHash(runningBoolParameter);
        idleBoolHash = string.IsNullOrWhiteSpace(idleBoolParameter)
            ? 0
            : Animator.StringToHash(idleBoolParameter);

        hasWalkingBoolParameter = HasAnimatorBoolParameter(animator, walkingBoolHash);
        hasRunningBoolParameter = HasAnimatorBoolParameter(animator, runningBoolHash);
        hasIdleBoolParameter = HasAnimatorBoolParameter(animator, idleBoolHash);
    }

    private void ApplyAnimatorBools(bool isWalking, bool isRunning, bool isIdle)
    {
        if (animator == null || !updateAnimatorBools)
            return;

        RefreshAnimatorParameterCacheIfNeeded();

        if (hasWalkingBoolParameter)
            animator.SetBool(walkingBoolHash, isWalking);

        if (hasRunningBoolParameter)
            animator.SetBool(runningBoolHash, isRunning);

        if (hasIdleBoolParameter)
            animator.SetBool(idleBoolHash, isIdle);
    }

    private static bool HasAnimatorBoolParameter(Animator targetAnimator, int parameterHash)
    {
        if (targetAnimator == null || parameterHash == 0)
            return false;

        AnimatorControllerParameter[] parameters = targetAnimator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            AnimatorControllerParameter parameter = parameters[i];
            if (parameter.type == AnimatorControllerParameterType.Bool &&
                parameter.nameHash == parameterHash)
            {
                return true;
            }
        }

        return false;
    }

    private void ResetBehaviorState()
    {
        agent.ResetPath();
        ClearWanderState();
        chaseTarget = null;
        hasLastKnownTargetPosition = false;
        nextThinkTime = 0f;
    }

    private void ClearWanderState()
    {
        hasWanderDestination = false;
        waitUntilTime = 0f;
        destinationExpireTime = 0f;
    }

    private void OnDrawGizmosSelected()
    {
        if (drawWanderGizmos)
        {
            Vector3 center = wanderAroundStartPosition && Application.isPlaying ? startPosition : transform.position;
            Gizmos.color = wanderRadiusColor;
            Gizmos.DrawWireSphere(center, wanderRadius);

            if (hasWanderDestination)
            {
                Gizmos.color = wanderDestinationColor;
                Gizmos.DrawSphere(currentWanderDestination + Vector3.up * 0.15f, 0.18f);
                Gizmos.DrawLine(transform.position + Vector3.up * 0.15f, currentWanderDestination + Vector3.up * 0.15f);
            }
        }

        if (!drawChaseGizmos || !Application.isPlaying)
            return;

        Vector3 origin = transform.position + Vector3.up * 0.25f;
        if (chaseTarget != null)
        {
            Gizmos.color = chaseTargetColor;
            Vector3 targetPosition = chaseTarget.position + Vector3.up * 0.25f;
            Gizmos.DrawLine(origin, targetPosition);
            Gizmos.DrawSphere(targetPosition, 0.2f);
        }

        if (hasLastKnownTargetPosition)
        {
            Gizmos.color = lastKnownTargetColor;
            Gizmos.DrawWireSphere(lastKnownTargetPosition + Vector3.up * 0.15f, 0.25f);
        }
    }

    private static float HorizontalDistanceSqr(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }
}
