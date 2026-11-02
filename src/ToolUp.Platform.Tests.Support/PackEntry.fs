// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The entry point every pack of the Platform family shares.
///
/// The Platform test pack is partitioned by server part: one pack per part,
/// the umbrella's pack as the residue, and the public-API approval gate's own
/// pack (docs/migrations/1194-platform-pack-partition.md). Each pack runs the
/// same three steps over its own registered list, and they live here once:
///
/// 1. the registration guard, over everything the pack REGISTERS, in every lane
///    (an unregistered `[<Tests>]` binding fails loud);
/// 2. the lane filter, over what RUNS, applied after the guard, so the guard
///    never sees a lane's projection;
/// 3. under the `affected` lane, the pack's share of the lists file (its
///    `<row>/<list>` entries, and for the residue the unqualified ones), with
///    an account of what it kept and skipped printed before anything runs.
///
/// Every pack runs sequenced by default: Expecto deadlocks when parallel tests
/// write to the console. `--parallel` still overrides (see
/// docs/platform/testing-conventions.md).
module ToolUp.Platform.Tests.Support.PackEntry

open System.Reflection
open Expecto
open ToolUp.Forge

/// What one pack declares about itself.
type Pack = {
    /// The pack's gate-manifest row, which a lists-file entry `<row>/<list>` names.
    Row: string
    /// The pack's own assembly, which the guard sweeps for `[<Tests>]` bindings.
    Assembly: Assembly
    /// A lower bound on the bindings the guard's sweep finds, so a sweep gone
    /// blind fails rather than passing vacuously.
    GuardFloor: int
    /// The `[<Tests>]` bindings this pack deliberately does not run, each with its reason.
    Exemptions: TestRegistrationGuard.Exemption list
    /// The pack's measured slow lists and audited pure lists.
    Lanes: TestLaneFilter.LaneDeclaration
    /// The explicitly-enumerated list the pack runs.
    Registered: Test
}

let private describe (tree: Test) =
    sprintf
        "%s — %d case(s)"
        (TestLaneFilter.labelOf tree |> Option.defaultValue "<unlabelled list>")
        (TestLaneFilter.caseCount tree)

/// Run `pack` under the lane this process reads, and return the exit code.
let run (pack: Pack) (argv: string array) : int =
    // The `affected` lane's lists, resolved against the registered list once,
    // so the tree that runs and the account printed of it are one resolution.
    let affectedKeep =
        match TestLane.current with
        | TestLane.Lane.Affected ->
            TestLane.Lists.read ()
            |> Result.bind (fun (_, lists) ->
                TestLaneFilter.resolveKeep
                    (TestLaneFilter.bindingResolver pack.Assembly)
                    (TestLane.Lists.forPack pack.Row lists)
                    pack.Registered)
            |> Some
        | TestLane.Lane.Pure
        | TestLane.Lane.Fast
        | TestLane.Lane.Full -> None

    match affectedKeep with
    | Some(Error why) ->
        eprintfn "%s" why
        2
    | _ ->
        let keep =
            match affectedKeep with
            | Some(Ok k) -> Some k
            | Some(Error _)
            | None -> None

        let dropped =
            TestLaneFilter.droppedIn TestLane.current pack.Lanes keep pack.Registered

        // Silent under `full` (the default), so the shipping gate's output is
        // unchanged; any other lane names itself and what it left out.
        TestLane.announce (
            sprintf
                "%d case(s) in %d registered list(s) are not in this run"
                (dropped |> List.sumBy TestLaneFilter.caseCount)
                (List.length dropped)
        )

        match keep with
        | Some k ->
            printfn
                "  kept %d list(s), %d case(s); skipped %d list(s); map: none"
                k.Kept.Length
                (k.Kept |> List.sumBy (snd >> TestLaneFilter.caseCount))
                k.Skipped.Length

            for line, tree in k.Kept do
                printfn "  kept     %s  [%s]" (describe tree) line

            for tree in k.Skipped do
                printfn "  skipped  %s" (describe tree)
        | None -> ()

        let guarded =
            TestRegistrationGuard.withGuardExempting pack.Assembly pack.GuardFloor pack.Exemptions pack.Registered
            |> TestLaneFilter.forLaneIn TestLane.current pack.Lanes keep pack.Registered

        runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv guarded