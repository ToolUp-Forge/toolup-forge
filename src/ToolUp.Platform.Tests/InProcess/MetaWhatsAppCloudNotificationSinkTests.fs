// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.MetaWhatsAppCloudNotificationSinkTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.EntityStore
open ToolUp.Platform.Secrets
open ToolUp.Platform.NotificationChannels.WhatsApp
open ToolUp.Platform.NotificationChannels.WhatsApp.MetaCloud
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.TransactionalDispatcherTests
open ToolUp.Webhooks
open ToolUp.Webhooks.Server

// ─── Phase 6f.C — the Meta WhatsApp Business Cloud companion ─────────
//
// No live credential anywhere in this file. The outbound half runs
// against a `StubHandler` transport behind `EndpointOverride`, with the
// token in a real `FileSecretStore` and recipients resolved by the real
// blob-backed address book over the real entity-backed contact store —
// so consent gating is the shipped `ResolveWhatsApp`, not a fake. The
// inbound half posts signed payloads through Phase 238's own
// `WebhookRoutes.routes` (mounted by `MetaCloudInbound.routes`).
//
//   1. `INotificationSinkContract.tests` + 827's `whatsAppTests`.
//   2. The request the sink sends, pinned byte-for-byte: URL, bearer
//      token, template JSON (header / body / button split by the
//      registry's arity, default language) and text JSON.
//   3. Failure classification and the refusals that never reach Meta.
//   4. Inbound: a signed `messages` delivery moves `LastInboundUtc`,
//      appends the inbound event, and a replay is deduplicated; a
//      tampered signature is rejected with no effect; a `failed` status
//      audits `NotificationDeliveryFailed`; the `GET` challenge.
//   5. Health probe and validator.

let private scope = "team-6fC"
let private actor = "admin-1"
let private phoneNumberId = "106540352242922"
let private stubBase = "https://graph.test.invalid"
let private accessToken = "EAAG-test-system-user-token"
let private appSecret = "meta-app-secret-test"
let private verifyToken = "verify-me-6fC"

/// A transport that records every request and answers from a script
/// (the last scripted response repeats).
type private StubHandler(responses: (HttpStatusCode * string) list) =
    inherit HttpMessageHandler()

    let seen = ConcurrentQueue<string * string * string * string>()
    let mutable remaining = responses

    member _.Requests = List.ofSeq seen

    override _.SendAsync(request, _) = task {
        let! body =
            match request.Content with
            | null -> Task.FromResult ""
            | content -> content.ReadAsStringAsync()

        let auth =
            match request.Headers.Authorization with
            | null -> ""
            | a -> a.Scheme + " " + a.Parameter

        seen.Enqueue(request.Method.Method, string request.RequestUri, auth, body)

        let status, payload =
            match remaining with
            | [ only ] -> only
            | head :: rest ->
                remaining <- rest
                head
            | [] -> HttpStatusCode.OK, """{"messages":[{"id":"wamid.default"}]}"""

        let response = new HttpResponseMessage(status)
        response.Content <- new StringContent(payload, Encoding.UTF8, "application/json")
        return response
    }

let private okResponse =
    HttpStatusCode.OK,
    """{"messaging_product":"whatsapp","contacts":[{"input":"447700900123","wa_id":"447700900123"}],"messages":[{"id":"wamid.HBgM"}]}"""

/// A registry holding the templates these tests send.
type private StubTemplates() =
    interface IWhatsAppTemplateRegistry with
        member _.GetTemplate name = async {
            return
                match name with
                | "appointment_reminder" ->
                    Some {
                        Name = name
                        Languages = [ "en_GB" ]
                        HeaderParameterCount = 0
                        BodyParameterCount = 2
                        ButtonParameterCount = 0
                    }
                | "order_update" ->
                    Some {
                        Name = name
                        Languages = [ "cy"; "en_GB" ]
                        HeaderParameterCount = 1
                        BodyParameterCount = 2
                        ButtonParameterCount = 1
                    }
                | _ -> None
        }

let private settings: MetaWhatsAppSettings = {
    MetaWhatsAppSettings.create phoneNumberId with
        EndpointOverride = Some stubBase
}

