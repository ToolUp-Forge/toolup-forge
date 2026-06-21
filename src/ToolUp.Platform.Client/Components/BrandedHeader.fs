// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Components.BrandedHeader

open Feliz
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── Phase 5e — per-team document branding ────────────────────────
//
// The shell renders the app name + logo by passing the resolved
// `Branding` straight into `Layout.AppShell`. This component owns the
// two branding fields that aren't shell-chrome props: the document
// favicon (`<link rel="icon">` href) and the brand primary colour
// (the `--brand-primary` CSS custom property on `:root`). It reads the
// resolved branding from `BrandingProvider.useBranding` and re-applies
// both whenever the active team's branding changes — so a logo / colour
// edit is reflected on the next render and live on team switch, with no
// reload. It renders nothing visible.
//
// DOM head manipulation goes through `[<Emit>]` shims, mirroring the
// established `Bootstrap.MetadataHook` pattern (create-if-absent, then
// set the attribute) rather than the typed Browser.Dom surface, which
// keeps the create-or-update branch a single JS expression.

[<Emit("(function(href) { if (!href) return; var el = document.querySelector('link[rel~=\"icon\"]'); if (!el) { el = document.createElement('link'); el.setAttribute('rel', 'icon'); document.head.appendChild(el); } el.setAttribute('href', href); })($0)")>]
let private setFavicon (href: string) : unit = jsNative

// Phase 223 — per-team palette injection. The active team's hex-validated
// colour overrides (`Branding.PaletteOverrides`) are written to `:root` as the
// theming tokens the whole client surface reads (`--color-brand` / `-brand-dark`
// / `-sidebar`, `--pos` / `--neg`, + the legacy `--brand-primary`). On team
// switch any token the previous team set but the new one didn't is REMOVED, so
// the deployment's base theme shows through — a team never clobbers another's,
// nor the app default, by leaving a field blank.
[<Emit("document.documentElement.style.setProperty($0, $1)")>]
let private setVar (name: string) (value: string) : unit = jsNative

[<Emit("document.documentElement.style.removeProperty($0)")>]
let private removeVar (name: string) : unit = jsNative

[<ReactComponent>]
let BrandedHeader () =
    let branding = BrandingProvider.useBranding ()

    // Stable dependency key so the effect re-runs only when the resolved
    // overrides actually change (a list isn't a stable useEffect dep).
    let paletteKey =
        branding.PaletteOverrides
        |> List.map (fun (k, v) -> k + "=" + v)
        |> String.concat ";"

    React.useEffect (
        (fun () ->
            setFavicon branding.FaviconUrl

            let overrides = Map.ofList branding.PaletteOverrides

            for cssVar in Branding.paletteCssVars do
                match Map.tryFind cssVar overrides with
                | Some value -> setVar cssVar value
                | None -> removeVar cssVar),
        [| box branding.FaviconUrl; box paletteKey |]
    )

    Html.none