// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The forge gate's model of itself: ONE list of what the gate runs, in
/// which lanes, where (locally under `VerifyAll`, in a CI job, at release),
/// reading what, within what budget and above what floor.
///
/// Everything that used to restate the list is a render of these rows:
/// `BuildConfig.TestPacks` (`packs`), the pure lane (the `Reads = []` pack
/// rows), CI's `EXPECTED_PACKS` floor (`ciFloor`), the CI table in
/// `CLAUDE.md` (`ciTable`), the scrubbed-environment allowlist each pack is
/// launched under (`Gate.scrubbedFor`), `VerifyGate`'s gated targets and
/// stale-holder bound (`gatedTargets`, `fullBudget`), and the trailer
/// `VerifyAll` prints naming the rows it did not run. **A new gate is a row
/// here; nothing else is edited by hand.** `dotnet run -- Gate --check`
/// refuses a render that drifted and `Gate --render` rewrites it.
///
/// FAKE-free and source-linked into `ToolUp.Platform.Build.Tests`, so the
/// driver and `GateManifestTests` read one list rather than one of them
/// scraping `Build.fs`. The rows' `Body` legs need the driver's own
/// functions, so `rows` takes them as a record; the tests pass `unbound`,
/// whose every body answers `Pending`, and read the data.
module ToolUp.Forge.GateManifest

open System
open System.Text.RegularExpressions
open ToolUp.Platform
open ToolUp.Platform.Build
open ToolUp.Forge.TestLane

/// The leg bodies the driver binds. Each returns the leg's own `Verdict`;
/// a floor on the row is applied by the fold, never by the body.
type Bodies = {
    GateReconcile: unit -> Verdict
    Fable: unit -> Verdict
    Samples: unit -> Verdict
    Analyzers: unit -> Verdict
    ScopeLiterals: unit -> Verdict
    ServiceConfigFold: unit -> Verdict
    FuaranCoResolution: unit -> Verdict
    FuaranCoBuild: unit -> Verdict
    MergeDrivers: unit -> Verdict
    WireFable: unit -> Verdict
    WireConformanceFable: unit -> Verdict
    DocSnippets: unit -> Verdict
    SourceCitations: unit -> Verdict
    UploadResults: unit -> Verdict
    Templates: unit -> Verdict
    PackagedModuleTemplate: unit -> Verdict
    DataPlatformTemplate: unit -> Verdict
    BrowserSmoke: unit -> Verdict
    SemVerBump: unit -> Verdict
    ReleaseChannel: unit -> Verdict
    PublishedPackages: unit -> Verdict
}

/// Every body answers `Pending`: the shape a reader of the DATA passes.
let unbound: Bodies =
    let unboundBody () =
        Verdict.Pending "body not bound — the manifest was read without the driver"

    {
        GateReconcile = unboundBody
        Fable = unboundBody
        Samples = unboundBody
        Analyzers = unboundBody
        ScopeLiterals = unboundBody
        ServiceConfigFold = unboundBody
        FuaranCoResolution = unboundBody
        FuaranCoBuild = unboundBody
        MergeDrivers = unboundBody
        WireFable = unboundBody
        WireConformanceFable = unboundBody
        DocSnippets = unboundBody
        SourceCitations = unboundBody
        UploadResults = unboundBody
        Templates = unboundBody
        PackagedModuleTemplate = unboundBody
        DataPlatformTemplate = unboundBody
        BrowserSmoke = unboundBody
        SemVerBump = unboundBody
        ReleaseChannel = unboundBody
        PublishedPackages = unboundBody
    }

/// The job `VerifyAll` runs under in `checks.yml`; `ciFloor` counts the
/// rows it runs, which is CI's `EXPECTED_PACKS`.
[<Literal>]
let VerifyAllJob = "verify-all"

let private everyLane = set [ Lane.Pure; Lane.Fast; Lane.Affected; Lane.Full ]

/// Every lane but `pure`: a leg that reads the tree, a toolchain or the
/// network is the opposite of that lane's claim.
let private readingLanes = set [ Lane.Fast; Lane.Affected; Lane.Full ]

let private local = set [ Site.Local; Site.Ci VerifyAllJob ]

