// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── 0.5.7 — IUserDirectory substrate + IUserDirectoryApi contract ──────
//
// Generic directory-lookup + invitation-notification surface so the
// team-management UI can offer a "type a name, pick a match" affordance
// AND send the invitee a branded "you've been added" email — both
// without forcing operators to memorise an email or stay on a chat tab
// to message the invitee out of band. Two layers:
//
//   1. `IUserDirectory` — server-side substrate. A companion package
//      (typically `ToolUp.AuthProviders.EntraDirectory` for Microsoft
//      Entra tenants; future Okta / Google / etc. companions plug in
//      against the same shape) implements `SearchUsers` against the
//      identity-provider's directory API and (when configured) sends
//      transactional invitation emails via `NotifyInvitation`.
//      Deployments without a directory companion register no
//      implementation: the `IUserDirectoryApi` handler returns `Ok []`
//      for every query and the team-invite path silently skips the
//      notification — the typeahead UI degrades to a plain email input
//      and the invitee is told out of band.
//
//   2. `IUserDirectoryApi` — Fable.Remoting contract. The client
//      typeahead component calls `SearchUsers` with a debounced query
//      prefix; the server proxies through to the registered
//      `IUserDirectory`. Gated to authenticated subjects only — no
//      anonymous directory enumeration. The `NotifyInvitation` side of
//      the substrate has no client-facing surface: it's invoked
//      server-side from the team-invite handler, opportunistically
//      and best-effort.
//
// **Portability** — six rules audit:
//
//   1. Identity by value. `UserSummary` carries strings; no live
//      handles to the directory provider's connection.
//   2. Async at every boundary. Every method returns `Async<_>`.
//   3. Retry / supervision as data. Failures returned as
//      `Result<_, string>`; the companion translates Graph 429 /
//      transient 5xx into either a retry-internal Ok with partial
//      results or an `Error "directory unavailable"` for the UI.
//   4. Stateless handlers between invocations. Each call stands alone
//      — no continuation or cursor between calls (paging is internal
//      to the companion if needed).
//   5. No cross-shard ordering promises. Result ordering follows the
//      provider's natural ranking; the UI sorts by displayName for
//      stable rendering.
//   6. Precision N/A — no time-based contract.

/// Lightweight projection of a directory entry returned by
/// `IUserDirectory.SearchUsers`. Shape deliberately small — the
/// typeahead UI renders one line per match (display name + email), and
/// the invite flow consumes only `Email` (routed through
/// `IssuePendingInviteByEmail`) or `UserId` (direct `AddTeamMember`).
///
/// Fields:
/// - `UserId` is the directory provider's stable identifier — for
///   Microsoft Entra this is the user's `oid` (object id), which
///   matches the `oid` claim in the access token the SDK consumes via
///   `AuthConfig.PreferOidWhenPresent`. Other providers map to
///   whatever stable identifier their JWT issues. Always populated;
///   used as the React key.
/// - `DisplayName` is the human-readable name (Graph `displayName` for
///   Entra). `None` only for the rare directory entry without one
///   (service principals, partial-provisioned guest accounts) —
///   consumers render the email or the user id as a fallback.
/// - `Email` is the primary email / userPrincipalName. `None` only
///   for service principals or accounts created without a mail
///   address; consumers render the display name or user id as a
///   fallback. The typeahead's "pick a match" path uses the email
///   when present, falling back to the user id so the resulting
///   invite still has a stable identifier.
type UserSummary = {
    UserId: string
    DisplayName: string option
    Email: string option
}

/// Payload handed to `IUserDirectory.NotifyInvitation` from the
/// team-invite handler. The companion renders an email body from
/// these fields and dispatches it via the provider's transactional
/// mail surface (Microsoft Graph `sendMail`, SES, SendGrid, etc.).
///
/// Fields:
/// - `Email` — recipient (the invitee). Always populated; the invite
///   handler refuses to run with a blank email.
/// - `TeamName` — display name of the team the invitee is joining.
///   The handler resolves it from `ITeamStore.GetTeam` at notification
///   time; companions render it verbatim into subject + body.
/// - `InviterName` — display name of the operator who issued the
///   invite. `None` when the operator's identity couldn't be resolved
///   from the directory (a platform admin acting on their own behalf,
///   a test fixture, etc.) — companions render a generic "You've
///   been invited" salutation in that case.
/// - `AppName` — deployment brand the email is sent on behalf of
///   (e.g. "ToolUp Pro"). Companions render this in subject + button
///   text. Defaults to "ToolUp" when the handler has no consumer-
///   supplied override.
/// - `RedirectUrl` — absolute URL the invitee clicks to land on the
///   app (`https://app.example.com/`). The team-invite handler
///   computes it from the request's Origin / scheme+host headers so
///   the email points back at the same surface the operator is using.
/// - `Role` — role the invitee will receive on first sign-in.
///   Companions render this in the email so the invitee knows the
///   capability level they're being granted.
type InvitationNotification = {
    Email: string
    TeamName: string
    InviterName: string option
    AppName: string
    RedirectUrl: string
    Role: TeamRole
}

