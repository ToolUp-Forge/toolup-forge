// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GrantPolicyTests

// ─── Phase 551 — module-declared grant policy ────────────────────────
//
// Before this phase the admin-authored `ModulePermissions` map was the
// sole authority on module access: a module had no way to say "not
// without X first", so an accidental admin grant silently exposed module
// state. A module now declares a narrowing-only `GrantPolicy`, enforced
// at the grant WRITE and — the part that actually holds — again at
// DISPATCH.
//
// Four things this pack is here to prove, matching 551.E:
//
//   1. **Parse is fail-closed.** No persisted token, however mangled,
//      reads back as `AdminDiscretion`. A table drives this because the
//      interesting cases are the ones nobody thinks to write by hand.
//   2. **The write guard refuses per arm**, with a typed refusal naming
//      the policy — and admits the writes it should, including every
//      revocation and every `AdminDiscretion` module.
//   3. **Dispatch refuses a grant row injected straight into the store**
//      — the permission entry exists, no record backs it, and the module
//      is refused with an `UnconsentedGrantRefused` row. This is the
//      Phase 311 property: the write guard can be bypassed, this cannot.
//   4. **`AdminDiscretion` everywhere is byte-parity with today** (GP 11
//      / GP 13) — an empty registry short-circuits before grants, the
//      audit log or the scheduler are touched at all.
//
// **Non-vacuity.** The dispatch cases run through
// `GrantPolicyGuard.guardDispatch` with a SYNCHRONOUS scheduler, so a
// single call yields both the decision and the emitted audit row: a
// guard that refused without auditing, or audited without refusing,
// fails here rather than passing half the assertion. The recording
// audit log `failtest`s nothing and simply accumulates, so an assertion
// on "exactly one row, of this shape" is a real assertion and not a
// tautology over an empty list.

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.PermissionStore

// ─── Fixtures ────────────────────────────────────────────────────────

let private Read = ModulePermission.Read
let private Write = ModulePermission.Write

/// A fresh blob-backed store over a private temp dir, so the write-guard
/// cases exercise the REAL serialisation path (including the grant-record
/// round trip) rather than a stub that cannot lose a field.
let private freshStore () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-grantpolicy-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    PermissionStore(LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage) :> IPermissionStore

/// Accumulates every recorded event so a test can assert the exact row.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Recorded = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }

        member _.GetAuditTrail(_, _, _) = async { return [] }

/// The synchronous scheduler the dispatch cases pass to
/// `guardDispatch`, so decision and emission are observed together.
let private runNow (work: Async<unit>) = Async.RunSynchronously work

let private registryOf declarations =
    GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations declarations

