// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module KnowledgeBase.ServerOriginalSourceResolver

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open SharedTypes

// ─── Phase 104 — source-kind-aware original resolution ───────────
//
// "Fetch the original" must be honest about the fact that not every
// knowledge source *has* a binary original. `KnowledgeSource` is
// `UploadedFile | FromNarrative | Note` — only `UploadedFile` carries a
// true binary original; a `Note`'s canonical form is its raw markdown
// at `knowledge/{docId}/note.md`; narrative chunks are synthetic
// (module-generated — their home is the producing module's page, via
// `NarrativeDocSource.PageRoute`, not a downloadable file). The
// resolver makes absence explicit and typed (`None`), never a
// guessed-at missing blob or a thrown exception (GP 9).
//
// ── Serving note (Phase 119) ──────────────────────────────────────
// Consumers that serve `OriginalDocument.Content` over HTTP (a download
// / preview endpoint) MUST set `Content-Disposition: attachment` for
// inline-renderable types. KB uploads accept arbitrary user content, and
// csv / md / html / svg originals carry active markup — served inline
// (`Content-Disposition: inline`, or no header on a browser-sniffed
// type) they execute in the deployment's origin: a stored-XSS vector.
// Force a download (`attachment`) and pin the `Content-Type` from
// `OriginalDocument.ContentType` rather than letting the browser sniff.
// See `docs/knowledge-base/concepts.md` (Serving originals safely).
//
// ── Preview seam (Phase 200) ──────────────────────────────────────
// This resolver answers "what are the original's bytes". It does not
// answer "where in the original was this cited" — that join lives in
// `IOriginalPreviewSeam.fs`, which composes THIS interface and pairs a
// resolved original with a neutral `PreviewAnchor` projected from the
// citation's `SourceLocator`. The seam is opt-in and off this file's
// path entirely: nothing below changed for it, and a deployment that
// never composes a preview seam runs the pre-200 code byte-for-byte
// (GP 13). A deployment swapping THIS resolver automatically changes
// what the preview seam resolves too — one resolution rule, not two.

// ── Metadata-only resolution (Phase 108) ──────────────────────────
// Phase 200 recorded a limitation it could not fix from where it sat:
// signed-URL delivery makes the RESPONSE byte-light, but the resolver
// still downloaded the whole original server-side just to establish
// that it exists. `ResolveMetadata` is the fix it named — the same
// per-`KnowledgeSource` branch, answering "does this have an original,
// and WHERE is it" without reading a byte of content. Signing needs
// exactly that: a blob name to sign and the metadata a viewer picks a
// renderer from.

/// Where a document's original lives and what it is, without its
/// bytes. `BlobName` is the key within the caller's scope container —
/// the handle a signed-URL mint (Phase 108) needs, and the one place
/// the per-`KnowledgeSource` naming convention is derived.
type OriginalLocation = {
    /// Blob key within the scope container (e.g.
    /// `knowledge/{docId}/{filename}`). `None` when the resolver can
    /// describe the original but cannot name one signable blob for it
    /// — a synthesised or multi-part original, or a custom resolver
    /// delegating to `locationViaResolve`. Signed-URL delivery then
    /// falls back to proxying, which is always correct.
    BlobName: string option
    /// Original file name, as `OriginalDocument.FileName`.
    FileName: string
    /// MIME content type, as `OriginalDocument.ContentType`.
    ContentType: string
    /// Size of the stored original in bytes.
    SizeBytes: int64
}

/// Resolve the original document behind a KB index entry, branching on
/// the entry's `KnowledgeSource`. Returns `None` when the source kind
/// has no retrievable original (or the backing blob is gone) — the
/// Phase 102 handler maps `None` onto the typed `NoOriginalAvailable`
/// result. Implementations are stateless between calls and receive
/// the storage handle + container per invocation, so one singleton
/// serves every scope without capturing per-request state.
type IOriginalSourceResolver =
    abstract Resolve:
        storage: IBlobStorage * container: string * doc: KnowledgeDocument -> Async<OriginalDocument option>

    /// Phase 108 — the same resolution decision, answered from metadata
    /// alone. MUST agree with `Resolve` on presence: `None` here exactly
    /// when `Resolve` would return `None`, so a caller that branches on
    /// this cannot serve a link to something `Resolve` calls absent.
    ///
    /// A custom resolver with no cheap metadata path delegates to
    /// `OriginalSourceResolver.locationViaResolve` — correct (it simply
    /// downloads), and honest about the fact that it is not cheap.
    abstract ResolveMetadata:
        storage: IBlobStorage * container: string * doc: KnowledgeDocument -> Async<OriginalLocation option>

/// MIME content type for a KB `FileType` extension. Unknown extensions
/// degrade to `application/octet-stream` (a safe download disposition)
/// rather than failing the fetch.
let contentTypeFor (fileType: string) : string =
    match (if isNull fileType then "" else fileType.ToLowerInvariant()) with
    | "pdf" -> "application/pdf"
    | "pptx" -> "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    | "docx" -> "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
    | "xlsx" -> "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    | "csv" -> "text/csv"
    | "txt" -> "text/plain"
    | "md"
    | "note" -> "text/markdown"
    | _ -> "application/octet-stream"

