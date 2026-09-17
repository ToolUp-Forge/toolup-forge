// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Offline.Tests.OfflineSyncHandlerTests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.EntityQueryTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Offline
open ToolUp.Offline.OfflineSyncApi
open ToolUp.Offline.OfflineSyncHandler

// ─── Phase 24 — replay-handler tests ─────────────────────────────────
//
// The three guards that carry the phase's correctness are asserted
// here, each against a fake `IEntityStore`:
//
//   1. a mutation for another scope is REJECTED, not redirected;
//   2. a mutation whose base version is behind head is a CONFLICT
//      carrying both documents — this is the guard that exists because
//      `BlobEntityStore.Save` never reports `VersionConflict` itself,
//      so removing it silently clobbers concurrent server edits;
//   3. an audited replay records ONE lifecycle row through `IAuditLog`,
//      carrying the resolved caller (not `"system"`) and, in its
//      `Replay` provenance, the mutation's ORIGINAL enqueue time beside
//      the application time (Phase 759 — the row's own `OccurredAt` is
//      the write time, so the replicator cursor still delivers it; the
//      double-row regression runs against a REAL `BlobEntityStore`).
//
// Each has a paired go-red: the scope test also asserts the matching
// scope APPLIES, the conflict test also asserts the matching version
// applies, and the replay-scope test also asserts the store's own row
// stands when the handler has no audit log — so a guard that refused
// everything, or a suppression that were global, would fail too.

type Inspection = {
    Id: string
    Type: string
    Version: int
    Notes: string
}

let private jsonOptions = FableConverters.create ()

let private serialise (value: 'T) =
    JsonSerializer.Serialize(value, jsonOptions)

/// Minimal in-memory `IEntityStore`. Only the members the handler
/// touches carry behaviour; the rest raise, so a handler change that
/// starts calling one fails loudly rather than passing on a silent
/// empty result.
type FakeEntityStore() =
    let store = Dictionary<string, string * int>()

    let key (entityType: string) (entityId: string) = sprintf "%s/%s" entityType entityId

    member _.Seed(entityType: string, entityId: string, json: string, version: int) =
        store[key entityType entityId] <- (json, version)

    interface IEntityStore with
        member _.Save<'T>(_scopeId: string, entity: 'T) = async {
            match tryGetEntityFields entity with
            | Error msg -> return Error(InvalidEntityShape msg)
            | Ok core ->
                let k = key core.Type core.Id

                let nextVersion =
                    match store.TryGetValue k with
                    | true, (_, v) -> v + 1
                    | _ -> 1

                // Mirrors BlobEntityStore: the store rewrites Version
                // on the way in, so the stored JSON carries the
                // assigned version, not the caller's.
                let raw =
                    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(serialise entity)

                let rewritten = Dictionary<string, obj>()

                for kv in raw do
                    if kv.Key = "Version" then
                        rewritten[kv.Key] <- box nextVersion
                    else
                        rewritten[kv.Key] <- box kv.Value

                store[k] <- (JsonSerializer.Serialize rewritten, nextVersion)

                return
                    Ok {
                        Id = core.Id
                        Type = core.Type
                        Version = nextVersion
                    }
        }

        member _.Get<'T>(_scopeId: string, entityType: string, entityId: EntityId) = async {
            match store.TryGetValue(key entityType entityId) with
            | true, (json, _) -> return Ok(JsonSerializer.Deserialize<'T>(json, jsonOptions))
            | _ -> return Error(EntityError.NotFound(entityType, entityId))
        }

        member _.Delete(_scopeId: string, entityType: string, entityId: EntityId) = async {
            let k = key entityType entityId

            if store.Remove k then
                return Ok()
            else
                return Error(EntityError.NotFound(entityType, entityId))
        }

        // Members the handler never touches. They RAISE rather than
        // returning an empty result, so a handler change that starts
        // calling one fails loudly instead of passing on a silent
        // default.
        member _.GetVersion<'T>(_, _, _, _) : Async<Result<'T, EntityError>> =
            failwith "not used by the offline handler"

        member _.ListVersions<'T>(_, _, _) : Async<EntityRef<'T> list> =
            failwith "not used by the offline handler"

        member _.FindByIndex<'T>(_, _, _, _) : Async<Result<EntityRef<'T> list, EntityError>> =
            failwith "not used by the offline handler"

        member _.Count(_, _) : Async<int> =
            failwith "not used by the offline handler"

        member _.ListAll<'T>(_, _, _, _) : Async<EntityRef<'T> list> =
            failwith "not used by the offline handler"

        member _.Query<'T>(_, _: EntityQuery<'T>) : Async<Result<'T list, EntityError>> =
            failwith "not used by the offline handler"

