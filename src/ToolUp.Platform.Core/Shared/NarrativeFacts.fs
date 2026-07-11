// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 521 — fact references carried by narrative `Metric` spans.
///
/// The wiring that turns a knowledge bank of prose and a fact base from
/// two parallel stores into one graph (plan D4): a narrative *references*
/// facts (via `InlineSpan.Metric`'s optional `factRef`) rather than
/// copying them, so citing a narrative transitively cites its facts and a
/// superseded fact mechanically identifies every stale narrative that
/// quotes it.
///
/// This module is the pure, dependency-free walk over that reference set:
/// it collects the fact ids a document / section cites, and — given a set
/// of ids known to be superseded — flags the citing document for
/// staleness. Flagging only: a superseded fact never rewrites a narrative
/// (plan D4). Fact ids are opaque strings, so this module takes no
/// dependency on the fact companion's types (GP 1); a caller holding the
/// fact store resolves the supersession heads and passes them in.
module ToolUp.Platform.Narrative.NarrativeFacts

/// A narrative flagged as potentially stale because it cites a fact that
/// has been superseded. Discovery / flagging only (plan D4) — the
/// document is never rewritten. `SupersededByFactId` is the id that
/// superseded the cited fact (its lineage's current head) when the caller
/// knew it, `None` otherwise.
type StaleNarrativeFlag = {
    /// The superseded fact id the narrative cites.
    SupersededFactId: string
    /// The fact that superseded it (the lineage's current head), when known.
    SupersededByFactId: string option
}

/// The fact ids referenced by one inline span (recursing into `Link`
/// content). Only a `Metric` with `Some factRef` contributes.
let rec factRefsInSpan (span: InlineSpan) : string list =
    match span with
    | Metric(_, _, Some factRef) -> [ factRef ]
    | Metric(_, _, None) -> []
    | Link(_, spans) -> spans |> List.collect factRefsInSpan
    | Text _
    | Emphasis _
    | Strong _
    | Code _
    | Image _
    | Br -> []

/// The fact ids referenced by one block element, recursing through every
/// span-bearing and element-bearing case (list bodies, table cells, card /
/// accordion / tab nested bodies).
let rec factRefsInElement (element: NarrativeElement) : string list =
    match element with
    | Paragraph spans
    | Heading(_, spans)
    | Callout(_, spans)
    | Blockquote(_, spans) -> spans |> List.collect factRefsInSpan
    | BulletList items
    | OrderedList items -> items |> List.collect (List.collect factRefsInSpan)
    | KeyValueGrid pairs -> pairs |> List.collect (fun (_, spans) -> spans |> List.collect factRefsInSpan)
    | Table(_, rows) -> rows |> List.collect (List.collect (List.collect factRefsInSpan))
    | Card spec -> spec.Body |> List.collect factRefsInElement
    | Accordion panels
    | Tabs panels -> panels |> List.collect (fun (_, body) -> body |> List.collect factRefsInElement)
    | CodeBlock _
    | Divider
    | Video _
    | Audio _
    | ImageGallery _
    | Embed _
    | Component _ -> []

/// The distinct fact ids referenced anywhere in one section.
let factRefsInSection (section: NarrativeSection) : Set<string> =
    section.Elements |> List.collect factRefsInElement |> Set.ofList

/// The distinct fact ids referenced anywhere in a document.
let factRefs (document: NarrativeDocument) : Set<string> =
    document.Sections
    |> List.collect (fun s -> s.Elements |> List.collect factRefsInElement)
    |> Set.ofList

/// Whether a document cites the given fact id in any of its `Metric` spans.
let cites (factId: string) (document: NarrativeDocument) : bool =
    factRefs document |> Set.contains factId

