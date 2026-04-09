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
    }

    private void OnDisable()
    {
        RestoreIdlePose();
        swingTimer = 0f;
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

        ApplyLimbRotation(leftArm, leftIdleLocalRotation, armSwingAmount, cachedArmSwingAxis, blendFactor);
        ApplyLimbRotation(rightArm, rightIdleLocalRotation, -armSwingAmount, cachedArmSwingAxis, blendFactor);
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
        bool headLookReferenceResolved = !enableHeadLook || headLookReference != null;
        bool thirdPersonReferenceResolved = !enableHeadLook || !onlyLookInThirdPerson || fpsController != null;

        if (!force && movementResolved && headLookReferenceResolved && thirdPersonReferenceResolved)
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
        if (limb == null)
            return;

        Quaternion targetRotation = idleRotation * Quaternion.AngleAxis(swingAmount, swingAxis);
        limb.localRotation = blendFactor >= 1f
            ? targetRotation
            : Quaternion.Lerp(limb.localRotation, targetRotation, blendFactor);
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

    private void OnValidate()
    {
        swingAngle = Mathf.Max(0f, swingAngle);
        legSwingAngle = Mathf.Max(0f, legSwingAngle);
        swingFrequency = Mathf.Max(0.01f, swingFrequency);
        maxSpeedForFullSwing = Mathf.Max(0.01f, maxSpeedForFullSwing);
        blendSpeed = Mathf.Max(0f, blendSpeed);
        movementThreshold = Mathf.Max(0f, movementThreshold);
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
