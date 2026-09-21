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
// The runtime ships to two hosts, and only this one can compile the
// extraction (see the header of `Client/ElmishProofDifferential.fs`).
// So this pack ALSO writes the model's verdicts for the campaign to
// `tests/elmish-proof-corpus/` as a self-describing corpus, holds that
// file to the live model on every run (regenerable under
// `TOOLUP_REGEN_ELMISH_PROOF_CORPUS=1`), and the Fable pack replays it
// against the transpiled runtime. Two hosts hold the shipped code to
// the proved model's answer; one computes it.
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
    ]