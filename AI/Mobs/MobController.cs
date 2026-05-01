using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(VoxelMobPathAgent))]
public sealed class MobController : MonoBehaviour
{
    private const int TargetBufferSize = 64;

    [Header("Config")]
    [SerializeField] private MobConfig config;

    [Header("References")]
    [SerializeField] private VoxelMobPathAgent pathAgent;
    [SerializeField] private VoxelMobVision vision;
    [SerializeField] private VoxelMobHealth health;
    [SerializeField] private Animator animator;
    [SerializeField] private Transform owner;

    [Header("Compatibility")]
    [SerializeField] private bool autoResolveReferences = true;

    private readonly Collider[] targetBuffer = new Collider[TargetBufferSize];
    private StateMachine stateMachine;
    private IdleState idleState;
    private WanderState wanderState;
    private ChaseState chaseState;
    private AttackState attackState;
    private FleeState fleeState;
    private FollowState followState;
    private Transform currentTarget;
    private Transform threatTarget;
    private Vector3 spawnPosition;
    private Vector3 lastKnownTargetPosition;
    private bool hasLastKnownTargetPosition;
    private MobLocomotionMode locomotionMode = MobLocomotionMode.Idle;
    private Vector3 lastAnimationPosition;
    private bool hasAnimationPositionSample;
    private Animator cachedAnimatorForParameters;
    private string cachedWalkingBoolParameter;
    private string cachedRunningBoolParameter;
    private string cachedIdleBoolParameter;
    private string cachedAttackTriggerParameter;
    private int walkingBoolHash;
    private int runningBoolHash;
    private int idleBoolHash;
    private int attackTriggerHash;
    private bool hasWalkingBoolParameter;
    private bool hasRunningBoolParameter;
    private bool hasIdleBoolParameter;
    private bool hasAttackTriggerParameter;
    private float animationMoveHoldUntilTime;

    public MobConfig Config => config;
    public StateMachine StateMachine => stateMachine;
    public VoxelMobPathAgent Agent => pathAgent;
    public VoxelMobVision Vision => vision;
    public VoxelMobHealth Health => health;
    public MobType MobType => config != null ? config.MobType : null;
    public Transform Owner => owner;
    public Transform CurrentTarget => currentTarget;
    public Transform ThreatTarget => threatTarget;
    public Vector3 SpawnPosition => spawnPosition;
    public Vector3 LastKnownTargetPosition => lastKnownTargetPosition;
    public bool HasLastKnownTargetPosition => hasLastKnownTargetPosition;
    public string CurrentStateName => stateMachine != null ? stateMachine.CurrentStateName : string.Empty;
    public MobLocomotionMode LocomotionMode => locomotionMode;
    public bool AgentHasPath => pathAgent != null && pathAgent.HasPath;
    public bool AgentHasDestination => pathAgent != null && pathAgent.HasDestination;
    public bool AgentReachedDestination => pathAgent == null || pathAgent.ReachedDestination;

    private void Awake()
    {
        ResolveReferences();
        BuildStateMachine();
        spawnPosition = transform.position;
        lastAnimationPosition = transform.position;
        hasAnimationPositionSample = true;

        if (health != null)
            health.Damaged += HandleDamaged;
    }

    private void Start()
    {
        EnterInitialState();
    }

    private void OnEnable()
    {
        lastAnimationPosition = transform.position;
        hasAnimationPositionSample = true;
    }

    private void OnDisable()
    {
        StopMoving();
        ApplyAnimatorBools(false, false, true);
        hasAnimationPositionSample = false;
    }

    private void OnDestroy()
    {
        if (health != null)
            health.Damaged -= HandleDamaged;
    }

    private void Update()
    {
        if (autoResolveReferences)
            ResolveReferences();

        if (config == null)
        {
            StopMoving();
            return;
        }

        stateMachine?.Update();
    }

    private void LateUpdate()
    {
        UpdateAnimatorState();
    }

    public void SetConfig(MobConfig newConfig, bool restartState = true)
    {
        config = newConfig;
        RefreshAnimatorParameterCacheIfNeeded();

        if (restartState && isActiveAndEnabled && stateMachine != null)
            EnterInitialState();
    }

