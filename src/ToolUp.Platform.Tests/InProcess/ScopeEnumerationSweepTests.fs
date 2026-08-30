module ToolUp.Platform.Tests.InProcess.ScopeEnumerationSweepTests

// ─── Phase 723 — scope-enumeration seam + converged restart sweep ────
//
// Phase 509 gave the ingestion queue a durable backing and left two
// consequences outside its lease. This pack pins both, plus the
// convergence that followed from the second.
//
// Four arms:
//
//  • **The seam.** `IScopeEnumerator` answers "what scopes exist". The
//    SDK default adapts `ITeamStore.ListTeams` — and the fake store here
//    THROWS from every other member, which is the actual evidence for the
//    interface decision: no member was added to `ITeamStore`, so an
//    external implementation of it keeps compiling and keeps working.
//
//  • **Recovery off the seam, with no container list.** A document left
//    `Pending` by a simulated dead process is marked `Failed` at startup
//    by a deployment that composed the enumerator and declared NO
//    containers — the case the pre-723 sweeps structurally could not
//    serve, because their only input was the list nobody wrote.
//
//  • **Composing nothing changes nothing (GP 13).** With no enumerator
//    and no explicit scopes the sweep enumerates nothing and touches no
//    surface — asserted against a recording surface that counts reads,
//    not merely against a returned zero, because a sweep that read every
//    scope and marked none would also return zero.
//
//  • **The async enqueue path (723.A).** Against a durable store held
//    open on a gate, `EnqueueAsync` yields to its caller while the store
//    round-trip is outstanding, and the synchronous `Enqueue` the KB
//    upload handlers used to call does not. The gate makes both
//    deterministic — no sleeps, no timing thresholds.

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open DataManagementTypes
open SharedTypes
open ToolUp.RAG.IngestionTypes

let private run = Async.RunSynchronously
let private logger = ConsoleLogger.ConsoleLogger() :> ILogger

// ── A fake ITeamStore that supports ListTeams and NOTHING else ──
//
// Every other member raises. That is the assertion, not a shortcut: if
// Phase 723 had added an abstract member to `ITeamStore` this type would
// not compile, and if the default enumerator reached for anything beyond
// `ListTeams` the test would throw rather than fail quietly.

let private unsupported<'T> (name: string) : 'T =
    raise (NotSupportedException(sprintf "Phase 723's scope enumerator must not call ITeamStore.%s" name))

let private teamStoreListing (teams: string list) =
    { new ITeamStore with
        member _.ListTeams() = async {
            return
                teams
                |> List.map (fun id -> {
                    TeamId = id
                    Name = id
                    CreatedAt = DateTime.UtcNow
                    Archived = false
                })
        }

        member _.CreateTeam(_, _) = unsupported "CreateTeam"
        member _.DeleteTeam(_) = unsupported "DeleteTeam"
        member _.GetTeam(_) = unsupported "GetTeam"
        member _.AddMember(_, _, _) = unsupported "AddMember"
        member _.RemoveMember(_, _) = unsupported "RemoveMember"
        member _.ChangeMemberRole(_, _, _) = unsupported "ChangeMemberRole"
        member _.GetTeamsForUser(_) = unsupported "GetTeamsForUser"
        member _.GetTeamMembers(_) = unsupported "GetTeamMembers"
        member _.GetMemberRole(_, _) = unsupported "GetMemberRole"
        member _.GetActiveTeam(_) = unsupported "GetActiveTeam"
        member _.SetActiveTeam(_, _) = unsupported "SetActiveTeam"
        member _.SetArchived(_, _) = unsupported "SetArchived"
        member _.PurgeTeam(_) = unsupported "PurgeTeam"
        member _.PurgeUser(_) = unsupported "PurgeUser"
    }

// ── A recording surface: proves "touched nothing", not just "marked 0" ──

type private RecordingSurface() =
    let reads = ResizeArray<string>()
    let marks = ResizeArray<string * string>()

    member _.Reads = List.ofSeq reads
    member _.Marks = List.ofSeq marks

    interface IIngestionRecoverySurface with
        member _.Name = "recording"

        member _.ListInterrupted(scope) = async {
            reads.Add scope
            return [ "stuck-doc" ]
        }

        member _.MarkInterrupted(scope, documentId, _reason) = async { marks.Add(scope, documentId) }

// ── 1. the seam ──

