using UnityEngine;

[DisallowMultipleComponent]
public class PlayerArmSwing : MonoBehaviour
{
    private const float ReferenceRetryInterval = 1f;

    [Header("Arm References")]
    [SerializeField] private Transform leftArm;
    [SerializeField] private Transform rightArm;

    [Header("Leg References")]
    [SerializeField] private Transform leftLeg;
    [SerializeField] private Transform rightLeg;

    [Header("Movement References")]
    [SerializeField] private CharacterController characterController;
    [Tooltip("Used only as a fallback to detect movement when no CharacterController is assigned.")]
    [SerializeField] private Transform movementReference;
    [Tooltip("Optional: used to detect whether the player is in third-person view.")]
    [SerializeField] private FPSController fpsController;

    [Header("Head References")]
    [SerializeField] private Transform head;
    [Tooltip("Usually the player camera transform.")]
    [SerializeField] private Transform headLookReference;

    [Header("Arm Animation Settings")]
    [SerializeField] private float swingAngle = 30f;
    [Tooltip("Local axis used to rotate the arms. X usually matches Minecraft-style rigs.")]
    [SerializeField] private Vector3 localSwingAxis = Vector3.right;

    [Header("Leg Animation Settings")]
    [SerializeField] private float legSwingAngle = 30f;
    [Tooltip("Local axis used to rotate the legs. X usually matches Minecraft-style rigs.")]
    [SerializeField] private Vector3 legSwingAxis = Vector3.right;
    [SerializeField] private bool animateLegsOnlyWhenGrounded = true;

    [Header("Shared Animation Settings")]
    [SerializeField] private float swingFrequency = 9f;
    [SerializeField] private float maxSpeedForFullSwing = 6f;
    [SerializeField] private float blendSpeed = 12f;
    [SerializeField] private float movementThreshold = 0.05f;

    [Header("Block Action Settings")]
    [SerializeField] private PlayerBlockBreaker blockBreaker;
    [SerializeField] private PlayerMobAttack mobAttack;
    [SerializeField] private bool enableBlockActionSwing = true;
    [SerializeField] private float breakActionSwingAngle = 92f;
    [SerializeField] private float breakActionSwingFrequency = 4.75f;
    [SerializeField] private float breakActionBlendSpeed = 18f;
    [SerializeField] private float placeActionSwingAngle = 78f;
    [SerializeField] private float placeActionDuration = 0.18f;

    [Header("Head Look Settings")]
    [SerializeField] private bool enableHeadLook = true;
    [SerializeField] private bool onlyLookInThirdPerson = true;
    [SerializeField] private float maxHeadPitch = 75f;
    [Tooltip("Use -1 to invert head pitch direction for your rig.")]
    [SerializeField] private float headPitchMultiplier = -1f;
    [SerializeField] private Vector3 headPitchAxis = Vector3.right;
    [SerializeField] private float headBlendSpeed = 16f;

    private Quaternion leftIdleLocalRotation = Quaternion.identity;
    private Quaternion rightIdleLocalRotation = Quaternion.identity;
    private Quaternion leftLegIdleLocalRotation = Quaternion.identity;
    private Quaternion rightLegIdleLocalRotation = Quaternion.identity;
    private Quaternion headIdleLocalRotation = Quaternion.identity;
    private Vector3 lastReferencePosition;
    private Vector3 cachedArmSwingAxis = Vector3.right;
    private Vector3 cachedLegSwingAxis = Vector3.right;
    private Vector3 cachedHeadPitchAxis = Vector3.right;
    private float swingTimer;
    private float nextReferenceResolveTime;
    private bool idlePoseCaptured;
    private bool hasAnyAnimatedReference;
    private float breakActionWeight;
    private float breakActionTimer;
    private int lastSeenPlaceActionVersion = -1;
    private int lastSeenAttackActionVersion = -1;
    private float placeActionElapsed = float.PositiveInfinity;

    private void Awake()
    {
        ResolveReferences(force: true);
        RefreshCachedState();
        CaptureIdlePose();
        CacheReferencePosition();
    }

    private void OnEnable()
    {
        ResolveReferences(force: true);
        RefreshCachedState();
        CacheReferencePosition();
        PrimeBlockActionTracking();
    }

    private void OnDisable()
    {
        RestoreIdlePose();
        swingTimer = 0f;
        ResetBlockActionAnimation();
    }

