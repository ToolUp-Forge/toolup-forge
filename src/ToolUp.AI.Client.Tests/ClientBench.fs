// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 849 — the browser runtime, measured.
///
/// Every performance number the repository held before this module was a
/// .NET number. The path a user actually waits on — the transpiled Elmish
/// loop, the remoting proxy's response decode, the boot that builds every
/// proxy by reflection at module import — runs as JavaScript, and nothing
/// timed it. This harness measures it under Node, as Fable transpiles it,
/// so each later phase on this road ships with a before and an after
/// rather than a reading of the code.
///
/// ─── What is measured ────────────────────────────────────────────────
///
///   * BOOT — wall-clock from `import()` of the transpiled
///     `samples/MinimalClient` entry module to its first commit into the
///     placeholder element, in a FRESH Node process per sample (a module
///     graph evaluates once per process, so a warm loop cannot see it).
///     Each boot also reports how many remoting proxies were built during
///     it; a second, import-only leg does the same for the SDK shell's
///     module graph (`SDK.Client`), which is where the platform's
///     module-level `Api.makeProxy` sites live. The boot child is
///     `client-bench-boot.mjs`; its proxy counter is installed by
///     `client-bench-loader.mjs`.
///   * DECODE PER RESPONSE — the proxy's 200 path exactly
///     (`SimpleJson.parseNative` then `Convert.fromJsonAs` over the
///     declared type's `TypeInfo`) over the remoting wire corpus's `.json`
///     fixtures, beside `SimpleJson.parseNative` alone and `JSON.parse`
///     alone, so the reflective walk is isolated. These are the bytes the
///     .NET benchmark (`ToolUp.Remoting.Benchmarks`) reads, so the two
///     hosts measure the same responses. Every decode is CHECKED against
///     the fixture's declared value first; a fixture that does not decode
///     to it is EXCLUDED with its reason printed, never timed.
///   * DISPATCH TO RENDER HOOK — a model-changing message dispatched
///     through `Program.runWithDispatch` to the render hook
///     (`Program.withSetState`), timing the `view` construction per
///     message; the number of render-hook calls one drain of several
///     messages makes; and the latency between an update issuing a
///     `Cmd.OfAsync` command and that async body starting.
///
/// ─── Discipline ──────────────────────────────────────────────────────
///
/// The statistic is MIN over rounds, as the server perf gate's is
/// (Phase 192): wall-clock noise on a shared machine is one-sided. The
/// fixture order and the dispatch sequence come from the proof corpus's
/// LCG (`ElmishProofDifferential.Lcg`) under one seed, printed on every
/// run, so a run is reproducible by seed. Node is not a browser: decode
/// and view construction are the same JavaScript a browser runs; boot's
/// import cost transfers, its paint does not (jsdom commits the DOM and
/// lays nothing out). The runtime and version are printed beside every
/// number for that reason.
///
/// ─── How it runs ─────────────────────────────────────────────────────
///
/// NOT in the ordinary test run: `Program.fs` enters here only when the
/// argument `ClientBench` is present, which `node --test` never passes.
///
///   node --import ./register-loader.mjs output/Program.js ClientBench
///        [--out <measurements.json>] [--seed <n>] [--boot-samples <n>]
///        [--rounds <n>]
///
/// With `--out`, the run is written as a `toolup.perf-measurements/v1`
/// document the `VerifyClientPerfBudget` target decides against the
/// `client` block of `perf-budgets.json`.
module ToolUp.AI.Client.Tests.ClientBench

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.SimpleJson
open Feliz
open ToolUp.Elmish
open ToolUp.Platform.Tests.Remoting
open ToolUp.Platform.Tests.Client.ElmishProofDifferential

// ─── Host surface ──────────────────────────────────────────────────────

[<Emit("performance.now()")>]
let private now () : float = jsNative

[<Emit("JSON.parse($0)")>]
let private jsonParse (text: string) : obj = jsNative

[<Emit("JSON.stringify($0, null, 2)")>]
let private jsonStringify (value: obj) : string = jsNative

