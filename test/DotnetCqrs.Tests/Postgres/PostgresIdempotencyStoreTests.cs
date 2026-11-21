using DotnetCqrs.Idempotency;
using Xunit;

namespace DotnetCqrs.Tests.Postgres;

/// <summary>Postgres parity for <see cref="IdempotencyStoreTests"/>.</summary>
[Collection("postgres")]
public class PostgresIdempotencyStoreTests(PostgresFixture fx)
{
    [SkippableFact]
    public async Task The_postgres_store_keeps_the_first_write_for_its_retention()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var clock = new ManualTimeProvider();
        await using var store = await PostgresIdempotencyStore.OpenAsync(await fx.NewSchemaAsync(), TimeSpan.FromDays(10), clock);
        await IdempotencyStoreBehaviour.FirstWriteWinsAndIsKeptForItsRetentionAsync(store, clock, TimeSpan.FromDays(10));
    }
}