    private void LateUpdate()
    {
        ResolveReferences();

        if (!idlePoseCaptured)
            CaptureIdlePose();

        if (!hasAnyAnimatedReference)
            return;

        float horizontalSpeed = GetHorizontalSpeed();
        float armSwingAmount = 0f;
        float legSwingAmount = 0f;
        float blendFactor = blendSpeed <= 0f ? 1f : 1f - Mathf.Exp(-blendSpeed * Time.deltaTime);

        if (horizontalSpeed > movementThreshold)
        {
            float normalizedSpeed = Mathf.Clamp01(horizontalSpeed / Mathf.Max(0.01f, maxSpeedForFullSwing));
            swingTimer += Time.deltaTime * swingFrequency * Mathf.Lerp(0.35f, 1f, normalizedSpeed);
            float swingPhase = Mathf.Sin(swingTimer) * normalizedSpeed;
            armSwingAmount = swingPhase * swingAngle;

            bool allowLegAnimation = !animateLegsOnlyWhenGrounded || characterController == null || characterController.isGrounded;
            if (allowLegAnimation)
                legSwingAmount = swingPhase * legSwingAngle;
        }

        Quaternion rightArmActionRotation = GetBlockActionRotation();

        ApplyLimbRotation(leftArm, ComposeLimbRotation(leftIdleLocalRotation, armSwingAmount, cachedArmSwingAxis), blendFactor);
        ApplyLimbRotation(rightArm, ComposeLimbRotation(rightIdleLocalRotation, -armSwingAmount, cachedArmSwingAxis, rightArmActionRotation), blendFactor);
        ApplyLimbRotation(leftLeg, leftLegIdleLocalRotation, -legSwingAmount, cachedLegSwingAxis, blendFactor);
        ApplyLimbRotation(rightLeg, rightLegIdleLocalRotation, legSwingAmount, cachedLegSwingAxis, blendFactor);
        ApplyHeadLookRotation(blendFactor);
        CacheReferencePosition();
    }

    [ContextMenu("Capture Idle Pose")]
    public void CaptureIdlePose()
    {
        RefreshCachedState();
        leftIdleLocalRotation = leftArm != null ? leftArm.localRotation : Quaternion.identity;
        rightIdleLocalRotation = rightArm != null ? rightArm.localRotation : Quaternion.identity;
        leftLegIdleLocalRotation = leftLeg != null ? leftLeg.localRotation : Quaternion.identity;
        rightLegIdleLocalRotation = rightLeg != null ? rightLeg.localRotation : Quaternion.identity;
        headIdleLocalRotation = head != null ? head.localRotation : Quaternion.identity;
        idlePoseCaptured = hasAnyAnimatedReference;
    }

    [ContextMenu("Restore Idle Pose")]
    public void RestoreIdlePose()
    {
        if (leftArm != null)
            leftArm.localRotation = leftIdleLocalRotation;

        if (rightArm != null)
            rightArm.localRotation = rightIdleLocalRotation;

        if (leftLeg != null)
            leftLeg.localRotation = leftLegIdleLocalRotation;

        if (rightLeg != null)
            rightLeg.localRotation = rightLegIdleLocalRotation;

        if (head != null)
            head.localRotation = headIdleLocalRotation;
    }

    private void ResolveReferences(bool force = false)
    {
        bool movementResolved = characterController != null && movementReference != null;
        bool blockActionResolved = !enableBlockActionSwing || blockBreaker != null;
        bool headLookReferenceResolved = !enableHeadLook || headLookReference != null;
        bool thirdPersonReferenceResolved = !enableHeadLook || !onlyLookInThirdPerson || fpsController != null;

        if (!force && movementResolved && blockActionResolved && headLookReferenceResolved && thirdPersonReferenceResolved)
            return;

        if (!force && Time.unscaledTime < nextReferenceResolveTime)
            return;

        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        if (characterController == null)
            characterController = GetComponentInParent<CharacterController>();

        if (movementReference == null)
            movementReference = characterController != null ? characterController.transform : transform;

        if (fpsController == null)
            fpsController = GetComponent<FPSController>();

        if (fpsController == null)
            fpsController = GetComponentInParent<FPSController>();

        if (blockBreaker == null)
            blockBreaker = GetComponent<PlayerBlockBreaker>();

        if (blockBreaker == null)
            blockBreaker = GetComponentInParent<PlayerBlockBreaker>();

        if (blockBreaker == null)
            blockBreaker = GetComponentInChildren<PlayerBlockBreaker>();

        if (blockBreaker == null)
            blockBreaker = FindAnyObjectByType<PlayerBlockBreaker>();

        if (mobAttack == null)
            mobAttack = GetComponent<PlayerMobAttack>();

        if (mobAttack == null)
            mobAttack = GetComponentInParent<PlayerMobAttack>();

        if (mobAttack == null)
            mobAttack = GetComponentInChildren<PlayerMobAttack>();

        if (mobAttack == null)
            mobAttack = FindAnyObjectByType<PlayerMobAttack>();

        if (headLookReference == null && fpsController != null)
            headLookReference = fpsController.CameraTransform;

        if (headLookReference == null && Camera.main != null)
            headLookReference = Camera.main.transform;

        nextReferenceResolveTime = Time.unscaledTime + ReferenceRetryInterval;
    }

