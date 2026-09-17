// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 687 — the native-parser fuzz leg: every `FuzzCorpus` case through
/// both of the toolkit's untrusted-input entry points, each in its own
/// capped child via the isolation seam.
///
/// **The outcome vocabulary, and what is red.** One child per case; the
/// seam answers with one of:
///
///   * ANSWERED  — `Ok` (the toolkit rendered or loaded) or `EntryFailed`
///                 (the toolkit refused cleanly). Both are contract-
///                 conformant; the shape is not asserted.
///   * CONTAINED — `MemoryCapExceeded` or `TimedOut`: the parser did not
///                 answer inside its bounds and the seam killed it. The
///                 seam did its job; the case is a FINDING (a resource-
///                 exhaustion input the parser should have refused early),
///                 recorded in the report, and the test passes, because
///                 the property this leg pins is that the host survives.
///   * CRASHED   — `WorkerCrashed`: a native fault. RED. This is the
///                 memory-safety class the seam exists to contain, and a
///                 parser that faults on input is a defect to report
///                 upstream whether or not the host survived it.
///   * a harness fault — `ProtocolViolation` / `WorkerUnavailable`: RED,
///                 because it means the leg measured nothing.
///
/// An external-entity case is additionally red if anything the toolkit
/// exported carries the planted leak marker: a resolved entity is a file
/// read the parser should never have performed.
///
/// **The order is deliberate: the entity-expansion cases run LAST.** They
/// are the ones that took the machine when this corpus ran in-process,
/// and running them last means a harness defect surfaces on the cheap
/// cases first, with the expensive ones still ahead.
module ToolUp.Companions.Fuzz.Tests.FuzzTests

open System
open System.IO
open System.IO.Compression
open System.Text
open Expecto
open ToolUp.Companions.Isolation
open ToolUp.Companions.Fuzz.Tests.Entries

/// The bounds every child runs under. The cap is the seam's default (512
/// MiB), well under anything a machine cannot spare; the timeout is the
/// seam's default (30 s), long enough for a 3,000-measure render.
/// `TOOLUP_FUZZ_MEMORY_CAP_MB` raises or lowers the cap for a
/// measurement — the way to tell "over 512 MiB" from "unbounded" for a
/// contained case is to run it again under a larger cap and watch it
/// reach that one too.
let private limits =
    match Environment.GetEnvironmentVariable "TOOLUP_FUZZ_MEMORY_CAP_MB" with
    | null
    | "" -> IsolationLimits.defaults
    | text ->
        match Int64.TryParse text with
        | true, mb when mb > 0L -> {
            IsolationLimits.defaults with
                MemoryCap = Some(mb * 1024L * 1024L)
          }
        | _ -> failwithf "TOOLUP_FUZZ_MEMORY_CAP_MB must be a positive integer (megabytes); got %s" text

/// Whether this host can hold the kernel cap. A corpus of hostile input
/// against a native parser is never run in a process without one — see
/// the 126 GB incident in `FuzzCorpus.fs` — so on a host without it the
/// cases are PENDING, not run, unless the operator opts into the soft
/// cap deliberately with `TOOLUP_FUZZ_ALLOW_SOFT_CAP=1` (a Linux host
/// with an OOM killer and a cgroup of its own).
let private capIsKernelEnforced =
    ProcessIsolation.kernelMemoryCapSupported
    || Environment.GetEnvironmentVariable "TOOLUP_FUZZ_ALLOW_SOFT_CAP" = "1"

let private nativeAvailable =
    match Verovio.NET.Internal.Interop.probeAvailability () with
    | Ok() -> true
    | Error _ -> false

/// Run when the host can hold the cap and the native library is present;
/// pending otherwise, so the report shows the leg did not run rather than
/// reading green over nothing.
let private gated (name: string) (body: unit -> unit) =
    if not capIsKernelEnforced then
        ptest
            $"{name} — PENDING: no kernel-enforced memory cap on this host (set TOOLUP_FUZZ_ALLOW_SOFT_CAP=1 to run under the sampler alone)" {
            body ()
        }
    elif not nativeAvailable then
        ptest $"{name} — PENDING: libverovio is not available on this host" { body () }
    else
        test name { body () }

/// Wrap raw bytes as a compressed MusicXML container, the shape
/// `LoadZipBuffer` reads: `META-INF/container.xml` naming the root file.
let asMxl (score: byte[]) : byte[] =
    use stream = new MemoryStream()

    (use archive = new ZipArchive(stream, ZipArchiveMode.Create, true)

     let container = archive.CreateEntry "META-INF/container.xml"

     (use writer = container.Open()

      let manifest =
          "<?xml version=\"1.0\" encoding=\"UTF-8\"?><container><rootfiles><rootfile full-path=\"score.xml\" media-type=\"application/vnd.recordare.musicxml+xml\"/></rootfiles></container>"

      writer.Write(Encoding.UTF8.GetBytes manifest))

     let entry = archive.CreateEntry "score.xml"
     use writer = entry.Open()
     writer.Write score)

    stream.ToArray()

