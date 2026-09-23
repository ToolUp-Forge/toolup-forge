// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ExternalContactManagerUI

open System
open ToolUp.Elmish
open Feliz
open Feliz.AgGrid
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Phase 6f.A — external contact admin ─────────────────────────────
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
    /// The last failure, shown in the banner until dismissed.
    Error: string option
}

/// Everything the module can be asked to do. Every remoting call is a
/// request / response pair, and every response carries a `Result` so a
/// failure lands in the banner rather than in an exception.
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
    /// Clear the error banner.
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

let private loadContactsCmd () =
    Cmd.OfRemoting.call contactApi.ListContacts () ContactsLoaded (fun e -> ContactsLoaded(Error e.Message))

// ─── Init / update ───────────────────────────────────────────────────

let init () : Model * Cmd<Msg> =
    {
        Contacts = []
        CurrentView = ContactList
        Form = None
        OptIn = OptInDraft.empty
        Loaded = false
        Busy = true
        Error = None
    },
    loadContactsCmd ()

/// The owner a newly filed contact is created under, as the CLIENT
/// guesses it. The server derives the real one from the caller's own
/// subject and ignores this — a request that could name an owner could
/// file a contact into someone else's address book — so this value is
/// a placeholder that keeps the wire shape complete.
let private clientOwnerPlaceholder = ContactOwner.Team ""

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | LoadContacts -> { model with Busy = true; Error = None }, loadContactsCmd ()

    | ContactsLoaded(Ok contacts) ->
        {
            model with
                Contacts = contacts
                Loaded = true
                Busy = false
                Error = None
        },
        Cmd.none

    | ContactsLoaded(Error err) ->
        {
            model with
                Loaded = true
                Busy = false
                Error = Some err
        },
        Cmd.none

    | OpenContact contactId ->
        {
            model with
                CurrentView = ContactDetail contactId
                Form = None
                OptIn = OptInDraft.empty
                Error = None
        },
        Cmd.none

    | BackToList ->
        {
            model with
                CurrentView = ContactList
                Form = None
                Error = None
        },
        Cmd.none

    | StartAdd ->
        {
            model with
                Form = Some ContactForm.empty
                Error = None
        },
        Cmd.none

    | StartEdit contactId ->
        let form =
            model.Contacts
            |> List.tryFind (fun c -> c.Id = contactId)
            |> Option.map ContactForm.ofContact

        { model with Form = form; Error = None }, Cmd.none

    | CancelForm -> { model with Form = None }, Cmd.none

    | SetFormField f ->
        {
            model with
                Form = model.Form |> Option.map f
        },
        Cmd.none

    | SubmitForm ->
        match model.Form with
        | None -> model, Cmd.none
        | Some form ->
            let cmd =
                match form.EditingId with
                | None ->
                    Cmd.OfRemoting.call
                        contactApi.CreateContact
                        (ContactForm.toCreate clientOwnerPlaceholder form)
                        ContactSaved
                        (fun e -> ContactSaved(Error e.Message))
                | Some contactId ->
                    Cmd.OfRemoting.call
                        contactApi.UpdateContact
                        (ContactForm.toUpdate contactId form)
                        ContactSaved
                        (fun e -> ContactSaved(Error e.Message))

            { model with Busy = true; Error = None }, cmd

    | ContactSaved(Ok _) ->
        {
            model with
                Form = None
                Busy = true
                Error = None
        },
        loadContactsCmd ()

    | ContactSaved(Error err) ->
        {
            model with
                Busy = false
                Error = Some err
        },
        Cmd.none

    | DeleteContact contactId ->
        { model with Busy = true; Error = None },
        Cmd.OfRemoting.call contactApi.DeleteContact contactId ContactDeleted (fun e -> ContactDeleted(Error e.Message))

    | ContactDeleted(Ok()) ->
        {
            model with
                CurrentView = ContactList
                Busy = true
                Error = None
        },
        loadContactsCmd ()

    | ContactDeleted(Error err) ->
        {
            model with
                Busy = false
                Error = Some err
        },
        Cmd.none

    | SetOptInChannel channel ->
        {
            model with
                OptIn = { model.OptIn with Channel = channel }
        },
        Cmd.none

    | SetOptInSource source ->
        {
            model with
                OptIn = { model.OptIn with Source = source }
        },
        Cmd.none

    | RecordOptIn contactId ->
        // The source is required client-side as well as server-side.
        // Both halves exist deliberately: the server's refusal is the
        // one that binds, and this one is what stops an operator
        // reaching for a blank box in the first place.
        if String.IsNullOrWhiteSpace model.OptIn.Source then
            model, Cmd.none
        else
            { model with Busy = true; Error = None },
            Cmd.OfRemoting.call
                contactApi.RecordOptIn
                {
                    ContactId = contactId
                    Channel = model.OptIn.Channel
                    Source = model.OptIn.Source.Trim()
                    ExpiresAt = None
                }
                OptInChanged
                (fun e -> OptInChanged(Error e.Message))

    | WithdrawOptIn(contactId, channel) ->
        { model with Busy = true; Error = None },
        Cmd.OfRemoting.call
            contactApi.WithdrawOptIn
            {
                ContactId = contactId
                Channel = channel
                Reason = "admin"
            }
            OptInChanged
            (fun e -> OptInChanged(Error e.Message))

    | OptInChanged(Ok _) ->
        {
            model with
                OptIn = OptInDraft.empty
                Busy = true
                Error = None
        },
        loadContactsCmd ()

    | OptInChanged(Error err) ->
        {
            model with
                Busy = false
                Error = Some err
        },
        Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

