// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.LdapAuthProviderTests

open System
open System.Text
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.HealthChecks
open ToolUp.AuthProviders
open ToolUp.AuthProviders.LdapConfig
open ToolUp.AuthProviders.LdapDirectory
open ToolUp.AuthProviders.LdapGroupMapper
open ToolUp.Platform.Tests.Contracts

// ─── LDAP / Active Directory auth-provider tests ─────────────────────
//
// Exercises the whole provider pipeline against an in-memory fake
// directory (no live LDAP server) via the `ILdapConnectionFactory`
// seam: `IAuthProviderContract` conformance, group→role mapping, the
// nested-group closure, RFC-4515 injection safety, the health probe's
// Degraded-on-zero-users signal, and the security-class config
// validator. The real `System.DirectoryServices.Protocols` adapter is
// exercised only against a live directory (README).

// ─── In-memory fake directory ────────────────────────────────────────

type private FakeUser = {
    Username: string
    Password: string
    Entry: LdapEntry
    /// Nested (transitive) group DNs returned by the AD in-chain
    /// matching-rule search for this user.
    NestedGroups: string list
}

/// A fake connection over a fixed user set. Recognises the three filter
/// shapes the provider / health-check issue: the nested-group
/// matching-rule search, a presence probe (`(attr=*)`), and the user
/// lookup (`(&(objectClass=…)(login=<escaped>))`).
type private FakeConnection(users: FakeUser list) =
    interface ILdapConnection with
        member _.Search(search: LdapSearch) = async {
            let filter = search.Filter

            if filter.Contains "member:" then
                // Nested-group resolution — find the user whose DN is
                // embedded (escaped) in the filter, return their
                // nested group DNs as bare entries.
                let matched =
                    users |> List.tryFind (fun u -> filter.Contains(escapeFilterValue u.Entry.Dn))

                return
                    Ok(
                        matched
                        |> Option.map (fun u ->
                            u.NestedGroups |> List.map (fun dn -> { Dn = dn; Attributes = Map.empty }))
                        |> Option.defaultValue []
                    )
            elif filter.EndsWith "=*)" then
                // Presence probe (health check).
                return Ok(users |> List.map _.Entry)
            else
                // User lookup by login attribute.
                let matched =
                    users
                    |> List.filter (fun u -> filter.Contains(sprintf "=%s)" (escapeFilterValue u.Username)))

                return Ok(matched |> List.map _.Entry)
        }

    interface IDisposable with
        member _.Dispose() = ()

/// Fake factory. `openResult` lets a test simulate a service-bind
/// failure (health-check Unhealthy path).
type private FakeFactory(users: FakeUser list, ?openResult: Result<unit, string>) =
    let openResult = defaultArg openResult (Ok())

    interface ILdapConnectionFactory with
        member _.OpenServiceBound() = async {
            match openResult with
            | Ok() -> return Ok(new FakeConnection(users) :> ILdapConnection)
            | Error e -> return Error e
        }

        member _.VerifyCredentials(dn: string, password: string) = async {
            match users |> List.tryFind (fun u -> u.Entry.Dn = dn) with
            | Some u -> return Ok(u.Password = password)
            | None -> return Ok false
        }

// ─── Fixtures ────────────────────────────────────────────────────────

let private testConfig = {
    LdapConfig.defaults "dc.example.test" with
        SearchBase = "OU=Users,DC=example,DC=test"
        // No service bind DN → the fake's service bind is anonymous;
        // keeps the fixture free of a secret store.
        ServiceBindDn = ""
}

/// Alice: a member of `CN=ToolUp-Admins` (direct) which nests under
/// `CN=ToolUp-Staff`. `objectGUID` is her stable id.
let private aliceEntry = {
    Dn = "CN=Alice,OU=Users,DC=example,DC=test"
    Attributes =
        Map.ofList [
            "objectGUID", [ "alice-guid-0001" ]
            "displayName", [ "Alice Example" ]
            "mail", [ "alice@example.test" ]
            "memberOf", [ "CN=ToolUp-Admins,OU=Groups,DC=example,DC=test" ]
        ]
}

let private alice = {
    Username = "alice"
    Password = "s3cret"
    Entry = aliceEntry
    NestedGroups = [ "CN=ToolUp-Staff,OU=Groups,DC=example,DC=test" ]
}

let private groupMap = {
    Mappings = [
        {
            Group = "ToolUp-Admins"
            Roles = [ "admin" ]
        }
        {
            Group = "ToolUp-Staff"
            Roles = [ "staff" ]
        }
    ]
    DefaultRoles = [ "member" ]
    MatchByCommonName = true
}

let private mkContext () = DefaultHttpContext() :> HttpContext

let private basicCtx (username: string) (password: string) =
    let ctx = mkContext ()

    let encoded =
        Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password))

    ctx.Request.Headers["Authorization"] <- StringValues("Basic " + encoded)
    RequestContextBuilder.ofHttpContext ctx

