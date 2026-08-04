// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module KnowledgeBase.ServerOriginalRedactor

open System
open System.Text
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open SharedTypes

// ─── Phase 201 — redaction-aware original retrieval ───────────────
//
// Phase 41 shipped a data-classification substrate that redacts
// classified FIELDS on the structured read path. Phase 102/104 shipped
// original-document retrieval, which returns the ingested bytes once the
// caller passes the *scope* check. Nothing joined the two: a caller
// in-scope for a document but lacking the per-level reader capability
// could read, verbatim from the original, exactly the spans the field
// gate would have masked in the structured view. The scope gate is not
// the classification gate, and until this phase the original path had
// only the former.
//
// `IOriginalRedactor` is that join. Given a resolved `OriginalDocument`
// and the caller's `AccessContext` it returns either the bytes to
// deliver — classified spans masked to the Phase 41
// `ClassificationGate.RedactedPlaceholder` — or a refusal. The
// `ClassificationPolicy` / `ClassificationDecision` / placeholder are
// Phase 41's, reused unchanged: one redaction verdict in the deployment,
// not a second one that can drift from the field gate's.
//
// What this seam deliberately is NOT:
//
//   * On by default. No redactor is registered unless a deployment
//     composes one, and with none composed every retrieval path below is
//     the pre-201 code byte-for-byte (GP 11 / GP 13).
//   * A PII detector. WHICH spans of a deployment's corpus are `Pii` is
//     a deployment fact the SDK cannot know (GP 1), so the default
//     redactor takes a span locator as a REQUIRED argument. There is no
//     "locate nothing" default, because a redactor that silently masks
//     nothing is worse than no redactor at all — it reads as protection.
//   * An existence oracle. A withheld original refuses with exactly the
//     `NoOriginalAvailable` a genuinely absent one gets (GP 4). The
//     distinction is recorded server-side in the audit trail, where the
//     operator can see it and the caller cannot.
//
// ── Honest scoping (the MVP boundary) ────────────────────────────
//
// Span masking is defined on **text**. An original whose bytes are not
// text — an image-only PDF with no text layer, a `.docx`, a `.xlsx`, any
// binary — cannot have a span replaced in it without re-authoring the
// container format, which is a document-processing capability the SDK
// does not have and is not going to grow inside a redaction seam. The
// redactor therefore DECLINES such an original rather than pretending,
// and `UnmaskableOriginalDisposition` decides what declining means:
// `Withhold` (the default, fail-closed — an original the redactor could
// not inspect is one it cannot vouch for) or `ServeAsIs` (correct only
// where a deployment knows its binary originals carry nothing
// classified). What never happens is a silent unmasked delivery from a
// redactor that was asked to mask.

/// A classified span of an original's decoded text. `Text` is matched
/// literally (ordinal, case-sensitive) — every occurrence is replaced.
/// An empty `Text` is ignored rather than matching everywhere.
type ClassifiedSpan = {
    /// The literal text to mask.
    Text: string
    /// The span's sensitivity, decided against the deployment's
    /// `ClassificationPolicy` exactly as a classified field's is.
    Level: ClassificationLevel
}

module ClassifiedSpan =
    /// A span of `text` at `level`.
    let create (level: ClassificationLevel) (text: string) : ClassifiedSpan = { Text = text; Level = level }

/// What the redactor does with an original whose content it cannot mask
/// (no text layer, or bytes that are not valid UTF-8 text).
[<RequireQualifiedAccess>]
type UnmaskableOriginalDisposition =
    /// Refuse the fetch. The caller gets `NoOriginalAvailable`,
    /// indistinguishable from absence (GP 4). The default, and the
    /// fail-closed reading of GP 2: the redactor was composed because
    /// this corpus carries classified content, and an original it could
    /// not inspect is not one it can vouch for.
    | Withhold
    /// Deliver the bytes unchanged. Correct only where a deployment
    /// knows its binary originals carry nothing the policy would mask —
    /// a deliberate, recorded choice rather than a silent fallback.
    | ServeAsIs

