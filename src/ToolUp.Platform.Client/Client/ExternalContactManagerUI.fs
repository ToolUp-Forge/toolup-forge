// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ExternalContactManagerUI

open Fuaran.MVU
open System
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── External contact admin ──────────────────────────────────────────
//
// Owner/Admin surface over `IExternalContactApi`: file the people a
// deployment wants to reach who have no account on it, and manage the
// per-channel consent that is the only thing making them reachable.
//
// **The consent controls are the design constraint everything else
// bends around.** Filing a contact is ordinary CRUD; recording a
// consent is asserting, on the record, that a named person agreed to be
// contacted — the GDPR Article 7(1) fact a regulator asks about. So the
// opt-in control is NOT a checkbox beside the phone number: it is a
// separate act, per channel, that requires the operator to state HOW
// the consent was obtained, and the form will not submit without it.
// The server refuses a blank source for the same reason; this is the
// half that stops an operator reaching for one.
//
// Withdrawal is one click and asks for no justification beyond a
// reason, because a withdrawal must never be harder than a grant.
//
// **Gating.** `NavRole.TeamOwnerAdmin` keeps the module out of a
// Member's sidebar, but that gate FAILS OPEN while team membership is
// in flight, so it is a navigation convenience, not a security
// boundary. The real gate is `ExternalContactApiHandler`, which refuses
// a non-Owner/Admin write and refuses a machine caller outright.

// ─── Model ───────────────────────────────────────────────────────────

/// Which screen the module is showing.
type View =
    /// The contact list.
    | ContactList
    /// One contact's detail, including its consent panel.
    | ContactDetail of contactId: string

/// The add/edit form's fields, shared by both because they edit the
/// same shape. `EditingId` distinguishes them: `None` is a new contact.
type ContactForm = {
    /// `None` while adding; the contact being edited otherwise.
    EditingId: string option
    /// The human label. Required.
    DisplayName: string
    /// RFC 5321 email, blank when none.
    Email: string
    /// E.164 phone, blank when none.
    Phone: string
    /// E.164 WhatsApp number, blank when none.
    WhatsApp: string
    /// Comma-separated grouping labels, split on submit.
    Tags: string
    /// Operator notes; never sent anywhere.
    Notes: string
}

module ContactForm =
    /// A blank new-contact form.
    let empty: ContactForm = {
        EditingId = None
        DisplayName = ""
        Email = ""
        Phone = ""
        WhatsApp = ""
        Tags = ""
        Notes = ""
    }

    /// The form pre-filled from an existing contact.
    let ofContact (contact: ExternalContact) : ContactForm = {
        EditingId = Some contact.Id
        DisplayName = contact.DisplayName
        Email = contact.OptionalEmailAddress |> Option.defaultValue ""
        Phone = contact.OptionalPhoneNumber |> Option.defaultValue ""
        WhatsApp = contact.OptionalWhatsAppNumber |> Option.defaultValue ""
        Tags = contact.Tags |> String.concat ", "
        Notes = contact.Notes |> Option.defaultValue ""
    }

    /// Split the comma-separated tag box into the list the wire carries.
    let parseTags (raw: string) : string list =
        raw.Split ','
        |> Array.map _.Trim()
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.toList

    let private optional (value: string) =
        if String.IsNullOrWhiteSpace value then
            None
        else
            Some(value.Trim())

    let toCreate (owner: ContactOwner) (form: ContactForm) : CreateExternalContactRequest = {
        DisplayName = form.DisplayName.Trim()
        EmailAddress = optional form.Email
        PhoneNumber = optional form.Phone
        WhatsAppNumber = optional form.WhatsApp
        Owner = owner
        Tags = parseTags form.Tags
        Notes = optional form.Notes
    }

    let toUpdate (contactId: string) (form: ContactForm) : UpdateExternalContactRequest = {
        ContactId = contactId
        DisplayName = form.DisplayName.Trim()
        EmailAddress = optional form.Email
        PhoneNumber = optional form.Phone
        WhatsAppNumber = optional form.WhatsApp
        Tags = parseTags form.Tags
        Notes = optional form.Notes
    }

/// The consent the operator is about to assert. `Source` is required —
/// see the module preamble.
type OptInDraft = {
    /// The channel in `NotificationKind.SinkKind` wire form.
    Channel: string
    /// The Article 7(1) evidence. Blank keeps the record action disabled.
    Source: string
}

