// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 789 / 851 / 871 — the dispatch loop's differential, and since
/// Phase 884 its HOST-NEUTRAL half.
///
/// `proofs/ElmishLoop.fst` models `Program.runWithDispatch` as a step
/// machine over the ring, the `reentered` latch, the `terminated` latch,
/// the model and — since Phase 851 — the `dirty` flag, with every impure
/// callee abstracted as an oracle that says which messages it
/// re-dispatches and whether it calls `Terminate`. Two oracles since 851:
/// `update` (update + subscribe + Subs.change + Cmd.exec, per message) and
/// `render` (the render hook, once per drain when the ring is empty, with
/// the model the drain ended on). `proofs/check.ps1` extracts it to F# and
/// byte-compares the result against the committed
/// `proofs/oracle/ElmishLoop.fs`.
///
/// **A proof is about the MODEL, and the differential is the only thing
/// that says the model is about the code.** The loop ships to two hosts:
/// .NET, where the platform test pack runs it, and the browser, where the
/// Fable transpilation of the same `Program.fs` is what every client
/// actually executes. Until Phase 884 the comparison ran on .NET only, so
/// the loop users run was the one loop the proof did not reach. This
/// module holds everything the two hosts need and nothing either cannot
/// compile — the SCRIPT, its generator, the drivers of the real loop and
/// of the extracted machine, the hand-transcribed skeleton with its
/// go-red variants, and the comparisons as ROWS — and is compiled into
/// `ToolUp.Platform.Tests` (Expecto) and into `ToolUp.AI.Client.Tests`
/// (`node:test`), the `ElmishProofDifferential.fs` precedent. The
/// extraction itself compiles on both hosts (Phase 850's normaliser);
/// the Fable host compiles it over the machine-integer `Prims` in
/// `proofs/oracle/fable/`, which is why every integer crosses into the
/// model through `Prims.parse_int` and back through `int` — the one
/// spelling both shims accept.
///
/// ─── The script ─────────────────────────────────────────────────────
///
/// Every arm is differential: a generated SCRIPT — which messages a
/// dispatcher-handle sink and an effect's start function raise BEFORE the
/// boot paint, which messages `init`'s command raises, which each
/// message's command raises (including `Terminate`), which messages the
/// render hook raises when handed a given model (the post-drain
/// `setState` dispatch — the case Phase 851 creates), which messages
/// terminate, whether the init model subscribes and what the
/// subscription's start raises, whether the terminate handler RAISES, and
/// what the outside world dispatches afterwards — is run through the real
/// `Program.runWithDispatch` with a scripted `update` and a scripted
/// `setState`, and through the extracted machine with the same script as
/// its oracles. The two must agree on the messages `update` saw in order,
/// on the messages `dispatch` accepted, on the final model, on
/// `IsActive`, on how many times the hook was called and the model it was
/// last handed, on the BOOT paint (`boot_paints_init_model`), on how many
/// of the boot's gated starts ran and how many handles are still held
/// (`terminated_starts_nothing`, `terminated_holds_nothing`) — and, since
/// Phase 884, on all of that PER STEP: after the boot and after every
/// external event, not only at the end. The model has no parameter for
/// the handler raising: the comparison is what says production behaves
/// the same either way.
///
/// The `log` comparison is what holds the two-flag encoding: production's
/// log is recorded through `IDispatcher.IsActive`, the model's through
/// its `terminated` cell, so a state in which the two disagree logs
/// differently on the next dispatch.
///
/// ─── One corpus, two hosts — and how it grows ───────────────────────
///
/// The campaign is drawn from fixed seeds through the LCG both hosts
/// share, and its canonical rendering is pinned (`CampaignFingerprint`):
/// both hosts assert it, so "the Fable differential runs the corpus the
/// .NET differential runs" is a checked statement rather than a hope, and
/// every coverage floor below — measured once — holds on both. The model's
/// verdicts over the campaign are pinned the same way
/// (`VerdictFingerprint`), which holds the Fable host's machine-integer
/// shim to the .NET host's `BigInteger` one over the loop model, the claim
/// Phase 850 made for the ring.
///
/// The comparisons are ROWS (`corpusChecks`): a name, what a violation
/// means, and a function returning the violations (empty is a pass). Each
/// host turns every row into one test case, so a new shape of script is a
/// new field and its generator draw, and a new property is a new row —
/// never a new harness, and never a row one host runs and the other does
/// not. A change to the generator or to the model moves a pin; the
/// failing row prints the value to pin, and the rendering is below.
///
/// The go-red rows are hand-transcribed loop skeletons with one line
/// moved each — the latch released BEFORE the drain; the paint made after
/// every message as it was until 851; the paint made once after the
/// `while` with no pop after it, so a hook that dispatches leaves its
/// message in the ring; the latch set AFTER the sinks and effects ran, as
/// it was until the boot was restated; the boot as it was until Phase 871,
/// starting every effect, the subscriptions and `init`'s command after a
/// sink or an effect had called `Terminate`; a registry that stores the
/// handle of a start that terminated the program itself; and Phase 900's
/// three, the message arm as it was until 900 — the rest of a diff started
/// after one of its subscriptions terminated the program, the handle of
/// the start that terminated it held, and the message's command run after
/// a terminating diff — run over the same campaign and asserted CAUGHT;
/// the same skeleton, faithful, is asserted to agree, so the difference
/// each go-red measures is the one line.
///
/// ─── The message arm's diff (Phase 900) ─────────────────────────────
///
/// A message's model asks for subscriptions of its own (`Subs`): the
/// diff after the message is FIRST processed starts them, in order, one
/// start each. Keys only ever accumulate — a subscription a message asks
/// for is asked for by every later model — so no diff stops anything and
/// `Held` on both sides counts every start that came up and was not
/// released; the model's `held` never moves down except at teardown, and
/// that is the shape the model is exact for (its header says so). Every
/// message's command is a start too, counted whether or not it raises
/// anything, so a command run after its diff terminated the program is a
/// start too many on production's side.
module ToolUp.Platform.Tests.Client.ElmishLoopDifferential

