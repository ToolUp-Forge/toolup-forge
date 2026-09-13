// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 784.B / 784.C — the System.Text.Json half of the remoting wire
/// differential corpus.
///
/// The SAME population as the MsgPack half, through the SAME comparison,
/// which is the whole reason `WireCorpus` declares shapes as data: two
/// suites over two independently-drawn populations would agree about
/// nothing in particular. Here the two wires are held to one contract,
/// and a shape that round-trips on one and not the other is visible as
/// exactly that.
///
/// The converter set under test is the one the server composes
/// (`FableConverters`), taken through `create ()` rather than the
/// process-wide `shared` singleton: the corpus must not be able to
/// perturb the options every other suite in this pack serialises through,
/// and STJ freezes an options object on first use in any case.
///
/// The JSON text is pinned the same way the bytes are, for the same
/// reason and with the same deliberate re-pin gesture. The text pin is
/// the more load-bearing of the two: a JSON wire is read by non-F#
/// clients whose parsers this repository does not own, so "the shape
/// changed but both of our ends agree" is precisely the failure a
/// round-trip-only suite cannot see.
module ToolUp.Platform.Tests.Remoting.StjRoundTripTests

open System
open System.IO
open System.Text.Json
open Expecto
open ToolUp.Platform.Tests.Remoting.WireCorpus

let private roundTrip (c: WireCase) =
    let text = c.WriteJson()

    let decoded =
        try
            Ok(readJson c text)
        with ex ->
            Error(sprintf "the converter threw on read: %s | json: %s" ex.Message text)

    match decoded with
    | Error problem -> Error problem
    | Ok value ->
        match c.Compare value with
        | Ok() -> Ok()
        | Error problem -> Error(sprintf "%s | json: %s" problem text)

let private pinnedRoundTrip =
    testList "pinned" [
        for c in pinnedCases ->
            testCase c.Name
            <| fun () ->
                match roundTrip c with
                | Ok() -> ()
                | Error problem -> failtestf "STJ round trip failed for `%s` (%A): %s" c.Name c.Class problem
    ]

let private generatedRoundTrip =
    testList "generated" [
        for shape in shapeGenerators ->
            testCase shape.ShapeName
            <| fun () ->
                let failing =
                    sizes
                    |> List.tryPick (fun size ->
                        let drawn = shape.Draw (seed ()) size

                        match roundTrip drawn with
                        | Ok() -> None
                        | Error problem -> Some(size, drawn, problem))

                match failing with
                | None -> ()
                | Some(size, drawn, problem) ->
                    let smallest =
                        shrinkSize shape (fun c ->
                            match roundTrip c with
                            | Ok() -> false
                            | Error _ -> true)

                    failtestf
                        "STJ round trip failed for generated shape `%s` at size %d (case `%s`): %s\nSmallest failing size: %s. Reproduce with `$env:%s = '%d'`."
                        shape.ShapeName
                        size
                        drawn.Name
                        problem
                        (match smallest with
                         | Some s -> string s
                         | None ->
                             "none — the failure did not reproduce, which means the draw is not deterministic and THAT is the defect")
                        SeedVariable
                        (seed ())
    ]

// ─── The falsifier and its control ───────────────────────────────────