module OptInDraft =
    let empty: OptInDraft = { Channel = "Email"; Source = "" }

type Model = {
    /// Every contact in the caller's scope, as last loaded.
    Contacts: ExternalContact list
    /// Which screen is showing.
    CurrentView: View
    /// Open when the operator is adding or editing; `None` otherwise.
    Form: ContactForm option
    /// The consent being drafted on the open contact.
    OptIn: OptInDraft
    /// `false` until the first load settles, so the empty state is not
    /// shown while the list is still in flight.
    Loaded: bool
    /// A call is outstanding.
    Busy: bool
    /// The last failure, shown as a notice until dismissed.
    Error: Notice option
}

/// Everything the module can be asked to do. Every remoting call is a
/// request / response pair, and every response carries a `Result` so a
/// failure lands in the error notice rather than in an exception.
type Msg =
    /// Re-read the contact list.
    | LoadContacts
    /// The list came back.
    | ContactsLoaded of Result<ExternalContact list, string>
    /// Show one contact's detail.
    | OpenContact of string
    /// Return to the list.
    | BackToList
    /// Open a blank add form.
    | StartAdd
    /// Open the edit form over an existing contact.
    | StartEdit of string
    /// Close the form without saving.
    | CancelForm
    /// Apply one edit to the open form.
    | SetFormField of (ContactForm -> ContactForm)
    /// Create or update, depending on `ContactForm.EditingId`.
    | SubmitForm
    /// The create or update came back.
    | ContactSaved of Result<ExternalContact, string>
    /// Delete a contact and every consent it carried.
    | DeleteContact of string
    /// The delete came back.
    | ContactDeleted of Result<unit, string>
    /// Pick the channel the drafted consent is for.
    | SetOptInChannel of string
    /// Type the drafted consent's evidence.
    | SetOptInSource of string
    /// Record the drafted consent. Ignored while the source is blank.
    | RecordOptIn of string
    /// Withdraw one channel's consent.
    | WithdrawOptIn of contactId: string * channel: string
    /// A consent record or withdrawal came back.
    | OptInChanged of Result<ExternalContact, string>
    /// Clear the error notice.
    | DismissError

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job. The API uses
// the default `/api/{type}/{method}` shape; the route builder is passed
// explicitly for symmetry with the server-side `Api.make`.
let private contactApi: IExternalContactApi =
    Api.makeProxy<IExternalContactApi> (
        routeBuilder = ExternalContactApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

let private loadContacts () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IExternalContactApi.ListContacts"
            contactApi.ListContacts
            ()
            (RemoteEffect.flatten >> ContactsLoaded)
    )
]

// ─── Init / update ───────────────────────────────────────────────────

let init () : Model * Step<Msg, RemoteEffect<Msg>> list =
    ({
        Contacts = []
        CurrentView = ContactList
        Form = None
        OptIn = OptInDraft.empty
        Loaded = false
        Busy = true
        Error = None
     },
     loadContacts ())

/// The owner a newly filed contact is created under, as the CLIENT
/// guesses it. The server derives the real one from the caller's own
/// subject and ignores this — a request that could name an owner could
/// file a contact into someone else's address book — so this value is
/// a placeholder that keeps the wire shape complete.
let private clientOwnerPlaceholder = ContactOwner.Team ""