let private minutes (m: float) = Some(TimeSpan.FromMinutes m)
let private seconds (s: float) = Some(TimeSpan.FromSeconds s)

/// An Expecto pack `VerifyAll` runs. Its lanes follow its inputs: a pack
/// whose own test sources read nothing (`Reads = []`, the grep
/// `PureLaneTests` re-runs) is in the `pure` lane; every pack is in the
/// other three.
let private pack name project (reads: Input list) : GateLeg<Lane> = {
    Name = name
    Kind = LegKind.ExpectoPack project
    Lanes = if List.isEmpty reads then everyLane else readingLanes
    Where = local
    Reads = reads
    Budget = None
    Floor = None
}

/// A leg of `VerifyAll` with a body of its own.
let private leg name (body: unit -> Verdict) (lanes: Set<Lane>) (reads: Input list) : GateLeg<Lane> = {
    Name = name
    Kind = LegKind.Body body
    Lanes = lanes
    Where = local
    Reads = reads
    Budget = None
    Floor = None
}

/// A row CI (or the release workflow) runs and `VerifyAll` does not. Its
/// lanes are `Full` alone — it belongs to the gate's model, not to a lane's
/// selection — and the trailer `VerifyAll` prints names it with its reads.
let private elsewhere name kind (where: Site list) (reads: Input list) : GateLeg<Lane> = {
    Name = name
    Kind = kind
    Lanes = set [ Lane.Full ]
    Where = set where
    Reads = reads
    Budget = None
    Floor = None
}

let private budget (b: TimeSpan option) (row: GateLeg<Lane>) = { row with Budget = b }
let private floor' (f: Floor) (row: GateLeg<Lane>) = { row with Floor = Some f }

let private also (site: Site) (row: GateLeg<Lane>) = {
    row with
        Where = Set.add site row.Where
}

let private env (names: string list) = names |> List.map Input.Env

let private apiKeys =
    env [ "ANTHROPIC_API_KEY"; "OPENAI_API_KEY"; "GEMINI_API_KEY" ]

/// The variables the Platform pack's own sources read, each gating an arm
/// or a regeneration. `PATH` is ambient toolchain, not an input, and is not
/// here. Harness-wide variables (`TOOLUP_TEST_LANE`, `TOOLUP_TEST_LISTS`,
/// `TOOLUP_TEST_ARGS`) belong to the driver, not to a row.
let private platformEnv =
    env [
        "TOOLUP_MODEL_EXECUTION_CORPUS"
        "TOOLUP_FEDERATION_SPEC_DIR"
        "TOOLUP_APPROVE_API"
        "TOOLUP_APPROVE_BRANDKIT"
        "TOOLUP_APPROVE_COMPOSITION"
        "TOOLUP_AUTH_MODE"
        "TOOLUP_AWS_SECRETS_REGION"
        "TOOLUP_AZURE_KEY_VAULT_URL"
        "TOOLUP_AZURE_STORAGE_CONNECTION_STRING"
        "TOOLUP_BUNDLE_FIXTURE_DIR"
        "TOOLUP_EMIT_WIRE_FIXTURES"
        "TOOLUP_GCP_PROJECT_ID"
        "TOOLUP_GCS_BUCKET"
        "TOOLUP_HNSW_SCALE_CORPUS"
        "TOOLUP_OAUTH_REDIRECT_BASE"
        "TOOLUP_OIDC_AUDIENCE"
        "TOOLUP_REDIS_CONNECTION"
        "TOOLUP_REGEN_AUDIT_EVENT_REFERENCE"
        "TOOLUP_REGEN_CONFIG_REFERENCE"
        "TOOLUP_REGEN_ELMISH_PROOF_CORPUS"
        "TOOLUP_REGEN_LOCALIZATION_SKELETON"
        "TOOLUP_REGEN_PLATFORM_DECODERS"
        "TOOLUP_REGEN_PLATFORM_JSON_DECODERS"
        "TOOLUP_REPLICA_COUNT"
        "TOOLUP_SECRETS_MASTER_KEY"
        "TOOLUP_SECRET_STORE"
        "TOOLUP_TEST_AGE_CONNSTRING"
        "TOOLUP_TEST_POSTGRES"
        "TOOLUP_TIMESCALE_CONN"
        "TOOLUP_TRANSITION_PIN_OUT"
        "GOOGLE_APPLICATION_CREDENTIALS"
        "VAULT_ADDR"
        "VAULT_NAMESPACE"
        "VAULT_TOKEN"
    ]

