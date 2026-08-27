using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetCqrs.Codegen.Model;

namespace DotnetCqrs.Codegen;

/// <summary>Thrown when a document fails schema validation.</summary>
public sealed class DocumentValidationException(IReadOnlyList<ValidationError> errors)
    : Exception($"document failed schema validation ({errors.Count} error(s)): {string.Join("; ", errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}")
{
    public IReadOnlyList<ValidationError> Errors { get; } = errors;
}

/// <summary>
/// Loads an EventModeling document: validates it against <see cref="DocumentSchema"/>
/// first, then deserializes into the typed <see cref="Model"/> — a caller only ever
/// holds a <see cref="Document"/> that's already known structurally valid.
///
/// Single-file JSON documents only. The schema project also supports a split
/// multi-file <c>manifest.json</c> form (<c>manifest.schema.json</c>,
/// <c>scripts/join.js</c>) for authoring convenience on large documents — deliberately
/// not ported here yet: it's an authoring-time optimization, not something the
/// generation pipeline (parse → map → generate → verify) needs to function. A single
/// joined document is always a valid input regardless of how it was authored.
/// </summary>
public static class DocumentLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>Reads, validates, and parses the document at <paramref name="path"/>.
    /// Throws <see cref="DocumentValidationException"/> if it fails schema
    /// validation.</summary>
    public static Document LoadFromFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>Validates and parses <paramref name="json"/>. Throws
    /// <see cref="DocumentValidationException"/> if it fails schema validation.</summary>
    public static Document Parse(string json)
    {
        var errors = DocumentSchema.Validate(json);
        if (errors.Count > 0)
            throw new DocumentValidationException(errors);

        return JsonSerializer.Deserialize<Document>(json, SerializerOptions)
            ?? throw new InvalidOperationException("document parsed as JSON null");
    }
}
