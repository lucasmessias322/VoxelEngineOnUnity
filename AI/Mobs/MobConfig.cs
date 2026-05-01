using UnityEngine;
using UnityEngine.Serialization;

public enum MobInitialState
{
    Idle = 0,
    Wander = 1,
    Follow = 2
}

public enum MobLocomotionMode
{
    Idle = 0,
    Walk = 1,
    Run = 2
}

public enum MobTemperament
{
    Passive = 0,
    Defensive = 1,
    Aggressive = 2
}

[CreateAssetMenu(fileName = "MobConfig", menuName = "Voxel Mobs/Mob Config")]
public sealed class MobConfig : ScriptableObject
{
    [Header("Identity")]
    [SerializeField] private MobType mobType;
    [SerializeField] private MobInitialState initialState = MobInitialState.Wander;

    [Header("Temperament")]
    [SerializeField] private MobTemperament temperament = MobTemperament.Passive;
    [SerializeField] private MobType[] enemyMobTypes;
    [SerializeField] private bool fleeFromEnemiesOnSight = false;
    [SerializeField] private bool canWander = true;
    [SerializeField] private bool isPet = false;

    [Header("Movement")]
    [SerializeField, Min(0f)] private float walkSpeed = 2f;
    [SerializeField, Min(0f)] private float runSpeed = 4f;
    [SerializeField, Min(0f)] private float fleeSpeed = 4.5f;
    [SerializeField, Min(0f)] private float followSpeed = 3f;

    [Header("Detection")]
    [SerializeField] private bool useVisionSensor = true;
    [SerializeField] private MobTargetSelector targetSelector;
    [SerializeField, Min(0.1f)] private float detectionRange = 12f;
    [SerializeField, Min(0.1f)] private float maxChaseDistance = 50f;
    [SerializeField, Min(0.05f)] private float chaseStopDistance = 1.25f;
    [SerializeField] private bool switchTargetWhileChasing = false;
    [SerializeField] private LayerMask targetLayers = ~0;
    [SerializeField] private bool filterTargetsByTag = true;
    [SerializeField] private string[] targetTags = { "Player" };
    [SerializeField] private QueryTriggerInteraction targetTriggerInteraction = QueryTriggerInteraction.Ignore;

    [Header("Wander")]
    [SerializeField, Min(1f)] private float wanderRadius = 8f;
    [SerializeField, Min(0f)] private float minWanderDistance = 2f;
    [SerializeField, Min(0f)] private float minIdleTime = 1f;
    [SerializeField, Min(0f)] private float maxIdleTime = 3f;
    [SerializeField, Min(1)] private int destinationAttempts = 10;
    [SerializeField, Min(0)] private int standableSampleRadius = 2;
    [SerializeField, Min(0)] private int standableVerticalProbe = 4;
    [SerializeField, Min(0.5f)] private float destinationTimeout = 8f;
    [SerializeField, Min(0.05f)] private float wanderStopDistance = 0.75f;
    [SerializeField] private bool wanderAroundSpawnPosition = false;

    [Header("Flee")]
    [SerializeField, Min(0.1f)] private float fleeDuration = 4f;
    [SerializeField, Min(1f)] private float fleeDistance = 10f;
    [SerializeField, Min(1f)] private float safeDistance = 14f;
    [SerializeField, Min(0.1f)] private float fleeDestinationRefreshInterval = 1.25f;
    [SerializeField, Range(0f, 180f)] private float fleeSearchAngle = 80f;
    [SerializeField, Min(0.05f)] private float fleeStopDistance = 0.75f;

    [Header("Attack")]
    [SerializeField, Min(0f)] private float attackDamage = 4f;
    [SerializeField, Min(0.1f)] private float attackRange = 1.7f;
    [SerializeField, Min(0.1f)] private float attackVerticalRange = 2.2f;
    [SerializeField, Min(0f)] private float attackCooldown = 1f;
    [SerializeField, Min(0f)] private float attackWindupTime = 0.2f;
    [SerializeField] private string attackDamageType = "mob_melee";

    [Header("Follow")]
    [SerializeField, Min(0.1f)] private float followStartDistance = 6f;
    [SerializeField, Min(0.05f)] private float followStopDistance = 2.5f;
    [SerializeField] private bool followUsesRunAnimation = false;

    [Header("Animation")]
    [SerializeField] private string walkingBoolParameter = "isWalking";
    [SerializeField] private string runningBoolParameter = "isRunning";
    [SerializeField] private string idleBoolParameter = "isIdle";
    [FormerlySerializedAs("attackingBoolParameter")]
    [SerializeField] private string attackTriggerParameter = "attack";
    [SerializeField, Min(0f)] private float animationMoveThreshold = 0.03f;
    [SerializeField, Min(0f)] private float animationMoveHoldTime = 0.25f;
    [SerializeField] private bool animateFromMoveIntent = true;

