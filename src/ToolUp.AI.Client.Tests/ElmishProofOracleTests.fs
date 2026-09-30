// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 788 — the proved Elmish runtime as oracle, under Fable.
///
/// The .NET pack (`ToolUp.Platform.Tests/InProcess/ElmishProofOracleTests.fs`)
/// runs the extracted `proofs/ElmishRing.fst` / `ElmishSub.fst` models
/// live beside the production `RingBuffer` and `Sub.Internal.diff`, and
/// writes the model's verdicts for its generated campaign to
/// `tests/elmish-proof-corpus/` — every case carrying its inputs and the
/// outputs the proved model produced. This pack replays that corpus
/// against the TRANSPILED `Ring.fs` / `Sub.fs`, the copy every browser
/// client actually executes, through the same host-neutral drivers
/// (`ElmishProofDifferential.fs`, compiled into both packs).
///
/// ─── The corpus, and — since Phase 850 — the extraction itself ───────
///
/// Until Phase 850 the extraction compiled on .NET only: the F* extractor
/// emits pre-F#-8 layout that needed `--strict-indentation-`, which Fable
/// reads from no fsproj, so the corpus carried the model's ANSWER instead
/// of the model. Phase 850's layout normaliser (`proofs/normalise-extraction.fsx`)
/// made the committed extraction indentation-clean F#, and this pack now
/// ALSO compiles `proofs/oracle/ElmishRing.fs` — the same bytes the .NET
/// oracle compiles — over the machine-integer `Prims` in
/// `proofs/oracle/fable/`, and runs the ring model live (the Phase 850
/// cases below). The corpus stays: it is now also the check that the two
/// hosts' shims — `BigInteger` on .NET, `int` here — compute the same
/// model, which no single host can assert alone.
///
/// ─── Why this pack and not a browser-smoke scenario ──────────────────
///
/// The differential is pure: no DOM, no React, no timers. `node:test`
/// over the transpiled output is the cheapest host that runs the
/// transpiled runtime, and this project already pulls `Platform.Client`
/// through Fable, so the ring and the diff are transpiled here at no new
/// cost.
///
/// ─── What it does NOT claim ──────────────────────────────────────────
///
/// Nothing beyond the .NET pack's claims, restated for the other
/// runtime. The go-red cases are asserted caught here too, for the same
/// reason: a comparison that has never been shown to fail agrees with
/// whatever it is shown, on either host.
module ToolUp.AI.Client.Tests.ElmishProofOracleTests

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.Platform.Tests.Client
open ToolUp.Platform.Tests.Client.ElmishProofDifferential

[<Import("readFileSync", from = "node:fs")>]
let private readFileSync (path: obj, encoding: string) : string = jsNative

[<Import("existsSync", from = "node:fs")>]
let private existsSync (path: obj) : bool = jsNative

/// Resolve a path relative to THIS MODULE rather than to the process cwd
/// — the `RemotingCorpusParityTests` precedent. The transpiled module
/// sits in `output/`, so the repository root is three levels up.
[<Emit("new URL($0, import.meta.url)")>]
let private beside (relative: string) : obj = jsNative

let private corpusPath (file: string) =
    beside ("../../../" + CorpusDir + "/" + file)

let private readCorpus (file: string) : string list =
    corpusLines (readFileSync (corpusPath file, "utf8"))

let private ringCases () =
    readCorpus RingCorpusFile |> List.map parseRingCase

let private diffCases () =
    readCorpus DiffCorpusFile |> List.map parseDiffCase

// ─── Phase 850 — the extracted ring, run DIRECTLY on this host ───────
//
// The bridge is the .NET host's (`InProcess/ElmishProofOracleTests.fs`)
// minus the BigInteger: under the Fable-host shim `Prims.int` IS `int`.
// Short and case for case, for the reason Rung 3 gives.

let private toModelOps (ops: RingOp list) : ElmishRing.op<int> list =
    ops
    |> List.map (fun op ->
        match op with
        | RPush v -> ElmishRing.Push v
        | RPop -> ElmishRing.Pop)

/// A popped placeholder — the defect `placeholder_unobserved` proves
/// impossible — is kept distinguishable from a legitimate `None`.
let private ofModelOutput (out: ElmishRing.opt<ElmishRing.slot<int>>) : Result<int option, string> =
    match out with
    | ElmishRing.ONone -> Ok None
    | ElmishRing.OSome(ElmishRing.Written v) -> Ok(Some v)
    | ElmishRing.OSome ElmishRing.Placeholder -> Error "the model popped a placeholder slot"

