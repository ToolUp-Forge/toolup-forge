# Grant-governance completeness (Phase 730) — consumer migration

**What changes.** Three completions to the module-declared grant policy
(Phase 551) and the consented-grant registry (Phase 552):

| # | Change |
|---|---|
| A | A grant recorded on a policy-bearing module now emits a `GrantRecorded` audit event — the success twin of the existing `GrantPolicyRefused`. |
| B | `PermissionGrants.grantModuleAccess` classifies its inner-store failures instead of relabelling them all as `GrantRefusal.UnbackedGrant`. |
| C | AI tool listing and dispatch consult the module's grant policy and consent, not just the permission map. |

## Do you have to do anything?

**No, if no module in your deployment declares a `GrantPolicy`.** The write
guard is composed only when the policy registry is non-empty, the request
stamps are written only then, and the AI gate resolves to a constant `true`
after one failed `GetService`. No new audit row, no extra store read, the same
tool list in the same order — byte-for-byte unchanged (GP 11). Record `n-a`.

**Yes, in one specific case, if you do declare one** — see B below. It is a
silent-wrong-outcome, not a compile error, so it is worth the two minutes.

---

## B — the one change that can silently mislead

`grantModuleAccess` used to map **every** error from the underlying store onto
`GrantRefusal.UnbackedGrant` ("the written permission entry carries no adequate
grant record"). By the time control reached that line the statement was
provably false — the permission entry and its grant record are written in one
document update precisely so they cannot be inconsistent — so the one thing the
message asserted was the one thing that could not have happened.

Two situations were being reported as that refusal, and they are not it:

- **A write parked by dual control** (Phase 555). Nothing was persisted; a
  second, distinct administrator must approve a named request.
- **A storage failure.** Nothing was persisted; the store said why.

They now have their own shapes:

```fsharp
match! PermissionGrants.grantModuleAccess store registry request with
| Ok GrantWriteOutcome.Granted -> "Access granted."
| Ok (GrantWriteOutcome.RecordedPendingConsent subjectId) ->
    $"Recorded — awaiting {subjectId}'s acceptance."

// NEW — on the Ok side, because the act was accepted into a ceremony
| Ok (GrantWriteOutcome.QueuedForApproval requestId) ->
    $"Queued for approval. A second administrator must approve {requestId}."

// NEW — carries the store's own message verbatim
| Error (GrantRefusal.StoreUnavailable (moduleName, message)) ->
    $"Not saved ({moduleName}): {message}"

| Error refusal -> GrantRefusal.describe refusal
```

**The hazard is a wildcard on the `Ok` side.** If your admin surface reads

```fsharp
| Ok _ -> "Access granted."
```

it will now report **"Access granted"** for a write that was parked and did not
apply — the operator walks away believing the grant is live, and nobody
approves the pending request. The old code returned an `Error` here, so the
same wildcard was accidentally safe.

**What to do:** grep your handling of `grantModuleAccess` for a wildcard `Ok _`
and give `QueuedForApproval` its own arm. If you compile with warnings-as-errors
on incomplete matches, the compiler finds both new cases for you; otherwise F#
reports FS0025 as a warning and the build still succeeds.

If you do not compose the Phase 555 dual-control gate, `QueuedForApproval` is
unreachable in your deployment and only `StoreUnavailable` applies.

---

## A — the new audit event

`AuditEvent.GrantRecorded` carries `ActorId` / `SubjectId` / `ModuleName` /
`DeclaredPolicy` / `State` / `Permissions` / `Justification`. `State` is the
field worth cutting on: `"active"` means authority now exists, `"pending-consent"`
means it is recorded and confers nothing until the grantee accepts.

- **Custom `IAuditSink`:** nothing to implement. Sinks receive `AuditEvent`
  values and the codec is registered by the SDK; a new case flows through.
- **CEF sink:** `GrantRecorded` is mapped **High**, alongside `PermissionChanged`
  and `GrantConsentApproved`.
- **SIEM rules:** if you alert on `GrantPolicyRefused`, consider the twin. A
  deployment watching only refusals can see every grant that was *stopped* and
  none that *succeeded*.
- **Exhaustive `match` over `AuditEvent`:** a new case is an FS0025 warning, not
  an error. Add an arm or keep your wildcard.

Fires only for modules declaring a policy stricter than `AdminDiscretion` —
ordinary permission changes remain `PermissionChanged`.

---

## C — the AI tool gate

If a subject holds a permission entry on a module whose grant is **pending their
acceptance**, or whose **counterparty consent has been revoked**, that module's
AI tools are now:

- absent from the per-turn tool list the model is offered,
- absent from `IAIAssistantApi.GetAvailableTools`,
- refused at the dispatch site (typed `Denied` + an `UnconsentedGrantRefused`
  audit row — the same event the Remoting seam already emits for this refusal,
  not a new event type),
- refused inside the `_platform.*` cross-module tools, per target module.

Nothing to implement. This is the enforcement of grants you already declared,
arriving where it was previously missing: the entry was present in both cases,
so the Phase 36.A RBAC filter admitted them and the module stayed visible to the
agent loop while being inert at the Remoting seam.

**What to check once:** a module whose tools "disappear" from the assistant for
one user is now an expected outcome of a pending or revoked grant, not a defect.
The `UnconsentedGrantRefused` rows name the module and the `InertReason`
(`awaiting-subject-consent`, `consent-revoked`, `no-grant-record`, …), which is
where to look first.

---

## Verification

1. Grant a policy-bearing module and confirm one `GrantRecorded` row with the
   expected `State`.
2. If you compose dual control: make the same grant and confirm your surface
   renders "queued", naming the request id, rather than "granted".
3. If you use the AI assistant: with a `RequiresSubjectConsent` module recorded
   pending, confirm its tools are absent from `GetAvailableTools` for that
   subject, and present once the grantee accepts.

## Rollback

Pin the previous SDK version. There is no persisted-state migration — no
document shape changed, and `GrantRecorded` rows are additive audit history that
an older build simply does not emit.
