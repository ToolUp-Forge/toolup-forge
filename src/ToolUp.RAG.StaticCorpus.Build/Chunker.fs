// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.RAG.StaticCorpus

open System
open System.Text
open System.Text.RegularExpressions
open Markdig
open Markdig.Syntax

// ─── Markdown-aware chunker (build-time) ─────────────────────────
//
// Parses a Markdown document with Markdig and splits it into retrievable
// chunks on H2/H3 heading boundaries. Each chunk carries its full ancestor
// heading chain (`HeadingPath`, e.g. `["# Title"; "## Section"; "### Sub"]`)
// so a retrieved chunk always carries its section context. A section whose
// body exceeds `MaxChunkChars` is split on block (paragraph / code-fence)
// boundaries — a fenced code block is a single block, so it is never split
// mid-fence. Each split replays the same `HeadingPath`.
//
// Pure function (`chunk`): same input ⇒ same output, no I/O, no vendor type
// on the boundary (Markdig stays internal). Chunk identity is by value:
// `{source}:{heading-anchor}:{ordinal}` (GP 12 rule 1).

/// A chunk of source text with its heading context, produced by the
/// chunker. The embedding is added later by the packer (which calls the
/// configured `IEmbeddingProvider`), so this carries no vector — it is the
/// text-only precursor to a `DocChunk`.
type RawChunk = {
    Id: string
    Source: string
    HeadingPath: string list
    Body: string
    /// Producer-stamped extras. Always carries `"anchor"` (the deepest
    /// heading's slug, for a `source#anchor` jump-link) and `"ordinal"`.
    Metadata: Map<string, string>
}

module Chunker =

    /// Default per-chunk character budget. A section body longer than this
    /// is split on block boundaries (never mid-code-fence, never
    /// mid-paragraph).
    [<Literal>]
    let DefaultMaxChunkChars = 1500

    /// GitHub-style heading slug: lowercase, alphanumerics kept, spaces /
    /// underscores / hyphens collapsed to a single `-`, other punctuation
    /// dropped. Used both for the `#anchor` jump-link and the chunk id.
    let slug (text: string) : string =
        let sb = StringBuilder()

        for ch in text.ToLowerInvariant() do
            if Char.IsLetterOrDigit ch then
                sb.Append ch |> ignore
            elif ch = ' ' || ch = '-' || ch = '_' then
                sb.Append '-' |> ignore
        // else: drop the character

        Regex.Replace(sb.ToString(), "-+", "-").Trim('-')

    /// Strip the leading `#` markers + whitespace from a raw heading line
    /// (`"## Tasks"` → `"Tasks"`).
    let private headingText (rawHeadingLine: string) = rawHeadingLine.TrimStart('#').Trim()

    /// Split a section's block texts into sub-bodies each within
    /// `maxChunkChars`, greedily grouping whole blocks. A single block
    /// larger than the budget becomes its own sub-body (never split — this
    /// is what keeps code fences and long paragraphs intact). Always yields
    /// at least one sub-body when `blocks` is non-empty.
    let private splitBlocks (maxChunkChars: int) (blocks: string list) : string list =
        let results = ResizeArray<string>()
        let current = ResizeArray<string>()
        let mutable currentLen = 0

        let flush () =
            if current.Count > 0 then
                results.Add(String.Join("\n\n", current))
                current.Clear()
                currentLen <- 0

        for b in blocks do
            // +2 for the "\n\n" join separator between blocks.
            let addedLen = b.Length + (if current.Count > 0 then 2 else 0)

            if current.Count > 0 && currentLen + addedLen > maxChunkChars then
                flush ()

            current.Add b
            currentLen <- currentLen + b.Length + (if current.Count > 1 then 2 else 0)

        flush ()
        List.ofSeq results

    /// Chunk a Markdown document. `source` is the logical source name (used
    /// in chunk ids + `Source`); `maxChunkChars` is the per-chunk budget
    /// (`DefaultMaxChunkChars` is the usual value); `markdown` is the raw
    /// document text. Returns the chunks in document order.
    let chunk (source: string) (maxChunkChars: int) (markdown: string) : RawChunk list =
        let doc = Markdown.Parse markdown

        // Raw source slice for a block — preserves the original Markdown
        // (fenced code blocks included) exactly, so nothing is re-rendered.
        let rawOf (b: Block) =
            let span = b.Span

            if span.Start >= 0 && span.Length > 0 && span.End < markdown.Length + 1 then
                markdown.Substring(span.Start, span.Length).TrimEnd()
            else
                ""

        let results = ResizeArray<RawChunk>()
        // Heading path for levels 1–3 only (H4+ are body content), each entry
        // the raw heading line ("## Tasks").
        let mutable pathStack: (int * string) list = []
        let bodyBlocks = ResizeArray<string>()
        let mutable ordinal = 0

        let flush () =
            if bodyBlocks.Count > 0 then
                let headingPath = pathStack |> List.map snd

                let anchor =
                    match List.tryLast pathStack with
                    | Some(_, raw) -> slug (headingText raw)
                    | None -> "_preamble"

                let subBodies = splitBlocks maxChunkChars (List.ofSeq bodyBlocks)

                for body in subBodies do
                    let id = sprintf "%s:%s:%d" source anchor ordinal

                    results.Add {
                        Id = id
                        Source = source
                        HeadingPath = headingPath
                        Body = body
                        Metadata = Map.ofList [ "anchor", anchor; "ordinal", string ordinal ]
                    }

                    ordinal <- ordinal + 1

                bodyBlocks.Clear()

        for block in doc do
            match block with
            | :? HeadingBlock as h ->
                let level = h.Level

                if level <= 3 then
                    // Chunk boundary (+ H1 title): close the current section,
                    // then update the ancestor path.
                    flush ()
                    pathStack <- pathStack |> List.filter (fun (l, _) -> l < level)
                    pathStack <- pathStack @ [ level, rawOf block ]
                else
                    // H4+ — sub-heading is part of the section body.
                    bodyBlocks.Add(rawOf block)
            | _ -> bodyBlocks.Add(rawOf block)

        flush ()
        List.ofSeq results