/// The extracted model over an op sequence, computed on THIS host.
let private modelRing (capacity: int) (ops: RingOp list) : int option list =
    match ElmishRing.run (ElmishRing.create capacity) (toModelOps ops) with
    | ElmishRing.Pair(_, outs) ->
        outs
        |> List.map (fun o ->
            match ofModelOutput o with
            | Ok v -> v
            | Error e -> failwith e)

/// A second campaign, drawn from a seed the corpus never used: the live
/// model can judge sequences no .NET run recorded, which a replay cannot.
let private liveCampaign () = genRingCampaign (Seed + 850) 60

[<Emit("performance.now()")>]
let private now () : float = jsNative

/// Wall-clock nanoseconds per operation over `rounds` runs of `body`.
let private perOpNs (rounds: int) (ops: int) (body: unit -> unit) : float =
    let started = now ()

    for _ in 1..rounds do
        body ()

    (now () - started) * 1_000_000.0 / float (rounds * ops)

// ─── Phase 884 — the dispatch loop, on this host ─────────────────────
//
// The loop every browser client runs is the transpilation of
// `Program.runWithDispatch`; until Phase 884 its differential ran on .NET
// only. Every row of the host-neutral `ElmishLoopDifferential` — the
// production/model agreement over the campaign, per step; the coverage
// floors; the faithful skeleton and its six go-reds; the loop properties
// run on production — is one case here, over the SAME campaign (the
// first row asserts the pinned fingerprint the .NET pack asserts too),
// with the extraction `proofs/oracle/ElmishLoop.fs` compiled by Fable
// over the machine-integer shim (the second row holds its verdicts to
// the ones the `BigInteger` shim computes). Nothing is replayed from a
// file: the model runs live, as the ring's does since 850.
//
// What does NOT run here is what is not the campaign: the .NET pack's
// hand-written production scenarios, which read the process-wide
// `Console.Error` or pin one-off shapes (see that file's header).

let private loopTests =
    testList "Phase 884 - the proved dispatch loop as oracle (Fable)" [
        for check in ElmishLoopDifferential.corpusChecks ->
            testCase check.Name (fun () ->
                let violations = check.Violations()
                Expect.isEmpty violations (ElmishLoopDifferential.report check violations))
    ]

// ─── Phase 955 — the extracted ARRAY ring, on this host ──────────────
//
// `proofs/ElmishRingArray.fst` is the ring over a mutable array, proved
// in Pulse to refine the list model; its extraction through Custard's F#
// backend (`proofs/oracle/custard/ElmishRingArray.fs`) is compiled here by
// Fable with the rest of the client tier — the same committed bytes the
// .NET pack runs. What Fable does NOT compile is the support library
// Custard writes beside it; the ring uses nothing from that file, and
// this host compiles the empty stand-in `proofs/oracle/fable/FStarCustard.fs`
// in its place (read its header).
//
// The bridge and the cases are the .NET host's
// (`ToolUp.Platform.Tests/InProcess/ElmishProofOracleTests.fs`), case for
// case, restated for the runtime a browser executes.

[<Literal>]
let private ArrayRingCeilingMessage =
    "the extracted ring refused a push at its capacity ceiling"

