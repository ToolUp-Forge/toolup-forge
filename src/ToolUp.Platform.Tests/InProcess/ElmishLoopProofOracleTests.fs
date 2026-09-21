// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ElmishLoopProofOracleTests

open System.Numerics
open Expecto
open ToolUp.Elmish
open ToolUp.Platform.Tests.Client.ElmishProofDifferential

// ─── Phase 789 — the proved dispatch loop as oracle ──────────────────
//
// `proofs/ElmishLoop.fst` models `Program.runWithDispatch` as a step
// machine over the ring, the `reentered` latch, the `terminated` latch
// and the model, with every impure callee abstracted as an oracle that
// says which messages it re-dispatches and whether it calls `Terminate`.
// It proves the loop hands `update` every accepted message exactly once,
// in dispatch order, that a reentrant dispatch is neither lost nor
// reordered, that termination is absorbing, that the boot drain IS the
// steady-state critical section, and that `DispatcherCore.active` is
// the loop's `terminated` negated. `proofs/check.ps1` extracts it to F#
// and byte-compares the result against the committed
// `proofs/oracle/ElmishLoop.fs`, which this pack references and runs.
//
// **A proof is about the MODEL, and this pack is the only thing that
// says the model is about the code.** Every arm below is differential:
// a generated SCRIPT — which messages `init`'s command raises, which
// each message's command raises (including `Terminate`), which messages
// terminate, and what the outside world dispatches afterwards — is run
// through the real `Program.runWithDispatch` with a scripted `update`,
// and through the extracted machine with the same script as its oracle,
// and the two must agree on the messages `update` saw in order, on the
// messages `dispatch` accepted, on the final model, and on `IsActive`.
//
// The `log` comparison is what holds the two-flag encoding: production's
// log is recorded through `IDispatcher.IsActive`, the model's through
// its `terminated` cell, so a state in which the two disagree logs
// differently on the next dispatch.
//
// The go-red case is a hand-transcribed loop skeleton with the latch
// released BEFORE the drain instead of after it — the defect the latch
// exists to prevent — run over the same campaign and asserted CAUGHT;
// the same skeleton, faithful, is asserted to agree, so the difference
// the go-red measures is the one line.

// ─── The script ──────────────────────────────────────────────────────

/// What a callee raises, synchronously, while a message is processed.
type Ev =
    | EMsg of int
    | ETerm

/// What the outside world does between drains.
type Ext =
    | XDispatch of int
    | XTerminate

type Script = {
    Capacity: int
    /// Raised by `init`'s command, during the boot drain.
    Init: Ev list
    /// Raised by each message's command. Absent means none. Every
    /// reply names a STRICTLY GREATER id than its parent, so a script
    /// is a DAG and every drain finishes — the generator's guarantee,
    /// not the loop's.
    Replies: Map<int, Ev list>
    /// The termination predicate's set.
    Terminating: Set<int>
    Exts: Ext list
}

let private replies (script: Script) (msg: int) : Ev list =
    Map.tryFind msg script.Replies |> Option.defaultValue []

/// What both sides are compared on.
type Outcome = {
    /// The messages `update` was handed, in order.
    Trace: int list
    /// The messages `dispatch` accepted, in order.
    Log: int list
    Model: int list
    Active: bool
    /// The model's drain finished (its latch is clear). Production
    /// cannot say otherwise — it would not have returned.
    Finished: bool
}

// ─── The generator ───────────────────────────────────────────────────

/// A script over ids `0 .. n-1`. Replies point forward only.
let private genScript (rng: Lcg) : Script =
    let n = 3 + rng.Next 6
    let capacity = 2 + rng.Next 4

    let genEv (lo: int) : Ev option =
        if lo >= n then None
        elif rng.Next 100 < 8 then Some ETerm
        else Some(EMsg(lo + rng.Next(n - lo)))

    let genEvs (lo: int) (max: int) : Ev list =
        [ for _ in 1 .. rng.Next(max + 1) -> genEv lo ] |> List.choose id

    let replies =
        [
            for m in 0 .. n - 1 do
                if rng.Next 100 < 70 then
                    m, genEvs (m + 1) 3
        ]
        |> List.filter (fun (_, evs) -> not (List.isEmpty evs))
        |> Map.ofList

    let terminating =
        [
            for m in 0 .. n - 1 do
                if rng.Next 100 < 10 then
                    m
        ]
        |> Set.ofList

    let exts = [
        for _ in 1 .. rng.Next 7 do
            if rng.Next 100 < 12 then
                XTerminate
            else
                XDispatch(rng.Next n)
    ]

    {
        Capacity = capacity
        Init = genEvs 0 3
        Replies = replies
        Terminating = terminating
        Exts = exts
    }

