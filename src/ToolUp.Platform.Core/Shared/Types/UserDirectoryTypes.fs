// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── 0.5.7 — IUserDirectory substrate + IUserDirectoryApi contract ──────
//
// Generic directory-lookup surface so the team-management UI can offer
// a "type a name, pick a match" affordance instead of forcing the
// operator to memorise an email or oid. Two layers:
//
//   1. `IUserDirectory` — server-side substrate. A companion package
//      (typically `ToolUp.AuthProviders.EntraDirectory` for Microsoft
//      Entra tenants; future Okta / Google / etc. companions plug in
//      against the same shape) implements `Search` against the
//      identity-provider's directory API. Deployments without a
//      directory companion register no implementation and the
//      `IUserDirectoryApi` handler returns `Ok []` for every query —
//      the typeahead UI degrades to a plain email input.
//
//   2. `IUserDirectoryApi` — Fable.Remoting contract. The client
//      typeahead component calls `Search` with a debounced query
//      prefix; the server proxies through to the registered
//      `IUserDirectory`. Gated to authenticated subjects only — no
//      anonymous directory enumeration.
//
// **Portability** — six rules audit:
//
//   1. Identity by value. `UserSummary` carries strings; no live
//      handles to the directory provider's connection.
//   2. Async at every boundary. `IUserDirectory.Search` and
//      `IUserDirectoryApi.SearchUsers` both return `Async<_>`.
//   3. Retry / supervision as data. Failures returned as
//      `Result<_, string>`; the companion translates Graph 429 /
//      transient 5xx into either a retry-internal Ok with partial
//      results or an `Error "directory unavailable"` for the UI.
//   4. Stateless handlers between invocations. Each `Search` call
//      stands alone — no continuation or cursor between calls (paging
//      is internal to the companion if needed).
//   5. No cross-shard ordering promises. Result ordering follows the
//      provider's natural ranking; the UI sorts by displayName for
//      stable rendering.
//   6. Precision N/A — no time-based contract.

/// Lightweight projection of a directory entry returned by
/// `IUserDirectory.Search`. Shape deliberately small — the typeahead
/// UI renders one line per match (display name + email), and the
/// invite flow consumes only `Email` (routed through
/// `IssuePendingInviteByEmail`) or `UserId` (direct
/// `AddTeamMember` when present).
///
/// Fields:
/// - `UserId` is the directory provider's stable identifier — for
///   Microsoft Entra this is the user's `oid` (object id), which
///   matches the `oid` claim in the access token the SDK consumes via
///   `OidcAuthProvider.PreferOidWhenPresent`. Other providers map to
///   whatever stable identifier their JWT issues.
/// - `DisplayName` is the human-readable name (Graph `displayName`).
///   Always populated — directory providers refuse to create a user
///   without one.
/// - `Email` is the primary email / userPrincipalName. Always
///   populated for human users; missing only for service principals
///   (which the search shape excludes anyway).
type UserSummary = {
    UserId: string
    DisplayName: string
    Email: string
}

/// Server-side substrate interface. Implemented by companion packages
/// against an identity-provider directory API. Registered as `Some
/// impl` via `ServerConfig.withUserDirectory` (or `None` for
/// deployments without a directory — the handler short-circuits to
/// `Ok []`).
///
/// `Search` is invoked with a non-empty prefix string and a maximum
/// result count. The companion is expected to:
///   * Trim and lower-case the query.
///   * Reject prefixes shorter than a provider-specific minimum
///     (Microsoft Graph requires ≥ 1 character but rate-limits
///     aggressively below 3; the companion typically enforces ≥ 2).
///   * Search both `displayName` and email / UPN with a `startsWith`
///     filter — substring matches are slower and noisier.
///   * Cap the returned list at `maxResults` (the client passes 10
///     by default).
///   * Return matches in directory-natural order (the UI sorts).
///
/// Concurrency: stateless, safe to call from multiple threads. The
/// companion is responsible for sharing one HTTP client / Graph
/// client across calls.
type IUserDirectory =
    abstract member Search: query: string -> maxResults: int -> Async<Result<UserSummary list, string>>

/// Fable.Remoting contract surfaced at `/api/IUserDirectoryApi/*`.
/// Called by the SDK's `Forms.Input.userTypeahead` component with a
/// debounced prefix.
///
/// Gated by `SubjectKind.AuthenticatedUser` at the handler — anonymous
/// callers receive `Error "authentication required"`. Platform / team
/// admin scoping is not enforced at this layer; the typeahead is
/// available to any signed-in subject so existing flows (a user
/// inviting a peer to a team they own) don't need a separate admin
/// elevation. Companion impls MAY enforce their own tenant filtering
/// (Microsoft Graph naturally scopes to the calling app's tenant).
type IUserDirectoryApi = {
    /// Returns up to `maxResults` directory entries whose displayName
    /// or email starts with `query` (case-insensitive). Empty list
    /// when the substrate is unregistered, when the query is shorter
    /// than the substrate's minimum, or when the provider returns no
    /// matches. `Error` only for genuine substrate failures (the
    /// companion lost its credential, the provider returned a 5xx
    /// after retries, etc.) — the UI renders the error string under
    /// the typeahead input.
    SearchUsers: string * int -> Async<Result<UserSummary list, string>>
}