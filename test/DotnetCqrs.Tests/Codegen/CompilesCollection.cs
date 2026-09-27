namespace DotnetCqrs.Tests.Codegen;

/// <summary>Tests that compile and run a scratch project in a child <c>dotnet</c> process
/// (generated hosts, generated deciders, the scenario verifier's harness, the CLI). They run
/// one class at a time: each child build is CPU-heavy, and several at once on a small
/// machine only makes each slower until they time out. The rest of the suite still runs in
/// parallel. They also carry <c>Category=Slow</c>, so the everyday loop can skip them:
/// <c>dotnet test --filter "Category!=Slow"</c>.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CompilesCollection
{
    public const string Name = "Compiles a scratch project";
}
