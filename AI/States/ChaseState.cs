using UnityEngine;

public sealed class ChaseState : MobState
{
    public override string Name => "Chase";

    public ChaseState(MobController controller) : base(controller)
    {
    }

    public override void Enter()
    {
        Controller.SetLocomotionMode(MobLocomotionMode.Run);

        if (Controller.CurrentTarget == null && Controller.TryFindBestTarget(out Transform target))
            Controller.SetTarget(target);

        if (Controller.CurrentTarget == null)
            EnterDefaultState();
    }

    public override void Update()
    {
        if (Controller.TryEnterFleeState())
            return;

        Transform target = Controller.CurrentTarget;
        if (target == null)
        {
            Controller.ClearTarget();
            EnterDefaultState();
            return;
        }

        if (Controller.IsCurrentTargetTooFar())
        {
            Controller.ClearTarget();
            EnterDefaultState();
            return;
        }

        if (Config != null &&
            Config.SwitchTargetWhileChasing &&
            Controller.TryFindBestTarget(out Transform betterTarget) &&
            betterTarget != target)
        {
            target = betterTarget;
            Controller.SetTarget(target);
        }

        Controller.RememberTargetPosition(target.position);

        if (Config == null)
        {
            EnterDefaultState();
            return;
        }

        if (Config.CanAttack && Controller.IsCurrentTargetInAttackRange())
        {
            Controller.EnterAttackState();
            return;
        }

        Controller.MoveToTarget(
            target,
            Config.RunSpeed,
            Config.ChaseStopDistance,
            forceRepath: false);
    }
}