[<Emit("process.version")>]
let private nodeVersion () : string = jsNative

[<Emit("process.versions.v8")>]
let private v8Version () : string = jsNative

[<Emit("process.platform + '/' + process.arch")>]
let private platform () : string = jsNative

[<Emit("process.env.NODE_ENV")>]
let private nodeEnv () : string = jsNative

[<Emit("process.execPath")>]
let private execPath () : string = jsNative

[<Emit("process.exitCode = $0")>]
let private setExitCode (code: int) : unit = jsNative

[<Emit("new URL($0, import.meta.url)")>]
let private beside (relative: string) : obj = jsNative

[<Import("fileURLToPath", from = "node:url")>]
let private fileURLToPath (url: obj) : string = jsNative

[<Import("existsSync", from = "node:fs")>]
let private existsSync (path: obj) : bool = jsNative

[<Import("readFileSync", from = "node:fs")>]
let private readFileSync (path: obj, encoding: string) : string = jsNative

[<Import("writeFileSync", from = "node:fs")>]
let private writeFileSync (path: string, data: string) : unit = jsNative

[<Import("mkdirSync", from = "node:fs")>]
let private mkdirSync (path: string, options: obj) : unit = jsNative

[<Import("dirname", from = "node:path")>]
let private dirname (path: string) : string = jsNative

[<Import("resolve", from = "node:path")>]
let private resolvePath (path: string) : string = jsNative

[<Import("spawnSync", from = "node:child_process")>]
let private spawnSync (command: string, args: string[], options: obj) : obj = jsNative

// ─── Arguments and statistics ──────────────────────────────────────────

/// The default seed. Any run prints the seed it used; `--seed` replays it.
[<Literal>]
let DefaultSeed = 849_001

type private Options = {
    Out: string option
    Seed: int
    BootSamples: int
    Rounds: int
}

let private parseOptions (argv: string[]) : Options =
    let valueOf (flag: string) =
        argv
        |> Array.tryFindIndex ((=) flag)
        |> Option.bind (fun i -> if i + 1 < argv.Length then Some argv[i + 1] else None)

    let intOf flag fallback =
        match valueOf flag with
        | Some v ->
            match Int32.TryParse v with
            | true, n when n > 0 -> n
            | _ -> failwithf "ClientBench: %s expects a positive integer, got '%s'" flag v
        | None -> fallback

    {
        Out = valueOf "--out"
        Seed = intOf "--seed" DefaultSeed
        BootSamples = intOf "--boot-samples" 6
        Rounds = intOf "--rounds" 9
    }

type private Stats = {
    Min: float
    Median: float
    Max: float
    Count: int
}

let private stats (values: float list) : Stats option =
    match List.sort values with
    | [] -> None
    | sorted ->
        let arr = Array.ofList sorted

        Some {
            Min = arr[0]
            Median = arr[arr.Length / 2]
            Max = arr[arr.Length - 1]
            Count = arr.Length
        }

let private round3 (v: float) = Math.Round(v, 3)

/// A statistic for the measurement document's `levers` block: the rounded
/// number, or JSON `null` when the leg measured nothing.
let private optMin (s: Stats option) : obj =
    match s with
    | Some s -> box (round3 s.Min)
    | None -> null

let private optMedian (s: Stats option) : obj =
    match s with
    | Some s -> box (round3 s.Median)
    | None -> null

let private fmt (v: float) = v.ToString("0.###")

/// One sample in the measurement document's shape (`toolup.perf-measurements/v1`).
let private sample (metric: string) (unit: string) (s: Stats option) (observed: bool) (evidence: string) =
    let v f =
        s |> Option.map (f >> round3) |> Option.defaultValue 0.0

    createObj [
        "metric" ==> metric
        "unit" ==> unit
        "statistic" ==> "min"
        "value" ==> v _.Min
        "samples" ==> (s |> Option.map _.Count |> Option.defaultValue 0)
        "observed" ==> (observed && Option.isSome s)
        "evidence" ==> evidence
        "median" ==> v _.Median
        "max" ==> v _.Max
    ]

