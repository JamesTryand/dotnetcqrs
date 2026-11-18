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
        var results = Schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });

        // The schema's slice/scenario shapes use allOf + if/then keyed on
        // pattern/kind: for a valid "stateChange" slice, the "stateView"/"automation"
        // branches' own `if` checks correctly fail to match (pattern != "stateView"),
        // which is NORMAL JSON Schema composition, not a violation -- if() failing
        // just means "don't apply then()" for that branch. So the tree is walked
        // from the root, descending only into invalid nodes under invalid nodes:
        // a failed `if` sits under a valid parent and is never reached.
        if (results.IsValid) return [];

        var errors = Collect(results);
        if (errors.Count == 0)
            errors = CollectEverything(results);
        errors = WithoutEchoes(errors);
        return errors.Distinct().OrderBy(e => e.InstanceLocation, StringComparer.Ordinal).ToList();
    }

    // Keywords whose own message only says that something beneath them failed
    // ("Some properties did not match", "Value does not match all of the
    // specified schemas"): reported only when nothing beneath them is.
    private static readonly HashSet<string> Aggregators =
    [
        "allOf", "anyOf", "oneOf", "not", "if", "then", "else", "$ref", "$dynamicRef",
        "properties", "patternProperties", "additionalProperties", "unevaluatedProperties",
        "items", "prefixItems", "unevaluatedItems", "contains", "dependentSchemas", "propertyNames",
    ];

    // The problems a person would fix. A oneOf or anyOf that nothing matched
    // reports every alternative's failures, nearly all from alternatives the
    // document never meant; only the alternative that got deepest into the
    // document is kept, the one it was evidently written as.
    private static List<ValidationError> Collect(EvaluationResults node)
    {
        var found = new List<ValidationError>();
        if (node.IsValid) return found;

        var invalid = (node.Details ?? []).Where(d => !d.IsValid).ToList();
        var alternatives = invalid.Where(IsAlternative).ToList();
        foreach (var child in invalid.Except(alternatives))
            found.AddRange(Collect(child));
        if (alternatives.Count > 0)
            // An alternative whose constant failed (a target's "kind": "slice" against the alternative for
            // "element") is one the document did not mean; of the rest, the deepest. If every constant failed,
            // the deepest of all.
            found.AddRange(alternatives
                .Select(a => (Errors: Collect(a), Meant: !ConstFailed(a)))
                .OrderByDescending(a => a.Meant).ThenByDescending(a => Depth(a.Errors))
                .First().Errors);

        if (node.Errors is { Count: > 0 })
        {
            var location = node.InstanceLocation.ToString();
            foreach (var (keyword, message) in node.Errors)
                if (!Aggregators.Contains(keyword) || found.Count == 0)
                    found.Add(new ValidationError(location, message));
        }
        return found;
    }

    private static bool IsAlternative(EvaluationResults node)
    {
        var path = node.EvaluationPath.ToString().Split('/');
        return path.Length >= 2 && int.TryParse(path[^1], out _) && path[^2] is "oneOf" or "anyOf";
    }

    private static bool ConstFailed(EvaluationResults node) =>
        !node.IsValid && ((node.Errors?.ContainsKey("const") ?? false) || (node.Details ?? []).Any(ConstFailed));

    private static int Depth(List<ValidationError> errors) =>
        errors.Count == 0 ? -1 : errors.Max(e => e.InstanceLocation.Count(c => c == '/'));

    // A refusal always gives a reason: if the reduction above left nothing,
    // every error in the tree is reported.
    private static List<ValidationError> CollectEverything(EvaluationResults node)
    {
        var found = new List<ValidationError>();
        if (node.IsValid) return found;
        if (node.Errors is { Count: > 0 })
            foreach (var (_, message) in node.Errors)
                found.Add(new ValidationError(node.InstanceLocation.ToString(), message));
        foreach (var child in node.Details ?? [])
            found.AddRange(CollectEverything(child));
        return found;
    }

    // A false schema (unevaluatedProperties: false) beside a real problem is
    // that problem's echo: an object whose subschema failed has none of its
    // properties counted as evaluated, so every one of them is reported too.
    // It is dropped when a real problem is on the same object or inside it.
    private static List<ValidationError> WithoutEchoes(List<ValidationError> errors)
    {
        static bool IsEcho(ValidationError e) => e.Message.Contains("false schema", StringComparison.Ordinal);
        var real = errors.Where(e => !IsEcho(e)).Select(e => e.InstanceLocation).ToList();
        return errors.Where(e =>
        {
            if (!IsEcho(e)) return true;
            var cut = e.InstanceLocation.LastIndexOf('/');
            if (cut < 0) return true;
            var parent = e.InstanceLocation[..cut];
            return !real.Any(r => r == parent || r.StartsWith(parent + "/", StringComparison.Ordinal));
        }).ToList();
    }
}
