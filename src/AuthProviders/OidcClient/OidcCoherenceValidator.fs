// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcCoherenceValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.AuthProviders.Oidc.OidcAppConfig

// ─── OIDC coherence validator ────────────────────────────────────────
//
// Validates an `OidcAppConfig` against a rule set that catches the
// deploy-time misconfigurations hand-rolled OIDC consumers most often
// trip over.  Modelled on `SurfaceCoherenceValidator` (Phase 66
// Stream B.2): one `ValidationResult` per `Validate ()` call,
// aggregating per-rule outcomes (Error wins, then Warning, else Ok).
//
// 0.4.0 BREAKING — `evaluate` and the `OidcCoherenceValidator`
// constructor previously took `(OidcUIConfig * PresetMetadata option)`.
// They now take a single `OidcAppConfig` whose `Preset: PresetKind
// option` field carries the same provenance.  Rules that previously
// relied on `PresetMetadata.AutoAddedScopes` / `.Notes` now derive
// the same data from `PresetKind` via the helpers in `OidcAppConfig`.
//
// Rules (15):
//
//   1. ERROR    Issuer empty.
//   2. ERROR    ClientId empty.
//   3. ERROR    RedirectUri empty.
//   4. ERROR    `openid` scope missing (OIDC spec violation —
//               issuer rejects authorize without it).
//   5. WARN     Issuer not `https://` (dev exceptions:
//               localhost / 127.0.0.1 / IPv6 loopback).
//   6. WARN     RedirectUri not `https://` (same dev
//               exceptions).
//   7. WARN     Preset `entra-workforce` declared but Issuer
//               doesn't contain `login.microsoftonline.com`.
//               Catches the workforce-vs-External-ID paste
//               mistake.
//   8. WARN     Preset `entra-external-id` declared but Issuer
//               is not CIAM-shaped (`*.ciamlogin.com/...` or
//               a custom-domain v2.0 path that isn't workforce).
//   9. WARN     Preset `auth0` declared but Issuer contains
//               `microsoftonline.com`.
//  10. WARN/    Preset's `autoAddedScopes` not all present in
//      ERROR    `Scopes`. Catches the common "consumer override
//               of Scopes dropped a load-bearing preset scope"
//               regression. **For `EntraWorkforce` losing
//               `api://{clientId}/access_as_user` this is an
//               ERROR** — Entra mints an opaque Microsoft Graph
//               token without it and every authenticated request
//               401s post-auth, and the app appears to boot
//               successfully which makes the failure invisible
//               until first API call. For other presets where
//               scope omission is sometimes intentional (e.g.
//               Auth0 dropping `offline_access` when the app
//               doesn't need refresh-token rotation), the
//               outcome stays a WARNING.
//  11. Ok-info  When a preset is declared on the config, a single
//               Ok-class outcome records the preset name + applied
//               quirks for the boot log / `/dev/inspect`
//               validators panel.
//  12. WARN     0.4.3 — `Generic` preset with
//               `ValidateIdToken = None`. The 0.4.3 default
//               flipped to `Some true`; landing on `None` now
//               means an explicit opt-out. Surfaces so the
//               decision is visible at startup rather than
//               assumed-safe by silence.
//  13. WARN     Preset `google` declared but Issuer is not
//               `https://accounts.google.com`. Google's issuer is a
//               fixed constant with no tenant / region variant, so
//               ANY divergence is a mistake — a copied issuer from
//               another preset, or a hand-edit that added a path
//               segment or trailing slash. Distinct from rules 7–9,
//               which have to reason about a parameterised family.
//  14. ERROR    Bearer strategy resolves to `id-token` but
//               `Audience` is not `ClientId`. An id_token's `aud`
//               claim is ALWAYS the client id — the OIDC spec says
//               so — while `Audience` is the value the server-side
//               validator binds against. A deployment that set
//               `Audience` to something else (an Auth0 API
//               identifier, a separately-presented workforce API
//               audience) and then selects the id_token as its
//               bearer has built a configuration in which EVERY
//               authenticated request fails audience validation.
//               Non-recoverable at runtime and invisible until the
//               first API call, which is the same argument that
//               makes rule 10's workforce-Entra arm an Error.
//  15. WARN     A preset whose access-token opacity is unfixable by
//               configuration (`Google`) left on the `access-token`
//               bearer strategy. The sign-in will succeed and every
//               subsequent request will 401, because there is no
//               dashboard knob, scope, or authorize parameter that
//               makes the access token decodable. Deliberately NOT
//               keyed on `expectsDecodableAccessToken` — `Generic`
//               and `Auth0` also answer `false` there, but for
//               reasons a deployment can act on (no SDK knowledge;
//               a configurable API audience), so warning about them
//               would report a non-problem. A Warning rather than an
//               Error because an explicit `Some AccessTokenBearer`
//               is a legitimate choice for a deployment validating
//               the opaque token by some other means.