let private freshSecrets (secrets: (string * string) list) : ISecretStore =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-tests-meta-whatsapp-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    let store = FileSecretStore.FileSecretStore(root) :> ISecretStore

    for key, value in secrets do
        match
            store.SetSecret(MetaWhatsAppSettings.SecretScope, key, value)
            |> Async.RunSynchronously
        with
        | Ok() -> ()
        | Error e -> failtestf "could not seed secret %s: %s" key e

    store

let private freshContacts () : IExternalContactStore =
    let blob = InMemoryBlobStorage() :> IBlobStorage
    let dos = DataObjectStore.DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    registry.Register ExternalContactStore.registration

    let entities =
        BlobEntityStore(dos, blob, registry, None) :> IEntityStore.IEntityStore

    ExternalContactStore.entityBacked entities None

/// File a contact with a WhatsApp number, optionally consented on WhatsApp.
let private seedContactIn
    (scopeId: string)
    (store: IExternalContactStore)
    (name: string)
    (number: string)
    (consented: bool)
    =
    async {
        let request: CreateExternalContactRequest = {
            DisplayName = name
            EmailAddress = None
            PhoneNumber = None
            WhatsAppNumber = Some number
            Owner = ContactOwner.Team "team-1"
            Tags = []
            Notes = None
        }

        let! created = store.Create(scopeId, actor, ContactOwner.Team "team-1", request)

        let contact =
            match created with
            | Ok c -> c
            | Error e -> failtestf "could not seed a contact: %s" (ExternalContactError.describe e)

        if consented then
            let consent: OptInRecord = {
                GrantedAt = DateTime.UtcNow
                Source = "manual-admin-entry"
                ExpiresAt = None
            }

            let! updated = store.RecordOptIn(scopeId, actor, contact.Id, NotificationKind.SinkKind.WhatsApp, consent)

            return
                match updated with
                | Ok c -> c
                | Error e -> failtestf "could not seed a consent: %s" (ExternalContactError.describe e)
        else
            return contact
    }
    |> Async.RunSynchronously

let private seedContact = seedContactIn scope

type private Fixture = {
    Sink: INotificationSink
    Stub: StubHandler
    Contacts: IExternalContactStore
    Consented: ExternalContact
    Unconsented: ExternalContact
}

let private fixtureIn
    (scopeId: string)
    (secrets: (string * string) list)
    (responses: (HttpStatusCode * string) list)
    : Fixture =
    let contacts = freshContacts ()
    let consented = seedContactIn scopeId contacts "Consented" "+44 7700 900123" true
    let unconsented = seedContactIn scopeId contacts "No consent" "+447700900999" false
    let blob = InMemoryBlobStorage() :> IBlobStorage

    let book =
        NotificationAddressBook.BlobBackedNotificationAddressBook(blob, None, Some contacts) :> INotificationAddressBook

    let stub = new StubHandler(responses)

    let sink =
        MetaWhatsAppCloudNotificationSink.createWith book (freshSecrets secrets) (StubTemplates()) settings stub None

    {
        Sink = sink
        Stub = stub
        Contacts = contacts
        Consented = consented
        Unconsented = unconsented
    }

let private fixtureWith = fixtureIn scope

let private defaultSecrets = [ MetaWhatsAppSettings.AccessTokenSecretKey, accessToken ]

let private fixture () =
    fixtureWith [ MetaWhatsAppSettings.AccessTokenSecretKey, accessToken ] [ okResponse ]

let private whatsAppEnvelope (recipients: RecipientId list) : WhatsAppEnvelope = {
    Recipients = recipients
    TemplateName = None
    TemplateLanguage = None
    TemplateParameters = []
    Body = Some "Thanks — see you tomorrow."
    Metadata = Map.empty
    CorrelationId = None
}

let private send (sink: INotificationSink) (whatsApp: WhatsAppEnvelope) : SinkResult =
    let envelope = NotificationEnvelope.create scope (TransactionalWhatsApp whatsApp)
    sink.Send(envelope.ScopeId, envelope) |> Async.RunSynchronously

// ── inbound harness ──────────────────────────────────────────────────

let private hexHmac (secret: string) (payload: string) : string =
    use h = new HMACSHA256(Encoding.UTF8.GetBytes secret)
    Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes payload)).ToLowerInvariant()

