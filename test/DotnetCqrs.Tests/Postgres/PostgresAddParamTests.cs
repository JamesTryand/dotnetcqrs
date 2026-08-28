using DotnetCqrs.Postgres;
using DotnetCqrs.ReadModels;
using Xunit;

namespace DotnetCqrs.Tests.Postgres;

/// <summary>
/// Guards the trap flagged for Milestone 7: <c>DbCommandExtensions.AddParam(name, null)</c>
/// maps a CLR <c>null</c> to an untyped <see cref="System.DBNull"/>. SQLite accepts that
/// (see <see cref="DbCommandExtensionsTests"/>); Npgsql historically rejected an untyped
/// null parameter with "cannot determine parameter type". <c>ProjectionGenerator</c>
/// emits exactly this path (an absent JSON field → <c>JsonElement.GetString()</c> → null),
/// so a generated projection writing a nullable column against Postgres depends on it
/// working. If this test regresses, fix <c>AddParam</c> in <c>DotnetCqrs.Abstractions</c>
/// (the provider-neutral seam) rather than every projection.
/// </summary>
[Collection("postgres")]
public class PostgresAddParamTests(PostgresFixture fx)
{
    [SkippableFact]
    public async Task AddParam_with_a_CLR_null_writes_SQL_NULL_on_Postgres()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var store = await PostgresReadModelStore.OpenAsync(await fx.NewSchemaAsync());

        await using (var ddl = store.Connection.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE notes (id text PRIMARY KEY, body text NULL)";
            await ddl.ExecuteNonQueryAsync();
        }

        await using (var insert = store.Connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO notes (id, body) VALUES (@id, @body)";
            insert.AddParam("@id", "n1");
            insert.AddParam("@body", null); // the path under test
            await insert.ExecuteNonQueryAsync();
        }

        await using var read = store.Connection.CreateCommand();
        read.CommandText = "SELECT body IS NULL FROM notes WHERE id = @id";
        read.AddParam("@id", "n1");
        Assert.True((bool)(await read.ExecuteScalarAsync())!);
    }
}
