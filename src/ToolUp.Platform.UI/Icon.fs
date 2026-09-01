// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Icon

open Fable.Core.JsInterop
open Fable.React
open Feliz

// ─── Icon-rendering helpers ───────────────────────────────────────
//
// Module icons are imported at compile time via vite-plugin-svgr's
// `?react` query suffix — each `./icons/foo.svg?react` resolves to a
// default-exported React component. Module Icons.fs files declare:
//
//   [<ImportDefault("./icons/foo.svg?react")>]
//   let private fooSvg : obj = jsNative
//   let foo : ReactElement = Icon.ofImport fooSvg
//
// `ofImport` instantiates the imported component as a ReactElement
// with no props, so size / colour are controlled by CSS on the parent
// (e.g. Tailwind `[&>svg]:w-8 text-brand` cascades into `currentColor`-
// using strokes). One unbox + createElement per icon, centralised here
// so per-module Icons.fs stays a flat list of `let foo = ...` lines.
//
// `ofUrl` is the legacy `<img src=...>` wrapper. Kept for
// `ClientConfig.AppLogo` and any other URL-string brand asset that
// hasn't been brought through SVGR — no module icon should hit this
// after the migration.

let inline ofImport (svgComponent: obj) : ReactElement =
    // Pass an empty props object rather than `null` — the three
    // ReactLegacy.createElement overloads can't disambiguate `null`
    // between (props: obj), (children: ReactElement), and
    // (children: ReactElement seq).
    ReactLegacy.createElement (unbox<ReactElement> svgComponent, createObj [])

let ofUrl (path: string) : ReactElement = Feliz.Html.img [ Feliz.prop.src path ]