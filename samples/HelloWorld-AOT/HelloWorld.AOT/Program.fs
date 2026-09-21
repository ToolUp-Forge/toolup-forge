// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module HelloWorld.AOT.Program

open System
open System.IO
open System.Text.Json
open ToolUp.Remoting
open ToolUp.Remoting.MsgPack
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.Tests.Remoting.WireCorpus
open HelloWorld.AOT

// =============================================================================
// Phase 804.B - the AOT proof, closing 69k.H
// =============================================================================
//
// Every pinned fixture of the remoting wire corpus goes through GENERATED
// code only:
//
//   * the `.msgpack` fixture (what the server's binary-response writer
//     emits) through the generated closed-algebra decoder registered for
//     the case's declared type - the client's response path;
//   * the `.json` fixture (what the request wire carries) through the
//     generated typed argument parse for the echo method declared at that
//     type - the server's argument path.
//
// Each decoded value is compared against the corpus's own declaration at
// the declared type, by the corpus's own `Compare`. The program exits
// non-zero on any disagreement, and ALSO when the set of fixtures the
// generator refused a decoder for is not exactly the set recorded below:
// a refusal that disappears means the algebra widened, and the record
// here should widen with it rather than silently stop being checked.
//
// ─── Why there is no printf in this file ───────────────────────────────
//
// F#'s printf family builds its formatter through
// `MethodInfo.MakeGenericMethod`, which native AOT refuses at RUNTIME
// (measured on this repository's .NET 10 SDK: a `printfn "%d"` in an
// otherwise-empty AOT-published F# console app throws NotSupportedException
// on its first call, after a publish that reported only FSharp.Core's
// blanket warnings). So a program that publishes clean is not yet proved;
// it has to RUN, and it has to avoid the one FSharp.Core path that fails
// only when reached. Output here is `Console.Out.WriteLine` over
// concatenated strings.

let private say (text: string) = Console.Out.WriteLine text

let private join (parts: string list) = String.Concat(List.toArray parts)

// ─── The generated argument-parse table, one arm per corpus type ────────
//
// The emitted parses are typed per method (`decodeInt32Args` returns
// `Result<int, DecodeError>`), so the corpus - which holds a `Type` and
// compares a boxed value - reaches each one through an arm that closes
// over the typed call and erases only the result. Nothing here reflects;
// the erasure is the corpus's own `Compare` boundary.

