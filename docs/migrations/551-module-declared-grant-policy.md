# Module-declared grant policy (Phase 551) — consumer migration

**What changes.** A module can now declare a precondition on being granted to
anyone — `GrantPolicy` — and the declaration is enforced fail-closed at the
grant write *and again* at dispatch. Before this phase the admin-authored
`ModulePermissions` map was the sole authority, so a module had no way to say
"not without X first" and an accidental admin grant silently exposed module
state.

```fsharp
ServerModule.create "PartnerBenchmarks"
|> ServerModule.withGrantPolicy GrantPolicy.RequiresSubjectConsent
|> ServerModule.withGuardedApi partnerBenchmarksApi
```

| Arm | Precondition |
|---|---|
| `AdminDiscretion` (default) | none — byte-for-byte today |
| `RequiresAcknowledgement` | the granting admin confirms + records a justification; live immediately |
| `RequiresSubjectConsent` | recorded `PendingConsent`, **inert at dispatch** until the grantee accepts |
| `RequiresCounterpartyApproval of PartyRef` | refuses every grant until [Phase 552](552-consented-grant-registry-igrantconsentstore.md) ships `IGrantConsentStore` |

Enforcement is in **two** places, not one. A gate the write path must remember
to call is a defect class rather than a control (the Phase 311 lesson), so the
per-route module-access gate re-verifies the policy *on use*: a permission row
present without its consent artifact is inert, refused, and audited. That is
the control that survives a row written straight into the store, restored from
a backup, or produced by a migration.

## Do you have to do anything?

**Almost certainly not, if you declare no policy.** An all-default deployment
composes an **empty** `ModuleGrantPolicyRegistry`, which is never registered —
so the permission store is not decorated, `ScopeResolutionMiddleware` performs
no extra read, and the dispatch gate short-circuits before touching grants, the
audit log, or the scheduler. Persisted permission documents predating this
phase carry no `grants` property and read back as "no policy applies"
(GP 11 / GP 13). Record `n-a`.

**Yes, in one case that is not opt-in.** `TeamPermissions` gained a `Grants`
field. Update if either applies:

1. **You construct `TeamPermissions` with a record literal.** It no longer
   compiles. Add `Grants = Map.empty`:

   ```diff
    let permissions = {
        Defaults = defaults
        Members = members
        Exposure = exposure
   +    Grants = Map.empty
    }
   ```

2. **You ship a custom `IPermissionStore`.** No interface member was added —
   grant records ride the existing `GetTeamPermissions` document — but your
   serialisation must **round-trip `Grants`**. A store that silently drops it
   renders every policy-bearing grant inert at dispatch, which presents as
   "the user was granted the module and still gets 403". The
   `IPermissionStore` contract pack now asserts the round-trip, so run it
   against your implementation.

**Yes, if you adopt a policy.** The legacy `SetMemberPermissions` signature has
nowhere to carry acknowledgement or justification, so it refuses a
policy-bearing grant by construction with a typed, greppable error
(`GRANT-POLICY-ACK-REQUIRED`). Move that admin write path to the policy-aware
entry point:

```diff
-match! permissionStore.SetMemberPermissions(teamId, subjectId, "PartnerBenchmarks", [ Read ]) with
-| Ok () -> ...
-| Error e -> ...
+let request: GrantPolicyGuard.ModuleGrantRequest = {
+    TeamId = teamId
+    ActorId = actingAdminId
+    SubjectId = subjectId
+    ModuleName = "PartnerBenchmarks"
+    Permissions = [ Read ]
+    Evidence = GrantPolicyGuard.GrantEvidence.acknowledged "ticket OPS-14"
+}
+
+match! GrantPolicyGuard.PermissionGrants.grantModuleAccess permissionStore registry request with
+| Ok GrantWriteOutcome.Granted -> ...
+| Ok (GrantWriteOutcome.RecordedPendingConsent subject) -> // tell them to accept
+| Error refusal -> // typed; GrantRefusal.describe / .code
```

The grantee accepts with
`GrantPolicyGuard.PermissionGrants.acceptGrant store teamId subjectId moduleName`.

Two behaviours worth knowing before you declare a policy:

- **A policy-bearing module cannot be handed out through team `Defaults`.** A
  default applies to every member lacking an explicit entry, so there is no
  subject to acknowledge or consent — refused rather than silently ineffective.
- **Tightening a policy invalidates grants written under the looser one.**
  Evidence records the policy it satisfied; grandfathering would make
  tightening a no-op for exactly the grants you tightened because of. Re-grant
  them under the new policy.

**Revocation is never blocked**, whatever the policy — a policy constrains the
creation of authority, never its removal.

## New audit events

| Event | Fires | Severity |
|---|---|---|
| `GrantPolicyRefused` | a grant **write** did not satisfy the declared policy | CEF High |
| `UnconsentedGrantRefused` | a module was refused at **dispatch** for an inert grant | CEF High |

`UnconsentedGrantRefused.InertReason` separates `no-grant-record` (the
injected-row signature) from `awaiting-subject-consent` (an ordinary pending
grant), `evidence-below-declared-policy` (a tightened policy), and
`counterparty-approval-unavailable`. Alert on the first; the second is routine.

## Verification

- `dotnet build` — a `TeamPermissions` record literal that needs the new field
  fails here.
- Run the `IPermissionStore` contract pack against your store if you ship one.
- With no policy declared, your permission documents and dispatch behaviour are
  unchanged; diff a persisted document before and after to confirm.
