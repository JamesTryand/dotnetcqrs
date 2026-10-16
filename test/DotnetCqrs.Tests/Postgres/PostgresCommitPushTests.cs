using DotnetCqrs.Consumers;
using DotnetCqrs.EventStore;
using DotnetCqrs.Postgres;
using Npgsql;
using Xunit;

namespace DotnetCqrs.Tests.Postgres;

/// <summary>
/// Another process's commit reaches this process's engine at once. Two stores on one
/// log stand for two nodes: a writer, and a reader with its own engine. The reader's engine has a one-hour tick, so
/// the only way the writer's event can reach it within the test is the store's push (<c>LISTEN/NOTIFY</c>). Before,
/// <see cref="IPollSource.Subscribe"/> was in-process only, and another node saw a new event on its next tick.
/// </summary>
[Collection("postgres")]
public class PostgresCommitPushTests(PostgresFixture fx)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private sealed class Recorder : IConsumer
    {
        private readonly Dictionary<string, TaskCompletionSource> _seen = [];
        public string Name => "recorder";

        public Task Seen(string type)
        {
            lock (_seen)
                return (_seen.TryGetValue(type, out var tcs) ? tcs : _seen[type] = New()).Task;
        }

        public Task ApplyAsync(Event ev, CancellationToken ct)
        {
            lock (_seen)
                (_seen.TryGetValue(ev.Type, out var tcs) ? tcs : _seen[ev.Type] = New()).TrySetResult();
            return Task.CompletedTask;
        }

        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static Task AppendAsync(PostgresEventStore store, string type) =>
        store.AppendAsync("probe", Guid.NewGuid().ToString("N"), 0, [new NewEvent(type, "{}")]);

    [SkippableFact]
    public async Task Another_processs_commit_wakes_the_engine_at_once()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var log = await fx.NewSchemaAsync();
        await using var writer = await PostgresEventStore.OpenAsync(log);
        await using var reader = await PostgresEventStore.OpenAsync(log);
        var recorder = new Recorder();
        var engine = new ConsumerEngine(reader, reader, tick: TimeSpan.FromHours(1));
        engine.Register(recorder);
        await AppendAsync(writer, "Before");
        using var stop = new CancellationTokenSource();

        var running = engine.StartAsync(stop.Token);
        await recorder.Seen("Before").WaitAsync(Patience); // the first pass, at start
        await AppendAsync(writer, "Pushed");

        await recorder.Seen("Pushed").WaitAsync(Patience);
        stop.Cancel();
        await running;
    }

    [SkippableFact]
    public async Task A_lost_listener_is_listening_again_after_the_next_pass()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var log = await fx.NewSchemaAsync();
        await using var writer = await PostgresEventStore.OpenAsync(log);
        await using var reader = await PostgresEventStore.OpenAsync(log);
        var recorder = new Recorder();
        var engine = new ConsumerEngine(reader, reader, tick: TimeSpan.FromHours(1));
        engine.Register(recorder);
        // Every wake-up the reader's store gives: once listening, once on losing it, once listening again. The reader
        // appends nothing itself, so nothing else wakes it.
        var wakes = new SemaphoreSlim(0);
        reader.SubscribeToCommits(() => wakes.Release());
        Assert.True(await wakes.WaitAsync(Patience), "never listened");
        using var stop = new CancellationTokenSource();
        var running = engine.StartAsync(stop.Token);

        // The server ends the listening session (a restart, a failover, an idle-session timeout).
        await using (var admin = NpgsqlDataSource.Create(fx.BaseConnectionString))
        await using (var terminate = admin.CreateCommand(
            "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE pid <> pg_backend_pid() AND query = @listen"))
        {
            terminate.Parameters.AddWithValue("@listen", $"LISTEN \"{reader.CommitChannel}\"");
            Assert.Equal(1L, await terminate.ExecuteScalarAsync());
        }
        Assert.True(await wakes.WaitAsync(Patience), "the loss did not wake the engine");
        Assert.True(await wakes.WaitAsync(Patience), "not listening again after the pass");
        await AppendAsync(writer, "Pushed");

        await recorder.Seen("Pushed").WaitAsync(Patience);
        stop.Cancel();
        await running;
    }

    [SkippableFact]
    public async Task Each_log_has_its_own_channel()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var one = await PostgresEventStore.OpenAsync(await fx.NewSchemaAsync());
        await using var other = await PostgresEventStore.OpenAsync(await fx.NewSchemaAsync());

        Assert.NotEqual(one.CommitChannel, other.CommitChannel);
    }

    [SkippableFact]
    public async Task A_log_in_a_schema_with_a_long_name_still_appends_and_pushes()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        // 63 characters, the longest a schema name can be; "dotnetcqrs_events_" + it is too long for a channel.
        var schema = ("l_" + Guid.NewGuid().ToString("N") + "_a_schema_with_a_very_long_name_indeed").PadRight(63, 'x')[..63];
        await using var admin = NpgsqlDataSource.Create(fx.BaseConnectionString);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA \"{schema}\""))
            await create.ExecuteNonQueryAsync();
        try
        {
            var log = new NpgsqlConnectionStringBuilder(fx.BaseConnectionString) { SearchPath = schema }.ConnectionString;
            await using var writer = await PostgresEventStore.OpenAsync(log);
            await using var reader = await PostgresEventStore.OpenAsync(log);
            var woken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wakes = 0;
            reader.SubscribeToCommits(() =>
            {
                if (Interlocked.Increment(ref wakes) == 1) listening.TrySetResult();
                else woken.TrySetResult();
            });
            await listening.Task.WaitAsync(Patience);

            await AppendAsync(writer, "Pushed");

            await woken.Task.WaitAsync(Patience);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA \"{schema}\" CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
