// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.CommandPaletteNav

// ─── Phase 571 — the command palette's pure half ─────────────────────
//
// Everything the Ctrl+K palette needs that is NOT React: which
// destinations a caller may jump to, how a typed query ranks them, and
// the three-field overlay state the shell model carries. The Feliz
// component lives in `Components/CommandPalette.fs`; this file is what a
// contract pack can pin.
//
// **Why the candidate list is not its own filter.** A palette that
// enumerated the raw registration list would be a hole straight through
// the sidebar's visibility fold: every admin page a Member cannot see in
// the rail would be two keystrokes away. So `candidates` does not filter
// at all — it calls `SidebarVisibility.visible` (the one definition site
// the sidebar and the Phase 569 route guard already share) and only
// expands what survives into per-page destinations. Parity is not a
// property maintained by agreement between two filters; there is one
// filter, and the palette is downstream of it.
//
// **Why this file sits ahead of `SDK.ClientTypes.fs`.** Same reason
// `SidebarVisibility.fs` does (read its header): nothing inside `module
// Client` is constructible under .NET, and `ClientConfig`'s own module
// initialiser throws outside a Fable compilation. The fold therefore
// takes a `'m -> PaletteModuleFacts` projection rather than an
// `ErasedModule`, so the shell can pass the real projection and
// `ToolUp.Platform.Tests/InProcess/CommandPaletteContractTests.fs` can
// pass `id` over hand-built facts.
//
// GP 4 / GP 12 unchanged: this is UX, not the security boundary. A
// caller who forges a navigation past it meets the server's per-route
// guard, not data.

// ─── Candidate derivation (571.B) ─────────────────────────────────────

/// One page of a multi-page module, projected from `PageConfig`. The
/// icon is deliberately absent — `ReactElement` values cannot be built
/// under .NET, and keeping them out is what lets this fold be pinned by
/// the in-process contract pack. The shell re-attaches icons at render.
type PalettePageFacts = {
    /// `PageConfig.Route` — leading `/` preserved, so the composite
    /// sidebar id concatenates cleanly.
    Route: string
    /// `PageConfig.Title` — the page's display name and half of the
    /// searchable text.
    Title: string
}

/// The facts the palette reads off a registered module: the visibility
/// facts verbatim (so the shared fold applies unchanged) plus the
/// display fields the palette needs to render and rank an entry.
type PaletteModuleFacts = {
    /// The four facts `SidebarVisibility` reads. Carried whole rather
    /// than restated so a future stage added to the fold reaches the
    /// palette without touching this file.
    Nav: SidebarVisibility.SidebarModuleFacts
    /// `ModuleDefinition.Name` — the display name, and the first half of
    /// the searchable text.
    Name: string
    /// `ModuleDefinition.Pages` projected. Populated for every module
    /// that declares pages, including the single-page ones the sidebar
    /// renders as leaves; `hasPageViews` is what decides the shape.
    Pages: PalettePageFacts list
    /// `ErasedModule.PageViews.IsSome` — whether the module registered
    /// per-page views via `ClientModule.withPages`. Together with the
    /// page count this reproduces the sidebar's nesting rule exactly.
    HasPageViews: bool
}

/// One destination the palette can navigate to.
type PaletteCandidate = {
    /// The composite sidebar id — `"{moduleId}"` for a leaf,
    /// `"{moduleId}{pageRoute}"` for a page of a multi-page module.
    /// Dispatched verbatim as `ModuleSelected`, which is the same
    /// message a sidebar click sends (Phase 12b semantics preserved).
    SidebarId: string
    /// The owning module's `ModuleDefinition.Id`.
    ModuleId: string
    /// `Some route` for a page entry, `None` for a leaf — the shell's
    /// `parseSidebarId` recovers the same pair from `SidebarId`, so this
    /// is a convenience for renderers and tests, not a second source of
    /// truth.
    PageRoute: string option
    /// The owning module's display name.
    ModuleName: string
    /// The page's title for a page entry; `None` for a leaf.
    PageTitle: string option
    /// The module's sidebar group, when it declares one — rendered as
    /// the entry's context label.
    Group: string option
}