/// Keeps a computed value reachable so no engine can discard the work that
/// produced it as dead.
let mutable private sink: obj = null

// ─── Boot ──────────────────────────────────────────────────────────────

/// The test project's directory: the transpiled module sits in `output/`.
let private testDir () = fileURLToPath (beside "../")

/// The transpiled modules the boot child imports, resolved relative to THIS
/// module (the Phase 613 precedent) and checked to exist — a moved output
/// layout must fail loudly, never time the import of nothing.
let private minimalClientModule = "./samples/MinimalClient/Client.js"

let private shellModule = "./ToolUp.Platform.Client/Client/SDK.Client.js"

type private BootChild = {
    Ok: bool
    ImportMs: float
    RenderMs: float option
    ProxiesBuilt: int
    /// Phase 853 — `Api.makeProxy` resolutions a GENERATED proxy served.
    ProxiesGenerated: int
    /// Phase 853 — proxies whose reflective build waits for a first call.
    ProxiesDeferred: int
    CounterState: string
    Detail: string
}

let private runBootChild (moduleRelative: string) (mode: string) : BootChild =
    let target = beside moduleRelative

    if not (existsSync target) then
        failwithf
            "ClientBench: the transpiled module '%s' does not exist beside the harness. The Fable output layout moved; fix the path in ClientBench.fs rather than timing nothing."
            moduleRelative

    let result =
        spawnSync (
            execPath (),
            [|
                "--import"
                "./register-loader.mjs"
                "client-bench-boot.mjs"
                string (target?href)
                mode
            |],
            createObj [
                "cwd" ==> testDir ()
                "encoding" ==> "utf8"
                "timeout" ==> 120000
                "maxBuffer" ==> 64 * 1024 * 1024
            ]
        )

    let stdout: string = if isNull result?stdout then "" else result?stdout
    let stderr: string = if isNull result?stderr then "" else result?stderr

    let report =
        stdout.Split('\n')
        |> Array.map _.Trim()
        |> Array.tryFindBack _.StartsWith("CLIENTBENCH-BOOT ")

    match report with
    | None ->
        let tail (s: string) =
            if s.Length > 1500 then s.Substring(s.Length - 1500) else s

        {
            Ok = false
            ImportMs = 0.0
            RenderMs = None
            ProxiesBuilt = 0
            ProxiesGenerated = 0
            ProxiesDeferred = 0
            CounterState = "no report"
            Detail = sprintf "the boot child printed no report (exit %O). stderr tail: %s" result?status (tail stderr)
        }
    | Some line ->
        let r = jsonParse (line.Substring "CLIENTBENCH-BOOT ".Length)
        let error: string = r?error

        {
            Ok = isNull error
            ImportMs = r?importMs
            RenderMs =
                if isNull r?renderMs then
                    None
                else
                    Some(unbox<float> r?renderMs)
            ProxiesBuilt = r?proxiesBuilt
            ProxiesGenerated = r?proxiesGenerated
            ProxiesDeferred = r?proxiesDeferred
            CounterState = r?counterState
            Detail = if isNull error then "" else error
        }

// ─── Decode per response ───────────────────────────────────────────────

type private DecodeFixture = {
    Name: string
    Text: string
    Info: TypeInfo
}

[<Literal>]
let private CorpusDir = "../../../tests/remoting-corpus/"

/// The floor below which the decode leg measured too little to mean
/// anything (the parity pack's own non-vacuity floor is 40 declared cases).
[<Literal>]
let DecodeFixtureFloor = 20

let private decodeFull (f: DecodeFixture) =
    Convert.fromJsonAs (SimpleJson.parseNative f.Text) f.Info

