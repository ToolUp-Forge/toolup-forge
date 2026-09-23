// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.TwilioWhatsAppNotificationSinkTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.NotificationChannels.WhatsApp
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tracing
open ToolUp.Platform.NotificationChannels.WhatsApp.Twilio
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.InProcess.TransactionalDispatcherTests

// Phase 6f.B — the Twilio WhatsApp companion. Never live: the contract
// pack runs against an unroutable endpoint (failures must classify, not
// throw) and against a `StubHandler` fake transport; the request-shape
// cases pin the exact form body Twilio receives.

/// One request the stub transport saw.
type private SeenRequest = {
    Method: string
    Url: string
    Authorization: string
    Body: string
}

/// Fake Twilio: records every request and answers with a programmed
/// status and body.
type private StubHandler(status: HttpStatusCode, responseBody: string) =
    inherit HttpMessageHandler()

    let seen = ConcurrentQueue<SeenRequest>()

    member _.Seen = seen |> Seq.toList

    override _.SendAsync(request: HttpRequestMessage, _: CancellationToken) : Task<HttpResponseMessage> = task {
        let! body =
            match request.Content with
            | null -> Task.FromResult ""
            | content -> content.ReadAsStringAsync()

        seen.Enqueue {
            Method = request.Method.Method
            Url = string request.RequestUri
            Authorization =
                match request.Headers.Authorization with
                | null -> ""
                | auth -> $"{auth.Scheme} {auth.Parameter}"
            Body = body
        }

        return
            new HttpResponseMessage(
                status,
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            )
    }

/// Resolves a WhatsApp number for exactly the recipients it was given;
/// every other lookup is a miss.
type private StubAddressBook(numbers: Map<RecipientId, string>) =
    interface INotificationAddressBook with
        member _.ResolveEmail(_, _) = async { return None }
        member _.ResolvePhone(_, _) = async { return None }
        member _.ResolvePushTokens(_, _) = async { return [] }
        member _.ResolveWhatsApp(recipient, _) = async { return numbers |> Map.tryFind recipient }

/// A secret store holding at most the one Twilio token — hermetic,
/// unlike a file store, which falls through to the process environment.
type private TokenStore(token: string option) =
    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            return
                if scopeId = "_platform" && key = "TWILIO_AUTH_TOKEN" then
                    token
                else
                    None
        }

        member _.SetSecret(_, _, _) = async { return Error "read-only" }
        member _.DeleteSecret(_, _) = async { return Error "read-only" }
        member _.ListKeys _ = async { return [] }

let private alice = RecipientId.User "wa-alice"
let private bob = RecipientId.External "wa-bob"
let private aliceNumber = "+447700900601"
let private bobNumber = "+447700900602"

let private addressBook () =
    StubAddressBook(Map.ofList [ alice, aliceNumber; bob, bobNumber ]) :> INotificationAddressBook

let private settings: TwilioWhatsAppSettings = {
    AccountSid = "ACtest"
    FromWhatsAppNumber = "+14155238886"
    EndpointOverride = None
    ContentSids =
        Map.ofList [
            "appointment_reminder@en_GB", "HXen0000000000000000000000000000"
            "appointment_reminder", "HXdefault00000000000000000000000"
        ]
}

let private messagesUrl =
    "https://api.twilio.com/2010-04-01/Accounts/ACtest/Messages.json"

let private expectedAuth =
    "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes "ACtest:tok-123")

let private stubbed (status: HttpStatusCode) (body: string) =
    let handler = new StubHandler(status, body)

    let sink =
        TwilioWhatsAppNotificationSink.createWithHandler
            (addressBook ())
            (TokenStore(Some "tok-123"))
            settings
            None
            handler

    sink, handler

let private delivered () =
    stubbed HttpStatusCode.Created """{"sid":"SM0000000000000000000000000000001","status":"queued"}"""

let private template (recipients: RecipientId list) : WhatsAppEnvelope = {
    Recipients = recipients
    TemplateName = Some "appointment_reminder"
    TemplateLanguage = Some "en_GB"
    TemplateParameters = [ "Alex"; "10:00" ]
    Body = None
    Metadata = Map.ofList [ "campaign", "reminders" ]
    CorrelationId = Some "corr-6fB"
}