// ─── View helpers ────────────────────────────────────────────────────

/// The channels an operator can record a consent on. The wire strings
/// are `NotificationKind.SinkKind.toWireString` values, which the
/// server parses back with `tryParse` — one vocabulary, not two.
let private channelOptions = [ "Email"; "Sms"; "Push.WebPush"; "Push.Fcm"; "Push.Apns" ]

let private pill (label: string) (cls: string) =
    Html.span [
        prop.className $"inline-block text-xs px-2 py-0.5 rounded {cls}"
        prop.text label
    ]

/// The consent summary shown in the grid row: the channels this contact
/// is actually reachable on, or a plain statement that it is reachable
/// on none. The empty case is spelled out rather than left blank —
/// "no consent recorded" is the single most important fact about a
/// contact and a blank cell reads as a rendering bug.
let private consentSummary (msgs: ExternalContactMessages) (contact: ExternalContact) =
    let channels =
        contact.OptIns
        |> Map.toList
        |> List.map (fst >> NotificationKind.SinkKind.toWireString)

    if List.isEmpty channels then
        pill msgs.NoConsent "bg-gray-100 text-gray-600"
    else
        Html.div [
            prop.className "flex gap-1 flex-wrap"
            prop.children [ for channel in channels -> pill channel "bg-green-100 text-green-700" ]
        ]

let private errorBanner (msgs: ExternalContactMessages) (model: Model) (dispatch: Msg -> unit) =
    match model.Error with
    | None -> Html.none
    | Some err ->
        Html.div [
            prop.className "mb-3 p-3 rounded bg-red-50 text-red-800 text-sm flex justify-between items-start gap-3"
            prop.children [
                Html.span [ prop.text err ]
                Forms.Button.secondary msgs.Dismiss (fun () -> dispatch DismissError)
            ]
        ]

let private contactFormPanel (msgs: ExternalContactMessages) (form: ContactForm) (dispatch: Msg -> unit) =
    let heading =
        match form.EditingId with
        | None -> msgs.NewContactHeading
        | Some _ -> msgs.EditContactHeading

    Layout.Panel.panel heading [
        Html.div [
            prop.className "flex flex-col gap-3"
            prop.children [
                Html.p [ prop.className "text-xs text-muted"; prop.text msgs.ContactFormHelp ]

                Forms.Field.field
                    msgs.FieldDisplayName
                    (Forms.Input.text
                        form.DisplayName
                        (fun v -> dispatch (SetFormField(fun f -> { f with DisplayName = v })))
                        msgs.PlaceholderDisplayName)

                Forms.Field.field
                    msgs.FieldEmail
                    (Forms.Input.text
                        form.Email
                        (fun v -> dispatch (SetFormField(fun f -> { f with Email = v })))
                        msgs.PlaceholderEmail)

                Forms.Field.field
                    msgs.FieldPhone
                    (Forms.Input.text
                        form.Phone
                        (fun v -> dispatch (SetFormField(fun f -> { f with Phone = v })))
                        msgs.PlaceholderPhone)

                Forms.Field.field
                    msgs.FieldWhatsApp
                    (Forms.Input.text
                        form.WhatsApp
                        (fun v -> dispatch (SetFormField(fun f -> { f with WhatsApp = v })))
                        msgs.PlaceholderPhone)

                Forms.Field.field
                    msgs.FieldTags
                    (Forms.Input.text
                        form.Tags
                        (fun v -> dispatch (SetFormField(fun f -> { f with Tags = v })))
                        msgs.PlaceholderTags)

                Forms.Field.field
                    msgs.FieldNotes
                    (Forms.Input.text
                        form.Notes
                        (fun v -> dispatch (SetFormField(fun f -> { f with Notes = v })))
                        msgs.PlaceholderNotes)

                Forms.Field.actions [
                    Forms.Button.primary msgs.SaveContact (fun () -> dispatch SubmitForm)
                    Forms.Button.secondary msgs.Cancel (fun () -> dispatch CancelForm)
                ]
            ]
        ]
    ]

