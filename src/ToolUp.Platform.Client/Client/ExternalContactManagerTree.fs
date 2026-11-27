// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The external-contact admin as a Fuaran-UI tree, built and drawn in a file of
/// its own, which `ExternalContactManagerUI`'s view mounts through
/// `TreeSurface.deferred` (the shell reads that module at boot; see
/// `TeamManagerTree` for the trade). Nothing imports this file statically.
///
/// The contact rows are kept per contact, every row id from the contact's id
/// and every row message carrying it; a contact's consents arrive with it, so
/// its consent list is kept whole on the contact. The form's text fields are
/// islands (`TreeIslands`): they hold what is typed until the field is left.
module internal ExternalContactManagerTree

open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open ExternalContactManagerUI
open KeepScoped

let private badge (id: string) (variant: BadgeVariant) (label: string) : Node<'Msg> =
    Fuaran.badge id {
        Label = Nodes.literal label
        Variant = variant
    }

/// A contact's consents, channel first. A record decoded without the map
/// reads as one with no consent.
let private consents (contact: ExternalContact) : (string * OptInRecord) list =
    if isNull (box contact.OptIns) then
        []
    else
        contact.OptIns
        |> Map.toList
        |> List.map (fun (channel, record) -> NotificationKind.SinkKind.toWireString channel, record)

let private orBlank (value: string option) = value |> Option.defaultValue ""

// ─── The contact list ────────────────────────────────────────────────

let private columns (msgs: ExternalContactMessages) : Nodes.Table.Column list = [
    Nodes.Table.column "name" msgs.ColumnName 3
    Nodes.Table.column "email" msgs.ColumnEmail 3
    Nodes.Table.column "phone" msgs.ColumnPhone 2
    Nodes.Table.column "tags" msgs.ColumnTags 2
    Nodes.Table.column "consent" msgs.ColumnConsent 2
]

/// One contact: what it holds, the channels it is reachable on (or a plain
/// statement that it is reachable on none) and the way into its detail.
let private contactRows =
    Rows(
        (fun (a: ExternalContactMessages) b -> sameObject a b),
        fun msgs (contact: ExternalContact) ->
            let id = Nodes.Ids.keyed "contact" contact.Id
            let channels = consents contact

            Nodes.Table.row id (columns msgs) [
                Nodes.Table.cell (Nodes.Ids.child id "name") [
                    Nodes.text (Nodes.Ids.child id "display-name") contact.DisplayName
                    Nodes.Actions.secondary (Nodes.Ids.child id "manage") msgs.Manage (OpenContact contact.Id)
                ]
                Nodes.Table.cell (Nodes.Ids.child id "email") [
                    Nodes.text (Nodes.Ids.child id "email-text") (orBlank contact.OptionalEmailAddress)
                ]
                Nodes.Table.cell (Nodes.Ids.child id "phone") [
                    Nodes.text (Nodes.Ids.child id "phone-text") (orBlank contact.OptionalPhoneNumber)
                ]
                Nodes.Table.cell (Nodes.Ids.child id "tags") [
                    Nodes.text (Nodes.Ids.child id "tags-text") (String.concat ", " contact.Tags)
                ]
                Nodes.Table.cell (Nodes.Ids.child id "consent") [
                    if List.isEmpty channels then
                        badge (Nodes.Ids.child id "no-consent") BadgeVariant.Neutral msgs.NoConsent
                    else
                        Nodes.Layout.row (Nodes.Ids.child id "channels") [
                            for channel, _ in channels ->
                                badge
                                    (Nodes.Ids.keyed (Nodes.Ids.child id "channel") channel)
                                    BadgeVariant.Success
                                    channel
                        ]
                ]
            ]
    )

// ─── The add / edit form ─────────────────────────────────────────────

