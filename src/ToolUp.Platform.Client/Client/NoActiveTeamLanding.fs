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
/// and pointing `ClientConfig.Chrome.NoActiveTeamLanding` (`NoActiveTeamLandingFill.Module`) at it. Supply
/// the sidebar label, the page heading + body copy, and (optionally) an
/// icon; the SDK registers the landing module for you and wires the
/// no-team gate to it. Use this when the only thing a deployment needs to
/// customise is the copy; reach for `NoActiveTeamLandingFill.Module` (a full
/// consumer-registered module) when the landing needs bespoke layout or
/// behaviour. See `ClientConfig.Chrome.NoActiveTeamLanding`.
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
/// `NoActiveTeamLandingFill.Module`). The stable id is shared by the module
/// factory (`NoActiveTeamLandingUI.builtIn`, in a later compilation unit
/// once `Icons` is available) and the gate resolver
/// (`ClientConfig.effectiveNoActiveTeamLandingId`).
module NoActiveTeamLanding =
    /// Stable sidebar / gate id for the SDK built-in landing module.
    [<Literal>]
    let moduleId = "AwaitingTeam"