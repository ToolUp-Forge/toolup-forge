// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ServiceAccountUI

open System
open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Phase 527 — service-account admin ───────────────────────────────
//
// Owner/Admin surface over `IServiceAccountApi`: list / create / disable
// service accounts, and mint / revoke their scoped API tokens.
//
// **The one-time secret is the design constraint everything else bends
// around.** `MintToken` is the only response in the whole API that ever
// carries a token secret, and there is no server-side copy to fetch
// again — the store keeps a salted hash. So the minted secret is held in
// the Elmish model, displayed in a panel that cannot be dismissed by
// accident, and cleared only by an explicit acknowledgement. Refreshing
// the list, selecting another account, or navigating away would lose it,
// so the panel sits above everything and the acknowledgement is the only
// control that clears it.
//
// **Gating.** `NavRole.TeamOwnerAdmin` keeps the module out of a
// Member's sidebar, but that gate FAILS OPEN while team membership is in
// flight (see `SDK.Client.fs`'s note on the role filter), so it is a
// navigation convenience and not a security boundary. The real gate is
// `ServiceAccountApiHandler`, which refuses a non-Owner/Admin — and
// refuses a machine caller outright, so a service-account token cannot
// reach this API to mint itself more credentials.

// ─── Model ───────────────────────────────────────────────────────────

/// A minted secret awaiting the operator's acknowledgement. Held
/// separately from the token list because it is not part of any list —
/// it is a modal fact with a lifetime of exactly one acknowledgement.
type PendingSecret = {
    Secret: string
    Token: ServiceAccountTokenView
}

type Model = {
    Accounts: ServiceAccount list
    /// Account whose tokens are shown. `None` = the list view.
    SelectedAccountId: string option
    Tokens: ServiceAccountTokenView list
    /// Set by a successful mint; cleared only by `AcknowledgeSecret`.
    PendingSecret: PendingSecret option
    Loaded: bool
    Busy: bool
    /// The page's failure notice, as a `Notice.Problem`. Cleared by
    /// `DismissError` or the next call.
    Error: Notice option
}

