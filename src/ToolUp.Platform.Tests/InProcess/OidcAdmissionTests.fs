// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.OidcAdmissionTests

open System
open Expecto
open ToolUp.AuthProviders
open ToolUp.AuthProviders.OidcAdmission
open ToolUp.Platform

let private bare: AuthConfig = {
    Issuer = Some "https://issuer.example"
    Audience = None
    KeySource = JwksDiscovery "https://issuer.example"
    TokenLocation = BearerHeader
    ClockSkewSeconds = None
    AcceptedAlgorithms = None
    PreferOidWhenPresent = None
    ClaimMapping = None
}

let private config: AuthConfig = {
    bare with
        ClaimMapping =
            Some {
                ClaimMapping.none with
                    RolesClaim = Some "roles"
                    ApiRoleGrants = Set.ofList [ "admin" ]
            }
}

let private settings: AdmissionSettings = {
    AllowedTenants = "TENANTS"
    RequiredRoles = "ROLES"
    GroupAliases = "ALIASES"
    PublicationReaders = [ "readers", "READER_GROUPS" ]
    RequiredSettings = [ "PUBLISHING_SCOPE" ]
}

let private complete =
    Map.ofList [
        "TENANTS", "t1, t2"
        "ROLES", "member"
        "ALIASES", "g1=member"
        "READER_GROUPS", "g2,g3"
        "PUBLISHING_SCOPE", "team-1"
    ]

let private layer (values: Map<string, string>) (declared: AdmissionSettings) (config: AuthConfig) =
    OidcAdmission.layeredFrom (fun name -> Map.tryFind name values) declared config

let private refusal (values: Map<string, string>) : string =
    try
        layer values settings config |> ignore
        failtest "layered"
    with :? InvalidOperationException as refused ->
        refused.Message

[<Tests>]
let tests =
    testList "OIDC admission layering" [
        testCase "the named settings are laid onto the claim mapping, and what it held stays"
        <| fun _ ->
            let mapping = (layer complete settings config).ClaimMapping.Value

            Expect.equal mapping.AllowedTenants [ "t1"; "t2" ] "the tenants, trimmed"
            Expect.equal mapping.RequiredRoles [ "member" ] "the member rule"
            Expect.equal mapping.PublicationReaders [ "readers" ] "the reader roles"

            Expect.equal
                mapping.GroupAliases
                (Map.ofList [ "g1", "member"; "g2", "readers"; "g3", "readers" ])
                "the aliases and each reader group"

            Expect.equal mapping.RolesClaim (Some "roles") "the claim name it held"
            Expect.equal mapping.ApiRoleGrants (Set.ofList [ "admin" ]) "the grants it held"

        testCase "nothing named and nothing set leaves the configuration as it is"
        <| fun _ -> Expect.isNone (layer Map.empty AdmissionSettings.none bare).ClaimMapping "no mapping is invented"

        testCase "a member rule the settings leave empty keeps the one the configuration holds"
        <| fun _ ->
            let held = {
                config with
                    ClaimMapping =
                        Some {
                            config.ClaimMapping.Value with
                                RequiredRoles = [ "held" ]
                        }
            }

            let mapping =
                (layer (complete |> Map.remove "ROLES") settings held).ClaimMapping.Value

            Expect.equal mapping.RequiredRoles [ "held" ] "kept"

        testCase "a publication reader with no member rule is refused (AZ-11)"
        <| fun _ ->
            let message = refusal (complete |> Map.remove "ROLES")
            Expect.stringContains message "member rule" "names the rule"

        testCase "a directory group aliased to two roles is refused, naming the group"
        <| fun _ ->
            let message = refusal (complete |> Map.add "ALIASES" "g1=member,g1=other")
            Expect.stringContains message "'g1'" "the group"
            Expect.stringContains message "more than one role" "the rule"

        testCase "a group aliased to a reader role and another role is refused across the two settings"
        <| fun _ ->
            let message = refusal (complete |> Map.add "ALIASES" "g2=member")
            Expect.stringContains message "'g2'" "the group"

        testCase "one group named twice for one role is no conflict"
        <| fun _ ->
            let mapping =
                (layer (complete |> Map.add "ALIASES" "g1=member,g1=member") settings config).ClaimMapping.Value

            Expect.equal (Map.find "g1" mapping.GroupAliases) "member" "aliased once"

        testCase "a group the configuration already aliases elsewhere is refused"
        <| fun _ ->
            let held = {
                config with
                    ClaimMapping =
                        Some {
                            config.ClaimMapping.Value with
                                GroupAliases = Map.ofList [ "g1", "other" ]
                        }
            }

            Expect.throwsT<InvalidOperationException> (fun () -> layer complete settings held |> ignore) "refused"

        testCase "a reader role's unset group setting is refused, naming the setting"
        <| fun _ -> Expect.stringContains (refusal (complete |> Map.remove "READER_GROUPS")) "READER_GROUPS" "named"

        testCase "a required setting left blank is refused, naming it"
        <| fun _ ->
            Expect.stringContains (refusal (complete |> Map.add "PUBLISHING_SCOPE" "  ")) "PUBLISHING_SCOPE" "named"

        testCase "an alias that is not group=role, and a list with an empty entry, are refused"
        <| fun _ ->
            Expect.stringContains (refusal (complete |> Map.add "ALIASES" "g1")) "'g1'" "the alias"
            Expect.stringContains (refusal (complete |> Map.add "TENANTS" "t1,,t2")) "TENANTS" "the list"
    ]