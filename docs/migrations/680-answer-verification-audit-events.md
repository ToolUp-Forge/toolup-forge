# 680 — answer-verification audit events + the provenance join

**What changes:** the answer-verification verdict now lands as a typed `AuditEvent` through
`IAuditLog`, **beside** the per-token `IEventStore` trail it has written since the numeric-fidelity
gate shipped — not instead of it. The recorded row additionally names the provenance the answer
stands on: the fact ids its verified figures cite, a digest over them, and — where a deployment has
them — the certificate covering that chain and the sealed composition the boot preflight affirmed.

All additive. `AnswerGate`, `ServerConfig`, `ServerApp` and the existing
`AnswerVerifier.runVerificationStage` signature are **unchanged**; a deployment that composes no
answer gate is untouched, and one that composes a gate but no audit log records nothing new
(GP 11, GP 13).

| Type / module | Tier | Purpose |
|---|---|---|
| `AnswerVerificationTokenAudit` | `Platform.Core` | one numeric token with its fact-match status |
| `AnswerVerificationPayload` | `Platform.Core` | the verdict for one answer, plus the join fields |
| `AnswerVerificationPassed`, `AnswerVerificationFlagged` | `Platform.Core` | the two new `AuditEvent` cases |
| `IAnswerProvenanceAnchors` | `Platform.Core` | the seam supplying the certificate ref + composition seal id |
| `AnswerVerifier.AnswerAuditJoin` (+ `.none`, `.auditOnly`) | `AI.Server` | where the row is recorded, and what anchors it may name |
| `AnswerVerifier.AnswerProvenanceAnchors` (`none`, `compositionSeal`, `fromBootVerification`) | `AI.Server` | ready-made anchor implementations |
| `AnswerVerifier.provenanceChainHead` | `AI.Server` | the digest over an answer's cited fact ids |
| `AnswerVerifier.runVerificationStageWithJoin` | `AI.Server` | the verification stage, with the join |
| `AIServerApp.withAnswerProvenanceAnchors` | `AI.Server` | declare the deployment's anchors |

## What you get without doing anything

Nothing changes for a deployment that has not composed the answer gate.

A deployment that **has** composed the gate (`AIServerApp.withNumericFidelityGate` /
`withAnswerVerifier` in a non-`Off` mode) starts recording one `AnswerVerificationPassed` or
`AnswerVerificationFlagged` row per answered turn, through the `IAuditLog` the platform already
composes — so whichever audit sinks it composed receive it, a hash-chained ledger among them. Both
join fields (`CertificateRef`, `CompositionSealId`) are `None` until anchors are declared; the fact
half (`CitedFactIds`, `ProvenanceChainHead`) is derived from the verdict itself and is populated
from the first turn.

**The affirmative row is recorded too**, deliberately. A row written only when a figure went
unverified cannot distinguish a clean answer from an answer the gate never saw, and those are the two
states an auditor most needs to tell apart. Volume is one row per answered turn, not one per token.

## Declaring the anchors (optional)

```fsharp
// The composition seal a verified boot affirmed. `verifyComposition` already
// returns the result; keep the sealed binding you started it with.
let bootResult = // ... ServerApp.verifyComposition options app
let anchors =
    AnswerVerifier.AnswerProvenanceAnchors.fromBootVerification bootResult (Some sealedBinding)

app |> AIServerApp.withAnswerProvenanceAnchors anchors
```

`fromBootVerification` returns `None` for the seal id unless the verdict was **affirmative**, even
though a binding is present on a drifted or failed boot too. Naming the seal there would assert
precisely what the boot check declined to affirm — and under the log-and-serve default such a process
keeps serving, which is exactly when the distinction matters.

To report a certificate ref as well, implement `IAnswerProvenanceAnchors` directly. Implementations
**report** what already exists; issuing a certificate from the answer path would put a signing
round-trip on every turn, and this seam deliberately does not.

## Reading the trail

```fsharp
// Every flagged answer in a scope.
let! flagged = auditLog.GetAuditTrail(scopeId, None, Some "AnswerVerificationFlagged")

// The unverified-token detail, unchanged, on the IEventStore surface.
let! tokens = eventStore.ReadBySource(scopeId, AnswerVerifier.AuditSourceModule)
```

Two surfaces, two questions. The `IEventStore` rows stay the module-scoped query surface for
individual unverified figures; the `IAuditLog` row is the chained, sink-replicated statement of the
whole verdict plus its provenance. Neither replaces the other, and the `IEventStore` write path is
byte-for-byte what it was.

The two event types are separate discriminators rather than one type with a `Flagged` flag, because
the discriminator is what a SIEM rule and a ledger query cut on — a flag inside the payload puts that
cut somewhere neither can reach without decoding every row.

## What the row does not prove

- **`ProvenanceChainHead` is a join key, not a seal.** It is a SHA-256 over the canonical join of the
  cited fact ids — recomputable by anyone holding those ids, which is what makes it portable, and
  unsigned, which means it attests to nothing on its own. The signed artefact over the same chain is
  the grounding certificate, and `CertificateRef` is where it is named.
- **`CompositionSealId` describes boot, not now.** It carries the boot preflight's bound whole: the
  composition at the instant it was derived, with nothing said about post-boot mutation.
- **A verified figure is a figure that matched a retrieved fact.** The gate's own bound, inherited
  unchanged: it says the number is in the fact tier, never that the fact is right.

## Custom `IAnswerVerifier` implementations

Unaffected. The seam, its inputs, and the `AnswerVerification` it returns are unchanged; the join is
built from that return value by the stage around it.

## Direct callers of `runVerificationStage`

Unaffected — the pre-680 function is preserved verbatim and delegates with an empty join. Move to
`runVerificationStageWithJoin` (one extra `AnswerAuditJoin` argument, after `eventStore`) to record
the row. The SDK's own chat handler already does; a consumer that drives the stage itself opts in
when it chooses to.

## Verification

1. `dotnet build ToolUp.Forge.sln`
2. `dotnet run --project Build.fsproj -- VerifyAll`
3. With the gate composed, serve one grounded answer and read
   `GetAuditTrail(scopeId, None, Some "AnswerVerificationPassed")` — expect one row whose
   `CitedFactIds` resolve in your fact store.

## Rollback

Revert the commit. Nothing persists that a pre-680 deployment cannot read: the new rows decode via
their own codec entries, and an SDK without them reports them through the existing
`AuditEventDecodeFailed` summary path rather than advancing the cursor silently.
