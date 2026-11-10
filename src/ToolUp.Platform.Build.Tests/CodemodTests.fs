// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.CodemodTests

open System
open System.IO
open System.Text
open Expecto
open ToolUp.Platform

// ─── Phase 183 — the consumer codemod for the 0.x breaking renames ─────
//
// The codemod's contract in four arms, all decided over the committed
// fixture consumer tree under fixtures/codemod/ — no build, no tool
// restore, no process.
//
// The GOLDEN arm materialises `before/` into a scratch tree, runs the
// codemod, and compares every file to `after/` byte-for-byte (modulo
// the checkout's line endings): every deterministic rename lands, and
// nothing else moves. The fixture tree carries every rewrite rule at
// least once, a hand-migrated file that must come back untouched, and
// a prose file the walk must never visit.
//
// The REPORT arm compares the rendered findings to `findings.txt`: the
// non-mechanical sites are LISTED, with their rule and the doc section
// to read, and are NOT rewritten — the golden `after/` files still carry
// them. A codemod that guessed at a `match ctx.Mode with` would pass a
// naive "the file changed" test; this pack pins that it did not.
//
// The IDEMPOTENCE arm runs the codemod a second time over its own
// output and asserts zero rewrites and an identical report. The
// `--check` rendering over a plan is asserted to write nothing.
//
// The ENCODING arm proves a UTF-8 BOM and CRLF terminators survive a
// rewrite, because a consumer's diff that flips every line ending is a
// diff nobody can review.
//
// The fixtures carry a `.txt` suffix on disk (`Program.fs.txt`,
// `Server.fsproj.txt`) that `materialise` strips: a real `.fsproj` under
// src/ would be swept into the Pack target's `src/**/*.fsproj` glob, and
// a real `.fs` would be Fantomas-checked and conformance-scanned as SDK
// source. The suffix keeps fixture consumer code out of every tree-wide
// gate without an exclusion list somewhere else.

let private fixtureRoot =
    Path.Combine(AppContext.BaseDirectory, "fixtures", "codemod")

let private normalise (s: string) = s.Replace("\r\n", "\n")

let private fixtureFiles (subdir: string) =
    let root = Path.Combine(fixtureRoot, subdir)

    Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories)
    |> Seq.map (fun f ->
        let rel = Path.GetRelativePath(root, f).Replace('\\', '/')
        rel.Substring(0, rel.Length - ".txt".Length), File.ReadAllText f)
    |> Map.ofSeq

/// Copies `before/` into a fresh scratch tree with the `.txt` suffix
/// stripped, and returns the tree's root. The caller deletes it.
let private materialise () =
    let temp =
        Path.Combine(Path.GetTempPath(), "toolup-codemod-" + Guid.NewGuid().ToString "N")

    for KeyValue(rel, text) in fixtureFiles "before" do
        let target = Path.Combine(temp, rel)
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        File.WriteAllText(target, text, UTF8Encoding false)

    temp

let private withScratchTree (body: string -> unit) =
    let temp = materialise ()

    try
        body temp
    finally
        if Directory.Exists temp then
            Directory.Delete(temp, true)

let private treeSnapshot (root: string) =
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    |> Seq.map (fun f -> Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllBytes f)
    |> Map.ofSeq

let private golden name =
    File.ReadAllText(Path.Combine(fixtureRoot, name)) |> normalise

let private rewriteSource (text: string) =
    Codemod.rewriteText CodemodFileClass.Source text

let private reviewSource (text: string) =
    Codemod.reviewText CodemodFileClass.Source text

