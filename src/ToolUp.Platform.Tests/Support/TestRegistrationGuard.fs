// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Support.TestRegistrationGuard

open System
open System.Collections.Generic
open System.Reflection
open Expecto

// ─── The unregistered-`[<Tests>]`-list guard (Phase 722) ────────────
//
// These packs do NOT use Expecto's `[<Tests>]` auto-discovery. Each
// `Program.fs` calls `runTestsWithCLIArgs` over an explicitly-enumerated
// list, so a new `[<Tests>]`-attributed binding that is not appended to
// that list compiles, is attributed exactly like every other, and
// SILENTLY NEVER RUNS — the pack reports its usual green with a total
// nobody reads as suspicious. Phase 634 hit this: its first full-pack
// run reported 7,045 passed / 0 failed having executed none of its seven
// new cases, and it was caught only by probing `--list-tests`.
//
// This module makes that omission loud. `withGuard` appends three cases
// to a pack's own root list:
//
//   1. the SUBSET CHECK — every `[<Tests>]`-attributed binding
//      discoverable by reflection in the pack's assembly appears
//      somewhere in the registered tree, and the failure names the
//      missing bindings so the fix is a copy-paste;
//   2. the NON-VACUITY FLOOR — a lower bound on how many bindings the
//      reflection sweep found, so a sweep that has silently stopped
//      finding anything cannot pass check 1 by quantifying over the
//      empty set (the same reason `VerifyFable` asserts a TAP case
//      floor rather than reading `node --test`'s exit code);
//   3. the FALSIFIER — the comparison run over a list that
//      deliberately omits a binding, asserting it goes red, PAIRED with
//      the control that it falls silent once that binding is
//      registered. A guard that only ever agrees with itself is the
//      shape this exists to end.
//
// A case-count floor alone was considered and rejected as the primary
// mechanism: it catches a list that STOPS emitting, not one that never
// started, and the latter is the case Phase 634 hit.
//
// Comparison is by PHYSICAL identity, never by label. A `[<Tests>] let
// tests = testList "…" [ … ]` binding is a module-level value evaluated
// once, so the object the assembly hands back through reflection is the
// same object the pack's list holds — including when the pack wraps it
// (`testSequencedGroup (testList "…" [ … ])` keeps its children's
// references). Matching on labels instead would let an unrelated nested
// test name silence a genuine omission.

/// A `[<Tests>]`-attributed static binding found by reflection.
type DiscoveredBinding = {
    /// `Namespace.Module.binding` — the name an author appends to the pack's list.
    Name: string
    /// The value the binding holds, compared by physical identity.
    Value: Test
}

/// What one sweep of an assembly found.
type Discovery = {
    /// Bindings whose value was read successfully.
    Bindings: DiscoveredBinding list
    /// Attributed members whose value could not be read, with why. An
    /// unreadable binding is reported as loudly as an unregistered one —
    /// "I could not check this" is never rendered as "this is fine".
    Unreadable: (string * string) list
}

let private staticMembers (t: Type) : MemberInfo seq =
    let flags = BindingFlags.Public ||| BindingFlags.Static

    seq {
        yield! (t.GetMethods flags |> Seq.cast<MemberInfo>)
        yield! (t.GetProperties flags |> Seq.cast<MemberInfo>)
        yield! (t.GetFields flags |> Seq.cast<MemberInfo>)
    }

let private assemblyTypes (asm: Assembly) : Type array =
    try
        asm.GetTypes()
    with :? ReflectionTypeLoadException as ex ->
        ex.Types |> Array.filter (isNull >> not)

let private isTestsAttributed (mi: MemberInfo) =
    mi.GetCustomAttributes(typeof<TestsAttribute>, true) |> Array.isEmpty |> not

