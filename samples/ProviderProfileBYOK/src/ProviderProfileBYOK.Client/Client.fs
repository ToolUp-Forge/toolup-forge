// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ProviderProfileBYOK.Client

open ToolUp.Elmish
open Feliz

// Phase 44 — the non-AI BYOK sample's client half.
//
// The whole composition surface of the shared provider-profile
// component, exercised end to end: the profile API, an optional verify
// delegate, and the surface keys this app routes. Nothing here opens
// `ToolUp.AI`, and the project references no AI tier — which is what the
// phase's layering note demands and what the sample exists to hold true.
//
// This sample deliberately supplies NO verify delegate. That is the
// honest shape for a consumer with nothing that can check a provider
// key: the form renders the clearly-labelled "not verified" state and
// entries still persist. An app that DOES have a verifier adds one line:
//
//   |> ProviderProfileConfig.withVerify myVerifier
//
// where `myVerifier : ProviderCandidate -> Async<Result<string list, string>>`.

/// The two surfaces this app routes providers for. The component renders
/// one routing block per key, each with a default and any per-context
/// overrides — a context-specific rule wins over the surface default,
/// which is `ProviderProfile.resolveEntry`'s own precedence.
let private surfaces = [ "rental.gateway"; "rental.settlement" ]

let private config = ProviderProfileUI.ProviderProfileConfig.create surfaces

// ─── The host's Elmish tree ──────────────────────────────────────────
//
// A real app would fold these into its own module's Model / Msg. Kept at
// the top level here so the sample is the composition and nothing else.

type Model = { Providers: ProviderProfileUI.Model }

type Msg = ProviderMsg of ProviderProfileUI.Msg

let init () : Model * Cmd<Msg> =
    let providers, cmd = ProviderProfileUI.init config
    { Providers = providers }, Cmd.map ProviderMsg cmd

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | ProviderMsg inner ->
        let providers, cmd = ProviderProfileUI.update config inner model.Providers
        { model with Providers = providers }, Cmd.map ProviderMsg cmd

let view (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "p-6 max-w-3xl mx-auto"
        prop.children [
            Html.h1 [ prop.className "text-lg font-semibold mb-4"; prop.text "Provider settings" ]
            ProviderProfileUI.view config model.Providers (ProviderMsg >> dispatch)
        ]
    ]