let private cloudParityEnv =
    env [
        "TOOLUP_PARITY_AZURITE"
        "TOOLUP_PARITY_FAKEGCS"
        "TOOLUP_PARITY_FAKEGCS_BUCKET"
        "TOOLUP_PARITY_LOCALSTACK"
        "TOOLUP_PARITY_LOCALSTACK_BUCKET"
        "TOOLUP_PARITY_LOCALSTACK_REGION"
        "TOOLUP_PARITY_LOCALSTACK_SECRETS"
        "TOOLUP_PARITY_LOCALSTACK_SECRETS_ENDPOINT"
    ]

/// One variable set per connector backend; each remote arm reports Pending
/// without its set, so the pack is green on a credential-free checkout.
let private dataSourcesEnv =
    let backend prefix (suffixes: string list) =
        suffixes |> List.map (fun s -> "TOOLUP_" + prefix + "_" + s)

    env (
        backend "ATHENA" [
            "CREDENTIAL_JSON"
            "DATABASE"
            "ISOLATED_DATABASE"
            "OUTPUT_LOCATION"
            "REGION"
            "SAMPLE_SQL"
            "TABLE"
            "WORKGROUP"
        ]
        @ backend "BIGQUERY" [
            "CREDENTIAL_JSON"
            "DATASET_ID"
            "ISOLATED_DATASET"
            "LOCATION"
            "MAX_BYTES"
            "PROJECT_ID"
            "SAMPLE_SQL"
            "TABLE"
        ]
        @ backend "REDSHIFT" [
            "CLUSTER_IDENTIFIER"
            "CREDENTIAL_JSON"
            "DATABASE"
            "DB_USER"
            "ISOLATED_SCHEMA"
            "REGION"
            "SAMPLE_SQL"
            "SCHEMA"
            "SECRET_ARN"
            "TABLE"
            "WORKGROUP_NAME"
        ]
        @ backend "SNOWFLAKE" [
            "ACCOUNT"
            "AUTHENTICATOR"
            "CREDENTIAL"
            "DATABASE"
            "ISOLATED_SCHEMA"
            "PRIVATE_KEY_FILE"
            "ROLE"
            "SAMPLE_SQL"
            "SCHEMA"
            "TABLE"
            "USER"
            "WAREHOUSE"
        ]
        @ backend "SQL" [ "BACKEND"; "CONNECTION_STRING"; "PASSWORD"; "SAMPLE_SQL"; "SCHEMA"; "TABLE" ]
        @ backend "SYNAPSE" [
            "AUTH"
            "CREDENTIAL"
            "DATABASE"
            "ISOLATED_SCHEMA"
            "SAMPLE_SQL"
            "SCHEMA"
            "SERVER"
            "TABLE"
            "USER"
        ]
    )

