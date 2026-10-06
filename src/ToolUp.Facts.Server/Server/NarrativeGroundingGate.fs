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
// a deployment can extend for another language.
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

    let rec private elementClaims (at: string) (element: NarrativeElement) : Claim list =
        match element with
        | Paragraph spans -> spanClaims at spans
        | Heading(_, spans) -> spanClaims at spans
        | BulletList items
        | OrderedList items ->
            items
            |> List.mapi (fun k item -> spanClaims (sprintf "%s › item %d" at (k + 1)) item)
            |> List.concat
        | KeyValueGrid pairs ->
            pairs
            |> List.mapi (fun k (key, spans) ->
                let loc = sprintf "%s › entry %d" at (k + 1)
                ProseClaim(loc, key) :: spanClaims loc spans)
            |> List.concat
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

            headers @ cells
        | Callout(_, spans) -> spanClaims at spans
        | CodeBlock(_, content) -> [ ProseClaim(at, content) ]
        | Blockquote(citation, spans) -> optProse at citation @ spanClaims at spans
        | Divider -> []
        | Video spec ->
            optProse at spec.Caption
            @ (spec.Tracks |> List.map (fun t -> ProseClaim(at, t.Label)))
        | Audio spec ->
            optProse at spec.Caption
            @ (spec.Tracks |> List.map (fun t -> ProseClaim(at, t.Label)))
        | ImageGallery images ->
            images
            |> List.mapi (fun k img ->
                let loc = sprintf "%s › image %d" at (k + 1)
                ProseClaim(loc, img.Alt) :: optProse loc img.Caption)
            |> List.concat
        | Embed spec -> [ ProseClaim(at, spec.Title) ]
        | Card spec ->
            optProse at spec.Heading
            @ (spec.Image
               |> Option.map (fun img -> ProseClaim(at, img.Alt) :: optProse at img.Caption)
               |> Option.defaultValue [])
            @ bodyClaims at spec.Body
        | Accordion panels
        | Tabs panels ->
            panels
            |> List.mapi (fun k (label, body) ->
                let loc = sprintf "%s › panel %d" at (k + 1)
                ProseClaim(loc, label) :: bodyClaims loc body)
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

    and private bodyClaims (at: string) (elements: NarrativeElement list) : Claim list =
        elements
        |> List.mapi (fun i e -> elementClaims (sprintf "%s › element %d" at (i + 1)) e)
        |> List.concat

    /// Every claim in the document's content, in document order.
    let claims (document: NarrativeDocument) : Claim list =
        document.Sections
        |> List.collect (fun section -> bodyClaims (sprintf "section '%s'" section.Id) section.Elements)

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

    /// The pure check: given how every reference resolved, the verdict
    /// over `document`. Every offending claim is reported, in document
    /// order.
    let check
        (policy: GroundingPolicy)
        (resolve: string -> GroundingResolution)
        (document: NarrativeDocument)
        : NarrativeGroundingVerdict =
        let offence loc claim reason = {
            Location = loc
            Claim = claim
            Reason = reason
        }

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

        let offences =
            claims document
            |> List.collect (function
                | ProseClaim(loc, text) ->
                    if statesNumber policy text then
                        [ offence loc text UnreferencedNumber ]
                    else
                        []
                | MetricClaim(loc, label, value, reference) ->
                    let claim = sprintf "%s = %s" label value

                    let labelOffence =
                        if statesNumber policy label then
                            [ offence loc label UnreferencedNumber ]
                        else
                            []

                    let valueOffence =
                        match reference with
                        | None -> [ offence loc claim MetricWithoutReference ]
                        | Some reference -> judgeReference loc claim reference (Some value)

                    labelOffence @ valueOffence
                | CitationClaim(loc, reference) -> judgeReference loc reference reference None)

        match offences with
        | [] ->
            references document
            |> List.map (fun reference ->
                let kind, _ = referenceKind reference

                {
                    ReferenceKind = kind
                    Reference = reference
                })
            |> Grounded
        | offences -> Ungrounded offences

    /// The Fact ids a verdict cites — what a certificate is issued over.
    let citedFactIds (citations: NarrativeCitation list) : string list = NarrativeCitation.factIds citations

    /// The fact tools a grounded run gives the model — and the only ones
    /// from the fact tier. The same three the fact store declares on
    /// `ServerApp.AITools`; the run adds the declared reference tools.
    let factTools: (AIToolDefinition * (HttpContext -> string -> Async<string>)) list = [
        FactQueryTool.definition, FactQueryTool.execute
        PopulationQueryTool.definition, PopulationQueryTool.execute
        CoverageTool.definition, CoverageTool.execute
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

    let displayFormatOf (fact: Fact) : string =
        registry
        |> Option.bind (fun r -> r.TryGetMetric fact.Metric.Value)
        |> Option.map _.DisplayFormat
        |> Option.defaultValue ""

    let currentOf (fact: Fact) : GroundingResolution =
        let format = displayFormatOf fact
        let rendering = FactRendering.render format fact.Value

        let value =
            match fact.Value with
            | Scalar d -> Some d
            | _ -> None

        let asFraction =
            format.StartsWith("P", StringComparison.OrdinalIgnoreCase)
            || format.Contains "%"

        GroundingCurrent([ rendering ], value, asFraction)

    let resolveFacts (scopeId: string) (principal: string) (surface: FactEgressSurface) (factIds: string list) = async {
        match factIds with
        | [] -> return Map.empty
        | ids ->
            let! verdicts = disclosure.Check(scopeId, principal, surface, ids)

            let! resolved =
                ids
                |> List.map (fun id -> async {
                    let! chain = store.QuerySupersessionChain(scopeId, id)

                    match List.tryFind (fun (f: Fact) -> f.FactId = id) chain with
                    | None -> return id, GroundingUnresolved
                    | Some fact ->
                        match verdicts.TryFind id with
                        | Some FactDisclosable ->
                            let head = List.last chain

                            if head.FactId <> id then
                                return id, GroundingSuperseded(Some head.FactId)
                            else
                                return id, currentOf fact
                        | Some(FactNotDisclosable policyRef) -> return id, GroundingWithheld policyRef
                        | None -> return id, GroundingWithheld "unknown-fact"
                })
                |> Async.Sequential

            return Map.ofArray resolved
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

    /// The certificate chain depth — deep enough to reach a Fact's input
    /// data from the narrative at the root.
    static member CertificateDepth = 8

    interface INarrativeGroundingGate with
        member _.Check(scopeId, principal, surface, document) = async {
            let references = NarrativeGrounding.references document

            let factIds, declared =
                references
                |> List.partition (fun r -> fst (NarrativeGrounding.referenceKind r) = NarrativeGrounding.FactKind)

            let! facts = resolveFacts scopeId principal surface factIds
            let! others = declared |> List.map (resolveDeclared scopeId principal) |> Async.Sequential

            let resolutions =
                Map.fold (fun acc k v -> Map.add k v acc) (Map.ofArray others) facts

            let resolve reference =
                resolutions.TryFind reference |> Option.defaultValue GroundingUnresolved

            return NarrativeGrounding.check policy resolve document
        }

        member _.Certify(scopeId, principal, narrativeId, citedFactIds) = async {
            let! issued =
                issuer.Issue(
                    scopeId,
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