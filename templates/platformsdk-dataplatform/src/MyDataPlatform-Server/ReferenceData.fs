module MyDataPlatform.Server.ReferenceData

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text

// ─── Reference data, verified before the server serves ────────────
//
// The fiscal calendar labels every fact's period (`FY2026 P01 W02`). It is
// reference data the deployment declares, so its bytes are pinned: the
// server refuses to start when the file is not the one the hash names,
// rather than silently labelling a year of facts against a different
// calendar. The hash is over the file's text with line endings normalised
// to LF, so a checkout that converts line endings still verifies.

/// SHA-256 of `reference/fiscal-calendar.csv` (LF line endings). Update it
/// in the same commit as the calendar.
[<Literal>]
let FiscalCalendarSha256 =
    "01f2f5f011c34ad57fccebf31a077129428f153e9dea2fb0d092f661daac9b73"

let private calendarPath () =
    Path.Combine(AppContext.BaseDirectory, "reference", "fiscal-calendar.csv")

/// The calendar's text, verified against `FiscalCalendarSha256`.
let private verifiedText () : string =
    let path = calendarPath ()

    if not (File.Exists path) then
        invalidOp (sprintf "reference data %s is missing" path)

    let text = File.ReadAllText(path).Replace("\r\n", "\n")

    let actual =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

    if actual <> FiscalCalendarSha256 then
        invalidOp (
            sprintf
                "reference data %s does not match its pinned hash (expected %s, found %s); update ReferenceData.FiscalCalendarSha256 deliberately if the calendar changed"
                path
                FiscalCalendarSha256
                actual
        )

    text

/// Business date -> fiscal label, read once from the verified calendar.
let private calendar: Lazy<Map<DateOnly, string>> =
    lazy
        (verifiedText().Split('\n')
         |> Array.map _.Trim()
         |> Array.filter (fun l -> l <> "" && not (l.StartsWith "#") && not (l.StartsWith "Date,"))
         |> Array.map (fun l ->
             match l.Split(',') with
             | [| date; year; period; week |] ->
                 DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                 sprintf "FY%s P%02d W%02d" year (int period) (int week)
             | _ -> invalidOp (sprintf "fiscal calendar line '%s' is not Date,FiscalYear,FiscalPeriod,FiscalWeek" l))
         |> Map.ofArray)

/// Verify every reference dataset; call before the server starts.
let verify () : unit = calendar.Force() |> ignore

/// The fiscal label of a business date, when the calendar holds it.
let fiscalWeek (date: DateOnly) : string option = calendar.Value |> Map.tryFind date