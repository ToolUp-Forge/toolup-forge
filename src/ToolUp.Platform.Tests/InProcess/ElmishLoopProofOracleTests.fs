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
// machine over the ring, the `reentered` latch, the `terminated` latch,
// the model and — since Phase 851 — the `dirty` flag, with every impure
// callee abstracted as an oracle that says which messages it
// re-dispatches and whether it calls `Terminate`. Two oracles since 851:
// `update` (update + subscribe + Subs.change + Cmd.exec, per message) and
// `render` (the render hook, once per drain when the ring is empty, with
// the model the drain ended on). It proves the loop hands `update` every
// accepted message exactly once, in dispatch order, that a reentrant
// dispatch is neither lost nor reordered (nor painted), that termination
// is absorbing (paint included), that the boot drain IS the steady-state
// critical section over the boot paint, that `DispatcherCore.active` is
// the loop's `terminated` negated, that the model on screen is the model
// whenever the loop is idle (`painted_is_model`), and that a dispatch
// from idle paints exactly once when the hook itself dispatches nothing
// (`render_once_per_drain`). `proofs/check.ps1` extracts it to F# and
// byte-compares the result against the committed
// `proofs/oracle/ElmishLoop.fs`, which this pack references and runs.
//
// **A proof is about the MODEL, and this pack is the only thing that
// says the model is about the code.** Every arm below is differential:
// a generated SCRIPT — which messages a dispatcher-handle sink and an
// effect's start function raise BEFORE the boot paint, which messages
// `init`'s command raises, which
// each message's command raises (including `Terminate`), which messages
// the render hook raises when handed a given model (the post-drain
// `setState` dispatch — the case Phase 851 creates), which messages
// terminate, and what the outside world dispatches afterwards — is run
// through the real `Program.runWithDispatch` with a scripted `update`
// and a scripted `setState`, and through the extracted machine with the
// same script as its oracles, and the two must agree on the messages
// `update` saw in order, on the messages `dispatch` accepted, on the
// final model, on `IsActive`, on how many times the hook was called, on
// the model it was last handed, and on the BOOT paint: the model it was
// handed and what `update` had seen by then (`boot_paints_init_model` —
// nothing, whatever the sinks and effects dispatched).
//
// The `log` comparison is what holds the two-flag encoding: production's
// log is recorded through `IDispatcher.IsActive`, the model's through
// its `terminated` cell, so a state in which the two disagree logs
// differently on the next dispatch.
//
// The go-red cases are hand-transcribed loop skeletons with one line
// moved each — the latch released BEFORE the drain; the paint made after
// every message as it was until 851; the paint made once after the
// `while` with no pop after it, so a hook that dispatches leaves its
// message in the ring; the latch set AFTER the sinks and effects ran, as
// it was until the boot was restated, so an effect that dispatches from
// its start function runs a drain ahead of the boot paint — run over the
// same campaign and asserted CAUGHT;
// the same skeleton, faithful, is asserted to agree, so the difference
// each go-red measures is the one line.

// ─── The script ──────────────────────────────────────────────────────

/// What a callee raises, synchronously, while a message is processed or
/// the model is painted.
type Ev =
    | EMsg of int
    | ETerm

/// What the outside world does between drains.
type Ext =
    | XDispatch of int
    | XTerminate

type Script = {
    Capacity: int
    /// Raised BEFORE the boot paint: the head by a dispatcher-handle
    /// sink, the rest by an effect's start function. Any id, because
    /// nothing has been processed yet for a reply to point back at.
    Pre: Ev list
    /// Raised by `init`'s command, during the boot drain.
    Init: Ev list
    /// Raised by each message's command. Absent means none. Every
    /// reply names a STRICTLY GREATER id than its parent, so a script
    /// is a DAG and every drain finishes — the generator's guarantee,
    /// not the loop's.
    Replies: Map<int, Ev list>
    /// Phase 851 — raised by the render hook when handed a model whose
    /// LAST message is the key: the post-drain `setState` dispatch.
    /// Forward-only for the same reason. Absent means the hook is quiet.
    Paints: Map<int, Ev list>
    /// Raised by the render hook when handed the EMPTY model — the boot
    /// paint of a program whose init dispatched nothing yet.
    BootPaint: Ev list
    /// The termination predicate's set.
    Terminating: Set<int>
    Exts: Ext list
}

let private replies (script: Script) (msg: int) : Ev list =
    Map.tryFind msg script.Replies |> Option.defaultValue []

/// What the render hook raises when handed `model` — keyed by the model's
/// last message, so the SAME model always raises the same events (the
/// oracle is a function of the model, as the F* says).
let private paints (script: Script) (model: int list) : Ev list =
    match List.tryLast model with
    | None -> script.BootPaint
    | Some last -> Map.tryFind last script.Paints |> Option.defaultValue []

/// What both sides are compared on.
type Outcome = {
    /// The messages `update` was handed, in order.
    Trace: int list
    /// The messages `dispatch` accepted, in order.
    Log: int list
    Model: int list
    Active: bool
    /// Phase 851 — how many times the render hook was called.
    Renders: int
    /// Phase 851 — the model the render hook was last handed.
    Painted: int list option
    /// The model the render hook was handed FIRST — the boot paint.
    BootPainted: int list option
    /// The messages `update` had been handed when the boot paint ran.
    BootTrace: int list
    /// The model's drain finished (its latch is clear). Production
    /// cannot say otherwise — it would not have returned.
    Finished: bool
}