/// The text a query is matched against: module name for a leaf, module
/// name + page title for a page entry. Both halves are searchable so
/// "sales sku" and "sku" both reach `Sales Analysis › SKU analysis`.
let searchText (c: PaletteCandidate) : string =
    match c.PageTitle with
    | Some title -> c.ModuleName + " " + title
    | None -> c.ModuleName

/// The destinations one already-visible module contributes.
///
/// **Mirrors the sidebar's nesting rule exactly** (`SDK.Client.fs`'s
/// `views` binding): a module renders page children only when it
/// registered `PageViews` AND declares two or more pages; anything else
/// is a leaf whose id is the bare module id. A module with one declared
/// page is a leaf in the rail, so it is one candidate here.
///
/// The multi-page PARENT row is not a separate candidate. In the narrow
/// rail it navigates, but it navigates to `defaultPageRoute` — the first
/// declared page — which is already the first candidate below. The
/// palette therefore lists every reachable DESTINATION exactly once
/// rather than listing a row twice under two names.
let entriesFor (f: PaletteModuleFacts) : PaletteCandidate list =
    let leaf = {
        SidebarId = f.Nav.Id
        ModuleId = f.Nav.Id
        PageRoute = None
        ModuleName = f.Name
        PageTitle = None
        Group = f.Nav.Group
    }

    match f.HasPageViews, f.Pages with
    | true, (_ :: _ :: _ as pages) ->
        pages
        |> List.map (fun page -> {
            SidebarId = f.Nav.Id + page.Route
            ModuleId = f.Nav.Id
            PageRoute = Some page.Route
            ModuleName = f.Name
            PageTitle = Some page.Title
            Group = f.Nav.Group
        })
    | _ -> [ leaf ]