/// Captures every `ModuleEvent` written, so the audit assertions can
/// read the stamped `OccurredAt` and payload directly.
type CapturingEventStore() =
    let written = ResizeArray<ModuleEvent>()

    member _.Written = written |> List.ofSeq

    interface IEventStore with
        member _.Write(evt: ModuleEvent) = async { written.Add evt }
        member _.ReadAll(_) = async { return [] }
        member _.ReadByType(_, _) = async { return [] }
        member _.ReadBySource(_, _) = async { return [] }
        member _.ListScopes() = async { return [] }

        member _.Erase(_, _, _, _) =
            failwith "not used by the offline handler"

let private contextFor (scopeId: string) (userId: string) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.Items["ToolUp.UserId"] <- box userId

    ctx.Items["ToolUp.StorageScope"] <-
        box {
            ScopeId = scopeId
            Container = sprintf "team-%s" scopeId
            Persist = true
        }

    ctx :> HttpContext

let private replays = [ OfflineEntityReplay.ofJson<Inspection> "Inspection" ]

let private enqueuedAt = DateTimeOffset(2026, 8, 31, 9, 14, 0, TimeSpan.Zero)

let private mutationFor (scopeId: string) (entityId: string) (baseVersion: int) (notes: string) : QueuedMutation =
    let entity = {
        Id = entityId
        Type = "Inspection"
        Version = baseVersion
        Notes = notes
    }

    {
        Id = "m-1"
        EnqueuedAt = enqueuedAt
        ScopeId = scopeId
        EntityType = "Inspection"
        EntityId = entityId
        Operation = SaveOp
        Payload = Encoding.UTF8.GetBytes(serialise entity)
        BaseVersion = baseVersion
        LocalRevision = 1
    }

let scopeGuardTests =
    testList "scope guard" [
        test "a mutation queued against another scope is rejected, not redirected" {
            let store = FakeEntityStore()

            let api =
                offlineSyncApi
                    store
                    (OfflineSyncOptions.defaults |> OfflineSyncOptions.withReplays replays)
                    (contextFor "team-a" "alice")

            let outcome =
                api.Apply(mutationFor "team-b" "e1" 0 "note") |> Async.RunSynchronously

            match outcome with
            | Rejected reason -> Expect.stringContains reason "team-b" "the refusal names the queued scope"
            | other -> failtestf "expected Rejected, got %A" other
        }

        test "the same mutation in the caller's own scope applies" {
            // The go-red partner: without this, a guard that rejected
            // everything would still pass the test above.
            let store = FakeEntityStore()

            let api =
                offlineSyncApi
                    store
                    (OfflineSyncOptions.defaults |> OfflineSyncOptions.withReplays replays)
                    (contextFor "team-a" "alice")

            let outcome =
                api.Apply(mutationFor "team-a" "e1" 0 "note") |> Async.RunSynchronously

            match outcome with
            | Applied _ -> ()
            | other -> failtestf "expected Applied, got %A" other
        }

        test "an empty ScopeId is accepted (single-scope deployments)" {
            let store = FakeEntityStore()

            let api =
                offlineSyncApi
                    store
                    (OfflineSyncOptions.defaults |> OfflineSyncOptions.withReplays replays)
                    (contextFor "team-a" "alice")

            match api.Apply(mutationFor "" "e1" 0 "note") |> Async.RunSynchronously with
            | Applied _ -> ()
            | other -> failtestf "expected Applied, got %A" other
        }
    ]