// ─── The generator ───────────────────────────────────────────────────

/// A script over ids `0 .. n-1`. Replies and paints point forward only.
/// `preRng` is a SECOND generator, drawn from only for `Pre`, so the
/// scripts the campaign held before `Pre` existed are the same scripts
/// with a `Pre` added — the shapes the coverage case counts did not move.
let private genScript (rng: Lcg) (preRng: Lcg) : Script =
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

    // Fewer and shorter than replies: a hook that dispatches is the
    // exception the model admits, not the adapter's behaviour.
    let paintsMap =
        [
            for m in 0 .. n - 1 do
                if rng.Next 100 < 30 then
                    m, genEvs (m + 1) 2
        ]
        |> List.filter (fun (_, evs) -> not (List.isEmpty evs))
        |> Map.ofList

    let bootPaint = if rng.Next 100 < 15 then genEvs 0 2 else []

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

    let pre =
        if preRng.Next 100 < 40 then
            [
                for _ in 1 .. 1 + preRng.Next 3 -> if preRng.Next 100 < 8 then ETerm else EMsg(preRng.Next n)
            ]
        else
            []

    {
        Capacity = capacity
        Pre = pre
        Init = genEvs 0 3
        Replies = replies
        Paints = paintsMap
        BootPaint = bootPaint
        Terminating = terminating
        Exts = exts
    }

let private campaign: Lazy<Script list> =
    lazy
        (let rng = Lcg 789_001
         let preRng = Lcg 789_002
         [ for _ in 1..400 -> genScript rng preRng ])

// ─── Production ──────────────────────────────────────────────────────

