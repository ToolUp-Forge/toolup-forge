module ToolUp.Platform.AuditSinks.CefSyslog

open System
open System.Net.Security
open System.Net.Sockets
open System.Text
open System.Threading

// ─── Public surface ──────────────────────────────────────────────
//
// Phase 9g.A syslog transports for the `ToolUp.AuditSinks.Cef`
// companion. BCL sockets only — no vendor SDK, no third-party syslog
// library (GP 1).
//
// **Distributed-ready.** Every transport connects, writes, and closes
// inside one `Send` call. Nothing is cached between calls, so two
// processes running the same sink behave identically and a restart loses
// no in-flight state (portability rule 4). The cost is a TCP handshake
// per batch, which is the right trade for an audit path delivering tens
// of batches a minute — a pooled connection would trade that for a
// silent failure mode where a half-open socket swallows a batch.
//
// **Three protocols.** UDP is the default and the one every SIEM
// accepts; it is fire-and-forget, so `Ok` means "the datagram left this
// host", never "the SIEM has it". TCP gives delivery confirmation to the
// collector's socket. TLS gives that plus confidentiality — audit
// payloads routinely carry user ids and resource names, so a deployment
// crossing an untrusted network wants `CefTlsSyslog`.
//
// **Framing.** Each CEF line is wrapped in an RFC 3164 syslog header
// (`<PRI>MMM dd HH:mm:ss HOSTNAME TAG: …`) because that is what the
// ArcSight / QRadar / LogRhythm syslog listeners parse before handing the
// remainder to their CEF decoder. UDP sends one datagram per line; the
// stream transports use RFC 6587 non-transparent framing (LF-terminated),
// which every mainstream collector accepts and which keeps a partial
// write recoverable at a line boundary.

/// Wire protocol for the syslog transport.
type CefSyslogProtocol =
    /// RFC 5426 UDP syslog. The SIEM default. Fire-and-forget: no
    /// delivery confirmation, and datagrams over ~1472 bytes fragment.
    /// The 1023-byte CEF cap keeps a framed line inside one datagram.
    | CefUdpSyslog
    /// RFC 6587 TCP syslog, LF-framed. Confirms the collector accepted
    /// the bytes.
    | CefTcpSyslog
    /// RFC 5425-style TLS syslog — TCP framing inside a TLS session, with
    /// full server-certificate validation.
    | CefTlsSyslog

module CefSyslogProtocol =
    /// Short label used in error strings and `ICefLineTransport.Protocol`.
    let name =
        function
        | CefUdpSyslog -> "udp"
        | CefTcpSyslog -> "tcp"
        | CefTlsSyslog -> "tls"

/// Resolved collector endpoint. Constructed from the `ISecretStore`
/// value on every delivery — never held on the sink — so an endpoint
/// migration takes effect on the next batch without a redeploy.
type CefSyslogEndpoint = {
    /// Hostname or IP of the syslog collector. For `CefTlsSyslog` this
    /// is also the name the server certificate is validated against.
    Host: string
    /// Collector port. 514 is the syslog default (UDP and TCP); 6514 is
    /// the registered TLS-syslog port.
    Port: int
}

module CefSyslogEndpoint =
    /// Parse a `host:port` secret value. IPv6 literals are accepted in
    /// bracket form (`[::1]:514`). The error strings name the malformed
    /// input shape rather than echoing the secret value — the endpoint is
    /// not itself sensitive, but the store it came from is, and an error
    /// string that quotes secret material ends up in the audit log.
    let parse (raw: string) : Result<CefSyslogEndpoint, string> =
        if String.IsNullOrWhiteSpace raw then
            Error "endpoint secret is empty — expected host:port"
        else
            let value = raw.Trim()

            let hostPart, portPart =
                if value.StartsWith("[", StringComparison.Ordinal) then
                    let close = value.IndexOf ']'

                    if close < 0 then
                        "", ""
                    else
                        let host = value.Substring(1, close - 1)
                        let rest = value.Substring(close + 1)

                        if rest.StartsWith(":", StringComparison.Ordinal) then
                            host, rest.Substring 1
                        else
                            host, ""
                else
                    let idx = value.LastIndexOf ':'

                    if idx <= 0 then
                        "", ""
                    else
                        value.Substring(0, idx), value.Substring(idx + 1)

            if String.IsNullOrWhiteSpace hostPart || String.IsNullOrWhiteSpace portPart then
                Error "endpoint secret is not in host:port form"
            else
                match Int32.TryParse portPart with
                | true, port when port > 0 && port <= 65535 -> Ok { Host = hostPart; Port = port }
                | _ -> Error "endpoint secret carries a port outside 1–65535"