let private services =
    lazy (ServiceCollection().AddLogging().BuildServiceProvider() :> IServiceProvider)

/// Run one request through `MetaCloudInbound.routes`; returns
/// `(status, responseBody)`, or `None` when the routes did not match.
let private invoke
    (handler: Giraffe.Core.HttpHandler)
    (httpMethod: string)
    (path: string)
    (query: string)
    (headers: (string * string) list)
    (body: string)
    : (int * string) option =
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.Force()
    ctx.Request.Method <- httpMethod
    ctx.Request.Path <- PathString path
    ctx.Request.QueryString <- QueryString query

    for name, value in headers do
        ctx.Request.Headers[name] <- StringValues value

    ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes body)
    let responseBody = new MemoryStream()
    ctx.Response.Body <- responseBody

    let result =
        handler (fun c -> Task.FromResult(Some c)) ctx
        |> Async.AwaitTask
        |> Async.RunSynchronously

    match result with
    | None -> None
    | Some _ -> Some(ctx.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()))

type private InboundFixture = {
    Routes: Giraffe.Core.HttpHandler
    Contacts: IExternalContactStore
    Events: IEventStore
    Audit: CapturingAuditLog
    Contact: ExternalContact
}

let private inboundFixture () : InboundFixture =
    let contacts = freshContacts ()
    let contact = seedContact contacts "Replier" "+447700900123" true
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let audit = CapturingAuditLog()
    let dedup = InMemoryWebhookDedupStore() :> IWebhookDedupStore

    let secrets =
        freshSecrets [
            MetaWhatsAppSettings.AppSecretKey, appSecret
            MetaWhatsAppSettings.VerifyTokenSecretKey, verifyToken
        ]

    let handler =
        MetaCloudInbound.create contacts dedup events audit (MetaCloudInbound.fixedScopes [ scope ]) None

    {
        Routes = MetaCloudInbound.routes secrets dedup (WebhookRegistry.ofList [ handler ])
        Contacts = contacts
        Events = events
        Audit = audit
        Contact = contact
    }

let private messagesPayload (messageId: string) (from: string) (unixSeconds: int64) =
    sprintf
        """{"object":"whatsapp_business_account","entry":[{"id":"WABA_ID","changes":[{"field":"messages","value":{"messaging_product":"whatsapp","metadata":{"display_phone_number":"15550001234","phone_number_id":"%s"},"contacts":[{"profile":{"name":"Replier"},"wa_id":"%s"}],"messages":[{"from":"%s","id":"%s","timestamp":"%d","type":"text","text":{"body":"Yes please"}}]}}]}]}"""
        phoneNumberId
        from
        from
        messageId
        unixSeconds

let private failedStatusPayload (messageId: string) (recipient: string) =
    sprintf
        """{"object":"whatsapp_business_account","entry":[{"id":"WABA_ID","changes":[{"field":"messages","value":{"messaging_product":"whatsapp","metadata":{"phone_number_id":"%s"},"statuses":[{"id":"%s","status":"failed","timestamp":"1790000000","recipient_id":"%s","biz_opaque_callback_data":"corr-6fC","errors":[{"code":131047,"title":"Re-engagement message","error_data":{"details":"More than 24 hours have passed"}}]}]}}]}]}"""
        phoneNumberId
        messageId
        recipient

let private post (routes: Giraffe.Core.HttpHandler) (signature: string) (body: string) =
    invoke routes "POST" MetaCloudInbound.RoutePath "" [ "X-Hub-Signature-256", signature ] body

let private contactNow (store: IExternalContactStore) (contactId: string) : ExternalContact =
    match store.Get(scope, contactId) |> Async.RunSynchronously with
    | Ok c -> c
    | Error e -> failtestf "could not read contact back: %s" (ExternalContactError.describe e)

let private inboundEvents (events: IEventStore) =
    events.ReadBySource(scope, MetaCloudInbound.InboundEventSource)
    |> Async.RunSynchronously

