// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.FeatureFlagProviders.OpenFeature.Validator

open System
open OpenFeature
open ToolUp.Platform.ConfigValidation

// ─── Phase 239 — OpenFeature flag-source startup preflight ───────────
//
// Mirrors the `Health` probe (which runs on every `/ready` poll); this
// validator runs ONCE at compose end, before `app.Run()`. It catches the
// silent-misconfiguration case at startup rather than at first flag
// evaluation: a deployment composed the OpenFeature source into its
// `FlagEvaluator` but never registered an external provider via
// `Api.Instance.SetProviderAsync(...)`, so `Api.Instance` is still the
// built-in No-op provider and every external resolution defers to the
// ToolUp declared default — the companion is inert.
//
// That is a `Warning`, not an `Error`: declared-default resolution is
// still correct behaviour, so an unwired companion must NOT abort startup
// (GP 11 / GP 13 — composing the companion stays byte-for-byte safe). An
// actual exception reading the provider metadata is a genuinely broken
// OpenFeature runtime state and aborts startup (`Error`), matching the
// companion-validator precedent (`RedisNotificationChannelValidator`).

[<Literal>]
let private NoOpProviderName = "No-op Provider"

type private Impl() =
    interface IConfigValidator with
        member _.Name = "openfeature-flag-source"
        member _.Timeout = TimeSpan.FromSeconds 1.0

        member _.Validate() = async {
            try
                match Api.Instance.GetProviderMetadata() with
                | null ->
                    return
                        Warning
                            "OpenFeature flag source is composed but no provider is registered on Api.Instance — external flags will resolve to ToolUp declared defaults. Call Api.Instance.SetProviderAsync(...) before serving."
                | md when md.Name = NoOpProviderName ->
                    return
                        Warning
                            "OpenFeature flag source is composed but Api.Instance is the No-op provider — external flags will resolve to ToolUp declared defaults. Call Api.Instance.SetProviderAsync(...) before serving."
                | _ -> return Ok
            with ex ->
                return Error(sprintf "OpenFeature provider preflight failed: %s" ex.Message)
        }

/// Create the validator. Takes no arguments — the OpenFeature provider
/// lives on the process-wide `Api.Instance`, registered by the
/// deployment. Register through `ServerApp.withConfigValidator`.
let create () : IConfigValidator = Impl() :> IConfigValidator