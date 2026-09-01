// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.StructuredDataConformanceTests

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Xml.Linq
open Expecto
open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering

// ─── Phase 212 — SEO / structured-data conformance lint ──────────────
//
// A CI validator over the markup the public-rendering surface emits:
// the JSON-LD from `StructuredDataHelpers`, the `<urlset>` /
// `<sitemapindex>` from `SitemapGenerator`, and the canonical /
// hreflang / robots head injections (`NarrativeLayout` +
// `PageHeadInjection`). Wave 21 shipped the SEO surface and was
// documented afterwards; this pack closes the missing conformance
// harness so a malformed emitter fails the build instead of a
// crawler.
//
// Three properties of the pack are deliberate:
//
//   * PURE-STRING AND OFFLINE (GP 12). Every rule is decided from the
//     emitted text — `System.Text.Json` for JSON-LD, `System.Xml.Linq`
//     for the sitemap, a small regex reader for the rendered head.
//     Nothing here contacts schema.org, Google, or any host: a
//     conformance gate that needs the network is a gate that goes red
//     for reasons unrelated to the code.
//
//   * TEST-ONLY (GP 13). No shipping source, and no `.fsproj` of a
//     packable assembly, is touched. The internal `generateUrlSetFrom`
//     is reached through its public delegators (`generateWith` /
//     `generate` / `generateSitemapIndex`) rather than by opening the
//     package's internals to the test assembly — the body under test is
//     the same string builder either way, and the package's shipped
//     metadata stays byte-for-byte unchanged.
//
//   * THE RULES ARE PROVEN TO BITE. Every validator below carries a
//     negative self-test (`ruleSelfTests`) that feeds it a deliberately
//     malformed input and asserts it reports. A conformance rule that
//     has never been observed to fail is indistinguishable from one
//     that cannot — the failure mode this pack exists to prevent, so it
//     is pinned rather than asserted in prose.
//
// One shipped behaviour genuinely fails a correct rule and is pinned
// as data rather than silently excused — see `knownGapTests` at the
// foot of the file.

// ─── 1. The conformance rule set ─────────────────────────────────────
//
// Each validator returns the list of violations it found — `[]` is
// conformant. Findings are strings because they are read by a human
// staring at a red build; the label argument names the emitter so a
// finding identifies its subject without a stack trace.