let private readValue (mi: MemberInfo) : Result<Test, string> =
    try
        let raw =
            match mi with
            | :? PropertyInfo as p when p.PropertyType = typeof<Test> -> Some(p.GetValue(null, null))
            | :? FieldInfo as f when f.FieldType = typeof<Test> -> Some(f.GetValue null)
            | :? MethodInfo as m when m.ReturnType = typeof<Test> && (m.GetParameters()).Length = 0 ->
                Some(m.Invoke(null, null))
            | _ -> None

        match raw with
        | Some v when not (isNull v) -> Ok(v :?> Test)
        | Some _ -> Error "the binding evaluated to null"
        | None -> Error "not a zero-argument static Test-valued member"
    with ex ->
        Error(sprintf "reading it raised %s: %s" (ex.GetType().Name) ex.Message)

/// Every `[<Tests>]`-attributed static binding in `asm`.
let discover (asm: Assembly) : Discovery =
    let named (mi: MemberInfo) =
        let owner =
            if isNull mi.DeclaringType then
                asm.GetName().Name
            else
                mi.DeclaringType.FullName

        owner + "." + mi.Name

    let attributed =
        assemblyTypes asm
        |> Seq.collect staticMembers
        |> Seq.filter isTestsAttributed
        // A property getter carries the property's name mangled as
        // `get_x` and would double-count the same binding.
        |> Seq.filter (fun mi ->
            match mi with
            | :? MethodInfo as m -> not m.IsSpecialName
            | _ -> true)
        |> Seq.toList

    let bindings = ResizeArray<DiscoveredBinding>()
    let unreadable = ResizeArray<string * string>()

    for mi in attributed do
        match readValue mi with
        | Ok value -> bindings.Add { Name = named mi; Value = value }
        | Error why -> unreadable.Add(named mi, why)

    {
        Bindings = List.ofSeq bindings
        Unreadable = List.ofSeq unreadable
    }

/// Every node reachable in `registered`, keyed by physical identity.
let private reachableNodes (registered: Test) : HashSet<Test> =
    let seen = HashSet<Test>(HashIdentity.Reference)

    let rec walk (t: Test) =
        if seen.Add t then
            match t with
            | TestCase _ -> ()
            | TestList(tests, _) -> Seq.iter walk tests
            | TestLabel(_, inner, _) -> walk inner
            // Qualified: `CLIArguments.Sequenced` shadows the `Test` case
            // under a bare `open Expecto`.
            | Test.Sequenced(_, inner) -> walk inner

    walk registered
    seen

/// The discovered bindings whose value appears nowhere in `registered`.
let unregistered (registered: Test) (discovered: DiscoveredBinding list) : DiscoveredBinding list =
    let nodes = reachableNodes registered
    discovered |> List.filter (fun b -> not (nodes.Contains b.Value))

/// A `[<Tests>]`-attributed binding a pack deliberately leaves out of
/// its list, and why. The reason is required, and a stale exemption is a
/// failure rather than a silence: one that names a binding the assembly
/// no longer carries, or one the pack has since registered, fails the
/// guard the same way an unregistered binding does. A marker that can go
/// out of date without saying so silences the finding it was written to
/// document.
type Exemption = { Binding: string; Reason: string }

