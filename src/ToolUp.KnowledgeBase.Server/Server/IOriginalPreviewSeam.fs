// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module KnowledgeBase.ServerOriginalPreviewSeam

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerOriginalSourceResolver

// ─── Phase 200 — anchor-highlighting original-document preview ───
//
// Phase 102 returns the original's bytes. Phase 104 decides which
// source kinds *have* an original. Phase 106 stamps a citation with a
// `SourceLocator` saying where in the original the answer came from.
// Between them a consumer had everything except the one thing it
// actually wanted: "open this document at that spot, highlighted".
// Building that meant re-deriving the join by hand, in every consumer.
//
// `IOriginalPreviewSeam` is that join, and nothing more. Given a
// document and a citation's locator it returns a `PreviewTarget`: the
// content reference (inline bytes, or a time-bound signed URL) plus a
// neutral `PreviewAnchor` the consumer's viewer reads.
//
// What this seam deliberately is NOT:
//
//   * A viewer. The SDK ships no PDF / PPTX / HTML renderer and never
//     will — which renderer to use is a consumer choice (GP 1). The
//     anchor contract is documented on `PreviewAnchor` so any viewer
//     can consume it.
//   * On the Phase 102 retrieval path. `getOriginalDocument` does not
//     call this and is byte-for-byte unchanged; a deployment that never
//     composes a seam registers nothing, allocates nothing, and runs
//     the pre-200 code (GP 11 / GP 13).
//   * An existence oracle. Scope resolution goes through
//     `previewOriginal`, which refuses an out-of-scope id with exactly
//     the `NotInScope` an unknown id gets (GP 4) — the same
//     indistinguishability `getOriginalDocument` maintains.

/// Project a `(document, citation locator)` pair onto the typed
/// `PreviewTarget` a consumer's viewer opens. Implementations are
/// stateless between calls and receive the storage handle + container
/// per invocation, so one singleton serves every scope without
/// capturing per-request state (the same contract
/// `IOriginalSourceResolver` holds).
///
/// Refusals are results, never exceptions (GP 9):
/// `NoOriginalAvailable` when the document's source kind has no
/// retrievable original (or its blob is gone), `OriginalRetrievalFailed`
/// for an operational failure.
type IOriginalPreviewSeam =
    abstract Preview:
        storage: IBlobStorage * container: string * doc: KnowledgeDocument * locator: SourceLocator option ->
            Async<Result<PreviewTarget, KnowledgeBaseError>>

/// Deployment-supplied issuer of time-bound preview URLs, for the
/// byte-light delivery mode. The SDK ships no signer: minting a URL
/// needs the deployment's storage backend, its public host, and its
/// signing key, none of which the KB tier owns (GP 1). A deployment
/// wiring one typically delegates to the same machinery its media tier
/// uses (`IMediaLibrary.SignedUrl`, Phase 88).
///
/// `Error` carries a diagnostic message and surfaces to the caller as
/// `OriginalRetrievalFailed` — a signer that cannot mint a URL must not
/// silently fall back to inlining megabytes of bytes the deployment
/// deliberately chose to keep out of the response.
type IPreviewUrlSigner =
    abstract Sign: doc: KnowledgeDocument * container: string * ttl: TimeSpan -> Async<Result<string, string>>

/// Tuning for the signed-URL delivery mode. `Ttl` bounds how long an
/// issued URL stays valid; `Now` is the clock the expiry is stamped
/// from (injectable so the TTL bound is testable without sleeping).
type PreviewSignedUrlOptions = {
    /// Lifetime of an issued preview URL. Must be strictly positive.
    Ttl: TimeSpan
    /// Clock seam — defaults to `DateTimeOffset.UtcNow`.
    Now: unit -> DateTimeOffset
}