/// The real loop, driven by the script. `update` records what it is
/// handed and returns a command that raises the script's replies; the
/// render hook counts itself, records the model it was handed and raises
/// the script's paints; the dispatcher handle captured at start is how
/// `Terminate` is raised from inside and how the outside world dispatches
/// afterwards.
let private productionRun (script: Script) : Outcome =
    let trace = ResizeArray<int>()
    let log = ResizeArray<int>()
    let mutable model: int list = []
    let mutable renders = 0
    let mutable painted: int list option = None
    let mutable bootPainted: int list option = None
    let mutable bootTrace: int list = []
    let mutable dispatcher: IDispatcher<int> option = None

    let handle () =
        match dispatcher with
        | Some d -> d
        | None -> failtest "the dispatcher handle was not captured before anything dispatched"

    // The head of `Pre` is raised by a dispatcher-handle sink, through the
    // handle it was just given; the rest by an effect's start function,
    // through the dispatch it was started with.
    let preFromSink, preFromEffect =
        match script.Pre with
        | [] -> [], []
        | first :: rest -> [ first ], rest

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

    // The model cell is what `update` produces — `state <- model'` in the
    // loop. Until 851 the bridge read it off `setState`, which ran after
    // every `update`; a hook that runs once per drain sees the model only
    // when the drain paints, and a drain that terminates paints nothing.
    let update (msg: int) (model': int list) =
        trace.Add msg
        let next = model' @ [ msg ]
        model <- next
        next, Cmd.ofEffect (fun dispatch -> raise dispatch (replies script msg))

    let setState (m: int list) (dispatch: int -> unit) =
        if renders = 0 then
            bootPainted <- Some m
            bootTrace <- List.ofSeq trace

        renders <- renders + 1
        painted <- Some m
        raise dispatch (paints script m)

    Program.mkProgram init update (fun _ _ -> ())
    |> Program.withSetState setState
    |> Program.withTermination (fun msg -> script.Terminating.Contains msg) ignore
    |> Program.withRingBufferCapacity script.Capacity
    |> Program.withDispatcherHandle (fun d -> dispatcher <- Some d)
    |> Program.withDispatcherHandle (fun d -> raise d.Dispatch preFromSink)
    |> Program.withEffect (
        EffectHandle.programLifetime "scripted-pre-boot" (fun dispatch ->
            raise dispatch preFromEffect

            { new System.IDisposable with
                member _.Dispose() = ()
            })
    )
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
        Renders = renders
        Painted = painted
        BootPainted = bootPainted
        BootTrace = bootTrace
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

    let render (model: int list) =
        paints script model |> List.map toModelEv

    let toTerminate (msg: int) = script.Terminating.Contains msg
    let pre = List.map toModelEv script.Pre

    let s =
        ElmishLoop.program
            fuel
            update
            toTerminate
            render
            (big script.Capacity)
            []
            pre
            (List.map toModelEv script.Init)
            (List.map toModelExt script.Exts)

    // The model's own account of the boot paint: the state `boot` passes
    // through on its way to the drain, which `boot_paints_init_model` is
    // stated over.
    let atBootPaint =
        ElmishLoop.boot_paint fuel update toTerminate render (ElmishLoop.initial (big script.Capacity) []) pre

    let painted (st: ElmishLoop.st<int, int list>) =
        match st.painted with
        | ElmishRing.OSome m -> Some m
        | ElmishRing.ONone -> None

    {
        Trace = s.trace
        Log = s.log
        Model = s.model
        Active = s.active
        Renders = int s.renders
        Painted = painted s
        BootPainted = painted atBootPaint
        BootTrace = atBootPaint.trace
        Finished = not s.reentered
    }

// ─── The skeleton, and its go-reds ───────────────────────────────────

type private Variant =
    | Faithful
    /// The defect: the latch is released before the drain rather than
    /// after it, so a dispatch from inside `update` finds it clear and
    /// recurses into a nested drain — a child is processed before its
    /// waiting siblings, and the outer `state <- model'` then overwrites
    /// the model the nested drain built.
    | ClearsLatchBeforeDrain
    /// Phase 851's before: the hook is called after every `update`, so a
    /// drain of N messages paints N times and the hook's dispatches
    /// arrive mid-drain.
    | PaintsPerMessage
    /// Phase 851's near miss: one paint after the `while`, and no pop
    /// after it — a hook that dispatches leaves its message in the ring
    /// until the next external dispatch happens to drain it.
    | PaintsWithoutRedrain
    /// The boot as it was until it was restated: the sinks and the
    /// effects' start functions run BEFORE the latch is set, so a dispatch
    /// one of them makes finds it clear and runs a whole drain — `update`,
    /// the command, a paint — ahead of the boot paint.
    | LatchesAfterEffects

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
    let mutable dirty = true
    let mutable state: int list = []
    let mutable renders = 0
    let mutable painted: int list option = None
    let mutable bootPainted: int list option = None
    let mutable bootTrace: int list = []

    let terminate () =
        if not terminated then
            terminated <- true

    let rec dispatch msg =
        if not terminated then
            rb.Push msg

            if not reentered then
                match variant with
                | ClearsLatchBeforeDrain ->
                    reentered <- true
                    reentered <- false
                    processMsgs ()
                | Faithful
                | PaintsPerMessage
                | PaintsWithoutRedrain
                | LatchesAfterEffects ->
                    reentered <- true
                    processMsgs ()
                    reentered <- false

    and raise (evs: Ev list) =
        for ev in evs do
            match ev with
            | EMsg m ->
                if not terminated then
                    log.Add m

                dispatch m
            | ETerm -> terminate ()

    and paint () =
        dirty <- false

        if renders = 0 then
            bootPainted <- Some state
            bootTrace <- List.ofSeq trace

        renders <- renders + 1
        painted <- Some state
        raise (paints script state)

    and stepMsg (msg: int) =
        if script.Terminating.Contains msg then
            terminated <- true
        else
            dirty <- true
            trace.Add msg
            let model' = state @ [ msg ]
            raise (replies script msg)
            state <- model'

            match variant with
            | PaintsPerMessage -> paint ()
            | _ -> ()

    and processMsgs () =
        let mutable nextMsg = rb.Pop()

        match variant with
        | Faithful
        | ClearsLatchBeforeDrain
        | LatchesAfterEffects ->
            while not terminated && (Option.isSome nextMsg || dirty) do
                match nextMsg with
                | None ->
                    paint ()
                    nextMsg <- rb.Pop()
                | Some msg ->
                    stepMsg msg
                    nextMsg <- rb.Pop()
        | PaintsPerMessage ->
            while not terminated && Option.isSome nextMsg do
                stepMsg nextMsg.Value
                nextMsg <- rb.Pop()
        | PaintsWithoutRedrain ->
            while not terminated && Option.isSome nextMsg do
                stepMsg nextMsg.Value
                nextMsg <- rb.Pop()

            if not terminated && dirty then
                paint ()

    match variant with
    | LatchesAfterEffects ->
        raise script.Pre
        reentered <- true
    | Faithful
    | ClearsLatchBeforeDrain
    | PaintsPerMessage
    | PaintsWithoutRedrain ->
        reentered <- true
        raise script.Pre

    paint ()
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
        Renders = renders
        Painted = painted
        BootPainted = bootPainted
        BootTrace = bootTrace
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

/// The render hook, handed some model the run actually painted, raised a
/// message — the post-drain `setState` dispatch reached the loop.
let private paintDispatched (script: Script) =
    let o = productionRun script

    let paintedModels =
        // Every prefix of the final model was, at some drain's end, the
        // model; the ones whose last message has a paint entry fired it.
        [ 0 .. List.length o.Model ] |> List.map (fun k -> List.truncate k o.Model)

    paintedModels
    |> List.exists (fun m -> paints script m |> List.exists (fun e -> e <> ETerm))
    && o.Renders > 1

/// A quiet script: no paint anywhere raises anything.
let private quiet (script: Script) =
    Map.isEmpty script.Paints && List.isEmpty script.BootPaint

/// Run `body` with `Console.Error` captured, and hand back what it wrote.
/// The guard's last resort is the console, so this is both how the
/// reporter cases READ what the guard did and how they keep a deliberate
/// failure's stack trace out of the gate's log. The cases that use it are
/// `testSequenced`: the writer is process-wide.
let private capturingStderr (body: unit -> unit) : string =
    let original = System.Console.Error
    use captured = new System.IO.StringWriter()
    System.Console.SetError captured

    try
        body ()
    finally
        System.Console.SetError original

    captured.ToString()

/// How many times `needle` occurs in `text`.
let private occurrences (needle: string) (text: string) : int =
    (text.Length - text.Replace(needle, "").Length) / needle.Length

[<Literal>]
let private ReporterRaised = "The error reporter raised while reporting: "

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

        test "the campaign re-dispatched from update, from the boot drain, from the paint, and after Terminate" {
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

            // Phase 851 — the case the render move creates: a paint that
            // dispatches, so the drain re-opens after the hook ran; and the
            // quiet scripts, on which `render_once_per_drain` is measured.
            let paintFired = count paintDispatched
            let quietScripts = count quiet

            let termFromPaint =
                count (fun s ->
                    s.Paints |> Map.exists (fun _ evs -> List.contains ETerm evs)
                    || List.contains ETerm s.BootPaint)

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
            Expect.isGreaterThan paintFired 80 $"only {paintFired} scripts had the render hook dispatch into the loop"
            Expect.isGreaterThan quietScripts 60 $"only {quietScripts} scripts kept the render hook quiet"
            Expect.isGreaterThan termFromPaint 15 $"only {termFromPaint} scripts raise Terminate from the render hook"
        }

        test "the hand-transcribed skeleton agrees with the model" {
            // So the go-reds below measure one line each and not the transcription.
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

        test "a loop that paints after every message is caught - go-red, Phase 851" {
            // The pre-851 loop. On a script with a drain longer than one
            // message its render count disagrees; on one whose hook
            // dispatches, its trace does too (the hook's message arrives
            // mid-drain rather than after it).
            let caught = mismatches (skeletonRun PaintsPerMessage) |> List.length

            Expect.isGreaterThan
                caught
                100
                "a loop that renders once per message was not caught by the render-count comparison"
        }

        test "a loop that paints once but never re-pops is caught - go-red, Phase 851" {
            // The near miss: paint once after the while, and stop. A hook
            // that dispatches leaves its message in the ring, so the trace
            // is short until an external dispatch happens to drain it.
            let caught = mismatches (skeletonRun PaintsWithoutRedrain) |> List.length

            Expect.isGreaterThan
                caught
                20
                "a loop that does not re-drain after the paint was not caught by the trace comparison"
        }

        test "a single-message boot drain and a steady-state dispatch process the same trace - boot_drain_equiv, run" {
            let differing =
                campaign.Force()
                |> List.choose (fun script ->
                    // The first non-Terminate message the script would raise
                    // at boot, or its first external dispatch — one message,
                    // raised from `init`'s command versus dispatched from
                    // outside, with no other external events. Both boot
                    // paint once and drain-paint once, so `Renders` agrees
                    // too (`boot_single_is_dispatch`: the boot paint, then
                    // `dispatch`). The theorem's premise is a boot paint
                    // that leaves the machine idle — `booted` is the paint
                    // with nothing drained after it — so the boot paint is
                    // kept quiet here; a boot paint that dispatches is the
                    // campaign comparison's business, not this one's.
                    let candidate =
                        script.Init
                        |> List.tryPick (fun e ->
                            match e with
                            | EMsg m -> Some m
                            | ETerm -> None)

                    match candidate with
                    | None -> None
                    | Some m ->
                        // …and nothing raised before the boot paint, the
                        // theorem's other premise: an effect's message
                        // would be one more message in the boot drain and
                        // a drain of its own ahead of the dispatch.
                        let viaBoot =
                            productionRun {
                                script with
                                    Pre = []
                                    Init = [ EMsg m ]
                                    BootPaint = []
                                    Exts = []
                            }

                        let viaDispatch =
                            productionRun {
                                script with
                                    Pre = []
                                    Init = []
                                    BootPaint = []
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

        test "after Terminate nothing is processed or painted and IsActive is false - terminated_absorbing, run" {
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
                        || after.Renders <> upTo.Renders
                        || after.Painted <> upTo.Painted
                        || after.Active
                    then
                        Some(sprintf "script %A\n  up to Terminate %A\n  after           %A" script upTo after)
                    else
                        None)

            Expect.isEmpty violations (String.concat "\n" (List.truncate 5 violations))
        }

        test "a drain of N messages calls the render hook once - render_once_per_drain, run" {
            // Phase 851.A's acceptance, on production with a quiet hook: one
            // external dispatch whose command fans out into a chain of
            // re-dispatches is ONE drain, and the hook runs once at its end
            // — however long the chain. The boot is one paint too.
            let renders = ResizeArray<int list>()
            let mutable handle: IDispatcher<int> option = None

            let update (msg: int) (model: int list) =
                // 0 fans out to 1..4; each of those to its double, up to 40.
                let fanOut =
                    if msg = 0 then [ 1..4 ]
                    elif msg > 0 && msg * 2 <= 40 then [ msg * 2 ]
                    else []

                model @ [ msg ], Cmd.batch [ for m in fanOut -> Cmd.ofMsg m ]

            Program.mkProgram (fun () -> [], Cmd.none) update (fun _ _ -> ())
            |> Program.withSetState (fun m _ -> renders.Add m)
            |> Program.withDispatcherHandle (fun d -> handle <- Some d)
            |> Program.runWithDispatch id ()

            Expect.equal (List.ofSeq renders) [ [] ] "the boot painted the init model exactly once"

            handle.Value.Dispatch 0
            let drained = renders.[renders.Count - 1]

            Expect.equal renders.Count 2 "one external dispatch, one drain, one paint"
            Expect.isGreaterThan (List.length drained) 10 "the drain processed the whole fan-out before painting"
            Expect.equal (List.head drained) 0 "…starting with the dispatched message"

            handle.Value.Dispatch 100
            Expect.equal renders.Count 3 "a second drain, a second paint"
            Expect.equal renders.[2] (drained @ [ 100 ]) "handed the model the drain ended on"
        }

        test "the model on screen is the model whenever the loop is idle - painted_is_model, run" {
            // Over the whole campaign: after production returns from the
            // boot and from every external dispatch, the model the hook was
            // last handed is the model `update` last produced — the hook is
            // never left behind by a drain, whatever the hook itself
            // dispatched.
            let violations =
                campaign.Force()
                |> List.choose (fun script ->
                    let o = productionRun script

                    if o.Active && o.Painted <> Some o.Model then
                        Some(sprintf "script %A\n  painted %A\n  model   %A" script o.Painted o.Model)
                    else
                        None)

            Expect.isEmpty violations (String.concat "\n" (List.truncate 5 violations))
        }

        test "a terminating dispatch paints nothing - render_once_per_drain, the terminated arm, run" {
            let renders = ResizeArray<int list>()
            let mutable handle: IDispatcher<int> option = None

            Program.mkProgram (fun () -> [], Cmd.none) (fun msg model -> model @ [ msg ], Cmd.none) (fun _ _ -> ())
            |> Program.withSetState (fun m _ -> renders.Add m)
            |> Program.withTermination ((=) 9) ignore
            |> Program.withDispatcherHandle (fun d -> handle <- Some d)
            |> Program.runWithDispatch id ()

            handle.Value.Dispatch 1
            Expect.equal renders.Count 2 "boot paint + one drain paint"
            handle.Value.Dispatch 9
            Expect.equal renders.Count 2 "the terminating drain painted nothing"
            Expect.isFalse handle.Value.IsActive "…and terminated"
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

        // ─── The boot, restated: the latch before the sinks and effects ───

        test "the campaign dispatched from a sink and from an effect's start function before the boot paint" {
            // The same rule as the coverage case above, for the clause the
            // restated boot adds: an agreement over scripts that raise
            // nothing before the boot paint says nothing about it.
            let scripts = campaign.Force()
            let count p = scripts |> List.filter p |> List.length
            let isMsg (e: Ev) = e <> ETerm

            let fromSink = count (fun s -> s.Pre |> List.truncate 1 |> List.exists isMsg)

            let fromEffect =
                count (fun s -> s.Pre |> List.skip (min 1 (List.length s.Pre)) |> List.exists isMsg)

            let termBeforeBoot = count (fun s -> List.contains ETerm s.Pre)
            let nothingBefore = count (fun s -> List.isEmpty s.Pre)

            Expect.isGreaterThan fromSink 100 $"only {fromSink} scripts dispatch from a dispatcher-handle sink"
            Expect.isGreaterThan fromEffect 60 $"only {fromEffect} scripts dispatch from an effect's start function"

            Expect.isGreaterThan
                termBeforeBoot
                10
                $"only {termBeforeBoot} scripts raise Terminate before the boot paint"

            Expect.isGreaterThan nothingBefore 150 $"only {nothingBefore} scripts raise nothing before the boot paint"
        }

        test "a loop that sets the latch after the effects ran is caught - go-red" {
            // The boot as it shipped until it was restated. An effect that
            // dispatches from its start function finds the latch clear and
            // runs a drain of its own, so `update` has been handed a message
            // — and the hook a model the server never rendered — before the
            // boot paint.
            let caught = mismatches (skeletonRun LatchesAfterEffects) |> List.length

            Expect.isGreaterThan
                caught
                80
                "a loop whose sinks and effects run before the latch is set was not caught by the boot-paint comparison"
        }

        test
            "an effect that dispatches from its start function does not move the boot paint - boot_paints_init_model, run" {
            let renders = ResizeArray<int list>()
            let seen = ResizeArray<int>()
            let seenAtBootPaint = ResizeArray<int>()

            let update (msg: int) (model: int list) =
                seen.Add msg
                model @ [ msg ], Cmd.none

            Program.mkProgram (fun () -> [], Cmd.ofMsg 3) update (fun _ _ -> ())
            |> Program.withSetState (fun m _ ->
                if renders.Count = 0 then
                    seenAtBootPaint.AddRange seen

                renders.Add m)
            |> Program.withDispatcherHandle (fun d -> d.Dispatch 1)
            |> Program.withEffect (
                EffectHandle.programLifetime "dispatches-at-start" (fun dispatch ->
                    dispatch 2

                    { new System.IDisposable with
                        member _.Dispose() = ()
                    })
            )
            |> Program.runWithDispatch id ()

            Expect.isEmpty seenAtBootPaint "`update` had been handed nothing when the boot paint ran"

            Expect.equal
                (List.ofSeq renders)
                [ []; [ 1; 2; 3 ] ]
                "the boot paint was handed the init model, and ONE drain painted everything raised at boot"

            Expect.equal
                (List.ofSeq seen)
                [ 1; 2; 3 ]
                "the sink's message, the effect's, then init's command's — in the order they were raised"
        }

        test "a subscription an effect's message asks for is running after the boot" {
            // The second thing the old order broke. The early drain started
            // the subscriptions of the model it produced; the boot then
            // diffed against the INIT model's subscriptions, computed before
            // anything ran, and stopped them — leaving a model that wants a
            // subscription with none running until the next message.
            let started = ResizeArray<string>()
            let stopped = ResizeArray<string>()

            let subscribe (model: int list) : Sub<int> =
                if List.contains 1 model then
                    [
                        [ "wanted-once-1-arrived" ],
                        (fun _ ->
                            started.Add "wanted-once-1-arrived"

                            { new System.IDisposable with
                                member _.Dispose() = stopped.Add "wanted-once-1-arrived"
                            })
                    ]
                else
                    []

            Program.mkProgram (fun () -> [], Cmd.none) (fun msg model -> model @ [ msg ], Cmd.none) (fun _ _ -> ())
            |> Program.withSubscription subscribe
            |> Program.withEffect (
                EffectHandle.programLifetime "dispatches-at-start" (fun dispatch ->
                    dispatch 1

                    { new System.IDisposable with
                        member _.Dispose() = ()
                    })
            )
            |> Program.runWithDispatch id ()

            Expect.equal (List.ofSeq started) [ "wanted-once-1-arrived" ] "started, once"
            Expect.isEmpty stopped "…and not stopped by the boot's diff against the init model's subscriptions"
        }

        // ─── The reporter is total by construction ───────────────────────
        //
        // The model has no transition for an error reporter that throws: an
        // exception from a callee is an oracle reply, reported, and the
        // loop goes on. That is only true of a reporter that returns, so
        // production routes every call to the program's reporter through a
        // guard. These pin that it does, at each site a reporter is reached
        // from — a reporter that escaped any of them would leave the latch
        // set, and every later dispatch would queue and never drain.

        testSequenced
        <| test "a reporter that throws does not wedge the loop - from update, from a command, from the render hook" {
            let seen = ResizeArray<int>()
            let renders = ResizeArray<int list>()
            let mutable handle: IDispatcher<int> option = None
            let mutable reports = 0

            let update (msg: int) (model: int list) =
                seen.Add msg

                if msg = 1 then
                    failwith "update raised"

                let cmd =
                    if msg = 2 then
                        Cmd.ofEffect (fun _ -> failwith "the command raised")
                    else
                        Cmd.none

                model @ [ msg ], cmd

            let setState (m: int list) (_: int -> unit) =
                renders.Add m

                if List.tryLast m = Some 3 then
                    failwith "the render hook raised"

            let written =
                capturingStderr (fun () ->
                    Program.mkProgram (fun () -> [], Cmd.none) update (fun _ _ -> ())
                    |> Program.withSetState setState
                    |> Program.withErrorReporter (fun _ ->
                        reports <- reports + 1
                        failwith "the reporter raised")
                    |> Program.withDispatcherHandle (fun d -> handle <- Some d)
                    |> Program.runWithDispatch id ()

                    for msg in [ 1; 2; 3 ] do
                        // Neither may raise into the caller, and the second must be
                        // DRAINED: a latch left set would accept it and process nothing.
                        handle.Value.Dispatch msg
                        handle.Value.Dispatch(msg * 10))

            Expect.equal (List.ofSeq seen) [ 1; 10; 2; 20; 3; 30 ] "every message was handed to `update`, in order"
            Expect.equal reports 3 "each failure reached the reporter once"

            // …and neither failure was lost: the guard wrote the reporter's
            // exception AND the one it had been asked to report.
            Expect.equal (occurrences ReporterRaised written) 3 "the guard said so, once per failure"

            for original in [ "update raised"; "the command raised"; "the render hook raised" ] do
                Expect.stringContains written original "the failure being reported reached the console"

            Expect.equal
                renders.[renders.Count - 1]
                [ 10; 2; 20; 3; 30 ]
                "…and the last drain painted the model it ended on"
        }

        testSequenced
        <| test
            "a reporter that throws does not wedge the boot - from a sink, an effect, a subscription, init's command" {
            let seen = ResizeArray<int>()
            let mutable handle: IDispatcher<int> option = None
            let mutable reports = 0

            let update (msg: int) (model: int list) =
                seen.Add msg
                model @ [ msg ], Cmd.none

            let written =
                capturingStderr (fun () ->
                    Program.mkProgram
                        (fun () -> [], Cmd.ofEffect (fun _ -> failwith "init's command raised"))
                        update
                        (fun _ _ -> ())
                    |> Program.withSubscription (fun _ -> [
                        [ "raises" ], (fun _ -> failwith "the subscription's start raised")
                    ])
                    |> Program.withErrorReporter (fun _ ->
                        reports <- reports + 1
                        failwith "the reporter raised")
                    |> Program.withDispatcherHandle (fun d -> handle <- Some d)
                    |> Program.withDispatcherHandle (fun _ -> failwith "the sink raised")
                    |> Program.withEffect (
                        EffectHandle.programLifetime "raises" (fun _ -> failwith "the effect's start raised")
                    )
                    |> Program.runWithDispatch id ())

            Expect.equal reports 4 "the sink, the effect, the subscription and the command each reached the reporter"
            Expect.equal (occurrences ReporterRaised written) 4 "the guard said so, once per failure"

            for original in
                [
                    "the sink raised"
                    "the effect's start raised"
                    "the subscription's start raised"
                    "init's command raised"
                ] do
                Expect.stringContains written original "the failure being reported reached the console"

            capturingStderr (fun () -> handle.Value.Dispatch 7) |> ignore
            Expect.equal (List.ofSeq seen) [ 7 ] "the boot returned with the latch released, and the loop drains"
        }

        testSequenced
        <| test "a reporter that throws does not stop Terminate tearing down" {
            let disposed = ResizeArray<string>()
            let mutable handle: IDispatcher<int> option = None

            let raisingOnDispose (name: string) =
                { new System.IDisposable with
                    member _.Dispose() =
                        disposed.Add name
                        failwith (name + " raised on dispose")
                }

            let written =
                capturingStderr (fun () ->
                    Program.mkProgram (fun () -> [], Cmd.none) (fun msg model -> model @ [ msg ], Cmd.none) (fun _ _ ->
                        ())
                    |> Program.withSubscription (fun _ -> [ [ "sub" ], (fun _ -> raisingOnDispose "sub") ])
                    |> Program.withErrorReporter (fun _ -> failwith "the reporter raised")
                    |> Program.withDispatcherHandle (fun d -> handle <- Some d)
                    |> Program.withEffect (EffectHandle.programLifetime "effect" (fun _ -> raisingOnDispose "effect"))
                    |> Program.runWithDispatch id ()

                    handle.Value.Terminate())

            Expect.equal (List.ofSeq disposed) [ "sub"; "effect" ] "the subscription and the effect were both disposed"
            Expect.equal (occurrences ReporterRaised written) 2 "the guard said so, once per failure"
            Expect.isFalse handle.Value.IsActive "…and the program terminated"
        }

        // ─── Termination is total (Phase 871) ────────────────────────────
        //
        // Both routes to termination — a message the predicate takes, and
        // `IDispatcher.Terminate` — run ONE teardown: the loop's flag first,
        // then the subscriptions stopped, the effects disposed and the handler
        // called, each under its own guard, and `MarkTerminated` in a
        // `finally`. And the boot starts nothing once the program is
        // terminated: it checks before every effect's registration, before the
        // subscription start and before `init`'s command, and a start that
        // terminated the program has what it returned released at once.
        // `terminated_holds_nothing` and `terminated_starts_nothing` are those
        // rules on the model; these are the production arms, each shown red on
        // the tree before the phase.

        test "a terminating message whose handler raises still terminates - teardown is total, run" {
            let seen = ResizeArray<int>()
            let reported = ResizeArray<ErrorContext>()
            let disposed = ResizeArray<string>()
            let mutable handle: IDispatcher<int> option = None

            let disposable (name: string) =
                { new System.IDisposable with
                    member _.Dispose() = disposed.Add name
                }

            let update (msg: int) (model: int list) =
                seen.Add msg
                model @ [ msg ], Cmd.none

            Program.mkProgram (fun () -> [], Cmd.none) update (fun _ _ -> ())
            |> Program.withTermination ((=) 9) (fun _ -> failwith "the terminate handler raised")
            |> Program.withSubscription (fun _ -> [ [ "sub" ], (fun _ -> disposable "sub") ])
            |> Program.withErrorReporter reported.Add
            |> Program.withDispatcherHandle (fun d -> handle <- Some d)
            |> Program.withEffect (EffectHandle.programLifetime "effect" (fun _ -> disposable "effect"))
            |> Program.runWithDispatch id ()

            handle.Value.Dispatch 1
            handle.Value.Dispatch 9
            Expect.isFalse handle.Value.IsActive "the handler raised, and the program terminated anyway"
            handle.Value.Dispatch 2
            Expect.equal (List.ofSeq seen) [ 1 ] "a dispatch after the terminating message reached `update` zero times"

            Expect.equal
                (List.ofSeq disposed)
                [ "sub"; "effect" ]
                "the subscription and the effect were disposed, in the teardown's order"

            Expect.equal reported.Count 1 "the handler's exception was reported, once"
            handle.Value.Terminate()
            Expect.equal (List.ofSeq disposed) [ "sub"; "effect" ] "a second Terminate tore nothing down twice"
        }

        test "a sink that terminates at boot starts no effect, no subscription and no command - run" {
            let started = ResizeArray<string>()
            let disposed = ResizeArray<string>()
            let renders = ResizeArray<int list>()
            let mutable handle: IDispatcher<int> option = None

            let disposable (name: string) =
                { new System.IDisposable with
                    member _.Dispose() = disposed.Add name
                }

            Program.mkProgram
                (fun () -> [], Cmd.ofEffect (fun _ -> started.Add "init's command"))
                (fun msg model -> model @ [ msg ], Cmd.none)
                (fun _ _ -> ())
            |> Program.withSetState (fun m _ -> renders.Add m)
            |> Program.withSubscription (fun _ -> [
                [ "sub" ],
                (fun _ ->
                    started.Add "sub"
                    disposable "sub")
            ])
            |> Program.withDispatcherHandle (fun d ->
                handle <- Some d
                d.Terminate())
            |> Program.withEffect (
                EffectHandle.programLifetime "effect" (fun _ ->
                    started.Add "effect"
                    disposable "effect")
            )
            |> Program.runWithDispatch id ()

            Expect.isEmpty started "nothing was started once a sink had terminated the program"
            Expect.isEmpty disposed "…so nothing was left to dispose"
            Expect.equal (List.ofSeq renders) [ [] ] "the boot paint is unconditional: the init model, once"
            Expect.isFalse handle.Value.IsActive "terminated"
        }

        test "an effect that terminates from its own start function has its handle disposed - run" {
            let started = ResizeArray<string>()
            let disposed = ResizeArray<string>()
            let mutable handle: IDispatcher<int> option = None

            let disposable (name: string) =
                { new System.IDisposable with
                    member _.Dispose() = disposed.Add name
                }

            // Effects start in the reverse of the order they were attached,
            // so `terminates` (attached last) starts first and `after` would
            // start second.
            Program.mkProgram
                (fun () -> [], Cmd.ofEffect (fun _ -> started.Add "init's command"))
                (fun msg model -> model @ [ msg ], Cmd.none)
                (fun _ _ -> ())
            |> Program.withSubscription (fun _ -> [
                [ "sub" ],
                (fun _ ->
                    started.Add "sub"
                    disposable "sub")
            ])
            |> Program.withDispatcherHandle (fun d -> handle <- Some d)
            |> Program.withEffect (
                EffectHandle.programLifetime "after" (fun _ ->
                    started.Add "after"
                    disposable "after")
            )
            |> Program.withEffect (
                EffectHandle.programLifetime "terminates" (fun _ ->
                    started.Add "terminates"
                    handle.Value.Terminate()
                    disposable "terminates")
            )
            |> Program.runWithDispatch id ()

            Expect.equal
                (List.ofSeq started)
                [ "terminates" ]
                "the effect that terminated ran; no effect, subscription or command started after it"

            Expect.equal
                (List.ofSeq disposed)
                [ "terminates" ]
                "the handle it returned after the teardown had run was disposed at once, not registered"

            Expect.isFalse handle.Value.IsActive "terminated"
        }

        test "a subscription that terminates from its own start function is stopped - run" {
            let started = ResizeArray<string>()
            let disposed = ResizeArray<string>()
            let mutable handle: IDispatcher<int> option = None

            let disposable (name: string) =
                { new System.IDisposable with
                    member _.Dispose() = disposed.Add name
                }

            Program.mkProgram
                (fun () -> [], Cmd.ofEffect (fun _ -> started.Add "init's command"))
                (fun msg model -> model @ [ msg ], Cmd.none)
                (fun _ _ -> ())
            |> Program.withSubscription (fun _ -> [
                [ "terminates" ],
                (fun _ ->
                    started.Add "terminates"
                    handle.Value.Terminate()
                    disposable "terminates")
            ])
            |> Program.withDispatcherHandle (fun d -> handle <- Some d)
            |> Program.runWithDispatch id ()

            Expect.equal (List.ofSeq started) [ "terminates" ] "the subscription ran; init's command did not"

            Expect.equal
                (List.ofSeq disposed)
                [ "terminates" ]
                "the subscription the teardown could not yet see was stopped once its start returned"

            Expect.isFalse handle.Value.IsActive "terminated"
        }

        test "a handler that calls Terminate tears down once, on either route - teardown is not re-entered" {
            // Bounded, so the tree before the phase shows red rather than
            // overflowing the stack: there the handler's `Terminate` found
            // the flag still clear and ran the whole teardown again.
            for viaMessage in [ true; false ] do
                let mutable calls = 0
                let disposed = ResizeArray<string>()
                let mutable handle: IDispatcher<int> option = None

                Program.mkProgram (fun () -> [], Cmd.none) (fun msg model -> model @ [ msg ], Cmd.none) (fun _ _ -> ())
                |> Program.withTermination ((=) 9) (fun _ ->
                    calls <- calls + 1

                    if calls < 5 then
                        handle.Value.Terminate())
                |> Program.withDispatcherHandle (fun d -> handle <- Some d)
                |> Program.withEffect (
                    EffectHandle.programLifetime "effect" (fun _ ->
                        { new System.IDisposable with
                            member _.Dispose() = disposed.Add "effect"
                        })
                )
                |> Program.runWithDispatch id ()

                if viaMessage then
                    handle.Value.Dispatch 9
                else
                    handle.Value.Terminate()

                let route =
                    if viaMessage then
                        "the message route"
                    else
                        "the callback route"

                Expect.equal calls 1 $"the handler ran once ({route})"
                Expect.equal (List.ofSeq disposed) [ "effect" ] $"the effect was disposed once ({route})"
                Expect.isFalse handle.Value.IsActive $"terminated ({route})"
        }
    ]