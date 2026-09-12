module ToolUp.Platform.EventStoreRetentionValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 465 (absorbing Phase 9m.C Gap 4) — unbounded event trail ──
//
// `EventStore = PersistentBlobBacked EventRetentionPolicy.unlimited`
// writes one JSON blob per event under `_platform/events/{scopeId}/`
// and prunes on neither age nor count. Pruning is not automatic in any
// case — the app schedules it — so an unlimited policy means the trail
// grows for the life of the deployment. Nothing fails; the bill and the
// listing latency rise until someone notices, which is typically long
// after the cheap moment to have chosen a policy.
//
// Unlimited is a legitimate choice: where the event log IS the audit
// trail and the compliance regime demands full history, pruning would
// be the defect. So this is a Warning with an acknowledgement, not a
// refusal — the same posture, and the same shape, as
// `RateLimitModeValidator`'s no-limiter warning.
//
// **Gated on the deployment shape, not on the policy alone.** A local
// dev shell with a blob-backed store and no policy is not a problem
// worth a line in its startup log; an authenticated or replicated
// deployment is. `requiresAnyAuth || ReplicaCount > 1` is the same
// production/multi-instance reading the admin-coherence and scheduler
// validators take.
//
// **The acknowledgement is read from the resolution seam rather than
// from a `ServerConfig` field**, unlike the older hatches beside it.
// Those predate the seam and each carries a record field; adding one
// here would widen a public record in `ToolUp.Platform.Core` while its
// release version is frozen, and a contract change shipped under a
// frozen number is precisely the drift the version discipline exists to
// stop. Read this way the hatch behaves identically for the operator
// and gains the manifest and profile lanes for free. Folding it onto
// `ServerConfig` beside its siblings is a mechanical move at the next
// minor.

/// The recognised affirmative tokens, matching `envFlag` in
/// `ServerConfig.fromEnv` — an acknowledgement that only worked when
/// spelled one particular way would read as unset and re-emit the
/// warning the operator had just answered.
let private isAcknowledged () =
    match
        ConfigResolution.tryValue ConfigKeys.Names.acceptUnlimitedEventRetention
        |> Option.map _.ToLowerInvariant()
    with
    | Some("1" | "true" | "yes" | "on") -> true
    | _ -> false

/// Phase 465 — warns when a persistent event store runs with neither an
/// age nor a count limit in a production or multi-instance shape.
type EventStoreRetentionValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "event-store-retention"
        member _.Timeout = timeout

        member _.Validate() = async {
            let productionShaped =
                DeploymentConfig.requiresAnyAuth config || config.ReplicaCount > 1

            match config.EventStore with
            | PersistentBlobBacked policy when
                policy.MaxAge.IsNone
                && policy.MaxCountPerScope.IsNone
                && productionShaped
                && not (isAcknowledged ())
                ->
                return
                    Warning(
                        sprintf
                            "ServerConfig.EventStore = PersistentBlobBacked with MaxAge = None and MaxCountPerScope = None, in a %s deployment. The blob-backed store writes one object per event under _platform/events/{scopeId}/ and prunes on neither dimension, so the trail — and its storage cost and listing latency — grows for the life of the deployment. Pruning is never automatic; the app runs it. Set a policy the deployment can defend: the SDK's policy is deployment-wide, so take the longest retention you owe — 365 days where the _platform.audit scope is your compliance record, 90 days otherwise (EventRetentionPolicy.byAge (TimeSpan.FromDays 365.0) / EventRetentionPolicy.ninetyDays) — and add a per-scope ceiling of MaxCountPerScope = Some 100_000 so one noisy scope cannot dominate the store. If unlimited retention is the deliberate choice because the event log IS the audit trail your regime requires in full, set %s=1 to record that and silence this warning."
                            (if config.ReplicaCount > 1 then
                                 sprintf "multi-instance (ReplicaCount = %d)" config.ReplicaCount
                             else
                                 sprintf "production-shaped (Surfaces = %s)" (DeploymentConfig.surfacesLabel config))
                            ConfigKeys.Names.acceptUnlimitedEventRetention
                    )
            | _ -> return Ok
        }