// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GrantGovernanceTests

// ─── Phase 730 — grant-governance completeness ───────────────────────
//
// Three gaps Phases 551 and 552 recorded against THEMSELVES, in their own
// Outcomes. Each is a control that exists, reads correctly, and does not
// cover the path that matters — the Phase 311 / Phase 36.A shape. This
// pack proves each one is closed, and proves the closing did not change
// what an undeclared deployment does.
//
//   1. **The audit trail had only its refusal half.** 551 emitted
//      `GrantPolicyRefused` for every grant a policy turned DOWN and
//      nothing at all for the ones it admitted, so the trail could answer
//      "what was blocked" and not "who was given access to what" — the
//      question a grant trail is actually for. `GrantRecorded` is the
//      twin, emitted from the same decorator so no write path can acquire
//      authority silently.
//
//   2. **`grantModuleAccess` relabelled every inner failure.** Any
//      `Error` from the inner store became `GrantRefusal.UnbackedGrant` —
//      "the written permission entry carries no adequate grant record" —
//      which by that point in the function is provably false, since the
//      entry and its record are written together precisely so they cannot
//      be inconsistent. A Phase 555 dual-control QUEUED result and a
//      storage outage both reported a missing record that was never
//      missing.
//
//   3. **AI tool dispatch consulted neither policy nor consent.** The
//      permission entry is PRESENT for a grant pending the subject's
//      acceptance and present for one whose counterparty consent was
//      revoked — that is what those states mean — so the Phase 36.A RBAC
//      filter admits both. The module stayed listed to the model and
//      callable by it while being inert at the Remoting seam.
//
// **Non-vacuity.** The audit cases use a synchronous-observing recorder
// and assert "exactly one row, of this shape", so a decorator that
// admitted the write without emitting fails here rather than passing an
// assertion over an empty list. The AI cases assert the negative and the
// positive against the SAME registry and the SAME stamps, differing only
// in the grant state — so a gate that filtered everything, or nothing,
// fails one half or the other rather than both passing.

open System
open System.IO
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.PermissionStore
open ToolUp.AI
open ToolUp.AI.AIToolRegistry

// ─── Fixtures ────────────────────────────────────────────────────────

let private Read = ModulePermission.Read
let private Admin = ModulePermission.Admin

let private acmeDpo = PartyRef.create "acme-dpo"

/// A real blob-backed store, so the grant record survives an actual
/// serialisation round trip rather than a stub that cannot lose a field.
let private freshStore () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-grantgov-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    PermissionStore(LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage) :> IPermissionStore

