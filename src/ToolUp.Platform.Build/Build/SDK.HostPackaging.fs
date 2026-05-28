// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.IO
open System.IO.Compression
open Fake.Core

/// Host packaging — emit the deployable bundle each cloud runtime expects.
///
/// The three functions are nominally distinct so consumer FAKE pipelines
/// can wire host-specific Verify steps and the call site documents the
/// deployment intent. The shipped behaviour today is identical: zip the
/// contents of a `dotnet publish` output directory. Per-host divergence
/// (Lambda Layers, multi-arch bundles, source-vs-published GCF inputs)
/// is the extension seam.
module HostPackaging =

    /// 100 KB — catches the "publish produced nothing" failure mode
    /// without false-positiving on tiny framework-dependent apps.
    [<Literal>]
    let MinBundleBytes = 102400L

    let private zipPublishDir (publishDir: string) (outputZipPath: string) =
        if not (Directory.Exists publishDir) then
            failwithf "Host packaging: publish directory does not exist: %s" publishDir

        let hasContent =
            Directory.EnumerateFileSystemEntries(publishDir, "*", SearchOption.TopDirectoryOnly)
            |> Seq.exists (fun _ -> true)

        if not hasContent then
            failwithf "Host packaging: publish directory is empty: %s" publishDir

        if File.Exists outputZipPath then
            File.Delete outputZipPath

        let outputDir = Path.GetDirectoryName outputZipPath

        if not (System.String.IsNullOrEmpty outputDir) then
            Directory.CreateDirectory outputDir |> ignore

        ZipFile.CreateFromDirectory(publishDir, outputZipPath, CompressionLevel.Optimal, false)

        let size = (FileInfo outputZipPath).Length

        if size < MinBundleBytes then
            failwithf
                "Host packaging: emitted bundle %s is %d bytes (< %d) — `dotnet publish` likely produced no output."
                outputZipPath
                size
                MinBundleBytes

        Trace.tracefn "  packed %s (%d bytes)" outputZipPath size

    /// Pack `dotnet publish` output for Azure Functions deployment.
    /// Consumed by `az functionapp deployment source config-zip --src ...`
    /// or `func azure functionapp publish ...`.
    let packAzureFunctions (publishDir: string) (outputZipPath: string) = zipPublishDir publishDir outputZipPath

    /// Pack `dotnet publish` output for AWS Lambda deployment.
    /// Consumed by `aws lambda update-function-code --zip-file fileb://...`
    /// or `dotnet lambda deploy-function`.
    let packAwsLambda (publishDir: string) (outputZipPath: string) = zipPublishDir publishDir outputZipPath

    /// Pack `dotnet publish` output for Google Cloud Functions deployment.
    /// Consumed by `gcloud functions deploy --gen2 --source=<extracted-zip>`
    /// when the operator pre-builds locally rather than letting the GCF
    /// buildpack run `dotnet publish` server-side.
    let packGoogleCloudFunctions (publishDir: string) (outputZipPath: string) = zipPublishDir publishDir outputZipPath