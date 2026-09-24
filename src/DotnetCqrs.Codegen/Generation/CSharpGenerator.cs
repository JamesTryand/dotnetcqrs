using DotnetCqrs.Codegen.Domain;

namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// Generates compilable C# source from a <see cref="Domain.Domain"/>: a decider, one
/// projection per read model, one reactor per reactor — same model, same
/// <see cref="Domain.DomainValidation.Validate"/> gate, ports pocketcqrs's
/// <c>GenerateGo</c>. THE GENERATED CODE IS A STARTING POINT, NOT A FINISHED DOMAIN:
/// the interesting invariants (which of a multi-event command's events actually
/// applies, the real per-event projection rules) are the author's job — see each
/// generator's own doc comment for exactly what's a placeholder.
/// </summary>
public static class CSharpGenerator
{
    public static IReadOnlyList<GeneratedFile> Generate(Domain.Domain domain)
    {
        domain.Validate();

        var files = new List<GeneratedFile> { DeciderGenerator.Generate(domain) };
        foreach (var readModel in domain.ReadModels)
        {
            files.Add(ProjectionGenerator.Generate(domain, readModel));
            if (SearchIndexGenerator.Generate(domain, readModel) is { } searchIndex)
                files.Add(searchIndex);
            if (HashedIndexGenerator.Generate(domain, readModel) is { } hashedIndex)
                files.Add(hashedIndex);
        }
        foreach (var reactor in domain.Reactors)
            files.Add(ReactorGenerator.Generate(domain, reactor));
        return files;
    }
}
