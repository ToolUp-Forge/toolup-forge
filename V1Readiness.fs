// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 257 — the v1.0 readiness scorecard: "are we 1.0 yet?" as a
/// regenerated table rather than a judgement call.
///
/// **What this is.** Six preconditions for the 1.0 tag, each a discrete,
/// machine-readable check with a measured value and a declared threshold,
/// rendered into `docs/reference/v1-readiness.md` by the `V1Readiness`
/// FAKE target. The document is the operator's pre-tag read; nothing in
/// it is typed by hand, so it cannot be out of date in the way a
/// hand-maintained checklist is — a stale row is a row nobody regenerated,
/// and the header says when it was.
///
/// **The six rows, and where each one reads from:**
///
///   1. `baseline-stable` — how many consecutive releases (newest first)
///      shipped the SAME `api-baselines/` as the working tree carries now.
///      Read from git: the release tags Phase 260 already parses, diffed
///      against the tree. The surface a 1.0 candidate ships should have
///      stopped moving before it is promised.
///   2. `conformance-coverage` — replaceable seams carrying a conformance
///      pack, as Phase 259's `ConformanceCoverage.reconcile` derives them
///      from the checkout. GP 12: a portable interface is not proven until
///      a pack runs against it.
///   3. `doc-coverage` — public XML-doc coverage, summed over Phase 261's
///      `api-baselines/doc-coverage.approved.txt`. The doc-coverage policy
///      says the 1.0 surface should be the DOCUMENTED surface.
///   4. `undecided-renames` — rows of Phase 256's rename-decision table
///      (in its migration doc) whose Decision cell is not a decision.
///      Fail-closed: a wording the parser does not know counts as
///      undecided, so a new hedge is SEEN rather than silently accepted.
///   5. `adoption-pending` — pending (🟡) cells in the declared consumers'
///      columns of the generated adoption matrix. The consumer set is
///      DECLARED, not hard-coded (operator decision 2026-09-12: the
///      commercial consumer only; every other consumer adopts by design
///      and is out of scope), so widening it is a configuration change.
///      Both the matrix and the consumer names are facts about private
///      consumers, so both arrive from OUTSIDE this repository —
///      `TOOLUP_ADOPTION_MATRIX` (a path) and `TOOLUP_ADOPTION_CONSUMERS`
///      (names, as the matrix's column headers spell them) — and the
///      rendered row reports counts only. Nothing here or in the
///      generated document names a consumer.
///   6. `open-deprecations` — `(obsolete)` markers on the rendered public
///      surface. The shard asked for "open `[<Obsolete>]` WITHOUT a removal
///      target"; Phase 258 has since gated exactly that on every verified
///      tree, so that count is zero by construction and a row over it
///      could never fail. What 1.0 actually decides is the OPEN set: every
///      0.x-era notice names "a future major", and 1.0 is that major, so
///      each open marker is a remove-or-carry decision the cut takes.
///
/// **Degradation is a ROW, never a crash.** Every input reaches a check as
/// `Input<'a>` — `Available` or `Unavailable <why>` — and an unavailable
/// input renders as an explicit `⏳ not yet` row that counts as failing.
/// A sibling artefact that does not exist on this tree (a sidecar not yet
/// generated, a matrix not supplied, a repo with no release tag) is a
/// finding the operator reads, not an exception the generator dies on.
///
/// **Pure over its inputs, BCL-only.** The FAKE target does the reading
/// (git, files, the environment) and hands text and numbers in; every
/// check and the renderer are functions of that data, so the companion
/// pack scores an all-green fixture and one-red-per-cause fixtures without
/// a checkout, a build or a git history. Same shape and same reason as
/// `SemVerBump.fs` beside this file: forge's own release hygiene, not SDK
/// surface, source-linked (not copied) into ToolUp.Platform.Build.Tests
/// so the target and its proofs run one implementation.
module ToolUp.Forge.V1Readiness

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

// ─── Thresholds ──────────────────────────────────────────────────────

