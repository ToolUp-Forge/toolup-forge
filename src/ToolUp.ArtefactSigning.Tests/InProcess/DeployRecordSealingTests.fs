// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.DeployRecordSealingTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.ArtefactSigning
open ToolUp.ArtefactSigning.Tests.Support.InMemoryStores

// ─── Deploy-record sealing over the application signer ───────────────
//
// The platform pack covers the substrate against a keyed-hash stub.
// This pack covers the same seam against the REAL signer — which is
// where the properties that only a signature can have are testable: the
// purpose binding, and the key lifecycle.

let private newProvider () =
    let secrets = InMemorySecretStore() :> ISecretStore
    let audit = InMemoryAuditLog()

    ApplicationSigning.inProcess secrets audit "deploy-key-v1" EcdsaP256 "system"

let private manifest: DeployManifest = {
    DeployManifest.empty with
        App = {
            Name = "Example"
            Slug = "example"
            Region = "eu-west"
        }
        Runtime = {
            DeployManifest.empty.Runtime with
                Framework = "dotnet:10"
        }
}

let private newRecord () =
    let provenance =
        DeployProvenance.none
        |> DeployProvenance.withArtifacts [
            {
                Path = "app.dll"
                ContentDigest = "aa11bb22"
            }
        ]
        |> DeployProvenance.withTranscriptDigest "cc33dd44"
        |> DeployProvenance.withUpstreamProvenanceDigest "an-opaque-value"

    DeployRecord.create "deploy-1" "tenant-1" "build-1" manifest provenance