let tests =
    testList "Phase 183 — consumer codemod" [
        testList "golden tree" [
            testCase "every deterministic rename lands and nothing else moves"
            <| fun () ->
                withScratchTree (fun root ->
                    let plan = Codemod.plan root
                    let written = Codemod.apply plan
                    let expected = fixtureFiles "after"

                    let actual =
                        treeSnapshot root |> Map.map (fun _ bytes -> Encoding.UTF8.GetString bytes)

                    Expect.equal
                        (actual |> Map.keys |> Set.ofSeq)
                        (expected |> Map.keys |> Set.ofSeq)
                        "the scratch tree holds exactly the golden file set"

                    for KeyValue(rel, text) in expected do
                        Expect.equal
                            (normalise actual[rel])
                            (normalise text)
                            (sprintf "%s matches after/%s.txt" rel rel)

                    Expect.equal
                        (written |> List.map (fun p -> Path.GetRelativePath(root, p).Replace('\\', '/')))
                        [
                            "Directory.Packages.props"
                            "src/Client/App.fs"
                            "src/Client/Grid.fs"
                            "src/Client/Shell.fs"
                            "src/Server/Assistant.fs"
                            "src/Server/Config.fs"
                            "src/Server/Connectors.fs"
                            "src/Server/Facts.fs"
                            "src/Server/Hosting.fs"
                            "src/Server/Identity.fs"
                            "src/Server/Infrastructure.fs"
                            "src/Server/Jobs.fs"
                            "src/Server/Models.fs"
                            "src/Server/Program.fs"
                            "src/Server/RateLimit.fs"
                            "src/Server/Remoting.fs"
                            "src/Server/Server.fsproj"
                            "src/Server/Sinks.fs"
                        ]
                        "apply writes exactly the changed files, in plan order")

            testCase "a hand-migrated file is byte-for-byte untouched and reports nothing"
            <| fun () ->
                withScratchTree (fun root ->
                    let plan = Codemod.plan root

                    let already =
                        plan.Files |> List.find (fun f -> f.RelativePath = "src/Server/Already.fs")

                    Expect.isFalse already.Changed "no rewrite"
                    Expect.isEmpty already.Rewrites "no rewrite record"
                    Expect.isEmpty already.Findings "no finding — a consumer who hand-migrated pays nothing (GP 13)")

            testCase "prose is never walked; only .fs / .fsx / .fsproj / .props / .targets are"
            <| fun () ->
                withScratchTree (fun root ->
                    let plan = Codemod.plan root
                    let visited = plan.Files |> List.map _.RelativePath

                    Expect.isFalse
                        (visited |> List.contains "README.md")
                        "README.md mentions `open Fable.Remoting.Server` and is not a codemod input"

                    Expect.equal
                        visited
                        [
                            "Directory.Packages.props"
                            "src/Client/App.fs"
                            "src/Client/Grid.fs"
                            "src/Client/Shell.fs"
                            "src/Server/AiRag.fs"
                            "src/Server/Already.fs"
                            "src/Server/Assistant.fs"
                            "src/Server/Config.fs"
                            "src/Server/Connectors.fs"
                            "src/Server/Facts.fs"
                            "src/Server/Guarded.fs"
                            "src/Server/Hosting.fs"
                            "src/Server/Identity.fs"
                            "src/Server/Infrastructure.fs"
                            "src/Server/Jobs.fs"
                            "src/Server/Models.fs"
                            "src/Server/Program.fs"
                            "src/Server/RateLimit.fs"
                            "src/Server/Remoting.fs"
                            "src/Server/RemotingLeftovers.fs"
                            "src/Server/Server.fsproj"
                            "src/Server/Sinks.fs"
                        ]
                        "the walk is the eligible files in ordinal relative-path order")

            testCase "the walk prunes build output and package caches"
            <| fun () ->
                withScratchTree (fun root ->
                    for dir in [ "bin"; "obj"; "node_modules"; ".git" ] do
                        let d = Path.Combine(root, dir)
                        Directory.CreateDirectory d |> ignore
                        File.WriteAllText(Path.Combine(d, "Stray.fs"), "open Elmish\n")

                    let plan = Codemod.plan root

                    Expect.isFalse
                        (plan.Files |> List.exists (fun f -> f.RelativePath.EndsWith "Stray.fs"))
                        "nothing under a pruned directory is visited")
        ]

        testList "report" [
            testCase "the non-mechanical sites are the golden findings list, and are not rewritten"
            <| fun () ->
                withScratchTree (fun root ->
                    let plan = Codemod.plan root
                    Expect.equal (normalise (Codemod.renderFindings plan)) (golden "findings.txt") "findings.txt"

                    // The sites the report names still carry their pre-rename text after apply.
                    Codemod.apply plan |> ignore
                    let program = File.ReadAllText(Path.Combine(root, "src", "Server", "Program.fs"))

                    Expect.stringContains program "match ctx.Mode with" "§4 arm mapping is reported, not guessed"

                    Expect.stringContains
                        program
                        "(mode: PlatformMode)"
                        "§6 handler-parameter drop is reported, not guessed"

                    Expect.stringContains
                        program
                        "ServerApp.withAnonymousRoute"
                        "§5 per-route requirement is reported, not guessed"

                    let sinks = File.ReadAllText(Path.Combine(root, "src", "Server", "Sinks.fs"))

                    Expect.stringContains
                        sinks
                        "NotificationKind.SinkKind.Push"
                        "the push variant is a choice, so the site is reported"

                    Expect.stringContains sinks "MaxRetries = 3" "the +1 attempt semantics are reported, not applied")

            testCase "the --check rendering is the golden diff"
            <| fun () ->
                withScratchTree (fun root ->
                    let plan = Codemod.plan root
                    Expect.equal (normalise (Codemod.renderDiff plan)) (golden "diff.txt") "diff.txt")

            testCase "planning and rendering write nothing"
            <| fun () ->
                withScratchTree (fun root ->
                    let before = treeSnapshot root
                    let plan = Codemod.plan root
                    Codemod.renderDiff plan |> ignore
                    Codemod.renderFindings plan |> ignore
                    Codemod.renderSummary plan |> ignore
                    Expect.equal (treeSnapshot root) before "the tree is untouched until apply")

            testCase "the summary counts what the golden tree holds"
            <| fun () ->
                withScratchTree (fun root ->
                    let plan = Codemod.plan root

                    Expect.equal
                        (Codemod.renderSummary plan)
                        "Codemod: 22 file(s) visited, 105 rewrite(s) in 18 file(s), 109 site(s) for review."
                        "summary")
        ]

        testList "Phase 1095 — the Capabilities member" [
            testCase "an implementer gains the record naming exactly the capability interfaces its block implements"
            <| fun () ->
                let source =
                    String.concat "\r\n" [
                        "type Both() ="
                        "    interface IEmitsNativePayload with"
                        "        member _.NativeFormat = PayloadFormat.Csv"
                        "    interface IDataSource with"
                        "        member _.Kind = \"Both\""
                        "    interface IWatchableFileSource with"
                        "        member _.ListObjects _ = async { return Ok [] }"
                        ""
                        "type Plain() ="
                        "    interface IDataSource with"
                        "        member _.Kind = \"Plain\""
                        ""
                    ]

                let rewritten, rewrites = rewriteSource source

                Expect.stringContains
                    rewritten
                    "    interface IDataSource with\r\n        member this.Capabilities = { ConnectorCapabilities.none with Watchable = Some(this :> IWatchableFileSource); Native = Some(this :> IEmitsNativePayload) }\r\n"
                    "both capabilities, in record order, on the implementation line's own terminator"

                Expect.stringContains
                    rewritten
                    "type Plain() =\r\n    interface IDataSource with\r\n        member _.Capabilities = ConnectorCapabilities.none\r\n"
                    "a type in the same file implementing neither declares none — blocks do not leak"

                Expect.equal
                    (rewrites |> List.map (fun r -> r.RuleId, r.Line))
                    [ "1095-capabilities-member", 4; "1095-capabilities-member", 10 ]
                    "one rewrite per implementer"

                Expect.isEmpty (snd (rewriteSource rewritten)) "a second run adds nothing"
        ]

        testList "the model and compute Capabilities member" [
            testCase
                "each seam's implementer gains its own record, and an object expression reaches its other interfaces by downcast"
            <| fun () ->
                let source =
                    String.concat "\n" [
                        "type Scoring() ="
                        "    interface IModelEvaluationMetrics with"
                        "        member _.EvaluateMetrics(_, _, _, _) = async { return Ok Map.empty }"
                        "    interface IModelFitProvider with"
                        "        member _.Kind = \"scoring\""
                        ""
                        "let registry (inner: IModelRegistry) ="
                        "    { new IModelRegistry with"
                        "        member _.Get(s, k) = inner.Get(s, k)"
                        "    }"
                        ""
                        "let pushing (inner: IExternalComputeDispatcher) ="
                        "    { new IExternalComputeDispatcher with"
                        "        member _.Backend = inner.Backend"
                        "      interface IExternalCallbackCapableBackend with"
                        "          member _.AcceptCallbackCredential(_, _) = async { return () }"
                        "    }"
                        ""
                    ]

                let rewritten, rewrites = rewriteSource source

                Expect.stringContains
                    rewritten
                    "member this.Capabilities = { ModelFitProviderCapabilities.none with Metrics = Some(this :> IModelEvaluationMetrics) }"
                    "a type upcasts to the capability interface it implements"

                Expect.stringContains
                    rewritten
                    "    { new IModelRegistry with\n        member _.Capabilities = RegistryCapabilities.none\n"
                    "a registry declaring nothing answers none"

                Expect.stringContains
                    rewritten
                    "member this.Capabilities = { BackendCapabilities.none with Callback = Some(this :?> IExternalCallbackCapableBackend) }"
                    "an object expression's `this` is the seam, so the capability is reached by downcast"

                Expect.equal
                    (rewrites |> List.map _.RuleId |> List.sort)
                    [
                        "1094-backend-capabilities-member"
                        "1094-fit-provider-capabilities-member"
                        "1094-registry-capabilities-member"
                    ]
                    "one rewrite per implementer, under its seam's rule"

                Expect.isEmpty (snd (rewriteSource rewritten)) "a second run adds nothing"

            testCase "a capability probe and an isolation-posture interface are reported, never rewritten"
            <| fun () ->
                let source =
                    String.concat "\n" [
                        "let evaluates (p: IModelFitProvider) = (box p :? IModelEvaluationMetrics)"
                        "type Sandbox() ="
                        "    interface IIsolatedComputeBackend with"
                        "        member _.IsolationPosture = IsolationPosture.standardOnly"
                    ]

                Expect.equal
                    (reviewSource source |> List.map (fun f -> f.RuleId, f.Line))
                    [ "1094-capability-probe", 1; "1094-isolated-backend", 3 ]
                    "both sites are listed for review"

            testCase "the one scope form: a held scope is passed whole, a string scope is reported, never rewritten"
            <| fun () ->
                let before =
                    String.concat "\n" [
                        "let! receipt = store.AssertBatch(scope.ScopeId, drafts)"
                        "let! ranked = store.QueryPopulation(ctx.Scope.ScopeId, query)"
                        "| Error offenders -> Error(FactWrite.renderOffenders (List.length drafts) offenders)"
                        "let options = CoverageNarrativeOptions.sameContainer \"reader\""
                        "let! fact = store.Get(scopeId, factId)"
                        "let! heads = store.Query(\"tenant\", FactQuery.all)"
                        "let! row = entities.Get(request.ScopeId, id)"
                    ]

                let after =
                    String.concat "\n" [
                        "let! receipt = store.AssertBatch(scope, drafts)"
                        "let! ranked = store.QueryPopulation(ctx.Scope, query)"
                        "| Error offenders -> Error(FactStoreError.describe offenders)"
                        "let options = CoverageNarrativeOptions.everyScope \"reader\""
                        "let! fact = store.Get(scopeId, factId)"
                        "let! heads = store.Query(\"tenant\", FactQuery.all)"
                        "let! row = entities.Get(request.ScopeId, id)"
                    ]

                let rewritten, rewrites = rewriteSource before

                Expect.equal (normalise rewritten) after "the golden rewrite"

                Expect.equal
                    (rewrites |> List.map _.RuleId |> List.sort)
                    [
                        "1157-render-offenders"
                        "1157-resolved-scope-id"
                        "1157-resolved-scope-id"
                        "1157-same-container"
                    ]
                    "one rewrite per site, a string-keyed store's member left alone"

                Expect.equal
                    (reviewSource rewritten
                     |> List.filter (fun f -> f.RuleId.StartsWith "1157-")
                     |> List.map (fun f -> f.RuleId, f.Line))
                    [ "1157-string-scope-call", 5; "1157-string-scope-call", 6 ]
                    "a string scope has no mechanical target, so it is listed for review"

                Expect.isEmpty (snd (rewriteSource rewritten)) "a second run adds nothing"
        ]

        testList "Phase 877 — nested config and FeatureMode" [
            testCase "a record update regroups its moved labels under their concern, the base re-read as base.Concern"
            <| fun () ->
                let before =
                    "let config = {\n    ServerConfig.defaults with\n        Port = 5000\n        // the entity store backs the catalog\n        EntityStore = EnabledEntityStore\n        RequireHttps = true\n        SlowRequestThresholdOverrides =\n            Map.ofList [\n                \"/api/slow\", TimeSpan.FromSeconds 5.0\n            ]\n        ReplicaCount = 3\n        FactStore = FactStoreMode.EnabledFactStore\n}\n"

                let after, rewrites = rewriteSource before

                Expect.equal
                    after
                    "let config = {\n    ServerConfig.defaults with\n        Port = 5000\n        Stores = {\n            ServerConfig.defaults.Stores with\n                // the entity store backs the catalog\n                EntityStore = FeatureMode.Enabled\n                FactStore = FeatureMode.Enabled\n        }\n        SecurityPosture = {\n            ServerConfig.defaults.SecurityPosture with\n                RequireHttps = true\n        }\n        Observability = {\n            ServerConfig.defaults.Observability with\n                SlowRequestThresholdOverrides =\n                    Map.ofList [\n                        \"/api/slow\", TimeSpan.FromSeconds 5.0\n                    ]\n        }\n        MultiInstance = {\n            ServerConfig.defaults.MultiInstance with\n                ReplicaCount = 3\n        }\n}\n"
                    "nested by concern, in order of first appearance, continuations shifted with their item"

                Expect.contains (rewrites |> List.map _.RuleId) "877-record-update" "the structural rewrite is recorded"

            testCase "a one-line update and a two-line opener both nest, and the opener keeps its column"
            <| fun () ->
                let oneLine, _ =
                    rewriteSource
                        "let trial = { defaults with Port = 1; ShareTokenStore = EnabledShareTokenStore; Webhooks = NoWebhooks }\n"

                Expect.equal
                    oneLine
                    "let trial = {\n    defaults with\n        Port = 1\n        Stores = {\n            defaults.Stores with\n                ShareTokenStore = FeatureMode.Enabled\n        }\n        Webhooks = FeatureMode.Disabled\n}\n"
                    "one line"

                let twoLine, _ =
                    rewriteSource
                        "let hardened =\n    {\n        app.Config with\n            SecurityHardening = StrictSecurityHardening\n            Port = 8080\n    }\n"

                Expect.equal
                    twoLine
                    "let hardened =\n    {\n        app.Config with\n            SecurityPosture = {\n                app.Config.SecurityPosture with\n                    SecurityHardening = StrictSecurityHardening\n            }\n            Port = 8080\n    }\n"
                    "two-line opener"

            testCase "a nested update is a fixed point, and a record update in a comment is prose"
            <| fun () ->
                let once, _ =
                    rewriteSource
                        "let c = { ServerConfig.defaults with ReplicaCount = 2; AuditLog = EnabledAuditLog }\n"

                let twice, rewrites = rewriteSource once
                Expect.equal twice once "fixed point"
                Expect.isEmpty rewrites "nothing fires on migrated text"

                let comment =
                    "// a deployment that sets `{ config with ReplicaCount = 3 }` in code\n"

                let after, _ = rewriteSource comment
                Expect.equal after comment "a comment is not nested"

            testCase
                "a base the pass cannot re-read, or only shared labels on an unnamed base, is reported and untouched"
            <| fun () ->
                let dir =
                    Path.Combine(Path.GetTempPath(), "codemod-877-" + Guid.NewGuid().ToString("N"))

                Directory.CreateDirectory dir |> ignore

                try
                    let text =
                        "let a = { (ServerConfig.fromEnv logger overrides) with LogLevel = LogLevel.Debug }\nlet deps = { JobDeps.empty with EventStore = store }\nlet ok = { ServerConfig.defaults with EventStore = InMemoryOnly }\n"

                    let path = Path.Combine(dir, "Config.fs")
                    File.WriteAllText(path, text)
                    let result = Codemod.analyseFile dir CodemodFileClass.Source path

                    Expect.equal
                        (result.Findings |> List.map (fun f -> f.Line, f.RuleId))
                        [ 1, "877-record-update-unprovable"; 2, "877-record-update-unprovable" ]
                        "both unprovable sites are findings, with their own line"

                    Expect.stringContains result.Findings[0].Guidance "could not nest it" "the base is the problem"

                    Expect.stringContains
                        result.Findings[1].Guidance
                        "other records carry too"
                        "the label is the problem"

                    Expect.equal
                        result.Rewritten
                        "let a = { (ServerConfig.fromEnv logger overrides) with LogLevel = LogLevel.Debug }\nlet deps = { JobDeps.empty with EventStore = store }\nlet ok = {\n    ServerConfig.defaults with\n        Stores = {\n            ServerConfig.defaults.Stores with\n                EventStore = InMemoryOnly\n        }\n}\n"
                        "the named base nests a shared label; the others are left as written"
                finally
                    Directory.Delete(dir, true)

            testCase "a moved field reads through its concern on a value receiver, and a module path is never a field"
            <| fun () ->
                let after, _ =
                    rewriteSource
                        "let x = config.ReplicaCount, cfg.EventStore, deps.EventStore, app.Config.LogLevel, ctx.Config.DatadogReadback\nopen ToolUp.AssetStore\nmodule ToolUp.Platform.EntityOutbox\nlet y = Consent.ConsentStateStore.inMemory, _.AcceptEphemeralRagIndex, config.Stores.EventStore\n"

                Expect.equal
                    after
                    "let x = config.MultiInstance.ReplicaCount, cfg.Stores.EventStore, deps.EventStore, app.Config.Observability.LogLevel, ctx.Config.DatadogReadback\nopen ToolUp.AssetStore\nmodule ToolUp.Platform.EntityOutbox\nlet y = Consent.ConsentStateStore.inMemory, _.RiskAcknowledgements.AcceptEphemeralRagIndex, config.Stores.EventStore\n"
                    "value receivers re-home; capitalised path segments, a client config's DatadogReadback and an already-nested read do not"

                let findings = reviewSource after

                Expect.equal
                    (findings |> List.map _.RuleId |> List.distinct)
                    [ "877-shared-label-receiver" ]
                    "the shared labels on other receivers are reported"

            testCase "the retired modes read as FeatureMode in every spelling, and --phase selects one migration"
            <| fun () ->
                let after, _ =
                    rewriteSource
                        "let m (mode: AuditLogMode) = match mode with | NoAuditLog -> 0 | AuditLogMode.EnabledAuditLog -> 1\nlet t = CustomTelemetrySink, PresenceMode.NoPresence\nopen Fable.Remoting.Server\n"

                Expect.equal
                    after
                    "let m (mode: FeatureMode) = match mode with | FeatureMode.Disabled -> 0 | FeatureMode.Enabled -> 1\nlet t = FeatureMode.Enabled, FeatureMode.Disabled\nopen ToolUp.Remoting.Server\n"
                    "every migration applies without a filter"

                let only, _ =
                    Codemod.rewriteTextFor
                        [ CodemodMigration.NestedConfig ]
                        CodemodFileClass.Source
                        "let t = NoAuditLog\nopen Fable.Remoting.Server\n"

                Expect.equal
                    only
                    "let t = FeatureMode.Disabled\nopen Fable.Remoting.Server\n"
                    "the filter leaves the other migrations' rules unapplied"

                Expect.equal (Codemod.migrationOfPhase "877") (Some CodemodMigration.NestedConfig) "by label number"
                Expect.equal (Codemod.migrationOfPhase "1") None "an unknown number"

            testCase "the concern table and the mode table are total and disjoint"
            <| fun () ->
                let fields = Codemod.concernFields |> List.collect snd
                Expect.equal (List.distinct fields) fields "a field has one home"
                Expect.equal (Codemod.concernFields |> List.length) 5 "five concern records"
                Expect.equal (Codemod.featureModes |> List.length) 37 "thirty-seven retired modes"
                let types = Codemod.featureModes |> List.map (fun (t, _, _) -> t)
                Expect.equal (List.distinct types) types "a type is retired once"
        ]

        testList "idempotence" [
            testCase "a second run over migrated source plans zero rewrites and the same report"
            <| fun () ->
                withScratchTree (fun root ->
                    let first = Codemod.plan root
                    let firstReport = Codemod.renderFindings first
                    Codemod.apply first |> ignore
                    let after = treeSnapshot root

                    let second = Codemod.plan root
                    Expect.isEmpty second.ChangedFiles "no rewrites pending"
                    Expect.equal (Codemod.renderFindings second) firstReport "the review list does not shrink"

                    Expect.stringContains
                        (Codemod.renderDiff second)
                        "no rewrites pending"
                        "--check reports the migrated state"

                    Codemod.apply second |> ignore
                    Expect.equal (treeSnapshot root) after "a second apply writes nothing")

            testCase "no rewrite rule's output is any rewrite rule's input"
            <| fun () ->
                // The property idempotence rests on, checked on the golden output
                // line by line rather than trusted from the rule comments.
                for KeyValue(rel, text) in fixtureFiles "after" do
                    match Codemod.classify rel with
                    | Some cls ->
                        let _, rewrites = Codemod.rewriteText cls text
                        Expect.isEmpty rewrites (sprintf "after/%s.txt matches no rewrite rule" rel)
                    | None -> ()
        ]

        testList "rules" [
            testCase "package-id rules never touch F# source — the namespaces did not move"
            <| fun () ->
                // 11.C.5 Tier 2 renamed package ids only; `module
                // ToolUp.Platform.AuditSinks.SplunkHec` is still the namespace.
                let source =
                    "module ToolUp.Platform.AuditSinks.SplunkHec\nopen ToolUp.Storage.Azure\n"

                let rewrittenSource, sourceRewrites = rewriteSource source
                Expect.equal rewrittenSource source "a .fs line is not a package id"
                Expect.isEmpty sourceRewrites "no source rewrite"

                let project =
                    "<PackageReference Include=\"ToolUp.Platform.AuditSinks.SplunkHec\" />"

                let rewrittenProject, projectRewrites =
                    Codemod.rewriteText CodemodFileClass.Project project

                Expect.equal
                    rewrittenProject
                    "<PackageReference Include=\"ToolUp.AuditSinks.SplunkHec\" />"
                    "the same token in a project file IS a package id"

                Expect.equal (projectRewrites |> List.map _.RuleId) [ "11c5-package-audit-sinks" ] "rule"

            testCase "`ToolUp.Storage.AzureBlob` is not re-suffixed"
            <| fun () ->
                let line = "<PackageVersion Include=\"ToolUp.Storage.AzureBlob\" Version=\"1\" />"
                let rewritten, rewrites = Codemod.rewriteText CodemodFileClass.Project line
                Expect.equal rewritten line "already the new id"
                Expect.isEmpty rewrites "no rewrite"

            testCase
                "873: an event-store or job-store write is routed through its adapter, and a scheduler transition is reported"
            <| fun () ->
                let rewritten, _ = rewriteSource "        do! eventStore.Write evt"
                Expect.equal rewritten "        do! EventStore.writeOrRaise eventStore evt" "event-store write"

                let rewritten, _ = rewriteSource "        do! jobStore.Update job"
                Expect.equal rewritten "        do! JobStoreWrite.orRaise <| jobStore.Update job" "job-store write"

                let again, rewrites = rewriteSource rewritten
                Expect.equal again rewritten "a migrated line is a fixed point"
                Expect.isEmpty rewrites "and plans no second rewrite"

                Expect.equal
                    (reviewSource "        do! scheduler.Cancel(scopeId, jobId)" |> List.map _.RuleId)
                    [ "873-job-transition-call" ]
                    "a transition's Result is the site's to decide"

                Expect.equal
                    (reviewSource "    interface IEventStore with" |> List.map _.RuleId)
                    [ "873-implementer" ]
                    "an implementer is reported"

            testCase "an equality read of .Mode is reported, never rewritten"
            <| fun () ->
                let line = "let isPublic (ctx: AccessContext) = ctx.Mode = Anonymous"
                let rewritten, rewrites = rewriteSource line
                Expect.equal rewritten line "a predicate read is not a field assignment"
                Expect.isEmpty rewrites "no rewrite"
                Expect.equal (reviewSource line |> List.map _.RuleId) [ "66-mode-read" ] "reported"

            testCase "a record-expression `Mode = <case>` maps to the same-shape Surfaces list"
            <| fun () ->
                let cases = [
                    "Anonymous", "Surfaces.anonymous"
                    "AuthenticatedEphemeral", "Surfaces.trial"
                    "Individual", "Surfaces.individual"
                    "Team", "Surfaces.team"
                    "MultiTeam", "Surfaces.multiTeam"
                ]

                for case, expected in cases do
                    let rewritten, _ = rewriteSource (sprintf "    Mode = %s" case)
                    Expect.equal rewritten (sprintf "    Surfaces = %s" expected) case

                    let qualified, _ = rewriteSource (sprintf "    Mode = PlatformMode.%s" case)
                    Expect.equal qualified (sprintf "    Surfaces = %s" expected) ("PlatformMode." + case)

            testCase "a consumer's own `Elmish.*` module declaration is left alone"
            <| fun () ->
                let line = "module Elmish.Program"
                let rewritten, rewrites = rewriteSource line
                Expect.equal rewritten line "the Unless guard holds"
                Expect.isEmpty rewrites "no rewrite"

            testCase "an upstream package name in prose is not a qualified access"
            <| fun () ->
                let line = "// forked from Fable.Elmish and Elmish.Navigation"
                let rewritten, _ = rewriteSource line
                Expect.equal rewritten line "`Fable.Elmish` is preceded by a dot; `Elmish.Navigation` is not carried"

                Expect.equal
                    (reviewSource line |> List.map _.RuleId)
                    [ "73-elmish-unknown-namespace" ]
                    "the sub-namespace is reported"

            testCase "an unknown Fable.Remoting namespace is reported after the known ones rewrite"
            <| fun () ->
                let text = "open Fable.Remoting.Server\nopen Fable.Remoting.AspNetCore\n"
                let rewritten, rewrites = rewriteSource text

                Expect.equal
                    rewritten
                    "open ToolUp.Remoting.Server\nopen Fable.Remoting.AspNetCore\n"
                    "only Server moved"

                Expect.equal
                    (rewrites |> List.map (fun r -> r.Line, r.RuleId))
                    [ 1, "73-remoting-namespace" ]
                    "one rewrite"

                Expect.equal
                    (reviewSource rewritten |> List.map (fun f -> f.Line, f.RuleId))
                    [ 2, "73-remoting-unknown-namespace" ]
                    "the residual namespace is the finding, on its own line"

            testCase "the Vite define rewrites with the env var"
            <| fun () ->
                let rewritten, _ =
                    rewriteSource
                        "define: { __TOOLUP_PLATFORM_MODE__: JSON.stringify(process.env.TOOLUP_PLATFORM_MODE) }"

                Expect.equal
                    rewritten
                    "define: { __TOOLUP_PLATFORM_SURFACES__: JSON.stringify(process.env.TOOLUP_PLATFORM_SURFACES) }"
                    "both spellings"

            // ── Phase 815 — the removed deprecations ──────────────────────
            //
            // Each rule below goes red on its own if the rule is dropped or
            // widened: the golden tree pins that every 815 rule fires, and
            // these pin the boundary each one must NOT cross.

            testCase "815: a named error handler in pipeline position is rewritten onto the reporter"
            <| fun () ->
                let line = "    |> Program.withErrorHandler onError"
                let rewritten, rewrites = rewriteSource line

                Expect.equal
                    rewritten
                    "    |> Program.withErrorReporter (fun ctx -> onError (ctx.Message, ctx.Exception))"
                    "the shim's own body, composed onto withErrorReporter"

                Expect.equal (rewrites |> List.map _.RuleId) [ "815-elmish-error-handler" ] "rule"
                Expect.isEmpty (reviewSource rewritten) "a rewritten site is not also reported"

            testCase "815: an inline lambda, or the program applied on the line, is reported and never rewritten"
            <| fun () ->
                for line in
                    [
                        "    |> Program.withErrorHandler (fun (text, ex) -> printfn \"%s: %A\" text ex)"
                        "let p = Program.withErrorHandler onError program"
                    ] do
                    let rewritten, rewrites = rewriteSource line
                    Expect.equal rewritten line (sprintf "not rewritten: %s" line)
                    Expect.isEmpty rewrites "no rewrite record"

                    Expect.equal
                        (reviewSource line |> List.map _.RuleId)
                        [ "815-elmish-error-handler-inline" ]
                        (sprintf "reported for reshaping: %s" line)

            testCase "815: withConsoleTrace becomes the withTrace hook over safeMsgRepr"
            <| fun () ->
                let rewritten, rewrites = rewriteSource "    |> Program.withConsoleTrace"

                Expect.stringStarts rewritten "    |> Program.withTrace (fun msg model _ ->" "the withTrace hook"
                Expect.stringContains rewritten "Program.safeMsgRepr msg" "bounded repr of the message"
                Expect.stringContains rewritten "Program.safeMsgRepr model" "bounded repr of the model"
                Expect.equal (rewrites |> List.map _.RuleId) [ "815-elmish-console-trace" ] "rule"

            testCase "815: the compat module names move to the standalone bindings, and their real neighbours do not"
            <| fun () ->
                let rewritten, rewrites =
                    rewriteSource "open ToolUp.Platform.AgGrid\nopen ToolUp.Platform.AgChart\n"

                Expect.equal rewritten "open Feliz.AgGrid\nopen Feliz.AgCharts\n" "both opens move"

                Expect.equal
                    (rewrites |> List.map _.RuleId)
                    [ "815-aggrid-compat-module"; "815-agchart-compat-module" ]
                    "one rule each"

                for untouched in
                    [
                        "open ToolUp.Platform.AgGridEnterprise"
                        "open ToolUp.Platform.AgChartExport"
                        "module MyApp.AgGrid"
                    ] do
                    let same, none = rewriteSource untouched
                    Expect.equal same untouched (sprintf "still real, left alone: %s" untouched)
                    Expect.isEmpty none "no rewrite"

            testCase "815: ThemeClass and makePermissionGuardedApi are reported, never rewritten"
            <| fun () ->
                let theme = "        prop.className ThemeClass.BalhamDark"

                let guarded =
                    "let handler = RemotingHelpers.makePermissionGuardedApi \"Sku\" apiFactory"

                for line, rule in
                    [
                        theme, "815-aggrid-theme-class"
                        guarded, "815-remoting-permission-guarded-api"
                    ] do
                    let rewritten, rewrites = rewriteSource line
                    Expect.equal rewritten line (sprintf "the target depends on the site: %s" line)
                    Expect.isEmpty rewrites "no rewrite"
                    Expect.equal (reviewSource line |> List.map _.RuleId) [ rule ] (sprintf "reported under %s" rule)

            testCase "rule ids are unique and every rule carries its one-line account"
            <| fun () ->
                let ids =
                    (Codemod.rewriteRules |> List.map _.Id) @ (Codemod.reviewRules |> List.map _.Id)

                Expect.equal (List.distinct ids) ids "no duplicate rule id"

                for r in Codemod.rewriteRules do
                    Expect.isNotEmpty r.Summary (sprintf "%s has a summary" r.Id)

                for r in Codemod.reviewRules do
                    Expect.isNotEmpty r.Guidance (sprintf "%s has guidance" r.Id)

            testCase "every rewrite rule fires at least once on the golden tree"
            <| fun () ->
                withScratchTree (fun root ->
                    let plan = Codemod.plan root
                    let fired = plan.Files |> List.collect _.Rewrites |> List.map _.RuleId |> Set.ofList

                    for r in Codemod.rewriteRules do
                        Expect.isTrue (fired.Contains r.Id) (sprintf "%s is exercised by before/" r.Id))

            testCase "every review rule fires at least once on the golden tree"
            <| fun () ->
                withScratchTree (fun root ->
                    let plan = Codemod.plan root
                    let fired = plan.Files |> List.collect _.Findings |> List.map _.RuleId |> Set.ofList

                    for r in Codemod.reviewRules do
                        Expect.isTrue (fired.Contains r.Id) (sprintf "%s is exercised by before/" r.Id))
        ]

        testList "encoding" [
            testCase "a UTF-8 BOM and CRLF terminators survive a rewrite"
            <| fun () ->
                let temp =
                    Path.Combine(Path.GetTempPath(), "toolup-codemod-" + Guid.NewGuid().ToString "N")

                Directory.CreateDirectory temp |> ignore

                try
                    let path = Path.Combine(temp, "Crlf.fs")

                    let bytes =
                        Array.append
                            [| 0xEFuy; 0xBBuy; 0xBFuy |]
                            (Encoding.UTF8.GetBytes "open Elmish\r\nopen Fable.Remoting.Server\r\nlet x = 1\r\n")

                    File.WriteAllBytes(path, bytes)
                    let plan = Codemod.plan temp
                    Codemod.apply plan |> ignore
                    let written = File.ReadAllBytes path

                    Expect.equal written[0..2] [| 0xEFuy; 0xBBuy; 0xBFuy |] "the BOM is written back"

                    Expect.equal
                        (Encoding.UTF8.GetString(written, 3, written.Length - 3))
                        "open ToolUp.Elmish\r\nopen ToolUp.Remoting.Server\r\nlet x = 1\r\n"
                        "CRLF preserved on every line, changed or not"
                finally
                    Directory.Delete(temp, true)

            testCase "a file without a BOM does not gain one"
            <| fun () ->
                withScratchTree (fun root ->
                    Codemod.plan root |> Codemod.apply |> ignore
                    let bytes = File.ReadAllBytes(Path.Combine(root, "src", "Server", "Program.fs"))
                    Expect.notEqual bytes[0] 0xEFuy "no BOM introduced")
        ]
    ]