/// The consent panel: what this contact has agreed to, one row per
/// channel with a withdrawal beside it, and the form that records a new
/// one. The source box is the point of the panel — see the preamble.
let private consentPanel
    (msgs: ExternalContactMessages)
    (model: Model)
    (contact: ExternalContact)
    (dispatch: Msg -> unit)
    =
    let recorded = contact.OptIns |> Map.toList

    Layout.Panel.panel msgs.ConsentHeading [
        Html.div [
            prop.className "flex flex-col gap-4"
            prop.children [
                Html.p [ prop.className "text-xs text-muted"; prop.text msgs.ConsentHelp ]

                if List.isEmpty recorded then
                    Html.p [ prop.className "text-sm text-muted py-2"; prop.text msgs.NoConsentBody ]
                else
                    Html.div [
                        prop.className "flex flex-col gap-2"
                        prop.children [
                            for channel, record in recorded ->
                                let wire = NotificationKind.SinkKind.toWireString channel

                                Html.div [
                                    prop.className
                                        "flex items-center justify-between gap-3 p-2 rounded border border-gray-200"
                                    prop.children [
                                        Html.div [
                                            prop.className "flex flex-col"
                                            prop.children [
                                                Html.span [ prop.className "text-sm font-medium"; prop.text wire ]
                                                Html.span [
                                                    prop.className "text-xs text-muted"
                                                    prop.text (msgs.ConsentEvidence record.Source)
                                                ]
                                            ]
                                        ]
                                        Forms.Button.secondary msgs.WithdrawConsent (fun () ->
                                            dispatch (WithdrawOptIn(contact.Id, wire)))
                                    ]
                                ]
                        ]
                    ]

                Html.div [
                    prop.className "flex flex-col gap-3 pt-2 border-t border-gray-200"
                    prop.children [
                        Html.p [ prop.className "text-xs text-muted"; prop.text msgs.RecordConsentHelp ]

                        Forms.Field.field
                            msgs.FieldChannel
                            (Html.select [
                                prop.className "border rounded px-2 py-1 text-sm"
                                prop.value model.OptIn.Channel
                                prop.onChange (fun (v: string) -> dispatch (SetOptInChannel v))
                                prop.children [
                                    for option in channelOptions -> Html.option [ prop.value option; prop.text option ]
                                ]
                            ])

                        Forms.Field.field
                            msgs.FieldConsentSource
                            (Forms.Input.text
                                model.OptIn.Source
                                (fun v -> dispatch (SetOptInSource v))
                                msgs.PlaceholderConsentSource)

                        Forms.Field.actions [
                            if String.IsNullOrWhiteSpace model.OptIn.Source then
                                // Disabled rather than hidden: the
                                // operator should see that the control
                                // exists and what it is waiting for.
                                Html.button [
                                    prop.disabled true
                                    prop.className "px-3 py-1.5 rounded text-sm bg-gray-200 text-gray-500"
                                    prop.text msgs.RecordConsent
                                ]
                            else
                                Forms.Button.primary msgs.RecordConsent (fun () -> dispatch (RecordOptIn contact.Id))
                        ]
                    ]
                ]
            ]
        ]
    ]

