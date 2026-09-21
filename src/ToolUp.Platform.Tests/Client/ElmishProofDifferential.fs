// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 788 — the differential between the production Elmish runtime
/// and the proved models: the HOST-NEUTRAL half.
///
/// `proofs/ElmishRing.fst` and `proofs/ElmishSub.fst` model
/// `RingBuffer<'item>` and `Sub.Internal.diff` clause for clause and
/// prove the ring a FIFO queue and the diff exactly what its comment
/// says. `proofs/check.ps1` extracts both to F# and byte-compares the
/// result against the committed `proofs/oracle/ElmishRing.fs` /
/// `ElmishSub.fs`.
///
/// **A proof is about the MODEL, and the differential is the only thing
/// that says the model is about the code.** The runtime ships to two
/// hosts — .NET, where the platform test pack runs, and the browser,
/// where Fable's transpilation of the same `Ring.fs` / `Sub.fs` is what
/// every client actually executes — so this module holds everything
/// both hosts need and nothing either cannot compile: the generator,
/// the production drivers, the two committed go-red variants, and the
/// CORPUS format. It is compiled into `ToolUp.Platform.Tests` (Expecto)
/// and into `ToolUp.AI.Client.Tests` (`node:test`), the `WireCorpus.fs`
/// precedent.
///
/// **Where the model runs, and why the corpus exists.** The extracted
/// model compiles on .NET only: the F* extractor emits pre-F#-8 layout
/// that needs `--strict-indentation-`, and Fable reads no `OtherFlags`
/// from an fsproj (checked against the 5.0.0 CLI), so the Fable pack
/// cannot compile the oracle without pinning its `LangVersion` back to
/// 7, which the workspace baseline forbids. So the .NET host runs the
/// model LIVE beside production over the generated sequences, and also
/// writes the model's verdicts for those same sequences to
/// `tests/elmish-proof-corpus/` as a self-describing corpus — each case
/// carries its inputs and the outputs the proved model produced. The
/// Fable host replays that corpus against the transpiled runtime. Both
/// hosts therefore hold the shipped code to the proved model's answer;
/// only one of them computes it.
///
/// Every comparison here returns MISMATCHES (empty is agreement) so
/// either host can assert on it. The generator is a small LCG rather
/// than `System.Random`, so a divergence is reproducible by seed on
/// either host.
///
/// **The go-red cases are committed.** `BrokenRing` skips the wrap check
/// (the write head runs over unread slots instead of growing);
/// `brokenDiffShape` returns a key in both `toStart` and `toKeep`. Each
/// must be CAUGHT over the generated inputs, on both hosts — a
/// differential that has never been shown to fail agrees with whatever
/// it is shown.
module ToolUp.Platform.Tests.Client.ElmishProofDifferential

open System
open ToolUp.Elmish

// ─── A deterministic generator ──────────────────────────────────────

/// A 32-bit LCG (Numerical Recipes constants) — identical on .NET and
/// under Fable, which `System.Random` is not.
type Lcg(seed: int) =
    let mutable state = uint32 seed

    /// The next value in `[0, bound)`.
    member _.Next(bound: int) : int =
        state <- state * 1664525u + 1013904223u
        int ((state >>> 8) % uint32 bound)

/// One seed for the whole differential, on both hosts, so a failure
/// names a reproducible run.
[<Literal>]
let Seed = 788_001

// ─── The ring ───────────────────────────────────────────────────────

/// One operation, host-side.
type RingOp =
    | RPush of int
    | RPop

/// A generated operation sequence. `pushBias` in `[0, 100)` is the
/// percentage of pushes; a high bias grows the ring past several
/// doublings, a low one drains it to empty repeatedly, and the mix is
/// what exercises every clause of `Push` / `Pop`.
let genRingOps (rng: Lcg) (length: int) (pushBias: int) : RingOp list =
    let mutable next = 1

    [
        for _ in 1..length do
            if rng.Next 100 < pushBias then
                let value = next
                next <- next + 1
                RPush value
            else
                RPop
    ]

/// The capacities and sequences one campaign draws: `sequences` base
/// sequences, each also run with a full drain appended so every
/// sequence ends at the empty state at least once. Same draw on both
/// hosts by construction.
let genRingCampaign (seed: int) (sequences: int) : (int * RingOp list) list =
    let rng = Lcg seed

    [
        for _ in 1..sequences do
            let capacity = 2 + rng.Next 12
            let length = 1 + rng.Next 160
            let pushBias = [| 50; 65; 80; 92 |][rng.Next 4]
            let ops = genRingOps rng length pushBias
            yield capacity, ops
            yield capacity, ops @ List.replicate (length + 1) RPop
    ]

