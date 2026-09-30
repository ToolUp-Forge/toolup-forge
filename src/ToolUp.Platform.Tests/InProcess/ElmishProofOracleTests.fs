// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ElmishProofOracleTests

open System
open System.IO
open System.Numerics
open System.Reflection
open Expecto
open ToolUp.Elmish
open ToolUp.Platform.Tests.Client.ElmishProofDifferential

// ─── Phase 788 — the proved Elmish runtime as oracle ─────────────────
//
// `proofs/ElmishRing.fst` models `RingBuffer<'item>` and proves it a
// FIFO queue through every grow; `proofs/ElmishSub.fst` models
// `Sub.Internal.diff` / `NewSubs.calculate` / `Fx.change` and proves the
// four lists exactly what the source comment says, on the shortcut path
// too. `proofs/check.ps1` extracts both to F# and byte-compares the
// result against the committed `proofs/oracle/ElmishRing.fs` /
// `ElmishSub.fs`, which this pack references and runs.
//
// **A proof is about the MODEL, and this pack is the only thing that
// says the model is about the code.** Every arm below is differential:
// the generated sequences and subscription sets go through production
// and through the extracted model, and the two must agree — on the
// popped values in order, and on all four diff lists with the IDENTITY
// of every handle and start function carried through.
//
// The runtime ships to two hosts. This pack ALSO writes the model's
// verdicts for the campaign to `tests/elmish-proof-corpus/` as a
// self-describing corpus, holds that file to the live model on every
// run (regenerable under `TOOLUP_REGEN_ELMISH_PROOF_CORPUS=1`), and the
// Fable pack replays it against the transpiled runtime. Until Phase 850
// only this host could compile the extraction (see the header of
// `Client/ElmishProofDifferential.fs`); since 850 the Fable pack compiles
// the ring extraction too and runs it live, and the corpus doubles as the
// check that the two hosts' `Prims` shims compute the same model.
//
// The go-red cases are committed and asserted CAUGHT, and the campaign
// asserts it reached the grow step and the shortcut — a differential
// that only ever exercised the steady state would pass every
// comparison and prove nothing about the clauses that matter.

// ─── The bridge ──────────────────────────────────────────────────────
//
// Case for case, and short on purpose: a defect here would make the
// comparison compare the wrong thing.

let private big (n: int) : BigInteger = BigInteger n

let private toModelOps (ops: RingOp list) : ElmishRing.op<int> list =
    ops
    |> List.map (fun op ->
        match op with
        | RPush v -> ElmishRing.Push v
        | RPop -> ElmishRing.Pop)

/// What the model's output means host-side. `Error` is a popped
/// placeholder — the defect `placeholder_unobserved` proves impossible,
/// kept distinguishable from a legitimate `None` so it can never read
/// as agreement.
let private ofModelOutput (out: ElmishRing.opt<ElmishRing.slot<int>>) : Result<int option, string> =
    match out with
    | ElmishRing.ONone -> Ok None
    | ElmishRing.OSome(ElmishRing.Written v) -> Ok(Some v)
    | ElmishRing.OSome ElmishRing.Placeholder -> Error "the model popped a placeholder slot"

/// The extracted model over an op sequence: the outputs and the final
/// ring, so the campaign can also say how far it grew.
let private modelRing (capacity: int) (ops: RingOp list) : int option list * ElmishRing.ring<int> =
    match ElmishRing.run (ElmishRing.create (big capacity)) (toModelOps ops) with
    | ElmishRing.Pair(ring, outs) ->
        let mapped =
            outs
            |> List.map (fun o ->
                match ofModelOutput o with
                | Ok v -> v
                | Error e -> failtest e)

        mapped, ring

let private modelSlots (ring: ElmishRing.ring<int>) : int =
    match ring with
    | ElmishRing.Writable(items, _) -> List.length items
    | ElmishRing.ReadWritable(items, _, _) -> List.length items

