// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.ExternalCompute.Http

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets
open ToolUp.Platform.Server

// ─── Phase 322 — the companion's probes and its composition helper ────
//
// Per the companion-authoring guide: a companion registers an
// `IHealthCheck` probe and, where connection state is testable, an
// `IConfigValidator` preflight.
//
// **Why the readiness probe needs a dedicated health URL and will not
// invent one.** Every other URL this companion knows about has a side
// effect or needs a subject: probing `SubmitUrl` would SUBMIT WORK on
// every readiness poll, and probing `StatusUrlTemplate` needs a job id
// there is no safe value for. So the probe exists only when the config
// names a health endpoint, and `tryHealthCheck` returns `None`
// otherwise — an absent probe is honest, and a probe that submitted a
// job every fifteen seconds would be a defect wearing a health check's
// name.
//
// **The probe is `Readiness`, and an unreachable service is `Degraded`
// rather than `Unhealthy`.** `/health` drives orchestrator RESTARTS, and
// restarting this replica does not fix an unreachable compute service.
// Beyond that: external compute is a hand-off destination, not a request
// path. A deployment whose compute service is down still serves every
// page, every API and every in-process job; what it cannot do is accept
// new external work, and that is exactly what `Degraded` means. Draining
// the whole rotation for it would convert a partial outage into a total
// one.
//
// **What the validator catches that `create` cannot.** `create` already
// refuses a malformed config (shape), so the validator's job is the two
// things only a running deployment can answer: whether the configured
// credential is actually IN `ISecretStore` — the single most common
// deployment miss, and one that otherwise surfaces as a 401 on the first
// submission hours later — and whether the service is reachable at all.

/// Readiness probe for the HTTP external-compute backend. Construct via
/// `HttpComputeHealth.tryHealthCheck`, which returns `None` when the
/// config names no health endpoint.
type HttpComputeHealthCheck(dispatcher: HttpComputeDispatcher, timeout: TimeSpan) =

    interface IHealthCheck with
        member _.Name = sprintf "external_compute:%s" dispatcher.Config.Backend
        member _.Kind = Readiness
        member _.Timeout = timeout

        member _.Check() = async {
            match! dispatcher.ProbeHealth() with
            | None ->
                // Unreachable in practice: the probe is only registered
                // when a health URL exists. Reported rather than thrown,
                // because a probe that throws is indistinguishable from
                // the dependency being down.
                return Degraded "no health endpoint is configured for this external-compute backend"
            | Some(Result.Ok()) -> return Healthy
            | Some(Result.Error reason) ->
                return
                    Degraded(
                        sprintf
                            "%s — new external work cannot be accepted; in-process request handling and local jobs are unaffected"
                            reason
                    )
        }

/// Startup preflight for the HTTP external-compute backend.
type HttpComputeConfigValidator(dispatcher: HttpComputeDispatcher, secretStore: ISecretStore, timeout: TimeSpan) =

    let config = dispatcher.Config

    interface IConfigValidator with
        member _.Name = sprintf "external-compute-http (%s)" config.Backend
        member _.Timeout = timeout

        member _.Validate() = async {
            // Shape first, and as an Error: `create` refuses these, so
            // reaching here means the validator was handed a config the
            // dispatcher was not built from. Reporting it is cheap and
            // the alternative is a validator that silently passes a
            // config nothing can use.
            match HttpComputeConfig.problems config with
            | problem :: _ ->
                return ValidationResult.Error(sprintf "external-compute HTTP config is not usable: %s" problem)
            | [] ->
                // The credential. Read here at startup for the same
                // reason it is read per call at runtime — this is the
                // miss that otherwise surfaces as a 401 on the first
                // submission, long after deploy.
                let! secretProblem = async {
                    match config.Auth with
                    | None -> return None
                    | Some auth ->
                        let! secret = secretStore.GetSecret(auth.SecretScope, auth.SecretKey)

                        match secret with
                        | Some value when not (String.IsNullOrWhiteSpace value) -> return None
                        | _ ->
                            return
                                Some(
                                    sprintf
                                        "the configured credential is not in ISecretStore at %s/%s, so every submission to backend '%s' would be refused"
                                        auth.SecretScope
                                        auth.SecretKey
                                        config.Backend
                                )
                }

                match secretProblem with
                | Some problem -> return ValidationResult.Error problem
                | None ->
                    match! dispatcher.ProbeHealth() with
                    | None ->
                        // No health endpoint configured. A Warning, not
                        // an Error: the deployment is usable, but nothing
                        // will notice the compute service going away
                        // until a submission fails.
                        return
                            ValidationResult.Warning(
                                sprintf
                                    "backend '%s' has no HealthUrl configured, so neither preflight nor readiness can tell whether the compute service is reachable"
                                    config.Backend
                            )
                    | Some(Result.Ok()) -> return ValidationResult.Ok
                    | Some(Result.Error reason) ->
                        // A Warning rather than an Error, deliberately.
                        // Aborting startup here would mean a compute
                        // service that is briefly down takes the whole
                        // deployment with it — including every path that
                        // has nothing to do with external compute.
                        return
                            ValidationResult.Warning(
                                sprintf "%s — external work cannot be submitted until this clears" reason
                            )
        }