let private seamTests =
    testList "IScopeEnumerator" [
        test "the ITeamStore-backed default enumerates team containers plus the well-known ones" {
            let enumerator =
                ScopeEnumeration.fromTeamStore (teamStoreListing [ "alpha"; "beta" ])

            let scopes = run (enumerator.ListScopes())

            Expect.equal enumerator.Name "team-store" "names itself for logs"

            Expect.containsAll
                scopes
                [ "_platform"; "_deployment"; "team-alpha"; "team-beta" ]
                "well-known containers and every team's container"

            Expect.equal scopes.Length 4 "and nothing else"
        }

        test "team ids already carrying the container prefix are not double-prefixed" {
            let enumerator =
                ScopeEnumeration.fromTeamStoreWith [] (teamStoreListing [ "team-alpha" ])

            Expect.equal (run (enumerator.ListScopes())) [ "team-alpha" ] "idempotent prefixing"
        }

        test "duplicate scopes collapse" {
            let enumerator =
                ScopeEnumeration.fromTeamStoreWith [ "team-alpha" ] (teamStoreListing [ "alpha"; "alpha" ])

            Expect.equal (run (enumerator.ListScopes())) [ "team-alpha" ] "a scope is visited once"
        }

        test "a fixed enumeration is available for deployments whose scopes are not teams" {
            let enumerator = ScopeEnumeration.ofScopes "fixed" [ "user-ann" ]
            Expect.equal (run (enumerator.ListScopes())) [ "user-ann" ] "returns what it was given"
        }
    ]

// ── 2. scope resolution ──

let private resolutionTests =
    testList "IngestionRecoverySweep.resolveScopes" [
        test "no enumerator composed ⇒ exactly the explicit scopes" {
            Expect.equal (run (IngestionRecoverySweep.resolveScopes [ "team-a" ] None logger)) [ "team-a" ] "unchanged"
        }

        test "an enumerator with no explicit scopes carries the whole sweep" {
            let scopes =
                run (
                    IngestionRecoverySweep.resolveScopes
                        []
                        (Some(ScopeEnumeration.ofScopes "fixed" [ "team-a" ]))
                        logger
                )

            Expect.equal scopes [ "team-a" ] "the container list is no longer required"
        }

        test "explicit scopes and the enumeration are UNIONED, so a migration loses no coverage" {
            let scopes =
                run (
                    IngestionRecoverySweep.resolveScopes
                        [ "legacy-container"; "team-a" ]
                        (Some(ScopeEnumeration.ofScopes "fixed" [ "team-a"; "team-b" ]))
                        logger
                )

            Expect.containsAll scopes [ "legacy-container"; "team-a"; "team-b" ] "both sources honoured"
            Expect.equal scopes.Length 3 "and the overlap collapses"
        }

        test "a throwing enumerator degrades to the explicit scopes rather than taking startup down" {
            let broken =
                { new IScopeEnumerator with
                    member _.Name = "broken"
                    member _.ListScopes() = async { return failwith "directory unreachable" }
                }

            Expect.equal
                (run (IngestionRecoverySweep.resolveScopes [ "team-a" ] (Some broken) logger))
                [ "team-a" ]
                "the outage is logged, not fatal"
        }
    ]

// ── 3. the converged sweep ──

