// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 799 — the JSON wire joins the decoder algebra.
///
/// The Phase 785 pack, over the other wire: the SAME corpus (`WireCorpus`,
/// Phase 784), the STJ converter set as the oracle where the reflection
/// reader was, and the same discipline — every arm is DIFFERENTIAL on
/// the accept path and DECLARED on the refuse path, so the two decoders
/// are held to one contract and a difference is visible as exactly that.
///
/// The decoders below are hand-written in the shape the generator emits
/// for the MsgPack wire, transposed to the JSON one: `field` by NAME
/// rather than position, `union` on the case name rather than the tag,
/// `asMap` with a key parser because a non-string key arrives as text.
/// They are NOT registered in the process-wide `JsonDecoders` table —
/// `WireCorpus.classifyJson` measures the STJ path through the seam the
/// registry sits in front of, and a corpus decoder registered there
/// would turn that measurement into a measurement of this file.
module ToolUp.Platform.Tests.Remoting.JsonDecoderAlgebraTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ToolUp.Remoting
open ToolUp.Remoting.Json
open ToolUp.Platform.Tests.Remoting.WireCorpus

/// Phase 839 — a minimal test-local "served API record" with one bare,
/// deliberately unregistered `string` argument. Used by the served
/// argument facet tests below to demonstrate genuine partial coverage
/// without depending on a REAL platform API record staying uncovered —
/// `PlatformJsonDecoders` covers all six of ITS records fully as of this
/// phase, so a real API is the wrong fixture for "still uncovered".
type private ProbeApi = { DoThing: string -> Async<unit> }

// ─── The corpus types' decoders ──────────────────────────────────────

let private priority: JsonDecoder<Priority> =
    JsonDecode.union "Priority" (function
        | "Low" -> Some(JsonDecode.case0 Low)
        | "Normal" -> Some(JsonDecode.case0 Normal)
        | "High" -> Some(JsonDecode.case0 High)
        | _ -> None)

let private address: JsonDecoder<Address> =
    JsonDecode.succeed (fun line1 postcode country -> {
        Line1 = line1
        Postcode = postcode
        Country = country
    })
    |> JsonDecode.apply (JsonDecode.field "Line1" JsonDecode.asString)
    |> JsonDecode.apply (JsonDecode.field "Postcode" JsonDecode.asString)
    |> JsonDecode.apply (JsonDecode.field "Country" JsonDecode.asString)

/// Zero, one and several fields — the three shapes the writer emits:
/// `"Pending"`, `{"Rejected": "…"}`, `{"Accepted": [guid, at]}`.
let private outcome: JsonDecoder<Outcome> =
    JsonDecode.union "Outcome" (function
        | "Accepted" ->
            Some(
                JsonDecode.fields
                    2
                    (JsonDecode.succeed (fun id at -> Outcome.Accepted(id, at))
                     |> JsonDecode.apply (JsonDecode.index 0 JsonDecode.asGuid)
                     |> JsonDecode.apply (JsonDecode.index 1 JsonDecode.asDateTimeOffset))
            )
        | "Rejected" -> Some(JsonDecode.payload (JsonDecode.asString |> JsonDecode.map Outcome.Rejected))
        | "Pending" -> Some(JsonDecode.case0 Outcome.Pending)
        | _ -> None)

let private customer: JsonDecoder<Customer> =
    JsonDecode.succeed (fun id name addr since balance tags -> {
        Id = id
        Name = name
        Address = addr
        Since = since
        Balance = balance
        Tags = tags
    })
    |> JsonDecode.apply (JsonDecode.field "Id" JsonDecode.asGuid)
    |> JsonDecode.apply (JsonDecode.field "Name" JsonDecode.asString)
    |> JsonDecode.apply (JsonDecode.field "Address" address)
    |> JsonDecode.apply (JsonDecode.field "Since" JsonDecode.asDateOnly)
    |> JsonDecode.apply (JsonDecode.field "Balance" JsonDecode.asDecimal)
    |> JsonDecode.apply (JsonDecode.field "Tags" (JsonDecode.list JsonDecode.asString))

let private consignment: JsonDecoder<Consignment> =
    JsonDecode.succeed (fun reference origin destination pri outc weights labels -> {
        Reference = reference
        Origin = origin
        Destination = destination
        Priority = pri
        Outcome = outc
        Weights = weights
        Labels = labels
    })
    |> JsonDecode.apply (JsonDecode.field "Reference" JsonDecode.asString)
    |> JsonDecode.apply (JsonDecode.field "Origin" address)
    |> JsonDecode.apply (JsonDecode.field "Destination" address)
    |> JsonDecode.apply (JsonDecode.field "Priority" priority)
    |> JsonDecode.apply (JsonDecode.field "Outcome" outcome)
    |> JsonDecode.apply (JsonDecode.field "Weights" (JsonDecode.list JsonDecode.asFloat))
    |> JsonDecode.apply (JsonDecode.field "Labels" (JsonDecode.asSet JsonDecode.asString))

let private envelope: JsonDecoder<ApiEnvelope> =
    JsonDecode.succeed (fun cust pri outc attempts window notes scores flags payload -> {
        Customer = cust
        Priority = pri
        Outcome = outc
        Attempts = attempts
        Window = window
        Notes = notes
        Scores = scores
        Flags = flags
        Payload = payload
    })
    |> JsonDecode.apply (JsonDecode.field "Customer" customer)
    |> JsonDecode.apply (JsonDecode.field "Priority" priority)
    |> JsonDecode.apply (JsonDecode.field "Outcome" outcome)
    |> JsonDecode.apply (JsonDecode.field "Attempts" JsonDecode.asInt32)
    |> JsonDecode.apply (JsonDecode.field "Window" JsonDecode.asTimeSpan)
    |> JsonDecode.apply (JsonDecode.optionalField "Notes" JsonDecode.asString)
    |> JsonDecode.apply (JsonDecode.field "Scores" (JsonDecode.asMap JsonDecode.Key.string JsonDecode.asFloat))
    |> JsonDecode.apply (JsonDecode.field "Flags" (JsonDecode.asSet JsonDecode.asString))
    |> JsonDecode.apply (JsonDecode.field "Payload" JsonDecode.asBytes)

/// Phase 827 — the two-case union a `TemplatedMessage` carries a list
/// of; each case one string, written `{"PlatformMember": "…"}`.
let private addressee: JsonDecoder<Addressee> =
    JsonDecode.union "Addressee" (function
        | "PlatformMember" -> Some(JsonDecode.payload (JsonDecode.asString |> JsonDecode.map PlatformMember))
        | "ExternalAddressee" -> Some(JsonDecode.payload (JsonDecode.asString |> JsonDecode.map ExternalAddressee))
        | _ -> None)

/// Phase 827 — the corpus's local mirror of the WhatsApp envelope.
let private templatedMessage: JsonDecoder<TemplatedMessage> =
    JsonDecode.succeed (fun recipients templateName templateLanguage parameters body metadata correlationId -> {
        Recipients = recipients
        TemplateName = templateName
        TemplateLanguage = templateLanguage
        TemplateParameters = parameters
        Body = body
        Metadata = metadata
        CorrelationId = correlationId
    })
    |> JsonDecode.apply (JsonDecode.field "Recipients" (JsonDecode.list addressee))
    |> JsonDecode.apply (JsonDecode.optionalField "TemplateName" JsonDecode.asString)
    |> JsonDecode.apply (JsonDecode.optionalField "TemplateLanguage" JsonDecode.asString)
    |> JsonDecode.apply (JsonDecode.field "TemplateParameters" (JsonDecode.list JsonDecode.asString))
    |> JsonDecode.apply (JsonDecode.optionalField "Body" JsonDecode.asString)
    |> JsonDecode.apply (JsonDecode.field "Metadata" (JsonDecode.asMap JsonDecode.Key.string JsonDecode.asString))
    |> JsonDecode.apply (JsonDecode.optionalField "CorrelationId" JsonDecode.asString)

