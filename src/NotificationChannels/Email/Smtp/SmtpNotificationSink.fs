module ToolUp.Platform.NotificationChannels.Email.Smtp

open System
open MailKit.Net.Smtp
open MimeKit
open ToolUp.Platform

// ─── Public surface ──────────────────────────────────────────────
//
// SDK-default email sink — Phase 6f. Implements `INotificationSink`
// over MailKit's `SmtpClient` (MIT-licensed, no paid deps required —
// works against any vanilla SMTP server: customer-owned mail relay,
// MailHog / Mailpit for local dev, AWS SES / SendGrid SMTP-mode
// endpoints).
//
// **Why MailKit instead of `System.Net.Mail.SmtpClient`.** The BCL
// implementation is officially deprecated for new code; MailKit is
// the actively maintained successor recommended by Microsoft. MIT-
// licensed under MimeKit so GP 2 (no paid deps) holds.
//
// **Connection per send vs. pooled.** The current implementation
// opens a fresh SMTP connection for each `Send` call. SMTP is
// connection-oriented, so a real production deployment with high
// volume would want connection pooling — that's a follow-up
// optimisation; the fresh-connection-per-send path is simpler to
// reason about, easier to test, and trades off latency (~50ms per
// send for connection setup) against throughput rather than
// correctness.

/// Connection settings for the SMTP sink. `fromEnv` builds one from
/// the documented `TOOLUP_SMTP_*` environment variables. Apps that
/// load settings from a different source (a config file, a deployment
/// secret manager, KMS-encrypted env) construct this record directly.
type SmtpSettings = {
    /// SMTP server hostname or IP.
    Host: string
    /// Port number. Conventionally 25 (legacy / no encryption), 587
    /// (STARTTLS submission), or 465 (implicit TLS).
    Port: int
    /// SASL username for AUTH, or `None` for an open relay.
    Username: string option
    /// SASL password / API token used as password.
    Password: string option
    /// `true` to enable STARTTLS / implicit TLS based on `Port`. MailKit
    /// auto-detects: port 465 → implicit TLS, 587 → STARTTLS, 25 →
    /// plain unless the server advertises STARTTLS.
    UseTls: bool
    /// Default `From:` address when the publishing scope's
    /// `_platform.notification_prefs.email.fromAddress` config is
    /// blank. Empty string = no fallback (sink rejects with
    /// `PermanentFailure` so the audit trail records the gap).
    DefaultFromAddress: string
    /// Display name attached to the default `From:` address.
    DefaultFromDisplayName: string option
}

module SmtpSettings =
    /// Read connection details from the standard env vars:
    ///   TOOLUP_SMTP_HOST       — required
    ///   TOOLUP_SMTP_PORT       — required, parsed as int
    ///   TOOLUP_SMTP_USERNAME   — optional (open relay if absent)
    ///   TOOLUP_SMTP_PASSWORD   — optional, paired with username
    ///   TOOLUP_SMTP_TLS        — "true" / "false" (default: true)
    ///   TOOLUP_SMTP_FROM       — required default `From:` address
    ///   TOOLUP_SMTP_FROM_NAME  — optional `From:` display name
    ///
    /// Throws on missing required env vars — a deployment with an
    /// `EnabledSmtp` config but no host / from is misconfigured and
    /// should fail loudly at startup, not silently swallow every send.
    let fromEnv () : SmtpSettings =
        let read name =
            match Environment.GetEnvironmentVariable name with
            | null
            | "" -> None
            | v -> Some v

        let readRequired name =
            match read name with
            | Some v -> v
            | None -> failwithf "Phase 6f Smtp: env var %s is required" name

        let port =
            match Int32.TryParse(readRequired "TOOLUP_SMTP_PORT") with
            | true, n -> n
            | false, _ -> failwithf "Phase 6f Smtp: TOOLUP_SMTP_PORT must be an integer"

        let useTls =
            match read "TOOLUP_SMTP_TLS" with
            | Some v -> v.Equals("false", StringComparison.OrdinalIgnoreCase) |> not
            | None -> true

        {
            Host = readRequired "TOOLUP_SMTP_HOST"
            Port = port
            Username = read "TOOLUP_SMTP_USERNAME"
            Password = read "TOOLUP_SMTP_PASSWORD"
            UseTls = useTls
            DefaultFromAddress = readRequired "TOOLUP_SMTP_FROM"
            DefaultFromDisplayName = read "TOOLUP_SMTP_FROM_NAME"
        }

