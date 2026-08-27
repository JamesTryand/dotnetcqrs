namespace DotnetCqrs.Codegen.Mapping;

/// <summary>Decisions an operator makes on a document's behalf.</summary>
public sealed class MappingOptions
{
    /// <summary>
    /// Maps a schema element id to an aggregate name — consulted ONLY where the
    /// document itself gives no <c>aggregate</c> tag.
    ///
    /// The <c>aggregate</c> field is optional in the schema, and a worked example may
    /// deliberately leave boundary elements untagged (a boundary notification isn't
    /// targeting a domain aggregate at all). But this project's write side is
    /// aggregate-organized throughout, and an automation's result events are real log
    /// events, so something must own that stream. Rather than guess — deriving from
    /// swimlaneId would silently merge unrelated stream families into one, invisibly,
    /// until the log is already wrong — the operator states the intent here.
    /// </summary>
    public IReadOnlyDictionary<string, string> AggregateOverrides { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>A document translated into this project's terms.</summary>
public sealed class MappingResult
{
    /// <summary>One entry per aggregate, in stable name order.</summary>
    public required IReadOnlyList<Domain.Domain> Domains { get; init; }

    public required MappingReport Report { get; init; }
}