type Msg =
    | LoadAccounts
    | AccountsLoaded of Result<ServiceAccount list, string>
    | SelectAccount of string
    | BackToList
    | TokensLoaded of Result<ServiceAccountTokenView list, string>
    | CreateAccount of CreateServiceAccountRequest
    | AccountCreated of Result<ServiceAccount, string>
    | SetStatus of string * ServiceAccountStatus
    | StatusSet of Result<ServiceAccount, string>
    | MintToken of MintServiceAccountTokenRequest
    | TokenMinted of Result<MintedServiceAccountTokenView, string>
    | AcknowledgeSecret
    | RevokeToken of string
    | TokenRevoked of Result<unit, string>
    | DismissError

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see
// WebhookAdminUI.fs. The API uses the default `/api/{type}/{method}`
// shape, so `ServiceAccountApi.routeBuilder` is passed explicitly for
// symmetry with the server-side `Api.make` rather than out of necessity.
let private serviceAccountApi: IServiceAccountApi =
    Api.makeProxy<IServiceAccountApi> (
        routeBuilder = ServiceAccountApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

let private loadAccountsCmd () =
    Cmd.OfRemoting.call serviceAccountApi.ListAccounts () AccountsLoaded (fun e -> AccountsLoaded(Error e.Message))

let private loadTokensCmd (accountId: string) =
    Cmd.OfRemoting.call serviceAccountApi.ListTokens accountId TokensLoaded (fun e -> TokensLoaded(Error e.Message))

// ─── Init / update ───────────────────────────────────────────────────

let init () : Model * Cmd<Msg> =
    {
        Accounts = []
        SelectedAccountId = None
        Tokens = []
        PendingSecret = None
        Loaded = false
        Busy = true
        Error = None
    },
    loadAccountsCmd ()

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | LoadAccounts -> { model with Busy = true; Error = None }, loadAccountsCmd ()

    | AccountsLoaded(Ok accounts) ->
        {
            model with
                Accounts = accounts
                Loaded = true
                Busy = false
                Error = None
        },
        Cmd.none

    | AccountsLoaded(Error err) ->
        {
            model with
                Loaded = true
                Busy = false
                Error = Some(Notice.Problem err)
        },
        Cmd.none

    | SelectAccount accountId ->
        {
            model with
                SelectedAccountId = Some accountId
                Tokens = []
                Busy = true
                Error = None
        },
        loadTokensCmd accountId

    | BackToList ->
        {
            model with
                SelectedAccountId = None
                Tokens = []
                Error = None
        },
        Cmd.none

    | TokensLoaded(Ok tokens) ->
        {
            model with
                Tokens = tokens
                Busy = false
                Error = None
        },
        Cmd.none

    | TokensLoaded(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
        },
        Cmd.none

    | CreateAccount request ->
        { model with Busy = true; Error = None },
        Cmd.OfRemoting.call serviceAccountApi.CreateAccount request AccountCreated (fun e ->
            AccountCreated(Error e.Message))

    | AccountCreated(Ok _) -> { model with Busy = true }, loadAccountsCmd ()

    | AccountCreated(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
        },
        Cmd.none

    | SetStatus(accountId, status) ->
        { model with Busy = true; Error = None },
        Cmd.OfRemoting.call serviceAccountApi.SetAccountStatus (accountId, status) StatusSet (fun e ->
            StatusSet(Error e.Message))

    | StatusSet(Ok _) -> { model with Busy = true }, loadAccountsCmd ()

    | StatusSet(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
        },
        Cmd.none

    | MintToken request ->
        { model with Busy = true; Error = None },
        Cmd.OfRemoting.call serviceAccountApi.MintToken request TokenMinted (fun e -> TokenMinted(Error e.Message))

    | TokenMinted(Ok minted) ->
        // The secret lands in the model and stays there until
        // acknowledged. The token list is refreshed underneath it so the
        // new row is present when the panel clears.
        let refresh =
            match model.SelectedAccountId with
            | Some accountId -> loadTokensCmd accountId
            | None -> Cmd.none

        {
            model with
                PendingSecret =
                    Some {
                        Secret = minted.Secret
                        Token = minted.Token
                    }
                Busy = false
                Error = None
        },
        refresh

    | TokenMinted(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
        },
        Cmd.none

    | AcknowledgeSecret -> { model with PendingSecret = None }, Cmd.none

    | RevokeToken tokenId ->
        { model with Busy = true; Error = None },
        Cmd.OfRemoting.call serviceAccountApi.RevokeToken tokenId TokenRevoked (fun e -> TokenRevoked(Error e.Message))

    | TokenRevoked(Ok()) ->
        let refresh =
            match model.SelectedAccountId with
            | Some accountId -> loadTokensCmd accountId
            | None -> Cmd.none

        { model with Busy = true }, refresh

    | TokenRevoked(Error err) ->
        {
            model with
                Busy = false
                Error = Some(Notice.Problem err)
        },
        Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

// ─── View helpers ────────────────────────────────────────────────────

let private permissionLabel (msgs: ServiceAccountMessages) (perm: ModulePermission) =
    match perm with
    | ModulePermission.Read -> msgs.PermissionRead
    | ModulePermission.Write -> msgs.PermissionWrite
    | ModulePermission.Admin -> msgs.PermissionAdmin
    | ModulePermission.SchemaOnly -> msgs.PermissionSchemaOnly

let private parsePermission (token: string) =
    match token with
    | "Write" -> ModulePermission.Write
    | "Admin" -> ModulePermission.Admin
    | "SchemaOnly" -> ModulePermission.SchemaOnly
    | _ -> ModulePermission.Read

let private pill (label: string) (cls: string) =
    Html.span [
        prop.className $"inline-block text-xs px-2 py-0.5 rounded {cls}"
        prop.text label
    ]

let private statusBadge (msgs: ServiceAccountMessages) (status: ServiceAccountStatus) =
    match status with
    | ServiceAccountStatus.Active -> pill msgs.StatusActive "bg-green-100 text-green-700"
    | ServiceAccountStatus.Disabled -> pill msgs.StatusDisabled "bg-red-100 text-red-700"

/// A token's live state, as the operator needs to read it: revoked and
/// expired are different facts with different remedies, and a token that
/// is both should read as revoked (the deliberate act outranks the
/// lapse) — the same ordering `ServiceAccountTypes.classifyToken`
/// applies server-side.
let private tokenBadge (msgs: ServiceAccountMessages) (token: ServiceAccountTokenView) =
    if token.Revoked then
        pill msgs.StatusRevoked "bg-red-100 text-red-700"
    elif token.ExpiresAt <= DateTimeOffset.UtcNow then
        pill msgs.StatusExpired "bg-yellow-100 text-yellow-800"
    else
        pill msgs.StatusActive "bg-green-100 text-green-700"

let private permissionSummary (msgs: ServiceAccountMessages) (permissions: Map<string, ModulePermission list>) =
    Html.div [
        prop.className "flex gap-1 flex-wrap"
        prop.children [
            for KeyValue(moduleName, perms) in permissions ->
                // The joined label is bound out rather than interpolated
                // inline: F# forbids a string literal inside an
                // interpolation hole in a single-quoted string (FS3373),
                // and `String.concat` needs one.
                let granted = perms |> List.map (permissionLabel msgs) |> String.concat ", "
                pill $"{moduleName}: {granted}" ("bg-gray-100 font-mono " + Tokens.Text.secondary)
        ]
    ]

/// The one-time secret panel. Deliberately loud, deliberately hard to
/// dismiss by reflex: this value cannot be recovered, and the only
/// remedy for losing it is minting a replacement and revoking this one.
let private secretPanel (msgs: ServiceAccountMessages) (pending: PendingSecret) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "mb-4 p-4 bg-amber-50 border-2 border-amber-400 rounded-lg"
        prop.children [
            Html.h3 [
                prop.className "text-sm font-semibold text-amber-900 mb-1"
                prop.text (msgs.CopyTokenHeading pending.Token.DisplayName)
            ]
            Html.p [
                prop.className "text-xs text-amber-800 mb-3"
                prop.text msgs.SecretOneTimeBody
            ]
            Html.pre [
                prop.className "bg-white border border-amber-300 rounded px-3 py-2 text-xs font-mono break-all mb-3"
                prop.text pending.Secret
            ]
            Html.button [
                prop.className "px-4 py-2 text-sm rounded-lg text-white bg-amber-600 hover:bg-amber-700 cursor-pointer"
                prop.text msgs.AcknowledgeSecret
                prop.onClick (fun _ -> dispatch AcknowledgeSecret)
            ]
        ]
    ]