let private argumentArms: (Type * (JsonSerializerOptions -> JsonElement list -> Result<obj, DecodeError>)) list = [
    typeof<bool>, (fun o a -> ICorpusApiDispatch.decodeBoolArgs o a |> Result.map box)
    typeof<int>, (fun o a -> ICorpusApiDispatch.decodeInt32Args o a |> Result.map box)
    typeof<string>, (fun o a -> ICorpusApiDispatch.decodeStringArgs o a |> Result.map box)
    typeof<char>, (fun o a -> ICorpusApiDispatch.decodeCharArgs o a |> Result.map box)
    typeof<byte>, (fun o a -> ICorpusApiDispatch.decodeByteArgs o a |> Result.map box)
    typeof<sbyte>, (fun o a -> ICorpusApiDispatch.decodeSByteArgs o a |> Result.map box)
    typeof<int16>, (fun o a -> ICorpusApiDispatch.decodeInt16Args o a |> Result.map box)
    typeof<uint16>, (fun o a -> ICorpusApiDispatch.decodeUInt16Args o a |> Result.map box)
    typeof<uint32>, (fun o a -> ICorpusApiDispatch.decodeUInt32Args o a |> Result.map box)
    typeof<int64>, (fun o a -> ICorpusApiDispatch.decodeInt64Args o a |> Result.map box)
    typeof<uint64>, (fun o a -> ICorpusApiDispatch.decodeUInt64Args o a |> Result.map box)
    typeof<float>, (fun o a -> ICorpusApiDispatch.decodeFloatArgs o a |> Result.map box)
    typeof<float32>, (fun o a -> ICorpusApiDispatch.decodeFloat32Args o a |> Result.map box)
    typeof<decimal>, (fun o a -> ICorpusApiDispatch.decodeDecimalArgs o a |> Result.map box)
    typeof<DateTime>, (fun o a -> ICorpusApiDispatch.decodeDateTimeArgs o a |> Result.map box)
    typeof<DateTimeOffset>, (fun o a -> ICorpusApiDispatch.decodeDateTimeOffsetArgs o a |> Result.map box)
    typeof<TimeSpan>, (fun o a -> ICorpusApiDispatch.decodeTimeSpanArgs o a |> Result.map box)
    typeof<DateOnly>, (fun o a -> ICorpusApiDispatch.decodeDateOnlyArgs o a |> Result.map box)
    typeof<TimeOnly>, (fun o a -> ICorpusApiDispatch.decodeTimeOnlyArgs o a |> Result.map box)
    typeof<Guid>, (fun o a -> ICorpusApiDispatch.decodeGuidArgs o a |> Result.map box)
    typeof<byte[]>, (fun o a -> ICorpusApiDispatch.decodeBytesArgs o a |> Result.map box)
    typeof<int option>, (fun o a -> ICorpusApiDispatch.decodeOptionIntArgs o a |> Result.map box)
    typeof<string option>, (fun o a -> ICorpusApiDispatch.decodeOptionStringArgs o a |> Result.map box)
    typeof<Address option>, (fun o a -> ICorpusApiDispatch.decodeOptionAddressArgs o a |> Result.map box)
    typeof<int list>, (fun o a -> ICorpusApiDispatch.decodeListIntArgs o a |> Result.map box)
    typeof<Address list>, (fun o a -> ICorpusApiDispatch.decodeListAddressArgs o a |> Result.map box)
    typeof<string[]>, (fun o a -> ICorpusApiDispatch.decodeArrayStringArgs o a |> Result.map box)
    typeof<Map<string, int>>, (fun o a -> ICorpusApiDispatch.decodeMapStringIntArgs o a |> Result.map box)
    typeof<Map<int, string>>, (fun o a -> ICorpusApiDispatch.decodeMapIntStringArgs o a |> Result.map box)
    typeof<Set<string>>, (fun o a -> ICorpusApiDispatch.decodeSetStringArgs o a |> Result.map box)
    typeof<Set<int>>, (fun o a -> ICorpusApiDispatch.decodeSetIntArgs o a |> Result.map box)
    typeof<int * string>, (fun o a -> ICorpusApiDispatch.decodeTuplePairArgs o a |> Result.map box)
    typeof<int * string * bool>, (fun o a -> ICorpusApiDispatch.decodeTupleTripleArgs o a |> Result.map box)
    typeof<Priority>, (fun o a -> ICorpusApiDispatch.decodePriorityArgs o a |> Result.map box)
    typeof<Outcome>, (fun o a -> ICorpusApiDispatch.decodeOutcomeArgs o a |> Result.map box)
    typeof<Address>, (fun o a -> ICorpusApiDispatch.decodeAddressArgs o a |> Result.map box)
    typeof<Consignment>, (fun o a -> ICorpusApiDispatch.decodeConsignmentArgs o a |> Result.map box)
    typeof<Customer>, (fun o a -> ICorpusApiDispatch.decodeCustomerArgs o a |> Result.map box)
    typeof<ApiEnvelope>, (fun o a -> ICorpusApiDispatch.decodeEnvelopeArgs o a |> Result.map box)
]

// ─── The fixtures the generator REFUSES a decoder for, by name ───────────
//
// Recorded rather than derived so a change in the algebra's reach is
// noticed. One gap remains, named by the generator's census and not this
// sample's to close: DateOnly and TimeOnly have no combinator because the
// Fable reader refuses both, and the algebra ships cross-host or not at
// all. A record holding either keeps the reflection path with it.
//
// Phase 800 closed the other two gaps this list used to record - tuples,
// and a union case carrying more than one field - so `tuple-pair`,
// `tuple-triple`, the three `union-*` fixtures and `record-consignment`
// (which holds an Outcome) now decode through the generated decoders and
// are no longer listed here.

