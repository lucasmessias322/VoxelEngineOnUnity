using UnityEngine;
using UnityEngine.UI;

public class PlayerStatus : MonoBehaviour
{
    public Slider HealthSlide;
    private float health;
    public float MaxHealth = 100f;

    public Slider StaminaSlide;
    private float stamina;
    public float MaxStamina = 100f;

    [Header("Stamina UI")]
    [SerializeField] private bool hideStaminaWhenIdle = true;
    [SerializeField] private float staminaHideDelay = 1f;

    private bool initialized;
    private float hideStaminaAtTime;

    void Awake()
    {
        health = MaxHealth;
        stamina = MaxStamina;
        ConfigureSliders();
        UpdateUI();
        SetStaminaVisible(!hideStaminaWhenIdle);
        initialized = true;
    }

    void Start()
    {
        if (!initialized)
        {
            health = MaxHealth;
            stamina = MaxStamina;
            ConfigureSliders();
            UpdateUI();
            SetStaminaVisible(!hideStaminaWhenIdle);
            initialized = true;
        }
    }

    void Update()
    {
        if (!hideStaminaWhenIdle || StaminaSlide == null)
            return;

        if (StaminaSlide.gameObject.activeSelf && Time.time >= hideStaminaAtTime)
        {
            SetStaminaVisible(false);
        }
    }

   

    public void TakeDamage(float amount)
    {
        health -= amount;
        health = Mathf.Clamp(health, 0, MaxHealth);
        UpdateUI();

        if (health <= 0)
        {
            Debug.Log("Player morreu");
        }
    }

    public void Heal(float amount)
    {
        health += amount;
        health = Mathf.Clamp(health, 0, MaxHealth);
        UpdateUI();
    }

    public void UseStamina(float amount)
    {
        if (amount <= 0f)
            return;

        float previousStamina = stamina;
        stamina -= amount;
        stamina = Mathf.Clamp(stamina, 0, MaxStamina);
        UpdateUI();

        if (!Mathf.Approximately(previousStamina, stamina))
            ShowStaminaForActivity();
    }

    public void RecoverStamina(float amount)
    {
        if (amount <= 0f)
            return;

        float previousStamina = stamina;
        stamina += amount;
        stamina = Mathf.Clamp(stamina, 0, MaxStamina);
        UpdateUI();

        if (!Mathf.Approximately(previousStamina, stamina))
            ShowStaminaForActivity();
    }

    public float GetStamina()
    {
        return stamina;
    }

    public float GetHealth()
    {
        return health;
    }

    private void ConfigureSliders()
    {
        ConfigureSlider(HealthSlide);
        ConfigureSlider(StaminaSlide);
    }

    private void ConfigureSlider(Slider slider)
    {
        if (slider == null)
            return;

        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.wholeNumbers = false;
    }

    private void UpdateUI()
    {
        if (HealthSlide != null)
            HealthSlide.SetValueWithoutNotify(Get01(health, MaxHealth));

        if (StaminaSlide != null)
            StaminaSlide.SetValueWithoutNotify(Get01(stamina, MaxStamina));
    }

    private float Get01(float value, float maxValue)
    {
        if (maxValue <= 0f)
            return 0f;

        return Mathf.Clamp01(value / maxValue);
    }

    private void ShowStaminaForActivity()
    {
        if (!hideStaminaWhenIdle || StaminaSlide == null)
            return;

        SetStaminaVisible(true);
        hideStaminaAtTime = Time.time + staminaHideDelay;
    }

    private void SetStaminaVisible(bool visible)
    {
        if (StaminaSlide == null)
            return;

        StaminaSlide.gameObject.SetActive(visible);
    }
}