open ToolUp.Elmish
open ToolUp.Platform.Tests.Client.ElmishProofDifferential

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
    /// Phase 871 — the init model's subscription: absent, or one whose
    /// start function raises these events. Its key never changes, so no
    /// later message's diff starts or stops it.
    Sub: Ev list option
    /// Phase 871 — the terminate handler raises. The model has no
    /// transition for this: teardown is the same either way.
    HandlerRaises: bool
    /// Phase 900 — the subscriptions a message's model asks for, keyed by
    /// the message: one entry per subscription, the events its start
    /// function raises. Started by the diff after the message is first
    /// processed, in this order; forward-only, so every drain finishes.
    /// Absent means the message asks for none.
    Subs: Map<int, Ev list list>
}

let replies (script: Script) (msg: int) : Ev list =
    Map.tryFind msg script.Replies |> Option.defaultValue []

/// Phase 900 — the subscriptions `msg`'s model asks for.
let subsFor (script: Script) (msg: int) : Ev list list =
    Map.tryFind msg script.Subs |> Option.defaultValue []

/// What the render hook raises when handed `model` — keyed by the model's
/// last message, so the SAME model always raises the same events (the
/// oracle is a function of the model, as the F* says).
let paints (script: Script) (model: int list) : Ev list =
    match List.tryLast model with
    | None -> script.BootPaint
    | Some last -> Map.tryFind last script.Paints |> Option.defaultValue []

/// Phase 884 — what the loop looks like from outside after the boot and
/// after each external event: the per-step half of the comparison.
type Step = {
    Trace: int list
    Log: int list
    Model: int list
    Active: bool
    Renders: int
    Painted: int list option
    Held: int
}

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
    /// Phase 871 — how many of the boot's gated starts ran: the effect's
    /// start function, the subscription's, `init`'s command.
    Started: int
    /// Phase 871 — how many handles those starts returned that nothing has
    /// disposed yet.
    Held: int
    /// Phase 884 — the state after the boot, then after each external
    /// event, in order: one entry more than the script has `Exts`.
    Steps: Step list
}

// ─── The generator ───────────────────────────────────────────────────

/// A script over ids `0 .. n-1`. Replies and paints point forward only.
/// `preRng` is a SECOND generator, drawn from only for `Pre`, so the
/// scripts the campaign held before `Pre` existed are the same scripts
/// with a `Pre` added — the shapes the coverage case counts did not move.
/// `startRng` is a THIRD, drawn from only for Phase 871's `Sub` and
/// `HandlerRaises`, for the same reason. `subsRng` is a FOURTH, drawn from
/// only for Phase 900's `Subs`. A later shape takes a FIFTH.
let genScript (rng: Lcg) (preRng: Lcg) (startRng: Lcg) (subsRng: Lcg) : Script =
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

    let sub =
        if startRng.Next 100 < 50 then
            Some [
                for _ in 1 .. 1 + startRng.Next 2 ->
                    if startRng.Next 100 < 10 then
                        ETerm
                    else
                        EMsg(startRng.Next n)
            ]
        else
            None

    let handlerRaises = startRng.Next 100 < 50

    // Phase 900 — about a third of the messages ask for one or two
    // subscriptions; each start raises up to two forward-pointing
    // messages, or Terminate. The last id can only terminate: there is
    // nothing after it to point at.
    let genSubEvs (m: int) : Ev list = [
        for _ in 1 .. subsRng.Next 3 do
            if subsRng.Next 100 < 12 then
                ETerm
            elif m + 1 < n then
                EMsg(m + 1 + subsRng.Next(n - m - 1))
    ]

    let subs =
        [
            for m in 0 .. n - 1 do
                if subsRng.Next 100 < 35 then
                    m, [ for _ in 1 .. 1 + subsRng.Next 2 -> genSubEvs m ]
        ]
        |> Map.ofList

    {
        Capacity = capacity
        Pre = pre
        Init = genEvs 0 3
        Replies = replies
        Paints = paintsMap
        BootPaint = bootPaint
        Terminating = terminating
        Exts = exts
        Sub = sub
        HandlerRaises = handlerRaises
        Subs = subs
    }

/// How many scripts the campaign draws.
[<Literal>]
let CampaignSize = 400

/// The campaign both hosts run: the same seeds, the same generator.
let campaign: Lazy<Script list> =
    lazy
        (let rng = Lcg 789_001
         let preRng = Lcg 789_002
         let startRng = Lcg 789_003
         let subsRng = Lcg 789_004
         [ for _ in 1..CampaignSize -> genScript rng preRng startRng subsRng ])

// ─── Production ──────────────────────────────────────────────────────