/// The guard's own cases, over `registered` (the pack's root list) and
/// `asm` (the pack's own assembly — passed rather than inferred, because
/// this file is source-linked into several assemblies and one of them
/// reaches it through a project reference).
let tests (asm: Assembly) (floor: int) (exemptions: Exemption list) (registered: Test) : Test =
    let packName = asm.GetName().Name

    testList "test-registration guard (Phase 722)" [
        testCase "every [<Tests>] binding in the assembly is registered in the pack's list"
        <| fun () ->
            let found = discover asm
            let exempt = exemptions |> List.map _.Binding |> Set.ofList
            let absent = unregistered registered found.Bindings |> List.map _.Name |> Set.ofList
            let discoveredNames = found.Bindings |> List.map _.Name |> Set.ofList

            let missing = Set.difference absent exempt |> Set.toList

            let unreadable =
                found.Unreadable
                |> List.map (fun (name, why) -> name + " — " + why)
                |> List.sort

            // A stale exemption never silences: it names something this
            // assembly no longer carries, or something already registered.
            let stale =
                exemptions
                |> List.filter (fun e -> not (Set.contains e.Binding absent))
                |> List.map (fun e ->
                    if Set.contains e.Binding discoveredNames then
                        e.Binding + " — is registered; delete the exemption"
                    else
                        e.Binding
                        + " — no such [<Tests>] binding in this assembly; delete the exemption")
                |> List.sort

            if not (List.isEmpty missing && List.isEmpty unreadable && List.isEmpty stale) then
                let lines =
                    List.map (fun m -> "  UNREGISTERED  " + m) missing
                    @ List.map (fun u -> "  UNREADABLE    " + u) unreadable
                    @ List.map (fun s -> "  STALE EXEMPT  " + s) stale

                failtestf
                    "%s runs an explicitly-enumerated list, not Expecto's [<Tests>] auto-discovery, so a\nbinding absent from that list NEVER RUNS and the pack still reports green. %d binding(s) are\nnot accounted for. Append each to the pack's list in Program.fs — or, where it genuinely must not\nrun here, declare it as an Exemption with its reason. Re-check with --list-tests:\n%s"
                    packName
                    (List.length lines)
                    (String.Join("\n", lines))

        // Only meaningful where the pack actually carries attributed
        // bindings; a `found >= 0` assertion would be exactly the
        // vacuous case this guard exists to end, so packs declaring a
        // floor of zero get no floor case at all.
        if floor > 0 then
            testCase "the [<Tests>] reflection sweep still finds bindings (non-vacuity floor)"
            <| fun () ->
                let found = (discover asm).Bindings |> List.length

                if found < floor then
                    failtestf
                        "%s: the [<Tests>] reflection sweep found %d binding(s), below the declared floor of %d.\nThe subset check above passes vacuously over an empty sweep, so a sweep that has gone blind must\nfail here. Lower the floor deliberately if bindings were genuinely removed, or find out why the\nsweep stopped seeing them."
                        packName
                        found
                        floor

        testCase "the comparison goes red over a deliberately-omitted binding (falsifier)"
        <| fun () ->
            // A list `registered` does NOT contain, so the comparison is
            // run over a genuine omission rather than agreeing with
            // itself.
            let omitted =
                testList "unregistered sentinel (falsifier fixture — deliberately never registered)" [
                    testCase "placeholder" ignore
                ]

            let sentinelName = "<falsifier sentinel>"

            let probe = { Name = sentinelName; Value = omitted } :: (discover asm).Bindings

            let namesOver (tree: Test) =
                unregistered tree probe |> List.map _.Name

            Expect.contains
                (namesOver registered)
                sentinelName
                "the guard must name a discovered binding that the registered list omits"

            // The control: "reports something" is not the mechanism —
            // registering the same binding must silence it.
            Expect.isFalse
                (namesOver (testList "falsifier control" [ omitted; registered ])
                 |> List.contains sentinelName)
                "the guard must fall silent once the omitted binding is registered"
    ]

/// Append the guard to a pack's already-rooted list, keeping the root
/// label intact — Expecto's `--filter` matches a TOP-LEVEL list-name
/// prefix, so adding a wrapping label would change every test's path.
let withGuardExempting (asm: Assembly) (floor: int) (exemptions: Exemption list) (root: Test) : Test =
    let guard = tests asm floor exemptions root

    match root with
    | TestLabel(label, TestList(children, listState), labelState) ->
        TestLabel(label, TestList(List.ofSeq children @ [ guard ], listState), labelState)
    | TestCase _
    | TestList _
    | TestLabel _
    | Test.Sequenced _ -> TestList([ root; guard ], FocusState.Normal)

/// `withGuardExempting` for the ordinary case: no binding is exempt.
let withGuard (asm: Assembly) (floor: int) (root: Test) : Test = withGuardExempting asm floor [] root