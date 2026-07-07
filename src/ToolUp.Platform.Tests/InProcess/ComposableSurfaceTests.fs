module ToolUp.Platform.Tests.InProcess.ComposableSurfaceTests

open Expecto
open System
open FSharp.Reflection
open ToolUp.Platform

// ─── Phase 293 — composable-surface descriptor ────────────────────────
//
// Covers the acceptance shape: the descriptor enumerates every shipped
// companion slot (single- and multi-impl) with its substrate
// requirements, the composition-shaping config-knob schemas, and the
// module contract — all DERIVED from the live `ServerApp` / `ServerConfig`
// registry (reflection), never a hand-declared list, so a newly-shipped
// slot surfaces with no descriptor code change. Distinct from the Phase
// 280 manifest (vocabulary vs composed instance), sharing the
// `ComponentId` slot-id space.

/// Independently recompute the companion-interface set by reflecting the
/// `ServerApp` record's interface-typed option / list fields — the same
/// derivation `ComposableSurface.slots` performs. Equality between this
/// and the descriptor's interfaces is the "derived, not hand-listed"
/// proof: the descriptor cannot silently omit (or invent) a slot.
let private reflectedCompanionInterfaces () : Set<string> =
    FSharpType.GetRecordFields(typeof<ServerApp>)
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

let tests =
    testList "ComposableSurface" [

        // ── slots derived from the live ServerApp registry ────────────
        testCase "enumerates the representative shipped companion slots"
        <| fun _ ->
            let interfaces = ComposableSurface.slots () |> List.map _.Interface |> Set.ofList

            Expect.isTrue
                (Set.isSubset
                    (Set.ofList [
                        "IAuthProvider"
                        "IBlobStorage"
                        "INotificationChannel"
                        "IAuditSink"
                        "IHealthCheck"
                        "IConfigValidator"
                    ])
                    interfaces)
                "the principal single- and multi-impl companion slots are present"

        testCase "the slot set is exactly the reflected companion-interface set (derived, not listed)"
        <| fun _ ->
            let descriptorInterfaces =
                ComposableSurface.slots () |> List.map _.Interface |> Set.ofList

            Expect.equal
                descriptorInterfaces
                (reflectedCompanionInterfaces ())
                "the descriptor derives its slots from the ServerApp record — no hand-maintained list can drift"

        testCase "the derivation surfaces a broad set, not a hand-picked few"
        <| fun _ ->
            // A hand-listed descriptor would carry a handful; the reflected
            // set is large. ILogger (Logger: ILogger option) is discovered
            // purely by reflection — it is not named in ComposableSurface.fs.
            let interfaces = ComposableSurface.slots () |> List.map _.Interface |> Set.ofList
            Expect.isGreaterThanOrEqual (Set.count interfaces) 8 "the reflected slot set is non-trivial"
            Expect.contains interfaces "ILogger" "a slot named nowhere in the descriptor is discovered by reflection"

        // ── cardinality: option → Single, list → Multi ───────────────
        testCase "cardinality distinguishes single-impl options from multi-impl lists"
        <| fun _ ->
            let byIface =
                ComposableSurface.slots ()
                |> List.map (fun s -> s.Interface, s.Cardinality)
                |> Map.ofList

            Expect.equal
                (byIface |> Map.tryFind "IAuthProvider")
                (Some SingleImpl)
                "IAuthProvider is a single-impl slot"

            Expect.equal (byIface |> Map.tryFind "IAuditSink") (Some MultiImpl) "IAuditSink is a multi-impl slot"

        // ── slot id shares the Phase 279 / 280 slot-id space ──────────
        testCase "slot ids are companion:<interface>, matching the manifest slot-id space"
        <| fun _ ->
            let auth =
                ComposableSurface.slots () |> List.find (fun s -> s.Interface = "IAuthProvider")

            Expect.equal
                auth.Slot
                (ComponentId.forCompanionSlot "IAuthProvider")
                "the slot id lines up with ComponentId.forCompanionSlot / the composed manifest"

        // ── substrate requirements surface as best-effort metadata ────
        testCase "a slot's substrate requirements surface"
        <| fun _ ->
            let audit =
                ComposableSurface.slots () |> List.find (fun s -> s.Interface = "IAuditSink")

            Expect.contains
                audit.SubstrateRequirements
                "ISecretStore"
                "IAuditSink declares its ISecretStore substrate need"

        // ── config-knob schemas derived from ServerConfig enum fields ─
        testCase "enumerates the composition-shaping config-knob schemas"
        <| fun _ ->
            let knobs = ComposableSurface.configKnobs ()
            let names = knobs |> List.map _.Name |> Set.ofList

            Expect.contains names "ProcessProfile" "the process-profile mode knob is enumerated"

            Expect.isTrue
                (knobs |> List.forall (fun k -> not k.Values.IsEmpty))
                "every knob schema lists its admissible values"

        // ── module contract present as data ───────────────────────────
        testCase "describes the four-file module contract"
        <| fun _ ->
            let surface = ComposableSurface.describe ()

            Expect.equal
                surface.ModuleContract.Files
                [ "SharedTypes"; "Server"; "ClientModel"; "ClientView" ]
                "the module contract enumerates the four-file convention"

            Expect.equal surface.ModuleContract.ModuleSlotPrefix "module" "module slot prefix matches Phase 279"

        // ── pure + deterministic (GP 13) ──────────────────────────────
        testCase "describe is deterministic"
        <| fun _ ->
            Expect.equal (ComposableSurface.describe ()) (ComposableSurface.describe ()) "same surface each call"
    ]