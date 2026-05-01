public abstract class MobState : IState
{
    protected readonly MobController Controller;

    protected MobConfig Config => Controller.Config;

    public abstract string Name { get; }

    protected MobState(MobController controller)
    {
        Controller = controller;
    }

    public virtual void Enter()
    {
    }

    public virtual void Update()
    {
    }

    public virtual void Exit()
    {
    }

    protected bool TryEnterPriorityState()
    {
        return Controller.TryEnterPriorityState();
    }

    protected void EnterDefaultState()
    {
        Controller.EnterDefaultState();
    }
}