/// The redactor's verdict on one original.
[<RequireQualifiedAccess>]
type OriginalRedaction =
    /// Deliver these bytes. `RedactedLevels` carries the stable
    /// `ClassificationLevel.name`s that were masked, sorted — empty when
    /// nothing was masked, in which case the document is the input
    /// byte-for-byte.
    | Deliver of document: OriginalDocument * redactedLevels: string list
    /// Do not deliver. `Reason` is for the server-side audit + log only;
    /// the caller sees `NoOriginalAvailable` and learns nothing from it.
    | Withhold of reason: string

/// Apply the deployment's classification policy to a resolved original
/// before its bytes leave the server. Invoked by the KB retrieval path
/// AFTER the scope gate and BEFORE the response is built, so a redactor
/// can never be reached by a caller who was going to be refused anyway.
///
/// Implementations are stateless between calls and receive everything
/// they need per invocation (the same contract `IOriginalSourceResolver`
/// and `IOriginalPreviewSeam` hold), so one singleton serves every scope
/// without capturing per-request state.
///
/// **Six portability rules (GP 12).** Identity by value (immutable
/// records + strings, no live handles); async at the boundary; no
/// callback failure channel — a refusal is the typed `Withhold` case;
/// stateless between invocations; no ordering promise; no timing
/// primitive.
type IOriginalRedactor =
    abstract Redact: original: OriginalDocument * ctx: AccessContext -> Async<OriginalRedaction>

/// Tuning for the default classification-driven redactor.
type OriginalRedactorOptions = {
    /// Per-level access decision. Defaults to
    /// `ClassificationGate.defaultPolicy` — the SAME policy value the
    /// Phase 41 field gate runs, so a caller who is masked in the
    /// structured view is masked in the original and vice versa. A
    /// deployment with a bespoke role mapping supplies its own here and
    /// passes the identical value to the field gate.
    Policy: ClassificationPolicy
    /// Locate the classified spans in one original's decoded text.
    /// Required — see the file header on why there is no default.
    LocateSpans: OriginalDocument -> string -> Async<ClassifiedSpan list>
    /// What to do with an original whose content cannot be masked.
    Unmaskable: UnmaskableOriginalDisposition
    /// Whether an original's content is text the redactor can mask
    /// spans in. Defaults to "the content type is a `text/*` type",
    /// which covers every format the Phase 104 resolver labels as text
    /// (`text/plain` / `text/markdown` / `text/csv`) plus a deployment's
    /// own text types. A deployment with a text-layer extractor for a
    /// richer format widens this AND supplies a locator that can read
    /// it; widening this alone only reaches the UTF-8 decode guard,
    /// which then declines.
    IsTextExtractable: OriginalDocument -> bool
}

module OriginalRedactorOptions =

    /// Default text-extractability test — a `text/*` content type,
    /// compared case-insensitively and ignoring any `; charset=` suffix.
    let defaultIsTextExtractable (original: OriginalDocument) : bool =
        not (String.IsNullOrWhiteSpace original.ContentType)
        && original.ContentType.TrimStart().StartsWith("text/", StringComparison.OrdinalIgnoreCase)

    /// Options over a span locator, with the fail-closed defaults: the
    /// Phase 41 default policy, `Withhold` for anything unmaskable, and
    /// text-extractability by `text/*` content type.
    let create (locateSpans: OriginalDocument -> string -> Async<ClassifiedSpan list>) : OriginalRedactorOptions = {
        Policy = ClassificationGate.defaultPolicy
        LocateSpans = locateSpans
        Unmaskable = UnmaskableOriginalDisposition.Withhold
        IsTextExtractable = defaultIsTextExtractable
    }

    /// Options over a fixed span list — the shape a deployment that
    /// declares its classified terms in configuration wants, and what
    /// the contract tests drive.
    let ofSpans (spans: ClassifiedSpan list) : OriginalRedactorOptions = create (fun _ _ -> async.Return spans)

    let withPolicy (policy: ClassificationPolicy) (o: OriginalRedactorOptions) = { o with Policy = policy }

    let withUnmaskableDisposition (disposition: UnmaskableOriginalDisposition) (o: OriginalRedactorOptions) = {
        o with
            Unmaskable = disposition
    }

    let withTextExtractableTest (isTextExtractable: OriginalDocument -> bool) (o: OriginalRedactorOptions) = {
        o with
            IsTextExtractable = isTextExtractable
    }

