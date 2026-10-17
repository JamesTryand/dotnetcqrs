using DotnetCqrs.Codegen.Domain;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>An aggregate name as a generated message writes it: lower-case words ("loan request does not exist"),
/// as a document's own error scenarios say it, with an acronym run kept as written.</summary>
public class NamesWordsTests
{
    [Theory]
    [InlineData("task", "task")]
    [InlineData("loanRequest", "loan request")]
    [InlineData("LoanRequest", "loan request")]
    [InlineData("orderPDF", "order PDF")]
    [InlineData("HTTPRequestLog", "HTTP request log")]
    [InlineData("a", "a")]
    public void An_aggregate_name_reads_as_words(string name, string words) =>
        Assert.Equal(words, Names.Words(name));
}
