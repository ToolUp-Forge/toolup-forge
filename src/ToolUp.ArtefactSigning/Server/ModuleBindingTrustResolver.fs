// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System
open ToolUp.Platform
open ToolUp.Platform.Secrets

// Aliased (not opened) so this module's `Result` `Ok`/`Error` are not
// shadowed by `ValidationResult.Ok`/`Error`.
module CV = ToolUp.Platform.ConfigValidation

// ─── Phase 170 — trust-anchor config → verifier (compose-time) ──────────
//
// Turns the Core-pure `ModuleBindingTrustConfig` (anchor *descriptions*
// from `fromEnv`) into the value-typed `ModuleBindingAnchor` set the
// `DefaultModuleBindingVerifier` is built over. Symmetric key material is
// resolved through `ISecretStore` here, at compose time — never read from
// plaintext config — so one container image is configured for binding by
// environment + secret-store alone. The companion validator
// (`ModuleBindingTrustValidator`) runs the same resolution at preflight and
// fails startup **closed** (a named gap) if a configured symmetric anchor's
// secret does not resolve, rather than silently disabling binding.

module ModuleBindingTrustResolver =

    let private decodeBase64 (label: string) (s: string) : Result<byte[], string> =
        try
            Ok(Convert.FromBase64String(s.Trim()))
        with _ ->
            Error(sprintf "%s is not valid base64" label)

    let private parseAlgorithm (s: string) : Result<SigningAlgorithm, string> =
        match SigningAlgorithm.tryParse s with
        | Some a -> Ok a
        | None -> Error(sprintf "unknown algorithm '%s' (expected EcdsaP256/ES256 or Ed25519/EdDSA)" s)

    /// Resolve one anchor description into a crypto-bearing anchor. The only
    /// I/O is the symmetric secret lookup; the asymmetric path is pure.
    let private resolveAnchor
        (secrets: ISecretStore)
        (anchorRef: ModuleBindingAnchorRef)
        : Async<Result<ModuleBindingAnchor, string>> =
        async {
            match anchorRef with
            | AsymmetricAnchorRef(keyId, alg, publicKeyBase64) ->
                match parseAlgorithm alg, decodeBase64 (sprintf "anchor '%s' public key" keyId) publicKeyBase64 with
                | Ok a, Ok pk -> return Ok(AsymmetricAnchor(keyId, a, pk))
                | Error e, _
                | _, Error e -> return Error(sprintf "anchor '%s': %s" keyId e)
            | SymmetricAnchorRef(keyId, scope, key) ->
                let! secret = secrets.GetSecret(scope, key)

                match secret with
                | None ->
                    return
                        Error(
                            sprintf
                                "anchor '%s': symmetric secret '%s/%s' did not resolve via ISecretStore"
                                keyId
                                scope
                                key
                        )
                | Some b64 ->
                    match decodeBase64 (sprintf "anchor '%s' MAC key" keyId) b64 with
                    | Ok bytes -> return Ok(SymmetricAnchor(keyId, bytes))
                    | Error e -> return Error(sprintf "anchor '%s': %s" keyId e)
        }

    /// Build an `IModuleBindingVerifier` from the trust config, resolving
    /// every anchor (symmetric secrets via `secrets`). Fails closed: any
    /// unresolvable anchor returns `Error` rather than a partial verifier.
    /// When `AllowUnbound = false` the result additionally refuses an
    /// unstamped module (the base verifier admits `None` stamps).
    let resolve
        (config: ModuleBindingTrustConfig)
        (secrets: ISecretStore)
        : Async<Result<IModuleBindingVerifier, string>> =
        async {
            let! results = config.Anchors |> List.map (resolveAnchor secrets) |> Async.Sequential

            let errors =
                results
                |> Array.choose (function
                    | Error e -> Some e
                    | Ok _ -> None)
                |> Array.toList

            match errors with
            | _ :: _ -> return Error(String.concat "; " errors)
            | [] ->
                let anchors =
                    results
                    |> Array.choose (function
                        | Ok a -> Some a
                        | Error _ -> None)
                    |> Array.toList

                let baseVerifier = DefaultModuleBindingVerifier.create anchors

                let verifier =
                    if config.AllowUnbound then
                        baseVerifier
                    else
                        { new IModuleBindingVerifier with
                            member _.Verify(moduleId, stamp) =
                                match stamp with
                                | None ->
                                    Rejected
                                        "module presents no binding stamp and this deployment requires every module to be bound (AllowUnbound = false)"
                                | Some _ -> baseVerifier.Verify(moduleId, stamp)
                        }

                return Ok verifier
        }

/// Preflight validator (Phase 9m `IConfigValidator`) that fails startup
/// closed if a configured module-binding anchor cannot be resolved — a
/// named gap rather than a silent disable. No anchors configured ⇒ `Ok`
/// (binding off, nothing to validate).
type ModuleBindingTrustValidator(config: ModuleBindingTrustConfig, secrets: ISecretStore, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout CV.IConfigValidator.defaultTimeout

    interface CV.IConfigValidator with
        member _.Name = "module-binding-trust"
        member _.Timeout = timeout

        member _.Validate() = async {
            match config.Anchors with
            | [] -> return CV.ValidationResult.Ok
            | _ ->
                let! resolved = ModuleBindingTrustResolver.resolve config secrets

                match resolved with
                | Ok _ -> return CV.ValidationResult.Ok
                | Error e ->
                    return CV.ValidationResult.Error(sprintf "module-binding trust anchors did not resolve: %s" e)
        }