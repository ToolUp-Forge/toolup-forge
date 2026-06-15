module ToolUp.Platform.Tests.InProcess.CleanRoomBrokerTests

open Expecto
open ToolUp.InterPlatform

// ─── Phase 18b — clean-room privacy-gate broker ──────────────────────
//
// Coverage of `DefaultCleanRoomBroker`: surface enforcement (only
// template methods run), the gate-composition invariant (a caller may
// tighten the floor, never loosen it), per-cell suppression of
// sub-threshold buckets, and whole-result withholding when the released
// cohort falls below the k-anonymity floor. Pure enforcement over a
// materialised `CohortResult`, so the pack is plain `testCase`.

let private floor: PrivacyGate = {
    MinCohortSize = 10
    SuppressionThreshold = 5
    PermittedShapes = Set.ofList [ Count; Aggregate; Histogram ]
}

let private template: CleanRoomTemplate = {
    TemplateId = "reach"
    AllowedMethods = Set.ofList [ "estimateReach"; "histogram" ]
    Floor = floor
}

let private cell label count = {
    Label = label
    Count = count
    Value = None
}

let private broker = CleanRoomBroker.create ()

let tests =
    testList "Phase 18b ICleanRoomBroker" [
        testCase "a method off the template surface is withheld"
        <| fun () ->
            let result = {
                Shape = Count
                Cells = [ cell "all" 1000 ]
            }

            match broker.Enforce(template, "exportRows", None, result) with
            | Withheld reason -> Expect.stringContains reason "not on clean-room template" "surface enforcement fires"
            | Released _ -> failtest "a non-template method must never release data"

        testCase "an output shape outside the gate is withheld"
        <| fun () ->
            // Tighten the gate so only Count is permitted, then send a Histogram.
            let countOnly: PrivacyGate = {
                floor with
                    PermittedShapes = Set.ofList [ Count ]
            }

            let result = {
                Shape = Histogram
                Cells = [ cell "x" 100 ]
            }

            match broker.Enforce(template, "histogram", Some countOnly, result) with
            | Withheld reason -> Expect.stringContains reason "shape" "shape constraint fires"
            | Released _ -> failtest "a disallowed shape must be withheld"

        testCase "a cohort below the k-anonymity floor is withheld whole"
        <| fun () ->
            let result = {
                Shape = Count
                Cells = [ cell "all" 7 ]
            } // 7 < k=10

            match broker.Enforce(template, "estimateReach", None, result) with
            | Withheld reason -> Expect.stringContains reason "k-anonymity floor" "k-floor fires"
            | Released _ -> failtest "a sub-k cohort must be withheld"

        testCase "sub-threshold cells are suppressed; survivors clearing k are released"
        <| fun () ->
            // floor: suppression 5, k 10. Two healthy buckets + two small.
            let result = {
                Shape = Histogram
                Cells = [ cell "big1" 40; cell "small1" 3; cell "big2" 30; cell "small2" 1 ]
            }

            match broker.Enforce(template, "histogram", None, result) with
            | Released(released, suppressed) ->
                Expect.equal (List.map _.Label released.Cells) [ "big1"; "big2" ] "only ≥-threshold cells survive"
                Expect.equal (List.sort suppressed) [ "small1"; "small2" ] "suppressed labels reported for audit"
            | Withheld r -> failtestf "expected Released, got Withheld %s" r

        testCase "a caller may tighten the floor: a stricter requested k withholds a borderline cohort"
        <| fun () ->
            // Cohort of 12 clears the floor (k=10) but not a requested k=20.
            let stricter: PrivacyGate = { floor with MinCohortSize = 20 }

            let result = {
                Shape = Count
                Cells = [ cell "all" 12 ]
            }

            match broker.Enforce(template, "estimateReach", Some stricter, result) with
            | Withheld reason -> Expect.stringContains reason "20" "the stricter requested k is the effective floor"
            | Released _ -> failtest "a caller-tightened gate must apply"

        testCase "a caller cannot loosen the floor: a requested k below the floor does not relax it"
        <| fun () ->
            // Caller 'requests' k=5, but the template floor is k=10; a
            // cohort of 7 must STILL be withheld (effective k = max = 10).
            let looser: PrivacyGate = { floor with MinCohortSize = 5 }

            let result = {
                Shape = Count
                Cells = [ cell "all" 7 ]
            }

            match broker.Enforce(template, "estimateReach", Some looser, result) with
            | Withheld reason -> Expect.stringContains reason "k-anonymity floor" "floor wins over a looser request"
            | Released _ -> failtest "a caller must not be able to relax the floor below the template"

        testCase "PrivacyGate.compose is the stricter of two gates"
        <| fun () ->
            let a: PrivacyGate = {
                MinCohortSize = 10
                SuppressionThreshold = 5
                PermittedShapes = Set.ofList [ Count; Histogram ]
            }

            let b: PrivacyGate = {
                MinCohortSize = 20
                SuppressionThreshold = 3
                PermittedShapes = Set.ofList [ Count; Aggregate ]
            }

            let composed = PrivacyGate.compose a b
            Expect.equal composed.MinCohortSize 20 "max cohort floor"
            Expect.equal composed.SuppressionThreshold 5 "max suppression threshold"
            Expect.equal composed.PermittedShapes (Set.ofList [ Count ]) "intersection of permitted shapes"

        testCase "isStricterOrEqual recognises a tightened gate and rejects a loosened one"
        <| fun () ->
            let tighter: PrivacyGate = {
                MinCohortSize = 20
                SuppressionThreshold = 5
                PermittedShapes = Set.ofList [ Count ]
            }

            let looser: PrivacyGate = {
                MinCohortSize = 5
                SuppressionThreshold = 5
                PermittedShapes = Set.ofList [ Count; Aggregate; Histogram ]
            }

            Expect.isTrue (PrivacyGate.isStricterOrEqual floor tighter) "tighter gate is ≥ floor"
            Expect.isFalse (PrivacyGate.isStricterOrEqual floor looser) "looser gate is not ≥ floor"
    ]