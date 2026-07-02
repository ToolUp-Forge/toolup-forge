module ToolUp.Platform.Tests.InProcess.HostActionAuditTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.HostActionAuditHook

// ─── Phase 272 — hosted-tree action audit emission tests (GP 6) ───────
//
// Every authorized (and every DENIED) hosted-tree action must leave a
// HostActionDispatched audit row keyed on the neutral ActionDescriptor +
// the decision. This pack pins:
//   * an authorized dispatch / call / invoke each emits the row with the
//     right principal / descriptor / scope, under the action's own scope;
//   * a denied action audits the denial (Allowed = false + reason);
//   * authorize-then-audit (authorizeAndAudit) is one path — the decision
//     is returned AND recorded;
//   * the disabled hook records nothing (GP 13);
//   * the HostActionDispatched event round-trips through the Phase 114
//     codec registry (registered + exhaustiveness-covered).

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Capturing `IAuditLog` recording every `(scopeId, HostActionDispatchedPayload)`.
type private CapturingAuditLog() =
    let rows = ConcurrentQueue<string * HostActionDispatchedPayload>()

    member _.Rows = rows |> List.ofSeq

    interface IAuditLog with
        member _.Record(scopeId, audit) = async {
            match audit with
            | HostActionDispatched p -> rows.Enqueue(scopeId, p)
            | _ -> ()
        }

        member _.GetAuditTrail(_, _, _) = async { return [] }

let private run a = a |> Async.RunSynchronously

let private teamCtx (userId: string) (teamId: string) : AccessContext = {
    UserId = userId
    TeamId = Some teamId
    Subject = TeamMember(userId, teamId)
    ModulePermissions = Map.empty
    ModuleExposure = Map.empty
    PlatformRole = None
}

let private action kind target scope : ActionDescriptor = {
    Kind = kind
    Target = target
    Scope = scope
}

let private mkHook (audit: CapturingAuditLog) : IHostActionAuditHook =
    HostActionAuditHook(audit :> IAuditLog, silentLogger) :> IHostActionAuditHook

let tests =
    testList "HostActionAudit (Phase 272)" [

        testCase "an authorized dispatch / call / invoke each emits one row with the right descriptor + scope"
        <| fun _ ->
            let audit = CapturingAuditLog()
            let hook = mkHook audit
            let principal = TeamMember("alice", "t1")

            for kind, target in
                [
                    "dispatch", "reports/refresh"
                    "call", "ReportApi.get"
                    "invoke", "host.clipboard.write"
                ] do
                hook.RecordAction principal (action kind target (Some "t1")) AuthorizationDecision.Allow
                |> run

            Expect.hasLength audit.Rows 3 "one row per authorized action"

            for (scopeId, p) in audit.Rows do
                Expect.equal scopeId "t1" "written under the action's own scope"
                Expect.isTrue p.Allowed "an authorized action records Allowed = true"
                Expect.equal p.SubjectKind "team" "subject kind carried"
                Expect.equal p.SubjectId (Some "alice") "subject id carried"
                Expect.equal p.ScopeId (Some "t1") "descriptor scope carried"

            let kinds = audit.Rows |> List.map (snd >> _.ActionKind)
            Expect.equal kinds [ "dispatch"; "call"; "invoke" ] "each action kind recorded in order"

            let targets = audit.Rows |> List.map (snd >> _.ActionTarget)
            Expect.equal targets [ "reports/refresh"; "ReportApi.get"; "host.clipboard.write" ] "each target recorded"

        testCase "a denied action audits the denial (the security-relevant case)"
        <| fun _ ->
            let audit = CapturingAuditLog()
            let hook = mkHook audit

            hook.RecordAction
                (TeamMember("bob", "t1"))
                (action "invoke" "host.file.read" (Some "t1"))
                (AuthorizationDecision.Deny "no policy rule covers this (default-deny)")
            |> run

            Expect.hasLength audit.Rows 1 "a denied action is still audited"
            let _, p = audit.Rows.Head
            Expect.isFalse p.Allowed "a denied action records Allowed = false"
            Expect.stringContains p.Reason "default-deny" "the denial reason is carried verbatim"

        testCase "an anonymous principal leaks no subject id, unscoped writes under _platform"
        <| fun _ ->
            let audit = CapturingAuditLog()
            let hook = mkHook audit

            hook.RecordAction (AnonymousSession "sess-9") (action "navigate" "Home" None) AuthorizationDecision.Allow
            |> run

            let scopeId, p = audit.Rows.Head
            Expect.equal scopeId "_platform" "an unscoped action writes under _platform"
            Expect.equal p.SubjectKind "anonymous" "anonymous kind"
            Expect.equal p.SubjectId None "no subject id for anonymous (no PII)"
            Expect.equal p.ScopeId None "descriptor scope is None"

        testCase "authorizeAndAudit is one path — the decision is returned AND recorded"
        <| fun _ ->
            let audit = CapturingAuditLog()
            let hook = mkHook audit
            let ctx = teamCtx "alice" "t1"
            let descriptor = action "invoke" "host.clipboard.write" (Some "t1")

            // Allow path.
            let allowed =
                authorizeAndAudit ActionAuthorizer.allowAll hook ctx.Subject descriptor ctx
                |> run

            match allowed with
            | AuthorizationDecision.Allow -> ()
            | AuthorizationDecision.Deny r -> failtestf "allowAll must return Allow; got: %s" r

            // Deny path.
            let denied =
                authorizeAndAudit ActionAuthorizer.denyAll hook ctx.Subject descriptor ctx
                |> run

            match denied with
            | AuthorizationDecision.Deny _ -> ()
            | AuthorizationDecision.Allow -> failtest "denyAll must return Deny"

            Expect.hasLength audit.Rows 2 "authorize-then-audit records one row per decision"
            Expect.equal (audit.Rows |> List.map (snd >> _.Allowed)) [ true; false ] "allow then deny recorded"

        testCase "the disabled hook records nothing (GP 13)"
        <| fun _ ->
            let audit = CapturingAuditLog()
            let ctx = teamCtx "alice" "t1"

            // disabled hook — authorizeAndAudit is a passthrough that records nothing.
            authorizeAndAudit ActionAuthorizer.allowAll disabled ctx.Subject (action "invoke" "x" None) ctx
            |> run
            |> ignore

            Expect.isEmpty audit.Rows "the disabled hook writes no audit rows"

        testCase "HostActionDispatched round-trips through the Phase 114 codec registry"
        <| fun _ ->
            // Registered + exhaustiveness-covered: serialise → registry-decode
            // yields structural equality (the same gate AuditEventRegistryTests
            // enforces across every case, pinned explicitly here).
            let original =
                HostActionDispatched {
                    SubjectKind = "team"
                    SubjectId = Some "alice"
                    ActionKind = "invoke"
                    ActionTarget = "host.clipboard.write"
                    ScopeId = Some "t1"
                    Allowed = true
                    Reason = "allowed"
                    OccurredAt = DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero)
                }

            let json = AuditLog.serialiseAuditEvent original

            Expect.equal
                (AuditLog.tryDecodeAuditEvent "HostActionDispatched" json)
                (Ok original)
                "HostActionDispatched serialise → registry decode round-trips"

            Expect.equal
                (AuditEvent.eventTypeName original)
                "HostActionDispatched"
                "eventTypeName matches the registry discriminator"
    ]