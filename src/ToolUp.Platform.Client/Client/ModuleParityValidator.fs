// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ModuleParityValidator

open ToolUp.Platform

// ─── Phase 583 — client half of `client-server-module-parity` ─────────
//
// The composition validator's `client-server-module-parity` rule runs
// SERVER-side and can only see the server's composed modules: the client
// root's module list is a separate compilation unit, and the Server tier
// does not (and must not) reference the Client tier. So the two roots are
// not compared to each other — they are each compared to the SAME
// consumer-declared list, and two sets equal to one set are equal to each
// other.
//
// This file is that second comparison. `ClientConfig.ExpectedModules`
// carries the same declaration `ServerConfig.ExpectedModules` carries; the
// server side checks it at preflight through the Phase 9m aggregator, and
// this checks it at client boot, in the same shape as the Phase 55
// `ModuleGroupingValidator` beside it (a pure `result` decision + a
// `failwith`-ing `validate` wrapper called once from `Client.boot`).
//
// **The identity token.** Entries are module *ids*, not display names —
// the client's `ModuleDefinition.Id`, which the cross-tier identity law on
// `ModuleIdentity.componentIdOf` states is the same token as the server's
// `ServerModule.Name`. Comparing display names would be unsound: the
// client `Name` is a sidebar label ("Hello World") whose id derivation
// strips spaces ("HelloWorld"), and it is the id the server knows.
//
// **Which module list.** The consumer's own input to `Client.run` /
// `program` — the same pre-`prepareModules` list `ModuleGroupingValidator`
// is given, and for the same reason: `prepareModules` injects SDK
// built-ins (data manager, team manager, platform admin, …) whose presence
// is decided by `ClientConfig`'s `No*` switches, not by the consumer's
// module list, and which have no server-side `ServerModule` counterpart to
// be at parity with. Asserting over the post-injection list would compare
// two sets that are not meant to be equal.
//
// **Dormant by default (GP 13).** `ExpectedModules = None` — the default —
// returns `Ok` after one `match`, so boot is byte-for-byte its pre-583
// self (GP 11).

/// Pure decision function. `Ok` when no expected-module list is declared
/// (the dormant default) or when the declared set equals the consumer's
/// composed set exactly; otherwise `Error` naming both directions of the
/// mismatch.
///
/// `Some []` is a real declaration ("this root composes no consumer
/// modules"), distinct from `None`, and is checked like any other.
let result (expected: string list option) (modules: ErasedModule list) : Result<unit, string> =
    match expected with
    | None -> Ok()
    | Some declared ->
        let declaredSet = Set.ofList declared
        let composedSet = modules |> List.map _.Definition.Id |> Set.ofList

        let missing = Set.difference declaredSet composedSet |> Set.toList
        let unexpected = Set.difference composedSet declaredSet |> Set.toList

        if List.isEmpty missing && List.isEmpty unexpected then
            Ok()
        else
            let enumerate (values: string list) =
                match values with
                | [] -> "none"
                | vs -> vs |> List.map (sprintf "'%s'") |> String.concat ", "

            Error(
                sprintf
                    "Composed client module set does not match the declared ClientConfig.ExpectedModules. Declared but not composed: %s. Composed but not declared: %s. Both composition roots are measured against the same declared list (ClientConfig.ExpectedModules here, ServerConfig.ExpectedModules at server preflight), so a mismatch on either side means the two roots do not compose the same modules — a module present server-side but absent client-side has routes and handlers no UI reaches, and the reverse renders a UI whose calls 404. Entries are module IDS (ModuleDefinition.Id — the same token as the server's ServerModule.Name), not display names; a module registered as \"Hello World\" has id 'HelloWorld' unless it chains ClientModule.withId. The list is measured against the consumer's own module list, before SDK built-ins are injected. Declared: %s. Composed: %s."
                    (enumerate missing)
                    (enumerate unexpected)
                    (enumerate declared)
                    (enumerate (composedSet |> Set.toList))
            )

/// Integration wrapper following the same explicit-composition pattern as
/// `ModuleGroupingValidator.validate`: fails loud with `failwithf` so the
/// boot summary carries the message and the shell never reaches a render
/// whose module set contradicts what the deployment declared.
///
/// Takes the declared list rather than the whole `ClientConfig` — the
/// check reads exactly one field, and the narrower signature keeps this
/// module (like `ModuleGroupingValidator` beside it) exercisable from the
/// .NET-side test pack, where constructing a `ClientConfig` is not
/// possible at all: `ClientConfig.defaults` reaches
/// `AgGridModuleConfig.community`, whose Fable `import` binding throws
/// "You've hit dummy code used for Fable bindings" outside a Fable
/// compilation. `Client.boot` passes `config.ExpectedModules`.
let validate (expected: string list option) (modules: ErasedModule list) : unit =
    match result expected modules with
    | Ok() -> ()
    | Error message -> failwithf "%s" message