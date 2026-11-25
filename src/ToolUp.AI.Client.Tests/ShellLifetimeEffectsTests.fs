module ToolUp.AI.Client.Tests.ShellLifetimeEffectsTests

open System
open Fuaran.MVU
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.AI.Client.Tests.StepDriver
open ToolUp.Platform
open ToolUp.AI.Client

// ─── The shell's subscriptions — the outer-composer contract ─────────
//
// The shell's long-lived sources (the navigation-request bus, the
// cross-module event bus, the SSE notification stream, the auth-token
// watcher, the auth-bridge health observer, the cross-tab identity watch,
// the shell dispatcher) are `Subscription` values the shell's model lists
// as resources — NOT started inside `Client.init`. A composer that builds
// its own program from the shell's `init` / `update` / views (ToolUp.AI's
// `AIClientConfig`) lists them too, lifted into its own message type.
//
// Dropping them is silent: the observed defect was an admin-landing tile
// click doing nothing in an AI-composed app — `AdminHome.update`'s
// `OpenModule` called `NavigationRequest.request`, and the bus is a
// documented no-op with zero subscribers, so no error surfaced anywhere.
// These tests pin the seam from both sides: the shell exposes the full
// set, and the AI composer lists every one of them, dispatch lifted
// through its own message (the `ShellMsg` shape it routes to
// `Client.update`).
//
// The second list pins what the runtime does with a shell-hosting
// program (`Client.programOver`): one render per drain, every resource
// stopped once whether it is delisted or the runtime terminates, and the
// errors of `update`, a performer, a subscription and a view reaching the
// `OnElmishError` sink with the context the shell has always reported.

/// The shell subscriptions, in their listing order. A subscription added
/// without extending this list fails the pin below — which is the point: it
/// must also reach every composer, and this test is where that obligation
/// surfaces.
let private expectedSites = [
    "navigation-request"
    "locale-request"
    "module-events"
    "session-epoch"
    "notifications-stream"
    "auth-token-acquired"
    "auth-bridge-health"
    "identity-across-tabs"
    "shell-dispatcher"
]

/// Stand-in for a composer's outer message type (`OuterMsg.ShellMsg`).
type private Outer = Wrap of Client.Msg

/// The test component's message: delist a resource, ask for follow-ups,
/// throw in `update`, or ask for an effect whose performer throws.
type private Probe =
    | Drop of string
    | Chain of int
    | Tick
    | Throw
    | BadEffect

/// A counting subscription: how often it was started and stopped.
type private Counts = {
    mutable Starts: int
    mutable Stops: int
}

let private counting (counts: Map<string, Counts>) (site: string) : Subscription<Probe> =
    Subscription(
        site,
        fun _ ->
            counts[site].Starts <- counts[site].Starts + 1

            { new IDisposable with
                member _.Dispose() =
                    counts[site].Stops <- counts[site].Stops + 1
            }
    )

/// A shell-hosting program over a model that is the set of resources it
/// lists, with every error the runtime reports going to `reported`.
let private probeProgram (counts: Map<string, Counts>) (reported: ResizeArray<ErrorContext>) =
    let config = {
        ClientConfig.defaults with
            Composition.OnElmishError = Some reported.Add
    }

    let update (msg: Probe) (model: Set<string>) : Set<string> * Step<Probe, RemoteEffect<Probe>> list =
        match msg with
        | Drop site -> Set.remove site model, []
        | Chain n -> model, List.replicate n (Then Tick)
        | Tick -> model, []
        | Throw -> failwith "update threw on purpose"
        | BadEffect ->
            model,
            [
                Do(RemoteEffect.signal "Probe.bad" (fun () -> failwith "performer threw on purpose"))
            ]

    Client.programOver
        config
        (fun () -> Set.ofSeq counts.Keys, [])
        update
        (fun model ->
            model
            |> Seq.map (fun site -> ResourceKey.constant site, counting counts site)
            |> Map.ofSeq)
        id
        (fun _ _ -> Feliz.Html.none)

