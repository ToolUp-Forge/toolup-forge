// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GoogleIdentity.GoogleIdentityConfig

open ToolUp.Platform

// ─── Browser-side Google Identity Services config ────────────────────
//
// Companion-specific UI config for the Google Identity Services (GIS)
// sign-in surface: the rendered branded button and — strictly opt-in —
// the One Tap auto-prompt.
//
// This is a UX upgrade over the redirect flow, not a replacement for
// it. `OidcPresets.google` + `OidcRegister.handler` already give a
// deployment complete, functional Google sign-in; this companion adds
// the button Google's brand guidelines specify and the One Tap prompt,
// both of which require loading Google's own JavaScript library. A
// deployment that does not compose this companion carries zero GIS
// bytes (GP 13).
//
// The record projects to the generic `OidcUIConfig` so the session
// this companion mints lands in exactly the same token store as a
// redirect-flow session — `classifyStoredToken`, `signOut` and the
// pre-expiry refresh timer all behave identically whichever entry the
// user took. There is no parallel session machinery.

/// Visual theme of the rendered Google button. Maps to the GIS
/// `theme` option; the values are Google's, restated as a closed DU
/// so a typo is a compile error rather than a silently-default button.
type GoogleButtonTheme =
    /// `outline` — the GIS default: white surface, grey border.
    | OutlineButton
    /// `filled_blue` — Google-blue surface, white text.
    | FilledBlueButton
    /// `filled_black` — black surface, white text.
    | FilledBlackButton

/// Rendered button size. Maps to the GIS `size` option.
type GoogleButtonSize =
    | LargeButton
    | MediumButton
    | SmallButton

/// Button caption. Maps to the GIS `text` option.
type GoogleButtonText =
    /// `signin_with` — "Sign in with Google" (the GIS default).
    | SignInWithGoogle
    /// `signup_with` — "Sign up with Google".
    | SignUpWithGoogle
    /// `continue_with` — "Continue with Google".
    | ContinueWithGoogle
    /// `signin` — "Sign in".
    | SignInOnly

/// Button outline shape. Maps to the GIS `shape` option.
type GoogleButtonShape =
    | RectangularButton
    | PillButton
    | CircleButton
    | SquareButton

/// Inputs the consumer supplies in
/// `ClientConfig.AuthUI = GoogleIdentityRegister.authUI config`.
///
/// The only required input is `ClientId` — Google's issuer is a fixed
/// constant (`https://accounts.google.com`), so unlike every other
/// provider there is no tenant, region, or custom-domain value to get
/// wrong.
type GoogleIdentityUIConfig = {
    /// OAuth 2.0 Client ID from the Google Cloud console — the same
    /// value `OidcPresets.google` takes. Becomes the GIS `client_id`
    /// and the `aud` claim the credential bridge binds against.
    ClientId: string
    /// The registered redirect URI, when the deployment ALSO wires the
    /// redirect flow off the same identity. GIS's default popup UX
    /// never redirects, so this companion does not use the value for
    /// sign-in — it is carried into the projected `OidcUIConfig` so a
    /// deployment running both entry points has one source of truth.
    RedirectUri: string option
    /// Opt IN to the One Tap auto-prompt (default `false` — button
    /// only). Auto-prompting a returning visitor is a product decision
    /// the SDK must not make on a deployment's behalf (GP 11), so the
    /// default composition renders the branded button and nothing else.
    OneTap: bool
    /// GIS `auto_select`: sign a returning user straight back in
    /// without a click when exactly one Google session matches.
    /// Default `false`, and only reachable at all when `OneTap` is on.
    AutoSelect: bool
    /// GIS `cancel_on_tap_outside`: dismiss the One Tap prompt when
    /// the user clicks outside it. Default `true` (Google's default).
    CancelOneTapOnTapOutside: bool
    /// GIS `use_fedcm_for_prompt`: route One Tap through the browser's
    /// FedCM API. Default `true` — Chrome requires FedCM for One Tap
    /// as third-party cookies are withdrawn, and the option is inert
    /// where the browser does not implement it.
    UseFedCm: bool
    /// Optional nonce sent to Google on `initialize` and required back
    /// on the returned credential's `nonce` claim. When `Some`, the
    /// bridge REFUSES a credential whose nonce does not match
    /// (`NonceMismatch`) — the same binding the redirect flow performs
    /// on its id_token. When `None` no nonce is sent and none is
    /// checked; Google does not require one for the credential flow.
    Nonce: string option
    /// Visual theme of the rendered button.
    ButtonTheme: GoogleButtonTheme
    /// Rendered button size.
    ButtonSize: GoogleButtonSize
    /// Button caption.
    ButtonText: GoogleButtonText
    /// Button outline shape.
    ButtonShape: GoogleButtonShape
    /// Explicit button width in pixels (GIS caps this at 400). `None`
    /// lets GIS size the button to its caption.
    ButtonWidthPx: int option
    /// Optional override for the OIDC `post_logout_redirect_uri`,
    /// forwarded to the shared sign-out path.
    PostLogoutRedirectUri: string option
    /// Heading rendered above the button on the sign-in screen.
    /// `None` uses "Welcome".
    Heading: string option
    /// Sub-heading rendered above the button. `None` uses
    /// "Sign in to continue."
    Subheading: string option
}

