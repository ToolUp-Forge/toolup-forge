module ToolUp.Platform.Tests.InProcess.CleanRoomBrokerTests

open Expecto
open ToolUp.InterPlatform
open ToolUp.Platform.Infra.Metrics
open ToolUp.Platform.Tests.Contracts

// ─── Phase 18b — clean-room privacy-gate broker ──────────────────────
//
// Coverage of `DefaultCleanRoomBroker`: surface enforcement (only
// template methods run), the gate-composition invariant (a caller may
// tighten the floor, never loosen it), per-cell suppression of
// sub-threshold buckets, and whole-result withholding when the released
// cohort falls below the k-anonymity floor. Pure enforcement over a
// materialised `CohortResult`, so the pack is plain `testCase`. The
// differencing checks' pure parts (the filter cohort, the complement rule)
// and the release window close the list.

let private floor: PrivacyGate = {
    MinCohortSize = 10
    SuppressionThreshold = 5
    PermittedShapes = Set.ofList [ Count; Aggregate; Histogram ]
}

let private template: CleanRoomTemplate = {
    TemplateId = "reach"
    AllowedMethods = Set.ofList [ "estimateReach"; "histogram" ]
    Floor = floor
    Release = ReleaseTerms.none
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
            | Released _
            | NoisedRelease _ -> failtest "a non-template method must never release data"

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
            | Released _
            | NoisedRelease _ -> failtest "a disallowed shape must be withheld"

        testCase "a cohort below the k-anonymity floor is withheld whole"
        <| fun () ->
            let result = {
                Shape = Count
                Cells = [ cell "all" 7 ]
            } // 7 < k=10

            match broker.Enforce(template, "estimateReach", None, result) with
            | Withheld reason -> Expect.stringContains reason "k-anonymity floor" "k-floor fires"
            | Released _
            | NoisedRelease _ -> failtest "a sub-k cohort must be withheld"

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
            | other -> failtestf "expected Released, got %A" other

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
            | Released _
            | NoisedRelease _ -> failtest "a caller-tightened gate must apply"

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
            | Released _
            | NoisedRelease _ -> failtest "a caller must not be able to relax the floor below the template"

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

        testCase "the differencing floor is the larger of the cohort floor and the suppression threshold"
        <| fun () ->
            Expect.equal (PrivacyGate.differencingFloor floor) 10 "k = 10 over a threshold of 5"

            Expect.equal
                (PrivacyGate.differencingFloor { floor with SuppressionThreshold = 30 })
                30
                "a threshold above k is the floor"

        testCase "a filter cohort counts distinct members and holds none of them"
        <| fun () ->
            let cohort = FilterCohort.ofMembers [ "r1"; "r2"; "r2"; "r3" ]

            Expect.equal cohort.Size 3 "a duplicate counts once"
            Expect.equal (List.sum cohort.Sketch) 3 "the sketch counts every member once"
            Expect.equal (List.length cohort.Sketch) FilterCohort.Buckets "the sketch has one count per bucket"
            Expect.isNone (FilterCohort.problem cohort) "a built cohort reads"

            Expect.equal
                (FilterCohort.ofMembers [ "r3"; "r1"; "r2" ]).MembershipHash
                cohort.MembershipHash
                "the hash names the members, not their order"

            let stored = JsonRpc.serialize cohort

            for m in [ "\"r1\""; "\"r2\""; "\"r3\"" ] do
                Expect.isFalse (stored.Contains m) $"the stored cohort holds no member identifier ({m})"

        testCase "the distance is exact for nested cohorts and never exceeds the true difference"
        <| fun () ->
            let members n = [ for i in 1..n -> $"respondent-{i}" ]
            let all = FilterCohort.ofMembers (members 100)

            for removed in [ 1; 9; 10; 40 ] do
                Expect.equal
                    (FilterCohort.distance all (FilterCohort.ofMembers (members (100 - removed))))
                    removed
                    $"a cohort {removed} smaller and inside the other is exactly {removed} away"

            let disjoint = FilterCohort.ofMembers [ for i in 1..100 -> $"other-{i}" ]
            let distance = FilterCohort.distance all disjoint
            Expect.isLessThanOrEqual distance 200 "two disjoint cohorts of 100 differ by 200 members at most"
            Expect.isGreaterThanOrEqual distance 10 "…and two unrelated cohorts are far apart on the sketch"

            let swapped =
                FilterCohort.ofMembers ((members 100 |> List.tail) @ [ "someone-else" ])

            Expect.isTrue (FilterCohort.isNear 10 all swapped) "a one-for-one swap is near: two members differ"

            Expect.isFalse
                (FilterCohort.isNear 10 all (FilterCohort.ofMembers (members 100)))
                "the same cohort is not a near duplicate of itself"

            Expect.isFalse
                (FilterCohort.isNear 10 all (FilterCohort.ofMembers (members 90)))
                "a cohort the floor away is not near"

        testCase "a filter cohort that does not read is named"
        <| fun () ->
            let cohort = FilterCohort.ofMembers [ "r1"; "r2" ]

            Expect.isSome (FilterCohort.problem { cohort with Size = 3 }) "a sketch that does not sum to the size"
            Expect.isSome (FilterCohort.problem { cohort with Sketch = [ 2 ] }) "a sketch of the wrong length"
            Expect.isSome (FilterCohort.problem { cohort with MembershipHash = "md5:x" }) "a hash that is not sha256"

        testCase "the complement check withholds a cell whose complement is below the floor, naming the cell only"
        <| fun () ->
            let answer = {
                Shape = Histogram
                Cells = [ cell "yes" 52; cell "no" 18 ]
            }

            let split = {
                Key = "q1"
                Base = 60
                Cells = [ "yes"; "no" ]
            }

            match ComplementRule.apply 10 (ComplementRule.Within "q1") [ split ] answer with
            | Error reason -> failtestf "a readable declaration was refused: %s" reason
            | Ok(released, verdicts) ->
                Expect.equal
                    (released.Cells |> List.map _.Label)
                    [ "no" ]
                    "yes (complement 8) is withheld; no (complement 42) stands"

                Expect.equal
                    verdicts
                    [ DifferencingVerdict.ComplementBelowFloor("q1", "yes") ]
                    "the verdict names the split and the cell"

                let reason = DifferencingVerdict.describe (List.head verdicts)
                Expect.isFalse (reason.Contains "8") "the verdict never names the count"

        testCase "the complement check refuses a declaration it cannot read"
        <| fun () ->
            let answer = {
                Shape = Histogram
                Cells = [ cell "yes" 52 ]
            }

            let within = ComplementRule.Within "q1"

            Expect.isError (ComplementRule.apply 10 within [] answer) "no split under the key"

            Expect.isError
                (ComplementRule.apply
                    10
                    within
                    [
                        {
                            Key = "q1"
                            Base = 40
                            Cells = [ "yes" ]
                        }
                    ]
                    answer)
                "a cell counting more than its split's base"

            Expect.isOk
                (ComplementRule.apply
                    10
                    within
                    [
                        {
                            Key = "q1"
                            Base = 80
                            Cells = [ "absent" ]
                        }
                    ]
                    answer)
                "a split naming a cell the answer does not carry withholds nothing"

        IReleaseWindowContract.tests "InMemoryReleaseWindow" (fun () -> InMemoryReleaseWindow() :> IReleaseWindow)

        IReleaseWindowContract.tests "BlobReleaseWindow" (fun () ->
            BlobReleaseWindow(InMemoryBlobStorage.InMemoryBlobStorage()) :> IReleaseWindow)
    ]