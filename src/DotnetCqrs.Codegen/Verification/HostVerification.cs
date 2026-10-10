using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetCqrs.Crypto;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Codegen.Verification;

/// <summary>
/// A host's own deciders, for verifying a document against the code it actually runs
/// (<see cref="ScenarioVerifier.VerifyAsync(Model.Document, Mapping.MappingResult, HostDeciders, CancellationToken)"/>).
/// <see cref="Register"/> registers every decider the host registers, with its PII protectors
/// over the <see cref="IKmsClient"/> it is handed (an in-memory stand-in, so scenarios need no
/// key service). A scenario's <c>when.actor</c>/<c>when.at</c> fix who and when; without them
/// <see cref="DefaultActor"/> and <see cref="DefaultAt"/> are used.
/// </summary>
public sealed record HostDeciders(Action<DeciderRegistry, IKmsClient> Register)
{
    public string DefaultActor { get; init; } = "scenario";
    public string DefaultAt { get; init; } = "2026-01-01T09:00:00.000Z";
}

/// <summary>
/// Runs command scenarios in-process against a host's own deciders: the given events are
/// appended to a fresh in-memory store, the command is handled with the scenario's actor and
/// time, and the result is compared with the scenario: the event types, then every value the
/// scenario declares (pii revealed first), or the refusal's message. Unlike a generated stand-in,
/// the host's deciders do their real arithmetic, so a worked example checks it.
/// </summary>
internal static class HostScenarioRunner
{
    public static async Task<ScenarioResult> RunAsync(CommandScenarioInput scenario, HostDeciders host, CancellationToken ct)
    {
        try
        {
            await using var store = await SqliteEventStore.OpenAsync(":memory:");
            var registry = new DeciderRegistry(store);
            var kms = new InMemoryKmsClient();
            host.Register(registry, kms);

            if (scenario.Given.Count > 0)
                await store.AppendAsync(scenario.Aggregate, scenario.StreamId, 0,
                    scenario.Given.Select(g => new NewEvent(g.Type, g.Data)).ToList(), ct);

            var meta = new Dictionary<string, object>
            {
                ["actor"] = scenario.Actor ?? host.DefaultActor,
                ["now"] = scenario.At ?? host.DefaultAt,
            };
            IReadOnlyList<Event> produced;
            try
            {
                produced = await registry.HandleWithMetaAsync(scenario.Aggregate, scenario.StreamId,
                    new Command(scenario.CommandName, scenario.CommandPayload), meta, ct);
            }
            catch (Exception ex)
            {
                if (scenario.Kind != "error")
                    return Result(scenario, false, "the decider refused the command: " + ex.Message);
                return ex.Message == scenario.ExpectedErrorMessage
                    ? Result(scenario, true, "refused: " + ex.Message)
                    : Result(scenario, false, $"refused for another reason: expected \"{scenario.ExpectedErrorMessage}\", got \"{ex.Message}\"");
            }

            if (scenario.Kind == "error")
                return Result(scenario, false, $"expected a refusal but the command was accepted and produced {produced.Count} event(s)");

            var got = produced.Select(e => e.Type).ToList();
            if (!got.SequenceEqual(scenario.ExpectedEventTypes))
                return Result(scenario, false, $"expected [{string.Join(", ", scenario.ExpectedEventTypes)}], got [{string.Join(", ", got)}]");

            var expectedData = scenario.ExpectedEventData ?? [];
            for (var i = 0; i < expectedData.Count && i < produced.Count; i++)
            {
                if (expectedData[i] is not { } expectedJson) continue;
                var actual = await ScenarioValueMatch.RevealAsync(JsonNode.Parse(produced[i].Data), kms, ct);
                if (ScenarioValueMatch.Mismatch(JsonNode.Parse(expectedJson), actual, "") is { } mismatch)
                    return Result(scenario, false, $"{produced[i].Type}: {mismatch}");
            }
            return Result(scenario, true, $"would append {string.Join(", ", got)}");
        }
        catch (Exception ex)
        {
            return Result(scenario, false, "verification error: " + ex.Message);
        }
    }

