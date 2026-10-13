using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;

namespace DotnetCqrs.Tests.Crypto;

/// <summary>
/// A decider is pure: time arrives on the command (the registry stamps it), never from the clock. The data-subject
/// decider fell back to <c>DateTimeOffset.UtcNow</c> when a command carried no time (timesheets' paradigm audit, R5), so
/// deciding the same command twice could give two different events. It now refuses a command with no time.
/// </summary>
public class DataSubjectTimeTests
{
    [Theory]
    [InlineData(DataSubject.RequestErasureCommand, "{}")]
    [InlineData(DataSubject.PlaceLegalHoldCommand, """{"reason":"litigation"}""")]
    public void A_command_with_no_time_is_refused_not_given_the_clocks(string command, string payload)
    {
        var decider = DataSubject.Create();

        var refusal = Assert.Throws<InvalidOperationException>(() =>
            decider.Decide(decider.InitialState(), new Command(command, payload) { Actor = "manager-1" }));

        Assert.Contains("time", refusal.Message);
    }

    [Fact]
    public void The_commands_time_is_the_events_time()
    {
        var decider = DataSubject.Create();

        var events = decider.Decide(decider.InitialState(),
            new Command(DataSubject.RequestErasureCommand, "{}") { Actor = "manager-1", Now = "2026-10-13T09:00:00.000Z" });

        Assert.Contains("\"requestedAt\":\"2026-10-13T09:00:00.000Z\"", Assert.Single(events).Data);
    }
}
