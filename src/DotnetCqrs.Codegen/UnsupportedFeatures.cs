using System.Text.Json;

namespace DotnetCqrs.Codegen;

/// <summary>
/// Schema features this generator accepts in a valid document but does not implement. Since the
/// pinned schema moved from 3.1.1 to 3.8.0, a document can declare things added in 3.2.0–3.7.0
/// that no generated code here would honour. Ignoring them would be worst for rules about who
/// may do what (an erasure authorisation, a partition boundary, a <c>selfAccess.via</c> mapping),
/// because the generated host would quietly be more open than the document says. So
/// <see cref="DocumentLoader"/> refuses the document, and names every such feature, before
/// anything is generated or verified.
/// </summary>
public static class UnsupportedFeatures
{
    public static IReadOnlyList<ValidationError> Find(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var found = new List<ValidationError>();

        void Refuse(string location, string what) =>
            found.Add(new ValidationError(location, $"{what} is not supported by the dotnetcqrs generator yet"));

        if (root.TryGetProperty("ingresses", out var ingresses) && ingresses.ValueKind == JsonValueKind.Object && ingresses.EnumerateObject().Any())
            Refuse("/ingresses", "an ingress (schema 3.4.0)");
        if (root.TryGetProperty("partitioning", out _))
            Refuse("/partitioning", "partitioning (schema 3.7.0)");
        if (root.TryGetProperty("dataSubjects", out _))
            Refuse("/dataSubjects", "a dataSubjects.erasure declaration (schema 3.2.0)");

        if (root.TryGetProperty("readModels", out var readModels) && readModels.ValueKind == JsonValueKind.Object)
            foreach (var readModel in readModels.EnumerateObject())
                if (readModel.Value.TryGetProperty("selfAccess", out var selfAccess) && selfAccess.TryGetProperty("via", out _))
                    Refuse($"/readModels/{readModel.Name}/selfAccess/via", "selfAccess.via (schema 3.2.0)");

        if (root.TryGetProperty("slices", out var slices) && slices.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var slice in slices.EnumerateArray())
            {
                foreach (var (property, what) in SliceFeatures)
                    if (slice.TryGetProperty(property, out _))
                        Refuse($"/slices/{i}/{property}", what);
                if (slice.TryGetProperty("scenarios", out var scenarios) && scenarios.ValueKind == JsonValueKind.Array)
                {
                    var j = 0;
                    foreach (var scenario in scenarios.EnumerateArray())
                    {
                        if (scenario.TryGetProperty("given", out var given) && given.ValueKind == JsonValueKind.Array
                            && given.EnumerateArray().Any(g => g.ValueKind == JsonValueKind.Object && g.TryGetProperty("elapsed", out _)))
                            Refuse($"/slices/{i}/scenarios/{j}/given", "an elapsed step in a scenario (schema 3.5.0)");
                        j++;
                    }
                }
                i++;
            }
        }
        return found;
    }

    private static readonly (string Property, string What)[] SliceFeatures =
    [
        ("ingressId", "a slice started by an ingress (schema 3.4.0)"),
        ("outcomes", "outcomes (schema 3.3.0)"),
        ("delay", "an automation started by a delay (schema 3.5.0)"),
        ("schedule", "an automation started by a schedule (schema 3.5.0)"),
        ("effect", "an automation effect (schema 3.6.0)"),
        ("partitionFrom", "partitionFrom (schema 3.7.0)"),
    ];
}
