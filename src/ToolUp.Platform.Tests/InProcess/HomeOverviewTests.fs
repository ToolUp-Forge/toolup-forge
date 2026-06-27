module ToolUp.Platform.Tests.InProcess.HomeOverviewTests

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts
open DataManagementTypes
open ToolUp.Platform.FileProcessor
open ProcessedDataTypes

// ─── Phase 171 — Home / Overview landing-surface verification ────────
//
// The Phase 171 implementation shipped via forge `e9fff7b`; its three
// verification tests were deferred at ship time because
// `ToolUp.Platform.Tests` transitively references the Client tier,
// which a concurrent session's then-non-compiling `MappingDataManagerUI.fs`
// blocked. Phase 172 (forge `e6f3420`) cleared that blocker, so the
// three tests land here:
//
//   1. count-affordance — `IDataCatalog.CountObjects`' portable default
//      equals `(ListObjects …).Length`, and a store that also implements
//      `IObjectCounter` returns the same number WITHOUT the `ListObjects`
//      path being taken (GP 12 — efficient where possible).
//   2. landing-selection — `prepareModules` head-injects the Home module
//      so the shell lands on `_sdk.home` when `ActiveModule = None`; an
//      explicit `ActiveModule` still wins; `NoHomeModule` leaves Home
//      absent entirely (GP 13 — off by default).
//   3. overview scope/AI — `GetOverview` counts are scope-correct (a
//      second scope's objects are not counted, GP 4) and `ActiveAi` is
//      `None` when no `IActiveAiProbe` is registered (GP 1 / GP 13).
//
// Production code (`DataCatalog` / `IObjectCounter` / `prepareModules`
// / `HomeOverviewApiHandler` / `IActiveAiProbe`) is read-only here — this
// is verification only.

// ─── Shared catalog fixtures ─────────────────────────────────────────

let private stubProcess (_: string * string) : Async<ProcessedData * ProcessedFileEntry> = async {
    return
        { TypeName = ""; Payload = "" },
        {
            FileName = ""
            DataType = ""
            ProcessedAt = DateTime.UtcNow
            Info = None
            Error = None
        }
}

let private mkType (id: string) (displayName: string) : DataType = {
    Info = {
        Id = id
        DisplayName = displayName
        Schema = None
    }
    Id = id
    Detect = fun _ -> async { return false }
    Process = stubProcess
}

let private catalogOver (registrations: (string * DataType) list) (store: IDataObjectStore) : IDataCatalog =
    let catalogRegistrations: DataCatalog.DataTypeRegistration list =
        registrations |> List.map (fun (m, dt) -> { ModuleName = m; DataType = dt })

    DataCatalog.DataCatalog(catalogRegistrations, store) :> IDataCatalog

let private bytesOf (s: string) = System.Text.Encoding.UTF8.GetBytes s

/// An `IDataObjectStore` that ALSO implements the optional fast-path
/// `IObjectCounter`. Every `IDataObjectStore` member delegates to a real
/// in-memory store; the wrapper's own `ListObjects` flips
/// `listObjectsTaken` so a test can assert the catalog never reached the
/// fall-back enumeration path. `CountObjects` counts via the *inner*
/// store's `ListObjects` (not the wrapper's), so the native-count branch
/// leaves the flag clear — exactly what the catalog should observe.
type private CountingObjectStore(inner: IDataObjectStore) =
    let mutable listObjectsTaken = false

    /// True once the wrapper's own `ListObjects` was invoked — i.e. the
    /// catalog fell back to the enumerate-and-count path.
    member _.ListObjectsTaken = listObjectsTaken

    interface IDataObjectStore with
        member _.Save(scopeId, objectId, content, dataType, createdBy, metadata, policy) =
            inner.Save(scopeId, objectId, content, dataType, createdBy, metadata, policy)

        member _.Get(scopeId, objectId) = inner.Get(scopeId, objectId)

        member _.GetVersion(scopeId, objectId, version) =
            inner.GetVersion(scopeId, objectId, version)

        member _.GetContent(scopeId, contentHash) = inner.GetContent(scopeId, contentHash)
        member _.ListVersions(scopeId, objectId) = inner.ListVersions(scopeId, objectId)

        member _.ListObjects(scopeId) =
            listObjectsTaken <- true
            inner.ListObjects scopeId

        member _.Recover(scopeId, objectId, version, createdBy) =
            inner.Recover(scopeId, objectId, version, createdBy)

        member _.Delete(scopeId, objectId) = inner.Delete(scopeId, objectId)
        member _.Evict(scopeId, objectId) = inner.Evict(scopeId, objectId)
        member _.Purge(scopeId) = inner.Purge(scopeId)

        member _.Erase(scopeId, subjectUserId, policy, dryRun) =
            inner.Erase(scopeId, subjectUserId, policy, dryRun)

    interface IObjectCounter with
        member _.CountObjects(scopeId, dataType) = async {
            // Native fast-path: count via the inner store directly so the
            // wrapper's tracked `ListObjects` is NOT taken.
            let! all = inner.ListObjects scopeId
            return all |> List.filter (fun o -> o.DataType = dataType) |> List.length
        }