    private float GetHorizontalSpeed()
    {
        if (characterController != null)
        {
            Vector3 velocity = characterController.velocity;
            return new Vector2(velocity.x, velocity.z).magnitude;
        }

        if (movementReference == null)
            return 0f;

        Vector3 delta = movementReference.position - lastReferencePosition;
        return new Vector2(delta.x, delta.z).magnitude / Mathf.Max(Time.deltaTime, 0.0001f);
    }

    private void ApplyLimbRotation(Transform limb, Quaternion idleRotation, float swingAmount, Vector3 swingAxis, float blendFactor)
    {
        ApplyLimbRotation(limb, ComposeLimbRotation(idleRotation, swingAmount, swingAxis), blendFactor);
    }

    private void ApplyLimbRotation(Transform limb, Quaternion targetRotation, float blendFactor)
    {
        if (limb == null)
            return;

        limb.localRotation = blendFactor >= 1f
            ? targetRotation
            : Quaternion.Lerp(limb.localRotation, targetRotation, blendFactor);
    }

    private Quaternion GetBlockActionRotation()
    {
        if (!enableBlockActionSwing || blockBreaker == null)
        {
            ResetBlockActionAnimation();
            return Quaternion.identity;
        }

        UpdatePlaceActionTrigger();
        UpdateAttackActionTrigger();

        float actionBlendFactor = breakActionBlendSpeed <= 0f
            ? 1f
            : 1f - Mathf.Exp(-breakActionBlendSpeed * Time.deltaTime);

        bool shouldAnimateBreak = blockBreaker.IsBreakInProgress;
        float targetWeight = shouldAnimateBreak ? 1f : 0f;
        breakActionWeight = actionBlendFactor >= 1f
            ? targetWeight
            : Mathf.Lerp(breakActionWeight, targetWeight, actionBlendFactor);

        if (shouldAnimateBreak)
        {
            breakActionTimer += Time.deltaTime * breakActionSwingFrequency;
        }
        else if (breakActionWeight <= 0.001f)
        {
            breakActionTimer = 0f;
        }

        float breakAngle = 0f;
        if (breakActionWeight > 0.001f)
        {
            float phase = breakActionTimer * Mathf.PI * 2f;
            float swing = 0.5f - (0.5f * Mathf.Cos(phase));
            float progressMultiplier = Mathf.Lerp(0.9f, 1f, blockBreaker.BreakProgressNormalized);
            breakAngle = swing * breakActionSwingAngle * breakActionWeight * progressMultiplier;
        }

        float placeAngle = 0f;
        if (placeActionElapsed < float.PositiveInfinity)
        {
            placeActionElapsed += Time.deltaTime;
            float duration = Mathf.Max(0.01f, placeActionDuration);
            float normalizedTime = Mathf.Clamp01(placeActionElapsed / duration);
            placeAngle = Mathf.Sin(normalizedTime * Mathf.PI) * placeActionSwingAngle;

            if (normalizedTime >= 1f)
                placeActionElapsed = float.PositiveInfinity;
        }

        float totalActionAngle = breakAngle + placeAngle;
        if (Mathf.Abs(totalActionAngle) <= 0.001f)
            return Quaternion.identity;

        return Quaternion.AngleAxis(totalActionAngle, cachedArmSwingAxis);
    }

    private void ApplyHeadLookRotation(float defaultBlendFactor)
    {
        if (head == null)
            return;

        bool shouldApplyHeadLook = enableHeadLook && headLookReference != null;
        if (shouldApplyHeadLook && onlyLookInThirdPerson)
            shouldApplyHeadLook = fpsController != null && fpsController.IsThirdPerson();

        Quaternion targetRotation = headIdleLocalRotation;
        if (shouldApplyHeadLook)
        {
            float relativePitch = GetRelativePitchDegrees(headLookReference.forward);
            float clampedPitch = Mathf.Clamp(relativePitch, -maxHeadPitch, maxHeadPitch);
            float headPitch = clampedPitch * headPitchMultiplier;
            targetRotation = headIdleLocalRotation * Quaternion.AngleAxis(headPitch, cachedHeadPitchAxis);
        }

        float headLookBlendFactor = headBlendSpeed <= 0f
            ? defaultBlendFactor
            : 1f - Mathf.Exp(-headBlendSpeed * Time.deltaTime);

        head.localRotation = headLookBlendFactor >= 1f
            ? targetRotation
            : Quaternion.Lerp(head.localRotation, targetRotation, headLookBlendFactor);
    }