let private sweepTests =
    testList "IngestionRecoverySweep" [
        test "a document left Pending by a dead process is recovered with NO container list" {
            // The Phase 723 case: the deployment composed the seam and
            // declared no containers at all.
            let store = IngestionStatusStore.createInMemory ()
            run (store.SetPending("team-alpha", "report.pdf", 12))

            let surfaces = [ IngestionRecoverySweep.ofIngestionStatusStore store ]

            let enumerator =
                ScopeEnumeration.fromTeamStoreWith [] (teamStoreListing [ "alpha" ])

            let svc =
                IngestionRecoverySweep.hostedService logger (fun () -> surfaces) (fun () ->
                    IngestionRecoverySweep.resolveScopes [] (Some enumerator) logger)

            svc.StartAsync(CancellationToken.None).Wait()

            match run (store.Get("team-alpha", "report.pdf")) with
            | Some(FileIngestionStatus.Failed reason) ->
                Expect.equal reason IngestionRecoverySweep.InterruptedReason "the shared, user-visible reason"
            | other -> failtestf "expected Failed, got %A" other
        }

        test "the sweep is idempotent — a second start marks nothing" {
            let store = IngestionStatusStore.createInMemory ()
            run (store.SetPending("team-alpha", "report.pdf", 12))
            let surfaces = [ IngestionRecoverySweep.ofIngestionStatusStore store ]

            Expect.equal (run (IngestionRecoverySweep.run surfaces [ "team-alpha" ] logger)) 1 "first start marks it"
            Expect.equal (run (IngestionRecoverySweep.run surfaces [ "team-alpha" ] logger)) 0 "second start marks none"
        }

        test "a terminal status is never re-failed" {
            let store = IngestionStatusStore.createInMemory ()
            run (store.Set("team-alpha", "done.pdf", FileIngestionStatus.Indexed))
            let surfaces = [ IngestionRecoverySweep.ofIngestionStatusStore store ]

            Expect.equal (run (IngestionRecoverySweep.run surfaces [ "team-alpha" ] logger)) 0 "left alone"

            Expect.equal
                (run (store.Get("team-alpha", "done.pdf")))
                (Some FileIngestionStatus.Indexed)
                "status unchanged"
        }

        test "one unreadable scope does not stop the others" {
            let broken =
                { new IIngestionRecoverySurface with
                    member _.Name = "broken"

                    member _.ListInterrupted(scope) = async {
                        if scope = "team-bad" then
                            return failwith "container unreadable"
                        else
                            return [ "doc-1" ]
                    }

                    member _.MarkInterrupted(_, _, _) = async { return () }
                }

            let marked =
                run (IngestionRecoverySweep.run [ broken ] [ "team-bad"; "team-good" ] logger)

            Expect.equal marked 1 "the good scope was still swept"
        }

        test "every registered surface is visited — the two sweeps share one traversal" {
            let ragStore = IngestionStatusStore.createInMemory ()
            run (ragStore.SetPending("team-alpha", "data.csv", 3))

            let blob = InMemoryBlobStorage() :> IBlobStorage

            let stuckDoc = {
                Id = "kb-1"
                FileName = "policy.pdf"
                FileType = "application/pdf"
                UploadedAt = DateTimeOffset.UtcNow
                UploadedBy = "ann"
                Status = IngestionStatus.Embedding(1, 4)
                SizeBytes = 1024L
                ChunkCount = 4
                Source = KnowledgeSource.UploadedFile
                ContentHash = None
                Version = 1
                Tags = []
            }

            run (KnowledgeBase.ServerIndexStorage.saveIndex blob "team-alpha" [ stuckDoc ])

            let surfaces = [
                IngestionRecoverySweep.ofIngestionStatusStore ragStore
                KnowledgeBase.ServerRecovery.documentIndexSurface blob
            ]

            let marked = run (IngestionRecoverySweep.run surfaces [ "team-alpha" ] logger)
            Expect.equal marked 2 "both the RAG per-file status and the KB document index were swept"

            let docs = run (KnowledgeBase.ServerIndexStorage.loadIndex blob "team-alpha")

            match docs |> List.tryHead with
            | Some d ->
                match d.Status with
                | IngestionStatus.Failed reason ->
                    Expect.equal reason IngestionRecoverySweep.InterruptedReason "one reason string, both surfaces"
                | other -> failtestf "expected Failed, got %A" other
            | None -> failtest "the KB index lost its document"
        }
    ]

// ── 4. GP 13 — composing nothing changes nothing ──

let private uncomposedTests =
    testList "uncomposed deployment" [
        test "no enumerator and no explicit scopes ⇒ the surface is never read" {
            let surface = RecordingSurface()

            let svc =
                IngestionRecoverySweep.hostedService
                    logger
                    (fun () -> [ surface :> IIngestionRecoverySurface ])
                    (fun () -> IngestionRecoverySweep.resolveScopes [] None logger)

            svc.StartAsync(CancellationToken.None).Wait()

            Expect.isEmpty surface.Reads "no scope was enumerated, so no scope was read"
            Expect.isEmpty surface.Marks "and nothing was rewritten"
        }

        // The control for the two "recorded nothing" arms around it. Without
        // this, a `RecordingSurface` that never recorded anything at all
        // would satisfy them both — the exact shape of a vacuous green.
        test "control — the recording surface DOES record when a scope is swept" {
            let surface = RecordingSurface()
            Expect.equal (run (IngestionRecoverySweep.run [ surface ] [ "team-a" ] logger)) 1 "one document marked"
            Expect.equal surface.Reads [ "team-a" ] "the read was recorded"
            Expect.equal surface.Marks [ "team-a", "stuck-doc" ] "and so was the mark"
        }

        test "a registered surface with no sweep scope is inert" {
            let surface = RecordingSurface()
            Expect.equal (run (IngestionRecoverySweep.run [ surface ] [] logger)) 0 "nothing to visit"
            Expect.isEmpty surface.Reads "declaring a surface is not an instruction to sweep"
        }

        test "no surfaces registered ⇒ the sweep does not even enumerate" {
            let mutable enumerated = 0

            let svc =
                IngestionRecoverySweep.hostedService logger (fun () -> []) (fun () -> async {
                    enumerated <- enumerated + 1
                    return [ "team-a" ]
                })

            svc.StartAsync(CancellationToken.None).Wait()
            Expect.equal enumerated 0 "a directory round-trip is not paid for by a deployment with nothing to sweep"
        }
    ]

