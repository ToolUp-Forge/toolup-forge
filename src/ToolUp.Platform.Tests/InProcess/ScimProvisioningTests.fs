// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ScimProvisioningTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Secrets
open ToolUp.AuthProviders.ScimTypes
open ToolUp.AuthProviders.ScimHandler
open ToolUp.AuthProviders.ScimRoutes
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── SCIM 2.0 provisioning conformance pack ──────────────────────────
//
// Replays RECORDED Entra ID and Okta provisioning sequences — the exact
// request bodies those two IdPs send, in the order they send them —
// against the real `TeamStore` over `InMemoryBlobStorage`. No live IdP,
// no HTTP listener, no network.
//
// The store is the SHIPPED one, not a fake. That is the load-bearing
// choice: the phase's claim is that a SCIM push is a different ACTOR
// rather than a different code path, and the only way to certify that
// is to make the writes land in the same store a human admin's writes
// land in — so the last-Owner safeguard, the membership-row layout and
// the audit shape are the production ones. A fake store would certify
// the fixtures against themselves.
//
// The fixtures are recorded bodies, kept verbatim (including the
// attributes this provider ignores and Okta's stringly `"active":
// "false"`), because a trimmed fixture stops testing the tolerance that
// makes the endpoint interoperable.

// ─── Fixtures ────────────────────────────────────────────────────────

module private Fixtures =

    /// Entra ID user create. Note the `urn:ietf:params:scim:schemas:
    /// extension:enterprise:2.0:User` extension block — Entra sends it
    /// on every create, and a provider that rejects unknown attributes
    /// cannot provision from Entra at all.
    let entraCreateUser (userName: string) =
        """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User","urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"],"externalId":"8a1f0c2e-4d5b-4a91-9c33-2f7e6b1d0a44","userName":"__USER__","active":true,"displayName":"Ada Lovelace","name":{"formatted":"Ada Lovelace","familyName":"Lovelace","givenName":"Ada"},"emails":[{"value":"__USER__","type":"work","primary":true}],"urn:ietf:params:scim:schemas:extension:enterprise:2.0:User":{"department":"Engineering","employeeNumber":"E-1074"},"meta":{"resourceType":"User"}}"""
            .Replace("__USER__", userName)

    /// Entra's group-assignment PATCH: add one member to a group.
    let entraAddMember (userId: string) =
        """{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"Add","path":"members","value":[{"value":"__USER__"}]}]}"""
            .Replace("__USER__", userId)

    /// Entra's deactivation: a path-less `replace` whose value object
    /// carries `active`. This is the shape that breaks providers which
    /// only handle `"path": "active"`.
    let entraDeactivate =
        """{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"replace","value":{"active":false}}]}"""

    /// Entra's targeted group removal.
    let entraRemoveMember (userId: string) =
        """{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"remove","path":"members[value eq \"__USER__\"]"}]}"""
            .Replace("__USER__", userId)

    /// Okta user create — a leaner body, no enterprise extension, and
    /// `password` present (which this provider must ignore rather than
    /// store).
    let oktaCreateUser (userName: string) =
        """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"__USER__","name":{"givenName":"Grace","familyName":"Hopper"},"emails":[{"primary":true,"value":"__USER__","type":"work"}],"displayName":"Grace Hopper","active":true,"password":"not-a-real-secret","externalId":"00u1a2b3c4d5e6f7g8h9"}"""
            .Replace("__USER__", userName)

    /// Okta's deactivation, with the STRINGLY boolean it is known to
    /// send.
    let oktaDeactivate =
        """{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"replace","path":"active","value":"false"}]}"""

    /// Okta's group membership push.
    let oktaAddMembers (userIds: string list) =
        let members =
            userIds
            |> List.map (fun u -> sprintf """{"value":"%s","display":"%s"}""" u u)
            |> String.concat ","

        sprintf
            """{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"add","path":"members","value":[%s]}]}"""
            members

    let nestedGroupPush (groupId: string) =
        sprintf
            """{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[{"op":"add","path":"members","value":[{"value":"%s","type":"Group"}]}]}"""
            groupId

// ─── Recording audit log ─────────────────────────────────────────────

