// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 6f.C - the `whatsapp-meta` inbound-webhook handler (customer messages
/// and delivery statuses) on the ToolUp.Webhooks substrate, and its routes.
module ToolUp.Platform.NotificationChannels.WhatsApp.MetaCloudInbound

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Giraffe
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.NotificationChannels.WhatsApp.MetaCloud
open ToolUp.Platform.Secrets
open ToolUp.Webhooks
open ToolUp.Webhooks.Server

// ─── Phase 6f.C — Meta WhatsApp inbound, on the Phase 238 substrate ──
//
// Meta delivers two things to the deployment's webhook: a customer's
// own messages (`value.messages`) and the delivery statuses of what the
// deployment sent (`value.statuses`). This module is an
// `IInboundWebhookHandler` of kind `whatsapp-meta` for the Phase 238
// `WebhookRegistry` — not a Giraffe route of its own. Verification
// (`X-Hub-Signature-256`, HMAC-SHA256 over the raw body, lower-case hex,
// keyed by the Meta App secret) is 238's `WebhookVerifier`, fail-closed
// and constant-time; the handler only ever sees a verified delivery.
//
// **What a message does.** Every contact whose WhatsApp number matches
// the sender has `LastInboundUtc` stamped through
// `IExternalContactStore.RecordInbound` — the store member
// `IExternalContactApi.RecordInbound` delegates to, called directly
// because a webhook carries no caller scope for the API's per-request
// resolution to find. That timestamp is what opens the 24-hour window
// `WhatsAppSendPolicyFilter` reads (Phase 827). The stamp is Meta's own
// message timestamp, and it never moves a contact's window BACKWARDS
// (an out-of-order redelivery of an older message is a no-op). One
// `_platform.whatsapp.inbound` event is appended per matched contact to
// the contact's scope, carrying the message id, type and — for a text
// message — its text, so a deployment can react to a reply.
//
// **What a status does.** Only `failed` is acted on: it appends
// `NotificationDeliveryFailed` (provider `meta-cloud`, the Meta error
// code and title, the correlation id the sink sent as
// `biz_opaque_callback_data`) in the scope of each contact holding the
// recipient's number — or `_platform` when none does. `sent`,
// `delivered` and `read` are acknowledged and ignored.
//
// **Dedup.** Meta sends no delivery-id header, so 238's header-keyed
// dedup never fires for this kind; the handler claims each message id
// (`msg:{wamid}`) and each failed status (`status:{wamid}:failed`) in
// the same `IWebhookDedupStore`, so a redelivered batch is a no-op item
// by item — including a batch that mixes new and replayed entries.
//
// **Which address books.** A business number is not scoped: the
// handler asks `contactScopes ()` which scopes' external address books
// to match the sender against (a single-tenant deployment passes one
// scope; a multi-team deployment enumerates its teams). The match reads
// `ExternalContact.OptionalWhatsAppNumber`, digits only — the same field
// `INotificationAddressBook.ResolveWhatsApp` sends to, so the number a
// message came from is exactly the number the window it opens governs.
//
// **The `GET` challenge.** Meta verifies a callback URL by `GET`ting it
// with `hub.mode=subscribe`, `hub.verify_token` and `hub.challenge`;
// 238's route is `POST`-only, so `routes` below answers the challenge
// (verify token from `ISecretStore`, constant-time compare) and hands
// every `POST` to 238's own `WebhookRoutes.routes` with the Meta scheme.
//
// Stateless between invocations (GP 12 rule 4): every dependency is
// injected, nothing is cached.

/// The integration kind — the registry key and the route segment
/// (`/webhooks/whatsapp-meta`).
[<Literal>]
let Kind = "whatsapp-meta"

/// The route this kind is served on.
[<Literal>]
let RoutePath = "/webhooks/whatsapp-meta"

/// `SourceModule` of the per-contact inbound-message event.
[<Literal>]
let InboundEventSource = "_platform.whatsapp.inbound"

