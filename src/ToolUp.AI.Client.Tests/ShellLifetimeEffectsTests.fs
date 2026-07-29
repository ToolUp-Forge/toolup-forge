module ToolUp.AI.Client.Tests.ShellLifetimeEffectsTests

open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.Elmish
open ToolUp.Platform

// ─── Shell program-lifetime effects — the outer-composer contract ─────
//
// The shell's background subscriptions (the NavigationRequest bus, the
// cross-module event bus, the SSE notification stream, the auth-token
// watcher, the auth-bridge health observer) register as lifetime-aware
// `EffectHandle`s at the `Program` site — NOT inside `Client.init`. An
// outer composer that rebuilds the Program from the shell's
// `init`/`update`/`view` pieces (ToolUp.AI's `withSidePanel`) therefore
// starts from an empty effect list and must re-attach them via
// `Client.withShellLifetimeEffects`, mapping each handle into its outer
// Msg.
//
// Dropping them is silent: the observed defect (Phase 573 follow-up) was
// an admin-landing tile click doing nothing in an AI-composed app —
// `AdminHome.update`'s `OpenModule` called `NavigationRequest.request`,
// and the bus is a documented no-op with zero subscribers, so no error
// surfaced anywhere. These tests pin the seam from both sides: the
// shell exposes the full set, and the attach helper delivers every one
// of them into an outer program, dispatch lifted through the outer Msg
// (the exact `ShellMsg` shape `withSidePanel` uses).

/// The five shell subscriptions, in their attach order. A shell effect
/// added without extending this list fails the pin below — which is the
/// point: it must also be delivered through every outer composer, and
/// this test is where that obligation surfaces.
let private expectedIds = [
    "navigation-request"
    "module-events"
    "notifications-stream"
    "auth-token-acquired"
    "auth-bridge-health"
]

/// Stand-in for a composer's outer message type (`OuterMsg.ShellMsg`).
type private Outer = Wrap of Client.Msg

/// An outer program of the composer shape: its own Msg, the shell's
/// nowhere in its type — exactly what `withSidePanel` builds before
/// re-attaching the shell effects.
let private outerProgram () : Program<unit, int, Outer, unit> =
    Program.mkProgram (fun () -> 0, Cmd.none) (fun (_: Outer) model -> model, Cmd.none) (fun _ _ -> ())

let tests =
    testList "Shell program-lifetime effects (outer-composer contract)" [

        testCase "programLifetimeEffects exposes the full shell subscription set"
        <| fun () ->
            Expect.equal
                (Client.programLifetimeEffects ClientConfig.defaults |> List.map _.Id)
                expectedIds
                "the five shell seams, in attach order — a new shell effect must join this list AND every outer composer"

        testCase "the navigation-request effect translates bus requests into ModuleSelected"
        <| fun () ->
            let nav =
                Client.programLifetimeEffects ClientConfig.defaults
                |> List.find (fun e -> e.Id = "navigation-request")

            let captured = ResizeArray<Client.Msg>()
            let subscription = nav.Start(fun msg -> captured.Add msg)

            NavigationRequest.request "_sdk.TeamManager"

            Expect.equal
                (List.ofSeq captured)
                [ Client.ModuleSelected "_sdk.TeamManager" ]
                "a bus request dispatches the same ModuleSelected a sidebar click does"

            subscription.Dispose()
            NavigationRequest.request "_sdk.TeamManager"

            Expect.equal captured.Count 1 "a disposed subscription no longer receives bus requests"

        testCase "EffectHandle.map lifts dispatch into the outer Msg and preserves identity"
        <| fun () ->
            let nav =
                Client.programLifetimeEffects ClientConfig.defaults
                |> List.find (fun e -> e.Id = "navigation-request")

            let mapped = EffectHandle.map Wrap nav

            Expect.equal mapped.Id nav.Id "the id survives the map — HMR disposal and uniqueness key on it"
            Expect.equal mapped.Lifetime nav.Lifetime "so does the lifetime — same subscription, same disposal story"

            let captured = ResizeArray<Outer>()
            let subscription = mapped.Start(fun msg -> captured.Add msg)

            NavigationRequest.request "_sdk.HealthMonitor"

            Expect.equal
                (List.ofSeq captured)
                [ Wrap(Client.ModuleSelected "_sdk.HealthMonitor") ]
                "the shell Msg arrives wrapped in the outer Msg — the ShellMsg shape withSidePanel routes to Client.update"

            subscription.Dispose()

        testCase "withShellLifetimeEffects attaches every shell effect to an outer program"
        <| fun () ->
            // The regression shape: withSidePanel built its outer Program
            // with an EMPTY effect list, so the bus had no subscriber and
            // a tile click was a silent no-op. The helper is the one
            // definition site both Client.program and outer composers
            // attach through.
            let composed =
                outerProgram () |> Client.withShellLifetimeEffects ClientConfig.defaults Wrap

            Expect.equal
                (Program.effectIds composed)
                expectedIds
                "an outer composer re-attaches the same set the shell's own program carries"

            Expect.isEmpty
                (Program.effectIds (outerProgram ()))
                "and without the helper the composed program has no effects at all — the defect this pack pins"
    ]