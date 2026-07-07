module ToolUp.Platform.OAuthStateStoreInstanceValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── multi-instance + InMemoryOAuthStateStore ──────────────────────
//
// `InMemoryOAuthStateStore` (the SDK default `IOAuthStateStore`) keeps
// OAuth CSRF `state` + PKCE `code_verifier` in a process-local
// `ConcurrentDictionary`. In a single-instance deployment that is
// correct. Behind a load balancer fronting N replicas the /authorize
// step (which issues + persists the `state`) and the provider's
// /callback (which consumes it) can land on different replicas — the
// callback replica has never seen the token, `TryConsume` returns
// `None`, and the connector authorisation fails with a state-mismatch.
// The failure is intermittent (depends on which replica the redirect
// hits) and presents as "OAuth keeps failing" with no obvious cause.
//
// The store is documented "single-instance only" but nothing
// structural enforces it. This mirrors `JobSchedulerInstanceValidator`
// exactly: operator-declared multi-instance via
// `ServerConfig.ReplicaCount`. Refusal (not warning) — a broken
// connector-authorisation path in production is not a soft degradation,
// and the escape hatch (a distributed store or sticky sessions) is a
// deliberate operator decision.

/// Config validator that refuses the in-memory `IOAuthStateStore`
/// paired with `ReplicaCount > 1`. Single-instance deployments are
/// unaffected; multi-instance deployments either configure a
/// distributed `IOAuthStateStore` companion (the Phase 9c half-2
/// Redis-backed port) / a sticky-session load balancer, or set the
/// explicit-opt-in escape hatch.
type OAuthStateStoreInstanceValidator(config: ServerConfig, stateStore: IOAuthStateStore, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // In-memory OAuth state under multi-instance is a
    // cross-instance-auth-state hole — security-class, so it runs even under SkipPreflight.
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "oauth-state-store-instance"
        member _.Timeout = timeout

        member _.Validate() = async {
            let isInMemory = stateStore.GetType() = typeof<InMemoryOAuthStateStore>

            let multiInstance = config.ReplicaCount > 1
            let escapeHatch = config.AcceptInMemoryOAuthStateInMultiInstance

            if isInMemory && multiInstance && not escapeHatch then
                return
                    Error(
                        sprintf
                            "ServerConfig uses the in-memory IOAuthStateStore with ReplicaCount = %d. OAuth CSRF/PKCE state is held in a process-local dictionary, so when the provider redirects back the /callback can land on a different replica than the one that issued the `state` — TryConsume returns None and connector authorisation fails with a state-mismatch, intermittently, depending on which replica the redirect hits. Configure a distributed IOAuthStateStore companion (the Phase 9c half-2 Redis-backed port), front the deployment with a sticky-session load balancer, or set ServerConfig.AcceptInMemoryOAuthStateInMultiInstance = true (TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE=1) if OAuth-flow traffic is already pinned to one replica. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            config.ReplicaCount
                    )
            else
                return Ok
        }