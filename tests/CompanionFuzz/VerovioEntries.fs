// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 687 — the Verovio entry points the corpus runs THROUGH THE SEAM.
/// Each is one `IIsolatedEntryPoint` the isolation worker instantiates by
/// name inside a capped child; the toolkit, the parse, the render and the
/// MEI export all happen there, and nothing here runs in the test runner.
///
/// The answer is a small UTF-8 payload the host reads: line 1 is a
/// one-word summary (`refused` / `rendered` / `loaded-but-not-rendered`),
/// and everything after it is whatever text the toolkit EXPORTED — the
/// SVG and the MEI — so the host can check that an external-entity case
/// leaked nothing into it. A toolkit that refuses cleanly is an
/// `Error` here and an `EntryFailed` on the host; the `Ok` payload is
/// the parser's own output, verbatim.
namespace ToolUp.Companions.Fuzz.Tests.Entries

open System.Text
open ToolUp.Companions.Isolation
open Verovio.NET

/// Load, and if it loaded, render and export — each a native call that
/// must return inside the child. Shared by both entry points.
module private Exercise =
    let run (load: Toolkit -> Result<unit, LoadError>) : Result<byte[], string> =
        use toolkit = Toolkit.Create()

        match load toolkit with
        | Error err -> Error $"refused: {err}"
        | Ok() ->
            let payload = StringBuilder()

            let summary =
                match toolkit.RenderToSvg 1 with
                | Ok svg ->
                    payload.Append(svg) |> ignore
                    "rendered"
                | Error _ -> "loaded-but-not-rendered"

            match toolkit.GetMei() with
            | Ok mei -> payload.Append(mei) |> ignore
            | Error _ -> ()

            Ok(Encoding.UTF8.GetBytes(summary + "\n" + payload.ToString()))

/// The string path — `LoadData` with the MusicXML input format — which is
/// what a pasted or uploaded score takes.
type MusicXmlStringEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke request =
            let text = Encoding.UTF8.GetString request.Input
            let options = LoadOptions.Create InputFormat.MusicXML
            Exercise.run (fun toolkit -> toolkit.LoadData(text, options))

/// The raw-bytes path — `LoadZipBuffer` — which a compressed `.mxl`
/// upload takes, exercising the native zip reader as well as the parser.
/// The host wraps each case as a real MXL container before sending it.
type MusicXmlZipEntry() =
    interface IIsolatedEntryPoint with
        member _.Invoke request =
            Exercise.run (fun toolkit -> toolkit.LoadZipBuffer request.Input)