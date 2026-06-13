namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting

// ─── In-memory OAuth state store (Phase 10b default impl) ────────
//
// Default `IOAuthStateStore` implementation. Backed by a
// `ConcurrentDictionary<string, OAuthFlowState>` keyed by `Token`.
// Sufficient for single-instance deployments and the dev path; multi-
// instance deployments need the Phase 9c half 2 distributed companion.
//
// **Atomic consume.** `TryConsume` uses `TryRemove` so the read-and-
// delete is one operation; concurrent callbacks against the same
// token would race, but state tokens are CSRF-random and 16 bytes
// of entropy — collision is astronomically unlikely.

/// Helpers for generating SDK-side CSRF state and PKCE values.
/// Server-side counterpart to `OidcPkce.fs` (which targets the
/// browser via WebCrypto). Uses BCL `RandomNumberGenerator` —
/// cryptographically secure on every supported platform.
module OAuthCrypto =
    /// 16-byte (~22-character) cryptographically random CSRF state
    /// (RFC 6749 §10.12), base64url-encoded via the shared codec.
    let generateState () : string =
        let bytes = Array.zeroCreate<byte> 16
        RandomNumberGenerator.Fill bytes
        Base64Url.encode bytes

    /// 32-byte (~43-character) cryptographically random PKCE
    /// code verifier (RFC 7636).
    let generateCodeVerifier () : string =
        let bytes = Array.zeroCreate<byte> 32
        RandomNumberGenerator.Fill bytes
        Base64Url.encode bytes

    /// SHA-256 the verifier, return the digest base64url-encoded.
    /// This is the `code_challenge` value sent to the provider in
    /// the /authorize step; the provider compares it against the
    /// SHA-256 of the verifier passed in /token.
    let codeChallengeFromVerifier (verifier: string) : string =
        use sha = SHA256.Create()
        let bytes = System.Text.Encoding.UTF8.GetBytes verifier
        let hash = sha.ComputeHash bytes
        Base64Url.encode hash

/// In-memory `IOAuthStateStore`. Single-instance only — flagged for
/// the Phase 9c half 2 distributed companion (Redis-backed, mirrors
/// the `src/NotificationChannels/Redis/` pattern).
type InMemoryOAuthStateStore() =
    let entries = ConcurrentDictionary<string, OAuthFlowState>()

    /// 10-minute TTL for in-flight state entries. Long enough for slow
    /// human consent (multi-account picker, MFA), short enough to keep
    /// the store from filling with stale entries. Kept private to the
    /// impl; distributed companions configure their own equivalent.
    let ttl = TimeSpan.FromMinutes 10.0

    interface IOAuthStateStore with
        member _.Save(state: OAuthFlowState) : Async<Result<unit, string>> = async {
            // TryAdd returns false if the key already exists. State
            // tokens are 16 bytes of entropy; a collision indicates
            // a broken generator and should fail loudly.
            if entries.TryAdd(state.Token, state) then
                return Ok()
            else
                return Error $"OAuth state token already exists (collision): {state.Token}"
        }

        member _.TryConsume(token: string) : Async<OAuthFlowState option> = async {
            // Atomic read-and-remove. Honours the docstring contract:
            // expired entries return `None` (the entry is still
            // removed — `TryRemove` runs unconditionally — so the
            // caller can't replay a fresh /callback against the same
            // expired token).
            match entries.TryRemove token with
            | true, state when DateTime.UtcNow - state.CreatedAt < ttl -> return Some state
            | _ -> return None
        }

        member _.Cleanup(ttl: TimeSpan) : Async<int> = async {
            let cutoff = DateTime.UtcNow - ttl
            let expired = ResizeArray<string>()

            for kvp in entries do
                if kvp.Value.CreatedAt < cutoff then
                    expired.Add kvp.Key

            let mutable removed = 0

            for key in expired do
                if (entries.TryRemove key |> fst) then
                    removed <- removed + 1

            return removed
        }

/// Phase 10b — `BackgroundService` that periodically sweeps the
/// in-memory state store for expired entries. Mirrors the
/// `JobScheduler` cadence (one sweep per minute) — frequent enough to
/// keep the dictionary bounded under churn, infrequent enough to
/// avoid lock contention against `Save` / `TryConsume`.
///
/// Lazy eviction in `TryConsume` already returns `None` for expired
/// entries — this sweep prevents indefinite memory growth from state
/// tokens that were issued but never consumed (user closed the
/// consent tab, network failed mid-redirect, etc.).
type OAuthStateCleanupService(stateStore: IOAuthStateStore) =
    inherit BackgroundService()

    /// Default TTL matches `InMemoryOAuthStateStore`'s internal value.
    /// Distributed companions configure their own equivalent.
    let ttl = TimeSpan.FromMinutes 10.0

    /// Sweep interval. One minute aligns with `JobScheduler`'s tick
    /// cadence and keeps wakeups predictable for diagnostics.
    let interval = TimeSpan.FromMinutes 1.0

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                try
                    let! _ = stateStore.Cleanup ttl
                    ()
                with _ ->
                    // Cleanup failures are non-fatal — the next tick
                    // tries again. Distributed implementations may
                    // throw on transient backend hiccups; swallow
                    // here so the service stays alive.
                    ()

                try
                    do! Task.Delay(interval, stoppingToken)
                with :? OperationCanceledException ->
                    ()
        }
        :> Task