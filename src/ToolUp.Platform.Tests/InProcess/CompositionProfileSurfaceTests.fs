module ToolUp.Platform.Tests.InProcess.CompositionProfileSurfaceTests

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Expecto
open FSharp.Reflection
open ToolUp.Platform

// ─── forge's composition profile, held to its renderings and to forge ─
//
// `spec/` publishes forge's composition profile (`server-app-host`, a
// vocabulary profile of the App-composition wire specification,
// https://github.com/Fuaran-Build/fuaran-app-composition-specification):
// the document, its registry, its vectors, and `surface-binding.json`,
// which says what each registered token reduces to in forge. This pack is
// the profile's §P7 gate. Every check is a pure function returning the
// disagreements it found, so the self-test at the bottom can plant a
// mismatch and watch each one go red.
//
// What it holds together:
//   1. the fixtures and their recorded digests, and the profile version
//      across the document, the registry, the manifest and the binding;
//   2. the document's tables against the registry, in both directions;
//   3. the registered setting grammars against the vectors that must
//      admit, or refuse, a setting value;
//   4. §P8's facet declaration against the core's facet-family vectors for
//      this profile, pinned by digest in `spec/fixtures/core/`;
//   5. base-token ownership: the registry's root bases are distinct, and a
//      core vector refused because its root's vocabulary resolves nowhere
//      does not name one of them;
//   6. the registry against forge's reflected composable surface, through
//      the binding: every registered token reduces to something forge has,
//      and every reflected slot, setting and setting value is registered
//      or named as deliberately unregistered.

let private specDir =
    lazy
        (let start = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

         let rec up (dir: DirectoryInfo) =
             if isNull dir then
                 failwithf "no spec/registry.json above %s" start
             elif File.Exists(Path.Combine(dir.FullName, "spec", "registry.json")) then
                 Path.Combine(dir.FullName, "spec")
             else
                 up dir.Parent

         up (DirectoryInfo start))

let private readJson (path: string) =
    JsonDocument.Parse(File.ReadAllText path).RootElement

let private prop (name: string) (e: JsonElement) : JsonElement option =
    match e.ValueKind with
    | JsonValueKind.Object ->
        match e.TryGetProperty name with
        | true, v -> Some v
        | _ -> None
    | _ -> None

let private str (name: string) (e: JsonElement) =
    prop name e
    |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
    |> Option.map _.GetString()

let private strings (e: JsonElement option) : string list =
    match e with
    | Some v when v.ValueKind = JsonValueKind.Array -> [ for x in v.EnumerateArray() -> x.GetString() ]
    | _ -> []

let private members (e: JsonElement option) : (string * JsonElement) list =
    match e with
    | Some v when v.ValueKind = JsonValueKind.Object -> [ for p in v.EnumerateObject() -> p.Name, p.Value ]
    | _ -> []

let private sha256Hex (bytes: byte array) =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

let private onlyIn (a: string seq) (b: string seq) =
    Set.difference (Set.ofSeq a) (Set.ofSeq b) |> Set.toList

let private both what (left: string, a: string seq) (right: string, b: string seq) = [
    for x in onlyIn a b -> sprintf "%s: %s lists %s, %s does not" what left x right
    for x in onlyIn b a -> sprintf "%s: %s lists %s, %s does not" what right x left
]

/// The artefacts the checks read, loaded once.
type Profile = {
    Document: string
    Registry: JsonElement
    Manifest: JsonElement
    Binding: JsonElement
    Dir: string
}

let private load () = {
    Document = File.ReadAllText(Path.Combine(specDir.Value, "composition-profile.md")).Replace("\r\n", "\n")
    Registry = readJson (Path.Combine(specDir.Value, "registry.json"))
    Manifest = readJson (Path.Combine(specDir.Value, "fixtures", "manifest.json"))
    Binding = readJson (Path.Combine(specDir.Value, "surface-binding.json"))
    Dir = specDir.Value
}

let private tokensOf (p: Profile) (slot: string) =
    p.Registry |> prop "tokens" |> Option.bind (prop slot)

// ── markdown readers ──────────────────────────────────────────────────

/// The text after `heading` up to the next heading of level 2 to 4.
let private sectionOf (doc: string) (heading: string) : string option =
    let start = doc.IndexOf(heading, StringComparison.Ordinal)

    if start < 0 then
        None
    else
        let rest = doc.Substring(start + heading.Length)
        let next = Regex.Match(rest, @"\n#{2,4} ")
        Some(if next.Success then rest.Substring(0, next.Index) else rest)

let private firstColumnTokens (section: string) = [
    for m in Regex.Matches(section, @"(?m)^\|\s*`([^`]+)`\s*\|") -> m.Groups[1].Value
]

// ── 1. fixtures, digests and versions ─────────────────────────────────

let private vectorEntries (p: Profile) =
    p.Manifest
    |> prop "vectors"
    |> Option.map _.EnumerateArray()
    |> Option.map List.ofSeq
    |> Option.defaultValue []

let private coreEntries (p: Profile) =
    p.Manifest
    |> prop "core"
    |> Option.bind (prop "vectors")
    |> Option.map (_.EnumerateArray() >> List.ofSeq)
    |> Option.defaultValue []

