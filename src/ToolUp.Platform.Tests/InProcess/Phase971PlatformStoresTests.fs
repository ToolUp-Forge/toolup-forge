module ToolUp.Platform.Tests.InProcess.Phase971PlatformStoresTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.FileManagement
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.Providers
open DataManagementTypes
open ProcessedDataTypes
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.DataObjectStoreTests

// ─── Phase 971 — stores: a refused delete that is the operation ────────
//
// `IBlobStorage.Delete` answers `Ok` on a missing blob, so an `Error` is a
// refusal. A config / provider-profile Clear and a file delete each ARE the
// delete: a refusal returning normally would claim the thing is gone while
// it is still there. `Clear` (no failure channel) raises; `DeleteFile`
// answers its Error; `ResetDataStore` (an `Async<int>`) raises.

let private uniqueScope () =
    let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)

    {
        ScopeId = "team-971-" + suffix
        Container = "team-971-" + suffix
        Persist = true
    }

let private raises (work: Async<'a>) = async {
    match! Async.Catch work with
    | Choice1Of2 value -> return failtestf "expected the operation to raise, it answered %A" value
    | Choice2Of2 _ -> return ()
}

// ── Config ────────────────────────────────────────────────────────────

let private schema: ModuleConfigSchema =
    ModuleConfigSchema.ofFields [
        {
            Key = "currencySymbol"
            DisplayName = "Currency symbol"
            Description = None
            Kind = ConfigFieldKind.String(Some 4)
            Required = false
            DefaultJson = "\"£\""
        }
    ]

// ── Provider profile ──────────────────────────────────────────────────

let private profile: ProviderProfile = {
    Entries = []
    Routing = []
    Fallback = { Ordered = [] }
    SurfaceModelOverrides = [ "ai.platform", "model-971" ]
    SurfaceProviderOverrides = []
    UpdatedAt = DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc)
}

// ── Files ─────────────────────────────────────────────────────────────

[<Literal>]
let private TypeId = "Phase971Type"

let private dataType: DataType = {
    Info = {
        Id = TypeId
        DisplayName = "Phase 971 type"
        Schema = None
    }
    Id = TypeId
    SchemaVersion = DataTypes.initialSchemaVersion
    Migrations = []
    Detect = fun contents -> async { return contents.Contains "header_x" }
    Process =
        fun (fileName, contents) -> async {
            let entry =
                ProcessedFileEntry.summarised
                    fileName
                    TypeId
                    DateTime.UtcNow
                    (ProcessedDataCodec.encode {| Rows = contents.Split('\n').Length |})

            return
                {
                    TypeName = TypeId
                    Payload = contents
                },
                entry
        }
}

let private upload fileName : DataFileUpload = {
    filename = fileName
    contents = "header_x,header_y\n1,2\n"
    dataType = "UnrecognisedData"
}

/// A `SessionFileStore` over a data-object store whose blob deletes for
/// `fileName`'s own versions are refused while `refusing` holds; returns the
/// store, the data-object store (to reopen over), the scope and the switch.
let private fileRig (fileName: string) =
    let refusing = ref true
    let prefix = $"objects/{fileName}/"

    let blobs =
        DeleteRefusingBlobStorage(InMemoryBlobStorage(), fun n -> refusing.Value && n.StartsWith prefix)

    let dos = DataObjectStore(blobs) :> IDataObjectStore
    let scope = uniqueScope ()

    let store =
        SessionFileStore([ dataType ], Some dos, scope, FileManagementRuntime.empty)

    store, dos, scope, refusing

let private reopen (dos: IDataObjectStore) (scope: StorageScope) =
    SessionFileStore([ dataType ], Some dos, scope, FileManagementRuntime.empty)

let tests =
    testList "Phase 971 - PlatformStores" [
        testCaseAsync "ConfigStore.Clear raises on a refused delete and the document stays readable"
        <| async {
            let scope = uniqueScope ()
            let blobName = $"config/{scope.Container}/domain.custom.json"

            let store =
                ConfigStore.create (DeleteRefusingBlobStorage(InMemoryBlobStorage(), (=) blobName))

            match! store.SetRaw(scope, "domain.custom", Map.ofList [ "currencySymbol", "\"$\"" ], schema) with
            | Ok() -> ()
            | Error e -> failtestf "seeding failed: %s" e

            do! raises (store.Clear(scope, "domain.custom"))

            let! raw = store.GetRaw(scope, "domain.custom")
            Expect.equal (raw |> Map.tryFind "currencySymbol") (Some "\"$\"") "the document is still there"
        }

        testCaseAsync "ConfigStore.Clear stays idempotent on a missing document"
        <| async {
            let store = ConfigStore.create (InMemoryBlobStorage())
            do! store.Clear(uniqueScope (), "never-written")
        }

        testCaseAsync "BlobProviderProfile.Clear raises on a refused delete and the profile stays readable"
        <| async {
            let store =
                BlobProviderProfile.create (
                    DeleteRefusingBlobStorage(InMemoryBlobStorage(), (=) "provider-profile.json")
                )

            let scope = uniqueScope ()

            match! store.Set(scope, profile) with
            | Ok() -> ()
            | Error e -> failtestf "seeding failed: %s" e

            do! raises (store.Clear scope)

            let! got = store.Get scope
            Expect.equal got (Some profile) "the profile is still there"
        }

        testCaseAsync "SessionFileStore.DeleteFile answers Error on a refused delete and the file is still listed"
        <| async {
            let store, dos, scope, refusing = fileRig "data.csv"

            let! added = store.AddFile(upload "data.csv", "alice")
            Expect.isOk added "AddFile succeeds"

            let! deleted = store.DeleteFile "data.csv"

            match deleted with
            | Ok() -> failtest "a refused delete must not read as Ok"
            | Error e -> Expect.stringContains e "data.csv" "the error names the file"

            Expect.contains (store.GetFiles() |> List.map _.FileName) "data.csv" "still listed in memory"

            let restarted = reopen dos scope

            Expect.contains
                (restarted.GetFiles() |> List.map _.FileName)
                "data.csv"
                "still persisted: the caller was told the truth"

            refusing.Value <- false
            let! retried = store.DeleteFile "data.csv"
            Expect.isOk retried "the retry finishes"
            Expect.isEmpty ((reopen dos scope).GetFiles()) "gone once the delete goes through"
        }

        testCaseAsync "SessionFileStore.ResetDataStore raises on a refused delete"
        <| async {
            let store, dos, scope, _ = fileRig "data.csv"

            let! added = store.AddFile(upload "data.csv", "alice")
            Expect.isOk added "AddFile succeeds"

            do! raises (store.ResetDataStore())

            Expect.contains
                ((reopen dos scope).GetFiles() |> List.map _.FileName)
                "data.csv"
                "the refused file is still persisted"
        }
    ]