/// The rows. Order is the order `VerifyAll` runs them: the packs, then the
/// legs, then the rows that run elsewhere. The `Budget` seeds are the
/// measurements on record — the Platform pack's 884.8 s and the Fable
/// recipe's ~1.5 min from the 2026-09-12 lane measurement, the Samples
/// leg's ~10 minutes and the Analyzers leg's ~6 s from their own phases —
/// and `dotnet run -- GateTimings` re-derives every one from the recorded
/// runs; a row with no measurement on record carries none rather than a
/// guess.
let rows (b: Bodies) : GateLeg<Lane> list = [
    pack
        "Platform"
        "src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj"
        ([ Input.Tree; Input.Git; Input.Docker ] @ platformEnv)
    |> budget (minutes 15.)
    pack "Forms" "src/ToolUp.Forms.Tests/ToolUp.Forms.Tests.fsproj" [ Input.Tree ]
    pack "Scheduling" "src/ToolUp.Scheduling.Tests/ToolUp.Scheduling.Tests.fsproj" [ Input.Tree ]
    // Each per-provider arm runs when its key is set and reports Pending
    // otherwise, so a fresh checkout is green without credentials.
    pack
        "AIProviders"
        "src/ToolUp.AIProviders.Tests/ToolUp.AIProviders.Tests.fsproj"
        ([ Input.Tree; Input.Network ] @ apiKeys @ env [ "AZURE_OPENAI_ENDPOINT" ])
    pack "Stripe" "src/ToolUp.Stripe.Tests/ToolUp.Stripe.Tests.fsproj" [ Input.Tree ]
    pack
        "Build"
        "src/ToolUp.Platform.Build.Tests/ToolUp.Platform.Build.Tests.fsproj"
        ([ Input.Tree; Input.Git ] @ env [ "TOOLUP_APPROVE_API" ])
    pack "RemotingAnalyzers" "src/ToolUp.Remoting.Analyzers.Tests/ToolUp.Remoting.Analyzers.Tests.fsproj" [ Input.Tree ]
    pack
        "Cli"
        "src/ToolUp.Cli.Tests/ToolUp.Cli.Tests.fsproj"
        ([ Input.Tree ]
         @ env [
             "TOOLUP_ADMIN_ENDPOINT"
             "TOOLUP_ADMIN_TOKEN"
             "TOOLUP_HELM_PATH"
             "TOOLUP_TEST_POSTGRES"
         ])
    pack "Voice" "src/ToolUp.Voice.Tests/ToolUp.Voice.Tests.fsproj" []
    pack "AICookbooks" "src/ToolUp.AICookbooks.Tests/ToolUp.AICookbooks.Tests.fsproj" [ Input.Tree ]
    pack "Algorithms" "src/ToolUp.Algorithms.Tests/ToolUp.Algorithms.Tests.fsproj" [ Input.Tree ]
    pack "AlgorithmProviders" "src/ToolUp.AlgorithmProviders.Tests/ToolUp.AlgorithmProviders.Tests.fsproj" []
    // Every emulator leg is env-gated; the `cloud-parity` job arms the
    // Azurite one and asserts a case floor of its own.
    pack
        "CloudParity"
        "src/ToolUp.Cloud.Parity.Tests/ToolUp.Cloud.Parity.Tests.fsproj"
        ([ Input.Tree; Input.Docker ] @ cloudParityEnv)
    |> also (Site.Ci "cloud-parity")
    pack "ArtefactSigning" "src/ToolUp.ArtefactSigning.Tests/ToolUp.ArtefactSigning.Tests.fsproj" [ Input.Tree ]
    pack "ReportingDocx" "src/Reporting/Docx.Tests/ToolUp.Reporting.Docx.Tests.fsproj" [ Input.Tree ]
    pack "ReportingXlsx" "src/Reporting/Xlsx.Tests/ToolUp.Reporting.Xlsx.Tests.fsproj" [ Input.Tree ]
    pack
        "DataSources"
        "src/ToolUp.DataSources.Tests/ToolUp.DataSources.Tests.fsproj"
        ([ Input.Tree; Input.Network ] @ dataSourcesEnv)
    pack "OpenXml" "src/ToolUp.OpenXml.Tests/ToolUp.OpenXml.Tests.fsproj" [ Input.Tree ]
    pack "OpenXmlSpreadsheet" "src/ToolUp.OpenXml.Spreadsheet.Tests/ToolUp.OpenXml.Spreadsheet.Tests.fsproj" [
        Input.Tree
    ]
    pack "AssetStore" "src/ToolUp.AssetStore.Tests/ToolUp.AssetStore.Tests.fsproj" []
    pack "SvgRasterizerSkia" "src/SvgRasterizers/Skia.Tests/ToolUp.OpenXml.SvgRasterizer.Skia.Tests.fsproj" [
        Input.Tree
    ]
    pack "Reporting" "src/ToolUp.Reporting.Tests/ToolUp.Reporting.Tests.fsproj" []
    pack "Offline" "src/ToolUp.Offline.Tests/ToolUp.Offline.Tests.fsproj" [ Input.Tree ]
    // Every out-of-process case starts a real child through `dotnet exec`.
    pack "CompanionsIsolation" "src/ToolUp.Companions.Isolation.Tests/ToolUp.Companions.Isolation.Tests.fsproj" [
        Input.Tree
    ]
    // The offline self-test pack only; the live replay and judge arms
    // report Pending unless a provider key is set.
    pack "AIEvaluation" "src/ToolUp.AI.Evaluation/ToolUp.AI.Evaluation.fsproj" ([ Input.Tree ] @ apiKeys)
    pack
        "TriageCalibration"
        "tools/ToolUp.TriageCalibration.Tests/ToolUp.TriageCalibration.Tests.fsproj"
        ([ Input.Tree ] @ env [ "TOOLUP_APPROVE_API" ])
    pack "Tabular" "src/ToolUp.Tabular.Tests/ToolUp.Tabular.Tests.fsproj" [ Input.Tree ]
    pack "Classification" "src/ToolUp.Platform.Classification.Tests/ToolUp.Platform.Classification.Tests.fsproj" [
        Input.Tree
    ]
    pack "DataSubject" "src/ToolUp.Platform.DataSubject.Tests/ToolUp.Platform.DataSubject.Tests.fsproj" [ Input.Tree ]
    pack
        "ReportingHtmlPdf"
        "src/Reporting/HtmlPdf.Tests/ToolUp.Reporting.HtmlPdf.Tests.fsproj"
        ([ Input.Tree; Input.Browser ] @ env [ "PLAYWRIGHT_BROWSERS_PATH" ])
    // Phase 1197 — a companion family's tests beside the family, building
    // against its closure and hosting the contract packs it conforms to.
    pack "Webhooks" "src/Webhooks/Tests/ToolUp.Webhooks.Tests.fsproj" []
    pack "JobSchedulers" "src/JobSchedulers/Tests/ToolUp.JobSchedulers.Tests.fsproj" [ Input.Tree ]
    pack
        "NotificationChannels"
        "src/NotificationChannels/Tests/ToolUp.NotificationChannels.Tests.fsproj"
        ([ Input.Tree ] @ env [ "TOOLUP_REDIS_CONNECTION" ])

    // ── The legs `VerifyAll` runs after the packs ──────────────────────
    // Every `*.Tests.fsproj` is a pack row or a named exclusion. Pure file
    // reading, so it runs in every lane, `pure` included.
    leg "GateReconcile" b.GateReconcile everyLane [ Input.Tree ]
    // The client-tier `node:test` harness; the `fable-tier` job runs the
    // same recipe on a Node-provisioned runner.
    leg "Fable" b.Fable readingLanes [ Input.Tree; Input.Node ]
    |> budget (minutes 1.5)
    |> floor' (Floor.AtLeast 100)
    |> also (Site.Ci "fable-tier")
    // Every sample builds; the Fable ones transpile in the full lane only.
    leg "Samples" b.Samples readingLanes [ Input.Tree; Input.Node ]
    |> budget (minutes 10.)
    leg "Analyzers" b.Analyzers readingLanes [ Input.Tree; Input.Git ]
    |> budget (seconds 6.)
    leg "ScopeLiterals" b.ScopeLiterals readingLanes [ Input.Tree ]
    leg "ServiceConfigFold" b.ServiceConfigFold readingLanes [ Input.Tree ]
    leg "FuaranCoResolution" b.FuaranCoResolution readingLanes [ Input.Tree ]
    // Restores the published Fuaran.UI from nuget.org and Fable-compiles
    // the fact tier beside it: the full lane only.
    leg "FuaranCoBuild" b.FuaranCoBuild (set [ Lane.Full ]) [ Input.Tree; Input.Node; Input.Network ]
    leg "MergeDrivers" b.MergeDrivers readingLanes [ Input.Git ]

    // ── Rows CI runs and `VerifyAll` does not ──────────────────────────
    // The wire layer on both hosts: the .NET pack and its Fable twin.
    elsewhere "WireTests" (LegKind.ExpectoPack "src/ToolUp.AI.Wire.Tests/ToolUp.AI.Wire.Tests.fsproj") [
        Site.Ci "fable-wire-smoke"
    ] []
    elsewhere "WireFable" (LegKind.Body b.WireFable) [ Site.Ci "fable-wire-smoke" ] [ Input.Tree; Input.Node ]
    |> floor' (Floor.AtLeast 1)
    // The connector mappers over one corpus, on both hosts.
    elsewhere "WireConformance" (LegKind.ExpectoPack "src/ToolUp.AI.Wire.Conformance/ToolUp.AI.Wire.Conformance.fsproj") [
        Site.Ci "ai-wire-conformance"
    ] []
    elsewhere "WireConformanceFable" (LegKind.Body b.WireConformanceFable) [ Site.Ci "ai-wire-conformance" ] [
        Input.Tree
        Input.Node
    ]
    |> floor' (Floor.AtLeast 1)
    // Every in-scope `fsharp` block compiles; the corpus may only grow.
    elsewhere "DocSnippets" (LegKind.Body b.DocSnippets) [ Site.Ci "doc-snippets" ] [ Input.Tree ]
    |> floor' (Floor.HighWater "docs-snippets/corpus-floor.txt")
    elsewhere "SourceCitations" (LegKind.Body b.SourceCitations) [ Site.Ci "source-citations" ] [ Input.Tree ]
    elsewhere "UploadResults" (LegKind.Body b.UploadResults) [ Site.Ci "source-citations" ] [ Input.Tree ]
    // The three template gates pack a closure from source and restore the
    // rest from nuget.org.
    elsewhere "Templates" (LegKind.Body b.Templates) [ Site.Ci "templates" ] [ Input.Tree; Input.Network ]
    elsewhere "PackagedModuleTemplate" (LegKind.Body b.PackagedModuleTemplate) [ Site.Ci "templates" ] [
        Input.Tree
        Input.Network
    ]
    elsewhere "DataPlatformTemplate" (LegKind.Body b.DataPlatformTemplate) [ Site.Ci "templates" ] [
        Input.Tree
        Input.Network
    ]
    // Eight real-browser scenarios, and the only CI transpile of
    // `ToolUp.Offline.Client`. The floor is exact: each scenario is
    // expensive, so losing one silently is what it must catch.
    elsewhere "BrowserSmoke" (LegKind.Body b.BrowserSmoke) [ Site.Ci "browser-smoke" ] [
        Input.Tree
        Input.Node
        Input.Browser
    ]
    |> floor' (Floor.Exactly 8)
    // The two measured gates: each script measures, then runs the SDK's
    // deciding target over the committed budget file.
    elsewhere "PerfBudget" (LegKind.Script "dev-scripts/perf-budget-gate.ps1") [ Site.Ci "perf-budget" ] [
        Input.Tree
        Input.Node
    ]
    elsewhere "CoreWebVitals" (LegKind.Script "dev-scripts/cwv-budget-gate.ps1") [] [
        Input.Tree
        Input.Node
        Input.Browser
    ]
    // The proof leg (`proofs.yml`): the pinned prover, the extraction diff
    // and the differential hosts, each host's case floor read from the
    // claims registry.
    elsewhere "Proofs" (LegKind.Script "proofs/check.ps1") [ Site.Ci "proofs" ] [ Input.Tree; Input.Network ]
    |> floor' (Floor.FromRegistry "proofs.json")

    // ── Rows the release workflow runs ─────────────────────────────────
    elsewhere "SemVerBump" (LegKind.Body b.SemVerBump) [ Site.Release ] [ Input.Git ]
    elsewhere "ReleaseChannel" (LegKind.Body b.ReleaseChannel) [ Site.Release ] [ Input.Git ]
    elsewhere "PublishedPackages" (LegKind.Body b.PublishedPackages) [ Site.Release ] [ Input.Network ]
]

