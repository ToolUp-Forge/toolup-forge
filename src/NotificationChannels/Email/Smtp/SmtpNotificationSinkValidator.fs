module ToolUp.Platform.NotificationChannels.Email.SmtpValidator

open System
open System.Net.Sockets
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.NotificationChannels.Email.Smtp

// ─── Phase 9m SMTP config preflight ──────────────────────────────────
//
// TCP-connect to the configured `Host:Port`. We deliberately do NOT
// authenticate, issue STARTTLS, or send SMTP commands — credential-
// using probes generate audit-log noise on the SMTP server and
// connection-level reachability is sufficient signal for "is the
// server up at deploy time". A successful TCP handshake means the
// SMTP daemon is listening; whether it accepts our credentials is a
// runtime concern surfaced through the existing
// `TransactionalDispatcher` retry / dead-letter path.
//
// Mirrors the Phase 9k `SmtpNotificationSinkHealth` probe exactly; the
// validator runs once at compose end while the health probe runs on
// every `/ready` poll.

type private Impl(settings: SmtpSettings) =
    interface IConfigValidator with
        member _.Name = sprintf "smtp-notification (%s:%d)" settings.Host settings.Port
        member _.Timeout = TimeSpan.FromSeconds 5.0

        member _.Validate() = async {
            try
                use client = new TcpClient()
                do! client.ConnectAsync(settings.Host, settings.Port) |> Async.AwaitTask
                return Ok
            with ex ->
                return Error(sprintf "TCP connect to %s:%d failed: %s" settings.Host settings.Port ex.Message)
        }

/// Construct a validator from explicit settings. Matches the
/// `SmtpNotificationSink.create` shape so deployments wiring custom
/// settings can re-use the same record.
let create (settings: SmtpSettings) : IConfigValidator = Impl(settings) :> IConfigValidator

/// Read SMTP settings from env via `SmtpSettings.fromEnv` and return a
/// validator. Fails fast on missing required vars — same contract as
/// `SmtpNotificationSink.fromEnv`. Use only when the deployment has
/// already decided to run the SMTP companion (i.e. behind a
/// `TOOLUP_TRANSACTIONAL_EMAIL=smtp` env-var check) so the failure
/// shape matches the sink's.
let fromEnv () : IConfigValidator = create (SmtpSettings.fromEnv ())