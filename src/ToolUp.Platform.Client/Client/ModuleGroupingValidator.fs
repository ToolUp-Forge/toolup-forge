// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ModuleGroupingValidator

open ToolUp.Platform

// ─── Phase 55 — `withGroup` requirement preflight ─────────────────────
//
// Surfaces a class of silent UX defect: a deployment that registers
// modules without calling `|> ClientModule.withGroup "Name"` lands
// every module in the sidebar's reserved `_other` bucket. When no
// other declared group is present (Anonymous-mode deployments with
// `No*` defaults on every SDK admin module; single-module apps that
// never set a group), the sidebar's `_other` section renders with no
// title (so no chevron), starts collapsed by default, and the
// operator sees an empty sidebar — even though every module is
// registered, accessible, and wired correctly.
//
// The fix on the consumer side is a one-line `|> ClientModule.withGroup
// "<your-group-name>"` on at least one module. The check below makes
// that requirement explicit at startup rather than discovered by a
// developer staring at an empty sidebar (a real bring-up shape —
// documentation alone proved insufficient).
//
// Server-side validator placement was considered first (matching the
// other Phase 9m / Phase 6l mode validators in
// `Server/*Validator.fs`), but `ClientModule.Group` is a client-tier
// record field with no server-side projection — `ServerConfig.ModuleNames`
// carries only the string ids. The check therefore lives client-side
// alongside `validateHandlers` / `validateRequestSeam` (the Phase 13a
// explicit-composition preflights), and runs once during `Client.boot`.

/// Pure decision function — returns `Ok` when at least one consumer-
/// supplied module declares a group, otherwise `Error` with a message
/// naming a representative module and pointing at the fix.
///
/// The check runs against the consumer's input to `Client.run`/`program`
/// (pre-`prepareModules`). SDK-injected admin modules (FileManager,
/// TeamManager, PlatformAdmin, …) all carry `withGroup` calls of their
/// own, but a deployment that has `No*`'d every one of those (the
/// canonical Anonymous-mode shape) ends up with only the consumer's
/// modules in the sidebar — so the requirement is meaningfully on the
/// consumer's set, not the final post-`prepareModules` set.
///
/// `Ok` cases:
/// - Empty module list (a degenerate deployment, but not the bug this
///   validator targets).
/// - Any module with `Group = Some _`.
///
/// `Error` case:
/// - Non-empty list where every entry has `Group = None`.
let result (modules: ErasedModule list) : Result<unit, string> =
    if List.isEmpty modules then
        Ok()
    elif modules |> List.exists (fun m -> m.Group.IsSome) then
        Ok()
    else
        let firstName =
            modules
            |> List.tryHead
            |> Option.map (fun m -> m.Definition.Name)
            |> Option.defaultValue "<unnamed>"

        Error(
            sprintf
                "Every registered ClientModule has Group = None — no `|> ClientModule.withGroup \"<name>\"` call anywhere in the consumer's module list. All ungrouped modules land in the sidebar's reserved `_other` bucket; when no other declared group is present, that bucket renders with no title (no chevron to expand) and starts collapsed by default, hiding every module from the user. First ungrouped module: '%s'. Add `|> ClientModule.withGroup \"<your-group-name>\"` to at least one ClientModule before calling `ClientModule.register`; modules sharing a group name render together under a collapsible header. This requirement is independent of the deployment's declared `Surfaces` — anonymous-only deployments with `No*` defaults on every SDK admin module are the most common shape that exposes it, but any deployment whose consumer modules are entirely ungrouped will exhibit the same empty sidebar."
                firstName
        )

/// Integration wrapper following the Phase 13a explicit-composition
/// pattern (`validateHandlers` / `validateRequestSeam` in
/// `SDK.Client.fs`): fails loud with `failwithf` so the boot summary
/// surfaces the message and the shell never reaches a render with the
/// silent-failure shape.
let validate (modules: ErasedModule list) : unit =
    match result modules with
    | Ok() -> ()
    | Error msg -> failwithf "%s" msg