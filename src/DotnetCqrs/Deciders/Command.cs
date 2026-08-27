namespace DotnetCqrs.Deciders;

/// <summary>An incoming intent: a name plus its JSON payload.</summary>
public sealed record Command(string Name, string Payload);