[<Tests>]
let tests =
    testList "Deploy-record sealing" [

        testCaseAsync "a sealed deploy record verifies against the signer that sealed it"
        <| async {
            let sealer = DeployRecordSealer.ofProvider (newProvider ())
            let record = newRecord ()
            let bytes = DeployRecords.canonicalBytes record

            match! sealer.Seal bytes with
            | Error e -> failtestf "sealing must succeed; got %s" e
            | Ok seal ->
                Expect.equal seal.Scheme (sealer.Scheme()) "the seal carries the sealer's scheme"
                Expect.equal seal.KeyId "deploy-key-v1" "the seal names the key it was minted under"
                Expect.equal seal.Claim "attribution" "an in-process key claims no more than attribution"

                match! sealer.VerifySeal(bytes, seal) with
                | Ok() -> ()
                | Error e -> failtestf "round-trip must verify; got %s" e
        }

        testCaseAsync "the whole verification walk passes for an untampered record"
        <| async {
            let sealer = DeployRecordSealer.ofProvider (newProvider ())
            let record = newRecord ()

            match! sealer.Seal(DeployRecords.canonicalBytes record) with
            | Error e -> failtestf "sealing must succeed; got %s" e
            | Ok seal ->
                // No artifacts are checked here — the record's recorded
                // artifact is not on this machine's disk — so the
                // locator answers "absent" and the walk reports it. The
                // point of this case is the SEAL leg passing over a real
                // signature; the artifact leg has its own coverage.
                match! DeployRecords.verifySeal sealer { Record = record; Seal = seal } with
                | Ok() -> ()
                | Error failures ->
                    failtestf
                        "the seal leg must pass; got %s"
                        (failures
                         |> List.map DeployRecords.DeployRecordVerificationFailure.describe
                         |> String.concat "; ")
        }

        testCaseAsync "editing the opaque upstream slot after sealing breaks the seal"
        <| async {
            // The platform never interprets that slot — but it does
            // cover it, which is the whole of what sealing offers there.
            let sealer = DeployRecordSealer.ofProvider (newProvider ())
            let record = newRecord ()

            match! sealer.Seal(DeployRecords.canonicalBytes record) with
            | Error e -> failtestf "sealing must succeed; got %s" e
            | Ok seal ->
                let edited = {
                    record with
                        Provenance =
                            record.Provenance
                            |> DeployProvenance.withUpstreamProvenanceDigest "a-planted-value"
                }

                match! sealer.VerifySeal(DeployRecords.canonicalBytes edited, seal) with
                | Ok() -> failtest "an edited record must not verify against its old seal"
                | Error _ -> ()
        }

        testCaseAsync "editing a recorded artifact digest after sealing breaks the seal"
        <| async {
            let sealer = DeployRecordSealer.ofProvider (newProvider ())
            let record = newRecord ()

            match! sealer.Seal(DeployRecords.canonicalBytes record) with
            | Error e -> failtestf "sealing must succeed; got %s" e
            | Ok seal ->
                let edited = {
                    record with
                        Provenance =
                            record.Provenance
                            |> DeployProvenance.withArtifacts [
                                {
                                    Path = "app.dll"
                                    ContentDigest = "ffffffff"
                                }
                            ]
                }

                match! sealer.VerifySeal(DeployRecords.canonicalBytes edited, seal) with
                | Ok() -> failtest "a rewritten digest must not verify"
                | Error _ -> ()
        }

        testCaseAsync "a signature minted for another purpose cannot be presented as a deploy-record seal"
        <| async {
            // The purpose is bound INTO the signed bytes, so this is
            // refused by construction rather than by a check that
            // someone has to remember to write.
            let provider = newProvider ()
            let signer = ApplicationSigning.create provider
            let sealer = DeployRecordSealer.overApplicationSigner signer
            let record = newRecord ()
            let bytes = DeployRecords.canonicalBytes record

            match! signer.SignPayload("some.other.purpose", bytes) with
            | Error e -> failtestf "signing must succeed; got %s" (SigningError.describe e)
            | Ok envelope ->
                let smuggled: DeployRecordSeal = {
                    Scheme = sealer.Scheme()
                    KeyId = envelope.Signature.KeyId
                    Claim = AttestationLevel.name envelope.Level
                    Token =
                        // The token shape is opaque by contract, so the
                        // only honest way to build a foreign one here is
                        // through the sealer's own encoding of a
                        // wrong-purpose envelope.
                        DeployRecordSealing.encodeToken envelope
                    SealedAt = envelope.Signature.SignedAt
                }

                match! sealer.VerifySeal(bytes, smuggled) with
                | Ok() -> failtest "a wrong-purpose signature must not verify as a deploy-record seal"
                | Error _ -> ()
        }

        testCaseAsync "a seal minted under a key that is later revoked stops verifying"
        <| async {
            let provider = newProvider ()
            let sealer = DeployRecordSealer.ofProvider provider
            let record = newRecord ()
            let bytes = DeployRecords.canonicalBytes record

            match! sealer.Seal bytes with
            | Error e -> failtestf "sealing must succeed; got %s" e
            | Ok seal ->
                match! sealer.VerifySeal(bytes, seal) with
                | Ok() -> ()
                | Error e -> failtestf "must verify before revocation; got %s" e

                do!
                    ApplicationSigning.revoke provider.Ledger "operator" seal.KeyId "key compromise"
                    |> Async.Ignore

                match! sealer.VerifySeal(bytes, seal) with
                | Ok() -> failtest "a revoked key's seal must be refused, including seals made before the revocation"
                | Error _ -> ()
        }

        testCaseAsync "a seal from a scheme this sealer does not own is refused, not parsed"
        <| async {
            let sealer = DeployRecordSealer.ofProvider (newProvider ())
            let record = newRecord ()
            let bytes = DeployRecords.canonicalBytes record

            match! sealer.Seal bytes with
            | Error e -> failtestf "sealing must succeed; got %s" e
            | Ok seal ->
                let relabelled = {
                    seal with
                        Scheme = "someone.elses.scheme.v1"
                }

                match! sealer.VerifySeal(bytes, relabelled) with
                | Ok() -> failtest "a foreign scheme must not verify"
                | Error reason ->
                    Expect.stringContains reason "someone.elses.scheme.v1" "the refusal names the scheme it refused"
        }

        testCaseAsync "an unreadable seal token is refused with a reason, never absorbed"
        <| async {
            let sealer = DeployRecordSealer.ofProvider (newProvider ())
            let record = newRecord ()
            let bytes = DeployRecords.canonicalBytes record

            match! sealer.Seal bytes with
            | Error e -> failtestf "sealing must succeed; got %s" e
            | Ok seal ->
                match! sealer.VerifySeal(bytes, { seal with Token = "not a token" }) with
                | Ok() -> failtest "an unreadable token must not verify"
                | Error reason -> Expect.isNotEmpty reason "the refusal carries a reason"
        }
    ]