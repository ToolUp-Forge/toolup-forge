// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.OidcPresetsTests

open Expecto
open ToolUp.Platform
open ToolUp.AuthProviders.Oidc.OidcPresets

// ─── OidcPresets — provider smart constructors ───────────────────────
//
// Tests pin every preset's invariants: the issuer URL form, the
// auto-added scopes, the `Name` metadata tag (downstream consumers
// tag metrics by this — a silent rename would invalidate dashboards),
// and the `ExpectsDecodableAccessToken` flag the coherence validator
// reads.
//
// The headline coverage: `entraWorkforce` MUST auto-add the
// `api://{clientId}/access_as_user` scope. That scope is the single
// load-bearing knob the preset exists to encode — a regression
// dropping it means every workforce-Entra deployment built on the
// preset would mint opaque Microsoft Graph tokens and 401 every
// request after sign-in.

let private testTenantGuid = "11111111-2222-3333-4444-555555555555"
let private testClientId = "abcdef01-2345-6789-abcd-ef0123456789"
let private testRedirectUri = "https://app.example.test/auth/callback"

let tests: Test =
    testList "OidcClient.OidcPresets" [

        // ─── generic preset ────────────────────────────────────────

        testList "generic" [
            testCase "Name = \"generic\""
            <| fun () ->
                let _, meta = generic "https://issuer.example.test" testClientId testRedirectUri
                Expect.equal meta.Name "generic" ""

            testCase "issuer is passed through verbatim"
            <| fun () ->
                let cfg, _ = generic "https://issuer.example.test" testClientId testRedirectUri
                Expect.equal cfg.Issuer "https://issuer.example.test" ""

            testCase "default scopes = openid profile email"
            <| fun () ->
                let cfg, _ = generic "https://issuer.example.test" testClientId testRedirectUri
                Expect.equal cfg.Scopes [ "openid"; "profile"; "email" ] ""

            testCase "no scopes auto-added (no quirks applied)"
            <| fun () ->
                let _, meta = generic "https://issuer.example.test" testClientId testRedirectUri
                Expect.equal meta.AutoAddedScopes [] ""

            testCase "ExpectsDecodableAccessToken = false (no audience binding assumed)"
            <| fun () ->
                let _, meta = generic "https://issuer.example.test" testClientId testRedirectUri
                Expect.isFalse meta.ExpectsDecodableAccessToken ""

            testCase "ValidateIdToken defaults to None"
            <| fun () ->
                let cfg, _ = generic "https://issuer.example.test" testClientId testRedirectUri
                Expect.equal cfg.ValidateIdToken None ""

            testCase "PostLogoutRedirectUri defaults to None"
            <| fun () ->
                let cfg, _ = generic "https://issuer.example.test" testClientId testRedirectUri
                Expect.equal cfg.PostLogoutRedirectUri None ""
        ]

        // ─── entraWorkforce preset (HEADLINE) ──────────────────────

        testList "entraWorkforce" [
            testCase "Name = \"entra-workforce\""
            <| fun () ->
                let _, meta = entraWorkforce testTenantGuid testClientId testRedirectUri
                Expect.equal meta.Name "entra-workforce" ""

            testCase "issuer is login.microsoftonline.com / tenant / v2.0"
            <| fun () ->
                let cfg, _ = entraWorkforce testTenantGuid testClientId testRedirectUri

                Expect.equal cfg.Issuer (sprintf "https://login.microsoftonline.com/%s/v2.0" testTenantGuid) ""

            testCase "auto-adds api://{clientId}/access_as_user scope (LOAD-BEARING)"
            <| fun () ->
                // The single most-commonly-misconfigured workforce-Entra
                // knob. Without this scope, Entra mints an opaque
                // Microsoft Graph token and the server's audience
                // validation rejects every request. A regression here
                // means every workforce-Entra deployment built on the
                // preset breaks after sign-in.
                let cfg, meta = entraWorkforce testTenantGuid testClientId testRedirectUri
                let expectedAccessScope = sprintf "api://%s/access_as_user" testClientId

                Expect.contains
                    cfg.Scopes
                    expectedAccessScope
                    "OidcUIConfig.Scopes must include the access_as_user scope"

                Expect.contains
                    meta.AutoAddedScopes
                    expectedAccessScope
                    "PresetMetadata.AutoAddedScopes must record the access_as_user scope"

            testCase "scopes include openid + profile + email + offline_access alongside the api scope"
            <| fun () ->
                let cfg, _ = entraWorkforce testTenantGuid testClientId testRedirectUri
                Expect.contains cfg.Scopes "openid" ""
                Expect.contains cfg.Scopes "profile" ""
                Expect.contains cfg.Scopes "email" ""
                Expect.contains cfg.Scopes "offline_access" ""

            testCase "ExpectsDecodableAccessToken = true (api audience produces v2 JWT)"
            <| fun () ->
                let _, meta = entraWorkforce testTenantGuid testClientId testRedirectUri

                Expect.isTrue
                    meta.ExpectsDecodableAccessToken
                    "the access_as_user scope produces a decodable JWT access token, not an opaque Graph token"

            testCase "metadata.Notes mention `requestedAccessTokenVersion`"
            <| fun () ->
                // The operator-facing hint that lets a misconfigured
                // app registration get fixed before the user hits a
                // 401. The validator (Phase C) renders this note.
                let _, meta = entraWorkforce testTenantGuid testClientId testRedirectUri

                let mentionsTokenVersion =
                    meta.Notes |> List.exists (fun s -> s.Contains "requestedAccessTokenVersion")

                Expect.isTrue
                    mentionsTokenVersion
                    "Notes must reference the load-bearing app-registration manifest setting"

            testCase "tenant `common` works (multi-tenant case)"
            <| fun () ->
                let cfg, _ = entraWorkforce "common" testClientId testRedirectUri

                Expect.equal
                    cfg.Issuer
                    (sprintf "https://login.microsoftonline.com/common/v2.0")
                    "multi-tenant apps use the `common` tenant — must produce the documented issuer URL"
        ]

        // ─── entraExternalId preset ────────────────────────────────

        testList "entraExternalId" [
            testCase "Name = \"entra-external-id\""
            <| fun () ->
                let _, meta = entraExternalId "mytenant" testClientId testRedirectUri
                Expect.equal meta.Name "entra-external-id" ""

            testCase "issuer is {tenant}.ciamlogin.com / {tenant} / v2.0"
            <| fun () ->
                let cfg, _ = entraExternalId "mytenant" testClientId testRedirectUri

                Expect.equal cfg.Issuer "https://mytenant.ciamlogin.com/mytenant/v2.0" ""

            testCase "tenant subdomain appears in both host AND path segments"
            <| fun () ->
                // External ID embeds the tenant subdomain into the
                // issuer twice (left of `.ciamlogin.com` AND in the
                // path before `/v2.0`). Missing either side produces
                // an issuer the validator won't accept.
                let cfg, _ = entraExternalId "uniquetenant" testClientId testRedirectUri
                Expect.stringContains cfg.Issuer "uniquetenant.ciamlogin.com" "host segment"
                Expect.stringContains cfg.Issuer "/uniquetenant/v2.0" "path segment"

            testCase "scopes include offline_access by default"
            <| fun () ->
                // External ID requires `offline_access` for refresh-
                // token rotation. The preset adds it; metadata records
                // the addition.
                let cfg, meta = entraExternalId "mytenant" testClientId testRedirectUri
                Expect.contains cfg.Scopes "offline_access" ""
                Expect.contains meta.AutoAddedScopes "offline_access" ""

            testCase "ValidateIdToken defaults to Some true (CIAM defence-in-depth)"
            <| fun () ->
                let cfg, _ = entraExternalId "mytenant" testClientId testRedirectUri
                Expect.equal cfg.ValidateIdToken (Some true) ""

            testCase "ExpectsDecodableAccessToken = true"
            <| fun () ->
                let _, meta = entraExternalId "mytenant" testClientId testRedirectUri
                Expect.isTrue meta.ExpectsDecodableAccessToken ""
        ]

        // ─── entraExternalIdWithDomain (custom-domain override) ────

        testList "entraExternalIdWithDomain" [
            testCase "issuer host replaced with the custom domain"
            <| fun () ->
                let cfg, _ =
                    entraExternalIdWithDomain "mytenant" "login.mybrand.com" testClientId testRedirectUri

                Expect.equal cfg.Issuer "https://login.mybrand.com/mytenant/v2.0" ""

            testCase "tenant subdomain still appears in the path segment"
            <| fun () ->
                // The CIAM contract: even with a custom domain, the
                // tenant subdomain is the path-segment identifier.
                let cfg, _ =
                    entraExternalIdWithDomain "mytenant" "login.mybrand.com" testClientId testRedirectUri

                Expect.stringContains cfg.Issuer "/mytenant/v2.0" ""

            testCase "Name still entra-external-id (same provider, different host)"
            <| fun () ->
                let _, meta =
                    entraExternalIdWithDomain "mytenant" "login.mybrand.com" testClientId testRedirectUri

                Expect.equal meta.Name "entra-external-id" ""

            testCase "IssuerForm metadata reflects the custom domain"
            <| fun () ->
                let _, meta =
                    entraExternalIdWithDomain "mytenant" "login.mybrand.com" testClientId testRedirectUri

                Expect.stringContains
                    meta.IssuerForm
                    "login.mybrand.com"
                    "validator should see the custom-domain shape, not the default ciamlogin.com form"

            testCase "scopes + ValidateIdToken inherit from entraExternalId base"
            <| fun () ->
                let cfg, _ =
                    entraExternalIdWithDomain "mytenant" "login.mybrand.com" testClientId testRedirectUri

                Expect.contains cfg.Scopes "offline_access" ""
                Expect.equal cfg.ValidateIdToken (Some true) ""
        ]

        // ─── auth0 preset ──────────────────────────────────────────

        testList "auth0" [
            testCase "Name = \"auth0\""
            <| fun () ->
                let _, meta = auth0 "mytenant.auth0.com" testClientId testRedirectUri
                Expect.equal meta.Name "auth0" ""

            testCase "issuer has trailing slash (matches Auth0's `iss` claim shape)"
            <| fun () ->
                // Auth0 issues `iss` claims WITH the trailing slash.
                // The preset's issuer carries it explicitly so the
                // classifier's normalisation matches without surprise.
                let cfg, _ = auth0 "mytenant.auth0.com" testClientId testRedirectUri
                Expect.equal cfg.Issuer "https://mytenant.auth0.com/" ""
                Expect.stringEnds cfg.Issuer "/" ""

            testCase "regional variant (eu.auth0.com) supported via the same domain input"
            <| fun () ->
                let cfg, _ = auth0 "mytenant.eu.auth0.com" testClientId testRedirectUri
                Expect.equal cfg.Issuer "https://mytenant.eu.auth0.com/" ""

            testCase "scopes include offline_access for refresh tokens"
            <| fun () ->
                let cfg, _ = auth0 "mytenant.auth0.com" testClientId testRedirectUri
                Expect.contains cfg.Scopes "offline_access" ""

            testCase "ExpectsDecodableAccessToken = false (Auth0 tokens opaque by default)"
            <| fun () ->
                // Auth0 default behaviour: opaque access tokens until
                // the consumer configures an API audience. The preset
                // records this so the coherence validator can surface
                // the `audience` extra-parameter hint.
                let _, meta = auth0 "mytenant.auth0.com" testClientId testRedirectUri
                Expect.isFalse meta.ExpectsDecodableAccessToken ""

            testCase "metadata.Notes reference the audience extra parameter"
            <| fun () ->
                let _, meta = auth0 "mytenant.auth0.com" testClientId testRedirectUri

                let mentionsAudience = meta.Notes |> List.exists (fun s -> s.Contains "audience")

                Expect.isTrue mentionsAudience "Notes must reference the `audience` extra parameter for JWT tokens"
        ]

        // ─── cross-preset invariants ───────────────────────────────

        testList "cross-preset invariants" [
            testCase "every preset's Name is unique (downstream metric-tag stability)"
            <| fun () ->
                let names = [
                    (generic "https://i" testClientId testRedirectUri |> snd).Name
                    (entraWorkforce testTenantGuid testClientId testRedirectUri |> snd).Name
                    (entraExternalId "t" testClientId testRedirectUri |> snd).Name
                    (auth0 "t.auth0.com" testClientId testRedirectUri |> snd).Name
                ]

                Expect.equal (List.length (List.distinct names)) (List.length names) ""

            testCase "every preset preserves ClientId + RedirectUri verbatim"
            <| fun () ->
                let presets: (unit -> OidcUIConfig) list = [
                    (fun () -> generic "https://i" testClientId testRedirectUri |> fst)
                    (fun () -> entraWorkforce testTenantGuid testClientId testRedirectUri |> fst)
                    (fun () -> entraExternalId "t" testClientId testRedirectUri |> fst)
                    (fun () -> entraExternalIdWithDomain "t" "login.b.com" testClientId testRedirectUri |> fst)
                    (fun () -> auth0 "t.auth0.com" testClientId testRedirectUri |> fst)
                ]

                for buildCfg in presets do
                    let cfg = buildCfg ()
                    Expect.equal cfg.ClientId testClientId "ClientId passed through"
                    Expect.equal cfg.RedirectUri testRedirectUri "RedirectUri passed through"

            testCase "every preset includes the OIDC-spec minimum scopes (openid + profile + email)"
            <| fun () ->
                let scopes: string list list = [
                    (generic "https://i" testClientId testRedirectUri |> fst).Scopes
                    (entraWorkforce testTenantGuid testClientId testRedirectUri |> fst).Scopes
                    (entraExternalId "t" testClientId testRedirectUri |> fst).Scopes
                    (auth0 "t.auth0.com" testClientId testRedirectUri |> fst).Scopes
                ]

                for s in scopes do
                    Expect.contains s "openid" "openid scope required by spec"
                    Expect.contains s "profile" "profile scope required for identity claims"
                    Expect.contains s "email" "email scope required for identity claims"
        ]
    ]