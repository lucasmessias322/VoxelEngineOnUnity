using UnityEngine;

public sealed class IdleState : MobState
{
    private float idleUntilTime;

    public override string Name => "Idle";

    public IdleState(MobController controller) : base(controller)
    {
    }

    public override void Enter()
    {
        Controller.SetLocomotionMode(MobLocomotionMode.Idle);
        Controller.StopMoving();
        idleUntilTime = Time.time + Controller.GetRandomIdleDuration();
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

        if (Time.time < idleUntilTime)
            return;

        if (Config != null && Config.CanWander)
        {
            Controller.EnterWanderState();
            return;
        }

        idleUntilTime = Time.time + Controller.GetRandomIdleDuration();
    }
}
