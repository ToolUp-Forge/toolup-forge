// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Remoting.Benchmarks.Program

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open ToolUp.Remoting
open ToolUp.Remoting.MsgPack
open ToolUp.Remoting.Server
open ToolUp.Platform.Tests.Remoting.WireCorpus
open HelloWorld.AOT
open HelloWorld.AOT.Contract

// =============================================================================
// Phase 804.C — generated versus reflection, measured (closing 69k.G)
// =============================================================================
//
// Two paths, two arms each, over the remoting wire corpus's pinned fixtures:
//
//   RESPONSE DECODE — the client's binary path. Generated: the one-pass
//   structural read into a `Value`, then the closed-algebra decoder the
//   generator registered for the declared type. Reflection: the reader's
//   typed entry, which walks the type's shape through its caches.
//
//   DISPATCH — the server's request path, for five representative methods
//   of the sample's echo contract, three arms (Phase 906):
//     * generated — the proxy the adapter builds with the generator's
//       invocation table registered (`ICorpusApiDispatch.register ()`): the
//       proxy's own verb check, parse, record-scoped decode and serialise
//       around the generated typed call. This is the route a deployment
//       takes, inside the adapter's pre-flight chain.
//     * reflection — the same proxy with nothing registered: every method's
//       endpoint built by the shape visitor, over every method of the record.
//     * direct — Phase 804's hand-written floor: the emitted typed parse, a
//       direct call of the handler and a serialise, with NO proxy at all.
//       It is what a route outside the pre-flight chain would cost, kept
//       only as the lower bound; no deployment may dispatch this way.
//
// COLD START is measured in a FRESH PROCESS per arm (min of five): a warm
// loop cannot see what the first call pays — reflection's shape caches
// and the proxy's build, or the generated path's registration and JIT.
// PER REQUEST is a warm loop: nine rounds, per-operation microseconds,
// min and median over rounds — the perf-budget gate's statistic, for the
// same reason: noise on a shared machine is one-sided.
//
// Every operation is CHECKED, not just timed: a decode that refuses or a
// dispatch that does not succeed fails the run. A benchmark that measures
// a failing path measures the failure.

// ─── Timing ─────────────────────────────────────────────────────────────

let private usPerTick = 1_000_000.0 / float Stopwatch.Frequency

type private Sample = { MinUs: float; MedianUs: float }

/// `rounds` rounds of `opsPerRound` calls; per-operation microseconds,
/// min and median over rounds.
let private measure (rounds: int) (opsPerRound: int) (op: unit -> unit) : Sample =
    let perRound = Array.zeroCreate rounds

    for r in 0 .. rounds - 1 do
        let start = Stopwatch.GetTimestamp()

        for _ in 1..opsPerRound do
            op ()

        let elapsed = Stopwatch.GetTimestamp() - start
        perRound[r] <- float elapsed * usPerTick / float opsPerRound

    let sorted = Array.sort perRound

    {
        MinUs = sorted[0]
        MedianUs = sorted[rounds / 2]
    }

/// Two arms measured in the SAME JIT state: warmed together, a settle so
/// tiered compilation's background promotion has landed for both, then
/// the rounds interleaved (arm A, arm B, arm A, ...). Measured before this
/// existed: the generated dispatch arm read 6.7 µs when timed first and
/// 1.3 µs when timed after the proxy's arm had warmed the same generic
/// instantiations — a tier-0 versus tier-1 artefact, not a dispatch cost.
let private measurePair (rounds: int) (opsPerRound: int) (a: unit -> unit) (b: unit -> unit) : Sample * Sample =
    for _ in 1..opsPerRound do
        a ()
        b ()

    Threading.Thread.Sleep 400
    let ra = Array.zeroCreate rounds
    let rb = Array.zeroCreate rounds

    let round (op: unit -> unit) =
        let start = Stopwatch.GetTimestamp()

        for _ in 1..opsPerRound do
            op ()

        float (Stopwatch.GetTimestamp() - start) * usPerTick / float opsPerRound

    for r in 0 .. rounds - 1 do
        ra[r] <- round a
        rb[r] <- round b

    let sample (xs: float[]) =
        let sorted = Array.sort xs

        {
            MinUs = sorted[0]
            MedianUs = sorted[rounds / 2]
        }

    sample ra, sample rb