let private emptyCtx () =
    RequestContextBuilder.ofHttpContext (mkContext ())

let private provider () =
    LdapAuthProvider.fromParts (FakeFactory [ alice ]) testConfig groupMap None

// ─── Group-mapper unit tests ─────────────────────────────────────────

let private groupMapperTests =
    testList "LdapGroupMapper" [
        test "commonNameOf extracts the left-most RDN value" {
            Expect.equal (GroupRoleMap.commonNameOf "CN=ToolUp-Admins,OU=Groups,DC=x") "ToolUp-Admins" "CN parsed"

            Expect.equal (GroupRoleMap.commonNameOf "ToolUp-Admins") "ToolUp-Admins" "bare CN passes through"
        }

        test "resolveRoles maps matched groups + default roles, deduped" {
            let roles =
                GroupRoleMap.resolveRoles groupMap [
                    "CN=ToolUp-Admins,OU=Groups,DC=example,DC=test"
                    "CN=ToolUp-Staff,OU=Groups,DC=example,DC=test"
                ]

            Expect.equal roles [ "member"; "admin"; "staff" ] "default first, then mapping order, no dups"
        }

        test "resolveRoles grants only default roles for an unmapped group" {
            let roles = GroupRoleMap.resolveRoles groupMap [ "CN=Randoms,OU=Groups,DC=x" ]
            Expect.equal roles [ "member" ] "only the default role"
        }

        test "expandNested computes the transitive closure, cycle-safe" {
            let parents = Map.ofList [ "a", [ "b" ]; "b", [ "c" ]; "c", [ "a" ] ] // cycle a→b→c→a

            let closure =
                GroupRoleMap.expandNested (fun g -> Map.tryFind g parents |> Option.defaultValue []) [ "a" ]

            Expect.equal (List.sort closure) [ "a"; "b"; "c" ] "all reachable, no infinite loop"
        }

        test "parse reads a full ldap.json policy" {
            let json =
                """{ "matchByCommonName": true, "defaultRoles": ["member"],
                     "mappings": [ { "group": "Admins", "roles": ["admin","member"] } ] }"""

            match GroupRoleMap.parse json with
            | Result.Ok m ->
                Expect.equal m.DefaultRoles [ "member" ] "default roles"
                Expect.equal m.Mappings.Length 1 "one mapping"
                Expect.equal m.Mappings.[0].Roles [ "admin"; "member" ] "roles parsed"
            | Result.Error e -> failtestf "expected Ok, got Error: %s" e
        }

        test "parse rejects malformed JSON rather than silently emptying" {
            match GroupRoleMap.parse "{ not json" with
            | Result.Error _ -> ()
            | Result.Ok _ -> failtest "malformed JSON must not parse to an empty map"
        }
    ]

// ─── Injection-safety unit test ──────────────────────────────────────

let private escapingTests =
    testList "LdapDirectory.escapeFilterValue" [
        test "escapes RFC-4515 metacharacters (LDAP injection)" {
            let escaped = escapeFilterValue "*)(uid=admin)"
            Expect.isFalse (escaped.Contains "*") "no raw asterisk"
            Expect.isFalse (escaped.Contains "(") "no raw open paren"
            Expect.isFalse (escaped.Contains ")") "no raw close paren"
            Expect.stringContains escaped "\\2a" "asterisk escaped"
            Expect.stringContains escaped "\\28" "open paren escaped"
        }
    ]

// ─── Provider behaviour ──────────────────────────────────────────────

let private providerTests =
    testList "LdapAuthProvider" [
        testCaseAsync "ValidateRequest resolves user id, email, and mapped roles (direct + nested)"
        <| async {
            match! (provider ()).ValidateRequest(basicCtx "alice" "s3cret") with
            | Error e -> failtestf "expected Ok, got Error: %s" e
            | Ok u ->
                Expect.equal u.UserId "alice-guid-0001" "stable id from objectGUID"
                Expect.equal u.DisplayName "Alice Example" "display name"
                Expect.equal u.Email (Some "alice@example.test") "email"
                Expect.equal (List.sort u.Roles) [ "admin"; "member"; "staff" ] "direct + nested + default roles"
        }

        testCaseAsync "ValidateRequest rejects a wrong password"
        <| async {
            match! (provider ()).ValidateRequest(basicCtx "alice" "wrong") with
            | Ok _ -> failtest "a wrong password must not validate"
            | Error _ -> ()
        }

        testCaseAsync "ValidateRequest rejects an empty password (unauthenticated-bind bypass)"
        <| async {
            match! (provider ()).ValidateRequest(basicCtx "alice" "") with
            | Ok _ -> failtest "an empty password must be rejected before any bind"
            | Error _ -> ()
        }

        testCaseAsync "ValidateRequest rejects an unknown user"
        <| async {
            match! (provider ()).ValidateRequest(basicCtx "mallory" "whatever") with
            | Ok _ -> failtest "an unknown user must not validate"
            | Error _ -> ()
        }

        testCaseAsync "IsCryptographicallyVerified is true (bind is authoritative proof)"
        <| async { Expect.isTrue (provider ()).IsCryptographicallyVerified "LDAP bind proves the password" }

        testCaseAsync "chain falls back to LDAP when the primary returns anonymous / Error"
        <| async {
            // A primary that never authenticates anyone.
            let deadPrimary =
                { new IAuthProvider with
                    member _.GetUser _ = async { return AuthenticatedUser.anonymous }
                    member _.ValidateRequest _ = async { return Error "primary: no match" }
                    member _.IsCryptographicallyVerified = true
                }

            let composite = LdapAuthProvider.withFallback deadPrimary (provider ())

            match! composite.ValidateRequest(basicCtx "alice" "s3cret") with
            | Ok u -> Expect.equal u.UserId "alice-guid-0001" "fell back to LDAP"
            | Error e -> failtestf "expected fallback to LDAP, got Error: %s" e
        }
    ]

