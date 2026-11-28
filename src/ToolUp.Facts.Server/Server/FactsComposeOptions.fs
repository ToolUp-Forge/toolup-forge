// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open Fuaran.Facts
open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose

// ─── FactsComposeOptions ─────────────────────────────────────────────
//
// The facts configuration of a composition root, stated as one value:
// `FactsCompose.withOptions` takes it whole, refuses a contradictory one
// once, by the name of the field at fault, and applies every field in one
// fixed order, so the order a root happens to write its configuration in
// can no longer change what composes. Every field's default is "absent":
// `FactsComposeOptions.defaults` composes the fact store and nothing on top
// of it.

/// A replacement `IFactStore` behind the composed fact store: `Backend` names
/// the implementation for introspection and for the blob store's multi-replica
/// scale guard, and `Factory` builds the store from the container.
type FactStoreReplacement = {
    /// The implementation's name (`"postgres"`).
    Backend: string
    /// Builds the store from the built container.
    Factory: IServiceProvider -> IFactStore
}

/// Construction for `FactStoreReplacement`.
[<RequireQualifiedAccess>]
module FactStoreReplacement =
    /// The replacement `factory` builds, named `backend`.
    let create (backend: string) (factory: IServiceProvider -> IFactStore) : FactStoreReplacement = {
        Backend = backend
        Factory = factory
    }

/// The team output visibility a deployment offers: the level a team starts
/// with, and the levels a team owner may choose from. Checked when the options
/// are composed: an empty allowed set, or a default outside it, is refused.
type TeamOutputVisibilityDeclaration = {
    /// The level a team starts with.
    Default: TeamVisibilityLevel
    /// The levels a team owner may choose from.
    Allowed: TeamVisibilityLevel list
}

/// Construction for `TeamOutputVisibilityDeclaration`.
[<RequireQualifiedAccess>]
module TeamOutputVisibilityDeclaration =
    /// The declaration starting every team at `defaultLevel`, choosing from `allowed`.
    let create (defaultLevel: TeamVisibilityLevel) (allowed: TeamVisibilityLevel list) =
        {
            Default = defaultLevel
            Allowed = allowed
        }
        : TeamOutputVisibilityDeclaration

/// The deployment's dataset models and the policy resolver each caller's
/// projection of them is answered under.
type DatasetModelsDeclaration = {
    /// The policy resolver the deployment composes its gate with.
    Resolver: DisclosurePolicyResolver
    /// The registered models (`DatasetModelRegistry.register`, or `ViewRegistry.register`).
    Registry: DatasetModelRegistry
}

/// Construction for `DatasetModelsDeclaration`.
[<RequireQualifiedAccess>]
module DatasetModelsDeclaration =
    /// `registry`, answered under `resolver`.
    let create (resolver: DisclosurePolicyResolver) (registry: DatasetModelRegistry) : DatasetModelsDeclaration = {
        Resolver = resolver
        Registry = registry
    }

/// The standing fact-base coherence check: what it checks, the recurring
/// trigger it runs on, and the declared principals whose scopes it sweeps.
type CoherenceCheckSchedule = {
    /// What the check checks, and the principal its health probe reads as.
    Config: CoherenceConfig
    /// The recurring trigger the sweep runs on (a daily `CronTrigger`).
    Cadence: Trigger
    /// The principals whose scopes are swept; none schedules no sweep.
    Principals: DeclaredPrincipal list
}

/// Construction for `CoherenceCheckSchedule`.
[<RequireQualifiedAccess>]
module CoherenceCheckSchedule =
    /// The schedule of `config` on `cadence`, sweeping `principals`.
    let create
        (config: CoherenceConfig)
        (cadence: Trigger)
        (principals: DeclaredPrincipal list)
        : CoherenceCheckSchedule =
        {
            Config = config
            Cadence = cadence
            Principals = principals
        }

    /// `CoherenceConfig.defaults` on `cadence`, sweeping and health-probing the
    /// scope `principal` resolves to.
    let forPrincipal (cadence: Trigger) (principal: DeclaredPrincipal) : CoherenceCheckSchedule =
        create
            {
                CoherenceConfig.defaults with
                    HealthPrincipal = Some principal
            }
            cadence
            [ principal ]

/// The audited grounding-envelope mutation door: the composition profile it
/// enforces, and how it reads the live grounding state (`None` for a
/// composition whose grounding declarations are immutable once composed).
type GroundingEnvelopeSealDeclaration = {
    /// `Verified` refuses a mutation arriving out of path; `Standard` records it.
    Profile: CompositionProfile
    /// Re-derives the envelope from live grounding state, where there is any.
    Observe: (unit -> GroundingEnvelope) option
}

