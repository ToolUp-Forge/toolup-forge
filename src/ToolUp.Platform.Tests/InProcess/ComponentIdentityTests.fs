module ToolUp.Platform.Tests.InProcess.ComponentIdentityTests

open Expecto
open ToolUp.Platform

// ─── Phase 279 — stable component identity (ComponentId) ──────────────
//
// Covers the three acceptance properties: declared ids are stable across
// rename + re-order; a default-derived id is deterministic; a duplicate
// explicit id is rejected at compose with a readable error (the exact
// `ComponentId.ensureUnique` call `ServerApp.run` makes before binding).
// Plus the companion-slot derivation (one slot per interface, multi-impl
// composes slot + sub-id, never positional).

/// Resolve a registered module's id the way `addModule` does — accumulate
/// it onto a fresh `ServerApp` and read it back, so the test exercises the
/// real composition path rather than a re-derivation.
let private resolvedIds (modules: ServerModule list) : ComponentId list =
    (ServerApp.empty |> ServerApp.addModules modules).ModuleComponentIds
    |> List.map snd

let tests =
    testList "ComponentIdentity" [

        // ── construction / validation ────────────────────────────────
        testCase "create rejects null / empty / whitespace"
        <| fun _ ->
            Expect.throws (fun () -> ComponentId.create "" |> ignore) "empty rejected"
            Expect.throws (fun () -> ComponentId.create "   " |> ignore) "whitespace rejected"
            Expect.throws (fun () -> ComponentId.create null |> ignore) "null rejected"

        testCase "tryCreate is None for blank, Some (trimmed) otherwise"
        <| fun _ ->
            Expect.isNone (ComponentId.tryCreate "  ") "blank -> None"
            Expect.equal (ComponentId.tryCreate "  x  ") (Some(ComponentId.create "x")) "trimmed -> Some"

        // ── per-kind namespacing keeps derivations collision-free ────
        testCase "same token under different kinds derives distinct ids"
        <| fun _ ->
            let m = ComponentId.ofModule "auth"
            let c = ComponentId.forCompanionSlot "auth"
            let d = ComponentId.forDataType "auth"
            let t = ComponentId.forTool "auth"
            Expect.isFalse (m = c) "module vs companion distinct"
            Expect.isFalse (m = d) "module vs datatype distinct"
            Expect.isFalse (c = t) "companion vs tool distinct"

        testCase "derivation is deterministic (pure projection)"
        <| fun _ ->
            Expect.equal (ComponentId.ofModule "Orders") (ComponentId.ofModule "Orders") "ofModule stable"

            Expect.equal
                (ComponentId.forCompanionSlot "IAuthProvider")
                (ComponentId.forCompanionSlot "IAuthProvider")
                "forCompanionSlot stable"

        // ── module id: stable across display-name rename ─────────────
        testCase "explicit module id is stable when the display Name is renamed"
        <| fun _ ->
            let before =
                ServerModule.create "Orders" |> ServerModule.withComponentId "orders-service"

            let afterRename =
                ServerModule.create "Order Management"
                |> ServerModule.withComponentId "orders-service"

            Expect.equal
                (resolvedIds [ before ])
                (resolvedIds [ afterRename ])
                "renaming Name does not change a declared id"

        testCase "explicit declared id is independent of the name-derived default"
        <| fun _ ->
            let explicit =
                ServerModule.create "Orders" |> ServerModule.withComponentId "orders-service"

            Expect.equal
                (resolvedIds [ explicit ])
                [ ComponentId.ofModule "orders-service" ]
                "explicit id resolves to ofModule(declaredId), not ofModule(Name)"

        // ── module id: default-derived + deterministic ───────────────
        testCase "default (no explicit id) derives deterministically from Name"
        <| fun _ ->
            let m1 = ServerModule.create "Orders"
            let m2 = ServerModule.create "Orders"
            Expect.equal (resolvedIds [ m1 ]) [ ComponentId.ofModule "Orders" ] "default = ofModule Name"
            Expect.equal (resolvedIds [ m1 ]) (resolvedIds [ m2 ]) "default-derived is deterministic"

        // ── module id: stable across registration re-order ───────────
        testCase "module ids are stable across registration re-order (non-positional)"
        <| fun _ ->
            let a = ServerModule.create "Alpha"
            let b = ServerModule.create "Bravo" |> ServerModule.withComponentId "bravo-svc"
            let forward = resolvedIds [ a; b ] |> List.sort
            let reversed = resolvedIds [ b; a ] |> List.sort
            Expect.equal forward reversed "the id set is identical regardless of order"

        // ── duplicate explicit id: rejected at compose ───────────────
        testCase "duplicate explicit ComponentId is rejected at compose with a readable error"
        <| fun _ ->
            let m1 = ServerModule.create "OrdersA" |> ServerModule.withComponentId "orders"
            let m2 = ServerModule.create "OrdersB" |> ServerModule.withComponentId "orders"
            // `ServerApp.run` makes exactly this call before anything binds.
            let ids = resolvedIds [ m1; m2 ]

            // The error names the colliding id so the failure is actionable.
            Expect.throwsC (fun () -> ComponentId.ensureUnique "module composition" ids) (fun ex ->
                Expect.stringContains ex.Message "module:orders" "error names the duplicate id")

        testCase "distinct module ids pass the compose-time uniqueness check"
        <| fun _ ->
            let m1 = ServerModule.create "Orders"
            let m2 = ServerModule.create "Inventory"
            ComponentId.ensureUnique "module composition" (resolvedIds [ m1; m2 ])
            Expect.isTrue true "no throw for distinct ids"

        testCase "findDuplicates reports each repeated id once"
        <| fun _ ->
            let ids = [ ComponentId.ofModule "a"; ComponentId.ofModule "b"; ComponentId.ofModule "a" ]

            Expect.equal (ComponentId.findDuplicates ids) [ ComponentId.ofModule "a" ] "only the repeated id"

        // ── companion slot identity ──────────────────────────────────
        testCase "one companion slot per interface (common case)"
        <| fun _ ->
            Expect.equal
                (ComponentId.forCompanionSlot "IBlobStorage")
                (ComponentId.forCompanionSlot "IBlobStorage")
                "slot id is the interface name, deterministic"

        testCase "multi-impl companion composes slot + sub-id, never positional"
        <| fun _ ->
            let splunk = ComponentId.forCompanionImpl "IAuditSink" "SplunkHec"
            let datadog = ComponentId.forCompanionImpl "IAuditSink" "DatadogLogs"
            Expect.isFalse (splunk = datadog) "different impls in one slot get different ids"
            // Same impl resolves to the same id regardless of where it sits
            // in the list — identity is by sub-id, not by index.
            Expect.equal splunk (ComponentId.forCompanionImpl "IAuditSink" "SplunkHec") "same impl -> same id"

        testCase "a multi-impl entry without a sub-id is rejected"
        <| fun _ ->
            Expect.throws
                (fun () -> ComponentId.forCompanionImpl "IAuditSink" "" |> ignore)
                "an unaddressable impl (no Kind / Name) is rejected"
    ]