let private contactListView (msgs: ExternalContactMessages) (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "space-y-4"
        prop.children [
            Layout.Panel.panel msgs.ContactsPanel [
                if model.Contacts.IsEmpty then
                    Html.p [ prop.className "text-sm text-muted py-4"; prop.text msgs.NoContactsYet ]
                else
                    AgGrid.grid [
                        AgGrid.theme Theme.themeBalham
                        AgGrid.domLayout AutoHeight
                        AgGrid.columnDefs [
                            ColumnDef.create [
                                ColumnDef.headerName msgs.ColumnName
                                ColumnDef.valueGetter (fun (c: ExternalContact) -> c.DisplayName)
                            ]
                            ColumnDef.create [
                                ColumnDef.headerName msgs.ColumnEmail
                                ColumnDef.valueGetter (fun (c: ExternalContact) ->
                                    c.OptionalEmailAddress |> Option.defaultValue "")
                            ]
                            ColumnDef.create [
                                ColumnDef.headerName msgs.ColumnPhone
                                ColumnDef.valueGetter (fun (c: ExternalContact) ->
                                    c.OptionalPhoneNumber |> Option.defaultValue "")
                            ]
                            ColumnDef.create [
                                ColumnDef.headerName msgs.ColumnTags
                                ColumnDef.valueGetter (fun (c: ExternalContact) -> c.Tags |> String.concat ", ")
                            ]
                            ColumnDef.create [
                                ColumnDef.headerName msgs.ColumnConsent
                                ColumnDef.cellRenderer (fun (p: ICellRendererParams<ExternalContact, obj>) ->
                                    match p.data with
                                    | Some contact -> consentSummary msgs contact
                                    | None -> Html.none)
                            ]
                            ColumnDef.create [
                                ColumnDef.headerName ""
                                ColumnDef.cellRenderer (fun (p: ICellRendererParams<ExternalContact, obj>) ->
                                    match p.data with
                                    | Some contact ->
                                        Forms.Button.secondary msgs.Manage (fun () ->
                                            dispatch (OpenContact contact.Id))
                                    | None -> Html.none)
                            ]
                        ]
                        AgGrid.rowData (List.toArray model.Contacts)
                    ]
            ]

            match model.Form with
            | Some form when form.EditingId.IsNone -> contactFormPanel msgs form dispatch
            | _ ->
                Html.div [
                    prop.children [ Forms.Button.primary msgs.AddContact (fun () -> dispatch StartAdd) ]
                ]
        ]
    ]

let private contactDetailView
    (msgs: ExternalContactMessages)
    (model: Model)
    (contactId: string)
    (dispatch: Msg -> unit)
    =
    match model.Contacts |> List.tryFind (fun c -> c.Id = contactId) with
    | None ->
        Html.div [
            prop.className "space-y-3"
            prop.children [
                Html.p [ prop.className "text-sm text-muted"; prop.text msgs.ContactGone ]
                Forms.Button.secondary msgs.BackToList (fun () -> dispatch BackToList)
            ]
        ]
    | Some contact ->
        Html.div [
            prop.className "space-y-4"
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between gap-3"
                    prop.children [
                        Html.h3 [ prop.className "text-base font-semibold"; prop.text contact.DisplayName ]
                        Forms.Button.secondary msgs.BackToList (fun () -> dispatch BackToList)
                    ]
                ]

                match model.Form with
                | Some form when form.EditingId = Some contact.Id -> contactFormPanel msgs form dispatch
                | _ ->
                    Layout.Panel.panel msgs.DetailsPanel [
                        Html.div [
                            prop.className "flex flex-col gap-2 text-sm"
                            prop.children [
                                Html.div [
                                    prop.text (
                                        msgs.DetailEmail(contact.OptionalEmailAddress |> Option.defaultValue "—")
                                    )
                                ]
                                Html.div [
                                    prop.text (msgs.DetailPhone(contact.OptionalPhoneNumber |> Option.defaultValue "—"))
                                ]
                                Html.div [ prop.text (msgs.DetailTags(contact.Tags |> String.concat ", ")) ]
                                Forms.Field.actions [
                                    Forms.Button.secondary msgs.EditContact (fun () -> dispatch (StartEdit contact.Id))
                                    Forms.Button.secondary msgs.DeleteContact (fun () ->
                                        dispatch (DeleteContact contact.Id))
                                ]
                            ]
                        ]
                    ]

                consentPanel msgs model contact dispatch
            ]
        ]

[<ReactComponent>]
let private ExternalContactManagerBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).ExternalContact

    let body =
        if not model.Loaded then
            Html.p [ prop.className "text-sm text-muted py-4"; prop.text msgs.Loading ]
        else
            match model.CurrentView with
            | ContactList -> contactListView msgs model dispatch
            | ContactDetail contactId -> contactDetailView msgs model contactId dispatch

    Html.div [
        prop.className "p-6 max-w-5xl"
        prop.children [
            Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text msgs.Heading ]
            Html.p [ prop.className "text-sm text-gray-600 mb-4"; prop.text msgs.Subheading ]
            errorBanner msgs model dispatch
            body
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    ExternalContactManagerBody model dispatch

// ─── Module creation ─────────────────────────────────────────────────

/// Create the built-in external-contact admin as an `ErasedModule`.
/// `NavRole.TeamOwnerAdmin` keeps it out of a Member's sidebar; the
/// server-side handler is the enforcement (see the module preamble).
let create (config: ExternalContactManagerConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Contacts"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.users

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.ExternalContactManager"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Team Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register