/// The test projects `VerifyAll` deliberately does not run as
/// packs, each with the runner that does run it or the input it needs. The
/// `GateReconcile` leg is red for any `*.Tests.fsproj` (in the solution, or
/// on disk under `src/`, `tests/`, `tools/`) that is neither a pack row of `rows` nor
/// named here, and for an entry here that names nothing.
let exclusions: Gate.Exclusion list = [
    Gate.exclude
        "src/ToolUp.AI.Client.Tests/ToolUp.AI.Client.Tests.fsproj"
        "Fable-tier node:test harness, not an Expecto pack: run by the Fable leg of VerifyAll (VerifyFable) and the fable-tier CI job."
    Gate.exclude
        "src/ToolUp.AI.Wire.Fable.Tests/ToolUp.AI.Wire.Fable.Tests.fsproj"
        "Fable-host twin of the wire pack, run under node:test after a Fable compile: the checks.yml fable-wire-smoke job runs it."
    Gate.exclude
        "src/ToolUp.AI.Wire.Tests/ToolUp.AI.Wire.Tests.fsproj"
        "the .NET host half of the wire-layer smoke, run beside its Fable twin by the checks.yml fable-wire-smoke job."
    Gate.exclude
        "tests/BrowserSmoke/ToolUp.BrowserSmoke.Tests/ToolUp.BrowserSmoke.Tests.fsproj"
        "launches real browsers (Phase 761): run by the opt-in VerifyBrowserSmoke target and its own CI job, outside the solution."
    Gate.exclude
        "tests/CompanionFuzz/ToolUp.Companions.Fuzz.Tests.fsproj"
        "needs Verovio.NET's native libverovio off the local feed (not on nuget.org, so it cannot join the solution CI restores) and a host that enforces a per-child kernel memory cap; run by an operator per tests/CompanionFuzz/README.md."
]

