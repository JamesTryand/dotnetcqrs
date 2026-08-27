using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace OrderFulfillment;

/// <summary>The task decider: create a task, then complete it.</summary>
public static class Tasks
{
    public const string Aggregate = "task";

    public sealed record State(bool Exists, bool Completed);

    public static Decider<State> Decider() => new()
    {
        InitialState = () => new State(false, false),
        Decide = (state, cmd) => cmd.Name switch
        {
            "CreateTask" when state.Exists => throw new InvalidOperationException("task already exists"),
            "CreateTask" => [new NewEvent("TaskCreated", cmd.Payload)],
            "CompleteTask" when !state.Exists => throw new InvalidOperationException("task does not exist"),
            "CompleteTask" when state.Completed => throw new InvalidOperationException("task already completed"),
            "CompleteTask" => [new NewEvent("TaskCompleted", "{}")],
            _ => throw new InvalidOperationException($"unknown command: {cmd.Name}"),
        },
        Evolve = (state, ev) => ev.Type switch
        {
            "TaskCreated" => state with { Exists = true },
            "TaskCompleted" => state with { Completed = true },
            _ => state,
        },
    };
}
