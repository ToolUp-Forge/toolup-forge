module ToolUp.Platform.Tests.InProcess.ModuleSurfaceTests

open Expecto
open System
open System.Text.Json
open FSharp.Reflection
open Feliz
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.VectorisationTypes

// ─── Phase 581 — module-surface descriptor ────────────────────────────
//
// Same acceptance shape as `ComposableSurfaceTests` (Phase 293), one
// level down: the descriptor enumerates what ONE module provides and
// needs, DERIVED from its own registrations rather than hand-listed. The
// load-bearing test is the coverage diff — this file independently
// reflects the `ServerModule` / `ErasedModule` record fields and asserts
// the descriptor classifies exactly that set, so adding a registration
// field without teaching the descriptor fails here rather than silently
// producing an incomplete label.

// ── reference module: every server registration populated ────────────

type private NoopJobHandler() =
    interface IJobHandler with
        member _.Execute _ = async { return JobResult.Success }

let private dataType (id: string) (displayName: string) : DataType = {
    Info = {
        Id = id
        DisplayName = displayName
        Schema = None
    }
    Id = id
    Detect = fun _ -> async { return false }
    Process =
        fun _ -> async {
            return
                { TypeName = id; Payload = "{}" },
                {
                    FileName = ""
                    DataType = id
                    ProcessedAt = DateTime.UnixEpoch
                    Info = None
                    Error = None
                }
        }
}

let private vectorisation (id: string) : VectorisationHandler = {
    DataTypeId = id
    Vectorise = fun _ -> []
    Summarise = None
}

let private toolDefinition (name: string) : AIToolDefinition = {
    Name = name
    Description = "reference tool"
    Parameters = []
    SourceModule = "reference"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
}

let private configSchema: ModuleConfigSchema = {
    Fields = [
        {
            Key = "retention-days"
            DisplayName = "Retention (days)"
            Description = None
            Kind = ConfigFieldKind.Int(Some 1, Some 365)
            Required = false
            DefaultJson = "30"
        }
    ]
}

let private signal: Metrics.MetricDefinition = {
    Name = "reference.processed_total"
    Kind = Metrics.Counter
    Description = "records processed"
    Unit = "1"
    Tags = []
}

let private groundingMetric: Grounding.MetricDefinition = {
    Id = "revenue"
    Name = "Revenue"
    Unit = "GBP"
    Dimensionality = "currency"
    Direction = Grounding.HigherIsBetter
    DisplayFormat = "N0"
    Staleness = Grounding.UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = None
}

let private groundingSubject: Grounding.SubjectDefinition = {
    Id = "product"
    Name = "Product"
    Levels = [ "root" ]
    Calendar = None
}

/// A module that exercises every `ServerModule` registration field, so
/// the descriptor's derivation is measured against a full surface rather
/// than a convenient subset.
let private referenceModule () : ServerModule =
    ServerModule.create "Reference"
    |> ServerModule.withHandlers [ Giraffe.Core.setStatusCode 200 ]
    |> ServerModule.withDataTypes [ dataType "SalesData" "Sales Data" ]
    |> ServerModule.withVectorisation [ vectorisation "SalesData" ]
    |> ServerModule.withConfig configSchema
    |> ServerModule.withQueryHandlers [
        {
            QueryKey = "latest-analysis"
            Handle = fun _ -> async { return "" }
        }
    ]
    |> ServerModule.withAITools [ toolDefinition "reference.run", (fun _ _ -> async { return "" }) ]
    |> ServerModule.withMetrics [ signal ]
    |> ServerModule.withSlowRequestThreshold "/api/reference/slow" (TimeSpan.FromSeconds 5.0)
    |> ServerModule.withDefaultSurfaceRequirement SurfaceRequirement.userOrTeam
    |> ServerModule.withRoutePrefix "/api/reference/"
    |> ServerModule.withRouteSurfaceRequirement "post" "/api/reference/public" SurfaceRequirement.claimBearerOnly
    |> ServerModule.withJobHandler ("reference-scan", NoopJobHandler(), CronTrigger "0 8 * * *")
    |> ServerModule.withBindingStamp (MacStamp("anchor-1", "tag"))
    |> ServerModule.withComponentId "reference-service"
    |> ServerModule.declareMetrics [ groundingMetric ]
    |> ServerModule.declareSubjects [ groundingSubject ]

