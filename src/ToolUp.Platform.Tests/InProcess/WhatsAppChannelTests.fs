// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.WhatsAppChannelTests

open System
open System.Collections.Concurrent
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.EntityStore
open ToolUp.Platform.Tracing
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.TransactionalDispatcherTests

// ─── Phase 827 — WhatsApp as a channel ───────────────────────────────
//
// The vendor-neutral half of WhatsApp: the `SinkKind` case, the
// `Notification` arm, the template registry, and the two WhatsApp
// Business rules the server enforces before any sink runs. There is no
// real WhatsApp sink in this repository yet, so every sink here is an
// in-memory fake — which is exactly what the contract pack is for.
//
// The rule tests put a RECORDING channel directly beneath the policy
// filter: what it receives is what the filter admitted, so "refused
// before any sink runs" is observed rather than inferred.

// ─── Doubles ─────────────────────────────────────────────────────────

type private RecordingChannel() =
    let published = ConcurrentQueue<string * Notification>()

    member _.Published = published |> Seq.toList

    member this.RecipientsSeen =
        this.Published
        |> List.collect (fun (_, n) -> fst (NotificationPreferenceFilter.recipientsOf n))

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue((scopeId, notification)) }
        member _.Subscribe(_, _) = async { return (Guid.NewGuid(): NotificationSubscriptionId) }
        member _.Unsubscribe _ = async { () }

