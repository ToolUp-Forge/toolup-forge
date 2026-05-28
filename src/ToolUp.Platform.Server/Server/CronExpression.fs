module ToolUp.Platform.CronExpression

open System

// ─── 5-field cron expression (Phase 9b) ──────────────────────────
//
// Standard 5-field cron: `Minute Hour DayOfMonth Month DayOfWeek`.
//
// Supported syntax (intentional v1 subset):
//   * `*`         — every value in range
//   * `n`         — single integer
//   * `n,m,p`     — comma-separated list of integers
//   * `*/N`       — step (every Nth value, starting at the field minimum)
//
// Not supported (deferred — flagged in `Trigger.CronTrigger` doc
// comment for module authors):
//   * Ranges `n-m`        — easy follow-up if asked for
//   * Named months (`JAN`) — easy follow-up if asked for
//   * Named days  (`MON`)  — easy follow-up if asked for
//   * Question marks, `L`, `W`, `#`, seconds field — Quartz-isms,
//     keep them out unless a concrete need arises
//
// Validation lives at parse time so `IJobScheduler.Schedule` can
// reject `Trigger.CronTrigger` with `ScheduleError.InvalidCron(expr,
// reason)` before any persistence work runs. Silent acceptance of
// nonsense expressions is the bug class this module exists to
// prevent.

type CronExpression = {
    Minutes: Set<int>
    Hours: Set<int>
    DaysOfMonth: Set<int>
    Months: Set<int>
    /// 0..6 with 0 = Sunday (matches `DateTime.DayOfWeek`'s
    /// `int`-cast semantics). `7` accepted as an alias for `0` on
    /// input, normalised to `0`.
    DaysOfWeek: Set<int>
}

// ─── Field parsing ───────────────────────────────────────────────

let private parseField (fieldName: string) (token: string) (lo: int) (hi: int) : Result<Set<int>, string> =
    let inRange v = v >= lo && v <= hi

    let validate (xs: int list) =
        match xs |> List.tryFind (fun v -> not (inRange v)) with
        | Some bad -> Error $"{fieldName}: value %d{bad} outside range [%d{lo}, %d{hi}]"
        | None -> Ok(Set.ofList xs)

    let trimmed = token.Trim()

    if trimmed = "*" then
        Ok(Set.ofList [ lo..hi ])
    elif trimmed.StartsWith "*/" then
        match Int32.TryParse(trimmed.Substring 2) with
        | false, _ -> Error $"{fieldName}: malformed step expression '{trimmed}'"
        | true, step when step <= 0 -> Error $"{fieldName}: step must be positive, got %d{step}"
        | true, step ->
            let values = [ lo..step..hi ]

            Ok(Set.ofList values)
    elif trimmed.Contains ',' then
        let parts = trimmed.Split(',')

        let parsed =
            parts
            |> Array.toList
            |> List.map (fun p ->
                match Int32.TryParse(p.Trim()) with
                | true, v -> Ok v
                | false, _ -> Error $"{fieldName}: '{p}' is not an integer")

        match
            parsed
            |> List.tryFind (function
                | Error _ -> true
                | _ -> false)
        with
        | Some(Error msg) -> Error msg
        | _ ->
            let values =
                parsed
                |> List.choose (function
                    | Ok v -> Some v
                    | _ -> None)

            validate values
    else
        match Int32.TryParse trimmed with
        | true, v -> validate [ v ]
        | false, _ -> Error $"{fieldName}: '{trimmed}' is not a recognised expression"

// ─── Parse / evaluate ────────────────────────────────────────────

/// Parse a 5-field cron expression. Returns `Error` with a specific
/// human-readable reason on any malformed field; `Ok` with the
/// pre-expanded `CronExpression` value on success. Implementations
/// must call this at registration time, not at tick time.
let tryParse (expr: string) : Result<CronExpression, string> =
    if String.IsNullOrWhiteSpace expr then
        Error "cron expression is empty"
    else
        let parts =
            expr.Trim().Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

        if parts.Length <> 5 then
            Error
                $"cron expression must have 5 fields (Minute Hour DayOfMonth Month DayOfWeek), got %d{parts.Length}: '%s{expr}'"
        else
            let minutes = parseField "Minute" parts[0] 0 59
            let hours = parseField "Hour" parts[1] 0 23
            let domsRaw = parseField "DayOfMonth" parts[2] 1 31
            let months = parseField "Month" parts[3] 1 12
            // Accept 0-7 for day-of-week and normalise 7 to 0 (Sunday).
            let dowsRaw = parseField "DayOfWeek" parts[4] 0 7

            let normaliseDow (s: Set<int>) =
                if s.Contains 7 then s |> Set.remove 7 |> Set.add 0 else s

            match minutes, hours, domsRaw, months, dowsRaw with
            | Ok m, Ok h, Ok dom, Ok mo, Ok dow ->
                Ok {
                    Minutes = m
                    Hours = h
                    DaysOfMonth = dom
                    Months = mo
                    DaysOfWeek = normaliseDow dow
                }
            | Error e, _, _, _, _ -> Error e
            | _, Error e, _, _, _ -> Error e
            | _, _, Error e, _, _ -> Error e
            | _, _, _, Error e, _ -> Error e
            | _, _, _, _, Error e -> Error e

/// Does `t` fall on a moment when this expression fires? Checks all
/// five fields against `t`'s minute / hour / day / month / day-of-week
/// values. Day-of-month and day-of-week are an OR (vixie cron's "if
/// either is restricted, the union fires") — the in-process default
/// follows that convention because it's the more common one in
/// scheduling software.
let isDue (expr: CronExpression) (t: DateTime) : bool =
    let minuteMatch = expr.Minutes.Contains t.Minute
    let hourMatch = expr.Hours.Contains t.Hour
    let monthMatch = expr.Months.Contains t.Month
    let domRestricted = expr.DaysOfMonth.Count < 31
    let dowRestricted = expr.DaysOfWeek.Count < 7
    let domMatch = expr.DaysOfMonth.Contains t.Day
    let dowMatch = expr.DaysOfWeek.Contains(int t.DayOfWeek)

    let dayMatch =
        match domRestricted, dowRestricted with
        | true, true -> domMatch || dowMatch
        | true, false -> domMatch
        | false, true -> dowMatch
        | false, false -> true

    minuteMatch && hourMatch && monthMatch && dayMatch

/// Find the first minute strictly after `after` at which the
/// expression fires. Returns `None` if no match exists within one
/// year (defensive cap — `30 2 31 2 *` (Feb 31st) cannot fire and
/// the search would otherwise loop forever). Scans minute-by-minute,
/// which is cheap at minute precision: at most 525,600 checks per
/// year, completes in milliseconds.
let nextRunAfter (expr: CronExpression) (after: DateTime) : DateTime option =
    let start =
        (after.AddMinutes 1.0).AddSeconds(-float after.Second).AddMilliseconds(-float after.Millisecond)

    let limit = start.AddYears 1
    let mutable t = start
    let mutable result = None

    while Option.isNone result && t < limit do
        if isDue expr t then
            result <- Some t
        else
            t <- t.AddMinutes 1.0

    result