let private campaign: Lazy<Script list> =
    lazy
        (let rng = Lcg 789_001
         [ for _ in 1..400 -> genScript rng ])

// ─── Production ──────────────────────────────────────────────────────

/// The real loop, driven by the script. `update` records what it is
/// handed and returns a command that raises the script's replies; the
/// dispatcher handle captured at start is how `Terminate` is raised
/// from inside and how the outside world dispatches afterwards.
let private productionRun (script: Script) : Outcome =
    let trace = ResizeArray<int>()
    let log = ResizeArray<int>()
    let mutable model: int list = []
    let mutable dispatcher: IDispatcher<int> option = None

    let handle () =
        match dispatcher with
        | Some d -> d
        | None -> failtest "the dispatcher handle was not captured before init's command ran"

    let raise (dispatch: int -> unit) (evs: Ev list) =
        for ev in evs do
            match ev with
            | EMsg m ->
                if (handle ()).IsActive then
                    log.Add m

                dispatch m
            | ETerm -> (handle ()).Terminate()

    let init () =
        [], Cmd.ofEffect (fun dispatch -> raise dispatch script.Init)

    let update (msg: int) (model: int list) =
        trace.Add msg
        model @ [ msg ], Cmd.ofEffect (fun dispatch -> raise dispatch (replies script msg))

    Program.mkProgram init update (fun _ _ -> ())
    |> Program.withSetState (fun m _ -> model <- m)
    |> Program.withTermination (fun msg -> script.Terminating.Contains msg) ignore
    |> Program.withRingBufferCapacity script.Capacity
    |> Program.withDispatcherHandle (fun d -> dispatcher <- Some d)
    |> Program.runWithDispatch id ()

    for ext in script.Exts do
        match ext with
        | XDispatch m ->
            if (handle ()).IsActive then
                log.Add m

            (handle ()).Dispatch m
        | XTerminate -> (handle ()).Terminate()

    {
        Trace = List.ofSeq trace
        Log = List.ofSeq log
        Model = model
        Active = (handle ()).IsActive
        Finished = true
    }

// ─── The extracted machine ───────────────────────────────────────────

let private big (n: int) : BigInteger = BigInteger n

let private toModelEv (ev: Ev) : ElmishLoop.ev<int> =
    match ev with
    | EMsg m -> ElmishLoop.Msg m
    | ETerm -> ElmishLoop.Term

let private toModelExt (ext: Ext) : ElmishLoop.ext<int> =
    match ext with
    | XDispatch m -> ElmishLoop.XDispatch m
    | XTerminate -> ElmishLoop.XTerminate

/// Enough for any script the generator produces; `Finished` says
/// whether it was.
let private fuel = big 100_000

let private modelRun (script: Script) : Outcome =
    let update (msg: int) (model: int list) =
        ElmishRing.Pair(model @ [ msg ], replies script msg |> List.map toModelEv)

    let s =
        ElmishLoop.program
            fuel
            update
            (fun msg -> script.Terminating.Contains msg)
            (big script.Capacity)
            []
            (List.map toModelEv script.Init)
            (List.map toModelExt script.Exts)

    {
        Trace = s.trace
        Log = s.log
        Model = s.model
        Active = s.active
        Finished = not s.reentered
    }

// ─── The skeleton, and its go-red ────────────────────────────────────

type private Variant =
    | Faithful
    /// The defect: the latch is released before the drain rather than
    /// after it, so a dispatch from inside `update` finds it clear and
    /// recurses into a nested drain — a child is processed before its
    /// waiting siblings, and the outer `state <- model'` then overwrites
    /// the model the nested drain built.
    | ClearsLatchBeforeDrain

