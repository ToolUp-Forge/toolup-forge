# Migration — Phase 196: adversarial fail-closed test pack for the auth/audit classifier

**Status:** **test-tier only — no public surface, no behaviour change (GP 11/13).** No consumer
action. This migration doc exists so the SDK-adoption matrix carries a row; every consumer cell is
⛔ N-A.

## What changes

A negative/adversarial test pack lands in `ToolUp.Platform.Tests`, codifying the "forgot the guard /
forgot to audit" defect class as structurally impossible to ship silently. It proves the *inverse* of
the Phase 69d/69h happy-path packs (which only asserted that correctly-annotated methods *allow* and
*audit*):

- **`Contracts/FailClosedContract.fs`** — a reusable, family-agnostic classifier fail-closed
  contract (`classifierFailClosedContract`). Every assertion drives `AuthClassifier.evaluate` (the
  exact per-request function the dispatcher calls) and demands a `Deny` for: `Unclassified` even
  against a god-mode caller, a classification-map miss (Phase 132 deny-on-miss), wrong/absent role,
  absent claim, missing tenant, anonymous-on-`RequiresAuth`, partial AND-satisfaction, and
  no-resolver-but-classified. Exported alongside the server-tier and `ToolUp.Platform.*` mirror
  classification maps.
- **`InProcess/AdversarialFailClosedTests.fs`** — the registered suite. Instantiates the contract for
  both attribute families, then adds adversarial fixtures driving the real composition path:
  - an un-annotated method makes `Remoting.buildHttpHandler` (resolver armed) and `Api.make` **refuse
    to start**, naming the record + the naked method;
  - the dispatcher's deny path builds an `ErrorCategory.Auth` envelope and `return!`s **before** the
    proxy invocation (source-pinned ordering — a denied call never reaches the handler body);
  - the wire deny signal is the opaque generic `category = "auth"`, and the wire envelope type
    (`CategorisedErrorResult`) carries **no structured deny-reason field** (the rich reason is
    server-side `AuthDecision.Deny` data, discriminable in logs, not on the wire);
  - a money-moving method **without** `[<Audit>]` is absent from `Audit.classify`'s map, so the
    dispatcher's `audits |> Map.tryFind` emission gate yields `None` → **zero** `AuditEvent`s (the
    omission is observable; the test fails the moment that silently changes); the inverse with
    `[<Audit("MoneyMoved")>]` emits **exactly one** `MoneyMoved` row;
  - forgetting `[<PiiSafe>]` keeps PII (`AccountNumber`, `Amount`) **out** of the audit payload
    (`<redacted:…>`), the fail-safe default.

`Auth.fs` and `Audit.fs` are exercised under adversarial test but **unchanged**. The pack is wired
into `Program.fs`'s `allTests` (Expecto only runs the supplied list).

## Known caveat surfaced by this pack (not fixed here — test-only phase)

The dispatcher's deny envelope **message text** currently embeds the server-side reason
(`"{method}: auth-denied: missing-role: Admin"` in `GiraffeAdapter.fs`), which contradicts the
`Auth.fs` doc comment on `AuthDecision.Deny` ("Reason is server-side only — not surfaced in the wire
body to avoid leaking authorisation rules"). The *structured* channel a client branches on is
unaffected (generic `category = "auth"`, no typed reason field), which is what this pack asserts.
Closing the doc/code gap (redact the message text, or amend the doc comment) is a production change
out of scope for a test-only phase — tracked as a TIDY-UP follow-up.

## Consumer action

None. No package surface changed; no recompile required beyond a normal SDK bump.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — the
  `Phase 196 — adversarial fail-closed pack` test list is green: every adversarial fixture either
  prevents startup or yields a `Deny`; the unaudited money-mover emits zero rows and the audited one
  exactly one; the PII default redacts.

## Rollback

Remove the two `<Compile>` entries + the `AdversarialFailClosedTests.tests` registration and delete
the two files. No production code is touched, so rollback is inert — it only drops the regression
gate that keeps un-annotated / un-audited methods from shipping silently.