let conflictTests =
    testList "conflict detection" [
        test "a stale base version conflicts and returns BOTH documents" {
            let store = FakeEntityStore()

            store.Seed(
                "Inspection",
                "e1",
                serialise {
                    Id = "e1"
                    Type = "Inspection"
                    Version = 5
                    Notes = "server edit"
                },
                5
            )

            let api =
                offlineSyncApi
                    store
                    (OfflineSyncOptions.defaults |> OfflineSyncOptions.withReplays replays)
                    (contextFor "team-a" "alice")

            match api.Apply(mutationFor "team-a" "e1" 3 "offline edit") |> Async.RunSynchronously with
            | Conflict(local, server) ->
                Expect.stringContains (Encoding.UTF8.GetString local) "offline edit" "local side is the queued payload"

                Expect.stringContains
                    (Encoding.UTF8.GetString server)
                    "server edit"
                    "server side is the current document"
            | other -> failtestf "expected Conflict, got %A" other
        }

        test "a matching base version applies and the server document is NOT clobbered blindly" {
            let store = FakeEntityStore()

            store.Seed(
                "Inspection",
                "e1",
                serialise {
                    Id = "e1"
                    Type = "Inspection"
                    Version = 3
                    Notes = "server edit"
                },
                3
            )

            let api =
                offlineSyncApi
                    store
                    (OfflineSyncOptions.defaults |> OfflineSyncOptions.withReplays replays)
                    (contextFor "team-a" "alice")

            match api.Apply(mutationFor "team-a" "e1" 3 "offline edit") |> Async.RunSynchronously with
            | Applied bytes ->
                let saved = Encoding.UTF8.GetString bytes
                Expect.stringContains saved "offline edit" "the offline edit won (last-writer-wins)"
                Expect.stringContains saved "\"Version\":4" "the returned document carries the store's new version"
            | other -> failtestf "expected Applied, got %A" other
        }

        test "an unregistered entity type is rejected, never guessed at" {
            let store = FakeEntityStore()

            let api =
                offlineSyncApi
                    store
                    (OfflineSyncOptions.defaults |> OfflineSyncOptions.withReplays [])
                    (contextFor "team-a" "alice")

            match api.Apply(mutationFor "team-a" "e1" 0 "note") |> Async.RunSynchronously with
            | Rejected reason -> Expect.stringContains reason "Inspection" "the refusal names the unregistered type"
            | other -> failtestf "expected Rejected, got %A" other
        }
    ]

/// Decode the lifecycle payload off a captured `ModuleEvent` with the
/// SAME converter set the audit log persists through.
let private decodeLifecycle (evt: ModuleEvent) : EntityLifecycleEventPayload =
    JsonSerializer.Deserialize<EntityLifecycleEventPayload>(evt.Payload, jsonOptions)

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A REAL `BlobEntityStore` over a temp directory, composed with the
/// SDK-default audit log over an in-memory event store — the composition
/// Phase 24 recorded the double row against. Returns the store, the
/// audit log and the event store so a test can read the trail both ways.
let private realStoreWithAudit () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-offline-audit-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let dos = DataObjectStore.DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityStore.EntityRegistry()
    registry.Register(EntityRegistration.create<Inspection> "Inspection")
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let auditLog = AuditLog.EventStoreAuditLog(events, silentLogger) :> IAuditLog

    let store =
        EntityStore.BlobEntityStore(dos, blob, registry, Some auditLog) :> IEntityStore

    store, auditLog, events

/// The lifecycle rows in a trail, oldest first, as `(case, payload)`.
let private lifecycleRows (trail: AuditEvent list) =
    trail
    |> List.rev
    |> List.choose (fun e ->
        match e with
        | EntityCreated p -> Some("EntityCreated", p)
        | EntityUpdated p -> Some("EntityUpdated", p)
        | EntityDeleted p -> Some("EntityDeleted", p)
        | _ -> None)