/// Every cross-host corpus case, checked through the proxy's decode path
/// against its declared value. Included ones are timed; excluded ones carry
/// the reason they were not.
let private loadDecodeFixtures () =
    let included = ResizeArray<DecodeFixture>()
    let excluded = ResizeArray<string * string>()

    for c in WireCorpus.crossHostCases do
        let path = beside (CorpusDir + c.Name + ".json")

        if not (existsSync path) then
            excluded.Add(c.Name, "no .json fixture beside the .msgpack one")
        else
            let fixture = {
                Name = c.Name
                Text = readFileSync (path, "utf8")
                Info = createTypeInfo c.ClrType
            }

            try
                match c.Compare(decodeFull fixture) with
                | Ok() -> included.Add fixture
                | Error problem -> excluded.Add(c.Name, problem)
            with ex ->
                excluded.Add(c.Name, "decode threw: " + ex.Message)

    List.ofSeq included, List.ofSeq excluded

/// `rounds` rounds; each runs `op` over every fixture `passes` times in an
/// LCG-shuffled order and yields microseconds per response.
let private perResponseRounds
    (rng: Lcg)
    (rounds: int)
    (passes: int)
    (fixtures: DecodeFixture list)
    (op: DecodeFixture -> obj)
    =
    let arr = Array.ofList fixtures

    [
        for _ in 1..rounds do
            // Fisher–Yates under the seeded LCG: the order is part of the run.
            for i in arr.Length - 1 .. -1 .. 1 do
                let j = rng.Next(i + 1)
                let t = arr[i]
                arr[i] <- arr[j]
                arr[j] <- t

            let start = now ()

            for _ in 1..passes do
                for f in arr do
                    sink <- op f

            (now () - start) * 1000.0 / float (passes * arr.Length)
    ]

/// Per-fixture min over rounds, for the breakdown a human reads.
let private perFixture (rounds: int) (ops: int) (f: DecodeFixture) (op: DecodeFixture -> obj) =
    [
        for _ in 1..rounds do
            let start = now ()

            for _ in 1..ops do
                sink <- op f

            (now () - start) * 1000.0 / float ops
    ]
    |> List.min

// ─── Dispatch to render hook ───────────────────────────────────────────

type private Row = { Id: int; Label: string; Value: int }

type private BenchModel = { Rows: Row[]; Tick: int }

type private BenchMsg =
    | Bump of index: int
    | Burst of count: int
    | Step
    | StartAsync
    | AsyncDone

/// A representative module-sized view: a table of rows, each with a text
/// cell, a value cell and a button carrying a dispatch closure.
let private benchView (model: BenchModel) (dispatch: BenchMsg -> unit) : ReactElement =
    Html.div [
        prop.className "bench"
        prop.children [
            Html.h2 [ prop.text (sprintf "Tick %d" model.Tick) ]
            Html.table [
                Html.tbody [
                    for i in 0 .. model.Rows.Length - 1 do
                        let row = model.Rows[i]

                        Html.tr [
                            prop.key row.Id
                            prop.children [
                                Html.td [ prop.text row.Label ]
                                Html.td [ prop.text (string row.Value) ]
                                Html.td [ Html.button [ prop.text "+"; prop.onClick (fun _ -> dispatch (Bump i)) ] ]
                            ]
                        ]
                ]
            ]
        ]
    ]

type private LoopProbe() =
    member val RenderCalls = 0 with get, set
    member val ViewMs = 0.0 with get, set
    member val Dispatch: (BenchMsg -> unit) option = None with get, set
    member val AsyncIssuedAt = 0.0 with get, set
    member val AsyncStartedAt = 0.0 with get, set
    member val OnAsyncDone: (unit -> unit) option = None with get, set

[<Literal>]
let BenchRows = 200

[<Literal>]
let DrainBurst = 16

[<Literal>]
let AsyncHops = 20

