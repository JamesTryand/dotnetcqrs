using DotnetCqrs.ReadModels;

namespace DotnetCqrs.Tests;

/// <summary>
/// <see cref="DbCommandExtensions.AddParam"/> is the provider-neutral stand-in for
/// <c>AddWithValue</c> (which is a Microsoft.Data.Sqlite / Npgsql extension, absent
/// from the ADO.NET <c>DbCommand</c> surface generated projection code now depends on).
/// The one behaviour that isn't obvious — and that <c>ProjectionGenerator</c> relies on
/// via <c>JsonElement.GetString()</c> returning <c>null</c> for a JSON null — is that a
/// CLR <c>null</c> must land as SQL <c>NULL</c>, not as a parameter left unset (which
/// writes nothing).
/// </summary>
public class DbCommandExtensionsTests
{
    [Fact]
    public async Task AddParam_maps_a_clr_null_to_sql_NULL_not_to_an_unset_parameter()
    {
        await using var store = await SqliteReadModelStore.OpenAsync(":memory:");

        await using (var create = store.Connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t (id TEXT PRIMARY KEY, note TEXT)";
            await create.ExecuteNonQueryAsync();
        }

        await using (var insert = store.Connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO t (id, note) VALUES (@id, @note)";
            insert.AddParam("@id", "row1");
            insert.AddParam("@note", null); // the trap: must become SQL NULL
            await insert.ExecuteNonQueryAsync();
        }

        await using var read = store.Connection.CreateCommand();
        read.CommandText = "SELECT note, note IS NULL FROM t WHERE id = @id";
        read.AddParam("@id", "row1");
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(1L, reader.GetInt64(1)); // note IS NULL -> 1, proving the row was written
    }
}
