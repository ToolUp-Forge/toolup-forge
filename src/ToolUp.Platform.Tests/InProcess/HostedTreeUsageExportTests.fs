module ToolUp.Platform.Tests.InProcess.HostedTreeUsageExportTests

open Expecto
open ToolUp.Platform

// ─── Phase 297 — ComponentId-keyed hosted-tree usage export tests ─────
//
// The export widens the Phase 268 fault signal to interaction / usage
// (action dispatch / surface visibility / capability invocation) keyed by the
// stable Phase 279 `ComponentId`, in a consumable, scope-isolated snapshot.
// This pack pins:
//   * a usage event attributes to its `ComponentId` (right kind tally);
//   * mixed-kind events tally per kind under one component;
//   * the export is scope-isolated — a scope's snapshot excludes another
//     scope's usage (GP 4);
//   * the NoOp default records nothing and snapshots empty (GP 13).

let private orders = ComponentId.ofModule "orders"
let private billing = ComponentId.ofModule "billing"

let private snapshot (export: IHostedTreeUsageExport) (scopeId: string) =
    export.Snapshot scopeId |> Async.RunSynchronously

let tests =
    testList "HostedTreeUsageExport (Phase 297)" [

        testCase "a usage event attributes to its ComponentId with the right kind tally"
        <| fun _ ->
            let export = InMemoryHostedTreeUsageExport() :> IHostedTreeUsageExport
            export.Record "team-1" (HostedTreeUsageEvent.actionDispatched orders "checkout")

            let snap = snapshot export "team-1"

            match Map.tryFind orders snap.ByComponent with
            | Some counts ->
                Expect.equal counts.ActionDispatches 1 "the action-dispatch tally is attributed to the component"
                Expect.equal counts.SurfaceVisibilities 0 "no surface-visibility recorded"
                Expect.equal counts.CapabilityInvocations 0 "no capability-invocation recorded"
            | None -> failtest "the component must appear in its scope's snapshot"

        testCase "mixed-kind events tally per kind under one component"
        <| fun _ ->
            let export = InMemoryHostedTreeUsageExport() :> IHostedTreeUsageExport
            export.Record "team-1" (HostedTreeUsageEvent.actionDispatched orders "a")
            export.Record "team-1" (HostedTreeUsageEvent.actionDispatched orders "b")
            export.Record "team-1" (HostedTreeUsageEvent.surfaceVisible orders "grid")
            export.Record "team-1" (HostedTreeUsageEvent.capabilityInvoked orders "clipboard")

            let counts = (snapshot export "team-1").ByComponent |> Map.find orders

            Expect.equal counts.ActionDispatches 2 "both action dispatches tallied"
            Expect.equal counts.SurfaceVisibilities 1 "the surface visibility tallied"
            Expect.equal counts.CapabilityInvocations 1 "the capability invocation tallied"
            Expect.equal (HostedTreeUsageCounts.total counts) 4 "total across kinds"

        testCase "the export is scope-isolated — a scope's snapshot excludes another scope's usage (GP 4)"
        <| fun _ ->
            let export = InMemoryHostedTreeUsageExport() :> IHostedTreeUsageExport
            export.Record "team-A" (HostedTreeUsageEvent.actionDispatched orders "x")
            export.Record "team-B" (HostedTreeUsageEvent.actionDispatched billing "y")

            let snapA = snapshot export "team-A"
            let snapB = snapshot export "team-B"

            Expect.equal snapA.ScopeId "team-A" "snapshot carries its scope id"
            Expect.isTrue (Map.containsKey orders snapA.ByComponent) "scope A sees its own component"
            Expect.isFalse (Map.containsKey billing snapA.ByComponent) "scope A does NOT see scope B's component"
            Expect.isTrue (Map.containsKey billing snapB.ByComponent) "scope B sees its own component"
            Expect.isFalse (Map.containsKey orders snapB.ByComponent) "scope B does NOT see scope A's component"

        testCase "an unused scope snapshots empty"
        <| fun _ ->
            let export = InMemoryHostedTreeUsageExport() :> IHostedTreeUsageExport
            export.Record "team-A" (HostedTreeUsageEvent.actionDispatched orders "x")

            let snap = snapshot export "never-used"
            Expect.isEmpty (Map.toList snap.ByComponent) "a scope with no usage snapshots empty"

        testCase "the NoOp default records nothing and snapshots empty (GP 13)"
        <| fun _ ->
            let export = NoOpHostedTreeUsageExport() :> IHostedTreeUsageExport
            export.Record "team-1" (HostedTreeUsageEvent.actionDispatched orders "x")
            export.Record "team-1" (HostedTreeUsageEvent.capabilityInvoked orders "y")

            let snap = snapshot export "team-1"
            Expect.equal snap.ScopeId "team-1" "the no-op snapshot still carries the requested scope id"
            Expect.isEmpty (Map.toList snap.ByComponent) "the NoOp export records nothing"
    ]