/// Server-side substrate interface. Implemented by companion packages
/// against an identity-provider directory API. Registered as `Some
/// impl` via `ServerApp.withUserDirectory` (or `None` for
/// deployments without a directory — the handler short-circuits to
/// `Ok []` and the team-invite path silently skips notification).
///
/// `SearchUsers` is invoked with a non-empty prefix string and a
/// maximum result count. The companion is expected to:
///   * Trim the query.
///   * Reject prefixes shorter than a provider-specific minimum
///     (Microsoft Graph requires ≥ 1 character but rate-limits
///     aggressively below 3; the companion typically enforces ≥ 2).
///   * Search both displayName and email / UPN with a `startsWith` /
///     `$search` filter — substring matches are slower and noisier.
///   * Cap the returned list at `take` (the client passes 10 by
///     default; the server handler clamps to [1, 25]).
///   * Return matches in directory-natural order (the UI sorts).
///
/// `NotifyInvitation` dispatches a branded "you've been invited to
/// <team>" email to the recipient. Best-effort by contract: the
/// companion returns `Error` only for unrecoverable transport-layer
/// failures (mailbox doesn't exist, 4xx from the provider). Transient
/// failures the companion swallows and returns `Ok ()` — the team-
/// invite handler is already audit-logged, and an undeliverable email
/// shouldn't roll back the invite. Companions that don't support
/// outbound mail (a future read-only directory adapter) return
/// `Error "notification not supported"` and the handler skips the
/// path with no further consequence.
///
/// Concurrency: stateless, safe to call from multiple threads. The
/// companion is responsible for sharing one HTTP client / Graph
/// client across calls.
/// `ResolveUsers` reverse-resolves a batch of stable user ids back to
/// `UserSummary` entries (display name + email). It is the inverse of
/// `SearchUsers`: where the typeahead turns a typed prefix into ids, the
/// admin tables turn stored ids (team Owners / Admins, Platform Admins)
/// into human-readable emails. The companion is expected to:
///   * De-duplicate and trim the id list, dropping blanks.
///   * Resolve in one batched provider call where the API supports it
///     (Microsoft Graph `directoryObjects/getByIds` takes up to 1000
///     ids per request) rather than N round-trips.
///   * Return only the entries it could resolve — ids the provider
///     doesn't recognise are silently omitted, NOT surfaced as errors.
///     Callers pair the result by `UserId` and fall back to rendering
///     the raw id for anything unresolved.
///   * Return `Ok []` for an empty input without touching the provider.
type IUserDirectory =
    abstract member SearchUsers: query: string * take: int -> Async<Result<UserSummary list, string>>
    abstract member ResolveUsers: ids: string list -> Async<Result<UserSummary list, string>>
    abstract member NotifyInvitation: notification: InvitationNotification -> Async<Result<unit, string>>

/// Fable.Remoting contract surfaced at `/api/IUserDirectoryApi/*`.
/// Called by the SDK's directory typeahead with a debounced prefix.
///
/// Gated by `SubjectKind.AuthenticatedUser` at the handler — anonymous
/// callers receive `Error "authentication required"`. Platform / team
/// admin scoping is not enforced at this layer; the typeahead is
/// available to any signed-in subject so existing flows (a user
/// inviting a peer to a team they own) don't need a separate admin
/// elevation. Companion impls MAY enforce their own tenant filtering
/// (Microsoft Graph naturally scopes to the calling app's tenant).
///
/// Only `SearchUsers` is exposed on the wire — `NotifyInvitation` is
/// strictly a server-internal capability used by the team-invite
/// handler, never reachable from the browser.
type IUserDirectoryApi = {
    /// Returns up to `take` directory entries whose displayName, mail,
    /// or userPrincipalName starts with `query` (case-insensitive).
    /// Empty list when the substrate is unregistered, when the query
    /// is shorter than the substrate's minimum, or when the provider
    /// returns no matches. `Error` only for genuine substrate failures
    /// (the companion lost its credential, the provider returned a
    /// 5xx after retries, etc.) — the UI renders the error string
    /// under the typeahead input.
    [<RequiresClaim "scope">]
    SearchUsers: string * int -> Async<Result<UserSummary list, string>>
    /// Reverse-resolve a batch of stable user ids to directory entries
    /// (display name + email). The inverse of `SearchUsers` — the admin
    /// tables call this with the ids they hold (team Owners / Admins,
    /// Platform Admins) to render emails instead of opaque `oid`s.
    ///
    /// Returns one `UserSummary` per id the substrate could resolve, in
    /// no guaranteed order; ids the directory doesn't recognise (or the
    /// whole list, when no companion is registered) are simply absent —
    /// the caller joins by `UserId` and falls back to the raw id for
    /// anything missing. Empty input ⇒ `Ok []` with no provider call.
    /// `Error` only for a genuine substrate failure (lost credential,
    /// provider 5xx after retries) so the caller can keep showing ids.
    [<RequiresClaim "scope">]
    ResolveUsers: string list -> Async<Result<UserSummary list, string>>
}