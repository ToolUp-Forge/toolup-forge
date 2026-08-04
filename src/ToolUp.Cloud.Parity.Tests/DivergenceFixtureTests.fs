module ToolUp.Cloud.Parity.Tests.DivergenceFixtureTests

open Expecto
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts
open ToolUp.Cloud.Parity.Tests.DivergentBlobStorage

// ─── Phase 193 — the divergence fixture (the matrix's control) ─────────
//
// Every leg above can legitimately report "skipped", so on a machine with
// no emulators the parity rows say nothing either way. That is the right
// behaviour for a fresh checkout and a bad property for a gate, because
// "the matrix found no divergence" and "the matrix cannot detect
// divergence" produce the same green tick.
//
// This fixture closes that hole and needs no emulator, so it runs on every
// checkout and in every CI job. It drives the SAME shared contract pack —
// the one the emulator legs use — over a provider perturbed in one known
// way, and asserts the pack FAILS it, at the specific case that models
// that divergence.
//
// It is deliberately two-sided. Asserting only that a broken provider
// fails would pass just as well if the harness failed everything, so the
// first test asserts the unperturbed double PASSES the same pack in full.
// Together they show the fixture discriminates, rather than merely
// reporting what was expected.

/// Run a contract pack in-process and return the leaf names of the cases
/// that did not pass. `evalTestsSilent` evaluates without printing, so a
/// deliberately-failing pack does not spray failure output into a green run.
let private failingCaseNames (pack: Test) =
    Impl.evalTestsSilent pack
    |> Async.RunSynchronously
    |> List.filter (fun (_, summary) -> not summary.result.isPassed)
    |> List.map (fun (flat, _) -> flat.name |> List.last)

let private contractFor (factory: unit -> IBlobStorage) =
    IBlobStorageContract.tests "divergence-fixture" factory

[<Tests>]
let tests =
    testList "Cloud parity — divergence fixture" [

        // The control's control. If this ever fails, the fixture below is
        // worthless regardless of what it reports, so it is asserted first
        // and its failure message says as much.
        testCase "the unperturbed in-memory double PASSES the shared IBlobStorage pack"
        <| fun _ ->
            let failures =
                contractFor (fun () -> InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage)
                |> failingCaseNames

            Expect.isEmpty
                failures
                ("A conforming provider must pass the shared pack cleanly. While this fails, the "
                 + "divergence assertions below prove nothing — every provider would 'fail' the matrix, "
                 + "including the real ones. Failing cases: "
                 + string failures)

        // One test per divergence class, each naming the case it must trip.
        // Asserting the SPECIFIC case (not merely "something failed") is
        // what stops this degrading into a tautology if the pack later
        // starts failing the fixture for an unrelated reason.
        for divergence in Divergence.all do
            testCase $"the matrix FLAGS a diverging provider — %s{Divergence.name divergence}"
            <| fun _ ->
                let expectedCase = Divergence.expectedFailingCase divergence

                let failures =
                    contractFor (fun () -> DivergentBlobStorage divergence :> IBlobStorage)
                    |> failingCaseNames

                Expect.isNonEmpty
                    failures
                    ($"The shared pack passed a provider that diverges on '%s{Divergence.name divergence}'. "
                     + "The parity matrix has no teeth for this divergence class — a real cloud companion "
                     + "could ship the same defect and the emulator legs would report green.")

                Expect.contains
                    failures
                    expectedCase
                    ($"Expected the '%s{expectedCase}' case to catch this divergence. It failed other "
                     + $"cases (%A{failures}) but not that one, so either the perturbation no longer "
                     + "models the divergence it claims to, or that contract case stopped asserting it.")
    ]