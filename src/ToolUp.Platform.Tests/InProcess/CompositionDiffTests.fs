module ToolUp.Platform.Tests.InProcess.CompositionDiffTests

open Expecto
open ToolUp.Platform

// ─── Phase 286 — composition structural diff (CompositionDiff.diff) ────
//
// Covers the acceptance shape: two `CompositionManifest`s diff by stable
// `ComponentId` (config knobs by `Name`) into a `CompositionDelta` —
// add / remove / swap / config-change each surface as the right delta; a
// changed entry reports the specific field deltas (label / impl moved),
// not just "changed"; an identical pair diffs to `empty`; and the diff is
// order-independent (keyed by id, not list position).

// A raw module entry with an explicit id, so we can move its display label
// while holding the id — the canonical "changed, not removed+added" case.
let private moduleEntry (id: string) (label: string) : ComponentEntry = {
    Id = ComponentId.ofModule id
    Kind = ModuleComponent
    Label = label
    Impl = None
}

// A single-impl companion slot carrying an impl sub-id, so we can swap the
// impl while holding the slot id (a same-id "changed" companion).
let private slotEntry (iface: string) (impl: string) : ComponentEntry = {
    Id = ComponentId.forCompanionSlot iface
    Kind = CompanionComponent
    Label = iface
    Impl = Some impl
}

