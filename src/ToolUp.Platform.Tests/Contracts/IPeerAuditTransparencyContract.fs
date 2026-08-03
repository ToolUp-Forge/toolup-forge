module ToolUp.Platform.Tests.Contracts.IPeerAuditTransparencyContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform

// ─── Phase 18a — cross-deployment audit transparency contract pack ───
//
// Exercises the audit-transparency contract host's two jobs:
//   1. the pure `project` projection (scoping + filters + ordering + cap);
//   2. the bespoke context-aware `registration` dispatch, including the
//      security-critical property that a peer reads ONLY the rows where it
//      was the validated caller, and that the contract round-trips through
//      the typed proxy over a loopback transport.
//
// The scoping is enforced from `PeerCallContext.Peer` — which the real
// JSON-RPC host overwrites with the authenticated `PeerPrincipal.Caller`
// before dispatch — so driving `Handle` / the dispatch with an explicit
// caller context here is a faithful stand-in for the validated identity.

let private buyer = {
    PeerId = "buyer-acme"
    DisplayName = "Buyer Acme"
}

let private rival = {
    PeerId = "rival-corp"
    DisplayName = "Rival Corp"
}

let private v1 = PeerAudit.v1

/// A fixed clock so ordering / `SinceUtc` assertions are deterministic.
let private t (minutes: int) : DateTimeOffset =
    DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero).AddMinutes(float minutes)

let private mkRow
    (caller: string)
    (contractId: string)
    (methodName: string)
    (succeeded: bool)
    (outcome: string)
    (occurredAt: DateTimeOffset)
    : AuditEvent =
    PeerCallCompleted {
        ContractId = contractId
        MethodName = methodName
        CallerPeerId = caller
        RootRequestId = Guid.NewGuid().ToString()
        Succeeded = succeeded
        Outcome = outcome
        OccurredAt = occurredAt
    }

/// Phase 310 — a *terminal* row for a long-running call. Takes the
/// correlation id explicitly, because the whole point of the pair is that
/// the terminal row and its schedule-time twin share one.
let private mkJobRow
    (caller: string)
    (contractId: string)
    (methodName: string)
    (rootRequestId: string)
    (succeeded: bool)
    (outcome: string)
    (occurredAt: DateTimeOffset)
    : AuditEvent =
    PeerJobCompleted {
        ContractId = contractId
        MethodName = methodName
        CallerPeerId = caller
        RootRequestId = rootRequestId
        JobId = Guid.NewGuid()
        Succeeded = succeeded
        Outcome = outcome
        OccurredAt = occurredAt
    }

/// Seed: three buyer rows (one failure) + two rival rows, plus a
/// non-peer audit event that must never appear in a peer audit answer.
let private seed = [
    mkRow "buyer-acme" "buyer-seller" "GetReach" true "ok" (t 10)
    mkRow "buyer-acme" "buyer-seller" "SubmitSpec" false "PeerHandler" (t 20)
    mkRow "buyer-acme" "clean-room" "RunQuery" true "ok" (t 30)
    mkRow "rival-corp" "buyer-seller" "GetReach" true "ok" (t 25)
    mkRow "rival-corp" "clean-room" "RunQuery" false "PeerUnauthorized" (t 35)
]

/// Phase 310 — one long-running call per peer, each as the pair of rows it
/// really produces: the schedule-time row (always `ok` — the receiver
/// accepted the call) and the terminal row recording how the computation
/// actually ended. The buyer's failed; the rival's succeeded, so a
/// `FailuresOnly` answer that leaked across peers would be visible.
let private longRunningCorrelationId = "root-long-running-buyer"

let private longRunningSeed = [
    mkRow "buyer-acme" "clean-room" "RunCohort" true "ok" (t 40)
    mkJobRow "buyer-acme" "clean-room" "RunCohort" longRunningCorrelationId false "PeerHandler" (t 55)
    mkRow "rival-corp" "clean-room" "RunCohort" true "ok" (t 41)
    mkJobRow "rival-corp" "clean-room" "RunCohort" "root-long-running-rival" true "ok" (t 56)
]

