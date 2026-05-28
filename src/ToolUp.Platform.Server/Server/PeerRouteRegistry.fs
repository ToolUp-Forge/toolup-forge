module ToolUp.Platform.PeerRouteRegistry

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Phase 37 — peer-route registry ──────────────────────────────────
//
// Mirrors the Phase 21b `AnonymousRoutePrefixes` shape. The registry is
// the single source of truth for "which request paths are owned by the
// peer-bearer-auth substrate". Both `PeerBearerAuthMiddleware` (which
// validates bearer tokens against `ISecretStore` for these paths) and
// `AuthEnforcementMiddleware` (which exempts these paths from the
// normal user-auth check — the bearer IS the authentication) consult
// the same list, so a single `ServerApp.withPeerRoutePrefix` call wires
// both sides.
//
// **Match shape.** `StartsWith` case-insensitive — matches the
// anonymous-route registry behaviour. Operators write the prefix the
// handler is mounted at (`/api/peer/echo` or `/api/peer/federated/`).
// Browsers that lowercase URL paths and operator typos that pin a
// trailing slash both work.
//
// **Default.** Empty list. With no prefixes configured the middleware
// is not registered at all (`SDK.Server.fs` short-circuits the
// `UseMiddleware<PeerBearerAuthMiddleware>` call), so deployments that
// never accept peer calls pay zero runtime cost — the strip-imports
// test the Phase 37 acceptance criteria pin.

[<Literal>]
let PeerNameItemsKey = "PeerName"

/// Returns true when `path` matches any prefix in `peerPrefixes`.
/// Case-insensitive `StartsWith` — mirrors
/// `AuthEnforcementMiddleware.isAnonymousRoute` exactly so both
/// registries share semantics.
let isPeerRoute (peerPrefixes: string list) (path: PathString) : bool =
    let value = path.Value

    peerPrefixes
    |> List.exists (fun prefix -> value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))