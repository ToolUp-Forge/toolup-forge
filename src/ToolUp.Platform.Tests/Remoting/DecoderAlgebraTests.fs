module ToolUp.Platform.Tests.Remoting.DecoderAlgebraTests

open System
open System.IO
open System.Reflection
open Expecto
open ToolUp.Remoting
open ToolUp.Remoting.MsgPack
open ToolUp.Platform.Tests.Remoting.WireCorpus

// ─── Phase 785 — the reflection reader as oracle ─────────────────────
//
// The algebra is an OPT-IN replacement for a decoder that already
// works, which makes "does it agree with the one it replaces" the
// question worth asking and "does it decode something" the question
// worth not asking. So every arm here is DIFFERENTIAL: the Phase 784
// corpus supplies the payload, the reflection reader supplies the
// oracle answer, and the algebra has to match it — on the accept path
// by producing the same value at the same runtime type, and on the
// refuse path by refusing whatever the oracle refused.
//
// **The refuse path is where the two are allowed to differ, and that
// difference is the point of the phase.** Phase 784 measured that the
// MsgPack wire refuses NONE of its ten mutation classes through the
// reflection reader — each escapes as a raw BCL exception or, worse, as
// an accepted value read out of a payload that does not encode one. So
// the refuse arm does not assert equality; it asserts a DECLARED table
// of per-mutation outcomes, exactly as `WireCorpus.mutations` does, and
// that table going red is how a gained or lost refusal announces
// itself. The improvements are enumerated there rather than summarised,
// because a summary cannot go red.
//
// **The go-red case is committed.** `silentlyWidening` below is a
// decoder written the way the reflection reader used to behave — it
// reads at a wider width and casts down — and a case asserts that the
// oracle comparison CATCHES it. A differential harness that has never
// been shown to fail is a harness that agrees with whatever it is
// shown; this is the falsifier for the whole pack.

// ─── Decoders for the corpus's own types ─────────────────────────────
//
// Written here rather than shipped in `PlatformDecoders` because the
// corpus types are test types. They double as the worked example the
// Phase 785 doc page points at: this is precisely what a consumer
// writes to put one of its own records on the algebra path.

/// A union whose every case carries no fields. It is STILL written as
/// `[tag]` — the writer emits a case-name string only for a union
/// attributed `[<StringEnum>]`, which this is not — so `union` with
/// `case0` arms is the right combinator and `stringEnum` would refuse
/// every payload.
let private priority: Decoder<Priority> =
    Decode.union "Priority" (function
        | 0 -> Some(Decode.case0 Low)
        | 1 -> Some(Decode.case0 Normal)
        | 2 -> Some(Decode.case0 High)
        | _ -> None)

let private address: Decoder<Address> =
    Decode.succeed (fun line1 postcode country -> {
        Line1 = line1
        Postcode = postcode
        Country = country
    })
    |> Decode.apply (Decode.field "Line1" 0 Decode.asString)
    |> Decode.apply (Decode.field "Postcode" 1 Decode.asString)
    |> Decode.apply (Decode.field "Country" 2 Decode.asString)

/// A union carrying zero, one and several fields — the three wire
/// shapes `Write.writeUnion` emits, all in one type.
let private outcome: Decoder<Outcome> =
    Decode.union "Outcome" (function
        // `Outcome.Accepted` is qualified because `RefusalOutcome`
        // (declared later in `WireCorpus`) also has an `Accepted` case,
        // and F#'s last-declaration-wins resolution picks that one.
        | 0 ->
            Some(
                Decode.payload (
                    Decode.succeed (fun id at -> Outcome.Accepted(id, at))
                    |> Decode.apply (Decode.field "id" 0 Decode.asGuid)
                    |> Decode.apply (Decode.field "at" 1 Decode.asDateTimeOffset)
                )
            )
        | 1 -> Some(Decode.payload (Decode.asString |> Decode.map Outcome.Rejected))
        | 2 -> Some(Decode.case0 Outcome.Pending)
        | _ -> None)

