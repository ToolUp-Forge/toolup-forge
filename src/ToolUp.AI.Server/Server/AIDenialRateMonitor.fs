// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.AIDenialRateMonitor

open System
open System.Collections.Concurrent
open ToolUp.Platform

// ─── Phase 47 — sustained action-denial rate alert ───────────────
//
// `/dev/ai-allowlist` and the health-monitor admin panel both answer
// "how bad is it" — but only once someone looks. A prompt-injection
// campaign against the G12 allowlist is a security incident (Phase
// 27a's stance: failures ARE incidents), and an incident nobody is
// looking at should page someone rather than wait for a refresh.
//
// So: a bounded in-memory rolling counter per scope. When a scope's
// denial count over the trailing window crosses the configured
// threshold, one `SystemMessage` notification is published and a
// cooldown starts. The cooldown is the load-bearing half — a campaign
// producing hundreds of denials a minute must produce ONE alert, not
// hundreds, or the alert channel becomes the denial-of-service.
//
// **Opt-in, zero footprint when unused (GP 13).** No policy declared
// (`AIServerApp.withDenialRateAlert` never called) → nothing is
// registered in DI, the agent loop's one `GetService` returns null, and
// the deployment is byte-for-byte as it was.
//
// **Never blocks or breaks a turn.** Same posture as the audit write it
// rides beside: a wedged notification channel loses the alert, it does
// not lose the refusal (which has already been enforced) nor fail the
// conversation.
//
// **Deliberately per-process.** The counter is in-memory, so an N-silo
// deployment alerts at N × the configured rate in the worst case (each
// silo counting only its own share). That is the honest trade for a
// hot-path counter with no store round-trip; it is documented on the
// policy record so an operator sizing a threshold knows to divide.
// A deployment wanting a cross-silo rate reads `DenialsPerMinute` off
// the rollup, which IS store-backed and therefore silo-complete.

/// When to raise a sustained-denial alert. Value-typed (GP 5).
type AIDenialRateAlertPolicy = {
    /// Denials within `Window`, in one scope, that constitute a
    /// campaign. Must be at least 1.
    Threshold: int
    /// The trailing window the threshold is counted over.
    Window: TimeSpan
    /// Minimum time between two alerts for the same scope. A campaign
    /// is one incident, not one incident per refused call.
    Cooldown: TimeSpan
    /// Where the `SystemMessage` is published.
    ///
    /// `None` (the default) publishes to the scope the denials occurred
    /// in — the team whose session is being driven. `Some scopeId`
    /// publishes every alert to one fixed operator channel instead
    /// (`Some "_platform"` is the usual choice: platform-wide, and not
    /// visible to the tenant whose session tripped it).
    ///
    /// Explicit rather than inferred because the two are different
    /// security postures and neither is right for every deployment: a
    /// single-tenant install wants the team to see it, a multi-tenant
    /// SaaS usually does not want to tell a tenant that its session is
    /// being probed.
    AlertScopeId: string option
}

module AIDenialRateAlertPolicy =
    /// A reasonable starting point: 20 denials in 5 minutes in one
    /// scope, at most one alert per 30 minutes, published to the scope
    /// the denials occurred in.
    ///
    /// Not a default that applies anywhere by itself — a deployment
    /// must call `AIServerApp.withDenialRateAlert` for any of this to
    /// exist (GP 13). This value is the shape to start tuning from.
    let defaults = {
        Threshold = 20
        Window = TimeSpan.FromMinutes 5.0
        Cooldown = TimeSpan.FromMinutes 30.0
        AlertScopeId = None
    }

/// Hard cap on distinct scopes tracked concurrently. Scopes are teams,
/// not attacker-controlled strings, so this is a memory guard against a
/// very large deployment rather than an abuse guard — but an unbounded
/// dictionary on a hot path is a leak whatever fills it.
[<Literal>]
let private MaxTrackedScopes = 2048

/// Hard cap on retained timestamps per scope. Once a scope is over
/// threshold, further precision buys nothing — the alert has fired.
[<Literal>]
let private MaxRetainedPerScope = 1024

