module ToolUp.Scheduling.RecurrenceExpander

open System
open ToolUp.Scheduling.SchedulingTypes

// ─── Phase 20 — Recurrence expansion ────────────────────────────────
//
// Pure expansion of a `RecurrenceRule` against a seed booking and a
// time window. No I/O. Bounded termination by `Until`, `Count`, the
// caller's `window.End`, and a hard cap of 10,000 occurrences to
// defend against pathological rules (open-ended Daily with
// Count = Int32.MaxValue, etc.).
//
// The four supported frequencies:
//   * `Daily` — advance by `Interval` days each step.
//   * `Weekly` — if `ByWeekday = []`, advance by `7 * Interval` days
//     each step. Otherwise walk forward day-by-day, emitting when
//     `(weekIndex % Interval = 0) && (dayOfWeek ∈ ByWeekday)`. The
//     "active week" filter respects `Interval`; per-day filter
//     respects `ByWeekday`.
//   * `Monthly` — advance by `Interval` months via
//     `DateTimeOffset.AddMonths`. `ByWeekday` is ignored in v1
//     (full RFC 5545 monthly rules — `BYMONTHDAY`, `BYDAY` with
//     positional offsets — deferred).
//   * `Yearly` — advance by `Interval` years via `AddYears`.
//
// Both `Until` and `Count` participate when both are set: the loop
// stops at whichever fires first.

[<Literal>]
let private HardCap = 10_000

/// Produce the candidate occurrence start instants for a rule starting
/// at `seed`, bounded by `upperBound` (exclusive), the rule's
/// `Until` (exclusive) and `Count` (total including the seed), and
/// the hard cap of 10,000 occurrences.
///
/// Returns `[]` for a structurally invalid rule (`Interval <= 0`).
let occurrenceStarts (seed: DateTimeOffset) (rule: RecurrenceRule) (upperBound: DateTimeOffset) : DateTimeOffset list =
    if rule.Interval <= 0 then
        []
    else
        let countLimit = rule.Count |> Option.defaultValue Int32.MaxValue

        let untilLimit = rule.Until |> Option.defaultValue DateTimeOffset.MaxValue

        let absoluteEnd = if untilLimit < upperBound then untilLimit else upperBound

        let advance (current: DateTimeOffset) =
            match rule.Frequency with
            | Daily -> current.AddDays(float rule.Interval)
            | Weekly -> current.AddDays(float (7 * rule.Interval))
            | Monthly -> current.AddMonths(rule.Interval)
            | Yearly -> current.AddYears(rule.Interval)

        let isWeeklyWithMask = rule.Frequency = Weekly && not (List.isEmpty rule.ByWeekday)

        if isWeeklyWithMask then
            // Walk day-by-day, emit when DayOfWeek matches AND we're in
            // an active week relative to the seed. Day-by-day walk is
            // bounded by `absoluteEnd` plus `HardCap` checks on emitted
            // count (not cursor advances) so a high-Interval mask
            // doesn't run for years.
            let seedDate = seed.Date

            let weekIndex (dt: DateTimeOffset) =
                let days = (dt.Date - seedDate).Days
                if days < 0 then 0 else days / 7

            let rec walk (cursor: DateTimeOffset) (count: int) (acc: DateTimeOffset list) : DateTimeOffset list =
                if cursor >= absoluteEnd || count >= countLimit || count >= HardCap then
                    List.rev acc
                else
                    let dow = cursor.DayOfWeek
                    let week = weekIndex cursor

                    let matches = List.contains dow rule.ByWeekday && week % rule.Interval = 0

                    if matches then
                        walk (cursor.AddDays(1.0)) (count + 1) (cursor :: acc)
                    else
                        walk (cursor.AddDays(1.0)) count acc

            walk seed 0 []
        else
            let rec emit (cursor: DateTimeOffset) (count: int) (acc: DateTimeOffset list) : DateTimeOffset list =
                if cursor >= absoluteEnd || count >= countLimit || count >= HardCap then
                    List.rev acc
                else
                    emit (advance cursor) (count + 1) (cursor :: acc)

            emit seed 0 []

/// Expand a seed booking + recurrence rule into the concrete occurrences
/// whose `StartUtc` falls within `window`. Each occurrence is the
/// seed cloned with its `StartUtc` / `EndUtc` shifted, retaining
/// duration and other fields. The seed itself is included if its
/// `StartUtc` is within `window`.
///
/// Termination order: `rule.Until` / `rule.Count` / `window.End` /
/// hard cap (10,000) — whichever fires first. A caller can detect
/// the hard-cap case by comparing the returned list length to
/// `HardCap` and emitting a `RecurrenceOverflow` conflict.
let expand (seed: Booking) (rule: RecurrenceRule) (window: DateRange) : Booking list =
    let duration = seed.EndUtc - seed.StartUtc
    let starts = occurrenceStarts seed.StartUtc rule window.End

    starts
    |> List.filter (fun s -> s >= window.Start && s < window.End)
    |> List.map (fun s -> {
        seed with
            StartUtc = s
            EndUtc = s + duration
    })