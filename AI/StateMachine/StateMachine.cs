using System;

public sealed class StateMachine
{
    public IState CurrentState { get; private set; }
    public IState PreviousState { get; private set; }
    public string CurrentStateName => CurrentState != null ? CurrentState.Name : string.Empty;

    public event Action<IState, IState> StateChanged;

    public void ChangeState(IState nextState)
    {
        if (nextState == null || ReferenceEquals(CurrentState, nextState))
            return;

        IState previous = CurrentState;
        previous?.Exit();

        PreviousState = previous;
        CurrentState = nextState;
        CurrentState.Enter();

        StateChanged?.Invoke(previous, CurrentState);
    }

    public void Update()
    {
        CurrentState?.Update();
    }

    public bool IsInState<T>() where T : class, IState
    {
        return CurrentState is T;
    }
}
