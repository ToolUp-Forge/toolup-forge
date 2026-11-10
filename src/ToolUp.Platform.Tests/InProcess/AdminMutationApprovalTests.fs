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
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Scope.PermissionStore
open ToolUp.Platform.Scope.AdminMutationApproval
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Scope
open ToolUp.Platform.Testing

/// A log that records nothing and never refuses — the test's "no audit".
let private noAudit =
    { new IAuditLog with
        member _.Record(AuditScope.StreamKey _, _actor, _) = async { return Ok() }
        member _.GetAuditTrail(AuditScope.StreamKey _, _, _) = async { return [] }
    }


// ─── Fixtures ────────────────────────────────────────────────────────

let private Read = ModulePermission.Read
let private Write = ModulePermission.Write

let private tempDir () = TempDir.create "dualcontrol"

let private storageAt (dir: TempDir) =
    LocalFileStorage.LocalFileStorage(dir.Path) :> IBlobStorage

/// A real blob-backed permission store plus the approval queue beside
/// it, over one private temp dir — the same substrate a deployment runs.
let private freshFixture (dir: TempDir) =
    let storage = storageAt dir

    let permissions = PermissionStore(storage) :> IPermissionStore

    let approvals =
        BlobAdminMutationApprovalStore(storage) :> IAdminMutationApprovalStore

    storage, permissions, approvals

/// Every recorded row as (audit stream, event), so a test can assert the exact row.
let private recorded (audit: TestAuditLog) =
    audit.Records |> List.map (fun r -> r.StreamKey, r.Event)

let private eventNames (audit: TestAuditLog) =
    audit.Events |> List.map AuditEvent.eventTypeName

/// An inner store whose document cannot be read: every read answers
/// `Unreadable` and every write refuses. Before Phase 1062 the read RAISED
/// and the raise travelled out of `grantModuleAccess` past the
/// classification of its `Result`.
type private UnreadablePermissionStore() =
    let unreadable () = async {
        return Error(PermissionStoreError.Unreadable "pinned: the permission document is unreadable")
    }

    let refused () = async { return Error "pinned: the permission document is unreadable" }

    interface IPermissionStore with
        member _.GetTeamPermissions _ = unreadable ()
        member _.UpdateTeamPermissions(_, _) = refused ()
        member _.SetTeamPermissions(_, _) = refused ()
        member _.GetEffectivePermissions(_, _) = unreadable ()
        member _.SetMemberPermissions(_, _, _, _) = refused ()
        member _.SetTeamDefaults(_, _) = refused ()
        member _.GetModuleExposure _ = unreadable ()
        member _.SetModuleExposure(_, _, _) = refused ()

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
    match store.GetEffectivePermissions(userId, teamId) |> Async.RunSynchronously with
    // The grants the store answers; a team that configured none reads as
    // the empty map, as these cases compare it.
    | Ok(ModulePermissions.Restricted perms) -> perms
    | Ok ModulePermissions.Unrestricted -> Map.empty
    | Error e -> failtestf "the permission read failed: %s" (PermissionStoreError.describe e)

