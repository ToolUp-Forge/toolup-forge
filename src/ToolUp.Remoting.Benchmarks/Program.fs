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
//   of the sample's echo contract. Generated: parse the argument array,
//   the typed argument parse the generator emitted, a direct call of the
//   handler, serialise the result. Reflection: the proxy the adapter builds
//   today, invoked with the same bytes — argument parse through a
//   `MethodInfo` walk over boxed values, handler invocation through the
//   shape visitor, the same serialiser.
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
    /// The generated path: typed parse, direct call, serialise.
    Generated: MemoryStream -> unit
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
        Generated =
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

/// The reflective path: the proxy the adapter builds, invoked as the
/// adapter invokes it (body bytes cached, JSON content type, POST).
let private invokeReflection
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
    | other -> failwithf "reflective dispatch of %s did not succeed: %A" arm.Label other

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
    | "dispatch", "generated" ->
        let arms = dispatchArms fixtures
        use output = new MemoryStream()

        timeOnce (fun () ->
            ICorpusApiDispatch.methods |> List.length |> ignore

            for a in arms do
                a.Generated output)
    | "dispatch", "reflection" ->
        let arms = dispatchArms fixtures
        use output = new MemoryStream()

        timeOnce (fun () ->
            let proxy = Proxy.makeApiProxy options

            for a in arms do
                invokeReflection proxy a output)
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
    let proxy = Proxy.makeApiProxy options
    use output = new MemoryStream()

    let dispatch =
        arms
        |> List.map (fun a ->
            let g, r =
                measurePair rounds 2000 (fun () -> a.Generated output) (fun () -> invokeReflection proxy a output)

            a.Label, g, r)

    let coldDecodeGenerated, wallDecodeGenerated = coldStart boots "decode" "generated"

    let coldDecodeReflection, wallDecodeReflection =
        coldStart boots "decode" "reflection"

    let coldDispatchGenerated, wallDispatchGenerated =
        coldStart boots "dispatch" "generated"

    let coldDispatchReflection, wallDispatchReflection =
        coldStart boots "dispatch" "reflection"

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

    line ""
    line "| Dispatch per request | generated | reflection |"
    line "|---|---|---|"

    for label, g, r in dispatch do
        line ("| " + label + " | " + us g + " | " + us r + " |")

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
        "generated arm, whole", (fun () -> intArm.Generated output)
        "reflective proxy, whole", (fun () -> invokeReflection proxy intArm output)
    ]

    for _, op in stages do
        for _ in 1..500 do
            op ()

    Console.Out.WriteLine "| Stage (`int` echo) | min / median per op |"
    Console.Out.WriteLine "|---|---|"

    for label, op in stages do
        Console.Out.WriteLine("| " + label + " | " + us (measure rounds n op) + " |")

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | [ "--cold"; path; armName ] ->
        Console.Out.WriteLine((cold path armName).ToString("0.000", Globalization.CultureInfo.InvariantCulture))
        0
    | [ "--probe" ] ->
        probe 9
        0
    | [] ->
        report 5 9
        0
    | [ "--boots"; b; "--rounds"; r ] ->
        report (int b) (int r)
        0
    | _ ->
        Console.Error.WriteLine "usage: ToolUp.Remoting.Benchmarks [--boots N --rounds N]"
        2