module private Conformance =

    /// The one `@context` a schema.org JSON-LD payload may declare.
    /// Google's structured-data parsers accept the `http://` spelling
    /// too, but the SDK emits exactly one form and the lint holds it
    /// there — a mixed corpus is how a payload drifts unnoticed.
    let schemaOrgContext = "https://schema.org"

    /// sitemaps.org caps a single `<urlset>` at 50,000 URLs.
    let sitemapUrlCap = 50_000

    let private sitemapNs = XNamespace.Get "http://www.sitemaps.org/schemas/sitemap/0.9"

    // ---- JSON-LD ----

    /// Every object node in a JSON-LD tree, paired with a dotted path
    /// for the finding message. Root first, then depth-first.
    let rec private objectNodes (path: string) (el: JsonElement) : (string * JsonElement) list =
        match el.ValueKind with
        | JsonValueKind.Object ->
            let children =
                el.EnumerateObject()
                |> Seq.collect (fun p -> objectNodes (path + "." + p.Name) p.Value)
                |> List.ofSeq

            (path, el) :: children
        | JsonValueKind.Array ->
            el.EnumerateArray()
            |> Seq.mapi (fun i child -> objectNodes (sprintf "%s[%d]" path i) child)
            |> Seq.concat
            |> List.ofSeq
        | _ -> []

    /// Every property in the tree whose value is JSON `null`, by path.
    let rec private nullPaths (path: string) (el: JsonElement) : string list =
        match el.ValueKind with
        | JsonValueKind.Null -> [ path ]
        | JsonValueKind.Object ->
            el.EnumerateObject()
            |> Seq.collect (fun p -> nullPaths (path + "." + p.Name) p.Value)
            |> List.ofSeq
        | JsonValueKind.Array ->
            el.EnumerateArray()
            |> Seq.mapi (fun i child -> nullPaths (sprintf "%s[%d]" path i) child)
            |> Seq.concat
            |> List.ofSeq
        | _ -> []

    let private tryProp (name: string) (el: JsonElement) : JsonElement option =
        match el.TryGetProperty name with
        | true, v -> Some v
        | _ -> None

    let private tryStringProp (name: string) (el: JsonElement) : string option =
        tryProp name el
        |> Option.bind (fun v ->
            if v.ValueKind = JsonValueKind.String then
                Some(v.GetString())
            else
                None)

    /// The `itemListElement` / `step` arrays every ordered emitter
    /// builds: each element must be an object carrying `@type` and a
    /// `position`, and the positions must run 1..n with no gap and no
    /// repeat. A crawler reading a list whose positions restart or skip
    /// silently drops entries.
    let private positionedArrayFindings (label: string) (root: JsonElement) : string list =
        [ "itemListElement"; "step" ]
        |> List.collect (fun name ->
            match tryProp name root with
            | Some arr when arr.ValueKind = JsonValueKind.Array ->
                let items = arr.EnumerateArray() |> List.ofSeq

                let positions =
                    items
                    |> List.mapi (fun i item ->
                        match tryProp "position" item with
                        | Some p when p.ValueKind = JsonValueKind.Number -> Ok(p.GetInt32())
                        | Some _ -> Error(sprintf "%s: %s[%d].position is not a number" label name i)
                        | None -> Error(sprintf "%s: %s[%d] has no `position`" label name i))

                let malformed =
                    positions
                    |> List.choose (function
                        | Error e -> Some e
                        | Ok _ -> None)

                let ordinals =
                    positions
                    |> List.choose (function
                        | Ok p -> Some p
                        | Error _ -> None)

                let contiguity =
                    if List.isEmpty malformed && ordinals <> [ 1 .. List.length items ] then
                        [
                            sprintf
                                "%s: %s positions are %A — expected a contiguous 1..%d run"
                                label
                                name
                                ordinals
                                (List.length items)
                        ]
                    else
                        []

                malformed @ contiguity
            | Some _ -> [ sprintf "%s: `%s` is present but is not an array" label name ]
            | None -> [])

    /// Structural conformance every JSON-LD payload must satisfy,
    /// independent of which schema.org type it declares.
    let jsonLdStructure (label: string) (payload: string) : string list =
        // The emitter rewrites `</` to `<\/` precisely so a value
        // containing `</script>` cannot break out of the enclosing
        // block. A payload reaching the wire with a raw `</` is an XSS
        // breakout, not a formatting nit.
        let scriptSafety =
            if payload.Contains "</" then
                [
                    sprintf "%s: raw '</' in the payload — it can terminate the enclosing <script> block" label
                ]
            else
                []

        let parsed =
            try
                use doc = JsonDocument.Parse payload
                let root = doc.RootElement

                if root.ValueKind <> JsonValueKind.Object then
                    Ok [ sprintf "%s: JSON-LD root is %A, not an object" label root.ValueKind ]
                else
                    let context =
                        match tryStringProp "@context" root with
                        | Some c when c = schemaOrgContext -> []
                        | Some c -> [ sprintf "%s: @context is '%s', expected '%s'" label c schemaOrgContext ]
                        | None -> [ sprintf "%s: no string `@context` on the root node" label ]

                    let typed =
                        objectNodes "$" root
                        |> List.collect (fun (path, node) ->
                            match tryStringProp "@type" node with
                            | Some t when not (String.IsNullOrWhiteSpace t) -> []
                            | Some _ -> [ sprintf "%s: %s has a blank `@type`" label path ]
                            | None -> [ sprintf "%s: %s carries no string `@type`" label path ])

                    // `@context` belongs to the document, not to each
                    // entity in it; a nested one is a copy-paste tell.
                    let nestedContext =
                        objectNodes "$" root
                        |> List.filter (fun (path, node) -> path <> "$" && (tryProp "@context" node).IsSome)
                        |> List.map (fun (path, _) ->
                            sprintf "%s: nested node %s declares its own `@context`" label path)

                    let nulls =
                        nullPaths "$" root
                        |> List.map (fun path -> sprintf "%s: %s is JSON null — omit the property instead" label path)

                    Ok(context @ typed @ nestedContext @ nulls @ positionedArrayFindings label root)
            with ex ->
                Error ex.Message

        match parsed with
        | Ok findings -> scriptSafety @ findings
        | Error message -> scriptSafety @ [ sprintf "%s: not well-formed JSON — %s" label message ]

    /// `jsonLdStructure` plus the declared type and the property set
    /// schema.org / Google require for it. "Required" here is presence:
    /// the emitters deliberately degrade a missing frontmatter value to
    /// an empty string rather than throwing, so emptiness is a separate
    /// rule (`jsonLdPopulated`) applied to a page whose source data is
    /// complete.
    let jsonLdOfType (label: string) (expectedType: string) (required: string list) (payload: string) : string list =
        let structural = jsonLdStructure label payload

        let typeAndProps =
            try
                use doc = JsonDocument.Parse payload
                let root = doc.RootElement

                if root.ValueKind <> JsonValueKind.Object then
                    []
                else
                    let declared =
                        match tryStringProp "@type" root with
                        | Some t when t = expectedType -> []
                        | Some t -> [ sprintf "%s: @type is '%s', expected '%s'" label t expectedType ]
                        | None -> []

                    let missing =
                        required
                        |> List.filter (fun name -> (tryProp name root).IsNone)
                        |> List.map (fun name ->
                            sprintf "%s: required `%s` property '%s' is absent" label expectedType name)

                    declared @ missing
            with _ -> []

        structural @ typeAndProps

    /// Required properties carry a VALUE, for a page whose source data
    /// populated every field. Catches an emitter that silently stopped
    /// reading a frontmatter key — the failure a presence-only rule
    /// cannot see, because the key is still emitted as `""`.
    let jsonLdPopulated (label: string) (required: string list) (payload: string) : string list =
        try
            use doc = JsonDocument.Parse payload
            let root = doc.RootElement

            required
            |> List.collect (fun name ->
                match tryProp name root with
                | None -> [ sprintf "%s: '%s' is absent" label name ]
                | Some v ->
                    match v.ValueKind with
                    | JsonValueKind.String when String.IsNullOrWhiteSpace(v.GetString()) -> [
                        sprintf "%s: '%s' is an empty string on a fully-populated page" label name
                      ]
                    | JsonValueKind.Array when Seq.isEmpty (v.EnumerateArray()) -> [
                        sprintf "%s: '%s' is an empty array on a fully-populated page" label name
                      ]
                    | JsonValueKind.Object when
                        v.EnumerateObject() |> Seq.filter (fun p -> p.Name <> "@type") |> Seq.isEmpty
                          ->
                          [
                              sprintf "%s: '%s' carries only an `@type` on a fully-populated page" label name
                          ]
                    | _ -> [])
        with ex -> [ sprintf "%s: not well-formed JSON — %s" label ex.Message ]

    // ---- Open Graph / Twitter meta pairs ----

    /// The `(name, content)` meta emitters: every required name present
    /// exactly once, no blank content, and no name repeated (a repeated
    /// `og:title` is undefined behaviour across crawlers).
    let metaPairs (label: string) (required: string list) (pairs: (string * string) list) : string list =
        let names = pairs |> List.map fst

        let missing =
            required
            |> List.filter (fun n -> not (List.contains n names))
            |> List.map (sprintf "%s: required meta '%s' is absent" label)

        let duplicated =
            names
            |> List.countBy id
            |> List.filter (fun (_, n) -> n > 1)
            |> List.map (fun (name, n) -> sprintf "%s: meta '%s' emitted %d times" label name n)

        let blank =
            pairs
            |> List.filter (fun (_, content) -> String.IsNullOrWhiteSpace content)
            |> List.map (fun (name, _) -> sprintf "%s: meta '%s' has blank content" label name)

        missing @ duplicated @ blank

    // ---- Sitemap ----

    let private isAbsoluteHttpUrl (url: string) =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, u -> u.Scheme = Uri.UriSchemeHttp || u.Scheme = Uri.UriSchemeHttps
        | _ -> false

    /// A `<lastmod>` must be a W3C datetime. The generator emits
    /// `yyyy-MM-dd`; the rule accepts any of the W3C profile's forms so
    /// a consumer-supplied fallback date is not rejected for being more
    /// precise than the SDK's own.
    let private isW3CDate (value: string) =
        let styles =
            Globalization.DateTimeStyles.AssumeUniversal
            ||| Globalization.DateTimeStyles.AdjustToUniversal

        fst (DateTimeOffset.TryParse(value, Globalization.CultureInfo.InvariantCulture, styles))

    let private childFindings (label: string) (parentName: string) (el: XElement) : string list =
        let locs = el.Elements(sitemapNs + "loc") |> List.ofSeq
        let lastmods = el.Elements(sitemapNs + "lastmod") |> List.ofSeq

        let locCount =
            match locs with
            | [ _ ] -> []
            | [] -> [ sprintf "%s: a <%s> carries no <loc>" label parentName ]
            | many -> [
                sprintf "%s: a <%s> carries %d <loc> elements" label parentName (List.length many)
              ]

        let locAbsolute =
            locs
            |> List.filter (fun l -> not (isAbsoluteHttpUrl (l.Value.Trim())))
            |> List.map (fun l -> sprintf "%s: <loc>%s</loc> is not an absolute http(s) URL" label (l.Value.Trim()))

        let lastmodCount =
            if List.length lastmods > 1 then
                [
                    sprintf "%s: a <%s> carries %d <lastmod> elements" label parentName (List.length lastmods)
                ]
            else
                []

        let lastmodFormat =
            lastmods
            |> List.filter (fun m -> not (isW3CDate (m.Value.Trim())))
            |> List.map (fun m -> sprintf "%s: <lastmod>%s</lastmod> is not a W3C datetime" label (m.Value.Trim()))

        locCount @ locAbsolute @ lastmodCount @ lastmodFormat

    let private parseXml (label: string) (xml: string) (f: XElement -> string list) : string list =
        try
            f (XDocument.Parse xml).Root
        with ex -> [ sprintf "%s: not well-formed XML — %s" label ex.Message ]

    /// Conformance for a `<urlset>` body: the sitemaps.org 0.9 root in
    /// its namespace, `<url>` children only, one absolute `<loc>` each,
    /// no duplicate `<loc>`, well-formed `<lastmod>`, and the 50,000-URL
    /// file cap.
    let sitemapUrlSet (label: string) (xml: string) : string list =
        parseXml label xml (fun root ->
            if root.Name <> sitemapNs + "urlset" then
                [
                    sprintf
                        "%s: root is <%s>, expected <urlset> in the sitemaps.org 0.9 namespace"
                        label
                        root.Name.LocalName
                ]
            else
                let children = root.Elements() |> List.ofSeq

                let foreign =
                    children
                    |> List.filter (fun e -> e.Name <> sitemapNs + "url")
                    |> List.map (fun e -> sprintf "%s: unexpected <%s> child of <urlset>" label e.Name.LocalName)

                let urls = children |> List.filter (fun e -> e.Name = sitemapNs + "url")

                let locs =
                    urls
                    |> List.collect (fun u ->
                        u.Elements(sitemapNs + "loc") |> Seq.map (fun l -> l.Value.Trim()) |> List.ofSeq)

                let duplicates =
                    locs
                    |> List.countBy id
                    |> List.filter (fun (_, n) -> n > 1)
                    |> List.map (fun (loc, n) -> sprintf "%s: <loc>%s</loc> appears %d times" label loc n)

                let overCap =
                    if List.length urls > sitemapUrlCap then
                        [
                            sprintf
                                "%s: %d <url> entries exceeds the %d-URL file cap"
                                label
                                (List.length urls)
                                sitemapUrlCap
                        ]
                    else
                        []

                foreign
                @ duplicates
                @ overCap
                @ (urls |> List.collect (childFindings label "url")))

    /// Conformance for a `<sitemapindex>` body — the shard-index half of
    /// the same contract.
    let sitemapIndex (label: string) (xml: string) : string list =
        parseXml label xml (fun root ->
            if root.Name <> sitemapNs + "sitemapindex" then
                [
                    sprintf
                        "%s: root is <%s>, expected <sitemapindex> in the sitemaps.org 0.9 namespace"
                        label
                        root.Name.LocalName
                ]
            else
                let children = root.Elements() |> List.ofSeq

                let foreign =
                    children
                    |> List.filter (fun e -> e.Name <> sitemapNs + "sitemap")
                    |> List.map (fun e -> sprintf "%s: unexpected <%s> child of <sitemapindex>" label e.Name.LocalName)

                let shards = children |> List.filter (fun e -> e.Name = sitemapNs + "sitemap")

                let locs =
                    shards
                    |> List.collect (fun s ->
                        s.Elements(sitemapNs + "loc") |> Seq.map (fun l -> l.Value.Trim()) |> List.ofSeq)

                let duplicates =
                    locs
                    |> List.countBy id
                    |> List.filter (fun (_, n) -> n > 1)
                    |> List.map (fun (loc, n) -> sprintf "%s: child <loc>%s</loc> appears %d times" label loc n)

                foreign @ duplicates @ (shards |> List.collect (childFindings label "sitemap")))

    /// The slugs a `<urlset>` actually advertises, relative to
    /// `baseUrl` — the read side of the exclusion rules.
    let advertisedSlugs (baseUrl: string) (xml: string) : string list =
        let prefix = baseUrl.TrimEnd('/') + "/"

        (XDocument.Parse xml).Root.Elements(sitemapNs + "url")
        |> Seq.collect (fun u -> u.Elements(sitemapNs + "loc"))
        |> Seq.map (fun l ->
            let v = l.Value.Trim()

            if v.StartsWith prefix then v.Substring prefix.Length else v)
        |> List.ofSeq

    // ---- Rendered head: canonical, hreflang, robots ----

    let private linkRx (rel: string) =
        Regex(sprintf "<link[^>]*rel=\"%s\"[^>]*>" (Regex.Escape rel), RegexOptions.IgnoreCase)

    let private attrRx (name: string) =
        Regex(sprintf "%s=\"([^\"]*)\"" (Regex.Escape name), RegexOptions.IgnoreCase)

    let private attrOf (name: string) (tag: string) : string option =
        let m = (attrRx name).Match tag
        if m.Success then Some m.Groups[1].Value else None

    /// Every `href` on a `<link rel="canonical">` in a rendered
    /// document.
    let canonicalHrefs (html: string) : string list =
        (linkRx "canonical").Matches html
        |> Seq.choose (fun m -> attrOf "href" m.Value)
        |> List.ofSeq

    /// Every `(hreflang, href)` on a `<link rel="alternate">`.
    let alternateLinks (html: string) : (string * string) list =
        (linkRx "alternate").Matches html
        |> Seq.choose (fun m ->
            match attrOf "hreflang" m.Value, attrOf "href" m.Value with
            | Some lang, Some href -> Some(lang, href)
            | _ -> None)
        |> List.ofSeq

    /// Every JSON-LD payload embedded in a rendered document.
    let embeddedJsonLd (html: string) : string list =
        Regex.Matches(html, "<script[^>]*type=\"application/ld\\+json\"[^>]*>(.*?)</script>", RegexOptions.Singleline)
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> List.ofSeq

    /// The `content` of every `<meta name="robots">`.
    let robotsMetaContents (html: string) : string list =
        Regex.Matches(html, "<meta[^>]*name=\"robots\"[^>]*>", RegexOptions.IgnoreCase)
        |> Seq.choose (fun m -> attrOf "content" m.Value)
        |> List.ofSeq

    /// A rendered page declares exactly one canonical, it is the
    /// expected absolute self-reference, and nothing else claims to be
    /// canonical. Two canonicals is worse than none — a crawler picks
    /// one arbitrarily.
    let canonicalSelfReference (label: string) (expected: string) (html: string) : string list =
        match canonicalHrefs html with
        | [] -> [ sprintf "%s: no <link rel=\"canonical\"> in the rendered document" label ]
        | [ href ] ->
            let matches =
                if href = expected then
                    []
                else
                    [
                        sprintf "%s: canonical is '%s', expected the page's own '%s'" label href expected
                    ]

            let absolute =
                if isAbsoluteHttpUrl href then
                    []
                else
                    [ sprintf "%s: canonical '%s' is not an absolute http(s) URL" label href ]

            matches @ absolute
        | many -> [
            sprintf "%s: %d canonical links in one document — %A" label (List.length many) many
          ]

    let private langTagRx = Regex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")

    /// A cluster is `(pageUrl, alternates)` per member. The rules are
    /// the ones Google's multi-locale documentation makes hard errors:
    /// every member self-references, every pair is RECIPROCAL, language
    /// tags are well-formed and unique within a member, and at most one
    /// `x-default` per member.
    ///
    /// `NarrativeLayout` documents reciprocity as the consumer's
    /// responsibility — it is not enforced at emit time and cannot be,
    /// since a page cannot see its siblings. This is the check that
    /// closes that gap: a deployment points it at its own resolved page
    /// set and a non-reciprocal cluster fails the build.
    let hreflangCluster (label: string) (cluster: (string * (string * string) list) list) : string list =
        let byUrl = cluster |> Map.ofList

        let selfLangOf (selfUrl: string) (alts: (string * string) list) =
            alts |> List.tryFind (fun (_, url) -> url = selfUrl) |> Option.map fst

        cluster
        |> List.collect (fun (selfUrl, alts) ->
            let wellFormed =
                alts
                |> List.filter (fun (lang, _) -> lang <> "x-default" && not (langTagRx.IsMatch lang))
                |> List.map (fun (lang, _) ->
                    sprintf "%s [%s]: '%s' is not a well-formed language tag" label selfUrl lang)

            let absolute =
                alts
                |> List.filter (fun (_, url) -> not (isAbsoluteHttpUrl url))
                |> List.map (fun (_, url) ->
                    sprintf "%s [%s]: alternate '%s' is not an absolute URL" label selfUrl url)

            let duplicateLangs =
                alts
                |> List.countBy fst
                |> List.filter (fun (_, n) -> n > 1)
                |> List.map (fun (lang, n) -> sprintf "%s [%s]: hreflang '%s' declared %d times" label selfUrl lang n)

            let selfRef =
                match selfLangOf selfUrl alts with
                | Some _ -> []
                | None -> [
                    sprintf "%s [%s]: the cluster does not include a self-referencing alternate" label selfUrl
                  ]

            let reciprocity =
                alts
                |> List.filter (fun (_, url) -> url <> selfUrl)
                |> List.collect (fun (theirLang, theirUrl) ->
                    match Map.tryFind theirUrl byUrl with
                    | None -> []
                    | Some theirAlts ->
                        let backLink =
                            if theirAlts |> List.exists (fun (_, url) -> url = selfUrl) then
                                []
                            else
                                [
                                    sprintf
                                        "%s [%s]: points at '%s', which does not point back — the cluster is not reciprocal"
                                        label
                                        selfUrl
                                        theirUrl
                                ]

                        let agreedLang =
                            match selfLangOf theirUrl theirAlts with
                            | Some ownLang when ownLang <> theirLang && theirLang <> "x-default" -> [
                                sprintf
                                    "%s [%s]: labels '%s' as '%s', but that page labels itself '%s'"
                                    label
                                    selfUrl
                                    theirUrl
                                    theirLang
                                    ownLang
                              ]
                            | _ -> []

                        backLink @ agreedLang)

            let xDefaults =
                alts |> List.filter (fun (lang, _) -> lang = "x-default") |> List.length

            let xDefault =
                if xDefaults > 1 then
                    [
                        sprintf "%s [%s]: %d x-default alternates — at most one is meaningful" label selfUrl xDefaults
                    ]
                else
                    []

            wellFormed @ absolute @ duplicateLangs @ selfRef @ reciprocity @ xDefault)

    let private robotsTokens =
        set [
            "all"
            "index"
            "noindex"
            "follow"
            "nofollow"
            "none"
            "noarchive"
            "nosnippet"
            "noimageindex"
            "notranslate"
            "nositelinkssearchbox"
            "indexifembedded"
        ]

    let private robotsPrefixes = [
        "max-snippet:"
        "max-image-preview:"
        "max-video-preview:"
        "unavailable_after:"
    ]

    /// A `<meta name="robots">` content string: known directives only,
    /// no repeats, and no self-contradicting pair. `index, noindex` is
    /// resolved by crawlers as the most restrictive — so a page meaning
    /// to stay indexed silently disappears.
    let robotsContent (label: string) (content: string) : string list =
        if String.IsNullOrWhiteSpace content then
            [ sprintf "%s: robots content is blank" label ]
        else
            let tokens =
                content.Split(',')
                |> Array.map (fun t -> t.Trim().ToLowerInvariant())
                |> List.ofArray

            let blanks =
                if tokens |> List.exists String.IsNullOrEmpty then
                    [ sprintf "%s: robots content '%s' has an empty directive" label content ]
                else
                    []

            let unknown =
                tokens
                |> List.filter (fun t ->
                    t <> ""
                    && not (robotsTokens.Contains t)
                    && not (
                        robotsPrefixes
                        |> List.exists (fun p -> t.StartsWith(p, StringComparison.Ordinal))
                    ))
                |> List.map (sprintf "%s: unknown robots directive '%s'" label)

            let duplicates =
                tokens
                |> List.countBy id
                |> List.filter (fun (t, n) -> t <> "" && n > 1)
                |> List.map (fun (t, n) -> sprintf "%s: robots directive '%s' repeated %d times" label t n)

            let contradictions =
                [ "index", "noindex"; "follow", "nofollow" ]
                |> List.filter (fun (a, b) -> List.contains a tokens && List.contains b tokens)
                |> List.map (fun (a, b) -> sprintf "%s: robots content declares both '%s' and '%s'" label a b)

            blanks @ unknown @ duplicates @ contradictions

    /// A slug the site marks `noindex` must not be advertised in
    /// `sitemap.xml`. Search Console reports the combination as the
    /// "Submitted URL marked 'noindex'" coverage error — the sitemap
    /// asks for indexing and the page refuses it.
    let noindexNotAdvertised (label: string) (noindexSlugs: string list) (advertised: string list) : string list =
        noindexSlugs
        |> List.filter (fun slug -> List.contains slug advertised)
        |> List.map (sprintf "%s: '%s' is marked noindex but is advertised in the sitemap" label)

