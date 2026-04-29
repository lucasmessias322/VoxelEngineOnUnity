using System;
using UnityEngine;
using UnityEngine.Events;

[Serializable]
public struct VoxelMobDamageInfo
{
    public float amount;
    public GameObject sourceObject;
    public GameObject sourceEntity;
    public Vector3 hitPoint;
    public Vector3 hitDirection;
    public string damageType;
    public float time;

    public bool HasSource => sourceObject != null || sourceEntity != null;
    public GameObject BestSource => sourceEntity != null ? sourceEntity : sourceObject;
}

[Serializable]
public sealed class VoxelMobDamagedEvent : UnityEvent<VoxelMobHealth, VoxelMobDamageInfo>
{
}

[Serializable]
public sealed class VoxelMobDiedEvent : UnityEvent<VoxelMobHealth, VoxelMobDamageInfo>
{
}

[Serializable]
public sealed class VoxelMobHealedEvent : UnityEvent<VoxelMobHealth, float>
{
}

[DisallowMultipleComponent]
public sealed class VoxelMobHealth : MonoBehaviour
{
    [Header("Health")]
    [SerializeField, Min(1f)] private float maxHealth = 20f;
    [SerializeField, Min(0f)] private float currentHealth = 20f;
    [SerializeField] private bool startFullHealth = true;
    [SerializeField] private bool invulnerable = false;
    [SerializeField, Min(0f)] private float damageCooldown = 0f;

    [Header("Source Resolve")]
    [SerializeField] private bool resolveSourceEntityFromParents = true;
    [SerializeField] private string playerTag = "Player";

    [Header("Death")]
    [SerializeField] private bool destroyOnDeath = false;
    [SerializeField, Min(0f)] private float destroyDelay = 0f;

    [Header("Events")]
    [SerializeField] private VoxelMobDamagedEvent onDamaged = new VoxelMobDamagedEvent();
    [SerializeField] private VoxelMobHealedEvent onHealed = new VoxelMobHealedEvent();
    [SerializeField] private VoxelMobDiedEvent onDied = new VoxelMobDiedEvent();

    private VoxelMobDamageInfo lastDamageInfo;
    private float nextDamageAllowedTime;
    private bool deathRegistered;

    public event Action<VoxelMobHealth, VoxelMobDamageInfo> Damaged;
    public event Action<VoxelMobHealth, float> Healed;
    public event Action<VoxelMobHealth, VoxelMobDamageInfo> Died;

    public float MaxHealth => maxHealth;
    public float CurrentHealth => currentHealth;
    public float HealthPercent => maxHealth <= 0f ? 0f : currentHealth / maxHealth;
    public bool IsDead => deathRegistered || currentHealth <= 0f;
    public bool IsInvulnerable
    {
        get => invulnerable;
        set => invulnerable = value;
    }

    public VoxelMobDamageInfo LastDamageInfo => lastDamageInfo;
    public GameObject LastDamageSource => lastDamageInfo.BestSource;
    public GameObject LastDamageSourceEntity => lastDamageInfo.sourceEntity;
    public GameObject LastDamageSourceObject => lastDamageInfo.sourceObject;

    private void Awake()
    {
        maxHealth = Mathf.Max(1f, maxHealth);
        currentHealth = startFullHealth ? maxHealth : Mathf.Clamp(currentHealth, 0f, maxHealth);
        deathRegistered = currentHealth <= 0f;
    }

    private void OnValidate()
    {
        maxHealth = Mathf.Max(1f, maxHealth);
        currentHealth = Mathf.Clamp(currentHealth, 0f, maxHealth);
        damageCooldown = Mathf.Max(0f, damageCooldown);
        destroyDelay = Mathf.Max(0f, destroyDelay);
    }

    public bool TakeDamage(float amount)
    {
        return TakeDamage(CreateDamageInfo(amount, null, null, default, default, string.Empty));
    }

    public bool TakeDamage(float amount, GameObject sourceObject)
    {
        return TakeDamage(CreateDamageInfo(amount, sourceObject, null, default, default, string.Empty));
    }

    public bool TakeDamage(float amount, Component sourceComponent)
    {
        return TakeDamage(amount, sourceComponent != null ? sourceComponent.gameObject : null);
    }

    public bool TakeDamageFromEntity(float amount, GameObject sourceEntity)
    {
        return TakeDamage(CreateDamageInfo(amount, sourceEntity, sourceEntity, default, default, string.Empty));
    }

