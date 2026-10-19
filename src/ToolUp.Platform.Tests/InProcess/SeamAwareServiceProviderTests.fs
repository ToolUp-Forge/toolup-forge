module ToolUp.Platform.Tests.InProcess.SeamAwareServiceProviderTests

open Expecto
open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.DeploymentVerification

// ─── Phase 770 — the seam-aware service provider ──────────────────────
//
// Phase 691 checks the seams a module's registrations imply; this pins
// the runtime half that makes the grant bind what a module's code
// RESOLVES. In the order the phase's acceptance states it:
//
//   1. **Refusal.** A module resolving a substrate seam outside its grant
//      by hand gets the framework's own "not registered" result, and the
//      Phase 688 denial reaches the audit path.
//   2. **Transparency.** A granted resolution is the raw provider's own
//      instance, at the raw provider's own lifetime, and a non-seam type
//      is never intercepted.
//   3. **The additive floor.** With no confinement composed — or one over
//      `SeamAuthorityGate.disabled` — a module's handler sees the request's
//      own provider, the same object, so nothing changes.
//   4. **The wiring.** The confinement reaches module code through
//      `ServerApp.addModule` with no opt-in, for route handlers and AI
//      tools alike.
//   5. **The report.** Observed reach sits beside derived reach, a
//      component reaching beyond its derivation is a
//      `SeamAuthorityObservedBeyondDerived` finding, and the hand-resolution
//      not-proved statement narrows only when confinement is composed.

// ── fixtures ──────────────────────────────────────────────────────────

/// Stand-ins for two substrate seams. The seam id space is interface
/// NAMES (Phase 688), so an interface named like the substrate is
/// classified exactly as the real one would be — without implementing
/// the real interface's whole surface in a test.
module Seams =
    type IAIProvider =
        abstract Name: string

    type IBlobStorage =
        abstract Name: string

/// A service that is not a substrate seam.
type INotASeam =
    abstract Name: string

type private Named(name: string) =
    interface Seams.IAIProvider with
        member _.Name = name

    interface Seams.IBlobStorage with
        member _.Name = name

    interface INotASeam with
        member _.Name = name

/// A per-scope object, to show lifetime is preserved.
type ScopedThing() =
    member val Id = Guid.NewGuid()

type private RecordingAuditLog() =
    let recorded = ResizeArray<AuditEvent>()
    let gate = obj ()
    member _.Events = lock gate (fun () -> List.ofSeq recorded)

    /// The deny observer writes fire-and-forget, so a test asserting on
    /// rows waits for them; event-driven, capped, and loud on timeout.
    member _.WaitFor(count: int) =
        let cap = TimeSpan.FromSeconds 30.0
        let sw = Diagnostics.Stopwatch.StartNew()

        lock gate (fun () ->
            while recorded.Count < count && sw.Elapsed < cap do
                Threading.Monitor.Wait(gate, cap - sw.Elapsed) |> ignore

            if recorded.Count < count then
                failtestf "audit wait: %d of %d expected row(s) arrived" recorded.Count count)

    interface IAuditLog with
        member _.Record(_, audit) = async {
            lock gate (fun () ->
                recorded.Add audit
                Threading.Monitor.PulseAll gate)
        }

        member _.GetAuditTrail(_, _, _) = async { return lock gate (fun () -> List.ofSeq recorded) }

let private reporter = ComponentId.ofModule "reporter"

let private effecting =
    CompanionCapability.identity
    |> CompanionCapability.withEffect Effecting
    |> CompanionCapability.withDeterminism DeterminismSource.externalState

let private signature: CapabilitySignature = Map.ofList [ reporter, effecting ]

/// `reporter` may reach blob storage and nothing else.
let private blobOnly: SeamGrantSignature =
    Map.ofList [ reporter, SeamGrant.ofInterfaces [ "IBlobStorage" ] ]

let private provider () : IServiceProvider =
    let shared = Named "shared"

    ServiceCollection()
        .AddSingleton<Seams.IAIProvider>(shared)
        .AddSingleton<Seams.IBlobStorage>(shared)
        .AddSingleton<INotASeam>(shared)
        .AddScoped<ScopedThing>()
        .BuildServiceProvider()
    :> IServiceProvider

