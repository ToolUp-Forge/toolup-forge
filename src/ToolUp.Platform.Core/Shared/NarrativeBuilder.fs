// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Fluent helpers for assembling a `NarrativeDocument` without spelling
/// out every record field. The underlying type is unchanged — these
/// helpers compose values into the existing shape so authors can mix and
/// match builder-style and record-literal style freely.
///
/// ```fsharp
/// open ToolUp.Platform.Narrative
///
/// let doc =
///     Narrative.create "Sales analysis"
///     |> Narrative.subtitle "Q3 FY26"
///     |> Narrative.section "Headline" "headline" [
///         Narrative.paragraph [ Narrative.text "Revenue up "; Narrative.strong "12%" ]
///         Narrative.callout Warning [ Narrative.text "One outlier excluded." ]
///     ]
///     |> Narrative.section "Detail" "detail" [
///         Narrative.bullets [
///             [ Narrative.text "North region above plan" ]
///             [ Narrative.text "South region flat" ]
///         ]
///     ]
/// ```
module ToolUp.Platform.Narrative.Narrative

open System

// ─── Inline span constructors ────────────────────────────────────

let text (s: string) : InlineSpan = Text s
let emphasis (s: string) : InlineSpan = Emphasis s
let strong (s: string) : InlineSpan = Strong s
let metric (label: string) (value: string) : InlineSpan = Metric(label, value)
let code (s: string) : InlineSpan = Code s

// ─── Element constructors ───────────────────────────────────────

let paragraph (spans: InlineSpan list) : NarrativeElement = Paragraph spans

let bullets (items: InlineSpan list list) : NarrativeElement = BulletList items

let ordered (items: InlineSpan list list) : NarrativeElement = OrderedList items

let keyValues (pairs: (string * InlineSpan list) list) : NarrativeElement = KeyValueGrid pairs

let table (columns: (string * TableAlignment) list) (rows: InlineSpan list list list) : NarrativeElement =
    Table(columns, rows)

let callout (severity: Severity) (spans: InlineSpan list) : NarrativeElement = Callout(severity, spans)

let divider: NarrativeElement = Divider

// ─── Document constructors ──────────────────────────────────────

/// Create an empty document with the given title. Sections are added via
/// `Narrative.section`; subtitle via `Narrative.subtitle`; provenance via
/// `Narrative.withProvenance`.
let create (title: string) : NarrativeDocument = {
    Title = title
    Subtitle = None
    Sections = []
    Provenance = None
}

/// Set the document's subtitle.
let subtitle (s: string) (doc: NarrativeDocument) : NarrativeDocument = { doc with Subtitle = Some s }

/// Append a section to the document. `id` is the stable anchor used by
/// renderers that support deep-linking; `heading` is the human-visible
/// title. Use `sectionWith` to add a subheading.
let section
    (heading: string)
    (id: string)
    (elements: NarrativeElement list)
    (doc: NarrativeDocument)
    : NarrativeDocument =
    let s = {
        Id = id
        Heading = heading
        Subheading = None
        Elements = elements
    }

    {
        doc with
            Sections = doc.Sections @ [ s ]
    }

/// Append a section with a subheading.
let sectionWith
    (heading: string)
    (id: string)
    (subheading: string)
    (elements: NarrativeElement list)
    (doc: NarrativeDocument)
    : NarrativeDocument =
    let s = {
        Id = id
        Heading = heading
        Subheading = Some subheading
        Elements = elements
    }

    {
        doc with
            Sections = doc.Sections @ [ s ]
    }

/// Stamp provenance directly. The composition-root helper
/// `NarrativePublisher.publishWithProvenance` does the same thing at
/// publish time; use this when you need the stamp before handing the
/// document somewhere else.
let withProvenance
    (moduleId: string)
    (pageRoute: string option)
    (settingsKey: string)
    (settingsDisplay: (string * string) list)
    (doc: NarrativeDocument)
    : NarrativeDocument =
    {
        doc with
            Provenance =
                Some {
                    ModuleId = moduleId
                    PageRoute = pageRoute
                    GeneratedAt = DateTimeOffset.UtcNow
                    SettingsKey = settingsKey
                    SettingsDisplay = settingsDisplay
                }
    }