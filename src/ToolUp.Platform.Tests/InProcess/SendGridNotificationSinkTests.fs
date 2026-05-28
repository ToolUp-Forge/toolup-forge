module ToolUp.Platform.Tests.InProcess.SendGridNotificationSinkTests

open System.IO
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.NotificationChannels.Email.SendGrid
open ToolUp.Platform.Tests.Contracts

/// Bind `INotificationSinkContract` to the SendGrid sink. The factory
/// configures the sink against a deliberately-unreachable endpoint; a
/// `Send` will resolve to `SinkResult.TransientFailure` (network) or
/// `SinkResult.PermanentFailure` (no API key) — both valid contract
/// outcomes. The point is to verify the metadata and that vendor
/// failures classify rather than throwing. A real round-trip test
/// against a recording mock or sandbox API key is deferred — same
/// convention as the SMTP env-gated test.
let tests =
    let factory () =
        let addressBook =
            NotificationAddressBook.NoOpNotificationAddressBook() :> INotificationAddressBook

        // Empty in-memory secret store — no SENDGRID_API_KEY → first
        // Send returns PermanentFailure with the configuration error.
        let secretStore =
            let root =
                Path.Combine(Path.GetTempPath(), "toolup-tests-sg-" + System.Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory root |> ignore
            FileSecretStore.FileSecretStore(root) :> ISecretStore

        let settings: SendGridSettings = {
            DefaultFromAddress = "noreply@example.com"
            DefaultFromDisplayName = None
            EndpointOverride = Some "https://localhost.invalid/v3/mail/send"
        }

        SendGridNotificationSink.create addressBook secretStore settings None

    let sampleEnvelope (scopeId: string) : NotificationEnvelope =
        let payload: EmailEnvelope = {
            RecipientUserIds = [ "user-x" ]
            Content = InlineEmail("Test subject", "Test body", None)
            CorrelationId = None
        }

        NotificationEnvelope.create scopeId (TransactionalEmail payload)

    INotificationSinkContract.tests "SendGridNotificationSink" factory sampleEnvelope