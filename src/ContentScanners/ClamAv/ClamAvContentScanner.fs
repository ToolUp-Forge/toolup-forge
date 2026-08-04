// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ContentScanners.ClamAv.ClamAvContentScanner

open System
open System.Net.Sockets
open System.Text
open System.Threading
open ToolUp.Platform

// ─── Phase 515 — ClamAV IContentScanner companion ────────────────────
//
// Production-ready. Stateless between `Scan` calls (GP 12 rule 4): every
// scan opens its own short-lived TCP connection to clamd and closes it,
// so the scanner is safe as a DI singleton shared by every request
// thread and safe across N replicas pointed at one daemon.
//
// **Zero dependencies, deliberately.** clamd's INSTREAM protocol is a
// command byte-string, length-prefixed chunks, and a NUL-terminated
// one-line reply. That is small enough to implement against `TcpClient`
// exactly, and doing so keeps this companion's dependency count at ZERO
// — no NuGet client package, nothing to CVE-track, nothing to reach
// `ToolUp.Platform.*` (GP 1). A binding layer here would be more code
// than the protocol.
//
// **The wire protocol, written down** so the next reader does not have
// to find it in clamd's man page:
//
//   → "zINSTREAM\0"          the `z` prefix selects NUL-terminated
//                            commands (the `n` prefix selects newline);
//                            mixing the two is what most hand-rolled
//                            clients get wrong.
//   → <uint32 BE length><chunk bytes>   repeated
//   → <uint32 BE 0>          end of stream
//   ← "stream: OK\0"                              clean
//   ← "stream: Eicar-Test-Signature FOUND\0"      a detection
//   ← "...ERROR\0"                                clamd could not scan
//
// **Verdict mapping is conservative.** Only an explicit `OK` is
// `ScanClean`; only an explicit `FOUND` is `ScanRejected`. Everything
// else — an `ERROR` reply, a truncated reply, a refused connection, a
// timeout, `StreamMaxLength` exceeded — is `ScanUnavailable`, because
// the honest statement is "this payload was not scanned", and the
// deployment's `ContentScanPolicy.OnScanError` decides what that means.
// Mapping an unreachable daemon to `ScanClean` here would silently
// convert every deployment to fail-open regardless of its policy.
//
// **No exception escapes `Scan`.** The seam's contract is a verdict, so
// socket failures are caught and reported as `ScanUnavailable` with the
// exception message as the reason.

/// Connection + framing tuning for the clamd client.
type ClamAvOptions = {
    /// Host running clamd. No default — an implicit `localhost` is how a
    /// deployment ends up scanning nothing in production while looking
    /// composed.
    Host: string
    /// clamd TCP port. 3310 is the daemon's default.
    Port: int
    /// Bytes per INSTREAM chunk. clamd accepts any size up to its
    /// `StreamMaxLength`; 64 KiB keeps the write loop's allocations flat
    /// without a syscall per kilobyte.
    ChunkBytes: int
    /// Wallclock ceiling for one complete scan — connect, stream, reply.
    /// On expiry the scan reports `ScanUnavailable`, never a partial
    /// verdict.
    Timeout: TimeSpan
}

[<RequireQualifiedAccess>]
module ClamAvOptions =

    /// clamd's own default port, a 64 KiB frame, and a 30s ceiling —
    /// generous enough for a multi-megabyte upload over a loopback or
    /// in-cluster hop, tight enough that a wedged daemon does not pin a
    /// request thread for minutes.
    let create (host: string) : ClamAvOptions = {
        Host = host
        Port = 3310
        ChunkBytes = 64 * 1024
        Timeout = TimeSpan.FromSeconds 30.0
    }

    let withPort (port: int) (options: ClamAvOptions) : ClamAvOptions = { options with Port = port }

    let withTimeout (timeout: TimeSpan) (options: ClamAvOptions) : ClamAvOptions = { options with Timeout = timeout }

    let withChunkBytes (chunkBytes: int) (options: ClamAvOptions) : ClamAvOptions = {
        options with
            ChunkBytes = chunkBytes
    }

// ─── Protocol primitives ─────────────────────────────────────────────

/// Big-endian uint32 length prefix, as INSTREAM frames require.
/// `BitConverter.GetBytes` is little-endian on every RID this ships on,
/// so the reverse is not optional and not a no-op.
let lengthPrefix (n: int) : byte[] =
    let raw = BitConverter.GetBytes(uint32 n)

    if BitConverter.IsLittleEndian then Array.rev raw else raw

/// Parse one clamd reply line into a verdict.
///
/// Public because it is the part of this companion that is worth
/// testing without a daemon: the reply grammar is where a hand-rolled
/// client goes wrong, and it is pure.
let parseReply (reply: string) : ScanVerdict =
    let trimmed = reply.Trim([| '\000'; '\n'; '\r'; ' ' |])

    if String.IsNullOrWhiteSpace trimmed then
        ScanUnavailable "clamd returned an empty reply"
    elif trimmed.EndsWith("OK", StringComparison.Ordinal) then
        ScanClean
    elif trimmed.EndsWith("FOUND", StringComparison.Ordinal) then
        // "stream: <SIGNATURE> FOUND" — the signature is what an
        // operator needs in the audit row and the refusal message.
        let signature =
            let body = trimmed.Substring(0, trimmed.Length - "FOUND".Length).Trim()

            match body.LastIndexOf(':') with
            | -1 -> body
            | i -> body.Substring(i + 1).Trim()

        let named =
            if String.IsNullOrWhiteSpace signature then
                "unnamed signature"
            else
                signature

        ScanRejected(sprintf "ClamAV detected %s" named)
    elif trimmed.EndsWith("ERROR", StringComparison.Ordinal) then
        ScanUnavailable(sprintf "clamd reported an error: %s" trimmed)
    else
        ScanUnavailable(sprintf "unrecognised clamd reply: %s" trimmed)