/// Entity name the redaction audit rows are recorded under. Not an
/// `IEntityStore` entity — the classification audit payload is keyed by
/// `(EntityName, FieldPath)` and this is the stable pair a reviewer
/// filters on to see original-document redactions: entity
/// `KnowledgeOriginal`, field path the document id.
[<Literal>]
let KnowledgeOriginalEntityName = "KnowledgeOriginal"

/// Value-free classification audit for a redacted delivery: one
/// `ClassifiedFieldRead` row per masked level, `Redacted = true`, under
/// the Phase 41 classification audit channel. Reusing the Phase 41 event
/// rather than minting a KB-specific one is deliberate — a reviewer
/// asking "what did this caller get masked today" wants one query, not
/// one per surface that happens to mask.
///
/// Identifiers + level names only. No spans, no content, no counts that
/// could be differenced into content — the same PII envelope every KB
/// audit event holds.
let redactionAuditEvents (userId: string) (documentId: string) (redactedLevels: string list) : AuditEvent list =
    redactedLevels
    |> List.map (fun level ->
        AuditEvent.ClassifiedFieldRead {
            UserId = userId
            EntityName = KnowledgeOriginalEntityName
            FieldPath = documentId
            Level = level
            Redacted = true
        })

/// Default redactor: decode the original as UTF-8 text, ask the locator
/// which spans are classified, and mask every span whose level the
/// policy `Redact`s for this caller.
///
/// The policy is consulted **per level, once** — not per span — so a
/// document with four hundred `Pii` spans costs one decision, and the
/// audit trail carries one row per level rather than four hundred rows
/// that say the same thing.
///
/// A caller the policy `Allow`s at every present level receives the
/// original unchanged and no audit row: being permitted to read is the
/// Phase 41 field gate's `Allow` arm, and it is not a redaction.
type ClassificationOriginalRedactor(options: OriginalRedactorOptions) =

    /// Strict UTF-8 decode. `None` when the bytes are not valid UTF-8 —
    /// which is the honest answer for a binary original that slipped
    /// past a widened `IsTextExtractable`, and reaches the same decline
    /// branch.
    let tryDecodeUtf8 (bytes: byte[]) : string option =
        if isNull bytes then
            None
        else
            try
                Some(UTF8Encoding(false, true).GetString bytes)
            with :? DecoderFallbackException ->
                None

    let decline (reason: string) (original: OriginalDocument) =
        match options.Unmaskable with
        | UnmaskableOriginalDisposition.ServeAsIs -> OriginalRedaction.Deliver(original, [])
        | UnmaskableOriginalDisposition.Withhold -> OriginalRedaction.Withhold reason

    interface IOriginalRedactor with
        member _.Redact(original, ctx) = async {
            if not (options.IsTextExtractable original) then
                return
                    decline
                        $"content type '{original.ContentType}' is not text-extractable; span masking is defined on text only"
                        original
            else
                match tryDecodeUtf8 original.Content with
                | None ->
                    return decline "content is not valid UTF-8 text; span masking is defined on text only" original
                | Some text ->
                    let! located = options.LocateSpans original text

                    let spans = located |> List.filter (fun s -> not (String.IsNullOrEmpty s.Text))

                    let levelsToMask =
                        spans
                        |> List.map _.Level
                        |> List.distinct
                        |> List.filter (fun level ->
                            match options.Policy level ctx with
                            | Redact -> true
                            | Allow -> false)

                    if List.isEmpty levelsToMask then
                        // Either nothing classified is present, or this
                        // caller may read all of it. Byte-for-byte the
                        // input in both cases.
                        return OriginalRedaction.Deliver(original, [])
                    else
                        let masked =
                            spans
                            |> List.filter (fun s -> List.contains s.Level levelsToMask)
                            |> List.fold
                                (fun (acc: string) s ->
                                    acc.Replace(
                                        s.Text,
                                        ClassificationGate.RedactedPlaceholder,
                                        StringComparison.Ordinal
                                    ))
                                text

                        let bytes = Encoding.UTF8.GetBytes masked

                        let redactedLevels = levelsToMask |> List.map ClassificationLevel.name |> List.sort

                        return
                            OriginalRedaction.Deliver(
                                {
                                    original with
                                        Content = bytes
                                        SizeBytes = int64 bytes.Length
                                },
                                redactedLevels
                            )
        }

