// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module KnowledgeBase.ServerUploadPolicy

open System
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.ConfigValidation
open SharedTypes
open KnowledgeBase.ServerIndexStorage

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

/// Phase 510 — compose-time lever for KB upload **versioning**. OFF by
/// default: call `withDocumentVersioning true` and a re-upload of an
/// EDITED file under a name the caller's scope already holds becomes the
/// next version of that document — same document id, `Version + 1`, the
/// prior version preserved as an immutable `KnowledgeDocumentVersion`
/// with its original bytes copied aside, only the chunks whose content
/// actually changed re-embedded, and any trailing chunks a shorter new
/// version left behind deleted from every retrieval index through
/// `IIndexLifecycle`.
///
/// Without it the pre-510 behaviour is byte-for-byte intact (GP 11): the
/// re-upload lands as a second, unrelated document, no predecessor
/// lookup runs, and no version or chunk-hash sidecar is written. The
/// default is OFF rather than ON precisely because versioning changes
/// what a re-upload MEANS — see `KnowledgeVersioningPolicy` for why a
/// corpus that wants every revision as its own record is a legitimate
/// shape we must not silently collapse.
///
/// Composes with `withDocumentDedup` rather than replacing it: a
/// byte-identical re-upload still dedups (nothing changed, so there is
/// no version to mint) and only a byte-different one supersedes.
/// Threads through the same `ComposeExtensions.ServiceConfig` seam as
/// `withUploadPolicy`, so `AIServerApp` / `RAGServerApp` inherit it via
/// their `Base`.
let withDocumentVersioning (enabled: bool) (app: ServerApp) : ServerApp =
    let policy: KnowledgeVersioningPolicy = { VersionUploads = enabled }

    let register (s: IServiceCollection) =
        s.AddSingleton<KnowledgeVersioningPolicy>(policy)

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

/// Phase 105 — compose-time lever for routing KB **original documents**
/// through `IDataObjectStore` rather than writing them as raw blobs at
/// the `knowledge/{docId}/{fileName}` convention path. OFF by default.
///
/// With it, `UploadDocument` saves the original through
/// `IDataObjectStore.Save(scopeId, docId, …, Versioned)` — content is
/// deduplicated by SHA-256 at rest within the scope, the envelope
/// carries file name / type / uploader / content hash, and the object
/// is matched by the Phase 9h `Erase` surface because `Save` records
/// `createdBy`. Original retrieval reads the store first and falls back
/// to the convention blob, so documents uploaded before the opt-in stay
/// retrievable with no backfill step; deletion removes both.
///
/// **`Versioned`, never `StrictlyVersioned`.** The store's policy is
/// sticky for the object's lifetime and `StrictlyVersioned` makes
/// `Delete` return `DeleteForbidden` — which would break the KB delete
/// cascade outright. Retention that must survive deletion is a
/// deployment-level policy, not a KB default.
///
/// **Why this is opt-in when the other substrate hooks are probes.**
/// `IDataObjectStore` is registered for every composed deployment by
/// `ComposeRuntimeServices`, so an "is one registered?" probe would
/// flip every existing deployment onto the new retention path on
/// upgrade rather than leaving it byte-for-byte (GP 11). Without this
/// call the upload / read / delete paths take no store call at all
/// (GP 13).
let withObjectStoreRetention (enabled: bool) (app: ServerApp) : ServerApp =
    let policy: KnowledgeObjectRetentionPolicy = {
        RetainOriginalsInObjectStore = enabled
    }

    let register (s: IServiceCollection) =
        s.AddSingleton<KnowledgeObjectRetentionPolicy>(policy)

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

// ─── Phase 512 — per-scope quota + retention compose hooks ───────────

/// Append a service registration onto the shared `ComposeExtensions`
/// seam — the same threading `withUploadPolicy` / `withDocumentDedup`
/// use, factored out now that four hooks share it.
let private withServiceConfig (register: IServiceCollection -> IServiceCollection) (app: ServerApp) : ServerApp = {
    app with
        Extensions = {
            app.Extensions with
                ServiceConfig =
                    match app.Extensions.ServiceConfig with
                    | None -> Some register
                    | Some baseFn -> Some(fun s -> register (baseFn s))
        }
}

// ─── Quota preflight ─────────────────────────────────────────────────

type private QuotaPolicyValidator(serverConfig: ServerConfig, policy: KnowledgeQuotaPolicy) =
    interface IConfigValidator with
        member _.Name = "knowledge-base:quota-policy"
        member _.Timeout = IConfigValidator.defaultTimeout

        member _.Validate() = async {
            let teamScoped = DeploymentConfig.hasTeamScope serverConfig

            if teamScoped && KnowledgeQuotaPolicy.isUnlimited policy then
                return
                    ValidationResult.Warning(
                        "Knowledge Base quota policy sets neither MaxDocuments nor MaxBytes in Team / MultiTeam mode. "
                        + "A single team can then grow its document corpus and vector index without bound, and the only "
                        + "backstop is the per-file MaxUploadBytes cap. Set a corpus cap via "
                        + "KnowledgeBase.Server.withKnowledgeQuota { MaxDocuments = Some <n>; MaxBytes = Some <bytes> }."
                    )
            else
                return ValidationResult.Ok
        }

