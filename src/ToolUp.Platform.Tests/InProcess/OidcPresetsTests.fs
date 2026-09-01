// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.OidcPresetsTests

open Expecto
open ToolUp.AuthProviders.Oidc.OidcAppConfig
open ToolUp.AuthProviders.Oidc.OidcPresets

// ─── OidcPresets — provider smart constructors (0.4.0 OidcAppConfig) ──
//
// Tests pin every preset's invariants: the issuer URL form, the
// auto-added scopes, the `Preset` provenance field (downstream
// consumers tag metrics by `PresetKind.label` — a silent rename would
// invalidate dashboards), the ValidateIdToken default, and the
// ClientId / RedirectUri pass-through.
//
// 0.4.0 BREAKING — presets now return `OidcAppConfig` directly
// instead of `(OidcUIConfig * PresetMetadata)`. The former metadata
// fields are derived from `PresetKind` via helpers in
// `OidcAppConfig.PresetKind`.
//
// Headline coverage: `entraWorkforce` MUST include the
// `api://{clientId}/access_as_user` scope in the returned config's
// Scopes list AND have it derivable from
// `PresetKind.autoAddedScopes EntraWorkforce clientId`. Regression
// here means every workforce-Entra deployment built on the preset
// would mint opaque Microsoft Graph tokens and 401 every request.

let private testTenantGuid = "11111111-2222-3333-4444-555555555555"
let private testClientId = "abcdef01-2345-6789-abcd-ef0123456789"
let private testRedirectUri = "https://app.example.test/auth/callback"

