module ToolUpApp.Build

open System.IO
open Fake.Core
open Fake.IO
open Fake.IO.Globbing.Operators
open ToolUp.Platform
open ToolUp.Platform.Build

let config = {
    BuildConfig.defaults with
        Port = 5000
        // The 4 in-tree Expecto runners — `dotnet run -- VerifyAll`
        // iterates these sequentially. AIProviders.Tests is env-gated;
        // arms with no API-key env var report Pending (not Failed), so
        // a fresh checkout is green without per-provider credentials.
        TestPacks = [
            TestPack.create "Platform" "src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj"
            TestPack.create "Forms" "src/ToolUp.Forms.Tests/ToolUp.Forms.Tests.fsproj"
            TestPack.create "Scheduling" "src/ToolUp.Scheduling.Tests/ToolUp.Scheduling.Tests.fsproj"
            TestPack.create "AIProviders" "src/ToolUp.AIProviders.Tests/ToolUp.AIProviders.Tests.fsproj"
            TestPack.create "Stripe" "src/ToolUp.Stripe.Tests/ToolUp.Stripe.Tests.fsproj"
            // Phase 182 — release SBOM gate + CycloneDX emission contract.
            TestPack.create "Build" "src/ToolUp.Platform.Build.Tests/ToolUp.Platform.Build.Tests.fsproj"
            // Phase 195 — compile-time auth/audit analyzer AST-path coverage
            // (offline FCS parse fixtures → Analyzer.analyzeParseTree). The
            // recognition-vs-runtime parity lives in the Platform pack.
            TestPack.create
                "RemotingAnalyzers"
                "src/ToolUp.Remoting.Analyzers.Tests/ToolUp.Remoting.Analyzers.Tests.fsproj"
            // Phase 167 — `toolup` CLI substrate (dispatch + docker-emit
            // token substitution). Pure-BCL host; no env gating.
            TestPack.create "Cli" "src/ToolUp.Cli.Tests/ToolUp.Cli.Tests.fsproj"
            // Phase 518 — ToolUp.Voice: Transcript model + error taxonomy
            // + the Whisper / AzureSpeech pure Wire surfaces. No env gating.
            TestPack.create "Voice" "src/ToolUp.Voice.Tests/ToolUp.Voice.Tests.fsproj"
            // Phase 12e — AICookbooks licensing-boundary: the Community AG
            // Chart prompt builder leaks zero Enterprise feature names + the
            // ~600-token bound + graceful no-op on a missing cookbook.
            TestPack.create "AICookbooks" "src/ToolUp.AICookbooks.Tests/ToolUp.AICookbooks.Tests.fsproj"
            // Phase 11.E.2 — ToolUp.Algorithms: the six-portability-rule
            // contract pack, the registry's duplicate-registration
            // failure path, the dispatcher's typed error surface, and
            // the AI tool edge. No env gating; no vendor dependency.
            TestPack.create "Algorithms" "src/ToolUp.Algorithms.Tests/ToolUp.Algorithms.Tests.fsproj"
            // Phase 11.E.3 — the Math.NET algorithm provider: the shared
            // contract packs bound against a real vendor implementation,
            // plus known-answer numerical fixtures for the hand-written
            // estimators (R-7 quantiles, OLS + categorical encoding,
            // each distribution's parameterisation, centred-vs-trailing
            // alignment). No env gating — the numerics run in-process.
            TestPack.create
                "AlgorithmProviders"
                "src/ToolUp.AlgorithmProviders.Tests/ToolUp.AlgorithmProviders.Tests.fsproj"
            // Phase 193 — emulator-backed multi-cloud parity matrix. Every
            // emulator leg is env-gated (clean skip on a fresh checkout); the
            // divergence fixture + seam ratchets always run.
            TestPack.create "CloudParity" "src/ToolUp.Cloud.Parity.Tests/ToolUp.Cloud.Parity.Tests.fsproj"
            // The artefact-signing pack: the byte-level signer/verifier
            // contract, the module-binding surfaces, and the application
            // signing seam's provider-conformance matrix + its probe
            // (deliberately broken providers the pack must reject). No env
            // gating; the crypto runs in-process.
            TestPack.create "ArtefactSigning" "src/ToolUp.ArtefactSigning.Tests/ToolUp.ArtefactSigning.Tests.fsproj"
            // Phase 23 — the Reporting DOCX / XLSX renderer sub-companions:
            // the shared IReportRendererContract pack bound through each
            // container format, plus format-specific fixtures (run
            // coalescing, native-table promotion, cell-address writes,
            // style preservation, formula recalculation). No env gating.
            TestPack.create "ReportingDocx" "src/Reporting/Docx.Tests/ToolUp.Reporting.Docx.Tests.fsproj"
            TestPack.create "ReportingXlsx" "src/Reporting/Xlsx.Tests/ToolUp.Reporting.Xlsx.Tests.fsproj"
            // Phase 10c — the IDataSource connector family under
            // src/DataSources/. The pure surfaces (ConnectionScope
            // parsing, the ISecretStore credential thunk, RFC 4180 CSV
            // emission, per-backend catalogue SQL, identifier refusal,
            // native-type classification) always run; each connector's
            // remote arm is env-gated and reports Pending on a
            // credential-free checkout.
            TestPack.create "DataSources" "src/ToolUp.DataSources.Tests/ToolUp.DataSources.Tests.fsproj"
            // Phase 206 — the ToolUp.OpenXml structural / revision
            // layer: the import + emit + tracked-changes surfaces, and
            // the round-trip fidelity corpus that pins six `.docx`
            // fixtures against committed DocModel / OpenXml-element /
            // residue goldens. The pack existed before this phase but
            // was never reached by `VerifyAll`, so a fidelity
            // regression could land green. No env gating; the fixtures
            // are built in-process.
            TestPack.create "OpenXml" "src/ToolUp.OpenXml.Tests/ToolUp.OpenXml.Tests.fsproj"
            // Phase 574 — the SpreadsheetML write side: the workbook
            // model's sheet-name / range validation, the emitted parts
            // (shared strings, styles carrying the number formats,
            // cols, mergeCells), the byte-identical-emit property, and
            // the reopen proof run through the ToolUp.Tabular reader.
            // No env gating; every fixture is built in-process.
            TestPack.create
                "OpenXmlSpreadsheet"
                "src/ToolUp.OpenXml.Spreadsheet.Tests/ToolUp.OpenXml.Spreadsheet.Tests.fsproj"
            // Phase 127 / 207 — the AssetStore derivative pipeline: the
            // sync + async job-backed paths, and the opt-in dead-letter
            // / retry-observability surface with its default-off twin.
            // Hermetic (in-memory blob storage, manual-pump scheduler);
            // no env gating, no SkiaSharp native needed — the pack's
            // renderers are doubles.
            TestPack.create "AssetStore" "src/ToolUp.AssetStore.Tests/ToolUp.AssetStore.Tests.fsproj"
            // Phase 576.C — the Skia ISvgRasterizer companion: real
            // rasterisation (requested width honoured, height from the
            // document's own aspect), the failure surface as values
            // rather than exceptions, and the end-to-end proof that a
            // figure composed with it carries both parts. Its own pack
            // rather than a leg of the OpenXml one, deliberately: that
            // pack's strip-imports evidence is that no rendering engine
            // is in its process at all, which a shared pack would
            // destroy. Needs a SkiaSharp native for the running RID —
            // the fsproj carries the Linux one, as ToolUp.Platform.Tests
            // does; no env gating.
            TestPack.create
                "SvgRasterizerSkia"
                "src/SvgRasterizers/Skia.Tests/ToolUp.OpenXml.SvgRasterizer.Skia.Tests.fsproj"
            // Phase 534 — scheduled report subscriptions, plus the
            // narrative egress path Phase 575 could not reach. Its own
            // pack because the cases that matter need BOTH
            // ToolUp.Reporting.Server (the API handler) and
            // ToolUp.Reporting.Docx (the renderer) in one process, and
            // neither project references the other — nor should it, so
            // neither of the existing packs could host them.
            TestPack.create "Reporting" "src/ToolUp.Reporting.Tests/ToolUp.Reporting.Tests.fsproj"
            // Phase 24 — ToolUp.Offline: the Core retry / status /
            // drain-selection model and the sync handler's three guards
            // (server-resolved scope, last-writer-wins conflict
            // detection, audit stamped with the mutation's ORIGINAL
            // enqueue time). Pure — no browser, no network, no
            // credentials. The IndexedDB and Feliz surfaces are
            // browser-only and ride the Fable compile gate instead.
            TestPack.create "Offline" "src/ToolUp.Offline.Tests/ToolUp.Offline.Tests.fsproj"
        ]
}