let auditTests =
    testList "audit replay provenance" [
        test "no audit log means no emission (GP 13)" {
            let store = FakeEntityStore()

            let api =
                offlineSyncApi
                    store
                    (OfflineSyncOptions.defaults |> OfflineSyncOptions.withReplays replays)
                    (contextFor "team-a" "alice")

            api.Apply(mutationFor "team-a" "e1" 0 "note")
            |> Async.RunSynchronously
            |> ignore
        // Nothing to assert beyond "it did not raise" — the point is
        // that the default composes with no audit log at all.
        }

        test "an applied replay records ONE row carrying the real user and BOTH timestamps" {
            let store = FakeEntityStore()
            let events = CapturingEventStore()
            let before = DateTime.UtcNow.AddSeconds -1.0

            let options =
                OfflineSyncOptions.defaults
                |> OfflineSyncOptions.withReplays replays
                |> OfflineSyncOptions.withAuditEventStore events

            let api = offlineSyncApi store options (contextFor "team-a" "alice")

            api.Apply(mutationFor "team-a" "e1" 0 "note")
            |> Async.RunSynchronously
            |> ignore

            match events.Written with
            | [ evt ] ->
                Expect.equal evt.ScopeId "team-a" "scoped to the resolved scope"
                Expect.equal evt.SourceModule AuditSourceModule.value "written on the audit source module"
                Expect.equal evt.EventType "EntityCreated" "version 1 is a creation"

                // The row's OccurredAt is the WRITE time — that is what
                // keeps it after the replicator cursor. The origination
                // time rides the payload, not the envelope.
                Expect.isGreaterThanOrEqual evt.OccurredAt before "OccurredAt is the write time, not the enqueue time"
                Expect.notEqual evt.OccurredAt enqueuedAt.UtcDateTime "the envelope is not backdated"

                let payload = decodeLifecycle evt
                Expect.equal payload.UserId "alice" "the applying user is preserved, not 'system'"
                Expect.equal payload.EntityType "Inspection" "entity type"
                Expect.equal payload.EntityId "e1" "entity id"
                Expect.equal payload.Version 1 "the version the store assigned"

                match payload.Replay with
                | Some replay ->
                    Expect.equal
                        replay.OriginatedAt
                        enqueuedAt.UtcDateTime
                        "OriginatedAt is the mutation's enqueue time"

                    Expect.isGreaterThanOrEqual replay.ReplayedAt before "ReplayedAt is the application time"
                    Expect.notEqual replay.OriginatedAt replay.ReplayedAt "the two timestamps are distinguishable"
                    Expect.equal replay.MutationId "m-1" "the origin marker is the queue's mutation id"
                | None -> failtest "a replayed row must carry replay provenance"
            | other -> failtestf "expected exactly one audit event, got %d" (List.length other)
        }

        test "a replay over an existing entity audits as an update" {
            let store = FakeEntityStore()

            store.Seed(
                "Inspection",
                "e1",
                serialise {
                    Id = "e1"
                    Type = "Inspection"
                    Version = 1
                    Notes = "first"
                },
                1
            )

            let events = CapturingEventStore()

            let options =
                OfflineSyncOptions.defaults
                |> OfflineSyncOptions.withReplays replays
                |> OfflineSyncOptions.withAuditEventStore events

            let api = offlineSyncApi store options (contextFor "team-a" "alice")

            api.Apply(mutationFor "team-a" "e1" 1 "second")
            |> Async.RunSynchronously
            |> ignore

            match events.Written with
            | [ evt ] ->
                Expect.equal evt.EventType "EntityUpdated" "version 2 is an update"
                Expect.equal (decodeLifecycle evt).Version 2 "the new version"
            | other -> failtestf "expected exactly one audit event, got %d" (List.length other)
        }

        test "a conflicted replay emits NO audit event" {
            // A conflict changed nothing, so auditing it would record a
            // write that did not happen.
            let store = FakeEntityStore()

            store.Seed(
                "Inspection",
                "e1",
                serialise {
                    Id = "e1"
                    Type = "Inspection"
                    Version = 5
                    Notes = "server"
                },
                5
            )

            let events = CapturingEventStore()

            let options =
                OfflineSyncOptions.defaults
                |> OfflineSyncOptions.withReplays replays
                |> OfflineSyncOptions.withAuditEventStore events

            let api = offlineSyncApi store options (contextFor "team-a" "alice")

            api.Apply(mutationFor "team-a" "e1" 3 "offline")
            |> Async.RunSynchronously
            |> ignore

            Expect.isEmpty events.Written "a conflict is not a write"
        }

        test "the replayed row is AFTER a replicator cursor that has passed its origination time" {
            // The Phase 24 premise this phase refutes: a row stamped
            // with the origination time was said to reach sinks "with
            // no sink-side change". The replicator's cursor filters on
            // OccurredAt, so a row backdated behind it is never
            // delivered. Pinned here in both directions.
            let store = FakeEntityStore()
            let events = CapturingEventStore()

            let options =
                OfflineSyncOptions.defaults
                |> OfflineSyncOptions.withReplays replays
                |> OfflineSyncOptions.withAuditEventStore events

            let api = offlineSyncApi store options (contextFor "team-a" "alice")

            api.Apply(mutationFor "team-a" "e1" 0 "note")
            |> Async.RunSynchronously
            |> ignore

            let cursor: AuditReplicatorCursor = {
                // A sink that last delivered a day AFTER the user made
                // the edit — the ordinary state when a device has been
                // offline for a while.
                LastDeliveredAt = enqueuedAt.UtcDateTime.AddDays 1.0
                LastDeliveredEventId = Guid.Empty
            }

            match events.Written with
            | [ evt ] ->
                Expect.isTrue (AuditReplicatorCursor.isAfter cursor evt) "the write-time row is delivered"

                // Go-red twin: the same row backdated the Phase 24 way
                // would have been silently skipped.
                let backdated = {
                    evt with
                        OccurredAt = enqueuedAt.UtcDateTime
                }

                Expect.isFalse (AuditReplicatorCursor.isAfter cursor backdated) "a backdated row is behind the cursor"
            | other -> failtestf "expected exactly one audit event, got %d" (List.length other)
        }

        test "a pre-759 lifecycle payload decodes with Replay = None" {
            // A row persisted before the field existed carries no
            // `Replay` property at all; it must read back as a live
            // write, with no version switch and no migration (GP 11).
            let legacy =
                """{"UserId":"system","EntityType":"Inspection","EntityId":"e1","Version":3}"""

            let payload =
                JsonSerializer.Deserialize<EntityLifecycleEventPayload>(legacy, jsonOptions)

            Expect.equal payload.UserId "system" "the legacy actor"
            Expect.equal payload.Version 3 "the legacy version"
            Expect.isNone payload.Replay "an absent field is a live write"
        }

        test "the provenance round-trips through EventStoreAuditLog.GetAuditTrail" {
            let store, auditLog, _ = realStoreWithAudit ()

            let options =
                OfflineSyncOptions.defaults
                |> OfflineSyncOptions.withReplays replays
                |> OfflineSyncOptions.withAuditLog auditLog

            let api = offlineSyncApi store options (contextFor "team-a" "alice")

            match api.Apply(mutationFor "team-a" "e1" 0 "note") |> Async.RunSynchronously with
            | Applied _ -> ()
            | other -> failtestf "expected Applied, got %A" other

            let trail = auditLog.GetAuditTrail("team-a", None, None) |> Async.RunSynchronously

            match lifecycleRows trail with
            | [ ("EntityCreated", p) ] ->
                Expect.equal p.UserId "alice" "the real user survives the round trip"

                match p.Replay with
                | Some replay ->
                    Expect.equal replay.OriginatedAt enqueuedAt.UtcDateTime "OriginatedAt survives the round trip"
                    Expect.equal replay.MutationId "m-1" "MutationId survives the round trip"

                    Expect.isGreaterThan
                        replay.ReplayedAt
                        replay.OriginatedAt
                        "the trail renders the two timestamps distinguishably"
                | None -> failtest "the trail must render the replay provenance"
            | other -> failtestf "expected exactly one lifecycle row, got %A" other
        }

        test "REGRESSION — a replayed version produces exactly ONE lifecycle row, and live writes are untouched" {
            // Phase 24's recorded residue: with the entity store ALSO
            // composed with an IAuditLog, an applied replay produced two
            // rows for one version — the store's ("system", write time)
            // and the handler's (real user, origination time).
            let store, auditLog, _ = realStoreWithAudit ()

            let options =
                OfflineSyncOptions.defaults
                |> OfflineSyncOptions.withReplays replays
                |> OfflineSyncOptions.withAuditLog auditLog

            let api = offlineSyncApi store options (contextFor "team-a" "alice")

            // 1. Replayed create → one row, the handler's.
            match api.Apply(mutationFor "team-a" "e1" 0 "offline v1") |> Async.RunSynchronously with
            | Applied _ -> ()
            | other -> failtestf "expected Applied, got %A" other

            // 2. A LIVE write through the store → its own generic row,
            //    byte-identical to today: "system", no provenance.
            let live =
                store.Save<Inspection>(
                    "team-a",
                    {
                        Id = "e1"
                        Type = "Inspection"
                        Version = 1
                        Notes = "live v2"
                    }
                )
                |> Async.RunSynchronously

            Expect.isOk live "the live save applies"

            // 3. Replayed update over the live head → one row again.
            match api.Apply(mutationFor "team-a" "e1" 2 "offline v3") |> Async.RunSynchronously with
            | Applied _ -> ()
            | other -> failtestf "expected Applied, got %A" other

            // 4. Replayed delete → one row.
            let deletion = {
                mutationFor "team-a" "e1" 3 "" with
                    Id = "m-4"
                    Operation = DeleteOp
            }

            match api.Apply deletion |> Async.RunSynchronously with
            | Applied _ -> ()
            | other -> failtestf "expected Applied, got %A" other

            let trail = auditLog.GetAuditTrail("team-a", None, None) |> Async.RunSynchronously
            let rows = lifecycleRows trail

            // Compared as a sorted set: the trail orders by OccurredAt,
            // and four writes inside one test can share a clock tick.
            let observed =
                rows
                |> List.map (fun (case, p) -> p.Version, case, p.UserId, Option.isSome p.Replay)
                |> List.sort

            Expect.equal
                observed
                [
                    1, "EntityCreated", "alice", true
                    2, "EntityUpdated", "system", false
                    3, "EntityDeleted", "alice", true
                    3, "EntityUpdated", "alice", true
                ]
                "four applied versions, four rows — never a double; replays carry the real user + provenance, the live write is the store's generic row"

            let deleted =
                rows |> List.tryFind (fun (case, _) -> case = "EntityDeleted") |> Option.map snd

            Expect.equal
                (deleted |> Option.bind _.Replay |> Option.map _.MutationId)
                (Some "m-4")
                "the delete row carries its own mutation id"
        }

        test "without an audit log on the handler the store's own row still stands (the scope is never global)" {
            let store, auditLog, _ = realStoreWithAudit ()

            let options = OfflineSyncOptions.defaults |> OfflineSyncOptions.withReplays replays

            let api = offlineSyncApi store options (contextFor "team-a" "alice")

            match api.Apply(mutationFor "team-a" "e1" 0 "note") |> Async.RunSynchronously with
            | Applied _ -> ()
            | other -> failtestf "expected Applied, got %A" other

            let trail = auditLog.GetAuditTrail("team-a", None, None) |> Async.RunSynchronously

            match lifecycleRows trail with
            | [ ("EntityCreated", p) ] ->
                Expect.equal p.UserId "system" "the store's generic row is not suppressed"
                Expect.isNone p.Replay "and it carries no provenance"
            | other -> failtestf "expected the store's single generic row, got %A" other
        }
    ]

