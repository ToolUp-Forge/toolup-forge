// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── ToolUp.Remoting API surface over IProviderProfile (Phase 44) ────
//
// Phase 42.B landed the canonical `IProviderProfile` store on the
// Platform.Core floor and Phase 43.A cut the AI assistant over to it,
// but both halves are SERVER-side: the only client surface that could
// manage a BYOK catalogue was the AI assistant's bespoke
// `AISettingsApi`, which speaks the AI assistant's vocabulary
// (`ListAvailable` / `SetActive` / "instances") and lives in
// `ToolUp.AI`. A non-AI BYOK consumer — a rental gateway, any future
// app — had no transport at all, so it would have had to hand-roll one
// alongside its own form.
//
// This record is that transport, and it is deliberately the
// GENERALISATION rather than a second copy: it speaks the store's own
// vocabulary (entries, tags, routing rules, fallback chain, health)
// and names no domain and no AI concept (GP 1). The server handler
// delegates every method to the registered `IProviderProfile`; the
// reusable `ProviderProfileUI` component composes over this record and
// nothing else.
//
// **Secret material never crosses this boundary outward.** An entry's
// `SecretKeyName` is an internal placement detail of the server's
// `ISecretStore` and is not on `ProviderEntryView`; what the client
// learns is the boolean `HasCredential`, exactly as the AI path's
// `AIProviderInstanceView.HasApiKey` does. A pasted key crosses
// INWARD once, on `SaveEntry`, and is written straight to the secret
// store.
//
// Six-rule portability audit: see `ProviderProfileApi` at the foot of
// this file.

/// One configured provider as the client sees it. Projection of
/// `ProviderEntry` with `SecretKeyName` replaced by the boolean the UI
/// actually renders and `OAuthBinding` reduced to its advisory
/// `ConnectedAt` — the flow name and correlation key are server-side
/// placement details with no client use.
type ProviderEntryView = {
    /// User-chosen label, unique within the profile. The identity every
    /// other method on this API keys by (rule 1 — identity by value).
    Label: string
    /// Provider descriptor id. Uninterpreted here; a stale id surfaces
    /// at resolve time in the consuming factory, exactly as it does on
    /// the store.
    ProviderId: string
    /// Optional model override; `None` → the descriptor's default.
    Model: string option
    /// User-assigned free-form tags ("fast", "cheap", "eu-resident").
    Tags: string list
    /// How the credential was supplied. Drives the form's read-only
    /// treatment of an OAuth-connected entry's key field.
    Origin: CredentialOrigin
    /// Whether the server holds credential material for this entry.
    /// True for a pasted key that is present, and for an OAuth-
    /// connected entry with a live binding. The key itself is never
    /// returned.
    HasCredential: bool
    /// When the OAuth consent round-trip completed, for an
    /// `OAuthConnected` entry whose binding is intact. `None` on a
    /// pasted-key entry — and also on an entry whose `Origin` claims
    /// `OAuthConnected` but whose binding is absent, which
    /// `ProviderEntry.oauthBinding` already treats as unusable rather
    /// than half-trusted.
    ConnectedAt: DateTime option
    /// Advisory live status, straight from the store.
    Health: ProviderHealth
    UpdatedAt: DateTime
}

/// The whole profile as the client sees it. `Routing` and `Fallback`
/// are the store's own value records — they carry no secret material
/// and no server-side placement detail, so projecting them would only
/// manufacture a second shape to keep in step.
type ProviderProfileView = {
    Entries: ProviderEntryView list
    Routing: RoutingRule list
    Fallback: FallbackChain
}

module ProviderProfileView =
    /// The view a scope with no saved profile resolves to. Distinct
    /// from an error: "nothing configured" is the ordinary starting
    /// state of every BYOK surface, and rendering it as a failure is
    /// how a first-run form ends up showing a red banner.
    let empty: ProviderProfileView = {
        Entries = []
        Routing = []
        Fallback = FallbackChain.empty
    }

/// Add-or-edit payload for one entry.
///
/// `ApiKey = None` is a METADATA edit (model, tags) that leaves any
/// stored credential untouched — the same distinction
/// `AISettingsApi.SaveInstance` draws, and for the same reason: a form
/// that re-submits the whole entry must not silently clear a key the
/// user did not retype.
type ProviderEntryInput = {
    [<PiiSafe>]
    [<NotEmpty>]
    Label: string
    [<PiiSafe>]
    [<NotEmpty>]
    ProviderId: string
    [<PiiSafe>]
    Model: string option
    [<PiiSafe>]
    Tags: string list
    /// The pasted key, when the caller is adding or rotating one.
    /// `None` = leave the stored credential as it is.
    ApiKey: string option
}

/// What a consumer's verify delegate observed. The client reports the
/// OUTCOME, not a `ProviderHealth`: the server maps it onto the health
/// record, so a client cannot mint a `LastVerifiedAt` or a
/// `RateLimitHeadroom` it never measured. Health is advisory by the
/// store's own contract, but "advisory" is a reason to keep the shape
/// honest, not a reason to stop.
[<RequireQualifiedAccess>]
type ProviderVerificationOutcome =
    /// The delegate reached the provider and it accepted the
    /// credential. `Models` is whatever model list the provider
    /// exposed — possibly empty, for a provider with no portable
    /// list-models call.
    | Verified of models: string list
    /// The delegate reached a verdict and it was negative, or the call
    /// itself could not run. One arm, because the UI renders both the
    /// same way and a consumer's delegate is the only thing that can
    /// tell them apart.
    | Failed of reason: string

