using DotnetCqrs.EventStore;
using DotnetCqrs.Postgres;
using Xunit;

namespace DotnetCqrs.Tests.Postgres;

/// <summary>Postgres parity for <see cref="DeadLetterTests"/>.</summary>
[Collection("postgres")]
public class PostgresDeadLetterTests(PostgresFixture fx)
{
    private async Task<PostgresEventStore> OpenAsync()
        => await PostgresEventStore.OpenAsync(await fx.NewSchemaAsync());

    [SkippableFact]
    public async Task AddDeadLetter_records_a_pending_entry_listed_by_default()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await OpenAsync();
        var appended = await store.AppendAsync("task", "t1", 0, [new NewEvent("TaskCreated", "{}")]);

        await store.AddDeadLetterAsync("extcall:notify", appended[0], "boom: connection refused");

        var pending = await store.ListDeadLettersAsync();
        var dl = Assert.Single(pending);
        Assert.Equal("extcall:notify", dl.Consumer);
        Assert.Equal(appended[0].Position, dl.EventPosition);
        Assert.Equal(appended[0].Id, dl.Event.Id);
        Assert.Equal("boom: connection refused", dl.Error);
        Assert.False(dl.Resolved);
    }

    [SkippableFact]
    public async Task ResolveDeadLetter_drops_it_from_the_default_pending_listing_but_not_the_full_one()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await OpenAsync();
        var appended = await store.AppendAsync("task", "t1", 0, [new NewEvent("TaskCreated", "{}")]);
        await store.AddDeadLetterAsync("extcall:notify", appended[0], "boom");
        var id = (await store.ListDeadLettersAsync()).Single().Id;

        await store.ResolveDeadLetterAsync(id);

        Assert.Empty(await store.ListDeadLettersAsync());
        var all = await store.ListDeadLettersAsync(includeResolved: true);
        Assert.True(Assert.Single(all).Resolved);
    }

    [SkippableFact]
    public async Task ResolveDeadLetter_throws_for_an_unknown_id()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await OpenAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ResolveDeadLetterAsync(999));
    }
}
