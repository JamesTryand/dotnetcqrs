using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace DotnetCqrs.Codegen;

/// <summary>One structural validation failure — a JSON Pointer into the instance plus
/// a human-readable message.</summary>
public sealed record ValidationError(string InstanceLocation, string Message);

/// <summary>
/// Validates raw JSON against the vendored EventModeling JSON Schema
/// (<c>Schema/eventmodeling.schema.json</c>, draft 2020-12) — structural
/// well-formedness only, exactly what the schema itself claims to enforce (its own
/// description: "methodology rules ... are deliberately NOT enforced here"). Real JSON
/// Schema validation (via JsonSchema.Net) rather than hand-rolled checks: the schema
/// uses conditional <c>if</c>/<c>then</c> requiredness keyed on <c>pattern</c>/<c>kind</c>,
/// which would be significant, error-prone surface to reimplement by hand and would
/// drift from the real schema over time.
/// </summary>
public static class DocumentSchema
{
    private static readonly JsonSchema Schema = LoadEmbeddedSchema();

    private static JsonSchema LoadEmbeddedSchema()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "DotnetCqrs.Codegen.Schema.eventmodeling.schema.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"embedded resource '{resourceName}' not found");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    /// <summary>Validates <paramref name="json"/> against the schema, returning every
    /// violation found (empty if valid).</summary>
    public static IReadOnlyList<ValidationError> Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var results = Schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        // The schema's slice/scenario shapes use allOf + if/then keyed on
        // pattern/kind: for a valid "stateChange" slice, the "stateView"/"automation"
        // branches' own `if` checks correctly fail to match (pattern != "stateView"),
        // which is NORMAL JSON Schema composition, not a violation -- if() failing
        // just means "don't apply then()" for that branch. The tree still surfaces
        // those non-matches as their own EvaluationResults nodes, so walking every
        // node indiscriminately (as an earlier version of this method did) reports
        // phantom errors even for a genuinely valid document. IsValid is the
        // authoritative answer; only walk Details -- and only collect from nodes
        // that are themselves invalid -- when the top-level result says invalid.
        if (results.IsValid) return [];

        var errors = new List<ValidationError>();
        CollectErrors(results, errors);
        return errors;
    }

    private static void CollectErrors(EvaluationResults results, List<ValidationError> errors)
    {
        if (results.IsValid) return;
        if (results.Errors is { Count: > 0 })
        {
            var location = results.InstanceLocation.ToString();
            foreach (var (_, message) in results.Errors)
                errors.Add(new ValidationError(location, message));
        }
        if (results.Details is not null)
            foreach (var detail in results.Details)
                CollectErrors(detail, errors);
    }
}