/// Audit emission is fire-and-forget (`Async.Start`), matching
/// `PlatformApiHandler`, so an assertion cannot simply read the list
/// after the call returns. `waitFor` polls to a bounded timeout rather
/// than sleeping a fixed interval: a sleep long enough to be reliable
/// makes the pack slow, and one short enough to be fast makes it flaky.
type private RecordingAuditLog() =
    let events = ConcurrentQueue<string * AuditEvent>()

    member _.Events = events |> Seq.toList

    member this.WaitFor(count: int) : (string * AuditEvent) list =
        let deadline = DateTime.UtcNow.AddSeconds 5.0

        while events.Count < count && DateTime.UtcNow < deadline do
            Thread.Sleep 10

        this.Events

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { events.Enqueue(scopeId, audit) }

        member _.GetAuditTrail(_, _, _) = async { return [] }

type private InMemorySecretStore(seed: (string * string * string) list) =
    let store = ConcurrentDictionary<string * string, string>()

    do
        for scope, key, value in seed do
            store[(scope, key)] <- value

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

// ─── Harness ─────────────────────────────────────────────────────────

let private teamId = "acme-engineering"
let private teamName = "Acme Engineering"

/// A fresh world: real `TeamStore`, real blob storage, a recording
/// audit log, one seeded team whose Owner already exists (a team with
/// no Owner is not a state the platform allows, and provisioning into
/// one would be testing a fiction).
let private freshWorld () =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
    let teams = TeamStore(storage, notifications) :> ITeamStore
    let auditLog = RecordingAuditLog()

    async {
        let! _ = teams.CreateTeam(teamId, teamName)
        let! _ = teams.AddMember(teamId, "founder@acme.test", Owner)

        return
            teams,
            auditLog,
            {
                Teams = teams
                Permissions = None
                Audit = Some(auditLog :> IAuditLog)
                Config = ScimConfig.create teamId
            }
    }
    |> Async.RunSynchronously

let private memberIds (teams: ITeamStore) =
    teams.GetTeamMembers teamId
    |> Async.RunSynchronously
    |> List.map _.UserId
    |> List.sort

let private roleOf (teams: ITeamStore) (userId: string) =
    teams.GetMemberRole(teamId, userId) |> Async.RunSynchronously