let fixtureFindings (p: Profile) : string list = [
    let fixtures = Path.Combine(p.Dir, "fixtures")
    let entries = vectorEntries p

    if entries.IsEmpty then
        "the manifest lists no profile vectors"

    let ids = entries |> List.choose (str "id")

    for id in ids |> List.countBy id |> List.filter (snd >> (<) 1) |> List.map fst do
        sprintf "vector id %s is listed twice" id

    for e in entries @ coreEntries p do
        let id = str "id" e |> Option.defaultValue "?"

        match str "file" e, str "sha256" e with
        | Some file, Some digest ->
            let path = Path.Combine(fixtures, file)

            if not (File.Exists path) then
                sprintf "%s: %s is missing" id file
            else
                let actual = sha256Hex (File.ReadAllBytes path)

                if actual <> digest then
                    sprintf "%s: %s hashes to %s, the manifest records %s" id file actual digest
        | _ -> sprintf "%s: the entry names no file or no digest" id

    for e in entries do
        let id = str "id" e |> Option.defaultValue "?"

        for key, expected in
            [
                "family", "profile-server-app-host"
                "profile", "server-app-host"
                "home", "server-app-host"
            ] do
            if str key e <> Some expected then
                sprintf "%s: %s is %A, not %s" id key (str key e) expected

    let listed = entries |> List.choose (str "file") |> Set.ofList

    for file in Directory.GetFiles(Path.Combine(fixtures, "profile-server-app-host"), "*.json") do
        let rel = "profile-server-app-host/" + Path.GetFileName file

        if not (listed.Contains rel) then
            sprintf "%s is in the fixture folder and the manifest does not list it" rel

    let commit = p.Manifest |> prop "core" |> Option.bind (str "commit")

    match commit with
    | Some c when Regex.IsMatch(c, "^[0-9a-f]{40}$") -> ()
    | other -> sprintf "the core pin names no full commit (%A)" other

    // The profile version, in every rendering.
    let header = Regex.Match(p.Document, @"\*\*Profile version ([0-9.]+)\.\*\*")

    let table =
        Regex.Match(p.Document, @"\|\s*Profile version\s*\|\s*([0-9][^|]*?)\s*\|")

    let renderings = [
        "the document's header", (if header.Success then Some header.Groups[1].Value else None)
        "the document's §P1 table", (if table.Success then Some table.Groups[1].Value else None)
        "the registry", str "profileVersion" p.Registry
        "the fixture manifest", str "profileVersion" p.Manifest
        "the surface binding", str "profileVersion" p.Binding
    ]

    match renderings |> List.choose snd |> List.distinct with
    | [ _ ] when renderings |> List.forall (snd >> Option.isSome) -> ()
    | _ -> sprintf "the profile version disagrees across its renderings: %A" renderings
]

// ── 2. the document against the registry ─────────────────────────────

let private tokenSections = [
    "§P2.1 `appRoot.base`", "appRoot.base"
    "§P2.2 `appRoot.layers` element", "appRoot.layers"
    "§P2.3 `companionSelection.slot`", "companionSelection.slot"
    "§P2.4 `moduleRef.view`", "moduleRef.view"
    "§P2.6 `moduleRef.factTables` grain element", "moduleRef.factTables.grain"
    "§P2.7 `pageRef.audience`", "pageRef.audience"
    "§P2.9 `groundedNarrative.surface`", "groundedNarrative.surface"
    "§P2.10 `aiFunding.policy`", "aiFunding.policy"
    "§P2.11 `pageRef.captures` value", "pageRef.captures"
    "§P2.12 `referenceData.columns` type", "referenceData.columns.type"
    "§P2.13 `moduleRef.entities` field `level`", "moduleRef.entities.fields.level"
    "§P2.14 `moduleRef.aiExposure`", "moduleRef.aiExposure"
    "§P2.15 `dataDelivery.mode`", "dataDelivery.mode"
    "§P2.16 `moduleRef.disclosurePolicies` `mode`", "moduleRef.disclosurePolicies.mode"
    "§P2.17 `moduleRef.disclosurePolicies` `permitSurfaces` (element)", "moduleRef.disclosurePolicies.permitSurfaces"
    "§P2.18 `reportTemplate.format` and `standingSubscription.format`", "reportTemplate.format"
    "§P2.18 `reportTemplate.format` and `standingSubscription.format`", "standingSubscription.format"
    "§P2.19 `reportTemplate.placeholders` `kind`", "reportTemplate.placeholders.kind"
    "§P2.20 `reportTemplate.placeholders` `columns` `kind`", "reportTemplate.placeholders.columns.kind"
    "§P2.21 `factBrowse.mount`", "factBrowse.mount"
    "§P2.8.1 A column's `shape`", "moduleRef.factTables.definition.columns.shape"
    "§P2.8.2 A definition's `historyMode`", "moduleRef.factTables.definition.historyMode"
    "§P2.8.3 A disclosure's `class`", "moduleRef.factTables.definition.disclosure.class"
    "§P2.8.3 A disclosure's `class`", "moduleRef.entities.disclosure.class"
    "§P2.8.4 A definition's `requirement`", "moduleRef.factTables.definition.requirement"
    "§P2.8.5 A metric definition's `direction`", "moduleRef.metricDefinitions.direction"
    "§P2.8.6 A staleness object's `policy`", "moduleRef.metricDefinitions.staleness.policy"
    "§P2.8.7 A metric definition's `recomputePolicy`", "moduleRef.metricDefinitions.recomputePolicy"
    "§P2.8.8 A roll-up object's `kind`", "moduleRef.metricDefinitions.rollUp.kind"
]