module GoogleIdentityUIConfig =
    /// Google's issuer — a fixed constant with no tenant, region or
    /// custom-domain variant. Stated here as well as in
    /// `OidcPresets.google` because this companion must not take a
    /// package dependency direction it does not otherwise need, and
    /// because the credential bridge compares an incoming `iss` claim
    /// against it.
    [<Literal>]
    let Issuer = "https://accounts.google.com"

    /// The scope set carried into the projected `OidcUIConfig`. GIS's
    /// credential flow requests no scopes of its own — it returns an
    /// id_token and nothing else — so these exist only to keep the
    /// projected record coherent for the shared sign-out path and for
    /// a deployment that also runs the redirect flow. Matches
    /// `OidcPresets.google`.
    let projectedScopes = [ "openid"; "profile"; "email" ]

    /// Construct a config from the one genuinely required input.
    /// Every other field defaults to the conservative choice: button
    /// only (no One Tap), no auto-select, no nonce, Google's own
    /// default button styling.
    let create (clientId: string) = {
        ClientId = clientId
        RedirectUri = None
        OneTap = false
        AutoSelect = false
        CancelOneTapOnTapOutside = true
        UseFedCm = true
        Nonce = None
        ButtonTheme = OutlineButton
        ButtonSize = LargeButton
        ButtonText = SignInWithGoogle
        ButtonShape = RectangularButton
        ButtonWidthPx = None
        PostLogoutRedirectUri = None
        Heading = None
        Subheading = None
    }

    /// Turn One Tap on. Separate from `create` so the opt-in is
    /// visible at the call site rather than buried in a record
    /// literal a reviewer skims.
    let withOneTap (config: GoogleIdentityUIConfig) = { config with OneTap = true }

    /// Project to the shape the generic OIDC client primitives expect.
    /// This is the whole of the "same session shape" guarantee: the
    /// credential bridge, `classifyStoredToken`, `signOut` and the
    /// refresh timer all take THIS value, so a GIS session and a
    /// redirect-flow session are the same session as far as every
    /// downstream consumer is concerned.
    ///
    /// `ValidateIdToken = Some true` because the credential Google
    /// hands back IS an id_token and it becomes the bearer — the
    /// deployment's most security-relevant value. The redirect flow's
    /// Google path makes the same choice.
    let toOidcUIConfig (config: GoogleIdentityUIConfig) : OidcUIConfig = {
        Issuer = Issuer
        ClientId = config.ClientId
        RedirectUri = config.RedirectUri |> Option.defaultValue ""
        Scopes = projectedScopes
        PostLogoutRedirectUri = config.PostLogoutRedirectUri
        ValidateIdToken = Some true
    }

    /// GIS wire value for a button theme.
    let themeValue =
        function
        | OutlineButton -> "outline"
        | FilledBlueButton -> "filled_blue"
        | FilledBlackButton -> "filled_black"

    /// GIS wire value for a button size.
    let sizeValue =
        function
        | LargeButton -> "large"
        | MediumButton -> "medium"
        | SmallButton -> "small"

    /// GIS wire value for a button caption.
    let textValue =
        function
        | SignInWithGoogle -> "signin_with"
        | SignUpWithGoogle -> "signup_with"
        | ContinueWithGoogle -> "continue_with"
        | SignInOnly -> "signin"

    /// GIS wire value for a button shape.
    let shapeValue =
        function
        | RectangularButton -> "rectangular"
        | PillButton -> "pill"
        | CircleButton -> "circle"
        | SquareButton -> "square"