/// Where the XXE cases would leak to if the parser resolved external
/// entities. Written before each case so a resolution would have real
/// content to leak; the child runs as the same user and sees the same
/// temp path.
let private plantLeakTarget () =
    File.WriteAllText(FuzzCorpus.hostileEntityFilePath, FuzzCorpus.leakMarker)

/// **The known-findings ledger.** A case recorded here is a defect the
/// corpus has already surfaced and that lives UPSTREAM (in libverovio) —
/// the seam contains it, and a consumer feeding uploads to the toolkit
/// behind the seam sees a typed refusal rather than a dead host. The
/// ledger turns each known crash from a standing red into a pinned
/// expectation, in BOTH directions, the way the public-API baselines
/// are pinned: a listed case that still crashes passes; one that now
/// ANSWERS goes red with the instruction to retire its entry, because a
/// ledger describing a defect that no longer exists is a false
/// statement about the parser; and any case NOT listed that crashes is
/// red as a NEW finding. Contained (cap / timeout) cases are reported,
/// not pinned: they are resource-exhaustion findings the bounds absorb,
/// and an improvement there needs no ceremony.
///
/// Measured 2026-09-17 against Verovio.NET 0.2.2 (libverovio 6.2.0):
///
///   * `malformed/chord-with-no-first-note` — `<note><chord/>…` with no
///     preceding note in the measure — native access violation
///     (0xC0000005) on the string and zip paths alike. A memory-safety
///     fault: the class this seam exists to contain, and the reason a
///     consumer must never run untrusted scores in-process.
let private knownCrashes = Set.ofList [ "malformed/chord-with-no-first-note" ]

/// The report row for one case on one path.
type Finding = {
    Case: string
    Path: string
    Outcome: string
    Elapsed: TimeSpan
}

/// The accumulated report — module-level and mutable on purpose: the
/// cases append to it as they run, and the final case prints it. A test
/// harness accumulator, not domain state.
let private findings = ResizeArray<Finding>()

let private record (row: Finding) =
    lock findings (fun () -> findings.Add row)

/// One case through one entry point, in its own child.
let private runCase<'Entry when 'Entry :> IIsolatedEntryPoint and 'Entry: (new: unit -> 'Entry)>
    (path: string)
    (fuzz: FuzzCorpus.FuzzCase)
    (input: byte[])
    =
    plantLeakTarget ()
    let isolation = ProcessIsolation.create limits
    let clock = Diagnostics.Stopwatch.StartNew()

    let outcome =
        CompanionIsolation.run<'Entry> isolation { Args = []; Input = input }
        |> Async.RunSynchronously

    clock.Stop()
    let name = $"{fuzz.Family}/{fuzz.Name}"

    let summary =
        match outcome with
        | Ok bytes ->
            let text = Encoding.UTF8.GetString bytes
            let firstLine = text.Split('\n', 2)[0]

            Expect.isFalse
                (text.Contains FuzzCorpus.leakMarker)
                $"{name} on the {path} path: the toolkit's export carried the external-entity target's content — a file read the parser must never perform"

            $"answered:{firstLine}"
        | Error(IsolationRefusal.EntryFailed _) -> "answered:refused"
        | Error(IsolationRefusal.MemoryCapExceeded(cap, observed)) -> $"contained:memory-cap ({observed} against {cap})"
        | Error(IsolationRefusal.TimedOut limit) -> $"contained:timed-out ({limit})"
        | Error(IsolationRefusal.WorkerCrashed(exitCode, diagnostic)) ->
            $"CRASHED (exit {exitCode}): {diagnostic.Trim()}"
        | Error(IsolationRefusal.ProtocolViolation detail) -> $"HARNESS-FAULT protocol: {detail}"
        | Error(IsolationRefusal.WorkerUnavailable reason) -> $"HARNESS-FAULT unavailable: {reason}"

    record {
        Case = name
        Path = path
        Outcome = summary
        Elapsed = clock.Elapsed
    }

    match outcome with
    | Error(IsolationRefusal.WorkerCrashed _) when knownCrashes.Contains name ->
        // Pinned: the known upstream fault still reproduces, contained.
        ()
    | Error(IsolationRefusal.WorkerCrashed _) ->
        failtestf
            "%s on the %s path: the native parser FAULTED in the worker — a NEW finding, not in the known-findings ledger — %s"
            name
            path
            summary
    | Error(IsolationRefusal.ProtocolViolation _)
    | Error(IsolationRefusal.WorkerUnavailable _) ->
        failtestf "%s on the %s path: the harness measured nothing — %s" name path summary
    | _ when knownCrashes.Contains name ->
        failtestf
            "%s on the %s path no longer crashes (%s): the known finding does not reproduce against this toolkit — retire it from knownCrashes in the same change that adopts the fixed toolkit"
            name
            path
            summary
    | _ -> ()