    private static ScenarioResult Result(CommandScenarioInput s, bool passed, string detail) =>
        new(s.SliceId, s.ScenarioId, s.Name, s.Kind, Passed: passed, Skipped: false, detail);
}

/// <summary>Compares a scenario's declared event values with a produced event. Objects match as
/// subsets (fields the scenario doesn't mention aren't checked); lists match item by item and must
/// be the same length; numbers compare by value; a date-only string matches the same date at
/// midnight (the 2026-09-14T00:00:00Z form an API stores). A pii envelope is revealed first.</summary>
internal static class ScenarioValueMatch
{
    public static async Task<JsonNode?> RevealAsync(JsonNode? node, IKmsClient kms, CancellationToken ct)
    {
        switch (node)
        {
            case JsonObject obj when obj.Count == 1 && obj["$pii"] is JsonObject envelope:
            {
                var subject = envelope["s"]?.GetValue<string>();
                var cipher = envelope["c"]?.GetValue<string>();
                if (subject is null || cipher is null) return null;
                var result = await kms.DecryptAsync(subject, cipher, ct);
                return result.Plaintext is null ? null : JsonNode.Parse(result.Plaintext);
            }
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                    obj[key] = await RevealAsync(obj[key]?.DeepClone(), kms, ct);
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    array[i] = await RevealAsync(array[i]?.DeepClone(), kms, ct);
                return array;
            default:
                return node;
        }
    }

    public static string? Mismatch(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected)
        {
            case null:
                return actual is null ? null : $"{Where(path)} expected null, got {actual.ToJsonString()}";
            case JsonObject expectedObject:
                if (actual is not JsonObject actualObject) return $"{Where(path)} expected an object, got {Show(actual)}";
                foreach (var (key, value) in expectedObject)
                    if (Mismatch(value, actualObject.TryGetPropertyValue(key, out var field) ? field : null, path.Length == 0 ? key : $"{path}.{key}") is { } m)
                        return m;
                return null;
            case JsonArray expectedArray:
                if (actual is not JsonArray actualArray) return $"{Where(path)} expected a list, got {Show(actual)}";
                if (expectedArray.Count != actualArray.Count)
                    return $"{Where(path)} expected {expectedArray.Count} item(s), got {actualArray.Count}";
                for (var i = 0; i < expectedArray.Count; i++)
                    if (Mismatch(expectedArray[i], actualArray[i], $"{path}[{i}]") is { } m)
                        return m;
                return null;
            case JsonValue expectedValue:
                if (actual is not JsonValue actualValue) return $"{Where(path)} expected {expected.ToJsonString()}, got {Show(actual)}";
                var e = expectedValue.GetValue<JsonElement>();
                var a = actualValue.GetValue<JsonElement>();
                if (e.ValueKind == JsonValueKind.Number && a.ValueKind == JsonValueKind.Number)
                    return Math.Abs(e.GetDouble() - a.GetDouble()) < 1e-9 ? null : $"{Where(path)} expected {e.GetRawText()}, got {a.GetRawText()}";
                if (e.ValueKind == JsonValueKind.String && a.ValueKind == JsonValueKind.String && SameDate(e.GetString()!, a.GetString()!))
                    return null;
                return JsonElement.DeepEquals(e, a) ? null : $"{Where(path)} expected {e.GetRawText()}, got {a.GetRawText()}";
            default:
                return null;
        }
    }

    private static bool SameDate(string expected, string actual) =>
        expected.Length == 10 && DateOnly.TryParseExact(expected, "yyyy-MM-dd", out var day)
        && DateTimeOffset.TryParse(actual, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
        && at.UtcDateTime.TimeOfDay == TimeSpan.Zero && DateOnly.FromDateTime(at.UtcDateTime) == day;

    private static string Where(string path) => path.Length == 0 ? "the event" : path;

    private static string Show(JsonNode? node) => node is null ? "nothing" : node.ToJsonString();
}