/// Stable provider string — separate constant so test code asserts
/// against the same value the dispatcher route sees. The sink's
/// `Kind` is a `NotificationKind.SinkKind` DU value, not a string;
/// the wire-format representation is `SinkKind.toWireString`.
[<Literal>]
let private ProviderName = "Smtp"

/// Build a MimeKit `MimeMessage` from an `EmailEnvelope`'s
/// `EmailContent`. `TemplatedEmail` returns `None` because SMTP has no
/// vendor-side template substitution — the sink rejects templated
/// envelopes with `PermanentFailure` so deployments switching from a
/// templated provider (SendGrid) to SMTP fail fast rather than
/// silently dropping the variables map.
let private buildMessage
    (fromAddress: MailboxAddress)
    (toAddresses: MailboxAddress list)
    (content: EmailContent)
    : Result<MimeMessage, string> =
    match content with
    | InlineEmail(subject, bodyText, bodyHtml) ->
        let msg = new MimeMessage()
        msg.From.Add fromAddress

        for addr in toAddresses do
            msg.To.Add addr

        msg.Subject <- subject

        let body = BodyBuilder()
        body.TextBody <- bodyText

        match bodyHtml with
        | Some html -> body.HtmlBody <- html
        | None -> ()

        msg.Body <- body.ToMessageBody()
        Ok msg
    | TemplatedEmail _ ->
        Error
            "SMTP does not support vendor-side templates; switch to a templated sink (SendGrid) or rewrite as InlineEmail"

/// Resolve the `From:` address for `scopeId`. The current
/// implementation uses the settings-supplied default; per-team
/// override (`_platform.notification_prefs.email.fromAddress`) is a
/// follow-up that needs `IConfigStore` resolution.
let private resolveFromAddress (settings: SmtpSettings) (_scopeId: string) : Result<MailboxAddress, string> =
    if String.IsNullOrWhiteSpace settings.DefaultFromAddress then
        Error "no From: address configured (set TOOLUP_SMTP_FROM or pass DefaultFromAddress)"
    else
        try
            let display = settings.DefaultFromDisplayName |> Option.defaultValue ""
            Ok(MailboxAddress(display, settings.DefaultFromAddress))
        with ex ->
            Error $"From: address parse failed: {settings.DefaultFromAddress} — {ex.GetType().Name}: {ex.Message}"

/// Map a vendor-neutral `EmailAddress` to MailKit's `MailboxAddress`.
/// Phase 6f uses MimeKit at the wire boundary only — `EmailAddress`
/// stays in the shared layer so a future SendGrid sink reuses it.
let private toMailbox (addr: EmailAddress) : MailboxAddress =
    let display = addr.DisplayName |> Option.defaultValue ""
    MailboxAddress(display, addr.Address)

