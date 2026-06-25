// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Feliz

// Pure, Fable-runtime-free home for the parameterized no-active-team
// landing surface — its config record, stable module id, and gate-target
// precedence. Deliberately a separate compilation unit ahead of
// `SDK.ClientTypes` (which `ClientConfig` lives in) so the precedence
// helper carries no `AgGrid` / `Icons` / `ClientConfig.defaults` startup
// dependencies and is therefore exercisable in the .NET test harness; the
// big client-shell file is Fable-runtime-only (`JsInterop.import` dummy
// bindings throw under .NET). The Feliz-rendered module factory lives in
// `NoActiveTeamLandingUI.fs`, after `Icons`.

/// Parameterized content for the SDK's built-in no-active-team landing
/// surface — the lightweight alternative to hand-rolling a landing module
/// and pointing `ClientConfig.NoActiveTeamLandingModuleId` at it. Supply
/// the sidebar label, the page heading + body copy, and (optionally) an
/// icon; the SDK registers the landing module for you and wires the
/// no-team gate to it. Use this when the only thing a deployment needs to
/// customise is the copy; reach for `NoActiveTeamLandingModuleId` (a full
/// consumer-registered module) when the landing needs bespoke layout or
/// behaviour. See `ClientConfig.NoActiveTeamLanding`.
type NoActiveTeamLandingConfig = {
    /// Sidebar entry label + group for the landing module (e.g. "Welcome").
    Label: string
    /// Page heading shown on the landing surface (e.g.
    /// "You're not on a team yet").
    Title: string
    /// Body paragraph rendered beneath the heading.
    Body: string
    /// Sidebar icon. `None` ⇒ the SDK default (`Icons.home`). Kept optional
    /// because `Icons` compiles after this type, so the default is resolved
    /// by the module factory at injection time, not in this record.
    Icon: ReactElement option
}

/// Identity + gate resolution for the SDK's built-in no-active-team
/// landing module (the parameterized alternative to a consumer-supplied
/// `NoActiveTeamLandingModuleId`). The stable id is shared by the module
/// factory (`NoActiveTeamLandingUI.create`, in a later compilation unit
/// once `Icons` is available) and the gate resolver
/// (`ClientConfig.effectiveNoActiveTeamLandingId`).
module NoActiveTeamLanding =
    /// Stable sidebar / gate id for the SDK built-in landing module.
    [<Literal>]
    let moduleId = "AwaitingTeam"

    /// Pure resolution of the no-active-team gate target from the two
    /// opt-in inputs, independent of the (Fable-only) `ClientConfig`: an
    /// explicit consumer-supplied custom module id wins; otherwise a
    /// parameterized landing config resolves to `moduleId`; otherwise
    /// `None` (gate inert). `ClientConfig.effectiveNoActiveTeamLandingId`
    /// is the thin wrapper that feeds this from a `ClientConfig`.
    let resolveLandingId (explicitModuleId: string option) (landing: NoActiveTeamLandingConfig option) : string option =
        match explicitModuleId with
        | Some _ as explicit -> explicit
        | None -> landing |> Option.map (fun _ -> moduleId)