let private consignment: Decoder<Consignment> =
    Decode.succeed (fun reference origin destination pri outc weights labels -> {
        Reference = reference
        Origin = origin
        Destination = destination
        Priority = pri
        Outcome = outc
        Weights = weights
        Labels = labels
    })
    |> Decode.apply (Decode.field "Reference" 0 Decode.asString)
    |> Decode.apply (Decode.field "Origin" 1 address)
    |> Decode.apply (Decode.field "Destination" 2 address)
    |> Decode.apply (Decode.field "Priority" 3 priority)
    |> Decode.apply (Decode.field "Outcome" 4 outcome)
    |> Decode.apply (Decode.field "Weights" 5 (Decode.list Decode.asFloat))
    |> Decode.apply (Decode.field "Labels" 6 (Decode.asSet Decode.asString))

/// The go-red decoder: an `int` decoder that reads at 64 bits and casts
/// down, which is exactly what `interpretIntegerAs` did before Phase
/// 786. Registered for nothing — it exists only so the differential
/// harness can be shown to fail.
let private silentlyWidening: Decoder<int> = Decode.asInt64 |> Decode.map int

// ─── The covered set ─────────────────────────────────────────────────

let private erase (decoder: Decoder<'T>) : Value -> Result<obj, DecodeError> =
    fun value -> decoder value |> Result.map box

let private entry<'T> (decoder: Decoder<'T>) : Type * (Value -> Result<obj, DecodeError>) = typeof<'T>, erase decoder

/// Every corpus type the algebra covers, and nothing else.
///
/// **`DateOnly`, `TimeOnly` and the two records holding one are
/// deliberately absent.** Phase 784 recorded that the Fable MsgPack
/// reader refuses both types outright — its `interpretIntegerAs` carries
/// an EMPTY `#if NET6_0_OR_GREATER` block where the .NET arm handles
/// them — so a primitive for them would decode on one host and not the
/// other, and this file ships a cross-host algebra or it ships nothing.
/// Closing that divergence is an emitter/host question rather than a
/// combinator one; the reflection path keeps both types working on .NET
/// exactly as before.
let private covered: (Type * (Value -> Result<obj, DecodeError>)) list = [
    entry Decode.asBool
    entry Decode.asInt32
    entry Decode.asString
    entry Decode.asChar
    entry Decode.asByte
    entry Decode.asSByte
    entry Decode.asInt16
    entry Decode.asUInt16
    entry Decode.asUInt32
    entry Decode.asInt64
    entry Decode.asUInt64
    entry Decode.asFloat
    entry Decode.asFloat32
    entry Decode.asDecimal
    entry Decode.asDateTime
    entry Decode.asDateTimeOffset
    entry Decode.asTimeSpan
    entry Decode.asGuid
    entry Decode.asBytes
    entry (Decode.option Decode.asInt32)
    entry (Decode.option Decode.asString)
    entry (Decode.option address)
    entry (Decode.list Decode.asInt32)
    entry (Decode.list address)
    entry (Decode.array Decode.asString)
    entry (Decode.asMap Decode.asString Decode.asInt32)
    entry (Decode.asMap Decode.asInt32 Decode.asString)
    entry (Decode.asSet Decode.asString)
    entry (Decode.asSet Decode.asInt32)
    entry (
        Decode.succeed (fun a b -> (a, b))
        |> Decode.apply (Decode.index 0 Decode.asInt32)
        |> Decode.apply (Decode.index 1 Decode.asString)
    )
    entry (
        Decode.succeed (fun a b c -> (a, b, c))
        |> Decode.apply (Decode.index 0 Decode.asInt32)
        |> Decode.apply (Decode.index 1 Decode.asString)
        |> Decode.apply (Decode.index 2 Decode.asBool)
    )
    entry priority
    entry outcome
    entry address
    entry consignment
]

