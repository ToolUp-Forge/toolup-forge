module ToolUp.Cloud.Parity.Tests.EmulatorSeamCoverageTests

open Expecto
open Microsoft.FSharp.Reflection

// ─── Phase 193 — the emulator-seam ratchet ────────────────────────────
//
// Two legs of the matrix are dark not because the emulator is missing but
// because the COMPANION has no way to be pointed at one (see the comments
// in `EmulatorLegs`). A dark leg reports a `pending` case, and pending
// output is easy to stop reading — so the gap would sit there indefinitely,
// and worse, a companion could GAIN the seam and nobody would notice the
// leg was still switched off.
//
// These tests are the ratchet. They run everywhere, need no emulator and no
// Docker, and read the companions' config records by reflection to assert
// the CURRENT seam coverage. Each is written to fail — loudly, with
// instructions — the moment the state it characterises changes:
//
//   * A companion that HAS a seam must keep it, or the leg depending on it
//     breaks and the reason would otherwise be a puzzle.
//   * A companion that LACKS one must either still lack it, or the leg gets
//     armed. These assertions are NOT an endorsement of the gap; they are
//     what makes closing it impossible to do silently.
//
// This is the same discipline as the divergence fixture: a matrix cell that
// cannot fail is not measuring anything.

let private fieldNames<'T> () =
    FSharpType.GetRecordFields typeof<'T> |> Array.map _.Name |> Array.toList

/// Does this config record expose any field that could redirect the SDK
/// client at a local emulator? Matched by intent rather than exact name, so
/// `EndpointUrl` / `ServiceEndpoint` / `BaseUri` / `EmulatorHost` all count
/// — the ratchet should fire on the capability landing, whatever it is named.
let private hasEndpointSeam<'T> () =
    fieldNames<'T> ()
    |> List.exists (fun name ->
        let lowered = name.ToLowerInvariant()

        lowered.Contains "endpoint"
        || lowered.Contains "emulator"
        || lowered.Contains "baseuri"
        || lowered.Contains "serviceurl")

[<Tests>]
let tests =
    testList "Cloud parity — emulator-seam coverage" [

        testCase "AwsS3StorageConfig HAS an endpoint seam (the LocalStack blob leg depends on it)"
        <| fun _ ->
            Expect.isTrue
                (hasEndpointSeam<ToolUp.Storage.AwsS3Storage.AwsS3StorageConfig> ())
                ("The S3 companion's endpoint override is what lets the LocalStack leg run at all. "
                 + "If it has been removed or renamed beyond recognition, arm the leg differently in "
                 + "EmulatorLegs.blobStorageFactory rather than leaving it silently skipped. Fields: "
                 + string (fieldNames<ToolUp.Storage.AwsS3Storage.AwsS3StorageConfig> ()))

        testCase "GoogleCloudStorageConfig still has NO endpoint seam — the fake-gcs leg stays dark"
        <| fun _ ->
            Expect.isFalse
                (hasEndpointSeam<ToolUp.Storage.GoogleCloudStorage.GoogleCloudStorageConfig> ())
                ("GoogleCloudStorageConfig has GAINED an endpoint/emulator seam. That is the change the "
                 + "fake-gcs-server leg was waiting for: arm it in EmulatorLegs.blobStorageFactory (the "
                 + "FakeGcs branch, replacing the NoCompanionSeam error), add the fake-gcs service to the "
                 + "compose matrix if it is not already there, and delete this test. Leaving it as-is "
                 + "means GCP is the one cloud with no emulator-backed parity coverage while the SDK "
                 + "claims 'the same image everywhere'.")

        testCase "AwsSecretsManagerConfig still has NO endpoint seam — the secrets leg needs the SDK env var"
        <| fun _ ->
            Expect.isFalse
                (hasEndpointSeam<ToolUp.Secrets.AwsSecretsManager.AwsSecretsManagerConfig> ())
                ("AwsSecretsManagerConfig has GAINED an endpoint seam. The LocalStack ISecretStore leg "
                 + "currently reaches the emulator only via the AWS SDK's AWS_ENDPOINT_URL_SECRETS_MANAGER "
                 + "resolution, and guards hard against that variable being absent because the fallback is "
                 + "the REAL Secrets Manager. Switch the leg to the explicit config field — it removes that "
                 + "footgun entirely — then delete this test.")
    ]