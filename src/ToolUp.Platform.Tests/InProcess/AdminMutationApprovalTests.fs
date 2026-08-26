// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AdminMutationApprovalTests

// ─── Phase 555 — dual control for sensitive admin mutations ──────────
//
// The two-person rule over the `IPermissionStore` write path. What this
// pack is here to prove, matching 555.D:
//
//   1. **A gated write does not apply.** The grantee's effective
//      permissions are unchanged between the proposal and the approval —
//      asserted by reading them, not by inspecting the queue.
//   2. **Self-approval is structurally refused**, including on a
//      capitalisation or a whitespace variant of the proposer's id, and
//      the pending record SURVIVES the refused attempt rather than being
//      consumed by it.
//   3. **A lapsed proposal is refused and swept**, with the expiry
//      recorded — so a proposal that ends is visible as having ended
//      rather than merely stopping.
//   4. **`SingleAdmin` is byte-parity with today** (GP 11 / GP 13), and
//      so is every write dual control does not gate — asserted on the
//      persisted BYTES of the permission document, not on a return value.
//   5. **Phase 551 pre-empts queueing.** With the composed chain in its
//      shipped order, a grant the module's declared `GrantPolicy` would
//      never admit is refused outright and the approval queue stays
//      EMPTY — a write that can never be approved is not parked.
//
// **Non-vacuity.** The ceremony cases run `propose` / `approve` /
// `reject` directly with a SYNCHRONOUS scheduler, so one call yields both
// the decision and the emitted audit row: a ceremony that refused without
// auditing, or audited without refusing, fails here rather than passing
// half the assertion. The stores are real — a blob-backed approval queue
// over a private temp dir and a real `PermissionStore` — so the captured
// `AdminMutation` round-trips through actual serialisation and a field
// silently lost on the way to disk fails a test rather than passing one.

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.AdminMutationApproval

// ─── Fixtures ────────────────────────────────────────────────────────

let private Read = ModulePermission.Read
let private Write = ModulePermission.Write

let private tempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-dualcontrol-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore
    dir

let private storageAt (dir: string) =
    LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

/// A real blob-backed permission store plus the approval queue beside
/// it, over one private temp dir — the same substrate a deployment runs.
let private freshFixture () =
    let dir = tempDir ()
    let storage = storageAt dir

    let permissions = PermissionStore(storage) :> IPermissionStore

    let approvals =
        BlobAdminMutationApprovalStore(storage) :> IAdminMutationApprovalStore

    dir, storage, permissions, approvals

/// Accumulates every recorded event so a test can assert the exact row.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Recorded = List.ofSeq recorded

    member this.EventNames = this.Recorded |> List.map (snd >> AuditEvent.eventTypeName)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// The synchronous scheduler the ceremony cases pass, so decision and
/// emission are observed together.
let private runNow (work: Async<unit>) = Async.RunSynchronously work

let private t0 = DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)

let private settingsAll = {
    Scope = AdminMutationScope.AllPermissionWrites
    PendingTtlMinutes = 60
}

let private settingsPolicyOnly = {
    Scope = AdminMutationScope.PolicyBearingModulesOnly
    PendingTtlMinutes = 60
}

let private nothingIsPolicyBearing (_: string) = false

/// Read the persisted permission document's raw bytes — the byte-parity
/// assertions compare these, not a decoded value, because "unchanged" in
/// GP 11 means unchanged on disk.
let private documentBytes (storage: IBlobStorage) (teamId: string) =
    async {
        match! storage.Download("_platform", $"permissions/{teamId}.json") with
        | Ok bytes -> return Some bytes
        | Error _ -> return None
    }
    |> Async.RunSynchronously

let private effectiveFor (store: IPermissionStore) userId teamId =
    store.GetEffectivePermissions(userId, teamId) |> Async.RunSynchronously