/// The real loop, driven by the script. `update` records what it is
/// handed and returns a command that raises the script's replies; the
/// render hook counts itself, records the model it was handed and raises
/// the script's paints; the dispatcher handle captured at start is how
/// `Terminate` is raised from inside and how the outside world dispatches
/// afterwards.
let productionRun (script: Script) : Outcome =
    let trace = ResizeArray<int>()
    let log = ResizeArray<int>()
    let steps = ResizeArray<Step>()
    let mutable model: int list = []
    let mutable renders = 0
    let mutable painted: int list option = None
    let mutable bootPainted: int list option = None
    let mutable bootTrace: int list = []
    let mutable dispatcher: IDispatcher<int> option = None
    let mutable started = 0
    let mutable held = 0

    // A start's handle: counted held when the start returns it, released
    // when anything disposes it.
    let holding () =
        held <- held + 1

        { new System.IDisposable with
            member _.Dispose() = held <- held - 1
        }

    let handle () =
        match dispatcher with
        | Some d -> d
        | None -> failwith "the dispatcher handle was not captured before anything dispatched"

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
        [],
        Cmd.ofEffect (fun dispatch ->
            started <- started + 1
            raise dispatch script.Init)

    let start (evs: Ev list) : Subscribe<int> =
        fun dispatch ->
            started <- started + 1
            raise dispatch evs
            holding ()

    // The init model's subscription, and every later model's: one key, so
    // the diff after each message keeps it running rather than restarting
    // it. Phase 900: plus the subscriptions each message in the model asks
    // for, keyed by the message — so the diff after a message is FIRST
    // processed starts exactly its subscriptions, in order, and a message
    // processed again finds its keys active (and duplicated in the request,
    // which the diff reports and dedups). Keys only ever accumulate.
    let subscribe (model: int list) : Sub<int> =
        let initial =
            match script.Sub with
            | None -> []
            | Some evs -> [ [ "scripted-sub" ], start evs ]

        let perMessage =
            model
            |> List.collect (fun m ->
                subsFor script m
                |> List.mapi (fun i evs -> [ "sub"; string m; string i ], start evs))

        initial @ perMessage

    // The model cell is what `update` produces — `state <- model'` in the
    // loop. Until 851 the bridge read it off `setState`, which ran after
    // every `update`; a hook that runs once per drain sees the model only
    // when the drain paints, and a drain that terminates paints nothing.
    // The command is a start (Phase 900): counted when it runs, so a
    // command run after its diff terminated the program is one start too
    // many.
    let update (msg: int) (model': int list) =
        trace.Add msg
        let next = model' @ [ msg ]
        model <- next

        next,
        Cmd.ofEffect (fun dispatch ->
            started <- started + 1
            raise dispatch (replies script msg))

    let setState (m: int list) (dispatch: int -> unit) =
        if renders = 0 then
            bootPainted <- Some m
            bootTrace <- List.ofSeq trace

        renders <- renders + 1
        painted <- Some m
        raise dispatch (paints script m)

    // A handler that raises is reported, and the reporter here swallows it:
    // the loop's behaviour is what is compared, not the report.
    let onTerminate (_: int list) =
        if script.HandlerRaises then
            failwith "the scripted terminate handler raised"

    let snapshot () =
        steps.Add {
            Step.Trace = List.ofSeq trace
            Log = List.ofSeq log
            Model = model
            Active = (handle ()).IsActive
            Renders = renders
            Painted = painted
            Held = held
        }

    Program.mkProgram init update (fun _ _ -> ())
    |> Program.withSetState setState
    |> Program.withSubscription subscribe
    |> Program.withTermination (fun msg -> script.Terminating.Contains msg) onTerminate
    |> Program.withErrorReporter ignore
    |> Program.withRingBufferCapacity script.Capacity
    |> Program.withDispatcherHandle (fun d -> dispatcher <- Some d)
    |> Program.withDispatcherHandle (fun d -> raise d.Dispatch preFromSink)
    |> Program.withEffect (
        EffectHandle.programLifetime "scripted-pre-boot" (fun dispatch ->
            started <- started + 1
            raise dispatch preFromEffect
            holding ())
    )
    |> Program.runWithDispatch id ()

    snapshot ()

    for ext in script.Exts do
        match ext with
        | XDispatch m ->
            if (handle ()).IsActive then
                log.Add m

            (handle ()).Dispatch m
        | XTerminate -> (handle ()).Terminate()

        snapshot ()

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
        Started = started
        Held = held
        Steps = List.ofSeq steps
    }

// ─── The extracted machine ───────────────────────────────────────────

/// Into the model's integers. `Prims.int` is `BigInteger` on the .NET
/// host and `int` on the Fable one; decimal text is what both shims'
/// `parse_int` read, which is how the extraction spells its own literals.
let toModelInt (n: int) : Prims.int = Prims.parse_int (string n)

let private toModelEv (ev: Ev) : ElmishLoop.ev<int> =
    match ev with
    | EMsg m -> ElmishLoop.Msg m
    | ETerm -> ElmishLoop.Term

let private toModelExt (ext: Ext) : ElmishLoop.ext<int> =
    match ext with
    | XDispatch m -> ElmishLoop.XDispatch m
    | XTerminate -> ElmishLoop.XTerminate

/// A start the script makes: the events it raises, and a handle returned.
let private toModelStart (evs: Ev list) : ElmishLoop.start<int> = {
    ElmishLoop.raised = List.map toModelEv evs
    ElmishLoop.holds = true
}

/// Enough for any script the generator produces; `Finished` says
/// whether it was.
let private fuel = toModelInt 100_000

let private paintedOf (st: ElmishLoop.st<int, int list>) =
    match st.painted with
    | ElmishRing.OSome m -> Some m
    | ElmishRing.ONone -> None

let private stepOf (st: ElmishLoop.st<int, int list>) : Step = {
    Step.Trace = st.trace
    Log = st.log
    Model = st.model
    Active = st.active
    Renders = int st.renders
    Painted = paintedOf st
    Held = int st.held
}

