module ToolUp.Platform.NotificationChannels.Email.SmtpHealth

open System
open System.Net.Sockets
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.NotificationChannels.Email.Smtp

// ─── Phase 9k SMTP transactional sink health probe ───────────────────
//
// TCP-connect to the configured `Host:Port`. We deliberately do NOT
// authenticate or issue SMTP commands — credential-using probes
// generate noise on the SMTP server's audit log on every poll, and
// connection-level reachability is sufficient signal for "is the
// server up". A successful TCP handshake means the SMTP daemon is
// listening; whether it accepts our credentials is a request-time
// concern surfaced through the existing `TransactionalDispatcher`
// retry/dead-letter path.

type SmtpNotificationSinkHealthCheck(settings: SmtpSettings) =
    interface IHealthCheck with
        member _.Name = "transactional_sink:smtp"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 3.0

        member _.Check() = async {
            try
                use client = new TcpClient()
                do! client.ConnectAsync(settings.Host, settings.Port) |> Async.AwaitTask
                return Healthy
            with ex ->
                return Unhealthy ex.Message
        }

let create (settings: SmtpSettings) : IHealthCheck =
    SmtpNotificationSinkHealthCheck(settings) :> IHealthCheck