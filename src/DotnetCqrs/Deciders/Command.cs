namespace DotnetCqrs.Deciders;

/// <summary>
/// An incoming intent: a name plus its JSON payload. <see cref="Actor"/>/<see cref="Now"/>/
/// <see cref="Provenance"/> are populated by <see cref="DeciderRegistry.HandleWithMetaAsync"/>
/// from its meta dictionary before <c>Decide</c> sees the command — a bare
/// <see cref="Command"/> literal leaves them "" ("no actor"), same as an anonymous
/// command handled via the meta-less <see cref="DeciderRegistry.HandleAsync"/>.
/// </summary>
public sealed record Command(string Name, string Payload, string Actor = "", string Now = "", string Provenance = "");