/// The extracted diff + change over an input, as a shape — handles and
/// subscribes are the input's ids, so the model's pairs map back to ids
/// directly.
let private modelDiffShape (input: DiffInput) : DiffShape =
    let active = input.Active |> List.map (fun (k, id) -> ElmishSub.Pair(k, id))
    let requested = input.Requested |> List.map (fun (k, id) -> ElmishSub.Pair(k, id))

    let ofPairs (xs: ElmishSub.pair<SubId, int> list) =
        xs
        |> List.map (fun p ->
            match p with
            | ElmishSub.Pair(k, id) -> k, id)

    let d = ElmishSub.diff active requested

    // `change` with every start succeeding — the production driver's
    // starts always succeed too (they return a fresh handle).
    let start (_: SubId) (id: int) : ElmishSub.opt<int> = ElmishSub.OSome id
    let next = ElmishSub.change start d

    match d with
    | ElmishSub.Diff(dupes, toStop, toKeep, toStart) -> {
        Dupes = dupes
        ToStop = ofPairs toStop
        ToKeep = ofPairs toKeep
        ToStart = ofPairs toStart
        ChangeKeys = ofPairs next |> List.map fst
      }

// ─── The campaign, with the model's verdicts ─────────────────────────

let private ringCases: Lazy<RingCase list> =
    lazy
        (genRingCampaign Seed 100
         |> List.map (fun (capacity, ops) -> {
             Capacity = capacity
             Ops = ops
             Expected = fst (modelRing capacity ops)
         }))

let private diffCases: Lazy<DiffCase list> =
    lazy
        (genDiffCampaign Seed 400
         |> List.map (fun input -> {
             Input = input
             Expected = modelDiffShape input
         }))

// ─── The corpus files ────────────────────────────────────────────────

let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private corpusPath (file: string) =
    Path.Combine(repoRoot (), CorpusDir, file)

let private regenCorpus () =
    match Environment.GetEnvironmentVariable "TOOLUP_REGEN_ELMISH_PROOF_CORPUS" with
    | null
    | "" -> false
    | v -> v = "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)

let private renderRingCorpus () =
    String.concat
        "\n"
        (corpusHeader "generated push/pop sequences"
         @ List.map formatRingCase (ringCases.Force()))
    + "\n"

let private renderDiffCorpus () =
    String.concat
        "\n"
        (corpusHeader "generated subscription inputs"
         @ List.map formatDiffCase (diffCases.Force()))
    + "\n"

let private goldenFile (file: string) (render: unit -> string) (what: string) =
    let rendered = render ()
    let path = corpusPath file

    if regenCorpus () then
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, rendered)
    else
        Expect.isTrue
            (File.Exists path)
            $"{path} is missing. Generate it with `dev-scripts/generate-elmish-proof-corpus.ps1`."

        Expect.equal
            (File.ReadAllText(path).Replace("\r\n", "\n"))
            rendered
            $"{CorpusDir}/{file} is stale — {what} no longer match what the proved model produces. Run `dev-scripts/generate-elmish-proof-corpus.ps1` and commit the result with whatever moved the model (or the generator)."

// ─── Phase 955 — the extracted ARRAY ring, run as the implementation ──
//
// `proofs/ElmishRingArray.fst` is the ring over a mutable array, written
// in Pulse and proved to refine the list model above: `create`, `pop` and
// `push` each do to the abstract ring exactly what `ElmishRing.create`,
// `ElmishRing.pop` and `ElmishRing.push` do. Its extraction
// (`proofs/oracle/custard/ElmishRingArray.fs`, through Custard's F#
// backend, byte-held by `proofs/check.ps1`) is generic F# over a .NET
// array and `ref` cells — code meant to be RUN as the ring, where the
// list model's extraction is run beside one.
//
// So the arms here are three-way: the shipped `RingBuffer`, the list
// model's verdicts, and the extracted array ring behind the wrapper
// below, over the same sequences.

/// What the wrapper says when the extracted ring refuses a push. The
/// array ring grows only while `2n + 1` is a size the prover can show the
/// platform holds (`max_growable` in the model); the shipped ring has no
/// such ceiling.
[<Literal>]
let private ArrayRingCeilingMessage =
    "the extracted ring refused a push at its capacity ceiling"

/// `RingBuffer<'item>`'s surface over the extracted functions — all a
/// replacement of `Ring.fs` would hand-write, and therefore all of it
/// that would be trusted rather than proved: the `int` capacity widened
/// to the extraction's `uint64`, the placeholder the constructor fills
/// unwritten slots with, and a refused push surfaced as an exception.
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

/// The extracted array ring over an op sequence: every `Pop`'s result in
/// order (the shape `productionRing` returns) and the slots it ended with.
let private arrayRing (capacity: int) (ops: RingOp list) : int option list * int =
    let rb = ArrayRing<int> capacity

    let outs = [
        for op in ops do
            match op with
            | RPush v -> rb.Push v
            | RPop -> yield rb.Pop()
    ]

    outs, rb.Slots

