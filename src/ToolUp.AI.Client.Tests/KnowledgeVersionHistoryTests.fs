// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.KnowledgeVersionHistoryTests

// ─── Phase 636 — the KB version-history client surface ───────────────
//
// Phase 510 shipped document versioning API-only. Phase 636 renders it:
// a version badge, a lazily-fetched history drawer, and per-version
// original access. This pack pins the two halves that a reader cannot
// verify by looking:
//
//   1. **The GP 11 gate** — a single-version document, and therefore
//      every document in a deployment that never composed
//      `withDocumentVersioning`, renders exactly the markup it rendered
//      before the phase. Asserted over REAL markup from a real mount,
//      not over the predicate alone: the predicate being right and the
//      badge being rendered anyway is precisely the failure this exists
//      to catch. The positive case is asserted alongside it so the pack
//      cannot pass vacuously by rendering nothing at all.
//
//   2. **The laziness** — `OpenVersionHistory` is what starts the fetch,
//      and nothing on the list-load path touches it. A drawer that
//      pre-fetched would be an N+1 over `GetDocumentVersions` on every
//      render of every corpus.
//
// ── Why Fable-side and not in the .NET Expecto pack ──
// The same constraint every pack in this project records: `ClientModel`
// holds a module-level `Api.makeProxy<KnowledgeApi>`, whose
// reflection-shaped builder raises under .NET reflection at static-init
// time — so `update` cannot even be CALLED there. Rendering React needs
// a JS runtime regardless.

open System
open SharedTypes
open ToolUp.Platform
open ToolUp.Platform.Testing
open ToolUp.AI.Client.Tests.NodeTest

// ─── Fixtures ────────────────────────────────────────────────────────

let private uploadedAt (day: int) =
    DateTimeOffset(2026, 8, day, 9, 30, 0, TimeSpan.Zero)

/// One document, parameterised on the ONE field this phase reads. Every
/// other field is held constant so a markup difference between two
/// fixtures can only have come from `Version`.
let private documentAtVersion (version: int) : KnowledgeDocument = {
    Id = "doc-1"
    FileName = "handbook.pdf"
    FileType = "pdf"
    UploadedAt = uploadedAt 1
    UploadedBy = "sam"
    Status = IngestionStatus.Complete 12
    SizeBytes = 2_400_000L
    ChunkCount = 12
    Source = KnowledgeSource.UploadedFile
    ContentHash = None
    Version = version
    Tags = []
}

let private versionRecord (version: int) (supersededAt: DateTimeOffset option) : KnowledgeDocumentVersion = {
    DocumentId = "doc-1"
    Version = version
    FileName = "handbook.pdf"
    FileType = "pdf"
    SizeBytes = 2_400_000L
    ChunkCount = 12
    ContentHash = None
    UploadedAt = uploadedAt version
    UploadedBy = "sam"
    OriginalBlobName = sprintf "knowledge/doc-1/versions/%d/handbook.pdf" version
    SupersededAt = supersededAt
}

let private listConfig: KnowledgeListView.KnowledgeListConfig = {
    EmptyStateText = "nothing here"
    RowAction = None
    InstanceKey = "phase-636-tests"
}

/// The rendered markup of the shared document list for one document.
let private markupFor (doc: KnowledgeDocument) : string =
    ViewMount.mount (KnowledgeListView.KnowledgeListView listConfig [ doc ])

/// The badge's distinguishing class. Matching on the class rather than
/// on the text is deliberate: `v3` could plausibly appear in a file name
/// or a tag, and a marker that a fixture can accidentally satisfy is not
/// a marker.
[<Literal>]
let private BadgeClass = "bg-violet-50"

// ─── The model gate ──────────────────────────────────────────────────

let private gateTests =
    testList "version gate" [
        testCase "a version-1 document has no history"
        <| fun _ ->
            Expect.isFalse
                (KnowledgeListView.hasVersionHistory (documentAtVersion 1))
                "version 1 is the whole population of an unversioned deployment"

        testCase "a superseded lineage has history"
        <| fun _ ->
            Expect.isTrue
                (KnowledgeListView.hasVersionHistory (documentAtVersion 2))
                "version 2 means one supersede has happened"

            Expect.isTrue (KnowledgeListView.hasVersionHistory (documentAtVersion 9)) "…and so does version 9"
    ]

// ─── The rendered gate (GP 11) ───────────────────────────────────────

let private renderTests =
    testList "rendered version affordance" [
        testCase "a single-version document renders no version badge"
        <| fun _ ->
            let markup = markupFor (documentAtVersion 1)

            Expect.isFalse
                (markup.Contains BadgeClass)
                "a version-1 row must be byte-for-byte what it was before Phase 636 (GP 11)"

        testCase "a versioned document renders its badge"
        <| fun _ ->
            let markup = markupFor (documentAtVersion 3)

            Expect.isTrue (markup.Contains BadgeClass) "a superseded lineage must be visible as one"
            Expect.isTrue (markup.Contains "v3") "the badge names the current version"

        testCase "the badge is the ONLY difference a version makes to a row"
        <| fun _ ->
            // Removing the badge span from the versioned markup must
            // reproduce the unversioned markup exactly. This is the
            // "renders byte-identically" claim stated as an equality
            // rather than as an absence, so a change that alters the row
            // in some OTHER version-dependent way still fails here.
            let plain = markupFor (documentAtVersion 1)
            let versioned = markupFor (documentAtVersion 4)

            // Located by walking out from the class to its enclosing
            // element rather than by matching a literal `<span class=…`:
            // the DOM serialises attributes in its own order, so the
            // literal form silently found nothing and the assertion
            // below would have passed on an empty diff.
            let classAt = versioned.IndexOf BadgeClass

            Expect.isTrue (classAt >= 0) "the versioned row must actually carry the badge span"

            let badgeStart = versioned.LastIndexOf("<span", classAt)
            let badgeEnd = versioned.IndexOf("</span>", classAt) + "</span>".Length
            let stripped = versioned.Remove(badgeStart, badgeEnd - badgeStart)

            Expect.equal stripped plain "with the badge removed, a versioned row is identical to an unversioned one"
    ]