    public MobType MobType => mobType;
    public MobInitialState InitialState => initialState;
    public MobTemperament Temperament => temperament;
    public bool CanWander => canWander;
    public bool IsPassive => temperament == MobTemperament.Passive;
    public bool IsDefensive => temperament == MobTemperament.Defensive;
    public bool IsAggressive => temperament == MobTemperament.Aggressive;
    public bool IsPet => isPet;
    public bool CanFlee => temperament == MobTemperament.Passive || fleeFromEnemiesOnSight;
    public bool FleeWhenDamaged => temperament == MobTemperament.Passive;
    public bool RetaliateWhenDamaged => temperament == MobTemperament.Defensive || temperament == MobTemperament.Aggressive;
    public bool CanAttackOnSight => temperament == MobTemperament.Aggressive;
    public bool CanAttack => temperament == MobTemperament.Defensive || temperament == MobTemperament.Aggressive;
    public bool FleeFromEnemiesOnSight => temperament == MobTemperament.Passive && fleeFromEnemiesOnSight;
    public MobType[] EnemyMobTypes => enemyMobTypes;
    public float WalkSpeed => walkSpeed;
    public float RunSpeed => runSpeed;
    public float FleeSpeed => fleeSpeed;
    public float FollowSpeed => followSpeed;
    public bool UseVisionSensor => useVisionSensor;
    public MobTargetSelector TargetSelector => targetSelector;
    public float DetectionRange => detectionRange;
    public float MaxChaseDistance => maxChaseDistance;
    public float ChaseStopDistance => chaseStopDistance;
    public bool SwitchTargetWhileChasing => switchTargetWhileChasing;
    public LayerMask TargetLayers => targetLayers;
    public bool FilterTargetsByTag => filterTargetsByTag;
    public string[] TargetTags => targetTags;
    public QueryTriggerInteraction TargetTriggerInteraction => targetTriggerInteraction;
    public float WanderRadius => wanderRadius;
    public float MinWanderDistance => minWanderDistance;
    public float MinIdleTime => minIdleTime;
    public float MaxIdleTime => maxIdleTime;
    public int DestinationAttempts => destinationAttempts;
    public int StandableSampleRadius => standableSampleRadius;
    public int StandableVerticalProbe => standableVerticalProbe;
    public float DestinationTimeout => destinationTimeout;
    public float WanderStopDistance => wanderStopDistance;
    public bool WanderAroundSpawnPosition => wanderAroundSpawnPosition;
    public float FleeDuration => fleeDuration;
    public float FleeDistance => fleeDistance;
    public float SafeDistance => safeDistance;
    public float FleeDestinationRefreshInterval => fleeDestinationRefreshInterval;
    public float FleeSearchAngle => fleeSearchAngle;
    public float FleeStopDistance => fleeStopDistance;
    public float AttackDamage => attackDamage;
    public float AttackRange => attackRange;
    public float AttackVerticalRange => attackVerticalRange;
    public float AttackCooldown => attackCooldown;
    public float AttackWindupTime => attackWindupTime;
    public string AttackDamageType => attackDamageType;
    public float FollowStartDistance => followStartDistance;
    public float FollowStopDistance => followStopDistance;
    public bool FollowUsesRunAnimation => followUsesRunAnimation;
    public string WalkingBoolParameter => walkingBoolParameter;
    public string RunningBoolParameter => runningBoolParameter;
    public string IdleBoolParameter => idleBoolParameter;
    public string AttackTriggerParameter => attackTriggerParameter;
    public float AnimationMoveThreshold => animationMoveThreshold;
    public float AnimationMoveHoldTime => animationMoveHoldTime;
    public bool AnimateFromMoveIntent => animateFromMoveIntent;

    public bool IsEnemyMobType(MobType candidateType)
    {
        if (candidateType == null || enemyMobTypes == null)
            return false;

        for (int i = 0; i < enemyMobTypes.Length; i++)
        {
            if (enemyMobTypes[i] == candidateType)
                return true;
        }

        return false;
    }

    private void OnValidate()
    {
        walkSpeed = Mathf.Max(0f, walkSpeed);
        runSpeed = Mathf.Max(0f, runSpeed);
        fleeSpeed = Mathf.Max(0f, fleeSpeed);
        followSpeed = Mathf.Max(0f, followSpeed);
        detectionRange = Mathf.Max(0.1f, detectionRange);
        maxChaseDistance = Mathf.Max(0.1f, maxChaseDistance);
        chaseStopDistance = Mathf.Max(0.05f, chaseStopDistance);
        wanderRadius = Mathf.Max(1f, wanderRadius);
        minWanderDistance = Mathf.Clamp(minWanderDistance, 0f, wanderRadius);
        maxIdleTime = Mathf.Max(minIdleTime, maxIdleTime);
        destinationAttempts = Mathf.Max(1, destinationAttempts);
        standableSampleRadius = Mathf.Max(0, standableSampleRadius);
        standableVerticalProbe = Mathf.Max(0, standableVerticalProbe);
        destinationTimeout = Mathf.Max(0.5f, destinationTimeout);
        wanderStopDistance = Mathf.Max(0.05f, wanderStopDistance);
        fleeDuration = Mathf.Max(0.1f, fleeDuration);
        fleeDistance = Mathf.Max(1f, fleeDistance);
        safeDistance = Mathf.Max(1f, safeDistance);
        fleeDestinationRefreshInterval = Mathf.Max(0.1f, fleeDestinationRefreshInterval);
        fleeStopDistance = Mathf.Max(0.05f, fleeStopDistance);
        attackDamage = Mathf.Max(0f, attackDamage);
        attackRange = Mathf.Max(0.1f, attackRange);
        attackVerticalRange = Mathf.Max(0.1f, attackVerticalRange);
        attackCooldown = Mathf.Max(0f, attackCooldown);
        attackWindupTime = Mathf.Max(0f, attackWindupTime);
        followStartDistance = Mathf.Max(0.1f, followStartDistance);
        followStopDistance = Mathf.Clamp(followStopDistance, 0.05f, followStartDistance);
        animationMoveThreshold = Mathf.Max(0f, animationMoveThreshold);
        animationMoveHoldTime = Mathf.Max(0f, animationMoveHoldTime);
    }
}
