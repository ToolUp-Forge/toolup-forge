// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Toolup.Markdown

open Feliz

// Minimal markdown renderer for AI chat bubbles.
//
// Supports: **bold**, *italic*, `code`, [text](url); headings #/##/###;
// bullet (-/*) and ordered (1.) lists; fenced code ``` ``` ```; blockquotes
// (> ); horizontal rules (---); soft line breaks inside paragraphs;
// GFM-style pipe tables with `:---` / `---:` / `:---:` alignment markers.
//
// Not a full CommonMark implementation — nested lists, reference-style
// links, images, setext headings, and HTML passthrough are intentionally
// out of scope. If the AI ever starts emitting those, extend here rather
// than pulling in a JS markdown library.
//
// Pipe tables require a leading `|` on header + body rows (the strict
// GFM form). `\|` escapes a literal pipe inside a cell. Rows with fewer
// cells than the header are padded with empties; extra cells are dropped.

type private Inline =
    | Txt of string
    | Strong of Inline list
    | Emph of Inline list
    | Code of string
    | Link of text: Inline list * url: string

type private Alignment =
    | Left
    | Center
    | Right
    | Default

type private Block =
    | Paragraph of string list
    | Heading of level: int * text: string
    | Bullets of string list
    | Ordered of string list
    | CodeFence of lang: string * lines: string list
    | Quote of string list
    | Table of headers: string list * alignments: Alignment list * rows: string list list
    | HRule
    | Blank

// ---------- Inline parser ----------

let private isBoundary (c: char) =
    c = ' '
    || c = '\t'
    || c = '\n'
    || c = '('
    || c = '['
    || c = '{'
    || c = '"'
    || c = '\''
    || c = ','
    || c = '.'
    || c = '!'
    || c = '?'
    || c = ';'
    || c = ':'
    || c = '>'
    || c = '<'

let private findClose (s: string) (start: int) (delim: string) : int option =
    let dlen = delim.Length

    let rec loop i =
        if i + dlen > s.Length then
            None
        elif i + dlen <= s.Length && s.Substring(i, dlen) = delim then
            Some i
        else
            loop (i + 1)

    loop start

let rec private parseInline (s: string) : Inline list =
    let sb = System.Text.StringBuilder()
    let acc = System.Collections.Generic.List<Inline>()

    let flush () =
        if sb.Length > 0 then
            acc.Add(Txt(sb.ToString()))
            sb.Clear() |> ignore

    let mutable i = 0

    while i < s.Length do
        let c = s[i]
        // [text](url)
        if c = '[' then
            match s.IndexOf(']', i + 1) with
            | -1 ->
                sb.Append(c) |> ignore
                i <- i + 1
            | closeBracket when closeBracket + 1 < s.Length && s[closeBracket + 1] = '(' ->
                match s.IndexOf(')', closeBracket + 2) with
                | -1 ->
                    sb.Append(c) |> ignore
                    i <- i + 1
                | closeParen ->
                    flush ()
                    let inner = s.Substring(i + 1, closeBracket - i - 1)
                    let url = s.Substring(closeBracket + 2, closeParen - closeBracket - 2)
                    acc.Add(Link(parseInline inner, url))
                    i <- closeParen + 1
            | _ ->
                sb.Append(c) |> ignore
                i <- i + 1
        // `code`
        elif c = '`' then
            match findClose s (i + 1) "`" with
            | Some close when close > i + 1 ->
                flush ()
                acc.Add(Code(s.Substring(i + 1, close - i - 1)))
                i <- close + 1
            | _ ->
                sb.Append(c) |> ignore
                i <- i + 1
        // **bold**
        elif c = '*' && i + 1 < s.Length && s[i + 1] = '*' then
            match findClose s (i + 2) "**" with
            | Some close when close > i + 2 ->
                flush ()
                acc.Add(Strong(parseInline (s.Substring(i + 2, close - i - 2))))
                i <- close + 2
            | _ ->
                sb.Append(c) |> ignore
                i <- i + 1
        // *italic* (word-boundary gated to avoid consuming lone asterisks)
        elif c = '*' then
            let prev = if i = 0 then ' ' else s[i - 1]

            if isBoundary prev then
                match findClose s (i + 1) "*" with
                | Some close when close > i + 1 && s[close - 1] <> ' ' ->
                    flush ()
                    acc.Add(Emph(parseInline (s.Substring(i + 1, close - i - 1))))
                    i <- close + 1
                | _ ->
                    sb.Append(c) |> ignore
                    i <- i + 1
            else
                sb.Append(c) |> ignore
                i <- i + 1
        // _italic_
        elif c = '_' then
            let prev = if i = 0 then ' ' else s[i - 1]

            if isBoundary prev then
                match findClose s (i + 1) "_" with
                | Some close when close > i + 1 && s[close - 1] <> ' ' ->
                    flush ()
                    acc.Add(Emph(parseInline (s.Substring(i + 1, close - i - 1))))
                    i <- close + 1
                | _ ->
                    sb.Append(c) |> ignore
                    i <- i + 1
            else
                sb.Append(c) |> ignore
                i <- i + 1
        else
            sb.Append(c) |> ignore
            i <- i + 1

    flush ()
    List.ofSeq acc

