

using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class FPSController : MonoBehaviour
{
    private const int ThirdPersonHitBufferSize = 16;
    private const float LegacyMouseSensitivityReferenceFps = 60f;

    private enum CameraViewMode
    {
        FirstPerson,
        ThirdPerson
    }

    [Header("Movement Settings")]
    [SerializeField] private float walkSpeed = 5f;
    [SerializeField] private float sprintSpeed = 9f;
    [SerializeField] private float crouchSpeed = 2f;
    [SerializeField] private float jumpForce = 7.5f;
    [SerializeField] private float gravity = 20f;
    [Tooltip("Extra forward boost when doing a sprint-jump.")]
    [SerializeField] private float sprintJumpBoost = 2f;

    [Header("Stamina Settings")]
    [SerializeField] private PlayerStatus playerStatus;
    [SerializeField] private float staminaDrainSprint = 10f;
    [SerializeField] private float staminaDrainJump = 20f;
    [SerializeField] private float staminaRecoveryRate = 15f;
    [SerializeField] private float minStaminaToSprint = 1f;

    [Header("Camera Settings")]
    [SerializeField] private float mouseSensitivity = 100f;
    [SerializeField] private Transform cameraTransform;

    [Header("View Mode Settings")]
    [SerializeField] private KeyCode firstPersonKey = KeyCode.F1;
    [SerializeField] private KeyCode thirdPersonKey = KeyCode.F2;
    [SerializeField] private CameraViewMode defaultViewMode = CameraViewMode.FirstPerson;
    [SerializeField] private float thirdPersonDistance = 3.25f;
    [SerializeField] private float thirdPersonHeightOffset = 0.15f;
    [SerializeField] private float thirdPersonShoulderOffset = 0f;
    [SerializeField] private float thirdPersonCollisionRadius = 0.2f;
    [SerializeField] private LayerMask thirdPersonCollisionMask = ~0;
    [SerializeField] private bool hideCameraChildrenInThirdPerson = true;
    [SerializeField] private GameObject[] firstPersonOnlyObjects;

    [Header("Crouch Settings")]
    [SerializeField] private float normalHeight = 2f;
    [SerializeField] private float crouchHeight = 1.2f;
    [Tooltip("Velocidade de transição entre alturas (segundos).")]
    [SerializeField] private float crouchTransitionSpeed = 8f;

    [Header("Flight Settings")]
    [SerializeField] private bool canFly = false;
    [SerializeField] private float flySpeed = 8f;
    [Tooltip("Multiplicador de velocidade de voo quando sprintando (LeftShift).")]
    [SerializeField] private float flySprintMultiplier = 2f;
    [Tooltip("Quão rápido a velocidade horizontal do voo alcança a velocidade alvo (em segundos inversos).")]
    [SerializeField] private float flyAcceleration = 8f;

    [Header("Minecraft-like Settings")]
    [Tooltip("Tempo máximo entre dois taps para reconhecer double-tap (sprint/voo).")]
    [SerializeField] private float doubleTapTime = 0.25f;
    [Tooltip("Número máximo de meshes aplicados por frame (não parte deste script, apenas referência).")]
    [SerializeField] private float stepOffset = 0.5f;

    private CharacterController characterController;
    private Vector3 velocity = Vector3.zero;
    private float xRotation = 0f;
    private readonly RaycastHit[] groundAheadRaycastBuffer = new RaycastHit[1];

    private float lastForwardTapTime = -1f;
    private bool sprintToggled = false;

    private float lastSpaceTapTime = -1f;
    private bool flightToggled = false;
    private bool isFlying = false;

    private Vector3 currentFlyHorizontal = Vector3.zero;
    private Vector3 flySmoothVelocity = Vector3.zero;

    private bool isCrouching = false;
    private float targetHeight;
    public float cameraTargetY;
    private float currentCameraPivotY;
    private float originalStepOffset;
    private bool worldLoadingLocked;
    private CameraViewMode currentViewMode;
    private Vector3 defaultCameraLocalPosition;
    private Transform[] cachedCameraChildren;
    private bool firstPersonVisualsVisible = true;
    [SerializeField] private LayerMask playerMeshLayer;
    private readonly RaycastHit[] thirdPersonHitBuffer = new RaycastHit[ThirdPersonHitBufferSize];
    private bool spentStaminaThisFrame;
    private bool loggedMissingPlayerStatus;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        characterController = GetComponent<CharacterController>();
        if (characterController == null) Debug.LogError("CharacterController component is missing!");

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        ResolvePlayerStatus(logWarning: true);

        CacheFirstPersonOnlyReferences();

        targetHeight = normalHeight;
        currentCameraPivotY = cameraTargetY;
        originalStepOffset = characterController.stepOffset;
        characterController.stepOffset = stepOffset;
        currentViewMode = defaultViewMode;

        if (cameraTransform != null)
        {
            defaultCameraLocalPosition = cameraTransform.localPosition;
            defaultCameraLocalPosition.y = cameraTargetY;
            UpdateCameraTransform(forceVisualRefresh: true);
            UpdateCameraCulling();
        }
    }

    void Update()
    {
        if (worldLoadingLocked)
        {
            velocity = Vector3.zero;
            currentFlyHorizontal = Vector3.zero;
            flySmoothVelocity = Vector3.zero;
            return;
        }

        HandleViewModeInput();
        HandleRotation();
      //  HandleCrouchInput();
        EnforceCreativeFlightPermission();
        HandleFlightToggle();
        spentStaminaThisFrame = false;
        HandleMovement();
        HandleStaminaRecovery();
       // SmoothCrouchTransition();
    }

    private void HandleRotation()
    {
        if (PlayerInventory.Instance != null && PlayerInventory.Instance.IsInventoryOpen)
            return;

        float lookSensitivity = mouseSensitivity / LegacyMouseSensitivityReferenceFps;
        float mouseX = Input.GetAxis("Mouse X") * lookSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * lookSensitivity;

        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f);

        transform.Rotate(Vector3.up * mouseX);
        UpdateCameraTransform();
    }

    private void HandleViewModeInput()
    {
        if (Input.GetKeyDown(firstPersonKey))
        {
            SetViewMode(CameraViewMode.FirstPerson);
        }
        else if (Input.GetKeyDown(thirdPersonKey))
        {
            SetViewMode(CameraViewMode.ThirdPerson);
        }
    }

    private void SetViewMode(CameraViewMode viewMode)
    {
        if (currentViewMode == viewMode)
            return;

        currentViewMode = viewMode;

        UpdateCameraTransform(forceVisualRefresh: true);
        UpdateCameraCulling();
    }

    private void CacheFirstPersonOnlyReferences()
    {
        if (cameraTransform != null)
        {
            cachedCameraChildren = new Transform[cameraTransform.childCount];
            for (int i = 0; i < cameraTransform.childCount; i++)
                cachedCameraChildren[i] = cameraTransform.GetChild(i);
        }

    }

    private void UpdateCameraTransform(bool forceVisualRefresh = false)
    {
        if (cameraTransform == null)
            return;

        UpdateFirstPersonVisuals(forceVisualRefresh);
        cameraTransform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        cameraTransform.localPosition = GetTargetCameraLocalPosition();
    }

    private Vector3 GetTargetCameraLocalPosition()
    {
        Vector3 pivotLocal = defaultCameraLocalPosition;
        pivotLocal.y = currentCameraPivotY;

        if (currentViewMode == CameraViewMode.FirstPerson)
            return pivotLocal;

        Vector3 thirdPersonPivotLocal = pivotLocal + new Vector3(0f, thirdPersonHeightOffset, 0f);
        Quaternion pitchRotation = Quaternion.Euler(xRotation, 0f, 0f);
        Vector3 desiredLocalOffset = pitchRotation * new Vector3(thirdPersonShoulderOffset, 0f, -Mathf.Max(0f, thirdPersonDistance));
        Vector3 desiredLocalPosition = thirdPersonPivotLocal + desiredLocalOffset;

        float castDistance = desiredLocalOffset.magnitude;
        if (castDistance <= 0.001f || thirdPersonCollisionRadius <= 0f)
            return desiredLocalPosition;

        Vector3 pivotWorld = transform.TransformPoint(thirdPersonPivotLocal);
        Vector3 desiredWorld = transform.TransformPoint(desiredLocalPosition);
        Vector3 direction = (desiredWorld - pivotWorld).normalized;
        int hitCount = Physics.SphereCastNonAlloc(
            pivotWorld,
            thirdPersonCollisionRadius,
            direction,
            thirdPersonHitBuffer,
            castDistance,
            thirdPersonCollisionMask,
            QueryTriggerInteraction.Ignore);

        float nearestHitDistance = castDistance;
        bool hasBlockingHit = false;

        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = thirdPersonHitBuffer[i];
            if (hit.collider == null)
                continue;

            Transform hitTransform = hit.collider.transform;
            if (hitTransform == transform || hitTransform.IsChildOf(transform))
                continue;

            if (hit.distance < nearestHitDistance)
            {
                nearestHitDistance = hit.distance;
                hasBlockingHit = true;
            }
        }

        if (!hasBlockingHit)
            return desiredLocalPosition;

        float safeDistance = Mathf.Max(0f, nearestHitDistance - thirdPersonCollisionRadius);
        Vector3 correctedWorldPosition = pivotWorld + direction * safeDistance;
        return transform.InverseTransformPoint(correctedWorldPosition);
    }

    private void UpdateFirstPersonVisuals(bool forceRefresh)
    {
        bool shouldShowFirstPersonVisuals = currentViewMode == CameraViewMode.FirstPerson;
        if (!forceRefresh && firstPersonVisualsVisible == shouldShowFirstPersonVisuals)
            return;

        firstPersonVisualsVisible = shouldShowFirstPersonVisuals;

        if (hideCameraChildrenInThirdPerson && cachedCameraChildren != null)
        {
            for (int i = 0; i < cachedCameraChildren.Length; i++)
            {
                Transform child = cachedCameraChildren[i];
                if (child != null)
                    child.gameObject.SetActive(shouldShowFirstPersonVisuals);
            }
        }

        if (firstPersonOnlyObjects != null)
        {
            for (int i = 0; i < firstPersonOnlyObjects.Length; i++)
            {
                GameObject firstPersonOnlyObject = firstPersonOnlyObjects[i];
                if (firstPersonOnlyObject != null)
                    firstPersonOnlyObject.SetActive(shouldShowFirstPersonVisuals);
            }
        }
    }

    private void HandleCrouchInput()
    {
        if (!isFlying && Input.GetKeyDown(KeyCode.LeftControl))
        {
            ToggleCrouch();
        }
    }

    private void ToggleCrouch()
    {
        if (isCrouching)
        {
            if (!CanStandUp())
            {
                return;
            }
            isCrouching = false;
            targetHeight = normalHeight;
            cameraTargetY = 2;
            characterController.stepOffset = originalStepOffset;
        }
        else
        {
            isCrouching = true;
            targetHeight = crouchHeight;
            cameraTargetY = crouchHeight / 2f;
            characterController.stepOffset = 0f;
            sprintToggled = false;
        }
    }


    // private bool CanStandUp()
    // {
    //     float radius = characterController.radius * 0.9f;
    //     Vector3 bottom = transform.position + characterController.center - Vector3.up * (characterController.height / 2f - 0.01f);
    //     Vector3 top = transform.position + characterController.center + Vector3.up * (targetHeight / 2f) + Vector3.up * 0.01f;

    //     return !Physics.CheckCapsule(bottom, transform.position + Vector3.up * (normalHeight - 0.01f), radius, ~0, QueryTriggerInteraction.Ignore);
    // }