/// Fail an Expecto case with every finding named, or pass silently.
let private expectConformant (subject: string) (findings: string list) =
    if not (List.isEmpty findings) then
        failtestf "%s is not conformant:\n  - %s" subject (String.concat "\n  - " findings)

// ─── 2. The representative page set ──────────────────────────────────

let private mkPage
    (slug: string)
    (title: string)
    (description: string)
    (body: ContentBody)
    (frontmatter: (string * string) list)
    : PublicPage =
    {
        Slug = Slug slug
        Title = title
        Description = description
        Body = body
        Layout = LayoutName "page"
        Frontmatter = Map.ofList frontmatter
        PublishedAt = Some(DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.Zero))
        Collection = None
        Status = Published
        Audience = PageAudience.Public
    }

/// A page whose frontmatter populates every key the emitters read — the
/// fixture the non-emptiness rules run against.
let private populatedPage =
    mkPage "about" "About Example Widgets" "Who we are and what we build" (Markdown "# About") [
        "author", "Ada Lovelace"
        "og:image", "https://example.com/img/about.png"
        "name", "Example Widgets"
        "jobTitle", "Chief Engineer"
        "email", "ada@example.com"
        "url", "https://example.com"
        "startDate", "2026-09-01T09:00:00Z"
        "endDate", "2026-09-01T17:00:00Z"
        "location", "Edinburgh"
        "sameAs", "https://example.com/social, https://example.org/profile"
    ]