[<EntryPoint>]
let main args =
    init args
    registerTargets config

    // Phase 614 — the Fable-tier test gate as ONE invocation.
    //
    // `VerifyAll` (in ToolUp.Platform.Build) covers the twelve .NET
    // Expecto packs. The client tier's harness is a different shape —
    // Fable-transpiled F# run under Node's built-in `node:test` — and
    // was until now a four-step recipe every caller reproduced by hand
    // from `CLAUDE.md`. Two transcriptions of one recipe drift; a single
    // target cannot, so CI and a developer necessarily run the same
    // thing.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyFable`
    //
    // The last step deliberately reads the TAP COUNTS, not just the exit
    // code. `node --test` exits 0 when it matched no test file at all,
    // so a harness that silently stopped emitting cases — a moved output
    // path, a Fable compile that wrote elsewhere, an entry point that
    // registered nothing — is indistinguishable from a green run by exit
    // status alone. That is the exact shape of the local `--filter`
    // incident that silently matched 0 Expecto tests. `fableCaseFloor` is
    // a LOWER BOUND, not the exact count: adding a case never needs a
    // companion edit here, and it fires only when the harness has
    // collapsed rather than shrunk.
    let fableCaseFloor = 100

    Target.create "VerifyFable" (fun _ ->
        let testDir = Path.getFullName "src/ToolUp.AI.Client.Tests"

        let onPath name =
            match ProcessUtils.tryFindFileOnPath name with
            | Some path -> path
            | None ->
                failwithf
                    "VerifyFable: `%s` was not found on PATH. The Fable-tier harness needs the .NET SDK and Node.js (>= 20)."
                    name

        let proc exe args =
            CreateProcess.fromRawCommand exe args
            |> CreateProcess.withWorkingDirectory testDir

        let runChecked exe args =
            proc exe args |> CreateProcess.ensureExitCode |> Proc.run |> ignore

        let npm = onPath "npm"
        let node = onPath "node"

        Trace.tracefn "▶ VerifyFable (1/4): dotnet tool restore"
        runChecked "dotnet" [ "tool"; "restore" ]

        // `ci`, not `install` — the lockfile is committed, so this is the
        // reproducible form and it is the same one CI runs.
        Trace.tracefn "▶ VerifyFable (2/4): npm ci"
        runChecked npm [ "ci"; "--no-fund"; "--no-audit" ]

        Trace.tracefn "▶ VerifyFable (3/4): dotnet fable -o output --noCache"
        runChecked "dotnet" [ "fable"; "-o"; "output"; "--noCache" ]

        Trace.tracefn "▶ VerifyFable (4/4): node --test output/Program.js"

        // `--test-reporter=tap` is pinned rather than inherited: node
        // picks `spec` on a TTY and `tap` otherwise, and the summary
        // counts parsed below only exist in the TAP form.
        let result =
            proc node [
                "--import"
                "./register-loader.mjs"
                "--test"
                "--test-reporter=tap"
                "output/Program.js"
            ]
            |> CreateProcess.redirectOutput
            |> Proc.run

        let output = result.Result.Output + result.Result.Error
        printfn "%s" output

        let tapCount (label: string) =
            let prefix = sprintf "# %s " label

            output.Split('\n')
            |> Array.map _.Trim()
            |> Array.tryPick (fun line ->
                if line.StartsWith prefix then
                    match System.Int32.TryParse(line.Substring prefix.Length) with
                    | true, n -> Some n
                    | _ -> None
                else
                    None)

        match tapCount "pass", tapCount "fail" with
        | Some passed, Some failed ->
            Trace.tracefn ""
            Trace.tracefn "VerifyFable summary: %d passed, %d failed (floor %d)." passed failed fableCaseFloor

            if failed > 0 then
                failwithf "VerifyFable: %d node:test case(s) failed." failed

            if passed < fableCaseFloor then
                failwithf
                    "VerifyFable: only %d case(s) ran, below the floor of %d. A run this small means the harness matched almost nothing — check the Fable output path and the entry point before lowering the floor."
                    passed
                    fableCaseFloor
        | _ ->
            failwith
                "VerifyFable: node:test printed no TAP `# pass` / `# fail` summary — the harness did not run. `node --test` exits 0 in that case, which is why the counts are checked and not just the exit status."

        if result.ExitCode <> 0 then
            failwithf "VerifyFable: node exited %d." result.ExitCode)

    // Template-content compile gate.
    //
    // The `templates/` scaffolds are shipped to consumers via `dotnet
    // new`, and nothing compiled them. Two had been broken for an
    // unknown span: platformsdk-application's client carried a
    // `ToolUp.Remoting.Client` PackageReference the 0.4.4 fold deleted
    // (NU1010 under CPM) plus a `ClientConfigOverrides` reference that
    // needed module qualification, and platformsdk-datamanager's
    // Server.fs implemented an `IDataSource` shape — Id / DisplayName /
    // Probe / Pull — that the SDK no longer has. A consumer running
    // `dotnet new` got a project that did not compile.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyTemplates`
    //
    // WHY NOT JUST ADD THEM TO ToolUp.Forge.sln. Template projects
    // reference ToolUp.Platform.* by PackageReference (they must — that
    // is what a consumer's instantiated copy does), and forge itself
    // consumes those only by ProjectReference. CI creates an EMPTY
    // ../../local-nuget-feed, so a template project inside the solution
    // would fail restore in CI and take the whole `verify-all` gate with
    // it. This target supplies the packages instead of assuming them.
    //
    // WHY A THROWAWAY VERSION. Packing at $(ToolUpSdkVersion) into the
    // shared feed would be a same-version repack: NuGet resolves from
    // the already-extracted global-packages entry, so the gate would
    // compile the templates against WHATEVER WAS PACKED LAST rather than
    // against current source — green while the real drift sat
    // undetected. `0.0.0-templategate` is packed to a scratch feed and
    // its cache entries are wiped first, so each run reads bits this run
    // produced. The cache wipe is the load-bearing half; without it the
    // second run onwards is stale.
    //
    // NOT COVERED, deliberately: templates/safer/ and
    // templates/platformsdk-solution/ are standalone solutions carrying
    // their own nuget.config (`../local-nuget-feed`, resolved relative to
    // the consumer's instantiated location) and, in safer's case, a
    // literal `TOOLUP_SDK_VERSION` placeholder substituted at
    // instantiation. Neither is buildable in-repo without rewriting what
    // makes it a template. Gating those needs an instantiate-then-build
    // harness — a bigger job than this, and the drift found here was all
    // in the root-inheriting set.
    let templateGateVersion = "0.0.0-templategate"

    // The SDK packages the gated templates reference, plus the closure
    // of ToolUp.* packages those declare. Kept explicit rather than
    // globbed: packing all ~43 is minutes of work for packages no
    // template names. Adding an SDK->SDK dependency without adding it
    // here does not silently degrade — the NU1603 escalation below turns
    // the resulting version fallback into a build error naming the
    // missing package.
    let templateGatePackages = [
        "ToolUp.Platform.Core"
        "ToolUp.Platform.Client"
        "ToolUp.Platform.Server"
        // Phase 307 — declared by Platform.Client (the UI toolkit,
        // promoted out of the client tier into its own package). This is
        // exactly the SDK->SDK dependency the note above describes: without
        // it, Platform.Client packed at the gate version would declare a
        // ToolUp.Platform.UI the scratch feed cannot serve, and the NU1603
        // escalation would fail the gate by name.
        "ToolUp.Platform.UI"
        // Also declared by Platform.Client, since Phase 344 promoted the
        // AG Grid / AG Charts bindings out of the client tier. They were
        // not added here at the time, and the consequence is not benign:
        // BOTH ids exist on nuget.org (Feliz.AgGrid 0.0.1, Feliz.AgCharts
        // 0.23.0 — unrelated packages by another author), so restore
        // silently fell through to a stranger's package and NU1603 turned
        // that into a hard error. The `templates` CI job has been red on
        // main since 344 landed. Found and fixed by Phase 307, which hit
        // it while adding ToolUp.Platform.UI above — the very case the
        // note at the head of this list describes.
        "Feliz.AgGrid"
        "Feliz.AgCharts"
        // Declared by Platform.Core.
        "ToolUp.AI.Wire"
        // Declared by Platform.Server, and its own ProjectReference.
        "ToolUp.Graph.InMemory"
        "ToolUp.Graph.Core"
    ]

    let gatedTemplateProjects = [
        "templates/platformsdk-application/src/MyApp-Client/MyApp-Client.fsproj"
        "templates/platformsdk-application/src/MyApp-Server/MyApp-Server.fsproj"
        "templates/platformsdk-datamanager/MyDataManager/MyDataManager.fsproj"
        "templates/platformsdk-module/MyModule/MyModule.fsproj"
    ]

    Target.create "VerifyTemplates" (fun _ ->
        let feedDir = Path.getFullName "obj/template-gate-feed"

        let runChecked exe args =
            CreateProcess.fromRawCommand exe args
            |> CreateProcess.withWorkingDirectory "."
            |> CreateProcess.ensureExitCode
            |> Proc.run
            |> ignore

        // Same invocation, minus `ensureExitCode`: the caller wants the
        // exit code back rather than an exception, so an aggregating
        // loop can record it and carry on.
        let runExit exe args =
            let result =
                CreateProcess.fromRawCommand exe args
                |> CreateProcess.withWorkingDirectory "."
                |> Proc.run

            result.ExitCode

        Shell.deleteDir feedDir
        Directory.ensure feedDir

        // Wipe this version's global-packages entries so the restore
        // below cannot serve a previous run's extracted copy. See the
        // "WHY A THROWAWAY VERSION" note above.
        let globalPackages =
            match Environment.environVarOrNone "NUGET_PACKAGES" with
            | Some dir when dir <> "" -> dir
            | _ ->
                Path.Combine(
                    System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile,
                    ".nuget",
                    "packages"
                )

        for pkg in templateGatePackages do
            let cached =
                Path.Combine(globalPackages, pkg.ToLowerInvariant(), templateGateVersion)

            if Directory.Exists cached then
                Trace.tracefn "▶ VerifyTemplates: clearing stale cache entry %s" cached
                Shell.deleteDir cached

        for pkg in templateGatePackages do
            Trace.tracefn "▶ VerifyTemplates: packing %s @ %s" pkg templateGateVersion

            runChecked "dotnet" [
                "pack"
                sprintf "src/%s/%s.fsproj" pkg pkg
                sprintf "-p:Version=%s" templateGateVersion
                "-o"
                feedDir
                "--nologo"
            ]

        // `RestoreAdditionalProjectSources` ADDS the scratch feed to the
        // repo nuget.config's sources. `--source` would replace them, and
        // the templates still need nuget.org for Feliz / Fable.Core.
        //
        // NU1603 is escalated to an error deliberately. It fires when a
        // gate-versioned package declares a ToolUp.* dependency that was
        // NOT packed at the gate version, in which case NuGet quietly
        // resolves some other version off the shared feed. That fallback
        // compiles the templates against a mix of current and months-old
        // SDK, and on a machine whose feed lacks the older version it
        // does not resolve at all — so the failure would surface later,
        // somewhere unrelated. Escalated, it names the missing package
        // and points straight at `templateGatePackages`.
        // The template projects are INDEPENDENT of one another — each is
        // what a separate `dotnet new` produces — so a broken scaffold
        // should cost its own signal and not the others'. `runExit`
        // rather than `runChecked` for exactly that reason: the exit code
        // is captured and aggregated instead of thrown, and the target
        // still fails at the end naming every scaffold that did not
        // compile. Before this, a reader of a red run learned that the
        // FIRST project in the list was broken and nothing whatsoever
        // about the rest.
        //
        // The pack loop above stays fail-fast deliberately, and the
        // asymmetry is the point: every build below reads those packages,
        // so a failed pack makes the remaining results meaningless rather
        // than merely unknown. Aggregating there would manufacture four
        // confident-looking failures out of one real one.
        gatedTemplateProjects
        |> List.map (fun proj ->
            Aggregate.leg proj (fun () ->
                Trace.tracefn "▶ VerifyTemplates: building %s" proj

                // Clear obj/ and bin/ first. Both are gitignored local
                // artefacts, and a consumer's freshly-instantiated copy has
                // neither — so building over them tests something the
                // consumer never experiences. Concretely: NuGet no-ops a
                // restore whose inputs are unchanged and REPLAYS the
                // warnings recorded in the existing project.assets.json, so
                // a leftover obj/ makes the gate report the previous run's
                // resolution rather than this one's.
                let projDir = Path.GetDirectoryName(Path.getFullName proj)
                Shell.deleteDir (Path.Combine(projDir, "obj"))
                Shell.deleteDir (Path.Combine(projDir, "bin"))

                runExit "dotnet" [
                    "build"
                    proj
                    sprintf "-p:ToolUpSdkVersion=%s" templateGateVersion
                    sprintf "-p:RestoreAdditionalProjectSources=%s" feedDir
                    "-warnaserror:NU1603"
                    "--nologo"
                ]))
        |> Aggregate.runAll "VerifyTemplates" "template project")

    // Phase 620 — compile-checked documentation snippets.
    //
    // Nothing detected a doc snippet naming an API the SDK no longer
    // has. The docs teach `fsharp` fenced blocks a reader copies
    // verbatim; when a phase renames or removes the thing a snippet
    // calls, the snippet silently becomes a lie that compiles nowhere
    // and fails at the reader's first build. Two live instances were
    // found minutes apart by a human noticing, which is not a process.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyDocSnippets`
    //        `… -- VerifyDocSnippets --update-baseline`  (see below)
    //
    // ── What is in scope (620.A) ────────────────────────────────────
    //
    // IN SCOPE BY DEFAULT: every ```fsharp block under `docs/**` and
    // `src/ToolUp.Platform/technical-guide/**`. The escape is a marker
    // in the fence INFO STRING — ```fsharp skip=<reason> — drawn from a
    // CLOSED set (`docSnippetSkipReasons`). An unknown reason, a bare
    // `skip`, or any other unrecognised attribute FAILS the target, so
    // the escape cannot be widened by typo or by invention; widening it
    // is a visible diff to this file.
    //
    // The info string is metadata, not content: every Markdown renderer
    // takes the FIRST word as the language, so `fsharp skip=fragment`
    // still highlights as F# and the marker is invisible to a reader.
    // That is what makes an in-band marker acceptable here — it adds no
    // ceremony to the code a reader copies.
    //
    // TWO tree-level exclusions, and they are the same class:
    // point-in-time documents whose code deliberately reflects a state
    // other than the current surface.
    //
    //   `docs/migrations/**` — a migration doc's job is to show the
    //   RETIRED shape beside its replacement, usually in the same block.
    //   Compiling it is category-incorrect: the old shape must not
    //   compile, that is the point of the page.
    //
    //   `docs/design/**` — a design record states what was PROPOSED.
    //   Where implementation diverged from the proposal, rewriting the
    //   blocks against the shipped surface would destroy exactly the
    //   value the document has (it would no longer record what was
    //   argued for), and marking them individually would mean widening
    //   the closed skip set to cover "historically accurate".
    //
    // Both are tree exclusions rather than per-block markers precisely
    // because a marker on every block of such a page would be the easy
    // opt-out that gets reached for. Widening this list is a visible
    // diff to this file and needs the same argument: the tree's code is
    // point-in-time BY DESIGN, not merely inconvenient to fix.
    //
    // ── How a fragment declares its context (620.B) ─────────────────
    //
    // Snippets are excerpts, not programs, and the docs must not grow
    // `open`-ceremony a reader then copies. So context is supplied
    // OUT-OF-BAND, in four layers, none of which touch the markdown:
    //
    //   1. `docSnippetPreamble` — the ambient opens any ToolUp source
    //      file has.
    //   2. `docSnippetTreePreamble` — per doc tree; a page under
    //      `docs/rag/` is read in the context of the RAG package.
    //   3. Per-page AMBIENT PREAMBLE — an optional F# file at
    //      `docs-snippets/ambient/<doc path>.fs` (the doc tree mirrored,
    //      `.md` -> `.fs`), inlined verbatim into every generated block
    //      of that page. See below.
    //   4. Page accumulation — `open` lines declared in an EARLIER
    //      block of the same page apply to later ones, which is how a
    //      reader reads a page top to bottom.
    //
    // ── The per-page ambient preamble, and what it is FOR ───────────
    //
    // A large share of doc blocks are not drifted and not prose: they
    // are excerpts of a composition root the page never shows in full,
    // reading locals a reader is expected to already have in scope —
    // `config`, `providerProfile`, `secretStore`, `authProvider`, an
    // Elmish `Model`, a page-local `loadCampaign`. Before this layer
    // existed the only honest classification for those was
    // `skip=fragment`, which buys silence: the block's SDK names are
    // then checked by nothing, so the next rename rots it invisibly —
    // the exact drift class this target exists to catch, in the blocks
    // the target cannot see.
    //
    // The ambient file declares those locals ONCE per page, out of
    // band, so the block compiles as written and every SDK name in it
    // is checked. It is ordinary F# (opens, type declarations, `let`
    // bindings — `failwith "ambient"` is the conventional body, since
    // nothing here runs), and it is inlined ahead of the block under
    // its own `#line` directive, so an error INSIDE an ambient file is
    // reported against that file and lands in the unattributable bucket
    // — a harness fault, which is exactly what it is.
    //
    // The rule for what may go in one: an ambient declaration stands in
    // for something the PAGE's own surrounding program would provide.
    // It must never redeclare an SDK name — that would fake the surface
    // the block is supposed to be checked against, turning the gate
    // into a mirror. When in doubt, name the binding after the doc's
    // own prose and give it the SDK type, so the type is still checked.
    //
    // SHAPE of an ambient file — `open`s at the top, everything else
    // inside one auto-opened module:
    //
    //     open ToolUp.PublicRendering
    //
    //     [<AutoOpen>]
    //     module PageAmbient =
    //         type Campaign = { Name: string }
    //         let loadCampaign (ctx: CallContext) (c: string) = failwith "ambient"
    //
    // The file is inlined VERBATIM — no reordering — so the `#line`
    // attribution stays exact. The two halves are both load-bearing:
    // top-level `open`s must be visible to the block (an `open` nested
    // inside the module would not be), while the declarations must NOT,
    // because a page routinely introduces a type in its first block and
    // reads it from its fifth. Flat declarations would collide with that
    // first block; auto-opened ones are simply SHADOWED by it, so the
    // page teaches the type once and every later block still compiles.
    //
    // See `docs-snippets/ambient/README.md`.
    //
    // Each block then compiles as its own module in its own generated
    // file. One file per BLOCK, not per page, is load-bearing: F#
    // abandons a file at its first parse error, so a single malformed
    // block in a shared file would mask every later block on the page.
    //
    // What this therefore gates is "every SDK name a snippet uses still
    // exists, with the shape shown" — not "this block is a runnable
    // program". That is the drift class, and it is the one that bites
    // readers.
    //
    // ── Where the failure points (620.E) ────────────────────────────
    //
    // Each generated file carries an F# `# <line> "<abs path to the
    // .md>"` directive, so the COMPILER ITSELF reports at the markdown
    // file and line — `docs/platform/auth.md(88,13): error FS1129: …`.
    // No error-message rewriting, and the location survives every
    // downstream tool that understands compiler output. The summary
    // below additionally groups by file and block ordinal.
    //
    // ── The baseline, and why it is not a way of going green ────────
    //
    // The corpus was measured before the gate was designed: of 655
    // in-scope blocks, 231 already named at least one API the SDK does
    // not have. Fixing all of them is a docs project, not this phase,
    // and marking them `skip=` would be a lie — `skip` means "not
    // checkable", not "checkable and currently wrong". They are instead
    // recorded in `docs-snippets/known-drift.txt`, keyed by a hash of
    // the block's own text.
    //
    // The baseline is a RATCHET, not a mute button, and it fails in
    // BOTH directions:
    //   * any failing block absent from the baseline fails the target
    //     (new drift cannot land);
    //   * any baseline entry that now compiles ALSO fails the target,
    //     demanding its removal (the baseline can only shrink).
    // Because the key is the block's content hash, editing a baselined
    // block invalidates its entry — so a broken snippet cannot be
    // quietly rewritten into a differently-broken one.
    //
    // Same posture as the Phase 175 public-API approval baseline, and
    // the same posture Phase 614 took when `verify-all` was not green
    // on Linux: record the evidence, gate what is true today, and let
    // the number only fall.
    //
    // `--update-baseline` REWRITES the file from the current run. It is
    // for the first seeding and for wholesale re-measurement after a
    // deliberate corpus change; it is never part of making CI pass.
    let docSnippetRoots = [ "docs"; "src/ToolUp.Platform/technical-guide" ]
    let docSnippetExcludedTrees = [ "docs/migrations"; "docs/design" ]

    // ── Phase 669: the PACKED teaching surfaces, and the contributor
    //    guide ────────────────────────────────────────────────────────
    //
    // `docs/**` is the site. It is not the whole of what a consumer
    // reads, and for a companion package it is frequently not what they
    // read FIRST. `Directory.Build.props` auto-includes every
    // `src/**/README.md` into its own nupkg, so a companion's README
    // ships INSIDE the artefact and is the page a consumer lands on from
    // nuget.org — the first, and for most of the long-tail companions the
    // only, teaching surface they will ever see. It was the one teaching
    // surface with no ratchet at all, which is exactly where the Phase
    // 660 burn-down found the same drift classes it had just cleared out
    // of `docs/**` sitting untouched.
    //
    // Three name-scoped additions rather than a `src` tree root, because
    // `src/**` is source, not documentation: a bare tree root would sweep
    // in 72 CHANGELOGs, ten LICENSEs and a `node_modules` tree, none of
    // which is teaching material and the last of which is not even ours.
    //
    //   `src/**/README.md`          — packed into the nupkg; see above.
    //   `src/**/TECHNICAL_GUIDE.md` — the per-package deep-dive the
    //                                 README links to. Same audience, same
    //                                 packing convention, and the largest
    //                                 single block counts in the estate
    //                                 (`ToolUp.AI` alone teaches 22).
    //   `CLAUDE.md`                 — the CONTRIBUTOR guide. A different
    //                                 audience from the other two, and
    //                                 deliberately in scope anyway: its
    //                                 module sample is the canonical
    //                                 four-file shape every consumer
    //                                 module is copied from, and it taught
    //                                 `Icon = "/svg/chart.svg"` against a
    //                                 `ReactElement` field for as long as
    //                                 that field has been typed. A guide
    //                                 that mis-teaches the shape is not
    //                                 less costly for addressing a
    //                                 contributor; it is more.
    //
    // `bin` / `obj` / `node_modules` are pruned by name during the walk.
    // The first two are build output (a packed README is copied into
    // `bin/` on its way into the nupkg, so an unpruned walk double-counts
    // every one of them); the third is vendored third-party content whose
    // prose is not ours to hold to our surface.
    let docSnippetSrcRoot = "src"
    let docSnippetSrcFileNames = set [ "README.md"; "TECHNICAL_GUIDE.md" ]
    let docSnippetPrunedDirs = set [ "bin"; "obj"; "node_modules" ]
    let docSnippetLooseFiles = [ "CLAUDE.md" ]

    // The closed escape set. Each reason is a claim about the block's
    // SHAPE that a reviewer can check by reading it:
    //   signature   — an `.fsi`-shaped API listing (`module M = val f: …`).
    //                 Not implementation F#; cannot be compiled as such.
    //   fragment    — an excerpt: an elided body (`|> ...`), or it reads
    //                 locals belonging to a surrounding program the page
    //                 does not show.
    //   anti-pattern— deliberately wrong, shown as a "don't".
    // "It failed to compile" is NOT a reason — that is what the
    // baseline is for.
    let docSnippetSkipReasons = set [ "signature"; "fragment"; "anti-pattern" ]

    // Floor guard: this target legitimately exits 0 with an empty
    // corpus (a bad glob, a moved docs folder, an extractor that
    // matched no fences), which is indistinguishable from a pass unless
    // the count is asserted.
    //
    // A STATIC lower bound catches the collapse but nothing else, and a
    // static number cannot ratchet: as the corpus grows the gap between
    // the floor and the truth widens into room for silent hollowing —
    // skip-marking a block that used to compile costs the corpus one
    // checked block and the gate says nothing, because the floor was
    // set years of docs ago. The floor is therefore a HIGH-WATER MARK
    // recorded in `docs-snippets/corpus-floor.txt`:
    //
    //   * `compiled < mark` FAILS. A block that used to compile no
    //     longer does — skip-marked, deleted, or moved into an excluded
    //     tree. Legitimate cases exist (that is what the exclusion of
    //     `docs/design` was), and the remedy is to lower the number BY
    //     HAND, which puts the decision in the diff where a reviewer
    //     sees it. Deliberately not a flag: an automated lower is the
    //     one motion this guard exists to make expensive.
    //   * `compiled > mark` REWRITES the mark and says so. Growth is
    //     always legitimate, so it must not red a build over a docs
    //     addition; but the new number lands in the working tree, so it
    //     rides the author's own commit as a reviewed one-line diff
    //     rather than being auto-committed by anything.
    //
    // The collapse case the static floor existed for is subsumed: a
    // harness that matches almost nothing scores far below the mark.
    let docSnippetFloorSeed = 300

    // Phase 668 — doc-declared type parity.
    //
    // The compile gate above has one structural blind spot, and it is the
    // one an api-reference page walks into by design. A block that
    // REDECLARES a public SDK type — `type IJobScheduler = abstract …` —
    // compiles green forever, because the local declaration SHADOWS the
    // real type and the compiler never consults the surface the doc claims
    // to describe. `docs/platform/jobs.md` taught a six-method
    // `IJobScheduler` for as long as this gate has existed; four of those
    // methods have not been on the interface for releases, and every run
    // was green. Phase 660's zero-drift guarantee is hollow wherever a doc
    // re-declares.
    //
    // So a second, compile-independent check: read the type names a block
    // declares, resolve them against the Phase 175 `api-baselines/`
    // rendering of the real public surface, and hold the declaration to
    // what the surface says.
    //
    // ── The elision rule: SUBSET, and there is no marker ─────────────
    //
    // Doc listings are routinely simplified — six of eleven methods, the
    // fields that matter to the page. Two rules could accommodate that: an
    // ELISION MARKER inside the block (unmarked blocks must be exhaustive),
    // or MEMBERSHIP-SUBSET TOLERANCE (whatever the block shows must be
    // real; what it omits is its own business). Subset, for two reasons,
    // and the second is the decisive one.
    //
    // First, the marker answers the wrong question. A member that CHANGED
    // does not present here as a missing member; it presents as a member
    // name the real type does not have (a rename) or a signature naming a
    // type the real member does not use (a retype). Both are the EXTRA
    // direction. A marker's whole semantic domain is the MISSING direction,
    // so it can never speak to a changed member — and if it is read, as it
    // inevitably would be, as "this listing is partial, relax", the
    // relaxation leaks into the extra direction and starts hiding exactly
    // the drift this check exists to catch. `IJobScheduler.Unschedule`
    // would become an elision.
    //
    // Second, an unmarked-means-exhaustive rule reddens every listing on
    // the page the next time the SDK GAINS a member — a maintenance tax
    // unrelated to mis-teaching — so the marker would be applied wholesale
    // and read thereafter as "reviewed", having been typed to go green.
    // This file already carries that argument twice: the skip set is closed
    // and widening it is a visible diff here, and the corpus floor's shrink
    // is deliberately a hand edit rather than a flag.
    //
    // Subset tolerance needs no escape and offers none. It asserts the one
    // claim a reader actually relies on — EVERY MEMBER THIS BLOCK TEACHES
    // IS A MEMBER THE SDK HAS — and makes the missing direction a
    // non-question by construction.
    //
    // ── What is compared, and why not more ───────────────────────────
    //
    // Per shown member: its NAME must exist on the real type, and the
    // public SDK type names its declared signature mentions must be a
    // SUBSET of those the real member's rendering mentions. That second
    // half is what catches a retype — `Schedule: JobDefinition -> …`
    // against a real `Schedule(JobRegistration)` — without pretending to
    // normalise F# source syntax into reflection metadata.
    //
    // Full signature equality was implemented first and measured before
    // being discarded: it produced 416 findings across the corpus, of which
    // essentially none were drift. Metadata erases F# type abbreviations
    // (`type JobId = Guid` renders as `System.Guid`, so a doc writing
    // `JobId` looks wrong), primitives render as `System.String` where a
    // doc writes `string`, and curried arrows render as tupled parameter
    // lists. Restricting the comparison to names the surface itself OWNS,
    // in the subset direction, dropped that to 5 — every one a genuine
    // mis-teach. A gate whose findings are mostly rendering artefacts is
    // one people learn to step over.
    //
    // Two residuals, stated rather than hidden, and both visible in the
    // census line rather than papered over:
    //   * a same-name member retyped only in BCL terms (`string` -> `int`)
    //     is not caught — no surface-owned name changed;
    //   * a SINGLE-LINE union (`type ChunkOrigin = Document | Note | …`)
    //     is read as `not comparable`, because the member reader keys off
    //     `|` at the start of a line. Six declarations sit in that bucket
    //     today. Worth closing; not closed here, because widening the
    //     reader is a corpus-wide change and this phase landed its own
    //     corpus at zero.
    //
    // ── Three soundness filters, each measured ───────────────────────
    //
    //   * AMBIGUOUS NAMES ARE NOT COMPARED. A doc's own Elmish `Model` /
    //     `Msg` example collides with the dozens of nested `…UI+Msg` types
    //     the surface renders; matching a bare `Msg` to one of them was
    //     never sound. A simple name resolving to more than one public type
    //     is counted and skipped.
    //   * BCL-COLLIDING SIMPLE NAMES ARE NOT SURFACE NAMES. A ToolUp module
    //     named `String` would otherwise make every `System.String` field
    //     in the baselines read as a ToolUp type and every doc `string`
    //     read as a mismatch.
    //   * F# CORE ALIASES ARE NOT SURFACE NAMES (`docParityAliasNames`) —
    //     `Set<SubjectKind>` renders as `FSharpSet\`1[…]`, so a doc's `Set`
    //     has no counterpart to be a subset of.
    //
    // The surface is read from the COMMITTED `api-baselines/` text, not
    // from built DLLs. That is what makes this check independent of build
    // state — it cannot be quietened by a stale `bin/`, and it runs before
    // the snippet project is compiled at all. The baselines are themselves
    // gated in both directions by the Phase 175 / 618 approval test, so a
    // surface change that has not been folded into them fails there rather
    // than silently moving the target here.
    //
    // SKIPPED blocks are IN SCOPE here, deliberately. `skip=signature` is
    // precisely the `.fsi`-shaped api-reference listing, i.e. the highest-
    // risk shape for this defect, and the check needs no compiler — so the
    // one pool the compile gate cannot see is the one pool this check can.
    let docParityAliasNames =
        set [
            "String"
            "Set"
            "Map"
            "List"
            "Array"
            "Option"
            "Async"
            "Result"
            "Seq"
            "Choice"
            "Ref"
            "Lazy"
            "Nullable"
            "Task"
            "Tuple"
            "Char"
            "Byte"
            "Guid"
            "DateTime"
            "TimeSpan"
        ]

    // Phase 672 — the fragment symbol-existence lint.
    //
    // `skip=fragment` buys silence, and the census above says how much:
    // 352 blocks at the time this was written, six times the Phase 660
    // estimate. A fragment is exempt from compilation, so a
    // `PlatformMode`-class rename rots it and every run stays green —
    // the exact drift class this target exists to catch, occurring in
    // the one pool the target cannot see.
    //
    // Existence, unlike correctness, is checkable WITHOUT compiling. A
    // fragment's dotted, capitalized identifiers (`ServerApp.withStorage`,
    // `ChunkOrigin.Note`) and its record-construction field labels either
    // resolve against the name universe the Phase 175 `api-baselines/`
    // render, or name something the block itself introduces, or they are
    // a lie. That is a weak check and it is deliberately weak: a RENAMED
    // api is caught, a RETYPED one is not. Retyping is compilation's job,
    // and redeclaration is 668's.
    //
    // ── The false-positive budget is the whole design ────────────────
    //
    // A fragment is, by its own marker, an excerpt full of names that are
    // not SDK names: locals of a surrounding program, deliberate
    // placeholders (`MyModule.analyse`), vendor identifiers, BCL calls.
    // A lint that fires on those is a lint someone turns off, and the
    // obvious remedy — an allow-list file — is a second baseline, which
    // this file has twice argued against. So the escape is a SHAPE rule
    // instead, and it has one governing idea: THE LINT SPEAKS ONLY ABOUT
    // NAMES THE SURFACE OWNS.
    //
    //   * ROOT-ANCHORED. A chain is checked only where a segment names a
    //     container the surface owns. `MyModule.analyse`, `Console.Write`,
    //     `Fable.Core.JsInterop` anchor nowhere and are not checked — not
    //     because they were listed, but because nothing in the universe
    //     answers to them. This one rule carries most of the budget.
    //   * A VALUE'S PROPERTY IS NOT A CONTAINER. Every segment before the
    //     anchor must be capitalized, so `ctx.Progress.Report` cannot
    //     anchor at `Progress` even when a `Progress` type exists.
    //   * WHAT THE BLOCK INTRODUCES WINS. A `type` / `module` / capitalized
    //     `let` in the block, in ANY block of the same page (a reader reads
    //     a page top to bottom — the same accumulation the compile arm
    //     gives `open`), or in the page's ambient preamble, takes the name
    //     out of the check.
    //   * AMBIGUOUS NAMES ARE NOT COMPARED, on 668's measured reasoning: a
    //     simple name resolving to more than one container was never
    //     soundly matchable to one of them.
    //   * DU CASES DO NOT CONTRIBUTE CONTAINER NAMES. `ContentBody+Html`
    //     would otherwise put Feliz's `Html.div` inside the universe and
    //     fail it. The case is still reachable as a MEMBER of its parent,
    //     which is how a doc legitimately writes `ContentBody.Html`.
    //   * DECLARATION LINES ARE NOT MEMBER ACCESSES. `open` / `namespace` /
    //     `module` carry a namespace path; asking whether a namespace
    //     segment "has a member" is not a question.
    //   * STRING LITERALS AND COMMENTS ARE STRIPPED, and the BCL-colliding
    //     and F#-core-alias filters above apply unchanged.
    //
    // ── Two arms, because a rename does not always present as a dot ───
    //
    // The dotted arm alone misses the shape a record-building fragment
    // takes: `docs/platform/jobs.md` builds a job with `HandlerName`,
    // `Retry` and `IdempotencyKey`, none of which the surface has had for
    // releases, and not one of them is dotted. So a second arm reads
    // RECORD-CONSTRUCTION FIELD LABELS.
    //
    // It has no type annotation to anchor on, so it INFERS one: the
    // surface record whose members cover most of the region's labels. An
    // inference needs a floor or it invents anchors, and these three were
    // measured rather than picked — the first cut, without them, produced
    // ten findings that were all wrong anchor:
    //   * a region needs >= `docFragmentMinRecordLabels` labels;
    //   * the best candidate must match >= `docFragmentMinRecordMatches`
    //     of them AND at least half of them;
    //   * that best must be UNIQUE — a tie means the doc is describing
    //     something the surface does not uniquely name, and silence is
    //     the honest answer.
    // One BRACE REGION is one construction. Tracking depth alone folds a
    // nested literal into its parent's label set, which then anchors on
    // neither type and reports the nesting itself as missing fields; that
    // was most of the first cut's remaining noise. `{| … |}` opens no
    // region at all — an anonymous record has no surface type to be held to.
    //
    // ── Retirement (672.C) ───────────────────────────────────────────
    //
    // THIS LINT IS A BRIDGE AND IS MEANT TO BE DELETED. Phase 660.B is
    // converting `skip=fragment` blocks to compiled ones at docs-project
    // pace, and every block it converts leaves this lint's universe by
    // construction — the scan reads `skip=fragment` and nothing else, so
    // no edit here is ever needed to hand a block over. What the lint
    // gives is coverage of the blocks 660.B has not reached yet, and its
    // value falls as that pool does.
    //
    // The deletion condition is therefore a NUMBER, not a judgement: when
    // the `skip=fragment` count on the summary reaches the floor 660.B
    // lands on — the residue of genuinely elided, prose-shaped excerpts
    // that no ambient preamble can rescue — this whole section goes, with
    // its census line, and the remaining fragments are accepted as
    // unchecked. Deleting it earlier would drop live coverage; keeping it
    // afterwards is a second gate over a handful of blocks, paid for on
    // every run. The `fragments walked` figure on the census line is what
    // that decision reads.
    let docFragmentMinRecordLabels = 3
    let docFragmentMinRecordMatches = 3

    // ---- The api-baseline surface universe, shared by both citation gates ----
    //
    // Phase 670 hoisted this out of `VerifyDocSnippets`. Phases 668, 669 and
    // 672 each read the committed `api-baselines/` text to answer a different
    // question about the same universe, and `VerifySourceCitations` below
    // asks a fourth about SOURCE comments rather than doc blocks. Four
    // readers of one rendering is the point: a second parser would be a
    // second idea of what the surface is, and the two would diverge exactly
    // where a rename made the answer matter.
    //
    // The read stays LAZY. A `Pack` or `Format` run must not pay for a
    // directory walk it never consults, and the value is identical across
    // targets within a process.
    let rxMatches (pattern: string) (input: string) =
        System.Text.RegularExpressions.Regex.Matches(input, pattern)
        |> Seq.map (fun m -> m.Value)
        |> List.ofSeq

    let rxGroups (pattern: string) (input: string) =
        let m = System.Text.RegularExpressions.Regex.Match(input, pattern)

        if m.Success then
            Some [ for g in m.Groups -> g.Value ]
        else
            None

    let simpleNameOf (s: string) =
        let bare = s.Split('`')[0]
        let i = max (bare.LastIndexOf '.') (bare.LastIndexOf '+')
        if i >= 0 then bare.Substring(i + 1) else bare

    let readApiSurface (repoRoot: string) =
        // ---- Phase 668: the public surface, read from api-baselines ----
        let apiBaselineDir = Path.Combine(repoRoot, "api-baselines")


        // Phase 669 — a container the RENDERER could not read is NOT
        // COMPARABLE, and must never be rendered as "this type has no
        // members".
        //
        // `PublicApiApproval` writes `<full>  # <members unavailable:
        // FileNotFoundException>` when reflection cannot load an
        // assembly's dependencies to enumerate its members. That line is
        // not a member line, so an unguarded reader below sees a
        // container with an EMPTY member list — and every doc that names
        // one of its members becomes a finding whose remedy is to delete
        // a perfectly correct sentence. Phase 669's walk hit exactly that
        // on ten findings across `AwsLambdaHost`, the three
        // `*KmsKeyResolver`s and the KMS artefact signers, all of which
        // exist in source and are spelled right in the docs.
        //
        // Same posture the estate takes everywhere else: "I cannot read
        // this" is reported as unknown, never as wrong. Dropping the
        // container entirely puts its members in the lint's OUTSIDE
        // bucket, where an unresolvable name already lives.
        let unreadableTypes =
            if Directory.Exists apiBaselineDir then
                Directory.EnumerateFiles(apiBaselineDir, "*.approved.txt")
                |> Seq.collect File.ReadAllLines
                |> Seq.choose (rxGroups @"^(\S+)\s+# <members unavailable")
                |> Seq.map (fun groups -> groups[1])
                |> Set.ofSeq
            else
                Set.empty

        // (full name, [member name, rendered member line]). Nested types
        // keep their `+`, so the DU-case reader below finds `T+Case`.
        let realTypes =
            if Directory.Exists apiBaselineDir then
                Directory.EnumerateFiles(apiBaselineDir, "*.approved.txt")
                |> Seq.collect (fun f ->
                    let lines =
                        File.ReadAllLines f
                        |> Array.filter (fun l -> l.Trim() <> "" && not (l.StartsWith "#"))

                    lines
                    |> Array.choose (rxGroups @"^(\S+) \((?:class|interface|struct|enum|delegate)\)$")
                    |> Array.filter (fun groups -> not (unreadableTypes.Contains groups[1]))
                    |> Array.map (fun groups ->
                        let full = groups[1]
                        let prefix = full + "."

                        let members =
                            lines
                            |> Array.filter (fun l -> l.StartsWith prefix)
                            |> Array.map (fun l ->
                                let rest = l.Substring prefix.Length
                                let cut = rest.IndexOfAny [| '('; ' ' |]
                                (if cut >= 0 then rest.Substring(0, cut) else rest), rest)
                            |> List.ofArray

                        full, members))
                |> List.ofSeq
            else
                []

        let realFullNames = realTypes |> List.map fst |> Set.ofList

        // An F# `module X` holding a `type X` renders as BOTH `A.X` and
        // `A.X+X`. That is not two competing types — it is a container and
        // the thing inside it, and a doc writing `X` after `open A` means
        // the latter. Left uncollapsed it reads as ambiguous, and the
        // whole family of interfaces whose module shares their name
        // (`IVectorStore`, `IRetrievalPipeline`, `IRetrievalTracer`, …)
        // drops silently out of the comparison. Found by the
        // demonstrated-red probe rather than by reading the code, which is
        // what that discipline is for: the check was green on a
        // deliberately staled signature.
        let realByName =
            realTypes
            |> List.groupBy (fst >> simpleNameOf)
            |> List.map (fun (name, candidates) ->
                let fulls = candidates |> List.map fst |> Set.ofList

                let collapsed =
                    candidates
                    |> List.filter (fun (full, _) -> not (fulls.Contains(full + "+" + name)))

                name, (if collapsed.IsEmpty then candidates else collapsed))
            |> Map.ofList

        // A simple name the SURFACE uses for a BCL type is not evidence of
        // a ToolUp type of that name — see the soundness filters above.
        let bclSimpleNames =
            realTypes
            |> Seq.collect (fun (_, ms) -> ms |> Seq.map snd)
            |> Seq.collect (rxMatches @"[A-Za-z_][A-Za-z0-9_.+`]*")
            |> Seq.filter (fun v -> v.StartsWith "System." || v.StartsWith "Microsoft.")
            |> Seq.map simpleNameOf
            |> Set.ofSeq

        let ownedSimpleNames =
            Set.difference (realByName |> Map.keys |> Set.ofSeq) (Set.union bclSimpleNames docParityAliasNames)

        let unambiguousTypeNames =
            realByName |> Map.filter (fun _ v -> v.Length = 1) |> Map.keys |> Set.ofSeq
        // ---- Phase 672: the name universe a fragment is held to ----
        //
        // Same `api-baselines/` reading as 668 above, re-keyed for the
        // question this arm asks. 668 resolves a DECLARED type name to one
        // rendered type; this resolves a USED container name to the set of
        // members a doc may name on it, which is a union rather than a
        // choice — over-accepting only ever passes a doc, and the subset
        // direction stays sound.
        // Phase 669 — member names by full type name, so a container can
        // fold in the cases of a DU nested inside it (see `surfaceContainers`).
        let membersByFull =
            realTypes |> List.map (fun (full, ms) -> full, ms |> List.map fst) |> Map.ofList

        let nestedByParent =
            realFullNames
            |> Seq.choose (fun f ->
                let i = f.LastIndexOf '+'

                if i > 0 then
                    Some(f.Substring(0, i), f.Substring(i + 1))
                else
                    None)
            |> Seq.groupBy fst
            |> Seq.map (fun (parent, xs) -> parent, xs |> Seq.map snd |> List.ofSeq)
            |> Map.ofSeq

        let realMemberNames =
            realTypes
            |> List.map (fun (f, ms) -> f, (ms |> List.map fst |> Set.ofList))
            |> Map.ofList

        // F# appends `Module` to a module that shares its name with a type
        // in the same scope, so `RAGServerAppModule` IS what a doc calls
        // `RAGServerApp`. The alias is ADDITIVE, never a replacement:
        // `ServerModule` is a TYPE whose own name ends in `Module`, and
        // replacing would file it under `Server` and lose it.
        let docFacingNamesOf (full: string) =
            let s = simpleNameOf full

            if s.EndsWith "Module" && s.Length > 6 then
                [ s; s.Substring(0, s.Length - 6) ]
            else
                [ s ]

        let isDuCase (full: string) =
            let i = full.LastIndexOf '+'

            if i < 0 then
                false
            else
                match realMemberNames.TryFind(full.Substring(0, i)) with
                | Some ms -> ms.Contains("Is" + full.Substring(i + 1))
                | None -> false

        // doc-facing container name -> (distinct containers, member names)
        let surfaceContainers =
            let byName =
                System.Collections.Generic.Dictionary<string, ResizeArray<string * (string * string) list>>()

            for (full, members) in realTypes do
                for key in docFacingNamesOf full do
                    if not (byName.ContainsKey key) then
                        byName[key] <- ResizeArray()

                    byName[key].Add(full, members)

            byName
            |> Seq.map (fun kv ->
                let usable =
                    kv.Value |> Seq.filter (fun (full, _) -> not (isDuCase full)) |> List.ofSeq

                // Two renderings can be ONE doc-facing container: a module
                // and the type it shadows (`X` / `XModule`), and a module
                // and the type nested inside it (`A.X` / `A.X+X`). Generic
                // arity is a third such detail. Collapse only WITHIN this
                // key's candidates — the global form of the first rule
                // fuses `ToolUp.Platform.Server` with the unrelated
                // `ToolUp.Platform.ServerModule`.
                let dropArity (f: string) =
                    System.Text.RegularExpressions.Regex.Replace(f, @"`\d+", "")

                let here = usable |> List.map (fst >> dropArity) |> Set.ofList

                let canonical (raw: string) =
                    let full = dropArity raw

                    let f =
                        if
                            full.EndsWith "Module"
                            && full.Length > 6
                            && here.Contains(full.Substring(0, full.Length - 6))
                        then
                            full.Substring(0, full.Length - 6)
                        else
                            full

                    let nested = f + "+" + simpleNameOf f
                    if here.Contains nested then nested else f

                // A GENERIC member renders with its arity —
                // ``ModuleQueryBus.ask`2``, ``Cmd.none`1`` — and a doc
                // writes the bare name. Stripping it here is load-bearing
                // rather than tidy: without it every generic function in
                // the surface reads as absent, which is a false positive on
                // exactly the composition helpers the docs teach most.
                //
                // Phase 669 — and the GRANDCHILDREN, because an F# DU
                // declared inside a module is reached THROUGH the module.
                // `StopWords.German` is how the language resolves a case of
                // the `Language` DU declared in `module StopWords`, and it
                // is how the compiling block on the same page spells it —
                // but the case renders as `StopWords+Language+German`, two
                // levels down, so a children-only fold reported a correct
                // line as naming a member the surface does not have.
                let members =
                    usable
                    |> List.collect (fun (full, ms) ->
                        let children = nestedByParent.TryFind full |> Option.defaultValue []

                        // Phase 669 — the CASES of a DU nested in this
                        // container are reached through the container.
                        // `StopWords.German` is how F# resolves a case of
                        // the `Language` DU declared in `module StopWords`,
                        // and how the compiling block on the same page
                        // spells it — but the case renders as a property of
                        // `StopWords+Language`, one level down, so a
                        // children-only fold reported a correct line as
                        // naming a member the surface does not have.
                        //
                        // Narrowed to DU CASES rather than every nested
                        // member, by the `IsXxx` companion property the F#
                        // compiler emits for each case. Folding a nested
                        // record's FIELDS into its parent would widen the
                        // accepted set for no rule the language has.
                        let nestedDuCases =
                            children
                            |> List.collect (fun c ->
                                match membersByFull.TryFind(full + "+" + c) with
                                | None -> []
                                | Some names -> names |> List.filter (fun n -> names |> List.contains ("Is" + n)))

                        (ms |> List.map fst) @ children @ nestedDuCases)
                    |> List.map (fun m -> m.Split('`')[0])
                    |> Set.ofList

                kv.Key, (usable |> List.map (fst >> canonical) |> List.distinct, members))
            |> Seq.filter (fun (_, (fulls, _)) -> not fulls.IsEmpty)
            |> Map.ofSeq

        let ownedContainerNames =
            Set.difference (surfaceContainers |> Map.keys |> Set.ofSeq) (Set.union bclSimpleNames docParityAliasNames)

        {|
            RealTypes = realTypes
            RealFullNames = realFullNames
            RealByName = realByName
            BclSimpleNames = bclSimpleNames
            OwnedSimpleNames = ownedSimpleNames
            UnambiguousTypeNames = unambiguousTypeNames
            SurfaceContainers = surfaceContainers
            OwnedContainerNames = ownedContainerNames
        |}

    let apiSurface = lazy (readApiSurface __SOURCE_DIRECTORY__)

    // Arm 1 — dotted, capitalized identifiers. The anchor is the first
    // segment the surface owns, and every segment before it must be
    // capitalized (so a lowercase value's property can never anchor).
    // Phase 669 — anchor at the RIGHTMOST container in the chain, not
    // the leftmost.
    //
    // A fully-qualified reference is namespace segments followed by a
    // container followed by a member: in
    // `ToolUp.Voice.Client.VoiceInput.registerPromptMic` the container
    // is `VoiceInput` and everything before it is qualification.
    // Scanning left-to-right stopped at `Client` — a real container in
    // a different package — and then reported the NAMESPACE segment
    // `VoiceInput` as a member `Client` does not have, on three pages
    // whose prose was correct as written. Rightmost-first reads the
    // chain the way F# does.
    //
    // The leading guard is unchanged and still load-bearing: a chain
    // whose first segment is lower-case is a local value being
    // dereferenced, not a container path, and must never anchor.
    let anchorIn (ownedContainerNames: Set<string>) (chain: string) =
        let segs = chain.Split('.')

        if segs.Length < 2 || not (System.Char.IsUpper(segs[0].[0])) then
            None
        else
            let rec go i =
                if i < 0 then
                    None
                elif ownedContainerNames.Contains segs[i] then
                    Some(segs[i], segs[i + 1])
                else
                    go (i - 1)

            go (segs.Length - 2)
    // A finding's value is the fix it suggests. `ServerConfig` renders
    // ~150 members, so the whole set is unreadable and an alphabetical
    // truncation of it reliably omits the answer — every `with*` helper
    // sorts after every field. Rank by shared trigrams, tie-broken by
    // shared prefix. Prefix alone was tried and measured against the
    // demonstrated-red probe: for a `withStorage` -> `withBlobStorage`
    // rename it offered `withScheduledJob` first, because an INFIX
    // insertion is exactly the case a prefix measure cannot see, and
    // insertion is a common rename shape.
    let nearestTo (wanted: string) (members: Set<string>) =
        let trigrams (s: string) =
            let t = s.ToLowerInvariant()

            if t.Length < 3 then
                Set.singleton t
            else
                set [ for i in 0 .. t.Length - 3 -> t.Substring(i, 3) ]

        let wantedGrams = trigrams wanted

        let sharedPrefix (candidate: string) =
            let n = min wanted.Length candidate.Length

            let rec go i =
                if
                    i < n
                    && System.Char.ToLowerInvariant wanted[i] = System.Char.ToLowerInvariant candidate[i]
                then
                    go (i + 1)
                else
                    i

            go 0

        members
        |> Set.toList
        |> List.filter (fun m -> m <> ".ctor" && not (m.StartsWith "Is" && members.Contains(m.Substring 2)))
        |> List.sortBy (fun m -> -(Set.intersect wantedGrams (trigrams m)).Count, -(sharedPrefix m), m)
        |> List.truncate 10
        |> String.concat ", "

    Target.create "VerifyDocSnippets" (fun _ ->
        // Read from the process argv rather than `p.Context.Arguments`:
        // FAKE's own CLI parser consumes trailing options before the
        // target sees them, so the flag never arrives there.
        let updateBaseline = args |> Array.contains "--update-baseline"
        let repoRoot = __SOURCE_DIRECTORY__
        let projDir = Path.Combine(repoRoot, "docs-snippets")
        let outDir = Path.Combine(projDir, "generated")
        let baselinePath = Path.Combine(projDir, "known-drift.txt")
        let floorPath = Path.Combine(projDir, "corpus-floor.txt")
        let ambientDir = Path.Combine(projDir, "ambient")

        let toSlash (s: string) = s.Replace('\\', '/')

        // Phase 669 — the name-scoped walk over `src/**`. Recursive by
        // hand rather than `SearchOption.AllDirectories` so `bin` / `obj`
        // / `node_modules` are PRUNED rather than enumerated and
        // discarded: an unpruned walk of `src` visits a full
        // `node_modules` tree on every run, and a packed README is copied
        // into `bin/` on its way to the nupkg, so the same file would be
        // counted twice under two different paths.
        let rec walkNamed (dir: string) =
            let here =
                Directory.EnumerateFiles dir
                |> Seq.filter (fun f -> docSnippetSrcFileNames.Contains(Path.GetFileName f))
                |> List.ofSeq

            let below =
                Directory.EnumerateDirectories dir
                |> Seq.filter (fun d -> not (docSnippetPrunedDirs.Contains(Path.GetFileName d)))
                |> Seq.collect walkNamed
                |> List.ofSeq

            here @ below

        let docFiles =
            [
                yield!
                    docSnippetRoots
                    |> List.collect (fun root ->
                        let full = Path.Combine(repoRoot, root)

                        if Directory.Exists full then
                            Directory.EnumerateFiles(full, "*.md", SearchOption.AllDirectories)
                            |> List.ofSeq
                        else
                            [])

                let srcFull = Path.Combine(repoRoot, docSnippetSrcRoot)

                if Directory.Exists srcFull then
                    yield! walkNamed srcFull

                yield!
                    docSnippetLooseFiles
                    |> List.map (fun f -> Path.Combine(repoRoot, f))
                    |> List.filter File.Exists
            ]
            |> List.map (fun f -> f, toSlash (Path.GetRelativePath(repoRoot, f)))
            |> List.filter (fun (_, rel) ->
                docSnippetExcludedTrees |> List.forall (fun t -> not (rel.StartsWith(t + "/"))))
            // A file reachable from two roots (a `README.md` inside the
            // technical-guide tree) is one page, not two.
            |> List.distinctBy snd
            |> List.sortBy snd

        // A fence opens with >= 3 backticks plus an info string, and
        // closes on the first >= as many backticks with no info string.
        let fenceOf (line: string) =
            let t = line.TrimStart(' ')
            let n = t.Length - t.TrimStart('`').Length
            if n >= 3 then Some(n, t.Substring(n).Trim()) else None

        let blocksIn (abs: string, rel: string) =
            let lines = File.ReadAllLines abs
            let acc = ResizeArray<string * int * int * int * string * string list>()
            let mutable i = 0
            let mutable ordinal = 0

            while i < lines.Length do
                match fenceOf lines[i] with
                | Some(n, info) when info <> "" ->
                    let mutable j = i + 1

                    let closes k =
                        match fenceOf lines[k] with
                        | Some(m, "") when m >= n -> true
                        | _ -> false

                    while j < lines.Length && not (closes j) do
                        j <- j + 1

                    ordinal <- ordinal + 1
                    let raw = [ for k in i + 1 .. j - 1 -> lines[k] ]

                    // Phase 669 — strip the block's COMMON leading indent.
                    //
                    // A fence nested inside a Markdown list is indented to
                    // sit under its list item; that indentation is a
                    // MARKDOWN artifact and carries no F# meaning, but it
                    // reaches the compiler as offside structure and every
                    // such block fails to parse. It is not drift and no
                    // skip reason describes it honestly — the block is a
                    // complete, correct program that the extractor handed
                    // over misaligned.
                    //
                    // COMMON indent, not per-line: the block's own internal
                    // structure is exactly what must survive. Blank lines
                    // are excluded from the measurement (a trailing empty
                    // line would otherwise pin it at 0 and change nothing).
                    // Unindented blocks — every block in `docs/**` — measure
                    // 0 and are returned untouched.
                    //
                    // The one cost: a compiler error's COLUMN is now the
                    // dedented column, while its file and LINE — the parts
                    // the `#line` directive carries and the parts a reader
                    // navigates by — stay exact.
                    let indent =
                        match raw |> List.filter (fun l -> l.Trim() <> "") with
                        | [] -> 0
                        | content -> content |> List.map (fun l -> l.Length - l.TrimStart(' ').Length) |> List.min

                    let body =
                        if indent = 0 then
                            raw
                        else
                            raw
                            |> List.map (fun l ->
                                if l.Length >= indent then
                                    l.Substring indent
                                else
                                    l.TrimStart(' '))

                    // rel, ordinal, first content line, closing-fence line, info, body
                    acc.Add(rel, ordinal, i + 2, j, info, body)
                    i <- j + 1
                | _ -> i <- i + 1

            List.ofSeq acc

        let allBlocks = docFiles |> List.collect blocksIn

        let fsharpBlocks =
            allBlocks
            |> List.filter (fun (_, _, _, _, info, _) -> info.Split(' ')[0] = "fsharp")

        // Validate every attribute BEFORE deciding anything, so a typo'd
        // marker fails loudly rather than silently leaving a block in or
        // out of scope.
        let markerErrors =
            fsharpBlocks
            |> List.collect (fun (rel, ord, start, _, info, _) ->
                info.Split(' ')
                |> Array.toList
                |> List.tail
                |> List.filter (fun a -> a <> "")
                |> List.choose (fun attr ->
                    if attr.StartsWith "skip=" then
                        let reason = attr.Substring 5

                        if docSnippetSkipReasons.Contains reason then
                            None
                        else
                            Some(
                                sprintf
                                    "%s:%d (block %d): unknown skip reason '%s'. Allowed: %s"
                                    rel
                                    (start - 1)
                                    ord
                                    reason
                                    (System.String.Join(", ", docSnippetSkipReasons))
                            )
                    else
                        Some(
                            sprintf
                                "%s:%d (block %d): unrecognised fence attribute '%s'. The only attribute is skip=<reason>."
                                rel
                                (start - 1)
                                ord
                                attr
                        )))

        if not markerErrors.IsEmpty then
            for e in markerErrors do
                Trace.traceError e

            failwithf "VerifyDocSnippets: %d invalid fence marker(s) — see above." markerErrors.Length

        let isSkipped (info: string) = info.Contains "skip="

        let inScope =
            fsharpBlocks |> List.filter (fun (_, _, _, _, info, _) -> not (isSkipped info))

        let skippedByReason =
            fsharpBlocks
            |> List.choose (fun (_, _, _, _, info, _) ->
                info.Split(' ')
                |> Array.tryPick (fun a -> if a.StartsWith "skip=" then Some(a.Substring 5) else None))
            |> List.countBy id
            |> List.sortBy fst

        // ---- the shared api-baseline surface universe (hoisted, Phase 670) ----
        let surface = apiSurface.Value
        let realTypes = surface.RealTypes
        let realFullNames = surface.RealFullNames
        let realByName = surface.RealByName
        let bclSimpleNames = surface.BclSimpleNames
        let ownedSimpleNames = surface.OwnedSimpleNames
        let unambiguousTypeNames = surface.UnambiguousTypeNames

        // The public SDK type names a DOC signature mentions. A trailing
        // `//` comment is cut first: `Kind: HealthKind  // Liveness |
        // Readiness` names two DU cases the real member never mentions, and
        // prose is not a declaration.
        let docTypeTokens (raw: string) =
            let cut = raw.IndexOf "//"
            let text = if cut >= 0 then raw.Substring(0, cut) else raw

            rxMatches @"[A-Za-z_][A-Za-z0-9_]*" text
            |> List.filter ownedSimpleNames.Contains
            |> Set.ofList

        // …and the ones a RENDERED member mentions. A token counts only
        // when its full dotted name is a type the surface actually renders,
        // which is what keeps `System.String` out. A nested `Outer+Inner`
        // contributes both halves — a doc writes a module-nested type
        // either way round.
        let realTypeTokens (rendered: string) =
            rxMatches @"[A-Za-z_][A-Za-z0-9_.+`]*" rendered
            |> List.map (fun v -> v.Split('`')[0])
            |> List.filter realFullNames.Contains
            |> List.collect (fun bare -> [ for seg in bare.Split '+' -> simpleNameOf seg ])
            |> Set.ofList

        // Case names of a real DU: the nested `T+Case` types, plus the
        // `IsCase` recognisers. Both are read because either can be absent
        // depending on how the case is compiled; over-collecting only ever
        // ACCEPTS a doc case, so the subset direction stays sound.
        let realUnionCases (full: string) =
            let nested = full + "+"

            let fromNested =
                realFullNames
                |> Seq.filter (fun f -> f.StartsWith nested)
                |> Seq.map (fun f -> f.Substring nested.Length)
                |> Seq.filter (fun n -> n <> "Tags" && not (n.Contains "+"))
                |> List.ofSeq

            let fromRecognisers =
                realTypes
                |> List.tryFind (fun (f, _) -> f = full)
                |> Option.map (fun (_, ms) ->
                    ms
                    |> List.map fst
                    |> List.filter (fun n -> n.StartsWith "Is" && n.Length > 2)
                    |> List.map (fun n -> n.Substring 2))
                |> Option.defaultValue []

            Set.ofList (fromNested @ fromRecognisers)

        // ---- Phase 668: what each block DECLARES ----
        //
        // (rel, ordinal, doc line of the `type` head, type name, kind,
        //  [member name, declared signature]). `kind` decides how the
        //  members are read AND which comparison applies.
        let declsIn (rel: string, _ord: int, start: int, body: string list) =
            let arr = List.toArray body

            let heads =
                arr
                |> Array.mapi (fun i l ->
                    if l.StartsWith " " || l.StartsWith "\t" then
                        None
                    else
                        rxGroups @"^(?:type|and)\s+(?:\[<[^\]]*>\]\s*)?([A-Za-z_][A-Za-z0-9_]*)" l
                        |> Option.map (fun g -> i, g[1]))
                |> Array.choose id

            heads
            |> Array.mapi (fun k (i, name) ->
                let stop =
                    if k + 1 < heads.Length then
                        fst heads[k + 1]
                    else
                        arr.Length

                let seg = arr[i .. stop - 1]

                let matches pattern =
                    seg
                    |> Array.exists (fun l -> System.Text.RegularExpressions.Regex.IsMatch(l, pattern))

                let kind =
                    if matches @"^\s+abstract\s" then
                        "interface"
                    elif matches @"^\s*\|\s*[A-Z]" then
                        "union"
                    elif seg |> Array.exists (fun l -> l.Contains "{") then
                        "record"
                    else
                        "other"

                let members =
                    match kind with
                    | "interface" ->
                        seg
                        |> Array.choose (
                            rxGroups @"^\s+abstract\s+(?:member\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(.+)$"
                        )
                        |> Array.map (fun g -> g[1], g[2])
                    | "record" ->
                        seg
                        |> Array.choose (rxGroups @"^\s+(?:mutable\s+)?([A-Z][A-Za-z0-9_]*)\s*:\s*(.+)$")
                        |> Array.map (fun g -> g[1], g[2])
                    | "union" ->
                        // `| Some x -> …` in a sample body is not a case
                        // declaration; an arrow disqualifies the line.
                        seg
                        |> Array.filter (fun l -> not (l.Contains "->"))
                        |> Array.choose (rxGroups @"^\s*\|\s*([A-Z][A-Za-z0-9_]*)\s*(?:of\b.*)?$")
                        |> Array.map (fun g -> g[1], "")
                    | _ -> [||]

                rel, _ord, start + i, name, kind, List.ofArray members)
            |> List.ofArray

        let docDecls =
            fsharpBlocks
            |> List.collect (fun (rel, ord, start, _, _, body) -> declsIn (rel, ord, start, body))

        let redeclarations =
            docDecls
            |> List.filter (fun (_, _, _, name, _, _) -> unambiguousTypeNames.Contains name)

        let ambiguousRedeclarations =
            docDecls
            |> List.filter (fun (_, _, _, name, _, _) ->
                realByName.ContainsKey name && not (unambiguousTypeNames.Contains name))
            |> List.length

        let notComparable =
            redeclarations
            |> List.filter (fun (_, _, _, _, kind, _) -> kind = "other")
            |> List.length

        // ---- Phase 668: the comparison ----
        let parityFindings =
            redeclarations
            |> List.collect (fun (rel, ord, line, name, kind, members) ->
                let full, realMembers = (realByName |> Map.find name).Head

                let fail (memberName: string) (why: string) (docShape: string) (realShape: string) = [
                    sprintf
                        "%s (block %d, line %d): %s.%s — %s\n      doc  : %s\n      real : %s"
                        rel
                        ord
                        line
                        name
                        memberName
                        why
                        docShape
                        realShape
                ]

                match kind with
                | "union" ->
                    let cases = realUnionCases full

                    members
                    |> List.collect (fun (caseName, _) ->
                        if cases.Contains caseName then
                            []
                        else
                            fail
                                caseName
                                (sprintf "%s has no such case." full)
                                (sprintf "| %s" caseName)
                                (cases |> Set.toList |> List.sort |> String.concat " | "))
                | "interface"
                | "record" ->
                    members
                    |> List.collect (fun (memberName, memberSig) ->
                        let candidates = realMembers |> List.filter (fun (n, _) -> n = memberName)

                        match candidates with
                        | [] ->
                            fail
                                memberName
                                (sprintf "%s has no such member." full)
                                (sprintf "%s: %s" memberName memberSig)
                                (realMembers |> List.map fst |> List.distinct |> List.sort |> String.concat ", ")
                        | _ ->
                            let declared = docTypeTokens memberSig

                            let ok =
                                candidates
                                |> List.exists (fun (_, rendered) -> Set.isSubset declared (realTypeTokens rendered))

                            if ok then
                                []
                            else
                                // Name the surface types the real member does
                                // not carry. Across OVERLOADS that set can be
                                // empty while no single overload is a superset
                                // — each carries some of what the doc names —
                                // so say that instead of printing nothing.
                                let unmatched =
                                    Set.difference
                                        declared
                                        (candidates |> List.map (snd >> realTypeTokens) |> Set.unionMany)

                                let why =
                                    if unmatched.IsEmpty then
                                        sprintf
                                            "no single overload of %s.%s carries all of %s."
                                            full
                                            memberName
                                            (declared |> Set.toList |> List.sort |> String.concat ", ")
                                    else
                                        sprintf
                                            "declared signature names %s, which %s.%s does not."
                                            (unmatched |> Set.toList |> List.sort |> String.concat ", ")
                                            full
                                            memberName

                                fail
                                    memberName
                                    why
                                    (sprintf "%s: %s" memberName memberSig)
                                    (candidates |> List.map snd |> String.concat "  |  "))
                | _ -> [])

        // ---- Phase 672: the name universe a fragment is held to ----
        // Built once by `readApiSurface` above and shared with
        // `VerifySourceCitations`; the reasoning for its shape lives there.
        let surfaceContainers = surface.SurfaceContainers
        let ownedContainerNames = surface.OwnedContainerNames


        // ---- Phase 672: what a fragment says ----
        let stripDocLiterals (line: string) =
            let cut = line.IndexOf "//"
            let text = if cut >= 0 then line.Substring(0, cut) else line

            let noTriple =
                System.Text.RegularExpressions.Regex.Replace(text, @"""""""[\s\S]*?""""""", @"""""")

            System.Text.RegularExpressions.Regex.Replace(noTriple, @"""(\\.|[^""\\])*""", @"""""")

        let declaredNamesIn (body: string list) =
            body
            |> List.collect (fun line ->
                let t = stripDocLiterals line

                [
                    yield!
                        rxGroups @"^\s*(?:type|and)\s+(?:\[<[^\]]*>\]\s*)?([A-Za-z_][A-Za-z0-9_]*)" t
                        |> Option.map (fun g -> g[1])
                        |> Option.toList
                    yield!
                        rxGroups @"^\s*(?:\[<[^\]]*>\]\s*)?module\s+(?:rec\s+)?([A-Za-z_][A-Za-z0-9_.]*)" t
                        |> Option.map (fun g -> simpleNameOf g[1])
                        |> Option.toList
                    yield!
                        rxGroups @"^\s*let\s+(?:mutable\s+|rec\s+|inline\s+)*([A-Z][A-Za-z0-9_]*)" t
                        |> Option.map (fun g -> g[1])
                        |> Option.toList
                ])
            |> Set.ofList

        // Page accumulation, exactly as the compile arm gives `open`: a
        // page introduces a type in its first block and reads it from its
        // fifth, and a reader reads the page top to bottom. The ambient
        // preamble counts for the same reason — it declares what the
        // page's surrounding program would have provided.
        let pageDeclaredNames =
            let ambientNamesFor (rel: string) =
                let path = Path.Combine(ambientDir, rel.Substring(0, rel.Length - 3) + ".fs")

                if File.Exists path then
                    declaredNamesIn (File.ReadAllLines path |> List.ofArray)
                else
                    Set.empty

            fsharpBlocks
            |> List.groupBy (fun (rel, _, _, _, _, _) -> rel)
            |> List.map (fun (rel, blocks) ->
                rel,
                blocks
                |> List.collect (fun (_, _, _, _, _, body) -> declaredNamesIn body |> Set.toList)
                |> Set.ofList
                |> Set.union (ambientNamesFor rel))
            |> Map.ofList

        let fragmentBlocks =
            fsharpBlocks
            |> List.filter (fun (_, _, _, _, info, _) ->
                info.Split(' ') |> Array.exists (fun a -> a = "skip=fragment"))

        // A declaration line carries a NAMESPACE path, not a member access.
        let isDeclarationLine (t: string) =
            System.Text.RegularExpressions.Regex.IsMatch(t, @"^\s*(open|namespace|#r|#load)\b")
            || System.Text.RegularExpressions.Regex.IsMatch(t, @"^\s*(?:\[<[^\]]*>\]\s*)?module\b")

        // Arm 1 — dotted, capitalized identifiers, anchored by `anchorIn`
        // (hoisted to module scope in Phase 670, where its rules are
        // explained — they are the same rules a source comment is held to).
        let anchorOf = anchorIn ownedContainerNames


        // Arm 2 — one brace region is one record construction.
        let labelRx =
            System.Text.RegularExpressions.Regex(@"(?:^|\{|;)\s*([A-Z][A-Za-z0-9_]*)\s*=(?![=>])")

        let recordRegionsIn (body: string list) (startLine: int) =
            let out = ResizeArray<int * Set<string>>()
            let stack = System.Collections.Generic.Stack<int * ResizeArray<string>>()

            let close () =
                let line, labels = stack.Pop()

                if line >= 0 && labels.Count > 0 then
                    out.Add(line, Set.ofSeq labels)

            body
            |> List.iteri (fun i raw ->
                let t = stripDocLiterals raw

                // by the position of the NAME, so a label lands in the
                // region open at that point in the line
                let atPos =
                    labelRx.Matches t
                    |> Seq.map (fun m -> m.Groups[1].Index, m.Groups[1].Value)
                    |> Map.ofSeq

                let mutable c = 0

                while c < t.Length do
                    match atPos.TryFind c with
                    | Some name when stack.Count > 0 -> (snd (stack.Peek())).Add name
                    | _ -> ()

                    match t[c] with
                    | '{' ->
                        if c + 1 < t.Length && t[c + 1] = '|' then
                            // anonymous record — collect and discard
                            stack.Push(-1, ResizeArray())
                            c <- c + 1
                        else
                            stack.Push(startLine + i, ResizeArray())
                    | '}' when stack.Count > 0 -> close ()
                    | _ -> ()

                    c <- c + 1)

            // an unclosed region — the elided `|> ...` tail of a fragment
            // — is still a construction and still checkable
            while stack.Count > 0 do
                close ()

            List.ofSeq out

        let recordAnchorCandidates =
            surfaceContainers
            |> Map.toList
            |> List.filter (fun (_, (fulls, ms)) -> fulls.Length = 1 && ms.Count >= docFragmentMinRecordMatches)
            |> List.map (fun (name, (_, ms)) -> name, ms)


        // (dotted candidates, resolvable, local, out-of-universe, ambiguous)
        let mutable fragDotted = 0
        let mutable fragResolvable = 0
        let mutable fragLocal = 0
        let mutable fragOutside = 0
        let mutable fragAmbiguous = 0
        let mutable fragRecordRegions = 0

        let fragmentFindings =
            fragmentBlocks
            |> List.collect (fun (rel, ord, start, _, _, body) ->
                let declared =
                    Set.union (declaredNamesIn body) (pageDeclaredNames.TryFind rel |> Option.defaultValue Set.empty)

                let dotted =
                    body
                    |> List.mapi (fun i line -> start + i, stripDocLiterals line)
                    |> List.filter (fun (_, t) -> not (isDeclarationLine t))
                    |> List.collect (fun (ln, t) ->
                        System.Text.RegularExpressions.Regex.Matches(
                            t,
                            @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+"
                        )
                        |> Seq.map (fun m -> ln, m.Value)
                        |> List.ofSeq)

                fragDotted <- fragDotted + dotted.Length

                let dottedFindings =
                    dotted
                    |> List.collect (fun (ln, chain) ->
                        match anchorOf chain with
                        | None ->
                            fragOutside <- fragOutside + 1
                            []
                        | Some(root, memberName) when declared.Contains root ->
                            fragLocal <- fragLocal + 1
                            []
                        | Some(root, memberName) ->
                            let fulls, members = surfaceContainers |> Map.find root

                            if fulls.Length > 1 then
                                fragAmbiguous <- fragAmbiguous + 1
                                []
                            elif members.Contains memberName then
                                fragResolvable <- fragResolvable + 1
                                []
                            else
                                [
                                    sprintf
                                        "%s:%d (block %d): %s — %s has no member `%s`.\n      nearest: %s"
                                        rel
                                        ln
                                        ord
                                        chain
                                        root
                                        memberName
                                        (nearestTo memberName members)
                                ])

                let regions = recordRegionsIn body start
                fragRecordRegions <- fragRecordRegions + regions.Length

                let recordFindings =
                    regions
                    |> List.collect (fun (ln, labels) ->
                        if labels.Count < docFragmentMinRecordLabels then
                            []
                        else
                            let scored =
                                recordAnchorCandidates
                                |> List.choose (fun (name, ms) ->
                                    let hit = Set.intersect labels ms |> Set.count

                                    if hit >= docFragmentMinRecordMatches && not (declared.Contains name) then
                                        Some(name, hit, ms)
                                    else
                                        None)

                            match scored with
                            | [] -> []
                            | _ ->
                                let best = scored |> List.map (fun (_, hit, _) -> hit) |> List.max

                                match scored |> List.filter (fun (_, hit, _) -> hit = best) with
                                | [ (name, hit, ms) ] when float hit >= 0.5 * float labels.Count ->
                                    match Set.difference labels ms |> Set.toList |> List.sort with
                                    | [] -> []
                                    | missing ->
                                        missing
                                        |> List.map (fun field ->
                                            sprintf
                                                "%s:%d (block %d): %s has no field `%s` (matched %d of %d labels).\n      nearest: %s"
                                                rel
                                                ln
                                                ord
                                                name
                                                field
                                                hit
                                                labels.Count
                                                (nearestTo field ms))
                                | _ -> [])

                dottedFindings @ recordFindings)

        // ---- ambient context, declared here and never in the docs ----
        let docSnippetPreamble = [
            "open System"
            "open System.Threading.Tasks"
            "open ToolUp.Platform"
            "open ToolUp.Platform.Auth"
            "open ToolUp.Platform.VectorKnowledgeTypes"
            "open DataManagementTypes"
        ]

        // The per-package opens, named once. Phase 669 brought the packed
        // `src/**/README.md` + `TECHNICAL_GUIDE.md` into the walk, and a
        // companion's own README is read in the context of its package for
        // exactly the reason `docs/ai/` is: the page is ABOUT that package,
        // so its blocks assume it without a ceremonial `open` a reader would
        // then copy. Sharing the lists keeps the two entry points from
        // drifting into two different ideas of what "the AI context" is.
        let aiOpens = [
            "open ToolUp.AI"
            "open ToolUp.AI.AICompose"
            "open ToolUp.Platform.AI"
            "open ToolUp.AI.Wire"
        ]

        let ragOpens = [ "open ToolUp.RAG"; "open ToolUp.RAG.RAGCompose" ]
        let kbOpens = [ "open ToolUp.KnowledgeBase"; "open SharedTypes" ]

        let formsOpens = [
            "open ToolUp.Forms"
            "open ToolUp.Forms.FormSchema"
            "open ToolUp.Forms.FormSubmission"
            "open ToolUp.Forms.AggregationTypes"
        ]

        let schedulingOpens = [
            "open ToolUp.Scheduling"
            "open ToolUp.Scheduling.SchedulingTypes"
            "open ToolUp.Scheduling.SchedulingCompose"
        ]

        let docSnippetTreePreamble = [
            "docs/ai/", aiOpens
            "docs/rag/", ragOpens
            "docs/knowledge-base/", kbOpens
            "docs/forms/", formsOpens
            "docs/scheduling/", schedulingOpens
            // Phase 669 — the packed teaching surfaces, same lists.
            "src/ToolUp.AI/", aiOpens
            "src/AI.Samples/", aiOpens
            "src/AIProviders/", aiOpens
            "src/AICookbooks/", aiOpens
            "src/ToolUp.RAG/", ragOpens
            "src/ToolUp.RAG.StaticCorpus.Server/", ragOpens
            "src/ToolUp.KnowledgeBase/", kbOpens
            "src/ToolUp.Forms/", formsOpens
            "src/ToolUp.Scheduling/", schedulingOpens
        ]

        let preambleFor (rel: string) =
            let tree =
                docSnippetTreePreamble
                |> List.tryPick (fun (prefix, opens) -> if rel.StartsWith prefix then Some opens else None)
                |> Option.defaultValue []

            docSnippetPreamble @ tree

        // Layer 3 — the per-page ambient preamble. Returns the ambient
        // file's absolute path (for the `#line` directive that makes an
        // error inside it point AT it) and its lines.
        let ambientFor (rel: string) =
            let path = Path.Combine(ambientDir, rel.Substring(0, rel.Length - 3) + ".fs")

            if File.Exists path then
                Some(path, File.ReadAllLines path |> List.ofArray)
            else
                None

        let ambientPages =
            docFiles |> List.filter (fun (_, rel) -> (ambientFor rel).IsSome) |> List.length

        let moduleNameOf (rel: string) =
            let stripped = rel.Replace(".md", "")

            let cleaned =
                System.String([| for c in stripped -> if System.Char.IsLetterOrDigit c then c else '_' |])

            if System.Char.IsDigit cleaned[0] then
                "d" + cleaned
            else
                cleaned

        let hashOf (body: string list) =
            use sha = System.Security.Cryptography.SHA256.Create()

            let bytes =
                System.Text.Encoding.UTF8.GetBytes(System.String.Join("\n", body |> List.map _.TrimEnd()))

            (sha.ComputeHash bytes)[..3] |> Array.map (sprintf "%02x") |> String.concat ""

        if Directory.Exists outDir then
            Directory.Delete(outDir, true)

        Directory.CreateDirectory outDir |> ignore

        // The blocks a compiler error may be attributed to. SKIPPED blocks
        // are present too, with inScope=false: a page's `open` lines are
        // carried forward to later blocks (see below), so an unresolvable
        // `open` declared in a skipped block would otherwise surface as an
        // unattributable error — and be reported as a harness fault — in
        // every later block on the page. Attributed to its declaring block
        // it is correctly ignored, because that block was already declared
        // uncheckable.
        // rel, ordinal, startLine, endLine, hash, inScope
        let blockTable = ResizeArray<string * int * int * int * string * bool>()

        for (rel, ord, start, endLine, info, body) in fsharpBlocks do
            if isSkipped info then
                blockTable.Add(rel, ord, start, endLine, hashOf body, false)

        for rel, blocks in fsharpBlocks |> List.groupBy (fun (rel, _, _, _, _, _) -> rel) do
            let m = moduleNameOf rel
            let absPath = docFiles |> List.find (fun (_, r) -> r = rel) |> fst

            let lineDirective line =
                sprintf "# %d \"%s\"" line (absPath.Replace("\\", "\\\\"))

            // (open text, the doc line it was declared on)
            let carried = ResizeArray<string * int>()

            for (_, ord, start, endLine, info, body) in blocks |> List.sortBy (fun (_, o, _, _, _, _) -> o) do
                if not (isSkipped info) then
                    let sb = System.Text.StringBuilder()

                    sb.AppendLine(sprintf "module ToolUp.DocSnippets.Generated.%s_B%02d" m ord)
                    |> ignore

                    for o in preambleFor rel do
                        sb.AppendLine o |> ignore

                    // The page's ambient preamble, under its OWN line
                    // directive: an error in it names the ambient file,
                    // does not match the `.md(` attribution probe, and
                    // is therefore reported as the harness fault it is
                    // rather than absorbed as drift in some block that
                    // merely inherited it.
                    match ambientFor rel with
                    | Some(ambientPath, ambientLines) ->
                        sb.AppendLine(sprintf "# 1 \"%s\"" (ambientPath.Replace("\\", "\\\\")))
                        |> ignore

                        for l in ambientLines do
                            sb.AppendLine l |> ignore
                    | None -> ()

                    // Each carried `open` is stamped with the line it was
                    // written on, so an unresolvable one is reported against
                    // the block that DECLARED it rather than every block that
                    // inherits it.
                    let declaresOpen (o: string) =
                        // Compare on the same stripped form the carry uses, so
                        // a block that writes `open X // why` is recognised as
                        // declaring `open X` and does not get a duplicate.
                        body
                        |> List.exists (fun l ->
                            let t = l.Trim()
                            let cut = t.IndexOf "//"
                            (if cut >= 0 then t.Substring(0, cut) else t).TrimEnd() = o)

                    for o, srcLine in Seq.distinctBy fst carried do
                        if not (declaresOpen o) then
                            sb.AppendLine(lineDirective srcLine) |> ignore
                            sb.AppendLine o |> ignore

                    // Everything after this directive is attributed to the
                    // markdown file — this is what makes the compiler name
                    // the doc rather than a generated artefact.
                    sb.AppendLine(lineDirective start) |> ignore

                    for l in body do
                        sb.AppendLine l |> ignore

                    File.WriteAllText(Path.Combine(outDir, sprintf "%s_B%02d.fs" m ord), sb.ToString())
                    blockTable.Add(rel, ord, start, endLine, hashOf body, true)

                body
                |> List.iteri (fun i l ->
                    let t = l.Trim()

                    // A trailing comment is STRIPPED, not disqualifying. The
                    // rule used to drop any `open` line containing `//`,
                    // which silently refused to carry the very lines a doc
                    // is most likely to annotate — `open Fake.Core.TargetOperators
                    // // brings the ==> operator` was declared, explained, and
                    // then not carried, so the next block on the page failed
                    // on `==>` with no hint that the open above it was the
                    // reason. A line that is ITSELF commented out never
                    // reaches here: it starts with `//`, not `open `.
                    if t.StartsWith "open " then
                        let cut = t.IndexOf "//"
                        let text = (if cut >= 0 then t.Substring(0, cut) else t).TrimEnd()

                        if text.Length > 5 then
                            carried.Add(text, start + i))

        let checkedCount =
            blockTable |> Seq.filter (fun (_, _, _, _, _, ok) -> ok) |> Seq.length

        Trace.tracefn
            "▶ VerifyDocSnippets (1/2): extracted %d compiled block(s) (+%d skipped) from %d file(s)"
            checkedCount
            (blockTable.Count - checkedCount)
            docFiles.Length

        // ---- the self-ratcheting corpus floor (high-water mark) ----
        let corpusMark =
            if File.Exists floorPath then
                File.ReadAllLines floorPath
                |> Array.map _.Trim()
                |> Array.filter (fun l -> l <> "" && not (l.StartsWith "#"))
                |> Array.tryPick (fun l ->
                    match System.Int32.TryParse l with
                    | true, n -> Some n
                    | _ -> None)
                |> Option.defaultValue docSnippetFloorSeed
            else
                docSnippetFloorSeed

        let writeCorpusMark (n: int) =
            // Rewrite the NUMBER, keep every comment line exactly as it is.
            // A deliberate shrink is a hand edit whose whole value is the
            // argument written beside it; regenerating the file wholesale on
            // the next growth would erase that argument — turning the one
            // motion this guard tries to make expensive back into a silent
            // one, a release or two later.
            let existing =
                if File.Exists floorPath then
                    File.ReadAllLines floorPath |> Array.toList
                else
                    []

            let rewritten =
                if
                    existing
                    |> List.exists (fun l -> l.Trim() <> "" && not (l.TrimStart().StartsWith "#"))
                then
                    existing
                    |> List.map (fun l ->
                        if l.Trim() <> "" && not (l.TrimStart().StartsWith "#") then
                            string n
                        else
                            l)
                    |> Some
                else
                    None

            let lines = [
                "# The compiled-doc-snippet HIGH-WATER MARK, asserted by"
                "# `dotnet run --project Build.fsproj -- VerifyDocSnippets`."
                "#"
                "# The gate FAILS when fewer blocks compile than this. Growth"
                "# rewrites the number in place (review it in your diff like any"
                "# other generated line); a genuine, argued shrink — a tree"
                "# exclusion, a page deleted — is a HAND edit, so the decision"
                "# is visible to a reviewer rather than absorbed by a flag."
                ""
                string n
            ]

            // LF explicitly — `.gitattributes` pins the repo to LF, and
            // WriteAllLines would make every regeneration on Windows a
            // whole-file diff on every other clone.
            File.WriteAllText(floorPath, System.String.Join("\n", rewritten |> Option.defaultValue lines) + "\n")

        if checkedCount < corpusMark then
            failwithf
                "VerifyDocSnippets: only %d block(s) compile, below the recorded high-water mark of %d (docs-snippets/corpus-floor.txt). Blocks that used to be checked no longer are — skip-marked, deleted, or moved into an excluded tree. Restore them, or, if the loss is deliberate and argued, lower the mark BY HAND in the same commit so the decision is in the diff."
                checkedCount
                corpusMark

        if checkedCount > corpusMark then
            writeCorpusMark checkedCount

            Trace.tracefn
                "VerifyDocSnippets: corpus grew %d -> %d; advanced the high-water mark in docs-snippets/corpus-floor.txt. Include that one-line change in your commit."
                corpusMark
                checkedCount

        Trace.tracefn "▶ VerifyDocSnippets (2/2): compiling against the real SDK"

        let result =
            CreateProcess.fromRawCommand "dotnet" [
                "build"
                "docs-snippets/ToolUp.DocSnippets.fsproj"
                "--nologo"
                "-v"
                "quiet"
            ]
            |> CreateProcess.withWorkingDirectory repoRoot
            |> CreateProcess.redirectOutput
            |> Proc.run

        let output = result.Result.Output + result.Result.Error

        // Attribute each reported error to the block whose line range
        // covers it. `+ 1` because an unterminated construct is reported
        // at the line PAST the block's last one.
        let errorLines =
            output.Split('\n')
            |> Array.map _.Trim()
            |> Array.filter (fun l -> l.Contains ": error ")
            |> Array.distinct

        let attribute (line: string) =
            let i = line.LastIndexOf ".md("

            if i < 0 then
                None
            else
                let rest = line.Substring(i + 4)

                match System.Int32.TryParse(rest.Substring(0, max 0 (rest.IndexOf ','))) with
                | false, _ -> None
                | true, lineNo ->
                    let rel = toSlash (line.Substring(0, i + 3))

                    blockTable
                    |> Seq.tryFind (fun (r, _, s, e, _, _) -> rel.EndsWith r && lineNo >= s && lineNo <= e + 1)
                    |> Option.map (fun (r, ord, s, _, h, ok) -> ((r, ord, s, h), line, ok))

        // Errors landing in a SKIPPED block are dropped: that block was
        // declared uncheckable, and the only way one of its lines reaches
        // the compiler is as an `open` carried onto a later block.
        let attributed =
            errorLines
            |> Array.choose attribute
            |> Array.filter (fun (_, _, ok) -> ok)
            |> Array.map (fun (k, l, _) -> k, l)

        // …but they still fail the BUILD, and that mattered more than the
        // original design allowed for. The final guard below reads a
        // non-zero exit with nothing failing as an unexplained harness
        // fault — which meant the target could never report success while
        // any skipped block declared an unresolvable `open`. That state
        // was invisible for as long as the baseline was non-empty, because
        // the run always failed earlier; it surfaced the moment the
        // baseline reached zero.
        //
        // Counted here rather than merely tolerated. An unresolvable
        // `open` in a `skip=fragment` block IS rot — the gate simply
        // cannot act on it, because the block declared itself
        // uncheckable. That makes the number the most direct evidence
        // available of drift inside the blind spot, so it belongs in the
        // summary beside the skip counts rather than in a silent filter.
        let inSkipped =
            errorLines
            |> Array.choose attribute
            |> Array.filter (fun (_, _, ok) -> not ok)
            |> Array.map (fun ((r, o, _, _), _, _) -> r, o)
            |> Array.distinct

        let unattributed = errorLines |> Array.filter (attribute >> Option.isNone)

        // An error the harness cannot pin to a block is a harness fault
        // (a broken preamble, a missing reference) and must never be
        // absorbed as doc drift.
        if unattributed.Length > 0 then
            for l in unattributed |> Array.truncate 20 do
                Trace.traceError l

            failwithf
                "VerifyDocSnippets: %d compiler error(s) could not be attributed to a documentation block. That is a fault in the harness (preamble, project references, or the generated project), not drift in the docs."
                unattributed.Length

        let failing =
            attributed
            |> Array.groupBy fst
            |> Array.map (fun (k, es) -> k, es |> Array.map snd)
            |> Array.sortBy (fun ((r, o, _, _), _) -> r, o)

        // The baseline key is the FULL triple `path#ordinal hash`, and
        // every part of it earns its place. Path alone cannot key a page
        // with several failing blocks. Hash alone is not unique ACROSS
        // files — identical illustrative blocks legitimately share one
        // (`8464b26d` sat in both `knowledge-base/concepts.md` and
        // `rag/api-reference.md`), and a hash-only prune during the
        // 2026-08-21 burn-down duly deleted the wrong file's line, which
        // only the next full run caught. Ordinal disambiguates two
        // identical blocks on the SAME page, which path+hash cannot.
        //
        // It is also the exact text of the baseline line's first two
        // fields, so "the key" and "the line to delete" are the same
        // thing a reader is looking at.
        let keyOf (rel: string) (ord: int) (hash: string) = sprintf "%s#%d %s" rel ord hash

        let failingKeys =
            failing |> Array.map (fun ((r, o, _, h), _) -> keyOf r o h) |> Set.ofArray

        let writeBaseline () =
            let lines = [
                "# Documentation snippets that name an API the SDK does not have."
                "#"
                "# EMPTY IS THE ENFORCED STATE. Any entry below fails"
                "# `dotnet run --project Build.fsproj -- VerifyDocSnippets`."
                "#"
                "# This list was a migration device. When the gate landed it held 231"
                "# entries — blocks that predated it and had already rotted — and it was a"
                "# ratchet that could only shrink. It reached zero, so the ratchet has"
                "# nothing left to hold: a block that does not compile is now simply a"
                "# failure, and the fix is the snippet, against the current SDK surface."
                "#"
                "# `--update-baseline` rewrites this file from a run and remains the"
                "# documented escape for a wholesale re-measurement. It is not a way of"
                "# going green: whatever it writes here still fails the gate."
                "#"
                "# Each line, when one exists: <content-hash> <doc path>#<block> — <first error>"
                "# The KEY is the full triple `<doc path>#<block> <content-hash>` — never the"
                "# hash alone, which identical blocks in different files share."
                ""
                for (rel, ord, start, h), errs in failing do
                    let first = if errs.Length > 0 then errs[0] else ""

                    // Keep the message, drop MSBuild's trailing
                    // ` [<absolute path>.fsproj]` — a machine-local path in
                    // a tracked file is churn on every other clone — and
                    // flatten control characters to spaces. FSC embeds raw
                    // 0x1D separators in its "Maybe you want one of the
                    // following" hints; a tracked file carrying those reads
                    // as BINARY to git's diff heuristics and to `file`,
                    // which would quietly opt this file out of the repo's
                    // LF normalisation.
                    let msg =
                        let i = first.IndexOf ": error "
                        let m = if i >= 0 then first.Substring(i + 2) else first
                        let j = m.LastIndexOf " ["
                        let trimmed = if j >= 0 && m.EndsWith "]" then m.Substring(0, j) else m

                        let flattened =
                            System.String([| for c in trimmed -> if System.Char.IsControl c then ' ' else c |])

                        System.Text.RegularExpressions.Regex.Replace(flattened, @"\s+", " ").Trim()

                    sprintf "%s %s#%d (line %d) — %s" h rel ord start msg
            ]

            // LF explicitly, not `WriteAllLines`: this file is tracked and
            // `.gitattributes` pins the repo to LF, so writing the platform
            // newline would make every regeneration on Windows a whole-file
            // diff on every other clone.
            File.WriteAllText(baselinePath, System.String.Join("\n", lines) + "\n")
            Trace.tracefn "VerifyDocSnippets: wrote %d baseline entries to %s" failing.Length baselinePath

        let runChecks () =
            let baseline =
                if File.Exists baselinePath then
                    File.ReadAllLines baselinePath
                    |> Array.filter (fun l -> l.Trim() <> "" && not (l.StartsWith "#"))
                    |> Array.choose (fun l ->
                        // `<hash> <path>#<ordinal> (line N) — <error>`:
                        // fields 2 and 1 ARE the key, in that order.
                        let parts = l.Split(' ')

                        if parts.Length >= 2 && parts[1].Contains "#" then
                            Some(parts[1] + " " + parts[0], l)
                        else
                            None)
                    |> Map.ofArray
                else
                    Map.empty

            let baselineKeys = baseline |> Map.keys |> Set.ofSeq

            let newFailures =
                failing
                |> Array.filter (fun ((r, o, _, h), _) -> not (baselineKeys.Contains(keyOf r o h)))

            let fixedButListed = baselineKeys - failingKeys
            let passing = checkedCount - failing.Length

            Trace.tracefn ""
            Trace.tracefn "VerifyDocSnippets summary:"
            Trace.tracefn "  blocks compiled : %d (high-water mark %d)" checkedCount corpusMark
            Trace.tracefn "  passing         : %d" passing
            Trace.tracefn "  known drift     : %d (docs-snippets/known-drift.txt)" baselineKeys.Count
            Trace.tracefn "  new failures    : %d" newFailures.Length
            Trace.tracefn "  fixed-but-listed: %d" fixedButListed.Count

            // The unchecked pool, printed as a number every run. A skip
            // marker is honest bookkeeping, but the blocks behind it are
            // the target's blind spot — and a blind spot nobody measures
            // is one that grows. `skip=fragment` in particular is the
            // one an ambient preamble can retire (see the header), so
            // its count is the standing size of that work.
            for reason, n in skippedByReason do
                Trace.tracefn "  skip=%-12s %d" reason n

            Trace.tracefn "  ambient pages   : %d (docs-snippets/ambient/)" ambientPages

            // Phase 668 — the redeclaration census. A block that
            // redeclares a public SDK type is invisible to the compile
            // gate above (the local declaration shadows the real type), so
            // the size of that pool belongs on the summary for the same
            // reason the skip counts do: a blind spot nobody measures is
            // one that grows. The two qualifiers are the check's own
            // honest limits, not footnotes — an ambiguous name cannot be
            // resolved to one type, and a `type X = …` with no members
            // read has nothing to compare.
            Trace.tracefn
                "  redeclared types: %d in %d block(s) — %d compared, %d not comparable, %d ambiguous"
                redeclarations.Length
                (redeclarations
                 |> List.map (fun (rel, ord, _, _, _, _) -> rel, ord)
                 |> List.distinct
                 |> List.length)
                (redeclarations.Length - notComparable)
                notComparable
                ambiguousRedeclarations

            // Phase 672 — the fragment census. It sits beside the skip
            // counts because it MEASURES one of them: `skip=fragment` is
            // the largest unchecked pool, and this line says how much of
            // it a compile-free existence check can still see. The four
            // classifications are the allow-shape rule made countable —
            // `outside` is the placeholder / vendor / BCL traffic the lint
            // deliberately says nothing about, and a collapse in
            // `resolved` against a steady `checked` is the tell that the
            // universe or the extractor has broken.
            //
            // `fragments walked` is also the RETIREMENT reading (672.C):
            // this whole check deletes when Phase 660.B's conversion work
            // brings that number to its landing floor.
            Trace.tracefn
                "  fragment symbols: %d fragment(s) walked — %d identifier(s) checked, %d resolved, %d local, %d outside, %d ambiguous; %d record region(s)"
                fragmentBlocks.Length
                fragDotted
                fragResolvable
                fragLocal
                fragOutside
                fragAmbiguous
                fragRecordRegions

            // A WATCHLIST, not a defect count. An `open` of a deliberately
            // fictional vendor namespace is legitimate in an illustrative
            // fragment; an `open` of a real SDK namespace that has since
            // moved is rot the gate cannot act on, because the block
            // declared itself uncheckable. Only reading them tells you
            // which — and this line is the only place either is visible.
            if inSkipped.Length > 0 then
                Trace.tracefn "  unresolved opens: %d skipped block(s) — illustrative, or moved?" inSkipped.Length

                for rel, ord in inSkipped |> Array.sort do
                    Trace.tracefn "                    %s#%d" rel ord

            if newFailures.Length > 0 then
                Trace.tracefn ""

                for (rel, ord, start, _), errs in newFailures do
                    Trace.traceError (sprintf "%s — block %d (opens at line %d):" rel ord (start - 1))

                    for e in errs |> Array.truncate 8 do
                        Trace.traceError ("    " + e)

                failwithf
                    "VerifyDocSnippets: %d documentation block(s) name an API the SDK does not have. Fix the snippet against the current surface; do NOT add it to known-drift.txt — that list may only shrink. If the block genuinely cannot be compiled (an .fsi-shaped listing, an elided excerpt, a deliberate anti-pattern), mark its fence with a skip reason instead."
                    newFailures.Length

            if fixedButListed.Count > 0 then
                Trace.tracefn ""
                Trace.tracefn "Delete these lines from docs-snippets/known-drift.txt, verbatim:"

                for k in fixedButListed |> Seq.sort do
                    Trace.traceError (baseline |> Map.find k)

                failwithf
                    "VerifyDocSnippets: %d baseline entr(ies) in docs-snippets/known-drift.txt now compile. Delete the FULL lines printed above — match on path, ordinal AND hash, never on the hash alone: identical illustrative blocks in different files share a hash, and a hash-only prune deletes the wrong file's entry silently. The ratchet only holds if a fixed snippet is removed from the list."
                    fixedButListed.Count

            // Phase 668 — the parity failures. Reported AFTER the compile
            // arms above, because a block that does not compile is the
            // more urgent diagnosis, and BEFORE the baseline-zero check,
            // because a stale redeclaration is a concrete fix while that
            // one is a policy statement. Zero is the enforced state and
            // there is no baseline: this check landed with its corpus
            // burnt down, so an entry has never been a tolerated state and
            // must not become one.
            if not parityFindings.IsEmpty then
                Trace.tracefn ""

                for f in parityFindings do
                    Trace.traceError ("    " + f)

                failwithf
                    "VerifyDocSnippets: %d documentation block(s) REDECLARE a public SDK type with a member the surface does not have. A redeclaration shadows the real type, so the compile arm above cannot see this — that is why the check exists. Fix the snippet against api-baselines/<assembly>.approved.txt (the rendered public surface). Showing FEWER members than the SDK has is fine and needs no marker; showing a member it does not have is not."
                    parityFindings.Length

            // Phase 672 — the fragment findings. Last of the three
            // compile-independent arms, and reported last because it is
            // the weakest claim of the three: it asserts only that a name
            // exists. Zero is the enforced state and there is no baseline
            // — this check landed with its corpus burnt down, so an entry
            // has never been a tolerated state and must not become one.
            if not fragmentFindings.IsEmpty then
                Trace.tracefn ""

                for f in fragmentFindings do
                    Trace.traceError ("    " + f)

                failwithf
                    "VerifyDocSnippets: %d finding(s) — a `skip=fragment` block names an SDK symbol the public surface does not have. A fragment is exempt from COMPILATION, not from being true — the marker says the block cannot be compiled, never that its API names stopped mattering. Fix the snippet against api-baselines/<assembly>.approved.txt. If the name is a placeholder or a local of the surrounding program the page does not show, it must not be spelled like an SDK container: rename it, or give the page a docs-snippets/ambient/ preamble and drop the skip marker entirely, which is the better fix — a block under the compile arm needs no lint at all. See docs-snippets/README.md."
                    fragmentFindings.Length

            // The baseline reached zero, so EMPTY is now the enforced state.
            //
            // While it held entries, the ratchet's two directions were the
            // whole mechanism: a new failure could not land, and a fixed
            // entry had to be deleted. At zero the first direction covers
            // everything — any failing block is a new failure — and the
            // second has nothing to act on. What is left is the risk the
            // ratchet was always shaped around: that the list starts
            // growing again, one plausible line at a time, and the gate
            // reports "0 new failures" while drift accumulates in a file
            // nobody re-reads.
            //
            // So an entry is a defect rather than a state. This check runs
            // AFTER the two above, so a run that both fails and lists gets
            // the specific diagnosis first.
            if baselineKeys.Count > 0 then
                Trace.tracefn ""

                for k in baselineKeys |> Seq.sort do
                    Trace.traceError (baseline |> Map.find k)

                failwithf
                    "VerifyDocSnippets: docs-snippets/known-drift.txt holds %d entr(ies), and empty is the enforced state. The baseline was a migration device for the drift that predated this gate; it reached zero, so a block that does not compile is a failure to fix, not a line to record. Fix the snippet against the current SDK surface, or — if the block genuinely cannot be compiled — mark its fence with a skip reason. `--update-baseline` remains the escape for a wholesale re-measurement, but what it writes still fails here."
                    baselineKeys.Count

            // An unexplained non-zero exit — no failing block AND no error
            // absorbed by a skipped one — is a harness fault: a restore
            // failure, an MSBuild-level error, anything that produced no
            // parseable `file(line,col): error` at all. That is what this
            // guard is for. It must NOT fire on the explained case, or the
            // target can never go green (see `inSkipped` above).
            if result.ExitCode <> 0 && failing.Length = 0 && inSkipped.Length = 0 then
                printfn "%s" output

                failwithf
                    "VerifyDocSnippets: the snippet project failed to build for a reason not attributable to any block (exit %d)."
                    result.ExitCode

            Trace.tracefn ""

            Trace.tracefn
                "VerifyDocSnippets: OK — %d block(s) compile, %d known-drift held at the ratchet."
                passing
                baselineKeys.Count

        if updateBaseline then writeBaseline () else runChecks ())

    // ---- Phase 670: comment-cited API resolution ----
    //
    // Everything above gates DOCS. A comment in `src/**/*.fs` is read with
    // more trust than a doc page and checked by nothing at all: it ships
    // inside the source it describes, so a reader — human or agent — takes
    // it as the local authority and follows it without a second thought.
    // Phase 660's burn-down surfaced the resulting class:
    // `ServerApp.withEntityStore` named in two comments and a migration doc
    // when the helper is `withEntity<'T>`; `ServerApp.withEntities` cited
    // where nothing of that name has ever existed;
    // `RedisNotificationChannelValidator.create` in three comments for a
    // module called `RedisValidator`; and one pointer comment reading
    // `UserSession.fs:342` copied verbatim into fourteen files while the
    // binding it points at sat four hundred lines further down.
    //
    // The check is deliberately NARROWER than the sweep that preceded it,
    // and the boundary is mechanical checkability rather than importance:
    //
    //   * ARM 1 — a `file.fs:NNN` pointer whose file RESOLVES. The claim is
    //     that the file still has that many lines, and — where the same
    //     comment names a binding as `File.member` — that the member's name
    //     still occurs near the cited line. A citation naming a path this
    //     repo does not hold is UNKNOWN, not wrong: a comment may legitimately
    //     point into a consumer's tree. The one exception is a path that
    //     begins `src/`, which can only mean this repo, so an unresolvable
    //     one is a finding.
    //
    //   * ARM 2 — a BACKTICK-QUALIFIED `Container.member` citation, resolved
    //     against the same api-baseline universe Phase 672 holds a fragment
    //     to, through the same `anchorIn`. Backticks are the whole
    //     false-positive budget: comment prose is full of dotted things
    //     (`ctx.Request.Path`, a sentence's `e.g.`, a URL), and a bare-token
    //     scan of it would need an allow-list, which this repo has twice
    //     argued against. A name a writer chose to mark as code is a name the
    //     writer is claiming exists.
    //
    // What stays OUT, on purpose: bare (un-backticked) API prose, a
    // citation of a private helper the public surface does not render (the
    // anchor rule makes those silent by construction — the lint speaks only
    // about names the surface OWNS), whether a comment is TRUE, and whether
    // a public member has a comment at all. That last one is Phase 261's,
    // and is a different question: 261 owns PRESENCE, this owns ACCURACY.
    // A noisy lint is a disabled lint, which is worth more than the extra
    // findings a fuzzier rule would buy.
    //
    // ARM 1's CORPUS IS ZERO AT LANDING, and that is the intended end
    // state rather than a reason to delete it. The sweep that shipped
    // with this check converted all twenty surviving `file.fs:NNN`
    // pointers to name or file citations, because a line number is the
    // one citation shape that cannot be made durable — it rots on any
    // edit above it and rots SILENTLY, since the file still exists and
    // the line still has content. So the arm is prospective: it holds
    // the first pointer someone writes to the two claims it can check,
    // and the census line says how many exist. A zero there is the rule
    // in CONTRIBUTING.md being kept, not a check with nothing to do.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifySourceCitations`
    let sourceCitationWindow = 15

    Target.create "VerifySourceCitations" (fun _ ->
        let repoRoot = __SOURCE_DIRECTORY__
        let toSlash (s: string) = s.Replace('\\', '/')

        let rec walkSources (dir: string) = seq {
            for d in Directory.EnumerateDirectories dir do
                let name = Path.GetFileName d

                if name <> "bin" && name <> "obj" && name <> "output" && name <> "node_modules" then
                    yield! walkSources d

            yield! Directory.EnumerateFiles(dir, "*.fs")
        }

        let sources =
            walkSources (Path.Combine(repoRoot, "src"))
            |> Seq.map (fun f -> toSlash (Path.GetRelativePath(repoRoot, f)), File.ReadAllLines f)
            |> List.ofSeq

        let byRelPath = sources |> Map.ofList

        let byBaseName =
            sources |> List.groupBy (fun (rel, _) -> Path.GetFileName rel) |> Map.ofList

        // A line's COMMENT TEXT, or nothing. String literals are erased
        // FIRST, in that order deliberately: a `"http://…"` or a
        // `"(* not a comment *)"` in code must never be read as prose, and
        // cutting at `//` before erasing strings gets exactly that wrong.
        // `(*)` is the multiplication operator, not a block opener.
        let commentLinesOf (lines: string[]) =
            let out = ResizeArray<int * string>()
            let mutable inBlock = false

            lines
            |> Array.iteri (fun i raw ->
                let noStr =
                    System.Text.RegularExpressions.Regex.Replace(
                        System.Text.RegularExpressions.Regex.Replace(raw, @"""""""[\s\S]*?""""""", @""""""),
                        @"""(\\.|[^""\\])*""",
                        @""""""
                    )

                let sb = System.Text.StringBuilder()
                let mutable c = 0
                let mutable lineDone = false

                while not lineDone && c < noStr.Length do
                    if inBlock then
                        if c + 1 < noStr.Length && noStr[c] = '*' && noStr[c + 1] = ')' then
                            inBlock <- false
                            c <- c + 2
                        else
                            sb.Append noStr[c] |> ignore
                            c <- c + 1
                    elif c + 1 < noStr.Length && noStr[c] = '/' && noStr[c + 1] = '/' then
                        sb.Append(noStr.Substring(c + 2)) |> ignore
                        lineDone <- true
                    elif
                        c + 1 < noStr.Length
                        && noStr[c] = '('
                        && noStr[c + 1] = '*'
                        && not (c + 2 < noStr.Length && noStr[c + 2] = ')')
                    then
                        inBlock <- true
                        c <- c + 2
                    else
                        c <- c + 1

                if sb.Length > 0 then
                    out.Add(i + 1, sb.ToString()))

            List.ofSeq out

        let pointerRx =
            System.Text.RegularExpressions.Regex(
                @"(?<path>[A-Za-z0-9_./\\+-]*[A-Za-z0-9_])\.fs:(?<from>\d{1,6})(?:\s*[-–—]\s*(?<to>\d{1,6}))?"
            )

        let namedBindingRx =
            System.Text.RegularExpressions.Regex(@"`([A-Z][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)`")

        let citationRx =
            System.Text.RegularExpressions.Regex(@"`([A-Z][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+)`")

        let surfaceContainers = apiSurface.Value.SurfaceContainers
        let anchorOf = anchorIn apiSurface.Value.OwnedContainerNames

        // ---- what the REPO declares, over and above what it publishes ----
        //
        // Two indexes, and both exist to answer the same objection: the
        // api-baseline universe renders a PUBLIC SURFACE, while a source
        // comment is written by someone who can see the whole module and
        // legitimately cites what the surface does not carry.
        //
        //   * `sourceDecls` — every name the tree declares, attributed to
        //     its enclosing container by indentation. This is what makes a
        //     `private` helper (`RAGCompose.makeVectorisationHook`), an
        //     interface implementation reached through the implementing
        //     type (`LocalFileStorage.List`), and a test-only binding
        //     resolve instead of reading as absent. Attribution is to EVERY
        //     enclosing container rather than the innermost, deliberately:
        //     over-accepting only ever passes a comment, so the subset
        //     direction stays sound, and an attribution slip then costs a
        //     missed finding rather than a false one.
        //
        //   * `namespacePairs` — parent/child segment pairs, from the
        //     baseline full names AND from every `namespace` and `open` in
        //     the tree. A package path is not a member access, and without
        //     this `Fable.SimpleJson` reads as "Fable has no member
        //     SimpleJson" nineteen times over. Taking the pairs from `open`
        //     is what makes the check know about VENDOR namespaces at all:
        //     a repo that opens `Google.Api.Gax` has told us `Api.Gax` is a
        //     path, and nothing else in the tree ever will.
        //
        // Both are unions with the surface, never replacements for it, and
        // both are rebuilt from source on every run — there is no second
        // baseline to drift, which this repo has now three times declined
        // to introduce.
        let containerHeadRx =
            System.Text.RegularExpressions.Regex(
                @"^(\s*)(?:\[<[^\]]*>\]\s*)*(module|type)\s+(?:rec\s+|private\s+|internal\s+|public\s+)*(?:\[<[^\]]*>\]\s*)*([A-Za-z_][A-Za-z0-9_.']*)"
            )

        let namespaceRx =
            System.Text.RegularExpressions.Regex(
                @"^\s*(?:namespace|open)\s+(?:rec\s+|global\.)?([A-Za-z_][A-Za-z0-9_.]*)"
            )

        // The modifier run is a REPEATED alternation rather than a fixed
        // order, because F# permits several and the orders differ:
        // `member inline this.addCellRange`, `member private this.IsLastOwner`,
        // `static member val internal Instance`, `let mutable private state`.
        // An ordered pattern captured the FIRST modifier as the name — so
        // every extension member on `IGridApi` recorded as `inline`, and the
        // four correct citations of them read as absent.
        let declRx =
            System.Text.RegularExpressions.Regex(
                @"^(\s*)(?:\[<[^\]]*>\]\s*)*(?:let|and|member|abstract|static|override|default|val)\b(?:\s+(?:inline|mutable|rec|private|internal|public|val|member|static))*\s+(?:[A-Za-z_][A-Za-z0-9_]*\.)?([A-Za-z_][A-Za-z0-9_']*)"
            )

        let caseOrFieldRx =
            System.Text.RegularExpressions.Regex(@"^(\s*)(?:\|\s*)?([A-Z][A-Za-z0-9_']*)\s*(?::|of\b|$)")

        let sourceDeclIndex, namespacePairIndex, sameUnitIndex =
            let decls =
                System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>()

            let pairs = System.Collections.Generic.HashSet<string * string>()

            // container simple name -> the files declaring it; and, per file,
            // everything that file declares. The pair answers a THIRD
            // question the two indexes above cannot: is the cited name at
            // least in the same compilation unit as the container it is
            // hung off? `BlobShareTokenStore.resolveSigningKey` names a
            // `let private` helper beside `type BlobShareTokenStore`, and
            // `EventStoreAuditLog.serialise` does the same — attributing
            // those to their nearest enclosing MODULE, which is what
            // indentation gives, files them under the wrong name and reads
            // them as absent. They are neither members nor lies; a comment
            // is prose, and "the helper next to that type" is how prose
            // refers to one. Same unit is the honest bound: it is the scope
            // a reader can actually check by opening the file.
            let containerFiles =
                System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<int>>()

            let fileDecls = ResizeArray<System.Collections.Generic.HashSet<string>>()

            let addDecl (container: string) (name: string) =
                match decls.TryGetValue container with
                | true, s -> s.Add name |> ignore
                | _ ->
                    let s = System.Collections.Generic.HashSet<string>()
                    s.Add name |> ignore
                    decls[container] <- s

            let addPath (path: string) =
                let segs = path.Split('.')

                for i in 0 .. segs.Length - 2 do
                    pairs.Add(segs[i], segs[i + 1]) |> ignore

            for full, _ in apiSurface.Value.RealTypes do
                addPath (full.Split('+')[0])

            sources
            |> List.iteri (fun fileIx (_, lines) ->
                let here = System.Collections.Generic.HashSet<string>()
                fileDecls.Add here

                let addContainerFile (name: string) =
                    match containerFiles.TryGetValue name with
                    | true, s -> s.Add fileIx |> ignore
                    | _ ->
                        let s = System.Collections.Generic.HashSet<int>()
                        s.Add fileIx |> ignore
                        containerFiles[name] <- s

                // (indent, container). A head with no `=` is a FILE-SCOPE
                // declaration whose contents sit at the same indent as it
                // does, so it is recorded at -1 or everything under it
                // dedents straight back out of scope.
                let stack = ResizeArray<int * string>()

                for raw in lines do
                    let cut = raw.IndexOf "//"
                    let line = if cut >= 0 then raw.Substring(0, cut) else raw

                    let ns = namespaceRx.Match line

                    if ns.Success then
                        addPath ns.Groups[1].Value

                    let head = containerHeadRx.Match line

                    if head.Success then
                        let indent = head.Groups[1].Value.Length

                        let scopeIndent =
                            if line.Contains "=" || head.Groups[2].Value = "type" then
                                indent
                            else
                                -1

                        while stack.Count > 0 && fst stack[stack.Count - 1] >= scopeIndent && scopeIndent >= 0 do
                            stack.RemoveAt(stack.Count - 1)

                        let name = simpleNameOf (head.Groups[3].Value.TrimEnd '.')
                        stack.Add(scopeIndent, name)
                        addContainerFile name
                        here.Add name |> ignore
                    else
                        let m = declRx.Match line
                        let m2 = if m.Success then m else caseOrFieldRx.Match line

                        if m2.Success then
                            let indent = m2.Groups[1].Value.Length
                            let name = m2.Groups[m2.Groups.Count - 1].Value
                            here.Add name |> ignore

                            while stack.Count > 0 && fst stack[stack.Count - 1] >= indent do
                                stack.RemoveAt(stack.Count - 1)

                            for _, container in stack do
                                addDecl container name)

            let sameUnit (container: string) (name: string) =
                match containerFiles.TryGetValue container with
                | true, ixs -> ixs |> Seq.exists (fun i -> fileDecls[i].Contains name)
                | _ -> false

            (decls |> Seq.map (fun kv -> kv.Key, Set.ofSeq kv.Value) |> Map.ofSeq), (pairs |> Set.ofSeq), sameUnit

        // A dotted token in prose is not always a member access, and the
        // three shapes below are the ones a comment corpus actually
        // contains. Each is DERIVED — there is no list of blessed names.
        //
        //   * a FILE PATH (`Directory.Build.props`, `Client.js`,
        //     `AIAgentEngine.fs.emitLatency`): a segment that is a source
        //     extension this repo holds.
        //   * an EXTERNAL namespace (`System.Diagnostics.ActivitySource`,
        //     `Fable.Mocha`, `Google.Api.Gax`): a root the tree `open`s or
        //     declares but the public surface never renders a type under.
        //     That difference IS the definition of external, and it costs
        //     nothing to keep current.
        //   * a QUALIFICATION step (`Platform.Client.HostRouteContract`):
        //     the "member" is itself a container the surface owns, so the
        //     dot is a path separator rather than an access.
        let citationFileExtensions =
            set [
                "config"
                "css"
                "csproj"
                "dll"
                "fs"
                "fsi"
                "fsproj"
                "fsx"
                "html"
                "js"
                "json"
                "md"
                "nupkg"
                "props"
                "ps1"
                "sln"
                "targets"
                "ts"
                "txt"
                "xml"
                "yaml"
                "yml"
            ]

        let externalRoots =
            let surfaceRoots =
                apiSurface.Value.RealTypes
                |> List.map (fun (full: string, _) -> (full.Split('+')[0]).Split('.')[0])
                |> Set.ofList

            let openedRoots =
                sources
                |> Seq.collect (fun (_, lines) -> lines)
                |> Seq.choose (fun l ->
                    let m = namespaceRx.Match l

                    if m.Success then
                        Some(m.Groups[1].Value.Split('.')[0])
                    else
                        None)
                |> Set.ofSeq

            Set.difference openedRoots surfaceRoots

        let mutable pointersSeen = 0
        let mutable pointersChecked = 0
        let mutable pointersOutside = 0
        let mutable pointersAmbiguous = 0
        let mutable pointersNamed = 0
        let mutable citationsSeen = 0
        let mutable citationsResolved = 0
        let mutable citationsOutside = 0
        let mutable citationsAmbiguous = 0
        let mutable citationsLocal = 0
        let mutable citationsNamespace = 0

        let findings =
            sources
            |> List.collect (fun (rel, lines) ->
                commentLinesOf lines
                |> List.collect (fun (ln, text) ->
                    let where = sprintf "%s:%d" rel ln

                    let pointerFindings =
                        pointerRx.Matches text
                        |> Seq.collect (fun m ->
                            pointersSeen <- pointersSeen + 1
                            let token = m.Groups["path"].Value
                            let citedFrom = int m.Groups["from"].Value

                            let citedTo =
                                if m.Groups["to"].Success then
                                    int m.Groups["to"].Value
                                else
                                    citedFrom

                            let target =
                                if token.Contains "/" || token.Contains "\\" then
                                    let relPath = toSlash token + ".fs"

                                    match byRelPath.TryFind relPath with
                                    | Some ls -> Ok(relPath, ls)
                                    | None when relPath.StartsWith "src/" -> Error(Some relPath)
                                    | None ->
                                        pointersOutside <- pointersOutside + 1
                                        Error None
                                else
                                    match byBaseName.TryFind(token + ".fs") with
                                    | Some [ (r, ls) ] -> Ok(r, ls)
                                    | Some(_ :: _ :: _) ->
                                        // Two files share this basename, so the
                                        // citation resolves to neither. Reported
                                        // as UNKNOWN, never as wrong — and
                                        // counted only here, so the census
                                        // classes stay disjoint and sum.
                                        pointersAmbiguous <- pointersAmbiguous + 1
                                        Error None
                                    | _ ->
                                        pointersOutside <- pointersOutside + 1
                                        Error None

                            match target with
                            | Error(Some missing) -> [
                                sprintf "%s: cites `%s:%d`, and this repo has no such file." where missing citedFrom
                              ]
                            | Error None -> []
                            | Ok(targetRel, targetLines) ->
                                pointersChecked <- pointersChecked + 1

                                if targetLines.Length < citedTo then
                                    [
                                        sprintf
                                            "%s: cites `%s:%d`, but that file has %d line(s)."
                                            where
                                            targetRel
                                            citedTo
                                            targetLines.Length
                                    ]
                                else
                                    // …and where the SAME comment names the
                                    // binding as `File.member`, the name must
                                    // still be near the line cited for it.
                                    let container = Path.GetFileNameWithoutExtension targetRel

                                    let named =
                                        namedBindingRx.Matches text
                                        |> Seq.filter (fun b -> b.Groups[1].Value = container)
                                        |> Seq.map (fun b -> b.Groups[2].Value)
                                        |> Seq.distinct
                                        |> List.ofSeq

                                    named
                                    |> List.collect (fun member' ->
                                        pointersNamed <- pointersNamed + 1
                                        let lo = max 1 (citedFrom - sourceCitationWindow)

                                        let hi = min targetLines.Length (citedTo + sourceCitationWindow)

                                        let inWindow =
                                            seq { lo..hi }
                                            |> Seq.exists (fun i ->
                                                System.Text.RegularExpressions.Regex.IsMatch(
                                                    targetLines[i - 1],
                                                    @"\b"
                                                    + System.Text.RegularExpressions.Regex.Escape member'
                                                    + @"\b"
                                                ))

                                        if inWindow then
                                            []
                                        else
                                            [
                                                sprintf
                                                    "%s: cites `%s.%s` at `%s:%d`, but `%s` does not occur within %d line(s) of there."
                                                    where
                                                    container
                                                    member'
                                                    targetRel
                                                    citedFrom
                                                    member'
                                                    sourceCitationWindow
                                            ]))
                        |> List.ofSeq

                    let citationFindings =
                        citationRx.Matches text
                        |> Seq.collect (fun m ->
                            let chain = m.Groups[1].Value
                            citationsSeen <- citationsSeen + 1

                            let segs = chain.Split('.')

                            if
                                segs |> Array.exists citationFileExtensions.Contains
                                || externalRoots.Contains segs[0]
                            then
                                citationsOutside <- citationsOutside + 1
                                []
                            else
                                match anchorOf chain with
                                | None ->
                                    citationsOutside <- citationsOutside + 1
                                    []
                                | Some(root, memberName) ->
                                    let fulls, members = surfaceContainers |> Map.find root

                                    let declaredLocally =
                                        (sourceDeclIndex.TryFind root
                                         |> Option.map (Set.contains memberName)
                                         |> Option.defaultValue false)
                                        || sameUnitIndex root memberName

                                    if fulls.Length > 1 then
                                        citationsAmbiguous <- citationsAmbiguous + 1
                                        []
                                    elif members.Contains memberName then
                                        citationsResolved <- citationsResolved + 1
                                        []
                                    elif
                                        namespacePairIndex.Contains(root, memberName)
                                        || apiSurface.Value.OwnedContainerNames.Contains memberName
                                    then
                                        citationsNamespace <- citationsNamespace + 1
                                        []
                                    elif declaredLocally then
                                        citationsLocal <- citationsLocal + 1
                                        []
                                    else
                                        [
                                            sprintf
                                                "%s: `%s` — %s has no member `%s`.\n      nearest: %s"
                                                where
                                                chain
                                                root
                                                memberName
                                                (nearestTo memberName members)
                                        ])
                        |> List.ofSeq

                    pointerFindings @ citationFindings))

        Trace.tracefn ""
        Trace.tracefn "VerifySourceCitations summary:"
        Trace.tracefn "  source files    : %d (src/**/*.fs)" sources.Length

        Trace.tracefn
            "  line pointers   : %d seen — %d checked, %d named a binding, %d outside, %d ambiguous"
            pointersSeen
            pointersChecked
            pointersNamed
            pointersOutside
            pointersAmbiguous

        Trace.tracefn
            "  API citations   : %d backticked — %d on the surface, %d declared in-tree, %d namespace path(s), %d outside, %d ambiguous"
            citationsSeen
            citationsResolved
            citationsLocal
            citationsNamespace
            citationsOutside
            citationsAmbiguous

        if not findings.IsEmpty then
            Trace.tracefn ""

            for f in findings do
                Trace.traceError ("    " + f)

            failwithf
                "VerifySourceCitations: %d comment(s) in src/**/*.fs cite something that is not there. Fix the citation against the current surface (api-baselines/<assembly>.approved.txt renders it). Prefer citing an API BY NAME over a bare `file.fs:NNN` pointer — a name is checkable and a line number rots the moment anything above it moves; where the pointer adds nothing a name cannot, delete it. See CONTRIBUTING.md, \"Citing APIs in comments\"."
                findings.Length

        Trace.tracefn ""

        Trace.tracefn
            "VerifySourceCitations: OK — %d pointer(s) and %d API citation(s) resolve."
            pointersChecked
            citationsResolved)

    // App-specific target: Azure deployment
    Target.create "Deploy-CD" (fun _ ->
        let dotnet args dir =
            CreateProcess.fromRawCommand "dotnet" args
            |> CreateProcess.withWorkingDirectory dir
            |> CreateProcess.ensureExitCode

        dotnet [ "run"; "--project"; "deploy/azure/Deploy.fsproj"; "--"; "cd" ] "."
        |> Proc.run
        |> ignore)

    // Phase 72 — template-pack packaging.
    //
    // The standard Pack target (in ToolUp.Platform.Build) walks
    // `src/**/*.fsproj` and packs each SDK fsproj as a NuGet assembly
    // package. Templates live under `templates/` and need their own
    // packaging shape (`<PackageType>Template</PackageType>` + content-
    // only packing), so they don't fit the standard Pack glob.
    //
    // `PackTemplates` packs each template-pack csproj into
    // ../local-nuget-feed for consumers to `dotnet new install
    // <PackageId>` from the same feed they consume the SDK from.
    //
    // Single-template-per-package convention: each template ships as
    // its own NuGet (e.g. ToolUp.Templates.SAFER ships only
    // `toolup-safer`). Add new template-packs by extending the
    // `templatePackProjects` glob.
    Target.create "PackTemplates" (fun _ ->
        let dotnet args dir =
            CreateProcess.fromRawCommand "dotnet" args
            |> CreateProcess.withWorkingDirectory dir
            |> CreateProcess.ensureExitCode

        let outputDir = Path.getFullName "../../local-nuget-feed"
        Directory.ensure outputDir

        let templatePackProjects = !!"templates/**/ToolUp.Templates.*.fsproj"

        for proj in templatePackProjects do
            Trace.tracefn "Packing template-pack %s..." proj

            dotnet [ "pack"; proj; "-c"; "Release"; "-o"; outputDir; "--nologo" ] "."
            |> Proc.run
            |> ignore)

    // Phase 4b dev convenience — wipe the local Platform Admin list
    // so the next `dotnet run` re-bootstraps from
    // `TOOLUP_INITIAL_PLATFORM_ADMIN` or `ServerConfig.AutoBootstrapDevAdmin`.
    // Saves the manual file-delete step when iterating on bootstrap.
    // Does NOT clear the rest of `data/` — KB content, files, teams,
    // etc. are preserved.
    //
    // Usage: `dotnet run -- ResetPlatformAdmins`
    //
    // The reset only applies to the reference deployment's
    // `LocalFileStorage` data path under `src/ToolUpApp-Server/data/`.
    // Cloud-storage deployments (Azure Blob, S3, GCS) are unaffected
    // — operators clear those via the cloud provider's tooling.
    Target.create "ResetPlatformAdmins" (fun _ ->
        let adminListPath =
            Path.Combine(__SOURCE_DIRECTORY__, "src", "ToolUpApp-Server", "data", "_platform", "platform-admins.json")

        if File.Exists adminListPath then
            File.Delete adminListPath
            Trace.tracefn "Removed %s" adminListPath
            Trace.tracefn "Next `dotnet run` will re-bootstrap the Platform Admin list."
        else
            Trace.tracefn "No admin list to remove (already empty / no LocalFileStorage data)."
            Trace.tracefn "Path checked: %s" adminListPath)

    // Phase 167 — pack the `toolup` CLI as a tool-manifest-installable
    // `dotnet tool` into the shared local feed. The standard `Pack`
    // target (in ToolUp.Platform.Build) already walks `src/**/*.fsproj`
    // and picks up ToolUp.Cli too — `PackCli` is the fast single-package
    // path for iterating on the CLI and then `dotnet tool install
    // --add-source ../../local-nuget-feed ToolUp.Cli`.
    //
    // Usage: `dotnet run -- PackCli`
    Target.create "PackCli" (fun _ ->
        let dotnet args dir =
            CreateProcess.fromRawCommand "dotnet" args
            |> CreateProcess.withWorkingDirectory dir
            |> CreateProcess.ensureExitCode

        let outputDir = Path.getFullName "../../local-nuget-feed"
        Directory.ensure outputDir

        dotnet
            [
                "pack"
                "src/ToolUp.Cli/ToolUp.Cli.fsproj"
                "-c"
                "Release"
                "-o"
                outputDir
                "--nologo"
            ]
            "."
        |> Proc.run
        |> ignore)

    // Phase 626 — unreferenced-definition report.
    //
    // Nothing in this repo detected a definition with no call sites, and
    // the compiler does not fill the gap: `--warnon:1182` fires only for
    // unused LOCAL bindings, so a module-level `let private` with zero
    // callers is silent. That is how `DatadogLogsAuditSink`'s
    // `extractEventScopeId` drifted 51 of 132 match arms behind without
    // anyone noticing — nothing called it, so nothing failed.
    //
    // A REPORT, not a gate, and that is a deliberate choice rather than
    // a staging post: the tool cannot distinguish "dead" from "not yet
    // used", and in this SDK both are legitimate — a seam shipped ahead
    // of its first implementor is exactly the shape it would flag. A
    // gate makes keeping such a thing cost a suppression mechanism, and
    // the pressure then runs toward deleting seams to get green. Pass
    // `--fail-on-dead` to gate anyway; see tools/ToolUp.DeadCode/README.md
    // for the promotion criteria and the analysis's documented limits.
    //
    // Usage: `dotnet run --project Build.fsproj -- DeadCodeReport`
    Target.create "DeadCodeReport" (fun _ ->
        // Read from the process argv rather than the target context, for
        // the same reason VerifyDocSnippets does: FAKE's own CLI parser
        // consumes trailing options before the target sees them.
        let passthrough =
            [ "--fail-on-dead"; "--verbose"; "--json" ]
            |> List.filter (fun flag -> args |> Array.contains flag)

        let toolArgs =
            [ "run"; "--project"; "tools/ToolUp.DeadCode"; "--" ]
            @ [ "--repo-root"; __SOURCE_DIRECTORY__ ]
            @ passthrough

        CreateProcess.fromRawCommand "dotnet" toolArgs
        |> CreateProcess.withWorkingDirectory __SOURCE_DIRECTORY__
        |> CreateProcess.ensureExitCode
        |> Proc.run
        |> ignore)

    // Phase 213 — Lighthouse / Core-Web-Vitals budget gate. The deciding
    // half only: it reads a committed budget file plus the Lighthouse
    // JSON reports a run already produced, and fails on any breach. The
    // measuring half (build the sample site, serve it on a throwaway
    // port, drive Lighthouse over the budgeted page set) is
    // dev-scripts/cwv-budget-gate.ps1, which sets TOOLUP_CWV_BUDGET /
    // TOOLUP_CWV_REPORTS and invokes this target last.
    //
    // Registration is unconditional and reads no environment at startup,
    // so every other target stays runnable with none of the variables
    // set; the target body resolves and reports its own missing inputs.
    CoreWebVitalsBudgetGate.registerTarget ()

    // Phase 587 — instantiate-then-build smoke gate for the
    // `platformsdk-module-packaged` template.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyPackagedModuleTemplate`
    //
    // WHY THIS IS A SEPARATE TARGET FROM VerifyTemplates. That gate
    // builds template PROJECTS in place, against the repo's own
    // Directory.Build.props / Directory.Packages.props / nuget.config.
    // It works because those templates are root-inheriting fragments.
    // This template is not: it is a whole repository — its own CPM
    // props, its own nuget.config, its own global.json, and a literal
    // TOOLUP_SDK_VERSION placeholder — which is exactly the class the
    // VerifyTemplates note calls "not buildable in-repo without
    // rewriting what makes it a template", alongside templates/safer
    // and templates/platformsdk-solution. So this IS the
    // instantiate-then-build harness that note said such a template
    // would need: `dotnet new` it into a scratch directory OUTSIDE the
    // repo (so no ancestor MSBuild file leaks in) and run the
    // scaffold's own pipeline end to end.
    //
    // WHAT IT PROVES, in the phase's words: scaffold -> build -> pack ->
    // both conformance layers green. The scaffold's own target chain
    // makes that one command: `Pack` depends on `Test` (the module-seam
    // contract pack) and on `VerifyPackagedModule` (the packaging
    // layout laws), so a green Pack cannot have skipped either.
    //
    // The gate-version + cache-wipe machinery is shared with
    // VerifyTemplates and load-bearing for the same reason: packing at
    // $(ToolUpSdkVersion) would be a same-version repack, and NuGet
    // would serve the previously-extracted copy, so the gate would
    // measure whatever was packed last rather than current source.
    let packagedModuleTemplateDir = "templates/platformsdk-module-packaged"

    // The VerifyTemplates closure plus the Build package: the generated
    // repo's Build.fsproj references ToolUp.Platform.Build for the
    // packaged-module conformance target. Everything else it restores
    // (Feliz, Fable.*, Expecto, FAKE) comes from nuget.org, which is
    // what a real consumer does.
    let packagedModuleTemplateGatePackages =
        templateGatePackages @ [ "ToolUp.Platform.Build" ]

    Target.create "VerifyPackagedModuleTemplate" (fun _ ->
        let runIn exe args dir =
            CreateProcess.fromRawCommand exe args
            |> CreateProcess.withWorkingDirectory dir
            |> Proc.run
            |> fun r -> r.ExitCode

        let runCheckedIn exe args dir =
            let code = runIn exe args dir

            if code <> 0 then
                failwithf
                    "VerifyPackagedModuleTemplate: `%s %s` exited %d (in %s)"
                    exe
                    (String.concat " " args)
                    code
                    dir

        // ── 0. The vendored conformance pack has not drifted ─────────
        //
        // The template's test project VENDORS the SDK's ModuleContract
        // laws, because the SDK's test project is not packable and
        // copying is the documented adoption route. A vendored copy
        // drifts silently by construction, and this one is shipped to
        // consumers as "born conformant" — so the copy is checked
        // against its source here rather than trusted.
        //
        // Only the LAWS half is vendored: everything from the reference
        // module onwards is the pack's own self-test, which binds
        // deliberately non-conforming witnesses and belongs to the SDK.
        let packSource = "src/ToolUp.Platform.Tests/Contracts/ModuleContract.fs"

        let vendoredPack =
            packagedModuleTemplateDir + "/tests/MyModule.Tests/Contracts/ModuleContract.fs"

        let selfTestMarker = "// ── a conforming reference module"

        let normaliseText (s: string) =
            s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd()

        let sourceText = File.ReadAllText packSource

        let markerIndex =
            sourceText.IndexOf(selfTestMarker, System.StringComparison.Ordinal)

        if markerIndex < 0 then
            failwithf
                "VerifyPackagedModuleTemplate: could not find the laws/self-test boundary marker %s in %s. The vendored-copy drift check cannot run; re-establish the marker or update this target."
                selfTestMarker
                packSource

        let expectedVendored = normaliseText (sourceText.Substring(0, markerIndex))
        let actualVendored = normaliseText (File.ReadAllText vendoredPack)

        if expectedVendored <> actualVendored then
            failwithf
                "VerifyPackagedModuleTemplate: the vendored conformance pack has drifted from the SDK's.%sRe-copy the laws region:%s  the first %d characters of %s (everything before `%s`) into %s."
                System.Environment.NewLine
                System.Environment.NewLine
                markerIndex
                packSource
                selfTestMarker
                vendoredPack

        Trace.tracefn "▶ VerifyPackagedModuleTemplate: vendored conformance pack matches %s" packSource

        // ── 1. A scratch feed carrying the SDK at the gate version ───
        let feedDir = Path.getFullName "obj/packaged-module-template-feed"
        Shell.deleteDir feedDir
        Directory.ensure feedDir

        let globalPackages =
            match Environment.environVarOrNone "NUGET_PACKAGES" with
            | Some dir when dir <> "" -> dir
            | _ ->
                Path.Combine(
                    System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile,
                    ".nuget",
                    "packages"
                )

        for pkg in packagedModuleTemplateGatePackages do
            let cached =
                Path.Combine(globalPackages, pkg.ToLowerInvariant(), templateGateVersion)

            if Directory.Exists cached then
                Shell.deleteDir cached

        for pkg in packagedModuleTemplateGatePackages do
            Trace.tracefn "▶ VerifyPackagedModuleTemplate: packing %s @ %s" pkg templateGateVersion

            runCheckedIn
                "dotnet"
                [
                    "pack"
                    sprintf "src/%s/%s.fsproj" pkg pkg
                    sprintf "-p:Version=%s" templateGateVersion
                    "-o"
                    feedDir
                    "--nologo"
                ]
                "."

        // ── 2. Instantiate OUTSIDE the repo ──────────────────────────
        //
        // Under the repo, the scaffold's own Directory.Build.props stops
        // the MSBuild walk but nuget.config files MERGE up the tree, so
        // forge's sources would silently paper over a broken feed
        // declaration in the template. A short temp path also keeps
        // Windows MAX_PATH away from fsc, which fails by emitting no dll
        // and no error.
        let scratchRoot = Path.Combine(Path.GetTempPath(), "tu-pmt")
        let moduleName = "Gate.Module"
        let scaffoldDir = Path.Combine(scratchRoot, moduleName)
        Shell.deleteDir scratchRoot
        Directory.ensure scaffoldDir

        let templatePath = Path.getFullName packagedModuleTemplateDir

        // Uninstall first, ignoring the exit code: a dev machine may
        // carry an earlier install of the same identity, and `install`
        // refuses rather than replacing.
        runIn "dotnet" [ "new"; "uninstall"; templatePath ] "." |> ignore

        runCheckedIn "dotnet" [ "new"; "install"; templatePath ] "."

        try
            // Forward slashes for the feed: it lands in an F# verbatim
            // string in the generated Build.fs and in nuget.config, and
            // a trailing-backslash Windows path would break the former.
            let feedForTemplate = feedDir.Replace('\\', '/')

            runCheckedIn
                "dotnet"
                [
                    "new"
                    "platformsdk-module-packaged"
                    "-n"
                    moduleName
                    "-o"
                    scaffoldDir
                    "--namespace-root"
                    "Gate.Module"
                    "--sdk-version"
                    templateGateVersion
                    "--feed"
                    feedForTemplate
                ]
                "."

            // ── 3. The scaffold's own pipeline, end to end ───────────
            //
            // One command, because the generated target chain wires
            // both conformance layers ahead of Pack. If either the
            // module-seam laws or the packaging layout laws fail, this
            // fails.
            runCheckedIn "dotnet" [ "run"; "--project"; "Build.fsproj"; "--"; "Pack" ] scaffoldDir

            // ── 4. Every ToolUp.* resolved at the gate version ───────
            //
            // The equivalent of VerifyTemplates' NU1603 escalation, done
            // by reading the restore result rather than by a warning
            // flag we cannot inject through the scaffold's own driver. A
            // ToolUp.* package resolved at any other version means the
            // gate closure above is incomplete and the scaffold compiled
            // against a mix of current and released SDK.
            let assetsFile =
                Path.Combine(scaffoldDir, "src", moduleName, "obj", "project.assets.json")

            if not (File.Exists assetsFile) then
                failwithf
                    "VerifyPackagedModuleTemplate: no restore graph at %s — the scaffold did not restore."
                    assetsFile

            let strayPins =
                System.Text.RegularExpressions.Regex.Matches(
                    File.ReadAllText assetsFile,
                    "\"(ToolUp\\.[A-Za-z.]+)/([^\"]+)\""
                )
                |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value)
                |> Seq.filter (fun (_, version) -> version <> templateGateVersion)
                |> Seq.distinct
                |> List.ofSeq

            if not (List.isEmpty strayPins) then
                failwithf
                    "VerifyPackagedModuleTemplate: %d ToolUp.* package(s) resolved at a version other than %s — the gate closure is incomplete, so the scaffold built against a MIX of current source and a released SDK: %s. Add the named package(s) to `packagedModuleTemplateGatePackages`."
                    (List.length strayPins)
                    templateGateVersion
                    (strayPins |> List.map (fun (p, v) -> sprintf "%s@%s" p v) |> String.concat ", ")

            // ── 5. The packed layout is what the contract declared ───
            //
            // VerifyPackagedModule ran pre-Pack against the project's
            // DECLARATIONS. This reads the artefact NuGet actually
            // produced, which is the half a declaration cannot prove.
            let nupkg =
                Directory.GetFiles(feedDir, moduleName + ".*.nupkg")
                |> Array.filter (fun p -> not (p.EndsWith(".snupkg", System.StringComparison.OrdinalIgnoreCase)))
                |> Array.sort
                |> Array.tryLast

            match nupkg with
            | None ->
                failwithf
                    "VerifyPackagedModuleTemplate: the scaffold's Pack produced no %s nupkg in %s."
                    moduleName
                    feedDir
            | Some path ->
                use archive = System.IO.Compression.ZipFile.OpenRead path

                let entries =
                    archive.Entries |> Seq.map (fun e -> e.FullName.Replace('\\', '/')) |> Set.ofSeq

                let required = [
                    "fable/" + moduleName + ".fsproj"
                    "fable/SharedTypes.fs"
                    "fable/ClientModel.fs"
                    "fable/Icons.fs"
                    "fable/ClientRegister.fs"
                    "fable/icons/module-icon.svg"
                ]

                let missing = required |> List.filter (fun r -> not (entries.Contains r))

                if not (List.isEmpty missing) then
                    failwithf
                        "VerifyPackagedModuleTemplate: the packed nupkg is missing %s. A consumer's Fable build resolves the client tier from these paths."
                        (String.concat ", " missing)

                // The server tier must NOT be in the Fable set: a
                // server-only file reaching Fable breaks the consumer's
                // build, naming this package's namespace.
                if entries.Contains "fable/Server.fs" then
                    failwith
                        "VerifyPackagedModuleTemplate: Server.fs is packed under fable/ — declared server-only, so it must never reach a consumer's Fable compile."

                Trace.tracefn
                    "▶ VerifyPackagedModuleTemplate: %s carries a conformant fable/ layout"
                    (Path.GetFileName path)
        finally
            runIn "dotnet" [ "new"; "uninstall"; templatePath ] "." |> ignore

        Trace.tracefn "▶ VerifyPackagedModuleTemplate: scaffold -> build -> conformance -> pack, green")

    execute args