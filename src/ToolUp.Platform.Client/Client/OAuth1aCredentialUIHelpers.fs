// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Feliz

// ─── Phase 10g — OAuth 1.0a credential form helpers ─────────────────────
//
// Feliz helpers for the OAuth 1.0a credential-form pattern (consumer key +
// secret inputs → Save → "Authorize"). A connector companion wires
// `credentialForm` into its `DataSourceCredentialHandler` the same way the
// OAuth 2.0 connectors render their consent button — the 1.0a difference is
// the two consumer-credential fields and the `/api/oauth1a/{flow}/start`
// (rather than `/authorize`) entry point of the three-legged flow.

module OAuth1aCredentialUIHelpers =

    /// Props for the 1.0a credential form.
    type OAuth1aCredentialFormProps = {
        /// The `IOAuth1aFlow.Name` (URL segment for the flow routes).
        FlowName: string
        /// The connection / resource id (the `/start` `resourceId` param).
        ResourceId: string
        /// Current consumer-key value (controlled input).
        ConsumerKey: string
        /// Current consumer-secret value (controlled input).
        ConsumerSecret: string
        /// Consumer-key change handler.
        OnConsumerKeyChange: string -> unit
        /// Consumer-secret change handler.
        OnConsumerSecretChange: string -> unit
        /// Persist the consumer credentials (before authorising).
        OnSave: unit -> unit
        /// Whether the connection already holds an access token.
        Connected: bool
    }

    /// The URL the "Authorize" link navigates to (leg 1 of the flow).
    /// `resourceId` is assumed URL-safe (connection ids are simple
    /// identifiers).
    let startUrl (flowName: string) (resourceId: string) : string =
        sprintf "/api/oauth1a/%s/start?resourceId=%s" flowName resourceId

    /// Render the consumer-key/secret form + Save action + Authorize link,
    /// with the resolved catalog section.
    ///
    /// Phase 751 — the additive form. `credentialForm` below keeps its
    /// arity: it is a public non-component render function, so widening it
    /// would read as a REMOVAL in the public-API approval baseline and
    /// would break every connector companion that wires it into a
    /// `DataSourceCredentialHandler` (444's recorded `…With` pattern).
    /// A connector rendering inside `DataIngestionUI` is already under the
    /// shell's catalog provider, so it can read the section with the hook
    /// and call this form.
    let credentialFormWith (msgs: OAuth1aCredentialMessages) (props: OAuth1aCredentialFormProps) : ReactElement =
        Html.div [
            prop.className "toolup-oauth1a-credential-form"
            prop.children [
                Html.label [ prop.text msgs.ConsumerKeyLabel ]
                Html.input [
                    prop.type' "text"
                    prop.value props.ConsumerKey
                    prop.onChange props.OnConsumerKeyChange
                ]
                Html.label [ prop.text msgs.ConsumerSecretLabel ]
                Html.input [
                    prop.type' "password"
                    prop.value props.ConsumerSecret
                    prop.onChange props.OnConsumerSecretChange
                ]
                Html.button [
                    prop.type' "button"
                    prop.text msgs.Save
                    prop.onClick (fun _ -> props.OnSave())
                ]
                Html.a [
                    prop.href (startUrl props.FlowName props.ResourceId)
                    prop.text (if props.Connected then msgs.Reconnect else msgs.Authorize)
                ]
            ]
        ]

    /// Render the consumer-key/secret form + Save action + Authorize link.
    let credentialForm (props: OAuth1aCredentialFormProps) : ReactElement =
        credentialFormWith MessageCatalog.english.OAuth1aCredential props