/// Create-account form. Permissions are built up one module at a time
/// rather than typed as free text, because the map is the account's
/// entire authority and a typo in a module name is a silently-narrower
/// credential — the "add" step makes each grant a deliberate act, and
/// the pending list makes the whole set visible before submit.
[<ReactComponent>]
let private CreateAccountForm (busy: bool) (onSubmit: CreateServiceAccountRequest -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).ServiceAccount
    let name, setName = React.useState ""
    let moduleName, setModuleName = React.useState ""
    let permission, setPermission = React.useState "Read"

    let permissions, setPermissions =
        React.useState (Map.empty<string, ModulePermission list>)

    let addPermission () =
        let trimmed = moduleName.Trim()

        if trimmed <> "" then
            setPermissions (permissions |> Map.add trimmed [ parsePermission permission ])
            setModuleName ""

    // An empty permission map is refused server-side — an empty map reads
    // as UNRESTRICTED everywhere else in the platform — so the submit
    // button enforces the same rule here rather than letting the operator
    // discover it as an error.
    let canSubmit = not busy && name.Trim() <> "" && not permissions.IsEmpty
    let blocked = name.Trim() = "" || permissions.IsEmpty

    Html.div [
        prop.className "bg-white rounded-lg border border-border p-4 mb-4"
        prop.children [
            Html.h3 [
                prop.className "text-sm font-semibold mb-3"
                prop.text msgs.NewAccountHeading
            ]

            Html.label [
                prop.className [ "block text-xs font-medium mb-1"; Tokens.Text.secondary ]
                prop.text msgs.NameLabel
            ]
            Html.input [
                prop.type' "text"
                prop.value name
                prop.placeholder msgs.NamePlaceholder
                prop.onChange (fun (v: string) -> setName v)
                prop.className
                    "border border-border rounded-lg px-4 py-2 focus:outline-none focus:border-brand w-full text-sm mb-3"
            ]

            Html.label [
                prop.className [ "block text-xs font-medium mb-1"; Tokens.Text.secondary ]
                prop.text msgs.ModulePermissionsLabel
            ]
            Html.div [
                prop.className "flex gap-2 mb-2"
                prop.children [
                    Html.input [
                        prop.type' "text"
                        prop.value moduleName
                        prop.placeholder msgs.ModuleNamePlaceholder
                        prop.onChange (fun (v: string) -> setModuleName v)
                        prop.onKeyDown (fun e ->
                            if e.key = "Enter" then
                                addPermission ())
                        prop.className
                            "border border-border rounded-lg px-3 py-2 focus:outline-none focus:border-brand flex-1 font-mono text-xs"
                    ]
                    Html.select [
                        prop.value permission
                        prop.onChange (fun (v: string) -> setPermission v)
                        prop.className "border border-border rounded-lg px-3 py-2 text-xs"
                        prop.children [
                            Html.option [ prop.value "Read"; prop.text msgs.PermissionRead ]
                            Html.option [ prop.value "Write"; prop.text msgs.PermissionWrite ]
                            Html.option [ prop.value "Admin"; prop.text msgs.PermissionAdmin ]
                            Html.option [ prop.value "SchemaOnly"; prop.text msgs.PermissionSchemaOnly ]
                        ]
                    ]
                    Forms.Button.secondaryWith Forms.Button.Enabled msgs.AddPermission addPermission
                ]
            ]

            if permissions.IsEmpty then
                Html.p [
                    prop.className [ "text-xs mb-3"; Tokens.Text.muted ]
                    prop.text msgs.NoPermissionsHint
                ]
            else
                Html.div [ prop.className "mb-3"; prop.children [ permissionSummary msgs permissions ] ]

            Forms.Button.primaryWith (Forms.Button.stateOf busy msgs.Working blocked) msgs.CreateAccount (fun () ->
                if canSubmit then
                    onSubmit {
                        DisplayName = name.Trim()
                        Permissions = permissions
                    }

                    setName ""
                    setPermissions Map.empty)
        ]
    ]

