// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Media.FFmpeg.FFmpegMediaProvider

open System
open System.IO
open System.Diagnostics
open ToolUp.MediaLibrary

// ─── Phase 88 — FFmpeg media derivation + transcode sub-companion ─────
//
// Opt-in `IMediaDerivation` + `IMediaTranscoder` backed by the system
// `ffmpeg` / `ffprobe` binaries (GP 1 — the heavy media dependency lives
// here, never in the core media library). The default media library
// declares no derivation capability and works with zero transcode deps;
// installing this companion lights up poster-frame extraction, duration
// / dimension probing, and HLS rendition production.
//
// **Runtime requirement.** `ffmpeg` and `ffprobe` must be on `PATH` (or
// supplied as absolute paths via `create`/`createTranscoder`). This is a
// process-shelling companion — it ships no vendor NuGet dependency and
// no bundled binary, so a deployment that installs it is opting into
// providing FFmpeg itself. Stateless between calls (GP 12 rule 4): every
// invocation writes the input to a fresh temp file and cleans up.

[<Literal>]
let private defaultFfmpeg = "ffmpeg"

[<Literal>]
let private defaultFfprobe = "ffprobe"

let private extForMime (mimeType: string) =
    match mimeType.ToLowerInvariant() with
    | "video/mp4"
    | "video/quicktime" -> ".mp4"
    | "video/webm" -> ".webm"
    | "video/ogg" -> ".ogv"
    | "audio/mpeg" -> ".mp3"
    | "audio/mp4" -> ".m4a"
    | "audio/ogg" -> ".ogg"
    | "audio/wav" -> ".wav"
    | _ -> ".bin"

/// Run an external process with an explicit argument list (no shell
/// quoting hazards). Returns captured stdout on success, the exit code +
/// stderr tail on failure, or the launch exception.
let private run (exe: string) (args: string list) : Result<string, string> =
    try
        let psi = ProcessStartInfo(exe)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        args |> List.iter psi.ArgumentList.Add
        use p = new Process()
        p.StartInfo <- psi
        p.Start() |> ignore
        let stdout = p.StandardOutput.ReadToEnd()
        let stderr = p.StandardError.ReadToEnd()
        p.WaitForExit()

        if p.ExitCode = 0 then
            Ok stdout
        else
            let tail =
                if stderr.Length > 600 then
                    stderr.Substring(stderr.Length - 600)
                else
                    stderr

            Error(sprintf "%s exited %d: %s" exe p.ExitCode tail)
    with ex ->
        Error(sprintf "could not launch %s (is it on PATH?): %s" exe ex.Message)

/// Write bytes to a fresh temp file, run `f` over the path, always clean up.
let private withTempInput (mimeType: string) (bytes: byte[]) (f: string -> 'a) : 'a =
    let path =
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extForMime mimeType)

    File.WriteAllBytes(path, bytes)

    try
        f path
    finally
        try
            File.Delete path
        with _ ->
            ()

let private tryParseInt (s: string) =
    match Int32.TryParse(s.Trim()) with
    | true, v -> Some v
    | _ -> None

let private tryParseFloat (s: string) =
    match Double.TryParse(s.Trim(), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | _ -> None

/// FFmpeg-backed poster + probe provider.
let create (ffmpegPath: string option) (ffprobePath: string option) : IMediaDerivation =
    let ffmpeg = ffmpegPath |> Option.defaultValue defaultFfmpeg
    let ffprobe = ffprobePath |> Option.defaultValue defaultFfprobe

    { new IMediaDerivation with
        member _.Capabilities = {
            CanExtractPoster = true
            CanTranscodeHls = false
        }

        member _.Probe(bytes, mimeType) = async {
            return
                withTempInput mimeType bytes (fun input ->
                    // Duration from the container; width/height from the
                    // first video stream. Best-effort — parse failures
                    // collapse to None.
                    let duration =
                        match
                            run ffprobe [
                                "-v"
                                "quiet"
                                "-show_entries"
                                "format=duration"
                                "-of"
                                "default=noprint_wrappers=1:nokey=1"
                                input
                            ]
                        with
                        | Ok out -> tryParseFloat out
                        | Error _ -> None

                    let width, height =
                        match
                            run ffprobe [
                                "-v"
                                "quiet"
                                "-select_streams"
                                "v:0"
                                "-show_entries"
                                "stream=width,height"
                                "-of"
                                "csv=p=0"
                                input
                            ]
                        with
                        | Ok out ->
                            match out.Trim().Split(',') with
                            | [| w; h |] -> tryParseInt w, tryParseInt h
                            | _ -> None, None
                        | Error _ -> None, None

                    {
                        DurationSeconds = duration
                        Width = width
                        Height = height
                    })
        }

        member _.ExtractPoster(bytes, mimeType) = async {
            return
                withTempInput mimeType bytes (fun input ->
                    let output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jpg")

                    try
                        match run ffmpeg [ "-y"; "-i"; input; "-frames:v"; "1"; "-q:v"; "3"; output ] with
                        | Error e -> Error e
                        | Ok _ ->
                            if File.Exists output then
                                Ok {
                                    Bytes = File.ReadAllBytes output
                                    MimeType = "image/jpeg"
                                    Width = None
                                    Height = None
                                }
                            else
                                Error "ffmpeg produced no poster frame"
                    finally
                        try
                            File.Delete output
                        with _ ->
                            ())
        }
    }

/// FFmpeg-backed HLS transcoder. Produces a single-rendition HLS package
/// (master `.m3u8` + `.ts` segments) by stream-copying the source. A
/// production deployment would extend the ladder; this keeps the
/// reference implementation dependency-light.
let createTranscoder (ffmpegPath: string option) : IMediaTranscoder =
    let ffmpeg = ffmpegPath |> Option.defaultValue defaultFfmpeg

    { new IMediaTranscoder with
        member _.Capabilities = {
            CanExtractPoster = false
            CanTranscodeHls = true
        }

        member _.TranscodeToHls(bytes, mimeType) = async {
            return
                withTempInput mimeType bytes (fun input ->
                    let outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
                    Directory.CreateDirectory outDir |> ignore
                    let manifest = Path.Combine(outDir, "master.m3u8")

                    try
                        match
                            run ffmpeg [
                                "-y"
                                "-i"
                                input
                                "-codec:"
                                "copy"
                                "-start_number"
                                "0"
                                "-hls_time"
                                "6"
                                "-hls_list_size"
                                "0"
                                "-f"
                                "hls"
                                manifest
                            ]
                        with
                        | Error e -> Error e
                        | Ok _ ->
                            let files =
                                Directory.GetFiles outDir
                                |> Array.toList
                                |> List.map (fun f ->
                                    let name = Path.GetFileName f
                                    let isMaster = name.EndsWith ".m3u8"

                                    {
                                        BlobSuffix = "hls/" + name
                                        Bytes = File.ReadAllBytes f
                                        MimeType =
                                            if isMaster then
                                                "application/vnd.apple.mpegurl"
                                            else
                                                "video/mp2t"
                                        RenditionName = "hls"
                                        IsMasterManifest = isMaster
                                    })

                            if List.isEmpty files then
                                Error "ffmpeg produced no HLS output"
                            else
                                Ok files
                    finally
                        try
                            Directory.Delete(outDir, true)
                        with _ ->
                            ())
        }
    }