let documentFindings (p: Profile) : string list = [
    let doc = p.Document

    // Every closed slot the registry declares has a section here, and the
    // other way round: a slot the list above forgot is a slot nobody checks.
    let registered =
        members (p.Registry |> prop "tokens")
        |> List.filter (fun (_, v) -> str "closure" v = Some "closed")
        |> List.map fst

    yield! both "closed slots" ("the registry", registered) ("this check", tokenSections |> List.map snd)

    for heading, slot in tokenSections do
        match sectionOf doc heading, tokensOf p slot with
        | None, _ -> sprintf "the document has no section %s, which the registry's %s renders" heading slot
        | _, None -> sprintf "the registry has no slot %s" slot
        | Some section, Some reg ->
            let inDoc = firstColumnTokens section
            let inReg = strings (prop "tokens" reg)

            if inDoc.IsEmpty then
                sprintf "%s: no token could be read out of %s, the table shape changed" slot heading

            if inReg.IsEmpty then
                sprintf "%s: the registry declares an empty closed vocabulary" slot

            yield! both slot ("the document", inDoc) ("the registry", inReg)

    // The cases that take an argument, marked REQUIRED in the document.
    for heading, slot in
        [
            "§P2.8.3 A disclosure's `class`", "moduleRef.factTables.definition.disclosure.class"
            "§P2.8.3 A disclosure's `class`", "moduleRef.entities.disclosure.class"
            "§P2.8.6 A staleness object's `policy`", "moduleRef.metricDefinitions.staleness.policy"
            "§P2.8.8 A roll-up object's `kind`", "moduleRef.metricDefinitions.rollUp.kind"
        ] do
        let section = sectionOf doc heading |> Option.defaultValue ""

        let inDoc = [
            for m in Regex.Matches(section, @"(?m)^\|\s*`([^`]+)`\s*\|[^|]*\|\s*REQUIRED\s*\|") -> m.Groups[1].Value
        ]

        if inDoc.IsEmpty then
            sprintf "%s: no argument-taking case could be read out of %s" slot heading

        yield!
            both
                (slot + " takes an argument")
                ("the document", inDoc)
                ("the registry", tokensOf p slot |> Option.bind (prop "takesArgument") |> strings)

    // Citation-bearing view tokens.
    let viewSection =
        sectionOf doc "### §P2.4 `moduleRef.view`" |> Option.defaultValue ""

    yield!
        both
            "citation-bearing"
            ("§P2.4",
             [
                 for m in Regex.Matches(viewSection, @"(?m)^\|\s*`([^`]+)`\s*\|\s*\*\*Yes\*\*\s*\|") ->
                     m.Groups[1].Value
             ])
            ("the registry", tokensOf p "moduleRef.view" |> Option.bind (prop "citationBearing") |> strings)

    // §P2.5 — the registered settings, their grammars and the grammars' definitions.
    let knob = tokensOf p "configKnob.key"
    let registeredKnobs = knob |> Option.bind (prop "registered") |> members
    let grammars = knob |> Option.bind (prop "valueGrammars")

    match sectionOf doc "### §P2.5 `configKnob.key` — **open**" with
    | None -> "the document has no §P2.5 section"
    | Some section ->
        let rows =
            Regex.Matches(section, @"(?m)^\|\s*`([^`]+)`\s*\|\s*`([^`]+)`\s*\|\s*(.+?)\s*\|\s*$")
            |> Seq.map (fun m -> m.Groups[1].Value, (m.Groups[2].Value, m.Groups[3].Value))
            |> Map.ofSeq

        if rows.IsEmpty then
            "configKnob.key: no registered key could be read out of §P2.5's table"

        yield! both "configKnob.key" ("§P2.5", rows.Keys) ("the registry", registeredKnobs |> List.map fst)

        for key, entry in registeredKnobs do
            match rows.TryFind key with
            | None -> ()
            | Some(grammar, cell) ->
                if str "grammar" entry <> Some grammar then
                    sprintf
                        "configKnob.key %s: §P2.5 holds it to %s, the registry to %A"
                        key
                        grammar
                        (str "grammar" entry)

                let cellValues = [ for m in Regex.Matches(cell, "`([^`]+)`") -> m.Groups[1].Value ]

                if grammar = "enum" then
                    yield!
                        both
                            ("configKnob.key " + key)
                            ("§P2.5", cellValues)
                            ("the registry", strings (prop "values" entry))
                elif not cellValues.IsEmpty then
                    sprintf "configKnob.key %s: §P2.5 lists values for a %s key, whose grammar defines them" key grammar

    let grammarHeadings = [
        for m in Regex.Matches(doc, @"(?m)^#### §P2\.5\.\d+ `([^`]+)`\s*$") -> m.Groups[1].Value
    ]

    yield! both "value grammars" ("§P2.5", grammarHeadings) ("the registry", members grammars |> List.map fst)

    let grammarSection name =
        let m = Regex.Match(doc, @"(?m)^#### §P2\.5\.\d+ `" + Regex.Escape name + "`\s*$")

        if m.Success then
            sectionOf doc m.Value |> Option.defaultValue ""
        else
            ""

    let retention = grammars |> Option.bind (prop "retention")

    let retentionRows = [
        for m in Regex.Matches(grammarSection "retention", @"(?m)^\|\s*`([^`]+)`\s*\|\s*([^|]*?)\s*\|") ->
            m.Groups[1].Value, m.Groups[2].Value
    ]

    if retentionRows.IsEmpty then
        "retention: no form could be read out of §P2.5.2"

    yield!
        both
            "retention literal forms"
            ("§P2.5.2",
             retentionRows
             |> List.filter (fun (f, _) -> not (f.Contains "<n>"))
             |> List.map fst)
            ("the registry", retention |> Option.bind (prop "literals") |> strings)

    yield!
        both
            "retention bounded forms"
            ("§P2.5.2",
             retentionRows
             |> List.filter (fun (f, _) -> f.Contains "<n>")
             |> List.map (fun (f, bound) -> sprintf "%s at most %s" f bound))
            ("the registry",
             retention
             |> Option.bind (prop "parameterised")
             |> Option.map (fun a -> [
                 for f in a.EnumerateArray() ->
                     sprintf
                         "%s<n>%s at most %d"
                         (str "prefix" f |> Option.defaultValue "")
                         (str "suffix" f |> Option.defaultValue "")
                         (f.GetProperty("max").GetInt64())
             ])
             |> Option.defaultValue [])

    yield!
        both
            "boolean literals"
            ("§P2.5.3", firstColumnTokens (grammarSection "boolean"))
            ("the registry",
             grammars
             |> Option.bind (prop "boolean")
             |> Option.bind (prop "literals")
             |> strings)

    let origin = grammars |> Option.bind (prop "origin")

    yield!
        both
            "origin schemes and default ports"
            ("§P2.5.4",
             [
                 for m in Regex.Matches(grammarSection "origin", @"(?m)^\|\s*`([^`]+)`\s*\|\s*([0-9]+)\s*\|") ->
                     m.Groups[1].Value + ":" + m.Groups[2].Value
             ])
            ("the registry",
             [
                 for s in origin |> Option.bind (prop "schemes") |> strings ->
                     let port =
                         origin
                         |> Option.bind (prop "defaultPorts")
                         |> Option.bind (prop s)
                         |> Option.map _.GetInt32()

                     sprintf "%s:%s" s (port |> Option.map string |> Option.defaultValue "?")
             ])

    // §P3 — the profile-only node kinds.
    let kinds = [
        for m in Regex.Matches(doc, @"(?m)^### §P3\.\d+ `([a-zA-Z]+)`") -> m.Groups[1].Value
    ]

    if kinds.IsEmpty then
        "no profile node kind could be read out of §P3"

    yield!
        both
            "profile-only node kinds"
            ("§P3", kinds)
            ("the registry", members (prop "nodeKinds" p.Registry) |> List.map fst)

    // Every refusal class the registry lists is stated in the document.
    for c in strings (prop "refusalClasses" p.Registry) do
        if not (doc.Contains("`" + c + "`")) then
            sprintf "the registry lists refusal class %s, which the document does not state" c

    // §P8 — the facet declaration, in the document, the registry and the manifest.
    let regFacets =
        members (prop "facets" p.Registry)
        |> List.map (fun (k, v) -> k, strings (Some v))

    let manifestFacets = members (prop "facetDeclaration" p.Manifest)

    match sectionOf doc "## §P8. Facet declaration" with
    | None -> "the document has no §P8 section"
    | Some section ->
        let rows = [
            for m in Regex.Matches(section, @"(?m)^\|\s*`([^`]+)`\s*\|\s*(.+?)\s*\|\s*$") ->
                m.Groups[1].Value, [ for v in Regex.Matches(m.Groups[2].Value, "`([^`]+)`") -> v.Groups[1].Value ]
        ]

        if rows.IsEmpty then
            "no honoured facet value could be read out of §P8"

        yield! both "facet axes" ("§P8", rows |> List.map fst) ("the registry", regFacets |> List.map fst)

        for axis, values in rows do
            match regFacets |> List.tryFind (fst >> (=) axis) with
            | Some(_, reg) -> yield! both ("facet axis " + axis) ("§P8", values) ("the registry", reg)
            | None -> ()

    for axis, entry in manifestFacets do
        let reg =
            regFacets
            |> List.tryFind (fst >> (=) axis)
            |> Option.map snd
            |> Option.defaultValue []

        yield!
            both ("facet axis " + axis) ("the fixture manifest", strings (prop "honoured" entry)) ("the registry", reg)

    for axis, reg in regFacets do
        if not (manifestFacets |> List.exists (fst >> (=) axis)) && not reg.IsEmpty then
            sprintf "facet axis %s: the registry honours values the fixture manifest declares nothing for" axis

    yield!
        both
            "facet families"
            ("the registry", strings (prop "facetFamilies" p.Registry))
            ("the fixture manifest", strings (prop "facetFamilies" p.Manifest))
]

