namespace DotnetCqrs.Codegen.Mapping;

/// <summary>
/// Collects everything a mapping decided or could not decide — ports pocketcqrs's
/// <c>emschema.Report</c>. The answer to a recurring class of defect: every mapping
/// choice taken on a document's behalf (an invented aggregate, a folded field type, a
/// dropped PII flag) is named here, or it's a silent wrong-doing.
/// </summary>
public sealed class MappingReport
{
    /// <summary>Make the document unmappable.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>Decisions taken on the document's behalf, and gaps the author still has
    /// to close. Mapping proceeds.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Names what this project cannot represent at all.</summary>
    public List<string> Lossy { get; } = [];

    public bool HasErrors => Errors.Count > 0;

    public void Error(string message) => Errors.Add(message);
    public void Warn(string message) => Warnings.Add(message);
    public void Note(string message) => Lossy.Add(message);
}

/// <summary>Thrown when a document could not be mapped — see <see cref="MappingReport.Errors"/>.</summary>
public sealed class DocumentMappingException(MappingReport report)
    : Exception($"document could not be mapped ({report.Errors.Count} error(s)): {string.Join("; ", report.Errors)}")
{
    public MappingReport Report { get; } = report;
}