/// Sequences the corpus never had, from a seed of this phase's own.
let private arrayLiveCampaign () = genRingCampaign (Seed + 955) 60

/// The measured sequence: Phase 850's case. Its draw is pinned below so
/// both hosts are shown to time the same operations — Phase 884 found
/// the last measurement had not.
[<Literal>]
let private MeasuredCapacity = 10

[<Literal>]
let private MeasuredOps = 4_000

let private measuredSequence () = genRingOps (Lcg 850_001) MeasuredOps 65

/// The `fingerprint` of the measured sequence's `renderRingDraw`. The
/// Fable host asserts the same literal.
[<Literal>]
let private MeasuredSequenceFingerprint = "1:14575:1899911031"

/// Wall-clock nanoseconds per operation: the MINIMUM over `rounds`
/// rounds of `reps` runs each — Phase 849's statistic, because noise on
/// a shared machine is one-sided.
let private minPerOpNs (rounds: int) (reps: int) (ops: int) (body: unit -> unit) : float =
    List.min [
        for _ in 1..rounds do
            let watch = Diagnostics.Stopwatch.StartNew()

            for _ in 1..reps do
                body ()

            yield float watch.Elapsed.TotalMilliseconds * 1_000_000.0 / float (reps * ops)
    ]

let private arrayRingTests =
    testList "Phase 955 - the extracted array ring as implementation" [

        test "the extracted array ring agrees with the shipped ring and with the proved model over the campaign" {
            let mismatches =
                ringCases.Force()
                |> List.collect (fun c ->
                    let extracted = fst (arrayRing c.Capacity c.Ops)

                    [
                        describeRingMismatch c.Capacity c.Ops extracted c.Expected
                        describeRingMismatch c.Capacity c.Ops extracted (productionRing c.Capacity c.Ops)
                    ]
                    |> List.choose id)

            Expect.isGreaterThan (List.length (ringCases.Force())) 150 "the campaign is not close to empty"

            Expect.isEmpty
                mismatches
                (sprintf
                    "%d comparison(s) on which the extracted array ring popped differently from the model or the shipped ring:\n%s"
                    (List.length mismatches)
                    (String.concat "\n" (List.truncate 10 mismatches)))
        }

        test "the array ring grows exactly as the model does - past several doublings" {
            // The refinement says more than "same outputs": the array ring's
            // backing array is the model's, slot for slot, so its LENGTH
            // after any sequence is the model's — `2n + 1` at every grow.
            let sizes =
                ringCases.Force()
                |> List.map (fun c -> snd (arrayRing c.Capacity c.Ops), modelSlots (snd (modelRing c.Capacity c.Ops)))

            let disagreements =
                sizes |> List.filter (fun (extracted, modelled) -> extracted <> modelled)

            Expect.isEmpty
                disagreements
                $"{List.length disagreements} sequence(s) after which the array ring held a different number of slots from the model"

            Expect.isGreaterThan
                (sizes |> List.map fst |> List.max)
                111
                "the largest backing array the campaign reached is fewer than three doublings of the largest capacity"
        }

        test "the extracted array ring agrees with the shipped ring on sequences the corpus never had" {
            let campaign = arrayLiveCampaign ()
            Expect.isGreaterThanOrEqual (List.length campaign) 100 "the live campaign is not close to empty"

            let mismatches =
                campaign
                |> List.choose (fun (capacity, ops) ->
                    describeRingMismatch capacity ops (fst (arrayRing capacity ops)) (productionRing capacity ops))

            Expect.isEmpty
                mismatches
                (sprintf
                    "%d live sequence(s) on which the extracted array ring and the shipped ring popped differently:\n%s"
                    (List.length mismatches)
                    (String.concat "\n" (List.truncate 10 mismatches)))
        }

        test "a ring that skips the wrap check is caught against the array ring - go-red" {
            let caught =
                arrayLiveCampaign ()
                |> List.filter (fun (capacity, ops) -> brokenRing capacity ops <> fst (arrayRing capacity ops))
                |> List.length

            Expect.isGreaterThan caught 0 "the broken ring was never caught by the comparison with the array ring"
        }

        test "a placeholder is never popped - the array ring over a reference type" {
            // `placeholder_unobserved`, run: over strings the placeholder is
            // `null`, which a pop would return as `Some null` if the read
            // head ever reached an unwritten slot.
            let outs =
                arrayLiveCampaign ()
                |> List.collect (fun (capacity, ops) ->
                    let rb = ArrayRing<string> capacity

                    [
                        for op in ops do
                            match op with
                            | RPush v -> rb.Push(string v)
                            | RPop -> yield rb.Pop()
                    ])

            let popped = outs |> List.choose id
            Expect.isGreaterThan (List.length popped) 1000 "the campaign popped items"
            Expect.isFalse (popped |> List.exists isNull) "a pop returned the placeholder"
        }

        test "at the capacity ceiling a push is refused, nothing is lost, and the ring goes on working" {
            // The one place the array ring differs from the shipped one:
            // past `max_growable` slots it refuses the push that would grow.
            let rb = ArrayRing<int> MeasuredCapacity
            let mutable pushed = 0
            let mutable refused = false

            while not refused && pushed < 70_000 do
                try
                    rb.Push(pushed + 1)
                    pushed <- pushed + 1
                with :? InvalidOperationException as e when e.Message = ArrayRingCeilingMessage ->
                    refused <- true

            Expect.isTrue refused "the array ring never refused a push"
            Expect.isGreaterThan rb.Slots 32_767 "the refusal came before the ceiling"
            Expect.isLessThanOrEqual rb.Slots 65_535 "the ring grew past the size the proof covers"
            Expect.equal pushed (rb.Slots - 1) "a ring of n slots holds n - 1 unread items when it refuses"

            Expect.equal (rb.Pop()) (Some 1) "the oldest item is still first"
            rb.Push(pushed + 1)

            let rest = [ for _ in 1..pushed -> rb.Pop() ]
            Expect.equal rest [ for v in 2 .. pushed + 1 -> Some v ] "every accepted item comes out once, in order"
            Expect.isNone (rb.Pop()) "and then the ring is empty"
        }

        test "the measured sequence is the draw both hosts make - its fingerprint is pinned" {
            let ops = measuredSequence ()

            Expect.equal
                (fingerprint [ renderRingDraw (MeasuredCapacity, ops) ])
                MeasuredSequenceFingerprint
                "the measured sequence moved: both hosts must time the same operations (pin the printed value in both host files if the generator moved on purpose)"
        }

        test "Phase 955 - shipped ring, list extraction and array extraction, measured (informational)" {
            // Phase 850's case under Phase 849's discipline: the sequence
            // from the corpus's generator by seed, every arm's output
            // asserted equal BEFORE anything is timed, the statistic the
            // minimum over rounds. The numbers are printed for the record in
            // proofs/README.md, where their falsifier is stated; nothing
            // here asserts a ratio, because a clock in a test pack is a
            // flake.
            let ops = measuredSequence ()
            let produced = productionRing MeasuredCapacity ops
            let modelled = fst (modelRing MeasuredCapacity ops)
            let extracted, slots = arrayRing MeasuredCapacity ops
            Expect.equal modelled produced "the list extraction and the shipped ring must agree before they are timed"
            Expect.equal extracted produced "the array extraction and the shipped ring must agree before they are timed"

            // Each arm did the work being timed.
            let popped = produced |> List.choose id |> List.length
            Expect.isGreaterThan popped 1_000 "the measured sequence popped items"
            Expect.isGreaterThan slots 1_000 "the measured sequence grew the ring through several doublings"

            let shippedNs =
                minPerOpNs 9 100 MeasuredOps (fun () -> productionRing MeasuredCapacity ops |> ignore)

            let listNs =
                minPerOpNs 3 1 MeasuredOps (fun () -> modelRing MeasuredCapacity ops |> ignore)

            let arrayNs =
                minPerOpNs 9 100 MeasuredOps (fun () -> arrayRing MeasuredCapacity ops |> ignore)

            // The ring alone: the same sequence walked from an array, with
            // no output list built, so the drivers' allocation is out of
            // the number. A pop is -1; pushed values start at 1.
            let steps =
                ops
                |> List.map (fun op ->
                    match op with
                    | RPush v -> v
                    | RPop -> -1)
                |> Array.ofList

            let shippedAlone () =
                let rb = RingBuffer<int> MeasuredCapacity
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
                "Phase 955 measurement (.NET %s, capacity %d, %d ops, 65%% pushes, min over rounds): shipped Ring.fs %.1f ns/op; list extraction %.0f ns/op; array extraction %.1f ns/op (%.2fx the shipped ring). Ring alone, no output list: shipped %.1f ns/op; array extraction %.1f ns/op (%.2fx)."
                (string Environment.Version)
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
                "every measurement ran"
        }
    ]