let private narrowingFalsifier =
    testList "narrowing falsifier" [
        testCase "an int64 payload read at int32 is REPORTED, not accepted"
        <| fun () ->
            let declared = both WireClass.NumericWidth "falsifier-int64" 2147483648L
            let text = declared.WriteJson()

            // The JSON wire is width-free — `2147483648` is just a number
            // — so the narrowing here is STJ's own, and it is the reason
            // this arm exists separately from the MsgPack one: the two
            // wires narrow by different mechanisms and a corpus that
            // only falsified one would be claiming coverage of both.
            let misread =
                try
                    Some(JsonSerializer.Deserialize(text, typeof<int32>, jsonOptions))
                with _ ->
                    None

            match misread with
            | None ->
                // STJ refusing outright is also a correct outcome, and it
                // is recorded as such rather than silently passing: the
                // claim is "the narrowing does not go unnoticed".
                ()
            | Some value ->
                match declared.Compare value with
                | Error _ -> ()
                | Ok() ->
                    failtest
                        "the corpus comparison ACCEPTED an int64 value decoded at int32 from JSON. The type-exact comparison is not doing its job."

        testCase "the control: the same text read at int64 falls silent"
        <| fun () ->
            let declared = both WireClass.NumericWidth "falsifier-int64" 2147483648L
            let text = declared.WriteJson()
            let correct = JsonSerializer.Deserialize(text, typeof<int64>, jsonOptions)

            match declared.Compare correct with
            | Ok() -> ()
            | Error problem ->
                failtestf "the control arm failed: a correctly-read value was reported as a mismatch (%s)." problem
    ]

// ─── The pinned fixture set (784.C, .NET half) ───────────────────────

let private fixturePin =
    testList "pinned fixtures" [
        yield! [
            for c in pinnedCases ->
                testCase (c.Name + " — text is what the converter emits today")
                <| fun () ->
                    let path = jsonFixturePath c
                    let emitted = c.WriteJson()

                    if refreshRequested () then
                        Directory.CreateDirectory(corpusDirectory ()) |> ignore
                        File.WriteAllText(path, emitted)
                    else
                        Expect.isTrue
                            (File.Exists path)
                            (sprintf
                                "no committed fixture at `%s`. Re-pin with `$env:%s = '1'` and commit the result."
                                path
                                RefreshVariable)

                        // `\r` is stripped on read so a CRLF checkout of
                        // a text fixture compares equal — the committed
                        // form is LF, and git may hand back either.
                        let committed = (File.ReadAllText path).Replace("\r", "")

                        if committed <> emitted.Replace("\r", "") then
                            failtestf
                                "the STJ converter set no longer emits the committed text for `%s`.\n  committed: %s\n  emitted:   %s\nIf the change is INTENDED, re-pin with `$env:%s = '1'` and review the diff — a JSON shape change is visible to every non-F# client of this wire."
                                c.Name
                                committed
                                emitted
                                RefreshVariable
        ]

        yield! [
            for c in pinnedCases ->
                testCase (c.Name + " — the committed text decodes to the declared value")
                <| fun () ->
                    let path = jsonFixturePath c

                    if File.Exists path then
                        let committed = File.ReadAllText path

                        match c.Compare(readJson c committed) with
                        | Ok() -> ()
                        | Error problem ->
                            failtestf
                                "the committed JSON fixture for `%s` does not decode to the declared value: %s"
                                c.Name
                                problem
                    else
                        failtestf "no committed fixture at `%s`." path
        ]

        testCase "no orphan fixtures"
        <| fun () ->
            let dir = corpusDirectory ()

            if Directory.Exists dir then
                let expected = pinnedCases |> List.map (fun c -> c.Name + ".json") |> Set.ofList

                let orphans =
                    Directory.GetFiles(dir, "*.json")
                    |> Array.map Path.GetFileName
                    |> Array.filter (fun f -> not (expected.Contains f))

                Expect.isEmpty
                    orphans
                    (sprintf "JSON fixture file(s) with no declared case: %s." (String.Join(", ", orphans)))
    ]

// ─── A measured divergence between the two wires (784.B) ─────────────

