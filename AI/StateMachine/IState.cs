public interface IState
{
    string Name { get; }

    void Enter();
    void Update();
    void Exit();
}
