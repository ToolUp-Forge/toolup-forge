// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Component-scoped configuration binding (ComponentConfig) ─────────
//
// A configuration section addressable by a stable Phase 279
// `ComponentId`. Today a deployment's config is a global `ServerConfig`
// plus per-module `ConfigSchema`; with stable ids a component's config
// becomes addressable, validated, and overridable *by id* rather than by
// an ad-hoc global key. A deployment can override exactly one
// component's configuration through a deterministic id-scoped env var
// (`TOOLUP_COMPONENT__<id>__<key>`) and have that override validated
// through the existing `IConfigValidator` preflight aggregator.
//
// This file carries only the Fable-safe section *type* + the pure,
// deterministic env-var-name derivation (so declaration and resolution
// agree on both tiers). The Server-side resolver — merge the declared
// section with the id-scoped env override, and the preflight validator
// that rejects an override that targets no known component — lives in
// `ToolUp.Platform.ComponentConfigResolver` (`ToolUp.Platform.Server`).
//
// Zero cost when unused (GP 11): a deployment that declares no
// `ComponentConfig` and sets no `TOOLUP_COMPONENT__*` override behaves
// exactly as before — the global `ServerConfig` path is untouched.

/// A configuration section owned by one composed component, keyed by its
/// stable Phase 279 `ComponentId`. `Values` are the component's declared
/// defaults; an id-scoped env override (`ComponentConfig.envVarName`)
/// merges on top at resolution time (server tier).
type ComponentConfig = {
    /// The component this section configures.
    Component: ComponentId
    /// The declared key → value defaults for this component.
    Values: Map<string, string>
}

/// Construction + the deterministic id-scoped env-var-name derivation
/// for `ComponentConfig`. The derivation is a pure string projection so
/// a config editor / scaffolding tool computes the same override name a
/// deployment sets, without server-tier code.
module ComponentConfig =

    /// The env-var prefix every id-scoped component override carries.
    /// A deployment overrides `key` for component `id` by setting
    /// `<Prefix><canonical id>__<canonical key>`.
    [<Literal>]
    let EnvVarPrefix = "TOOLUP_COMPONENT__"

    /// A `ComponentConfig` with the given declared defaults.
    let create (componentId: ComponentId) (values: (string * string) list) : ComponentConfig = {
        Component = componentId
        Values = Map.ofList values
    }

    /// An empty section for a component (no declared defaults).
    let empty (componentId: ComponentId) : ComponentConfig = {
        Component = componentId
        Values = Map.empty
    }

    /// A declared value for `key`, if the section declares one. (Does not
    /// consult env overrides — that is the server-tier resolver's job.)
    let tryValue (key: string) (config: ComponentConfig) : string option = config.Values |> Map.tryFind key

    /// Fold an arbitrary token into a legal env-var segment: upper-case,
    /// every run of non-`[A-Z0-9]` characters collapsed to a single `_`,
    /// and leading / trailing `_` trimmed. Deterministic + total. This is
    /// what turns a slot-prefixed id (`companion:IAuditSink/SplunkHec`,
    /// `module:orders-service`) into a shell-legal env token
    /// (`COMPANION_IAUDITSINK_SPLUNKHEC`, `MODULE_ORDERS_SERVICE`).
    /// Collapsing runs guarantees no interior `__`, so the `__`
    /// delimiter between the id segment and the key segment stays
    /// unambiguous.
    let canonicalSegment (raw: string) : string =
        if System.String.IsNullOrEmpty raw then
            ""
        else
            let sb = System.Text.StringBuilder(raw.Length)
            let mutable pendingUnderscore = false

            for ch in raw.ToUpperInvariant() do
                if (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') then
                    if pendingUnderscore && sb.Length > 0 then
                        sb.Append '_' |> ignore

                    pendingUnderscore <- false
                    sb.Append ch |> ignore
                else
                    pendingUnderscore <- true

            sb.ToString()

    /// The env-var name that overrides `key` for component `id`:
    /// `TOOLUP_COMPONENT__<canonical id>__<canonical key>`. Deterministic
    /// and total; the same derivation the server-tier resolver reads and
    /// the preflight validator enumerates.
    let envVarName (componentId: ComponentId) (key: string) : string =
        EnvVarPrefix
        + canonicalSegment (ComponentId.value componentId)
        + "__"
        + canonicalSegment key