/// Mint form for the selected account. Expiry is a day count rather than
/// a date picker because the operational question is "how long should
/// this live", and the default is the substrate's own 90 days.
[<ReactComponent>]
let private MintTokenForm (accountId: string) (busy: bool) (onSubmit: MintServiceAccountTokenRequest -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).ServiceAccount
    let label, setLabel = React.useState ""
    let days, setDays = React.useState "90"

    let canSubmit = not busy && label.Trim() <> ""

    let submit () =
        if canSubmit then
            let expiresAt =
                match Int32.TryParse(days.Trim()) with
                | true, d when d > 0 -> Some(DateTimeOffset.UtcNow.AddDays(float d))
                | _ -> None

            onSubmit {
                AccountId = accountId
                DisplayName = label.Trim()
                ExpiresAt = expiresAt
            }

            setLabel ""

    Html.div [
        prop.className "bg-white rounded-lg border border-border p-4 mb-4"
        prop.children [
            Html.h3 [ prop.className "text-sm font-semibold mb-3"; prop.text msgs.MintTokenHeading ]
            Html.div [
                prop.className "flex gap-2"
                prop.children [
                    Html.input [
                        prop.type' "text"
                        prop.value label
                        prop.placeholder msgs.MintLabelPlaceholder
                        prop.onChange (fun (v: string) -> setLabel v)
                        prop.onKeyDown (fun e ->
                            if e.key = "Enter" then
                                submit ())
                        prop.className
                            "border border-border rounded-lg px-3 py-2 focus:outline-none focus:border-brand flex-1 text-sm"
                    ]
                    Html.input [
                        prop.type' "number"
                        prop.value days
                        prop.onChange (fun (v: string) -> setDays v)
                        prop.className "border border-border rounded-lg px-3 py-2 w-24 text-sm"
                    ]
                    Html.span [
                        prop.className [ "self-center text-xs"; Tokens.Text.muted ]
                        prop.text msgs.Days
                    ]
                    Forms.Button.primaryWith
                        (Forms.Button.stateOf busy msgs.Working (label.Trim() = ""))
                        msgs.Mint
                        submit
                ]
            ]
        ]
    ]

