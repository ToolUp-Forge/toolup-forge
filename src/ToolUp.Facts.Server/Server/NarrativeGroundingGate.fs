// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Globalization
open System.Text.RegularExpressions
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes

// ─── The narrative grounding gate (Phase 985) ────────────────────────
//
// Phase 521 made a fact reference OPTIONAL on a `Metric` span. That is
// right for an authored narrative and wrong for a model-written one: a
// model can state a figure that is plausible, well formatted and wrong.
// The gate below is what a model-written narrative passes before it is
// published, and it refuses the document when ANY number in its content
// is not a reference that:
//
//   1. resolves — a Fact id the scope holds, or `<kind>:<id>` for a kind a
//      deployment registered (`ICitableReferenceKind`); an unregistered
//      kind is refused by name;
//   2. may be disclosed at the publishing surface (the one Phase 525
//      predicate, through `IFactDisclosureGate`, so the deny is audited
//      like every other door's);
//   3. is current — the head of its lineage, not a superseded Fact;
//   4. states the value it refers to — compared by number at the
//      precision the narrative states it, so "1.25m" for 1,248,300 passes
//      and "1.3m" does not.
//
// **What counts as a number.** A numeral anywhere in prose (any Unicode
// number character, so `½` and `²` count), or a number written in words
// (`twelve`, `million`, `doubled`). Both are refused outside a `Metric`
// span, and a span's LABEL is prose too — only its value may carry the
// figure. The word list is a policy value (`GroundingPolicy.NumberWords`)
// a deployment can extend for another language. Two whole numbers pass
// without a Fact (`NarrativeGrounding.checkWith`): one the wording in force
// asked for, and, at the repair loop's late stage only, a count of what
// the document itself lists.
//
// **What is checked.** The document's CONTENT: every element of every
// section, recursively (cards, accordions, tabs), tables and lists cell by
// cell, captions, alt text, code. The title, subtitle and section headings
// are STRUCTURE the deployment authors — the grounded run takes them from
// the registered shape, never from the model — and are not inspected. A
// `Component` block's props are prose, except a `factRefs` prop: a
// comma-separated list of references a chart component draws its values
// from, which are checked as citations (resolve, disclosable, current) with
// no stated value to compare.
//
// **The computed-percentage decision (985.F).** A figure computed from two
// Facts — "margin was 12.5%" from revenue and cost — is REFUSED. It is not
// a reference to anything: stated as prose it is an unreferenced number,
// and stated as a span citing one of its inputs it misstates that input.
// A deployment that wants to publish a derived figure asserts it as a Fact
// first (a `Computed` method naming its inputs), which gives it an id, a
// lineage, a disclosure stance and a supersession edge — everything the
// gate checks. Admitting "a declared derivation of named Facts" instead
// would put a second arithmetic engine inside the gate, whose rounding and
// formula a reader would have to trust without a Fact to trace it to.

/// What one reference resolved to, as the pure check reads it.
type GroundingResolution =
    /// Current and disclosable. `Renderings` are the forms it may be stated
    /// as; `Value` its number when it has one; `PercentAsFraction` says a
    /// stated `%` reads the value as a fraction (0.125 ⇒ 12.5%).
    | GroundingCurrent of renderings: string list * value: decimal option * percentAsFraction: bool
    | GroundingUnresolved
    | GroundingSuperseded of supersededBy: string option
    | GroundingWithheld of policyRef: string
    | GroundingUnregisteredKind of kind: string

/// The tunable half of the gate. Deliberately small: what a number IS may
/// vary by language; what a grounded number must be does not.
type GroundingPolicy = {
    /// Lower-case words that state a quantity. A word in prose matching one
    /// is an unreferenced number.
    NumberWords: Set<string>
}

