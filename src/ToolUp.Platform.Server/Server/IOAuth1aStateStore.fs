// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── OAuth 1.0a request-token state store (Phase 10g) ───────────────────
//
// Correlates leg 1 (request-token fetch) with leg 3 (access-token
// exchange) across the user-authorisation redirect. The substrate stashes
// the request-token *secret* (keyed by the request *token*) when it fetches
// the request token, and consumes it single-use on the callback. Mirrors
// the OAuth 2.0 `IOAuthStateStore` shape; the in-memory default suits a
// single instance, and a distributed companion (Phase 9c half-2) plugs in
// against the same contract.
//
// Six-rule portability audit (GP 12): identity by value (string request
// token, record state), async at every boundary, no callbacks, stateless
// contract (nothing survives beyond the persisted entry), single-use
// consume, TTL-bounded.

type IOAuth1aStateStore =
    /// Persist the request-token state, keyed by the request token.
    abstract Save: requestToken: string * state: OAuth1aRequestState -> Async<unit>

    /// Read and remove (single-use) the state for a request token. Returns
    /// `None` when absent, already consumed, or older than `ttl` — the
    /// substrate treats every one of those as "no valid pending
    /// authorisation" and surfaces `StateTokenMismatch`.
    abstract TakeValid: requestToken: string * ttl: TimeSpan -> Async<OAuth1aRequestState option>