let private tryAlgebra (target: Type) =
    covered
    |> List.tryPick (fun (t, decoder) -> if t = target then Some decoder else None)

/// Every pinned case the algebra claims. Deliberately computed from the
/// corpus rather than listed, so a case added to Phase 784's corpus at a
/// type this file covers joins this pack with no edit here.
let private coveredCases =
    pinnedCases |> List.filter (fun c -> (tryAlgebra c.ClrType).IsSome)

// ─── Running the two paths ───────────────────────────────────────────

/// The algebra path, end to end: one bytes-to-`Value` pass, then the
/// registered decoder. Never throws — the classification IS the
/// measurement, the discipline `WireCorpus.classifyMsgPack` established.
let private algebraDecode (target: Type) (bytes: byte[]) : Result<obj, DecodeError> =
    match tryAlgebra target with
    | None -> Error(DecodeError.create "an algebra decoder" (sprintf "no decoder registered for %s" target.FullName))
    | Some decoder -> Read.Reader(bytes).TryReadValue() |> Result.bind decoder

/// The same classification `WireCorpus.classifyMsgPack` applies to the
/// reflection reader, applied to the algebra path — so the refuse arm
/// below compares like with like.
let private classifyAlgebra (target: Type) (bytes: byte[]) : RefusalOutcome * string =
    try
        match algebraDecode target bytes with
        | Ok value -> Accepted(sprintf "%A" value), "Ok"
        | Error e -> Refused, DecodeError.render e
    with ex ->
        ThrewUnnamed(ex.GetType().Name), ex.Message

// ─── The refuse-path declaration ─────────────────────────────────────

/// What the ALGEBRA does with each Phase 784 mutation whose target type
/// it covers, measured 2026-09-13, beside what the reflection reader
/// does with the same bytes.
///
/// Declared rather than asserted-as-equal because the two paths are
/// EXPECTED to differ: the whole reason this phase exists is that the
/// reflection path's answer to a malformed payload is usually a BCL
/// exception or an accepted value. A row moving is the signal — if a
/// refusal was gained, record it here, and if one was lost, a guard has
/// regressed.
let private algebraOutcomes: (string * RefusalOutcome) list = [
    "wrong-tag-nil-for-record", Refused
    "wrong-tag-bool-for-string", Refused
    "truncated-record-body", Refused
    "truncated-string-header", Refused
    "truncated-empty-payload", Refused
    "wrong-width-string-into-int", Refused
    // A record written with FEWER elements than it has fields is a
    // different shape, and `field` refuses it by name — where the
    // reflection reader reads field 3 off the end of a two-element array
    // and escapes as an IndexOutOfRangeException.
    "missing-field-record", Refused
    // An EXTRA trailing element is ACCEPTED, deliberately and in
    // agreement with the reflection reader. A record decoder reads the
    // positions it declares and ignores what follows, which is what
    // keeps an additive wire change non-breaking: a server that grows a
    // field must not break every client compiled against the older
    // record. Refusing it would buy strictness at the cost of the one
    // evolution property this wire has, so the doc page's not-claimed
    // list says so rather than the combinator pretending otherwise.
    "extra-field-record",
    Accepted "a trailing element past the declared fields is ignored, so an additive wire change stays non-breaking"
    // The one mutation the algebra cannot refuse either, and Phase 786
    // explained why it is not about the decoder: `writeInt64` compacts
    // `2147483648L` into bytes that ARE a well-formed `int32
    // -2147483648`, the shape `writeDecimal`'s sign word travels in. The
    // value model faithfully carries `Int(-2147483648, Bits32)`, and a
    // 32-bit source into a 32-bit target is admitted by Phase 786's
    // second rule — correctly, because refusing it would refuse every
    // negative decimal the corpus pins. Closing this needs the EMITTER,
    // not the decode algebra, and the doc page's not-claimed list says
    // so.
    "wrong-width-int64-into-int32", Accepted "the compacted encoding is a well-formed int32 -2147483648"
]

// ─── The IL pin ──────────────────────────────────────────────────────