/// Construction for `GroundingEnvelopeSealDeclaration`.
[<RequireQualifiedAccess>]
module GroundingEnvelopeSealDeclaration =
    /// The door under `profile`, observing live state through `observe`.
    let create
        (profile: CompositionProfile)
        (observe: (unit -> GroundingEnvelope) option)
        : GroundingEnvelopeSealDeclaration =
        { Profile = profile; Observe = observe }

/// The facts configuration of a composition root, composed onto a
/// `ServerApp` by `FactsCompose.withOptions`. Each field is one thing the
/// fact tier can compose over the store; its default is absent, and
/// `FactsComposeOptions.defaults` composes the store alone.
///
/// The fields apply in one fixed order whatever order they are written in:
/// the store and its replacement, the gate's facets, the table writer, the
/// decorations over the store and the writer, the services over them, the
/// tools, the withholding surfaces, and the grounding-envelope seal last, so
/// the seal covers everything the options compose.
type FactsComposeOptions = {
    /// Turn the fact store on (`ServerConfig.Stores.FactStore = FeatureMode.Enabled`)
    /// before composing, so the options do not depend on a `withConfig` having
    /// run first. `false` composes only where the configuration enables the store.
    EnableStore: bool
    /// Replace the composed `IFactStore`, keeping every registration built over
    /// it (`ToolUp.FactStores.Postgres` is the shipped replacement).
    StoreImplementation: FactStoreReplacement option
    /// The answer planner's question compiler, outranking a registered
    /// `IAIProvider` and the AI tier's `IAIProviderFactory`.
    /// `Some AnswerPlanner.noQuestionCompiler` turns the push door off on
    /// purpose. Registered whatever the fact-store knob says.
    QuestionCompiler: QuestionCompiler option
    /// Purpose-bound disclosure: the purpose taxonomy and the per-surface
    /// allowed sets, projected into the composition manifest.
    DisclosurePurposes: DisclosurePurposeConfig option
    /// Declassification budgets and the ledger they account through. An
    /// accounting control, not a differential-privacy guarantee
    /// (`DeclassificationBudget.fs`).
    DeclassificationBudgets: DeclassificationBudgetConfig option
    /// How many fact reads one disclosure check holds in flight; a bound below
    /// 1 is raised to 1. `None` runs the gate at
    /// `DisclosureFanOut.DefaultMaxConcurrentReads`.
    DisclosureFanOut: int option
    /// The default `IFactTableWriter`, with every declared fact table bound to it.
    FactTableWriter: bool
    /// The declared fact tables held as delegate facts: kept as rows, one
    /// delegate record per column, and a read of a delegated metric answered
    /// from the table.
    DelegateFacts: string list
    /// The coverage-narrative trigger over the composed store. The options
    /// name the principal the narratives are judged against, which must be a
    /// floor on what the scope may see.
    CoverageNarratives: CoverageNarrative.CoverageNarrativeOptions option
    /// The once-per-delivery narrative trigger: which runs a table's commit
    /// enqueues. Needs a table writer (`FactTableWriter` or `DelegateFacts`).
    DeliveryTrigger: (string -> string list) option
    /// Tolerance triggers over the composed writer. Needs a table writer
    /// (`FactTableWriter` or `DelegateFacts`).
    ToleranceTriggers: ToleranceTrigger list
    /// Team output visibility: the gate's viewer-aware facet, the middleware
    /// that establishes the request's viewer, and the source of each team's
    /// chosen level.
    TeamOutputVisibility: TeamOutputVisibilityDeclaration option
    /// Team-to-team fact publication: the consolidation tables publications
    /// may be written into and the signing profile.
    FactPublication: FactPublicationConfig option
    /// The certificate-verified fact import door, with the peers it accepts
    /// facts from. `Some []` composes a door that trusts nobody, which the
    /// audit trail tells apart from no door at all.
    FactImport: PeerTrustAnchor list option
    /// The standing fact-base coherence check.
    CoherenceChecks: CoherenceCheckSchedule option
    /// The deployment's authorised narrative calculations, which the grounding
    /// gate derives every `calc:` reference against.
    NarrativeCalculations: NarrativeCalculations option
    /// The pipeline profile `evaluate_pipeline` reads every pipeline against.
    PipelineProfile: RegisteredPipelineProfile option
    /// "Spec in, Facts out" tools (`FactsComposeOptions.specTool`), declared
    /// beside the fact tools.
    SpecTools: (AIToolDefinition * (HttpContext -> string -> Async<string>)) list
    /// The registered methods (`MethodRegistry.register`): one tool per card
    /// and `describe_method`.
    Methods: MethodProfile option
    /// The dataset models and `describe_dataset_model`.
    DatasetModels: DatasetModelsDeclaration option
    /// What `list_fact_tables` lists.
    FactTables: FactTablesOptions option
    /// Withholding surfaces for the startup disclosure finding, as (what the
    /// finding calls it, the egress door it asks the gate at).
    WithholdingSurfaces: (string * FactEgressSurface) list
    /// The audited grounding-envelope mutation door, sealed to the envelope the
    /// app declares once every other field has composed.
    GroundingEnvelopeSeal: GroundingEnvelopeSealDeclaration option
}