let private run x = Async.RunSynchronously x

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 555 — dual control for sensitive admin mutations" [

        // ── 1. The ceremony ──────────────────────────────────────────
        testList "the two-person ceremony" [
            test "Phase 1062 — dual control over an unreadable document refuses with a typed error, never a throw" {
                use dir = tempDir ()

                let gate =
                    DualControlPermissionStore(
                        UnreadablePermissionStore(),
                        settingsAll,
                        BlobAdminMutationApprovalStore(storageAt dir),
                        nothingIsPolicyBearing,
                        (fun () -> Some "admin-a"),
                        (fun () -> t0),
                        noAudit
                    )
                    :> IPermissionStore

                let request: GrantPolicyGuard.ModuleGrantRequest = {
                    TeamId = TeamId "acme"
                    ActorId = "admin-a"
                    SubjectId = "bob"
                    ModuleName = "reports"
                    Permissions = [ Read ]
                    Evidence = GrantPolicyGuard.GrantEvidence.none
                }

                match
                    GrantPolicyGuard.PermissionGrants.grantModuleAccess
                        gate
                        GrantPolicyGuard.ModuleGrantPolicyRegistry.empty
                        request
                    |> Async.Catch
                    |> run
                with
                | Choice1Of2(Error(GrantRefusal.StoreUnavailable("reports", _))) -> ()
                | Choice1Of2 other -> failtestf "expected StoreUnavailable, got %A" other
                | Choice2Of2 ex -> failtestf "grantModuleAccess threw past its Result: %s" ex.Message
            }


            test "a gated grant is inert until a second administrator approves it" {
                use dir = tempDir ()
                let _, permissions, approvals = freshFixture dir

                let gate =
                    DualControlPermissionStore(
                        permissions,
                        settingsAll,
                        approvals,
                        nothingIsPolicyBearing,
                        (fun () -> Some "admin-a"),
                        (fun () -> t0),
                        noAudit
                    )

                let store = gate :> IPermissionStore

                // Admin A grants. The write must NOT apply.
                let written =
                    store.SetMemberPermissions(TeamId "acme", UserId "bob", "reports", [ Read; Write ])
                    |> run

                match written with
                | Ok() -> failtest "a gated write must not report success — it did not apply"
                | Error message ->
                    Expect.stringContains
                        message
                        "DUAL-CONTROL-PENDING-APPROVAL"
                        "the refusal names the dual-control queue"

                Expect.isEmpty
                    (effectiveFor store (UserId "bob") (TeamId "acme"))
                    "the grantee's effective permissions must be unchanged while the proposal is pending"

                // Exactly one proposal is queued.
                let pending =
                    match listPending approvals noAudit t0 "acme" |> run with
                    | Ok ps -> ps
                    | Error e -> failtestf "listing the queue failed: %s" e

                Expect.hasLength pending 1 "one proposal queued"
                Expect.equal pending[0].ProposedBy "admin-a" "the proposer is recorded"

                // Admin B approves — and only now does it apply.
                match gate.Approve("acme", pending[0].RequestId, "admin-b") |> run with
                | Ok(AdminMutationDecision.Applied _) -> ()
                | other -> failtestf "a distinct approver must apply the mutation; got %A" other

                Expect.equal
                    (effectiveFor store (UserId "bob") (TeamId "acme") |> Map.tryFind "reports")
                    (Some [ Read; Write ])
                    "the approved grant is live"

                match listPending approvals noAudit t0 "acme" |> run with
                | Ok [] -> ()
                | Ok remaining -> failtestf "the decided proposal must leave the queue; %d remain" remaining.Length
                | Error e -> failtestf "listing the queue failed: %s" e
            }

            test "propose emits AdminMutationProposed naming the request, proposer and fingerprint" {
                use dir = tempDir ()
                let _, _, approvals = freshFixture dir
                let audit = TestAuditLog()

                let mutation = AdminMutation.SetMemberPermissions(UserId "bob", "reports", [ Read ])

                let queued =
                    match
                        propose approvals settingsAll (audit :> IAuditLog) t0 "acme" "admin-a" mutation
                        |> run
                    with
                    | Ok q -> q
                    | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                match recorded audit with
                | [ scope, AdminMutationProposed p ] ->
                    Expect.equal scope "acme" "recorded under the team audit stream (the bare id)"
                    Expect.equal p.RequestId queued.RequestId "the row names the queued request"
                    Expect.equal p.ProposerId "admin-a" "the row names the proposer"
                    Expect.equal p.MutationKind "member-permissions" "the row carries the mutation kind token"
                    Expect.equal p.Fingerprint (fingerprint mutation) "the row carries the payload fingerprint"
                    Expect.equal p.ExpiresAtUtc (t0.AddMinutes 60.0) "the row carries the lapse instant"
                | other -> failtestf "expected exactly one AdminMutationProposed row; got %A" other
            }

            test "the proposer may withdraw their own proposal, and nothing applies" {
                use dir = tempDir ()
                let _, permissions, approvals = freshFixture dir
                let audit = TestAuditLog()

                let mutation = AdminMutation.SetMemberPermissions(UserId "bob", "reports", [ Read ])

                let queued =
                    match propose approvals settingsAll noAudit t0 "acme" "admin-a" mutation |> run with
                    | Ok q -> q
                    | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                // Withdrawal narrows nothing and creates nothing, so it
                // does NOT require a second person — see the asymmetry
                // note in AdminMutationApproval.fs.
                match
                    reject approvals (audit :> IAuditLog) t0 "acme" queued.RequestId "admin-a" "mistyped the user id"
                    |> run
                with
                | Ok(AdminMutationDecision.Rejected _) -> ()
                | other -> failtestf "a proposer must be able to withdraw; got %A" other

                Expect.isEmpty
                    (effectiveFor permissions (UserId "bob") (TeamId "acme"))
                    "a rejected proposal applies nothing"

                match eventNames audit with
                | [ "AdminMutationRejected" ] -> ()
                | names -> failtestf "expected exactly one AdminMutationRejected row; got %A" names
            }
        ]

        // ── 2. Self-approval ─────────────────────────────────────────
        testList "self-approval is refused by construction" [

            test "the proposer cannot approve their own proposal, and the record survives" {
                use dir = tempDir ()
                let _, permissions, approvals = freshFixture dir
                let audit = TestAuditLog()

                let mutation =
                    AdminMutation.SetMemberPermissions(UserId "bob", "reports", [ Read; Write ])

                let queued =
                    match propose approvals settingsAll noAudit t0 "acme" "admin-a" mutation |> run with
                    | Ok q -> q
                    | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                let apply _ = async { return Ok() }

                match
                    approve approvals apply (audit :> IAuditLog) t0 "acme" queued.RequestId "admin-a"
                    |> run
                with
                | Error(AdminMutationRefusal.SelfApprovalRefused(id, actor)) ->
                    Expect.equal id queued.RequestId "the refusal names the request"
                    Expect.equal actor "admin-a" "the refusal names the attempting actor"
                | other -> failtestf "self-approval must be refused; got %A" other

                Expect.isEmpty
                    (effectiveFor permissions (UserId "bob") (TeamId "acme"))
                    "a refused self-approval applies nothing"

                // The record must SURVIVE: a refused attempt is not a
                // decision, and consuming the proposal would let a
                // proposer destroy their own pending grant by attempting
                // to approve it.
                match approvals.TryGet(TeamId "acme", queued.RequestId) |> run with
                | Ok(Some _) -> ()
                | other -> failtestf "the pending record must survive a refused approval; got %A" other

                match recorded audit with
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
                    use dir = tempDir ()
                    let _, _, approvals = freshFixture dir

                    let queued =
                        match
                            propose
                                approvals
                                settingsAll
                                noAudit
                                t0
                                "acme"
                                "admin-a"
                                (AdminMutation.SetMemberPermissions(UserId "bob", "reports", [ Read ]))
                            |> run
                        with
                        | Ok q -> q
                        | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                    let apply _ = async { return Ok() }

                    match approve approvals apply noAudit t0 "acme" queued.RequestId variant |> run with
                    | Error(AdminMutationRefusal.SelfApprovalRefused _) -> ()
                    | other -> failtestf "'%s' must not be able to approve admin-a's proposal; got %A" variant other

                Expect.isTrue
                    (isSameAdministrator "admin-a" " Admin-A ")
                    "the sameness predicate is trimmed and case-insensitive"

                Expect.isFalse (isSameAdministrator "admin-a" "admin-b") "genuinely distinct ids stay distinct"
            }

            test "an unattributable gated write is refused, never parked" {
                use dir = tempDir ()
                let _, permissions, approvals = freshFixture dir

                let gate =
                    DualControlPermissionStore(
                        permissions,
                        settingsAll,
                        approvals,
                        nothingIsPolicyBearing,
                        // No resolvable acting administrator.
                        (fun () -> None),
                        (fun () -> t0),
                        noAudit
                    )

                match
                    (gate :> IPermissionStore).SetMemberPermissions(TeamId "acme", UserId "bob", "reports", [ Read ])
                    |> run
                with
                | Error message ->
                    Expect.stringContains message "DUAL-CONTROL-UNATTRIBUTED" "the refusal names the missing actor"
                | Ok() -> failtest "an unattributable gated write must not apply"

                match listPending approvals noAudit t0 "acme" |> run with
                | Ok [] -> ()
                | Ok queued -> failtestf "nothing may be queued for an unknown proposer; %d were" queued.Length
                | Error e -> failtestf "listing the queue failed: %s" e

                Expect.isEmpty (effectiveFor permissions (UserId "bob") (TeamId "acme")) "nothing applied"
            }
        ]

        // ── 3. Expiry ────────────────────────────────────────────────
        testList "a proposal that lapses is refused and swept" [

            test "approval past the TTL is refused, the record removed, and the expiry recorded" {
                use dir = tempDir ()
                let _, permissions, approvals = freshFixture dir
                let audit = TestAuditLog()

                let queued =
                    match
                        propose
                            approvals
                            settingsAll
                            noAudit
                            t0
                            "acme"
                            "admin-a"
                            (AdminMutation.SetMemberPermissions(UserId "bob", "reports", [ Read ]))
                        |> run
                    with
                    | Ok q -> q
                    | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                let tooLate = t0.AddMinutes 61.0
                let apply _ = async { return Ok() }

                match
                    approve approvals apply (audit :> IAuditLog) tooLate "acme" queued.RequestId "admin-b"
                    |> run
                with
                | Error(AdminMutationRefusal.Expired(id, at)) ->
                    Expect.equal id queued.RequestId "the refusal names the request"
                    Expect.equal at (t0.AddMinutes 60.0) "the refusal names the lapse instant"
                | other -> failtestf "a lapsed proposal must be refused; got %A" other

                Expect.isEmpty
                    (effectiveFor permissions (UserId "bob") (TeamId "acme"))
                    "a lapsed proposal applies nothing"

                match approvals.TryGet(TeamId "acme", queued.RequestId) |> run with
                | Ok None -> ()
                | other -> failtestf "a lapsed record must be swept as it is touched; got %A" other

                // Both rows, in order: the proposal ended, and the
                // attempt on it was refused. A trail that showed only the
                // refusal would leave the proposal looking open forever.
                Expect.equal
                    (eventNames audit)
                    [ "AdminMutationExpired"; "AdminMutationApprovalRefused" ]
                    "the expiry is recorded alongside the refused attempt"
            }

            test "listing the queue sweeps lapsed records and records each expiry" {
                use dir = tempDir ()
                let _, _, approvals = freshFixture dir
                let audit = TestAuditLog()

                for user in [ "bob"; "carol" ] do
                    propose
                        approvals
                        settingsAll
                        noAudit
                        t0
                        "acme"
                        "admin-a"
                        (AdminMutation.SetMemberPermissions(UserId user, "reports", [ Read ]))
                    |> run
                    |> ignore

                // One still-live proposal, made later.
                let live = t0.AddMinutes 30.0

                propose
                    approvals
                    settingsAll
                    noAudit
                    live
                    "acme"
                    "admin-a"
                    (AdminMutation.SetMemberPermissions(UserId "dave", "reports", [ Read ]))
                |> run
                |> ignore

                match listPending approvals (audit :> IAuditLog) (t0.AddMinutes 61.0) "acme" |> run with
                | Ok remaining ->
                    Expect.hasLength remaining 1 "only the later proposal is still live"

                    Expect.equal
                        remaining[0].ExpiresAtUtc
                        (live.AddMinutes 60.0)
                        "the surviving record is the later one"
                | Error e -> failtestf "listing the queue failed: %s" e

                Expect.equal
                    (eventNames audit
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
                use dir = tempDir ()
                let _, permissions, approvals = freshFixture dir

                permissions.SetMemberPermissions(TeamId "acme", UserId "bob", "reports", [ Read; Write ])
                |> run
                |> ignore

                let gate =
                    DualControlPermissionStore(
                        permissions,
                        settingsAll,
                        approvals,
                        nothingIsPolicyBearing,
                        (fun () -> Some "admin-a"),
                        (fun () -> t0),
                        noAudit
                    )

                match
                    (gate :> IPermissionStore).SetMemberPermissions(TeamId "acme", UserId "bob", "reports", [])
                    |> run
                with
                | Ok() -> ()
                | Error e -> failtestf "a revocation must apply immediately; got %s" e

                Expect.isEmpty (effectiveFor permissions (UserId "bob") (TeamId "acme")) "the revocation landed"

                match listPending approvals noAudit t0 "acme" |> run with
                | Ok [] -> ()
                | Ok queued -> failtestf "a revocation must not be queued; %d were" queued.Length
                | Error e -> failtestf "listing the queue failed: %s" e
            }

            test "an ungated write persists BYTE-IDENTICAL bytes to the undecorated store" {
                // GP 11 is a claim about what is on disk, so this compares
                // the persisted document rather than a return value: a
                // deployment that enables dual control must find writes the
                // gate does not touch producing exactly the bytes they did.
                use plainDir = tempDir ()
                use gatedDir = tempDir ()
                let plainStorage, plainStore, _ = freshFixture plainDir
                let gatedStorage, gatedInner, approvals = freshFixture gatedDir

                let gate =
                    DualControlPermissionStore(
                        gatedInner,
                        // The narrow scope, with nothing declaring a policy
                        // — so no write in this test is gated.
                        settingsPolicyOnly,
                        approvals,
                        nothingIsPolicyBearing,
                        (fun () -> Some "admin-a"),
                        (fun () -> t0),
                        noAudit
                    )

                let apply (store: IPermissionStore) =
                    store.SetMemberPermissions(TeamId "acme", UserId "bob", "reports", [ Read; Write ])
                    |> run
                    |> ignore

                    store.SetTeamDefaults(TeamId "acme", Map.ofList [ "reports", [ Read ] ])
                    |> run
                    |> ignore

                    store.SetModuleExposure(TeamId "acme", "admin", ModuleExposure.Hidden)
                    |> run
                    |> ignore

                apply plainStore
                apply (gate :> IPermissionStore)

                match documentBytes plainStorage "acme", documentBytes gatedStorage "acme" with
                | Some expected, Some actual ->
                    Expect.equal actual expected "the persisted permission document is byte-identical"
                | before, after -> failtestf "both documents must exist; got %A / %A" before after
            }

            test "the narrow scope gates only modules that declare a grant policy" {
                use dir = tempDir ()
                let _, permissions, approvals = freshFixture dir

                let gate =
                    DualControlPermissionStore(
                        permissions,
                        settingsPolicyOnly,
                        approvals,
                        (fun name -> name = "payroll"),
                        (fun () -> Some "admin-a"),
                        (fun () -> t0),
                        noAudit
                    )

                let store = gate :> IPermissionStore

                match
                    store.SetMemberPermissions(TeamId "acme", UserId "bob", "reports", [ Read ])
                    |> run
                with
                | Ok() -> ()
                | Error e -> failtestf "a non-policy module must not be gated under the narrow scope; got %s" e

                match
                    store.SetMemberPermissions(TeamId "acme", UserId "bob", "payroll", [ Read ])
                    |> run
                with
                | Error message ->
                    Expect.stringContains message "DUAL-CONTROL-PENDING-APPROVAL" "the policy-bearing module IS gated"
                | Ok() -> failtest "a policy-bearing module must be gated under the narrow scope"

                Expect.equal
                    (effectiveFor permissions (UserId "bob") (TeamId "acme") |> Map.tryFind "reports")
                    (Some [ Read ])
                    "the ungated grant landed"

                Expect.equal
                    (effectiveFor permissions (UserId "bob") (TeamId "acme") |> Map.tryFind "payroll")
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
                    (widenedModules previous (AdminMutation.SetMemberPermissions(UserId "bob", "reports", [ Read ])))
                    "an unchanged entry is not a widening"

                Expect.equal
                    (widenedModules
                        previous
                        (AdminMutation.SetMemberPermissions(UserId "bob", "reports", [ Read; Write ])))
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
                use dir = tempDir ()
                let _, permissions, approvals = freshFixture dir

                let gate =
                    DualControlPermissionStore(
                        permissions,
                        settingsAll,
                        approvals,
                        (fun name -> name = "payroll"),
                        (fun () -> Some "admin-a"),
                        (fun () -> t0),
                        noAudit
                    )

                let registry =
                    GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [
                        "payroll", GrantPolicy.RequiresAcknowledgement
                    ]

                let composed =
                    GrantPolicyGuard.GrantPolicyPermissionStore(gate :> IPermissionStore, registry, noAudit)
                    :> IPermissionStore

                match
                    composed.SetMemberPermissions(TeamId "acme", UserId "bob", "payroll", [ Read ])
                    |> run
                with
                | Error message ->
                    Expect.stringContains
                        message
                        "GRANT-POLICY-ACK-REQUIRED"
                        "the module's declared policy refuses first"

                    Expect.isFalse (message.Contains "DUAL-CONTROL") "the write never reached the dual-control gate"
                | Ok() -> failtest "an unbacked policy-bearing grant must be refused"

                match listPending approvals noAudit t0 "acme" |> run with
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
                use dir = tempDir ()
                let _, permissions, approvals = freshFixture dir

                let gate =
                    DualControlPermissionStore(
                        permissions,
                        settingsAll,
                        approvals,
                        (fun name -> name = "payroll"),
                        (fun () -> Some "admin-a"),
                        (fun () -> t0),
                        noAudit
                    )

                let registry =
                    GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [
                        "payroll", GrantPolicy.RequiresAcknowledgement
                    ]

                let composed =
                    GrantPolicyGuard.GrantPolicyPermissionStore(gate :> IPermissionStore, registry, noAudit)
                    :> IPermissionStore

                let outcome =
                    GrantPolicyGuard.PermissionGrants.grantModuleAccess composed registry {
                        TeamId = TeamId "acme"
                        ActorId = "admin-a"
                        SubjectId = "bob"
                        ModuleName = "payroll"
                        Permissions = [ Read ]
                        Evidence = GrantPolicyGuard.GrantEvidence.acknowledged "quarter-end close"
                    }
                    |> run

                // Phase 730 — this assertion was `Expect.isError`, and the
                // sentence beside it ("still parked") is what it was TRYING
                // to say. `grantModuleAccess` had no way to say it: it
                // mapped every inner-store error onto
                // `GrantRefusal.UnbackedGrant`, so a parked write was
                // reported as a missing grant record — which is exactly
                // what it was not, since the record had been written with
                // the entry. Phase 730 gave the outcome a name, so the test
                // can now assert the thing it means.
                //
                // This is a STRENGTHENING, not a relaxation: `isError`
                // passed equally for a parked write, a storage outage and a
                // policy refusal, and those want three different operator
                // responses. The queued id is asserted against the queue
                // below, so the outcome and the ceremony are pinned to each
                // other rather than checked separately.
                let queuedId =
                    match outcome with
                    | Ok(GrantWriteOutcome.QueuedForApproval requestId) -> requestId
                    | other ->
                        failtestf "an acknowledged grant is still parked by dual control, and must SAY so; got %A" other

                Expect.isEmpty
                    (effectiveFor permissions (UserId "bob") (TeamId "acme"))
                    "the grantee's access is unchanged while the proposal is pending"

                let pending =
                    match listPending approvals noAudit t0 "acme" |> run with
                    | Ok ps -> ps
                    | Error e -> failtestf "listing the queue failed: %s" e

                Expect.hasLength pending 1 "the policy-satisfying grant IS queued"

                Expect.equal
                    queuedId
                    pending[0].RequestId
                    "and the id the caller was handed is the one an approver has to name"

                match gate.Approve("acme", pending[0].RequestId, "admin-b") |> run with
                | Ok(AdminMutationDecision.Applied _) -> ()
                | other -> failtestf "a distinct approver must apply it; got %A" other

                Expect.equal
                    (effectiveFor permissions (UserId "bob") (TeamId "acme") |> Map.tryFind "payroll")
                    (Some [ Read ])
                    "and the grant is live once both controls are satisfied"
            }
        ]

        // ── 6. The fingerprint binds the approval to the payload ─────
        testList "the approval binds to the exact bytes proposed" [

            test "the fingerprint is stable across processes and distinguishes payloads" {
                let a = AdminMutation.SetMemberPermissions(UserId "bob", "reports", [ Read ])
                let b = AdminMutation.SetMemberPermissions(UserId "bob", "reports", [ Read; Write ])
                let c = AdminMutation.SetMemberPermissions(UserId "carol", "reports", [ Read ])

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
                use dir = tempDir ()
                let _, permissions, approvals = freshFixture dir

                let queued =
                    match
                        propose
                            approvals
                            settingsAll
                            noAudit
                            t0
                            "acme"
                            "admin-a"
                            (AdminMutation.SetMemberPermissions(UserId "bob", "reports", [ Read ]))
                        |> run
                    with
                    | Ok q -> q
                    | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                // Swap the payload for a wider one under the recorded
                // fingerprint — the shape a tampered or partially-written
                // blob takes.
                let tampered =
                    match approvals.TryGet(TeamId "acme", queued.RequestId) |> run with
                    | Ok(Some p) -> {
                        p with
                            Mutation = AdminMutation.SetMemberPermissions(UserId "bob", "reports", [ Read; Write ])
                      }
                    | other -> failtestf "could not read the pending record back: %A" other

                approvals.Propose tampered |> run |> ignore

                let apply _ = async { return Ok() }

                match approve approvals apply noAudit t0 "acme" queued.RequestId "admin-b" |> run with
                | Error(AdminMutationRefusal.FingerprintMismatch id) ->
                    Expect.equal id queued.RequestId "the refusal names the request"
                | other -> failtestf "a tampered record must not be approved; got %A" other

                Expect.isEmpty (effectiveFor permissions (UserId "bob") (TeamId "acme")) "nothing applied"
            }

            test "the captured mutation survives a real round-trip through the blob store" {
                // The pending record is persisted as JSON, so a field
                // silently lost on the way to disk would make the approval
                // apply something other than what was proposed. Every
                // mutation arm is round-tripped, including the whole-
                // document one carrying grant records and exposure.
                use dir = tempDir ()
                let _, _, approvals = freshFixture dir

                let mutations = [
                    AdminMutation.SetMemberPermissions(UserId "bob", "reports", [ Read; Write ])
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
                        Pages = Map.ofList [ "bob", [ PageGrant.SaveTeam ] ]
                    }
                ]

                for mutation in mutations do
                    let queued =
                        match propose approvals settingsAll noAudit t0 "acme" "admin-a" mutation |> run with
                        | Ok q -> q
                        | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

                    match approvals.TryGet(TeamId "acme", queued.RequestId) |> run with
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

            match approve failing apply noAudit t0 "acme" "req-1" "admin-b" |> run with
            | Error(AdminMutationRefusal.ApprovalStoreUnavailable _) -> ()
            | other -> failtestf "an unreadable store must refuse; got %A" other

            Expect.isFalse applied.Value "nothing may be applied when the queue cannot be read"
        }

        // Phase 1059 — removing the record before applying is what stops a
        // second approver applying the same mutation again. A refused
        // `Remove` therefore refuses the approval: nothing is applied, by
        // this approver or the next.
        test "a refused Remove applies nothing, and a second approver cannot apply it either" {
            use dir = tempDir ()
            let _, _, approvals = freshFixture dir

            let stuck =
                { new IAdminMutationApprovalStore with
                    member _.Propose p = approvals.Propose p
                    member _.TryGet(t, r) = approvals.TryGet(t, r)
                    member _.List t = approvals.List t
                    member _.Remove(_, _) = async { return Error "remove refused" }
                }

            let mutation =
                AdminMutation.SetMemberPermissions(UserId "bob", "reports", [ Read; Write ])

            let queued =
                match propose stuck settingsAll noAudit t0 "acme" "admin-a" mutation |> run with
                | Ok q -> q
                | Error r -> failtestf "propose refused: %s" (AdminMutationRefusal.describe r)

            let applied = ref 0

            let apply _ = async {
                applied.Value <- applied.Value + 1
                return Ok()
            }

            for approver in [ "admin-b"; "admin-c" ] do
                match approve stuck apply noAudit t0 "acme" queued.RequestId approver |> run with
                | Error _ -> ()
                | other ->
                    failtestf "%s's approval must be refused while the record cannot be removed; got %A" approver other

            Expect.equal applied.Value 0 "a mutation whose record could not be removed is never applied"
        }

        test "an unknown request is refused and applies nothing" {
            use dir = tempDir ()
            let _, _, approvals = freshFixture dir
            let audit = TestAuditLog()
            let applied = ref false

            let apply _ = async {
                applied.Value <- true
                return Ok()
            }

            match
                approve approvals apply (audit :> IAuditLog) t0 "acme" "no-such-request" "admin-b"
                |> run
            with
            | Error(AdminMutationRefusal.UnknownRequest id) ->
                Expect.equal id "no-such-request" "the refusal names the request"
            | other -> failtestf "an unknown request must be refused; got %A" other

            Expect.isFalse applied.Value "nothing applied"

            match recorded audit with
            | [ _, AdminMutationApprovalRefused p ] ->
                Expect.equal p.RefusalCode "unknown-request" "the row carries the dashboarded code"
            | other -> failtestf "expected exactly one refusal row; got %A" other
        }
    ]