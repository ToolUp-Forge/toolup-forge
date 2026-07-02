// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module KnowledgeBase.ServerUploadPolicy

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open SharedTypes

// ─── Phase 119 — KB upload-policy preflight ────────────────────────
//
// Warns at startup when the Knowledge Base runs in Team / MultiTeam mode
// with no `MaxUploadBytes` cap: each upload's full `byte[]` is held in
// memory through Remoting + extraction, so an unbounded cap is a
// per-tenant memory-exhaustion lever in a shared deployment. Mirrors the
// `AcceptSharedEmbeddingCacheInTeamMode` convention — an explicit
// `AcceptUnboundedUploads` opt-out silences the warning. `Warning` only,
// never `Error`, so it never aborts startup (GP 11).

type private UploadPolicyValidator(serverConfig: ServerConfig, policy: KnowledgeUploadPolicy) =
    interface IConfigValidator with
        member _.Name = "knowledge-base:upload-policy"
        member _.Timeout = IConfigValidator.defaultTimeout

        member _.Validate() = async {
            let teamScoped = DeploymentConfig.hasTeamScope serverConfig

            if KnowledgeUploadPolicy.warnsUncappedInTeamMode teamScoped policy then
                return
                    ValidationResult.Warning(
                        "Knowledge Base upload policy sets no MaxUploadBytes in Team / MultiTeam mode. "
                        + "Each upload's full byte[] is held in memory through Remoting + extraction, so an "
                        + "uncapped upload is a per-tenant memory-exhaustion lever. Set a cap via "
                        + "KnowledgeBase.Server.withUploadPolicy { ... MaxUploadBytes = Some <bytes> }, or accept "
                        + "unbounded uploads deliberately and silence this warning with AcceptUnboundedUploads = true."
                    )
            else
                return ValidationResult.Ok
        }

/// Compose-time Knowledge Base upload policy (Phase 119): size cap +
/// extension allowlist + unsupported-type handling, on top of the
/// always-on filename sanitisation the upload boundary applies
/// regardless. Registers the policy as a DI singleton (read per request
/// by `KnowledgeApiDeps.resolve`) plus a startup preflight validator that
/// emits a `Warning` on an uncapped Team / MultiTeam deployment. Threads
/// through the shared `ComposeExtensions.ServiceConfig` seam (the same
/// pattern as `withOriginalSourceResolver`) so `AIServerApp` /
/// `RAGServerApp` inherit it via their `Base`. Apps that never call this
/// get `KnowledgeUploadPolicy.permissive` — no caps, pre-119 behaviour
/// (GP 11 / GP 13).
let withUploadPolicy (policy: KnowledgeUploadPolicy) (app: ServerApp) : ServerApp =
    let register (s: IServiceCollection) =
        s.AddSingleton<KnowledgeUploadPolicy>(policy)

    let withSingleton = {
        app with
            Extensions = {
                app.Extensions with
                    ServiceConfig =
                        match app.Extensions.ServiceConfig with
                        | None -> Some register
                        | Some baseFn -> Some(fun s -> register (baseFn s))
            }
    }

    ServerApp.withConfigValidator (UploadPolicyValidator(app.Config, policy) :> IConfigValidator) withSingleton

/// Phase 14x — compose-time lever for KB upload content-hash dedup.
/// Dedup is ON by default (no call needed): `UploadDocument` SHA-256-
/// hashes the raw uploaded bytes and a scope-local match returns the
/// existing document — no re-chunk, no re-embed, no duplicate retrieval
/// hits — emitting a `KnowledgeDocumentDeduplicated` audit row and an
/// Info `SystemMessage` to the uploader. Call `withDocumentDedup false`
/// when byte-identical re-uploads must legitimately stay separate
/// documents (e.g. contract revisions where each upload is its own
/// record); the opt-out restores the pre-14x upload path byte-for-byte
/// (GP 11 — no hash computed, no ref written). Threads through the same
/// `ComposeExtensions.ServiceConfig` seam as `withUploadPolicy`, so
/// `AIServerApp` / `RAGServerApp` inherit it via their `Base`.
let withDocumentDedup (enabled: bool) (app: ServerApp) : ServerApp =
    let policy: KnowledgeDedupPolicy = { DedupUploads = enabled }

    let register (s: IServiceCollection) =
        s.AddSingleton<KnowledgeDedupPolicy>(policy)

    {
        app with
            Extensions = {
                app.Extensions with
                    ServiceConfig =
                        match app.Extensions.ServiceConfig with
                        | None -> Some register
                        | Some baseFn -> Some(fun s -> register (baseFn s))
            }
    }