/// The declared thresholds every row is graded against. Read from
/// `v1-readiness.json` at the repo root; `defaults` applies where the
/// file is absent. The adoption row's consumer set is deliberately NOT
/// here — see `consumersEnvVar`.
type Thresholds = {
    /// Minimum number of consecutive releases whose `api-baselines/`
    /// equal the working tree's.
    StableReleases: int
    /// Minimum percentage of replaceable seams carrying a conformance pack.
    ConformanceCoveragePct: float
    /// Minimum public XML-doc coverage percentage over the tracked surface.
    DocCoveragePct: float
    /// Maximum open `[<Obsolete>]` markers on the public surface.
    OpenDeprecations: int
}

/// The thresholds a tree is graded against when `v1-readiness.json` is
/// absent. Doc coverage at 100% is the doc-coverage policy's own sentence
/// ("the 1.0 surface should be the documented surface"); conformance at
/// 100% is GP 12 read literally; the rest are the shard's.
let defaults = {
    StableReleases = 2
    ConformanceCoveragePct = 100.0
    DocCoveragePct = 100.0
    OpenDeprecations = 0
}

/// The file the thresholds are declared in, relative to the repo root.
let thresholdsFileName = "v1-readiness.json"

let private knownKeys =
    set [
        "stableReleases"
        "conformanceCoveragePct"
        "docCoveragePct"
        "openDeprecations"
    ]

/// Parse a thresholds file. Every key is optional and falls back to
/// `defaults`; an UNKNOWN key is refused, because a misspelt threshold
/// that silently reads as the default is a threshold nobody set.
let parseThresholds (json: string) : Result<Thresholds, string> =
    try
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            Error(sprintf "%s must be a JSON object" thresholdsFileName)
        else
            let unknown =
                root.EnumerateObject()
                |> Seq.map _.Name
                |> Seq.filter (fun k -> not (Set.contains k knownKeys))
                |> List.ofSeq

            match unknown with
            | _ :: _ ->
                Error(
                    sprintf
                        "%s carries unknown key(s) %s — the known keys are %s"
                        thresholdsFileName
                        (String.Join(", ", unknown))
                        (String.Join(", ", knownKeys))
                )
            | [] ->
                let tryProp (name: string) =
                    match root.TryGetProperty name with
                    | true, v -> Some v
                    | _ -> None

                let int' name fallback =
                    match tryProp name with
                    | Some v when v.ValueKind = JsonValueKind.Number -> Ok(v.GetInt32())
                    | Some _ -> Error(sprintf "%s.%s must be an integer" thresholdsFileName name)
                    | None -> Ok fallback

                let float' name fallback =
                    match tryProp name with
                    | Some v when v.ValueKind = JsonValueKind.Number -> Ok(v.GetDouble())
                    | Some _ -> Error(sprintf "%s.%s must be a number" thresholdsFileName name)
                    | None -> Ok fallback

                match
                    int' "stableReleases" defaults.StableReleases,
                    float' "conformanceCoveragePct" defaults.ConformanceCoveragePct,
                    float' "docCoveragePct" defaults.DocCoveragePct,
                    int' "openDeprecations" defaults.OpenDeprecations
                with
                | Ok s, Ok c, Ok d, Ok o ->
                    Ok {
                        StableReleases = s
                        ConformanceCoveragePct = c
                        DocCoveragePct = d
                        OpenDeprecations = o
                    }
                | Error e, _, _, _
                | _, Error e, _, _
                | _, _, Error e, _
                | _, _, _, Error e -> Error e
    with :? JsonException as e ->
        Error(sprintf "%s is not valid JSON: %s" thresholdsFileName e.Message)

// ─── Rows ────────────────────────────────────────────────────────────

/// How a row scored. `NotYet` is an input the tree could not supply —
/// it renders distinctly so the reader knows WHY the row is red, and it
/// counts as failing so the scorecard cannot pass on a missing input.
type Verdict =
    | Pass
    | Fail
    | NotYet

/// One scorecard row.
type Row = {
    /// Stable machine id (`baseline-stable`, `doc-coverage`, …).
    Id: string
    /// The precondition, in one sentence.
    Check: string
    /// The measured value, rendered.
    Measured: string
    /// The threshold it was graded against, rendered.
    Threshold: string
    Verdict: Verdict
    /// What the reader does about it, or why the input was unavailable.
    Note: string
}

/// An input as the target observed it. The target never throws a missing
/// artefact at a check; it says so, and the check says so in its row.
type Input<'a> =
    | Available of 'a
    | Unavailable of string

