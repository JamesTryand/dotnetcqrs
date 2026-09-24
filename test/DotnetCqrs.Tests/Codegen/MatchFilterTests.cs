using DotnetCqrs.Codegen;
using DotnetCqrs.Codegen.Generation;
using DotnetCqrs.Codegen.Mapping;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Milestones D5/D6: schema 3.1.0 <c>match</c> filters. The normalizer's pinned rules (they
/// are a contract: a hashed index only works if every implementation agrees byte for byte)
/// and the mapper's reference checks, which JSON Schema can't express.
/// </summary>
public class MatchFilterTests
{
    [Theory]
    [InlineData("none", "  Alice ", "  Alice ")]
    [InlineData("caseFold", "  ALICE Smith ", "alice smith")]
    [InlineData("caseFold", "ﬁle", "file")] // NFKC folds the ligature
    [InlineData("caseFold", "STRASSE Straße", "strasse straße")] // simple lowercase: ß is NOT folded to ss
    [InlineData("email", " Alice.Example@Example.COM ", "alice.example@example.com")]
    [InlineData("personName", "  José   María  Ñúñez ", "jose maria nunez")]
    [InlineData("personName", "Zoë\tO'Brien", "zoe o'brien")]
    [InlineData("phone", "+44 (0)20 7946-0000", "+4402079460000")]
    [InlineData("phone", "020 7946 0000", "02079460000")] // no country inferred
    public void Normalizers_follow_the_pinned_rules(string normalize, string input, string expected) =>
        Assert.Equal(expected, MatchNormalizer.Normalize(normalize, input));

    [Fact]
    public void Like_patterns_escape_wildcards_so_a_term_matches_itself()
    {
        Assert.Equal(@"50\%", MatchNormalizer.EscapeLike("50%"));
        Assert.Equal(@"a\\b", MatchNormalizer.EscapeLike(@"a\b"));
        Assert.Equal(@"a\_b%", MatchNormalizer.Pattern("prefix", "a_b"));
        Assert.Equal(@"%100\%%", MatchNormalizer.Pattern("contains", "100%"));
        Assert.Equal("exact", MatchNormalizer.Pattern("exact", "exact"));
    }

    private static string Doc(string filter, bool emailIsPii = false, string emailType = "string") => DocTemplate
        .Replace("__TYPE__", emailType)
        .Replace("__PII__", emailIsPii ? """, "pii": true, "piiSubject": "customerId" """ : "")
        .Replace("__FILTER__", filter);

    private const string DocTemplate = """
        {
          "eventModelingSchemaVersion": "3.1.0", "id": "match-test", "name": "Match Test",
          "swimlanes": [{"id":"s","name":"S","kind":"team"}],
          "events": {
            "customer-registered": {"name": "Customer Registered", "swimlaneId": "s", "aggregate": "Customer",
              "fields": [
                {"name": "customerId", "type": "string", "idAttribute": true},
                {"name": "email", "type": "__TYPE__"__PII__}
              ]}
          },
          "commands": {"register-customer": {"name": "Register Customer", "aggregate": "Customer"}},
          "readModels": {
            "customers": {
              "name": "Customers", "builtFromEventIds": ["customer-registered"],
              "fields": [
                {"name": "customerId", "type": "string", "idAttribute": true},
                {"name": "email", "type": "__TYPE__"__PII__}
              ],
              "filters": [__FILTER__]
            }
          },
          "screens": {"scr1": {"name": "S"}},
          "slices": [{"id": "register", "name": "Register", "pattern": "stateChange", "swimlaneId": "s", "status": "created",
            "screenId": "scr1", "commandId": "register-customer", "eventIds": ["customer-registered"], "scenarios": []}]
        }
        """;

    private static MappingResult Map(string json) => DocumentMapper.Map(DocumentLoader.Parse(json));

    private static IReadOnlyList<string> Errors(string json) =>
        Assert.Throws<DocumentMappingException>(() => Map(json)).Report.Errors;

    [Fact]
    public void A_match_filter_on_a_plain_text_field_maps_with_caseFold_as_the_default_normalizer()
    {
        var result = Map(Doc("""{"param": "q", "field": "email", "kind": "match", "mode": "prefix", "minPrefixLength": 3}"""));

        var filter = Assert.Single(result.Domains.Single().ReadModels.Single().Filters);
        Assert.True(filter.IsMatch);
        Assert.Equal(("prefix", "caseFold", (int?)3), (filter.Mode, filter.Normalize, filter.MinPrefixLength));
    }