/// `RingBuffer<'item>`'s surface over the extracted functions — all a
/// replacement of `Ring.fs` would hand-write. The extraction's indices
/// are `uint64`, which Fable carries as a JavaScript BigInt.
type private ArrayRing<'item>(size: int) =
    let ring =
        ElmishRingArray.elmishRingArray_create (uint64 (max size 0)) Unchecked.defaultof<'item>

    member _.Pop() : 'item option =
        ElmishRingArray.elmishRingArray_pop ring

    member _.Push(item: 'item) : unit =
        if not (ElmishRingArray.elmishRingArray_push ring item) then
            invalidOp ArrayRingCeilingMessage

    /// The backing array's length — read by the campaign, never by a client.
    member _.Slots: int = int ring.cap.Value

let private arrayRing (capacity: int) (ops: RingOp list) : int option list =
    let rb = ArrayRing<int> capacity

    [
        for op in ops do
            match op with
            | RPush v -> rb.Push v
            | RPop -> yield rb.Pop()
    ]

/// The .NET host's second campaign, drawn here from the same seed.
let private arrayLiveCampaign () = genRingCampaign (Seed + 955) 60

[<Literal>]
let private MeasuredCapacity = 10

[<Literal>]
let private MeasuredOps = 4_000

let private measuredSequence () = genRingOps (Lcg 850_001) MeasuredOps 65

/// The literal the .NET host pins: the two hosts time the same operations.
[<Literal>]
let private MeasuredSequenceFingerprint = "1:14575:1899911031"

/// The MINIMUM over `rounds` rounds of `reps` runs each — Phase 849's
/// statistic.
let private minPerOpNs (rounds: int) (reps: int) (ops: int) (body: unit -> unit) : float =
    List.min [ for _ in 1..rounds -> perOpNs reps ops body ]

[<Emit("process.version")>]
let private nodeVersion () : string = jsNative

let private arrayRingTests =
    testList "Phase 955 - the extracted array ring as implementation (Fable)" [

        testCase
            "the extracted array ring agrees with the transpiled ring and with the proved model over the corpus"
            (fun () ->
                let cases = ringCases ()
                Expect.isTrue (List.length cases >= 150) $"only {List.length cases} ring case(s)"

                let mismatches =
                    cases
                    |> List.collect (fun c ->
                        let extracted = arrayRing c.Capacity c.Ops

                        [
                            describeRingMismatch c.Capacity c.Ops extracted c.Expected
                            describeRingMismatch c.Capacity c.Ops extracted (productionRing c.Capacity c.Ops)
                        ]
                        |> List.choose id)

                Expect.isEmpty
                    mismatches
                    ($"{List.length mismatches} comparison(s) on which the extracted array ring popped differently from the model or the transpiled ring: "
                     + String.concat " | " (List.truncate 5 mismatches)))

        testCase "the array ring grows past several doublings, to the sizes the model's grow step gives" (fun () ->
            // Every capacity the ring can reach from `c` is `c`, `2c + 1`,
            // `4c + 3`, … — the model's `doubleSize`. The .NET host holds
            // the slot count to the model's exactly; here the final size is
            // held to that sequence, and the campaign shown to reach it.
            let reachable (capacity: int) (slots: int) =
                let mutable n = max capacity 2

                while n < slots do
                    n <- n + n + 1

                n = slots

            let sizes =
                ringCases ()
                |> List.map (fun c ->
                    let rb = ArrayRing<int> c.Capacity

                    for op in c.Ops do
                        match op with
                        | RPush v -> rb.Push v
                        | RPop -> rb.Pop() |> ignore

                    c.Capacity, rb.Slots)

            Expect.isTrue
                (sizes |> List.forall (fun (capacity, slots) -> reachable capacity slots))
                "a ring ended with a slot count the grow step cannot produce"

            Expect.isTrue
                (sizes |> List.map snd |> List.max > 111)
                "the largest backing array the corpus reached is fewer than three doublings of the largest capacity")

        testCase "the extracted array ring agrees with the transpiled ring on sequences the corpus never had" (fun () ->
            let campaign = arrayLiveCampaign ()
            Expect.isTrue (List.length campaign >= 100) $"only {List.length campaign} live sequence(s)"

            let mismatches =
                campaign
                |> List.choose (fun (capacity, ops) ->
                    describeRingMismatch capacity ops (arrayRing capacity ops) (productionRing capacity ops))

            Expect.isEmpty
                mismatches
                ($"{List.length mismatches} live sequence(s) on which the extracted array ring and the transpiled ring popped differently: "
                 + String.concat " | " (List.truncate 5 mismatches)))

        testCase "a ring that skips the wrap check is caught against the array ring - go-red" (fun () ->
            let caught =
                arrayLiveCampaign ()
                |> List.filter (fun (capacity, ops) -> brokenRing capacity ops <> arrayRing capacity ops)
                |> List.length

            Expect.isTrue (caught > 0) "the broken ring was never caught by the comparison with the array ring")

        testCase "a placeholder is never popped - the array ring over a reference type" (fun () ->
            let popped =
                arrayLiveCampaign ()
                |> List.collect (fun (capacity, ops) ->
                    let rb = ArrayRing<string> capacity

                    [
                        for op in ops do
                            match op with
                            | RPush v -> rb.Push(string v)
                            | RPop -> yield rb.Pop()
                    ])
                |> List.choose id

            Expect.isTrue (List.length popped > 1000) $"only {List.length popped} item(s) popped"
            Expect.isFalse (popped |> List.exists isNull) "a pop returned the placeholder")

        testCase "at the capacity ceiling a push is refused, nothing is lost, and the ring goes on working" (fun () ->
            let rb = ArrayRing<int> MeasuredCapacity
            let mutable pushed = 0
            let mutable refused = false

            while not refused && pushed < 70_000 do
                try
                    rb.Push(pushed + 1)
                    pushed <- pushed + 1
                with e when e.Message = ArrayRingCeilingMessage ->
                    refused <- true

            Expect.isTrue refused "the array ring never refused a push"
            Expect.isTrue (rb.Slots > 32_767) "the refusal came before the ceiling"
            Expect.isTrue (rb.Slots <= 65_535) "the ring grew past the size the proof covers"
            Expect.equal pushed (rb.Slots - 1) "a ring of n slots holds n - 1 unread items when it refuses"

            Expect.equal (rb.Pop()) (Some 1) "the oldest item is still first"
            rb.Push(pushed + 1)

            // A loop, not a list comparison: `node:assert`'s deep equality
            // recurses per cons cell and tens of thousands overflow it.
            let mutable inOrder = true

            for expected in 2 .. pushed + 1 do
                if rb.Pop() <> Some expected then
                    inOrder <- false

            Expect.isTrue inOrder "every accepted item comes out once, in order"
            Expect.isNone (rb.Pop()) "and then the ring is empty")

        testCase "the measured sequence is the draw both hosts make - its fingerprint is pinned" (fun () ->
            Expect.equal
                (fingerprint [ renderRingDraw (MeasuredCapacity, measuredSequence ()) ])
                MeasuredSequenceFingerprint
                "the measured sequence moved: both hosts must time the same operations")

        testCase "Phase 955 - shipped ring, list extraction and array extraction, measured (informational)" (fun () ->
            // The .NET host's case, on node: every arm's output asserted
            // equal before anything is timed, the minimum over rounds.
            let ops = measuredSequence ()
            let produced = productionRing MeasuredCapacity ops
            let modelled = modelRing MeasuredCapacity ops
            let extracted = arrayRing MeasuredCapacity ops

            Expect.equal
                (Array.ofList modelled)
                (Array.ofList produced)
                "the list extraction and the transpiled ring must agree before they are timed"

            Expect.equal
                (Array.ofList extracted)
                (Array.ofList produced)
                "the array extraction and the transpiled ring must agree before they are timed"

            let popped = produced |> List.choose id |> List.length
            Expect.isTrue (popped > 1_000) $"the measured sequence popped only {popped} item(s)"

            let shippedNs =
                minPerOpNs 9 100 MeasuredOps (fun () -> productionRing MeasuredCapacity ops |> ignore)

            let listNs =
                minPerOpNs 3 1 MeasuredOps (fun () -> modelRing MeasuredCapacity ops |> ignore)

            let arrayNs =
                minPerOpNs 9 100 MeasuredOps (fun () -> arrayRing MeasuredCapacity ops |> ignore)

            // The ring alone: the sequence walked from an array, no
            // output list built. A pop is -1; pushed values start at 1.
            let steps =
                ops
                |> List.map (fun op ->
                    match op with
                    | RPush v -> v
                    | RPop -> -1)
                |> Array.ofList

            let shippedAlone () =
                let rb = ToolUp.Elmish.RingBuffer<int> MeasuredCapacity
                let mutable sum = 0

                for step in steps do
                    if step >= 0 then
                        rb.Push step
                    else
                        match rb.Pop() with
                        | Some v -> sum <- sum + v
                        | None -> sum <- sum + 1

                sum

            let arrayAlone () =
                let rb = ArrayRing<int> MeasuredCapacity
                let mutable sum = 0

                for step in steps do
                    if step >= 0 then
                        rb.Push step
                    else
                        match rb.Pop() with
                        | Some v -> sum <- sum + v
                        | None -> sum <- sum + 1

                sum

            Expect.equal (arrayAlone ()) (shippedAlone ()) "the two rings walked alone must pop the same items"

            let shippedAloneNs =
                minPerOpNs 9 100 MeasuredOps (fun () -> shippedAlone () |> ignore)

            let arrayAloneNs = minPerOpNs 9 100 MeasuredOps (fun () -> arrayAlone () |> ignore)

            printfn
                "Phase 955 measurement (Fable/node %s, capacity %d, %d ops, 65%% pushes, min over rounds): shipped Ring.fs %.1f ns/op; list extraction %.0f ns/op; array extraction %.1f ns/op (%.2fx the shipped ring). Ring alone, no output list: shipped %.1f ns/op; array extraction %.1f ns/op (%.2fx)."
                (nodeVersion ())
                MeasuredCapacity
                MeasuredOps
                shippedNs
                listNs
                arrayNs
                (arrayNs / shippedNs)
                shippedAloneNs
                arrayAloneNs
                (arrayAloneNs / shippedAloneNs)

            Expect.isTrue
                (shippedNs > 0.0
                 && listNs > 0.0
                 && arrayNs > 0.0
                 && shippedAloneNs > 0.0
                 && arrayAloneNs > 0.0)
                "every measurement ran")
    ]

let tests =
    testList "Phase 788 - the proved Elmish runtime as oracle (Fable)" [

        testCase "the corpus resolved and is not close to empty" (fun () ->
            // The non-vacuity floor: every case below would pass by doing
            // nothing if the path were wrong or the files empty.
            Expect.isTrue (existsSync (corpusPath RingCorpusFile)) $"no ring corpus at {CorpusDir}/{RingCorpusFile}"
            Expect.isTrue (existsSync (corpusPath DiffCorpusFile)) $"no diff corpus at {CorpusDir}/{DiffCorpusFile}"
            Expect.isTrue (List.length (ringCases ()) >= 150) $"only {List.length (ringCases ())} ring case(s)"
            Expect.isTrue (List.length (diffCases ()) >= 300) $"only {List.length (diffCases ())} diff case(s)")

        testCase "the transpiled ring agrees with the proved model over the corpus sequences" (fun () ->
            let mismatches =
                ringCases ()
                |> List.choose (fun c ->
                    describeRingMismatch c.Capacity c.Ops (productionRing c.Capacity c.Ops) c.Expected)

            Expect.isEmpty
                mismatches
                ($"{List.length mismatches} sequence(s) on which the transpiled ring and the proved model popped differently: "
                 + String.concat " | " (List.truncate 5 mismatches)))

        testCase "the transpiled diff and change agree with the proved model over the corpus inputs" (fun () ->
            let mismatches =
                diffCases ()
                |> List.collect (fun c -> describeDiffMismatch c.Input (productionDiffShape c.Input) c.Expected)

            Expect.isEmpty
                mismatches
                ($"{List.length mismatches} input(s) on which the transpiled diff and the proved model differed: "
                 + String.concat " | " (List.truncate 5 mismatches)))

        // Phase 900 — the two generators the corpus was drawn from, drawn
        // HERE and held to the pin the .NET host asserts: the corpus replay
        // above says the transpiled runtime agrees with recorded verdicts,
        // and this says this host draws what the .NET host drew.
        testCase "the ring campaign is the draw both hosts make - its fingerprint is pinned - Phase 900" (fun () ->
            let violations = ringCampaignPinViolations ()

            Expect.isEmpty
                violations
                ("ring campaign drift: pin the printed value in RingCampaignFingerprint if the generator moved on purpose\n"
                 + String.concat "\n" violations))

        testCase "the diff campaign is the draw both hosts make - its fingerprint is pinned - Phase 900" (fun () ->
            let violations = diffCampaignPinViolations ()

            Expect.isEmpty
                violations
                ("diff campaign drift: pin the printed value in DiffCampaignFingerprint if the generator moved on purpose\n"
                 + String.concat "\n" violations))

        testCase "the corpus exercised the shortcut and the duplicate path" (fun () ->
            let classified = diffCases () |> List.map (fun c -> classifyDiffInput c.Input)
            let fastPath = classified |> List.filter fst |> List.length
            let withDupes = classified |> List.filter snd |> List.length
            Expect.isTrue (fastPath > 30) $"only {fastPath} inputs hit the shortcut"
            Expect.isTrue (withDupes > 30) $"only {withDupes} inputs carried a duplicate key")

        testCase "a ring that skips the wrap check is caught - go-red" (fun () ->
            let caught =
                ringCases ()
                |> List.filter (fun c -> brokenRing c.Capacity c.Ops <> c.Expected)
                |> List.length

            Expect.isTrue (caught > 0) "the broken ring was never caught")

        testCase "a diff that starts a key it also keeps is caught - go-red" (fun () ->
            let caught =
                diffCases ()
                |> List.filter (fun c -> brokenDiffShape c.Input <> c.Expected)
                |> List.length

            Expect.isTrue (caught > 0) "the broken diff was never caught")

        // ─── Phase 850 — the extraction itself, on this host ─────────

        testCase
            "Phase 850 - the extracted ring runs directly under Fable and reproduces the verdicts the .NET host recorded"
            (fun () ->
                // The two shims held to each other: the corpus was computed on
                // .NET with `Prims.int = BigInteger`; this host computes the
                // same model with `Prims.int = int`. Disagreement here would be
                // the shim, not the ring — which is exactly what the machine-
                // integer assumption on the ladder needs a test to say.
                let disagreements =
                    ringCases ()
                    |> List.choose (fun c ->
                        describeRingMismatch c.Capacity c.Ops (modelRing c.Capacity c.Ops) c.Expected)

                Expect.isEmpty
                    disagreements
                    ($"{List.length disagreements} sequence(s) on which the model under the machine-integer shim differs from the corpus the BigInteger shim wrote: "
                     + String.concat " | " (List.truncate 5 disagreements)))

        testCase
            "Phase 850 - the transpiled ring agrees with the extracted model computed live, on sequences the corpus never had"
            (fun () ->
                let campaign = liveCampaign ()
                Expect.isTrue (List.length campaign >= 100) $"only {List.length campaign} live sequence(s)"

                let mismatches =
                    campaign
                    |> List.choose (fun (capacity, ops) ->
                        describeRingMismatch capacity ops (productionRing capacity ops) (modelRing capacity ops))

                Expect.isEmpty
                    mismatches
                    ($"{List.length mismatches} live sequence(s) on which the transpiled ring and the extracted model popped differently: "
                     + String.concat " | " (List.truncate 5 mismatches)))

        testCase "Phase 850 - the ring that skips the wrap check is caught by the live model - go-red" (fun () ->
            let caught =
                liveCampaign ()
                |> List.filter (fun (capacity, ops) -> brokenRing capacity ops <> modelRing capacity ops)
                |> List.length

            Expect.isTrue (caught > 0) "the broken ring was never caught by the live model")

        testCase "Phase 850 - the extracted ring against the shipped ring, measured (informational)" (fun () ->
            // A local stopwatch, not Phase 849's harness (it was in flight
            // when this was written). Asserts only that both ran the same
            // sequence to the same answer; the numbers are printed for the
            // record in proofs/README.md's Phase 850 section, where their
            // falsifier is stated. 4,000 ops at 65% pushes grows the ring
            // through several doublings; the model's `run` is not
            // tail-recursive, so the sequence length is bounded by the
            // JavaScript stack rather than by patience.
            let capacity = 10
            let opCount = 4_000
            let ops = genRingOps (Lcg 850_001) opCount 65
            let produced = productionRing capacity ops
            let modelled = modelRing capacity ops
            // As arrays: `node:assert`'s deep equality recurses once per
            // cons cell, and a list of ~1,400 pops overflows its stack. Until
            // Phase 884 fixed the Fable LCG this sequence was pushes after its
            // first few draws, so the list compared here was all but empty and
            // the limit was never met.
            Expect.equal (Array.ofList modelled) (Array.ofList produced) "the measured runs must still agree"

            let productionNs =
                perOpNs 100 opCount (fun () -> productionRing capacity ops |> ignore)

            let modelNs = perOpNs 3 opCount (fun () -> modelRing capacity ops |> ignore)

            printfn
                "Phase 850 measurement (Fable/node, capacity %d, %d ops, 65%% pushes): shipped Ring.fs %.0f ns/op; extracted model %.0f ns/op; ratio %.0fx"
                capacity
                opCount
                productionNs
                modelNs
                (modelNs / productionNs)

            Expect.isTrue (productionNs > 0.0 && modelNs > 0.0) "both measurements ran")

        loopTests

        // Phase 955 — the array ring. Nested, as the loop's list is.
        arrayRingTests
    ]