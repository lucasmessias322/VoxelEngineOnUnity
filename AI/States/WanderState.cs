using UnityEngine;

public sealed class WanderState : MobState
{
    private Vector3 destination;
    private float destinationExpireTime;
    private bool hasDestination;

    public override string Name => "Wander";

    public WanderState(MobController controller) : base(controller)
    {
    }

    public override void Enter()
    {
        Controller.SetLocomotionMode(MobLocomotionMode.Walk);
        hasDestination = false;

        if (!TryChooseDestination())
            Controller.EnterIdleState();
    }

    public override void Update()
    {
        if (TryEnterPriorityState())
            return;

        if (Controller.ShouldFollowOwner())
        {
            Controller.EnterFollowState();
            return;
        }

        if (!hasDestination)
        {
            Controller.EnterIdleState();
            return;
        }

        if (Controller.AgentReachedDestination)
        {
            Controller.EnterIdleState();
            return;
        }

        if (Time.time >= destinationExpireTime)
        {
            Controller.EnterIdleState();
            return;
        }

        if (!Controller.AgentHasPath && !Controller.AgentHasDestination)
        {
            Controller.EnterIdleState();
            return;
        }
    }

    public override void Exit()
    {
        hasDestination = false;
    }

    private bool TryChooseDestination()
    {
        if (Config == null || !Controller.TryChooseWanderDestination(out destination))
            return false;

        hasDestination = Controller.MoveTo(
            destination,
            Config.WalkSpeed,
            Config.WanderStopDistance,
            forceRepath: true);

        destinationExpireTime = Time.time + Config.DestinationTimeout;
        return hasDestination;
    }
}
