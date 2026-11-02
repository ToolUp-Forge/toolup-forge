// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The lane filter over a pack's registered test tree.
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
/// **The `affected` lane is the one NAME-keyed selection**, because its
/// lists come from a file and a file cannot hold the objects. Each line is
/// resolved to the registered children it names — by the child's label,
/// or by the binding `Program.fs` registers, read by reflection, so once
/// the object is in hand the comparison is by identity again. A line that
/// names no registered child is an error the pack reports red before it
/// runs anything: a mistyped Expecto `--filter` that silently ran zero
/// tests is the failure this lane is shaped against.
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

open System
open System.Collections.Generic
open System.Reflection
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

/// What the `affected` lane's lists file resolved to over the pack's
/// registered children.
type Keep = {
    /// Each registered child kept, with the line that kept it (`*` when
    /// the file keeps everything).
    Kept: (string * Test) list
    /// Every registered child not kept.
    Skipped: Test list
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

/// The label a registered child carries, looking through the wrapper
/// `testSequenced` / `testSequencedGroup` put around a list.
let rec labelOf (tree: Test) : string option =
    match tree with
    | TestLabel(label, _, _) -> Some label
    | Test.Sequenced(_, inner) -> labelOf inner
    | TestCase _
    | TestList _ -> None

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

/// A resolver from a binding's name (`Namespace.Module.binding`, or the
/// shorter spelling `Program.fs` uses) to the value it holds, over the
/// static `Test`-valued members of `asm`. Only the named member is read,
/// so resolving a name evaluates no other test module.
let bindingResolver (asm: Assembly) : string -> Test option =
    let types =
        lazy
            (let all =
                try
                    asm.GetTypes()
                with :? ReflectionTypeLoadException as ex ->
                    ex.Types |> Array.filter (isNull >> not)

             // A compiler-generated type (`<StartupCode$…>`, a closure) is
             // never a module a registration names.
             all
             |> Array.filter (fun t ->
                 not (t.FullName.Contains "<" || t.FullName.Contains "$" || t.FullName.Contains "@"))
             |> Array.map (fun t -> t.FullName.Replace('+', '.'), t))

    let flags = BindingFlags.Public ||| BindingFlags.Static

    let read (t: Type) (memberName: string) : Test option =
        let value: obj =
            match t.GetProperty(memberName, flags) with
            | null ->
                match t.GetField(memberName, flags) with
                | null ->
                    match t.GetMethod(memberName, flags, null, Type.EmptyTypes, null) with
                    | null -> null
                    | m -> m.Invoke(null, null)
                | f -> f.GetValue null
            | p -> p.GetValue(null, null)

        match value with
        | :? Test as test -> Some test
        | _ -> None

    fun (name: string) ->
        match name.LastIndexOf '.' with
        | i when i > 0 ->
            let typeName, memberName = name.Substring(0, i), name.Substring(i + 1)
            // A module sharing its name with a type compiles as `<Name>Module`.
            let wanted = [ typeName; typeName + "Module" ]

            let exact = types.Value |> Array.filter (fun (full, _) -> List.contains full wanted)

            let matches =
                if exact.Length > 0 then
                    exact
                else
                    types.Value
                    |> Array.filter (fun (full, _) ->
                        wanted
                        |> List.exists (fun w -> full.EndsWith("." + w, StringComparison.Ordinal)))

            match matches with
            | [| (_, t) |] -> read t memberName
            | _ -> None
        | _ -> None

/// Resolve `lists` against the registered children of `registeredRoot`.
/// Each name keeps every child whose label is that name, and the child
/// that is, or contains, the value of the binding the name resolves to —
/// so a binding nested inside a sequenced group keeps the group. A name
/// keeping nothing is an `Error` naming it, and so is a root of a shape
/// this cannot select from.
let resolveKeep
    (resolveBinding: string -> Test option)
    (lists: TestLane.Lists.File)
    (registeredRoot: Test)
    : Result<Keep, string> =
    match registeredChildren registeredRoot with
    | None ->
        Error
            "the pack's registered root is not the `testList \"<pack>\" [ … ]` shape, so the affected lane cannot select from it"
    | Some children when lists.Everything ->
        Ok {
            Kept = children |> List.map (fun c -> TestLane.Lists.Everything, c)
            Skipped = []
        }
    | Some children ->
        let owner = Dictionary<Test, Test>(HashIdentity.Reference)

        let rec walk (top: Test) (t: Test) =
            owner.TryAdd(t, top) |> ignore

            match t with
            | TestCase _ -> ()
            | TestList(tests, _) -> tests |> Seq.iter (walk top)
            | TestLabel(_, inner, _) -> walk top inner
            | Test.Sequenced(_, inner) -> walk top inner

        children |> List.iter (fun c -> walk c c)

        let keptBy (name: string) =
            let byLabel = children |> List.filter (fun c -> labelOf c = Some name)

            let byBinding =
                match resolveBinding name with
                | Some value ->
                    match owner.TryGetValue value with
                    | true, top -> [ top ]
                    | false, _ -> []
                | None -> []

            byLabel @ byBinding

        let resolved = lists.Names |> List.map (fun n -> n, keptBy n)

        match resolved |> List.filter (snd >> List.isEmpty) |> List.map fst with
        | [] ->
            let seen = HashSet<Test>(HashIdentity.Reference)

            let kept =
                resolved
                |> List.collect (fun (n, cs) -> cs |> List.map (fun c -> n, c))
                |> List.filter (fun (_, c) -> seen.Add c)

            Ok {
                Kept = kept
                Skipped = children |> List.filter (seen.Contains >> not)
            }
        | unmatched ->
            Error(
                sprintf
                    "%s names %d list(s) matching no registered list, by label or by binding; refusing to run less than was asked:\n%s"
                    TestLane.ListsVariableName
                    unmatched.Length
                    (String.Join("\n", unmatched |> List.map (sprintf "  %s")))
            )

/// The nodes `lane` drops, given the pack's declaration, the `affected`
/// lane's resolved keep, and the tree as it was REGISTERED (before the
/// guard was attached).
let droppedIn
    (lane: TestLane.Lane)
    (declaration: LaneDeclaration)
    (keep: Keep option)
    (registeredRoot: Test)
    : Test list =
    match lane with
    | TestLane.Lane.Full -> []
    | TestLane.Lane.Fast -> declaration.Slow
    | TestLane.Lane.Affected ->
        match keep with
        | Some k -> k.Skipped
        | None -> []
    | TestLane.Lane.Pure ->
        // `pure` is the complement of an allowlist rather than a second
        // slow list: every registered child except the declared-pure
        // ones. Adding a list to the pack therefore leaves it OUT of
        // `pure` until someone audits it, which is the direction a
        // purity claim has to fail in.
        match registeredChildren registeredRoot with
        | None -> []
        | Some children ->
            let pure' = identitySet declaration.Pure
            children |> List.filter (pure'.Contains >> not)

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

/// Apply `lane` to `guardedTree` — the pack's registered root with its
/// registration guard already attached. `full` returns the tree
/// PHYSICALLY UNCHANGED, so the default lane cannot differ from the
/// pre-lane run even in tree identity.
let forLaneIn
    (lane: TestLane.Lane)
    (declaration: LaneDeclaration)
    (keep: Keep option)
    (registeredRoot: Test)
    (guardedTree: Test)
    : Test =
    match droppedIn lane declaration keep registeredRoot with
    | [] -> guardedTree
    | drop ->
        match prune (identitySet drop) guardedTree with
        | Some filtered -> filtered
        | None -> guardedTree