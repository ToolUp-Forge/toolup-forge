module ToolUp.Cloud.Parity.Tests.EmulatorSeamCoverageTests

open Expecto
open Microsoft.FSharp.Reflection

// ─── Phase 193 — the emulator-seam ratchet ────────────────────────────
//
// When Phase 193 shipped, two legs of the matrix were dark not because the
// emulator was missing but because the COMPANION had no way to be pointed
// at one. A dark leg reports a `pending` case, and pending output is easy
// to stop reading — so the gap would have sat there indefinitely, and
// worse, a companion could have GAINED the seam with nobody noticing the
// leg was still switched off.
//
// These tests are the ratchet that stopped that. They run everywhere, need
// no emulator and no Docker, and read the companions' config records by
// reflection to assert the CURRENT seam coverage. Each is written to fail —
// loudly, with instructions — the moment the state it characterises changes:
//
//   * A companion that HAS a seam must keep it, or the leg depending on it
//     breaks and the reason would otherwise be a puzzle.
//   * A companion that LACKS one must either still lack it, or the leg gets
//     armed. Those assertions were NOT an endorsement of the gap; they were
//     what made closing it impossible to do silently.
//
// **Both gaps are now CLOSED (2026-08-26, tidy-drain).**
// `GoogleCloudStorageConfig` and `AwsSecretsManagerConfig` each gained an
// `EndpointUrl`, mirroring `AwsS3StorageConfig`, and both legs are armed in
// `EmulatorLegs`. The two "still has NO seam" assertions therefore INVERT
// rather than being deleted: the ratchet's job did not end when the seam
// landed, it changed direction. A companion that quietly drops the field
// again would otherwise switch its leg back off in silence, which is the
// exact failure this file exists to prevent.
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

        testCase "GoogleCloudStorageConfig HAS an endpoint seam (the fake-gcs blob + audit legs depend on it)"
        <| fun _ ->
            Expect.isTrue
                (hasEndpointSeam<ToolUp.Storage.GoogleCloudStorage.GoogleCloudStorageConfig> ())
                ("GoogleCloudStorageConfig has LOST its endpoint/emulator seam. It is what arms the "
                 + "fake-gcs-server legs of both the IBlobStorage and IAuditSink packs — removing it makes "
                 + "GCP the one cloud with no emulator-backed parity coverage while the SDK claims 'the "
                 + "same image everywhere'. Restore the field, or arm the leg differently in "
                 + "EmulatorLegs.blobStorageFactory rather than leaving it silently skipped. Fields: "
                 + string (fieldNames<ToolUp.Storage.GoogleCloudStorage.GoogleCloudStorageConfig> ()))

        testCase "AwsSecretsManagerConfig HAS an endpoint seam (the LocalStack secrets leg depends on it)"
        <| fun _ ->
            Expect.isTrue
                (hasEndpointSeam<ToolUp.Secrets.AwsSecretsManager.AwsSecretsManagerConfig> ())
                ("AwsSecretsManagerConfig has LOST its endpoint seam. Without it the LocalStack ISecretStore "
                 + "leg can reach the emulator only via the AWS SDK's AWS_ENDPOINT_URL_SECRETS_MANAGER "
                 + "resolution, whose fallback when the variable is absent is the REAL Secrets Manager — the "
                 + "footgun the explicit field removed. Restore it. Fields: "
                 + string (fieldNames<ToolUp.Secrets.AwsSecretsManager.AwsSecretsManagerConfig> ()))
    ]