// ─── Test 1 — count affordance (pure server) ─────────────────────────

[<Tests>]
let countAffordanceTests =
    testList "Phase 171 — IDataCatalog.CountObjects affordance" [

        testCaseAsync "portable default equals (ListObjects typeId).Length"
        <| async {
            // A store with NO IObjectCounter → the catalog falls back to
            // ListObjects |> filter |> length. The count must equal the
            // length of the same filtered list.
            let store = IDataCatalogContract.inMemoryObjectStore ()
            let catalog = catalogOver [ "SalesAnalysis", mkType "Sales" "Sales Data" ] store

            Expect.isFalse (store :? IObjectCounter) "fixture store does not implement IObjectCounter"

            let scope = "scope-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let! _ = store.Save(scope, "s1", bytesOf "a", "Sales", "u", Map.empty, Unversioned)
            let! _ = store.Save(scope, "s2", bytesOf "b", "Sales", "u", Map.empty, Unversioned)
            let! _ = store.Save(scope, "s3", bytesOf "c", "Sales", "u", Map.empty, Unversioned)
            // A non-matching type must not inflate the Sales count.
            let! _ = store.Save(scope, "m1", bytesOf "d", "Media", "u", Map.empty, Unversioned)

            let! listed = catalog.ListObjects(scope, "Sales")
            let! count = catalog.CountObjects(scope, "Sales")

            Expect.equal count listed.Length "CountObjects equals (ListObjects typeId).Length"
            Expect.equal count 3 "three Sales objects counted (Media excluded)"

            let! emptyCount = catalog.CountObjects(scope, "Unknown")
            Expect.equal emptyCount 0 "unknown type counts zero"
        }

        testCaseAsync "IObjectCounter store returns the same number without taking ListObjects"
        <| async {
            // A store that ALSO implements IObjectCounter → the catalog
            // uses the native count and never enumerates via the store's
            // ListObjects. Same number, no full materialisation (GP 12).
            let inner = IDataCatalogContract.inMemoryObjectStore ()
            let counting = CountingObjectStore(inner)
            let store = counting :> IDataObjectStore
            let catalog = catalogOver [ "SalesAnalysis", mkType "Sales" "Sales Data" ] store

            Expect.isTrue (store :? IObjectCounter) "wrapper store implements IObjectCounter"

            let scope = "scope-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let! _ = store.Save(scope, "s1", bytesOf "a", "Sales", "u", Map.empty, Unversioned)
            let! _ = store.Save(scope, "s2", bytesOf "b", "Sales", "u", Map.empty, Unversioned)
            let! _ = store.Save(scope, "m1", bytesOf "c", "Media", "u", Map.empty, Unversioned)

            let! count = catalog.CountObjects(scope, "Sales")

            Expect.equal count 2 "native counter returns the scope-correct count"

            Expect.isFalse
                counting.ListObjectsTaken
                "catalog used the IObjectCounter fast-path — store's ListObjects not taken"
        }
    ]

// ─── Test 2 (landing selection) lives in the Fable harness ───────────
//
// The landing-selection test ("`EnabledHomeModule` + `ActiveModule =
// None` ⇒ shell lands on `_sdk.home`; explicit `ActiveModule` wins;
// `NoHomeModule` ⇒ Home absent") exercises `Client.prepareModules`,
// which requires a `ClientConfig`. Building one in-process is
// impossible: `ClientConfig.defaults` / `ClientConfig.create` eagerly
// evaluate `AgGridModuleConfig.community`, whose module init is a Fable
// `JsInterop.import` that throws under .NET ("You've hit dummy code used
// for Fable bindings"). This is the same reason `BuiltInModuleSurfaceTests.visibilityTests`
// (which also touches `ClientConfig.defaults`) is not wired into the
// in-process runner. The landing-selection test therefore lives in the
// Fable-tier harness `ToolUp.AI.Client.Tests/HomeLandingTests.fs`, where
// `ClientConfig.defaults` resolves against the real npm AG Grid module —
// the same rig the other Platform.Client-tier tests (ClientHostBridge /
// NotificationClient / BootDegradation / ConsentBanner) already use.

// ─── Test 3 — overview scope-correctness + AI absence ────────────────

let private buildHttpContext (accessContext: AccessContext) (catalog: IDataCatalog option) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<AccessContext>(accessContext) |> ignore

    match catalog with
    | Some c -> services.AddSingleton<IDataCatalog>(c) |> ignore
    | None -> ()

    // Deliberately register NO IActiveAiProbe — the overview's ActiveAi
    // must resolve to None.
    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx

[<Tests>]
let overviewScopeAiTests =
    testList "Phase 171 — GetOverview scope-correctness + AI absence" [

        testCaseAsync "counts are scope-correct and ActiveAi is None without an IActiveAiProbe"
        <| async {
            let store = IDataCatalogContract.inMemoryObjectStore ()
            let catalog = catalogOver [ "SalesAnalysis", mkType "Sales" "Sales Data" ] store

            // Caller's scope (AuthenticatedUser → UserId is the scope id)
            // holds two Sales objects; a second scope holds a third that
            // must NOT be counted.
            let! _ = store.Save("scope-a", "a1", bytesOf "x", "Sales", "u", Map.empty, Unversioned)
            let! _ = store.Save("scope-a", "a2", bytesOf "y", "Sales", "u", Map.empty, Unversioned)
            let! _ = store.Save("scope-b", "b1", bytesOf "z", "Sales", "u", Map.empty, Unversioned)

            let accessContext = AccessContext.unrestricted (AuthenticatedUser "scope-a")
            let ctx = buildHttpContext accessContext (Some catalog)

            let api = HomeOverviewApiHandler.homeOverviewApi ctx
            let! overview = api.GetOverview()

            let salesTool =
                overview.Tools |> List.tryFind (fun t -> t.ModuleId = "SalesAnalysis")

            match salesTool with
            | Some tool ->
                Expect.equal tool.TotalRecords 2 "only the caller's scope is counted (scope-b excluded)"

                let salesCount =
                    tool.DataCounts
                    |> List.tryFind (fun d -> d.TypeId = "Sales")
                    |> Option.map _.Count

                Expect.equal salesCount (Some 2) "per-type Sales count is scope-correct"
            | None -> failtest "expected a SalesAnalysis tool in the overview"

            Expect.isNone overview.ActiveAi "ActiveAi is None when no IActiveAiProbe is registered"
            // Phase 217 — no IHomeWidgetDataProvider registered ⇒ the
            // widget-data bag is empty, so the overview is the
            // byte-for-byte Phase 171 shape (GP 13).
            Expect.isTrue (Map.isEmpty overview.WidgetData) "WidgetData is empty without a provider"
        }
    ]

// ─── Phase 217 — IHomeWidgetDataProvider merge + scope-correctness ───

/// A widget-data provider that echoes the scope id back under a fixed
/// key — lets the test assert the handler passes the *caller's* scope
/// (GP 4) and merges every provider's map (GP 9 / GP 1: the SDK never
/// names the providing module).
type private StubWidgetDataProvider(entries: string -> Map<string, string>) =
    interface IHomeWidgetDataProvider with
        member _.Describe scopeId = async { return entries scopeId }

let private ctxWithProviders (accessContext: AccessContext) (providers: IHomeWidgetDataProvider list) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<AccessContext>(accessContext) |> ignore

    for p in providers do
        services.AddSingleton<IHomeWidgetDataProvider>(p) |> ignore

    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx

[<Tests>]
let widgetDataTests =
    testList "Phase 217 — Home widget-data seam" [

        testCaseAsync "registered providers merge into WidgetData, scoped to the caller"
        <| async {
            let p1 =
                StubWidgetDataProvider(fun scope -> Map [ "w1.scope", scope; "w1.k", "a" ]) :> IHomeWidgetDataProvider

            let p2 =
                StubWidgetDataProvider(fun _ -> Map [ "w2.k", "b" ]) :> IHomeWidgetDataProvider

            let accessContext = AccessContext.unrestricted (AuthenticatedUser "scope-a")
            let ctx = ctxWithProviders accessContext [ p1; p2 ]

            let api = HomeOverviewApiHandler.homeOverviewApi ctx
            let! overview = api.GetOverview()

            Expect.equal
                (Map.tryFind "w1.scope" overview.WidgetData)
                (Some "scope-a")
                "provider sees the caller's scope (GP 4)"

            Expect.equal (Map.tryFind "w1.k" overview.WidgetData) (Some "a") "first provider's data is present"
            Expect.equal (Map.tryFind "w2.k" overview.WidgetData) (Some "b") "second provider's data is merged in"
            Expect.equal overview.WidgetData.Count 3 "every namespaced key from both providers is merged"
        }

        testCaseAsync "no provider ⇒ WidgetData is empty (GP 13)"
        <| async {
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "scope-a")
            let ctx = ctxWithProviders accessContext []

            let api = HomeOverviewApiHandler.homeOverviewApi ctx
            let! overview = api.GetOverview()

            Expect.isTrue (Map.isEmpty overview.WidgetData) "absent provider ⇒ empty bag"
        }
    ]