/// RFC 3164 framing parameters. Defaults put audit traffic on `local0`
/// with the tag `ToolUpAudit`, which is what a collector's routing rule
/// keys off when the deployment does not want every syslog source in one
/// bucket.
type CefSyslogFraming = {
    /// Syslog facility number. `local0` = 16 through `local7` = 23 are
    /// the range reserved for site-local use; the well-known facilities
    /// (0–15) belong to the OS and mixing audit traffic into them makes
    /// a collector's rules ambiguous.
    Facility: int
    /// Hostname stamped into the syslog header. Deployments set this to
    /// the emitting node's name so a SOC can attribute an event to a
    /// host without reading the CEF extension.
    Hostname: string
    /// Syslog TAG (application name).
    AppName: string
}

module CefSyslogFraming =
    /// `local0`, hostname `toolup`, tag `ToolUpAudit`.
    let defaults: CefSyslogFraming = {
        Facility = 16
        Hostname = "toolup"
        AppName = "ToolUpAudit"
    }

    /// Map a CEF severity (0–10, high = severe) onto a syslog severity
    /// (0–7, LOW = severe — the scales run in opposite directions, which
    /// is the classic way this mapping gets written backwards).
    ///
    /// 9–10 → 2 (critical), 7–8 → 3 (error), 4–6 → 4 (warning),
    /// 1–3 → 5 (notice), 0 → 6 (informational).
    let syslogSeverityOfCef (cefSeverity: int) =
        let clamped = cefSeverity |> max 0 |> min 10

        if clamped >= 9 then 2
        elif clamped >= 7 then 3
        elif clamped >= 4 then 4
        elif clamped >= 1 then 5
        else 6

    /// RFC 3164 PRI value — `facility * 8 + severity`.
    let priority (framing: CefSyslogFraming) (cefSeverity: int) =
        let facility = framing.Facility |> max 0 |> min 23
        facility * 8 + syslogSeverityOfCef cefSeverity

    /// A sanitised syslog token — no spaces, no control characters. The
    /// RFC 3164 header is whitespace-delimited, so a hostname containing
    /// a space shifts every later field.
    let private token (fallback: string) (value: string) =
        if String.IsNullOrWhiteSpace value then
            fallback
        else
            let sb = StringBuilder(value.Length)

            for ch in value do
                if not (Char.IsWhiteSpace ch) && not (Char.IsControl ch) then
                    sb.Append ch |> ignore

            if sb.Length = 0 then fallback else sb.ToString()

    /// Wrap one CEF line in an RFC 3164 syslog header. `timestamp` is
    /// rendered in the RFC's `MMM dd HH:mm:ss` form with a
    /// space-padded day — invariant culture, because a collector parses
    /// the English month abbreviations positionally and a machine running
    /// under a non-English locale would otherwise emit an unparseable
    /// header.
    let frame (framing: CefSyslogFraming) (timestamp: DateTime) (cefSeverity: int) (cefLine: string) : string =
        let pri = priority framing cefSeverity
        let culture = Globalization.CultureInfo.InvariantCulture
        let month = timestamp.ToString("MMM", culture)
        let day = timestamp.Day.ToString(culture).PadLeft 2
        let time = timestamp.ToString("HH:mm:ss", culture)

        sprintf
            "<%d>%s %s %s %s: %s"
            pri
            month
            day
            (token "toolup" framing.Hostname)
            (token "ToolUpAudit" framing.AppName)
            cefLine

/// One framed syslog line plus the CEF severity it was rendered at (the
/// PRI byte needs the severity, and recomputing it inside the transport
/// would duplicate the formatter's classification).
type CefSyslogRecord = {
    /// The rendered CEF line, before syslog framing.
    CefLine: string
    /// CEF severity 0–10 of the underlying event.
    Severity: int
}

/// Transport seam. `CefAuditSink` depends on this rather than on a
/// concrete socket type so a deployment can substitute a queueing or
/// relaying transport, and so the contract tests can bind a real
/// in-process listener without a SIEM.
type ICefLineTransport =
    /// Short protocol label for diagnostics (`udp` / `tcp` / `tls`).
    abstract Protocol: string
    /// Deliver every record to `endpoint`. Returns `Error` with a
    /// diagnostic on any failure; the dispatcher owns the retry loop.
    abstract Send: endpoint: CefSyslogEndpoint * records: CefSyslogRecord list -> Async<Result<unit, string>>

let private frameAll (framing: CefSyslogFraming) (records: CefSyslogRecord list) =
    let now = DateTime.Now

    records
    |> List.map (fun record -> CefSyslogFraming.frame framing now record.Severity record.CefLine)