    public bool TakeDamageFromEntity(float amount, Component sourceEntity)
    {
        return TakeDamageFromEntity(amount, sourceEntity != null ? sourceEntity.gameObject : null);
    }

    public bool TakeDamage(
        float amount,
        GameObject sourceObject,
        GameObject sourceEntity,
        Vector3 hitPoint,
        Vector3 hitDirection,
        string damageType = "")
    {
        return TakeDamage(CreateDamageInfo(amount, sourceObject, sourceEntity, hitPoint, hitDirection, damageType));
    }

    public bool TakeDamage(VoxelMobDamageInfo damageInfo)
    {
        if (damageInfo.amount <= 0f || invulnerable || IsDead)
            return false;

        if (damageCooldown > 0f && Time.time < nextDamageAllowedTime)
            return false;

        damageInfo.time = Time.time;
        damageInfo.sourceEntity = ResolveSourceEntity(damageInfo.sourceObject, damageInfo.sourceEntity);
        if (damageInfo.sourceObject == null)
            damageInfo.sourceObject = damageInfo.sourceEntity;

        float previousHealth = currentHealth;
        currentHealth = Mathf.Max(0f, currentHealth - damageInfo.amount);
        damageInfo.amount = previousHealth - currentHealth;

        lastDamageInfo = damageInfo;
        nextDamageAllowedTime = Time.time + damageCooldown;

        onDamaged.Invoke(this, damageInfo);
        Damaged?.Invoke(this, damageInfo);

        if (currentHealth <= 0f)
            Die(damageInfo);

        return true;
    }

    public bool Heal(float amount, bool revive = false)
    {
        if (amount <= 0f)
            return false;

        if (IsDead && !revive)
            return false;

        if (revive && IsDead)
            deathRegistered = false;

        float previousHealth = currentHealth;
        currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
        float healedAmount = currentHealth - previousHealth;

        if (healedAmount <= 0f)
            return false;

        onHealed.Invoke(this, healedAmount);
        Healed?.Invoke(this, healedAmount);
        return true;
    }

    public void SetHealth(float health)
    {
        currentHealth = Mathf.Clamp(health, 0f, maxHealth);
        deathRegistered = currentHealth <= 0f;
    }

    public void Revive(float health = -1f)
    {
        deathRegistered = false;
        currentHealth = health > 0f ? Mathf.Min(health, maxHealth) : maxHealth;
    }

    private void Die(VoxelMobDamageInfo damageInfo)
    {
        if (deathRegistered)
            return;

        deathRegistered = true;
        onDied.Invoke(this, damageInfo);
        Died?.Invoke(this, damageInfo);

        if (destroyOnDeath)
            Destroy(gameObject, destroyDelay);
    }

    private VoxelMobDamageInfo CreateDamageInfo(
        float amount,
        GameObject sourceObject,
        GameObject sourceEntity,
        Vector3 hitPoint,
        Vector3 hitDirection,
        string damageType)
    {
        return new VoxelMobDamageInfo
        {
            amount = amount,
            sourceObject = sourceObject,
            sourceEntity = sourceEntity,
            hitPoint = hitPoint,
            hitDirection = hitDirection,
            damageType = damageType,
            time = Time.time
        };
    }

    private GameObject ResolveSourceEntity(GameObject sourceObject, GameObject explicitSourceEntity)
    {
        if (explicitSourceEntity != null)
            return explicitSourceEntity;

        if (sourceObject == null || !resolveSourceEntityFromParents)
            return sourceObject;

        Transform current = sourceObject.transform;
        while (current != null)
        {
            if (!string.IsNullOrEmpty(playerTag) && current.gameObject.tag == playerTag)
                return current.gameObject;

            VoxelMobHealth mobHealth = current.GetComponent<VoxelMobHealth>();
            if (mobHealth != null && mobHealth != this)
                return current.gameObject;

            VoxelMobBehaviorController behavior = current.GetComponent<VoxelMobBehaviorController>();
            if (behavior != null && current.gameObject != gameObject)
                return current.gameObject;

            VoxelMobPathAgent pathAgent = current.GetComponent<VoxelMobPathAgent>();
            if (pathAgent != null && current.gameObject != gameObject)
                return current.gameObject;

            current = current.parent;
        }

        return sourceObject.transform.root != null ? sourceObject.transform.root.gameObject : sourceObject;
    }
}