/// Every type the `Decode` module compiles to — the module type itself
/// plus the closure classes F# emits for its lambdas, which is where the
/// combinator bodies actually live.
let private decodeModuleTypes () =
    let assembly = typeof<DecodeError>.Assembly
    let root = "ToolUp.Remoting.Decode"

    assembly.GetTypes()
    |> Array.filter (fun t ->
        let name = t.FullName

        not (isNull name)
        && (name = root || name.StartsWith(root + "+", StringComparison.Ordinal)))

/// The members a method body CALLS, read off its IL.
///
/// Approximate in one direction only, and the direction is the safe one:
/// it scans for the three call opcodes and resolves the four bytes that
/// follow each as a metadata token, so it can report a member the method
/// does not really call (a byte window that happens to resolve) but
/// cannot MISS one it does. For an absence check that is the right
/// approximation — a false positive names a member and fails loudly, a
/// false negative would pass silently.
let private calledMembers (method: MethodBase) : string list =
    match method.GetMethodBody() with
    | null -> []
    | body ->
        let il = body.GetILAsByteArray()

        if isNull il then
            []
        else
            [
                for i in 0 .. il.Length - 5 do
                    // call = 0x28, callvirt = 0x6F, newobj = 0x73.
                    if il.[i] = 0x28uy || il.[i] = 0x6Fuy || il.[i] = 0x73uy then
                        let token = BitConverter.ToInt32(il, i + 1)

                        let resolved =
                            try
                                match method.Module.ResolveMember(token) with
                                | null -> None
                                | m ->
                                    Some(
                                        sprintf
                                            "%s.%s"
                                            (if isNull m.DeclaringType then
                                                 ""
                                             else
                                                 m.DeclaringType.FullName)
                                            m.Name
                                    )
                            with _ ->
                                None

                        match resolved with
                        | Some name -> yield name
                        | None -> ()
            ]

// ─── The pack ────────────────────────────────────────────────────────

