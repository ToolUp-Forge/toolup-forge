// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.RedactionAllowlist

// ─── The credential-suffix allowlist (Phase 9n), extracted ───────────
//
// Property-name suffixes (matched case-insensitively against the LOWERED
// name) whose values are replaced by a redaction marker before a
// diagnostic surface persists or emits them. `ServerConfig` itself
// carries no secrets — those live in `ISecretStore` — so this is
// defence-in-depth against a future field named `*ApiKey` / `*Token` /
// `*Secret` / `*Password` leaking through a snapshot blob or a support
// bundle.
//
// **Why this file exists.** The list was deliberately duplicated when it
// had two consumers, and the duplication was guarded by a source-parsing
// parity test rather than a shared module — the reasoning being that with
// two sites, an extra `<Compile>` entry and an extra indirection for
// future readers cost more than the maintenance saving. That reasoning
// expired at three: `ConfigDriftDetector` (9q), `DiagnosticBundleHandler`
// (9n) and `ApplianceSupportBundle.SuffixFloor` (488.D) each carried a
// copy, and only two of the three were under the parity guard — so a
// suffix added to either guarded copy would have left the appliance
// bundle's floor silently behind, on the one surface where redaction is
// the load-bearing guarantee rather than defence-in-depth.
//
// Deliberately the *floor* and not a policy: nothing here decides what
// ELSE a surface masks. `ApplianceSupportBundle` still adds the
// deployment's declared field classifications on top, because a
// four-suffix credential list is not a statement about CONTENT.

/// The suffixes, lower-cased, in the order they have always been
/// written. Order is not semantic — every consumer asks "does any suffix
/// match" — but it is stable so a diff of this list reads cleanly.
let suffixes = [ "apikey"; "token"; "secret"; "password" ]

/// Whether a property name ends in a credential-shaped suffix.
///
/// Total over `null` / empty: neither matches, which is the honest
/// reading — an unnamed property is not a credential.
let shouldRedact (propertyName: string) : bool =
    if System.String.IsNullOrEmpty propertyName then
        false
    else
        let lower = propertyName.ToLowerInvariant()
        suffixes |> List.exists lower.EndsWith

/// The replacement written in place of a redacted value: the shape is
/// preserved (a length), the content is not.
///
/// A length rather than a fixed token because "this field was present and
/// 47 characters long" is genuinely diagnostic — it distinguishes an
/// absent setting from a populated one, which is often the whole question
/// — and a length is not the content.
let redactedString (length: int) : string = sprintf "<redacted:length=%d>" length