let private startLoop (rng: Lcg) (probe: LoopProbe) =
    let rows =
        Array.init BenchRows (fun i -> {
            Id = i
            Label = sprintf "row-%04d" (rng.Next 10000)
            Value = rng.Next 1000
        })

    let init () = { Rows = rows; Tick = 0 }, Cmd.none

    let update (msg: BenchMsg) (model: BenchModel) =
        match msg with
        | Bump i ->
            let rows' = Array.copy model.Rows

            rows'[i] <- {
                rows'[i] with
                    Value = rows'[i].Value + 1
            }

            {
                model with
                    Rows = rows'
                    Tick = model.Tick + 1
            },
            Cmd.none
        | Burst n -> { model with Tick = model.Tick + 1 }, Cmd.batch [ for _ in 1..n -> Cmd.ofMsg Step ]
        | Step -> { model with Tick = model.Tick + 1 }, Cmd.none
        | StartAsync ->
            probe.AsyncIssuedAt <- now ()

            model, Cmd.OfAsync.perform (fun () -> async { probe.AsyncStartedAt <- now () }) () (fun () -> AsyncDone)
        | AsyncDone ->
            probe.OnAsyncDone |> Option.iter (fun k -> k ())
            model, Cmd.none

    // The render hook: exactly what `withReactSynchronous` does minus the
    // DOM — build the view — timed, and counted.
    let setState (model: BenchModel) (dispatch: BenchMsg -> unit) =
        probe.Dispatch <- Some dispatch
        probe.RenderCalls <- probe.RenderCalls + 1
        let start = now ()
        sink <- box (benchView model dispatch)
        probe.ViewMs <- probe.ViewMs + (now () - start)

    Program.mkProgram init update benchView
    |> Program.withSetState setState
    |> Program.runWithDispatch id ()

// ─── The run ───────────────────────────────────────────────────────────

