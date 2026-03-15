using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class FPSController : MonoBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float walkSpeed = 5f;
    [SerializeField] private float sprintSpeed = 9f;
    [SerializeField] private float crouchSpeed = 2f;
    [SerializeField] private float jumpForce = 7.5f;
    [SerializeField] private float gravity = 20f;
    [Tooltip("Extra forward boost when doing a sprint-jump.")]
    [SerializeField] private float sprintJumpBoost = 2f;

    [Header("Camera Settings")]
    [SerializeField] private float mouseSensitivity = 100f;
    [SerializeField] private Transform cameraTransform;

    [Header("Crouch Settings")]
    [SerializeField] private float normalHeight = 2f;
    [SerializeField] private float crouchHeight = 1.2f;
    [Tooltip("Velocidade de transição entre alturas (segundos).")]
    [SerializeField] private float crouchTransitionSpeed = 8f;

    [Header("Flight Settings")]
    [SerializeField] private bool canFly = false; // Habilita voo (modo criativo)
    [SerializeField] private float flySpeed = 8f;
    [Tooltip("Multiplicador de velocidade de voo quando sprintando (LeftShift).")]
    [SerializeField] private float flySprintMultiplier = 2f;
    [Tooltip("Quão rápido a velocidade horizontal do voo alcança a velocidade alvo (em segundos inversos).")]
    [SerializeField] private float flyAcceleration = 8f;

    [Header("Minecraft-like Settings")]
    [Tooltip("Tempo máximo entre dois taps para reconhecer double-tap (sprint/voo).")]
    [SerializeField] private float doubleTapTime = 0.25f;
    [Tooltip("Número máximo de meshes aplicados por frame (não parte deste script, apenas referência).")]
    [SerializeField] private float stepOffset = 0.5f; // step offset para subir blocos

    // internal
    private CharacterController characterController;
    private Vector3 velocity = Vector3.zero; // inclui vertical
    private float xRotation = 0f;

    // sprint double-tap
    private float lastForwardTapTime = -1f;
    private bool sprintToggled = false;

    // flight double-tap
    private float lastSpaceTapTime = -1f;
    private bool flightToggled = false;
    private bool isFlying = false;

    // fly smoothing state (horizontal)
    private Vector3 currentFlyHorizontal = Vector3.zero;
    private Vector3 flySmoothVelocity = Vector3.zero;

    // crouch
    private bool isCrouching = false;
    private float targetHeight;
    public float cameraTargetY;
    private float originalStepOffset;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        characterController = GetComponent<CharacterController>();
        if (characterController == null) Debug.LogError("CharacterController component is missing!");

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        targetHeight = normalHeight;
        originalStepOffset = characterController.stepOffset;
        characterController.stepOffset = stepOffset;

        // inicial camera pos
        if (cameraTransform != null)
        {
            Vector3 local = cameraTransform.localPosition;
            local.y = cameraTargetY;
            cameraTransform.localPosition = local;
        }
    }

    void Update()
    {
        HandleRotation();
        // HandleCrouchInput(); // descoment se quiser ativar toggle crouch
        HandleFlightToggle();
        HandleMovement();
        SmoothCrouchTransition();
    }

    private void HandleRotation()
    {
        if (PlayerInventory.Instance != null && PlayerInventory.Instance.IsInventoryOpen)
            return;

        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity * Time.deltaTime;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity * Time.deltaTime;

        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f);

        if (cameraTransform != null)
            cameraTransform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);

        transform.Rotate(Vector3.up * mouseX);
    }

    private void HandleCrouchInput()
    {
        // Toggle crouch (usando LeftControl para manter compatibilidade com seu PlayerVoxelInteraction)
        if (!isFlying && Input.GetKeyDown(KeyCode.LeftControl))
        {
            ToggleCrouch();
        }
    }

    private void ToggleCrouch()
    {
        // se for levantar, checar se há espaço
        if (isCrouching)
        {
            if (!CanStandUp())
            {
                // não há espaço para levantar
                return;
            }
            isCrouching = false;
            targetHeight = normalHeight;
            cameraTargetY = 2;
            characterController.stepOffset = originalStepOffset; // restaura step
        }
        else
        {
            isCrouching = true;
            targetHeight = crouchHeight;
            cameraTargetY = crouchHeight / 2f;
            characterController.stepOffset = 0f; // evitar "pular" em agachado
            // ao agachar, também cancelamos sprint toggle
            sprintToggled = false;
        }
    }

    private bool CanStandUp()
    {
        // verifica se há espaço para ficar em pé (faz um CheckCapsule)
        float radius = characterController.radius * 0.9f;
        Vector3 bottom = transform.position + characterController.center - Vector3.up * (characterController.height / 2f - 0.01f);
        Vector3 top = transform.position + characterController.center + Vector3.up * (targetHeight / 2f) + Vector3.up * 0.01f;

        // fazemos um overlap check para saber se há algo ocupando a cabeça
        return !Physics.CheckCapsule(bottom, transform.position + Vector3.up * (normalHeight - 0.01f), radius, ~0, QueryTriggerInteraction.Ignore);
    }

    private void HandleFlightToggle()
    {
        // Double tap space para alternar voo (quando canFly ativado)
        if (!canFly) return;

        if (Input.GetKeyDown(KeyCode.Space))
        {
            if (Time.time - lastSpaceTapTime <= doubleTapTime)
            {
                // double-tap detectado -> alterna o modo de voo
                flightToggled = !flightToggled;
                // ao ativar/desativar voo, atualizamos isFlying
                if (flightToggled)
                {
                    isFlying = true;
                    velocity.y = 0f;
                    characterController.stepOffset = 0f;

                    // se ativou voo enquanto sprint ativo, dá um impulso inicial horizontal parecido com o sprint-boost
                    bool holdSprint = Input.GetKey(KeyCode.LeftShift);
                    bool forwardPressed = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow);
                    bool sprintActive = holdSprint || sprintToggled;
                    if (sprintActive && forwardPressed)
                    {
                        currentFlyHorizontal = transform.forward * (flySpeed * flySprintMultiplier * 0.8f); // impulso inicial
                    }
                }
                else
                {
                    isFlying = false;
                    characterController.stepOffset = originalStepOffset;
                    // manter vertical velocity conforme estava (grávidade resolverá)
                }
            }
            lastSpaceTapTime = Time.time;
        }
    }

    private void HandleMovement()
    {
        bool isGrounded = characterController.isGrounded;

        // --- SPRINT (double-tap W OR LeftShift)
        bool holdSprint = Input.GetKey(KeyCode.LeftShift);
        bool forwardPressed = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow);

        // detect double-tap forward
        if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow))
        {
            if (Time.time - lastForwardTapTime <= doubleTapTime)
            {
                sprintToggled = true;
            }
            lastForwardTapTime = Time.time;
        }

        // desliga sprint toggle se o jogador parar de mover pra frente
        if (!forwardPressed || Input.GetAxis("Vertical") <= 0f)
            sprintToggled = false;

        bool sprintActive = holdSprint || sprintToggled;

        // --- velocidade base (para movimento horizontal no chão)
        float currentSpeed;
        if (isCrouching) currentSpeed = crouchSpeed;
        else if (isFlying) currentSpeed = flySpeed; // usado apenas como fallback; no bloco isFlying re-calculamos com multiplier
        else if (sprintActive) currentSpeed = sprintSpeed;
        else currentSpeed = walkSpeed;

        // entrada raw
        Vector3 inputDir = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));

        // --- VOO (modo criativo, comportamento estilo Minecraft)
        if (isFlying)
        {
            // movimento relativo à câmera (inclui componente Y do forward da câmera)
            Vector3 camForward = cameraTransform != null ? cameraTransform.forward : transform.forward;
            Vector3 camRight = cameraTransform != null ? cameraTransform.right : transform.right;

            // construir target horizontal (inclui forward + right)
            Vector3 rawFly = camRight * inputDir.x + camForward * inputDir.z;

            // se não houver input, rawFly será zero; mantemos inércia via SmoothDamp
            Vector3 targetHorizontal;
            float targetHorizontalSpeed = flySpeed * (sprintActive ? flySprintMultiplier : 1f);

            if (rawFly.sqrMagnitude > 0.001f)
            {
                // normalizamos para manter direcionalidade correta, mas preservamos magnitude do input
                targetHorizontal = rawFly.normalized * targetHorizontalSpeed * rawFly.magnitude;
            }
            else
            {
                targetHorizontal = Vector3.zero;
            }

            // suavizamos a transição horizontal (similar à aceleração do Minecraft)
            currentFlyHorizontal = Vector3.SmoothDamp(currentFlyHorizontal, targetHorizontal, ref flySmoothVelocity, 1f / Mathf.Max(0.0001f, flyAcceleration));

            // controle vertical independente via Space (subir) / LeftControl (descer)
            float v = 0f;
            if (Input.GetKey(KeyCode.Space)) v += 1f;
            if (Input.GetKey(KeyCode.LeftControl)) v -= 1f;

            // velocidade vertical no voo usa a mesma referência de velocidade alvo (poder ajustar se quiser)
            velocity.y = v * targetHorizontalSpeed;

            // montar movimento final (horizontal suave já está em world-space porque usamos camRight/camForward)
            Vector3 finalMove = new Vector3(currentFlyHorizontal.x, velocity.y, currentFlyHorizontal.z);

            // mover CharacterController
            characterController.Move(finalMove * Time.deltaTime);
            return;
        }

        // --- Movimento no chão (ou no ar com gravidade)
        Vector3 move = transform.right * inputDir.x + transform.forward * inputDir.z;
        Vector3 horizontalMove = move.normalized * move.magnitude * currentSpeed;

        // Se agachado (sneak), previne cair de beirada: checar se terreo abaixo da futura posição
        if (isCrouching && horizontalMove.sqrMagnitude > 0.001f)
        {
            if (!HasGroundAhead(horizontalMove * Time.deltaTime))
            {
                horizontalMove = Vector3.zero; // bloqueia movimento que cairia do penhasco
            }
        }

        // --- vertical e gravidade / pulo
        if (isGrounded && velocity.y < 0f)
        {
            velocity.y = -0.1f; // leve downward para manter contato com chão
        }

        if (isGrounded && Input.GetButtonDown("Jump") && !isCrouching)
        {
            // pulo simples
            velocity.y = jumpForce;

            // sprint-jump: adiciona impulso frontal quando pulando correndo
            if (sprintActive)
            {
                horizontalMove += transform.forward * sprintJumpBoost;
            }
        }

        // aplicar gravidade
        velocity.y -= gravity * Time.deltaTime;

        // construir movimento final
        Vector3 finalMoveGround = new Vector3(horizontalMove.x, velocity.y, horizontalMove.z);

        // mover CharacterController (Move aplica deltaTime dentro)
        characterController.Move(finalMoveGround * Time.deltaTime);
    }

    private bool HasGroundAhead(Vector3 deltaMove)
    {
        // projeto uma posição futura horizontal (mantendo y atual)
        Vector3 futurePos = transform.position + new Vector3(deltaMove.x, 0f, deltaMove.z);

        // ponto de checagem a partir do centro do jogador (um pouco acima do solo)
        Vector3 origin = futurePos + Vector3.up * 0.1f;
        float maxDown = 1.5f + 0.01f; // quão longe abaixo procuramos ground (suficiente para um bloco)
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, maxDown, ~0, QueryTriggerInteraction.Ignore))
        {
            // se o chão está dentro do alcance, podemos andar
            return true;
        }

        // sem chão detectado -> não permitir movimento (sneak behaviour)
        return false;
    }

    private void SmoothCrouchTransition()
    {
        // ajusta altura do CharacterController e posição da camera suavemente
        float currentHeight = characterController.height;
        float newHeight = Mathf.Lerp(currentHeight, targetHeight, Time.deltaTime * crouchTransitionSpeed);

        // ajustar center para manter chão
        float heightDiff = newHeight - currentHeight;
        characterController.height = newHeight;
        characterController.center = new Vector3(characterController.center.x, newHeight / 2f, characterController.center.z);

        // ajustar camera local position
        if (cameraTransform != null)
        {
            Vector3 cp = cameraTransform.localPosition;
            cp.y = Mathf.Lerp(cp.y, cameraTargetY, Time.deltaTime * crouchTransitionSpeed);
            cameraTransform.localPosition = cp;
        }
    }

    // Exposição de API útil
    public bool IsCrouching() => isCrouching;
    public bool IsSprinting() => sprintToggled || Input.GetKey(KeyCode.LeftShift);
    public bool IsFlying() => isFlying;
}
