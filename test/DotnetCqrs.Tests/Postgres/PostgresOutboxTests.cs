using DotnetCqrs.Outbox;
using DotnetCqrs.Postgres;
using Xunit;

namespace DotnetCqrs.Tests.Postgres;

/// <summary>Postgres parity for <see cref="OutboxTests"/>: the same storage rules for the outbox
/// <see cref="PostgresEventStore"/> keeps.</summary>
[Collection("postgres")]
public class PostgresOutboxTests(PostgresFixture fx)
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [SkippableFact]
    public async Task Only_a_consumers_entries_that_are_due_come_back_soonest_first_and_a_key_is_recorded_once()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await using var store = await PostgresEventStore.OpenAsync(await fx.NewSchemaAsync());
        await ((IOutbox)store).EnqueueAsync("review", "b", "{\"n\":2}", T0.AddDays(2));
        await ((IOutbox)store).EnqueueAsync("review", "a", "{\"n\":1}", T0.AddDays(1));
        await ((IOutbox)store).EnqueueAsync("review", "a", "{\"n\":9}", T0);
        await ((IOutbox)store).EnqueueAsync("review", "later", "{}", T0.AddDays(30));
        await ((IOutbox)store).EnqueueAsync("other", "x", "{}", T0);

        var due = await ((IOutbox)store).DueAsync("review", T0.AddDays(2), 10);

        Assert.Equal(["a", "b"], due.Select(e => e.Key));
        Assert.Equal("{\"n\":1}", due[0].Payload);
        Assert.Equal(T0.AddDays(1), due[0].Due);
        Assert.Equal(T0.AddDays(1), await ((IOutbox)store).NextDueAsync("review"));
        Assert.Null(await ((IOutbox)store).NextDueAsync("nobody"));
    }

    [SkippableFact]
    public async Task An_entry_is_retried_later_or_removed_and_survives_reopening_the_store()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var connection = await fx.NewSchemaAsync();
        await using (var store = await PostgresEventStore.OpenAsync(connection))
        {
            await ((IOutbox)store).EnqueueAsync("review", "s1", "{}", T0);
            await ((IOutbox)store).EnqueueAsync("review", "s2", "{}", T0);
            await ((IOutbox)store).RetryAsync("review", "s1", 3, T0.AddHours(1));
            await ((IOutbox)store).RemoveAsync("review", "s2");
        }
        await using var reopened = await PostgresEventStore.OpenAsync(connection);
        Assert.Empty(await ((IOutbox)reopened).DueAsync("review", T0, 10));
        var later = Assert.Single(await ((IOutbox)reopened).DueAsync("review", T0.AddHours(1), 10));
        Assert.Equal(("s1", 3), (later.Key, later.Attempts));
    }
}
