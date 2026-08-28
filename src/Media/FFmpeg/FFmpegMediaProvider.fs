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

// ─── Phase 471 — AES-128 encrypted HLS ────────────────────────────────
//
// FFmpeg encrypts HLS segments through `-hls_key_info_file`, which
// points at a small text file of two or three lines:
//
//     <key URI>          written verbatim into the manifest's #EXT-X-KEY
//     <key file path>    ffmpeg reads the raw 16 key bytes from here
//     <IV as hex>        optional; omitted → per-segment IV from the
//                        media sequence number
//
// Two consequences shape everything below.
//
// **The key is on disk while ffmpeg runs**, in the key file and (as a
// path) in the info file. Both live in a temp directory of their own —
// NOT the HLS output directory — for one blunt reason: the output
// directory is enumerated wholesale and every file in it is uploaded to
// blob storage. A key file placed there would be persisted beside the
// ciphertext it opens, which is precisely the arrangement this phase
// exists to prevent (GP 4). The separate directory is deleted in a
// `finally`, and the produced-file list is ALSO filtered by extension,
// so the invariant does not rest on one mechanism.
//
// **The manifest carries the URI, not the key.** Line 1 is what ffmpeg
// writes into `#EXT-X-KEY`; the library supplies the origin's gated key
// endpoint, and the serve path rewrites it to origin-absolute form.

/// Extensions an HLS pass is allowed to emit into blob storage. A
/// belt-and-braces guard beside the separate key directory: even if a
/// future ffmpeg flag dropped key material into the output directory,
/// it would not be uploaded.
let private hlsOutputExtensions = set [ ".m3u8"; ".ts"; ".m4s"; ".mp4"; ".vtt" ]

/// The `-hls_key_info_file` payload. Pure, so its exact shape — which
/// is a positional file format with no keys and no error reporting — is
/// unit-testable without an ffmpeg binary present.
let keyInfoContent (keyUri: string) (keyFilePath: string) (iv: byte[] option) : string =
    let lines = [
        yield keyUri
        yield keyFilePath

        match iv with
        | Some bytes -> yield Convert.ToHexString(bytes).ToLowerInvariant()
        | None -> ()
    ]

    String.Join("\n", lines) + "\n"

/// The ffmpeg argument list for one HLS pass. Pure, and shared by the
/// encrypted and unencrypted paths so the two cannot drift in anything
/// but the encryption flag — which is the whole claim behind "the
/// unencrypted path is unchanged" (GP 11).
let hlsArgs (input: string) (manifest: string) (keyInfoFile: string option) : string list = [
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

    match keyInfoFile with
    | Some path ->
        "-hls_key_info_file"
        path
    | None -> ()

    "-f"
    "hls"
    manifest
]

/// Collect the produced HLS files, filtered to the extensions an HLS
/// package legitimately contains (see `hlsOutputExtensions`).
let private collectHlsOutput (outDir: string) : Result<TranscodedFile list, string> =
    let files =
        Directory.GetFiles outDir
        |> Array.toList
        |> List.filter (fun f -> hlsOutputExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
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

/// Run one HLS pass. `key` present ⇒ segments are AES-128 encrypted and
/// the manifest carries `#EXT-X-KEY:METHOD=AES-128,URI="…"`.
let private runHlsPass
    (ffmpeg: string)
    (mimeType: string)
    (bytes: byte[])
    (key: HlsEncryptionKey option)
    : Result<TranscodedFile list, string> =
    withTempInput mimeType bytes (fun input ->
        let outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory outDir |> ignore
        let manifest = Path.Combine(outDir, "master.m3u8")

        // Key material lives in its own directory, never in `outDir` —
        // see the section header. `None` when this is a plain pass, so
        // the unencrypted path creates no extra directory at all.
        let keyDir =
            key
            |> Option.map (fun _ ->
                let d = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
                Directory.CreateDirectory d |> ignore
                d)

        try
            let keyInfoFile =
                match key, keyDir with
                | Some k, Some dir ->
                    let keyFile = Path.Combine(dir, "hls.key")
                    let infoFile = Path.Combine(dir, "hls.keyinfo")
                    File.WriteAllBytes(keyFile, k.KeyBytes)
                    File.WriteAllText(infoFile, keyInfoContent k.KeyUri keyFile k.Iv)
                    Some infoFile
                | _ -> None

            match run ffmpeg (hlsArgs input manifest keyInfoFile) with
            | Error e -> Error e
            | Ok _ -> collectHlsOutput outDir
        finally
            // The key directory goes first and unconditionally: an
            // ffmpeg failure must not be the reason key material
            // outlives the pass.
            match keyDir with
            | Some dir ->
                try
                    Directory.Delete(dir, true)
                with _ ->
                    ()
            | None -> ()

            try
                Directory.Delete(outDir, true)
            with _ ->
                ())

/// FFmpeg-backed HLS transcoder. Produces a single-rendition HLS package
/// (master `.m3u8` + `.ts` segments) by stream-copying the source. A
/// production deployment would extend the ladder; this keeps the
/// reference implementation dependency-light.
///
/// Phase 471 — also declares `IMediaHlsEncryptingTranscoder`, so a
/// deployment that opts into encrypted HLS gets it from the same
/// sub-companion. The plain `TranscodeToHls` path is byte-for-byte what
/// it was: same argument list, no key directory created, nothing to
/// clean up (GP 11).
let createTranscoder (ffmpegPath: string option) : IMediaTranscoder =
    let ffmpeg = ffmpegPath |> Option.defaultValue defaultFfmpeg

    { new IMediaTranscoder with
        member _.Capabilities = {
            CanExtractPoster = false
            CanTranscodeHls = true
        }

        member _.TranscodeToHls(bytes, mimeType) = async { return runHlsPass ffmpeg mimeType bytes None }

      interface IMediaHlsEncryptingTranscoder with
          member _.TranscodeToHlsEncrypted(bytes, mimeType, key) = async {
              if isNull (box key.KeyBytes) || key.KeyBytes.Length <> 16 then
                  return Error "an AES-128 HLS key must be exactly 16 bytes"
              else
                  return runHlsPass ffmpeg mimeType bytes (Some key)
          }
    }