    public void SetOwner(Transform newOwner)
    {
        owner = newOwner;
    }

    public void SetTarget(Transform target)
    {
        currentTarget = target;
        if (target != null)
            RememberTargetPosition(target.position);
    }

    public void ClearTarget()
    {
        currentTarget = null;
    }

    public void SetThreat(Transform threat)
    {
        threatTarget = threat;
    }

    public void ClearThreat()
    {
        threatTarget = null;
    }

    public void RememberTargetPosition(Vector3 position)
    {
        lastKnownTargetPosition = position;
        hasLastKnownTargetPosition = true;
    }

    public void SetLocomotionMode(MobLocomotionMode mode)
    {
        locomotionMode = mode;
        if (mode == MobLocomotionMode.Idle)
            animationMoveHoldUntilTime = 0f;
    }

    public bool MoveTo(Vector3 destination, float speed, float stopDistance, bool forceRepath = true)
    {
        if (pathAgent == null)
            return false;

        pathAgent.MoveSpeed = speed;
        pathAgent.StopDistance = stopDistance;
        return pathAgent.SetDestination(destination, forceRepath);
    }

    public bool MoveToTarget(Transform target, float speed, float stopDistance, bool forceRepath = false)
    {
        if (pathAgent == null || target == null)
            return false;

        pathAgent.MoveSpeed = speed;
        pathAgent.StopDistance = stopDistance;
        pathAgent.SetTarget(target, forceRepath);
        return true;
    }

    public void StopMoving()
    {
        if (pathAgent != null)
            pathAgent.ResetPath();
    }

    public bool TryGetNearestStandablePosition(
        Vector3 preferredPosition,
        int horizontalRadius,
        int verticalRadius,
        out Vector3 standablePosition)
    {
        if (pathAgent == null)
        {
            standablePosition = default;
            return false;
        }

        return pathAgent.TryGetNearestStandablePosition(
            preferredPosition,
            horizontalRadius,
            verticalRadius,
            out standablePosition);
    }

    public void EnterIdleState()
    {
        stateMachine?.ChangeState(idleState);
    }

    public void EnterWanderState()
    {
        stateMachine?.ChangeState(wanderState);
    }

    public void EnterChaseState()
    {
        stateMachine?.ChangeState(chaseState);
    }

    public void EnterAttackState()
    {
        stateMachine?.ChangeState(attackState);
    }

    public void EnterFleeState()
    {
        stateMachine?.ChangeState(fleeState);
    }

    public void EnterFollowState()
    {
        stateMachine?.ChangeState(followState);
    }

    public void EnterDefaultState()
    {
        if (config != null && config.IsPet && owner != null)
        {
            EnterFollowState();
            return;
        }

        if (config != null && config.CanWander)
        {
            EnterWanderState();
            return;
        }

        EnterIdleState();
    }

    public bool TryEnterPriorityState()
    {
        if (TryEnterFleeState())
            return true;

        if (config != null &&
            config.FleeFromEnemiesOnSight &&
            TryFindBestTarget(out Transform threat))
        {
            SetThreat(threat);
            EnterFleeState();
            return true;
        }

        if (config != null &&
            config.CanAttackOnSight &&
            TryFindBestTarget(out Transform target))
        {
            SetTarget(target);
            EnterChaseState();
            return true;
        }

        return false;
    }

    public bool TryEnterFleeState()
    {
        if (config == null || !config.CanFlee || !config.FleeWhenDamaged || threatTarget == null)
            return false;

        if (IsThreatAtSafeDistance())
        {
            ClearThreat();
            return false;
        }

        EnterFleeState();
        return true;
    }

    public bool ShouldFollowOwner()
    {
        if (config == null || !config.IsPet || owner == null)
            return false;

        float followStartSqr = config.FollowStartDistance * config.FollowStartDistance;
        return HorizontalDistanceSqr(transform.position, owner.position) > followStartSqr;
    }

    public bool IsCurrentTargetTooFar()
    {
        if (config == null || currentTarget == null)
            return true;

        float maxDistanceSqr = config.MaxChaseDistance * config.MaxChaseDistance;
        return HorizontalDistanceSqr(transform.position, currentTarget.position) > maxDistanceSqr;
    }