/// Per-rule outcome.  Aggregator collapses these into a single
/// `ValidationResult`; `evaluate` exposes the raw list for tests +
/// the future `/dev/inspect` validators panel so structure is
/// visible without re-parsing the aggregated message.
type RuleOutcome =
    | RuleOk of message: string
    | RuleWarning of message: string
    | RuleError of message: string

module RuleOutcome =
    let message =
        function
        | RuleOk m
        | RuleWarning m
        | RuleError m -> m

    let isError =
        function
        | RuleError _ -> true
        | _ -> false

    let isWarning =
        function
        | RuleWarning _ -> true
        | _ -> false

    let isOk =
        function
        | RuleOk _ -> true
        | _ -> false

let private isHttps (url: string) = url.StartsWith "https://"

/// Dev-acceptable `http://` exceptions.  Browsers / OIDC issuers
/// accept localhost / 127.0.0.1 / IPv6 loopback as redirect URIs
/// without TLS for local development; everything else needs HTTPS.
let private isDevAcceptableHttp (url: string) =
    url.StartsWith "http://localhost"
    || url.StartsWith "http://127.0.0.1"
    || url.StartsWith "http://[::1]"

let private looksLikeCiam (issuer: string) =
    issuer.Contains "ciamlogin.com"
    || (issuer.Contains "/v2.0" && not (issuer.Contains "login.microsoftonline.com"))