/// Client-facing ToolUp.Remoting API for managing the caller's own
/// `ProviderProfile`. Every method is scoped to the caller's
/// `AccessContext` — no method accepts a scope from the wire (GP 4),
/// which is why none of them carries a scope parameter.
type IProviderProfileApi = {
    /// The caller's profile. A scope with no saved profile answers
    /// `Ok ProviderProfileView.empty`, never an error.
    [<RequiresClaim "scope">]
    GetProfile: unit -> Async<Result<ProviderProfileView, string>>

    /// Add or replace one entry by `Label`. A pasted `ApiKey` is
    /// written to the server's secret store; `None` leaves the stored
    /// credential untouched. Team mode requires Owner/Admin.
    [<RequiresClaim "scope">]
    [<Audit "PolicyChanged">]
    SaveEntry: ProviderEntryInput -> Async<Result<unit, string>>

    /// Remove the entry with this label, its stored credential, and
    /// every routing rule and fallback position that named it.
    /// Idempotent — removing an absent label succeeds.
    [<RequiresClaim "scope">]
    [<Audit "PolicyChanged">]
    RemoveEntry: string -> Async<Result<unit, string>>

    /// Upsert one routing rule, replacing any existing rule for the
    /// same `(Surface, Context)` key. Rejects an `EntryLabel` no entry
    /// carries: a stale label resolves to `None` at read time, which
    /// looks exactly like "no provider configured for this surface"
    /// and is the failure worth refusing at the point of entry.
    [<RequiresClaim "scope">]
    [<Audit "PolicyChanged">]
    SetRoute: RoutingRule -> Async<Result<unit, string>>

    /// Remove the rule for `(surface, context)`. Idempotent.
    [<RequiresClaim "scope">]
    [<Audit "PolicyChanged">]
    ClearRoute: string * string option -> Async<Result<unit, string>>

    /// Replace the ordered fallback chain. Every label must name an
    /// entry, for the same reason `SetRoute` validates: a fallback
    /// position that names nothing is silently skipped at failover,
    /// which is indistinguishable from having no fallback at all.
    [<RequiresClaim "scope">]
    [<Audit "PolicyChanged">]
    SetFallback: string list -> Async<Result<unit, string>>

    /// Advisory health per entry label, in the profile's entry order.
    /// Separate from `GetProfile` so a dashboard can poll the badges
    /// without re-reading (and re-rendering) the whole editor.
    [<RequiresClaim "scope">]
    GetHealth: unit -> Async<Result<(string * ProviderHealth) list, string>>

    /// Record what a consumer-supplied verify delegate observed for one
    /// entry. The server maps the outcome onto `ProviderHealth` and
    /// writes it through `IProviderProfile.SetEntryHealth`. No-op `Ok`
    /// when no entry carries the label, matching the store's own
    /// `SetEntryHealth` contract.
    [<RequiresClaim "scope">]
    RecordVerification: string * ProviderVerificationOutcome -> Async<Result<unit, string>>
}

module ProviderProfileApi =
    /// ToolUp.Remoting endpoint prefix. The default `/api/{type}/{method}`
    /// shape, matching `PlatformApi` / `IFeatureFlagApi` /
    /// `IModuleVisibilityApi`, so the client proxy needs no override.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"

// ─── Six-rule portability audit (GP 12) ───────────────────────────
//
// 1. Identity by value        — every method keys by `string` label
//                               and `string` surface / context, and
//                               returns value records
//                               (`ProviderEntryView`, `RoutingRule`,
//                               `ProviderHealth`). No live handle,
//                               no store reference, no `HttpContext`
//                               on the surface. The caller's scope
//                               is ambient (GP 4 / GP 7), never a
//                               parameter, so it cannot be a handle
//                               either.
// 2. Async at every boundary  — every method returns `Async<_>`.
//                               There is no sync accessor and no
//                               fire-and-forget arm; `RecordVerification`
//                               is the one that would have tempted a
//                               `Tell`, and it returns
//                               `Async<Result<unit, string>>` so a
//                               failed health write is observable.
// 3. Retry / supervision as data — none on this surface. It is a
//                               transport over a persistence store;
//                               every failure arm is a
//                               `Result<_, string>` the caller may
//                               re-issue. No `OnFailure` callback,
//                               no retry record, no dead-letter
//                               concept — adding one here would
//                               claim a dispatcher's semantics the
//                               underlying `IProviderProfile` does
//                               not have.
// 4. Stateless between calls  — no in-memory continuity. Each call
//                               reads and writes fresh state through
//                               `IProviderProfile`; there is no
//                               session, cursor, transaction handle
//                               or draft held across calls, so
//                               successive calls may be served by
//                               different nodes. The read-modify-write
//                               methods (`SaveEntry`, `SetRoute`,
//                               `SetFallback`, `RemoveEntry`) are
//                               last-writer-wins per scope, which is
//                               the store's own `Set` contract
//                               rather than a promise added here.
// 5. No cross-shard ordering  — each caller's scope is an
//                               independent shard (the store's rule
//                               5). Nothing on this surface reads or
//                               orders across scopes; there is no
//                               list-all, no admin cross-scope read.
// 6. Precision at lower bound — no timing primitive. The only
//                               temporal values are the advisory
//                               `UpdatedAt` / `ConnectedAt` /
//                               `ProviderHealth.LastVerifiedAt`
//                               stamps, which are observations
//                               rather than schedules; nothing here
//                               promises a deadline, an interval or
//                               a sub-second guarantee. N/A by
//                               construction, exactly as on
//                               `IProviderProfile`.
//
// Additionally: no framework serialisation attribute on any type in
// this file, no `open Akka.*` / `open Orleans.*`, and no companion
// type on the surface — every type is either a BCL primitive or a
// Platform.Core value record.