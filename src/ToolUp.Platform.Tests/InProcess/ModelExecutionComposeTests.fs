// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ModelExecutionComposeTests

open System
open System.IO
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore

// ─── Phase 728 — the model-execution compose leg ────────────────────────
//
// The defect this pack pins is not a behaviour but an ABSENCE: no forge
// compose path registered an `IModelRegistry`, so every registration in
// the tree lived in a test fixture and a real deployment discovered that
// from a `SubstrateDisabled` refusal on its first request.
//
// So the load-bearing property of this pack is that **it never touches the
// `ModelExecutionApiTests` fixture**. It builds the DI graph forge itself
// composes — an `IDataObjectStore` over blob storage and an `IAuditLog` —
// calls the leg, and then resolves the registry through the same
// `ModelExecutionApi` handler a request would. A green here with the
// fixtures deleted is exactly the acceptance criterion; a test that reused
// them would prove the thing that was already true.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add((scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

/// The substrate forge composes for every deployment — nothing
/// model-execution-specific, which is the point.
let private composedSubstrate () : IServiceCollection =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-modelexec-compose-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    let blob = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore

    let services = ServiceCollection() :> IServiceCollection
    services.AddSingleton<IDataObjectStore>(dataObjects) |> ignore
    services.AddSingleton<IAuditLog>(RecordingAuditLog() :> IAuditLog) |> ignore
    services.AddSingleton<ILogger>(silentLogger) |> ignore
    services

/// An `HttpContext` over a built graph, carrying the caller's resolved
/// scope exactly as `ScopeResolutionMiddleware` would.
let private ctxOver (services: IServiceCollection) (userId: string) : HttpContext =
    services.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser userId))
    |> ignore

    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx

let private countRegistrations (services: IServiceCollection) (t: Type) =
    services |> Seq.filter (fun d -> d.ServiceType = t) |> Seq.length

let private observerNamed (name: string) (seen: ResizeArray<string>) =
    { new IModelRegistrationObserver with
        member _.Name = name

        member _.OnRegistered(_, artifact) = async { seen.Add($"{name}:{artifact.CompositeKey.Hash}") }
    }

let private validate (config: ServerConfig) (services: IServiceCollection) =
    let validator =
        ModelExecutionDepsValidator.ModelExecutionDepsValidator(config, services) :> ConfigValidation.IConfigValidator

    validator.Validate() |> Async.RunSynchronously

