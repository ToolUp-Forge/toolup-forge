module ToolUp.Cloud.Parity.Tests.BlobStorageParityTests

open Expecto
open ToolUp.Platform.Tests.Contracts
open ToolUp.Cloud.Parity.Tests.EmulatorLegs

// ─── Phase 193 — IBlobStorage row of the parity matrix ────────────────
//
// The SAME `IBlobStorageContract` pack that `ToolUp.Platform.Tests` runs
// against the local and live implementations, bound here once per cloud
// emulator. Nothing in this file asserts anything itself — that is the
// design. Parity means every cloud is measured by one set of assertions,
// so the only per-cloud code is how the provider is constructed.
//
// A leg with no emulator reports one `pending` case naming the reason, so
// a fresh checkout is green and a CI log distinguishes "not configured"
// from "cannot be configured".

let private legTests leg =
    match blobStorageFactory leg with
    | Ok factory -> IBlobStorageContract.tests $"%s{CloudLeg.name leg} — IBlobStorage" factory
    | Error skip ->
        testList $"%s{CloudLeg.name leg} — IBlobStorage" [ ptestCase (LegSkip.describe skip) <| fun _ -> () ]

[<Tests>]
let tests =
    testList "Cloud parity — IBlobStorage" (CloudLeg.all |> List.map legTests)