let modelRun (script: Script) : Outcome =
    // Phase 900 — the oracle's reply is split at the diff: the starts the
    // diff makes (the message's subscriptions, on its first processing;
    // none on a repeat, whose keys are already active) and the command.
    let update (msg: int) (model: int list) : ElmishLoop.reply<int, int list> = {
        ElmishLoop.next = model @ [ msg ]
        ElmishLoop.starts =
            if List.contains msg model then
                []
            else
                subsFor script msg |> List.map toModelStart
        ElmishLoop.cmd = ElmishRing.OSome(replies script msg |> List.map toModelEv)
    }

    let render (model: int list) =
        paints script model |> List.map toModelEv

    let toTerminate (msg: int) = script.Terminating.Contains msg

    // The head of `Pre` is the sink's; the rest the effect's start
    // function's, and the effect always returns a handle.
    let sinks, fx =
        match script.Pre with
        | [] -> [], [ toModelStart [] ]
        | first :: rest -> [ toModelEv first ], [ toModelStart rest ]

    let subs =
        match script.Sub with
        | None -> ElmishRing.ONone
        | Some evs -> ElmishRing.OSome(toModelStart evs)

    let cmd = ElmishRing.OSome(List.map toModelEv script.Init)
    let exts = List.map toModelExt script.Exts

    let s =
        ElmishLoop.program fuel update toTerminate render (toModelInt script.Capacity) [] sinks fx subs cmd exts

    // The model's own account of the boot paint: the state `boot` passes
    // through on its way to the drain, which `boot_paints_init_model` is
    // stated over.
    let atBootPaint =
        ElmishLoop.boot_paint
            fuel
            update
            toTerminate
            render
            (ElmishLoop.initial (toModelInt script.Capacity) [])
            sinks
            fx

    // Phase 884 — the same run a step at a time: `program` is `run` over
    // the booted state, and `run` over a list is `run` over each element
    // in turn, so these are the states `program` passes through.
    let booted =
        ElmishLoop.boot
            fuel
            update
            toTerminate
            render
            (ElmishLoop.initial (toModelInt script.Capacity) [])
            sinks
            fx
            subs
            cmd

    let steps =
        exts
        |> List.scan (fun st ext -> ElmishLoop.run fuel update toTerminate render st [ ext ]) booted
        |> List.map stepOf

    {
        Trace = s.trace
        Log = s.log
        Model = s.model
        Active = s.active
        Renders = int s.renders
        Painted = paintedOf s
        BootPainted = paintedOf atBootPaint
        BootTrace = atBootPaint.trace
        Finished = not s.reentered
        Started = int s.started
        Held = int s.held
        Steps = steps
    }

// ─── The skeleton, and its go-reds ───────────────────────────────────

type Variant =
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
    /// The boot as it was until Phase 871: every start is made whether or
    /// not a sink or an earlier start has called `Terminate`.
    | StartsAfterTerminate
    /// The registry as it was until Phase 871: a start that terminated the
    /// program itself has its handle stored after the teardown emptied the
    /// registry, and held for good.
    | KeepsATerminatingStartsHandle
    /// `Subs.Fx.change` as it was until Phase 900: every subscription of a
    /// message's diff is started, whether or not an earlier one in the same
    /// diff called `Terminate`.
    | StartsTheRestOfADiffAfterTerminate
    /// The message arm as it was until Phase 900: the handle of a start that
    /// terminated the program from inside a message's diff is assigned to
    /// the active set the teardown had already stopped, and held for good.
    | KeepsATerminatingDiffsHandle
    /// The message arm as it was until Phase 900: the message's command runs
    /// after its diff terminated the program.
    | RunsTheCommandAfterATerminatingDiff

/// `runWithDispatch`'s scheduling skeleton, transcribed by hand with the
/// callees replaced by the script — the same abstraction the model
/// makes, in F#, over the production ring. Faithful, it must agree with
/// the model; with the one line moved, it must be caught.
let skeletonRun (variant: Variant) (script: Script) : Outcome =
    let trace = ResizeArray<int>()
    let log = ResizeArray<int>()
    let steps = ResizeArray<Step>()
    let rb = RingBuffer<int> script.Capacity
    let mutable reentered = false
    let mutable terminated = false
    let mutable dirty = true
    let mutable state: int list = []
    let mutable renders = 0
    let mutable painted: int list option = None
    let mutable bootPainted: int list option = None
    let mutable bootTrace: int list = []
    let mutable started = 0
    let mutable held = 0

    let terminate () =
        if not terminated then
            terminated <- true
            held <- 0

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
                | LatchesAfterEffects
                | StartsAfterTerminate
                | KeepsATerminatingStartsHandle
                | StartsTheRestOfADiffAfterTerminate
                | KeepsATerminatingDiffsHandle
                | RunsTheCommandAfterATerminatingDiff ->
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

    // Phase 900 — one start of a message's diff: made only while the
    // program runs, holding its handle only if it did not terminate the
    // program itself. The two go-reds are the message arm before 900.
    and startInDiff (evs: Ev list) =
        if not terminated || variant = StartsTheRestOfADiffAfterTerminate then
            started <- started + 1
            raise evs

            if not terminated || variant = KeepsATerminatingDiffsHandle then
                held <- held + 1

    and stepMsg (msg: int) =
        if script.Terminating.Contains msg then
            terminate ()
        else
            dirty <- true
            trace.Add msg
            let model' = state @ [ msg ]

            // The diff: the message's subscriptions on its first processing,
            // one gated start each (Phase 900).
            if not (List.contains msg state) then
                for evs in subsFor script msg do
                    startInDiff evs

            // The command, only while the program is still running after
            // the diff (Phase 900); a start that holds nothing.
            if not terminated || variant = RunsTheCommandAfterATerminatingDiff then
                started <- started + 1
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
        | LatchesAfterEffects
        | StartsAfterTerminate
        | KeepsATerminatingStartsHandle
        | StartsTheRestOfADiffAfterTerminate
        | KeepsATerminatingDiffsHandle
        | RunsTheCommandAfterATerminatingDiff ->
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

    // Phase 871 — a gated start: made only while the program runs, and
    // holding its handle only if it did not terminate the program itself.
    let gated (evs: Ev list) (holds: bool) =
        if not terminated || variant = StartsAfterTerminate then
            started <- started + 1
            raise evs

            if holds && (not terminated || variant = KeepsATerminatingStartsHandle) then
                held <- held + 1

    let sinks, effect =
        match script.Pre with
        | [] -> [], []
        | first :: rest -> [ first ], rest

    let preboot () =
        raise sinks
        gated effect true

    let snapshot () =
        steps.Add {
            Step.Trace = List.ofSeq trace
            Log = List.ofSeq log
            Model = state
            Active = not terminated
            Renders = renders
            Painted = painted
            Held = held
        }

    match variant with
    | LatchesAfterEffects ->
        preboot ()
        reentered <- true
    | Faithful
    | ClearsLatchBeforeDrain
    | PaintsPerMessage
    | PaintsWithoutRedrain
    | StartsAfterTerminate
    | KeepsATerminatingStartsHandle
    | StartsTheRestOfADiffAfterTerminate
    | KeepsATerminatingDiffsHandle
    | RunsTheCommandAfterATerminatingDiff ->
        reentered <- true
        preboot ()

    paint ()

    match script.Sub with
    | Some evs -> gated evs true
    | None -> ()

    gated script.Init false
    processMsgs ()
    reentered <- false
    snapshot ()

    for ext in script.Exts do
        match ext with
        | XDispatch m ->
            if not terminated then
                log.Add m

            dispatch m
        | XTerminate -> terminate ()

        snapshot ()

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
        Started = started
        Held = held
        Steps = List.ofSeq steps
    }