// ── 3. the setting grammars against the vectors ──────────────────────

let private positiveAtMost (digits: string) (max: int64) =
    Regex.IsMatch(digits, "^[1-9][0-9]*$")
    && digits.Length <= (string max).Length
    && Decimal.Parse digits <= decimal max

/// Whether `value` is admissible on a registered key held to `entry`'s grammar.
let private admits (grammars: JsonElement option) (entry: JsonElement) (value: string) =
    let g name = grammars |> Option.bind (prop name)

    match str "grammar" entry with
    | Some "enum" -> strings (prop "values" entry) |> List.contains value
    | Some "retention" ->
        let r = g "retention"

        (r |> Option.bind (prop "literals") |> strings |> List.contains value)
        || (match r |> Option.bind (prop "parameterised") with
            | Some forms ->
                forms.EnumerateArray()
                |> Seq.exists (fun f ->
                    let prefix = str "prefix" f |> Option.defaultValue ""
                    let suffix = str "suffix" f |> Option.defaultValue ""

                    value.Length > prefix.Length + suffix.Length
                    && value.StartsWith(prefix, StringComparison.Ordinal)
                    && value.EndsWith(suffix, StringComparison.Ordinal)
                    && positiveAtMost
                        (value.Substring(prefix.Length, value.Length - prefix.Length - suffix.Length))
                        (f.GetProperty("max").GetInt64()))
            | None -> false)
    | Some "boolean" -> g "boolean" |> Option.bind (prop "literals") |> strings |> List.contains value
    | Some "origin" ->
        let o = g "origin"
        let m = Regex.Match(value, @"^([a-z]+)://([a-z0-9.-]+)(?::([0-9]+))?$")
        let schemes = o |> Option.bind (prop "schemes") |> strings

        if not m.Success || not (List.contains m.Groups[1].Value schemes) then
            false
        else
            let host = m.Groups[2].Value

            let labelOk (l: string) =
                Regex.IsMatch(l, "^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")

            let defaultPort =
                o
                |> Option.bind (prop "defaultPorts")
                |> Option.bind (prop m.Groups[1].Value)
                |> Option.map _.GetInt32()

            host.Length <= 253
            && host.Split('.') |> Array.forall labelOk
            && (not m.Groups[3].Success
                || (positiveAtMost m.Groups[3].Value 65535L
                    && Some(int m.Groups[3].Value) <> defaultPort))
    | _ -> false

