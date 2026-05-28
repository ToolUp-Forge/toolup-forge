module ToolUp.Platform.Tests.InProcess.TwilioNotificationSinkTests

open System.IO
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.NotificationChannels.Sms.Twilio
open ToolUp.Platform.Tests.Contracts

/// Bind `INotificationSinkContract` to the Twilio SMS sink. Same
/// no-live-backend pattern as the SendGrid binding — vendor failures
/// classify into `SinkResult` rather than throwing.
let tests =
    let factory () =
        let addressBook =
            NotificationAddressBook.NoOpNotificationAddressBook() :> INotificationAddressBook

        let secretStore =
            let root =
                Path.Combine(Path.GetTempPath(), "toolup-tests-twilio-" + System.Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory root |> ignore
            FileSecretStore.FileSecretStore(root) :> ISecretStore

        let settings: TwilioSettings = {
            AccountSid = "AC_dummy_sid"
            FromPhoneNumber = "+15555550000"
            EndpointOverride = Some "https://localhost.invalid/Messages.json"
        }

        TwilioNotificationSink.create addressBook secretStore settings None

    let sampleEnvelope (scopeId: string) : NotificationEnvelope =
        let payload: SmsEnvelope = {
            RecipientUserIds = [ "user-x" ]
            Body = "Test SMS"
            CorrelationId = None
        }

        NotificationEnvelope.create scopeId (TransactionalSms payload)

    INotificationSinkContract.tests "TwilioNotificationSink" factory sampleEnvelope