type private RecordingAuditLog() =
    let events = ConcurrentQueue<string * AuditEvent>()

    member this.Refusals =
        events
        |> Seq.toList
        |> List.choose (fun (_, e) ->
            match e with
            | NotificationDeliveryRefused p -> Some p
            | _ -> None)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { events.Enqueue((scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

/// An in-memory `IWhatsAppTemplateRegistry`: a fixed set of templates,
/// and a count of lookups so a test can show the registry was (or was
/// not) consulted.
type private InMemoryTemplateRegistry(templates: WhatsAppTemplateDescriptor list) =
    let mutable lookups = 0

    member _.Lookups = lookups

    interface IWhatsAppTemplateRegistry with
        member _.GetTemplate name = async {
            Interlocked.Increment(&lookups) |> ignore
            return templates |> List.tryFind (fun t -> t.Name = name)
        }

type private ThrowingTemplateRegistry() =
    interface IWhatsAppTemplateRegistry with
        member _.GetTemplate _ = async { return raise (InvalidOperationException "registry offline") }

/// An in-memory WhatsApp sink over a fake transport: records what it was
/// asked to send and classifies it the way a vendor sink must.
type private InMemoryWhatsAppSink(supportsTemplates: bool) =
    let sent = ConcurrentQueue<WhatsAppEnvelope>()

    member _.Sent = sent |> Seq.toList

    interface INotificationSink with
        member _.Kind = NotificationKind.SinkKind.WhatsApp
        member _.Provider = "in-memory"

        member _.Send(_, envelope) = async {
            match envelope.Notification with
            | TransactionalWhatsApp whatsApp ->
                match whatsApp.TemplateName with
                | Some _ when not supportsTemplates ->
                    return SinkResult.PermanentFailure "templates are not supported by this sink"
                | _ ->
                    sent.Enqueue whatsApp
                    return SinkResult.Delivered(Some(Guid.NewGuid().ToString "N"))
            | _ -> return SinkResult.Skipped "not a WhatsApp notification"
        }

// ─── Fixtures ────────────────────────────────────────────────────────

let private actor = "admin-1"
let private scope = "team-827"
let private now = DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc)

let private reminder: WhatsAppTemplateDescriptor = {
    Name = "appointment_reminder"
    Languages = [ "en_GB"; "cy" ]
    HeaderParameterCount = 1
    BodyParameterCount = 2
    ButtonParameterCount = 0
}

/// A fresh entity-backed contact store over in-memory blob storage —
/// the shipped store, registered as a composed deployment registers it.
let private freshStore () : IExternalContactStore =
    let blob = InMemoryBlobStorage() :> IBlobStorage
    let dos = DataObjectStore.DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    registry.Register ExternalContactStore.registration

    let entities =
        BlobEntityStore(dos, blob, registry, None) :> IEntityStore.IEntityStore

    ExternalContactStore.entityBacked entities None

/// Numbers are unique per seeded contact: the store refuses a second
/// contact with the same phone number in one address book.
let private nextNumber = ref 0

/// File a contact with a WhatsApp number, record the given consents, and
/// — when `inboundAt` is set — record an inbound message at that instant.
let private seedContact
    (store: IExternalContactStore)
    (name: string)
    (consents: NotificationKind.SinkKind list)
    (inboundAt: DateTime option)
    : Async<string> =
    async {
        let n = Interlocked.Increment(&nextNumber.contents)

        let request: CreateExternalContactRequest = {
            DisplayName = name
            EmailAddress = None
            PhoneNumber = Some $"+4477009%05d{n}"
            WhatsAppNumber = Some $"+4477008%05d{n}"
            Owner = ContactOwner.Team "team-1"
            Tags = []
            Notes = None
        }

        let! created = store.Create(scope, actor, ContactOwner.Team "team-1", request)

        let contact =
            match created with
            | Ok c -> c
            | Error e -> failtestf "could not seed a contact: %s" (ExternalContactError.describe e)

        for channel in consents do
            let! recorded =
                store.RecordOptIn(
                    scope,
                    actor,
                    contact.Id,
                    channel,
                    {
                        GrantedAt = now.AddDays -30.0
                        Source = "manual-admin-entry"
                        ExpiresAt = None
                    }
                )

            match recorded with
            | Ok _ -> ()
            | Error e -> failtestf "could not seed a consent: %s" (ExternalContactError.describe e)

        match inboundAt with
        | None -> ()
        | Some at ->
            match! store.RecordInbound(scope, contact.Id, at) with
            | Ok _ -> ()
            | Error e -> failtestf "could not record an inbound: %s" (ExternalContactError.describe e)

        return contact.Id
    }

let private freeForm (recipients: RecipientId list) : Notification =
    TransactionalWhatsApp {
        Recipients = recipients
        TemplateName = None
        TemplateLanguage = None
        TemplateParameters = []
        Body = Some "Thanks for your message — see you at 10."
        Metadata = Map.empty
        CorrelationId = Some "corr-827"
    }

let private templated
    (recipients: RecipientId list)
    (name: string)
    (language: string option)
    (parameters: string list)
    =
    TransactionalWhatsApp {
        Recipients = recipients
        TemplateName = Some name
        TemplateLanguage = language
        TemplateParameters = parameters
        Body = None
        Metadata = Map.empty
        CorrelationId = Some "corr-827"
    }

/// The policy filter over a recording channel, with a fixed clock.
let private policy
    (templates: IWhatsAppTemplateRegistry)
    (contacts: IExternalContactStore option)
    : RecordingChannel * RecordingAuditLog * INotificationChannel =
    let beneath = RecordingChannel()
    let audit = RecordingAuditLog()

    let filter =
        WhatsAppSendPolicyFilter(beneath, templates, contacts, Some(audit :> IAuditLog), SilentLogger(), fun () -> now)

    beneath, audit, filter :> INotificationChannel

let private noTemplates () =
    WhatsAppTemplateRegistry.NoOpWhatsAppTemplateRegistry() :> IWhatsAppTemplateRegistry

let private withReminder () =
    InMemoryTemplateRegistry [ reminder ] :> IWhatsAppTemplateRegistry

let private whatsAppWire =
    NotificationKind.SinkKind.toWireString NotificationKind.SinkKind.WhatsApp

let private seedBlob (storage: IBlobStorage) (name: string) (json: string) = async {
    match! storage.Upload("_platform", WhatsAppTemplateRegistry.blobName name, Encoding.UTF8.GetBytes json) with
    | Ok _ -> ()
    | Error e -> failtestf "could not seed the template blob: %s" e
}

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 827 — WhatsApp as a channel" [

        testList "SinkKind.WhatsApp and the Notification arm" [
            test "SinkKind.WhatsApp round-trips through toWireString / tryParse" {
                let wire = NotificationKind.SinkKind.toWireString NotificationKind.SinkKind.WhatsApp
                Expect.equal wire "WhatsApp" "the wire string is the case name"

                Expect.equal
                    (NotificationKind.SinkKind.tryParse wire)
                    (Some NotificationKind.SinkKind.WhatsApp)
                    "tryParse inverts toWireString"
            }

            test "every shipped SinkKind still round-trips, and none collides with WhatsApp" {
                let kinds = [
                    NotificationKind.SinkKind.Email
                    NotificationKind.SinkKind.Sms
                    NotificationKind.SinkKind.Push NotificationKind.PushVariant.WebPush
                    NotificationKind.SinkKind.Push NotificationKind.PushVariant.Fcm
                    NotificationKind.SinkKind.Push NotificationKind.PushVariant.Apns
                    NotificationKind.SinkKind.Push(NotificationKind.PushVariant.Other "WhatsApp")
                    NotificationKind.SinkKind.WhatsApp
                ]

                for kind in kinds do
                    Expect.equal
                        (NotificationKind.SinkKind.tryParse (NotificationKind.SinkKind.toWireString kind))
                        (Some kind)
                        $"%A{kind} round-trips"

                let wires = kinds |> List.map NotificationKind.SinkKind.toWireString
                Expect.equal (List.distinct wires) wires "every kind has its own wire string"
            }

            test "TransactionalWhatsApp is transactional, kind-named, and ungoverned by preferences" {
                let notification = freeForm [ RecipientId.User "u-1" ]
                Expect.isTrue (NotificationKind.isTransactional notification) "never written to the SSE stream"

                Expect.equal
                    (NotificationKind.ofNotification notification)
                    NotificationKind.TransactionalWhatsApp
                    "kind string"

                Expect.isNone
                    (PreferenceChannel.ofNotification notification)
                    "no preference family, so never digest-held past its window"
            }
        ]

        testList "INotificationSink contract — bound by in-memory fake sinks" [
            INotificationSinkContract.tests
                "InMemoryWhatsAppSink"
                (fun () -> InMemoryWhatsAppSink true :> INotificationSink)
                (fun scopeId -> NotificationEnvelope.create scopeId (freeForm [ RecipientId.User "u-1" ]))

            INotificationSinkContract.whatsAppTests
                "InMemoryWhatsAppSink (templates)"
                (fun () -> InMemoryWhatsAppSink true :> INotificationSink)
                (RecipientId.User "u-1")
                INotificationSinkContract.SupportsTemplates

            INotificationSinkContract.whatsAppTests
                "InMemoryWhatsAppSink (free-form only)"
                (fun () -> InMemoryWhatsAppSink false :> INotificationSink)
                (RecipientId.User "u-1")
                INotificationSinkContract.FreeFormOnly
        ]

        testList "IWhatsAppTemplateRegistry defaults" [
            testCaseAsync "the no-op registry knows no template"
            <| async {
                let! found = (noTemplates ()).GetTemplate "appointment_reminder"
                Expect.isNone found "every template send is refused on a deployment with no registry"
            }

            testCaseAsync "the blob registry reads _platform/whatsapp-templates/{name}.json"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage

                do!
                    seedBlob
                        storage
                        "appointment_reminder"
                        """{"Name":"appointment_reminder","Languages":["en_GB","cy"],"HeaderParameterCount":1,"BodyParameterCount":2,"ButtonParameterCount":0}"""

                let registry = WhatsAppTemplateRegistry.blobBacked storage None
                let! found = registry.GetTemplate "appointment_reminder"
                Expect.equal found (Some reminder) "the pinned on-disk shape decodes to the descriptor"

                let! missing = registry.GetTemplate "not_registered"
                Expect.isNone missing "a template with no blob is unknown"
            }

            testCaseAsync "a record that disagrees with its path, or is malformed, or undecodable, is refused"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage

                do!
                    seedBlob
                        storage
                        "renamed"
                        """{"Name":"something_else","Languages":["en_GB"],"HeaderParameterCount":0,"BodyParameterCount":1,"ButtonParameterCount":0}"""

                do!
                    seedBlob
                        storage
                        "negative"
                        """{"Name":"negative","Languages":["en_GB"],"HeaderParameterCount":0,"BodyParameterCount":-1,"ButtonParameterCount":0}"""

                do!
                    seedBlob
                        storage
                        "no_language"
                        """{"Name":"no_language","Languages":[],"HeaderParameterCount":0,"BodyParameterCount":1,"ButtonParameterCount":0}"""

                do! seedBlob storage "garbage" "{ this is not json"

                let registry = WhatsAppTemplateRegistry.blobBacked storage None

                for name in [ "renamed"; "negative"; "no_language"; "garbage" ] do
                    let! found = registry.GetTemplate name
                    Expect.isNone found $"'%s{name}' cannot be vouched for"
            }

            testCaseAsync "an unsafe template name never reaches storage"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage

                // A descriptor planted where a traversal would land.
                do!
                    storage.Upload(
                        "_platform",
                        "evil.json",
                        Encoding.UTF8.GetBytes
                            """{"Name":"../evil","Languages":["en_GB"],"HeaderParameterCount":0,"BodyParameterCount":0,"ButtonParameterCount":0}"""
                    )
                    |> Async.Ignore

                let registry = WhatsAppTemplateRegistry.blobBacked storage None

                for name in [ "../evil"; "a/b"; ""; "with space"; String.replicate 513 "a" ] do
                    let! found = registry.GetTemplate name
                    Expect.isNone found $"'%s{name}' is not a template name"

                Expect.isTrue (WhatsAppTemplateRegistry.isValidTemplateName "appointment_reminder_v2") "a real name"
                Expect.isTrue (WhatsAppTemplateRegistry.isValidTemplateName "order-shipped") "hyphenated"
            }
        ]

        testList "the 24-hour customer-care window" [
            test "the boundary is inclusive, and no inbound means outside" {
                Expect.isTrue (WhatsAppSendPolicy.isInsideWindow now (Some(now.AddHours -24.0))) "exactly 24 h"

                Expect.isFalse
                    (WhatsAppSendPolicy.isInsideWindow now (Some(now.AddHours(-24.0).AddSeconds -1.0)))
                    "24 h + 1 s"

                Expect.isTrue (WhatsAppSendPolicy.isInsideWindow now (Some(now.AddMinutes -5.0))) "5 min"
                Expect.isFalse (WhatsAppSendPolicy.isInsideWindow now None) "never wrote in"
            }

            testCaseAsync
                "a Body-only send to a contact whose last inbound is older than 24 h is refused before any sink"
            <| async {
                let store = freshStore ()
                let! stale = seedContact store "Stale" [ NotificationKind.SinkKind.WhatsApp ] (Some(now.AddHours -25.0))
                let beneath, audit, channel = policy (noTemplates ()) (Some store)

                do! channel.Publish(scope, freeForm [ RecipientId.External stale ])

                Expect.isEmpty beneath.Published "nothing reaches the channel beneath, so no sink runs"

                match audit.Refusals with
                | [ refusal ] ->
                    Expect.equal refusal.Reason "outside_24h_window_no_template" "the window reason"
                    Expect.equal refusal.Reason WhatsAppSendPolicy.OutsideWindowReason "the published literal"
                    Expect.equal refusal.NotificationKind whatsAppWire "kind"
                    Expect.equal refusal.ContactIds [ stale ] "the refused contact"
                    Expect.equal refusal.RecipientHashes.Length 1 "one hash per refused recipient"
                    Expect.equal refusal.CorrelationId (Some "corr-827") "correlation carried"
                | other -> failtestf "expected one NotificationDeliveryRefused row, got %A" other
            }

            testCaseAsync "a contact that never wrote in is outside the window"
            <| async {
                let store = freshStore ()
                let! silent = seedContact store "Silent" [ NotificationKind.SinkKind.WhatsApp ] None
                let beneath, audit, channel = policy (noTemplates ()) (Some store)

                do! channel.Publish(scope, freeForm [ RecipientId.External silent ])

                Expect.isEmpty beneath.Published "refused"

                Expect.equal (audit.Refusals |> List.map _.Reason) [ WhatsAppSendPolicy.OutsideWindowReason ] "audited"
            }

            testCaseAsync "a contact who wrote in two hours ago receives the free-form reply"
            <| async {
                let store = freshStore ()

                let! recent =
                    seedContact store "Recent" [ NotificationKind.SinkKind.WhatsApp ] (Some(now.AddHours -2.0))

                let beneath, audit, channel = policy (noTemplates ()) (Some store)

                do! channel.Publish(scope, freeForm [ RecipientId.External recent ])

                Expect.equal beneath.RecipientsSeen [ RecipientId.External recent ] "admitted"
                Expect.isEmpty audit.Refusals "no refusal"
            }

            testCaseAsync "a mixed envelope keeps the in-window contact and refuses the rest"
            <| async {
                let store = freshStore ()

                let! recent =
                    seedContact store "Recent" [ NotificationKind.SinkKind.WhatsApp ] (Some(now.AddHours -1.0))

                let! stale = seedContact store "Stale" [ NotificationKind.SinkKind.WhatsApp ] (Some(now.AddDays -3.0))
                let beneath, audit, channel = policy (noTemplates ()) (Some store)

                do!
                    channel.Publish(
                        scope,
                        freeForm [
                            RecipientId.External recent
                            RecipientId.External stale
                            RecipientId.User "u-1"
                        ]
                    )

                Expect.equal beneath.RecipientsSeen [ RecipientId.External recent ] "only the in-window contact"

                match audit.Refusals with
                | [ refusal ] ->
                    Expect.equal refusal.ContactIds [ stale ] "the stale contact"
                    Expect.equal refusal.RecipientHashes.Length 2 "the stale contact AND the platform user"
                | other -> failtestf "expected one refusal row, got %A" other
            }

            testCaseAsync "a platform user has no inbound record, so a free-form send to one is refused"
            <| async {
                let beneath, audit, channel = policy (noTemplates ()) None

                do! channel.Publish(scope, freeForm [ RecipientId.User "u-1" ])

                Expect.isEmpty beneath.Published "a business-initiated message must be a template"

                Expect.equal (audit.Refusals |> List.map _.Reason) [ WhatsAppSendPolicy.OutsideWindowReason ] "audited"
            }

            testCaseAsync "fail closed: no contact store, or an unknown contact, refuses"
            <| async {
                let beneath, audit, channel = policy (noTemplates ()) None
                do! channel.Publish(scope, freeForm [ RecipientId.External "c-unknown" ])

                let beneathB, auditB, channelB = policy (noTemplates ()) (Some(freshStore ()))
                do! channelB.Publish(scope, freeForm [ RecipientId.External "c-unknown" ])

                Expect.isEmpty beneath.Published "no store"
                Expect.isEmpty beneathB.Published "unknown contact"
                Expect.equal audit.Refusals.Length 1 "audited (no store)"
                Expect.equal auditB.Refusals.Length 1 "audited (unknown contact)"
            }

            testCaseAsync "a template send is not window-gated"
            <| async {
                let store = freshStore ()
                let! silent = seedContact store "Silent" [ NotificationKind.SinkKind.WhatsApp ] None
                let beneath, audit, channel = policy (withReminder ()) (Some store)

                let notification =
                    templated
                        [ RecipientId.External silent; RecipientId.User "u-1" ]
                        "appointment_reminder"
                        (Some "en_GB")
                        [ "Dr Evans"; "Alex"; "10:00" ]

                do! channel.Publish(scope, notification)

                Expect.equal beneath.Published [ scope, notification ] "published exactly as sent"
                Expect.isEmpty audit.Refusals "no refusal"
            }
        ]

        testList "template validation against the registry" [
            testCaseAsync "a template the registry does not know is refused before any sink"
            <| async {
                let registry = InMemoryTemplateRegistry [ reminder ]
                let beneath, audit, channel = policy registry None

                do! channel.Publish(scope, templated [ RecipientId.User "u-1" ] "order_shipped" None [ "x" ])

                Expect.isEmpty beneath.Published "refused"
                Expect.equal registry.Lookups 1 "the registry was asked"

                Expect.equal
                    (audit.Refusals |> List.map _.Reason)
                    [ WhatsAppSendPolicy.TemplateUnknownReason ]
                    "typed reason"
            }

            testCaseAsync "a parameter count that disagrees with the registered arity is refused"
            <| async {
                let beneath, audit, channel = policy (withReminder ()) None

                // Registered arity is 1 header + 2 body = 3.
                do!
                    channel.Publish(
                        scope,
                        templated [ RecipientId.User "u-1" ] "appointment_reminder" (Some "en_GB") [ "Alex"; "10:00" ]
                    )

                Expect.isEmpty beneath.Published "refused"

                Expect.equal
                    (audit.Refusals |> List.map _.Reason)
                    [ WhatsAppSendPolicy.TemplateArityReason ]
                    "typed reason"
            }

            testCaseAsync "a language the template is not approved in is refused; no language leaves it to the sink"
            <| async {
                let beneath, audit, channel = policy (withReminder ()) None
                let parameters = [ "Dr Evans"; "Alex"; "10:00" ]

                do!
                    channel.Publish(
                        scope,
                        templated [ RecipientId.User "u-1" ] "appointment_reminder" (Some "fr_FR") parameters
                    )

                Expect.isEmpty beneath.Published "fr_FR is not approved"

                Expect.equal
                    (audit.Refusals |> List.map _.Reason)
                    [ WhatsAppSendPolicy.TemplateLanguageReason ]
                    "typed reason"

                do! channel.Publish(scope, templated [ RecipientId.User "u-1" ] "appointment_reminder" None parameters)

                do!
                    channel.Publish(
                        scope,
                        templated [ RecipientId.User "u-1" ] "appointment_reminder" (Some "CY") parameters
                    )

                Expect.equal beneath.Published.Length 2 "unspecified, and a case-insensitive match, are admitted"
            }

            testCaseAsync "a registry that throws cannot vouch for the template"
            <| async {
                let beneath, audit, channel = policy (ThrowingTemplateRegistry()) None

                do!
                    channel.Publish(
                        scope,
                        templated [ RecipientId.User "u-1" ] "appointment_reminder" None [ "a"; "b"; "c" ]
                    )

                Expect.isEmpty beneath.Published "refused"

                Expect.equal
                    (audit.Refusals |> List.map _.Reason)
                    [ WhatsAppSendPolicy.TemplateUnknownReason ]
                    "fail closed"
            }

            testCaseAsync "an envelope with neither template nor body, or with both, is refused"
            <| async {
                let beneath, audit, channel = policy (withReminder ()) None

                let neither =
                    TransactionalWhatsApp {
                        Recipients = [ RecipientId.User "u-1" ]
                        TemplateName = None
                        TemplateLanguage = None
                        TemplateParameters = []
                        Body = None
                        Metadata = Map.empty
                        CorrelationId = None
                    }

                let both =
                    match templated [ RecipientId.User "u-1" ] "appointment_reminder" None [ "a"; "b"; "c" ] with
                    | TransactionalWhatsApp w -> TransactionalWhatsApp { w with Body = Some "and some text" }
                    | other -> other

                do! channel.Publish(scope, neither)
                do! channel.Publish(scope, both)

                Expect.isEmpty beneath.Published "both refused"

                Expect.equal
                    (audit.Refusals |> List.map _.Reason)
                    [ WhatsAppSendPolicy.NoContentReason; WhatsAppSendPolicy.TemplateAndBodyReason ]
                    "typed reasons"
            }

            testCaseAsync "every other notification kind passes through untouched"
            <| async {
                let registry = InMemoryTemplateRegistry []
                let beneath, audit, channel = policy registry None

                let sms =
                    TransactionalSms {
                        Recipients = [ RecipientId.User "u-1" ]
                        Body = "hi"
                        CorrelationId = None
                    }

                do! channel.Publish(scope, sms)
                do! channel.Publish(scope, SystemMessage(SystemMessageLevel.Info, "hello"))

                Expect.equal
                    beneath.Published
                    [ scope, sms; scope, SystemMessage(SystemMessageLevel.Info, "hello") ]
                    "untouched"

                Expect.equal registry.Lookups 0 "the registry is not consulted"
                Expect.isEmpty audit.Refusals "no refusal"
            }
        ]

        testList "the 6f.A consent gate applies to WhatsApp, and runs FIRST" [
            testCaseAsync "an SMS consent does not admit a WhatsApp send"
            <| async {
                let store = freshStore ()
                let! smsOnly = seedContact store "SmsOnly" [ NotificationKind.SinkKind.Sms ] (Some(now.AddHours -1.0))
                let beneath, policyAudit, policyChannel = policy (noTemplates ()) (Some store)
                let consentAudit = RecordingAuditLog()

                let consent =
                    ExternalContactConsentFilter(
                        policyChannel,
                        Some store,
                        Some(consentAudit :> IAuditLog),
                        SilentLogger(),
                        fun () -> now
                    )
                    :> INotificationChannel

                do! consent.Publish(scope, freeForm [ RecipientId.External smsOnly ])

                Expect.isEmpty beneath.Published "refused"

                Expect.equal
                    (consentAudit.Refusals |> List.map (fun r -> r.Reason, r.NotificationKind))
                    [ ExternalContactConsent.NoOptInReason, whatsAppWire ]
                    "refused for want of a WhatsApp consent, under the WhatsApp kind"

                Expect.isEmpty policyAudit.Refusals "the window rule never saw a recipient without consent"
            }

            testCaseAsync
                "consent is answered before the window: an unconsented, out-of-window contact is refused ONCE, as no_opt_in"
            <| async {
                let store = freshStore ()
                let! unconsented = seedContact store "Nobody" [] (Some(now.AddDays -5.0))

                let! consented =
                    seedContact store "Stale" [ NotificationKind.SinkKind.WhatsApp ] (Some(now.AddDays -5.0))

                let beneath, policyAudit, policyChannel = policy (noTemplates ()) (Some store)
                let consentAudit = RecordingAuditLog()

                let consent =
                    ExternalContactConsentFilter(
                        policyChannel,
                        Some store,
                        Some(consentAudit :> IAuditLog),
                        SilentLogger(),
                        fun () -> now
                    )
                    :> INotificationChannel

                do!
                    consent.Publish(
                        scope,
                        freeForm [ RecipientId.External unconsented; RecipientId.External consented ]
                    )

                Expect.isEmpty beneath.Published "neither is delivered"

                Expect.equal
                    (consentAudit.Refusals |> List.collect _.ContactIds)
                    [ unconsented ]
                    "consent refuses the unconsented contact"

                Expect.equal
                    (policyAudit.Refusals |> List.collect _.ContactIds)
                    [ consented ]
                    "the window rule sees ONLY the consented one"
            }
        ]

        testList "dispatch and composition" [
            testCaseAsync
                "TransactionalWhatsApp routes to the SinkKind.WhatsApp sink behind the whatsapp.enabled switch"
            <| async {
                let results = ConcurrentQueue<SinkResult>()
                let sink = FakeSink(NotificationKind.SinkKind.WhatsApp, "Fake", results)

                let prefs = Map.ofList [ ConfigKeys.NotificationPrefsKeys.WhatsAppEnabled, "true" ]

                use dispatcher =
                    new TransactionalDispatcher.TransactionalDispatcher(
                        [ sink :> INotificationSink ],
                        FakeConfigStore(prefs),
                        RecordingAuditLog(),
                        SilentLogger(),
                        TransactionalRetryPolicy.defaults,
                        NoOpActivitySink() :> IActivitySink
                    )

                do! dispatcher.StartAsync(CancellationToken.None) |> Async.AwaitTask

                try
                    Expect.isTrue (dispatcher.HasSinkForKind whatsAppWire) "registered under its wire string"

                    let channel =
                        TransactionalDispatcher.DispatchingNotificationChannel(
                            NotificationChannel.InMemoryNotificationChannel(None),
                            dispatcher
                        )
                        :> INotificationChannel

                    do! channel.Publish(scope, freeForm [ RecipientId.User "u-1" ])

                    let! winner =
                        Task.WhenAny(sink.FirstCallTask, Task.Delay(TimeSpan.FromSeconds 10.0))
                        |> Async.AwaitTask

                    Expect.isTrue (obj.ReferenceEquals(winner, sink.FirstCallTask)) "the WhatsApp sink received it"
                finally
                    dispatcher.StopAsync(CancellationToken.None) |> ignore
            }

            testCaseAsync "with the whatsapp.enabled switch unset the team is opted out"
            <| async {
                let results = ConcurrentQueue<SinkResult>()
                let sink = FakeSink(NotificationKind.SinkKind.WhatsApp, "Fake", results)
                let audit = CapturingAuditLog()

                use dispatcher =
                    new TransactionalDispatcher.TransactionalDispatcher(
                        [ sink :> INotificationSink ],
                        FakeConfigStore(Map.empty),
                        audit,
                        SilentLogger(),
                        TransactionalRetryPolicy.defaults,
                        NoOpActivitySink() :> IActivitySink
                    )

                do! dispatcher.StartAsync(CancellationToken.None) |> Async.AwaitTask

                try
                    let channel =
                        TransactionalDispatcher.DispatchingNotificationChannel(
                            NotificationChannel.InMemoryNotificationChannel(None),
                            dispatcher
                        )
                        :> INotificationChannel

                    do! channel.Publish(scope, freeForm [ RecipientId.User "u-1" ])
                    audit.WaitFor 1

                    Expect.isEmpty sink.Calls "default-off"

                    match audit.Recorded with
                    | [ _, NotificationSilentlySkipped p ] ->
                        Expect.equal p.Reason "team_opted_out" "the team kill switch"
                        Expect.equal p.NotificationKind whatsAppWire "kind"
                    | other -> failtestf "expected one skip row, got %A" other
                finally
                    dispatcher.StopAsync(CancellationToken.None) |> ignore
            }

            test "the send policy is composed only when a WhatsApp sink is registered (GP 13)" {
                let inner = RecordingChannel() :> INotificationChannel
                let audit = RecordingAuditLog() :> IAuditLog

                // No dispatcher at all.
                let bare = ServiceCollection().BuildServiceProvider()

                Expect.isTrue
                    (obj.ReferenceEquals(
                        ComposeRuntimeServices.composeWhatsAppSendPolicy
                            (bare :> IServiceProvider)
                            inner
                            audit
                            (SilentLogger()),
                        inner
                    ))
                    "no dispatcher: the channel is returned as-is"

                // A dispatcher with no WhatsApp sink.
                use emailOnly =
                    new TransactionalDispatcher.TransactionalDispatcher(
                        [
                            FakeSink(NotificationKind.SinkKind.Email, "Fake", ConcurrentQueue()) :> INotificationSink
                        ],
                        FakeConfigStore(Map.empty),
                        audit,
                        SilentLogger(),
                        TransactionalRetryPolicy.defaults,
                        NoOpActivitySink() :> IActivitySink
                    )

                let emailServices = ServiceCollection()

                emailServices.AddSingleton<TransactionalDispatcher.TransactionalDispatcher>(emailOnly)
                |> ignore

                Expect.isTrue
                    (obj.ReferenceEquals(
                        ComposeRuntimeServices.composeWhatsAppSendPolicy
                            (emailServices.BuildServiceProvider() :> IServiceProvider)
                            inner
                            audit
                            (SilentLogger()),
                        inner
                    ))
                    "no WhatsApp sink: the channel is returned as-is"

                // A dispatcher routing WhatsApp.
                use withWhatsApp =
                    new TransactionalDispatcher.TransactionalDispatcher(
                        [ InMemoryWhatsAppSink true :> INotificationSink ],
                        FakeConfigStore(Map.empty),
                        audit,
                        SilentLogger(),
                        TransactionalRetryPolicy.defaults,
                        NoOpActivitySink() :> IActivitySink
                    )

                let services = ServiceCollection()

                services.AddSingleton<TransactionalDispatcher.TransactionalDispatcher>(withWhatsApp)
                |> ignore

                match
                    ComposeRuntimeServices.composeWhatsAppSendPolicy
                        (services.BuildServiceProvider() :> IServiceProvider)
                        inner
                        audit
                        (SilentLogger())
                with
                | :? WhatsAppSendPolicyFilter -> ()
                | other -> failtestf "expected the send policy, got %s" (other.GetType().Name)
            }

            test "a WhatsApp sink brings the blob-backed template registry; a registry registered first wins" {
                let sinks = [ InMemoryWhatsAppSink true :> INotificationSink ]

                use dispatcher =
                    new TransactionalDispatcher.TransactionalDispatcher(
                        sinks,
                        FakeConfigStore(Map.empty),
                        RecordingAuditLog(),
                        SilentLogger(),
                        TransactionalRetryPolicy.defaults,
                        NoOpActivitySink() :> IActivitySink
                    )

                let services = ServiceCollection()
                services.AddSingleton<IBlobStorage>(InMemoryBlobStorage()) |> ignore

                ComposeNotifications.registerTransactionalDispatcher
                    services
                    ServerConfig.defaults
                    (Some dispatcher)
                    sinks

                match services.BuildServiceProvider().GetService(typeof<IWhatsAppTemplateRegistry>) with
                | :? WhatsAppTemplateRegistry.BlobWhatsAppTemplateRegistry -> ()
                | other -> failtestf "expected the blob-backed default, got %A" other

                let custom = InMemoryTemplateRegistry []
                let preRegistered = ServiceCollection()
                preRegistered.AddSingleton<IWhatsAppTemplateRegistry>(custom) |> ignore

                ComposeNotifications.registerTransactionalDispatcher
                    preRegistered
                    ServerConfig.defaults
                    (Some dispatcher)
                    sinks

                Expect.isTrue
                    (obj.ReferenceEquals(
                        preRegistered.BuildServiceProvider().GetService(typeof<IWhatsAppTemplateRegistry>),
                        custom
                    ))
                    "the composition root's own registry is kept"

                let emailSinks = [
                    FakeSink(NotificationKind.SinkKind.Email, "Fake", ConcurrentQueue()) :> INotificationSink
                ]

                let emailServices = ServiceCollection()

                ComposeNotifications.registerTransactionalDispatcher
                    emailServices
                    ServerConfig.defaults
                    (Some dispatcher)
                    emailSinks

                Expect.isNull
                    (emailServices.BuildServiceProvider().GetService(typeof<IWhatsAppTemplateRegistry>))
                    "no WhatsApp sink, no registry"
            }
        ]
    ]