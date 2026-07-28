// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 580 — tier-shared module identity ─────────────────────────
//
// A module's id is the real key on BOTH tiers: server-side
// `ServerModule.Name` is the permission key + the `ComponentId` seed,
// client-side `ModuleDefinition.Id` is the `Model.ModuleStates` map key,
// the sidebar-filter lookup, and the `AIMessageRequest.ActiveModule`
// payload. The two must agree — `ServerModule.Name` is documented as
// "must match the client `ClientModule.Definition.Id`" — so the
// derivation and the uniqueness gate live here once, in Core, and both
// tiers read the same law rather than each carrying its own copy.
//
// Two gaps this closes on the client tier:
//
//   1. `ServerApp.run` runs `ComponentId.ensureUnique` over the resolved
//      module ids before anything binds. The client shell keyed
//      `Model.ModuleStates` by `Definition.Id` in a `Map` with no
//      equivalent check, so two modules sharing an id silently collapsed
//      into one map entry — the second module's `Init` state overwrote
//      the first's, and every subsequent `update` for either module ran
//      against whichever `Model` won. `ensureUnique` below makes that a
//      compose-time failure naming both display names and the colliding
//      id (the same shape as the server's gate and the Phase 579
//      duplicate-query-handler rejection).
//
//   2. `ClientModule.create` auto-derives `Id` from `Name` with spaces
//      stripped, and nothing surfaced which ids were derived that way
//      versus declared with `ClientModule.withId`. `table` below renders
//      the composed module-id table with each row's origin so a consumer
//      can audit the derivation instead of inferring it.
//
// Purely structural (GP 9) — the SDK never names a module — and pure, so
// it is Fable-safe and ships under `fable/` with the rest of Core.

/// Where a composed module's id came from, relative to the SDK's
/// name-derivation rule (`ModuleIdentity.deriveId`).
///
/// The classification is *structural*, not provenance-tracked: an id
/// equal to its module's derived id reports `DerivedFromName` whether
/// the consumer omitted `ClientModule.withId` or passed exactly the
/// derived value. That equivalence is the point — the question the
/// diagnostics answer is "which ids does the name determine?", so an
/// explicit declaration that happens to match the derivation is, for
/// audit purposes, the same id under the same rule.
type ModuleIdOrigin =
    /// The id is what `deriveId` produces from the module's display
    /// `Name` — i.e. renaming the module would change the id unless the
    /// consumer pins it with `ClientModule.withId`.
    | DerivedFromName
    /// The id differs from the name-derivation, so it was declared
    /// explicitly (`ClientModule.withId` / `ServerModule.withComponentId`)
    /// and is rename-stable.
    | ExplicitlyDeclared

/// One row of the composed module-identity table: the module's declared
/// id, its display name, the `ComponentId` that id lifts to, and whether
/// the id is name-derived or explicitly declared.
type ModuleIdentityRow = {
    /// The module's declared id — `ClientModule.Definition.Id` on the
    /// client tier, `ServerModule.Name` on the server tier.
    Id: string
    /// The module's display name (client `Definition.Name`). Equal to
    /// `Id` on the server tier, where the two are the same field.
    Name: string
    /// `ComponentId.ofModule Id` — the stable, slot-namespaced identity
    /// the introspection / telemetry-correlation surfaces address.
    ComponentId: ComponentId
    /// Whether `Id` is the name-derivation or an explicit declaration.
    Origin: ModuleIdOrigin
}