let batchTests =
    testList "ApplyBatch" [
        test "outcomes are paired back to their mutation ids" {
            let store = FakeEntityStore()

            let api =
                offlineSyncApi
                    store
                    (OfflineSyncOptions.defaults |> OfflineSyncOptions.withReplays replays)
                    (contextFor "team-a" "alice")

            let batch = [
                {
                    mutationFor "team-a" "e1" 0 "one" with
                        Id = "m-1"
                }
                {
                    mutationFor "team-b" "e2" 0 "two" with
                        Id = "m-2"
                }
            ]

            let results = api.ApplyBatch batch |> Async.RunSynchronously

            Expect.equal (results |> List.map _.MutationId) [ "m-1"; "m-2" ] "ids come back in request order"

            match results |> List.map _.Outcome with
            | [ Applied _; Rejected _ ] -> ()
            | other -> failtestf "expected [Applied; Rejected], got %A" other
        }

        test "a conflict does not abort the rest of the batch" {
            // The whole reason ApplyBatch exists: one unresolvable
            // entity must not hold every unrelated write hostage.
            let store = FakeEntityStore()

            store.Seed(
                "Inspection",
                "e1",
                serialise {
                    Id = "e1"
                    Type = "Inspection"
                    Version = 9
                    Notes = "server"
                },
                9
            )

            let api =
                offlineSyncApi
                    store
                    (OfflineSyncOptions.defaults |> OfflineSyncOptions.withReplays replays)
                    (contextFor "team-a" "alice")

            let batch = [
                {
                    mutationFor "team-a" "e1" 2 "conflicting" with
                        Id = "m-1"
                }
                {
                    mutationFor "team-a" "e2" 0 "fine" with
                        Id = "m-2"
                }
            ]

            match api.ApplyBatch batch |> Async.RunSynchronously |> List.map _.Outcome with
            | [ Conflict _; Applied _ ] -> ()
            | other -> failtestf "expected [Conflict; Applied], got %A" other
        }
    ]