/// `runWithDispatch`'s scheduling skeleton, transcribed by hand with the
/// callees replaced by the script — the same abstraction the model
/// makes, in F#, over the production ring. Faithful, it must agree with
/// the model; with the one line moved, it must be caught.
let private skeletonRun (variant: Variant) (script: Script) : Outcome =
    let trace = ResizeArray<int>()
    let log = ResizeArray<int>()
    let rb = RingBuffer<int> script.Capacity
    let mutable reentered = false
    let mutable terminated = false
    let mutable state: int list = []

    let terminate () =
        if not terminated then
            terminated <- true

    let rec dispatch msg =
        if not terminated then
            rb.Push msg

            if not reentered then
                match variant with
                | Faithful ->
                    reentered <- true
                    processMsgs ()
                    reentered <- false
                | ClearsLatchBeforeDrain ->
                    reentered <- true
                    reentered <- false
                    processMsgs ()

    and raise (evs: Ev list) =
        for ev in evs do
            match ev with
            | EMsg m ->
                if not terminated then
                    log.Add m

                dispatch m
            | ETerm -> terminate ()

    and processMsgs () =
        let mutable nextMsg = rb.Pop()

        while not terminated && Option.isSome nextMsg do
            let msg = nextMsg.Value

            if script.Terminating.Contains msg then
                terminated <- true
            else
                trace.Add msg
                let model' = state @ [ msg ]
                raise (replies script msg)
                state <- model'

            nextMsg <- rb.Pop()

    reentered <- true
    raise script.Init
    processMsgs ()
    reentered <- false

    for ext in script.Exts do
        match ext with
        | XDispatch m ->
            if not terminated then
                log.Add m

            dispatch m
        | XTerminate -> terminate ()

    {
        Trace = List.ofSeq trace
        Log = List.ofSeq log
        Model = state
        Active = not terminated
        Finished = true
    }

// ─── Reporting ───────────────────────────────────────────────────────

let private describe (script: Script) (actual: Outcome) (expected: Outcome) : string option =
    if actual = expected then
        None
    else
        Some(sprintf "script %A\n  actual   %A\n  expected %A" script actual expected)

let private mismatches (run: Script -> Outcome) : string list =
    campaign.Force()
    |> List.choose (fun script -> describe script (run script) (modelRun script))

/// A reply list that raises `Terminate` and then goes on dispatching —
/// the messages after it must be dropped.
let private termThenMsg (evs: Ev list) =
    match List.tryFindIndex ((=) ETerm) evs with
    | Some i -> evs |> List.skip (i + 1) |> List.exists (fun e -> e <> ETerm)
    | None -> false

/// A parent with at least two replies, one of which replies in turn —
/// the shape on which a nested drain changes the order.
let private nestedWithSibling (script: Script) =
    script.Replies
    |> Map.exists (fun _ evs ->
        List.length evs >= 2
        && evs
           |> List.exists (fun e ->
               match e with
               | EMsg c -> not (List.isEmpty (replies script c))
               | ETerm -> false))