/// A page carrying nothing but its own identity — the fixture the
/// presence rules run against, and the one that proves the documented
/// degrade-to-empty behaviour never becomes a throw.
let private minimalPage = mkPage "minimal" "Minimal" "" (Markdown "x") []

let private narrativeBase = Narrative.create "Quarterly Review"

let private narrativeDoc = {
    narrativeBase with
        Subtitle = Some "How the quarter went"
        Lang = Some "en-GB"
        CanonicalUrl = Some "https://example.com/reports/q1"
        Provenance =
            Some {
                ModuleId = "analytics"
                PageRoute = Some "/reports/q1"
                GeneratedAt = DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.Zero)
                SettingsKey = "q1-2026"
                SettingsDisplay = []
            }
}

let private narrativePage =
    mkPage "reports/q1" "Q1 Review" "The first quarter in numbers" (Narrative narrativeDoc) [
        "og:image", "https://example.com/img/q1.png"
    ]

/// `(label, @type, required properties, payload)` for every shipped
/// JSON-LD emitter, bound against the populated fixture. The required
/// sets are schema.org's plus Google's rich-result requirements for the
/// types that have one.
let private jsonLdCases: (string * string * string list * string) list = [
    "article",
    "Article",
    [ "headline"; "description"; "author"; "image"; "datePublished" ],
    StructuredDataHelpers.article populatedPage
    "person", "Person", [ "name"; "jobTitle"; "image"; "email"; "url" ], StructuredDataHelpers.person populatedPage
    "event",
    "Event",
    [ "name"; "description"; "startDate"; "endDate"; "location"; "image" ],
    StructuredDataHelpers.event populatedPage
    "organization",
    "Organization",
    [ "name"; "url"; "logo"; "description"; "sameAs" ],
    StructuredDataHelpers.organization populatedPage
    "breadcrumb",
    "BreadcrumbList",
    [ "itemListElement" ],
    StructuredDataHelpers.breadcrumb [ "Home", "https://example.com/"; "About", "https://example.com/about" ]
    "siteNavigation",
    "ItemList",
    [ "itemListElement" ],
    StructuredDataHelpers.siteNavigation [ "Home", "https://example.com/"; "Docs", "https://example.com/docs" ]
    "itemList",
    "ItemList",
    [ "itemListElement" ],
    StructuredDataHelpers.itemList [ "First", "https://example.com/1"; "Second", "https://example.com/2" ]
    "webSite",
    "WebSite",
    [ "name"; "url"; "potentialAction" ],
    StructuredDataHelpers.webSite
        "Example Widgets"
        "https://example.com"
        (Some "https://example.com/search?q={search_term_string}")
    "faqPage",
    "FAQPage",
    [ "mainEntity" ],
    StructuredDataHelpers.faqPage [ "What is it?", "A widget."; "How much?", "Ten pounds." ]
    "howTo", "HowTo", [ "name"; "step" ], StructuredDataHelpers.howTo "Assemble a widget" [ "Unbox it"; "Turn it on" ]
    "learningResource",
    "LearningResource",
    [
        "name"
        "description"
        "learningResourceType"
        "educationalLevel"
        "teaches"
        "inLanguage"
    ],
    StructuredDataHelpers.learningResource "Widgets 101" "An introduction" "widget assembly"
    "learningResourceWith",
    "LearningResource",
    [
        "name"
        "description"
        "learningResourceType"
        "educationalLevel"
        "teaches"
        "inLanguage"
    ],
    StructuredDataHelpers.learningResourceWith "Widgets 301" "Advanced" "widget theory" "Course" "Advanced" "en-GB"
    "course",
    "Course",
    [ "name"; "provider"; "description" ],
    StructuredDataHelpers.course "Widgets 101" "Example Widgets" "An introduction"
    "product",
    "Product",
    [ "name"; "description"; "offers"; "aggregateRating" ],
    StructuredDataHelpers.product "Widget" "A fine widget" (Some("10.00", "GBP")) (Some("4.7", "120"))
    "videoObject",
    "VideoObject",
    [ "name"; "description"; "thumbnailUrl"; "uploadDate"; "contentUrl" ],
    StructuredDataHelpers.videoObject
        "Widget demo"
        "A demonstration"
        "https://example.com/img/thumb.png"
        "2026-06-19"
        "https://example.com/video/demo.mp4"
]

/// The emitters again, bound against the MINIMAL page — the presence
/// and structural rules must hold with no frontmatter at all.
let private minimalJsonLdCases: (string * string * string list * string) list = [
    "article (minimal)",
    "Article",
    [ "headline"; "description"; "author"; "image"; "datePublished" ],
    StructuredDataHelpers.article minimalPage
    "person (minimal)",
    "Person",
    [ "name"; "jobTitle"; "image"; "email"; "url" ],
    StructuredDataHelpers.person minimalPage
    "event (minimal)",
    "Event",
    [ "name"; "description"; "startDate"; "endDate"; "location"; "image" ],
    StructuredDataHelpers.event minimalPage
    "organization (minimal)",
    "Organization",
    [ "name"; "url"; "logo"; "description" ],
    StructuredDataHelpers.organization minimalPage
    "webSite (no search)", "WebSite", [ "name"; "url" ], StructuredDataHelpers.webSite "Site" "https://example.com" None
    "faqPage (empty)", "FAQPage", [ "mainEntity" ], StructuredDataHelpers.faqPage []
    "breadcrumb (empty)", "BreadcrumbList", [ "itemListElement" ], StructuredDataHelpers.breadcrumb []
    "product (bare)",
    "Product",
    [ "name"; "description" ],
    StructuredDataHelpers.product "Widget" "A fine widget" None None
]

