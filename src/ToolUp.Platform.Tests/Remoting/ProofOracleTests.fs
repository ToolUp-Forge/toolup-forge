module ToolUp.Platform.Tests.Remoting.ProofOracleTests

open System
open System.Numerics
open Expecto
open ToolUp.Remoting
open ToolUp.Remoting.MsgPack
open ToolUp.Platform.Tests.Remoting.WireCorpus

// ─── Phase 787 — the proved model as oracle ──────────────────────────
//
// `proofs/RemotingDecode.fst` models the Phase 785 combinator algebra
// clause for clause and proves it total: given a parsed value, no
// decoder built from the combinators can diverge, throw, or reach a
// state that is neither an accept nor a named refusal — and which of
// the two is characterised structurally. `proofs/check.ps1` extracts
// that model to F# and byte-compares the result against the committed
// `proofs/oracle/RemotingDecode.fs`, which this pack references and
// runs.
//
// **A proof is about the MODEL, and this pack is the only thing that
// says the model is about the code.** That gap is the whole reason the
// extraction exists: a hand-written F* module can drift from the F# it
// describes silently and for months, and no amount of proving fixes a
// model of the wrong thing. So every arm below is DIFFERENTIAL in the
// same shape Phase 785's pack established — the Phase 784 corpus
// supplies the payload, production and the extracted model each decode
// it, and the two answers must agree on OUTCOME CLASS, DECODED VALUE
// and REFUSAL MESSAGE at once. The messages are compared because they
// are the cheapest place drift shows: a combinator whose refusal text
// moved has almost certainly had its logic moved too.
//
// **The go-red case is committed, and it is the width rule.** The blind
// bridge below hands the model a value model that has FORGOTTEN the
// source width class — every integer declared 64-bit, which is what a
// naive bridge would do — and a case asserts the differential CATCHES
// it. The disagreement it produces is not incidental: Phase 786
// established that `writeDecimal`'s four words ride
// `write32bitNumber`, so a negative word arrives as `uint32
// 0xFFFFFFFF`, and the width rule's "a source no wider than its target
// always survives" clause is the only reason it decodes. Forget the
// width and every negative decimal in the corpus refuses. A
// differential that has never been shown to fail agrees with whatever
// it is shown.

/// The model instantiated at this host's payload types. The `bin`
/// payload and the float payload are the model's two opaque type
/// parameters — nothing in it inspects either — so the extracted
/// generic code carries the host's own `byte[]` and `float` through
/// untouched, and a decoded value can be compared against production's
/// directly rather than through a rendering.
type private ModelValue = RemotingDecode.value<byte[], float>

type private ModelDecoder<'a> = ModelValue -> RemotingDecode.outcome<'a>