/// SMTP-backed transactional email sink. `addressBook` is the SDK's
/// `INotificationAddressBook` (default `BlobBackedNotificationAddressBook`,
/// or a directory-driven impl), used to resolve `RecipientUserIds` to
/// concrete `EmailAddress`es at dispatch time. `settings` carries the
/// SMTP connection details; `logger` is optional so tests can run
/// silent.
type SmtpNotificationSink(addressBook: INotificationAddressBook, settings: SmtpSettings, logger: ILogger option) =

    let logWarn (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    /// Resolve every recipient userId to an `EmailAddress`. Skips
    /// userIds with no resolvable address — caller decides what to do
    /// with an empty resolved list.
    let resolveRecipients (scopeId: string) (userIds: string list) : Async<MailboxAddress list> = async {
        let resolutions =
            userIds
            |> List.map (fun userId -> async {
                let! email = addressBook.ResolveEmail(userId, scopeId)
                return email |> Option.map toMailbox
            })
            |> Async.Parallel

        let! addresses = resolutions
        return addresses |> Array.choose id |> Array.toList
    }

    interface INotificationSink with
        member _.Kind = NotificationKind.SinkKind.Email
        member _.Provider = ProviderName

        member _.Send(scopeId, envelope) = async {
            match envelope.Notification with
            | TransactionalEmail email ->
                let! recipients = resolveRecipients scopeId email.RecipientUserIds

                if List.isEmpty recipients then
                    return SinkResult.Skipped "no_addressable_recipients"
                else
                    match resolveFromAddress settings scopeId with
                    | Error err -> return SinkResult.PermanentFailure err
                    | Ok fromAddr ->
                        match buildMessage fromAddr recipients email.Content with
                        | Error err -> return SinkResult.PermanentFailure err
                        | Ok message ->
                            // Forward the caller's CorrelationId as
                            // RFC 5322 Message-ID when present so
                            // vendor-side dedup (SES, SendGrid) and
                            // operator audit cross-reference work.
                            match email.CorrelationId with
                            | Some corr ->
                                try
                                    message.MessageId <- corr
                                with _ ->
                                    ()
                            | None -> ()

                            try
                                use client = new SmtpClient()

                                let secureOption =
                                    if settings.UseTls then
                                        MailKit.Security.SecureSocketOptions.Auto
                                    else
                                        MailKit.Security.SecureSocketOptions.None

                                do!
                                    client.ConnectAsync(settings.Host, settings.Port, secureOption)
                                    |> Async.AwaitTask

                                match settings.Username, settings.Password with
                                | Some u, Some p -> do! client.AuthenticateAsync(u, p) |> Async.AwaitTask
                                | _ -> ()

                                let! _ = client.SendAsync message |> Async.AwaitTask

                                do! client.DisconnectAsync(true) |> Async.AwaitTask

                                // SMTP doesn't return a vendor-side
                                // message id; the message-id header
                                // (caller's CorrelationId or a MimeKit-
                                // generated Guid) is the closest
                                // equivalent.
                                return SinkResult.Delivered(Some message.MessageId)
                            with
                            | :? SmtpCommandException as ex ->
                                // SMTP error codes 4xx are transient
                                // (mailbox temporarily unavailable,
                                // service shutting down, exceeded
                                // storage allocation); 5xx are
                                // permanent (bad address, message
                                // rejected). MailKit's
                                // SmtpCommandException carries the
                                // numeric status code so we classify
                                // mechanically.
                                let code = int ex.StatusCode

                                if code >= 400 && code < 500 then
                                    return SinkResult.TransientFailure(sprintf "SMTP %d: %s" code ex.Message)
                                else
                                    return SinkResult.PermanentFailure(sprintf "SMTP %d: %s" code ex.Message)
                            | :? System.Net.Sockets.SocketException as ex ->
                                // Network-level failure: connection
                                // refused, DNS lookup failed, TCP
                                // reset. All transient — retry could
                                // succeed.
                                return SinkResult.TransientFailure(sprintf "SMTP socket: %s" ex.Message)
                            | ex ->
                                // Any other exception (TLS handshake
                                // failure, protocol parse error) is
                                // also transient by default — the
                                // dispatcher's retry budget will
                                // promote it to permanent if it keeps
                                // failing, and that promotion is the
                                // correct audit shape.
                                logWarn $"[SmtpNotificationSink] unhandled exception: {ex.GetType().Name}: {ex.Message}"
                                return SinkResult.TransientFailure(sprintf "%s: %s" (ex.GetType().Name) ex.Message)
            | other ->
                // Non-email notifications shouldn't reach this sink
                // (the dispatcher routes by Kind). Defensive case:
                // PermanentFailure rather than panic so the audit
                // trail records the routing bug.
                return
                    SinkResult.PermanentFailure
                        $"SmtpNotificationSink received unexpected notification kind: {NotificationKind.ofNotification other}"
        }

module SmtpNotificationSink =
    /// Construct an `INotificationSink` from explicit settings.
    /// Apps wanting per-deployment custom config (a YAML file, KMS
    /// secrets) build `SmtpSettings` themselves.
    let create
        (addressBook: INotificationAddressBook)
        (settings: SmtpSettings)
        (logger: ILogger option)
        : INotificationSink =
        SmtpNotificationSink(addressBook, settings, logger) :> _

    /// Construct an `INotificationSink` reading SMTP settings from the
    /// `TOOLUP_SMTP_*` env vars. Convenient for the common case where
    /// the deployment already exposes mail credentials through env.
    /// Fails fast on missing env vars — see `SmtpSettings.fromEnv`.
    let fromEnv (addressBook: INotificationAddressBook) (logger: ILogger option) : INotificationSink =
        SmtpNotificationSink(addressBook, SmtpSettings.fromEnv (), logger) :> _