let private confinementOver (gate: ISeamAuthorityGate) =
    SeamConfinement(gate, ModuleSurface.substrateSeams, SeamReachObserver())

let private verifiedGate (audit: IAuditLog) : ISeamAuthorityGate =
    match
        VerifiedCompositionProfile.auditedSeamGate
            audit
            "scope-1"
            CompositionProfile.Verified
            (Some signature)
            (Some blobOnly)
    with
    | Ok gate -> gate
    | Error refusal -> failtestf "fixture gate refused: %A" refusal

/// A request whose services are `services` plus, optionally, an
/// enforcing confinement — what `SeamAuthorityEnforcement.confine`
/// registers.
let private requestOver (confinement: SeamConfinement option) : DefaultHttpContext =
    let collection =
        ServiceCollection()
            .AddSingleton<Seams.IAIProvider>(Named "ai")
            .AddSingleton<Seams.IBlobStorage>(Named "blob")
            .AddSingleton<INotASeam>(Named "plain")

    match confinement with
    | Some c ->
        collection.AddSingleton<SeamConfinement>(c).AddSingleton<SeamReachObserver>(c.Observer)
        |> ignore
    | None -> ()

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- collection.BuildServiceProvider()
    ctx

let private run (handler: Giraffe.Core.HttpHandler) (ctx: HttpContext) =
    handler (fun c -> Threading.Tasks.Task.FromResult(Some c)) ctx
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> ignore

// ── 1. refusal ────────────────────────────────────────────────────────

let private refusal =
    testList "refusal" [

        testCase "an ungranted seam resolved by hand is the framework's own 'not registered'"
        <| fun _ ->
            let confined =
                SeamAwareServiceProvider(provider (), reporter, confinementOver (verifiedGate (RecordingAuditLog())))

            Expect.isNull
                (confined.GetService typeof<Seams.IAIProvider>)
                "a seam outside the grant must resolve as absent, not as a partial instance"

            Expect.throwsT<InvalidOperationException>
                (fun () -> (confined :> IServiceProvider).GetRequiredService<Seams.IAIProvider>() |> ignore)
                "GetRequiredService must fail exactly as for an unregistered service"

            Expect.isNull
                (confined.GetService typeof<Collections.Generic.IEnumerable<Seams.IAIProvider>>)
                "GetServices asks for IEnumerable<seam>; it is the same seam and must be refused the same way"

        testCase "the refusal carries the Phase 688 denial onto the audit path"
        <| fun _ ->
            let audit = RecordingAuditLog()

            let confined =
                SeamAwareServiceProvider(provider (), reporter, confinementOver (verifiedGate audit))

            confined.GetService typeof<Seams.IAIProvider> |> ignore
            audit.WaitFor 1

            match audit.Events with
            | [ AuditEvent.CompositionCapabilityRefused _ ] -> ()
            | other -> failtestf "expected exactly one CompositionCapabilityRefused row, got %A" other

        testCase "the provider cannot be stepped around by asking for the container"
        <| fun _ ->
            let confined =
                SeamAwareServiceProvider(provider (), reporter, confinementOver (verifiedGate (RecordingAuditLog())))

            Expect.isTrue
                (obj.ReferenceEquals(confined.GetService typeof<IServiceProvider>, confined))
                "asking for IServiceProvider must hand back the confined provider, not the raw one"

            use scope =
                (confined :> IServiceProvider).GetRequiredService<IServiceScopeFactory>().CreateScope()

            Expect.isNull
                (scope.ServiceProvider.GetService typeof<Seams.IAIProvider>)
                "a scope created from inside the confinement must be confined to the same grant"
    ]

// ── 2. transparency ───────────────────────────────────────────────────