let tests =
    testList "Phase 788 - the proved Elmish runtime as oracle" [

        test "the extracted ring agrees with the production ring over generated push/pop sequences" {
            let mismatches =
                ringCases.Force()
                |> List.choose (fun c ->
                    describeRingMismatch c.Capacity c.Ops (productionRing c.Capacity c.Ops) c.Expected)

            Expect.isEmpty
                mismatches
                (sprintf
                    "%d sequence(s) on which the production ring and the proved model popped differently:\n%s"
                    (List.length mismatches)
                    (String.concat "\n" (List.truncate 10 mismatches)))
        }

        test "the campaign grew the ring past several doublings" {
            // The campaign has to be shown to REACH the grow step, or the
            // comparison above says nothing about `doubleSize`. Capacity
            // tops out at 13; three doublings of that is 111 slots.
            let maxSlots =
                ringCases.Force()
                |> List.map (fun c -> modelSlots (snd (modelRing c.Capacity c.Ops)))
                |> List.max

            Expect.isGreaterThan
                maxSlots
                111
                $"the largest backing array the model reached held {maxSlots} slots — fewer than three doublings of the largest capacity, so the grow step was barely exercised"
        }

        test "the extracted diff agrees with the production diff and change over generated subscription sets" {
            let mismatches =
                diffCases.Force()
                |> List.collect (fun c -> describeDiffMismatch c.Input (productionDiffShape c.Input) c.Expected)

            Expect.isEmpty
                mismatches
                (sprintf
                    "%d input(s) on which production and the proved model diffed differently:\n%s"
                    (List.length mismatches)
                    (String.concat "\n" (List.truncate 10 mismatches)))
        }

        test "the campaign exercised the shortcut and the duplicate path" {
            let classified = diffCases.Force() |> List.map (fun c -> classifyDiffInput c.Input)
            let fastPath = classified |> List.filter fst |> List.length
            let withDupes = classified |> List.filter snd |> List.length

            Expect.isGreaterThan fastPath 30 $"only {fastPath} inputs hit the `keys = newKeys` shortcut"
            Expect.isGreaterThan withDupes 30 $"only {withDupes} inputs carried a duplicate key"
        }

        test "a ring that skips the wrap check is caught - go-red" {
            // The falsifying probe. Without it, every agreement above is
            // consistent with a comparison that agrees with everything.
            let caught =
                ringCases.Force()
                |> List.filter (fun c -> brokenRing c.Capacity c.Ops <> c.Expected)
                |> List.length

            Expect.isGreaterThan
                caught
                0
                "a ring whose write head runs over unread slots instead of growing was never caught by the comparison"
        }

        test "a diff that starts a key it also keeps is caught - go-red" {
            let caught =
                diffCases.Force()
                |> List.filter (fun c -> brokenDiffShape c.Input <> c.Expected)
                |> List.length

            Expect.isGreaterThan
                caught
                0
                "a diff that returns an active key in `toStart` was never caught by the comparison"
        }

        test "the floor is one number, and the model's precondition covers it" {
            // 788.D — the constructor and `Program.withRingBufferCapacity`
            // read one constant, and it is the model's `minimum_capacity`,
            // which every ring theorem assumes.
            Expect.equal RingBuffer<int>.MinimumCapacity 2 "the shipped floor"

            Expect.equal
                (int ElmishRing.minimum_capacity)
                RingBuffer<int>.MinimumCapacity
                "the proof's precondition and the shipped floor are the same number"

            let program =
                Program.mkSimple (fun () -> ()) (fun () () -> ()) (fun () _ -> ())
                |> Program.withRingBufferCapacity 1

            Expect.equal
                (Program.ringBufferCapacity program)
                RingBuffer<int>.MinimumCapacity
                "`withRingBufferCapacity` clamps to the same floor rather than to 1"

            // …and a ring asked for the floor behaves as the model says a
            // two-slot ring does: FIFO through its first grow.
            let ops = [ RPush 1; RPush 2; RPush 3; RPop; RPop; RPop; RPop ]
            Expect.isNone (describeRingMismatch 2 ops (productionRing 2 ops) (fst (modelRing 2 ops))) "at the floor"
        }

        test "at capacity one the model loses an item - why the floor exists" {
            // `capacity_one_loses_an_item`, run: two pushes, one pop, and
            // the SECOND item comes out. The constructor floors precisely
            // so this state is unreachable.
            let one: ElmishRing.ring<int> =
                ElmishRing.Writable([ ElmishRing.Placeholder ], big 0)

            let outs =
                match ElmishRing.run one [ ElmishRing.Push 1; ElmishRing.Push 2; ElmishRing.Pop ] with
                | ElmishRing.Pair(_, outs) ->
                    outs
                    |> List.map (fun o ->
                        match ofModelOutput o with
                        | Ok v -> v
                        | Error e -> failtest e)

            Expect.equal
                outs
                [ Some 2 ]
                "a one-slot ring returns the second item first — the first was overwritten before the grow step ran"
        }

        // ─── The generators, pinned across hosts (Phase 900) ─────────

        test "the ring campaign is the draw both hosts make - its fingerprint is pinned - Phase 900" {
            let violations = ringCampaignPinViolations ()

            Expect.isEmpty
                violations
                ("ring campaign drift: pin the printed value in RingCampaignFingerprint if the generator moved on purpose\n"
                 + String.concat "\n" violations)
        }

        test "the diff campaign is the draw both hosts make - its fingerprint is pinned - Phase 900" {
            let violations = diffCampaignPinViolations ()

            Expect.isEmpty
                violations
                ("diff campaign drift: pin the printed value in DiffCampaignFingerprint if the generator moved on purpose\n"
                 + String.concat "\n" violations)
        }

        // ─── The corpus the Fable host replays ───────────────────────

        test "tests/elmish-proof-corpus/ring-cases.txt matches the model (regenerable)" {
            goldenFile RingCorpusFile renderRingCorpus "the ring sequences and their expected outputs"
        }

        test "tests/elmish-proof-corpus/diff-cases.txt matches the model (regenerable)" {
            goldenFile DiffCorpusFile renderDiffCorpus "the subscription inputs and their expected shapes"
        }

        test "the corpus format round-trips" {
            // The parser is what the Fable host reads with, and it is
            // never run on this host otherwise — so a formatter/parser
            // disagreement would surface only as a mysterious Fable
            // failure.
            for c in ringCases.Force() |> List.truncate 20 do
                Expect.equal (parseRingCase (formatRingCase c)) c "ring case round-trip"

            for c in diffCases.Force() |> List.truncate 50 do
                Expect.equal (parseDiffCase (formatDiffCase c)) c "diff case round-trip"

            Expect.equal
                (corpusLines "# header\r\nline one\n\nline two\n")
                [ "line one"; "line two" ]
                "the reader drops the header, blank lines and CR"
        }

        // ─── Phase 850 — the spike's measurement, .NET half ──────────

        test "Phase 850 - the extracted ring against the shipped ring, measured (informational)" {
            // A local stopwatch, not Phase 849's harness (in flight when
            // this was written). Asserts only that both ran the same
            // sequence to the same answer; the numbers are printed for the
            // record in proofs/README.md's Phase 850 section, where their
            // falsifier is stated. The Fable pack runs the same
            // measurement on node. 4,000 ops at 65% pushes grows the ring
            // through several doublings.
            let capacity = 10
            let opCount = 4_000
            let ops = genRingOps (Lcg 850_001) opCount 65
            let produced = productionRing capacity ops
            let modelled = fst (modelRing capacity ops)
            Expect.equal modelled produced "the measured runs must still agree"

            let perOpNs (rounds: int) (body: unit -> unit) =
                let watch = Diagnostics.Stopwatch.StartNew()

                for _ in 1..rounds do
                    body ()

                float watch.Elapsed.TotalMilliseconds * 1_000_000.0 / float (rounds * opCount)

            let productionNs = perOpNs 100 (fun () -> productionRing capacity ops |> ignore)
            let modelNs = perOpNs 3 (fun () -> modelRing capacity ops |> ignore)

            printfn
                "Phase 850 measurement (.NET, capacity %d, %d ops, 65%% pushes): shipped Ring.fs %.0f ns/op; extracted model %.0f ns/op; ratio %.0fx"
                capacity
                opCount
                productionNs
                modelNs
                (modelNs / productionNs)

            Expect.isTrue (productionNs > 0.0 && modelNs > 0.0) "both measurements ran"
        }

        // ─── Phase 955 — the array ring. Nested, so the pack still
        // registers one list for the Elmish runtime's proofs. ─────────
        arrayRingTests
    ]