/// Rolling per-scope denial counter with alert de-duplication.
///
/// Thread-safe: the agent loop reaches it from concurrent request
/// threads. Mutation is confined to this type (a documented GP 5
/// exception — hot-path counter), and every read the rest of the SDK
/// makes is through `Observe`'s return value.
type AIDenialRateMonitor(policy: AIDenialRateAlertPolicy, clock: unit -> DateTime) =

    /// scopeId → the retained denial timestamps in that scope.
    let counts = ConcurrentDictionary<string, ResizeArray<DateTime>>()

    /// scopeId → when that scope last alerted. Separate from `counts`
    /// so pruning the window never forgets that an alert already fired.
    let lastAlert = ConcurrentDictionary<string, DateTime>()

    let effectiveThreshold = max 1 policy.Threshold

    member _.Policy = policy

    /// Record one denial in `scopeId` and answer whether it should
    /// raise an alert now.
    ///
    /// `true` at most once per `Cooldown` per scope, and only when the
    /// trailing-window count has reached the threshold.
    member _.Observe(scopeId: string) : bool =
        let now = clock ()
        let windowStart = now - policy.Window

        // Bound the dictionary before inserting a NEW key. An existing
        // scope keeps counting regardless — dropping a tracked scope
        // mid-campaign is worse than exceeding the cap by the scopes
        // already present.
        if not (counts.ContainsKey scopeId) && counts.Count >= MaxTrackedScopes then
            false
        else
            let bucket = counts.GetOrAdd(scopeId, (fun _ -> ResizeArray<DateTime>()))

            let countInWindow =
                lock bucket (fun () ->
                    bucket.Add now

                    // Prune aged-out entries, then cap. Both passes are
                    // over a list bounded by `MaxRetainedPerScope`.
                    bucket.RemoveAll(fun t -> t < windowStart) |> ignore

                    if bucket.Count > MaxRetainedPerScope then
                        bucket.RemoveRange(0, bucket.Count - MaxRetainedPerScope)

                    bucket.Count)

            if countInWindow < effectiveThreshold then
                false
            else
                // Threshold reached — alert unless this scope is still
                // inside its cooldown. `AddOrUpdate` decides and stamps
                // in one atomic step so two concurrent denials on the
                // same scope cannot both alert.
                let mutable fired = false

                lastAlert.AddOrUpdate(
                    scopeId,
                    (fun _ ->
                        fired <- true
                        now),
                    (fun _ previous ->
                        if now - previous >= policy.Cooldown then
                            fired <- true
                            now
                        else
                            previous)
                )
                |> ignore

                fired

/// The alert text. Named and separate so a test can assert what an
/// operator is actually told, and so the wording is reviewable without
/// reading the publish plumbing.
let alertText (policy: AIDenialRateAlertPolicy) (scopeId: string) (toolName: string) : string =
    sprintf
        "AI action-denial rate alert: scope '%s' has reached %d refused client-tool calls within %.0f minute(s) (most recent tool: '%s'). This is the shape of a prompt-injection campaign or a mis-scoped allowlist — review /dev/ai-allowlist or the Health Monitor's AI-denial panel. Further alerts for this scope are suppressed for %.0f minute(s)."
        scopeId
        policy.Threshold
        policy.Window.TotalMinutes
        toolName
        policy.Cooldown.TotalMinutes

/// Observe one denial and publish the `SystemMessage` when the monitor
/// says to. Best-effort throughout: an absent monitor (no policy
/// declared), an absent channel, or a publish failure all leave the
/// conversation untouched.
let observeAndAlert
    (monitorOpt: AIDenialRateMonitor option)
    (channelOpt: INotificationChannel option)
    (scopeId: string)
    (toolName: string)
    : Async<unit> =
    async {
        match monitorOpt, channelOpt with
        | Some monitor, Some channel when monitor.Observe scopeId ->
            let policy = monitor.Policy
            let target = policy.AlertScopeId |> Option.defaultValue scopeId

            try
                do!
                    channel.Publish(
                        target,
                        Notification.SystemMessage(SystemMessageLevel.Warning, alertText policy scopeId toolName)
                    )
            with _ ->
                // The refusal is already enforced and already audited;
                // a dropped alert is a monitoring gap, not a security
                // regression. Never surfaces to the agent loop.
                ()
        | Some monitor, None ->
            // Count anyway, so the cooldown state is consistent if a
            // channel is registered later in the process's life.
            monitor.Observe scopeId |> ignore
        | _ -> ()
    }