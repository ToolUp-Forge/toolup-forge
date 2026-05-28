module ToolUp.AI.AICancellationDispatchInstanceValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Multi-instance + in-process cancel / client-tool dispatch ──────
//
// `AICancellationRegistry` and `ClientToolDispatchRegistry` are
// per-process `ConcurrentDictionary` singletons. Behind a load
// balancer with >1 replica:
//   * `POST /api/ai/cancel/{taskId}` that lands on the instance NOT
//     running the agent loop 404s — the user's Cancel button does
//     nothing and the agent keeps burning provider spend.
//   * A client tool-result `POST` that lands on the wrong instance
//     404s — the suspended agent loop hangs until its timeout, then
//     reports a bogus "client did not respond".
// There is no distributed implementation yet (tracked roadmap work);
// this validator makes the degradation explicit at startup instead of
// surfacing as mysterious dead Cancel buttons and hung loops in prod.
//
// Only registered when `composeWithAI` / `composeWithRAG` runs, so AI
// is active by construction — the warning keys purely on replica
// count. Warning, not Error: a deployment may front replicas with a
// sticky-session LB (which keeps cancel/result on the owning
// instance) and legitimately accept the residual risk.

/// Config validator: warns when an AI deployment runs >1 replica
/// while the cancel + client-tool-dispatch registries are still the
/// in-process singletons (no cross-instance routing).
type AICancellationDispatchInstanceValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "ai-cancel-dispatch-instance"
        member _.Timeout = timeout

        member _.Validate() = async {
            let multiInstance = config.ReplicaCount > 1
            let attested = config.AcceptStickyRoutedAiInMultiInstance

            if multiInstance && not attested then
                return
                    Error(
                        sprintf
                            "AI is composed with ReplicaCount = %d, but AICancellationRegistry and ClientToolDispatchRegistry are per-process singletons with no cross-instance routing. A cancel POST or a client-tool-result POST that lands on a replica other than the one running the agent loop 404s — the Cancel button silently does nothing (agent keeps spending) and client-tool round-trips hang until the agent-loop timeout. Run a single AI replica, or set ServerConfig.AcceptStickyRoutedAiInMultiInstance = true (TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE=1) to attest that a sticky-session load balancer pins cancel/result to the owning replica. A distributed registry is a roadmap item. Verify in the HealthMonitorUI admin tab or /dev/inspect Validators panel."
                            config.ReplicaCount
                    )
            elif multiInstance && attested then
                return
                    Warning(
                        sprintf
                            "AI ReplicaCount = %d with in-process cancel/dispatch registries; sticky-routing attested (AcceptStickyRoutedAiInMultiInstance = true). Residual risk if the LB drops affinity mid-loop: a cancel/result reaching a non-owning replica 404s. A distributed registry is a roadmap item."
                            config.ReplicaCount
                    )
            else
                return Ok
        }