/// The model's applicative step, bound locally so the pipelines below
/// read exactly as their production counterparts do.
let inline private (|>>) (fn: ModelDecoder<'a -> 'b>) (arg: ModelDecoder<'a>) : ModelDecoder<'b> =
    RemotingDecode.op_Bar_Greater_Greater fn arg

/// The model's `nat` positions and arities.
let private ix (n: int) : BigInteger = BigInteger n

// ─── The bridge ──────────────────────────────────────────────────────

let private modelWidth (width: IntegerWidth) : RemotingDecode.integer_width =
    match width with
    | IntegerWidth.Fixnum -> RemotingDecode.Fixnum
    | IntegerWidth.Bits8 -> RemotingDecode.Bits8
    | IntegerWidth.Bits16 -> RemotingDecode.Bits16
    | IntegerWidth.Bits32 -> RemotingDecode.Bits32
    | IntegerWidth.Bits64 -> RemotingDecode.Bits64

let private modelFloatWidth (width: FloatWidth) : RemotingDecode.float_width =
    match width with
    | FloatWidth.Single -> RemotingDecode.Single
    | FloatWidth.Double -> RemotingDecode.Double

/// `Value` to the model's `value`, faithfully.
///
/// Three fields are MEASURED here rather than by the model, and each is
/// deliberate: a string's `Length`, a `bin`'s `Length`, and a float's
/// `string` rendering. The model's own header argues why — recomputing
/// any of them in F* would be a second implementation of something the
/// host already does, free to disagree with it. `String.Length` counts
/// UTF-16 code units and `FStar.String.length` counts F* characters, so
/// for strings that disagreement is real rather than theoretical.
let rec private bridge (value: Value) : ModelValue =
    match value with
    | Value.Nil -> RemotingDecode.VNil
    | Value.Bool b -> RemotingDecode.VBool b
    | Value.Int(n, width) -> RemotingDecode.VInt(BigInteger n, modelWidth width)
    | Value.UInt(n, width) -> RemotingDecode.VUInt(BigInteger n, modelWidth width)
    | Value.Float(n, width) -> RemotingDecode.VFloat(n, modelFloatWidth width, string n)
    | Value.Str text -> RemotingDecode.VStr(text, ix text.Length)
    | Value.Bin bytes -> RemotingDecode.VBin(bytes, ix bytes.Length)
    | Value.Arr items -> RemotingDecode.VArr(items |> List.map bridge)
    | Value.Map entries ->
        RemotingDecode.VMap(entries |> List.map (fun (k, v) -> RemotingDecode.Pair(bridge k, bridge v)))

/// **The go-red bridge.** Identical but for one thing: every integer
/// arrives declared 64-bit, so the model can no longer see the width
/// the format byte actually carried. It is the bridge a careless
/// implementer writes — the payload survives, only the width class is
/// lost — and losing it is exactly what makes Phase 786's second clause
/// unreachable.
let rec private blindBridge (value: Value) : ModelValue =
    match value with
    | Value.Int(n, _) -> RemotingDecode.VInt(BigInteger n, RemotingDecode.Bits64)
    | Value.UInt(n, _) -> RemotingDecode.VUInt(BigInteger n, RemotingDecode.Bits64)
    | Value.Arr items -> RemotingDecode.VArr(items |> List.map blindBridge)
    | Value.Map entries ->
        RemotingDecode.VMap(
            entries
            |> List.map (fun (k, v) -> RemotingDecode.Pair(blindBridge k, blindBridge v))
        )
    | other -> bridge other

let private toDecodeError (error: RemotingDecode.refusal) : DecodeError = {
    Path = error.path
    Expected = error.expected
    Found = error.found
}

let private toResult (outcome: RemotingDecode.outcome<'T>) : Result<'T, DecodeError> =
    match outcome with
    | RemotingDecode.Accepted value -> Ok value
    | RemotingDecode.Refused error -> Error(toDecodeError error)

// ─── The host's casts ────────────────────────────────────────────────
//
// The model's integer arms yield the MATHEMATICAL value; production's
// cast down to the target type. That split is faithful rather than
// convenient, and the model proves why: an accept is exactly the width
// rule holding, so the cast is reached only where the value carries no
// more information than the target holds — see
// `lemma_int32_accept_is_32_bit_information`. What the cast does there
// is REINTERPRET, never truncate, which is why these helpers take the
// low bits rather than calling `BigInteger`'s checked conversions
// (`int 4294967295I` throws; `int32 4294967295UL` is -1, and -1 is what
// production produces for a negative decimal word).

let private low (bits: int) (n: BigInteger) : BigInteger =
    let modulus = BigInteger.Pow(BigInteger 2, bits)
    ((n % modulus) + modulus) % modulus

let private toInt32 (n: BigInteger) : int = int32 (uint32 (low 32 n))
let private toUInt32 (n: BigInteger) : uint32 = uint32 (low 32 n)
let private toInt64 (n: BigInteger) : int64 = int64 (uint64 (low 64 n))
let private toUInt64 (n: BigInteger) : uint64 = uint64 (low 64 n)
let private toInt16 (n: BigInteger) : int16 = int16 (uint16 (low 16 n))
let private toUInt16 (n: BigInteger) : uint16 = uint16 (low 16 n)
let private toSByte (n: BigInteger) : sbyte = sbyte (byte (low 8 n))
let private toByte (n: BigInteger) : byte = byte (low 8 n)

let private toOption (o: RemotingDecode.opt<'a>) : 'a option =
    match o with
    | RemotingDecode.OSome item -> Some item
    | RemotingDecode.ONone -> None

// ─── The model's decoders, mirroring the production ones ─────────────
//
// Each is the same pipeline written against the extracted combinators.
// Ten of them take a `()` first: F*'s F# backend thunks a definition
// the value restriction would otherwise reject.

let private mBool: ModelDecoder<bool> = RemotingDecode.as_bool
let private mString: ModelDecoder<string> = RemotingDecode.as_string

let private mChar: ModelDecoder<char> =
    RemotingDecode.as_char_with (fun (s: string) -> s.[0])

let private mInt32: ModelDecoder<int> =
    RemotingDecode.map toInt32 (RemotingDecode.as_int32 ())

let private mInt64: ModelDecoder<int64> =
    RemotingDecode.map toInt64 (RemotingDecode.as_int64 ())

let private mInt16: ModelDecoder<int16> =
    RemotingDecode.map toInt16 (RemotingDecode.as_int16 ())

let private mSByte: ModelDecoder<sbyte> =
    RemotingDecode.map toSByte (RemotingDecode.as_sbyte ())

let private mByte: ModelDecoder<byte> =
    RemotingDecode.map toByte (RemotingDecode.as_byte ())

let private mUInt16: ModelDecoder<uint16> =
    RemotingDecode.map toUInt16 (RemotingDecode.as_uint16 ())

let private mUInt32: ModelDecoder<uint32> =
    RemotingDecode.map toUInt32 (RemotingDecode.as_uint32 ())

let private mUInt64: ModelDecoder<uint64> =
    RemotingDecode.map toUInt64 (RemotingDecode.as_uint64 ())

let private mFloat: ModelDecoder<float> = RemotingDecode.as_float

let private mFloat32: ModelDecoder<float32> =
    RemotingDecode.map float32 RemotingDecode.as_float32

let private mBytes: ModelDecoder<byte[]> = RemotingDecode.as_bytes

let private mGuid: ModelDecoder<Guid> =
    RemotingDecode.as_guid_with (fun (b: byte[]) -> Guid b)

let private mTimeSpan: ModelDecoder<TimeSpan> =
    RemotingDecode.as_time_span_with (fun n -> TimeSpan(toInt64 n))

let private mDateTime: ModelDecoder<DateTime> =
    RemotingDecode.as_date_time_with (fun ticks kind ->
        let kind =
            match toInt64 kind with
            | 1L -> DateTimeKind.Utc
            | 2L -> DateTimeKind.Local
            | _ -> DateTimeKind.Unspecified

        DateTime(toInt64 ticks, kind))

let private mDateTimeOffset: ModelDecoder<DateTimeOffset> =
    RemotingDecode.as_date_time_offset_with (fun ticks minutes ->
        DateTimeOffset(toInt64 ticks, TimeSpan.FromMinutes(float (toInt64 minutes))))

let private mDecimal: ModelDecoder<decimal> =
    RemotingDecode.as_decimal_with (fun lo mid hi flags ->
        Decimal [| toInt32 lo; toInt32 mid; toInt32 hi; toInt32 flags |])

let private mList (element: ModelDecoder<'a>) : ModelDecoder<'a list> = RemotingDecode.list_of element

let private mArray (element: ModelDecoder<'a>) : ModelDecoder<'a[]> =
    RemotingDecode.map List.toArray (RemotingDecode.list_of element)

let private mSet (element: ModelDecoder<'a>) : ModelDecoder<Set<'a>> =
    RemotingDecode.map Set.ofList (RemotingDecode.list_of element)

let private mMap (key: ModelDecoder<'k>) (entry: ModelDecoder<'v>) : ModelDecoder<Map<'k, 'v>> =
    RemotingDecode.map
        (fun pairs -> pairs |> List.map (fun (RemotingDecode.Pair(k, v)) -> k, v) |> Map.ofList)
        (RemotingDecode.entries_of key entry)

let private mOption (inner: ModelDecoder<'a>) : ModelDecoder<'a option> =
    RemotingDecode.map toOption (RemotingDecode.as_option inner)

let private mPriority: ModelDecoder<Priority> =
    RemotingDecode.union "Priority" (fun tag ->
        if tag = ix 0 then
            RemotingDecode.OSome(RemotingDecode.case0 Low)
        elif tag = ix 1 then
            RemotingDecode.OSome(RemotingDecode.case0 Normal)
        elif tag = ix 2 then
            RemotingDecode.OSome(RemotingDecode.case0 High)
        else
            RemotingDecode.ONone)

let private mAddress: ModelDecoder<Address> =
    RemotingDecode.succeed (fun line1 postcode country -> {
        Line1 = line1
        Postcode = postcode
        Country = country
    })
    |>> RemotingDecode.field "Line1" (ix 0) mString
    |>> RemotingDecode.field "Postcode" (ix 1) mString
    |>> RemotingDecode.field "Country" (ix 2) mString

let private mOutcome: ModelDecoder<Outcome> =
    RemotingDecode.union "Outcome" (fun tag ->
        if tag = ix 0 then
            RemotingDecode.OSome(
                RemotingDecode.payload (
                    RemotingDecode.succeed (fun id at -> Outcome.Accepted(id, at))
                    |>> RemotingDecode.field "id" (ix 0) mGuid
                    |>> RemotingDecode.field "at" (ix 1) mDateTimeOffset
                )
            )
        elif tag = ix 1 then
            RemotingDecode.OSome(RemotingDecode.payload (RemotingDecode.map Outcome.Rejected mString))
        elif tag = ix 2 then
            RemotingDecode.OSome(RemotingDecode.case0 Outcome.Pending)
        else
            RemotingDecode.ONone)

let private mConsignment: ModelDecoder<Consignment> =
    RemotingDecode.succeed (fun reference origin destination pri outc weights labels -> {
        Reference = reference
        Origin = origin
        Destination = destination
        Priority = pri
        Outcome = outc
        Weights = weights
        Labels = labels
    })
    |>> RemotingDecode.field "Reference" (ix 0) mString
    |>> RemotingDecode.field "Origin" (ix 1) mAddress
    |>> RemotingDecode.field "Destination" (ix 2) mAddress
    |>> RemotingDecode.field "Priority" (ix 3) mPriority
    |>> RemotingDecode.field "Outcome" (ix 4) mOutcome
    |>> RemotingDecode.field "Weights" (ix 5) (mList mFloat)
    |>> RemotingDecode.field "Labels" (ix 6) (mSet mString)

// ─── The production decoders, and the pairing ────────────────────────
//
// Written here rather than reached for across the file boundary: Phase
// 785's pack keeps its own set `private`, and a differential whose two
// sides came from one declaration would be comparing a thing with
// itself.

let private pPriority: Decoder<Priority> =
    Decode.union "Priority" (function
        | 0 -> Some(Decode.case0 Low)
        | 1 -> Some(Decode.case0 Normal)
        | 2 -> Some(Decode.case0 High)
        | _ -> None)

let private pAddress: Decoder<Address> =
    Decode.succeed (fun line1 postcode country -> {
        Line1 = line1
        Postcode = postcode
        Country = country
    })
    |> Decode.apply (Decode.field "Line1" 0 Decode.asString)
    |> Decode.apply (Decode.field "Postcode" 1 Decode.asString)
    |> Decode.apply (Decode.field "Country" 2 Decode.asString)

let private pOutcome: Decoder<Outcome> =
    Decode.union "Outcome" (function
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

let private pConsignment: Decoder<Consignment> =
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
    |> Decode.apply (Decode.field "Origin" 1 pAddress)
    |> Decode.apply (Decode.field "Destination" 2 pAddress)
    |> Decode.apply (Decode.field "Priority" 3 pPriority)
    |> Decode.apply (Decode.field "Outcome" 4 pOutcome)
    |> Decode.apply (Decode.field "Weights" 5 (Decode.list Decode.asFloat))
    |> Decode.apply (Decode.field "Labels" 6 (Decode.asSet Decode.asString))

/// One corpus type, decoded both ways. `Production` and `Model` are the
/// two halves whose agreement this pack asserts.
type private Paired = {
    ClrType: Type
    Production: Value -> Result<obj, DecodeError>
    Model: ModelValue -> Result<obj, DecodeError>
}

let private pair<'T> (production: Decoder<'T>) (model: ModelDecoder<'T>) : Paired = {
    ClrType = typeof<'T>
    Production = fun value -> production value |> Result.map box
    Model = fun value -> model value |> toResult |> Result.map box
}

/// Every corpus type the model covers. `DateOnly` and `TimeOnly` are
/// absent for the reason Phase 785's covered set records: the Fable
/// MsgPack reader refuses both outright, so neither has a production
/// combinator to be an oracle for.
let private paired: Paired list = [
    pair Decode.asBool mBool
    pair Decode.asString mString
    pair Decode.asChar mChar
    pair Decode.asInt32 mInt32
    pair Decode.asInt64 mInt64
    pair Decode.asInt16 mInt16
    pair Decode.asSByte mSByte
    pair Decode.asByte mByte
    pair Decode.asUInt16 mUInt16
    pair Decode.asUInt32 mUInt32
    pair Decode.asUInt64 mUInt64
    pair Decode.asFloat mFloat
    pair Decode.asFloat32 mFloat32
    pair Decode.asBytes mBytes
    pair Decode.asGuid mGuid
    pair Decode.asTimeSpan mTimeSpan
    pair Decode.asDateTime mDateTime
    pair Decode.asDateTimeOffset mDateTimeOffset
    pair Decode.asDecimal mDecimal
    pair (Decode.option Decode.asInt32) (mOption mInt32)
    pair (Decode.option Decode.asString) (mOption mString)
    pair (Decode.option pAddress) (mOption mAddress)
    pair (Decode.list Decode.asInt32) (mList mInt32)
    pair (Decode.list pAddress) (mList mAddress)
    pair (Decode.array Decode.asString) (mArray mString)
    pair (Decode.asSet Decode.asString) (mSet mString)
    pair (Decode.asSet Decode.asInt32) (mSet mInt32)
    pair (Decode.asMap Decode.asString Decode.asInt32) (mMap mString mInt32)
    pair (Decode.asMap Decode.asInt32 Decode.asString) (mMap mInt32 mString)
    pair pPriority mPriority
    pair pOutcome mOutcome
    pair pAddress mAddress
    pair pConsignment mConsignment
]

let private tryPaired (target: Type) =
    paired |> List.tryPick (fun p -> if p.ClrType = target then Some p else None)

/// Computed from the corpus rather than listed, so a case added to
/// Phase 784's corpus at a type this file covers joins this pack with
/// no edit here.
let private coveredCases =
    pinnedCases |> List.filter (fun c -> (tryPaired c.ClrType).IsSome)

// ─── Running the two, and comparing all three things at once ─────────

/// Outcome class, decoded value, decoded runtime type and refusal
/// message, in one string. Comparing renderings rather than values is
/// deliberate: `=` on a boxed `byte[]` is reference equality, and the
/// difference this pack exists to catch (a width narrowed on the way
/// back) produces a value that is equal-looking at every representation
/// except its type.
let private render (result: Result<obj, DecodeError>) : string =
    match result with
    | Ok value ->
        let runtime = if isNull value then "null" else value.GetType().FullName
        sprintf "Ok %A : %s" value runtime
    | Error error ->
        sprintf "Error expected=%s found=%s path=%s" error.Expected error.Found (DecodeError.renderPath error.Path)

/// Never throws. A throw from EITHER side is a finding this pack must
/// report rather than a crash it should take — the model is proved
/// total, so a throw out of the model side would be the extraction or
/// the bridge, and either is worth naming.
let private runBoth (toModel: Value -> ModelValue) (p: Paired) (value: Value) : string * string =
    let viaProduction =
        try
            render (p.Production value)
        with ex ->
            sprintf "THREW %s: %s" (ex.GetType().Name) ex.Message

    let viaModel =
        try
            render (p.Model(toModel value))
        with ex ->
            sprintf "THREW %s: %s" (ex.GetType().Name) ex.Message

    viaProduction, viaModel

/// Every disagreement over a byte payload, at the type it is declared
/// at. Returns the empty list when the two agree everywhere.
let private disagreements (toModel: Value -> ModelValue) (cases: (string * Type * byte[]) list) =
    cases
    |> List.choose (fun (name, target, bytes) ->
        match tryPaired target with
        | None -> None
        | Some p ->
            match Read.Reader(bytes).TryReadValue() with
            // A payload the one-pass reader itself refuses never reaches
            // either decoder, so there is nothing to compare. The reader
            // is outside the theorem and this pack does not pretend
            // otherwise — see `proofs/README.md`'s last rung.
            | Error _ -> None
            | Ok value ->
                let viaProduction, viaModel = runBoth toModel p value

                if viaProduction = viaModel then
                    None
                else
                    Some(
                        sprintf
                            "%s (%s)\n    production: %s\n    model:      %s"
                            name
                            target.Name
                            viaProduction
                            viaModel
                    ))

let private acceptCases () =
    coveredCases |> List.map (fun c -> c.Name, c.ClrType, c.WriteMsgPack())

let private refuseCases () =
    mutations ()
    |> List.choose (fun m ->
        match m.MsgPack with
        | Some bytes -> Some(m.Name, m.Target, bytes)
        | None -> None)

// ─── The pack ────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 787 - the proved model as oracle" [

        testCase "the covered set is not vacuous"
        <| fun () ->
            // The adequacy guard. Every arm below is a `List.isEmpty`
            // assertion over a computed population, and an empty
            // population satisfies one trivially - so the population
            // itself is asserted first.
            Expect.isGreaterThan
                (List.length coveredCases)
                30
                "the model should be an oracle for a substantial slice of the Phase 784 corpus"

            Expect.isGreaterThan (List.length (refuseCases ())) 5 "the refuse path should have mutations to run"

        testCase "the extracted model agrees with production over every covered corpus case"
        <| fun () ->
            let found = disagreements bridge (acceptCases ())

            Expect.isEmpty
                found
                (sprintf
                    "the proved model and the shipped algebra must agree on outcome, value and message:\n  %s"
                    (String.concat "\n  " found))

        testCase "the extracted model agrees with production over every refuse-path mutation"
        <| fun () ->
            // The refuse path is where a model most easily drifts: a
            // refusal that still refuses but for a different stated
            // reason is invisible to an outcome-class comparison and
            // caught by this one, because `render` carries the expected
            // and found text.
            let found = disagreements bridge (refuseCases ())

            Expect.isEmpty
                found
                (sprintf
                    "the proved model and the shipped algebra must refuse the same payloads for the same stated reasons:\n  %s"
                    (String.concat "\n  " found))

        testCase "the differential CATCHES a bridge that forgets the source width - the go-red case"
        <| fun () ->
            let found = disagreements blindBridge (acceptCases () @ refuseCases ())

            Expect.isNonEmpty
                found
                "a bridge that declares every integer 64-bit must be caught: if this passes, the comparison is \
                 agreeing with whatever it is shown and every other arm in this pack is worthless"

        testCase "and the width rule is what it catches"
        <| fun () ->
            // The mechanism, pinned rather than left to the population:
            // `writeDecimal`'s words ride `write32bitNumber`, so a
            // negative word arrives as a 32-bit UNSIGNED value that
            // `Decode.asInt32` admits under Phase 786's "a source no
            // wider than its target always survives" clause. Forget the
            // width and the same value refuses.
            let word = Value.UInt(4294967295UL, IntegerWidth.Bits32)

            let honest =
                RemotingDecode.map toInt32 (RemotingDecode.as_int32 ()) (bridge word)
                |> toResult

            let blind =
                RemotingDecode.map toInt32 (RemotingDecode.as_int32 ()) (blindBridge word)
                |> toResult

            Expect.equal honest (Ok -1) "the honest bridge reinterprets a 32-bit unsigned word as Int32 -1"
            Expect.isError blind "the blind bridge must lose the width check and refuse a well-formed decimal word"

            Expect.equal
                (Decode.asInt32 word)
                (Ok -1)
                "and production agrees with the honest bridge, which is what makes the disagreement a finding"

        testCase "the model never throws, on any corpus payload, at any covered type"
        <| fun () ->
            // The theorem's operational face. `decode_total` says the
            // model cannot reach a state that is neither an accept nor a
            // named refusal; the extraction is trusted rather than
            // proved, so this is the arm that would catch an extractor
            // or bridge defect turning that into an exception.
            let threw =
                acceptCases () @ refuseCases ()
                |> List.collect (fun (name, target, bytes) ->
                    match tryPaired target with
                    | None -> []
                    | Some p ->
                        match Read.Reader(bytes).TryReadValue() with
                        | Error _ -> []
                        | Ok value ->
                            try
                                p.Model(bridge value) |> ignore
                                []
                            with ex -> [ sprintf "%s: %s %s" name (ex.GetType().Name) ex.Message ])

            Expect.isEmpty threw (sprintf "the extracted model threw:\n  %s" (String.concat "\n  " threw))

        testCase "the reference vocabulary round-trips through the extracted encoder and decoder"
        <| fun () ->
            // `decode_encode_roundtrip` proved this for every value of
            // the reference record. Running one instance is not a
            // second proof - it is the check that the EXTRACTION of
            // both halves still computes what the proof is about.
            let address: RemotingDecode.ref_address = {
                line1 = "1 Example Way"
                postcode = "EX1 2MP"
                country = "GB"
            }

            let consignment: RemotingDecode.ref_consignment = {
                reference = "REF-001"
                origin = address
                weight = BigInteger -4200
                urgent = true
            }

            let strLen (s: string) = ix s.Length

            let encoded: ModelValue = RemotingDecode.encode_consignment strLen consignment

            Expect.equal
                (RemotingDecode.decode_consignment () encoded |> toResult)
                (Ok consignment)
                "decode (encode c) = c, over the reference API-record vocabulary"
    ]