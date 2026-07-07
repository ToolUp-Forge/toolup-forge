module ToolUp.Platform.Tests.InProcess.ModuleApiFactoryTests

// ─── Phase 495 — Module API-factory helper contract tests ────────────
//
// Contract coverage for `ModuleApiFactory` / `ModuleApiContext` /
// `NarrativeSettings` (ToolUp.Platform.Server): the settings-key
// convention, scope-aware `GetFileContents` (incl. the
// `FileNotFoundInSessionException` contract), the `FromFile` endpoint
// wrapper, provenance stamping + replace-latest publish semantics, the
// no-store no-op, and the best-effort `TryPublishNarrative` degrade.
//
// The tail is the Phase 495.C reference migration: one consumer-shaped
// API factory written both ways — `demoApiBefore` (the verbatim
// composition-root boilerplate every consumer module repeats) and
// `demoApiAfter` (the helper) — with a test proving identical runtime
// behaviour. Line counts are recorded in
// docs/migrations/495-module-api-factory.md.

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.FileManagement
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.Narrative
open DataManagementTypes

// ─── Rig ──────────────────────────────────────────────────────────────

let private mkScope () =
    let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)

    {
        ScopeId = "maf-" + suffix
        Container = "maf-" + suffix
        Persist = false
    }

/// Request context shaped like a live handler's: `DataType list` in DI
/// (as `ScopeResolutionMiddleware`-era compose does), the resolved
/// `StorageScope` in `Items`, and optionally an `INarrativeStore`.
let private buildCtx (scope: StorageScope) (narrativeStore: INarrativeStore option) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<DataType list>([]) |> ignore

    match narrativeStore with
    | Some s -> services.AddSingleton<INarrativeStore>(s) |> ignore
    | None -> ()

    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Items["ToolUp.StorageScope"] <- box scope
    ctx

/// Seed a file into the scope's session store — the same store
/// `getFileContents` resolves (keyed by `scope.Container`).
let private seedFile (scope: StorageScope) (fileName: string) (contents: string) =
    let store = getStore [] None FileManagementRuntime.empty scope

    let result =
        store.AddFile(
            {
                filename = fileName
                contents = contents
                dataType = "UnrecognisedData"
            },
            "maf-test-user"
        )
        |> Async.RunSynchronously

    match result with
    | Ok _ -> ()
    | Error e -> failwithf "seedFile: AddFile failed: %s" e

let private mkDoc (title: string) (subtitle: string option) : NarrativeDocument = {
    Title = title
    Subtitle = subtitle
    Sections = []
    Provenance = None
    Lang = None
    CanonicalUrl = None
}

/// `INarrativeStore` whose every write throws — drives the
/// `TryPublishNarrative` degrade path.
let private throwingStore () : INarrativeStore =
    let boom () : 'a = failwith "narrative store unavailable"

    { new INarrativeStore with
        member _.Publish(_, _, _, _) = async { return boom () }
        member _.PublishTagged(_, _, _, _, _) = async { return boom () }
        member _.ReplaceLatest(_, _, _, _, _) = async { return boom () }
        member _.ReplaceLatestTagged(_, _, _, _, _, _) = async { return boom () }
        member _.List(_, _) = async { return boom () }
        member _.ListByTag(_, _, _) = async { return boom () }
        member _.Get(_, _) = async { return boom () }
        member _.GetSection(_, _, _) = async { return boom () }
        member _.DeleteScope(_) = async { return boom () }
    }

// ─── Phase 495.C reference migration — consumer-shaped demo module ────
//
// A miniature of the real consumer factory blocks (SalesAnalysis /
// ChannelAnalysis / DmaExplorer shape): one plain file-backed endpoint
// + one narrative-publishing endpoint. Domain routines stay pure; the
// factory is the only thing that changes between BEFORE and AFTER.

type private DemoInsightRequest = {
    FileName: string
    Region: string
    Window: int
}

type private DemoInsightResult = {
    Summary: string
    Narrative: NarrativeDocument option
}

