module ToolUp.Platform.Tests.Contracts.IUserSchemaStoreContract

open System
open System.Text
open System.Text.Json
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 7b — IUserSchemaStore contract pack ────────────────────────
//
// Binds the reusable conformance bar to the blob-backed default
// (`BlobUserSchemaStore` over a real `DataObjectStore` over an in-memory
// `IBlobStorage`): round-trip CRUD, version-history reads, migration
// execution (direct + through the `SchemaMigrationJobHandler` the
// scheduler drives), scope isolation, and audit emission on every state
// change.

/// In-memory `IAuditLog` capturing every recorded `(scopeId, event)` so
/// the pack can assert emission on schema state changes.
type private CapturingAuditLog() =
    let events = ConcurrentQueue<string * AuditEvent>()

    member _.Recorded = events |> Seq.toList

    member _.KindsFor(scopeId: string) =
        events
        |> Seq.filter (fun (s, _) -> s = scopeId)
        |> Seq.map (fun (_, e) -> AuditEvent.eventTypeName e)
        |> Seq.toList

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { events.Enqueue((scopeId, audit)) }

        member _.GetAuditTrail(scopeId, _dateRange, eventType) = async {
            return
                events
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> Seq.filter (fun e ->
                    match eventType with
                    | Some t -> AuditEvent.eventTypeName e = t
                    | None -> true)
                |> Seq.toList
        }

let private noopLogger: ILogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Fresh store per test: (store, backing object store, capturing audit).
let private mkStore () : IUserSchemaStore * IDataObjectStore * CapturingAuditLog =
    let blob = InMemoryBlobStorage() :> IBlobStorage
    let dos = DataObjectStore.DataObjectStore(blob, noopLogger) :> IDataObjectStore
    let audit = CapturingAuditLog()

    let store =
        BlobUserSchemaStore.BlobUserSchemaStore(dos, Some(audit :> IAuditLog)) :> IUserSchemaStore

    store, dos, audit

let private field (name: string) (t: BIFriendlyType) (sensitivity: FieldSensitivity) : UserSchemaField = {
    Name = name
    Type = t
    Required = false
    Description = None
    Sensitivity = sensitivity
}

let private sampleSchema (schemaId: string) (scope: string) : UserAuthoredSchema =
    UserAuthoredSchema.create schemaId "Expense Claim" scope [
        field "Region" BIFriendlyType.String FieldSensitivity.Public
        field "Amount" (BIFriendlyType.Currency "USD") FieldSensitivity.Financial
    ]

/// Save an instance row (DataType = schemaId) directly so migration can
/// transform it.
let private saveInstance (dos: IDataObjectStore) (scope: string) (schemaId: string) (instId: string) (json: string) =
    dos.Save(scope, instId, Encoding.UTF8.GetBytes json, schemaId, "u", Map.empty, VersioningPolicy.Versioned)
    |> Async.RunSynchronously
    |> ignore

let private readInstanceJson (dos: IDataObjectStore) (scope: string) (instId: string) : string =
    match dos.Get(scope, instId) |> Async.RunSynchronously with
    | Ok(_, bytes) -> Encoding.UTF8.GetString bytes
    | Error e -> failtestf "instance %s not found: %A" instId e

let private jsonHas (json: string) (key: string) (value: string) : bool =
    use doc = JsonDocument.Parse json

    match doc.RootElement.TryGetProperty key with
    | true, prop -> prop.GetString() = value
    | false, _ -> false

let private jsonHasKey (json: string) (key: string) : bool =
    use doc = JsonDocument.Parse json
    let mutable p = Unchecked.defaultof<JsonElement>
    doc.RootElement.TryGetProperty(key, &p)