/// The transitions, with their remote calls as values.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | LoadContacts -> { model with Busy = true; Error = None }, loadContacts ()

    | ContactsLoaded(Ok contacts) ->
        {
            model with
                Contacts = Memo.keepEqual (fun (c: ExternalContact) -> c.Id) model.Contacts contacts
                Loaded = true
                Busy = false
                Error = None
        },
        []

    | ContactsLoaded(Error err) ->
        {
            model with
                Loaded = true
                Busy = false
                Error = Some(Notice.Problem err)
        },
        []

    | OpenContact contactId ->
        {
            model with
                CurrentView = ContactDetail contactId
                Form = None
                OptIn = OptInDraft.empty
                Error = None
        },
        []

    | BackToList ->
        {
            model with
                CurrentView = ContactList
                Form = None
                Error = None
        },
        []

    | StartAdd ->
        {
            model with
                Form = Some ContactForm.empty
                Error = None
        },
        []

    | StartEdit contactId ->
        let form =
            model.Contacts
            |> List.tryFind (fun c -> c.Id = contactId)
            |> Option.map ContactForm.ofContact

        { model with Form = form; Error = None }, []

    | CancelForm -> { model with Form = None }, []

    | SetFormField f ->
        {
            model with
                Form = model.Form |> Option.map f
        },
        []

    | SubmitForm ->
        match model.Form with
        | None -> model, []
        | Some form ->
            let save =
                match form.EditingId with
                | None ->
                    RemoteEffect.call
                        "IExternalContactApi.CreateContact"
                        contactApi.CreateContact
                        (ContactForm.toCreate clientOwnerPlaceholder form)
                        (RemoteEffect.flatten >> ContactSaved)
                | Some contactId ->
                    RemoteEffect.call
                        "IExternalContactApi.UpdateContact"
                        contactApi.UpdateContact
                        (ContactForm.toUpdate contactId form)
                        (RemoteEffect.flatten >> ContactSaved)

            { model with Busy = true; Error = None }, [ Do save ]

    | ContactSaved(Ok _) ->
        {
            model with
                Form = None
                Busy = true
                Error = None
        },
        loadContacts ()

    | ContactSaved(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
        },
        []

    | DeleteContact contactId ->
        { model with Busy = true; Error = None },
        [
            Do(
                RemoteEffect.call
                    "IExternalContactApi.DeleteContact"
                    contactApi.DeleteContact
                    contactId
                    (RemoteEffect.flatten >> ContactDeleted)
            )
        ]

    | ContactDeleted(Ok()) ->
        {
            model with
                CurrentView = ContactList
                Busy = true
                Error = None
        },
        loadContacts ()

    | ContactDeleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
        },
        []

    | SetOptInChannel channel ->
        {
            model with
                OptIn = { model.OptIn with Channel = channel }
        },
        []

    | SetOptInSource source ->
        {
            model with
                OptIn = { model.OptIn with Source = source }
        },
        []

    | RecordOptIn contactId ->
        // The source is required client-side as well as server-side.
        // Both halves exist deliberately: the server's refusal is the
        // one that binds, and this one is what stops an operator
        // reaching for a blank box in the first place.
        if String.IsNullOrWhiteSpace model.OptIn.Source then
            model, []
        else
            { model with Busy = true; Error = None },
            [
                Do(
                    RemoteEffect.call
                        "IExternalContactApi.RecordOptIn"
                        contactApi.RecordOptIn
                        {
                            ContactId = contactId
                            Channel = model.OptIn.Channel
                            Source = model.OptIn.Source.Trim()
                            ExpiresAt = None
                        }
                        (RemoteEffect.flatten >> OptInChanged)
                )
            ]

    | WithdrawOptIn(contactId, channel) ->
        { model with Busy = true; Error = None },
        [
            Do(
                RemoteEffect.call
                    "IExternalContactApi.WithdrawOptIn"
                    contactApi.WithdrawOptIn
                    {
                        ContactId = contactId
                        Channel = channel
                        Reason = "admin"
                    }
                    (RemoteEffect.flatten >> OptInChanged)
            )
        ]

    | OptInChanged(Ok _) ->
        {
            model with
                OptIn = OptInDraft.empty
                Busy = true
                Error = None
        },
        loadContacts ()

    | OptInChanged(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
        },
        []

    | DismissError -> { model with Error = None }, []

// ─── View ────────────────────────────────────────────────────────────

/// The channels an operator can record a consent on. The wire strings
/// are `NotificationKind.SinkKind.toWireString` values, which the
/// server parses back with `tryParse` — one vocabulary, not two.
let internal channelOptions = [ "Email"; "Sms"; "Push.WebPush"; "Push.Fcm"; "Push.Apns" ]

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: ExternalContactMessages
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./ExternalContactManagerTree.fs")

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "ExternalContactManagerTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.ExternalContact
            Locale = ctx.Locale
        |}
    )

// ─── Module creation ─────────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.ExternalContactManager"

/// The built-in external-contact admin (`ShellSlots.builtIn` builds its module).
/// `NavRole.TeamOwnerAdmin` keeps it out of a Member's sidebar; the
/// server-side handler is the enforcement (see the module preamble).
let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Contacts"
        Icon = ToolUp.Platform.Icons.users
    }
    Group = Some "Team Management"
    NavRole = Some ToolUp.Platform.NavRole.TeamOwnerAdmin
    Area = Product
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = fun _ -> init ()
    Update = update
    View = BuiltInView.Page view
    Extend = id
}