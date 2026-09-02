// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ProviderStatusProbeJobHandler

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Providers

// ─── Phase 43.C — live-status probe ───────────────────────────────
//
// An `IJobHandler` registered with `IJobScheduler` at
// `JobPrecision.Minute` that periodically exercises the configured
// entries of ONE scope through `IProviderEntryProbe` and writes what
// it observed back through `IProviderProfile.SetEntryHealth`.
//
// **Scope-at-a-time, for the same reason the refresh job is
// entry-at-a-time**: `IProviderProfile` has no cross-scope
// enumeration, so a sweep cannot discover tenants. The probe job is
// registered for a scope the first time that scope saves a provider
// entry, and from then on it reads the scope's own profile on every
// tick and probes whatever it finds — so entries added later are
// picked up with no further registration.
//
// **Health is advisory and never load-bearing.** `SetEntryHealth` is
// used rather than `Set` precisely so a probe cannot clobber a user
// editing routing in the same instant, and nothing in resolution gates
// on `ProviderHealth` — a `Unhealthy` entry still routes, it just
// carries a warning badge. That is the design in
// `ProviderProfileTypes.fs` and this handler does not quietly change it.
//
// **Cost.** Each tick makes one real provider call per configured
// entry, which is not free — hence the hourly default cadence rather
// than the refresh job's five-minutely one. A deployment that wants a
// different cadence supplies its own cron when registering.
//
// **GP 13.** Registered only when the deployment has BOTH an
// `IProviderProfile` and an `IProviderEntryProbe`. A deployment with
// no provider-profile substrate never constructs this handler.

/// Handler name the scheduler dispatches under. Stable — persisted in
/// every `JobDefinition.Handler` this substrate writes.
[<Literal>]
let HandlerName = "_platform.providers.status.probe"

/// Default cadence: hourly, on the hour. Each tick costs one real
/// provider round-trip per configured entry, so this is deliberately
/// far sparser than the refresh job's `*/5 * * * *`.
[<Literal>]
let DefaultCronExpression = "0 * * * *"

/// Durable payload naming the scope to probe. No entry label: the
/// handler probes whatever the scope's profile holds at dispatch
/// time, so adding an entry needs no new job.
type ProviderStatusProbePayload = {
    ScopeId: string
    Container: string
    Persist: bool
}

module ProviderStatusProbePayload =
    let private options = FableConverters.create ()

    let serialize (payload: ProviderStatusProbePayload) : string =
        JsonSerializer.Serialize(payload, options)

    let tryDeserialize (json: string) : ProviderStatusProbePayload option =
        if String.IsNullOrWhiteSpace json then
            None
        else
            try
                let parsed = JsonSerializer.Deserialize<ProviderStatusProbePayload>(json, options)

                if String.IsNullOrWhiteSpace parsed.Container then
                    None
                else
                    Some parsed
            with _ ->
                None

    let scope (payload: ProviderStatusProbePayload) : StorageScope = {
        ScopeId = payload.ScopeId
        Container = payload.Container
        Persist = payload.Persist
    }

/// Build the `JobRegistration` probing one scope. Idempotent on
/// `(handler, container)`, so registering it on every settings save is
/// safe and cheap.
let registrationFor (scope: StorageScope) (cron: string) (createdBy: string) : JobRegistration =
    let payload: ProviderStatusProbePayload = {
        ScopeId = scope.ScopeId
        Container = scope.Container
        Persist = scope.Persist
    }

    {
        ScopeId = scope.ScopeId
        Handler = HandlerName
        Payload = ProviderStatusProbePayload.serialize payload
        Trigger = CronTrigger cron
        Idempotency =
            Some {
                Key = $"{HandlerName}:{scope.Container}"
                TtlSeconds = 365 * 24 * 60 * 60
            }
        RetryPolicy = JobRetryPolicy.defaults
        ShardKey = Some scope.Container
        Precision = Minute
        CreatedBy = createdBy
        Tags = Map [ "substrate", "provider-status-probe" ]
    }

/// `IJobHandler` for `_platform.providers.status.probe`.
type ProviderStatusProbeJobHandler(providerProfile: IProviderProfile, probe: IProviderEntryProbe, logger: ILogger) =

    interface IJobHandler with
        member _.Execute(ctx) = async {
            match ProviderStatusProbePayload.tryDeserialize ctx.Payload with
            | None ->
                let msg = $"[ProviderStatusProbe] malformed payload for job %A{ctx.JobId}"
                logger.Warn msg
                return PermanentFailure msg
            | Some payload ->
                let scope = ProviderStatusProbePayload.scope payload
                let! profile = providerProfile.Get scope

                match profile with
                | None ->
                    // Nothing configured in this scope (yet, or any
                    // more). Not a failure.
                    return Success
                | Some p ->
                    let now = DateTime.UtcNow

                    // Sequential, not parallel: a scope with a dozen
                    // entries would otherwise fan a dozen billed
                    // provider calls out at once every tick, and the
                    // job has no deadline pressure that would justify
                    // it.
                    let mutable failures = 0

                    for entry in p.Entries do
                        let! outcome = probe.Probe(scope, entry)

                        let health =
                            match outcome with
                            | Ok o -> ProviderProbeOutcome.toHealth entry.Health now o
                            | Error e -> ProviderProbeOutcome.toHealth entry.Health now (ProviderProbeOutcome.failed e)

                        match outcome with
                        | Ok o when o.Reachable -> ()
                        | _ -> failures <- failures + 1

                        let! written = providerProfile.SetEntryHealth(scope, entry.Label, health)

                        match written with
                        | Ok() -> ()
                        | Error e ->
                            logger.Warn
                                $"[ProviderStatusProbe] health write failed for '{entry.Label}' scope={scope.Container}: {e}"

                    logger.Debug
                        $"[ProviderStatusProbe] probed {p.Entries.Length} entrie(s) in scope={scope.Container}; {failures} unreachable"

                    // An unreachable PROVIDER is not a failed JOB — the
                    // probe did its work and recorded the verdict. A
                    // retry would just re-bill the same failing call.
                    return Success
        }

/// Factory used by the compose-time wiring.
let create (providerProfile: IProviderProfile) (probe: IProviderEntryProbe) (logger: ILogger) : IJobHandler =
    ProviderStatusProbeJobHandler(providerProfile, probe, logger) :> IJobHandler