module MyDataPlatform.Tests.Reader

open System
open Expecto
open DailyTrade.Server

// The delivery reader, on its own: it accepts the shape it documents and
// rejects everything else with a line-numbered problem.

let private calendar (date: DateOnly) =
    if date.Year = 2026 then Some "FY2026 P01 W02" else None

let private header = "Date,Site,Channel,Orders,Shoppers,Revenue,Units"

let tests =
    testList "the delivery reader" [
        test "a well-formed delivery reads every line" {
            match read calendar (header + "\n# a comment\n2026-01-12,uk-web,direct,3,2,30.50,7\n") with
            | Ok [ line ] ->
                Expect.equal line.Site "uk-web" "site"
                Expect.equal line.Revenue 30.50m "revenue"
            | other -> failtestf "expected one line, got %A" other
        }

        test "a wrong header is refused" {
            match read calendar "Day,Site\n2026-01-12,uk-web\n" with
            | Error [ problem ] -> Expect.stringContains problem "line 1" "names the header line"
            | other -> failtestf "expected a header problem, got %A" other
        }

        test "a duplicate subject-day is refused" {
            let row = "2026-01-12,uk-web,direct,3,2,30.50,7"

            match read calendar (String.concat "\n" [ header; row; row ]) with
            | Error [ problem ] -> Expect.stringContains problem "more than once" "names the duplicate"
            | other -> failtestf "expected a duplicate problem, got %A" other
        }

        test "an empty delivery is refused" {
            Expect.isError (read calendar header) "a header and no rows writes nothing"
        }
    ]