// ─── 3. JSON-LD emitter conformance ──────────────────────────────────

let private jsonLdTests =
    testList "JSON-LD emitters" [
        testList "structure + @context/@type + required properties (populated page)" [
            for label, expectedType, required, payload in jsonLdCases ->
                testCase label
                <| fun _ -> expectConformant label (Conformance.jsonLdOfType label expectedType required payload)
        ]

        testList "structure holds with no frontmatter at all (GP 11 degrade-to-empty)" [
            for label, expectedType, required, payload in minimalJsonLdCases ->
                testCase label
                <| fun _ -> expectConformant label (Conformance.jsonLdOfType label expectedType required payload)
        ]

        testList "required properties carry a value when the source data does" [
            for label, _, required, payload in jsonLdCases ->
                testCase label
                <| fun _ -> expectConformant label (Conformance.jsonLdPopulated label required payload)
        ]

        testCase "articleFromNarrative conforms and threads the document's provenance"
        <| fun _ ->
            match StructuredDataHelpers.articleFromNarrative narrativePage narrativeDoc with
            | None -> failtest "a document carrying Provenance must emit an Article"
            | Some payload ->
                let required = [
                    "headline"
                    "description"
                    "datePublished"
                    "dateModified"
                    "identifier"
                    "provider"
                ]

                expectConformant
                    "articleFromNarrative"
                    (Conformance.jsonLdOfType "articleFromNarrative" "Article" required payload)

                expectConformant
                    "articleFromNarrative"
                    (Conformance.jsonLdPopulated "articleFromNarrative" required payload)

        testCase "articleFromNarrative emits nothing for a document with no provenance"
        <| fun _ ->
            Expect.isNone
                (StructuredDataHelpers.articleFromNarrative narrativePage narrativeBase)
                "no provenance → no Article node (an empty Article is worse than none)"

        testCase "the SearchAction target is a typed EntryPoint carrying the query token"
        <| fun _ ->
            let payload =
                StructuredDataHelpers.webSite
                    "S"
                    "https://example.com"
                    (Some "https://example.com/s?q={search_term_string}")

            Expect.stringContains payload "\"@type\":\"SearchAction\"" "potentialAction is a SearchAction"
            Expect.stringContains payload "\"@type\":\"EntryPoint\"" "its target is an EntryPoint"
            Expect.stringContains payload "{search_term_string}" "the urlTemplate keeps Google's literal token"
            Expect.stringContains payload "required name=search_term_string" "query-input names the token"

        testCase "a `</script>` in page content cannot break out of the embedding block"
        <| fun _ ->
            let hostile =
                mkPage "x" "</script><script>alert(1)</script>" "</script>" (Markdown "x") [ "author", "</script>" ]

            let payload = StructuredDataHelpers.article hostile

            expectConformant
                "article (hostile content)"
                (Conformance.jsonLdStructure "article (hostile content)" payload)

            Expect.stringContains payload "<\\/script>" "the emitter escapes the closing sequence"

        testCase "openGraphFromNarrative emits the required Open Graph set"
        <| fun _ ->
            let pairs = StructuredDataHelpers.openGraphFromNarrative narrativePage narrativeDoc

            expectConformant
                "openGraphFromNarrative"
                (Conformance.metaPairs
                    "openGraphFromNarrative"
                    [ "og:title"; "og:type"; "og:description"; "og:url"; "og:locale" ]
                    pairs)

            Expect.equal
                (pairs |> List.tryFind (fst >> (=) "og:locale") |> Option.map snd)
                (Some "en_GB")
                "og:locale uses the underscore form"

        testCase "twitterCardFromNarrative emits a known card type and the required set"
        <| fun _ ->
            let pairs =
                StructuredDataHelpers.twitterCardFromNarrative narrativePage narrativeDoc

            expectConformant
                "twitterCardFromNarrative"
                (Conformance.metaPairs
                    "twitterCardFromNarrative"
                    [ "twitter:card"; "twitter:title"; "twitter:description" ]
                    pairs)

            let card = pairs |> List.tryFind (fst >> (=) "twitter:card") |> Option.map snd

            Expect.isTrue
                (List.contains card [ Some "summary"; Some "summary_large_image"; Some "app"; Some "player" ])
                "twitter:card is one of the four defined card types"
    ]

// ─── 4. Sitemap conformance ──────────────────────────────────────────
//
// `generateUrlSetFrom` is the assembly-internal body builder; it is
// reached here through its public delegators so the pack needs no
// change to the shipped package (GP 13). `generateWith` is a one-line
// wrapper over it, so the bytes under test are the same bytes.

let private baseUrl = "https://example.com"

let private withStatus (status: PublishStatus) (page: PublicPage) : PublicPage = { page with Status = status }

let private withAudience (audience: PageAudience) (page: PublicPage) : PublicPage = { page with Audience = audience }

let private sitemapPages = [
    mkPage "about" "About" "" (Markdown "x") []
    mkPage "contact" "Contact" "" (Markdown "x") []
    mkPage "secret" "Secret" "" (Markdown "x") [ "sitemap", "exclude" ]
    mkPage "draft" "Draft" "" (Markdown "x") [] |> withStatus Draft
    mkPage "archived" "Archived" "" (Markdown "x") [] |> withStatus Archived
    mkPage "later" "Later" "" (Markdown "x") []
    |> withStatus (Scheduled(DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)))
    mkPage "members" "Members" "" (Markdown "x") []
    |> withAudience PageAudience.Authenticated
    mkPage "clients" "Clients" "" (Markdown "x") []
    |> withAudience (PageAudience.ScopeGated [ "admin" ])
]

let private sitemapTests =
    testList "sitemap <urlset>" [
        testCase "the generated body conforms to the sitemaps.org 0.9 schema"
        <| fun _ ->
            let xml =
                SitemapGenerator.generateWith baseUrl sitemapPages [ Slug "tag/news"; Slug "tag/product" ]

            expectConformant "sitemap.xml" (Conformance.sitemapUrlSet "sitemap.xml" xml)

        testCase "every excluded slug class is absent, and the public ones are present"
        <| fun _ ->
            let xml = SitemapGenerator.generateWith baseUrl sitemapPages [ Slug "tag/news" ]
            let advertised = Conformance.advertisedSlugs baseUrl xml

            for slug in [ "about"; "contact"; "tag/news" ] do
                Expect.contains advertised slug (sprintf "'%s' must be advertised" slug)

            for slug in [ "secret"; "draft"; "archived"; "later"; "members"; "clients" ] do
                Expect.isFalse (List.contains slug advertised) (sprintf "'%s' must never reach a crawler" slug)

        testCase "a dynamic slug duplicating a page slug is deduped, not emitted twice"
        <| fun _ ->
            let xml =
                SitemapGenerator.generateWith baseUrl sitemapPages [ Slug "about"; Slug "tag/news" ]

            expectConformant
                "sitemap.xml (overlapping dynamic slug)"
                (Conformance.sitemapUrlSet "sitemap.xml (overlapping dynamic slug)" xml)

            let advertised = Conformance.advertisedSlugs baseUrl xml
            Expect.equal (advertised |> List.filter ((=) "about") |> List.length) 1 "'about' appears exactly once"

        testCase "a trailing slash on the base URL does not double in <loc>"
        <| fun _ ->
            let xml =
                SitemapGenerator.generate (baseUrl + "/") [ mkPage "about" "About" "" (Markdown "x") [] ]

            expectConformant
                "sitemap.xml (trailing-slash base)"
                (Conformance.sitemapUrlSet "sitemap.xml (trailing-slash base)" xml)

            Expect.isFalse (xml.Contains "example.com//about") "no doubled separator"

        testCase "XML-significant characters in a slug are escaped, keeping the body well-formed"
        <| fun _ ->
            let xml =
                SitemapGenerator.generate baseUrl [ mkPage "search?a=1&b=2" "Q" "" (Markdown "x") [] ]

            expectConformant "sitemap.xml (escaping)" (Conformance.sitemapUrlSet "sitemap.xml (escaping)" xml)
            Expect.stringContains xml "&amp;" "the ampersand is escaped"

        testCase "the sharded <sitemapindex> conforms and its children are absolute"
        <| fun _ ->
            let universe = [ for i in 1..25 -> Slug(sprintf "page-%02d" i), Some "2026-06-19" ]
            let defaults = SitemapGenerator.SitemapShardingOptions.defaults

            let shards =
                SitemapGenerator.shardUniverse { defaults with Threshold = 10 } universe

            let xml = SitemapGenerator.generateSitemapIndex baseUrl shards
            expectConformant "sitemapindex" (Conformance.sitemapIndex "sitemapindex" xml)
            Expect.equal (List.length shards) 3 "25 entries at a threshold of 10 shard into 3"

        testCase "an empty universe still emits a well-formed <urlset>"
        <| fun _ ->
            let xml = SitemapGenerator.generate baseUrl []
            expectConformant "sitemap.xml (empty)" (Conformance.sitemapUrlSet "sitemap.xml (empty)" xml)
    ]

