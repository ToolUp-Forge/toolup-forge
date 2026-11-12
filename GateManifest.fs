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
    NavRoleFinding: unit -> Verdict
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
        NavRoleFinding = unboundBody
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

let private seed (b: TimeSpan option) (row: GateLeg<Lane>) = { row with Budget = b }
let private floor' (f: Floor) (row: GateLeg<Lane>) = { row with Floor = Some f }

let private also (site: Site) (row: GateLeg<Lane>) = {
    row with
        Where = Set.add site row.Where
}

let private env (names: string list) = names |> List.map Input.Env

let private apiKeys =
    env [ "ANTHROPIC_API_KEY"; "OPENAI_API_KEY"; "GEMINI_API_KEY" ]

/// The variables the residue pack's own sources read, each gating an arm or
/// a regeneration; each part's pack declares its own beside its row. `PATH`
/// is ambient toolchain, not an input, and is not here. Harness-wide
/// variables (`TOOLUP_TEST_LANE`, `TOOLUP_TEST_LISTS`, `TOOLUP_TEST_ARGS`)
/// belong to the driver, not to a row.
let private platformEnv =
    env [
        "TOOLUP_FEDERATION_SPEC_DIR"
        "TOOLUP_APPROVE"
        "TOOLUP_APPROVE_API"
        "TOOLUP_APPROVE_COMPOSITION"
        "TOOLUP_AUTH_MODE"
        "TOOLUP_BUNDLE_FIXTURE_DIR"
        "TOOLUP_EMIT_WIRE_FIXTURES"
        "TOOLUP_HNSW_SCALE_CORPUS"
        "TOOLUP_NEUTRALITY_REQUIRED"
        "TOOLUP_NEUTRALITY_TOKENS"
        "TOOLUP_REDIS_CONNECTION"
        "TOOLUP_REGEN_AUDIT_EVENT_REFERENCE"
        "TOOLUP_REGEN_CONFIG_REFERENCE"
        "TOOLUP_REPLICA_COUNT"
        "TOOLUP_SECRETS_MASTER_KEY"
        "TOOLUP_SECRET_STORE"
        "TOOLUP_TEST_POSTGRES"
        "TOOLUP_TRANSITION_PIN_OUT"
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
/// legs, then the rows that run elsewhere. A row's `Budget` here is its
/// SEED (`seed`): the figure it keeps when the committed timings record
/// (`gate-timings.json`) carries no measured median for it.
/// `withRecordedBudgets` replaces every seed the record measures, and
/// `budgetSource` says which one a row's budget is. A row with neither a
/// measurement nor a seed carries no budget rather than a guess.
let rows (b: Bodies) : GateLeg<Lane> list = [
    pack
        "Platform"
        "src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj"
        ([ Input.Tree; Input.Git; Input.Docker ] @ platformEnv)
    |> seed (minutes 15.)
    // The Platform pack partitioned by server part: each part's pack holds the
    // lists whose test files reach that part and no later one, and references
    // only the part's closure (docs/migrations/1194-platform-pack-partition.md).
    pack
        "Platform.Remoting"
        "src/ToolUp.Platform.Tests.Remoting/ToolUp.Platform.Tests.Remoting.fsproj"
        ([ Input.Tree; Input.Git; Input.Docker ]
         @ env [
             "TOOLUP_MODEL_EXECUTION_CORPUS"
             "TOOLUP_NEUTRALITY_REQUIRED"
             "TOOLUP_NEUTRALITY_TOKENS"
             "TOOLUP_APPROVE"
             "TOOLUP_APPROVE_BRANDKIT"
             "TOOLUP_REGEN_ELMISH_PROOF_CORPUS"
             "TOOLUP_REGEN_LOCALIZATION_SKELETON"
             "TOOLUP_REGEN_PLATFORM_DECODERS"
             "TOOLUP_REGEN_PLATFORM_JSON_DECODERS"
             "TOOLUP_TEST_AGE_CONNSTRING"
         ])
    |> seed (minutes 2.)
    pack "Platform.Infra" "src/ToolUp.Platform.Tests.Infra/ToolUp.Platform.Tests.Infra.fsproj" [ Input.Tree ]
    |> seed (seconds 20.)
    pack
        "Platform.Config"
        "src/ToolUp.Platform.Tests.Config/ToolUp.Platform.Tests.Config.fsproj"
        ([ Input.Tree ]
         @ env [
             "TOOLUP_AUTH_MODE"
             "TOOLUP_REPLICA_COUNT"
             "TOOLUP_SECRETS_MASTER_KEY"
             "TOOLUP_SECRET_STORE"
         ])
    |> seed (seconds 10.)
    pack
        "Platform.Scope"
        "src/ToolUp.Platform.Tests.Scope/ToolUp.Platform.Tests.Scope.fsproj"
        ([ Input.Tree ] @ env [ "TOOLUP_AUTH_MODE"; "TOOLUP_OIDC_AUDIENCE" ])
    |> seed (seconds 10.)
    pack
        "Platform.Jobs"
        "src/ToolUp.Platform.Tests.Jobs/ToolUp.Platform.Tests.Jobs.fsproj"
        ([ Input.Tree ] @ env [ "TOOLUP_OAUTH_REDIRECT_BASE"; "TOOLUP_REDIS_CONNECTION" ])
    |> seed (seconds 20.)
    pack "Platform.Audit" "src/ToolUp.Platform.Tests.Audit/ToolUp.Platform.Tests.Audit.fsproj" [ Input.Tree ]
    |> seed (seconds 10.)
    pack
        "Platform.PublicApi"
        "src/ToolUp.Platform.Tests.PublicApi/ToolUp.Platform.Tests.PublicApi.fsproj"
        ([ Input.Tree ] @ env [ "TOOLUP_APPROVE"; "TOOLUP_APPROVE_API" ])
    |> seed (minutes 1.5)
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
        ([ Input.Tree; Input.Git ] @ env [ "TOOLUP_APPROVE"; "TOOLUP_APPROVE_API" ])
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
    pack "OpenXml" "src/ToolUp.OpenXml.Tests/ToolUp.OpenXml.Tests.fsproj" ([ Input.Tree ] @ env [ "TOOLUP_APPROVE" ])
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
        ([ Input.Tree ] @ env [ "TOOLUP_APPROVE"; "TOOLUP_APPROVE_API" ])
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
    pack "Webhooks" "src/Webhooks/ToolUp.Webhooks.Tests/ToolUp.Webhooks.Tests.fsproj" []
    pack "JobSchedulers" "src/JobSchedulers/ToolUp.JobSchedulers.Tests/ToolUp.JobSchedulers.Tests.fsproj" [ Input.Tree ]
    pack
        "NotificationChannels"
        "src/NotificationChannels/ToolUp.NotificationChannels.Tests/ToolUp.NotificationChannels.Tests.fsproj"
        ([ Input.Tree ] @ env [ "TOOLUP_REDIS_CONNECTION" ])
    pack "AuditSinks" "src/AuditSinks/ToolUp.AuditSinks.Tests/ToolUp.AuditSinks.Tests.fsproj" [ Input.Tree ]
    pack
        "Storage"
        "src/Storage/ToolUp.Storage.Tests/ToolUp.Storage.Tests.fsproj"
        ([ Input.Tree; Input.Network ]
         @ env [
             "TOOLUP_AWS_S3_BUCKET"
             "TOOLUP_AZURE_STORAGE_CONNECTION_STRING"
             "TOOLUP_GCS_BUCKET"
             "GOOGLE_APPLICATION_CREDENTIALS"
         ])
    pack
        "Secrets"
        "src/Secrets/ToolUp.Secrets.Tests/ToolUp.Secrets.Tests.fsproj"
        ([ Input.Tree; Input.Network ]
         @ env [
             "TOOLUP_AWS_SECRETS_REGION"
             "TOOLUP_AZURE_KEY_VAULT_URL"
             "TOOLUP_GCP_PROJECT_ID"
             "GOOGLE_APPLICATION_CREDENTIALS"
             "VAULT_ADDR"
             "VAULT_NAMESPACE"
             "VAULT_TOKEN"
         ])
    pack
        "EmbeddingProviders"
        "src/EmbeddingProviders/ToolUp.EmbeddingProviders.Tests/ToolUp.EmbeddingProviders.Tests.fsproj"
        [ Input.Tree ]
    pack
        "EntityStores"
        "src/EntityStores/ToolUp.EntityStores.Tests/ToolUp.EntityStores.Tests.fsproj"
        (env [ "TOOLUP_TEST_POSTGRES" ])
    pack
        "TimeSeriesStores"
        "src/TimeSeriesStores/ToolUp.TimeSeriesStores.Tests/ToolUp.TimeSeriesStores.Tests.fsproj"
        (env [ "TOOLUP_TIMESCALE_CONN" ])
    pack
        "FeatureFlagProviders"
        "src/FeatureFlagProviders/ToolUp.FeatureFlagProviders.Tests/ToolUp.FeatureFlagProviders.Tests.fsproj"
        [ Input.Tree ]
    pack "Media" "src/Media/ToolUp.Media.Tests/ToolUp.Media.Tests.fsproj" [ Input.Tree ]

    // ── The legs `VerifyAll` runs after the packs ──────────────────────
    // Every `*.Tests.fsproj` is a pack row or a named exclusion. Pure file
    // reading, so it runs in every lane, `pure` included.
    leg "GateReconcile" b.GateReconcile everyLane [ Input.Tree ]
    // The client-tier `node:test` harness; the `fable-tier` job runs the
    // same recipe on a Node-provisioned runner.
    leg "Fable" b.Fable readingLanes [ Input.Tree; Input.Node ]
    |> seed (minutes 1.5)
    |> floor' (Floor.AtLeast 100)
    |> also (Site.Ci "fable-tier")
    // Every sample builds; the Fable ones transpile in the full lane only.
    leg "Samples" b.Samples readingLanes [ Input.Tree; Input.Node ]
    |> seed (minutes 10.)
    leg "Analyzers" b.Analyzers readingLanes [ Input.Tree; Input.Git ]
    |> seed (seconds 6.)
    leg "ScopeLiterals" b.ScopeLiterals readingLanes [ Input.Tree ]
    leg "ServiceConfigFold" b.ServiceConfigFold readingLanes [ Input.Tree ]
    // Prints the modules gated by the deprecated group-name fallback, or
    // "none"; the fallback and this row go together at 1.0.
    leg "NavRoleFinding" b.NavRoleFinding readingLanes [ Input.Tree ]
    |> seed (seconds 2.)
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
/// `GateReconcile` leg is red for any test project (in the solution, or
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
    Gate.exclude
        "src/ToolUp.Platform.Tests.Support/ToolUp.Platform.Tests.Support.fsproj"
        "a library, not a pack: the harness every pack of the Platform family references (the lane filter, the registration guard, the shared entry point). It holds no cases."
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

// ─── What the gate reads ─────────────────────────────────────────────
//
// `gate-reads.json` at the repository root is the record the landing
// tooling reads to decide whether a commit on `main` owes a re-gate: the
// .NET default (sources, tests, proofs, build and project files, the
// formatter's configuration, the gate script) plus the record's `reads`
// is the gate's input set, and everything outside it is gate-inert. It is
// a RENDER of the declarations below: `Gate --render` writes it, and both
// `Gate --check` and the `GateReconcile` row are red when the file on disk
// is not the render. It is CHECKED, not trusted: `GateReadsCensusTests`
// fails by name for a file the suite's sources or project files name that
// is in neither `reads` nor `notRead`.
//
// A row's `Reads` says which KIND of input it depends on (`Input.Tree`);
// these lists say which non-code files of the tree. They describe the gate
// `verify.ps1` runs (its own steps and the rows `VerifyAll` runs in the
// full lane), never a CI-only row, which no landing re-runs.

/// The record's file name, at the repository root.
let GateReadsFile = "gate-reads.json"

/// The `kind` the record carries.
let GateReadsKind = "gateReads"

/// Does `spec` cover the repo-relative `path`? The record's grammar: a spec
/// with no `*` names a file or a directory (and everything beneath it); a
/// spec with wildcards is a glob in which `**` crosses `/` and `*` does not.
/// Case-sensitive, because folding case only ever widens the inert set.
let specCovers (spec: string) (path: string) : bool =
    let s = spec.Trim().Replace('\\', '/').Trim('/')
    let p = path.Trim().Replace('\\', '/').TrimStart('/')

    if s = "" || p = "" then
        false
    elif not (s.Contains "*") then
        p = s || p.StartsWith(s + "/", StringComparison.Ordinal)
    else
        let pattern =
            Regex.Escape(s).Replace(@"\*\*", "\u0001").Replace(@"\*", "[^/]*").Replace("\u0001", ".*")

        Regex.IsMatch(p, "^" + pattern + "$")

/// What `verify.ps1`'s own steps read beyond the .NET default, before any
/// row runs: the tool manifest `dotnet tool restore` resolves (at the root,
/// not under `.config/`), the files `Directory.Build.props` packs beside
/// every project, and the template files `ToolUp.Cli` embeds.
let stepReads: (string * string list) list = [
    "tool restore", [ "dotnet-tools.json" ]
    "build",
    [
        "README.md"
        "icon.png"
        "templates/platformsdk-docker/**"
        "templates/platformsdk-k8s/**"
    ]
]

/// The non-code files each row's sources open from the real repository,
/// beyond the .NET default. Keyed by row name; a key names a row
/// `VerifyAll` runs in the full lane, and that row reads the tree.
let rowReads: (string * string list) list = [
    // The fitness sweeps enumerate the checkout through
    // `git ls-files --exclude-standard`, which reads every ignore file.
    "Platform",
    [
        ".gitignore"
        "**/.gitignore"
        "DRAFT-NOTES.md"
        "api-baselines/**"
        "composition-baselines/**"
        "dev-scripts/merge-baselines.ps1"
        "docs/**"
        "projections.json"
        "samples/**"
        "spec/**"
    ]
    // The publication-boundary sweep reads a declared surface, not the
    // whole tracked set: `docs/**`, `samples/**`, the root README,
    // CONTRIBUTING and SECURITY, and the packable projects' sources and
    // READMEs under `src/` (a default input). So it records those, not `**`.
    "Platform.Remoting",
    [
        "CONTRIBUTING.md"
        "README.md"
        "SECURITY.md"
        "docs/**"
        "samples/**"
        "spec/**"
    ]
    "Platform.PublicApi", [ "api-baselines/**"; "docs/platform/deprecation-policy.md" ]
    "Build",
    [
        ".github/workflows/checks.yml"
        ".github/workflows/publish-nuget.yml"
        "CHANGELOG.md"
        "CLAUDE.md"
        "api-baselines/**"
        "build-census.json"
        "dev-scripts/cwv-budget-gate.ps1"
        "dev-scripts/perf-budget-gate.ps1"
        "docs/migrations/**"
        "docs/platform/open-core-boundary.md"
        "fuaran-ui-floors.json"
        "perf-budgets.json"
        "proofs.json"
        "release-channel.json"
        "samples/PublicSite/cwv-budget.json"
        "v1-readiness.json"
        "version-groups.json"
    ]
    "Samples", [ "samples/**" ]
]

/// Paths the census flags (a source names them) that the gate does not
/// read, each with why. Read by the census only: a `notRead` entry never
/// widens what the landing tooling treats as inert.
let notRead: (string * string) list = [
    "**/README.md",
    "file-name literal: AffectedLaneTests, TemplateGateTests and the driver's template gates name README.md for fixtures; the READMEs the gate opens are in reads (the root README, docs/**, samples/**) or a default input (src/**)"
    "THIRD_PARTY_NOTICES.md", "written, not read: the ThirdPartyNotices target emits it and no leg opens it"
    "gate-timings.json",
    "the gate's measured cost, written by GateTimings --record: GateReconcile reads it only to hold it against the manifest, and --record refuses to write a record that check fails, so committing a fold never flips a verdict; the stale-holder bound reads its budgets, which decide how long a run waits, never whether it passes"
    "docs-snippets/corpus-floor.txt",
    "read by the DocSnippets row, which the doc-snippets CI job runs and VerifyAll does not"
    "docs-snippets/known-drift.txt",
    "read by the DocSnippets row, which the doc-snippets CI job runs and VerifyAll does not"
    "templates/*/global.json",
    "named by the template gates (CI-only rows) and by AffectedLaneTests' fixture literals; no leg VerifyAll runs opens a template's own"
    "templates/*/nuget.config",
    "named by the template gates (CI-only rows) and by fixture literals in AffectedLaneTests and PublishedSmokeTests; no leg VerifyAll runs opens a template's own"
    "templates/*/.config/dotnet-tools.json",
    "named by the template gates (CI-only rows); the gate restores the root tool manifest only"
    "templates/*/compose.yml", "named by tools/ToolUp.PortGuard, which is outside the solution and which no leg runs"
    "templates/**/vite.config.mts",
    "named by tools/ToolUp.PortGuard, which is outside the solution and which no leg runs"
    "templates/**/launchSettings.json",
    "named by tools/ToolUp.PortGuard, which is outside the solution and which no leg runs"
]

/// The record's `reads`: the union of the step and row declarations, each
/// entry dropped when another glob in the set already covers it, sorted
/// ordinally.
let gateReads () : string list =
    let all = (stepReads @ rowReads) |> List.collect snd |> List.distinct

    all
    |> List.filter (fun p -> not (all |> List.exists (fun s -> s <> p && s.Contains "*" && specCovers s p)))
    |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

let private jsonString (s: string) =
    "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

/// Why the record exists, as its `_` member says it.
let private gateReadsNote =
    "GENERATED by `dotnet run --project Build.fsproj -- Gate --render` from GateManifest.fs (stepReads, rowReads, notRead); do not edit by hand. The non-code files the gate verify.ps1 runs reads beyond the .NET default (sources, tests, proofs, build and project files, the formatter configuration, the gate script); a commit touching nothing in the union is gate-inert. Checked by GateReadsCensusTests. CI-only rows are not the gate a landing re-runs, so their reads are not here. No row sweeps the whole tracked set: the publication-boundary sweep reads docs/**, samples/** and the root README, CONTRIBUTING and SECURITY, so it records those rather than **."

/// The record's text, exactly as `Gate --render` writes it.
let renderGateReads () : string =
    let reads = gateReads () |> List.map (fun p -> "    " + jsonString p)

    let notReadLines =
        notRead
        |> List.map (fun (p, why) -> sprintf "    { \"path\": %s, \"reason\": %s }" (jsonString p) (jsonString why))

    String.concat "\n" [
        "{"
        sprintf "  \"kind\": %s," (jsonString GateReadsKind)
        sprintf "  \"_\": %s," (jsonString gateReadsNote)
        "  \"reads\": ["
        String.concat ",\n" reads
        "  ],"
        "  \"notRead\": ["
        String.concat ",\n" notReadLines
        "  ]"
        "}"
        ""
    ]

/// Why the declarations or the committed record disagree with the rows: a
/// key naming no row, a key naming a row the gate does not run, a row
/// declaring file reads without reading the tree, and a record that is not
/// the render. Empty when they agree.
let gateReadsFindings (rows: GateLeg<Lane> list) (recordText: string option) : string list = [
    let local = localRows rows |> List.map (fun r -> r.Name, r) |> Map.ofList

    for name, _ in rowReads do
        match rows |> List.exists (fun r -> r.Name = name), Map.tryFind name local with
        | false, _ -> sprintf "rowReads names `%s`, which is no row of the manifest" name
        | true, None ->
            sprintf
                "rowReads names `%s`, which VerifyAll does not run in the full lane; the record describes the gate a landing re-runs"
                name
        | true, Some row when not (List.contains Input.Tree row.Reads) ->
            sprintf "row `%s` declares file reads in rowReads but its `Reads` does not name the tree" name
        | _ -> ()

    match recordText with
    | None -> sprintf "%s is absent; `Gate --render` writes it" GateReadsFile
    | Some text when text.Replace("\r\n", "\n") <> renderGateReads () ->
        sprintf "%s is not the render of GateManifest.fs (stepReads, rowReads, notRead)" GateReadsFile
    | Some _ -> ()
]

// ─── The timings record: gate-timings.json ───────────────────────────
//
// `gate-timings.json` at the repository root is the committed fold of the
// gate's admission journal (`VerifyGate.Runs`): the window it covers, the
// machines whose runs it folds, per lane the median and p90 wall clock of
// the green `VerifyAll` runs, and per row the median seconds over the
// green full-lane runs. `dotnet run -- GateTimings --record` writes it;
// nothing else does. The planning tooling that reads a repository's
// `gate-timings.json` prices the gate from it, and `VerifyGate`'s
// stale-holder bound and gated set read the row budgets it measures
// (`withRecordedBudgets`), so the bound and the planner read one figure.
//
// It is gate-inert (listed in `notRead`): `GateReconcile` reads it only
// to hold it against the manifest, and `--record` refuses to write a
// record that check would fail, so committing a fold never flips a
// verdict.

/// The record's file name, at the repository root.
let GateTimingsFile = "gate-timings.json"

/// The `kind` the record carries.
let GateTimingsKind = "gateTimings"

/// How old the record's window may grow before `GateReconcile` warns.
let TimingsStaleAfter = TimeSpan.FromDays 14.

/// A lane's figures: seconds, over the green `VerifyAll` runs in that lane.
type LaneTiming = { Median: float; P90: float; Runs: int }

/// A row's figures: seconds, over the green full-lane runs that ran it.
type RowTiming = { Median: float; Runs: int }

/// The record, as `GateTimings --record` writes it.
type Timings = {
    /// The window's start: `Until` less the fold's width.
    Since: DateTimeOffset
    /// The newest run the journal holds.
    Until: DateTimeOffset
    /// Every run in the window, green or not, of every gated target.
    Runs: int
    /// The machines whose runs the window holds, as hashed labels, sorted.
    Machines: string list
    /// Sorted by lane name.
    Lanes: (string * LaneTiming) list
    /// Sorted by row name.
    Rows: (string * RowTiming) list
    /// The commit the record was folded at. Not part of what a re-fold
    /// compares: a fold that differs only here leaves the file alone.
    FoldedAt: string option
}

let private invariant = Globalization.CultureInfo.InvariantCulture

/// Seconds as the record writes them: one decimal, invariant.
let private secondsText (s: float) = s.ToString("0.0", invariant)

let private stampText (t: DateTimeOffset) =
    t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", invariant)

let private ordinal (a: string, _) (b: string, _) = String.CompareOrdinal(a, b)

/// Why the record exists, as its `_` member says it.
let private gateTimingsNote =
    "GENERATED by `dotnet run --project Build.fsproj -- GateTimings --record` from the gate's admission journal (<common git dir>/fuaran-gate/verify-runs.jsonl); do not edit by hand. Seconds. lanes: median and p90 wall clock of the green VerifyAll runs per lane. rows: median of each row over the green full-lane runs, which is the Budget the gate's stale-holder bound sums. machines: hashed labels (the first 8 hex digits of the SHA-256 of the upper-cased host name), never host names. The window is the given width ending at the newest recorded run. A re-fold that changes nothing but foldedAt leaves the file as it is. Gate-inert: listed in gate-reads.json's notRead."

/// The record's text, exactly as `GateTimings --record` writes it: keys in
/// a fixed order, lanes and rows sorted ordinally, seconds to one decimal.
let renderTimings (t: Timings) : string =
    let block (name: string) (entries: string list) (last: bool) = [
        match entries with
        | [] -> sprintf "  %s: {}%s" (jsonString name) (if last then "" else ",")
        | entries ->
            sprintf "  %s: {" (jsonString name)
            String.concat ",\n" entries
            if last then "  }" else "  },"
    ]

    let lanes =
        t.Lanes
        |> List.sortWith ordinal
        |> List.map (fun (name, l) ->
            sprintf
                "    %s: { \"median\": %s, \"p90\": %s, \"runs\": %d }"
                (jsonString name)
                (secondsText l.Median)
                (secondsText l.P90)
                l.Runs)

    let rows =
        t.Rows
        |> List.sortWith ordinal
        |> List.map (fun (name, r) ->
            sprintf "    %s: { \"median\": %s, \"runs\": %d }" (jsonString name) (secondsText r.Median) r.Runs)

    String.concat "\n" [
        "{"
        sprintf "  \"kind\": %s," (jsonString GateTimingsKind)
        sprintf "  \"_\": %s," (jsonString gateTimingsNote)
        sprintf "  \"since\": %s," (jsonString (stampText t.Since))
        sprintf "  \"until\": %s," (jsonString (stampText t.Until))
        sprintf "  \"runs\": %d," t.Runs
        sprintf "  \"machines\": [%s]," (t.Machines |> List.sort |> List.map jsonString |> String.concat ", ")
        match t.FoldedAt with
        | Some commit -> sprintf "  \"foldedAt\": %s," (jsonString commit)
        | None -> ()
        yield! block "lanes" lanes false
        yield! block "rows" rows true
        "}"
        ""
    ]

/// Read a record. `Error` names why the text is not one.
let parseTimings (text: string) : Result<Timings, string> =
    try
        use doc = Text.Json.JsonDocument.Parse text
        let r = doc.RootElement

        let prop (name: string) =
            match r.TryGetProperty name with
            | true, v -> v
            | _ -> failwithf "it has no `%s`" name

        let stamp (name: string) =
            DateTimeOffset.Parse((prop name).GetString(), invariant, Globalization.DateTimeStyles.AssumeUniversal)

        let entries (name: string) = [
            for p in (prop name).EnumerateObject() do
                p.Name, p.Value
        ]

        match (prop "kind").GetString() with
        | kind when kind <> GateTimingsKind -> Error(sprintf "its kind is `%s`, not `%s`" kind GateTimingsKind)
        | _ ->
            Ok {
                Since = stamp "since"
                Until = stamp "until"
                Runs = (prop "runs").GetInt32()
                Machines = [ for m in (prop "machines").EnumerateArray() -> m.GetString() ]
                Lanes = [
                    for name, v in entries "lanes" ->
                        name,
                        {
                            LaneTiming.Median = v.GetProperty("median").GetDouble()
                            P90 = v.GetProperty("p90").GetDouble()
                            Runs = v.GetProperty("runs").GetInt32()
                        }
                ]
                Rows = [
                    for name, v in entries "rows" ->
                        name,
                        {
                            RowTiming.Median = v.GetProperty("median").GetDouble()
                            Runs = v.GetProperty("runs").GetInt32()
                        }
                ]
                FoldedAt =
                    match r.TryGetProperty "foldedAt" with
                    | true, v -> Some(v.GetString())
                    | _ -> None
            }
    with e ->
        Error e.Message

/// Read the record at the repository root `root`; `None` when it is absent
/// or does not parse (`timingsFindings` names which).
let readTimings (root: string) : Timings option =
    let path = IO.Path.Combine(root, GateTimingsFile)

    if IO.File.Exists path then
        parseTimings (IO.File.ReadAllText path) |> Result.toOption
    else
        None

/// Where a row's budget comes from.
[<RequireQualifiedAccess>]
type BudgetSource =
    /// The record's median for the row, over `runs` runs.
    | Measured of median: TimeSpan * runs: int
    /// The row's declared seed: the record carries no median for it.
    | Seed of TimeSpan
    /// Neither.
    | Unbudgeted

/// Where `row`'s budget comes from, given the record.
let budgetSource (timings: Timings option) (row: GateLeg<Lane>) : BudgetSource =
    match
        timings
        |> Option.bind (fun t -> t.Rows |> List.tryFind (fun (name, _) -> name = row.Name))
    with
    | Some(_, measured) -> BudgetSource.Measured(TimeSpan.FromSeconds measured.Median, measured.Runs)
    | None ->
        match row.Budget with
        | Some s -> BudgetSource.Seed s
        | None -> BudgetSource.Unbudgeted

/// The rows with every budget the record measures read from it; a row the
/// record does not measure keeps its seed.
let withRecordedBudgets (timings: Timings option) (rows: GateLeg<Lane> list) : GateLeg<Lane> list =
    rows
    |> List.map (fun row ->
        match budgetSource timings row with
        | BudgetSource.Measured(median, _) -> { row with Budget = Some median }
        | BudgetSource.Seed _
        | BudgetSource.Unbudgeted -> row)

/// The record held against the rows, at `now`: the findings that make
/// `GateReconcile` red (absent, unreadable, a row the record measures that
/// `VerifyAll` does not run in the full lane, a row it runs that the record
/// does not measure and that declares no seed), and the warnings that do
/// not (the window ended more than `TimingsStaleAfter` ago).
let timingsFindings
    (rows: GateLeg<Lane> list)
    (now: DateTimeOffset)
    (recordText: string option)
    : string list * string list =
    match recordText with
    | None -> [ sprintf "%s is absent; `GateTimings --record` writes it" GateTimingsFile ], []
    | Some text ->
        match parseTimings text with
        | Error why -> [ sprintf "%s is not a timings record: %s" GateTimingsFile why ], []
        | Ok t ->
            let local = localRows rows
            let measured = t.Rows |> List.map fst |> Set.ofList

            let errors = [
                for name in measured do
                    if not (local |> List.exists (fun r -> r.Name = name)) then
                        sprintf
                            "%s measures `%s`, which is no row VerifyAll runs in the full lane; re-run `GateTimings --record`"
                            GateTimingsFile
                            name

                for row in local do
                    if not (Set.contains row.Name measured) && row.Budget.IsNone then
                        sprintf
                            "row `%s` has no median in %s and declares no seed; re-run `GateTimings --record` over a window it ran in, or give it a seed"
                            row.Name
                            GateTimingsFile
            ]

            let warnings = [
                if now - t.Until > TimingsStaleAfter then
                    sprintf
                        "%s's window ended %s, more than %d days ago; run `GateTimings --record` and commit it"
                        GateTimingsFile
                        (stampText t.Until)
                        (int TimingsStaleAfter.TotalDays)
            ]

            errors, warnings