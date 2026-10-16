# Governed erasure (DotnetCqrs.Retention)

Crypto-shredding ([concepts](concepts.md#personal-data-crypto-shredding-instead-of-delete)) makes erasure
*possible*: destroy a person's key and their personal data is unreadable everywhere, including backups. It is also
**permanent**, and a business is usually obliged to keep some records about a person for a time. Article 17 of the
GDPR itself exempts erasure where retention is needed to meet a legal obligation or for legal claims.

## The core: a request, and the erasure

`DotnetCqrs.Crypto` ships the `dataSubject` aggregate (`dataSubject/{subjectId}`) with two commands:

| Command | Event |
|---|---|
| `RequestErasure` (`reason`, optional) | `ErasureRequested` (`requestedBy`, `reason`, `requestedAt`) |
| `EraseSubject` | `SubjectErased` (no payload) |

**A request is not an erasure.** It records that someone asked, and erases nothing. Asking twice is harmless; asking
after erasure does nothing. `EraseSubject` records the erasure, and `SubjectKeyDestroyer` destroys the key.
`DataSubject.AcceptedCommands(state)` says which of the two a subject would act on, for a screen.

With only the core, nothing acts on a request: deciding one is the host's. `DotnetCqrs.Retention` is the opt-in
module that does it.

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

The module adds four commands to the core's two, and only ever adds refusals to them. Everything is an event on the
subject's own stream, so *why* data was or was not erased is itself on the record:

| Command | Event |
|---|---|
| `HoldErasure` | `ErasureHeld` (reason, review date) |
| `ApproveErasure` | `ErasureApproved` |
| `PlaceLegalHold` / `ReleaseLegalHold` | `LegalHoldPlaced` / `LegalHoldReleased` |

- A legal hold blocks `ApproveErasure` **and** `EraseSubject`.
- **Reasons are stored in the clear and outlive the erasure** (the stream is never rewritten). A reason is a short
  code such as `statutory-records` or `legal-hold`, never personal data. Anything over 200 characters is refused.
- `GovernedSubject.AcceptedCommands(state)` covers all six commands.

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
   `new PeriodRetentionPolicy(years: 6, activity, reason: "statutory-records")`. `ISubjectActivity` is the one domain
   fact the library cannot know (a last transaction, a contract ending, an account closing); you implement it.
   Several duties combine with `CompositeRetentionPolicy` (held while any holds, until the latest date).
   `AllowAllRetentionPolicy` is for a system with nothing to retain.
2. **Implement the interface** for anything else.

Which duties apply, and for how long, is a question for whoever advises your business. The library enforces the
policy you give it.

## The governor

`ErasureGovernor` is a consumer on the subject's stream. It reacts to `ErasureRequested` (and to `LegalHoldReleased`)
by asking the policy, then approving or holding; it reacts to `ErasureApproved` by sending `EraseSubject`. A legal
hold overrides any policy.

A held request does not wait forever. When the governor sees `ErasureHeld` with a review date, it records a review in
the outbox (`IOutbox`, kept by the event store), due on that date. Its `Reviews` worker asks the policy again then:
the due date itself, not a timer looking for work, and it reads the subject's own stream, never a read model.
Everything the governor sends is idempotent and attributed to `system:retention-policy`, so at-least-once delivery
is safe.

## Wiring it into a host

```csharp
registry.RegisterDataSubjects();                               // the core aggregate (kept if the module's is there)
engine.Register(new SubjectKeyDestroyer(kms));                 // key destruction
var governor = engine.RegisterErasureRetention(                // the governed aggregate + the governor
    registry, eventStore, policy, reviews: eventStore);
_ = governor.Reviews!.RunAsync(stoppingToken);                 // reviews on their due dates, on the writer
```

`RegisterErasureRetention` registers the governed decider in place of the core one, whichever is called first.

**Authorise every command.** Erasure cannot be undone, so the gateway **fails closed for this aggregate**: while no
`authorize` policy is wired at all, it refuses every `dataSubject` command with `403` and records nothing. (A generated
host is an unauthenticated scaffold and wires none, so erasure is closed there by default; for a throwaway local run
only, `DOTNETCQRS_ALLOW_UNAUTHORIZED_ERASURE=1` opens it, with a warning at startup.) Once you wire a policy it
decides, and it must cover every command in `GovernedSubject.Commands`: a policy that returns true for a command it
has no rule for opens erasure again. Which roles may send which is yours to decide. Typically a wider group may ask
than may hold, approve, place or release a legal hold, or erase directly.

**Showing requests on a screen** is a read model like any other: a projection of the `dataSubject` events into a
table your screen lists.

## What this does not do

- **Partial erasure.** Erasure is whole-subject: every value under the subject's key becomes unreadable.
- **Decide your retention duties.** It enforces the policy you give it.