    public bool IsCurrentTargetValid()
    {
        if (currentTarget == null)
            return false;

        if (currentTarget == transform || currentTarget.IsChildOf(transform))
            return false;

        if (config != null && config.RetaliateWhenDamaged && currentTarget == threatTarget)
            return IsTargetAlive(currentTarget);

        return IsValidTarget(currentTarget);
    }

    public bool IsCurrentTargetInAttackRange()
    {
        if (config == null || currentTarget == null)
            return false;

        if (!IsCurrentTargetValid())
            return false;

        Vector3 origin = transform.position;
        Vector3 targetPosition = currentTarget.position;
        if (Mathf.Abs(origin.y - targetPosition.y) > config.AttackVerticalRange)
            return false;

        float attackRangeSqr = config.AttackRange * config.AttackRange;
        return HorizontalDistanceSqr(origin, targetPosition) <= attackRangeSqr;
    }

    public bool IsThreatAtSafeDistance()
    {
        if (config == null || threatTarget == null)
            return true;

        float safeDistanceSqr = config.SafeDistance * config.SafeDistance;
        return HorizontalDistanceSqr(transform.position, threatTarget.position) >= safeDistanceSqr;
    }

    public float HorizontalDistanceSqrTo(Vector3 worldPosition)
    {
        return HorizontalDistanceSqr(transform.position, worldPosition);
    }

    public void FaceCurrentTarget()
    {
        if (currentTarget == null)
            return;

        Vector3 direction = currentTarget.position - transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude <= 0.0001f)
            return;

        transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
    }

    public void TriggerAttackAnimation()
    {
        if (animator == null)
            return;

        RefreshAnimatorParameterCacheIfNeeded();

        if (!hasAttackTriggerParameter)
            return;

        animator.ResetTrigger(attackTriggerHash);
        animator.SetTrigger(attackTriggerHash);
    }

    public bool DealDamageToCurrentTarget()
    {
        if (config == null || currentTarget == null || !IsCurrentTargetInAttackRange())
            return false;

        Vector3 hitDirection = currentTarget.position - transform.position;
        if (hitDirection.sqrMagnitude <= 0.0001f)
            hitDirection = transform.forward;
        else
            hitDirection.Normalize();

        Vector3 hitPoint = currentTarget.position;

        VoxelMobHealth targetHealth = currentTarget.GetComponentInParent<VoxelMobHealth>();
        if (targetHealth != null && targetHealth != health && !targetHealth.IsDead)
        {
            return targetHealth.TakeDamage(
                config.AttackDamage,
                gameObject,
                gameObject,
                hitPoint,
                hitDirection,
                config.AttackDamageType);
        }

        PlayerStatus playerStatus = currentTarget.GetComponentInParent<PlayerStatus>();
        if (playerStatus != null && playerStatus.GetHealth() > 0f)
        {
            playerStatus.TakeDamage(config.AttackDamage);
            return true;
        }

        return false;
    }

    public float GetRandomIdleDuration()
    {
        if (config == null)
            return 0.5f;

        return Random.Range(config.MinIdleTime, config.MaxIdleTime);
    }

    public bool TryChooseWanderDestination(out Vector3 destination)
    {
        destination = default;

        if (config == null)
            return false;

        Vector3 center = config.WanderAroundSpawnPosition ? spawnPosition : transform.position;
        float minDistanceSqr = config.MinWanderDistance * config.MinWanderDistance;
        float maxRadius = Mathf.Max(config.MinWanderDistance, config.WanderRadius);

        for (int i = 0; i < config.DestinationAttempts; i++)
        {
            Vector2 offset = Random.insideUnitCircle;
            if (offset.sqrMagnitude <= 0.0001f)
                continue;

            float distance = Random.Range(config.MinWanderDistance, maxRadius);
            offset = offset.normalized * distance;
            Vector3 candidate = center + new Vector3(offset.x, 0f, offset.y);

            if (!TryGetNearestStandablePosition(
                    candidate,
                    config.StandableSampleRadius,
                    config.StandableVerticalProbe,
                    out Vector3 standablePosition))
            {
                continue;
            }

            Vector3 currentFeet = pathAgent != null ? pathAgent.FeetPosition : transform.position;
            if (HorizontalDistanceSqr(currentFeet, standablePosition) < minDistanceSqr)
                continue;

            destination = standablePosition;
            return true;
        }

        return false;
    }

    public bool TryChooseFleeDestination(out Vector3 destination)
    {
        destination = default;

        if (config == null || threatTarget == null)
            return false;

        Vector3 away = transform.position - threatTarget.position;
        away.y = 0f;
        if (away.sqrMagnitude <= 0.0001f)
            away = -transform.forward;

        away.y = 0f;
        if (away.sqrMagnitude <= 0.0001f)
            away = Vector3.back;

        away.Normalize();
        float currentThreatDistanceSqr = HorizontalDistanceSqr(transform.position, threatTarget.position);

        for (int i = 0; i < config.DestinationAttempts; i++)
        {
            float angle = Random.Range(-config.FleeSearchAngle, config.FleeSearchAngle);
            Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) * away;
            float distance = Random.Range(config.FleeDistance * 0.65f, config.FleeDistance);
            Vector3 candidate = transform.position + direction * distance;

            if (!TryGetNearestStandablePosition(
                    candidate,
                    config.StandableSampleRadius,
                    config.StandableVerticalProbe,
                    out Vector3 standablePosition))
            {
                continue;
            }

            if (HorizontalDistanceSqr(standablePosition, threatTarget.position) <= currentThreatDistanceSqr)
                continue;

            destination = standablePosition;
            return true;
        }

        return false;
    }

    public bool TryFindBestTarget(out Transform target)
    {
        target = null;

        if (config == null)
            return false;

        if (config.TargetSelector != null &&
            config.TargetSelector.TrySelectTarget(this, out target) &&
            IsValidTarget(target))
        {
            return true;
        }

        return TryFindNearestTarget(out target);
    }

    public bool TryFindNearestTarget(out Transform target)
    {
        target = null;

        if (config == null)
            return false;

        Vector3 origin = transform.position;
        float detectionRangeSqr = config.DetectionRange * config.DetectionRange;
        float bestDistanceSqr = float.PositiveInfinity;

        if (config.UseVisionSensor && vision != null)
        {
            vision.ScanNow();
            var visibleTargets = vision.VisibleTargets;
            for (int i = 0; i < visibleTargets.Count; i++)
                TryConsiderTarget(visibleTargets[i], origin, detectionRangeSqr, ref bestDistanceSqr, ref target);

            if (target != null)
                return true;
        }

        int hitCount = Physics.OverlapSphereNonAlloc(
            origin,
            config.DetectionRange,
            targetBuffer,
            config.TargetLayers,
            config.TargetTriggerInteraction);

        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = targetBuffer[i];
            if (hit == null)
                continue;

            Transform resolvedTarget = ResolveTargetTransform(hit.transform);
            TryConsiderTarget(resolvedTarget, origin, detectionRangeSqr, ref bestDistanceSqr, ref target);
        }

        return target != null;
    }

    private void TryConsiderTarget(
        Transform candidate,
        Vector3 origin,
        float maxDistanceSqr,
        ref float bestDistanceSqr,
        ref Transform bestTarget)
    {
        if (!IsValidTarget(candidate))
            return;

        float distanceSqr = HorizontalDistanceSqr(origin, candidate.position);
        if (distanceSqr > maxDistanceSqr || distanceSqr >= bestDistanceSqr)
            return;

        bestDistanceSqr = distanceSqr;
        bestTarget = candidate;
    }

    private Transform ResolveTargetTransform(Transform hitTransform)
    {
        if (hitTransform == null)
            return null;

        MobController targetMob = hitTransform.GetComponentInParent<MobController>();
        if (targetMob != null)
            return targetMob.transform;

        VoxelMobHealth targetHealth = hitTransform.GetComponentInParent<VoxelMobHealth>();
        if (targetHealth != null)
            return targetHealth.transform;

        FPSController player = hitTransform.GetComponentInParent<FPSController>();
        if (player != null)
            return player.transform;

        if (config != null && config.FilterTargetsByTag)
        {
            Transform current = hitTransform;
            while (current != null)
            {
                if (HasAllowedTargetTag(current))
                    return current;

                current = current.parent;
            }

            return null;
        }

        return hitTransform.root != null ? hitTransform.root : hitTransform;
    }

    private bool IsValidTarget(Transform candidate)
    {
        if (candidate == null || candidate == transform || candidate.IsChildOf(transform))
            return false;

        MobController targetMob = candidate.GetComponentInParent<MobController>();
        if (targetMob != null)
            return IsValidEnemyMob(targetMob);

        if (config != null && config.FilterTargetsByTag && !HasAllowedTargetTagInParents(candidate))
            return false;

        VoxelMobHealth targetHealth = candidate.GetComponentInParent<VoxelMobHealth>();
        if (targetHealth != null && (targetHealth == health || targetHealth.IsDead))
            return false;

        return true;
    }

    private bool IsTargetAlive(Transform candidate)
    {
        if (candidate == null)
            return false;

        VoxelMobHealth targetHealth = candidate.GetComponentInParent<VoxelMobHealth>();
        if (targetHealth != null)
            return targetHealth != health && !targetHealth.IsDead;

        PlayerStatus playerStatus = candidate.GetComponentInParent<PlayerStatus>();
        if (playerStatus != null)
            return playerStatus.GetHealth() > 0f;

        return true;
    }

    private bool IsValidEnemyMob(MobController targetMob)
    {
        if (targetMob == null || targetMob == this)
            return false;

        Transform targetTransform = targetMob.transform;
        if (targetTransform == transform || targetTransform.IsChildOf(transform))
            return false;

        if (targetMob.Health != null && targetMob.Health.IsDead)
            return false;

        if (config == null || targetMob.Config == null)
            return false;

        return config.IsEnemyMobType(targetMob.Config.MobType);
    }

    private bool HasAllowedTargetTagInParents(Transform candidate)
    {
        Transform current = candidate;
        while (current != null)
        {
            if (HasAllowedTargetTag(current))
                return true;

            current = current.parent;
        }

        return false;
    }

    private bool HasAllowedTargetTag(Transform candidate)
    {
        if (candidate == null || config == null)
            return false;

        string[] tags = config.TargetTags;
        if (tags == null || tags.Length == 0)
            return false;

        string candidateTag = candidate.gameObject.tag;
        for (int i = 0; i < tags.Length; i++)
        {
            string targetTag = tags[i];
            if (!string.IsNullOrEmpty(targetTag) && candidateTag == targetTag)
                return true;
        }

        return false;
    }

    private void HandleDamaged(VoxelMobHealth damagedHealth, VoxelMobDamageInfo damageInfo)
    {
        GameObject source = damageInfo.BestSource;
        if (source == null)
            return;

        Transform sourceTransform = source.transform;
        if (sourceTransform == transform || sourceTransform.IsChildOf(transform))
            return;

        threatTarget = sourceTransform;

        if (config == null || !isActiveAndEnabled || stateMachine == null)
            return;

        if (config.CanFlee && config.FleeWhenDamaged)
        {
            EnterFleeState();
            return;
        }

        if (config.RetaliateWhenDamaged)
        {
            SetTarget(sourceTransform);
            EnterChaseState();
        }
    }

    private void ResolveReferences()
    {
        if (pathAgent == null)
            pathAgent = GetComponent<VoxelMobPathAgent>();

        if (vision == null)
            vision = GetComponent<VoxelMobVision>();

        if (vision == null)
            vision = GetComponentInChildren<VoxelMobVision>();

        if (health == null)
            health = GetComponent<VoxelMobHealth>();

        if (animator == null)
            animator = GetComponent<Animator>();

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        RefreshAnimatorParameterCacheIfNeeded();
    }

    private void BuildStateMachine()
    {
        stateMachine = new StateMachine();
        idleState = new IdleState(this);
        wanderState = new WanderState(this);
        chaseState = new ChaseState(this);
        attackState = new AttackState(this);
        fleeState = new FleeState(this);
        followState = new FollowState(this);
    }

    private void EnterInitialState()
    {
        if (config == null)
        {
            EnterIdleState();
            return;
        }

        switch (config.InitialState)
        {
            case MobInitialState.Follow:
                if (owner != null)
                {
                    EnterFollowState();
                    return;
                }

                EnterDefaultState();
                break;

            case MobInitialState.Wander:
                if (config.CanWander)
                {
                    EnterWanderState();
                    return;
                }

                EnterIdleState();
                break;

            default:
                EnterIdleState();
                break;
        }
    }

    private void UpdateAnimatorState()
    {
        if (animator == null || config == null)
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
        float speed = Time.deltaTime > 0.0001f
            ? horizontalDelta.magnitude / Time.deltaTime
            : 0f;

        bool isActuallyMoving = speed > config.AnimationMoveThreshold;
        bool hasMoveIntent = config.AnimateFromMoveIntent && HasAnimationMoveIntent();
        if (isActuallyMoving || hasMoveIntent)
        {
            float holdTime = config.AnimationMoveHoldTime > 0f ? config.AnimationMoveHoldTime : 0.2f;
            animationMoveHoldUntilTime = Time.time + holdTime;
        }

        bool isMoving = locomotionMode != MobLocomotionMode.Idle && Time.time <= animationMoveHoldUntilTime;
        bool isWalking = isMoving && locomotionMode == MobLocomotionMode.Walk;
        bool isRunning = isMoving && locomotionMode == MobLocomotionMode.Run;
        bool isIdle = !isWalking && !isRunning;

        ApplyAnimatorBools(isWalking, isRunning, isIdle);
        lastAnimationPosition = currentPosition;
    }

    private bool HasAnimationMoveIntent()
    {
        if (locomotionMode == MobLocomotionMode.Idle || pathAgent == null)
            return false;

        if (pathAgent.HasPath)
            return true;

        return pathAgent.HasDestination && !pathAgent.ReachedDestination;
    }

    private void RefreshAnimatorParameterCacheIfNeeded()
    {
        string walkingParameter = config != null ? config.WalkingBoolParameter : "isWalking";
        string runningParameter = config != null ? config.RunningBoolParameter : "isRunning";
        string idleParameter = config != null ? config.IdleBoolParameter : "isIdle";
        string attackTriggerParameter = config != null ? config.AttackTriggerParameter : "attack";

        if (animator == cachedAnimatorForParameters &&
            walkingParameter == cachedWalkingBoolParameter &&
            runningParameter == cachedRunningBoolParameter &&
            idleParameter == cachedIdleBoolParameter &&
            attackTriggerParameter == cachedAttackTriggerParameter)
        {
            return;
        }

        cachedAnimatorForParameters = animator;
        cachedWalkingBoolParameter = walkingParameter;
        cachedRunningBoolParameter = runningParameter;
        cachedIdleBoolParameter = idleParameter;
        cachedAttackTriggerParameter = attackTriggerParameter;

        walkingBoolHash = string.IsNullOrEmpty(walkingParameter) ? 0 : Animator.StringToHash(walkingParameter);
        runningBoolHash = string.IsNullOrEmpty(runningParameter) ? 0 : Animator.StringToHash(runningParameter);
        idleBoolHash = string.IsNullOrEmpty(idleParameter) ? 0 : Animator.StringToHash(idleParameter);
        attackTriggerHash = string.IsNullOrEmpty(attackTriggerParameter) ? 0 : Animator.StringToHash(attackTriggerParameter);

        hasWalkingBoolParameter = HasAnimatorBoolParameter(animator, walkingBoolHash);
        hasRunningBoolParameter = HasAnimatorBoolParameter(animator, runningBoolHash);
        hasIdleBoolParameter = HasAnimatorBoolParameter(animator, idleBoolHash);
        hasAttackTriggerParameter = HasAnimatorTriggerParameter(animator, attackTriggerHash);
    }

    private void ApplyAnimatorBools(bool isWalking, bool isRunning, bool isIdle)
    {
        if (animator == null)
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

    private static bool HasAnimatorTriggerParameter(Animator targetAnimator, int parameterHash)
    {
        if (targetAnimator == null || parameterHash == 0)
            return false;

        AnimatorControllerParameter[] parameters = targetAnimator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            AnimatorControllerParameter parameter = parameters[i];
            if (parameter.type == AnimatorControllerParameterType.Trigger &&
                parameter.nameHash == parameterHash)
            {
                return true;
            }
        }

        return false;
    }

    private static float HorizontalDistanceSqr(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }
}