/// Accumulates every recorded event. Deliberately never fails a test on
/// its own — an assertion on "exactly one row of this shape" is only a
/// real assertion because this thing would happily record nothing.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()

    member _.Recorded = List.ofSeq recorded

    member this.GrantRecordedRows =
        this.Recorded
        |> List.choose (function
            | _, GrantRecorded p -> Some p
            | _ -> None)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add((scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// The decorator emits its success row through `Async.Start`, exactly as
/// it emits its refusal row — best-effort per the `IAuditLog` contract,
/// so a downed audit pipeline cannot turn a committed grant into an
/// error. That means a test has to WAIT for the row rather than read it
/// straight after the call.
///
/// Polling with a deadline rather than a fixed sleep: a fixed sleep is
/// either flaky or slow, and this returns the moment the row lands.
/// Deliberately returns whatever it has at the deadline instead of
/// throwing, so the failure a caller sees is "expected 1 row, got 0" —
/// which names the defect — rather than a timeout that does not.
let private awaitRows (log: RecordingAuditLog) (expected: int) =
    let deadline = DateTime.UtcNow.AddSeconds 5.0

    while log.GrantRecordedRows.Length < expected && DateTime.UtcNow < deadline do
        Thread.Sleep 10

    log.GrantRecordedRows

let private registryOf declarations =
    GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations declarations

/// An `IPermissionStore` whose writes all fail with one message. Stands
/// in for a storage outage AND for a decorator composed outside this one
/// that refused — `grantModuleAccess` must not claim to know which.
type private FailingStore(message: string) =
    interface IPermissionStore with
        member _.GetTeamPermissions _ = async { return TeamPermissions.empty }
        member _.GetEffectivePermissions(_, _) = async { return Map.empty }
        member _.GetModuleExposure _ = async { return Map.empty }
        member _.SetModuleExposure(_, _, _) = async { return Error message }
        member _.SetTeamPermissions(_, _) = async { return Error message }
        member _.SetMemberPermissions(_, _, _, _) = async { return Error message }
        member _.SetTeamDefaults(_, _) = async { return Error message }

let private activeRecord policy justification = {
    State = GrantState.Active
    SatisfiedPolicy = policy
    Justification = justification
    ConsentedBy = None
}

let private pendingRecord policy justification = {
    State = GrantState.PendingConsent
    SatisfiedPolicy = policy
    Justification = justification
    ConsentedBy = None
}

// ─── AI-side fixtures ────────────────────────────────────────────────

let private toolFor (name: string) (sourceModule: string) : RegisteredTool = {
    Definition = {
        Name = name
        Description = "test tool"
        Parameters = []
        SourceModule = sourceModule
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
        EmitsActions = None
    }
    ProviderDef = {
        Name = name
        Description = "test tool"
        InputSchema = "{}"
    }
    Execute = fun _ _ -> async { return "{}" }
}

/// A background-shaped `HttpContext` carrying exactly what
/// `createBackgroundContext` copies forward after Phase 730 — the
/// permission map, the Phase 551 grant stamp, the Phase 552 consent
/// verdicts, and the policy registry in DI.
let private buildContext
    (registry: GrantPolicyGuard.ModuleGrantPolicyRegistry option)
    (perms: (string * ModulePermission list) list)
    (grants: (string * ModuleGrantRecord) list)
    (verdicts: (string * Result<unit, ConsentDenial>) list)
    =
    let services = ServiceCollection()

    match registry with
    | Some r -> services.AddSingleton<GrantPolicyGuard.ModuleGrantPolicyRegistry>(r) |> ignore
    | None -> ()

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Items["ToolUp.UserId"] <- box "alice"

    ctx.Items["ToolUp.StorageScope"] <-
        box {
            ScopeId = "t1"
            Container = "team-t1"
            Persist = true
        }

    if not (List.isEmpty perms) then
        ctx.Items["ToolUp.ModulePermissions"] <- box (Map.ofList perms)

    if not (List.isEmpty grants) then
        ctx.Items[GrantPolicyGuard.ModuleGrantsItemsKey] <- box (Map.ofList grants)

    if not (List.isEmpty verdicts) then
        ctx.Items[GrantConsentStore.ModuleGrantConsentsItemsKey] <- box (Map.ofList verdicts)

    ctx :> HttpContext

let private accessFor (perms: (string * ModulePermission list) list) = {
    UserId = "alice"
    TeamId = Some "t1"
    Subject = TeamMember("alice", "t1")
    ModulePermissions = Map.ofList perms
    ModuleExposure = Map.empty
    PlatformRole = None
}

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 730 — grant-governance completeness" [

        // ── 730.A — the audit twin ───────────────────────────────────
        testList "GrantRecorded — the success half of the trail" [

            testCase "a grant admitted by policy emits exactly one row carrying state, permissions and justification"
            <| fun _ ->
                let log = RecordingAuditLog()
                let registry = registryOf [ "Payroll", GrantPolicy.RequiresAcknowledgement ]

                let store =
                    GrantPolicyGuard.GrantPolicyPermissionStore(freshStore (), registry, log :> IAuditLog, "admin-bob")
                    :> IPermissionStore

                let outcome =
                    GrantPolicyGuard.PermissionGrants.grantModuleAccess store registry {
                        TeamId = "t1"
                        ActorId = "admin-bob"
                        SubjectId = "alice"
                        ModuleName = "Payroll"
                        Permissions = [ Read; Admin ]
                        Evidence = GrantPolicyGuard.GrantEvidence.acknowledged "quarter-end audit"
                    }
                    |> Async.RunSynchronously

                Expect.equal outcome (Ok GrantWriteOutcome.Granted) "the write itself is unchanged"

                let rows = awaitRows log 1

                Expect.hasLength rows 1 "exactly one GrantRecorded row — a grant admitted silently is the defect"

                let row = rows.Head
                Expect.equal row.ActorId "admin-bob" "the administrator who performed it"
                Expect.equal row.SubjectId "alice" "the subject who now holds it"
                Expect.equal row.ModuleName "Payroll" "the module, on the same key the policy was declared under"

                Expect.equal
                    row.DeclaredPolicy
                    (GrantPolicy.toToken GrantPolicy.RequiresAcknowledgement)
                    "the declared policy, joining this row to its refusal twin"

                Expect.equal
                    row.State
                    "active"
                    "acknowledgement is the admin's own ceremony, so the grant is live at once"

                Expect.equal
                    row.Permissions
                    "Admin,Read"
                    "the permissions granted, sorted — a grant of Admin is not the same event as a grant of Read"

                Expect.equal row.Justification "quarter-end audit" "the stated reason the policy demanded"

            testCase "a grant recorded PENDING the subject's acceptance is distinguishable ON THE ROW"
            <| fun _ ->
                // The whole reason `State` is on the payload. Without it,
                // "authority now exists" and "authority is recorded and
                // confers nothing yet" are the same row, and a reviewer
                // reading the trail cannot tell which happened.
                let log = RecordingAuditLog()
                let registry = registryOf [ "Payroll", GrantPolicy.RequiresSubjectConsent ]

                let store =
                    GrantPolicyGuard.GrantPolicyPermissionStore(freshStore (), registry, log :> IAuditLog, "admin-bob")
                    :> IPermissionStore

                let outcome =
                    GrantPolicyGuard.PermissionGrants.grantModuleAccess store registry {
                        TeamId = "t1"
                        ActorId = "admin-bob"
                        SubjectId = "alice"
                        ModuleName = "Payroll"
                        Permissions = [ Read ]
                        Evidence = GrantPolicyGuard.GrantEvidence.acknowledged "onboarding"
                    }
                    |> Async.RunSynchronously

                Expect.equal outcome (Ok(GrantWriteOutcome.RecordedPendingConsent "alice")) "recorded, not live"

                let rows = awaitRows log 1
                Expect.hasLength rows 1 "a pending grant is still a recorded grant and earns a row"
                Expect.equal rows.Head.State "pending-consent" "and the row says so"

            testCase "an AdminDiscretion deployment emits no GrantRecorded row at all (GP 11)"
            <| fun _ ->
                // The symmetric scope decision: this event covers the
                // GOVERNED set, exactly like its refusal twin. Widening it
                // to ordinary permission changes would bury the governed
                // rows in the volume of routine ones — and those already
                // have `PermissionChanged`.
                let log = RecordingAuditLog()
                let registry = registryOf [ "Payroll", GrantPolicy.RequiresAcknowledgement ]

                let store =
                    GrantPolicyGuard.GrantPolicyPermissionStore(freshStore (), registry, log :> IAuditLog, "admin-bob")
                    :> IPermissionStore

                let outcome =
                    GrantPolicyGuard.PermissionGrants.grantModuleAccess store registry {
                        TeamId = "t1"
                        ActorId = "admin-bob"
                        SubjectId = "alice"
                        // NOT the policy-bearing module.
                        ModuleName = "MoodJournal"
                        Permissions = [ Read ]
                        Evidence = GrantPolicyGuard.GrantEvidence.none
                    }
                    |> Async.RunSynchronously

                Expect.equal outcome (Ok GrantWriteOutcome.Granted) "an undeclared module grants exactly as before"

                // Give a stray row time to appear before concluding none
                // did — asserting an empty list immediately would pass even
                // if the emission were merely slow.
                let rows = awaitRows log 1
                Expect.isEmpty rows "an undeclared module's audit stream is byte-for-byte its pre-730 self"

            testCase "no row is emitted when the write did not commit"
            <| fun _ ->
                // A row claiming a grant the store then refused is worse
                // than no row: it asserts authority that does not exist.
                let log = RecordingAuditLog()
                let registry = registryOf [ "Payroll", GrantPolicy.RequiresAcknowledgement ]

                let store =
                    GrantPolicyGuard.GrantPolicyPermissionStore(
                        FailingStore "STORAGE-DOWN: the blob backend is unreachable." :> IPermissionStore,
                        registry,
                        log :> IAuditLog,
                        "admin-bob"
                    )
                    :> IPermissionStore

                GrantPolicyGuard.PermissionGrants.grantModuleAccess store registry {
                    TeamId = "t1"
                    ActorId = "admin-bob"
                    SubjectId = "alice"
                    ModuleName = "Payroll"
                    Permissions = [ Read ]
                    Evidence = GrantPolicyGuard.GrantEvidence.acknowledged "quarter-end audit"
                }
                |> Async.RunSynchronously
                |> ignore

                let rows = awaitRows log 1
                Expect.isEmpty rows "the write failed, so no authority came into existence and no row claims it did"
        ]

        // ── 730.B / 730.C — the honest return surface ────────────────
        testList "grantModuleAccess classifies inner failures instead of relabelling them" [

            testCase "a storage failure surfaces as StoreUnavailable carrying the store's own message"
            <| fun _ ->
                let registry = registryOf [ "Payroll", GrantPolicy.RequiresAcknowledgement ]
                let message = "STORAGE-DOWN: the blob backend is unreachable."

                let store =
                    GrantPolicyGuard.GrantPolicyPermissionStore(
                        FailingStore message :> IPermissionStore,
                        registry,
                        GrantPolicyGuard.CounterpartyConsentOracle.denyAll,
                        None,
                        Some "admin-bob"
                    )
                    :> IPermissionStore

                let outcome =
                    GrantPolicyGuard.PermissionGrants.grantModuleAccess store registry {
                        TeamId = "t1"
                        ActorId = "admin-bob"
                        SubjectId = "alice"
                        ModuleName = "Payroll"
                        Permissions = [ Read ]
                        Evidence = GrantPolicyGuard.GrantEvidence.acknowledged "quarter-end audit"
                    }
                    |> Async.RunSynchronously

                match outcome with
                | Error(GrantRefusal.StoreUnavailable(m, msg)) ->
                    Expect.equal m "Payroll" "naming the module"

                    Expect.equal
                        msg
                        message
                        "and carrying the inner message VERBATIM — whatever the store had to say survives"
                | other ->
                    failtestf
                        "expected StoreUnavailable; got %A. UnbackedGrant here was the Phase 551 defect: it asserts a missing grant record, and the record was written with the entry."
                        other

            testCase "a dual-control QUEUED write surfaces as QueuedForApproval, not as a refusal"
            <| fun _ ->
                // The motivating instance. Phase 555 parks a gated write
                // and reports it through `Result<unit, string>` — the only
                // channel `IPermissionStore` has — so 551 read it as an
                // error and relabelled it "unbacked grant". It is neither
                // unbacked nor, properly, a refusal: the act was accepted
                // into a two-person ceremony.
                let registry = registryOf [ "Payroll", GrantPolicy.RequiresAcknowledgement ]

                let queuedMessage =
                    DualControlSignal.message "req-42" (DateTimeOffset.UtcNow.AddHours 24.0)

                let store =
                    GrantPolicyGuard.GrantPolicyPermissionStore(
                        FailingStore queuedMessage :> IPermissionStore,
                        registry,
                        GrantPolicyGuard.CounterpartyConsentOracle.denyAll,
                        None,
                        Some "admin-bob"
                    )
                    :> IPermissionStore

                let outcome =
                    GrantPolicyGuard.PermissionGrants.grantModuleAccess store registry {
                        TeamId = "t1"
                        ActorId = "admin-bob"
                        SubjectId = "alice"
                        ModuleName = "Payroll"
                        Permissions = [ Read ]
                        Evidence = GrantPolicyGuard.GrantEvidence.acknowledged "quarter-end audit"
                    }
                    |> Async.RunSynchronously

                Expect.equal
                    outcome
                    (Ok(GrantWriteOutcome.QueuedForApproval "req-42"))
                    "parked, on the Ok side, naming the request an approver has to act on"
        ]

        testList "DualControlSignal — one constant, both directions" [

            testCase "a message minted by the gate round-trips to its request id"
            <| fun _ ->
                let expires = DateTimeOffset.UtcNow.AddHours 12.0
                let minted = DualControlSignal.message "req-abc-123" expires

                Expect.stringStarts minted DualControlSignal.Code "the greppable discriminator leads"

                Expect.equal
                    (DualControlSignal.tryParseRequestId minted)
                    (Some "req-abc-123")
                    "and the id an approver needs comes back out"

            testCase "no other error text is mistaken for a parked write"
            <| fun _ ->
                // The recognition has to be narrow in BOTH directions. A
                // storage error that happens to mention dual control must
                // not be read as "queued" — a caller would then route an
                // operator to approve a request that does not exist.
                let notQueued = [
                    ""
                    "STORAGE-DOWN: the blob backend is unreachable."
                    "GRANT-POLICY-UNBACKED-GRANT: module 'Payroll' ..."
                    "the write is pending DUAL-CONTROL-PENDING-APPROVAL somewhere in the middle"
                    // The right prefix and no quoted id: actionable only if
                    // an approver can be named, so this is not a parked
                    // verdict either.
                    DualControlSignal.Code + ": the write did not apply."
                ]

                for text in notQueued do
                    Expect.isNone (DualControlSignal.tryParseRequestId text) $"must not parse: '{text}'"
        ]

        // ── 730.D — the AI dispatch gate ─────────────────────────────
        testList "AI tool dispatch consults grant policy and consent" [

            testCase "a module whose grant is PENDING the subject's acceptance is not listed to the model"
            <| fun _ ->
                let registry = registryOf [ "Payroll", GrantPolicy.RequiresSubjectConsent ]

                let ctx =
                    buildContext (Some registry) [ "Payroll", [ Read ]; "MoodJournal", [ Read ] ] [
                        "Payroll", pendingRecord GrantPolicy.RequiresSubjectConsent "onboarding"
                    ] []

                let toolRegistry = AIToolRegistry()
                toolRegistry.RegisterAll [ toolFor "payroll_summary" "Payroll" ]
                toolRegistry.RegisterAll [ toolFor "mood_summary" "MoodJournal" ]

                let access = accessFor [ "Payroll", [ Read ]; "MoodJournal", [ Read ] ]

                let listed =
                    toolRegistry.ListAccessible(access, moduleGrantGate ctx)
                    |> List.map _.Definition.Name
                    |> List.sort

                Expect.equal
                    listed
                    [ "mood_summary" ]
                    "the permission entry EXISTS for Payroll — that is what a pending grant means — so the RBAC filter alone admits it"

            testCase "an Active, consented module is listed exactly as before"
            <| fun _ ->
                // The positive half, against the same registry and the same
                // stamps. A gate that filtered everything would pass the
                // case above and fail this one.
                let registry = registryOf [ "Payroll", GrantPolicy.RequiresSubjectConsent ]

                let ctx =
                    buildContext (Some registry) [ "Payroll", [ Read ]; "MoodJournal", [ Read ] ] [
                        "Payroll", activeRecord GrantPolicy.RequiresSubjectConsent "onboarding"
                    ] []

                let toolRegistry = AIToolRegistry()
                toolRegistry.RegisterAll [ toolFor "payroll_summary" "Payroll" ]
                toolRegistry.RegisterAll [ toolFor "mood_summary" "MoodJournal" ]

                let access = accessFor [ "Payroll", [ Read ]; "MoodJournal", [ Read ] ]

                let listed =
                    toolRegistry.ListAccessible(access, moduleGrantGate ctx)
                    |> List.map _.Definition.Name
                    |> List.sort

                Expect.equal listed [ "mood_summary"; "payroll_summary" ] "a live grant is unchanged"

            testCase "a counterparty module whose consent was REVOKED is not listed"
            <| fun _ ->
                // Composes with Phase 552: the grant record is Active and
                // adequate, and the consent verdict is what withdraws the
                // authority. Both halves are required, so failing either
                // hides the module.
                let policy = GrantPolicy.RequiresCounterpartyApproval acmeDpo
                let registry = registryOf [ "Payroll", policy ]

                let ctx =
                    buildContext (Some registry) [ "Payroll", [ Read ] ] [
                        "Payroll", activeRecord policy "data-sharing agreement"
                    ] [ "Payroll", Error ConsentDenial.Revoked ]

                let toolRegistry = AIToolRegistry()
                toolRegistry.RegisterAll [ toolFor "payroll_summary" "Payroll" ]

                let listed =
                    toolRegistry.ListAccessible(accessFor [ "Payroll", [ Read ] ], moduleGrantGate ctx)

                Expect.isEmpty
                    listed
                    "the record is Active and adequate; the consent is what was withdrawn, and it is enough on its own"

            testCase "the same counterparty module IS listed while consent stands"
            <| fun _ ->
                let policy = GrantPolicy.RequiresCounterpartyApproval acmeDpo
                let registry = registryOf [ "Payroll", policy ]

                let ctx =
                    buildContext (Some registry) [ "Payroll", [ Read ] ] [
                        "Payroll", activeRecord policy "data-sharing agreement"
                    ] [ "Payroll", Ok() ]

                let toolRegistry = AIToolRegistry()
                toolRegistry.RegisterAll [ toolFor "payroll_summary" "Payroll" ]

                Expect.hasLength
                    (toolRegistry.ListAccessible(accessFor [ "Payroll", [ Read ] ], moduleGrantGate ctx))
                    1
                    "both halves present ⇒ admitted"

            testCase "a deployment declaring no GrantPolicy sees the pre-730 list, in the pre-730 order (GP 11)"
            <| fun _ ->
                // No registry in DI at all — the shape of every deployment
                // that has not adopted Phase 551. One failed `GetService`,
                // a constant `true`, and `ListAccessible` is what it was.
                let ctx = buildContext None [ "Payroll", [ Read ]; "MoodJournal", [ Read ] ] [] []

                let toolRegistry = AIToolRegistry()
                toolRegistry.RegisterAll [ toolFor "payroll_summary" "Payroll" ]
                toolRegistry.RegisterAll [ toolFor "mood_summary" "MoodJournal" ]

                let access = accessFor [ "Payroll", [ Read ]; "MoodJournal", [ Read ] ]

                Expect.equal
                    (toolRegistry.ListAccessible(access, moduleGrantGate ctx)
                     |> List.map _.Definition.Name)
                    (toolRegistry.ListAccessible access |> List.map _.Definition.Name)
                    "gated and ungated agree exactly when nothing is declared"

            testCase "the dispatch-time guard refuses a pending module AND emits the seam's own audit row"
            <| fun _ ->
                // The boundary, not the ergonomic filter. Reaching here
                // means a forged tool name or a replayed history from when
                // the grant was live — which is precisely the case a
                // revocation has to survive. `UnconsentedGrantRefused` is
                // the same event the Remoting seam emits for the same
                // refusal; a second event type would put it on a different
                // row under a different subject.
                let log = RecordingAuditLog()
                let registry = registryOf [ "Payroll", GrantPolicy.RequiresSubjectConsent ]

                let services = ServiceCollection()

                services.AddSingleton<GrantPolicyGuard.ModuleGrantPolicyRegistry>(registry)
                |> ignore

                services.AddSingleton<IAuditLog>(log :> IAuditLog) |> ignore

                let ctx = DefaultHttpContext()
                ctx.RequestServices <- services.BuildServiceProvider()
                ctx.Items["ToolUp.UserId"] <- box "alice"

                ctx.Items["ToolUp.StorageScope"] <-
                    box {
                        ScopeId = "t1"
                        Container = "team-t1"
                        Persist = true
                    }

                ctx.Items[GrantPolicyGuard.ModuleGrantsItemsKey] <-
                    box (Map.ofList [ "Payroll", pendingRecord GrantPolicy.RequiresSubjectConsent "onboarding" ])

                let access = accessFor [ "Payroll", [ Read ] ]

                Expect.isFalse
                    (guardToolGrant (ctx :> HttpContext) access "Payroll")
                    "refused before the executor is reached"

                let deadline = DateTime.UtcNow.AddSeconds 5.0

                while List.isEmpty log.Recorded && DateTime.UtcNow < deadline do
                    Thread.Sleep 10

                let refusals =
                    log.Recorded
                    |> List.choose (function
                        | _, UnconsentedGrantRefused p -> Some p
                        | _ -> None)

                Expect.hasLength refusals 1 "and audited — a guard that refuses silently covers half the requirement"
                Expect.equal refusals.Head.ModuleName "Payroll" "naming the module"
                Expect.equal refusals.Head.UserId "alice" "and the subject who reached for it"

            testCase "the dispatch guard admits a live grant, and audits nothing"
            <| fun _ ->
                let log = RecordingAuditLog()
                let registry = registryOf [ "Payroll", GrantPolicy.RequiresSubjectConsent ]

                let services = ServiceCollection()

                services.AddSingleton<GrantPolicyGuard.ModuleGrantPolicyRegistry>(registry)
                |> ignore

                services.AddSingleton<IAuditLog>(log :> IAuditLog) |> ignore

                let ctx = DefaultHttpContext()
                ctx.RequestServices <- services.BuildServiceProvider()
                ctx.Items["ToolUp.UserId"] <- box "alice"

                ctx.Items[GrantPolicyGuard.ModuleGrantsItemsKey] <-
                    box (Map.ofList [ "Payroll", activeRecord GrantPolicy.RequiresSubjectConsent "onboarding" ])

                Expect.isTrue
                    (guardToolGrant (ctx :> HttpContext) (accessFor [ "Payroll", [ Read ] ]) "Payroll")
                    "a live grant dispatches"

                Thread.Sleep 100
                Expect.isEmpty log.Recorded "and produces no refusal row"
        ]

        // ── the shared decision ──────────────────────────────────────
        testList "the list filter and the audited guard share ONE decision" [

            testCase "dispatchVerdict and guardDispatchWithConsent agree on every arm"
            <| fun _ ->
                // The property that makes the split safe. Two gates that
                // must agree should share their whole decision, not most
                // of it — a module the filter hides but the boundary would
                // admit is a bug in one direction, and the reverse is a
                // bug in the other.
                let policy = GrantPolicy.RequiresCounterpartyApproval acmeDpo

                let registry =
                    registryOf [ "Payroll", policy; "Ledger", GrantPolicy.RequiresSubjectConsent ]

                let cases = [
                    "no record at all", Map.empty, Map.empty
                    "pending record",
                    Map.ofList [ "Ledger", pendingRecord GrantPolicy.RequiresSubjectConsent "x" ],
                    Map.empty
                    "active record",
                    Map.ofList [ "Ledger", activeRecord GrantPolicy.RequiresSubjectConsent "x" ],
                    Map.empty
                    "counterparty, consent revoked",
                    Map.ofList [ "Payroll", activeRecord policy "x" ],
                    Map.ofList [ "Payroll", Error ConsentDenial.Revoked ]
                    "counterparty, consent live",
                    Map.ofList [ "Payroll", activeRecord policy "x" ],
                    Map.ofList [ "Payroll", Ok() ]
                    "counterparty, consent live but no record", Map.empty, Map.ofList [ "Payroll", Ok() ]
                ]

                for label, grants, verdicts in cases do
                    for moduleName in [ "Payroll"; "Ledger"; "Undeclared" ] do
                        let filterSays =
                            GrantConsentStore.dispatchVerdict registry grants verdicts moduleName
                            |> Result.isOk

                        let guardSays =
                            GrantConsentStore.guardDispatchWithConsent
                                registry
                                grants
                                verdicts
                                None
                                Async.RunSynchronously
                                "t1"
                                "alice"
                                moduleName
                            |> Result.isOk

                        Expect.equal filterSays guardSays $"{label} / {moduleName} — the two must never disagree"
        ]
    ]