// ─── The drawer's MVU arms ───────────────────────────────────────────

let private drawerTests =
    testList "version-history drawer MVU" [
        testCase "the list load never touches the drawer — the fetch is tied to the OPEN"
        <| fun _ ->
            let m0, _ = ClientModel.init ()
            Expect.isNone m0.VersionHistory "init: drawer closed"

            let loaded, _ =
                ClientModel.update (ClientModel.LoadDocuments(Finished [ documentAtVersion 3 ])) m0

            Expect.isNone
                loaded.VersionHistory
                "loading a corpus of versioned documents must not pre-fetch any history (no N+1)"

        testCase "opening enters the loading state for that document"
        <| fun _ ->
            let m0, _ = ClientModel.init ()

            let opened, _ =
                ClientModel.update (ClientModel.OpenVersionHistory("doc-1", "handbook.pdf")) m0

            match opened.VersionHistory with
            | None -> failwith "OpenVersionHistory must open the drawer"
            | Some state ->
                Expect.equal state.DocId "doc-1" "the drawer is bound to the document it was opened from"
                Expect.equal state.FileName "handbook.pdf" "the header reads the name captured at open time"
                Expect.isNone state.Versions "Versions stays None while the lazy fetch is in flight"
                Expect.isNone state.LoadError "no error before the fetch has answered"

        testCase "loaded versions arrive newest-first"
        <| fun _ ->
            let m0, _ = ClientModel.init ()

            let opened, _ =
                ClientModel.update (ClientModel.OpenVersionHistory("doc-1", "handbook.pdf")) m0

            // Deliberately handed to the client out of order.
            let wire = [
                versionRecord 1 (Some(uploadedAt 2))
                versionRecord 3 None
                versionRecord 2 (Some(uploadedAt 3))
            ]

            let settled, _ =
                ClientModel.update (ClientModel.VersionHistoryLoaded("doc-1", wire)) opened

            match settled.VersionHistory with
            | Some { Versions = Some versions } ->
                Expect.equal (versions |> List.map _.Version) [ 3; 2; 1 ] "newest version first"
            | _ -> failwith "VersionHistoryLoaded must populate the open drawer"

        testCase "a stale answer for another document is discarded"
        <| fun _ ->
            let m0, _ = ClientModel.init ()

            let opened, _ =
                ClientModel.update (ClientModel.OpenVersionHistory("doc-2", "other.pdf")) m0

            let settled, _ =
                ClientModel.update (ClientModel.VersionHistoryLoaded("doc-1", [ versionRecord 2 None ])) opened

            match settled.VersionHistory with
            | Some state ->
                Expect.equal state.DocId "doc-2" "the open drawer is unchanged"
                Expect.isNone state.Versions "the first document's history must not land under the second's heading"
            | None -> failwith "the drawer must stay open"

        testCase "a failed fetch is reported, not silently empty"
        <| fun _ ->
            let m0, _ = ClientModel.init ()

            let opened, _ =
                ClientModel.update (ClientModel.OpenVersionHistory("doc-1", "handbook.pdf")) m0

            let failed, _ =
                ClientModel.update (ClientModel.VersionHistoryFailed("doc-1", "network down")) opened

            match failed.VersionHistory with
            | Some state ->
                Expect.equal state.LoadError (Some "network down") "the reason reaches the drawer"
                Expect.isNone state.Versions "a failure is not an empty history"
            | None -> failwith "the drawer must stay open to show the error"

        testCase "a download failure is surfaced and clears the in-flight marker"
        <| fun _ ->
            let m0, _ = ClientModel.init ()

            let opened, _ =
                ClientModel.update (ClientModel.OpenVersionHistory("doc-1", "handbook.pdf")) m0

            let requesting, _ =
                ClientModel.update (ClientModel.DownloadVersionRequested(versionRecord 3 None)) opened

            match requesting.VersionHistory with
            | Some state -> Expect.equal state.Downloading (Some 3) "the requested version is marked in flight"
            | None -> failwith "the drawer must stay open"

            let settled, _ =
                ClientModel.update
                    (ClientModel.DownloadVersionSettled(Error "This document isn't available."))
                    requesting

            match settled.VersionHistory with
            | Some state ->
                Expect.isNone state.Downloading "the in-flight marker clears whatever the outcome"

                Expect.equal
                    state.DownloadError
                    (Some "This document isn't available.")
                    "the typed refusal reaches the drawer as a message"
            | None -> failwith "the drawer must stay open"

        testCase "closing discards the drawer entirely"
        <| fun _ ->
            let m0, _ = ClientModel.init ()

            let opened, _ =
                ClientModel.update (ClientModel.OpenVersionHistory("doc-1", "handbook.pdf")) m0

            let closed, _ = ClientModel.update ClientModel.CloseVersionHistory opened
            Expect.isNone closed.VersionHistory "a closed drawer holds no cached history"
    ]

let tests =
    testList "KnowledgeBase version history (Phase 636)" [ gateTests; renderTests; drawerTests ]