[<RequireQualifiedAccess>]
module HttpComputeHealth =

    /// The readiness probe, when the config names a health endpoint.
    /// `None` otherwise — see the file header for why one is not
    /// invented.
    let tryHealthCheck (dispatcher: HttpComputeDispatcher) : IHealthCheck option =
        match dispatcher.Config.HealthUrl with
        | None -> None
        | Some _ ->
            // The probe's own budget, capped by the aggregator's. Reusing
            // the per-request budget would let a 30s submit timeout
            // become a 30s readiness probe.
            let timeout =
                if dispatcher.Config.RequestTimeout < TimeSpan.FromSeconds 5.0 then
                    dispatcher.Config.RequestTimeout
                else
                    TimeSpan.FromSeconds 5.0

            Some(HttpComputeHealthCheck(dispatcher, timeout) :> IHealthCheck)

    /// The startup preflight. Always available — its most valuable check
    /// (is the credential present?) needs no health endpoint.
    let configValidator (dispatcher: HttpComputeDispatcher) (secretStore: ISecretStore) : IConfigValidator =
        HttpComputeConfigValidator(dispatcher, secretStore, IConfigValidator.defaultTimeout) :> IConfigValidator

[<RequireQualifiedAccess>]
module HttpComputeCompose =

    /// Compose the HTTP external-compute backend onto a `ServerApp`.
    ///
    /// One call folds in everything the companion contributes: the
    /// `IExternalComputeDispatcher` DI singleton, the readiness probe
    /// (when a health URL is configured), the startup preflight, and
    /// `ServerConfig.ExternalCompute = CustomExternalCompute`.
    ///
    /// **The mode flip is not cosmetic.** Under the default
    /// `NoExternalCompute`, compose registers `NoExternalComputeDispatcher`
    /// — and a later registration of the same service type is what
    /// `GetService` resolves, so leaving the mode alone would make
    /// whether real work is submitted depend on registration ORDER
    /// between this companion and the SDK's own compose. `CustomExternalCompute`
    /// is precisely the mode meaning "a companion under
    /// `src/ExternalCompute/` owns this seam"; setting it here is what
    /// makes the composition unambiguous.
    ///
    /// A deployment that never calls this keeps the `NoExternalCompute`
    /// default and pays nothing (GP 11 + GP 13) — no probe, no validator,
    /// no HTTP client, no allocation.
    let withHttpCompute
        (config: HttpComputeConfig)
        (secretStore: ISecretStore)
        (httpClient: System.Net.Http.HttpClient)
        (logger: ILogger)
        (app: ServerApp)
        : ServerApp =
        // Built through `createTyped`, which validates and REFUSES a
        // malformed config, so a composition can never hold an unusable
        // dispatcher.
        let dispatcher =
            HttpComputeDispatcher.createTyped config secretStore httpClient logger

        let registerDispatcher (services: IServiceCollection) =
            services.AddSingleton<IExternalComputeDispatcher>(dispatcher :> IExternalComputeDispatcher)

        let withProbe app =
            match HttpComputeHealth.tryHealthCheck dispatcher with
            | Some probe -> ServerApp.withHealthCheck probe app
            | None -> app

        let composed =
            {
                app with
                    Config = {
                        app.Config with
                            ExternalCompute = CustomExternalCompute
                    }
            }
            |> withProbe
            |> ServerApp.withConfigValidator (HttpComputeHealth.configValidator dispatcher secretStore)

        let extensions = composed.Extensions

        {
            composed with
                Extensions = {
                    extensions with
                        ServiceConfig =
                            match extensions.ServiceConfig with
                            | None -> Some registerDispatcher
                            | Some existing -> Some(fun services -> registerDispatcher (existing services))
                }
        }