let private run x = Async.RunSynchronously x

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 555 — dual control for sensitive admin mutations" [

        // ── 1. The ceremony ──────────────────────────────────────────
        testList "the two-person ceremony" [

            test "a gated grant is inert until a second administrator approves it" {
                let _, _, permissions, approvals = freshFixture ()

                let gate =
                    DualControlPermissionStore(
                        permissions,
                        settingsAll,
                        approvals,
                        nothingIsPolicyBearing,
                        (fun () -> Some "admin-a"),
                        (fun () -> t0)
                    )

                let store = gate :> IPermissionStore

                // Admin A grants. The write must NOT apply.
                let written =
                    store.SetMemberPermissions("acme", "bob", "reports", [ Read; Write ]) |> run

                match written with
                | Ok() -> failtest "a gated write must not report success — it did not apply"
                | Error message ->
                    Expect.stringContains
                        message
                        "DUAL-CONTROL-PENDING-APPROVAL"
                        "the refusal names the dual-control queue"

                Expect.isEmpty
                    (effectiveFor store "bob" "acme")
                    "the grantee's effective permissions must be unchanged while the proposal is pending"

                // Exactly one proposal is queued.
                let pending =
                    match listPending approvals None runNow t0 "acme" |> run with
                    | Ok ps -> ps
                    | Error e -> failtestf "listing the queue failed: %s" e

                Expect.hasLength pending 1 "one proposal queued"
                Expect.equal pending[0].ProposedBy "admin-a" "the proposer is recorded"

                // Admin B approves — and only now does it apply.
                match gate.Approve("acme", pending[0].RequestId, "admin-b") |> run with
                | Ok(AdminMutationDecision.Applied _) -> ()
                | other -> failtestf "a distinct approver must apply the mutation; got %A" other

                Expect.equal
                    (effectiveFor store "bob" "acme" |> Map.tryFind "reports")
                    (Some [ Read; Write ])
                    "the approved grant is live"

                match listPending approvals None runNow t0 "acme" |> run with
                | Ok [] -> ()
                | Ok remaining -> failtestf "the decided proposal must leave the queue; %d remain" remaining.Length
                | Error e -> failtestf "listing the queue failed: %s" e
            }

            test "propose emits AdminMutationProposed naming the request, proposer and fingerprint" {
                let _, _, _, approvals = freshFixture ()
                let audit = RecordingAuditLog()

                let mutation = AdminMutation.SetMemberPermissions("bob", "reports", [ Read ])

                let queued =
                    match
                        propose approvals settingsAll (Some(audit :> IAuditLog)) runNow t0 "acme" "admin-a" mutation
                        |> run
                    with
                    | Ok q -> q
                    | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                match audit.Recorded with
                | [ scope, AdminMutationProposed p ] ->
                    Expect.equal scope "team-acme" "recorded under the team scope"
                    Expect.equal p.RequestId queued.RequestId "the row names the queued request"
                    Expect.equal p.ProposerId "admin-a" "the row names the proposer"
                    Expect.equal p.MutationKind "member-permissions" "the row carries the mutation kind token"
                    Expect.equal p.Fingerprint (fingerprint mutation) "the row carries the payload fingerprint"
                    Expect.equal p.ExpiresAtUtc (t0.AddMinutes 60.0) "the row carries the lapse instant"
                | other -> failtestf "expected exactly one AdminMutationProposed row; got %A" other
            }

            test "the proposer may withdraw their own proposal, and nothing applies" {
                let _, _, permissions, approvals = freshFixture ()
                let audit = RecordingAuditLog()

                let mutation = AdminMutation.SetMemberPermissions("bob", "reports", [ Read ])

                let queued =
                    match propose approvals settingsAll None runNow t0 "acme" "admin-a" mutation |> run with
                    | Ok q -> q
                    | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                // Withdrawal narrows nothing and creates nothing, so it
                // does NOT require a second person — see the asymmetry
                // note in AdminMutationApproval.fs.
                match
                    reject
                        approvals
                        (Some(audit :> IAuditLog))
                        runNow
                        t0
                        "acme"
                        queued.RequestId
                        "admin-a"
                        "mistyped the user id"
                    |> run
                with
                | Ok(AdminMutationDecision.Rejected _) -> ()
                | other -> failtestf "a proposer must be able to withdraw; got %A" other

                Expect.isEmpty (effectiveFor permissions "bob" "acme") "a rejected proposal applies nothing"

                match audit.Recorded |> List.map (snd >> AuditEvent.eventTypeName) with
                | [ "AdminMutationRejected" ] -> ()
                | names -> failtestf "expected exactly one AdminMutationRejected row; got %A" names
            }
        ]

        // ── 2. Self-approval ─────────────────────────────────────────
        testList "self-approval is refused by construction" [

            test "the proposer cannot approve their own proposal, and the record survives" {
                let _, _, permissions, approvals = freshFixture ()
                let audit = RecordingAuditLog()

                let mutation = AdminMutation.SetMemberPermissions("bob", "reports", [ Read; Write ])

                let queued =
                    match propose approvals settingsAll None runNow t0 "acme" "admin-a" mutation |> run with
                    | Ok q -> q
                    | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                let apply _ = async { return Ok() }

                match
                    approve approvals apply (Some(audit :> IAuditLog)) runNow t0 "acme" queued.RequestId "admin-a"
                    |> run
                with
                | Error(AdminMutationRefusal.SelfApprovalRefused(id, actor)) ->
                    Expect.equal id queued.RequestId "the refusal names the request"
                    Expect.equal actor "admin-a" "the refusal names the attempting actor"
                | other -> failtestf "self-approval must be refused; got %A" other

                Expect.isEmpty (effectiveFor permissions "bob" "acme") "a refused self-approval applies nothing"

                // The record must SURVIVE: a refused attempt is not a
                // decision, and consuming the proposal would let a
                // proposer destroy their own pending grant by attempting
                // to approve it.
                match approvals.TryGet("acme", queued.RequestId) |> run with
                | Ok(Some _) -> ()
                | other -> failtestf "the pending record must survive a refused approval; got %A" other

                match audit.Recorded with
                | [ _, AdminMutationApprovalRefused p ] ->
                    Expect.equal p.RefusalCode "self-approval-refused" "the row carries the dashboarded code"
                    Expect.equal p.ProposerId "admin-a" "the row names the proposer"
                    Expect.equal p.AttemptedApproverId "admin-a" "the row names the attempted approver"
                | other -> failtestf "expected exactly one AdminMutationApprovalRefused row; got %A" other
            }

            test "distinctness is not defeated by capitalisation or surrounding whitespace" {
                // A comparison that treated "Admin-A" as a different
                // person from "admin-a" would let a self-approval through
                // on a capitalisation — which is precisely the class of
                // near-miss this control exists to catch.
                for variant in [ "ADMIN-A"; "Admin-A"; "  admin-a  " ] do
                    let _, _, _, approvals = freshFixture ()

                    let queued =
                        match
                            propose
                                approvals
                                settingsAll
                                None
                                runNow
                                t0
                                "acme"
                                "admin-a"
                                (AdminMutation.SetMemberPermissions("bob", "reports", [ Read ]))
                            |> run
                        with
                        | Ok q -> q
                        | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                    let apply _ = async { return Ok() }

                    match approve approvals apply None runNow t0 "acme" queued.RequestId variant |> run with
                    | Error(AdminMutationRefusal.SelfApprovalRefused _) -> ()
                    | other -> failtestf "'%s' must not be able to approve admin-a's proposal; got %A" variant other

                Expect.isTrue
                    (isSameAdministrator "admin-a" " Admin-A ")
                    "the sameness predicate is trimmed and case-insensitive"

                Expect.isFalse (isSameAdministrator "admin-a" "admin-b") "genuinely distinct ids stay distinct"
            }

            test "an unattributable gated write is refused, never parked" {
                let _, _, permissions, approvals = freshFixture ()

                let gate =
                    DualControlPermissionStore(
                        permissions,
                        settingsAll,
                        approvals,
                        nothingIsPolicyBearing,
                        // No resolvable acting administrator.
                        (fun () -> None),
                        (fun () -> t0)
                    )

                match
                    (gate :> IPermissionStore).SetMemberPermissions("acme", "bob", "reports", [ Read ])
                    |> run
                with
                | Error message ->
                    Expect.stringContains message "DUAL-CONTROL-UNATTRIBUTED" "the refusal names the missing actor"
                | Ok() -> failtest "an unattributable gated write must not apply"

                match listPending approvals None runNow t0 "acme" |> run with
                | Ok [] -> ()
                | Ok queued -> failtestf "nothing may be queued for an unknown proposer; %d were" queued.Length
                | Error e -> failtestf "listing the queue failed: %s" e

                Expect.isEmpty (effectiveFor permissions "bob" "acme") "nothing applied"
            }
        ]

        // ── 3. Expiry ────────────────────────────────────────────────
        testList "a proposal that lapses is refused and swept" [

            test "approval past the TTL is refused, the record removed, and the expiry recorded" {
                let _, _, permissions, approvals = freshFixture ()
                let audit = RecordingAuditLog()

                let queued =
                    match
                        propose
                            approvals
                            settingsAll
                            None
                            runNow
                            t0
                            "acme"
                            "admin-a"
                            (AdminMutation.SetMemberPermissions("bob", "reports", [ Read ]))
                        |> run
                    with
                    | Ok q -> q
                    | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                let tooLate = t0.AddMinutes 61.0
                let apply _ = async { return Ok() }

                match
                    approve approvals apply (Some(audit :> IAuditLog)) runNow tooLate "acme" queued.RequestId "admin-b"
                    |> run
                with
                | Error(AdminMutationRefusal.Expired(id, at)) ->
                    Expect.equal id queued.RequestId "the refusal names the request"
                    Expect.equal at (t0.AddMinutes 60.0) "the refusal names the lapse instant"
                | other -> failtestf "a lapsed proposal must be refused; got %A" other

                Expect.isEmpty (effectiveFor permissions "bob" "acme") "a lapsed proposal applies nothing"

                match approvals.TryGet("acme", queued.RequestId) |> run with
                | Ok None -> ()
                | other -> failtestf "a lapsed record must be swept as it is touched; got %A" other

                // Both rows, in order: the proposal ended, and the
                // attempt on it was refused. A trail that showed only the
                // refusal would leave the proposal looking open forever.
                Expect.equal
                    audit.EventNames
                    [ "AdminMutationExpired"; "AdminMutationApprovalRefused" ]
                    "the expiry is recorded alongside the refused attempt"
            }

            test "listing the queue sweeps lapsed records and records each expiry" {
                let _, _, _, approvals = freshFixture ()
                let audit = RecordingAuditLog()

                for user in [ "bob"; "carol" ] do
                    propose
                        approvals
                        settingsAll
                        None
                        runNow
                        t0
                        "acme"
                        "admin-a"
                        (AdminMutation.SetMemberPermissions(user, "reports", [ Read ]))
                    |> run
                    |> ignore

                // One still-live proposal, made later.
                let live = t0.AddMinutes 30.0

                propose
                    approvals
                    settingsAll
                    None
                    runNow
                    live
                    "acme"
                    "admin-a"
                    (AdminMutation.SetMemberPermissions("dave", "reports", [ Read ]))
                |> run
                |> ignore

                match
                    listPending approvals (Some(audit :> IAuditLog)) runNow (t0.AddMinutes 61.0) "acme"
                    |> run
                with
                | Ok remaining ->
                    Expect.hasLength remaining 1 "only the later proposal is still live"

                    Expect.equal
                        remaining[0].ExpiresAtUtc
                        (live.AddMinutes 60.0)
                        "the surviving record is the later one"
                | Error e -> failtestf "listing the queue failed: %s" e

                Expect.equal
                    (audit.EventNames
                     |> List.filter (fun n -> n = "AdminMutationExpired")
                     |> List.length)
                    2
                    "one expiry row per swept record"
            }

            test "a non-positive configured TTL reads as the default, not as never or instantly" {
                // "0" from a misread env var must not mean "born expired"
                // (which refuses every gated write forever) nor "never
                // expires" (which is the accumulating queue the TTL exists
                // to prevent).
                for configured in [ 0; -1; -4320 ] do
                    Expect.equal
                        (DualControlSettings.ttlMinutes {
                            Scope = AdminMutationScope.AllPermissionWrites
                            PendingTtlMinutes = configured
                        })
                        DualControlSettings.DefaultTtlMinutes
                        $"a TTL of {configured} falls back to the default"

                Expect.equal
                    (DualControlSettings.ttlMinutes settingsAll)
                    60
                    "a positive configured TTL is honoured verbatim"
            }
        ]

        // ── 4. GP 11 / GP 13 — the unchanged floor ───────────────────
        testList "unconfigured and ungated writes are byte-parity with today" [

            test "ServerConfig defaults to SingleAdmin" {
                Expect.equal
                    ServerConfig.defaults.AdminMutationPolicy
                    AdminMutationPolicy.SingleAdmin
                    "an existing deployment that upgrades is not silently put under dual control"
            }

            test "a revocation is never gated — it narrows authority" {
                // Parking a revocation would leave authority standing that
                // an administrator has decided must go, and would hand a
                // compromised admin a way to keep their own access by
                // proposing its removal and never approving it.
                let _, _, permissions, approvals = freshFixture ()

                permissions.SetMemberPermissions("acme", "bob", "reports", [ Read; Write ])
                |> run
                |> ignore

                let gate =
                    DualControlPermissionStore(
                        permissions,
                        settingsAll,
                        approvals,
                        nothingIsPolicyBearing,
                        (fun () -> Some "admin-a"),
                        (fun () -> t0)
                    )

                match
                    (gate :> IPermissionStore).SetMemberPermissions("acme", "bob", "reports", [])
                    |> run
                with
                | Ok() -> ()
                | Error e -> failtestf "a revocation must apply immediately; got %s" e

                Expect.isEmpty (effectiveFor permissions "bob" "acme") "the revocation landed"

                match listPending approvals None runNow t0 "acme" |> run with
                | Ok [] -> ()
                | Ok queued -> failtestf "a revocation must not be queued; %d were" queued.Length
                | Error e -> failtestf "listing the queue failed: %s" e
            }

            test "an ungated write persists BYTE-IDENTICAL bytes to the undecorated store" {
                // GP 11 is a claim about what is on disk, so this compares
                // the persisted document rather than a return value: a
                // deployment that enables dual control must find writes the
                // gate does not touch producing exactly the bytes they did.
                let _, plainStorage, plainStore, _ = freshFixture ()
                let _, gatedStorage, gatedInner, approvals = freshFixture ()

                let gate =
                    DualControlPermissionStore(
                        gatedInner,
                        // The narrow scope, with nothing declaring a policy
                        // — so no write in this test is gated.
                        settingsPolicyOnly,
                        approvals,
                        nothingIsPolicyBearing,
                        (fun () -> Some "admin-a"),
                        (fun () -> t0)
                    )

                let apply (store: IPermissionStore) =
                    store.SetMemberPermissions("acme", "bob", "reports", [ Read; Write ])
                    |> run
                    |> ignore

                    store.SetTeamDefaults("acme", Map.ofList [ "reports", [ Read ] ])
                    |> run
                    |> ignore

                    store.SetModuleExposure("acme", "admin", ModuleExposure.Hidden) |> run |> ignore

                apply plainStore
                apply (gate :> IPermissionStore)

                match documentBytes plainStorage "acme", documentBytes gatedStorage "acme" with
                | Some expected, Some actual ->
                    Expect.equal actual expected "the persisted permission document is byte-identical"
                | before, after -> failtestf "both documents must exist; got %A / %A" before after
            }

            test "the narrow scope gates only modules that declare a grant policy" {
                let _, _, permissions, approvals = freshFixture ()

                let gate =
                    DualControlPermissionStore(
                        permissions,
                        settingsPolicyOnly,
                        approvals,
                        (fun name -> name = "payroll"),
                        (fun () -> Some "admin-a"),
                        (fun () -> t0)
                    )

                let store = gate :> IPermissionStore

                match store.SetMemberPermissions("acme", "bob", "reports", [ Read ]) |> run with
                | Ok() -> ()
                | Error e -> failtestf "a non-policy module must not be gated under the narrow scope; got %s" e

                match store.SetMemberPermissions("acme", "bob", "payroll", [ Read ]) |> run with
                | Error message ->
                    Expect.stringContains message "DUAL-CONTROL-PENDING-APPROVAL" "the policy-bearing module IS gated"
                | Ok() -> failtest "a policy-bearing module must be gated under the narrow scope"

                Expect.equal
                    (effectiveFor permissions "bob" "acme" |> Map.tryFind "reports")
                    (Some [ Read ])
                    "the ungated grant landed"

                Expect.equal
                    (effectiveFor permissions "bob" "acme" |> Map.tryFind "payroll")
                    None
                    "the gated grant did not"
            }

            test "widenedModules reports additions and exposure increases, never removals" {
                let previous = {
                    TeamPermissions.empty with
                        Members = Map.ofList [ "bob", Map.ofList [ "reports", [ Read ] ] ]
                        Exposure = Map.ofList [ "admin", ModuleExposure.Unavailable ]
                }

                // Re-writing the identical entry widens nothing, so an
                // operator repairing a document proposes nothing.
                Expect.isEmpty
                    (widenedModules previous (AdminMutation.SetMemberPermissions("bob", "reports", [ Read ])))
                    "an unchanged entry is not a widening"

                Expect.equal
                    (widenedModules previous (AdminMutation.SetMemberPermissions("bob", "reports", [ Read; Write ])))
                    [ "reports" ]
                    "adding a permission is a widening"

                Expect.isEmpty
                    (widenedModules previous (AdminMutation.SetModuleExposure("admin", ModuleExposure.Unavailable)))
                    "an unchanged exposure is not a widening"

                Expect.equal
                    (widenedModules previous (AdminMutation.SetModuleExposure("admin", ModuleExposure.Hidden)))
                    [ "admin" ]
                    "raising exposure is a widening"

                Expect.isEmpty
                    (widenedModules
                        {
                            previous with
                                Exposure = Map.ofList [ "admin", ModuleExposure.Available ]
                        }
                        (AdminMutation.SetModuleExposure("admin", ModuleExposure.Hidden)))
                    "lowering exposure is not a widening"

                // A whole-document write that simply OMITS a restriction
                // re-exposes the module, because absence reads as
                // Available. That is a widening even though nothing in the
                // new document mentions it.
                Expect.equal
                    (widenedModules previous (AdminMutation.SetTeamPermissions TeamPermissions.empty))
                    [ "admin" ]
                    "dropping a restriction is a widening"
            }
        ]

        // ── 5. Composition with Phase 551 ────────────────────────────
        testList "composition with Phase 551 module grant policy" [

            test "a grant the module would never admit is REFUSED, not queued" {
                // The 555.D requirement, asserted on the shipped chain
                // order: GrantPolicyPermissionStore outermost, dual
                // control inside it. A write that can never be approved
                // must not cost two administrators a ceremony.
                let _, _, permissions, approvals = freshFixture ()

                let gate =
                    DualControlPermissionStore(
                        permissions,
                        settingsAll,
                        approvals,
                        (fun name -> name = "payroll"),
                        (fun () -> Some "admin-a"),
                        (fun () -> t0)
                    )

                let registry =
                    GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [
                        "payroll", GrantPolicy.RequiresAcknowledgement
                    ]

                let composed =
                    GrantPolicyGuard.GrantPolicyPermissionStore(gate :> IPermissionStore, registry) :> IPermissionStore

                match composed.SetMemberPermissions("acme", "bob", "payroll", [ Read ]) |> run with
                | Error message ->
                    Expect.stringContains
                        message
                        "GRANT-POLICY-ACK-REQUIRED"
                        "the module's declared policy refuses first"

                    Expect.isFalse (message.Contains "DUAL-CONTROL") "the write never reached the dual-control gate"
                | Ok() -> failtest "an unbacked policy-bearing grant must be refused"

                match listPending approvals None runNow t0 "acme" |> run with
                | Ok [] -> ()
                | Ok queued ->
                    failtestf
                        "a write the module can never admit must not be parked; %d proposal(s) were queued"
                        queued.Length
                | Error e -> failtestf "listing the queue failed: %s" e
            }

            test "a policy-satisfying grant still requires the second administrator" {
                // The two controls are independent, not alternatives:
                // satisfying the module's ceremony does not satisfy the
                // estate's.
                let _, _, permissions, approvals = freshFixture ()

                let gate =
                    DualControlPermissionStore(
                        permissions,
                        settingsAll,
                        approvals,
                        (fun name -> name = "payroll"),
                        (fun () -> Some "admin-a"),
                        (fun () -> t0)
                    )

                let registry =
                    GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [
                        "payroll", GrantPolicy.RequiresAcknowledgement
                    ]

                let composed =
                    GrantPolicyGuard.GrantPolicyPermissionStore(gate :> IPermissionStore, registry) :> IPermissionStore

                let outcome =
                    GrantPolicyGuard.PermissionGrants.grantModuleAccess composed registry {
                        TeamId = "acme"
                        ActorId = "admin-a"
                        SubjectId = "bob"
                        ModuleName = "payroll"
                        Permissions = [ Read ]
                        Evidence = GrantPolicyGuard.GrantEvidence.acknowledged "quarter-end close"
                    }
                    |> run

                Expect.isError outcome "an acknowledged grant is still parked by dual control"

                Expect.isEmpty
                    (effectiveFor permissions "bob" "acme")
                    "the grantee's access is unchanged while the proposal is pending"

                let pending =
                    match listPending approvals None runNow t0 "acme" |> run with
                    | Ok ps -> ps
                    | Error e -> failtestf "listing the queue failed: %s" e

                Expect.hasLength pending 1 "the policy-satisfying grant IS queued"

                match gate.Approve("acme", pending[0].RequestId, "admin-b") |> run with
                | Ok(AdminMutationDecision.Applied _) -> ()
                | other -> failtestf "a distinct approver must apply it; got %A" other

                Expect.equal
                    (effectiveFor permissions "bob" "acme" |> Map.tryFind "payroll")
                    (Some [ Read ])
                    "and the grant is live once both controls are satisfied"
            }
        ]

        // ── 6. The fingerprint binds the approval to the payload ─────
        testList "the approval binds to the exact bytes proposed" [

            test "the fingerprint is stable across processes and distinguishes payloads" {
                let a = AdminMutation.SetMemberPermissions("bob", "reports", [ Read ])
                let b = AdminMutation.SetMemberPermissions("bob", "reports", [ Read; Write ])
                let c = AdminMutation.SetMemberPermissions("carol", "reports", [ Read ])

                Expect.equal (fingerprint a) (fingerprint a) "the fingerprint is deterministic"
                Expect.notEqual (fingerprint a) (fingerprint b) "a widened grant fingerprints differently"
                Expect.notEqual (fingerprint a) (fingerprint c) "a different subject fingerprints differently"

                Expect.equal (fingerprint a).Length 64 "SHA-256, lowercase hex"

                Expect.isTrue
                    ((fingerprint a)
                     |> Seq.forall (fun ch -> Char.IsDigit ch || (ch >= 'a' && ch <= 'f')))
                    "lowercase hex only"
            }

            test "a record whose payload no longer hashes to its fingerprint cannot be approved" {
                let _, _, permissions, approvals = freshFixture ()

                let queued =
                    match
                        propose
                            approvals
                            settingsAll
                            None
                            runNow
                            t0
                            "acme"
                            "admin-a"
                            (AdminMutation.SetMemberPermissions("bob", "reports", [ Read ]))
                        |> run
                    with
                    | Ok q -> q
                    | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                // Swap the payload for a wider one under the recorded
                // fingerprint — the shape a tampered or partially-written
                // blob takes.
                let tampered =
                    match approvals.TryGet("acme", queued.RequestId) |> run with
                    | Ok(Some p) -> {
                        p with
                            Mutation = AdminMutation.SetMemberPermissions("bob", "reports", [ Read; Write ])
                      }
                    | other -> failtestf "could not read the pending record back: %A" other

                approvals.Propose tampered |> run |> ignore

                let apply _ = async { return Ok() }

                match approve approvals apply None runNow t0 "acme" queued.RequestId "admin-b" |> run with
                | Error(AdminMutationRefusal.FingerprintMismatch id) ->
                    Expect.equal id queued.RequestId "the refusal names the request"
                | other -> failtestf "a tampered record must not be approved; got %A" other

                Expect.isEmpty (effectiveFor permissions "bob" "acme") "nothing applied"
            }

            test "the captured mutation survives a real round-trip through the blob store" {
                // The pending record is persisted as JSON, so a field
                // silently lost on the way to disk would make the approval
                // apply something other than what was proposed. Every
                // mutation arm is round-tripped, including the whole-
                // document one carrying grant records and exposure.
                let _, _, _, approvals = freshFixture ()

                let mutations = [
                    AdminMutation.SetMemberPermissions("bob", "reports", [ Read; Write ])
                    AdminMutation.SetTeamDefaults(Map.ofList [ "reports", [ Read ] ])
                    AdminMutation.SetModuleExposure("admin", ModuleExposure.Unavailable)
                    AdminMutation.SetTeamPermissions {
                        Defaults = Map.ofList [ "reports", [ Read ] ]
                        Members = Map.ofList [ "bob", Map.ofList [ "payroll", [ Read; Write ] ] ]
                        Exposure = Map.ofList [ "admin", ModuleExposure.Hidden ]
                        Grants =
                            Map.ofList [
                                "bob",
                                Map.ofList [
                                    "payroll",
                                    {
                                        State = GrantState.Active
                                        SatisfiedPolicy = GrantPolicy.RequiresAcknowledgement
                                        Justification = "quarter-end close"
                                        ConsentedBy = Some "bob"
                                    }
                                ]
                            ]
                    }
                ]

                for mutation in mutations do
                    let queued =
                        match propose approvals settingsAll None runNow t0 "acme" "admin-a" mutation |> run with
                        | Ok q -> q
                        | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                    match approvals.TryGet("acme", queued.RequestId) |> run with
                    | Ok(Some p) ->
                        Expect.equal p.Mutation mutation "the captured mutation round-trips unchanged"
                        Expect.equal p.Fingerprint (fingerprint mutation) "and still hashes to its fingerprint"
                    | other -> failtestf "the pending record must read back; got %A" other
            }
        ]

        // ── 7. Store failures fail CLOSED ────────────────────────────
        test "an unreadable approval store refuses the approval rather than admitting it" {
            // A two-person rule that fails open on a storage blip is a
            // single-person rule with extra steps.
            let failing =
                { new IAdminMutationApprovalStore with
                    member _.Propose _ = async { return Ok() }
                    member _.TryGet(_, _) = async { return Error "storage unreachable" }
                    member _.List _ = async { return Error "storage unreachable" }
                    member _.Remove(_, _) = async { return Ok() }
                }

            let applied = ref false

            let apply _ = async {
                applied.Value <- true
                return Ok()
            }

            match approve failing apply None runNow t0 "acme" "req-1" "admin-b" |> run with
            | Error(AdminMutationRefusal.ApprovalStoreUnavailable _) -> ()
            | other -> failtestf "an unreadable store must refuse; got %A" other

            Expect.isFalse applied.Value "nothing may be applied when the queue cannot be read"
        }

        test "an unknown request is refused and applies nothing" {
            let _, _, _, approvals = freshFixture ()
            let audit = RecordingAuditLog()
            let applied = ref false

            let apply _ = async {
                applied.Value <- true
                return Ok()
            }

            match
                approve approvals apply (Some(audit :> IAuditLog)) runNow t0 "acme" "no-such-request" "admin-b"
                |> run
            with
            | Error(AdminMutationRefusal.UnknownRequest id) ->
                Expect.equal id "no-such-request" "the refusal names the request"
            | other -> failtestf "an unknown request must be refused; got %A" other

            Expect.isFalse applied.Value "nothing applied"

            match audit.Recorded with
            | [ _, AdminMutationApprovalRefused p ] ->
                Expect.equal p.RefusalCode "unknown-request" "the row carries the dashboarded code"
            | other -> failtestf "expected exactly one refusal row; got %A" other
        }
    ]