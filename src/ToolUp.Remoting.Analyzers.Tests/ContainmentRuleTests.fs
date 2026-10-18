module ToolUp.Remoting.Analyzers.Tests.ContainmentRuleTests

open Expecto
open FSharp.Compiler.Text
open FSharp.Analyzers.SDK
open ToolUp.Remoting.Analyzers

// ─── Phase 776 — TUA001–TUA003, the rule metadata, the inventory ─────
//
// Each rule has a fixture that fires and a sanctioned-form fixture that does
// not. Everything here is in memory (parse-only FCS over string fixtures, and
// `TreeScan.reconcile` over literal data), so the pack stays in the `pure`
// lane: the filesystem half of the tree run is the VerifyAll leg's, not
// this pack's.

/// Through the same parse-and-analyse path the VerifyAll leg takes, so the
/// fixtures exercise `TreeScan.scanSource` as well as the rules.
let private codes (source: string) =
    match TreeScan.scanSource "fixture.fs" source with
    | Ok sites -> sites |> List.map fst
    | Error e -> failtestf "fixture does not parse: %s" e

// ── TUA001 ───────────────────────────────────────────────────────────

let private handResolution =
    """module Demo
let handler (ctx: HttpContext) =
    let store = ctx.RequestServices.GetService(typeof<IStore>) :?> IStore
    let log = ctx.GetService<ILogger>()
    store, log

let fromProvider (sp: System.IServiceProvider) = sp.GetRequiredService<IStore>()
"""

let private resolutionInRegistration =
    """module Demo
let configure (services: IServiceCollection) =
    services
        .AddSingleton<IStore>(fun sp -> Store(sp.GetRequiredService<IBlobStorage>()) :> IStore)
        .AddScoped<IReader>(System.Func<System.IServiceProvider, IReader>(fun sp -> Reader(sp.GetService<IStore>())))
    |> ignore
    services.TryAddSingleton<IClock>(fun sp -> Clock(sp.GetService<ILogger>()))

type Delegating(inner: System.IServiceProvider) =
    interface System.IServiceProvider with
        member _.GetService(t) = inner.GetService t
"""

// ── TUA002 ───────────────────────────────────────────────────────────

let private handBuiltClients =
    """module Demo
let a = new HttpClient()
let b = new System.Net.Http.HttpClient(Timeout = System.TimeSpan.FromSeconds 15.0)
let c = HttpClient(new HttpClientHandler())
"""

let private egressClients =
    """module Demo
let a = PlatformHttpClient.create EgressSurface.Other
let b = new HttpClient(PlatformHttpClient.wrap EgressSurface.Other (new HttpClientHandler()), Timeout = System.TimeSpan.FromSeconds 5.0)
let handler = new HttpClientHandler(AllowAutoRedirect = false)
"""

// ── TUA003 ───────────────────────────────────────────────────────────

let private blockingOnRequestPath =
    """module Demo
let handle (store: IStore) id =
    let item = store.Get id |> Async.RunSynchronously
    let other = Microsoft.FSharp.Control.Async.RunSynchronously(store.Get id, 1000)
    item, other
"""

let private blockingAtEntryPoint =
    """module Demo
let run () = async { return 0 }

[<EntryPoint>]
let main argv =
    run () |> Async.RunSynchronously
"""

let private ruleTests =
    testList "Phase 776 — TUA001–TUA003" [
        test "TUA001 fires on hand resolution from request services and from a provider" {
            Expect.equal (codes handResolution) [ "TUA001"; "TUA001"; "TUA001" ] "three resolution sites"
        }

        test "TUA001 is silent inside a registration factory and inside a provider's own GetService" {
            Expect.isEmpty (codes resolutionInRegistration) "registration and delegation are the sanctioned shapes"
        }

        test "TUA002 fires on every HttpClient built without the egress handler" {
            Expect.equal (codes handBuiltClients) [ "TUA002"; "TUA002"; "TUA002" ] "new, qualified and new-less forms"
        }

        test "TUA002 is silent when the construction routes through PlatformHttpClient, and on HttpClientHandler" {
            Expect.isEmpty (codes egressClients) "the egress seam is the sanctioned shape"
        }

        test "TUA003 fires on Async.RunSynchronously in an ordinary function, qualified or not" {
            Expect.equal (codes blockingOnRequestPath) [ "TUA003"; "TUA003" ] "both blocking sites"
        }

        test "TUA003 is silent inside an [<EntryPoint>]" {
            Expect.isEmpty (codes blockingAtEntryPoint) "a program's entry point may block"
        }

        test "a TUA message is a Warning that names the sanctioned form" {
            let message =
                ContainmentRules.toMessage {
                    Code = "TUA002"
                    Range = Range.range0
                }

            Expect.equal
                message.Severity
                Severity.Warning
                "warning in the editor; the repository gate treats any finding as red"

            Expect.stringContains message.Message "Sanctioned form" "the fix travels with the finding"
            Expect.stringContains message.Message "PlatformHttpClient" "and names the seam"
        }
    ]

// ── Rule metadata and the generated document ─────────────────────────