/// Send one NUL-terminated command over a fresh connection and read the
/// NUL-terminated reply. Shared by INSTREAM and by the health probe's
/// PING, so both agree on framing.
let roundTrip
    (options: ClamAvOptions)
    (command: string)
    (writeBody: NetworkStream -> CancellationToken -> Async<unit>)
    : Async<string> =
    async {
        use cts = new CancellationTokenSource(options.Timeout)
        let ct = cts.Token

        use client = new TcpClient()
        do! client.ConnectAsync(options.Host, options.Port, ct).AsTask() |> Async.AwaitTask

        use stream = client.GetStream()

        let commandBytes = Encoding.ASCII.GetBytes(command + "\000")
        do! stream.WriteAsync(ReadOnlyMemory commandBytes, ct).AsTask() |> Async.AwaitTask

        do! writeBody stream ct
        do! stream.FlushAsync ct |> Async.AwaitTask

        // clamd answers with a single NUL-terminated line and then
        // closes, so read to end-of-stream rather than guessing a length.
        let buffer = Array.zeroCreate<byte> 512
        let received = StringBuilder()
        let mutable reading = true

        while reading do
            let! read = stream.ReadAsync(Memory buffer, ct).AsTask() |> Async.AwaitTask

            if read <= 0 then
                reading <- false
            else
                received.Append(Encoding.ASCII.GetString(buffer, 0, read)) |> ignore

                if Array.exists ((=) 0uy) (Array.sub buffer 0 read) then
                    reading <- false

        return received.ToString()
    }

/// Stream `bytes` as INSTREAM frames, then the zero-length terminator.
let writeInstream (options: ClamAvOptions) (bytes: byte[]) =
    fun (stream: NetworkStream) (ct: CancellationToken) -> async {
        let chunk = max 1024 options.ChunkBytes
        let mutable offset = 0

        while offset < bytes.Length do
            let size = min chunk (bytes.Length - offset)

            do!
                stream.WriteAsync(ReadOnlyMemory(lengthPrefix size), ct).AsTask()
                |> Async.AwaitTask

            do!
                stream.WriteAsync(ReadOnlyMemory(bytes, offset, size), ct).AsTask()
                |> Async.AwaitTask

            offset <- offset + size

        // Zero-length frame terminates the stream.
        do!
            stream.WriteAsync(ReadOnlyMemory(lengthPrefix 0), ct).AsTask()
            |> Async.AwaitTask
    }

// ─── The scanner ─────────────────────────────────────────────────────

/// `IContentScanner` over a clamd endpoint, spoken directly on its
/// INSTREAM protocol.
///
/// Construct via `ClamAvContentScanner.create` (or this constructor) and
/// compose it with `KnowledgeBase.Server.withContentScanning`. A
/// deployment that never composes it keeps the pre-515 upload path
/// byte-for-byte (GP 13).
type ClamAvContentScanner(options: ClamAvOptions) =

    do
        if String.IsNullOrWhiteSpace options.Host then
            invalidArg
                "options"
                "ClamAvContentScanner requires an explicit clamd host — an implicit localhost is how a deployment ends up scanning nothing while looking composed."

    member _.Options = options

    /// PING/PONG against the configured daemon. Used by the health probe
    /// and useful directly in a preflight.
    member _.Ping() : Async<Result<unit, string>> = async {
        try
            let! reply = roundTrip options "zPING" (fun _ _ -> async { return () })

            if reply.Contains "PONG" then
                return Ok()
            else
                return Error(sprintf "clamd did not answer PING (got '%s')" (reply.Trim()))
        with ex ->
            return Error(sprintf "clamd unreachable at %s:%d — %s" options.Host options.Port ex.Message)
    }

    interface IContentScanner with
        member _.Name = "clamav"

        member _.Scan(bytes, _fileName) = async {
            // The file name is deliberately unused: clamd inspects the
            // payload, and a scanner that shortcut on extension would be
            // trivially defeated by renaming.
            try
                let! reply = roundTrip options "zINSTREAM" (writeInstream options bytes)
                return parseReply reply
            with ex ->
                return
                    ScanUnavailable(sprintf "clamd scan failed against %s:%d — %s" options.Host options.Port ex.Message)
        }

/// Build a ClamAV scanner for `host` on clamd's default port.
let create (host: string) : IContentScanner =
    ClamAvContentScanner(ClamAvOptions.create host) :> IContentScanner

/// Build a ClamAV scanner from fully-specified options.
let createWith (options: ClamAvOptions) : IContentScanner =
    ClamAvContentScanner(options) :> IContentScanner