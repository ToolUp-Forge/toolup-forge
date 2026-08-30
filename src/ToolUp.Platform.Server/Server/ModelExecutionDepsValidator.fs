// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ModelExecutionDepsValidator

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 728 — fail at composition, not at dispatch ────────────────────
//
// `ServerConfig.ModelExecution = EnabledModelExecutionApi` mounts the
// `ModelExecutionApi` remoting surface. Four of its six methods resolve
// `IModelRegistry` from DI, and no forge compose path registered one until
// the Phase 728 leg — so the whole cost of that gap was WHERE it surfaced:
// a deployment learned about it from the first `GetOutcome` call, as a
// `SubstrateDisabled "model registry"` refusal indistinguishable from a
// substrate the operator had deliberately left off.
//
// This validator moves the discovery to startup, which is the only place a
// composition mistake can be corrected before anyone depends on it.
//
// **`Warning`, not `Error`, and the grade is the decision.** A refusal is
// the honest answer for a deployment that mounts the read face while its
// registry lives elsewhere — behind a peer contract, or in a companion
// composed after this preflight runs. Refusing to start would convert a
// working-if-unusual composition into an outage on upgrade, which is
// exactly the class GP 11 exists to prevent. What was missing was never a
// prohibition; it was the *naming*.
//
// **It probes the `IServiceCollection`, not a forge-held instance** (the
// `DeployPlaneDepsValidator` / `SignedExportDepsValidator` shape): there is
// no registry for forge to inspect unless something registered one, which
// is the whole question being asked. The collection is captured by
// reference and read at `Validate()` time, so registrations appended by
// later compose stages — the Phase 728 leg among them — are visible.

/// Warn when the model-execution API is mounted with no `IModelRegistry`
/// composed. Names the builder that composes one and the hand-registration
/// escape hatch.
type ModelExecutionDepsValidator(config: ServerConfig, services: IServiceCollection, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    let isRegistered (t: Type) =
        services
        |> Seq.exists (fun d -> not (isNull d.ServiceType) && d.ServiceType = t)

    interface IConfigValidator with
        member _.Name = "model-execution-deps"
        member _.Timeout = timeout

        member _.Validate() = async {
            match config.ModelExecution with
            | NoModelExecutionApi -> return Ok
            | EnabledModelExecutionApi ->
                if isRegistered typeof<IModelRegistry> then
                    return Ok
                else
                    return
                        Warning(
                            "ServerConfig.ModelExecution = EnabledModelExecutionApi mounts the ModelExecutionApi surface, but no IModelRegistry is composed. "
                            + "GetOutcome, QueryOutcomes and RequestScore will each refuse with SubstrateDisabled \"model registry\" on the first request. "
                            + $"Compose the model-execution leg — {ComposeModelExecution.BuilderName} ModelExecutionComposeOptions.defaults — which registers the blob-backed BlobModelRegistry over the composed IDataObjectStore and IAuditLog; "
                            + "or register your own IModelRegistry singleton before Build. "
                            + "Note IModelScorer stays consumer-supplied by design (a default scorer needs score providers forge does not have), so RequestScore additionally needs one composed."
                        )
        }