let private activeRecord policy justification = {
    State = GrantState.Active
    SatisfiedPolicy = policy
    Justification = justification
    ConsentedBy = None
}

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 551 — module-declared grant policy" [

        // ── 1. Fail-closed parse ─────────────────────────────────────
        testList "GrantPolicy.ofToken is fail-closed" [
            test "every known token round-trips through toToken" {
                let policies = [
                    GrantPolicy.AdminDiscretion
                    GrantPolicy.RequiresAcknowledgement
                    GrantPolicy.RequiresSubjectConsent
                    GrantPolicy.RequiresCounterpartyApproval(PartyRef.create "acme-dpo")
                ]

                for policy in policies do
                    Expect.equal
                        (GrantPolicy.ofToken (GrantPolicy.toToken policy))
                        policy
                        $"round-trip for {GrantPolicy.toToken policy}"
            }

            test "no unrecognised token reads back as AdminDiscretion" {
                // The table is the point: these are the shapes a corrupt
                // blob, a truncated write, or a NEWER deployment's policy
                // arm actually produce, and every one of them must land
                // somewhere stricter than "an admin may do as they like".
                let mangled = [
                    ""
                    "   "
                    null
                    "AdminDiscretion" // the F# case name, not the wire token
                    "admin_discretion"
                    "requires-something-we-have-not-shipped"
                    "requires-acknowledgement-v2"
                    "0"
                    "true"
                ]

                for token in mangled do
                    let parsed = GrantPolicy.ofToken token

                    Expect.notEqual parsed GrantPolicy.AdminDiscretion $"'{token}' must not parse as AdminDiscretion"

                    Expect.equal
                        parsed
                        GrantPolicy.strictestConstructible
                        $"'{token}' lands on the strictest arm constructible from a token carrying no party"
            }

            test "a counterparty arm with no party keeps the arm and refuses everything" {
                // Distinguished from a wholly-unknown token on purpose:
                // the ARM is recognised here, so downgrading it would be
                // a loosening on the strength of corruption.
                let parsed = GrantPolicy.ofToken "requires-counterparty-approval"

                match parsed with
                | GrantPolicy.RequiresCounterpartyApproval party ->
                    Expect.isTrue (PartyRef.isEmpty party) "party is unnameable"
                | other -> failtestf "expected the counterparty arm, got %A" other

                Expect.isFalse
                    (GrantPolicy.isGrantLive parsed (Some(activeRecord parsed "forged")))
                    "an unnameable counterparty admits nothing, even with an Active record"
            }

            test "GrantState.ofToken never reads an unknown state as Active" {
                for token in [ ""; null; "ACTIVE!"; "approved"; "live" ] do
                    Expect.equal (GrantState.ofToken token) GrantState.PendingConsent $"'{token}' is inert, not Active"

                Expect.equal (GrantState.ofToken "active") GrantState.Active "the real token still works"
            }
        ]

        // ── Narrowing-only composition ───────────────────────────────
        testList "narrowing-only composition (D15)" [
            test "tighten admits a strictly stricter policy and rejects a looser one" {
                Expect.equal
                    (GrantPolicy.tighten "M" GrantPolicy.RequiresAcknowledgement GrantPolicy.RequiresSubjectConsent)
                    (Ok GrantPolicy.RequiresSubjectConsent)
                    "acknowledgement → subject consent narrows"

                match
                    GrantPolicy.tighten "M" GrantPolicy.RequiresSubjectConsent GrantPolicy.RequiresAcknowledgement
                with
                | Error(GrantRefusal.PolicyLoosening("M", _, _)) -> ()
                | other -> failtestf "expected a PolicyLoosening refusal, got %A" other

                match GrantPolicy.tighten "M" GrantPolicy.RequiresSubjectConsent GrantPolicy.AdminDiscretion with
                | Error(GrantRefusal.PolicyLoosening _) -> ()
                | other -> failtestf "expected a PolicyLoosening refusal for AdminDiscretion, got %A" other
            }

            test "two counterparty arms naming different parties are incomparable, not equal" {
                let a = PartyRef.create "acme-dpo"
                let b = PartyRef.create "globex-dpo"

                match
                    GrantPolicy.tighten
                        "M"
                        (GrantPolicy.RequiresCounterpartyApproval a)
                        (GrantPolicy.RequiresCounterpartyApproval b)
                with
                | Error(GrantRefusal.ConflictingCounterparty("M", declared, attempted)) ->
                    Expect.equal declared a "names the declared party"
                    Expect.equal attempted b "names the attempted party"
                | other -> failtestf "expected a ConflictingCounterparty refusal, got %A" other

                // The same party is a legal (idempotent) re-declaration.
                Expect.equal
                    (GrantPolicy.tighten
                        "M"
                        (GrantPolicy.RequiresCounterpartyApproval a)
                        (GrantPolicy.RequiresCounterpartyApproval(PartyRef.create " acme-dpo ")))
                    (Ok(GrantPolicy.RequiresCounterpartyApproval a))
                    "the same party re-declared, whitespace and all"
            }

            test "ServerModule.withGrantPolicy refuses a loosening at compose time" {
                let tightened =
                    ServerModule.create "Partner"
                    |> ServerModule.withGrantPolicy GrantPolicy.RequiresSubjectConsent

                Expect.equal tightened.GrantPolicy GrantPolicy.RequiresSubjectConsent "declaration took"

                Expect.throws
                    (fun () ->
                        tightened
                        |> ServerModule.withGrantPolicy GrantPolicy.RequiresAcknowledgement
                        |> ignore)
                    "loosening a declared policy fails at composition, not at first request"
            }

            test "a module that declares nothing carries AdminDiscretion" {
                Expect.equal (ServerModule.create "Plain").GrantPolicy GrantPolicy.AdminDiscretion "the pre-551 default"
            }

            test "ModuleSurface classifies GrantPolicy, and reports it only when declared" {
                // The `ServerModule` drift guard fails on any registration
                // field the descriptor does not classify — deliberately, so
                // a new field gets classified by a decision rather than by
                // omission. `GrantPolicy` is `Provides` on the
                // `DefaultSurfaceRequirement` precedent (a module-declared
                // access posture a composition reads off the registration),
                // and its ENTRY is conditional on the `BindingStamp`
                // precedent, because `AdminDiscretion` is this field's
                // "declares nothing".
                let plain = ServerModule.create "Plain"
                let plainSurface = ModuleSurface.describe plain

                match
                    plainSurface.Coverage
                    |> List.filter (fun c -> c.Origin = "server" && c.Field = nameof plain.GrantPolicy)
                with
                | [ c ] -> Expect.equal c.Facet ProvidesFacet "classified as a declaration the module offers"
                | other -> failtestf "expected exactly one GrantPolicy coverage row, got %A" other

                Expect.isEmpty plainSurface.Unclassified "the field is classified, so nothing drifts"

                Expect.isEmpty
                    (plainSurface.Provides |> List.filter (fun e -> e.Kind = "grant-policy"))
                    "an AdminDiscretion module reports no grant-policy entry — byte-identical to pre-551"

                let declared =
                    ServerModule.create "Partner"
                    |> ServerModule.withGrantPolicy (
                        GrantPolicy.RequiresCounterpartyApproval(PartyRef.create "acme-dpo")
                    )

                match
                    (ModuleSurface.describe declared).Provides
                    |> List.filter (fun e -> e.Kind = "grant-policy")
                with
                | [ e ] ->
                    Expect.equal e.Field (nameof declared.GrantPolicy) "attributed to the registration field"

                    Expect.equal
                        e.Key
                        "requires-counterparty-approval:acme-dpo"
                        "the wire token is the declared identity, party and all"
                | other -> failtestf "expected exactly one grant-policy entry, got %A" other
            }
        ]

        // ── 2. The write guard, per arm ──────────────────────────────
        testList "grant-write guard (551.C)" [
            testCaseAsync "RequiresAcknowledgement refuses a grant with no confirmation, then no justification"
            <| async {
                let registry = registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ]
                let store = freshStore ()

                let request: GrantPolicyGuard.ModuleGrantRequest = {
                    TeamId = "t1"
                    ActorId = "admin"
                    SubjectId = "alice"
                    ModuleName = "Partner"
                    Permissions = [ Read ]
                    Evidence = GrantPolicyGuard.GrantEvidence.none
                }

                match! GrantPolicyGuard.PermissionGrants.grantModuleAccess store registry request with
                | Error(GrantRefusal.AcknowledgementRequired("Partner", policy)) ->
                    Expect.equal policy GrantPolicy.RequiresAcknowledgement "the refusal names the policy"
                | other -> failtestf "expected AcknowledgementRequired, got %A" other

                match!
                    GrantPolicyGuard.PermissionGrants.grantModuleAccess store registry {
                        request with
                            Evidence = {
                                Acknowledged = true
                                Justification = "   "
                            }
                    }
                with
                | Error(GrantRefusal.JustificationRequired("Partner", _)) -> ()
                | other -> failtestf "expected JustificationRequired, got %A" other

                // Nothing was persisted by either refusal.
                let! doc = store.GetTeamPermissions "t1"
                Expect.isEmpty doc.Members "a refused grant writes nothing"
                Expect.isEmpty doc.Grants "a refused grant records nothing"
            }

            testCaseAsync "RequiresAcknowledgement admits an acknowledged, justified grant and it is live"
            <| async {
                let registry = registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ]
                let store = freshStore ()

                let! outcome =
                    GrantPolicyGuard.PermissionGrants.grantModuleAccess store registry {
                        TeamId = "t1"
                        ActorId = "admin"
                        SubjectId = "alice"
                        ModuleName = "Partner"
                        Permissions = [ Read; Write ]
                        Evidence = GrantPolicyGuard.GrantEvidence.acknowledged "pilot onboarding, ticket OPS-14"
                    }

                Expect.equal outcome (Ok GrantWriteOutcome.Granted) "granted outright — no third party is involved"

                let! doc = store.GetTeamPermissions "t1"
                let record = TeamPermissions.grantsFor "alice" doc |> Map.tryFind "Partner"

                match record with
                | Some r ->
                    Expect.equal r.State GrantState.Active "live immediately"
                    Expect.equal r.SatisfiedPolicy GrantPolicy.RequiresAcknowledgement "records what was satisfied"
                    Expect.equal r.Justification "pilot onboarding, ticket OPS-14" "justification persisted"
                | None -> failtest "expected a grant record to be persisted"

                Expect.isTrue
                    (GrantPolicy.isGrantLive GrantPolicy.RequiresAcknowledgement record)
                    "and it is live at dispatch"
            }

            testCaseAsync "RequiresSubjectConsent records the grant PENDING and it is inert until accepted"
            <| async {
                let registry = registryOf [ "Partner", GrantPolicy.RequiresSubjectConsent ]
                let store = freshStore ()

                let! outcome =
                    GrantPolicyGuard.PermissionGrants.grantModuleAccess store registry {
                        TeamId = "t1"
                        ActorId = "admin"
                        SubjectId = "alice"
                        ModuleName = "Partner"
                        Permissions = [ Read ]
                        Evidence = GrantPolicyGuard.GrantEvidence.acknowledged "requested by alice"
                    }

                Expect.equal
                    outcome
                    (Ok(GrantWriteOutcome.RecordedPendingConsent "alice"))
                    "the admin cannot consent on the subject's behalf"

                let! pending = store.GetTeamPermissions "t1"

                let pendingRecord =
                    TeamPermissions.grantsFor "alice" pending |> Map.tryFind "Partner"

                Expect.isFalse
                    (GrantPolicy.isGrantLive GrantPolicy.RequiresSubjectConsent pendingRecord)
                    "a pending grant confers nothing"

                // The permission entry EXISTS meanwhile — which is
                // exactly why liveness has to be a separate question from
                // `canAccessModule`.
                let! effective = store.GetEffectivePermissions("alice", "t1")
                Expect.isTrue (effective.ContainsKey "Partner") "the permission entry is written; it is just inert"

                match! GrantPolicyGuard.PermissionGrants.acceptGrant store "t1" "alice" "Partner" with
                | Error e -> failtestf "acceptGrant failed: %s" e
                | Ok() -> ()

                let! accepted = store.GetTeamPermissions "t1"

                let acceptedRecord =
                    TeamPermissions.grantsFor "alice" accepted |> Map.tryFind "Partner"

                Expect.isTrue
                    (GrantPolicy.isGrantLive GrantPolicy.RequiresSubjectConsent acceptedRecord)
                    "live once the subject accepts"

                Expect.equal
                    (acceptedRecord |> Option.bind _.ConsentedBy)
                    (Some "alice")
                    "and the acceptance is attributed"
            }

            testCaseAsync "acceptGrant refuses when there is nothing to accept"
            <| async {
                // Otherwise a subject could manufacture their own
                // authority by consenting to a grant nobody made.
                let store = freshStore ()

                match! GrantPolicyGuard.PermissionGrants.acceptGrant store "t1" "mallory" "Partner" with
                | Error msg -> Expect.stringContains msg "GRANT-POLICY-NO-PENDING-GRANT" "typed, greppable refusal"
                | Ok() -> failtest "consenting to a non-existent grant must refuse"
            }

            testCaseAsync "RequiresCounterpartyApproval refuses every grant until Phase 552"
            <| async {
                let party = PartyRef.create "acme-dpo"

                let registry =
                    registryOf [ "Partner", GrantPolicy.RequiresCounterpartyApproval party ]

                let store = freshStore ()

                match!
                    GrantPolicyGuard.PermissionGrants.grantModuleAccess store registry {
                        TeamId = "t1"
                        ActorId = "admin"
                        SubjectId = "alice"
                        ModuleName = "Partner"
                        Permissions = [ Read ]
                        Evidence = GrantPolicyGuard.GrantEvidence.acknowledged "counterparty said yes over email"
                    }
                with
                | Error(GrantRefusal.CounterpartyApprovalUnavailable("Partner", named)) ->
                    Expect.equal named party "the refusal names the party whose approval is missing"
                | other -> failtestf "expected CounterpartyApprovalUnavailable, got %A" other

                let! doc = store.GetTeamPermissions "t1"
                Expect.isEmpty doc.Members "nothing persisted — an emailed approval is not an artifact"
            }

            testCaseAsync "revocation is always admitted, whatever the policy"
            <| async {
                // A policy constrains the CREATION of authority, never
                // its removal. A policy that could block a revocation
                // would be a liability, not a control.
                let registry =
                    registryOf [ "Partner", GrantPolicy.RequiresCounterpartyApproval(PartyRef.create "acme") ]

                let store = freshStore ()

                let! outcome =
                    GrantPolicyGuard.PermissionGrants.grantModuleAccess store registry {
                        TeamId = "t1"
                        ActorId = "admin"
                        SubjectId = "alice"
                        ModuleName = "Partner"
                        Permissions = []
                        Evidence = GrantPolicyGuard.GrantEvidence.none
                    }

                Expect.equal outcome (Ok GrantWriteOutcome.Granted) "revocation needs no precondition"
            }

            testCaseAsync "the legacy SetMemberPermissions path cannot write a policy-bearing grant"
            <| async {
                // Its signature has nowhere to put acknowledgement or
                // justification, so it refuses by construction rather
                // than writing an unevidenced grant.
                let registry = registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ]
                let audit = RecordingAuditLog()

                let store =
                    GrantPolicyGuard.GrantPolicyPermissionStore(freshStore (), registry, audit, "admin")
                    :> IPermissionStore

                match! store.SetMemberPermissions("t1", "alice", "Partner", [ Read ]) with
                | Error msg -> Expect.stringContains msg "GRANT-POLICY-ACK-REQUIRED" "typed, greppable refusal"
                | Ok() -> failtest "an unevidenced grant on a policy-bearing module must refuse"

                // An AdminDiscretion module through the same store is
                // untouched — this is the GP 11 half of the decorator.
                match! store.SetMemberPermissions("t1", "alice", "Ordinary", [ Read ]) with
                | Error e -> failtestf "an AdminDiscretion grant must pass unchanged: %s" e
                | Ok() -> ()

                // And a revocation on the policy-bearing module passes.
                match! store.SetMemberPermissions("t1", "alice", "Partner", []) with
                | Error e -> failtestf "revocation must pass: %s" e
                | Ok() -> ()
            }

            testCaseAsync "a policy-bearing module cannot be handed out through team DEFAULTS"
            <| async {
                // A default applies to every member who lacks an explicit
                // entry, so there is no subject to acknowledge or consent
                // — refused rather than silently ineffective.
                let registry = registryOf [ "Partner", GrantPolicy.RequiresSubjectConsent ]

                let store =
                    GrantPolicyGuard.GrantPolicyPermissionStore(freshStore (), registry) :> IPermissionStore

                match! store.SetTeamDefaults("t1", Map.ofList [ "Partner", [ Read ] ]) with
                | Error msg -> Expect.stringContains msg "GRANT-POLICY-UNBACKED-GRANT" "refused, naming the module"
                | Ok() -> failtest "a policy-bearing module must not be grantable by default"

                match! store.SetTeamDefaults("t1", Map.ofList [ "Ordinary", [ Read ] ]) with
                | Error e -> failtestf "an AdminDiscretion default must pass unchanged: %s" e
                | Ok() -> ()
            }

            testCaseAsync "the write guard emits GrantPolicyRefused"
            <| async {
                let registry = registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ]
                let audit = RecordingAuditLog()

                let store =
                    GrantPolicyGuard.GrantPolicyPermissionStore(freshStore (), registry, audit, "admin-7")
                    :> IPermissionStore

                let! _ = store.SetMemberPermissions("t1", "alice", "Partner", [ Read ])

                // The decorator's emission is fire-and-forget per the
                // IAuditLog contract, so poll briefly rather than assume
                // scheduling order — a flake here would be a false red,
                // and a permanently-absent row still fails.
                let mutable waited = 0

                while List.isEmpty audit.Recorded && waited < 100 do
                    do! Async.Sleep 20
                    waited <- waited + 1

                match audit.Recorded with
                | [ scope, GrantPolicyRefused payload ] ->
                    Expect.equal scope "team-t1" "recorded under the team scope"
                    Expect.equal payload.ActorId "admin-7" "attributes the acting administrator"
                    Expect.equal payload.SubjectId "alice" "names the subject"
                    Expect.equal payload.ModuleName "Partner" "names the module"
                    Expect.equal payload.DeclaredPolicy "requires-acknowledgement" "carries the wire token"
                    Expect.equal payload.RefusalCode "acknowledgement-required" "carries the stable discriminator"
                | other -> failtestf "expected exactly one GrantPolicyRefused row, got %A" other
            }
        ]

        // ── 3. Dispatch enforcement ──────────────────────────────────
        testList "dispatch enforcement (551.D)" [
            test "a grant row injected with no record is refused at dispatch, and audited" {
                // The acceptance case. The permission entry is present
                // (so `canAccessModule` says yes) and no grant record
                // backs it — the exact shape a row written straight into
                // the store, restored from a backup, or produced by a
                // migration takes. Write-path-only enforcement would
                // admit it.
                let registry =
                    registryOf [
                        "Partner", GrantPolicy.RequiresCounterpartyApproval(PartyRef.create "acme-dpo")
                    ]

                let audit = RecordingAuditLog()

                let outcome =
                    GrantPolicyGuard.guardDispatch registry Map.empty (Some audit) runNow "team-t1" "mallory" "Partner"

                match outcome with
                | Error payload ->
                    Expect.equal payload.UserId "mallory" "names the caller"
                    Expect.equal payload.ModuleName "Partner" "names the module"

                    Expect.equal
                        payload.DeclaredPolicy
                        "requires-counterparty-approval:acme-dpo"
                        "carries the declared policy verbatim"

                    Expect.equal payload.InertReason "counterparty-approval-unavailable" "and why the grant is inert"
                | Ok() -> failtest "an unconsented grant row must be refused at dispatch"

                match audit.Recorded with
                | [ scope, UnconsentedGrantRefused payload ] ->
                    Expect.equal scope "team-t1" "recorded under the team scope"
                    Expect.equal payload.ModuleName "Partner" "the row names the module"
                | other -> failtestf "expected exactly one UnconsentedGrantRefused row, got %A" other
            }

            test "an Active record forged for a counterparty module is still refused" {
                // No path can legitimately have written one while Phase
                // 552 is unshipped, so its presence is evidence of
                // injection rather than of consent.
                let policy = GrantPolicy.RequiresCounterpartyApproval(PartyRef.create "acme-dpo")
                let registry = registryOf [ "Partner", policy ]
                let grants = Map.ofList [ "Partner", activeRecord policy "forged" ]

                match GrantPolicyGuard.guardDispatch registry grants None runNow "team-t1" "mallory" "Partner" with
                | Error payload ->
                    Expect.equal
                        payload.InertReason
                        "counterparty-approval-unavailable"
                        "refused on the arm, not the record"
                | Ok() -> failtest "a forged Active record must not confer authority"
            }

            test "a pending grant is inert and a consented one is live, distinguishably" {
                let registry = registryOf [ "Partner", GrantPolicy.RequiresSubjectConsent ]

                let pending =
                    Map.ofList [
                        "Partner",
                        {
                            State = GrantState.PendingConsent
                            SatisfiedPolicy = GrantPolicy.RequiresSubjectConsent
                            Justification = "requested"
                            ConsentedBy = None
                        }
                    ]

                match GrantPolicyGuard.guardDispatch registry pending None runNow "team-t1" "alice" "Partner" with
                | Error payload ->
                    // The reason is what lets an operator separate an
                    // ordinary pending grant from a suspected injection.
                    Expect.equal payload.InertReason "awaiting-subject-consent" "distinguishable from no-grant-record"
                | Ok() -> failtest "a pending grant must be inert"

                let consented =
                    Map.ofList [ "Partner", activeRecord GrantPolicy.RequiresSubjectConsent "requested" ]

                Expect.equal
                    (GrantPolicyGuard.guardDispatch registry consented None runNow "team-t1" "alice" "Partner")
                    (Ok())
                    "a consented grant is live"
            }

            test "no-grant-record is reported distinctly from awaiting-consent" {
                let registry = registryOf [ "Partner", GrantPolicy.RequiresSubjectConsent ]

                match GrantPolicyGuard.guardDispatch registry Map.empty None runNow "team-t1" "mallory" "Partner" with
                | Error payload -> Expect.equal payload.InertReason "no-grant-record" "the injected-row signature"
                | Ok() -> failtest "an unbacked entry must be refused"
            }

            test "a module that TIGHTENS its policy invalidates grants written under the looser one" {
                // Evidence gathered under acknowledgement does not
                // satisfy a later demand for subject consent —
                // grandfathering would make tightening a no-op for
                // exactly the grants an operator tightened because of.
                let registry = registryOf [ "Partner", GrantPolicy.RequiresSubjectConsent ]

                let stale =
                    Map.ofList [ "Partner", activeRecord GrantPolicy.RequiresAcknowledgement "old" ]

                match GrantPolicyGuard.guardDispatch registry stale None runNow "team-t1" "alice" "Partner" with
                | Error payload ->
                    Expect.equal payload.InertReason "evidence-below-declared-policy" "named as stale evidence"
                | Ok() -> failtest "evidence below the declared policy must not satisfy it"

                // The converse is admitted: evidence STRONGER than what
                // is demanded now still satisfies it.
                let strongerRegistry = registryOf [ "Partner", GrantPolicy.RequiresAcknowledgement ]

                let stronger =
                    Map.ofList [ "Partner", activeRecord GrantPolicy.RequiresSubjectConsent "new" ]

                Expect.equal
                    (GrantPolicyGuard.guardDispatch strongerRegistry stronger None runNow "team-t1" "alice" "Partner")
                    (Ok())
                    "stronger evidence satisfies a weaker demand"
            }

            test "a module absent from the registry is AdminDiscretion and passes with no record" {
                let registry = registryOf [ "Partner", GrantPolicy.RequiresSubjectConsent ]

                Expect.equal
                    (GrantPolicyGuard.guardDispatch registry Map.empty None runNow "team-t1" "alice" "Ordinary")
                    (Ok())
                    "an undeclared module is unaffected by a sibling's policy"
            }
        ]

        // ── 4. GP 11 / GP 13 — byte-parity with today ────────────────
        testList "AdminDiscretion everywhere is byte-parity with today (GP 11 / GP 13)" [
            test "an all-default deployment composes an EMPTY registry" {
                // This is the whole of the zero-cost story: an empty
                // registry is never registered, so the store is not
                // decorated, the middleware performs no extra read, and
                // the dispatch gate short-circuits.
                let registry =
                    registryOf [ "A", GrantPolicy.AdminDiscretion; "B", GrantPolicy.AdminDiscretion ]

                Expect.isTrue
                    (GrantPolicyGuard.ModuleGrantPolicyRegistry.isEmpty registry)
                    "declaring the default declares nothing"
            }

            test "an empty registry short-circuits before grants or audit are touched" {
                let audit = RecordingAuditLog()

                let outcome =
                    GrantPolicyGuard.guardDispatch
                        GrantPolicyGuard.ModuleGrantPolicyRegistry.empty
                        Map.empty
                        (Some audit)
                        (fun _ -> failtest "an all-default deployment must never schedule an audit emission")
                        "team-t1"
                        "alice"
                        "Anything"

                Expect.equal outcome (Ok()) "every module passes"
                Expect.isEmpty audit.Recorded "and nothing is recorded"
            }

            testCaseAsync "a document written with no policy round-trips with an empty grant map"
            <| async {
                let store = freshStore ()

                match! store.SetMemberPermissions("t1", "alice", "Ordinary", [ Read; Write ]) with
                | Error e -> failtestf "SetMemberPermissions failed: %s" e
                | Ok() -> ()

                let! doc = store.GetTeamPermissions "t1"
                Expect.isEmpty doc.Grants "no records are minted for an AdminDiscretion module"

                let! effective = store.GetEffectivePermissions("alice", "t1")
                Expect.equal effective (Map.ofList [ "Ordinary", [ Read; Write ] ]) "and the merge is unchanged"
            }

            testCaseAsync "a pre-551 document with no 'grants' property reads back with an empty grant map"
            <| async {
                // The upgrade case, exercised through the real store
                // rather than the (private) parser: every persisted
                // document predating this phase lacks the property
                // entirely and must read back as "no policy applies"
                // rather than as anything stricter — otherwise an upgrade
                // would lock every team out of every module.
                let dir =
                    Path.Combine(Path.GetTempPath(), "toolup-grantpolicy-legacy-" + Guid.NewGuid().ToString("N"))

                Directory.CreateDirectory dir |> ignore
                let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

                let legacy =
                    """{"defaults":{"M1":["Read"]},"members":{"alice":{"M1":["Admin"]}},"hidden":["M2"]}"""

                match! storage.Upload("_platform", "permissions/t1.json", Text.Encoding.UTF8.GetBytes legacy) with
                | Error e -> failtestf "could not seed the legacy document: %s" e
                | Ok _ -> ()

                let store = PermissionStore(storage) :> IPermissionStore
                let! doc = store.GetTeamPermissions "t1"

                Expect.isEmpty doc.Grants "absent ⇒ empty ⇒ every module behaves as AdminDiscretion"
                Expect.equal (Map.count doc.Members) 1 "and the rest of the document is unaffected"

                Expect.equal
                    doc.Exposure
                    (Map.ofList [ "M2", ModuleExposure.Hidden ])
                    "including the legacy exposure fallback"
            }
        ]
    ]