let private expectOk (label: string) (r: Result<'a, ScimError>) : 'a =
    match r with
    | Ok v -> v
    | Error e -> failtestf "%s: expected success, got %d %s" label e.Status e.Detail

let private expectError (label: string) (r: Result<'a, ScimError>) : ScimError =
    match r with
    | Ok _ -> failtestf "%s: expected a SCIM error, got success" label
    | Error e -> e

let private jsonOf (payload: string) = JsonDocument.Parse(payload).RootElement

let private str (el: JsonElement) (name: string) = el.GetProperty(name).GetString()

// ─── HTTP harness (for the bearer gate + route surface) ──────────────

let private contextFor
    (teams: ITeamStore)
    (auditLog: IAuditLog)
    (secrets: ISecretStore)
    (verb: string)
    (path: string)
    (query: string)
    (authHeader: string option)
    (body: string option)
    : HttpContext * MemoryStream =
    let services = ServiceCollection()
    services.AddSingleton<ITeamStore>(teams) |> ignore
    services.AddSingleton<IAuditLog>(auditLog) |> ignore
    services.AddSingleton<ISecretStore>(secrets) |> ignore
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- services.BuildServiceProvider() :> IServiceProvider
    ctx.Request.Method <- verb
    ctx.Request.Path <- PathString path

    if query <> "" then
        ctx.Request.QueryString <- QueryString("?" + query)

    match authHeader with
    | Some h -> ctx.Request.Headers["Authorization"] <- Microsoft.Extensions.Primitives.StringValues h
    | None -> ()

    match body with
    | Some b -> ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes b)
    | None -> ctx.Request.Body <- new MemoryStream([||])

    let responseBody = new MemoryStream()
    ctx.Response.Body <- responseBody
    ctx, responseBody

/// Invoke the route table exactly as Giraffe would, returning the
/// status code and the response body.
let private invoke (config: ScimConfig) (ctx: HttpContext) (responseBody: MemoryStream) : int * string =
    let next: HttpFunc = fun _ -> System.Threading.Tasks.Task.FromResult(Some ctx)

    let result = (routes config) next ctx |> Async.AwaitTask |> Async.RunSynchronously

    match result with
    | None -> 404, ""
    | Some _ -> ctx.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray())

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "SCIM 2.0 provisioning companion" [

        // ─── Recorded IdP sequences (the conformance pack proper) ───

        testList "recorded IdP sequences" [

            test "Entra: create user -> assign group -> change role -> deactivate" {
                let teams, auditLog, deps = freshWorld ()

                // 1. Create. Entra sends the enterprise-extension block;
                //    the provider must ignore it, not reject the body.
                let created =
                    createUser deps (Fixtures.entraCreateUser "ada@acme.test")
                    |> Async.RunSynchronously
                    |> expectOk "create"

                let createdDoc = jsonOf created
                Expect.equal (str createdDoc "id") "ada@acme.test" "created resource echoes the mapped platform id"
                Expect.isTrue (createdDoc.GetProperty("active").GetBoolean()) "created user is active"

                Expect.equal
                    (memberIds teams)
                    [ "ada@acme.test"; "founder@acme.test" ]
                    "the membership row landed in the real store"

                Expect.equal (roleOf teams "ada@acme.test") (Some Member) "unmapped group name yields least privilege"

                // 2. Group assignment. Already a member at the mapped
                //    role, so this is a no-op rather than a duplicate.
                patchGroup deps teamId (Fixtures.entraAddMember "ada@acme.test")
                |> Async.RunSynchronously
                |> expectOk "group add"
                |> ignore

                Expect.equal (memberIds teams) [ "ada@acme.test"; "founder@acme.test" ] "group add is idempotent"

                // 3. Role change, driven by the group's displayName.
                let adminDeps = {
                    deps with
                        Config = {
                            deps.Config with
                                Mapping = {
                                    ScimAttributeMapping.defaults with
                                        Roles = ScimRoleMapping.defaults |> ScimRoleMapping.withGroup teamName Admin
                                }
                        }
                }

                patchGroup adminDeps teamId (Fixtures.entraAddMember "ada@acme.test")
                |> Async.RunSynchronously
                |> expectOk "role change"
                |> ignore

                Expect.equal (roleOf teams "ada@acme.test") (Some Admin) "the group assignment carried the role change"

                // 4. Deactivate — Entra's path-less replace.
                let deactivated =
                    patchUser deps "ada@acme.test" Fixtures.entraDeactivate
                    |> Async.RunSynchronously
                    |> expectOk "deactivate"

                Expect.isNone deactivated "a deprovisioning PATCH returns no resource — there is no tombstone"
                Expect.equal (memberIds teams) [ "founder@acme.test" ] "access is gone within the one request"

                // The audit trail: added, role-changed, removed — every
                // one stamped with the SCIM actor.
                let events = auditLog.WaitFor 3 |> List.map snd

                let kinds =
                    events
                    |> List.map (fun e ->
                        match e with
                        | MemberAdded p -> "added:" + p.UserId
                        | MemberRoleChanged p -> "role:" + p.UserId + ":" + p.OldRole + "->" + p.NewRole
                        | MemberRemoved p -> "removed:" + p.UserId
                        | _ -> "other")

                Expect.equal
                    kinds
                    [
                        "added:" + ScimActorId
                        "role:" + ScimActorId + ":Member->Admin"
                        "removed:" + ScimActorId
                    ]
                    "the shipped audit events fired, stamped with the SCIM origin"
            }

            test "Okta: create user -> group push -> stringly deactivate" {
                let teams, auditLog, deps = freshWorld ()

                createUser deps (Fixtures.oktaCreateUser "grace@acme.test")
                |> Async.RunSynchronously
                |> expectOk "okta create"
                |> ignore

                // Okta pushes the whole group roster including members
                // it already knows about.
                patchGroup deps teamId (Fixtures.oktaAddMembers [ "grace@acme.test"; "alan@acme.test" ])
                |> Async.RunSynchronously
                |> expectOk "okta group push"
                |> ignore

                Expect.equal
                    (memberIds teams)
                    [ "alan@acme.test"; "founder@acme.test"; "grace@acme.test" ]
                    "the new member was added and the known one left alone"

                // `"active": "false"` — a string, not a boolean.
                patchUser deps "grace@acme.test" Fixtures.oktaDeactivate
                |> Async.RunSynchronously
                |> expectOk "okta deactivate"
                |> ignore

                Expect.equal
                    (memberIds teams)
                    [ "alan@acme.test"; "founder@acme.test" ]
                    "Okta's stringly boolean deprovisioned the member"

                let removals =
                    auditLog.WaitFor 3
                    |> List.map snd
                    |> List.choose (fun e ->
                        match e with
                        | MemberRemoved p -> Some p.AffectedUserId
                        | _ -> None)

                Expect.equal removals [ "grace@acme.test" ] "exactly one removal was audited"
            }

            test "DELETE deprovisions, and the targeted group removal does too" {
                let teams, _, deps = freshWorld ()

                createUser deps (Fixtures.oktaCreateUser "grace@acme.test")
                |> Async.RunSynchronously
                |> expectOk "create"
                |> ignore

                createUser deps (Fixtures.entraCreateUser "ada@acme.test")
                |> Async.RunSynchronously
                |> expectOk "create"
                |> ignore

                deleteUser deps "grace@acme.test" |> Async.RunSynchronously |> expectOk "delete"

                patchGroup deps teamId (Fixtures.entraRemoveMember "ada@acme.test")
                |> Async.RunSynchronously
                |> expectOk "targeted removal"
                |> ignore

                Expect.equal (memberIds teams) [ "founder@acme.test" ] "both deprovision routes removed their member"
            }

            test "PUT Groups replaces membership as a delta" {
                let teams, auditLog, deps = freshWorld ()

                createUser deps (Fixtures.oktaCreateUser "grace@acme.test")
                |> Async.RunSynchronously
                |> expectOk "create"
                |> ignore

                let body =
                    """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:Group"],"displayName":"Acme Engineering","members":[{"value":"founder@acme.test"},{"value":"grace@acme.test"},{"value":"alan@acme.test"}]}"""

                replaceGroup deps teamId body
                |> Async.RunSynchronously
                |> expectOk "replace"
                |> ignore

                Expect.equal
                    (memberIds teams)
                    [ "alan@acme.test"; "founder@acme.test"; "grace@acme.test" ]
                    "the one missing member was added"

                // Two adds total across the whole test (grace via
                // create, alan via replace) — the replace did NOT
                // re-add the two members it already had.
                let adds =
                    auditLog.WaitFor 2
                    |> List.map snd
                    |> List.choose (fun e ->
                        match e with
                        | MemberAdded p -> Some p.AffectedUserId
                        | _ -> None)
                    |> List.sort

                Expect.equal
                    adds
                    [ "alan@acme.test"; "grace@acme.test" ]
                    "a replace emits a delta, not one event per member"
            }
        ]

        // ─── Refusals and safeguards ───────────────────────────────

        testList "refusals" [

            test "a duplicate create is 409 uniqueness, not a second row" {
                let teams, _, deps = freshWorld ()

                createUser deps (Fixtures.entraCreateUser "ada@acme.test")
                |> Async.RunSynchronously
                |> expectOk "first create"
                |> ignore

                let err =
                    createUser deps (Fixtures.entraCreateUser "ada@acme.test")
                    |> Async.RunSynchronously
                    |> expectError "second create"

                Expect.equal err.Status 409 "duplicate create is 409"
                Expect.equal err.ScimType (Some "uniqueness") "and carries the spec's uniqueness scimType"
                Expect.equal (memberIds teams) [ "ada@acme.test"; "founder@acme.test" ] "no duplicate row was written"
            }

            test "removing the last Owner is refused, and the refusal is surfaced" {
                let teams, _, deps = freshWorld ()

                let err =
                    deleteUser deps "founder@acme.test"
                    |> Async.RunSynchronously
                    |> expectError "last owner"

                Expect.equal err.Status 400 "the store's refusal surfaces as a client error"

                Expect.equal
                    (memberIds teams)
                    [ "founder@acme.test" ]
                    "the shipped last-Owner safeguard held — the SCIM path is not a bypass"
            }

            test "an unsupported filter is 501 invalidFilter, naming the expression" {
                let _, _, deps = freshWorld ()

                let err =
                    listUsers deps ScimPage.defaults (ScimFilter.parse "userName sw \"a\"")
                    |> Async.RunSynchronously
                    |> expectError "sw filter"

                Expect.equal err.Status 501 "RFC 7644 §3.4.2.2 says 501"
                Expect.equal err.ScimType (Some "invalidFilter") "with the invalidFilter scimType"
                Expect.stringContains err.Detail "userName sw" "and quotes the expression back"
            }

            test "a nested-group push is refused by name" {
                let _, _, deps = freshWorld ()

                let err =
                    patchGroup deps teamId (Fixtures.nestedGroupPush "some-other-group")
                    |> Async.RunSynchronously
                    |> expectError "nested group"

                Expect.stringContains err.Detail "Nested groups" "the refusal says what it refused"
            }

            test "a create with no usable join key is refused rather than given a synthetic id" {
                let teams, _, deps = freshWorld ()

                let byExternalId = {
                    deps with
                        Config = {
                            deps.Config with
                                Mapping = {
                                    ScimAttributeMapping.defaults with
                                        Identity = FromExternalId
                                }
                        }
                }

                let body =
                    """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"noid@acme.test","active":true}"""

                let err =
                    createUser byExternalId body
                    |> Async.RunSynchronously
                    |> expectError "no externalId"

                Expect.equal err.Status 400 "a missing join key is a client error"
                Expect.stringContains err.Detail "externalId" "and names the attribute it needed"
                Expect.equal (memberIds teams) [ "founder@acme.test" ] "nothing was provisioned"
            }

            test "malformed JSON is invalidSyntax, not an unhandled exception" {
                let _, _, deps = freshWorld ()

                let err =
                    createUser deps "{not json" |> Async.RunSynchronously |> expectError "malformed"

                Expect.equal err.ScimType (Some "invalidSyntax") "malformed JSON is a typed SCIM error"
            }
        ]

        // ─── Scope isolation (GP 4) ────────────────────────────────

        testList "scope isolation" [

            test "another team's group is a plain 404, not a 403" {
                let _, _, deps = freshWorld ()

                let err =
                    getGroup deps "some-other-team"
                    |> Async.RunSynchronously
                    |> expectError "foreign group"

                Expect.equal
                    err.Status
                    404
                    "a 403 would confirm the team exists — the endpoint must not be a team-id oracle"
            }

            test "a PATCH aimed at another team writes nothing" {
                let teams, _, deps = freshWorld ()

                let err =
                    patchGroup deps "some-other-team" (Fixtures.oktaAddMembers [ "intruder@acme.test" ])
                    |> Async.RunSynchronously
                    |> expectError "foreign patch"

                Expect.equal err.Status 404 "refused"
                Expect.equal (memberIds teams) [ "founder@acme.test" ] "and the configured team is untouched"
            }
        ]

        // ─── The bearer gate ───────────────────────────────────────

        testList "bearer gate" [

            test "constant-time compare accepts only an exact match" {
                Expect.isTrue (tokensMatch "s3cret-token" "s3cret-token") "exact match"
                Expect.isFalse (tokensMatch "s3cret-token" "s3cret-tokeN") "one byte differs"
                Expect.isFalse (tokensMatch "s3cret-token" "s3cret-token-longer") "length differs"
                Expect.isFalse (tokensMatch "" "s3cret-token") "empty presented"
                Expect.isFalse (tokensMatch "s3cret-token" "") "empty configured"
            }

            test "the Authorization header is parsed strictly" {
                let ctxWith (header: string option) =
                    let ctx = DefaultHttpContext() :> HttpContext

                    match header with
                    | Some h -> ctx.Request.Headers["Authorization"] <- Microsoft.Extensions.Primitives.StringValues h
                    | None -> ()

                    ctx

                Expect.equal (bearerToken (ctxWith (Some "Bearer abc123"))) (Some "abc123") "a bearer token"
                Expect.equal (bearerToken (ctxWith (Some "bearer abc123"))) (Some "abc123") "scheme is case-insensitive"
                Expect.isNone (bearerToken (ctxWith (Some "Basic abc123"))) "a non-Bearer scheme is refused"
                Expect.isNone (bearerToken (ctxWith (Some "Bearer   "))) "an empty token is refused"
                Expect.isNone (bearerToken (ctxWith None)) "no header at all"
            }

            test "an unauthenticated request is refused with a SCIM error body" {
                let teams, auditLog, _ = freshWorld ()

                let secrets =
                    InMemorySecretStore [ ("team-" + teamId, ScimConfig.DefaultTokenKey, "the-token") ]

                let config = ScimConfig.create teamId

                let ctx, out =
                    contextFor
                        teams
                        (auditLog :> IAuditLog)
                        (secrets :> ISecretStore)
                        "GET"
                        "/scim/v2/Users"
                        ""
                        None
                        None

                let status, body = invoke config ctx out

                Expect.equal status 401 "no token means 401"
                let doc = jsonOf body
                Expect.equal (str doc "status") "401" "the SCIM error carries status as a STRING"
                Expect.stringContains (str doc "detail") "Bearer" "and says what was missing"
            }

            test "a wrong token is refused" {
                let teams, auditLog, _ = freshWorld ()

                let secrets =
                    InMemorySecretStore [ ("team-" + teamId, ScimConfig.DefaultTokenKey, "the-token") ]

                let config = ScimConfig.create teamId

                let ctx, out =
                    contextFor
                        teams
                        (auditLog :> IAuditLog)
                        (secrets :> ISecretStore)
                        "GET"
                        "/scim/v2/Users"
                        ""
                        (Some "Bearer wrong-token")
                        None

                let status, _ = invoke config ctx out
                Expect.equal status 401 "a wrong token means 401"
            }

            test "the gate is fail-CLOSED when no token is configured" {
                let teams, auditLog, _ = freshWorld ()
                // Secret store present but holding nothing.
                let secrets = InMemorySecretStore []
                let config = ScimConfig.create teamId

                let ctx, out =
                    contextFor
                        teams
                        (auditLog :> IAuditLog)
                        (secrets :> ISecretStore)
                        "GET"
                        "/scim/v2/Users"
                        ""
                        (Some "Bearer anything")
                        None

                let status, _ = invoke config ctx out

                Expect.equal status 401 "an unconfigured endpoint refuses — there is no 'no token means open' branch"
            }

            test "a correct token reaches the resource" {
                let teams, auditLog, _ = freshWorld ()

                let secrets =
                    InMemorySecretStore [ ("team-" + teamId, ScimConfig.DefaultTokenKey, "the-token") ]

                let config = ScimConfig.create teamId

                let ctx, out =
                    contextFor
                        teams
                        (auditLog :> IAuditLog)
                        (secrets :> ISecretStore)
                        "GET"
                        "/scim/v2/Users"
                        ""
                        (Some "Bearer the-token")
                        None

                let status, body = invoke config ctx out

                Expect.equal status 200 "authorised"
                let doc = jsonOf body
                Expect.equal (doc.GetProperty("totalResults").GetInt32()) 1 "the seeded Owner is listed"

                Expect.equal
                    (doc.GetProperty("schemas").EnumerateArray() |> Seq.head |> _.GetString())
                    ScimSchemas.ListResponse
                    "and the ListResponse envelope is present"
            }
        ]

        // ─── Route surface ─────────────────────────────────────────

        testList "route surface" [

            test "a POST create round-trips through the route table" {
                let teams, auditLog, _ = freshWorld ()

                let secrets =
                    InMemorySecretStore [ ("team-" + teamId, ScimConfig.DefaultTokenKey, "the-token") ]

                let config = ScimConfig.create teamId

                let ctx, out =
                    contextFor
                        teams
                        (auditLog :> IAuditLog)
                        (secrets :> ISecretStore)
                        "POST"
                        "/scim/v2/Users"
                        ""
                        (Some "Bearer the-token")
                        (Some(Fixtures.entraCreateUser "ada@acme.test"))

                let status, body = invoke config ctx out

                Expect.equal status 201 "RFC 7644 §3.3 — a create answers 201"
                Expect.equal (str (jsonOf body) "id") "ada@acme.test" "with the created resource"
                Expect.equal (memberIds teams) [ "ada@acme.test"; "founder@acme.test" ] "and the member landed"
            }

            test "a deprovisioning PATCH answers 204" {
                let teams, auditLog, _ = freshWorld ()

                let secrets =
                    InMemorySecretStore [ ("team-" + teamId, ScimConfig.DefaultTokenKey, "the-token") ]

                let config = ScimConfig.create teamId

                teams.AddMember(teamId, "ada@acme.test", Member)
                |> Async.RunSynchronously
                |> ignore

                let ctx, out =
                    contextFor
                        teams
                        (auditLog :> IAuditLog)
                        (secrets :> ISecretStore)
                        "PATCH"
                        "/scim/v2/Users/ada@acme.test"
                        ""
                        (Some "Bearer the-token")
                        (Some Fixtures.entraDeactivate)

                let status, _ = invoke config ctx out

                Expect.equal status 204 "no resource remains to return"
                Expect.equal (memberIds teams) [ "founder@acme.test" ] "the member is gone"
            }

            test "group creation and deletion are refused with 501" {
                let teams, auditLog, _ = freshWorld ()

                let secrets =
                    InMemorySecretStore [ ("team-" + teamId, ScimConfig.DefaultTokenKey, "the-token") ]

                let config = ScimConfig.create teamId

                let post, postOut =
                    contextFor
                        teams
                        (auditLog :> IAuditLog)
                        (secrets :> ISecretStore)
                        "POST"
                        "/scim/v2/Groups"
                        ""
                        (Some "Bearer the-token")
                        (Some """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:Group"],"displayName":"New"}""")

                let postStatus, _ = invoke config post postOut
                Expect.equal postStatus 501 "a SCIM push cannot mint a team"

                let del, delOut =
                    contextFor
                        teams
                        (auditLog :> IAuditLog)
                        (secrets :> ISecretStore)
                        "DELETE"
                        ("/scim/v2/Groups/" + teamId)
                        ""
                        (Some "Bearer the-token")
                        None

                let delStatus, _ = invoke config del delOut
                Expect.equal delStatus 501 "nor delete one"
            }

            test "the discovery documents are well-formed and declare the real capabilities" {
                let teams, auditLog, _ = freshWorld ()

                let secrets =
                    InMemorySecretStore [ ("team-" + teamId, ScimConfig.DefaultTokenKey, "the-token") ]

                let config = ScimConfig.create teamId

                let fetch (path: string) =
                    let ctx, out =
                        contextFor
                            teams
                            (auditLog :> IAuditLog)
                            (secrets :> ISecretStore)
                            "GET"
                            path
                            ""
                            (Some "Bearer the-token")
                            None

                    invoke config ctx out

                let spcStatus, spc = fetch "/scim/v2/ServiceProviderConfig"
                Expect.equal spcStatus 200 "ServiceProviderConfig is served"
                let spcDoc = jsonOf spc

                Expect.isTrue
                    (spcDoc.GetProperty("patch").GetProperty("supported").GetBoolean())
                    "PATCH is declared supported — it is how both IdPs deprovision"

                Expect.isFalse
                    (spcDoc.GetProperty("bulk").GetProperty("supported").GetBoolean())
                    "bulk is declared UNsupported rather than omitted"

                Expect.equal
                    (spcDoc.GetProperty("filter").GetProperty("maxResults").GetInt32())
                    ScimPage.MaxCount
                    "maxResults matches the page cap an IdP will plan against"

                let rtStatus, rt = fetch "/scim/v2/ResourceTypes"
                Expect.equal rtStatus 200 "ResourceTypes is served"
                Expect.equal ((jsonOf rt).GetProperty("Resources").GetArrayLength()) 2 "User and Group"

                let schStatus, sch = fetch "/scim/v2/Schemas"
                Expect.equal schStatus 200 "Schemas is served"
                Expect.equal ((jsonOf sch).GetProperty("Resources").GetArrayLength()) 2 "User and Group schemas"
            }
        ]

        // ─── Wire model ────────────────────────────────────────────

        testList "wire model" [

            test "filter parsing recognises exactly the supported shape" {
                Expect.equal
                    (ScimFilter.parse "userName eq \"ada@acme.test\"")
                    (UserNameEquals "ada@acme.test")
                    "userName eq"

                Expect.equal
                    (ScimFilter.parse "UserName EQ \"ada@acme.test\"")
                    (UserNameEquals "ada@acme.test")
                    "attribute and operator are case-insensitive"

                Expect.equal (ScimFilter.parse "displayName eq \"Acme\"") (DisplayNameEquals "Acme") "displayName eq"
                Expect.equal (ScimFilter.parse "externalId eq \"x\"") (ExternalIdEquals "x") "externalId eq"
                Expect.equal (ScimFilter.parse "") NoFilter "empty is no filter"

                match ScimFilter.parse "userName co \"ada\"" with
                | UnsupportedFilter e -> Expect.stringContains e "co" "an unsupported operator carries its expression"
                | other -> failtestf "expected UnsupportedFilter, got %A" other
            }

            test "startIndex is 1-based" {
                let items = [ "a"; "b"; "c"; "d" ]
                Expect.equal (ScimPage.apply (ScimPage.create 1 2) items) [ "a"; "b" ] "page 1 starts at the first item"
                Expect.equal (ScimPage.apply (ScimPage.create 3 2) items) [ "c"; "d" ] "startIndex 3 is the third item"
                Expect.equal (ScimPage.apply (ScimPage.create 0 2) items) [ "a"; "b" ] "a startIndex below 1 clamps"
                Expect.equal (ScimPage.apply (ScimPage.create 9 2) items) [] "past the end is empty, not an error"
                Expect.equal (ScimPage.apply (ScimPage.create 1 0) items) [] "count 0 means no resources"
            }

            test "PATCH paths are interpreted, including the value filter" {
                Expect.equal (ScimPatchPath.parse "active") ActivePath "active"
                Expect.equal (ScimPatchPath.parse "members") MembersPath "members"

                Expect.equal
                    (ScimPatchPath.parse "members[value eq \"ada@acme.test\"]")
                    (MemberValuePath "ada@acme.test")
                    "the targeted-removal path recovers its id"

                match ScimPatchPath.parse "urn:some:extension:attr" with
                | OtherPath p -> Expect.stringContains p "extension" "an unknown path is carried, not thrown"
                | other -> failtestf "expected OtherPath, got %A" other
            }

            test "an unknown attribute is ignored rather than rejected (RFC 7644 §3.5.2)" {
                let body =
                    """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"ada@acme.test","someFutureAttribute":{"nested":true}}"""

                match ScimJson.decodeUser body with
                | Ok u -> Expect.equal u.UserName "ada@acme.test" "the known attributes decoded"
                | Error e -> failtestf "expected the unknown attribute to be ignored, got %d %s" e.Status e.Detail
            }

            test "active defaults to true when the attribute is absent" {
                let body =
                    """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"ada@acme.test"}"""

                match ScimJson.decodeUser body with
                | Ok u ->
                    Expect.isTrue
                        u.Active
                        "a create that omitted `active` means provision, and must not read as a deactivation"
                | Error e -> failtestf "decode failed: %s" e.Detail
            }

            test "the primary email is selected, with first as the documented fallback" {
                let flagged =
                    """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"ada","emails":[{"value":"alt@acme.test"},{"value":"primary@acme.test","primary":true}]}"""

                let unflagged =
                    """{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"ada","emails":[{"value":"first@acme.test"},{"value":"second@acme.test"}]}"""

                match ScimJson.decodeUser flagged, ScimJson.decodeUser unflagged with
                | Ok a, Ok b ->
                    Expect.equal (ScimUser.primaryEmail a) (Some "primary@acme.test") "the flagged entry wins"
                    Expect.equal (ScimUser.primaryEmail b) (Some "first@acme.test") "otherwise the first"
                | _ -> failtest "decode failed"
            }

            test "the encoded error carries status as a string" {
                let encoded = ScimJson.encodeError (ScimError.notFound "gone")
                let doc = jsonOf encoded

                Expect.equal
                    (doc.GetProperty("status").ValueKind)
                    JsonValueKind.String
                    "an IdP parsing a numeric status fails — RFC 7644 §3.12 says string"

                Expect.equal (str doc "status") "404" "and it is the right code"
            }

            test "a User encodes with the core schema URN" {
                let u = {
                    Id = "ada@acme.test"
                    ExternalId = Some "x-1"
                    UserName = "ada@acme.test"
                    Name = ScimName.empty
                    DisplayName = Some "Ada"
                    Emails = [
                        {
                            Value = "ada@acme.test"
                            Type = Some "work"
                            Primary = true
                        }
                    ]
                    Active = true
                    Meta = None
                }

                let doc = jsonOf (ScimJson.encodeUser u)

                Expect.equal
                    (doc.GetProperty("schemas").EnumerateArray() |> Seq.head |> _.GetString())
                    ScimSchemas.User
                    "the envelope is decided by the resource type"

                Expect.isTrue (doc.GetProperty("active").GetBoolean()) "active is always emitted"
            }

            test "role mapping resolves by group name, defaulting to least privilege" {
                let mapping =
                    ScimRoleMapping.defaults
                    |> ScimRoleMapping.withGroup "Acme Admins" Admin
                    |> ScimRoleMapping.withGroup "Acme Owners" Owner

                Expect.equal (ScimRoleMapping.resolve "Acme Admins" mapping) Admin "a mapped group"
                Expect.equal (ScimRoleMapping.resolve "acme admins" mapping) Admin "case-insensitively"
                Expect.equal (ScimRoleMapping.resolve "Acme Engineering" mapping) Member "an unmapped group is Member"
                Expect.equal (ScimRoleMapping.resolve "anything" ScimRoleMapping.defaults) Member "the default default"
            }
        ]

        // ─── Opt-in (GP 13) ────────────────────────────────────────

        test "a deployment that does not compose SCIM contributes no handler" {
            let baseApp = ServerApp.empty
            let wrapped = ScimServerApp.ofServerApp baseApp

            Expect.equal wrapped.Mode NoScim "the default is NoScim"

            Expect.equal
                (List.length wrapped.Base.Extensions.Handlers)
                (List.length baseApp.Extensions.Handlers)
                "and the base app's handler list is untouched"

            let mounted = wrapped |> ScimServerApp.withScim (ScimConfig.create teamId)

            match mounted.Mode with
            | EnabledScim c -> Expect.equal c.TeamId teamId "withScim records the bound team"
            | NoScim -> failtest "withScim should have enabled the mode"
        }
    ]