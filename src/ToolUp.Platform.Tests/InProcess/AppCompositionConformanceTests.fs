// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AppCompositionConformanceTests

// ─── forge's pinned core vectors, held to their home ─────────────────
//
// `spec/fixtures/core/` carries copies of the App-composition wire
// specification's vectors that certify forge's composition profile (§P8),
// pinned at the commit `spec/fixtures/manifest.json` records. The profile
// pack (`CompositionProfileSurfaceTests`) holds each copy to the digest the
// manifest records beside it. This pack holds the copies and the digests to
// the SPECIFICATION, read at that commit:
//
//   1. every pinned copy is enumerated by the specification at the pin,
//      under the same family and digest, and is byte-identical to the file
//      there;
//   2. every vector the specification scopes to `server-app-host` at the
//      pin is pinned here — a pin that silently drops one certifies a
//      smaller declaration than the profile makes.
//
// What this pack does NOT do is compose or refuse a composition. Refusing a
// composition by class is the obligation of an implementation that reads
// compositions (profile §P6), and this repository contains none: forge's
// builders take typed values. See docs/platform/composition-conformance.md.
//
// Every check is a pure function of the two manifests and two byte readers,
// so the self-tests at the bottom plant a defect and watch it go red,
// naming the vector.

open System
open System.IO
open System.Security.Cryptography
open Expecto
open ToolUp.Platform.Tests.InProcess.AppCompositionCorpus

/// The disagreements between forge's pin and the specification at the pin.
/// `readHome` takes a path relative to the specification's `wire-fixtures/`;
/// `readPinned` one relative to forge's `spec/fixtures/`.
let provenanceFindings
    (pinned: VectorEntry list)
    (upstream: VectorEntry list)
    (readHome: string -> Result<byte array, string>)
    (readPinned: string -> byte array option)
    : string list =
    let byId = upstream |> List.map (fun u -> u.Id, u) |> Map.ofList
    let pinnedIds = pinned |> List.map _.Id |> Set.ofList

    [
        for p in pinned do
            match Map.tryFind p.Id byId with
            | None -> yield sprintf "%s: pinned here, but the specification does not enumerate it at the pin" p.Id
            | Some u ->
                if u.Family <> p.Family then
                    yield sprintf "%s: pinned under family '%s', enumerated under '%s'" p.Id p.Family u.Family

                if not (String.Equals(u.Sha256, p.Sha256, StringComparison.OrdinalIgnoreCase)) then
                    yield sprintf "%s: the recorded digest %s is not the specification's %s" p.Id p.Sha256 u.Sha256

                match readHome u.File, readPinned p.File with
                | Error why, _ ->
                    yield sprintf "%s: the specification's file %s is unreadable at the pin: %s" p.Id u.File why
                | _, None -> yield sprintf "%s: the copy %s is missing" p.Id p.File
                | Ok home, Some copy when home <> copy ->
                    yield sprintf "%s: the copy %s is not byte-identical to its home %s" p.Id p.File u.File
                | Ok _, Some _ -> ()

        for u in upstream do
            if u.Profile = Some Profile && not (pinnedIds.Contains u.Id) then
                yield sprintf "%s: the specification scopes it to %s, and it is not pinned here" u.Id Profile
    ]

/// The run against the real pin and the real specification home.
type private Run = {
    Pin: Pin
    Upstream: VectorEntry list
    Findings: string list
}

let private run =
    lazy
        (let pin = loadPin ()

         match specHome () with
         | None -> Error(missingSpecHomeMessage ())
         | Some home when not (commitPresent home pin.Commit) ->
             Error(
                 sprintf
                     "The specification home %s does not hold the pinned commit %s. Fetch it (git -C \"%s\" fetch origin %s) and re-run."
                     home
                     pin.Commit
                     home
                     pin.Commit
             )
         | Some home ->
             upstreamVectors home pin.Commit
             |> Result.map (fun upstream ->
                 let readHome file =
                     blobAt home pin.Commit ("wire-fixtures/" + file)

                 let readPinned (file: string) =
                     let path = Path.Combine(pin.FixturesDir, file)

                     if File.Exists path then
                         Some(File.ReadAllBytes path)
                     else
                         None

                 {
                     Pin = pin
                     Upstream = upstream
                     Findings = provenanceFindings pin.Pinned upstream readHome readPinned
                 }))

