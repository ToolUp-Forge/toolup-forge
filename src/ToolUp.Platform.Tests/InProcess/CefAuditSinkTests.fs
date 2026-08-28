module ToolUp.Platform.Tests.InProcess.CefAuditSinkTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AuditSinks.Cef
open ToolUp.Platform.AuditSinks.CefFormat
open ToolUp.Platform.AuditSinks.CefSyslog
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts

// ─── Phase 9g.A — ToolUp.AuditSinks.Cef ─────────────────────────────
//
// Three layers:
//
//   1. A strict CEF parser (below) written independently of the
//      formatter. Every rendering assertion goes through it, so the
//      tests fail on a line a receiver could not parse rather than on a
//      string this repo happens to expect. It re-derives the escaping
//      rules from the spec rather than calling the formatter's own
//      escape functions — a shared helper would agree with a bug.
//
//   2. Real in-process syslog listeners (TCP + UDP on the loopback,
//      ephemeral ports). The phase text says "against a local rsyslog
//      fixture"; a real rsyslog would be an external daemon dependency
//      and would break the fresh-checkout-green rule, so the fixture is
//      an in-process socket speaking the same wire. The sink, the
//      transport, the framing, and the socket path are all exercised for
//      real — only the daemon is substituted.
//
//   3. The `IAuditSinkContract` pack bound over the TCP listener (TCP,
//      not UDP: the contract pack counts deliveries, and a
//      fire-and-forget datagram is not a countable event).

// ── A strict, independent CEF parser ──────────────────────────────

type ParsedCef = {
    Version: string
    Vendor: string
    Product: string
    DeviceVersion: string
    SignatureId: string
    Name: string
    Severity: int
    Extension: (string * string) list
}

module private CefParser =
    /// True when the character at `index` is escaped — i.e. preceded by
    /// an ODD number of backslashes.
    let private isEscaped (text: string) (index: int) =
        let mutable back = index - 1
        let mutable count = 0

        while back >= 0 && text[back] = '\\' do
            count <- count + 1
            back <- back - 1

        count % 2 = 1

    let private unescapeHeader (value: string) =
        let sb = StringBuilder(value.Length)
        let mutable i = 0

        while i < value.Length do
            if value[i] = '\\' && i + 1 < value.Length then
                sb.Append value[i + 1] |> ignore
                i <- i + 2
            else
                sb.Append value[i] |> ignore
                i <- i + 1

        sb.ToString()

    let private unescapeExtension (value: string) =
        let sb = StringBuilder(value.Length)
        let mutable i = 0

        while i < value.Length do
            if value[i] = '\\' && i + 1 < value.Length then
                match value[i + 1] with
                | 'n' -> sb.Append '\n' |> ignore
                | 'r' -> sb.Append '\r' |> ignore
                | other -> sb.Append other |> ignore

                i <- i + 2
            elif value[i] = '\\' then
                // A trailing lone backslash: the escape sequence was cut
                // in half. Surface it so the truncation tests can fail on
                // it rather than silently absorbing it.
                failwith "CEF extension value ends in a dangling backslash — an escape sequence was split"
            else
                sb.Append value[i] |> ignore
                i <- i + 1

        sb.ToString()

    /// Split the extension into key/value pairs. Keys are alphanumeric
    /// runs terminated by an unescaped `=`; a value runs to the space
    /// before the next key.
    let private parseExtension (text: string) =
        let keyStarts = [
            for i in 0 .. text.Length - 1 do
                if text[i] = '=' && not (isEscaped text i) then
                    let mutable start = i - 1

                    while start >= 0 && Char.IsLetterOrDigit text[start] do
                        start <- start - 1

                    if start + 1 = i then
                        failwithf "CEF extension has an empty key before '=' at offset %d" i

                    yield start + 1, i
        ]

        keyStarts
        |> List.mapi (fun index (keyStart, eq) ->
            let key = text.Substring(keyStart, eq - keyStart)

            let valueEnd =
                match List.tryItem (index + 1) keyStarts with
                | Some(nextKeyStart, _) -> nextKeyStart - 1
                | None -> text.Length

            let raw = text.Substring(eq + 1, valueEnd - (eq + 1))
            key, unescapeExtension raw)

    /// Parse a bare CEF line (no syslog header). Throws with a
    /// diagnostic on anything a receiver would reject.
    let parse (line: string) : ParsedCef =
        if not (line.StartsWith("CEF:", StringComparison.Ordinal)) then
            failwithf "not a CEF line: %s" line

        // Take the first six unescaped pipes; everything after the sixth
        // is the extension (which may legally contain pipes).
        let boundaries = [
            for i in 0 .. line.Length - 1 do
                if line[i] = '|' && not (isEscaped line i) then
                    yield i
        ]

        if boundaries.Length < 7 then
            failwithf "CEF header has %d unescaped pipes, expected at least 7: %s" boundaries.Length line

        let cut = boundaries |> List.take 7

        let fields =
            cut
            |> List.mapi (fun index pipe ->
                let start = if index = 0 then 0 else cut[index - 1] + 1
                line.Substring(start, pipe - start))

        let extension = line.Substring(cut[6] + 1)

        let severity =
            match Int32.TryParse fields[6] with
            | true, value when value >= 0 && value <= 10 -> value
            | _ -> failwithf "CEF severity field %s is not an integer in 0–10" fields[6]

        {
            Version = fields[0]
            Vendor = unescapeHeader fields[1]
            Product = unescapeHeader fields[2]
            DeviceVersion = unescapeHeader fields[3]
            SignatureId = unescapeHeader fields[4]
            Name = unescapeHeader fields[5]
            Severity = severity
            Extension = parseExtension extension
        }

    /// Strip an RFC 3164 syslog header (`<PRI>MMM dd HH:mm:ss host tag: `)
    /// and return the PRI plus the CEF remainder.
    let stripSyslog (framed: string) : int * string =
        if not (framed.StartsWith("<", StringComparison.Ordinal)) then
            failwithf "not a syslog-framed line: %s" framed

        let close = framed.IndexOf '>'

        if close < 0 then
            failwithf "syslog PRI is not terminated: %s" framed

        let pri =
            match Int32.TryParse(framed.Substring(1, close - 1)) with
            | true, value -> value
            | _ -> failwithf "syslog PRI is not an integer: %s" framed

        let cefStart = framed.IndexOf("CEF:", StringComparison.Ordinal)

        if cefStart < 0 then
            failwithf "syslog line carries no CEF payload: %s" framed

        pri, framed.Substring cefStart

