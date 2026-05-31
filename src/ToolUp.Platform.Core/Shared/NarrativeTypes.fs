// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Narrative

open System

/// Severity hint for a `Callout` element. Renderers map severity to colour
/// and iconography; serialisers may map to prefixes like "WARNING:" or
/// markdown admonition blocks.
type Severity =
    | Info
    | Notice
    | Warning
    | Critical

/// Inline text run. Deliberately non-recursive: a `Paragraph` is a flat list
/// of spans, not a tree. `Metric` is the single concession to structure —
/// both consumers format labelled numeric values (`r = 0.42`, `γ = 1.04`)
/// frequently enough that renderers benefit from styling them distinctly.
type InlineSpan =
    | Text of string
    | Emphasis of string
    | Strong of string
    | Metric of label: string * value: string
    | Code of string

/// Per-column horizontal alignment for `Table` cells. Renderers that can't
/// express alignment (e.g. Feliz text tables on narrow viewports) may ignore
/// the value, but markdown and aligned-plaintext renderers honour it.
type TableAlignment =
    | Left
    | Right
    | Center

/// A single block-level element inside a section. `BulletList` and
/// `OrderedList` take `InlineSpan list list` — each inner list is the spans
/// of one bullet. `KeyValueGrid` is for labelled pairs; a Feliz renderer
/// can lay them out as a two-column grid, a markdown renderer as a
/// definition list. `Table` is a first-class tabular element: each column
/// has a header and alignment; each row is a list of cells; each cell is
/// a list of inline spans.
type NarrativeElement =
    | Paragraph of InlineSpan list
    | BulletList of InlineSpan list list
    | OrderedList of InlineSpan list list
    | KeyValueGrid of (string * InlineSpan list) list
    | Table of columns: (string * TableAlignment) list * rows: InlineSpan list list list
    | Callout of Severity * InlineSpan list
    | Divider

/// A document section with a stable Id (used as an anchor by renderers
/// that support linking), a Heading, and an optional Subheading.
type NarrativeSection = {
    Id: string
    Heading: string
    Subheading: string option
    Elements: NarrativeElement list
}

/// Provenance metadata attached server-side after generation. Identifies
/// the module and page that produced the document, when it was generated,
/// and the analysis settings that produced it — used by the Knowledge Base
/// to deduplicate stored narratives and by the UI to show "Save to KB".
///
/// `SettingsKey` is canonical and deterministic: a stable ordering of the
/// analysis inputs (not display options) collapsed into one string. Two
/// runs with the same inputs must produce the same `SettingsKey` so a
/// re-run can be detected as a duplicate of the earlier stored version.
///
/// `SettingsDisplay` is a human-readable rendering of the same inputs for
/// the UI: label / value pairs preserving source order.
type NarrativeProvenance = {
    ModuleId: string
    PageRoute: string option
    GeneratedAt: DateTimeOffset
    SettingsKey: string
    SettingsDisplay: (string * string) list
}

/// The top-level narrative document returned by a module's narrative
/// generator. Crosses the server/client boundary via Fable.Remoting.
///
/// `Provenance` is optional because pure narrative libraries produce
/// documents without knowing the request context. The composition root
/// (`Server.fs`) attaches provenance after generation, before the doc
/// is persisted or returned to the client.
type NarrativeDocument = {
    Title: string
    Subtitle: string option
    Sections: NarrativeSection list
    Provenance: NarrativeProvenance option
}