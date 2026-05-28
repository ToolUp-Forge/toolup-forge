module ToolUp.Platform.Tests.InProcess.SmtpNotificationSinkTests

open ToolUp.Platform
open ToolUp.Platform.NotificationChannels.Email.Smtp
open ToolUp.Platform.Tests.Contracts

/// Bind `INotificationSinkContract` to the SDK SMTP sink. The factory
/// returns a sink configured against a deliberately-unreachable host:
///
///   * `Kind` and `Provider` assertions verify metadata regardless of
///     connection state.
///   * The "Send returns a SinkResult" assertion exercises the
///     classification path — connection refused / DNS-fail surfaces as
///     `SinkResult.TransientFailure`, which IS a valid result per the
///     contract. The point is that vendor failures must not throw.
///
/// A separate env-gated integration test (against MailHog / Mailpit
/// in `TOOLUP_SMTP_TEST_*`) exercises the happy path; that test is
/// not part of this contract binding because it requires external
/// infrastructure.
let tests =
    let factory () =
        let addressBook =
            NotificationAddressBook.NoOpNotificationAddressBook() :> INotificationAddressBook

        let settings: SmtpSettings = {
            Host = "localhost.invalid" // unreachable; Send returns TransientFailure
            Port = 25
            Username = None
            Password = None
            UseTls = false
            DefaultFromAddress = "noreply@example.com"
            DefaultFromDisplayName = None
        }

        SmtpNotificationSink.create addressBook settings None

    let sampleEnvelope (scopeId: string) : NotificationEnvelope =
        let payload: EmailEnvelope = {
            RecipientUserIds = [ "user-x" ]
            Content = InlineEmail("Test subject", "Test body", None)
            CorrelationId = None
        }

        NotificationEnvelope.create scopeId (TransactionalEmail payload)

    INotificationSinkContract.tests "SmtpNotificationSink" factory sampleEnvelope