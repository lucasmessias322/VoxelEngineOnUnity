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

    private Quaternion leftIdleLocalRotation = Quaternion.identity;
    private Quaternion rightIdleLocalRotation = Quaternion.identity;
    private Quaternion leftLegIdleLocalRotation = Quaternion.identity;
    private Quaternion rightLegIdleLocalRotation = Quaternion.identity;
    private Vector3 lastReferencePosition;
    private Vector3 cachedArmSwingAxis = Vector3.right;
    private Vector3 cachedLegSwingAxis = Vector3.right;
    private float swingTimer;
    private float nextReferenceResolveTime;
    private bool idlePoseCaptured;
    private bool hasAnyLimbReference;

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

        if (!hasAnyLimbReference)
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
        idlePoseCaptured = hasAnyLimbReference;
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
    }

    private void ResolveReferences(bool force = false)
    {
        if (!force && characterController != null && movementReference != null)
            return;

        if (!force && Time.unscaledTime < nextReferenceResolveTime)
            return;

        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        if (characterController == null)
            characterController = GetComponentInParent<CharacterController>();

        if (movementReference == null)
            movementReference = characterController != null ? characterController.transform : transform;

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
        RefreshCachedState();
    }

    private void RefreshCachedState()
    {
        hasAnyLimbReference = leftArm != null || rightArm != null || leftLeg != null || rightLeg != null;
        cachedArmSwingAxis = localSwingAxis.sqrMagnitude > 0.0001f ? localSwingAxis.normalized : Vector3.right;
        cachedLegSwingAxis = legSwingAxis.sqrMagnitude > 0.0001f ? legSwingAxis.normalized : Vector3.right;
    }
}