let private accountsTable (msgs: ServiceAccountMessages) (model: Model) (dispatch: Msg -> unit) =
    if List.isEmpty model.Accounts then
        Html.div [
            prop.className "bg-white rounded-lg border border-border p-6 text-center"
            prop.children [
                Html.p [
                    prop.className [ "text-sm"; Tokens.Text.secondary ]
                    prop.text msgs.NoAccountsHeading
                ]
                Html.p [
                    prop.className [ "text-xs mt-1"; Tokens.Text.muted ]
                    prop.text msgs.NoAccountsBody
                ]
            ]
        ]
    else
        Html.div [
            prop.className "bg-white rounded-lg border border-border divide-y divide-border"
            prop.children [
                for account in model.Accounts ->
                    Html.div [
                        prop.className "p-4 flex items-start justify-between gap-4"
                        prop.children [
                            Html.div [
                                prop.className "flex-1 min-w-0"
                                prop.children [
                                    Html.div [
                                        prop.className "flex items-baseline gap-2 mb-1 flex-wrap"
                                        prop.children [
                                            Html.span [
                                                prop.className "text-sm font-semibold"
                                                prop.text account.DisplayName
                                            ]
                                            statusBadge msgs account.Status
                                            Html.span [
                                                prop.className [ "text-xs font-mono break-all"; Tokens.Text.muted ]
                                                prop.text account.AccountId
                                            ]
                                        ]
                                    ]
                                    permissionSummary msgs account.Permissions
                                ]
                            ]
                            Html.div [
                                prop.className "flex gap-2 shrink-0"
                                prop.children [
                                    Forms.Button.secondaryWith Forms.Button.Enabled msgs.Tokens (fun () ->
                                        dispatch (SelectAccount account.AccountId))
                                    Forms.Button.secondaryWith
                                        (Forms.Button.stateOf false msgs.Working model.Busy)
                                        (match account.Status with
                                         | ServiceAccountStatus.Active -> msgs.Disable
                                         | ServiceAccountStatus.Disabled -> msgs.Enable)
                                        (fun () ->
                                            let next =
                                                match account.Status with
                                                | ServiceAccountStatus.Active -> ServiceAccountStatus.Disabled
                                                | ServiceAccountStatus.Disabled -> ServiceAccountStatus.Active

                                            dispatch (SetStatus(account.AccountId, next)))
                                ]
                            ]
                        ]
                    ]
            ]
        ]

