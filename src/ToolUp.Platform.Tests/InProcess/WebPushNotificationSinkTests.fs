module ToolUp.Platform.Tests.InProcess.WebPushNotificationSinkTests

open System.IO
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.NotificationChannels.Push.WebPush
open ToolUp.Platform.Tests.Contracts

/// Bind `INotificationSinkContract` to the WebPush sink. The factory
/// uses test-only VAPID key material — the sink will fail to dispatch
/// because there are no registered tokens (the contract's sample
/// envelope carries `user-x` whose `ResolvePushTokens` returns `[]` on
/// the no-op address book), so `Send` returns
/// `SinkResult.Skipped "no_addressable_recipients"`. That's still a
/// valid contract outcome.
let tests =
    let factory () =
        let addressBook =
            NotificationAddressBook.NoOpNotificationAddressBook() :> INotificationAddressBook

        let secretStore =
            let root =
                Path.Combine(Path.GetTempPath(), "toolup-tests-webpush-" + System.Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory root |> ignore
            FileSecretStore.FileSecretStore(root) :> ISecretStore

        // VAPID public + private keys are base64url-encoded byte
        // arrays (P-256). The values here are valid in shape; without
        // a registered subscription Send won't reach the crypto path.
        let settings: WebPushSettings = {
            VapidPublicKey = "BEl62iUYgUivxIkv69yViEuiBIa-Ib9-SkvMeAtA3LFgDzkrxZJjSgSnfckjBJuBkr3qBUYIHBQFLXYp5Nksh8U"
            VapidSubject = "mailto:noreply@example.com"
        }

        WebPushNotificationSink.create addressBook secretStore settings None

    let sampleEnvelope (scopeId: string) : NotificationEnvelope =
        let payload: PushEnvelope = {
            RecipientUserIds = [ "user-x" ]
            Title = "Test push"
            Body = "Body"
            DeepLink = None
            CorrelationId = None
        }

        NotificationEnvelope.create scopeId (MobilePush payload)

    INotificationSinkContract.tests "WebPushNotificationSink" factory sampleEnvelope