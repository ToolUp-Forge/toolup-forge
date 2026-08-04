// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ContentScanners.ClamAv.Health

open System
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.ContentScanners.ClamAv.ClamAvContentScanner

// ─── Phase 515 — ClamAV content-scanner health probe ─────────────────
//
// A PING against the configured clamd. Registered as `Readiness`, so it
// appears on `/ready` rather than driving orchestrator RESTARTS from
// `/health` — restarting the app does not fix an unreachable daemon.
//
// **Whether an unreachable clamd is `Unhealthy` or `Degraded` follows
// the deployment's own scan policy, and that is the point.** Under
// `FailClosedOnScanError` a dead daemon means every upload is refused —
// the replica genuinely cannot serve its purpose, so `Unhealthy` is
// truthful and taking it out of rotation is the right response. Under
// `FailOpenOnScanError` the same daemon being dead degrades a control
// but breaks no request path, so `Degraded` is truthful and failing
// readiness would empty the rotation for a scanner outage the deployment
// has already said it tolerates. Reporting one verdict for both postures
// would be wrong for one of them.

/// Companion-contributed `IHealthCheck` for the ClamAV scanner. Pass the
/// same scanner instance that was composed, and the same
/// `ContentScanPolicy`, so the probe reflects what an outage actually
/// costs this deployment.
type ClamAvHealthCheck(scanner: ClamAvContentScanner, policy: ContentScanPolicy) =

    interface IHealthCheck with
        member _.Name = "content_scanner:clamav"
        member _.Kind = Readiness

        // A PING on a healthy daemon is a loopback or in-cluster
        // round-trip; 2s absorbs jitter without hiding a wedged daemon
        // under the timeout.
        member _.Timeout = TimeSpan.FromSeconds 2.0

        member _.Check() = async {
            match! scanner.Ping() with
            | Ok() -> return Healthy
            | Error reason ->
                match policy.OnScanError with
                | FailClosedOnScanError ->
                    return
                        Unhealthy(
                            sprintf
                                "%s — this deployment fails closed on a scan error, so every upload is currently being refused"
                                reason
                        )
                | FailOpenOnScanError ->
                    return
                        Degraded(
                            sprintf
                                "%s — this deployment fails open on a scan error, so uploads are being ADMITTED UNSCANNED"
                                reason
                        )
        }