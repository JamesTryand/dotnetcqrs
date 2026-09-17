namespace DotnetCqrs.Crypto;

/// <summary>An encrypt-shaped call hit a subject with no Transit key yet — the caller
/// skipped (or lost a race with) <see cref="IKmsClient.EnsureKeyAsync"/>. A programming
/// error to fix at the call site, not an expected runtime state — contrast with
/// <see cref="KmsKeyState.Destroyed"/>, decrypt's own expected "gone" outcome.</summary>
public sealed class KmsKeyNotFoundException(string subjectId)
    : Exception($"No KMS key exists for subject '{subjectId}' — call EnsureKeyAsync first.")
{
    public string SubjectId { get; } = subjectId;
}

/// <summary>The facade responded with something this client doesn't know how to
/// interpret — an unexpected status code, a missing/malformed JSON body, or (for a
/// batch call) a per-item error surfaced through <see cref="Pii{T}.RevealAsync"/>.</summary>
public sealed class KmsProtocolException : Exception
{
    public KmsProtocolException(string message) : base(message) { }
    public KmsProtocolException(string message, Exception inner) : base(message, inner) { }
}