/// The recursive union, eta-expanded as the generator emits it.
let rec private tree: JsonDecoder<Tree> =
    fun value ->
        (JsonDecode.union "Tree" (function
            | "Leaf" -> Some(JsonDecode.payload (JsonDecode.asString |> JsonDecode.map Leaf))
            | "Branch" ->
                Some(
                    JsonDecode.fields
                        2
                        (JsonDecode.succeed (fun label children -> Branch(label, children))
                         |> JsonDecode.apply (JsonDecode.index 0 JsonDecode.asString)
                         |> JsonDecode.apply (JsonDecode.index 1 (JsonDecode.list tree)))
                )
            | _ -> None))
            value

/// The go-red decoder: reads at `float` and casts to `int`, which is what
/// a `JNumber of float` carrier would have forced. Registered for
/// nothing.
let private throughFloat: JsonDecoder<int64> =
    JsonDecode.asFloat |> JsonDecode.map int64

/// A union-typed map KEY (Phase 6f.A, corpus case `map-union-key`). The
/// writer emits a non-string key as its own JSON text used as the member
/// name, so a payload-bearing case arrives as `{"Rejected":"no opt-in"}`
/// in the property-name position. The key is therefore parsed as JSON and
/// read with the same `outcome` decoder a value would be — one reading
/// of the union, whichever position it occupies on the wire.
let private outcomeKey: JsonDecode.KeyDecoder<Outcome> =
    fun name -> JsonRead.tryParse name |> Result.bind outcome

// ─── The covered set ─────────────────────────────────────────────────

let private erase (decoder: JsonDecoder<'T>) : RegisteredJsonDecoder =
    fun value -> decoder value |> Result.map box

let private entry<'T> (decoder: JsonDecoder<'T>) : Type * RegisteredJsonDecoder = typeof<'T>, erase decoder

/// Every corpus type the JSON algebra covers. `DateOnly` / `TimeOnly`
/// ARE here, unlike the MsgPack pack: this wire's decode seam is .NET
/// only, so the cross-host argument that kept them out of that file does
/// not apply.
let private covered: (Type * RegisteredJsonDecoder) list = [
    entry JsonDecode.asBool
    entry JsonDecode.asInt32
    entry JsonDecode.asString
    entry JsonDecode.asChar
    entry JsonDecode.asByte
    entry JsonDecode.asSByte
    entry JsonDecode.asInt16
    entry JsonDecode.asUInt16
    entry JsonDecode.asUInt32
    entry JsonDecode.asInt64
    entry JsonDecode.asUInt64
    entry JsonDecode.asFloat
    entry JsonDecode.asFloat32
    entry JsonDecode.asDecimal
    entry JsonDecode.asDateTime
    entry JsonDecode.asDateTimeOffset
    entry JsonDecode.asTimeSpan
    entry JsonDecode.asDateOnly
    entry JsonDecode.asTimeOnly
    entry JsonDecode.asGuid
    entry JsonDecode.asBytes
    entry (JsonDecode.option JsonDecode.asInt32)
    entry (JsonDecode.option JsonDecode.asString)
    entry (JsonDecode.option address)
    entry (JsonDecode.list JsonDecode.asInt32)
    entry (JsonDecode.list address)
    entry (JsonDecode.array JsonDecode.asString)
    entry (JsonDecode.asMap JsonDecode.Key.string JsonDecode.asInt32)
    entry (JsonDecode.asMap JsonDecode.Key.int32 JsonDecode.asString)
    entry (JsonDecode.asMap outcomeKey address)
    entry (JsonDecode.asSet JsonDecode.asString)
    entry (JsonDecode.asSet JsonDecode.asInt32)
    entry (JsonDecode.tuple2 JsonDecode.asInt32 JsonDecode.asString)
    entry (JsonDecode.tuple3 JsonDecode.asInt32 JsonDecode.asString JsonDecode.asBool)
    entry priority
    entry outcome
    entry address
    entry customer
    entry consignment
    entry envelope
    entry tree
    entry templatedMessage
]

let private tryAlgebra (target: Type) =
    covered
    |> List.tryPick (fun (t, decoder) -> if t = target then Some decoder else None)

let private coveredCases =
    pinnedCases |> List.filter (fun c -> (tryAlgebra c.ClrType).IsSome)

// ─── Running the two paths ───────────────────────────────────────────

/// The algebra path, end to end: STJ's parse, one element-to-value pass,
/// then the decoder. Never throws.
let private algebraDecode (target: Type) (text: string) : Result<obj, DecodeError> =
    match tryAlgebra target with
    | None -> Error(DecodeError.create "an algebra decoder" (sprintf "no decoder for %s" target.FullName))
    | Some decoder -> JsonRead.tryParse text |> Result.bind decoder

let private classifyAlgebra (target: Type) (text: string) : RefusalOutcome * string =
    try
        match algebraDecode target text with
        | Ok value -> Accepted(sprintf "%A" value), "Ok"
        | Error e -> Refused, DecodeError.render e
    with ex ->
        ThrewUnnamed(ex.GetType().Name), ex.Message

// ─── The refuse-path declaration ─────────────────────────────────────

/// What the ALGEBRA does with each Phase 784 mutation that carries a
/// JSON payload, measured 2026-09-22, beside what the STJ path does.
let private algebraOutcomes: (string * RefusalOutcome) list = [
    // `null` for a record: STJ hands back a null reference (Accepted);
    // the algebra has no null and `field` refuses.
    "wrong-tag-nil-for-record", Refused
    "wrong-tag-string-for-list", Refused
    "wrong-tag-bool-for-string", Refused
    // Malformed text: STJ's parse throws before the seam (ThrewUnnamed
    // on that path); `JsonRead.tryParse` names it.
    "truncated-record-body", Refused
    "truncated-empty-payload", Refused
    // An int64 written as the wire's STRING form, read at int32: STJ
    // refuses; so does the algebra — `asInt32` takes no string.
    "wrong-width-int64-into-int32", Refused
    // A quoted `"7"` at int: STJ ACCEPTS (`AllowReadingFromString`); the
    // algebra refuses — a string is not a number on this wire, and the
    // one type the writer quotes (`int64`) has its own arm.
    "wrong-width-string-into-int", Refused
    // A record missing a declared field: STJ reads it back as `null`
    // (Accepted, the additive read path); the algebra refuses by name.
    // The two are different contracts and the doc page says which
    // applies where — a decoder registered for a record is a statement
    // that its fields are required.
    "missing-field-record", Refused
    // A surplus member is ignored on BOTH paths — the one evolution
    // property this wire has, kept deliberately.
    "extra-field-record", Accepted "an unmatched member is ignored, so an additive wire change stays non-breaking"
]

// ─── Generated JSON mutations, per shape ─────────────────────────────

/// A mutation derived from a pinned case's own JSON text, by class of
/// damage. Each carries what the algebra MUST do with it.
type private ShapeMutation = {
    Name: string
    Kind: MutationKind
    Target: Type
    Text: string
    Expected: RefusalOutcome
}

let private wrongKind (text: string) =
    let trimmed = text.TrimStart()

    if trimmed.StartsWith "{" then "[]"
    elif trimmed.StartsWith "[" then "{}"
    else "{\"unexpected\":1}"

/// The mutations the generator derives for a case: a value of the wrong
/// kind at the root (every case), a truncated container or string
/// (containers and strings), a non-integral token where an integer was
/// declared (the width classes), and a missing / surplus member (the
/// record classes). Each is a text the STJ path is also shown, so the
/// differential arm can report where the two disagree.
let private shapeMutations (c: WireCase) : ShapeMutation list =
    let text = c.WriteJson()
    let trimmed = text.TrimStart()
    let isContainer = trimmed.StartsWith "{" || trimmed.StartsWith "["
    let isString = trimmed.StartsWith "\""
    let isNumber = trimmed.Length > 0 && (Char.IsDigit trimmed.[0] || trimmed.[0] = '-')

    [
        {
            Name = c.Name + "/wrong-kind"
            Kind = MutationKind.WrongTag
            Target = c.ClrType
            Text = wrongKind text
            Expected = Refused
        }

        if (isContainer || isString) && text.Length >= 4 then
            {
                Name = c.Name + "/truncated"
                Kind = MutationKind.Truncated
                Target = c.ClrType
                Text = text.Substring(0, text.Length / 2)
                Expected = Refused
            }

        if c.Class = WireClass.NumericWidth && isNumber then
            {
                Name = c.Name + "/fractional"
                Kind = MutationKind.WrongWidth
                Target = c.ClrType
                Text = "1.5"
                Expected = Refused
            }

        if c.Class = WireClass.NumericWidth && isString then
            {
                Name = c.Name + "/fractional-string"
                Kind = MutationKind.WrongWidth
                Target = c.ClrType
                Text = "\"1.5\""
                Expected = Refused
            }

        if c.Class = WireClass.Record || c.Class = WireClass.NestedRecord then
            let node = JsonNode.Parse(text).AsObject()
            let first = node |> Seq.head
            node.Remove first.Key |> ignore

            {
                Name = c.Name + "/missing-field"
                Kind = MutationKind.MissingField
                Target = c.ClrType
                Text = node.ToJsonString()
                Expected = Refused
            }

            let surplus = JsonNode.Parse(text).AsObject()
            surplus.Add("Surplus", System.Text.Json.Nodes.JsonValue.Create "x")

            {
                Name = c.Name + "/extra-field"
                Kind = MutationKind.ExtraField
                Target = c.ClrType
                Text = surplus.ToJsonString()
                Expected = Accepted "a surplus member is ignored"
            }
    ]