    [Fact]
    public void Contains_on_a_pii_field_maps_but_is_flagged_as_plaintext_at_rest()
    {
        var result = Map(Doc("""{"param": "q", "field": "email", "kind": "match", "mode": "contains", "normalize": "email"}""", emailIsPii: true));

        Assert.Single(result.Domains.Single().ReadModels.Single().Filters);
        Assert.Contains(result.Report.Warnings, w => w.Contains("PLAINTEXT index") && w.Contains("excluded from backups"));
    }

    [Fact]
    public void Exact_on_a_pii_field_maps_to_a_keyed_hash_index()
    {
        var result = Map(Doc("""{"param": "q", "field": "email", "kind": "match", "mode": "exact", "normalize": "email"}""", emailIsPii: true));

        var domain = result.Domains.Single();
        Assert.Equal("exact", Assert.Single(domain.ReadModels.Single().Filters).Mode);
        Assert.DoesNotContain(result.Report.Warnings, w => w.Contains("PLAINTEXT"));
        var files = CSharpGenerator.Generate(domain);
        var index = Assert.Single(files, f => f.Name == "CustomersHashedIndex.cs");
        Assert.Contains("customers__hash_email_email_exact", index.Source);
        Assert.DoesNotContain(files, f => f.Name == "CustomersSearchIndex.cs"); // no plaintext index
    }

    [Fact]
    public void Prefix_on_a_pii_field_must_declare_minPrefixLength() =>
        Assert.Contains(Errors(Doc("""{"param": "q", "field": "email", "kind": "match", "mode": "prefix"}""", emailIsPii: true)),
            e => e.Contains("prefix on pii field \"email\" must declare minPrefixLength"));

    [Fact]
    public void Prefix_on_a_pii_field_maps_but_warns_that_prefix_hashes_can_be_enumerated()
    {
        var result = Map(Doc("""{"param": "q", "field": "email", "kind": "match", "mode": "prefix", "minPrefixLength": 3}""", emailIsPii: true));

        Assert.Contains(CSharpGenerator.Generate(result.Domains.Single()), f => f.Name == "CustomersHashedIndex.cs");
        Assert.Contains(result.Report.Warnings, w => w.Contains("confirm a guessed") && w.Contains("hmac"));
    }

    [Fact]
    public void Two_pii_prefix_filters_sharing_an_index_must_agree_on_minPrefixLength() =>
        Assert.Contains(Errors(Doc("""
            {"param": "q", "field": "email", "kind": "match", "mode": "prefix", "minPrefixLength": 3},
            {"param": "r", "field": "email", "kind": "match", "mode": "prefix", "minPrefixLength": 4}
            """, emailIsPii: true)),
            e => e.Contains("declares a different minPrefixLength"));

    [Fact]
    public void Prefixes_are_cut_in_code_points_from_the_minimum_to_the_whole_value()
    {
        Assert.Equal(["ali", "alic", "alice"], MatchNormalizer.Prefixes("alice", 3));
        Assert.Empty(MatchNormalizer.Prefixes("al", 3));
        // U+1F600 is two UTF-16 units but one code point: never split, and counted once.
        Assert.Equal(["a😀", "a😀b"], MatchNormalizer.Prefixes("a😀b", 2));
        Assert.Equal(3, MatchNormalizer.CodePointLength("a😀b"));
    }

    [Fact]
    public void A_match_filter_naming_a_field_the_read_model_lacks_is_rejected() =>
        Assert.Contains(Errors(Doc("""{"param": "q", "field": "phone", "kind": "match", "mode": "exact"}""")),
            e => e.Contains("names field \"phone\", which the read model does not declare"));

    [Fact]
    public void A_match_filter_on_a_non_text_field_is_rejected_for_now() =>
        Assert.Contains(Errors(Doc("""{"param": "q", "field": "email", "kind": "match", "mode": "exact"}""", emailType: "integer")),
            e => e.Contains("match is supported on text fields only"));

    [Fact]
    public void The_schema_itself_rejects_minPrefixLength_outside_prefix_mode() =>
        Assert.ThrowsAny<Exception>(() => DocumentLoader.Parse(
            Doc("""{"param": "q", "field": "email", "kind": "match", "mode": "exact", "minPrefixLength": 3}""")));
}