/// `EventType` of the per-contact inbound-message event.
[<Literal>]
let InboundEventType = "WhatsAppInboundMessage"

/// Meta's signature scheme: `X-Hub-Signature-256: sha256=<hex>` over the
/// raw body, keyed by the App secret. No timestamp (Meta sends none, so
/// there is no freshness window — replay is stopped by the per-item
/// dedup) and no delivery-id header.
let scheme: WebhookScheme = {
    WebhookScheme.gitHubStyle with
        EventIdHeader = None
}

/// The `resolveSecret` for 238's route: the Meta App secret for this
/// kind, read from `ISecretStore` per delivery; `None` for any other
/// kind, which the route then refuses fail-closed.
let resolveSecret (secretStore: ISecretStore) : string -> Async<string option> =
    fun kind -> async {
        if kind = Kind then
            let! secret = secretStore.GetSecret(MetaWhatsAppSettings.SecretScope, MetaWhatsAppSettings.AppSecretKey)
            return secret |> Option.filter (String.IsNullOrWhiteSpace >> not)
        else
            return None
    }

// ── payload reading ──────────────────────────────────────────────────

type private InboundMessage = {
    Id: string
    From: string
    At: DateTime option
    MessageType: string
    Text: string option
}

type private FailedStatus = {
    Id: string
    Recipient: string
    Error: string
    CorrelationId: string option
}

let private str (element: JsonElement) (name: string) : string option =
    match element.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
    | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetRawText())
    | _ -> None

let private items (element: JsonElement) (name: string) : JsonElement list =
    match element.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> Seq.toList
    | _ -> []

let private unixSeconds (value: string option) : DateTime option =
    value
    |> Option.bind (fun s ->
        match Int64.TryParse s with
        | true, seconds -> Some(DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime)
        | false, _ -> None)

/// Every `value` object under `entry[].changes[]` whose `field` is
/// `messages` (the only field a WhatsApp Business Account subscription
/// delivers message and status traffic on).
let private messageValues (root: JsonElement) : JsonElement list = [
    for entry in items root "entry" do
        for change in items entry "changes" do
            if str change "field" = Some "messages" then
                match change.TryGetProperty "value" with
                | true, value when value.ValueKind = JsonValueKind.Object -> yield value
                | _ -> ()
]

let private readMessages (value: JsonElement) : InboundMessage list =
    items value "messages"
    |> List.choose (fun m ->
        match str m "id", str m "from" with
        | Some id, Some from ->
            let text =
                match m.TryGetProperty "text" with
                | true, t when t.ValueKind = JsonValueKind.Object -> str t "body"
                | _ -> None

            Some {
                Id = id
                From = toWaId from
                At = unixSeconds (str m "timestamp")
                MessageType = str m "type" |> Option.defaultValue "unknown"
                Text = text
            }
        | _ -> None)

let private readFailedStatuses (value: JsonElement) : FailedStatus list =
    items value "statuses"
    |> List.choose (fun s ->
        match str s "id", str s "status", str s "recipient_id" with
        | Some id, Some "failed", Some recipient ->
            let error =
                match items s "errors" with
                | first :: _ ->
                    let code = str first "code" |> Option.defaultValue "?"
                    let title = str first "title" |> Option.defaultValue "unknown error"

                    let detail =
                        match first.TryGetProperty "error_data" with
                        | true, data when data.ValueKind = JsonValueKind.Object -> str data "details"
                        | _ -> None

                    match detail with
                    | Some d -> sprintf "Meta WhatsApp status failed (%s): %s — %s [wamid %s]" code title d id
                    | None -> sprintf "Meta WhatsApp status failed (%s): %s [wamid %s]" code title id
                | [] -> sprintf "Meta WhatsApp status failed [wamid %s]" id

            Some {
                Id = id
                Recipient = toWaId recipient
                Error = error
                CorrelationId = str s "biz_opaque_callback_data"
            }
        | _ -> None)