let private tokensTable (msgs: ServiceAccountMessages) (model: Model) (dispatch: Msg -> unit) =
    if List.isEmpty model.Tokens then
        Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.NoTokensYet ]
    else
        Html.div [
            prop.className "bg-white rounded-lg border border-border divide-y divide-border"
            prop.children [
                for token in model.Tokens ->
                    // Same FS3373 constraint as `permissionSummary` — the
                    // format literal cannot live inside the interpolation.
                    let issuedOn = token.IssuedAt.ToString "yyyy-MM-dd"
                    let expiresOn = token.ExpiresAt.ToString "yyyy-MM-dd"

                    Html.div [
                        prop.className "p-4 flex items-center justify-between gap-4"
                        prop.children [
                            Html.div [
                                prop.className "flex-1 min-w-0"
                                prop.children [
                                    Html.div [
                                        prop.className "flex items-baseline gap-2 mb-1 flex-wrap"
                                        prop.children [
                                            Html.span [
                                                prop.className "text-sm font-medium"
                                                prop.text token.DisplayName
                                            ]
                                            tokenBadge msgs token
                                        ]
                                    ]
                                    Html.p [
                                        prop.className [ "text-xs"; Tokens.Text.muted ]
                                        prop.text (msgs.TokenIssuedSummary issuedOn token.IssuedBy expiresOn)
                                    ]
                                ]
                            ]
                            if not token.Revoked then
                                Forms.Button.dangerWith
                                    (Forms.Button.stateOf false msgs.Working model.Busy)
                                    msgs.Revoke
                                    (fun () -> dispatch (RevokeToken token.TokenId))
                            else
                                Html.none
                        ]
                    ]
            ]
        ]

/// The module's page, over the catalog section the view hands it.
let private body (msgs: ServiceAccountMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let body =
        match model.SelectedAccountId with
        | None ->
            Html.div [
                prop.children [
                    CreateAccountForm model.Busy (fun request -> dispatch (CreateAccount request))
                    if not model.Loaded then
                        Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.Loading ]
                    else
                        accountsTable msgs model dispatch
                ]
            ]
        | Some accountId ->
            let account = model.Accounts |> List.tryFind (fun a -> a.AccountId = accountId)

            Html.div [
                prop.children [
                    Html.button [
                        prop.className [ "text-xs hover:underline mb-3"; Tokens.Text.secondary ]
                        prop.text msgs.BackToList
                        prop.onClick (fun _ -> dispatch BackToList)
                    ]
                    Html.h3 [
                        prop.className "text-sm font-semibold mb-3"
                        prop.text (
                            match account with
                            | Some a -> msgs.TokensForAccount a.DisplayName
                            | None -> msgs.Tokens
                        )
                    ]
                    MintTokenForm accountId model.Busy (fun request -> dispatch (MintToken request))
                    tokensTable msgs model dispatch
                ]
            ]

    Html.div [
        prop.className "p-6 max-w-4xl"
        prop.children [
            Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text msgs.Heading ]
            Html.p [
                prop.className [ "text-sm mb-4"; Tokens.Text.secondary ]
                prop.text msgs.Subheading
            ]
            (match model.PendingSecret with
             | Some pending -> secretPanel msgs pending dispatch
             | None -> Html.none)
            StateViews.notice { Dismiss = msgs.Dismiss } model.Error (fun () -> dispatch DismissError)
            body
        ]
    ]

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(body ctx.Messages.ServiceAccount model ctx.Dispatch)

// ─── Module creation ─────────────────────────────────────────────────

/// Phase 879 — this built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.ServiceAccountAdmin"

/// The built-in service-account admin (`ShellSlots.builtIn` builds its module).
/// `NavRole.TeamOwnerAdmin` keeps it out of a Member's sidebar; the
/// server-side handler is the enforcement (see the module preamble).
let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Service Accounts"
        Icon = ToolUp.Platform.Icons.lock
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