module ToolUp.Platform.Tests.InProcess.GoogleCloudStorageTests

open System
open System.Security.Cryptography
open Expecto
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

// ─── Env-gated ───────────────────────────────────────────────────────
//
// Runs the `IBlobStorage` contract pack against a real GCS bucket when
// `TOOLUP_GCS_BUCKET` is set. Credentials resolve via the GCS SDK's
// Application Default Credentials chain (env `GOOGLE_APPLICATION_CREDENTIALS`,
// gcloud user login, GCE metadata server, GKE workload identity) — or
// via `TOOLUP_GCS_CREDENTIALS_JSON` for inline JSON.
//
// Contract tests use GUID-suffixed scope identifiers internally so
// a shared bucket across runs doesn't leak keys between tests.

let private contractTests =
    match Environment.GetEnvironmentVariable "TOOLUP_GCS_BUCKET" with
    | null
    | "" -> testList "contract" [ ptestCase "skipped — TOOLUP_GCS_BUCKET not set" <| fun _ -> () ]
    | _ ->
        let factory () =
            match ToolUp.Storage.GoogleCloudStorage.fromEnv () with
            | Some s -> s
            | None -> failwith "GoogleCloudStorage.fromEnv returned None despite bucket env var being set"

        IBlobStorageContract.tests "contract" factory

// ─── Phase 1037 — credential JSON through CredentialFactory ──────────
//
// Always on: loading a credential issues no request. The companion moved
// off the deprecated `GoogleCredential.FromJson`, which accepted every
// credential type the library knows; these pin that each of those five
// still loads to the same underlying credential, and that any other type
// is refused by name rather than narrowed silently.

let private serviceAccountJson () =
    use rsa = RSA.Create 2048
    let pem = rsa.ExportPkcs8PrivateKeyPem().Replace("\n", "\\n")

    sprintf
        """{"type":"service_account","project_id":"p","private_key_id":"k","private_key":"%s","client_email":"sa@p.iam.gserviceaccount.com","client_id":"1","token_uri":"https://oauth2.googleapis.com/token"}"""
        pem

let private credentialSamples: (string * (unit -> string) * string) list = [
    "service_account", serviceAccountJson, "ServiceAccountCredential"
    "authorized_user",
    (fun () -> """{"type":"authorized_user","client_id":"c","client_secret":"s","refresh_token":"r"}"""),
    "UserCredential"
    "external_account",
    (fun () ->
        """{"type":"external_account","audience":"//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/q","subject_token_type":"urn:ietf:params:oauth:token-type:jwt","token_url":"https://sts.googleapis.com/v1/token","credential_source":{"file":"token.jwt"}}"""),
    "FileSourcedExternalAccountCredential"
    "impersonated_service_account",
    (fun () ->
        """{"type":"impersonated_service_account","service_account_impersonation_url":"https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/sa@p.iam.gserviceaccount.com:generateAccessToken","source_credentials":{"type":"authorized_user","client_id":"c","client_secret":"s","refresh_token":"r"},"delegates":[]}"""),
    "ImpersonatedCredential"
    "external_account_authorized_user",
    (fun () ->
        """{"type":"external_account_authorized_user","audience":"//iam.googleapis.com/locations/global/workforcePools/p/providers/q","client_id":"c","client_secret":"s","refresh_token":"r","token_url":"https://sts.googleapis.com/v1/oauthtoken","token_info_url":"https://sts.googleapis.com/v1/introspect"}"""),
    "ExternalAccountAuthorizedUserCredential"
]

let private credentialTests =
    testList "credential JSON (Phase 1037)" [
        for credentialType, json, expected in credentialSamples do
            test (sprintf "a %s credential loads as before" credentialType) {
                let credential = ToolUp.Storage.GoogleCloudStorage.credentialFromJson (json ())

                Expect.equal
                    (credential.UnderlyingCredential.GetType().Name)
                    expected
                    "every credential type the deprecated loader accepted must load to the same underlying credential"
            }

        test "a credential type outside the accepted five is refused by name" {
            let refusal =
                Expect.throwsC
                    (fun () ->
                        ToolUp.Storage.GoogleCloudStorage.credentialFromJson """{"type":"gdch_service_account"}"""
                        |> ignore)
                    id

            Expect.stringContains refusal.Message "gdch_service_account" "the refusal names the declared type"
            Expect.stringContains refusal.Message "service_account," "the refusal names what is accepted"
        }
    ]

let tests = testList "GoogleCloudStorage" [ credentialTests; contractTests ]