let private countsFor (sites: string list) : Map<string, Counts> =
    sites |> List.map (fun s -> s, { Starts = 0; Stops = 0 }) |> Map.ofList

let private start (program: Client.ShellProgram<Set<string>, Probe>) =
    let runtime = Runtime(program.Component, program.Interpreter, program.Host, ())

    runtime.Start()
    runtime

let private send (runtime: Runtime<unit, Set<string>, Probe, _, _, _>) (msg: Probe) =
    runtime.Dispatch(msg, User "test") |> ignore

let tests =
    testList "Shell subscriptions and the runtime that runs them" [

        testCase "the shell lists its full subscription set"
        <| fun () ->
            Expect.equal
                (Client.subscriptions ClientConfig.defaults |> List.map _.Site)
                expectedSites
                "the shell seams, in listing order — a new subscription must join this list AND every composer"

            Expect.equal
                (Client.resources ClientConfig.defaults
                 |> Map.toList
                 |> List.map (fst >> ResourceKey.segments))
                (expectedSites |> List.sort |> List.map List.singleton)
                "each is a resource under its site label"

        testCase "the navigation-request subscription translates bus requests into ModuleSelected"
        <| fun () ->
            let nav =
                Client.subscriptions ClientConfig.defaults
                |> List.find (fun s -> s.Site = "navigation-request")

            let captured = ResizeArray<Client.Msg>()
            let subscription = nav.Start(fun msg -> captured.Add msg)

            NavigationRequest.request "_sdk.TeamManager"

            Expect.equal
                (List.ofSeq captured)
                [ Client.ModuleSelected "_sdk.TeamManager" ]
                "a bus request dispatches the same ModuleSelected a sidebar click does"

            subscription.Dispose()
            NavigationRequest.request "_sdk.TeamManager"

            Expect.equal captured.Count 1 "a stopped subscription no longer receives bus requests"

        testCase "Subscription.map lifts dispatch into the outer Msg and keeps the identity"
        <| fun () ->
            let nav =
                Client.subscriptions ClientConfig.defaults
                |> List.find (fun s -> s.Site = "navigation-request")

            let mapped = Subscription.map Wrap nav

            Expect.equal mapped.Site nav.Site "the site survives the map — the runtime keys and diffs on it"
            Expect.isTrue (mapped.Equals nav) "so a relisted, lifted subscription is the same resource"

            let captured = ResizeArray<Outer>()
            let subscription = mapped.Start(fun msg -> captured.Add msg)

            NavigationRequest.request "_sdk.HealthMonitor"

            Expect.equal
                (List.ofSeq captured)
                [ Wrap(Client.ModuleSelected "_sdk.HealthMonitor") ]
                "the shell Msg arrives wrapped in the outer Msg — the ShellMsg shape the composer routes to Client.update"

            subscription.Dispose()

        testCase "the AI composer lists every shell subscription"
        <| fun () ->
            // The regression shape: a composer that built its program with
            // no shell resources had a bus with no subscriber, so a tile
            // click was a silent no-op.
            let composed =
                AIClientConfig.composerResources ToolUp.AI.AISlotFills.empty ClientConfig.defaults

            Expect.equal
                (composed |> Map.toList |> List.map (fst >> ResourceKey.segments))
                (expectedSites |> List.sort |> List.map (fun site -> [ "shell"; site ]))
                "the composer lists the shell's set, under the shell's prefix, and nothing else with no assistant"

        testCase "a message and its follow-ups render once"
        <| fun () ->
            let program = probeProgram (countsFor [ "a" ]) (ResizeArray())
            let runtime = start program
            let before = runtime.Renders
            send runtime (Chain 3)

            Expect.equal (runtime.Renders - before) 1 "one drain, one render, however many follow-ups it ran"

            runtime.Terminate()

        testCase "a delisted resource stops once, and termination stops the rest once"
        <| fun () ->
            let counts = countsFor [ "a"; "b"; "c" ]
            let runtime = start (probeProgram counts (ResizeArray()))

            for site in [ "a"; "b"; "c" ] do
                Expect.equal (counts[site].Starts, counts[site].Stops) (1, 0) $"{site} starts once at boot"

            send runtime (Drop "b")
            send runtime (Chain 2)

            Expect.equal (counts["b"].Starts, counts["b"].Stops) (1, 1) "the dropped resource stops with its listing"
            Expect.equal (counts["a"].Starts, counts["a"].Stops) (1, 0) "a kept resource is never restarted"

            runtime.Terminate()
            runtime.Terminate()

            for site in [ "a"; "b"; "c" ] do
                Expect.equal (counts[site].Starts, counts[site].Stops) (1, 1) $"{site} stops exactly once"

        testCase "an update that throws reaches the error sink as the message's update error"
        <| fun () ->
            let reported = ResizeArray<ErrorContext>()
            let runtime = start (probeProgram (countsFor [ "a" ]) reported)
            send runtime Throw

            match List.ofSeq reported with
            | [ ctx ] ->
                Expect.equal ctx.Phase (ErrorPhase.Update(box Throw)) "the phase names the message"
                Expect.isTrue (ctx.Message.StartsWith "Unable to process the message: ") ctx.Message
                Expect.equal ctx.Exception.Message "update threw on purpose" "the exception is the one update threw"
            | other -> failwithf "expected one report, got %d" other.Length

            runtime.Terminate()

        testCase "a performer that throws reaches the error sink with the effect's site"
        <| fun () ->
            let reported = ResizeArray<ErrorContext>()
            let runtime = start (probeProgram (countsFor [ "a" ]) reported)
            send runtime BadEffect

            match List.ofSeq reported with
            | [ ctx ] ->
                Expect.equal ctx.Message "Error handling the effect: Probe.bad" "the text names the effect"
                Expect.equal ctx.Exception.Message "performer threw on purpose" "the exception is the performer's"
            | other -> failwithf "expected one report, got %d" other.Length

            runtime.Terminate()

        testCase "a subscription that fails to start reaches the error sink as a subscription error"
        <| fun () ->
            let reported = ResizeArray<ErrorContext>()

            let config = {
                ClientConfig.defaults with
                    Composition.OnElmishError = Some reported.Add
            }

            let failing =
                Subscription<Probe>("failing", fun _ -> failwith "start threw on purpose")

            let program =
                Client.programOver
                    config
                    (fun () -> Set.empty, [])
                    (fun (_: Probe) model -> model, [])
                    (fun _ -> Map.ofList [ ResourceKey.constant "failing", failing ])
                    id
                    (fun _ _ -> Feliz.Html.none)

            let runtime = start program

            match List.ofSeq reported with
            | [ ctx ] ->
                Expect.equal ctx.Phase (ErrorPhase.Subscription "failing") "the phase names the subscription"
                Expect.equal ctx.Message "Error starting subscription: failing" "the text the shell always used"
            | other -> failwithf "expected one report, got %d" other.Length

            runtime.Terminate()

        testCase "a view that throws on its first build reaches the error sink and propagates"
        <| fun () ->
            let reported = ResizeArray<ErrorContext>()

            let config = {
                ClientConfig.defaults with
                    Composition.OnElmishError = Some reported.Add
            }

            let program =
                Client.programOver
                    config
                    (fun () -> (), [])
                    (fun (_: Probe) model -> model, [])
                    (fun _ -> Map.empty)
                    id
                    (fun _ _ -> failwith "view threw on purpose")

            let propagated =
                try
                    viewOf program () ignore
                    false
                with _ ->
                    true

            Expect.isTrue propagated "a first build has nothing to hold, so the throw propagates"

            match List.ofSeq reported with
            | [ ctx ] ->
                Expect.equal ctx.Phase ErrorPhase.View "the phase is the view's"
                Expect.equal ctx.Message "Error rendering the model" "the text the shell always used"
            | other -> failwithf "expected one report, got %d" other.Length
    ]