let private notYet id check threshold why = {
    Id = id
    Check = check
    Measured = "—"
    Threshold = threshold
    Verdict = NotYet
    Note = "Input unavailable: " + why
}

let private pct (numerator: int) (denominator: int) =
    if denominator = 0 then
        0.0
    else
        100.0 * float numerator / float denominator

let private pctText (numerator: int) (denominator: int) =
    sprintf "%d / %d (%.1f%%)" numerator denominator (pct numerator denominator)

let private pctThreshold (t: float) = sprintf "≥ %.1f%%" t

// ─── 1. baseline-stable ──────────────────────────────────────────────

let private stableId = "baseline-stable"

let private stableCheck =
    "API baselines unchanged for N consecutive releases (the surface has stopped moving)"

/// Consecutive releases, newest first, whose committed `api-baselines/`
/// equal the working tree's. The input is `(tag, identicalToTree)` pairs
/// in newest-first order; the count stops at the first release that
/// differs, because a release BEHIND a change is not evidence of
/// stability across it.
let stableReleaseCount (releasesNewestFirst: (string * bool) list) : int =
    releasesNewestFirst |> List.takeWhile snd |> List.length

/// The stability row.
let baselineStability (threshold: int) (releasesNewestFirst: Input<(string * bool) list>) : Row =
    let thresholdText = sprintf "≥ %d release(s)" threshold

    match releasesNewestFirst with
    | Unavailable why -> notYet stableId stableCheck thresholdText why
    | Available [] -> notYet stableId stableCheck thresholdText "no release tag is reachable from HEAD"
    | Available releases ->
        let stable = stableReleaseCount releases
        let newest = releases |> List.head |> fst

        let note =
            if stable >= threshold then
                sprintf
                    "The working tree's baselines match the last %d release(s), back to and including %s."
                    stable
                    (releases |> List.item (stable - 1) |> fst)
            elif stable = 0 then
                sprintf
                    "The working tree's baselines differ from the newest release (%s): the surface moved since it. Every release from here to 1.0 restarts the count."
                    newest
            else
                sprintf
                    "Stable for %d release(s) only; the surface moved at %s."
                    stable
                    (releases |> List.item stable |> fst)

        {
            Id = stableId
            Check = stableCheck
            Measured = sprintf "%d release(s)" stable
            Threshold = thresholdText
            Verdict = (if stable >= threshold then Pass else Fail)
            Note = note
        }

// ─── 2. conformance-coverage ─────────────────────────────────────────

let private conformanceId = "conformance-coverage"

let private conformanceCheck =
    "Every replaceable seam (public interface, ≥ 2 production implementations) carries a conformance pack"

/// The conformance row over `(packedSeams, replaceableSeams)`.
let conformanceCoverage (thresholdPct: float) (counts: Input<int * int>) : Row =
    match counts with
    | Unavailable why -> notYet conformanceId conformanceCheck (pctThreshold thresholdPct) why
    | Available(packed, total) ->
        let measured = pct packed total
        let passes = total > 0 && measured >= thresholdPct

        {
            Id = conformanceId
            Check = conformanceCheck
            Measured = pctText packed total
            Threshold = pctThreshold thresholdPct
            Verdict = (if passes then Pass else Fail)
            Note =
                if total = 0 then
                    "No replaceable seam was derived from the checkout — the api-baselines or the Contracts directory are empty, which is not a pass."
                elif passes then
                    "Every derived seam is packed."
                else
                    sprintf
                        "%d seam(s) carry no pack. The committed ratchet lists them: src/ToolUp.Platform.Tests/Contracts/conformance-coverage.approved.txt ([UNPACKED])."
                        (total - packed)
        }

// ─── 3. doc-coverage ─────────────────────────────────────────────────

let private docId = "doc-coverage"

let private docCheck = "Public XML-doc coverage over the tracked public surface"

let private coverageLine = Regex(@"^(\S+)\s+(\d+)/(\d+)\s*$", RegexOptions.Compiled)

