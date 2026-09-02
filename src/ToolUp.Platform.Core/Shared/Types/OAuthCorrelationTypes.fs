// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Neutral OAuth correlation identifier (Phase 43.B) ───────────
//
// The Phase 10e `IOAuthCredentialFlow` substrate keyed every part of
// an Authorization Code round-trip — the state-store entry, the
// persisted refresh-token secret key, the audit payload — on a
// `DataSourceId`. That was correct while the only consumer was a data
// -source connector; it is not once a *provider-profile entry* wants
// the same flow (the S1 "Connect Anthropic" path). `DataSourceId` is
// a `string` alias, so a provider entry COULD have been smuggled
// through it — a correlation whose field name lies is exactly the
// class of thing that reads as working until someone tries to route
// on it.
//
// `OAuthCorrelationKey` is the neutral identifier the substrate now
// correlates on. It is a two-part value — a `Kind` naming the
// SUBJECT FAMILY and an `Id` naming the subject within it — rather
// than a bare string, so the callback handler can *dispatch* on the
// family (persist a data-source credential blob, or bind a
// `ProviderEntry`) instead of guessing from the shape of an opaque
// id.
//
// **`DataSourceId` maps onto it, losslessly and by construction.**
// `OAuthCorrelationKey.dataSource id` is the neutral form of every
// pre-43.B correlation, and the shipped `OAuthFlowContext.DataSourceId`
// / `OAuthFlowState.DataSourceId` fields are still populated with the
// same value they always carried. Every shipped `IOAuthCredentialFlow`
// implementation reads `ctx.DataSourceId` and is therefore unchanged —
// which is the whole point of doing this additively.
//
// **Six-rule portability audit (GP 12) on the changed surface.**
//   1. Identity by value      — `OAuthCorrelationKey` is a record of
//                                two strings. No live handle, no
//                                framework-typed id.
//   2. Async at boundary      — this file adds no method; the
//                                `IOAuthCredentialFlow` methods it
//                                rides on still return
//                                `Async<Result<_, OAuthError>>`.
//   3. Retry / supervision as data — unchanged; `OAuthError` remains
//                                the discriminated failure surface and
//                                no callback is introduced.
//   4. Stateless between calls — the key is passed per call inside
//                                `OAuthFlowContext`; nothing is
//                                cached between invocations.
//   5. No cross-shard ordering — correlation keys are independent;
//                                the substrate promises no ordering
//                                across two keys.
//   6. Precision at lower bound — no timing primitive on this
//                                surface. (The refresh JOB that
//                                consumes it declares
//                                `JobPrecision.Minute` — see
//                                `ProviderOAuthRefreshJobHandler`.)

/// Neutral correlation identifier for one OAuth Authorization Code
/// round-trip. `Kind` names the subject family the substrate should
/// dispatch on; `Id` is the subject's own identifier within that
/// family (a `DataSourceId`, a `ProviderEntry.Label`, …).
///
/// Rendered form is `"{Kind}:{Id}"` — used as an `ISecretStore` key
/// component and in audit payloads. Neither part may contain a `:`
/// (`OAuthCorrelationKey.isWellFormed` checks this) so the rendered
/// form parses back unambiguously.
type OAuthCorrelationKey = {
    /// Subject family. `OAuthCorrelationKey.DataSourceKind` for the
    /// Phase 10e data-source connectors, `ProviderEntryKind` for a
    /// Phase 43.B provider-profile entry. Free-form so a downstream
    /// consumer can introduce its own family without an SDK change;
    /// the substrate dispatches only on the two it ships and treats
    /// anything else as opaque.
    Kind: string
    /// Subject identifier within the family. Stable for the lifetime
    /// of the connection — renaming it strands the persisted refresh
    /// token exactly as renaming a flow does.
    Id: string
}

module OAuthCorrelationKey =
    /// Family tag for the Phase 10e data-source connectors. The value
    /// is part of the rendered secret-key form, so changing it would
    /// strand every persisted refresh token — treat it as a wire
    /// constant.
    [<Literal>]
    let DataSourceKind = "data-source"

    /// Family tag for a Phase 43.B provider-profile entry (an
    /// `OAuthConnected` `ProviderEntry`). Same wire-constant posture
    /// as `DataSourceKind`.
    [<Literal>]
    let ProviderEntryKind = "provider-entry"

    /// The neutral key for a data-source connection. This is the
    /// mapping that keeps every pre-43.B connector working unchanged.
    let dataSource (dataSourceId: string) : OAuthCorrelationKey = {
        Kind = DataSourceKind
        Id = dataSourceId
    }

    /// The neutral key for a provider-profile entry, correlated by the
    /// entry's `Label` (unique within a `ProviderProfile.Entries`).
    let providerEntry (label: string) : OAuthCorrelationKey = { Kind = ProviderEntryKind; Id = label }

    /// Rendered form, `"{Kind}:{Id}"`. Stable — it is a component of
    /// persisted `ISecretStore` key names.
    let render (key: OAuthCorrelationKey) : string = key.Kind + ":" + key.Id

    /// Parse a rendered key. Splits on the FIRST `:` only, so an id
    /// that (against the well-formedness rule) contains a colon still
    /// round-trips its kind. `None` when there is no separator.
    let tryParse (rendered: string) : OAuthCorrelationKey option =
        if System.String.IsNullOrEmpty rendered then
            None
        else
            let idx = rendered.IndexOf ':'

            if idx <= 0 || idx = rendered.Length - 1 then
                None
            else
                Some {
                    Kind = rendered.Substring(0, idx)
                    Id = rendered.Substring(idx + 1)
                }

    /// Whether a key renders unambiguously — both parts non-empty and
    /// neither containing the `:` separator. The substrate refuses to
    /// mint a correlation that fails this rather than persisting a
    /// secret under a key it cannot parse back.
    let isWellFormed (key: OAuthCorrelationKey) : bool =
        not (System.String.IsNullOrWhiteSpace key.Kind)
        && not (System.String.IsNullOrWhiteSpace key.Id)
        && not (key.Kind.Contains ":")
        && not (key.Id.Contains ":")

    /// True when the key names a Phase 10e data-source connection.
    let isDataSource (key: OAuthCorrelationKey) : bool = key.Kind = DataSourceKind

    /// True when the key names a Phase 43.B provider-profile entry.
    let isProviderEntry (key: OAuthCorrelationKey) : bool = key.Kind = ProviderEntryKind