// ─── The pins: one corpus, one verdict set, on both hosts ────────────

/// A canonical rendering, spelled out rather than `%A`: the two hosts'
/// `%A` differ, and the pin must be the same text on both.
let private renderInts (xs: int list) =
    "[" + String.concat "," (List.map string xs) + "]"

let private renderEv (ev: Ev) =
    match ev with
    | EMsg m -> string m
    | ETerm -> "T"

let private renderEvs (evs: Ev list) =
    "[" + String.concat "," (List.map renderEv evs) + "]"

let private renderMap (m: Map<int, Ev list>) =
    "{"
    + String.concat "," (Map.toList m |> List.map (fun (k, evs) -> string k + ":" + renderEvs evs))
    + "}"

let private renderSubs (m: Map<int, Ev list list>) =
    "{"
    + String.concat
        ","
        (Map.toList m
         |> List.map (fun (k, subs) -> string k + ":" + String.concat "+" (List.map renderEvs subs)))
    + "}"

let private renderBool (b: bool) = if b then "1" else "0"

let private renderOpt (render: 'a -> string) (o: 'a option) =
    match o with
    | Some x -> render x
    | None -> "-"

let renderScript (s: Script) : string =
    String.concat ";" [
        string s.Capacity
        renderEvs s.Pre
        renderEvs s.Init
        renderMap s.Replies
        renderMap s.Paints
        renderEvs s.BootPaint
        renderInts (Set.toList s.Terminating)
        "["
        + String.concat
            ","
            (s.Exts
             |> List.map (fun e ->
                 match e with
                 | XDispatch m -> string m
                 | XTerminate -> "T"))
        + "]"
        renderOpt renderEvs s.Sub
        renderBool s.HandlerRaises
        renderSubs s.Subs
    ]

let private renderStep (st: Step) : string =
    String.concat ";" [
        renderInts st.Trace
        renderInts st.Log
        renderInts st.Model
        renderBool st.Active
        string st.Renders
        renderOpt renderInts st.Painted
        string st.Held
    ]

let renderOutcome (o: Outcome) : string =
    String.concat ";" [
        renderInts o.Trace
        renderInts o.Log
        renderInts o.Model
        renderBool o.Active
        string o.Renders
        renderOpt renderInts o.Painted
        renderOpt renderInts o.BootPainted
        renderInts o.BootTrace
        renderBool o.Finished
        string o.Started
        string o.Held
        "<" + String.concat "|" (List.map renderStep o.Steps) + ">"
    ]

/// The campaign's canonical rendering, pinned (`fingerprint` is the
/// shared one in `ElmishProofDifferential`, since Phase 900 also the
/// ring's and the diff's). Both hosts assert it, so both run the same
/// 400 scripts. Moved by Phase 900: the scripts gained `Subs`.
[<Literal>]
let CampaignFingerprint = "400:31565:1359516053"

/// The extracted machine's verdicts over the campaign, pinned. The .NET
/// host computes them over `BigInteger`, the Fable host over `int`.
/// Moved by Phase 900: the step is split at the diff and every message's
/// command counts as a start.
[<Literal>]
let VerdictFingerprint = "400:108737:2021978336"

// ─── The rows ────────────────────────────────────────────────────────

/// Each host's model verdicts and production runs over the campaign,
/// computed once and shared by every row that reads them.
let private modelOutcomes: Lazy<(Script * Outcome) list> =
    lazy (campaign.Force() |> List.map (fun s -> s, modelRun s))

let private productionOutcomes: Lazy<(Script * Outcome) list> =
    lazy (campaign.Force() |> List.map (fun s -> s, productionRun s))

let describe (script: Script) (actual: Outcome) (expected: Outcome) : string option =
    if actual = expected then
        None
    else
        Some(sprintf "script %A\n  actual   %A\n  expected %A" script actual expected)

