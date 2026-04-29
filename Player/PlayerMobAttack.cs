using UnityEngine;

[DisallowMultipleComponent]
public sealed class PlayerMobAttack : MonoBehaviour
{
    private const int HitBufferSize = 32;

    [Header("References")]
    [SerializeField] private Camera attackCamera;
    [SerializeField] private Transform attackOrigin;
    [SerializeField] private GameObject sourceEntity;
    [SerializeField] private AudioSource audioSource;

    [Header("Input")]
    [SerializeField] private bool readInputInUpdate = true;
    [SerializeField] private int mouseButton = 0;
    [SerializeField] private bool holdToAttack = true;
    [SerializeField] private bool ignoreWhenInventoryOpen = true;
    [SerializeField] private bool ignoreUntilWorldReady = true;

    [Header("Attack")]
    [SerializeField, Min(0f)] private float damage = 4f;
    [SerializeField, Min(0.1f)] private float range = 3f;
    [SerializeField, Min(0f)] private float cooldown = 0.5f;
    [SerializeField] private string damageType = "player_melee";
    [SerializeField] private LayerMask hitMask = ~0;
    [SerializeField] private QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Ignore;
    [SerializeField] private bool nonMobCollidersBlockAttack = true;
    [SerializeField, Min(0f)] private float blockerDistancePadding = 0.02f;

    [Header("Feedback")]
    [SerializeField] private AudioClip hitClip;
    [SerializeField] private AudioClip missClip;
    [SerializeField] private bool playMissClip = false;
    [SerializeField] private bool drawDebugRay = false;

    private readonly RaycastHit[] hitBuffer = new RaycastHit[HitBufferSize];
    private float nextAttackTime;
    private int lastHandledFrame = -1;
    private int attackActionVersion;
    private bool blockBreakThisFrame;
    private bool hitThisFrame;
    private VoxelMobHealth lastHitMob;

    public float Damage
    {
        get => damage;
        set => damage = Mathf.Max(0f, value);
    }

    public float Range
    {
        get => range;
        set => range = Mathf.Max(0.1f, value);
    }

    public float Cooldown
    {
        get => cooldown;
        set => cooldown = Mathf.Max(0f, value);
    }

    public bool BlockBreakThisFrame => blockBreakThisFrame;
    public bool HitThisFrame => hitThisFrame;
    public bool IsCoolingDown => Time.time < nextAttackTime;
    public int AttackActionVersion => attackActionVersion;
    public VoxelMobHealth LastHitMob => lastHitMob;

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnValidate()
    {
        damage = Mathf.Max(0f, damage);
        range = Mathf.Max(0.1f, range);
        cooldown = Mathf.Max(0f, cooldown);
        blockerDistancePadding = Mathf.Max(0f, blockerDistancePadding);
        mouseButton = Mathf.Max(0, mouseButton);
    }

    private void Update()
    {
        if (readInputInUpdate)
            TryHandleAttackInput();
        else if (lastHandledFrame != Time.frameCount)
            ResetFrameFlags();
    }

    public bool TryHandleAttackInput()
    {
        if (lastHandledFrame == Time.frameCount)
            return blockBreakThisFrame;

        lastHandledFrame = Time.frameCount;
        ResetFrameFlags();

        if (!CanReadInput() || !IsAttackInputPressed())
            return false;

        return TryHandleAttackAttempt();
    }

    public bool TryAttack()
    {
        if (Time.time < nextAttackTime)
            return false;

        if (!TryFindAttackTarget(out VoxelMobHealth target, out RaycastHit hit))
            return false;

        ApplyDamage(target, hit);
        return true;
    }

    private bool TryHandleAttackAttempt()
    {
        if (!TryFindAttackTarget(out VoxelMobHealth target, out RaycastHit hit))
        {
            if (playMissClip && missClip != null && audioSource != null)
                audioSource.PlayOneShot(missClip);

            return false;
        }

        blockBreakThisFrame = true;

        if (Time.time < nextAttackTime)
            return true;

        ApplyDamage(target, hit);
        return true;
    }

    private void ApplyDamage(VoxelMobHealth target, RaycastHit hit)
    {
        if (target == null)
            return;

        GameObject resolvedSourceEntity = ResolveSourceEntity();
        GameObject sourceObject = gameObject;
        Vector3 hitDirection = ResolveAttackForward();
        Vector3 hitPoint = hit.collider != null ? hit.point : target.transform.position;

        bool damaged = target.TakeDamage(
            damage,
            sourceObject,
            resolvedSourceEntity,
            hitPoint,
            hitDirection,
            damageType);

        nextAttackTime = Time.time + cooldown;
        attackActionVersion++;

        if (!damaged)
            return;

        hitThisFrame = true;
        lastHitMob = target;

        if (hitClip != null && audioSource != null)
            audioSource.PlayOneShot(hitClip);
    }

