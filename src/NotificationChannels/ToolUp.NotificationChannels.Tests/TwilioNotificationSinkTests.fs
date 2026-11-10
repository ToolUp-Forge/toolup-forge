module ToolUp.NotificationChannels.Tests.TwilioNotificationSinkTests

open System.IO
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.NotificationChannels.Sms.Twilio
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Config
open ToolUp.Platform.Jobs
open ToolUp.Platform.Tests.InProcess
open ToolUp.Platform.Testing

/// Bind `INotificationSinkContract` to the Twilio SMS sink. Same
/// no-live-backend pattern as the SendGrid binding — vendor failures
/// classify into `SinkResult` rather than throwing.
let tests =
    let factory () =
        let addressBook =
            NotificationAddressBook.NoOpNotificationAddressBook() :> INotificationAddressBook

        let secretStore =
            let root = (TempDir.forRun "tests-twilio").Path
            FileSecretStore.FileSecretStore(root) :> ISecretStore

        let settings: TwilioSettings = {
            AccountSid = "AC_dummy_sid"
            FromPhoneNumber = "+15555550000"
            EndpointOverride = Some "https://localhost.invalid/Messages.json"
        }

        TwilioNotificationSink.create addressBook secretStore settings None

    let sampleEnvelope (scopeId: string) : NotificationEnvelope =
        let payload: SmsEnvelope = {
            Recipients = [ RecipientId.User(UserId "user-x") ]
            Body = "Test SMS"
            CorrelationId = None
        }

        NotificationEnvelope.create scopeId (TransactionalSms payload)

    INotificationSinkContract.tests "TwilioNotificationSink" factory sampleEnvelope