// ---------- Block parser ----------

let private bulletPrefix (line: string) : string option =
    if line.StartsWith("- ") then
        Some(line.Substring 2)
    elif line.StartsWith("* ") && not (line.StartsWith("**")) then
        Some(line.Substring 2)
    else
        None

let private orderedPrefix (line: string) : string option =
    let mutable i = 0

    while i < line.Length && System.Char.IsDigit line[i] do
        i <- i + 1

    if i > 0 && i + 1 < line.Length && line[i] = '.' && line[i + 1] = ' ' then
        Some(line.Substring(i + 2))
    else
        None

// ---------- Pipe-table helpers ----------

// Split a `| a | b | c |` row into its trimmed cells. Returns None for
// any line that doesn't begin with `|` after trimming — the strict GFM
// form. The trailing `|` is optional. `\|` escapes a literal pipe.
let private tableEscapePlaceholder = ""

let private splitTableRow (line: string) : string list option =
    let trimmed = line.TrimStart()

    if not (trimmed.StartsWith "|") then
        None
    else
        let escaped = trimmed.Substring(1).Replace("\\|", tableEscapePlaceholder)

        let body =
            if escaped.EndsWith "|" then
                escaped.Substring(0, escaped.Length - 1)
            else
                escaped

        Some(
            body.Split('|')
            |> Array.map (fun c -> c.Trim().Replace(tableEscapePlaceholder, "|"))
            |> Array.toList
        )

// Recognise the separator row: each cell is `---`, `:---`, `---:`, or
// `:---:` (3 or more dashes). Returns the alignment per cell on
// success.
let private parseAlignmentCell (cell: string) : Alignment option =
    let c = cell.Trim()

    if c.Length < 3 then
        None
    else
        let startColon = c.StartsWith ":"
        let endColon = c.EndsWith ":"

        let body =
            let s = if startColon then c.Substring 1 else c
            if endColon then s.Substring(0, s.Length - 1) else s

        if body.Length >= 3 && body |> Seq.forall (fun ch -> ch = '-') then
            match startColon, endColon with
            | true, true -> Some Center
            | true, false -> Some Left
            | false, true -> Some Right
            | false, false -> Some Default
        else
            None

let private tryParseSeparator (line: string) : Alignment list option =
    match splitTableRow line with
    | None -> None
    | Some cells ->
        if cells.IsEmpty then
            None
        else
            let alignments = cells |> List.map parseAlignmentCell

            if alignments |> List.forall Option.isSome then
                Some(alignments |> List.map Option.get)
            else
                None

// Right-pad a row's cells with empty strings (extra cells past the
// header count are dropped). Matches GFM behaviour: a row mismatch
// shouldn't break the rest of the table.
let private normaliseRow (cells: string list) (expectedCount: int) : string list =
    let cellsArr = List.toArray cells

    Array.init expectedCount (fun i -> if i < cellsArr.Length then cellsArr[i] else "")
    |> Array.toList

let private headingLevel (line: string) : (int * string) option =
    let mutable n = 0

    while n < line.Length && n < 6 && line[n] = '#' do
        n <- n + 1

    if n > 0 && n < line.Length && line[n] = ' ' then
        Some(n, line.Substring(n + 1))
    else
        None