// ── Fixtures ──────────────────────────────────────────────────────

/// In-process TCP syslog listener on an ephemeral loopback port.
/// Accepts connections in a background loop and records every
/// LF-terminated line.
type private TcpSyslogListener() =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    let lines = ConcurrentQueue<string>()
    let cts = new CancellationTokenSource()

    do
        listener.Start()

        let loop = async {
            while not cts.IsCancellationRequested do
                let! client = listener.AcceptTcpClientAsync(cts.Token).AsTask() |> Async.AwaitTask
                use client = client
                use stream = client.GetStream()
                use reader = new StreamReader(stream, Encoding.UTF8)
                let! payload = reader.ReadToEndAsync() |> Async.AwaitTask

                for line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries) do
                    lines.Enqueue line
        }

        Async.Start(
            async {
                try
                    do! loop
                with _ ->
                    ()
            },
            cts.Token
        )

    member _.Port = (listener.LocalEndpoint :?> IPEndPoint).Port
    member _.Lines = lines |> List.ofSeq

    /// Poll until at least `count` lines have arrived or the deadline
    /// passes. The listener drains a connection on close, so the wait is
    /// for the sink's socket teardown, not for the network.
    member this.WaitForLines(count: int, timeout: TimeSpan) =
        let deadline = DateTime.UtcNow.Add timeout

        while this.Lines.Length < count && DateTime.UtcNow < deadline do
            Thread.Sleep 25

        this.Lines

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()
            listener.Stop()
            cts.Dispose()

