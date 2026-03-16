using UnityEngine;

[DisallowMultipleComponent]
public class PlayerArmSwing : MonoBehaviour
{
    [Header("Arm References")]
    [SerializeField] private Transform leftArm;
    [SerializeField] private Transform rightArm;
    [SerializeField] private CharacterController characterController;
    [Tooltip("Used only as a fallback to detect movement when no CharacterController is assigned.")]
    [SerializeField] private Transform movementReference;

    [Header("Animation Settings")]
    [SerializeField] private float swingAngle = 30f;
    [SerializeField] private float swingFrequency = 9f;
    [SerializeField] private float maxSpeedForFullSwing = 6f;
    [SerializeField] private float blendSpeed = 12f;
    [SerializeField] private float movementThreshold = 0.05f;
    [Tooltip("Local axis used to rotate the arms. X usually matches Minecraft-style rigs.")]
    [SerializeField] private Vector3 localSwingAxis = Vector3.right;

    private Quaternion leftIdleLocalRotation = Quaternion.identity;
    private Quaternion rightIdleLocalRotation = Quaternion.identity;
    private Vector3 lastReferencePosition;
    private float swingTimer;
    private bool idlePoseCaptured;

    private void Awake()
    {
        ResolveReferences();
        CaptureIdlePose();
        CacheReferencePosition();
    }

    private void OnEnable()
    {
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

        if (leftArm == null && rightArm == null)
            return;

        float horizontalSpeed = GetHorizontalSpeed();
        float swingAmount = 0f;

        if (horizontalSpeed > movementThreshold)
        {
            float normalizedSpeed = Mathf.Clamp01(horizontalSpeed / Mathf.Max(0.01f, maxSpeedForFullSwing));
            swingTimer += Time.deltaTime * swingFrequency * Mathf.Lerp(0.35f, 1f, normalizedSpeed);
            swingAmount = Mathf.Sin(swingTimer) * swingAngle * normalizedSpeed;
        }

        ApplyArmRotation(leftArm, leftIdleLocalRotation, swingAmount);
        ApplyArmRotation(rightArm, rightIdleLocalRotation, -swingAmount);
        CacheReferencePosition();
    }

    [ContextMenu("Capture Idle Pose")]
    public void CaptureIdlePose()
    {
        leftIdleLocalRotation = leftArm != null ? leftArm.localRotation : Quaternion.identity;
        rightIdleLocalRotation = rightArm != null ? rightArm.localRotation : Quaternion.identity;
        idlePoseCaptured = leftArm != null || rightArm != null;
    }

    [ContextMenu("Restore Idle Pose")]
    public void RestoreIdlePose()
    {
        if (leftArm != null)
            leftArm.localRotation = leftIdleLocalRotation;

        if (rightArm != null)
            rightArm.localRotation = rightIdleLocalRotation;
    }

    private void ResolveReferences()
    {
        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        if (characterController == null)
            characterController = GetComponentInParent<CharacterController>();

        if (movementReference == null)
            movementReference = characterController != null ? characterController.transform : transform;
    }

    private float GetHorizontalSpeed()
    {
        Vector3 horizontalVelocity;

        if (characterController != null)
        {
            Vector3 velocity = characterController.velocity;
            horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);
        }
        else
        {
            Vector3 delta = movementReference.position - lastReferencePosition;
            delta.y = 0f;
            horizontalVelocity = delta / Mathf.Max(Time.deltaTime, 0.0001f);
        }

        return horizontalVelocity.magnitude;
    }

    private void ApplyArmRotation(Transform arm, Quaternion idleRotation, float swingAmount)
    {
        if (arm == null)
            return;

        Vector3 axis = localSwingAxis.sqrMagnitude > 0.0001f ? localSwingAxis.normalized : Vector3.right;
        Quaternion targetRotation = idleRotation * Quaternion.AngleAxis(swingAmount, axis);
        float blendFactor = blendSpeed <= 0f ? 1f : 1f - Mathf.Exp(-blendSpeed * Time.deltaTime);
        arm.localRotation = Quaternion.Slerp(arm.localRotation, targetRotation, blendFactor);
    }

    private void CacheReferencePosition()
    {
        if (movementReference == null)
            movementReference = characterController != null ? characterController.transform : transform;

        lastReferencePosition = movementReference.position;
    }

    private void OnValidate()
    {
        swingFrequency = Mathf.Max(0.01f, swingFrequency);
        maxSpeedForFullSwing = Mathf.Max(0.01f, maxSpeedForFullSwing);
        blendSpeed = Mathf.Max(0f, blendSpeed);
        movementThreshold = Mathf.Max(0f, movementThreshold);
    }
}