/// The `checks.yml` jobs that are not rows, each with why: a lint with no
/// driver target behind it.
let ciOnlyJobs: (string * string) list = [
    "spdx-headers", "a shell lint over the Fable-packed sources' headers; no driver target"
    "fantomas", "the formatter's own `--check`, which `verify.ps1` also runs"
    "dco", "a commit-message lint; PR only, since direct-to-main is this repo's integration path"
]

/// What a job's "Gates?" column says when it is not simply `yes`.
let jobNotes: (string * string) list = [ "dco", "PR only"; "perf-budget", "runs; not yet a required check" ]

// ─── Reads over the rows ─────────────────────────────────────────────

/// The rows `VerifyAll` runs in the full lane, locally.
let localRows (rows: GateLeg<Lane> list) =
    rows
    |> List.filter (fun r -> Set.contains Site.Local r.Where && Set.contains Lane.Full r.Lanes)

/// Budget of the full lane: the sum over its rows that carry one.
let fullBudget (rows: GateLeg<Lane> list) : TimeSpan =
    localRows rows |> List.choose _.Budget |> List.fold (+) TimeSpan.Zero

/// The FAKE target that runs a `Body` row on its own: `Verify<Name>`.
let target (row: GateLeg<Lane>) = "Verify" + row.Name

