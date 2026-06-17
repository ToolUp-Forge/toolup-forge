// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.LoadingIndicatorContext

open Feliz
open Toolup.UIToolkit

// ─── Loading-indicator React context ──────────────────────────────
//
// Surfaces the deployment's configured `LoadingIndicatorMode` to module
// views so a module can render the app's chosen loading affordance while
// it waits for its own data. The mode lives on `ClientConfig`
// (Client-tier), but `ClientModuleContext` — the record a module's
// `Init` receives — is a `ToolUp.Platform.Core` type with no Feliz
// dependency and therefore cannot carry a `ReactElement` (or the
// `LoadingIndicatorMode` DU, whose `CustomLoader` case is Feliz-typed).
// So the indicator travels the same idiomatic path as `FeatureFlags` /
// `ProcessedDataContext`: a React context the shell provides at the root
// of the view tree and module components read via a hook.

/// React context carrying the configured indicator mode. The shell
/// mounts a provider in `SDK.Client.view` from
/// `ClientConfig.LoadingIndicator`. Default `SkeletonLoader` matches
/// `ClientConfig.defaults`, so an isolated render (tests, no provider)
/// degrades to the same compact skeleton.
let private context =
    React.createContext<LoadingIndicatorMode> (defaultValue = SkeletonLoader)

/// Wrap children in a provider supplying the configured indicator mode.
/// Used by the shell; deep module views read it via `useIndicator`
/// rather than threading config through props or `ClientModuleContext`.
let provider (mode: LoadingIndicatorMode) (children: ReactElement) = context.Provider(mode, children)

/// Hook — returns the resolved in-content loading element for the
/// deployment's configured indicator (`Layout.loadingIndicatorInline`).
/// Must be called from a React component body (e.g. a
/// `[<ReactComponent>]`-attributed function); returns the
/// `SkeletonLoader` default outside a provider.
let useIndicator () : ReactElement =
    React.useContext context |> Layout.loadingIndicatorInline