/// Derivation, classification, tabulation, and the compose-time
/// uniqueness gate for module ids. Shared by both tiers so the client
/// shell and the server composition root cannot drift apart on what a
/// module's identity is.
module ModuleIdentity =

    /// The SDK's module-id derivation from a display name: spaces
    /// stripped, nothing else. This is the rule `ClientModule.create`
    /// applies when the consumer does not chain `ClientModule.withId`,
    /// lifted here verbatim so the law has one home.
    ///
    /// A `null` name derives the empty string rather than raising —
    /// classification and tabulation are diagnostics surfaces and must
    /// never be the thing that crashes a boot.
    let deriveId (name: string) : string =
        if System.String.IsNullOrEmpty name then
            ""
        else
            name.Replace(" ", "")

    /// Classify a module's `(name, id)` pair against `deriveId`.
    let originOf (name: string) (id: string) : ModuleIdOrigin =
        if id = deriveId name then
            DerivedFromName
        else
            ExplicitlyDeclared

    /// Lift a module's declared id to its stable `ComponentId`. Thin
    /// alias for `ComponentId.ofModule` named at the module-identity
    /// call site — the *cross-tier identity law*: the client's
    /// `Definition.Id` and the server's `ServerModule.Name` are the same
    /// token, so both tiers resolve the same `ComponentId` for the same
    /// module.
    let componentIdOf (id: string) : ComponentId = ComponentId.ofModule id

    /// Build one table row from a module's `(name, id)` pair.
    let row (name: string) (id: string) : ModuleIdentityRow = {
        Id = id
        Name = name
        ComponentId = componentIdOf id
        Origin = originOf name id
    }

    /// Build the composed module-identity table from `(name, id)` pairs,
    /// preserving composition order.
    let table (modules: (string * string) list) : ModuleIdentityRow list =
        modules |> List.map (fun (name, id) -> row name id)

    /// The colliding ids in a table, each paired with every display name
    /// that resolved to it, in first-seen order. Empty when every id is
    /// unique.
    let collisions (rows: ModuleIdentityRow list) : (ComponentId * string list) list =
        rows
        |> List.groupBy _.ComponentId
        |> List.filter (fun (_, group) -> List.length group > 1)
        |> List.map (fun (componentId, group) -> componentId, group |> List.map _.Name)

    /// Render the composed table as one aligned line per module — the
    /// inspectable derived-vs-explicit audit surface. Emitted as the
    /// tail of the compose-time failure message and available to
    /// consumers directly (client: `Client.moduleIdentityReport`).
    let render (rows: ModuleIdentityRow list) : string =
        if List.isEmpty rows then
            "  (no modules composed)"
        else
            let width = rows |> List.map (fun r -> r.Id.Length) |> List.max

            rows
            |> List.map (fun r ->
                let origin =
                    match r.Origin with
                    | DerivedFromName -> "derived from Name"
                    | ExplicitlyDeclared -> "explicitly declared"

                sprintf
                    "  %s  %s  (%s, name=\"%s\")"
                    (r.Id.PadRight width)
                    (ComponentId.value r.ComponentId)
                    origin
                    r.Name)
            |> String.concat "\n"

    /// Render the compose-time failure text for a table carrying
    /// duplicate ids. Every collision is reported (not just the first)
    /// so one restart names the whole misconfiguration, and each names
    /// every colliding module's display `Name` — the id alone is not
    /// enough to find two registrations in a composition root.
    let private renderCollisions
        (context: string)
        (rows: ModuleIdentityRow list)
        (dups: (ComponentId * string list) list)
        =
        let perCollision =
            dups
            |> List.map (fun (componentId, names) ->
                let id =
                    rows
                    |> List.tryFind (fun r -> r.ComponentId = componentId)
                    |> Option.map _.Id
                    |> Option.defaultValue (ComponentId.value componentId)

                sprintf
                    "module id \"%s\" (%s) is registered by %d modules: %s"
                    id
                    (ComponentId.value componentId)
                    (List.length names)
                    (names |> List.map (sprintf "\"%s\"") |> String.concat ", "))
            |> String.concat "; "

        sprintf
            "%s: duplicate module id(s) detected — %s. The shell keys per-module state by the module id, so all but one registration would be silently collapsed into a single state entry and every module sharing the id would run its update against whichever Model won. Give each module a distinct id: chain `ClientModule.withId \"<distinct-id>\"` before `register` (or rename the module — an id left unset is derived from Name with spaces stripped). Composed module-id table:\n%s"
            context
            perCollision
            (render rows)

    /// Raise a readable error if any id in the table is duplicated;
    /// no-op when every id is unique. `context` names the composition
    /// stage so the failure points at where to look (e.g.
    /// `"client module composition"`). Structural enforcement — a
    /// collision is a compose-time failure, not a runtime surprise —
    /// mirroring the server's `ComponentId.ensureUnique` gate in
    /// `ServerApp.run`. A composition with unique ids is unaffected
    /// (GP 11).
    let ensureUnique (context: string) (rows: ModuleIdentityRow list) : unit =
        match collisions rows with
        | [] -> ()
        | dups -> failwith (renderCollisions context rows dups)