    private bool TryFindAttackTarget(out VoxelMobHealth target, out RaycastHit targetHit)
    {
        target = null;
        targetHit = default;

        if (!TryResolveAttackRay(out Ray ray))
            return false;

        int hitCount = Physics.RaycastNonAlloc(ray, hitBuffer, range, hitMask, triggerInteraction);
        if (hitCount <= 0)
            return false;

        float nearestMobDistance = float.PositiveInfinity;
        float nearestBlockerDistance = float.PositiveInfinity;

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = hitBuffer[i];
            Collider hitCollider = hit.collider;
            if (hitCollider == null || IsOwnCollider(hitCollider))
                continue;

            VoxelMobHealth mobHealth = hitCollider.GetComponentInParent<VoxelMobHealth>();
            if (mobHealth != null && IsValidTarget(mobHealth))
            {
                if (hit.distance < nearestMobDistance)
                {
                    nearestMobDistance = hit.distance;
                    target = mobHealth;
                    targetHit = hit;
                }

                continue;
            }

            if (nonMobCollidersBlockAttack && hit.distance < nearestBlockerDistance)
                nearestBlockerDistance = hit.distance;
        }

        if (target == null)
            return false;

        if (!nonMobCollidersBlockAttack)
            return true;

        return nearestMobDistance <= nearestBlockerDistance + blockerDistancePadding;
    }

    private bool IsValidTarget(VoxelMobHealth targetHealth)
    {
        if (targetHealth == null || !targetHealth.enabled || targetHealth.IsDead)
            return false;

        GameObject resolvedSourceEntity = ResolveSourceEntity();
        if (resolvedSourceEntity == null)
            return targetHealth.gameObject != gameObject;

        Transform sourceTransform = resolvedSourceEntity.transform;
        Transform targetTransform = targetHealth.transform;
        return targetTransform != sourceTransform && !targetTransform.IsChildOf(sourceTransform);
    }

    private bool IsOwnCollider(Collider hitCollider)
    {
        GameObject resolvedSourceEntity = ResolveSourceEntity();
        if (resolvedSourceEntity == null)
            return hitCollider.transform == transform || hitCollider.transform.IsChildOf(transform);

        Transform sourceTransform = resolvedSourceEntity.transform;
        Transform hitTransform = hitCollider.transform;
        return hitTransform == sourceTransform || hitTransform.IsChildOf(sourceTransform);
    }

    private bool TryResolveAttackRay(out Ray ray)
    {
        ResolveReferences();

        Transform origin = attackOrigin != null
            ? attackOrigin
            : attackCamera != null
                ? attackCamera.transform
                : transform;

        Vector3 forward = origin.forward;
        if (forward.sqrMagnitude <= 0.0001f)
        {
            ray = default;
            return false;
        }

        ray = new Ray(origin.position, forward.normalized);
        return true;
    }

    private Vector3 ResolveAttackForward()
    {
        if (TryResolveAttackRay(out Ray ray))
            return ray.direction;

        return transform.forward;
    }

    private bool CanReadInput()
    {
        if (!isActiveAndEnabled)
            return false;

        if (ignoreUntilWorldReady && World.Instance != null && !World.Instance.IsInitialWorldReady)
            return false;

        if (ignoreWhenInventoryOpen &&
            PlayerInventory.Instance != null &&
            PlayerInventory.Instance.IsInventoryOpen)
        {
            return false;
        }

        return true;
    }

    private bool IsAttackInputPressed()
    {
        return holdToAttack
            ? Input.GetMouseButton(mouseButton)
            : Input.GetMouseButtonDown(mouseButton);
    }

    private GameObject ResolveSourceEntity()
    {
        if (sourceEntity != null)
            return sourceEntity;

        FPSController fpsController = GetComponent<FPSController>();
        if (fpsController == null)
            fpsController = GetComponentInParent<FPSController>();

        sourceEntity = fpsController != null ? fpsController.gameObject : gameObject;
        return sourceEntity;
    }

    private void ResolveReferences()
    {
        if (attackCamera == null)
            attackCamera = GetComponentInChildren<Camera>();

        if (attackCamera == null)
            attackCamera = GetComponentInParent<Camera>();

        if (attackCamera == null)
            attackCamera = Camera.main;

        if (attackOrigin == null && attackCamera != null)
            attackOrigin = attackCamera.transform;

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (audioSource == null)
            audioSource = GetComponentInParent<AudioSource>();

        ResolveSourceEntity();
    }

    private void ResetFrameFlags()
    {
        blockBreakThisFrame = false;
        hitThisFrame = false;
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawDebugRay)
            return;

        Transform origin = attackOrigin != null
            ? attackOrigin
            : attackCamera != null
                ? attackCamera.transform
                : transform;

        Gizmos.color = Color.red;
        Gizmos.DrawLine(origin.position, origin.position + origin.forward * range);
    }
}
