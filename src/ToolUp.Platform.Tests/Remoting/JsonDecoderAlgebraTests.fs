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
open Expecto
open ToolUp.Remoting
open ToolUp.Remoting.Json
open ToolUp.Platform.Tests.Remoting.WireCorpus

/// Phase 839 — a minimal test-local "served API record" with one bare,
/// deliberately unregistered `string` argument. Used by the served
/// argument facet tests below to demonstrate genuine partial coverage
/// without depending on a REAL platform API record staying uncovered —
/// `PlatformJsonDecoders` covers every platform record's arguments since
/// Phase 841 generated it, so a real API is the wrong fixture for "still
/// uncovered".
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

/// Phase 844 promoted the shape-by-shape generator itself into
/// `WireCorpus` (`generatedMutations`), declared over BOTH wires exactly
/// like the hand-written `mutations()` rows — so this arm now draws the
/// population rather than deriving it, restricted to the cases this
/// file's algebra actually covers.
let private generatedMutations () =
    WireCorpus.generatedMutations coveredCases

/// What the ALGEBRA (as opposed to the STJ oracle `WireCorpus` declares
/// `ExpectedJson` against) must do with a generated mutation, by class of
/// damage — every kind this generator produces is a refuse-path shape
/// except a surplus member, which every JSON decoder in this pack (STJ
/// and algebra alike) is deliberately tolerant of.
let private algebraExpectedFor (kind: MutationKind) =
    match kind with
    | MutationKind.ExtraField -> Accepted "a surplus member is ignored"
    | MutationKind.WrongTag
    | MutationKind.Truncated
    | MutationKind.WrongWidth
    | MutationKind.MissingField -> Refused

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
/// by its `(record, type)` key, beside its recorded verification run.
/// Phase 841 — the run is the GENERATED module's own `verifyAll`, emitted
/// beside `registerAll` from the same registration list, so the committed
/// set cannot outgrow its run by construction; the key-set comparison below
/// still holds the two to each other.
let private platformVerifications
    ()
    : ((string option * string) * Result<JsonDecoderVerification, DecoderRefusal>) list =
    PlatformJsonDecoders.verifyAll gateOracle gateDraws gateSeed

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
                    match m.Json with
                    | None -> ()
                    | Some text ->
                        let measured, detail = classifyAlgebra m.Target text
                        let viaStj, _ = classifyJson m.Target text
                        let expected = algebraExpectedFor m.Kind

                        if not (sameOutcomeClass measured expected) then
                            failtestf
                                "generated mutation `%s` (%A): declared %s, measured %s (%s)\n  json: %s"
                                m.Name
                                m.Kind
                                (describeOutcome expected)
                                (describeOutcome measured)
                                detail
                                text

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
            testCase "coverage is a ratio over the served set by ARGUMENT types, advisory under Standard"
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

                // Phase 842 made the facet follow the profile
                // (`requiresAlgebraDecoders`), the same predicate the
                // response facet has used since Phase 801 — so this case,
                // which is about the STANDARD (advisory) reading, now
                // inspects under Standard rather than Verified. The
                // Verified/mandatory reading is "Phase 842" below.
                let facet =
                    ToolUp.Platform.RemotingDecoderFacet.inspectServedArguments
                        ToolUp.Platform.CompositionProfile.Standard

                Expect.equal
                    (ToolUp.Platform.RemotingDecoderFacet.coverage facet)
                    (1, 2)
                    "the presence API's arguments are all covered; the probe API takes a bare, unregistered string"

                let probe =
                    facet.FacetBindings |> List.find (fun b -> b.DecoderApiRecord = "ProbeApi")

                Expect.equal probe.DecoderUncovered [ typeof<string>.FullName ] "the uncovered argument type is named"
                Expect.isFalse facet.FacetRequired "advisory under Standard"

                Expect.isOk
                    (ToolUp.Platform.RemotingDecoderFacet.verifyArguments facet)
                    "not required under Standard, so nothing refuses"

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

        testList "Phase 842 — the argument facet becomes mandatory under the verified profile" [
            testCase "842.A — mandatory under Verified, advisory under Standard"
            <| fun () ->
                ToolUp.Platform.ServedApiRecords.resetForTests ()
                JsonDecoders.resetForTests ()
                PlatformJsonDecoders.registerAll ()
                ToolUp.Platform.ServedApiRecords.record typeof<ToolUp.Platform.IPresenceApi>

                let standard =
                    ToolUp.Platform.RemotingDecoderFacet.inspectServedArguments
                        ToolUp.Platform.CompositionProfile.Standard

                Expect.isFalse standard.FacetRequired "the argument facet stays advisory under Standard"

                let verified =
                    ToolUp.Platform.RemotingDecoderFacet.inspectServedArguments
                        ToolUp.Platform.CompositionProfile.Verified

                Expect.isTrue
                    verified.FacetRequired
                    "mandatory under Verified since Phase 842, following the response facet since Phase 801"

                // 842.E — a Verified deployment serving only platform
                // records (fully covered by Phase 841's generated
                // decoders) boots.
                Expect.isOk
                    (ToolUp.Platform.RemotingDecoderFacet.verifyArguments verified)
                    "IPresenceApi's arguments are fully covered by PlatformJsonDecoders (Phase 841), so a Verified deployment serving only platform records boots"

                ToolUp.Platform.ServedApiRecords.resetForTests ()
                JsonDecoders.resetForTests ()

            testCase
                "842.B/C — a Verified deployment serving an unregistered consumer record refuses, naming the record and its uncovered argument types"
            <| fun () ->
                ToolUp.Platform.ServedApiRecords.resetForTests ()
                JsonDecoders.resetForTests ()
                PlatformJsonDecoders.registerAll ()
                ToolUp.Platform.ServedApiRecords.record typeof<ToolUp.Platform.IPresenceApi>
                // ProbeApi (this file's local record, `DoThing: string -> Async<unit>`)
                // stands in for "a consumer's own record" — registered
                // nowhere, so its `string` argument stays uncovered.
                ToolUp.Platform.ServedApiRecords.record typeof<ProbeApi>

                let verified =
                    ToolUp.Platform.RemotingDecoderFacet.inspectServedArguments
                        ToolUp.Platform.CompositionProfile.Verified

                match ToolUp.Platform.RemotingDecoderFacet.verifyArguments verified with
                | Ok() -> failtest "a served record with an unregistered argument type must refuse under Verified"
                | Error(ToolUp.Platform.RemotingArgumentDecodersUnregistered records) ->
                    Expect.equal (List.map fst records) [ "ProbeApi" ] "the refusal names the served record"

                    Expect.equal
                        (records |> List.tryFind (fun (r, _) -> r = "ProbeApi") |> Option.map snd)
                        (Some [ typeof<string>.FullName ])
                        "the refusal names the uncovered argument type beside the record"

                    let message =
                        ToolUp.Platform.CompositionProfileRefusal.describe (
                            ToolUp.Platform.RemotingArgumentDecodersUnregistered records
                        )

                    Expect.stringContains message "ProbeApi" "the operator-facing message names the record"

                    Expect.stringContains
                        message
                        typeof<string>.FullName
                        "the operator-facing message names the uncovered type, not just that something is missing"
                | Error other -> failtestf "expected RemotingArgumentDecodersUnregistered, got %A" other

                // 842.C — the same served set under Standard is
                // unaffected: the gap is the same, but nothing refuses.
                let standard =
                    ToolUp.Platform.RemotingDecoderFacet.inspectServedArguments
                        ToolUp.Platform.CompositionProfile.Standard

                Expect.isOk
                    (ToolUp.Platform.RemotingDecoderFacet.verifyArguments standard)
                    "Standard never refuses on the argument facet — register the consumer's own decoders, or stay on Standard"

                ToolUp.Platform.ServedApiRecords.resetForTests ()
                JsonDecoders.resetForTests ()

            testCase "842.E — the deployment report renders the argument-decoder ratio on both sides"
            <| fun () ->
                ToolUp.Platform.ServedApiRecords.resetForTests ()
                JsonDecoders.resetForTests ()
                PlatformJsonDecoders.registerAll ()
                ToolUp.Platform.ServedApiRecords.record typeof<ToolUp.Platform.IPresenceApi>

                let coveredEvidence =
                    ToolUp.Platform.RemotingDecoderFacet.inspectServedArguments
                        ToolUp.Platform.CompositionProfile.Verified
                    |> ToolUp.Platform.RemotingDecoderFacet.toIntegrity
                    |> Some
                    |> fun integrity ->
                        ToolUp.Platform.DeploymentVerificationEvidence.none
                        |> ToolUp.Platform.DeploymentVerificationEvidence.withRemotingArgumentDecoders integrity

                let coveredSection =
                    ToolUp.Platform.DeploymentVerificationReport.gatherRemotingArgumentDecoders coveredEvidence

                // `inspectServedArguments` hardcodes `DecoderCorpusCovered
                // = false` throughout (the wire corpus draws its own
                // types, never a platform argument type — see the
                // facet's own doc comment), so this section can never
                // read `Verified`, only `Observed` — an honest ceiling,
                // not a gap in this test — and its summary counts
                // corpus-covered records, which is zero here even though
                // IPresenceApi is fully algebra-covered. The per-record
                // FINDING is what actually renders that: one line,
                // naming IPresenceApi, with nothing uncovered.
                match coveredSection.Verdict with
                | ToolUp.Platform.VerificationSectionVerdict.Observed summary ->
                    Expect.stringContains
                        summary
                        "of 1 declared API record(s)"
                        "the fully-covered side renders the denominator"

                    Expect.stringContains
                        summary
                        "0 still take one or more by reflection"
                        "nothing is left on the reflection path"
                | other -> failtestf "expected Observed, got %A" other

                Expect.equal
                    coveredSection.Findings
                    [ "IPresenceApi: algebra, corpus coverage NOT declared" ]
                    "the fully-covered side's one finding names the record, fully covered"

                ToolUp.Platform.ServedApiRecords.resetForTests ()
                JsonDecoders.resetForTests ()
                PlatformJsonDecoders.registerAll ()
                ToolUp.Platform.ServedApiRecords.record typeof<ToolUp.Platform.IPresenceApi>
                ToolUp.Platform.ServedApiRecords.record typeof<ProbeApi>

                let partialEvidence =
                    ToolUp.Platform.RemotingDecoderFacet.inspectServedArguments
                        ToolUp.Platform.CompositionProfile.Verified
                    |> ToolUp.Platform.RemotingDecoderFacet.toIntegrity
                    |> Some
                    |> fun integrity ->
                        ToolUp.Platform.DeploymentVerificationEvidence.none
                        |> ToolUp.Platform.DeploymentVerificationEvidence.withRemotingArgumentDecoders integrity

                let partialSection =
                    ToolUp.Platform.DeploymentVerificationReport.gatherRemotingArgumentDecoders partialEvidence

                match partialSection.Verdict with
                | ToolUp.Platform.VerificationSectionVerdict.Observed summary ->
                    Expect.stringContains
                        summary
                        "of 2 declared API record(s)"
                        "the partially-covered side renders the denominator"

                    Expect.stringContains
                        summary
                        "1 still take one or more by reflection"
                        "the uncovered record shows up in the reflection count"
                | other -> failtestf "expected Observed, got %A" other

                Expect.equal
                    partialSection.Findings
                    [
                        "IPresenceApi: algebra, corpus coverage NOT declared"
                        sprintf
                            "ProbeApi: reflection, corpus coverage NOT declared — no decoder for %s"
                            typeof<string>.FullName
                    ]
                    "the partially-covered side's findings name both records, one still uncovered"

                ToolUp.Platform.ServedApiRecords.resetForTests ()
                JsonDecoders.resetForTests ()
        ]
        // ─── Phase 845 — decide the Fable `Long` object form on `asInt64` ──
        //
        // The decision (see `docs/migrations/799-json-wire-decoder-algebra.md`):
        // ADMIT the Fable/Long.js runtime shape `{"high","low","unsigned"}` in
        // `asInt64` / `asUInt64`, matching what the pre-existing STJ
        // `Int64Converter` / `UInt64Converter` already reconstructed a value
        // from. The rejected alternative was to leave the algebra narrower
        // than the converter set it replaces — a consumer whose hand-built
        // request currently works against STJ would silently start being
        // refused the moment its argument type moved onto the algebra, which
        // is a regression the algebra opts a consumer INTO rather than one it
        // asked for.
        testList "Phase 845 — decide the Fable `Long` object form on `asInt64`" [
            let longObjectJson (low: int32) (high: int32) (isUnsigned: bool) =
                sprintf """{"high":%d,"low":%d,"unsigned":%b}""" high low isUnsigned

            let bitsOfInt64 (value: int64) : int32 * int32 =
                let bits = uint64 value
                int32 (uint32 bits), int32 (uint32 (bits >>> 32))

            let bitsOfUInt64 (value: uint64) : int32 * int32 =
                int32 (uint32 value), int32 (uint32 (value >>> 32))

            let signedString (value: int64) =
                (if value >= 0L then "+" else "") + string value

            testCase
                "845.A/B/C — the object form decodes `asInt64` to the same value as the number and signed-string forms, over the same population"
            <| fun () ->
                let population = [ 0L; 1L; -1L; 42L; -42L; 9007199254740993L; Int64.MinValue; Int64.MaxValue ]

                for value in population do
                    let low, high = bitsOfInt64 value
                    let objectText = longObjectJson low high (value >= 0L)

                    match JsonRead.tryParse objectText |> Result.bind JsonDecode.asInt64 with
                    | Ok decoded -> Expect.equal decoded value (sprintf "object form decodes %d" value)
                    | Error e -> failtestf "object form for %d was refused: %s" value (DecodeError.render e)

                    Expect.equal
                        (JsonRead.tryParse (string value) |> Result.bind JsonDecode.asInt64)
                        (Ok value)
                        "agrees with the number form"

                    Expect.equal
                        (JsonRead.tryParse (sprintf "\"%s\"" (signedString value))
                         |> Result.bind JsonDecode.asInt64)
                        (Ok value)
                        "agrees with the signed-string form"

            testCase "845.B — the reciprocal `asUInt64` admits the object form identically"
            <| fun () ->
                let population = [ 0UL; 1UL; 42UL; 9007199254740993UL; UInt64.MaxValue ]

                for value in population do
                    let low, high = bitsOfUInt64 value
                    let objectText = longObjectJson low high true

                    match JsonRead.tryParse objectText |> Result.bind JsonDecode.asUInt64 with
                    | Ok decoded -> Expect.equal decoded value (sprintf "object form decodes %d" value)
                    | Error e -> failtestf "object form for %d was refused: %s" value (DecodeError.render e)

                    Expect.equal
                        (JsonRead.tryParse (string value) |> Result.bind JsonDecode.asUInt64)
                        (Ok value)
                        "agrees with the number form"

                    Expect.equal
                        (JsonRead.tryParse (sprintf "\"%d\"" value) |> Result.bind JsonDecode.asUInt64)
                        (Ok value)
                        "agrees with the digit-string form"

            testCase "845 — a malformed object form (missing / non-numeric `low` or `high`) is refused, never thrown"
            <| fun () ->
                for text in [ """{"high":1}"""; """{"low":1}"""; """{"low":"x","high":1}"""; "{}" ] do
                    match JsonRead.tryParse text |> Result.bind JsonDecode.asInt64 with
                    | Error _ -> ()
                    | Ok decoded -> failtestf "`%s` should have been refused, decoded to %d" text decoded
        ]

        testList "Phase 885 — the JSON gate verifies against the browser's writer" [
            let browserGate = JsonDecoders.browserOracle gateOracle

            // The pre-885 `asDecimal`, kept as a probe: a number token only.
            // Right on every text the SERVER writes, wrong on the browser's
            // quoted decimal — exactly the decoder 840's one-writer gate
            // admitted and the browser's first call refused.
            let numberOnlyDecimal: JsonDecoder<decimal> =
                function
                | JsonValue.Number _ as value -> JsonDecode.asDecimal value
                | value -> Error(DecodeError.create "Decimal" (JsonValue.describe value))

            let decodeText (decoder: JsonDecoder<'T>) (text: string) =
                JsonRead.tryParse text |> Result.bind decoder

            // The browser texts the SERVER refuses on both of its paths, each
            // with why. Declared, and asserted still refused, so the list
            // cannot outlive the fact.
            let serverRefusesBrowserText = [
                "NaN: a quoted name",
                "the converter set reads a double from no string and the algebra's asFloat takes a number token only: a browser NaN argument is refused on both paths, as it was before this phase"
            ]

            // Measured by this phase's gate and NOT closed by it: a `Map`
            // whose key is neither primitive nor an enum-like union is written
            // by the browser as an ARRAY of `[key, value]` pairs, which the
            // converter set reads and `JsonDecode.asMap` (an object only, keys
            // read through a member-name `KeyDecoder`) refuses — even empty
            // (`[]`). Closing it needs a JSON-value key decoder on `asMap` and
            // in the generator: a successor phase, not a widening of this one.
            // The gate REFUSES such a decoder against the browser's writer,
            // which is the gate doing its job; the case is asserted refused
            // so the declaration goes red the day the successor lands.
            let browserStrictness: (Type * string) list = [
                typeof<Map<Outcome, Address>>,
                "the browser writes a union-keyed Map as an array of [key, value] pairs; asMap reads an object only"
            ]

            testCase "885.A — the mirror writes every pinned case exactly as the transpiled Fable.SimpleJson does"
            <| fun () ->
                Expect.isGreaterThan (List.length BrowserWriterFixture.cases) 40 "the fixture is close to empty"

                for c in BrowserWriterFixture.cases do
                    Expect.equal (BrowserJsonWriter.serialize c.ValueType c.Value) c.Browser c.Name

            testCase "885.A — the server's reference decode reads every pinned browser text to its value"
            <| fun () ->
                for c in BrowserWriterFixture.cases do
                    match List.tryFind (fun (name, _) -> name = c.Name) serverRefusesBrowserText with
                    | Some(_, reason) ->
                        Expect.isError (browserGate.Decode c.ValueType c.Browser) (c.Name + ": " + reason)
                        Expect.isError (decodeText JsonDecode.asFloat c.Browser) (c.Name + ": " + reason)
                    | None ->
                        match browserGate.Decode c.ValueType c.Browser with
                        | Ok decoded -> Expect.equal (sprintf "%A" decoded) (sprintf "%A" c.Value) c.Name
                        | Error e ->
                            failtestf
                                "%s: the converter set refused the browser's text: %s"
                                c.Name
                                (DecodeError.render e)

            testCase
                "885.B — the gate runs both references: right on the server's text, wrong on the browser's, is refused"
            <| fun () ->
                match JsonDecoders.verifyWith gateOracle gateDraws gateSeed numberOnlyDecimal with
                | Ok verification ->
                    Expect.isNone verification.Verification.Divergence "the server writer never quotes a decimal"
                | Error refusal ->
                    failtestf "the probe should agree on the server's text: %s" (JsonDecoders.describeRefusal refusal)

                match JsonDecoders.verifyBothWith gateOracle gateDraws gateSeed numberOnlyDecimal with
                | Error(DecoderDiverges { Divergence = Some d }) ->
                    Expect.equal d.Draw 0 "refused at the browser's first draw"
                    Expect.stringContains d.Candidate "Error" "the candidate refused the quoted decimal"
                | other -> failtestf "the browser's quoted decimal was not refused: %A" other

                JsonDecoders.resetForTests ()

                match ToolUp.Remoting.Json.SystemTextJson.FableConverters.registerVerified numberOnlyDecimal with
                | Error(DecoderDiverges _) -> ()
                | other -> failtestf "`registerVerified` admitted a decoder the browser's text refutes: %A" other

                match
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.verifyDecoder
                        gateDraws
                        gateSeed
                        numberOnlyDecimal
                with
                | Error(DecoderDiverges _) -> ()
                | other -> failtestf "`verifyDecoder` ran one writer only: %A" other

                Expect.equal (JsonDecoders.count ()) 0 "the refusal registered nothing"

            testCase "885.B — the corpus decoders and the platform set agree with the browser's writer"
            <| fun () ->
                let refusals =
                    covered
                    |> List.choose (fun (target, decoder) ->
                        let declared = List.tryFind (fun (t, _) -> t = target) browserStrictness

                        match
                            JsonDecoders.verifyByTypeWith browserGate gateDraws gateSeed target decoder, declared
                        with
                        | Ok _, None -> None
                        | Error refusal, None -> Some(JsonDecoders.describeRefusal refusal)
                        | Error(DecoderDiverges _), Some _ -> None
                        | other, Some(_, reason) ->
                            Some(sprintf "%s: declared (%s) but the gate said %A" target.FullName reason other))

                Expect.isEmpty refusals (String.Join("\n", refusals))

                for key, result in PlatformJsonDecoders.verifyAll browserGate gateDraws gateSeed do
                    match result with
                    | Ok verification ->
                        for d in verification.DeclaredDifferences do
                            Expect.equal d.Loss.Type typeof<TimeSpan> (sprintf "%A: draw %d" key d.Draw)
                    | Error refusal -> failtestf "%A: %s" key (JsonDecoders.describeRefusal refusal)

            testCase "885.B — the browser twin declares the converter set's TimeSpan loss, and says which writer it was"
            <| fun () ->
                match JsonDecoders.verifyWith browserGate gateDraws gateSeed JsonDecode.asTimeSpan with
                | Ok verification ->
                    Expect.isNonEmpty
                        verification.DeclaredDifferences
                        "the browser's millisecond text is read a tick off too"

                    for d in verification.DeclaredDifferences do
                        Expect.equal d.Loss.Type typeof<TimeSpan> "attributed to the declared loss"
                        Expect.stringStarts d.Loss.Reason JsonDecoders.BrowserLossPrefix "and to the browser's writer"
                | Error refusal -> failtest (JsonDecoders.describeRefusal refusal)

                match
                    JsonDecoders.verifyWith { browserGate with Losses = [] } gateDraws gateSeed JsonDecode.asTimeSpan
                with
                | Error(DecoderDiverges _) -> ()
                | other -> failtestf "an undeclared loss on the browser's text was not a divergence: %A" other

                match JsonDecoders.verifyBothWith gateOracle gateDraws gateSeed JsonDecode.asTimeSpan with
                | Ok verification ->
                    let browser, server =
                        verification.DeclaredDifferences
                        |> List.partition (fun d -> d.Loss.Reason.StartsWith JsonDecoders.BrowserLossPrefix)

                    Expect.isNonEmpty server "the server writer's run is said"
                    Expect.isNonEmpty browser "and the browser writer's, each naming its writer"
                | Error refusal -> failtest (JsonDecoders.describeRefusal refusal)

            testCase
                "885.C decimal — ADMITTED: a quoted decimal reads exactly, as the converter set's AllowReadingFromString does"
            <| fun () ->
                Expect.equal (decodeText JsonDecode.asDecimal "\"1234.5\"") (Ok 1234.5M) "the browser's form"

                Expect.equal
                    (decodeText JsonDecode.asDecimal "\"79228162514264337593543950335\"")
                    (Ok 79228162514264337593543950335M)
                    "every digit, not through a double"

                Expect.equal
                    (decodeText JsonDecode.asDecimal "\"0.0000000000000000000000000001\"")
                    (Ok 0.0000000000000000000000000001M)
                    "the smallest positive"

                Expect.equal (decodeText JsonDecode.asDecimal "1234.50") (Ok 1234.50M) "the number form, unchanged"

                for text in [ "\"abc\""; "\" 1\""; "\"1 \""; "\"+1\""; "\"\""; "\"0x10\""; "\"1e999\"" ] do
                    match decodeText JsonDecode.asDecimal text with
                    | Error _ -> ()
                    | Ok d -> failtestf "`%s` is not a decimal the browser writes, decoded to %M" text d

                match JsonDecoders.verifyBothWith gateOracle gateDraws gateSeed JsonDecode.asDecimal with
                | Ok verification -> Expect.isEmpty verification.DeclaredDifferences "exact on both writers"
                | Error refusal -> failtest (JsonDecoders.describeRefusal refusal)

            testCase
                "885.C TimeSpan — ONE representation: milliseconds as a number token, every writer's spelling to the same tick"
            <| fun () ->
                let read = decodeText JsonDecode.asTimeSpan

                // The browser (`90000`), the server and generated encoders
                // (`90000.0`), and a double's exponent spelling.
                for text in [ "90000"; "90000.0"; "9e4"; "9E+4" ] do
                    Expect.equal (read text) (Ok(TimeSpan.FromSeconds 90.0)) text

                Expect.equal (read "1.5") (Ok(TimeSpan.FromTicks 15000L)) "fractional milliseconds, exactly"
                Expect.equal (read "0.0001") (Ok(TimeSpan.FromTicks 1L)) "one tick"
                Expect.equal (read "-250.25") (Ok(TimeSpan.FromTicks -2502500L)) "negative"

                // The same span through every writer is the same number.
                let span = TimeSpan.FromMilliseconds 5400000.25
                Expect.equal (BrowserJsonWriter.serialize typeof<TimeSpan> (box span)) "5400000.25" "the browser"
                Expect.equal (JsonEncode.toText (JsonEncode.timeSpan span)) "5400000.25" "the generated encoder"
                Expect.equal (gateOracle.Write typeof<TimeSpan> (box span)) "5400000.25" "the converter set"

                // No writer uses a second representation (ticks, a string).
                for c in BrowserWriterFixture.cases do
                    if c.ValueType = typeof<TimeSpan> then
                        match JsonRead.tryParse c.Browser with
                        | Ok(JsonValue.Number _) -> Expect.equal (read c.Browser) (Ok(unbox<TimeSpan> c.Value)) c.Name
                        | other -> failtestf "%s: the browser wrote a TimeSpan as %A, not a number" c.Name other

            testCase
                "885.C DateTime — the browser's toISOString reads with its kind: `Z` is UTC, no offset is Unspecified"
            <| fun () ->
                match decodeText JsonDecode.asDateTime "\"2026-09-27T10:30:00.123Z\"" with
                | Ok d ->
                    Expect.equal d.Kind DateTimeKind.Utc "UTC kind"
                    Expect.equal d (DateTime(2026, 9, 27, 10, 30, 0, 123, DateTimeKind.Utc)) "the instant"
                | Error e -> failtest (DecodeError.render e)

                match decodeText JsonDecode.asDateTime "\"2026-01-15T08:05:09.007\"" with
                | Ok d ->
                    Expect.equal d.Kind DateTimeKind.Unspecified "Unspecified kind"
                    Expect.equal d (DateTime(2026, 1, 15, 8, 5, 9, 7)) "its fields"
                | Error e -> failtest (DecodeError.render e)

            testCase
                "885 acceptance — a consumer argument's decimal, TimeSpan and DateTime round-trip from each browser writer to the server decoder"
            <| fun () ->
                let booking = BrowserWriterFixture.booking

                Expect.equal
                    (JsonEncode.arguments [ BrowserWriterFixture.bookingEncoder booking ])
                    BrowserWriterFixture.ServerBody
                    "the encoder on this host writes the server's spelling"

                Expect.equal
                    ("["
                     + BrowserJsonWriter.serialize typeof<BrowserWriterFixture.Booking> (box booking)
                     + "]")
                    BrowserWriterFixture.ReflectiveBody
                    "the mirror writes what the reflective proxy sent"

                let bodies = [
                    "reflective proxy", BrowserWriterFixture.ReflectiveBody
                    "generated proxy, in the browser", BrowserWriterFixture.EncodedBody
                    "server writer", BrowserWriterFixture.ServerBody
                ]

                for name, body in bodies do
                    use document = JsonDocument.Parse body
                    let element = document.RootElement.[0]

                    for route in [ "algebra"; "converter set" ] do
                        JsonDecoders.resetForTests ()

                        if route = "algebra" then
                            JsonDecoders.registerFor<BrowserWriterFixture.Booking>
                                "BookingApi"
                                BrowserWriterFixture.bookingDecoder

                        match
                            ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseElement
                                (Some "BookingApi")
                                element
                                typeof<BrowserWriterFixture.Booking>
                                jsonOptions
                        with
                        | Ok value ->
                            let b = unbox<BrowserWriterFixture.Booking> value
                            let label = sprintf "%s via the %s" name route
                            Expect.equal b booking label
                            Expect.equal b.At.Kind DateTimeKind.Utc (label + ": UTC kind")
                            Expect.equal b.Duration.Ticks 54000002500L (label + ": the tick")
                        | Error e -> failtestf "%s via the %s: refused: %s" name route (DecodeError.render e)

                JsonDecoders.resetForTests ()
        ]
    ]