/// Every registered setting in `doc` whose value its grammar does not admit, with the grammar.
let private inadmissibleSettings (p: Profile) (doc: JsonElement) : (string * string) list =
    let knob = tokensOf p "configKnob.key"
    let registered = knob |> Option.bind (prop "registered") |> members |> Map.ofList
    let grammars = knob |> Option.bind (prop "valueGrammars")
    let found = ResizeArray()

    let rec walk (n: JsonElement) =
        match n.ValueKind with
        | JsonValueKind.Array -> n.EnumerateArray() |> Seq.iter walk
        | JsonValueKind.Object ->
            match str "kind" n, str "key" n with
            | Some "configKnob", Some key when registered.ContainsKey key ->
                let entry = registered[key]

                match str "value" n with
                | Some v when admits grammars entry v -> ()
                | _ -> found.Add(key, str "grammar" entry |> Option.defaultValue "?")
            | _ -> ()

            for p in n.EnumerateObject() do
                walk p.Value
        | _ -> ()

    walk doc
    List.ofSeq found

let grammarFindings (p: Profile) : string list = [
    let grammars =
        tokensOf p "configKnob.key"
        |> Option.bind (prop "valueGrammars")
        |> members
        |> List.map fst

    let mutable rejects = 0
    let refusedUnder = Collections.Generic.HashSet<string>()

    for e in vectorEntries p do
        let id = str "id" e |> Option.defaultValue "?"
        let kind = str "kind" e
        let mustAdmit = kind = Some "round-trip" || kind = Some "accept"

        let mustRefuse =
            kind = Some "reject" && str "reject" e = Some "knob-value-unregistered"

        if mustAdmit || mustRefuse then
            let doc =
                readJson (Path.Combine(p.Dir, "fixtures", str "file" e |> Option.defaultValue ""))

            let bad = inadmissibleSettings p doc

            if mustAdmit && not bad.IsEmpty then
                sprintf "%s: a vector that must be accepted carries %A, which §P2.5's grammars do not admit" id bad

            if mustRefuse then
                rejects <- rejects + 1

                for _, g in bad do
                    refusedUnder.Add g |> ignore

                if bad.IsEmpty then
                    sprintf
                        "%s: refused as knob-value-unregistered, and every registered setting in it is admissible"
                        id

    if rejects = 0 then
        "no knob-value-unregistered vector was found, so the grammars went untested"

    for g in grammars do
        if not (refusedUnder.Contains g) then
            sprintf "no knob-value-unregistered vector refuses a value under the %s grammar" g
]

