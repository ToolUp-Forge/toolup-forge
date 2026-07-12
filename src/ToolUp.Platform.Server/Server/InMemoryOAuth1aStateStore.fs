// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent

// ─── In-memory OAuth 1.0a request-token state store (Phase 10g) ─────────
//
// The single-instance default `IOAuth1aStateStore`. Request-token state is
// short-lived (a user authorises within minutes), so an in-process
// dictionary is sufficient; a multi-instance deployment composes a
// distributed companion (Phase 9c half-2) against the same contract.
// Consume is single-use (`TryRemove`); an expired entry is removed and
// reported absent.

type InMemoryOAuth1aStateStore() =
    let entries = ConcurrentDictionary<string, OAuth1aRequestState>()

    interface IOAuth1aStateStore with
        member _.Save(requestToken: string, state: OAuth1aRequestState) = async {
            entries[requestToken] <- state
            return ()
        }

        member _.TakeValid(requestToken: string, ttl: TimeSpan) = async {
            match entries.TryRemove requestToken with
            | true, state when DateTime.UtcNow - state.CreatedAt <= ttl -> return Some state
            // Present-but-expired: TryRemove already dropped it, so the
            // stale entry is cleaned as a side effect. Report absent.
            | _ -> return None
        }

module InMemoryOAuth1aStateStore =
    /// Construct the in-memory default `IOAuth1aStateStore`.
    let create () : IOAuth1aStateStore =
        InMemoryOAuth1aStateStore() :> IOAuth1aStateStore