type private DemoApi = {
    GetPreview: string -> Async<string>
    RunInsight: DemoInsightRequest -> Async<DemoInsightResult>
}

let private previewRoutine (contents: string) : string = contents.Split('\n')[0]

let private insightRoutine (contents: string) (request: DemoInsightRequest) : DemoInsightResult = {
    Summary = sprintf "%s:%d rows=%d" request.Region request.Window (contents.Split('\n').Length)
    Narrative = Some(mkDoc "Demo insight" (Some request.Region))
}

// ── BEFORE — the verbatim composition-root shape (consumer Wiring.fs
//    boilerplate: per-endpoint getFileContents plumbing, sprintf
//    settings-key construction, 7-arg publishWithProvenance call).
//    32 code lines (see migration doc for the count method). ──
let private demoApiBefore (ctx: HttpContext) : DemoApi = {
    GetPreview =
        fun fileName -> async {
            let contents = getFileContents ctx fileName
            return previewRoutine contents
        }
    RunInsight =
        fun request -> async {
            let contents = getFileContents ctx request.FileName
            let result = insightRoutine contents request

            match result.Narrative with
            | Some doc ->
                let settingsKey =
                    sprintf "file=%s|region=%s|window=%d" request.FileName request.Region request.Window

                let settingsDisplay = [
                    "Data file", request.FileName
                    "Region", request.Region
                    "Window", string request.Window
                ]

                let! stamped =
                    NarrativePublisher.publishWithProvenance
                        ctx
                        "Demo"
                        (Some "/insight")
                        settingsKey
                        settingsDisplay
                        doc.Subtitle
                        doc

                return { result with Narrative = Some stamped }
            | None -> return result
        }
}

// ── AFTER — the Phase 495 helper: bind once, collapse per endpoint.
//    24 code lines (see migration doc for the count method). ──
let private demoApiAfter (ctx: HttpContext) : DemoApi =
    let m = ModuleApiFactory.create "Demo" ctx

    {
        GetPreview = m.FromFile(id, fun contents _ -> previewRoutine contents)
        RunInsight =
            fun request -> async {
                let result = insightRoutine (m.GetFileContents request.FileName) request

                match result.Narrative with
                | Some doc ->
                    let settings =
                        NarrativeSettings.create [
                            "file", request.FileName
                            "region", request.Region
                            "window", string request.Window
                        ] [
                            "Data file", request.FileName
                            "Region", request.Region
                            "Window", string request.Window
                        ]

                    let! stamped = m.PublishNarrative(Some "/insight", settings, doc)
                    return { result with Narrative = Some stamped }
                | None -> return result
            }
    }

// ─── Tests ────────────────────────────────────────────────────────────

let private csv = "header_a,header_b\n1,2\n3,4\n"

