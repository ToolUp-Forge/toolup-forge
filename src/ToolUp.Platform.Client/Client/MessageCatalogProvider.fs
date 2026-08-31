// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.MessageCatalogProvider

open Feliz
open Fable.Core.JsInterop

// ─── Message-catalog React context (Phase 444) ────────────────────────
//
// The publishing half of the Phase 444 substrate: the shell resolves the
// catalog once per render (locale from `ClientConfig.Locale` +
// `Model.PlatformConfig` + the browser, then the deployment's
// `MessageCatalogOverride`) and provides it here; every shell-chrome and
// built-in-module view reads it with `useMessages ()`.
//
// Same shape and the same rationale as `BrandingProvider` (Phase 5e):
// a React context the shell mounts at the view-tree root rather than a
// value threaded through props or through the Feliz-free
// `ClientModuleContext`. Resolution is live on team switch for the same
// reason branding is — `TeamSwitched` clears `PlatformConfig` and the
// `ConfigsLoaded` reload repopulates it, re-running the resolve.
//
// Outside a provider the hook returns the built-in English catalog, so
// an isolated component render (a Storybook-style harness, a unit test
// mounting one view) neither crashes nor renders blanks.
//
// This file is the boundary where the Feliz dependency starts. The
// decisions themselves — which locale wins, what the resolved catalog is
// — are in `MessageCatalog.fs`, which is Feliz-free and therefore
// exercised directly by the .NET in-process harness.

let private context =
    React.createContext<MessageCatalog> (defaultValue = MessageCatalog.english)

/// Wrap children in a provider supplying the resolved catalog. One call
/// site, in the shell's `view`.
let provider (catalog: MessageCatalog) (children: ReactElement) = context.Provider(catalog, children)

/// Hook — the resolved `MessageCatalog`. Must be called from a React
/// component body; returns the built-in English catalog outside a
/// provider.
let useMessages () : MessageCatalog = React.useContext context

/// Hook — the resolved BCP 47 locale tag. Sugar over
/// `(useMessages ()).Locale` for the call sites that want the tag for
/// an `Intl` call rather than a string from the catalog.
let useLocale () : string = (useMessages ()).Locale

/// The visitor's browser locale preference, or `None` where there is no
/// browser to ask. Read defensively rather than through a typed
/// `Browser.Navigator` binding on purpose: this runs during the shell's
/// `view`, and a host without `navigator` (a prerender pass under Node,
/// a jsdom harness that stubs part of the DOM) must degrade to the
/// declared fallback rather than throw mid-render.
let browserLocale () : string option =
    let raw: string =
        emitJsExpr
            ()
            "(typeof navigator !== 'undefined' && (navigator.language || (navigator.languages && navigator.languages[0]))) || ''"

    if System.String.IsNullOrWhiteSpace raw then
        None
    else
        Some(raw.Trim())

// ─── Date / number formatting ─────────────────────────────────────────
//
// GP 13 — the SDK bundles no CLDR data and never will. Every
// locale-aware format below delegates to the browser's own `Intl`,
// which already carries the data for every locale the browser supports
// and costs the bundle nothing.
//
// These take the locale as a parameter rather than reading the context
// themselves, so they are callable from a `let`-bound helper as well as
// from a component body — the hook rule otherwise makes the obvious
// factoring (a formatting helper shared by several rows of a table)
// illegal. Pair them with `useLocale ()` at the component boundary.
//
// Under Fable these compile to a direct `Intl` call. On .NET the
// `emitJsExpr` bodies are unreachable, which is why nothing on the
// .NET-testable side of the substrate calls them.

/// Format a number for `locale` via `Intl.NumberFormat`.
let formatNumber (locale: string) (value: float) : string =
    let formatted = emitJsExpr (locale, value) "new Intl.NumberFormat($0).format($1)"
    string formatted

/// Format a currency amount for `locale` via `Intl.NumberFormat`.
/// `currencyCode` is an ISO 4217 code ("GBP", "EUR", …).
let formatCurrency (locale: string) (currencyCode: string) (value: float) : string =
    let formatted =
        emitJsExpr
            (locale, currencyCode, value)
            "new Intl.NumberFormat($0, { style: 'currency', currency: $1 }).format($2)"

    string formatted

/// Format a date for `locale` via `Intl.DateTimeFormat`.
let formatDate (locale: string) (value: System.DateTime) : string =
    let isoString = value.ToString("o")

    let formatted =
        emitJsExpr (locale, isoString) "new Intl.DateTimeFormat($0).format(new Date($1))"

    string formatted