let private collectRules (cfg: OidcAppConfig) : RuleOutcome list = [
    let bearerKind = OidcAppConfig.resolveBearerToken cfg

    // ─── Rule 1 — Issuer empty ──────────────────────────────
    if String.IsNullOrWhiteSpace cfg.Issuer then
        RuleError
            "OidcAppConfig.Issuer is empty. Provide the identity provider's issuer URL — via OidcPresets.entraWorkforce / .entraExternalId / .auth0 / .generic, or via OidcAppConfig.create."

    // ─── Rule 2 — ClientId empty ────────────────────────────
    if String.IsNullOrWhiteSpace cfg.ClientId then
        RuleError
            "OidcAppConfig.ClientId is empty. Provide the app-registration client id (defaults the access-token audience the server-side validator binds against)."

    // ─── Rule 3 — RedirectUri empty ─────────────────────────
    if String.IsNullOrWhiteSpace cfg.RedirectUri then
        RuleError
            "OidcAppConfig.RedirectUri is empty. Provide the registered callback URL (e.g. `https://app.example.com/auth/callback`)."

    // ─── Rule 4 — `openid` scope missing ────────────────────
    if not (List.contains "openid" cfg.Scopes) then
        RuleError
            "OidcAppConfig.Scopes does not contain `openid`. The OIDC spec requires it on every authorize request; the issuer will reject sign-in (and the nonce-binding check in handleCallback assumes an id_token is returned, which requires the `openid` scope)."

    // ─── Rule 5 — Issuer not HTTPS ──────────────────────────
    if
        not (String.IsNullOrWhiteSpace cfg.Issuer)
        && not (isHttps cfg.Issuer)
        && not (isDevAcceptableHttp cfg.Issuer)
    then
        RuleWarning(
            sprintf
                "OidcAppConfig.Issuer (`%s`) is not `https://`. Production OIDC deployments require TLS — the issuer's discovery document and JWKS endpoint are fetched over the wire on every JWKS refresh."
                cfg.Issuer
        )

    // ─── Rule 6 — RedirectUri not HTTPS ─────────────────────
    if
        not (String.IsNullOrWhiteSpace cfg.RedirectUri)
        && not (isHttps cfg.RedirectUri)
        && not (isDevAcceptableHttp cfg.RedirectUri)
    then
        RuleWarning(
            sprintf
                "OidcAppConfig.RedirectUri (`%s`) is not `https://`. Production OIDC issuers reject non-localhost http:// redirect URIs at the authorize endpoint as a phishing-defence."
                cfg.RedirectUri
        )

    // ─── Rule 14 — id_token bearer with a non-clientId audience ──
    // Preset-independent: it applies to a hand-built config that
    // selected the strategy explicitly just as much as to a preset
    // that defaulted into it, which is why it sits outside the
    // preset-aware block below.
    if
        bearerKind = IdTokenBearer
        && not (String.IsNullOrWhiteSpace cfg.ClientId)
        && cfg.Audience <> cfg.ClientId
    then
        RuleError(
            sprintf
                "OidcAppConfig selects the `id-token` bearer strategy but Audience (`%s`) is not ClientId (`%s`). An id_token's `aud` claim is always the client id, so the server-side validator — which binds against Audience — will reject every authenticated request after an otherwise-successful sign-in. Either set Audience = ClientId, or select `BearerToken = Some AccessTokenBearer` and configure the identity provider to issue a decodable access token addressed to `%s`."
                cfg.Audience
                cfg.ClientId
                cfg.Audience
        )

    // ─── Preset-aware rules (7–11, 15) ──────────────────────
    match cfg.Preset with
    | None -> ()
    | Some kind ->
        let label = PresetKind.label kind

        // Rule 7 — entra-workforce + non-workforce issuer.
        if
            label = "entra-workforce"
            && not (cfg.Issuer.Contains "login.microsoftonline.com")
        then
            RuleWarning(
                sprintf
                    "Preset `entra-workforce` declared but OidcAppConfig.Issuer is `%s`, which does not contain `login.microsoftonline.com`. `OidcPresets.entraWorkforce` is for workforce Entra ID / Azure AD only — for Entra External ID use `OidcPresets.entraExternalId`."
                    cfg.Issuer
            )

        // Rule 8 — entra-external-id + non-CIAM issuer.
        if label = "entra-external-id" && not (looksLikeCiam cfg.Issuer) then
            RuleWarning(
                sprintf
                    "Preset `entra-external-id` declared but OidcAppConfig.Issuer is `%s`, which doesn't look like a CIAM issuer (`{tenant}.ciamlogin.com/{tenant}/v2.0` or a custom-domain equivalent). Verify the preset matches your identity provider."
                    cfg.Issuer
            )

        // Rule 9 — auth0 preset but Microsoft-shaped issuer.
        if label = "auth0" && cfg.Issuer.Contains "microsoftonline.com" then
            RuleWarning(
                sprintf
                    "Preset `auth0` declared but OidcAppConfig.Issuer is `%s`, which is a Microsoft Entra issuer. Did you mean `OidcPresets.entraWorkforce`?"
                    cfg.Issuer
            )

        // Rule 13 — google preset but a non-Google issuer.
        // Cheaper to judge than rules 7–9: Google's issuer is a
        // fixed constant, so this is an exact comparison rather
        // than a shape heuristic. Trailing slash tolerated — the
        // discovery fetch normalises it, and refusing it would
        // report a non-problem.
        if label = "google" && cfg.Issuer.TrimEnd '/' <> "https://accounts.google.com" then
            RuleWarning(
                sprintf
                    "Preset `google` declared but OidcAppConfig.Issuer is `%s`, not `https://accounts.google.com`. Google's issuer is a fixed constant — there is no tenant, region, or custom-domain variant — so any other value will fail OIDC discovery. If you meant a different identity provider, use the matching preset (or `OidcPresets.generic` for one the SDK has no first-class knowledge of)."
                    cfg.Issuer
            )

        // Rule 10 — preset auto-added scopes dropped by override.
        // Severity is preset-aware: EntraWorkforce dropping its
        // load-bearing access_as_user scope is non-recoverable at
        // runtime (every authenticated request 401s post-auth with
        // no operator signal), so it's an Error. For other presets
        // where scope omission is sometimes intentional, a Warning
        // surfaces the choice without aborting startup.
        let added = PresetKind.autoAddedScopes kind cfg.ClientId

        let missing = added |> List.filter (fun s -> not (List.contains s cfg.Scopes))

        if not (List.isEmpty missing) then
            let message =
                sprintf
                    "Preset `%s` auto-adds scope(s) [%s] but OidcAppConfig.Scopes does not include them. A consumer override likely dropped the preset's contribution — for `entra-workforce` the `api://{clientId}/access_as_user` scope is load-bearing (Entra issues an opaque Microsoft Graph token without it and server-side audience validation rejects every authenticated request after sign-in)."
                    label
                    (String.concat "; " missing)

            if label = "entra-workforce" then
                RuleError message
            else
                RuleWarning message

        // Rule 15 — unfixably-opaque access token left as the bearer.
        if PresetKind.opaqueAccessTokenIsUnfixable kind && bearerKind = AccessTokenBearer then
            RuleWarning(
                sprintf
                    "Preset `%s` issues opaque access tokens with no configuration that makes them decodable, but this deployment sends the access token as its bearer. Sign-in will succeed and every authenticated request will then 401, because the server-side validator has no JWT to verify. The preset's own default (via `PresetKind.defaultBearerToken`) is the id_token — an ordinary RS256 JWT with `aud` = ClientId, which the server validates unchanged — so this deployment has set `BearerToken = Some AccessTokenBearer` explicitly. That is legitimate only if the opaque token is validated by some other means; otherwise drop the override and let the preset's default apply."
                    label
            )

        // Rule 11 — informational provenance.
        RuleOk(
            sprintf
                "preset `%s` applied (issuer form: %s; auto-added scopes: [%s]; expects decodable access token: %b; bearer: %s)"
                label
                (PresetKind.issuerForm kind)
                (String.concat "; " added)
                (PresetKind.expectsDecodableAccessToken kind)
                (BearerTokenKind.label bearerKind)
        )

        // Rule 12 — Generic + ValidateIdToken = None explicit opt-out.
        if label = "generic" && cfg.ValidateIdToken = None then
            RuleWarning
                "Preset `generic` was used with `ValidateIdToken = None` (explicit opt-out). The 0.4.3 default flipped to `Some true` because the `generic` preset has no per-IdP knowledge to judge whether the channel is internal or customer-facing. Confirm the post-callback channel is trusted; otherwise set `ValidateIdToken = Some true` so id_token signature / iss / aud / exp are re-checked on every callback."
]