/// The form both adding and editing use: six fields, then Save and Cancel.
let private formPanel (msgs: ExternalContactMessages) (busy: bool) (form: ContactForm) (dispatch: Msg -> unit) =
    let heading =
        match form.EditingId with
        | None -> msgs.NewContactHeading
        | Some _ -> msgs.EditContactHeading

    let field
        (name: string)
        (label: string)
        (placeholder: string)
        (value: string)
        (set: ContactForm -> string -> ContactForm)
        =
        TreeIslands.textField (Nodes.Ids.child "contact-form" name) label placeholder value (fun v ->
            dispatch (SetFormField(fun f -> set f v)))

    Nodes.Panel.card "contact-form" heading [
        Nodes.text "contact-form-help" msgs.ContactFormHelp
        field "display-name" msgs.FieldDisplayName msgs.PlaceholderDisplayName form.DisplayName (fun f v -> {
            f with
                DisplayName = v
        })
        field "email" msgs.FieldEmail msgs.PlaceholderEmail form.Email (fun f v -> { f with Email = v })
        field "phone" msgs.FieldPhone msgs.PlaceholderPhone form.Phone (fun f v -> { f with Phone = v })
        field "whatsapp" msgs.FieldWhatsApp msgs.PlaceholderPhone form.WhatsApp (fun f v -> { f with WhatsApp = v })
        field "tags" msgs.FieldTags msgs.PlaceholderTags form.Tags (fun f v -> { f with Tags = v })
        field "notes" msgs.FieldNotes msgs.PlaceholderNotes form.Notes (fun f v -> { f with Notes = v })
        Nodes.Layout.row "contact-form-actions" [
            Nodes.Actions.button "contact-form-save" ButtonVariant.Primary msgs.SaveContact busy SubmitForm
            Nodes.Actions.secondary "contact-form-cancel" msgs.Cancel CancelForm
        ]
    ]

// ─── One contact ─────────────────────────────────────────────────────

type private ConsentContext = {
    Msgs: ExternalContactMessages
    Contact: ExternalContact
}

/// What a contact has agreed to: a row per channel with the evidence and a
/// withdrawal beside it, kept whole on the contact it lists.
let private consentList =
    One(
        (fun (a: ConsentContext) b -> sameObject a.Msgs b.Msgs && sameObject a.Contact b.Contact),
        fun context ->
            let msgs = context.Msgs
            let recorded = consents context.Contact

            if List.isEmpty recorded then
                Nodes.text "consents-none" msgs.NoConsentBody
            else
                Nodes.Layout.column "consents" [
                    for channel, record in recorded ->
                        let id = Nodes.Ids.keyed "consent" channel

                        Fuaran.box id {
                            Defaults.box with
                                Role = BoxRole.Card
                                Children = [
                                    Fuaran.box (Nodes.Ids.child id "grid") {
                                        Defaults.box with
                                            Layout = BoxLayout.Grid(2, Some "minmax(0, 1fr) auto", Some 16)
                                            Children = [
                                                Nodes.Layout.column (Nodes.Ids.child id "about") [
                                                    Nodes.text (Nodes.Ids.child id "channel") channel
                                                    Nodes.text
                                                        (Nodes.Ids.child id "evidence")
                                                        (msgs.ConsentEvidence record.Source)
                                                ]
                                                Nodes.Actions.secondary
                                                    (Nodes.Ids.child id "withdraw")
                                                    msgs.WithdrawConsent
                                                    (WithdrawOptIn(context.Contact.Id, channel))
                                            ]
                                    }
                                ]
                        }
                ]
    )

/// The consent panel: the list, then the form that records a new consent.
/// Its record action stays visible and inert while the source is blank, so the
/// operator sees what it is waiting for.
let private consentPanel
    (msgs: ExternalContactMessages)
    (model: Model)
    (contact: ExternalContact)
    (dispatch: Msg -> unit)
    =
    Nodes.Panel.card "consent" msgs.ConsentHeading [
        Nodes.text "consent-help" msgs.ConsentHelp
        consentList.Node { Msgs = msgs; Contact = contact }
        Nodes.Layout.column "consent-record" [
            Nodes.text "consent-record-help" msgs.RecordConsentHelp
            Nodes.Forms.dropdown
                "consent-record-channel"
                msgs.FieldChannel
                [ for option in channelOptions -> option, option ]
                model.OptIn.Channel
                SetOptInChannel
            TreeIslands.textField
                "consent-record-source"
                msgs.FieldConsentSource
                msgs.PlaceholderConsentSource
                model.OptIn.Source
                (SetOptInSource >> dispatch)
            Nodes.Actions.button
                "consent-record-submit"
                ButtonVariant.Primary
                msgs.RecordConsent
                (model.Busy || System.String.IsNullOrWhiteSpace model.OptIn.Source)
                (RecordOptIn contact.Id)
        ]
    ]

