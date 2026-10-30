module ToolUp.Platform.Tests.InProcess.FactWriteTests

// Phase 1091 — the write core as one pure plan. `FactWrite.plan` is what
// asserting drafts means for every store; these are its laws, stated over
// the plan alone, with no store behind it. Each row of the Phase 888
// differential seed — a lineage's versions, a competing method, a
// human-asserted value, a cosmetic period label, a repeated draft, an
// awkward subject path, a batch carrying two versions of one lineage — is
// restated here as a property of one plan. That a store EXECUTES the plan
// is the contract pack's law (`IFactStoreContract.tests`), bound to every
// store.

open System
open Expecto
open ToolUp.Facts

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private rollup = Computed("rollup", "1", "p0")
let private estimator = Computed("estimator", "1", "p0")

let private draftAt (path: string list) (metric: string) (method: MethodRef) (value: FactValue) (input: string) = {
    Subject = { Hierarchy = "geography"; Path = path }
    Metric = MetricRef metric
    Value = value
    Period = q2
    Method = method
    Evidence = {
        ResultRef = None
        InputHashes = [ input ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Disclosure.Surfaceable
}

let private draft path input value =
    draftAt path "revenue" rollup (Scalar value) input

let private t0 = DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc)

let private request shape drafts =
    match FactWrite.prepare shape drafts with
    | Ok request -> request
    | Error errors -> failtestf "expected the pre-flight to pass, got %A" errors

let private planOver (stored: Fact list) shape drafts =
    let ids = stored |> List.map _.FactId |> Set.ofList
    FactWrite.plan t0 ids.Contains stored (request shape drafts)

/// Sequential scalar plans, each over the facts the earlier ones wrote —
/// what the same drafts asserted one at a time would store.
let private sequential (stored: Fact list) (drafts: FactDraft list) : Fact list =
    drafts
    |> List.fold
        (fun (prior: Fact list, written: Fact list) d ->
            let plan = planOver prior WriteShape.ScalarAssert [ d ]
            prior @ plan.Facts, written @ plan.Facts)
        (stored, [])
    |> snd

/// The Phase 888 differential seed's rows, as drafts.
let private differentialRows: FactDraft list = [
    draft [ "uk" ] "uk-q2-v1" 1m
    draft [ "uk" ] "uk-q2-v2" 2m
    draftAt [ "uk" ] "revenue" estimator (Scalar 3m) "uk-q2-est"
    {
        draft [ "uk" ] "uk-q2-relabelled" 4m with
            Period = { q2 with Label = None }
    }
    draftAt [ "eu"; "sku-000001" ] "elasticity" (HumanAsserted "cfo") (Scalar 77m) "cfo-1"
    draftAt [ "eu"; "sku-000001" ] "elasticity" (HumanAsserted "cfo") (Scalar 78m) "cfo-1"
    draftAt [ "eu"; "sku-000006" ] "elasticity" rollup (Absent "no data loaded") "h6"
    draft [ "eu"; "we>ird\tta\\b\nnl" ] "hodd" 42m
    draft [ "eu"; "100%" ] "hpct" 43.0m
    draft [ "eu"; "100%" ] "hpct-scale" 43.00m
    draft [ "fr" ] "fr-q2" 9m
    draft [ "fr" ] "fr-q2" 9m
]

let tests =
    testList "FactWrite — the write plan (Phase 1091)" [

        testCase "a plan is deterministic in its inputs"
        <| fun () ->
            let prior = (planOver [] WriteShape.BatchAssert [ draft [ "uk" ] "v1" 1m ]).Facts
            let once = planOver prior WriteShape.BatchAssert differentialRows
            let twice = planOver prior WriteShape.BatchAssert differentialRows
            Expect.equal once twice "the same stored facts, drafts and clock give the same plan"

        testCase "a batch settles exactly as the same drafts asserted one at a time"
        <| fun () ->
            let prior = (planOver [] WriteShape.BatchAssert [ draft [ "uk" ] "v0" 0m ]).Facts
            let batch = planOver prior WriteShape.BatchAssert differentialRows
            Expect.equal batch.Facts (sequential prior differentialRows) "batch and sequential scalar plans agree"

        testCase "an unchanged draft lands in Unchanged and never as a new fact"
        <| fun () ->
            let stored = (planOver [] WriteShape.ScalarAssert [ draft [ "uk" ] "v1" 1m ]).Facts
            let plan = planOver stored WriteShape.BatchAssert [ draft [ "uk" ] "v1" 1m ]
            Expect.isEmpty plan.Facts "nothing new is written"
            Expect.equal plan.Unchanged [ stored.Head.FactId ] "the stored address is unchanged"
            Expect.isEmpty plan.IndexDeltas "no head moves"
            Expect.equal plan.Receipt.IdempotentCount 1 "the receipt counts the skip"

        testCase "a repeated draft in one request is written once and unchanged the second time"
        <| fun () ->
            let plan =
                planOver [] WriteShape.BatchAssert [ draft [ "fr" ] "fr-q2" 9m; draft [ "fr" ] "fr-q2" 9m ]

            Expect.equal (List.length plan.Facts) 1 "one write"
            Expect.equal plan.Unchanged [ plan.Facts.Head.FactId ] "the repeat is unchanged"

        testCase "versions of one lineage supersede in submission order with strictly increasing AsOf"
        <| fun () ->
            let plan =
                planOver [] WriteShape.BatchAssert [
                    draft [ "uk" ] "v1" 1m
                    draft [ "uk" ] "v2" 2m
                    draft [ "uk" ] "v3" 3m
                ]

            let v1, v2, v3 =
                match plan.Facts with
                | [ a; b; c ] -> a, b, c
                | other -> failtestf "expected three facts, got %d" other.Length

            Expect.equal v1.Supersedes None "the first version opens the lineage"
            Expect.equal v2.Supersedes (Some v1.FactId) "v2 supersedes v1"
            Expect.equal v3.Supersedes (Some v2.FactId) "v3 supersedes v2"
            Expect.isTrue (v1.AsOf < v2.AsOf && v2.AsOf < v3.AsOf) "AsOf strictly increases under a frozen clock"
            Expect.equal plan.Supersessions [ v2.FactId, v1.FactId; v3.FactId, v2.FactId ] "the edges"

            Expect.equal
                plan.IndexDeltas
                [
                    {
                        Lineage = Fact.lineageKey v1.Subject v1.Metric v1.Period v1.Method
                        Retires = None
                        Head = v3.FactId
                    }
                ]
                "one lineage moves, to its last version"

        testCase "a stored head is superseded one tick after it when the clock is not later"
        <| fun () ->
            let head = {
                (planOver [] WriteShape.ScalarAssert [ draft [ "uk" ] "v1" 1m ]).Facts.Head with
                    AsOf = t0.AddDays 1.0
            }

            let plan = planOver [ head ] WriteShape.ScalarAssert [ draft [ "uk" ] "v2" 2m ]
            Expect.equal plan.Facts.Head.AsOf (head.AsOf.AddTicks 1L) "law L3 under a clock behind the head"
            Expect.equal plan.Facts.Head.Supersedes (Some head.FactId) "it supersedes the stored head"
            Expect.equal plan.IndexDeltas.Head.Retires (Some head.FactId) "the delta retires the stored head"

        testCase "the head rule takes the latest AsOf, and the first seen among equals"
        <| fun () ->
            let a = {
                (planOver [] WriteShape.ScalarAssert [ draft [ "uk" ] "a" 1m ]).Facts.Head with
                    AsOf = t0
            }

            let b = {
                (planOver [] WriteShape.ScalarAssert [ draft [ "uk" ] "b" 2m ]).Facts.Head with
                    AsOf = t0
            }

            let plan = planOver [ a; b ] WriteShape.ScalarAssert [ draft [ "uk" ] "c" 3m ]
            Expect.equal plan.Facts.Head.Supersedes (Some a.FactId) "first among equal AsOf values"
            Expect.equal (FactWrite.headOf [ a; b ]) (Some a) "the shared head rule agrees"

        testCase "a competing method opens its own lineage and supersedes nothing"
        <| fun () ->
            let stored = (planOver [] WriteShape.ScalarAssert [ draft [ "uk" ] "v1" 1m ]).Facts

            let plan =
                planOver stored WriteShape.ScalarAssert [ draftAt [ "uk" ] "revenue" estimator (Scalar 3m) "est" ]

            Expect.equal plan.Facts.Head.Supersedes None "competitors are never merged (D19)"
            Expect.equal plan.Receipt.AssertedCount 1 "asserted, not superseding"

        testCase "a human-asserted value change is a new fact that supersedes (D18)"
        <| fun () ->
            let cfo v =
                draftAt [ "eu" ] "elasticity" (HumanAsserted "cfo") (Scalar v) "cfo-1"

            let plan = planOver [] WriteShape.BatchAssert [ cfo 77m; cfo 78m ]
            let first, second = plan.Facts.Head, plan.Facts.Tail.Head
            Expect.notEqual first.FactId second.FactId "the value is folded into the address"
            Expect.equal second.Supersedes (Some first.FactId) "and the second supersedes the first"

        testCase "a period label is cosmetic: the relabelled draft supersedes in the same lineage"
        <| fun () ->
            let plan =
                planOver [] WriteShape.BatchAssert [
                    draft [ "uk" ] "v1" 1m
                    {
                        draft [ "uk" ] "v2" 2m with
                            Period = { q2 with Label = None }
                    }
                ]

            Expect.equal plan.Facts.Tail.Head.Supersedes (Some plan.Facts.Head.FactId) "one lineage"

        testCase "a scalar write is audited per fact, with the supersession edge"
        <| fun () ->
            let stored = (planOver [] WriteShape.ScalarAssert [ draft [ "uk" ] "v1" 1m ]).Facts
            let plan = planOver stored WriteShape.ScalarAssert [ draft [ "uk" ] "v2" 2m ]
            let fact = plan.Facts.Head

            Expect.equal
                (plan.Audit |> List.map FactAuditEvent.eventType)
                [ FactEvents.AssertedType; FactEvents.SupersededType ]
                "FactAsserted, then FactSuperseded"

            match plan.Audit with
            | [ FactAuditEvent.AssertedRow asserted; FactAuditEvent.SupersededRow superseded ] ->
                Expect.equal asserted.FactId fact.FactId "the asserted row names the fact"
                Expect.equal asserted.Method "computed:rollup:1:p0" "the method identity"
                Expect.equal asserted.Subject "geography/uk" "the readable subject"
                Expect.equal superseded.SupersededFactId stored.Head.FactId "the edge"
                Expect.equal (FactAuditEvent.occurredAt plan.Audit.Head) fact.AsOf "timed by the fact"
            | other -> failtestf "unexpected audit %A" other

        testCase "an idempotent scalar write is not audited"
        <| fun () ->
            let stored = (planOver [] WriteShape.ScalarAssert [ draft [ "uk" ] "v1" 1m ]).Facts

            Expect.isEmpty
                (planOver stored WriteShape.ScalarAssert [ draft [ "uk" ] "v1" 1m ]).Audit
                "nothing changed state"

        testCase "a batch is audited by one row carrying its receipt, even when nothing moved"
        <| fun () ->
            let written = planOver [] WriteShape.BatchAssert differentialRows

            match written.Audit with
            | [ FactAuditEvent.BatchAssertedRow row ] ->
                Expect.equal row.Receipt written.Receipt "the receipt the caller is told"
                Expect.equal row.AsOf (written.Facts |> List.map _.AsOf |> List.max) "timed by its latest fact"
            | other -> failtestf "unexpected audit %A" other

            let again = planOver written.Facts WriteShape.BatchAssert differentialRows
            Expect.isEmpty again.Facts "a re-run writes nothing"

            match again.Audit with
            | [ FactAuditEvent.BatchAssertedRow row ] ->
                Expect.equal row.Receipt.IdempotentCount (List.length differentialRows) "all idempotent"
                Expect.equal row.AsOf t0 "timed by the clock"
            | other -> failtestf "unexpected audit %A" other

        testCase "an empty batch is no write and no audit row"
        <| fun () ->
            let plan = planOver [] WriteShape.BatchAssert []
            Expect.isEmpty plan.Audit "nothing to do"
            Expect.equal plan.Receipt BatchAssertReceipt.empty "the empty receipt"

        testCase "the receipt partitions the drafts and digests the full ordered disposition"
        <| fun () ->
            let plan = planOver [] WriteShape.BatchAssert differentialRows
            let r = plan.Receipt
            Expect.equal r.DraftCount (List.length differentialRows) "every draft"
            Expect.equal (r.AssertedCount + r.SupersedingCount + r.IdempotentCount) r.DraftCount "a partition"
            Expect.equal (List.length plan.Facts) (r.AssertedCount + r.SupersedingCount) "the writes"
            Expect.equal (List.length plan.Unchanged) r.IdempotentCount "the skips"

        testCase "a batch with malformed drafts is refused whole, naming each by position"
        <| fun () ->
            let bad = {
                draft [ "uk" ] "v1" 1m with
                    Metric = MetricRef ""
            }

            match FactWrite.prepare WriteShape.BatchAssert [ draft [ "fr" ] "ok" 1m; bad ] with
            | Ok _ -> failtest "expected a refusal"
            | Error errors ->
                Expect.equal
                    (FactWrite.renderOffenders 2 errors)
                    "fact store batch rejected: 1 of 2 drafts are malformed and none were committed — #1 geography/uk / : metric id is empty"
                    "the caller-facing refusal"

        testCase "an empty path member is a scalar fact the batch pre-flight refuses"
        <| fun () ->
            let empty = draft [ "eu"; "" ] "hempty" 43.0m
            Expect.equal (planOver [] WriteShape.ScalarAssert [ empty ]).Facts.Length 1 "Assert writes it"
            Expect.isError (FactWrite.prepare WriteShape.BatchAssert [ empty ]) "AssertBatch refuses it"

        testCase "a scalar write is never refused by the pre-flight (GP 11)"
        <| fun () ->
            let bad = {
                draft [ "uk" ] "v1" 1m with
                    Metric = MetricRef ""
            }

            Expect.isOk (FactWrite.prepare WriteShape.ScalarAssert [ bad ]) "Assert accepts what it always accepted"

        testCase "the refusal names at most ten offenders and counts the rest"
        <| fun () ->
            let bad =
                List.init 12 (fun i -> {
                    draft [ "uk" ] (string i) 1m with
                        Metric = MetricRef ""
                })

            match FactWrite.prepare WriteShape.BatchAssert bad with
            | Ok _ -> failtest "expected a refusal"
            | Error errors ->
                let text = FactWrite.renderOffenders 12 errors
                Expect.stringContains text "12 of 12 drafts" "the count"
                Expect.stringEnds text " (and 2 more)" "the overflow"

        testCase "a recorded draft is addressed exactly as a write of it"
        <| fun () ->
            let d = draftAt [ "eu" ] "elasticity" (HumanAsserted "cfo") (Scalar 77m) "cfo-1"
            let written = (planOver [] WriteShape.ScalarAssert [ d ]).Facts.Head
            Expect.equal (FactWrite.recordedAt d t0) written "one draft-to-fact step"

        testCase "the competition indicator names the other methods' heads only"
        <| fun () ->
            let plan =
                planOver [] WriteShape.BatchAssert [
                    draft [ "uk" ] "v1" 1m
                    draftAt [ "uk" ] "revenue" estimator (Scalar 3m) "est"
                    draft [ "fr" ] "f1" 2m
                ]

            let uk = plan.Facts.Head

            Expect.equal
                (FactCompetition.competingMethods plan.Facts uk)
                [ "computed:estimator:1:p0" ]
                "the estimator competes with the rollup for the UK"

            Expect.isEmpty (FactCompetition.competingMethods plan.Facts (List.last plan.Facts)) "France is uncontested"
    ]