/// Parse Phase 261's sidecar: `<assembly> <documented>/<total>` per
/// non-comment line. Any other non-blank line is refused — a sidecar
/// half-read would report a coverage nobody measured.
let parseDocCoverage (text: string) : Result<(string * int * int) list, string> =
    let lines =
        text.Replace("\r\n", "\n").Split('\n')
        |> Array.map _.Trim()
        |> Array.filter (fun l -> l <> "" && not (l.StartsWith "#"))

    let parsed =
        lines
        |> Array.map (fun l ->
            let m = coverageLine.Match l

            if m.Success then
                Ok(m.Groups[1].Value, int m.Groups[2].Value, int m.Groups[3].Value)
            else
                Error(sprintf "unreadable doc-coverage line: '%s'" l))

    let firstError =
        parsed
        |> Array.tryPick (function
            | Error e -> Some e
            | Ok _ -> None)

    match firstError with
    | Some e -> Error e
    | None when Array.isEmpty parsed -> Error "the doc-coverage sidecar carries no assembly lines"
    | None -> Ok(parsed |> Array.choose Result.toOption |> List.ofArray)

/// The doc-coverage row over the sidecar's text.
let docCoverage (thresholdPct: float) (sidecar: Input<string>) : Row =
    match sidecar with
    | Unavailable why -> notYet docId docCheck (pctThreshold thresholdPct) why
    | Available text ->
        match parseDocCoverage text with
        | Error e -> notYet docId docCheck (pctThreshold thresholdPct) e
        | Ok rows ->
            let documented = rows |> List.sumBy (fun (_, d, _) -> d)
            let total = rows |> List.sumBy (fun (_, _, t) -> t)
            let measured = pct documented total
            let passes = total > 0 && measured >= thresholdPct

            {
                Id = docId
                Check = docCheck
                Measured = pctText documented total
                Threshold = pctThreshold thresholdPct
                Verdict = (if passes then Pass else Fail)
                Note =
                    if passes then
                        sprintf "Measured over %d assemblies." rows.Length
                    else
                        sprintf
                            "%d public subject(s) across %d assemblies carry no XML doc. The per-assembly floor is api-baselines/doc-coverage.approved.txt; the policy is docs/platform/doc-coverage.md."
                            (total - documented)
                            rows.Length
            }

// ─── 4. undecided-renames ────────────────────────────────────────────

let private renamesId = "undecided-renames"

let private renamesCheck =
    "Every parked rename carries a decision (Phase 256's decision table)"

/// The wordings that count as a DECISION. Anything else — an empty cell,
/// "undecided", "TBD", "escalated", a hedge nobody taught this list — is
/// undecided. Fail-closed on purpose: the cost of a false red is one
/// wording added here; the cost of a false green is a 1.0 that froze a
/// name nobody decided to keep.
let private decidedPhrases = [
    "deferred-to-"
    "deferred to "
    "resolved"
    "shipped"
    "done"
    "kept"
    "applied"
    "renamed"
    "removed"
    "withdrawn"
    "accepted"
]

/// True when a Decision cell states a decision.
let isDecided (cell: string) =
    let lower = cell.Trim().ToLowerInvariant()
    lower <> "" && decidedPhrases |> List.exists lower.Contains

let private cells (row: string) =
    let trimmed = row.Trim()
    let inner = trimmed.Substring(1, trimmed.Length - 2)
    inner.Split('|') |> Array.map _.Trim()

let private isTableRow (line: string) =
    let t = line.Trim()
    t.StartsWith "|" && t.EndsWith "|" && t.Length >= 2

let private isSeparatorRow (line: string) =
    isTableRow line
    && (cells line
        |> Array.forall (fun c -> c <> "" && c |> Seq.forall (fun ch -> ch = '-' || ch = ':')))

/// Find every markdown table in `markdown` whose header row contains a
/// column named `column`, returning `(precedingHeading, headerCells, bodyRows)`.
let private tablesWithColumn (column: string) (markdown: string) =
    let lines = markdown.Replace("\r\n", "\n").Split('\n')
    let found = ResizeArray()
    let mutable heading = ""
    let mutable i = 0

    while i < lines.Length do
        let line = lines[i]

        if line.TrimStart().StartsWith "#" then
            heading <- line.Trim()
            i <- i + 1
        elif isTableRow line && i + 1 < lines.Length && isSeparatorRow lines[i + 1] then
            let header = cells line

            if header |> Array.contains column then
                let body = ResizeArray()
                let mutable j = i + 2

                while j < lines.Length && isTableRow lines[j] do
                    body.Add(cells lines[j])
                    j <- j + 1

                found.Add(heading, header, List.ofSeq body)
                i <- j
            else
                i <- i + 1
        else
            i <- i + 1

    List.ofSeq found

