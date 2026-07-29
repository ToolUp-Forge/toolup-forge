// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text.Json
open FSharp.Reflection
open ToolUp.Remoting.Json.SystemTextJson

// ─── Module-surface descriptor (ModuleSurface) ───────────────────────
//
// `ComposableSurface` (Phase 293) made the PLATFORM's composition
// vocabulary introspectable — what forge CAN compose. This is the same
// idea one level down: what ONE MODULE provides and needs, as data.
// It is the module's **label** — everything a composition can rely on
// without reading the module's source: the data types it registers and
// the wire `TypeName`s they carry, the query keys it answers, the AI
// tools it exposes, the routes it owns (and the surface requirement
// guarding each), the background jobs it schedules, the metrics and
// subject hierarchies it declares, the config fields it publishes, the
// pages it renders — and, on the needs side, the substrate interfaces
// its registrations imply and the client-side flag / event-topic keys
// it reads.
//
// **Derived from the registrations, never hand-listed.** Every value
// comes out of the module's own `ServerModule` (and, optionally, its
// erased client registration); every registration-field NAME comes out
// of `nameof`, so renaming a field breaks the build rather than
// silently dropping a declaration. The descriptor additionally reports
// its own coverage: `Coverage` names every registration field it knows
// how to classify, and `Unclassified` names every field the live record
// carries that it does NOT. `Unclassified` is empty on a healthy build
// — a newly-added `ServerModule` (or `ErasedModule`) field lands there
// until the descriptor learns it, which is exactly what the drift-guard
// test fails on. `Stale` is the mirror: a field the descriptor claims
// that the registration no longer declares (only reachable on the
// reflectively-read client side; the server side is `nameof`-checked by
// the compiler).
//
// **Honest about what a registration does NOT expose.** Some fields
// carry a *function*, so their keys are not enumerable at all: a
// module's HTTP `Handlers` are Giraffe closures (the route surface is
// recovered from the declared `RoutePrefixes` /
// `RouteSurfaceRequirements` instead), `NeedsData` is a predicate over
// data-type ids rather than a list of them, and `ActionDecoder` is a
// `(key, payload) -> Msg option` function rather than a declared key
// set. Those surface in `Opaque` — named, counted, with the reason —
// rather than being guessed at or quietly omitted. Outbound module
// queries are the same story from the other end: no registration field
// declares which `(TargetModule, QueryKey)` pairs a module ASKS for, so
// the needs side reports the substrate it can derive and says so.
//
// **Generic substrate (GP 9); zero cost when unused (GP 13).** The SDK
// names no module here — the shape carries only `ComponentId`s,
// interface names, and strings drawn from the module's own
// registrations — and nothing is built until a caller asks
// (`ModuleSurface.describe`). A deployment that never introspects is
// byte-for-byte unchanged.

/// How a registration field contributes to a module's surface.
type ModuleSurfaceFacet =
    /// The field declares something the module offers to a composition.
    | ProvidesFacet
    /// The field declares something the module requires from a
    /// composition (substrate, a flag key, an event topic).
    | NeedsFacet
    /// The field carries behaviour or an opaque function — real, but
    /// with no enumerable declaration to report. See `Opaque`.
    | OpaqueFacet

/// One derived declaration on a module's surface.
type ModuleSurfaceEntry = {
    /// The registration field it was derived from (`nameof`-checked on
    /// the server side; `client:`-prefixed for the erased client
    /// registration).
    Field: string
    /// The declaration family — `datatype` / `query` / `tool` / `job` /
    /// `route` / `metric` / `subject` / `config-field` / `page` /
    /// `substrate` / …
    Kind: string
    /// The declared identity: a data-type id (the wire `TypeName`), a
    /// query key, a tool name, a route prefix, a companion interface.
    Key: string
    /// Secondary human label — display name, description, admitted
    /// subject kinds. Empty when the registration declares none.
    Label: string
    /// The Phase 279 slot id, where the kind has one in that id space.
    Slot: ComponentId option
}

/// A registration field that is real but carries no enumerable
/// declaration — reported rather than guessed at.
type ModuleOpaqueSurface = {
    Field: string
    Kind: string
    /// How many such registrations the module declares (`0` when the
    /// gap is that no registration field exists for this shape at all).
    Count: int
    /// Why the keys are not enumerable.
    Reason: string
}

