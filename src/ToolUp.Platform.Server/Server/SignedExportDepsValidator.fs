// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SignedExportDepsValidator

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 162 — signed-export fail-closed preflight ────────────────────
//
// `DataSubjectRequests = Enabled { SignExports = true }` declares the
// intent to ship tamper-evident Article-15 exports, but the actual signing
// happens through an `IExportEnvelopeSigner` the `ToolUp.ArtefactSigning`
// `SignedExportBundle` adapter registers in DI over a deployment-supplied
// `IArtefactSigner`. If that registration is missing,
// `DownloadSignedExport` would return a runtime "not enabled" error on
// every call — a silent compliance gap discovered only when an auditor
// asks for the signature. This validator turns that into a hard startup
// refusal naming the missing signer, so the misconfiguration surfaces at
// deploy time, not at audit time.
//
// `Error` (not `Warning`): a deployment that explicitly opted into signed
// exports and shipped no signer is misconfigured, not merely degraded —
// the whole point of `SignExports = true` is the signature. `SignExports
// = false` (the default) and a disabled DSR substrate both validate to
// `Ok`, so a deployment that never opts in is unaffected (GP 11).

/// Refuse startup when `DataSubjectRequests = Enabled { SignExports =
/// true }` but no `IExportEnvelopeSigner` is registered. Inspects the live
/// `IServiceCollection` for the seam the `SignedExportBundle` adapter
/// fills.
type SignedExportDepsValidator(config: ServerConfig, services: IServiceCollection, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    let isRegistered (t: Type) =
        services
        |> Seq.exists (fun d -> not (isNull d.ServiceType) && d.ServiceType = t)

    interface IConfigValidator with
        member _.Name = "signed-export-deps"
        member _.Timeout = timeout

        member _.Validate() = async {
            match config.DataSubjectRequests with
            | DataSubjectRequestMode.Enabled cfg when cfg.SignExports ->
                if isRegistered typeof<IExportEnvelopeSigner> then
                    return Ok
                else
                    return
                        Error(
                            "ServerConfig.DataSubjectRequests = Enabled { SignExports = true }, but no IExportEnvelopeSigner is composed. "
                            + "Signed (tamper-evident) DSR exports require the ToolUp.ArtefactSigning SignedExportBundle adapter registered over an IArtefactSigner — "
                            + "call SignedExportBundle.register services signer (or services.AddSingleton<IExportEnvelopeSigner>(SignedExportBundle.adapter signer)) at compose. "
                            + "Either compose the signer, or set SignExports = false to ship unsigned exports."
                        )
            | _ -> return Ok
        }