/// The targets `VerifyGate` serialises: `VerifyAll`, and every `Body` row
/// that reads the tree for longer than a minute — the runs that thrash
/// when two worktrees start them at once.
let gatedTargets (rows: GateLeg<Lane> list) : Set<string> =
    rows
    |> List.filter (fun r ->
        List.contains Input.Tree r.Reads
        && (match r.Kind, r.Budget with
            | LegKind.Body _, Some b -> b > TimeSpan.FromMinutes 1.
            | _ -> false))
    |> List.map target
    |> Set.ofList
    |> Set.add "VerifyAll"

/// The job ids a row runs under.
let jobsOf (row: GateLeg<Lane>) =
    row.Where
    |> Set.toList
    |> List.choose (function
        | Site.Ci job -> Some job
        | Site.Local
        | Site.Release -> None)

/// The text a workflow job that runs `row` must carry: `VerifyAll` for the
/// rows `VerifyAll` runs, else the row's own target, `Gate --run <Name>`,
/// the script path or the pack's project path.
let runTokens (job: string) (row: GateLeg<Lane>) : string list = [
    if job = VerifyAllJob && Set.contains Site.Local row.Where then
        "VerifyAll"

    "Gate --run " + row.Name

    match row.Kind with
    | LegKind.Body _ -> target row
    | LegKind.Script path -> path
    | LegKind.ExpectoPack project -> project
]

let private jobLine = Regex(@"^  (?<id>[A-Za-z0-9_.-]+):\s*$")

/// The job ids a workflow text declares under `jobs:`, in order — not the
/// `on:` block's triggers, which sit at the same indentation.
let jobIds (workflowText: string) : string list =
    workflowText.Replace("\r\n", "\n").Split('\n')
    |> Array.skipWhile (fun line -> line.TrimEnd() <> "jobs:")
    |> Array.choose (fun line ->
        let m = jobLine.Match line
        if m.Success then Some m.Groups["id"].Value else None)
    |> List.ofArray

let private expectedPacks = Regex(@"EXPECTED_PACKS:\s*(\d+)")