/// The default and the whole-record checks for `FactsComposeOptions`.
[<RequireQualifiedAccess>]
module FactsComposeOptions =

    /// Every field absent: the fact store, where the configuration enables it,
    /// and nothing on top of it.
    let defaults: FactsComposeOptions = {
        EnableStore = false
        StoreImplementation = None
        QuestionCompiler = None
        DisclosurePurposes = None
        DeclassificationBudgets = None
        DisclosureFanOut = None
        FactTableWriter = false
        DelegateFacts = []
        CoverageNarratives = None
        DeliveryTrigger = None
        ToleranceTriggers = []
        TeamOutputVisibility = None
        FactPublication = None
        FactImport = None
        CoherenceChecks = None
        NarrativeCalculations = None
        PipelineProfile = None
        SpecTools = []
        Methods = None
        DatasetModels = None
        FactTables = None
        WithholdingSurfaces = []
        GroundingEnvelopeSeal = None
    }

    /// A spec tool as a `SpecTools` entry: its declaration and the door's executor.
    let specTool (tool: SpecTool<'Spec, 'Refusal, 'Result>) = tool.Definition, SpecTool.executor tool

    /// The tools the options declare beside the fact tools, as (field, tool
    /// name), in the order they compose.
    let internal declaredTools (options: FactsComposeOptions) : (string * string) list = [
        for definition, _ in options.SpecTools do
            "SpecTools", definition.Name
        match options.Methods with
        | Some profile when not profile.Methods.IsEmpty ->
            for tool in profile.Tools do
                "Methods", tool.Definition.Name
        | _ -> ()
        match options.DatasetModels with
        | Some declared when not declared.Registry.Models.IsEmpty -> "DatasetModels", DatasetModelTools.Name
        | _ -> ()
    ]

    /// The refusals of `options` taken as a whole, given whether the app it is
    /// composed onto already composes a table writer and which tool names it
    /// already declares; the first is what `FactsCompose.withOptions` raises.
    let internal refusalsOver
        (writerComposed: bool)
        (declared: Set<string>)
        (options: FactsComposeOptions)
        : CompositionRefusal list =
        let field name = "FactsComposeOptions." + name

        let hasWriter =
            writerComposed || options.FactTableWriter || not options.DelegateFacts.IsEmpty

        let needsWriter name =
            CompositionRefusal.MissingDependency(
                field name,
                "IFactTableWriter",
                "set FactTableWriter = true, or hold the tables it watches as DelegateFacts"
            )

        let duplicateTools =
            declaredTools options
            |> List.fold
                (fun (seen: Set<string>, twice) (owner, name) ->
                    if seen.Contains name then
                        seen, twice @ [ owner, name ]
                    else
                        seen.Add name, twice)
                (declared, [])
            |> snd
            |> List.distinct

        [
            match ToleranceTrigger.malformed options.ToleranceTriggers with
            | problem :: _ -> CompositionRefusal.InvalidDeclaration(field "ToleranceTriggers", problem)
            | [] -> ()

            match options.TeamOutputVisibility with
            | Some declared ->
                match TeamOutputVisibilitySettings.create declared.Default declared.Allowed with
                | Error message -> CompositionRefusal.InvalidDeclaration(field "TeamOutputVisibility", message)
                | Ok _ -> ()
            | None -> ()

            if options.DeliveryTrigger.IsSome && not hasWriter then
                needsWriter "DeliveryTrigger"

            if not options.ToleranceTriggers.IsEmpty && not hasWriter then
                needsWriter "ToleranceTriggers"

            for owner, name in duplicateTools do
                CompositionRefusal.InvalidDeclaration(field owner, sprintf "a tool named '%s' is already declared" name)
        ]

    /// The refusals of `options` taken as a whole, composed onto an app that
    /// composes no table writer and declares no tool: a delivery or tolerance
    /// trigger with no table writer, a malformed trigger or visibility
    /// declaration, and a tool declared twice. Empty when the options compose.
    let refusals (options: FactsComposeOptions) : CompositionRefusal list = refusalsOver false Set.empty options