/// **The palette's candidate set (571.B).** `SidebarVisibility.visible`
/// piped into `entriesFor` — the whole definition, deliberately. The
/// palette owns no predicate of its own, so "a hidden admin page never
/// appears in the palette" is not a rule anyone has to remember: the
/// page is not in the list the expansion runs over.
///
/// Order is registration order, page order within a module — the rail's
/// pre-preference order, which is also the order an empty query shows.
let candidates
    (facts: 'm -> PaletteModuleFacts)
    (inputs: SidebarVisibility.SidebarVisibilityInputs)
    (modules: 'm list)
    : PaletteCandidate list =
    modules
    |> SidebarVisibility.visible (fun m -> (facts m).Nav) inputs
    |> List.collect (facts >> entriesFor)

// ─── Fuzzy matching (571.C) ───────────────────────────────────────────
//
// A subsequence matcher with three positional boosts, in ~30 lines and
// with no dependency. The alternative — an npm fuzzy-search package —
// would put a vendor dependency in the client tier for a ranking
// function whose entire input is a list of tens of module names (GP 1).

/// Per-character bonuses. Named because the ranking they produce is the
/// only user-visible contract here: a prefix beats a word start, which
/// beats a run, which beats a scattered match.
let private matchPoint = 1
let private contiguousBonus = 8
let private boundaryBonus = 12

/// True when position `i` starts a word — index 0, or preceded by
/// anything that is not a letter or digit. `"SKU analysis"` therefore
/// scores "a" at index 4 the same as at index 0.
let private isWordStart (s: string) (i: int) =
    i = 0 || not (System.Char.IsLetterOrDigit s[i - 1])

/// Score `haystack` against `query`, or `None` when the query's
/// characters do not appear in order.
///
/// Case-insensitive, and **whitespace in the query is ignored** — a
/// query is a fragment the user is typing, not a phrase, so "sal an"
/// and "salan" both reach "Sales Analysis". An empty / whitespace-only
/// query matches everything at score 0, which is what makes the
/// just-opened palette show the full list.
///
/// Higher is better. The leading-gap penalty (capped) breaks ties
/// towards a match that starts earlier in the text.
let score (query: string) (haystack: string) : int option =
    let q =
        query
        |> Seq.filter (System.Char.IsWhiteSpace >> not)
        |> Seq.toArray
        |> System.String

    if q.Length = 0 then
        Some 0
    else
        let q = q.ToLowerInvariant()
        let h = haystack.ToLowerInvariant()

        let rec walk qi hi acc prev first =
            if qi >= q.Length then
                Some(acc, first)
            elif hi >= h.Length then
                None
            elif q[qi] = h[hi] then
                let contiguity = if hi = prev + 1 then contiguousBonus else 0
                let boundary = if isWordStart h hi then boundaryBonus else 0
                let first' = if first < 0 then hi else first
                walk (qi + 1) (hi + 1) (acc + matchPoint + contiguity + boundary) hi first'
            else
                walk qi (hi + 1) acc prev first

        walk 0 0 0 -2 -1 |> Option.map (fun (acc, first) -> acc - min first 10)

/// Rank `entries` by `score` over their projected text, dropping
/// non-matches. Ties keep input order — a stable sort by
/// `(-score, index)` rather than `sortByDescending`, so two equally-good
/// matches stay in registration order instead of depending on the
/// sort's internals.
let rank (text: 'e -> string) (query: string) (entries: 'e list) : 'e list =
    entries
    |> List.indexed
    |> List.choose (fun (i, e) -> score query (text e) |> Option.map (fun s -> -s, i, e))
    |> List.sortBy (fun (negScore, i, _) -> negScore, i)
    |> List.map (fun (_, _, e) -> e)

/// The palette's ranked view of the candidate set — `rank` over
/// `searchText`. One call site in the component, one in the contract
/// pack.
let filter (query: string) (cs: PaletteCandidate list) : PaletteCandidate list = rank searchText query cs

// ─── Overlay state (571.C) ────────────────────────────────────────────

/// The palette's whole state, carried on the shell model so the overlay
/// is Elmish-native — no portal framework, no component-local store the
/// `update` cannot see.
///
/// **`Highlight` is unbounded on purpose.** It is a signed counter, not
/// an index: the model cannot know how many entries a query matched
/// (the ranking happens at render), so clamping here would need the
/// filtered count threaded back into `update`. The renderer resolves it
/// with `highlightIndex`, which wraps it into range — arrow keys stay a
/// pure `+1` / `-1` on an integer.
type PaletteState = {
    IsOpen: bool
    Query: string
    Highlight: int
}

/// Shut, empty, first entry highlighted — the initial state, and the
/// state every dismissal returns to. A palette that reopened onto the
/// last query would make the shortcut unpredictable: the same
/// keystrokes must always mean the same thing.
let closed: PaletteState = {
    IsOpen = false
    Query = ""
    Highlight = 0
}

let opened: PaletteState = { closed with IsOpen = true }

/// A new query resets the highlight — after typing another character
/// the ranking has changed, so keeping the old cursor position would
/// leave it pointing at an unrelated row.
let withQuery (query: string) (state: PaletteState) : PaletteState = {
    state with
        Query = query
        Highlight = 0
}

let moveHighlight (delta: int) (state: PaletteState) : PaletteState = {
    state with
        Highlight = state.Highlight + delta
}

/// Resolve an unbounded `Highlight` counter into a row index for a list
/// of `count` entries, wrapping in both directions (so ArrowUp from the
/// first row lands on the last). `0` for an empty list — the renderer
/// shows an empty state and Enter selects nothing.
///
/// Takes the counter rather than the state so the renderer, which is
/// the only caller that knows `count`, does not have to rebuild a
/// `PaletteState` to ask.
let highlightIndex (count: int) (highlight: int) : int =
    if count <= 0 then
        0
    else
        ((highlight % count) + count) % count