let tests =
    testList "Phase 495 — ModuleApiFactory" [

        // ── NarrativeSettings — the settings-key convention ──
        testCase "NarrativeSettings.key collapses ordered pairs into key=value|key=value"
        <| fun () ->
            Expect.equal
                (NarrativeSettings.key [ "country", "UK"; "brand", "X"; "last52", "true" ])
                "country=UK|brand=X|last52=true"
                "canonical collapse, source order preserved"

            Expect.equal (NarrativeSettings.key []) "" "empty pair list collapses to the empty key"

        testCase "NarrativeSettings.ofPairs uses one pair list for both key and display"
        <| fun () ->
            let pairs = [ "channel", "TV"; "audience", "Adults" ]
            let s = NarrativeSettings.ofPairs pairs
            Expect.equal s.Key "channel=TV|audience=Adults" "key collapsed from the pairs"
            Expect.equal s.Display pairs "display is the same pairs verbatim"

        testCase "NarrativeSettings.create keeps machine key and display pairs distinct"
        <| fun () ->
            let s =
                NarrativeSettings.create [ "sku", "S1"; "market", "North" ] [ "SKU", "Brand S1 (North)" ]

            Expect.equal s.Key "sku=S1|market=North" "key from the machine pairs"
            Expect.equal s.Display [ "SKU", "Brand S1 (North)" ] "display pairs verbatim"

        // ── GetFileContents / FromFile — scope-aware file access ──
        testCaseAsync "GetFileContents resolves the request-scoped file"
        <| async {
            let scope = mkScope ()
            seedFile scope "data.csv" csv
            let m = ModuleApiFactory.create "Demo" (buildCtx scope None)
            Expect.equal (m.GetFileContents "data.csv") csv "contents round-trip through the scope's store"
        }

        testCase "GetFileContents raises FileNotFoundInSessionException for a missing file"
        <| fun () ->
            let scope = mkScope ()
            let m = ModuleApiFactory.create "Demo" (buildCtx scope None)

            Expect.throwsT<FileNotFoundInSessionException>
                (fun () -> m.GetFileContents "absent.csv" |> ignore)
                "missing file must surface the 4xx-classified exception, matching hand-rolled wiring"

        testCaseAsync "FromFile threads file contents and the request into the routine"
        <| async {
            let scope = mkScope ()
            seedFile scope "data.csv" csv
            let m = ModuleApiFactory.create "Demo" (buildCtx scope None)

            let endpoint =
                m.FromFile(_.FileName, (fun contents r -> sprintf "%s@%d:%s" r.Region r.Window contents))

            let! result =
                endpoint {
                    FileName = "data.csv"
                    Region = "North"
                    Window = 4
                }

            Expect.equal result (sprintf "North@4:%s" csv) "routine sees both the contents and the request"
        }

        // ── PublishNarrative — provenance stamp + store semantics ──
        testCaseAsync "PublishNarrative stamps provenance and stores under the module id"
        <| async {
            let scope = mkScope ()
            let store = InMemoryNarrativeStore() :> INarrativeStore
            let m = ModuleApiFactory.create "Demo" (buildCtx scope (Some store))
            let settings = NarrativeSettings.ofPairs [ "region", "North" ]

            let! stamped = m.PublishNarrative(Some "/insight", settings, mkDoc "Doc" (Some "North"))

            match stamped.Provenance with
            | None -> failtest "returned document must carry the provenance stamp"
            | Some p ->
                Expect.equal p.ModuleId "Demo" "module id from the bound context"
                Expect.equal p.PageRoute (Some "/insight") "page route as supplied"
                Expect.equal p.SettingsKey "region=North" "canonical settings key"
                Expect.equal p.SettingsDisplay [ "region", "North" ] "display pairs preserved"

            let! entries = store.List(scope.ScopeId, 10)

            let entry =
                Expect.wantSome (entries |> List.tryHead) "one entry stored in the request scope"

            Expect.equal entry.ModuleId "Demo" "stored under the module id"
            Expect.equal entry.Subtitle (Some "North") "subtitle defaulted from the document's own Subtitle"
        }

        testCaseAsync "PublishNarrative replaces the latest entry for the same subtitle key"
        <| async {
            let scope = mkScope ()
            let store = InMemoryNarrativeStore() :> INarrativeStore
            let m = ModuleApiFactory.create "Demo" (buildCtx scope (Some store))
            let settings = NarrativeSettings.ofPairs [ "region", "North" ]

            // The default overload keys the replacement on the document's
            // own Subtitle — a regenerated narrative for the same subject
            // (same SKU / channel / market) replaces its predecessor.
            let! _ = m.PublishNarrative(Some "/insight", settings, mkDoc "First" (Some "k"))
            let! _ = m.PublishNarrative(Some "/insight", settings, mkDoc "Second" (Some "k"))
            let! sameKey = store.List(scope.ScopeId, 10)
            Expect.hasLength sameKey 1 "same (module, route, subtitleKey) replaces — not appends"
            Expect.equal sameKey.Head.Title "Second" "the replacement is the latest document"

            let! _ = m.PublishNarrative(Some "/insight", settings, mkDoc "Third" (Some "other"))
            let! distinctKey = store.List(scope.ScopeId, 10)
            Expect.hasLength distinctKey 2 "a distinct subtitle key appends a separate entry"
        }

        testCaseAsync "PublishNarrative without a registered store still returns the stamped document"
        <| async {
            let scope = mkScope ()
            let m = ModuleApiFactory.create "Demo" (buildCtx scope None)

            let! stamped = m.PublishNarrative(None, NarrativeSettings.ofPairs [ "a", "1" ], mkDoc "Doc" None)

            let p =
                Expect.wantSome stamped.Provenance "stamp applies even when the publish is a no-op"

            Expect.equal p.SettingsKey "a=1" "provenance carries the settings key"
        }

        testCaseAsync "TryPublishNarrative degrades to the unstamped document on store failure"
        <| async {
            let scope = mkScope ()
            let m = ModuleApiFactory.create "Demo" (buildCtx scope (Some(throwingStore ())))
            let doc = mkDoc "Doc" (Some "sub")

            let! result = m.TryPublishNarrative(Some "/x", NarrativeSettings.ofPairs [ "a", "1" ], doc)

            Expect.equal result doc "publish failure returns the original document, unstamped (DmaExplorer posture)"
        }

        // ── Phase 495.C — reference migration: BEFORE ≡ AFTER ──
        testCaseAsync "reference migration: helper-based factory behaves identically to the hand-rolled one"
        <| async {
            // Two isolated scopes, one per factory shape, so store
            // contents can be compared independently.
            let scopeBefore = mkScope ()
            let scopeAfter = mkScope ()
            seedFile scopeBefore "sales.csv" csv
            seedFile scopeAfter "sales.csv" csv
            let storeBefore = InMemoryNarrativeStore() :> INarrativeStore
            let storeAfter = InMemoryNarrativeStore() :> INarrativeStore
            let apiBefore = demoApiBefore (buildCtx scopeBefore (Some storeBefore))
            let apiAfter = demoApiAfter (buildCtx scopeAfter (Some storeAfter))

            // Plain file-backed endpoint.
            let! previewBefore = apiBefore.GetPreview "sales.csv"
            let! previewAfter = apiAfter.GetPreview "sales.csv"
            Expect.equal previewAfter previewBefore "GetPreview identical"

            // Narrative-publishing endpoint.
            let request = {
                FileName = "sales.csv"
                Region = "North"
                Window = 4
            }

            let! resultBefore = apiBefore.RunInsight request
            let! resultAfter = apiAfter.RunInsight request
            Expect.equal resultAfter.Summary resultBefore.Summary "domain result identical"

            let provOf (r: DemoInsightResult) =
                match r.Narrative with
                | Some { Provenance = Some p } -> p
                | _ -> failtest "narrative must be stamped"

            let pBefore = provOf resultBefore
            let pAfter = provOf resultAfter
            // GeneratedAt is a fresh timestamp per publish — compare
            // every other provenance field.
            Expect.equal pAfter.ModuleId pBefore.ModuleId "provenance module id identical"
            Expect.equal pAfter.PageRoute pBefore.PageRoute "provenance page route identical"
            Expect.equal pAfter.SettingsKey pBefore.SettingsKey "provenance settings key identical"
            Expect.equal pAfter.SettingsDisplay pBefore.SettingsDisplay "provenance settings display identical"

            // Stored entries match too (scope-local stores, one each).
            let! entriesBefore = storeBefore.List(scopeBefore.ScopeId, 10)
            let! entriesAfter = storeAfter.List(scopeAfter.ScopeId, 10)
            Expect.hasLength entriesBefore 1 "hand-rolled factory stored one entry"
            Expect.hasLength entriesAfter 1 "helper factory stored one entry"

            let eBefore = entriesBefore.Head
            let eAfter = entriesAfter.Head
            Expect.equal eAfter.ModuleId eBefore.ModuleId "stored module id identical"
            Expect.equal eAfter.PageRoute eBefore.PageRoute "stored page route identical"
            Expect.equal eAfter.Title eBefore.Title "stored title identical"
            Expect.equal eAfter.Subtitle eBefore.Subtitle "stored subtitle identical"
        }
    ]