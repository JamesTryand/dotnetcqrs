using System.Data.Common;

namespace DotnetCqrs.ReadModels;

/// <summary>
/// Provider-neutral parameter helpers for projection code working against a
/// <see cref="DbCommand"/>. <c>AddWithValue</c> is a Microsoft.Data.Sqlite / Npgsql
/// extension, not part of the ADO.NET base surface, so it is unavailable once a
/// projection depends on <see cref="DbCommand"/> rather than a concrete command type.
/// </summary>
public static class DbCommandExtensions
{
    /// <summary>Adds a named parameter, mapping a CLR <c>null</c> to
    /// <see cref="DBNull.Value"/> — a raw <see cref="DbParameter"/> left with a
    /// <c>null</c> <see cref="DbParameter.Value"/> does not behave like
    /// <c>AddWithValue(name, null)</c> and would write nothing instead of SQL NULL.
    /// Returns the command for chaining.</summary>
    public static DbCommand AddParam(this DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return command;
    }
}