// ── the handler ──────────────────────────────────────────────────────

/// The `whatsapp-meta` inbound handler. Register it in the deployment's
/// `WebhookRegistry` and mount `MetaCloudInbound.routes`.
type MetaWhatsAppInboundWebhookHandler
    (
        contacts: IExternalContactStore,
        dedup: IWebhookDedupStore,
        eventStore: IEventStore,
        auditLog: IAuditLog,
        contactScopes: unit -> Async<string list>,
        logger: ILogger option
    ) =

    let logWarn (message: string) =
        match logger with
        | Some l -> l.Warn message
        | None -> ()

    /// Every `(scopeId, contact)` whose WhatsApp number is `waId`.
    let contactsFor (waId: string) : Async<(string * ExternalContact) list> = async {
        let! scopes = contactScopes ()

        let! perScope =
            scopes
            |> List.distinct
            |> List.map (fun scopeId -> async {
                let! all = contacts.List scopeId

                return
                    all
                    |> List.filter (fun c ->
                        match c.OptionalWhatsAppNumber with
                        | Some number -> toWaId number = waId
                        | None -> false)
                    |> List.map (fun c -> scopeId, c)
            })
            |> Async.Sequential

        return perScope |> Array.toList |> List.concat
    }

    let inboundEventPayload (contact: ExternalContact) (message: InboundMessage) : string =
        let payload = JsonObject()
        payload["ContactId"] <- JsonValue.Create contact.Id
        payload["MessageId"] <- JsonValue.Create message.Id
        payload["MessageType"] <- JsonValue.Create message.MessageType

        payload["ReceivedAt"] <- JsonValue.Create((message.At |> Option.defaultValue DateTime.UtcNow).ToString("o"))

        match message.Text with
        | Some text -> payload["Text"] <- JsonValue.Create text
        | None -> ()

        payload.ToJsonString()

    let handleMessage (message: InboundMessage) : Async<bool> = async {
        let! claimed = dedup.TryClaim(Kind, "msg:" + message.Id)

        if not claimed then
            return false
        else
            let at = message.At |> Option.defaultValue DateTime.UtcNow
            let! matches = contactsFor message.From

            for scopeId, contact in matches do
                let newer =
                    match contact.LastInboundUtc with
                    | Some last -> at > last
                    | None -> true

                if newer then
                    let! recorded = contacts.RecordInbound(scopeId, contact.Id, at)

                    match recorded with
                    | Result.Ok _ -> ()
                    | Result.Error e ->
                        logWarn
                            $"[MetaWhatsAppInbound] RecordInbound failed for contact {contact.Id}: {ExternalContactError.describe e}"

                do!
                    eventStore.Write(
                        Events.create scopeId InboundEventSource InboundEventType (inboundEventPayload contact message)
                    )

            return true
    }

    let handleFailedStatus (status: FailedStatus) : Async<bool> = async {
        let! claimed = dedup.TryClaim(Kind, "status:" + status.Id + ":failed")

        if not claimed then
            return false
        else
            let! matches = contactsFor status.Recipient

            let targets =
                match matches with
                | [] -> [ "_platform", [] ]
                | _ ->
                    matches
                    |> List.groupBy fst
                    |> List.map (fun (scopeId, hits) ->
                        scopeId,
                        hits
                        |> List.map (fun (_, c) -> RecipientId.toAuditString (RecipientId.External c.Id)))

            for scopeId, recipients in targets do
                do!
                    auditLog.Record(
                        scopeId,
                        NotificationDeliveryFailed {
                            UserId = "system"
                            ScopeId = scopeId
                            NotificationKind = NotificationKind.SinkKind.toWireString NotificationKind.SinkKind.WhatsApp
                            Provider = "meta-cloud"
                            RecipientUserIds = recipients
                            Error = status.Error
                            Attempts = 1
                            CorrelationId = status.CorrelationId
                        }
                    )

            return true
    }

    interface IInboundWebhookHandler with
        member _.Kind = Kind

        member _.Handle(verified) = async {
            let parsed =
                try
                    Some(JsonDocument.Parse verified.Body)
                with _ ->
                    None

            match parsed with
            | None -> return Ignored "malformed payload: not JSON"
            | Some doc ->
                use doc = doc
                let root = doc.RootElement

                if
                    root.ValueKind <> JsonValueKind.Object
                    || str root "object" <> Some "whatsapp_business_account"
                then
                    return Ignored "not a whatsapp_business_account delivery"
                else
                    let values = messageValues root
                    let messages = values |> List.collect readMessages
                    let failures = values |> List.collect readFailedStatuses

                    let mutable acted = false

                    for message in messages do
                        let! handled = handleMessage message
                        acted <- acted || handled

                    for failure in failures do
                        let! handled = handleFailedStatus failure
                        acted <- acted || handled

                    if acted then
                        return Acknowledged
                    elif List.isEmpty messages && List.isEmpty failures then
                        return Ignored "no inbound message or failed status in the delivery"
                    else
                        return Ignored "every item in the delivery was already processed (replay)"
        }