/// Parse the rename-decision table — the one whose header carries both an
/// `Item` and a `Decision` column — into `(item, decision)` pairs.
let parseRenameDecisions (markdown: string) : Result<(string * string) list, string> =
    let candidates =
        tablesWithColumn "Decision" markdown
        |> List.filter (fun (_, header, _) -> Array.contains "Item" header)

    match candidates with
    | [] -> Error "no table with `Item` and `Decision` columns was found"
    | (_, header, body) :: _ ->
        let item = Array.findIndex ((=) "Item") header
        let decision = Array.findIndex ((=) "Decision") header

        body
        |> List.map (fun row ->
            let at i = if i < row.Length then row[i] else ""

            at item, at decision)
        |> Ok

/// The renames row over the migration doc's text.
let undecidedRenames (doc: Input<string>) : Row =
    let threshold = "= 0"

    match doc with
    | Unavailable why -> notYet renamesId renamesCheck threshold why
    | Available markdown ->
        match parseRenameDecisions markdown with
        | Error e -> notYet renamesId renamesCheck threshold e
        | Ok [] -> notYet renamesId renamesCheck threshold "the decision table has no rows"
        | Ok rows ->
            let undecided = rows |> List.filter (fun (_, d) -> not (isDecided d))

            {
                Id = renamesId
                Check = renamesCheck
                Measured = sprintf "%d of %d undecided" undecided.Length rows.Length
                Threshold = threshold
                Verdict = (if List.isEmpty undecided then Pass else Fail)
                Note =
                    if List.isEmpty undecided then
                        "Every row states a decision."
                    else
                        undecided
                        |> List.map (fun (item, d) -> sprintf "%s → \"%s\"" item (if d = "" then "(empty)" else d))
                        |> String.concat "; "
                        |> sprintf
                            "Undecided: %s. Decide each in the table (docs/migrations/256-public-surface-minimization.md)."
            }

// ─── 5. adoption-pending ─────────────────────────────────────────────

let private adoptionId = "adoption-pending"

let private adoptionCheck =
    "The declared consumers' columns of the generated adoption matrix carry no pending (🟡) cell"

/// The environment variable naming the generated adoption matrix (a
/// path). The matrix is a private artefact and lives outside this
/// repository, which is why it is supplied rather than located.
let matrixEnvVar = "TOOLUP_ADOPTION_MATRIX"

/// The environment variable declaring the consumer set: names as the
/// matrix's column headers spell them, separated by `;` or `,`. Supplied
/// beside the matrix, for the same reason: a consumer's name is a fact
/// about a private product, and neither the committed thresholds file
/// nor the generated document may carry one.
let consumersEnvVar = "TOOLUP_ADOPTION_CONSUMERS"