let private runAsync (argv: string[]) : Async<int> = async {
    let options = parseOptions argv
    let rng = Lcg options.Seed
    // React selects its build from NODE_ENV when it is first imported, which
    // is before this function runs. A browser bundle runs the PRODUCTION
    // build; the development build validates every element and is several
    // times slower, so a view or boot number taken under it is not comparable
    // with the baseline and is refused as unobserved rather than recorded.
    let reactProduction = nodeEnv () = "production"
    let reactBuild = if reactProduction then "production" else "development"

    let runtime =
        sprintf "node %s (v8 %s) %s, React %s build" (nodeVersion ()) (v8Version ()) (platform ()) reactBuild

    let buildEvidence =
        if reactProduction then
            ""
        else
            " — REFUSED: NODE_ENV is not 'production', so React ran its development build; set NODE_ENV=production (the gate does)"

    let say (text: string) = printfn "[client-bench] %s" text

    say (sprintf "runtime %s · seed %d" runtime options.Seed)

    if not reactProduction then
        say (
            "NODE_ENV is not 'production': boot and view numbers are refused"
            + buildEvidence
        )

    // ── Boot ──
    let boots = [
        for _ in 1 .. options.BootSamples -> runBootChild minimalClientModule "render"
    ]

    let rendered = boots |> List.filter (fun b -> b.Ok && Option.isSome b.RenderMs)
    let bootStats = rendered |> List.choose _.RenderMs |> stats
    let bootProxies = boots |> List.map _.ProxiesBuilt |> List.distinct
    let bootGenerated = boots |> List.map _.ProxiesGenerated |> List.distinct
    let bootDeferred = boots |> List.map _.ProxiesDeferred |> List.distinct
    let bootCounter = boots |> List.map _.CounterState |> List.distinct

    for b in boots |> List.filter (fun b -> not b.Ok || Option.isNone b.RenderMs) do
        say (sprintf "boot NOT observed: %s" (if b.Detail = "" then "no first render" else b.Detail))

    let bootEvidence =
        sprintf
            "first render into #elmish-app observed in %d/%d fresh-process boots of samples/MinimalClient"
            rendered.Length
            boots.Length

    match bootStats with
    | Some s ->
        say (
            sprintf
                "boot (samples/MinimalClient, import -> first render): min %s ms  median %s ms  max %s ms  (n=%d) · proxies built by reflection during boot: %A, generated: %A, deferred to first call: %A (counter: %s)"
                (fmt s.Min)
                (fmt s.Median)
                (fmt s.Max)
                s.Count
                bootProxies
                bootGenerated
                bootDeferred
                (String.Join(", ", bootCounter))
        )
    | None -> say "boot: NO boot reached a first render"

    let shells = [ for _ in 1..3 -> runBootChild shellModule "import" ]
    let shellStats = shells |> List.filter _.Ok |> List.map _.ImportMs |> stats
    let shellProxies = shells |> List.map _.ProxiesBuilt |> List.distinct
    let shellGenerated = shells |> List.map _.ProxiesGenerated |> List.distinct
    let shellDeferred = shells |> List.map _.ProxiesDeferred |> List.distinct
    let shellCounter = shells |> List.map _.CounterState |> List.distinct

    for b in shells |> List.filter (fun b -> not b.Ok) do
        say (sprintf "shell import failed: %s" b.Detail)

    match shellStats with
    | Some s ->
        say (
            sprintf
                "shell import (ToolUp.Platform.Client SDK.Client module graph, no render): min %s ms (n=%d) · proxies built by reflection at import: %A, generated: %A, deferred to first call: %A (counter: %s)"
                (fmt s.Min)
                s.Count
                shellProxies
                shellGenerated
                shellDeferred
                (String.Join(", ", shellCounter))
        )
    | None -> say "shell import: NOT observed"

    // ── Decode per response ──
    let fixtures, excluded = loadDecodeFixtures ()
    let passes = 20

    // Warm every arm once so the rounds time the JIT's settled code.
    for op in
        [
            decodeFull
            (fun f -> box (SimpleJson.parseNative f.Text))
            (fun f -> jsonParse f.Text)
        ] do
        perResponseRounds rng 2 passes fixtures op |> ignore

    let fullRounds = perResponseRounds rng options.Rounds passes fixtures decodeFull

    let nativeRounds =
        perResponseRounds rng options.Rounds passes fixtures (fun f -> box (SimpleJson.parseNative f.Text))

    let parseRounds =
        perResponseRounds rng options.Rounds passes fixtures (fun f -> jsonParse f.Text)

    let decodeStats = stats fullRounds
    let decodeObserved = List.length fixtures >= DecodeFixtureFloor

    let decodeEvidence =
        sprintf
            "%d corpus fixture(s) decoded through parseNative + Convert.fromJsonAs and matched their declared value before timing (%d excluded; floor %d)"
            (List.length fixtures)
            (List.length excluded)
            DecodeFixtureFloor

    let minOf xs =
        stats xs |> Option.map (_.Min >> fmt) |> Option.defaultValue "-"

    say (
        sprintf
            "decode per response: Convert.fromJsonAs path %s us · SimpleJson.parseNative alone %s us · JSON.parse alone %s us  (min over %d rounds, %d fixtures)"
            (minOf fullRounds)
            (minOf nativeRounds)
            (minOf parseRounds)
            options.Rounds
            (List.length fixtures)
    )

    for f in fixtures do
        let full = perFixture options.Rounds 200 f decodeFull
        let parse = perFixture options.Rounds 200 f (fun f -> jsonParse f.Text)

        say (
            sprintf
                "   %-32s full %8s us   JSON.parse %8s us   (%s x)"
                f.Name
                (fmt full)
                (fmt parse)
                (if parse > 0.0 then (full / parse).ToString("0.0") else "-")
        )

    for name, reason in excluded do
        say (sprintf "   excluded %s: %s" name reason)

    if not decodeObserved then
        say (
            sprintf
                "decode: only %d fixture(s) decoded — below the floor of %d"
                (List.length fixtures)
                DecodeFixtureFloor
        )

    // ── Phase 853 — serialise per call ──
    // The request body a call builds, both ways, over the Phase 853 encoder
    // fixture: the reflective proxy's per-call `Convert.serialize` through
    // the tuple TypeInfo wrapper it rebuilds per call (its argument
    // `TypeInfo` is built once per proxy, so it is built once here), and
    // the generated proxy's `JsonEncode.arguments` over the generated
    // encoder. Each is CHECKED to decode to the value first.
    let encodeCases =
        ToolUp.Platform.Tests.Remoting.ClientEncoderFixture.cases
        |> List.map (fun c -> c, createTypeInfo c.ValueType)

    for (c, _) in encodeCases do
        match c.DecodesToValue(c.Encode()) with
        | Ok() -> ()
        | Error e -> failwithf "ClientBench: the generated encoder for %s does not round-trip: %s" c.Name e

    let encodeRounds (op: ToolUp.Platform.Tests.Remoting.ClientEncoderFixture.EncoderCase * TypeInfo -> obj) = [
        for _ in 1 .. options.Rounds do
            let start = now ()

            for _ in 1..passes do
                for case in encodeCases do
                    sink <- op case

            (now () - start) * 1000.0 / float (passes * List.length encodeCases)
    ]

    let reflectiveEncode (c: ToolUp.Platform.Tests.Remoting.ClientEncoderFixture.EncoderCase, info: TypeInfo) =
        box (Convert.serialize c.Value (TypeInfo.Tuple(fun _ -> [| info |])))

    let generatedEncode (c: ToolUp.Platform.Tests.Remoting.ClientEncoderFixture.EncoderCase, _: TypeInfo) =
        box ("[" + c.Encode() + "]")

    encodeRounds reflectiveEncode |> ignore
    encodeRounds generatedEncode |> ignore
    let reflectiveEncodeRounds = encodeRounds reflectiveEncode
    let generatedEncodeRounds = encodeRounds generatedEncode

    say (
        sprintf
            "serialise per call: reflective Convert.serialize %s us · generated encoder %s us  (min over %d rounds, %d fixture argument(s))"
            (minOf reflectiveEncodeRounds)
            (minOf generatedEncodeRounds)
            options.Rounds
            (List.length encodeCases)
    )

    // ── Dispatch to render hook ──
    let probe = LoopProbe()
    startLoop rng probe

    let dispatch =
        match probe.Dispatch with
        | Some d -> d
        | None -> failwith "ClientBench: the program never called its render hook at boot"

    let messagesPerRound = 200

    // Warm-up round.
    for _ in 1..messagesPerRound do
        dispatch (Bump(rng.Next BenchRows))

    let viewRounds, hookCounts =
        [
            for _ in 1 .. options.Rounds do
                probe.RenderCalls <- 0
                probe.ViewMs <- 0.0

                for _ in 1..messagesPerRound do
                    dispatch (Bump(rng.Next BenchRows))

                probe.ViewMs * 1000.0 / float messagesPerRound, probe.RenderCalls
        ]
        |> List.unzip

    let viewStats = stats viewRounds
    let viewObserved = hookCounts |> List.forall ((=) messagesPerRound)

    let viewEvidence =
        sprintf
            "render hook called once per dispatched message in every round (%d messages x %d rounds, %d-row view): %b"
            messagesPerRound
            options.Rounds
            BenchRows
            viewObserved

    say (
        sprintf
            "view per dispatch: %s us (min over %d rounds of %d messages, %d-row table) · render-hook calls per round: %A"
            (viewStats |> Option.map (_.Min >> fmt) |> Option.defaultValue "-")
            options.Rounds
            messagesPerRound
            BenchRows
            (List.distinct hookCounts)
    )

    probe.RenderCalls <- 0
    dispatch (Burst DrainBurst)
    let hooksPerDrain = probe.RenderCalls

    say (
        sprintf
            "render-hook calls per drain: %d for one drain of %d messages (1 dispatched + %d Cmd.ofMsg)"
            hooksPerDrain
            (DrainBurst + 1)
            DrainBurst
    )

    // ── The async command hop ──
    let hopOnce () =
        Async.FromContinuations(fun (ok, _, _) ->
            probe.OnAsyncDone <-
                Some(fun () ->
                    probe.OnAsyncDone <- None
                    ok (probe.AsyncStartedAt - probe.AsyncIssuedAt))

            dispatch StartAsync)

    let! hops = async {
        let acc = ResizeArray<float>()

        for _ in 1..AsyncHops do
            let! h = hopOnce ()
            acc.Add h

        return List.ofSeq acc
    }

    let hopStats = stats hops

    match hopStats with
    | Some s ->
        say (
            sprintf
                "async command hop (update issues Cmd.OfAsync -> async body starts): min %s ms  median %s ms  max %s ms  (n=%d)"
                (fmt s.Min)
                (fmt s.Median)
                (fmt s.Max)
                s.Count
        )
    | None -> ()

    // ── The measurement document ──
    let samples = [|
        sample
            "bootMs"
            "ms"
            bootStats
            (reactProduction && rendered.Length = boots.Length)
            (bootEvidence + buildEvidence)
        sample "decodePerResponseUs" "us" decodeStats decodeObserved decodeEvidence
        sample "viewPerDispatchUs" "us" viewStats (reactProduction && viewObserved) (viewEvidence + buildEvidence)
    |]

    let levers =
        createObj [
            "proxiesBuiltAtMinimalClientBoot" ==> Array.ofList bootProxies
            "proxyCounterAtMinimalClientBoot" ==> Array.ofList bootCounter
            "proxiesBuiltAtShellImport" ==> Array.ofList shellProxies
            "generatedProxiesAtMinimalClientBoot" ==> Array.ofList bootGenerated
            "deferredProxiesAtMinimalClientBoot" ==> Array.ofList bootDeferred
            "generatedProxiesAtShellImport" ==> Array.ofList shellGenerated
            "deferredProxiesAtShellImport" ==> Array.ofList shellDeferred
            "proxyCounterAtShellImport" ==> Array.ofList shellCounter
            "shellImportMsMin" ==> (shellStats |> optMin)
            "serialisePerCallReflectiveUsMin" ==> (stats reflectiveEncodeRounds |> optMin)
            "serialisePerCallGeneratedUsMin" ==> (stats generatedEncodeRounds |> optMin)
            "decodeParseNativeUsMin" ==> (stats nativeRounds |> optMin)
            "decodeJsonParseUsMin" ==> (stats parseRounds |> optMin)
            "decodeFixturesExcluded" ==> (excluded |> List.map fst |> Array.ofList)
            "renderHookCallsPerDrain" ==> hooksPerDrain
            "drainMessages" ==> DrainBurst + 1
            "asyncHopMsMin" ==> (hopStats |> optMin)
            "asyncHopMsMedian" ==> (hopStats |> optMedian)
        ]

    let document =
        createObj [
            "schema" ==> "toolup.perf-measurements/v1"
            "label"
            ==> sprintf "ClientBench — %d boots, %d rounds, seed %d" options.BootSamples options.Rounds options.Seed
            "appDirectory" ==> resolvePath (fileURLToPath (beside "./"))
            "runtime" ==> runtime
            "seed" ==> options.Seed
            "outputAssemblies" ==> [||]
            "samples" ==> samples
            "levers" ==> levers
        ]

    match options.Out with
    | Some path ->
        let full = resolvePath path
        mkdirSync (dirname full, createObj [ "recursive" ==> true ])
        writeFileSync (full, jsonStringify document)
        say (sprintf "measurements written to %s" full)
    | None -> ()

    let observed =
        reactProduction
        && rendered.Length = boots.Length
        && decodeObserved
        && viewObserved

    if not observed then
        say "one or more measurements were NOT observed — see the lines above"

    return (if observed then 0 else 1)
}

/// The `ClientBench` entry: runs every leg and sets the process exit code
/// (0 when every budgeted measurement was observed; deciding the numbers
/// against a budget is `VerifyClientPerfBudget`'s job, not this one's).
let run (argv: string[]) : unit =
    Async.StartWithContinuations(
        runAsync argv,
        setExitCode,
        (fun ex ->
            eprintfn "[client-bench] failed: %s" ex.Message
            setExitCode 2),
        ignore
    )