/// Flag every superseded fact a document cites. `supersededBy` maps each
/// known-superseded fact id to the id that superseded it (its lineage's
/// current head), or `None` when the caller doesn't know the head. The
/// result carries one flag per cited-and-superseded fact; a document that
/// cites none returns `[]`. Flagging only — the document is never rewritten
/// (plan D4).
let staleFlags (supersededBy: Map<string, string option>) (document: NarrativeDocument) : StaleNarrativeFlag list =
    let cited = factRefs document

    supersededBy
    |> Map.toList
    |> List.filter (fun (factId, _) -> cited.Contains factId)
    |> List.map (fun (factId, head) -> {
        SupersededFactId = factId
        SupersededByFactId = head
    })

// ─── Disclosure redaction (Phase 525.C) ──────────────────────────────
//
// The tool-result egress door: a narrative returned to the AI model as a
// tool result must not carry the *value* of a fact the disclosure gate
// denied. The walk below replaces each denied `Metric` span's value with a
// non-disclosive marker naming the policy — a typed refusal, not an empty
// result, so the model can explain *that* a value exists but is restricted
// without ever seeing it. The span keeps its label and fact ref (ids are
// content hashes, not values), so transitive citation stays intact.
// Fact-companion-free like the rest of this module: `denied` maps opaque
// fact ids to the policy ref that denied them.

/// The marker a denied `Metric` span's value is replaced with. Names the
/// policy, never the value.
let notDisclosableMarker (policyRef: string) : string =
    sprintf "[not disclosable under policy %s]" policyRef

/// Redact one inline span (recursing into `Link` content). Only a `Metric`
/// whose `factRef` appears in `denied` changes.
let rec redactSpan (denied: Map<string, string>) (span: InlineSpan) : InlineSpan =
    match span with
    | Metric(label, _, Some factRef) ->
        match denied.TryFind factRef with
        | Some policyRef -> Metric(label, notDisclosableMarker policyRef, Some factRef)
        | None -> span
    | Link(href, spans) -> Link(href, spans |> List.map (redactSpan denied))
    | Metric(_, _, None)
    | Text _
    | Emphasis _
    | Strong _
    | Code _
    | Image _
    | Br -> span

/// Redact one block element, recursing through every span-bearing and
/// element-bearing case — the write twin of `factRefsInElement`, so the
/// two walks cover the same cases and cannot drift.
let rec redactElement (denied: Map<string, string>) (element: NarrativeElement) : NarrativeElement =
    let spans = List.map (redactSpan denied)

    match element with
    | Paragraph s -> Paragraph(spans s)
    | Heading(level, s) -> Heading(level, spans s)
    | Callout(kind, s) -> Callout(kind, spans s)
    | Blockquote(cite, s) -> Blockquote(cite, spans s)
    | BulletList items -> BulletList(items |> List.map spans)
    | OrderedList items -> OrderedList(items |> List.map spans)
    | KeyValueGrid pairs -> KeyValueGrid(pairs |> List.map (fun (k, s) -> k, spans s))
    | Table(header, rows) -> Table(header, rows |> List.map (List.map spans))
    | Card spec ->
        Card {
            spec with
                Body = spec.Body |> List.map (redactElement denied)
        }
    | Accordion panels -> Accordion(panels |> List.map (fun (t, body) -> t, body |> List.map (redactElement denied)))
    | Tabs panels -> Tabs(panels |> List.map (fun (t, body) -> t, body |> List.map (redactElement denied)))
    | CodeBlock _
    | Divider
    | Video _
    | Audio _
    | ImageGallery _
    | Embed _
    | Component _ -> element

/// Redact every denied fact value in a document. `denied` maps each denied
/// fact id to the policy ref that denied it; a document citing none of
/// them returns structurally unchanged.
let redactDeniedFacts (denied: Map<string, string>) (document: NarrativeDocument) : NarrativeDocument =
    if Map.isEmpty denied then
        document
    else
        {
            document with
                Sections =
                    document.Sections
                    |> List.map (fun section -> {
                        section with
                            Elements = section.Elements |> List.map (redactElement denied)
                    })
        }