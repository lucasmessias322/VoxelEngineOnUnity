using UnityEngine;

public sealed class AttackState : MobState
{
    private float nextAttackTime;
    private float damageTime;
    private bool attackWindingUp;

    public override string Name => "Attack";

    public AttackState(MobController controller) : base(controller)
    {
    }

    public override void Enter()
    {
        if (Config == null || !Config.CanAttack)
        {
            EnterDefaultState();
            return;
        }

        Controller.SetLocomotionMode(MobLocomotionMode.Idle);
        Controller.StopMoving();
        nextAttackTime = 0f;
        damageTime = 0f;
        attackWindingUp = false;
    }

    public override void Update()
    {
        if (Controller.TryEnterFleeState())
            return;

        if (Config == null || !Config.CanAttack || !Controller.IsCurrentTargetValid())
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

        if (!Controller.IsCurrentTargetInAttackRange())
        {
            attackWindingUp = false;
            Controller.EnterChaseState();
            return;
        }

        Controller.StopMoving();
        Controller.FaceCurrentTarget();

        if (attackWindingUp)
        {
            if (Time.time < damageTime)
                return;

            attackWindingUp = false;

            if (Controller.IsCurrentTargetInAttackRange())
                Controller.DealDamageToCurrentTarget();

            nextAttackTime = Time.time + Config.AttackCooldown;
            return;
        }

        if (Time.time < nextAttackTime)
            return;

        float windup = Config.AttackWindupTime;
        if (windup <= 0f)
        {
            Controller.TriggerAttackAnimation();
            Controller.DealDamageToCurrentTarget();
            nextAttackTime = Time.time + Config.AttackCooldown;
            return;
        }

        Controller.TriggerAttackAnimation();
        attackWindingUp = true;
        damageTime = Time.time + windup;
    }

    public override void Exit()
    {
        attackWindingUp = false;
    }
}