/// Every script on which `run` disagrees with the extracted machine.
let mismatches (run: Script -> Outcome) : string list =
    modelOutcomes.Force()
    |> List.choose (fun (script, expected) -> describe script (run script) expected)

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
let private paintDispatched (script: Script) (o: Outcome) =
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

/// `[]` when `count` clears `floor`, else the message naming the shortfall.
let private above (count: int) (floor: int) (what: string) : string list =
    if count > floor then
        []
    else
        [ $"only {count} {what} (floor {floor})" ]

let private countScripts (p: Script -> bool) =
    campaign.Force() |> List.filter p |> List.length

let private countRuns (p: Script -> Outcome -> bool) =
    productionOutcomes.Force() |> List.filter (fun (s, o) -> p s o) |> List.length

let private reachedPredicate (s: Script) (o: Outcome) =
    not o.Active && Set.intersect s.Terminating (Set.ofList o.Log) <> Set.empty

/// One comparison, as data: a host turns it into one test case.
type Check = {
    Name: string
    /// What one violation means, for the failure message.
    Explain: string
    /// The violations; empty is a pass.
    Violations: unit -> string list
}

/// A go-red row: the variant must be caught on more than `floor` scripts.
let private goRed (name: string) (variant: Variant) (floor: int) (explain: string) : Check = {
    Name = name
    Explain = explain
    Violations = fun () -> above (List.length (mismatches (skeletonRun variant))) floor "script(s) caught it"
}

/// The failure message a host prints for a row's violations.
let report (check: Check) (violations: string list) : string =
    $"{List.length violations} {check.Explain}:\n"
    + String.concat "\n" (List.truncate 5 violations)