let private parseBlocks (text: string) : Block list =
    let lines =
        text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n') |> Array.toList

    let blocks = System.Collections.Generic.List<Block>()
    let mutable rest = lines

    while not rest.IsEmpty do
        match rest with
        | [] -> ()
        | line :: tail ->
            let trimmed = line.TrimStart()

            if System.String.IsNullOrWhiteSpace line then
                blocks.Add Blank
                rest <- tail
            elif trimmed.StartsWith "```" then
                let lang = trimmed.Substring(3).Trim()

                let rec take acc (ls: string list) =
                    match ls with
                    | [] -> List.rev acc, []
                    | l :: more ->
                        if l.TrimStart().StartsWith "```" then
                            List.rev acc, more
                        else
                            take (l :: acc) more

                let body, remaining = take [] tail
                blocks.Add(CodeFence(lang, body))
                rest <- remaining
            elif trimmed = "---" || trimmed = "***" then
                blocks.Add HRule
                rest <- tail
            elif trimmed.StartsWith "> " then
                let rec take acc (ls: string list) =
                    match ls with
                    | l :: more when l.TrimStart().StartsWith "> " -> take (l.TrimStart().Substring 2 :: acc) more
                    | _ -> List.rev acc, ls

                let items, remaining = take [ line.TrimStart().Substring 2 ] tail
                blocks.Add(Quote items)
                rest <- remaining
            // Pipe table: leading `|` on the current line AND the
            // next line parses as an alignment separator. Without
            // the separator a `|`-bearing line is just prose
            // (and falls through to the paragraph branch below).
            elif
                trimmed.StartsWith "|"
                && (match tail with
                    | sep :: _ -> tryParseSeparator sep |> Option.isSome
                    | [] -> false)
            then
                match splitTableRow trimmed, tail with
                | Some headers, sep :: afterSep ->
                    let alignments = tryParseSeparator sep |> Option.get
                    let cols = alignments.Length
                    // Normalise the header row to the alignment-row
                    // width so the renderer never indexes past the
                    // alignments list — keeps a malformed header
                    // (e.g. one too many `|`) from breaking the
                    // table entirely.
                    let normalisedHeaders = normaliseRow headers cols

                    let rec takeRows acc (ls: string list) =
                        match ls with
                        | l :: more ->
                            match splitTableRow l with
                            | Some cells when not (System.String.IsNullOrWhiteSpace l) ->
                                takeRows (normaliseRow cells cols :: acc) more
                            | _ -> List.rev acc, ls
                        | [] -> List.rev acc, []

                    let rows, remaining = takeRows [] afterSep
                    blocks.Add(Table(normalisedHeaders, alignments, rows))
                    rest <- remaining
                | _ ->
                    // Should be unreachable given the guard above,
                    // but fall through to paragraph rather than
                    // crashing if the line shape changes mid-flight.
                    blocks.Add(Paragraph [ line ])
                    rest <- tail
            else
                match headingLevel trimmed with
                | Some(lvl, txt) ->
                    blocks.Add(Heading(lvl, txt))
                    rest <- tail
                | None ->
                    match bulletPrefix trimmed with
                    | Some first ->
                        let rec take acc (ls: string list) =
                            match ls with
                            | l :: more ->
                                match bulletPrefix (l.TrimStart()) with
                                | Some item -> take (item :: acc) more
                                | None -> List.rev acc, ls
                            | [] -> List.rev acc, []

                        let items, remaining = take [ first ] tail
                        blocks.Add(Bullets items)
                        rest <- remaining
                    | None ->
                        match orderedPrefix trimmed with
                        | Some first ->
                            let rec take acc (ls: string list) =
                                match ls with
                                | l :: more ->
                                    match orderedPrefix (l.TrimStart()) with
                                    | Some item -> take (item :: acc) more
                                    | None -> List.rev acc, ls
                                | [] -> List.rev acc, []

                            let items, remaining = take [ first ] tail
                            blocks.Add(Ordered items)
                            rest <- remaining
                        | None ->
                            let rec take acc (ls: string list) =
                                match ls with
                                | l :: more when
                                    not (System.String.IsNullOrWhiteSpace l)
                                    && headingLevel (l.TrimStart()) = None
                                    && bulletPrefix (l.TrimStart()) = None
                                    && orderedPrefix (l.TrimStart()) = None
                                    && not (l.TrimStart().StartsWith "```")
                                    && not (l.TrimStart().StartsWith "> ")
                                    && l.TrimStart() <> "---"
                                    && l.TrimStart() <> "***"
                                    ->
                                    take (l :: acc) more
                                | _ -> List.rev acc, ls

                            let para, remaining = take [ line ] tail
                            blocks.Add(Paragraph para)
                            rest <- remaining

    List.ofSeq blocks

// ---------- Feliz renderer ----------

let rec private renderInline (node: Inline) : ReactElement =
    match node with
    | Txt s -> Html.text s
    | Strong children ->
        Html.strong [
            prop.className "font-semibold"
            prop.children (children |> List.map renderInline)
        ]
    | Emph children -> Html.em [ prop.className "italic"; prop.children (children |> List.map renderInline) ]
    | Code s ->
        Html.code [
            prop.className "font-mono text-[0.92em] px-1 py-[1px] bg-black/10 rounded"
            prop.text s
        ]
    | Link(children, url) ->
        Html.a [
            prop.href url
            prop.target "_blank"
            prop.rel "noopener noreferrer"
            prop.className "underline hover:opacity-80"
            prop.children (children |> List.map renderInline)
        ]

