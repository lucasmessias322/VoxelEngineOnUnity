using UnityEngine;

[CreateAssetMenu(fileName = "NearestVisibleTargetSelector", menuName = "Voxel Mobs/Target Selectors/Nearest Visible")]
public sealed class NearestVisibleTargetSelector : MobTargetSelector
{
    public override bool TrySelectTarget(MobController controller, out Transform target)
    {
        target = null;
        return controller != null && controller.TryFindNearestTarget(out target);
    }
}
