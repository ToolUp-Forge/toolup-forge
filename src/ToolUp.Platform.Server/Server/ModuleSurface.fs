// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Security.Cryptography
open System.Text
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
// rather than being guessed at or quietly omitted.
//
// **Phase 621 gave three of those an enumerable half.** A client
// registration may now declare `NeedsDataKeys` beside the predicate,
// `ActionKeys` beside the decoder, and `QueryTargets` where no
// registration field existed at all — and the descriptor reports each
// as ordinary entries (`datatype-need` / `action-key` / `query-target`)
// instead of having only an opaque note to offer. Three things stay
// true and are what keeps the report honest:
//   * A module that declares nothing is reported exactly as before —
//     the opaque note, no entries (GP 11). `None` is "no claim"; `Some
//     []` is the claim that the set is empty, and the two read
//     differently here rather than being conflated.
//   * The opaque note SURVIVES the declaration where a function is
//     still registered, because the function is still opaque. The
//     declaration is an "at least these" subset claim — the predicate
//     may accept an id the list omits, the decoder a key it omits — so
//     a reader that treated the list as the whole truth would be
//     wrong. The note now says how many keys were declared beside it.
//   * Outbound queries keep an opaque line on the SERVER side, because
//     `ServerModule` still declares none — see `serverOpaque`.
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

// ─── Phase 589 — the certifiable projection of a surface ─────────────────
//
// A certification has to hash SOMETHING, and the obvious candidate — the whole
// `ModuleSurface` record — is the wrong one. Four of its fields (`Coverage`,
// `Unclassified`, `Stale`, and the `Reason` prose on `Opaque`) are the
// DESCRIPTOR's self-report: they describe what this SDK version knows how to
// classify, not what the module declares. Hashing them would decertify every
// module in the estate the moment a `ServerModule` field was added — a
// mass-decertification event in which no module had changed — and a gate that
// fires when nothing is wrong is one people learn to switch off.
//
// So the certified projection is the module's DECLARATIONS: its identity, the
// side of the registration that was described, and the provide / need sets
// keyed `"<kind>:<key>"`. `Label` and `Slot` are excluded too — a `Label` is a
// display name (renaming a data type's `DisplayName` is not a composability
// change) and a `Slot` is derived from the `Key` deterministically, so it
// carries no independent claim.
//
// Deterministic by construction, not by convention: every list is a sorted,
// de-duplicated list of strings, and record fields serialise in declaration
// order — so two independent derivations of the same registration, on
// different machines, in different processes, produce byte-identical JSON.

/// The subset of a `ModuleSurface` a certification covers — what the module
/// DECLARES, as opposed to what the descriptor reports about itself.
type CertifiedSurfaceProjection = {
    /// The module's registration `Name`.
    Module: string
    /// The module's resolved `ComponentId`, rendered.
    Component: string
    /// Which registrations the certified surface was derived from: `"server"`
    /// (server registration alone) or `"server+client"`. Carried because the
    /// two are not comparable — certifying `server+client` and re-deriving
    /// `server` at a gate that has no client registration would read as dozens
    /// of vanished provides, when the honest report is that a different half
    /// of the module was described.
    Described: string
    /// What the module offers a composition, as sorted `"<kind>:<key>"` tokens.
    Provides: string list
    /// What the module requires from a composition, same shape.
    Needs: string list
}

