// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 43.B — provider-profile OAuth connect ──────────────────
//
// `IProviderOAuthFlow` is the "thin specialisation of the generalised
// `IOAuthCredentialFlow`" the phase calls for. It adds nothing to the
// OAuth mechanics — the four Authorization Code methods, PKCE, the
// typed `OAuthError` surface and the `/api/oauth/{flowName}/*`
// handlers are all inherited unchanged — and adds only the two facts
// the substrate needs in order to turn a completed round-trip into a
// `ProviderEntry`:
//
//   * WHICH provider descriptor the resulting entry names
//     (`ProviderId`), so `IAIProviderFactory` — or any other BYOK
//     consumer — can build a provider from it; and
//   * WHAT to call the entry by default (`DefaultEntryLabel`), so a
//     one-click "Connect Anthropic" needs no label prompt.
//
// **Why a specialisation and not a second interface.** A separate
// `IProviderOAuthFlow` with its own authorize / exchange / refresh
// methods would have duplicated the PKCE handling, the CSRF state
// machinery and the error taxonomy, and would have needed its own
// route pair. Inheriting means one registration
// (`ServerApp.withOAuthCredentialFlow`), one set of endpoints, and one
// conformance bar — a provider flow is validated by
// `IOAuthCredentialFlowContract` for the OAuth half and
// `IProviderOAuthFlowContract` for the binding half.
//
// **Six-rule portability audit (GP 12).**
//   1. Identity by value      — `ProviderId` / `DefaultEntryLabel` are
//                                strings; the correlation is an
//                                `OAuthCorrelationKey` value. No live
//                                handle appears on the surface.
//   2. Async at boundary      — the inherited methods all return
//                                `Async<Result<_, OAuthError>>`. The
//                                three members added here are pure
//                                declarative properties (the same
//                                posture as `IOAuthCredentialFlow.Name`
//                                / `Descriptor` / `SupportsPkce`), not
//                                operations, so there is nothing to
//                                await.
//   3. Retry / supervision as data — no callback is introduced;
//                                refresh outcomes are the
//                                `ProviderOAuthRefreshOutcome` DU and
//                                the job substrate's existing
//                                `JobRetryPolicy` record.
//   4. Stateless between calls — implementations read client
//                                credentials from `ISecretStore` per
//                                call (inherited contract); nothing is
//                                held between invocations.
//   5. No cross-shard ordering — each `(scope, entry label)` is an
//                                independent subject; no ordering is
//                                promised across two of them.
//   6. Precision at lower bound — the auto-refresh job declares
//                                `JobPrecision.Minute`, the
//                                in-process scheduler's floor.

/// A `IOAuthCredentialFlow` that can additionally mint a
/// provider-profile entry. Register it exactly like any other flow —
/// `ServerApp.withOAuthCredentialFlow` — and the substrate's
/// `/api/oauth/{flowName}/authorize?providerEntry={label}` route
/// becomes available for it.
type IProviderOAuthFlow =
    inherit IOAuthCredentialFlow

    /// Provider descriptor id the minted `ProviderEntry` carries
    /// (`ProviderEntry.ProviderId`) — the same id a pasted-key entry
    /// for this vendor would carry, so both origins resolve through
    /// the identical consumer catalogue lookup.
    abstract ProviderId: string

    /// Label used when the caller does not supply one. Unique within a
    /// `ProviderProfile.Entries` by convention; the substrate refuses
    /// to bind when a DIFFERENT-origin entry already holds the label,
    /// rather than silently converting a pasted-key entry into an
    /// OAuth one.
    abstract DefaultEntryLabel: string

    /// Optional model to stamp on the minted entry
    /// (`ProviderEntry.Model`). `None` leaves it unset, which means
    /// "the consuming factory's descriptor default" — the same
    /// semantics a pasted-key entry has.
    abstract DefaultModel: string option

/// `ISecretStore` key derivation for OAuth-minted credentials,
/// covering BOTH correlation families from one function so the two
/// paths cannot drift.
module ProviderOAuthKeys =
    /// Key the long-lived refresh token is persisted under.
    ///
    /// **The data-source form is byte-identical to Phase 10e's**
    /// (`"{flowName}-refresh-{dataSourceId}"`). That is load-bearing,
    /// not tidiness: every already-connected data source has a token
    /// sitting at that key, and a "generalisation" that changed the
    /// derivation would have silently disconnected every one of them
    /// at deploy time. Other families get the kind folded in
    /// (`"{flowName}-refresh-{kind}-{id}"`) so two subjects with the
    /// same id in different families can never collide.
    let refreshTokenKey (flowName: string) (correlation: OAuthCorrelationKey) : string =
        if OAuthCorrelationKey.isDataSource correlation then
            $"{flowName}-refresh-{correlation.Id}"
        else
            $"{flowName}-refresh-{correlation.Kind}-{correlation.Id}"

    /// Derived key for the cached access token. Same `.access` suffix
    /// convention as `OAuthRefreshDescriptor.accessTokenKey`, so an
    /// operator reading `ISecretStore.ListKeys` sees one shape across
    /// both refresh substrates.
    let accessTokenKey (flowName: string) (correlation: OAuthCorrelationKey) : string =
        refreshTokenKey flowName correlation + ".access"

    /// Derived key for the cached access-token expiry, an ISO-8601
    /// UTC round-trip string (`"o"`).
    let accessExpiryKey (flowName: string) (correlation: OAuthCorrelationKey) : string =
        refreshTokenKey flowName correlation + ".expires-at"