module NarrativeGrounding =

    /// The reference kind of a Fact id.
    [<Literal>]
    let FactKind = NarrativeCitation.FactKind

    /// The `Component` prop carrying the references a chart draws from.
    [<Literal>]
    let FactRefsProp = "factRefs"

    /// English cardinals, scale words and multiplicatives. Ordinals
    /// ("first", "third") are left out on purpose: they order, they do not
    /// quantify. "one" is in: "one percent" is a figure, and refusing "one
    /// of the strongest weeks" costs a rewording, never a wrong number.
    let englishNumberWords: Set<string> =
        set [
            "zero"
            "one"
            "two"
            "three"
            "four"
            "five"
            "six"
            "seven"
            "eight"
            "nine"
            "ten"
            "eleven"
            "twelve"
            "thirteen"
            "fourteen"
            "fifteen"
            "sixteen"
            "seventeen"
            "eighteen"
            "nineteen"
            "twenty"
            "thirty"
            "forty"
            "fifty"
            "sixty"
            "seventy"
            "eighty"
            "ninety"
            "hundred"
            "hundreds"
            "thousand"
            "thousands"
            "million"
            "millions"
            "billion"
            "billions"
            "trillion"
            "dozen"
            "dozens"
            "percent"
            "twice"
            "double"
            "doubled"
            "triple"
            "tripled"
            "quadrupled"
            "halved"
        ]

    let defaultPolicy: GroundingPolicy = { NumberWords = englishNumberWords }

    let private wordPattern = Regex(@"\p{L}+", RegexOptions.CultureInvariant)

    /// Does `text` state a quantity — a numeral, or a number word?
    let statesNumber (policy: GroundingPolicy) (text: string) : bool =
        if String.IsNullOrEmpty text then
            false
        elif text |> Seq.exists Char.IsNumber then
            true
        else
            wordPattern.Matches(text)
            |> Seq.exists (fun m -> policy.NumberWords.Contains(m.Value.ToLowerInvariant()))

    // ── Numbers a reader never mistakes for a figure ────────────────

    /// One number stated in a run of text, as the two admissions read it.
    type NumberMention = {
        /// Its value when it is a WHOLE number (digits alone, or an English
        /// cardinal from zero to ninety-nine) and nothing marks it as a
        /// figure. `None` for a percentage, a currency amount, a fraction, a
        /// decimal, a signed or grouped figure, an ordinal, a scale word
        /// ("million") or a multiplicative ("doubled"): no admission reaches
        /// those.
        Whole: int option
        /// The words that follow it up to the end of its noun phrase,
        /// stopped by punctuation, another number, a function word or the
        /// fourth word, lower-cased and singularised. "three largest
        /// categories all fell" reads `["largest"; "category"]`.
        Following: string list
    }

    let private cardinals: Map<string, int> =
        [
            "zero"
            "one"
            "two"
            "three"
            "four"
            "five"
            "six"
            "seven"
            "eight"
            "nine"
            "ten"
            "eleven"
            "twelve"
            "thirteen"
            "fourteen"
            "fifteen"
            "sixteen"
            "seventeen"
            "eighteen"
            "nineteen"
        ]
        |> List.mapi (fun i w -> w, i)
        |> Map.ofList

    let private tens: Map<string, int> =
        [ "twenty"; "thirty"; "forty"; "fifty"; "sixty"; "seventy"; "eighty"; "ninety" ]
        |> List.mapi (fun i w -> w, (i + 2) * 10)
        |> Map.ofList

    /// A cardinal written in words: "seven", "forty", "twenty-one".
    let private cardinalValue (word: string) : int option =
        match word.Split '-' with
        | [| w |] ->
            match cardinals.TryFind w with
            | Some v -> Some v
            | None -> tens.TryFind w
        | [| t; u |] ->
            match tens.TryFind t, cardinals.TryFind u with
            | Some t, Some u when u >= 1 && u <= 9 -> Some(t + u)
            | _ -> None
        | _ -> None

    /// Words that end a noun phrase.
    let private phraseStops: Set<string> =
        set [
            "a"
            "all"
            "an"
            "and"
            "are"
            "as"
            "at"
            "be"
            "been"
            "both"
            "but"
            "by"
            "each"
            "for"
            "from"
            "had"
            "has"
            "have"
            "in"
            "into"
            "is"
            "its"
            "of"
            "on"
            "or"
            "over"
            "per"
            "than"
            "that"
            "the"
            "their"
            "these"
            "this"
            "those"
            "to"
            "was"
            "were"
            "which"
            "while"
            "who"
            "with"
        ]

    /// Nouns that make the number before them a currency amount.
    let private currencyNouns: Set<string> =
        set [
            "cent"
            "dollar"
            "euro"
            "franc"
            "penny"
            "pence"
            "pound"
            "rupee"
            "yen"
            "yuan"
        ]

    /// `word` lower-cased and read in the singular: "categories" is
    /// "category", "boxes" is "box", "markets" is "market"; "business" stays.
    let singular (word: string) : string =
        let w = word.ToLowerInvariant()

        if w.Length > 4 && w.EndsWith "ies" then
            w.Substring(0, w.Length - 3) + "y"
        elif
            w.Length > 4
            && (w.EndsWith "sses"
                || w.EndsWith "shes"
                || w.EndsWith "ches"
                || w.EndsWith "xes"
                || w.EndsWith "zes")
        then
            w.Substring(0, w.Length - 2)
        elif
            w.Length > 2
            && w.EndsWith "s"
            && not (w.EndsWith "ss")
            && not (w.EndsWith "us")
            && not (w.EndsWith "is")
        then
            w.Substring(0, w.Length - 1)
        else
            w

    let private tokenPattern =
        Regex(@"(?<num>\p{N}+)|(?<word>\p{L}+(?:[-'’]\p{L}+)*)|(?<other>\S)", RegexOptions.CultureInvariant)

    type private Token =
        | NumToken of text: string * start: int * finish: int
        | WordToken of text: string
        | OtherToken

    /// Every number `text` states, in order: each numeral run and each number
    /// word `policy` names, which are exactly the numbers `statesNumber` sees.
    let mentions (policy: GroundingPolicy) (text: string) : NumberMention list =
        if String.IsNullOrEmpty text then
            []
        else
            let tokens =
                tokenPattern.Matches(text)
                |> Seq.map (fun m ->
                    if m.Groups["num"].Success then
                        NumToken(m.Value, m.Index, m.Index + m.Length)
                    elif m.Groups["word"].Success then
                        WordToken m.Value
                    else
                        OtherToken)
                |> Array.ofSeq

            let isNumberWord (word: string) =
                word.ToLowerInvariant().Split([| '-'; '\''; '’' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.exists policy.NumberWords.Contains

            let wordAt (k: int) =
                if k >= 0 && k < tokens.Length then
                    match tokens[k] with
                    | WordToken w -> Some w
                    | _ -> None
                else
                    None

            let following (i: int) : string list =
                let rec go j acc =
                    match wordAt j with
                    | Some w when
                        List.length acc < 4
                        && not (isNumberWord w)
                        && not (phraseStops.Contains(w.ToLowerInvariant()))
                        ->
                        go (j + 1) (singular w :: acc)
                    | _ -> List.rev acc

                go (i + 1) []

            // "3 percent", "three per cent", "40 pounds": a percentage or a
            // currency amount written with a word after it.
            let unitAfter (i: int) =
                match wordAt (i + 1) |> Option.map singular with
                | Some "percent" -> true
                | Some "per" -> wordAt (i + 2) |> Option.map singular = Some "cent"
                | Some noun -> currencyNouns.Contains noun
                | None -> false

            let charAt (k: int) =
                if k >= 0 && k < text.Length then Some text[k] else None

            // Digits joined to anything but space or ordinary punctuation are
            // a figure: "£40", "-3", "1,200", "3.5", "3%", "1/3", "3m", "3rd",
            // "Q3"; so are digits after a currency code ("GBP 40").
            let markedAsFigure (i: int) (start: int) (finish: int) =
                let joinedBefore =
                    match charAt (start - 1) with
                    | Some c -> Char.IsLetterOrDigit c || Char.IsSymbol c || "-−+.,/".Contains c
                    | None -> false

                let joinedAfter =
                    match charAt finish with
                    | Some c when Char.IsLetterOrDigit c || Char.IsSymbol c || "%‰/".Contains c -> true
                    | Some c when ".,".Contains c -> charAt (finish + 1) |> Option.exists Char.IsDigit
                    | _ -> false

                let currencyCode =
                    match wordAt (i - 1) with
                    | Some w -> w.Length = 3 && w |> Seq.forall Char.IsUpper
                    | None -> false

                joinedBefore || joinedAfter || currencyCode || unitAfter i

            tokens
            |> Array.mapi (fun i token ->
                match token with
                | NumToken(digits, start, finish) ->
                    let whole =
                        if
                            digits |> Seq.forall (fun c -> c >= '0' && c <= '9')
                            && not (markedAsFigure i start finish)
                        then
                            match Int32.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture) with
                            | true, v -> Some v
                            | _ -> None
                        else
                            None

                    Some {
                        Whole = whole
                        Following = following i
                    }
                | WordToken word when isNumberWord word ->
                    Some {
                        Whole =
                            if unitAfter i then
                                None
                            else
                                cardinalValue (word.ToLowerInvariant())
                        Following = following i
                    }
                | _ -> None)
            |> Array.choose id
            |> List.ofArray

    /// Does the instruction state `mention`'s number counting the same thing?
    /// Some number in `instruction` has the same whole value, and the last
    /// word of ITS noun phrase (the noun it counts) is among the words that
    /// follow `mention`: "the three largest categories" echoes "report on the
    /// top 3 categories"; "three markets" and "four categories" do not.
    let echoes (instruction: NumberMention list) (mention: NumberMention) : bool =
        match mention.Whole with
        | None -> false
        | Some value ->
            instruction
            |> List.exists (fun asked ->
                asked.Whole = Some value
                && (match List.tryLast asked.Following with
                    | Some noun -> List.contains noun mention.Following
                    | None -> false))

    /// The kind a reference names: `"fact"` for a bare Fact id, the prefix
    /// for `<kind>:<id>`. A Fact id is content-addressed hex and never
    /// carries a `:`.
    let referenceKind (reference: string) : string * string =
        match reference.IndexOf ':' with
        | i when i > 0 -> reference.Substring(0, i), reference.Substring(i + 1)
        | _ -> FactKind, reference

    // ── Claims ──────────────────────────────────────────────────────

    /// One thing in a document the gate must judge.
    type Claim =
        /// A prose run that may state a number.
        | ProseClaim of location: string * text: string
        /// A metric span: its label is prose, its value the stated figure.
        | MetricClaim of location: string * label: string * value: string * reference: string option
        /// A reference with no stated value (a chart component's `factRefs`).
        | CitationClaim of location: string * reference: string

    let private spanClaims (at: string) (spans: InlineSpan list) : Claim list =
        let rec go (prefix: string) (spans: InlineSpan list) =
            spans
            |> List.mapi (fun j span ->
                let loc = sprintf "%s › span %d" prefix (j + 1)

                match span with
                | Text s
                | Emphasis s
                | Strong s
                | Code s -> [ ProseClaim(loc, s) ]
                | Metric(label, value, reference) -> [ MetricClaim(loc, label, value, reference) ]
                | Link(_, inner) -> go loc inner
                | Image(_, alt, title) ->
                    ProseClaim(loc, alt)
                    :: (title |> Option.map (fun t -> ProseClaim(loc, t)) |> Option.toList)
                | Br -> [])
            |> List.concat

        go at spans

    let private optProse (loc: string) (text: string option) : Claim list =
        text |> Option.map (fun t -> ProseClaim(loc, t)) |> Option.toList

    let private splitRefs (value: string) : string list =
        value.Split([| ','; ' '; ';'; '\n'; '\r'; '\t' |], StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray

    /// Where a claim sits: its section, the element that holds it (by that
    /// element's location) and the element that follows it there.
    type private ClaimContext = {
        Section: string
        At: string
        Next: NarrativeElement option
    }

    let rec private elementClaims
        (section: string)
        (at: string)
        (next: NarrativeElement option)
        (element: NarrativeElement)
        : (Claim * ClaimContext) list =
        let own (claims: Claim list) =
            let context = {
                Section = section
                At = at
                Next = next
            }

            claims |> List.map (fun c -> c, context)

        match element with
        | Paragraph spans -> own (spanClaims at spans)
        | Heading(_, spans) -> own (spanClaims at spans)
        | BulletList items
        | OrderedList items ->
            items
            |> List.mapi (fun k item -> spanClaims (sprintf "%s › item %d" at (k + 1)) item)
            |> List.concat
            |> own
        | KeyValueGrid pairs ->
            pairs
            |> List.mapi (fun k (key, spans) ->
                let loc = sprintf "%s › entry %d" at (k + 1)
                ProseClaim(loc, key) :: spanClaims loc spans)
            |> List.concat
            |> own
        | Table(columns, rows) ->
            let headers =
                columns
                |> List.mapi (fun c (h, _) -> ProseClaim(sprintf "%s › header %d" at (c + 1), h))

            let cells =
                rows
                |> List.mapi (fun r row ->
                    row
                    |> List.mapi (fun c cell -> spanClaims (sprintf "%s › row %d › column %d" at (r + 1) (c + 1)) cell)
                    |> List.concat)
                |> List.concat

            own (headers @ cells)
        | Callout(_, spans) -> own (spanClaims at spans)
        | CodeBlock(_, content) -> own [ ProseClaim(at, content) ]
        | Blockquote(citation, spans) -> own (optProse at citation @ spanClaims at spans)
        | Divider -> []
        | Video spec ->
            own (
                optProse at spec.Caption
                @ (spec.Tracks |> List.map (fun t -> ProseClaim(at, t.Label)))
            )
        | Audio spec ->
            own (
                optProse at spec.Caption
                @ (spec.Tracks |> List.map (fun t -> ProseClaim(at, t.Label)))
            )
        | ImageGallery images ->
            images
            |> List.mapi (fun k img ->
                let loc = sprintf "%s › image %d" at (k + 1)
                ProseClaim(loc, img.Alt) :: optProse loc img.Caption)
            |> List.concat
            |> own
        | Embed spec -> own [ ProseClaim(at, spec.Title) ]
        | Card spec ->
            own (
                optProse at spec.Heading
                @ (spec.Image
                   |> Option.map (fun img -> ProseClaim(at, img.Alt) :: optProse at img.Caption)
                   |> Option.defaultValue [])
            )
            @ bodyClaims section at spec.Body
        | Accordion panels
        | Tabs panels ->
            panels
            |> List.mapi (fun k (label, body) ->
                let loc = sprintf "%s › panel %d" at (k + 1)
                own [ ProseClaim(loc, label) ] @ bodyClaims section loc body)
            |> List.concat
        | Component(name, props) ->
            let loc = sprintf "%s › component '%s'" at name

            props
            |> Map.toList
            |> List.collect (fun (key, value) ->
                if key = FactRefsProp then
                    splitRefs value |> List.map (fun r -> CitationClaim(loc, r))
                else
                    [ ProseClaim(sprintf "%s › prop '%s'" loc key, value) ])
            |> own

    and private bodyClaims
        (section: string)
        (at: string)
        (elements: NarrativeElement list)
        : (Claim * ClaimContext) list =
        elements
        |> List.mapi (fun i e ->
            elementClaims section (sprintf "%s › element %d" at (i + 1)) (List.tryItem (i + 1) elements) e)
        |> List.concat

    /// Every claim in the document's content with where it sits, in
    /// document order.
    let private locatedClaims (document: NarrativeDocument) : (Claim * ClaimContext) list =
        document.Sections
        |> List.collect (fun section -> bodyClaims section.Id (sprintf "section '%s'" section.Id) section.Elements)

    /// Every claim in the document's content, in document order.
    let claims (document: NarrativeDocument) : Claim list = locatedClaims document |> List.map fst

    /// The distinct references the document cites, first-seen order.
    let references (document: NarrativeDocument) : string list =
        claims document
        |> List.choose (function
            | MetricClaim(_, _, _, Some reference)
            | CitationClaim(_, reference) -> Some reference
            | _ -> None)
        |> List.distinct

    // ── Stated values ───────────────────────────────────────────────

    /// A stated figure read as a number: its value in base units, the
    /// half-unit of its last stated digit (also in base units), and whether
    /// it was stated as a percentage. `None` when the text is not a plain
    /// figure (it must then equal a rendering verbatim).
    let parseStated (stated: string) : (decimal * decimal * bool) option =
        let s = stated.Trim()

        if s = "" then
            None
        else
            let negative, rest =
                match s.[0] with
                | '-'
                | '−' -> true, s.Substring(1).TrimStart()
                | _ -> false, s
            // A leading currency symbol or code ("£", "$", "GBP ") — and
            // nothing else: a stated value that opens with words is not a
            // plain figure.
            let prefix =
                rest
                |> Seq.takeWhile (fun c -> not (Char.IsDigit c) && c <> '.')
                |> Seq.toArray
                |> String

            let prefixIsCurrency =
                let p = prefix.Trim()

                p = ""
                || (p |> Seq.forall (fun c -> not (Char.IsLetterOrDigit c)))
                || (p.Length = 3 && p |> Seq.forall Char.IsUpper)

            let body = rest.Substring(prefix.Length)

            let mantissa =
                body
                |> Seq.takeWhile (fun c -> Char.IsDigit c || c = ',' || c = '.')
                |> Seq.toArray
                |> String

            let suffix = body.Substring(mantissa.Length).Trim().ToLowerInvariant()
            let digits = mantissa.Replace(",", "")

            let scale, percent =
                match suffix with
                | "" -> Some 1m, false
                | "k" -> Some 1_000m, false
                | "m"
                | "mn" -> Some 1_000_000m, false
                | "b"
                | "bn" -> Some 1_000_000_000m, false
                | "%" -> Some 1m, true
                | _ -> None, false

            match scale, Decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) with
            | Some scale, (true, number) when digits <> "" && prefixIsCurrency ->
                let places =
                    match digits.IndexOf '.' with
                    | -1 -> 0
                    | i -> digits.Length - i - 1

                let halfUnit = 0.5m * scale / pown 10m places
                let value = (if negative then -number else number) * scale
                Some(value, halfUnit, percent)
            | _ -> None

    let private normalise (s: string) : string =
        String.Join(" ", s.Split([| ' '; '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries))

    /// Does `stated` state the resolved value? A verbatim rendering always
    /// does; otherwise the stated figure, rounded at the precision it is
    /// stated to, must equal the value.
    let statesValue
        (stated: string)
        (renderings: string list)
        (value: decimal option)
        (percentAsFraction: bool)
        : bool =
        let n = normalise stated

        if renderings |> List.exists (fun r -> normalise r = n) then
            true
        else
            match value, parseStated stated with
            | Some v, Some(figure, halfUnit, percent) ->
                let target = if percent && percentAsFraction then v * 100m else v
                abs (figure - target) <= halfUnit
            | _ -> false

    // ── The check ───────────────────────────────────────────────────

    /// The pure check under `options`: given how every reference resolved
    /// and the subject each current Fact reference is about (`subjectOf`;
    /// `None` for a reference with no subject), the verdict over `document`.
    /// Every offending claim is reported, in document order.
    ///
    /// A number in prose is refused unless EVERY number in that run is
    /// admitted, by one of two admissions, each recorded as a citation:
    ///
    ///  - **the instruction echo**, always applied: a whole number the
    ///    wording in force (the prompt, or the guidance of the section the
    ///    number sits in) states counting the same noun (`echoes`), cited
    ///    `instruction:<wording version>#<section id>`;
    ///  - **the structural count**, applied only at the repair loop's late
    ///    stage (`options.LateStage`): a whole number equal to the distinct
    ///    subjects the `Metric` spans of the same element cite, or to the
    ///    items or rows of the list or table that immediately follows that
    ///    element, cited `structural-count:<location>`.
    ///
    /// No other number becomes admissible: a percentage, a currency amount,
    /// a fraction or a decimal is never a whole number (`mentions`).
    let checkWith
        (policy: GroundingPolicy)
        (resolve: string -> GroundingResolution)
        (subjectOf: string -> string option)
        (options: GroundingCheckOptions)
        (document: NarrativeDocument)
        : NarrativeGroundingVerdict =
        let offence loc claim reason = {
            Location = loc
            Claim = claim
            Reason = reason
        }

        let located = locatedClaims document

        let asked =
            match options.Instruction with
            | None -> fun (_: string) -> []
            | Some instruction ->
                let promptMentions = mentions policy instruction.Prompt

                let bySection =
                    instruction.Guidance |> Map.map (fun _ guidance -> mentions policy guidance)

                fun (section: string) -> promptMentions @ (bySection.TryFind section |> Option.defaultValue [])

        // The distinct subjects each element's Metric spans cite.
        let subjectsByElement =
            located
            |> List.choose (function
                | MetricClaim(_, _, _, Some reference), context ->
                    subjectOf reference |> Option.map (fun subject -> context.At, subject)
                | _ -> None)
            |> List.distinct
            |> List.countBy fst
            |> Map.ofList

        let counts (context: ClaimContext) : int list =
            let subjects = subjectsByElement.TryFind context.At |> Option.toList

            let following =
                match context.Next with
                | Some(BulletList items)
                | Some(OrderedList items) -> [ List.length items ]
                | Some(Table(_, rows)) -> [ List.length rows ]
                | _ -> []

            subjects @ following |> List.filter (fun n -> n > 0)

        // `Some citations` when every number in `text` is admitted (none at
        // all when it states no number); `None` when one is not.
        let admit (context: ClaimContext) (loc: string) (text: string) : NarrativeCitation list option =
            if not (statesNumber policy text) then
                Some []
            else
                match mentions policy text with
                | [] -> None
                | stated ->
                    let instruction = asked context.Section

                    let admitted =
                        stated
                        |> List.map (fun mention ->
                            match options.Instruction, mention.Whole with
                            | Some wording, _ when echoes instruction mention ->
                                Some(NarrativeCitation.instruction wording.Version context.Section)
                            | _, Some n when options.LateStage && List.contains n (counts context) ->
                                Some(NarrativeCitation.structuralCount loc)
                            | _ -> None)

                    if admitted |> List.forall Option.isSome then
                        Some(admitted |> List.choose id)
                    else
                        None

        let judgeReference (loc: string) (claim: string) (reference: string) (stated: string option) =
            match resolve reference with
            | GroundingUnresolved -> [ offence loc claim (UnresolvedReference reference) ]
            | GroundingUnregisteredKind kind -> [ offence loc claim (UnregisteredReferenceKind(kind, reference)) ]
            | GroundingWithheld policyRef -> [ offence loc claim (UndisclosableReference(reference, policyRef)) ]
            | GroundingSuperseded head -> [ offence loc claim (SupersededReference(reference, head)) ]
            | GroundingCurrent(renderings, value, asFraction) ->
                match stated with
                | Some s when not (statesValue s renderings value asFraction) ->
                    let current = renderings |> List.tryHead |> Option.defaultValue "no stated form"
                    [ offence loc claim (MisstatedValue(reference, s, current)) ]
                | _ -> []

        let prose (context: ClaimContext) (loc: string) (text: string) =
            match admit context loc text with
            | Some admissions -> [], admissions
            | None -> [ offence loc text UnreferencedNumber ], []

        let judged =
            located
            |> List.map (fun (claim, context) ->
                match claim with
                | ProseClaim(loc, text) -> prose context loc text
                | MetricClaim(loc, label, value, reference) ->
                    let claim = sprintf "%s = %s" label value
                    let labelOffence, admissions = prose context loc label

                    let valueOffence =
                        match reference with
                        | None -> [ offence loc claim MetricWithoutReference ]
                        | Some reference -> judgeReference loc claim reference (Some value)

                    labelOffence @ valueOffence, admissions
                | CitationClaim(loc, reference) -> judgeReference loc reference reference None, [])

        match judged |> List.collect fst with
        | [] ->
            let references =
                references document
                |> List.map (fun reference ->
                    let kind, _ = referenceKind reference

                    {
                        ReferenceKind = kind
                        Reference = reference
                    })

            references @ (judged |> List.collect snd) |> List.distinct |> Grounded
        | offences -> Ungrounded offences

    /// The pure check: given how every reference resolved, the verdict
    /// over `document`, with no instruction and not at the late stage: a
    /// number in prose is admitted only as a `Metric` span. Every offending
    /// claim is reported, in document order.
    let check
        (policy: GroundingPolicy)
        (resolve: string -> GroundingResolution)
        (document: NarrativeDocument)
        : NarrativeGroundingVerdict =
        checkWith policy resolve (fun _ -> None) GroundingCheckOptions.none document

    /// The Fact ids a verdict cites — what a certificate is issued over.
    let citedFactIds (citations: NarrativeCitation list) : string list = NarrativeCitation.factIds citations

    /// The fact tools: the ones the fact store declares on `ServerApp.AITools`
    /// (`FactsCompose.withFactStore` reads this list), and the only ones a
    /// grounded run gives the model from the fact tier — the run adds the
    /// declared reference tools. The two reads, the two discovery surfaces
    /// (coverage by metric, the table listing by table), then the roll-up:
    /// a total over the population read, summed by the server so the model
    /// never adds figures itself.
    let factTools: (AIToolDefinition * (HttpContext -> string -> Async<string>)) list = [
        FactQueryTool.definition, FactQueryTool.execute
        PopulationQueryTool.definition, PopulationQueryTool.execute
        CoverageTool.definition, CoverageTool.execute
        FactTablesTool.definition, FactTablesTool.execute
        AggregateMetricTool.definition, AggregateMetricTool.execute
    ]

/// The fact tier's `INarrativeGroundingGate` (validator:NarrativeGroundingGate):
/// resolves every reference a document cites against the fact store, the
/// disclosure gate and the registered reference kinds, then applies the
/// pure check. Certifies through the composed grounding-certificate issuer.
type FactNarrativeGroundingGate
    (
        store: IFactStore,
        disclosure: IFactDisclosureGate,
        registry: Grounding.IMetricRegistry option,
        kinds: ICitableReferenceKind list,
        issuer: IGroundingCertificateIssuer,
        policy: GroundingPolicy
    ) =

    let kindsByName = kinds |> List.map (fun k -> k.Kind, k) |> Map.ofList

    let currentOf (fact: Fact) : GroundingResolution =
        let metric = registry |> Option.bind (fun r -> r.TryGetMetric fact.Metric.Value)
        let format = metric |> Option.map _.DisplayFormat |> Option.defaultValue ""
        let rendering = FactRendering.renderFor metric fact.Value

        let value =
            match fact.Value with
            | Scalar d -> Some d
            | _ -> None

        let asFraction =
            format.StartsWith("P", StringComparison.OrdinalIgnoreCase)
            || format.Contains "%"

        GroundingCurrent([ rendering ], value, asFraction)

    /// Each Fact id's resolution, and the subject of each current one.
    let resolveFacts (scope: ResolvedScope) (principal: string) (surface: FactEgressSurface) (factIds: string list) = async {
        match factIds with
        | [] -> return Map.empty, Map.empty
        | ids ->
            let! verdicts = disclosure.Check(scope, principal, surface, ids)

            let! resolved =
                ids
                |> List.map (fun id -> async {
                    let! chain = store.QuerySupersessionChain(scope, id) |> FactStoreResult.orRaiseAsync

                    match List.tryFind (fun (f: Fact) -> f.FactId = id) chain with
                    | None -> return id, GroundingUnresolved, None
                    | Some fact ->
                        match verdicts.TryFind id with
                        | Some FactDisclosable ->
                            let head = List.last chain

                            if head.FactId <> id then
                                return id, GroundingSuperseded(Some head.FactId), None
                            else
                                return id, currentOf fact, Some(SubjectRef.toString fact.Subject)
                        | Some(FactNotDisclosable policyRef) -> return id, GroundingWithheld policyRef, None
                        | None -> return id, GroundingWithheld "unknown-fact", None
                })
                |> Async.Sequential

            let resolutions =
                resolved |> Array.map (fun (id, resolution, _) -> id, resolution) |> Map.ofArray

            let subjects =
                resolved
                |> Array.choose (fun (id, _, subject) -> subject |> Option.map (fun s -> id, s))
                |> Map.ofArray

            return resolutions, subjects
    }

    let resolveDeclared (scopeId: string) (principal: string) (reference: string) = async {
        let kind, id = NarrativeGrounding.referenceKind reference

        match kindsByName.TryFind kind with
        | None -> return reference, GroundingUnregisteredKind kind
        | Some k ->
            let! resolution = k.Resolve(scopeId, principal, id)

            return
                reference,
                match resolution with
                | CitableReferenceCurrent(renderings, value) -> GroundingCurrent(renderings, value, false)
                | CitableReferenceUnresolved -> GroundingUnresolved
                | CitableReferenceSuperseded by -> GroundingSuperseded by
                | CitableReferenceWithheld policyRef -> GroundingWithheld policyRef
    }

    let checkWith
        (scope: ResolvedScope)
        (principal: string)
        (surface: FactEgressSurface)
        (document: NarrativeDocument)
        (options: GroundingCheckOptions)
        =
        async {
            let references = NarrativeGrounding.references document

            let factIds, declared =
                references
                |> List.partition (fun r -> fst (NarrativeGrounding.referenceKind r) = NarrativeGrounding.FactKind)

            let! facts, subjects = resolveFacts scope principal surface factIds

            let! others =
                declared
                |> List.map (resolveDeclared scope.ScopeId principal)
                |> Async.Sequential

            let resolutions =
                Map.fold (fun acc k v -> Map.add k v acc) (Map.ofArray others) facts

            let resolve reference =
                resolutions.TryFind reference |> Option.defaultValue GroundingUnresolved

            return NarrativeGrounding.checkWith policy resolve subjects.TryFind options document
        }

    /// The certificate chain depth — deep enough to reach a Fact's input
    /// data from the narrative at the root.
    static member CertificateDepth = 8

    interface IInstructedNarrativeGroundingGate with
        member _.CheckWith(scope, principal, surface, document, options) =
            checkWith scope principal surface document options

    interface INarrativeGroundingGate with
        member _.Check(scope, principal, surface, document) =
            checkWith scope principal surface document GroundingCheckOptions.none

        member _.Certify(scope, principal, narrativeId, citedFactIds) = async {
            let! issued =
                issuer.Issue(
                    scope,
                    principal,
                    NarrativeCertificate(narrativeId, citedFactIds),
                    FactNarrativeGroundingGate.CertificateDepth
                )

            match issued with
            | Ok certificate ->
                return
                    Ok {
                        Root = certificate.Body.Root
                        Digest = GroundingCertificate.certificateDigest certificate.Body
                        KeyId = certificate.Body.DeploymentKeyId
                        IssuedAt = certificate.Body.IssuedAt
                        CitedFactIds = citedFactIds
                        CertificateJson = CertificateEnvelope.predicateJson certificate
                        Admissions = []
                    }
            | Error error -> return Error(CertificateError.describe error)
        }

module FactNarrativeGroundingGate =
    /// The gate over the composed fact tier, under the default policy.
    let create
        (store: IFactStore)
        (disclosure: IFactDisclosureGate)
        (registry: Grounding.IMetricRegistry option)
        (kinds: ICitableReferenceKind list)
        (issuer: IGroundingCertificateIssuer)
        : INarrativeGroundingGate =
        FactNarrativeGroundingGate(store, disclosure, registry, kinds, issuer, NarrativeGrounding.defaultPolicy)
        :> INarrativeGroundingGate

    /// The gate under an explicit policy (another language's number words).
    let createWithPolicy
        (policy: GroundingPolicy)
        (store: IFactStore)
        (disclosure: IFactDisclosureGate)
        (registry: Grounding.IMetricRegistry option)
        (kinds: ICitableReferenceKind list)
        (issuer: IGroundingCertificateIssuer)
        : INarrativeGroundingGate =
        FactNarrativeGroundingGate(store, disclosure, registry, kinds, issuer, policy) :> INarrativeGroundingGate