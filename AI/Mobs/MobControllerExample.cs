using UnityEngine;

public sealed class MobControllerExample : MonoBehaviour
{
    [SerializeField] private MobController mob;
    [SerializeField] private MobConfig config;
    [SerializeField] private Transform owner;
    [SerializeField] private Transform forcedTarget;

    private void Awake()
    {
        if (mob == null)
            mob = GetComponent<MobController>();

        if (mob == null)
            return;

        if (config != null)
            mob.SetConfig(config, restartState: false);

        if (owner != null)
            mob.SetOwner(owner);
    }

    [ContextMenu("Force Chase Target")]
    private void ForceChaseTarget()
    {
        if (mob == null || forcedTarget == null)
            return;

        mob.SetTarget(forcedTarget);
        mob.EnterChaseState();
    }

    [ContextMenu("Return To Default State")]
    private void ReturnToDefaultState()
    {
        if (mob != null)
            mob.EnterDefaultState();
    }
}