/// The descriptor's own coverage of one registration field — the
/// "derived, not hand-listed" proof surface.
type ModuleSurfaceCoverage = {
    Field: string
    /// `server` (the `ServerModule` record) or `client` (the erased
    /// client registration).
    Origin: string
    Facet: ModuleSurfaceFacet
}

/// One module's composable surface, as data.
type ModuleSurface = {
    /// The module's registration `Name` (its RBAC / routing key).
    Module: string
    /// The module's stable Phase 279 id — its declared `ComponentId`, or
    /// the `Name`-derived one when it declares none (GP 11).
    Component: ComponentId
    /// What the module offers a composition.
    Provides: ModuleSurfaceEntry list
    /// What the module requires from a composition.
    Needs: ModuleSurfaceEntry list
    /// Registrations with no enumerable declaration, named honestly.
    Opaque: ModuleOpaqueSurface list
    /// Every registration field the descriptor classifies.
    Coverage: ModuleSurfaceCoverage list
    /// Registration fields the live record carries that the descriptor
    /// does NOT classify. Empty on a healthy build.
    Unclassified: string list
    /// Registration fields the descriptor classifies that the live
    /// record no longer carries. Empty on a healthy build.
    Stale: string list
    /// `true` when an erased client registration was supplied — the
    /// page / flag / event-topic side of the surface is only derivable
    /// with one.
    ClientDescribed: bool
}