let tests =
    testList "Phase 728 — model-execution compose leg" [

        // ── Composed: the API resolves the registry, no hand-registration ──

        testCase "composed leg — ModelExecutionApi resolves IModelRegistry with no hand-registration"
        <| fun _ ->
            let services = composedSubstrate ()
            ComposeModelExecution.register ComposeModelExecution.ModelExecutionComposeOptions.defaults services

            let ctx = ctxOver services "alice"
            let api = ModelExecutionApiHandler.modelExecutionApi ctx

            let result = api.GetOutcome "no-such-key" |> Async.RunSynchronously

            // `NotFound` — not `SubstrateDisabled` — is the whole phase:
            // the registry was resolved and answered, rather than being
            // absent from the graph.
            match result with
            | Error(ModelExecutionRefusal.NotFound what) ->
                Expect.stringContains what "no-such-key" "the refusal names the key the registry looked for"
            | other -> failtestf "expected NotFound from a composed registry, got %A" other

        testCase "composed leg — the registry is the blob-backed default over composed substrate"
        <| fun _ ->
            let services = composedSubstrate ()
            ComposeModelExecution.register ComposeModelExecution.ModelExecutionComposeOptions.defaults services

            let provider = services.BuildServiceProvider()
            let registry = provider.GetService(typeof<IModelRegistry>) :?> IModelRegistry

            Expect.isNotNull (box registry) "the leg registers an IModelRegistry"

            // It answers a query rather than merely existing.
            let page =
                registry.QueryPage("alice", ModelRegistryQuery.any, None, 10)
                |> Async.RunSynchronously

            match page with
            | Ok p -> Expect.isEmpty p.Artifacts "a fresh scope holds no artifacts"
            | Error e -> failtestf "expected an empty page from the default registry, got %A" e

        // ── Not composed: byte-parity with today ──────────────────────────

        testCase "not composed — the graph is unchanged and the API refuses exactly as before"
        <| fun _ ->
            let services = composedSubstrate ()
            let before = countRegistrations services typeof<IModelRegistry>

            let ctx = ctxOver services "alice"
            let api = ModelExecutionApiHandler.modelExecutionApi ctx

            Expect.equal before 0 "no forge path registers IModelRegistry without the leg"

            Expect.equal
                (countRegistrations services typeof<IModelRegistry>)
                0
                "composing nothing appends no registration (GP 13)"

            match api.GetOutcome "any" |> Async.RunSynchronously with
            | Error(ModelExecutionRefusal.SubstrateDisabled surface) ->
                Expect.equal surface "model registry" "the pre-728 refusal is unchanged"
            | other -> failtestf "expected the unchanged SubstrateDisabled refusal, got %A" other

        testCase "not composed — ServerApp.empty carries no model-execution leg"
        <| fun _ -> Expect.isNone ServerApp.empty.ModelExecutionCompose "the leg is absent by default (GP 13)"

        testCase "not composed — withModelExecution is the only thing that turns it on"
        <| fun _ ->
            let app =
                ServerApp.empty
                |> ServerApp.withModelExecution ComposeModelExecution.ModelExecutionComposeOptions.defaults

            Expect.isSome app.ModelExecutionCompose "the builder declares the leg"

            match app.ModelExecutionCompose with
            | Some options ->
                Expect.isNone options.Registry "the defaults build the blob-backed registry rather than carrying one"
                Expect.isNone options.Scorer "IModelScorer stays consumer-supplied by design"
                Expect.isNone options.Policy "an unset policy leaves the handler's permissive fallback in place"
                Expect.isEmpty options.Observers "no observers by default"
            | None -> failtest "expected the declared options"

        // ── The validator (728.C) ─────────────────────────────────────────

        testCase "validator — mounted API with no registry composed warns and names the builder"
        <| fun _ ->
            let services = composedSubstrate ()

            let config = {
                ServerConfig.defaults with
                    ModelExecution = EnabledModelExecutionApi
            }

            match validate config services with
            | ConfigValidation.Warning message ->
                Expect.stringContains message "IModelRegistry" "the finding names the missing registration"

                Expect.stringContains
                    message
                    ComposeModelExecution.BuilderName
                    "the finding names the builder that composes one"
            | other -> failtestf "expected a Warning for a mounted API with no registry, got %A" other

        testCase "validator — composed leg silences the finding"
        <| fun _ ->
            let services = composedSubstrate ()
            ComposeModelExecution.register ComposeModelExecution.ModelExecutionComposeOptions.defaults services

            let config = {
                ServerConfig.defaults with
                    ModelExecution = EnabledModelExecutionApi
            }

            Expect.equal (validate config services) ConfigValidation.Ok "a composed registry validates clean"

        testCase "validator — a hand-registered registry also silences it"
        <| fun _ ->
            let services = composedSubstrate ()
            let provider = services.BuildServiceProvider()

            let hand =
                BlobModelRegistry.create
                    (provider.GetService(typeof<IDataObjectStore>) :?> IDataObjectStore)
                    (provider.GetService(typeof<IAuditLog>) :?> IAuditLog)

            services.AddSingleton<IModelRegistry>(hand) |> ignore

            let config = {
                ServerConfig.defaults with
                    ModelExecution = EnabledModelExecutionApi
            }

            Expect.equal
                (validate config services)
                ConfigValidation.Ok
                "the validator asks whether a registry is composed, not who composed it"

        testCase "validator — an unmounted API validates clean whatever is composed"
        <| fun _ ->
            let services = composedSubstrate ()

            Expect.equal
                (validate ServerConfig.defaults services)
                ConfigValidation.Ok
                "NoModelExecutionApi (the default) is never a finding"

        // ── Options: overrides, TryAdd semantics, observers ───────────────

        testCase "options — a supplied registry is registered as-is"
        <| fun _ ->
            let services = composedSubstrate ()
            let provider = services.BuildServiceProvider()

            let mine =
                BlobModelRegistry.create
                    (provider.GetService(typeof<IDataObjectStore>) :?> IDataObjectStore)
                    (provider.GetService(typeof<IAuditLog>) :?> IAuditLog)

            ComposeModelExecution.ModelExecutionComposeOptions.defaults
            |> ComposeModelExecution.ModelExecutionComposeOptions.withRegistry mine
            |> fun options -> ComposeModelExecution.register options services

            let resolved =
                services.BuildServiceProvider().GetService(typeof<IModelRegistry>) :?> IModelRegistry

            // Reference equality, not equivalence: with no observers
            // `ModelRegistrationObservers.decorate` returns the same object,
            // so an unobserved deployment holds exactly what it declared.
            Expect.isTrue (Object.ReferenceEquals(resolved, mine)) "the declared registry is registered undecorated"

        testCase "options — TryAdd never overrides a pre-registered registry"
        <| fun _ ->
            let services = composedSubstrate ()
            let provider = services.BuildServiceProvider()

            let pre =
                BlobModelRegistry.create
                    (provider.GetService(typeof<IDataObjectStore>) :?> IDataObjectStore)
                    (provider.GetService(typeof<IAuditLog>) :?> IAuditLog)

            services.AddSingleton<IModelRegistry>(pre) |> ignore
            ComposeModelExecution.register ComposeModelExecution.ModelExecutionComposeOptions.defaults services

            let resolved =
                services.BuildServiceProvider().GetService(typeof<IModelRegistry>) :?> IModelRegistry

            Expect.isTrue
                (Object.ReferenceEquals(resolved, pre))
                "composing the leg over a consumer's own registry leaves theirs in place"

        testCase "options — declared and DI-registered observers are merged, deduplicated by name"
        <| fun _ ->
            let services = composedSubstrate ()
            let seen = ResizeArray<string>()

            services.AddSingleton<IModelRegistrationObserver>(observerNamed "from-di" seen)
            |> ignore

            ComposeModelExecution.ModelExecutionComposeOptions.defaults
            |> ComposeModelExecution.ModelExecutionComposeOptions.withObserver (observerNamed "declared" seen)
            // A restatement of the DI-composed observer: same name, dropped.
            |> ComposeModelExecution.ModelExecutionComposeOptions.withObserver (observerNamed "from-di" seen)
            |> fun options -> ComposeModelExecution.register options services

            let resolved =
                services.BuildServiceProvider().GetService(typeof<IModelRegistry>) :?> IModelRegistry

            // A decorated registry is NOT the blob registry it wraps, which
            // is how "observers were applied" is observable without
            // registering an artifact.
            Expect.isNotNull (box resolved) "the decorated registry resolves"

            let outcome: FitOutcome = {
                CompositeKey = FitCompositeKey.compute "spec-728" "alice/ds@v1" 7L "reference" "1.0"
                ArtifactRef = {
                    ArtifactId = "artifact-728"
                    ContentHash = "hash-728"
                    ByteLength = 4L
                }
                Diagnostics = Map.empty
                GateVerdicts = []
                DurationMs = 0L
                CostUnits = 0.0
            }

            match
                resolved.Register("alice", outcome, "alice", Map.empty, "composed leg")
                |> Async.RunSynchronously
            with
            | Ok _ -> ()
            | Error e -> failtestf "expected the composed registry to accept a registration, got %A" e

            let names = seen |> Seq.map (fun s -> s.Split(':').[0]) |> List.ofSeq

            Expect.containsAll names [ "from-di"; "declared" ] "both observers ran"
            Expect.equal (names |> List.filter ((=) "from-di") |> List.length) 1 "the duplicate name ran once"

        testCase "options — the scorer and policy are registered only when declared"
        <| fun _ ->
            let bare = composedSubstrate ()
            ComposeModelExecution.register ComposeModelExecution.ModelExecutionComposeOptions.defaults bare

            Expect.equal
                (countRegistrations bare typeof<IModelScorer>)
                0
                "IModelScorer stays consumer-supplied — the leg registers none by default"

            Expect.equal
                (countRegistrations bare typeof<ModelExecutionPolicy>)
                0
                "an undeclared policy leaves the handler's permissive fallback (GP 11)"

            let declared = composedSubstrate ()

            ComposeModelExecution.ModelExecutionComposeOptions.defaults
            |> ComposeModelExecution.ModelExecutionComposeOptions.withPolicy ModelExecutionPolicy.refuseGateFailures
            |> fun options -> ComposeModelExecution.register options declared

            Expect.equal
                (countRegistrations declared typeof<ModelExecutionPolicy>)
                1
                "a declared policy is registered once"

            let resolved =
                declared.BuildServiceProvider().GetService(typeof<ModelExecutionPolicy>) :?> ModelExecutionPolicy

            Expect.isTrue resolved.RefuseGateFailedArtifacts "the declared policy is the one the handler resolves"
    ]