let private resolved () =
    match run.Value with
    | Error why -> failtest why
    | Ok r -> r

// ─── Self-tests: a planted defect goes red, naming the vector ────────

let private sha (bytes: byte array) =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

let private entry id profile (bytes: byte array) = {
    Id = id
    Family = "kill-and-replay"
    Profile = profile
    File = id + ".json"
    Sha256 = sha bytes
}

let private a = "{\"a\":1}"B
let private b = "{\"b\":2}"B

let private home =
    Map.ofList [ "kill-and-replay/one.json", a; "kill-and-replay/two.json", b ]

let private readHomeOf (m: Map<string, byte array>) file =
    match Map.tryFind file m with
    | Some bytes -> Ok bytes
    | None -> Error "absent"

let private upstreamOf () = [
    entry "kill-and-replay/one" (Some Profile) a
    entry "kill-and-replay/two" (Some Profile) b
]

let private names (id: string) (findings: string list) =
    Expect.isNonEmpty findings $"a planted defect in '{id}' must be found"

    Expect.all findings (fun f -> f.StartsWith(id + ":", StringComparison.Ordinal)) $"every finding must name '{id}'"

let private selfTests =
    testList "self-tests" [
        test "a faithful pin has no findings" {
            let copies = home

            provenanceFindings (upstreamOf ()) (upstreamOf ()) (readHomeOf home) (fun f -> Map.tryFind f copies)
            |> fun findings -> Expect.isEmpty findings "a faithful pin must be clean"
        }

        test "a copy one byte away from its home is found, by vector" {
            let corrupt = Array.copy a
            corrupt[2] <- byte 'z'
            let copies = home |> Map.add "kill-and-replay/one.json" corrupt

            provenanceFindings (upstreamOf ()) (upstreamOf ()) (readHomeOf home) (fun f -> Map.tryFind f copies)
            |> names "kill-and-replay/one"
        }

        test "a recorded digest that is not the home's is found, by vector" {
            let pinned =
                upstreamOf ()
                |> List.map (fun e ->
                    if e.Id = "kill-and-replay/two" then
                        { e with Sha256 = sha "{}"B }
                    else
                        e)

            provenanceFindings pinned (upstreamOf ()) (readHomeOf home) (fun f -> Map.tryFind f home)
            |> names "kill-and-replay/two"
        }

        test "a scoped vector the pin dropped is found, by vector" {
            let pinned = upstreamOf () |> List.filter (fun e -> e.Id <> "kill-and-replay/two")

            provenanceFindings pinned (upstreamOf ()) (readHomeOf home) (fun f -> Map.tryFind f home)
            |> names "kill-and-replay/two"
        }

        test "a pinned vector its home no longer enumerates is found, by vector" {
            let pinned = upstreamOf () @ [ entry "kill-and-replay/three" None a ]

            provenanceFindings pinned (upstreamOf ()) (readHomeOf home) (fun f -> Map.tryFind f home)
            |> names "kill-and-replay/three"
        }
    ]

let tests =
    testList "App-composition core pin — forge's copies against the specification at the pin" [
        test "the specification home is present and holds the pinned commit" {
            let r = resolved ()
            Expect.isNonEmpty r.Upstream "the specification enumerates no vectors at the pin"
            Expect.isNonEmpty r.Pin.Pinned "forge pins no core vectors; there is nothing to certify"
        }

        test "every pinned copy is its home's vector at the pin, and every vector scoped to the profile is pinned" {
            let r = resolved ()
            let failing = r.Findings.Length

            let scoped =
                r.Upstream |> List.filter (fun u -> u.Profile = Some Profile) |> List.length

            // The count the run executed, against the count the manifest
            // enumerates: a harness that read nothing would otherwise pass.
            printfn
                "app-composition core pin %s: %d pinned, %d scoped to %s by the specification; %d finding(s)"
                (r.Pin.Commit.Substring(0, 7))
                r.Pin.Pinned.Length
                scoped
                Profile
                failing

            if failing > 0 then
                failtestf
                    "%d disagreement(s) with the specification at %s:\n  %s"
                    failing
                    r.Pin.Commit
                    (String.concat "\n  " r.Findings)
        }

        selfTests
    ]