let tests: Test =
    testList "OidcClient.OidcPresets" [

        // ─── generic preset ────────────────────────────────────────

        testList "generic" [
            testCase "Preset = Some Generic"
            <| fun () ->
                let cfg = generic "https://issuer.example.test" testClientId testRedirectUri
                Expect.equal cfg.Preset (Some Generic) ""

            testCase "issuer is passed through verbatim"
            <| fun () ->
                let cfg = generic "https://issuer.example.test" testClientId testRedirectUri
                Expect.equal cfg.Issuer "https://issuer.example.test" ""

            testCase "default scopes = openid profile email"
            <| fun () ->
                let cfg = generic "https://issuer.example.test" testClientId testRedirectUri
                Expect.equal cfg.Scopes [ "openid"; "profile"; "email" ] ""

            testCase "PresetKind.autoAddedScopes is empty (no quirks)"
            <| fun () -> Expect.equal (PresetKind.autoAddedScopes Generic testClientId) [] ""

            testCase "PresetKind.expectsDecodableAccessToken = false"
            <| fun () -> Expect.isFalse (PresetKind.expectsDecodableAccessToken Generic) ""

            testCase "ValidateIdToken defaults to Some true (0.4.3 defence-in-depth)"
            <| fun () ->
                // Without first-class provider knowledge the SDK has
                // no way to know whether the chosen IdP is internal
                // or customer-facing, so the safer default is to
                // re-check id_token signature / iss / aud / exp on
                // every callback. Consumers who explicitly trust the
                // post-callback channel can set it back to None;
                // Rule 12 of OidcCoherenceValidator surfaces that
                // opt-out at startup so the decision is visible.
                let cfg = generic "https://issuer.example.test" testClientId testRedirectUri
                Expect.equal cfg.ValidateIdToken (Some true) ""

            testCase "Audience defaults to ClientId"
            <| fun () ->
                let cfg = generic "https://issuer.example.test" testClientId testRedirectUri
                Expect.equal cfg.Audience testClientId ""
        ]

        // ─── entraWorkforce preset (HEADLINE) ──────────────────────

        testList "entraWorkforce" [
            testCase "Preset = Some EntraWorkforce"
            <| fun () ->
                let cfg = entraWorkforce testTenantGuid testClientId testRedirectUri
                Expect.equal cfg.Preset (Some EntraWorkforce) ""

            testCase "PresetKind.label EntraWorkforce = \"entra-workforce\""
            <| fun () -> Expect.equal (PresetKind.label EntraWorkforce) "entra-workforce" ""

            testCase "issuer is login.microsoftonline.com / tenant / v2.0"
            <| fun () ->
                let cfg = entraWorkforce testTenantGuid testClientId testRedirectUri

                Expect.equal cfg.Issuer (sprintf "https://login.microsoftonline.com/%s/v2.0" testTenantGuid) ""

            testCase "auto-adds api://{clientId}/access_as_user scope (LOAD-BEARING)"
            <| fun () ->
                // The single most-commonly-misconfigured workforce-Entra
                // knob. Without this scope, Entra mints an opaque
                // Microsoft Graph token and the server's audience
                // validation rejects every request. A regression here
                // means every workforce-Entra deployment built on the
                // preset breaks after sign-in.
                let cfg = entraWorkforce testTenantGuid testClientId testRedirectUri
                let expectedAccessScope = sprintf "api://%s/access_as_user" testClientId

                Expect.contains
                    cfg.Scopes
                    expectedAccessScope
                    "OidcAppConfig.Scopes must include the access_as_user scope"

                Expect.contains
                    (PresetKind.autoAddedScopes EntraWorkforce testClientId)
                    expectedAccessScope
                    "PresetKind.autoAddedScopes EntraWorkforce must derive the access_as_user scope from the clientId"

            testCase "scopes include openid + profile + email + offline_access alongside the api scope"
            <| fun () ->
                let cfg = entraWorkforce testTenantGuid testClientId testRedirectUri
                Expect.contains cfg.Scopes "openid" ""
                Expect.contains cfg.Scopes "profile" ""
                Expect.contains cfg.Scopes "email" ""
                Expect.contains cfg.Scopes "offline_access" ""

            testCase "PresetKind.expectsDecodableAccessToken = true (api audience produces v2 JWT)"
            <| fun () -> Expect.isTrue (PresetKind.expectsDecodableAccessToken EntraWorkforce) ""

            testCase "PresetKind.notes mention `requestedAccessTokenVersion`"
            <| fun () ->
                // The operator-facing hint that lets a misconfigured
                // app registration get fixed before the user hits a
                // 401. The validator renders this note.
                let notes = PresetKind.notes EntraWorkforce

                let mentionsTokenVersion =
                    notes |> List.exists (fun s -> s.Contains "requestedAccessTokenVersion")

                Expect.isTrue
                    mentionsTokenVersion
                    "Notes must reference the load-bearing app-registration manifest setting"

            testCase "tenant `common` works (multi-tenant case)"
            <| fun () ->
                let cfg = entraWorkforce "common" testClientId testRedirectUri

                Expect.equal
                    cfg.Issuer
                    "https://login.microsoftonline.com/common/v2.0"
                    "multi-tenant apps use the `common` tenant — must produce the documented issuer URL"
        ]

        // ─── entraExternalId preset ────────────────────────────────

        testList "entraExternalId" [
            testCase "Preset = Some EntraExternalId"
            <| fun () ->
                let cfg = entraExternalId "mytenant" testClientId testRedirectUri
                Expect.equal cfg.Preset (Some EntraExternalId) ""

            testCase "issuer is {tenant}.ciamlogin.com / {tenant} / v2.0"
            <| fun () ->
                let cfg = entraExternalId "mytenant" testClientId testRedirectUri
                Expect.equal cfg.Issuer "https://mytenant.ciamlogin.com/mytenant/v2.0" ""

            testCase "tenant subdomain appears in both host AND path segments"
            <| fun () ->
                let cfg = entraExternalId "uniquetenant" testClientId testRedirectUri
                Expect.stringContains cfg.Issuer "uniquetenant.ciamlogin.com" "host segment"
                Expect.stringContains cfg.Issuer "/uniquetenant/v2.0" "path segment"

            testCase "scopes include offline_access by default"
            <| fun () ->
                let cfg = entraExternalId "mytenant" testClientId testRedirectUri
                Expect.contains cfg.Scopes "offline_access" ""

                Expect.contains (PresetKind.autoAddedScopes EntraExternalId testClientId) "offline_access" ""

            testCase "ValidateIdToken defaults to Some true (CIAM defence-in-depth)"
            <| fun () ->
                let cfg = entraExternalId "mytenant" testClientId testRedirectUri
                Expect.equal cfg.ValidateIdToken (Some true) ""

            testCase "PresetKind.expectsDecodableAccessToken = true"
            <| fun () -> Expect.isTrue (PresetKind.expectsDecodableAccessToken EntraExternalId) ""

            // ─── Ported from the removed companion (Phase 749) ──────
            //
            // The `EntraExternalId` companion's own issuer-URL vectors,
            // kept when its config module was deleted. The v1.0 endpoint
            // exists but rejects the bound audience format current app
            // registrations use, which surfaces as an `InvalidAudience`
            // error that is hard to diagnose from logs alone — so the
            // v2.0 path is a correctness property of the preset, not an
            // incidental detail of the string it happens to build.

            testCase "the issuer is always the v2.0 path — the v1.0 endpoint is never emitted"
            <| fun () ->
                for tenant in [ "contoso"; "x"; "mytenant" ] do
                    let cfg = entraExternalId tenant testClientId testRedirectUri
                    Expect.stringEnds cfg.Issuer "/v2.0" $"v2.0 path enforced for tenant '{tenant}'"
                    Expect.isFalse (cfg.Issuer.Contains "/v1.0") $"no v1.0 path for tenant '{tenant}'"

            testCase "the companion's own default-host vector"
            <| fun () ->
                let cfg = entraExternalId "contoso" testClientId testRedirectUri
                Expect.equal cfg.Issuer "https://contoso.ciamlogin.com/contoso/v2.0" "default host pattern"
        ]

        // ─── entraExternalIdWithDomain (custom-domain override) ────

        testList "entraExternalIdWithDomain" [
            testCase "issuer host replaced with the custom domain"
            <| fun () ->
                let cfg =
                    entraExternalIdWithDomain "mytenant" "login.mybrand.com" testClientId testRedirectUri

                Expect.equal cfg.Issuer "https://login.mybrand.com/mytenant/v2.0" ""

            testCase "tenant subdomain still appears in the path segment"
            <| fun () ->
                let cfg =
                    entraExternalIdWithDomain "mytenant" "login.mybrand.com" testClientId testRedirectUri

                Expect.stringContains cfg.Issuer "/mytenant/v2.0" ""

            testCase "Preset carries the custom domain (EntraExternalIdWithDomain ctor)"
            <| fun () ->
                let cfg =
                    entraExternalIdWithDomain "mytenant" "login.mybrand.com" testClientId testRedirectUri

                match cfg.Preset with
                | Some(EntraExternalIdWithDomain d) -> Expect.equal d "login.mybrand.com" ""
                | other -> failtestf "expected Some (EntraExternalIdWithDomain _), got %A" other

            testCase "label still \"entra-external-id\" (same provider, different host)"
            <| fun () ->
                let cfg =
                    entraExternalIdWithDomain "mytenant" "login.mybrand.com" testClientId testRedirectUri

                let label = cfg.Preset |> Option.map PresetKind.label |> Option.defaultValue ""

                Expect.equal label "entra-external-id" ""

            testCase "PresetKind.issuerForm renders the custom domain"
            <| fun () ->
                let kind = EntraExternalIdWithDomain "login.mybrand.com"

                Expect.stringContains
                    (PresetKind.issuerForm kind)
                    "login.mybrand.com"
                    "validator should see the custom-domain shape, not the default ciamlogin.com form"

            testCase "scopes + ValidateIdToken inherit from entraExternalId base"
            <| fun () ->
                let cfg =
                    entraExternalIdWithDomain "mytenant" "login.mybrand.com" testClientId testRedirectUri

                Expect.contains cfg.Scopes "offline_access" ""
                Expect.equal cfg.ValidateIdToken (Some true) ""

            testCase "the companion's own custom-domain vector (host replaced, tenant kept in path)"
            <| fun () ->
                // Ported from the removed companion (Phase 749): a
                // custom CIAM domain replaces the host, and the tenant
                // id stays in the path segment because External ID's
                // v2.0 metadata structure requires it there.
                let cfg =
                    entraExternalIdWithDomain "contoso" "login.contoso.com" testClientId testRedirectUri

                Expect.equal
                    cfg.Issuer
                    "https://login.contoso.com/contoso/v2.0"
                    "custom domain replaces host; path keeps tenant"
        ]

        // ─── auth0 preset ──────────────────────────────────────────

        testList "auth0" [
            testCase "Preset = Some Auth0"
            <| fun () ->
                let cfg = auth0 "mytenant.auth0.com" testClientId testRedirectUri
                Expect.equal cfg.Preset (Some Auth0) ""

            testCase "issuer has trailing slash (matches Auth0's `iss` claim shape)"
            <| fun () ->
                let cfg = auth0 "mytenant.auth0.com" testClientId testRedirectUri
                Expect.equal cfg.Issuer "https://mytenant.auth0.com/" ""
                Expect.stringEnds cfg.Issuer "/" ""

            testCase "regional variant (eu.auth0.com) supported via the same domain input"
            <| fun () ->
                let cfg = auth0 "mytenant.eu.auth0.com" testClientId testRedirectUri
                Expect.equal cfg.Issuer "https://mytenant.eu.auth0.com/" ""

            testCase "scopes include offline_access for refresh tokens"
            <| fun () ->
                let cfg = auth0 "mytenant.auth0.com" testClientId testRedirectUri
                Expect.contains cfg.Scopes "offline_access" ""

            testCase "PresetKind.expectsDecodableAccessToken = false (Auth0 tokens opaque by default)"
            <| fun () -> Expect.isFalse (PresetKind.expectsDecodableAccessToken Auth0) ""

            testCase "PresetKind.notes reference the audience extra parameter"
            <| fun () ->
                let notes = PresetKind.notes Auth0

                let mentionsAudience = notes |> List.exists (fun s -> s.Contains "audience")

                Expect.isTrue mentionsAudience "Notes must reference the `audience` extra parameter for JWT tokens"
        ]

        // ─── google preset ─────────────────────────────────────────

        testList "google" [
            testCase "Preset = Some Google"
            <| fun () ->
                let cfg = google testClientId testRedirectUri
                Expect.equal cfg.Preset (Some Google) ""

            testCase "PresetKind.label Google = \"google\""
            <| fun () -> Expect.equal (PresetKind.label Google) "google" ""

            testCase "issuer is the fixed https://accounts.google.com (no tenant parameter)"
            <| fun () ->
                // Google has no tenant / region / custom-domain
                // variant — the whole class of wrong-issuer
                // misconfiguration the Entra presets guard against
                // cannot arise, provided this constant stays exact.
                let cfg = google testClientId testRedirectUri
                Expect.equal cfg.Issuer "https://accounts.google.com" ""

            testCase "scopes are the OIDC-spec minimum — NO offline_access"
            <| fun () ->
                // Load-bearing negative assertion. Google ignores
                // `offline_access`; a refresh token comes from the
                // `access_type=offline` authorize parameter. Adding
                // the scope here would encode a falsehood AND give
                // validator Rule 10 a phantom regression to report
                // whenever a consumer trimmed it back.
                let cfg = google testClientId testRedirectUri
                Expect.equal cfg.Scopes [ "openid"; "profile"; "email" ] ""

                Expect.isFalse
                    (List.contains "offline_access" cfg.Scopes)
                    "Google ignores offline_access — the preset must not request it"

            testCase "PresetKind.autoAddedScopes Google is empty"
            <| fun () -> Expect.equal (PresetKind.autoAddedScopes Google testClientId) [] ""

            testCase "PresetKind.expectsDecodableAccessToken = false (Google tokens ALWAYS opaque)"
            <| fun () ->
                // Not a default that a dashboard setting can flip
                // (which is what distinguishes Google from Auth0) —
                // a fixed property of the provider.
                Expect.isFalse (PresetKind.expectsDecodableAccessToken Google) ""

            testCase "ValidateIdToken defaults to Some true (customer-facing boundary)"
            <| fun () ->
                let cfg = google testClientId testRedirectUri
                Expect.equal cfg.ValidateIdToken (Some true) ""

            testCase "PresetKind.notes reference access_type=offline, not an offline_access scope"
            <| fun () ->
                let notes = PresetKind.notes Google

                Expect.isTrue
                    (notes |> List.exists (fun s -> s.Contains "access_type=offline"))
                    "Notes must name the authorize parameter that actually yields a refresh token"

                Expect.isTrue
                    (notes |> List.exists (fun s -> s.Contains "prompt=consent"))
                    "Notes must name the consent re-prompt, without which a repeat authorisation yields no refresh token"

            testCase "PresetKind.notes state the access token is opaque"
            <| fun () ->
                let notes = PresetKind.notes Google

                Expect.isTrue
                    (notes |> List.exists (fun s -> s.Contains "opaque"))
                    "Notes must warn that server-side bearer validation cannot decode a Google access token"

            testCase "PresetKind.issuerForm Google renders the fixed accounts.google.com form"
            <| fun () -> Expect.stringContains (PresetKind.issuerForm Google) "https://accounts.google.com" ""
        ]

        // ─── cross-preset invariants ───────────────────────────────

        testList "cross-preset invariants" [
            testCase "every preset's PresetKind.label is unique (downstream metric-tag stability)"
            <| fun () ->
                let labels = [
                    PresetKind.label Generic
                    PresetKind.label EntraWorkforce
                    PresetKind.label EntraExternalId
                    PresetKind.label Auth0
                    PresetKind.label Google
                ]
                // Note: EntraExternalIdWithDomain shares label
                // "entra-external-id" with EntraExternalId by
                // design (same provider, customised host).
                Expect.equal (List.length (List.distinct labels)) (List.length labels) ""

            testCase "EntraExternalIdWithDomain shares label with EntraExternalId (same provider)"
            <| fun () ->
                Expect.equal
                    (PresetKind.label (EntraExternalIdWithDomain "any.example.com"))
                    (PresetKind.label EntraExternalId)
                    ""

            testCase "every preset preserves ClientId + RedirectUri verbatim"
            <| fun () ->
                let presets: (unit -> OidcAppConfig) list = [
                    (fun () -> generic "https://i" testClientId testRedirectUri)
                    (fun () -> entraWorkforce testTenantGuid testClientId testRedirectUri)
                    (fun () -> entraExternalId "t" testClientId testRedirectUri)
                    (fun () -> entraExternalIdWithDomain "t" "login.b.com" testClientId testRedirectUri)
                    (fun () -> auth0 "t.auth0.com" testClientId testRedirectUri)
                    (fun () -> google testClientId testRedirectUri)
                ]

                for buildCfg in presets do
                    let cfg = buildCfg ()
                    Expect.equal cfg.ClientId testClientId "ClientId passed through"
                    Expect.equal cfg.RedirectUri testRedirectUri "RedirectUri passed through"

            testCase "every preset defaults Audience = ClientId"
            <| fun () ->
                let cfgs = [
                    generic "https://i" testClientId testRedirectUri
                    entraWorkforce testTenantGuid testClientId testRedirectUri
                    entraExternalId "t" testClientId testRedirectUri
                    entraExternalIdWithDomain "t" "login.b.com" testClientId testRedirectUri
                    auth0 "t.auth0.com" testClientId testRedirectUri
                    google testClientId testRedirectUri
                ]

                for cfg in cfgs do
                    Expect.equal cfg.Audience testClientId ""

            testCase "every preset includes the OIDC-spec minimum scopes (openid + profile + email)"
            <| fun () ->
                let scopes: string list list = [
                    (generic "https://i" testClientId testRedirectUri).Scopes
                    (entraWorkforce testTenantGuid testClientId testRedirectUri).Scopes
                    (entraExternalId "t" testClientId testRedirectUri).Scopes
                    (auth0 "t.auth0.com" testClientId testRedirectUri).Scopes
                    (google testClientId testRedirectUri).Scopes
                ]

                for s in scopes do
                    Expect.contains s "openid" "openid scope required by spec"
                    Expect.contains s "profile" "profile scope required for identity claims"
                    Expect.contains s "email" "email scope required for identity claims"
        ]
    ]