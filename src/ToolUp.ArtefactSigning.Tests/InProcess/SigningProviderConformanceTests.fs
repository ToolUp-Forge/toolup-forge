// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.SigningProviderConformanceTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.ArtefactSigning
open ToolUp.ArtefactSigning.Tests.Contracts
open ToolUp.ArtefactSigning.Tests.Support
open ToolUp.ArtefactSigning.Tests.Support.InMemoryStores

// ─── The provider-conformance matrix, plus its control ─────────────────
//
// Every shipped application signing provider is bound against the one
// conformance pack here, and the pack is then bound against deliberately
// broken providers to show it discriminates. The second half is the load
// -bearing one: a pack that passes everything and a pack that works are
// indistinguishable from their output alone.

/// Run a pack in-process and return the leaf names of the cases that did
/// not pass. `evalTestsSilent` evaluates without printing, so a
/// deliberately-failing pack does not spray failure output into a green
/// run.
let private failingCaseNames (pack: Test) =
    Impl.evalTestsSilent pack
    |> Async.RunSynchronously
    |> List.filter (fun (_, summary) -> not summary.result.isPassed)
    |> List.map (fun (flat, _) -> flat.name |> List.last)

/// Build a conformance fixture over a provider family.
///
/// `makeProvider` mints a provider for the n-th key id; `wrap` turns a
/// provider into the signer under test — the shipped implementation for
/// a real provider, a perturbation for the probe. The two share one
/// secret store and one ledger across rotations, which is what makes an
/// earlier key's material stay resolvable after it is retired.
let private fixtureOver
    (name: string)
    (level: AttestationLevel)
    (makeProvider: ISecretStore -> ISigningKeyLedger -> int -> SigningProvider)
    (wrap: SigningProvider -> IApplicationSigner)
    ()
    : ISigningProviderConformance.SigningProviderFixture =

    let secrets = InMemorySecretStore() :> ISecretStore
    let ledger = SecretStoreSigningKeyLedger.create secrets

    let activate (p: SigningProvider) =
        ApplicationSigning.activate ledger "system" (p.Signer.KeyId())
        |> Async.RunSynchronously
        |> ignore

    let counter = ref 1
    let first = makeProvider secrets ledger 1
    activate first
    let current = ref first

    let rotate () =
        ApplicationSigning.retire ledger "system" (current.Value.Signer.KeyId())
        |> Async.RunSynchronously
        |> ignore

        counter.Value <- counter.Value + 1
        let next = makeProvider secrets ledger counter.Value
        activate next
        current.Value <- next
        wrap next

    {
        Name = name
        DeclaredLevel = level
        Signer = wrap first
        Rotate = rotate
        Ledger = ledger
    }

// ── the shipped provider families ──────────────────────────────────────

/// Keys held in the deployment's own secret store and loaded into the
/// signing process. This is the development / in-process provider; with
/// a file-backed store it is the file-keyed one. Level `Attribution`.
let private inProcessProvider (secrets: ISecretStore) (ledger: ISigningKeyLedger) (n: int) =
    let audit = InMemoryAuditLog() :> IAuditLog

    ApplicationSigning.inProcess secrets audit $"app-signing-v{n}" EcdsaP256 "system"
    |> ApplicationSigning.withLedger ledger

/// Keys held by an external key-management service: the process sends a
/// digest out and receives a signature back, never seeing key material.
/// Level `IsolatedSigner`. Certified against the offline stand-in for the
/// same reason the byte-level cloud arms are — see `KeyManagedStandIn`.
let private keyManagedProvider (secrets: ISecretStore) (ledger: ISigningKeyLedger) (n: int) =
    let keyId = $"managed-signing-v{n}"

    ApplicationSigning.keyManaged
        "key-managed"
        (KeyManagedStandIn.create secrets keyId)
        (DefaultArtefactVerifier.create secrets)
        ledger

let private conforming = ApplicationSigning.create

let private inProcessFixture =
    fixtureOver "in-process (secret-store keyed)" Attribution inProcessProvider conforming

let private keyManagedFixture =
    fixtureOver "key-managed (external key holder)" IsolatedSigner keyManagedProvider conforming

// ── the probe ──────────────────────────────────────────────────────────

let private probeTests =
    testList "provider-conformance probe" [

        // The control's control. While this fails, everything below
        // proves nothing — a pack that fails every provider "flags" the
        // broken ones for free.
        testCase "a conforming provider PASSES the conformance pack cleanly"
        <| fun _ ->
            let failures =
                ISigningProviderConformance.tests inProcessFixture |> failingCaseNames

            Expect.isEmpty
                failures
                ("A conforming provider must pass the pack cleanly; while it does not, the defect "
                 + "assertions below are vacuous. Failing cases: "
                 + string failures)

        // One test per defect, each naming the case it must trip.
        // Asserting the SPECIFIC case is what stops this degrading into
        // "something failed" if the pack later breaks for an unrelated
        // reason. Other cases may also fail — a defect is not obliged to
        // be detectable in exactly one way — but the named one must.
        for defect in PerturbedApplicationSigner.SigningDefect.all do
            testCase $"the pack REJECTS a broken provider — %s{PerturbedApplicationSigner.SigningDefect.name defect}"
            <| fun _ ->
                let expected = PerturbedApplicationSigner.SigningDefect.expectedFailingCase defect

                let failures =
                    fixtureOver
                        $"perturbed ({PerturbedApplicationSigner.SigningDefect.name defect})"
                        Attribution
                        inProcessProvider
                        (PerturbedApplicationSigner.create defect)
                    |> ISigningProviderConformance.tests
                    |> failingCaseNames

                Expect.contains
                    failures
                    expected
                    ($"The conformance pack must reject a provider whose defect is '%s{PerturbedApplicationSigner.SigningDefect.name defect}' "
                     + $"at the case '%s{expected}'. Cases that did fail: %A{failures}")
    ]

[<Tests>]
let tests =
    testList "Application signing — provider conformance" [
        ISigningProviderConformance.tests inProcessFixture
        ISigningProviderConformance.tests keyManagedFixture
        probeTests
    ]