let tests =
    testList "IUserSchemaStore contract" [

        testCaseAsync "Save assigns v1 + Owner, Get round-trips"
        <| async {
            let store, _, _ = mkStore ()
            let scope = "team-a"
            let! saved = store.Save(scope, sampleSchema "expense" scope, "author-1")

            match saved with
            | Ok s ->
                Expect.equal s.Version 1 "first save is v1"
                Expect.equal s.Owner scope "Owner set to the scope"
                Expect.equal s.Fields.Length 2 "fields preserved"
            | Error e -> failtestf "save failed: %A" e

            let! got = store.Get(scope, "expense")

            match got with
            | Ok s -> Expect.equal s.DisplayName "Expense Claim" "round-trip display name"
            | Error e -> failtestf "get failed: %A" e
        }

        testCaseAsync "Save twice bumps version; history + GetVersion walk"
        <| async {
            let store, _, _ = mkStore ()
            let scope = "team-a"
            let! _ = store.Save(scope, sampleSchema "expense" scope, "author-1")

            let v2 = {
                sampleSchema "expense" scope with
                    VersionLabel = "v2"
                    Fields = [ field "Region" BIFriendlyType.String FieldSensitivity.Public ]
            }

            let! saved2 = store.Save(scope, v2, "author-1")

            match saved2 with
            | Ok s -> Expect.equal s.Version 2 "second save is v2"
            | Error e -> failtestf "save2 failed: %A" e

            let! history = store.History(scope, "expense")
            Expect.equal (history |> List.map _.Version) [ 1; 2 ] "history oldest-first"

            let! v1 = store.GetVersion(scope, "expense", 1)

            match v1 with
            | Ok s -> Expect.equal s.Fields.Length 2 "v1 still has both fields"
            | Error e -> failtestf "GetVersion(1) failed: %A" e
        }

        testCaseAsync "List returns latest of each schema in scope"
        <| async {
            let store, _, _ = mkStore ()
            let scope = "team-a"
            let! _ = store.Save(scope, sampleSchema "expense" scope, "u")
            let! _ = store.Save(scope, sampleSchema "invoice" scope, "u")
            let! listed = store.List scope
            let ids = listed |> List.map _.SchemaId |> Set.ofList
            Expect.equal ids (Set.ofList [ "expense"; "invoice" ]) "both schemas listed"
        }

        testCaseAsync "scope isolation — Team B cannot see Team A's schemas"
        <| async {
            let store, _, _ = mkStore ()
            let! _ = store.Save("team-a", sampleSchema "expense" "team-a", "u")

            let! inB = store.List "team-b"
            Expect.equal inB [] "team-b sees nothing"

            let! getB = store.Get("team-b", "expense")

            match getB with
            | Error(SchemaNotFound _) -> ()
            | other -> failtestf "expected SchemaNotFound in team-b, got %A" other
        }

        testCaseAsync "Get unknown schema → SchemaNotFound"
        <| async {
            let store, _, _ = mkStore ()
            let! r = store.Get("team-a", "nope")

            match r with
            | Error(SchemaNotFound "nope") -> ()
            | other -> failtestf "expected SchemaNotFound, got %A" other
        }

        testCaseAsync "invalid schema id rejected"
        <| async {
            let store, _, _ = mkStore ()
            let! r = store.Save("team-a", sampleSchema "bad/id" "team-a", "u")

            match r with
            | Error(InvalidSchema _) -> ()
            | other -> failtestf "expected InvalidSchema, got %A" other
        }

        testCaseAsync "audit — Save emits SchemaChanged(Created); Delete emits SchemaChanged"
        <| async {
            let store, _, audit = mkStore ()
            let scope = "team-a"
            let! _ = store.Save(scope, sampleSchema "expense" scope, "u")
            Expect.contains (audit.KindsFor scope) "SchemaChanged" "Save emits SchemaChanged"

            let! _ = store.Delete(scope, "expense", "u")

            let changedCount =
                audit.KindsFor scope |> List.filter ((=) "SchemaChanged") |> List.length

            Expect.isGreaterThanOrEqual changedCount 2 "Save + Delete each emit SchemaChanged"
        }

        testCaseAsync "audit — AI-proposed commit emits SchemaApproved"
        <| async {
            let store, _, audit = mkStore ()
            let scope = "team-a"

            let aiSchema = {
                sampleSchema "expense" scope with
                    ProposedBy = AIWithApproval "conv-42"
            }

            let! _ = store.Save(scope, aiSchema, "approver-1")
            Expect.contains (audit.KindsFor scope) "SchemaApproved" "AIWithApproval commit emits SchemaApproved"
        }

        testCaseAsync "migration — AddField evolves schema + populates instance default"
        <| async {
            let store, dos, audit = mkStore ()
            let scope = "team-a"
            let! _ = store.Save(scope, sampleSchema "expense" scope, "u")

            saveInstance dos scope "expense" "inst-1" """{"Region":"EU"}"""
            saveInstance dos scope "expense" "inst-2" """{"Region":"US"}"""

            let newField = field "Approved" BIFriendlyType.Boolean FieldSensitivity.Internal
            let migrations = [ AddField(newField, "false") ]
            let! outcome = store.ExecuteMigration(scope, "expense", migrations, "u")

            match outcome with
            | Ok o ->
                Expect.equal o.FromVersion 1 "from v1"
                Expect.equal o.ToVersion 2 "to v2"
                Expect.equal o.InstancesMigrated 2 "both instances migrated"
                Expect.equal o.AppliedMigrations 1 "one migration applied"
            | Error e -> failtestf "migration failed: %A" e

            // Evolved schema carries the new field + EvolvedFrom.
            let! evolved = store.Get(scope, "expense")

            match evolved with
            | Ok s ->
                Expect.isTrue (s.Fields |> List.exists (fun f -> f.Name = "Approved")) "new field on schema"
                Expect.equal s.EvolvedFrom (Some "expense") "EvolvedFrom recorded"
            | Error e -> failtestf "get evolved failed: %A" e

            // Instances carry the defaulted field.
            let inst1 = readInstanceJson dos scope "inst-1"
            Expect.isTrue (jsonHas inst1 "Approved" "false") "instance defaulted"
            Expect.contains (audit.KindsFor scope) "SchemaChanged" "migration emits SchemaChanged"
        }

        testCaseAsync "migration — RemoveField + RenameField reshape instances"
        <| async {
            let store, dos, _ = mkStore ()
            let scope = "team-a"
            let! _ = store.Save(scope, sampleSchema "expense" scope, "u")
            saveInstance dos scope "expense" "inst-1" """{"Region":"EU","Amount":"10"}"""

            let migrations = [ RemoveField "Amount"; RenameField("Region", "Market") ]
            let! _ = store.ExecuteMigration(scope, "expense", migrations, "u")

            let inst = readInstanceJson dos scope "inst-1"
            Expect.isFalse (jsonHasKey inst "Amount") "Amount removed"
            Expect.isFalse (jsonHasKey inst "Region") "Region gone"
            Expect.isTrue (jsonHas inst "Market" "EU") "renamed to Market with value"
        }

        testCaseAsync "migration via SchemaMigrationJobHandler (IJobScheduler binding) survives the dispatch shape"
        <| async {
            let store, dos, _ = mkStore ()
            let scope = "team-a"
            let! _ = store.Save(scope, sampleSchema "expense" scope, "u")
            saveInstance dos scope "expense" "inst-1" """{"Region":"EU"}"""

            let handler = SchemaMigrationJobHandler.create store

            let payload: SchemaMigrationJobHandler.SchemaMigrationJobPayload = {
                SchemaId = "expense"
                Migrations = [
                    AddField(field "Flag" BIFriendlyType.Boolean FieldSensitivity.Internal, "true")
                ]
                ActorUserId = "job-runner"
            }

            let ctx: JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = scope
                AccessContext = AccessContext.unrestricted (AnonymousSession "system")
                Attempt = 1
                Trigger = Trigger.Manual
                TriggerSource = ScheduledManually "admin"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = SchemaMigrationJobHandler.serialisePayload payload
                DeadLetterDestination = None
            }

            let! result = handler.Execute ctx
            Expect.equal result Success "job handler runs the migration to Success"

            let inst = readInstanceJson dos scope "inst-1"
            Expect.isTrue (jsonHas inst "Flag" "true") "instance migrated via the job handler"
        }

        testCaseAsync "migration job handler rejects a malformed payload permanently"
        <| async {
            let store, _, _ = mkStore ()
            let handler = SchemaMigrationJobHandler.create store

            let ctx: JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = "team-a"
                AccessContext = AccessContext.unrestricted (AnonymousSession "system")
                Attempt = 1
                Trigger = Trigger.Manual
                TriggerSource = ScheduledManually "admin"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = "{ not json"
                DeadLetterDestination = None
            }

            let! result = handler.Execute ctx

            match result with
            | PermanentFailure _ -> ()
            | other -> failtestf "expected PermanentFailure, got %A" other
        }

        testCaseAsync "Delete is idempotent"
        <| async {
            let store, _, _ = mkStore ()
            let scope = "team-a"
            let! _ = store.Save(scope, sampleSchema "expense" scope, "u")
            let! d1 = store.Delete(scope, "expense", "u")
            Expect.isOk d1 "first delete ok"
            let! d2 = store.Delete(scope, "expense", "u")
            Expect.isOk d2 "second delete idempotent"
            let! got = store.Get(scope, "expense")

            match got with
            | Error(SchemaNotFound _) -> ()
            | other -> failtestf "expected SchemaNotFound after delete, got %A" other
        }
    ]