let private metadataTests =
    testList "Phase 776 — rule metadata" [
        test "rule ids are unique and every field is filled" {
            let ids = Rules.all |> List.map _.Id
            Expect.equal (List.distinct ids) ids "no duplicate id"

            for r in Rules.all do
                for (name, value) in
                    [
                        "Title", r.Title
                        "Severity", r.Severity
                        "Rationale", r.Rationale
                        "Sanctioned", r.Sanctioned
                        "RuntimeHalf", r.RuntimeHalf
                        "Since", r.Since
                    ] do
                    Expect.isFalse (System.String.IsNullOrWhiteSpace value) (sprintf "%s.%s is filled" r.Id name)
        }

        test "the rendered document carries every rule and the wiring note" {
            let doc = Rules.renderDocument ()

            for r in Rules.all do
                Expect.stringContains doc (sprintf "### %s" r.Id) (sprintf "%s has a section" r.Id)

            Expect.stringContains doc "PackageReference" "consumer wiring note"
            Expect.stringContains doc "FSharpAnalyzersOtherFlags" "the MSBuild property"
            Expect.isFalse (doc.Contains "\r") "rendered with LF only"
        }
    ]

// ── The inventory ────────────────────────────────────────────────────

let private inventoryJson =
    """{
  "exclude": [
    { "segmentSuffix": ".Tests", "reason": "test packs" },
    { "prefix": "src/Bench/", "reason": "benchmarks" }
  ],
  "suppressions": [
    { "rule": "TUA003", "reason": "compose-time schema probe", "sites": { "src/Store/Store.fs": 2 } }
  ]
}"""

let private inventory =
    match TreeScan.parseInventory inventoryJson with
    | Ok inv -> inv
    | Error e -> failwithf "fixture inventory does not parse: %A" e

let private tracked = [
    "src/Store/Store.fs"
    "src/Store.Tests/StoreTests.fs"
    "src/Bench/Run.fs"
    "src/Other/Other.fs"
]

let private inventoryTests =
    testList "Phase 776 — analyser inventory" [
        test "an inventory that matches the tree exactly reconciles clean" {
            let observed = Map.ofList [ ("TUA003", "src/Store/Store.fs"), [ 10; 20 ] ]
            Expect.isEmpty (TreeScan.reconcile inventory tracked observed) "equal counts, live exclusions"
        }

        test "a site the inventory does not account for is a finding naming its line" {
            let observed =
                Map.ofList [
                    ("TUA003", "src/Store/Store.fs"), [ 10; 20 ]
                    ("TUA002", "src/Other/Other.fs"), [ 7 ]
                ]

            match TreeScan.reconcile inventory tracked observed with
            | [ finding ] ->
                Expect.stringContains finding "src/Other/Other.fs" "names the file"
                Expect.stringContains finding "line(s) 7" "names the line"
            | other -> failtestf "expected one finding, got %A" other
        }

        test "a count the tree no longer needs is a finding, so the inventory only tightens deliberately" {
            let observed = Map.ofList [ ("TUA003", "src/Store/Store.fs"), [ 10 ] ]

            match TreeScan.reconcile inventory tracked observed with
            | [ finding ] -> Expect.stringContains finding "tighten the inventory to 1" "names the new count"
            | other -> failtestf "expected one finding, got %A" other
        }

        test "an exclusion matching no tracked file is a finding" {
            let observed = Map.ofList [ ("TUA003", "src/Store/Store.fs"), [ 10; 20 ] ]

            let findings =
                TreeScan.reconcile inventory [ "src/Store/Store.fs"; "src/Other/Other.fs" ] observed

            Expect.equal findings.Length 2 "both exclusions are stale against this tree"
            Expect.all findings (fun f -> f.StartsWith "stale exclusion") "each is named as stale"
        }

        test "a segment suffix matches directories, never the file name" {
            Expect.isTrue (TreeScan.isExcludedBy (TreeScan.SegmentSuffix ".Tests") "src/A/B.Tests/X.fs") "directory"
            Expect.isFalse (TreeScan.isExcludedBy (TreeScan.SegmentSuffix ".Tests") "src/A/X.Tests") "file name"
        }

        test "an unknown rule, a missing reason and a doubly-listed site are each refused by name" {
            let bad =
                """{
  "exclude": [],
  "suppressions": [
    { "rule": "TUA999", "reason": "x", "sites": { "src/a.fs": 1 } },
    { "rule": "TUA001", "sites": { "src/b.fs": 1 } },
    { "rule": "TUA002", "reason": "x", "sites": { "src/c.fs": 1 } },
    { "rule": "TUA002", "reason": "y", "sites": { "src/c.fs": 2 } }
  ]
}"""

            match TreeScan.parseInventory bad with
            | Ok _ -> failtest "a malformed inventory must not parse"
            | Error errors ->
                let all = String.concat "\n" errors
                Expect.stringContains all "unknown rule `TUA999`" "unknown rule"
                Expect.stringContains all "missing or empty `reason`" "missing reason"
                Expect.stringContains all "TUA002 src/c.fs is listed in more than one group" "duplicate site"
        }
    ]

[<Tests>]
let tests =
    testList "ToolUp.Remoting.Analyzers — containment rules (Phase 776)" [ ruleTests; metadataTests; inventoryTests ]