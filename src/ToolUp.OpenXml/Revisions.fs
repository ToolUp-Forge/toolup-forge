// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.OpenXml

open System

// ─── Phase 124 — edit sets lowered to tracked changes ────────────
//
// An `EditSet` describes programmatic edits at model addresses;
// `Revisions.applyTracked` rewrites the model so the edits are
// represented as revision-marked runs / paragraph marks + comments.
// `Emit` then lowers those marks to native `w:ins` / `w:del` runs
// and comment parts — the output opens in Word as a redline the
// reviewer can accept or reject. Application is pure: the input
// model is untouched; errors come back as data (GP 5).

/// Who an edit set is attributed to. `Initials` feed the comment
/// part; Word surfaces them in the review pane.
type EditAuthor = {
    Name: string
    Initials: string option
}

/// One edit at a model address. Addresses refer to the model state
/// the edit is applied to — edits in a set apply in order, and an
/// insertion shifts the addresses of later blocks in its section.
type DocEdit =
    /// Insert a new paragraph after the addressed block, marked as
    /// a tracked insertion. The paragraph is classified by its
    /// style id (a `Heading1`–`Heading9` style inserts a heading).
    | InsertParagraphAfter of address: BlockAddress * paragraph: ParagraphModel
    /// Mark the addressed paragraph-like block (heading, paragraph,
    /// list item) as a tracked deletion — struck through in Word,
    /// removed when the reviewer accepts.
    | DeleteParagraph of address: BlockAddress
    /// Replace the first occurrence of `find` in the addressed
    /// paragraph's visible text with `replaceWith` — lowered as a
    /// tracked deletion of the matched spans (splitting runs at the
    /// match boundaries, so the match may cross formatting runs)
    /// plus a tracked insertion of the replacement. An empty
    /// `replaceWith` is a pure tracked deletion of the match.
    | ReplaceText of address: BlockAddress * find: string * replaceWith: string
    /// Anchor a new comment on the addressed paragraph-like block.
    | AddComment of address: BlockAddress * text: string

type EditSet = {
    Author: EditAuthor
    Timestamp: DateTimeOffset
    Edits: DocEdit list
}

type RevisionError =
    | AddressOutOfRange of BlockAddress
    /// The edit needs a paragraph-like block (heading, paragraph,
    /// list item) but the address holds a table or opaque block.
    | NotAParagraphBlock of address: BlockAddress * actualKind: string
    | TextNotFound of address: BlockAddress * find: string