/// The erased client registration for the same module. `Icon` / `View`
/// are Fable render machinery and are never invoked through this path —
/// the descriptor reads declarations, not renderers.
let private referenceClient () : ErasedModule = {
    Definition = {
        Id = "Reference"
        Name = "Reference"
        Icon = Unchecked.defaultof<ReactElement>
        Pages = [
            {
                Route = "/reference/overview"
                Title = "Overview"
                Icon = Unchecked.defaultof<ReactElement>
            }
            {
                Route = "/reference/detail"
                Title = "Detail"
                Icon = Unchecked.defaultof<ReactElement>
            }
        ]
    }
    Init = fun _ -> box (), Cmd.none
    Update = fun _ state -> state, Cmd.none
    View = None
    PageViews = None
    NeedsData = Some(fun has -> has "SalesData")
    DataTypes = []
    ProvidesProcessedData = None
    ProvidesNarrative = None
    Config = Some configSchema
    FeatureFlags = [
        {
            Key = "reference.new-grid"
            DefaultValue = FlagValue.Bool false
            Description = "new grid"
            Owner = None
        }
    ]
    Availability = Always
    Group = Some "Analysis"
    // Phase 611 — a declared rail slot, so the descriptor's `placement`
    // facet has a value to report for this reference registration.
    Placement = Some Toolup.Sidebar.TrailingSlot
    NavRole = None
    Area = ModuleArea.Product
    ClientQueryHandlers = []
    ActionDecoder = Some(fun _ -> None)
    Visibility = Visibility.visibleToAll
    EventSubscriptions = Map.ofList [ "sales.refreshed", (fun (_: string) -> box ()) ]
}

/// A stand-in registration carrying one field the descriptor has never
/// heard of. Adding a field to a real registration record is exactly this
/// shape from the descriptor's point of view, so this is the empirical
/// proof that the drift guard FAILS LOUD rather than silently producing a
/// short label — without needing to mutate `ServerModule` to demonstrate
/// it.
type private RegistrationWithNewField = {
    Definition: obj
    Init: obj
    Update: obj
    View: obj
    PageViews: obj
    NeedsData: obj
    DataTypes: obj
    ProvidesProcessedData: obj
    ProvidesNarrative: obj
    Config: obj
    FeatureFlags: obj
    Availability: obj
    Group: obj
    Area: obj
    ClientQueryHandlers: obj
    ActionDecoder: obj
    Visibility: obj
    EventSubscriptions: obj
    /// The new registration the descriptor does not classify.
    WebhookSubscriptions: string list
}

// ── independent reflection (the drift-guard's other half) ────────────

/// Recompute the registration-field set straight off the record type —
/// deliberately NOT via `ModuleSurface`. Equality between this and the
/// descriptor's coverage is the "derived, not hand-listed" proof.
let private reflectedFields (t: Type) : Set<string> =
    FSharpType.GetRecordFields(t, true) |> Array.map _.Name |> Set.ofArray

let private keysOf (kind: string) (entries: ModuleSurfaceEntry list) =
    entries |> List.filter (fun e -> e.Kind = kind) |> List.map _.Key |> Set.ofList