let tests =
    testList "Phase 785 — the closed decoder algebra" [

        testList "the algebra agrees with the reflection oracle" [
            testCase "the covered set is not vacuous"
            <| fun () ->
                Expect.isGreaterThan
                    (List.length coveredCases)
                    40
                    (sprintf
                        "only %d of %d pinned case(s) are covered by an algebra decoder, so the differential below says very little"
                        (List.length coveredCases)
                        (List.length pinnedCases))

            testCase "every covered case decodes to the declared value through the algebra"
            <| fun () ->
                for c in coveredCases do
                    let bytes = c.WriteMsgPack()

                    match algebraDecode c.ClrType bytes with
                    | Error e ->
                        failtestf
                            "`%s` refused through the algebra: %s\n  bytes: %s"
                            c.Name
                            (DecodeError.render e)
                            (describeBytes bytes)
                    | Ok decoded ->
                        match c.Compare decoded with
                        | Ok() -> ()
                        | Error problem -> failtestf "`%s` decoded through the algebra: %s" c.Name problem

            testCase "the algebra and the reflection reader produce the same value"
            <| fun () ->
                for c in coveredCases do
                    let bytes = c.WriteMsgPack()
                    let viaReflection = Read.Reader(bytes).TryRead c.ClrType
                    let viaAlgebra = algebraDecode c.ClrType bytes

                    match viaReflection, viaAlgebra with
                    | Ok fromReflection, Ok fromAlgebra ->
                        // Runtime type first, then value — the narrowing
                        // this pack exists to catch produces a value that
                        // is equal-looking at every representation except
                        // its type, which is the lesson `WireCorpus`
                        // recorded when it put `typeMismatch` ahead of
                        // equality.
                        //
                        // `None` and `unit` box to `null` in F#, so the
                        // runtime-type comparison is guarded: the
                        // OptionFamily cases are precisely the ones whose
                        // decoded value has no runtime type to read, and
                        // reaching for one would fault on the class the
                        // corpus exists to cover.
                        if not (isNull fromReflection) && not (isNull fromAlgebra) then
                            Expect.equal
                                (fromAlgebra.GetType().FullName)
                                (fromReflection.GetType().FullName)
                                (sprintf "`%s`: the two paths landed on different runtime types" c.Name)
                        else
                            Expect.equal
                                (isNull fromAlgebra)
                                (isNull fromReflection)
                                (sprintf "`%s`: one path decoded to null and the other did not" c.Name)

                        Expect.equal
                            (sprintf "%A" fromAlgebra)
                            (sprintf "%A" fromReflection)
                            (sprintf "`%s`: the two paths decoded different values" c.Name)
                    | Error e, Ok _ ->
                        failtestf
                            "`%s`: the algebra refused a payload the reflection reader accepts: %s"
                            c.Name
                            (DecodeError.render e)
                    | Ok _, Error e ->
                        failtestf
                            "`%s`: the reflection reader refused a payload the algebra accepts: %s"
                            c.Name
                            (DecodeError.render e)
                    | Error _, Error _ -> ()

            testCase "the differential CATCHES a decoder that silently widens — the go-red case"
            <| fun () ->
                // `2147483648L` at `int64` is a legitimate corpus value.
                // A decoder that reads it at 64 bits and casts to `int`
                // produces `-2147483648` — equal-looking, wrong, and
                // exactly what the pre-786 reader did. The comparison
                // below is the one the arm above performs; it must fail.
                let declared = both WireClass.NumericWidth "probe" 2147483648L
                let bytes = declared.WriteMsgPack()

                let widened = Read.Reader(bytes).TryReadValue() |> Result.bind silentlyWidening

                let honest = Read.Reader(bytes).TryReadValue() |> Result.bind Decode.asInt64

                match widened, honest with
                | Ok widenedValue, Ok honestValue ->
                    Expect.notEqual
                        (sprintf "%A" (box widenedValue))
                        (sprintf "%A" (box honestValue))
                        "the silently-widening decoder produced the same value as the honest one, so the differential above could not catch it and this pack proves nothing"
                | _ ->
                    failtest
                        "the go-red probe did not produce two decodes to compare; it can no longer demonstrate that the differential fails on a widening decoder"

            testCase "`asInt32` refuses the widening the go-red decoder performs"
            <| fun () ->
                let bytes =
                    (both WireClass.NumericWidth "probe" 9223372036854775807L).WriteMsgPack()

                match Read.Reader(bytes).TryReadValue() |> Result.bind Decode.asInt32 with
                | Ok value -> failtestf "`asInt32` accepted an Int64 payload and produced %d" value
                | Error e ->
                    Expect.stringContains
                        (DecodeError.render e)
                        "Int32"
                        "the refusal does not name the target type it refused for"
        ]

        testList "the round-trip law, over the corpus" [
            // 785.E — `encode (decode bytes) = bytes`, asserted as its two
            // halves in one case per fixture: the algebra decodes the
            // emitted bytes to the declared value, and the emitter emits
            // those bytes for that value. Composed, that is the law. It is
            // NOT asserted by re-serialising the decoded `obj`, because
            // recovering the static type to do so would put reflection
            // back into the middle of the arm meant to demonstrate its
            // absence.
            testCase "decode (encode value) = value, and encode value is stable"
            <| fun () ->
                for c in coveredCases do
                    let first = c.WriteMsgPack()
                    let second = c.WriteMsgPack()

                    Expect.sequenceEqual
                        second
                        first
                        (sprintf
                            "`%s`: the emitter is not deterministic, so no round-trip law over it means anything"
                            c.Name)

                    match algebraDecode c.ClrType first with
                    | Error e -> failtestf "`%s`: decode (encode value) refused: %s" c.Name (DecodeError.render e)
                    | Ok decoded ->
                        match c.Compare decoded with
                        | Ok() -> ()
                        | Error problem -> failtestf "`%s`: decode (encode value) <> value — %s" c.Name problem

            testCase "the committed fixtures decode through the algebra too"
            <| fun () ->
                // The corpus's bytes on disk, not the emitter's output —
                // so a writer change that moved BOTH halves together
                // cannot make this arm agree with itself.
                let directory = corpusDirectory ()

                if Directory.Exists directory then
                    for c in coveredCases do
                        let path = msgPackFixturePath c

                        if File.Exists path then
                            let committed = File.ReadAllBytes path

                            match algebraDecode c.ClrType committed with
                            | Error e ->
                                failtestf
                                    "`%s`: the committed fixture refused through the algebra: %s"
                                    c.Name
                                    (DecodeError.render e)
                            | Ok decoded ->
                                match c.Compare decoded with
                                | Ok() -> ()
                                | Error problem ->
                                    failtestf "`%s`: the committed fixture decoded wrong — %s" c.Name problem
                else
                    failtestf "the remoting corpus directory is absent at `%s`, so this arm measured nothing" directory
        ]

        testList "the refuse path" [
            testCase "every declared algebra outcome holds"
            <| fun () ->
                for name, expected in algebraOutcomes do
                    let mutation = mutations () |> List.tryFind (fun m -> m.Name = name)

                    match mutation with
                    | None ->
                        failtestf
                            "mutation `%s` is declared here and no longer exists in the Phase 784 corpus; the declaration is stale"
                            name
                    | Some m ->
                        match m.MsgPack with
                        | None ->
                            failtestf "mutation `%s` carries no MsgPack payload, so it cannot be classified here" name
                        | Some bytes ->
                            let measured, detail = classifyAlgebra m.Target bytes

                            let sameClass =
                                match expected, measured with
                                | Refused, Refused -> true
                                | Accepted _, Accepted _ -> true
                                | ThrewUnnamed _, ThrewUnnamed _ -> true
                                | _ -> false

                            Expect.isTrue
                                sameClass
                                (sprintf
                                    "the ALGEBRA's behaviour on mutation `%s` has changed class.\n  declared: %A\n  measured: %A (%s)\nIf a refusal was GAINED, record it here — that is this list doing its job. If one was LOST, a combinator has regressed."
                                    name
                                    expected
                                    measured
                                    detail)

            testCase "the algebra refuses strictly more than the reflection reader"
            <| fun () ->
                // The phase's actual claim, measured rather than asserted:
                // every mutation the reflection reader refuses, the
                // algebra also refuses, and at least one it does not.
                // Without the second half this case would pass for a
                // decoder that changed nothing.
                let rows = [
                    for name, _ in algebraOutcomes do
                        match mutations () |> List.tryFind (fun m -> m.Name = name) with
                        | Some m ->
                            match m.MsgPack with
                            | Some bytes ->
                                let viaAlgebra, _ = classifyAlgebra m.Target bytes
                                let viaReflection, _ = classifyMsgPack m.Target bytes
                                yield name, viaReflection, viaAlgebra
                            | None -> ()
                        | None -> ()
                ]

                let lost =
                    rows
                    |> List.filter (fun (_, reflection, algebra) ->
                        reflection = Refused
                        && (match algebra with
                            | Refused -> false
                            | _ -> true))

                Expect.isEmpty
                    (lost |> List.map (fun (name, _, _) -> name))
                    "the algebra accepts a mutation the reflection reader refuses — a refusal has been LOST"

                let gained =
                    rows
                    |> List.filter (fun (_, reflection, algebra) ->
                        algebra = Refused
                        && (match reflection with
                            | Refused -> false
                            | _ -> true))

                Expect.isNonEmpty
                    gained
                    "the algebra refuses nothing the reflection reader did not already refuse, so this phase bought no refusals at all"
        ]

        testList "the combinators are total, pure and reflection-free" [
            testCase "no combinator throws on any shape in the value model"
            <| fun () ->
                // Totality, measured behaviourally: every combinator is
                // applied to one value of every case in the closed model,
                // and none may escape with an exception. An absence check
                // over IL says what the code does not CALL; this says what
                // the code does not DO.
                let shapes = [
                    Value.Nil
                    Value.Bool true
                    Value.Int(-1L, IntegerWidth.Fixnum)
                    Value.UInt(9223372036854775808UL, IntegerWidth.Bits64)
                    Value.Float(1.5, FloatWidth.Double)
                    Value.Str "x"
                    Value.Bin [| 1uy |]
                    Value.Arr [ Value.Nil; Value.Str "x" ]
                    Value.Map [ Value.Str "k", Value.Nil ]
                ]

                let probes: (string * (Value -> Result<obj, DecodeError>)) list = [
                    "asBool", erase Decode.asBool
                    "asUnit", erase Decode.asUnit
                    "asString", erase Decode.asString
                    "asChar", erase Decode.asChar
                    "asInt32", erase Decode.asInt32
                    "asInt64", erase Decode.asInt64
                    "asInt16", erase Decode.asInt16
                    "asByte", erase Decode.asByte
                    "asSByte", erase Decode.asSByte
                    "asUInt16", erase Decode.asUInt16
                    "asUInt32", erase Decode.asUInt32
                    "asUInt64", erase Decode.asUInt64
                    "asFloat", erase Decode.asFloat
                    "asFloat32", erase Decode.asFloat32
                    "asTimeSpan", erase Decode.asTimeSpan
                    "asBytes", erase Decode.asBytes
                    "asGuid", erase Decode.asGuid
                    "asDateTime", erase Decode.asDateTime
                    "asDateTimeOffset", erase Decode.asDateTimeOffset
                    "asDecimal", erase Decode.asDecimal
                    "items", erase Decode.items
                    "exactly 2", erase (Decode.exactly 2)
                    "index 0", erase (Decode.index 0 Decode.asString)
                    "field", erase (Decode.field "F" 0 Decode.asString)
                    "list", erase (Decode.list Decode.asString)
                    "array", erase (Decode.array Decode.asString)
                    "asSet", erase (Decode.asSet Decode.asString)
                    "entries", erase (Decode.entries Decode.asString Decode.asString)
                    "asMap", erase (Decode.asMap Decode.asString Decode.asString)
                    "option", erase (Decode.option Decode.asString)
                    "result", erase (Decode.result Decode.asString Decode.asString)
                    "stringEnum", erase (Decode.stringEnum "E" [ "a", 1 ])
                    "union", erase (Decode.union "U" (fun _ -> Some(Decode.case0 1)))
                    "address", erase address
                    "outcome", erase outcome
                    "consignment", erase consignment
                ]

                for name, probe in probes do
                    for shape in shapes do
                        try
                            probe shape |> ignore
                        with ex ->
                            failtestf
                                "`%s` threw a %s on %s — a combinator that can throw is not total"
                                name
                                (ex.GetType().Name)
                                (Value.describe shape)

            testCase "a combinator answers the same twice — purity, at the only granularity a test can see"
            <| fun () ->
                let value =
                    (pinnedCases |> List.find (fun c -> c.Name = "record-consignment")).WriteMsgPack()

                let once = algebraDecode typeof<Consignment> value
                let twice = algebraDecode typeof<Consignment> value

                Expect.equal
                    (sprintf "%A" twice)
                    (sprintf "%A" once)
                    "the same decoder over the same bytes answered differently"

            testCase "the `Decode` module's IL calls no reflection and no `failwith`"
            <| fun () ->
                let forbidden = [ "FSharp.Reflection"; "Microsoft.FSharp.Reflection"; "System.Reflection" ]

                let offenders =
                    [
                        for t in decodeModuleTypes () do
                            for m in
                                t.GetMethods(
                                    BindingFlags.Public
                                    ||| BindingFlags.NonPublic
                                    ||| BindingFlags.Instance
                                    ||| BindingFlags.Static
                                    ||| BindingFlags.DeclaredOnly
                                ) do
                                for called in calledMembers m do
                                    if
                                        forbidden
                                        |> List.exists (fun f -> called.StartsWith(f, StringComparison.Ordinal))
                                        || called.EndsWith(".FailWith", StringComparison.Ordinal)
                                        || called.EndsWith(".PrintFormatToStringThenFail", StringComparison.Ordinal)
                                    then
                                        yield sprintf "%s.%s -> %s" t.FullName m.Name called
                    ]
                    |> List.distinct

                Expect.isEmpty
                    offenders
                    "the `Decode` module's compiled bodies reach reflection or `failwith`; the totality claim rests on their absence"

            testCase "the IL pin can see a call at all — the falsifier"
            <| fun () ->
                // An absence check over an empty scan is vacuous, and the
                // scan's approximation makes that a live risk: if the
                // token resolution silently failed everywhere, the case
                // above would pass over nothing. This asserts the scan
                // finds SOMETHING in the same bodies.
                let seen = [
                    for t in decodeModuleTypes () do
                        for m in
                            t.GetMethods(
                                BindingFlags.Public
                                ||| BindingFlags.NonPublic
                                ||| BindingFlags.Instance
                                ||| BindingFlags.Static
                                ||| BindingFlags.DeclaredOnly
                            ) do
                            yield! calledMembers m
                ]

                Expect.isNonEmpty
                    seen
                    "the IL scan resolved no called member anywhere in the `Decode` module, so the absence check above measured nothing"
        ]

        testList "the platform's named decoder set" [
            testCase "every covered wire type is actually registered"
            <| fun () ->
                PlatformDecoders.registerAll ()
                let registered = RemotingDecoders.registered () |> Set.ofList

                let missing =
                    PlatformDecoders.covered
                    |> List.filter (fun name -> not (registered.Contains name))

                Expect.isEmpty
                    missing
                    "a decoder is declared in `PlatformDecoders.covered` and not registered by `registerAll` — it would never be consulted"

            testCase "every API record the facet classifies names only registered types"
            <| fun () ->
                PlatformDecoders.registerAll ()
                let registered = RemotingDecoders.registered () |> Set.ofList

                for record, types in PlatformDecoders.coveredApiRecords do
                    let missing = types |> List.filter (fun name -> not (registered.Contains name))

                    Expect.isEmpty
                        missing
                        (sprintf "API record `%s` names wire types no decoder is registered for" record)

            testCase "the platform decoders round-trip their own records"
            <| fun () ->
                PlatformDecoders.registerAll ()

                let probe: ToolUp.Platform.HealthSnapshot = {
                    GeneratedAt = DateTime(2026, 9, 13, 8, 30, 0, DateTimeKind.Utc)
                    Probes = [
                        {
                            Name = "blob_storage"
                            Kind = "Readiness"
                            TimeoutMs = 5000
                            Status = "Healthy"
                            Message = "ok"
                            ElapsedMs = 12L
                        }
                    ]
                }

                let bytes =
                    let serializer =
                        Write.makeSerializer<Result<ToolUp.Platform.HealthSnapshot, string>> ()

                    use buffer = new MemoryStream()
                    serializer.Invoke(Ok probe, buffer)
                    buffer.ToArray()

                match RemotingDecoders.tryGet typeof<Result<ToolUp.Platform.HealthSnapshot, string>> with
                | None ->
                    failtest
                        "`Result<HealthSnapshot, string>` is not registered, so the client branch would never fire for it"
                | Some decoder ->
                    match Read.Reader(bytes).TryReadValue() |> Result.bind decoder with
                    | Error e -> failtestf "the platform decoder refused its own record: %s" (DecodeError.render e)
                    | Ok decoded ->
                        Expect.equal
                            (sprintf "%A" decoded)
                            (sprintf "%A" (box (Ok probe: Result<ToolUp.Platform.HealthSnapshot, string>)))
                            "the platform decoder did not reproduce the value the writer emitted"
        ]
    ]