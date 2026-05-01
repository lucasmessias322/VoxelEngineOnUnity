using UnityEngine;

public abstract class MobTargetSelector : ScriptableObject
{
    public abstract bool TrySelectTarget(MobController controller, out Transform target);
}
