// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 762 — the gate lane, read from `TOOLUP_TEST_LANE` once per
/// process.
///
/// The full gate is ~16 minutes of wall-clock (measured 2026-09-12: 967 s
/// over 23 packs), which a dispatched worker has to spend before it can
/// know whether its own change compiles green. A lane lets it spend
/// minutes on its own work first and the full gate ONCE, on the folded
/// tree — the citable run.
///
/// **The lane is an environment variable, not a `verify.ps1` switch, and
/// that is deliberate.** `verify.ps1` is a hard-denied path on the forge
/// roadmap side (`RM-FOOTPRINT-FORBIDDEN` — the gate script sits beneath
/// approval), so a `-Lane` parameter would move the recorded gate-script
/// hash and could not be authored at all. The variable rides INSIDE the
/// recorded `--gate-cmd` string instead
/// (`$env:TOOLUP_TEST_LANE='fast'; pwsh ./verify.ps1`), so the evidence
/// stays self-describing and the gate file's hash never moves.
///
/// **Why this file lives under `ToolUp.Platform.Tests/Support/` and is
/// source-linked into `Build.fsproj`.** There are two readers of the
/// variable — the `VerifyAll` driver (which selects PACKS) and the
/// Platform pack's own entry point (which selects LISTS) — and they sit
/// in assemblies that cannot reference each other. Two independent
/// parsers of one variable drift, and a lane that means different things
/// to the driver and the pack is worse than no lane, so there is exactly
/// one parser and `Build.fsproj` links it. The namespace is
/// `ToolUp.Forge` (forge's own build hygiene, as `SdkManifest.fs` is)
/// rather than the tests namespace it is filed under. It takes no
/// dependency beyond `System` for the same reason: `Build.fsproj` has no
/// Expecto, and the Expecto-shaped half lives in `TestLaneFilter.fs`
/// beside it, which only the pack compiles.
module ToolUp.Forge.TestLane

open System

/// Which lane this process is running.
[<RequireQualifiedAccess>]
type Lane =
    /// Only the suites DECLARED to touch no filesystem, process, socket
    /// or environment variable. Seconds. Membership is opt-IN at both
    /// levels — an unaudited pack or list is never in this lane — so the
    /// lane can under-claim but never over-claim.
    | Pure
    /// Everything except the measured slow set. The pre-merge lane.
    | Fast
    /// The whole suite. The default, and the only lane a ship may cite.
    | Full

/// The variable this module reads. Named once so a caller reporting it
/// (a warning, a `--help` line, a recorded gate command) cannot misspell
/// it independently of the reader.
[<Literal>]
let VariableName = "TOOLUP_TEST_LANE"

/// The spelling of a lane in the variable and in operator prose.
let name (lane: Lane) =
    match lane with
    | Lane.Pure -> "pure"
    | Lane.Fast -> "fast"
    | Lane.Full -> "full"

/// Every accepted spelling, for an error message that tells the reader
/// what to type instead of only that they were wrong.
let accepted = [ Lane.Pure; Lane.Fast; Lane.Full ] |> List.map name

/// Parse one variable value. Unset / empty is `Full` — the default lane
/// is the pre-phase behaviour, so a machine that has never heard of this
/// variable runs exactly the gate it ran before.
///
/// An UNRECOGNISED value is an `Error`, and the caller resolves it to
/// `Full` with a warning rather than to a reduced lane. That direction is
/// load-bearing: a typo resolving to `full` costs minutes, whereas a typo
/// resolving to `fast` would report a green over a suite the operator did
/// not choose — and the operator would read it as the full run they
/// asked for.
let parse (raw: string) : Result<Lane, string> =
    match (if isNull raw then "" else raw.Trim().ToLowerInvariant()) with
    | "" -> Ok Lane.Full
    | "pure" -> Ok Lane.Pure
    | "fast" -> Ok Lane.Fast
    | "full" -> Ok Lane.Full
    | other -> Error other

/// The lane this process runs, read once. Module-level, so the variable
/// is read exactly once however many times a caller asks — a lane that
/// could change mid-run would make the pack's own count of what it
/// excluded a lie.
let current: Lane =
    match parse (Environment.GetEnvironmentVariable VariableName) with
    | Ok lane -> lane
    | Error other ->
        eprintfn
            "WARNING: %s=%s is not a lane; running the FULL suite. Accepted: %s."
            VariableName
            other
            (String.Join(", ", accepted))

        Lane.Full

/// One line naming the lane and what it left out, printed ONLY when the
/// lane is not `full`. The default lane must leave the gate's output
/// exactly as it was, so an unset variable prints nothing at all — the
/// full gate is byte-for-byte the run it was before this phase.
let announce (whatWasExcluded: string) =
    if current <> Lane.Full then
        printfn "%s=%s — %s" VariableName (name current) whatWasExcluded