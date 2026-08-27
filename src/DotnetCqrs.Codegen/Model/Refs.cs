using System.Text.Json;

namespace DotnetCqrs.Codegen.Model;

/// <summary>References one event, with optional payload data — used in a scenario's
/// <c>given</c>/<c>then</c> and a slice's implicit event linkage.</summary>
public sealed record EventRef(string EventId, JsonElement? Data);

/// <summary>References one command, with optional payload data — used in a
/// <c>stateChange</c>/<c>error</c> scenario's <c>when</c>.</summary>
public sealed record CommandRef(string CommandId, JsonElement? Data);

/// <summary>References one read model query — used in a <c>stateView</c> scenario's
/// <c>when</c>.</summary>
public sealed record ReadModelQuery(string ReadModelId, JsonElement? QueryParams);