    private float GetRelativePitchDegrees(Vector3 worldDirection)
    {
        Vector3 localDirection = transform.InverseTransformDirection(worldDirection);
        float horizontalMagnitude = Mathf.Sqrt((localDirection.x * localDirection.x) + (localDirection.z * localDirection.z));
        if (horizontalMagnitude <= 0.0001f)
            return localDirection.y >= 0f ? 90f : -90f;

        return Mathf.Atan2(localDirection.y, horizontalMagnitude) * Mathf.Rad2Deg;
    }

    private void CacheReferencePosition()
    {
        if (movementReference == null)
            movementReference = characterController != null ? characterController.transform : transform;

        lastReferencePosition = movementReference.position;
    }

    private void PrimeBlockActionTracking()
    {
        lastSeenPlaceActionVersion = blockBreaker != null ? blockBreaker.PlaceActionVersion : -1;
        lastSeenAttackActionVersion = mobAttack != null ? mobAttack.AttackActionVersion : -1;
        placeActionElapsed = float.PositiveInfinity;
    }

    private void ResetBlockActionAnimation()
    {
        breakActionWeight = 0f;
        breakActionTimer = 0f;
        placeActionElapsed = float.PositiveInfinity;
    }

    private void UpdatePlaceActionTrigger()
    {
        if (blockBreaker == null)
            return;

        int currentPlaceActionVersion = blockBreaker.PlaceActionVersion;
        if (lastSeenPlaceActionVersion < 0)
        {
            lastSeenPlaceActionVersion = currentPlaceActionVersion;
            return;
        }

        if (currentPlaceActionVersion == lastSeenPlaceActionVersion)
            return;

        lastSeenPlaceActionVersion = currentPlaceActionVersion;
        placeActionElapsed = 0f;
    }

    private void UpdateAttackActionTrigger()
    {
        if (mobAttack == null)
            return;

        int currentAttackActionVersion = mobAttack.AttackActionVersion;
        if (lastSeenAttackActionVersion < 0)
        {
            lastSeenAttackActionVersion = currentAttackActionVersion;
            return;
        }

        if (currentAttackActionVersion == lastSeenAttackActionVersion)
            return;

        lastSeenAttackActionVersion = currentAttackActionVersion;
        placeActionElapsed = 0f;
    }

    private static Quaternion ComposeLimbRotation(Quaternion idleRotation, float swingAmount, Vector3 swingAxis)
    {
        Quaternion swingRotation = Quaternion.AngleAxis(swingAmount, swingAxis);
        return idleRotation * swingRotation;
    }

    private static Quaternion ComposeLimbRotation(Quaternion idleRotation, float swingAmount, Vector3 swingAxis, Quaternion extraRotation)
    {
        Quaternion swingRotation = Quaternion.AngleAxis(swingAmount, swingAxis);
        return idleRotation * swingRotation * extraRotation;
    }

    private void OnValidate()
    {
        swingAngle = Mathf.Max(0f, swingAngle);
        legSwingAngle = Mathf.Max(0f, legSwingAngle);
        swingFrequency = Mathf.Max(0.01f, swingFrequency);
        maxSpeedForFullSwing = Mathf.Max(0.01f, maxSpeedForFullSwing);
        blendSpeed = Mathf.Max(0f, blendSpeed);
        movementThreshold = Mathf.Max(0f, movementThreshold);
        breakActionSwingFrequency = Mathf.Max(0.01f, breakActionSwingFrequency);
        breakActionBlendSpeed = Mathf.Max(0f, breakActionBlendSpeed);
        placeActionDuration = Mathf.Max(0.01f, placeActionDuration);
        maxHeadPitch = Mathf.Clamp(maxHeadPitch, 0f, 89f);
        headBlendSpeed = Mathf.Max(0f, headBlendSpeed);
        RefreshCachedState();
    }

    private void RefreshCachedState()
    {
        hasAnyAnimatedReference = leftArm != null || rightArm != null || leftLeg != null || rightLeg != null || head != null;
        cachedArmSwingAxis = localSwingAxis.sqrMagnitude > 0.0001f ? localSwingAxis.normalized : Vector3.right;
        cachedLegSwingAxis = legSwingAxis.sqrMagnitude > 0.0001f ? legSwingAxis.normalized : Vector3.right;
        cachedHeadPitchAxis = headPitchAxis.sqrMagnitude > 0.0001f ? headPitchAxis.normalized : Vector3.right;
    }
}