module ModuleSurface =

    // ── shared shaping ────────────────────────────────────────────────

    let private entry field kind key label slot : ModuleSurfaceEntry = {
        Field = field
        Kind = kind
        Key = key
        Label = label
        Slot = slot
    }

    /// A `SurfaceRequirement` rendered as a stable, sorted admit-set
    /// label (`Set<SubjectKind>` iterates in tag order, so the string is
    /// deterministic).
    let private admitLabel (requirement: SurfaceRequirement) : string =
        requirement.AcceptedSubjects |> Seq.map string |> String.concat "|"

    /// Deterministic ordering for every emitted list.
    let private ordered (entries: ModuleSurfaceEntry list) : ModuleSurfaceEntry list =
        entries |> List.sortBy (fun e -> e.Kind, e.Key, e.Field)

    // ── server side: derived from the ServerModule registration ───────

    /// Every registration field of `ServerModule`, with the facet it
    /// contributes. Field names come from `nameof`, so a rename is a
    /// compile error rather than a silent coverage hole; a newly-ADDED
    /// field is caught at runtime by the `Unclassified` diff below.
    let private serverCoverage (m: ServerModule) : ModuleSurfaceCoverage list =
        let c field facet : ModuleSurfaceCoverage = {
            Field = field
            Origin = "server"
            Facet = facet
        }

        [
            c (nameof m.Name) ProvidesFacet
            c (nameof m.Handlers) OpaqueFacet
            c (nameof m.DataTypes) ProvidesFacet
            c (nameof m.VectorisationHandlers) ProvidesFacet
            c (nameof m.ConfigSchema) ProvidesFacet
            c (nameof m.QueryHandlers) ProvidesFacet
            c (nameof m.AITools) ProvidesFacet
            c (nameof m.MetricDefinitions) ProvidesFacet
            c (nameof m.SlowRequestThresholdOverrides) ProvidesFacet
            c (nameof m.DefaultSurfaceRequirement) ProvidesFacet
            c (nameof m.RoutePrefixes) ProvidesFacet
            c (nameof m.RouteSurfaceRequirements) ProvidesFacet
            c (nameof m.JobHandlers) ProvidesFacet
            c (nameof m.BindingStamp) ProvidesFacet
            c (nameof m.ComponentId) ProvidesFacet
            c (nameof m.Metrics) ProvidesFacet
            c (nameof m.Subjects) ProvidesFacet
        ]

    let private serverProvides (m: ServerModule) : ModuleSurfaceEntry list =
        let dataTypes =
            m.DataTypes
            |> List.map (fun dt ->
                // `DataType.Id` IS the wire `TypeName` — `Process`
                // stamps it onto the emitted `ProcessedData`.
                entry (nameof m.DataTypes) "datatype" dt.Id dt.Info.DisplayName (Some(ComponentId.forDataType dt.Id)))

        let vectorisation =
            m.VectorisationHandlers
            |> List.map (fun vh ->
                entry
                    (nameof m.VectorisationHandlers)
                    "vectorisation"
                    vh.DataTypeId
                    ""
                    (Some(ComponentId.forDataType vh.DataTypeId)))

        let configFields =
            match m.ConfigSchema with
            | None -> []
            | Some schema ->
                schema.Fields
                |> List.map (fun f -> entry (nameof m.ConfigSchema) "config-field" f.Key f.DisplayName None)

        let queries =
            m.QueryHandlers
            |> List.map (fun h -> entry (nameof m.QueryHandlers) "query" h.QueryKey "" None)

        let tools =
            m.AITools
            |> List.map (fun (definition, _) ->
                entry
                    (nameof m.AITools)
                    "tool"
                    definition.Name
                    definition.Description
                    (Some(ComponentId.forTool definition.Name)))

        let signals =
            m.MetricDefinitions
            |> List.map (fun d -> entry (nameof m.MetricDefinitions) "signal" d.Name d.Description None)

        let latencyCeilings =
            m.SlowRequestThresholdOverrides
            |> Seq.map (fun kv ->
                entry (nameof m.SlowRequestThresholdOverrides) "route-latency" kv.Key (string kv.Value) None)
            |> List.ofSeq

        let surfaceDefault = [
            entry
                (nameof m.DefaultSurfaceRequirement)
                "surface-default"
                (admitLabel m.DefaultSurfaceRequirement)
                ""
                None
        ]

        let routePrefixes =
            m.RoutePrefixes
            |> List.map (fun p ->
                entry (nameof m.RoutePrefixes) "route-prefix" p (admitLabel m.DefaultSurfaceRequirement) None)

        let routeOverrides =
            m.RouteSurfaceRequirements
            |> List.map (fun ((httpMethod, path), requirement) ->
                entry
                    (nameof m.RouteSurfaceRequirements)
                    "route"
                    (httpMethod.ToUpperInvariant() + " " + path)
                    (admitLabel requirement)
                    None)

        let jobs =
            m.JobHandlers
            |> List.map (fun d -> entry (nameof m.JobHandlers) "job" d.HandlerName (string d.Trigger) None)

        let bindingStamp =
            match m.BindingStamp with
            | None -> []
            | Some stamp ->
                let kind =
                    match stamp with
                    | JwsStamp _ -> "jws"
                    | MacStamp(keyId, _) -> "mac:" + keyId

                [ entry (nameof m.BindingStamp) "binding-stamp" kind "" None ]

        let metrics =
            m.Metrics
            |> List.map (fun d -> entry (nameof m.Metrics) "metric" d.Id d.Name (Some(ComponentId.forMetric d.Id)))

        let subjects =
            m.Subjects
            |> List.map (fun d -> entry (nameof m.Subjects) "subject" d.Id d.Name (Some(ComponentId.forSubject d.Id)))

        List.concat [
            dataTypes
            vectorisation
            configFields
            queries
            tools
            signals
            latencyCeilings
            surfaceDefault
            routePrefixes
            routeOverrides
            jobs
            bindingStamp
            metrics
            subjects
        ]

    /// The substrate a module's own registrations IMPLY. Not a per-module
    /// hand-list (GP 9 — the SDK names no module): each rule keys off a
    /// registration field, so a module that registers an AI tool needs an
    /// `IAIProvider` by construction, and a module that registers nothing
    /// needs nothing.
    let private serverNeeds (m: ServerModule) : ModuleSurfaceEntry list =
        let implied field (declared: bool) (interfaces: string list) =
            if not declared then
                []
            else
                interfaces
                |> List.map (fun iface ->
                    entry field "substrate" iface ("implied by " + field) (Some(ComponentId.forCompanionSlot iface)))

        List.concat [
            implied (nameof m.DataTypes) (not m.DataTypes.IsEmpty) [ "IBlobStorage" ]
            implied (nameof m.VectorisationHandlers) (not m.VectorisationHandlers.IsEmpty) [
                "IEmbeddingProvider"
                "IVectorStore"
            ]
            implied (nameof m.ConfigSchema) m.ConfigSchema.IsSome [ "IConfigStore" ]
            implied (nameof m.QueryHandlers) (not m.QueryHandlers.IsEmpty) [ "IModuleQueryBus" ]
            implied (nameof m.AITools) (not m.AITools.IsEmpty) [ "IAIProvider" ]
            implied (nameof m.MetricDefinitions) (not m.MetricDefinitions.IsEmpty) [ "IMetricsSink" ]
            implied (nameof m.JobHandlers) (not m.JobHandlers.IsEmpty) [ "IJobScheduler" ]
            implied (nameof m.BindingStamp) m.BindingStamp.IsSome [ "IModuleBindingVerifier" ]
            implied (nameof m.Metrics) (not m.Metrics.IsEmpty) [ "IFactStore" ]
            implied (nameof m.Subjects) (not m.Subjects.IsEmpty) [ "IFactStore" ]
        ]
        |> List.distinctBy (fun e -> e.Kind, e.Key)

    let private serverOpaque (m: ServerModule) : ModuleOpaqueSurface list = [
        {
            Field = nameof m.Handlers
            Kind = "http-handler"
            Count = m.Handlers.Length
            Reason =
                "a Giraffe HttpHandler is a closure — its routes are not enumerable; "
                + "the declared route surface is RoutePrefixes / RouteSurfaceRequirements"
        }
        {
            Field = "(unregistered)"
            Kind = "query-target"
            Count = 0
            Reason =
                "no registration field declares the (TargetModule, QueryKey) pairs a module ASKS for — "
                + "outbound queries are ordinary calls through IModuleQueryBus, so the needs side "
                + "reports only the substrate the registrations imply"
        }
    ]

    // ── client side: read reflectively off the erased registration ────
    //
    // The Server tier does not (and must not) reference the Client tier,
    // so the erased client registration arrives as `obj` and is read by
    // reflection. The field-name literals below are that seam; a rename
    // on the client record shows up in `Stale` + `Unclassified` rather
    // than silently dropping a declaration.

    let private tryProp (name: string) (o: obj) : obj option =
        if isNull o then
            None
        else
            match o.GetType().GetProperty name with
            | null -> None
            | p ->
                match p.GetValue o with
                | null -> None
                | v -> Some v

    /// Unwrap an F# `option`-typed reflected value (`None` reflects as
    /// `null`, so only `Some` reaches here); pass anything else through.
    let private unwrapOption (o: obj) : obj option =
        if isNull o then
            None
        else
            let t = o.GetType()

            if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>> then
                match FSharpValue.GetUnionFields(o, t) with
                | case, [| v |] when case.Name = "Some" -> Some v
                | _ -> None
            else
                Some o

    let private elements (o: obj option) : obj list =
        match o |> Option.bind unwrapOption with
        | Some(:? System.Collections.IEnumerable as e) -> e |> Seq.cast<obj> |> List.ofSeq
        | _ -> []

    let private tryString (name: string) (o: obj) : string option =
        match tryProp name o with
        | Some(:? string as s) -> Some s
        | _ -> None

    let private caseName (o: obj) : string option =
        if isNull o then
            None
        else
            let t = o.GetType()
            let unionType = if FSharpType.IsUnion(t, true) then t else t.BaseType

            if not (isNull unionType) && FSharpType.IsUnion(unionType, true) then
                let case, _ = FSharpValue.GetUnionFields(o, unionType, true)
                Some case.Name
            else
                None

    /// The `ErasedModule` registration fields, with the facet each
    /// contributes. Literals, because the Server tier cannot name the
    /// Client tier's type — the `Stale` / `Unclassified` diff is what
    /// keeps them true.
    let private clientFieldFacets: (string * ModuleSurfaceFacet) list = [
        "Definition", ProvidesFacet
        "Init", OpaqueFacet
        "Update", OpaqueFacet
        "View", OpaqueFacet
        "PageViews", ProvidesFacet
        "NeedsData", OpaqueFacet
        "DataTypes", ProvidesFacet
        "ProvidesProcessedData", OpaqueFacet
        "ProvidesNarrative", OpaqueFacet
        "Config", ProvidesFacet
        "FeatureFlags", NeedsFacet
        "Availability", ProvidesFacet
        "Group", ProvidesFacet
        "NavRole", ProvidesFacet
        "Area", ProvidesFacet
        "ClientQueryHandlers", ProvidesFacet
        "ActionDecoder", OpaqueFacet
        "Visibility", OpaqueFacet
        "EventSubscriptions", NeedsFacet
    ]

    let private clientField (name: string) = "client:" + name

    let private clientProvides (registration: obj) : ModuleSurfaceEntry list =
        let definition = tryProp "Definition" registration

        let pages =
            definition
            |> Option.map (fun d -> elements (tryProp "Pages" d))
            |> Option.defaultValue []
            |> List.choose (fun page ->
                tryString "Route" page
                |> Option.map (fun route ->
                    entry
                        (clientField "Definition")
                        "page"
                        route
                        (tryString "Title" page |> Option.defaultValue "")
                        None))

        let pageViews =
            tryProp "PageViews" registration
            |> Option.bind unwrapOption
            |> Option.map (fun m ->
                elements (Some m)
                |> List.choose (fun kv -> tryString "Key" kv)
                |> List.map (fun route -> entry (clientField "PageViews") "page-view" route "" None))
            |> Option.defaultValue []

        let dataTypes =
            elements (tryProp "DataTypes" registration)
            |> List.choose (fun display ->
                tryProp "Info" display
                |> Option.bind (fun info ->
                    tryString "Id" info
                    |> Option.map (fun id ->
                        entry
                            (clientField "DataTypes")
                            "datatype-display"
                            id
                            (tryString "DisplayName" info |> Option.defaultValue "")
                            (Some(ComponentId.forDataType id)))))

        let configFields =
            tryProp "Config" registration
            |> Option.bind unwrapOption
            |> Option.map (fun schema ->
                elements (tryProp "Fields" schema)
                |> List.choose (fun f ->
                    tryString "Key" f
                    |> Option.map (fun key ->
                        entry
                            (clientField "Config")
                            "config-field"
                            key
                            (tryString "DisplayName" f |> Option.defaultValue "")
                            None)))
            |> Option.defaultValue []

        let queries =
            elements (tryProp "ClientQueryHandlers" registration)
            |> List.choose (fun h ->
                tryString "QueryKey" h
                |> Option.map (fun key -> entry (clientField "ClientQueryHandlers") "query" key "" None))

        let placement =
            let one name value =
                value
                |> Option.map (fun v ->
                    entry (clientField name) "placement" (name.ToLowerInvariant() + "=" + v) "" None)
                |> Option.toList

            List.concat [
                one "Availability" (tryProp "Availability" registration |> Option.bind caseName)
                one "Area" (tryProp "Area" registration |> Option.bind caseName)
                one "Group" (tryProp "Group" registration |> Option.bind unwrapOption |> Option.map string)
                // Phase 568 — the declared nav-role gate. Optional, so a
                // module that declares none emits no placement entry (the
                // `Group` line above is the same shape); the facet table
                // above is what keeps the field CLASSIFIED either way.
                one
                    "NavRole"
                    (tryProp "NavRole" registration
                     |> Option.bind unwrapOption
                     |> Option.bind caseName)
            ]

        List.concat [ pages; pageViews; dataTypes; configFields; queries; placement ]

    let private clientNeeds (registration: obj) : ModuleSurfaceEntry list =
        let flags =
            elements (tryProp "FeatureFlags" registration)
            |> List.choose (fun f ->
                tryString "Key" f
                |> Option.map (fun key ->
                    entry
                        (clientField "FeatureFlags")
                        "feature-flag"
                        key
                        (tryString "Description" f |> Option.defaultValue "")
                        None))

        let topics =
            elements (tryProp "EventSubscriptions" registration)
            |> List.choose (fun kv -> tryString "Key" kv)
            |> List.map (fun topic -> entry (clientField "EventSubscriptions") "event-topic" topic "" None)

        flags @ topics

    let private clientOpaque (registration: obj) : ModuleOpaqueSurface list =
        let declared name =
            if (tryProp name registration |> Option.bind unwrapOption).IsSome then
                1
            else
                0

        [
            {
                Field = clientField "NeedsData"
                Kind = "needs-data"
                Count = declared "NeedsData"
                Reason =
                    "NeedsData is a predicate over data-type ids ((DataTypeId -> bool) -> bool), "
                    + "not a declared key set — the ids it accepts are not enumerable"
            }
            {
                Field = clientField "ActionDecoder"
                Kind = "action-key"
                Count = declared "ActionDecoder"
                Reason =
                    "ActionDecoder is a (actionKey, payloadJson) -> Msg option function — "
                    + "the action keys it accepts are not enumerable"
            }
            {
                Field = clientField "ProvidesProcessedData"
                Kind = "processed-data"
                Count = declared "ProvidesProcessedData"
                Reason = "an extractor over module state; the entries it yields are runtime values, not declarations"
            }
            {
                Field = clientField "ProvidesNarrative"
                Kind = "narrative"
                Count = declared "ProvidesNarrative"
                Reason = "an extractor over module state and the active page route; produces runtime values"
            }
        ]

    // ── coverage diff (the drift guard's surface) ─────────────────────

    /// The record fields a value's runtime type declares, or `[]` when it
    /// is not an F# record (a foreign client registration shape reports
    /// nothing rather than throwing).
    let private recordFieldNames (t: Type) : string list =
        if FSharpType.IsRecord(t, true) then
            FSharpType.GetRecordFields(t, true) |> Array.map _.Name |> List.ofArray
        else
            []

    // ── the descriptor ────────────────────────────────────────────────

    /// Derive a module's surface from its server registration and,
    /// optionally, its erased client registration (`ErasedModule`, passed
    /// as `obj` — the Server tier does not reference the Client tier).
    /// Pure and on demand: nothing is built until a caller asks (GP 13).
    let describeWith (serverModule: ServerModule, clientRegistration: obj option) : ModuleSurface =
        let m = serverModule

        let clientCoverage =
            match clientRegistration with
            | None -> []
            | Some _ ->
                clientFieldFacets
                |> List.map (fun (name, facet) -> {
                    Field = clientField name
                    Origin = "client"
                    Facet = facet
                })

        let coverage = serverCoverage m @ clientCoverage

        let declaredServerFields = recordFieldNames typeof<ServerModule> |> Set.ofList
        let coveredServerFields = serverCoverage m |> List.map _.Field |> Set.ofList

        let declaredClientFields =
            match clientRegistration with
            | Some registration when not (isNull registration) ->
                recordFieldNames (registration.GetType()) |> Set.ofList
            | _ -> Set.empty

        let coveredClientFields =
            match clientRegistration with
            | None -> Set.empty
            | Some _ -> clientFieldFacets |> List.map fst |> Set.ofList

        let unclassified =
            [
                yield!
                    Set.difference declaredServerFields coveredServerFields
                    |> Set.toList
                    |> List.map (fun f -> "server:" + f)
                yield!
                    Set.difference declaredClientFields coveredClientFields
                    |> Set.toList
                    |> List.map (fun f -> "client:" + f)
            ]
            |> List.sort

        let stale =
            [
                yield!
                    Set.difference coveredServerFields declaredServerFields
                    |> Set.toList
                    |> List.map (fun f -> "server:" + f)
                yield!
                    Set.difference coveredClientFields declaredClientFields
                    |> Set.toList
                    |> List.map (fun f -> "client:" + f)
            ]
            |> List.sort

        let clientRecord =
            clientRegistration |> Option.filter (isNull >> not) |> Option.defaultValue null

        {
            Module = m.Name
            Component = m.ComponentId |> Option.defaultValue (ComponentId.ofModule m.Name)
            Provides =
                ordered (
                    serverProvides m
                    @ (if isNull clientRecord then
                           []
                       else
                           clientProvides clientRecord)
                )
            Needs = ordered (serverNeeds m @ (if isNull clientRecord then [] else clientNeeds clientRecord))
            Opaque =
                serverOpaque m
                @ (if isNull clientRecord then
                       []
                   else
                       clientOpaque clientRecord)
                |> List.sortBy (fun o -> o.Field, o.Kind)
            Coverage = coverage |> List.sortBy (fun c -> c.Origin, c.Field)
            Unclassified = unclassified
            Stale = stale
            ClientDescribed = not (isNull clientRecord)
        }

    /// Derive a module's surface from its server registration alone. The
    /// page / feature-flag / event-topic side of the surface needs the
    /// client registration — see `describeWith`.
    let describe (serverModule: ServerModule) : ModuleSurface = describeWith (serverModule, None)

    // ── JSON projection ───────────────────────────────────────────────

    /// The canonical wire serialiser (F# records / DUs / options round-trip
    /// through the same converter set the rest of the SDK's non-Remoting
    /// JSON uses).
    let private jsonOptions = FableConverters.create ()

    /// Project a module's surface to JSON so an external tool can snapshot
    /// it without linking the server assembly. Deterministic: every list on
    /// the descriptor is emitted in a stable sort order, and record fields
    /// serialise in declaration order — the same module registration always
    /// yields byte-identical JSON.
    let toJson (surface: ModuleSurface) : string =
        JsonSerializer.Serialize(surface, jsonOptions)

    /// `describe` + `toJson` in one call.
    let describeJson (serverModule: ServerModule) : string = describe serverModule |> toJson