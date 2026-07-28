module ToolUp.Platform.Tests.InProcess.HostEnvelopeTests

open Expecto
open System
open FSharp.Reflection
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.FileProcessor

// ─── Phase 588 — host-envelope descriptor ─────────────────────────────
//
// Same acceptance shape as `ComposableSurfaceTests` (Phase 293) and
// `ModuleSurfaceTests` (Phase 581): the descriptor is measured against an
// INDEPENDENT enumeration of the composition, not against a copy of its
// own output. Every load-bearing test below re-derives one axis of the
// envelope from the live registry by a different route (reflection over
// `ServerApp` / `ServerConfig` / `ComponentKind`, or a direct walk of the
// composed modules) and asserts set-equality — so a composition that
// gains a companion slot, a config knob, a component kind, a module or a
// route, and an envelope that misses it, FAILS HERE.

// A minimal `DataType` whose `Detect` / `Process` are never invoked — the
// envelope reads its `Id` (the wire `TypeName`) only.
let private stubDataType (id: string) : DataType = {
    Info = {
        Id = id
        DisplayName = id
        Schema = None
    }
    Id = id
    Detect = fun _ -> async { return false }
    Process = fun _ -> async { return failwith "stub DataType.Process is never called by the envelope" }
}

let private stubTool (name: string) : AIToolDefinition * (HttpContext -> string -> Async<string>) =
    {
        Name = name
        Description = ""
        Parameters = []
        SourceModule = "envelope-test"
        EmitsActions = None
        Location = ServerResident
        Surface = Both
    },
    (fun _ _ -> async { return "" })

/// The reference modules: one rich module declaring a data type, a query
/// key, a tool, a route prefix and an exact route override; one bare
/// module that declares nothing but its name.
let private referenceModules () : ServerModule list = [
    ServerModule.create "Orders"
    |> ServerModule.withComponentId "orders-service"
    |> ServerModule.withDataTypes [ stubDataType "SalesData" ]
    |> ServerModule.withQueryHandlers [
        {
            QueryKey = "latest-order"
            Handle = fun _ -> async { return "" }
        }
    ]
    |> ServerModule.withAITools [ stubTool "orders.run" ]
    |> ServerModule.withRoutePrefix "/api/orders/"
    |> ServerModule.withRouteSurfaceRequirement "post" "/api/orders/public" SurfaceRequirement.claimBearerOnly

    ServerModule.create "Inventory"
]

/// The reference composition: the modules above, composed through the
/// real `addModule` path, plus one filled multi-impl companion slot.
let private referenceApp () : ServerApp =
    ServerApp.empty
    |> ServerApp.addModules (referenceModules ())
    |> ServerApp.withAuditSink (InMemoryAuditSink "splunk-archive")

let private envelope () =
    HostEnvelope.describe (referenceApp (), referenceModules ())

// ── independent re-derivations (the drift guard's other half) ────────

/// Recompute the companion-interface universe straight off the
/// `ServerApp` record — deliberately NOT via `ComposableSurface` or the
/// envelope. Equality with the envelope's slot set is the "derived, not
/// hand-listed" proof.
let private reflectedCompanionInterfaces () : Set<string> =
    FSharpType.GetRecordFields typeof<ServerApp>
    |> Array.choose (fun field ->
        let t = field.PropertyType

        if t.IsGenericType then
            let def = t.GetGenericTypeDefinition()
            let arg = t.GetGenericArguments()[0]

            if
                (def = typedefof<option<_>> || def = typedefof<list<_>>)
                && arg.IsInterface
                && arg.Name.StartsWith("I", StringComparison.Ordinal)
            then
                Some arg.Name
            else
                None
        else
            None)
    |> Set.ofArray

/// Recompute the enum-like `ServerConfig` knob names straight off the
/// record type.
let private reflectedKnobNames () : Set<string> =
    FSharpType.GetRecordFields typeof<ServerConfig>
    |> Array.choose (fun field ->
        let t = field.PropertyType

        if FSharpType.IsUnion t then
            let cases = FSharpType.GetUnionCases t

            if cases.Length > 0 && cases |> Array.forall (fun c -> c.GetFields().Length = 0) then
                Some field.Name
            else
                None
        else
            None)
    |> Set.ofArray

/// Recompute the component-kind labels straight off the union.
let private reflectedKindLabels () : Set<string> =
    FSharpType.GetUnionCases typeof<ComponentKind>
    |> Array.map (fun case ->
        let name = case.Name

        let trimmed =
            if name.EndsWith("Component", StringComparison.Ordinal) then
                name.Substring(0, name.Length - "Component".Length)
            else
                name

        trimmed.ToLowerInvariant())
    |> Set.ofArray

/// Recompute the occupied route set by walking the composed modules'
/// own registrations — not through `ModuleSurface`, and not through the
/// envelope.
let private declaredRoutes () : Set<string * string> =
    referenceModules ()
    |> List.collect (fun m -> [
        for prefix in m.RoutePrefixes -> m.Name, prefix
        for ((httpMethod, path), _) in m.RouteSurfaceRequirements -> m.Name, httpMethod.ToUpperInvariant() + " " + path
    ])
    |> Set.ofList