/// Parse the consumer declaration: split, trim, drop blanks, keep order,
/// drop duplicates.
let parseConsumers (declaration: string) : string list =
    if isNull declaration then
        []
    else
        declaration.Split([| ';'; ',' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        |> Array.filter (fun s -> s <> "")
        |> List.ofArray
        |> List.distinct

/// The pending marker the generated matrix uses for a refactor a consumer
/// has neither adopted, declared not-applicable, nor deferred with a reason.
let pendingMarker = "🟡"

/// Count pending cells in `consumer`'s column of the matrix grid(s) whose
/// preceding heading names the producer `producer` (case-insensitive
/// substring — the file carries one grid per producer). Refuses a text
/// with no such grid, and a grid with no such column: an absent column
/// read as "zero pending" would pass a consumer the matrix never scored.
let pendingCells (producer: string) (consumer: string) (matrix: string) : Result<int, string> =
    let grids =
        tablesWithColumn "Refactor" matrix
        |> List.filter (fun (heading, _, _) -> heading.Contains(producer, StringComparison.OrdinalIgnoreCase))

    match grids with
    | [] -> Error(sprintf "the matrix carries no `Refactor` grid under a heading naming '%s'" producer)
    | _ ->
        grids
        |> List.fold
            (fun acc (_, header, body) ->
                match acc with
                | Error e -> Error e
                | Ok n ->
                    match Array.tryFindIndex ((=) consumer) header with
                    | None -> Error(sprintf "the '%s' grid has no column for consumer '%s'" producer consumer)
                    | Some col ->
                        let pending =
                            body
                            |> List.filter (fun row -> col < row.Length && row[col].StartsWith pendingMarker)
                            |> List.length

                        Ok(n + pending))
            (Ok 0)

/// Pending cells per declared consumer — the detail the TARGET prints to
/// the console and the rendered row deliberately omits (a consumer's name
/// does not belong in a published document; its count does).
let pendingPerConsumer (consumers: string list) (matrix: string) : (string * Result<int, string>) list =
    consumers |> List.map (fun c -> c, pendingCells "forge" c matrix)

/// The adoption row over the matrix text for every declared consumer.
/// Consumer-blind by construction: it reports how many consumers were
/// declared and how many cells are pending, never a name.
let adoptionPending (consumers: string list) (matrix: Input<string>) : Row =
    let threshold = "= 0"

    match consumers, matrix with
    | [], _ ->
        notYet
            adoptionId
            adoptionCheck
            threshold
            (sprintf "no consumer is declared — set %s beside %s" consumersEnvVar matrixEnvVar)
    | _, Unavailable why -> notYet adoptionId adoptionCheck threshold why
    | _, Available text ->
        let perConsumer = pendingPerConsumer consumers text

        // A refusal names the consumer's POSITION in the declaration, not
        // its name: the note lands in the published document.
        let firstError =
            perConsumer
            |> List.mapi (fun i (name, r) ->
                match r with
                | Error e -> Some(e.Replace(sprintf "'%s'" name, sprintf "#%d" (i + 1)))
                | Ok _ -> None)
            |> List.tryPick id

        match firstError with
        | Some e -> notYet adoptionId adoptionCheck threshold e
        | None ->
            let total =
                perConsumer
                |> List.sumBy (fun (_, r) -> r |> Result.toOption |> Option.defaultValue 0)

            {
                Id = adoptionId
                Check = adoptionCheck
                Measured = sprintf "%d pending across %d declared consumer(s)" total consumers.Length
                Threshold = threshold
                Verdict = (if total = 0 then Pass else Fail)
                Note =
                    if total = 0 then
                        "Every consumer-facing refactor is adopted, not-applicable, or deferred with a reason."
                    else
                        sprintf
                            "%d pending cell(s). A cell flips when the consumer's own adoption manifest records the refactor (adopted / n-a / deferred with a reason); the matrix regenerates from the manifests. The target prints the per-consumer split to the console."
                            total
            }

// ─── 6. open-deprecations ────────────────────────────────────────────

let private deprecationsId = "open-deprecations"

let private deprecationsCheck =
    "Open [<Obsolete>] markers on the public surface — each is a remove-or-carry decision the 1.0 cut takes"

/// The suffix Phase 258's `obsoleteMarker` appends to a deprecated
/// member's token in the rendered baselines. Read here as text because the
/// renderer needs a metadata load context this module deliberately has no
/// dependency on; the two spellings are pinned together by the
/// PublicApiApproval header's cross-reference.
let obsoleteMarkerSuffix = "  (obsolete)"

/// Count the `(obsolete)` markers in one rendered baseline.
let countObsoleteMarkers (baselineText: string) : int =
    baselineText.Replace("\r\n", "\n").Split('\n')
    |> Array.filter (fun l -> l.TrimEnd().EndsWith obsoleteMarkerSuffix)
    |> Array.length

/// The deprecations row over `(baselineFileName, text)` pairs.
let openDeprecations (threshold: int) (baselines: Input<(string * string) list>) : Row =
    let thresholdText = sprintf "≤ %d" threshold

    match baselines with
    | Unavailable why -> notYet deprecationsId deprecationsCheck thresholdText why
    | Available [] -> notYet deprecationsId deprecationsCheck thresholdText "no api-baselines were found"
    | Available files ->
        let perFile =
            files
            |> List.map (fun (name, text) -> name, countObsoleteMarkers text)
            |> List.filter (fun (_, n) -> n > 0)

        let total = perFile |> List.sumBy snd

        {
            Id = deprecationsId
            Check = deprecationsCheck
            Measured = sprintf "%d open" total
            Threshold = thresholdText
            Verdict = (if total <= threshold then Pass else Fail)
            Note =
                if total = 0 then
                    "No public member is deprecated."
                else
                    let assemblyOf (fileName: string) =
                        let stem = Path.GetFileName fileName
                        let suffix = ".approved.txt"

                        if stem.EndsWith suffix then
                            stem.Substring(0, stem.Length - suffix.Length)
                        else
                            stem

                    let listed =
                        perFile
                        |> List.map (fun (name, n) -> sprintf "%s: %d" (assemblyOf name) n)
                        |> String.concat ", "

                    sprintf
                        "%s. Every notice names a replacement and a removal target (Phase 258 gates that on every verified tree); what remains is the decision — remove at the cut (the deprecation window allows it) or carry into 1.x deliberately, raising `openDeprecations` in %s as the record."
                        listed
                        thresholdsFileName
        }

// ─── The scorecard ───────────────────────────────────────────────────

/// True when every row passes. `NotYet` is not a pass.
let ready (rows: Row list) =
    not (List.isEmpty rows) && rows |> List.forall (fun r -> r.Verdict = Pass)

let private verdictText =
    function
    | Pass -> "✅ pass"
    | Fail -> "❌ fail"
    | NotYet -> "⏳ not yet"

let private escapeCell (s: string) =
    s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ")

/// The generated document. `generatedAt` and `treeSha` are stamped by the
/// target, so the same rows render identically under test.
let render (generatedAt: string) (treeSha: string) (thresholdsSource: string) (rows: Row list) : string =
    let sb = StringBuilder()
    let line (s: string) = sb.Append(s).Append('\n') |> ignore
    let failing = rows |> List.filter (fun r -> r.Verdict <> Pass)

    line "# v1.0 readiness scorecard (generated)"
    line ""

    line (
        sprintf
            "Generated by `dotnet run --project Build.fsproj -- V1Readiness` on %s at `%s`, graded against %s."
            generatedAt
            treeSha
            thresholdsSource
    )

    line "**Do not edit by hand** — re-run the target. Each row is one machine-readable check with its measured"
    line "value and the threshold it was graded against; a `⏳ not yet` row is an input this tree could not supply"
    line "and counts as failing. What each row reads, and why, is in the header of `V1Readiness.fs`."
    line ""

    if ready rows then
        line "**Verdict: READY — every precondition passes.**"
    else
        line (
            sprintf
                "**Verdict: NOT READY — %d of %d precondition(s) fail (%s).**"
                failing.Length
                rows.Length
                (failing |> List.map _.Id |> String.concat ", ")
        )

    line ""
    line "| # | Check | Measured | Threshold | Verdict | Note |"
    line "|---|---|---|---|---|---|"

    rows
    |> List.iteri (fun i r ->
        line (
            sprintf
                "| %d | `%s` — %s | %s | %s | %s | %s |"
                (i + 1)
                r.Id
                (escapeCell r.Check)
                (escapeCell r.Measured)
                (escapeCell r.Threshold)
                (verdictText r.Verdict)
                (escapeCell r.Note)
        ))

    line ""
    line "## Regenerating"
    line ""
    line "```powershell"
    line "# The adoption row reads the generated consumer adoption matrix and the declared consumer set,"
    line "# both of which live outside this repository; supply them or the row reports `not yet`."
    line (sprintf "$env:%s = '<path to the generated adoption matrix>'" matrixEnvVar)
    line (sprintf "$env:%s = '<consumer column header>[;<another>]'" consumersEnvVar)
    line "dotnet run --project Build.fsproj -- V1Readiness            # regenerates this file"
    line "dotnet run --project Build.fsproj -- V1Readiness --require-ready   # exit 1 unless every row passes"
    line "```"
    line ""

    line (
        sprintf
            "Thresholds are declared in `%s`; a decision to accept a measured value is a change to that file, recorded in git. The consumer set is declared beside the matrix, outside this repository."
            thresholdsFileName
    )

    sb.ToString()