// ─── IAuthProviderContract conformance ───────────────────────────────

let private contractTests =
    IAuthProviderContract.tests {
        Name = "LdapActiveDirectory"
        Provider = provider ()
        ValidCtx = fun () -> basicCtx "alice" "s3cret"
        ExpectedUserId = "alice-guid-0001"
        ExpiredCtx = fun () -> basicCtx "alice" "wrong"
        EmptyCtx = emptyCtx
    }

// ─── Health check ────────────────────────────────────────────────────

let private healthCheckTests =
    testList "LdapHealthCheck" [
        testCaseAsync "Healthy when bind succeeds and the probe finds users"
        <| async {
            let hc = LdapHealthCheck.fromParts testConfig (FakeFactory [ alice ])
            let! result = hc.Check()
            Expect.equal result Healthy "bind ok + users present"
        }

        testCaseAsync "Degraded when bind succeeds but the probe returns 0 users"
        <| async {
            let hc = LdapHealthCheck.fromParts testConfig (FakeFactory [])
            let! result = hc.Check()

            match result with
            | Degraded _ -> ()
            | other -> failtestf "expected Degraded for the 0-users misconfiguration, got %A" other
        }

        testCaseAsync "Unhealthy when the service bind fails"
        <| async {
            let hc =
                LdapHealthCheck.fromParts testConfig (FakeFactory([], openResult = Error "connection refused"))

            let! result = hc.Check()

            match result with
            | Unhealthy _ -> ()
            | other -> failtestf "expected Unhealthy for a bind failure, got %A" other
        }
    ]

// ─── Config validator ────────────────────────────────────────────────

let private validatorTests =
    testList "LdapConfigValidator" [
        test "is a security-class validator (runs under SkipPreflight)" {
            let v = LdapConfigValidator.create testConfig true

            Expect.isTrue
                (v :? ConfigValidation.ISecurityClassValidator)
                "must implement the ISecurityClassValidator marker"
        }

        testCaseAsync "Error when the search base is empty"
        <| async {
            let v = LdapConfigValidator.create { testConfig with SearchBase = "" } true

            match! v.Validate() with
            | ConfigValidation.Error _ -> ()
            | other -> failtestf "expected Error for an empty search base, got %A" other
        }

        testCaseAsync "Error on a plaintext bind that was not opted into"
        <| async {
            let v =
                LdapConfigValidator.create
                    {
                        testConfig with
                            ChannelBinding = Plaintext
                    }
                    false

            match! v.Validate() with
            | ConfigValidation.Error _ -> ()
            | other -> failtestf "expected Error for un-acknowledged plaintext, got %A" other
        }

        testCaseAsync "Warning on a plaintext bind that was opted into"
        <| async {
            let v =
                LdapConfigValidator.create
                    {
                        testConfig with
                            ChannelBinding = Plaintext
                    }
                    true

            match! v.Validate() with
            | ConfigValidation.Warning _ -> ()
            | other -> failtestf "expected Warning for acknowledged plaintext, got %A" other
        }

        testCaseAsync "Warning when certificate validation is disabled"
        <| async {
            let v =
                LdapConfigValidator.create
                    {
                        testConfig with
                            CertificateValidation = AllowUntrusted
                    }
                    true

            match! v.Validate() with
            | ConfigValidation.Warning _ -> ()
            | other -> failtestf "expected Warning for AllowUntrusted, got %A" other
        }
    ]

let tests =
    testList "LDAP / Active Directory auth provider" [
        groupMapperTests
        escapingTests
        providerTests
        contractTests
        healthCheckTests
        validatorTests
    ]