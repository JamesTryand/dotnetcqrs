using System.Text.Json;

namespace DotnetCqrs.Codegen;

/// <summary>
/// Schema features this generator accepts in a valid document but does not implement. Since the
/// pinned schema moved from 3.1.1 to 3.8.0, a document can declare things added in 3.2.0–3.7.0
/// that no generated code here would honour. Ignoring them would be worst for rules about who
/// may do what (a partition boundary, a <c>selfAccess.via</c> mapping; an erasure authorisation was one until it
/// was generated),
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

        if (root.TryGetProperty("partitioning", out _))
            Refuse("/partitioning", "partitioning (schema 3.7.0)");

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
                i++;
            }
        }
        return found;
    }

    private static readonly (string Property, string What)[] SliceFeatures =
    [
        ("partitionFrom", "partitionFrom (schema 3.7.0)"),
    ];
}
