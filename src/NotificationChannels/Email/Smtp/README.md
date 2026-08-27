# SMTP transactional notification sink (Phase 6f)

The SDK-default email backend. Implements `INotificationSink` over
[MailKit](https://github.com/jstedfast/MailKit) — MIT-licensed, no
paid dependency, GP 2 holds. Works against any vanilla SMTP server:
customer-owned mail relays, MailHog / Mailpit for local dev, AWS SES
SMTP-mode endpoints, etc.

## Activation

Two paths:

1. **Env-driven** (the reference app's wiring):
   ```bash
   TOOLUP_TRANSACTIONAL_EMAIL=smtp
   TOOLUP_SMTP_HOST=smtp.example.com
   TOOLUP_SMTP_PORT=587
   TOOLUP_SMTP_USERNAME=apikey
   TOOLUP_SMTP_PASSWORD=…
   TOOLUP_SMTP_TLS=true
   TOOLUP_SMTP_FROM=noreply@example.com
   TOOLUP_SMTP_FROM_NAME=ExampleCo
   ```

2. **Explicit construction** in your composition root:
   ```fsharp skip=fragment
   let addressBook = NotificationAddressBook.BlobBackedNotificationAddressBook(blobStorage, Some logger) :> INotificationAddressBook
   let settings: SmtpSettings = {
       Host = "smtp.example.com"
       Port = 587
       Username = Some "apikey"
       Password = Some "…"
       UseTls = true
       DefaultFromAddress = "noreply@example.com"
       DefaultFromDisplayName = Some "ExampleCo"
   }

   let smtpSink = SmtpNotificationSink.create addressBook settings (Some logger)

   ServerApp.empty
   |> ServerApp.withStorage blobStorage
   |> ServerApp.withTransactionalSink smtpSink
   |> ServerApp.run
   ```

## Failure classification

| SMTP response | `SinkResult` |
|---|---|
| 4xx command | `TransientFailure` (retried) |
| 5xx command | `PermanentFailure` |
| Socket / TLS error | `TransientFailure` |
| Other exception | `TransientFailure` (dispatcher promotes to permanent on retry exhaustion) |

## Limitations

- **Templated email is not supported.** SMTP has no vendor-side
  template substitution; `TemplatedEmail` envelopes return
  `PermanentFailure`. Use the SendGrid companion for templates.
- **Connection per send.** Fresh SMTP connection on every dispatch.
  Adds ~50 ms latency per send; an obvious follow-up is connection
  pooling.

## See also

- `src/ToolUp.Platform/TECHNICAL_GUIDE.md` — transactional notification
  dispatcher, including its retry / audit semantics.