let private transparency =
    testList "transparency" [

        testCase "a granted resolution is the raw provider's own instance"
        <| fun _ ->
            let raw = provider ()

            let confined =
                SeamAwareServiceProvider(raw, reporter, confinementOver (verifiedGate (RecordingAuditLog())))

            Expect.isTrue
                (obj.ReferenceEquals(
                    confined.GetService typeof<Seams.IBlobStorage>,
                    raw.GetService typeof<Seams.IBlobStorage>
                ))
                "a granted seam must be the same object the raw provider hands out"

            Expect.isTrue
                (obj.ReferenceEquals(confined.GetService typeof<INotASeam>, raw.GetService typeof<INotASeam>))
                "a type that is not a substrate seam is never intercepted"

        testCase "a granted resolution keeps the raw provider's lifetime"
        <| fun _ ->
            use scope = (provider ()).CreateScope()

            let confined =
                SeamAwareServiceProvider(
                    scope.ServiceProvider,
                    reporter,
                    confinementOver (verifiedGate (RecordingAuditLog()))
                )

            let viaConfined = confined.GetService typeof<ScopedThing> :?> ScopedThing
            let viaRaw = scope.ServiceProvider.GetService typeof<ScopedThing> :?> ScopedThing

            Expect.equal viaConfined.Id viaRaw.Id "a scoped service must be the scope's one instance either way"

        testCase "granted seams are observed; refused ones are not"
        <| fun _ ->
            let confinement = confinementOver (verifiedGate (RecordingAuditLog()))
            let confined = SeamAwareServiceProvider(provider (), reporter, confinement)
            confined.GetService typeof<Seams.IBlobStorage> |> ignore
            confined.GetService typeof<Seams.IAIProvider> |> ignore
            confined.GetService typeof<INotASeam> |> ignore

            Expect.equal
                (confinement.Observer.Snapshot())
                (Map.ofList [ reporter, [ SeamId.ofInterface "IBlobStorage" ] ])
                "observed reach is the seams actually handed over — not a refusal, not a non-seam"
    ]

// ── 3. the additive floor ─────────────────────────────────────────────

let private floor =
    testList "additive floor" [

        testCase "with no confinement composed the handler sees the request's own provider"
        <| fun _ ->
            let ctx = requestOver None
            let original = ctx.RequestServices
            let mutable seen: IServiceProvider = null

            let handler: Giraffe.Core.HttpHandler =
                fun next c ->
                    seen <- c.RequestServices
                    next c

            run (SeamAwareServiceProvider.confineHandler reporter handler) ctx

            Expect.isTrue (obj.ReferenceEquals(seen, original)) "Standard must hand the module the same provider object"

        testCase "a confinement over the disabled gate is the same passthrough"
        <| fun _ ->
            let ctx = requestOver (Some(confinementOver SeamAuthorityGate.disabled))
            let original = ctx.RequestServices
            let mutable seen: IServiceProvider = null

            let handler: Giraffe.Core.HttpHandler =
                fun next c ->
                    seen <- c.RequestServices
                    next c

            run (SeamAwareServiceProvider.confineHandler reporter handler) ctx

            Expect.isTrue
                (obj.ReferenceEquals(seen, original))
                "SeamAuthorityGate.disabled must not build a provider at all"

        testCase "confine over the disabled gate leaves the app untouched"
        <| fun _ ->
            let app = ServerApp.empty
            let confined = SeamAuthorityEnforcement.confine SeamAuthorityGate.disabled app

            Expect.isTrue
                (obj.ReferenceEquals(confined.Extensions.ServiceConfig, app.Extensions.ServiceConfig))
                "the disabled gate must register nothing"
    ]

// ── 4. the wiring through addModule ───────────────────────────────────

let private toolDefinition: AIToolDefinition = {
    Name = "reporter.render"
    Description = "confinement fixture tool"
    Parameters = []
    SourceModule = "reporter"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = UndeclaredEffects
}

/// What resolving `serviceType` through `provider` produced, as text.
let private probe (services: IServiceProvider) (serviceType: Type) : string =
    match services.GetService serviceType with
    | null -> "absent"
    | _ -> "present"

let private reporterModule () : ServerModule =
    let handler: Giraffe.Core.HttpHandler =
        fun next ctx ->
            ctx.Items["ai"] <- box (probe ctx.RequestServices typeof<Seams.IAIProvider>)
            ctx.Items["blob"] <- box (probe ctx.RequestServices typeof<Seams.IBlobStorage>)
            next ctx

    ServerModule.create "reporter"
    |> ServerModule.withHandlers [ handler ]
    |> ServerModule.withAITools [
        toolDefinition,
        fun ctx _ -> async {
            return
                sprintf
                    "%s/%s"
                    (probe ctx.RequestServices typeof<Seams.IAIProvider>)
                    (probe ctx.RequestServices typeof<Seams.IBlobStorage>)
        }
    ]

