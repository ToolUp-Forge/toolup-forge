module ToolUp.Platform.PeerBearerConfigValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Secrets

// ─── Phase 37 — peer-bearer config preflight ─────────────────────────
//
// Warns when `ServerConfig.PeerRoutePrefixes` is non-empty but the
// `ISecretStore` has no entries under the `peers/` prefix. That
// combination ships a deployment that advertises peer-routable
// endpoints but cannot actually authenticate any peer: every request
// will land on `RejectionReason.NoSecretConfigured` and return 401.
// Likely causes are a forgotten `SetSecret` step in deployment
// bootstrap or a typo on the peer name when the operator seeded the
// secret manually.
//
// **Warning, not Error.** The aggregator continues startup on
// `Warning`. The deployment is functional in the narrow sense (the
// middleware is correctly rejecting unauthenticated calls); the
// operator just hasn't finished setup. Refusing startup would block
// a legitimate "stand up the routes first, seed secrets later"
// workflow. The warning surfaces in admin diagnostics so the gap is
// visible.
//
// **Listing semantics.** `ISecretStore.ListKeys` is the canonical way
// to enumerate per-scope keys. Implementations that don't support
// listing (env-var-only stores) return an empty list — the validator
// then reports `Warning` even though secrets may exist, because the
// substrate cannot prove they do. Operators on read-only stores opt
// out by leaving `PeerRoutePrefixes` empty or by validating their
// secrets through their own bootstrap tooling.

/// Phase 37 — warn when peer routes are registered with no
/// corresponding peer secrets configured.
type PeerBearerConfigValidator(config: ServerConfig, secretStore: ISecretStore, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "peer-bearer-config"
        member _.Timeout = timeout

        member _.Validate() = async {
            if List.isEmpty config.PeerRoutePrefixes then
                return Ok
            else
                let! keys = secretStore.ListKeys PeerBearerAuthMiddleware.SecretStoreScope
                let hasPeerSecret = keys |> List.exists (fun k -> k.StartsWith "peers/")

                if hasPeerSecret then
                    return Ok
                else
                    return
                        Warning(
                            sprintf
                                "ServerConfig.PeerRoutePrefixes contains %d prefix(es) but ISecretStore has no entries under '_platform' scope matching 'peers/{peerName}/bearer'. Every peer request will be rejected with 401 (RejectionReason.NoSecretConfigured) until at least one peer bearer is seeded via `secretStore.SetSecret(\"_platform\", $\"peers/{peerName}/bearer\", token)`. If your secret store does not support ListKeys (env-var-only stores), this warning is expected and can be acknowledged at the deployment-bootstrap level."
                                config.PeerRoutePrefixes.Length
                        )
        }