/// The `EXPECTED_PACKS` value a `checks.yml` text declares, if one.
let readExpectedPacks (workflowText: string) : int option =
    let m = expectedPacks.Match workflowText
    if m.Success then Some(int m.Groups[1].Value) else None

/// The workflow text with `EXPECTED_PACKS` set to the manifest's floor.
let renderExpectedPacks (floor: int) (workflowText: string) : string =
    expectedPacks.Replace(workflowText, sprintf "EXPECTED_PACKS: %d" floor, 1)

// ─── The CI table in CLAUDE.md ───────────────────────────────────────

[<Literal>]
let CiTableBegin = "<!-- gate-manifest:ci-table:begin -->"

[<Literal>]
let CiTableEnd = "<!-- gate-manifest:ci-table:end -->"

let private inputName (input: Input) =
    match input with
    | Input.Tree -> "Tree"
    | Input.Git -> "Git"
    | Input.Node -> "Node"
    | Input.Browser -> "Browser"
    | Input.Docker -> "Docker"
    | Input.Network -> "Network"
    | Input.Env name -> "Env " + name

/// A row's inputs for a table cell: the kinds by name, the variables as a count.
let readsCell (reads: Input list) =
    let kinds =
        reads
        |> List.filter (function
            | Input.Env _ -> false
            | _ -> true)
        |> List.map inputName

    let envs =
        reads
        |> List.filter (function
            | Input.Env _ -> true
            | _ -> false)
        |> List.length

    match kinds, envs with
    | [], 0 -> "—"
    | ks, 0 -> String.concat ", " ks
    | ks, n -> String.concat ", " (ks @ [ sprintf "%d env var(s)" n ])

/// The table, one line per job in `jobs` order (the workflow's), rendered
/// from `Where`. Generated into `CLAUDE.md` between the markers by
/// `Gate --render` and held there by `GateManifestTests`.
let ciTable (jobs: string list) (rows: GateLeg<Lane> list) : string list =
    let note job =
        jobNotes
        |> List.tryFind (fst >> (=) job)
        |> Option.map snd
        |> Option.defaultValue "yes"

    [
        "| Job | Rows of the gate manifest it runs | Reads | Gates? |"
        "|---|---|---|---|"
        for job in jobs do
            let mine = rows |> List.filter (fun r -> Set.contains (Site.Ci job) r.Where)

            match mine, ciOnlyJobs |> List.tryFind (fst >> (=) job) with
            | [], Some(_, reason) -> sprintf "| `%s` | — (CI-only: %s) | — | %s |" job reason (note job)
            | [], None -> sprintf "| `%s` | — (NOT IN THE MANIFEST) | — | %s |" job (note job)
            | mine, _ ->
                let names =
                    if job = VerifyAllJob then
                        sprintf
                            "%d rows, every `Local` row of the full lane: %s"
                            mine.Length
                            (mine |> List.map _.Name |> String.concat ", ")
                    else
                        mine |> List.map _.Name |> String.concat ", "

                let reads = mine |> List.collect _.Reads |> List.distinct
                sprintf "| `%s` | %s | %s | %s |" job names (readsCell reads) (note job)
    ]

/// `text` with the region between the markers replaced by `lines`; `None`
/// when the markers are missing or out of order.
let spliceRegion (beginMarker: string) (endMarker: string) (lines: string list) (text: string) : string option =
    let b = text.IndexOf(beginMarker, StringComparison.Ordinal)
    let e = text.IndexOf(endMarker, StringComparison.Ordinal)

    if b < 0 || e < 0 || e < b then
        None
    else
        let head = text.Substring(0, b + beginMarker.Length)
        let tail = text.Substring e
        Some(head + "\n" + String.concat "\n" lines + "\n" + tail)

/// The lines currently between the markers, trimmed of the blank edges.
let regionLines (beginMarker: string) (endMarker: string) (text: string) : string list option =
    let b = text.IndexOf(beginMarker, StringComparison.Ordinal)
    let e = text.IndexOf(endMarker, StringComparison.Ordinal)

    if b < 0 || e < 0 || e < b then
        None
    else
        text.Substring(b + beginMarker.Length, e - b - beginMarker.Length).Replace("\r\n", "\n").Trim('\n').Split('\n')
        |> List.ofArray
        |> Some