/// In-process UDP syslog listener on an ephemeral loopback port.
type private UdpSyslogListener() =
    let client = new UdpClient(IPEndPoint(IPAddress.Loopback, 0))
    let datagrams = ConcurrentQueue<string>()
    let cts = new CancellationTokenSource()

    do
        Async.Start(
            async {
                try
                    while not cts.IsCancellationRequested do
                        let! result = client.ReceiveAsync(cts.Token).AsTask() |> Async.AwaitTask
                        datagrams.Enqueue(Encoding.UTF8.GetString result.Buffer)
                with _ ->
                    ()
            },
            cts.Token
        )

    member _.Port = (client.Client.LocalEndPoint :?> IPEndPoint).Port
    member _.Datagrams = datagrams |> List.ofSeq

    member this.WaitForDatagrams(count: int, timeout: TimeSpan) =
        let deadline = DateTime.UtcNow.Add timeout

        while this.Datagrams.Length < count && DateTime.UtcNow < deadline do
            Thread.Sleep 25

        this.Datagrams

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()
            client.Dispose()
            cts.Dispose()

type private FixedSecretStore(value: string option) =
    interface ISecretStore with
        member _.GetSecret(_scope, _key) = async { return value }
        member _.SetSecret(_scope, _key, _value) = async { return Ok() }
        member _.DeleteSecret(_scope, _key) = async { return Ok() }
        member _.ListKeys(_scope) = async { return [] }

let private uniqueDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-cef-audit-sink-tests", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore
    dir

let private emptyStorage () =
    LocalFileStorage.LocalFileStorage(uniqueDir ()) :> IBlobStorage

let private baseTime = DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc)

let private envelopeFor (scopeId: string) (event: AuditEvent) =
    AuditEnvelope.fromScopeId scopeId baseTime event

let private loginEvent (userId: string) =
    UserLoggedIn {
        UserId = userId
        AuthProvider = "Header"
    }

let private testIdentity: CefDeviceIdentity = {
    Vendor = "Contoso"
    Product = "ContosoAnalytics"
    DeviceVersion = "2.4.0"
}

// ── Contract-pack binding ─────────────────────────────────────────

let private listeners = ConcurrentDictionary<obj, TcpSyslogListener>()

let private contractTests =
    let factory () =
        let listener = new TcpSyslogListener()

        let settings = {
            CefSinkSettings.defaults with
                Identity = testIdentity
                Protocol = CefTcpSyslog
        }

        let secrets =
            FixedSecretStore(Some(sprintf "127.0.0.1:%d" listener.Port)) :> ISecretStore

        let sink =
            create "test-cef" settings secrets "cef_syslog_endpoint" (emptyStorage ())

        listeners[box sink] <- listener
        sink

    let verifyDelivered (sink: IAuditSink) (expected: AuditEnvelope list list) =
        let listener = listeners[box sink]
        let expectedLines = expected |> List.sumBy List.length
        let observed = listener.WaitForLines(expectedLines, TimeSpan.FromSeconds 10.0)

        Expect.hasLength observed expectedLines "one syslog line per delivered envelope"

        for line in observed do
            let _, cef = CefParser.stripSyslog line
            let parsed = CefParser.parse cef
            Expect.equal parsed.Vendor testIdentity.Vendor "vendor header survives the wire"

    IAuditSinkContract.tests "CefAuditSink" factory verifyDelivered

// ── Rendering ─────────────────────────────────────────────────────

