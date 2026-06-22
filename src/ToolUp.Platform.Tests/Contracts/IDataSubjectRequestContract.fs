module ToolUp.Platform.Tests.Contracts.IDataSubjectRequestContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.ErasurePipeline
open ToolUp.Platform.DataSubjectRequestApi
open ToolUp.Platform.DataSubjectRequestApiHandler
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore

// ─── Phase 9h MVP — IDataSubjectRequestApi orchestrator contract ────
//
// Validates the Phase 9h orchestrator end-to-end against in-memory
// stub IDataExporter / IErasureHandler instances. Per-store
// (per-real-store) contract bindings land per-store as the
// interface-extension follow-ups land. This pack pins the
// orchestrator's load-bearing semantics:
//
//   - Export concatenates registered exporters in alphabetical order
//   - Export segments are deterministic across calls
//   - PreviewErasure calls each handler's Preview and aggregates counts
//   - PreviewErasure does not mutate (verified via separate stub
//     mutation tracker)
//   - ConfirmErasure runs ALL registered handlers
//   - ConfirmErasure aggregates per-handler outcomes
//   - ConfirmErasure on an unknown id refuses
//   - HandlerRefused / StoreUnreachable do not abort the run
//   - Audit callback fires for every state transition
//   - Disabled mode is the default; the SDK ships no DSR endpoints
//     in a vanilla compose

// ─── Stub exporter / handler ─────────────────────────────────────────

type private StubExporter(name: string, segments: ExportSegment list) =
    interface IDataExporter with
        member _.Name = name
        member _.Export(_, _) = async { return segments }

type private StubHandler(name: string, count: int, ?refuse: string) =
    let mutable invocations = 0

    interface IErasureHandler with
        member _.Name = name

        member _.Erase(_, _, _) = async {
            invocations <- invocations + 1

            match refuse with
            | Some reason -> return Result.Error(HandlerRefused(name, reason))
            | None ->
                return
                    Result.Ok {
                        HandlerName = name
                        RecordsAffected = count
                        Note = None
                    }
        }

        member _.Preview(_, _, _) = async {
            return {
                HandlerName = name
                RecordsAffected = count
                Note = None
            }
        }

    member _.InvocationCount = invocations

let private mkApi (exporters: IDataExporter list) (handlers: IErasureHandler list) =
    let auditEvents = ResizeArray<DsrAuditEvent>()

    let audit: AuditOnDsr =
        fun e -> async { lock auditEvents (fun () -> auditEvents.Add e) }

    // Phase 229 — DSR is Platform-Admin gated; the orchestrator contract
    // runs as an admin so the gate passes and the pipeline behaviour under
    // test is exercised. (The gate itself is covered by
    // DataSubjectRequestTests.authorizationTests.)
    let adminContext: AccessContext = {
        AccessContext.unrestricted (AuthenticatedUser "admin-actor") with
            PlatformRole = Some PlatformRole.PlatformAdmin
    }

    let api =
        DataSubjectRequestApiHandler.create
            exporters
            handlers
            ErasurePolicy.Tombstone
            "team-test"
            "admin-actor"
            adminContext
            audit
            None // Phase 9h.A — synchronous-only; no async export deps in this contract.

    api, auditEvents

