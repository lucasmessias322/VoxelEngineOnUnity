using UnityEngine;

public sealed class FleeState : MobState
{
    private float fleeUntilTime;
    private float nextDestinationTime;

    public override string Name => "Flee";

    public FleeState(MobController controller) : base(controller)
    {
    }

    public override void Enter()
    {
        Controller.SetLocomotionMode(MobLocomotionMode.Run);

        if (Config == null)
        {
            EnterDefaultState();
            return;
        }

        fleeUntilTime = Time.time + Config.FleeDuration;
        nextDestinationTime = 0f;
        TryRefreshDestination();
    }

    public override void Update()
    {
        if (Config == null)
        {
            EnterDefaultState();
            return;
        }

        if (Controller.ThreatTarget == null)
        {
            Controller.ClearThreat();
            EnterDefaultState();
            return;
        }

        if (Time.time >= fleeUntilTime || Controller.IsThreatAtSafeDistance())
        {
            Controller.ClearThreat();
            EnterDefaultState();
            return;
        }

        if (Time.time >= nextDestinationTime || Controller.AgentReachedDestination || !Controller.AgentHasPath)
            TryRefreshDestination();
    }

    private void TryRefreshDestination()
    {
        if (Config == null)
            return;

        if (Controller.TryChooseFleeDestination(out Vector3 destination))
        {
            Controller.MoveTo(
                destination,
                Config.FleeSpeed,
                Config.FleeStopDistance,
                forceRepath: true);
        }

        nextDestinationTime = Time.time + Config.FleeDestinationRefreshInterval;
    }
}