// ── 4. §P8 certified against the core's facet-family vectors ─────────

let facetFindings (p: Profile) : string list = [
    let core = prop "core" p.Manifest

    let axes =
        core
        |> Option.bind (prop "facetAxes")
        |> members
        |> List.map (fun (k, v) -> k, strings (Some v))

    let withoutDiscriminator =
        core
        |> Option.bind (prop "facetValuesWithoutDiscriminator")
        |> Option.map (fun a -> [
            for e in a.EnumerateArray() ->
                sprintf "%s/%s" (e.GetProperty("axis").GetString()) (e.GetProperty("value").GetString())
        ])
        |> Option.defaultValue []

    let declaration = members (prop "facetDeclaration" p.Manifest)
    let declaredFamilies = strings (prop "facetFamilies" p.Manifest)

    if axes.IsEmpty then
        "the core pin carries no facet axes"

    // §11.4 — every axis is declared, with its note; every value is in its axis's vocabulary.
    for axis, vocabulary in axes do
        match declaration |> List.tryFind (fst >> (=) axis) with
        | None -> sprintf "the declaration says nothing on axis %s; §11.4 requires every axis" axis
        | Some(_, entry) ->
            if (str "note" entry |> Option.defaultValue "").Trim() = "" then
                sprintf "axis %s: the declaration carries no note" axis

            for v in strings (prop "honoured" entry) do
                if not (List.contains v vocabulary) then
                    sprintf "axis %s honours %s, which is not in the axis's vocabulary" axis v

                if List.contains (axis + "/" + v) withoutDiscriminator then
                    sprintf "axis %s honours %s, for which the core states no discriminator" axis v

    for axis, _ in declaration do
        if not (axes |> List.exists (fst >> (=) axis)) then
            sprintf "the declaration names axis %s, which the core does not register" axis

    // The pinned facet-family vectors for this profile and what each demonstrates.
    let facetVectors =
        coreEntries p
        |> List.filter (fun e -> str "profile" e = Some "server-app-host")
        |> List.map (fun e ->
            let doc =
                readJson (Path.Combine(p.Dir, "fixtures", str "file" e |> Option.defaultValue ""))

            let demonstrates =
                doc
                |> prop "demonstrates"
                |> Option.map (fun a -> [
                    for d in a.EnumerateArray() ->
                        d.GetProperty("axis").GetString(), d.GetProperty("value").GetString()
                ])
                |> Option.defaultValue []

            str "id" e |> Option.defaultValue "?", str "family" e |> Option.defaultValue "?", demonstrates)

    if facetVectors.IsEmpty then
        "no core facet-family vector for this profile is pinned"

    for id, family, _ in facetVectors do
        if not (List.contains family declaredFamilies) then
            sprintf "%s: its family %s is not one this profile is in scope of" id family

    for family in declaredFamilies do
        if not (facetVectors |> List.exists (fun (_, f, _) -> f = family)) then
            sprintf "facet family %s: no pinned core vector for this profile" family

    // §11.3, both directions: every honoured value is demonstrated, and
    // every demonstration is of a value the declaration honours.
    let honoured =
        declaration
        |> List.collect (fun (axis, entry) -> strings (prop "honoured" entry) |> List.map (fun v -> axis, v))

    for axis, value in honoured do
        if not (facetVectors |> List.exists (fun (_, _, ds) -> List.contains (axis, value) ds)) then
            sprintf
                "facet-undemonstrated: %s=%s is declared and no pinned core vector demonstrates it (§11.3)"
                axis
                value

    for id, _, ds in facetVectors do
        for axis, value in ds do
            if not (List.contains (axis, value) honoured) then
                sprintf "%s demonstrates %s=%s, which the declaration does not honour" id axis value
]

// ── 5. base-token ownership ───────────────────────────────────────────

let baseFindings (p: Profile) : string list = [
    let bases = tokensOf p "appRoot.base" |> Option.bind (prop "tokens") |> strings

    if bases.IsEmpty then
        "the registry registers no appRoot.base token, so no root belongs to this profile"

    for b in bases |> List.countBy id |> List.filter (snd >> (<) 1) |> List.map fst do
        sprintf "appRoot.base token %s is registered twice" b

    let unresolved =
        coreEntries p
        |> List.filter (fun e -> str "reject" e = Some "vocabulary-unresolved")

    if unresolved.IsEmpty then
        "no vocabulary-unresolved core vector is pinned, so base ownership went untested"

    for e in unresolved do
        let doc =
            readJson (Path.Combine(p.Dir, "fixtures", str "file" e |> Option.defaultValue ""))

        match str "base" doc with
        | Some b when List.contains b bases ->
            sprintf
                "%s: its root's base %s is registered here, so the vocabulary resolves and the vector falsifies nothing"
                (str "id" e |> Option.defaultValue "?")
                b
        | _ -> ()
]

// ── 6. the registry against forge's composable surface ───────────────