let private freeForm (recipients: RecipientId list) : WhatsAppEnvelope = {
    template recipients with
        TemplateName = None
        TemplateLanguage = None
        TemplateParameters = []
        Body = Some "Thanks — see you tomorrow."
}

let private send (sink: INotificationSink) (whatsApp: WhatsAppEnvelope) =
    let envelope =
        NotificationEnvelope.create "scope-6fB" (TransactionalWhatsApp whatsApp)

    sink.Send(envelope.ScopeId, envelope)

let private unroutableFactory () =
    let secretStore =
        let root =
            Path.Combine(Path.GetTempPath(), "toolup-tests-twilio-wa-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore
        FileSecretStore.FileSecretStore(root) :> ISecretStore

    TwilioWhatsAppNotificationSink.create
        (NotificationAddressBook.NoOpNotificationAddressBook() :> INotificationAddressBook)
        secretStore
        {
            settings with
                EndpointOverride = Some "https://localhost.invalid/Messages.json"
        }
        None

let tests =
    testList "Phase 6f.B — Twilio WhatsApp companion" [
        INotificationSinkContract.tests "TwilioWhatsAppNotificationSink" unroutableFactory (fun scopeId ->
            NotificationEnvelope.create scopeId (TransactionalWhatsApp(freeForm [ RecipientId.User "user-x" ])))

        INotificationSinkContract.whatsAppTests
            "TwilioWhatsAppNotificationSink (fake transport)"
            (fun () -> fst (delivered ()))
            alice
            INotificationSinkContract.SupportsTemplates

        testList "request shape (StubHandler)" [
            testCaseAsync "a template send posts ContentSid + numbered ContentVariables, whatsapp:-prefixed"
            <| async {
                let sink, handler = delivered ()
                let! result = send sink (template [ alice ])

                Expect.equal
                    result
                    (SinkResult.Delivered(Some "SM0000000000000000000000000000001"))
                    "delivered, carrying Twilio's message SID"

                match handler.Seen with
                | [ request ] ->
                    Expect.equal request.Method "POST" "a create"
                    Expect.equal request.Url messagesUrl "the account's Messages endpoint"
                    Expect.equal request.Authorization expectedAuth "Basic AccountSid:AuthToken"

                    Expect.equal
                        request.Body
                        ("From=whatsapp%3A%2B14155238886"
                         + "&To=whatsapp%3A%2B447700900601"
                         + "&ContentSid=HXen0000000000000000000000000000"
                         + "&ContentVariables=%7B%221%22%3A%22Alex%22%2C%222%22%3A%2210%3A00%22%7D")
                        "the exact form body: language-specific content SID, placeholders 1 and 2, no Body, no metadata"
                | other -> failtestf "expected exactly one request, saw %d" other.Length
            }

            testCaseAsync "a free-form send posts Body and no content-template fields"
            <| async {
                let sink, handler = delivered ()
                let! result = send sink (freeForm [ alice ])

                Expect.isTrue
                    (match result with
                     | SinkResult.Delivered _ -> true
                     | _ -> false)
                    $"delivered, got %A{result}"

                match handler.Seen with
                | [ request ] ->
                    Expect.equal
                        request.Body
                        ("From=whatsapp%3A%2B14155238886"
                         + "&To=whatsapp%3A%2B447700900601"
                         + "&Body=Thanks+%E2%80%94+see+you+tomorrow.")
                        "the exact form body of a free-form send"
                | other -> failtestf "expected exactly one request, saw %d" other.Length
            }

            testCaseAsync "a template with no parameters sends no ContentVariables"
            <| async {
                let sink, handler = delivered ()

                let! _ =
                    send sink {
                        template [ alice ] with
                            TemplateParameters = []
                    }

                match handler.Seen with
                | [ request ] ->
                    Expect.equal
                        request.Body
                        "From=whatsapp%3A%2B14155238886&To=whatsapp%3A%2B447700900601&ContentSid=HXen0000000000000000000000000000"
                        "no empty ContentVariables object"
                | other -> failtestf "expected exactly one request, saw %d" other.Length
            }

            testCaseAsync "an unmapped language falls back to the bare-name content SID"
            <| async {
                let sink, handler = delivered ()

                let! _ =
                    send sink {
                        template [ alice ] with
                            TemplateLanguage = Some "fr"
                    }

                match handler.Seen with
                | [ request ] ->
                    Expect.stringContains
                        request.Body
                        "ContentSid=HXdefault00000000000000000000000"
                        "the language-agnostic entry"
                | other -> failtestf "expected exactly one request, saw %d" other.Length
            }

            testCaseAsync "a configured whatsapp: prefix on the sender is not doubled"
            <| async {
                let handler = new StubHandler(HttpStatusCode.Created, "{}")

                let sink =
                    TwilioWhatsAppNotificationSink.createWithHandler
                        (addressBook ())
                        (TokenStore(Some "tok-123"))
                        {
                            settings with
                                FromWhatsAppNumber = "whatsapp:+14155238886"
                        }
                        None
                        handler

                let! _ = send sink (freeForm [ alice ])

                match handler.Seen with
                | [ request ] ->
                    Expect.stringStarts request.Body "From=whatsapp%3A%2B14155238886&" "one prefix, not two"
                | other -> failtestf "expected exactly one request, saw %d" other.Length
            }

            testCaseAsync "every resolved recipient gets its own request, in order"
            <| async {
                let sink, handler = delivered ()
                let! _ = send sink (template [ alice; bob ])

                Expect.equal
                    (handler.Seen |> List.map (fun r -> r.Body.Split('&')[1]))
                    [ "To=whatsapp%3A%2B447700900601"; "To=whatsapp%3A%2B447700900602" ]
                    "one POST per recipient"
            }
        ]

        testList "refusals and classification" [
            testCaseAsync "a template with no content SID fails permanently and sends nothing"
            <| async {
                let sink, handler = delivered ()

                let! result =
                    send sink {
                        template [ alice ] with
                            TemplateName = Some "never_mapped"
                    }

                match result with
                | SinkResult.PermanentFailure reason ->
                    Expect.stringStarts reason "twilio_content_sid_not_configured" "snake_case reason first"
                | other -> failtestf "expected PermanentFailure, got %A" other

                Expect.isEmpty handler.Seen "no request for a template Twilio cannot address"
            }

            testCaseAsync "no resolvable recipient is a skip, and nothing is sent"
            <| async {
                let sink, handler = delivered ()
                let! result = send sink (template [ RecipientId.External "no-consent" ])

                Expect.equal
                    result
                    (SinkResult.Skipped "no_addressable_recipients")
                    "a consent-less recipient resolves to nothing"

                Expect.isEmpty handler.Seen "no request"
            }

            testCaseAsync "a missing auth token fails permanently and sends nothing"
            <| async {
                let handler = new StubHandler(HttpStatusCode.Created, "{}")

                let sink =
                    TwilioWhatsAppNotificationSink.createWithHandler
                        (addressBook ())
                        (TokenStore None)
                        settings
                        None
                        handler

                let! result = send sink (template [ alice ])

                match result with
                | SinkResult.PermanentFailure reason ->
                    Expect.stringContains reason "TWILIO_AUTH_TOKEN" "names the secret"
                | other -> failtestf "expected PermanentFailure, got %A" other

                Expect.isEmpty handler.Seen "no request without a credential"
            }

            testCaseAsync "4xx is permanent, and the first failure stops the loop"
            <| async {
                let sink, handler =
                    stubbed HttpStatusCode.BadRequest """{"code":63016,"message":"outside the allowed window"}"""

                let! result = send sink (template [ alice; bob ])

                match result with
                | SinkResult.PermanentFailure reason -> Expect.stringContains reason "Twilio 400" "carries the status"
                | other -> failtestf "expected PermanentFailure, got %A" other

                Expect.equal handler.Seen.Length 1 "the second recipient is not attempted"
            }

            testCaseAsync "5xx and 429 are transient"
            <| async {
                for status in [ HttpStatusCode.ServiceUnavailable; HttpStatusCode.TooManyRequests ] do
                    let sink, _ = stubbed status "{}"
                    let! result = send sink (template [ alice ])

                    match result with
                    | SinkResult.TransientFailure _ -> ()
                    | other -> failtestf "expected TransientFailure for %A, got %A" status other
            }

            testCaseAsync "a notification of another kind is refused permanently"
            <| async {
                let sink, handler = delivered ()

                let envelope =
                    NotificationEnvelope.create
                        "scope-6fB"
                        (TransactionalSms {
                            Recipients = [ alice ]
                            Body = "hi"
                            CorrelationId = None
                        })

                let! result = sink.Send(envelope.ScopeId, envelope)

                Expect.isTrue
                    (match result with
                     | SinkResult.PermanentFailure _ -> true
                     | _ -> false)
                    $"got %A{result}"

                Expect.isEmpty handler.Seen "nothing sent"
            }
        ]

        testList "settings" [
            test "parseContentSids reads name and name@language entries" {
                Expect.equal
                    (TwilioWhatsAppSettings.parseContentSids " reminder = HX1 , reminder@en_GB=HX2 ")
                    (Ok(Map.ofList [ "reminder", "HX1"; "reminder@en_GB", "HX2" ]))
                    "trimmed pairs"

                Expect.equal (TwilioWhatsAppSettings.parseContentSids "") (Ok Map.empty) "blank is empty"
            }

            test "parseContentSids refuses a malformed entry, naming it" {
                match TwilioWhatsAppSettings.parseContentSids "reminder=HX1,broken,=HX3" with
                | Error message ->
                    Expect.stringContains message "broken" "names the entry without '='"
                    Expect.stringContains message "=HX3" "names the entry with a blank key"
                | Ok map -> failtestf "expected an Error, got %A" map
            }

            test "contentSidFor prefers the language entry, then the bare name" {
                Expect.equal
                    (TwilioWhatsAppSettings.contentSidFor "appointment_reminder" (Some "en_GB") settings)
                    (Some "HXen0000000000000000000000000000")
                    "language-specific"

                Expect.equal
                    (TwilioWhatsAppSettings.contentSidFor "appointment_reminder" None settings)
                    (Some "HXdefault00000000000000000000000")
                    "no language → bare name"

                Expect.isNone (TwilioWhatsAppSettings.contentSidFor "other" (Some "en_GB") settings) "unmapped"
            }
        ]

        testList "health probe" [
            testCaseAsync "a 200 from the account resource is Healthy; the probe is a GET of Accounts/{sid}.json"
            <| async {
                let handler = new StubHandler(HttpStatusCode.OK, """{"sid":"ACtest"}""")

                let probe =
                    TwilioHealth.createWithHandler (TokenStore(Some "tok-123")) settings handler

                let! result = probe.Check()
                Expect.equal result Healthy "credential accepted"

                match handler.Seen with
                | [ request ] ->
                    Expect.equal request.Method "GET" "reads, never sends"

                    Expect.equal
                        request.Url
                        "https://api.twilio.com/2010-04-01/Accounts/ACtest.json"
                        "the account resource"

                    Expect.equal request.Authorization expectedAuth "the send's own credential"
                | other -> failtestf "expected one request, saw %d" other.Length

                Expect.equal probe.Kind Readiness "a readiness probe"
            }

            testCaseAsync "a rejected credential is Unhealthy; a vendor 5xx only Degraded"
            <| async {
                let! rejected =
                    (TwilioHealth.createWithHandler
                        (TokenStore(Some "tok-123"))
                        settings
                        (new StubHandler(HttpStatusCode.Unauthorized, "{}")))
                        .Check()

                Expect.isTrue
                    (match rejected with
                     | Unhealthy _ -> true
                     | _ -> false)
                    $"401 → Unhealthy, got %A{rejected}"

                let! outage =
                    (TwilioHealth.createWithHandler
                        (TokenStore(Some "tok-123"))
                        settings
                        (new StubHandler(HttpStatusCode.ServiceUnavailable, "{}")))
                        .Check()

                Expect.isTrue
                    (match outage with
                     | Degraded _ -> true
                     | _ -> false)
                    $"503 → Degraded, got %A{outage}"
            }

            testCaseAsync "a missing token is Unhealthy without calling Twilio"
            <| async {
                let handler = new StubHandler(HttpStatusCode.OK, "{}")
                let! result = (TwilioHealth.createWithHandler (TokenStore None) settings handler).Check()

                Expect.isTrue
                    (match result with
                     | Unhealthy message -> message.Contains "TWILIO_AUTH_TOKEN"
                     | _ -> false)
                    $"got %A{result}"

                Expect.isEmpty handler.Seen "no request"
            }

            test "the account URL is derived from a Messages endpoint override" {
                Expect.equal
                    (TwilioHealth.accountUrl {
                        settings with
                            EndpointOverride = Some "https://mock.example/2010-04-01/Accounts/ACtest/Messages.json"
                    })
                    "https://mock.example/2010-04-01/Accounts/ACtest.json"
                    "Messages.json → .json"
            }
        ]

        testList "preflight validator" [
            testCaseAsync "a complete configuration validates Ok"
            <| async {
                let! result = (TwilioValidator.create (TokenStore(Some "tok-123")) settings).Validate()
                Expect.equal result ConfigValidation.ValidationResult.Ok "token, SID and an E.164 sender"
            }

            testCaseAsync "a missing auth token is caught at startup"
            <| async {
                let! result = (TwilioValidator.create (TokenStore None) settings).Validate()

                match result with
                | ConfigValidation.ValidationResult.Error message ->
                    Expect.stringContains message "TWILIO_AUTH_TOKEN" "names the secret"
                | other -> failtestf "expected Error, got %A" other
            }

            testCaseAsync "an empty SID and a non-E.164 sender are both reported"
            <| async {
                let! result =
                    (TwilioValidator.create (TokenStore(Some "tok-123")) {
                        settings with
                            AccountSid = " "
                            FromWhatsAppNumber = "0207 946 0000"
                    })
                        .Validate()

                match result with
                | ConfigValidation.ValidationResult.Error message ->
                    Expect.stringContains message "account SID" "the SID"
                    Expect.stringContains message "E.164" "the sender"
                | other -> failtestf "expected Error, got %A" other
            }

            test "isE164" {
                Expect.isTrue (TwilioValidator.isE164 "+14155238886") "the sandbox number"
                Expect.isFalse (TwilioValidator.isE164 "14155238886") "no plus"
                Expect.isFalse (TwilioValidator.isE164 "+0123") "leading zero"
                Expect.isFalse (TwilioValidator.isE164 "+1 415 523 8886") "spaces"
            }
        ]

        testList "compose-time wiring" [
            testCaseAsync "the WhatsApp sink registers beside the Twilio SMS sink — one account, two kinds"
            <| async {
                let whatsApp = fst (delivered ())

                let sms =
                    NotificationChannels.Sms.Twilio.TwilioNotificationSink.create
                        (addressBook ())
                        (TokenStore(Some "tok-123"))
                        ({
                            AccountSid = "ACtest"
                            FromPhoneNumber = "+15555550000"
                            EndpointOverride = Some "https://localhost.invalid/Messages.json"
                        }
                        : NotificationChannels.Sms.Twilio.TwilioSettings)
                        None

                use dispatcher =
                    new TransactionalDispatcher.TransactionalDispatcher(
                        [ sms; whatsApp ],
                        FakeConfigStore(Map.empty),
                        CapturingAuditLog(),
                        CapturingLogger(),
                        TransactionalRetryPolicy.defaults,
                        NoOpActivitySink() :> IActivitySink
                    )

                Expect.isTrue
                    (dispatcher.HasSinkForKind(
                        NotificationKind.SinkKind.toWireString NotificationKind.SinkKind.WhatsApp
                    ))
                    "routed on SinkKind.WhatsApp"

                Expect.isTrue
                    (dispatcher.HasSinkForKind(NotificationKind.SinkKind.toWireString NotificationKind.SinkKind.Sms))
                    "the SMS sink is untouched"
            }

            test "a second WhatsApp sink is refused at compose time" {
                Expect.throws
                    (fun () ->
                        use _ =
                            new TransactionalDispatcher.TransactionalDispatcher(
                                [ fst (delivered ()); fst (delivered ()) ],
                                FakeConfigStore(Map.empty),
                                CapturingAuditLog(),
                                CapturingLogger(),
                                TransactionalRetryPolicy.defaults,
                                NoOpActivitySink() :> IActivitySink
                            )

                        ())
                    "one sink per kind"
            }
        ]
    ]