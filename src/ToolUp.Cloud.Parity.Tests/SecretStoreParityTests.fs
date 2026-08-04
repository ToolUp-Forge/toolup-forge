module ToolUp.Cloud.Parity.Tests.SecretStoreParityTests

open Expecto
open ToolUp.Platform.Tests.Contracts
open ToolUp.Cloud.Parity.Tests.EmulatorLegs

// ─── Phase 193 — ISecretStore row of the parity matrix ────────────────
//
// This row is deliberately the thinnest, and the reason is a finding
// rather than an omission: of the three clouds, only AWS has a
// secret-manager emulator. LocalStack emulates Secrets Manager; Azurite
// emulates Blob / Queue / Table and there is no Key Vault emulator; and
// fake-gcs-server emulates Cloud Storage with no Secret Manager
// counterpart. So `ISecretStore` cannot be brought to full emulator-backed
// parity by any arrangement of these three emulators, and the honest
// matrix says so per cell rather than implying three-cloud coverage.
//
// Azure and GCP `ISecretStore` conformance therefore stays where it
// already is: the env-gated live-account bindings in `ToolUp.Platform.Tests`
// against the same shared pack. Same assertions, different trigger.

let private legTests leg =
    match secretStoreFactory leg with
    | Ok factory -> ISecretStoreContract.tests $"%s{CloudLeg.name leg} — ISecretStore" factory
    | Error skip ->
        testList $"%s{CloudLeg.name leg} — ISecretStore" [ ptestCase (LegSkip.describe skip) <| fun _ -> () ]

[<Tests>]
let tests =
    testList "Cloud parity — ISecretStore" (CloudLeg.all |> List.map legTests)