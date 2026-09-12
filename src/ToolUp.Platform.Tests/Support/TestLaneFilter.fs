// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 762 — the lane filter over a pack's registered test tree.
///
/// The Expecto-shaped half of `TestLane.fs` beside it: the vocabulary is
/// shared with the `VerifyAll` driver (which has no Expecto), the tree
/// surgery is not.
///
/// **Selection is by PHYSICAL IDENTITY, never by label** — the same
/// choice `TestRegistrationGuard` made, for the same reason. A
/// `[<Tests>] let tests = testList "…" [ … ]` binding is a module-level
/// value evaluated once, so the object the pack's list holds IS the
/// object a declaration here names. Matching on labels would let an
/// unrelated nested list with a colliding name silently join or leave a
/// lane, and a lane that quietly drops a suite is the exact failure this
/// pack's registration guard exists to end.
///
/// **The filter runs AFTER the registration guard is attached, and that
/// ordering is load-bearing.** The guard asserts that every
/// `[<Tests>]`-attributed binding in the assembly is reachable in the
/// tree it is given. Filtering FIRST would drop the slow lists out of
/// that tree and the guard would then fail under `fast` naming them as
/// unregistered — a lane that cannot go green. Filtering SECOND leaves
/// the guard quantifying over the pack's full registration (which is
/// what it is about) while the lane decides only what RUNS. The guard's
/// own cases survive every lane: `withGuardExempting` appends them as
/// siblings of the registered children, and nothing here ever names
/// them, so every lane runs at least the check that the pack's
/// registration is complete.
module ToolUp.Platform.Tests.Support.TestLaneFilter

open System.Collections.Generic
open Expecto
open ToolUp.Forge

/// What a pack declares about its own lists.
type LaneDeclaration = {
    /// Lists measured slow enough to dominate the gate. Excluded from
    /// `fast` and `pure`; run in `full`.
    Slow: Test list
    /// Lists declared to touch no filesystem, process, socket or
    /// environment variable. The `pure` lane keeps ONLY these (plus the
    /// registration guard). Opt-in, so an unaudited list is excluded
    /// from `pure` by default rather than wrongly claimed for it.
    Pure: Test list
}

let private identitySet (tests: Test list) =
    let set = HashSet<Test>(HashIdentity.Reference)
    tests |> List.iter (set.Add >> ignore)
    set

/// The pack's directly-registered children — the granularity a
/// declaration names. `None` when the root is not the
/// `testList "<pack>" [ … ]` shape every forge pack uses, in which case
/// the caller declines to filter rather than guessing at a structure it
/// does not recognise.
let private registeredChildren (root: Test) : Test list option =
    match root with
    | TestLabel(_, TestList(children, _), _) -> Some(List.ofSeq children)
    | TestCase _
    | TestList _
    | TestLabel _
    | Test.Sequenced _ -> None

/// Rebuild `tree` without any node in `drop`. A list that loses every
/// child is KEPT as an empty list rather than removed: the shape of the
/// surviving tree then still mirrors the pack's registration, so a
/// `--list-tests` diff between lanes reads as "these cases are absent",
/// not "this pack was reorganised".
let rec private prune (drop: HashSet<Test>) (tree: Test) : Test option =
    if drop.Contains tree then
        None
    else
        match tree with
        | TestCase _ -> Some tree
        | TestList(tests, state) -> Some(TestList(tests |> Seq.choose (prune drop) |> Seq.toList, state))
        | TestLabel(label, inner, state) -> prune drop inner |> Option.map (fun i -> TestLabel(label, i, state))
        // Qualified: `CLIArguments.Sequenced` shadows the `Test` case
        // under a bare `open Expecto`.
        | Test.Sequenced(method', inner) -> prune drop inner |> Option.map (fun i -> Test.Sequenced(method', i))

/// The nodes this lane drops, given the pack's declaration and the tree
/// as it was REGISTERED (before the guard was attached).
let dropped (declaration: LaneDeclaration) (registeredRoot: Test) : Test list =
    match TestLane.current with
    | TestLane.Lane.Full -> []
    | TestLane.Lane.Fast -> declaration.Slow
    | TestLane.Lane.Pure ->
        // `pure` is the complement of an allowlist rather than a second
        // slow list: every registered child except the declared-pure
        // ones. Adding a list to the pack therefore leaves it OUT of
        // `pure` until someone audits it, which is the direction a
        // purity claim has to fail in.
        match registeredChildren registeredRoot with
        | None -> []
        | Some children ->
            let keep = identitySet declaration.Pure
            children |> List.filter (keep.Contains >> not)

/// The number of test CASES in `tree` — labels and lists are structure,
/// not cases. Used to state what a lane left out in the numbers Expecto
/// itself reports, so "excluded 144" and Expecto's own totals are
/// comparable without translating between two ways of counting.
let rec caseCount (tree: Test) : int =
    match tree with
    | TestCase _ -> 1
    | TestList(tests, _) -> tests |> Seq.sumBy caseCount
    | TestLabel(_, inner, _) -> caseCount inner
    | Test.Sequenced(_, inner) -> caseCount inner

/// Apply `declaration` for the current lane to `guardedTree` — the
/// pack's registered root with its registration guard already attached.
/// `full` returns the tree PHYSICALLY UNCHANGED, so the default lane
/// cannot differ from the pre-phase run even in tree identity.
let forLane (declaration: LaneDeclaration) (registeredRoot: Test) (guardedTree: Test) : Test =
    match dropped declaration registeredRoot with
    | [] -> guardedTree
    | drop ->
        match prune (identitySet drop) guardedTree with
        | Some filtered -> filtered
        | None -> guardedTree