let private generatedMutations () =
    coveredCases |> List.collect shapeMutations

// ─── The IL pin ──────────────────────────────────────────────────────

let private decodeModuleTypes () =
    let assembly = typeof<DecodeError>.Assembly
    let root = "ToolUp.Remoting.Json.JsonDecode"

    assembly.GetTypes()
    |> Array.filter (fun t ->
        let name = t.FullName

        not (isNull name)
        && (name = root || name.StartsWith(root + "+", StringComparison.Ordinal)))

let private calledMembers (m: Reflection.MethodBase) : string list =
    match m.GetMethodBody() with
    | null -> []
    | body ->
        let il = body.GetILAsByteArray()
        let module' = m.Module
        let found = ResizeArray()
        let mutable i = 0

        while i < il.Length do
            let op = il.[i]

            // `call` 0x28, `callvirt` 0x6F, `newobj` 0x73 — each followed
            // by a 4-byte metadata token.
            if (op = 0x28uy || op = 0x6Fuy || op = 0x73uy) && i + 4 < il.Length then
                let token = BitConverter.ToInt32(il, i + 1)

                try
                    let target = module'.ResolveMethod token

                    if not (isNull target) && not (isNull target.DeclaringType) then
                        found.Add(target.DeclaringType.FullName + "::" + target.Name)
                with _ ->
                    ()

                i <- i + 5
            else
                i <- i + 1

        List.ofSeq found

let private forbiddenCall (called: string) =
    called.StartsWith "Microsoft.FSharp.Reflection."
    || called.StartsWith "System.Reflection."
    || called.Contains "::GetType"
    || called.EndsWith "::FailWith"
    || called.EndsWith "::Raise"
    || called.EndsWith "::Throw"

let private allMethods (t: Type) =
    let flags =
        Reflection.BindingFlags.Public
        ||| Reflection.BindingFlags.NonPublic
        ||| Reflection.BindingFlags.Static
        ||| Reflection.BindingFlags.Instance
        ||| Reflection.BindingFlags.DeclaredOnly

    [
        yield! t.GetMethods flags |> Seq.cast<Reflection.MethodBase>
        yield! t.GetConstructors flags |> Seq.cast<Reflection.MethodBase>
    ]

// ─── The tests ───────────────────────────────────────────────────────

[<Tests>]
// ─── Phase 840 — the registration gate ───────────────────────────────

/// The shipped converter set as the gate's oracle, and the MessagePack
/// gate's draw count and seed — the pair `registerVerified` uses.
let private gateOracle =
    ToolUp.Remoting.Json.SystemTextJson.FableConverters.decoderOracle

let private gateDraws = RemotingDecoders.DefaultDraws
let private gateSeed = RemotingDecoders.DefaultSeed

