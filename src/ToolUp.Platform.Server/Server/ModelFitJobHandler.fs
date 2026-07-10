// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 449 — fit-run envelope + IJobHandler ─────────────────────────
//
// The envelope orchestrates one fit: resolve the provider by kind, compute
// the forge-owned composite key, execute the provider, evaluate the gates,
// emit the audit trail, and return the assembled outcome. Every run is
// audited under `_platform.audit` carrying the composite key (GP 6). Gate
// failure is *data* — a `GateVerdict` in the outcome plus a
// `ModelFitGateFailed` audit row — never an exception (acceptance).
//
// Forge owns `CompositeKey` + `GateVerdicts`; the provider owns the
// artifact + diagnostics + its self-reported cost. The envelope overwrites
// whatever the provider set for the two forge-owned fields, so a fit's
// identity + gate outcome cannot be forged by a misbehaving provider.

module ModelFitEnvelope =
    /// STJ options with the full F# converter set — matches the
    /// Fable-compatible wire shape used everywhere else in the SDK, so a
    /// persisted `FitRequest` job payload round-trips faithfully (DU /
    /// Option / record / list / Map).
    let private jsonOptions = FableConverters.create ()

    /// Serialise a `FitRequest` to the persisted job-payload string.
    let serialiseRequest (request: FitRequest) : string =
        JsonSerializer.Serialize(request, jsonOptions)

    /// Parse a persisted job payload back into a `FitRequest`. `Error`
    /// carries the failure detail; the job handler treats a parse failure
    /// as a `PermanentFailure` (a malformed payload will not recover on
    /// retry).
    let tryParseRequest (payload: string) : Result<FitRequest, string> =
        try
            Ok(JsonSerializer.Deserialize<FitRequest>(payload, jsonOptions))
        with ex ->
            Error ex.Message

    /// Run one fit through the envelope. Resolves the provider, computes the
    /// forge-owned composite key, executes the provider, evaluates the
    /// requested gates, and emits `ModelFitStarted` / `ModelFitCompleted`
    /// (and `ModelFitGateFailed` when any gate fails). Returns the assembled
    /// outcome regardless of gate pass/fail — a failed gate is a verdict in
    /// the outcome, not an `Error`. `Error` is reserved for envelope-level
    /// failures (unknown kind, provider raised).
    let runFit
        (registry: ModelFitProviderRegistry)
        (audit: IAuditLog)
        (request: FitRequest)
        : Async<Result<FitOutcome, ModelFitError>> =
        async {
            match registry.TryResolve request.ProviderKind with
            | None -> return Error(ModelFitError.ProviderNotFound request.ProviderKind)
            | Some provider ->
                let key =
                    FitCompositeKey.compute
                        request.SpecRef.SpecHash
                        (DatasetVersionRef.key request.DatasetVersion)
                        request.Seed
                        provider.Kind
                        provider.ProviderVersion

                do!
                    audit.Record(
                        request.ScopeId,
                        ModelFitStarted {
                            CompositeKeyHash = key.Hash
                            SpecHash = key.SpecHash
                            DatasetVersion = key.DatasetVersion
                            Seed = key.Seed
                            ProviderId = key.ProviderId
                            ProviderVersion = key.ProviderVersion
                            ScopeId = request.ScopeId
                        }
                    )

                let! raw = async {
                    try
                        let! outcome = provider.Fit request
                        return Ok outcome
                    with ex ->
                        return Error ex.Message
                }

                match raw with
                | Error message -> return Error(ModelFitError.ProviderFailed(provider.Kind, message))
                | Ok providerOutcome ->
                    // Forge owns the composite key + gate verdicts — overwrite
                    // whatever the provider set so identity + gate outcome are
                    // authoritative regardless of provider behaviour.
                    let verdicts = Gate.evaluateAll providerOutcome.Diagnostics request.Gates

                    let outcome = {
                        providerOutcome with
                            CompositeKey = key
                            GateVerdicts = verdicts
                    }

                    let failed = verdicts |> List.filter (fun v -> not v.Passed)

                    do!
                        audit.Record(
                            request.ScopeId,
                            ModelFitCompleted {
                                CompositeKeyHash = key.Hash
                                ProviderId = key.ProviderId
                                ProviderVersion = key.ProviderVersion
                                DiagnosticCount = Map.count providerOutcome.Diagnostics
                                GatesEvaluated = List.length verdicts
                                GatesFailed = List.length failed
                                ArtifactHash = outcome.ArtifactRef.ContentHash
                                ScopeId = request.ScopeId
                            }
                        )

                    if not (List.isEmpty failed) then
                        do!
                            audit.Record(
                                request.ScopeId,
                                ModelFitGateFailed {
                                    CompositeKeyHash = key.Hash
                                    ProviderId = key.ProviderId
                                    FailedGates = failed |> List.map (fun v -> v.Name)
                                    ScopeId = request.ScopeId
                                }
                            )

                    return Ok outcome
        }

/// `IJobHandler` bound to `_platform.modelfit.run`. Deserialises a
/// `FitRequest` from the job payload and runs it through the envelope. A
/// malformed payload or an unknown provider kind is a `PermanentFailure`
/// (no retry recovers it); a provider exception is a `TransientFailure`
/// (the fit may succeed on a later attempt).
type ModelFitJobHandler(registry: ModelFitProviderRegistry, audit: IAuditLog, logger: ILogger) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            match ModelFitEnvelope.tryParseRequest ctx.Payload with
            | Error e ->
                logger.Error($"ModelFitJobHandler: malformed FitRequest payload — {e}", None)
                return PermanentFailure $"malformed FitRequest payload: {e}"
            | Ok request ->
                match! ModelFitEnvelope.runFit registry audit request with
                | Ok _ -> return Success
                | Error(ModelFitError.ProviderNotFound k) ->
                    logger.Warn $"ModelFitJobHandler: no provider registered for kind '{k}'"
                    return PermanentFailure(ModelFitError.describe (ModelFitError.ProviderNotFound k))
                | Error(ModelFitError.ProviderFailed(k, m)) ->
                    logger.Warn $"ModelFitJobHandler: provider '{k}' failed — {m}"
                    return TransientFailure(ModelFitError.describe (ModelFitError.ProviderFailed(k, m)))
        }

module ModelFitJobHandler =
    /// Reserved scheduler handler name for the fit-run job.
    [<Literal>]
    let HandlerName = "_platform.modelfit.run"

    /// Construct the fit-run job handler.
    let create (registry: ModelFitProviderRegistry) (audit: IAuditLog) (logger: ILogger) : IJobHandler =
        ModelFitJobHandler(registry, audit, logger) :> IJobHandler