let private joinMessages (msgs: string list) =
    msgs |> String.concat "\n  - " |> sprintf "\n  - %s"

let private aggregate (outcomes: RuleOutcome list) : ValidationResult =
    let errors =
        outcomes
        |> List.choose (function
            | RuleError m -> Some m
            | _ -> None)

    let warnings =
        outcomes
        |> List.choose (function
            | RuleWarning m -> Some m
            | _ -> None)

    if not (List.isEmpty errors) then
        Error(sprintf "OidcCoherenceValidator refused startup:%s" (joinMessages errors))
    elif not (List.isEmpty warnings) then
        Warning(sprintf "OidcCoherenceValidator warnings:%s" (joinMessages warnings))
    else
        Ok

/// Public projection of the per-rule outcomes against a given
/// configuration.  Used by tests + the future `/dev/inspect`
/// validators panel so per-rule structure is visible without parsing
/// the aggregated `ValidationResult` message.
let evaluate (cfg: OidcAppConfig) : RuleOutcome list = collectRules cfg

/// `IConfigValidator` implementation.  Register with the server-side
/// `ConfigValidatorAggregator` so the validator runs at compose time
/// and aborts startup on `Error`.
type OidcCoherenceValidator(cfg: OidcAppConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "oidc-coherence"
        member _.Timeout = timeout

        member _.Validate() = async {
            let outcomes = collectRules cfg
            return aggregate outcomes
        }