let private renderingTests =
    testList "CEF rendering" [
        test "sample audit event renders a line a strict CEF parser accepts" {
            let envelope = envelopeFor "team-42" (loginEvent "u123")
            let line = renderLine testIdentity envelope
            let parsed = CefParser.parse line

            Expect.equal parsed.Version "CEF:0" "version prefix"
            Expect.equal parsed.Vendor "Contoso" "vendor"
            Expect.equal parsed.Product "ContosoAnalytics" "product"
            Expect.equal parsed.DeviceVersion "2.4.0" "device version"
            Expect.equal parsed.SignatureId "UserLoggedIn" "signature id is the audit event type"
            Expect.equal parsed.Name "UserLoggedIn" "name is the audit event type"
            Expect.equal parsed.Severity 2 "UserLoggedIn is informational"

            let extension = Map.ofList parsed.Extension
            Expect.equal (Map.tryFind "cat" extension) (Some "UserLoggedIn") "cat carries the event type"
            Expect.equal (Map.tryFind "cs1" extension) (Some "team-42") "cs1 carries the scope id"
            Expect.equal (Map.tryFind "cs1Label" extension) (Some "ScopeId") "cs1Label names cs1"
            Expect.equal (Map.tryFind "cs2" extension) (Some "team") "cs2 carries the subject kind"
            Expect.equal (Map.tryFind "cs3" extension) (Some "42") "cs3 carries the team id"
            Expect.equal (Map.tryFind "cs4" extension) (Some "2") "cs4 carries the audit schema version"
            Expect.isSome (Map.tryFind "externalId" extension) "externalId dedup key present"
            Expect.isSome (Map.tryFind "msg" extension) "msg carries the event payload"

            let rt = Map.find "rt" extension

            let expectedRt =
                (baseTime - DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds
                |> int64

            Expect.equal rt (string expectedRt) "rt is the envelope's OccurredAt in epoch millis"
        }

        test "header pipes and backslashes are escaped and round-trip" {
            let identity = {
                Vendor = "Acme|Corp"
                Product = "back\\slash"
                DeviceVersion = "1|2\\3"
            }

            let line = renderLine identity (envelopeFor "team-1" (loginEvent "u1"))
            let parsed = CefParser.parse line

            Expect.equal parsed.Vendor "Acme|Corp" "pipe survives the header escape round-trip"
            Expect.equal parsed.Product "back\\slash" "backslash survives the header escape round-trip"
            Expect.equal parsed.DeviceVersion "1|2\\3" "both survive together"
        }

        test "extension equals signs and backslashes are escaped and round-trip" {
            // The scope id lands in cs1 verbatim, so it is the cleanest
            // injection point for characters that are structural in the
            // extension section.
            let hostile = "a=b\\c d"
            let line = renderLine testIdentity (envelopeFor hostile (loginEvent "u1"))
            let parsed = CefParser.parse line
            let extension = Map.ofList parsed.Extension

            Expect.equal (Map.tryFind "cs1" extension) (Some hostile) "'=', '\\' and ' ' survive the extension escape"
        }

        test "newlines in an extension value are escaped, never emitted raw" {
            let line =
                renderLine testIdentity (envelopeFor "line\nbreak\rhere" (loginEvent "u1"))

            Expect.isFalse (line.Contains "\n") "no raw LF in the rendered line"
            Expect.isFalse (line.Contains "\r") "no raw CR in the rendered line"

            let extension = CefParser.parse line |> _.Extension |> Map.ofList
            Expect.equal (Map.tryFind "cs1" extension) (Some "line\nbreak\rhere") "the escapes round-trip"
        }

        test "non-alphanumeric extension keys are dropped, not escaped" {
            Expect.equal (sanitiseExtensionKey "cs1Label") "cs1Label" "a conforming key is untouched"
            Expect.equal (sanitiseExtensionKey "bad key=x") "badkeyx" "spaces and '=' are removed"
            Expect.equal (sanitiseExtensionKey "!!!") "" "an unusable key reduces to empty"
        }

        test "header fields are trimmed before escaping, so an escape is never split" {
            // A vendor string of nothing but backslashes: every character
            // doubles under escaping, so a trim applied to the escaped
            // text would strand a lone backslash.
            let identity = {
                testIdentity with
                    Vendor = String.replicate 400 "\\"
            }

            let line = renderLine identity (envelopeFor "team-1" (loginEvent "u1"))
            let parsed = CefParser.parse line

            Expect.equal parsed.Vendor (String.replicate MaxHeaderFieldChars "\\") "trimmed on raw text, escaped after"
        }
    ]

// ── Truncation ────────────────────────────────────────────────────

let private truncationTests =
    testList "1023-byte cap" [
        test "an oversized event truncates explicitly and still parses" {
            let payload = String.replicate 4000 "x"
            let line = renderLine testIdentity (envelopeFor "team-42" (loginEvent payload))

            Expect.isLessThanOrEqual
                (Encoding.UTF8.GetByteCount line)
                MaxCefLineBytes
                "the rendered line respects the CEF 1023-byte cap"

            Expect.stringEnds line TruncationMarker "truncation is explicit, not silent"

            let parsed = CefParser.parse line
            let extension = Map.ofList parsed.Extension

            Expect.equal (Map.tryFind "cefTruncated" extension) (Some "true") "the marker parses as a normal pair"
            Expect.equal (Map.tryFind "cs1" extension) (Some "team-42") "correlation fields survive truncation"
            Expect.isSome (Map.tryFind "externalId" extension) "the dedup key survives truncation"
        }

        test "a value made entirely of escape-expanding characters never cuts mid-escape" {
            // Every '=' escapes to '\=' (two bytes), so a truncation
            // computed on the escaped text lands between the backslash
            // and the '=' roughly half the time. The parser throws on a
            // dangling backslash, so this test fails loudly if the cut
            // is taken on the wrong side of the escape.
            for length in [ 700; 900; 1100; 1500; 4000 ] do
                let hostile = String.replicate length "="
                let line = renderLine testIdentity (envelopeFor "team-1" (loginEvent hostile))

                Expect.isLessThanOrEqual (Encoding.UTF8.GetByteCount line) MaxCefLineBytes "within the cap"

                let parsed = CefParser.parse line
                let msg = parsed.Extension |> List.tryFind (fst >> (=) "msg")

                match msg with
                | Some(_, value) ->
                    Expect.isFalse (value.Contains "\\") "the unescaped payload carries no stray backslash"
                | None -> ()
        }

        test "no partial key is ever emitted" {
            // Whatever the budget, every emitted pair must have a
            // non-empty alphanumeric key — the parser fails on an empty
            // one, so reaching the assertion at all is the guarantee.
            for length in [ 0; 1; 200; 950; 1023; 5000 ] do
                let line =
                    renderLine testIdentity (envelopeFor "team-1" (loginEvent (String.replicate length "y")))

                let parsed = CefParser.parse line

                for key, _ in parsed.Extension do
                    Expect.isTrue (key |> Seq.forall Char.IsLetterOrDigit) (sprintf "key %s is alphanumeric" key)
        }

        test "an unpadded event is not marked truncated" {
            let line = renderLine testIdentity (envelopeFor "team-42" (loginEvent "u1"))

            Expect.isFalse (line.Contains TruncationMarker) "a line well inside the cap carries no marker"
        }

        test "a pathological header still leaves a parseable line" {
            let identity = {
                Vendor = String.replicate 500 "V"
                Product = String.replicate 500 "P"
                DeviceVersion = String.replicate 500 "D"
            }

            let line =
                renderLine identity (envelopeFor "team-1" (loginEvent (String.replicate 2000 "z")))

            Expect.isLessThanOrEqual (Encoding.UTF8.GetByteCount line) MaxCefLineBytes "still inside the cap"
            CefParser.parse line |> ignore
        }
    ]

// ── Severity ──────────────────────────────────────────────────────

let private severityTests =
    testList "severity mapping" [
        test "bands project onto the CEF 0–10 scale" {
            Expect.equal (CefSeverityBand.toScore CefInformational) 2 "informational"
            Expect.equal (CefSeverityBand.toScore CefLow) 4 "low"
            Expect.equal (CefSeverityBand.toScore CefMedium) 6 "medium"
            Expect.equal (CefSeverityBand.toScore CefHigh) 8 "high"
            Expect.equal (CefSeverityBand.toScore CefCritical) 10 "critical"

            for band in CefSeverityBand.all do
                let score = CefSeverityBand.toScore band
                Expect.isGreaterThanOrEqual score 0 "at or above the CEF floor"
                Expect.isLessThanOrEqual score 10 "at or below the CEF ceiling"
        }

        test "known events classify by kind" {
            Expect.equal (severityBandOf (loginEvent "u1")) CefInformational "a login is routine"

            Expect.equal
                (severityBandOf (DataStoreReset { UserId = "u1"; FileCount = 12 }))
                CefCritical
                "wiping a scope's data store is irreversible"

            Expect.equal
                (severityBandOf (
                    PermissionChanged {
                        UserId = "u1"
                        TeamId = "t1"
                        AffectedUserId = "u2"
                        ModuleName = "m"
                        Permissions = "Admin"
                    }
                ))
                CefHigh
                "a permission change is security-relevant"

            Expect.equal
                (severityBandOf (FileDeleted { UserId = "u1"; FileName = "f" }))
                CefMedium
                "a deletion is notable"

            // Phase 739 — pins the band the phase argued for, in both
            // directions. Medium rather than High because this row is
            // authority EXERCISED under a credential the deployment
            // already issued, not authority coming into existence; and
            // strictly above Low because the key is the whole protection
            // on segments that leave the origin.
            Expect.equal
                (severityBandOf (
                    MediaKeyDelivered {
                        MediaId = "m1"
                        SubjectKind = "user"
                        SubjectId = Some "u1"
                        ScopeContainer = "team-a"
                        AdmissionRoute = "scope"
                        At = DateTime.UtcNow
                    }
                ))
                CefMedium
                "a delivered media key is access under existing authority, not an authority change"
        }

        test "an unclassified event falls to CefLow rather than failing" {
            Expect.equal
                (severityBandOf (
                    TeamCreated {
                        UserId = "u1"
                        TeamId = "t1"
                        TeamName = "T"
                    }
                ))
                CefLow
                "the conservative default for an unclassified state change"
        }

        test "a deployment override replaces the classification and is clamped" {
            let settings = {
                CefSinkSettings.defaults with
                    Identity = testIdentity
                    SeverityOverride = Some(fun _ -> 99)
            }

            let line =
                renderLineWith testIdentity settings.SeverityOverride (envelopeFor "team-1" (loginEvent "u1"))

            Expect.equal (CefParser.parse line).Severity 10 "an out-of-range override is clamped into 0–10"
        }

        test "syslog severity runs opposite to CEF severity" {
            // The classic way this mapping ships backwards: CEF 10 is the
            // most severe, syslog 0 is.
            let critical = CefSyslogFraming.syslogSeverityOfCef 10
            let informational = CefSyslogFraming.syslogSeverityOfCef 0

            Expect.isLessThan critical informational "a severe CEF event maps to a LOWER syslog severity"
            Expect.equal critical 2 "CEF 10 → syslog critical"
            Expect.equal informational 6 "CEF 0 → syslog informational"

            Expect.equal
                (CefSyslogFraming.priority CefSyslogFraming.defaults 10)
                (16 * 8 + 2)
                "PRI is facility * 8 + severity"
        }
    ]

// ── Dedup key ─────────────────────────────────────────────────────

let private dedupTests =
    testList "batch idempotency" [
        test "the dedup key is deterministic across renders" {
            let envelope = envelopeFor "team-42" (loginEvent "u123")

            Expect.equal (dedupKey envelope) (dedupKey envelope) "the same envelope hashes the same twice"

            let redelivered = envelopeFor "team-42" (loginEvent "u123")

            Expect.equal
                (dedupKey envelope)
                (dedupKey redelivered)
                "a retried envelope presents the same externalId so the SIEM can dedup it"
        }

        test "distinct events hash differently" {
            let a = envelopeFor "team-42" (loginEvent "u1")
            let b = envelopeFor "team-42" (loginEvent "u2")
            let c = envelopeFor "team-43" (loginEvent "u1")

            Expect.notEqual (dedupKey a) (dedupKey b) "payload difference changes the key"
            Expect.notEqual (dedupKey a) (dedupKey c) "scope difference changes the key"
        }

        test "the rendered externalId matches the computed dedup key" {
            let envelope = envelopeFor "team-42" (loginEvent "u123")

            let extension =
                renderLine testIdentity envelope |> CefParser.parse |> _.Extension |> Map.ofList

            Expect.equal (Map.tryFind "externalId" extension) (Some(dedupKey envelope)) "externalId is the dedup key"
        }
    ]

// ── Endpoint + config resolution ──────────────────────────────────

let private configTests =
    testList "endpoint + identity resolution" [
        test "host:port parses, including IPv6 bracket form" {
            Expect.equal
                (CefSyslogEndpoint.parse "siem.example.com:514")
                (Ok {
                    Host = "siem.example.com"
                    Port = 514
                })
                "host:port"

            Expect.equal
                (CefSyslogEndpoint.parse "[2001:db8::1]:6514")
                (Ok { Host = "2001:db8::1"; Port = 6514 })
                "bracketed IPv6 literal"
        }

        test "a malformed endpoint is rejected without echoing the secret" {
            for bad in
                [
                    ""
                    "   "
                    "siem.example.com"
                    "siem.example.com:0"
                    "siem.example.com:70000"
                    ":514"
                ] do
                match CefSyslogEndpoint.parse bad with
                | Ok endpoint -> failtestf "expected a parse failure for %s, got %A" bad endpoint
                | Error reason ->
                    Expect.isFalse
                        (reason.Contains bad && bad.Length > 3)
                        "the diagnostic must not echo the secret store value"
        }

        test "a missing endpoint secret surfaces as Error, not an exception" {
            let sink =
                create "cef-no-secret" CefSinkSettings.defaults (FixedSecretStore None) "cef_endpoint" (emptyStorage ())

            let envelope = envelopeFor "team-1" (loginEvent "u1")

            match sink.Deliver [ envelope ] |> Async.RunSynchronously with
            | Ok() -> failtest "delivery with no configured endpoint must not report success"
            | Error message -> Expect.stringContains message "cef_endpoint" "the diagnostic names the missing secret"
        }

        test "the config blob overrides the device identity field-by-field" {
            let fallback = {
                Vendor = "FallbackVendor"
                Product = "FallbackProduct"
                DeviceVersion = "0.0.1"
            }

            let parsed =
                parseIdentityJson fallback """{"vendor":"Contoso","deviceVersion":"2.4.0"}"""

            Expect.equal
                parsed
                (Some {
                    Vendor = "Contoso"
                    Product = "FallbackProduct"
                    DeviceVersion = "2.4.0"
                })
                "named keys override; absent keys keep the fallback"

            Expect.equal
                (parseIdentityJson fallback """{"VENDOR":"Contoso"}""")
                (Some { fallback with Vendor = "Contoso" })
                "property names match case-insensitively — the file is hand-edited"
        }

        test "a malformed config blob falls back rather than failing the delivery" {
            let fallback = CefDeviceIdentity.defaults

            Expect.isNone (parseIdentityJson fallback "not json at all") "unparseable input yields None"
            Expect.isNone (parseIdentityJson fallback "[1,2,3]") "a non-object document yields None"
            Expect.isNone (parseIdentityJson fallback "") "an empty document yields None"

            Expect.equal
                (parseIdentityJson fallback """{"unrelated":"key"}""")
                (Some fallback)
                "a valid object with no recognised keys keeps the fallback"
        }

        test "the blob identity reaches the wire" {
            use listener = new TcpSyslogListener()
            let dir = uniqueDir ()
            let storage = LocalFileStorage.LocalFileStorage dir :> IBlobStorage

            let json =
                """{"vendor":"Contoso","product":"ContosoAnalytics","deviceVersion":"9.9.9"}"""

            storage.Upload("_platform", "audit/cef.json", Encoding.UTF8.GetBytes json)
            |> Async.RunSynchronously
            |> ignore

            let settings = {
                CefSinkSettings.defaults with
                    Protocol = CefTcpSyslog
            }

            let secrets = FixedSecretStore(Some(sprintf "127.0.0.1:%d" listener.Port))
            let sink = create "cef-config" settings secrets "cef_endpoint" storage

            let result =
                sink.Deliver [ envelopeFor "team-1" (loginEvent "u1") ]
                |> Async.RunSynchronously

            Expect.equal result (Ok()) "delivery succeeds"

            let lines = listener.WaitForLines(1, TimeSpan.FromSeconds 10.0)
            Expect.hasLength lines 1 "one line delivered"

            let _, cef = CefParser.stripSyslog lines[0]
            let parsed = CefParser.parse cef

            Expect.equal parsed.Vendor "Contoso" "vendor comes from _platform/audit/cef.json"
            Expect.equal parsed.DeviceVersion "9.9.9" "device version comes from the config blob"
        }
    ]

// ── Transports ────────────────────────────────────────────────────

let private transportTests =
    testList "syslog transports" [
        test "UDP delivers a syslog-framed CEF datagram" {
            use listener = new UdpSyslogListener()

            let settings = {
                CefSinkSettings.defaults with
                    Identity = testIdentity
                    Protocol = CefUdpSyslog
            }

            let secrets = FixedSecretStore(Some(sprintf "127.0.0.1:%d" listener.Port))
            let sink = create "cef-udp" settings secrets "cef_endpoint" (emptyStorage ())

            let result =
                sink.Deliver [ envelopeFor "team-7" (loginEvent "u9") ]
                |> Async.RunSynchronously

            Expect.equal result (Ok()) "the datagram left the host"

            let datagrams = listener.WaitForDatagrams(1, TimeSpan.FromSeconds 10.0)
            Expect.hasLength datagrams 1 "the loopback listener received it"

            let pri, cef = CefParser.stripSyslog datagrams[0]
            let parsed = CefParser.parse cef

            Expect.equal
                pri
                (CefSyslogFraming.priority CefSyslogFraming.defaults parsed.Severity)
                "PRI matches severity"

            Expect.equal parsed.SignatureId "UserLoggedIn" "the event survives the datagram"
        }

        test "TCP delivers one LF-framed line per envelope" {
            use listener = new TcpSyslogListener()

            let settings = {
                CefSinkSettings.defaults with
                    Identity = testIdentity
                    Protocol = CefTcpSyslog
            }

            let secrets = FixedSecretStore(Some(sprintf "127.0.0.1:%d" listener.Port))
            let sink = create "cef-tcp" settings secrets "cef_endpoint" (emptyStorage ())

            let batch = [
                envelopeFor "team-1" (loginEvent "u1")
                envelopeFor "team-2" (loginEvent "u2")
                envelopeFor "team-3" (loginEvent "u3")
            ]

            Expect.equal (sink.Deliver batch |> Async.RunSynchronously) (Ok()) "the batch was accepted"

            let lines = listener.WaitForLines(3, TimeSpan.FromSeconds 10.0)
            Expect.hasLength lines 3 "one line per envelope"

            let scopes =
                lines
                |> List.map (fun line ->
                    let _, cef = CefParser.stripSyslog line
                    CefParser.parse cef |> _.Extension |> Map.ofList |> Map.find "cs1")

            Expect.equal scopes [ "team-1"; "team-2"; "team-3" ] "delivered in batch order"
        }

        test "an unreachable collector surfaces as Error, never as a throw" {
            let settings = {
                CefSinkSettings.defaults with
                    Protocol = CefTcpSyslog
            }

            // Port 1 on the loopback: reliably refused, no DNS involved.
            let secrets = FixedSecretStore(Some "127.0.0.1:1")

            let sink =
                create "cef-unreachable" settings secrets "cef_endpoint" (emptyStorage ())

            match
                sink.Deliver [ envelopeFor "team-1" (loginEvent "u1") ]
                |> Async.RunSynchronously
            with
            | Ok() -> failtest "a refused connection must not report success"
            | Error message -> Expect.stringContains message "tcp" "the diagnostic names the transport"
        }

        test "transportFor selects the protocol it is asked for" {
            let framing = CefSyslogFraming.defaults

            Expect.equal (transportFor CefUdpSyslog framing).Protocol "udp" "udp"
            Expect.equal (transportFor CefTcpSyslog framing).Protocol "tcp" "tcp"
            Expect.equal (transportFor CefTlsSyslog framing).Protocol "tls" "tls"
        }
    ]

// ── Strip-imports ─────────────────────────────────────────────────

let private stripImportsTests =
    testList "strip-imports" [
        test "removing the companion returns the deployment to no replication" {
            // The companion is reachable only through the sink list a
            // deployment composes. With it gone the list is empty, and
            // the replicator subsystem — background service, bounded
            // channel, event-store decorator — is never constructed
            // (GP 13).
            let storage = emptyStorage ()
            let inner = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let logger =
                { new ILogger with
                    member _.Debug(_) = ()
                    member _.Info(_) = ()
                    member _.Warn(_) = ()
                    member _.Error(_, _) = ()
                }

            let built =
                ComposeAudit.buildAuditReplicatorSubsystem [] None AuditSamplingPolicy.none storage inner logger

            Expect.isNone built "no sinks composed ⇒ no audit-replication subsystem"
        }

        test "composing the CEF sink does construct the subsystem" {
            let storage = emptyStorage ()
            let inner = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let logger =
                { new ILogger with
                    member _.Debug(_) = ()
                    member _.Info(_) = ()
                    member _.Warn(_) = ()
                    member _.Error(_, _) = ()
                }

            let sink =
                create "cef-strip" CefSinkSettings.defaults (FixedSecretStore(Some "127.0.0.1:514")) "k" storage

            let built =
                ComposeAudit.buildAuditReplicatorSubsystem [ sink ] None AuditSamplingPolicy.none storage inner logger

            Expect.isSome built "the sink is the only thing standing between the two states"
        }
    ]

let tests =
    testList "Phase 9g.A — CEF audit sink" [
        renderingTests
        truncationTests
        severityTests
        dedupTests
        configTests
        transportTests
        stripImportsTests
        contractTests
    ]