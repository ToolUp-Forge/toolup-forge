// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Offline.Tests.SyncCoordinatorListenerTests

// ─── Phase 1069 — SyncCoordinator detaches what it attached ──────────
//
// `SyncCoordinator.startRouted` registers an `online` listener on the
// window and a `visibilitychange` listener on the document, and `Stop()`
// removes them. Before Phase 1069 both sites passed the F# closure
// through `unbox`, which Fable is free to emit as two DIFFERENT function
// objects — so `removeEventListener` matched nothing and a stopped
// coordinator kept waking on every reconnect and tab focus (the defect
// Phase 755 fixed in the OIDC companion).
//
// The coordinator is browser-only Fable code this .NET pack cannot run,
// so the contract is asserted on the source, the same textual approach
// the client-tier theming and localisation packs take: every listener is
// attached and detached through the `[<Emit>]` shims, with the SAME bound
// identifier at the add and the remove site, and no listener call passes
// `unbox` at all.

open System.IO
open System.Text.RegularExpressions
open Expecto

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private coordinatorSource () =
    File.ReadAllText(Path.Combine(repoRoot (), "src", "ToolUp.Offline.Client", "Client", "SyncCoordinator.fs"))

/// `addWindowListener "online" onOnline` → (add, online, onOnline).
let private shimCall =
    Regex(@"\b(add|remove)(?:Window|Document)Listener\s+""([A-Za-z]+)""\s+([A-Za-z_][A-Za-z0-9_]*)")

/// A direct DOM listener call handing it an unboxed closure — the shape
/// that emits a fresh function object per site.
let private unboxedListener =
    Regex(@"(?:add|remove)EventListener\s*\(\s*""[^""]*""\s*,\s*unbox\b")

/// The handler identifier passed at each (verb, event) site.
let private handlersBy (verb: string) (source: string) : Map<string, string list> =
    shimCall.Matches source
    |> Seq.filter (fun m -> m.Groups[1].Value = verb)
    |> Seq.map (fun m -> m.Groups[2].Value, m.Groups[3].Value)
    |> Seq.groupBy fst
    |> Seq.map (fun (event, hs) -> event, hs |> Seq.map snd |> List.ofSeq)
    |> Map.ofSeq

[<Tests>]
let tests =
    testList "SyncCoordinator listeners (Phase 1069)" [

        test "no listener is attached or detached through unbox" {
            let source = coordinatorSource ()

            Expect.isFalse
                (unboxedListener.IsMatch source)
                "an `addEventListener`/`removeEventListener` call passes `unbox <closure>` — Fable may emit a different function object at each site, so the remove detaches nothing"
        }

        test "the handler passed to remove is the one passed to add, for both events" {
            let source = coordinatorSource ()
            let added = handlersBy "add" source
            let removed = handlersBy "remove" source

            for event in [ "online"; "visibilitychange" ] do
                let a = added |> Map.tryFind event |> Option.defaultValue []
                let r = removed |> Map.tryFind event |> Option.defaultValue []

                Expect.hasLength a 1 $"`{event}` is attached exactly once through a listener shim"
                Expect.equal r a $"`{event}` is detached with the same bound handler it was attached with"
        }

        test "the source probe sees the shim calls it asserts on — go-red" {
            // Falsifier for the two tests above: on the pre-1069 source the
            // first finds the `unbox` shape and the second finds no shim
            // call at all.
            let before = """Browser.Dom.window.addEventListener ("online", unbox onOnline)"""

            Expect.isTrue (unboxedListener.IsMatch before) "the unboxed shape is recognised"
            Expect.isEmpty (handlersBy "add" before) "and is not mistaken for a shim call"

            let after = """addWindowListener "online" onOnline"""

            Expect.equal
                (handlersBy "add" after)
                (Map.ofList [ "online", [ "onOnline" ] ])
                "a shim call is read as (event, handler)"
        }
    ]