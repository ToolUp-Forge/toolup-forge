module ToolUp.Platform.Tests.InProcess.CronExpressionTests

open System
open Expecto
open ToolUp.Platform

// ─── CronExpression — parser + evaluator tests ───────────────────
//
// `CronExpression` is a pure module — no IO, no DI — so tests can
// exercise it directly without the contract-pack indirection that
// the persistence interfaces use.

let tests =
    testList "CronExpression" [

        test "parses every-minute wildcard" {
            match CronExpression.tryParse "* * * * *" with
            | Ok expr ->
                Expect.equal expr.Minutes.Count 60 "every minute 0..59"
                Expect.equal expr.Hours.Count 24 "every hour 0..23"
                Expect.equal expr.DaysOfMonth.Count 31 "every day 1..31"
                Expect.equal expr.Months.Count 12 "every month 1..12"
                Expect.equal expr.DaysOfWeek.Count 7 "every dow 0..6"
            | Error e -> failtestf "Expected Ok, got Error '%s'" e
        }

        test "parses single-value field" {
            match CronExpression.tryParse "0 9 * * *" with
            | Ok expr ->
                Expect.equal expr.Minutes (Set.singleton 0) "minute = 0"
                Expect.equal expr.Hours (Set.singleton 9) "hour = 9"
            | Error e -> failtestf "Expected Ok, got Error '%s'" e
        }

        test "parses comma list" {
            match CronExpression.tryParse "0,15,30,45 * * * *" with
            | Ok expr -> Expect.equal expr.Minutes (Set.ofList [ 0; 15; 30; 45 ]) "quarter-hourly"
            | Error e -> failtestf "Expected Ok, got Error '%s'" e
        }

        test "parses step expression" {
            match CronExpression.tryParse "*/5 * * * *" with
            | Ok expr ->
                Expect.equal expr.Minutes (Set.ofList [ 0; 5; 10; 15; 20; 25; 30; 35; 40; 45; 50; 55 ]) "every 5 mins"
            | Error e -> failtestf "Expected Ok, got Error '%s'" e
        }

        test "normalises day-of-week 7 to 0 (Sunday)" {
            match CronExpression.tryParse "0 0 * * 7" with
            | Ok expr -> Expect.equal expr.DaysOfWeek (Set.singleton 0) "7 normalised to 0"
            | Error e -> failtestf "Expected Ok, got Error '%s'" e
        }

        test "rejects empty expression" {
            match CronExpression.tryParse "" with
            | Error _ -> ()
            | Ok _ -> failtest "Expected empty expression to be rejected"
        }

        test "rejects wrong-arity expression" {
            match CronExpression.tryParse "* * *" with
            | Error msg -> Expect.stringContains msg "5 fields" "field-count error"
            | Ok _ -> failtest "Expected wrong-arity to be rejected"
        }

        test "rejects out-of-range value" {
            match CronExpression.tryParse "60 * * * *" with
            | Error msg -> Expect.stringContains msg "Minute" "minute range error"
            | Ok _ -> failtest "Expected out-of-range minute to be rejected"
        }

        test "rejects malformed step" {
            match CronExpression.tryParse "*/abc * * * *" with
            | Error msg -> Expect.stringContains msg "step" "step parse error"
            | Ok _ -> failtest "Expected malformed step to be rejected"
        }

        test "isDue matches every-minute on any timestamp" {
            let expr =
                CronExpression.tryParse "* * * * *"
                |> Result.defaultWith (fun _ -> failwith "parse")

            Expect.isTrue (CronExpression.isDue expr (DateTime(2026, 4, 28, 14, 23, 17))) "matches arbitrary minute"
        }

        test "isDue matches hourly-on-the-hour" {
            let expr =
                CronExpression.tryParse "0 * * * *"
                |> Result.defaultWith (fun _ -> failwith "parse")

            Expect.isTrue (CronExpression.isDue expr (DateTime(2026, 4, 28, 14, 0, 0))) "matches at :00"
            Expect.isFalse (CronExpression.isDue expr (DateTime(2026, 4, 28, 14, 1, 0))) "does not match at :01"
        }

        test "nextRunAfter advances to next match" {
            let expr =
                CronExpression.tryParse "0 9 * * *"
                |> Result.defaultWith (fun _ -> failwith "parse")
            // 14:00 on 2026-04-28 -> next 09:00 should be 2026-04-29 09:00
            let after = DateTime(2026, 4, 28, 14, 0, 0)

            match CronExpression.nextRunAfter expr after with
            | Some next -> Expect.equal next (DateTime(2026, 4, 29, 9, 0, 0)) "rolls forward to next 09:00"
            | None -> failtest "Expected a next-run time"
        }

        test "nextRunAfter returns None for impossible Feb-31 expression" {
            let expr =
                CronExpression.tryParse "0 0 31 2 *"
                |> Result.defaultWith (fun _ -> failwith "parse")

            let after = DateTime(2026, 1, 1, 0, 0, 0)

            match CronExpression.nextRunAfter expr after with
            | None -> ()
            | Some t -> failtestf "Expected None for Feb 31, got %A" t
        }
    ]