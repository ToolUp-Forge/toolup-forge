// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.SigningKeyHandler

open Microsoft.AspNetCore.Http
open Giraffe
open ToolUp.Platform.Secrets
open ToolUp.ArtefactSigning

// ─── Phase 40 — public verification-key endpoint ───────────────────────
//
//   GET /_platform/signing-key/{keyId}
//
// Returns the public verification key for `keyId` in PEM + JWK form so a
// relying party can validate an `ArtefactSignature` independently. The
// route is **anonymous by design** — verifying parties (auditors,
// downstream systems, partner deployments) need to fetch the public key
// without holding a deployment credential. Only the public component is
// served; the private key never leaves `ISecretStore`.
//
// Because the handler resolves the key by id directly from
// `ISecretStore`, it serves rotated-out keys too: a signature produced
// under `signing-v1` remains verifiable after `signing-v2` becomes the
// active signing key, as long as the `signing/v1` blob survives.
//
// **Anonymous-route exemption.** Mount this handler ahead of any auth /
// surface-enforcement middleware, or register the path on the SDK's
// anonymous-route allowlist, so verifying parties reach it without a
// session.

[<Literal>]
let RoutePrefix = "/_platform/signing-key/"

let private signingKeyHandler (keyId: string) : HttpHandler =
    fun _next (ctx: HttpContext) -> task {
        match ctx.RequestServices.GetService(typeof<ISecretStore>) with
        | :? ISecretStore as secrets ->
            let! stored = SigningKeyMaterial.tryLoad secrets keyId |> Async.StartImmediateAsTask

            match stored with
            | Some key ->
                let meta = SigningKeyMaterial.metadata keyId key
                ctx.Response.StatusCode <- 200

                return!
                    ctx.WriteJsonAsync {|
                        keyId = meta.KeyId
                        alg = SigningAlgorithm.jwsAlg meta.Algorithm
                        algorithm = SigningAlgorithm.name meta.Algorithm
                        pem = meta.Pem
                        jwk = meta.Jwk
                    |}
            | None ->
                ctx.Response.StatusCode <- 404
                return! ctx.WriteTextAsync $"signing key '{keyId}' not found"
        | _ ->
            ctx.Response.StatusCode <- 500
            return! ctx.WriteTextAsync "artefact-signing: no ISecretStore registered"
    }

/// Giraffe routes for the signing-key endpoint. Compose into the
/// deployment's anonymous-route group:
///   `choose [ SigningKeyHandler.routes; ...existing... ]`
let routes: HttpHandler =
    GET >=> routef "/_platform/signing-key/%s" signingKeyHandler