let private expectedRefusals: (string * string) list = [
    "record-nested", "holds a DateOnly - no cross-host combinator"
    "record-envelope", "holds a Customer, and so a DateOnly, and an Outcome"
    "date-dateonly", "DateOnly - no cross-host combinator"
    "date-timeonly", "TimeOnly - no cross-host combinator"
    // Phase 803's boundary fixtures for the same two types.
    "date-dateonly-min", "DateOnly - no cross-host combinator"
    "date-dateonly-max", "DateOnly - no cross-host combinator"
    "date-timeonly-max", "TimeOnly - no cross-host combinator"
]

// ─── One case, both wires ───────────────────────────────────────────────

[<RequireQualifiedAccess>]
type private Verdict =
    | Passed
    | Refused
    | Failed of string

let private describe =
    function
    | Verdict.Passed -> "passed"
    | Verdict.Refused -> "refused"
    | Verdict.Failed why -> "FAILED: " + why

let private judge (c: WireCase) (decoded: Result<obj, DecodeError>) : Verdict =
    match decoded with
    | Error error -> Verdict.Failed(DecodeError.render error)
    | Ok value ->
        match c.Compare value with
        | Ok() -> Verdict.Passed
        | Error why -> Verdict.Failed why

/// The response path: fixture bytes -> one-pass structural read -> the
/// generated decoder registered for the declared type. `None` from the
/// registry IS the reflection path, and this program never takes it.
let private decodeResponse (c: WireCase) (bytes: byte[]) : Verdict =
    match RemotingDecoders.tryGet c.ClrType with
    | None -> Verdict.Refused
    | Some decoder -> Read.Reader(bytes).TryReadValue() |> Result.bind decoder |> judge c

/// The argument path: fixture text -> one parsed element -> the generated
/// typed parse for the echo method at the declared type, exactly as a
/// generated dispatch table would hand a one-argument call its argument.
let private decodeArgument (options: JsonSerializerOptions) (c: WireCase) (text: string) : Verdict =
    match argumentArms |> List.tryFind (fun (t, _) -> t = c.ClrType) with
    | None -> Verdict.Failed "the contract declares no echo method at this type"
    | Some(_, parse) ->
        use document = JsonDocument.Parse text
        parse options [ document.RootElement.Clone() ] |> judge c

// ─── The corpus directory ───────────────────────────────────────────────

let private findCorpus (argv: string[]) : string option =
    match argv |> Array.tryFindIndex ((=) "--corpus") with
    | Some i when i + 1 < argv.Length -> Some argv[i + 1]
    | _ ->
        let rec up (dir: DirectoryInfo) =
            if isNull dir then
                None
            else
                let candidate = Path.Combine(dir.FullName, CorpusRelativePath)

                if Directory.Exists candidate then
                    Some candidate
                else
                    up dir.Parent

        up (DirectoryInfo AppContext.BaseDirectory)