/// RFC 5426 UDP syslog transport — one datagram per line. Fire-and-forget:
/// an `Ok` means the datagrams were handed to the OS, not that the SIEM
/// received them. Deployments that need delivery evidence use
/// `CefTcpSyslog` or `CefTlsSyslog`.
type UdpCefSyslogTransport(framing: CefSyslogFraming) =

    interface ICefLineTransport with
        member _.Protocol = CefSyslogProtocol.name CefUdpSyslog

        member _.Send(endpoint, records) = async {
            if List.isEmpty records then
                return Ok()
            else
                try
                    use client = new UdpClient()

                    for line in frameAll framing records do
                        let bytes = Encoding.UTF8.GetBytes line

                        do!
                            client.SendAsync(ReadOnlyMemory bytes, endpoint.Host, endpoint.Port).AsTask()
                            |> Async.AwaitTask
                            |> Async.Ignore

                    return Ok()
                with ex ->
                    return
                        Error(sprintf "CEF udp syslog send to %s:%d failed: %s" endpoint.Host endpoint.Port ex.Message)
        }

/// Shared connect-write-close body for the two stream transports. The
/// only difference between them is whether a `SslStream` is layered over
/// the `NetworkStream`, so the framing, timeout, and error shapes stay in
/// one place.
let private sendOverStream
    (protocolName: string)
    (framing: CefSyslogFraming)
    (connectTimeout: TimeSpan)
    (useTls: bool)
    (endpoint: CefSyslogEndpoint)
    (records: CefSyslogRecord list)
    =
    async {
        if List.isEmpty records then
            return Ok()
        else
            try
                use cts = new CancellationTokenSource(connectTimeout)
                use client = new TcpClient()

                do!
                    client.ConnectAsync(endpoint.Host, endpoint.Port, cts.Token).AsTask()
                    |> Async.AwaitTask

                use networkStream = client.GetStream()

                let! stream =
                    if not useTls then
                        async { return networkStream :> IO.Stream }
                    else
                        async {
                            let ssl = new SslStream(networkStream, leaveInnerStreamOpen = false)
                            do! ssl.AuthenticateAsClientAsync endpoint.Host |> Async.AwaitTask
                            return ssl :> IO.Stream
                        }

                use payloadStream = stream

                // RFC 6587 non-transparent framing: one LF-terminated line
                // per record. A short write leaves the collector at a line
                // boundary rather than mid-record.
                let payload =
                    frameAll framing records
                    |> List.map (fun line -> line + "\n")
                    |> String.concat ""
                    |> Encoding.UTF8.GetBytes

                do!
                    payloadStream.WriteAsync(ReadOnlyMemory payload, cts.Token).AsTask()
                    |> Async.AwaitTask

                do! payloadStream.FlushAsync cts.Token |> Async.AwaitTask

                return Ok()
            with ex ->
                return
                    Error(
                        sprintf
                            "CEF %s syslog send to %s:%d failed: %s"
                            protocolName
                            endpoint.Host
                            endpoint.Port
                            ex.Message
                    )
    }

/// RFC 6587 TCP syslog transport, LF-framed. Connects per `Send`.
type TcpCefSyslogTransport(framing: CefSyslogFraming, connectTimeout: TimeSpan) =
    /// 10-second default connect/write timeout.
    new(framing) = TcpCefSyslogTransport(framing, TimeSpan.FromSeconds 10.0)

    interface ICefLineTransport with
        member _.Protocol = CefSyslogProtocol.name CefTcpSyslog

        member _.Send(endpoint, records) =
            sendOverStream (CefSyslogProtocol.name CefTcpSyslog) framing connectTimeout false endpoint records

/// TLS syslog transport — TCP framing inside a TLS session. Server
/// certificate validation is the platform default (full chain + name
/// check); this companion deliberately exposes no "skip validation"
/// switch, since a deployment reaching for TLS here is doing so to keep
/// audit payloads off the wire in clear, and a bypass flag is how that
/// gets silently undone.
type TlsCefSyslogTransport(framing: CefSyslogFraming, connectTimeout: TimeSpan) =
    /// 10-second default connect/handshake/write timeout.
    new(framing) = TlsCefSyslogTransport(framing, TimeSpan.FromSeconds 10.0)

    interface ICefLineTransport with
        member _.Protocol = CefSyslogProtocol.name CefTlsSyslog

        member _.Send(endpoint, records) =
            sendOverStream (CefSyslogProtocol.name CefTlsSyslog) framing connectTimeout true endpoint records

/// Build the transport for a protocol with the given framing.
let transportFor (protocol: CefSyslogProtocol) (framing: CefSyslogFraming) : ICefLineTransport =
    match protocol with
    | CefUdpSyslog -> UdpCefSyslogTransport framing :> _
    | CefTcpSyslog -> TcpCefSyslogTransport framing :> _
    | CefTlsSyslog -> TlsCefSyslogTransport framing :> _