// ─── 5. Canonical + hreflang across the host-aware site registry ──────
//
// A page served on a satellite host must canonicalise to THAT host
// (Phase 145 passes each site's own `BaseUrl` into the Phase 148
// self-canonical enrichment), and the locale cluster spanning the two
// hosts must be reciprocal. Both are checked over the rendered
// document, not over the helper's return value, so the head-injection
// step is inside the assertion.

/// Site definitions as data — the registry's host → base-URL mapping is
/// all these rules need, and building it from `PublicSiteDef` records
/// keeps the pack filesystem-free (GP 12).
let private sites = [
    PublicSite.create "en" [ "example.com"; "www.example.com" ] "https://example.com" (ContentRoot "en-content")
    PublicSite.create "fr" [ "example.fr" ] "https://example.fr" (ContentRoot "fr-content")
]

let private siteByName (name: string) =
    sites |> List.find (fun s -> s.Name = name)

let private enAbout = "https://example.com/about"
let private frAbout = "https://example.fr/a-propos"

let private hreflangValue (pairs: (string * string) list) =
    pairs
    |> List.map (fun (lang, url) -> sprintf "%s=%s" lang url)
    |> String.concat ", "

let private enClusterPage =
    mkPage "about" "About" "Who we are" (Markdown "x") [
        "hreflang", hreflangValue [ "en", enAbout; "fr", frAbout; "x-default", enAbout ]
    ]

let private frClusterPage =
    mkPage "a-propos" "À propos" "Qui nous sommes" (Markdown "x") [
        "hreflang", hreflangValue [ "fr", frAbout; "en", enAbout; "x-default", enAbout ]
    ]

/// Render a page the way a site's layout does: the self-canonical
/// enrichment for that site's origin, the shared head tags, then the
/// Phase 111 head injection over the rendered document.
let private renderForSite (site: PublicSiteDef) (page: PublicPage) : string =
    let enriched = NarrativeLayout.SelfCanonical.enrichPage site.BaseUrl page

    let document =
        html [] [ head [] (NarrativeLayout.headTags enriched); body [] [ str enriched.Title ] ]

    PageHeadInjection.injectFromPage enriched (RenderView.AsString.htmlDocument document)

let private canonicalHreflangTests =
    testList "canonical + hreflang (host-aware)" [
        testCase "each site canonicalises a page to its OWN origin"
        <| fun _ ->
            for siteName, page, expected in [ "en", enClusterPage, enAbout; "fr", frClusterPage, frAbout ] do
                let site = siteByName siteName
                let rendered = renderForSite site page

                expectConformant
                    (sprintf "canonical on site '%s'" siteName)
                    (Conformance.canonicalSelfReference (sprintf "site '%s'" siteName) expected rendered)

        testCase "a page declaring an explicit canonical is not double-canonicalised"
        <| fun _ ->
            let explicitPage =
                mkPage "about" "About" "" (Markdown "x") [ "head:canonical", "https://example.com/canonical-home" ]

            let rendered = renderForSite (siteByName "en") explicitPage

            expectConformant
                "explicit canonical"
                (Conformance.canonicalSelfReference "explicit canonical" "https://example.com/canonical-home" rendered)

        testCase "the root and index slugs canonicalise to the origin, not to a bare host"
        <| fun _ ->
            for slug in [ ""; "index" ] do
                let rendered =
                    renderForSite (siteByName "en") (mkPage slug "Home" "" (Markdown "x") [])

                expectConformant
                    (sprintf "canonical for slug '%s'" slug)
                    (Conformance.canonicalSelfReference (sprintf "slug '%s'" slug) "https://example.com/" rendered)

        testCase "the two-host locale cluster is reciprocal, self-referencing and well-formed"
        <| fun _ ->
            let cluster =
                [
                    siteByName "en", enClusterPage, enAbout
                    siteByName "fr", frClusterPage, frAbout
                ]
                |> List.map (fun (site, page, selfUrl) -> selfUrl, Conformance.alternateLinks (renderForSite site page))

            expectConformant "locale cluster" (Conformance.hreflangCluster "locale cluster" cluster)

        testCase "the self-referencing hreflang agrees with the page's canonical"
        <| fun _ ->
            for site, page, selfUrl in
                [
                    siteByName "en", enClusterPage, enAbout
                    siteByName "fr", frClusterPage, frAbout
                ] do
                let rendered = renderForSite site page
                let canonical = Conformance.canonicalHrefs rendered |> List.exactlyOne

                let selfAlternate =
                    Conformance.alternateLinks rendered
                    |> List.filter (fun (_, url) -> url = canonical)

                Expect.isNonEmpty
                    selfAlternate
                    (sprintf "the cluster on '%s' must include an alternate pointing at the canonical" selfUrl)

        testCase "a page with no hreflang frontmatter emits no alternates (GP 11)"
        <| fun _ ->
            let rendered =
                renderForSite (siteByName "en") (mkPage "plain" "Plain" "" (Markdown "x") [])

            Expect.isEmpty (Conformance.alternateLinks rendered) "no hreflang key → no rel=alternate links"

        testCase "injected JSON-LD survives the head injection and is still conformant"
        <| fun _ ->
            let page =
                mkPage "q1" "Q1" "The quarter" (Markdown "x") [
                    "head:canonical", "https://example.com/q1"
                    "head:jsonld:1", StructuredDataHelpers.course "Widgets 101" "Example Widgets" "An introduction"
                ]

            let rendered = renderForSite (siteByName "en") page
            let payloads = Conformance.embeddedJsonLd rendered
            Expect.equal (List.length payloads) 1 "one JSON-LD block reached the document"

            expectConformant
                "injected JSON-LD"
                (Conformance.jsonLdOfType
                    "injected JSON-LD"
                    "Course"
                    [ "name"; "provider"; "description" ]
                    payloads.Head)

        testCase "head injection leaves exactly one </head> in the document"
        <| fun _ ->
            let rendered = renderForSite (siteByName "en") enClusterPage

            Expect.equal
                (Regex.Matches(rendered, "</head>", RegexOptions.IgnoreCase).Count)
                1
                "the injection must not duplicate or drop the closing tag"
    ]

// ─── 6. Robots directives ────────────────────────────────────────────

let private robotsTests =
    testList "robots directives" [
        testCase "each shipped directive spelling is emitted verbatim and conforms"
        <| fun _ ->
            for content in
                [
                    "noindex"
                    "noindex, nofollow"
                    "noarchive"
                    "noindex,nofollow,noarchive"
                    "max-snippet:-1"
                ] do
                let page = mkPage "p" "P" "" (Markdown "x") [ "robots", content ]
                let emitted = Conformance.robotsMetaContents (renderForSite (siteByName "en") page)

                Expect.equal emitted [ content.Trim() ] (sprintf "'%s' emitted once, verbatim" content)
                expectConformant (sprintf "robots '%s'" content) (Conformance.robotsContent "robots" content)

        testCase "a blank robots key emits no tag rather than an empty directive"
        <| fun _ ->
            for value in [ ""; "   " ] do
                let page = mkPage "p" "P" "" (Markdown "x") [ "robots", value ]

                Expect.isEmpty
                    (Conformance.robotsMetaContents (renderForSite (siteByName "en") page))
                    "a blank value must not emit <meta name=\"robots\" content=\"\">"

        testCase "surrounding whitespace on the directive is trimmed before emission"
        <| fun _ ->
            let page = mkPage "p" "P" "" (Markdown "x") [ "robots", "  noindex, nofollow  " ]

            Expect.equal
                (Conformance.robotsMetaContents (renderForSite (siteByName "en") page))
                [ "noindex, nofollow" ]
                "the emitted content is trimmed"

        testCase "a page with no robots key emits no robots tag (GP 11)"
        <| fun _ ->
            Expect.isEmpty
                (Conformance.robotsMetaContents (renderForSite (siteByName "en") (mkPage "p" "P" "" (Markdown "x") [])))
                "absent key → no tag"

        testCase "a sitemap-excluded page is excluded from the sitemap whatever its robots key"
        <| fun _ ->
            let pages = [
                mkPage "public" "Public" "" (Markdown "x") []
                mkPage "hidden" "Hidden" "" (Markdown "x") [ "sitemap", "exclude"; "robots", "noindex" ]
            ]

            let advertised =
                Conformance.advertisedSlugs baseUrl (SitemapGenerator.generate baseUrl pages)

            expectConformant
                "noindex vs sitemap"
                (Conformance.noindexNotAdvertised "noindex vs sitemap" [ "hidden" ] advertised)
    ]