/// Default `IOriginalSourceResolver`, one branch per `KnowledgeSource`:
///
///   * `UploadedFile` — download the raw blob persisted at upload
///     (`knowledge/{docId}/{filename}`) and return it under the
///     extension-derived content type.
///   * `Note` — return the note's canonical markdown body
///     (`knowledge/{docId}/note.md`) as `text/markdown`.
///   * `FromNarrative` — `None`. Narratives are synthetic module
///     output; the rendered-markdown blob the commit path persists is
///     an ingestion convenience, not a user-facing original (the
///     original "document" is the producing module's live page).
///
/// A download failure (blob deleted out-of-band, storage error) also
/// resolves to `None` — absence over exception, per the interface
/// contract.
type DefaultOriginalSourceResolver() =
    interface IOriginalSourceResolver with
        member _.Resolve(storage, container, doc) = async {
            let downloadAs (blobName: string) (contentType: string) = async {
                let! result = storage.Download(container, blobName)

                match result with
                | Ok bytes ->
                    return
                        Some {
                            FileName = doc.FileName
                            ContentType = contentType
                            SizeBytes = int64 bytes.Length
                            Content = bytes
                        }
                | Error _ -> return None
            }

            match doc.Source with
            | UploadedFile ->
                return! downloadAs (sprintf "knowledge/%s/%s" doc.Id doc.FileName) (contentTypeFor doc.FileType)
            | Note _ -> return! downloadAs (sprintf "knowledge/%s/note.md" doc.Id) "text/markdown"
            | FromNarrative _ -> return None
        }

        member _.ResolveMetadata(storage, container, doc) = async {
            // `GetMetadata` reads the blob's properties only — no
            // content transfer — so this branch is O(1) in the
            // original's size where `Resolve` is O(n). A missing blob
            // resolves to `None`, exactly as a failed download does in
            // `Resolve`: presence must agree between the two.
            let describe (blobName: string) (contentType: string) = async {
                let! result = storage.GetMetadata(container, blobName)

                match result with
                | Ok meta ->
                    return
                        Some {
                            BlobName = Some blobName
                            FileName = doc.FileName
                            ContentType = contentType
                            SizeBytes = meta.Size
                        }
                | Error _ -> return None
            }

            match doc.Source with
            | UploadedFile ->
                return! describe (sprintf "knowledge/%s/%s" doc.Id doc.FileName) (contentTypeFor doc.FileType)
            | Note _ -> return! describe (sprintf "knowledge/%s/note.md" doc.Id) "text/markdown"
            | FromNarrative _ -> return None
        }

/// Phase 108 — shared `ResolveMetadata` fallback for a custom
/// `IOriginalSourceResolver` with no cheap metadata path. Correct
/// against the "presence must agree with `Resolve`" contract, but NOT
/// cheap: it downloads the original and describes what it got. The
/// returned `BlobName` is `None`, so a caller that would have minted a
/// signed URL falls back to proxying rather than signing a blob this
/// helper cannot name.
///
/// Adopting the Phase 108 member on an existing custom resolver is
/// therefore a one-liner:
///
///     member this.ResolveMetadata(storage, container, doc) =
///         OriginalSourceResolver.locationViaResolve this storage container doc
let locationViaResolve
    (resolver: IOriginalSourceResolver)
    (storage: IBlobStorage)
    (container: string)
    (doc: KnowledgeDocument)
    : Async<OriginalLocation option> =
    async {
        let! resolved = resolver.Resolve(storage, container, doc)

        return
            resolved
            |> Option.map (fun original -> {
                BlobName = None
                FileName = original.FileName
                ContentType = original.ContentType
                SizeBytes = original.SizeBytes
            })
    }

/// Construct the default resolver. Deployments swap it via
/// `withOriginalSourceResolver` (or by registering an
/// `IOriginalSourceResolver` singleton before compose).
let createDefault () : IOriginalSourceResolver = DefaultOriginalSourceResolver() :> _

/// Register a custom `IOriginalSourceResolver` so a deployment can
/// extend original resolution to a custom source kind (or rewire the
/// built-in branches). Threads the singleton registration through the
/// shared `ComposeExtensions.ServiceConfig` seam — the same pattern as
/// `ServerApp.withCspContributor` — so `AIServerApp` / `RAGServerApp`
/// inherit it via their `Base` without a per-wrapper forwarder. Apps
/// that never call this get `DefaultOriginalSourceResolver` (GP 11).
let withOriginalSourceResolver (resolver: IOriginalSourceResolver) (app: ServerApp) : ServerApp =
    let register (s: IServiceCollection) =
        s.AddSingleton<IOriginalSourceResolver>(resolver)

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