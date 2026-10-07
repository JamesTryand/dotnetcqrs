using System.Globalization;
using System.Text.Json;
using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;
using DotnetCqrs.Projections;
using DotnetCqrs.ReadModels;

namespace DotnetCqrs.Crypto;

// Governed erasure (venture-overview decision 0013). The data subject's lifecycle lives in
// DataSubject.cs; this file is everything around it: the retention policy that says whether a
// request may proceed, the queue projection that makes requests visible, and the governor that
// drives a request to a conclusion.

/// <summary>A retention policy's answer to "may this subject be erased now?".</summary>
/// <param name="Allowed">True: no retention duty stands, so the request is approved.</param>
/// <param name="Reason">When held: a short code or non-identifying text, stored in the clear on the
/// subject's stream (see <see cref="DataSubject"/>).</param>
/// <param name="ReviewAt">When held: when the governor should ask again. Null means "do not
/// re-check on a schedule" (for example, waiting for a legal hold to be released).</param>
public readonly record struct RetentionDecision(bool Allowed, string? Reason = null, DateTimeOffset? ReviewAt = null)
{
    public static RetentionDecision Allow => new(true);

    public static RetentionDecision Hold(string reason, DateTimeOffset? reviewAt) => new(false, reason, reviewAt);
}

/// <summary>Whether a subject's data must still be kept. Owned by the business, not by the
/// library: which duties apply and for how long is a legal question per system. Two shapes ship
/// here: implement this interface directly for anything bespoke, or configure
/// <see cref="PeriodRetentionPolicy"/> (a period after the subject's last activity) for the
/// common case. <see cref="CompositeRetentionPolicy"/> combines several duties.</summary>
public interface IRetentionPolicy
{
    Task<RetentionDecision> EvaluateAsync(string subjectId, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>When the subject last did something that starts or extends a retention duty (their
/// last entry, last payment, leaving date...). The one domain fact <see cref="PeriodRetentionPolicy"/>
/// cannot know for itself.</summary>
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

/// <summary>The configurable default: keep a subject's data for a fixed period after their last
/// activity. <c>new PeriodRetentionPolicy(years: 6, activity, reason: "payroll-tax")</c> holds a
/// request until six years after <see cref="ISubjectActivity.LastActivityAsync"/>, then allows it.
/// A subject with no recorded activity has nothing to retain and is allowed.</summary>
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

/// <summary>Several duties at once (tax, employment, pension...): held while ANY of them holds, until
/// the latest review date among them.</summary>
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

/// <summary>One row of the erasure request queue.</summary>
public sealed record ErasureRequestRow(
    string SubjectId, ErasureStage Stage, string? Reason, string? RequestedBy,
    DateTimeOffset? RequestedAt, DateTimeOffset? ReviewAt, bool LegalHold);

/// <summary>The erasure request queue as a read model: one row per subject that has ever had a
/// request or a legal hold. It is what an administrator screen lists ("pending", "held until..."),
/// and what <see cref="ErasureGovernor"/> reads to find held requests whose review date has come.
/// Holds no personal data: reasons are codes, and the subject id is opaque.</summary>
public sealed class ErasureRequestsProjection(IReadModelStore store) : IProjection
{
    public const string Table = "erasureRequests";

    public string Name => "erasure-requests";
    public IReadOnlyList<string> Tables => [Table];

    public async Task InitAsync(CancellationToken ct = default)
    {
        await using var command = store.Connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {Table} (
                subject_id TEXT PRIMARY KEY,
                stage TEXT NOT NULL,
                reason TEXT,
                requested_by TEXT,
                requested_at TEXT,
                review_at TEXT,
                legal_hold INTEGER NOT NULL DEFAULT 0
            )
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The one timestamp format the table uses, so text comparison is time comparison.</summary>
    internal static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseUtc(object? value) =>
        value is string s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed : null;

    public async Task ApplyAsync(Event ev, CancellationToken ct)
    {
        if (ev.Aggregate != DataSubject.Aggregate) return;
        if (ev.Type is not (DataSubject.ErasureRequestedEvent or DataSubject.ErasureHeldEvent or DataSubject.ErasureApprovedEvent
            or DataSubject.SubjectErasedEvent or DataSubject.LegalHoldPlacedEvent or DataSubject.LegalHoldReleasedEvent)) return;

        await using var bypass = await store.BeginBypassAsync(ct).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(string.IsNullOrWhiteSpace(ev.Data) ? "{}" : ev.Data) ?? [];
        string? Str(string name) => data.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? Time(string name) =>
            Str(name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? Utc(t) : null;

        // Make sure the row exists (a legal hold can arrive before any request), then set what
        // this event is about. Plain UPDATEs keep the SQL portable between SQLite and Postgres.
        await using (var insert = store.Connection.CreateCommand())
        {
            insert.CommandText = $"INSERT INTO {Table} (subject_id, stage) VALUES (@id, 'None') ON CONFLICT (subject_id) DO NOTHING";
            insert.AddParam("@id", ev.AggregateId);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var update = store.Connection.CreateCommand();
        update.AddParam("@id", ev.AggregateId);
        switch (ev.Type)
        {
            case DataSubject.ErasureRequestedEvent:
                update.CommandText = $"UPDATE {Table} SET stage = 'Requested', reason = @reason, requested_by = @by, requested_at = @at, review_at = NULL WHERE subject_id = @id";
                update.AddParam("@reason", Str("reason"));
                update.AddParam("@by", Str("requestedBy"));
                update.AddParam("@at", Time("requestedAt"));
                break;
            case DataSubject.ErasureHeldEvent:
                update.CommandText = $"UPDATE {Table} SET stage = 'Held', reason = @reason, review_at = @review WHERE subject_id = @id";
                update.AddParam("@reason", Str("reason"));
                update.AddParam("@review", Time("reviewAt"));
                break;
            case DataSubject.ErasureApprovedEvent:
                update.CommandText = $"UPDATE {Table} SET stage = 'Approved', review_at = NULL WHERE subject_id = @id";
                break;
            case DataSubject.SubjectErasedEvent:
                update.CommandText = $"UPDATE {Table} SET stage = 'Erased', review_at = NULL WHERE subject_id = @id";
                break;
            case DataSubject.LegalHoldPlacedEvent:
                update.CommandText = $"UPDATE {Table} SET legal_hold = 1 WHERE subject_id = @id";
                break;
            case DataSubject.LegalHoldReleasedEvent:
                update.CommandText = $"UPDATE {Table} SET legal_hold = 0 WHERE subject_id = @id";
                break;
        }
        await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The queue, optionally narrowed to one stage, oldest request first.</summary>
    public static Task<IReadOnlyList<ErasureRequestRow>> ListAsync(IReadModelStore store, ErasureStage? stage = null, CancellationToken ct = default) =>
        store.ReadAsync<IReadOnlyList<ErasureRequestRow>>(async (connection, readCt) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT subject_id, stage, reason, requested_by, requested_at, review_at, legal_hold FROM {Table}"
                + (stage is null ? "" : " WHERE stage = @stage") + " ORDER BY requested_at, subject_id";
            if (stage is not null) command.AddParam("@stage", stage.Value.ToString());
            var rows = new List<ErasureRequestRow>();
            await using var reader = await command.ExecuteReaderAsync(readCt).ConfigureAwait(false);
            while (await reader.ReadAsync(readCt).ConfigureAwait(false))
                rows.Add(new ErasureRequestRow(
                    reader.GetString(0), Enum.Parse<ErasureStage>(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    ParseUtc(reader.IsDBNull(4) ? null : reader.GetString(4)), ParseUtc(reader.IsDBNull(5) ? null : reader.GetString(5)),
                    reader.GetInt64(6) != 0));
            return rows;
        }, ct);

    /// <summary>Subjects whose held request has reached its review date and has no legal hold.</summary>
    internal static Task<IReadOnlyList<string>> DueForReviewAsync(IReadModelStore store, DateTimeOffset now, int limit, CancellationToken ct) =>
        store.ReadAsync<IReadOnlyList<string>>(async (connection, readCt) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT subject_id FROM {Table} WHERE stage = 'Held' AND legal_hold = 0 AND review_at IS NOT NULL AND review_at <= @now ORDER BY review_at, subject_id LIMIT {limit}";
            command.AddParam("@now", Utc(now));
            var ids = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(readCt).ConfigureAwait(false);
            while (await reader.ReadAsync(readCt).ConfigureAwait(false)) ids.Add(reader.GetString(0));
            return ids;
        }, ct);
}

/// <summary>Drives an erasure request to a conclusion. A consumer on the subject's own stream:
/// <list type="bullet">
/// <item><c>ErasureRequested</c> (and <c>LegalHoldReleased</c>): ask the <see cref="IRetentionPolicy"/>, then
/// dispatch <c>ApproveErasure</c> or <c>HoldErasure</c>. A legal hold overrides the policy.</item>
/// <item><c>ErasureApproved</c>: dispatch <c>EraseSubject</c>, which <see cref="SubjectKeyDestroyer"/> then
/// turns into a destroyed key.</item>
/// </list>
/// A held request does not wait forever: <see cref="ReviewDueAsync"/> re-asks the policy for every held
/// request whose review date has come, and <see cref="RunReviewLoopAsync"/> does that on a timer, so the
/// lifecycle always closes. Everything it dispatches is idempotent, so the at-least-once delivery of the
/// consumer engine is safe, and every command is attributed to <see cref="SystemActor"/>.</summary>
public sealed class ErasureGovernor(
    DeciderRegistry registry, IEventStore events, IRetentionPolicy policy,
    IReadModelStore? readModels = null, TimeProvider? time = null, Action<string>? log = null) : IConsumer
{
    public const string ConsumerName = "pii:erasure-governor";

    /// <summary>The actor recorded on everything the governor decides.</summary>
    public const string SystemActor = "system:retention-policy";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => ConsumerName;

    public async Task ApplyAsync(Event ev, CancellationToken ct)
    {
        if (ev.Aggregate != DataSubject.Aggregate) return;
        switch (ev.Type)
        {
            case DataSubject.ErasureRequestedEvent:
            case DataSubject.LegalHoldReleasedEvent:
                await EvaluateAsync(ev.AggregateId, ct).ConfigureAwait(false);
                break;
            case DataSubject.ErasureApprovedEvent:
                await DispatchAsync(ev.AggregateId, DataSubject.EraseSubjectCommand, "{}", ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Rules on one subject's open request: nothing happens if there is none.</summary>
    public async Task EvaluateAsync(string subjectId, CancellationToken ct)
    {
        var state = await LoadAsync(subjectId, ct).ConfigureAwait(false);
        if (state.Erased || state.Stage is not (ErasureStage.Requested or ErasureStage.Held)) return;

        if (state.LegalHold)
        {
            // A legal hold overrides any policy. The request waits (with no review date: releasing the
            // hold is what re-opens it).
            if (state.Stage == ErasureStage.Requested)
                await DispatchAsync(subjectId, DataSubject.HoldErasureCommand, Payload("legal-hold", null), ct).ConfigureAwait(false);
            return;
        }

        var decision = await policy.EvaluateAsync(subjectId, _time.GetUtcNow(), ct).ConfigureAwait(false);
        if (decision.Allowed)
            await DispatchAsync(subjectId, DataSubject.ApproveErasureCommand, "{}", ct).ConfigureAwait(false);
        else
            await DispatchAsync(subjectId, DataSubject.HoldErasureCommand, Payload(decision.Reason ?? "retention", decision.ReviewAt), ct).ConfigureAwait(false);
    }

    /// <summary>Re-asks the policy for every held request whose review date has come. Returns how many it looked at.</summary>
    public async Task<int> ReviewDueAsync(CancellationToken ct)
    {
        if (readModels is null)
            throw new InvalidOperationException("ErasureGovernor needs the read-model store to find held requests due for review");
        var due = await ErasureRequestsProjection.DueForReviewAsync(readModels, _time.GetUtcNow(), 100, ct).ConfigureAwait(false);
        foreach (var subjectId in due) await EvaluateAsync(subjectId, ct).ConfigureAwait(false);
        return due.Count;
    }

    /// <summary>Runs <see cref="ReviewDueAsync"/> every <paramref name="interval"/> until cancelled. A failed pass
    /// is logged and retried on the next tick, never fatal.</summary>
    public async Task RunReviewLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ReviewDueAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log?.Invoke($"erasure review pass failed: {ex.Message}"); }
            try { await Task.Delay(interval, _time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<DataSubjectState> LoadAsync(string subjectId, CancellationToken ct)
    {
        var decider = DataSubject.Create();
        var stream = await events.LoadStreamAsync(DataSubject.Aggregate, subjectId, ct).ConfigureAwait(false);
        return stream.Aggregate(decider.InitialState(), (state, ev) => decider.Evolve(state, ev));
    }

    private static string Payload(string reason, DateTimeOffset? reviewAt) =>
        JsonSerializer.Serialize(new { reason, reviewAt }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private async Task DispatchAsync(string subjectId, string command, string payload, CancellationToken ct)
    {
        var meta = new Dictionary<string, object>
        {
            ["actor"] = SystemActor,
            ["now"] = _time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };
        try
        {
            await registry.HandleWithMetaAsync(DataSubject.Aggregate, subjectId, new Command(command, payload), meta, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (DeciderRegistry.IsRejection(ex))
        {
            // The subject moved on between reading it and deciding (erased, held, a hold placed): the
            // current state wins. Retrying would fail the same way, so this is not a delivery failure.
            log?.Invoke($"{command} for subject '{subjectId}' not applied: {ex.Message}");
        }
    }
}

/// <summary>Wiring for the governed-erasure workflow on a host.</summary>
public static class ErasureGovernance
{
    /// <summary>Registers the request-queue projection and the governor with the engine, creating the queue table.
    /// The host still registers <see cref="SubjectKeyDestroyer"/> and calls
    /// <see cref="DataSubject.RegisterDataSubjects"/>, adds <see cref="ErasureRequestsProjection.Tables"/> to its
    /// write-guard list, and runs <see cref="ErasureGovernor.RunReviewLoopAsync"/> in the background so held requests are
    /// reviewed. Authorise the dataSubject commands: the generic gateway allows a command with no declared policy.</summary>
    public static async Task<ErasureGovernor> RegisterErasureGovernanceAsync(
        this ConsumerEngine engine, DeciderRegistry registry, IEventStore events, IReadModelStore readModels,
        IRetentionPolicy policy, TimeProvider? time = null, Action<string>? log = null, CancellationToken ct = default)
    {
        var projection = new ErasureRequestsProjection(readModels);
        await projection.InitAsync(ct).ConfigureAwait(false);
        var governor = new ErasureGovernor(registry, events, policy, readModels, time, log);
        engine.Register(projection);
        engine.Register(governor);
        return governor;
    }
}