let tests =
    testList "MetaWhatsAppCloudNotificationSink (Phase 6f.C)" [
        INotificationSinkContract.tests
            "MetaWhatsAppCloudNotificationSink"
            (fun () -> (fixture ()).Sink)
            (fun scopeId ->
                let f = fixtureIn scopeId defaultSecrets [ okResponse ]

                NotificationEnvelope.create
                    scopeId
                    (TransactionalWhatsApp(whatsAppEnvelope [ RecipientId.External f.Consented.Id ])))

        // 827's WhatsApp cases need a recipient the sink's address book
        // resolves, in the scope the pack sends under ("scope-test"). The
        // recipient is bound through one shared fixture seeded there.
        (let shared = fixtureIn "scope-test" defaultSecrets [ okResponse ]

         INotificationSinkContract.whatsAppTests
             "MetaWhatsAppCloudNotificationSink"
             (fun () -> shared.Sink)
             (RecipientId.External shared.Consented.Id)
             INotificationSinkContract.SupportsTemplates)

        testList "the request the sink sends" [
            test "a template message: URL, bearer token, and the components split by the registry's arity" {
                let f = fixture ()

                let result =
                    send f.Sink {
                        whatsAppEnvelope [ RecipientId.External f.Consented.Id ] with
                            TemplateName = Some "order_update"
                            TemplateParameters = [ "A-1042"; "Alex"; "Thursday"; "track/A-1042" ]
                            Body = None
                            CorrelationId = Some "corr-6fC"
                    }

                Expect.equal result (SinkResult.Delivered(Some "wamid.HBgM")) "the wamid is the vendor message id"

                match f.Stub.Requests with
                | [ httpMethod, uri, auth, body ] ->
                    Expect.equal httpMethod "POST" "a send is a POST"

                    Expect.equal
                        uri
                        (sprintf "%s/v26.0/%s/messages" stubBase phoneNumberId)
                        "the override replaces the base only; the pinned version and the phone number id stay in the path"

                    Expect.equal auth ("Bearer " + accessToken) "the System User token rides as a bearer token"

                    // The envelope named no language: the template's FIRST
                    // registered language is used. Header takes one
                    // parameter, body two, the one button parameter
                    // addresses dynamic-URL button 0. `to` is the E.164
                    // number reduced to digits.
                    Expect.equal
                        body
                        """{"messaging_product":"whatsapp","recipient_type":"individual","to":"447700900123","type":"template","template":{"name":"order_update","language":{"code":"cy"},"components":[{"type":"header","parameters":[{"type":"text","text":"A-1042"}]},{"type":"body","parameters":[{"type":"text","text":"Alex"},{"type":"text","text":"Thursday"}]},{"type":"button","sub_type":"url","index":"0","parameters":[{"type":"text","text":"track/A-1042"}]}]},"biz_opaque_callback_data":"corr-6fC"}"""
                        "the template JSON is pinned"
                | other -> failtestf "expected exactly one request, got %A" other
            }

            test "a free-form message is a text message" {
                let f = fixture ()

                let result = send f.Sink (whatsAppEnvelope [ RecipientId.External f.Consented.Id ])

                Expect.equal result (SinkResult.Delivered(Some "wamid.HBgM")) "delivered"

                match f.Stub.Requests with
                | [ _, _, _, body ] ->
                    Expect.equal
                        body
                        """{"messaging_product":"whatsapp","recipient_type":"individual","to":"447700900123","type":"text","text":{"body":"Thanks \u2014 see you tomorrow."}}"""
                        "the text JSON is pinned (non-ASCII escaped, as System.Text.Json writes it); no correlation id means no biz_opaque_callback_data"
                | other -> failtestf "expected exactly one request, got %A" other
            }

            test "one request per recipient — there is no batch endpoint" {
                let f = fixture ()
                let second = seedContact f.Contacts "Second" "+447700900124" true

                let result =
                    send
                        f.Sink
                        (whatsAppEnvelope [ RecipientId.External f.Consented.Id; RecipientId.External second.Id ])

                Expect.isTrue
                    (match result with
                     | SinkResult.Delivered _ -> true
                     | _ -> false)
                    "both delivered"

                let recipients =
                    f.Stub.Requests
                    |> List.map (fun (_, _, _, body) ->
                        body.Contains "\"to\":\"447700900123\"", body.Contains "\"to\":\"447700900124\"")

                Expect.equal recipients [ true, false; false, true ] "one POST per recipient, in order"
            }
        ]

        testList "outcomes that never reach Meta, and how Meta's refusals classify" [
            test "a recipient with no live WhatsApp consent is skipped, nothing is sent" {
                let f = fixture ()

                let result =
                    send f.Sink (whatsAppEnvelope [ RecipientId.External f.Unconsented.Id ])

                Expect.equal result (SinkResult.Skipped "no_addressable_recipients") "skipped"
                Expect.isEmpty f.Stub.Requests "no request left the process"
            }

            test "no access token in the secret store is a PermanentFailure before any request" {
                let f = fixtureWith [] [ okResponse ]

                match send f.Sink (whatsAppEnvelope [ RecipientId.External f.Consented.Id ]) with
                | SinkResult.PermanentFailure message ->
                    Expect.stringContains message MetaWhatsAppSettings.AccessTokenSecretKey "names the secret"
                | other -> failtestf "expected PermanentFailure, got %A" other

                Expect.isEmpty f.Stub.Requests "no request left the process"
            }

            test "a template the registry does not hold is a PermanentFailure before any request" {
                let f = fixture ()

                match
                    send f.Sink {
                        whatsAppEnvelope [ RecipientId.External f.Consented.Id ] with
                            TemplateName = Some "never_approved"
                            Body = None
                    }
                with
                | SinkResult.PermanentFailure message ->
                    Expect.stringContains message "whatsapp_template_unknown" "names the rule"
                | other -> failtestf "expected PermanentFailure, got %A" other

                Expect.isEmpty f.Stub.Requests "no request left the process"
            }

            test "5xx and Meta's throttling codes are transient; a coded refusal is permanent" {
                let classify (status: HttpStatusCode) (body: string) =
                    let f =
                        fixtureWith [ MetaWhatsAppSettings.AccessTokenSecretKey, accessToken ] [ status, body ]

                    send f.Sink (whatsAppEnvelope [ RecipientId.External f.Consented.Id ])

                let isTransient =
                    function
                    | SinkResult.TransientFailure _ -> true
                    | _ -> false

                let isPermanent =
                    function
                    | SinkResult.PermanentFailure _ -> true
                    | _ -> false

                Expect.isTrue (isTransient (classify HttpStatusCode.InternalServerError "{}")) "500 is transient"
                Expect.isTrue (isTransient (classify HttpStatusCode.TooManyRequests "{}")) "429 is transient"

                Expect.isTrue
                    (isTransient (
                        classify HttpStatusCode.BadRequest """{"error":{"code":130429,"message":"Rate limit hit"}}"""
                    ))
                    "Meta's rate-limit code on a 400 is transient"

                Expect.isTrue
                    (isPermanent (
                        classify
                            HttpStatusCode.BadRequest
                            """{"error":{"code":131026,"message":"Message undeliverable"}}"""
                    ))
                    "an undeliverable number is permanent"

                Expect.isTrue
                    (isPermanent (classify HttpStatusCode.Unauthorized """{"error":{"code":190,"message":"expired"}}"""))
                    "a rejected token is permanent until rotated"
            }
        ]

        testList "inbound on the Phase 238 route" [
            test
                "a signed messages delivery moves LastInboundUtc and appends the inbound event; a replay is deduplicated" {
                let f = inboundFixture ()
                let sentAt = 1_790_000_000L
                let body = messagesPayload "wamid.IN1" "447700900123" sentAt
                let signature = "sha256=" + hexHmac appSecret body

                Expect.isNone f.Contact.LastInboundUtc "no inbound yet"

                let first = post f.Routes signature body
                Expect.equal first (Some(200, "ok")) "the verified delivery is acknowledged"

                let after = contactNow f.Contacts f.Contact.Id

                Expect.equal
                    after.LastInboundUtc
                    (Some(DateTimeOffset.FromUnixTimeSeconds(sentAt).UtcDateTime))
                    "LastInboundUtc is Meta's message timestamp — this is what opens the 24-hour window"

                let events = inboundEvents f.Events
                Expect.equal events.Length 1 "one inbound event for the one matched contact"
                Expect.equal events.Head.EventType MetaCloudInbound.InboundEventType "event type"
                Expect.stringContains events.Head.Payload "\"MessageId\":\"wamid.IN1\"" "carries the message id"
                Expect.stringContains events.Head.Payload "\"Text\":\"Yes please\"" "and the text"

                let replay = post f.Routes signature body
                Expect.equal replay (Some(200, "ok (ignored)")) "the replay is acknowledged but not acted on"
                Expect.equal (inboundEvents f.Events).Length 1 "the replay appended nothing"
            }

            test "an older message delivered late never moves the window backwards" {
                let f = inboundFixture ()
                let newer = messagesPayload "wamid.NEW" "447700900123" 1_790_000_600L
                let older = messagesPayload "wamid.OLD" "447700900123" 1_790_000_000L
                post f.Routes ("sha256=" + hexHmac appSecret newer) newer |> ignore
                post f.Routes ("sha256=" + hexHmac appSecret older) older |> ignore

                Expect.equal
                    (contactNow f.Contacts f.Contact.Id).LastInboundUtc
                    (Some(DateTimeOffset.FromUnixTimeSeconds(1_790_000_600L).UtcDateTime))
                    "the later timestamp stands"
            }

            test "a tampered signature is rejected before any effect" {
                let f = inboundFixture ()
                let body = messagesPayload "wamid.T1" "447700900123" 1_790_000_000L
                let signature = "sha256=" + hexHmac appSecret body
                let tampered = body.Replace("Yes please", "No thanks")

                let result = post f.Routes signature tampered
                Expect.equal result (Some(400, "verification failed")) "fail-closed"
                Expect.isNone (contactNow f.Contacts f.Contact.Id).LastInboundUtc "LastInboundUtc did not move"
                Expect.isEmpty (inboundEvents f.Events) "no event"

                let wrongKey = post f.Routes ("sha256=" + hexHmac "not-the-app-secret" body) body
                Expect.equal wrongKey (Some(400, "verification failed")) "a signature under another key is rejected too"

                let unsigned = invoke f.Routes "POST" MetaCloudInbound.RoutePath "" [] body
                Expect.equal unsigned (Some(400, "verification failed")) "and so is an unsigned delivery"
                Expect.isNone (contactNow f.Contacts f.Contact.Id).LastInboundUtc "still no effect"
            }

            test "a failed status appends NotificationDeliveryFailed in the recipient's scope, once" {
                let f = inboundFixture ()
                let body = failedStatusPayload "wamid.OUT1" "447700900123"
                let signature = "sha256=" + hexHmac appSecret body

                Expect.equal (post f.Routes signature body) (Some(200, "ok")) "acknowledged"
                Expect.equal (post f.Routes signature body) (Some(200, "ok (ignored)")) "the replay is a no-op"

                match f.Audit.Recorded with
                | [ auditScope, NotificationDeliveryFailed payload ] ->
                    Expect.equal auditScope scope "audited in the contact's scope"
                    Expect.equal payload.Provider "meta-cloud" "provider"
                    Expect.equal payload.NotificationKind "WhatsApp" "kind"
                    Expect.equal payload.RecipientUserIds [ "external:" + f.Contact.Id ] "the contact, in audit form"
                    Expect.equal payload.CorrelationId (Some "corr-6fC") "the correlation id Meta echoed back"
                    Expect.stringContains payload.Error "131047" "Meta's error code"
                    Expect.stringContains payload.Error "wamid.OUT1" "and the message id"
                | other -> failtestf "expected one NotificationDeliveryFailed, got %A" other

                Expect.isNone (contactNow f.Contacts f.Contact.Id).LastInboundUtc "a status is not an inbound message"
            }

            test "the GET challenge echoes hub.challenge for the right verify token, and refuses otherwise" {
                let f = inboundFixture ()

                let challenge token =
                    invoke
                        f.Routes
                        "GET"
                        MetaCloudInbound.RoutePath
                        (sprintf "?hub.mode=subscribe&hub.verify_token=%s&hub.challenge=1158201444" token)
                        []
                        ""

                Expect.equal (challenge verifyToken) (Some(200, "1158201444")) "the challenge is echoed"
                Expect.equal (challenge "wrong") (Some(403, "verification refused")) "a wrong token is refused"
            }

            test "the Meta mount is gated on its own path, so a general webhook mount still sees other kinds" {
                let f = inboundFixture ()
                let other = invoke f.Routes "POST" "/webhooks/stripe" "" [] "{}"
                Expect.isNone other "the Meta routes do not answer for another kind"
            }
        ]

        testList "health probe and validator" [
            test "the probe reads the phone-number node with the token; 401 is Unhealthy" {
                let stub = new StubHandler([ HttpStatusCode.OK, """{"id":"106540352242922"}""" ])

                let secrets =
                    freshSecrets [ MetaWhatsAppSettings.AccessTokenSecretKey, accessToken ]

                let probe = MetaCloudHealth.createWith secrets settings stub
                Expect.equal (probe.Check() |> Async.RunSynchronously) HealthChecks.Healthy "healthy"

                match stub.Requests with
                | [ "GET", uri, auth, _ ] ->
                    Expect.equal uri (sprintf "%s/v26.0/%s" stubBase phoneNumberId) "the phone-number node"
                    Expect.equal auth ("Bearer " + accessToken) "authenticated"
                | other -> failtestf "expected one GET, got %A" other

                let rejected =
                    MetaCloudHealth.createWith secrets settings (new StubHandler([ HttpStatusCode.Unauthorized, "{}" ]))

                match rejected.Check() |> Async.RunSynchronously with
                | HealthChecks.Unhealthy _ -> ()
                | other -> failtestf "expected Unhealthy, got %A" other

                let missing =
                    MetaCloudHealth.createWith (freshSecrets []) settings (new StubHandler([ okResponse ]))

                match missing.Check() |> Async.RunSynchronously with
                | HealthChecks.Unhealthy message ->
                    Expect.stringContains message MetaWhatsAppSettings.AccessTokenSecretKey "names the secret"
                | other -> failtestf "expected Unhealthy, got %A" other
            }

            test "the validator: numeric phone id, token, app secret when inbound, HTTPS PublicBaseUrl in production" {
                let validate
                    (secrets: (string * string) list)
                    (s: MetaWhatsAppSettings)
                    (config: ServerConfig)
                    inbound
                    =
                    (MetaCloudValidator.create (freshSecrets secrets) s config inbound).Validate()
                    |> Async.RunSynchronously

                let allSecrets = [
                    MetaWhatsAppSettings.AccessTokenSecretKey, accessToken
                    MetaWhatsAppSettings.AppSecretKey, appSecret
                    MetaWhatsAppSettings.VerifyTokenSecretKey, verifyToken
                ]

                let production = {
                    ServerConfig.defaults with
                        Surfaces = [ SurfaceProfile.team ]
                        PublicBaseUrl = Some "https://app.example.com"
                }

                Expect.equal
                    (validate allSecrets settings production true)
                    ConfigValidation.Ok
                    "a complete production configuration passes"

                let isError =
                    function
                    | ConfigValidation.Error _ -> true
                    | _ -> false

                Expect.isTrue
                    (isError (
                        validate
                            allSecrets
                            {
                                settings with
                                    PhoneNumberId = "+44 7700 900123"
                            }
                            production
                            true
                    ))
                    "a phone NUMBER in the phone-number-id slot refuses the boot"

                Expect.isTrue
                    (isError (validate [ MetaWhatsAppSettings.AppSecretKey, appSecret ] settings production true))
                    "no access token refuses the boot"

                Expect.isTrue
                    (isError (
                        validate [ MetaWhatsAppSettings.AccessTokenSecretKey, accessToken ] settings production true
                    ))
                    "the inbound handler without an app secret refuses the boot"

                Expect.equal
                    (validate [ MetaWhatsAppSettings.AccessTokenSecretKey, accessToken ] settings production false)
                    ConfigValidation.Ok
                    "without the inbound handler, the app secret is not required"

                let plainHttp = {
                    production with
                        PublicBaseUrl = Some "http://app.example.com"
                }

                match validate allSecrets settings plainHttp true with
                | ConfigValidation.Error message -> Expect.stringContains message "not HTTPS" "names the rule"
                | other -> failtestf "a non-HTTPS PublicBaseUrl in production must refuse the boot, got %A" other

                let demo = {
                    plainHttp with
                        Surfaces = ServerConfig.defaults.Surfaces
                }

                match validate allSecrets settings demo true with
                | ConfigValidation.Warning message -> Expect.stringContains message "not HTTPS" "warned, not refused"
                | other -> failtestf "outside production a non-HTTPS PublicBaseUrl warns, got %A" other
            }
        ]
    ]