/// Build the request the composed app would serve, with the services the
/// app's `ServiceConfig` registers.
let private requestFor (app: ServerApp) : DefaultHttpContext =
    let collection =
        ServiceCollection().AddSingleton<Seams.IAIProvider>(Named "ai").AddSingleton<Seams.IBlobStorage>(Named "blob")

    let configured =
        match app.Extensions.ServiceConfig with
        | Some configure -> configure collection
        | None -> collection

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- configured.BuildServiceProvider()
    ctx

let private wiring =
    testList "wiring through addModule" [

        testCase "a module's route handler is confined with no opt-in, and the request is restored"
        <| fun _ ->
            let app =
                ServerApp.empty
                |> ServerApp.addModule (reporterModule ())
                |> SeamAuthorityEnforcement.confine (verifiedGate (RecordingAuditLog()))

            let ctx = requestFor app
            let original = ctx.RequestServices
            run (List.exactlyOne app.Handlers) ctx

            Expect.equal
                (ctx.Items["ai"])
                (box "absent")
                "the ungranted seam must be refused inside the module's handler"

            Expect.equal (ctx.Items["blob"]) (box "present") "the granted seam must resolve"

            Expect.isTrue
                (obj.ReferenceEquals(ctx.RequestServices, original))
                "the request's own provider must be back once the handler returns"

        testCase "the same module under Standard is unchanged"
        <| fun _ ->
            let app = ServerApp.empty |> ServerApp.addModule (reporterModule ())
            let ctx = requestFor app
            run (List.exactlyOne app.Handlers) ctx

            Expect.equal (ctx.Items["ai"]) (box "present") "with nothing composed every service resolves as before"
            Expect.equal (ctx.Items["blob"]) (box "present") "with nothing composed every service resolves as before"

        testCase "a module's AI tool is confined through its own view of the request"
        <| fun _ ->
            let app =
                ServerApp.empty
                |> ServerApp.addModule (reporterModule ())
                |> SeamAuthorityEnforcement.confine (verifiedGate (RecordingAuditLog()))

            let ctx = requestFor app
            let original = ctx.RequestServices
            let _, execute = List.exactlyOne app.AITools
            let result = execute ctx "{}" |> Async.RunSynchronously

            Expect.equal result "absent/present" "the tool must see the module's grant"

            Expect.isTrue
                (obj.ReferenceEquals(ctx.RequestServices, original))
                "a tool must never swap the shared request: tools run concurrently"

        testCase "a refusal never throws out of an un-required resolution, and nothing leaks between modules"
        <| fun _ ->
            let other =
                ServerModule.create "other"
                |> ServerModule.withHandlers [
                    fun next ctx ->
                        ctx.Items["other-ai"] <- box (probe ctx.RequestServices typeof<Seams.IAIProvider>)
                        next ctx
                ]

            let app =
                ServerApp.empty
                |> ServerApp.addModules [ reporterModule (); other ]
                |> SeamAuthorityEnforcement.confine (verifiedGate (RecordingAuditLog()))

            let ctx = requestFor app

            for handler in app.Handlers do
                run handler ctx

            Expect.equal (ctx.Items["ai"]) (box "absent") "reporter is held to its grant"

            Expect.equal
                (ctx.Items["other-ai"])
                (box "present")
                "a module absent from the grant signature resolves to UnrestrictedSeams (GP 11)"
    ]

// ── 5. the report ─────────────────────────────────────────────────────

let private integrity (observed: SeamId list option) : SeamAuthorityIntegrity = {
    Profile = "verified"
    DeclarationMandatory = true
    Components = [
        {
            AuthorityComponent = reporter
            DeclaredGrant = SeamGrant.ofInterfaces [ "IBlobStorage"; "IJobScheduler" ]
            DerivedReach = [ SeamId.ofInterface "IBlobStorage" ]
            ComposedHere = true
            ObservedReach = observed
        }
    ]
    Verification = SeamAuthorityAdmitted(1, 1)
}

let private sectionsFor (evidence: IDeploymentVerificationEvidence) = [
    DeploymentVerificationReport.gatherSeamAuthority evidence
]

let private subsetClaim (sections: ReportSection list) =
    DeploymentVerificationReport.notProvedFor sections
    |> List.find (fun s -> s.Id = "seam-reach-is-a-subset-claim")