private bool CanStandUp()
{
    float radius = characterController.radius * 0.95f;

    Vector3 center = transform.position + characterController.center;

    Vector3 bottom = center - Vector3.up * (characterController.height / 2f - 0.05f);
    Vector3 top = center + Vector3.up * (normalHeight / 2f - 0.05f);

    int mask = ~playerMeshLayer; // 👈 ignora o player

    return !Physics.CheckCapsule(bottom, top, radius, mask, QueryTriggerInteraction.Ignore);
}


    private void HandleFlightToggle()
    {
        if (!CanUseCreativeFlight()) return;

        if (Input.GetKeyDown(KeyCode.Space))
        {
            if (Time.time - lastSpaceTapTime <= doubleTapTime)
            {
                flightToggled = !flightToggled;
                if (flightToggled)
                {
                    isFlying = true;
                    velocity.y = 0f;
                    characterController.stepOffset = 0f;

                    bool holdSprint = Input.GetKey(KeyCode.LeftShift);
                    bool forwardPressed = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow);
                    bool sprintActive = holdSprint || sprintToggled;
                    if (sprintActive && forwardPressed)
                    {
                        currentFlyHorizontal = transform.forward * (flySpeed * flySprintMultiplier * 0.8f);
                    }
                }
                else
                {
                    StopFlying();
                }
            }
            lastSpaceTapTime = Time.time;
        }
    }

    private bool CanUseCreativeFlight()
    {
        return canFly &&
               CraftingSystem.Instance != null &&
               CraftingSystem.Instance.CreativeModeEnabled;
    }

    private void EnforceCreativeFlightPermission()
    {
        if (CanUseCreativeFlight())
            return;

        lastSpaceTapTime = -1f;
        flightToggled = false;

        if (isFlying)
            StopFlying(resetVerticalVelocity: true);
    }

    private void StopFlying(bool resetVerticalVelocity = false)
    {
        isFlying = false;
        flightToggled = false;
        currentFlyHorizontal = Vector3.zero;
        flySmoothVelocity = Vector3.zero;
        characterController.stepOffset = originalStepOffset;

        if (resetVerticalVelocity)
            velocity.y = 0f;
    }

    private void HandleMovement()
    {
        bool isGrounded = characterController.isGrounded;

        bool holdSprint = Input.GetKey(KeyCode.LeftShift);
        bool forwardPressed = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow);

        if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow))
        {
            if (Time.time - lastForwardTapTime <= doubleTapTime)
            {
                if (CanSprint())
                    sprintToggled = true;
            }
            lastForwardTapTime = Time.time;
        }

        if (!forwardPressed || Input.GetAxis("Vertical") <= 0f)
            sprintToggled = false;

        bool sprintActive = (holdSprint || sprintToggled) && CanSprint();

        float currentSpeed;
        if (isCrouching) currentSpeed = crouchSpeed;
        else if (isFlying) currentSpeed = flySpeed;
        else if (sprintActive) currentSpeed = sprintSpeed;
        else currentSpeed = walkSpeed;

        Vector3 inputDir = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));

        if (isFlying)
        {
            Vector3 camForward = cameraTransform != null ? cameraTransform.forward : transform.forward;
            Vector3 camRight = cameraTransform != null ? cameraTransform.right : transform.right;

            Vector3 rawFly = camRight * inputDir.x + camForward * inputDir.z;

            Vector3 targetHorizontal;
            float targetHorizontalSpeed = flySpeed * (sprintActive ? flySprintMultiplier : 1f);

            if (rawFly.sqrMagnitude > 0.001f)
            {
                targetHorizontal = rawFly.normalized * targetHorizontalSpeed * rawFly.magnitude;
            }
            else
            {
                targetHorizontal = Vector3.zero;
            }

            currentFlyHorizontal = Vector3.SmoothDamp(currentFlyHorizontal, targetHorizontal, ref flySmoothVelocity, 1f / Mathf.Max(0.0001f, flyAcceleration));

            float v = 0f;
            if (Input.GetKey(KeyCode.Space)) v += 1f;
            if (Input.GetKey(KeyCode.LeftControl)) v -= 1f;

            velocity.y = v * targetHorizontalSpeed;

            Vector3 finalMove = new Vector3(currentFlyHorizontal.x, velocity.y, currentFlyHorizontal.z);

            characterController.Move(finalMove * Time.deltaTime);
            return;
        }

        Vector3 move = transform.right * inputDir.x + transform.forward * inputDir.z;
        Vector3 horizontalMove = move.normalized * move.magnitude * currentSpeed;

        if (isCrouching && horizontalMove.sqrMagnitude > 0.001f)
        {
            if (!HasGroundAhead(horizontalMove * Time.deltaTime))
            {
                horizontalMove = Vector3.zero;
            }
        }

        if (sprintActive && !isFlying && inputDir.z > 0.1f)
        {
            DrainSprintStamina();
            if (!CanSprint())
            {
                sprintToggled = false;
                currentSpeed = walkSpeed;
                horizontalMove = move.normalized * move.magnitude * currentSpeed;
            }
        }

        if (isGrounded && velocity.y < 0f)
        {
            velocity.y = -0.1f;
        }

        if (isGrounded && Input.GetButtonDown("Jump") && !isCrouching && CanJump())
        {
            UseJumpStamina();

            velocity.y = jumpForce;

            if (sprintActive)
            {
                horizontalMove += transform.forward * sprintJumpBoost;
            }
        }

        velocity.y -= gravity * Time.deltaTime;

        Vector3 finalMoveGround = new Vector3(horizontalMove.x, velocity.y, horizontalMove.z);

        characterController.Move(finalMoveGround * Time.deltaTime);
    }

    private bool HasGroundAhead(Vector3 deltaMove)
    {
        Vector3 futurePos = transform.position + new Vector3(deltaMove.x, 0f, deltaMove.z);

        Vector3 origin = futurePos + Vector3.up * 0.1f;
        float maxDown = 1.5f + 0.01f;
        int hits = Physics.RaycastNonAlloc(
            origin,
            Vector3.down,
            groundAheadRaycastBuffer,
            maxDown,
            ~0,
            QueryTriggerInteraction.Ignore);

        return hits > 0;
    }

    private void SmoothCrouchTransition()
    {
        float currentHeight = characterController.height;
        float newHeight = Mathf.Lerp(currentHeight, targetHeight, Time.deltaTime * crouchTransitionSpeed);

        float heightDiff = newHeight - currentHeight;
        characterController.height = newHeight;
        characterController.center = new Vector3(characterController.center.x, newHeight / 2f, characterController.center.z);

        if (cameraTransform != null)
        {
            currentCameraPivotY = Mathf.Lerp(currentCameraPivotY, cameraTargetY, Time.deltaTime * crouchTransitionSpeed);
            UpdateCameraTransform();
        }
    }

    private void HandleStaminaRecovery()
    {
        if (!EnsurePlayerStatus())
            return;

        if (spentStaminaThisFrame)
            return;

        bool holdSprint = Input.GetKey(KeyCode.LeftShift);
        bool forwardPressed = Input.GetAxis("Vertical") > 0.1f;
        bool sprinting = (holdSprint || sprintToggled) && forwardPressed && !isFlying;

        if (!sprinting)
        {
            playerStatus.RecoverStamina(staminaRecoveryRate * Time.deltaTime);
        }
    }

    private void DrainSprintStamina()
    {
        if (!EnsurePlayerStatus())
            return;

        playerStatus.UseStamina(staminaDrainSprint * Time.deltaTime);
        spentStaminaThisFrame = true;
    }

    private void UseJumpStamina()
    {
        if (!EnsurePlayerStatus())
            return;

        playerStatus.UseStamina(staminaDrainJump);
        spentStaminaThisFrame = true;
    }

    private bool CanSprint()
    {
        if (!EnsurePlayerStatus())
            return true;

        return playerStatus.GetStamina() > minStaminaToSprint;
    }

    private bool CanJump()
    {
        if (!EnsurePlayerStatus())
            return true;

        return playerStatus.GetStamina() >= staminaDrainJump;
    }

    private bool EnsurePlayerStatus()
    {
        if (playerStatus != null)
            return true;

        ResolvePlayerStatus(logWarning: false);
        return playerStatus != null;
    }

    private void ResolvePlayerStatus(bool logWarning)
    {
        if (playerStatus != null)
            return;

        playerStatus = GetComponent<PlayerStatus>();
        if (playerStatus != null)
            return;

        playerStatus = GetComponentInParent<PlayerStatus>();
        if (playerStatus != null)
            return;

        playerStatus = GetComponentInChildren<PlayerStatus>();
        if (playerStatus != null)
            return;

        playerStatus = FindAnyObjectByType<PlayerStatus>();
        if (playerStatus != null)
            return;

        if (logWarning && !loggedMissingPlayerStatus)
        {
            loggedMissingPlayerStatus = true;
            Debug.LogWarning("FPSController nao encontrou PlayerStatus; stamina de corrida e pulo nao sera alterada.", this);
        }
    }

    public bool IsCrouching() => isCrouching;
    public bool IsSprinting() => sprintToggled || Input.GetKey(KeyCode.LeftShift);
    public bool IsFlying() => isFlying;
    public bool IsThirdPerson() => currentViewMode == CameraViewMode.ThirdPerson;
    public Transform CameraTransform => cameraTransform;

    public void SetWorldLoadingState(bool isLoading)
    {
        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        worldLoadingLocked = isLoading;
        velocity = Vector3.zero;
        currentFlyHorizontal = Vector3.zero;
        flySmoothVelocity = Vector3.zero;
        sprintToggled = false;
        flightToggled = false;
        isFlying = false;

        if (isLoading && characterController != null && characterController.enabled)
            characterController.Move(Vector3.zero);
    }

    public void TeleportTo(Vector3 worldPosition)
    {
        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        velocity = Vector3.zero;
        currentFlyHorizontal = Vector3.zero;
        flySmoothVelocity = Vector3.zero;

        if (characterController != null && characterController.enabled)
        {
            characterController.enabled = false;
            transform.position = worldPosition;
            characterController.enabled = true;
            return;
        }

        transform.position = worldPosition;
    }

    private void UpdateCameraCulling()
    {
        if (cameraTransform == null) return;

        Camera cam = cameraTransform.GetComponent<Camera>();
        if (cam == null) return;

        if (currentViewMode == CameraViewMode.FirstPerson)
        {
            cam.cullingMask &= ~playerMeshLayer;
        }
        else
        {
            cam.cullingMask |= playerMeshLayer;
        }
    }
}