/// Construct the default classification-driven redactor over a span
/// locator, with the fail-closed defaults.
let createDefault (locateSpans: OriginalDocument -> string -> Async<ClassifiedSpan list>) : IOriginalRedactor =
    ClassificationOriginalRedactor(OriginalRedactorOptions.create locateSpans) :> _

/// Construct the default redactor over explicit options.
let create (options: OriginalRedactorOptions) : IOriginalRedactor =
    ClassificationOriginalRedactor(options) :> _

/// Register an `IOriginalRedactor` so classified spans are masked in
/// every original this deployment serves. Threads the singleton
/// registration through the shared `ComposeExtensions.ServiceConfig`
/// seam — the same pattern as `withOriginalSourceResolver` /
/// `withOriginalPreviewSeam` — so `AIServerApp` / `RAGServerApp` inherit
/// it via their `Base` without a per-wrapper forwarder.
///
/// **Opt-in and zero-cost (GP 11 / GP 13):** an app that never calls
/// this registers nothing, and `GetOriginalDocument` /
/// `GetOriginalDelivery` run the pre-201 code byte-for-byte.
///
/// **Composing this disables signed-URL delivery** for originals —
/// deliberately, and it is the one interaction worth knowing before you
/// compose it. A signed URL hands the client a link to the *raw stored
/// bytes*; the redactor never ran over those, and no amount of care at
/// the API tier changes what object storage will serve. So a deployment
/// that composes both a redactor and `withSignedOriginalUrls` gets
/// inline delivery, once per fetch, with the degradation named in the
/// log — the same per-fetch, never-silent shape Phase 105 uses where
/// object-store retention and signed delivery collide.
let withOriginalRedactor (redactor: IOriginalRedactor) (app: ServerApp) : ServerApp =
    let register (s: IServiceCollection) =
        s.AddSingleton<IOriginalRedactor>(redactor)

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

/// Redaction-aware `IOriginalPreviewSeam.previewOriginal` — the third
/// delivery route, closed here so a consumer driving the preview seam
/// directly is held to the same gate the API handlers are.
///
/// With no redactor composed this IS `previewOriginal`, one call, no
/// second copy of anything.
///
/// With one composed, an **inline** target's bytes go through the
/// redactor and a masked target is rebuilt through
/// `PreviewTarget.ofOriginal`, so the top-level `SizeBytes` matches the
/// masked body rather than the original's. A **signed-URL** target is
/// refused with `NoOriginalAvailable`: the URL points at bytes the
/// redactor never saw, and unlike `getOriginalDelivery` — which owns the
/// resolver and can fall back to serving inline — this entry point has
/// only the seam it was handed and cannot re-resolve. A deployment
/// wanting previews AND redaction composes the inline seam
/// (`OriginalPreviewSeam.createDefault`), not the signing one.
///
/// Audit is the caller's: this is a library entry point with no
/// `KnowledgeApiDeps` and therefore no audit log. The API handlers, which
/// do have one, emit the Phase 107 + classification rows themselves.
let redactedPreviewOriginal
    (redactor: IOriginalRedactor option)
    (ctx: AccessContext)
    (seam: KnowledgeBase.ServerOriginalPreviewSeam.IOriginalPreviewSeam)
    (storage: IBlobStorage)
    (container: string)
    (docId: string)
    (locator: ToolUp.Platform.VectorKnowledgeTypes.SourceLocator option)
    : Async<Result<PreviewTarget, KnowledgeBaseError>> =
    async {
        let! result = KnowledgeBase.ServerOriginalPreviewSeam.previewOriginal seam storage container docId locator

        match redactor, result with
        | None, _ -> return result
        | Some _, Error e -> return Error e
        | Some r, Ok target ->
            match target.Content with
            | PreviewContent.SignedUrl _ -> return Error NoOriginalAvailable
            | PreviewContent.Inline original ->
                let! verdict = r.Redact(original, ctx)

                match verdict with
                | OriginalRedaction.Withhold _ -> return Error NoOriginalAvailable
                | OriginalRedaction.Deliver(masked, _) ->
                    return Ok(PreviewTarget.ofOriginal target.DocumentId target.Anchor masked)
    }