let tests =
    testList "ModuleSurface" [

        // ── the coverage diff: the drift guard proper ─────────────────
        testCase "the descriptor classifies exactly the ServerModule registration fields"
        <| fun _ ->
            let surface = ModuleSurface.describe (referenceModule ())

            let covered =
                surface.Coverage
                |> List.filter (fun c -> c.Origin = "server")
                |> List.map _.Field
                |> Set.ofList

            Expect.equal
                covered
                (reflectedFields typeof<ServerModule>)
                "every ServerModule field is classified — adding one without teaching the descriptor fails here"

        testCase "a registration field the descriptor does not know surfaces as Unclassified"
        <| fun _ ->
            let surface = ModuleSurface.describe (referenceModule ())

            Expect.isEmpty
                surface.Unclassified
                "no ServerModule field is unclassified on a healthy build — a new field lands here until the descriptor learns it"

            Expect.isEmpty surface.Stale "no classified field has disappeared from the registration"

        testCase "the client registration is diffed the same way"
        <| fun _ ->
            let surface =
                ModuleSurface.describeWith (referenceModule (), Some(box (referenceClient ())))

            let covered =
                surface.Coverage
                |> List.filter (fun c -> c.Origin = "client")
                |> List.map (fun c -> c.Field.Substring "client:".Length)
                |> Set.ofList

            Expect.equal
                covered
                (reflectedFields typeof<ErasedModule>)
                "every ErasedModule field is classified — the Server tier reads it reflectively, so this diff is the only guard"

            Expect.isEmpty surface.Unclassified "no client registration field is unclassified"
            Expect.isEmpty surface.Stale "no client field the descriptor names has been renamed away"

        testCase "a registration field the descriptor has never heard of fails the guard"
        <| fun _ ->
            // The proof that the guard is load-bearing: a registration
            // carrying one unknown field must surface it, not quietly
            // produce a label that is missing a declaration.
            let drifted: RegistrationWithNewField = {
                Definition = null
                Init = null
                Update = null
                View = null
                PageViews = null
                NeedsData = null
                DataTypes = null
                ProvidesProcessedData = null
                ProvidesNarrative = null
                Config = null
                FeatureFlags = null
                Availability = null
                Group = null
                Area = null
                ClientQueryHandlers = null
                ActionDecoder = null
                Visibility = null
                EventSubscriptions = null
                WebhookSubscriptions = [ "billing.invoice" ]
            }

            let surface = ModuleSurface.describeWith (referenceModule (), Some(box drifted))

            Expect.equal
                surface.Unclassified
                [ "client:WebhookSubscriptions" ]
                "the unlearned registration field is named, so the drift guard fails instead of under-reporting"

        // ── provides: derived from the module's own registrations ─────
        testCase "provides enumerates the server-side declarations with zero hand-maintained lists"
        <| fun _ ->
            let surface = ModuleSurface.describe (referenceModule ())

            Expect.equal (keysOf "datatype" surface.Provides) (Set.ofList [ "SalesData" ]) "the registered data type"

            Expect.equal
                (keysOf "query" surface.Provides)
                (Set.ofList [ "latest-analysis" ])
                "the declared query-handler key"

            Expect.equal (keysOf "tool" surface.Provides) (Set.ofList [ "reference.run" ]) "the declared AI tool"
            Expect.equal (keysOf "job" surface.Provides) (Set.ofList [ "reference-scan" ]) "the declared job"
            Expect.equal (keysOf "metric" surface.Provides) (Set.ofList [ "revenue" ]) "the declared grounding metric"
            Expect.equal (keysOf "subject" surface.Provides) (Set.ofList [ "product" ]) "the declared subject hierarchy"

            Expect.equal
                (keysOf "route-prefix" surface.Provides)
                (Set.ofList [ "/api/reference/" ])
                "the declared route prefix"

            Expect.equal
                (keysOf "route" surface.Provides)
                (Set.ofList [ "POST /api/reference/public" ])
                "the exact-match route override, method-normalised"

            Expect.equal
                (keysOf "config-field" surface.Provides)
                (Set.ofList [ "retention-days" ])
                "the declared config field"

        testCase "a data type's key IS its wire TypeName, and carries the Phase 279 slot id"
        <| fun _ ->
            let surface = ModuleSurface.describe (referenceModule ())

            let dt =
                surface.Provides
                |> List.find (fun e -> e.Kind = "datatype" && e.Key = "SalesData")

            Expect.equal dt.Slot (Some(ComponentId.forDataType "SalesData")) "slot id shares the ComponentId space"
            Expect.equal dt.Label "Sales Data" "the display name rides along as the label"

        testCase "the module id is the declared ComponentId, not the display name"
        <| fun _ ->
            let surface = ModuleSurface.describe (referenceModule ())
            Expect.equal surface.Module "Reference" "the registration Name"

            Expect.equal
                surface.Component
                (ComponentId.ofModule "reference-service")
                "an explicitly declared ComponentId wins over the name-derived one"

            let nameDerived = ModuleSurface.describe (ServerModule.create "Plain")

            Expect.equal
                nameDerived.Component
                (ComponentId.ofModule "Plain")
                "a module that declares no id keeps the name-derived one (GP 11)"

        // ── needs: substrate implied by the registrations ─────────────
        testCase "needs derives substrate from what the module registers, not from a per-module list"
        <| fun _ ->
            let surface = ModuleSurface.describe (referenceModule ())
            let substrate = keysOf "substrate" surface.Needs

            Expect.isTrue
                (Set.isSubset
                    (Set.ofList [
                        "IAIProvider"
                        "IJobScheduler"
                        "IModuleQueryBus"
                        "IVectorStore"
                        "IEmbeddingProvider"
                        "IFactStore"
                        "IConfigStore"
                    ])
                    substrate)
                "each registration implies its substrate"

            Expect.equal
                (surface.Needs |> List.find (fun e -> e.Key = "IAIProvider") |> _.Slot)
                (Some(ComponentId.forCompanionSlot "IAIProvider"))
                "substrate needs key against the ComposableSurface slot-id space"

        testCase "a module that registers nothing needs nothing (GP 13)"
        <| fun _ ->
            let surface = ModuleSurface.describe (ServerModule.create "Empty")
            Expect.isEmpty surface.Needs "an empty registration implies no substrate"
            Expect.isFalse surface.ClientDescribed "no client registration was supplied"

        // ── honest about opaque registrations ─────────────────────────
        testCase "registrations with no enumerable keys are named, not guessed at"
        <| fun _ ->
            let surface =
                ModuleSurface.describeWith (referenceModule (), Some(box (referenceClient ())))

            let kinds = surface.Opaque |> List.map _.Kind |> Set.ofList

            Expect.isTrue
                (Set.isSubset (Set.ofList [ "http-handler"; "query-target"; "needs-data"; "action-key" ]) kinds)
                "the four shapes a registration cannot expose are reported explicitly"

            let needsData = surface.Opaque |> List.find (fun o -> o.Kind = "needs-data")
            Expect.equal needsData.Count 1 "the module declares a NeedsData predicate"

            Expect.isTrue
                (needsData.Reason.Contains "predicate")
                "the reason says why the accepted data-type ids are not enumerable"

        // ── client side ──────────────────────────────────────────────
        testCase "the client registration contributes pages, flag keys and event topics"
        <| fun _ ->
            let surface =
                ModuleSurface.describeWith (referenceModule (), Some(box (referenceClient ())))

            Expect.isTrue surface.ClientDescribed "a client registration was supplied"

            Expect.equal
                (keysOf "page" surface.Provides)
                (Set.ofList [ "/reference/overview"; "/reference/detail" ])
                "both declared page routes"

            Expect.equal
                (keysOf "feature-flag" surface.Needs)
                (Set.ofList [ "reference.new-grid" ])
                "the declared flag key is a need, not a provide"

            Expect.equal
                (keysOf "event-topic" surface.Needs)
                (Set.ofList [ "sales.refreshed" ])
                "the subscribed event topic is a need"

        // ── determinism + JSON projection ────────────────────────────
        testCase "describe is deterministic"
        <| fun _ ->
            let a =
                ModuleSurface.describeWith (referenceModule (), Some(box (referenceClient ())))

            let b =
                ModuleSurface.describeWith (referenceModule (), Some(box (referenceClient ())))

            Expect.equal a.Provides b.Provides "the provides list is stably ordered"
            Expect.equal a.Needs b.Needs "the needs list is stably ordered"
            Expect.equal a.Coverage b.Coverage "the coverage list is stably ordered"

        testCase "the JSON projection is deterministic and carries the declarations"
        <| fun _ ->
            let first = ModuleSurface.describeJson (referenceModule ())
            let second = ModuleSurface.describeJson (referenceModule ())
            Expect.equal first second "the same registration yields byte-identical JSON"

            // Parses as ordinary JSON — an external tool reads the label
            // without linking the server assembly.
            use parsed = JsonDocument.Parse first

            Expect.isTrue
                (parsed.RootElement.TryGetProperty("Provides") |> fst)
                "the projection carries the provides list"

            Expect.stringContains first "SalesData" "the data type's wire TypeName is in the snapshot"
            Expect.stringContains first "reference.run" "the tool name is in the snapshot"
    ]