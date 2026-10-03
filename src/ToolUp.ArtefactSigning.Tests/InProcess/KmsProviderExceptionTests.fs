// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.KmsProviderExceptionTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.ArtefactSigning

// ─── Phase 972 — the cloud-KMS signers classify a missing key ───────────
//
// Each cloud signer reaches its KMS through `Async.AwaitTask`, which raises a
// faulted task's `AggregateException` rather than the SDK exception inside
// it. Before Phase 972 each signer type-tested the SDK exception directly, so
// a deleted, disabled or forbidden key — which the code maps to
// `KeyUnavailable` — was reported as a generic `CryptoFailure`. These pins
// drive each REAL signer over its REAL SDK client type, with only the one
// network call overridden to fault its task with the SDK's own exception, the
// way the SDK does when the KMS answers 404 / 403.

let private artefact = Text.Encoding.UTF8.GetBytes "artefact"

let private expectKeyUnavailable (label: string) (signer: IArtefactSigner) = async {
    match! Async.Catch(signer.Sign artefact) with
    | Choice1Of2(Error(KeyUnavailable _)) -> ()
    | Choice1Of2(Error other) -> failtestf "%s: expected KeyUnavailable, got %A" label other
    | Choice1Of2(Ok _) -> failtestf "%s: a faulted KMS call reported a signature" label
    | Choice2Of2 escaped -> failtestf "%s: the failure ESCAPED as %s" label (escaped.GetType().Name)
}

// AWS — the real client type; only `SignAsync` is overridden. No network is
// reached: the credentials and region exist only to construct the client.
type private FaultingAwsKms(fault: unit -> exn) =
    inherit
        Amazon.KeyManagementService.AmazonKeyManagementServiceClient(
            Amazon.Runtime.BasicAWSCredentials("test-access-key", "test-secret-key"),
            Amazon.RegionEndpoint.EUWest2
        )

    override _.SignAsync
        (_request: Amazon.KeyManagementService.Model.SignRequest, _ct: CancellationToken)
        : Task<Amazon.KeyManagementService.Model.SignResponse> =
        Task.FromException<Amazon.KeyManagementService.Model.SignResponse>(fault ())

// Azure — the SDK's own mocking constructors; only `SignAsync` is overridden.
type private FaultingAzureCrypto(fault: unit -> exn) =
    inherit Azure.Security.KeyVault.Keys.Cryptography.CryptographyClient()

    override _.SignAsync
        (
            _algorithm: Azure.Security.KeyVault.Keys.Cryptography.SignatureAlgorithm,
            _digest: byte[],
            _ct: CancellationToken
        ) : Task<Azure.Security.KeyVault.Keys.Cryptography.SignResult> =
        Task.FromException<Azure.Security.KeyVault.Keys.Cryptography.SignResult>(fault ())

type private UnusedAzureKeys() =
    inherit Azure.Security.KeyVault.Keys.KeyClient()

// GCP — the abstract client; only `AsymmetricSignAsync` is overridden.
type private FaultingGcpKms(fault: unit -> exn) =
    inherit Google.Cloud.Kms.V1.KeyManagementServiceClient()

    override _.AsymmetricSignAsync
        (_request: Google.Cloud.Kms.V1.AsymmetricSignRequest, _settings: Google.Api.Gax.Grpc.CallSettings)
        : Task<Google.Cloud.Kms.V1.AsymmetricSignResponse> =
        Task.FromException<Google.Cloud.Kms.V1.AsymmetricSignResponse>(fault ())

let private gcpKeyVersion =
    "projects/p/locations/global/keyRings/r/cryptoKeys/k/cryptoKeyVersions/1"

let private rpc (code: Grpc.Core.StatusCode) () : exn =
    Grpc.Core.RpcException(Grpc.Core.Status(code, string code))

let tests =
    testList "Phase 972 — cloud-KMS signers classify a faulted KMS call" [

        testCaseAsync "AWS KMS: NotFound / Disabled / KMSInvalidState are KeyUnavailable"
        <| async {
            for label, fault in
                [
                    "NotFound", (fun () -> Amazon.KeyManagementService.Model.NotFoundException "no such key" :> exn)
                    "Disabled", (fun () -> Amazon.KeyManagementService.Model.DisabledException "key disabled" :> exn)
                    "KMSInvalidState",
                    (fun () -> Amazon.KeyManagementService.Model.KMSInvalidStateException "pending deletion" :> exn)
                ] do
                do!
                    expectKeyUnavailable
                        ("AWS " + label)
                        (ToolUp.ArtefactSigning.AwsKms.AwsKmsArtefactSigner.create (new FaultingAwsKms(fault)) "alias/k")
        }

        testCaseAsync "Azure Key Vault: 404 and 403 are KeyUnavailable"
        <| async {
            for status in [ 404; 403 ] do
                let signer =
                    ToolUp.ArtefactSigning.AzureKeyVault.AzureKeyVaultArtefactSigner(
                        new FaultingAzureCrypto(fun () -> Azure.RequestFailedException(status, "key vault said no")),
                        new UnusedAzureKeys(),
                        "signing-key"
                    )
                    :> IArtefactSigner

                do! expectKeyUnavailable (sprintf "Azure %d" status) signer
        }

        testCaseAsync "GCP KMS: NotFound / FailedPrecondition / PermissionDenied are KeyUnavailable"
        <| async {
            for code in
                [
                    Grpc.Core.StatusCode.NotFound
                    Grpc.Core.StatusCode.FailedPrecondition
                    Grpc.Core.StatusCode.PermissionDenied
                ] do
                let signer =
                    ToolUp.ArtefactSigning.GoogleCloudKms.GoogleCloudKmsArtefactSigner.createFromName
                        (new FaultingGcpKms(rpc code))
                        gcpKeyVersion

                do! expectKeyUnavailable (sprintf "GCP %A" code) signer
        }

        testCaseAsync "a fault that is not a key problem stays CryptoFailure"
        <| async {
            let signer =
                ToolUp.ArtefactSigning.AwsKms.AwsKmsArtefactSigner.create
                    (new FaultingAwsKms(fun () -> InvalidOperationException "boom" :> exn))
                    "alias/k"

            match! signer.Sign artefact with
            | Error(CryptoFailure _) -> ()
            | other -> failtestf "expected CryptoFailure, got %A" other
        }
    ]