let private timeOnce (op: unit -> unit) : float =
    let start = Stopwatch.GetTimestamp()
    op ()
    float (Stopwatch.GetTimestamp() - start) * usPerTick / 1000.0

// ─── The corpus ─────────────────────────────────────────────────────────

type private Fixture = {
    Case: WireCase
    MsgPack: byte[]
    Json: string
}

let private findCorpus () : string =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then
            failwithf "no %s above %s" CorpusRelativePath AppContext.BaseDirectory
        else
            let candidate = Path.Combine(dir.FullName, CorpusRelativePath)

            if Directory.Exists candidate then
                candidate
            else
                up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let private loadFixtures () : Fixture list =
    let corpus = findCorpus ()

    pinnedCases
    |> List.map (fun c -> {
        Case = c
        MsgPack = File.ReadAllBytes(Path.Combine(corpus, c.Name + ".msgpack"))
        Json = File.ReadAllText(Path.Combine(corpus, c.Name + ".json"))
    })

/// The fixtures the generator emitted a decoder for — read off the emitted
/// `covered` list, so an arm can know the set WITHOUT registering (the
/// reflection arm must not pay the registration it is being compared to).
let private expressible (fixtures: Fixture list) : Fixture list =
    let covered = Set.ofList CorpusDecoders.covered

    fixtures
    |> List.filter (fun f -> covered.Contains(RemotingDecoders.keyFor f.Case.ClrType))

// ─── Response decode ────────────────────────────────────────────────────

let private decodeGenerated (decoder: RegisteredDecoder) (bytes: byte[]) : unit =
    match Read.Reader(bytes).TryReadValue() |> Result.bind decoder with
    | Ok _ -> ()
    | Error error -> failwith (DecodeError.render error)

let private decodeReflection (t: Type) (bytes: byte[]) : unit =
    match Read.Reader(bytes).TryRead t with
    | Ok _ -> ()
    | Error error -> failwith (DecodeError.render error)

// ─── Dispatch ───────────────────────────────────────────────────────────