/// The production ring over an op sequence — every `Pop`'s result, in
/// order.
let productionRing (capacity: int) (ops: RingOp list) : int option list =
    let rb = RingBuffer<int> capacity

    [
        for op in ops do
            match op with
            | RPush v -> rb.Push v
            | RPop -> yield rb.Pop()
    ]

/// **Go-red.** `RingBuffer` with the wrap check removed: when the write
/// head catches the read head it keeps writing instead of growing, so
/// the oldest unread slot is silently overwritten. Otherwise clause for
/// clause the production ring, including the floor.
type BrokenRing<'item>(size: int) =
    let mutable state: 'item RingState =
        Writable(Array.zeroCreate (max size RingBuffer<'item>.MinimumCapacity), 0)

    member _.Pop() =
        match state with
        | ReadWritable(items, wix, rix) ->
            let rix' = (rix + 1) % items.Length

            match rix' = wix with
            | true -> state <- Writable(items, wix)
            | _ -> state <- ReadWritable(items, wix, rix')

            Some items[rix]
        | _ -> None

    member _.Push(item: 'item) =
        match state with
        | Writable(items, ix) ->
            items[ix] <- item
            let wix = (ix + 1) % items.Length
            state <- ReadWritable(items, wix, ix)
        | ReadWritable(items, wix, rix) ->
            items[wix] <- item
            let wix' = (wix + 1) % items.Length
            // The bug: no `wix' = rix` check, no `doubleSize`.
            state <- ReadWritable(items, wix', rix)

let brokenRing (capacity: int) (ops: RingOp list) : int option list =
    let rb = BrokenRing<int> capacity

    [
        for op in ops do
            match op with
            | RPush v -> rb.Push v
            | RPop -> yield rb.Pop()
    ]

/// One corpus case: a capacity, an op sequence, and the outputs the
/// PROVED MODEL produced for them (computed on .NET from the extraction).
type RingCase = {
    Capacity: int
    Ops: RingOp list
    Expected: int option list
}

/// Describe a divergence between an actual output list and the expected
/// one, or `None` when they agree.
let describeRingMismatch (capacity: int) (ops: RingOp list) (actual: int option list) (expected: int option list) =
    if actual = expected then
        None
    else
        let firstDiff =
            Seq.zip (Seq.append (Seq.map Some actual) (Seq.initInfinite (fun _ -> None))) (Seq.map Some expected)
            |> Seq.tryFindIndex (fun (a, e) -> a <> e)
            |> Option.map string
            |> Option.defaultValue "length"

        Some
            $"capacity {capacity}, {List.length ops} ops: popped {List.length actual} value(s) against the model's {List.length expected}; first divergence at pop #{firstDiff}"

// ─── The ring corpus format ─────────────────────────────────────────
//
// One case per line: `capacity|ops|expected`, ops as `P<n>` / `-`
// separated by spaces, expected as `<n>` / `_` separated by spaces. Plain
// enough to parse with `Split` on both hosts, with no JSON library on
// either.

let formatRingCase (c: RingCase) : string =
    let ops =
        c.Ops
        |> List.map (fun op ->
            match op with
            | RPush v -> $"P{v}"
            | RPop -> "-")
        |> String.concat " "

    let expected =
        c.Expected
        |> List.map (fun e ->
            match e with
            | Some v -> string v
            | None -> "_")
        |> String.concat " "

    $"{c.Capacity}|{ops}|{expected}"

let private splitItems (field: string) : string list =
    if String.IsNullOrWhiteSpace field then
        []
    else
        field.Split(' ') |> Array.toList

let parseRingCase (line: string) : RingCase =
    match line.Split('|') with
    | [| capacity; ops; expected |] -> {
        Capacity = int capacity
        Ops =
            splitItems ops
            |> List.map (fun t -> if t = "-" then RPop else RPush(int (t.Substring 1)))
        Expected = splitItems expected |> List.map (fun t -> if t = "_" then None else Some(int t))
      }
    | _ -> failwithf "malformed ring corpus line: %s" line

// ─── The subscription diff ──────────────────────────────────────────

/// A running subscription's handle, identified so the differential can
/// assert the production diff carried the SAME object through rather
/// than an equal-looking one.
type Handle(id: int) =
    member _.Id = id

    interface IDisposable with
        member _.Dispose() = ()

/// The key alphabet — short `SubId`s, some sharing a prefix, because
/// the diff compares whole keys and a prefix-only comparison would be
/// a bug worth catching. No key contains the corpus separators.
let alphabet: SubId list = [
    [ "a" ]
    [ "b" ]
    [ "c" ]
    [ "a"; "x" ]
    [ "a"; "y" ]
    [ "b"; "x" ]
    [ "timer" ]
    [ "timer"; "fast" ]
]

/// A diff input, as DATA: active keys with their handle ids, requested
/// keys with their subscribe ids. The host materialises the objects.
type DiffInput = {
    Active: (SubId * int) list
    Requested: (SubId * int) list
}

/// Draw an active list (distinct keys, one handle each) and a requested
/// list (may repeat keys; sometimes exactly the active key set, so the
/// shortcut fires). Handles and subscribes are numbered so identity is
/// checkable.
let genDiffInput (rng: Lcg) : DiffInput =
    let activeCount = rng.Next(List.length alphabet + 1)

    let activeKeys =
        [
            for i in 0 .. activeCount - 1 -> alphabet[(rng.Next(List.length alphabet) + i) % List.length alphabet]
        ]
        |> List.distinct

    let active = activeKeys |> List.mapi (fun i key -> key, 100 + i)

    let requested =
        match rng.Next 4 with
        | 0 ->
            // Exactly the active key set, possibly reordered — the
            // shortcut's case.
            activeKeys
            |> List.sortBy (fun _ -> rng.Next 1000)
            |> List.mapi (fun i key -> key, 200 + i)
        | _ ->
            let count = rng.Next 9
            [ for i in 0 .. count - 1 -> alphabet[rng.Next(List.length alphabet)], 300 + i ]

    {
        Active = active
        Requested = requested
    }

let genDiffCampaign (seed: int) (inputs: int) : DiffInput list =
    let rng = Lcg seed
    [ for _ in 1..inputs -> genDiffInput rng ]

/// What a diff produced, as DATA: keys plus the id of the handle or
/// subscribe carried through, and the keys `change` left active.
type DiffShape = {
    Dupes: SubId list
    ToStop: (SubId * int) list
    ToKeep: (SubId * int) list
    ToStart: (SubId * int) list
    ChangeKeys: SubId list
}

/// Materialise an input: real handles and real subscribe functions, each
/// recoverable to its id BY REFERENCE, so a production output that
/// substituted an equal-looking object would not resolve.
let private materialise (input: DiffInput) =
    let active =
        input.Active |> List.map (fun (key, id) -> key, (new Handle(id) :> IDisposable))

    let subscribes =
        input.Requested
        |> List.map (fun (key, id) ->
            let subscribe: Subscribe<int> = fun _ -> new Handle(id) :> IDisposable
            key, id, subscribe)

    let requested: Sub<int> = subscribes |> List.map (fun (key, _, s) -> key, s)

    let handleId (d: IDisposable) : int =
        match d with
        | :? Handle as h -> h.Id
        | _ -> -1

    let subscribeId (s: Subscribe<int>) : int =
        subscribes
        |> List.tryPick (fun (_, id, s') -> if obj.ReferenceEquals(s, s') then Some id else None)
        |> Option.defaultValue -1

    active, requested, handleId, subscribeId

let private shapeOf
    (handleId: IDisposable -> int)
    (subscribeId: Subscribe<int> -> int)
    (dupes, toStop, toKeep, toStart)
    (next: (SubId * IDisposable) list)
    : DiffShape =
    {
        Dupes = dupes
        ToStop = toStop |> List.map (fun (k, h) -> k, handleId h)
        ToKeep = toKeep |> List.map (fun (k, h) -> k, handleId h)
        ToStart = toStart |> List.map (fun (k, s) -> k, subscribeId s)
        ChangeKeys = next |> List.map fst
    }

/// Production `Sub.Internal.diff` + `Fx.change` over an input, as a
/// shape.
let productionDiffShape (input: DiffInput) : DiffShape =
    let active, requested, handleId, subscribeId = materialise input
    let quad = Sub.Internal.diff active requested
    let next = Sub.Internal.Fx.change (fun _ -> ()) ignore quad
    shapeOf handleId subscribeId quad next

/// **Go-red.** A diff that computes `toStart` from the requested list
/// without excluding the keys already active — so a key that is both
/// active and requested lands in `toKeep` AND `toStart`, the "started
/// twice" leak `never_both` rules out. `change` runs unchanged over it.
let brokenDiffShape (input: DiffInput) : DiffShape =
    let active, requested, handleId, subscribeId = materialise input
    let keys = active |> List.map fst |> Set.ofList
    let dupes, newKeys, newSubs = Sub.Internal.NewSubs.calculate requested

    let quad =
        if keys = newKeys then
            dupes, [], active, []
        else
            let toKeep, toStop = active |> List.partition (fun (k, _) -> Set.contains k newKeys)

            dupes, toStop, toKeep, newSubs

    let next = Sub.Internal.Fx.change (fun _ -> ()) ignore quad
    shapeOf handleId subscribeId quad next

/// One corpus case: the input, and the shape the PROVED MODEL produced.
type DiffCase = {
    Input: DiffInput
    Expected: DiffShape
}

let describeKey (key: SubId) : string = String.Join("/", key)

let describeDiffInput (input: DiffInput) : string =
    let keys (xs: (SubId * int) list) =
        xs |> List.map (fun (k, id) -> $"{describeKey k}#{id}") |> String.concat ","

    $"active [{keys input.Active}] requested [{keys input.Requested}]"

/// The fields on which two shapes differ, named; empty is agreement.
let describeDiffMismatch (input: DiffInput) (actual: DiffShape) (expected: DiffShape) : string list =
    let context = describeDiffInput input

    [
        if actual.Dupes <> expected.Dupes then
            $"dupes differ — {context}"
        if actual.ToStop <> expected.ToStop then
            $"toStop differs — {context}"
        if actual.ToKeep <> expected.ToKeep then
            $"toKeep differs — {context}"
        if actual.ToStart <> expected.ToStart then
            $"toStart differs — {context}"
        if actual.ChangeKeys <> expected.ChangeKeys then
            $"change keys differ — {context}"
    ]

// ─── The diff corpus format ─────────────────────────────────────────
//
// One case per line: `active|requested|dupes|stop|keep|start|change`,
// items comma-separated, a key as `a/x`, a keyed id as `a/x#101`.

let private formatKeyed (xs: (SubId * int) list) : string =
    xs |> List.map (fun (k, id) -> $"{describeKey k}#{id}") |> String.concat ","

let private formatKeys (xs: SubId list) : string =
    xs |> List.map describeKey |> String.concat ","

let formatDiffCase (c: DiffCase) : string =
    String.concat "|" [
        formatKeyed c.Input.Active
        formatKeyed c.Input.Requested
        formatKeys c.Expected.Dupes
        formatKeyed c.Expected.ToStop
        formatKeyed c.Expected.ToKeep
        formatKeyed c.Expected.ToStart
        formatKeys c.Expected.ChangeKeys
    ]

let private parseKey (text: string) : SubId = text.Split('/') |> Array.toList

let private parseKeys (field: string) : SubId list =
    if String.IsNullOrWhiteSpace field then
        []
    else
        field.Split(',') |> Array.toList |> List.map parseKey

let private parseKeyed (field: string) : (SubId * int) list =
    if String.IsNullOrWhiteSpace field then
        []
    else
        field.Split(',')
        |> Array.toList
        |> List.map (fun item ->
            match item.Split('#') with
            | [| key; id |] -> parseKey key, int id
            | _ -> failwithf "malformed keyed item: %s" item)

let parseDiffCase (line: string) : DiffCase =
    match line.Split('|') with
    | [| active; requested; dupes; stop; keep; start; change |] -> {
        Input = {
            Active = parseKeyed active
            Requested = parseKeyed requested
        }
        Expected = {
            Dupes = parseKeys dupes
            ToStop = parseKeyed stop
            ToKeep = parseKeyed keep
            ToStart = parseKeyed start
            ChangeKeys = parseKeys change
        }
      }
    | _ -> failwithf "malformed diff corpus line: %s" line

/// Whether an input hits the shortcut (its requested key set equals the
/// active one) and whether it carries a duplicate key — the two paths a
/// campaign must be shown to have exercised.
let classifyDiffInput (input: DiffInput) : bool * bool =
    let activeKeys = input.Active |> List.map fst |> Set.ofList
    let requestedKeys = input.Requested |> List.map fst |> Set.ofList
    activeKeys = requestedKeys, List.length input.Requested <> Set.count requestedKeys

// ─── The corpus files ───────────────────────────────────────────────

/// The corpus directory, relative to the repository root.
[<Literal>]
let CorpusDir = "tests/elmish-proof-corpus"

[<Literal>]
let RingCorpusFile = "ring-cases.txt"

[<Literal>]
let DiffCorpusFile = "diff-cases.txt"

/// The header every corpus file carries; lines starting with `#` are
/// skipped by the reader.
let corpusHeader (what: string) : string list = [
    $"# GENERATED FILE — do not edit by hand. Phase 788: {what}, and the outputs the PROVED MODEL"
    "# (proofs/ElmishRing.fst / ElmishSub.fst, extracted to proofs/oracle/) produced for them on .NET."
    "# Regenerate with dev-scripts/generate-elmish-proof-corpus.ps1; ToolUp.Platform.Tests compares this"
    "# file against the live model on every run, and ToolUp.AI.Client.Tests replays it against the"
    $"# Fable-transpiled runtime. Seed {Seed}."
]

let corpusLines (text: string) : string list =
    text.Split('\n')
    |> Array.toList
    |> List.map (fun l -> l.TrimEnd('\r'))
    |> List.filter (fun l -> l <> "" && not (l.StartsWith "#"))