/// Phase 512 — compose-time per-scope Knowledge Base **corpus** quota:
/// a document-count ceiling and a total-bytes ceiling, enforced at the
/// upload boundary before anything is persisted and surfaced through
/// `KnowledgeApi.GetScopeUsage` (plus the `/dev/knowledge-base/usage`
/// endpoint below when `ServerConfig.EnableDevEndpoints` is on).
/// Complements `withUploadPolicy`, which caps ONE upload; this caps the
/// accumulated corpus.
///
/// Registers the policy as a DI singleton (read per request by
/// `KnowledgeApiDeps.resolve`) plus a preflight validator that warns on
/// an uncapped Team / MultiTeam deployment — the same `Warning`-never-
/// `Error` posture the upload-policy validator takes, so it can never
/// abort startup (GP 11).
///
/// Apps that never call this get `KnowledgeQuotaPolicy.unlimited`: no
/// usage tally is taken on the upload path and no upload can be refused
/// for quota — pre-512 behaviour byte-for-byte (GP 11 / GP 13).
let withKnowledgeQuota (policy: KnowledgeQuotaPolicy) (app: ServerApp) : ServerApp =
    let withSingleton =
        app |> withServiceConfig (fun s -> s.AddSingleton<KnowledgeQuotaPolicy>(policy))

    ServerApp.withConfigValidator (QuotaPolicyValidator(app.Config, policy) :> IConfigValidator) withSingleton

// ─── Dev usage endpoint (`/dev/knowledge-base/usage`) ────────────────

let private usageJsonOptions =
    let o = ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()
    o.WriteIndented <- true
    o

/// Resolve the caller's KB container the same way `KnowledgeApiDeps`
/// does — a middleware-resolved `ToolUp.StorageScope` first, then a
/// per-user fallback (GP 4: the container comes from the resolver, never
/// from the caller, so this endpoint cannot report another tenant's
/// usage no matter what is asked for).
let private devUsageContainer (ctx: HttpContext) : string =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> s.Container
    | _ ->
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> sprintf "user-%s" id
        | _ -> "user-anonymous"

let private devUsageHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let storage =
            match ctx.RequestServices.GetService typeof<IBlobStorage> with
            | :? IBlobStorage as s -> Some s
            | _ -> None

        let policy =
            match ctx.RequestServices.GetService typeof<KnowledgeQuotaPolicy> with
            | :? KnowledgeQuotaPolicy as p -> p
            | _ -> KnowledgeQuotaPolicy.unlimited

        let container = devUsageContainer ctx

        let! usage =
            match storage with
            | Some s ->
                async {
                    let! docs = loadIndex s container
                    return KnowledgeScopeUsage.ofDocuments policy docs
                }
                |> Async.StartAsTask
            | None -> System.Threading.Tasks.Task.FromResult KnowledgeScopeUsage.empty

        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.Headers["Cache-Control"] <- "no-store"

        do!
            ctx.Response.WriteAsync(
                System.Text.Json.JsonSerializer.Serialize(
                    {|
                        container = container
                        usage = usage
                    |},
                    usageJsonOptions
                )
            )

        return! next ctx
    }

/// Phase 512 — mount the read-only `/dev/knowledge-base/usage`
/// diagnostics endpoint reporting the caller's scope usage against the
/// composed quota. Gated on `ServerConfig.EnableDevEndpoints` (default
/// `false`) exactly like `/dev/inspect` — a deployment that has not
/// opted into dev endpoints registers no handler at all and gets the
/// ordinary 404 (GP 13). Composed automatically by
/// `withKnowledgeQuotaAndUsageEndpoint`; exposed separately for
/// deployments that want the endpoint without a quota (usage reporting
/// against an unlimited policy is still useful).
let withKnowledgeUsageEndpoint (app: ServerApp) : ServerApp =
    if app.Config.EnableDevEndpoints then
        {
            app with
                Extensions = {
                    app.Extensions with
                        Handlers =
                            app.Extensions.Handlers
                            @ [ Giraffe.Routing.route "/dev/knowledge-base/usage" >=> devUsageHandler ]
                }
        }
    else
        app

/// Phase 512 — the common pairing: compose the quota AND mount its dev
/// usage endpoint (the latter still gated on `EnableDevEndpoints`).
let withKnowledgeQuotaAndUsageEndpoint (policy: KnowledgeQuotaPolicy) (app: ServerApp) : ServerApp =
    app |> withKnowledgeQuota policy |> withKnowledgeUsageEndpoint

