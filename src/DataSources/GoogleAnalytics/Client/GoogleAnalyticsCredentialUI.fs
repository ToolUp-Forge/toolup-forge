// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.GoogleAnalyticsCredentialUI

open Feliz
open ToolUp.Platform

// ─── GA4 credential UI ───────────────────────────────────────────────
//
// The per-`Kind` credential form the built-in data-ingestion admin
// module inlines under a Google Analytics data source. Three steps, in
// the order an operator has to do them:
//
//   1. Enter the OAuth client id + secret from the Google Cloud project.
//   2. Consent — bounce through the OAuth substrate to Google and back.
//   3. Pick the property to report on, and disconnect when done.
//
// Each step is gated on the one before it having produced something, so
// the panel shows what to do next rather than four controls of which
// three do not yet work.
//
// **Why client-credential persistence arrives through `create`.** The
// SDK ships no wire method for writing a data source's OAuth client id
// and secret: `IDataIngestionApi` has none, and `DataSourceConfig`'s
// `ConnectionScope` is documented as never carrying credentials — which
// is right, since it is persisted as an ordinary config blob. So the
// deployment supplies the path: a `SaveClientCredentials` function that
// reaches whatever endpoint it uses for its own bring-your-own-key
// settings, ending in `ISecretStore.SetSecret` under the two keys the
// flow reads (`GoogleOAuthFlow.clientIdKey` /
// `clientSecretKey`). Same shape as every server-tier companion taking
// its `ISecretStore` through `create` — the companion names what it
// needs, the composition root supplies it.

/// Wiring for the GA4 credential form.
type GoogleAnalyticsCredentialUIConfig = {
    /// The `DataSourceConfig.Kind` this form is registered against.
    /// Must match the connector's `IDataSource.Kind`. Default
    /// `"GoogleAnalytics"`.
    Kind: string
    /// The paired `IOAuthCredentialFlow.Name` — sent to
    /// `IDataIngestionApi.BeginOAuth` as the flow to start, and used in
    /// the help text so an operator reading the panel and an operator
    /// reading a server log see the same word. Default
    /// `"google-analytics"`.
    FlowName: string
    /// Persist the OAuth client id + secret for one data source. See the
    /// module note for why this is supplied rather than shipped.
    /// Arguments: data-source id, client id, client secret.
    SaveClientCredentials: DataSourceId -> string -> string -> Async<Result<unit, string>>
}

module GoogleAnalyticsCredentialUIConfig =
    [<Literal>]
    let DefaultKind = "GoogleAnalytics"

    [<Literal>]
    let DefaultFlowName = "google-analytics"

    /// Standard wiring — you supply only the persistence path.
    let create
        (saveClientCredentials: DataSourceId -> string -> string -> Async<Result<unit, string>>)
        : GoogleAnalyticsCredentialUIConfig =
        {
            Kind = DefaultKind
            FlowName = DefaultFlowName
            SaveClientCredentials = saveClientCredentials
        }

// ─── API proxy ───────────────────────────────────────────────────────

let private dataIngestionApi: IDataIngestionApi =
    Api.makeProxy<IDataIngestionApi> (customOptions = UserSession.withRequestHeaders)

// ─── Presentation helpers ────────────────────────────────────────────

let private stepHeading (index: int) (title: string) (enabled: bool) =
    Html.div [
        prop.className "flex items-center gap-2 mb-2"
        prop.children [
            Html.span [
                prop.className (
                    if enabled then
                        "inline-flex items-center justify-center w-5 h-5 rounded-full text-xs font-semibold bg-green-100 text-green-700"
                    else
                        "inline-flex items-center justify-center w-5 h-5 rounded-full text-xs font-semibold bg-gray-100 text-gray-500"
                )
                prop.text (string index)
            ]
            Html.span [
                prop.className (
                    if enabled then
                        "text-sm font-medium text-gray-900"
                    else
                        "text-sm font-medium text-gray-500"
                )
                prop.text title
            ]
        ]
    ]

let private notice (isError: bool) (text: string) =
    Html.div [
        prop.className (
            if isError then
                "text-xs text-red-700 bg-red-50 border border-red-200 rounded px-2 py-1"
            else
                "text-xs text-green-700 bg-green-50 border border-green-200 rounded px-2 py-1"
        )
        prop.text text
    ]

let private labelledInput (label: string) (inputType: string) (value: string) (onChange: string -> unit) =
    Html.label [
        prop.className "flex flex-col gap-1"
        prop.children [
            Html.span [ prop.className "text-xs font-medium text-gray-700"; prop.text label ]
            Html.input [
                prop.type' inputType
                prop.className "border border-gray-300 rounded px-2 py-1 text-sm"
                prop.value value
                prop.onChange onChange
            ]
        ]
    ]

// ─── Panel ───────────────────────────────────────────────────────────