/// In-memory `IAuditLog` returning the seeded trail. Honours the
/// `eventType` filter exactly the way the real `DefaultAuditLog` does, so
/// the host's per-event-type reads are genuinely exercised — including
/// (Phase 310) the second read for the terminal rows.
type private StubAuditLog(events: AuditEvent list) =
    interface IAuditLog with
        member _.Record(_scopeId, _audit) = async { return () }

        member _.GetAuditTrail(_scopeId, _dateRange, eventType) = async {
            return
                match eventType with
                | Some "PeerCallCompleted" ->
                    events
                    |> List.filter (fun e ->
                        match e with
                        | PeerCallCompleted _ -> true
                        | _ -> false)
                | Some "PeerJobCompleted" ->
                    events
                    |> List.filter (fun e ->
                        match e with
                        | PeerJobCompleted _ -> true
                        | _ -> false)
                | Some _ -> []
                | None -> events
        }

let private allQuery: PeerAuditQuery = {
    ContractId = None
    MethodName = None
    SinceUtc = None
    FailuresOnly = false
    Limit = 0 // exercises the defaultLimit fallback
}

/// A call context whose `Peer` is the validated caller (the host sets this
/// from the authenticated principal). Version / route / hops are fixed —
/// the audit contract doesn't read them.
let private ctxFor (peer: PeerIdentity) : PeerCallContext = {
    Peer = peer
    User = Anonymous
    ContractVersion = v1
    Route = [ peer.PeerId ]
    RootRequestId = Guid.NewGuid().ToString()
    ParentRequestId = None
    HopsRemaining = 8
}

/// Marshal a single positional `PeerAuditQuery` argument the same way the
/// typed proxy does (a JSON array with one element).
let private argsFor (query: PeerAuditQuery) : string = JsonRpc.serialize [ query ]

/// In-process `IPeerClient` routing a typed proxy call straight to a
/// receiver's `Handle` — mirrors the foundation pack's loopback client.
type private LoopbackPeerClient(peer: IPlatformPeer) =
    interface IPeerClient with
        member _.Invoke(_target, contractId, methodName, payload, ?_cancellationToken) =
            peer.Handle(contractId, payload.Context, methodName, payload.Arguments)

        member _.PollJob(_target, _contractId, _jobId, ?_cancellationToken) = async {
            return Error(PeerTransport "loopback client does not poll jobs")
        }