let tests =
    testList "Phase 789 - the proved dispatch loop as oracle" [

        test "the extracted machine agrees with the production loop over generated reentrancy scripts" {
            let found = mismatches productionRun

            Expect.isEmpty
                found
                (sprintf
                    "%d script(s) on which the production loop and the proved model processed differently:\n%s"
                    (List.length found)
                    (String.concat "\n" (List.truncate 5 found)))
        }

        test "every drain in the campaign finished - the fuel bound never decided an agreement" {
            let stalled =
                campaign.Force()
                |> List.filter (fun s -> not (modelRun s).Finished)
                |> List.length

            Expect.equal
                stalled
                0
                "the model reported a stalled drain; the generator's forward-only guarantee is broken"
        }

        test "the campaign re-dispatched from update, from the boot drain, and after Terminate" {
            // The comparison above says nothing about a clause the campaign
            // never reached. Each shape the phase names is counted.
            let scripts = campaign.Force()
            let count p = scripts |> List.filter p |> List.length

            let nested = count nestedWithSibling
            let bootMulti = count (fun s -> List.length (List.filter ((<>) ETerm) s.Init) >= 2)

            let termInside =
                count (fun s -> s.Replies |> Map.exists (fun _ evs -> termThenMsg evs))

            let termThenDispatch =
                count (fun s ->
                    match List.tryFindIndex ((=) XTerminate) s.Exts with
                    | Some i -> s.Exts |> List.skip (i + 1) |> List.exists ((<>) XTerminate)
                    | None -> false)

            let terminatedByPredicate =
                count (fun s ->
                    let o = productionRun s in
                    not o.Active && Set.intersect s.Terminating (Set.ofList o.Log) <> Set.empty)

            let stillActive = count (fun s -> (productionRun s).Active)

            Expect.isGreaterThan nested 40 $"only {nested} scripts nest a re-dispatch under a sibling"
            Expect.isGreaterThan bootMulti 40 $"only {bootMulti} scripts raise two or more messages from the boot drain"

            Expect.isGreaterThan
                termInside
                20
                $"only {termInside} scripts dispatch after a Terminate raised from inside"

            Expect.isGreaterThan
                termThenDispatch
                40
                $"only {termThenDispatch} scripts dispatch from outside after a Terminate"

            Expect.isGreaterThan
                terminatedByPredicate
                40
                $"only {terminatedByPredicate} scripts reached the termination predicate"

            Expect.isGreaterThan stillActive 100 $"only {stillActive} scripts ended still active"
        }

        test "the hand-transcribed skeleton agrees with the model" {
            // So the go-red below measures one line and not the transcription.
            let found = mismatches (skeletonRun Faithful)

            Expect.isEmpty
                found
                (sprintf
                    "%d script(s) on which the faithful skeleton disagreed:\n%s"
                    (List.length found)
                    (String.concat "\n" (List.truncate 5 found)))
        }

        test "a loop that releases the latch before draining is caught - go-red" {
            // The falsifying probe. Without it, every agreement above is
            // consistent with a comparison that agrees with everything.
            let caught = mismatches (skeletonRun ClearsLatchBeforeDrain) |> List.length

            Expect.isGreaterThan
                caught
                0
                "a loop whose reentrant dispatches recurse into nested drains was never caught by the comparison"
        }

        test "a single-message boot drain and a steady-state dispatch process the same trace - boot_drain_equiv, run" {
            let differing =
                campaign.Force()
                |> List.choose (fun script ->
                    // The first non-Terminate message the script would raise
                    // at boot, or its first external dispatch — one message,
                    // raised from `init`'s command versus dispatched from
                    // outside, with no other external events.
                    let candidate =
                        script.Init
                        |> List.tryPick (fun e ->
                            match e with
                            | EMsg m -> Some m
                            | ETerm -> None)

                    match candidate with
                    | None -> None
                    | Some m ->
                        let viaBoot =
                            productionRun {
                                script with
                                    Init = [ EMsg m ]
                                    Exts = []
                            }

                        let viaDispatch =
                            productionRun {
                                script with
                                    Init = []
                                    Exts = [ XDispatch m ]
                            }

                        describe script viaBoot viaDispatch)

            Expect.isEmpty
                differing
                (sprintf
                    "%d script(s) on which boot and dispatch differed:\n%s"
                    (List.length differing)
                    (String.concat "\n" (List.truncate 5 differing)))
        }

        test "after Terminate nothing is processed and IsActive is false - terminated_absorbing, run" {
            let violations =
                campaign.Force()
                |> List.choose (fun script ->
                    let upTo =
                        productionRun {
                            script with
                                Exts = script.Exts @ [ XTerminate ]
                        }

                    let after =
                        productionRun {
                            script with
                                Exts = script.Exts @ [ XTerminate; XDispatch 0; XDispatch 1; XTerminate; XDispatch 2 ]
                        }

                    if
                        after.Trace <> upTo.Trace
                        || after.Log <> upTo.Log
                        || after.Model <> upTo.Model
                        || after.Active
                    then
                        Some(sprintf "script %A\n  up to Terminate %A\n  after           %A" script upTo after)
                    else
                        None)

            Expect.isEmpty violations (String.concat "\n" (List.truncate 5 violations))
        }

        test "the fallback arm drives the two flags apart - the encoding's one exception" {
            // `fallback_breaks_encoding`, on production: a dispatcher exposed
            // without its terminate callback clears `active` and leaves the
            // loop untouched — `IDispatcher.Dispatch` refuses while the
            // wired `dispatch` still runs. The runtime never wires the
            // interface this way; the arm reports the defect, and this pins
            // that it is the ONE state the proved invariant excludes.
            let seen = ResizeArray<int>()
            let reported = ResizeArray<exn>()
            let core = DispatcherCore<int>()
            core.Wire seen.Add
            let handle = core.AsInterface reported.Add

            Expect.isTrue handle.IsActive "wired"
            handle.Terminate()
            Expect.isFalse handle.IsActive "the fallback cleared `active`"
            Expect.equal reported.Count 1 "…and reported the wiring defect"
            handle.Dispatch 1
            Expect.isEmpty seen "the interface refuses"

            // The model computes the same state.
            let s = ElmishLoop.fallback_terminate (ElmishLoop.initial (big 2) ([]: int list))
            Expect.isFalse s.active "the model's `active`"
            Expect.isFalse s.terminated "…while the loop's flag is untouched"
        }
    ]