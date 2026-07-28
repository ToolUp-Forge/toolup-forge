// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Testing.ModuleHarness

open ToolUp.Elmish
open ToolUp.Platform

// ─── Elmish module harness ────────────────────────────────────────────
//
// Tests a module's `init` / `update` cycle without dragging the rest
// of the Elmish shell in. The harness holds the current model + most
// recent `Cmd` and provides a fluent `Dispatch` → assertions →
// `Dispatch` chain so multi-message flows can be exercised with
// state assertions between each step.
//
// Typical usage:
//
//     let h = ModuleHarness.fromUnitInit MyModule.init MyModule.update
//     h
//         .Dispatch(MyModule.LoadData)
//         .AssertModel(fun m -> m.IsLoading)
//         .Dispatch(MyModule.DataLoaded sample)
//         .AssertModel(fun m -> not m.IsLoading)
//
// `Cmds` are captured for inspection but not executed — the harness
// is a pure-`update` harness. Tests that want to simulate command
// effects pull messages out of the captured `Cmd` and feed them back
// via `Dispatch`.

/// Holds the current `'Model` plus the most recent `Cmd<'Msg>`. The
/// fluent API returns a new harness on every `Dispatch`, mirroring
/// Elmish's pure-`update` semantics — assertions can be chained
/// without re-binding.
type ModuleHarness<'Model, 'Msg>(model: 'Model, cmd: Cmd<'Msg>, update: 'Msg -> 'Model -> 'Model * Cmd<'Msg>) =
    /// Current model.
    member _.Model = model
    /// Last `Cmd<'Msg>` emitted by `init` or `update`.
    member _.Cmd = cmd

    /// Dispatch a message through `update`; returns a new harness with
    /// the updated model + emitted Cmd.
    member _.Dispatch(msg: 'Msg) =
        let model', cmd' = update msg model
        ModuleHarness<'Model, 'Msg>(model', cmd', update)

    /// Dispatch several messages in sequence; equivalent to chaining
    /// `Dispatch` calls.
    member this.DispatchAll(msgs: #seq<'Msg>) =
        msgs |> Seq.fold (fun (h: ModuleHarness<'Model, 'Msg>) m -> h.Dispatch m) this

    /// Assert a predicate over the current model; throws on failure.
    /// Returns the same harness so assertions chain into further
    /// dispatches.
    member this.AssertModel(predicate: 'Model -> bool) =
        if not (predicate model) then
            failwithf "ModuleHarness.AssertModel: predicate failed against model %A" model

        this

    /// Like `AssertModel` but with a label included in the failure
    /// message — useful for distinguishing repeated assertions.
    member this.AssertModelWith(label: string, predicate: 'Model -> bool) =
        if not (predicate model) then
            failwithf "ModuleHarness.AssertModel(%s): predicate failed against model %A" label model

        this

    /// Assert a predicate over the last emitted `Cmd<'Msg>`. The
    /// predicate is invoked on the Cmd value itself — typically used
    /// to assert it is `Cmd.none` or to count the queued sub-cmds.
    member this.AssertCmd(predicate: Cmd<'Msg> -> bool) =
        if not (predicate cmd) then
            failwithf "ModuleHarness.AssertCmd: predicate failed on cmd %A" cmd

        this

    /// Convenience assertion for the common "no further effects" case.
    member this.AssertNoCmd() =
        if not (List.isEmpty cmd) then
            failwithf "ModuleHarness.AssertNoCmd: expected Cmd.none but got %d sub-cmd(s)" (List.length cmd)

        this

    // ─── Phase 180 — accessibility floor ──────────────────────────────
    //
    // `render` takes the harness's CURRENT model plus a dispatch and
    // returns the rendered tree; the harness runs the profile's rules
    // over it and fails on any finding fatal for that profile. Dispatch
    // is `ignore` — rendering must not depend on dispatching, and a view
    // that does is itself the defect.
    //
    // Why `render` is a parameter rather than "the module's `view`": a
    // module's `view` produces a Fable-tier Feliz `ReactElement`, which
    // the .NET Expecto runner cannot evaluate (the constraint
    // `AriaPropTests` records for the whole Fable-only client tier). The
    // caller therefore supplies whichever render it can actually run —
    // an `A11yNode` built from the view's shape, an SSR fragment, or a
    // browser-side `outerHTML` capture under a Fable test runner. See
    // `Accessibility` for the full rationale.

    /// Assert the module's rendered tree against an accessibility
    /// profile (default `Minimal`). Throws with the consolidated
    /// `A11yFinding` list — rule + element path + message — on any
    /// finding fatal for that profile. Returns the same harness so it
    /// chains alongside `AssertModel` / `AssertCmd`.
    member this.AssertAccessible
        (render: 'Model -> ('Msg -> unit) -> Accessibility.A11yNode, ?profile: Accessibility.A11yProfile)
        =
        let profile = defaultArg profile Accessibility.Minimal
        render model ignore |> Accessibility.assertAccessible profile |> ignore
        this

    /// `AssertAccessible` for a render that produces an HTML fragment
    /// string (an SSR view, or a mounted node's `outerHTML`) rather than
    /// an `A11yNode` — parsed via `Accessibility.ofHtml`.
    member this.AssertAccessibleHtml(render: 'Model -> ('Msg -> unit) -> string, ?profile: Accessibility.A11yProfile) =
        let profile = defaultArg profile Accessibility.Minimal
        render model ignore |> Accessibility.assertHtml profile |> ignore
        this

/// Build a harness from a `unit`-shaped `init`.
let fromUnitInit
    (init: unit -> 'Model * Cmd<'Msg>)
    (update: 'Msg -> 'Model -> 'Model * Cmd<'Msg>)
    : ModuleHarness<'Model, 'Msg> =
    let model, cmd = init ()
    ModuleHarness<'Model, 'Msg>(model, cmd, update)

/// Build a harness from a context-shaped `init`. Pass
/// `ClientModuleContext.empty` for the default "anonymous user, no
/// config" case, or construct a context with specific
/// `Config`/`Flags`/`UserId` to exercise context-sensitive init.
let fromContextInit
    (ctx: ClientModuleContext)
    (init: ClientModuleContext -> 'Model * Cmd<'Msg>)
    (update: 'Msg -> 'Model -> 'Model * Cmd<'Msg>)
    : ModuleHarness<'Model, 'Msg> =
    let model, cmd = init ctx
    ModuleHarness<'Model, 'Msg>(model, cmd, update)

/// Build a harness from a strongly-typed `ClientModule<'Model,'Msg>`
/// before `register` erasure. Uses `ClientModuleContext.empty` for the
/// init; tests that need a populated context build via
/// `fromContextInit` instead.
let fromClientModule (m: ClientModule<'Model, 'Msg>) : ModuleHarness<'Model, 'Msg> =
    fromContextInit ClientModuleContext.empty m.Init m.Update