/// 840.C — every registration `PlatformJsonDecoders.registerAll` makes,
/// by its `(record, type)` key, beside its recorded verification run. A
/// registration added to `registerAll` without a line here fails the
/// key-set comparison below, so the committed set cannot outgrow its run.
let private platformVerifications
    ()
    : ((string option * string) * Result<JsonDecoderVerification, DecoderRefusal>) list =
    let verify (record: string) (decoder: JsonDecoder<'T>) =
        (Some record, RemotingDecoders.keyFor typeof<'T>),
        JsonDecoders.verifyWith<'T> gateOracle gateDraws gateSeed decoder

    [
        verify "ITeamInviteApi" PlatformJsonDecoders.teamRole
        verify "IPresenceApi" PlatformJsonDecoders.presenceLocation
        verify "IPresenceApi" PlatformJsonDecoders.entityLockRef
        verify "IAuditViewApi" PlatformJsonDecoders.auditTrailQuery
        verify "IProvenanceQueryApi" PlatformJsonDecoders.wireProvenanceRef
        verify "IProvenanceQueryApi" PlatformJsonDecoders.wireProvenanceDirection
        verify "IProvenanceQueryApi" PlatformJsonDecoders.wireProvenanceChainRequest
        verify "ITeamInviteApi" PlatformJsonDecoders.teamInviteIssueRequest
        verify "IHomeOverviewApi" PlatformJsonDecoders.pinRequest
        verify "TeamApi" PlatformJsonDecoders.createTeamRequest
        verify "ITeamInviteApi" PlatformJsonDecoders.pendingInviteIssueRequest
        verify "ITeamInviteApi" PlatformJsonDecoders.teamInviteApiString
        verify "IHomeOverviewApi" PlatformJsonDecoders.homeOverviewApiString
        verify "TeamApi" PlatformJsonDecoders.teamApiString
        verify "TeamApi" PlatformJsonDecoders.teamApiStringPair
        verify "TeamApi" PlatformJsonDecoders.teamApiStringStringRole
    ]

/// 840.D — the go-red: total, correct-looking, and WRONG. Two same-typed
/// fields read in each other's place, so every text is accepted and the
/// value is well-typed and not what the client sent.
let private swappedAddress: JsonDecoder<Address> =
    JsonDecode.succeed (fun line1 postcode country -> {
        Line1 = line1
        Postcode = postcode
        Country = country
    })
    |> JsonDecode.apply (JsonDecode.field "Postcode" JsonDecode.asString)
    |> JsonDecode.apply (JsonDecode.field "Line1" JsonDecode.asString)
    |> JsonDecode.apply (JsonDecode.field "Country" JsonDecode.asString)

/// 840.D — the same failure on a union: a case name read as its
/// neighbour.
let private rotatedPriority: JsonDecoder<Priority> =
    JsonDecode.union "Priority" (function
        | "Low" -> Some(JsonDecode.case0 Low)
        | "Normal" -> Some(JsonDecode.case0 High)
        | "High" -> Some(JsonDecode.case0 Normal)
        | _ -> None)

/// The draw index a wrong decoder must be refused at, computed from the
/// draws themselves rather than trusted from the gate: the first draw of
/// `'T` (same seed, same depth) on which `differs` holds.
let private firstDrawWhere<'T> (differs: 'T -> bool) : int =
    let rng = Random gateSeed

    let rec go i =
        if i >= gateDraws then
            failtestf "no draw of %s in %d distinguishes the wrong decoder" typeof<'T>.Name gateDraws
        else
            match DecoderShapes.draw rng DecoderShapes.DefaultDepth typeof<'T> with
            | Error reason -> failtestf "%s is undrawable: %s" typeof<'T>.Name reason
            | Ok drawn -> if differs (unbox<'T> drawn) then i else go (i + 1)

    go 0

/// 840.E — the lenient inputs: the Phase 784 mutations the converter set
/// ACCEPTS and the algebra REFUSES, each already declared in
/// `algebraOutcomes` above. The writer never produces any of them.
let private declaredStrictness = [
    // STJ hands back a null reference; the algebra has no null record.
    "wrong-tag-nil-for-record"
    // A quoted `"7"` at int32 (`AllowReadingFromString`); the algebra
    // takes no string at a width the writer emits bare.
    "wrong-width-string-into-int"
    // An absent member read back as null; the algebra's `field` requires
    // it.
    "missing-field-record"
]

let tests =
    testList "Phase 799 — the JSON wire joins the decoder algebra" [

        testList "the algebra agrees with the STJ oracle" [
            testCase "the covered set is not vacuous"
            <| fun () ->
                Expect.isGreaterThan
                    (List.length coveredCases)
                    50
                    (sprintf
                        "only %d of %d pinned case(s) are covered by a JSON algebra decoder"
                        (List.length coveredCases)
                        (List.length pinnedCases))

                let uncovered =
                    pinnedCases
                    |> List.filter (fun c -> (tryAlgebra c.ClrType).IsNone)
                    |> List.map (fun c -> c.Name)

                Expect.isEmpty uncovered "every pinned case has a JSON algebra decoder in this file"

            testCase "every covered case decodes to the declared value through the algebra"
            <| fun () ->
                for c in coveredCases do
                    let text = c.WriteJson()

                    match algebraDecode c.ClrType text with
                    | Error e ->
                        failtestf "`%s` refused through the algebra: %s\n  json: %s" c.Name (DecodeError.render e) text
                    | Ok decoded ->
                        match c.Compare decoded with
                        | Ok() -> ()
                        | Error problem -> failtestf "`%s` decoded through the algebra: %s" c.Name problem

            testCase "the algebra and the STJ converter set produce the same value"
            <| fun () ->
                for c in coveredCases do
                    let text = c.WriteJson()

                    let viaStj =
                        try
                            Ok(readJson c text)
                        with ex ->
                            Error ex.Message

                    match viaStj, algebraDecode c.ClrType text with
                    | Ok fromStj, Ok fromAlgebra ->
                        if not (isNull fromStj) && not (isNull fromAlgebra) then
                            Expect.equal
                                (fromAlgebra.GetType().FullName)
                                (fromStj.GetType().FullName)
                                (sprintf "`%s`: the two paths landed on different runtime types" c.Name)
                        else
                            Expect.equal
                                (isNull fromAlgebra)
                                (isNull fromStj)
                                (sprintf "`%s`: one path decoded to null and the other did not" c.Name)

                        Expect.equal
                            (sprintf "%A" fromAlgebra)
                            (sprintf "%A" fromStj)
                            (sprintf "`%s`: the two paths decoded different values" c.Name)
                    | Error problem, Ok _ ->
                        failtestf "`%s`: STJ refused a payload the algebra accepts: %s" c.Name problem
                    | Ok _, Error e ->
                        failtestf "`%s`: the algebra refused a payload STJ accepts: %s" c.Name (DecodeError.render e)
                    | Error _, Error _ -> ()

            testCase "the generated shapes agree too"
            <| fun () ->
                for c in generatedCases () do
                    match tryAlgebra c.ClrType with
                    | None -> ()
                    | Some _ ->
                        let text = c.WriteJson()

                        match algebraDecode c.ClrType text with
                        | Error e -> failtestf "`%s` refused through the algebra: %s" c.Name (DecodeError.render e)
                        | Ok decoded ->
                            match c.Compare decoded with
                            | Ok() -> ()
                            | Error problem -> failtestf "`%s`: %s" c.Name problem

            testCase "the differential CATCHES a decoder that goes through float — the go-red case"
            <| fun () ->
                // `9007199254740993` (2^53 + 1) at `int64`: a decoder that
                // parsed the token as a double and cast would land on
                // 2^53, one short, with no error anywhere. This is the
                // class of loss that disqualified a `float` carrier.
                let text = "9007199254740993"
                let lossy = JsonRead.tryParse text |> Result.bind throughFloat
                let honest = JsonRead.tryParse text |> Result.bind JsonDecode.asInt64

                match lossy, honest with
                | Ok l, Ok h ->
                    Expect.notEqual
                        l
                        h
                        "the through-float decoder agreed with the lexical one, so the differential could not catch it"
                | _ -> failtest "the go-red probe did not produce two decodes to compare"

                Expect.equal honest (Ok 9007199254740993L) "the lexical decoder is exact"

            testCase "the three losses 785.F named are closed: int64 past 2^53, decimal, TimeSpan ticks"
            <| fun () ->
                Expect.equal
                    (JsonRead.tryParse "9007199254740993" |> Result.bind JsonDecode.asInt64)
                    (Ok 9007199254740993L)
                    "2^53 + 1 survives"

                Expect.equal
                    (JsonRead.tryParse "\"+9007199254740993\"" |> Result.bind JsonDecode.asInt64)
                    (Ok 9007199254740993L)
                    "and in the writer's signed-string form"

                Expect.equal
                    (JsonRead.tryParse "79228162514264337593543950335"
                     |> Result.bind JsonDecode.asDecimal)
                    (Ok Decimal.MaxValue)
                    "Decimal.MaxValue survives with every digit"

                Expect.equal
                    (JsonRead.tryParse "0.0000000000000000000000000001"
                     |> Result.bind JsonDecode.asDecimal)
                    (Ok 0.0000000000000000000000000001M)
                    "and the smallest positive decimal"

                // The tick STJ loses: 14:13:31.2158396 came back one tick
                // short through the converter (Phase 784).
                let span = TimeSpan.Parse "14:13:31.2158396"
                let c = both WireClass.DateFamily "timespan-probe" span

                Expect.equal
                    (JsonRead.tryParse (c.WriteJson()) |> Result.bind JsonDecode.asTimeSpan)
                    (Ok span)
                    "the algebra recovers the exact tick from the writer's millisecond text"
        ]

        testList "the refuse path" [
            testCase "every declared algebra outcome holds"
            <| fun () ->
                for name, expected in algebraOutcomes do
                    match mutations () |> List.tryFind (fun m -> m.Name = name) with
                    | None -> failtestf "mutation `%s` no longer exists in the Phase 784 corpus" name
                    | Some m ->
                        match m.Json with
                        | None -> failtestf "mutation `%s` carries no JSON payload" name
                        | Some text ->
                            let measured, detail = classifyAlgebra m.Target text

                            Expect.isTrue
                                (sameOutcomeClass expected measured)
                                (sprintf
                                    "the JSON ALGEBRA's behaviour on mutation `%s` has changed class.\n  declared: %s\n  measured: %s (%s)"
                                    name
                                    (describeOutcome expected)
                                    (describeOutcome measured)
                                    detail)

            testCase "every corpus mutation with a JSON payload is declared here"
            <| fun () ->
                let declared = algebraOutcomes |> List.map fst |> Set.ofList

                let undeclared =
                    mutations ()
                    |> List.filter (fun m -> m.Json.IsSome && (tryAlgebra m.Target).IsSome)
                    |> List.map (fun m -> m.Name)
                    |> List.filter (fun n -> not (declared.Contains n))

                Expect.isEmpty undeclared "a corpus mutation reaches the JSON algebra with no declared outcome"

            testCase "the algebra refuses strictly more than the STJ path, and nothing it throws on"
            <| fun () ->
                let rows = [
                    for name, _ in algebraOutcomes do
                        match mutations () |> List.tryFind (fun m -> m.Name = name) with
                        | Some m ->
                            match m.Json with
                            | Some text ->
                                let viaAlgebra, _ = classifyAlgebra m.Target text
                                let viaStj, _ = classifyJson m.Target text
                                yield name, viaStj, viaAlgebra
                            | None -> ()
                        | None -> ()
                ]

                let thrown =
                    rows
                    |> List.filter (fun (_, _, algebra) ->
                        match algebra with
                        | ThrewUnnamed _ -> true
                        | _ -> false)

                Expect.isEmpty (thrown |> List.map (fun (n, _, _) -> n)) "the algebra path threw on a mutation"

                let lost =
                    rows
                    |> List.filter (fun (_, stj, algebra) -> stj = Refused && algebra <> Refused)

                Expect.isEmpty
                    (lost |> List.map (fun (n, _, _) -> n))
                    "the algebra accepts a mutation STJ refuses — a refusal has been LOST"

                let gained =
                    rows
                    |> List.filter (fun (_, stj, algebra) -> algebra = Refused && stj <> Refused)

                Expect.isNonEmpty gained "the algebra refuses nothing the STJ path did not already refuse"

                printfn
                    "JSON refusals gained over STJ: %s"
                    (gained |> List.map (fun (n, _, _) -> n) |> String.concat ", ")

            testCase "every generated shape mutation meets its declared outcome, and STJ's class is reported beside it"
            <| fun () ->
                let generated = generatedMutations ()
                Expect.isGreaterThan (List.length generated) 80 "the generated mutation set is thin"

                let kinds = generated |> List.map (fun m -> m.Kind) |> List.distinct

                for kind in allMutationKinds do
                    Expect.contains kinds kind (sprintf "no generated JSON mutation of kind %A" kind)

                let disagreements = ResizeArray()

                for m in generated do
                    let measured, detail = classifyAlgebra m.Target m.Text
                    let viaStj, _ = classifyJson m.Target m.Text

                    if not (sameOutcomeClass measured m.Expected) then
                        failtestf
                            "generated mutation `%s` (%A): declared %s, measured %s (%s)\n  json: %s"
                            m.Name
                            m.Kind
                            (describeOutcome m.Expected)
                            (describeOutcome measured)
                            detail
                            m.Text

                    if not (sameOutcomeClass measured viaStj) then
                        disagreements.Add(sprintf "%s: algebra %A, STJ %A" m.Name measured viaStj)

                printfn
                    "generated JSON mutations: %d; the two paths disagree on %d:\n  %s"
                    (List.length generated)
                    disagreements.Count
                    (String.Join("\n  ", disagreements))

            testCase "a refusal carries the path to the member that refused"
            <| fun () ->
                let text =
                    """{"Reference":"r","Origin":{"Line1":"a","Postcode":"b","Country":3},"Destination":{"Line1":"a","Postcode":"b","Country":"c"},"Priority":"Low","Outcome":"Pending","Weights":[],"Labels":[]}"""

                match JsonRead.tryParse text |> Result.bind consignment with
                | Ok _ -> failtest "a number where a string was declared was accepted"
                | Error e ->
                    Expect.equal e.Path [ "Origin"; "Country" ] "the path names the nested member"
                    Expect.equal e.Expected "string" "the refusal names the type"

            testCase "an unknown case name is refused by name, never a fallback case"
            <| fun () ->
                match JsonRead.tryParse "\"Urgent\"" |> Result.bind priority with
                | Ok v -> failtestf "an unknown case name decoded to %A" v
                | Error e -> Expect.stringContains e.Found "Urgent" "the refusal quotes the name"

                match
                    JsonRead.tryParse "{\"Accepted\":[\"not-a-guid\",\"2026-09-13T08:30:00+00:00\"]}"
                    |> Result.bind outcome
                with
                | Ok _ -> failtest "a bad guid inside a several-field case was accepted"
                | Error e -> Expect.equal e.Path [ "[0]" ] "the refusal names the field position"
        ]

        testList "the bounded pass" [
            testCase "nesting past the ceiling is refused by name, on both the parse and the pass"
            <| fun () ->
                let deep n =
                    String.replicate n "[" + String.replicate n "]"

                Expect.isOk (JsonRead.tryParse (deep 64)) "64 containers deep is within the ceiling"

                match JsonRead.tryParse (deep 65) with
                | Ok _ -> failtest "65 containers deep was admitted"
                | Error e ->
                    Expect.stringContains
                        (DecodeError.render e)
                        "JSON document"
                        "STJ's parse refuses first, and it is named"

                // The pass's own bound, exercised with the parser's raised.
                use doc = JsonDocument.Parse(deep 70, JsonDocumentOptions(MaxDepth = 128))

                match JsonRead.tryReadWith 64 1000 doc.RootElement with
                | Ok _ -> failtest "the pass admitted 70 containers under a ceiling of 64"
                | Error e -> Expect.stringContains e.Expected "64 container(s)" "the pass names its ceiling"

            testCase "a container wider than the member bound is refused by name"
            <| fun () ->
                use doc = JsonDocument.Parse "[1,2,3,4,5]"

                match JsonRead.tryReadWith 64 4 doc.RootElement with
                | Ok _ -> failtest "five elements were admitted under a bound of four"
                | Error e -> Expect.stringContains e.Expected "at most 4 element(s)" "the pass names the bound"

                use obj = JsonDocument.Parse """{"a":1,"b":2,"c":3}"""

                match JsonRead.tryReadWith 64 2 obj.RootElement with
                | Ok _ -> failtest "three members were admitted under a bound of two"
                | Error e -> Expect.stringContains e.Expected "at most 2 member(s)" "the pass names the bound"

            testCase "a number's text is carried verbatim, and never through float"
            <| fun () ->
                match JsonRead.tryParse "[1, 1.0, 1e2, -0, 12345678901234567890123456789]" with
                | Error e -> failtestf "refused: %s" (DecodeError.render e)
                | Ok(JsonValue.Array items) ->
                    Expect.equal
                        items
                        [
                            JsonValue.Number "1"
                            JsonValue.Number "1.0"
                            JsonValue.Number "1e2"
                            JsonValue.Number "-0"
                            JsonValue.Number "12345678901234567890123456789"
                        ]
                        "each token as written"
                | Ok other -> failtestf "not an array: %A" other
        ]

        testList "the combinators are total, pure and reflection-free" [
            testCase "no combinator throws on any shape in the value model"
            <| fun () ->
                let shapes = [
                    JsonValue.Null
                    JsonValue.Bool true
                    JsonValue.Number "0"
                    JsonValue.Number "-1.5e3"
                    JsonValue.Number "99999999999999999999"
                    JsonValue.Number "not-a-number"
                    JsonValue.String ""
                    JsonValue.String "x"
                    JsonValue.String "+1"
                    JsonValue.Array []
                    JsonValue.Array [ JsonValue.Null ]
                    JsonValue.Array [ JsonValue.Number "1"; JsonValue.String "a" ]
                    JsonValue.Object []
                    JsonValue.Object [ "Case", JsonValue.Null ]
                    JsonValue.Object [ "a", JsonValue.Number "1"; "b", JsonValue.Number "2" ]
                ]

                for _, decoder in covered do
                    for shape in shapes do
                        try
                            decoder shape |> ignore
                        with ex ->
                            failtestf
                                "a decoder threw %s on %s: %s"
                                (ex.GetType().Name)
                                (JsonValue.describe shape)
                                ex.Message

            testCase "a combinator answers the same twice"
            <| fun () ->
                for c in coveredCases do
                    let text = c.WriteJson()

                    Expect.equal
                        (sprintf "%A" (algebraDecode c.ClrType text))
                        (sprintf "%A" (algebraDecode c.ClrType text))
                        (sprintf "`%s`: two decodes of one text differed" c.Name)

            testCase "the `JsonDecode` module's IL calls no reflection and no `failwith`"
            <| fun () ->
                let types = decodeModuleTypes ()
                Expect.isNonEmpty types "the JsonDecode module's types were not found"

                let offenders = [
                    for t in types do
                        for m in allMethods t do
                            for called in calledMembers m do
                                if forbiddenCall called then
                                    yield sprintf "%s.%s -> %s" t.Name m.Name called
                ]

                Expect.isEmpty offenders "a combinator calls reflection or raises"

            testCase "the number grammar admits exactly RFC 8259's tokens"
            <| fun () ->
                for ok in
                    [
                        "0"
                        "-0"
                        "1"
                        "-1"
                        "1.5"
                        "0.5"
                        "1e5"
                        "1E+5"
                        "1.5e-3"
                        "123456789012345678901234567890"
                    ] do
                    Expect.isTrue (JsonValue.isNumberToken ok) (sprintf "`%s` is a number token" ok)

                for bad in
                    [
                        ""
                        "-"
                        "01"
                        "1."
                        ".5"
                        "+1"
                        "1e"
                        "1e+"
                        "0x10"
                        "NaN"
                        "Infinity"
                        "1 "
                        " 1"
                        "1,0"
                    ] do
                    Expect.isFalse (JsonValue.isNumberToken bad) (sprintf "`%s` is not a number token" bad)

                Expect.isTrue (JsonValue.isIntegralToken "-42") "an integer is integral"
                Expect.isFalse (JsonValue.isIntegralToken "1.0") "a fraction is not, whatever its value"
                Expect.isFalse (JsonValue.isIntegralToken "1e0") "an exponent is not, whatever its value"

            testCase "size decreases into every subterm, over the corpus"
            <| fun () ->
                let rec check (value: JsonValue) =
                    let total = JsonValue.size value

                    match value with
                    | JsonValue.Array items ->
                        for item in items do
                            Expect.isLessThan (JsonValue.size item) total "an element is smaller than its array"
                            check item
                    | JsonValue.Object members ->
                        for _, m in members do
                            Expect.isLessThan (JsonValue.size m) total "a member is smaller than its object"
                            check m
                    | _ -> ()

                for c in coveredCases do
                    match JsonRead.tryParse (c.WriteJson()) with
                    | Ok v -> check v
                    | Error e -> failtestf "`%s`: %s" c.Name (DecodeError.render e)
        ]

        testList "the served argument facet" [
            testCase "coverage is a ratio over the served set by ARGUMENT types, advisory under every profile"
            <| fun () ->
                ToolUp.Platform.ServedApiRecords.resetForTests ()
                JsonDecoders.resetForTests ()
                PlatformJsonDecoders.registerAll ()
                ToolUp.Platform.ServedApiRecords.record typeof<ToolUp.Platform.IPresenceApi>
                // Phase 839 fully covers every one of `PlatformJsonDecoders`'
                // six scoped records now, so a genuinely-uncovered second
                // record for this test is a local probe type rather than a
                // real platform API — one that is registered NOWHERE, under
                // any name, so its bare `string` argument stays uncovered
                // regardless of what the platform set later adds.
                ToolUp.Platform.ServedApiRecords.record typeof<ProbeApi>

                let facet =
                    ToolUp.Platform.RemotingDecoderFacet.inspectServedArguments
                        ToolUp.Platform.CompositionProfile.Verified

                Expect.equal
                    (ToolUp.Platform.RemotingDecoderFacet.coverage facet)
                    (1, 2)
                    "the presence API's arguments are all covered; the probe API takes a bare, unregistered string"

                let probe =
                    facet.FacetBindings |> List.find (fun b -> b.DecoderApiRecord = "ProbeApi")

                Expect.equal probe.DecoderUncovered [ typeof<string>.FullName ] "the uncovered argument type is named"
                Expect.isFalse facet.FacetRequired "advisory even under Verified — see the facet's own note"

                Expect.isOk
                    (ToolUp.Platform.RemotingDecoderFacet.verify facet)
                    "so the verified profile does not refuse on it"

                Expect.stringContains
                    (ToolUp.Platform.RemotingDecoderFacet.describeArguments facet)
                    "1 of 2 served API record(s)"
                    "the boot line carries the ratio"

                ToolUp.Platform.ServedApiRecords.resetForTests ()
                JsonDecoders.resetForTests ()

            testCase "Phase 839 — the platform's own scoped records are now fully covered, not just partially"
            <| fun () ->
                ToolUp.Platform.ServedApiRecords.resetForTests ()
                JsonDecoders.resetForTests ()
                PlatformJsonDecoders.registerAll ()
                ToolUp.Platform.ServedApiRecords.record typeof<ToolUp.Platform.IPresenceApi>
                ToolUp.Platform.ServedApiRecords.record typeof<ToolUp.Platform.IAuditViewApi>
                ToolUp.Platform.ServedApiRecords.record typeof<ToolUp.Platform.IProvenanceQueryApi>
                ToolUp.Platform.ServedApiRecords.record typeof<ToolUp.Platform.ITeamInviteApi>
                ToolUp.Platform.ServedApiRecords.record typeof<ToolUp.Platform.IHomeOverviewApi>
                ToolUp.Platform.ServedApiRecords.record typeof<ToolUp.Platform.TeamApi>

                let facet =
                    ToolUp.Platform.RemotingDecoderFacet.inspectServedArguments
                        ToolUp.Platform.CompositionProfile.Verified

                Expect.equal
                    (ToolUp.Platform.RemotingDecoderFacet.coverage facet)
                    (6, 6)
                    "every one of the six records Phase 839 scoped decoders for is now fully Algebra-covered"

                for binding in facet.FacetBindings do
                    Expect.isEmpty
                        binding.DecoderUncovered
                        (sprintf "%s should have nothing left uncovered" binding.DecoderApiRecord)

                ToolUp.Platform.ServedApiRecords.resetForTests ()
                JsonDecoders.resetForTests ()
        ]

        testList "the argument seam" [
            testCase "a registered type decodes through the algebra at `tryDeserialise`, and a miss is the STJ path"
            <| fun () ->
                JsonDecoders.resetForTests ()

                // Not registered: STJ's leniency applies — a missing
                // member reads back as null.
                use missing = JsonDocument.Parse """{"Line1":"a"}"""

                match
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialise<Address>
                        missing.RootElement
                        jsonOptions
                with
                | Ok a -> Expect.isNull (box a.Postcode) "STJ read the absent member as null"
                | Error e -> failtestf "STJ refused: %s" (DecodeError.render e)

                // Registered (unscoped): the algebra refuses by name, with a path.
                JsonDecoders.register<Address> address

                match
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialise<Address>
                        missing.RootElement
                        jsonOptions
                with
                | Ok _ -> failtest "the registered decoder was not consulted"
                | Error e -> Expect.equal e.Path [ "Postcode" ] "the refusal names the missing member"

                // And a well-formed element decodes to the same value.
                use wellFormed = JsonDocument.Parse """{"Line1":"a","Postcode":"b","Country":"c"}"""

                match
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialise<Address>
                        wellFormed.RootElement
                        jsonOptions
                with
                | Ok a ->
                    Expect.equal
                        a
                        {
                            Line1 = "a"
                            Postcode = "b"
                            Country = "c"
                        }
                        "decoded through the algebra"
                | Error e -> failtestf "refused: %s" (DecodeError.render e)

                // The erased twin takes the same route — `None` is a
                // caller with no record to name, which is exactly what
                // `tryDeserialise` above passes internally.
                match
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseElement
                        None
                        missing.RootElement
                        typeof<Address>
                        jsonOptions
                with
                | Ok _ -> failtest "the erased seam did not consult the registry"
                | Error e -> Expect.equal e.Path [ "Postcode" ] "same refusal on the erased seam"

                JsonDecoders.resetForTests ()

            // ─── Phase 839 — record-scoped registration isolation ─────

            testCase "a record-scoped registration is isolated: it decodes ITS OWN record's arguments and no other's"
            <| fun () ->
                JsonDecoders.resetForTests ()

                // A strict decoder for `Address`, registered ONLY for record "A".
                JsonDecoders.registerFor<Address> "A" address

                use missing = JsonDocument.Parse """{"Line1":"a"}"""

                // Record "A": its own registration is consulted and refuses by name.
                match
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseElement
                        (Some "A")
                        missing.RootElement
                        typeof<Address>
                        jsonOptions
                with
                | Ok _ -> failtest "record A's own registration was not consulted"
                | Error e -> Expect.equal e.Path [ "Postcode" ] "record A's decoder refuses the missing member"

                // Record "B": A's registration never reaches it — STJ's
                // leniency applies, exactly as if nothing were registered.
                match
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseElement
                        (Some "B")
                        missing.RootElement
                        typeof<Address>
                        jsonOptions
                with
                | Ok a -> Expect.isNull (box (unbox<Address> a).Postcode) "record B falls through to STJ untouched"
                | Error e -> failtestf "record B's decode should not have been refused: %s" (DecodeError.render e)

                // No record at all: the same — a caller with no record to
                // name gets exactly the bare-type (unscoped) behaviour,
                // which here is nothing registered.
                match
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseElement
                        None
                        missing.RootElement
                        typeof<Address>
                        jsonOptions
                with
                | Ok a ->
                    Expect.isNull
                        (box (unbox<Address> a).Postcode)
                        "an unscoped caller is unaffected by A's registration"
                | Error e -> failtestf "an unscoped caller should not have been refused: %s" (DecodeError.render e)

                JsonDecoders.resetForTests ()

            testCase
                "an unscoped registration still applies to every record, and a record's own registration wins over it there"
            <| fun () ->
                JsonDecoders.resetForTests ()

                // Unscoped: every record sees it, including a caller that
                // names no record.
                JsonDecoders.register<Address> address

                use missing = JsonDocument.Parse """{"Line1":"a"}"""

                for recordName in [ None; Some "A"; Some "B" ] do
                    match
                        ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseElement
                            recordName
                            missing.RootElement
                            typeof<Address>
                            jsonOptions
                    with
                    | Ok _ -> failtestf "%A: the unscoped registration was not consulted" recordName
                    | Error e ->
                        Expect.equal e.Path [ "Postcode" ] (sprintf "%A: the unscoped decoder refuses" recordName)

                // A DIFFERENT (lenient) decoder registered for "A" only:
                // record A must see ITS OWN decoder rather than the
                // unscoped one; every other record is unaffected. Only
                // `Postcode` is missing here — `Country` is present, so a
                // successful decode is unambiguous evidence that A's own
                // (lenient-on-Postcode) registration, not the unscoped
                // strict one, was consulted.
                use missingPostcodeOnly = JsonDocument.Parse """{"Line1":"a","Country":"c"}"""

                let lenientAddress: JsonDecoder<Address> =
                    JsonDecode.succeed (fun line1 postcode country -> {
                        Line1 = line1
                        Postcode = postcode |> Option.defaultValue ""
                        Country = country
                    })
                    |> JsonDecode.apply (JsonDecode.field "Line1" JsonDecode.asString)
                    |> JsonDecode.apply (JsonDecode.optionalField "Postcode" JsonDecode.asString)
                    |> JsonDecode.apply (JsonDecode.field "Country" JsonDecode.asString)

                JsonDecoders.registerFor<Address> "A" lenientAddress

                match
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseElement
                        (Some "A")
                        missingPostcodeOnly.RootElement
                        typeof<Address>
                        jsonOptions
                with
                | Ok a ->
                    Expect.equal (unbox<Address> a).Postcode "" "record A's OWN registration wins over the unscoped one"
                | Error e ->
                    failtestf "record A should have decoded through its own registration: %s" (DecodeError.render e)

                match
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseElement
                        (Some "B")
                        missingPostcodeOnly.RootElement
                        typeof<Address>
                        jsonOptions
                with
                | Ok _ -> failtest "record B should still fall to the unscoped (strict) decoder and refuse"
                | Error e -> Expect.equal e.Path [ "Postcode" ] "record B is still bound by the unscoped decoder"

                JsonDecoders.resetForTests ()

            testCase "a consumer that registers nothing is byte-for-byte unchanged"
            <| fun () ->
                JsonDecoders.resetForTests ()

                use wellFormed = JsonDocument.Parse """{"Line1":"a","Postcode":"b","Country":"c"}"""

                for recordName in [ None; Some "AnyRecord" ] do
                    match
                        ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseElement
                            recordName
                            wellFormed.RootElement
                            typeof<Address>
                            jsonOptions
                    with
                    | Ok a ->
                        Expect.equal
                            (unbox<Address> a)
                            {
                                Line1 = "a"
                                Postcode = "b"
                                Country = "c"
                            }
                            (sprintf "%A: falls straight through to STJ, unchanged" recordName)
                    | Error _ -> failtestf "%A: should not refuse — nothing is registered" recordName

                JsonDecoders.resetForTests ()

            testCase
                "the platform's first set registers scoped to its own record, and every decoder agrees with STJ over a drawn value"
            <| fun () ->
                JsonDecoders.resetForTests ()
                PlatformJsonDecoders.registerAll ()

                for (recordName, key) in PlatformJsonDecoders.covered do
                    Expect.contains
                        (JsonDecoders.registered ())
                        (recordName, key)
                        (sprintf "%A/%s is not registered" recordName key)

                Expect.equal (JsonDecoders.count ()) (List.length PlatformJsonDecoders.covered) "nothing else is"

                // One drawn value per (record, type), written by the
                // shipped converter set, read back both ways THROUGH THE
                // SAME RECORD SCOPE it is registered under.
                let agreeFor (recordName: string) (value: 'T) =
                    let text = JsonSerializer.Serialize(value, jsonOptions)
                    let viaStj = JsonSerializer.Deserialize<'T>(text, jsonOptions)

                    match
                        JsonRead.tryParse text
                        |> Result.bind (JsonDecoders.tryGet (Some recordName) typeof<'T>).Value
                    with
                    | Ok viaAlgebra ->
                        Expect.equal
                            (unbox<'T> viaAlgebra)
                            viaStj
                            (sprintf "%s/%s: the two paths differ\n  json: %s" recordName typeof<'T>.Name text)
                    | Error e ->
                        failtestf
                            "%s/%s: the algebra refused the writer's own text: %s\n  json: %s"
                            recordName
                            typeof<'T>.Name
                            (DecodeError.render e)
                            text

                agreeFor "ITeamInviteApi" ToolUp.Platform.TeamRole.Admin
                agreeFor "IPresenceApi" ({ Module = "m"; Page = Some "p" }: ToolUp.Platform.PresenceLocation)
                agreeFor "IPresenceApi" ({ Module = "m"; Page = None }: ToolUp.Platform.PresenceLocation)

                agreeFor "IPresenceApi" ({ EntityType = "doc"; EntityId = "42" }: ToolUp.Platform.EntityLockRef)

                agreeFor
                    "IAuditViewApi"
                    ({
                        From = Some(DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc))
                        To = None
                        EventType = Some "login"
                        Actor = None
                        Cursor = Some "c"
                        PageSize = 50
                    }
                    : ToolUp.Platform.AuditTrailQuery)

                agreeFor "IProvenanceQueryApi" (ToolUp.Platform.WireProvenanceRef.FactRef "f1")
                agreeFor "IProvenanceQueryApi" ToolUp.Platform.WireProvenanceDirection.Upstream

                agreeFor
                    "IProvenanceQueryApi"
                    ({
                        Root = ToolUp.Platform.WireProvenanceRef.ResultRef "r"
                        Direction = ToolUp.Platform.WireProvenanceDirection.Downstream
                        Depth = 3
                    }
                    : ToolUp.Platform.WireProvenanceChainRequest)

                agreeFor
                    "ITeamInviteApi"
                    ({
                        TeamId = "t"
                        Role = ToolUp.Platform.TeamRole.Member
                        ExpiresIn = Some(TimeSpan.FromMinutes 90.0)
                        EmailHint = None
                        MaxUses = Some 3
                    }
                    : ToolUp.Platform.TeamInviteIssueRequest)

                agreeFor "IHomeOverviewApi" ({ ModuleId = "home"; Pinned = true }: ToolUp.Platform.PinRequest)

                agreeFor "TeamApi" ({ Name = "n"; InitialOwnerUserId = "u" }: ToolUp.Platform.CreateTeamRequest)

                agreeFor
                    "ITeamInviteApi"
                    ({
                        TeamId = "t"
                        Email = "e@example.com"
                        Role = ToolUp.Platform.TeamRole.Member
                        ExpiresIn = None
                    }
                    : ToolUp.Platform.PendingInviteIssueRequest)

                agreeFor "ITeamInviteApi" "a-token"
                agreeFor "IHomeOverviewApi" "home-tool"
                agreeFor "TeamApi" "team-1"
                agreeFor "TeamApi" ("u1", "u2")
                agreeFor "TeamApi" ("u1", "u2", ToolUp.Platform.TeamRole.Admin)

                JsonDecoders.resetForTests ()
        ]
        testList "Phase 840 — the registration gate" [
            testCase "840.C — the platform's set agrees with the converter set over every registration"
            <| fun () ->
                let runs = platformVerifications ()

                for key, result in runs do
                    match result with
                    | Ok verification ->
                        Expect.equal verification.Verification.Draws gateDraws (sprintf "%A was drawn in full" key)
                        Expect.isNone verification.Verification.Divergence (sprintf "%A agrees" key)

                        // A difference is admitted only as the converter
                        // set's ONE declared loss, and only on a type whose
                        // shape reaches it.
                        for d in verification.DeclaredDifferences do
                            Expect.equal d.Loss.Type typeof<TimeSpan> (sprintf "%A: draw %d" key d.Draw)
                    | Error refusal -> failtestf "%A: %s" key (JsonDecoders.describeRefusal refusal)

                Expect.equal
                    (runs |> List.map fst |> Set.ofList)
                    (Set.ofList PlatformJsonDecoders.covered)
                    "the recorded run covers exactly the registrations `registerAll` makes"

            testCase "every corpus decoder the algebra covers agrees with the converter set"
            <| fun () ->
                let refusals =
                    covered
                    |> List.choose (fun (target, decoder) ->
                        match JsonDecoders.verifyByTypeWith gateOracle gateDraws gateSeed target decoder with
                        | Ok _ -> None
                        | Error refusal -> Some(JsonDecoders.describeRefusal refusal))

                Expect.isEmpty refusals (String.Join("\n", refusals))

            testCase "840.E — the converter set's TimeSpan loss is declared and SAID, never refused and never silent"
            <| fun () ->
                match JsonDecoders.verifyWith gateOracle gateDraws gateSeed JsonDecode.asTimeSpan with
                | Ok verification ->
                    Expect.isNone verification.Verification.Divergence "the exact decoder agrees"

                    Expect.isNonEmpty
                        verification.DeclaredDifferences
                        "the seeded draws include values the converter set reads a tick off"

                    for d in verification.DeclaredDifferences do
                        Expect.equal d.Loss.Type typeof<TimeSpan> "attributed to the declared loss"
                        Expect.notEqual d.Candidate d.Oracle "and both decodes are recorded"
                | Error refusal -> failtest (JsonDecoders.describeRefusal refusal)

                // Without the declaration the same exact decoder is a
                // divergence: the declaration is what admits it.
                match
                    JsonDecoders.verifyWith { gateOracle with Losses = [] } gateDraws gateSeed JsonDecode.asTimeSpan
                with
                | Error(DecoderDiverges _) -> ()
                | other -> failtestf "an undeclared loss was not reported as a divergence: %A" other

            testCase "840.E — a declared loss never excuses a candidate that differs from the written value"
            <| fun () ->
                let aTickLate: JsonDecoder<TimeSpan> =
                    JsonDecode.asTimeSpan |> JsonDecode.map (fun t -> t + TimeSpan.FromTicks 1L)

                match JsonDecoders.verifyWith gateOracle gateDraws gateSeed aTickLate with
                | Error(DecoderDiverges { Divergence = Some _ }) -> ()
                | other -> failtestf "a tick-late decoder was admitted under the declared loss: %A" other

            testCase "840.D — two same-typed fields swapped are refused by name and by draw"
            <| fun () ->
                let expectedDraw = firstDrawWhere<Address> (fun a -> a.Line1 <> a.Postcode)

                match JsonDecoders.verifyWith gateOracle gateDraws gateSeed swappedAddress with
                | Error(DecoderDiverges verification as refusal) ->
                    Expect.equal verification.WireType (RemotingDecoders.keyFor typeof<Address>) "refused by name"

                    match verification.Divergence with
                    | Some divergence ->
                        Expect.equal divergence.Draw expectedDraw "refused at the first draw that tells them apart"
                        Expect.notEqual divergence.Candidate divergence.Reflection "both decodes are rendered"
                    | None -> failtest "a divergence names its draw"

                    let described = JsonDecoders.describeRefusal refusal
                    Expect.stringContains described "Address" "the description names the type"
                    Expect.stringContains described (sprintf "draw %d" expectedDraw) "and the draw"
                | other -> failtestf "the swapped decoder was not refused as a divergence: %A" other

            testCase "840.D — a union case read as its neighbour is refused at its first draw"
            <| fun () ->
                let expectedDraw = firstDrawWhere<Priority> (fun p -> p <> Low)

                match JsonDecoders.verifyWith gateOracle gateDraws gateSeed rotatedPriority with
                | Error(DecoderDiverges { Divergence = Some divergence }) ->
                    Expect.equal divergence.Draw expectedDraw "refused at the first Normal or High"
                | other -> failtestf "the rotated decoder was not refused: %A" other

            testCase "840.B/D — `registerVerified` registers only on agreement and leaves the table untouched otherwise"
            <| fun () ->
                JsonDecoders.resetForTests ()

                let registerVerified =
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.registerVerified

                let registerVerifiedFor =
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.registerVerifiedFor

                match registerVerified swappedAddress with
                | Error(DecoderDiverges _) -> ()
                | other -> failtestf "unscoped: the swapped decoder was not refused: %A" other

                match registerVerifiedFor "IProbeApi" swappedAddress with
                | Error(DecoderDiverges _) -> ()
                | other -> failtestf "scoped: the swapped decoder was not refused: %A" other

                Expect.equal (JsonDecoders.count ()) 0 "a refusal registers nothing, scoped or not"

                match registerVerified address with
                | Ok verification -> Expect.isNone verification.Verification.Divergence "the right decoder agrees"
                | Error refusal -> failtest (JsonDecoders.describeRefusal refusal)

                match registerVerifiedFor "IProbeApi" address with
                | Ok _ -> ()
                | Error refusal -> failtest (JsonDecoders.describeRefusal refusal)

                Expect.isTrue (JsonDecoders.isRegistered None typeof<Address>) "registered unscoped on agreement"

                Expect.equal
                    (JsonDecoders.registered ())
                    [
                        None, RemotingDecoders.keyFor typeof<Address>
                        Some "IProbeApi", RemotingDecoders.keyFor typeof<Address>
                    ]
                    "and scoped"

                // A refused re-registration over a standing one leaves the
                // standing decoder in place — the table is untouched, not
                // emptied.
                match registerVerified swappedAddress with
                | Error _ -> ()
                | Ok _ -> failtest "the swapped decoder was accepted over a standing one"

                let sample = """{"Line1":"a","Postcode":"b","Country":"c"}"""

                match JsonDecoders.tryGet None typeof<Address> with
                | Some decoder ->
                    match JsonRead.tryParse sample |> Result.bind decoder with
                    | Ok value ->
                        Expect.equal
                            (unbox<Address> value)
                            {
                                Line1 = "a"
                                Postcode = "b"
                                Country = "c"
                            }
                            "the standing, verified decoder still answers"
                    | Error e -> failtest (DecodeError.render e)
                | None -> failtest "the standing registration was lost"

                // Plain `register` stays unverified and unchanged: it is
                // the Fable client's path, which has no oracle.
                JsonDecoders.resetForTests ()
                JsonDecoders.register swappedAddress
                Expect.equal (JsonDecoders.count ()) 1 "plain `register` does not consult the oracle"
                JsonDecoders.resetForTests ()

            testCase "an undrawable type is a named refusal, never a silent pass or a throw"
            <| fun () ->
                let anything: JsonDecoder<obj> = fun _ -> Ok(box 1)

                match JsonDecoders.verifyWith gateOracle gateDraws gateSeed anything with
                | Error(DecoderUndrawable(wireType, reason)) ->
                    Expect.equal wireType (RemotingDecoders.keyFor typeof<obj>) "named"
                    Expect.isNonEmpty reason "with a reason"
                | other -> failtestf "`obj` was not refused as undrawable: %A" other

            testCase "840.E — deliberate strictness is declared, and the gate does not report it as divergence"
            <| fun () ->
                for name in declaredStrictness do
                    let m =
                        match mutations () |> List.tryFind (fun m -> m.Name = name) with
                        | Some m -> m
                        | None -> failtestf "mutation `%s` no longer exists in the Phase 784 corpus" name

                    let text =
                        match m.Json with
                        | Some text -> text
                        | None -> failtestf "mutation `%s` carries no JSON payload" name

                    // The declared difference: the converter set accepts
                    // the lenient input, the algebra refuses it.
                    match classifyJson m.Target text with
                    | Accepted _, _ -> ()
                    | other, detail -> failtestf "`%s`: STJ no longer accepts it (%A: %s)" name other detail

                    match classifyAlgebra m.Target text with
                    | Refused, _ -> ()
                    | other, detail -> failtestf "`%s`: the algebra no longer refuses it (%A: %s)" name other detail

                    Expect.equal
                        (algebraOutcomes |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd)
                        (Some Refused)
                        (sprintf "`%s` is declared in the refuse-path table" name)

                    // And the gate over that SAME decoder is green: the
                    // writer never produces the lenient input, so the
                    // strictness is unreachable from a draw.
                    match tryAlgebra m.Target with
                    | None -> failtestf "`%s`: no corpus decoder for %s" name m.Target.FullName
                    | Some decoder ->
                        match JsonDecoders.verifyByTypeWith gateOracle gateDraws gateSeed m.Target decoder with
                        | Ok verification -> Expect.isNone verification.Verification.Divergence name
                        | Error refusal -> failtestf "`%s`: %s" name (JsonDecoders.describeRefusal refusal)
        ]
    ]