let private detail (msgs: ExternalContactMessages) (model: Model) (contactId: string) (dispatch: Msg -> unit) =
    match model.Contacts |> List.tryFind (fun c -> c.Id = contactId) with
    | None ->
        Nodes.Layout.column "contact-gone" [
            Nodes.text "contact-gone-text" msgs.ContactGone
            Nodes.Actions.secondary "contact-gone-back" msgs.BackToList BackToList
        ]
    | Some contact ->
        Nodes.Layout.column "contact" [
            Nodes.Layout.row "contact-title" [
                Nodes.heading "contact-name" 3 contact.DisplayName
                Nodes.Actions.secondary "contact-back" msgs.BackToList BackToList
            ]
            match model.Form with
            | Some form when form.EditingId = Some contact.Id -> formPanel msgs model.Busy form dispatch
            | _ ->
                Nodes.Panel.card "contact-details" msgs.DetailsPanel [
                    Nodes.text
                        "contact-email"
                        (msgs.DetailEmail(contact.OptionalEmailAddress |> Option.defaultValue "—"))
                    Nodes.text
                        "contact-phone"
                        (msgs.DetailPhone(contact.OptionalPhoneNumber |> Option.defaultValue "—"))
                    Nodes.text "contact-tags" (msgs.DetailTags(String.concat ", " contact.Tags))
                    Nodes.Layout.row "contact-actions" [
                        Nodes.Actions.secondary "contact-edit" msgs.EditContact (StartEdit contact.Id)
                        Nodes.Actions.secondary "contact-delete" msgs.DeleteContact (DeleteContact contact.Id)
                    ]
                ]
            consentPanel msgs model contact dispatch
        ]

// ─── The page ────────────────────────────────────────────────────────

/// The page: the heading, the error, then the list or one contact.
/// `dispatch` reaches the form's islands only; every node sends its message
/// through the tree.
let tree (msgs: ExternalContactMessages) (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
    Nodes.Layout.column "contacts-page" [
        Nodes.heading "contacts-heading" 2 msgs.Heading
        Nodes.text "contacts-subheading" msgs.Subheading
        match model.Error with
        | Some(Notice.Problem text) ->
            Nodes.Actions.banner "contacts-error" ToneVariant.Critical text msgs.Dismiss DismissError
        | Some(Notice.Info text) ->
            Nodes.Actions.banner "contacts-error" ToneVariant.Info text msgs.Dismiss DismissError
        | None -> ()
        if not model.Loaded then
            Nodes.text "contacts-loading" msgs.Loading
        else
            match model.CurrentView with
            | ContactDetail contactId -> detail msgs model contactId dispatch
            | ContactList ->
                Nodes.Panel.card "contacts" msgs.ContactsPanel [
                    if model.Contacts.IsEmpty then
                        Nodes.text "contacts-none" msgs.NoContactsYet
                    else
                        Nodes.Table.table
                            "contacts-table"
                            msgs.ContactsPanel
                            (columns msgs)
                            (contactRows.List msgs model.Contacts)
                ]

                match model.Form with
                | Some form when form.EditingId.IsNone -> formPanel msgs model.Busy form dispatch
                | _ -> Nodes.Actions.primary "contacts-add" msgs.AddContact StartAdd
    ]

/// The deferred component `ExternalContactManagerUI`'s view mounts.
let Surface (props: ExternalContactManagerUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Model props.Dispatch)
    )