[<RequireQualifiedAccess>]
module Revisions =

    // ─── Model surgery helpers ───────────────────────────────────

    let private replaceAt (index: int) (value: 'a) (items: 'a list) : 'a list =
        items |> List.mapi (fun i item -> if i = index then value else item)

    let private withSectionBlocks
        (address: BlockAddress)
        (rewrite: Block list -> Result<Block list, RevisionError>)
        (model: DocModel)
        : Result<DocModel, RevisionError> =
        match model.Sections |> List.tryItem address.Section with
        | None -> Error(AddressOutOfRange address)
        | Some section ->
            if address.Block < 0 || address.Block >= section.Blocks.Length then
                Error(AddressOutOfRange address)
            else
                rewrite section.Blocks
                |> Result.map (fun blocks ->
                    let updated = { section with Blocks = blocks }

                    {
                        model with
                            Sections = model.Sections |> replaceAt address.Section updated
                    })

    let private blockKind (block: Block) : string =
        match block with
        | Heading _ -> "heading"
        | Paragraph _ -> "paragraph"
        | ListItem _ -> "list item"
        | Table _ -> "table"
        | Figure _ -> "figure"
        | OpaqueBlock _ -> "opaque block"

    /// Decompose a paragraph-like block into its paragraph + a
    /// rebuild function preserving the block kind.
    let private asParagraph
        (address: BlockAddress)
        (block: Block)
        : Result<ParagraphModel * (ParagraphModel -> Block), RevisionError> =
        match block with
        | Paragraph p -> Ok(p, Paragraph)
        | Heading(level, p) -> Ok(p, (fun updated -> Heading(level, updated)))
        | ListItem(numbering, p) -> Ok(p, (fun updated -> ListItem(numbering, updated)))
        | Table _
        // A figure carries no runs, so there is nothing for a tracked
        // change to wrap — the same refusal a table gets, for the same
        // reason.
        | Figure _
        | OpaqueBlock _ -> Error(NotAParagraphBlock(address, blockKind block))

    let private updateParagraphAt
        (address: BlockAddress)
        (update: ParagraphModel -> Result<ParagraphModel, RevisionError>)
        (model: DocModel)
        : Result<DocModel, RevisionError> =
        model
        |> withSectionBlocks address (fun blocks ->
            asParagraph address blocks[address.Block]
            |> Result.bind (fun (paragraph, rebuild) ->
                update paragraph
                |> Result.map (fun updated -> blocks |> replaceAt address.Block (rebuild updated))))

    // ─── Edit semantics ──────────────────────────────────────────

    let private markInserted (info: RevisionInfo) (paragraph: ParagraphModel) : ParagraphModel = {
        paragraph with
            Runs =
                paragraph.Runs
                |> List.map (fun run ->
                    match run.Revision with
                    | Some _ -> run
                    | None -> {
                        run with
                            Revision = Some(Inserted info)
                      })
            MarkRevision = Some(Inserted info)
    }

    let private markDeleted (info: RevisionInfo) (paragraph: ParagraphModel) : ParagraphModel = {
        paragraph with
            Runs =
                paragraph.Runs
                |> List.map (fun run ->
                    match run.Revision with
                    | Some(Deleted _) -> run
                    | _ -> {
                        run with
                            Revision = Some(Deleted info)
                      })
            MarkRevision = Some(Deleted info)
    }

    /// Tracked find/replace across run boundaries: the matched span
    /// is split out of the runs it covers (preserving each covered
    /// segment's formatting) and marked deleted; the replacement is
    /// inserted after the last covered segment with the formatting
    /// of the first.
    let private replaceInRuns
        (info: RevisionInfo)
        (find: string)
        (replaceWith: string)
        (runs: Run list)
        : Run list option =
        let searchableText =
            runs
            |> List.choose (fun run ->
                match run.Revision with
                | Some(Deleted _) -> None
                | _ -> Some run.Text)
            |> String.concat ""

        match searchableText.IndexOf(find, StringComparison.Ordinal) with
        | -1 -> None
        | matchStart ->
            let matchEnd = matchStart + find.Length
            let result = ResizeArray<Run>()
            let mutable offset = 0
            let mutable replacementFormatting = None
            let mutable replacementEmitted = false

            for run in runs do
                match run.Revision with
                | Some(Deleted _) -> result.Add run
                | _ ->
                    let runStart = offset
                    let runEnd = offset + run.Text.Length
                    offset <- runEnd

                    if runEnd <= matchStart || runStart >= matchEnd then
                        result.Add run
                    else
                        let coveredFrom = max 0 (matchStart - runStart)
                        let coveredTo = min run.Text.Length (matchEnd - runStart)

                        if coveredFrom > 0 then
                            result.Add {
                                run with
                                    Text = run.Text.Substring(0, coveredFrom)
                            }

                        if coveredTo > coveredFrom then
                            if replacementFormatting.IsNone then
                                replacementFormatting <- Some run.Formatting

                            result.Add {
                                run with
                                    Text = run.Text.Substring(coveredFrom, coveredTo - coveredFrom)
                                    Revision = Some(Deleted info)
                            }

                        if runEnd >= matchEnd && not replacementEmitted then
                            replacementEmitted <- true

                            if replaceWith <> "" then
                                result.Add {
                                    Text = replaceWith
                                    Formatting = replacementFormatting |> Option.defaultValue RunFormatting.none
                                    Revision = Some(Inserted info)
                                }

                        if coveredTo < run.Text.Length then
                            result.Add {
                                run with
                                    Text = run.Text.Substring coveredTo
                            }

            Some(List.ofSeq result)

    let private nextCommentId (model: DocModel) : int =
        match model.Comments with
        | [] -> 1
        | comments -> 1 + (comments |> List.map _.Id |> List.max)

    let private applyEdit
        (author: EditAuthor)
        (info: RevisionInfo)
        (model: DocModel)
        (edit: DocEdit)
        : Result<DocModel, RevisionError> =
        match edit with
        | InsertParagraphAfter(address, paragraph) ->
            model
            |> withSectionBlocks address (fun blocks ->
                let marked = markInserted info paragraph
                let block = Block.classify None marked

                Ok(
                    (blocks |> List.truncate (address.Block + 1))
                    @ [ block ]
                    @ (blocks |> List.skip (address.Block + 1))
                ))
        | DeleteParagraph address -> model |> updateParagraphAt address (markDeleted info >> Ok)
        | ReplaceText(address, find, replaceWith) ->
            model
            |> updateParagraphAt address (fun paragraph ->
                match replaceInRuns info find replaceWith paragraph.Runs with
                | Some runs -> Ok { paragraph with Runs = runs }
                | None -> Error(TextNotFound(address, find)))
        | AddComment(address, text) ->
            let commentId = nextCommentId model

            model
            |> updateParagraphAt address (fun paragraph ->
                Ok {
                    paragraph with
                        CommentIds = paragraph.CommentIds @ [ commentId ]
                })
            |> Result.map (fun updated ->
                let comment = {
                    Id = commentId
                    Author = author.Name
                    Initials = author.Initials
                    Date = info.Date
                    Text = text
                }

                {
                    updated with
                        Comments = updated.Comments @ [ comment ]
                })

    /// Apply an edit set as tracked changes: a new model in which
    /// every edit is represented as revision marks + comments,
    /// attributed to the set's author + timestamp. Edits apply in
    /// order; the first failure aborts the set.
    let applyTracked (editSet: EditSet) (model: DocModel) : Result<DocModel, RevisionError> =
        let info = {
            Author = editSet.Author.Name
            Date = Some editSet.Timestamp
        }

        editSet.Edits
        |> List.fold (fun state edit -> state |> Result.bind (fun m -> applyEdit editSet.Author info m edit)) (Ok model)