/// Found by this corpus on its first run, 2026-09-13, on a generated
/// envelope at size 0: a `TimeSpan` of `14:13:31.2158396` came back as
/// `14:13:31.2158395`. One tick.
///
/// The STJ remoting converter writes a `TimeSpan` as a JSON NUMBER of
/// milliseconds (`"Window":51211215.8396`). At tick resolution that is
/// four decimal places of a double, and a double cannot hold every such
/// value exactly, so the reconstruction rounds down by a tick. The
/// MsgPack wire writes `TimeSpan.Ticks` as an int64 and is exact.
///
/// **This is the differential the phase exists to produce**: neither wire
/// disagrees with ITSELF — a round trip is self-consistent on each — and
/// only holding both to one declared population makes the difference
/// visible. It is recorded here rather than papered over, and the
/// generated draws are restricted to whole milliseconds with a pointer
/// back to this test (see `WireCorpus.randomEnvelope`).
///
/// The assertion is a SEARCH rather than one magic tick value, because a
/// hard-coded constant that stopped reproducing would fall silent instead
/// of going red. Three claims, all falsifiable:
///
///   1. at least one drawn tick value loses precision through STJ — if
///      this stops being true the converter has been fixed, and the right
///      response is to delete this test and widen the generated draws;
///   2. no loss exceeds one tick — the measured bound, so a WORSE
///      converter is a red run rather than a quietly-tolerated one;
///   3. every one of the same values is exact through MsgPack — the
///      control, without which "JSON loses ticks" could equally be "our
///      corpus mis-declares TimeSpans".
let private timeSpanTickLoss =
    testCase "TimeSpan: the JSON wire loses up to one tick, the MsgPack wire does not"
    <| fun () ->
        // Deterministic tick values at full resolution, spread across a
        // day. Derived from the corpus seed so the population is the same
        // on every machine.
        let ticks = [ for i in 1..2000 -> (int64 i * 6_151_231_237L) % 863_999_999_999L ]

        let lossy =
            ticks
            |> List.choose (fun t ->
                let original = TimeSpan.FromTicks t
                let c = both WireClass.DateFamily "timespan-probe" original
                let decoded = readJson c (c.WriteJson()) :?> TimeSpan
                let delta = abs (decoded.Ticks - t)
                if delta = 0L then None else Some(t, delta))

        Expect.isNonEmpty
            lossy
            "no drawn TimeSpan lost precision through the STJ wire. Either the converter has been fixed — in which case delete this test and widen the whole-millisecond restriction on the generated draws in `WireCorpus.randomEnvelope` — or this probe has stopped measuring the wire."

        let worst = lossy |> List.map snd |> List.max

        printfn
            "STJ TimeSpan precision: %d of %d drawn tick values lose precision, worst %d tick(s); e.g. %d ticks"
            (List.length lossy)
            (List.length ticks)
            worst
            (fst lossy.Head)

        Expect.isTrue
            (worst <= 1L)
            (sprintf
                "the STJ TimeSpan loss is now %d ticks, worse than the 1-tick bound measured 2026-09-13. The converter's millisecond-as-double encoding has degraded further; this is a regression, not the recorded divergence."
                worst)

        // The control. Same values, the other wire.
        let msgPackLossy =
            ticks
            |> List.filter (fun t ->
                let c = both WireClass.DateFamily "timespan-probe" (TimeSpan.FromTicks t)
                let decoded = readMsgPack c (c.WriteMsgPack()) :?> TimeSpan
                decoded.Ticks <> t)

        Expect.isEmpty
            msgPackLossy
            (sprintf
                "%d drawn TimeSpan value(s) also lose precision through the MsgPack wire, which writes ticks as an int64 and should be exact. The divergence recorded here is then not a JSON-side defect but something wider."
                (List.length msgPackLossy))

/// The differential claim itself, stated as a test rather than left
/// implicit in the existence of two suites: both wires are exercised over
/// the SAME case list, so neither can quietly cover a different
/// population than the other.
let private differentialShape =
    testCase "both wires run the same population"
    <| fun () ->
        Expect.isNonEmpty pinnedCases "the pinned population is empty; both suites are vacuously green."

        Expect.isNonEmpty
            (shapeGenerators)
            "no generated shapes are declared; the generated arm of both suites is vacuously green."

        printfn "%s" (adequacyReport "STJ population (pinned + generated)" (pinnedCases @ generatedCases ()))

[<Tests>]
let tests =
    testList "Remoting STJ wire corpus" [
        pinnedRoundTrip
        generatedRoundTrip
        narrowingFalsifier
        fixturePin
        timeSpanTickLoss
        differentialShape
    ]