/// What forge reflects, separated from the reflection so the self-test can plant a change.
type Surface = {
    Slots: string list
    Knobs: (string * string list) list
    ConfigFields: (string * Type) list
}

let reflectedSurface () : Surface = {
    Slots = ComposableSurface.slots () |> List.map _.Interface
    Knobs = ComposableSurface.configKnobs () |> List.map (fun k -> k.Name, k.Values)
    ConfigFields =
        FSharpType.GetRecordFields(typeof<ServerConfig>)
        |> Array.map (fun f -> f.Name, f.PropertyType)
        |> List.ofArray
}

let private clrName (t: Type) = t.FullName.Replace('+', '.')

let private resolveType (assembly: string) (typeName: string) : Result<Type, string> =
    try
        let asm = Assembly.Load(AssemblyName assembly)

        match
            asm.GetTypes()
            |> Array.tryFind (fun t -> clrName t = typeName || clrName t = typeName + "Module")
        with
        | Some t -> Ok t
        | None -> Error(sprintf "%s has no type %s" assembly typeName)
    with ex ->
        Error(sprintf "assembly %s does not load: %s" assembly ex.Message)

let private resolveTarget (what: string) (e: JsonElement) = [
    match str "assembly" e, str "type" e with
    | Some asm, Some typeName ->
        match resolveType asm typeName with
        | Error msg -> sprintf "%s: %s" what msg
        | Ok t ->
            match str "member" e with
            | Some m when
                t.GetMembers(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.Instance)
                |> Array.exists (fun x -> x.Name = m)
                |> not
                ->
                sprintf "%s: %s has no public member %s" what typeName m
            | _ -> ()
    | _ -> sprintf "%s: the binding names no assembly or type" what
]

let private companionInterfaces =
    lazy
        ([ typeof<ServerConfig>.Assembly; typeof<ServerApp>.Assembly ]
         |> List.collect (fun a -> a.GetTypes() |> List.ofArray)
         |> List.filter (fun t -> t.IsInterface && t.IsPublic || t.IsInterface && t.IsNestedPublic)
         |> List.map _.Name
         |> Set.ofList)

let surfaceFindings (p: Profile) (surface: Surface) : string list = [
    let b = p.Binding

    let registryTokens slot =
        tokensOf p slot |> Option.bind (prop "tokens") |> strings

    // Bases and layers: the registry's tokens are exactly the binding's, and each reduces to a forge type.
    for slot, key in [ "appRoot.base", "bases"; "appRoot.layers", "layers" ] do
        let bound = members (prop key b)
        yield! both slot ("the registry", registryTokens slot) ("the surface binding", bound |> List.map fst)

        for token, target in bound do
            yield! resolveTarget (sprintf "%s %s" slot token) target

    // Slots, registry -> forge: each registered seam names a forge companion interface.
    let slots = members (prop "slots" b) |> List.map (fun (k, v) -> k, v.GetString())

    yield!
        both
            "companionSelection.slot"
            ("the registry", registryTokens "companionSelection.slot")
            ("the surface binding", slots |> List.map fst)

    for token, iface in slots do
        if not (companionInterfaces.Value.Contains iface) then
            sprintf "companionSelection.slot %s binds %s, which is not a forge companion interface" token iface

    // Slots, forge -> registry: every reflected slot is registered or named as unregistered.
    let unregisteredSlots = members (prop "unregisteredSlots" b)

    for iface, reason in unregisteredSlots do
        if slots |> List.exists (snd >> (=) iface) then
            sprintf "%s is both bound to a registered token and named unregistered" iface

        if (reason.GetString() |> Option.ofObj |> Option.defaultValue "").Trim() = "" then
            sprintf "%s is named unregistered with no reason" iface

    yield!
        both
            "composable slots"
            ("forge's reflected surface", surface.Slots)
            ("the profile (registered or named unregistered)",
             (slots |> List.map snd |> List.filter (fun i -> List.contains i surface.Slots))
             @ (unregisteredSlots |> List.map fst))

    // Settings, registry -> forge.
    let settings = members (prop "settings" b)

    let registeredKnobs =
        tokensOf p "configKnob.key" |> Option.bind (prop "registered") |> members

    yield!
        both
            "configKnob.key"
            ("the registry", registeredKnobs |> List.map fst)
            ("the surface binding", settings |> List.map fst)

    let fieldType name =
        surface.ConfigFields |> List.tryFind (fst >> (=) name) |> Option.map snd

    let caseKnobs = ResizeArray<string>()

    for key, s in settings do
        let registeredValues =
            registeredKnobs
            |> List.tryFind (fst >> (=) key)
            |> Option.map (snd >> prop "values" >> strings)
            |> Option.defaultValue []

        match str "kind" s with
        | Some "cases" ->
            let field = str "field" s |> Option.defaultValue key

            match fieldType field with
            | None -> sprintf "setting %s: ServerConfig has no field %s" key field
            | Some t when not (FSharpType.IsUnion t) -> sprintf "setting %s: ServerConfig.%s is not a union" key field
            | Some t ->
                caseKnobs.Add field
                let cases = FSharpType.GetUnionCases t |> Array.map _.Name |> List.ofArray
                let unregisteredValues = members (prop "unregisteredValues" s) |> List.map fst

                for v in registeredValues do
                    if not (List.contains v cases) then
                        sprintf "setting %s: the profile registers %s, which ServerConfig.%s does not have" key v field

                yield!
                    both
                        ("setting " + key + " values")
                        ("ServerConfig." + field, cases)
                        ("the profile (registered or named unregistered)", registeredValues @ unregisteredValues)

                match surface.Knobs |> List.tryFind (fst >> (=) field) with
                | Some(_, reflected) when reflected <> cases ->
                    sprintf
                        "setting %s: the composable surface reflects %A, the field's cases are %A"
                        key
                        reflected
                        cases
                | _ -> ()
        | Some "spelled" ->
            let field = str "field" s |> Option.defaultValue key

            if (fieldType field).IsNone then
                sprintf "setting %s: ServerConfig has no field %s" key field
        | Some "builder" -> yield! resolveTarget ("setting " + key) s
        | other -> sprintf "setting %s: the binding's kind %A is not cases, spelled or builder" key other

        if
            str "kind" s <> Some "cases"
            && (str "reason" s |> Option.defaultValue "").Trim() = ""
        then
            sprintf "setting %s: a %A binding states no reason" key (str "kind" s)

    // Settings, forge -> registry: every reflected setting is registered or named unregistered.
    let unregisteredKnobs =
        prop "unregisteredSettings" b |> Option.bind (prop "keys") |> strings

    for k in unregisteredKnobs do
        if caseKnobs.Contains k then
            sprintf "setting %s is both registered and named unregistered" k

    yield!
        both
            "composable settings"
            ("forge's reflected surface", surface.Knobs |> List.map fst)
            ("the profile (registered or named unregistered)",
             (List.ofSeq caseKnobs
              |> List.filter (fun k -> surface.Knobs |> List.exists (fst >> (=) k)))
             @ unregisteredKnobs)
]