let tests =
    testList "IPeerAuditTransparencyContract" [

        // ─── project: scoping ─────────────────────────────────────

        testCase "project returns only the caller's own rows"
        <| fun _ ->
            let entries = PeerAuditContractHost.project "buyer-acme" allQuery seed
            Expect.equal entries.Length 3 "buyer made three calls"

            Expect.isTrue
                (entries
                 |> List.forall (fun e -> e.ContractId = "buyer-seller" || e.ContractId = "clean-room"))
                "every returned row is one of the buyer's own calls"

        testCase "project never leaks another peer's rows"
        <| fun _ ->
            // Rival has rows in the trail, but a buyer query must not see
            // them — the security property, asserted by exclusion.
            let entries = PeerAuditContractHost.project "buyer-acme" allQuery seed

            Expect.isFalse
                (entries |> List.exists (fun e -> e.Outcome = "PeerUnauthorized"))
                "the rival's PeerUnauthorized row never appears in the buyer's answer"

        testCase "project for an unknown caller returns nothing"
        <| fun _ ->
            let entries = PeerAuditContractHost.project "stranger" allQuery seed
            Expect.isEmpty entries "a peer with no recorded calls sees an empty trail"

        // ─── project: ordering + filters + cap ────────────────────

        testCase "project orders newest-first"
        <| fun _ ->
            let entries = PeerAuditContractHost.project "buyer-acme" allQuery seed
            let times = entries |> List.map _.OccurredAt
            Expect.equal times (times |> List.sortDescending) "rows are reverse-chronological"
            Expect.equal entries.Head.OccurredAt (t 30) "the most recent buyer call leads"

        testCase "project honours the ContractId filter"
        <| fun _ ->
            let q = {
                allQuery with
                    ContractId = Some "clean-room"
            }

            let entries = PeerAuditContractHost.project "buyer-acme" q seed
            Expect.equal entries.Length 1 "only the clean-room call matches"
            Expect.equal entries.Head.MethodName "RunQuery" "the matching row is the clean-room RunQuery"

        testCase "project honours FailuresOnly"
        <| fun _ ->
            let q = { allQuery with FailuresOnly = true }
            let entries = PeerAuditContractHost.project "buyer-acme" q seed
            Expect.equal entries.Length 1 "only the failed buyer call matches"
            Expect.isFalse entries.Head.Succeeded "the matched row is the failure"

        testCase "project honours SinceUtc"
        <| fun _ ->
            let q = { allQuery with SinceUtc = Some(t 15) }
            let entries = PeerAuditContractHost.project "buyer-acme" q seed
            Expect.equal entries.Length 2 "only rows at/after t+15 match"
            Expect.isTrue (entries |> List.forall (fun e -> e.OccurredAt >= t 15)) "no row predates the lower bound"

        testCase "project clamps Limit to maxLimit"
        <| fun _ ->
            let many =
                List.init (PeerAudit.maxLimit + 50) (fun i -> mkRow "buyer-acme" "c" "m" true "ok" (t i))

            let q = {
                allQuery with
                    Limit = PeerAudit.maxLimit + 50
            }

            let entries = PeerAuditContractHost.project "buyer-acme" q many

            Expect.equal
                entries.Length
                PeerAudit.maxLimit
                "the response is capped at maxLimit regardless of the request"

        testCase "project applies a positive Limit under the cap"
        <| fun _ ->
            let q = { allQuery with Limit = 2 }
            let entries = PeerAuditContractHost.project "buyer-acme" q seed
            Expect.equal entries.Length 2 "the explicit limit is honoured"

        // ─── registration dispatch: method guard ──────────────────

        testCaseAsync "dispatch rejects a method other than QueryCalls"
        <| async {
            let reg = PeerAuditContractHost.registration (StubAuditLog seed)
            let! result = reg.Dispatch (ctxFor buyer) "Nope" (argsFor allQuery)

            match result with
            | Error(PeerMethodNotFound m) -> Expect.equal m "Nope" "the unknown method name is reported"
            | other -> failtestf "Expected PeerMethodNotFound, got %A" other
        }

        // ─── registration dispatch: scoped read end-to-end ────────

        testCaseAsync "dispatch returns the caller-scoped, serialised projection"
        <| async {
            let reg = PeerAuditContractHost.registration (StubAuditLog seed)
            let! result = reg.Dispatch (ctxFor buyer) PeerAudit.queryMethod (argsFor allQuery)

            match result with
            | Ok json ->
                let entries = JsonRpc.deserialize<PeerAuditEntry list> json
                Expect.equal entries.Length 3 "the buyer sees exactly its three rows over the wire"
            | Error e -> failtestf "Expected Ok, got Error %A" e
        }

        testCaseAsync "dispatch scopes per validated caller identity"
        <| async {
            // Same audit log, two callers — each sees only its own rows.
            let reg = PeerAuditContractHost.registration (StubAuditLog seed)

            let! buyerResult = reg.Dispatch (ctxFor buyer) PeerAudit.queryMethod (argsFor allQuery)
            let! rivalResult = reg.Dispatch (ctxFor rival) PeerAudit.queryMethod (argsFor allQuery)

            match buyerResult, rivalResult with
            | Ok bJson, Ok rJson ->
                let bEntries = JsonRpc.deserialize<PeerAuditEntry list> bJson
                let rEntries = JsonRpc.deserialize<PeerAuditEntry list> rJson
                Expect.equal bEntries.Length 3 "buyer sees three"
                Expect.equal rEntries.Length 2 "rival sees two — its own only"
            | other -> failtestf "Expected both Ok, got %A" other
        }

        // ─── registration: typed proxy round-trip over loopback ───

        testCaseAsync "typed IPeerAuditApi proxy round-trips QueryCalls over loopback"
        <| async {
            let peer = DefaultPlatformPeer() :> IPlatformPeer
            peer.RegisterContract(PeerAuditContractHost.registration (StubAuditLog seed))

            let proxy =
                JsonRpcPeerClient.create<IPeerAuditApi> {
                    Client = LoopbackPeerClient peer
                    Target = {
                        Peer = {
                            PeerId = "seller"
                            DisplayName = "Seller"
                        }
                        BaseUrl = "loopback"
                    }
                    Caller = buyer
                    User = Anonymous
                    Version = v1
                    ContractId = PeerAudit.contractId
                    HopBudget = 8
                }

            let! entries = proxy.QueryCalls { allQuery with FailuresOnly = true }
            Expect.equal entries.Length 1 "the typed proxy resolves the buyer's single failed call"
            Expect.equal entries.Head.Outcome "PeerHandler" "the failure outcome label round-trips typed"
        }

        // ─── Phase 310 — terminal rows join the transparency read ─

        testCase "project returns both the schedule-time and terminal rows for a long-running call"
        <| fun _ ->
            let entries = PeerAuditContractHost.project "buyer-acme" allQuery longRunningSeed
            Expect.equal entries.Length 2 "the buyer sees the call it made and how it ended"

            Expect.isTrue
                (entries |> List.forall (fun e -> e.MethodName = "RunCohort"))
                "both rows describe the same long-running method"

        testCase "the terminal row correlates to its schedule-time row"
        <| fun _ ->
            let entries = PeerAuditContractHost.project "buyer-acme" allQuery longRunningSeed

            let terminal =
                entries |> List.find (fun e -> e.RootRequestId = longRunningCorrelationId)

            Expect.isFalse terminal.Succeeded "the terminal row carries the real failure"

            Expect.equal
                terminal.Outcome
                "PeerHandler"
                "the terminal row reports the failing PeerError case, not the schedule-time ok"

        testCase "FailuresOnly surfaces a long-running failure the schedule-time row reported as ok"
        <| fun _ ->
            // The defect this phase closes: before the terminal row
            // existed, this query answered EMPTY for a call that failed —
            // `peer.Handle` had returned `Ok jobId`, so the only row said
            // `ok`. It now returns exactly one row, and exactly the right
            // one (no double-count from the schedule-time twin).
            let q = { allQuery with FailuresOnly = true }
            let entries = PeerAuditContractHost.project "buyer-acme" q longRunningSeed

            Expect.equal entries.Length 1 "one failure, counted once"

            Expect.equal
                entries.Head.RootRequestId
                longRunningCorrelationId
                "the returned failure is the buyer's own terminal outcome"

        testCase "project never leaks another peer's terminal rows"
        <| fun _ ->
            let entries = PeerAuditContractHost.project "buyer-acme" allQuery longRunningSeed

            Expect.isFalse
                (entries |> List.exists (fun e -> e.RootRequestId = "root-long-running-rival"))
                "the rival's terminal row is scoped out by the same caller check as its schedule-time row"

        testCaseAsync "dispatch reads the terminal rows as well as the schedule-time rows"
        <| async {
            // Guards the host's second `GetAuditTrail` read: the stub
            // answers `Some "PeerJobCompleted"` only when it is asked, so a
            // host that still made one read would return half the answer.
            let reg = PeerAuditContractHost.registration (StubAuditLog longRunningSeed)
            let! result = reg.Dispatch (ctxFor buyer) PeerAudit.queryMethod (argsFor allQuery)

            match result with
            | Ok json ->
                let entries = JsonRpc.deserialize<PeerAuditEntry list> json
                Expect.equal entries.Length 2 "the buyer sees both halves of its long-running call over the wire"

                Expect.isTrue
                    (entries |> List.exists (fun e -> e.RootRequestId = longRunningCorrelationId))
                    "the terminal row survives the serialised round trip"
            | Error e -> failtestf "Expected Ok, got Error %A" e
        }
    ]