let tests =
    testList "Phase 9h — IDataSubjectRequest orchestrator contract" [
        testCaseAsync "Export concatenates exporters alphabetically"
        <| async {
            let segA = {
                Name = "a-segment"
                MimeType = "text/plain"
                Body = [| 1uy |]
            }

            let segB = {
                Name = "b-segment"
                MimeType = "text/plain"
                Body = [| 2uy |]
            }

            // Register out of alphabetical order; orchestrator should
            // sort them.
            let exporters = [
                StubExporter("zeta", [ segB ]) :> IDataExporter
                StubExporter("alpha", [ segA ]) :> IDataExporter
            ]

            let api, _audit = mkApi exporters []

            let! result =
                api.RequestExport {
                    SubjectUserId = "u1"
                    TeamId = None
                    Reason = "test"
                }

            match result with
            | Result.Error e -> failtestf "Expected Ok; got Error: %s" e
            | Result.Ok bytes ->
                let body = System.Text.Encoding.UTF8.GetString bytes
                let alphaIdx = body.IndexOf "a-segment"
                let betaIdx = body.IndexOf "b-segment"
                Expect.isLessThan alphaIdx betaIdx "alpha precedes beta in deterministic ordering"
        }

        testCaseAsync "PreviewErasure aggregates per-handler counts"
        <| async {
            let h1 = StubHandler("01-events", 5)
            let h2 = StubHandler("02-data", 3)
            let api, _audit = mkApi [] [ h1 :> IErasureHandler; h2 :> IErasureHandler ]

            let! result =
                api.PreviewErasure {
                    SubjectUserId = "u1"
                    TeamId = None
                    Reason = "test"
                    OverridePolicy = None
                }

            match result with
            | Result.Error e -> failtestf "Expected Ok; got Error: %s" e
            | Result.Ok preview ->
                Expect.equal preview.PerHandlerCounts.Count 2 "two handlers counted"
                Expect.equal preview.PerHandlerCounts["01-events"].RecordsAffected 5 ""
                Expect.equal preview.PerHandlerCounts["02-data"].RecordsAffected 3 ""
                Expect.equal h1.InvocationCount 0 "preview does not invoke Erase"
                Expect.equal h2.InvocationCount 0 "preview does not invoke Erase"
        }

        testCaseAsync "ConfirmErasure runs every registered handler"
        <| async {
            let h1 = StubHandler("01-events", 5)
            let h2 = StubHandler("02-data", 3)
            let api, _audit = mkApi [] [ h1 :> IErasureHandler; h2 :> IErasureHandler ]

            let! previewResult =
                api.PreviewErasure {
                    SubjectUserId = "u1"
                    TeamId = None
                    Reason = "test"
                    OverridePolicy = None
                }

            let preview =
                match previewResult with
                | Result.Ok p -> p
                | Result.Error e -> failtestf "Preview failed: %s" e

            let! confirmResult = api.ConfirmErasure preview.Request.Id

            match confirmResult with
            | Result.Error e -> failtestf "Expected Ok; got Error: %s" e
            | Result.Ok(Completed outcome) ->
                Expect.isTrue outcome.OverallSuccess "all handlers succeeded"
                Expect.equal outcome.PerHandler.Count 2 "two handlers executed"
                Expect.equal h1.InvocationCount 1 "h1 invoked once"
                Expect.equal h2.InvocationCount 1 "h2 invoked once"
            | Result.Ok other -> failtestf "Expected Completed; got %A" other
        }

        testCaseAsync "ConfirmErasure on unknown id refuses"
        <| async {
            let api, _audit = mkApi [] []
            let! result = api.ConfirmErasure "nonexistent-id"

            match result with
            | Result.Ok(Refused _) -> ()
            | other -> failtestf "Expected Refused; got %A" other
        }

        testCaseAsync "Handler refusal does not abort the run"
        <| async {
            let h1 = StubHandler("01-good", 5)
            let h2 = StubHandler("02-refused", 0, refuse = "audit retention overrides erasure")

            let api, _audit = mkApi [] [ h1 :> IErasureHandler; h2 :> IErasureHandler ]

            let! preview =
                api.PreviewErasure {
                    SubjectUserId = "u1"
                    TeamId = None
                    Reason = "test"
                    OverridePolicy = None
                }

            let req =
                match preview with
                | Result.Ok p -> p.Request
                | Result.Error e -> failtestf "Preview failed: %s" e

            let! confirmResult = api.ConfirmErasure req.Id

            match confirmResult with
            | Result.Ok(Completed outcome) ->
                Expect.isFalse outcome.OverallSuccess "refusal flips OverallSuccess to false"
                Expect.equal outcome.PerHandler.Count 2 "both handlers reported"
                Expect.equal h1.InvocationCount 1 "good handler still ran"
                Expect.equal h2.InvocationCount 1 "refused handler still ran (records refusal)"

                match outcome.PerHandler["02-refused"] with
                | Result.Error(HandlerRefused _) -> ()
                | other -> failtestf "Expected HandlerRefused; got %A" other
            | other -> failtestf "Expected Completed; got %A" other
        }

        testCaseAsync "Audit callback fires for state transitions"
        <| async {
            let h = StubHandler("h1", 1)
            let api, audit = mkApi [] [ h :> IErasureHandler ]

            let! preview =
                api.PreviewErasure {
                    SubjectUserId = "u1"
                    TeamId = None
                    Reason = "test"
                    OverridePolicy = None
                }

            let req =
                match preview with
                | Result.Ok p -> p.Request
                | _ -> failtest "preview failed"

            let! _ = api.ConfirmErasure req.Id

            // Expect: RequestStarted (from preview) +
            // PreviewCompleted + ErasureCompleted
            let kinds = audit |> Seq.map _.Kind |> List.ofSeq
            Expect.contains kinds RequestStarted "RequestStarted emitted"
            Expect.contains kinds PreviewCompleted "PreviewCompleted emitted"
            Expect.contains kinds ErasureCompleted "ErasureCompleted emitted"
        }

        testCaseAsync "Default ServerConfig has DataSubjectRequests = Disabled"
        <| async {
            let cfg = ServerConfig.defaults

            match cfg.DataSubjectRequests with
            | DataSubjectRequestMode.Disabled -> ()
            | other -> failtestf "Expected Disabled; got %A" other
        }

        // ─── Real-store binding: IEventStore via EventStoreErasureHandler
        //
        // The per-store interface-extension follow-up. Exercises the
        // real Server-tier InMemoryEventStore through the adapter's
        // IDataExporter / IErasureHandler so the orchestrator's
        // contract is pinned against a real store, not just stubs.

        testCaseAsync "EventStore exporter is scope-isolated to the subject's team"
        <| async {
            let store: IEventStore =
                ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> _

            do! store.Write(Events.create "team-1" "m" "E" """{"userId":"u1","tag":"T1ONLY"}""")
            do! store.Write(Events.create "team-1" "m" "E" """{"userId":"u2","tag":"OTHERUSER"}""")
            do! store.Write(Events.create "team-2" "m" "E" """{"userId":"u1","tag":"T2ONLY"}""")

            let exp = ToolUp.Platform.EventStoreErasureHandler.exporter store
            let! segments = exp.Export("team-1", "u1")

            Expect.equal segments.Length 1 "one segment for the matched scope"
            let body = System.Text.Encoding.UTF8.GetString segments.Head.Body
            Expect.stringContains body "T1ONLY" "subject's in-scope event is exported"
            Expect.isFalse (body.Contains "T2ONLY") "other team's event is NOT exported (GP4)"
            Expect.isFalse (body.Contains "OTHERUSER") "another subject's event is NOT exported"
        }

        testCaseAsync "EventStore Tombstone erase redacts in-scope payloads only"
        <| async {
            let store: IEventStore =
                ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> _

            do! store.Write(Events.create "team-1" "m" "E" """{"userId":"u1","tag":"T1ONLY"}""")
            do! store.Write(Events.create "team-1" "m" "E" """{"userId":"u2","tag":"KEEPME"}""")
            do! store.Write(Events.create "team-2" "m" "E" """{"userId":"u1","tag":"T2ONLY"}""")

            let h = ToolUp.Platform.EventStoreErasureHandler.erasureHandler store

            let! preview = h.Preview("team-1", "u1", ErasurePolicy.Tombstone)
            Expect.equal preview.RecordsAffected 1 "preview counts the one in-scope match"

            let! eventsBeforeConfirm = store.ReadAll "team-1"

            Expect.isTrue
                (eventsBeforeConfirm
                 |> List.forall (fun e -> e.Payload <> Erasure.TombstoneMarker))
                "preview does not mutate"

            let! result = h.Erase("team-1", "u1", ErasurePolicy.Tombstone)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "one event redacted"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! t1 = store.ReadAll "team-1"

            let redacted = t1 |> List.filter (fun e -> e.Payload = Erasure.TombstoneMarker)

            Expect.equal redacted.Length 1 "exactly the u1 event redacted"

            Expect.isTrue
                (t1 |> List.exists (fun e -> e.Payload.Contains "KEEPME"))
                "other subject's event is untouched"

            let! t2 = store.ReadAll "team-2"

            Expect.isTrue
                (t2 |> List.exists (fun e -> e.Payload.Contains "T2ONLY"))
                "other team's event is untouched (GP4 scope isolation)"
        }

        testCaseAsync "EventStore HardDelete erase removes only matching in-scope events"
        <| async {
            let store: IEventStore =
                ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> _

            do! store.Write(Events.create "team-1" "m" "E" """{"userId":"u1"}""")
            do! store.Write(Events.create "team-1" "m" "E" """{"userId":"u2"}""")

            let h = ToolUp.Platform.EventStoreErasureHandler.erasureHandler store
            let! result = h.Erase("team-1", "u1", ErasurePolicy.HardDelete)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "one event removed"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! remaining = store.ReadAll "team-1"
            Expect.equal remaining.Length 1 "only the non-matching event remains"
            Expect.isTrue (remaining.Head.Payload.Contains "u2") "u2's event survived"
        }

        testCaseAsync "EventStore RetainPerCompliance refuses erasure"
        <| async {
            let store: IEventStore =
                ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> _

            do! store.Write(Events.create "team-1" "m" "E" """{"userId":"u1"}""")

            let h = ToolUp.Platform.EventStoreErasureHandler.erasureHandler store
            let! result = h.Erase("team-1", "u1", ErasurePolicy.RetainPerCompliance)

            match result with
            | Result.Error(HandlerRefused("events", _)) -> ()
            | other -> failtestf "Expected HandlerRefused; got %A" other

            let! remaining = store.ReadAll "team-1"
            Expect.equal remaining.Length 1 "refusal leaves the event log intact"
        }

        testCaseAsync "EventStore erase on a blank subject is a no-op"
        <| async {
            let store: IEventStore =
                ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> _

            do! store.Write(Events.create "team-1" "m" "E" """{"userId":"u1"}""")

            let h = ToolUp.Platform.EventStoreErasureHandler.erasureHandler store
            let! result = h.Erase("team-1", "  ", ErasurePolicy.HardDelete)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 0 "blank subject affects nothing"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! remaining = store.ReadAll "team-1"
            Expect.equal remaining.Length 1 "no events removed for a blank subject"
        }

        // ─── Real-store binding: IDataObjectStore via
        //     DataObjectStoreErasureHandler (blob-backed).

        testCaseAsync "DataObjectStore exporter is scope-isolated to the subject"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IDataObjectStore =
                ToolUp.Platform.DataObjectStore.DataObjectStore(blob) :> _

            let bytes (s: string) = System.Text.Encoding.UTF8.GetBytes s

            let! _ = store.Save("team-1", "o1", bytes "a", "dt", "u1", Map.empty, Versioned)
            let! _ = store.Save("team-1", "o2", bytes "b", "dt", "u2", Map.empty, Versioned)
            let! _ = store.Save("team-2", "o3", bytes "c", "dt", "u1", Map.empty, Versioned)

            let exp = ToolUp.Platform.DataObjectStoreErasureHandler.exporter store
            let! segments = exp.Export("team-1", "u1")

            Expect.equal segments.Length 1 "one segment for the scope"
            let body = System.Text.Encoding.UTF8.GetString segments.Head.Body
            Expect.stringContains body "o1" "subject's in-scope object exported"
            Expect.isFalse (body.Contains "o2") "other subject's object NOT exported"
            Expect.isFalse (body.Contains "o3") "other team's object NOT exported (GP4)"
        }

        testCaseAsync "DataObjectStore Tombstone redacts CreatedBy + content; scope-isolated"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IDataObjectStore =
                ToolUp.Platform.DataObjectStore.DataObjectStore(blob) :> _

            let bytes (s: string) = System.Text.Encoding.UTF8.GetBytes s

            let! _ = store.Save("team-1", "o1", bytes "secret", "dt", "u1", Map [ "k", "u1-ref" ], Versioned)
            let! _ = store.Save("team-1", "o2", bytes "keep", "dt", "u2", Map.empty, Versioned)
            let! _ = store.Save("team-2", "o3", bytes "other", "dt", "u1", Map.empty, Versioned)

            let h = ToolUp.Platform.DataObjectStoreErasureHandler.erasureHandler store

            let! preview = h.Preview("team-1", "u1", ErasurePolicy.Tombstone)
            Expect.equal preview.RecordsAffected 1 "one in-scope object matched"

            let! result = h.Erase("team-1", "u1", ErasurePolicy.Tombstone)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "one object tombstoned"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! o1 = store.Get("team-1", "o1")

            match o1 with
            | Result.Ok(obj, content) ->
                Expect.equal obj.CreatedBy Erasure.TombstoneMarker "CreatedBy redacted"
                Expect.equal obj.Metadata["k"] Erasure.TombstoneMarker "metadata value redacted"
                Expect.equal (System.Text.Encoding.UTF8.GetString content) Erasure.TombstoneMarker "content redacted"
            | Result.Error e -> failtestf "o1 should still exist: %A" e

            let! o2 = store.Get("team-1", "o2")

            match o2 with
            | Result.Ok(obj, content) ->
                Expect.equal obj.CreatedBy "u2" "other subject untouched"
                Expect.equal (System.Text.Encoding.UTF8.GetString content) "keep" "other content untouched"
            | Result.Error e -> failtestf "o2 should exist: %A" e

            let! o3 = store.Get("team-2", "o3")

            match o3 with
            | Result.Ok(obj, _) -> Expect.equal obj.CreatedBy "u1" "other team untouched (GP4 scope isolation)"
            | Result.Error e -> failtestf "o3 should exist: %A" e
        }

        testCaseAsync "DataObjectStore HardDelete removes matched objects only"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IDataObjectStore =
                ToolUp.Platform.DataObjectStore.DataObjectStore(blob) :> _

            let bytes (s: string) = System.Text.Encoding.UTF8.GetBytes s

            let! _ = store.Save("team-1", "o1", bytes "a", "dt", "u1", Map.empty, Versioned)
            let! _ = store.Save("team-1", "o2", bytes "b", "dt", "u2", Map.empty, Versioned)

            let h = ToolUp.Platform.DataObjectStoreErasureHandler.erasureHandler store
            let! result = h.Erase("team-1", "u1", ErasurePolicy.HardDelete)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "one object removed"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! o1 = store.Get("team-1", "o1")

            match o1 with
            | Result.Error NotFound -> ()
            | other -> failtestf "o1 should be gone; got %A" other

            let! o2 = store.Get("team-1", "o2")
            Expect.isTrue (Result.isOk o2) "o2 survived"
        }

        testCaseAsync "DataObjectStore RetainPerCompliance redacts identifiers but keeps content"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IDataObjectStore =
                ToolUp.Platform.DataObjectStore.DataObjectStore(blob) :> _

            let bytes (s: string) = System.Text.Encoding.UTF8.GetBytes s

            let! _ = store.Save("team-1", "o1", bytes "keepcontent", "dt", "u1", Map.empty, Versioned)

            let h = ToolUp.Platform.DataObjectStoreErasureHandler.erasureHandler store
            let! result = h.Erase("team-1", "u1", ErasurePolicy.RetainPerCompliance)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "one object redacted"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! o1 = store.Get("team-1", "o1")

            match o1 with
            | Result.Ok(obj, content) ->
                Expect.equal obj.CreatedBy Erasure.TombstoneMarker "identifier redacted"
                Expect.equal (System.Text.Encoding.UTF8.GetString content) "keepcontent" "content retained"
            | Result.Error e -> failtestf "o1 should still exist: %A" e
        }

        // ─── Real-store binding: ILineageStore via
        //     LineageStoreErasureHandler (event-projection).

        testCaseAsync "LineageStore erase reports matched links, scope-isolated"
        <| async {
            let es: IEventStore = ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> _
            let ls: ILineageStore = ToolUp.Platform.LineageStore.EventStoreLineageStore(es) :> _

            let mkLink from' to' = {
                LinkId = System.Guid.NewGuid()
                FromObjectId = from'
                ToObjectId = to'
                ModuleName = "m"
                LinkType = Derived
                Timestamp = System.DateTime.UtcNow
            }

            let! _ = ls.Record("team-1", mkLink "u1-src" "d1")
            let! _ = ls.Record("team-1", mkLink "other" "d2")
            let! _ = ls.Record("team-2", mkLink "u1-src" "d3")

            let h = ToolUp.Platform.LineageStoreErasureHandler.erasureHandler ls
            let! result = h.Erase("team-1", "u1", ErasurePolicy.HardDelete)

            match result with
            | Result.Ok s ->
                Expect.equal s.RecordsAffected 1 "only the in-scope link naming the subject is counted (GP4)"
            | Result.Error e -> failtestf "Expected Ok; got %A" e
        }

        testCaseAsync "LineageStore RetainPerCompliance refuses"
        <| async {
            let es: IEventStore = ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> _
            let ls: ILineageStore = ToolUp.Platform.LineageStore.EventStoreLineageStore(es) :> _
            let h = ToolUp.Platform.LineageStoreErasureHandler.erasureHandler ls
            let! result = h.Erase("team-1", "u1", ErasurePolicy.RetainPerCompliance)

            match result with
            | Result.Error(HandlerRefused("lineage", _)) -> ()
            | other -> failtestf "Expected HandlerRefused; got %A" other
        }

        testCaseAsync "LineageStore blank subject is a no-op"
        <| async {
            let es: IEventStore = ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> _
            let ls: ILineageStore = ToolUp.Platform.LineageStore.EventStoreLineageStore(es) :> _
            let h = ToolUp.Platform.LineageStoreErasureHandler.erasureHandler ls
            let! result = h.Erase("team-1", "   ", ErasurePolicy.HardDelete)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 0 "blank subject affects nothing"
            | Result.Error e -> failtestf "Expected Ok; got %A" e
        }

        // ─── Real-store binding: IConfigStore via
        //     ConfigStoreErasureHandler (blob-backed).

        testCaseAsync "ConfigStore Tombstone redacts matching values; scope-isolated"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IConfigStore = ToolUp.Platform.ConfigStore.create blob
            let bytes (s: string) = System.Text.Encoding.UTF8.GetBytes s

            // Pre-seed the persisted map shape directly (field values
            // are JSON-encoded strings).
            let! _ =
                blob.Upload("_platform", "config/team-1/modA.json", bytes """{"owner":"\"u1@x\"","keep":"\"safe\""}""")

            let! _ = blob.Upload("_platform", "config/team-2/modB.json", bytes """{"owner":"\"u1@x\""}""")

            let h = ToolUp.Platform.ConfigStoreErasureHandler.erasureHandler store

            let! preview = h.Preview("team-1", "u1", ErasurePolicy.Tombstone)
            Expect.equal preview.RecordsAffected 1 "one in-scope document matched"

            let! result = h.Erase("team-1", "u1", ErasurePolicy.Tombstone)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "one document redacted"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! a = blob.Download("_platform", "config/team-1/modA.json")

            match a with
            | Ok b ->
                let body = System.Text.Encoding.UTF8.GetString b
                Expect.stringContains body Erasure.TombstoneMarker "matching value redacted"
                Expect.isFalse (body.Contains "u1@x") "subject value gone"
                Expect.stringContains body "safe" "non-matching value retained"
            | Error e -> failtestf "doc should still exist: %s" e

            let! t2 = blob.Download("_platform", "config/team-2/modB.json")

            match t2 with
            | Ok b ->
                Expect.stringContains
                    (System.Text.Encoding.UTF8.GetString b)
                    "u1@x"
                    "other team's config untouched (GP4)"
            | Error e -> failtestf "team-2 doc should exist: %s" e
        }

        testCaseAsync "ConfigStore HardDelete removes matching documents only"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IConfigStore = ToolUp.Platform.ConfigStore.create blob
            let bytes (s: string) = System.Text.Encoding.UTF8.GetBytes s

            let! _ = blob.Upload("_platform", "config/team-1/modA.json", bytes """{"owner":"\"u1@x\""}""")
            let! _ = blob.Upload("_platform", "config/team-1/modB.json", bytes """{"owner":"\"u2@x\""}""")

            let h = ToolUp.Platform.ConfigStoreErasureHandler.erasureHandler store
            let! result = h.Erase("team-1", "u1", ErasurePolicy.HardDelete)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "one document removed"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! existsA = blob.Exists("_platform", "config/team-1/modA.json")
            Expect.isFalse existsA "matching document removed"
            let! existsB = blob.Exists("_platform", "config/team-1/modB.json")
            Expect.isTrue existsB "non-matching document survived"
        }

        testCaseAsync "ConfigStore blank subject is a no-op"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IConfigStore = ToolUp.Platform.ConfigStore.create blob
            let bytes (s: string) = System.Text.Encoding.UTF8.GetBytes s
            let! _ = blob.Upload("_platform", "config/team-1/modA.json", bytes """{"owner":"\"u1@x\""}""")

            let h = ToolUp.Platform.ConfigStoreErasureHandler.erasureHandler store
            let! result = h.Erase("team-1", "  ", ErasurePolicy.HardDelete)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 0 "blank subject affects nothing"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! existsA = blob.Exists("_platform", "config/team-1/modA.json")
            Expect.isTrue existsA "no document removed for a blank subject"
        }

        // ─── Real-store binding: IFeatureFlagStore via
        //     FeatureFlagStoreErasureHandler (blob-backed).

        testCaseAsync "FeatureFlagStore Tombstone redacts matching flags; scope-isolated"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IFeatureFlagStore = ToolUp.Platform.FeatureFlagStore.create blob

            let! _ = store.SetFlag(FlagScope.Team "1", "targeting", FlagValue.Variant([ "a" ], "for:u1"))
            let! _ = store.SetFlag(FlagScope.Team "1", "plain", FlagValue.Bool true)
            let! _ = store.SetFlag(FlagScope.Team "1", "other", FlagValue.Variant([ "x" ], "nobody"))
            let! _ = store.SetFlag(FlagScope.Team "2", "targeting", FlagValue.Variant([ "a" ], "for:u1"))

            let h = ToolUp.Platform.FeatureFlagStoreErasureHandler.erasureHandler store

            let! preview = h.Preview("team-1", "u1", ErasurePolicy.Tombstone)
            Expect.equal preview.RecordsAffected 1 "only the targeting flag names the subject"

            let! result = h.Erase("team-1", "u1", ErasurePolicy.Tombstone)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "one flag redacted"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! t1 = store.ListFlags(FlagScope.Team "1")

            match t1["targeting"] with
            | FlagValue.Variant(_, value) -> Expect.equal value Erasure.TombstoneMarker "value redacted"
            | other -> failtestf "Expected redacted Variant; got %A" other

            Expect.equal t1["plain"] (FlagValue.Bool true) "bool flag untouched"

            match t1["other"] with
            | FlagValue.Variant(_, value) -> Expect.equal value "nobody" "non-matching flag untouched"
            | other -> failtestf "Expected intact Variant; got %A" other

            let! t2 = store.ListFlags(FlagScope.Team "2")

            match t2["targeting"] with
            | FlagValue.Variant(_, value) -> Expect.equal value "for:u1" "other team untouched (GP4)"
            | other -> failtestf "Expected intact Variant; got %A" other
        }

        testCaseAsync "FeatureFlagStore HardDelete drops matching flags only"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IFeatureFlagStore = ToolUp.Platform.FeatureFlagStore.create blob

            let! _ = store.SetFlag(FlagScope.Team "1", "targeting", FlagValue.Variant([], "for:u1"))
            let! _ = store.SetFlag(FlagScope.Team "1", "keep", FlagValue.Bool false)

            let h = ToolUp.Platform.FeatureFlagStoreErasureHandler.erasureHandler store
            let! result = h.Erase("team-1", "u1", ErasurePolicy.HardDelete)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "one flag removed"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! t1 = store.ListFlags(FlagScope.Team "1")
            Expect.isFalse (Map.containsKey "targeting" t1) "matching flag removed"
            Expect.isTrue (Map.containsKey "keep" t1) "non-matching flag survived"
        }

        testCaseAsync "FeatureFlagStore blank subject is a no-op"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IFeatureFlagStore = ToolUp.Platform.FeatureFlagStore.create blob
            let! _ = store.SetFlag(FlagScope.Team "1", "targeting", FlagValue.Variant([], "for:u1"))

            let h = ToolUp.Platform.FeatureFlagStoreErasureHandler.erasureHandler store
            let! result = h.Erase("team-1", "  ", ErasurePolicy.HardDelete)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 0 "blank subject affects nothing"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! t1 = store.ListFlags(FlagScope.Team "1")
            Expect.isTrue (Map.containsKey "targeting" t1) "nothing removed for a blank subject"
        }

        // ─── Real-store binding: IBlobStorage via
        //     BlobStorageErasureHandler (prefix-scoped).

        testCaseAsync "BlobStorage Tombstone overwrites subject-prefixed blobs; scope-isolated"
        <| async {
            let store: ToolUp.Platform.BlobStorage.IBlobStorage =
                InMemoryBlobStorage.InMemoryBlobStorage() :> _

            let bytes (s: string) = System.Text.Encoding.UTF8.GetBytes s

            let! _ = store.Upload("team-1", "u1/a.txt", bytes "secretA")
            let! _ = store.Upload("team-1", "u1/b.txt", bytes "secretB")
            let! _ = store.Upload("team-1", "u2/c.txt", bytes "keepC")
            let! _ = store.Upload("team-2", "u1/d.txt", bytes "otherteam")

            let h = ToolUp.Platform.BlobStorageErasureHandler.erasureHandler store

            let! preview = h.Preview("team-1", "u1", ErasurePolicy.Tombstone)
            Expect.equal preview.RecordsAffected 2 "two subject-prefixed blobs in scope"

            let! result = h.Erase("team-1", "u1", ErasurePolicy.Tombstone)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 2 "two blobs tombstoned"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! a = store.Download("team-1", "u1/a.txt")

            match a with
            | Ok b -> Expect.equal (System.Text.Encoding.UTF8.GetString b) Erasure.TombstoneMarker "content redacted"
            | Error e -> failtestf "blob should still exist (key kept): %s" e

            let! c = store.Download("team-1", "u2/c.txt")

            match c with
            | Ok b -> Expect.equal (System.Text.Encoding.UTF8.GetString b) "keepC" "other subject untouched"
            | Error e -> failtestf "u2 blob should exist: %s" e

            let! d = store.Download("team-2", "u1/d.txt")

            match d with
            | Ok b -> Expect.equal (System.Text.Encoding.UTF8.GetString b) "otherteam" "other team untouched (GP4)"
            | Error e -> failtestf "team-2 blob should exist: %s" e
        }

        testCaseAsync "BlobStorage HardDelete deletes subject-prefixed blobs only"
        <| async {
            let store: ToolUp.Platform.BlobStorage.IBlobStorage =
                InMemoryBlobStorage.InMemoryBlobStorage() :> _

            let bytes (s: string) = System.Text.Encoding.UTF8.GetBytes s

            let! _ = store.Upload("team-1", "u1/a.txt", bytes "x")
            let! _ = store.Upload("team-1", "u2/c.txt", bytes "y")

            let h = ToolUp.Platform.BlobStorageErasureHandler.erasureHandler store
            let! result = h.Erase("team-1", "u1", ErasurePolicy.HardDelete)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "one blob deleted"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! existsA = store.Exists("team-1", "u1/a.txt")
            Expect.isFalse existsA "subject blob deleted"
            let! existsC = store.Exists("team-1", "u2/c.txt")
            Expect.isTrue existsC "other subject's blob survived"
        }

        testCaseAsync "BlobStorage blank subject is a no-op"
        <| async {
            let store: ToolUp.Platform.BlobStorage.IBlobStorage =
                InMemoryBlobStorage.InMemoryBlobStorage() :> _

            let bytes (s: string) = System.Text.Encoding.UTF8.GetBytes s
            let! _ = store.Upload("team-1", "u1/a.txt", bytes "x")

            let h = ToolUp.Platform.BlobStorageErasureHandler.erasureHandler store
            let! result = h.Erase("team-1", "   ", ErasurePolicy.HardDelete)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 0 "blank subject affects nothing"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! existsA = store.Exists("team-1", "u1/a.txt")
            Expect.isTrue existsA "nothing deleted for a blank subject"
        }

        // ─── Real-store binding: IVectorStore via HnswVectorStore +
        //     VectorStoreErasureHandler (KB chunks + embedding-cache).

        testCaseAsync "VectorStore Tombstone removes subject chunks from retrieval; scope-isolated"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IVectorStore =
                new ToolUp.RAG.VectorStores.Hnsw.HnswVectorStore.HnswVectorStore(blob) :> _

            let chunk (c: string) : TextChunk = { Content = c; Metadata = Map.empty }
            let vec = [| 1.0f; 0.0f |]

            do! store.Upsert (Team "1") "c1" vec (chunk "report about u1 activity")
            do! store.Upsert (Team "1") "c2" vec (chunk "unrelated content")
            do! store.Upsert (Team "2") "c3" vec (chunk "u1 in another team")

            let! preview = store.Erase(Team "1", "u1", ErasurePolicy.Tombstone, true)

            match preview with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "one chunk names the subject in scope"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! result = store.Erase(Team "1", "u1", ErasurePolicy.Tombstone, false)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "one chunk tombstoned"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! liveT1 = store.ListChunks (Team "1") false
            let liveIds = liveT1 |> List.map fst |> Set.ofList
            Expect.isFalse (liveIds.Contains "c1") "subject chunk filtered from retrieval"
            Expect.isTrue (liveIds.Contains "c2") "unrelated chunk retained"

            let! liveT2 = store.ListChunks (Team "2") false
            Expect.isTrue (liveT2 |> List.exists (fun (cid, _) -> cid = "c3")) "other team's chunk untouched (GP4)"
        }

        testCaseAsync "VectorStore HardDelete purges subject chunks"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IVectorStore =
                new ToolUp.RAG.VectorStores.Hnsw.HnswVectorStore.HnswVectorStore(blob) :> _

            let chunk (c: string) : TextChunk = { Content = c; Metadata = Map.empty }
            let vec = [| 0.0f; 1.0f |]

            do! store.Upsert (Team "1") "c1" vec (chunk "secret about u1")
            do! store.Upsert (Team "1") "c2" vec (chunk "keep me")

            let! result = store.Erase(Team "1", "u1", ErasurePolicy.HardDelete, false)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "one chunk purged"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! all = store.ListChunks (Team "1") true
            let ids = all |> List.map fst |> Set.ofList
            Expect.isFalse (ids.Contains "c1") "subject chunk physically purged (incl. tombstones)"
            Expect.isTrue (ids.Contains "c2") "unrelated chunk survived"
        }

        testCaseAsync "VectorStore adapter flushes the embedding cache"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IVectorStore =
                new ToolUp.RAG.VectorStores.Hnsw.HnswVectorStore.HnswVectorStore(blob) :> _

            do!
                store.Upsert (Team "1") "c1" [| 1.0f |] {
                    Content = "u1 data"
                    Metadata = Map.empty
                }

            let cleared = ref false

            let cache =
                { new ToolUp.Platform.IEmbeddingCache.IEmbeddingCache with
                    member _.TryGet _ = async { return None }
                    member _.Set _ _ = async { return () }
                    member _.HitRate() = async { return 0.0 }
                    member _.Clear() = async { cleared.Value <- true }
                }

            let h = ToolUp.Platform.VectorStoreErasureHandler.erasureHandler store cache
            let! result = h.Erase("team-1", "u1", ErasurePolicy.HardDelete)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 1 "subject chunk erased via adapter"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            Expect.isTrue cleared.Value "embedding cache flushed after chunk erasure"
        }

        testCaseAsync "VectorStore blank subject is a no-op"
        <| async {
            let blob =
                InMemoryBlobStorage.InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage

            let store: IVectorStore =
                new ToolUp.RAG.VectorStores.Hnsw.HnswVectorStore.HnswVectorStore(blob) :> _

            do!
                store.Upsert (Team "1") "c1" [| 1.0f |] {
                    Content = "u1 data"
                    Metadata = Map.empty
                }

            let! result = store.Erase(Team "1", "  ", ErasurePolicy.HardDelete, false)

            match result with
            | Result.Ok s -> Expect.equal s.RecordsAffected 0 "blank subject affects nothing"
            | Result.Error e -> failtestf "Expected Ok; got %A" e

            let! live = store.ListChunks (Team "1") false
            Expect.isTrue (live |> List.exists (fun (cid, _) -> cid = "c1")) "nothing erased for a blank subject"
        }
    ]