[<ReactComponent>]
let private GoogleAnalyticsCredentialPanel
    (config: GoogleAnalyticsCredentialUIConfig)
    (source: DataSourceConfig option)
    (refresh: unit -> unit)
    =
    // Text inputs hold their display value in React state and dispatch
    // only on submit — the CLAUDE.md rule; a per-keystroke round-trip
    // through the parent would re-render the whole admin table.
    let clientId, setClientId = React.useState ""
    let clientSecret, setClientSecret = React.useState ""
    // `None` while the first status read is in flight. Distinguished
    // from `Some NotConfigured` so the panel does not flash "not
    // configured" at an operator whose source is in fact connected.
    let status, setStatus = React.useState (None: CredentialStatus option)
    let busy, setBusy = React.useState false
    let message, setMessage = React.useState (None: (bool * string) option)

    let sourceId = source |> Option.map _.Id

    // Re-read whenever the panel is pointed at a different source. The
    // dependency is the id rather than the record: the parent rebuilds
    // the config on every refresh, so a record dependency would re-fetch
    // on every parent render.
    React.useEffect (
        (fun () ->
            match sourceId with
            | None -> setStatus (Some NotConfigured)
            | Some id ->
                async {
                    let! s = dataIngestionApi.GetCredentialStatus id
                    setStatus (Some s)
                }
                |> Async.StartImmediate),
        [| box sourceId |]
    )

    let saveCredentials () =
        match sourceId with
        | None -> setMessage (Some(true, "Save the data source before entering credentials."))
        | Some id ->
            setBusy true
            setMessage None

            async {
                let! result = config.SaveClientCredentials id clientId clientSecret

                match result with
                | Ok() ->
                    // Clear the secret from the browser's memory as soon
                    // as it is persisted. The id is left visible — it is
                    // not secret, and an operator checking they pasted
                    // the right project wants to see it.
                    setClientSecret ""
                    setMessage (Some(false, "Client credentials saved."))
                    let! s = dataIngestionApi.GetCredentialStatus id
                    setStatus (Some s)
                    refresh ()
                | Error err -> setMessage (Some(true, err))

                setBusy false
            }
            |> Async.StartImmediate

    let beginConsent () =
        match sourceId with
        | None -> setMessage (Some(true, "Save the data source before connecting."))
        | Some id ->
            setBusy true
            setMessage None

            async {
                let! result = dataIngestionApi.BeginOAuth(id, config.FlowName)

                match result with
                | Ok url ->
                    // Full-page navigation to Google. `assign` leaves a
                    // history entry so a cancelled consent back-buttons
                    // to here rather than out of the app.
                    Browser.Dom.window.location.assign url
                | Error err ->
                    setMessage (Some(true, err))
                    setBusy false
            }
            |> Async.StartImmediate

    let disconnect () =
        match sourceId with
        | None -> ()
        | Some id ->
            setBusy true
            setMessage None

            async {
                let! result = dataIngestionApi.Disconnect id

                match result with
                | Ok() ->
                    setMessage (Some(false, "Disconnected. Reconnect to resume reporting."))
                    let! s = dataIngestionApi.GetCredentialStatus id
                    setStatus (Some s)
                    refresh ()
                | Error err -> setMessage (Some(true, err))

                setBusy false
            }
            |> Async.StartImmediate

    let selectProperty (property: string) =
        match source with
        | None -> ()
        | Some ds ->
            setBusy true

            async {
                let updated = {
                    ds with
                        ConnectionScope = ds.ConnectionScope |> Map.add "property_id" property
                }

                let! result = dataIngestionApi.SaveDataSource updated

                match result with
                | Ok() ->
                    setMessage (Some(false, $"Reporting on {property}."))
                    refresh ()
                | Error err -> setMessage (Some(true, err))

                setBusy false
            }
            |> Async.StartImmediate

    // Step gating. `hasClientCredentials` is inferred from the credential
    // status rather than tracked separately: the server flips a source
    // out of `NotConfigured` once the client id and secret are present,
    // which is exactly the question step 2 is asking.
    let hasClientCredentials =
        match status with
        | Some NotConfigured
        | None -> false
        | Some _ -> true

    let connected =
        match status with
        | Some(Connected _) -> true
        | _ -> false

    let selectedProperty =
        source
        |> Option.bind (fun ds -> ds.ConnectionScope |> Map.tryFind "property_id")

    let knownProperties = source |> Option.bind _.Tables |> Option.defaultValue []

    Html.div [
        prop.className "flex flex-col gap-4 p-3 border border-gray-200 rounded bg-gray-50"
        prop.children [
            // ── Step 1 — client credentials ──────────────────────────
            Html.div [
                prop.children [
                    stepHeading 1 "Google Cloud OAuth client" true
                    Html.p [
                        prop.className "text-xs text-gray-600 mb-2"
                        prop.text
                            "Create an OAuth 2.0 Client ID of type \"Web application\" in the Google Cloud project, register this deployment's callback as an authorised redirect URI, and paste the credentials here. They are written to the server's secret store and never returned to the browser."
                    ]
                    Html.div [
                        prop.className "flex flex-col gap-2"
                        prop.children [
                            labelledInput "Client ID" "text" clientId setClientId
                            labelledInput "Client secret" "password" clientSecret setClientSecret
                            Html.button [
                                prop.type' "button"
                                prop.className
                                    "self-start text-sm px-3 py-1 rounded bg-gray-800 text-white disabled:opacity-50"
                                prop.disabled (busy || sourceId.IsNone || clientId = "" || clientSecret = "")
                                prop.text "Save"
                                prop.onClick (fun _ -> saveCredentials ())
                            ]
                        ]
                    ]
                ]
            ]

            // ── Step 2 — consent ─────────────────────────────────────
            Html.div [
                prop.children [
                    stepHeading 2 "Authorise Google Analytics access" hasClientCredentials
                    Html.p [
                        prop.className "text-xs text-gray-600 mb-2"
                        prop.text
                            "Sends you to Google to grant this deployment read-only access to your Analytics data. You will be returned here when consent completes."
                    ]
                    Html.button [
                        prop.type' "button"
                        prop.className "text-sm px-3 py-1 rounded bg-blue-600 text-white disabled:opacity-50"
                        prop.disabled (busy || not hasClientCredentials)
                        prop.text (
                            if connected then
                                "Reconnect Google Analytics"
                            else
                                "Connect Google Analytics"
                        )
                        prop.onClick (fun _ -> beginConsent ())
                    ]
                ]
            ]

            // ── Step 3 — connected state ─────────────────────────────
            Html.div [
                prop.children [
                    stepHeading 3 "Reporting property" connected
                    match status with
                    | Some(Connected at) ->
                        Html.div [
                            prop.className "flex flex-col gap-2"
                            prop.children [
                                Html.span [
                                    prop.className "text-xs text-green-700"
                                    prop.text $"Connected — last refreshed {at:``yyyy-MM-dd HH:mm``} UTC"
                                ]

                                if List.isEmpty knownProperties then
                                    // The admin API surface has no
                                    // client-callable property listing,
                                    // so the picker offers what the
                                    // source's saved `Tables` carries.
                                    // An empty list is the honest "run a
                                    // discovery pass first" state, not an
                                    // error.
                                    Html.span [
                                        prop.className "text-xs text-gray-500"
                                        prop.text
                                            "No properties discovered yet. Trigger a refresh on this data source to enumerate the properties this account can see."
                                    ]
                                else
                                    Html.label [
                                        prop.className "flex flex-col gap-1"
                                        prop.children [
                                            Html.span [
                                                prop.className "text-xs font-medium text-gray-700"
                                                prop.text "Property"
                                            ]
                                            Html.select [
                                                prop.className "border border-gray-300 rounded px-2 py-1 text-sm"
                                                prop.disabled busy
                                                prop.value (selectedProperty |> Option.defaultValue "")
                                                prop.onChange selectProperty
                                                prop.children [
                                                    Html.option [ prop.value ""; prop.text "— select a property —" ]
                                                    for property in knownProperties do
                                                        Html.option [ prop.value property; prop.text property ]
                                                ]
                                            ]
                                        ]
                                    ]

                                Html.button [
                                    prop.type' "button"
                                    prop.className
                                        "self-start text-sm px-3 py-1 rounded border border-red-300 text-red-700 disabled:opacity-50"
                                    prop.disabled busy
                                    prop.text "Disconnect"
                                    prop.onClick (fun _ -> disconnect ())
                                ]
                            ]
                        ]
                    | Some(NeedsReauthorization reason) ->
                        Html.span [
                            prop.className "text-xs text-red-700"
                            prop.text $"Reconnect required — {reason}"
                        ]
                    | _ ->
                        Html.span [
                            prop.className "text-xs text-gray-500"
                            prop.text "Available once Google Analytics is connected."
                        ]
                ]
            ]

            match message with
            | Some(isError, text) -> notice isError text
            | None -> Html.none
        ]
    ]

// ─── Registration ────────────────────────────────────────────────────

/// Build the `(Kind, handler)` pair for
/// `ClientConfig.Handlers.DataSourceCredentialHandlers`.
///
/// ```fsharp
/// Handlers = {
///     ClientHandlerRegistry.empty with
///         DataSourceCredentialHandlers = [
///             GoogleAnalyticsCredentialUI.handler
///                 (GoogleAnalyticsCredentialUIConfig.create saveClientCredentials)
///         ]
/// }
/// ```
let handler (config: GoogleAnalyticsCredentialUIConfig) : string * DataSourceCredentialHandler =
    config.Kind,
    fun (ctx: DataSourceCredentialUIContext) -> GoogleAnalyticsCredentialPanel config ctx.DataSource ctx.Refresh