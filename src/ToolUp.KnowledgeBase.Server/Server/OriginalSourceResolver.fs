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