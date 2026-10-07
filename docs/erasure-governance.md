# Governed erasure

Crypto-shredding ([concepts](concepts.md#personal-data-crypto-shredding-instead-of-delete)) makes
erasure *possible*: destroy a person's key and their personal data is unreadable everywhere, including
backups. It is also **permanent**, and it runs into a duty pulling the other way. A business is usually
obliged to keep some records about a person (payroll, tax, employment, pensions) for years, and Article 17
itself exempts erasure where retention is needed to meet a legal obligation. Destroying the key the
moment someone asks would make a reference request, a pension query or a tax enquiry unanswerable.

So **a request is not an erasure.** Erasure is a governed process, decided in
`thing/venture-overview` decision `0013`. This page is how it works and how to wire it into a host. It is
not legal advice: which records you must keep, and for how long, is a question for whoever advises your
business. The library gives you the machinery and leaves that answer to you.

## The lifecycle

```
                    ┌──────────── legal hold (any time before erasure) ────────────┐
                    ▼                                                              │
 (none) ──RequestErasure──▶ Requested ──policy says "not yet"──▶ Held ──review──┐  │
                               │                                  ▲             │  │
                               │ policy says "go"                 └─────────────┘  │
                               ▼                                                    │
                            Approved ──EraseSubject──▶ Erased  (terminal)           │
```

Everything is an event on the subject's own stream (`dataSubject/{subjectId}`), so *why* data was or
was not erased is itself on the record:

| Command | Event | Who (default) |
|---|---|---|
| `RequestErasure` | `ErasureRequested` | `manager`, `administrator` |
| `HoldErasure` | `ErasureHeld` (reason, review date) | the governor, or `administrator` |
| `ApproveErasure` | `ErasureApproved` | the governor, or `administrator` |
| `PlaceLegalHold` / `ReleaseLegalHold` | `LegalHoldPlaced` / `LegalHoldReleased` | `administrator` |
| `EraseSubject` | `SubjectErased` | the governor; `administrator` as a direct override |

- A legal hold blocks `ApproveErasure` **and** `EraseSubject`.
- Asking twice is harmless; asking after erasure does nothing.
- `SubjectErased` still carries no payload, so a stream written before this workflow still reads.
- **Reasons are stored in the clear and outlive the erasure** (the stream is never rewritten). A reason is a
  short code such as `payroll-tax` or `legal-hold`, never personal data. Anything over 200 characters is refused.

## The retention policy

Whether a request may proceed is the business's call, so it is a plug-in:

```csharp
public interface IRetentionPolicy
{
    Task<RetentionDecision> EvaluateAsync(string subjectId, DateTimeOffset now, CancellationToken ct = default);
}
// RetentionDecision.Allow  |  RetentionDecision.Hold(reason, reviewAt)
```

Two shapes ship:

1. **Configure the common case.** Keep data for a period after the subject's last activity:
   `new PeriodRetentionPolicy(years: 6, activity, reason: "payroll-tax")`. `ISubjectActivity` is the one domain
   fact the library cannot know (their last entry, last payment, leaving date); you implement it. Several duties
   combine with `CompositeRetentionPolicy` (held while any holds, until the latest date). `AllowAllRetentionPolicy`
   is for a system with nothing to retain.
2. **Implement the interface** for anything else.

Bind the period from configuration however your host does it; the policy is data (`RetainYears: 6`), not code.

## The governor

`ErasureGovernor` is a consumer on the subject's stream. It reacts to `ErasureRequested` (and to
`LegalHoldReleased`) by asking the policy, then approving or holding; it reacts to `ErasureApproved` by sending
`EraseSubject`, which `SubjectKeyDestroyer` turns into a destroyed key. A legal hold overrides any policy.

A held request does not wait forever. `ReviewDueAsync` re-asks the policy for every held request whose review date
has come, and `RunReviewLoopAsync(interval, ct)` does that on a timer, so the lifecycle always closes. Everything the
governor sends is idempotent and attributed to `system:retention-policy`, so the consumer engine's at-least-once
delivery is safe.

## Wiring it into a host

```csharp
registry.RegisterDataSubjects();                              // the aggregate
engine.Register(new SubjectKeyDestroyer(kms));                // key destruction
var governor = await engine.RegisterErasureGovernanceAsync(   // queue projection + governor
    registry, eventStore, readModelStore, policy);
// add ErasureRequestsProjection.Tables to the write-guard list, then, in the background:
_ = governor.RunReviewLoopAsync(TimeSpan.FromHours(1), stoppingToken);
```

`ErasureRequestsProjection.ListAsync(store, stage)` lists the queue ("pending", "held until...") for an administrator
screen. It holds no personal data.

**Authorise every command.** The generic gateway allows a command that has no declared policy, and erasure cannot
be undone. Declare one for each, for example from `DataSubject.DefaultRequiredRoles(command)` (anyone who runs
the roster may *ask*; only an administrator may hold, approve, place or release a legal hold, or erase directly):

```csharp
foreach (var command in DataSubject.Commands)
    policies[(DataSubject.Aggregate, command)] = new(RequiredRole: [.. DataSubject.DefaultRequiredRoles(command)!]);
```

## What this does not do

- **Partial erasure.** Whole-subject only: you cannot erase a subject's free text while keeping their payroll
  record. That needs a retention class per field in the schema (decision 0013, stage 2), and it is not built.
- **Decide your retention duties.** It enforces the policy you give it.
- **Pocketcqrs yet.** The command and event names above are meant to be the cross-stack contract
  (`lab/cqrs-system-contracts`) so the Go library can implement the same lifecycle; that port, and the
  contract entry, are not done.