// ─── Retention sweep ─────────────────────────────────────────────────

/// Phase 512 — compose-time per-scope age-based retention. Registers the
/// policy as a DI singleton and, when it can actually expire something,
/// schedules the `knowledge-base.retention-sweep` job on the composed
/// `IJobScheduler` for each scope in `scopes`.
///
/// **`scopes` is explicit, and deliberately so.** `IBlobStorage` has no
/// cross-container enumeration and the SDK does not enumerate tenants
/// automatically (see `ScheduledJobDeclaration.Scopes`), so the sweep
/// cannot discover the containers it should visit. Pass the same list
/// `recoverStuckDocumentsAtStartup` takes — typically `ITeamStore`
/// results plus any `_platform` / `_deployment` containers the
/// deployment uses. An empty list schedules nothing, which is honest:
/// silently defaulting to `_platform` would look composed while sweeping
/// a container that holds no documents.
///
/// **`retainForever` registers nothing.** An inert policy (`MaxAge =
/// None`) short-circuits before any hosted service or scheduler entry is
/// created, so a deployment that never calls this — or calls it with the
/// default — is byte-for-byte its pre-512 self (GP 11 / GP 13).
///
/// Registration is deferred to `IHostedService.StartAsync` via
/// `DeferredScheduledJobDeclaration` (Phase 623): the handler resolves
/// `IBlobStorage` / `IIndexLifecycle` / `IAuditLog` from the built
/// provider on every run, so nothing is captured at compose time and the
/// handler stays stateless between invocations (GP 12 rule 4).
let withKnowledgeRetention (policy: KnowledgeRetentionPolicy) (scopes: string list) (app: ServerApp) : ServerApp =
    let register (s: IServiceCollection) =
        let withPolicy = s.AddSingleton<KnowledgeRetentionPolicy>(policy)

        if KnowledgeRetentionPolicy.isInert policy || List.isEmpty scopes then
            withPolicy
        else
            withPolicy.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                Func<IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                    DeferredScheduledJobDeclaration.hostedService
                        "Knowledge Base retention sweep"
                        [
                            DeferredScheduledJobDeclaration.create (fun provider ->
                                ScheduledJobDeclaration.create
                                    KnowledgeBase.ServerRetentionSweep.SweepHandlerName
                                    (KnowledgeBase.ServerRetentionSweep.RetentionSweepJobHandler(provider, policy)
                                    :> IJobHandler)
                                    (Trigger.CronTrigger policy.SweepSchedule)
                                |> ScheduledJobDeclaration.withScopes scopes)
                        ]
                        sp)
            )

    app |> withServiceConfig register

// ─── Phase 515 — content scanning at the upload boundary ─────────────

type private ContentScanValidator(scanner: IContentScanner, policy: ContentScanPolicy) =
    interface IConfigValidator with
        member _.Name = "knowledge-base:content-scanning"
        member _.Timeout = IConfigValidator.defaultTimeout

        member _.Validate() = async {
            match policy.OnScanError with
            | FailClosedOnScanError -> return ValidationResult.Ok
            | FailOpenOnScanError ->
                return
                    ValidationResult.Warning(
                        sprintf
                            "Knowledge Base content scanning is composed with '%s' but set to FAIL OPEN. When the scanner is unreachable or errors, uploads are admitted UNSCANNED and the audit row records Refused = false. That is a legitimate posture for defence-in-depth behind another control; if it is not deliberate, compose ContentScanPolicy.failClosed instead."
                            scanner.Name
                    )
        }

/// Phase 515 — compose-time content scanning at the KB upload boundary.
/// Registers the scanner and its error policy as DI singletons (read per
/// request by `KnowledgeApiDeps.resolve`), plus a preflight validator
/// that warns when the deployment has chosen to fail open.
///
/// The scan runs after the cheap pure policy checks (size cap,
/// allowlist, filename sanitisation — no point streaming a payload to a
/// scanner that will be refused anyway) and **before** dedup, the corpus
/// quota, versioning and persistence. That ordering is the point of the
/// phase: a rejected payload never reaches the blob container, never
/// consumes a byte of the scope's quota, and can never be admitted on
/// the strength of an earlier scan by an older signature set.
///
/// Apps that never call this resolve `ContentScanner = None`: no scan,
/// no hash computed for a scan audit row, no network call, no policy
/// read — the pre-515 upload path byte-for-byte (GP 11 / GP 13).
let withContentScanning (scanner: IContentScanner) (policy: ContentScanPolicy) (app: ServerApp) : ServerApp =
    let withSingletons =
        app
        |> withServiceConfig (fun s -> s.AddSingleton<IContentScanner>(scanner).AddSingleton<ContentScanPolicy>(policy))

    ServerApp.withConfigValidator (ContentScanValidator(scanner, policy) :> IConfigValidator) withSingletons