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

[<Emit("(function(color) { if (!color) return; document.documentElement.style.setProperty('--brand-primary', color); })($0)")>]
let private setPrimaryColour (color: string) : unit = jsNative

[<ReactComponent>]
let BrandedHeader () =
    let branding = BrandingProvider.useBranding ()

    React.useEffect (
        (fun () ->
            setFavicon branding.FaviconUrl
            setPrimaryColour branding.PrimaryColor),
        [| box branding.FaviconUrl; box branding.PrimaryColor |]
    )

    Html.none