/// One divergence between a certified surface and the live one.
type ModuleSurfaceDrift = {
    /// Which part of the projection moved: `module` / `component` /
    /// `described` / `provides` / `needs`.
    Facet: string
    /// `added` (live declares it, the certification did not), `removed` (the
    /// certification declared it, the live surface does not), or `changed`
    /// (a single-valued facet holds a different value).
    Change: string
    /// The declaration itself — a `"<kind>:<key>"` token for the set facets, or
    /// `"<certified> -> <live>"` for a single-valued one.
    Declaration: string
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
            // Phase 10b — the module's declared config schema-evolution
            // steps. `Provides` on the `ConfigSchema` precedent directly
            // above: a migration chain is part of what the module
            // declares about its own config surface, not a substrate it
            // asks the composition for. (What it IMPLIES — an
            // `IConfigStore` to migrate documents in — is emitted on the
            // `Needs` side below.)
            c (nameof m.ConfigMigrations) ProvidesFacet
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
            // Phase 551 — the module's declared grant policy. `Provides`
            // rather than `Needs`, on the `DefaultSurfaceRequirement`
            // precedent two lines up: both are module-DECLARED access
            // postures that a composition reads off the registration. The
            // `Needs` side of this descriptor is exclusively "substrate
            // interface implied by a registration", and a grant policy
            // implies none — `IPermissionStore` is registered
            // unconditionally by `ComposeTeamRuntime`, so reporting it as
            // an implied need would name a dependency that is never a
            // composition decision.
            c (nameof m.GrantPolicy) ProvidesFacet
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

        // Phase 10b — one entry per declared migration step, so a
        // composition's surface names the version hops a module's config
        // documents will be carried through rather than only the schema
        // they land on.
        let configMigrations =
            m.ConfigMigrations
            |> List.map (fun mig ->
                entry
                    (nameof m.ConfigMigrations)
                    "config-migration"
                    (sprintf "%s:%d->%d" mig.ModuleKey mig.FromVersion mig.ToVersion)
                    (sprintf "config schema %d -> %d" mig.FromVersion mig.ToVersion)
                    None)

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

        // Phase 551 — emitted only when the module declares a policy, on
        // the `BindingStamp` precedent immediately above: `AdminDiscretion`
        // is this field's "declares nothing" value exactly as `None` is
        // `BindingStamp`'s, so an undeclared module's surface is
        // byte-identical to its pre-551 self (GP 11). The FIELD is covered
        // unconditionally — coverage is the drift guard's subject, the
        // entry is the declaration's.
        let grantPolicy =
            match m.GrantPolicy with
            | GrantPolicy.AdminDiscretion -> []
            | declared ->
                // The wire token IS the declared identity, and it carries
                // the `PartyRef` on the counterparty arm — a string drawn
                // from the module's own registration, so the SDK still
                // names no module and no party (GP 9).
                [
                    entry (nameof m.GrantPolicy) "grant-policy" (GrantPolicy.toToken declared) "" None
                ]

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
            configMigrations
            queries
            tools
            signals
            latencyCeilings
            surfaceDefault
            routePrefixes
            routeOverrides
            jobs
            bindingStamp
            grantPolicy
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
            // Phase 10b — a declared migrator needs somewhere to read and
            // write the documents it upgrades. Usually redundant with the
            // line above (a module that versions a schema declares one),
            // but not always: a deployment may register a migrator for a
            // reserved `_platform*` key no ServerModule owns a schema for.
            // `distinctBy` at the end of this list dedupes the common case.
            implied (nameof m.ConfigMigrations) (not m.ConfigMigrations.IsEmpty) [ "IConfigStore" ]
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
                "no ServerModule field declares the (TargetModule, QueryKey) pairs a server-side module "
                + "ASKS for — outbound queries are ordinary calls through IModuleQueryBus, so the "
                + "server needs side reports only the substrate the registrations imply. Phase 621 "
                + "added the client-side declaration (client:QueryTargets), which is reported as "
                + "query-target entries when the client registration declares one; it is a subset "
                + "claim, since nothing observes an undeclared Ask"
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

    /// Phase 621 — read one of the declared key lists (`'T list option`)
    /// off the erased registration. The three-valued result is the whole
    /// point and must not be flattened: `None` — the module declares
    /// nothing, so the descriptor reports the pre-621 opaque note and no
    /// entries (GP 11); `Some []` — the module declares that the set is
    /// empty, a real claim; `Some xs` — the declared members.
    ///
    /// `tryProp` already returns `None` for an F# `None` (which reflects
    /// as null), so the two absent cases collapse here — which is correct:
    /// a registration shape that carries no such field at all and one that
    /// carries `None` both mean "no declaration", and the `Unclassified`
    /// diff is what catches a field that has genuinely gone missing.
    let private declaredElements (name: string) (registration: obj) : obj list option =
        tryProp name registration
        |> Option.bind unwrapOption
        |> Option.map (fun value -> elements (Some value))

    let private declaredStrings (name: string) (registration: obj) : string list option =
        declaredElements name registration
        |> Option.map (
            List.choose (fun (o: obj) ->
                match o with
                | :? string as s -> Some s
                | _ -> None)
        )

    /// How a declared key list reads in an `Opaque` reason, so the note
    /// beside a still-opaque function says what was declared beside it.
    ///
    /// **Empty for `None`, deliberately.** An undeclared module's whole
    /// descriptor — not merely its behaviour — must be byte-identical to
    /// the pre-621 one (GP 11), and a `Reason` string is part of it; a
    /// "declares nothing" note would be a diff on every module that never
    /// opted in. The absence of the suffix IS the pre-621 report.
    let private declaredSuffix (field: string) (declared: string list option) : string =
        match declared with
        | None -> ""
        | Some [] -> sprintf "; the registration declares an EMPTY %s list — a claim, not an absence" field
        | Some keys ->
            sprintf
                "; the registration additionally declares %d key(s) as %s, reported as entries — an 'at least these' subset claim, since the function above stays authoritative"
                (List.length keys)
                field

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
        // Phase 621 — the enumerable half of the data gate. A NEED: the
        // ids name data the module expects some OTHER module to provide.
        "NeedsDataKeys", NeedsFacet
        "DataTypes", ProvidesFacet
        "ProvidesProcessedData", OpaqueFacet
        "ProvidesNarrative", OpaqueFacet
        "Config", ProvidesFacet
        "FeatureFlags", NeedsFacet
        "Availability", ProvidesFacet
        "Group", ProvidesFacet
        "Placement", ProvidesFacet
        "NavRole", ProvidesFacet
        "Area", ProvidesFacet
        "ClientQueryHandlers", ProvidesFacet
        // Phase 621 — declared outbound queries. A NEED: each names a
        // handler the module expects some other module to answer.
        "QueryTargets", NeedsFacet
        "ActionDecoder", OpaqueFacet
        // Phase 621 — the decoder's key set, as data. A PROVIDE, unlike
        // the two above: the module offers the composition its ability to
        // RECEIVE these actions, so the pairing runs emitter (a tool's
        // `EmitsActions`, server-side) → decoder (here).
        "ActionKeys", ProvidesFacet
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
                // Phase 611 — the declared rail slot. Same optional shape as
                // `Group` / `NavRole`: a module that declares none emits no
                // entry, and the facet table above is what keeps the field
                // classified either way. Worth surfacing because a placed row
                // is one a user preference cannot move, which is exactly the
                // kind of declaration a composition audit wants to see.
                one
                    "Placement"
                    (tryProp "Placement" registration
                     |> Option.bind unwrapOption
                     |> Option.bind caseName)
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

        // Phase 621 — the action keys the module's decoder handles, now
        // that they can be declared rather than probed for. A provide: it
        // is the receiving half of the emitter↔decoder pairing, whose
        // emitting half is a server-side tool's `EmitsActions`.
        let actionKeys =
            declaredStrings "ActionKeys" registration
            |> Option.defaultValue []
            |> List.map (fun key -> entry (clientField "ActionKeys") "action-key" key "" None)

        List.concat [ pages; pageViews; dataTypes; configFields; queries; placement; actionKeys ]

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

        // Phase 621 — the enumerable half of the data gate. Keyed against
        // the same `ComponentId.forDataType` slot space the `datatype`
        // provide uses, so a need and the provide that satisfies it join
        // on an id rather than on a string comparison at the reader.
        let dataNeeds =
            declaredStrings "NeedsDataKeys" registration
            |> Option.defaultValue []
            |> List.map (fun id ->
                entry
                    (clientField "NeedsDataKeys")
                    "datatype-need"
                    id
                    "declared beside the NeedsData predicate"
                    (Some(ComponentId.forDataType id)))

        // Phase 621 — declared outbound module queries. Keyed
        // `"<TargetModule>.<QueryKey>"`, the identity string
        // `ModuleQueryTarget.describe` renders and the same shape the
        // `query` provide's key composes with the answering module's
        // name — so a declared target and the handler that answers it are
        // joinable without the reader re-deriving the convention.
        let queryTargets =
            declaredElements "QueryTargets" registration
            |> Option.defaultValue []
            |> List.choose (fun target ->
                match tryString "TargetModule" target, tryString "QueryKey" target with
                | Some targetModule, Some queryKey ->
                    Some(
                        entry
                            (clientField "QueryTargets")
                            "query-target"
                            (targetModule + "." + queryKey)
                            ""
                            (Some(ComponentId.ofModule targetModule))
                    )
                | _ -> None)

        flags @ topics @ dataNeeds @ queryTargets

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
                // Phase 621 — the predicate is STILL opaque, so the note
                // stays; what changed is that it can now say what the
                // module declared beside it. Dropping the note once a
                // declaration exists would read as "the surface is fully
                // enumerated", which the subset claim does not support.
                Reason =
                    "NeedsData is a predicate over data-type ids ((DataTypeId -> bool) -> bool), "
                    + "not a declared key set — the ids it accepts are not enumerable"
                    + declaredSuffix "NeedsDataKeys" (declaredStrings "NeedsDataKeys" registration)
            }
            {
                Field = clientField "ActionDecoder"
                Kind = "action-key"
                Count = declared "ActionDecoder"
                Reason =
                    "ActionDecoder is a (actionKey, payloadJson) -> Msg option function — "
                    + "the action keys it accepts are not enumerable"
                    + declaredSuffix "ActionKeys" (declaredStrings "ActionKeys" registration)
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

    // ── Phase 589 — certifiable projection, hash, and drift ──────────────

    /// Ordinal string sort — culture-independent, so the canonical JSON does
    /// not depend on the machine's locale.
    /// Ordinal string sort + de-duplication. Both matter, and neither is
    /// inherited from `describe`: `ordered` sorts ENTRIES by `(Kind, Key,
    /// Field)`, which is not the same relation as an ordinal sort of the
    /// `"<kind>:<key>"` tokens, and it de-duplicates nothing — two registration
    /// fields declaring one key (a route prefix listed twice) reach the
    /// projection as two identical tokens. Canonicalising here means the
    /// certified hash does not depend on the descriptor's internal ordering.
    let private ordinal (xs: string list) : string list =
        xs |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    /// The declaration token for one entry: `"<kind>:<key>"`.
    let private token (e: ModuleSurfaceEntry) : string = e.Kind + ":" + e.Key

    /// Project a surface onto the facets a certification covers. See the
    /// commentary on `CertifiedSurfaceProjection` for what is deliberately
    /// excluded and why.
    let project (surface: ModuleSurface) : CertifiedSurfaceProjection = {
        Module = surface.Module
        Component = surface.Component.Value
        Described =
            if surface.ClientDescribed then
                "server+client"
            else
                "server"
        Provides = surface.Provides |> List.map token |> ordinal
        Needs = surface.Needs |> List.map token |> ordinal
    }

    /// The canonical JSON a certification is computed over. Deterministic: the
    /// projection's lists are sorted and de-duplicated, its fields are strings
    /// and string lists only, and records serialise in declaration order — so
    /// two independent derivations of the same registration are byte-identical.
    let certificationJson (surface: ModuleSurface) : string =
        JsonSerializer.Serialize(project surface, jsonOptions)

    /// base64url (RFC 4648 §5, unpadded) SHA-256 over a canonical projection
    /// JSON string's UTF-8 bytes. Split out from `certificationHash` because
    /// the deploy-time stamper hashes the JSON it was HANDED (it has no
    /// registration to describe), and both paths must agree byte for byte.
    let certificationHashOfJson (canonicalJson: string) : string =
        let digest = SHA256.HashData(Encoding.UTF8.GetBytes canonicalJson)

        Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    /// The certified-surface hash of a live surface.
    let certificationHash (surface: ModuleSurface) : string =
        certificationJson surface |> certificationHashOfJson

    /// Build the certified label for a surface, with no conformance verdict.
    let certify (surface: ModuleSurface) : CertifiedModuleSurface =
        let json = certificationJson surface

        {
            SurfaceJson = json
            SurfaceHash = certificationHashOfJson json
            Verdict = None
        }

    /// Build the certified label for a surface, recording the conformance
    /// verdict of the run that certified it.
    let certifyWith (verdict: ModuleConformanceVerdict) (surface: ModuleSurface) : CertifiedModuleSurface = {
        certify surface with
            Verdict = Some verdict
    }

    /// Read a canonical projection back from its JSON.
    let parseProjection (canonicalJson: string) : Result<CertifiedSurfaceProjection, string> =
        try
            let parsed =
                JsonSerializer.Deserialize<CertifiedSurfaceProjection>(canonicalJson, jsonOptions)

            if isNull (box parsed) then
                Error "the certified surface JSON is null"
            else
                let p = parsed
                let str (s: string) = if isNull (box s) then "" else s

                // A missing field deserialises to null through the STJ converter
                // set (not to `[]` / `""`), and a null F# list NREs on the first
                // list operation — so coerce before anything reads them. `[]` is
                // the `Empty` singleton, never null.
                Ok {
                    Module = str p.Module
                    Component = str p.Component
                    Described = str p.Described
                    Provides = if isNull (box p.Provides) then [] else p.Provides
                    Needs = if isNull (box p.Needs) then [] else p.Needs
                }
        with ex ->
            Error(sprintf "the certified surface JSON is not a readable projection: %s" ex.Message)

    /// Diff a certified projection against a live surface. An empty list means
    /// the live surface still matches what was certified.
    ///
    /// A `Described` mismatch short-circuits: certifying `server+client` and
    /// re-deriving `server` differs in nearly every entry, and reporting sixty
    /// phantom removals would bury the one fact that explains them.
    let driftAgainst (certified: CertifiedSurfaceProjection) (live: ModuleSurface) : ModuleSurfaceDrift list =
        let liveProjection = project live

        let changed facet (certifiedValue: string) (liveValue: string) =
            if certifiedValue = liveValue then
                []
            else
                [
                    {
                        Facet = facet
                        Change = "changed"
                        Declaration = certifiedValue + " -> " + liveValue
                    }
                ]

        let identity =
            changed "module" certified.Module liveProjection.Module
            @ changed "component" certified.Component liveProjection.Component

        let described = changed "described" certified.Described liveProjection.Described

        if not described.IsEmpty then
            identity @ described
        else
            let setDrift facet (certifiedSet: string list) (liveSet: string list) =
                let c = Set.ofList certifiedSet
                let l = Set.ofList liveSet

                [
                    for declaration in Set.difference l c |> Set.toList ->
                        {
                            Facet = facet
                            Change = "added"
                            Declaration = declaration
                        }
                    for declaration in Set.difference c l |> Set.toList ->
                        {
                            Facet = facet
                            Change = "removed"
                            Declaration = declaration
                        }
                ]

            identity
            @ setDrift "provides" certified.Provides liveProjection.Provides
            @ setDrift "needs" certified.Needs liveProjection.Needs

    /// `parseProjection` + `driftAgainst` — the shape a verifier holding a
    /// certified JSON string and a live registration needs.
    let driftFrom (certifiedJson: string) (live: ModuleSurface) : Result<ModuleSurfaceDrift list, string> =
        parseProjection certifiedJson
        |> Result.map (fun certified -> driftAgainst certified live)

    /// Render a drift list as one neutral diagnostic line, suitable for a
    /// startup log. Names every drifted facet — the point of carrying the
    /// certified projection rather than only its hash.
    let describeDrift (drifts: ModuleSurfaceDrift list) : string =
        drifts
        |> List.map (fun d -> sprintf "%s %s '%s'" d.Facet d.Change d.Declaration)
        |> String.concat "; "

/// Opt-in verifier of a module's signed certification (Phase 589), implemented
/// by `DefaultModuleBindingVerifier` (`ToolUp.ArtefactSigning`). Separate from
/// `IModuleBindingVerifier` so the Phase 165 interface stays unchanged: a
/// deployment that never certifies a module never touches this surface.
///
/// It lives here rather than beside the other binding contracts in
/// `ToolUp.Platform.Core` for one structural reason — it must name
/// `ModuleSurface`, which is a Server-tier type (its derivation reads the
/// `ServerModule` registration). The DATA contracts it carries
/// (`CertifiedModuleSurface` / `ModuleCertificationStamp`) are tier-shared and
/// do live in Core.
///
/// **Verification rule (load-bearing, two halves):** the certification's
/// signature MUST verify under some configured anchor over its canonical
/// bytes, AND the `live` surface's certification hash MUST equal the certified
/// one. A failure of either is `Rejected`; a hash failure names the drifted
/// facets.
type IModuleCertificationVerifier =
    /// Decide whether `moduleId`'s live surface still matches the certified
    /// one its `certification` attests to.
    abstract VerifyCertification:
        moduleId: string * live: ModuleSurface * certification: ModuleCertificationStamp -> BindingOutcome

/// Phase 589 — the compose-time certified-surface gate.
///
/// **Why it is a list operation over the module list rather than a branch
/// inside `ServerApp.addModule`.** The gate must DERIVE the live surface, and
/// that derivation reads the `ServerModule` registration — so `ModuleSurface`
/// necessarily compiles *after* `ServerApp`, and `addModule` cannot name it.
/// The shape here is therefore the one Phase 166 already established for the
/// same reason: the composition root pipes its module list through this gate
/// before `addModules`, exactly as it pipes it through
/// `ModuleBindingManifest.applyToAll` to attach the stamps in the first place.
///
/// ```fsharp skip=fragment
/// let certifications = ModuleBindingManifest.loadCertificationsFromDir deployDir
/// modules
/// |> ModuleBindingManifest.applyToAll stamps
/// |> ModuleCertificationGate.admit (Some verifier) certifications
/// ```
///
/// **GP 11 / GP 13.** A module with no entry in the certification map is
/// `Allowed` untouched and its surface is never derived, so a deployment that
/// certifies nothing pays nothing and behaves byte-for-byte as it did pre-589.
/// A module that IS certified on a deployment with no verifier fails closed —
/// the same posture `addModule` takes for a stamped module with no binding
/// verifier, and for the same reason: a certified module is self-protecting.
module ModuleCertificationGate =

    /// Decide one module against the certification filed under its name, using
    /// an explicitly-supplied live surface. Use this from a composition root
    /// that holds the module's client registration too — pass
    /// `ModuleSurface.describeWith (m, Some(box clientRegistration))` so a
    /// `server+client` certification is compared against a `server+client`
    /// derivation.
    let decideAgainst
        (verifier: IModuleCertificationVerifier option)
        (certifications: Map<string, ModuleCertificationStamp>)
        (m: ServerModule, live: ModuleSurface)
        : BindingOutcome =
        match Map.tryFind m.Name certifications with
        | None -> Allowed
        | Some certification ->
            match verifier with
            | Some v -> v.VerifyCertification(m.Name, live, certification)
            | None ->
                Rejected
                    "module carries a certified surface but this deployment has no module-certification verifier configured"

    /// Decide one module against its certification, deriving the live surface
    /// from the server registration alone.
    let decide
        (verifier: IModuleCertificationVerifier option)
        (certifications: Map<string, ModuleCertificationStamp>)
        (m: ServerModule)
        : BindingOutcome =
        // The derivation is INSIDE the `Some` arm of `decideAgainst`'s lookup
        // for the zero-cost path, so an uncertified module never describes.
        if Map.containsKey m.Name certifications then
            decideAgainst verifier certifications (m, ModuleSurface.describe m)
        else
            Allowed

    /// Partition a module list into the modules the gate admits and the
    /// `(moduleName, reason)` pairs it refused. The refusals are neutral
    /// diagnostics naming the drifted facets — suitable for a startup log.
    let partition
        (verifier: IModuleCertificationVerifier option)
        (certifications: Map<string, ModuleCertificationStamp>)
        (modules: ServerModule list)
        : ServerModule list * (string * string) list =
        let decided = modules |> List.map (fun m -> m, decide verifier certifications m)

        let admitted =
            decided
            |> List.choose (fun (m, outcome) ->
                match outcome with
                | Allowed -> Some m
                | Rejected _ -> None)

        let refused =
            decided
            |> List.choose (fun (m, outcome) ->
                match outcome with
                | Rejected reason -> Some(m.Name, reason)
                | Allowed -> None)

        admitted, refused

    /// The admitted modules alone — the `applyToAll` shape a composition root
    /// pipes through before `addModules`. Use `partition` when the refusals
    /// need logging.
    let admit
        (verifier: IModuleCertificationVerifier option)
        (certifications: Map<string, ModuleCertificationStamp>)
        (modules: ServerModule list)
        : ServerModule list =
        partition verifier certifications modules |> fst