// ── the suite ─────────────────────────────────────────────────────────

let private expectNone (findings: string list) =
    if not findings.IsEmpty then
        failtestf "%d disagreement(s):\n  %s" findings.Length (String.concat "\n  " findings)

let tests =
    testList "Composition profile — spec/ against its renderings and forge's surface" [
        test "the fixtures hold their recorded digests and the profile version agrees everywhere" {
            expectNone (fixtureFindings (load ()))
        }

        test "the document's tables and the registry say the same things" { expectNone (documentFindings (load ())) }

        test "the setting grammars admit every accepted vector and refuse every knob-value-unregistered one" {
            expectNone (grammarFindings (load ()))
        }

        test "§P8's facet declaration is certified against the core's facet-family vectors" {
            expectNone (facetFindings (load ()))
        }

        test "the registry's root bases are its own, and no vocabulary-unresolved vector names one" {
            expectNone (baseFindings (load ()))
        }

        test "the registry and forge's composable surface agree, through the surface binding" {
            expectNone (surfaceFindings (load ()) (reflectedSurface ()))
        }

        test "each check goes red on a planted mismatch" {
            let p = load ()
            let surface = reflectedSurface ()

            let withBinding (edit: string -> string) =
                let path = Path.Combine(p.Dir, "surface-binding.json")

                {
                    p with
                        Binding = JsonDocument.Parse(edit (File.ReadAllText path)).RootElement
                }

            let planted = [
                // forge grows a companion slot nobody registered or declined.
                "a new reflected slot",
                surfaceFindings p {
                    surface with
                        Slots = "IPlantedSeam" :: surface.Slots
                }
                // forge grows a setting.
                "a new reflected setting",
                surfaceFindings p {
                    surface with
                        Knobs = ("PlantedMode", [ "Off"; "On" ]) :: surface.Knobs
                }
                // a registered slot binds an interface forge does not have.
                "a slot bound to nothing",
                surfaceFindings (withBinding (fun t -> t.Replace("\"IVectorStore\"", "\"IPlantedStore\""))) surface
                // a registered layer reduces to no type.
                "a layer reducing to no type",
                surfaceFindings
                    (withBinding (fun t ->
                        t.Replace("ToolUp.RAG.RAGCompose.RAGServerApp", "ToolUp.RAG.RAGCompose.Planted")))
                    surface
                // the document drops a registered token.
                "a document missing a token",
                documentFindings {
                    p with
                        Document = p.Document.Replace("| `forms` |", "| forms |")
                }
                // §P8 honours a value no core vector demonstrates.
                "an undemonstrated facet value",
                facetFindings {
                    p with
                        Manifest =
                            JsonDocument
                                .Parse(
                                    File
                                        .ReadAllText(Path.Combine(p.Dir, "fixtures", "manifest.json"))
                                        .Replace("\r\n", "\n")
                                        .Replace(
                                            "\"atMostOnce\",\n        \"atLeastOnce\"\n",
                                            "\"atMostOnce\",\n        \"atLeastOnce\",\n        \"exactlyOnceEffective\"\n"
                                        )
                                )
                                .RootElement
                }
            ]

            for what, findings in planted do
                Expect.isNonEmpty findings (sprintf "planting %s must be reported" what)
        }
    ]