module PreviewSignedUrlOptions =
    /// One hour, matching the `IMediaLibrary` `SignedUrlDefaultTtl`
    /// default (Phase 88) so a deployment running both tiers does not
    /// have to reconcile two different link lifetimes.
    let defaults: PreviewSignedUrlOptions = {
        Ttl = TimeSpan.FromHours 1.0
        Now = fun () -> DateTimeOffset.UtcNow
    }

/// Default seam: resolve the original through the composed
/// `IOriginalSourceResolver` (so every Phase 104 branch — and any
/// deployment-custom branch layered over it — is honoured exactly once)
/// and pair it with the projected anchor. Inline delivery.
type DefaultOriginalPreviewSeam(resolver: IOriginalSourceResolver) =
    interface IOriginalPreviewSeam with
        member _.Preview(storage, container, doc, locator) = async {
            try
                let! resolved = resolver.Resolve(storage, container, doc)

                match resolved with
                | None -> return Error NoOriginalAvailable
                | Some original -> return Ok(PreviewTarget.ofOriginal doc.Id (PreviewAnchor.ofLocator locator) original)
            with ex ->
                return Error(OriginalRetrievalFailed ex.Message)
        }

/// Byte-light seam: same resolution and the same anchor, but the bytes
/// are replaced by a time-bound URL from the deployment's signer.
///
/// The resolver still runs — it is what establishes that the document
/// *has* an original and what its content type and size are, and it is
/// the single place the per-`KnowledgeSource` branching lives. So this
/// mode makes the **response** byte-light, not the server-side read; a
/// metadata-only resolution path would mean widening
/// `IOriginalSourceResolver`, which is a larger change than this seam
/// should carry (tracked as a tidy-up, not a silent compromise).
type SignedUrlOriginalPreviewSeam
    (resolver: IOriginalSourceResolver, signer: IPreviewUrlSigner, options: PreviewSignedUrlOptions) =

    do
        if options.Ttl <= TimeSpan.Zero then
            invalidArg
                "options"
                "PreviewSignedUrlOptions.Ttl must be strictly positive — a non-positive TTL mints links that are already expired."

    interface IOriginalPreviewSeam with
        member _.Preview(storage, container, doc, locator) = async {
            try
                let! resolved = resolver.Resolve(storage, container, doc)

                match resolved with
                | None -> return Error NoOriginalAvailable
                | Some original ->
                    let! signed = signer.Sign(doc, container, options.Ttl)

                    match signed with
                    | Error reason -> return Error(OriginalRetrievalFailed reason)
                    | Ok url ->
                        let expiresAt = options.Now().Add options.Ttl

                        return
                            Ok(
                                PreviewTarget.withSignedUrl
                                    doc.Id
                                    (PreviewAnchor.ofLocator locator)
                                    original
                                    url
                                    expiresAt
                            )
            with ex ->
                return Error(OriginalRetrievalFailed ex.Message)
        }

