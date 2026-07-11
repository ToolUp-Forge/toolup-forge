# Phase 209 — Internet-readiness secure-default scorecard

**What changes:** A new read-side `InternetReadinessScorecard` (in `ToolUp.Platform.Server`) that
consolidates the secure-by-default request-edge `IConfigValidator`s — CSRF (`csrf-default-mode` /
`csrf-hardening-split-origin`), security headers (`security-headers-mode`), CORS (`cors-config`),
forwarded-headers trust (`forwarded-headers-trust`), redirect base (`public-base-url-format`),
dev-admin bootstrap (`auto-bootstrap-dev-admin-mode`), SSE auth (`sse-auth-mode`), share-token +
OAuth secret provenance (`share-token-signing-key-provenance` / `oauth-secret-encryption-mode` /
`oauth-state-store-instance`), secret-store encryption (`encrypted-secret-store-mode`), request-body
cap (`max-request-body-bytes`), and rate limiting (`rate-limit-mode`) — into a single graded report
that answers one operator question: *is this deployment safe to expose to the internet?*

It is a **pure projection** over the aggregated `ValidatorOutcome` list the Phase 9m config-preflight
aggregator already computes. Each catalog control is tagged with a category (`edge` / `auth` /
`transport` / `limits`) and a weight, and its status is a read of the outcome its own validator
already reported (`Ok` → pass, `Warning` → warn, `Error` → fail). A control whose validator did not
run is `NotAssessed` — never a fabricated pass. The overall grade is worst-status:

| Assessed controls | Grade |
|---|---|
| ≥1 assessed, all pass | `Ready` |
| no fail, ≥1 warn (or nothing assessed) | `ReadyWithWarnings` |
| ≥1 fail | `NotReady` |

**Breaking?** No. The scorecard introduces **no new validation logic and no new failure semantics**:
it can only report a fail that a validator already returned as `Error` (which would itself have
aborted preflight). It is off unless a consumer calls it, allocates nothing when unused, and a
deployment that never requests it is byte-for-byte unchanged (GP 11 / GP 13).

## Opting in

The scorecard is a library surface, not a mounted route. Emit it at the end of compose from the
preflight snapshot the aggregator populates:

```fsharp
open ToolUp.Platform

// `outcomes` is the aggregated preflight run — e.g. `snapshot.LastRun`
// from the `IPreflightSnapshot` service, or the list returned by
// `ConfigValidatorAggregator.validate`.
InternetReadinessScorecard.logIfEnabled emitScorecard logger outcomes |> ignore
```

`logIfEnabled false _ _` is a no-op that reads no outcomes and touches no logger — this is the
"behind a compose option" seam. When `true`, the scorecard is rendered and logged at a level matching
the grade (`Info` for `Ready`, `Warn` otherwise). For a diagnostics surface instead of a log line,
call `InternetReadinessScorecard.ofOutcomes outcomes` and project `report.Controls` / `report.Grade`
onto your own admin read (it mirrors the Phase 177 deployment-readiness shape).

The scorecard reads **only** the controls in its catalog; an `Error` from any other validator is not
its concern (and never inflates its grade).

## SDK adoption

Adoption is **optional** and per-consumer. This phase is marked `consumer_facing` so it appears as a
row in the generated `ToolUp/SDK-ADOPTION.md`. A consumer records its stance in its **own**
`sdk-adoption.json` — never by hand-editing the generated matrix:

```json
{ "refactor": "209", "status": "adopted",  "sha": "<commit>" }   // wired logIfEnabled
{ "refactor": "209", "status": "n-a", "reason": "no internet-facing surface" }
```

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — `Platform.Tests` pack green, including the
  Phase 209 `internet-readiness scorecard` pack (all-satisfied → `Ready`; flipping any one control
  downgrades the grade and names the offender; a non-catalog `Error` does not fail the scorecard;
  every catalog name keys a live secure-by-default validator).

## Rollback

Remove the `InternetReadinessScorecard.logIfEnabled` call. No data migration, no persisted state,
no mounted route — the scorecard is a pure read over signals that already exist.