/// Every comparison over the campaign, in the order the .NET pack has
/// always run them. Both hosts run every row.
let corpusChecks: Check list = [
    {
        Name = "the campaign is the corpus both hosts run - its fingerprint is pinned"
        Explain = "campaign drift(s): pin the printed value in CampaignFingerprint if the generator moved on purpose"
        Violations =
            fun () ->
                let scripts = campaign.Force()
                let actual = fingerprint (List.map renderScript scripts)

                [
                    if List.length scripts <> CampaignSize then
                        $"the campaign drew {List.length scripts} script(s), not {CampaignSize}"
                    if actual <> CampaignFingerprint then
                        $"the campaign renders to {actual}, pinned {CampaignFingerprint}"
                ]
    }
    {
        Name = "the extracted machine's verdicts are the same on both hosts - its fingerprint is pinned"
        Explain = "verdict drift(s): the model's integer shim, or the model, moved"
        Violations =
            fun () ->
                let actual = fingerprint (modelOutcomes.Force() |> List.map (snd >> renderOutcome))

                if actual = VerdictFingerprint then
                    []
                else
                    [ $"the model's verdicts render to {actual}, pinned {VerdictFingerprint}" ]
    }
    {
        Name = "the extracted machine agrees with the production loop over generated reentrancy scripts"
        Explain = "script(s) on which the production loop and the proved model processed differently"
        Violations =
            fun () ->
                productionOutcomes.Force()
                |> List.zip (modelOutcomes.Force())
                |> List.choose (fun ((script, expected), (_, actual)) -> describe script actual expected)
    }
    {
        Name = "every drain in the campaign finished - the fuel bound never decided an agreement"
        Explain = "stalled drain(s): the model ran out of fuel, so the generator's forward-only guarantee is broken"
        Violations =
            fun () ->
                modelOutcomes.Force()
                |> List.filter (fun (_, o) -> not o.Finished)
                |> List.map (fun (s, _) -> sprintf "script %A" s)
    }
    {
        Name = "the campaign re-dispatched from update, from the boot drain, from the paint, and after Terminate"
        Explain = "coverage shortfall(s): the comparison says nothing about a clause the campaign never reached"
        Violations =
            fun () -> [
                yield! above (countScripts nestedWithSibling) 40 "scripts nest a re-dispatch under a sibling"
                yield!
                    above
                        (countScripts (fun s -> List.length (List.filter ((<>) ETerm) s.Init) >= 2))
                        40
                        "scripts raise two or more messages from the boot drain"
                yield!
                    above
                        (countScripts (fun s -> s.Replies |> Map.exists (fun _ evs -> termThenMsg evs)))
                        20
                        "scripts dispatch after a Terminate raised from inside"
                yield!
                    above
                        (countScripts (fun s ->
                            match List.tryFindIndex ((=) XTerminate) s.Exts with
                            | Some i -> s.Exts |> List.skip (i + 1) |> List.exists ((<>) XTerminate)
                            | None -> false))
                        40
                        "scripts dispatch from outside after a Terminate"
                yield! above (countRuns reachedPredicate) 40 "scripts reached the termination predicate"
                // 80, not 100, since Phase 871: about thirty scripts then ended
                // terminated by a subscription's start at boot (measured 99 of
                // 400 still active); 60 since Phase 900, whose message-diff
                // subscriptions terminate fifteen more (measured 84).
                yield! above (countRuns (fun _ o -> o.Active)) 60 "scripts ended still active"
                // Phase 851 — the case the render move creates: a paint that
                // dispatches, so the drain re-opens after the hook ran; and the
                // quiet scripts, on which `render_once_per_drain` is measured.
                // 50 since Phase 900 (measured 68, from over 80): a script a
                // message's subscription terminates paints nothing after it.
                yield! above (countRuns paintDispatched) 50 "scripts had the render hook dispatch into the loop"
                yield! above (countScripts quiet) 60 "scripts kept the render hook quiet"
                yield!
                    above
                        (countScripts (fun s ->
                            s.Paints |> Map.exists (fun _ evs -> List.contains ETerm evs)
                            || List.contains ETerm s.BootPaint))
                        15
                        "scripts raise Terminate from the render hook"
            ]
    }
    {
        Name = "the hand-transcribed skeleton agrees with the model"
        Explain = "script(s) on which the faithful skeleton disagreed"
        // So the go-reds below measure one line each and not the transcription.
        Violations = fun () -> mismatches (skeletonRun Faithful)
    }
    // The falsifying probe. Without it, every agreement above is consistent
    // with a comparison that agrees with everything.
    goRed
        "a loop that releases the latch before draining is caught - go-red"
        ClearsLatchBeforeDrain
        0
        "shortfall: a loop whose reentrant dispatches recurse into nested drains was never caught by the comparison"
    // The pre-851 loop. On a script with a drain longer than one message its
    // render count disagrees; on one whose hook dispatches, its trace does
    // too (the hook's message arrives mid-drain rather than after it).
    goRed
        "a loop that paints after every message is caught - go-red, Phase 851"
        PaintsPerMessage
        100
        "shortfall: a loop that renders once per message was not caught by the render-count comparison"
    // The near miss: paint once after the while, and stop. A hook that
    // dispatches leaves its message in the ring, so the trace is short until
    // an external dispatch happens to drain it.
    goRed
        "a loop that paints once but never re-pops is caught - go-red, Phase 851"
        PaintsWithoutRedrain
        20
        "shortfall: a loop that does not re-drain after the paint was not caught by the trace comparison"
    {
        Name = "a single-message boot drain and a steady-state dispatch process the same trace - boot_drain_equiv, run"
        Explain = "script(s) on which boot and dispatch differed"
        Violations =
            fun () ->
                campaign.Force()
                |> List.choose (fun script ->
                    // The first non-Terminate message the script would raise at
                    // boot, or its first external dispatch — one message, raised
                    // from `init`'s command versus dispatched from outside, with
                    // no other external events. Both boot paint once and
                    // drain-paint once, so `Renders` agrees too
                    // (`boot_single_is_dispatch`: the boot paint, then
                    // `dispatch`). The theorem's premise is a boot paint that
                    // leaves the machine idle — `booted` is the paint with
                    // nothing drained after it — so the boot paint is kept quiet
                    // here; a boot paint that dispatches is the campaign
                    // comparison's business, not this one's. The per-step
                    // record is dropped: the two runs take a different number
                    // of external steps by construction, and the theorem is
                    // about where they END.
                    let candidate =
                        script.Init
                        |> List.tryPick (fun e ->
                            match e with
                            | EMsg m -> Some m
                            | ETerm -> None)

                    match candidate with
                    | None -> None
                    | Some m ->
                        // …and nothing raised before the boot paint and no
                        // subscription, the theorem's other premises: an
                        // effect's or a subscription's message would be one more
                        // message in the boot drain, ahead of the one the
                        // dispatch would process.
                        let viaBoot =
                            productionRun {
                                script with
                                    Pre = []
                                    Init = [ EMsg m ]
                                    BootPaint = []
                                    Exts = []
                                    Sub = None
                            }

                        let viaDispatch =
                            productionRun {
                                script with
                                    Pre = []
                                    Init = []
                                    BootPaint = []
                                    Exts = [ XDispatch m ]
                                    Sub = None
                            }

                        describe script { viaBoot with Steps = [] } { viaDispatch with Steps = [] })
    }
    {
        Name = "after Terminate nothing is processed or painted and IsActive is false - terminated_absorbing, run"
        Explain = "script(s) that processed, painted or stayed active after Terminate"
        Violations =
            fun () ->
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
    }
    {
        Name = "the model on screen is the model whenever the loop is idle - painted_is_model, run"
        Explain = "script(s) on which the hook was left behind by a drain"
        // After production returns from the boot and from every external
        // dispatch, the model the hook was last handed is the model `update`
        // last produced — whatever the hook itself dispatched. Since Phase 884
        // at every step, not only the last.
        Violations =
            fun () ->
                productionOutcomes.Force()
                |> List.choose (fun (script, o) ->
                    match o.Steps |> List.tryFind (fun st -> st.Active && st.Painted <> Some st.Model) with
                    | Some st -> Some(sprintf "script %A\n  painted %A\n  model   %A" script st.Painted st.Model)
                    | None -> None)
    }
    {
        Name = "the campaign dispatched from a sink and from an effect's start function before the boot paint"
        Explain =
            "coverage shortfall(s): an agreement over scripts that raise nothing before the boot paint says nothing about it"
        Violations =
            fun () ->
                let isMsg (e: Ev) = e <> ETerm

                [
                    yield!
                        above
                            (countScripts (fun s -> s.Pre |> List.truncate 1 |> List.exists isMsg))
                            100
                            "scripts dispatch from a dispatcher-handle sink"
                    yield!
                        above
                            (countScripts (fun s -> s.Pre |> List.skip (min 1 (List.length s.Pre)) |> List.exists isMsg))
                            60
                            "scripts dispatch from an effect's start function"
                    yield!
                        above
                            (countScripts (fun s -> List.contains ETerm s.Pre))
                            10
                            "scripts raise Terminate before the boot paint"
                    yield!
                        above
                            (countScripts (fun s -> List.isEmpty s.Pre))
                            150
                            "scripts raise nothing before the boot paint"
                ]
    }
    // The boot as it shipped until it was restated. An effect that dispatches
    // from its start function finds the latch clear and runs a drain of its
    // own, so `update` has been handed a message — and the hook a model the
    // server never rendered — before the boot paint.
    goRed
        "a loop that sets the latch after the effects ran is caught - go-red"
        LatchesAfterEffects
        80
        "shortfall: a loop whose sinks and effects run before the latch is set was not caught by the boot-paint comparison"
    {
        Name =
            "the campaign terminated from a sink, an effect's start and a subscription's start, and with a raising handler"
        Explain = "coverage shortfall(s): an agreement over scripts that never terminate at boot says nothing about it"
        // Measured 9, 13, 29, 208 and 52; the sink and effect shapes are
        // `Pre`'s, whose generator predates Phase 871 and is not moved.
        Violations =
            fun () -> [
                yield!
                    above
                        (countScripts (fun s -> List.tryHead s.Pre = Some ETerm))
                        5
                        "scripts terminate from a dispatcher-handle sink"
                yield!
                    above
                        (countScripts (fun s -> s.Pre |> List.skip (min 1 (List.length s.Pre)) |> List.contains ETerm))
                        8
                        "scripts terminate from an effect's start function"
                yield!
                    above
                        (countScripts (fun s -> s.Sub |> Option.exists (List.contains ETerm)))
                        20
                        "scripts terminate from a subscription's start"
                yield! above (countScripts (fun s -> Option.isSome s.Sub)) 150 "scripts subscribe at boot"
                yield!
                    above
                        (countRuns (fun s o -> s.HandlerRaises && reachedPredicate s o))
                        30
                        "scripts reach the termination predicate with a handler that raises"
            ]
    }
    {
        Name = "a terminated program holds nothing - terminated_holds_nothing, run"
        Explain = "script(s) that ended terminated still holding a handle"
        // Whenever the program is terminated — at any step — every handle the
        // boot's starts returned has been disposed.
        Violations =
            fun () ->
                productionOutcomes.Force()
                |> List.choose (fun (script, o) ->
                    match o.Steps |> List.tryFind (fun st -> not st.Active && st.Held <> 0) with
                    | Some st -> Some(sprintf "script %A\n  terminated, still holding %d" script st.Held)
                    | None -> None)
    }
    {
        Name = "a terminate handler that raises changes nothing the loop does - teardown is total, run"
        Explain = "script(s) on which a raising handler changed the run"
        // The model has no parameter for the handler raising, so this is the
        // claim that it needs none: production with a handler that raises and
        // with one that returns are the same run.
        Violations =
            fun () ->
                campaign.Force()
                |> List.choose (fun script ->
                    describe
                        script
                        (productionRun { script with HandlerRaises = true })
                        (productionRun { script with HandlerRaises = false }))
    }
    // The boot as it was: the effect, the subscription and `init`'s command
    // all start after a sink or an earlier start called `Terminate`. Caught on
    // the start count.
    goRed
        "a boot that starts after Terminate is caught - go-red, Phase 871"
        StartsAfterTerminate
        20
        "shortfall: a boot that starts things after Terminate was not caught by the start-count comparison"
    // The registry as it was: an effect (or a subscription) that terminated
    // the program from its own start function has its handle stored after the
    // teardown ran, and nothing disposes it.
    goRed
        "a registry that keeps a terminating start's handle is caught - go-red, Phase 871"
        KeepsATerminatingStartsHandle
        20
        "shortfall: a start whose handle outlives the teardown was not caught by the held-handle comparison"
    {
        Name = "the campaign started subscriptions from a message's diff, and terminated from one with another after it"
        Explain =
            "coverage shortfall(s): an agreement over scripts whose diffs never terminate mid-way says nothing about it"
        // Phase 900. Measured 348, 250, 111 and 25; the floors are below.
        Violations =
            fun () ->
                let diffTerminatesMidWay (s: Script) =
                    s.Subs
                    |> Map.exists (fun _ subs ->
                        let terminating = subs |> List.tryFindIndex (List.contains ETerm)

                        match terminating with
                        | Some i -> i < List.length subs - 1
                        | None -> false)

                [
                    yield!
                        above
                            (countScripts (fun s -> not (Map.isEmpty s.Subs)))
                            150
                            "scripts ask for subscriptions from a message"
                    yield!
                        above
                            (countScripts (fun s -> s.Subs |> Map.exists (fun _ subs -> List.length subs >= 2)))
                            80
                            "scripts ask for two subscriptions from one message"
                    yield!
                        above
                            (countScripts (fun s ->
                                s.Subs |> Map.exists (fun _ subs -> subs |> List.exists (List.contains ETerm))))
                            50
                            "scripts terminate from a subscription a message's diff starts"
                    yield!
                        above
                            (countRuns (fun s o ->
                                diffTerminatesMidWay s
                                && s.Subs |> Map.exists (fun m _ -> List.contains m o.Trace)))
                            15
                            "scripts reach a diff that terminates with a subscription after the one that terminated it"
                ]
    }
    // `Subs.Fx.change` as it was: the rest of the diff started after one of
    // its subscriptions called `Terminate`. Caught on the start count.
    goRed
        "a diff that starts the rest after one of its subscriptions terminated is caught - go-red, Phase 900"
        StartsTheRestOfADiffAfterTerminate
        10
        "shortfall: a diff that goes on starting after Terminate was not caught by the start-count comparison"
    // The message arm as it was: the terminating start's handle assigned to
    // the stopped set, and never disposed. Caught on the held count.
    goRed
        "a message arm that keeps a terminating diff's handle is caught - go-red, Phase 900"
        KeepsATerminatingDiffsHandle
        10
        "shortfall: a diff's handle that outlives the teardown was not caught by the held-handle comparison"
    // The message arm as it was: the command run after its diff terminated
    // the program. Caught on the start count.
    goRed
        "a message arm that runs the command after a terminating diff is caught - go-red, Phase 900"
        RunsTheCommandAfterATerminatingDiff
        10
        "shortfall: a command run after its diff terminated the program was not caught by the start-count comparison"
]