let private report =
    testList "report" [

        testCase "the observer's snapshot is overlaid at report time"
        <| fun _ ->
            let observer = SeamReachObserver()
            observer.Record(reporter, SeamId.ofInterface "IJobScheduler")

            let services =
                ServiceCollection()
                    .AddSingleton<IDeploymentVerificationEvidence>(
                        DeploymentVerificationEvidence.none
                        |> DeploymentVerificationEvidence.withSeamAuthority (Some(integrity None))
                    )
                    .AddSingleton<SeamReachObserver>(observer)
                    .BuildServiceProvider()

            let resolved = DeploymentVerificationReport.resolveEvidence services

            match DeploymentVerificationEvidence.seamAuthorityOf resolved with
            | Some found ->
                Expect.equal
                    (List.exactlyOne found.Components).ObservedReach
                    (Some [ SeamId.ofInterface "IJobScheduler" ])
                    "the report must read the observer live, not the root's boot-time value"
            | None -> failtest "the seam-authority member must survive the overlay"

        testCase "observed reach beyond the derived reach is a finding, not a refusal"
        <| fun _ ->
            let evidence =
                DeploymentVerificationEvidence.none
                |> DeploymentVerificationEvidence.withSeamAuthority (
                    Some(integrity (Some [ SeamId.ofInterface "IBlobStorage"; SeamId.ofInterface "IJobScheduler" ]))
                )

            let section = DeploymentVerificationReport.gatherSeamAuthority evidence

            match section.Verdict with
            | VerificationSectionVerdict.Verified _ -> ()
            | other -> failtestf "an observation must not change the verdict, got %A" other

            Expect.exists
                section.Findings
                (fun f ->
                    f.StartsWith(
                        DeploymentVerificationReport.SeamAuthorityObservedBeyondDerived,
                        StringComparison.Ordinal
                    )
                    && f.Contains "{IJobScheduler}")
                "the seam the derivation could not see must be named"

            Expect.exists
                section.Findings
                (fun f -> f.Contains "observed {IBlobStorage,IJobScheduler}")
                "the component line must set observed reach beside derived reach"

        testCase "the hand-resolution statement narrows only when confinement is composed"
        <| fun _ ->
            let withoutConfinement =
                DeploymentVerificationEvidence.none
                |> DeploymentVerificationEvidence.withSeamAuthority (Some(integrity None))
                |> sectionsFor
                |> subsetClaim

            Expect.stringContains
                withoutConfinement.Statement
                "can still resolve substrate from the container by hand"
                "with no confinement the pre-770 statement must stand verbatim"

            let withConfinement =
                DeploymentVerificationEvidence.none
                |> DeploymentVerificationEvidence.withSeamAuthority (Some(integrity (Some [])))
                |> sectionsFor
                |> subsetClaim

            Expect.stringContains
                withConfinement.Statement
                "query handlers, job handlers"
                "the narrowed statement must name the paths the provider does not cover"

            Expect.isFalse
                (withConfinement.Statement.Contains "by hand")
                "the hand-resolution clause is removed for the covered paths"
    ]

// ── the vocabulary ────────────────────────────────────────────────────

let private vocabulary =
    testList "seam vocabulary" [

        testCase "substrateSeams is every seam a module's registrations can imply"
        <| fun _ ->
            Expect.equal
                ModuleSurface.substrateSeams
                [
                    "IAIProvider"
                    "IBlobStorage"
                    "IConfigStore"
                    "IEmbeddingProvider"
                    "IFactStore"
                    "IFactTableWriter"
                    "IJobScheduler"
                    "IMetricsSink"
                    "IModuleBindingVerifier"
                    "IModuleQueryBus"
                    "IVectorStore"
                ]
                "the vocabulary is read off the same rule rows as the Needs projection"

        testCase "every derived reach lies inside the vocabulary"
        <| fun _ ->
            let m =
                ServerModule.create "tools"
                |> ServerModule.withAITools [ toolDefinition, (fun _ _ -> async { return "" }) ]

            for seam in (SeamAuthorityEnforcement.reachOf m).ReachedSeams do
                Expect.contains
                    ModuleSurface.substrateSeams
                    (SeamId.value seam)
                    "a derived seam the provider cannot see"
    ]

let tests =
    testList "Phase 770 — seam-aware service provider" [ refusal; transparency; floor; wiring; report; vocabulary ]