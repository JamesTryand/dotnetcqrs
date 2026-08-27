using System.Text;

namespace DotnetCqrs.Codegen.Generation;

/// <summary>Generates one <see cref="DotnetCqrs.Reactors.IReactor"/> per reactor —
/// ports pocketcqrs's <c>reactorGo</c>; dotnetcqrs's <c>IReactor</c> is structurally
/// close enough to pocketcqrs's own that this stays a near-direct translation.</summary>
internal static class ReactorGenerator
{
    public static GeneratedFile Generate(Domain.Domain domain, Domain.Reactor reactor)
    {
        var typeName = GenerationSupport.ExportName(reactor.Name) + "Reactor";

        var b = new StringBuilder();
        b.AppendLine("using DotnetCqrs.Deciders;");
        b.AppendLine("using DotnetCqrs.EventStore;");
        b.AppendLine("using DotnetCqrs.Reactors;");
        b.AppendLine();
        b.AppendLine($"namespace Generated.{GenerationSupport.ExportName(domain.Aggregate)};");
        b.AppendLine();
        b.AppendLine($"/// <summary>Reacts to \"{domain.Aggregate}\"'s events by dispatching {reactor.Aggregate}/{reactor.Command}");
        b.AppendLine("/// -- one reaction per triggering event, its payload the causing event's own data");
        b.AppendLine("/// untouched. The target id is derived from the source event, so a replay hits the");
        b.AppendLine("/// target's own \"already exists\" rejection instead of dispatching twice -- keep it");
        b.AppendLine("/// deterministic if you change it.</summary>");
        b.AppendLine($"public sealed class {typeName} : IReactor");
        b.AppendLine("{");
        b.AppendLine($"    public string Name => \"{reactor.Name}\";");
        b.AppendLine();
        b.AppendLine("    public IReadOnlyList<Reaction> React(Event ev)");
        b.AppendLine("    {");
        b.AppendLine($"        if (ev.Type is not ({string.Join(" or ", reactor.On.Select(n => $"\"{n}\""))})) return [];");
        b.AppendLine();
        b.AppendLine($"        return [new Reaction(\"{reactor.Aggregate}\", \"{reactor.IdPrefix}\" + ev.AggregateId, new Command(\"{reactor.Command}\", ev.Data))];");
        b.AppendLine("    }");
        b.AppendLine("}");

        return new GeneratedFile($"{typeName}.cs", b.ToString());
    }
}
