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
                ToolUp.Platform.ServedApiRecords.record typeof<ToolUp.Platform.IHomeOverviewApi>

                let facet =
                    ToolUp.Platform.RemotingDecoderFacet.inspectServedArguments
                        ToolUp.Platform.CompositionProfile.Verified

                Expect.equal
                    (ToolUp.Platform.RemotingDecoderFacet.coverage facet)
                    (1, 2)
                    "the presence API's arguments are all covered; the home API takes a bare string"

                let home =
                    facet.FacetBindings
                    |> List.find (fun b -> b.DecoderApiRecord = "IHomeOverviewApi")

                Expect.equal home.DecoderUncovered [ typeof<string>.FullName ] "the uncovered argument type is named"
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

                // Registered: the algebra refuses by name, with a path.
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

                // The erased twin takes the same route.
                match
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseElement
                        missing.RootElement
                        typeof<Address>
                        jsonOptions
                with
                | Ok _ -> failtest "the erased seam did not consult the registry"
                | Error e -> Expect.equal e.Path [ "Postcode" ] "same refusal on the erased seam"

                JsonDecoders.resetForTests ()

            testCase "the platform's first set registers, and every decoder in it agrees with STJ over a drawn value"
            <| fun () ->
                JsonDecoders.resetForTests ()
                PlatformJsonDecoders.registerAll ()

                for key in PlatformJsonDecoders.covered do
                    Expect.contains (JsonDecoders.registered ()) key (sprintf "%s is not registered" key)

                Expect.equal (JsonDecoders.count ()) (List.length PlatformJsonDecoders.covered) "nothing else is"

                // One drawn value per type, written by the shipped
                // converter set, read back both ways.
                let agree (value: 'T) =
                    let text = JsonSerializer.Serialize(value, jsonOptions)
                    let viaStj = JsonSerializer.Deserialize<'T>(text, jsonOptions)

                    match JsonRead.tryParse text |> Result.bind (JsonDecoders.tryGet typeof<'T>).Value with
                    | Ok viaAlgebra ->
                        Expect.equal
                            (unbox<'T> viaAlgebra)
                            viaStj
                            (sprintf "%s: the two paths differ\n  json: %s" typeof<'T>.Name text)
                    | Error e ->
                        failtestf
                            "%s: the algebra refused the writer's own text: %s\n  json: %s"
                            typeof<'T>.Name
                            (DecodeError.render e)
                            text

                agree ToolUp.Platform.TeamRole.Admin
                agree ({ Module = "m"; Page = Some "p" }: ToolUp.Platform.PresenceLocation)
                agree ({ Module = "m"; Page = None }: ToolUp.Platform.PresenceLocation)

                agree ({ EntityType = "doc"; EntityId = "42" }: ToolUp.Platform.EntityLockRef)

                agree (
                    {
                        From = Some(DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc))
                        To = None
                        EventType = Some "login"
                        Actor = None
                        Cursor = Some "c"
                        PageSize = 50
                    }
                    : ToolUp.Platform.AuditTrailQuery
                )

                agree (ToolUp.Platform.WireProvenanceRef.FactRef "f1")

                agree (
                    {
                        Root = ToolUp.Platform.WireProvenanceRef.ResultRef "r"
                        Direction = ToolUp.Platform.WireProvenanceDirection.Downstream
                        Depth = 3
                    }
                    : ToolUp.Platform.WireProvenanceChainRequest
                )

                agree (
                    {
                        TeamId = "t"
                        Role = ToolUp.Platform.TeamRole.Member
                        ExpiresIn = Some(TimeSpan.FromMinutes 90.0)
                        EmailHint = None
                        MaxUses = Some 3
                    }
                    : ToolUp.Platform.TeamInviteIssueRequest
                )

                agree ({ ModuleId = "home"; Pinned = true }: ToolUp.Platform.PinRequest)

                agree ({ Name = "n"; InitialOwnerUserId = "u" }: ToolUp.Platform.CreateTeamRequest)

                JsonDecoders.resetForTests ()
        ]
    ]