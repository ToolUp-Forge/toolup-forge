module ToolUp.Platform.Tests.InProcess.ShareTokenSigningKeyProvenanceValidatorTests

open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Wave 19 — share-token signing-key provenance preflight ──────────
//
// Fires when the HMAC signing key would be auto-generated (absent from
// ISecretStore) in a production / multi-instance share-token deployment,
// so the security-critical key is not invisible to backup / rotation
// governance. Single-instance / non-public, or a key already
// provisioned, is silent.
//
// **Phase 460 raised the unacknowledged production-shaped arm from
// `Warning` to `Error`** — a public or multi-replica deployment no
// longer boots onto a key nobody provisioned. The two cases below that
// read `Warning` before now read `Error`; the acknowledgement path, the
// auto-generated-key-in-force path and the provenance probe are pinned
// in `ShareTokenSigningKeyGovernanceTests`, which owns the full ladder.
// This pack keeps its original scope: the SHAPE gate (which deployments
// the validator speaks to at all), which Phase 460 did not touch.

/// Minimal in-memory ISecretStore keyed by (scopeId, key). Seed via the
/// constructor to model "key already provisioned".
type private SeedableSecretStore(seed: (string * string * string) list) =
    let store = ConcurrentDictionary<string * string, string>()

    do
        for scopeId, key, value in seed do
            store[(scopeId, key)] <- value

    interface Secrets.ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Result.Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Result.Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

/// Config with a live share-token surface unless overridden.
let private cfg
    (surfaces: SurfaceProfile list)
    (shareTokenStore: ShareTokenStoreMode)
    (replicaCount: int)
    (publicBaseUrl: string option)
    =
    {
        ServerConfig.defaults with
            Surfaces = surfaces
            ShareTokenStore = shareTokenStore
            ReplicaCount = replicaCount
            PublicBaseUrl = publicBaseUrl
    }

let private emptyStore () : Secrets.ISecretStore =
    SeedableSecretStore([]) :> Secrets.ISecretStore

let private seededStore () : Secrets.ISecretStore =
    SeedableSecretStore(
        [
            ShareTokenStore.platformContainer,
            ShareTokenStore.signingKeySecretName,
            "c2VlZGVkLWtleS1iYXNlNjR1cmwtMzItYnl0ZXMtZXhhbXBsZQ"
        ]
    )
    :> Secrets.ISecretStore

let private validate (config: ServerConfig) (store: Secrets.ISecretStore) : ValidationResult =
    let v =
        ShareTokenSigningKeyProvenanceValidator.ShareTokenSigningKeyProvenanceValidator(config, store)
        :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Wave 19 — share-token signing-key provenance validator" [

        test "Surface off (NoShareTokenStore) + key absent → Ok (validator self-gates)" {
            let config =
                cfg Surfaces.individual NoShareTokenStore 3 (Some "https://app.example.com")

            Expect.equal (validate config (emptyStore ())) Ok "no share-token surface → silent"
        }

        test "Surface live + single instance, non-public + key absent → Ok (auto-gen is fine for dev)" {
            let config = cfg Surfaces.individual EnabledShareTokenStore 1 None
            Expect.equal (validate config (emptyStore ())) Ok "single-instance non-public → silent"
        }

        test "Surface live + ReplicaCount > 1 + key absent → Error (Phase 460: was Warning)" {
            let config = cfg Surfaces.individual EnabledShareTokenStore 3 None
            let result = validate config (emptyStore ())

            match result with
            | Error msg ->
                Expect.stringContains msg ShareTokenStore.signingKeySecretName "names the unmanaged signing-key secret"
                Expect.stringContains msg "pre-provision" "names the fix"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Surface live + PublicBaseUrl set (inferred scale-out) + key absent → Error (Phase 460: was Warning)" {
            let config =
                cfg Surfaces.individual EnabledShareTokenStore 1 (Some "https://app.example.com")

            match validate config (emptyStore ()) with
            | Error _ -> ()
            | other -> failtestf "expected Error, got %A" other
        }

        test "Surface live + production shape + key already provisioned → Ok" {
            let config =
                cfg Surfaces.individual EnabledShareTokenStore 3 (Some "https://app.example.com")

            Expect.equal (validate config (seededStore ())) Ok "operator-managed key → silent"
        }

        test "Validator name is stable + non-empty" {
            let v =
                ShareTokenSigningKeyProvenanceValidator.ShareTokenSigningKeyProvenanceValidator(
                    cfg Surfaces.anonymous NoShareTokenStore 1 None,
                    emptyStore ()
                )
                :> IConfigValidator

            Expect.equal v.Name "share-token-signing-key-provenance" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]