// ─── 7. The rules are proven to bite ─────────────────────────────────
//
// A conformance rule nobody has watched fail is indistinguishable from
// a rule that cannot fail. Each case below hands a validator a
// deliberately malformed input and asserts it REPORTS — the fail-before
// half of the acceptance, kept in the suite rather than performed once
// by hand and forgotten.

let private ruleSelfTests =
    testList "the lint rejects malformed input" [
        testCase "malformed JSON is reported, not swallowed"
        <| fun _ -> Expect.isNonEmpty (Conformance.jsonLdStructure "probe" "{\"@type\": ") "a truncated payload fails"

        testCase "a wrong @context is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.jsonLdStructure "probe" """{"@context":"https://schema.org/","@type":"Article"}""")
                "a trailing slash on the context is not the emitted form"

        testCase "a missing @context or @type is reported"
        <| fun _ ->
            Expect.isNonEmpty (Conformance.jsonLdStructure "probe" """{"@type":"Article"}""") "no @context"

            Expect.isNonEmpty
                (Conformance.jsonLdStructure "probe" """{"@context":"https://schema.org","headline":"x"}""")
                "no @type"

        testCase "an untyped nested entity is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.jsonLdStructure
                    "probe"
                    """{"@context":"https://schema.org","@type":"Article","author":{"name":"Ada"}}""")
                "author is an object with no @type"

        testCase "a non-contiguous itemListElement position run is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.jsonLdStructure
                    "probe"
                    """{"@context":"https://schema.org","@type":"ItemList","itemListElement":[{"@type":"ListItem","position":1},{"@type":"ListItem","position":3}]}""")
                "positions 1,3 skip 2"

        testCase "a raw </script> sequence in a payload is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.jsonLdStructure
                    "probe"
                    """{"@context":"https://schema.org","@type":"Article","headline":"</script>"}""")
                "an unescaped closing sequence breaks out of the block"

        testCase "an absent required property is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.jsonLdOfType
                    "probe"
                    "Article"
                    [ "headline" ]
                    """{"@context":"https://schema.org","@type":"Article"}""")
                "headline is required for an Article rich result"

        testCase "the wrong @type for the emitter is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.jsonLdOfType
                    "probe"
                    "Article"
                    []
                    """{"@context":"https://schema.org","@type":"BlogPosting"}""")
                "a retyped emitter is a silent rich-result change"

        testCase "an empty required value on a populated page is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.jsonLdPopulated
                    "probe"
                    [ "headline" ]
                    """{"@context":"https://schema.org","@type":"Article","headline":""}""")
                "an emitter that stopped reading its key still emits the key"

        testCase "a non-<urlset> root is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.sitemapUrlSet
                    "probe"
                    """<?xml version="1.0"?><pages xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"/>""")
                "wrong root element"

        testCase "a <urlset> outside the sitemaps.org namespace is reported"
        <| fun _ ->
            Expect.isNonEmpty (Conformance.sitemapUrlSet "probe" """<?xml version="1.0"?><urlset/>""") "no namespace"

        testCase "malformed sitemap XML is reported"
        <| fun _ -> Expect.isNonEmpty (Conformance.sitemapUrlSet "probe" "<urlset><url>") "unclosed elements"

        testCase "a relative <loc> is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.sitemapUrlSet
                    "probe"
                    """<?xml version="1.0"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>/about</loc></url></urlset>""")
                "search engines require absolute URLs in <loc>"

        testCase "a duplicate <loc> is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.sitemapUrlSet
                    "probe"
                    """<?xml version="1.0"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://e.com/a</loc></url><url><loc>https://e.com/a</loc></url></urlset>""")
                "a slug advertised twice"

        testCase "an unparseable <lastmod> is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.sitemapUrlSet
                    "probe"
                    """<?xml version="1.0"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://e.com/a</loc><lastmod>yesterday</lastmod></url></urlset>""")
                "<lastmod> must be a W3C datetime"

        testCase "a <sitemapindex> containing <url> children is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.sitemapIndex
                    "probe"
                    """<?xml version="1.0"?><sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://e.com/a</loc></url></sitemapindex>""")
                "an index lists <sitemap>, not <url>"

        testCase "a missing, wrong or duplicated canonical is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.canonicalSelfReference "probe" enAbout "<html><head></head></html>")
                "no canonical"

            Expect.isNonEmpty
                (Conformance.canonicalSelfReference
                    "probe"
                    enAbout
                    """<link rel="canonical" href="https://elsewhere.test/x">""")
                "canonical points at another page"

            Expect.isNonEmpty
                (Conformance.canonicalSelfReference
                    "probe"
                    enAbout
                    (sprintf """<link rel="canonical" href="%s"><link rel="canonical" href="%s">""" enAbout frAbout))
                "two canonicals"

            Expect.isNonEmpty
                (Conformance.canonicalSelfReference "probe" "/about" """<link rel="canonical" href="/about">""")
                "a relative canonical"

        testCase "a non-reciprocal hreflang cluster is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.hreflangCluster "probe" [
                    enAbout, [ "en", enAbout; "fr", frAbout ]
                    frAbout, [ "fr", frAbout ]
                ])
                "the fr page does not point back at the en page"

        testCase "a cluster member that does not self-reference is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.hreflangCluster "probe" [
                    enAbout, [ "fr", frAbout ]
                    frAbout, [ "en", enAbout; "fr", frAbout ]
                ])
                "the en page omits its own alternate"

        testCase "disagreeing language labels across a cluster are reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.hreflangCluster "probe" [
                    enAbout, [ "en", enAbout; "de", frAbout ]
                    frAbout, [ "fr", frAbout; "en", enAbout ]
                ])
                "the en page calls the fr page 'de'"

        testCase "a malformed language tag and a relative alternate are reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.hreflangCluster "probe" [ enAbout, [ "english", enAbout ] ])
                "'english' is not a BCP-47 tag"

            Expect.isNonEmpty
                (Conformance.hreflangCluster "probe" [ "/about", [ "en", "/about" ] ])
                "a relative alternate"

        testCase "two x-default alternates on one page are reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.hreflangCluster "probe" [
                    enAbout, [ "en", enAbout; "x-default", enAbout; "x-default", frAbout ]
                ])
                "at most one x-default is meaningful"

        testCase "an unknown, repeated or contradictory robots directive is reported"
        <| fun _ ->
            Expect.isNonEmpty (Conformance.robotsContent "probe" "noindexx") "a typo'd directive"
            Expect.isNonEmpty (Conformance.robotsContent "probe" "noindex, noindex") "a repeated directive"
            Expect.isNonEmpty (Conformance.robotsContent "probe" "index, noindex") "a contradictory pair"
            Expect.isNonEmpty (Conformance.robotsContent "probe" "") "a blank directive"

        testCase "a noindex page advertised in the sitemap is reported"
        <| fun _ ->
            Expect.isNonEmpty
                (Conformance.noindexNotAdvertised "probe" [ "hidden" ] [ "public"; "hidden" ])
                "Search Console reports this as a coverage error"

        testCase "a missing or duplicated meta pair is reported"
        <| fun _ ->
            Expect.isNonEmpty (Conformance.metaPairs "probe" [ "og:title" ] [ "og:type", "article" ]) "og:title absent"

            Expect.isNonEmpty
                (Conformance.metaPairs "probe" [] [ "og:title", "a"; "og:title", "b" ])
                "og:title emitted twice"

            Expect.isNonEmpty (Conformance.metaPairs "probe" [] [ "og:title", "" ]) "blank content"

        testCase "the conformant fixtures do NOT trip the rules — the lint is not vacuously red"
        <| fun _ ->
            Expect.isEmpty
                (Conformance.jsonLdOfType "probe" "Article" [ "headline" ] (StructuredDataHelpers.article populatedPage))
                "a shipped payload passes"

            Expect.isEmpty
                (Conformance.sitemapUrlSet "probe" (SitemapGenerator.generate baseUrl sitemapPages))
                "a shipped sitemap passes"

            Expect.isEmpty (Conformance.robotsContent "probe" "noindex, nofollow") "a valid directive passes"
    ]