[<EntryPoint>]
let main argv =
    match findCorpus argv with
    | None ->
        say (
            join [
                "HelloWorld-AOT: no "
                CorpusRelativePath
                " above "
                AppContext.BaseDirectory
                "; pass --corpus <dir>."
            ]
        )

        2
    | Some corpus ->
        say (
            join [
                "HelloWorld-AOT - dynamic code supported: "
                (string System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
                "; corpus: "
                corpus
            ]
        )

        // The one explicit act a deployment performs to put the algebra
        // path live. Nothing registers itself.
        CorpusDecoders.registerAll ()
        let options = FableConverters.create ()

        // ─── What the argument table can and cannot claim natively ─────
        //
        // The emitted table's one call is the typed STJ seam, and that seam
        // runs the platform's reflection converter set. MEASURED on the
        // first native run of this program (2026-09-15, .NET 10): every
        // primitive, string, date, Guid, byte[] and string[] fixture parses;
        // every option, list, set, map, tuple, union and record fixture does
        // not — the generic converters over value types have no native
        // instantiation ("missing native code") and record / union
        // construction goes through a reflective invoke the AOT runtime
        // refuses. That is the boundary Phase 785 recorded (785.F): the
        // argument side is a decoder for bytes that arrive as JSON, and the
        // algebra's JSON extension is a separate phase. So under the native
        // host an argument-side failure is tallied as REFLECTION-BOUND and
        // reported, never hidden and never fatal — this program must not
        // widen the algebra to make it pass. Under the JIT the same failure
        // IS fatal: there the seam is expected to work, and 68 of 68 do.
        let dynamicCode =
            System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported

        let mutable responsePassed = 0
        let mutable argumentPassed = 0
        let mutable reflectionBound = 0
        let mutable failures = 0
        let refused = ResizeArray<string>()

        for c in pinnedCases do
            let bytes = File.ReadAllBytes(Path.Combine(corpus, c.Name + ".msgpack"))
            let text = File.ReadAllText(Path.Combine(corpus, c.Name + ".json"))
            let response = decodeResponse c bytes
            let argument = decodeArgument options c text

            match response with
            | Verdict.Passed -> responsePassed <- responsePassed + 1
            | Verdict.Refused -> refused.Add c.Name
            | Verdict.Failed _ -> failures <- failures + 1

            let argumentShown =
                match argument with
                | Verdict.Passed ->
                    argumentPassed <- argumentPassed + 1
                    describe argument
                | Verdict.Failed why when not dynamicCode ->
                    reflectionBound <- reflectionBound + 1
                    "reflection-bound: " + why
                | Verdict.Refused
                | Verdict.Failed _ ->
                    failures <- failures + 1
                    describe argument

            say (
                join [
                    "  "
                    c.Name.PadRight 28
                    " response: "
                    (describe response).PadRight 10
                    " argument: "
                    argumentShown
                ]
            )

        let expected = expectedRefusals |> List.map fst |> Set.ofList
        let actual = Set.ofSeq refused
        let unexpectedlyRefused = Set.difference actual expected |> Set.toList
        let unexpectedlyExpressible = Set.difference expected actual |> Set.toList

        say ""

        say (
            join [
                string (List.length pinnedCases)
                " fixture(s): "
                string responsePassed
                " decoded through generated decoders, "
                string refused.Count
                " refused by the generator (kept the reflection path, which this program does not take), "
                string argumentPassed
                " parsed through the generated argument table, "
                string failures
                " failure(s)."
            ]
        )

        if reflectionBound > 0 then
            say (
                join [
                    "  "
                    string reflectionBound
                    " argument parse(s) are reflection-bound under the native host: the typed STJ seam runs the reflection converter set, which native AOT does not serve for options, lists, sets, maps, tuples, unions or records. Measured, recorded, not fatal here - the JSON extension of the algebra is the separate act Phase 785 recorded."
                ]
            )

        for name in refused do
            let why =
                expectedRefusals
                |> List.tryFind (fst >> (=) name)
                |> Option.map snd
                |> Option.defaultValue "NOT RECORDED"

            say (join [ "  refused: "; name; " - "; why ])

        for name in unexpectedlyRefused do
            say (join [ "  UNEXPECTED refusal: "; name; " - the recorded set does not name it" ])

        for name in unexpectedlyExpressible do
            say (
                join [
                    "  now expressible: "
                    name
                    " - the algebra widened; remove it from the recorded refusals"
                ]
            )

        if
            failures = 0
            && List.isEmpty unexpectedlyRefused
            && List.isEmpty unexpectedlyExpressible
        then
            0
        else
            1