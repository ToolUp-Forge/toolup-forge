module ToolUp.Platform.Tests.Contracts.ComponentRegistryContract

open Expecto
open ToolUp.Platform

// ─── Phase 285 — IComponentRegistryContract conformance pack ──────────
//
// The GP-12 reusable contract pack for component identity: any
// alternative or future component registry validates against the same
// Phase 279 id-stability bar. Mirrors the shipped contract packs
// (`IJobSchedulerContract`, `IShareTokenStoreContract`,
// `IClientHostCapabilitiesContract`) — a suite parameterised over a
// registry witness, asserting the identity laws independent of any
// concrete implementation:
//
//   1. Declared ids are stable across a display-name **rename**.
//   2. Ids are stable across registration **re-order** (non-positional).
//   3. Default (no explicit id) derivation is **deterministic** and
//      non-positional.
//   4. A **duplicate** explicit id is rejected at compose.
//   5. The Phase 280 manifest enumeration is **complete + id-keyed** (the
//      manifest's module ids are exactly the registry's resolved ids).
//
// This file ships no runtime surface (test/build infra only) and is
// byte-for-byte absent from any consumer build (GP 13).

/// A registry witness: everything the identity laws need, expressed as
/// operations over module declarations `(displayName, explicitId option)`
/// so the pack is independent of any concrete registry. The in-tree
/// default binding (`defaultWitness`) drives the real `ServerApp` /
/// `ServerModule` composition path.
type ComponentRegistryWitness = {
    /// Resolve the component ids the registry accumulates for the given
    /// module declarations, in registration order.
    ResolveModuleIds: (string * string option) list -> ComponentId list
    /// The Phase 280 manifest's module component ids for the same
    /// declarations.
    ManifestModuleIds: (string * string option) list -> ComponentId list
    /// The registry's compose-time duplicate-id enforcement (raises on a
    /// collision).
    EnsureUnique: ComponentId list -> unit
}

// ── the identity laws as standalone checks (raise on violation) ───────
// Each takes a witness and asserts one law with Expect.*, so it throws an
// AssertException on a non-conforming registry — which is exactly what
// the self-test below relies on to prove the pack has teeth.

let lawRenameStable (w: ComponentRegistryWitness) : unit =
    // One module, same explicit id, two different display names → same id.
    Expect.equal
        (w.ResolveModuleIds [ "Orders", Some "orders-service" ])
        (w.ResolveModuleIds [ "Order Management", Some "orders-service" ])
        "an explicit id is stable when the display name is renamed"

let lawReorderStable (w: ComponentRegistryWitness) : unit =
    let forward =
        w.ResolveModuleIds [ "Alpha", None; "Bravo", Some "bravo-svc" ] |> List.sort

    let reversed =
        w.ResolveModuleIds [ "Bravo", Some "bravo-svc"; "Alpha", None ] |> List.sort

    Expect.equal forward reversed "the resolved id set is identical regardless of registration order (non-positional)"

let lawDefaultDeterministic (w: ComponentRegistryWitness) : unit =
    Expect.equal
        (w.ResolveModuleIds [ "Orders", None ])
        (w.ResolveModuleIds [ "Orders", None ])
        "a default (name-derived) id is deterministic"

    Expect.equal
        (w.ResolveModuleIds [ "Orders", None ])
        [ ComponentId.ofModule "Orders" ]
        "a default id derives from the display name under the module slot"

let lawDuplicateRejected (w: ComponentRegistryWitness) : unit =
    let ids = w.ResolveModuleIds [ "OrdersA", Some "orders"; "OrdersB", Some "orders" ]

    Expect.throwsC (fun () -> w.EnsureUnique ids) (fun ex ->
        Expect.stringContains ex.Message "orders" "the duplicate-id error names the colliding id")

let lawManifestComplete (w: ComponentRegistryWitness) : unit =
    let decls = [ "Orders", Some "orders-service"; "Inventory", None ]

    Expect.equal
        (w.ManifestModuleIds decls |> List.sort)
        (w.ResolveModuleIds decls |> List.sort)
        "the manifest enumeration is complete + id-keyed — exactly the registry's resolved ids"

/// The reusable pack: run every identity law against `witness`.
let laws (name: string) (witness: ComponentRegistryWitness) : Test =
    testList $"{name} — IComponentRegistry contract" [
        testCase "declared id is stable across a display-name rename"
        <| fun _ -> lawRenameStable witness
        testCase "ids are stable across registration re-order (non-positional)"
        <| fun _ -> lawReorderStable witness
        testCase "default derivation is deterministic + non-positional"
        <| fun _ -> lawDefaultDeterministic witness
        testCase "a duplicate explicit id is rejected at compose"
        <| fun _ -> lawDuplicateRejected witness
        testCase "the Phase 280 manifest enumeration is complete + id-keyed"
        <| fun _ -> lawManifestComplete witness
    ]

// ── the in-tree default registry as the first conformance witness ─────

let private modulesOf (decls: (string * string option) list) : ServerModule list =
    decls
    |> List.map (fun (name, explicit) ->
        let m = ServerModule.create name

        match explicit with
        | Some id -> m |> ServerModule.withComponentId id
        | None -> m)

let defaultWitness: ComponentRegistryWitness = {
    ResolveModuleIds =
        fun decls ->
            (ServerApp.empty |> ServerApp.addModules (modulesOf decls)).ModuleComponentIds
            |> List.map snd
    ManifestModuleIds =
        fun decls ->
            let app = ServerApp.empty |> ServerApp.addModules (modulesOf decls)
            (ServerApp.compositionManifest app).Modules |> List.map _.Id
    EnsureUnique = ComponentId.ensureUnique "module composition"
}

/// The pack bound to the in-tree default registry — wired into
/// `Build.fsproj -- VerifyAll` so an identity-law regression fails CI.
let tests = laws "default ServerApp registry" defaultWitness

// ── self-test: the pack fails a non-conforming registry ───────────────
// Proves the acceptance property "a deliberately unstable / positional /
// duplicate-tolerating registry fails the pack" — each law throws when
// its witness violates it.

/// A witness that derives ids from the display *name*, ignoring the
/// explicit id — so a rename changes the id (violates law 1).
let private renameUnstableWitness = {
    defaultWitness with
        ResolveModuleIds = fun decls -> decls |> List.map (fun (name, _) -> ComponentId.ofModule name)
}

/// A witness that folds the registration index into the id — so a
/// re-order changes the id (violates law 2).
let private positionalWitness = {
    defaultWitness with
        ResolveModuleIds =
            fun decls ->
                decls
                |> List.mapi (fun i (name, _) -> ComponentId.ofModule (sprintf "%s@%d" name i))
}

/// A witness that tolerates duplicate ids (violates law 4).
let private duplicateTolerantWitness = {
    defaultWitness with
        EnsureUnique = ignore
}

let selfTests =
    testList "IComponentRegistry contract — self-test (pack has teeth)" [
        testCase "a rename-unstable registry fails the rename law"
        <| fun _ ->
            Expect.throws (fun () -> lawRenameStable renameUnstableWitness) "a rename-unstable registry must fail"

        testCase "a positional registry fails the re-order law"
        <| fun _ -> Expect.throws (fun () -> lawReorderStable positionalWitness) "a positional registry must fail"

        testCase "a duplicate-tolerating registry fails the duplicate law"
        <| fun _ ->
            Expect.throws
                (fun () -> lawDuplicateRejected duplicateTolerantWitness)
                "a duplicate-tolerating registry must fail"
    ]