/// The entity-expansion / external-entity cases: the ones that took the
/// machine in-process. Last.
let private isEntityCase (fuzz: FuzzCorpus.FuzzCase) =
    fuzz.Name.Contains "entity" || fuzz.Name.Contains "dtd"

let private ordered =
    let entity, rest = FuzzCorpus.all |> List.partition isEntityCase
    rest @ entity

let private caseTests = [
    for fuzz in ordered do
        gated $"{fuzz.Family}/{fuzz.Name}: string path survives" (fun () ->
            runCase<MusicXmlStringEntry> "string" fuzz fuzz.Input)

        gated $"{fuzz.Family}/{fuzz.Name}: zip path survives" (fun () ->
            runCase<MusicXmlZipEntry> "zip" fuzz (asMxl fuzz.Input))
]

let private corpusShapeTests =
    testList "corpus shape" [
        test "every family is represented and every case name is unique" {
            let families = FuzzCorpus.all |> List.map _.Family |> List.distinct |> List.sort
            Expect.equal families [ "hostile"; "malformed"; "oversized"; "truncated" ] "families"

            Expect.equal
                (FuzzCorpus.all
                 |> List.map (fun c -> c.Family + "/" + c.Name)
                 |> List.distinct
                 |> List.length)
                FuzzCorpus.all.Length
                "names are unique"

            Expect.isGreaterThan FuzzCorpus.all.Length 80 "a corpus, not a handful"
        }

        test "the entity cases are ordered last" {
            let names = ordered |> List.map _.Name

            let firstEntity =
                names |> List.findIndex (fun n -> n.Contains "entity" || n.Contains "dtd")

            let lastOther =
                names
                |> List.findIndexBack (fun n -> not (n.Contains "entity" || n.Contains "dtd"))

            Expect.isGreaterThan firstEntity lastOther "every entity case follows every other case"
        }

        test "the zip container the raw-bytes leg feeds is a real MXL" {
            let mxl = asMxl (Encoding.UTF8.GetBytes FuzzCorpus.validMusicXml)
            use archive = new ZipArchive(new MemoryStream(mxl), ZipArchiveMode.Read)
            Expect.isNotNull (archive.GetEntry "META-INF/container.xml") "container manifest"
            Expect.isNotNull (archive.GetEntry "score.xml") "root file"
        }

        gated
            "the baseline score renders through the seam (so the corpus mutates something real, and the seam works)"
            (fun () ->
                let outcome =
                    CompanionIsolation.run<MusicXmlStringEntry> (ProcessIsolation.create limits) {
                        Args = []
                        Input = Encoding.UTF8.GetBytes FuzzCorpus.validMusicXml
                    }
                    |> Async.RunSynchronously

                match outcome with
                | Ok bytes ->
                    let text = Encoding.UTF8.GetString bytes
                    Expect.stringStarts text "rendered\n" "the baseline rendered"
                    Expect.stringContains text "<svg" "the payload carries the SVG"
                | other -> failtestf "the baseline did not render through the seam: %A" other)
    ]

/// Printed last: every row, then the contained findings on their own,
/// because those are what this leg exists to surface.
let private reportTests =
    testList "report" [
        test "findings" {
            let rows = lock findings (fun () -> List.ofSeq findings)

            let line (row: Finding) =
                $"  {row.Case} [{row.Path}] {row.Elapsed.TotalSeconds:F2}s  {row.Outcome}"

            printfn "Fuzz corpus report — %d rows" rows.Length

            for row in rows do
                printfn "%s" (line row)

            let contained = rows |> List.filter (fun r -> r.Outcome.StartsWith "contained:")

            printfn "Contained (resource-exhaustion findings) — %d" contained.Length

            for row in contained do
                printfn "%s" (line row)

            let crashed = rows |> List.filter (fun r -> r.Outcome.StartsWith "CRASHED")

            printfn "Crashed (native faults, all in the known-findings ledger) — %d" crashed.Length

            for row in crashed do
                printfn "%s" (line row)

            let unknown = crashed |> List.filter (fun r -> not (knownCrashes.Contains r.Case))

            Expect.isEmpty unknown "no case outside the known-findings ledger faulted the worker"
        }
    ]

/// Sequenced regardless of the runner's flags: one child at a time is
/// what makes the memory measurement honest, and what keeps the machine's
/// exposure to one capped child rather than N.
let tests =
    testSequenced (
        testList "Phase 687 — native-parser fuzz corpus (Verovio / MusicXML), through the seam" [
            corpusShapeTests
            testList "cases" caseTests
            reportTests
        ]
    )