let tests =
    testList "HostEnvelope" [

        // ── slots: the universe, marked filled or open ────────────────
        testCase "the slot offers are exactly the reflected companion-slot universe"
        <| fun _ ->
            let offered = (envelope ()).EnvelopeSlots |> List.map _.OfferInterface |> Set.ofList

            Expect.equal
                offered
                (reflectedCompanionInterfaces ())
                "the envelope offers every slot forge can compose — a new slot cannot be missed"

        testCase "a composed slot reads Filled with its impl sub-ids; an uncomposed one reads Open"
        <| fun _ ->
            let byInterface =
                (envelope ()).EnvelopeSlots
                |> List.map (fun s -> s.OfferInterface, s)
                |> Map.ofList

            let audit = byInterface |> Map.find "IAuditSink"
            Expect.equal audit.OfferState FilledSlot "the composed audit-sink slot is filled"
            Expect.equal audit.OfferImpls [ "splunk-archive" ] "the filled multi-impl slot names its impl sub-id"
            Expect.equal audit.OfferCardinality MultiImpl "IAuditSink is a multi-impl slot"

            let auth = byInterface |> Map.find "IAuthProvider"

            Expect.equal
                auth.OfferState
                OpenSlot
                "a slot forge can compose but this deployment did not is OPEN — what a module may not rely on"

            Expect.isEmpty auth.OfferImpls "an open slot names no impl"

        testCase "slot ids share the Phase 279 slot-id space"
        <| fun _ ->
            let audit =
                (envelope ()).EnvelopeSlots
                |> List.find (fun s -> s.OfferInterface = "IAuditSink")

            Expect.equal
                audit.OfferSlot
                (ComponentId.forCompanionSlot "IAuditSink")
                "the offer id joins directly against the composition manifest"

        // ── capability layers: seeded from the ComponentKind union ────
        testCase "the capability layers are exactly the ComponentKind universe"
        <| fun _ ->
            let layers =
                (envelope ()).EnvelopeCapabilities |> List.map _.LayerKind |> Set.ofList

            Expect.equal
                layers
                (reflectedKindLabels ())
                "every component kind surfaces as a layer — a newly-shipped kind cannot be missed"

        testCase "each layer enumerates exactly what the manifest composed under its kind"
        <| fun _ ->
            let manifest = ServerApp.compositionManifest (referenceApp ())

            let expected =
                CompositionManifest.allComponents manifest
                |> List.map (fun entry -> ComponentId.value entry.Id)
                |> Set.ofList

            let actual =
                (envelope ()).EnvelopeCapabilities |> List.collect _.LayerIds |> Set.ofList

            Expect.equal actual expected "the layers partition the composed component set exactly"

            let byKind =
                (envelope ()).EnvelopeCapabilities
                |> List.map (fun l -> l.LayerKind, l)
                |> Map.ofList

            Expect.equal
                (byKind |> Map.find "module").LayerCount
                2
                "both composed modules surface under the module layer"

            Expect.equal
                (byKind |> Map.find "metric").LayerCount
                0
                "a kind nothing was composed under still surfaces, honestly empty"

            Expect.isTrue
                ((envelope ()).EnvelopeCapabilities
                 |> List.forall (fun l -> l.LayerCount = l.LayerIds.Length))
                "every layer's count matches its enumerated ids"

        // ── module surfaces aggregated, not re-derived ────────────────
        testCase "every composed module contributes its Phase 581 surface"
        <| fun _ ->
            let names = (envelope ()).EnvelopeModules |> List.map _.Module |> Set.ofList

            Expect.equal
                names
                (referenceModules () |> List.map _.Name |> Set.ofList)
                "the envelope carries one surface per composed module"

            let orders =
                (envelope ()).EnvelopeModules |> List.find (fun s -> s.Module = "Orders")

            Expect.equal
                orders
                (ModuleSurface.describe (referenceModules () |> List.find (fun m -> m.Name = "Orders")))
                "the aggregated surface IS the module's own descriptor — no second derivation to drift"

            let keys kind =
                orders.Provides |> List.filter (fun e -> e.Kind = kind) |> List.map _.Key

            Expect.equal (keys "datatype") [ "SalesData" ] "the data-type wire TypeName is on the offer surface"
            Expect.equal (keys "query") [ "latest-order" ] "the query key already answered is on the offer surface"
            Expect.equal (keys "tool") [ "orders.run" ] "the tool name already taken is on the offer surface"

        // ── routes filtered from those same surfaces ──────────────────
        testCase "the occupied routes are exactly what the composed modules declared"
        <| fun _ ->
            let actual =
                (envelope ()).EnvelopeRoutes
                |> List.map (fun r -> r.RouteOwner, r.RouteKey)
                |> Set.ofList

            Expect.equal actual (declaredRoutes ()) "no declared route is missed and none is invented"

            let exact = (envelope ()).EnvelopeRoutes |> List.find (fun r -> r.RouteExact)

            Expect.equal exact.RouteKey "POST /api/orders/public" "an exact override carries its method"
            Expect.equal exact.RouteOwner "Orders" "the route is attributed to its declaring module"

            Expect.isTrue
                ((envelope ()).EnvelopeRoutes |> List.forall (fun r -> r.RouteAdmits <> ""))
                "every occupied route reports the admit set guarding it"

        // ── knobs: schema + the value THIS deployment resolved ────────
        testCase "the knob offers are exactly the reflected enum-like ServerConfig fields"
        <| fun _ ->
            let offered = (envelope ()).EnvelopeKnobs |> List.map _.KnobName |> Set.ofList

            Expect.equal
                offered
                (reflectedKnobNames ())
                "every composition-shaping knob surfaces — a new mode switch cannot be missed"

        testCase "a knob reports both its admissible set and the resolved value"
        <| fun _ ->
            let resolved = (envelope ()).EnvelopeKnobs

            Expect.isTrue
                (resolved |> List.forall (fun k -> not k.KnobAdmissible.IsEmpty))
                "every knob lists its admissible values"

            Expect.isTrue
                (resolved
                 |> List.forall (fun k -> k.KnobAdmissible |> List.contains k.KnobResolved))
                "the value this deployment resolved is one of the admissible values"

            let app = referenceApp ()

            let profile = resolved |> List.find (fun k -> k.KnobName = "ProcessProfile")

            Expect.equal
                profile.KnobResolved
                (string app.Config.ProcessProfile)
                "the resolved value is read off the live ServerConfig, not a hand-listed copy"

        // ── platform build ───────────────────────────────────────────
        testCase "the envelope reports the platform build it was derived under"
        <| fun _ ->
            let platform = (envelope ()).EnvelopePlatform

            Expect.equal
                platform.Package
                "ToolUp.Platform.Server"
                "the platform assembly is the one that carries the composition types"

            Expect.notEqual platform.Version "unknown" "the platform version resolves"
            Expect.equal (envelope ()).EnvelopeSchemaVersion HostEnvelope.CurrentSchemaVersion "the shape is versioned"

        // ── canonical JSON + hash stamp ──────────────────────────────
        testCase "describe is deterministic and its JSON projection is byte-identical"
        <| fun _ ->
            Expect.equal (envelope ()) (envelope ()) "the same composition describes identically"

            Expect.equal
                (HostEnvelope.toJson (envelope ()))
                (HostEnvelope.describeJson (referenceApp (), referenceModules ()))
                "the canonical JSON is deterministic — the same composition always yields the same bytes"

        testCase "the stamp pins the envelope's content hash"
        <| fun _ ->
            let stamp = HostEnvelope.stampOf (envelope ())

            Expect.equal stamp.StampContentHash (HostEnvelope.contentHash (envelope ())) "the stamp carries the hash"
            Expect.equal stamp.StampContentHash.Length 64 "SHA-256 rendered as lowercase hex"

            Expect.isTrue
                (stamp.StampContentHash
                 |> Seq.forall (fun c -> Char.IsDigit c || (c >= 'a' && c <= 'f')))
                "the hash is lowercase hex"

            Expect.isTrue (HostEnvelope.isCurrent stamp (envelope ())) "a stamp is current against its own envelope"
            Expect.isEmpty (HostEnvelope.staleness stamp (envelope ())) "a current stamp reports no staleness reason"

        testCase "a composition change makes a pinned stamp stale"
        <| fun _ ->
            let pinned = HostEnvelope.stampOf (envelope ())

            // The deployment gains a companion — exactly the change an
            // authoring tool holding a pinned snapshot must detect.
            let moved =
                HostEnvelope.describe (
                    referenceApp () |> ServerApp.withAuditSink (InMemoryAuditSink "datadog-logs"),
                    referenceModules ()
                )

            Expect.isFalse (HostEnvelope.isCurrent pinned moved) "the pinned snapshot is no longer true of the app"

            Expect.contains
                (HostEnvelope.staleness pinned moved)
                HostEnvelope.ContentChangedReason
                "the staleness reason names the content change"

            Expect.isFalse
                (HostEnvelope.staleness pinned moved
                 |> List.contains HostEnvelope.PlatformVersionMovedReason)
                "the platform did not move — only the composition did"

        // ── GP 13: nothing composed means nothing offered ─────────────
        testCase "an empty composition offers no modules, no routes and every slot open"
        <| fun _ ->
            let empty = HostEnvelope.describe (ServerApp.empty, [])

            Expect.isEmpty empty.EnvelopeModules "no module composed"
            Expect.isEmpty empty.EnvelopeRoutes "no route occupied"

            Expect.isTrue
                (empty.EnvelopeSlots |> List.forall (fun s -> s.OfferState = OpenSlot))
                "every companion slot is open — a module may rely on none of them"

            Expect.isTrue
                (empty.EnvelopeCapabilities |> List.forall (fun l -> l.LayerCount = 0))
                "every capability layer is honestly empty rather than absent"

            Expect.isFalse empty.EnvelopeKnobs.IsEmpty "the knob schemas are the platform's, not the composition's"
    ]