/// Build the `whatsapp-meta` handler.
let create
    (contacts: IExternalContactStore)
    (dedup: IWebhookDedupStore)
    (eventStore: IEventStore)
    (auditLog: IAuditLog)
    (contactScopes: unit -> Async<string list>)
    (logger: ILogger option)
    : IInboundWebhookHandler =
    MetaWhatsAppInboundWebhookHandler(contacts, dedup, eventStore, auditLog, contactScopes, logger) :> _

/// A `contactScopes` for a deployment whose business number serves a
/// fixed set of scopes.
let fixedScopes (scopeIds: string list) : unit -> Async<string list> = fun () -> async { return scopeIds }

// ── routes ───────────────────────────────────────────────────────────

let private constantTimeEquals (a: string) (b: string) : bool =
    CryptographicOperations.FixedTimeEquals(
        ReadOnlySpan(Encoding.UTF8.GetBytes a),
        ReadOnlySpan(Encoding.UTF8.GetBytes b)
    )

/// Answer Meta's callback-verification `GET`: `hub.mode=subscribe` and a
/// `hub.verify_token` equal to the configured verify token echo
/// `hub.challenge` with 200; anything else is 403. No verify token
/// configured → 403 (fail-closed).
let verificationHandler (secretStore: ISecretStore) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
        let query (name: string) =
            match ctx.Request.Query.TryGetValue name with
            | true, v when not (String.IsNullOrEmpty(string v)) -> Some(string v)
            | _ -> None

        let! expected =
            secretStore.GetSecret(MetaWhatsAppSettings.SecretScope, MetaWhatsAppSettings.VerifyTokenSecretKey)
            |> Async.StartImmediateAsTask

        match query "hub.mode", query "hub.verify_token", query "hub.challenge", expected with
        | Some "subscribe", Some presented, Some challenge, Some expected when
            not (String.IsNullOrWhiteSpace expected)
            && constantTimeEquals presented expected
            ->
            return! (setStatusCode 200 >=> text challenge) next ctx
        | _ -> return! (setStatusCode 403 >=> text "verification refused") next ctx
    }

/// Mount the Meta WhatsApp webhook on `RoutePath`: the `GET`
/// verification challenge, and every `POST` through 238's own
/// verify → dedup → dispatch route with the Meta `scheme`. Gated on the
/// exact path, so it composes BEFORE a deployment's general
/// `WebhookRoutes.routes` mount (which carries a different scheme)
/// without shadowing it. `registry` must contain this kind's handler.
let routes (secretStore: ISecretStore) (dedup: IWebhookDedupStore) (registry: WebhookRegistry) : HttpHandler =
    route RoutePath
    >=> choose [
        GET >=> verificationHandler secretStore
        WebhookRoutes.routes scheme (resolveSecret secretStore) dedup registry
    ]