let private renderLine (line: string) : ReactElement list =
    parseInline line |> List.map renderInline

let private joinSoftBreaks (lines: string list) : ReactElement list =
    lines
    |> List.mapi (fun i line ->
        let pieces = renderLine line
        if i = 0 then pieces else Html.br [] :: pieces)
    |> List.concat

let private renderBlock (block: Block) : ReactElement option =
    match block with
    | Blank -> None
    | HRule -> Some(Html.hr [ prop.className "my-2 border-current opacity-20" ])
    | Heading(lvl, txt) ->
        let cls =
            match lvl with
            | 1 -> "text-lg font-semibold mt-1 mb-1"
            | 2 -> "text-base font-semibold mt-1 mb-1"
            | _ -> "text-sm font-semibold mt-1 mb-1"

        let children = renderLine txt
        let props: IReactProperty list = [ prop.className cls; prop.children children ]

        let elem =
            match lvl with
            | 1 -> Html.h1 props
            | 2 -> Html.h2 props
            | 3 -> Html.h3 props
            | _ -> Html.h4 props

        Some elem
    | Paragraph lines -> Some(Html.p [ prop.className "leading-relaxed"; prop.children (joinSoftBreaks lines) ])
    | Bullets items ->
        Some(
            Html.ul [
                prop.className "list-disc list-outside pl-5 space-y-0.5"
                prop.children (items |> List.map (fun item -> Html.li [ prop.children (renderLine item) ]))
            ]
        )
    | Ordered items ->
        Some(
            Html.ol [
                prop.className "list-decimal list-outside pl-5 space-y-0.5"
                prop.children (items |> List.map (fun item -> Html.li [ prop.children (renderLine item) ]))
            ]
        )
    | CodeFence(_, lines) ->
        Some(
            Html.pre [
                prop.className "font-mono text-[0.92em] p-2 bg-black/10 rounded overflow-x-auto whitespace-pre"
                prop.children [ Html.code [ prop.text (System.String.Join("\n", lines)) ] ]
            ]
        )
    | Quote lines ->
        Some(
            Html.blockquote [
                prop.className "pl-3 border-l-2 border-current/30 opacity-80 italic"
                prop.children (joinSoftBreaks lines)
            ]
        )
    | Table(headers, alignments, rows) ->
        let alignClass alignment =
            match alignment with
            | Center -> "text-center"
            | Right -> "text-right"
            | Left
            | Default -> "text-left"

        let headerCells =
            headers
            |> List.mapi (fun i h ->
                Html.th [
                    prop.className (
                        sprintf
                            "px-2 py-1 border border-current/20 font-semibold bg-current/[0.04] %s"
                            (alignClass alignments[i])
                    )
                    prop.children (renderLine h)
                ])

        let bodyRows =
            rows
            |> List.mapi (fun rowIdx row ->
                Html.tr [
                    prop.className (if rowIdx % 2 = 1 then "bg-current/[0.02]" else "")
                    prop.children (
                        row
                        |> List.mapi (fun i cell ->
                            Html.td [
                                prop.className (
                                    sprintf
                                        "px-2 py-1 border border-current/20 align-top %s"
                                        (alignClass alignments[i])
                                )
                                prop.children (renderLine cell)
                            ])
                    )
                ])

        // Wrap the table in an overflow-x-auto container so wide
        // tables scroll horizontally rather than overflowing the
        // chat bubble or forcing the bubble to widen past its
        // intended max-width.
        Some(
            Html.div [
                prop.className "overflow-x-auto my-2"
                prop.children [
                    Html.table [
                        prop.className "text-sm border-collapse border border-current/20"
                        prop.children [
                            Html.thead [ prop.children [ Html.tr [ prop.children headerCells ] ] ]
                            Html.tbody [ prop.children bodyRows ]
                        ]
                    ]
                ]
            ]
        )

/// Parse the given markdown text and return a container element whose
/// children are the rendered blocks. Safe for untrusted input: the
/// parser never injects raw HTML, and links always open with
/// `rel="noopener noreferrer"`.
let render (text: string) : ReactElement =
    let blocks = parseBlocks text

    Html.div [
        prop.className "space-y-2"
        prop.children (blocks |> List.choose renderBlock)
    ]