let private echo (value: 'T) : Async<'T> = async.Return value

/// The sample's contract, every method an echo.
let private impl: ICorpusApi = {
    Bool = echo
    Int32 = echo
    String = echo
    Char = echo
    Byte = echo
    SByte = echo
    Int16 = echo
    UInt16 = echo
    UInt32 = echo
    Int64 = echo
    UInt64 = echo
    Float = echo
    Float32 = echo
    Decimal = echo
    DateTime = echo
    DateTimeOffset = echo
    TimeSpan = echo
    DateOnly = echo
    TimeOnly = echo
    Guid = echo
    Bytes = echo
    OptionInt = echo
    OptionString = echo
    OptionAddress = echo
    ListInt = echo
    ListAddress = echo
    ArrayString = echo
    MapStringInt = echo
    MapIntString = echo
    SetString = echo
    SetInt = echo
    TuplePair = echo
    TupleTriple = echo
    Priority = echo
    Outcome = echo
    Address = echo
    Consignment = echo
    Customer = echo
    Envelope = echo
    Tree = echo
    MapUnionKey = echo
    TemplatedMessage = echo
}

/// The options the adapter would build: the default STJ backend with the
/// platform's converter set, the implementation as a static value.
let private options: RemotingOptions<obj, ICorpusApi> =
    Remoting.createApi () |> Remoting.fromValue impl

let private stjOptions =
    match options.JsonSerializer with
    | SystemTextJson o -> o

/// One request's arguments, as the proxy sees them: the outer array
/// parsed once, each element cloned out of the document.
let private parseArgs (body: byte[]) : JsonElement list =
    use document = JsonDocument.Parse(ReadOnlyMemory body)
    document.RootElement.EnumerateArray() |> Seq.map _.Clone() |> Seq.toList

type private DispatchArm = {
    Label: string
    Endpoint: string
    Body: byte[]
    /// Phase 804's floor: typed parse, direct call, serialise, no proxy.
    Direct: MemoryStream -> unit
}

let private arm
    (label: string)
    (methodName: string)
    (fixture: Fixture)
    (parse: JsonSerializerOptions -> JsonElement list -> Result<'a, DecodeError>)
    (handler: 'a -> Async<'a>)
    : DispatchArm =
    let body = Encoding.UTF8.GetBytes("[" + fixture.Json + "]")

    {
        Label = label
        Endpoint = options.RouteBuilder typeof<ICorpusApi>.Name methodName
        Body = body
        Direct =
            fun output ->
                output.SetLength 0L

                match parse stjOptions (parseArgs body) with
                | Ok value ->
                    // The same execution shape as the proxy's endpoint: the
                    // handler's Async bound inside a task, the result written
                    // to the output stream and the stream rewound. Anything
                    // else (an `Async.RunSynchronously`, say) would time a
                    // different scheduler rather than a different dispatch.
                    let run = task {
                        let! result = handler value
                        Proxy.jsonSerializeWithBackend options.JsonSerializer result output
                        output.Position <- 0L
                    }

                    run.Wait()
                | Error error -> failwith (DecodeError.render error)
    }

let private dispatchArms (fixtures: Fixture list) : DispatchArm list =
    let fixture name =
        fixtures |> List.find (fun f -> f.Case.Name = name)

    [
        arm "int" "Int32" (fixture "primitive-int") ICorpusApiDispatch.decodeInt32Args impl.Int32
        arm "string" "String" (fixture "primitive-string") ICorpusApiDispatch.decodeStringArgs impl.String
        arm "Address (flat record)" "Address" (fixture "record-flat") ICorpusApiDispatch.decodeAddressArgs impl.Address
        arm
            "Address list"
            "ListAddress"
            (fixture "list-of-records")
            ICorpusApiDispatch.decodeListAddressArgs
            impl.ListAddress
        arm
            "Consignment (nested record)"
            "Consignment"
            (fixture "record-consignment")
            ICorpusApiDispatch.decodeConsignmentArgs
            impl.Consignment
    ]

/// A dispatch through the proxy the adapter builds — generated or
/// reflective, whichever it composed — invoked as the adapter invokes it
/// (body bytes cached, JSON content type, POST).
let private invokeProxy
    (proxy: InvocationProps<ICorpusApi> -> Task<InvocationResult>)
    (arm: DispatchArm)
    (output: MemoryStream)
    : unit =
    output.SetLength 0L

    let props = {
        Input = new MemoryStream(arm.Body, false)
        InputBytes = Some arm.Body
        Output = output
        ImplementationBuilder = fun () -> impl
        EndpointName = arm.Endpoint
        HttpVerb = "POST"
        InputContentType = "application/json"
        IsProxyHeaderPresent = true
    }

    match (proxy props).Result with
    | InvocationResult.Success _ -> ()
    | other -> failwithf "dispatch of %s through the proxy did not succeed: %A" arm.Label other

// ─── Cold start: one arm, in this (fresh) process ───────────────────────

let private cold (path: string) (armName: string) : float =
    let fixtures = loadFixtures ()

    match path, armName with
    | "decode", "generated" ->
        let set = expressible fixtures

        timeOnce (fun () ->
            CorpusDecoders.registerAll ()

            for f in set do
                decodeGenerated (RemotingDecoders.tryGet f.Case.ClrType |> Option.get) f.MsgPack)
    | "decode", "reflection" ->
        let set = expressible fixtures

        timeOnce (fun () ->
            for f in set do
                decodeReflection f.Case.ClrType f.MsgPack)
    | "dispatch", "direct" ->
        let arms = dispatchArms fixtures
        use output = new MemoryStream()

        timeOnce (fun () ->
            ICorpusApiDispatch.methods |> List.length |> ignore

            for a in arms do
                a.Direct output)
    | "dispatch", "generated" ->
        let arms = dispatchArms fixtures
        use output = new MemoryStream()

        timeOnce (fun () ->
            ICorpusApiDispatch.register ()
            let proxy = Proxy.makeApiProxy options

            for a in arms do
                invokeProxy proxy a output)
    | "dispatch", "reflection" ->
        let arms = dispatchArms fixtures
        use output = new MemoryStream()

        timeOnce (fun () ->
            let proxy = Proxy.makeApiProxy options

            for a in arms do
                invokeProxy proxy a output)
    | _ -> failwithf "unknown cold arm %s %s" path armName

/// Spawn this program `boots` times for one arm; the child's own first-pass
/// milliseconds, min over boots, beside the child's wall time for context.
let private coldStart (boots: int) (path: string) (armName: string) : float * float =
    let dll = Reflection.Assembly.GetEntryAssembly().Location

    let samples = [
        for _ in 1..boots do
            let info =
                ProcessStartInfo(Environment.ProcessPath, RedirectStandardOutput = true, UseShellExecute = false)

            for a in [ dll; "--cold"; path; armName ] do
                info.ArgumentList.Add a

            let wall = Stopwatch.StartNew()
            use child = Process.Start info
            let text = child.StandardOutput.ReadToEnd()
            child.WaitForExit()
            wall.Stop()

            if child.ExitCode <> 0 then
                failwithf "cold %s %s: child exited %d: %s" path armName child.ExitCode text

            Double.Parse(text.Trim(), Globalization.CultureInfo.InvariantCulture), wall.Elapsed.TotalMilliseconds
    ]

    samples |> List.map fst |> List.min, samples |> List.map snd |> List.min

// ─── The report ─────────────────────────────────────────────────────────

let private ms (v: float) = v.ToString("0.000") + " ms"

let private us (s: Sample) =
    s.MinUs.ToString("0.00") + " / " + s.MedianUs.ToString("0.00") + " µs"

let private report (boots: int) (rounds: int) =
    let fixtures = loadFixtures ()
    let set = expressible fixtures
    CorpusDecoders.registerAll ()

    let decoders =
        set
        |> List.map (fun f -> RemotingDecoders.tryGet f.Case.ClrType |> Option.get, f)

    let generatedDecode, reflectionDecode =
        measurePair
            rounds
            200
            (fun () ->
                for d, f in decoders do
                    decodeGenerated d f.MsgPack)
            (fun () ->
                for _, f in decoders do
                    decodeReflection f.Case.ClrType f.MsgPack)

    let perFixture (s: Sample) = {
        MinUs = s.MinUs / float set.Length
        MedianUs = s.MedianUs / float set.Length
    }

    let arms = dispatchArms fixtures
    // The reflective proxy is built BEFORE the table is registered — the
    // proxy reads the registry when it is built — and the generated one
    // after, so one process holds both.
    let reflective = Proxy.makeApiProxy options
    ICorpusApiDispatch.register ()
    let generated = Proxy.makeApiProxy options
    use output = new MemoryStream()
    use reflectiveOutput = new MemoryStream()

    // Checked, not just timed: the two routes must write the same bytes.
    for a in arms do
        invokeProxy generated a output
        invokeProxy reflective a reflectiveOutput

        if output.ToArray() <> reflectiveOutput.ToArray() then
            failwithf "dispatch of %s: the generated and reflective routes wrote different responses" a.Label

    let dispatch =
        arms
        |> List.map (fun a ->
            let g, r =
                measurePair rounds 2000 (fun () -> invokeProxy generated a output) (fun () ->
                    invokeProxy reflective a output)

            let d = measure rounds 2000 (fun () -> a.Direct output)
            a.Label, g, r, d)

    let coldDecodeGenerated, wallDecodeGenerated = coldStart boots "decode" "generated"

    let coldDecodeReflection, wallDecodeReflection =
        coldStart boots "decode" "reflection"

    let coldDispatchGenerated, wallDispatchGenerated =
        coldStart boots "dispatch" "generated"

    let coldDispatchReflection, wallDispatchReflection =
        coldStart boots "dispatch" "reflection"

    let coldDispatchDirect, wallDispatchDirect = coldStart boots "dispatch" "direct"

    let optimised =
        let attr =
            Reflection.Assembly.GetEntryAssembly().GetCustomAttributes(typeof<DebuggableAttribute>, false)

        attr.Length = 0 || not (attr[0] :?> DebuggableAttribute).IsJITOptimizerDisabled

    let line (s: string) = Console.Out.WriteLine s

    line (
        "## ToolUp.Remoting benchmarks — "
        + DateTime.UtcNow.ToString("yyyy-MM-dd")
        + ", "
        + (if optimised then
               "optimised (Release)"
           else
               "UNOPTIMISED (Debug)")
        + ", "
        + string Environment.ProcessorCount
        + " logical cores, .NET "
        + Environment.Version.ToString()
    )

    line ""

    line (
        "Corpus: "
        + string fixtures.Length
        + " pinned fixtures; "
        + string set.Length
        + " expressible in the closed algebra."
    )

    line (
        "Cold start: first pass in a fresh process, min of "
        + string boots
        + " boots (child wall time beside it)."
    )

    line ("Per request: min / median per-operation over " + string rounds + " rounds.")
    line ""
    line "| Path | Arm | Cold start (first pass) | Child wall | Per request |"
    line "|---|---|---|---|---|"

    line (
        "| Response decode, whole expressible set | generated | "
        + ms coldDecodeGenerated
        + " | "
        + ms wallDecodeGenerated
        + " | "
        + us generatedDecode
        + " per set; "
        + us (perFixture generatedDecode)
        + " per fixture |"
    )

    line (
        "| Response decode, whole expressible set | reflection | "
        + ms coldDecodeReflection
        + " | "
        + ms wallDecodeReflection
        + " | "
        + us reflectionDecode
        + " per set; "
        + us (perFixture reflectionDecode)
        + " per fixture |"
    )

    line (
        "| Dispatch, five methods | generated | "
        + ms coldDispatchGenerated
        + " | "
        + ms wallDispatchGenerated
        + " | see below |"
    )

    line (
        "| Dispatch, five methods | reflection | "
        + ms coldDispatchReflection
        + " | "
        + ms wallDispatchReflection
        + " | see below |"
    )

    line (
        "| Dispatch, five methods | direct (804 floor, no proxy) | "
        + ms coldDispatchDirect
        + " | "
        + ms wallDispatchDirect
        + " | see below |"
    )

    line ""
    line "| Dispatch per request | generated | reflection | direct (floor) |"
    line "|---|---|---|---|"

    for label, g, r, d in dispatch do
        line ("| " + label + " | " + us g + " | " + us r + " | " + us d + " |")

/// Where a generated dispatch's microseconds go, stage by stage, for the
/// `int` echo: the argument-array parse, the typed parse, the handler bound
/// in a task, the serialise. Beside them the reflective proxy whole. A
/// per-request number with no decomposition is a number nobody can act on.
let private probe (rounds: int) =
    let fixtures = loadFixtures ()
    let fixture = fixtures |> List.find (fun f -> f.Case.Name = "primitive-int")
    let body = Encoding.UTF8.GetBytes("[" + fixture.Json + "]")
    let elements = parseArgs body
    let proxy = Proxy.makeApiProxy options
    let arms = dispatchArms fixtures
    let intArm = arms |> List.find (fun a -> a.Label = "int")
    use output = new MemoryStream()
    let n = 5000

    let stages: (string * (unit -> unit)) list = [
        "argument-array parse (JsonDocument + Clone)", (fun () -> parseArgs body |> ignore)
        "typed parse (generated decodeInt32Args)",
        (fun () ->
            match ICorpusApiDispatch.decodeInt32Args stjOptions elements with
            | Ok _ -> ()
            | Error e -> failwith (DecodeError.render e))
        "handler in a task + serialise",
        (fun () ->
            output.SetLength 0L

            let run = task {
                let! result = impl.Int32(unbox<int> fixture.Case.Value)
                Proxy.jsonSerializeWithBackend options.JsonSerializer result output
                output.Position <- 0L
            }

            run.Wait())
        "direct (804 floor), whole", (fun () -> intArm.Direct output)
        "reflective proxy, whole", (fun () -> invokeProxy proxy intArm output)
    ]

    for _, op in stages do
        for _ in 1..500 do
            op ()

    Console.Out.WriteLine "| Stage (`int` echo) | min / median per op |"
    Console.Out.WriteLine "|---|---|"

    for label, op in stages do
        Console.Out.WriteLine("| " + label + " | " + us (measure rounds n op) + " |")

// ─── Phase 856.E — wide-record decode ───────────────────────────────────

/// A record of `width` fields as the writer puts it on the wire: one
/// MessagePack array, fields positionally, each a positive fixint. Built
/// as BYTES and read through the one-pass reader, so the fixture does not
/// depend on how `Value.Arr` spells its carrier — the same source measured
/// the list-backed model before Phase 856 and the array-backed one after.
let private wideRecordBytes (width: int) : byte[] =
    let header =
        if width <= 15 then
            [| byte (0x90 ||| width) |]
        else
            [| 0xdcuy; byte (width >>> 8); byte (width &&& 0xff) |]

    Array.append header (Array.init width (fun i -> byte (i % 128)))

/// The decode a generated record decoder performs: one `Decode.field` per
/// field, each addressing its own position in the same array. The shape
/// that was quadratic while `field` reached its element through
/// `List.tryItem` — field `i` walked `i` cells — and is linear once the
/// access is indexed.
let private wideRecordDecoders (width: int) : Decoder<int> list =
    List.init width (fun i -> Decode.field ("F" + string i) i Decode.asInt32)

let private decodeWide (decoders: Decoder<int> list) (value: Value) : unit =
    for decoder in decoders do
        match decoder value with
        | Ok _ -> ()
        | Error error -> failwith (DecodeError.render error)

// ─── Phase 905 — the same fixture on the JSON wire ──────────────────────

/// A record of `width` fields as the JSON writer puts it on the wire: one
/// object keyed by field name (`{"F0":0,"F1":1,…}`), members in
/// declaration order. Built as TEXT and read through the one-pass reader,
/// so the fixture does not depend on how `JsonValue.Object` spells its
/// carrier — the same source measures the model before and after.
let private wideJsonObjectText (width: int) : string =
    Seq.init width (fun i -> "\"F" + string i + "\":" + string (i % 128))
    |> String.concat ","
    |> fun members -> "{" + members + "}"

/// The same values as a JSON array — the positional shape a tuple, a
/// union's field list and a map entry take on this wire.
let private wideJsonArrayText (width: int) : string =
    Seq.init width (fun i -> string (i % 128))
    |> String.concat ","
    |> fun items -> "[" + items + "]"

let private parseJson (text: string) : Json.JsonValue =
    match Json.JsonText.tryParse text with
    | Ok value -> value
    | Error error -> failwith (DecodeError.render error)

let private decodeWideJson (decoders: Json.JsonDecoder<int> list) (value: Json.JsonValue) : unit =
    for decoder in decoders do
        match decoder value with
        | Ok _ -> ()
        | Error error -> failwith (DecodeError.render error)

/// Per-record and per-field cost of the JSON decode a generated record
/// decoder performs (one `JsonDecode.field` BY NAME per field, in
/// declaration order), and of positional `JsonDecode.index` over an array
/// of the same width. The falsifier is the same as the MessagePack table's:
/// a per-field column that is flat from 8 to 256 fields means linear.
///
/// The FRESH table decodes a distinct parsed value on every operation, as a
/// server does per request, so whatever a lookup builds once per value is
/// paid on every row of it rather than amortised across the warm loop.
let private wideJson (rounds: int) =
    let widths = [ 8; 32; 64; 128; 256 ]

    let table (title: string) (fresh: bool) (text: int -> string) (decoders: int -> Json.JsonDecoder<int> list) =
        Console.Out.WriteLine("| " + title + " | whole record, min / median | per field, min / median |")
        Console.Out.WriteLine "|---|---|---|"

        for width in widths do
            let decoders = decoders width

            let ops =
                if fresh then
                    max 50 (20_000 / width)
                else
                    max 200 (200_000 / width)

            // One value per operation when FRESH (warm-up and every round),
            // parsed up front so the parse is not what is timed.
            let pool =
                if fresh then
                    Array.init (ops * (rounds + 1)) (fun _ -> parseJson (text width))
                else
                    [| parseJson (text width) |]

            let mutable next = 0

            let decodeNext () =
                decodeWideJson decoders pool[next % pool.Length]
                next <- next + 1

            // Checked once before timing: a fixture that refuses measures the refusal.
            decodeWideJson decoders pool[0]

            for _ in 1..ops do
                decodeNext ()

            let s = measure rounds ops decodeNext

            let perField = {
                MinUs = s.MinUs / float width
                MedianUs = s.MedianUs / float width
            }

            Console.Out.WriteLine("| " + string width + " | " + us s + " | " + us perField + " |")

    let byName width =
        List.init width (fun i -> Json.JsonDecode.field ("F" + string i) Json.JsonDecode.asInt32)

    table "JSON object, `field` by name (fields)" false wideJsonObjectText byName
    Console.Out.WriteLine ""
    table "JSON object, `field` by name, a FRESH value per decode (fields)" true wideJsonObjectText byName
    Console.Out.WriteLine ""

    table "JSON array, `index` (elements)" false wideJsonArrayText (fun width ->
        List.init width (fun i -> Json.JsonDecode.index i Json.JsonDecode.asInt32))

/// Per-record and per-field decode cost across widths. LINEAR cost reads
/// as a flat per-field column; quadratic cost as a per-field column that
/// grows with the width. The falsifier is that column: if the per-field
/// figure at 256 fields is not materially above the one at 8, there was
/// no quadratic term to remove.
let private wide (rounds: int) =
    let widths = [ 8; 32; 64; 128; 256 ]

    Console.Out.WriteLine "| Wide record (fields) | whole record, min / median | per field, min / median |"
    Console.Out.WriteLine "|---|---|---|"

    for width in widths do
        let value =
            match Read.Reader(wideRecordBytes width).TryReadValue() with
            | Ok value -> value
            | Error error -> failwith (DecodeError.render error)

        let decoders = wideRecordDecoders width
        // Checked once before timing: a fixture that refuses measures the refusal.
        decodeWide decoders value
        let ops = max 200 (200_000 / width)

        for _ in 1..ops do
            decodeWide decoders value

        let s = measure rounds ops (fun () -> decodeWide decoders value)

        let perField = {
            MinUs = s.MinUs / float width
            MedianUs = s.MedianUs / float width
        }

        Console.Out.WriteLine("| " + string width + " | " + us s + " | " + us perField + " |")

    Console.Out.WriteLine ""
    wideJson rounds

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | [ "--cold"; path; armName ] ->
        Console.Out.WriteLine((cold path armName).ToString("0.000", Globalization.CultureInfo.InvariantCulture))
        0
    | [ "--probe" ] ->
        probe 9
        0
    | [ "--wide" ] ->
        wide 9
        0
    | [] ->
        report 5 9
        Console.Out.WriteLine ""
        wide 9
        0
    | [ "--boots"; b; "--rounds"; r ] ->
        report (int b) (int r)
        Console.Out.WriteLine ""
        wide (int r)
        0
    | _ ->
        Console.Error.WriteLine "usage: ToolUp.Remoting.Benchmarks [--boots N --rounds N] | --probe | --wide"
        2