// ── 5. 723.A — the async enqueue path ──

/// A durable store whose append is held open until the test releases it.
/// Deterministic by construction: the enqueue CANNOT complete while the
/// gate is unset, so "did the caller get its thread back" is an
/// assertion rather than a race.
type private GatedStore() =
    let entered = new ManualResetEventSlim(false)
    let gate = TaskCompletionSource<bool>()

    member _.Entered = entered
    member _.Release() = gate.TrySetResult true |> ignore

    interface IIngestionQueueStore with
        member _.Name = "gated"

        member _.Enqueue(_job, _capacity) = async {
            entered.Set()
            return! gate.Task |> Async.AwaitTask
        }

        member _.Claim(_) = async { return None }
        member _.Complete(_) = async { return () }
        member _.Release(_) = async { return () }
        member _.ReclaimExpired() = async { return 0 }
        member _.Depth() = async { return 0 }

let private sampleJob: DocumentIngestionJob = {
    DocumentId = "doc-1"
    DocumentName = "report.pdf"
    Chunks = []
    Scope = VectorScope.Team "alpha"
    ScopeId = "alpha"
    Container = "team-alpha"
    OriginatingUserId = Some "ann"
}

let private enqueueTests =
    testList "durable enqueue" [
        test "EnqueueAsync yields to its caller while the store round-trip is outstanding" {
            let store = GatedStore()
            let queue = IngestionQueue(store = (store :> IIngestionQueueStore))

            let pending = queue.EnqueueAsync sampleJob |> Async.StartAsTask

            // The store has been entered, so the append is genuinely in
            // flight — and the caller is here, not inside it.
            Expect.isTrue (store.Entered.Wait(TimeSpan.FromSeconds 10.0)) "the store append started"
            Expect.isFalse pending.IsCompleted "the enqueue has not completed"

            store.Release()
            Expect.isTrue (pending.Wait(TimeSpan.FromSeconds 10.0)) "and completes once the store answers"
            Expect.isTrue pending.Result "the job was accepted"
        }

        test "the synchronous Enqueue the upload handlers used to call OCCUPIES its thread" {
            // The contrast that makes 723.A concrete rather than
            // stylistic: `Enqueue` is `Async.RunSynchronously` over the
            // same round-trip, so the thread that called it is held for
            // the whole of it. On a request thread, that is the cost.
            let store = GatedStore()
            let queue = IngestionQueue(store = (store :> IIngestionQueueStore))

            let blocked = Task.Run(fun () -> queue.Enqueue sampleJob)

            Expect.isTrue (store.Entered.Wait(TimeSpan.FromSeconds 10.0)) "the store append started"
            Expect.isFalse (blocked.Wait 250) "the calling thread is still inside the enqueue"

            store.Release()
            Expect.isTrue (blocked.Wait(TimeSpan.FromSeconds 10.0)) "and is released only when the store answers"
            Expect.isTrue blocked.Result "the job was accepted"
        }

        test "with no store composed the two forms are the same lock-free channel write" {
            let queue = IngestionQueue(capacity = 4)

            Expect.isTrue (run (queue.EnqueueAsync sampleJob)) "async form accepts"
            Expect.isTrue (queue.Enqueue sampleJob) "sync form accepts"
            Expect.equal queue.Count 2 "both landed on the same channel"
            Expect.isFalse queue.IsDurable "and the queue is still the in-memory default (GP 11)"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 723 — scope enumeration + converged ingestion recovery" [
        seamTests
        resolutionTests
        sweepTests
        uncomposedTests
        enqueueTests
    ]