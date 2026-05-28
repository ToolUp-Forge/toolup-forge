// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.FeatureFlags

open Feliz
open Fable.Core
open Fable.Core.JsInterop

let private log = Logger.forCategory "client.flags"

/// React context carrying the resolved feature-flag map. The shell
/// mounts a provider at the root (`SDK.Client.view`) supplying the
/// prefetched snapshot from `Model.ResolvedFlags`. Default value is
/// the empty map — before the prefetch completes, or in tests that
/// render a component without the shell.
let private flagsContext =
    React.createContext<Map<string, FlagValue>> (defaultValue = Map.empty)

/// React context carrying the union of declared flag keys the client
/// knows about: every `ClientModule.FeatureFlags` entry collected at
/// boot plus every key the server surfaced in the resolved map.
/// `flag` / `variant` use this to `console.warn` (once per key) when
/// a view reads an undeclared key — typo protection for module
/// authors. Default value is the empty set; when the set is empty
/// the warning is suppressed so isolated tests and apps that declare
/// no flags don't spam the console.
let private declaredKeysContext =
    React.createContext<Set<string>> (defaultValue = Set.empty)

/// Wrap children in a context provider supplying the given flag map.
/// Used by the shell; deep views read the map via `useFlags` /
/// `flag` / `variant` hooks rather than threading the map through
/// props. `declared` is the client's total known set of declared
/// keys (module-declared ∪ server-resolved) and drives typo
/// warnings — pass `Set.empty` to disable (tests, apps with no
/// flags).
let provider (declared: Set<string>) (flags: Map<string, FlagValue>) (children: ReactElement) =
    flagsContext.Provider(flags, declaredKeysContext.Provider(declared, children))

/// Read the current resolved flag map from context. Hook — must be
/// called from a React component body. Returns `Map.empty` when no
/// provider is mounted (e.g. isolated tests) rather than throwing.
let useFlags () : Map<string, FlagValue> = React.useContext flagsContext

/// Read the declared-keys set from context. Hook — returns
/// `Set.empty` outside a provider.
let useDeclaredKeys () : Set<string> = React.useContext declaredKeysContext

/// Per-session memo of keys we've already warned about so a repeated
/// lookup on the same undeclared key only logs once. Scoped to the
/// browser tab; never trimmed because the set is tiny by
/// construction (one entry per misspelled key in the source).
let private warnedKeys = System.Collections.Generic.HashSet<string>()

/// Emit a one-shot `Warn` the first time an undeclared key is read.
/// Suppressed when `declared` is empty — that indicates either a
/// test render with no provider or an app that declares no flags,
/// neither of which should produce noise.
let private warnIfUndeclared (declared: Set<string>) (key: string) =
    if not (Set.isEmpty declared) && not (declared.Contains key) && warnedKeys.Add key then
        log.Warn
            $"read of undeclared key '{key}'. Declare it on the module via `ClientModule.FeatureFlags` or on the server via `ServerConfig.FeatureFlags`."

/// Boolean-flag convenience. Hook — must be called from a React
/// component body. Returns `false` for unknown keys and for keys
/// whose resolved value is a `Variant` (declared-shape drift would
/// be surprising; callers who want variant-typed flags use
/// `variant`). Emits a one-shot `console.warn` on reads against
/// undeclared keys (see `declaredKeysContext`).
let flag (key: string) : bool =
    let flags = useFlags ()
    let declared = useDeclaredKeys ()
    warnIfUndeclared declared key

    match flags |> Map.tryFind key with
    | Some(FlagValue.Bool b) -> b
    | _ -> false

/// Variant-flag convenience. Hook — must be called from a React
/// component body. Returns `""` for unknown keys and for keys whose
/// resolved value is a `Bool`. Emits a one-shot `console.warn` on
/// reads against undeclared keys.
let variant (key: string) : string =
    let flags = useFlags ()
    let declared = useDeclaredKeys ()
    warnIfUndeclared declared key

    match flags |> Map.tryFind key with
    | Some(FlagValue.Variant(_, v)) -> v
    | _ -> ""