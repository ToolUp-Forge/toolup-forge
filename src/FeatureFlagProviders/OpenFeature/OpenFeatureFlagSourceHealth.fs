// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.FeatureFlagProviders.OpenFeature.Health

open System
open OpenFeature
open ToolUp.Platform.HealthChecks

// ─── Phase 239 — OpenFeature flag-source readiness probe ─────────────
//
// The companion resolves through the process-wide OpenFeature
// `OpenFeature.Api.Instance` (the deployment registers its vendor provider via
// `OpenFeature.Api.Instance.SetProviderAsync(...)`, not through our `create`). The
// only readiness signal the OpenFeature .NET 2.3.0 surface exposes
// publicly is the *registered provider's metadata* — `FeatureProvider`
// has no public status getter (status is event-driven internally), so
// the probe keys off `OpenFeature.Api.Instance.GetProviderMetadata()`.
//
// Before any `SetProviderAsync`, `OpenFeature.Api.Instance` is the built-in **No-op
// provider** (metadata name `"No-op Provider"`), under which every
// `Resolve` returns an `ErrorType` and the companion defers to the
// ToolUp declared default. A deployment that composed this companion but
// never registered an external provider is therefore *inert* — flag
// resolution silently falls back to declared defaults. That is reported
// `Degraded` (not `Unhealthy`): resolution still works, so `/ready` must
// not flip to 503 — but the operator should see that the external
// backend is contributing nothing. A real provider reports `Healthy`.
//
// The probe is a pure in-process metadata read (no network), so the
// 1-second budget is generous; it cannot burn vendor quota.

[<Literal>]
let private NoOpProviderName = "No-op Provider"

/// Companion-contributed `IHealthCheck` for the OpenFeature flag source.
/// Construct via `Health.create ()` and register through
/// `ServerApp.withHealthCheck`. Reads the process-wide
/// `OpenFeature.Api.Instance` provider — the same one `OpenFeatureFlagSource`
/// resolves through — so the probe reflects the adapter's actual backend.
type OpenFeatureFlagSourceHealth() =
    interface IHealthCheck with
        member _.Name = "feature_flags:openfeature"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 1.0

        member _.Check() = async {
            try
                match Api.Instance.GetProviderMetadata() with
                | null ->
                    return
                        Degraded
                            "no OpenFeature provider registered on Api.Instance — flag resolution defers to ToolUp declared defaults"
                | md when md.Name = NoOpProviderName ->
                    return
                        Degraded
                            "Api.Instance is the No-op provider (no external OpenFeature provider registered) — flag resolution defers to ToolUp declared defaults"
                | _ -> return Healthy
            with ex ->
                return Unhealthy ex.Message
        }

/// Create the probe. Takes no arguments — the OpenFeature provider lives
/// on the process-wide `OpenFeature.Api.Instance`, registered by the deployment.
let create () : IHealthCheck =
    OpenFeatureFlagSourceHealth() :> IHealthCheck