/// Phase 108 — signed-URL delivery minted from the composed
/// `IBlobStorage` itself, with a transparent fall back to proxying.
///
/// This is the seam `SignedUrlOriginalPreviewSeam` above wanted and
/// could not have: Phase 200 had to take an `IPreviewUrlSigner` from
/// the deployment because the SDK had no way to ask a blob backend for
/// a URL. Phase 108's `ISignedUrlBlobStorage` capability is that way,
/// so a deployment on Azure / S3 / GCS composes ONE option and writes
/// no signer.
///
/// Three outcomes, and the middle one is the whole point:
///
///   * the backend signed          → `PreviewContent.SignedUrl`, and
///                                   the bytes never touch the app tier.
///   * the backend cannot sign     → INLINE delivery, silently. This is
///                                   the local-filesystem answer, and
///                                   an encrypted-at-rest deployment's
///                                   too: `EncryptedBlobStorage` wraps
///                                   the backend and does not implement
///                                   the capability, so the probe fails
///                                   and nobody is ever handed a URL to
///                                   ciphertext. Structural, not a rule
///                                   somebody has to remember.
///   * signing failed operationally → `OriginalRetrievalFailed`. NOT a
///                                   fallback: a deployment that chose
///                                   to keep megabytes out of its
///                                   responses does not want them
///                                   silently reinstated by a transient
///                                   storage fault.
///
/// **Byte-light on BOTH sides.** Resolution goes through
/// `ResolveMetadata` (Phase 108), so establishing that the original
/// exists costs a properties read rather than a full download —
/// closing the limitation Phase 200 recorded and could only describe.
/// The inline fallback path pays the download, because inline delivery
/// needs the bytes anyway.
///
/// **Scope check precedes minting (GP 4).** A signed URL is a bearer
/// token — whoever holds it reads the object until it expires. Nothing
/// in this type resolves a scope: `previewOriginal` (and the KB API
/// handler) resolve the caller's container and refuse an out-of-scope
/// id with `NotInScope` BEFORE `Preview` is ever entered, so the mint
/// is unreachable without a passing gate. Deliberately not defence in
/// depth here — one gate, upstream, in the one place that knows the
/// caller.
type BlobSignedUrlOriginalPreviewSeam(resolver: IOriginalSourceResolver, options: PreviewSignedUrlOptions) =

    do
        if options.Ttl <= TimeSpan.Zero then
            invalidArg
                "options"
                "PreviewSignedUrlOptions.Ttl must be strictly positive — a non-positive TTL mints links that are already expired."

    /// Inline delivery, byte-for-byte the `DefaultOriginalPreviewSeam`
    /// path — reached whenever the backend cannot mint.
    let inlineFallback (storage: IBlobStorage) container doc locator = async {
        let! resolved = resolver.Resolve(storage, container, doc)

        match resolved with
        | None -> return Error NoOriginalAvailable
        | Some original -> return Ok(PreviewTarget.ofOriginal doc.Id (PreviewAnchor.ofLocator locator) original)
    }

    interface IOriginalPreviewSeam with
        member _.Preview(storage, container, doc, locator) = async {
            try
                let! located = resolver.ResolveMetadata(storage, container, doc)

                match located with
                | None -> return Error NoOriginalAvailable
                | Some location ->
                    let! minted =
                        match location.BlobName with
                        // A resolver that cannot name one signable blob
                        // (`locationViaResolve`, a synthesised original)
                        // takes the same route as an unsigning backend.
                        | None -> async.Return(Ok None)
                        | Some blobName -> BlobStorage.trySignedUrl storage container blobName options.Ttl

                    match minted with
                    | Error reason -> return Error(OriginalRetrievalFailed reason)
                    | Ok None -> return! inlineFallback storage container doc locator
                    | Ok(Some url) ->
                        let expiresAt = options.Now().Add options.Ttl

                        // `PreviewTarget.withSignedUrl` reads only the
                        // metadata fields off its `original` and drops
                        // `Content` — so the empty array below is never
                        // observable. Going through the smart
                        // constructor rather than building the record
                        // by hand is what keeps the top-level metadata
                        // and the delivery mode from drifting.
                        let metadataOnly: OriginalDocument = {
                            FileName = location.FileName
                            ContentType = location.ContentType
                            SizeBytes = location.SizeBytes
                            Content = Array.empty
                        }

                        return
                            Ok(
                                PreviewTarget.withSignedUrl
                                    doc.Id
                                    (PreviewAnchor.ofLocator locator)
                                    metadataOnly
                                    url
                                    expiresAt
                            )
            with ex ->
                return Error(OriginalRetrievalFailed ex.Message)
        }

/// Construct the inline-delivery seam over a resolver (typically
/// `OriginalSourceResolver.createDefault ()`, or whatever the
/// deployment composed).
let createDefault (resolver: IOriginalSourceResolver) : IOriginalPreviewSeam =
    DefaultOriginalPreviewSeam(resolver) :> _

