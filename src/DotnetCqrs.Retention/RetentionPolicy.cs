using DotnetCqrs.Crypto;

namespace DotnetCqrs.Retention;

/// <summary>A retention policy's answer to "may this subject be erased now?".</summary>
/// <param name="Allowed">True: no retention duty stands, so the request is approved.</param>
/// <param name="Reason">When held: a short code or non-identifying text, stored in the clear on the subject's
/// stream.</param>
/// <param name="ReviewAt">When held: when the governor should ask again. Null means "do not re-check on a schedule"
/// (for example, waiting for a legal hold to be released).</param>
public readonly record struct RetentionDecision(bool Allowed, string? Reason = null, DateTimeOffset? ReviewAt = null)
{
    public static RetentionDecision Allow => new(true);

    public static RetentionDecision Hold(string reason, DateTimeOffset? reviewAt) => new(false, reason, reviewAt);
}

/// <summary>Whether a subject's data must still be kept. Owned by the business, not by the library: which duties
/// apply and for how long is a legal question per system. Implement this interface for anything bespoke, or configure
/// <see cref="PeriodRetentionPolicy"/> (a period after the subject's last activity) for the common case.
/// <see cref="CompositeRetentionPolicy"/> combines several duties.</summary>
public interface IRetentionPolicy
{
    Task<RetentionDecision> EvaluateAsync(string subjectId, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>When the subject last did something that starts or extends a retention duty (a transaction, a contract
/// ending, an account closing). The one domain fact <see cref="PeriodRetentionPolicy"/> cannot know for itself.</summary>
public interface ISubjectActivity
{
    /// <summary>The most recent such moment, or null if there is none.</summary>
    Task<DateTimeOffset?> LastActivityAsync(string subjectId, CancellationToken ct = default);
}

/// <summary>No duty stands: every request is approved. For a system with nothing to retain, and for tests.</summary>
public sealed class AllowAllRetentionPolicy : IRetentionPolicy
{
    public Task<RetentionDecision> EvaluateAsync(string subjectId, DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult(RetentionDecision.Allow);
}

/// <summary>Keeps a subject's data for a fixed period after their last activity:
/// <c>new PeriodRetentionPolicy(years: 6, activity, reason: "statutory-records")</c> holds a request until six years
/// after <see cref="ISubjectActivity.LastActivityAsync"/>, then allows it. A subject with no recorded activity has
/// nothing to retain and is allowed.</summary>
public sealed class PeriodRetentionPolicy(int years, ISubjectActivity activity, string reason = "retention-period", int months = 0) : IRetentionPolicy
{
    public async Task<RetentionDecision> EvaluateAsync(string subjectId, DateTimeOffset now, CancellationToken ct = default)
    {
        var last = await activity.LastActivityAsync(subjectId, ct).ConfigureAwait(false);
        if (last is null) return RetentionDecision.Allow;
        var until = last.Value.AddYears(years).AddMonths(months);
        return now >= until ? RetentionDecision.Allow : RetentionDecision.Hold(reason, until);
    }
}

/// <summary>Several duties at once: held while ANY of them holds, until the latest review date among them, with their
/// distinct reasons joined.</summary>
public sealed class CompositeRetentionPolicy(params IRetentionPolicy[] policies) : IRetentionPolicy
{
    public async Task<RetentionDecision> EvaluateAsync(string subjectId, DateTimeOffset now, CancellationToken ct = default)
    {
        var holds = new List<RetentionDecision>();
        foreach (var policy in policies)
        {
            var decision = await policy.EvaluateAsync(subjectId, now, ct).ConfigureAwait(false);
            if (!decision.Allowed) holds.Add(decision);
        }
        if (holds.Count == 0) return RetentionDecision.Allow;

        var reason = string.Join(", ", holds.Select(h => h.Reason).Where(r => !string.IsNullOrEmpty(r)).Distinct());
        if (reason.Length > DataSubject.MaxReasonLength) reason = reason[..DataSubject.MaxReasonLength];
        var dated = holds.Where(h => h.ReviewAt is not null).Select(h => h.ReviewAt!.Value).ToList();
        return RetentionDecision.Hold(reason.Length == 0 ? "retention" : reason, dated.Count == 0 ? null : dated.Max());
    }
}