let tests =
    testList "CompositionDiff" [

        // ── an identical pair diffs to the empty delta ───────────────
        testCase "an identical pair diffs to empty"
        <| fun _ ->
            let m =
                CompositionManifest.build
                    [ moduleEntry "orders" "Orders" ]
                    [ CompositionManifest.companionImplEntry "IAuditSink" "splunk" ]
                    [ CompositionManifest.dataTypeEntry "SalesData" ] [ CompositionManifest.toolEntry "orders.run" ] [
                        CompositionManifest.knob "RateLimiter" "InProcess"
                    ]

            let delta = CompositionDiff.diff m m
            Expect.isTrue (CompositionDiff.isEmpty delta) "a manifest diffed against itself is empty"

        // ── a module added surfaces under ModulesAdded ───────────────
        testCase "a module added surfaces as ModulesAdded"
        <| fun _ ->
            let before = CompositionManifest.build [ moduleEntry "orders" "Orders" ] [] [] [] []

            let after =
                CompositionManifest.build [ moduleEntry "orders" "Orders"; moduleEntry "shipping" "Shipping" ] [] [] [] []

            let delta = CompositionDiff.diff before after

            Expect.equal
                (delta.ModulesAdded |> List.map _.Id)
                [ ComponentId.ofModule "shipping" ]
                "the new module is added"

            Expect.isEmpty delta.ModulesRemoved "nothing removed"
            Expect.isEmpty delta.ModulesChanged "nothing changed"

        // ── a module removed surfaces under ModulesRemoved ───────────
        testCase "a module removed surfaces as ModulesRemoved"
        <| fun _ ->
            let before =
                CompositionManifest.build [ moduleEntry "orders" "Orders"; moduleEntry "shipping" "Shipping" ] [] [] [] []

            let after = CompositionManifest.build [ moduleEntry "orders" "Orders" ] [] [] [] []

            let delta = CompositionDiff.diff before after

            Expect.equal
                (delta.ModulesRemoved |> List.map _.Id)
                [ ComponentId.ofModule "shipping" ]
                "the dropped module is removed"

            Expect.isEmpty delta.ModulesAdded "nothing added"

        // ── a renamed module (stable id) surfaces as a CHANGED entry ─
        //    with the specific label field delta, not remove + add. ───
        testCase "a renamed module (stable id) is a changed entry with a label delta"
        <| fun _ ->
            let before = CompositionManifest.build [ moduleEntry "orders" "Orders" ] [] [] [] []

            let after =
                CompositionManifest.build [ moduleEntry "orders" "Order Service" ] [] [] [] []

            let delta = CompositionDiff.diff before after

            Expect.isEmpty delta.ModulesAdded "a rename is not an add"
            Expect.isEmpty delta.ModulesRemoved "a rename is not a remove"

            match delta.ModulesChanged with
            | [ c ] ->
                Expect.equal c.Id (ComponentId.ofModule "orders") "the changed entry keeps its stable id"
                Expect.equal c.LabelDelta (Some("Orders", "Order Service")) "the label field delta is reported"
                Expect.equal c.ImplDelta None "the impl did not move"
            | other -> failtestf "expected exactly one changed module, got %A" other

        // ── a companion impl swap on a stable slot → CHANGED w/ impl delta
        testCase "a companion impl swap on a stable slot is a changed entry with an impl delta"
        <| fun _ ->
            let before = CompositionManifest.build [] [ slotEntry "IBlobStorage" "s3" ] [] [] []

            let after =
                CompositionManifest.build [] [ slotEntry "IBlobStorage" "azure" ] [] [] []

            let delta = CompositionDiff.diff before after

            match delta.CompanionSlotsChanged with
            | [ c ] ->
                Expect.equal c.Id (ComponentId.forCompanionSlot "IBlobStorage") "the slot id is stable across the swap"
                Expect.equal c.ImplDelta (Some(Some "s3", Some "azure")) "the impl field delta is reported"
            | other -> failtestf "expected exactly one changed companion slot, got %A" other

        // ── a multi-impl companion swap (id composes sub-id) → remove + add
        testCase "a multi-impl companion swap surfaces as remove + add (id composes the sub-id)"
        <| fun _ ->
            let before =
                CompositionManifest.build [] [ CompositionManifest.companionImplEntry "IAuditSink" "splunk" ] [] [] []

            let after =
                CompositionManifest.build [] [ CompositionManifest.companionImplEntry "IAuditSink" "datadog" ] [] [] []

            let delta = CompositionDiff.diff before after

            Expect.equal
                (delta.CompanionSlotsRemoved |> List.map _.Id)
                [ ComponentId.forCompanionImpl "IAuditSink" "splunk" ]
                "the old impl (by its composed id) is removed"

            Expect.equal
                (delta.CompanionSlotsAdded |> List.map _.Id)
                [ ComponentId.forCompanionImpl "IAuditSink" "datadog" ]
                "the new impl (by its composed id) is added"

        // ── a datatype added / removed surfaces on the datatype lane ─
        testCase "datatype add / remove surface on the datatype lane"
        <| fun _ ->
            let before =
                CompositionManifest.build [] [] [ CompositionManifest.dataTypeEntry "SalesData" ] [] []

            let after =
                CompositionManifest.build [] [] [ CompositionManifest.dataTypeEntry "StockData" ] [] []

            let delta = CompositionDiff.diff before after

            Expect.equal
                (delta.DataTypesRemoved |> List.map _.Id)
                [ ComponentId.forDataType "SalesData" ]
                "the dropped datatype is removed"

            Expect.equal
                (delta.DataTypesAdded |> List.map _.Id)
                [ ComponentId.forDataType "StockData" ]
                "the new datatype is added"

        // ── a config-knob value change surfaces as ConfigKnobsChanged ─
        testCase "a config-knob value change surfaces with the before/after values"
        <| fun _ ->
            let before =
                CompositionManifest.build [] [] [] [] [ CompositionManifest.knob "RateLimiter" "NoRateLimiter" ]

            let after =
                CompositionManifest.build [] [] [] [] [ CompositionManifest.knob "RateLimiter" "InProcessRateLimiter" ]

            let delta = CompositionDiff.diff before after

            match delta.ConfigKnobsChanged with
            | [ k ] ->
                Expect.equal k.Name "RateLimiter" "the changed knob is named"
                Expect.equal k.Before "NoRateLimiter" "the prior value is reported"
                Expect.equal k.After "InProcessRateLimiter" "the new value is reported"
            | other -> failtestf "expected exactly one changed knob, got %A" other

            Expect.isEmpty delta.ConfigKnobsAdded "a value change is not an add"
            Expect.isEmpty delta.ConfigKnobsRemoved "a value change is not a remove"

        // ── order independence: same entries, different list order → empty
        testCase "the diff is order-independent (keyed by id, not position)"
        <| fun _ ->
            let a = moduleEntry "orders" "Orders"
            let b = moduleEntry "shipping" "Shipping"
            let c = moduleEntry "inventory" "Inventory"

            let before = CompositionManifest.build [ a; b; c ] [] [] [] []
            let after = CompositionManifest.build [ c; a; b ] [] [] [] []

            let delta = CompositionDiff.diff before after

            Expect.isTrue (CompositionDiff.isEmpty delta) "the same module set in a different order diffs to empty"

        // ── order independence with a real change buried in a shuffle ─
        testCase "a single change is detected regardless of surrounding order"
        <| fun _ ->
            let before =
                CompositionManifest.build [ moduleEntry "orders" "Orders"; moduleEntry "shipping" "Shipping" ] [] [] [] []

            let after =
                CompositionManifest.build
                    // shipping renamed, and the two entries re-ordered.
                    [ moduleEntry "shipping" "Fulfilment"; moduleEntry "orders" "Orders" ]
                    []
                    [] [] []

            let delta = CompositionDiff.diff before after

            match delta.ModulesChanged with
            | [ ch ] ->
                Expect.equal
                    ch.Id
                    (ComponentId.ofModule "shipping")
                    "the changed module is identified by id, not position"

                Expect.equal ch.LabelDelta (Some("Shipping", "Fulfilment")) "its label delta is reported"
            | other -> failtestf "expected exactly one changed module, got %A" other

        // ── render: the readable failure names what moved ────────────
        testCase "render names the added / removed / changed units + knob move"
        <| fun _ ->
            let before =
                CompositionManifest.build [ moduleEntry "orders" "Orders" ] [] [] [] [
                    CompositionManifest.knob "RateLimiter" "NoRateLimiter"
                ]

            let after =
                CompositionManifest.build
                    [ moduleEntry "orders" "Order Service"; moduleEntry "shipping" "Shipping" ]
                    []
                    [] [] [ CompositionManifest.knob "RateLimiter" "InProcessRateLimiter" ]

            let text = CompositionDiff.render (CompositionDiff.diff before after)

            Expect.stringContains text "module:shipping" "the added module id appears"
            Expect.stringContains text "module:orders" "the changed module id appears"
            Expect.stringContains text "Order Service" "the new label appears"
            Expect.stringContains text "RateLimiter" "the changed knob appears"
            Expect.stringContains text "InProcessRateLimiter" "the new knob value appears"

        // ── render of the empty delta is a single benign line ────────
        testCase "render of an empty delta is a single benign line"
        <| fun _ ->
            let text = CompositionDiff.render CompositionDiff.empty
            Expect.stringContains text "no composition differences" "empty renders to a benign no-diff line"
    ]