/// Phase 108 — construct the blob-backed signed-URL seam over a
/// resolver. Raises at **construction** (compose time, not on a request
/// path) if the TTL is not strictly positive.
let createBlobSignedUrl (resolver: IOriginalSourceResolver) (options: PreviewSignedUrlOptions) : IOriginalPreviewSeam =
    BlobSignedUrlOriginalPreviewSeam(resolver, options) :> _

/// Construct the signed-URL-delivery seam. Raises at **construction**
/// (compose time, not on a request path) if the TTL is not strictly
/// positive — a misconfigured lifetime is a deployment defect that
/// should fail the boot, not a per-request refusal.
let createSignedUrl
    (resolver: IOriginalSourceResolver)
    (signer: IPreviewUrlSigner)
    (options: PreviewSignedUrlOptions)
    : IOriginalPreviewSeam =
    SignedUrlOriginalPreviewSeam(resolver, signer, options) :> _

/// Scope-gated entry point: resolve `docId` within the caller's
/// container and preview it. An id that is not in this scope's index —
/// whether it belongs to another team or does not exist at all —
/// refuses with `NotInScope`, identical in both cases so the surface is
/// not an existence oracle (GP 4). Storage failures surface as
/// `OriginalRetrievalFailed`, never as an exception (GP 9).
let previewOriginal
    (seam: IOriginalPreviewSeam)
    (storage: IBlobStorage)
    (container: string)
    (docId: string)
    (locator: SourceLocator option)
    : Async<Result<PreviewTarget, KnowledgeBaseError>> =
    async {
        try
            let! index = loadIndex storage container

            match index |> List.tryFind (fun d -> d.Id = docId) with
            | None -> return Error NotInScope
            | Some doc -> return! seam.Preview(storage, container, doc, locator)
        with ex ->
            return Error(OriginalRetrievalFailed ex.Message)
    }

/// Register an `IOriginalPreviewSeam` so a deployment can serve
/// open-at-and-highlight previews of KB originals. Threads the
/// singleton registration through the shared
/// `ComposeExtensions.ServiceConfig` seam — the same pattern as
/// `withOriginalSourceResolver` — so `AIServerApp` / `RAGServerApp`
/// inherit it via their `Base` without a per-wrapper forwarder.
///
/// **Opt-in and zero-cost (GP 13):** an app that never calls this
/// registers nothing. There is no default seam in DI, no hosted
/// service, and no change to `getOriginalDocument` — the pre-200
/// original-retrieval path is byte-for-byte what it was.
let withOriginalPreviewSeam (seam: IOriginalPreviewSeam) (app: ServerApp) : ServerApp =
    let register (s: IServiceCollection) =
        s.AddSingleton<IOriginalPreviewSeam>(seam)

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

/// Phase 108 — opt into time-bound direct-download URLs for originals.
/// One call, no signer to write: the seam mints through whatever
/// `IBlobStorage` the deployment composed, and falls back to proxying
/// the bytes on any backend that cannot sign (local filesystem,
/// encrypted-at-rest, in-memory) — so composing this is safe even on a
/// deployment that will not benefit from it.
///
/// **Default off, and off means unchanged (GP 11 / GP 13).** An app
/// that never calls this registers no seam; `GetOriginalDocument` is
/// the byte-for-byte Phase 102 path either way, and
/// `GetOriginalDelivery` reports `PreviewContent.Inline` over exactly
/// that path. Composing this changes what `GetOriginalDelivery` returns
/// on a signing backend and nothing else.
///
/// Uses the default per-`KnowledgeSource` resolver. A deployment with a
/// custom `IOriginalSourceResolver` composes
/// `withOriginalPreviewSeam (createBlobSignedUrl myResolver options)`
/// instead, so its own resolution branches (and its own
/// `ResolveMetadata`) are the ones that run.
let withSignedOriginalUrls (options: PreviewSignedUrlOptions) (app: ServerApp) : ServerApp =
    withOriginalPreviewSeam
        (createBlobSignedUrl (KnowledgeBase.ServerOriginalSourceResolver.createDefault ()) options)
        app