let drainSelectionTests =
    testList "DrainSelection" [
        let entryFor state attempts : QueueEntry = {
            Mutation = mutationFor "team-a" "e1" 0 "note"
            State = state
            Attempts = attempts
            ServerEntity = None
        }

        test "pending entries are always due" {
            Expect.isTrue
                (DrainSelection.isRetryDue RetryPolicy.defaults enqueuedAt (entryFor Pending 0))
                "a pending entry needs no backoff"
        }

        test "conflicted and applied entries are never due" {
            Expect.isFalse
                (DrainSelection.isRetryDue RetryPolicy.defaults (enqueuedAt.AddDays 1.0) (entryFor Conflicted 0))
                "a conflict waits on the user, not the clock"

            Expect.isFalse
                (DrainSelection.isRetryDue RetryPolicy.defaults (enqueuedAt.AddDays 1.0) (entryFor AppliedState 0))
                "a settled entry is never replayed"
        }

        test "a failed entry waits out its backoff, then becomes due" {
            let entry = entryFor (Failed "boom") 2
            // delayFor(2) = 2000 ms under the defaults.
            Expect.isFalse
                (DrainSelection.isRetryDue RetryPolicy.defaults (enqueuedAt.AddMilliseconds 1500.0) entry)
                "still inside the backoff"

            Expect.isTrue
                (DrainSelection.isRetryDue RetryPolicy.defaults (enqueuedAt.AddMilliseconds 2500.0) entry)
                "past the backoff"
        }

        test "an exhausted entry is never due again" {
            let entry = entryFor (Failed "boom") RetryPolicy.defaults.MaxAttempts

            Expect.isFalse
                (DrainSelection.isRetryDue RetryPolicy.defaults (enqueuedAt.AddDays 30.0) entry)
                "the attempt budget is spent"
        }

        test "eligible entries come back in LocalRevision order" {
            let at revision = {
                entryFor Pending 0 with
                    Mutation = {
                        mutationFor "team-a" "e1" 0 "note" with
                            LocalRevision = revision
                    }
            }

            let ordered =
                DrainSelection.eligible RetryPolicy.defaults enqueuedAt [ at 3; at 1; at 2 ]
                |> List.map _.LocalRevision

            Expect.equal ordered [ 1; 2; 3 ] "enqueue order is replay order"
        }
    ]

[<Tests>]
let tests =
    testList "OfflineSyncHandler" [ scopeGuardTests; conflictTests; auditTests; batchTests; drainSelectionTests ]