// ─── 8. Phase 737 — the pinned gap, closed behind an opt-in ──────────
//
// Phase 212 pinned a real gap here as data: `SitemapGenerator.entriesAt`
// never consulted the Phase 152 `robots` key, so a page declaring
// `robots: noindex` was still advertised in `sitemap.xml` — the "Submitted
// URL marked 'noindex'" coverage error, a sitemap asking a crawler to
// index a URL the page itself refuses. The rule
// (`Conformance.noindexNotAdvertised`) was correct throughout and was
// never weakened to make the emitter pass; the pin carried the FINDING
// until the behaviour could change.
//
// Phase 737 closes it behind `SitemapUniverseOptions.ExcludeNoindex`,
// default off, because closing it unconditionally would have moved the
// sitemap body — and with it the Phase 149 ETag — of every deployment
// already using the key (GP 11). So the pin becomes THREE obligations,
// and all three are live assertions rather than a carve-out:
//
//   * opted in, the 212 rule is asserted DIRECTLY: no noindex page is
//     advertised, and no known-gap exemption remains anywhere in the pack;
//   * NOT opted in, the universe is byte-for-byte what it was — proven
//     against an explicit expected body, not merely against itself, so a
//     future default flip cannot pass this pack;
//   * the documented `sitemap: exclude` workaround still holds, on BOTH
//     paths. The two mechanisms are a union of exclusions, so a deployment
//     that adopted the workaround adopts the flag with no content edit.

let private noindexPages = [
    mkPage "public" "Public" "" (Markdown "x") []
    mkPage "hidden" "Hidden" "" (Markdown "x") [ "robots", "noindex" ]
]

/// A clock pinned inside every fixture page's visibility window, so the
/// cases below decide on the `robots` key alone.
let private pinnedNow = DateTimeOffset.Parse "2026-06-19T00:00:00Z"

/// The advertised slugs for a given set of universe options — the deduped
/// universe `<urlset>` is emitted from, one line upstream of the body.
/// Taken here rather than off the rendered XML because the body builder is
/// assembly-`internal` (this pack reaches it only through the public
/// delegators — see the header), and because the Phase 212 rule under test
/// takes a slug list. The rendered opted-in body is asserted over the real
/// handler in `SitemapSearchIndexTests`' Phase 737 list, and
/// `generateUrlSetFrom`'s conformance over an arbitrary universe — the
/// shorter opted-in one included — is already pinned by `sitemapTests`.
let private advertisedWith
    (universeOptions: SitemapGenerator.SitemapUniverseOptions)
    (pages: PublicPage list)
    : string list =
    SitemapGenerator.entriesAtWith universeOptions pinnedNow pages []
    |> List.map (fun (Slug s, _) -> s)

let private optedIn: SitemapGenerator.SitemapUniverseOptions = { ExcludeNoindex = true }

let private noindexExclusionTests =
    testList "Phase 737 — robots:noindex excluded from sitemap.xml behind an opt-in" [

        testCase "opted in: a robots:noindex page is absent from <urlset> and the 212 rule passes directly"
        <| fun _ ->
            let advertised = advertisedWith optedIn noindexPages

            Expect.contains advertised "public" "an indexable page is still advertised"
            Expect.isFalse (List.contains "hidden" advertised) "the noindex page is not advertised"

            expectConformant
                "noindex vs sitemap (opted in)"
                (Conformance.noindexNotAdvertised "noindex vs sitemap (opted in)" [ "hidden" ] advertised)

        testCase "NOT opted in: the body is byte-for-byte the pre-737 output (GP 11)"
        <| fun _ ->
            // The pre-737 emitter advertised BOTH pages; that is what a
            // non-adopting deployment must keep seeing, ETag included.
            // Asserted against an explicit expected body rather than
            // against another call of the same code path — a default
            // flipped to `true` would sail through the latter.
            let expected =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
                + "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n"
                + "  <url>\n    <loc>https://example.com/public</loc>\n    <lastmod>2026-06-19</lastmod>\n  </url>\n"
                + "  <url>\n    <loc>https://example.com/hidden</loc>\n    <lastmod>2026-06-19</lastmod>\n  </url>\n"
                + "</urlset>\n"

            let actual = (SitemapGenerator.generate baseUrl noindexPages).Replace("\r\n", "\n")

            Expect.equal actual expected "the default path emits the pre-737 body, byte for byte"

            Expect.equal
                (SitemapGenerator.entriesAt pinnedNow noindexPages [])
                (SitemapGenerator.entriesAtWith SitemapGenerator.SitemapUniverseOptions.defaults pinnedNow noindexPages [])
                "entriesAt IS entriesAtWith at defaults"

        testCase "the documented workaround holds on both paths: sitemap:exclude alongside robots:noindex"
        <| fun _ ->
            let pages = [
                mkPage "public" "Public" "" (Markdown "x") []
                mkPage "hidden" "Hidden" "" (Markdown "x") [ "robots", "noindex"; "sitemap", "exclude" ]
            ]

            for label, options in
                [
                    "workaround (default)", SitemapGenerator.SitemapUniverseOptions.defaults
                    "workaround (opted in)", optedIn
                ] do
                expectConformant
                    label
                    (Conformance.noindexNotAdvertised label [ "hidden" ] (advertisedWith options pages))

        testCase "the exclusions are a UNION — neither key weakens the other"
        <| fun _ ->
            // Opted in, `sitemap: exclude` still excludes a page whose
            // robots key positively invites indexing.
            let pages = [
                mkPage "public" "Public" "" (Markdown "x") []
                mkPage "excluded" "Excluded" "" (Markdown "x") [ "sitemap", "exclude"; "robots", "index, follow" ]
            ]

            let advertised = advertisedWith optedIn pages
            Expect.contains advertised "public" "the plain page is advertised"
            Expect.isFalse (List.contains "excluded" advertised) "sitemap:exclude still wins whatever robots says"

        testCase "'none' resolves to noindex; an indexing directive and a blank key do not"
        <| fun _ ->
            // `none` is the documented shorthand for `noindex, nofollow`.
            // A reader matching only the literal `noindex` would advertise
            // a page that is just as firmly excluded.
            let cases = [
                "noindex", true
                "none", true
                "NoIndex", true
                "  noindex , nofollow  ", true
                "noindex,nofollow,noarchive", true
                "index, follow", false
                "noarchive", false
                "nofollow", false
                "", false
                "   ", false
            ]

            for value, expected in cases do
                let page = mkPage "p" "P" "" (Markdown "x") [ "robots", value ]

                Expect.equal
                    (SitemapGenerator.resolvesToNoindex page)
                    expected
                    (sprintf "robots '%s' resolves to noindex = %b" value expected)

            Expect.isFalse
                (SitemapGenerator.resolvesToNoindex (mkPage "p" "P" "" (Markdown "x") []))
                "an absent robots key never excludes"

        testCase "opting in excludes nothing on a site that declares no robots key"
        <| fun _ ->
            // The flag is not a blanket filter: it moves the universe only
            // where a `robots` key actually says noindex, which is why a
            // deployment with none can adopt it with no crawl impact.
            Expect.equal
                (advertisedWith optedIn sitemapPages)
                (advertisedWith SitemapGenerator.SitemapUniverseOptions.defaults sitemapPages)
                "the representative page set carries no robots key — identical universes"
    ]

let tests =
    testList "PublicRendering — Phase 212 SEO / structured-data conformance" [
        jsonLdTests
        sitemapTests
        canonicalHreflangTests
        robotsTests
        ruleSelfTests
        noindexExclusionTests
    ]