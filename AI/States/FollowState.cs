using UnityEngine;

public sealed class FollowState : MobState
{
    public override string Name => "Follow";

    public FollowState(MobController controller) : base(controller)
    {
    }

    public override void Enter()
    {
        Controller.SetLocomotionMode(MobLocomotionMode.Idle);
    }

    public override void Update()
    {
        if (TryEnterPriorityState())
            return;

        if (Config == null || Controller.Owner == null)
        {
            EnterDefaultState();
            return;
        }

        float distanceSqr = Controller.HorizontalDistanceSqrTo(Controller.Owner.position);
        float followStartSqr = Config.FollowStartDistance * Config.FollowStartDistance;
        float followStopSqr = Config.FollowStopDistance * Config.FollowStopDistance;

        if (distanceSqr > followStartSqr)
        {
            Controller.SetLocomotionMode(Config.FollowUsesRunAnimation ? MobLocomotionMode.Run : MobLocomotionMode.Walk);
            Controller.MoveToTarget(
                Controller.Owner,
                Config.FollowSpeed,
                Config.FollowStopDistance,
                forceRepath: false);
            return;
        }

        if (distanceSqr <= followStopSqr || !Controller.AgentHasPath)
        {
            Controller.SetLocomotionMode(MobLocomotionMode.Idle);
            Controller.StopMoving();
        }
    }
}
